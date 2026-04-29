/**
 * ConnectedPlatforms — /settings/platforms
 *
 * Story 31-9 — post-onboarding management panel. Lists every
 * tenant_platform_installations row for the caller's tenant. Cross-
 * tenant scoping is enforced server-side by
 * `IPlatformConnectService.ListForTenantAsync` (which keys on the
 * tenant id derived from the JWT, not the URL).
 *
 * Future stories add disconnect / rotate buttons; first cut keeps the
 * panel read-only so we don't need to wire 31-7's
 * `PLATFORM.INSTALLATION.DISCONNECTED` event emission yet.
 */

import { useEffect, useState, type JSX } from 'react';
import { Link } from 'react-router-dom';
import {
  listConnectedPlatforms,
  type PlatformConnection,
} from '../../api/platforms';

export function ConnectedPlatforms(): JSX.Element {
  const [items, setItems] = useState<PlatformConnection[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const resp = await listConnectedPlatforms();
        if (!cancelled) {
          setItems(resp.items);
          setLoading(false);
        }
      } catch (err) {
        if (!cancelled) {
          setError(
            err instanceof Error
              ? err.message
              : 'Failed to load connected platforms',
          );
          setLoading(false);
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  if (loading) {
    return (
      <div role="status" className="text-sm text-gray-500">
        Loading connected platforms…
      </div>
    );
  }

  if (error !== null) {
    return (
      <div role="alert" className="p-4 bg-red-50 text-red-700 rounded">
        {error}
      </div>
    );
  }

  return (
    <div>
      <header className="mb-6 flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-semibold text-gray-900">
            Connected platforms
          </h1>
          <p className="mt-1 text-sm text-gray-600">
            Git platforms wired into Tamma for this organization.
          </p>
        </div>
        <Link
          to="/onboarding/platforms"
          className="px-3 py-2 bg-blue-600 text-white rounded-md text-sm hover:bg-blue-700"
        >
          Connect another
        </Link>
      </header>

      {items.length === 0 ? (
        <div className="p-6 bg-gray-50 border border-dashed border-gray-300 rounded text-sm text-gray-600">
          No platforms connected yet. Click "Connect another" to wire your
          first one.
        </div>
      ) : (
        <table className="w-full bg-white border border-gray-200 rounded">
          <thead className="bg-gray-50 text-left text-xs font-medium text-gray-500 uppercase">
            <tr>
              <th className="px-4 py-2">Platform</th>
              <th className="px-4 py-2">Base URL</th>
              <th className="px-4 py-2">Status</th>
              <th className="px-4 py-2">Connected</th>
            </tr>
          </thead>
          <tbody className="text-sm divide-y divide-gray-100">
            {items.map((row) => (
              <tr key={row.installationId}>
                <td className="px-4 py-3 font-medium text-gray-900">
                  {row.kind}
                  {row.isPrimary && (
                    <span className="ml-2 text-xs text-blue-600">
                      primary
                    </span>
                  )}
                </td>
                <td className="px-4 py-3 text-gray-700 font-mono text-xs">
                  {row.baseUrl}
                </td>
                <td className="px-4 py-3 text-gray-700">{row.status}</td>
                <td className="px-4 py-3 text-gray-500 text-xs">
                  {new Date(row.createdAt).toLocaleString()}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
