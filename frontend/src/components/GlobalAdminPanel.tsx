import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Pencil } from 'lucide-react';
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
} from '@/components/ui/dialog';
import { Tabs, TabsList, TabsTrigger, TabsContent } from '@/components/ui/tabs';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Badge } from '@/components/ui/badge';
import { Separator } from '@/components/ui/separator';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import {
  EditableListContainer,
  EditableListRow,
  EditFormActions,
  ItemActions,
} from '@/components/editable-list';
import { useEditableList } from '@/hooks/use-editable-list';
import { WebhooksTab } from '@/components/WebhooksTab';
import {
  createBoard,
  createUser,
  deactivateUser,
  deleteBoard,
  fetchBoards,
  fetchUsers,
  updateBoard,
  updateUser,
} from '@/lib/api';
import type { UpdateBoardPatch, UpdateUserPatch } from '@/types';
import { ROLES } from '@/lib/roles';
import { queryKeys } from '@/lib/query-keys';
import { QUERY_DEFAULTS } from '@/lib/query-config';

type GlobalAdminPanelProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
};

// Role options derived from the ROLES const so adding a new role
// (e.g. 'Agent Admin') propagates to every selector automatically.
const ROLE_OPTIONS = Object.entries(ROLES).map(([label, value]) => ({
  label,
  value,
}));
const ROLE_MAP: Record<number, string> = Object.fromEntries(
  ROLE_OPTIONS.map(({ label, value }) => [value, label]),
);
const DEFAULT_NEW_ROLE = String(ROLES.Human);

export function GlobalAdminPanel({ open, onOpenChange }: GlobalAdminPanelProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-3xl h-[85vh] flex flex-col overflow-hidden p-6">
        <DialogHeader>
          <DialogTitle>Admin Panel</DialogTitle>
          <DialogDescription>Manage boards, users, and webhooks.</DialogDescription>
        </DialogHeader>

        {/* `flex-1` makes the Tabs fill the DialogContent flex column so the
            pinned `shrink-0` Add footer reaches the dialog's true bottom. Without
            it the Tabs are only as tall as their content — on a short boards/users
            list the footer floats mid-dialog with dead space below it instead of
            pinning. `min-h-0` keeps the inner list's `overflow-y-auto`
            engaging when the list is long. */}
        <Tabs defaultValue="boards" className="mt-2 flex min-h-0 flex-1 flex-col gap-4">
          <TabsList variant="line" className="w-full justify-start gap-2 border-b pb-2">
            <TabsTrigger value="boards">Boards</TabsTrigger>
            <TabsTrigger value="users">Users</TabsTrigger>
            <TabsTrigger value="webhooks">Webhooks</TabsTrigger>
          </TabsList>

          {/* `data-[hidden]:hidden` lets the inactive (keepMounted) panel's
              `display:none` win over `flex` — without it both mounted panels
              flex-grow and the active panel collapses, breaking the inner
              scroll. */}
          <TabsContent
            value="boards"
            keepMounted
            className="flex min-h-0 flex-col p-1 data-[hidden]:hidden"
          >
            <BoardsTab />
          </TabsContent>
          <TabsContent
            value="users"
            keepMounted
            className="flex min-h-0 flex-col p-1 data-[hidden]:hidden"
          >
            <UsersTab />
          </TabsContent>
          <TabsContent
            value="webhooks"
            keepMounted
            className="flex min-h-0 flex-1 flex-col p-1 data-[hidden]:hidden"
          >
            <WebhooksTab />
          </TabsContent>
        </Tabs>
      </DialogContent>
    </Dialog>
  );
}

