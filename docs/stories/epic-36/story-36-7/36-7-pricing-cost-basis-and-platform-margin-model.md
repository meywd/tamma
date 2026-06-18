# Story 36-7: Cost-Basis vs Margin Analytics View (Per-Tenant Gross Margin + Platform-Aggregate Margin)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase workflow (Read → Research → Break Down → TDD →
Quality Gates → Failure Handling), the `.dev/` knowledge base, TRACE/DEBUG logging
requirements, test-first development, and the build-success / coverage gates.

## ⚠️ Boundary — this story is an analytics VIEW, NOT the markup engine

This story is the **read-side analytics view** of cost-basis vs margin. It **reads** the
already-computed pricing outputs of **Story 34-5 (Cost→Price Markup Engine)** as they were
persisted onto the analytics fact tables by the **Story 36-2 projection pipeline** (the
`CostUsd` cost basis and the `PlatformBilledUsd` sell price on `AnalyticsUsageHourly`/
`AnalyticsUsageDaily`), and the per-call usage emitted by **Story 32-9 (cost-basis + margin
metering)**. It presents per-tenant gross-margin and a platform-level margin aggregate
(owner-only).

**It MUST NOT recompute pricing.** The margin multiplier, the cost-basis rate sheet, the
`cost × (1 + margin)` arithmetic, and the BYOK zero-markup rule all live in **34-5**
(`IUsagePricingEngine` / `IMarginPolicyResolver` / `ProviderPricingService`) and are applied
once, at projection time, by **36-2**. This story reads `PlatformBilledUsd` (revenue) and
`CostUsd` (cost) off the fact rows and reports `revenue − cost` as margin. If this story ever
multiplies a cost by a margin, classifies BYOK vs platform from a secret cabinet, or reads a
margin policy row, it has crossed the boundary — that is a 34-5/36-2 change, not 36-7.

## User Story

As a **self-hosted owner** (single-user) **/ tenant administrator** (SaaS), and — for the
fleet-wide view — as the **platform owner**,
I want a profitability view that reads my already-priced usage facts and shows cost basis,
billed revenue, and the resulting gross margin per tenant (and, for the platform owner only, a
single platform-wide margin aggregate), broken down by provider / agent / workflow / cost-basis
and trended over time,
so that I can see where Tamma is making or losing money on platform-provided usage and confirm
that BYOK usage carries zero token markup — without any pricing being recomputed in the
reporting path.

## Priority

P0 - Profitability is the business question Epic 36's owner-side analytics exists to answer; the
per-tenant gross-margin view is the tenant-facing counterpart that proves the BYOK/platform
split is billing as designed.

## Scope

Read-model + query service + endpoints **only**. This story:

- adds an `IMarginAnalyticsService` / `MarginAnalyticsService` that **reads** the Story 36-1
  per-tenant fact tables (`analytics_usage_daily` / `analytics_usage_hourly`) through the
  tenant's `TenantDbContext` and computes a **margin view** = `Σ PlatformBilledUsd` (revenue)
  − `Σ CostUsd` (cost) per dimension and per period — **no markup math**;
- exposes a **tenant-scoped** gross-margin endpoint (`MemberAccess`) returning cost / revenue /
  margin / margin-% with provider·agent·workflow·cost-basis breakdowns and a daily/monthly
  trend, scoped to exactly one tenant schema;
- exposes a **platform-owner-only** margin-aggregate endpoint (`OwnerAccess` /
  `PlatformOwnerAccess`) that fans the per-tenant `PlatformBilledUsd` − `CostUsd` figures into a
  single fleet-wide profitability summary + trend, modelled on `AdminAnalyticsEndpoints` /
  `PlatformAnalyticsService`.

**Out of scope (owned elsewhere — do not touch):** the markup engine and margin policy
(Story 34-5), the per-call usage emission + cost-basis classification (Story 32-9 / 35-2), the
fact-table schema (Story 36-1), the projection that populates `CostUsd`/`PlatformBilledUsd`
(Story 36-2), exports / scheduled reports (later Epic 36 stories), and the dashboard surface
(a later Epic 36 story — this story ships the read API the dashboard will call).

## Acceptance Criteria

