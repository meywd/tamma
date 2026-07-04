/**
 * Story 34-9 — typed HTTP client for the platform-owner PRICING dashboard.
 * Mirrors the `admin-tenants-client.ts` `fetchJSON<T>` + typed-error pattern.
 *
 * Every route below is gated server-side by `PlatformOwnerAccess` (the price
 * book + margin policies are platform-GLOBAL in both single-user and SaaS
 * modes). The admin dashboard UI is already inside the `AdminGuard` chain, but
 * the server is authoritative — the UI gate is UX-only.
 *
 * Routes consumed (all under `/api`, mirroring AdminPricing*Endpoints.cs):
 *   GET    /api/admin/pricing/overview                              (34-9 dashboard rollup)
 *   GET    /api/admin/pricing/plans?status=&isCustom=&tenantId=     (34-2 admin catalog)
 *   POST   /api/admin/pricing/plans                                 (34-2 create v1 → 201)
 *   PUT    /api/admin/pricing/plans/{slug}                          (34-2 version → 200)
 *   POST   /api/admin/pricing/plans/custom                          (34-2 mint bespoke → 201)
 *   DELETE /api/admin/pricing/plans/{slug}/versions/{version}?force= (34-2 deprecate → 204/409)
 *   GET    /api/admin/pricing/margins                               (34-5 list policies)
 *   PUT    /api/admin/pricing/margins                               (34-5 version policy)
 *
 * Plan assignment (34-4) is NOT duplicated here — the CustomPlanPanel reuses
 * `adminTenantsApi.updatePlan(tenantId, planId)` from admin-tenants-client.ts.
 *
 * Wire field names are camelCase because the API's default System.Text.Json
 * policy lower-cases the C# PascalCase records (see admin-tenants-client.ts).
 * NOTE: there is NO global JsonStringEnumConverter, so PlanSnapshot entitlement
 * `metricKey` arrives as a NUMERIC ordinal (see METRIC_KEYS / metricKeyLabel).
 */

const API_BASE = (import.meta.env as { VITE_API_BASE_URL?: string }).VITE_API_BASE_URL ?? '/api';

export class AdminPricingApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
    public readonly body: unknown,
  ) {
    super(message);
    this.name = 'AdminPricingApiError';
  }
}

async function fetchJSON<T>(url: string, options?: RequestInit): Promise<T> {
  const response = await fetch(`${API_BASE}${url}`, {
    headers: { 'Content-Type': 'application/json', ...options?.headers },
    credentials: 'include',
    ...options,
  });
  if (!response.ok) {
    const err = (await response.json().catch(() => ({ error: response.statusText }))) as Record<
      string,
      unknown
    >;
    const message =
      (err.message as string | undefined) ??
      (err.error as string | undefined) ??
      `HTTP ${response.status}`;
    throw new AdminPricingApiError(response.status, message, err);
  }
  if (response.status === 204) return undefined as unknown as T;
  return (await response.json()) as T;
}

// ── Entitlement metric keys ──────────────────────────────────────────
// Canonical snake_case list in EntitlementMetricKey enum-declaration order.
// The write DTO (PlanEntitlementDto.MetricKey) is the snake_case string; a
// PlanSnapshot READ returns the numeric ordinal (no string-enum converter),
// so `metricKeyLabel` maps ordinal → snake_case for display.
export const METRIC_KEYS = [
  'agents',
  'workflow_runs',
  'llm_tokens',
  'seats',
  'repos',
  'rag_storage_mb',
  'benchmark_retention_days',
] as const;

export type EntitlementMetricKey = (typeof METRIC_KEYS)[number];

/** Map a PlanSnapshot entitlement `metricKey` (numeric ordinal OR already a string) to its snake_case label. */
export function metricKeyLabel(metricKey: number | string): string {
  if (typeof metricKey === 'string') return metricKey;
  return METRIC_KEYS[metricKey] ?? `metric_${metricKey}`;
}

export const ENTITLEMENT_PERIODS = ['monthly', 'total'] as const;
export type EntitlementPeriod = (typeof ENTITLEMENT_PERIODS)[number];

export const OVERAGE_MODES = ['block', 'allow', 'meter'] as const;
export type OverageMode = (typeof OVERAGE_MODES)[number];

export const PRICING_MODES = ['platform_provided', 'byok'] as const;
export type PricingMode = (typeof PRICING_MODES)[number];

export const MARGIN_SCOPES = ['global', 'plan', 'provider'] as const;
export type MarginScope = (typeof MARGIN_SCOPES)[number];

// ── Plan snapshot wire types (PlanSnapshot.cs) ───────────────────────

export interface PlanFeatureView {
  featureKey: string;
  boolValue: boolean | null;
  stringValue: string | null;
}

