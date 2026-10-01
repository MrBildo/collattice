import { describe, test, expect, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { DndContext, MouseSensor, TouchSensor, useSensor, useSensors } from '@dnd-kit/core';
import { LaneColumn } from './LaneColumn';
import type { Lane } from '@/types';

function makeLane(overrides: Partial<Lane> = {}): Lane {
  return {
    id: 'lane-1',
    boardId: 'board-1',
    name: 'Backlog',
    position: 0,
    ...overrides,
  };
}

// The board's own sensors. A bare DndContext would add dnd-kit's default
// keyboard sensor, whose keydown listener on the header turns an Enter on the
// add button into a lane drag; the board registers no keyboard sensor.
function BoardDnd({
  children,
  onDragStart,
}: {
  children: React.ReactNode;
  onDragStart?: () => void;
}) {
  const sensors = useSensors(
    useSensor(MouseSensor, { activationConstraint: { distance: 8 } }),
    useSensor(TouchSensor, { activationConstraint: { delay: 200, tolerance: 5 } }),
  );
  return (
    <DndContext sensors={sensors} onDragStart={onDragStart}>
      {children}
    </DndContext>
  );
}

function renderLanes(
  lanes: Lane[],
  onAddCard = vi.fn(),
  onToggleCollapse = vi.fn(),
  onDragStart = vi.fn(),
) {
  render(
    <BoardDnd onDragStart={onDragStart}>
      {lanes.map((lane) => (
        <LaneColumn
          key={lane.id}
          lane={lane}
          cards={[]}
          onCardClick={vi.fn()}
          onAddCard={() => onAddCard(lane.name)}
          activeCardId={null}
          isLaneDragging={false}
          sizeMap={new Map()}
          enrichedCardMap={new Map()}
          isCollapsed={false}
          onToggleCollapse={onToggleCollapse}
        />
      ))}
    </BoardDnd>,
  );
  return { onAddCard, onToggleCollapse, onDragStart };
}

// A press followed by a move well past the mouse sensor's 8px activation distance.
function pressAndMove(target: HTMLElement) {
  fireEvent.mouseDown(target, { button: 0, clientX: 10, clientY: 10 });
  fireEvent.mouseMove(document, { clientX: 60, clientY: 10 });
  fireEvent.mouseMove(document, { clientX: 120, clientY: 10 });
  fireEvent.mouseUp(document, { clientX: 120, clientY: 10 });
}

describe('LaneColumn add-card button', () => {
  test('each lane names its add-card button after the lane', () => {
    renderLanes([makeLane(), makeLane({ id: 'lane-2', name: 'In Progress', position: 1 })]);

    expect(screen.getByRole('button', { name: 'Add card to Backlog' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Add card to In Progress' })).toBeInTheDocument();
  });

  test('clicking the named button adds a card to that lane and does not collapse it', async () => {
    // Arrange
    const user = userEvent.setup();
    const { onAddCard, onToggleCollapse } = renderLanes([
      makeLane(),
      makeLane({ id: 'lane-2', name: 'In Progress', position: 1 }),
    ]);

    // Act
    await user.click(screen.getByRole('button', { name: 'Add card to In Progress' }));

    // Assert
    expect(onAddCard).toHaveBeenCalledTimes(1);
    expect(onAddCard).toHaveBeenCalledWith('In Progress');
    expect(onToggleCollapse).not.toHaveBeenCalled();
  });

  test('Enter on the focused button adds a card', async () => {
    // Arrange
    const user = userEvent.setup();
    const { onAddCard } = renderLanes([makeLane()]);
    screen.getByRole('button', { name: 'Add card to Backlog' }).focus();

    // Act
    await user.keyboard('{Enter}');

    // Assert
    expect(onAddCard).toHaveBeenCalledWith('Backlog');
  });

  test('a press and drag that starts on the header starts a lane drag', () => {
    const { onDragStart } = renderLanes([makeLane()]);

    pressAndMove(screen.getByRole('heading', { name: 'Backlog' }));

    expect(onDragStart).toHaveBeenCalledTimes(1);
  });

  test('a press and drag that starts on the add-card button does not start a lane drag', () => {
    const { onDragStart } = renderLanes([makeLane()]);

    pressAndMove(screen.getByRole('button', { name: 'Add card to Backlog' }));

    expect(onDragStart).not.toHaveBeenCalled();
  });

  test('a touch press on the add-card button does not reach the header drag listeners', () => {
    // Arrange
    const onParentTouchStart = vi.fn();
    render(
      <div onTouchStart={onParentTouchStart}>
        <BoardDnd>
          <LaneColumn
            lane={makeLane()}
            cards={[]}
            onCardClick={vi.fn()}
            onAddCard={vi.fn()}
            activeCardId={null}
            isLaneDragging={false}
            sizeMap={new Map()}
            enrichedCardMap={new Map()}
            isCollapsed={false}
            onToggleCollapse={vi.fn()}
          />
        </BoardDnd>
      </div>,
    );

    // Act
    fireEvent.touchStart(screen.getByRole('button', { name: 'Add card to Backlog' }));

    // Assert
    expect(onParentTouchStart).not.toHaveBeenCalled();
  });
});