1. A new tenant-scoped read service `IMarginAnalyticsService` /
   `MarginAnalyticsService`
   (`apps/tamma-elsa/src/Tamma.Api/Services/Analytics/IMarginAnalyticsService.cs` +
   `MarginAnalyticsService.cs`) computes, for the caller's tenant over a `[from, to]` UTC
   window, a **margin view**: `CostUsd` (cost basis, summed from the fact rows),
   `PlatformBilledUsd` (billed revenue, summed from the fact rows), `MarginUsd =
   PlatformBilledUsd − CostUsd`, and `MarginPct = MarginUsd / PlatformBilledUsd` (0 when
   revenue is 0). It reads `AnalyticsUsageDaily` for windows spanning whole days and
   `AnalyticsUsageHourly` for sub-day windows, through the tenant's `TenantDbContext` resolved
   via `ITenantDbContextFactory` — **the same tables Story 36-2 populates**.

2. **No pricing is recomputed.** The service performs only summation and subtraction over the
   already-persisted `CostUsd` and `PlatformBilledUsd` measures. It does **not** reference
   `IUsagePricingEngine`, `IMarginPolicyResolver`, `ProviderPricingService`, any margin
   multiplier, or any cost rate sheet. A code-level test (or analyzer assertion) confirms the
   `Tamma.Api.Services.Analytics` margin-view types take **no** dependency on the 34-5 pricing
   namespace. The boundary is documented in the service's XML doc-comment, citing 34-5 as the
   owner of `PlatformBilledUsd` and 36-2 as the producer.

3. The service returns dimension breakdowns — **by provider, by agent, by workflow definition,
   and by cost-basis (`byok` / `platform`)** — each row carrying its own
   `Cost / Revenue / Margin / MarginPct`, with a `NULL`-dimension ("unattributed") bucket
   preserved so `Σ(breakdown rows) == grand total` (mirrors the Story 36-2 reconciliation
   guarantee; the view never coerces a `NULL` dimension to a sentinel and never drops it).

4. **BYOK rows surface a zero-revenue, zero-margin line.** Because Story 34-5/36-2 persist
   `PlatformBilledUsd = 0` for every `CostBasis = byok` row (Tamma never marks up a BYOK call),
   the `byok` cost-basis bucket reports `Revenue = 0`, `Margin = −Cost` (the BYOK platform/seat
   fee is billed by Epic 35, not metered here as token revenue), and `MarginPct = 0`. The view
   asserts this directly from the stored data — it does **not** re-derive the zero; a unit test
   pins that a `byok` fact row yields `Revenue == 0` in the breakdown.

5. A **trend** series is returned: a daily (and, on request, monthly) time series of
   `Cost / Revenue / Margin / MarginPct` over the window, read from `AnalyticsUsageDaily`
   (daily grain) so the tenant can chart profitability over time without re-scanning events.
   The trend buckets sum losslessly to the window grand total (AC1).

6. A **tenant-scoped** endpoint `GET /api/v1/orgs/{tenantId}/analytics/margin`
   (`MemberAccess`) returns the tenant's cost/revenue/margin summary + breakdowns + trend for a
   `from`/`to` (and optional `grain=day|month`) query. It resolves the tenant strictly from
   `ITenantContext.TenantId` (SaaS) or the sole tenant (single-user) — a member of tenant A can
   never read tenant B's margin (cross-tenant request → 404, mirroring the existing org-scoped
   endpoint precedent); the per-tenant `TenantDbContext` enforces isolation physically.

7. A **platform-owner-only** aggregate is exposed via a new method on the platform analytics
   surface (`IPlatformAnalyticsService.GetPlatformMarginAsync(...)` +
   `PlatformAnalyticsService`) and endpoint `GET /api/admin/analytics/margin`
   (`OwnerAccess`), returning one **fleet-wide** profitability summary — total `CostUsd`, total
   `PlatformBilledUsd`, total `MarginUsd`, blended `MarginPct` — plus a top-N
   tenants-by-margin list and a fleet trend. This is **owner-only**: tenants and members cannot
   call it (403) and never see another tenant's or the platform's aggregate margin.

8. The platform aggregate is composed by **summing the per-tenant `PlatformBilledUsd` and
   `CostUsd` fact figures across active tenants** (the CP `platform_analytics_hourly` table from
   Story 28-10 carries `CostUsd` but **not** `PlatformBilledUsd`, so revenue must come from the
   per-tenant `analytics_usage_*` store — verified). The fan-out mirrors
   `FanOutTenantRollupsActivity` / `PlatformAnalyticsService.GetTenantResourceSummaryAsync`
   tenant-iteration shape: a single tenant's read failure is caught, logged, excluded from the
   aggregate with a counted skip, and never aborts the fleet view.

