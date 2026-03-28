/**
 * Admin API Client
 *
 * Typed HTTP client for the admin panel, communicating with:
 * - /api/auth/me — current user identity
 * - /api/admin/users — user management (Story 16.2)
 * - /api/admin/users/:id/keys — per-user API key management
 * - /api/admin/health — system health aggregation
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
    throw new Error((error as Record<string, string>).error ?? `HTTP ${response.status}`);
  }

  return response.json() as Promise<T>;
}

// === Types ===

export interface CurrentUser {
  id: string;
  username: string;
  githubId: number;
  role: string;
}

export interface AdminUser {
  id: string;
  githubId: number;
  githubLogin: string;
  email: string | null;
  role: 'owner' | 'admin' | 'member';
  lastActiveAt?: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface ListUsersResult {
  users: AdminUser[];
  total: number;
}

export interface ApiKeyEntry {
  id: string;
  keyPrefix: string;
  label: string;
  userId: string;
  lastUsedAt: string | null;
  createdAt: string;
  revokedAt: string | null;
}

export interface CreateApiKeyResult {
  id: string;
  key: string;
  prefix: string;
  label: string;
  createdAt: string;
}

export interface InviteResult {
  id: string;
  inviteLink: string;
  role: string;
  expiresAt: string;
}

export interface ServiceHealth {
  name: string;
  status: 'healthy' | 'unhealthy' | 'unknown';
  responseTime: number | null;
  checkedAt: string;
  details?: string;
}

export interface SystemHealthResult {
  services: ServiceHealth[];
  checkedAt: string;
}

// === Auth ===

export const authApi = {
  getMe: () =>
    fetchJSON<{ user: CurrentUser }>('/auth/me').then((r) => r.user),
};

// === Users (matches Story 16.2 routes) ===

export const usersApi = {
  list: (options?: { limit?: number; offset?: number; role?: string }) => {
    const params = new URLSearchParams();
    if (options?.limit !== undefined) params.set('limit', String(options.limit));
    if (options?.offset !== undefined) params.set('offset', String(options.offset));
    if (options?.role !== undefined) params.set('role', options.role);
    const qs = params.toString();
    return fetchJSON<ListUsersResult>(`/admin/users${qs ? `?${qs}` : ''}`);
  },

  get: (userId: string) =>
    fetchJSON<{ user: AdminUser; installations: unknown[]; apiKeys: ApiKeyEntry[] }>(
      `/admin/users/${userId}`,
    ),

  updateRole: (userId: string, role: 'owner' | 'admin' | 'member') =>
    fetchJSON<{ user: AdminUser }>(`/admin/users/${userId}/role`, {
      method: 'PUT',
      body: JSON.stringify({ role }),
    }),

  remove: (userId: string) =>
    fetchJSON<{ ok: boolean }>(`/admin/users/${userId}`, {
      method: 'DELETE',
    }),

  invite: (data: { email?: string; role: string }) =>
    fetchJSON<InviteResult>('/admin/users/invite', {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  listInvites: () =>
    fetchJSON<{ invites: unknown[] }>('/admin/users/invites'),
};

// === Per-User API Keys (matches Story 16.2 routes) ===

export const apiKeysApi = {
  list: (userId: string) =>
    fetchJSON<{ apiKeys: ApiKeyEntry[] }>(`/admin/users/${userId}/keys`).then((r) => r.apiKeys),

  create: (userId: string, label: string) =>
    fetchJSON<CreateApiKeyResult>(`/admin/users/${userId}/keys`, {
      method: 'POST',
      body: JSON.stringify({ label }),
    }),

  revoke: (userId: string, keyId: string) =>
    fetchJSON<{ ok: boolean }>(`/admin/users/${userId}/keys/${keyId}`, {
      method: 'DELETE',
    }),
};

// === System Health (new admin health aggregation) ===

export const systemHealthApi = {
  getHealth: () => fetchJSON<SystemHealthResult>('/admin/health'),
};
