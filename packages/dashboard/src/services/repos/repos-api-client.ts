/**
 * Repos API client (Story 21-4).
 *
 * Typed HTTP client for the tenant-facing connected-repositories read
 * endpoint. Cookie-session authenticated (`credentials: 'include'`); the
 * server resolves the tenant from the caller's principal, so no tenant id is
 * ever sent from the browser (no IDOR surface). A null tenant fails closed
 * with 404 `no_active_tenant`.
 */

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '';

async function fetchJSON<T>(url: string, options?: RequestInit): Promise<T> {
  const response = await fetch(`${API_BASE}${url}`, {
    credentials: 'include',
    headers: { 'Content-Type': 'application/json', ...options?.headers },
    ...options,
  });

  if (!response.ok) {
    const body = (await response.json().catch(() => ({ error: response.statusText }))) as {
      error?: string;
    };
    throw new Error(body.error ?? `HTTP ${response.status}`);
  }

  return response.json() as Promise<T>;
}

/** A connected git-platform installation for the current tenant. */
export interface ConnectedRepo {
  id: string;
  /** Human label — "owner/repo" when metadata carries it, else kind:externalId. */
  name: string;
  /** github | gitea | forgejo | gitlab | bitbucket | azure_devops */
  platform: string;
  baseUrl: string;
  externalId: string | null;
  /** connected | suspended | disconnected */
  status: string;
  isPrimary: boolean;
  connectedAt: string;
  updatedAt: string;
}

export interface ReposListResponse {
  tenantId: string;
  repos: ConnectedRepo[];
  count: number;
}

export const reposApi = {
  /** List the current tenant's connected repositories / installations. */
  list: (): Promise<ReposListResponse> => fetchJSON<ReposListResponse>('/api/v1/repos'),
};
