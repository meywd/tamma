/**
 * Runs API client (Story 21-4).
 *
 * Typed HTTP client for the tenant-facing workflow-runs read endpoints:
 *   • GET /api/v1/runs            — paginated run list (WorkflowInstance rows)
 *   • GET /api/v1/runs/summary    — Story 23-5 Workflow Monitor: windowed
 *                                   per-status/per-definition instance counts.
 *   • GET /api/v1/runs/{runId}    — one run's DCB event/log timeline + the
 *                                   tenant's OWN recorded cost.
 *
 * Cookie-session authenticated; the server resolves the tenant from the
 * caller's principal (no tenant id sent from the browser). A null tenant, or a
 * run owned by another tenant, fails closed with 404.
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

/** One workflow run as shown in the /runs list. */
export interface WorkflowRunSummary {
  id: string;
  definitionId: string;
  /** pending | running | completed | failed | cancelled (+ any engine status) */
  status: string;
  currentActivity: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  durationMs: number | null;
}

export interface RunsListResponse {
  tenantId: string;
  total: number;
  page: number;
  pageSize: number;
  runs: WorkflowRunSummary[];
}

/** A single DCB event on a run's timeline. */
export interface RunEvent {
  id: string;
  type: string;
  tags: Record<string, unknown> | null;
  data: Record<string, unknown> | null;
  createdAt: string;
  sequenceNumber: number;
}

/** Full run detail: instance metadata + timeline + the tenant's OWN cost. */
export interface WorkflowRunDetail {
  id: string;
  definitionId: string;
  status: string;
  currentActivity: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  durationMs: number | null;
  provider: string | null;
  issueNumber: number | null;
  repository: string | null;
  prUrl: string | null;
  filesChanged: string[];
  /** The tenant's OWN recorded spend for this run (never a platform margin). */
  totalCostUsd: number;
  eventCount: number;
  events: RunEvent[];
  logs: string[];
}

/** Instance count for a single workflow status (Story 23-5). */
export interface WorkflowStatusCount {
  status: string;
  count: number;
}

/** Instance count for a single workflow definition (Story 23-5). */
export interface WorkflowDefinitionCount {
  definitionId: string;
  definitionName: string;
  count: number;
}

/**
 * Windowed aggregate of the tenant's workflow instances (Story 23-5). Pure
 * counts — the endpoint never returns any cost / economics figure.
 */
export interface WorkflowRunsSummary {
  tenantId: string;
  from: string | null;
  to: string | null;
  total: number;
  byStatus: WorkflowStatusCount[];
  byDefinition: WorkflowDefinitionCount[];
}

export const runsApi = {
  /** List the current tenant's workflow runs (newest first). */
  list: (params?: { limit?: number; page?: number }): Promise<RunsListResponse> => {
    const search = new URLSearchParams();
    if (params?.limit !== undefined) search.set('limit', String(params.limit));
    if (params?.page !== undefined) search.set('page', String(params.page));
    const qs = search.toString();
    return fetchJSON<RunsListResponse>(`/api/v1/runs${qs ? `?${qs}` : ''}`);
  },

  /**
   * Windowed per-status / per-definition instance counts for the current
   * tenant (Story 23-5 Workflow Monitor). `from`/`to` are ISO-8601 strings.
   */
  summary: (params?: { from?: string; to?: string }): Promise<WorkflowRunsSummary> => {
    const search = new URLSearchParams();
    if (params?.from !== undefined) search.set('from', params.from);
    if (params?.to !== undefined) search.set('to', params.to);
    const qs = search.toString();
    return fetchJSON<WorkflowRunsSummary>(`/api/v1/runs/summary${qs ? `?${qs}` : ''}`);
  },

  /** Get one run's detail (event timeline, logs, files changed, cost). */
  getDetail: (runId: string): Promise<WorkflowRunDetail> =>
    fetchJSON<WorkflowRunDetail>(`/api/v1/runs/${encodeURIComponent(runId)}`),
};
