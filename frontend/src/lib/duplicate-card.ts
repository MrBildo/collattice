import type { CardItem, Lane } from '@/types';

// The fields a duplicate starts from. Comments, attachments, description
// history and position are deliberately absent: duplicating is only a shortcut
// for filling in the new-card form, and nothing links the copy to its source.
export type DuplicateFields = {
  name: string;
  descriptionMarkdown: string;
  sizeId: string;
  labelIds: string[];
  laneId: string;
};

export type CardPrefill = DuplicateFields & {
  source: {
    number: number;
    isArchived: boolean;
  };
};

// Both readings of the source card, taken when Duplicate is pressed: `draft` is
// what the form shows (unsaved edits included), `saved` is the card as stored.
// With no unsaved edits they are the same; with edits, whether the user saves
// or discards them decides which one the copy starts from.
export type DuplicateRequest = {
  draft: CardPrefill;
  saved: CardPrefill;
};

// The copy lands in the source's lane. An archived source has no visible lane
// to offer (its card sits in the board's hidden archive lane), so the picker is
// left blank and the user must choose one; the same applies if the lane has
// since been deleted.
export function resolveDuplicateLaneId(laneId: string, isArchived: boolean, lanes: Lane[]): string {
  if (isArchived) {
    return '';
  }

  return lanes.some((lane) => lane.id === laneId) ? laneId : '';
}

export function buildCardPrefill(
  fields: DuplicateFields,
  source: CardItem,
  lanes: Lane[],
): CardPrefill {
  return {
    ...fields,
    laneId: resolveDuplicateLaneId(fields.laneId, source.isArchived, lanes),
    source: { number: source.number, isArchived: source.isArchived },
  };
}
