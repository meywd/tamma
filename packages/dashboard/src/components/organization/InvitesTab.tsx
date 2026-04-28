/**
 * Invites Tab (Story 18-8 AC 2, 5)
 *
 * Pending invites table + create-invite form + per-row resend / revoke
 * actions. Calls the Story 18-7 backend handlers for resend (which
 * extends expiry without rotating the token) and the existing
 * `DeleteInvite` handler for revoke.
 */

import { useState, type JSX } from 'react';
import { useOrgInvites } from '../../hooks/orgs/useOrgInvites.js';
import { useCurrentTenant } from '../../hooks/orgs/useCurrentTenant.js';
import { LoadingSpinner } from '../common/LoadingSpinner.js';
import { Badge } from '../common/Badge.js';
import { ConfirmDialog } from '../common/ConfirmDialog.js';
import { mapOrgError, mapOrgHttpError } from '../../services/orgs/error-copy.js';
import type {
  PendingInvite,
  TenantRole,
} from '../../services/orgs/org-api-client.js';

const ROLE_BADGE: Record<TenantRole, 'error' | 'warning' | 'info'> = {
  owner: 'error',
  admin: 'warning',
  member: 'info',
};

function relativeFromNow(dateStr: string): string {
  const target = new Date(dateStr).getTime();
  const now = Date.now();
  const diff = target - now;
  const absMin = Math.abs(diff) / 60_000;
  if (absMin < 60) return diff > 0 ? `in ${Math.round(absMin)}m` : `${Math.round(absMin)}m ago`;
  const absHr = absMin / 60;
  if (absHr < 24) return diff > 0 ? `in ${Math.round(absHr)}h` : `${Math.round(absHr)}h ago`;
  const absDay = absHr / 24;
  return diff > 0 ? `in ${Math.round(absDay)}d` : `${Math.round(absDay)}d ago`;
}

function CreateInviteForm({
  callerRole,
  onSubmit,
}: {
  callerRole: 'owner' | 'admin' | 'member' | null;
  onSubmit: (email: string, role: TenantRole) => Promise<void>;
}): JSX.Element {
  const [email, setEmail] = useState('');
  const [role, setRole] = useState<TenantRole>('member');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Owners can pick any role; admins can only invite members or admins.
  const roleOptions: TenantRole[] =
    callerRole === 'owner' ? ['owner', 'admin', 'member'] : ['admin', 'member'];

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    if (!email || !email.includes('@')) {
      setError('Enter a valid email address.');
      return;
    }
    setSubmitting(true);
    try {
      await onSubmit(email.trim(), role);
      setEmail('');
      setRole('member');
    } catch (err) {
      const status = (err as Error & { status?: number }).status;
      setError(mapOrgHttpError(err instanceof Error ? err.message : null, status));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <form onSubmit={(e) => void handleSubmit(e)} className="bg-white rounded-lg border border-gray-200 shadow-sm p-4 mb-6">
      <h3 className="text-sm font-semibold text-gray-900 mb-3">Invite a new member</h3>
      <div className="flex gap-3 items-start">
        <div className="flex-1">
          <label htmlFor="invite-email" className="sr-only">Email</label>
          <input
            id="invite-email"
            type="email"
            placeholder="user@example.com"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            disabled={submitting}
            className="w-full text-sm border border-gray-300 rounded-md px-3 py-2 focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-50"
          />
        </div>
        <div>
          <label htmlFor="invite-role" className="sr-only">Role</label>
          <select
            id="invite-role"
            value={role}
            onChange={(e) => setRole(e.target.value as TenantRole)}
            disabled={submitting}
            className="text-sm border border-gray-300 rounded-md px-3 py-2 bg-white focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-50"
          >
            {roleOptions.map((r) => (
              <option key={r} value={r}>
                {r}
              </option>
            ))}
          </select>
        </div>
        <button
          type="submit"
          disabled={submitting || !email}
          className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50"
        >
          {submitting ? 'Sending…' : 'Send invite'}
        </button>
      </div>
      {error && <p className="mt-2 text-sm text-red-600" role="alert">{error}</p>}
    </form>
  );
}

