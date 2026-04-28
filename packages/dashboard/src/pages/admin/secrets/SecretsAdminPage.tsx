import { SecretsListView } from '../../../components/secrets/SecretsListView.js';
import { platformSecretsApi } from '../../../services/secrets/secrets-api-client.js';

import type { JSX } from "react";

/**
 * Story 29-4 — platform-admin secrets management page at
 * `/admin/secrets`. Thin wrapper over <SecretsListView /> with
 * `platformSecretsApi` bound. `AdminGuard` on the route ensures only
 * platform-admin users reach this page; the server endpoints are
 * also gated by the `OwnerAccess` policy (defense-in-depth).
 */
export function SecretsAdminPage(): JSX.Element {
  return (
    <div>
      <h1 className="text-2xl font-bold text-gray-900 mb-1">Platform secrets</h1>
      <p className="text-sm text-gray-600 mb-6">
        Database credentials, webhook HMACs, and other operational secrets used
        by the Tamma control plane. Values are stored envelope-encrypted; the
        plaintext is revealed to you exactly once at creation or rotation.
      </p>
      <SecretsListView
        api={platformSecretsApi}
        scopeLabel="Platform"
        emptyStateMessage="No platform secrets yet. Create one to begin rotating control-plane credentials through this UI."
      />
    </div>
  );
}
