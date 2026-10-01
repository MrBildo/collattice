import { describe, test, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { useState } from 'react';
import { CreateCardDialog } from './CreateCardDialog';
import { createCard, fetchBoardData, fetchLabels } from '@/lib/api';
import type { CardPrefill } from '@/lib/duplicate-card';

// The dialog opened by Duplicate is the ordinary new-card dialog with starting
// values. Choosing a lane from the picker needs real pointer events that jsdom
// cannot drive on this Select, so the archived-source path is proven up to the
// blank picker and the disabled Create button; picking the lane is demonstrated
// in the running app.

vi.mock('@/lib/api', () => ({
  createCard: vi.fn(),
  createTempCard: vi.fn(),
  cancelTempCard: vi.fn(),
  finalizeCard: vi.fn(),
  uploadAttachment: vi.fn(),
  fetchBoardData: vi.fn(),
  fetchLabels: vi.fn(),
}));

vi.mock('./CardAttachments', () => ({ CardAttachments: () => null }));
vi.mock('mermaid', () => ({ default: { initialize: vi.fn(), render: vi.fn() } }));

const lanes = [
  { id: 'lane-1', boardId: 'board-1', name: 'Backlog', position: 0 },
  { id: 'lane-2', boardId: 'board-1', name: 'Doing', position: 1 },
];
const sizes = [
  { id: 'size-s', boardId: 'board-1', name: 'S', ordinal: 0 },
  { id: 'size-l', boardId: 'board-1', name: 'L', ordinal: 2 },
];
const labels = [
  { id: 'label-bug', boardId: 'board-1', name: 'Bug', color: '#f00' },
  { id: 'label-docs', boardId: 'board-1', name: 'Docs', color: '#0f0' },
];

function makePrefill(overrides: Partial<CardPrefill> = {}): CardPrefill {
  return {
    name: 'Fix login',
    descriptionMarkdown: 'Steps to reproduce',
    sizeId: 'size-l',
    labelIds: ['label-bug'],
    laneId: 'lane-2',
    source: { id: 'card-12', number: 12, isArchived: false },
    ...overrides,
  };
}

function setup(prefill?: CardPrefill) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MemoryRouter>
      <QueryClientProvider client={queryClient}>
        <CreateCardDialog
          boardId="board-1"
          lanes={lanes}
          sizes={sizes}
          open
          onOpenChange={() => {}}
          prefill={prefill}
        />
      </QueryClientProvider>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(fetchLabels).mockResolvedValue(labels);
  vi.mocked(fetchBoardData).mockResolvedValue({ lanes, cards: [], sizes });
});

describe('CreateCardDialog prefilled from a duplicate', () => {
  test('opens titled as a duplicate with the source name, description, size, lane and labels', async () => {
    setup(makePrefill());

    expect(screen.getByRole('heading', { name: 'Duplicate Card' })).toBeInTheDocument();
    expect(screen.getByText(/Starts from #12/)).toBeInTheDocument();
    expect(screen.getByLabelText('Name *')).toHaveValue('Fix login');
    expect(screen.getByPlaceholderText('Write a description...')).toHaveValue('Steps to reproduce');
    expect(screen.getAllByText('L').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Doing').length).toBeGreaterThanOrEqual(1);
    expect(await screen.findByText('Bug')).toBeInTheDocument();
    expect(screen.queryByText('Docs')).not.toBeInTheDocument();
  });

  test('saving creates the card with the edited title and the copied fields', async () => {
    const user = userEvent.setup();
    vi.mocked(createCard).mockResolvedValue({
      id: 'card-new',
      number: 13,
      name: 'Fix login on Safari',
      descriptionMarkdown: 'Steps to reproduce',
      laneId: 'lane-2',
      position: 1,
      sizeId: 'size-l',
      sizeName: 'L',
      labels: [labels[0]],
      commentCount: 0,
      attachmentCount: 0,
      isArchived: false,
      createdByUserId: 'me',
      createdAtUtc: '2026-09-29T10:00:00.000Z',
      lastUpdatedByUserId: 'me',
      lastUpdatedAtUtc: '2026-09-29T10:00:00.000Z',
    });
    setup(makePrefill());
    await screen.findByText('Bug');

    await user.type(screen.getByLabelText('Name *'), ' on Safari');
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(createCard).toHaveBeenCalledTimes(1));
    expect(createCard).toHaveBeenCalledWith('board-1', {
      name: 'Fix login on Safari',
      descriptionMarkdown: 'Steps to reproduce',
      sizeId: 'size-l',
      laneId: 'lane-2',
      position: 1,
      labelIds: ['label-bug'],
    });
  });

  test('a duplicate of an archived card has no lane and cannot be created until one is chosen', async () => {
    setup(makePrefill({ laneId: '', source: { id: 'card-12', number: 12, isArchived: true } }));
    await screen.findByText('Bug');

    expect(screen.getByText('#12 is archived, so choose a lane for the copy.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create' })).toBeDisabled();
    expect(screen.getByLabelText('Lane')).toHaveTextContent('Select lane');
  });

  test('after creating, focus goes where findReturnFocus points for the card just created', async () => {
    // Arrange
    const user = userEvent.setup();
    vi.mocked(createCard).mockResolvedValue({
      id: 'card-new',
      number: 13,
      name: 'Fix login',
      descriptionMarkdown: 'Steps to reproduce',
      laneId: 'lane-2',
      position: 1,
      sizeId: 'size-l',
      sizeName: 'L',
      labels: [labels[0]],
      commentCount: 0,
      attachmentCount: 0,
      isArchived: false,
      createdByUserId: 'me',
      createdAtUtc: '2026-09-29T10:00:00.000Z',
      lastUpdatedByUserId: 'me',
      lastUpdatedAtUtc: '2026-09-29T10:00:00.000Z',
    });
    const findReturnFocus = vi.fn(() => document.getElementById('new-tile'));
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    function Harness() {
      const [isOpen, setIsOpen] = useState(true);
      return (
        <>
          <button type="button" id="new-tile">
            #13
          </button>
          <CreateCardDialog
            boardId="board-1"
            lanes={lanes}
            sizes={sizes}
            open={isOpen}
            onOpenChange={setIsOpen}
            prefill={makePrefill()}
            findReturnFocus={findReturnFocus}
          />
        </>
      );
    }
    render(
      <MemoryRouter>
        <QueryClientProvider client={queryClient}>
          <Harness />
        </QueryClientProvider>
      </MemoryRouter>,
    );
    await screen.findByText('Bug');

    // Act
    await user.click(screen.getByRole('button', { name: 'Create' }));

    // Assert
    await waitFor(() => expect(document.activeElement).toBe(document.getElementById('new-tile')));
    expect(findReturnFocus).toHaveBeenCalledWith('card-new');
  });

  test('a plain new card keeps its usual title and first-lane default', () => {
    setup();

    expect(screen.getByRole('heading', { name: 'Create Card' })).toBeInTheDocument();
    expect(screen.getByLabelText('Name *')).toHaveValue('');
    expect(screen.getByLabelText('Lane')).toHaveTextContent('Backlog');
    expect(screen.queryByText(/choose a lane/i)).not.toBeInTheDocument();
  });
});
