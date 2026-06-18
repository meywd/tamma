# Story 36-10: Platform Business Analytics (Owner-Only: MRR, Churn, Conversion)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

## User Story

As the **platform owner**,
I want a single owner-only business-analytics surface that reports MRR/ARR, churn, trial→paid
conversion, active/trialing/churned tenant counts, plan distribution, and platform-wide gross
margin — computed entirely from control-plane tenant/plan/subscription data plus the platform fact
table,
So that I can see how the business is performing across the whole fleet **without ever exposing one
tenant's revenue, identity, or margin to another tenant**.

## Priority

P1 — Platform-owner business visibility. Built on the operational analytics rollup (Story 28-10)
and the billing/pricing surfaces (Epic 35 billing, Story 36-7 pricing/margin).

## Boundary Note (READ FIRST — non-negotiable scope guard)

This story is **strictly platform-owner-only (SaaS mode only)** and adds a *business* analytics
layer on top of the existing *operational* analytics rollup. The following boundaries are
acceptance-blocking, not advisory:

1. **Owner-only, never per-tenant.** Every endpoint added here is gated by the platform-owner
   policy (`PlatformOwnerAccess` — the real policy name; the spec calls it "OwnerAccess"). There is
   **no** tenant-facing variant. A tenant member/admin, or a cross-tenant principal, MUST receive
   `403` (or `404` where the route is owner-namespaced) and a test asserts this.
2. **Control-plane sourced only.** All metrics are computed from control-plane data:
   `ControlPlaneDbContext` (`Tenant`, `Plan`, the Epic 35 subscription/billing entities) plus the
   `platform_analytics_hourly` fact table and `platform_events`. This story MUST NOT open a
   per-tenant schema connection to read per-tenant *business* detail. The only per-tenant data it
   touches is the **pre-aggregated** fact-table rows (which are already CP-resident and fleet-wide
   by design) and the owner-visible tenant directory it already exposes via
   `GetTopTenantsAsync`.
3. **No cross-tenant identity leakage.** Aggregate metrics (MRR, churn rate, conversion rate, plan
   distribution) carry no per-tenant identifiers. Where a tenant breakdown is unavoidable (e.g. the
   plan-distribution drill-down reuses `GetTopTenantsAsync`), it is only ever returned to the
   platform owner — never assembled into a payload reachable by any tenant principal.
4. **Do not duplicate Epic 5/23 ops metrics.** This extends `AdminAnalyticsEndpoints` /
   `IPlatformAnalyticsService` with a new *business* surface; it does not re-implement the workflow/
   agent-dispatch/cost ops counters that already exist there.
5. **Single-user mode has no business analytics.** In single-user mode there is no MRR/churn/
   conversion concept (one user, no billing relationship). The new endpoints are SaaS-only; in
   single-user mode they are not mounted (or return `404`).

## Acceptance Criteria

1. **Business summary endpoint.** `GET /api/admin/analytics/business` returns, for a selectable UTC
   window (default trailing 30d): `mrrUsd`, `arrUsd` (`mrrUsd × 12`), `netNewMrrUsd`,
   `churnedMrrUsd`, `activeTenants`, `trialingTenants`, `churnedTenants` (in window),
   `trialToPaidConversionRate`, and `planDistribution` (count + MRR per plan slug). Computed from
   `Tenant` + `Plan` (+ subscription state from the re-targeted Epic 20 / Epic 35 billing entities).

2. **MRR/ARR computation.** `mrrUsd` = sum of the active recurring monthly price for every tenant
   in an active *paying* subscription state at window end, derived from each tenant's plan price
   (`Plan.MonthlyPriceUsd`) net of any active discount/credit recorded by Epic 35. `arrUsd` is
   `mrrUsd × 12`. Free-plan and trialing tenants contribute `0` to MRR. The math is unit-tested
   against a fixed fixture.

