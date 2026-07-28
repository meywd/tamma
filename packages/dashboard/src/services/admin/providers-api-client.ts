/**
 * Provider Settings Admin API Client (Story 46-2)
 *
 * Typed wrapper for the platform-owner provider admin surface exposed by
 * `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderAdminEndpoints.cs`
 * (Epic 46 — stories 46-0/46-1).
 *
 * Routes (NOTE: the roster is `/admin/providers/status` — the bare
 * `/admin/providers` is Story 34-11's provider COST price-book roster):
 *   GET    /api/admin/providers/status         — provider status roster
 *   GET    /api/admin/providers/:key/models    — live model list (fail-soft,
 *                                                always 200 for a known key)
 *   PUT    /api/admin/providers/:key/settings  — set platform default model
 *                                                and/or enabled flag
 *   DELETE /api/admin/providers/:key/settings  — remove the platform row →
 *                                                fall back to config/descriptor
 *
 * Error codes the API returns (surface inline):
 *   invalid_provider     (400) — blank provider key
 *   unknown_provider     (404) — key not in the catalogue/allowlist
 *   invalid_request      (400) — PUT body missing both fields
 *   invalid_model        (400) — empty / too long / control characters
 *   settings_not_found   (404) — DELETE with no platform row to remove
 */

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? '/api';

export interface ApiError extends Error {
  status?: number;
  code?: string | undefined;
}

async function fetchJSON<T>(url: string, options?: RequestInit): Promise<T> {
  const response = await fetch(`${API_BASE}${url}`, {
    headers: { 'Content-Type': 'application/json', ...options?.headers },
    credentials: 'include',
    ...options,
  });

  if (!response.ok) {
    const body = await response
      .json()
      .catch(() => ({ error: response.statusText })) as Record<string, string>;
    const err = new Error(
      body['detail'] ?? body['error'] ?? `HTTP ${response.status}`,
    ) as ApiError;
    err.status = response.status;
    err.code = body['error'];
    throw err;
  }

  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

// === Types ===================================================================
// Copied from the C# response DTOs in ProviderAdminEndpoints.cs (records
// `ProviderStatusRow`, `ProviderModelEntry`, `ProviderModelsResponse`,
// `PutProviderSettingsRequest`, `PutProviderSettingsResponse`) — serialized
// camelCase by the API. Do NOT invent fields here (Epic 45 lesson, 45-1).

/** Three-way key classification — never key material (46-0 AC7). */
export type ProviderKeyStatus = 'configured' | 'missing' | 'not_required';

/**
 * Provenance of the current default model, from the 46-1 resolver
 * (`InlineToolLoopRunner.ResolveDefaultModel`). The admin roster resolves at
 * platform scope, so `tenant-override` should not appear there; it is in the
 * union because the resolver can emit it.
 */
export type ProviderModelSource =
  | 'tenant-override'
  | 'platform-db'
  | 'config'
  | 'descriptor';

export type ProviderTransport = 'http' | 'cli' | 'mcp';

/** One provider row of `GET /api/admin/providers/status` (C# `ProviderStatusRow`). */
export interface ProviderStatusRow {
  key: string;
  displayName: string;
  transport: ProviderTransport;
  /** Wire dialect for HTTP providers; `null` for cli/mcp transports. */
  dialect: string | null;
  effectiveBaseUrl: string | null;
  keyStatus: ProviderKeyStatus;
  /** Whether the provider has a listable models endpoint (descriptor `ModelsEndpointPath`). */
  modelsSupported: boolean;
  currentModel: string | null;
  source: ProviderModelSource | null;
  enabled: boolean;
  aliases: string[];
}

/** Envelope of `GET /api/admin/providers/status`. */
export interface ProviderStatusResponse {
  providers: ProviderStatusRow[];
}

/** One entry of a live model list (C# `ProviderModelEntry`). */
export interface ProviderModelEntry {
  id: string;
  displayName: string | null;
  deprecated: boolean;
  /** The currently-effective model — always present exactly once (epic D6). */
  current: boolean;
  /**
   * `true` ONLY on the entry the server synthesized because the provider's
   * live list no longer carries the current model
   * (`BuildModelsResponse` — the C# record omits `false` from the wire, so
   * absent and `false` both mean "genuinely listed"). Replaces the 46-2
   * index-0/null-displayName heuristic
   * (.dev/bugs/2026-07-27-models-envelope-lacks-delisted-flag.md).
   */
  delisted?: boolean;
}

/**
 * The fail-soft models envelope (C# `ProviderModelsResponse`) — always
 * HTTP 200 for a known provider: fresh list, stale-cached list flagged
 * `stale`, or empty list with `errorCode`.
 */
export interface ProviderModelsResponse {
  provider: string;
  models: ProviderModelEntry[];
  /** ISO 8601; `null` when the list could not be fetched at all. */
  fetchedAt: string | null;
  stale: boolean;
  errorCode: string | null;
}

/** PUT body (C# `PutProviderSettingsRequest`) — at least one field required. */
export interface PutProviderSettingsRequest {
  defaultModel?: string;
  enabled?: boolean;
}

/** PUT response (C# `PutProviderSettingsResponse`) — carries the D3b pricing warning. */
export interface PutProviderSettingsResponse {
  provider: string;
  defaultModel: string | null;
  enabled: boolean;
  pricingKnown: boolean;
  warning: string | null;
}

// === Admin API ===============================================================

export const providersAdminApi = {
  /** `GET /api/admin/providers/status` — the provider status roster. */
  listProviders: () => fetchJSON<ProviderStatusResponse>('/admin/providers/status'),

  /** `GET /api/admin/providers/:key/models` — live model list, fail-soft. */
  listProviderModels: (key: string) =>
    fetchJSON<ProviderModelsResponse>(
      `/admin/providers/${encodeURIComponent(key)}/models`,
    ),

  /** `PUT /api/admin/providers/:key/settings` — set default model and/or enabled. */
  putProviderSettings: (key: string, body: PutProviderSettingsRequest) =>
    fetchJSON<PutProviderSettingsResponse>(
      `/admin/providers/${encodeURIComponent(key)}/settings`,
      { method: 'PUT', body: JSON.stringify(body) },
    ),

  /** `DELETE /api/admin/providers/:key/settings` — reset to config/descriptor (204). */
  deleteProviderSettings: (key: string) =>
    fetchJSON<void>(`/admin/providers/${encodeURIComponent(key)}/settings`, {
      method: 'DELETE',
    }),
};
