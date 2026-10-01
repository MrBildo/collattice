import { describe, test, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, createElement } from 'react';
import { useLaneDnd } from './use-lane-dnd';
import { queryKeys } from '@/lib/query-keys';
import type { BoardData, Lane } from '@/types';

vi.mock('@/lib/api', () => ({
  reorderLanes: vi.fn(),
}));

import { reorderLanes } from '@/lib/api';

const mockReorderLanes = vi.mocked(reorderLanes);

const BOARD_ID = 'board-1';
const LANE_A = 'lane-a';
const LANE_B = 'lane-b';
const LANE_C = 'lane-c';

function makeLane(id: string, name: string, position: number): Lane {
  return { id, boardId: BOARD_ID, name, position };
}

const serverLanes = [
  makeLane(LANE_A, 'Backlog', 0),
  makeLane(LANE_B, 'In Progress', 1),
  makeLane(LANE_C, 'Done', 2),
];

function setupClient(initialData?: BoardData) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  if (initialData) {
    queryClient.setQueryData(queryKeys.boards.data(BOARD_ID), initialData);
  }
  return queryClient;
}

function renderLaneDnd(queryClient: QueryClient, lanes: Lane[] = serverLanes) {
  const wrapper = ({ children }: { children: React.ReactNode }) =>
    createElement(QueryClientProvider, { client: queryClient }, children);
  return renderHook(() => useLaneDnd(BOARD_ID, lanes), { wrapper });
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe('useLaneDnd optimistic reorder', () => {
  test('localLanes reflects the server order at rest, sorted by position', () => {
    const queryClient = setupClient();
    // Pass lanes out of order — the hook must sort them.
    const { result } = renderLaneDnd(queryClient, [
      makeLane(LANE_C, 'Done', 2),
      makeLane(LANE_A, 'Backlog', 0),
      makeLane(LANE_B, 'In Progress', 1),
    ]);

    expect(result.current.localLanes.map((l) => l.id)).toEqual([LANE_A, LANE_B, LANE_C]);
    expect(result.current.activeLaneId).toBeNull();
  });

  test('dragging a lane over another reorders localLanes optimistically before drop', () => {
    const queryClient = setupClient();
    const { result } = renderLaneDnd(queryClient);

    act(() => {
      result.current.onDragStart({ active: { id: LANE_A } } as never);
      result.current.onDragOver({ active: { id: LANE_A }, over: { id: LANE_C } } as never);
    });

    // A moved to where C was → B, C, A.
    expect(result.current.localLanes.map((l) => l.id)).toEqual([LANE_B, LANE_C, LANE_A]);
    expect(result.current.activeLaneId).toBe(LANE_A);
    // No server call until drop.
    expect(mockReorderLanes).not.toHaveBeenCalled();
  });

  test('dropping a moved lane calls reorderLanes with the complete new order', async () => {
    mockReorderLanes.mockResolvedValue([
      makeLane(LANE_B, 'In Progress', 0),
      makeLane(LANE_A, 'Backlog', 1),
      makeLane(LANE_C, 'Done', 2),
    ]);
    const queryClient = setupClient();
    const { result } = renderLaneDnd(queryClient);

    await act(async () => {
      result.current.onDragStart({ active: { id: LANE_A } } as never);
      result.current.onDragOver({ active: { id: LANE_A }, over: { id: LANE_B } } as never);
      result.current.onDragEnd({ active: { id: LANE_A }, over: { id: LANE_B } } as never);
    });

    await waitFor(() => {
      expect(mockReorderLanes).toHaveBeenCalledOnce();
    });
    // Complete desired order, A and B swapped.
    expect(mockReorderLanes).toHaveBeenCalledWith(BOARD_ID, [LANE_B, LANE_A, LANE_C]);
    expect(result.current.activeLaneId).toBeNull();
  });

  test('a drop that lands the lane back in place is a no-op (no server call)', async () => {
    const queryClient = setupClient();
    const { result } = renderLaneDnd(queryClient);

    await act(async () => {
      result.current.onDragStart({ active: { id: LANE_A } } as never);
      // over its own id → no reorder
      result.current.onDragEnd({ active: { id: LANE_A }, over: { id: LANE_A } } as never);
    });

    expect(mockReorderLanes).not.toHaveBeenCalled();
    expect(result.current.localLanes.map((l) => l.id)).toEqual([LANE_A, LANE_B, LANE_C]);
  });

  test('a drop with no drop target cancels the optimistic order without a server call', async () => {
    const queryClient = setupClient();
    const { result } = renderLaneDnd(queryClient);

    await act(async () => {
      result.current.onDragStart({ active: { id: LANE_A } } as never);
      result.current.onDragOver({ active: { id: LANE_A }, over: { id: LANE_C } } as never);
      result.current.onDragEnd({ active: { id: LANE_A }, over: null } as never);
    });

    expect(mockReorderLanes).not.toHaveBeenCalled();
    expect(result.current.localLanes.map((l) => l.id)).toEqual([LANE_A, LANE_B, LANE_C]);
  });

  test('cancelling a drag restores the saved order and clears the dragged lane', () => {
    const queryClient = setupClient();
    const { result } = renderLaneDnd(queryClient);

    // Partial drag events, cast because the hook reads only these fields.
    act(() => {
      result.current.onDragStart({ active: { id: LANE_A } } as never);
      result.current.onDragOver({ active: { id: LANE_A }, over: { id: LANE_C } } as never);
    });
    act(() => {
      result.current.onDragCancel();
    });

    expect(result.current.localLanes.map((l) => l.id)).toEqual([LANE_A, LANE_B, LANE_C]);
    expect(result.current.activeLaneId).toBeNull();
    expect(mockReorderLanes).not.toHaveBeenCalled();
  });
});

describe('useLaneDnd cache reconciliation', () => {
  test('onSuccess merges the new dense positions into the cached lane list and re-sorts', async () => {
    const initialData: BoardData = {
      lanes: serverLanes,
      cards: [],
      sizes: [],
    };
    const queryClient = setupClient(initialData);

    // Server returns the reordered lanes with new dense positions (B,A,C).
    mockReorderLanes.mockResolvedValue([
      makeLane(LANE_B, 'In Progress', 0),
      makeLane(LANE_A, 'Backlog', 1),
      makeLane(LANE_C, 'Done', 2),
    ]);
    const { result } = renderLaneDnd(queryClient);

    await act(async () => {
      result.current.onDragStart({ active: { id: LANE_A } } as never);
      result.current.onDragOver({ active: { id: LANE_A }, over: { id: LANE_B } } as never);
      result.current.onDragEnd({ active: { id: LANE_A }, over: { id: LANE_B } } as never);
    });

    await waitFor(() => {
      const cached = queryClient.getQueryData<BoardData>(queryKeys.boards.data(BOARD_ID));
      expect(cached?.lanes.map((l) => l.id)).toEqual([LANE_B, LANE_A, LANE_C]);
    });

    const cached = queryClient.getQueryData<BoardData>(queryKeys.boards.data(BOARD_ID));
    // Positions came from the server response, sorted ascending.
    expect(cached?.lanes.map((l) => l.position)).toEqual([0, 1, 2]);
  });

  test('does not introduce or remove any lane in the cached list (e.g. an archive lane)', async () => {
    const archiveLane = makeLane('lane-archive', 'Archive', Number.MAX_SAFE_INTEGER);
    const initialData: BoardData = {
      lanes: [...serverLanes, archiveLane],
      cards: [],
      sizes: [],
    };
    const queryClient = setupClient(initialData);

    // Server returns only the non-archive lanes (matches the real endpoint).
    mockReorderLanes.mockResolvedValue([
      makeLane(LANE_B, 'In Progress', 0),
      makeLane(LANE_A, 'Backlog', 1),
      makeLane(LANE_C, 'Done', 2),
    ]);
    const { result } = renderLaneDnd(queryClient, serverLanes);

    await act(async () => {
      result.current.onDragStart({ active: { id: LANE_A } } as never);
      result.current.onDragEnd({ active: { id: LANE_A }, over: { id: LANE_B } } as never);
    });

    await waitFor(() => {
      expect(mockReorderLanes).toHaveBeenCalledOnce();
    });

    const cached = queryClient.getQueryData<BoardData>(queryKeys.boards.data(BOARD_ID));
    // The archive lane stays in the cached list, untouched (still int.MaxValue).
    const archived = cached?.lanes.find((l) => l.id === 'lane-archive');
    expect(archived?.position).toBe(Number.MAX_SAFE_INTEGER);
    expect(cached?.lanes).toHaveLength(4);
  });

  test('a failed reorder invalidates board data so the optimistic order rolls back', async () => {
    const initialData: BoardData = {
      lanes: serverLanes,
      cards: [],
      sizes: [],
    };
    const queryClient = setupClient(initialData);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    mockReorderLanes.mockRejectedValue(new Error('boom'));
    const { result } = renderLaneDnd(queryClient);

    await act(async () => {
      result.current.onDragStart({ active: { id: LANE_A } } as never);
      result.current.onDragEnd({ active: { id: LANE_A }, over: { id: LANE_B } } as never);
    });

    await waitFor(() => {
      expect(invalidateSpy).toHaveBeenCalledWith({
        queryKey: queryKeys.boards.data(BOARD_ID),
      });
    });
  });
});
