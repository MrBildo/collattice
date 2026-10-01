import {
  forwardRef,
  useCallback,
  useEffect,
  useId,
  useImperativeHandle,
  useMemo,
  useRef,
  useState,
} from 'react';
import { usePanelResize } from '@/hooks/use-panel-resize';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { MarkdownRenderer } from '@/components/MarkdownRenderer';
import { DialogHeader, DialogDescription } from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { Label } from '@/components/ui/label';
import { Separator } from '@/components/ui/separator';
import { Badge } from '@/components/ui/badge';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { CardComments } from '@/components/CardComments';
import { CardAttachments } from '@/components/CardAttachments';
import { CardDescriptionHistory } from '@/components/CardDescriptionHistory';
import {
  deleteCard,
  fetchCardHistory,
  fetchCardLabels,
  fetchLabels,
  updateCard,
  uploadAttachment,
} from '@/lib/api';
import { InlineError } from '@/components/ui/inline-error';
import { toMessage } from '@/lib/mutation-floor';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { LabelPicker } from '@/components/LabelPicker';
import { queryKeys } from '@/lib/query-keys';
import { QUERY_DEFAULTS } from '@/lib/query-config';
import { useUserDirectory } from '@/hooks/use-user-directory';
import { useCardLinkContext } from '@/hooks/use-card-links';
import { useArchiveCard } from '@/hooks/use-archive-card';
import { useRestoreCard } from '@/hooks/use-restore-card';
import { usePasteAttachment } from '@/hooks/use-paste-attachment';
import { cn, arraysEqual, formatDateTime } from '@/lib/utils';
import {
  Archive,
  ArchiveRestore,
  Check,
  ChevronLeft,
  ChevronRight,
  Copy,
  History,
  RefreshCw,
  RotateCcw,
  Tag,
} from 'lucide-react';
import { ROLES } from '@/lib/roles';
import { buildCardPrefill } from '@/lib/duplicate-card';
import type { DuplicateRequest } from '@/lib/duplicate-card';
import type { BoardData, CardItem, CardSize, Lane, UpdateCardPatch } from '@/types';

type FieldName = 'name' | 'description' | 'sizeId' | 'laneId' | 'labelIds';

type DescriptionView = 'edit' | 'preview' | 'history';

// A remote change to a field the user is editing, tagged with who made it so
// the warning can name the actor ("Marcus changed the description") rather than
// count anonymous fields. The actor is the card's lastUpdatedByUserId at the
// moment the change was observed — resolved to a name at render time.
type ExternalUpdate = { remoteValue: string; actorId: string };
type ExternalLabelUpdate = { remoteLabelIds: string[]; actorId: string };

type ExternalUpdates = {
  name?: ExternalUpdate;
  description?: ExternalUpdate;
  sizeId?: ExternalUpdate;
  laneId?: ExternalUpdate;
  labelIds?: ExternalLabelUpdate;
};

// Field names in the operator's vocabulary, not the wire's (sizeId -> "size").
const FIELD_LABELS: Record<FieldName, string> = {
  name: 'name',
  description: 'description',
  sizeId: 'size',
  laneId: 'lane',
  labelIds: 'labels',
};

// Summarise who changed what while the card was open. The dominant collision is
// one other editor touching one field, so that case names the person and the
// field in full; multi-field and multi-actor cases fall back to a count to keep
// the line short.
function buildCollisionMessage(entries: { field: string; actor: string }[]): string {
  if (entries.length === 0) return '';
  if (entries.length === 1) {
    return `${entries[0].actor} changed the ${entries[0].field}`;
  }
  const actors = new Set(entries.map((e) => e.actor));
  if (actors.size === 1) {
    return `${entries[0].actor} changed ${entries.length} fields`;
  }
  return `${entries.length} fields changed externally`;
}

// Preserve the actor recorded when a field's remote value was first seen: only
// re-attribute a field when its remote value actually changed. Without this, a
// later edit to a *different* field by a *different* person would silently
// reassign authorship of an already-flagged field, since the sync effect
// rebuilds the whole map on every incoming card and stamps the latest editor.
function reconcileActors(next: ExternalUpdates, prev: ExternalUpdates): ExternalUpdates {
  const out: ExternalUpdates = {};
  if (next.name) {
    out.name = prev.name?.remoteValue === next.name.remoteValue ? prev.name : next.name;
  }
  if (next.description) {
    out.description =
      prev.description?.remoteValue === next.description.remoteValue
        ? prev.description
        : next.description;
  }
  if (next.sizeId) {
    out.sizeId = prev.sizeId?.remoteValue === next.sizeId.remoteValue ? prev.sizeId : next.sizeId;
  }
  if (next.laneId) {
    out.laneId = prev.laneId?.remoteValue === next.laneId.remoteValue ? prev.laneId : next.laneId;
  }
  if (next.labelIds) {
    out.labelIds =
      prev.labelIds && arraysEqual(prev.labelIds.remoteLabelIds, next.labelIds.remoteLabelIds)
        ? prev.labelIds
        : next.labelIds;
  }
  return out;
}

type CardBaseline = {
  name: string;
  description: string;
  sizeId: string;
  laneId: string;
  labelIds: string[];
};

