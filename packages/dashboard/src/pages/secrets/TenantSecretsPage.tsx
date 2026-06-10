import { useMemo, type JSX } from 'react';
import { SecretsListView } from '../../components/secrets/SecretsListView.js';
import { tenantSecretsApi } from '../../services/secrets/secrets-api-client.js';
import { useCurrentTenant } from '../../hooks/orgs/useCurrentTenant.js';

/**
 * Story 29-5 — tenant-admin secrets management page. Defense-in-depth:
 *   1. TenantAdminGuard on the route rejects member-level callers.
 *   2. Endpoint filter enforces tenant membership (server).
 *   3. Endpoint handler enforces admin+ role for writes (server).
 *   4. Query service scope filter (server, four-layer defense-in-depth).
 *   5. RLS on `secrets` (when the tenant connection is wired).
 *
 * Tenant id comes from the caller's active tenant context, NOT the URL —
 * this prevents a tenant admin from tampering with another tenant's
 * secrets by forging a path segment.
 */
export function TenantSecretsPage(): JSX.Element {
  const { tenantId, loading } = useCurrentTenant();

  const api = useMemo(() => {
    if (!tenantId) return null;
    return tenantSecretsApi(tenantId);
  }, [tenantId]);

  if (loading) {
    return <p className="text-sm text-gray-500 dark:text-gray-400">Loading…</p>;
  }

  if (!api || !tenantId) {
    return (
      <div className="bg-white border border-gray-200 rounded-lg p-8 text-center dark:bg-gray-800 dark:border-gray-700">
        <p className="text-sm text-gray-600 dark:text-gray-400">
          No active tenant selected. Pick an organization from the switcher to
          manage its secrets.
        </p>
      </div>
    );
  }

  return (
    <div>
      <h1 className="text-2xl font-bold text-gray-900 mb-1 dark:text-gray-100">Organization secrets</h1>
      <p className="text-sm text-gray-600 mb-6 dark:text-gray-400">
        Tenant-scoped credentials (database users, Cranl API keys, webhook
        HMACs). Values are stored envelope-encrypted and isolated to this
        organization; the plaintext is revealed to you exactly once at
        creation or rotation.
      </p>
      <SecretsListView
        api={api}
        scopeLabel="Organization"
        tenantId={tenantId}
        emptyStateMessage="No secrets yet. When you create a tenant-scoped DB user, Cranl API key, or webhook HMAC, it shows up here."
      />
    </div>
  );
}
