import { type DragEndEvent, type DragOverEvent, type DragStartEvent } from '@dnd-kit/core';
import { arrayMove } from '@dnd-kit/sortable';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { reorderLanes } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';
import type { BoardData, Lane } from '@/types';

// Lane drag-drop. Sibling to use-board-dnd.ts — deliberately a separate hook
// rather than folding lane logic into the dense card hook (which carries
// scar tissue from prior regressions we don't want to destabilize). The two
// hooks share one board <DndContext>; App.tsx
// discriminates by the active draggable's data.type ('lane' vs card) and routes
// each context handler to the right hook. This hook owns only the lane path.
//
// Same optimistic shape as the card hook, one axis up: local reorder during
// drag, reconcile on drop via POST /boards/{boardId}/lanes/reorder (server owns
// all position math), merge-not-overwrite on success, invalidate-rollback on
// error. Desktop-only (the grab target is the lane header, which has no touch
// listeners — the Lanes admin tab is the touch fallback).
export function useLaneDnd(boardId: string | undefined, serverLanes: Lane[]) {
  const queryClient = useQueryClient();

  const [activeLaneId, setActiveLaneId] = useState<string | null>(null);
  const [dragLanes, setDragLanes] = useState<Lane[] | null>(null);

  // Reset drag state when switching boards (React-recommended "adjust state
  // during render" pattern — mirrors use-board-dnd.ts).
  const [prevBoardId, setPrevBoardId] = useState(boardId);
  if (boardId !== prevBoardId) {
    setPrevBoardId(boardId);
    setDragLanes(null);
    setActiveLaneId(null);
  }

  const sortedServerLanes = useMemo(
    () => [...serverLanes].sort((a, b) => a.position - b.position),
    [serverLanes],
  );

  // During a drag, render the optimistic order; otherwise the server order.
  const localLanes = dragLanes ?? sortedServerLanes;

  const reorderMutation = useMutation({
    // The operator is looking at the board when they drag, so a failed reorder
    // snaps back via the onError invalidate (the self-evident signal) and a
    // toast adds the "why" — same disposition the card hook uses.
    meta: { errorMessage: "Couldn't reorder lanes — try again" },
    mutationFn: (orderedLaneIds: string[]) => reorderLanes(boardId as string, orderedLaneIds),
    onMutate: async () => {
      if (!boardId) return;
      await queryClient.cancelQueries({ queryKey: queryKeys.boards.data(boardId) });
    },
    onSuccess: (lanes) => {
      if (!boardId) return;
      // Merge the server's new dense positions into the cached board-data lane
      // list, then re-sort so the rendered order matches before the SSE refetch
      // lands. The server returns only the reordered non-archive lanes; cards,
      // sizes, and any archive lane in the cache are left untouched.
      const positionById = new Map(lanes.map((l) => [l.id, l.position]));
      queryClient.setQueryData<BoardData>(queryKeys.boards.data(boardId), (old) => {
        if (!old) return old;
        const merged = old.lanes.map((lane) => {
          const position = positionById.get(lane.id);
          return position === undefined ? lane : { ...lane, position };
        });
        return {
          ...old,
          lanes: [...merged].sort((a, b) => a.position - b.position),
        };
      });
    },
    onError: () => {
      if (!boardId) return;
      queryClient.invalidateQueries({ queryKey: queryKeys.boards.data(boardId) });
    },
    onSettled: () => {
      setDragLanes(null);
    },
  });

  const onDragStart = (event: DragStartEvent) => {
    setActiveLaneId(String(event.active.id));
    setDragLanes(sortedServerLanes);
  };

  const onDragOver = (event: DragOverEvent) => {
    const { active, over } = event;
    if (!over) return;

    const activeId = String(active.id);
    const overId = String(over.id);
    if (activeId === overId) return;

    setDragLanes((prev) => {
      const base = prev ?? sortedServerLanes;
      const oldIdx = base.findIndex((l) => l.id === activeId);
      const newIdx = base.findIndex((l) => l.id === overId);
      if (oldIdx === -1 || newIdx === -1 || oldIdx === newIdx) return base;
      return arrayMove(base, oldIdx, newIdx);
    });
  };

  const onDragEnd = (event: DragEndEvent) => {
    const { active, over } = event;
    setActiveLaneId(null);

    if (!over || !boardId) {
      setDragLanes(null);
      return;
    }

    const activeId = String(active.id);
    const overId = String(over.id);

    const base = dragLanes ?? sortedServerLanes;
    const oldIdx = base.findIndex((l) => l.id === activeId);
    const newIdx = base.findIndex((l) => l.id === overId);
    if (oldIdx === -1 || newIdx === -1) {
      setDragLanes(null);
      return;
    }

    const finalOrder = arrayMove(base, oldIdx, newIdx);

    // No-op guard: if the drop lands the lane back where it started, the order
    // is unchanged — skip the round-trip (and the renumber it would trigger).
    const unchanged = finalOrder.every((lane, i) => lane.id === sortedServerLanes[i]?.id);
    if (unchanged) {
      setDragLanes(null);
      return;
    }

    setDragLanes(finalOrder);
    reorderMutation.mutate(finalOrder.map((l) => l.id));
  };

  // Escape during a drag cancels it: the lanes go back to the saved order.
  const onDragCancel = () => {
    setActiveLaneId(null);
    setDragLanes(null);
  };

  return {
    activeLaneId,
    localLanes,
    onDragStart,
    onDragOver,
    onDragEnd,
    onDragCancel,
  };
}