// Per-field collision indicator. The remote value and the "accept" action are
// load-bearing UI, so they live in a click/keyboard-openable Popover with an
// accessible name — not a hover-only tooltip, which keyboard and screen-reader
// users cannot reach and which is the wrong home for an action.
function ExternalUpdateIndicator({
  field,
  actorName,
  remoteDisplay,
  onAccept,
}: {
  field: string;
  actorName: string;
  remoteDisplay: string;
  onAccept: () => void;
}) {
  const [open, setOpen] = useState(false);
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger
        render={
          <Button
            type="button"
            variant="ghost"
            size="icon-xs"
            aria-label={`${field} changed by ${actorName}. Review and accept their version.`}
            className="size-5 rounded-full hover:bg-accent/15"
          />
        }
      >
        <span className="inline-block size-2.5 rounded-full bg-accent ring-2 ring-accent/40" />
      </PopoverTrigger>
      <PopoverContent side="top" align="start" className="w-72">
        <p className="text-sm text-foreground">
          <span className="font-medium">{actorName}</span> changed the {field.toLowerCase()} to:
        </p>
        <p className="max-h-32 overflow-y-auto rounded bg-muted/50 p-2 text-sm break-words text-foreground">
          {remoteDisplay}
        </p>
        <Button
          type="button"
          variant="outline"
          size="sm"
          className="self-start"
          onClick={() => {
            onAccept();
            setOpen(false);
          }}
        >
          <RotateCcw className="mr-1 h-3.5 w-3.5" />
          Accept their version
        </Button>
      </PopoverContent>
    </Popover>
  );
}

export type CardDetailFormHandle = {
  save: () => void;
};

type CardDetailFormProps = {
  card: CardItem;
  onClose: () => void;
  onSaveComplete?: () => void;
  onSaveError?: () => void;
  currentUserId?: string;
  currentUserRole?: number;
  lanes?: Lane[];
  boardId?: string;
  sizes?: CardSize[];
  isDirtyRef: React.MutableRefObject<boolean>;
  navPosition?: string | null;
  onNavigatePrev?: () => void;
  onNavigateNext?: () => void;
  onDuplicate?: (request: DuplicateRequest) => void;
};

