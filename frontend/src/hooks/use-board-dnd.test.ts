import { describe, test, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, createElement } from 'react';
import { useBoardDnd } from './use-board-dnd';
import { queryKeys } from '@/lib/query-keys';
import type { BoardData, CardItem, CardSummary, Lane } from '@/types';

vi.mock('@/lib/api', () => ({
  reorderCard: vi.fn(),
}));

import { reorderCard } from '@/lib/api';

const mockReorderCard = vi.mocked(reorderCard);

const BOARD_ID = 'board-1';
const LANE_BACKLOG = 'lane-backlog';
const LANE_DONE = 'lane-done';
const LANE_ARCHIVE = 'lane-archive';

function makeLane(id: string, name: string, position: number): Lane {
  return { id, boardId: BOARD_ID, name, position };
}

function makeCardSummary(id: string, laneId: string, position: number): CardSummary {
  return {
    id,
    number: 1,
    name: `card-${id}`,
    descriptionMarkdown: '',
    sizeId: 'size-1',
    sizeName: 'M',
    laneId,
    position,
    isArchived: false,
    createdByUserId: 'u1',
    createdAtUtc: '2026-05-27T00:00:00Z',
    lastUpdatedByUserId: 'u1',
    lastUpdatedAtUtc: '2026-05-27T00:00:00Z',
    labels: [],
    commentCount: 0,
    attachmentCount: 0,
  };
}

function makeCardItem(id: string, laneId: string, position: number): CardItem {
  return {
    id,
    number: 1,
    name: `card-${id}`,
    descriptionMarkdown: '',
    laneId,
    position,
    sizeId: 'size-1',
    isArchived: false,
    createdByUserId: 'u1',
    createdAtUtc: '2026-05-27T00:00:00Z',
    lastUpdatedByUserId: 'u1',
    lastUpdatedAtUtc: '2026-05-27T00:00:00Z',
  };
}

function setupClient(initialData: BoardData) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  queryClient.setQueryData(queryKeys.boards.data(BOARD_ID), initialData);
  return queryClient;
}

