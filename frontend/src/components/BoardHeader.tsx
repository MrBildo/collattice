import { BoardSwitcher } from '@/components/BoardSwitcher';
import { CollatticeLogo } from '@/components/CollatticeLogo';
import { GearMenu } from '@/components/GearMenu';
import { SearchCommand } from '@/components/SearchCommand';
import { Button } from '@/components/ui/button';
import type { Board, VersionStatus } from '@/types';
import type { Role } from '@/lib/roles';

type BoardHeaderProps = {
  boards: Board[];
  currentSlug?: string;
  boardName?: string;
  isAdmin: boolean;
  version?: string;
  versionStatus?: VersionStatus;
  currentUserName?: string;
  currentUserRole?: Role;
  onNewCard: () => void;
  onBoardSettings: () => void;
  onGlobalAdmin: () => void;
  onLogout: () => void;
};

export function BoardHeader({
  boards,
  currentSlug,
  boardName,
  isAdmin,
  version,
  versionStatus,
  currentUserName,
  currentUserRole,
  onNewCard,
  onBoardSettings,
  onGlobalAdmin,
  onLogout,
}: BoardHeaderProps) {
  return (
    <header className="flex h-14 shrink-0 items-center gap-x-3 border-b border-border px-4">
      {/* Left region — logo + board switcher. From xs up it grows equally with
          the right region but never shrinks below its own content, so the center
          region is the genuine free space between the two side clusters and the
          search can page-center without overlapping the logo/switcher. Below xs
          there is no search, and on the narrowest phones the logo and switcher
          together are wider than the room beside the menu, so the region starts
          from its content's width and may shrink below it: both give way rather
          than push the menu off the screen. */}
      <div className="flex min-w-0 flex-auto items-center gap-x-3 xs:min-w-auto xs:flex-1">
        {/* Logo — may narrow on a small phone, never below a legible 6rem */}
        <CollatticeLogo className="w-32 min-w-24 shrink xs:w-48 xs:shrink-0" />
        {/* Board switcher — always inline. A flex box, so the trigger narrows
            with it, and its floor matches the trigger's own minimum width, so a
            narrow screen shortens the logo and the board name together instead
            of letting the trigger spill over the menu. */}
        {boards.length > 1 && (
          <div className="flex min-w-[7rem] shrink">
            <BoardSwitcher boards={boards} currentSlug={currentSlug} />
          </div>
        )}
        {boards.length === 1 && boardName && (
          <span className="hidden max-w-[10rem] truncate text-sm font-medium text-muted-foreground xs:inline">
            {boardName}
          </span>
        )}
      </div>
      {/* Center region — search, hidden on mobile, visible at xs+. Takes the
          free space between the side clusters and centers the search within it;
          SearchCommand keeps its own w-full max-w-md cap, so it shrinks (never
          overlaps) when the free space is tighter than its cap. Below xs the
          whole region is gone, so an empty box doesn't cost the row a gap. */}
      <div className="hidden min-w-0 flex-1 basis-0 justify-center xs:flex xs:px-4">
        <div className="flex w-full justify-center">
          <SearchCommand />
        </div>
      </div>
      {/* Right region — actions. Mirrors the left region: grows equally but
          never shrinks below its content, justified to the end. */}
      <div className="flex flex-1 items-center justify-end gap-2">
        {/* + New Card: xs+ only */}
        <Button onClick={onNewCard} className="hidden xs:inline-flex">
          + New Card
        </Button>
        {/* Board Settings: lg+ only (admin) */}
        {isAdmin && (
          <Button variant="outline" onClick={onBoardSettings} className="hidden lg:inline-flex">
            Board Settings
          </Button>
        )}
        {/* Gear menu — always visible, main menu across all tiers */}
        <GearMenu
          isAdmin={isAdmin}
          version={version}
          versionStatus={versionStatus}
          currentUserName={currentUserName}
          currentUserRole={currentUserRole}
          onNewCard={onNewCard}
          onBoardSettings={onBoardSettings}
          onGlobalAdmin={onGlobalAdmin}
          onLogout={onLogout}
        />
      </div>
    </header>
  );
}
