/**
 * Story 28-11 — typed HTTP client for the platform-admin tenant-status
 * UX endpoints. Mirrors AdminTenantsEndpoints.cs in the C# API.
 *
 * Routes (all gated by OwnerAccess / platform-owner only):
 *   GET   /api/admin/tenants              — list with filters + pagination
 *   GET   /api/admin/tenants/:id/detail   — single tenant + recent events
 *   POST  /api/admin/tenants/:id/actions/retry
 *   POST  /api/admin/tenants/:id/actions/delete
 *   POST  /api/admin/tenants/:id/actions/force-delete (requires X-Admin-Confirm header)
 *   PATCH /api/admin/tenants/:id/plan
 *
 * Every typed-body below is the exact wire shape the C# DTOs serialise —
 * names are lower-cased by the default System.Text.Json policy so the
 * TypeScript fields stay camelCase.
 */

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '/api';

async function fetchJSON<T>(url: string, options?: RequestInit): Promise<T> {
  const response = await fetch(`${API_BASE}${url}`, {
    headers: { 'Content-Type': 'application/json', ...options?.headers },
    credentials: 'include',
    ...options,
  });
  if (!response.ok) {
    const err = await response.json().catch(() => ({ error: response.statusText }));
    const message =
      (err as { message?: string; error?: string }).message
      ?? (err as { error?: string }).error
      ?? `HTTP ${response.status}`;
    throw new AdminTenantApiError(response.status, message, err);
  }
  if (response.status === 204) return undefined as unknown as T;
  return response.json() as Promise<T>;
}

export class AdminTenantApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly body: unknown,
  ) {
    super(message);
    this.name = 'AdminTenantApiError';
  }
}

// ── Wire types ──

export type TenantStatus =
  | 'pending_verification'
  | 'provisioning'
  | 'active'
  | 'failed'
  | 'deleting'
  | 'deleted'
  | null;

export interface AdminTenantListItem {
  id: string;
  name: string;
  slug: string;
  type: string;
  /** Shadow-column status; null for pre-Epic-28 legacy tenants (UI renders as "active"). */
  status: string | null;
  legacyPlan: string;
  planName: string | null;
  planSlug: string | null;
  planId: string | null;
  ownerId: string | null;
  ownerEmail: string | null;
  createdAt: string;
  updatedAt: string;
  failureReason: string | null;
  deleteRequestedAt: string | null;
  kekVersion: number | null;
  hasEncryptedConnectionString: boolean;
}

export interface AdminTenantListResponse {
  tenants: AdminTenantListItem[];
  total: number;
  page: number;
  pageSize: number;
}

export interface AdminTenantEventItem {
  id: string;
  type: string;
  createdAt: string;
  tags: string;
  data: string;
}

export interface AdminTenantActionGate {
  canRetry: boolean;
  canDelete: boolean;
  canForceDelete: boolean;
  canChangePlan: boolean;
}

export interface AdminTenantDetailResponse {
  tenant: AdminTenantListItem;
  recentEvents: AdminTenantEventItem[];
  actions: AdminTenantActionGate;
}

export interface AdminTenantActionResponse {
  tenantId: string;
  status: string;
  message: string;
}

// ── Filters ──

export interface ListTenantsFilters {
  status?: string;
  plan?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}

function buildQuery(filters?: ListTenantsFilters): string {
  if (!filters) return '';
  const params = new URLSearchParams();
  if (filters.status) params.set('status', filters.status);
  if (filters.plan) params.set('plan', filters.plan);
  if (filters.search) params.set('search', filters.search);
  if (filters.page !== undefined) params.set('page', String(filters.page));
  if (filters.pageSize !== undefined) params.set('pageSize', String(filters.pageSize));
  const qs = params.toString();
  return qs ? `?${qs}` : '';
}

// ── Public API ──

export const adminTenantsApi = {
  list: (filters?: ListTenantsFilters) =>
    fetchJSON<AdminTenantListResponse>(`/admin/tenants${buildQuery(filters)}`),

  getDetail: (tenantId: string) =>
    fetchJSON<AdminTenantDetailResponse>(`/admin/tenants/${tenantId}/detail`),

  retry: (tenantId: string) =>
    fetchJSON<AdminTenantActionResponse>(
      `/admin/tenants/${tenantId}/actions/retry`,
      { method: 'POST' },
    ),

  delete: (tenantId: string) =>
    fetchJSON<AdminTenantActionResponse>(
      `/admin/tenants/${tenantId}/actions/delete`,
      { method: 'POST' },
    ),

  forceDelete: (tenantId: string) =>
    fetchJSON<AdminTenantActionResponse>(
      `/admin/tenants/${tenantId}/actions/force-delete`,
      {
        method: 'POST',
        // Server-enforced friction: header echoes tenant id so a stray
        // double-click / CSRF can't nuke a prod tenant. Dashboard wraps this
        // call in a typed-slug confirmation modal.
        headers: { 'X-Admin-Confirm': tenantId },
      },
    ),

  updatePlan: (tenantId: string, planId: string) =>
    fetchJSON<AdminTenantActionResponse>(
      `/admin/tenants/${tenantId}/plan`,
      {
        method: 'PATCH',
        body: JSON.stringify({ planId }),
      },
    ),
};
