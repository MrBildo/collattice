import { describe, test, expect, vi, beforeEach } from 'vitest';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { CardDetailForm } from './CardDetailForm';
import { Dialog, DialogContent } from '@/components/ui/dialog';
import {
  fetchBoardData,
  fetchCardHistory,
  fetchCardLabels,
  fetchLabels,
  fetchUserDirectory,
  updateCard,
} from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';
import { ROLES } from '@/lib/roles';
import type { BoardData, CardHistoryTrail, CardItem, CardSummary, Label, Lane } from '@/types';

// This suite covers the concurrent-edit guard: an edit another person makes
// while you have the card open surfaces as a named, reachable warning without
// blocking your save. What jsdom can prove — the warning renders, names the
// actor, stays reachable by role/name, and the accept flow works — lives here.
// The dark-mode contrast of the amber banner is a build-transformed-CSS
// property jsdom cannot see; that is verified in a real browser against the
// production build.

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

// The comments, attachments and history panels have their own suites; keep them
// inert so the mount stays focused on the guard. Mermaid is mocked for the same
// reason the history suite does — the preview renders through MarkdownRenderer.
vi.mock('./CardComments', () => ({ CardComments: () => null }));
vi.mock('./CardAttachments', () => ({ CardAttachments: () => null }));
vi.mock('./CardDescriptionHistory', () => ({ CardDescriptionHistory: () => null }));
vi.mock('mermaid', () => ({ default: { initialize: vi.fn(), render: vi.fn() } }));

const emptyTrail: CardHistoryTrail = {
  cardId: 'card-1',
  field: 'description',
  entries: [],
  totalCount: 0,
  offset: 0,
  limit: 1,
};

const emptyBoard: BoardData = { lanes: [], cards: [], sizes: [] };

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

function setup(initialCard: CardItem, lanes?: Lane[]) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const isDirtyRef = { current: false };
  const tree = (card: CardItem) => (
    <MemoryRouter>
      <QueryClientProvider client={queryClient}>
        <Dialog open onOpenChange={() => {}}>
          <DialogContent>
            <CardDetailForm
              card={card}
              lanes={lanes}
              onClose={() => {}}
              currentUserId="me"
              currentUserRole={ROLES.Human}
              boardId="board-1"
              isDirtyRef={isDirtyRef}
            />
          </DialogContent>
        </Dialog>
      </QueryClientProvider>
    </MemoryRouter>
  );
  const utils = render(tree(initialCard));
  // Simulate an SSE-driven refetch handing the form a fresh card prop.
  const sendRemote = (card: CardItem) => utils.rerender(tree(card));
  return { ...utils, isDirtyRef, sendRemote, queryClient };
}

function toSummary(card: CardItem): CardSummary {
  return { ...card, sizeName: 'S', labels: [], commentCount: 0, attachmentCount: 0 };
}

async function editDescription(user: ReturnType<typeof userEvent.setup>, text: string) {
  await user.click(screen.getByRole('button', { name: 'Edit' }));
  await user.type(screen.getByPlaceholderText('Write a description...'), text);
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(fetchCardLabels).mockResolvedValue([]);
  vi.mocked(fetchLabels).mockResolvedValue([]);
  vi.mocked(fetchCardHistory).mockResolvedValue(emptyTrail);
  vi.mocked(fetchBoardData).mockResolvedValue(emptyBoard);
  vi.mocked(fetchUserDirectory).mockResolvedValue([
    { id: 'me', name: 'Me' },
    { id: 'marcus', name: 'Marcus' },
    { id: 'nina', name: 'Nina' },
  ]);
});

