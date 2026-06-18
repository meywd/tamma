# Story 36-3: Tenant Usage Analytics API

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase workflow (Read → Research → Break Down → TDD →
Quality Gates → Failure Handling), the `.dev/` knowledge base, TRACE/DEBUG logging
requirements, test-first development, and the build-success / coverage gates.

## User Story

As a **tenant administrator** (SaaS) **/ self-hosted owner** (single-user),
I want a per-tenant, RBAC-gated query API that reads my pre-aggregated usage facts from Story
36-1's `analytics_usage_hourly` / `analytics_usage_daily` tables (populated by Story 36-2) — and
returns time-bucketed usage (workflows, agent dispatches, tokens) broken down by provider, agent,
workflow, and repo over an arbitrary window at hourly or daily grain,
so that the tenant dashboard (Story 36-6) and the CSV/JSON exporter (Story 36-8) read one stable,
isolated, tenant-scoped contract instead of re-scanning the DCB stream — and a member of org A can
never read org B's usage.

## Priority

P0 — The dimensional store (36-1) is populated (36-2) but inert without a read surface. Every
tenant-facing Epic 36 consumer (dashboard 36-6, exporter 36-8) reads through this API; nothing
renders a tenant usage chart until these endpoints ship.

## Scope

Read-only query API **only**. This story exposes two tenant-scoped HTTP endpoints over the Story
36-1 fact tables, resolves the principal per-mode (single-user → sole user, SaaS → tenant), gates
reads behind tenant membership (any member may read), and serves a stable response contract that
the dashboard and exporter consume. It owns:

- a `TenantAnalyticsService` that reads `AnalyticsUsageDaily` / `AnalyticsUsageHourly` through
  `ITenantDbContextFactory.CreateAsync(tenantId)` (physical per-tenant schema isolation) and
  shapes time-bucketed + breakdown results;
- a `TenantAnalyticsEndpoints` pair (`GET …/analytics/usage`, `GET …/analytics/usage/breakdown`)
  mounted under `/api/v1/orgs/{tenantId}/…`, gated by the existing `RequireTenantMembershipFilter`
  (the cross-tenant 403 guard) and the `MemberAccess` policy;
- request DTOs/validation (range clamping, granularity/groupBy enums, **forced-UTC DateTime
  binding** mirroring the `AdminAnalyticsEndpoints` fix) and a response contract with
  `period_start` / `period_end` echoes.

It does **not** write or alter the fact tables (36-1), populate them (36-2), implement exports
(36-8), render UI (36-6), or touch the control-plane `platform_analytics_hourly` /
`AdminAnalyticsEndpoints` owner surface (Story 28-10) — that stays the platform-owner business
analytics surface and is **not duplicated** here. It does not add per-tenant OPS/health metrics
(Epic 5 / Epic 23) — usage analytics only.

## Acceptance Criteria

1. A new `GET /api/v1/orgs/{tenantId}/analytics/usage` endpoint
   (`apps/tamma-elsa/src/Tamma.Api/Endpoints/TenantAnalyticsEndpoints.GetUsage`) returns
   **time-bucketed** usage rows — each row carrying `period`, `workflowsStarted`,
   `workflowsCompleted`, `workflowsFailed`, `agentDispatches`, `tokensIn`, `tokensOut` (and `costUsd`
   / `platformBilledUsd` measures from the 36-1 schema) — for the requested window, reading
   `AnalyticsUsageDaily` (granularity `day`) or `AnalyticsUsageHourly` (granularity `hour`) through
   `ITenantDbContextFactory.CreateAsync(tenantId)`. It **never** re-scans `domain_events`.

2. The endpoint accepts query params `from` (ISO-8601), `to` (ISO-8601),
   `granularity` (`hour` | `day`, default `day`), and an optional `groupBy`
   (`provider` | `agent` | `workflow` | `repo`). With `groupBy` absent, rows are summed across all
   dimensions per time bucket (one row per bucket); with `groupBy` set, rows are
   `GROUP BY (bucket, <dimension>)` so each bucket fans out into one row per dimension value, with
   the dimension value echoed in a `key` field (and the `NULL` "unattributed" bucket surfaced as a
   `null`/empty key, never dropped or coerced to a sentinel — preserving 36-2's reconciliation
   guarantee).

