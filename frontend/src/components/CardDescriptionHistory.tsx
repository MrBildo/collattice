import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Button } from '@/components/ui/button';
import { InlineError } from '@/components/ui/inline-error';
import { MarkdownRenderer } from '@/components/MarkdownRenderer';
import { fetchCardHistory } from '@/lib/api';
import { toMessage } from '@/lib/mutation-floor';
import { parseUnifiedDiff } from '@/lib/unified-diff';
import { queryKeys } from '@/lib/query-keys';
import { QUERY_DEFAULTS } from '@/lib/query-config';
import { cn, formatDateTime } from '@/lib/utils';
import type { CardHistoryEntry } from '@/types';

// Read-only view of a card description's edit history: per revision, who
// changed it, when, what changed (unified diff), and the full text at that
// version one interaction away.
//
// Two rendering rules are deliberate and load-bearing:
//
// 1. Diff text renders as plain text nodes inside styled rows — never through
//    the markdown pipeline and never as HTML. A diff shows raw source lines;
//    treating them as anything richer would let historical description content
//    become markup.
// 2. The full-text view renders through the shared MarkdownRenderer (the one
//    place the sanitizer and the link-origin check live) with diagrams
//    suppressed, and without the card-link context: a revision is a snapshot
//    of the past, so `#NNN` references render as the plain text the author
//    wrote rather than as live links resolved against today's board.
type CardDescriptionHistoryProps = {
  cardId: string;
};

