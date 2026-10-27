import { describe, test, expect } from 'vitest';

import type { CardSummary } from '@/types';
import { mergeSavedCard } from './merge-saved-card';

function makeCard(overrides: Partial<CardSummary> = {}): CardSummary {
  return {
    id: 'card-1',
    number: 1,
    name: 'Card',
    descriptionMarkdown: '',
    sizeId: 'size-1',
    sizeName: 'S',
    laneId: 'lane-a',
    position: 0,
    isArchived: false,
    createdByUserId: 'me',
    createdAtUtc: '2026-10-01T00:00:00Z',
    lastUpdatedByUserId: 'me',
    lastUpdatedAtUtc: '2026-10-01T00:00:00Z',
    labels: [],
    commentCount: 0,
    attachmentCount: 0,
    ...overrides,
  };
}

// The order the board renders a lane in: its cards sorted by position, ties kept in array order.
function laneOrder(cards: CardSummary[], laneId: string): string[] {
  return cards
    .filter((c) => c.laneId === laneId)
    .sort((a, b) => a.position - b.position)
    .map((c) => c.name);
}

describe('mergeSavedCard', () => {
  test('a card moved into a lane with a wide gap sorts last, not between its new neighbours', () => {
    // Arrange: the target lane is P 0, R 40 in the cache (three cards deleted between them).
    // The server put C at the end and renumbered the lane to P 0, R 10, C 20.
    const cards = [
      makeCard({ id: 'p', name: 'P', laneId: 'lane-b', position: 0 }),
      makeCard({ id: 'r', name: 'R', laneId: 'lane-b', position: 40 }),
      makeCard({ id: 'c', name: 'C', laneId: 'lane-a', position: 10 }),
    ];
    const saved = makeCard({ id: 'c', name: 'C', laneId: 'lane-b', position: 20 });

    // Act
    const merged = mergeSavedCard(cards, saved, true);

    // Assert
    expect(laneOrder(merged, 'lane-b')).toEqual(['P', 'R', 'C']);
  });

  test('a card whose new number ties a neighbour sorts last even when it comes first in the array', () => {
    // Arrange: the target lane is X 0, B 20 in the cache; the server put P at 20 and moved B to
    // 10. P's lane comes first in the board's card order, so a tie would keep P above B.
    const cards = [
      makeCard({ id: 'p', name: 'P', laneId: 'lane-a', position: 0 }),
      makeCard({ id: 'x', name: 'X', laneId: 'lane-b', position: 0 }),
      makeCard({ id: 'b', name: 'B', laneId: 'lane-b', position: 20 }),
    ];
    const saved = makeCard({ id: 'p', name: 'P', laneId: 'lane-b', position: 20 });

    // Act
    const merged = mergeSavedCard(cards, saved, true);

    // Assert
    expect(laneOrder(merged, 'lane-b')).toEqual(['X', 'B', 'P']);
  });

  test("keeps the server's number when it already sorts after every cached neighbour", () => {
    const cards = [
      makeCard({ id: 'x', name: 'X', laneId: 'lane-b', position: 0 }),
      makeCard({ id: 'y', name: 'Y', laneId: 'lane-b', position: 10 }),
      makeCard({ id: 'm', name: 'M', laneId: 'lane-a', position: 0 }),
    ];
    const saved = makeCard({ id: 'm', name: 'M', laneId: 'lane-b', position: 20 });

    const merged = mergeSavedCard(cards, saved, true);

    expect(merged.find((c) => c.id === 'm')?.position).toBe(20);
  });

  test('a card moved into an empty lane keeps the number the server gave it', () => {
    const cards = [makeCard({ id: 'm', laneId: 'lane-a', position: 30 })];
    const saved = makeCard({ id: 'm', laneId: 'lane-b', position: 0 });

    const merged = mergeSavedCard(cards, saved, true);

    expect(merged[0]).toMatchObject({ laneId: 'lane-b', position: 0 });
  });

  test('a save that keeps the lane merges the response but keeps the cached position', () => {
    // Arrange: a rename; the server's number differs from the cached one, as it does after the
    // server has renumbered the lane and the cache has not caught up.
    const cards = [
      makeCard({ id: 'x', name: 'X', laneId: 'lane-a', position: 41 }),
      makeCard({ id: 'y', name: 'Y', laneId: 'lane-a', position: 40 }),
    ];
    const saved = makeCard({ id: 'x', name: 'X renamed', laneId: 'lane-a', position: 20 });

    // Act
    const merged = mergeSavedCard(cards, saved, false);

    // Assert
    expect(merged).toEqual([{ ...cards[0], name: 'X renamed' }, cards[1]]);
  });

  test('a second save before the refetch keeps the moved card last in its new lane', () => {
    // Arrange: C moves into P 0, R 40 (the server says C 20, R 10); then C is renamed before the
    // board refetch lands, and that response carries the server's 20 again.
    const cards = [
      makeCard({ id: 'p', name: 'P', laneId: 'lane-b', position: 0 }),
      makeCard({ id: 'r', name: 'R', laneId: 'lane-b', position: 40 }),
      makeCard({ id: 'c', name: 'C', laneId: 'lane-a', position: 10 }),
    ];
    const moved = makeCard({ id: 'c', name: 'C', laneId: 'lane-b', position: 20 });
    const renamed = makeCard({ id: 'c', name: 'C renamed', laneId: 'lane-b', position: 20 });

    // Act
    const afterMove = mergeSavedCard(cards, moved, true);
    const afterRename = mergeSavedCard(afterMove, renamed, false);

    // Assert
    expect(laneOrder(afterMove, 'lane-b')).toEqual(['P', 'R', 'C']);
    expect(laneOrder(afterRename, 'lane-b')).toEqual(['P', 'R', 'C renamed']);
  });

  test('merges every field of the response into the moved card', () => {
    const cards = [makeCard({ id: 'm', laneId: 'lane-a', commentCount: 0 })];
    const saved = makeCard({ id: 'm', laneId: 'lane-b', name: 'New name', commentCount: 3 });

    const merged = mergeSavedCard(cards, saved, true);

    expect(merged[0]).toEqual(saved);
  });
});
