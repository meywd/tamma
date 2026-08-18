# Story 36-4: Cost & Spend Analytics API (BYOK vs Platform)

Status: done
<!-- Flipped drafted -> done 2026-08-18. The deliverable named in the acceptance criteria
     was located in apps/tamma-elsa/src (and its suites in apps/tamma-elsa/tests) before this
     header was changed — not taken from a changelog. The per-story evidence is recorded
     inline on this story's line in docs/sprint-status.yaml.
-->

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase workflow (Read → Research → Break Down → TDD →
Quality Gates → Failure Handling), the `.dev/` knowledge base, TRACE/DEBUG logging
requirements, test-first development, and the build-success / coverage gates.

## User Story

As a **tenant administrator** (SaaS) **/ self-hosted owner** (single-user),
I want a tenant-scoped cost & spend analytics endpoint that separates the raw provider cost I
paid on my own BYOK keys (informational only — Tamma never marks it up) from the
platform-provided usage Tamma fronts and bills (cost basis + margin = `PlatformBilledUsd`), and
that shows my spend over time by provider and by agent, my month-to-date and projected
end-of-month billable spend, and how that projection stands against my configured budget,
so that I can see exactly where my money goes, distinguish my own provider bills from what
Tamma will invoice me, and catch a budget overrun before the period closes.

## Priority

P0 — Cost transparency (BYOK vs platform split + budget projection) is the headline tenant-facing
analytics surface of Epic 36 and gates the billing-dashboard trust story. It is the first
**read** consumer of the dimensional fact tables (Story 36-1) populated by the projection
pipeline (Story 36-2).

## Scope

The **analytics read side only**. This story builds a tenant-scoped `CostAnalyticsService` and a
`GET /api/v1/orgs/{tenantId}/analytics/cost` endpoint that read the already-populated, per-tenant
`analytics_usage_daily` fact table (Story 36-1 / Story 36-2) and join `BudgetConfig` for budget
context, then emit a budget-projection-exceeded DCB event when the linear run-rate projection
crosses the tenant's budget cap.

**Out of scope (hard boundary):**
- **No markup / margin computation.** The markup *engine* is Story 34-5; `PlatformBilledUsd` is
  already materialized per fact row by Story 36-2 (which consumes 34-5's margin via the
  `IAnalyticsPricingConfig` seam). This endpoint reads `PlatformBilledUsd` as the single source of
  truth and **never re-applies a margin** (AC4). The BYOK-fee / seat-fee model (Story 36-7) is also
  consumed upstream, not recomputed here.
- **No billing / charging mechanics.** Invoicing, metering, overage line items, and limit
  *enforcement* live in (re-targeted) Epic 20. This story reports spend; it does not charge, cap,
  or block.
- **No projection / population.** The fact tables are filled by Story 36-2's
  `HourlyAnalyticsRollupWorkflow` fan-out; this story never writes a fact row.
- **No schema change to the fact tables.** It reads `analytics_usage_daily`; the only new persisted
  artefact is the DCB event it emits.
- **No new budget write/limit surface.** `BudgetConfig` is read-only here (write/limits stay in
  Epic 20 / the existing budget API).

## Acceptance Criteria

1. A new `CostAnalyticsService`
   (`apps/tamma-elsa/src/Tamma.Api/Services/Analytics/CostAnalyticsService.cs`, behind a new
   `ICostAnalyticsService` in the same folder) reads one tenant's `analytics_usage_daily` rows
   (Story 36-1) through `ITenantDbContextFactory.CreateAsync(tenantId, ct)` — the per-tenant
   search-path schema is the only data plane it touches; it never reads the control-plane
   `platform_analytics_hourly` table or another tenant's schema.

2. `GET /api/v1/orgs/{tenantId}/analytics/cost?from=ISO&to=ISO&groupBy=provider|agent` returns a
   **daily spend time-series** in which every bucket carries two separate measures:
   `byokCostUsd` (sum of `CostUsd` where `CostBasis = byok` — raw provider cost, informational) and
   `platformBilledUsd` (sum of `PlatformBilledUsd` where `CostBasis = platform` — the billable
   amount). The series is grouped by `Day`; an optional `groupBy=provider|agent` adds a second
   grouping dimension (the `Provider` / `AgentId` fact dimension), with the `AgentId = NULL` bucket
   surfaced as `"unattributed"` in the response (never dropped, so per-group sums reconcile to the
   ungrouped total).