9. **RBAC, per-mode.** Tenant-scope margin (cost/revenue/margin for one tenant) is readable by
   any tenant member (`MemberAccess`) in SaaS and by the sole user in single-user; the
   platform-aggregate margin is **`OwnerAccess` only** in both modes. The raw margin multiplier
   and cost rate sheet are never exposed by this story at all — tenants see resulting billed
   amounts and the derived margin, never the policy that produced them (that policy lives in
   34-5 behind `PlatformOwnerAccess`). A test matrix asserts: member → tenant margin OK,
   member → `/api/admin/analytics/margin` 403, cross-tenant → 404, owner → both OK.

10. **Per-mode ownership is answered explicitly** (per CLAUDE.md "two scoping models"): in
    single-user mode the sole user owns the one tenant schema's margin view and *is* the
    platform owner (sees both surfaces against their single tenant); in SaaS mode tenant
    admins/members see only their tenant's margin and the platform owner alone sees the
    cross-tenant aggregate. The endpoint shape is identical across modes; auth middleware +
    `ITenantContext` decide scope (prompt-store API precedent).

11. **Empty / pre-projection safety.** A tenant with no projected fact rows yet (fresh tenant,
    or 36-2 has not run for the window) returns a zeroed summary (`Cost = Revenue = Margin = 0`,
    `MarginPct = 0`, empty breakdowns/trend) — never null, never throwing — mirroring
    `TenantResourceSummary.Empty`. The platform aggregate over zero active tenants returns the
    same zeroed shape.

12. The view emits a lightweight **read-audit** DCB event `ANALYTICS.MARGIN.VIEWED`
    (`AGGREGATE.ACTION.STATUS` convention) on each margin query, tagged
    `{ scope: tenant|platform, tenantId?, from, to, grain }`, appended best-effort via the
    existing `IEventRepository.AppendAsync` try/catch+log pattern (`PostgresAlertSink`
    precedent) so the query path never fails on an emit error. **No `PRICING.*` event is emitted
    by this story** — pricing-config audit (`PRICING.MARGIN.UPDATED`) is owned by 34-5; this
    story only records that a margin *view* was read.

13. Unit + integration tests cover: cost/revenue/margin summation and `MarginPct` (incl.
    zero-revenue → 0); per-dimension breakdown reconciliation to the grand total (incl. the
    `NULL` bucket); BYOK row → zero revenue / negative-of-cost margin read from stored data;
    daily/monthly trend lossless sum; tenant isolation (tenant A's margin excludes tenant B's
    rows — Postgres 17 Testcontainer, per `SchemaPerTenantMigrationTests`); platform aggregate
    fan-out (sum across tenants; one tenant failure skipped, fleet view intact); RBAC matrix
    (member/owner/cross-tenant); empty-tenant zeroed result; and the **no-recompute boundary**
    assertion (AC2).

## Tasks / Subtasks

- [ ] Task 1: Margin-view DTOs + reconciliation contract (AC: 1, 3, 5, 11)
  - [ ] Add `MarginAnalyticsDtos.cs` records: `MarginSummary(CostUsd, RevenueUsd, MarginUsd,
        MarginPct, GeneratedAt)`, `MarginBreakdownRow(Dimension, Key?, Cost, Revenue, Margin,
        MarginPct)`, `MarginTrendPoint(Bucket, Cost, Revenue, Margin, MarginPct)`,
        `TenantMarginReport(Summary, Breakdowns, Trend)`, `PlatformMarginReport(Summary,
        TopTenantsByMargin, Trend)`. `MarginSummary.Empty` zeroed constant (AC11).
  - [ ] Document each record's "revenue = persisted `PlatformBilledUsd`, cost = persisted
        `CostUsd`, margin = revenue − cost; nothing is recomputed" contract in XML doc-comments.

