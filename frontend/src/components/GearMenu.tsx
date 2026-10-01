import { useEffect, useState } from 'react';
import { ArrowUpCircle, HelpCircle, Moon, Settings, Sun, X } from 'lucide-react';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { Badge } from '@/components/ui/badge';
import { Button, buttonVariants } from '@/components/ui/button';
import { ROLES, type Role } from '@/lib/roles';
import type { VersionStatus } from '@/types';
import { compareVersionCores } from '@/lib/semver';
import { cn } from '@/lib/utils';

// Per-version dismissal: the operator dismisses a specific available version, not all
// future updates. The dot reappears when a newer version than the dismissed one is detected,
// because the dismissed value is an exact string match against the current `latest`.
const DISMISSED_VERSION_KEY = 'collattice-dismissed-update';

function getDismissedVersion(): string | null {
  if (typeof window === 'undefined') return null;
  return localStorage.getItem(DISMISSED_VERSION_KEY);
}

function dismissVersion(version: string) {
  localStorage.setItem(DISMISSED_VERSION_KEY, version);
}

const ROLE_LABELS: Record<Role, string> = {
  [ROLES.Administrator]: 'Administrator',
  [ROLES.Human]: 'Human',
  [ROLES.Agent]: 'Agent',
  [ROLES['Agent Admin']]: 'Agent Admin',
};

function roleBadgeClassName(role: Role): string {
  return role === ROLES.Administrator || role === ROLES['Agent Admin']
    ? 'bg-primary/15 text-primary'
    : role === ROLES.Agent
      ? 'bg-accent/15 text-accent'
      : '';
}

function getStoredTheme(): 'light' | 'dark' {
  if (typeof window === 'undefined') return 'light';
  return (localStorage.getItem('collattice-theme') as 'light' | 'dark') ?? 'light';
}

function applyTheme(theme: 'light' | 'dark') {
  document.documentElement.setAttribute('data-theme', theme);
  localStorage.setItem('collattice-theme', theme);
}

