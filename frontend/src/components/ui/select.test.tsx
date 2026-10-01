import { describe, test, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Label } from '@/components/ui/label';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';

function FruitItems() {
  return (
    <SelectContent>
      <SelectItem value="apple">Apple</SelectItem>
      <SelectItem value="pear">Pear</SelectItem>
    </SelectContent>
  );
}

describe('Select', () => {
  test('an aria-label on the Select names the trigger and the open list', async () => {
    // Arrange
    const user = userEvent.setup();
    render(
      <Select aria-label="Fruit" defaultValue="apple">
        <SelectTrigger>
          <SelectValue />
        </SelectTrigger>
        <FruitItems />
      </Select>,
    );

    // Act
    await user.click(screen.getByRole('combobox', { name: 'Fruit' }));

    // Assert
    expect(await screen.findByRole('listbox', { name: 'Fruit' })).toBeInTheDocument();
  });

  test('a label the Select points at with aria-labelledby names the trigger and the open list', async () => {
    // Arrange
    const user = userEvent.setup();
    render(
      <>
        <Label id="fruit-label">Fruit</Label>
        <Select aria-labelledby="fruit-label" defaultValue="apple">
          <SelectTrigger>
            <SelectValue />
          </SelectTrigger>
          <FruitItems />
        </Select>
      </>,
    );

    // Act
    await user.click(screen.getByRole('combobox', { name: 'Fruit' }));

    // Assert
    expect(await screen.findByRole('listbox', { name: 'Fruit' })).toBeInTheDocument();
  });

  test('two selects on one page each name their own list', async () => {
    // Arrange
    const user = userEvent.setup();
    render(
      <>
        <Select aria-label="Fruit" defaultValue="apple">
          <SelectTrigger>
            <SelectValue />
          </SelectTrigger>
          <FruitItems />
        </Select>
        <Select aria-label="Dessert" defaultValue="apple">
          <SelectTrigger>
            <SelectValue />
          </SelectTrigger>
          <FruitItems />
        </Select>
      </>,
    );

    // Act
    await user.click(screen.getByRole('combobox', { name: 'Dessert' }));

    // Assert
    expect(await screen.findByRole('listbox', { name: 'Dessert' })).toBeInTheDocument();
  });
});
