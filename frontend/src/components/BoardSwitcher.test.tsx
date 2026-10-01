import { describe, test, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
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
