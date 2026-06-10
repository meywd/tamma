/**
 * Members Tab (Story 18-8 AC 1, 3, 4)
 *
 * Renders the active tenant's member list with role-change dropdown +
 * remove button. Hierarchy guards on the backend (Story 18-7) drive the
 * 4xx error copies — UI maps each to a friendly string via
 * `mapOrgError`.
 */

import { useState, type JSX } from 'react';
import { useOrgMembers } from '../../hooks/orgs/useOrgMembers.js';
import { useCurrentTenant } from '../../hooks/orgs/useCurrentTenant.js';
import { LoadingSpinner } from '../common/LoadingSpinner.js';
import { Badge } from '../common/Badge.js';
import { ConfirmDialog } from '../common/ConfirmDialog.js';
import { mapOrgError } from '../../services/orgs/error-copy.js';
import type {
  OrgMember,
  TenantRole,
} from '../../services/orgs/org-api-client.js';

const ROLE_BADGE: Record<TenantRole, 'error' | 'warning' | 'info'> = {
  owner: 'error',
  admin: 'warning',
  member: 'info',
};

function formatDate(dateStr: string): string {
  return new Date(dateStr).toLocaleDateString();
}

/** Filter the role options the caller is allowed to assign to a target. */
function allowedRolesFor(
  callerRole: 'owner' | 'admin' | 'member',
  targetRole: TenantRole,
): TenantRole[] {
  if (callerRole === 'owner') {
    // Owner can do everything except they can't demote the last owner —
    // the backend handles that case; the UI just lists the choices.
    return ['owner', 'admin', 'member'];
  }
  if (callerRole === 'admin') {
    // Admin can change peers/below to member only; can't touch owners
    // and can't promote anyone to admin/owner.
    if (targetRole === 'owner') return [targetRole]; // disabled effectively
    return ['member']; // only allowed change
  }
  return [targetRole]; // member can change nothing
}

export function MembersTab(): JSX.Element {
  const { members, total, loading, error, updateRole, remove } = useOrgMembers();
  const { role: callerRole, me } = useCurrentTenant();
  const [confirmRole, setConfirmRole] =
    useState<{ member: OrgMember; newRole: TenantRole } | null>(null);
  const [confirmRemove, setConfirmRemove] = useState<OrgMember | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  if (loading && members.length === 0) {
    return (
      <div className="flex justify-center py-12">
        <LoadingSpinner size="lg" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-lg p-4 text-sm text-red-700 dark:bg-red-950 dark:text-red-300 dark:border-red-800">
        {mapOrgError(error)}
      </div>
    );
  }

  const handleRoleChange = async () => {
    if (!confirmRole) return;
    setActionError(null);
    try {
      await updateRole(confirmRole.member.userId, confirmRole.newRole);
    } catch (err) {
      setActionError(mapOrgError(err instanceof Error ? err.message : null));
    } finally {
      setConfirmRole(null);
    }
  };

  const handleRemove = async () => {
    if (!confirmRemove) return;
    setActionError(null);
    try {
      await remove(confirmRemove.userId);
    } catch (err) {
      setActionError(mapOrgError(err instanceof Error ? err.message : null));
    } finally {
      setConfirmRemove(null);
    }
  };

  return (
    <div>
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-lg font-semibold text-gray-900 dark:text-gray-100">
          Members <span className="text-gray-400 font-normal dark:text-gray-500">({total})</span>
        </h2>
      </div>

      {actionError && (
        <div className="mb-4 bg-red-50 border border-red-200 rounded-lg p-3 text-sm text-red-700 dark:bg-red-950 dark:text-red-300 dark:border-red-800">
          {actionError}
        </div>
      )}

      <div className="bg-white rounded-lg border border-gray-200 shadow-sm overflow-hidden dark:bg-gray-800 dark:border-gray-700">
        <table className="min-w-full divide-y divide-gray-200">
          <thead className="bg-gray-50 dark:bg-gray-900">
            <tr>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider dark:text-gray-400">
                Name
              </th>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider dark:text-gray-400">
                Email
              </th>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider dark:text-gray-400">
                Role
              </th>
              <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider dark:text-gray-400">
                Joined
              </th>
              <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider dark:text-gray-400">
                Actions
              </th>
            </tr>
          </thead>
          <tbody className="bg-white divide-y divide-gray-200 dark:bg-gray-800">
            {members.map((m) => {
              const isSelf = m.userId === me?.id;
              const roleOptions = callerRole
                ? allowedRolesFor(callerRole, m.role)
                : [m.role];
              const canEdit = !isSelf && roleOptions.length > 1;
              const canRemove =
                !isSelf &&
                callerRole !== null &&
                callerRole !== 'member' &&
                !(callerRole === 'admin' && m.role === 'owner');

              return (
                <tr key={m.userId} className="hover:bg-gray-50 dark:hover:bg-gray-800">
                  <td className="px-6 py-4 whitespace-nowrap text-sm font-medium text-gray-900 dark:text-gray-100">
                    {m.displayName ?? m.userId}
                    {isSelf && (
                      <span className="ml-2 text-xs text-gray-400 dark:text-gray-500">(you)</span>
                    )}
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-gray-400">
                    {m.email ?? '-'}
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap">
                    {canEdit ? (
                      <select
                        value={m.role}
                        onChange={(e) =>
                          setConfirmRole({
                            member: m,
                            newRole: e.target.value as TenantRole,
                          })
                        }
                        className="text-sm border border-gray-300 rounded-md px-2 py-1 bg-white focus:outline-none focus:ring-2 focus:ring-blue-500 dark:bg-gray-800 dark:border-gray-600"
                        aria-label={`Role for ${m.displayName ?? m.userId}`}
                      >
                        {(['owner', 'admin', 'member'] as TenantRole[]).map((r) => (
                          <option
                            key={r}
                            value={r}
                            disabled={!roleOptions.includes(r)}
                          >
                            {r}
                          </option>
                        ))}
                      </select>
                    ) : (
                      <Badge variant={ROLE_BADGE[m.role]}>{m.role}</Badge>
                    )}
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500 dark:text-gray-400">
                    {formatDate(m.joinedAt)}
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap text-right">
                    {canRemove && (
                      <button
                        type="button"
                        onClick={() => setConfirmRemove(m)}
                        className="text-sm text-red-600 hover:text-red-800 font-medium dark:text-red-400"
                      >
                        Remove
                      </button>
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      <ConfirmDialog
        open={confirmRole !== null}
        title="Change Role"
        message={
          confirmRole
            ? `Change ${confirmRole.member.displayName ?? confirmRole.member.userId}'s role from "${confirmRole.member.role}" to "${confirmRole.newRole}"?`
            : ''
        }
        confirmLabel="Change Role"
        onConfirm={() => void handleRoleChange()}
        onCancel={() => setConfirmRole(null)}
      />

      <ConfirmDialog
        open={confirmRemove !== null}
        title="Remove Member"
        message={
          confirmRemove
            ? `${confirmRemove.displayName ?? confirmRemove.userId} will lose access to this organization. Their API keys and workflow assignments are revoked.`
            : ''
        }
        confirmLabel="Remove"
        variant="danger"
        onConfirm={() => void handleRemove()}
        onCancel={() => setConfirmRemove(null)}
      />
    </div>
  );
}