export const CardDetailForm = forwardRef<CardDetailFormHandle, CardDetailFormProps>(
  function CardDetailForm(
    {
      card,
      onClose,
      onSaveComplete,
      onSaveError,
      currentUserId,
      currentUserRole,
      lanes,
      boardId,
      sizes,
      isDirtyRef,
      navPosition,
      onNavigatePrev,
      onNavigateNext,
      onDuplicate,
    },
    ref,
  ) {
    const queryClient = useQueryClient();
    const dialogRef = useRef<HTMLDivElement>(null);
    const bodyRef = useRef<HTMLDivElement>(null);
    const { width: commentsWidth, isDragging, onMouseDown } = usePanelResize(bodyRef);

    const isArchived = card.isArchived;

    const [name, setName] = useState(card.name);
    const [currentLaneId, setCurrentLaneId] = useState(card.laneId);
    const [description, setDescription] = useState(card.descriptionMarkdown ?? '');
    const [sizeId, setSizeId] = useState(card.sizeId);
    const [selectedLabelIds, setSelectedLabelIds] = useState<string[] | null>(null);
    const [descriptionView, setDescriptionView] = useState<DescriptionView>('preview');
    const [isConfirmingDelete, setIsConfirmingDelete] = useState(false);
    const [showArchiveActions, setShowArchiveActions] = useState(false);
    const [restoreLaneId, setRestoreLaneId] = useState<string | null>(null);
    const [showRestorePicker, setShowRestorePicker] = useState(false);
    const [pasteStatus, setPasteStatus] = useState<string | null>(null);
    const [saveStatus, setSaveStatus] = useState<string | null>(null);
    // Inline error for the card-update path. The operator is looking at this
    // form, so a save failure belongs here, not in a toast.
    const [saveError, setSaveError] = useState<string | null>(null);

    // Touch tracking: fields the user has edited since mount/last save
    const touchedFields = useRef(new Set<FieldName>());

    // Baseline: the card prop values we compare dirty state against (frozen for touched fields)
    const [baselineState, setBaselineState] = useState<CardBaseline>({
      name: card.name,
      description: card.descriptionMarkdown ?? '',
      sizeId: card.sizeId,
      laneId: card.laneId,
      labelIds: [],
    });

    // External updates: remote changes to fields the user has touched
    const [externalUpdates, setExternalUpdates] = useState<ExternalUpdates>({});

    const pasteMutation = useMutation({
      mutationFn: (file: File) => uploadAttachment(card.id, file),
      onSuccess: (data) => {
        queryClient.invalidateQueries({ queryKey: queryKeys.cards.attachments(card.id) });
        setPasteStatus(`Attached "${data.fileName}"`);
        setTimeout(() => setPasteStatus(null), 3000);
      },
      onError: () => {
        setPasteStatus('Paste upload failed');
        setTimeout(() => setPasteStatus(null), 3000);
      },
    });

    const handlePasteFile = useCallback(
      (file: File) => {
        pasteMutation.mutate(file);
      },
      [pasteMutation],
    );

    usePasteAttachment({
      onFile: handlePasteFile,
      enabled: !isArchived,
      containerRef: dialogRef,
    });

    useEffect(() => {
      const el = dialogRef.current;
      if (!el) return;
      if (
        !el.contains(document.activeElement) ||
        (document.activeElement?.tagName !== 'INPUT' &&
          document.activeElement?.tagName !== 'TEXTAREA')
      ) {
        el.focus({ preventScroll: true });
      }
    }, []);

    const canDelete =
      currentUserRole === ROLES.Administrator ||
      (currentUserRole === ROLES.Human && card.createdByUserId === currentUserId);

    const { getUserName } = useUserDirectory();
    const { boardSlug, cardNumbers, cardPreviews } = useCardLinkContext(boardId);

    const labelsQuery = useQuery({
      queryKey: queryKeys.cards.labels(card.id),
      queryFn: () => fetchCardLabels(card.id),
      ...QUERY_DEFAULTS.labels,
    });
    const labelsErrorId = useId();

    const allLabelsQuery = useQuery({
      queryKey: queryKeys.labels.all(boardId as string),
      queryFn: () => fetchLabels(boardId as string),
      enabled: !!boardId,
      ...QUERY_DEFAULTS.labels,
    });

    // Gates the History control on whether any description revisions exist.
    // This SPA renders the card detail from the board composite cache and never
    // calls GET /cards/{id}, so the detail's descriptionHistoryCount is not in
    // reach without fetching a payload that duplicates the comments, labels and
    // attachments queries. A limit-1 probe of the history trail reads the same
    // number — totalCount reports the whole trail regardless of paging, through
    // the same backend query — for a response a few hundred bytes long.
    const historyMetaQuery = useQuery({
      queryKey: queryKeys.cards.historyMeta(card.id),
      queryFn: () => fetchCardHistory(card.id, { limit: 1, format: 'diff' }),
      ...QUERY_DEFAULTS.history,
    });
    const historyCount = historyMetaQuery.data?.totalCount ?? 0;
    // A failed probe cannot rule history out. Showing the control and letting
    // the panel present the real load error beats silently hiding a trail —
    // "no history" and "couldn't check" must never look the same.
    const isHistoryAvailable = historyCount > 0 || historyMetaQuery.isError;

    const originalLabelIds = useMemo(
      () => (labelsQuery.data ?? []).map((l) => l.id),
      [labelsQuery.data],
    );

    const effectiveLabelIds = selectedLabelIds ?? originalLabelIds;

    const assignedLabels = useMemo(
      () => (allLabelsQuery.data ?? []).filter((l) => effectiveLabelIds.includes(l.id)),
      [allLabelsQuery.data, effectiveLabelIds],
    );

    // Field sync: when card prop changes (SSE refetch), sync untouched fields,
    // track external updates for touched fields.
    // Uses functional setBaselineState to read the current baseline without a stale closure.
    useEffect(() => {
      const touched = touchedFields.current;
      const remoteName = card.name;
      const remoteDesc = card.descriptionMarkdown ?? '';
      const remoteSizeId = card.sizeId;
      const remoteLaneId = card.laneId;
      const remoteLabelIds = originalLabelIds;
      // Whoever most recently wrote the card is the actor behind any field that
      // now differs from the baseline; reconcileActors keeps an earlier actor
      // when only an unrelated field changed.
      const actorId = card.lastUpdatedByUserId;

      setBaselineState((base) => {
        const newExternal: ExternalUpdates = {};
        const patches: Partial<CardBaseline> = {};

        // Name
        if (!touched.has('name')) {
          if (remoteName !== base.name) {
            setName(remoteName);
            patches.name = remoteName;
          }
        } else if (remoteName !== base.name) {
          newExternal.name = { remoteValue: remoteName, actorId };
        }

        // Description
        if (!touched.has('description')) {
          if (remoteDesc !== base.description) {
            setDescription(remoteDesc);
            patches.description = remoteDesc;
          }
        } else if (remoteDesc !== base.description) {
          newExternal.description = { remoteValue: remoteDesc, actorId };
        }

        // SizeId
        if (!touched.has('sizeId')) {
          if (remoteSizeId !== base.sizeId) {
            setSizeId(remoteSizeId);
            patches.sizeId = remoteSizeId;
          }
        } else if (remoteSizeId !== base.sizeId) {
          newExternal.sizeId = { remoteValue: remoteSizeId, actorId };
        }

        // LaneId
        if (!touched.has('laneId')) {
          if (remoteLaneId !== base.laneId) {
            setCurrentLaneId(remoteLaneId);
            patches.laneId = remoteLaneId;
          }
        } else if (remoteLaneId !== base.laneId) {
          newExternal.laneId = { remoteValue: remoteLaneId, actorId };
        }

        // LabelIds
        if (!touched.has('labelIds')) {
          if (!arraysEqual(remoteLabelIds, base.labelIds)) {
            setSelectedLabelIds(null);
            patches.labelIds = remoteLabelIds;
          }
        } else if (!arraysEqual(remoteLabelIds, base.labelIds)) {
          newExternal.labelIds = { remoteLabelIds, actorId };
        }

        // Update external updates state, keeping each field's original actor
        // when only its actor (not its value) would have changed.
        setExternalUpdates((prev) => {
          const reconciled = reconcileActors(newExternal, prev);
          if (JSON.stringify(prev) === JSON.stringify(reconciled)) return prev;
          return reconciled;
        });

        // Return updated baseline (or same ref if no changes)
        if (Object.keys(patches).length === 0) return base;
        return { ...base, ...patches };
      });
    }, [
      card.name,
      card.descriptionMarkdown,
      card.sizeId,
      card.laneId,
      card.lastUpdatedByUserId,
      originalLabelIds,
    ]);

    // Accept a remote value for a field: replace local state, update baseline, clear touch
    const acceptRemote = useCallback(
      (field: FieldName) => {
        if (field === 'name' && externalUpdates.name) {
          const val = externalUpdates.name.remoteValue;
          setName(val);
          setBaselineState((prev) => ({ ...prev, name: val }));
        } else if (field === 'description' && externalUpdates.description) {
          const val = externalUpdates.description.remoteValue;
          setDescription(val);
          setBaselineState((prev) => ({ ...prev, description: val }));
        } else if (field === 'sizeId' && externalUpdates.sizeId) {
          const val = externalUpdates.sizeId.remoteValue;
          setSizeId(val);
          setBaselineState((prev) => ({ ...prev, sizeId: val }));
        } else if (field === 'laneId' && externalUpdates.laneId) {
          const val = externalUpdates.laneId.remoteValue;
          setCurrentLaneId(val);
          setBaselineState((prev) => ({ ...prev, laneId: val }));
        } else if (field === 'labelIds' && externalUpdates.labelIds) {
          const val = externalUpdates.labelIds.remoteLabelIds;
          setSelectedLabelIds(null);
          setBaselineState((prev) => ({ ...prev, labelIds: val }));
        }

        touchedFields.current.delete(field);
        setExternalUpdates((prev) => {
          const next = { ...prev };
          delete next[field];
          return next;
        });
      },
      [externalUpdates],
    );

    const externalUpdateCount = useMemo(
      () => Object.keys(externalUpdates).length,
      [externalUpdates],
    );

    const collisionMessage = useMemo(() => {
      const entries = (Object.keys(externalUpdates) as FieldName[]).map((f) => ({
        field: FIELD_LABELS[f],
        actor: getUserName(externalUpdates[f]!.actorId),
      }));
      return buildCollisionMessage(entries);
    }, [externalUpdates, getUserName]);

    const acceptAllRemote = useCallback(() => {
      const fields = Object.keys(externalUpdates) as FieldName[];
      for (const field of fields) {
        acceptRemote(field);
      }
    }, [externalUpdates, acceptRemote]);

    // Dirty calculation: compare local state against baseline (not the live card prop)
    const isDirty =
      !isArchived &&
      (name !== baselineState.name ||
        description !== baselineState.description ||
        sizeId !== baselineState.sizeId ||
        currentLaneId !== baselineState.laneId ||
        !arraysEqual(effectiveLabelIds, baselineState.labelIds));

    useEffect(() => {
      isDirtyRef.current = isDirty;
    }, [isDirty, isDirtyRef]);

    const updateMutation = useMutation({
      // Inline tier: the operator is looking at this form, so the failure belongs
      // in the form, not a toast. Opt out of the floor's toast and render
      // <InlineError> at the form footer. This is the app's biggest current
      // silent-loss gap.
      meta: { skipToast: true },
      mutationFn: (patch: UpdateCardPatch) => updateCard(card.id, patch),
      onSuccess: (updatedCard, patch) => {
        if (boardId) {
          // PATCH /cards/{id} now returns the enriched CardSummary, so the
          // mutation response carries everything the board cache needs — labels,
          // sizeName, commentCount, attachmentCount, isArchived. No re-fetch needed.
          queryClient.setQueryData<BoardData>(queryKeys.boards.data(boardId), (old) =>
            old
              ? {
                  ...old,
                  cards: old.cards.map((c) => (c.id === card.id ? { ...c, ...updatedCard } : c)),
                }
              : old,
          );
        }
        queryClient.invalidateQueries({ queryKey: queryKeys.cards.labels(card.id) });
        if (patch.descriptionMarkdown !== undefined) {
          // A description save just recorded new revisions (0 → 2 on the first
          // edit); refresh the History gate and any open trail together.
          queryClient.invalidateQueries({ queryKey: queryKeys.cards.history(card.id) });
        }

        // Reset touch tracking and baseline after successful save
        touchedFields.current.clear();
        setBaselineState({
          name,
          description,
          sizeId,
          laneId: currentLaneId,
          labelIds: effectiveLabelIds,
        });
        setExternalUpdates({});
        isDirtyRef.current = false;
        setSaveError(null);

        // Show transient "Saved" indicator
        setSaveStatus('Saved');
        setTimeout(() => setSaveStatus(null), 2500);

        // Signal completion so the sheet can execute any pending action
        onSaveComplete?.();
      },
      onError: (error: unknown) => {
        // Inline surface: render the message in the form (skipToast above).
        setSaveError(toMessage(error));
        // Signal the failure so the sheet can drop any action that was waiting
        // on this save, rather than running it after some later save.
        onSaveError?.();
      },
    });

    const deleteMutation = useMutation({
      // Board action (no form to attach to) — the floor toasts it.
      meta: { errorMessage: "Couldn't delete card" },
      mutationFn: () => deleteCard(card.id),
      onSuccess: () => {
        if (boardId) {
          queryClient.setQueryData<BoardData>(queryKeys.boards.data(boardId), (old) =>
            old ? { ...old, cards: old.cards.filter((c) => c.id !== card.id) } : old,
          );
          queryClient.invalidateQueries({ queryKey: queryKeys.boards.cards(boardId) });
        }
        isDirtyRef.current = false;
        onClose();
      },
      // No onError: the floor handles the toast.
    });

    const archiveMutation = useArchiveCard({
      cardId: card.id,
      boardId,
      onSuccess: () => {
        isDirtyRef.current = false;
        onClose();
      },
    });

    const restoreMutation = useRestoreCard({
      cardId: card.id,
      boardId,
      onSuccess: () => {
        isDirtyRef.current = false;
        onClose();
      },
    });

    const handleSave = useCallback(() => {
      if (!isDirty) {
        return;
      }

      const patch: UpdateCardPatch = {};

      if (name !== baselineState.name) patch.name = name;
      if (description !== baselineState.description) patch.descriptionMarkdown = description;
      if (sizeId !== baselineState.sizeId) patch.sizeId = sizeId;
      if (currentLaneId !== baselineState.laneId) patch.laneId = currentLaneId;
      if (!arraysEqual(effectiveLabelIds, baselineState.labelIds))
        patch.labelIds = effectiveLabelIds;

      setSaveError(null);
      updateMutation.mutate(patch);
    }, [
      isDirty,
      name,
      description,
      sizeId,
      currentLaneId,
      effectiveLabelIds,
      baselineState,
      updateMutation,
    ]);

    useImperativeHandle(ref, () => ({ save: handleSave }), [handleSave]);

    const handleDelete = () => {
      if (!isConfirmingDelete) {
        setIsConfirmingDelete(true);
        return;
      }
      deleteMutation.mutate();
    };

    const handleArchiveClick = () => {
      if (!showArchiveActions) {
        setShowArchiveActions(true);
        return;
      }
    };

    const handleArchiveConfirm = () => {
      archiveMutation.mutate();
    };

    const handleRestore = () => {
      if (!showRestorePicker) {
        setShowRestorePicker(true);
        if (lanes && lanes.length > 0) {
          setRestoreLaneId(lanes[0].id);
        }
        return;
      }
      if (restoreLaneId) {
        restoreMutation.mutate(restoreLaneId);
      }
    };

    const handleClose = () => {
      onClose();
    };

    // Label edits and the copy's labels both start from this card's label query.
    // Until it has loaded (or if it failed), an edit would start from no labels and
    // a save would drop the ones the card has; a duplicate would silently lose
    // them too. Both wait for it.
    const hasCardLabels = labelsQuery.data !== undefined;
    const canDuplicate = hasCardLabels;

    const handleDuplicate = () => {
      if (!onDuplicate || !canDuplicate) {
        return;
      }

      const visibleLanes = lanes ?? [];
      onDuplicate({
        draft: buildCardPrefill(
          {
            name,
            descriptionMarkdown: description,
            sizeId,
            labelIds: effectiveLabelIds,
            laneId: currentLaneId,
          },
          card,
          visibleLanes,
        ),
        saved: buildCardPrefill(
          {
            name: card.name,
            descriptionMarkdown: card.descriptionMarkdown ?? '',
            sizeId: card.sizeId,
            labelIds: originalLabelIds,
            laneId: card.laneId,
          },
          card,
          visibleLanes,
        ),
      });
    };

    const duplicateButton = onDuplicate && (
      <Button
        variant="outline"
        size="sm"
        onClick={handleDuplicate}
        disabled={!canDuplicate}
        aria-describedby={labelsQuery.isLoadingError ? labelsErrorId : undefined}
      >
        <Copy className="mr-1 h-4 w-4" />
        Duplicate
      </Button>
    );

    return (
      <div
        ref={dialogRef}
        tabIndex={-1}
        className="flex min-h-0 flex-1 flex-col overflow-hidden outline-none"
      >
        {/* Save / Paste feedback */}
        {saveStatus && (
          <div className="flex items-center gap-1.5 border-b bg-primary/10 px-6 py-2 text-sm text-primary">
            <Check className="h-4 w-4" />
            {saveStatus}
          </div>
        )}
        {pasteStatus && (
          <div className="bg-primary/10 text-primary border-b px-6 py-2 text-sm">{pasteStatus}</div>
        )}

        {/* Archived banner */}
        {isArchived && (
          <div className="flex items-center gap-2 border-b bg-accent/10 px-6 py-2">
            <Archive className="h-4 w-4 text-foreground" />
            <span className="text-sm font-medium text-foreground">This card is archived</span>
          </div>
        )}

        {/* Header */}
        <DialogHeader className="border-b px-6 pt-6 pb-4">
          <div className="flex items-center gap-2">
            <DialogDescription className="text-xs">#{card.number}</DialogDescription>
            {isArchived && <Badge className="bg-accent/20 text-foreground">Archived</Badge>}
            {(onNavigatePrev || onNavigateNext) && (
              <div className="flex items-center gap-1 md:hidden">
                <Button
                  type="button"
                  variant="ghost"
                  size="icon-sm"
                  onClick={onNavigatePrev}
                  disabled={!onNavigatePrev}
                  aria-label="Previous card"
                >
                  <ChevronLeft className="h-4 w-4" />
                </Button>
                {navPosition && (
                  <span className="text-xs text-muted-foreground tabular-nums">{navPosition}</span>
                )}
                <Button
                  type="button"
                  variant="ghost"
                  size="icon-sm"
                  onClick={onNavigateNext}
                  disabled={!onNavigateNext}
                  aria-label="Next card"
                >
                  <ChevronRight className="h-4 w-4" />
                </Button>
              </div>
            )}
          </div>
          <div className="flex items-start gap-3">
            <div className="flex min-w-0 flex-1 items-center gap-1.5">
              <Input
                value={name}
                onChange={(e) => {
                  touchedFields.current.add('name');
                  setName(e.target.value);
                }}
                maxLength={120}
                disabled={isArchived}
                className={cn(
                  'border-none bg-transparent px-0 text-xl font-semibold shadow-none focus-visible:ring-0',
                  isArchived && 'cursor-default opacity-70',
                )}
              />
              {externalUpdates.name && (
                <ExternalUpdateIndicator
                  field="Name"
                  actorName={getUserName(externalUpdates.name.actorId)}
                  remoteDisplay={externalUpdates.name.remoteValue}
                  onAccept={() => acceptRemote('name')}
                />
              )}
            </div>
          </div>
          <div className="flex flex-wrap items-center gap-3 pt-2">
            {sizes && sizes.length > 0 && (
              <div className="flex items-center gap-1">
                <Tooltip>
                  <TooltipTrigger render={<span />}>
                    <Select
                      value={sizeId}
                      onValueChange={(v) => {
                        if (v) {
                          touchedFields.current.add('sizeId');
                          setSizeId(v);
                        }
                      }}
                      disabled={isArchived}
                    >
                      <SelectTrigger className={cn('w-36', isArchived && 'opacity-70')}>
                        <SelectValue>{sizes.find((s) => s.id === sizeId)?.name ?? '?'}</SelectValue>
                      </SelectTrigger>
                      <SelectContent>
                        {sizes.map((s) => (
                          <SelectItem key={s.id} value={s.id}>
                            {s.name}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </TooltipTrigger>
                  <TooltipContent>{sizes.find((s) => s.id === sizeId)?.name ?? '?'}</TooltipContent>
                </Tooltip>
                {externalUpdates.sizeId && (
                  <ExternalUpdateIndicator
                    field="Size"
                    actorName={getUserName(externalUpdates.sizeId.actorId)}
                    remoteDisplay={
                      sizes.find((s) => s.id === externalUpdates.sizeId?.remoteValue)?.name ?? '?'
                    }
                    onAccept={() => acceptRemote('sizeId')}
                  />
                )}
              </div>
            )}
            {lanes && lanes.length > 0 && !isArchived && (
              <div className="flex items-center gap-1">
                <Tooltip>
                  <TooltipTrigger render={<span />}>
                    <Select
                      value={currentLaneId}
                      onValueChange={(v) => {
                        if (v && v !== currentLaneId) {
                          touchedFields.current.add('laneId');
                          setCurrentLaneId(v);
                        }
                      }}
                    >
                      <SelectTrigger className="w-36">
                        <SelectValue>
                          {lanes.find((l) => l.id === currentLaneId)?.name ?? '?'}
                        </SelectValue>
                      </SelectTrigger>
                      <SelectContent>
                        {lanes.map((lane) => (
                          <SelectItem key={lane.id} value={lane.id}>
                            {lane.name}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </TooltipTrigger>
                  <TooltipContent>
                    {lanes.find((l) => l.id === currentLaneId)?.name ?? '?'}
                  </TooltipContent>
                </Tooltip>
                {externalUpdates.laneId && (
                  <ExternalUpdateIndicator
                    field="Lane"
                    actorName={getUserName(externalUpdates.laneId.actorId)}
                    remoteDisplay={
                      lanes.find((l) => l.id === externalUpdates.laneId?.remoteValue)?.name ?? '?'
                    }
                    onAccept={() => acceptRemote('laneId')}
                  />
                )}
              </div>
            )}
            {/* Without the card's own labels the picker would start from none, and
                a save after one click would drop every label the card has; a
                duplicate would silently lose them too. Say so instead. Only when
                they never loaded: a failed refresh still has the last good list. */}
            {labelsQuery.isLoadingError && (
              <div className="flex items-center gap-2">
                <div id={labelsErrorId}>
                  <InlineError message="Couldn't load this card's labels, so they can't be edited and the card can't be duplicated." />
                </div>
                <Button
                  variant="outline"
                  size="xs"
                  onClick={() => labelsQuery.refetch()}
                  disabled={labelsQuery.isFetching}
                >
                  {labelsQuery.isFetching ? 'Retrying...' : 'Retry'}
                </Button>
              </div>
            )}
            {!isArchived && !hasCardLabels && !labelsQuery.isLoadingError && (
              <Button
                variant="outline"
                size="sm"
                className="h-auto min-h-8 w-fit gap-1.5 px-2.5 py-1"
                disabled
                aria-busy="true"
              >
                <Tag className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
                <span className="text-muted-foreground">Loading labels...</span>
              </Button>
            )}
            {!isArchived && hasCardLabels && (
              <div className="flex items-center gap-1">
                <LabelPicker
                  allLabels={allLabelsQuery.data ?? []}
                  assignedLabels={assignedLabels}
                  onAdd={(id) => {
                    touchedFields.current.add('labelIds');
                    setSelectedLabelIds((prev) => [...(prev ?? originalLabelIds), id]);
                  }}
                  onRemove={(id) => {
                    touchedFields.current.add('labelIds');
                    setSelectedLabelIds((prev) =>
                      (prev ?? originalLabelIds).filter((x) => x !== id),
                    );
                  }}
                />
                {externalUpdates.labelIds && (
                  <ExternalUpdateIndicator
                    field="Labels"
                    actorName={getUserName(externalUpdates.labelIds.actorId)}
                    remoteDisplay={`${externalUpdates.labelIds.remoteLabelIds.length} label(s)`}
                    onAccept={() => acceptRemote('labelIds')}
                  />
                )}
              </div>
            )}
            {isArchived && assignedLabels.length > 0 && (
              <div className="flex flex-wrap gap-1">
                {assignedLabels.map((label) => (
                  <Badge
                    key={label.id}
                    className="opacity-70"
                    style={
                      label.color ? { backgroundColor: label.color, color: '#fff' } : undefined
                    }
                  >
                    {label.name}
                  </Badge>
                ))}
              </div>
            )}
          </div>
        </DialogHeader>

        {/* Two-column body (stacked on mobile) */}
        <div
          ref={bodyRef}
          className="flex flex-1 gap-0 overflow-hidden max-md:flex-col max-md:overflow-y-auto"
        >
          {/* Left column — details */}
          <div className="min-w-0 flex-1 px-6 py-4 md:overflow-y-auto">
            {/* Description */}
            <div className="mb-4">
              {/* Archived cards are frozen but their history stays readable, so
                  the view switcher renders for them too once history exists —
                  minus the Edit segment. */}
              {(!isArchived || isHistoryAvailable) && (
                <div className="mb-2 flex items-center gap-1">
                  {!isArchived && (
                    <Button
                      variant={descriptionView === 'edit' ? 'secondary' : 'ghost'}
                      size="xs"
                      onClick={() => setDescriptionView('edit')}
                    >
                      Edit
                    </Button>
                  )}
                  <Button
                    variant={descriptionView === 'preview' ? 'secondary' : 'ghost'}
                    size="xs"
                    onClick={() => setDescriptionView('preview')}
                  >
                    Preview
                  </Button>
                  {isHistoryAvailable && (
                    <Button
                      variant={descriptionView === 'history' ? 'secondary' : 'ghost'}
                      size="xs"
                      onClick={() => setDescriptionView('history')}
                    >
                      <History className="mr-1 h-3.5 w-3.5" />
                      History{historyCount > 0 ? ` (${historyCount})` : ''}
                    </Button>
                  )}
                  {!isArchived && externalUpdates.description && (
                    <ExternalUpdateIndicator
                      field="Description"
                      actorName={getUserName(externalUpdates.description.actorId)}
                      remoteDisplay={externalUpdates.description.remoteValue || '(empty)'}
                      onAccept={() => acceptRemote('description')}
                    />
                  )}
                </div>
              )}
              {descriptionView === 'history' && isHistoryAvailable ? (
                <CardDescriptionHistory cardId={card.id} />
              ) : descriptionView === 'edit' && !isArchived ? (
                <Textarea
                  value={description}
                  onChange={(e) => {
                    touchedFields.current.add('description');
                    setDescription(e.target.value);
                  }}
                  rows={16}
                  className="font-mono md:text-sm"
                  placeholder="Write a description..."
                />
              ) : (
                <div className="prose prose-sm max-w-none overflow-x-auto rounded-md border bg-muted/30 p-4 text-sm text-foreground">
                  {description ? (
                    <MarkdownRenderer
                      boardSlug={boardSlug}
                      cardNumbers={cardNumbers}
                      cardPreviews={cardPreviews}
                    >
                      {description}
                    </MarkdownRenderer>
                  ) : (
                    <p className="italic text-muted-foreground">
                      {isArchived
                        ? 'No description.'
                        : 'No description yet. Switch to Edit to add one.'}
                    </p>
                  )}
                </div>
              )}
            </div>

            <Separator className="my-4" />

            {/* Attachments */}
            <div>
              <Label className="mb-2 text-xs text-muted-foreground">Attachments</Label>
              <CardAttachments
                mode="live"
                cardId={card.id}
                currentUserId={currentUserId}
                currentUserRole={currentUserRole}
                readOnly={isArchived}
              />
            </div>

            {/* Metadata */}
            <div className="mt-4 text-xs text-muted-foreground">
              <p>
                Created by {getUserName(card.createdByUserId)} · {formatDateTime(card.createdAtUtc)}
              </p>
              <p>
                Updated by {getUserName(card.lastUpdatedByUserId)} ·{' '}
                {formatDateTime(card.lastUpdatedAtUtc)}
              </p>
            </div>
          </div>

          {/* Drag handle (desktop only) — wide hit area, thin visible line via pseudo-element */}
          <div
            onMouseDown={onMouseDown}
            className={cn(
              'relative hidden w-3 shrink-0 cursor-col-resize md:block',
              'after:absolute after:inset-y-0 after:left-1/2 after:w-px after:-translate-x-1/2 after:bg-border after:transition-colors',
              isDragging ? 'after:bg-primary/50' : 'hover:after:bg-primary/50',
            )}
          />

          {/* Right column — comments (below on mobile) */}
          <div
            className="comments-panel-resizable flex shrink-0 flex-col border-border px-5 pt-2 pb-4 max-md:w-full max-md:border-t md:overflow-y-auto"
            style={{ '--comments-width': `${Math.round(commentsWidth)}px` } as React.CSSProperties}
          >
            <h3 className="mb-3 text-sm font-semibold">Comments</h3>
            <CardComments
              cardId={card.id}
              boardId={boardId}
              currentUserId={currentUserId}
              currentUserRole={currentUserRole}
              readOnly={isArchived}
            />
          </div>
        </div>

        {/* Inline save error — the card-update path is
            the biggest current silent-loss gap; the error stays in the form so
            the draft is intact. Archive/restore failures are board-action tier
            and now surface via the global toast floor (use-archive-card /
            use-restore-card), so they're no longer rendered here. */}
        {saveError && (
          <div className="mx-6 mb-1">
            <InlineError message={saveError} />
          </div>
        )}

        {/* Footer. It wraps rather than overflows: on a phone-width dialog a
            delete confirmation's longer label is enough to push the last button
            past the edge, out of reach. The right-hand controls carry ml-auto,
            which keeps them on the right on a wide screen and when they drop to
            a line of their own; their group is also justify-end, so when the
            collision warning fills its first line, Close and Save wrap to the
            right rather than the left. The archived actions and the restore
            lane picker are display:contents so they wrap one control at a
            time, which lets Close share a line with the controls before it
            instead of stacking an extra row. */}
        <div className="flex flex-wrap items-center gap-2 border-t px-6 py-3">
          {isArchived ? (
            /* Archived card footer */
            <div className="contents">
              {showRestorePicker && lanes && lanes.length > 0 ? (
                <div className="contents">
                  <Select
                    value={restoreLaneId ?? ''}
                    onValueChange={(v) => v && setRestoreLaneId(v)}
                  >
                    <SelectTrigger className="w-40">
                      <SelectValue placeholder="Select lane">
                        {lanes.find((l) => l.id === restoreLaneId)?.name ?? 'Select lane'}
                      </SelectValue>
                    </SelectTrigger>
                    <SelectContent>
                      {lanes.map((lane) => (
                        <SelectItem key={lane.id} value={lane.id}>
                          {lane.name}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                  <Button
                    size="sm"
                    onClick={handleRestore}
                    disabled={!restoreLaneId || restoreMutation.isPending}
                  >
                    <ArchiveRestore className="mr-1 h-4 w-4" />
                    {restoreMutation.isPending ? 'Restoring...' : 'Restore'}
                  </Button>
                  <Button variant="outline" size="sm" onClick={() => setShowRestorePicker(false)}>
                    Cancel
                  </Button>
                </div>
              ) : (
                <>
                  <Button size="sm" onClick={handleRestore}>
                    <ArchiveRestore className="mr-1 h-4 w-4" />
                    Restore
                  </Button>
                  {/* Step aside while a delete is being confirmed, as the other
                      footer actions do while archive or restore is in progress. */}
                  {!isConfirmingDelete && duplicateButton}
                </>
              )}
              {canDelete && !showRestorePicker && (
                <Button
                  variant="destructive"
                  size="sm"
                  onClick={handleDelete}
                  disabled={deleteMutation.isPending}
                >
                  {isConfirmingDelete ? 'This cannot be undone. Confirm delete?' : 'Delete'}
                </Button>
              )}
            </div>
          ) : showArchiveActions ? (
            /* Active card — expanded archive actions */
            <div className="flex flex-wrap items-center gap-2">
              <Button size="sm" onClick={handleArchiveConfirm} disabled={archiveMutation.isPending}>
                <Archive className="mr-1 h-4 w-4" />
                {archiveMutation.isPending ? 'Archiving...' : 'Archive'}
              </Button>
              {canDelete && (
                <Button
                  variant="destructive"
                  size="sm"
                  onClick={handleDelete}
                  disabled={deleteMutation.isPending}
                >
                  {isConfirmingDelete ? 'This cannot be undone. Confirm?' : 'Delete'}
                </Button>
              )}
              <Button
                variant="outline"
                size="sm"
                onClick={() => {
                  setShowArchiveActions(false);
                  setIsConfirmingDelete(false);
                }}
              >
                Cancel
              </Button>
            </div>
          ) : (
            /* Active card — default state with archive button (all roles can archive) */
            <div className="flex flex-wrap items-center gap-2">
              <Button variant="outline" size="sm" onClick={handleArchiveClick}>
                <Archive className="mr-1 h-4 w-4" />
                Archive
              </Button>
              {duplicateButton}
            </div>
          )}
          {!isArchived && !showArchiveActions && (
            <div className="ml-auto flex flex-wrap items-center justify-end gap-2">
              {externalUpdateCount > 0 && (
                // A solid amber chip (the accent surface paired with its own
                // foreground) so the warning stays legible in both themes —
                // text-accent-foreground alone sits on no accent surface and
                // renders near-invisible on the dark dialog background. Named,
                // and placed at the Save locus, so it is hard to miss without
                // blocking the save.
                <div className="mr-auto flex items-center gap-2 rounded-md bg-accent px-3 py-1.5 text-sm text-accent-foreground">
                  <RefreshCw className="h-4 w-4 shrink-0" />
                  <span className="font-medium">{collisionMessage}</span>
                  <Button
                    variant="outline"
                    size="xs"
                    onClick={acceptAllRemote}
                    className="border-accent-foreground/40 bg-transparent text-accent-foreground hover:bg-accent-foreground/10"
                  >
                    {externalUpdateCount === 1 ? 'Accept their version' : 'Accept all'}
                  </Button>
                </div>
              )}
              <Button variant="outline" size="sm" onClick={handleClose}>
                Close
              </Button>
              <Button
                size="sm"
                onClick={handleSave}
                disabled={!isDirty || updateMutation.isPending}
              >
                {updateMutation.isPending ? 'Saving...' : 'Save'}
              </Button>
            </div>
          )}
          {isArchived && (
            <Button variant="outline" size="sm" className="ml-auto" onClick={handleClose}>
              Close
            </Button>
          )}
        </div>
      </div>
    );
  },
);