- [ ] Task 2: `IMarginAnalyticsService` / `MarginAnalyticsService` (AC: 1, 2, 3, 4, 5, 11)
  - [ ] Read `AnalyticsUsageDaily` (whole-day windows) / `AnalyticsUsageHourly` (sub-day) via
        `ITenantDbContextFactory`; `GROUP BY` dimension; `Σ CostUsd` / `Σ PlatformBilledUsd`;
        derive `MarginUsd` / `MarginPct` (guard divide-by-zero).
  - [ ] `NULL`-dimension bucket preserved; breakdown rows reconcile to summary.
  - [ ] Zero deps on the 34-5 pricing namespace (assertable — AC2).

- [ ] Task 3: Platform-aggregate read (AC: 7, 8, 11)
  - [ ] Add `GetPlatformMarginAsync(from, to, grain, topN, ct)` to `IPlatformAnalyticsService`
        + `PlatformAnalyticsService`; fan across active tenants (28-10 fan-out shape), sum
        per-tenant `PlatformBilledUsd`/`CostUsd`; per-tenant read failure caught + skipped +
        counted; top-N tenants by margin; fleet trend.

- [ ] Task 4: Endpoints + RBAC (AC: 6, 7, 9, 10, 12)
  - [ ] Tenant: `GET /api/v1/orgs/{tenantId}/analytics/margin` (`MemberAccess`) →
        `MarginAnalyticsService`; resolve tenant from `ITenantContext`; cross-tenant 404.
  - [ ] Admin: `GET /api/admin/analytics/margin` (`OwnerAccess`) → platform aggregate; map on
        the existing `admin` group beside the other `/analytics/*` endpoints in `Program.cs`.
  - [ ] Best-effort `ANALYTICS.MARGIN.VIEWED` DCB emit per query.
  - [ ] DI: register `IMarginAnalyticsService` (scoped) in the analytics service-collection
        extension.

- [ ] Task 5: Tests (AC: 13)
  - [ ] Unit (InMemory): summation/MarginPct/zero-revenue, breakdown reconciliation, BYOK
        zero-revenue, trend lossless, empty-tenant, no-recompute dependency assertion.
  - [ ] Integration (Postgres 17 Testcontainer): tenant isolation (A excludes B), platform
        fan-out aggregate + one-tenant-failure tolerance, RBAC matrix.

## Technical Design

### Read-model boundary (the load-bearing decision)

```
Story 34-5  ── IUsagePricingEngine ─────────────►  (markup math: cost × margin → sell price; BYOK → 0)
   │                                                 OWNS the margin policy + rate sheet
   ▼  (sell price applied once, at projection time)
Story 36-2  ── ComputeTenantDimensionalRollupActivity
   │              writes per fact row:  CostUsd  (= 34-5 cost basis)
   │                                    PlatformBilledUsd (= 34-5 sell price; 0 for BYOK)
   ▼
Story 36-1  ── analytics_usage_hourly / analytics_usage_daily  (per-tenant schema)
   │              dims: Provider, AgentId, WorkflowDefinitionId, RepoId, CostBasis(byok|platform)
   ▼
Story 36-7 (THIS) ── MarginAnalyticsService  =  Σ PlatformBilledUsd − Σ CostUsd
                       PURE READ. No multiply. No policy lookup. No cabinet read.
```

The whole story is summation + subtraction over two already-priced columns. The moment the
reporting path multiplies a cost by a margin, it has duplicated 34-5 and will drift — the AC2
no-recompute assertion exists to catch that in CI.

### C# namespace / file structure

```
apps/tamma-elsa/src/Tamma.Api/Services/Analytics/
  IMarginAnalyticsService.cs                 # NEW — tenant-scoped margin read port
  MarginAnalyticsService.cs                  # NEW — Σ revenue − Σ cost over the fact tables
  MarginAnalyticsDtos.cs                     # NEW — summary / breakdown / trend records
  IPlatformAnalyticsService.cs               # MODIFY — + GetPlatformMarginAsync(...)
  PlatformAnalyticsService.cs                # MODIFY — fleet-wide margin via per-tenant fan-out
  PlatformAnalyticsDtos.cs                   # MODIFY — + PlatformMarginReport / TenantMarginRow

apps/tamma-elsa/src/Tamma.Api/Endpoints/
  AdminAnalyticsEndpoints.cs                 # MODIFY — + GetMargin (OwnerAccess, platform aggregate)
  Analytics/MarginAnalyticsEndpoints.cs      # NEW — tenant GET /api/v1/orgs/{tenantId}/analytics/margin

apps/tamma-elsa/src/Tamma.Api/
  Program.cs                                 # MODIFY — map both routes; DI registration
  Extensions/<AnalyticsServiceCollectionExtensions>.cs  # MODIFY/NEW — register IMarginAnalyticsService

apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/
  MarginAnalyticsServiceTests.cs             # NEW — summation / breakdown / BYOK / trend / no-recompute
  MarginAnalyticsIsolationTests.cs           # NEW — Postgres 17: tenant isolation + platform fan-out
  MarginAnalyticsEndpointsTests.cs           # NEW — RBAC matrix + cross-tenant 404 + empty
```