3. A new `GET /api/v1/orgs/{tenantId}/analytics/usage/breakdown` endpoint
   (`TenantAnalyticsEndpoints.GetBreakdown`) returns **top-N rows for a single dimension** over the
   window — `dimension` (`provider` | `agent` | `workflow` | `repo`, required), a `metric`
   selector (`tokens` | `runs` | `dispatches` | `cost`, default `tokens`), and `limit` (default 10,
   clamped 1..100) — each row carrying the dimension `key` and the aggregated `value` + the full
   measure set, ordered by the selected metric descending (e.g. tokens by agent, runs by repo).

4. **All queries are hard-scoped to the caller's tenant schema.** Both endpoints sit under
   `/api/v1/orgs/{tenantId}/…` behind `RequireTenantMembershipFilter`, which returns **403** when
   the authenticated caller has no membership row in the route `{tenantId}` (the cross-tenant
   guard, finding 001/024). A test asserts a member of tenant A requesting tenant B's route gets
   403 and that **no** tenant-B fact row is ever returned (no cross-tenant leakage) — physical
   isolation is the `ITenantDbContextFactory` per-tenant connection, defence-in-depth is the
   membership filter.

5. **RBAC: any tenant member may read.** Both GET endpoints carry the `MemberAccess` policy +
   `RequireTenantMembershipFilter` and **never** require `tenant_owner` / `tenant_admin` —
   mirroring the prompt-store "GET resolved prompt → any tenant member" RBAC and the
   `UserDashboardEndpoints` precedent. In single-user mode the sole user is the principal and no
   role is required; in SaaS mode `member`-role callers read successfully (no 403 on GET).

6. **Per-mode principal resolution** is identical in endpoint shape across both modes: the
   `{tenantId}` route segment is the principal in both modes (single-user resolves to the sole
   user's tenant schema, SaaS to the tenant's schema), the auth middleware/membership filter binds
   the caller, and the per-tenant `TenantDbContext` enforces isolation physically — no per-mode
   branch in the handler (the CLAUDE.md prompt-store "endpoint shape identical between modes"
   precedent).

7. **Responses are bounded.** The requested window is clamped to a maximum range of **365 days**
   (a longer `from..to` is truncated to the most-recent 365 days and the effective window echoed),
   `from` defaults to 30 days ago and `to` to now when omitted, `granularity` clamps so an `hour`
   query over a window wider than a configured cap (default 31 days) is rejected with 400 (an
   unbounded hourly scan is the anti-pattern), and `groupBy`/`dimension`/`metric` values outside
   the allowed enums return 400 with a clear error.

8. Every response echoes `period_start` and `period_end` (the **effective** UTC window after
   clamping, ISO-8601) plus `granularity`, `groupBy`/`dimension`, and a `rows` array — a stable,
   self-describing schema the dashboard (36-6) and the CSV exporter (36-8) consume without
   re-deriving the window. The DTOs live in
   `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/TenantAnalyticsDtos.cs` and the contract is
   version-stable (additive-only).

9. **DateTime binding forces UTC.** `from`/`to` bind as `DateTime?` and are normalized via
   `DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc)` (the exact
   `AdminAnalyticsEndpoints.GetEventHistogram` fix) so the query window matches the stored
   `Hour`/`Day` UTC buckets (`timestamp with time zone`, top-of-hour / midnight UTC per 36-1) — a
   `from` parsed as Local never shifts the window off the bucket boundary.

10. The `TenantAnalyticsService`
    (`apps/tamma-elsa/src/Tamma.Api/Services/Analytics/TenantAnalyticsService.cs`,
    `ITenantAnalyticsService` seam) is the single read seam: it takes
    `ITenantDbContextFactory`, builds the grouped/aggregated query against the appropriate fact
    table for the granularity, applies the `[from, to)` half-open window filter on the bucket
    column, and returns the DTO — so the endpoints are thin and the aggregation is unit-testable in
    isolation against a seeded InMemory context.

