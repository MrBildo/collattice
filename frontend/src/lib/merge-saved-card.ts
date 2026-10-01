import type { CardSummary } from '@/types';

// Merges a card saved from the card form into the board's cached cards.
//
// A save that changes the card's lane sends no position, so the server puts the card at the end
// of its new lane and renumbers both lanes. The response carries only this card's new number,
// while its new neighbours keep their old cached numbers until the board is refetched. Against
// those, the new number can tie or sort too early: a lane with gaps left by deleted cards is
// renumbered downward. So until the refetch, the moved card keeps the server's number when that
// already sorts after every other cached card in its new lane, and otherwise takes one past the
// highest of them. Either way the lane shows the order the server has.
export function mergeSavedCard(
  cards: CardSummary[],
  saved: CardSummary,
  isLaneChange: boolean,
): CardSummary[] {
  const merged = cards.map((c) => (c.id === saved.id ? { ...c, ...saved } : c));
  if (!isLaneChange) return merged;

  const highestNeighbour = Math.max(
    -Infinity,
    ...cards.filter((c) => c.laneId === saved.laneId && c.id !== saved.id).map((c) => c.position),
  );
  if (saved.position > highestNeighbour) return merged;

  return merged.map((c) => (c.id === saved.id ? { ...c, position: highestNeighbour + 1 } : c));
}
