import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Tag } from 'lucide-react';
import { LabelPicker } from '@/components/LabelPicker';
import { Button } from '@/components/ui/button';
import { useToggleCardLabel } from '@/hooks/use-toggle-card-label';
import { fetchLabels } from '@/lib/api';
import { QUERY_DEFAULTS } from '@/lib/query-config';
import { queryKeys } from '@/lib/query-keys';
import type { CardLabelSummary } from '@/types';

type CardLabelQuickPickerProps = {
  cardId: string;
  cardNumber: number;
  boardId: string;
  assignedLabels: CardLabelSummary[];
};

// Stops an interaction with the picker from also counting as one with the tile
// underneath. React events bubble through the component tree — including out of
// the popover's portal — so without this, a click in the picker would also open
// the card and a press on the trigger could start a card drag.
function stopTileInteraction(e: React.SyntheticEvent) {
  e.stopPropagation();
}

export function CardLabelQuickPicker({
  cardId,
  cardNumber,
  boardId,
  assignedLabels,
}: CardLabelQuickPickerProps) {
  const [isOpen, setIsOpen] = useState(false);

  // Fetched only while the picker is open: every tile carries a picker, and the
  // board's change events invalidate this query, so an always-on query would
  // refetch the label list once per board update for no one.
  const labelsQuery = useQuery({
    queryKey: queryKeys.labels.all(boardId),
    queryFn: () => fetchLabels(boardId),
    enabled: isOpen,
    ...QUERY_DEFAULTS.labels,
  });
  const allLabels = labelsQuery.data ?? [];

  const { mutate: toggleLabel } = useToggleCardLabel({ cardId, boardId });

  function handleAdd(labelId: string) {
    const label = allLabels.find((l) => l.id === labelId);
    if (label) toggleLabel({ label, isAssigned: false });
  }

  function handleRemove(labelId: string) {
    const label =
      assignedLabels.find((l) => l.id === labelId) ?? allLabels.find((l) => l.id === labelId);
    if (label) toggleLabel({ label, isAssigned: true });
  }

  return (
    <div
      className="shrink-0"
      onClick={stopTileInteraction}
      onPointerDown={stopTileInteraction}
      onMouseDown={stopTileInteraction}
      onTouchStart={stopTileInteraction}
    >
      <LabelPicker
        allLabels={allLabels}
        assignedLabels={assignedLabels}
        onAdd={handleAdd}
        onRemove={handleRemove}
        isLoading={labelsQuery.isPending && isOpen}
        onOpenChange={setIsOpen}
        trigger={{
          render: (
            <Button
              variant="ghost"
              size="icon-xs"
              aria-label={`Edit labels on card #${cardNumber}`}
              aria-haspopup="listbox"
              // Revealed on tile hover, or while keyboard focus is anywhere in
              // the tile, where a pointer can hover; always shown on touch
              // screens, which have no hover to reveal it with. Stays shown
              // while its popover is open.
              className="size-5 text-muted-foreground opacity-0 group-hover/tile:opacity-100 group-has-[:focus-visible]/tile:opacity-100 focus-visible:opacity-100 data-popup-open:opacity-100 [@media(hover:none)]:opacity-100"
            />
          ),
          content: <Tag className="size-3.5" />,
        }}
      />
    </div>
  );
}
