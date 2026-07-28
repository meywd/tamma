/**
 * Story 46-3 — tenant provider/model settings API client. Built on the shared
 * ApiClient (never a bare `fetch`, 45-1) so the refresh-on-401 dance is
 * inherited.
 *
 * Server contract (read, not guessed — apps/tamma-elsa/src/Tamma.Api/
 * Endpoints/ProviderCredentialEndpoints.cs + ProviderAdminEndpoints.cs,
 * registered in Program.cs under the /api/v1/agents group):
 *
 *   GET    /api/v1/agents/providers/models
 *     200 { providers: TenantProviderRosterRow[] } — one row per ENABLED
 *         provider (platform-disabled providers are simply absent).
 *   GET    /api/v1/agents/providers/{provider}/models
 *     200 ProviderModelsResponse — fail-soft ALWAYS-200 envelope (epic D6):
 *         fresh list, stale cached list (stale:true + errorCode), or empty
 *         list + errorCode; the currently-effective model is always present
 *         as an entry flagged current (synthesized when delisted). Fetched
 *         server-side with the tenant's BYOK key when present (epic D5) —
 *         the browser never sees a key.
 *     404 { error: "unknown_provider" } — non-catalogue provider only.
 *   GET    /api/v1/agents/providers/{provider}/model
 *     200 TenantProviderModelResponse — resolved model + provenance +
 *         raw override (null when none) + fallbackModel (what a removed
 *         override would resolve to, computed server-side).
 *   PUT    /api/v1/agents/providers/{provider}/model   { model }
 *     200 PutTenantProviderModelResponse (carries pricingKnown + warning —
 *         epic D3b allow-with-warning, never blocks)
 *     400 { error: "invalid_model" | "no_user_context" }
 *     403 member-role caller (AgentManage route policy — the server is the
 *         RBAC enforcement; the client's canEdit is cosmetic, D2)
 *     409 { error: "provider_disabled" } — platform off switch wins
 *   DELETE /api/v1/agents/providers/{provider}/model
 *     204 (empty body → ApiClient resolves null)
 *     403 member-role caller
 *     404 { error: "override_not_found" }
 *
 * The tenant is ALWAYS resolved server-side from the session — this client
 * never sends a tenant id.
 */

import { apiClient } from './client';

/**
 * Provenance of the resolved model (46-1 resolver:
 * tenant override → platform DB → config → descriptor). The tenant UI maps
 * everything except 'tenant-override' to "platform default" (46-3 D3) —
 * config/descriptor are platform deployment internals.
 */
export type ModelSource = 'tenant-override' | 'platform-db' | 'config' | 'descriptor';

/**
 * Mirrors TenantProviderRosterRow — ProviderCredentialEndpoints.cs.
 * NOTE: the C# field is `Provider` (serialized `provider`), not `key`.
 * `byokKeyPresent` is presence metadata only — NEVER the key.
 */
export interface TenantProviderRosterRow {
  provider: string;
  displayName: string;
  modelsSupported: boolean;
  model: string | null;
  source: string;
  hasOverride: boolean;
  byokKeyPresent: boolean;
  /**
   * What the effective model WOULD be if the tenant override were removed —
   * the server-side skip-principal resolution (platform DB → config →
   * descriptor; never restated client-side). Equals `model` when no override
   * is active; `null` when nothing below the override names a model.
   * (.dev/bugs/2026-07-27-tenant-surface-cannot-name-platform-default-under-override.md)
   */
  fallbackModel: string | null;
}

/** Mirrors the anonymous `{ providers = roster }` object GetTenantProviderRoster returns. */
export interface TenantProviderRosterResponse {
  providers: TenantProviderRosterRow[];
}

/** Mirrors ProviderModelEntry — ProviderAdminEndpoints.cs. */
export interface ProviderModelEntry {
  id: string;
  displayName: string | null;
  deprecated: boolean;
  current: boolean;
  /**
   * `true` ONLY on the entry BuildModelsResponse synthesized because the
   * provider's live list no longer carries the current model (the C# record
   * omits `false` from the wire — absent/false both mean "genuinely listed").
   * Replaces the exported isCurrentDelisted heuristic
   * (.dev/bugs/2026-07-27-models-envelope-lacks-delisted-flag.md).
   */
  delisted?: boolean;
}

/**
 * Mirrors ProviderModelsResponse — ProviderAdminEndpoints.cs:431-436 (the
 * fail-soft envelope both apps consume, epic D6). `fetchedAt` is an ISO
 * timestamp or null (never fetched).
 */
export interface ProviderModelsResponse {
  provider: string;
  models: ProviderModelEntry[];
  fetchedAt: string | null;
  stale: boolean;
  errorCode: string | null;
}

/** Mirrors TenantProviderModelResponse — ProviderCredentialEndpoints.cs. */
export interface TenantProviderModelResponse {
  provider: string;
  model: string | null;
  source: string;
  override: string | null;
  /** Same skip-principal fallback as `TenantProviderRosterRow.fallbackModel`. */
  fallbackModel: string | null;
}

/** Mirrors PutTenantProviderModelResponse — ProviderCredentialEndpoints.cs:667-668. */
export interface PutTenantProviderModelResponse {
  provider: string;
  model: string;
  source: string;
  pricingKnown: boolean;
  warning: string | null;
}

export const providerModelsApi = {
  /** Tenant roster: one row per platform-enabled provider. */
  listProviderModelSettings: (): Promise<TenantProviderRosterResponse> =>
    apiClient.get<TenantProviderRosterResponse>('/api/v1/agents/providers/models'),

  /** Live model list (server-side fetch, BYOK key preferred — epic D5). */
  listProviderModels: (provider: string): Promise<ProviderModelsResponse> =>
    apiClient.get<ProviderModelsResponse>(
      `/api/v1/agents/providers/${encodeURIComponent(provider)}/models`,
    ),

  /** Resolved model + provenance + raw override for one provider. */
  getProviderModel: (provider: string): Promise<TenantProviderModelResponse> =>
    apiClient.get<TenantProviderModelResponse>(
      `/api/v1/agents/providers/${encodeURIComponent(provider)}/model`,
    ),

  /** Upsert the tenant model override. Mirrors PutTenantProviderModelRequest { model }. */
  putProviderModel: (provider: string, model: string): Promise<PutTenantProviderModelResponse> =>
    apiClient.put<PutTenantProviderModelResponse>(
      `/api/v1/agents/providers/${encodeURIComponent(provider)}/model`,
      { model },
    ),

  /** Remove the tenant override → resolution falls back to the platform default (204). */
  deleteProviderModel: async (provider: string): Promise<void> => {
    await apiClient.delete<null>(`/api/v1/agents/providers/${encodeURIComponent(provider)}/model`);
  },
};
