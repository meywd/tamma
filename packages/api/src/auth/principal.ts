/**
 * AuthPrincipal — Tagged union type for authenticated API callers.
 *
 * Every authenticated request is associated with exactly one principal
 * variant, determined by the scope of the API key used:
 *
 *   - user:         Human user (via user_api_keys or dashboard)
 *   - installation: GitHub App installation (CLI runners)
 *   - service:      Internal service (Elsa, tamma-api-dotnet)
 */

import type { Role } from './permissions.js';

export type AuthPrincipal =
  | { scope: 'user'; keyId: string; userId: string; role: Role; tenantId: string }
  | { scope: 'installation'; keyId: string; installationId: number; tenantId: string }
  | {
      scope: 'service';
      keyId: string;
      serviceName: string;
      permissions: string[];
      tenantId: string | null; // null until X-Tenant-Id header is parsed
    };
