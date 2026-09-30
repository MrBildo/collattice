import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Dialog, DialogContent } from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { isTextInputFocused } from '@/lib/utils';
import { CardDetailForm } from '@/components/CardDetailForm';
import type { CardDetailFormHandle } from '@/components/CardDetailForm';
import { UnsavedChangesDialog } from '@/components/UnsavedChangesDialog';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import type { CardItem, CardSize, Lane } from '@/types';
import type { UnsavedChangesAction } from '@/components/UnsavedChangesDialog';
import type { CardPrefill, DuplicateRequest } from '@/lib/duplicate-card';

// Snapshot navigation context so lane changes don't shift prev/next nav.
// Uses "setState during render" pattern — React allows this when value differs.
function useNavSnapshot(card: CardItem | null, cardsInLane: CardItem[] | undefined) {
  const [snapshot, setSnapshot] = useState<{
    cardId: string | null;
    laneId: string | null;
    cards: CardItem[];
  }>({ cardId: null, laneId: null, cards: [] });

  const cardId = card?.id ?? null;
  const laneId = card?.laneId ?? null;
  const cards = cardsInLane ?? [];

  // Card changed — take fresh snapshot (setState during render is OK for derived state)
  if (cardId !== snapshot.cardId) {
    setSnapshot({ cardId, laneId, cards });
    return cards;
  }

  // Same card, same lane — update snapshot (handles reorders/additions within the lane)
  if (cardId && laneId === snapshot.laneId && cards !== snapshot.cards) {
    setSnapshot({ cardId, laneId, cards });
    return cards;
  }

  // Card moved to different lane — keep frozen snapshot
  return snapshot.cards;
}

type PendingAction =
  | { type: 'close' }
  | { type: 'navigate'; cardNumber: number }
  | { type: 'duplicate'; request: DuplicateRequest };

type CardDetailSheetProps = {
  card: CardItem | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  currentUserId?: string;
  currentUserRole?: number;
  lanes?: Lane[];
  boardId?: string;
  sizes?: CardSize[];
  cardsInLane?: CardItem[];
  onNavigateCard?: (cardNumber: number) => void;
  onDuplicate?: (prefill: CardPrefill) => void;
};