> **Note on the spec's `PricingConfigService` / `AdminPricingEndpoints` / `Plan` / `AgentConfig`
> primary-components list:** those are the **34-5** markup-engine components
> (`AdminPricingEndpoints`, `MarginPolicy`, the `Plan` price book), not 36-7's. This story
> deliberately does **not** create or edit them — per the boundaryNote, 36-7 is the analytics
> *view* of what 34-5 produced. The `Tamma.Api/Services/Analytics/*` files above are 36-7's.

### Margin read (tenant scope — pure summation)

```csharp
// MarginAnalyticsService — NO pricing recompute. Reads the columns 34-5/36-2 already wrote.
public async Task<TenantMarginReport> GetTenantMarginAsync(
    Guid tenantId, DateTime fromUtc, DateTime toUtc, MarginGrain grain, CancellationToken ct)
{
    await using var db = await _tenantDbFactory.CreateAsync(tenantId, ct);

    // Daily grain for whole-day windows (cheap); hourly only for sub-day windows.
    var rows = await db.AnalyticsUsageDaily
        .Where(r => r.Day >= fromUtc && r.Day < toUtc)
        .ToListAsync(ct);

    decimal cost    = rows.Sum(r => r.CostUsd);
    decimal revenue = rows.Sum(r => r.PlatformBilledUsd);
    var summary = MarginSummary.From(cost, revenue);   // Margin = revenue - cost; Pct guards /0

    var byCostBasis = rows
        .GroupBy(r => r.CostBasis)                       // byok rows already carry PlatformBilledUsd == 0
        .Select(g => MarginBreakdownRow.From("costBasis", g.Key.ToString(),
            g.Sum(x => x.CostUsd), g.Sum(x => x.PlatformBilledUsd)))
        .ToList();
    // …same GROUP BY for Provider / AgentId (NULL bucket kept) / WorkflowDefinitionId…

    var trend = rows
        .GroupBy(r => r.Day)
        .OrderBy(g => g.Key)
        .Select(g => MarginTrendPoint.From(g.Key,
            g.Sum(x => x.CostUsd), g.Sum(x => x.PlatformBilledUsd)))
        .ToList();

    return new TenantMarginReport(summary, breakdowns: Combine(byCostBasis, …), trend);
}

// MarginSummary.From — the ONLY arithmetic in this story.
public static MarginSummary From(decimal cost, decimal revenue) => new(
    CostUsd: cost,
    RevenueUsd: revenue,
    MarginUsd: revenue - cost,
    MarginPct: revenue == 0m ? 0m : Math.Round((revenue - cost) / revenue, 4, MidpointRounding.AwayFromZero),
    GeneratedAt: DateTime.UtcNow);
```

### Platform aggregate (owner-only — fan across tenants)

`platform_analytics_hourly` (Story 28-10, CP) carries `CostUsd` but **no `PlatformBilledUsd`**
(verified — see `Tamma.Data/Entities/PlatformAnalyticsHourly.cs`). Revenue therefore comes from
the per-tenant `analytics_usage_*` store. `GetPlatformMarginAsync` iterates active tenants (the
28-10 fan-out target set), reads each tenant's `MarginSummary` via `MarginAnalyticsService`,
sums them, and builds the fleet summary + top-N-by-margin + trend — mirroring
`PlatformAnalyticsService.GetTenantResourceSummaryAsync` / `FanOutTenantRollupsActivity`
tenant-iteration and per-tenant fault tolerance (one tenant's read failure is caught, logged,
skipped with a counted skip, and never aborts the aggregate).

### Endpoints

