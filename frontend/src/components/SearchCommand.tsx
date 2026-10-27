import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Archive, LayoutDashboard, Loader2, Search, X } from 'lucide-react';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';
import { fetchBoardBySlug, searchAllCards } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';
import { buildMarkdownSnippet } from '@/lib/markdown-snippet';
import { cn, isTextInputFocused } from '@/lib/utils';
import { useDebounce } from '@/hooks/use-debounce';
import { useClickOutside } from '@/hooks/use-click-outside';
import type { SearchResult } from '@/types';

export function SearchCommand() {
  const navigate = useNavigate();
  const { slug } = useParams<{ slug: string }>();
  const [query, setQuery] = useState('');
  const [dismissedQuery, setDismissedQuery] = useState<string | null>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const debouncedQuery = useDebounce(query.trim(), 300);

  const isSearchable = debouncedQuery.length >= 2 || /^\d/.test(debouncedQuery);

  // Resolve current board ID from route slug (reads from TanStack Query cache)
  const boardMetaQuery = useQuery({
    queryKey: queryKeys.boards.detail(slug as string),
    queryFn: () => fetchBoardBySlug(slug as string),
    enabled: !!slug,
    staleTime: 60_000,
  });
  const currentBoardId = boardMetaQuery.data?.id;

  // Derive open state: show dropdown when query is valid and not dismissed for this specific query
  const [isInputFocused, setIsInputFocused] = useState(false);
  const open = isSearchable && dismissedQuery !== debouncedQuery;

  // Close on outside click — callback always sees latest debouncedQuery via latest-ref pattern in hook
  useClickOutside(
    containerRef,
    useCallback(() => {
      setDismissedQuery(debouncedQuery);
    }, [debouncedQuery]),
  );

  // Keyboard shortcuts: "/" or Ctrl+K to focus
  useEffect(() => {
    function handleKeyDown(event: KeyboardEvent) {
      if (document.querySelector('[data-slot="dialog-overlay"]')) return;
      if (event.key === '/' && !isTextInputFocused()) {
        event.preventDefault();
        inputRef.current?.focus();
      }
      if ((event.ctrlKey || event.metaKey) && event.key === 'k') {
        event.preventDefault();
        inputRef.current?.focus();
      }
    }
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, []);

  // Auto-prefix pure numeric queries with # for exact card number lookup
  const effectiveQuery = /^\d+$/.test(debouncedQuery) ? `#${debouncedQuery}` : debouncedQuery;

  // currentBoardId fills two distinct backend params: boardId ranks this board's
  // non-archived matches first (priority); archiveBoardId keeps this board's archived
  // cards eligible so they still render under the Archived header.
  const searchQuery = useQuery({
    queryKey: queryKeys.search.cards(effectiveQuery, currentBoardId, currentBoardId),
    queryFn: () => searchAllCards(effectiveQuery, 20, currentBoardId, currentBoardId),
    enabled: isSearchable,
    staleTime: 30_000,
  });

  // Separate active and archived results
  const { activeResults, archivedResults } = useMemo(() => {
    const raw: SearchResult[] = searchQuery.data ?? [];
    const active: SearchResult[] = [];
    const archived: SearchResult[] = [];

    for (const group of raw) {
      const activeCards = group.cards.filter((c) => !c.isArchived);
      const archivedCards = group.cards.filter((c) => c.isArchived);
      if (activeCards.length > 0) {
        active.push({ ...group, cards: activeCards });
      }
      if (archivedCards.length > 0) {
        archived.push({ ...group, cards: archivedCards });
      }
    }

    return { activeResults: active, archivedResults: archived };
  }, [searchQuery.data]);

  // Parsed once per result set rather than on every render: hovering a row
  // re-renders the list.
  const snippets = useMemo(() => {
    const byCardId = new Map<string, string>();
    for (const group of searchQuery.data ?? []) {
      for (const card of group.cards) {
        if (card.descriptionMarkdown) {
          byCardId.set(card.id, buildMarkdownSnippet(card.descriptionMarkdown, 100));
        }
      }
    }
    return byCardId;
  }, [searchQuery.data]);

  const totalCards = useMemo(
    () =>
      activeResults.reduce((sum, r) => sum + r.cards.length, 0) +
      archivedResults.reduce((sum, r) => sum + r.cards.length, 0),
    [activeResults, archivedResults],
  );

  const flatResults = useMemo(
    () => [
      ...activeResults.flatMap((group) =>
        group.cards.map((card) => ({
          boardSlug: group.boardSlug,
          card,
          isArchived: false as const,
        })),
      ),
      ...archivedResults.flatMap((group) =>
        group.cards.map((card) => ({
          boardSlug: group.boardSlug,
          card,
          isArchived: true as const,
        })),
      ),
    ],
    [activeResults, archivedResults],
  );

  const [focusedIndex, setFocusedIndex] = useState(-1);
  const dropdownRef = useRef<HTMLDivElement>(null);

  // Reset focused index when results change
  useEffect(() => {
    setFocusedIndex(-1);
  }, [flatResults.length]);

  // Scroll focused item into view, accounting for sticky group headers
  useEffect(() => {
    const dropdown = dropdownRef.current;
    if (!dropdown) return;
    if (focusedIndex <= 0) {
      dropdown.scrollTop = 0;
      return;
    }
    const el = dropdown.querySelector<HTMLElement>(`[data-search-index="${focusedIndex}"]`);
    if (!el) return;

    const dropdownRect = dropdown.getBoundingClientRect();
    const elRect = el.getBoundingClientRect();
    const headerOffset = 28; // sticky group header height (~py-1.5 + text-xs + px-3)

    if (elRect.top < dropdownRect.top + headerOffset) {
      dropdown.scrollTop -= dropdownRect.top + headerOffset - elRect.top;
    } else if (elRect.bottom > dropdownRect.bottom) {
      dropdown.scrollTop += elRect.bottom - dropdownRect.bottom;
    }
  }, [focusedIndex]);

  const handleSelect = (boardSlug: string, cardNumber: number) => {
    setDismissedQuery(debouncedQuery);
    setQuery('');
    setFocusedIndex(-1);
    inputRef.current?.blur();
    navigate(`/boards/${boardSlug}/cards/${cardNumber}`);
  };

  const handleClear = () => {
    setQuery('');
    setDismissedQuery(debouncedQuery);
    setFocusedIndex(-1);
    inputRef.current?.focus();
  };

  const handleInputKeyDown = (event: React.KeyboardEvent) => {
    if (event.key === 'Escape') {
      setQuery('');
      setDismissedQuery(debouncedQuery);
      setFocusedIndex(-1);
      inputRef.current?.blur();
      return;
    }
    if (!open || flatResults.length === 0) return;
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setFocusedIndex((prev) => Math.min(prev + 1, flatResults.length - 1));
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setFocusedIndex((prev) => Math.max(prev - 1, 0));
    } else if (event.key === 'Enter' && focusedIndex >= 0) {
      event.preventDefault();
      const item = flatResults[focusedIndex];
      handleSelect(item.boardSlug, item.card.number);
    }
  };

  // Progressive placeholder based on available width
  const [placeholder, setPlaceholder] = useState('Search cards... (/ or Ctrl+K)');
  const inputWrapperRef = useRef<HTMLDivElement>(null);

  useLayoutEffect(() => {
    const el = inputWrapperRef.current;
    if (!el) return;

    const update = () => {
      const w = el.clientWidth;
      if (w >= 280) setPlaceholder('Search cards... (/ or Ctrl+K)');
      else if (w >= 140) setPlaceholder('Search... (/)');
      else setPlaceholder('/');
    };

    update();
    const observer = new ResizeObserver(update);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  return (
    <div ref={containerRef} className="relative w-full max-w-md">
      <div ref={inputWrapperRef} className="relative">
        {searchQuery.isFetching ? (
          <Loader2 className="pointer-events-none absolute left-2.5 top-1/2 w-4 h-4 -translate-y-1/2 animate-spin text-muted-foreground" />
        ) : (
          <Search className="pointer-events-none absolute left-2.5 top-1/2 w-4 h-4 -translate-y-1/2 text-muted-foreground" />
        )}
        {/* A fixed name: the placeholder is cleared on focus and shortens to "/" when the box
            is narrow, so it cannot be what the field is called. */}
        <Input
          ref={inputRef}
          type="text"
          aria-label="Search cards"
          placeholder={isInputFocused ? '' : placeholder}
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onFocus={() => {
            setIsInputFocused(true);
            setDismissedQuery(null);
          }}
          onBlur={() => setIsInputFocused(false)}
          onKeyDown={handleInputKeyDown}
          className="pl-8 pr-8"
        />
        {query.length > 0 && (
          <Button
            variant="ghost"
            size="icon-xs"
            aria-label="Clear search"
            onClick={handleClear}
            className="absolute right-2 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground"
          >
            <X className="w-4 h-4" />
          </Button>
        )}
      </div>
      {open && (
        <div
          ref={dropdownRef}
          className="absolute top-full mt-1 w-full max-h-80 overflow-y-auto rounded-lg border border-border bg-popover shadow-md z-50"
        >
          {searchQuery.isLoading && (
            <p className="px-3 py-4 text-sm text-muted-foreground">Searching...</p>
          )}
          {searchQuery.isSuccess && totalCards === 0 && (
            <p className="px-3 py-4 text-sm text-muted-foreground">No cards match your search</p>
          )}
          {searchQuery.isSuccess &&
            totalCards > 0 &&
            (() => {
              let flatIndex = 0;
              const hasMultipleGroups =
                activeResults.length + archivedResults.length > 1 || archivedResults.length > 0;

              const renderCard = (
                group: SearchResult,
                card: SearchResult['cards'][number],
                archived: boolean,
              ) => {
                const idx = flatIndex++;
                const snippet = snippets.get(card.id);
                return (
                  <button
                    key={card.id}
                    type="button"
                    data-search-index={idx}
                    onClick={() => handleSelect(group.boardSlug, card.number)}
                    onMouseEnter={() => setFocusedIndex(idx)}
                    className={cn(
                      'flex w-full items-start gap-2 px-3 py-2 text-left text-sm',
                      'hover:bg-accent/10 focus-visible:bg-accent/10 focus-visible:outline-none',
                      idx === focusedIndex && 'bg-accent text-accent-foreground',
                      archived && 'opacity-60',
                    )}
                  >
                    <span className="shrink-0 rounded bg-muted px-1.5 py-0.5 text-xs font-mono text-muted-foreground">
                      #{card.number}
                    </span>
                    <div className="min-w-0 flex-1">
                      <p className="truncate font-medium text-foreground">{card.name}</p>
                      {snippet && (
                        <p className="mt-0.5 truncate text-xs text-muted-foreground">{snippet}</p>
                      )}
                    </div>
                    {archived && (
                      <Archive className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground" />
                    )}
                  </button>
                );
              };

              return (
                <>
                  {activeResults.map((group) => (
                    <div key={group.boardId}>
                      {hasMultipleGroups && (
                        <div className="sticky top-0 flex items-center gap-1.5 border-b border-border bg-muted px-3 py-1.5 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                          <LayoutDashboard className="h-3 w-3" />
                          {group.boardName}
                        </div>
                      )}
                      {group.cards.map((card) => renderCard(group, card, false))}
                    </div>
                  ))}
                  {archivedResults.length > 0 && (
                    <div key="__archived__">
                      <div className="sticky top-0 flex items-center gap-1.5 border-b border-border bg-muted px-3 py-1.5 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                        <Archive className="h-3 w-3" />
                        Archived
                      </div>
                      {archivedResults.flatMap((group) =>
                        group.cards.map((card) => renderCard(group, card, true)),
                      )}
                    </div>
                  )}
                </>
              );
            })()}
          {searchQuery.isError && (
            <p className="px-3 py-4 text-sm text-destructive">Search failed. Please try again.</p>
          )}
        </div>
      )}
    </div>
  );
}
