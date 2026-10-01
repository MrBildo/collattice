import { describe, test, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { VersionStatus } from '@/types';
import { GearMenu } from './GearMenu';

const baseProps = {
  isAdmin: false,
  onNewCard: vi.fn(),
  onBoardSettings: vi.fn(),
  onGlobalAdmin: vi.fn(),
  onLogout: vi.fn(),
};

// A payload reporting that an upgrade is available from 1.16.0. In the tests below that pass a
// separate `version` prop, this doubles as the stale payload an operator sees just after
// upgrading — one that is still advertising the release they have already installed.
function statusWithUpdate(latest = '1.17.0'): VersionStatus {
  return {
    current: '1.16.0',
    latest,
    updateAvailable: true,
    releaseUrl: 'https://example.test/release',
    lastChecked: new Date().toISOString(),
  };
}

function statusUpToDate(): VersionStatus {
  return {
    current: '1.16.0',
    latest: '1.16.0',
    updateAvailable: false,
    releaseUrl: 'https://example.test/release',
    lastChecked: new Date().toISOString(),
  };
}

// The update state is checked twice: through the trigger's accessible name, which is what a
// screen reader announces, and through the drawn dot, which is what a sighted operator sees.
// A label on the dot alone would satisfy a label query and still not be announced.
function expectUpdateAnnounced() {
  const trigger = screen.getByRole('button', { name: 'Main menu, update available' });
  expect(trigger.querySelector('[data-update-dot]')).not.toBeNull();
}

function expectNoUpdateAnnounced() {
  const trigger = screen.getByRole('button', { name: 'Main menu' });
  expect(trigger.querySelector('[data-update-dot]')).toBeNull();
}

async function openMenu(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('button', { name: /^Main menu/ }));
}

