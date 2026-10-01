import { useNavigate } from 'react-router-dom';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import type { Board } from '@/types';

type BoardSwitcherProps = {
  boards: Board[];
  currentSlug?: string;
};

export function BoardSwitcher({ boards, currentSlug }: BoardSwitcherProps) {
  const navigate = useNavigate();
  const currentBoard = boards.find((b) => b.slug === currentSlug);

  return (
    <Select value={currentSlug ?? ''} onValueChange={(v) => navigate(`/boards/${v}`)}>
      {/* Named for what it does, so it is announced as a board picker and not only by the
          board it currently shows. A name cut off here is shown in full in the open list.
          From lg up the header has room to spare, so the cap rises: 12rem fits a name like
          "Platform Operations" with the search beside it still wide enough for its full
          placeholder at 1024px (a longer name there costs the search its shorter placeholder,
          as narrower screens already show), and from xl 16rem shows about 25 characters. */}
      <SelectTrigger
        size="sm"
        aria-label="Board"
        className="min-w-[7rem] max-w-[10rem] flex-1 lg:max-w-[12rem] xl:max-w-[16rem]"
      >
        <SelectValue>{currentBoard?.name ?? 'Select board'}</SelectValue>
      </SelectTrigger>
      {/* The trigger is narrow, so the open list sizes to the longest board name
          instead: an item at the trigger's width would wrap a long name onto
          many lines. It is never narrower than the trigger, and is capped so it
          stays on a phone screen; past the cap, names wrap inside their item. */}
      <SelectContent className="w-max max-w-[min(20rem,calc(100vw-2rem))] min-w-(--anchor-width)">
        {boards.map((b) => (
          <SelectItem key={b.id} value={b.slug}>
            {b.name}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}
