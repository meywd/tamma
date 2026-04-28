/**
 * Users Tab
 *
 * Displays a table of all users with avatar, login, email, role, last active, created.
 * Owner-only actions: change roles, remove users.
 * Invite user dialog with role selection.
 */

import { useState, useEffect, useRef, useCallback, type JSX } from 'react';
import { useUsers } from '../../hooks/admin/useUsers.js';
import { useCurrentUser } from '../../hooks/admin/useCurrentUser.js';
import { LoadingSpinner } from '../../components/common/LoadingSpinner.js';
import { Badge } from '../../components/common/Badge.js';
import { ConfirmDialog } from '../../components/common/ConfirmDialog.js';
import type { AdminUser } from '../../services/admin/admin-api-client.js';

type UserRole = 'owner' | 'admin' | 'member';

const ROLE_BADGE_VARIANT = {
  owner: 'error' as const,
  admin: 'warning' as const,
  member: 'info' as const,
};

function formatRelative(dateStr: string): string {
  const date = new Date(dateStr);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffMins = Math.floor(diffMs / 60_000);
  const diffHours = Math.floor(diffMins / 60);
  const diffDays = Math.floor(diffHours / 24);

  if (diffMins < 1) return 'just now';
  if (diffMins < 60) return `${diffMins}m ago`;
  if (diffHours < 24) return `${diffHours}h ago`;
  if (diffDays < 30) return `${diffDays}d ago`;
  return date.toLocaleDateString();
}

function formatDate(dateStr: string): string {
  return new Date(dateStr).toLocaleDateString();
}

/** Role selector dropdown */
function RoleSelector({
  currentRole,
  canPromote,
  disabled,
  onChange,
}: {
  currentRole: UserRole;
  canPromote: boolean;
  disabled: boolean;
  onChange: (role: UserRole) => void;
}): JSX.Element {
  if (disabled) {
    return <Badge variant={ROLE_BADGE_VARIANT[currentRole]}>{currentRole}</Badge>;
  }

  // Members can't change roles. Admins can only set member. Owners can set anything.
  const options: UserRole[] = canPromote
    ? ['owner', 'admin', 'member']
    : ['member'];

  if (options.length <= 1 && options[0] === currentRole) {
    return <Badge variant={ROLE_BADGE_VARIANT[currentRole]}>{currentRole}</Badge>;
  }

  return (
    <select
      value={currentRole}
      onChange={(e) => onChange(e.target.value as UserRole)}
      className="text-sm border border-gray-300 rounded-md px-2 py-1 bg-white focus:outline-none focus:ring-2 focus:ring-blue-500"
    >
      <option value="owner" disabled={!canPromote}>
        owner
      </option>
      <option value="admin" disabled={!canPromote}>
        admin
      </option>
      <option value="member">member</option>
    </select>
  );
}

