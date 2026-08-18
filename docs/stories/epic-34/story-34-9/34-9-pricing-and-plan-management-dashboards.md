# Story 34-9: Pricing & Plan Management Dashboards

Status: done
<!-- Flipped drafted -> done 2026-08-18. The deliverable named in the acceptance criteria
     was located in apps/tamma-elsa/src (and its suites in apps/tamma-elsa/tests) before this
     header was changed — not taken from a changelog. The per-story evidence is recorded
     inline on this story's line in docs/sprint-status.yaml.
   Both halves shipped — the admin pricing overview route on the server and the dashboard
   pages that read it.
-->

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase workflow (Read → Research → Break Down → TDD →
Quality Gates → Failure Handling), the `.dev/` knowledge base usage rules, TRACE/DEBUG logging
requirements, the test-first (TDD) workflow, the 100%-critical-path coverage requirement, and
the build/quality-gate enforcement. **Failure to follow this process will result in rework.**

## User Story

As a **platform owner** (admin dashboard) and as a **tenant owner/admin** (user dashboard),
I want to author and assign the price-book from the admin UI and see my plan, entitlement
headroom, BYOK/platform mode, and credit balance from the tenant UI with an upgrade/estimate
flow,
so that monetization is operable end-to-end through a UI — owners manage plans/margins/promos/credits
and tenants understand and change what they pay — without anyone touching the database or curling
the API.

This story is the **presentation layer** for Epic 34. It reuses the read APIs delivered by
34-2 (plan catalog), 34-5 (pricing/estimate engine), 34-6 (entitlement/headroom), and 34-7
(trials/credits/promo), and the BYOK mode-toggle endpoint from 34-3. It owns NO new pricing
business logic — it surfaces and orchestrates the existing endpoints. (Two thin tenant-read
endpoints — `GET /api/pricing/estimate` and `POST /api/pricing/subscribe` — are CANONICALLY owned
by 34-5 and 34-4 respectively; this story consumes them and does not re-implement them.)

## Priority

P1 — Required for self-service monetization; the engine (34-1..34-7) is unusable by non-engineers
until both dashboards surface it. Sits behind the P0 data/engine stories.

## Acceptance Criteria

1. **Admin Pricing section gated to platform owners.** `packages/dashboard` gains a `pricing`
   tab in `AdminLayout.tsx` (`AdminTab` union + `TABS` array) that is wrapped by the existing
   `AdminGuard` so non-platform-owner users never see it; every read/mutation it issues targets
   `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminPricingEndpoints.cs` routes, all gated by
   `PlatformOwnerAccess` server-side (the UI gate is UX-only; the server is authoritative).

2. **Admin plan-version editor round-trip.** The Pricing tab lists active + deprecated plan
   versions from `GET /api/admin/pricing/plans`, opens a version editor that edits features
   (`PlanFeature`), typed entitlements (`PlanEntitlement` keyed by `EntitlementMetricKey`), and
   prices (`PlanPrice`), and submits via `POST/PUT /api/admin/pricing/plans` (34-2). Because plan
   versions are immutable after activation (34-1), a save of an active version surfaces the
   server's "creates a new version + deprecates prior" behaviour and re-renders the supersede
   chain; a deprecate call that returns `409` with affected-tenant count is surfaced with the
   `?force=true` opt-in.

3. **Admin margin-policy editor.** The tab includes a margin-policy panel reading/writing
   `GET/PUT /api/admin/pricing/margins` (34-5) over `MarginPolicy` rows (scope plan|provider|global,
   `MarkupMultiplier`/`FixedUsdPer1M`, `EffectiveFrom`); saving surfaces the `PRICING.MARGIN.UPDATED`
   DCB event result (success/version) and validates that at least one of multiplier/fixed is set.

4. **Admin promo + credit management.** The tab can create/list promo codes
   (`POST /api/admin/pricing/promo` / list) and grant credits to a tenant
   (`POST /api/admin/tenants/{id}/credits`, 34-7); a successful grant shows the new `CreditLedger`
   balance returned by the API; promo creation validates `DiscountKind` (percent|fixed),
   `MaxRedemptions`, and `Expiry` client-side before POST (server remains authoritative).

5. **Admin custom-plan minting + assignment.** The tab can mint an `IsCustom` plan bound to a
   target tenant via `POST /api/admin/pricing/plans/custom` (34-2) and assign it via
   `PUT /api/admin/tenants/{id}/plan` (34-4); custom plans are visually flagged and excluded from
   the public-catalog list; an attempt to surface a custom plan publicly is rejected server-side
   (400) and the error is shown inline.

