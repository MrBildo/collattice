import { describe, expect, test } from 'vitest';
import type { Active, Announcements, Over } from '@dnd-kit/core';
import {
  BOARD_DRAG_INSTRUCTIONS,
  buildBoardDragAnnouncements,
  type BoardDragState,
} from './board-drag-announcements';
import type { CardItem, Lane } from '@/types';

// GUID-shaped ids, so "no id in any message" is checked against the real shape.
const BACKLOG = '0b9a64f5-2e47-4a3c-9d0e-6f1a2b3c4d01';
const DOING = '0b9a64f5-2e47-4a3c-9d0e-6f1a2b3c4d02';
const DONE = '0b9a64f5-2e47-4a3c-9d0e-6f1a2b3c4d03';
const CARD_A = 'dc7a9e3b-cadf-44ef-a49c-45261f9d1301';
const CARD_B = 'dc7a9e3b-cadf-44ef-a49c-45261f9d1302';
const CARD_C = 'dc7a9e3b-cadf-44ef-a49c-45261f9d1303';
const ALL_IDS = [BACKLOG, DOING, DONE, CARD_A, CARD_B, CARD_C];

function makeLane(id: string, name: string, position: number): Lane {
  return { id, boardId: 'board-1', name, position };
}

function makeCard(id: string, number: number, name: string, laneId: string): CardItem {
  return {
    id,
    number,
    name,
    descriptionMarkdown: '',
    laneId,
    position: number,
    sizeId: 'size-m',
    isArchived: false,
    createdByUserId: 'me',
    createdAtUtc: '2026-09-01T10:00:00.000Z',
    lastUpdatedByUserId: 'me',
    lastUpdatedAtUtc: '2026-09-01T10:00:00.000Z',
  };
}

function makeActive(id: string, type?: 'lane'): Active {
  return {
    id,
    data: { current: type ? { type } : undefined },
    rect: { current: { initial: null, translated: null } },
  };
}

function makeOver(id: string): Over {
  return {
    id,
    rect: { width: 0, height: 0, top: 0, left: 0, right: 0, bottom: 0 },
    disabled: false,
    data: { current: undefined },
  };
}

const lanes = [
  makeLane(BACKLOG, 'Backlog', 0),
  makeLane(DOING, 'In Progress', 1),
  makeLane(DONE, 'Done', 2),
];
const savedCards = [
  makeCard(CARD_A, 12, 'Fix the date picker', BACKLOG),
  makeCard(CARD_B, 13, 'Tidy the header', DOING),
  makeCard(CARD_C, 14, 'Ship it', DOING),
];

function makeState(overrides: Partial<BoardDragState> = {}): BoardDragState {
  return { cards: savedCards, savedCards, lanes, savedLanes: lanes, ...overrides };
}

// Card A mid-drag, moved by the board into In Progress ahead of card B.
const midDragCards = [
  makeCard(CARD_A, 12, 'Fix the date picker', DOING),
  savedCards[1],
  savedCards[2],
];

function allMessages(announcements: Announcements, active: Active, overIds: (string | null)[]) {
  return overIds.flatMap((overId) => {
    const over = overId ? makeOver(overId) : null;
    return [
      announcements.onDragStart({ active }),
      announcements.onDragOver({ active, over }),
      announcements.onDragEnd({ active, over }),
      announcements.onDragCancel({ active, over }),
    ];
  });
}

