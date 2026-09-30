import {
  DndContext,
  DragOverlay,
  type DragEndEvent,
  type DragOverEvent,
  type DragStartEvent,
} from '@dnd-kit/core';
import { SortableContext, horizontalListSortingStrategy } from '@dnd-kit/sortable';
import { useQuery } from '@tanstack/react-query';
import { Columns3 } from 'lucide-react';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { AdminPanel } from '@/components/AdminPanel';
import { BoardHeader } from '@/components/BoardHeader';
import { CardDetailSheet } from '@/components/CardDetailSheet';
import { CardOverlay } from '@/components/CardOverlay';
import { CreateCardDialog } from '@/components/CreateCardDialog';
import { EmptyState } from '@/components/ui/empty-state';
import { GlobalAdminPanel } from '@/components/GlobalAdminPanel';
import { LaneColumn } from '@/components/LaneColumn';
import { LaneOverlay } from '@/components/LaneOverlay';
import { LoginScreen } from '@/components/LoginScreen';
import { fetchBoards, fetchCards, fetchVersion, fetchVersionStatus } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';
import { useAuth } from '@/hooks/use-auth';
import { useBoardData } from '@/hooks/use-board-data';
import { useBoardEvents } from '@/hooks/use-board-events';
import { useBoardDnd } from '@/hooks/use-board-dnd';
import { useLaneDnd } from '@/hooks/use-lane-dnd';
import { useCurrentUser } from '@/hooks/use-current-user';
import { useIsMobile } from '@/hooks/use-is-mobile';
import { useLaneCollapse } from '@/hooks/use-lane-collapse';
import { cn } from '@/lib/utils';
import { isLaneDragEvent } from '@/lib/dnd-active-type';
import { useLaneResize } from '@/hooks/use-lane-resize';
import type { CardPrefill } from '@/lib/duplicate-card';
import type { CardItem } from '@/types';

