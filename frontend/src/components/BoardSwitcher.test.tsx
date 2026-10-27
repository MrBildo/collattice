import { describe, test, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';

import type { Board } from '@/types';
import { BoardSwitcher } from './BoardSwitcher';

function makeBoard(overrides: Partial<Board> = {}): Board {
  return {
    id: 'board-1',
    name: 'Default',
    slug: 'default',
    createdAtUtc: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

const boards = [makeBoard(), makeBoard({ id: 'board-2', name: 'Roadmap', slug: 'roadmap' })];

// React warns about a dropped ref only once per component in a test file, on its first render,
// so the spy is in place before any test here renders, and the warning test reads every call.
const consoleError = vi.spyOn(console, 'error');

describe('BoardSwitcher accessible name', () => {
  test('is announced as a board picker, with the current board as its value', () => {
    render(
      <MemoryRouter>
        <BoardSwitcher boards={boards} currentSlug="roadmap" />
      </MemoryRouter>,
    );

    // The name says what the control is for; the board it shows stays its content, so the
    // name does not change when the operator switches boards.
    const switcher = screen.getByRole('combobox', { name: 'Board' });

    expect(switcher).toHaveTextContent('Roadmap');
  });
});

describe('BoardSwitcher trigger', () => {
  test('renders as the select trigger itself, with no tooltip wrapped around it', () => {
    render(
      <MemoryRouter>
        <BoardSwitcher boards={boards} currentSlug="default" />
      </MemoryRouter>,
    );

    expect(screen.getByRole('combobox', { name: 'Board' })).toHaveAttribute(
      'data-slot',
      'select-trigger',
    );
  });

  test('renders and opens without React warning about a dropped ref', async () => {
    // Arrange
    const user = userEvent.setup();

    // Act
    render(
      <MemoryRouter>
        <BoardSwitcher boards={boards} currentSlug="default" />
      </MemoryRouter>,
    );
    await user.hover(screen.getByRole('combobox', { name: 'Board' }));
    await user.click(screen.getByRole('combobox', { name: 'Board' }));

    // Assert: the full list is the place a cut-off name is read in full
    expect(await screen.findByRole('option', { name: 'Roadmap' })).toBeInTheDocument();
    const refWarnings = consoleError.mock.calls.filter((args) =>
      String(args[0]).includes('cannot be given refs'),
    );
    expect(refWarnings).toEqual([]);
  });
});
