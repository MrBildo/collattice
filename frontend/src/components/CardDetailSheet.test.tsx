import { describe, test, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { CardDetailSheet } from './CardDetailSheet';
import {
  fetchBoardData,
  fetchCardHistory,
  fetchCardLabels,
  fetchLabels,
  fetchUserDirectory,
  updateCard,
} from '@/lib/api';
import { ROLES } from '@/lib/roles';
import type { CardItem } from '@/types';

// Duplicating a card with unsaved edits routes through the same unsaved-changes
// guard as closing or navigating away, so the edits are never lost silently —
// and the user's choice decides which version the copy starts from.

vi.mock('@/lib/api', () => ({
  updateCard: vi.fn(),
  deleteCard: vi.fn(),
  uploadAttachment: vi.fn(),
  archiveCard: vi.fn(),
  restoreCard: vi.fn(),
  fetchCardLabels: vi.fn(),
  fetchLabels: vi.fn(),
  fetchCardHistory: vi.fn(),
  fetchUserDirectory: vi.fn(),
  fetchBoardData: vi.fn(),
}));

vi.mock('./CardComments', () => ({ CardComments: () => null }));
vi.mock('./CardAttachments', () => ({ CardAttachments: () => null }));
vi.mock('./CardDescriptionHistory', () => ({ CardDescriptionHistory: () => null }));
vi.mock('mermaid', () => ({ default: { initialize: vi.fn(), render: vi.fn() } }));

const lanes = [{ id: 'lane-1', boardId: 'board-1', name: 'Backlog', position: 0 }];

function makeCard(overrides: Partial<CardItem> = {}): CardItem {
  return {
    id: 'card-1',
    number: 7,
    name: 'Original name',
    descriptionMarkdown: 'Original description',
    laneId: 'lane-1',
    position: 0,
    sizeId: 'size-1',
    isArchived: false,
    createdByUserId: 'me',
    createdAtUtc: '2026-08-01T10:00:00.000Z',
    lastUpdatedByUserId: 'me',
    lastUpdatedAtUtc: '2026-08-01T10:00:00.000Z',
    ...overrides,
  };
}

function makeSavedCard(name: string) {
  return { ...makeCard({ name }), labels: [], sizeName: 'M', commentCount: 0, attachmentCount: 0 };
}

function setup() {
  const onDuplicate = vi.fn();
  const onOpenChange = vi.fn();
  const card = makeCard();
  // A stable lane list: the sheet snapshots it for prev/next navigation, and a
  // fresh array on every render would never settle.
  const cardsInLane = [card];
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MemoryRouter>
      <QueryClientProvider client={queryClient}>
        <CardDetailSheet
          card={card}
          cardsInLane={cardsInLane}
          open
          onOpenChange={onOpenChange}
          currentUserId="me"
          currentUserRole={ROLES.Human}
          lanes={lanes}
          boardId="board-1"
          onDuplicate={onDuplicate}
        />
      </QueryClientProvider>
    </MemoryRouter>,
  );
  return { onDuplicate, onOpenChange };
}

async function editNameThenDuplicate(user: ReturnType<typeof userEvent.setup>) {
  const nameInput = await screen.findByDisplayValue('Original name');
  await user.type(nameInput, ' v2');
  const duplicate = screen.getByRole('button', { name: 'Duplicate' });
  await waitFor(() => expect(duplicate).toBeEnabled());
  await user.click(duplicate);
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(fetchCardLabels).mockResolvedValue([]);
  vi.mocked(fetchLabels).mockResolvedValue([]);
  vi.mocked(fetchCardHistory).mockResolvedValue({
    cardId: 'card-1',
    field: 'description',
    entries: [],
    totalCount: 0,
    offset: 0,
    limit: 1,
  });
  vi.mocked(fetchBoardData).mockResolvedValue({ lanes: [], cards: [], sizes: [] });
  vi.mocked(fetchUserDirectory).mockResolvedValue([{ id: 'me', name: 'Me' }]);
});

