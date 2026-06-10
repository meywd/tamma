/**
 * Conventions API Client (Story 27-11)
 *
 * Typed wrapper for `/api/conventions/*` and `/api/admin/conventions/*`
 * endpoints exposed by `apps/tamma-elsa/src/Tamma.Api/Endpoints/ConventionEndpoints.cs`.
 *
 * Convention entity fields:
 *   id, role, action, body, enabled, version, source ("tenant"|"system"),
 *   isOverride, updatedAt.
 * NO name/description fields.
 *
 * Error codes the API returns (surface in toasts/inline):
 *   INVALID_ROLE_ACTION     (400) — unknown role or action token
 *   INELIGIBLE_ROLE_ACTION  (400) — known but ineligible pair (e.g. developer/deploy)
 *   CONVENTION_NOT_FOUND    (404) — no convention for that (role, action)
 *   CONVENTION_BODY_REQUIRED (400) — body field missing or blank
 *   CONCURRENT_UPSERT_CONFLICT (409) — concurrent race on upsert
 */

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '/api';

export interface ApiError extends Error {
  status?: number;
  code?: string;
}

async function fetchJSON<T>(url: string, options?: RequestInit): Promise<T> {
  const response = await fetch(`${API_BASE}${url}`, {
    headers: { 'Content-Type': 'application/json', ...options?.headers },
    credentials: 'include',
    ...options,
  });

  if (!response.ok) {
    const body = await response.json().catch(() => ({ error: response.statusText })) as Record<string, string>;
    const err = new Error(body['error'] ?? `HTTP ${response.status}`) as ApiError;
    err.status = response.status;
    err.code = body['code'];
    throw err;
  }

  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

// === Types ===================================================================

/** Provenance of a convention — `"system"` (shipped default) or `"tenant"` (override). */
export type ConventionSource = 'system' | 'tenant';

export interface ConventionResponse {
  id: string;
  role: string;
  action: string;
  body: string;
  enabled: boolean;
  version: number;
  source: ConventionSource;
  /** Only present on tenant-view responses. */
  isOverride?: boolean;
  updatedAt: string;
}

export interface UpsertConventionRequest {
  body: string;
  enabled: boolean;
}

export interface ResolveRequest {
  role: string;
  action: string;
}

export interface ResolveResponse extends ConventionResponse {
  source: ConventionSource;
}

export interface RegistryResponse {
  roles: string[];
  actions: string[];
  /** Array of `{ role, action }` eligible pairs. */
  roleActions: { role: string; action: string }[];
}

// === Tenant API ==============================================================

export const conventionsApi = {
  /** `GET /api/conventions` — resolved list with isOverride flag. */
  list: () => fetchJSON<ConventionResponse[]>('/conventions'),

  /** `POST /api/conventions/resolve` — resolve a single (role,action). 404 on miss; 400 on ineligible. */
  resolve: (req: ResolveRequest) =>
    fetchJSON<ResolveResponse>('/conventions/resolve', {
      method: 'POST',
      body: JSON.stringify(req),
    }),

  /** `GET /api/conventions/:role/:action` — get a specific convention. */
  get: (role: string, action: string) =>
    fetchJSON<ConventionResponse>(
      `/conventions/${encodeURIComponent(role)}/${encodeURIComponent(action)}`,
    ),

  /** `PUT /api/conventions/:role/:action` — upsert tenant override. */
  upsert: (role: string, action: string, req: UpsertConventionRequest) =>
    fetchJSON<ConventionResponse>(
      `/conventions/${encodeURIComponent(role)}/${encodeURIComponent(action)}`,
      { method: 'PUT', body: JSON.stringify(req) },
    ),

  /** `DELETE /api/conventions/:role/:action` — remove tenant override. */
  delete: (role: string, action: string) =>
    fetchJSON<{ message: string }>(
      `/conventions/${encodeURIComponent(role)}/${encodeURIComponent(action)}`,
      { method: 'DELETE' },
    ),
};

// === Admin API ===============================================================

export const adminConventionsApi = {
  /** `GET /api/conventions/defaults` — list all system defaults. */
  listDefaults: () => fetchJSON<ConventionResponse[]>('/conventions/defaults'),

  /** `GET /api/conventions/defaults/:role/:action` — single system default. */
  getDefault: (role: string, action: string) =>
    fetchJSON<ConventionResponse>(
      `/conventions/defaults/${encodeURIComponent(role)}/${encodeURIComponent(action)}`,
    ),

  /** `PUT /api/admin/conventions/:role/:action` — set system default. */
  upsert: (role: string, action: string, req: UpsertConventionRequest) =>
    fetchJSON<ConventionResponse>(
      `/admin/conventions/${encodeURIComponent(role)}/${encodeURIComponent(action)}`,
      { method: 'PUT', body: JSON.stringify(req) },
    ),

  /** `DELETE /api/admin/conventions/:role/:action` — remove customisation (falls back to seed). */
  delete: (role: string, action: string) =>
    fetchJSON<{ message: string }>(
      `/admin/conventions/${encodeURIComponent(role)}/${encodeURIComponent(action)}`,
      { method: 'DELETE' },
    ),

  /** `POST /api/admin/conventions/:role/:action/reset` — reset to ConventionSeedSpecs default. */
  reset: (role: string, action: string) =>
    fetchJSON<ConventionResponse>(
      `/admin/conventions/${encodeURIComponent(role)}/${encodeURIComponent(action)}/reset`,
      { method: 'POST' },
    ),
};

// === Registry API ============================================================

export const conventionRegistryApi = {
  /** `GET /api/conventions/registry/roles` */
  getRoles: () => fetchJSON<string[]>('/conventions/registry/roles'),

  /** `GET /api/conventions/registry/actions` */
  getActions: () => fetchJSON<string[]>('/conventions/registry/actions'),

  /** `GET /api/conventions/registry/role-actions` — eligible (role,action) pairs. */
  getRoleActions: () =>
    fetchJSON<{ role: string; action: string }[]>('/conventions/registry/role-actions'),
};