6. **Tenant Plan & Pricing page.** `packages/dashboard-user` gains a `/settings/billing` route
   (AuthGuard → AppLayout) rendering: the current plan + version (from
   `GET /api/pricing/plans/{slug}` + the tenant's active assignment), the resolved entitlement set
   with usage-vs-limit bars driven by `GET /api/pricing/entitlements` + the `CheckHeadroom` calc
   (34-6), the per-provider BYOK/platform mode (34-3), the credit balance (34-7), and a cost
   estimate widget. Unlimited entitlements (`LimitValue == null`) render as "Unlimited" with no bar.

7. **BYOK toggle never leaks the key.** For each provider, owner/admin can switch
   platform↔BYOK; enabling BYOK stores the provider key through the secret-cabinet-backed endpoint
   from 34-3 (the key is POSTed once, never read back) and the UI thereafter shows only the
   `Mode = byok` + a `SecretRef` presence indicator — the stored key value is **never** rendered,
   logged, or returned in any GET. `member`-role callers see the mode as read-only (no toggle
   control rendered; server returns 403 on the mutation regardless, mirroring `SettingsManage`).

8. **Tenant upgrade flow with entitlement delta preview.** The tenant picks a public plan from
   `GET /api/pricing/plans`; before committing, the UI computes and displays the entitlement
   **gains and losses** by diffing the target plan's resolved entitlements against the current
   resolved set + the `CheckHeadroom` result (so a downgrade that would put the tenant over a new
   limit is flagged), then commits via `POST /api/pricing/subscribe` (`SettingsManage`); the
   response's flagged-violation list (from 34-4) is surfaced as a non-blocking warning.

9. **Cost estimate widget.** A form (provider, model, input/output tokens) calls
   `GET /api/pricing/estimate` (34-5) and renders `{ costBasisUsd, marginUsd, sellPriceUsd,
   creditsApplied, pricingMode }`; BYOK mode shows a zero token markup; an unknown provider/model
   surfaces the server's `PricingUnknownModel` error inline (never silently 0).

10. **Trial + promo affordances.** A redeem-promo input calls `POST /api/pricing/promo/redeem`
    (`SettingsManage`, 34-7) and surfaces the `422` reason on rejection; an active `TenantTrial`
    renders a countdown banner ("Trial ends in N days") computed from `EndsAt`; member role sees
    the banner read-only (no redeem control).

11. **Per-mode degradation (single-user vs SaaS).** In single-user mode (no RBAC, sole user owns
    everything per `ITammaModeProvider`) the tenant Plan & Pricing page renders all controls for
    the sole user with no role gating; in SaaS mode the same page gates mutations to owner/admin
    and renders `member` as read-only. The admin Pricing section is platform-owner-only in both
    modes. No feature throws or hard-fails when a tenant has no active trial/credits/custom plan
    (empty states render).

12. **RBAC parity with the APIs (negative tests).** Component/E2E tests prove: a `member`-role
    tenant user cannot mutate (no controls + a stubbed 403 surfaces cleanly), a non-platform-owner
    cannot see the admin Pricing tab, and the BYOK toggle round-trip never places the plaintext key
    in any rendered DOM, network response body fixture, or console output.

13. **No new server pricing logic.** This story adds at most thin DTO/route wiring only where a
    dependency story did not already expose a needed read; it MUST NOT re-implement margin math,
    entitlement resolution, headroom, or credit netting — those live in 34-5/34-6/34-7 and are
    called, not duplicated (honors the 34-5 canonical-owner boundary note).

## Technical Design

### Component ownership map

| Layer | Package | New artifacts |
|---|---|---|
| Admin price-book UI | `packages/dashboard` | `PricingTab.tsx` + sub-panels, `admin-pricing-client.ts` |
| Tenant plan & pricing UI | `packages/dashboard-user` | `PlanPricingPage.tsx` + widgets, `pricing.ts` API client |
| Backend (consumed, NOT owned here) | `apps/tamma-elsa` | `PricingEndpoints.cs` (34-2/4/5/6/7), `Admin/AdminPricingEndpoints.cs` (34-2/5/7) |

The two C# endpoint files appear in this story's `primaryComponents` because they are the contract
surface this UI binds to; their **implementations are delivered by the dependency stories**. This
story only adds backend code if a consumed read is missing — see AC-13. Verified at authoring time:
neither `Tamma.Api/Endpoints/PricingEndpoints.cs` nor `Endpoints/Admin/AdminPricingEndpoints.cs`
nor `Services/Pricing/` exists yet (they land with 34-1..34-7), so this story is correctly
sequenced after them.

### Admin dashboard — file structure (`packages/dashboard/src`)

```
services/admin/
  admin-pricing-client.ts            # typed client for AdminPricingEndpoints.cs + AdminTenantsEndpoints plan/credit
  admin-pricing-client.test.ts
pages/admin/
  AdminLayout.tsx                    # MODIFY: add 'pricing' to AdminTab union + TABS + content switch
  pricing/
    PricingTab.tsx                   # tab shell: sub-tabs Plans | Margins | Promos & Credits | Custom Plans
    PlanVersionEditor.tsx            # features/entitlements/prices editor, supersede-chain view, 409 force flow
    MarginPolicyPanel.tsx            # MarginPolicy CRUD (scope/markup/fixed/effectiveFrom)
    PromoCreditPanel.tsx             # promo create/list + credit grant
    CustomPlanPanel.tsx              # mint IsCustom plan + assign to bound tenant
    __tests__/
      PricingTab.test.tsx
      PlanVersionEditor.test.tsx
      MarginPolicyPanel.test.tsx
      PromoCreditPanel.test.tsx
      CustomPlanPanel.test.tsx
```

`PricingTab` is rendered only inside the existing `AdminGuard` chain (admin dashboard is already
platform-admin scoped); the tab follows the `AdminLayout.tsx` pattern exactly (a `TabDef` row in
`TABS`, a content guard `{activeTab === 'pricing' && <PricingTab />}`). The client mirrors the
`fetchJSON<T>` + typed-error pattern of `admin-tenants-client.ts` (`AdminTenantApiError`).

Admin client surface (camelCase wire shapes match the default `System.Text.Json` policy, exactly
as `admin-tenants-client.ts` documents):

```typescript
export const adminPricingApi = {
  // Plan catalog (34-2)
  listPlans: (opts?: { includeDeprecated?: boolean; includeCustom?: boolean }) =>
    fetchJSON<PlanSnapshot[]>(`/admin/pricing/plans${query(opts)}`),
  getPlan: (slug: string, version?: number) =>
    fetchJSON<PlanSnapshot>(`/admin/pricing/plans/${slug}${version ? `?version=${version}` : ''}`),
  createPlanVersion: (body: UpsertPlanBody) =>
    fetchJSON<PlanSnapshot>(`/admin/pricing/plans`, { method: 'POST', body: JSON.stringify(body) }),
  updatePlanVersion: (slug: string, body: UpsertPlanBody) =>
    fetchJSON<PlanSnapshot>(`/admin/pricing/plans/${slug}`, { method: 'PUT', body: JSON.stringify(body) }),
  deprecatePlan: (slug: string, version: number, force = false) =>
    fetchJSON<DeprecateResult>(`/admin/pricing/plans/${slug}/${version}/deprecate?force=${force}`, { method: 'POST' }),
  mintCustomPlan: (body: MintCustomPlanBody) =>
    fetchJSON<PlanSnapshot>(`/admin/pricing/plans/custom`, { method: 'POST', body: JSON.stringify(body) }),
  // Margins (34-5)
  listMargins: () => fetchJSON<MarginPolicyDto[]>(`/admin/pricing/margins`),
  upsertMargin: (body: UpsertMarginBody) =>
    fetchJSON<MarginPolicyDto>(`/admin/pricing/margins`, { method: 'PUT', body: JSON.stringify(body) }),
  // Promos + credits (34-7)
  listPromos: () => fetchJSON<PromoCodeDto[]>(`/admin/pricing/promo`),
  createPromo: (body: CreatePromoBody) =>
    fetchJSON<PromoCodeDto>(`/admin/pricing/promo`, { method: 'POST', body: JSON.stringify(body) }),
  grantCredits: (tenantId: string, body: GrantCreditsBody) =>
    fetchJSON<CreditLedgerEntryDto>(`/admin/tenants/${tenantId}/credits`, { method: 'POST', body: JSON.stringify(body) }),
  // Assignment (34-4) — reuse adminTenantsApi.updatePlan, do NOT duplicate
};
```

`DeprecateResult` carries `{ deprecated: boolean; affectedTenants: number; requiresForce: boolean }`
so the 409 path (AC-2) renders the affected count and a "Deprecate anyway (force)" confirmation.

### Tenant dashboard — file structure (`packages/dashboard-user/src`)

```
api/
  pricing.ts                         # typed client over PricingEndpoints.cs (tenant routes)
  pricing.test.ts
pages/settings/
  PlanPricingPage.tsx                # current plan, entitlement bars, BYOK panel, credits, estimate, upgrade
  PlanPricingPage.test.tsx
components/pricing/
  EntitlementBar.tsx                 # one metric: limit, usage, headroom bar (or "Unlimited")
  ByokModePanel.tsx                  # per-provider platform/byok toggle + key-store form (write-only)
  CostEstimateWidget.tsx            # estimate form -> GET /api/pricing/estimate
  UpgradePlanModal.tsx               # plan picker + entitlement delta preview -> POST /api/pricing/subscribe
  TrialBanner.tsx                    # countdown from TenantTrial.EndsAt
  PromoRedeemForm.tsx                # POST /api/pricing/promo/redeem
  __tests__/ (colocated *.test.tsx for each)
App.tsx                              # MODIFY: add /settings/billing route under AuthGuard → AppLayout
layouts/AppLayout.tsx               # MODIFY: add "Billing" nav link
```

Tenant client (built on the shared `ApiClient` so the refresh-on-401 dance is inherited, exactly
like `api/alerts.ts`). `tenantId`/`role` come from `useAuth()` (`AuthUser.tenantId`, `AuthUser.role`):

```typescript
import { apiClient } from './client';

export type PricingMode = 'platform_provided' | 'byok';
export type EntitlementMetricKey =
  | 'agents' | 'workflow_runs' | 'llm_tokens' | 'seats' | 'repos'
  | 'rag_storage_mb' | 'benchmark_retention_days';

export interface ResolvedEntitlement {
  metric: EntitlementMetricKey;
  limit: number | null;            // null = unlimited
  period: 'monthly' | 'total';
  overageMode: 'block' | 'allow' | 'meter';
  currentUsage: number;            // from CheckHeadroom (34-6)
  remaining: number | null;        // null when unlimited
  over: boolean;
}

export interface CurrentPlanResponse {
  planSlug: string; planName: string; version: number;
  isCustom: boolean; status: string;
  entitlements: ResolvedEntitlement[];
  creditBalanceUsd: number;
  trial: { endsAt: string; status: 'active' | 'converted' | 'expired' } | null;
  providerModes: { provider: string; mode: PricingMode; hasSecretRef: boolean }[];
}

export interface EstimateResponse {
  costBasisUsd: number; marginUsd: number; sellPriceUsd: number;
  creditsApplied: number; pricingMode: PricingMode;
}

export const tenantPricingApi = {
  getEntitlements: () => apiClient.get<{ entitlements: ResolvedEntitlement[] }>(`/api/pricing/entitlements`),
  getCurrentPlan: () => apiClient.get<CurrentPlanResponse>(`/api/pricing/plan`),
  listPublicPlans: () => apiClient.get<{ plans: PlanSnapshotDto[] }>(`/api/pricing/plans`),
  estimate: (q: { provider: string; model: string; inputTokens: number; outputTokens: number }) =>
    apiClient.get<EstimateResponse>(`/api/pricing/estimate?${new URLSearchParams(/* ... */)}`),
  subscribe: (body: { planSlug: string; version?: number }) =>
    apiClient.post<SubscribeResponse>(`/api/pricing/subscribe`, body),
  redeemPromo: (body: { code: string; planSlug: string }) =>
    apiClient.post<RedeemResponse>(`/api/pricing/promo/redeem`, body),
  // BYOK mode (34-3)
  setProviderMode: (provider: string, body: { mode: PricingMode; plaintextKey?: string }) =>
    apiClient.put<{ provider: string; mode: PricingMode; hasSecretRef: boolean }>(
      `/api/pricing/providers/${provider}/mode`, body),
};
```

> `getCurrentPlan` / `setProviderMode` route shapes are owned by 34-3/34-4/34-6; if a dependency
> exposes them under a slightly different path the client is the single adjustment point. The
> `plaintextKey` is **write-only**: it is sent on the enable-BYOK PUT and never present in any
> response type (AC-7). `EntitlementBar` and `UpgradePlanModal` consume `CheckHeadroom`-derived
> `remaining`/`over` fields rather than recomputing — honoring AC-13.

### BYOK key-handling (security-critical, AC-7)

`ByokModePanel` posts the key exactly once, mirroring the secret-cabinet write pattern used by the
admin secrets client (`secrets-api-client.ts`, `CreateSecretBody.plaintext`): the field lives only
in component state, is cleared on submit, and is **never** stored in any response type or rendered
back. The post-enable view reads `hasSecretRef: boolean` from the mode response and shows a neutral
"Key stored (BYOK active)" badge. A client-side pre-flight (mirroring `hasPlaintextCredential` in
`api/alerts.ts`) rejects any attempt to embed a key in a non-key field. The server (34-3) is
authoritative — UI is UX-only.

### Per-mode + per-tenant handling

| Concern | single-user mode | SaaS mode |
|---|---|---|
| Tenant page principal | sole user (`user_id`) — no role gating; all controls enabled | `tenantId` from `useAuth()`; mutations gated owner/admin, `member` read-only |
| BYOK / subscribe / promo / credits read-back gate | sole user does everything | `SettingsManage` (owner/admin); `member` → 403 |
| Admin Pricing section | visible to the sole user (it is their instance) | `PlatformOwnerAccess` only; never shown to tenant members |
| Source of mode | `ITammaModeProvider` (server) reflected in `/api/auth/me` role/tenant shape | same |

The UI never decides the mode itself; it reads `useAuth()` (which is populated by `/api/auth/me`
sourced from the server's mode + membership) and the server enforces the boundary. This mirrors the
prompt-store "endpoint shape identical between modes; auth middleware decides" precedent.

### DCB events (emitted by the consumed endpoints, surfaced — not raised — by the UI)

The UI does not append DCB events; it triggers endpoints that do, and reflects their results:

- `PLAN.CATALOG.UPDATED` / `PLAN.CUSTOM.CREATED` / `PLAN.VERSION.CREATED` / `PLAN.DEPRECATED` (34-1/34-2)
- `PRICING.MARGIN.UPDATED` (34-5)
- `TENANT.PLAN.CHANGED` (34-4) — on subscribe/upgrade; the UI may show a recent-change confirmation
- `CREDIT.GRANTED` / `CREDIT.CONSUMED` / `PROMO.REDEEMED` / `TENANT.TRIAL.ENDED` (34-7)

These names are AGGREGATE.ACTION.STATUS per CLAUDE.md and exist in the dependency stories; the UI
only asserts (in tests) that the right endpoint was called with the right body.

### EF migration sketch

**None in this story.** All schema (`Plan`, `PlanFeature`, `PlanEntitlement`, `PlanPrice`,
`MarginPolicy`, `TenantPlanAssignment`, `TenantProviderBilling`, `TenantTrial`, `CreditLedger`,
`PromoCode`) is created by 34-1..34-7. If AC-13's "thin missing read" escape hatch is needed (e.g.
a consolidated `GET /api/pricing/plan` for the tenant's current plan does not already exist), it is
a read-only projection over existing entities via `ControlPlaneDbContext` — additive endpoint, no
migration. Verify `dotnet ef migrations has-pending-model-changes` reports **none** if any backend
file is touched.

## Dependencies

**Internal (prerequisite — all must be merged before this story starts):**

- **Story 34-2** — Plan Catalog Admin API & Custom Enterprise Plans (provides
  `AdminPricingEndpoints.cs` plan CRUD + custom-plan minting + `GET /api/pricing/plans`).
- **Story 34-5** — Cost→Price Markup Engine (provides `GET/PUT /api/admin/pricing/margins` and the
  tenant `GET /api/pricing/estimate`). 34-5 is the **canonical owner** of markup math — this story
  must not re-implement it.
- **Story 34-6** — Entitlement & Quota Resolution Service (provides `GET /api/pricing/entitlements`
  and the `CheckHeadroom` calc the entitlement bars + upgrade delta consume).
- **Story 34-7** — Trials, Credits & Promo Codes (provides promo redeem/admin-grant + trial state +
  credit-aware net price).

**Internal (transitive prerequisites of the above):**

- **Story 34-1** — Plan & Price-Book data model + `EntitlementMetricKey` enum.
- **Story 34-3** — BYOK vs platform mode endpoint + secret-cabinet wiring (the BYOK toggle target).
- **Story 34-4** — Per-tenant plan assignment (`PUT /api/admin/tenants/{id}/plan`,
  `POST /api/pricing/subscribe`).

**Blocks:**

- Epic 35 (Billing) UI surfaces, if any, build on the plan/entitlement views established here.

**External:**

- React 18 + Vite + React Router (already in both dashboard packages).
- Vitest 3.x + Testing Library (already configured: `packages/dashboard/src/test/`,
  `packages/dashboard-user/src/test/`).
- No new npm dependencies expected.

## Testing Strategy

**Admin dashboard (`packages/dashboard`, Vitest + Testing Library, colocated `__tests__/`):**

1. `admin-pricing-client.test.ts` — every method builds the right URL/method/body; non-2xx
   surfaces `AdminTenantApiError`-style typed error; 409 deprecate parses `affectedTenants`.
2. `PlanVersionEditor.test.tsx` — edit features/entitlements/prices → save calls
   `createPlanVersion`/`updatePlanVersion`; saving an active version shows the "new version created"
   result; deprecate with assignments shows the 409 affected-count + force opt-in (mock fetch).
3. `MarginPolicyPanel.test.tsx` — list renders, save validates "at least one of multiplier/fixed",
   calls `upsertMargin`.
4. `PromoCreditPanel.test.tsx` — promo create validates DiscountKind/MaxRedemptions/Expiry; credit
   grant shows new balance.
5. `CustomPlanPanel.test.tsx` — mint custom plan bound to tenant; assign via `adminTenantsApi.updatePlan`;
   custom plans excluded from public list view; public-surface attempt shows the 400 error.
6. `PricingTab.test.tsx` + `AdminLayout.test.tsx` (extend existing) — tab appears in `TABS`,
   renders inside `AdminGuard`, sub-tab switching works.

**Tenant dashboard (`packages/dashboard-user`, Vitest + Testing Library):**

7. `pricing.test.ts` — client URL/method/body matrix; estimate query-string assembly; refresh-on-401
   inherited from `ApiClient`.
8. `PlanPricingPage.test.tsx` — current plan renders; entitlement bars render usage/limit/headroom;
   unlimited renders "Unlimited" with no bar; empty trial/credits render empty states; member role
   renders read-only (no mutate controls) and a stubbed 403 surfaces cleanly.
9. `EntitlementBar.test.tsx` — bar width/over-limit styling from `remaining`/`over`; null limit path.
10. `ByokModePanel.test.tsx` — **key-leak test**: enable BYOK posts the key once; the plaintext key
    NEVER appears in the rendered DOM after submit, NEVER in any response fixture, NEVER in
    `console` output; post-enable shows `hasSecretRef` badge only; member sees read-only mode.
11. `CostEstimateWidget.test.tsx` — estimate renders cost/margin/sell/credits/mode; BYOK shows zero
    token markup; unknown-model surfaces `PricingUnknownModel` inline (not 0).
12. `UpgradePlanModal.test.tsx` — delta preview shows gains AND losses vs current resolved set;
    downgrade-over-limit flagged; commit calls `subscribe`; violation warning surfaced non-blocking.
13. `TrialBanner.test.tsx` / `PromoRedeemForm.test.tsx` — countdown from `EndsAt`; 422 promo reason
    surfaced; member read-only.

**Tenant-isolation / RBAC (mocked-server) tests:**

14. `member`-role user: all mutate controls absent; any forced mutation receives a stubbed 403 and
    renders the message (no white-screen).
15. Cross-tenant safety: the tenant client only ever derives `tenantId` from `useAuth()`, never from
    a URL param the user controls — asserted by reading the client's call args.

**Mocks:**

- Stripe / provider SDKs are NOT touched here (this is UI over already-built endpoints) — fetch is
  mocked via the existing `vi.fn()`/MSW-style helpers in each package's `test/` setup.
- `useAuth()` is stubbed per-test to drive single-user vs SaaS vs member-role matrices.

**Coverage:** follow project targets (80% line / 75% branch / 85% function); the BYOK key-leak path
and the member-403 path are critical and must be 100% covered.

## Estimated Effort

5-6 days

## Files Created/Modified

| File | Action |
|------|--------|
| `packages/dashboard/src/services/admin/admin-pricing-client.ts` | Create |
| `packages/dashboard/src/services/admin/admin-pricing-client.test.ts` | Create |
| `packages/dashboard/src/pages/admin/pricing/PricingTab.tsx` | Create |
| `packages/dashboard/src/pages/admin/pricing/PlanVersionEditor.tsx` | Create |
| `packages/dashboard/src/pages/admin/pricing/MarginPolicyPanel.tsx` | Create |
| `packages/dashboard/src/pages/admin/pricing/PromoCreditPanel.tsx` | Create |
| `packages/dashboard/src/pages/admin/pricing/CustomPlanPanel.tsx` | Create |
| `packages/dashboard/src/pages/admin/pricing/__tests__/PricingTab.test.tsx` | Create |
| `packages/dashboard/src/pages/admin/pricing/__tests__/PlanVersionEditor.test.tsx` | Create |
| `packages/dashboard/src/pages/admin/pricing/__tests__/MarginPolicyPanel.test.tsx` | Create |
| `packages/dashboard/src/pages/admin/pricing/__tests__/PromoCreditPanel.test.tsx` | Create |
| `packages/dashboard/src/pages/admin/pricing/__tests__/CustomPlanPanel.test.tsx` | Create |
| `packages/dashboard/src/pages/admin/AdminLayout.tsx` | Modify (add `pricing` tab) |
| `packages/dashboard/src/pages/admin/__tests__/AdminLayout.test.tsx` | Modify (assert tab) |
| `packages/dashboard-user/src/api/pricing.ts` | Create |
| `packages/dashboard-user/src/api/pricing.test.ts` | Create |
| `packages/dashboard-user/src/pages/settings/PlanPricingPage.tsx` | Create |
| `packages/dashboard-user/src/pages/settings/PlanPricingPage.test.tsx` | Create |
| `packages/dashboard-user/src/components/pricing/EntitlementBar.tsx` | Create |
| `packages/dashboard-user/src/components/pricing/ByokModePanel.tsx` | Create |
| `packages/dashboard-user/src/components/pricing/CostEstimateWidget.tsx` | Create |
| `packages/dashboard-user/src/components/pricing/UpgradePlanModal.tsx` | Create |
| `packages/dashboard-user/src/components/pricing/TrialBanner.tsx` | Create |
| `packages/dashboard-user/src/components/pricing/PromoRedeemForm.tsx` | Create |
| `packages/dashboard-user/src/components/pricing/__tests__/*.test.tsx` | Create (one per component) |
| `packages/dashboard-user/src/App.tsx` | Modify (add `/settings/billing` route) |
| `packages/dashboard-user/src/layouts/AppLayout.tsx` | Modify (add Billing nav link) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/PricingEndpoints.cs` | Modify only if a consumed tenant read is missing (AC-13; additive read, no migration) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminPricingEndpoints.cs` | Modify only if a consumed admin read is missing (AC-13; additive read, no migration) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md).
2. Searched `.dev/` for related spikes, bugs, findings, decisions.
3. Confirmed 34-2/34-3/34-4/34-5/34-6/34-7 are merged and their endpoints exist (grep
   `Tamma.Api/Endpoints/PricingEndpoints.cs` and `Endpoints/Admin/AdminPricingEndpoints.cs` — they
   did NOT exist at this story's authoring time, which is why this story is sequenced last in Epic 34).
4. Reviewed the existing dashboard patterns this story mirrors:
   `packages/dashboard/src/pages/admin/AdminLayout.tsx` (tab union + TABS),
   `packages/dashboard/src/services/admin/admin-tenants-client.ts` (typed `fetchJSON` client),
   `packages/dashboard-user/src/pages/alerts/TenantAlertFeed.tsx` (tenant page + member read-only),
   `packages/dashboard-user/src/api/alerts.ts` (ApiClient-based client + plaintext-credential
   pre-flight), `packages/dashboard/src/services/secrets/secrets-api-client.ts` (write-only
   secret-cabinet pattern for the BYOK key).
5. Planned the TDD cycle (Red-Green-Refactor) per component.

### Key Design Decisions

- **Presentation only, zero business duplication.** The single biggest risk is re-implementing
  margin/headroom/credit math in the UI. Decision: the UI calls the engine endpoints and renders
  their numbers verbatim (AC-13). The entitlement delta preview (AC-8) is the only "compute" the UI
  does, and it is a pure set-diff over server-resolved entitlement lists + the server's `CheckHeadroom`
  output — no pricing math.
- **BYOK key is write-only, never round-tripped.** Mirrors the secret cabinet's reveal-once design.
  The mode response carries a boolean presence flag, never the key. This is enforced by a dedicated
  leak test (AC-7, test #10).
- **One source of truth for `tenantId`/`role` — `useAuth()`.** The tenant client never accepts a
  caller-supplied tenant id; it derives identity from the authenticated session, so a member can't
  spoof another tenant. The server is still authoritative (RequireTenantMembership filter).
- **Admin reuses `adminTenantsApi.updatePlan` for assignment** rather than adding a duplicate plan
  PATCH — assignment is owned by 34-4's `AdminTenantsEndpoints.cs`/`updatePlan` client method.
- **Per-mode rendering is data-driven, not branchy.** A single `canMutate = isSingleUser || role in
  {owner, admin}` gate (computed from `useAuth()`) controls every mutate control; the admin Pricing
  tab is platform-owner-only via the existing `AdminGuard`.

### Integration Points

- Admin: `AdminPricingEndpoints.cs` (`PlatformOwnerAccess`), `AdminTenantsEndpoints.cs`
  (`PlatformOwnerAccess`) plan + credit routes.
- Tenant: `PricingEndpoints.cs` tenant routes (`MemberAccess` reads / `SettingsManage` mutations),
  the 34-3 BYOK mode route, the secret cabinet (Epic 29) via the 34-3 enable flow.
- Auth: `/api/auth/me` (drives `useAuth()` role/tenant/mode), the shared `ApiClient` refresh-on-401.

### Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| Dependency endpoints not yet merged / shape drift | High | Hard-gate start on 34-2/3/4/5/6/7 merge; the typed clients are the single adjustment point if a path differs; integration smoke test against a running API before UI sign-off |
| BYOK key leaks into DOM/logs/responses | Critical | Write-only field cleared on submit; dedicated leak test asserts absence in DOM + response fixtures + console; client-side plaintext pre-flight; server reveal-once is authoritative |
| UI re-implements pricing/headroom math (drift from engine) | High | AC-13 forbids it; render server numbers verbatim; delta preview is pure set-diff only; code review checklist item |
| Member-role user reaches a mutation | High | Controls hidden when `!canMutate` AND server returns 403; both paths tested; never trust the hidden control alone |
| Single-user mode mis-gated as SaaS (or vice-versa) | Medium | `canMutate` derived from `useAuth()` which reflects server mode; per-mode test matrix in `PlanPricingPage.test.tsx` |
| Deprecate-with-assignments 409 confusing UX | Medium | Surface affected-tenant count + explicit "Deprecate anyway (force)" confirm; no silent force |

### Success Metrics

- [ ] A platform owner can version a plan, set a margin, mint+assign a custom plan, and grant
      credits entirely from the admin UI with no DB/API access.
- [ ] A tenant owner can see plan + headroom, switch a provider to BYOK (key never visible), get a
      cost estimate, and upgrade with a correct entitlement-delta preview.
- [ ] `pnpm test --filter @tamma/dashboard` and `--filter @tamma/dashboard-user` green; no new lint
      errors; BYOK key-leak + member-403 tests pass at 100% coverage on those paths.

## Logging Requirements

The browser UI uses sparse, structured console logging (no Pino in-browser); the server endpoints
it calls own the authoritative Pino logs.

- **INFO**: plan version saved, margin policy updated, custom plan minted/assigned, credits granted,
  promo created/redeemed, subscribe committed (log endpoint + status + non-sensitive ids only).
- **DEBUG**: client request issued (method + path, no body), tab/page mounted, estimate requested.
- **WARN**: 409 deprecate-with-assignments surfaced, 422 promo rejection, 403 mutation attempt by
  member, entitlement-violation downgrade flagged.
- **ERROR**: non-2xx surfaced to the user (status + path), client typed-error thrown.
- **Credential safety**: NEVER log the BYOK plaintext key, any secret-cabinet value, or full
  response bodies that could contain a key/credential. The BYOK field is excluded from all logging
  and from any error-context object.

## Related

- Epic 34 stories: `docs/stories/epic-34/story-34-1/` … `story-34-7/`
- Implementation plan: `docs/superpowers/plans/2026-06-17-34-9-pricing-and-plan-management-dashboards-plan.md`
- Pattern exemplars: `packages/dashboard/src/pages/admin/AdminLayout.tsx`,
  `packages/dashboard-user/src/pages/alerts/TenantAlertFeed.tsx`

## References

- **MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md)
- CLAUDE.md — Operating Modes (single-user vs SaaS) and per-mode ownership rule
- CLAUDE.md — Prompt Store Architecture / RBAC (endpoint-shape-identical, auth-middleware-decides precedent)

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