11. The control-plane owner analytics surface (`AdminAnalyticsEndpoints`,
    `IPlatformAnalyticsService`, `platform_analytics_hourly`, Story 28-10) is **left entirely
    intact** — this story adds a parallel tenant-scoped surface and does **not** modify, re-route,
    or read the CP business-analytics path. A test asserts the new endpoints never touch
    `IPlatformAnalyticsService` / `ControlPlaneDbContext`.

12. Integration tests (Postgres 17 Testcontainer, two tenant schemas) cover: **grouping
    correctness** against seeded `analytics_usage_*` fact rows (per-dimension rows sum to the
    grand total — the 36-2 reconciliation contract held at read time, including the `NULL`
    dimension bucket); **range clamping** (a 400-day window truncates to 365; an hourly query past
    the hour cap → 400); **UTC handling** (a `from` with a non-UTC offset selects the same buckets
    as its UTC equivalent); and the **cross-tenant 403** (tenant-A caller on tenant-B route → 403,
    no tenant-B rows returned).

## Tasks / Subtasks

- [ ] Task 1: Request/response DTOs + validation (AC: 2, 3, 7, 8, 9)
  - [ ] Add `TenantAnalyticsDtos.cs`: `UsageQuery` (from/to/granularity/groupBy),
        `BreakdownQuery` (dimension/metric/limit), `UsageBucketRow`, `BreakdownRow`,
        `UsageResponse`/`BreakdownResponse` (with `period_start`/`period_end`/`granularity` echoes).
  - [ ] Add an `AnalyticsGranularity` (`Hour`|`Day`), `AnalyticsDimension`
        (`Provider`|`Agent`|`Workflow`|`Repo`), `AnalyticsMetric`
        (`Tokens`|`Runs`|`Dispatches`|`Cost`) parse/validate helper returning 400 on bad input.
  - [ ] Window-clamp helper: default window, 365-day max range, hour-granularity cap; forced-UTC
        normalization (mirror `AdminAnalyticsEndpoints`). Unit-test the clamp matrix in isolation.

- [ ] Task 2: `ITenantAnalyticsService` + `TenantAnalyticsService` (AC: 1, 2, 3, 10)
  - [ ] Read `AnalyticsUsageDaily` (day) / `AnalyticsUsageHourly` (hour) via
        `ITenantDbContextFactory.CreateAsync(tenantId)`; `[from, to)` filter on `Day`/`Hour`.
  - [ ] Time-bucketed aggregation (no groupBy → sum per bucket; groupBy → `GROUP BY bucket, dim`);
        breakdown top-N by metric desc. `NULL` dimension surfaced, never dropped/sentinel-coerced.
  - [ ] Unit tests against seeded InMemory contexts: grouping correctness + reconciliation.

- [ ] Task 3: `TenantAnalyticsEndpoints` + Program.cs wiring (AC: 1, 3, 4, 5, 6, 11)
  - [ ] `GetUsage` / `GetBreakdown` handlers (thin; service does the work).
  - [ ] Map under `orgs` group with `RequireTenantMembershipFilter` + `MemberAccess` (mirror
        `UserDashboardEndpoints` mapping); register `ITenantAnalyticsService` in DI.
  - [ ] CP-untouched assertion (no `IPlatformAnalyticsService`/`ControlPlaneDbContext` reference).

- [ ] Task 4: Integration tests (AC: 4, 12)
  - [ ] Postgres 17 Testcontainer, two tenant schemas seeded with fact rows.
  - [ ] Grouping correctness + reconciliation; range clamp; UTC handling; cross-tenant 403 +
        no-leakage.

## Technical Design

### C# namespace / file structure

```
apps/tamma-elsa/src/
  Tamma.Api/Endpoints/TenantAnalyticsEndpoints.cs              # NEW — GET usage + breakdown handlers
  Tamma.Api/Services/Analytics/ITenantAnalyticsService.cs      # NEW — read seam
  Tamma.Api/Services/Analytics/TenantAnalyticsService.cs       # NEW — per-tenant aggregation
  Tamma.Api/Services/Analytics/TenantAnalyticsDtos.cs          # NEW — query + response DTOs + enums
  Tamma.Api/Program.cs                                         # MODIFY — map 2 routes + register service

apps/tamma-elsa/tests/Tamma.Api.Tests/
  Analytics/TenantAnalyticsServiceTests.cs                     # NEW — grouping/clamp/UTC unit tests (InMemory)
  Analytics/TenantAnalyticsEndpointsTests.cs                   # NEW — RBAC matrix + cross-tenant 403 + CP-untouched
  Analytics/TenantAnalyticsIntegrationTests.cs                 # NEW — Postgres 17: grouping correctness, isolation
```

