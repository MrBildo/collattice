import { useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { useRef } from 'react';
import { MessageSquare, Paperclip } from 'lucide-react';
import { CardLabelQuickPicker } from '@/components/CardLabelQuickPicker';
import { Badge } from '@/components/ui/badge';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { useLabelLayout } from '@/hooks/use-label-layout';
import { cn, getContrastColor } from '@/lib/utils';
import type { CardItem, CardSummary } from '@/types';

type SortableCardProps = {
  card: CardItem;
  boardId: string;
  onCardClick: (card: CardItem) => void;
  isDragging: boolean;
  sizeMap: Map<string, string>;
  enrichedData?: CardSummary;
};

export function SortableCard({
  card,
  boardId,
  onCardClick,
  isDragging,
  sizeMap,
  enrichedData,
}: SortableCardProps) {
  const { attributes, listeners, setNodeRef, transform, transition } = useSortable({ id: card.id });
  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
  };
  const labels = [...(enrichedData?.labels ?? [])].sort((a, b) => a.name.length - b.name.length);
  const commentCount = enrichedData?.commentCount ?? 0;
  const attachmentCount = enrichedData?.attachmentCount ?? 0;
  const labelContainerRef = useRef<HTMLDivElement>(null);
  const labelLayout = useLabelLayout(labels, labelContainerRef);
  // Archived cards are frozen, so they get no label picker. The board never
  // shows them today; this keeps the tile honest if it ever does.
  const isArchived = enrichedData?.isArchived ?? card.isArchived;

  return (
    <div
      ref={setNodeRef}
      style={style}
      {...listeners}
      {...attributes}
      onClick={() => onCardClick(card)}
      className={cn(
        'group/tile cursor-pointer overflow-hidden rounded-lg border border-border bg-card p-3 shadow-xs transition-shadow hover:shadow-md hover:border-primary/30',
        isDragging && 'opacity-0',
      )}
    >
      <div className="flex items-start justify-between gap-2">
        <h3 className="min-w-0 text-sm font-medium leading-snug break-words">{card.name}</h3>
        {(() => {
          const sizeName = sizeMap.get(card.sizeId) ?? '?';
          let sizeDisplay: string;
          if (sizeName.length <= 3) {
            sizeDisplay = sizeName;
          } else {
            sizeDisplay = sizeName[0].toUpperCase();
          }
          return (
            <Tooltip>
              <TooltipTrigger render={<span />}>
                <Badge variant="outline" className="mt-0.5 shrink-0 text-xs">
                  {sizeDisplay}
                </Badge>
              </TooltipTrigger>
              <TooltipContent>{sizeName}</TooltipContent>
            </Tooltip>
          );
        })()}
      </div>

      <div className="mt-2 flex items-center gap-3 text-xs text-muted-foreground">
        <span>#{card.number}</span>

        {commentCount > 0 && (
          <span className="flex items-center gap-0.5" title={`${commentCount} comments`}>
            <MessageSquare className="h-3.5 w-3.5" />
            {commentCount}
          </span>
        )}

        {attachmentCount > 0 && (
          <span className="flex items-center gap-0.5" title={`${attachmentCount} attachments`}>
            <Paperclip className="h-3.5 w-3.5" />
            {attachmentCount}
          </span>
        )}
      </div>

      {/*
       * Label row height is reserved unconditionally so card height does not
       * change when labels are added or removed. Mid-drop, the drag overlay
       * (which renders without the label row) and the landed card (which may
       * have labels) would otherwise differ in height — producing a 1-frame
       * reflow flash on every drop. aria-hidden when empty so screen
       * readers don't announce a vacant region. The label picker sits outside
       * the measured label container so the chips lay out in the width that
       * is left beside it, and its reserved slot does not move on hover.
       */}
      <div className="mt-2 flex min-h-5 items-center gap-1">
        <div
          ref={labelContainerRef}
          aria-hidden={labels.length === 0}
          className="flex min-w-0 flex-1 items-center gap-1"
        >
          {labels.length > 0 && (
            <>
              {labelLayout.items.map((item) =>
                item.mode === 'full' ? (
                  <Tooltip key={item.label.id}>
                    <TooltipTrigger render={<span className="flex items-center" />}>
                      <Badge
                        variant="secondary"
                        className="max-w-full rounded-sm text-xs"
                        style={
                          item.label.color
                            ? {
                                backgroundColor: item.label.color,
                                color: getContrastColor(item.label.color),
                              }
                            : undefined
                        }
                      >
                        {item.label.name}
                      </Badge>
                    </TooltipTrigger>
                    <TooltipContent>{item.label.name}</TooltipContent>
                  </Tooltip>
                ) : (
                  <Tooltip key={item.label.id}>
                    <TooltipTrigger render={<span className="flex items-center" />}>
                      <span
                        className="inline-block h-5 w-6 shrink-0 rounded-sm"
                        style={{ backgroundColor: item.label.color ?? '#6b7280' }}
                      />
                    </TooltipTrigger>
                    <TooltipContent>{item.label.name}</TooltipContent>
                  </Tooltip>
                ),
              )}
              {labelLayout.overflowCount > 0 && (
                <Tooltip>
                  <TooltipTrigger render={<span />}>
                    <Badge variant="outline" className="rounded-sm text-xs">
                      +{labelLayout.overflowCount}
                    </Badge>
                  </TooltipTrigger>
                  <TooltipContent>
                    {labels
                      .slice(labels.length - labelLayout.overflowCount)
                      .map((l) => l.name)
                      .join(', ')}
                  </TooltipContent>
                </Tooltip>
              )}
            </>
          )}
        </div>
        {!isArchived && (
          <CardLabelQuickPicker
            cardId={card.id}
            cardNumber={card.number}
            boardId={boardId}
            assignedLabels={enrichedData?.labels ?? []}
          />
        )}
      </div>
    </div>
  );
}
