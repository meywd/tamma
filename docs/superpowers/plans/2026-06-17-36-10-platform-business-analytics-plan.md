# Story 36-10 — Platform Business Analytics (Owner-Only: MRR, Churn, Conversion) — Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan step-by-step. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every step writes tests
> before implementation. Read [BEFORE_YOU_CODE.md](../../guides/BEFORE_YOU_CODE.md) first.

**Goal:** Give the platform owner a single owner-only business-analytics surface — MRR/ARR, net-new
vs churned MRR, churn rate, trial→paid conversion, active/trialing/churned tenant counts, plan
distribution, and platform-wide gross margin — computed entirely from **control-plane** data
(`ControlPlaneDbContext`: `Tenant`, `Plan`, Epic 35 subscription/billing entities; the
`platform_analytics_hourly` fact table; the Epic 36-7 pricing source). It is **SaaS-mode only** and
**strictly never exposed to any tenant** — gated behind `PlatformOwnerAccess`. It **extends**
`AdminAnalyticsEndpoints` / `IPlatformAnalyticsService` (Story 28-10) rather than duplicating the
ops counters from Epic 5/23.

**Story file:** `docs/stories/epic-36/story-36-10/36-10-platform-business-analytics.md`

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API), React/Vite
dashboard in `packages/dashboard` (Vitest). xUnit tests live in
`apps/tamma-elsa/tests/Tamma.Api.Tests/`; docker-bound suites run via
`sg docker -c "dotnet test ..."` (memory `reference_dotnet_test_docker`). Dashboard tests:
`pnpm test --filter @tamma/dashboard`.

---

## Non-goals (YAGNI guard)

- **NO tenant-facing business analytics.** There is no `/api/v1/orgs/{tenantId}/analytics/business`
  variant and never will be in this story. Owner-only, full stop.
- **NO per-tenant schema reads for business detail.** Business metrics come from CP tables + the
  CP-resident `platform_analytics_hourly` fact table + the pricing source. The service must not take
  an `ITenantConnectionResolver` dependency.
- **NO duplication of Epic 5/23 ops metrics.** Workflow/agent-dispatch/cost counters already exist
  on `IPlatformAnalyticsService`; reuse `GetTopTenantsAsync`, don't re-query the tenant directory.
- **NO new fact table or migration.** Provider cost basis comes from existing
  `platform_analytics_hourly.CostUsd`; revenue from the Epic 36-7 pricing source; subscription state
  from Epic 35 entities. This story adds a read-side service + 2 endpoints + a dashboard tab only.
- **NO single-user surface.** No MRR/churn concept with one user; routes are not mounted in
  single-user mode.
- **NO blocking on Epic 35 / 36-7.** Ship a documented degraded mode (coarse MRR from
  `Tenant.Plan`+`Plan.MonthlyPriceUsd`; churn/conversion/margin zeroed) and swap real sources behind
  the ports when they land — no contract change.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### What exists and is reused

