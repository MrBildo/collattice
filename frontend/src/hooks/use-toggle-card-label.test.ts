import { describe, test, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, createElement } from 'react';
import { useToggleCardLabel } from './use-toggle-card-label';
import { queryKeys } from '@/lib/query-keys';
import type { BoardData, CardLabelSummary, CardSummary } from '@/types';

vi.mock('@/lib/api', () => ({
  addCardLabel: vi.fn(),
  removeCardLabel: vi.fn(),
}));

import { addCardLabel, removeCardLabel } from '@/lib/api';

const mockAddCardLabel = vi.mocked(addCardLabel);
const mockRemoveCardLabel = vi.mocked(removeCardLabel);

const BOARD_ID = 'board-1';
const CARD_ID = 'card-1';
const BUG: CardLabelSummary = { id: 'label-bug', name: 'Bug', color: '#ff5533' };
const FEATURE: CardLabelSummary = { id: 'label-feature', name: 'Feature', color: '#66dd33' };
const DOCS: CardLabelSummary = { id: 'label-docs', name: 'Docs', color: '#22aaaa' };

function makeCard(id: string, labels: CardLabelSummary[]): CardSummary {
  return {
    id,
    number: 1,
    name: `card-${id}`,
    descriptionMarkdown: '',
    sizeId: 'size-1',
    sizeName: 'M',
    laneId: 'lane-1',
    position: 0,
    isArchived: false,
    createdByUserId: 'u1',
    createdAtUtc: '2026-09-29T00:00:00Z',
    lastUpdatedByUserId: 'u1',
    lastUpdatedAtUtc: '2026-09-29T00:00:00Z',
    labels,
    commentCount: 0,
    attachmentCount: 0,
  };
}

function setup(labels: CardLabelSummary[]) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  const board: BoardData = {
    lanes: [],
    sizes: [],
    cards: [makeCard(CARD_ID, labels), makeCard('card-2', [BUG])],
  };
  queryClient.setQueryData(queryKeys.boards.data(BOARD_ID), board);
  function Wrapper({ children }: { children: React.ReactNode }) {
    return createElement(QueryClientProvider, { client: queryClient }, children);
  }
  const hook = renderHook(() => useToggleCardLabel({ cardId: CARD_ID, boardId: BOARD_ID }), {
    wrapper: Wrapper,
  });
  return { queryClient, hook };
}

function readLabelIds(queryClient: QueryClient, cardId: string): string[] {
  const data = queryClient.getQueryData<BoardData>(queryKeys.boards.data(BOARD_ID));
  return (data?.cards.find((c) => c.id === cardId)?.labels ?? []).map((l) => l.id);
}

function createDeferred() {
  let resolve!: () => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<void>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe('useToggleCardLabel', () => {
  test('adding a label calls the add endpoint and shows the label on the tile before the save lands', async () => {
    // Arrange
    const save = createDeferred();
    mockAddCardLabel.mockReturnValue(save.promise);
    const { queryClient, hook } = setup([BUG]);

    // Act
    act(() => hook.result.current.mutate({ label: FEATURE, isAssigned: false }));

    // Assert
    await waitFor(() =>
      expect(readLabelIds(queryClient, CARD_ID)).toEqual(['label-bug', 'label-feature']),
    );
    expect(mockAddCardLabel).toHaveBeenCalledWith(CARD_ID, 'label-feature');
    expect(mockRemoveCardLabel).not.toHaveBeenCalled();
    expect(readLabelIds(queryClient, 'card-2')).toEqual(['label-bug']);

    await act(async () => save.resolve());
  });

  test('removing a label calls the remove endpoint and drops it from the tile', async () => {
    // Arrange
    mockRemoveCardLabel.mockResolvedValue(undefined);
    const { queryClient, hook } = setup([BUG, FEATURE]);

    // Act
    act(() => hook.result.current.mutate({ label: BUG, isAssigned: true }));

    // Assert
    await waitFor(() => expect(readLabelIds(queryClient, CARD_ID)).toEqual(['label-feature']));
    expect(mockRemoveCardLabel).toHaveBeenCalledWith(CARD_ID, 'label-bug');
    expect(mockAddCardLabel).not.toHaveBeenCalled();
  });

  test('a failed toggle reverts only its own label, leaving a concurrent toggle in place', async () => {
    // Arrange
    const failing = createDeferred();
    const succeeding = createDeferred();
    mockAddCardLabel.mockImplementation((_cardId, labelId) =>
      labelId === FEATURE.id ? failing.promise : succeeding.promise,
    );
    const { queryClient, hook } = setup([BUG]);

    // Act
    act(() => hook.result.current.mutate({ label: FEATURE, isAssigned: false }));
    act(() => hook.result.current.mutate({ label: DOCS, isAssigned: false }));
    await waitFor(() =>
      expect(readLabelIds(queryClient, CARD_ID)).toEqual([
        'label-bug',
        'label-feature',
        'label-docs',
      ]),
    );
    await act(async () => failing.reject(new Error('400')));

    // Assert
    await waitFor(() =>
      expect(readLabelIds(queryClient, CARD_ID)).toEqual(['label-bug', 'label-docs']),
    );

    await act(async () => succeeding.resolve());
  });

  test('the board and the card label list refetch once, after the last in-flight toggle settles', async () => {
    // Arrange
    const first = createDeferred();
    const second = createDeferred();
    mockAddCardLabel.mockImplementation((_cardId, labelId) =>
      labelId === FEATURE.id ? first.promise : second.promise,
    );
    const { queryClient, hook } = setup([]);
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    // Act
    act(() => hook.result.current.mutate({ label: FEATURE, isAssigned: false }));
    act(() => hook.result.current.mutate({ label: DOCS, isAssigned: false }));
    await waitFor(() => expect(mockAddCardLabel).toHaveBeenCalledTimes(2));
    await act(async () => first.resolve());

    // Assert — one toggle is still in flight, so nothing refetches yet
    expect(invalidateSpy).not.toHaveBeenCalled();

    await act(async () => second.resolve());
    await waitFor(() =>
      expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: queryKeys.boards.data(BOARD_ID) }),
    );
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: queryKeys.cards.labels(CARD_ID) });
    expect(invalidateSpy).toHaveBeenCalledTimes(2);
  });
});
