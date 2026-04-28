/**
 * Story 29-4 + 29-5 — typed HTTP client for the platform-admin and
 * tenant-admin secret-management endpoints defined in
 * `apps/tamma-elsa/src/Tamma.Api/Endpoints/SecretEndpoints.cs`.
 *
 * Routes consumed (platform, gated by OwnerAccess):
 *   GET    /api/v1/admin/secrets
 *   GET    /api/v1/admin/secrets/:id
 *   GET    /api/v1/admin/secrets/:id/versions
 *   POST   /api/v1/admin/secrets           (create; reveal-once)
 *   POST   /api/v1/admin/secrets/:id/rotate (rotate; reveal-once)
 *   POST   /api/v1/admin/secrets/:id/retire-version/:n
 *
 * Routes consumed (tenant, gated by tenant membership + admin role):
 *   GET    /api/v1/orgs/:tenantId/secrets
 *   GET    /api/v1/orgs/:tenantId/secrets/:id
 *   GET    /api/v1/orgs/:tenantId/secrets/:id/versions
 *   POST   /api/v1/orgs/:tenantId/secrets           (create; reveal-once)
 *   POST   /api/v1/orgs/:tenantId/secrets/:id/rotate
 *   POST   /api/v1/orgs/:tenantId/secrets/:id/retire-version/:n
 *
 * Reveal exchange (shared; token IS the auth):
 *   GET    /api/v1/secrets/reveal/:token
 *
 * Every typed body below matches the exact wire shape that
 * SecretEndpoints.ToListItem / ToDetail / ToVersionItem / ToIssueResponse
 * produce — the default System.Text.Json camelCase policy gives us
 * these field names.
 */

const API_BASE = (import.meta.env as { VITE_API_BASE_URL?: string }).VITE_API_BASE_URL ?? '/api';

export class SecretApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly body: unknown,
  ) {
    super(message);
    this.name = 'SecretApiError';
  }
}

async function fetchJSON<T>(url: string, options?: RequestInit): Promise<T> {
  const response = await fetch(`${API_BASE}${url}`, {
    headers: { 'Content-Type': 'application/json', ...options?.headers },
    credentials: 'include',
    ...options,
  });
  if (!response.ok) {
    const err = (await response.json().catch(() => ({ error: response.statusText }))) as Record<
      string,
      unknown
    >;
    const message =
      (err.message as string | undefined) ??
      (err.error as string | undefined) ??
      `HTTP ${response.status}`;
    throw new SecretApiError(response.status, message, err);
  }
  if (response.status === 204) return undefined as unknown as T;
  return (await response.json()) as T;
}

// ── Wire types ────────────────────────────────────────────────────

export type SecretScope = 'platform' | 'tenant';

export type SecretPurpose =
  | 'Generic'
  | 'DbCredential'
  | 'ApiKey'
  | 'HmacSharedSecret'
  | 'WebhookSigning'
  | 'JwtSigning'
  | 'EncryptionKey'
  | 'OAuthClientSecret';

export type ConsumerRefType =
  | 'postgres'
  | 'cranl'
  | 'github_webhook'
  | 'hmac_shared'
  | 'tamma_engine'
  | 'generic';

export interface ConsumerRef {
  type: ConsumerRefType;
  /** Context-dependent: role name (postgres), app id (cranl), installation id (github_webhook), endpoint (hmac/tamma_engine), opaque (generic). */
  target: string;
  /** Extra free-form labelling (e.g. "db/app-role") for the UI. */
  label?: string;
}

export interface SecretListItem {
  secretId: string;
  name: string;
  scope: SecretScope;
  tenantId: string | null;
  purpose: SecretPurpose;
  consumerRefs: ConsumerRef[];
  activeVersion: number;
  lastRotatedAt: string | null;
  nextRotationDueAt: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface SecretDetail extends SecretListItem {
  ownerUserId: string;
  rotationSchedule: {
    kind: 'None' | 'Days' | 'Cron';
    days: number | null;
    cronExpression: string | null;
  };
}

export interface SecretVersionItem {
  secretId: string;
  versionNumber: number;
  status: 'Pending' | 'Active' | 'RetiredGrace' | 'Revoked';
  createdAt: string;
  activatedAt: string | null;
  retiredAt: string | null;
  createdByUserId: string;
}

export interface RevealEnvelope {
  secretId: string;
  name: string;
  scope: SecretScope;
  tenantId: string | null;
  purpose: SecretPurpose;
  activeVersion: number;
  createdAt: string;
  updatedAt: string;
  revealToken: string;
  revealExpiresAt: string;
  revealUrl: string;
  message: string;
}

export interface CreateSecretBody {
  name: string;
  purpose: SecretPurpose;
  plaintext: string;
  consumerRefs?: ConsumerRef[];
  rotationDays?: number;
}

export interface RotateSecretBody {
  newPlaintext: string;
}

export interface RevealResult {
  secretId: string;
  name: string;
  version: number;
  plaintext: string;
  expiresAt: string;
}

// ── API surface ───────────────────────────────────────────────────

export const platformSecretsApi = {
  list: (): Promise<{ secrets: SecretListItem[] }> => fetchJSON('/v1/admin/secrets'),

  get: (secretId: string): Promise<SecretDetail> =>
    fetchJSON(`/v1/admin/secrets/${secretId}`),

  listVersions: (secretId: string): Promise<{ versions: SecretVersionItem[] }> =>
    fetchJSON(`/v1/admin/secrets/${secretId}/versions`),

  create: (body: CreateSecretBody): Promise<RevealEnvelope> =>
    fetchJSON('/v1/admin/secrets', {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  rotate: (secretId: string, body: RotateSecretBody): Promise<RevealEnvelope> =>
    fetchJSON(`/v1/admin/secrets/${secretId}/rotate`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  retireVersion: (
    secretId: string,
    versionNumber: number,
  ): Promise<{ secretId: string; versionNumber: number; status: string }> =>
    fetchJSON(`/v1/admin/secrets/${secretId}/retire-version/${versionNumber}`, {
      method: 'POST',
    }),
};

export function tenantSecretsApi(tenantId: string) {
  return {
    list: (): Promise<{ secrets: SecretListItem[] }> =>
      fetchJSON(`/v1/orgs/${tenantId}/secrets`),

    get: (secretId: string): Promise<SecretDetail> =>
      fetchJSON(`/v1/orgs/${tenantId}/secrets/${secretId}`),

    listVersions: (secretId: string): Promise<{ versions: SecretVersionItem[] }> =>
      fetchJSON(`/v1/orgs/${tenantId}/secrets/${secretId}/versions`),

    create: (body: CreateSecretBody): Promise<RevealEnvelope> =>
      fetchJSON(`/v1/orgs/${tenantId}/secrets`, {
        method: 'POST',
        body: JSON.stringify(body),
      }),

    rotate: (secretId: string, body: RotateSecretBody): Promise<RevealEnvelope> =>
      fetchJSON(`/v1/orgs/${tenantId}/secrets/${secretId}/rotate`, {
        method: 'POST',
        body: JSON.stringify(body),
      }),

    retireVersion: (
      secretId: string,
      versionNumber: number,
    ): Promise<{ secretId: string; versionNumber: number; status: string }> =>
      fetchJSON(
        `/v1/orgs/${tenantId}/secrets/${secretId}/retire-version/${versionNumber}`,
        { method: 'POST' },
      ),
  };
}

export const revealApi = {
  consume: (token: string): Promise<RevealResult> =>
    fetchJSON(`/v1/secrets/reveal/${encodeURIComponent(token)}`),
};