describe('CardDetailSheet duplicate', () => {
  test('with no unsaved edits the copy opens straight away', async () => {
    const user = userEvent.setup();
    const { onDuplicate } = setup();

    const duplicate = await screen.findByRole('button', { name: 'Duplicate' });
    await waitFor(() => expect(duplicate).toBeEnabled());
    await user.click(duplicate);

    expect(onDuplicate).toHaveBeenCalledTimes(1);
    expect(onDuplicate.mock.calls[0][0].name).toBe('Original name');
    expect(screen.queryByText('Unsaved Changes')).not.toBeInTheDocument();
  });

  test('unsaved edits ask first, and discarding them copies the card as stored', async () => {
    const user = userEvent.setup();
    const { onDuplicate } = setup();

    await editNameThenDuplicate(user);
    expect(onDuplicate).not.toHaveBeenCalled();
    expect(await screen.findByText('Unsaved Changes')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Discard & Duplicate' }));

    expect(onDuplicate).toHaveBeenCalledTimes(1);
    expect(onDuplicate.mock.calls[0][0].name).toBe('Original name');
    expect(updateCard).not.toHaveBeenCalled();
  });

  test('saving the edits first stores them and the copy includes them', async () => {
    const user = userEvent.setup();
    vi.mocked(updateCard).mockResolvedValue(makeSavedCard('Original name v2'));
    const { onDuplicate } = setup();

    await editNameThenDuplicate(user);
    await user.click(await screen.findByRole('button', { name: 'Save & Duplicate' }));

    await waitFor(() => expect(onDuplicate).toHaveBeenCalledTimes(1));
    expect(updateCard).toHaveBeenCalledWith('card-1', { name: 'Original name v2' });
    expect(onDuplicate.mock.calls[0][0].name).toBe('Original name v2');
  });

  test('Keep Editing cancels the duplicate and leaves the edit in place', async () => {
    const user = userEvent.setup();
    const { onDuplicate } = setup();

    await editNameThenDuplicate(user);
    await user.click(await screen.findByRole('button', { name: 'Keep Editing' }));

    expect(onDuplicate).not.toHaveBeenCalled();
    expect(screen.getByDisplayValue('Original name v2')).toBeInTheDocument();
  });

  test('a failed save, then a manual save, opens nothing', async () => {
    // Arrange
    const user = userEvent.setup();
    vi.mocked(updateCard)
      .mockRejectedValueOnce(new Error('The save failed'))
      .mockResolvedValueOnce(makeSavedCard('Original name v2 fixed'));
    const { onDuplicate } = setup();

    // Act
    await editNameThenDuplicate(user);
    await user.click(await screen.findByRole('button', { name: 'Save & Duplicate' }));
    expect(await screen.findByText('The save failed')).toBeInTheDocument();
    await user.type(screen.getByDisplayValue('Original name v2'), ' fixed');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // Assert
    expect(await screen.findByText('Saved')).toBeInTheDocument();
    expect(updateCard).toHaveBeenCalledTimes(2);
    expect(onDuplicate).not.toHaveBeenCalled();
  });
});

describe('CardDetailSheet save failure', () => {
  test('a failed Save & Close, then a manual save, leaves the card open', async () => {
    // Arrange
    const user = userEvent.setup();
    vi.mocked(updateCard)
      .mockRejectedValueOnce(new Error('The save failed'))
      .mockResolvedValueOnce(makeSavedCard('Original name v2 fixed'));
    const { onOpenChange } = setup();
    await user.type(await screen.findByDisplayValue('Original name'), ' v2');

    // Act — the footer's Close; the dialog's corner X reaches the same guard
    await user.click(screen.getAllByRole('button', { name: 'Close' })[0]);
    await user.click(await screen.findByRole('button', { name: 'Save & Close' }));
    expect(await screen.findByText('The save failed')).toBeInTheDocument();
    await user.type(screen.getByDisplayValue('Original name v2'), ' fixed');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // Assert
    expect(await screen.findByText('Saved')).toBeInTheDocument();
    expect(updateCard).toHaveBeenCalledTimes(2);
    expect(onOpenChange).not.toHaveBeenCalled();
  });
});