### Endpoint shape (mirrors `UserDashboardEndpoints` — route `{tenantId}` + factory)

```csharp
// GET /api/v1/orgs/{tenantId}/analytics/usage
//   ?from=2026-05-01T00:00:00Z&to=2026-06-01T00:00:00Z&granularity=day&groupBy=provider
public static async Task<IResult> GetUsage(
    Guid tenantId,
    [FromQuery] DateTime? from,
    [FromQuery] DateTime? to,
    [FromQuery] string? granularity,
    [FromQuery] string? groupBy,
    ITenantAnalyticsService analytics,
    CancellationToken ct)
{
    // Forced-UTC binding (mirror AdminAnalyticsEndpoints.GetEventHistogram) so the
    // window lands on the stored top-of-hour / midnight-UTC buckets, not a Local shift.
    var window = AnalyticsWindow.Resolve(from, to, granularity);   // clamps 365d / hour-cap / defaults
    if (!window.IsValid) return Results.BadRequest(new { error = window.Error });

    var result = await analytics.GetUsageAsync(
        tenantId, window, AnalyticsDimension.TryParse(groupBy), ct);
    return Results.Ok(result);   // echoes period_start/period_end/granularity/groupBy + rows
}

// GET /api/v1/orgs/{tenantId}/analytics/usage/breakdown
//   ?dimension=agent&metric=tokens&limit=10&from=…&to=…
public static async Task<IResult> GetBreakdown(
    Guid tenantId,
    [FromQuery] DateTime? from,
    [FromQuery] DateTime? to,
    [FromQuery] string dimension,
    [FromQuery] string? metric,
    [FromQuery] int? limit,
    ITenantAnalyticsService analytics,
    CancellationToken ct) { /* parse+clamp → analytics.GetBreakdownAsync(...) */ }
```

`from`/`to` are normalized exactly as `AdminAnalyticsEndpoints.GetEventHistogram` does:
`DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc)` — the documented UTC-binding fix
(AC9).

### Service seam + per-tenant read (mirrors `UserDashboardEndpoints.GetStats`)

```csharp
public interface ITenantAnalyticsService
{
    Task<UsageResponse> GetUsageAsync(
        Guid tenantId, AnalyticsWindow window, AnalyticsDimension? groupBy, CancellationToken ct);

    Task<BreakdownResponse> GetBreakdownAsync(
        Guid tenantId, AnalyticsWindow window, AnalyticsDimension dimension,
        AnalyticsMetric metric, int limit, CancellationToken ct);
}

public sealed class TenantAnalyticsService(ITenantDbContextFactory tenantDbFactory)
    : ITenantAnalyticsService
{
    public async Task<UsageResponse> GetUsageAsync(
        Guid tenantId, AnalyticsWindow w, AnalyticsDimension? groupBy, CancellationToken ct)
    {
        await using var db = await tenantDbFactory.CreateAsync(tenantId);

        if (w.Granularity == AnalyticsGranularity.Day)
        {
            var q = db.AnalyticsUsageDaily
                .Where(r => r.Day >= w.From && r.Day < w.To);     // half-open [from, to)
            var rows = groupBy switch
            {
                AnalyticsDimension.Provider => q
                    .GroupBy(r => new { r.Day, r.Provider })
                    .Select(g => new UsageBucketRow(/* bucket, key=Provider, summed measures */)),
                AnalyticsDimension.Agent => q.GroupBy(r => new { r.Day, r.AgentId }) /* … */,
                // workflow → WorkflowDefinitionId, repo → RepoId; NULL key preserved.
                _ => q.GroupBy(r => r.Day).Select(g => new UsageBucketRow(/* bucket, summed */)),
            };
            return new UsageResponse(w.From, w.To, w.Granularity, groupBy, await Materialize(rows, ct));
        }
        // granularity == Hour → identical against db.AnalyticsUsageHourly (r.Hour bucket).
    }
    // GetBreakdownAsync: GROUP BY <dimension> over the window, OrderByDescending(metric), Take(limit).
}
```