function DiffView({ diff }: { diff: string }) {
  const lines = useMemo(() => parseUnifiedDiff(diff), [diff]);

  return (
    <div className="overflow-x-auto font-mono text-xs leading-5">
      {/* Lines never wrap, so a long one overflows the scroll box. Block rows size to
          the box, not to their content, which would stop each row's colour band at the
          visible width and leave the rest of a long line unshaded once scrolled. Sizing
          this wrapper to the widest line (never narrower than the box) makes every row —
          and its band — as wide as the longest line. */}
      <div className="w-max min-w-full">
        {lines.map((line, index) => (
          <div
            key={index}
            className={cn(
              'flex min-h-5 whitespace-pre',
              line.kind === 'hunk' && 'bg-muted/60 text-muted-foreground',
              line.kind === 'add' && 'bg-primary/10',
              line.kind === 'remove' && 'bg-destructive/10',
            )}
          >
            <span
              aria-hidden="true"
              className={cn(
                'w-5 shrink-0 select-none pl-1.5',
                line.kind === 'add' && 'text-primary',
                line.kind === 'remove' && 'text-destructive',
              )}
            >
              {line.kind === 'add' ? '+' : line.kind === 'remove' ? '-' : ''}
            </span>
            {/* The gutter glyph above is aria-hidden and colour-coded — a screen-reader
                user can't perceive either, so add/remove needs a spoken equivalent. */}
            {line.kind === 'add' && <span className="sr-only">Added: </span>}
            {line.kind === 'remove' && <span className="sr-only">Removed: </span>}
            <span className="pr-3">{line.text}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

function FullTextView({ value }: { value: string }) {
  if (value === '') {
    return <p className="px-3 py-2 text-xs italic text-muted-foreground">(empty description)</p>;
  }
  return (
    <div className="prose prose-sm max-w-none overflow-x-auto border-t bg-muted/20 p-3 text-sm text-foreground">
      <MarkdownRenderer suppressDiagrams>{value}</MarkdownRenderer>
    </div>
  );
}

// How the original version's attribution reads. The name is always hedged with
// how it was inferred, because nobody observed who wrote that text: a card
// created before history recording began may have been edited by someone else
// first. With no inference available the view says so rather than rendering an
// empty name.
function formatOriginalAttribution(inferredEditor: CardHistoryEntry['inferredEditor']): string {
  if (!inferredEditor) {
    return 'original version — author unknown';
  }
  const basisLabel = inferredEditor.basis === 'creator' ? 'card creator' : 'inferred';
  return `original version — ${inferredEditor.name} (${basisLabel})`;
}

function RevisionItem({ entry }: { entry: CardHistoryEntry }) {
  const [isFullTextShown, setIsFullTextShown] = useState(false);

  // The trail's first revision holds the text as it stood when recording began.
  // Its observed author and timestamp are null on purpose — nobody saw that
  // value being written — so this view never renders an empty name or an
  // invalid date for it, and shows the inferred attribution instead.
  const isOriginal = entry.revision === 1;

  return (
    <li className="overflow-hidden rounded-md border border-border">
      <div className="flex flex-wrap items-center gap-x-2 gap-y-1 bg-muted/30 px-3 py-1.5 text-xs">
        <span className="font-medium text-foreground">Revision {entry.revision}</span>
        {isOriginal ? (
          <span className="text-muted-foreground">
            {formatOriginalAttribution(entry.inferredEditor)}
          </span>
        ) : (
          <span className="text-muted-foreground">
            {entry.editedByName ?? 'Unknown user'}
            {entry.editedAtUtc ? ` · ${formatDateTime(entry.editedAtUtc)}` : ''}
          </span>
        )}
        {entry.value !== undefined && (
          <Button
            variant="ghost"
            size="xs"
            className="ml-auto"
            onClick={() => setIsFullTextShown((shown) => !shown)}
          >
            {isFullTextShown ? 'Hide full text' : 'Show full text'}
          </Button>
        )}
      </div>
      {isOriginal ? (
        <p className="px-3 py-2 text-xs italic text-muted-foreground">
          The description as it stood when history recording began.
        </p>
      ) : entry.diff ? (
        <DiffView diff={entry.diff} />
      ) : (
        // A recorded revision can still carry an empty diff when the edit was
        // solely a line-ending or shared-trailing-newline change. The revision
        // is real; the full text is where the (invisible) difference lives.
        <p className="px-3 py-2 text-xs italic text-muted-foreground">
          No visible change — a line-ending or trailing-newline difference only. See the full text.
        </p>
      )}
      {isFullTextShown && entry.value !== undefined && <FullTextView value={entry.value} />}
    </li>
  );
}

export function CardDescriptionHistory({ cardId }: CardDescriptionHistoryProps) {
  const trailQuery = useQuery({
    queryKey: queryKeys.cards.historyTrail(cardId),
    // Default REST format (`both`): the diff renders in place, the full text is
    // one toggle away with no second round-trip. The whole trail is fetched —
    // trails are short today, and the server pages when one earns it.
    queryFn: () => fetchCardHistory(cardId),
    ...QUERY_DEFAULTS.history,
  });

  if (trailQuery.isLoading) {
    return <p className="py-2 text-sm text-muted-foreground">Loading history...</p>;
  }

  // A history view that renders empty on a failure would be indistinguishable
  // from "never edited" — the one ambiguity this feature exists to remove — so
  // a load failure is always presented as one.
  if (trailQuery.isError) {
    return (
      <div className="flex flex-col items-start gap-2 py-1">
        <InlineError message={`Couldn't load history: ${toMessage(trailQuery.error)}`} />
        <Button variant="outline" size="xs" onClick={() => trailQuery.refetch()}>
          Retry
        </Button>
      </div>
    );
  }

  const trail = trailQuery.data;
  if (!trail || trail.entries.length === 0) {
    return (
      <p className="py-2 text-sm italic text-muted-foreground">
        No recorded history. The trail starts at the first description edit made after edit history
        shipped.
      </p>
    );
  }

  return (
    <div className="flex flex-col gap-2">
      <p className="text-xs text-muted-foreground">
        {trail.totalCount} {trail.totalCount === 1 ? 'revision' : 'revisions'}, newest first
      </p>
      <ol aria-label="Description history, newest first" className="flex flex-col gap-2">
        {trail.entries.map((entry) => (
          <RevisionItem key={entry.revision} entry={entry} />
        ))}
      </ol>
    </div>
  );
}
