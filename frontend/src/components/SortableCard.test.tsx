import { describe, test, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import { DndContext, MouseSensor, useSensor, useSensors } from '@dnd-kit/core';
import { SortableContext } from '@dnd-kit/sortable';
import { SortableCard } from './SortableCard';
import { addCardLabel, fetchLabels, removeCardLabel } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';
import type { BoardData, CardItem, CardLabelSummary, CardSummary, Label } from '@/types';

vi.mock('@/lib/api', () => ({
  addCardLabel: vi.fn(),
  removeCardLabel: vi.fn(),
  fetchLabels: vi.fn(),
}));

const mockAddCardLabel = vi.mocked(addCardLabel);
const mockRemoveCardLabel = vi.mocked(removeCardLabel);
const mockFetchLabels = vi.mocked(fetchLabels);

const BOARD_ID = 'board-1';
const CARD_ID = 'card-1';

const BOARD_LABELS: Label[] = [
  { id: 'label-bug', boardId: BOARD_ID, name: 'Bug', color: '#ff5533' },
  { id: 'label-feature', boardId: BOARD_ID, name: 'Feature', color: '#66dd33' },
];

function makeCardItem(overrides: Partial<CardItem> = {}): CardItem {
  return {
    id: CARD_ID,
    number: 7,
    name: 'Tile under test',
    descriptionMarkdown: '',
    laneId: 'lane-1',
    position: 0,
    sizeId: 'size-1',
    isArchived: false,
    createdByUserId: 'u1',
    createdAtUtc: '2026-09-29T00:00:00Z',
    lastUpdatedByUserId: 'u1',
    lastUpdatedAtUtc: '2026-09-29T00:00:00Z',
    ...overrides,
  };
}

function makeSummary(labels: CardLabelSummary[], isArchived = false): CardSummary {
  return {
    ...makeCardItem({ isArchived }),
    sizeName: 'M',
    labels,
    commentCount: 0,
    attachmentCount: 0,
  };
}

// Reads the tile's enriched data from the board cache, as the real board does,
// so an optimistic label change is visible on the rendered tile.
function BoardTile({
  onCardClick,
  isArchived,
}: {
  onCardClick: (card: CardItem) => void;
  isArchived: boolean;
}) {
  const { data } = useQuery<BoardData>({
    queryKey: queryKeys.boards.data(BOARD_ID),
    queryFn: () => Promise.reject(new Error('the test seeds the cache')),
    enabled: false,
  });
  // The board's own mouse sensor: a press only becomes a drag after 8px of
  // movement, so a plain click still reaches the tile's click handler.
  const sensors = useSensors(useSensor(MouseSensor, { activationConstraint: { distance: 8 } }));
  const card = makeCardItem({ isArchived });
  return (
    <DndContext sensors={sensors}>
      <SortableContext items={[CARD_ID]}>
        <SortableCard
          card={card}
          boardId={BOARD_ID}
          onCardClick={onCardClick}
          isDragging={false}
          sizeMap={new Map([['size-1', 'M']])}
          enrichedData={data?.cards.find((c) => c.id === CARD_ID)}
        />
      </SortableContext>
    </DndContext>
  );
}

function setup({
  labels = [],
  isArchived = false,
}: { labels?: CardLabelSummary[]; isArchived?: boolean } = {}) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  queryClient.setQueryData<BoardData>(queryKeys.boards.data(BOARD_ID), {
    lanes: [],
    sizes: [],
    cards: [makeSummary(labels, isArchived)],
  });
  const onCardClick = vi.fn();
  const onParentMouseDown = vi.fn();
  render(
    <QueryClientProvider client={queryClient}>
      {/* Stands in for the drag listeners, which sit on the tile and receive
          every mouse press that bubbles up to it. */}
      <div onMouseDown={onParentMouseDown}>
        <BoardTile onCardClick={onCardClick} isArchived={isArchived} />
      </div>
    </QueryClientProvider>,
  );
  return { onCardClick, onParentMouseDown };
}

beforeEach(() => {
  vi.clearAllMocks();
  mockFetchLabels.mockResolvedValue(BOARD_LABELS);
  mockAddCardLabel.mockResolvedValue(undefined);
  mockRemoveCardLabel.mockResolvedValue(undefined);
});

describe('SortableCard label picker', () => {
  test('the tile offers a label picker named for its card', () => {
    setup();

    expect(screen.getByRole('button', { name: 'Edit labels on card #7' })).toBeInTheDocument();
  });

  test('opening the picker lists the board labels without opening the card', async () => {
    // Arrange
    const user = userEvent.setup();
    const { onCardClick } = setup({ labels: [BOARD_LABELS[0]] });

    // Act
    await user.click(screen.getByRole('button', { name: 'Edit labels on card #7' }));

    // Assert
    const bug = await screen.findByRole('option', { name: 'Bug' });
    expect(bug).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('option', { name: 'Feature' })).toHaveAttribute(
      'aria-selected',
      'false',
    );
    expect(onCardClick).not.toHaveBeenCalled();
  });

  test('toggling labels saves each one immediately and updates the tile in place', async () => {
    // Arrange
    const user = userEvent.setup();
    const { onCardClick } = setup({ labels: [BOARD_LABELS[0]] });
    await user.click(screen.getByRole('button', { name: 'Edit labels on card #7' }));

    // Act
    await user.click(await screen.findByRole('option', { name: 'Feature' }));
    await user.click(screen.getByRole('option', { name: 'Bug' }));

    // Assert
    expect(mockAddCardLabel).toHaveBeenCalledWith(CARD_ID, 'label-feature');
    expect(mockRemoveCardLabel).toHaveBeenCalledWith(CARD_ID, 'label-bug');
    await waitFor(() =>
      expect(screen.getByRole('option', { name: 'Feature' })).toHaveAttribute(
        'aria-selected',
        'true',
      ),
    );
    expect(screen.getByRole('option', { name: 'Bug' })).toHaveAttribute('aria-selected', 'false');
    expect(onCardClick).not.toHaveBeenCalled();
  });

  test('a press on the picker trigger does not reach the tile drag listeners', () => {
    const { onParentMouseDown } = setup();

    fireEvent.mouseDown(screen.getByRole('button', { name: 'Edit labels on card #7' }));

    expect(onParentMouseDown).not.toHaveBeenCalled();
  });

  test('a click and a press on the tile itself still open the card and reach the drag listeners', async () => {
    // Arrange
    const user = userEvent.setup();
    const { onCardClick, onParentMouseDown } = setup();

    // Act
    await user.click(screen.getByText('Tile under test'));

    // Assert
    expect(onCardClick).toHaveBeenCalledTimes(1);
    expect(onParentMouseDown).toHaveBeenCalled();
  });

  test('an archived card has no label picker', () => {
    setup({ labels: [BOARD_LABELS[0]], isArchived: true });

    expect(
      screen.queryByRole('button', { name: 'Edit labels on card #7' }),
    ).not.toBeInTheDocument();
  });
});