```
GET /api/v1/orgs/{tenantId}/analytics/margin?from=ISO&to=ISO&grain=day|month   (MemberAccess)
    → TenantMarginReport   (tenant resolved from ITenantContext; cross-tenant → 404)

GET /api/admin/analytics/margin?from=ISO&to=ISO&grain=day|month&limit=N         (OwnerAccess)
    → PlatformMarginReport (fleet aggregate; tenant/member → 403)
```

The admin route is added on the existing `admin` group beside
`admin.MapGet("/analytics/summary", AdminAnalyticsEndpoints.GetSummary)` in `Program.cs`
(verified the group + `OwnerAccess` wiring already exist). The tenant route mounts on the
existing `/api/v1/orgs` `MemberAccess` group (verified).

### DCB events

| Event | When | Tags |
|---|---|---|
| `ANALYTICS.MARGIN.VIEWED` | per margin query (tenant or platform) | `scope` (`tenant`/`platform`), `tenantId?`, `from`, `to`, `grain` |

Appended best-effort via `IEventRepository.AppendAsync` inside try/catch+log (the
`PostgresAlertSink` emit pattern) — a margin read never fails on an emit error. **No
`PRICING.*` event is emitted here** (that family is 34-5's pricing-config audit).

### Per-mode + per-tenant handling

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who reads a tenant's margin view? | The sole user (their one tenant schema; no RBAC). | Any tenant member (`MemberAccess`); member is read-only — margin is a read. |
| Who reads the platform aggregate? | The sole user (they are the platform owner; aggregate = their single tenant). | Platform owner only (`OwnerAccess`); tenants/members 403. |
| Where does the data come from? | The sole tenant's `analytics_usage_*` (36-2 populated). | Each tenant's own `t_<hex>` `analytics_usage_*`; aggregate fans across all active tenants. |
| Is the margin policy ever exposed? | No — only resulting cost/revenue/margin (policy lives in 34-5). | No — same; tenants never see the multiplier. |
| Isolation plane | Search-path schema + connection string. | Same — physically separate schema per tenant; a row in A is unreachable from B. |

Mode does not change the read shape — both resolve to exactly one tenant schema per tenant-scope
request; only the platform-aggregate fan-out target set differs (one tenant vs all active
tenants).

### Integration points

- **Story 36-1 (`analytics_usage_hourly`/`_daily`, `CostBasis` enum)** — the fact tables and the
  `byok|platform` discriminator this view reads. (Drafted.)
- **Story 36-2 (projection)** — populates `CostUsd` + `PlatformBilledUsd`; this view is inert
  until 36-2 has run for the window (AC11 zeroed-result handles the gap). (Drafted.)
- **Story 34-5 (`IUsagePricingEngine`)** — the markup engine that *produced* `PlatformBilledUsd`
  upstream. Referenced as the source of truth for the figures; **not called** by this story.
  (Plan written; engine NEW — `IUsagePricingEngine`/`MarginPolicy` confirmed absent today.)
- **Story 32-9** — emits the per-call cost-basis + margin usage line 36-2 folds in. (Plan
  written.)
- **Story 28-10 (`PlatformAnalyticsService` / `AdminAnalyticsEndpoints` / `IEventRepository`)**
  — the owner-side analytics surface, fan-out shape, and DCB emit pattern this story extends.
  (Merged.)

## Dependencies

**Prerequisite (internal):**
- **Story 36-1** — per-tenant `AnalyticsUsageHourly`/`AnalyticsUsageDaily` + `CostBasis` enum
  (the columns this view reads). Drafted.
- **Story 36-2** — the projection that writes `CostUsd` + `PlatformBilledUsd` onto those rows.
  Drafted. (Until it runs, AC11 returns a zeroed view.)
- **Epic 28** — per-tenant schema + `ITenantDbContextFactory` + the 28-10
  `PlatformAnalyticsService`/`AdminAnalyticsEndpoints` surface this extends. Merged.

**Source-of-truth (read-only — this story consumes their *outputs*, never their math):**
- **Story 34-5 (Cost→Price Markup Engine)** — owns `IUsagePricingEngine` / `IMarginPolicyResolver`
  / `ProviderPricingService` and the `cost × (1 + margin)` / BYOK-zero-markup arithmetic that
  produced `PlatformBilledUsd`. **Not invoked here.** Plan written; entities NEW.
- **Story 32-9** — the cost-basis + margin metering producer that emits the per-call usage line.

**Blocks (internal):**
- Later Epic 36 stories — margin exports, scheduled profitability reports, and the dashboard
  margin/profitability surface — all read this service's API.

**External:**
- PostgreSQL 17 (per-tenant schema isolation; the integration suite uses a Testcontainer).
- EF Core 9 / Npgsql.
- Testcontainers + Docker for the isolation + fan-out integration suite (run via
  `sg docker -c "dotnet test ..."`).

## Testing Strategy

1. **Unit — margin math (`MarginAnalyticsServiceTests`, InMemory):** `Σ PlatformBilledUsd −
   Σ CostUsd`; `MarginPct` (incl. zero-revenue → 0, negative margin when cost > revenue);
   rounding determinism (4dp).

2. **Unit — breakdown reconciliation:** rows spanning multiple providers/agents/workflows/
   cost-bases produce one breakdown row per key with its own cost/revenue/margin;
   `NULL`-dimension bucket preserved; `Σ(breakdown rows) == grand total`.

3. **Unit — BYOK zero revenue:** a fact row with `CostBasis = byok` (and the
   `PlatformBilledUsd = 0` 36-2 persists) yields `Revenue == 0`, `Margin == −Cost`,
   `MarginPct == 0` — read from stored data, not re-derived.

4. **Unit — trend lossless:** daily/monthly trend points sum to the window grand total; monthly
   grain rolls daily rows up correctly.

5. **Unit — empty tenant:** no fact rows → `MarginSummary.Empty` (all zero), empty
   breakdowns/trend, never null/throwing.

6. **Unit — no-recompute boundary (AC2):** assert the margin-view types reference **no** symbol
   from the 34-5 pricing namespace (`IUsagePricingEngine` / `IMarginPolicyResolver` /
   `ProviderPricingService` / `MarginPolicy`) — reflection-over-assembly-references or a
   dependency test; documents the boundary as an executable guard.

7. **Integration — tenant isolation (`MarginAnalyticsIsolationTests`, Postgres 17
   Testcontainer, per `SchemaPerTenantMigrationTests`):** seed fact rows into two tenant schemas;
   tenant A's margin reflects only A's rows; B's rows are invisible to A.

8. **Integration — platform fan-out:** seed two tenants; `GetPlatformMarginAsync` sums both into
   the fleet aggregate + top-N-by-margin; force a read failure on tenant A → A is skipped (counted),
   tenant B still contributes, the aggregate completes.

9. **Endpoint — RBAC matrix (`MarginAnalyticsEndpointsTests`):** member → tenant margin 200;
   member → `/api/admin/analytics/margin` 403; cross-tenant `{tenantId}` → 404; owner → both
   200; single-user → both 200 against the one tenant; empty window → zeroed 200.

**Mocks:** No Stripe / external provider calls (read-only aggregation). InMemory provider for
math/breakdown/trend shape; a real Postgres 17 Testcontainer for per-tenant isolation and the
fan-out aggregate (EF InMemory does not model search-path schema isolation — same rationale as
`ConventionStoreMigrationTests` / `SchemaPerTenantMigrationTests`).

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/IMarginAnalyticsService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/MarginAnalyticsService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/MarginAnalyticsDtos.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/IPlatformAnalyticsService.cs` | Modify (add `GetPlatformMarginAsync`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/PlatformAnalyticsService.cs` | Modify (fleet margin via per-tenant fan-out) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/PlatformAnalyticsDtos.cs` | Modify (add `PlatformMarginReport` / `TenantMarginRow`) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs` | Modify (add `GetMargin`, OwnerAccess) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Analytics/MarginAnalyticsEndpoints.cs` | Create (tenant `GET /api/v1/orgs/{tenantId}/analytics/margin`) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map both routes; DI) |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/AnalyticsServiceCollectionExtensions.cs` | Modify/Create (register `IMarginAnalyticsService`) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/MarginAnalyticsServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/MarginAnalyticsIsolationTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/MarginAnalyticsEndpointsTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions (analytics, margin, tenancy,
   event sourcing).
3. Reviewed Story 36-1 (the fact tables + `CostBasis` enum + `PlatformBilledUsd` column),
   Story 36-2 (which populates `CostUsd`/`PlatformBilledUsd`), and Story 34-5 (the markup
   engine whose outputs this view reads — to understand the boundary you must not cross).
4. Reviewed `PlatformAnalyticsService` / `AdminAnalyticsEndpoints` / `PlatformAnalyticsDtos`
   (Story 28-10) — this story extends that exact surface and fan-out/tolerance shape.
5. Confirmed the test runner: docker-bound suites run via `sg docker -c "dotnet test ..."`
   (session docker group is stale; the build itself needs no wrapper).
6. Planned the TDD cycle (write the margin-math + reconciliation + no-recompute tests red first,
   then the service).

### Key Design Decisions

- **View, not engine — the boundary is the whole story.** 36-7 reads `PlatformBilledUsd`
  (revenue) and `CostUsd` (cost) that 34-5 computed and 36-2 persisted, and reports
  `revenue − cost`. It performs no markup math, no policy lookup, no cabinet read. The AC2
  no-recompute test makes that boundary an executable CI guard so a future change can't quietly
  duplicate 34-5's arithmetic and let the two drift.
- **Revenue comes from the per-tenant store, not the CP fact table.** `platform_analytics_hourly`
  (28-10) tracks `CostUsd` only — it has no `PlatformBilledUsd` column. The platform-aggregate
  margin therefore sums the per-tenant `analytics_usage_*` revenue across tenants (fan-out),
  rather than reading a non-existent CP revenue column. (If a future story adds
  `PlatformBilledUsd` to the CP rollup, this aggregate can swap its source behind
  `IPlatformAnalyticsService` with no endpoint change.)
- **Platform aggregate is OWNER-ONLY.** A tenant must never see the fleet margin or another
  tenant's margin; the admin route is `OwnerAccess`, the tenant route is `MemberAccess` and
  hard-scoped to `ITenantContext.TenantId` (cross-tenant → 404). The raw margin multiplier is
  never exposed by this story in either mode.
- **`NULL` dimension buckets, never sentinels.** The breakdown preserves the 36-2 `NULL`
  ("unattributed") buckets so per-dimension margin and the grand total reconcile exactly —
  coercing `NULL` to `"unknown"` would fork the total.
- **Daily grain by default.** Read `analytics_usage_daily` for whole-day windows (the common
  dashboard case) so a month of margin is a small `GROUP BY`, not an hourly scan; fall to
  `analytics_usage_hourly` only for sub-day windows.
- **Empty is zero, not null.** Pre-projection / fresh-tenant windows return a zeroed summary
  (mirrors `TenantResourceSummary.Empty`) so the dashboard renders "no margin yet" rather than
  erroring.

### Read-only boundary

This story creates **no** markup engine, **no** margin policy, **no** `AdminPricingEndpoints`,
**no** `PricingConfigService`, **no** change to `Plan` / `AgentConfig`, **no** fact-table schema
change, and **no** projection logic — all of those are 34-5 / 36-1 / 36-2 scope. It adds only the
read service, the two query endpoints, their DTOs, and tests. Any PR that recomputes a price,
reads a margin policy, or classifies BYOK from a secret cabinet under cover of this story is out
of scope — keep the diff to the analytics read path.

## Logging Requirements

- **INFO**: margin query served (`scope`, `tenantId?`, `from`, `to`, `grain`, `rowsRead`,
  `costUsd`, `revenueUsd`, `marginUsd`); platform aggregate served (`tenantsIncluded`,
  `tenantsSkipped`, totals).
- **DEBUG**: per-dimension breakdown sizes; daily-vs-hourly grain selection; trend bucket count.
- **WARN**: a per-tenant read skipped in the platform aggregate (`tenantId`, errorType) —
  fan-out continues; `ANALYTICS.MARGIN.VIEWED` emit failure (best-effort, swallowed).
- **ERROR**: tenant-scope margin read failed for the resolved tenant (surfaced to the caller as
  5xx — a single-tenant read failure is not tolerated the way the fleet fan-out is); CP/tenant
  context resolution failure.
- **Structured context**: include `{ scope, tenantId, from, to, grain, costUsd, revenueUsd,
  marginUsd, tenantsIncluded, tenantsSkipped }` where applicable.
- **Credential safety**: NEVER log tenant connection strings, search-path schema secrets, or
  provider API keys. This story reads only cost/revenue measures — it never touches provider-key
  plaintext or the margin policy, so no pricing-config secret can leak through the view.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
