# Story 36-5: Agent & Tenant Performance Rollups API (consume Epic 32)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase workflow (Read → Research → Break Down → TDD →
Quality Gates → Failure Handling), the `.dev/` knowledge base, TRACE/DEBUG logging
requirements, test-first development, and the build-success / coverage gates.

This story targets the C# **`apps/tamma-elsa`** stack (Tamma.Api + Tamma.Data + Tamma.Core).
The TypeScript `packages/api` is **deleted** — do not reference it. DCB event sourcing, the
agent action trail (32-6) and outcome/defect capture (32-8), the `ProviderDiagnostic`
diagnostics (Epic 9), the dimensional analytics store (36-1) and its projection pipeline (36-2),
the per-tenant `TenantDbContext`, and the tenant-scoped `IEventRepository` all live in
`apps/tamma-elsa`.

## User Story

As a **tenant owner/member deciding which agent and provider configuration to keep running my
design and review work**,
I want a **reporting API over my own pre-aggregated analytics** — per-agent rollups (runs,
success rate, average duration, tokens-per-run, cost-per-run, platform-billed) over a selectable
window, a daily trend per agent, and a tenant-level performance summary — read **only** from my
tenant's datasets,
So that I can compare my agents on real downstream success and cost without ever exposing my
performance data to another tenant or to the platform admin who happens to own a public agent
definition.

## Priority

P1 — the consumable reporting payoff of the Epic 36 analytics pipeline. Stories 36-1/36-2 build
and fill the per-tenant `analytics_usage_hourly`/`analytics_usage_daily` fact tables; this story
turns that dimensional store into the agent/tenant performance views a tenant actually reads.
Without it the facts are queryable row-by-row but there is no rollup/reporting surface.

## Scope

**Reporting API only.** This story is a **read-side consumer** of the Story 36-1/36-2 dimensional
fact tables and the Epic 9 `ProviderDiagnostic` outcome substrate. It:

- adds an `AgentPerformanceAnalyticsService` that aggregates a tenant's own `analytics_usage_daily`
  (with `analytics_usage_hourly` for sub-day windows) into **per-agent rollups** keyed by the
  `AgentId` dimension, resolving each agent's display **name** from the Epic 32 agent registry;
- adds a **daily trend** read (success rate + cost + tokens) for one agent;
- adds a **tenant-level summary** (blended success rate, total billable spend, most/least
  efficient agents);
- exposes all three behind tenant-scoped `/api/v1/orgs/{tenantId}/analytics/agents*` endpoints
  with member-read RBAC.

It does **NOT**:

- **recompute or duplicate Story 32-10's `BenchmarkProjectionService`.** 32-10 owns the
  agent/provider/prompt/persona **leaderboard** read model (success rate, iterations-to-done,
  defect-by-category, p50/p95 latency, k-anonymous public-agent fleet rollup) folded directly from
  the `AGENT.*` action-trail events into the tenant's `BenchmarkProjection` rows. **This story is
  the analytics-layer rollup/reporting API** that reads the Epic 36 **dimensional usage store**
  (provider/agent/workflow/repo/cost-basis facts) — a different projection, a different shape
  (cost/throughput-centric, not defect/latency-centric), serving the Epic 36 analytics product.
  Where the two overlap (per-agent success rate), this story reads the 36-1 fact measures
  (`WorkflowsStarted/Completed/Failed` per agent) and **references** 32-10 rather than re-folding
  the action trail. The leaderboard and this rollup are sibling read models, not the same surface.
- alter the 36-1 fact-table schema, change the 36-2 projection, or add a new source-event family;
- expose any cross-tenant or platform-admin per-tenant performance path (performance rollups are
  **strictly per-tenant** — a platform admin cannot read a tenant's agent performance; the only
  cross-tenant analytics surface that exists is 32-10's k-anonymous **public-agent** fleet rollup,
  which this story does not extend).

## Acceptance Criteria

1. A new `AgentPerformanceAnalyticsService`
   (`apps/tamma-elsa/src/Tamma.Api/Services/Analytics/AgentPerformanceAnalyticsService.cs`,
   behind `IAgentPerformanceAnalyticsService`) reads the resolving tenant's own
   `analytics_usage_daily` rows (and `analytics_usage_hourly` for windows finer than a day) via
   `ITenantDbContextFactory.CreateAsync(tenantId, ct)` and produces **per-agent rollups** grouped
   by the `AgentId` dimension over a `[from, to)` window. It exposes a static pure-DI
   `ComputeAgentRollupsAsync(...)` entry point (taking the resolved `TenantDbContext`, an
   `IAgentNameResolver`, `tenantId`, window, ordering metric, logger, `CancellationToken`) so the
   endpoint and unit tests drive the same code without an HTTP context — mirroring the
   `PlatformAnalyticsService` aggregation shape.

