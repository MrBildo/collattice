import { describe, test, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Outlet, Route, Routes } from 'react-router-dom';
import type { ReactNode } from 'react';
import { SearchCommand } from './SearchCommand';

// Bypass the 300 ms debounce so tests get synchronous query propagation.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
vi.mock('@/hooks/use-debounce', () => ({ useDebounce: (value: any) => value }));

vi.mock('@/lib/api', () => ({
  fetchBoardBySlug: vi.fn().mockResolvedValue({
    id: 'board-uuid-1',
    name: 'Test Board',
    slug: 'test',
  }),
  searchAllCards: vi.fn(),
}));

import { searchAllCards } from '@/lib/api';

const mockSearchAllCards = vi.mocked(searchAllCards);

function makeSearchResult(
  overrides: {
    boardSlug?: string;
    cards?: Array<{ id: string; number: number; name: string; descriptionMarkdown?: string }>;
  } = {},
) {
  return {
    boardId: 'board-uuid-1',
    boardName: 'Test Board',
    boardSlug: overrides.boardSlug ?? 'test',
    cards: (overrides.cards ?? [{ id: 'card-1', number: 42, name: 'Fix the thing' }]).map((c) => ({
      ...c,
      laneId: 'lane-1',
      position: 0,
      isArchived: false,
      descriptionMarkdown: c.descriptionMarkdown ?? '',
      labels: [],
      sizeName: 'S',
      sizeId: 'size-1',
      commentCount: 0,
      attachmentCount: 0,
      createdByUserId: 'user-1',
      createdAtUtc: '2026-01-01T00:00:00Z',
      lastUpdatedByUserId: 'user-1',
      lastUpdatedAtUtc: '2026-01-01T00:00:00Z',
    })),
  };
}

// Render SearchCommand in a persistent layout that stays mounted across route changes.
// This matches the actual app structure: BoardHeader (with SearchCommand) is rendered
// by the App component and never unmounts when the card-detail route activates.
// The layout route here keeps SearchCommand alive while /cards/:cardNumber is active.
function BoardLayout() {
  return (
    <>
      <SearchCommand />
      <Outlet />
    </>
  );
}

function renderSearch() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/boards/test']}>{children}</MemoryRouter>
      </QueryClientProvider>
    );
  }
  return render(
    <Routes>
      <Route path="/boards/:slug" element={<BoardLayout />}>
        <Route path="cards/:cardNumber" element={<div data-testid="card-detail" />} />
      </Route>
    </Routes>,
    { wrapper: Wrapper },
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  mockSearchAllCards.mockResolvedValue([makeSearchResult()]);
});

describe('SearchCommand — clear on selection', () => {
  test('clicking a result clears the search input', async () => {
    const user = userEvent.setup();
    renderSearch();

    const input = screen.getByRole('textbox');

    // Type a query long enough to satisfy the 2-char minimum
    await user.click(input);
    await user.type(input, 'fix');

    // Wait for the mocked result to render (debounce is bypassed via mock)
    await waitFor(() => {
      expect(screen.getByText('Fix the thing')).toBeInTheDocument();
    });

    expect(input).toHaveValue('fix');

    // Click the result
    await user.click(screen.getByText('Fix the thing'));

    // Input must be cleared automatically
    expect(input).toHaveValue('');
  });

  test('selecting a result with Enter clears the search input', async () => {
    const user = userEvent.setup();
    renderSearch();

    const input = screen.getByRole('textbox');

    await user.click(input);
    await user.type(input, 'fix');

    await waitFor(() => {
      expect(screen.getByText('Fix the thing')).toBeInTheDocument();
    });

    expect(input).toHaveValue('fix');

    // Arrow-down focuses the first result; Enter selects it
    await user.keyboard('{ArrowDown}');
    await user.keyboard('{Enter}');

    expect(input).toHaveValue('');
  });

  test('dropdown is not visible after selecting a result', async () => {
    const user = userEvent.setup();
    renderSearch();

    const input = screen.getByRole('textbox');

    await user.click(input);
    await user.type(input, 'fix');

    await waitFor(() => {
      expect(screen.getByText('Fix the thing')).toBeInTheDocument();
    });

    await user.click(screen.getByText('Fix the thing'));

    // The result item must no longer be rendered
    expect(screen.queryByText('Fix the thing')).not.toBeInTheDocument();
  });
});

describe('SearchCommand — accessible names', () => {
  test('the search field keeps its name while focused, after its placeholder clears', async () => {
    const user = userEvent.setup();
    renderSearch();

    await user.click(screen.getByRole('textbox'));

    // The placeholder is emptied on focus, so it cannot be what names the field here.
    const input = screen.getByRole('textbox', { name: 'Search cards' });
    expect(input).toHaveFocus();
    expect(input).toHaveAttribute('placeholder', '');
  });

  test('the clear button has a name once there is a query', async () => {
    const user = userEvent.setup();
    renderSearch();

    const input = screen.getByRole('textbox');
    await user.type(input, 'fix');
    await user.click(screen.getByRole('button', { name: 'Clear search' }));

    expect(input).toHaveValue('');
  });
});

describe('SearchCommand — boardId wiring', () => {
  test('passes the current board id as the priority boardId arg', async () => {
    const user = userEvent.setup();
    renderSearch();

    const input = screen.getByRole('textbox');

    await user.click(input);
    await user.type(input, 'fix');

    // Wait for the current board id (resolved from the route slug) to reach the call.
    await waitFor(() => {
      expect(mockSearchAllCards).toHaveBeenCalledWith('fix', 20, 'board-uuid-1', 'board-uuid-1');
    });

    // The priority param (3rd arg) must be the current board id — the Bug-1 regression.
    // The pre-fix code sent the current board id in the archiveBoardId slot and never
    // populated boardId, so the backend priority sort received null.
    const lastCall = mockSearchAllCards.mock.calls.at(-1);
    expect(lastCall?.[2]).toBe('board-uuid-1');
  });
});

describe('SearchCommand — result snippets', () => {
  test('shows the description of a result as plain text, without its Markdown', async () => {
    // Arrange
    const user = userEvent.setup();
    mockSearchAllCards.mockResolvedValue([
      makeSearchResult({
        cards: [
          {
            id: 'card-1',
            number: 1,
            name: 'Welcome',
            descriptionMarkdown: '**This is a sample card.** See [the guide](https://example.com).',
          },
        ],
      }),
    ]);
    renderSearch();

    // Act
    await user.type(screen.getByRole('textbox'), 'welcome');

    // Assert
    expect(await screen.findByText('This is a sample card. See the guide.')).toBeInTheDocument();
    expect(screen.queryByText(/\*\*/)).not.toBeInTheDocument();
  });
});
