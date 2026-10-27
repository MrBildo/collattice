import { describe, test, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { CardDescriptionHistory } from './CardDescriptionHistory';
import { fetchCardHistory } from '@/lib/api';
import type { CardHistoryEntry, CardHistoryTrail } from '@/types';

vi.mock('@/lib/api', () => ({
  fetchCardHistory: vi.fn(),
}));

// MermaidBlock pulls in the real mermaid package; the full-text view renders
// through MarkdownRenderer, so keep the module inert in jsdom.
vi.mock('mermaid', () => ({
  default: {
    initialize: vi.fn(),
    render: vi.fn(),
  },
}));

const mockedFetchCardHistory = vi.mocked(fetchCardHistory);

function makeEntry(overrides: Partial<CardHistoryEntry> = {}): CardHistoryEntry {
  return {
    revision: 2,
    editedByUserId: 'user-1',
    editedByName: 'Bot Cora',
    editedAtUtc: '2026-07-23T20:41:12.7731840+00:00',
    value: 'the full text at this revision',
    diff: '@@ -1,1 +1,1 @@\n-old line\n+new line\n',
    ...overrides,
  };
}

function makeTrail(
  entries: CardHistoryEntry[],
  overrides: Partial<CardHistoryTrail> = {},
): CardHistoryTrail {
  return {
    cardId: 'card-1',
    field: 'description',
    entries,
    totalCount: entries.length,
    offset: 0,
    limit: null,
    ...overrides,
  };
}

function renderPanel() {
  const queryClient = new QueryClient({
    // The component's own config asks for one retry; zero delay keeps the
    // error-path test fast without changing how many attempts run.
    defaultOptions: { queries: { retryDelay: 0 } },
  });
  return render(
    <MemoryRouter>
      <QueryClientProvider client={queryClient}>
        <CardDescriptionHistory cardId="card-1" />
      </QueryClientProvider>
    </MemoryRouter>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe('CardDescriptionHistory', () => {
  test('renders revisions newest-first with author and timestamp', async () => {
    mockedFetchCardHistory.mockResolvedValue(
      makeTrail([
        makeEntry({ revision: 3, editedByName: 'Bot Kai' }),
        makeEntry({ revision: 2, editedByName: 'Bot Cora' }),
        makeEntry({
          revision: 1,
          editedByUserId: null,
          editedByName: null,
          editedAtUtc: null,
          diff: '',
          value: 'the original text',
        }),
      ]),
    );

    renderPanel();

    await waitFor(() => {
      expect(screen.getByText('Revision 3')).toBeInTheDocument();
    });
    const headers = screen.getAllByText(/^Revision \d+$/).map((el) => el.textContent);
    expect(headers).toEqual(['Revision 3', 'Revision 2', 'Revision 1']);
    expect(screen.getByText(/Bot Kai/)).toBeInTheDocument();
    expect(screen.getByText(/Bot Cora/)).toBeInTheDocument();
    expect(screen.getByText('3 revisions, newest first')).toBeInTheDocument();
  });

  test('the oldest revision renders as the un-attributed starting text, never an empty author or invalid date', async () => {
    mockedFetchCardHistory.mockResolvedValue(
      makeTrail([
        makeEntry({ revision: 2 }),
        makeEntry({
          revision: 1,
          editedByUserId: null,
          editedByName: null,
          editedAtUtc: null,
          diff: '',
          value: 'the original text',
        }),
      ]),
    );

    const { container } = renderPanel();

    await waitFor(() => {
      expect(screen.getByText('original version — author unknown')).toBeInTheDocument();
    });
    expect(
      screen.getByText('The description as it stood when history recording began.'),
    ).toBeInTheDocument();
    expect(container.textContent).not.toContain('Invalid Date');
  });

  test('the oldest revision names the card creator, hedged as the card creator, and observed revisions are untouched', async () => {
    mockedFetchCardHistory.mockResolvedValue(
      makeTrail([
        makeEntry({ revision: 2, editedByName: 'Bot Cora' }),
        makeEntry({
          revision: 1,
          editedByUserId: null,
          editedByName: null,
          editedAtUtc: null,
          inferredEditor: { userId: 'user-9', name: 'Bot Cairn', basis: 'creator' },
          diff: '',
          value: 'the original text',
        }),
      ]),
    );

    const { container } = renderPanel();

    await waitFor(() => {
      expect(screen.getByText('original version — Bot Cairn (card creator)')).toBeInTheDocument();
    });
    expect(screen.queryByText(/author unknown/)).not.toBeInTheDocument();
    // The inferred name belongs to the original version only; the observed
    // revision still reads as its own editor.
    expect(screen.getByText(/^Bot Cora/)).toBeInTheDocument();
    expect(container.textContent).not.toContain('Invalid Date');
  });

  test('an inference basis this view does not recognise still renders hedged, never as an observed author', async () => {
    mockedFetchCardHistory.mockResolvedValue(
      makeTrail([
        makeEntry({ revision: 2 }),
        makeEntry({
          revision: 1,
          editedByUserId: null,
          editedByName: null,
          editedAtUtc: null,
          inferredEditor: { userId: 'user-9', name: 'Bot Cairn', basis: 'something-new' },
          diff: '',
        }),
      ]),
    );

    renderPanel();

    await waitFor(() => {
      expect(screen.getByText('original version — Bot Cairn (inferred)')).toBeInTheDocument();
    });
  });

  test('renders diff lines with their content and marks additions and removals', async () => {
    mockedFetchCardHistory.mockResolvedValue(
      makeTrail([
        makeEntry({
          revision: 2,
          diff: '@@ -1,2 +1,2 @@\n context line\n-removed line\n+added line\n',
        }),
      ]),
    );

    renderPanel();

    await waitFor(() => {
      expect(screen.getByText('@@ -1,2 +1,2 @@')).toBeInTheDocument();
    });
    expect(screen.getByText('context line')).toBeInTheDocument();
    expect(screen.getByText('removed line')).toBeInTheDocument();
    expect(screen.getByText('added line')).toBeInTheDocument();
  });

  test('add/remove lines carry a screen-reader-only signal beyond the aria-hidden glyph', async () => {
    mockedFetchCardHistory.mockResolvedValue(
      makeTrail([
        makeEntry({
          revision: 2,
          diff: '@@ -1,2 +1,2 @@\n context line\n-removed line\n+added line\n',
        }),
      ]),
    );

    renderPanel();

    await waitFor(() => {
      expect(screen.getByText('@@ -1,2 +1,2 @@')).toBeInTheDocument();
    });

    // The visible gutter marker (+/-) is aria-hidden, so colour is the only
    // sighted-only cue. A screen-reader user needs a spoken equivalent that
    // does not depend on perceiving colour.
    expect(screen.getByText('Added:', { selector: '.sr-only' })).toBeInTheDocument();
    expect(screen.getByText('Removed:', { selector: '.sr-only' })).toBeInTheDocument();
    // The unchanged context line has no colour-only distinction to replace.
    const contextRow = screen.getByText('context line').closest('div');
    expect(contextRow?.querySelector('.sr-only')).toBeNull();
  });

  test('a non-first revision with an empty diff renders the no-visible-change note, not a blank panel', async () => {
    mockedFetchCardHistory.mockResolvedValue(
      makeTrail([makeEntry({ revision: 2, diff: '' }), makeEntry({ revision: 1, diff: '' })]),
    );

    renderPanel();

    await waitFor(() => {
      expect(screen.getByText(/No visible change/)).toBeInTheDocument();
    });
  });

  test('Show full text reveals the revision value rendered as markdown', async () => {
    mockedFetchCardHistory.mockResolvedValue(
      makeTrail([makeEntry({ revision: 2, value: 'some **bold** text' })]),
    );

    renderPanel();
    await waitFor(() => {
      expect(screen.getByText('Revision 2')).toBeInTheDocument();
    });

    expect(screen.queryByText('bold')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Show full text' }));

    const bold = screen.getByText('bold');
    expect(bold.tagName).toBe('STRONG');
    expect(screen.getByRole('button', { name: 'Hide full text' })).toBeInTheDocument();
  });

  test('a load failure renders an error with a retry, never an empty state', async () => {
    mockedFetchCardHistory.mockRejectedValue(new Error('boom'));

    renderPanel();

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent(/Couldn't load history/);
    });
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
    expect(screen.queryByText(/No recorded history/)).not.toBeInTheDocument();
  });

  test('an empty trail renders the honest empty state', async () => {
    mockedFetchCardHistory.mockResolvedValue(makeTrail([]));

    renderPanel();

    await waitFor(() => {
      expect(screen.getByText(/No recorded history/)).toBeInTheDocument();
    });
  });
});