export interface PlanEntitlementView {
  /** Numeric ordinal on the wire (no string-enum converter). Use metricKeyLabel(). */
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

export interface PlanSnapshot {
  planId: string;
  slug: string;
  displayName: string;
  version: number;
  status: string;
  isCustom: boolean;
  billingInterval: string;
  supersedesPlanId: string | null;
  features: PlanFeatureView[];
  entitlements: PlanEntitlementView[];
  prices: PlanPriceView[];
}

// ── Overview wire types (AdminPricingDashboardEndpoints.cs) ───────────

export interface PlanOverviewRow {
  planId: string;
  slug: string;
  displayName: string;
  version: number;
  status: string;
  isCustom: boolean;
  billingInterval: string;
  recurringUsd: number | null;
  activeTenantCount: number;
}

export interface MarginSummary {
  activePolicyCount: number;
  globalPolicyCount: number;
  planScopedPolicyCount: number;
  providerScopedPolicyCount: number;
  globalMarkupMultiplier: number | null;
  globalFixedUsdPer1M: number | null;
}

export interface PricingOverviewTotals {
  activePlanCount: number;
  customPlanCount: number;
  deprecatedPlanCount: number;
  totalActiveAssignments: number;
  plansWithActiveAssignments: number;
}

export interface PricingOverviewResponse {
  plans: PlanOverviewRow[];
  margins: MarginSummary;
  totals: PricingOverviewTotals;
}

// ── Margin policy wire types (AdminPricingEndpoints.cs) ───────────────

export interface MarginPolicyDto {
  id: string;
  scope: string;
  refKey: string | null;
  markupMultiplier: number | null;
  fixedUsdPer1M: number | null;
  effectiveFrom: string;
  status: string;
  createdAt: string;
  updatedAt: string;
}

export interface VersionMarginResponse {
  policy: MarginPolicyDto;
  supersededPolicyId: string | null;
}

// ── Request bodies ───────────────────────────────────────────────────

export interface PlanFeatureBody {
  featureKey: string;
  boolValue?: boolean | null;
  stringValue?: string | null;
}

export interface PlanEntitlementBody {
  /** snake_case string validated against EntitlementMetricKey server-side. */
  metricKey: string;
  limitValue: number | null;
  period: string;
  overageMode: string;
}

export interface PlanPriceBody {
  pricingMode: string;
  recurringUsd: number;
  seatUsd: number;
  meteredComponentJson?: string | null;
}

export interface CreatePlanBody {
  slug: string;
  displayName: string;
  billingInterval: string;
  features?: PlanFeatureBody[] | null;
  entitlements?: PlanEntitlementBody[] | null;
  prices?: PlanPriceBody[] | null;
}

/** Null child collections mean "copy from the prior version" (PlanDraftSpec semantics). */
export interface VersionPlanBody {
  displayName?: string | null;
  billingInterval?: string | null;
  features?: PlanFeatureBody[] | null;
  entitlements?: PlanEntitlementBody[] | null;
  prices?: PlanPriceBody[] | null;
}

export interface MintCustomPlanBody {
  tenantId: string;
  displayName: string;
  billingInterval: string;
  features?: PlanFeatureBody[] | null;
  entitlements?: PlanEntitlementBody[] | null;
  prices?: PlanPriceBody[] | null;
  /** Never sent true by the UI; server rejects a public custom plan with 400 (AC5). */
  makePublic?: boolean;
}

export interface VersionMarginBody {
  scope: string;
  refKey?: string | null;
  markupMultiplier?: number | null;
  fixedUsdPer1M?: number | null;
  effectiveFrom?: string | null;
}

export interface ListPlansOpts {
  status?: string;
  isCustom?: boolean;
  tenantId?: string;
}

/** Result of a deprecate call: success is 204; a 409 throws AdminPricingApiError. */
export interface DeprecateResult {
  deprecated: boolean;
}

function planQuery(opts?: ListPlansOpts): string {
  if (!opts) return '';
  const params = new URLSearchParams();
  if (opts.status) params.set('status', opts.status);
  if (opts.isCustom !== undefined) params.set('isCustom', String(opts.isCustom));
  if (opts.tenantId) params.set('tenantId', opts.tenantId);
  const qs = params.toString();
  return qs ? `?${qs}` : '';
}

// ── Public API ───────────────────────────────────────────────────────

export const adminPricingApi = {
  // 34-9 dashboard rollup
  getOverview: (): Promise<PricingOverviewResponse> => fetchJSON('/admin/pricing/overview'),

  // 34-2 plan catalog
  listPlans: (opts?: ListPlansOpts): Promise<{ plans: PlanSnapshot[] }> =>
    fetchJSON(`/admin/pricing/plans${planQuery(opts)}`),

  createPlan: (body: CreatePlanBody): Promise<PlanSnapshot> =>
    fetchJSON('/admin/pricing/plans', { method: 'POST', body: JSON.stringify(body) }),

  versionPlan: (slug: string, body: VersionPlanBody): Promise<PlanSnapshot> =>
    fetchJSON(`/admin/pricing/plans/${encodeURIComponent(slug)}`, {
      method: 'PUT',
      body: JSON.stringify(body),
    }),

  mintCustomPlan: (body: MintCustomPlanBody): Promise<PlanSnapshot> =>
    fetchJSON('/admin/pricing/plans/custom', { method: 'POST', body: JSON.stringify(body) }),

  deprecateVersion: async (
    slug: string,
    version: number,
    force = false,
  ): Promise<DeprecateResult> => {
    await fetchJSON<void>(
      `/admin/pricing/plans/${encodeURIComponent(slug)}/versions/${version}?force=${force}`,
      { method: 'DELETE' },
    );
    // 204 → success; a 409 (active assignments, no force) throws
    // AdminPricingApiError whose body carries { affectedTenantCount }.
    return { deprecated: true };
  },

  // 34-5 margins
  listMargins: (): Promise<{ policies: MarginPolicyDto[] }> => fetchJSON('/admin/pricing/margins'),

  versionMargin: (body: VersionMarginBody): Promise<VersionMarginResponse> =>
    fetchJSON('/admin/pricing/margins', { method: 'PUT', body: JSON.stringify(body) }),
};