Measure summation matches the 36-1 columns (`TokensIn`, `TokensOut`, `WorkflowsStarted`,
`WorkflowsCompleted`, `WorkflowsFailed`, `AgentDispatches` as `long`; `CostUsd`,
`PlatformBilledUsd` as `decimal(20,4)`). There is **no `TenantId` column** on the fact tables
(36-1 Doc 01 §1.4); isolation is the per-tenant schema the factory binds — the `Where` is only on
the bucket + dimension, not a tenant predicate.

### `AnalyticsWindow` clamp (AC7, AC9)

```csharp
public readonly record struct AnalyticsWindow(
    DateTime From, DateTime To, AnalyticsGranularity Granularity, bool IsValid, string? Error)
{
    public static AnalyticsWindow Resolve(DateTime? from, DateTime? to, string? granularity)
    {
        var toUtc   = ToUtc(to)   ?? DateTime.UtcNow;
        var fromUtc = ToUtc(from) ?? toUtc.AddDays(-30);            // default 30d
        if (fromUtc >= toUtc) return Invalid("from must precede to");

        // Max range 365d — truncate to the most-recent 365d, echo the effective window.
        if ((toUtc - fromUtc).TotalDays > 365) fromUtc = toUtc.AddDays(-365);

        var gran = ParseGranularity(granularity);                   // default Day; 400 on bad enum
        // Hourly scans are capped (default 31d) — an unbounded hourly window is the anti-pattern.
        if (gran == AnalyticsGranularity.Hour && (toUtc - fromUtc).TotalDays > 31)
            return Invalid("granularity=hour requires a window <= 31 days");

        return new(fromUtc, toUtc, gran, true, null);
    }

    private static DateTime? ToUtc(DateTime? v) => v is null
        ? null : DateTime.SpecifyKind(v.Value.ToUniversalTime(), DateTimeKind.Utc);
}
```

### Response contract (AC8 — stable, dashboard + exporter consumable)

```jsonc
// GET …/analytics/usage?granularity=day&groupBy=provider
{
  "tenantId": "…",
  "period_start": "2026-05-18T00:00:00Z",  // effective UTC window after clamp
  "period_end":   "2026-06-17T00:00:00Z",
  "granularity":  "day",
  "groupBy":      "provider",
  "rows": [
    { "period": "2026-05-18T00:00:00Z", "key": "anthropic-claude",
      "workflowsStarted": 12, "workflowsCompleted": 11, "workflowsFailed": 1,
      "agentDispatches": 34, "tokensIn": 120000, "tokensOut": 45000,
      "costUsd": 1.2340, "platformBilledUsd": 1.4808 },
    { "period": "2026-05-18T00:00:00Z", "key": null, /* unattributed bucket — never dropped */ … }
  ]
}
```

### Routing + RBAC wiring (Program.cs — mirror `UserDashboardEndpoints`)

```csharp
// orgs = app.MapGroup("/api/v1/orgs").RequireAuthorization("MemberAccess");  // (existing)
orgs.MapGet("/{tenantId:guid}/analytics/usage", TenantAnalyticsEndpoints.GetUsage)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();
orgs.MapGet("/{tenantId:guid}/analytics/usage/breakdown", TenantAnalyticsEndpoints.GetBreakdown)
    .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();

builder.Services.AddScoped<ITenantAnalyticsService, TenantAnalyticsService>();
```

`MemberAccess` requires only an authenticated user (Program.cs line ~991); the cross-tenant guard
is `RequireTenantMembershipFilter` (403 when the caller has no membership in the route `{tenantId}`
— `apps/tamma-elsa/src/Tamma.Api/Authorization/RequireTenantMembershipFilter.cs`). This is the
exact gate the existing `/api/v1/orgs/{tenantId}/dashboard/*` and `/api/v1/orgs/{tenantId}/alerts`
routes use. **No** owner/admin policy is added — GET is member-readable (AC5).