function BoardsTab() {
  const queryClient = useQueryClient();
  const [newName, setNewName] = useState('');
  const [editName, setEditName] = useState('');
  const list = useEditableList();

  const boardsQuery = useQuery({
    queryKey: queryKeys.boards.all(),
    queryFn: fetchBoards,
    ...QUERY_DEFAULTS.boards,
  });

  // Admin-tab mutations are inline tier — failures surface
  // inline via `list.setDeleteError` → <EditableListContainer>. skipToast keeps
  // the floor quiet; the call site owns the surface.
  const createMutation = useMutation({
    meta: { skipToast: true },
    mutationFn: () => createBoard(newName.trim()),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.boards.all() });
      setNewName('');
    },
    onError: (err) => {
      list.setDeleteError(err instanceof Error ? err.message : 'Failed to create board.');
    },
  });

  const updateMutation = useMutation({
    meta: { skipToast: true },
    mutationFn: ({ id, patch }: { id: string; patch: UpdateBoardPatch }) => updateBoard(id, patch),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.boards.all() });
      list.setEditingId(null);
    },
    onError: (err) => {
      list.setDeleteError(err instanceof Error ? err.message : 'Failed to update board.');
    },
  });

  const deleteMutation = useMutation({
    meta: { skipToast: true },
    mutationFn: (id: string) => deleteBoard(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.boards.all() });
      list.clearDelete();
    },
    onError: () => {
      list.setDeleteError('Cannot delete board — it may still have lanes.');
    },
  });

  const handleCreate = () => {
    if (!newName.trim()) return;
    createMutation.mutate();
  };

  const startEdit = (id: string, name: string) => {
    list.startEdit(id);
    setEditName(name);
  };

  const saveEdit = () => {
    if (!list.editingId) return;
    const board = boardsQuery.data?.find((b) => b.id === list.editingId);
    if (!board) return;
    if (editName.trim() !== board.name) {
      updateMutation.mutate({ id: list.editingId, patch: { name: editName.trim() } });
    } else {
      list.cancelEdit();
    }
  };

  const handleDelete = (id: string) => {
    if (list.confirmDeleteId === id) {
      deleteMutation.mutate(id);
    } else {
      list.confirmDelete(id);
    }
  };

  const boards = boardsQuery.data ?? [];

  return (
    <div className="flex h-full min-h-0 flex-col gap-4">
      {/* Scroll zone — fills remaining height, scrolls internally on long lists. */}
      <div className="min-h-0 flex-1 overflow-y-auto">
        <EditableListContainer error={list.deleteError}>
          {boards.map((board) => (
            <EditableListRow key={board.id}>
              {list.editingId === board.id ? (
                <>
                  <div className="flex flex-1 items-center gap-2">
                    <Input
                      value={editName}
                      onChange={(e) => setEditName(e.target.value)}
                      maxLength={80}
                      className="h-7"
                      placeholder="Board name"
                    />
                  </div>
                  <EditFormActions
                    onSave={saveEdit}
                    onCancel={list.cancelEdit}
                    isPending={updateMutation.isPending}
                  />
                </>
              ) : (
                <>
                  <div className="flex items-center gap-3">
                    <span className="font-medium">{board.name}</span>
                    <Badge variant="secondary">/{board.slug}</Badge>
                  </div>
                  <ItemActions
                    isConfirmingDelete={list.confirmDeleteId === board.id}
                    isDeleting={deleteMutation.isPending}
                    onEdit={() => startEdit(board.id, board.name)}
                    onDelete={() => handleDelete(board.id)}
                  />
                </>
              )}
            </EditableListRow>
          ))}
        </EditableListContainer>
      </div>

      {/* Pinned footer — never scrolls; the Add form stays reachable. */}
      <div className="shrink-0">
        <Separator className="mb-4" />
        <h3 className="mb-3 text-sm font-medium">Add Board</h3>
        <div className="flex items-end gap-2">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="board-name">Name</Label>
            <Input
              id="board-name"
              value={newName}
              onChange={(e) => setNewName(e.target.value)}
              maxLength={80}
              placeholder="e.g. Sprint Board"
            />
          </div>
          <Button
            type="button"
            onClick={handleCreate}
            disabled={createMutation.isPending || !newName.trim()}
          >
            {createMutation.isPending ? 'Adding...' : 'Add Board'}
          </Button>
        </div>
      </div>
    </div>
  );
}

