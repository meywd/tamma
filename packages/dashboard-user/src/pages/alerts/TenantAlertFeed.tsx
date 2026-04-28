/**
 * TenantAlertFeed — /alerts
 *
 * Scoped to the caller's active tenant. Backend enforces isolation:
 * `GET /api/v1/orgs/{tenantId}/alerts` is behind
 * `RequireTenantMembershipFilter` and returns only rows where
 * `alerts.tenant_id = {tenantId}`. Cross-tenant reads 404.
 *
 * Ack/resolve require tenant admin+ (server-side). The UI surfaces
 * the buttons only for admin/owner; members see the read-only feed.
 */

import { useCallback, useEffect, useMemo, useState, type JSX } from 'react';
import { useAuth } from '../../hooks/useAuth';
import {
  acknowledgeTenantAlert,
  listTenantAlerts,
  resolveTenantAlert,
  type AlertDto,
  type AlertSeverity,
  type AlertStatus,
} from '../../api/alerts';

const SEVERITIES: AlertSeverity[] = ['critical', 'warning', 'info'];
const STATUSES: AlertStatus[] = ['active', 'acknowledged', 'resolved'];
const WINDOW_OPTIONS = [
  { key: '1', label: 'Last 24 h', days: 1 },
  { key: '7', label: 'Last 7 days', days: 7 },
  { key: '30', label: 'Last 30 days', days: 30 },
  { key: 'all', label: 'All', days: undefined as number | undefined },
];