describe('buildBoardDragAnnouncements', () => {
  test('no message for any card or lane drag reads out an id', () => {
    const announcements = buildBoardDragAnnouncements(makeState({ cards: midDragCards }));
    const overIds = [...ALL_IDS, null];

    const messages = [
      ...allMessages(announcements, makeActive(CARD_A), overIds),
      ...allMessages(announcements, makeActive(BACKLOG, 'lane'), overIds),
    ];

    expect(messages.length).toBeGreaterThan(0);
    for (const message of messages) {
      if (message === undefined) {
        continue;
      }

      for (const id of ALL_IDS) {
        expect(message).not.toContain(id);
      }
    }
  });

  test('picking up a card names its number, title and lane', () => {
    const announcements = buildBoardDragAnnouncements(makeState());

    expect(announcements.onDragStart({ active: makeActive(CARD_A) })).toBe(
      'Picked up card #12, Fix the date picker, in lane Backlog.',
    );
  });

  test('a card over another card is announced as over that card’s lane', () => {
    const announcements = buildBoardDragAnnouncements(makeState());

    expect(announcements.onDragOver({ active: makeActive(CARD_A), over: makeOver(CARD_C) })).toBe(
      'Card #12 is over lane In Progress.',
    );
    expect(announcements.onDragOver({ active: makeActive(CARD_A), over: makeOver(DONE) })).toBe(
      'Card #12 is over lane Done.',
    );
    expect(announcements.onDragOver({ active: makeActive(CARD_A), over: null })).toBe(
      'Card #12 is not over a lane.',
    );
  });

  test('dropping a card names the lane and position it lands at', () => {
    const announcements = buildBoardDragAnnouncements(makeState({ cards: midDragCards }));

    expect(announcements.onDragEnd({ active: makeActive(CARD_A), over: makeOver(CARD_B) })).toBe(
      'Dropped card #12 in lane In Progress, at position 1 of 3.',
    );
  });

  test('a card dropped outside a lane, or cancelled, is announced where it was saved', () => {
    const announcements = buildBoardDragAnnouncements(makeState({ cards: midDragCards }));

    expect(announcements.onDragEnd({ active: makeActive(CARD_A), over: null })).toBe(
      'Card #12 was dropped outside a lane and stays in lane Backlog.',
    );
    expect(announcements.onDragCancel({ active: makeActive(CARD_A), over: makeOver(CARD_B) })).toBe(
      'Moving card #12 was cancelled. It stays in lane Backlog.',
    );
  });

  test('a lane drag names the lane and the position it would take', () => {
    // Mid-drag the board has already moved Backlog to the end.
    const midDragLanes = [lanes[1], lanes[2], lanes[0]];
    const announcements = buildBoardDragAnnouncements(makeState({ lanes: midDragLanes }));
    const active = makeActive(BACKLOG, 'lane');

    expect(buildBoardDragAnnouncements(makeState()).onDragStart({ active })).toBe(
      'Picked up lane Backlog, at position 1 of 3.',
    );
    expect(announcements.onDragOver({ active, over: makeOver(DONE) })).toBe(
      'Lane Backlog is over position 2 of 3.',
    );
    expect(announcements.onDragEnd({ active, over: makeOver(BACKLOG) })).toBe(
      'Dropped lane Backlog at position 3 of 3.',
    );
    expect(announcements.onDragCancel({ active, over: null })).toBe(
      'Moving lane Backlog was cancelled. It stays at position 1 of 3.',
    );
  });

  test('a lane over a card says nothing new, and dropped there it stays where it was saved', () => {
    const midDragLanes = [lanes[1], lanes[0], lanes[2]];
    const announcements = buildBoardDragAnnouncements(makeState({ lanes: midDragLanes }));
    const active = makeActive(BACKLOG, 'lane');

    expect(announcements.onDragOver({ active, over: makeOver(CARD_B) })).toBeUndefined();
    expect(announcements.onDragEnd({ active, over: makeOver(CARD_B) })).toBe(
      'Lane Backlog was dropped outside the lanes and stays at position 1 of 3.',
    );
  });

  test('an item the board cannot find is described generically, not by id', () => {
    const announcements = buildBoardDragAnnouncements(makeState());
    const unknown = 'f00dfeed-0000-4000-8000-000000000000';

    const message = announcements.onDragStart({ active: makeActive(unknown) });

    expect(message).toBe('Picked up a card, in a lane.');
    expect(message).not.toContain(unknown);
  });
});

test('the drag instructions do not describe a keyboard drag the board does not offer', () => {
  expect(BOARD_DRAG_INSTRUCTIONS.draggable).not.toMatch(/space bar|arrow keys/i);
});