function renderDnd(queryClient: QueryClient, isMobile = false) {
  const wrapper = ({ children }: { children: React.ReactNode }) =>
    createElement(QueryClientProvider, { client: queryClient }, children);
  return renderHook(
    () =>
      useBoardDnd(
        BOARD_ID,
        [makeCardItem('c1', LANE_BACKLOG, 0)],
        new Set([LANE_BACKLOG, LANE_DONE]),
        isMobile,
      ),
    { wrapper },
  );
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe('useBoardDnd reorder cache merge', () => {
  test('does not introduce the archive lane into the cached lane list', async () => {
    const initialLanes = [makeLane(LANE_BACKLOG, 'Backlog', 0), makeLane(LANE_DONE, 'Done', 1)];
    const initialData: BoardData = {
      lanes: initialLanes,
      cards: [makeCardSummary('c1', LANE_BACKLOG, 0)],
      sizes: [],
    };
    const queryClient = setupClient(initialData);

    // Server response includes the archive lane (matches the real reorder endpoint shape).
    mockReorderCard.mockResolvedValue({
      lanes: [...initialLanes, makeLane(LANE_ARCHIVE, 'Archive', Number.MAX_SAFE_INTEGER)],
      cards: [makeCardItem('c1', LANE_DONE, 0)],
    });

    const { result } = renderDnd(queryClient);

    await act(async () => {
      // Use the mutation directly via the reorderMutation closure — fire a drop.
      // Easiest path: simulate onDragEnd with our card landing in Done.
      result.current.onDragStart({ active: { id: 'c1' } } as never);
      result.current.onDragEnd({
        active: { id: 'c1' },
        over: { id: LANE_DONE, rect: { top: 0, height: 0 } },
      } as never);
    });

    await waitFor(() => {
      expect(mockReorderCard).toHaveBeenCalledOnce();
    });

    // Allow the onSuccess callback to flush.
    await waitFor(() => {
      const cached = queryClient.getQueryData<BoardData>(queryKeys.boards.data(BOARD_ID));
      expect(cached?.cards.find((c) => c.id === 'c1')?.laneId).toBe(LANE_DONE);
    });

    const cached = queryClient.getQueryData<BoardData>(queryKeys.boards.data(BOARD_ID));
    // The cached lane list must NOT have grown to include the archive lane.
    expect(cached?.lanes.map((l) => l.id)).toEqual([LANE_BACKLOG, LANE_DONE]);
    expect(cached?.lanes.some((l) => l.id === LANE_ARCHIVE)).toBe(false);
  });

  test('preserves enriched card fields (labels, counts) after reorder merge', async () => {
    const enriched: CardSummary = {
      ...makeCardSummary('c1', LANE_BACKLOG, 0),
      labels: [{ id: 'lbl-1', name: 'Bug', color: '#ff0000' }],
      commentCount: 3,
      attachmentCount: 1,
    };
    const initialData: BoardData = {
      lanes: [makeLane(LANE_BACKLOG, 'Backlog', 0), makeLane(LANE_DONE, 'Done', 1)],
      cards: [enriched],
      sizes: [],
    };
    const queryClient = setupClient(initialData);

    // Server returns plain CardItem (no labels/counts) — the merge must preserve enriched fields.
    mockReorderCard.mockResolvedValue({
      lanes: initialData.lanes,
      cards: [makeCardItem('c1', LANE_DONE, 0)],
    });

    const { result } = renderDnd(queryClient);

    await act(async () => {
      result.current.onDragStart({ active: { id: 'c1' } } as never);
      result.current.onDragEnd({
        active: { id: 'c1' },
        over: { id: LANE_DONE, rect: { top: 0, height: 0 } },
      } as never);
    });

    await waitFor(() => {
      const cached = queryClient.getQueryData<BoardData>(queryKeys.boards.data(BOARD_ID));
      expect(cached?.cards[0].laneId).toBe(LANE_DONE);
    });

    const cached = queryClient.getQueryData<BoardData>(queryKeys.boards.data(BOARD_ID));
    expect(cached?.cards[0].labels).toEqual([{ id: 'lbl-1', name: 'Bug', color: '#ff0000' }]);
    expect(cached?.cards[0].commentCount).toBe(3);
    expect(cached?.cards[0].attachmentCount).toBe(1);
  });
});

describe('useBoardDnd cancel', () => {
  test('cancelling a drag puts the card back in its saved lane and shows it again', () => {
    const queryClient = setupClient({ lanes: [], cards: [], sizes: [] });
    const { result } = renderDnd(queryClient);

    // Partial drag events, cast because the hook reads only these fields.
    act(() => {
      result.current.onDragStart({ active: { id: 'c1' } } as never);
      result.current.onDragOver({
        active: { id: 'c1', rect: { current: { translated: null } } },
        over: { id: LANE_DONE, rect: { top: 0, height: 0 } },
      } as never);
    });
    // The drag has moved the card into Done.
    expect(result.current.localCards.find((c) => c.id === 'c1')?.laneId).toBe(LANE_DONE);

    act(() => {
      result.current.onDragCancel();
    });

    expect(result.current.localCards.find((c) => c.id === 'c1')?.laneId).toBe(LANE_BACKLOG);
    expect(result.current.activeCardId).toBeNull();
    expect(mockReorderCard).not.toHaveBeenCalled();
  });
});

describe('useBoardDnd sensor gating (drag is desktop-only)', () => {
  test('registers drag sensors on desktop', () => {
    const queryClient = setupClient({
      lanes: [makeLane(LANE_BACKLOG, 'Backlog', 0)],
      cards: [],
      sizes: [],
    });
    const { result } = renderDnd(queryClient, false);
    // MouseSensor + TouchSensor — the board <DndContext> can start a drag.
    expect(result.current.sensors.length).toBe(2);
  });

  test('registers NO drag sensors on mobile so no drag (card or lane) can start', () => {
    const queryClient = setupClient({
      lanes: [makeLane(LANE_BACKLOG, 'Backlog', 0)],
      cards: [],
      sizes: [],
    });
    const { result } = renderDnd(queryClient, true);
    // Empty sensor set — the shared board <DndContext> has nothing to activate, so
    // both card reorder and lane reorder are inert. Card moves stay available via
    // the card-detail lane dropdown.
    expect(result.current.sensors.length).toBe(0);
  });
});