describe('GearMenu update indicator', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  test('announces the update in the trigger name and draws the dot when an update is available', () => {
    render(<GearMenu {...baseProps} versionStatus={statusWithUpdate()} />);

    expectUpdateAnnounced();
  });

  test('names the trigger Main menu and draws no dot when up to date', () => {
    render(<GearMenu {...baseProps} versionStatus={statusUpToDate()} />);

    expectNoUpdateAnnounced();
  });

  test('shows the update link row with the release URL inside the menu', async () => {
    const user = userEvent.setup();
    render(<GearMenu {...baseProps} versionStatus={statusWithUpdate()} />);

    await openMenu(user);
    // The dropdown content is rendered once the trigger is opened.
    const link = await screen.findByRole('link', { name: /v1.16.0.*v1.17.0 available/i });

    expect(link).toHaveAttribute('href', 'https://example.test/release');
    expect(link).toHaveAttribute('target', '_blank');
  });

  test('dismissing the update hides the dot and persists per-version', async () => {
    const user = userEvent.setup();
    const { rerender } = render(<GearMenu {...baseProps} versionStatus={statusWithUpdate()} />);

    await openMenu(user);
    await user.click(await screen.findByLabelText('Dismiss update reminder'));

    expect(localStorage.getItem('collattice-dismissed-update')).toBe('1.17.0');
    expectNoUpdateAnnounced();

    // A newer version than the dismissed one re-shows the dot.
    rerender(<GearMenu {...baseProps} versionStatus={statusWithUpdate('1.18.0')} />);
    expectUpdateAnnounced();
  });

  test('a previously-dismissed version stays hidden across mounts', () => {
    localStorage.setItem('collattice-dismissed-update', '1.17.0');

    render(<GearMenu {...baseProps} versionStatus={statusWithUpdate('1.17.0')} />);

    expectNoUpdateAnnounced();
  });

  test('falls back to the plain version footer when no status is provided', async () => {
    const user = userEvent.setup();
    render(<GearMenu {...baseProps} version="1.16.0" />);

    await openMenu(user);

    expect(await screen.findByText('v1.16.0')).toBeInTheDocument();
    expectNoUpdateAnnounced();
  });

  test('prefers the fresher /version value over a stale status payload for the footer', async () => {
    const user = userEvent.setup();
    // version (5-minute staleTime) has already caught up to a new deploy; versionStatus
    // (30-minute staleTime) still reflects the pre-deploy build. The footer must show the
    // fresher value, not the stale one.
    render(<GearMenu {...baseProps} version="1.16.1" versionStatus={statusUpToDate()} />);

    await openMenu(user);

    expect(await screen.findByText('v1.16.1')).toBeInTheDocument();
    expect(screen.queryByText('v1.16.0')).not.toBeInTheDocument();
  });

  test('prefers the fresher /version value in the update-available link text too', async () => {
    const user = userEvent.setup();
    render(<GearMenu {...baseProps} version="1.16.1" versionStatus={statusWithUpdate()} />);

    await openMenu(user);

    expect(
      await screen.findByRole('link', { name: /v1.16.1.*v1.17.0 available/i }),
    ).toBeInTheDocument();
  });

  test('falls back to the status payload current version when /version has not resolved yet', async () => {
    const user = userEvent.setup();
    render(<GearMenu {...baseProps} versionStatus={statusUpToDate()} />);

    await openMenu(user);

    expect(await screen.findByText('v1.16.0')).toBeInTheDocument();
  });

  test('suppresses the update row once the displayed version is the advertised release', async () => {
    const user = userEvent.setup();
    // The operator has upgraded to 1.17.0 and /version already says so, but the slower status
    // query is still advertising 1.17.0 as the upgrade. Advertising it would read
    // "v1.17.0 -> v1.17.0 available".
    render(<GearMenu {...baseProps} version="1.17.0" versionStatus={statusWithUpdate('1.17.0')} />);

    expectNoUpdateAnnounced();

    await openMenu(user);

    expect(await screen.findByText('v1.17.0')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /available/i })).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Dismiss update reminder')).not.toBeInTheDocument();
  });

  test('suppresses the update row on a pre-release build of the advertised release', async () => {
    const user = userEvent.setup();
    // /version keeps the pre-release suffix that the status payload drops, so the two
    // spellings of the same release are not equal as strings — only as versions.
    render(
      <GearMenu {...baseProps} version="1.17.0-rc1" versionStatus={statusWithUpdate('1.17.0')} />,
    );

    expectNoUpdateAnnounced();

    await openMenu(user);

    expect(await screen.findByText('v1.17.0-rc1')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /available/i })).not.toBeInTheDocument();
  });

  test('suppresses the update row when the displayed version is ahead of the advertised release', async () => {
    const user = userEvent.setup();
    render(<GearMenu {...baseProps} version="1.18.0" versionStatus={statusWithUpdate('1.17.0')} />);

    expectNoUpdateAnnounced();

    await openMenu(user);

    expect(await screen.findByText('v1.18.0')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /available/i })).not.toBeInTheDocument();
  });

  test('still shows the update row when the displayed version is genuinely behind', async () => {
    const user = userEvent.setup();
    render(<GearMenu {...baseProps} version="1.17.0" versionStatus={statusWithUpdate('1.18.0')} />);

    expectUpdateAnnounced();

    await openMenu(user);

    expect(
      await screen.findByRole('link', { name: /v1.17.0.*v1.18.0 available/i }),
    ).toBeInTheDocument();
  });

  test('still shows the update row when the displayed version cannot be compared', async () => {
    const user = userEvent.setup();
    // A build with no stamped version reports a four-part assembly version, which is not a
    // release number the comparison can read. The server has asserted an update exists, so it
    // stays advertised rather than being silently withheld.
    render(
      <GearMenu {...baseProps} version="1.0.0.0" versionStatus={statusWithUpdate('1.17.0')} />,
    );

    expectUpdateAnnounced();

    await openMenu(user);

    expect(
      await screen.findByRole('link', { name: /v1.0.0.0.*v1.17.0 available/i }),
    ).toBeInTheDocument();
  });

  test('still shows the update row when /version is the staler of the two sources', async () => {
    const user = userEvent.setup();
    // Suppression keys on the displayed version against the advertised one, not on the two
    // payloads disagreeing: here they disagree and the update is still real.
    const freshStatus: VersionStatus = {
      current: '1.17.0',
      latest: '1.18.0',
      updateAvailable: true,
      releaseUrl: 'https://example.test/release',
      lastChecked: new Date().toISOString(),
    };
    render(<GearMenu {...baseProps} version="1.16.0" versionStatus={freshStatus} />);

    expectUpdateAnnounced();

    await openMenu(user);

    expect(
      await screen.findByRole('link', { name: /v1.16.0.*v1.18.0 available/i }),
    ).toBeInTheDocument();
  });
});