> **Why not the admin analytics surface?** `AdminAnalyticsEndpoints` +
> `IPlatformAnalyticsService` (Story 28-10) read **across all tenants** behind `OwnerAccess`/admin
> and serve platform-owner business analytics off the CP `platform_analytics_hourly` table. This
> story is the **tenant-facing** mirror: per-tenant, member-readable, reading the per-tenant
> `analytics_usage_*` tables. The two surfaces are deliberately separate (CLAUDE.md two-scoping
> rule); this story never reads CP analytics and never duplicates the OPS/health metrics of
> Epic 5 / Epic 23.

### Per-mode + per-tenant handling

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who is the principal? | The sole user — `{tenantId}` resolves to their (only) tenant schema. | The tenant — `{tenantId}` is the tenant's `t_<hex>` schema. |
| Who can read (GET)? | The user (no RBAC). | Any tenant member (`MemberAccess` + membership filter); `member` is read-only and reads succeed (no 403 on GET). |
| Endpoint shape | identical | identical — auth middleware/membership filter binds the caller, handler has no per-mode branch (prompt-store precedent). |
| Isolation plane | search-path schema + connection string (no `TenantId` column, no query filter). | same — physically separate schema per tenant; a row in schema A is unreachable through schema B's context. |
| Cross-tenant leakage | N/A (one tenant). | `RequireTenantMembershipFilter` 403s a non-member of the route tenant; the per-tenant `TenantDbContext` physically can't read another schema (AC4/AC12). |

### Source tables (read-only; owned by 36-1, populated by 36-2)

- **`AnalyticsUsageDaily`** (`db.AnalyticsUsageDaily`, bucket `Day` UTC-midnight) — default grain.
- **`AnalyticsUsageHourly`** (`db.AnalyticsUsageHourly`, bucket `Hour` UTC top-of-hour) — fine grain.
- Dimensions: `Provider` (required), `AgentId` / `WorkflowDefinitionId` / `RepoId` (nullable),
  `CostBasis` (`byok`|`platform`). Breakdown index `IX_analytics_usage_*_breakdown` (36-1 AC6)
  serves these grouped reads.
- `ProviderDiagnostic` (`apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs`) is a 36-2
  *projection source*, **not** read by this query API — this story reads only the pre-aggregated
  facts (AC1).

## Dependencies

**Prerequisite (internal):**
- **Story 36-1** — the per-tenant `AnalyticsUsageHourly` / `AnalyticsUsageDaily` fact tables +
  `CostBasis` enum + breakdown indexes this API reads. (Drafted.)
- **Story 36-2** — the projection pipeline that populates those tables from DCB events +
  `ProviderDiagnostic`. The query API returns the rows 36-2 writes; reconciliation (per-dimension
  rows sum to total) is 36-2's contract verified here at read time. (Drafted.)
- **Epic 28** — `TenantContextMiddleware`
  (`apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs`), `ITenantContext`, and
  `ITenantDbContextFactory` (`apps/tamma-elsa/src/Tamma.Data/Abstractions/ITenantDbContextFactory.cs`
  / `TenantDbContextFactory.cs`) — the per-tenant schema routing this API resolves through. (Merged.)
- **Epic 18** — tenant membership RBAC: `RequireTenantMembershipFilter`
  (`apps/tamma-elsa/src/Tamma.Api/Authorization/RequireTenantMembershipFilter.cs`) +
  `MemberAccess` policy (Program.cs) — the member-read gate + cross-tenant 403. (Merged.)

**Blocks (internal):**
- **Story 36-6** (tenant analytics dashboard, `packages/dashboard-user`) — consumes this contract.
- **Story 36-8** (usage exports CSV/JSON) — re-uses the same `ITenantAnalyticsService` query +
  `period_start`/`period_end` echo to label exports.

**Left intact (NOT a dependency — explicitly untouched):**
- **Story 28-10** — `AdminAnalyticsEndpoints`, `IPlatformAnalyticsService`,
  `platform_analytics_hourly` (platform-owner business analytics). This story adds a parallel
  tenant surface; it does not read, modify, or re-route the CP path (AC11).

