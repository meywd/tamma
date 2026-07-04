/**
 * Story 34-9 — tenant-facing pricing API client (built on the shared ApiClient
 * so the refresh-on-401 dance is inherited, exactly like `api/alerts.ts`).
 *
 * SECURITY (AC6/AC7 — mirrors the 34-5 estimate-leak rule): the tenant surface
 * NEVER exposes platform-internal economics. `GET /api/pricing/estimate` returns
 * ONLY the sell price the caller would be charged — there is no cost-basis or
 * margin field in `EstimateResponse` (the server strips them). Do not add one.
 *
 * The tenant is ALWAYS resolved server-side from the authenticated session; this
 * client never sends a caller-supplied tenant id, so a member can't spoof
 * another tenant. Routes consumed (all shipped, gated MemberAccess reads /
 * SettingsManage mutations):
 *   GET  /api/pricing/entitlements        (34-6 resolved entitlements + headroom)
 *   GET  /api/pricing/plans               (34-2 public catalog)
 *   GET  /api/pricing/plans/{slug}        (34-2 single public plan)
 *   GET  /api/pricing/estimate?…          (34-5 sell-price-only estimate)
 *   POST /api/pricing/subscribe           (34-4 self-service plan change)
 */

import { apiClient } from './client';

export type PricingMode = 'platform_provided' | 'byok';

// Canonical EntitlementMetricKey labels (enum-declaration order). The tenant
// entitlements endpoint returns snake_case strings; a PlanSnapshot READ returns
// the numeric ordinal (no string-enum converter) — normalize both via label().
export const METRIC_KEYS = [
  'agents',
  'workflow_runs',
  'llm_tokens',
  'seats',
  'repos',
  'rag_storage_mb',
  'benchmark_retention_days',
] as const;

export function metricKeyLabel(metricKey: number | string): string {
  if (typeof metricKey === 'string') return metricKey;
  return METRIC_KEYS[metricKey] ?? `metric_${metricKey}`;
}

// ── Resolved entitlements (ResolvedEntitlementsDto / ResolvedEntitlementDto) ──

export interface ResolvedEntitlementLine {
  metricKey: string;
  limitValue: number | null; // null ⇒ unlimited
  period: string;
  overageMode: string;
  currentUsage: number | null; // null ⇒ usage unavailable
  remaining: number | null; // null ⇒ unlimited
  isOver: boolean;
  overagePercent: number | null;
}

export interface ResolvedEntitlementsResponse {
  tenantId: string;
  planId: string;
  planVersion: number;
  isCustom: boolean;
  limits: ResolvedEntitlementLine[];
}

// ── Public plan snapshot (PlanSnapshot.cs — entitlement metricKey is numeric) ──

export interface PlanEntitlementView {
  metricKey: number | string;
  limitValue: number | null;
  period: string;
  overageMode: string;
}

export interface PlanPriceView {
  pricingMode: string;
  recurringUsd: number;
  seatUsd: number;
  meteredComponent: string;
}

export interface PlanSnapshotDto {
  planId: string;
  slug: string;
  displayName: string;
  version: number;
  status: string;
  isCustom: boolean;
  billingInterval: string;
  supersedesPlanId: string | null;
  features: { featureKey: string; boolValue: boolean | null; stringValue: string | null }[];
  entitlements: PlanEntitlementView[];
  prices: PlanPriceView[];
}

// ── Estimate (PricingEndpoints.GetEstimate — SELL PRICE ONLY) ──

export interface EstimateResponse {
  provider: string;
  model: string | null;
  inputTokens: number;
  outputTokens: number;
  pricingMode: string;
  sellPriceUsd: number;
  invoice: { sellPriceUsd: number };
  // NOTE: intentionally NO costBasisUsd / marginUsd — the tenant never sees them.
}

export interface EstimateParams {
  provider: string;
  model: string;
  inputTokens: number;
  outputTokens: number;
}

// ── Subscribe (AdminTenantsEndpoints plan-assignment response) ──

export interface SubscribeResponse {
  tenantId: string;
  planId?: string;
  planSlug?: string;
  planName?: string;
  version?: number;
  status?: string;
  message?: string;
  /** Optional flagged-violation list surfaced as a non-blocking warning (34-4). */
  violations?: string[];
}

export const tenantPricingApi = {
  getEntitlements: (): Promise<ResolvedEntitlementsResponse> =>
    apiClient.get<ResolvedEntitlementsResponse>('/api/pricing/entitlements'),

  listPublicPlans: (): Promise<{ plans: PlanSnapshotDto[] }> =>
    apiClient.get<{ plans: PlanSnapshotDto[] }>('/api/pricing/plans'),

  getPublicPlan: (slug: string): Promise<PlanSnapshotDto> =>
    apiClient.get<PlanSnapshotDto>(`/api/pricing/plans/${encodeURIComponent(slug)}`),

  estimate: (params: EstimateParams): Promise<EstimateResponse> => {
    const qs = new URLSearchParams({
      provider: params.provider,
      model: params.model,
      inputTokens: String(params.inputTokens),
      outputTokens: String(params.outputTokens),
    });
    return apiClient.get<EstimateResponse>(`/api/pricing/estimate?${qs.toString()}`);
  },

  subscribe: (body: { planSlug: string }): Promise<SubscribeResponse> =>
    apiClient.post<SubscribeResponse>('/api/pricing/subscribe', body),
};