/** Invite User Dialog */
function InviteDialog({ onClose }: { onClose: () => void }): JSX.Element {
  const { invite } = useUsers();
  const [email, setEmail] = useState('');
  const [role, setRole] = useState<'admin' | 'member'>('member');
  const [inviteUrl, setInviteUrl] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const dialogRef = useRef<HTMLDivElement>(null);

  // Focus dialog on mount
  useEffect(() => {
    requestAnimationFrame(() => {
      dialogRef.current?.focus();
    });
  }, []);

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        onClose();
      }
    },
    [onClose],
  );

  const handleSubmit = async () => {
    setSubmitting(true);
    setError(null);
    try {
      const inviteData: { role: string; email?: string } = { role };
      if (email) {
        inviteData.email = email;
      }
      const result = await invite(inviteData);
      setInviteUrl(result.inviteLink);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to create invite');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center" role="presentation">
      <div className="fixed inset-0 bg-black/50" onClick={onClose} aria-hidden="true" />
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby="invite-dialog-title"
        tabIndex={-1}
        onKeyDown={handleKeyDown}
        className="relative bg-white rounded-lg shadow-xl p-6 max-w-md w-full mx-4 outline-none"
      >
        <h3 id="invite-dialog-title" className="text-lg font-semibold text-gray-900 mb-4">
          Invite User
        </h3>

        {inviteUrl ? (
          <div>
            <p className="text-sm text-gray-600 mb-3">
              Share this invite link with the user:
            </p>
            <div className="flex items-center gap-2">
              <input
                type="text"
                readOnly
                value={inviteUrl}
                className="flex-1 text-sm border border-gray-300 rounded-md px-3 py-2 bg-gray-50"
              />
              <button
                type="button"
                onClick={() => void navigator.clipboard.writeText(inviteUrl)}
                className="px-3 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md"
              >
                Copy
              </button>
            </div>
            <div className="flex justify-end mt-4">
              <button
                type="button"
                onClick={onClose}
                className="px-4 py-2 text-sm font-medium text-gray-700 bg-gray-100 hover:bg-gray-200 rounded-md"
              >
                Done
              </button>
            </div>
          </div>
        ) : (
          <div>
            <div className="mb-4">
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Email (optional)
              </label>
              <input
                type="email"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                placeholder="user@example.com"
                className="w-full text-sm border border-gray-300 rounded-md px-3 py-2 focus:outline-none focus:ring-2 focus:ring-blue-500"
              />
            </div>

            <div className="mb-4">
              <label className="block text-sm font-medium text-gray-700 mb-1">Role</label>
              <select
                value={role}
                onChange={(e) => setRole(e.target.value as 'admin' | 'member')}
                className="w-full text-sm border border-gray-300 rounded-md px-3 py-2 bg-white focus:outline-none focus:ring-2 focus:ring-blue-500"
              >
                <option value="member">Member</option>
                <option value="admin">Admin</option>
              </select>
            </div>

            {error && <p className="text-sm text-red-600 mb-4">{error}</p>}

            <div className="flex justify-end gap-3">
              <button
                type="button"
                onClick={onClose}
                className="px-4 py-2 text-sm font-medium text-gray-700 bg-gray-100 hover:bg-gray-200 rounded-md"
              >
                Cancel
              </button>
              <button
                type="button"
                onClick={() => void handleSubmit()}
                disabled={submitting}
                className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50"
              >
                {submitting ? 'Creating...' : 'Create Invite'}
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

export function UsersTab(): JSX.Element {
  const { users, total, loading, error } = useUsers();
  const { user: currentUser, isOwner } = useCurrentUser();
  const { updateRole, remove } = useUsers();

  const [showInvite, setShowInvite] = useState(false);
  const [confirmRemove, setConfirmRemove] = useState<AdminUser | null>(null);
  const [confirmRole, setConfirmRole] = useState<{ user: AdminUser; newRole: UserRole } | null>(null);

  if (loading && users.length === 0) {
    return (
      <div className="flex justify-center py-12">
        <LoadingSpinner size="lg" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-lg p-4 text-sm text-red-700">
        {error}
      </div>
    );
  }

  if (users.length === 0) {
    return (
      <div className="text-center py-12 text-gray-500">
        <p className="text-lg mb-2">No users yet</p>
        <p className="text-sm">Invite users to get started.</p>
        <button
          type="button"
          onClick={() => setShowInvite(true)}
          className="mt-4 px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md"
        >
          Invite User
        </button>
        {showInvite && <InviteDialog onClose={() => setShowInvite(false)} />}
      </div>
    );
  }

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-lg font-semibold text-gray-900">
          Users <span className="text-gray-400 font-normal">({total})</span>
        </h2>
        <button
          type="button"
          onClick={() => setShowInvite(true)}
          className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md"
        >
          Invite User
        </button>
      </div>

      <div className="bg-white rounded-lg border border-gray-200 shadow-sm overflow-hidden">
        <table className="min-w-full divide-y divide-gray-200">
          <thead className="bg-gray-50">
            <tr>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                User
              </th>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                Email
              </th>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                Role
              </th>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                Last Active
              </th>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                Created
              </th>
              <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                Actions
              </th>
            </tr>
          </thead>
          <tbody className="bg-white divide-y divide-gray-200">
            {users.map((user) => (
              <tr key={user.id} className="hover:bg-gray-50">
                <td className="px-6 py-4 whitespace-nowrap">
                  <div className="flex items-center gap-3">
                    <img
                      src={`https://github.com/${user.githubLogin}.png?size=32`}
                      alt={user.githubLogin}
                      className="h-8 w-8 rounded-full"
                    />
                    <span className="text-sm font-medium text-gray-900">
                      {user.githubLogin}
                    </span>
                  </div>
                </td>
                <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                  {user.email ?? '-'}
                </td>
                <td className="px-6 py-4 whitespace-nowrap">
                  <RoleSelector
                    currentRole={user.role}
                    canPromote={isOwner}
                    disabled={user.id === currentUser?.id}
                    onChange={(newRole) => setConfirmRole({ user, newRole })}
                  />
                </td>
                <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                  {user.lastActiveAt ? formatRelative(user.lastActiveAt) : 'Never'}
                </td>
                <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                  {formatDate(user.createdAt)}
                </td>
                <td className="px-6 py-4 whitespace-nowrap text-right">
                  {isOwner && user.id !== currentUser?.id && (
                    <button
                      type="button"
                      onClick={() => setConfirmRemove(user)}
                      className="text-sm text-red-600 hover:text-red-800 font-medium"
                    >
                      Remove
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {/* Invite Dialog */}
      {showInvite && <InviteDialog onClose={() => setShowInvite(false)} />}

      {/* Confirm Role Change */}
      <ConfirmDialog
        open={confirmRole !== null}
        title="Change User Role"
        message={
          confirmRole
            ? `Change ${confirmRole.user.githubLogin}'s role from "${confirmRole.user.role}" to "${confirmRole.newRole}"?`
            : ''
        }
        confirmLabel="Change Role"
        onConfirm={() => {
          if (confirmRole) {
            void updateRole(confirmRole.user.id, confirmRole.newRole);
          }
          setConfirmRole(null);
        }}
        onCancel={() => setConfirmRole(null)}
      />

      {/* Confirm Remove */}
      <ConfirmDialog
        open={confirmRemove !== null}
        title="Remove User"
        message={
          confirmRemove
            ? `Are you sure you want to remove ${confirmRemove.githubLogin}? This action can be undone by an administrator.`
            : ''
        }
        confirmLabel="Remove"
        variant="danger"
        onConfirm={() => {
          if (confirmRemove) {
            void remove(confirmRemove.id);
          }
          setConfirmRemove(null);
        }}
        onCancel={() => setConfirmRemove(null)}
      />
    </div>
  );
}
