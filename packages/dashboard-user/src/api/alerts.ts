/**
 * Tenant-scope alert API — mirrors Story 5.6 / 1.5-37 (Wave C.3)
 * /api/v1/orgs/{tenantId}/alerts/* and /alert-channels/*. All calls
 * use the ApiClient so the refresh-on-401 dance is inherited.
 *
 * IMPORTANT: we NEVER send plaintext credentials on channel create —
 * the `credentialsSecretId` field carries a secret-store id. Any
 * client code that tries to attach a `password`/`webhookUrl` field
 * in the config blob is rejected server-side with 400 (mirrored in
 * `hasPlaintextCredential()` for a snappy client-side pre-flight).
 */

import { apiClient } from './client';

export type AlertSeverity = 'critical' | 'warning' | 'info';
export type AlertStatus = 'active' | 'acknowledged' | 'resolved';

export interface AlertDto {
  id: string;
  ruleId: string | null;
  severity: AlertSeverity;
  title: string;
  description: string;
  correlationId: string | null;
  tenantId: string | null;
  metadata: string;
  status: AlertStatus;
  acknowledgedBy: string | null;
  acknowledgedAt: string | null;
  resolvedBy: string | null;
  resolvedAt: string | null;
  resolution: string | null;
  createdAt: string;
}

export interface AlertListResponse {
  items: AlertDto[];
  count: number;
  limit: number;
}

export interface DeliveryAttemptDto {
  id: string;
  channelId: string;
  attemptNumber: number;
  status: string;
  error: string | null;
  deliveredAt: string | null;
  nextAttemptAt: string | null;
  createdAt: string;
}

export interface AlertDetailResponse {
  alert: AlertDto;
  deliveryAttempts: DeliveryAttemptDto[];
}

export interface ChannelDto {
  id: string;
  tenantId: string | null;
  name: string;
  channelType: string;
  isEnabled: boolean;
  config: string;
  credentialsSecretId: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface ChannelListResponse {
  items: ChannelDto[];
  count: number;
}

export interface CreateChannelBody {
  name: string;
  channelType: 'email' | 'slack' | 'pagerduty' | 'webhook';
  tenantId?: string | null;
  config?: string;
  credentialsSecretId?: string | null;
}

export interface UpdateChannelBody {
  name?: string | null;
  isEnabled?: boolean | null;
  config?: string | null;
}

export interface ListAlertsParams {
  status?: AlertStatus;
  severity?: AlertSeverity;
  sinceDays?: number;
  limit?: number;
}

export async function listTenantAlerts(
  tenantId: string,
  params: ListAlertsParams = {},
): Promise<AlertListResponse> {
  const qs = new URLSearchParams();
  if (params.status) qs.set('status', params.status);
  if (params.severity) qs.set('severity', params.severity);
  if (params.sinceDays !== undefined) {
    const since = new Date(Date.now() - params.sinceDays * 86400_000);
    qs.set('since', since.toISOString());
  }
  if (params.limit !== undefined) qs.set('limit', String(params.limit));
  const suffix = qs.toString() ? `?${qs.toString()}` : '';
  return apiClient.get<AlertListResponse>(
    `/api/v1/orgs/${tenantId}/alerts${suffix}`,
  );
}

export async function getTenantAlert(
  tenantId: string,
  alertId: string,
): Promise<AlertDetailResponse> {
  return apiClient.get<AlertDetailResponse>(
    `/api/v1/orgs/${tenantId}/alerts/${alertId}`,
  );
}

export async function acknowledgeTenantAlert(
  tenantId: string,
  alertId: string,
  note?: string,
): Promise<AlertDto> {
  return apiClient.post<AlertDto>(
    `/api/v1/orgs/${tenantId}/alerts/${alertId}/acknowledge`,
    { note: note ?? null },
  );
}

export async function resolveTenantAlert(
  tenantId: string,
  alertId: string,
  resolution: string,
): Promise<AlertDto> {
  return apiClient.post<AlertDto>(
    `/api/v1/orgs/${tenantId}/alerts/${alertId}/resolve`,
    { resolution },
  );
}

export async function listTenantChannels(
  tenantId: string,
): Promise<ChannelListResponse> {
  return apiClient.get<ChannelListResponse>(
    `/api/v1/orgs/${tenantId}/alert-channels`,
  );
}

export async function createTenantChannel(
  tenantId: string,
  body: CreateChannelBody,
): Promise<ChannelDto> {
  if (hasPlaintextCredential(body.config)) {
    throw new Error(
      'Config must not contain plaintext credentials. ' +
        'Use `credentialsSecretId` + the secret store instead.',
    );
  }
  return apiClient.post<ChannelDto>(
    `/api/v1/orgs/${tenantId}/alert-channels`,
    body,
  );
}

export async function updateTenantChannel(
  tenantId: string,
  channelId: string,
  body: UpdateChannelBody,
): Promise<ChannelDto> {
  if (body.config != null && hasPlaintextCredential(body.config)) {
    throw new Error(
      'Config must not contain plaintext credentials. ' +
        'Use `credentialsSecretId` + the secret store instead.',
    );
  }
  // Re-use ApiClient's PATCH pattern via fetch directly (no apiClient.patch).
  const resp = await fetch(
    `${apiClientBaseUrl()}/api/v1/orgs/${tenantId}/alert-channels/${channelId}`,
    {
      method: 'PATCH',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify(body),
    },
  );
  if (!resp.ok) {
    const text = await resp.text();
    throw new Error(`PATCH failed ${resp.status}: ${text}`);
  }
  return (await resp.json()) as ChannelDto;
}

export async function deleteTenantChannel(
  tenantId: string,
  channelId: string,
): Promise<void> {
  await apiClient.delete<null>(
    `/api/v1/orgs/${tenantId}/alert-channels/${channelId}`,
  );
}

/**
 * Client-side pre-flight mirror of the server-side
 * `ContainsPlaintextCredential`. Rejects before we even send the
 * POST so the UX surfaces the error instantly. The server remains
 * authoritative — this is a UX speedup, not a security boundary.
 */
export function hasPlaintextCredential(configJson?: string | null): boolean {
  if (!configJson || configJson.trim() === '' || configJson.trim() === '{}') {
    return false;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(configJson);
  } catch {
    // Malformed JSON: server will return 400 with a useful error.
    return false;
  }
  if (typeof parsed !== 'object' || parsed === null) return false;
  const banned = new Set(
    [
      'webhookurl',
      'webhook_url',
      'routingkey',
      'routing_key',
      'password',
      'apikey',
      'api_key',
      'secret',
      'sharedsecret',
      'shared_secret',
      'token',
      'authtoken',
      'auth_token',
    ].map((s) => s.toLowerCase()),
  );
  for (const key of Object.keys(parsed)) {
    if (banned.has(key.toLowerCase())) return true;
  }
  return false;
}

function apiClientBaseUrl(): string {
  return (
    (typeof import.meta !== 'undefined' &&
      (import.meta as { env?: Record<string, string> }).env?.VITE_API_URL) ||
    ''
  );
}
