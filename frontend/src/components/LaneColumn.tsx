import { useSortable } from '@dnd-kit/sortable';
import { SortableContext, verticalListSortingStrategy } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { ChevronDown, Plus } from 'lucide-react';
import { useId, useMemo } from 'react';
import { Button } from '@/components/ui/button';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { SortableCard } from '@/components/SortableCard';
import { cn } from '@/lib/utils';
import type { CardItem, CardSummary, Lane } from '@/types';

type LaneColumnProps = {
  lane: Lane;
  cards: CardItem[];
  onCardClick: (card: CardItem) => void;
  onAddCard: () => void;
  activeCardId: string | null;
  isLaneDragging: boolean;
  sizeMap: Map<string, string>;
  enrichedCardMap: Map<string, CardSummary>;
  isCollapsed: boolean;
  onToggleCollapse: () => void;
};

export function LaneColumn({
  lane,
  cards,
  onCardClick,
  onAddCard,
  activeCardId,
  isLaneDragging,
  sizeMap,
  enrichedCardMap,
  isCollapsed,
  onToggleCollapse,
}: LaneColumnProps) {
  // The lane is both a card-drop target (over.id === lane.id, used by
  // use-board-dnd) and a sortable lane item (use-lane-dnd). useSortable supplies
  // both the droppable node ref the card hook needs AND the draggable behavior
  // for lane reordering — data.type lets App.tsx route the shared DndContext
  // handlers to the right hook. The drag `listeners` are attached to the HEADER
  // only (the grab target), keeping the gesture distinct from the resize handles
  // that live in the column gaps (App.tsx) and from card drags inside the lane.
  //
  // dnd-kit's `attributes` are deliberately not spread on the header. They would
  // make it one focusable button wrapping the add-card button, and describe a
  // press-space-to-drag gesture the board does not support (it registers no
  // keyboard sensor). Lane drag stays a pointer gesture, as card drag is.
  const { setNodeRef, listeners, transform, transition, isOver } = useSortable({
    id: lane.id,
    data: { type: 'lane' },
  });
  const cardIds = useMemo(() => cards.map((c) => c.id), [cards]);
  const addCardButtonLabel = `Add card to ${lane.name}`;
  const cardListId = useId();

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
  };

  return (
    <article
      ref={setNodeRef}
      style={style}
      data-lane=""
      className={cn(
        'flex min-w-0 flex-col rounded-lg max-md:border-0 md:border md:border-lane-border bg-lane-bg md:border-t-2 md:border-t-primary md:overflow-hidden transition-[box-shadow,background-color] duration-150',
        isOver && 'ring-2 ring-primary/60 bg-primary/5',
        isLaneDragging && 'opacity-40',
      )}
    >
      <div
        {...listeners}
        data-lane-header=""
        className="flex min-w-0 shrink-0 items-center justify-between overflow-hidden px-4 py-3 max-md:sticky max-md:top-0 max-md:z-10 max-md:bg-lane-bg max-md:border-t-2 max-md:border-t-primary max-md:rounded-t-lg max-md:cursor-pointer max-md:select-none md:cursor-grab md:active:cursor-grabbing"
        onClick={(e) => {
          if (window.innerWidth < 768) {
            e.preventDefault();
            onToggleCollapse();
          }
        }}
      >
        <div className="flex min-w-0 items-center gap-2">
          {/* The phone's collapse toggle, for keyboard and screen reader users;
              a tap anywhere on the header also toggles. Hidden on desktop,
              where lanes do not collapse. Has no click handler of its own: its
              click, from a tap or from Enter or Space, bubbles to the header's. */}
          <Button
            variant="ghost"
            size="icon-xs"
            aria-expanded={!isCollapsed}
            aria-controls={cardListId}
            aria-label={`Cards in ${lane.name}`}
            // The negative margin keeps the old 16px icon's footprint, so the
            // lane name does not move; the button's hit area stays 24px. The
            // ghost variant shades an aria-expanded button as an open menu; a
            // lane is expanded most of the time, so that shading is cancelled.
            className="-mx-1 shrink-0 text-muted-foreground aria-expanded:bg-transparent aria-expanded:text-muted-foreground md:hidden"
          >
            <ChevronDown
              className={cn(
                'h-4 w-4 transition-transform duration-200',
                isCollapsed && '-rotate-90',
              )}
            />
          </Button>
          <Tooltip>
            <TooltipTrigger
              render={<h2 className="truncate text-sm font-semibold uppercase tracking-wide" />}
            >
              {lane.name}
            </TooltipTrigger>
            <TooltipContent>{lane.name}</TooltipContent>
          </Tooltip>
          <span className="shrink-0 text-xs text-muted-foreground">{cards.length}</span>
        </div>
        <Tooltip>
          <TooltipTrigger
            render={
              <Button
                variant="ghost"
                size="icon-xs"
                aria-label={addCardButtonLabel}
                // Stop the press from reaching the header's drag listeners so a
                // press on the add button never starts a lane drag. The board's
                // sensors listen for mousedown and touchstart, so those are the
                // events that must stop here; pointerdown alone is not enough.
                onPointerDown={(e) => e.stopPropagation()}
                onMouseDown={(e) => e.stopPropagation()}
                onTouchStart={(e) => e.stopPropagation()}
                onClick={(e) => {
                  e.stopPropagation();
                  onAddCard();
                }}
                className="text-muted-foreground"
              />
            }
          >
            <Plus className="h-3.5 w-3.5" />
          </TooltipTrigger>
          <TooltipContent>{addCardButtonLabel}</TooltipContent>
        </Tooltip>
      </div>
      <SortableContext items={cardIds} strategy={verticalListSortingStrategy}>
        <div
          id={cardListId}
          className={cn(
            'space-y-2 px-3 pb-3 md:flex-1 md:overflow-y-auto',
            'max-md:grid max-md:transition-[grid-template-rows,visibility] max-md:duration-200 max-md:ease-in-out',
            // A collapsed lane's cards are clipped to nothing on a phone. Hiding
            // them also takes them out of the tab order and the accessibility
            // tree, so a collapsed lane is collapsed for keyboard and screen
            // reader users too. Visibility switches at the end of the collapse
            // transition and at the start of the expand, so the animation is kept.
            isCollapsed ? 'max-md:invisible max-md:grid-rows-[0fr]' : 'max-md:grid-rows-[1fr]',
          )}
        >
          <div className="max-md:overflow-hidden">
            {cards.map((card) => (
              <div key={card.id} className="mb-2 last:mb-0">
                <SortableCard
                  card={card}
                  boardId={lane.boardId}
                  onCardClick={onCardClick}
                  isDragging={card.id === activeCardId}
                  sizeMap={sizeMap}
                  enrichedData={enrichedCardMap.get(card.id)}
                />
              </div>
            ))}
          </div>
        </div>
      </SortableContext>
    </article>
  );
}