3. **Net-new vs churned MRR.** `netNewMrrUsd` = MRR added by tenants that became paying inside the
   window (new + upgrades); `churnedMrrUsd` = MRR lost by tenants that cancelled/downgraded/expired
   inside the window. Both are signed-consistent (churn reported as a positive "lost" figure) and
   reconcile such that `endMrr − startMrr ≈ netNewMrrUsd − churnedMrrUsd` for a closed window.

4. **Churn definition is documented and deterministic.** Churn is computed over a defined cohort
   window (default trailing 30d): `churnRate` = churned paying tenants in window ÷ paying tenants at
   window start. The exact definition (cohort = tenants paying at window start; churned = those who
   left a paying state before window end) is documented in this story and in the service XML-doc.
   For a given UTC window the result is **deterministic** — windowing derives from an injected clock
   (`TimeProvider`/`IClock`) exactly like `PlatformAnalyticsService`, and a test pins determinism.

5. **Trial→paid conversion.** `trialToPaidConversionRate` = tenants that converted from trialing to
   a paying state in the window ÷ tenants whose trial ended in the window. The numerator/denominator
   are also returned (`trialsConverted`, `trialsEnded`) so a zero denominator yields a `null`/`0`
   rate rather than a divide-by-zero, and the raw counts are auditable.

6. **Active / trialing / churned tenant counts.** `activeTenants` = tenants in a paying state at
   window end (excludes soft-deleted via the CP `DeletedAt` query filter); `trialingTenants` =
   tenants in a trial state at window end; `churnedTenants` = tenants who left a paying state inside
   the window. Counts exclude soft-deleted and non-`org` placeholder tenants consistently with the
   existing tenant-directory counts.

7. **Plan distribution.** `planDistribution` is a list of `{ planSlug, displayName, tenantCount,
   mrrUsd }`, one row per `Plan`, covering every active plan (zero-count plans included so the chart
   is stable). Sourced from `Tenant.Plan` joined to `Plan`; no per-tenant identifiers in the
   aggregate rows.

8. **Margin endpoint.** `GET /api/admin/analytics/business/margin` returns platform gross margin for
   the window: `platformBilledUsd` − `providerCostBasisUsd`, plus the two components and a
   `grossMarginPct`. `platformBilledUsd` comes from the Epic 36-7 pricing/margin source (NEW —
   marked below); `providerCostBasisUsd` is summed from `platform_analytics_hourly.CostUsd` (the
   provider cost basis already rolled up by Story 28-10) over the same UTC window.

9. **Owner-only RBAC enforced.** Both business endpoints are gated by `PlatformOwnerAccess`. A test
   asserts a tenant principal (member, tenant_admin, tenant_owner) and a cross-tenant principal
   cannot reach either route (`403`/`404`), and that an unauthenticated request is rejected.

10. **Control-plane sourced; no per-tenant schema reads.** A test/assertion verifies the service
    depends only on `ControlPlaneDbContext` + the fact table + the pricing source — it never
    resolves a per-tenant connection. Per-tenant business detail beyond owner-visible aggregates is
    never read.

11. **No per-tenant identity leakage to other tenants.** Because the routes are owner-only there is
    no tenant-reachable path; a test confirms the business payloads are only ever produced under the
    owner policy, and that the aggregate fields contain no tenant ids/slugs except in the
    owner-only plan/top-tenant drill-down which reuses `GetTopTenantsAsync`.

12. **Reuse, don't re-implement.** The plan/tenant breakdown reuses `GetTopTenantsAsync` rather than
    re-querying the tenant directory; the ops counters (workflows/dispatch/cost) are NOT duplicated.

13. **Admin dashboard Business Analytics view.** The admin dashboard (`packages/dashboard`) gains a
    "Business Analytics" tab rendering MRR/ARR, churn, trial→paid conversion, plan distribution, and
    gross-margin charts, behind the existing owner-only admin gate. It is not shown to non-owner
    users and is absent in single-user mode.

14. **Window selection.** Both endpoints accept an optional `window` (or `from`/`to`) query param;
    absent → trailing 30d. Inputs are parsed as UTC (mirroring the `DateTime.Kind` handling already
    in `AdminAnalyticsEndpoints.GetEventHistogram`).