export function TenantAlertFeed(): JSX.Element {
  const { user } = useAuth();
  const tenantId = user?.tenantId ?? null;
  const role = user?.role ?? '';
  const canMutate = role === 'admin' || role === 'owner';

  const [alerts, setAlerts] = useState<AlertDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [severity, setSeverity] = useState<AlertSeverity | ''>('');
  const [status, setStatus] = useState<AlertStatus | ''>('');
  const [windowKey, setWindowKey] = useState<string>('7');

  const [ackOpen, setAckOpen] = useState<AlertDto | null>(null);
  const [resolveOpen, setResolveOpen] = useState<AlertDto | null>(null);
  const [ackNote, setAckNote] = useState('');
  const [resolveText, setResolveText] = useState('');
  const [mutationErr, setMutationErr] = useState<string | null>(null);

  const windowSelection = useMemo(
    () => WINDOW_OPTIONS.find((w) => w.key === windowKey) ?? WINDOW_OPTIONS[1]!,
    [windowKey],
  );

  const refresh = useCallback(async () => {
    if (!tenantId) return;
    setLoading(true);
    setError(null);
    try {
      const resp = await listTenantAlerts(tenantId, {
        severity: severity || undefined,
        status: status || undefined,
        sinceDays: windowSelection.days,
        limit: 200,
      });
      setAlerts(resp.items);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load alerts');
    } finally {
      setLoading(false);
    }
  }, [tenantId, severity, status, windowSelection]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const doAck = async (): Promise<void> => {
    if (!tenantId || ackOpen === null) return;
    setMutationErr(null);
    try {
      await acknowledgeTenantAlert(tenantId, ackOpen.id, ackNote || undefined);
      setAckOpen(null);
      setAckNote('');
      await refresh();
    } catch (err) {
      setMutationErr(err instanceof Error ? err.message : 'Acknowledge failed');
    }
  };

  const doResolve = async (): Promise<void> => {
    if (!tenantId || resolveOpen === null) return;
    if (!resolveText.trim()) return;
    setMutationErr(null);
    try {
      await resolveTenantAlert(tenantId, resolveOpen.id, resolveText);
      setResolveOpen(null);
      setResolveText('');
      await refresh();
    } catch (err) {
      setMutationErr(err instanceof Error ? err.message : 'Resolve failed');
    }
  };

  if (!tenantId) {
    return (
      <div className="p-4 text-sm text-gray-500">
        No active organization. Start onboarding to enable alerts.
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-2xl font-bold text-gray-900">Alerts</h1>
        <p className="mt-1 text-sm text-gray-500">
          Alerts raised for your organization. Admins can acknowledge and resolve.
        </p>
      </div>

      <div className="flex flex-wrap gap-3 items-end bg-white border border-gray-200 rounded-md p-3">
        <label className="flex flex-col text-xs text-gray-600">
          Severity
          <select
            aria-label="Severity filter"
            value={severity}
            onChange={(e) => setSeverity(e.target.value as AlertSeverity | '')}
            className="mt-1 px-2 py-1 border border-gray-300 rounded text-sm"
          >
            <option value="">All</option>
            {SEVERITIES.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </select>
        </label>

        <label className="flex flex-col text-xs text-gray-600">
          Status
          <select
            aria-label="Status filter"
            value={status}
            onChange={(e) => setStatus(e.target.value as AlertStatus | '')}
            className="mt-1 px-2 py-1 border border-gray-300 rounded text-sm"
          >
            <option value="">All</option>
            {STATUSES.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </select>
        </label>

        <label className="flex flex-col text-xs text-gray-600">
          Window
          <select
            aria-label="Time window"
            value={windowKey}
            onChange={(e) => setWindowKey(e.target.value)}
            className="mt-1 px-2 py-1 border border-gray-300 rounded text-sm"
          >
            {WINDOW_OPTIONS.map((w) => (
              <option key={w.key} value={w.key}>
                {w.label}
              </option>
            ))}
          </select>
        </label>

        <button
          type="button"
          onClick={() => void refresh()}
          className="ml-auto px-3 py-1.5 text-sm bg-gray-900 text-white rounded hover:bg-gray-800"
        >
          Refresh
        </button>
      </div>

      {error !== null && (
        <div role="alert" className="p-3 text-sm text-red-700 bg-red-50 rounded-md">
          {error}
        </div>
      )}

      {mutationErr !== null && (
        <div role="alert" className="p-3 text-sm text-red-700 bg-red-50 rounded-md">
          {mutationErr}
        </div>
      )}

      {loading ? (
        <p className="text-sm text-gray-500">Loading…</p>
      ) : alerts.length === 0 ? (
        <p className="text-sm text-gray-500">No alerts match your filters.</p>
      ) : (
        <div className="bg-white border border-gray-200 rounded-md overflow-hidden">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 text-xs uppercase text-gray-600">
              <tr>
                <th className="px-3 py-2 text-left">Time</th>
                <th className="px-3 py-2 text-left">Severity</th>
                <th className="px-3 py-2 text-left">Title</th>
                <th className="px-3 py-2 text-left">Status</th>
                {canMutate && <th className="px-3 py-2 text-right">Actions</th>}
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {alerts.map((a) => (
                <tr key={a.id} className="hover:bg-gray-50">
                  <td className="px-3 py-2 whitespace-nowrap text-gray-700">
                    {new Date(a.createdAt).toLocaleString()}
                  </td>
                  <td className="px-3 py-2">
                    <SeverityPill severity={a.severity} />
                  </td>
                  <td className="px-3 py-2 text-gray-900">{a.title}</td>
                  <td className="px-3 py-2 text-gray-700">{a.status}</td>
                  {canMutate && (
                    <td className="px-3 py-2 text-right whitespace-nowrap">
                      {a.status === 'active' && (
                        <button
                          type="button"
                          onClick={() => setAckOpen(a)}
                          className="px-2 py-1 text-xs border border-gray-300 rounded hover:bg-gray-50"
                        >
                          Ack
                        </button>
                      )}
                      {a.status !== 'resolved' && (
                        <button
                          type="button"
                          onClick={() => setResolveOpen(a)}
                          className="ml-1 px-2 py-1 text-xs border border-green-600 text-green-700 rounded hover:bg-green-50"
                        >
                          Resolve
                        </button>
                      )}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {ackOpen !== null && (
        <Modal title="Acknowledge alert" onClose={() => setAckOpen(null)}>
          <p className="text-sm text-gray-700 mb-2">{ackOpen.title}</p>
          <label className="block text-xs text-gray-600 mb-1" htmlFor="ack-note">
            Note (optional)
          </label>
          <textarea
            id="ack-note"
            value={ackNote}
            onChange={(e) => setAckNote(e.target.value)}
            className="w-full border border-gray-300 rounded px-2 py-1 text-sm"
            rows={3}
          />
          <div className="mt-3 flex justify-end gap-2">
            <button
              type="button"
              onClick={() => setAckOpen(null)}
              className="px-3 py-1 text-sm border border-gray-300 rounded"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={() => void doAck()}
              className="px-3 py-1 text-sm bg-gray-900 text-white rounded"
            >
              Acknowledge
            </button>
          </div>
        </Modal>
      )}

      {resolveOpen !== null && (
        <Modal title="Resolve alert" onClose={() => setResolveOpen(null)}>
          <p className="text-sm text-gray-700 mb-2">{resolveOpen.title}</p>
          <label className="block text-xs text-gray-600 mb-1" htmlFor="resolve-text">
            Resolution (required)
          </label>
          <textarea
            id="resolve-text"
            value={resolveText}
            onChange={(e) => setResolveText(e.target.value)}
            className="w-full border border-gray-300 rounded px-2 py-1 text-sm"
            rows={3}
            required
          />
          <div className="mt-3 flex justify-end gap-2">
            <button
              type="button"
              onClick={() => setResolveOpen(null)}
              className="px-3 py-1 text-sm border border-gray-300 rounded"
            >
              Cancel
            </button>
            <button
              type="button"
              disabled={!resolveText.trim()}
              onClick={() => void doResolve()}
              className="px-3 py-1 text-sm bg-green-700 text-white rounded disabled:opacity-50"
            >
              Resolve
            </button>
          </div>
        </Modal>
      )}
    </div>
  );
}

function SeverityPill({ severity }: { severity: AlertSeverity }): JSX.Element {
  const colors: Record<AlertSeverity, string> = {
    critical: 'bg-red-100 text-red-800',
    warning: 'bg-yellow-100 text-yellow-800',
    info: 'bg-blue-100 text-blue-800',
  };
  return (
    <span
      className={`inline-flex px-2 py-0.5 text-xs font-medium rounded ${colors[severity]}`}
    >
      {severity}
    </span>
  );
}

function Modal({
  title,
  children,
  onClose,
}: {
  title: string;
  children: React.ReactNode;
  onClose: () => void;
}): JSX.Element {
  return (
    <div
      role="dialog"
      aria-labelledby="modal-title"
      aria-modal="true"
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/40"
    >
      <div className="bg-white rounded-lg shadow-lg p-5 w-full max-w-md">
        <div className="flex items-center justify-between mb-3">
          <h2 id="modal-title" className="text-lg font-medium">
            {title}
          </h2>
          <button
            type="button"
            aria-label="Close"
            onClick={onClose}
            className="text-gray-400 hover:text-gray-600"
          >
            ×
          </button>
        </div>
        {children}
      </div>
    </div>
  );
}
