import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react';
import { Check, Tag } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { cn, getContrastColor, getReadableColor } from '@/lib/utils';
import type { Label } from '@/types';

type AssignedLabel = Pick<Label, 'id' | 'name' | 'color'>;

type LabelPickerTrigger = {
  // The element the popover trigger renders as (e.g. a compact icon Button).
  render: React.ReactElement;
  content: React.ReactNode;
};

type LabelPickerProps = {
  allLabels: Label[];
  assignedLabels: AssignedLabel[];
  onAdd: (labelId: string) => void;
  onRemove: (labelId: string) => void;
  // Replaces the default trigger — an outline button listing the assigned
  // labels — where the picker lives somewhere that trigger would not fit.
  trigger?: LabelPickerTrigger;
  // True while the board's label list is still being fetched, so the list
  // says "loading" instead of claiming there are no labels.
  isLoading?: boolean;
  onOpenChange?: (isOpen: boolean) => void;
};

export function LabelPicker({
  allLabels,
  assignedLabels,
  onAdd,
  onRemove,
  trigger,
  isLoading = false,
  onOpenChange,
}: LabelPickerProps) {
  const [isOpen, setIsOpen] = useState(false);
  const [filter, setFilter] = useState('');
  const [focusedIndex, setFocusedIndex] = useState(-1);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  // Many pickers can exist on one page (one per board tile), so the listbox and
  // option ids must be unique per instance for aria-controls/activedescendant.
  const idPrefix = useId();
  const listboxId = `${idPrefix}-listbox`;
  const buildOptionId = (labelId: string) => `${idPrefix}-option-${labelId}`;

  const assignedIds = useMemo(() => new Set(assignedLabels.map((l) => l.id)), [assignedLabels]);

  const filtered = useMemo(
    () =>
      filter
        ? allLabels.filter((l) => l.name.toLowerCase().includes(filter.toLowerCase()))
        : allLabels,
    [allLabels, filter],
  );

  const toggle = useCallback(
    (id: string) => {
      if (assignedIds.has(id)) {
        onRemove(id);
      } else {
        onAdd(id);
      }
    },
    [assignedIds, onAdd, onRemove],
  );

  const handleFilterChange = useCallback((value: string) => {
    setFilter(value);
    setFocusedIndex(-1);
  }, []);

  useEffect(() => {
    if (focusedIndex >= 0 && listRef.current) {
      const items = listRef.current.querySelectorAll('[role="option"]');
      if (items[focusedIndex]) {
        (items[focusedIndex] as HTMLElement).scrollIntoView({ block: 'nearest' });
      }
    }
  }, [focusedIndex]);

  const handleOpenChange = (open: boolean) => {
    setIsOpen(open);
    onOpenChange?.(open);
    if (open) {
      setFilter('');
      setFocusedIndex(-1);
      setTimeout(() => inputRef.current?.focus(), 0);
    }
  };

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'ArrowDown') {
        e.preventDefault();
        setFocusedIndex((prev) => Math.min(prev + 1, filtered.length - 1));
      } else if (e.key === 'ArrowUp') {
        e.preventDefault();
        setFocusedIndex((prev) => Math.max(prev - 1, 0));
      } else if ((e.key === 'Enter' || e.key === ' ') && focusedIndex >= 0) {
        e.preventDefault();
        toggle(filtered[focusedIndex].id);
      } else if (e.key === 'Escape') {
        e.preventDefault();
        setIsOpen(false);
        onOpenChange?.(false);
      }
    },
    [filtered, focusedIndex, toggle, onOpenChange],
  );

  return (
    <div className="w-fit">
      <Popover open={isOpen} onOpenChange={handleOpenChange}>
        {trigger ? (
          <PopoverTrigger render={trigger.render}>{trigger.content}</PopoverTrigger>
        ) : (
          <PopoverTrigger
            render={
              <Button
                variant="outline"
                size="sm"
                className="h-auto min-h-8 gap-1.5 px-2.5 py-1"
                aria-haspopup="listbox"
              />
            }
          >
            <Tag className="w-3.5 h-3.5 shrink-0 text-muted-foreground" />
            {assignedLabels.length > 0 ? (
              <span className="flex flex-wrap gap-1">
                {assignedLabels.map((label) => (
                  <Badge
                    key={label.id}
                    variant="secondary"
                    className="rounded-sm px-1.5 py-0 text-xs leading-4"
                    style={{
                      backgroundColor: label.color ?? '#6b7280',
                      color: getContrastColor(label.color),
                      borderColor: label.color ?? '#6b7280',
                    }}
                  >
                    {label.name}
                  </Badge>
                ))}
              </span>
            ) : (
              <span className="text-muted-foreground">Labels</span>
            )}
          </PopoverTrigger>
        )}

        <PopoverContent
          side="bottom"
          align="start"
          className="w-56 gap-0 p-0"
          onKeyDown={handleKeyDown}
        >
          <div className="p-1">
            <Input
              ref={inputRef}
              value={filter}
              onChange={(e) => handleFilterChange(e.target.value)}
              placeholder="Search labels..."
              className="h-7 text-sm"
              aria-label="Search labels"
              role="combobox"
              aria-expanded={true}
              aria-controls={listboxId}
              aria-activedescendant={
                focusedIndex >= 0 && filtered[focusedIndex]
                  ? buildOptionId(filtered[focusedIndex].id)
                  : undefined
              }
            />
          </div>
          <div
            ref={listRef}
            className="max-h-48 overflow-y-auto p-1"
            role="listbox"
            id={listboxId}
            aria-label="Available labels"
            aria-busy={isLoading}
          >
            {filtered.map((label, index) => {
              const selected = assignedIds.has(label.id);
              const focused = index === focusedIndex;
              return (
                <Button
                  key={label.id}
                  id={buildOptionId(label.id)}
                  variant="ghost"
                  size="sm"
                  role="option"
                  aria-selected={selected}
                  onClick={() => toggle(label.id)}
                  className={cn(
                    'relative w-full justify-start gap-2 pr-8',
                    focused && 'bg-accent text-accent-foreground',
                  )}
                >
                  <span
                    className="size-3 shrink-0 rounded-full border"
                    style={{
                      backgroundColor: label.color ?? '#6b7280',
                      borderColor: getReadableColor(label.color),
                    }}
                  />
                  {label.name}
                  {selected && <Check className="absolute right-2 h-4 w-4" />}
                </Button>
              );
            })}
            {filtered.length === 0 && (
              <p className="py-2 text-center text-sm text-muted-foreground">
                {isLoading ? 'Loading labels...' : 'No labels found.'}
              </p>
            )}
          </div>
        </PopoverContent>
      </Popover>
    </div>
  );
}
