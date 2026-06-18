# Story 36-7: Cost-Basis vs Margin Analytics View — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan phase-by-phase. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every phase writes failing
> tests before implementation.

**Story:** `docs/stories/epic-36/story-36-7/36-7-pricing-cost-basis-and-platform-margin-model.md` |
**Epic 36** Analytics & Reporting Platform | **Priority** P0 | **Effort** 3-4 days |
**Date** 2026-06-17

---

## Goal

Ship the **read-side analytics view** of cost-basis vs margin: a per-tenant gross-margin report
(cost / billed revenue / margin / margin-% with provider·agent·workflow·cost-basis breakdowns and
a daily/monthly trend) and an **owner-only** platform-wide margin aggregate. The view **reads**
the already-priced `CostUsd` (cost basis) and `PlatformBilledUsd` (sell price) columns that
**Story 34-5** computed and **Story 36-2** persisted onto the **Story 36-1** fact tables, and
reports `revenue − cost`. It moves no money and **recomputes no pricing**.

## Boundary (the one rule that governs every phase)

This is the analytics **VIEW**, not the markup engine. Pricing — the margin multiplier, the cost
rate sheet, `cost × (1 + margin)`, BYOK zero-markup, the cabinet-based BYOK/platform
classification — all live in **Story 34-5** (`IUsagePricingEngine` / `IMarginPolicyResolver` /
`ProviderPricingService`) and **Story 32-9** (usage emission), applied once at projection time by
**Story 36-2**. 36-7 sums two already-priced columns and subtracts. **If a phase multiplies a cost
by a margin, reads a margin policy, or reads a secret cabinet, it has crossed into 34-5/36-2 — stop
and reconsider.** Phase 2 ships an executable CI guard (the AC2 no-recompute dependency test) so
this can't regress.

## Non-goals (YAGNI guard)

- **NO markup math, NO margin policy, NO rate sheet.** Read `PlatformBilledUsd` / `CostUsd`;
  never recompute them. (34-5 owns it.)
- **NO `PricingConfigService` / `AdminPricingEndpoints` / `MarginPolicy` / `Plan` / `AgentConfig`
  changes.** Those are the 34-5 markup-engine components the spec's primary-components list named;
  they are explicitly out of this story's scope per the boundaryNote.
- **NO fact-table schema change and NO projection logic.** 36-1 owns the schema; 36-2 owns
  population. This story only reads.
- **NO exports, NO scheduled reports, NO dashboard.** Later Epic 36 stories. This story ships the
  read API the dashboard will call.
- **NO money movement / Stripe.** Billing is Epic 35. This is reporting.
- **NO TypeScript-side analytics.** The view lives in the C# control plane (`apps/tamma-elsa`);
  `packages/*` consume it over HTTP. (`packages/api` is DELETED — never a target.)

## Current-state findings (verified 2026-06-17, repo @ main)

