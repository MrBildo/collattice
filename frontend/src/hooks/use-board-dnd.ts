import {
  type DragEndEvent,
  type DragOverEvent,
  type DragStartEvent,
  MouseSensor,
  TouchSensor,
  closestCenter,
  pointerWithin,
  useSensor,
  useSensors,
  type CollisionDetection,
} from '@dnd-kit/core';
import { arrayMove } from '@dnd-kit/sortable';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { reorderCard } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';
import type { BoardData, CardItem } from '@/types';

const kanbanCollision: CollisionDetection = (args) => {
  const pointerCollisions = pointerWithin(args);
  if (pointerCollisions.length > 0) return pointerCollisions;
  return closestCenter(args);
};

export function useBoardDnd(
  boardId: string | undefined,
  serverCards: CardItem[],
  laneIds: Set<string>,
  isMobile: boolean,
) {
  const queryClient = useQueryClient();

  // Drag-and-drop is desktop-only. On mobile we register NO sensors, so
  // the shared board <DndContext> can never start a drag — neither cards nor
  // lanes (both concerns ride this one sensor set). Hooks must run unconditionally,
  // so the sensors are always created; `useSensors` is given an empty list on
  // mobile rather than gating the `useSensor` calls themselves. With no sensor to
  // satisfy an activation constraint, the SortableContexts/useSortables stay inert
  // — there is no half-working drag and no dead affordance (cards have no grab
  // handle; the lane header's `cursor-grab` is a `md:` style that doesn't apply
  // below the breakpoint). Card moves remain available via the card-detail lane
  // dropdown, so no capability is lost.
  const mouseSensor = useSensor(MouseSensor, { activationConstraint: { distance: 8 } });
  const touchSensor = useSensor(TouchSensor, {
    activationConstraint: { delay: 200, tolerance: 5 },
  });
  const desktopSensors = useSensors(mouseSensor, touchSensor);
  const noSensors = useSensors();
  const sensors = isMobile ? noSensors : desktopSensors;

  const [activeCardId, setActiveCardId] = useState<string | null>(null);
  const [dragPhase, setDragPhase] = useState<'idle' | 'dragging' | 'settling'>('idle');
  const [dragCards, setDragCards] = useState<CardItem[] | null>(null);

  // Reset drag state when switching boards (React-recommended "adjust state during render" pattern)
  const [prevBoardId, setPrevBoardId] = useState(boardId);
  if (boardId !== prevBoardId) {
    setPrevBoardId(boardId);
    setDragPhase('idle');
    setDragCards(null);
    setActiveCardId(null);
  }

  const sortedServerCards = useMemo(() => {
    const seen = new Set<string>();
    const unique: CardItem[] = [];
    for (const card of serverCards) {
      if (!seen.has(card.id)) {
        seen.add(card.id);
        unique.push(card);
      }
    }
    return unique.sort((a, b) => a.position - b.position);
  }, [serverCards]);

  const localCards = dragPhase === 'idle' ? sortedServerCards : (dragCards ?? sortedServerCards);

  const reorderMutation = useMutation({
    // A failed drag toasts ON TOP of the optimistic snap-back. The snap is the
    // primary self-evident signal; the toast adds the "why" — disambiguating a
    // server error from a misdrop, which matters on a multi-user board where
    // another user could be the cause. The onError below still
    // performs the rollback; the floor reads this meta and toasts.
    meta: { errorMessage: "Couldn't move card — try again" },
    mutationFn: (vars: { cardId: string; laneId: string; index: number }) =>
      reorderCard(vars.cardId, vars.laneId, vars.index),
    onMutate: async () => {
      if (!boardId) return;
      await queryClient.cancelQueries({ queryKey: queryKeys.boards.data(boardId) });
    },
    onSuccess: (data) => {
      if (!boardId) return;
      // Merge reorder response into existing cache. We update position/lane
      // on existing cards while preserving enriched fields (labels, counts).
      //
      // Lane set is intentionally NOT overwritten: reorder cannot create,
      // delete, or reposition lanes, so the cached lane list is already
      // authoritative. The reorder endpoint also returns archive lanes
      // (unlike the composite board endpoint which filters them out), so
      // accepting `data.lanes` verbatim would briefly flash the archive
      // lane into the rendered lane list during the drop transition.
      queryClient.setQueryData<BoardData>(queryKeys.boards.data(boardId), (old) => {
        if (!old) return old;
        const updatedMap = new Map(data.cards.map((c) => [c.id, c]));
        return {
          ...old,
          cards: old.cards.map((existing) => {
            const updated = updatedMap.get(existing.id);
            return updated
              ? { ...existing, laneId: updated.laneId, position: updated.position }
              : existing;
          }),
        };
      });
    },
    onError: () => {
      if (!boardId) return;
      queryClient.invalidateQueries({ queryKey: queryKeys.boards.data(boardId) });
    },
    onSettled: () => {
      setDragPhase('idle');
      setDragCards(null);
    },
  });

  const onDragStart = (event: DragStartEvent) => {
    setDragPhase('dragging');
    setActiveCardId(String(event.active.id));
  };

  const onDragOver = (event: DragOverEvent) => {
    const { active, over } = event;
    if (!over) return;

    const activeId = String(active.id);
    const overId = String(over.id);

    setDragCards((prev) => {
      prev = prev ?? sortedServerCards;
      const activeIdx = prev.findIndex((c) => c.id === activeId);
      if (activeIdx === -1) return prev;

      const activeCard = prev[activeIdx];
      const activeLaneId = activeCard.laneId;
      const overLaneId = laneIds.has(overId)
        ? overId
        : (prev.find((c) => c.id === overId)?.laneId ?? null);

      if (!overLaneId) return prev;

      if (activeLaneId === overLaneId) {
        if (laneIds.has(overId)) return prev;
        const laneCards = prev.filter((c) => c.laneId === activeLaneId);
        const oldIdx = laneCards.findIndex((c) => c.id === activeId);
        const newIdx = laneCards.findIndex((c) => c.id === overId);
        if (oldIdx === -1 || newIdx === -1 || oldIdx === newIdx) return prev;

        const reordered = arrayMove(laneCards, oldIdx, newIdx);
        const reorderedIds = new Set(reordered.map((c) => c.id));
        const rest = prev.filter((c) => !reorderedIds.has(c.id));
        return [...rest, ...reordered];
      }

      const overLaneCards = prev.filter((c) => c.laneId === overLaneId && c.id !== activeId);

      let newIndex = overLaneCards.length;
      if (!laneIds.has(overId)) {
        const overIndex = overLaneCards.findIndex((c) => c.id === overId);
        if (overIndex >= 0) {
          const isBelowOver =
            active.rect.current.translated &&
            active.rect.current.translated.top > over.rect.top + over.rect.height;
          newIndex = isBelowOver ? overIndex + 1 : overIndex;
        }
      }

      const movedCard = { ...activeCard, laneId: overLaneId };
      const withoutActive = prev.filter((c) => c.id !== activeId);
      const targetLaneCards = withoutActive.filter((c) => c.laneId === overLaneId);
      const otherCards = withoutActive.filter((c) => c.laneId !== overLaneId);

      targetLaneCards.splice(newIndex, 0, movedCard);
      return [...otherCards, ...targetLaneCards];
    });
  };

  const onDragEnd = (event: DragEndEvent) => {
    const { active, over } = event;
    const cardId = String(active.id);

    if (!over) {
      setDragPhase('idle');
      setDragCards(null);
      setActiveCardId(null);
      return;
    }

    const card = localCards.find((c) => c.id === cardId);
    if (!card) {
      setDragPhase('idle');
      setActiveCardId(null);
      return;
    }

    const targetLaneId = card.laneId;
    const laneCards = localCards.filter((c) => c.laneId === targetLaneId);
    const index = laneCards.findIndex((c) => c.id === cardId);

    setDragPhase('settling');
    reorderMutation.mutate({ cardId, laneId: targetLaneId, index: index === -1 ? 0 : index });
    setActiveCardId(null);
  };

  // Escape during a drag cancels it. Without this the card would stay where the
  // drag had moved it, still hidden as the dragged card, until a reload.
  const onDragCancel = () => {
    setDragPhase('idle');
    setDragCards(null);
    setActiveCardId(null);
  };

  return {
    sensors,
    collisionDetection: kanbanCollision,
    activeCardId,
    localCards,
    sortedServerCards,
    onDragStart,
    onDragOver,
    onDragEnd,
    onDragCancel,
  };
}
