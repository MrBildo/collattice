import { describe, test, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { GlobalAdminPanel } from './GlobalAdminPanel';
import { ROLES } from '@/lib/roles';

vi.mock('@/lib/api', () => ({
  fetchBoards: vi.fn().mockResolvedValue([]),
  createBoard: vi.fn(),
  updateBoard: vi.fn(),
  deleteBoard: vi.fn(),
  fetchUsers: vi.fn(),
  createUser: vi.fn(),
  updateUser: vi.fn(),
  deactivateUser: vi.fn(),
  // The Webhooks tab is keepMounted, so its data hooks fire when the panel
  // mounts — stub the fetchers so the queries resolve cleanly.
  fetchWebhookSubscriptions: vi.fn().mockResolvedValue([]),
  fetchWebhookDeliveries: vi
    .fn()
    .mockResolvedValue({ items: [], totalCount: 0, offset: 0, limit: 200 }),
  fetchWebhookStatus: vi.fn().mockResolvedValue({
    enabled: true,
    allowPrivateNetworkTargets: false,
    subscriptionCount: 0,
    enabledSubscriptionCount: 0,
  }),
  createWebhookSubscription: vi.fn(),
  updateWebhookSubscription: vi.fn(),
  deleteWebhookSubscription: vi.fn(),
  testWebhookSubscription: vi.fn(),
}));

import { fetchUsers, updateUser } from '@/lib/api';

const mockFetchUsers = vi.mocked(fetchUsers);
const mockUpdateUser = vi.mocked(updateUser);

function makeUser(
  overrides: Partial<{
    id: string;
    name: string;
    role: number;
    isActive: boolean;
    authKey: string;
  }> = {},
) {
  return {
    id: 'user-1',
    name: 'Original Name',
    role: ROLES.Human,
    authKey: 'authkey-abc',
    isActive: true,
    ...overrides,
  };
}

function renderPanel() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
  }
  return render(<GlobalAdminPanel open={true} onOpenChange={() => {}} />, { wrapper: Wrapper });
}

beforeEach(() => {
  vi.clearAllMocks();
  mockUpdateUser.mockResolvedValue(makeUser());
});

describe('GlobalAdminPanel — UsersTab edit flow', () => {
  test('renders role label for every known ROLES value', async () => {
    mockFetchUsers.mockResolvedValue([
      makeUser({ id: 'u-admin', name: 'Admin User', role: ROLES.Administrator }),
      makeUser({ id: 'u-human', name: 'Human User', role: ROLES.Human }),
      makeUser({ id: 'u-agent', name: 'Agent User', role: ROLES.Agent }),
      makeUser({ id: 'u-agent-admin', name: 'Agent Admin User', role: ROLES['Agent Admin'] }),
    ]);

    renderPanel();
    const user = userEvent.setup();
    await user.click(screen.getByRole('tab', { name: /users/i }));

    await waitFor(() => {
      expect(screen.getByText('Admin User')).toBeInTheDocument();
    });
    expect(screen.getByText('Human User')).toBeInTheDocument();
    expect(screen.getByText('Agent User')).toBeInTheDocument();
    expect(screen.getByText('Agent Admin User')).toBeInTheDocument();
    // Role badges — derived from the ROLES const. Each role label appears at
    // least once as a user-row badge and once as a Select option in the
    // "Add User" form (the select renders its items in the DOM even when
    // closed). Asserting >= 1 occurrence covers both.
    expect(screen.getAllByText('Administrator').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Human').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Agent').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Agent Admin').length).toBeGreaterThanOrEqual(1);
  });

  test('both role selects are named, the new-user one by its Role label', async () => {
    // Arrange
    mockFetchUsers.mockResolvedValue([makeUser({ name: 'Jane Doe' })]);
    renderPanel();
    const user = userEvent.setup();
    await user.click(screen.getByRole('tab', { name: /users/i }));
    await screen.findByText('Jane Doe');

    // Act
    await user.click(screen.getByRole('button', { name: /edit jane doe/i }));

    // Assert
    expect(screen.getByRole('combobox', { name: 'Role' })).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'User role' })).toBeInTheDocument();
  });

  test('clicking edit reveals the name input pre-filled with current name', async () => {
    mockFetchUsers.mockResolvedValue([makeUser({ name: 'Jane Doe' })]);

    renderPanel();
    const user = userEvent.setup();
    await user.click(screen.getByRole('tab', { name: /users/i }));

    await waitFor(() => {
      expect(screen.getByText('Jane Doe')).toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: /edit jane doe/i }));

    const nameInput = await screen.findByLabelText(/user name/i);
    expect(nameInput).toHaveValue('Jane Doe');
  });

  test('saving with a changed name calls updateUser with name patch only', async () => {
    mockFetchUsers.mockResolvedValue([makeUser({ id: 'u-1', name: 'Old', role: ROLES.Human })]);

    renderPanel();
    const user = userEvent.setup();
    await user.click(screen.getByRole('tab', { name: /users/i }));

    await waitFor(() => {
      expect(screen.getByText('Old')).toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: /edit old/i }));

    const nameInput = await screen.findByLabelText(/user name/i);
    await user.clear(nameInput);
    await user.type(nameInput, 'New Name');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => {
      expect(mockUpdateUser).toHaveBeenCalledWith('u-1', { name: 'New Name' });
    });
    // Role unchanged — should not be sent.
    expect(mockUpdateUser.mock.calls[0][1]).not.toHaveProperty('role');
  });

  test('saving with no changes does not call updateUser', async () => {
    mockFetchUsers.mockResolvedValue([makeUser({ name: 'Unchanged' })]);

    renderPanel();
    const user = userEvent.setup();
    await user.click(screen.getByRole('tab', { name: /users/i }));

    await waitFor(() => {
      expect(screen.getByText('Unchanged')).toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: /edit unchanged/i }));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(mockUpdateUser).not.toHaveBeenCalled();
  });

  test('saving with empty name surfaces a validation error and does not call updateUser', async () => {
    mockFetchUsers.mockResolvedValue([makeUser({ name: 'Original' })]);

    renderPanel();
    const user = userEvent.setup();
    await user.click(screen.getByRole('tab', { name: /users/i }));

    await waitFor(() => {
      expect(screen.getByText('Original')).toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: /edit original/i }));

    const nameInput = await screen.findByLabelText(/user name/i);
    await user.clear(nameInput);
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(mockUpdateUser).not.toHaveBeenCalled();
    expect(screen.getByText(/name cannot be empty/i)).toBeInTheDocument();
  });

  test('cancel exits edit mode without calling updateUser', async () => {
    mockFetchUsers.mockResolvedValue([makeUser({ name: 'Stay Put' })]);

    renderPanel();
    const user = userEvent.setup();
    await user.click(screen.getByRole('tab', { name: /users/i }));

    await waitFor(() => {
      expect(screen.getByText('Stay Put')).toBeInTheDocument();
    });

    await user.click(screen.getByRole('button', { name: /edit stay put/i }));

    const nameInput = await screen.findByLabelText(/user name/i);
    await user.clear(nameInput);
    await user.type(nameInput, 'Changed');
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(mockUpdateUser).not.toHaveBeenCalled();
    // Row returns to non-edit display.
    await waitFor(() => {
      expect(screen.queryByLabelText(/user name/i)).not.toBeInTheDocument();
    });
    expect(screen.getByText('Stay Put')).toBeInTheDocument();
  });
});