3. The response includes a `summary` object with **month-to-date** `platformBilledUsd` (sum over
   the current calendar-month-to-date in the tenant's reporting timezone — UTC), a **projected
   end-of-month** `platformBilledUsd` computed by **linear run-rate**
   (`mtdBilled / daysElapsed * daysInMonth`), and the current **budget** + **alertThreshold**
   pulled from `BudgetConfig` for the tenant (`GetAsync(tenantId, accountId, ct)` where `accountId`
   is the tenant GUID string, per the existing budget scope convention). When no `BudgetConfig`
   row exists the budget fields are `null` and projection is still returned.

4. **`PlatformBilledUsd` is read, never recomputed.** The endpoint sums the
   `analytics_usage_daily.PlatformBilledUsd` column materialized by Story 36-2 (which is itself the
   product of Story 34-5's markup engine) and **does not multiply by any margin**. A test asserts
   that changing a (mocked) margin config has zero effect on this endpoint's output — proving the
   single-source-of-truth contract.

5. **BYOK rows never contribute to `platformBilledUsd`.** Because Story 36-2 writes
   `PlatformBilledUsd = 0` for every `CostBasis = byok` fact row, a `byok`-basis day contributes
   only to `byokCostUsd`. An integration test seeds a **fully-BYOK** tenant and asserts the
   response shows `summary.platformBilledUsd == 0` (and `projectedPlatformBilledUsd == 0`) while
   `byokCostUsd > 0` — markup on a BYOK call is structurally impossible here.

6. **Cost trends** are derivable: the response exposes a `trend` block with the prior-equivalent-
   window `platformBilledUsd` (the immediately preceding window of the same length) and the
   percentage delta vs the requested window, so a dashboard can render "spend up/down X% vs last
   period" without a second request. BYOK cost carries its own informational delta in the same
   block.

7. **Budget-vs-actual** is explicit: the `summary` reports `budgetUsd`, `mtdPlatformBilledUsd`,
   `projectedPlatformBilledUsd`, `budgetUtilizationPct` (`mtdBilled / budgetUsd`),
   `projectedUtilizationPct` (`projectedBilled / budgetUsd`), and a boolean
   `projectedToExceedBudget` (`projectedBilled > budgetUsd`). All utilization fields are `null` when
   no budget is configured.

8. **RBAC — per mode.** The endpoint is tenant-scoped under the existing
   `/api/v1/orgs` group (`MemberAccess` policy) plus the `RequireTenantMembershipFilter` endpoint
   filter on the `{tenantId:guid}` route — exactly the pattern every other
   `/api/v1/orgs/{tenantId:guid}/...` route uses. In **SaaS mode** any tenant member (including
   `member`) may read; **no** owner/admin elevation is required for read (write/limits live in
   Epic 20). In **single-user mode** the sole user is the only caller and owns the data. A
   cross-tenant caller is rejected by the membership filter (404/403) before the handler runs.

9. **Budget-projection-exceeded DCB event.** When `projectedToExceedBudget` is true (a budget is
   configured and the linear projection exceeds it), the service appends one
   `ANALYTICS.COST.BUDGET_PROJECTED_EXCEEDED` `DomainEvent` (via `IEventRepository.AppendAsync`,
   `TenantId` set, `Type` following the `AGGREGATE.ACTION.STATUS` convention) tagged with
   `tenantId`, `mode`, `budgetUsd`, `projectedPlatformBilledUsd`, and `window`, so the alerting /
   scheduled-report pipeline (a downstream Epic 36 / Story 5.6 consumer) can act on it. Emission is
   **best-effort and deduplicated within the calendar period** — a read that re-projects the same
   exceed condition within the same month does **not** emit a second event (dedup keyed
   `(tenantId, period)`); a read that does **not** breach emits nothing.

10. **No markup, no charging, no fact-write.** The service performs read-only aggregation plus the
    single DCB event append — it never writes `analytics_usage_*`, never calls a billing/charging
    path, and never computes a margin. A code-shape test (or reviewer checklist item) confirms the
    service has no dependency on the markup engine (Story 34-5) or any Epic 20 charging service.

11. **Tenant isolation is physical and proven.** An integration test (Postgres 17 Testcontainer,
    following `SchemaPerTenantMigrationTests`) seeds `analytics_usage_daily` rows into two tenant
    schemas with different cost profiles and asserts that a request for tenant A returns only tenant
    A's spend (tenant B's rows are unreachable through A's context — search-path isolation, no EF
    query filter), and that the budget join reads only tenant A's `BudgetConfig`.

12. **Validation + empty-state.** `from`/`to` default to the current calendar month when omitted,
    are clamped to a max window (default 400 days) and rejected with `400` when `from > to`;
    `groupBy` accepts only `provider` / `agent` (else `400`). A tenant with zero fact rows returns a
    well-formed empty series with `summary` measures `0` and `null` trend deltas — never a 500.

13. Unit + integration tests cover: the BYOK/platform split (AC2, AC5); projection math (AC3 —
    run-rate against a fixed clock); the `BudgetConfig` join + budget-vs-actual fields (AC3, AC7);
    `groupBy=provider` and `groupBy=agent` with the `NULL`→`"unattributed"` bucket reconciling to
    the total (AC2); the read-only / no-re-markup contract (AC4); the
    `ANALYTICS.COST.BUDGET_PROJECTED_EXCEEDED` emit + per-period dedup + no-emit-when-under (AC9);
    RBAC (member read OK, cross-tenant rejected) (AC8); and per-tenant isolation (AC11).

## Tasks / Subtasks

- [ ] Task 1: DTOs + event constant (AC: 2, 3, 6, 7, 9)
  - [ ] Add the response DTOs under `apps/tamma-elsa/src/Tamma.Api/Dtos/Analytics/`
        (`CostAnalyticsResponse`, `CostSeriesBucket`, `CostSummary`, `CostTrend`).
  - [ ] Add the `ANALYTICS.COST.BUDGET_PROJECTED_EXCEEDED` constant (a small
        `CostAnalyticsEvents` static, or reuse the existing analytics event-constants home if one
        fits) following the `AGGREGATE.ACTION.STATUS` convention.

- [ ] Task 2: `ICostAnalyticsService` + `CostAnalyticsService` (AC: 1, 2, 3, 4, 5, 6, 7, 9, 10)
  - [ ] Read `analytics_usage_daily` via `ITenantDbContextFactory`; aggregate `byokCostUsd`
        (sum `CostUsd` where `CostBasis=byok`) and `platformBilledUsd` (sum `PlatformBilledUsd`)
        per day, with optional second grouping by `Provider`/`AgentId`.
  - [ ] Compute month-to-date + linear run-rate projection against an injected `IClock`/`TimeProvider`
        (deterministic in tests).
  - [ ] Join `BudgetConfig` (read-only) for budget-vs-actual; compute trend vs prior window.
  - [ ] Best-effort, per-period-deduped `ANALYTICS.COST.BUDGET_PROJECTED_EXCEEDED` emit via
        `IEventRepository.AppendAsync` when projected > budget.

- [ ] Task 3: `TenantAnalyticsEndpoints` + wiring (AC: 8, 12)
  - [ ] Add `apps/tamma-elsa/src/Tamma.Api/Endpoints/TenantAnalyticsEndpoints.cs` with the
        `GetCost` handler (query binding, validation, defaulting, clamping).
  - [ ] Wire `orgs.MapGet("/{tenantId:guid}/analytics/cost", TenantAnalyticsEndpoints.GetCost)
        .AddEndpointFilter<RequireTenantMembershipFilter>()` in `Program.cs` (the `orgs` group is
        already `MemberAccess`); register `ICostAnalyticsService` in DI.

- [ ] Task 4: Unit tests (AC: 13)
  - [ ] InMemory-provider service tests: split, projection (fixed clock), budget join,
        budget-vs-actual fields, grouping + `NULL`→`unattributed`, trend, no-re-markup, empty-state.
  - [ ] Event tests: emit on breach, per-period dedup, no-emit under budget.

- [ ] Task 5: Integration tests (AC: 5, 8, 11, 13)
  - [ ] Postgres 17 Testcontainer: fully-BYOK tenant → `platformBilledUsd=0`, `byokCostUsd>0`;
        two-tenant isolation (A sees only A); RBAC (member read OK, cross-tenant rejected).

## Technical Design

### C# namespace / file structure

```
apps/tamma-elsa/src/
  Tamma.Api/Endpoints/TenantAnalyticsEndpoints.cs            # NEW — tenant-scoped analytics endpoints
  Tamma.Api/Services/Analytics/ICostAnalyticsService.cs      # NEW — read-side cost analytics contract
  Tamma.Api/Services/Analytics/CostAnalyticsService.cs       # NEW — implementation
  Tamma.Api/Services/Analytics/CostAnalyticsEvents.cs        # NEW — DCB event constant(s)
  Tamma.Api/Dtos/Analytics/CostAnalyticsResponse.cs          # NEW — response DTOs
  Tamma.Api/Program.cs                                       # MODIFY — register service + map route

apps/tamma-elsa/tests/Tamma.Api.Tests/
  Analytics/CostAnalyticsServiceTests.cs                     # NEW — unit (InMemory)
  Analytics/CostAnalyticsEndpointTests.cs                    # NEW — endpoint/RBAC/validation
  Analytics/CostAnalyticsIsolationTests.cs                   # NEW — Postgres 17 isolation + BYOK split
```

> **Targeting note.** All new code lands in the C# `apps/tamma-elsa` solution. The legacy
> TypeScript `packages/api` is deleted and is **not** a target for any file in this story.

### Source columns (verified, real)

The service reads **only** the per-tenant `analytics_usage_daily` fact table (Story 36-1) plus the
control-plane-resident `BudgetConfig` for budget context:

- **`AnalyticsUsageDaily`** (NEW in Story 36-1 — `Day`, `Provider`, `AgentId`,
  `WorkflowDefinitionId`, `RepoId`, `CostBasis` enum `byok|platform`, measures `CostUsd` and
  `PlatformBilledUsd` as `decimal(20,4)`, plus token/workflow counters). This story consumes
  `Day`, `Provider`, `AgentId`, `CostBasis`, `CostUsd`, `PlatformBilledUsd`.
- **`BudgetConfig`** (`Tamma.Data/Entities/BudgetConfig.cs`, verified real) — `LimitUsd`,
  `AlertThreshold`, `PeriodDays`, keyed `(TenantId, AccountId)` where `AccountId` is the tenant GUID
  string today; read via the existing `BudgetConfigRepository.GetAsync(tenantId, accountId, ct)`.

`PlatformBilledUsd` is the **materialized billable amount** written by Story 36-2 (cost basis ×
margin, where the margin comes from Story 34-5's markup engine via 36-2's `IAnalyticsPricingConfig`
seam). This endpoint **sums it; it does not derive it.** BYOK rows already carry
`PlatformBilledUsd = 0` (Story 36-2 AC), so the split falls out of the data with no special-casing
beyond reading the right column per cost basis.

### `ICostAnalyticsService` (read-side contract)

```csharp
public interface ICostAnalyticsService
{
    /// <summary>
    /// Tenant-scoped cost/spend analytics for [from, to): a daily byok/platform
    /// split series (optionally grouped by provider|agent), month-to-date +
    /// linear-run-rate projection, a BudgetConfig join, and a trend vs the prior
    /// equivalent window. Emits ANALYTICS.COST.BUDGET_PROJECTED_EXCEEDED
    /// (best-effort, per-period deduped) when the projection crosses the budget.
    /// Reads analytics_usage_daily through the tenant's own schema only —
    /// PlatformBilledUsd is read as the single source of truth (no re-markup).
    /// </summary>
    Task<CostAnalyticsResponse> GetCostAsync(
        Guid tenantId,
        DateOnly from,
        DateOnly to,
        CostGroupBy groupBy,        // None | Provider | Agent
        string mode,                // ITammaModeProvider value, for the event tag
        CancellationToken ct);
}
```

### Aggregation sketch (read-only)

```csharp
await using var db = await _factory.CreateAsync(tenantId, ct);

var rows = await db.AnalyticsUsageDaily
    .Where(r => r.Day >= fromUtc && r.Day < toUtcExclusive)
    .Select(r => new { r.Day, r.Provider, r.AgentId, r.CostBasis, r.CostUsd, r.PlatformBilledUsd })
    .ToListAsync(ct);

// byokCostUsd = Σ CostUsd where CostBasis == Byok  (informational)
// platformBilledUsd = Σ PlatformBilledUsd          (billable; byok rows are already 0)
// group by Day (+ Provider|AgentId when requested; AgentId NULL -> "unattributed")
```

Counters are decimals throughout (`decimal(20,4)` round-trips losslessly from the fact column). The
run-rate projection uses an injected `TimeProvider` so tests pin "today" inside the month.

### Budget-vs-actual + projection

```csharp
var budget = await _budgets.GetAsync(tenantId, tenantId.ToString(), ct);   // existing scope key
var mtdBilled = /* Σ PlatformBilledUsd for current-month-to-date */;
var daysElapsed = now.Day;                       // 1..daysInMonth
var daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
var projected = daysElapsed == 0 ? 0m : mtdBilled / daysElapsed * daysInMonth;

var projectedToExceed = budget is not null && projected > budget.LimitUsd;
```

`budgetUtilizationPct` / `projectedUtilizationPct` are `null` when `budget is null`.
`AlertThreshold` is echoed straight from `BudgetConfig` (no enforcement here — that is Epic 20).

### DCB event

```csharp
// AGGREGATE.ACTION.STATUS — tenant-scoped, best-effort, per-period deduped.
public const string BudgetProjectedExceeded = "ANALYTICS.COST.BUDGET_PROJECTED_EXCEEDED";

await _events.AppendAsync(new DomainEvent
{
    Type = CostAnalyticsEvents.BudgetProjectedExceeded,
    TenantId = tenantId,
    Tags = JsonSerializer.Serialize(new { tenantId, mode, period }),
    Data = JsonSerializer.Serialize(new { budgetUsd, projectedPlatformBilledUsd, mtdPlatformBilledUsd, window }),
    Metadata = JsonSerializer.Serialize(new { workflowVersion = "1.0.0", eventSource = "system" }),
});
```

**Dedup:** before appending, the service checks (via `IEventRepository`) whether a
`BUDGET_PROJECTED_EXCEEDED` event for this `(tenantId, period)` already exists this calendar month
(`GetLastByTypeAsync` + period-tag compare, or a period-scoped query) and skips if so — so a hot
dashboard polling the endpoint produces at most one event per tenant per month, not one per request.
Emission never throws into the read path (a failed append is logged WARN; the response still
returns) — consistent with the best-effort emission shape used across the rollup/alert pipeline.

### Endpoint + wiring

```csharp
// TenantAnalyticsEndpoints.GetCost — minimal-API handler.
// orgs is already MapGroup("/api/v1/orgs").RequireAuthorization("MemberAccess").
orgs.MapGet("/{tenantId:guid}/analytics/cost", TenantAnalyticsEndpoints.GetCost)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
```

The membership filter (already used by every `/{tenantId:guid}/...` org route) verifies the caller
belongs to the route tenant before the handler runs; the per-tenant `TenantDbContext` then enforces
data isolation physically. No new auth policy is introduced.

### API shape

```
GET /api/v1/orgs/{tenantId}/analytics/cost?from=2026-06-01&to=2026-06-30&groupBy=provider

200 OK
{
  "window": { "from": "2026-06-01", "to": "2026-06-30" },
  "groupBy": "provider",
  "series": [
    { "day": "2026-06-01", "group": "anthropic", "byokCostUsd": 4.12, "platformBilledUsd": 0.00 },
    { "day": "2026-06-01", "group": "openai",    "byokCostUsd": 0.00, "platformBilledUsd": 6.30 },
    ...
  ],
  "summary": {
    "byokCostUsd": 128.40,
    "mtdPlatformBilledUsd": 211.75,
    "projectedPlatformBilledUsd": 423.50,
    "budgetUsd": 400.00,
    "alertThreshold": 0.8,
    "budgetUtilizationPct": 0.529,
    "projectedUtilizationPct": 1.059,
    "projectedToExceedBudget": true
  },
  "trend": {
    "platformBilledUsd": 190.10,        // prior equivalent window
    "platformBilledDeltaPct": 0.114,
    "byokCostUsd": 120.00,
    "byokCostDeltaPct": 0.070
  }
}
```

`series[].platformBilledUsd` is `0` for any BYOK-only row; `groupBy=agent` substitutes the agent id
(or `"unattributed"`) for `group`. Endpoint shape is identical across modes — the auth middleware +
membership filter resolve the tenant; the per-tenant context enforces isolation (the CLAUDE.md
prompt-store precedent: same wire shape, mode-aware scoping underneath).

### Per-mode + per-tenant handling

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns the cost data? | The sole user — it lives in their (only) tenant schema. | The tenant — its `t_<hex>` schema. |
| Who may read it? | The user (no RBAC). | Any tenant member (`MemberAccess` + membership filter); `member` reads, no elevation. |
| Cost basis split | Typically all `platform` (no `billing_mode`/BYOK signal self-hosted) → `byokCostUsd` may be 0; `platformBilledUsd` carries any configured margin from 36-2/34-5. | `byok` vs `platform` per the 35-2 `billing_mode` already baked into the fact rows by 36-2; BYOK → `byokCostUsd` only, platform → `platformBilledUsd`. |
| Budget source | `BudgetConfig` for the sole tenant (or the platform-default NULL-tenant row). | `BudgetConfig` for that tenant. |
| Where does the budget-exceeded event land? | The user's (only) tenant event stream (`TenantId` set). | The tenant's event stream (`TenantId` set) → tenant-scoped alerting. |
| Cross-tenant leakage risk | N/A (one tenant). | Prevented twice: membership filter rejects non-members; the per-tenant schema makes another tenant's rows physically unreachable. |

Mode does not change the wire shape — both resolve to exactly one tenant schema per request. The
`mode` value (from `ITammaModeProvider`) is carried only as an event tag for downstream alerting.

## Dependencies

**Prerequisite (internal):**
- **Story 36-1** — the per-tenant `AnalyticsUsageDaily` fact table + `CostBasis` enum +
  `CostUsd`/`PlatformBilledUsd` measure columns this story reads. (Drafted.)
- **Story 36-2** — the projection pipeline that **populates** `analytics_usage_daily`, including the
  materialized `PlatformBilledUsd` (the single source of truth this endpoint sums) and the
  `byok→PlatformBilledUsd=0` rule that makes the BYOK split structural. (Drafted.)
- **Epic 28** — per-tenant schema + `ITenantDbContextFactory` (the data plane) + the
  `/api/v1/orgs/{tenantId:guid}` group + `RequireTenantMembershipFilter` (the RBAC plane). (Merged.)
- **`BudgetConfig` + `BudgetConfigRepository`** — the read-only budget join. (Merged, verified.)

**Soft / forward (degrade gracefully if absent):**
- **Story 34-5 (markup engine)** — owns the margin math; consumed **upstream** by Story 36-2 to
  materialize `PlatformBilledUsd`. This story is decoupled from it: it reads the materialized
  column. If 34-5/36-2 wrote zero margin, `platformBilledUsd == CostUsd` for platform rows; the
  endpoint is correct either way (AC4 pins "no re-markup").
- **Story 35-2 (Epic 35 `billing_mode`)** — the BYOK-vs-platform discriminator baked into the fact
  rows' `CostBasis` by 36-2. If 35-2 has not landed, 36-2 defaults rows to `platform`; this endpoint
  then reports `byokCostUsd = 0` and all spend as `platformBilledUsd` — still well-formed.
- **Story 36-7 (pricing / BYOK-fee model)** — consumed upstream (by 36-2 / 34-5); not recomputed
  here.

**Blocks (internal):**
- Downstream Epic 36 surfaces — the cost dashboard tile, exports, and scheduled cost reports — read
  this endpoint / service. The `ANALYTICS.COST.BUDGET_PROJECTED_EXCEEDED` event feeds the
  alerting / scheduled-report consumers.

**Out-of-scope siblings (explicitly NOT dependencies of the read path):**
- **Epic 20 (billing / charging — re-targeted)** — invoicing, metering, limit enforcement. This
  endpoint reports; it does not charge or cap.

**External:**
- PostgreSQL 17 (per-tenant schema; isolation test).
- EF Core 9 / Npgsql.
- Testcontainers + Docker for the isolation / BYOK-split integration suite (run via
  `sg docker -c "dotnet test ..."`).

## Testing Strategy

1. **Unit — BYOK/platform split (`CostAnalyticsServiceTests`, InMemory):** seed `analytics_usage_daily`
   rows mixing `byok` and `platform` cost bases; assert `byokCostUsd` sums only `CostUsd` of `byok`
   rows and `platformBilledUsd` sums `PlatformBilledUsd` (byok rows contribute 0).

2. **Unit — projection math (fixed `TimeProvider`):** with "today" pinned mid-month, assert
   `projectedPlatformBilledUsd == mtdBilled / daysElapsed * daysInMonth`; day-1 and end-of-month
   edge cases; zero-MTD → zero projection (no divide-by-zero).

3. **Unit — budget join + budget-vs-actual (AC3, AC7):** with a `BudgetConfig`, assert `budgetUsd`,
   `alertThreshold`, `budgetUtilizationPct`, `projectedUtilizationPct`, `projectedToExceedBudget`;
   with **no** `BudgetConfig`, assert budget/utilization fields are `null` and the series/projection
   still return.

4. **Unit — grouping (`groupBy=provider` / `groupBy=agent`):** assert one bucket per (day, group);
   the `AgentId = NULL` rows surface as `"unattributed"`; `Σ(group buckets) == ungrouped total`
   (reconciliation).

5. **Unit — no re-markup (AC4, AC10):** run with two different (mocked) margin configs present in
   DI; assert byte-identical `platformBilledUsd` output — proving the endpoint reads the
   materialized column and never multiplies.

6. **Unit — trend (AC6):** seed the prior equivalent window; assert `trend.platformBilledUsd` and
   `platformBilledDeltaPct` (and the byok counterparts); empty prior window → `null` delta.

7. **Unit — budget event (AC9):** projection over budget → exactly one
   `ANALYTICS.COST.BUDGET_PROJECTED_EXCEEDED` appended with `tenantId`/`mode`/budget tags; a second
   call same period → no second event (dedup); projection under budget → no event; a failing
   `AppendAsync` is swallowed (response still returns, WARN logged).

8. **Unit — validation / empty-state (AC12):** `from > to` → 400; bad `groupBy` → 400; omitted
   window defaults to current month; window clamp; zero fact rows → well-formed empty series +
   zeroed summary + null trend.

9. **Integration — fully-BYOK tenant (AC5, Postgres 17 Testcontainer):** seed a tenant whose
   `analytics_usage_daily` rows are all `byok` (so `PlatformBilledUsd = 0`); assert
   `summary.platformBilledUsd == 0`, `projectedPlatformBilledUsd == 0`, `byokCostUsd > 0`.

10. **Integration — per-tenant isolation (AC11):** seed two tenant schemas with different cost
    profiles; assert a request for A returns only A's spend (B unreachable through A's context) and
    the budget join reads only A's `BudgetConfig`.