| Artifact | Path | Use |
|---|---|---|
| `AdminAnalyticsEndpoints` (3 owner-only handlers) | `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs` | Extend with `GetBusiness` + `GetBusinessMargin`. |
| `IPlatformAnalyticsService` + impl (`GetTopTenantsAsync`, injected `_clock`) | `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/{IPlatformAnalyticsService,PlatformAnalyticsService}.cs` | Reuse `GetTopTenantsAsync`; copy the `_clock`-based UTC windowing pattern (`PlatformAnalyticsService.cs:84`). |
| Analytics DTOs (`TenantAnalyticsRow`, `CostAggregates`, …) | `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/PlatformAnalyticsDtos.cs` | Add new business DTOs alongside. |
| `PlatformAnalyticsHourly` (`CostUsd` = provider cost basis) | `apps/tamma-elsa/src/Tamma.Data/Entities/PlatformAnalyticsHourly.cs` | Sum `CostUsd` over the UTC window for `providerCostBasisUsd`. |
| `Plan` (`MonthlyPriceUsd`, `Slug`, `DisplayName`, `IsActive`) | `apps/tamma-elsa/src/Tamma.Data/Entities/Plan.cs` | MRR per plan; plan distribution rows. |
| `Tenant` (`Plan` slug, `Type`, `DeletedAt`, `ProvisioningState`) | `apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs` | Active/plan-distribution counts; CP `DeletedAt` filter excludes soft-deleted. |
| `ControlPlaneDbContext` | `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Sole DB dependency of the new service. |
| `PlatformOwnerAccess` policy | `apps/tamma-elsa/src/Tamma.Api/Program.cs` ~986 | Gate both endpoints. (Spec says "OwnerAccess"; the real platform-owner policy is `PlatformOwnerAccess`. `OwnerAccess` ~971 is the looser per-tenant-owner gate — DO NOT use it here.) |
| Owner-only analytics maps (template) | `Program.cs` ~1343-1350 (`/analytics/summary|tenants|events` each `RequireAuthorization("PlatformOwnerAccess")`) | Copy the mapping shape. |
| Endpoint UTC coercion precedent | `AdminAnalyticsEndpoints.GetEventHistogram` (`DateTime.SpecifyKind(... ToUniversalTime(), Utc)`) | Apply to `from`/`to`. |
| `ITammaModeProvider` / `TammaMode.cs` | `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs` | SaaS-gate the route mapping. |
| Admin dashboard tabs (`AdminTab` union + `TABS` + render switch) | `packages/dashboard/src/pages/admin/AdminLayout.tsx` | Add the "Business Analytics" tab. |
| Admin API client convention | `packages/dashboard/src/services/admin/admin-api-client.ts` | Mirror for `business-analytics-client.ts`. |
| Chart primitives | `packages/dashboard/src/components/knowledge-base/analytics/*` | Reuse for the new charts where practical. |

### What does NOT exist yet (mark NEW; degraded-mode until they land)

- **Epic 35 subscription/billing entities** — `grep Entities | -iE 'subscript|billing|invoice|...'`
  returns **nothing**. So paying/trialing/churned subscription *state*, discounts, billing-cycle
  events, and conversion data have no source today. Until they land: coarse MRR/plan distribution
  from `Tenant.Plan` + `Plan.MonthlyPriceUsd`; churn/conversion/net-new/churned MRR = `0`/`null`.
- **Story 36-7 pricing/margin source** (`platformBilledUsd`) — NEW. Until it lands: margin endpoint
  reports `platformBilledUsd = 0`. The story dirs `docs/stories/epic-36/story-36-7` and
  `story-36-3` exist (planned), the C# entities do not.
- **`Tenant` has no `Status`/`TrialEndsAt`/`PlanId`** — only `Plan` (string slug), `Type`,
  `DeletedAt`, `ProvisioningState`. Conversion/trial/churn fidelity therefore *requires* Epic 35.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user | SaaS |
|---|---|---|
| Who owns business analytics? | N/A — no billing relationship; routes not mounted (`404`). | Platform owner ONLY (`PlatformOwnerAccess`). Never any tenant. |
| What data is read? | — | CP only: `Tenant`, `Plan`, Epic 35 entities, `platform_analytics_hourly`, Epic 36-7 pricing. |
| Surface? | Hidden. | Owner-only "Business Analytics" admin tab. |

---

## Architecture

**CP-only read service → 2 owner-only endpoints → owner-only dashboard tab.** No new persistence.

1. **`IBusinessAnalyticsService` + `BusinessAnalyticsService`** (new, `Services/Analytics/`) — the
   single read-side seam. Ctor: `ControlPlaneDbContext`, Epic 35 billing read port, Epic 36-7
   pricing read port, `IPlatformAnalyticsService` (for `GetTopTenantsAsync`), `TimeProvider`/`IClock`
   (deterministic windowing). No per-tenant connection dependency — that absence is asserted by a
   test (AC10).
2. **DTOs** (new, alongside `PlatformAnalyticsDtos.cs`): `BusinessAnalyticsSummary`,
   `PlanDistributionRow`, `PlatformMarginSummary`, with a `degradedMode` flag + missing-source list.
3. **Endpoints** (extend `AdminAnalyticsEndpoints`): `GET /api/admin/analytics/business` and
   `GET /api/admin/analytics/business/margin`, mapped in `Program.cs` with
   `RequireAuthorization("PlatformOwnerAccess")`, SaaS-gated via `ITammaModeProvider`. `from`/`to`
   coerced to UTC like `GetEventHistogram`.
4. **Dashboard**: `BusinessAnalyticsTab.tsx` + `business-analytics-client.ts`; registered in
   `AdminLayout.tsx`. Owner-only; degraded-mode banner when Epic 35/36-7 absent.

**Metric definitions** are pinned in the story (MRR, ARR=MRR×12, net-new/churned MRR, churn rate
over a window-start cohort, trial→paid conversion with null-on-zero-denominator, plan distribution,
gross margin = billed − cost basis). All windows derive from the injected clock so a fixed clock +
fixed `from`/`to` gives a byte-identical result.

---

## Step breakdown (TDD; each step: tests first → implement → green)

### Step 1 — DTOs + `IBusinessAnalyticsService` port + degraded-mode shape

- [ ] Add `BusinessAnalyticsSummary`, `PlanDistributionRow`, `PlatformMarginSummary` (+ `DegradedMode`
      bool + `MissingSources` list) to a new `Services/Analytics/BusinessAnalyticsDtos.cs`.
- [ ] Add `IBusinessAnalyticsService` with `GetSummaryAsync(from?, to?, ct)` and
      `GetMarginAsync(from?, to?, ct)`.
- [ ] No tests yet (pure declarations); compiles clean.

### Step 2 — `BusinessAnalyticsService`: MRR/ARR + plan distribution (coarse path) — TESTS FIRST

- [ ] `tests/Tamma.Api.Tests/Epic36/BusinessAnalyticsServiceTests.cs`: fixed `TimeProvider`, in-memory
      CP fixture of tenants across `free`/`team`/`enterprise` + a soft-deleted tenant. Assert:
      `mrrUsd` = Σ non-free non-deleted `Plan.MonthlyPriceUsd`; `arrUsd = mrr × 12`; free contributes
      0; soft-deleted excluded; `planDistribution` has one row per active plan incl. zero-count;
      per-plan MRR correct; **no tenant ids in `PlanDistribution` rows**.
- [ ] Implement the coarse path: CP query grouped by `Tenant.Plan` joined to `Plan`; clock-derived
      `windowEnd`. Set `degradedMode = true` + `MissingSources = ["Epic35.Billing","Story36-7.Pricing"]`
      when those ports are absent/null.
- [ ] Green.

### Step 3 — Churn, net-new/churned MRR, trial→paid conversion (Epic 35 path + degraded fallback) — TESTS FIRST

- [ ] Extend the service test: with an Epic 35 billing read-port **fake** supplying subscription
      state + transitions, assert `churnRate` (window-start cohort), `netNewMrrUsd`/`churnedMrrUsd`
      (`endMrr − startMrr ≈ netNew − churned`), `trialsEnded`/`trialsConverted`/
      `trialToPaidConversionRate`, zero-cohort → churn 0, zero `trialsEnded` → conversion `null`.
- [ ] With the port **absent** (degraded), assert churn/conversion/net-new/churned = `0`/`null` and
      `degradedMode = true`.
- [ ] Implement against the Epic 35 read port (define a thin `IBillingSubscriptionReadPort` if Epic 35
      hasn't shipped one yet — keep it a minimal read contract the eventual entities satisfy).
- [ ] Green.

### Step 4 — Margin endpoint logic — TESTS FIRST

- [ ] Test: `providerCostBasisUsd` = Σ `platform_analytics_hourly.CostUsd` where `Hour ∈ [from,to)`;
      `platformBilledUsd` from a pricing read-port fake; `grossMarginUsd = billed − cost`;
      `grossMarginPct = margin / billed` (billed 0 → pct 0); degraded → `platformBilledUsd = 0`.
- [ ] Implement `GetMarginAsync` querying the fact table (CP) + pricing port.
- [ ] Green.

### Step 5 — Determinism + control-plane-only assertions — TESTS FIRST

- [ ] Test: fixed `TimeProvider` + fixed `from`/`to` → two calls produce byte-identical summaries
      (serialize + compare). Default window = trailing 30d off the fixed clock.
- [ ] Test/assertion: `BusinessAnalyticsService` ctor takes only `ControlPlaneDbContext` + the read
      ports + `IPlatformAnalyticsService` + `TimeProvider` — **no** `ITenantConnectionResolver`
      (reflect over the ctor params, or a compile-time check).
- [ ] Green.

### Step 6 — Endpoints + Program.cs wiring (SaaS-gated, owner-only) — TESTS FIRST

- [ ] `tests/Tamma.Api.Tests/Epic36/BusinessAnalyticsEndpointsRbacTests.cs`: WebApplicationFactory.
      Owner principal → 200 on both routes; tenant member / tenant_admin / tenant_owner → 403;
      cross-tenant principal → 403/404; unauthenticated → 401. `from`/`to` parsed as UTC regardless
      of `DateTime.Kind`. Single-user mode → routes return 404.
- [ ] Add `GetBusiness` + `GetBusinessMargin` static handlers to `AdminAnalyticsEndpoints.cs`
      (coerce `from`/`to` to UTC like `GetEventHistogram`).
- [ ] Register `IBusinessAnalyticsService` in DI (next to `IPlatformAnalyticsService`); map both
      routes in `Program.cs` near the `/analytics/*` block with `RequireAuthorization("PlatformOwnerAccess")`,
      conditional on `ITammaModeProvider` reporting SaaS.
- [ ] Green; full C# suite stays green.

### Step 7 — Dashboard Business Analytics tab — TESTS FIRST

- [ ] `business-analytics-client.ts` mirroring `admin-api-client.ts`; calls both endpoints.
- [ ] `packages/dashboard/src/pages/admin/__tests__/BusinessAnalyticsTab.test.tsx` (Vitest + Testing
      Library): tab renders MRR/ARR/churn/conversion/plan-distribution/margin from a mocked client;
      degraded-mode banner when `degradedMode`; tab hidden for non-owner.
- [ ] `BusinessAnalyticsTab.tsx` (reuse KB analytics chart primitives where practical).
- [ ] Register in `AdminLayout.tsx`: add `'business-analytics'` to `AdminTab`, a `TABS` entry, and the
      render branch; gate visibility on the existing owner-only admin gate.
- [ ] `pnpm test --filter @tamma/dashboard` green; no new lint errors.

---

## Step order & dependencies

Step 1 → 2 → 3 → 4 → 5 → 6 → 7. Steps 2–5 build the service incrementally; Step 6 exposes it; Step 7
visualizes it. Steps 3/4 degrade gracefully if Epic 35 / 36-7 ports are absent, so the wave is not
blocked on those landing.

## Risks

- **Cross-tenant leakage (the cardinal sin).** Mitigation: routes are owner-only (`PlatformOwnerAccess`);
  there is no tenant-reachable code path; aggregate DTOs carry no tenant ids except the owner-only
  drill-down via `GetTopTenantsAsync`. The RBAC test (Step 6) is acceptance-blocking.
- **Epic 35 / 36-7 not landed.** Mitigation: documented degraded mode — coarse MRR from
  `Tenant.Plan`/`Plan.MonthlyPriceUsd`, churn/conversion/margin zeroed, `degradedMode` flag + banner.
  Real sources swap in behind the ports with no contract change.
- **`OwnerAccess` vs `PlatformOwnerAccess` confusion.** The spec says "OwnerAccess"; the *real*
  platform-owner-only policy is `PlatformOwnerAccess` (`OwnerAccess` lets every personal-tenant owner
  through). Using the wrong one would expose business analytics to every tenant owner — exactly the
  forbidden outcome. Step 6 wires `PlatformOwnerAccess` and tests a tenant_owner gets 403.
- **Non-deterministic windows.** Mitigation: inject `TimeProvider`/`IClock` (mirror
  `PlatformAnalyticsService._clock`); never `DateTime.UtcNow` inline; Step 5 pins determinism.
- **Accidental per-tenant schema read.** Mitigation: ctor takes only `ControlPlaneDbContext` + ports;
  Step 5 asserts no `ITenantConnectionResolver` dependency.
- **Ops/business metric duplication.** Mitigation: reuse `GetTopTenantsAsync` and the existing fact
  table; do not re-add workflow/dispatch/cost counters.
