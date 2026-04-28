/**
 * Tenant-aware /auth/me fetcher (Story 18-8).
 *
 * The platform-admin client at services/admin/admin-api-client.ts narrows
 * the `/auth/me` response to `{ id, username, githubId, role }` — that's
 * the platform role only. The tenant-admin UI needs the full memberships
 * + active tenant id so it can derive the caller's role inside the
 * current tenant. This hits the same endpoint with a wider type.
 */

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '/api';

async function fetchJSON<T>(url: string): Promise<T> {
  const r = await fetch(`${API_BASE}${url}`, { credentials: 'include' });
  if (!r.ok) throw new Error(`${r.status}`);
  return r.json() as Promise<T>;
}

export interface MeMembership {
  tenantId: string;
  tenantName: string;
  /** Tenant role inside that tenant. */
  role: 'owner' | 'admin' | 'member';
}

export interface MeUserFull {
  id: string;
  email: string;
  displayName: string | null;
  githubId: number | null;
  username: string | null;
  /** Platform-wide role from JWT. */
  role: string;
  /** Platform role mapping (e.g. `platform_admin`, `user`). */
  platformRole: string;
  authMethod: string;
  /** The user's currently active tenant id (mirrors JWT tid claim). */
  tenantId: string | null;
  memberships: MeMembership[];
}

export const meApi = {
  /** Returns the rich MeUserPayload (memberships + active tenant). */
  getFull: () =>
    fetchJSON<{ user: MeUserFull }>('/auth/me').then((r) => r.user),
};

/**
 * Resolves the caller's role inside their currently-active tenant.
 * Returns `null` when the user has no active tenant or no membership
 * row matches (defensive — should not happen in practice).
 */
export function resolveActiveTenantRole(me: MeUserFull): {
  tenantId: string;
  role: 'owner' | 'admin' | 'member';
} | null {
  if (!me.tenantId) return null;
  const m = me.memberships.find((x) => x.tenantId === me.tenantId);
  if (!m) return null;
  return { tenantId: m.tenantId, role: m.role };
}

export function isTenantAdmin(role: 'owner' | 'admin' | 'member' | null): boolean {
  return role === 'owner' || role === 'admin';
}