11. **Integration — RBAC (AC8):** a `member` of the tenant reads `200`; a non-member / cross-tenant
    caller is rejected by `RequireTenantMembershipFilter` (404/403) before the handler runs.

**Mocks:** No external provider / Stripe calls (read-only aggregation + one DCB append). InMemory
provider for split/projection/grouping/trend/event-shape assertions; a real Postgres 17
Testcontainer for the BYOK-split end-to-end, per-tenant isolation, and RBAC (EF InMemory does not
model per-tenant schemas — same rationale as `ConventionStoreMigrationTests`). The margin config is
mocked only to prove it has **no** effect (AC4).

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/TenantAnalyticsEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/ICostAnalyticsService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/CostAnalyticsService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/CostAnalyticsEvents.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Dtos/Analytics/CostAnalyticsResponse.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (register `ICostAnalyticsService`, map `/analytics/cost` route) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/CostAnalyticsServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/CostAnalyticsEndpointTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/CostAnalyticsIsolationTests.cs` | Create |

> `AnalyticsUsageDaily` (Story 36-1) and `BudgetConfig` (existing) are **read** by this story; they
> are not modified. `TenantAnalyticsEndpoints.cs` and `CostAnalyticsService.cs` are NEW (verified
> absent at authoring time).

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions (analytics, cost, budget, BYOK,
   tenancy).
3. Reviewed Story 36-1 (the `AnalyticsUsageDaily` shape + `CostBasis` enum +
   `CostUsd`/`PlatformBilledUsd` columns) and Story 36-2 (how `PlatformBilledUsd` is materialized
   and why BYOK rows carry 0) — this endpoint is a *reader* of both.
4. Reviewed `BudgetConfig` / `BudgetConfigRepository` (scope key = tenant GUID string) and the
   `/api/v1/orgs/{tenantId:guid}` + `RequireTenantMembershipFilter` wiring in `Program.cs`.
5. Confirmed the test runner: docker-bound suites run via `sg docker -c "dotnet test ..."`
   (session docker group is stale; the build itself needs no wrapper).
6. Planned the TDD cycle (write the split + projection + no-re-markup tests red first, then the
   service).

### Key Design Decisions

- **Read `PlatformBilledUsd`, never recompute it.** The markup engine is Story 34-5; the margin is
  materialized into the fact rows by Story 36-2. If this endpoint re-applied a margin it would
  double-charge and fork the single source of truth. AC4's "margin config has zero effect" test is
  the guardrail.
- **BYOK split is structural, not a special case.** Story 36-2 writes `PlatformBilledUsd = 0` for
  every `byok` row, so summing the right column per cost basis yields the split with no branching —
  a fully-BYOK tenant *cannot* show non-zero platform-billed spend (AC5). `byokCostUsd` is purely
  informational ("here's what you paid your own provider").
- **Linear run-rate projection, documented as such.** `mtd / daysElapsed * daysInMonth` is the
  simplest defensible projection and matches how operators reason about budgets. It is deliberately
  not a smoothed/seasonal model — that is a later enhancement, not P0. The clock is injected so the
  math is deterministic in tests.
- **Per-period event dedup, best-effort emission.** A dashboard may poll this endpoint many times a
  day; the budget-exceeded DCB event is for *alerting*, so it fires at most once per tenant per
  calendar period and never throws into the read path. The dedup key is `(tenantId, period)`, read
  from the existing event store before appending.
- **No new auth policy.** Tenant-scoped reads ride the established `/api/v1/orgs/{tenantId:guid}`
  group (`MemberAccess` + `RequireTenantMembershipFilter`). Read access is `member`-level by design
  (cost transparency is a team-wide concern); write/limit power stays in Epic 20.
- **Daily grain, not hourly.** Cost dashboards and budget projection work on days; reading
  `analytics_usage_daily` (pre-compacted by 36-2) keeps the query cheap and avoids re-aggregating
  the hourly table on every request.

### Read-only boundary

This story adds **no** projection/population, **no** schema change to the fact tables, **no** markup
/ margin math, **no** billing/charging/limit-enforcement, and **no** write to `analytics_usage_*`.
Its only persisted side effect is the deduped `ANALYTICS.COST.BUDGET_PROJECTED_EXCEEDED` DCB event.
Any PR that adds margin computation, a charging call, or a fact-table write under cover of this
story is out of scope — keep the diff to the read service, the endpoint, the DTOs, the event
constant, and tests.

## Logging Requirements

- **INFO**: cost analytics queried (`tenantId`, `from`, `to`, `groupBy`, `rows`,
  `mtdPlatformBilledUsd`, `projectedPlatformBilledUsd`); budget-projected-exceeded event emitted
  (`tenantId`, `budgetUsd`, `projectedPlatformBilledUsd`, `period`).
- **DEBUG**: per-bucket aggregation; budget join result (`budgetUsd`, `alertThreshold` — never the
  raw row); projection inputs (`mtdBilled`, `daysElapsed`, `daysInMonth`); dedup decision
  (event already present this period → skip).
- **WARN**: budget-exceeded event append failed (best-effort — response still returned);
  request window clamped (`requested`, `clamped`).
- **ERROR**: tenant context resolution failure / unexpected aggregation error surfaced as a 500
  (with `tenantId`, no data values).
- **Structured context**: include `{ tenantId, from, to, groupBy, mode, budgetUsd,
  mtdPlatformBilledUsd, projectedPlatformBilledUsd }` where applicable.
- **Credential safety**: NEVER log tenant connection strings, search-path schema secrets, or
  provider API keys. The endpoint reads only aggregated cost/spend numbers and the `billing_mode`-
  derived `CostBasis` discriminator — never raw provider-key plaintext. Cost figures are
  business-sensitive but not secrets; still scope them to DEBUG, not INFO, beyond the summary.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