describe('CardDetailForm concurrent-edit guard', () => {
  test('a field the user has not touched updates silently, with no collision warning', async () => {
    const { sendRemote } = setup(makeCard({ descriptionMarkdown: 'Original description' }));
    await screen.findByDisplayValue('Original name');

    sendRemote(
      makeCard({ descriptionMarkdown: 'Updated remotely', lastUpdatedByUserId: 'marcus' }),
    );

    // The preview reflects the remote value without any warning.
    expect(await screen.findByText('Updated remotely')).toBeInTheDocument();
    expect(screen.queryByText(/changed the/)).not.toBeInTheDocument();
  });

  test('a field the user is editing surfaces a warning that names the actor and the field', async () => {
    const user = userEvent.setup();
    const { sendRemote } = setup(makeCard());
    await screen.findByDisplayValue('Original name');

    await editDescription(user, 'my local draft');
    sendRemote(makeCard({ descriptionMarkdown: 'Marcus edit', lastUpdatedByUserId: 'marcus' }));

    expect(await screen.findByText('Marcus changed the description')).toBeInTheDocument();
    // The per-field control is reachable by role and name — not a hover-only span.
    expect(
      screen.getByRole('button', { name: /Description changed by Marcus/ }),
    ).toBeInTheDocument();
  });

  test('the warning does not block saving — Save stays enabled through a collision', async () => {
    const user = userEvent.setup();
    const { sendRemote } = setup(makeCard());
    await screen.findByDisplayValue('Original name');

    await editDescription(user, 'my local draft');
    sendRemote(makeCard({ descriptionMarkdown: 'Marcus edit', lastUpdatedByUserId: 'marcus' }));

    await screen.findByText('Marcus changed the description');
    expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled();
  });

  test('Accept takes the remote value and clears the warning', async () => {
    const user = userEvent.setup();
    const { sendRemote } = setup(makeCard());
    await screen.findByDisplayValue('Original name');

    await editDescription(user, 'my local draft');
    sendRemote(makeCard({ descriptionMarkdown: 'Marcus edit', lastUpdatedByUserId: 'marcus' }));
    await screen.findByText('Marcus changed the description');

    await user.click(screen.getByRole('button', { name: 'Accept their version' }));

    expect(screen.getByPlaceholderText('Write a description...')).toHaveValue('Marcus edit');
    await waitFor(() =>
      expect(screen.queryByText('Marcus changed the description')).not.toBeInTheDocument(),
    );
  });

  test('the per-field indicator opens a reachable popover showing the value and an accept action', async () => {
    const user = userEvent.setup();
    const { sendRemote } = setup(makeCard());
    await screen.findByDisplayValue('Original name');

    await editDescription(user, 'x');
    sendRemote(
      makeCard({ descriptionMarkdown: 'Marcus full text', lastUpdatedByUserId: 'marcus' }),
    );

    const trigger = await screen.findByRole('button', { name: /Description changed by Marcus/ });
    await user.click(trigger);

    const value = await screen.findByText('Marcus full text');
    const popover = value.closest('[data-slot="popover-content"]') as HTMLElement;
    expect(popover).not.toBeNull();
    await user.click(within(popover).getByRole('button', { name: /Accept their version/ }));

    expect(screen.getByPlaceholderText('Write a description...')).toHaveValue('Marcus full text');
  });

  test('two fields changed by the same person read as a count, not one name', async () => {
    const user = userEvent.setup();
    const { sendRemote } = setup(makeCard());
    const nameInput = await screen.findByDisplayValue('Original name');

    await user.type(nameInput, '!');
    await editDescription(user, 'x');
    sendRemote(
      makeCard({
        name: 'Remote name',
        descriptionMarkdown: 'Remote desc',
        lastUpdatedByUserId: 'marcus',
      }),
    );

    expect(await screen.findByText('Marcus changed 2 fields')).toBeInTheDocument();
  });

  test('each field stays attributed to the person who changed it across separate updates', async () => {
    const user = userEvent.setup();
    const { sendRemote } = setup(makeCard());
    const nameInput = await screen.findByDisplayValue('Original name');

    await user.type(nameInput, '!');
    await editDescription(user, 'x');

    // Marcus changes the description first.
    sendRemote(
      makeCard({
        name: 'Original name',
        descriptionMarkdown: 'Marcus desc',
        lastUpdatedByUserId: 'marcus',
      }),
    );
    await screen.findByRole('button', { name: /Description changed by Marcus/ });

    // Nina then changes the name; the description must not be reassigned to Nina.
    sendRemote(
      makeCard({
        name: 'Nina name',
        descriptionMarkdown: 'Marcus desc',
        lastUpdatedByUserId: 'nina',
      }),
    );

    expect(await screen.findByRole('button', { name: /Name changed by Nina/ })).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: /Description changed by Marcus/ }),
    ).toBeInTheDocument();
    expect(screen.getByText('2 fields changed externally')).toBeInTheDocument();
  });
});