**External:**
- PostgreSQL 17 (per-tenant schema; `timestamp with time zone` bucket columns).
- EF Core 9 / Npgsql.
- Testcontainers + Docker for the isolation/grouping integration suite (run via
  `sg docker -c "dotnet test ..."`).

## Testing Strategy

1. **Unit — window clamp (`TenantAnalyticsServiceTests`):** default window (30d / now); 400-day
   `from..to` truncates to most-recent 365d with the effective window echoed; `granularity=hour`
   past the 31-day cap → 400; `from >= to` → 400; bad `granularity`/`groupBy`/`dimension`/`metric`
   enum → 400.

2. **Unit — UTC binding:** a `from` with a non-UTC offset (e.g. `+02:00`) normalizes to the same
   UTC instant as its `Z` equivalent and selects the identical bucket set (the
   `AdminAnalyticsEndpoints` fix proven for this surface).

3. **Unit — grouping correctness (InMemory, seeded `AnalyticsUsageDaily`/`Hourly`):**
   - `groupBy` absent → one summed row per bucket.
   - `groupBy=provider|agent|workflow|repo` → one row per `(bucket, dimension)`; the `NULL`
     dimension bucket is surfaced (key `null`), never dropped or coerced to `"unknown"`.
   - **Reconciliation:** `Σ(grouped rows per bucket) == ungrouped row for that bucket` (the 36-2
     reconciliation contract held at read time).
   - breakdown: top-N by each `metric` (`tokens`/`runs`/`dispatches`/`cost`) ordered desc,
     `limit` clamp respected.

4. **Endpoint — RBAC matrix (`TenantAnalyticsEndpointsTests`):** member / tenant_admin /
   tenant_owner all read 200; non-member of the route tenant → 403; unauthenticated → 401;
   single-user mode (sole user) reads 200. Asserts the handlers reference **only**
   `ITenantAnalyticsService` (no `IPlatformAnalyticsService` / `ControlPlaneDbContext`) — CP
   untouched (AC11).

5. **Integration — isolation + grouping (`TenantAnalyticsIntegrationTests`, Postgres 17
   Testcontainer, two tenant schemas, per `SchemaPerTenantMigrationTests`):** seed distinct fact
   rows in schema A and schema B; a tenant-A caller on tenant-A route sees only A's rows; a
   tenant-A caller on tenant-B route → 403 and **zero** B rows returned (AC4/AC12); grouping
   correctness + reconciliation against real Npgsql `GROUP BY`; range clamp + UTC bucket selection
   on real `timestamp with time zone` columns.

**Mocks:** No external provider/Stripe calls (read-only aggregation). InMemory provider for clamp
/ grouping shape; a real Postgres 17 Testcontainer for per-tenant isolation + real `GROUP BY` /
`timestamp with time zone` window selection (EF InMemory does not model schema isolation or
date-bucket semantics faithfully — same rationale as `ConventionStoreMigrationTests`).

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/TenantAnalyticsEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/ITenantAnalyticsService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/TenantAnalyticsService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/TenantAnalyticsDtos.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map 2 org routes + register `ITenantAnalyticsService`) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/TenantAnalyticsServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/TenantAnalyticsEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/TenantAnalyticsIntegrationTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions (analytics, tenancy, RBAC,
   cross-tenant leakage).
3. Reviewed Story 36-1 (the fact tables/columns/indexes this API reads), Story 36-2 (the
   reconciliation contract this API verifies at read time), and the precedents this story mirrors:
   `UserDashboardEndpoints` (route `{tenantId}` + `ITenantDbContextFactory` + membership filter),
   `AdminAnalyticsEndpoints` (the forced-UTC `from`/`since` binding fix), and
   `RequireTenantMembershipFilter` (the cross-tenant 403 guard).
4. Confirmed the test runner: docker-bound suites run via `sg docker -c "dotnet test ..."`
   (session docker group is stale; the build itself needs no wrapper).