type GearMenuProps = {
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

export function GearMenu({
  isAdmin,
  version,
  versionStatus,
  currentUserName,
  currentUserRole,
  onNewCard,
  onBoardSettings,
  onGlobalAdmin,
  onLogout,
}: GearMenuProps) {
  const [theme, setTheme] = useState<'light' | 'dark'>(getStoredTheme);
  const [dismissedVersion, setDismissedVersion] = useState<string | null>(getDismissedVersion);

  useEffect(() => {
    applyTheme(theme);
  }, [theme]);

  const toggleTheme = () => {
    setTheme((prev) => (prev === 'light' ? 'dark' : 'light'));
  };

  // The current version shown in the footer prefers the plain /version string — it carries a
  // shorter cache lifetime than the update-status payload, so it reflects a freshly deployed
  // build sooner. Falls back to the status payload's `current` field when /version hasn't
  // resolved yet.
  const currentVersion = version ?? versionStatus?.current;

  // The update row reads "v<yours> → v<newer> available", but its two halves come from
  // different queries: "yours" from /version, the target from the update-status payload,
  // which carries a much longer cache lifetime. Right after an upgrade the status payload can
  // still be advertising the release the operator just installed — "v1.17.0 → v1.17.0
  // available", a sentence that contradicts itself. So only advertise an update the displayed
  // version is actually behind.
  //
  // Comparing by release rather than by string is what makes that safe in both directions: a
  // pre-release build is not nagged to "upgrade" to the release it is a candidate for, and a
  // self-consistent status payload is never suppressed, since the server decided "update
  // available" by that same comparison. The check can only fire when the two halves came from
  // different snapshots. An unparseable version leaves the row visible — the server has
  // asserted an update exists, and silently withholding an upgrade notice is worse than
  // showing an odd-looking one.
  const latestComparedToDisplayed = compareVersionCores(versionStatus?.latest, currentVersion);
  const hasNewerVersion = latestComparedToDisplayed === null || latestComparedToDisplayed > 0;

  // An update is "showable" only when the backend reports one, it is genuinely newer than
  // what we are displaying, AND the operator hasn't dismissed this exact latest version. The
  // dot and the link row share this gate, so a dismiss clears both at once and a later, newer
  // `latest` re-shows both — and nothing advertises an update the menu then declines to name.
  const updateShowable =
    versionStatus?.updateAvailable === true &&
    versionStatus.latest !== null &&
    versionStatus.latest !== dismissedVersion &&
    hasNewerVersion;

  const handleDismiss = () => {
    if (versionStatus?.latest) {
      dismissVersion(versionStatus.latest);
      setDismissedVersion(versionStatus.latest);
    }
  };

  // The trigger is icon-only, so it needs a name of its own; on a phone it is the whole main
  // menu. The update dot's message is spoken as part of that name. A label on the dot itself
  // is not reliable: ARIA does not allow naming a plain span, so whether it is read, and as
  // what, varies by browser and screen reader (Chrome read it as the whole button's name).
  const triggerLabel = updateShowable ? 'Main menu, update available' : 'Main menu';

  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        aria-label={triggerLabel}
        className="relative inline-flex h-9 w-9 items-center justify-center rounded-md text-muted-foreground hover:bg-secondary hover:text-foreground"
      >
        <Settings className="h-4 w-4" />
        {/* Update-available dot — rides the always-visible gear icon so it shows on every
            tier (mobile included) without opening the menu. */}
        {updateShowable && (
          <span
            aria-hidden="true"
            data-update-dot
            className="absolute right-1.5 top-1.5 h-2 w-2 rounded-full bg-accent ring-2 ring-background"
          />
        )}
      </DropdownMenuTrigger>
      <DropdownMenuContent
        align="end"
        className="min-w-[16.25rem] max-w-[min(20rem,calc(100vw-1.5rem))]"
      >
        {/* Current user identity — shown while resolved; hidden during pending */}
        {currentUserName !== undefined && currentUserRole !== undefined && (
          <>
            <div className="flex items-center gap-2 px-2 py-1.5">
              <span
                className="min-w-0 flex-1 truncate text-sm font-medium text-foreground"
                title={currentUserName}
              >
                {currentUserName}
              </span>
              <Badge
                variant="secondary"
                className={cn('shrink-0 text-xs', roleBadgeClassName(currentUserRole))}
              >
                {ROLE_LABELS[currentUserRole] ?? `Role ${currentUserRole}`}
              </Badge>
            </div>
            <DropdownMenuSeparator />
          </>
        )}
        {/* + New Card: mobile only (hidden at md+) */}
        <DropdownMenuItem onClick={onNewCard} className="xs:hidden">
          + New Card
        </DropdownMenuItem>
        {/* Board Settings: mobile + md, hidden at lg (admin only) */}
        {isAdmin && (
          <DropdownMenuItem onClick={onBoardSettings} className="lg:hidden">
            Board Settings
          </DropdownMenuItem>
        )}
        {/* Admin: all tiers (admin only) */}
        {isAdmin && <DropdownMenuItem onClick={onGlobalAdmin}>Admin</DropdownMenuItem>}
        {/* Separator — visible when any action item above is shown */}
        <DropdownMenuSeparator className={isAdmin ? '' : 'xs:hidden'} />
        {/* Theme toggle: all tiers */}
        <DropdownMenuItem onClick={toggleTheme}>
          <span className="flex items-center gap-2">
            {theme === 'light' ? (
              <>
                <Moon className="h-3.5 w-3.5" /> Dark mode
              </>
            ) : (
              <>
                <Sun className="h-3.5 w-3.5" /> Light mode
              </>
            )}
          </span>
        </DropdownMenuItem>
        {/* Help / User Guide: all tiers — links to published docs in a new tab */}
        <DropdownMenuItem
          onClick={() =>
            window.open(
              'https://github.com/MrBildo/collattice/blob/main/docs/user-guide.md',
              '_blank',
              'noopener,noreferrer',
            )
          }
        >
          <span className="flex items-center gap-2">
            <HelpCircle className="h-3.5 w-3.5" /> Help / User Guide
          </span>
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuItem onClick={onLogout}>Logout</DropdownMenuItem>
        {updateShowable ? (
          <>
            <DropdownMenuSeparator />
            <div className="flex items-center gap-1 px-1 py-0.5">
              <a
                href={versionStatus?.releaseUrl ?? undefined}
                target="_blank"
                rel="noopener noreferrer"
                className={cn(
                  buttonVariants({ variant: 'ghost', size: 'sm' }),
                  'min-w-0 flex-1 justify-start text-xs text-accent hover:text-accent',
                )}
              >
                <ArrowUpCircle className="h-3.5 w-3.5 shrink-0" />
                <span className="truncate">
                  v{currentVersion} &rarr; v{versionStatus?.latest} available
                </span>
              </a>
              <Button
                variant="ghost"
                size="icon-xs"
                aria-label="Dismiss update reminder"
                onClick={handleDismiss}
                className="shrink-0 text-muted-foreground"
              >
                <X className="h-3.5 w-3.5" />
              </Button>
            </div>
          </>
        ) : (
          currentVersion && (
            <>
              <DropdownMenuSeparator />
              <div className="px-1.5 py-1 text-xs text-muted-foreground">v{currentVersion}</div>
            </>
          )
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
