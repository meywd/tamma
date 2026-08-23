/**
 * AuthorizationsPanel — actions waiting on a human decision.
 *
 * Reads GET /api/actions/authorizations (pending by default) and lets an
 * admin approve or deny each row via POST …/{id}/decide. This is the surface
 * that makes a suspended run actionable: when the autonomy dial says a person
 * must decide (for example a production deploy below the dial), the run waits
 * here until someone grants or denies it.
 *
 * A row past its expiry still says `pending` (expiry is enforced at the
 * transition, not by a sweeper); the API flags it as `expired` and a decide
 * would 409, so the buttons are hidden for those rows.
 */

import { useCallback, useEffect, useState, type JSX } from 'react';
import { LoadingSpinner } from '../../../components/common/LoadingSpinner.js';
import {
  actionsPolicyApi,
  type ActionAuthorization,
  type AuthorizationDecision,
} from '../../../services/admin/actions-policy-api-client.js';

function formatUtc(value: string | null): string {
  if (value === null) return '—';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}

export function AuthorizationsPanel(): JSX.Element {
  const [rows, setRows] = useState<ActionAuthorization[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [decidingId, setDecidingId] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const response = await actionsPolicyApi.listAuthorizations('pending');
      setRows(response.authorizations);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load pending authorizations');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const decide = useCallback(
    async (id: string, decision: AuthorizationDecision) => {
      setActionError(null);
      setDecidingId(id);
      try {
        await actionsPolicyApi.decideAuthorization(id, decision);
        await load();
      } catch (err) {
        setActionError(err instanceof Error ? err.message : 'Decision failed');
      } finally {
        setDecidingId(null);
      }
    },
    [load],
  );

  if (loading && rows.length === 0) {
    return (
      <div className="flex justify-center py-16">
        <LoadingSpinner size="lg" />
      </div>
    );
  }

  if (error !== null) {
    return (
      <div className="bg-red-50 border border-red-200 rounded-md p-4 text-sm text-red-700 dark:bg-red-950 dark:text-red-300 dark:border-red-800">
        <div className="font-medium mb-1">Failed to load pending authorizations</div>
        <div className="mb-3">{error}</div>
        <button
          type="button"
          onClick={() => void load()}
          className="px-3 py-1.5 text-xs font-medium text-red-700 border border-red-300 bg-white rounded-md hover:bg-red-100 dark:bg-gray-800 dark:text-red-300 dark:border-red-700"
        >
          Retry
        </button>
      </div>
    );
  }

  if (rows.length === 0) {
    return (
      <div
        data-testid="authorizations-empty"
        className="bg-white rounded-lg border border-gray-200 shadow-sm p-6 text-sm text-gray-600 dark:bg-gray-800 dark:border-gray-700 dark:text-gray-400"
      >
        <p className="font-medium text-gray-900 mb-1 dark:text-gray-100">
          Nothing is waiting on a person.
        </p>
        <p>
          Actions that need a human decision — for example a production deploy below the
          autonomy dial — appear here until someone approves or denies them.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {actionError !== null && (
        <div
          role="alert"
          className="bg-red-50 border border-red-200 rounded-md p-3 text-sm text-red-700 dark:bg-red-950 dark:text-red-300 dark:border-red-800"
        >
          {actionError}
        </div>
      )}

      <div className="overflow-x-auto border border-gray-200 rounded-md dark:border-gray-700">
        <table className="min-w-full text-sm">
          <thead className="bg-gray-50 dark:bg-gray-800">
            <tr>
              <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Action</th>
              <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Requested</th>
              <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Expires</th>
              <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Dial at request</th>
              <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Reason</th>
              <th className="px-4 py-2 text-left font-medium text-gray-600 dark:text-gray-300">Decide</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr
                key={row.id}
                data-testid={`authorization-row-${row.id}`}
                className="border-t border-gray-100 dark:border-gray-800"
              >
                <td className="px-4 py-2">
                  <div className="font-mono text-gray-900 dark:text-gray-100">{row.targetKey}</div>
                  <div className="font-mono text-xs text-gray-500 dark:text-gray-400">
                    run {row.correlationId}
                  </div>
                </td>
                <td className="px-4 py-2 text-gray-700 dark:text-gray-300">
                  {formatUtc(row.requestedAtUtc)}
                </td>
                <td className="px-4 py-2 text-gray-700 dark:text-gray-300">
                  {formatUtc(row.expiresAtUtc)}
                  {row.expired && (
                    <span
                      data-testid={`authorization-expired-${row.id}`}
                      className="ml-2 inline-block px-2 py-0.5 rounded text-xs font-medium bg-amber-100 text-amber-800 dark:bg-amber-950 dark:text-amber-300"
                    >
                      Expired
                    </span>
                  )}
                </td>
                <td className="px-4 py-2 text-gray-700 dark:text-gray-300">
                  {row.autonomyLevelAtRequest ?? '—'}
                </td>
                <td className="px-4 py-2 text-gray-700 dark:text-gray-300">{row.reason ?? '—'}</td>
                <td className="px-4 py-2">
                  {row.expired ? (
                    <span className="text-xs text-gray-500 dark:text-gray-400">
                      Expired — no longer decidable
                    </span>
                  ) : (
                    <div className="flex items-center gap-2">
                      <button
                        type="button"
                        aria-label={`Approve ${row.targetKey}`}
                        disabled={decidingId !== null}
                        onClick={() => void decide(row.id, 'granted')}
                        className="px-3 py-1 text-xs font-medium text-white bg-green-600 rounded hover:bg-green-700 disabled:opacity-40"
                      >
                        Approve
                      </button>
                      <button
                        type="button"
                        aria-label={`Deny ${row.targetKey}`}
                        disabled={decidingId !== null}
                        onClick={() => void decide(row.id, 'denied')}
                        className="px-3 py-1 text-xs font-medium text-red-700 border border-red-300 rounded hover:bg-red-50 disabled:opacity-40 dark:text-red-300 dark:border-red-700 dark:hover:bg-red-950"
                      >
                        Deny
                      </button>
                    </div>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