15. **Tests.** Unit + integration tests cover MRR/ARR/net-new/churned math, churn-rate definition,
    trial→paid conversion (incl. zero-denominator), plan distribution, margin computation,
    `PlatformOwnerAccess` enforcement (tenant principal denied), and UTC determinism for a fixed
    window + fixed clock.

## Technical Design

### Mode scoping (mandatory two-model answer per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns business analytics? | **N/A** — no billing relationship, no MRR/churn/conversion concept. The endpoints are not mounted (or return `404`). | The **platform owner** exclusively (`PlatformOwnerAccess`). Never any tenant. |
| What data is read? | — | Control-plane only: `Tenant`, `Plan`, Epic 35 subscription/billing entities, `platform_analytics_hourly`, the Epic 36-7 pricing source. |
| Where does it surface? | Hidden in the dashboard. | Owner-only "Business Analytics" admin tab. |

Mode comes from the already process-stable `ITammaModeProvider` (`TammaMode.cs`). The endpoint
mapping checks SaaS mode at wire time so single-user deployments never expose the routes.

### Service: `BusinessAnalyticsService`

New `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/BusinessAnalyticsService.cs` implementing a
new `IBusinessAnalyticsService` port. It mirrors the existing `PlatformAnalyticsService` shape:

- Constructor takes `ControlPlaneDbContext`, the Epic 35 billing/subscription read port, the Epic
  36-7 pricing/margin read port, `IPlatformAnalyticsService` (to reuse `GetTopTenantsAsync`), and a
  `TimeProvider`/`IClock` (so windowing is deterministic and unit-testable — same pattern as
  `PlatformAnalyticsService`'s injected `_clock`).
- All windows derive from `clock.GetUtcNow().UtcDateTime` at the call site; never `DateTime.UtcNow`
  inline, so a fixed clock yields a fixed result.

```csharp
public interface IBusinessAnalyticsService
{
    /// <summary>
    /// MRR/ARR, net-new vs churned MRR, active/trialing/churned tenant counts,
    /// trial→paid conversion, and plan distribution for [windowStart, windowEnd).
    /// Platform-owner-only — reads control-plane tenant/plan/subscription data;
    /// never opens a per-tenant schema connection.
    /// </summary>
    Task<BusinessAnalyticsSummary> GetSummaryAsync(
        DateTime? from = null, DateTime? to = null, CancellationToken ct = default);

    /// <summary>
    /// Platform gross margin = sum(platformBilledUsd) − sum(providerCostBasisUsd)
    /// across all tenants for the window. Billed revenue from the Epic 36-7
    /// pricing source; provider cost basis summed from platform_analytics_hourly.CostUsd.
    /// </summary>
    Task<PlatformMarginSummary> GetMarginAsync(
        DateTime? from = null, DateTime? to = null, CancellationToken ct = default);
}
```

### DTOs (new, alongside `PlatformAnalyticsDtos.cs`)

```csharp
public sealed record BusinessAnalyticsSummary(
    DateTime WindowStart,
    DateTime WindowEnd,
    decimal MrrUsd,
    decimal ArrUsd,                 // MrrUsd * 12
    decimal NetNewMrrUsd,
    decimal ChurnedMrrUsd,
    int ActiveTenants,
    int TrialingTenants,
    int ChurnedTenants,
    int PayingTenantsAtWindowStart,
    double ChurnRate,               // churnedTenants / payingTenantsAtWindowStart
    int TrialsEnded,
    int TrialsConverted,
    double? TrialToPaidConversionRate,   // null when TrialsEnded == 0
    IReadOnlyList<PlanDistributionRow> PlanDistribution,
    DateTime GeneratedAt);

public sealed record PlanDistributionRow(
    string PlanSlug,
    string DisplayName,
    int TenantCount,
    decimal MrrUsd);

public sealed record PlatformMarginSummary(
    DateTime WindowStart,
    DateTime WindowEnd,
    decimal PlatformBilledUsd,       // Epic 36-7 pricing source (NEW)
    decimal ProviderCostBasisUsd,    // sum(platform_analytics_hourly.CostUsd) in window
    decimal GrossMarginUsd,          // billed − cost basis
    double GrossMarginPct,           // grossMargin / billed (0 when billed == 0)
    DateTime GeneratedAt);
```

### Metric definitions (documented, deterministic)

- **MRR** — Σ over tenants in a paying subscription state at `windowEnd` of
  `(plan.MonthlyPriceUsd − activeDiscountUsd)`. Free + trial tenants contribute 0. Subscription
  state + discount come from the Epic 35 billing entities; the plan price from `Plan`.
- **ARR** — `MRR × 12`.
- **Net-new MRR** — Σ MRR gained by tenants entering/upgrading into a paying state in
  `[windowStart, windowEnd)`.
- **Churned MRR** — Σ MRR lost by tenants leaving a paying state (cancel/downgrade/expire) in the
  window (reported positive).
- **Churn rate** — cohort = tenants paying at `windowStart`; churned = those no longer paying at
  `windowEnd`; `churnRate = churnedCount / cohortCount` (`0` when cohort empty).
- **Trial→paid conversion** — `trialsConverted / trialsEnded` where `trialsEnded` = trials that
  ended in the window and `trialsConverted` = of those, ones that entered a paying state. `null`
  when `trialsEnded == 0`.
- **Active / trialing / churned counts** — point-in-time at `windowEnd` (active, trialing) and
  in-window flow (churned). Soft-deleted tenants excluded by the CP `DeletedAt` query filter.
- **Plan distribution** — `GROUP BY Tenant.Plan` joined to `Plan`; one row per active plan including
  zero-count plans. MRR per plan = Σ paying-tenant prices in that plan.
- **Gross margin** — `platformBilledUsd − providerCostBasisUsd` over the window;
  `providerCostBasisUsd = Σ platform_analytics_hourly.CostUsd WHERE Hour ∈ [windowStart, windowEnd)`.

### Endpoints (extend `AdminAnalyticsEndpoints`)

Two new static handlers in `AdminAnalyticsEndpoints.cs`, wired in `Program.cs` next to the existing
`/analytics/*` maps with `RequireAuthorization("PlatformOwnerAccess")` (matching the existing
owner-only admin endpoints — `AdminAnalyticsEndpoints` handlers are mounted on the
`/api/admin` group). The maps are conditional on SaaS mode.

```
GET /api/admin/analytics/business[?from=ISO&to=ISO]        → BusinessAnalyticsSummary   (PlatformOwnerAccess)
GET /api/admin/analytics/business/margin[?from=ISO&to=ISO] → PlatformMarginSummary      (PlatformOwnerAccess)
```

UTC handling mirrors `GetEventHistogram` — `from`/`to` are coerced to `DateTimeKind.Utc` before
reaching the service so the window matches the UTC-stored CP columns.

### Admin dashboard

- New `packages/dashboard/src/services/admin/business-analytics-client.ts` (mirrors
  `admin-api-client.ts`) calling the two endpoints.
- New `packages/dashboard/src/pages/admin/BusinessAnalyticsTab.tsx`; register in
  `packages/dashboard/src/pages/admin/AdminLayout.tsx` by adding `'business-analytics'` to the
  `AdminTab` union and a `{ id: 'business-analytics', label: 'Business Analytics' }` entry to
  `TABS`, plus the `activeTab === 'business-analytics' && <BusinessAnalyticsTab />` branch.
- Charts: MRR/ARR tiles, net-new vs churned MRR, churn-rate trend, trial→paid conversion, plan
  distribution (bar), gross-margin tile (billed vs cost basis). Reuse the chart primitives already
  used by the knowledge-base analytics components where practical.
- The tab is only rendered for platform-owner users (existing owner-only admin gate) and is absent
  in single-user mode.

## Dependencies

- **Story 28-10** (platform fact table `platform_analytics_hourly` + `IPlatformAnalyticsService` +
  `AdminAnalyticsEndpoints`) — extended here; provides `CostUsd` (provider cost basis) and
  `GetTopTenantsAsync` (reused). VERIFIED present.
- **Epic 35 billing** (subscription state, billing-cycle, discounts/credits) — **NEW**: subscription/
  billing entities do **not** exist in `Tamma.Data/Entities` yet. This story's subscription-state
  reads (paying/trialing/churned, conversion, net-new/churned MRR) depend on those entities landing.
  Until then the service falls back to `Tenant.Plan` + `Plan.MonthlyPriceUsd` for a coarse MRR/plan
  distribution and reports trial/churn/conversion as `0`/`null` (documented degraded mode).
- **Epic 20 (re-targeted)** — the subscription/plan-state lineage the spec references; in the C#
  control plane this lands as the Epic 35 billing entities above.
- **Story 36-7** (pricing / margin) — **NEW**: provides `platformBilledUsd` for the margin endpoint.
  Until 36-7 lands, the margin endpoint reports `platformBilledUsd = 0` (and thus a negative/zero
  margin) behind the same degraded-mode flag.
- **Epic 16 / OwnerAccess policy** — in this repo the platform-owner policy is `PlatformOwnerAccess`
  (`Program.cs` ~986). VERIFIED present.

## Testing Strategy

1. **MRR/ARR math** — fixture of tenants across plans + subscription states; assert `mrrUsd`,
   `arrUsd = mrr × 12`, free/trial contribute 0, discounts applied.
2. **Net-new vs churned MRR** — fixture with new/upgrade/cancel/downgrade events inside the window;
   assert `endMrr − startMrr ≈ netNew − churned`.
3. **Churn rate** — cohort at window start vs leavers; zero-cohort → `0`; documented definition.
4. **Trial→paid conversion** — trials ended/converted counts; zero `trialsEnded` → `null` rate (no
   divide-by-zero).
5. **Plan distribution** — every active plan present incl. zero-count; MRR per plan correct; no
   tenant ids in aggregate rows.
6. **Margin** — `platformBilledUsd` from the pricing source minus `Σ CostUsd` from
   `platform_analytics_hourly`; `grossMarginPct` correct; billed == 0 → pct 0.
7. **RBAC** — `PlatformOwnerAccess` allows owner; tenant member/tenant_admin/tenant_owner and
   cross-tenant principals get `403`/`404`; unauthenticated rejected. (xUnit integration test
   against the endpoint with a tenant principal.)
8. **Control-plane only** — assert the service has no per-tenant-connection dependency (constructor
   takes only `ControlPlaneDbContext` + ports; no `ITenantConnectionResolver`).
9. **UTC determinism** — fixed `TimeProvider` + fixed `from`/`to` → byte-identical summary across
   runs; `from`/`to` parsed as UTC regardless of request `DateTime.Kind`.
10. **Mode** — SaaS mode mounts the routes; single-user mode does not (route returns `404`).
11. **Dashboard** — colocated Vitest/Testing-Library: tab renders MRR/churn/conversion/margin,
    hidden for non-owner, ack of degraded-mode banner when Epic 35/36-7 absent.

Docker-bound xUnit suites run via `sg docker -c "dotnet test ..."` (see project memory
`reference_dotnet_test_docker`).

## Estimated Effort

3-4 days (service + 2 endpoints + DTOs + dashboard tab + tests). Margin and full churn/conversion
fidelity track Epic 35 / Story 36-7 availability; coarse plan/MRR works against existing
`Tenant`/`Plan` today.

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/IBusinessAnalyticsService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/BusinessAnalyticsService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/BusinessAnalyticsDtos.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs` | Modify (add `GetBusiness`, `GetBusinessMargin`) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (register service; map 2 endpoints w/ `PlatformOwnerAccess`, SaaS-gated) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Epic36/BusinessAnalyticsServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Epic36/BusinessAnalyticsEndpointsRbacTests.cs` | Create |
| `packages/dashboard/src/services/admin/business-analytics-client.ts` | Create |
| `packages/dashboard/src/pages/admin/BusinessAnalyticsTab.tsx` | Create |
| `packages/dashboard/src/pages/admin/AdminLayout.tsx` | Modify (add tab to `AdminTab` union + `TABS` + render branch) |
| `packages/dashboard/src/pages/admin/__tests__/BusinessAnalyticsTab.test.tsx` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions
3. Reviewed Story 28-10 (`PlatformAnalyticsService`, `PlatformAnalyticsHourly`,
   `AdminAnalyticsEndpoints`) — this story extends, not duplicates, it
4. Confirmed which Epic 35 billing entities + Story 36-7 pricing source exist when you start; if
   absent, ship the coarse `Tenant.Plan`/`Plan.MonthlyPriceUsd` MRR + plan distribution and gate the
   churn/conversion/margin fields behind the documented degraded-mode flag
5. Planned the TDD cycle (Red-Green-Refactor)

### Architecture target (do not get this wrong)

- Target is the C# control plane: `apps/tamma-elsa` (`Tamma.Api`, `Tamma.Data`) + the React admin
  dashboard `packages/dashboard`. **`packages/api` is deleted — never target or cite it.**
- Business analytics is **control-plane** data only. Read `ControlPlaneDbContext`
  (`apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs`), the `platform_analytics_hourly` fact
  table, and the Epic 35 / 36-7 read ports. Do **not** resolve a per-tenant connection for business
  detail.

### Determinism

Inject `TimeProvider`/`IClock` exactly like `PlatformAnalyticsService` (`_clock` at
`PlatformAnalyticsService.cs:84`). Derive `now`, `windowStart`, `windowEnd` once at the top of each
method. Never call `DateTime.UtcNow` inline. This is what makes AC4/AC15 pass.

### RBAC precedent

`AdminAnalyticsEndpoints` handlers are mounted on `app.MapGroup("/api/admin")` with per-handler
`RequireAuthorization("PlatformOwnerAccess")` (the platform-owner policy defined in `Program.cs`
~986; the spec's "OwnerAccess" is `PlatformOwnerAccess` here — `OwnerAccess` is the looser
per-tenant-owner gate and MUST NOT be used). Follow the same pattern; the existing owner-only
analytics maps (`/analytics/summary|tenants|events`, `Program.cs` ~1343-1350) are the template.

### Degraded mode (Epic 35 / 36-7 not yet landed)

Subscription/billing entities and the pricing source are NEW. Until they exist:
- MRR/ARR/plan distribution compute from `Tenant.Plan` + `Plan.MonthlyPriceUsd` (every non-free,
  non-deleted tenant counts as paying — coarse but real).
- Churn, trial→paid conversion, net-new/churned MRR report `0`/`null`.
- Margin reports `platformBilledUsd = 0`.
Surface a `degradedMode: true` flag (+ list of missing sources) in the payload and a dashboard
banner, so the owner knows numbers are partial rather than wrong. Wire the real sources behind the
ports once 35/36-7 land — no endpoint contract change.

## Logging Requirements

- **INFO**: Business summary computed (`windowStart`, `windowEnd`, `mrrUsd`, `activeTenants`,
  `durationMs`); margin computed (`windowStart`, `windowEnd`, `platformBilledUsd`,
  `providerCostBasisUsd`, `grossMarginUsd`).
- **DEBUG**: Cohort/window boundaries resolved; degraded-mode source list; plan-distribution row
  counts.
- **WARN**: Degraded mode active (missing Epic 35 / 36-7 source named); zero paying cohort for churn
  (rate forced to 0); zero `trialsEnded` (conversion null).
- **ERROR**: CP query failure; pricing-source read failure (margin endpoint).
- **Structured context**: `{ windowStart, windowEnd, mode, degradedMode, durationMs }` where
  applicable.
- **Credential / leakage safety**: NEVER log per-tenant revenue keyed to a tenant id in a way that
  could surface cross-tenant; business payloads are owner-only — keep them out of any tenant-scoped
  log stream.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