2. `GET /api/v1/orgs/{tenantId}/analytics/agents?from=&to=&orderBy=&limit=` returns per-agent
   rollups — each row `{ agentId, name, runs, successRate, avgDurationMs, tokensPerRun,
   costUsdPerRun, platformBilledUsd }` — over the window, **ordered by a selectable metric**
   (`orderBy` ∈ `runs|successRate|avgDurationMs|tokensPerRun|costUsdPerRun|platformBilledUsd`;
   default `runs` desc) with a documented stable final tie-break on `agentId` asc. `runs` is the
   per-agent terminal-workflow count (`WorkflowsCompleted + WorkflowsFailed`); `successRate` is
   `WorkflowsCompleted ÷ runs`; `tokensPerRun` is `(TokensIn + TokensOut) ÷ runs`; `costUsdPerRun`
   is `CostUsd ÷ runs`; `platformBilledUsd` is the summed `PlatformBilledUsd` measure.

3. **All performance numbers come strictly from the resolving tenant's own
   `analytics_usage_*` rows** — the agent registry is consulted **only** to resolve the display
   `name` for an `AgentId`. The agent identity/name is resolved from the Epic 32 agent registry
   distinguishing public/system definitions from private/tenant ones, but a public agent's row
   shows **only this tenant's** measures, never a fleet aggregate. **A test asserts** that a
   public/system agent run by two tenants yields, for each tenant's call, only that tenant's
   metrics (no aggregate, no other tenant's numbers).

4. `GET /api/v1/orgs/{tenantId}/analytics/agents/{agentId}/trend?from=&to=&granularity=day`
   returns the **daily trend series** for one agent: an ordered array of
   `{ date, runs, successRate, costUsd, tokensIn, tokensOut, platformBilledUsd }` points, one per
   UTC day in `[from, to)` that the agent had activity, read from `analytics_usage_daily` filtered
   to the agent's `AgentId`. Days with no activity are omitted (the series is sparse, not
   zero-padded) — see AC7 for the zero-activity contract.

5. `GET /api/v1/orgs/{tenantId}/analytics/summary?from=&to=` returns a **tenant-level performance
   summary**: `{ blendedSuccessRate, totalRuns, totalCostUsd, totalPlatformBilledUsd,
   mostEfficientAgents[], leastEfficientAgents[] }` where the blended success rate is the
   tenant-wide `Σ WorkflowsCompleted ÷ Σ (WorkflowsCompleted + WorkflowsFailed)` across all agents
   in the window, total billable spend is `Σ PlatformBilledUsd`, and most/least efficient agents
   are the top/bottom-N by an efficiency metric (default `costUsdPerRun` asc = most efficient),
   each guarded by a minimum-run threshold so a 1-run agent never tops the chart.

