import { describe, test, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import type { ReactNode } from 'react';
import { App } from './App';
import type { CardPrefill } from '@/lib/duplicate-card';

// useAuth is the route-level gate. Mock it so each test drives the logged-in flag
// directly rather than through localStorage + the auth library.
const mockUseAuth = vi.fn();
vi.mock('@/hooks/use-auth', () => ({
  useAuth: () => mockUseAuth(),
}));

// useBoardData is stubbed to empty lanes (per the card) so the logged-in path
// renders board chrome without exercising the board-fetch query chain.
vi.mock('@/hooks/use-board-data', () => ({
  useBoardData: () => ({
    board: { id: 'board-1', name: 'Test Board', slug: 'test' },
    boardId: 'board-1',
    boardMetaQuery: { isLoading: false, isError: false },
    boardDataQuery: { isLoading: false, isError: false },
    lanes: [],
    sizes: [],
    sizeMap: new Map(),
    serverCards: [],
    enrichedCardMap: new Map(),
  }),
}));

// useBoardEvents opens a real EventSource (absent in jsdom). The gate test does
// not exercise SSE — that surface is covered by use-board-events.test.ts.
vi.mock('@/hooks/use-board-events', () => ({
  useBoardEvents: () => {},
}));

// API layer: the board-list and version queries are enabled once logged in.
// Resolve them to benign values so nothing hits the network.
vi.mock('@/lib/api', () => ({
  fetchBoards: vi.fn().mockResolvedValue([]),
  fetchCards: vi.fn().mockResolvedValue({ items: [] }),
  fetchVersion: vi.fn().mockResolvedValue({ version: '1.0.0' }),
  fetchVersionStatus: vi.fn().mockResolvedValue({
    current: '1.0.0',
    latest: null,
    updateAvailable: false,
    releaseUrl: null,
    lastChecked: null,
  }),
  fetchMe: vi.fn().mockResolvedValue({ id: 'user-1', name: 'Test User', role: 1 }),
  fetchUsers: vi.fn().mockResolvedValue([]),
  // Read by the new-card dialog.
  fetchLabels: vi.fn().mockResolvedValue([]),
  fetchBoardData: vi.fn().mockResolvedValue({ lanes: [], cards: [], sizes: [] }),
}));

// The card dialog has its own suites. Stand in for it with the one thing the
// duplicate test needs from it: a Duplicate button that hands App a prefill.
vi.mock('@/components/CardDetailSheet', () => ({
  CardDetailSheet: ({ onDuplicate }: { onDuplicate?: (prefill: CardPrefill) => void }) => (
    <button type="button" onClick={() => onDuplicate?.(duplicatePrefill)}>
      Duplicate #12
    </button>
  ),
}));
vi.mock('mermaid', () => ({ default: { initialize: vi.fn(), render: vi.fn() } }));

const duplicatePrefill: CardPrefill = {
  name: 'Fix login',
  descriptionMarkdown: 'Steps to reproduce',
  sizeId: '',
  labelIds: [],
  laneId: '',
  source: { id: 'card-12', number: 12, isArchived: false },
};

function renderApp() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/boards/test']}>
          <Routes>
            <Route path="/boards/:slug" element={children} />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>
    );
  }
  return render(<App />, { wrapper: Wrapper });
}

beforeEach(() => {
  vi.clearAllMocks();
  mockUseAuth.mockReturnValue({
    loggedIn: false,
    handleLogin: vi.fn(),
    handleLogout: vi.fn(),
  });
});

describe('App route-level login gate', () => {
  test('logged out renders the login screen and no board chrome', () => {
    mockUseAuth.mockReturnValue({
      loggedIn: false,
      handleLogin: vi.fn(),
      handleLogout: vi.fn(),
    });

    renderApp();

    expect(screen.getByText(/enter your auth key to continue/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /log in/i })).toBeInTheDocument();
    // Board chrome must be absent: no kanban region, no new-card action.
    expect(screen.queryByRole('region', { name: /kanban board/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /new card/i })).not.toBeInTheDocument();
  });

  test('logged in renders board chrome and not the login screen', () => {
    mockUseAuth.mockReturnValue({
      loggedIn: true,
      handleLogin: vi.fn(),
      handleLogout: vi.fn(),
    });

    renderApp();

    expect(screen.getByRole('region', { name: /kanban board/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /new card/i })).toBeInTheDocument();
    expect(screen.queryByText(/enter your auth key to continue/i)).not.toBeInTheDocument();
  });
});

describe('App new-card dialog after a duplicate', () => {
  test('New Card opens a blank dialog after a duplicate was started and cancelled', async () => {
    // Arrange
    const user = userEvent.setup();
    mockUseAuth.mockReturnValue({ loggedIn: true, handleLogin: vi.fn(), handleLogout: vi.fn() });
    renderApp();
    await user.click(screen.getByRole('button', { name: 'Duplicate #12' }));
    expect(await screen.findByRole('heading', { name: 'Duplicate Card' })).toBeInTheDocument();
    expect(screen.getByLabelText('Name *')).toHaveValue('Fix login');
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() =>
      expect(screen.queryByRole('heading', { name: 'Duplicate Card' })).not.toBeInTheDocument(),
    );

    // Act
    await user.click(screen.getByRole('button', { name: /new card/i }));

    // Assert
    expect(await screen.findByRole('heading', { name: 'Create Card' })).toBeInTheDocument();
    expect(screen.getByLabelText('Name *')).toHaveValue('');
    expect(screen.getByLabelText('Description')).toHaveValue('');
  });
});
