import type { Active, Announcements, Over, ScreenReaderInstructions } from '@dnd-kit/core';
import { isLaneDragEvent } from '@/lib/dnd-active-type';
import type { CardItem, Lane } from '@/types';

// The board as a drag sees it: cards and lanes in their current order, which a
// drag rearranges as it goes, and as last saved, which is where a cancelled
// drag leaves them.
type BoardDragState = {
  cards: CardItem[];
  savedCards: CardItem[];
  lanes: Lane[];
  savedLanes: Lane[];
};

// dnd-kit's default instructions describe a keyboard drag (space to pick up,
// arrows to move). The board registers no keyboard sensor, so they would tell a
// screen reader user to press keys that do nothing.
const BOARD_DRAG_INSTRUCTIONS: ScreenReaderInstructions = {
  draggable: 'Drag to move it. A card can also be moved from its details by choosing a lane.',
};

function findCard(cards: CardItem[], id: Active['id']): CardItem | null {
  return cards.find((c) => c.id === String(id)) ?? null;
}

function findLane(lanes: Lane[], id: string | null): Lane | null {
  return lanes.find((l) => l.id === id) ?? null;
}

// Messages name things by number and name, never by id: dnd-kit's defaults read
// the raw ids out loud, and a GUID tells a listener nothing.
function formatCardName(card: CardItem | null): string {
  return card ? `card #${card.number}, ${card.name}` : 'a card';
}

function formatCardNumber(card: CardItem | null): string {
  return card ? `card #${card.number}` : 'the card';
}

function formatLaneName(lane: Lane | null): string {
  return lane ? `lane ${lane.name}` : 'a lane';
}

function formatPosition(lanes: Lane[], laneId: string): string {
  const index = lanes.findIndex((l) => l.id === laneId);
  return index === -1 ? 'its place' : `position ${index + 1} of ${lanes.length}`;
}

function formatCardPosition(cards: CardItem[], card: CardItem): string {
  const laneCards = cards.filter((c) => c.laneId === card.laneId);
  const index = laneCards.findIndex((c) => c.id === card.id);
  return index === -1 ? '' : `, at position ${index + 1} of ${laneCards.length}`;
}

function capitalize(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
}

function sortByPosition(lanes: Lane[]): Lane[] {
  return [...lanes].sort((a, b) => a.position - b.position);
}

// A card can be over a lane or over another card; either way the lane is what
// the listener needs to hear.
function findOverLane(state: BoardDragState, over: Over | null): Lane | null {
  if (!over) {
    return null;
  }

  const overId = String(over.id);
  const lane = findLane(state.lanes, overId);
  if (lane) {
    return lane;
  }

  const overCard = findCard(state.cards, overId);
  return overCard ? findLane(state.lanes, overCard.laneId) : null;
}

function buildBoardDragAnnouncements(state: BoardDragState): Announcements {
  const savedLanes = sortByPosition(state.savedLanes);

  return {
    onDragStart({ active }) {
      if (isLaneDragEvent({ active })) {
        const laneId = String(active.id);
        return `Picked up ${formatLaneName(findLane(state.lanes, laneId))}, at ${formatPosition(state.lanes, laneId)}.`;
      }

      const card = findCard(state.cards, active.id);
      const lane = card ? findLane(state.lanes, card.laneId) : null;
      return `Picked up ${formatCardName(card)}, in ${formatLaneName(lane)}.`;
    },

    onDragOver({ active, over }) {
      if (isLaneDragEvent({ active })) {
        const lane = formatLaneName(findLane(state.lanes, String(active.id)));
        if (!over) {
          return `${capitalize(lane)} is not over the board.`;
        }

        // Over a card rather than a lane, the board does not move the lane, so
        // there is nothing new to say.
        const overLane = findLane(state.lanes, String(over.id));
        if (!overLane) {
          return undefined;
        }

        return `${capitalize(lane)} is over ${formatPosition(state.lanes, overLane.id)}.`;
      }

      const card = formatCardNumber(findCard(state.cards, active.id));
      const overLane = findOverLane(state, over);
      if (!overLane) {
        return `${capitalize(card)} is not over a lane.`;
      }

      return `${capitalize(card)} is over ${formatLaneName(overLane)}.`;
    },

    onDragEnd({ active, over }) {
      if (isLaneDragEvent({ active })) {
        const laneId = String(active.id);
        const lane = formatLaneName(findLane(state.lanes, laneId));
        // A lane dropped anywhere but on a lane goes back where it was saved.
        const overLane = over ? findLane(state.lanes, String(over.id)) : null;
        if (!overLane) {
          return `${capitalize(lane)} was dropped outside the lanes and stays at ${formatPosition(savedLanes, laneId)}.`;
        }

        return `Dropped ${lane} at ${formatPosition(state.lanes, overLane.id)}.`;
      }

      // The card's lane in the current order is where the drop sends it.
      const card = findCard(state.cards, active.id);
      if (!over || !card) {
        const savedCard = findCard(state.savedCards, active.id);
        const savedLane = savedCard ? findLane(savedLanes, savedCard.laneId) : null;
        return `${capitalize(formatCardNumber(savedCard))} was dropped outside a lane and stays in ${formatLaneName(savedLane)}.`;
      }

      const lane = findLane(state.lanes, card.laneId);
      return `Dropped ${formatCardNumber(card)} in ${formatLaneName(lane)}${formatCardPosition(state.cards, card)}.`;
    },

    onDragCancel({ active }) {
      if (isLaneDragEvent({ active })) {
        const laneId = String(active.id);
        const lane = formatLaneName(findLane(savedLanes, laneId));
        return `Moving ${lane} was cancelled. It stays at ${formatPosition(savedLanes, laneId)}.`;
      }

      const savedCard = findCard(state.savedCards, active.id);
      const savedLane = savedCard ? findLane(savedLanes, savedCard.laneId) : null;
      return `Moving ${formatCardNumber(savedCard)} was cancelled. It stays in ${formatLaneName(savedLane)}.`;
    },
  };
}

export { BOARD_DRAG_INSTRUCTIONS, buildBoardDragAnnouncements };
export type { BoardDragState };
