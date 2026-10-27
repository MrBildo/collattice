import { describe, test, expect, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { LabelPicker } from './LabelPicker';
import type { Label } from '@/types';

const LABELS: Label[] = [
  { id: 'label-bug', boardId: 'board-1', name: 'Bug', color: '#ff5533' },
  { id: 'label-feature', boardId: 'board-1', name: 'Feature', color: '#66dd33' },
];

function renderPicker() {
  render(<LabelPicker allLabels={LABELS} assignedLabels={[]} onAdd={vi.fn()} onRemove={vi.fn()} />);
  return screen.getByRole('button', { name: /labels/i });
}

function findPopup(): HTMLElement | null {
  return document.querySelector('[data-slot="popover-content"]');
}

describe('LabelPicker focus on open', () => {
  test('opening with the mouse focuses the search box', async () => {
    // Arrange
    const user = userEvent.setup();
    const trigger = renderPicker();

    // Act
    await user.click(trigger);

    // Assert
    await waitFor(() =>
      expect(screen.getByRole('combobox', { name: 'Search labels' })).toHaveFocus(),
    );
  });

  test('opening with the keyboard focuses the search box', async () => {
    // Arrange
    const user = userEvent.setup();
    const trigger = renderPicker();
    trigger.focus();

    // Act
    await user.keyboard('{Enter}');

    // Assert
    await waitFor(() =>
      expect(screen.getByRole('combobox', { name: 'Search labels' })).toHaveFocus(),
    );
  });

  test('opening by touch focuses the popup, not the search box, so no on-screen keyboard appears', async () => {
    // Arrange
    const user = userEvent.setup();
    const trigger = renderPicker();

    // Act
    await user.pointer([{ keys: '[TouchA]', target: trigger }]);

    // Assert
    await waitFor(() => {
      const popup = findPopup();
      expect(popup).not.toBeNull();
      expect(popup).toHaveFocus();
    });
    expect(screen.getByRole('combobox', { name: 'Search labels' })).not.toHaveFocus();
    expect(screen.getAllByRole('option')).toHaveLength(2);
  });

  test('tapping the search box after a touch open focuses it', async () => {
    // Arrange
    const user = userEvent.setup();
    const trigger = renderPicker();
    await user.pointer([{ keys: '[TouchA]', target: trigger }]);
    await waitFor(() => expect(findPopup()).toHaveFocus());
    const search = screen.getByRole('combobox', { name: 'Search labels' });

    // Act
    await user.pointer([{ keys: '[TouchA]', target: search }]);

    // Assert
    expect(search).toHaveFocus();
  });
});