export function App() {
  const { slug, cardNumber } = useParams<{ slug: string; cardNumber: string }>();
  const navigate = useNavigate();
  const { loggedIn, handleLogin, handleLogout } = useAuth();
  const { currentUserId, currentUserName, currentUserRole, isAdmin } = useCurrentUser(loggedIn);

  const {
    board,
    boardId,
    boardMetaQuery,
    boardDataQuery,
    lanes,
    sizes,
    sizeMap,
    serverCards,
    enrichedCardMap,
  } = useBoardData(slug, loggedIn);

  // SSE for this board
  useBoardEvents(boardId);

  // Board list for switcher
  const boardsQuery = useQuery({
    queryKey: queryKeys.boards.all(),
    queryFn: fetchBoards,
    enabled: loggedIn,
    staleTime: 60_000,
  });

  const versionQuery = useQuery({
    queryKey: queryKeys.version(),
    queryFn: fetchVersion,
    staleTime: 5 * 60 * 1000,
    refetchOnWindowFocus: true,
  });

  // Update status. The backend throttles the real GitHub poll, so the client polls
  // softly — a long staleTime plus refetch-on-focus is plenty to surface a newly-available
  // version without per-client fan-out.
  const versionStatusQuery = useQuery({
    queryKey: queryKeys.versionStatus(),
    queryFn: fetchVersionStatus,
    staleTime: 30 * 60 * 1000,
    refetchOnWindowFocus: true,
  });

  const [createOpen, setCreateOpen] = useState(false);
  const [createLaneId, setCreateLaneId] = useState<string | undefined>(undefined);
  const [createDialogKey, setCreateDialogKey] = useState(0);
  const [createPrefill, setCreatePrefill] = useState<CardPrefill | undefined>(undefined);
  const [adminOpen, setAdminOpen] = useState(false);
  const [globalAdminOpen, setGlobalAdminOpen] = useState(false);

  const laneIds = useMemo(() => new Set(lanes.map((l) => l.id)), [lanes]);

  // Drag-and-drop is a desktop-only feature. One breakpoint hook gates
  // every drag surface; on the board page it drives the shared <DndContext>'s
  // sensor set (empty on mobile → cards and lanes are both inert).
  const isMobile = useIsMobile();

  const {
    sensors,
    collisionDetection,
    activeCardId,
    localCards,
    sortedServerCards,
    onDragStart: onCardDragStart,
    onDragOver: onCardDragOver,
    onDragEnd: onCardDragEnd,
  } = useBoardDnd(boardId, serverCards, laneIds, isMobile);

  const {
    activeLaneId,
    localLanes,
    onDragStart: onLaneDragStart,
    onDragOver: onLaneDragOver,
    onDragEnd: onLaneDragEnd,
  } = useLaneDnd(boardId, lanes);

  // Two drag concerns share one DndContext (card reorder + lane reorder). Route
  // each handler to the right hook by the active draggable's data.type.
  const handleDragStart = (event: DragStartEvent) =>
    isLaneDragEvent(event) ? onLaneDragStart(event) : onCardDragStart(event);
  const handleDragOver = (event: DragOverEvent) =>
    isLaneDragEvent(event) ? onLaneDragOver(event) : onCardDragOver(event);
  const handleDragEnd = (event: DragEndEvent) =>
    isLaneDragEvent(event) ? onLaneDragEnd(event) : onCardDragEnd(event);

  // Derive selected card from URL — check board data first, fall back to archived card fetch
  const cardNum = cardNumber ? parseInt(cardNumber, 10) : null;

  const boardCard = useMemo(() => {
    if (!cardNum || sortedServerCards.length === 0) return null;
    return sortedServerCards.find((c) => c.number === cardNum) ?? null;
  }, [cardNum, sortedServerCards]);

  // Fallback: fetch archived card when not found in board data
  const archivedCardQuery = useQuery({
    queryKey: queryKeys.boards.archivedCard(boardId as string, cardNum!),
    queryFn: async () => {
      const result = await fetchCards(boardId as string, {
        search: `#${cardNum}`,
        includeArchived: true,
        limit: 1,
      });
      return result.items.find((c) => c.number === cardNum) ?? null;
    },
    enabled: !!boardId && !!cardNum && !boardCard && sortedServerCards.length > 0,
    staleTime: 30_000,
  });

  const selectedCard = boardCard ?? archivedCardQuery.data ?? null;
  const isDetailOpen = selectedCard !== null || (!!cardNum && archivedCardQuery.isLoading);

  const handleDetailOpenChange = useCallback(
    (open: boolean) => {
      if (!open) {
        navigate(`/boards/${slug}`, { replace: true });
      }
    },
    [slug, navigate],
  );

  const handleNavigateCard = useCallback(
    (cardNumber: number) => {
      navigate(`/boards/${slug}/cards/${cardNumber}`, { replace: true });
    },
    [slug, navigate],
  );

  const byLane = useMemo(() => {
    const map = new Map<string, CardItem[]>();
    localLanes.forEach((lane) => map.set(lane.id, []));
    localCards.forEach((card) => map.get(card.laneId)?.push(card));
    return map;
  }, [localLanes, localCards]);

  const {
    isCollapsed,
    toggle: toggleLaneCollapse,
    initDefaults: initCollapseDefaults,
  } = useLaneCollapse(boardId);

  const laneIdList = useMemo(() => localLanes.map((l) => l.id), [localLanes]);
  const {
    sectionRef,
    gridTemplateColumns,
    handlePositions,
    handleHitWidth,
    onHandleMouseDown,
    draggingIndex: resizingHandleIndex,
  } = useLaneResize(boardId ?? '', laneIdList);

  // Auto-collapse empty lanes on first data load (when no saved state exists)
  useEffect(() => {
    if (lanes.length > 0 && byLane.size > 0) {
      initCollapseDefaults(
        lanes.map((l) => ({ id: l.id, cardCount: byLane.get(l.id)?.length ?? 0 })),
      );
    }
  }, [lanes, byLane, initCollapseDefaults]);

  const handleCardClick = useCallback(
    (card: CardItem) => {
      navigate(`/boards/${slug}/cards/${card.number}`, { replace: true });
    },
    [slug, navigate],
  );

  const handleNewCard = useCallback(() => {
    setCreateLaneId(undefined);
    setCreatePrefill(undefined);
    setCreateDialogKey((k) => k + 1);
    setCreateOpen(true);
  }, []);

  // Duplicating closes the source card and opens the new-card dialog pre-filled;
  // nothing is created until the user saves it.
  const handleDuplicate = useCallback(
    (prefill: CardPrefill) => {
      navigate(`/boards/${slug}`, { replace: true });
      setCreateLaneId(undefined);
      setCreatePrefill(prefill);
      setCreateDialogKey((k) => k + 1);
      setCreateOpen(true);
    },
    [slug, navigate],
  );

  if (!loggedIn) {
    return <LoginScreen onLogin={handleLogin} />;
  }

  const boards = boardsQuery.data ?? [];

  return (
    <main className="flex h-screen flex-col bg-background text-foreground">
      <BoardHeader
        boards={boards}
        currentSlug={slug}
        boardName={board?.name}
        isAdmin={isAdmin}
        version={versionQuery.data?.version}
        versionStatus={versionStatusQuery.data}
        currentUserName={currentUserName}
        currentUserRole={currentUserRole}
        onNewCard={handleNewCard}
        onBoardSettings={() => setAdminOpen(true)}
        onGlobalAdmin={() => setGlobalAdminOpen(true)}
        onLogout={handleLogout}
      />
      {boardDataQuery.isError && (
        <div className="mx-4 mt-4 rounded-lg border border-destructive/30 bg-destructive/5 p-4 text-center text-sm text-destructive">
          Failed to load board. Check your auth key and try again.
        </div>
      )}
      {(boardDataQuery.isLoading || boardMetaQuery.isLoading) && (
        <p className="py-8 text-center text-muted-foreground">Loading board...</p>
      )}
      <DndContext
        sensors={sensors}
        collisionDetection={collisionDetection}
        onDragStart={handleDragStart}
        onDragOver={handleDragOver}
        onDragEnd={handleDragEnd}
      >
        <section
          ref={sectionRef}
          // When lanes exist, justify-start packs the fixed-width lane
          // tracks from the left so leftover viewport width sits to the right of
          // the last lane as plain board background, instead of an `1fr` last lane
          // inflating to fill it. With no lanes the grid keeps its default stretch
          // so the centered EmptyState fills the board width.
          className={cn(
            'relative grid min-h-0 flex-1 gap-4 overflow-x-auto p-4 pb-2',
            gridTemplateColumns && 'justify-start',
          )}
          style={{
            gridTemplateColumns,
          }}
          aria-label="Kanban board"
        >
          <SortableContext items={laneIdList} strategy={horizontalListSortingStrategy}>
            {localLanes.map((lane) => (
              <LaneColumn
                key={lane.id}
                lane={lane}
                cards={byLane.get(lane.id) ?? []}
                onCardClick={handleCardClick}
                onAddCard={() => {
                  setCreateLaneId(lane.id);
                  setCreatePrefill(undefined);
                  setCreateDialogKey((k) => k + 1);
                  setCreateOpen(true);
                }}
                activeCardId={activeCardId}
                isLaneDragging={lane.id === activeLaneId}
                sizeMap={sizeMap}
                enrichedCardMap={enrichedCardMap}
                isCollapsed={isCollapsed(lane.id)}
                onToggleCollapse={() => toggleLaneCollapse(lane.id)}
              />
            ))}
          </SortableContext>
          {/* Board with no lanes: a freshly created board
              seeds only the hidden Archive lane, so the visible board is blank.
              Role-aware — an admin gets a real action into Board Settings → Lanes;
              a non-admin gets explanatory text and no dead button. Gated on the
              board having loaded so it never flashes during the initial fetch. */}
          {!boardDataQuery.isLoading &&
            !boardMetaQuery.isLoading &&
            !boardDataQuery.isError &&
            boardId &&
            localLanes.length === 0 && (
              <EmptyState
                icon={Columns3}
                title="This board has no lanes yet"
                description={
                  isAdmin
                    ? 'Lanes are the columns cards move between. Add one to get started.'
                    : 'An admin can add lanes in Board Settings.'
                }
                action={
                  isAdmin ? { label: 'Add a lane', onClick: () => setAdminOpen(true) } : undefined
                }
              />
            )}
          {/* Overlay resize handles — positioned over column gaps, no layout impact */}
          <div className="pointer-events-none absolute inset-0 hidden md:block">
            {handlePositions.map((left, i) => (
              <div
                key={i}
                onMouseDown={(e) => onHandleMouseDown(i, e)}
                className="pointer-events-auto absolute top-0 bottom-0 cursor-col-resize"
                style={{ left: left - handleHitWidth / 2, width: handleHitWidth }}
              >
                <div
                  className={cn(
                    'absolute inset-y-4 left-1/2 w-px -translate-x-1/2 rounded-full transition-colors',
                    resizingHandleIndex === i
                      ? 'bg-primary/60'
                      : 'bg-transparent hover:bg-primary/40',
                  )}
                />
              </div>
            ))}
          </div>
        </section>
        <DragOverlay>
          {(() => {
            if (activeLaneId) {
              const activeLane = localLanes.find((l) => l.id === activeLaneId);
              return activeLane ? <LaneOverlay lane={activeLane} /> : null;
            }
            const activeCard = activeCardId
              ? localCards.find((c) => c.id === activeCardId)
              : undefined;
            return activeCard ? <CardOverlay card={activeCard} sizeMap={sizeMap} /> : null;
          })()}
        </DragOverlay>
      </DndContext>

      <CardDetailSheet
        card={selectedCard}
        open={isDetailOpen}
        onOpenChange={handleDetailOpenChange}
        currentUserId={currentUserId}
        currentUserRole={currentUserRole}
        lanes={lanes}
        boardId={boardId}
        sizes={sizes}
        cardsInLane={selectedCard ? (byLane.get(selectedCard.laneId) ?? []) : []}
        onNavigateCard={handleNavigateCard}
        onDuplicate={handleDuplicate}
      />

      {boardId && (
        <CreateCardDialog
          key={createDialogKey}
          boardId={boardId}
          lanes={lanes}
          sizes={sizes}
          open={createOpen}
          onOpenChange={setCreateOpen}
          defaultLaneId={createLaneId}
          prefill={createPrefill}
        />
      )}

      {boardId && <AdminPanel boardId={boardId} open={adminOpen} onOpenChange={setAdminOpen} />}

      <GlobalAdminPanel open={globalAdminOpen} onOpenChange={setGlobalAdminOpen} />
    </main>
  );
}