export function CardDetailSheet({
  card,
  open,
  onOpenChange,
  currentUserId,
  currentUserRole,
  lanes,
  boardId,
  sizes,
  cardsInLane,
  onNavigateCard,
  onDuplicate,
}: CardDetailSheetProps) {
  const isDirtyRef = useRef(false);
  const formRef = useRef<CardDetailFormHandle>(null);
  const [pendingAction, setPendingAction] = useState<PendingAction | null>(null);

  const navSnapshot = useNavSnapshot(card, cardsInLane);

  const { prevCard, nextCard } = useMemo(() => {
    if (!card || navSnapshot.length === 0) return { prevCard: null, nextCard: null };
    const idx = navSnapshot.findIndex((c) => c.id === card.id);
    if (idx === -1) return { prevCard: null, nextCard: null };
    return {
      prevCard: idx > 0 ? navSnapshot[idx - 1] : null,
      nextCard: idx < navSnapshot.length - 1 ? navSnapshot[idx + 1] : null,
    };
  }, [card, navSnapshot]);

  const executePendingAction = useCallback(
    (action: PendingAction, isDiscarding = false) => {
      if (action.type === 'close') {
        onOpenChange(false);
      } else if (action.type === 'navigate' && onNavigateCard) {
        onNavigateCard(action.cardNumber);
      } else if (action.type === 'duplicate' && onDuplicate) {
        // Discarding the unsaved edits means the copy starts from the card as
        // stored; saving them (or having none) means it starts from the form.
        onDuplicate(isDiscarding ? action.request.saved : action.request.draft);
      }
    },
    [onOpenChange, onNavigateCard, onDuplicate],
  );

  // Ref to hold the pending action that should be executed after save completes
  const pendingAfterSaveRef = useRef<PendingAction | null>(null);

  const handleSaveComplete = useCallback(() => {
    const action = pendingAfterSaveRef.current;
    pendingAfterSaveRef.current = null;
    if (action) {
      executePendingAction(action);
    }
  }, [executePendingAction]);

  // A failed save cancels the action that was waiting on it. Left queued, it
  // would run after the user's next successful save, which they did not ask
  // for, and a queued duplicate would carry the draft from the failed attempt.
  const handleSaveError = useCallback(() => {
    pendingAfterSaveRef.current = null;
  }, []);

  const handleUnsavedAction = useCallback(
    (action: UnsavedChangesAction) => {
      const pending = pendingAction;
      setPendingAction(null);

      if (action === 'cancel') return;

      if (action === 'save') {
        // Store the pending action so handleSaveComplete can execute it after save
        pendingAfterSaveRef.current = pending;
        formRef.current?.save();
        return;
      }

      // discard
      isDirtyRef.current = false;
      if (pending) executePendingAction(pending, true);
    },
    [pendingAction, executePendingAction],
  );

  const requestAction = useCallback(
    (action: PendingAction) => {
      if (isDirtyRef.current) {
        setPendingAction(action);
        return;
      }
      executePendingAction(action);
    },
    [executePendingAction],
  );

  const handleNavigate = useCallback(
    (direction: 'prev' | 'next') => {
      const target = direction === 'prev' ? prevCard : nextCard;
      if (!target || !onNavigateCard) return;
      requestAction({ type: 'navigate', cardNumber: target.number });
    },
    [prevCard, nextCard, onNavigateCard, requestAction],
  );

  useEffect(() => {
    if (!open) return;
    const handleKeyDown = (e: KeyboardEvent) => {
      if (isTextInputFocused()) return;
      if (e.key === 'ArrowLeft' && prevCard) {
        e.preventDefault();
        handleNavigate('prev');
      } else if (e.key === 'ArrowRight' && nextCard) {
        e.preventDefault();
        handleNavigate('next');
      }
    };
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [open, prevCard, nextCard, handleNavigate]);

  const handleDialogOpenChange = useCallback(
    (nextOpen: boolean) => {
      if (!nextOpen) {
        requestAction({ type: 'close' });
        return;
      }
      onOpenChange(nextOpen);
    },
    [onOpenChange, requestAction],
  );

  if (!card) return null;

  const navPosition =
    navSnapshot.length > 1
      ? `${(navSnapshot.findIndex((c) => c.id === card.id) ?? 0) + 1} / ${navSnapshot.length}`
      : null;

  return (
    <>
      <Dialog open={open} onOpenChange={handleDialogOpenChange}>
        <DialogContent
          data-mobile-fullscreen
          className="flex flex-col p-0 md:max-h-[85vh] md:!w-[80vw] md:!max-w-[80vw]"
          style={{ overflow: 'visible' }}
        >
          {/* Floating nav buttons — desktop only, positioned outside the dialog.
              Two distinct pointer-hit hazards are guarded here:

              1. Press-feedback transform clobber (the operative bug). The base
                 Button carries `active:translate-y-px`, which sets the WHOLE CSS
                 `transform` property on press — clobbering, not composing with, any
                 positioning transform on the same element. When the button itself
                 owned `-translate-y-1/2` for vertical centering, pressing it
                 replaced `translateY(-16px)` with `translateY(1px)` and the button
                 leapt ~17px downward mid-press. The pointer-up then landed on the
                 dialog overlay behind, so the `click` (and the navigation) never
                 fired — the press animation played but the card never changed. Fix:
                 the centering lives on the wrapper; the Button is an in-flow child
                 whose transform is free for `active:translate-y-px` to use without
                 moving the button out from under the pointer.

              2. Rounded-corner hit fall-through. The hit target is a square
                 <Button> (`rounded-none`); the round appearance lives on an inner
                 <span>. A circular border-radius clips pointer hit-testing, so a
                 fully-rounded button drops clicks in its four corners. Keeping the
                 button rectangular makes the whole bounding box clickable. */}
          {prevCard && (
            <div className="absolute top-1/2 -left-14 z-50 hidden -translate-y-1/2 md:block">
              <Button
                variant="outline"
                size="icon"
                onClick={() => handleNavigate('prev')}
                onPointerDown={(e) => e.stopPropagation()}
                className="flex rounded-none border-transparent bg-transparent p-0 shadow-none hover:bg-transparent"
                aria-label="Previous card"
              >
                <span className="flex size-8 items-center justify-center rounded-full border border-border bg-background shadow-lg transition-colors group-hover/button:bg-muted">
                  <ChevronLeft className="h-5 w-5" />
                </span>
              </Button>
            </div>
          )}
          {nextCard && (
            <div className="absolute top-1/2 -right-14 z-50 hidden -translate-y-1/2 md:block">
              <Button
                variant="outline"
                size="icon"
                onClick={() => handleNavigate('next')}
                onPointerDown={(e) => e.stopPropagation()}
                className="flex rounded-none border-transparent bg-transparent p-0 shadow-none hover:bg-transparent"
                aria-label="Next card"
              >
                <span className="flex size-8 items-center justify-center rounded-full border border-border bg-background shadow-lg transition-colors group-hover/button:bg-muted">
                  <ChevronRight className="h-5 w-5" />
                </span>
              </Button>
            </div>
          )}
          <div className="flex min-h-0 flex-1 flex-col overflow-hidden">
            <CardDetailForm
              ref={formRef}
              key={card.id}
              card={card}
              onClose={() => handleDialogOpenChange(false)}
              onSaveComplete={handleSaveComplete}
              onSaveError={handleSaveError}
              currentUserId={currentUserId}
              currentUserRole={currentUserRole}
              lanes={lanes}
              boardId={boardId}
              sizes={sizes}
              isDirtyRef={isDirtyRef}
              navPosition={navPosition}
              onNavigatePrev={prevCard ? () => handleNavigate('prev') : undefined}
              onNavigateNext={nextCard ? () => handleNavigate('next') : undefined}
              onDuplicate={
                onDuplicate ? (request) => requestAction({ type: 'duplicate', request }) : undefined
              }
            />
          </div>
        </DialogContent>
      </Dialog>
      <UnsavedChangesDialog
        open={pendingAction !== null}
        onAction={handleUnsavedAction}
        actionType={pendingAction?.type}
      />
    </>
  );
}