5. Planned the TDD cycle (write `TenantAnalyticsServiceTests` grouping/clamp/UTC red first, then
   the service, then the endpoints + integration suite).

### Key Design Decisions

- **Mirror `UserDashboardEndpoints`, not `AdminAnalyticsEndpoints`.** The tenant query API is a
  route-`{tenantId}` + `ITenantDbContextFactory` + `RequireTenantMembershipFilter` surface (the
  user-dashboard shape), NOT the cross-tenant `OwnerAccess` admin shape. The admin analytics
  endpoints stay the platform-owner business surface (28-10) — this story does not read or touch
  them. Two surfaces, two scoping models (CLAUDE.md two-scoping rule).
- **Member-read RBAC.** GET carries `MemberAccess` + the membership filter and **never** an
  owner/admin policy — usage analytics is read-only and tenant-wide, mirroring the prompt-store
  "GET resolved → any tenant member". This is a deliberate divergence from `AlertEndpoints`, whose
  mutating tenant routes inline-gate admin+ via `RequireTenantAdmin`; this story has no mutations.
- **Read pre-aggregated facts, never re-scan the stream.** The whole point of 36-1/36-2 is that a
  tenant chart reads one indexed `GROUP BY` over `analytics_usage_*`, not a `domain_events` scan.
  The service touches only the fact tables (and `ProviderDiagnostic` is a 36-2 source, not read
  here).
- **Forced-UTC binding is load-bearing.** The 36-1 buckets are UTC top-of-hour / midnight. A
  `from`/`to` parsed as Local would shift the window off the bucket boundary and silently drop or
  double-count edge buckets — the `AdminAnalyticsEndpoints.GetEventHistogram` fix is replicated
  verbatim (AC9). Pin it with the offset-equivalence test.
- **Bounded by construction.** 365-day max range + hour-granularity cap keep a malicious or naive
  `from=1970..to=now&granularity=hour` from issuing an unbounded scan. Clamping (not erroring) the
  range keeps the dashboard forgiving; rejecting an oversized hourly window (400) keeps the cost
  predictable.
- **`NULL` dimension preserved.** A grouped query surfaces the `NULL` "unattributed" bucket as a
  `null` key — never dropped, never `"unknown"` — so the per-dimension breakdown reconciles to the
  ungrouped total, honoring 36-2's reconciliation guarantee. Coercing `NULL` to a sentinel would
  fork the total.

### Query-only boundary

This story creates **no** schema change (reads 36-1's tables), **no** projection/population logic
(36-2 owns it), **no** export format (36-8), **no** dashboard UI (36-6 in `packages/dashboard-user`),
and **no** change to the control-plane `AdminAnalyticsEndpoints` / `platform_analytics_hourly`
owner surface (28-10). Any PR that adds population, an export endpoint, or a UI under cover of this
story is out of scope — keep the diff to the endpoints + service + DTOs + Program wiring + tests.

## Logging Requirements

- **INFO**: usage query served (`tenantId`, `granularity`, `groupBy`, effective
  `periodStart`/`periodEnd`, `rowCount`, `durationMs`); breakdown query served (`tenantId`,
  `dimension`, `metric`, `limit`, `rowCount`).
- **DEBUG**: requested-vs-effective window after clamp (`requestedFrom`, `requestedTo`,
  `effectiveFrom`, `effectiveTo`, `clamped`); fact table chosen (`hourly`|`daily`).
- **WARN**: window clamped to 365-day max (`requestedDays`); hourly window rejected over cap
  (`requestedDays`, `cap`); empty result for a non-trivial window (possible un-projected tenant —
  hint to check the 36-2 rollup lag SLO).
- **ERROR**: tenant DB context resolution failure (`tenantId`) surfaced as 500 — never leak the
  tenant connection string into the response or log.
- **Structured context**: include `{ tenantId, granularity, groupBy, dimension, metric,
  periodStart, periodEnd, rowCount, durationMs }` where applicable.
- **Credential safety**: NEVER log the per-tenant connection string or search-path schema secret
  (AES-GCM-encrypted at rest via `TenantSecretProtector`); the cross-tenant 403 message must not
  echo the other tenant's identity beyond the route value the caller already supplied.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