export function InvitesTab(): JSX.Element {
  const { invites, loading, error, create, resend, revoke } = useOrgInvites();
  const { role: callerRole } = useCurrentTenant();
  const [actionMessage, setActionMessage] =
    useState<{ kind: 'ok' | 'err'; text: string } | null>(null);
  const [confirmRevoke, setConfirmRevoke] = useState<PendingInvite | null>(null);
  const [pendingResendId, setPendingResendId] = useState<string | null>(null);

  if (loading && invites.length === 0) {
    return (
      <div className="flex justify-center py-12">
        <LoadingSpinner size="lg" />
      </div>
    );
  }

  const handleCreate = async (email: string, role: TenantRole) => {
    setActionMessage(null);
    await create(email, role);
    setActionMessage({ kind: 'ok', text: `Invite sent to ${email}.` });
  };

  const handleResend = async (invite: PendingInvite) => {
    setActionMessage(null);
    setPendingResendId(invite.id);
    try {
      await resend(invite.id);
      setActionMessage({
        kind: 'ok',
        text: `Resent invite to ${invite.email ?? invite.id}.`,
      });
    } catch (err) {
      const status = (err as Error & { status?: number }).status;
      setActionMessage({
        kind: 'err',
        text: mapOrgHttpError(err instanceof Error ? err.message : null, status),
      });
    } finally {
      setPendingResendId(null);
    }
  };

  const handleRevoke = async () => {
    if (!confirmRevoke) return;
    setActionMessage(null);
    try {
      await revoke(confirmRevoke.id);
      setActionMessage({ kind: 'ok', text: 'Invite revoked.' });
    } catch (err) {
      const status = (err as Error & { status?: number }).status;
      setActionMessage({
        kind: 'err',
        text: mapOrgHttpError(err instanceof Error ? err.message : null, status),
      });
    } finally {
      setConfirmRevoke(null);
    }
  };

  return (
    <div>
      <CreateInviteForm callerRole={callerRole} onSubmit={handleCreate} />

      {actionMessage && (
        <div
          className={`mb-4 rounded-lg p-3 text-sm ${
            actionMessage.kind === 'ok'
              ? 'bg-green-50 border border-green-200 text-green-700'
              : 'bg-red-50 border border-red-200 text-red-700'
          }`}
          role={actionMessage.kind === 'err' ? 'alert' : 'status'}
        >
          {actionMessage.text}
        </div>
      )}

      {error && !actionMessage && (
        <div className="mb-4 bg-red-50 border border-red-200 rounded-lg p-3 text-sm text-red-700">
          {mapOrgError(error)}
        </div>
      )}

      <h3 className="text-sm font-semibold text-gray-900 mb-3">
        Pending invites ({invites.length})
      </h3>

      {invites.length === 0 ? (
        <div className="bg-gray-50 border border-gray-200 rounded-lg p-6 text-center text-sm text-gray-500">
          No pending invites.
        </div>
      ) : (
        <div className="bg-white rounded-lg border border-gray-200 shadow-sm overflow-hidden">
          <table className="min-w-full divide-y divide-gray-200">
            <thead className="bg-gray-50">
              <tr>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                  Email
                </th>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                  Role
                </th>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                  Expires
                </th>
                <th className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                  Actions
                </th>
              </tr>
            </thead>
            <tbody className="bg-white divide-y divide-gray-200">
              {invites.map((i) => (
                <tr key={i.id} className="hover:bg-gray-50">
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-900">
                    {i.email ?? '—'}
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap">
                    <Badge variant={ROLE_BADGE[i.role]}>{i.role}</Badge>
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                    {relativeFromNow(i.expiresAt)}
                  </td>
                  <td className="px-6 py-4 whitespace-nowrap text-right text-sm font-medium space-x-3">
                    <button
                      type="button"
                      onClick={() => void handleResend(i)}
                      disabled={pendingResendId === i.id}
                      className="text-blue-600 hover:text-blue-800 disabled:opacity-50"
                    >
                      {pendingResendId === i.id ? 'Resending…' : 'Resend'}
                    </button>
                    <button
                      type="button"
                      onClick={() => setConfirmRevoke(i)}
                      className="text-red-600 hover:text-red-800"
                    >
                      Revoke
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <ConfirmDialog
        open={confirmRevoke !== null}
        title="Revoke invite"
        message={
          confirmRevoke
            ? `Revoke the pending invite to ${confirmRevoke.email ?? confirmRevoke.id}? They won't be able to use the original link.`
            : ''
        }
        confirmLabel="Revoke"
        variant="danger"
        onConfirm={() => void handleRevoke()}
        onCancel={() => setConfirmRevoke(null)}
      />
    </div>
  );
}
