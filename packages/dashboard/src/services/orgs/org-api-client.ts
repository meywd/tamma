/**
 * Org API Client (Story 18-8)
 *
 * Typed HTTP client for tenant-admin user-management endpoints exposed
 * by `OrgEndpoints.cs` (Story 18-3) plus the three completion handlers
 * added in Story 18-7 (resend invite, tenant audit, role-changed event).
 *
 * Mirrors the shape of `admin-api-client.ts` so the calling components
 * can swap call sites trivially. All endpoints under
 * `/api/v1/orgs/{tenantId}/*` require tenant-admin role; the backend
 * enforces this via `RequireTenantMembershipFilter` + per-handler role
 * checks.
 */

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '/api';

async function fetchJSON<T>(url: string, options?: RequestInit): Promise<T> {
  const response = await fetch(`${API_BASE}${url}`, {
    headers: { 'Content-Type': 'application/json', ...options?.headers },
    credentials: 'include',
    ...options,
  });

  if (!response.ok) {
    const error = await response.json().catch(() => ({ error: response.statusText }));
    const err = new Error((error as Record<string, string>).error ?? `HTTP ${response.status}`);
    // Stamp the status so callers can branch on 400 / 403 / 429 without
    // round-tripping the message string through error-copy mappers.
    (err as Error & { status?: number }).status = response.status;
    throw err;
  }

  // Some endpoints return 204 No Content (e.g. revoke invite).
  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

// === Types ==================================================================

export type TenantRole = 'owner' | 'admin' | 'member';

export interface OrgMember {
  userId: string;
  role: TenantRole;
  joinedAt: string;
  displayName: string | null;
  email: string | null;
}

export interface ListMembersResult {
  members: OrgMember[];
  total: number;
  limit: number;
  offset: number;
}

export interface PendingInvite {
  id: string;
  email: string | null;
  role: TenantRole;
  invitedBy: string;
  expiresAt: string;
  createdAt: string;
}

export interface ListInvitesResult {
  invites: PendingInvite[];
}

export interface CreateInviteResult {
  id: string;
  email: string;
  role: TenantRole;
  expiresAt: string;
}

export interface ResendInviteResult {
  id: string;
  expiresAt: string;
}

export interface AuditEvent {
  id: string;
  type: string;
  createdAt: string;
  /** Raw JSON string. Caller `JSON.parse`s. */
  tags: string;
  /** Raw JSON string. Caller `JSON.parse`s. */
  data: string;
}

export interface ListAuditResult {
  events: AuditEvent[];
  total: number;
  limit: number;
  offset: number;
}

// === Members ================================================================

export const orgMembersApi = {
  list: (tenantId: string, options?: { limit?: number; offset?: number }) => {
    const params = new URLSearchParams();
    if (options?.limit !== undefined) params.set('limit', String(options.limit));
    if (options?.offset !== undefined) params.set('offset', String(options.offset));
    const qs = params.toString();
    return fetchJSON<ListMembersResult>(
      `/v1/orgs/${tenantId}/members${qs ? `?${qs}` : ''}`,
    );
  },

  updateRole: (tenantId: string, userId: string, role: TenantRole) =>
    fetchJSON<{ message: string; tenantId: string; userId: string; role: TenantRole }>(
      `/v1/orgs/${tenantId}/members/${userId}/role`,
      {
        method: 'PUT',
        body: JSON.stringify({ role }),
      },
    ),

  remove: (tenantId: string, userId: string) =>
    fetchJSON<{ ok: boolean }>(`/v1/orgs/${tenantId}/members/${userId}`, {
      method: 'DELETE',
    }),
};

// === Invites ================================================================

export const orgInvitesApi = {
  list: (tenantId: string) =>
    fetchJSON<ListInvitesResult>(`/v1/orgs/${tenantId}/invites`),

  create: (tenantId: string, data: { email: string; role: TenantRole }) =>
    fetchJSON<CreateInviteResult>(`/v1/orgs/${tenantId}/invites`, {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  /** Story 18-7: extends expiry, re-sends email, does NOT rotate token. */
  resend: (tenantId: string, inviteId: string) =>
    fetchJSON<ResendInviteResult>(
      `/v1/orgs/${tenantId}/invites/${inviteId}/resend`,
      { method: 'POST' },
    ),

  revoke: (tenantId: string, inviteId: string) =>
    fetchJSON<{ ok: boolean }>(`/v1/orgs/${tenantId}/invites/${inviteId}`, {
      method: 'DELETE',
    }),
};

// === Audit ==================================================================

export const orgAuditApi = {
  /** Story 18-7: tenant-scoped event-store read with prefix-type filter. */
  list: (
    tenantId: string,
    options?: { limit?: number; offset?: number; type?: string },
  ) => {
    const params = new URLSearchParams();
    if (options?.limit !== undefined) params.set('limit', String(options.limit));
    if (options?.offset !== undefined) params.set('offset', String(options.offset));
    if (options?.type !== undefined && options.type.length > 0) {
      params.set('type', options.type);
    }
    const qs = params.toString();
    return fetchJSON<ListAuditResult>(
      `/v1/orgs/${tenantId}/audit${qs ? `?${qs}` : ''}`,
    );
  },
};