describe('CardDetailForm duplicate', () => {
  const lanes = [
    { id: 'lane-1', boardId: 'board-1', name: 'Backlog', position: 0 },
    { id: 'lane-2', boardId: 'board-1', name: 'Doing', position: 1 },
  ];
  const bug = { id: 'label-bug', boardId: 'board-1', name: 'Bug', color: '#f00' };

  function setupDuplicate(card: CardItem, onDuplicate = vi.fn()) {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    render(
      <MemoryRouter>
        <QueryClientProvider client={queryClient}>
          <Dialog open onOpenChange={() => {}}>
            <DialogContent>
              <CardDetailForm
                card={card}
                onClose={() => {}}
                currentUserId="me"
                currentUserRole={ROLES.Human}
                boardId="board-1"
                lanes={lanes}
                isDirtyRef={{ current: false }}
                onDuplicate={onDuplicate}
              />
            </DialogContent>
          </Dialog>
        </QueryClientProvider>
      </MemoryRouter>,
    );
    return { onDuplicate, queryClient };
  }

  test('offers the source card as both draft and saved when nothing is edited', async () => {
    const user = userEvent.setup();
    vi.mocked(fetchCardLabels).mockResolvedValue([bug]);
    const { onDuplicate } = setupDuplicate(makeCard({ laneId: 'lane-2' }));

    const button = await screen.findByRole('button', { name: 'Duplicate' });
    await waitFor(() => expect(button).toBeEnabled());
    await user.click(button);

    const expected = {
      name: 'Original name',
      descriptionMarkdown: 'Original description',
      sizeId: 'size-1',
      labelIds: ['label-bug'],
      laneId: 'lane-2',
      source: { id: 'card-1', number: 7, isArchived: false },
    };
    expect(onDuplicate).toHaveBeenCalledWith({ draft: expected, saved: expected });
  });

  test('an unsaved edit rides in the draft while the saved reading keeps the stored card', async () => {
    const user = userEvent.setup();
    const { onDuplicate } = setupDuplicate(makeCard());
    const nameInput = await screen.findByDisplayValue('Original name');

    await user.type(nameInput, ' v2');
    const button = screen.getByRole('button', { name: 'Duplicate' });
    await waitFor(() => expect(button).toBeEnabled());
    await user.click(button);

    const request = onDuplicate.mock.calls[0][0];
    expect(request.draft.name).toBe('Original name v2');
    expect(request.saved.name).toBe('Original name');
  });

  test('an archived card can be duplicated, and the copy has no lane chosen for it', async () => {
    const user = userEvent.setup();
    const { onDuplicate } = setupDuplicate(makeCard({ isArchived: true, laneId: 'lane-archive' }));

    // Archived cards keep Restore as the primary action; Duplicate sits beside it.
    expect(await screen.findByRole('button', { name: 'Restore' })).toBeInTheDocument();
    const button = screen.getByRole('button', { name: 'Duplicate' });
    await waitFor(() => expect(button).toBeEnabled());
    await user.click(button);

    const request = onDuplicate.mock.calls[0][0];
    expect(request.draft.laneId).toBe('');
    expect(request.saved.laneId).toBe('');
    expect(request.saved.source).toEqual({ id: 'card-1', number: 7, isArchived: true });
  });

  test('Duplicate waits for the card labels so a copy never silently drops them', async () => {
    vi.mocked(fetchCardLabels).mockReturnValue(new Promise(() => {}));
    setupDuplicate(makeCard());

    expect(await screen.findByRole('button', { name: 'Duplicate' })).toBeDisabled();
  });

  test('a failed refresh of labels already loaded keeps the picker and Duplicate, with no error', async () => {
    // Arrange
    const user = userEvent.setup();
    vi.mocked(fetchCardLabels).mockResolvedValueOnce([bug]);
    const { onDuplicate, queryClient } = setupDuplicate(makeCard());
    const button = await screen.findByRole('button', { name: 'Duplicate' });
    await waitFor(() => expect(button).toBeEnabled());
    vi.mocked(fetchCardLabels).mockRejectedValue(new Error('network down'));

    // Act — the refetch fails, and so does its one retry
    await act(async () => {
      await queryClient.refetchQueries({ queryKey: queryKeys.cards.labels('card-1') });
    });
    await waitFor(() => expect(fetchCardLabels).toHaveBeenCalledTimes(3), { timeout: 3000 });

    // Assert
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(button).toBeEnabled();
    expect(button).not.toHaveAttribute('aria-describedby');
    expect(screen.getByRole('button', { name: /Labels|Bug/ })).toBeInTheDocument();
    await user.click(button);
    expect(onDuplicate.mock.calls[0][0].saved.labelIds).toEqual(['label-bug']);
  });

  test('when the card labels fail to load, the dialog says why Duplicate is off and can retry', async () => {
    // Arrange
    const user = userEvent.setup();
    // Twice: the labels query retries once on its own before it reports an error.
    vi.mocked(fetchCardLabels)
      .mockRejectedValueOnce(new Error('network down'))
      .mockRejectedValueOnce(new Error('network down'))
      .mockResolvedValue([bug]);
    const { onDuplicate } = setupDuplicate(makeCard());

    // Act
    const alert = await screen.findByRole('alert', undefined, { timeout: 3000 });

    // Assert — the reason is visible, tied to the button, and the picker that
    // would start from no labels is not offered
    expect(alert).toHaveTextContent(
      "Couldn't load this card's labels, so they can't be edited and the card can't be duplicated.",
    );
    const button = screen.getByRole('button', { name: 'Duplicate' });
    expect(button).toBeDisabled();
    expect(button).toHaveAccessibleDescription(
      "Couldn't load this card's labels, so they can't be edited and the card can't be duplicated.",
    );
    expect(screen.queryByRole('button', { name: 'Labels' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(button).toBeEnabled());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    await user.click(button);
    expect(onDuplicate.mock.calls[0][0].saved.labelIds).toEqual(['label-bug']);
  });
});

describe('CardDetailForm labels', () => {
  const bug: Label = { id: 'label-bug', boardId: 'board-1', name: 'Bug', color: '#f00' };
  const feature: Label = {
    id: 'label-feature',
    boardId: 'board-1',
    name: 'Feature',
    color: '#0f0',
  };
  const chore: Label = { id: 'label-chore', boardId: 'board-1', name: 'Chore', color: '#888' };

  test('the picker waits for the card labels, so a save keeps the labels the card has', async () => {
    // Arrange — hold the card's labels request open
    const user = userEvent.setup();
    let resolveCardLabels: (labels: Label[]) => void = () => {};
    vi.mocked(fetchCardLabels).mockReturnValue(
      new Promise<Label[]>((resolve) => {
        resolveCardLabels = resolve;
      }),
    );
    vi.mocked(fetchLabels).mockResolvedValue([bug, feature, chore]);
    // Only the patch the form sends matters here; the response just needs to resolve.
    vi.mocked(updateCard).mockResolvedValue(makeCard() as unknown as CardSummary);
    setup(makeCard());

    // Assert — while the request is open there is nothing to pick from
    const loading = await screen.findByRole('button', { name: 'Loading labels...' });
    expect(loading).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Labels' })).not.toBeInTheDocument();
    await user.click(loading);
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();

    // Act — the labels arrive, then one more is picked and the card saved
    await act(async () => {
      resolveCardLabels([bug, feature]);
    });
    await user.click(await screen.findByRole('button', { name: /Bug/ }));
    await user.click(await screen.findByRole('option', { name: 'Chore' }));
    // The card's own labels arriving is not someone else's edit.
    expect(screen.queryByText(/changed the/)).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // Assert — the save adds to the labels the card had instead of replacing them
    await waitFor(() => expect(updateCard).toHaveBeenCalledTimes(1));
    expect(vi.mocked(updateCard).mock.calls[0][1]).toEqual({
      labelIds: ['label-bug', 'label-feature', 'label-chore'],
    });
  });
});

describe('CardDetailForm lane change', () => {
  const lanes: Lane[] = [
    { id: 'lane-1', boardId: 'board-1', name: 'Backlog', position: 0 },
    { id: 'lane-2', boardId: 'board-1', name: 'Done', position: 1 },
  ];
  const boardKey = queryKeys.boards.data('board-1');

  // The order the board renders a lane in: its cards sorted by position.
  function cachedLaneOrder(queryClient: QueryClient, laneId: string): string[] {
    const data = queryClient.getQueryData<BoardData>(boardKey);
    return (data?.cards ?? [])
      .filter((c) => c.laneId === laneId)
      .sort((a, b) => a.position - b.position)
      .map((c) => c.name);
  }

  // Done holds P at 0 and R at 40 in the cache: cards deleted between them left a gap.
  function seedBoard(queryClient: QueryClient, moving: CardItem) {
    queryClient.setQueryData<BoardData>(boardKey, {
      lanes,
      sizes: [],
      cards: [
        toSummary(makeCard({ id: 'p', number: 5, name: 'P', laneId: 'lane-2', position: 0 })),
        toSummary(makeCard({ id: 'r', number: 9, name: 'R', laneId: 'lane-2', position: 40 })),
        toSummary(moving),
      ],
    });
  }

  // Every board fetch is held open until the test releases it, so the cache can be read in the
  // window between the save and the refetch landing, which is where the wrong order showed.
  function holdBoardFetches() {
    const pending: Array<(data: BoardData) => void> = [];
    vi.mocked(fetchBoardData).mockImplementation(
      () => new Promise<BoardData>((resolve) => pending.push(resolve)),
    );
    return pending;
  }

  test('a card moved through the form shows at the end of its new lane before and after the refetch', async () => {
    // Arrange: the server puts the card at the end of Done and renumbers the lane to P 0, R 10,
    // card 20, but the save response carries only the card's own number.
    const user = userEvent.setup();
    const pending = holdBoardFetches();
    const moving = makeCard({ name: 'Moved' });
    const { queryClient } = setup(moving, lanes);
    seedBoard(queryClient, moving);
    const savedCard = toSummary({ ...moving, laneId: 'lane-2', position: 20 });
    vi.mocked(updateCard).mockResolvedValue(savedCard);

    // Act
    const laneSelect = screen
      .getAllByRole('combobox')
      .find((el) => el.textContent?.includes('Backlog'));
    if (!laneSelect) throw new Error('lane select not found');
    await user.click(laneSelect);
    await user.click(await screen.findByRole('option', { name: 'Done' }));
    const fetchesBeforeSave = pending.length;
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // Assert: before the refetch lands, the card is already last in Done
    await waitFor(() => expect(updateCard).toHaveBeenCalledTimes(1));
    expect(vi.mocked(updateCard).mock.calls[0][1]).toEqual({ laneId: 'lane-2' });
    await waitFor(() =>
      expect(cachedLaneOrder(queryClient, 'lane-2')).toEqual(['P', 'R', 'Moved']),
    );
    expect(pending.length).toBeGreaterThan(fetchesBeforeSave);

    // Act: the refetch lands with the server's renumbered lane
    await act(async () => {
      pending[pending.length - 1]({
        lanes,
        sizes: [],
        cards: [
          toSummary(makeCard({ id: 'p', number: 5, name: 'P', laneId: 'lane-2', position: 0 })),
          toSummary(makeCard({ id: 'r', number: 9, name: 'R', laneId: 'lane-2', position: 10 })),
          savedCard,
        ],
      });
    });

    // Assert: same order, now on the server's numbers
    expect(cachedLaneOrder(queryClient, 'lane-2')).toEqual(['P', 'R', 'Moved']);
    expect(
      queryClient.getQueryData<BoardData>(boardKey)?.cards.find((c) => c.id === 'r')?.position,
    ).toBe(10);
  });

  test('a second save of the moved card before the refetch lands keeps it last in its new lane', async () => {
    // Arrange: move the card into Done (server: P 0, R 10, card 20) with every board fetch held.
    const user = userEvent.setup();
    holdBoardFetches();
    const moving = makeCard({ name: 'Moved' });
    const { queryClient } = setup(moving, lanes);
    seedBoard(queryClient, moving);
    vi.mocked(updateCard).mockResolvedValueOnce(
      toSummary({ ...moving, laneId: 'lane-2', position: 20 }),
    );
    const laneSelect = screen
      .getAllByRole('combobox')
      .find((el) => el.textContent?.includes('Backlog'));
    if (!laneSelect) throw new Error('lane select not found');
    await user.click(laneSelect);
    await user.click(await screen.findByRole('option', { name: 'Done' }));
    await user.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() =>
      expect(cachedLaneOrder(queryClient, 'lane-2')).toEqual(['P', 'R', 'Moved']),
    );

    // Act: rename and save again while the refetch is still held; the response carries the
    // server's 20, which sorts above R's stale cached 40.
    vi.mocked(updateCard).mockResolvedValueOnce(
      toSummary({ ...moving, name: 'Renamed', laneId: 'lane-2', position: 20 }),
    );
    const nameInput = screen.getByDisplayValue('Moved');
    await user.clear(nameInput);
    await user.type(nameInput, 'Renamed');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // Assert
    await waitFor(() => expect(updateCard).toHaveBeenCalledTimes(2));
    expect(vi.mocked(updateCard).mock.calls[1][1]).toEqual({ name: 'Renamed' });
    await waitFor(() =>
      expect(cachedLaneOrder(queryClient, 'lane-2')).toEqual(['P', 'R', 'Renamed']),
    );
  });

  test('a save that keeps the lane does not refetch the board', async () => {
    // Arrange
    const user = userEvent.setup();
    const pending = holdBoardFetches();
    const card = makeCard({ name: 'Original name' });
    const { queryClient } = setup(card, lanes);
    seedBoard(queryClient, card);
    vi.mocked(updateCard).mockResolvedValue(toSummary({ ...card, name: 'Renamed' }));

    // Act
    const nameInput = screen.getByDisplayValue('Original name');
    await user.clear(nameInput);
    await user.type(nameInput, 'Renamed');
    const fetchesBeforeSave = pending.length;
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // Assert
    await waitFor(() => expect(cachedLaneOrder(queryClient, 'lane-1')).toEqual(['Renamed']));
    expect(pending.length).toBe(fetchesBeforeSave);
  });
});