function UsersTab() {
  const queryClient = useQueryClient();
  const [newName, setNewName] = useState('');
  const [newRole, setNewRole] = useState(DEFAULT_NEW_ROLE);
  const [editName, setEditName] = useState('');
  const [editRole, setEditRole] = useState(DEFAULT_NEW_ROLE);
  const [editError, setEditError] = useState<string | null>(null);
  const [createdKey, setCreatedKey] = useState<string | null>(null);
  const [confirmDeactivateId, setConfirmDeactivateId] = useState<string | null>(null);
  const list = useEditableList();

  const usersQuery = useQuery({
    queryKey: queryKeys.users.all(),
    queryFn: fetchUsers,
    ...QUERY_DEFAULTS.userDirectory,
  });

  // Admin-tab mutations are inline tier — failures surface
  // inline via `setEditError` → <EditableListContainer>. skipToast keeps the
  // floor quiet; the call site owns the surface. (Update was already inline;
  // create and deactivate previously went silent — now uniform.)
  const createMutation = useMutation({
    meta: { skipToast: true },
    mutationFn: () => createUser(newName.trim(), parseInt(newRole, 10)),
    onSuccess: (user) => {
      queryClient.invalidateQueries({ queryKey: queryKeys.users.all() });
      setNewName('');
      setNewRole(DEFAULT_NEW_ROLE);
      setCreatedKey(user.authKey);
      setEditError(null);
    },
    onError: (error: unknown) => {
      setEditError(error instanceof Error ? error.message : 'Failed to create user.');
    },
  });

  const updateMutation = useMutation({
    meta: { skipToast: true },
    mutationFn: ({ id, patch }: { id: string; patch: UpdateUserPatch }) => updateUser(id, patch),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.users.all() });
      queryClient.invalidateQueries({ queryKey: queryKeys.users.directory() });
      list.setEditingId(null);
      setEditError(null);
    },
    onError: (error: unknown) => {
      setEditError(error instanceof Error ? error.message : 'Failed to update user.');
    },
  });

  const deactivateMutation = useMutation({
    meta: { skipToast: true },
    mutationFn: (id: string) => deactivateUser(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.users.all() });
      setConfirmDeactivateId(null);
      setEditError(null);
    },
    onError: (error: unknown) => {
      setEditError(error instanceof Error ? error.message : 'Failed to deactivate user.');
    },
  });

  const handleCreate = () => {
    if (!newName.trim()) return;
    createMutation.mutate();
  };

  const startEdit = (id: string, name: string, role: number) => {
    list.startEdit(id);
    setEditName(name);
    setEditRole(String(role));
    setEditError(null);
    setConfirmDeactivateId(null);
  };

  const cancelEdit = () => {
    list.cancelEdit();
    setEditError(null);
  };

  const saveEdit = () => {
    if (!list.editingId) return;
    const user = usersQuery.data?.find((u) => u.id === list.editingId);
    if (!user) return;
    const trimmedName = editName.trim();
    if (!trimmedName) {
      setEditError('Name cannot be empty.');
      return;
    }
    const patch: UpdateUserPatch = {};
    if (trimmedName !== user.name) patch.name = trimmedName;
    const nextRole = parseInt(editRole, 10);
    if (nextRole !== user.role) patch.role = nextRole;
    if (Object.keys(patch).length === 0) {
      cancelEdit();
      return;
    }
    updateMutation.mutate({ id: list.editingId, patch });
  };

  const handleDeactivate = (id: string) => {
    if (confirmDeactivateId === id) {
      deactivateMutation.mutate(id);
    } else {
      setConfirmDeactivateId(id);
    }
  };

  const copyKey = async (key: string) => {
    await navigator.clipboard.writeText(key);
  };

  const users = usersQuery.data ?? [];

  return (
    <div className="flex h-full min-h-0 flex-col gap-4">
      {/* Scroll zone — fills remaining height, scrolls internally on long lists. */}
      <div className="min-h-0 flex-1 overflow-y-auto">
        <EditableListContainer error={editError}>
          {users.map((user) => (
            <EditableListRow key={user.id}>
              {list.editingId === user.id ? (
                <>
                  <div className="flex flex-1 items-center gap-2">
                    <Input
                      value={editName}
                      onChange={(e) => setEditName(e.target.value)}
                      maxLength={80}
                      className="h-7"
                      placeholder="Name"
                      aria-label="User name"
                    />
                    <Select
                      aria-label="User role"
                      value={editRole}
                      onValueChange={(v) => v && setEditRole(v)}
                    >
                      <SelectTrigger className="h-7 w-40">
                        <SelectValue>{ROLE_MAP[parseInt(editRole, 10)] ?? editRole}</SelectValue>
                      </SelectTrigger>
                      <SelectContent>
                        {ROLE_OPTIONS.map(({ label, value }) => (
                          <SelectItem key={value} value={String(value)}>
                            {label}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </div>
                  <EditFormActions
                    onSave={saveEdit}
                    onCancel={cancelEdit}
                    isPending={updateMutation.isPending}
                  />
                </>
              ) : (
                <>
                  <div className="flex items-center gap-3">
                    <span className="font-medium">{user.name}</span>
                    <Badge
                      variant="secondary"
                      className={
                        user.role === ROLES.Administrator || user.role === ROLES['Agent Admin']
                          ? 'bg-primary/15 text-primary'
                          : user.role === ROLES.Agent
                            ? 'bg-accent/15 text-accent'
                            : ''
                      }
                    >
                      {ROLE_MAP[user.role] ?? `Role ${user.role}`}
                    </Badge>
                    <Badge variant={user.isActive ? 'outline' : 'destructive'}>
                      {user.isActive ? 'Active' : 'Inactive'}
                    </Badge>
                  </div>
                  <div className="flex gap-1">
                    <Button
                      size="xs"
                      variant="ghost"
                      onClick={() => startEdit(user.id, user.name, user.role)}
                      title="Edit user"
                      aria-label={`Edit ${user.name}`}
                    >
                      <Pencil className="h-3.5 w-3.5" />
                    </Button>
                    {user.isActive && (
                      <Button
                        size="xs"
                        variant="ghost"
                        className="text-destructive hover:text-destructive"
                        onClick={() => handleDeactivate(user.id)}
                        disabled={deactivateMutation.isPending}
                      >
                        {confirmDeactivateId === user.id ? 'Confirm' : 'Deactivate'}
                      </Button>
                    )}
                  </div>
                </>
              )}
            </EditableListRow>
          ))}
        </EditableListContainer>
      </div>

      {/* Pinned footer — never scrolls; the created-key callout and the Add form
          stay reachable. The callout is pinned with Add so a freshly
          created auth key isn't scrolled off when the user list is long. */}
      <div className="shrink-0">
        <Separator className="mb-4" />

        {createdKey && (
          <div className="mb-4 rounded-lg border bg-muted/30 p-3">
            <p className="mb-1 text-sm font-medium">New user auth key:</p>
            <div className="flex items-center gap-2">
              <code className="flex-1 rounded bg-muted px-2 py-1 text-xs break-all">
                {createdKey}
              </code>
              <Button size="xs" variant="outline" onClick={() => copyKey(createdKey)}>
                Copy
              </Button>
              <Button size="xs" variant="ghost" onClick={() => setCreatedKey(null)}>
                Dismiss
              </Button>
            </div>
          </div>
        )}

        <h3 className="mb-3 text-sm font-medium">Add User</h3>
        <div className="flex items-end gap-2">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="user-name">Name</Label>
            <Input
              id="user-name"
              value={newName}
              onChange={(e) => setNewName(e.target.value)}
              placeholder="e.g. Jane Doe"
            />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label id="new-user-role-label">Role</Label>
            <Select
              aria-labelledby="new-user-role-label"
              value={newRole}
              onValueChange={(v) => v && setNewRole(v)}
            >
              <SelectTrigger className="w-40">
                <SelectValue>{ROLE_MAP[parseInt(newRole, 10)] ?? newRole}</SelectValue>
              </SelectTrigger>
              <SelectContent>
                {ROLE_OPTIONS.map(({ label, value }) => (
                  <SelectItem key={value} value={String(value)}>
                    {label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <Button onClick={handleCreate} disabled={createMutation.isPending || !newName.trim()}>
            {createMutation.isPending ? 'Adding...' : 'Add User'}
          </Button>
        </div>
      </div>
    </div>
  );
}
