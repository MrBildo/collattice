import { describe, test, expect } from 'vitest';
import { buildCardPrefill, resolveDuplicateLaneId } from './duplicate-card';
import type { CardItem, Lane } from '@/types';

function makeLane(id: string): Lane {
  return { id, boardId: 'board-1', name: id, position: 0 };
}

function makeCard(overrides: Partial<CardItem> = {}): CardItem {
  return {
    id: 'card-1',
    number: 12,
    name: 'Source',
    descriptionMarkdown: 'Body',
    laneId: 'lane-doing',
    position: 30,
    sizeId: 'size-m',
    isArchived: false,
    createdByUserId: 'me',
    createdAtUtc: '2026-09-01T10:00:00.000Z',
    lastUpdatedByUserId: 'me',
    lastUpdatedAtUtc: '2026-09-01T10:00:00.000Z',
    ...overrides,
  };
}

const lanes = [makeLane('lane-todo'), makeLane('lane-doing')];

describe('resolveDuplicateLaneId', () => {
  test('keeps the source lane when the source is active', () => {
    expect(resolveDuplicateLaneId('lane-doing', false, lanes)).toBe('lane-doing');
  });

  test('leaves the lane blank when the source is archived', () => {
    // An archived card's laneId is the hidden archive lane, which is never offered.
    expect(resolveDuplicateLaneId('lane-archive', true, lanes)).toBe('');
  });

  test('leaves the lane blank when the source lane is no longer on the board', () => {
    expect(resolveDuplicateLaneId('lane-gone', false, lanes)).toBe('');
  });
});

describe('buildCardPrefill', () => {
  test('carries the given fields and records the source for the dialog copy', () => {
    const prefill = buildCardPrefill(
      {
        name: 'Source',
        descriptionMarkdown: 'Body',
        sizeId: 'size-m',
        labelIds: ['label-bug'],
        laneId: 'lane-doing',
      },
      makeCard(),
      lanes,
    );

    expect(prefill).toEqual({
      name: 'Source',
      descriptionMarkdown: 'Body',
      sizeId: 'size-m',
      labelIds: ['label-bug'],
      laneId: 'lane-doing',
      source: { number: 12, isArchived: false },
    });
  });

  test('an archived source produces a blank lane however the fields were read', () => {
    const prefill = buildCardPrefill(
      {
        name: 'Source',
        descriptionMarkdown: '',
        sizeId: 'size-m',
        labelIds: [],
        laneId: 'lane-doing',
      },
      makeCard({ isArchived: true }),
      lanes,
    );

    expect(prefill.laneId).toBe('');
    expect(prefill.source.isArchived).toBe(true);
  });
});