| Site | What exists today |
|---|---|
| `apps/tamma-elsa/src/Tamma.Data/Entities/PlatformAnalyticsHourly.cs` | CP fleet-wide hourly fact (28-10): `WorkflowsStarted/Completed/Failed`, `AgentDispatches`, `TokensIn/Out`, **`CostUsd` (decimal 20,4)** — but **NO `PlatformBilledUsd`** column. ⇒ platform-aggregate revenue must come from the per-tenant store. |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/IPlatformAnalyticsService.cs` + `PlatformAnalyticsService.cs` | Owner-side read port: `GetSummaryAsync`, `GetTopTenantsAsync`, `GetEventHistogramAsync`, `GetTenantResourceSummaryAsync`. Doc-comment: "Gate calls behind `OwnerAccess`." The fan-out + per-tenant-tolerance + `*.Empty` zeroed-result shape to mirror. Add `GetPlatformMarginAsync` here. |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs` | Static-handler style for `/api/admin/analytics/*`; `GetSummary`/`GetTopTenants`/`GetEventHistogram` injecting `IPlatformAnalyticsService`. Add `GetMargin` here. |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/PlatformAnalyticsDtos.cs` | `sealed record` DTO precedent (`PlatformAnalyticsSummary`, `CostAggregates(decimal Last24hUsd, …)`, windowed counts). Mirror for `PlatformMarginReport` / `TenantMarginRow`. |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs:1346-1351` | `admin.MapGet("/analytics/summary", AdminAnalyticsEndpoints.GetSummary).RequireAuthorization("OwnerAccess")` (+ tenants/events). Add `admin.MapGet("/analytics/margin", …)` beside it. |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs:971/986/991` | `OwnerAccess` (971), `PlatformOwnerAccess` (986), `MemberAccess` (991) policies already registered — reuse, don't add. |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs:1512` (per 34-5 plan) | `var orgs = app.MapGroup("/api/v1/orgs").RequireAuthorization("MemberAccess")` — the tenant-scoped group for `GET /api/v1/orgs/{tenantId}/analytics/margin`. |
| `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | Per-tenant DbSets (`ProviderDiagnostics`, `DomainEvents`, …). Story 36-1 adds `AnalyticsUsageHourly`/`AnalyticsUsageDaily` here (drafted, not yet merged). |
| `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs` | Per-call cost row: `Cost`, `InputTokens`/`OutputTokens`, `ProviderKey`, `AgentType`, `ProjectId`, `TenantId`. The diagnostic source 36-2 folds — read here only via the projected facts, not directly. |
| `apps/tamma-elsa/src/Tamma.Data/ITenantContext.cs` | `Guid? TenantId { get; }` — the per-request tenant the tenant-scope endpoint resolves from. `ITammaModeProvider` in `Services/PromptStore/TammaMode.cs`. |
| `apps/tamma-elsa/src/Tamma.Api/Services/Alerts/PostgresAlertSink.cs` (per 34-5 plan ~133/180) | Canonical DCB emit: `IEventRepository.AppendAsync(new DomainEvent { Type, TenantId, Tags, Metadata, Data })` in try/catch+log. Copy for `ANALYTICS.MARGIN.VIEWED`. |
| `apps/tamma-elsa/docs/.../SchemaPerTenantMigrationTests` / `ConventionStoreMigrationTests` | Postgres 17 Testcontainer precedent for per-tenant isolation tests (run via `sg docker -c "dotnet test ..."`). |

**Confirmed absent (this story creates):** `IMarginAnalyticsService` / `MarginAnalyticsService`,
`MarginAnalyticsDtos`, `MarginAnalyticsEndpoints`, `PlatformMarginReport`,
`IPlatformAnalyticsService.GetPlatformMarginAsync`, the `ANALYTICS.MARGIN.VIEWED` event.

**Confirmed absent today (upstream, NEW elsewhere — referenced, not built here):**
`IUsagePricingEngine` / `MarginPolicy` (Story 34-5); `AnalyticsUsageHourly`/`AnalyticsUsageDaily`
(Story 36-1, drafted) — both are dependencies, not 36-7 targets.

**Key gotcha:** `PlatformAnalyticsHourly` has **no `PlatformBilledUsd`** — do not try to read
revenue from the CP fact table. Sum per-tenant `analytics_usage_*` revenue across tenants for the
platform aggregate (Phase 3).

---

## Phased task breakdown (test-first / TDD)

### Phase 0 — Prereq gate (no code)

- [ ] Confirm Stories 36-1 (fact tables + `CostBasis` enum + `PlatformBilledUsd` column) and 36-2
      (which populates `CostUsd`/`PlatformBilledUsd`) are merged, or stub `AnalyticsUsageDaily`/
      `AnalyticsUsageHourly` on `TenantDbContext` so the read service + tests can compile. The view
      tolerates an unpopulated store (AC11 zeroed result) so it can land ahead of real projection
      data — but it cannot compile without the 36-1 entities.
- [ ] Re-read the boundary above and Story 34-5's plan §Goal so you know exactly which symbols you
      must NOT touch.

### Phase 1 — Margin-view DTOs (read-side contract)

- **Files:** `Tamma.Api/Services/Analytics/MarginAnalyticsDtos.cs` (new).
- **Tests first:** `tests/Tamma.Api.Tests/Analytics/MarginAnalyticsServiceTests.cs` (DTO portion):
  `MarginSummary.From(cost, revenue)` ⇒ `Margin = revenue − cost`, `MarginPct = (revenue−cost)/
  revenue` rounded 4dp, `Pct == 0` when `revenue == 0`, negative margin when `cost > revenue`;
  `MarginSummary.Empty` is all-zero.
- **Approach:** `sealed record` DTOs mirroring `PlatformAnalyticsDtos`: `MarginSummary`,
  `MarginBreakdownRow(string Dimension, string? Key, decimal Cost, decimal Revenue, decimal
  Margin, decimal MarginPct)`, `MarginTrendPoint(DateTime Bucket, …)`, `TenantMarginReport`,
  `PlatformMarginReport`, `TenantMarginRow`. `MarginSummary.From` is the **only** arithmetic in
  the whole story (sum/subtract/divide-guard). Document the read-only contract in XML comments.

### Phase 2 — `MarginAnalyticsService` (tenant-scope, pure read) + no-recompute guard

- **Files:** `Tamma.Api/Services/Analytics/IMarginAnalyticsService.cs`,
  `MarginAnalyticsService.cs` (new).
- **Tests first (same `MarginAnalyticsServiceTests`):**
  - summation `Σ PlatformBilledUsd − Σ CostUsd` over seeded `AnalyticsUsageDaily` rows;
  - per-dimension breakdown (provider / agent / workflow / cost-basis) each with its own
    cost/revenue/margin; `NULL`-dimension bucket preserved; `Σ(breakdown) == grand total`;
  - **BYOK row → `Revenue == 0`, `Margin == −Cost`, `Pct == 0`** read from stored data;
  - daily/monthly trend lossless sum;
  - empty tenant → `MarginSummary.Empty`, empty breakdowns/trend, never null/throw;
  - **AC2 no-recompute dependency test** — assert (reflection over the assembly's referenced
    symbols, or an ArchUnitNET/manual-type-scan test) that the margin-view types reference **no**
    `IUsagePricingEngine` / `IMarginPolicyResolver` / `ProviderPricingService` / `MarginPolicy`
    symbol.
- **Approach:** inject `ITenantDbContextFactory`; `GetTenantMarginAsync(tenantId, from, to, grain,
  ct)` reads `AnalyticsUsageDaily` for whole-day windows (else `AnalyticsUsageHourly`), `GROUP BY`
  each dimension, sums `CostUsd`/`PlatformBilledUsd`, derives margin via `MarginSummary.From`. Pure
  read — no pricing namespace import (the import is the thing the AC2 test forbids). InMemory
  provider for these unit tests.

### Phase 3 — Platform-aggregate read (owner-only, fan-out)

- **Files:** `Tamma.Api/Services/Analytics/IPlatformAnalyticsService.cs` (+ `GetPlatformMarginAsync`),
  `PlatformAnalyticsService.cs` (impl), `PlatformAnalyticsDtos.cs` (+ `PlatformMarginReport` /
  `TenantMarginRow`).
- **Tests first:** `tests/Tamma.Api.Tests/Analytics/MarginAnalyticsIsolationTests.cs` (Postgres 17
  Testcontainer, per `SchemaPerTenantMigrationTests`):
  - seed fact rows in two tenant schemas; tenant A's margin reflects only A (isolation);
  - `GetPlatformMarginAsync` sums both into the fleet aggregate + top-N-by-margin + trend;
  - force a read failure on tenant A → A skipped (counted), B still contributes, aggregate
    completes (28-10 AC5 tolerance shape);
  - zero active tenants → zeroed `PlatformMarginReport`.
- **Approach:** iterate the active-tenant set (the 28-10 fan-out target — reuse
  `PlatformAnalyticsService`'s existing tenant enumeration), call `MarginAnalyticsService`
  per tenant, sum summaries, build top-N + fleet trend. Per-tenant try/catch → log WARN + skip +
  increment `tenantsSkipped`; never abort. **Revenue is the per-tenant `PlatformBilledUsd` sum** —
  the CP `platform_analytics_hourly` has no revenue column (Phase-0 finding).

### Phase 4 — Endpoints + RBAC + DCB emit + DI

- **Files:** `Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs` (+ `GetMargin`),
  `Tamma.Api/Endpoints/Analytics/MarginAnalyticsEndpoints.cs` (new tenant handler),
  `Program.cs` (map both routes + DI), `Extensions/AnalyticsServiceCollectionExtensions.cs`
  (register `IMarginAnalyticsService` scoped — create the extension if analytics DI is inline today).
- **Tests first:** `tests/Tamma.Api.Tests/Analytics/MarginAnalyticsEndpointsTests.cs` —
  member → `GET /api/v1/orgs/{tenantId}/analytics/margin` 200; member →
  `GET /api/admin/analytics/margin` 403; cross-tenant `{tenantId}` → 404; owner → both 200;
  single-user → both 200 against the one tenant; empty window → zeroed 200; assert exactly one
  `ANALYTICS.MARGIN.VIEWED` event per query (captured `IEventRepository`), and that emit failure
  does not fail the request.
- **Approach:**
  - Admin: `admin.MapGet("/analytics/margin", AdminAnalyticsEndpoints.GetMargin)
    .RequireAuthorization("OwnerAccess")` beside the existing `/analytics/*` maps (Program.cs:1346).
    `GetMargin` injects `IPlatformAnalyticsService`, parses `from`/`to`/`grain`/`limit` (UTC-force
    like `GetEventHistogram`), returns `PlatformMarginReport`.
  - Tenant: map on the `/api/v1/orgs` `MemberAccess` group; resolve tenant from
    `ITenantContext.TenantId`; if the route `{tenantId}` ≠ context tenant → 404 (cross-tenant
    guard, the org-scoped precedent). Inject `IMarginAnalyticsService`.
  - DCB: best-effort `ANALYTICS.MARGIN.VIEWED` via `IEventRepository.AppendAsync` in try/catch+log
    (PostgresAlertSink pattern). No `PRICING.*` event.

### Phase 5 — Wire-up, quality gates, boundary doc

- [ ] `dotnet build` clean; `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests"` green.
- [ ] Logging per the story's Logging Requirements (INFO/DEBUG/WARN/ERROR; no connection strings,
      no provider keys — the view never touches them).
- [ ] Confirm the AC2 no-recompute test is in the suite and passing (the boundary's CI guard).
- [ ] No migration in this story (read-only over 36-1's tables) — verify `has-pending-model-changes`
      stays clean for both contexts (no accidental entity edit).
- [ ] Update the Epic 36 consumer notes so downstream margin export/report/dashboard stories
      reference `IMarginAnalyticsService` / `GetPlatformMarginAsync` rather than re-summing facts.

## Sequencing & dependencies

Phase 0 (prereq gate) → Phase 1 (DTOs) → Phase 2 (tenant read + no-recompute guard) → Phase 3
(platform aggregate, needs Phase 2) → Phase 4 (endpoints, needs 2+3) → Phase 5 (gates). Phases 1
and the AC2 guard can land ahead of real 36-2 data (AC11 zeroed result). Hard prerequisites:
Story 36-1 (fact-table entities — must compile against them) and, for meaningful numbers, Story
36-2 (population). Source-of-truth dependency (consumed, never invoked): Story 34-5
(`IUsagePricingEngine` produced `PlatformBilledUsd`) and Story 32-9 (usage emission).

## Risks + mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| Reporting path recomputes margin → drifts from 34-5 | High | The view only sums two persisted columns; AC2 no-recompute dependency test is an executable CI guard; boundary doc in service comment + non-goals. |
| Revenue read from the wrong table (CP `platform_analytics_hourly` has no `PlatformBilledUsd`) | High | Phase-0 finding pins it; platform aggregate sums per-tenant `analytics_usage_*` revenue via fan-out; swap source behind `IPlatformAnalyticsService` if a CP revenue column ever lands. |
| Cross-tenant margin leak | High | Tenant route hard-scoped to `ITenantContext.TenantId` (cross-tenant → 404); admin aggregate `OwnerAccess`; tenant roles 403 on `/api/admin/*`; isolation + RBAC tests. |
| Platform fan-out aborts on one tenant's read error | Medium | Per-tenant try/catch → log + skip + count (28-10 tolerance shape); fan-out test forces a tenant failure and asserts the aggregate still completes. |
| Pre-projection window returns null/throws | Medium | `MarginSummary.Empty` zeroed result (mirrors `TenantResourceSummary.Empty`); empty-tenant + empty-window tests. |
| `MarginPct` divide-by-zero | Low | `revenue == 0 ? 0 : (revenue−cost)/revenue` in `MarginSummary.From`; unit-tested. |
| Spec primary-components mislead the implementer toward the markup engine | Medium | Story + plan explicitly call out that `PricingConfigService`/`AdminPricingEndpoints`/`Plan`/`AgentConfig` are 34-5's, NOT 36-7's; the Files table lists only `Services/Analytics/*` view files. |
| Targeting deleted `packages/api` | Low | All targets are `apps/tamma-elsa`; `packages/api` is DELETED and never referenced. |

## Acceptance criteria (mirror of the story)

- [ ] `IMarginAnalyticsService` reads `analytics_usage_daily`/`_hourly` via `ITenantDbContextFactory`
      and returns `CostUsd` / `PlatformBilledUsd` / `MarginUsd = revenue−cost` / `MarginPct` for a
      `[from,to]` window — pure summation, no pricing recompute.
- [ ] AC2 boundary: margin-view types take **no** dependency on the 34-5 pricing namespace
      (executable test).
- [ ] Breakdowns by provider / agent / workflow / cost-basis, each with own cost/revenue/margin;
      `NULL` bucket preserved; `Σ(breakdown) == grand total`.
- [ ] BYOK rows surface `Revenue == 0`, `Margin == −Cost`, `Pct == 0` read from stored data.
- [ ] Daily (and monthly) trend, lossless to the window total.
- [ ] `GET /api/v1/orgs/{tenantId}/analytics/margin` (`MemberAccess`) — tenant-scoped, cross-tenant
      → 404.
- [ ] `GET /api/admin/analytics/margin` (`OwnerAccess`) — fleet aggregate via per-tenant fan-out;
      one-tenant failure skipped; tenant/member → 403.
- [ ] Per-mode ownership answered (single-user sole user = both surfaces; SaaS tenant = own margin,
      owner = aggregate); raw margin multiplier never exposed.
- [ ] Empty/pre-projection → zeroed result, never null/throw.
- [ ] `ANALYTICS.MARGIN.VIEWED` best-effort DCB emit per query; no `PRICING.*` event here.
- [ ] Unit + integration tests (math/breakdown/BYOK/trend/isolation/fan-out/RBAC/empty/no-recompute)
      green; build clean; no pending model changes.