6. **RBAC, per-mode.** All read endpoints are gated `MemberAccess` + `RequireTenantMembershipFilter`
   on `{tenantId}` — any tenant **member** may read their tenant's performance (single-user mode:
   the sole user; SaaS mode: any member of that tenant). A caller who is not a member of `{tenantId}`
   is rejected by `RequireTenantMembershipFilter` (403/404). **No cross-tenant agent performance is
   ever returned**, and there is **no** platform-admin route that reads a tenant's agent
   performance — a platform owner calling `/api/v1/orgs/{otherTenant}/analytics/*` is denied like
   any non-member. **Explicitly tested** (member-read for own tenant; cross-tenant denied;
   platform owner denied a tenant's performance).

7. **Zero-activity and unknown/deleted agents are handled gracefully, never as errors.** An agent
   with zero rows in the window returns an **empty trend** (`[]`, HTTP 200) — not a 404 or 500.
   An `AgentId` present in the fact rows but **unknown/deleted** in the registry (e.g. a removed
   private agent) renders with a **placeholder name** (`name: "(unknown)"`, the `agentId` still
   carried) so the row still appears and the tenant can investigate — mirroring the
   `PlatformAnalyticsService.GetTopTenantsAsync` `(unknown)` placeholder fallback for
   hard-deleted tenant directory rows. The `AgentId = NULL` "unattributed" dimension bucket
   (events with no `agent_id` tag, per 36-2 AC3) is surfaced as a synthetic row with
   `agentId: null, name: "(unattributed)"` so per-agent rows reconcile to the tenant grand total.

8. The endpoints are **read-only reporting** — there is no mutation surface for performance rows
   (they are derived from the 36-1/36-2 fact tables; there is no edit, no rebuild trigger here —
   the dimensional rollup is rebuilt by the Story 36-2 workflow / its backfill input, not this API).
   The service performs **no recompute on the read path**: it aggregates the pre-folded
   `analytics_usage_*` rows with a `GROUP BY AgentId` over the window, never re-scans the raw event
   stream.

9. **Windowing is bounded and validated.** `from`/`to` are optional ISO-8601 UTC instants; default
   window is the trailing 30 days (`to = now`, `from = now − 30d`); `from` must be `< to`, the
   window is capped (default 366 days, configurable) and `limit`/top-N are bounded
   (`limit` default 50, max 500; top/bottom-N default 5, max 50) — mirroring the
   `PlatformAnalyticsService.GetTopTenantsAsync` limit-clamp convention — so a malformed or
   unbounded request returns 400, never a runaway scan.

10. **No new DCB source events are produced.** This story emits, at most, lightweight
    observability events for the reporting reads (`ANALYTICS.REPORT.AGENT_ROLLUP.SERVED`,
    `ANALYTICS.REPORT.TENANT_SUMMARY.SERVED` on the `AGGREGATE.ACTION.STATUS` convention) carrying
    `{ tenantId, window, agentCount, rowsRead }` for audit/usage telemetry — it consumes the
    `analytics_usage_*` facts (36-1/36-2) and the `ProviderDiagnostic` substrate (Epic 9) and does
    not write to either. The reporting events are best-effort and never block or fail a read.

11. **Unit + integration tests** cover: per-agent aggregation (runs/successRate/tokensPerRun/
    costUsdPerRun/platformBilledUsd from a known fact-row fixture → exact values); trend bucketing
    (daily series ordered, sparse for inactive days, empty for a zero-activity agent); metric
    ordering (each `orderBy` option produces the documented order + `agentId` tie-break);
    name resolution (registry name; `(unknown)` placeholder for a missing/deleted agent;
    `(unattributed)` for the `NULL` bucket); **public-agent isolation** (a public/system agent run
    by two tenants → each tenant sees only its own metrics, proven across two tenant schemas);
    tenant **isolation** (tenant B cannot read tenant A's rollup via any path; platform owner
    denied a tenant's performance); zero-activity (empty trend, HTTP 200); and window validation
    (bad `from`/`to` → 400, window clamp applied).

## Tasks / Subtasks

- [ ] Task 1: DTOs + service contract (AC: 1, 2, 4, 5)
  - [ ] Add `AgentPerformanceDtos.cs` — `AgentRollupRow`, `AgentTrendPoint`, `TenantPerformanceSummary`,
        and the window/order request shapes.
  - [ ] Add `IAgentPerformanceAnalyticsService` + `IAgentNameResolver` seams.

- [ ] Task 2: Agent name resolution (AC: 3, 7)
  - [ ] `IAgentNameResolver` resolves `AgentId → name` from the Epic 32 agent registry
        (public/system vs private/tenant); returns `"(unknown)"` for a missing/deleted id and
        treats the `NULL` bucket as `"(unattributed)"`. The resolver reads **only** identity/name —
        never performance numbers.
  - [ ] Unit test all three name paths.

- [ ] Task 3: `AgentPerformanceAnalyticsService` aggregation (AC: 1, 2, 5, 8, 9)
  - [ ] `ComputeAgentRollupsAsync` — `GROUP BY AgentId` over `analytics_usage_daily`
        (`analytics_usage_hourly` for sub-day windows), measures → rollup metrics; ordering metric
        + `agentId` tie-break; window-clamp + validation.
  - [ ] `GetAgentTrendAsync` — daily series for one agent (sparse, ordered).
  - [ ] `GetTenantSummaryAsync` — blended success rate, total spend, most/least efficient (min-run
        guarded).

- [ ] Task 4: Tenant analytics endpoints (AC: 2, 4, 5, 6, 9, 10)
  - [ ] `TenantAnalyticsEndpoints.cs` — map `GET …/analytics/agents`,
        `GET …/analytics/agents/{agentId}/trend`, `GET …/analytics/summary` under the
        `/api/v1/orgs/{tenantId}` group with `MemberAccess` + `RequireTenantMembershipFilter`;
        window parse + 400 on bad input; best-effort `ANALYTICS.REPORT.*` events.
  - [ ] DI wiring in `Program.cs`.

- [ ] Task 5: Tests (AC: 11)
  - [ ] Unit (InMemory) for aggregation/trend/ordering/name-resolution/summary against fact-row
        fixtures.
  - [ ] Integration (Postgres 17 Testcontainer) for public-agent isolation across two tenant
        schemas, cross-tenant + platform-owner denial, zero-activity empty trend, window 400.

## Technical Design

### Where the data comes from (verified against repo @ main 98cfb1c2, 2026-06-17)

This story is a **read model over read models**: the dimensional usage facts (36-1/36-2) are the
primary source; the Epic 32 agent registry is the identity/name lookup; the Epic 9
`ProviderDiagnostic` table is the diagnostic outcome substrate behind the facts. Isolation is
**structural** — every fact read goes through the tenant's `TenantDbContext` (search-path schema),
so a tenant query can only ever see that tenant's rows.

| Component | File | Role in this story |
|---|---|---|
| Per-tenant usage facts (primary source) | `apps/tamma-elsa/src/Tamma.Data/Entities/AnalyticsUsageDaily.cs` (**Story 36-1 — NEW, drafted**), `AnalyticsUsageHourly.cs` (**36-1 NEW**) | `AgentId`, `Provider`, measures `TokensIn/Out`, `WorkflowsCompleted/Failed`, `CostUsd`, `PlatformBilledUsd`, `Day`/`Hour` — the rollup substrate, read-only. |
| Per-tenant DbContext | `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | Owns the `analytics_usage_*` DbSets (added by 36-1); the structural isolation plane (no cross-tenant query filter). Read-only here. |
| Schema-per-tenant routing | `ITenantDbContextFactory` / `ITenantContext` (`Tamma.Data`) | Resolves the resolving tenant's schema for every read — no new plumbing. |
| Diagnostic outcome substrate | `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs` (`AgentType`, `RequestDurationMs`, `InputTokens`/`OutputTokens`, `Cost`, `Success`) | The Epic 9 diagnostics the 36-2 projection already folds into the facts; referenced as the lineage of `avgDurationMs`/`success`. **Read/referenced, not modified.** |
| Agent registry (identity/name only) | Epic 32 agent registry (`AgentConfig`/agent definition entity + `AgentEndpoints.cs`) | `AgentId → name` + public/system vs private/tenant flag. **Identity only — never a performance source.** |
| Tenant-scope endpoint precedent | `apps/tamma-elsa/src/Tamma.Api/Authorization/RequireTenantMembershipFilter.cs` + the `/api/v1/orgs/{tenantId}/…` group (`Program.cs`, `AlertEndpoints.cs`, `OrgEndpoints.cs`) | The member-readable tenant-path pattern these endpoints ride. |
| Auth policies | `apps/tamma-elsa/src/Tamma.Api/Program.cs` (`MemberAccess` ~991, `OwnerAccess`/`PlatformOwnerAccess` ~971-990) | `MemberAccess` for the reads; **no** `PlatformOwnerAccess` per-tenant route exists for performance. |
| Placeholder fallback precedent | `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/PlatformAnalyticsService.cs` (`GetTopTenantsAsync` `(unknown)` fallback ~344-350; limit-clamp ~280-281) | The `(unknown)` placeholder + limit-clamp conventions this story mirrors for deleted agents + window bounds. |
| Leaderboard read model (sibling, NOT duplicated) | `apps/tamma-elsa/src/Tamma.Api/Services/Agents/BenchmarkProjectionService.cs` (**Story 32-10 — NEW, drafted**) | The agent/provider/prompt/persona leaderboard folded from the action trail. **Referenced** as the sibling; this story does the cost/throughput rollup over the dimensional store instead — see Scope. |

> **NEW files** are marked NEW in the Files table. `AgentPerformanceAnalyticsService.cs`,
> `IAgentPerformanceAnalyticsService`, `IAgentNameResolver`, `TenantAnalyticsEndpoints.cs`, and
> `AgentPerformanceDtos.cs` do **not** exist yet. `AnalyticsUsageDaily.cs`/`AnalyticsUsageHourly.cs`
> are NEW from **Story 36-1** (drafted, not yet merged) — this story is hard-blocked on them.
> `ProviderDiagnostic.cs`, `TenantDbContext.cs`, `RequireTenantMembershipFilter.cs`,
> `PlatformAnalyticsService.cs`, and the Epic 32 agent registry are **referenced**, not rewritten.

### C# namespace / file structure

```
apps/tamma-elsa/src/
  Tamma.Api/Services/Analytics/
    IAgentPerformanceAnalyticsService.cs        # NEW — service contract
    AgentPerformanceAnalyticsService.cs         # NEW — per-agent rollup + trend + tenant summary
    IAgentNameResolver.cs                        # NEW — AgentId → name (registry identity only)
    AgentNameResolver.cs                         # NEW — registry-backed resolver + (unknown)/(unattributed)
    AgentPerformanceDtos.cs                      # NEW — rollup row / trend point / summary wire shapes
  Tamma.Api/Endpoints/
    TenantAnalyticsEndpoints.cs                  # NEW — /api/v1/orgs/{tenantId}/analytics/* (member-read)
  Tamma.Api/Program.cs                           # MODIFY — DI + map the three routes
  Tamma.Data/Entities/AnalyticsUsageDaily.cs     # READ (Story 36-1) — primary source
  Tamma.Data/Entities/AnalyticsUsageHourly.cs    # READ (Story 36-1) — sub-day windows
  Tamma.Data/Entities/ProviderDiagnostic.cs      # READ (Epic 9) — outcome lineage

apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/
  AgentPerformanceAnalyticsServiceTests.cs       # NEW — aggregation/trend/ordering/name/summary (InMemory)
  TenantAnalyticsEndpointsTests.cs               # NEW — RBAC, window 400, zero-activity 200
  AgentPerformanceIsolationTests.cs              # NEW — public-agent isolation + cross-tenant denial (Postgres 17)
```

### Per-agent rollup (the aggregation)

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Analytics/IAgentPerformanceAnalyticsService.cs (NEW)
public interface IAgentPerformanceAnalyticsService
{
    /// Per-agent rollup over [from, to) for the resolving tenant, ordered by `orderBy`.
    Task<IReadOnlyList<AgentRollupRow>> GetAgentRollupsAsync(
        Guid tenantId, DateTime from, DateTime to, string orderBy, int limit, CancellationToken ct = default);

    /// Daily trend (success rate + cost + tokens) for one agent. Empty (not 404) for zero activity.
    Task<IReadOnlyList<AgentTrendPoint>> GetAgentTrendAsync(
        Guid tenantId, string? agentId, DateTime from, DateTime to, CancellationToken ct = default);

    /// Tenant-level blended summary + most/least efficient agents.
    Task<TenantPerformanceSummary> GetTenantSummaryAsync(
        Guid tenantId, DateTime from, DateTime to, int topN, CancellationToken ct = default);
}
```

The rollup is a `GROUP BY AgentId` over the **pre-folded** facts — no event-stream rescan:

```
rows = tenantDb.AnalyticsUsageDaily.Where(r => r.Day >= from && r.Day < to)   // hourly for sub-day windows
group rows by r.AgentId into g:
    runs              = Σ (WorkflowsCompleted + WorkflowsFailed)
    successRate       = runs == 0 ? 0 : Σ WorkflowsCompleted / runs
    tokensPerRun      = runs == 0 ? 0 : Σ (TokensIn + TokensOut) / runs
    costUsdPerRun     = runs == 0 ? 0 : Σ CostUsd / runs
    platformBilledUsd = Σ PlatformBilledUsd
    avgDurationMs     = avg run duration (from ProviderDiagnostic lineage carried on the facts / 36-2 measure)
    name              = nameResolver.Resolve(AgentId)   // registry identity only
order by orderBy (default runs desc) then agentId asc
take limit
```

`avgDurationMs` is sourced from the duration substrate the 36-2 projection carries from
`ProviderDiagnostic.RequestDurationMs`; if the dimensional store does not carry a duration measure
at 36-2 merge time, this story reads `ProviderDiagnostic` per-agent (tenant-scoped) for the duration
column only — **coordinate the duration measure with 36-2 before merge** (mark the duration source
explicitly in the PR; do not invent a new fact column under cover of this story).

### Daily trend

```csharp
// GET /api/v1/orgs/{tenantId}/analytics/agents/{agentId}/trend?from=&to=
// Reads analytics_usage_daily filtered to AgentId == agentId, one point per active UTC day, ordered.
// Zero activity → [] (HTTP 200), never 404.
public record AgentTrendPoint(
    DateOnly Date, long Runs, double SuccessRate,
    decimal CostUsd, long TokensIn, long TokensOut, decimal PlatformBilledUsd);
```

### Tenant summary

```csharp
public record TenantPerformanceSummary(
    double BlendedSuccessRate,     // Σ Completed / Σ (Completed + Failed) tenant-wide
    long TotalRuns,
    decimal TotalCostUsd,
    decimal TotalPlatformBilledUsd,
    IReadOnlyList<AgentRollupRow> MostEfficientAgents,   // top-N by costUsdPerRun asc, min-run guarded
    IReadOnlyList<AgentRollupRow> LeastEfficientAgents); // bottom-N
```

### Name resolution (identity only — the isolation guarantee)

`IAgentNameResolver` is the **only** place the Epic 32 registry is touched, and it reads **only**
`AgentId → name` (+ public/system vs private/tenant). It never reads or returns a performance
number. This is what enforces AC3: a public agent's *name* comes from the shared registry, but
every metric comes from the tenant's own `analytics_usage_*` rows, so two tenants running the same
public agent see two disjoint metric sets. Missing/deleted id → `"(unknown)"` (the
`GetTopTenantsAsync` placeholder precedent); the `NULL` bucket → `"(unattributed)"`.

### Endpoints

```
GET /api/v1/orgs/{tenantId}/analytics/agents
      ?from=&to=&orderBy=runs&limit=50            → AgentRollupRow[]            (MemberAccess)
GET /api/v1/orgs/{tenantId}/analytics/agents/{agentId}/trend
      ?from=&to=                                   → AgentTrendPoint[] (or [])  (MemberAccess)
GET /api/v1/orgs/{tenantId}/analytics/summary
      ?from=&to=&topN=5                            → TenantPerformanceSummary   (MemberAccess)
```

All three ride the `/api/v1/orgs/{tenantId}` group with `RequireTenantMembershipFilter`, so
`{tenantId}` is route-bound and validated against the caller's membership; the read is physically
scoped by the per-tenant `TenantDbContext` to that schema. There is no admin/cross-tenant variant.

### DCB events (this story)

No new **source** events. At most two best-effort observability events on the
`AGGREGATE.ACTION.STATUS` convention, appended via `IPlatformEventPublisher` (best-effort, never
blocks a read), for usage telemetry / audit:

| Event | When | Key data |
|---|---|---|
| `ANALYTICS.REPORT.AGENT_ROLLUP.SERVED` | a rollup/trend read served | `{ tenantId, window, agentCount, rowsRead }` |
| `ANALYTICS.REPORT.TENANT_SUMMARY.SERVED` | a summary read served | `{ tenantId, window, totalRuns }` |

These are optional telemetry; if the platform prefers zero events for read paths, they may be
dropped — the reporting reads function without them. They are **never** new performance facts.

### Per-mode + per-tenant handling (mandatory two-scoping answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns the performance data? | The sole user — their instance, their (single) tenant schema. | The tenant that generated it. ALWAYS tenant-scoped; one public agent definition → many independent per-tenant datasets. |
| Who reads the rollups/trend/summary? | The user (member-read). | Any tenant member (`MemberAccess`) for their own tenant only. |
| Can a platform admin read a tenant's agent performance? | N/A (sole user). | **No.** Per-tenant facts are structurally unreachable cross-tenant; there is no admin per-tenant performance route. (The only cross-tenant agent analytics that exists is 32-10's k-anonymous **public-agent** fleet rollup — a different surface this story does not extend.) |
| Where do the facts live? | The single tenant store (`analytics_usage_*`). | The originating tenant's `t_<hex>` schema. |
| Mode source | `ITammaModeProvider` (process-stable). | same |

Mode does not change the endpoint shape — both resolve to exactly one tenant schema per request
(the prompt-store precedent: identical endpoint shape, auth middleware decides scope). The
platform-wide owner analytics stays on the CP `platform_analytics_hourly` table (Story 28-10),
which this story never touches.

## Dependencies

**Prerequisite (internal):**

- **Story 36-1** (dimensional analytics schema & store) — the `analytics_usage_daily` /
  `analytics_usage_hourly` fact tables, the `AgentId`/`Provider` dimensions, and the
  `WorkflowsCompleted/Failed`, `TokensIn/Out`, `CostUsd`, `PlatformBilledUsd` measures this story
  aggregates. **Hard prerequisite** — there is nothing to roll up without the fact tables.
  (Drafted; not yet merged.)
- **Story 36-2** (DCB-to-analytics projection pipeline) — fills the fact tables (per-agent measures
  bucketed by the `agent_id` Epic 32 tag, with the `NULL` "unattributed" bucket). **Hard
  prerequisite** — the tables are inert until 36-2 populates them. (Drafted.)
- **Story 36-3** (per the spec dependency list — the Epic 36 query-API foundation / shared analytics
  read seam this reporting API builds on; align the tenant analytics route group + window/paging
  conventions with 36-3 before merge). (Per spec dependency.)
- **Epic 32 (agents as first-class entities + `agent_id` action trail + per-tenant datasets)** —
  the agent registry this story resolves names from, and the `agent_id` DCB tag the 36-2 projection
  buckets the per-agent facts by. The tenancy rule (one definition → many per-tenant datasets;
  platform admin cannot read tenant performance) is the design constraint this story enforces.
- **Epic 9 (`ProviderDiagnostic`)** — the diagnostic outcome substrate (duration, tokens, cost,
  success) the 36-2 projection folds into the facts; referenced as the lineage of `avgDurationMs`
  and the per-agent success/cost signal.

**Related (sibling read model — referenced, NOT duplicated):**

- **Story 32-10** (Benchmark projections & leaderboards) — the agent/provider/prompt/persona
  leaderboard folded directly from the `AGENT.*` action trail (success rate, iterations-to-done,
  defect-by-category, p50/p95 latency, k-anonymous public-agent fleet rollup). This story is the
  **analytics-layer rollup/reporting API** over the Epic 36 **dimensional usage store** — a
  different projection (cost/throughput-centric) serving the Epic 36 analytics product. Where they
  overlap (per-agent success rate), this story reads the 36-1 fact measures and references 32-10;
  it does **not** re-fold the action trail or re-implement the leaderboard. Coordinate the
  per-agent success-rate definition with 32-10 so the two surfaces agree.

**Blocks (internal):**

- Downstream Epic 36 stories (exports, scheduled reports, dashboard) that surface agent/tenant
  performance read these endpoints.

**External:**

- PostgreSQL 17 (per-tenant schema reads; `GROUP BY` over the fact tables).
- EF Core 9 / Npgsql.
- Testcontainers + Docker for the per-tenant isolation suite (run via
  `sg docker -c "dotnet test ..."`).

## Testing Strategy

1. **Metric-correctness (highest priority — AC 2, 5, 11)**
   (`AgentPerformanceAnalyticsServiceTests`, InMemory): seed `analytics_usage_daily` fact rows for
   two agents over a known window (e.g. agent A: 7 completed / 3 failed, 12 000 tokens, $0.40 cost,
   $0.52 billed; agent B: 4 completed / 1 failed, …) and assert the rollup row's
   `runs == 10`, `successRate == 0.7`, `tokensPerRun`, `costUsdPerRun`, `platformBilledUsd`, and
   the tenant summary's `blendedSuccessRate`, `totalPlatformBilledUsd`, and most/least-efficient
   ordering to **exact** values.

2. **Trend bucketing (AC 4, 7):** seed daily facts across a sparse set of days → the trend series
   is ordered by date, has one point per active day, omits inactive days; an agent with zero rows
   in the window → empty series (`[]`, HTTP 200), never 404.

3. **Metric ordering (AC 2):** seed agents with deliberate ties → each `orderBy` option produces
   the documented order with the `agentId` asc final tie-break; an invalid `orderBy` → 400.

4. **Name resolution (AC 3, 7):** registry name resolved for a known agent; `(unknown)` for a
   missing/deleted `AgentId`; `(unattributed)` for the `NULL` bucket; the resolver is asserted to
   read **only** identity (a test injects a resolver that would throw if asked for a metric).

5. **Public-agent isolation (AC 3, 11 — docker-bound, `sg docker -c "dotnet test ..."`):** a
   public/system agent definition is run by tenant A and tenant B; seed each tenant's
   `analytics_usage_daily` independently; assert `GET /api/v1/orgs/{A}/analytics/agents` returns
   only A's metrics for that agent and `…/orgs/{B}/…` returns only B's — never a sum, never the
   other tenant's numbers (proven across two `t_<hex>` schemas).

6. **Tenant isolation + RBAC (AC 6):** a member of A hitting `/api/v1/orgs/{B}/analytics/*` is
   rejected by `RequireTenantMembershipFilter` (403/404); a platform owner has no per-tenant
   performance route and is denied a tenant's performance like any non-member; a member reads their
   own tenant's rollups (200).

7. **Window validation (AC 9):** `from >= to` → 400; window beyond the cap → clamped; `limit`/`topN`
   beyond max → clamped (the `GetTopTenantsAsync` clamp precedent).

**Mocks:** No external provider/Stripe calls (read-only aggregation). InMemory provider for the
aggregation/trend/ordering/name/summary shape; a real Postgres 17 Testcontainer for per-tenant
isolation across two schemas (EF InMemory cannot model the search-path isolation plane — same
rationale as `ConventionStoreMigrationTests`). TDD: write the metric-correctness + public-agent
isolation tests first.

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/IAgentPerformanceAnalyticsService.cs` | Create (NEW) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/AgentPerformanceAnalyticsService.cs` | Create (NEW — per-agent rollup + trend + tenant summary over the 36-1 facts) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/IAgentNameResolver.cs` | Create (NEW) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/AgentNameResolver.cs` | Create (NEW — registry identity only; `(unknown)`/`(unattributed)`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/AgentPerformanceDtos.cs` | Create (NEW — rollup row / trend point / summary) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/TenantAnalyticsEndpoints.cs` | Create (NEW — member-read tenant analytics routes) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (DI: `IAgentPerformanceAnalyticsService` + `IAgentNameResolver`; map the 3 routes under `/api/v1/orgs/{tenantId}`) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/AgentPerformanceAnalyticsServiceTests.cs` | Create (NEW) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/TenantAnalyticsEndpointsTests.cs` | Create (NEW) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/AgentPerformanceIsolationTests.cs` | Create (NEW — Postgres 17 Testcontainer) |

> Note: `apps/tamma-elsa/src/Tamma.Data/Entities/AnalyticsUsageDaily.cs` +
> `AnalyticsUsageHourly.cs` (Story 36-1, NEW/drafted), `ProviderDiagnostic.cs` (Epic 9), the Epic
> 32 agent registry, `RequireTenantMembershipFilter.cs`, and `PlatformAnalyticsService.cs` are
> **referenced/read, not modified** — this story consumes the pre-folded dimensional facts and the
> registry identity; it does not change the schema, the projection, or any sibling service.
> (`packages/api` is deleted; all work is in the C# `apps/tamma-elsa` stack.)

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes/bugs/findings/decisions (analytics, tenancy, EF, projection
   read models) — especially the per-tenant cross-tenant read-routing decision behind why a
   tenant's facts are structurally unreachable cross-tenant.
3. Read **Story 36-1** (the fact tables/measures/dimensions this story reads), **Story 36-2** (how
   the per-agent facts are bucketed by `agent_id`, including the `NULL` "unattributed" bucket), and
   sibling **Story 32-10** (the leaderboard read model — align the per-agent success-rate
   definition; do **not** duplicate its action-trail fold).
4. Read the Epic 32 design spec (`docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`)
   — the tenancy rule: definition ownership ≠ data ownership; **performance is per-tenant**;
   platform admin cannot read tenant performance.
5. Confirmed the test runner: docker-bound suites run via `sg docker -c "dotnet test ..."`
   (session docker group is stale; the build itself needs no wrapper).
6. Planned the TDD cycle (metric-correctness + public-agent isolation tests first).

### Key design decisions

- **Read the dimensional store, do not re-fold the trail.** The per-agent rollup reads the
  pre-folded `analytics_usage_*` facts (36-1/36-2) with a `GROUP BY AgentId` — it never re-scans
  the raw event stream and never re-implements 32-10's `BenchmarkProjectionService`. This keeps the
  reporting API a thin, cheap consumer and avoids two folds of the same trail drifting apart.
- **Sibling, not duplicate, of 32-10.** 32-10 is the **defect/latency/leaderboard** read model
  folded from the action trail; this is the **cost/throughput rollup/reporting** read model over
  the Epic 36 dimensional store. They share the per-tenant-isolation rule and overlap on per-agent
  success rate (which this story reads from the facts and aligns to 32-10's definition). Keeping
  them distinct is deliberate — the analytics product (36) and the agent-benchmark product (32) are
  different surfaces.
- **Performance is strictly per-tenant; identity is shared.** The registry is consulted *only* for
  the display name (public/system vs private/tenant). Every metric comes from the resolving
  tenant's own facts, read through `ITenantDbContextFactory`, so a public agent shows only this
  tenant's numbers — the design-spec tenancy rule, enforced structurally by the schema and a
  name-only resolver, and pinned by the public-agent isolation test.
- **No platform-admin per-tenant performance path.** A platform owner cannot read a tenant's agent
  performance — there is no admin variant of these routes. The only cross-tenant agent analytics
  that exists anywhere is 32-10's k-anonymous public-agent fleet rollup, which this story does not
  touch.
- **Graceful, not erroring, on missing data.** Zero activity → empty trend (HTTP 200);
  unknown/deleted agent → `(unknown)` placeholder (the `GetTopTenantsAsync` precedent); the
  `AgentId = NULL` bucket → `(unattributed)` so per-agent rows reconcile to the grand total. A
  reporting endpoint that 404s on an idle agent would be a worse experience than an honest empty
  series.
- **Bounded windows.** `from < to`, a window cap, and `limit`/top-N clamps (the
  `GetTopTenantsAsync` clamp convention) keep a malformed or unbounded request a 400, not a runaway
  scan over a large fact table.
- **Read-only, no recompute on the read path.** The dimensional rollup is rebuilt by the Story 36-2
  workflow (and its backfill input), not by this API — there is no rebuild trigger here. This story
  only aggregates pre-folded facts.

### Integration points

- **36-1/36-2** are the producers of the facts; this story is a pure read consumer of the
  dimensional store — coordinate the per-agent measure set (especially the duration measure feeding
  `avgDurationMs`) with 36-2 before merge.
- **Epic 32 agent registry** is the identity/name source (public/system vs private/tenant) — read
  through `IAgentNameResolver`, identity only.
- **`RequireTenantMembershipFilter` + `/api/v1/orgs/{tenantId}` group** is the member-read tenant
  path-gate these endpoints ride.
- **`ITenantDbContextFactory`** is the structural isolation plane for every fact read.
- **32-10** is the sibling agent read model; align the per-agent success-rate definition, do not
  duplicate the fold.

### Risks and Mitigations

| Risk | Severity | Mitigation |
| ---- | --------- | ---------- |
| Cross-tenant leakage of a public agent's performance | Critical | Per-tenant schema + `ITenantDbContextFactory`; registry resolves name only; no admin per-tenant route; explicit public-agent isolation test across two schemas |
| Duplicating 32-10's leaderboard fold | High | This story reads the 36-1 dimensional facts (`GROUP BY AgentId`), never re-folds the action trail; references 32-10; aligns the success-rate definition before merge |
| `avgDurationMs` source ambiguous (fact measure vs `ProviderDiagnostic`) | Medium | Coordinate the duration measure with 36-2; if absent, read `ProviderDiagnostic.RequestDurationMs` tenant-scoped for duration only — do not add a fact column under this story |
| Unbounded window scans a huge fact table | Medium | `from < to` validation, window cap, `limit`/top-N clamps (the `GetTopTenantsAsync` precedent) |
| Idle agent 404s instead of empty | Low | Zero-activity → empty trend (HTTP 200); deleted agent → `(unknown)`; `NULL` bucket → `(unattributed)` |
| 36-1/36-2 not yet merged | High (blocking) | Hard prerequisite; story does not start until the fact tables + projection exist (mark NEW; coordinate measure names) |

### Success Metrics

- [ ] Rollup/trend/summary metrics match hand-computed values on the fact-row fixture (exact)
- [ ] A public agent run by two tenants shows each tenant only its own metrics (isolation test green)
- [ ] 0 cross-tenant + 0 platform-admin per-tenant performance reads possible (RBAC suite green)
- [ ] Zero-activity agent returns an empty trend (HTTP 200); deleted agent renders `(unknown)`
- [ ] Metric ordering + tie-break deterministic; window validation returns 400 on bad input

## Logging Requirements

- **INFO**: rollup served (`tenantId`, `window`, agentCount, rowsRead); trend served (`tenantId`,
  `agentId`, points); summary served (`tenantId`, `window`, totalRuns)
- **DEBUG**: per-agent group aggregation step (`agentId`, runs); name resolution
  (`agentId` → resolved/`(unknown)`/`(unattributed)`); window parse + clamp
- **WARN**: window validation failed (`from`, `to`) → 400; `orderBy` unknown → 400; name resolver
  unavailable → fall back to `(unknown)` and log once
- **ERROR**: tenant fact read failure (repository/context error) — surfaced as 500, never a
  cross-tenant fallback
- **Structured context**: include `{ tenantId, window, agentId, orderBy, rowsRead }` where applicable
- **Credential / privacy safety**: NEVER log another tenant's id in a tenant's report path; NEVER
  log tenant connection strings or search-path schema secrets; the reporting reads touch only the
  resolving tenant's fact rows + the registry name lookup — no provider-key plaintext, no
  cross-tenant identifiers

## Related

- Design spec: `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`
  (§Ownership/data-scoping — performance is PER-TENANT; platform admin cannot read tenant performance)
- Implementation plan: `docs/superpowers/plans/2026-06-17-36-5-agent-and-tenant-performance-rollups-api-plan.md`
- Source stories: `docs/stories/epic-36/story-36-1` (fact tables), `docs/stories/epic-36/story-36-2`
  (projection pipeline)
- Sibling read model (NOT duplicated): `docs/stories/epic-32/story-32-10` (benchmark leaderboards)

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
