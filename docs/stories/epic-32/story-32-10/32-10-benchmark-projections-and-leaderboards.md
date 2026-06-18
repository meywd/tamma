# Story 32-10: Benchmark Projections & Leaderboards (per agent/provider/prompt, per-tenant)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

This story targets the C# **`apps/tamma-elsa`** stack (Tamma.Api + Tamma.Data + Tamma.Core). The TypeScript `packages/api` is **deleted** — do not reference it. DCB event sourcing, the agent action trail (32-6), outcome/defect capture (32-8), usage/cost emission (32-9), the per-tenant `TenantDbContext`, and the `IEventRepository` tenant-scoped store all live in `apps/tamma-elsa`.

## User Story

As a **tenant owner/member choosing which agent, provider, prompt, or persona to trust with my design and review work**,
I want my own action-trail + outcome + usage data folded into per-tenant **benchmark projections** — success rate, average iterations-to-done, defect rate by taxonomy category, p50/p95 latency, and average cost-basis / billable per task — and exposed as **leaderboards** that rank agents, providers, prompts, and personas *within my tenant only*,
So that I can pick the best-performing configuration on real downstream quality and cost, while being certain my performance data never leaks to another tenant or to the platform admin who owns a public agent definition.

## Priority

P1 — the read-model payoff of the Epic 32 tracking stack. 32-6 captures the trail, 32-8 scores outcomes + classifies defects, 32-9 emits usage/cost; this story turns all three event families into the leaderboards a tenant actually reads to make decisions. Without it the captured data is queryable row-by-row but not comparable across agents/providers/prompts.

## Acceptance Criteria

1. A **projection engine** (`BenchmarkProjectionService`) deterministically folds the resolving tenant's `DomainEvents` — the `AGENT.TASK.SUCCESS`/`.FAILED`/`.PARTIAL` and `AGENT.OUTCOME.RECORDED` (32-6/32-8), `AGENT.DEFECT.RECORDED` (32-8), `AGENT.ITERATION.COMPLETED`/`AGENT.PANEL.AGGREGATED` (32-6/32-7), and `AGENT.USAGE.RECORDED` (32-9) families — into materialized **per-agent / per-provider / per-prompt / per-persona** benchmark rows stored in the **tenant's own schema** (`t_<hex>`), never on the control plane. Every projection row carries `TenantId` = the resolving tenant.
2. Each benchmark row computes, over a selectable window: `successRate` (success ÷ terminal runs), `avgIterationsToDone` (mean of `iterationsToDone` over successful runs), `defectRateByCategory` (one rate per the 6 taxonomy buckets `visual|functional|regression|security|performance|style`, plus `other`), `latencyP50Ms` / `latencyP95Ms`, `avgCostBasisUsd` (provider/model cost basis), `avgBillableUsd` (marked-up/billable amount from 32-9), and `runCount`. Percentiles are computed over the run-latency distribution, not averaged.
3. Projections fold **idempotently** with a **`SequenceNumber` cursor** per dimension: each projection-target row tracks the highest `SequenceNumber` it has consumed; re-folding the same events is a no-op (same input → same output). New events advance the cursor and update incrementally. A **full rebuild** (cursor reset → replay from 0) produces byte-identical rows to the incremental path. (AC tested for equivalence.)
4. A **leaderboard query API** `GET /api/v1/orgs/{tenantId}/agents/leaderboard?dimension=agent|provider|prompt|persona&window=…` returns ranked, **tenant-scoped** results: ordered by the primary metric (default `successRate` desc) with documented, deterministic **tie-breaking** (`successRate` desc → `avgIterationsToDone` asc → `avgCostBasisUsd` asc → dimension key asc) and a **minimum-sample-size guard** (`minRuns`, default 5): rows below the threshold are returned in a separate `belowThreshold` bucket (or flagged `provisional: true`), never silently ranked as if statistically meaningful.
5. **Tenant isolation is strict and structural.** All reads/writes go through `IEventRepository` / `ITenantDbContextFactory`, which scope to the resolving tenant's schema; there is **no cross-tenant and no platform-admin read path** for a tenant's benchmark rows. A platform owner (`OwnerAccess`/`PlatformOwnerAccess`) calling a tenant's leaderboard for another tenant gets 403/404, never that tenant's data. This holds even when the benchmarked agent is a **public/system** definition: one definition → many independent per-tenant datasets (design-spec tenancy rule). **Explicitly tested.**
6. The **platform-admin cross-tenant view is limited to public agents and aggregated/anonymized**: `GET /api/v1/admin/agents/{agentId}/benchmark` (`PlatformOwnerAccess`) returns a fleet-wide rollup for a **public** agent definition **only**, computed across tenants with a **k-anonymity threshold** (`minTenants`, default 5) — below k tenants contributing, the rollup is suppressed (404/empty). It **never** returns a single tenant's row, a tenant identifier, or a private-agent rollup. **Explicitly tested** (the negative case: k-1 tenants → suppressed; private agent → 404; no `tenantId` ever in the payload).
7. The projection read models are consistent with the **backend-development projection patterns**: idempotent fold, cursor-tracked, replayable from the event stream, with the projection as a derived materialized view that can be dropped and rebuilt from events at any time (the events are the source of truth; the projection is a cache).
8. Benchmark rows are **member-readable** (any tenant member can GET the leaderboard); there is **no tenant mutation surface** for projection rows (they are derived, not edited). An admin/operator **rebuild** trigger exists (`POST /api/v1/orgs/{tenantId}/agents/leaderboard/rebuild`, tenant_owner/tenant_admin) that resets cursors and replays — `member` → 403.
9. Projection updates are **non-blocking to agent runs**: the fold runs on a background projector (or on-demand at query time against the trail with a cache), never inside the managed-agent execution path. A projection-write failure is logged + retried and never fails or delays a run.
10. **Tests** cover: (a) **metric correctness** against fixture event streams (known success/fail/partial mix, known iteration counts, known defect categories, known latency distribution for p50/p95, known cost/billable) → asserted exact metric values; (b) **incremental-vs-full-rebuild equivalence** (replay produces identical rows); (c) **leaderboard ordering + tie-break + min-sample guard**; (d) **tenant isolation** (tenant B cannot read tenant A's benchmark via any path; platform admin denied per-tenant); (e) **k-anonymity** on the admin public-agent rollup (below-k suppressed; no tenant id leaks); (f) **idempotency** (double-fold = single result).

## Technical Design

### Where the data comes from and where the projection lives (verified against repo @ main 98cfb1c2, 2026-06-17)

The benchmark is a **read model derived from the tenant's DCB event stream** — the same `domain_events` table the action trail (32-6), outcome/defect capture (32-8), and usage/cost emission (32-9) write into. Isolation is **structural**: the projection table lives in the tenant's `t_<hex>` schema, written + read through `ITenantDbContextFactory`, exactly like every other tenant-resident entity on `TenantDbContext`.

| Component | File (verified) | Role in this story |
|---|---|---|
| DCB event row (source of truth) | `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` | `Type`, `TenantId`, JSONB `Tags`/`Data`, `CreatedAt`, **`SequenceNumber` (`BIGSERIAL`)** — the fold cursor. Reused verbatim, **no schema change**. |
| Tenant-scoped event store | `apps/tamma-elsa/src/Tamma.Data/Repositories/{IEventRepository,EventRepository}.cs` | `QueryWithPaginationAsync`/`ListByTenantAsync` scope to one tenant and **throw `NotSupportedException` on a null tenant** — the isolation backbone the fold reads through. We add `QueryAgentEventsForProjectionAsync` (cursor-paged, tenant-scoped, type-prefix filtered). |
| Per-tenant DbContext | `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | Owns per-tenant business data; we add `DbSet<BenchmarkProjection>` + `DbSet<BenchmarkProjectionCursor>`. Target shape (Doc 01 §1.4) has **no `TenantId` column** on tenant tables — but we keep a transitional `TenantId` for the shared-DB phase, mirroring `agent_configs`/`domain_events`. |
| Schema-per-tenant routing | `ITenantDbContextFactory` / `ITenantContext` (`Tamma.Data`) | Structural isolation plane — no new plumbing. |
| DCB emission precedent | `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs` (`UpdateConfig`, ~93-113) | Canonical `DomainEvent { Type, TenantId, Tags=flat strings, Metadata=envelope, Data }` shape; this story **reads** that shape, emits only `BENCHMARK.PROJECTION.*` lifecycle events. |
| Cost/latency source | `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs` (`CorrelationId`, `AgentType`, `InputTokens`/`OutputTokens`, `Cost`, `RequestDurationMs`) | Latency + cost-basis substrate; 32-9 surfaces these as `AGENT.USAGE.RECORDED` events keyed by `(agentId, correlationId)` — this story folds the *events*, not the diagnostics table directly, so the fold stays replayable. |
| Tenant-scope endpoint precedent | `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs` + tenant `/api/v1/orgs/{tenantId}/…` group with `RequireTenantMembershipFilter` (`apps/tamma-elsa/src/Tamma.Api/Authorization/RequireTenantMembershipFilter.cs`) | The exact member-readable tenant-path pattern the leaderboard endpoints ride. |
| Auth policies | `apps/tamma-elsa/src/Tamma.Api/Program.cs` (`OwnerAccess`, `PlatformOwnerAccess`, `MemberAccess`, ~971-991) | `MemberAccess` for tenant leaderboard read; tenant_owner/tenant_admin for rebuild; `PlatformOwnerAccess` for the public-agent fleet rollup. |
| Analytics seam | `apps/tamma-elsa/src/Tamma.Api/Services/AnalyticsService.cs` + `apps/tamma-elsa/src/Tamma.Core/Interfaces/IAnalyticsService.cs` | The existing analytics service (mentorship-oriented, mostly TODO stubs). Benchmark projection is a **distinct, agent-scoped service** (`BenchmarkProjectionService`); we do **not** overload the mentorship `AnalyticsService`. They are cited together because both are the "analytics read-model" family; the leaderboard surface is the agent-benchmark half. |

> **NEW files** are marked NEW in the Files table. `BenchmarkProjectionService.cs`, the `BenchmarkProjection`/`BenchmarkProjectionCursor` entities, the projector background service, and the leaderboard DTOs/endpoints do **not** exist yet — they are created by this story. `DomainEvent.cs`, `ProviderDiagnostic.cs`, `EventRepository.cs` (other than the additive query method), and `AnalyticsService.cs` are **referenced**, not rewritten.

### Source events (the fold inputs)

The projection consumes only events that already exist (or are introduced by sibling 32-x stories) — it adds **no new source event family**:

| Source event | Producer | Fold contribution |
|---|---|---|
| `AGENT.TASK.SUCCESS` / `.FAILED` / `.PARTIAL` | 32-6 | terminal-run count + `successRate` numerator/denominator; `durationMs` (run latency for percentiles); `costUsd` fallback |
| `AGENT.OUTCOME.RECORDED` | 32-8 | `outcome` + `iterationsToDone` (authoritative for `avgIterationsToDone`); supersedes the `AGENT.TASK.*` outcome when both present for a run |
| `AGENT.DEFECT.RECORDED` | 32-8 | per-category defect count → `defectRateByCategory[category]` (defects per run); `category` ∈ `BugCategory` wire strings |
| `AGENT.ITERATION.COMPLETED` | 32-6/32-7 | iteration count fallback when `AGENT.OUTCOME.RECORDED` is absent |
| `AGENT.PANEL.AGGREGATED` | 32-7 | persona/strategy attribution for panel runs |
| `AGENT.USAGE.RECORDED` | 32-9 (**sibling — mark NEW if 32-9 unmerged**) | `costBasisUsd` (provider/model cost) + `billableUsd` (marked-up) → `avgCostBasisUsd`/`avgBillableUsd`; tokens; provider/model dimension keys |

> **32-9 dependency note:** the `AGENT.USAGE.RECORDED` event type and its `costBasisUsd`/`billableUsd` data fields are owned by **Story 32-9** (Agent usage & cost emission). At authoring time the 32-9 story file is **NEW (not yet drafted)**. This story consumes that event by name per the design spec (§Tracking: "usage/cost emission (tokens + provider/model cost basis) consumed by Epics 34/35/36"); if 32-9 lands after 32-10 begins, the cost columns degrade gracefully to `null`/`0` and the fold backfills on the next rebuild once usage events exist. **Do not invent a parallel usage event** — align the type/field names with 32-9 before merge.

### Projection entity (tenant schema)

```csharp
// apps/tamma-elsa/src/Tamma.Data/Entities/BenchmarkProjection.cs  (NEW)
namespace Tamma.Data.Entities;

/// <summary>
/// Per-tenant materialized benchmark row: one row per (dimension, dimensionKey,
/// window) within a tenant's schema. Derived (a cache) — the tenant's
/// domain_events stream is the source of truth; this row can be dropped and
/// rebuilt from events at any time (backend-development projection-patterns).
/// </summary>
public class BenchmarkProjection
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }            // transitional (shared-DB phase); structural isolation is the schema

    public string Dimension { get; set; } = null!; // "agent" | "provider" | "prompt" | "persona"
    public string DimensionKey { get; set; } = null!; // agentId | provider name | promptRef | personaId
    public string Window { get; set; } = null!;    // "7d" | "30d" | "90d" | "all"

    // Counts
    public int RunCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public int PartialCount { get; set; }

    // Metrics
    public double SuccessRate { get; set; }
    public double AvgIterationsToDone { get; set; }
    public double LatencyP50Ms { get; set; }
    public double LatencyP95Ms { get; set; }
    public decimal AvgCostBasisUsd { get; set; }
    public decimal AvgBillableUsd { get; set; }

    /// JSON map { "functional": 0.12, "security": 0.03, ... } — rate per BugCategory.
    public string DefectRateByCategory { get; set; } = "{}";

    public DateTime ComputedAt { get; set; }
}

// apps/tamma-elsa/src/Tamma.Data/Entities/BenchmarkProjectionCursor.cs  (NEW)
/// One cursor per (dimension, dimensionKey, window): the highest DomainEvent
/// SequenceNumber folded into the row. Incremental folds resume from here;
/// a rebuild resets it to 0. This is what makes the fold idempotent + replayable.
public class BenchmarkProjectionCursor
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Dimension { get; set; } = null!;
    public string DimensionKey { get; set; } = null!;
    public string Window { get; set; } = null!;
    public long LastSequenceNumber { get; set; }   // BIGSERIAL cursor — never CreatedAt
    public DateTime UpdatedAt { get; set; }
}
```

`UNIQUE (TenantId, Dimension, DimensionKey, Window)` on both tables (NULLS NOT DISTINCT on `TenantId`, mirroring the established tenant-entity convention) so the upsert key is the fold target and a concurrent fold collapses to one row.

### Projection engine (the fold)

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Agents/BenchmarkProjectionService.cs  (NEW)
public interface IBenchmarkProjectionService
{
    /// Incrementally fold new events (SequenceNumber > cursor) into the tenant's
    /// benchmark rows for one or all dimensions. Idempotent; replayable.
    Task ProjectAsync(Guid tenantId, string? dimension = null, CancellationToken ct = default);

    /// Full rebuild: reset cursors → replay from SequenceNumber 0. Produces
    /// byte-identical rows to the incremental path (AC3).
    Task RebuildAsync(Guid tenantId, CancellationToken ct = default);

    /// Read the ranked leaderboard for a dimension + window (query-time read;
    /// no recompute — rows are pre-folded). Applies tie-break + min-sample guard.
    Task<LeaderboardResult> GetLeaderboardAsync(
        Guid tenantId, string dimension, string window, int minRuns, CancellationToken ct = default);
}
```

The fold is a pure reducer over the event stream:

```
foreach event in events where SequenceNumber > cursor, ordered by SequenceNumber:
    key = dimensionKeyFor(dimension, event.Tags)   // agentId | provider | promptRef | personaId
    bucket[key].apply(event)                        // increment counts / accumulate latency / costs / defects
    cursor[key] = event.SequenceNumber
percentiles: p50/p95 computed from the accumulated per-run latency sample (t-digest or sorted-sample)
upsert bucket rows; persist cursors
```

Idempotency comes from the `SequenceNumber` cursor — an event already folded (its `SequenceNumber <= cursor`) is skipped, so re-running `ProjectAsync` is a no-op (AC3). `RebuildAsync` zeroes the cursor and replays, guaranteeing the incremental and full-rebuild rows match.

### Leaderboard query + ranking

```csharp
// GET /api/v1/orgs/{tenantId}/agents/leaderboard?dimension=agent&window=30d&minRuns=5
public static async Task<IResult> GetLeaderboard(
    HttpContext http, IBenchmarkProjectionService svc, ITenantContext tenantContext,
    Guid tenantId, string dimension = "agent", string window = "30d", int? minRuns = null)
{
    // tenantId is route-bound + validated by RequireTenantMembershipFilter;
    // the read is physically scoped by the tenant DbContext to that schema.
    if (!ValidDimensions.Contains(dimension))
        return Results.BadRequest(new { error = "invalid_dimension", valid = ValidDimensions });

    var result = await svc.GetLeaderboardAsync(
        tenantId, dimension, window, minRuns is > 0 ? minRuns.Value : 5, http.RequestAborted);

    return Results.Ok(new
    {
        dimension, window,
        ranked = result.Ranked,            // >= minRuns, ordered by tie-break chain
        belowThreshold = result.Provisional // < minRuns, surfaced separately, never ranked
    });
}
```

**Ranking order (deterministic, documented):** `successRate` desc → `avgIterationsToDone` asc → `avgCostBasisUsd` asc → `dimensionKey` asc (final stable tiebreak). **Min-sample guard:** rows with `runCount < minRuns` go to `belowThreshold`, never into `ranked`.

### Platform-admin fleet rollup (public agents only, k-anonymous)

```csharp
// GET /api/v1/admin/agents/{agentId}/benchmark?window=30d   (PlatformOwnerAccess)
// - agentId MUST be a PUBLIC/system agent definition (else 404).
// - Aggregates the SAME metrics across tenants that ran this public agent.
// - k-anonymity: requires >= minTenants (default 5) distinct contributing tenants;
//   below k → 404/empty. NEVER returns a per-tenant row, a tenantId, or a private-agent rollup.
```

This is the **only** cross-tenant view, and it is deliberately narrow: a platform admin who owns a public agent definition sees an anonymized fleet average (is `atlas` a good default?) but **zero** of any single tenant's data. Computing it requires a per-tenant fan-out over the warm-tenant set (the same fan-out `EventRepository` documents as "build when a story demands it" — this story is that demand, scoped to public agents only). The fan-out reads each contributing tenant's pre-folded `BenchmarkProjection` rows for the public agent's `agentId`, then aggregates with the k-guard; it never exposes the per-tenant rows it read.

### Per-mode / per-tenant ownership (mandatory two-scoping answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns the benchmark data? | The sole user — their instance, their dataset (single tenant). | The tenant that generated it. ALWAYS tenant-scoped; one public agent definition → many independent per-tenant datasets (design spec §Ownership). |
| Who reads the leaderboard? | The user (member-read). | Any tenant member (`MemberAccess`) for their own tenant only. |
| Who can rebuild? | The user. | tenant_owner / tenant_admin; `member` → 403. |
| Can a platform admin read a tenant's benchmark? | N/A (sole user). | **No.** Per-tenant rows are structurally unreachable cross-tenant. The only admin view is the **k-anonymized public-agent fleet rollup** — never a single tenant's data, never a tenant id. |
| Where do projection rows + cursors live? | The single tenant store. | The originating tenant's `t_<hex>` schema (per-tenant routing via `ITenantDbContextFactory`). |
| Mode source | `ITammaModeProvider` (process-stable). | same |

## Tasks / Subtasks

- [ ] Task 1: Projection entities + tenant migration (AC 1, 3, 7)
  - [ ] Subtask 1.1: Add `BenchmarkProjection` + `BenchmarkProjectionCursor` entities; `DbSet`s on `TenantDbContext`; model config (unique key, JSON column, transitional `TenantId`) in the tenant model configuration
  - [ ] Subtask 1.2: Additive **Tenant** EF migration (`dotnet ef migrations add` against the Tenant context; the `InitialTenant` baseline exists — this is additive, not a baseline edit); verify `has-pending-model-changes` reports none after
- [ ] Task 2: Projection engine (AC 1, 2, 3, 7, 9)
  - [ ] Subtask 2.1: `IBenchmarkProjectionService` + `BenchmarkProjectionService`; `dimensionKeyFor` (agent/provider/prompt/persona from `Tags`); the per-bucket reducer (counts, success rate, iterations, defects-by-category, latency sample, cost/billable)
  - [ ] Subtask 2.2: `IEventRepository.QueryAgentEventsForProjectionAsync` (tenant-scoped, `SequenceNumber > cursor`, type-prefix filter, `NotSupportedException` on null tenant) + `EventRepository` impl
  - [ ] Subtask 2.3: p50/p95 from the run-latency distribution (sorted-sample or t-digest); cursor-tracked idempotent upsert; `ProjectAsync` (incremental) + `RebuildAsync` (reset→replay)
  - [ ] Subtask 2.4: Background projector (BackgroundService, interval + on-demand) so the fold is off the run path; non-blocking failure handling (log + retry, never throw)
- [ ] Task 3: Tenant leaderboard API (AC 4, 5, 8)
  - [ ] Subtask 3.1: `GET /api/v1/orgs/{tenantId}/agents/leaderboard` (member-read) with dimension/window/minRuns; tie-break chain; `ranked` vs `belowThreshold` split
  - [ ] Subtask 3.2: `POST /api/v1/orgs/{tenantId}/agents/leaderboard/rebuild` (tenant_owner/tenant_admin; member → 403) → 202, triggers `RebuildAsync`
  - [ ] Subtask 3.3: Leaderboard DTOs + cursor-free read shape; map under `/api/v1/orgs/{tenantId}` with `RequireTenantMembershipFilter`
- [ ] Task 4: Platform-admin public-agent fleet rollup (AC 6)
  - [ ] Subtask 4.1: `GET /api/v1/admin/agents/{agentId}/benchmark` (`PlatformOwnerAccess`); reject non-public/private agent → 404
  - [ ] Subtask 4.2: Per-tenant fan-out over warm tenants reading pre-folded rows; k-anonymity guard (`minTenants`, default 5) → suppress below k; assert payload carries no `tenantId`
- [ ] Task 5: Tests (AC 10)
  - [ ] Subtask 5.1: Metric correctness vs fixture streams (exact successRate / avgIterationsToDone / defectRateByCategory / p50 / p95 / avgCostBasisUsd / avgBillableUsd)
  - [ ] Subtask 5.2: Incremental-vs-full-rebuild equivalence; double-fold idempotency
  - [ ] Subtask 5.3: Leaderboard ordering + tie-break + min-sample guard
  - [ ] Subtask 5.4: Tenant isolation (B cannot read A; platform admin denied per-tenant)
  - [ ] Subtask 5.5: k-anonymity (k-1 tenants → suppressed; private agent → 404; no tenant id in payload)

## Dependencies

**Internal Dependencies:**

- **Prerequisite — Story 32-6** (Agent action trail): supplies the `AGENT.TASK.*`, `AGENT.ITERATION.COMPLETED`, `AGENT.PANEL.AGGREGATED` events tagged `agentId`/`agentVersion`/`provider`/`promptRef` in the tenant store, and the `SequenceNumber` cursor convention this fold reuses. Hard prerequisite — there is nothing to project without the trail.
- **Prerequisite — Story 32-8** (Outcome capture & bug taxonomy): supplies `AGENT.OUTCOME.RECORDED` (`outcome` + `iterationsToDone`) and `AGENT.DEFECT.RECORDED` (`category` ∈ `BugCategory`) — the success-rate, iterations-to-done, and defect-rate inputs. Hard prerequisite for those three metric families.
- **Prerequisite — Story 32-9** (Agent usage & cost emission): supplies `AGENT.USAGE.RECORDED` carrying `costBasisUsd` / `billableUsd` / tokens — the cost columns. **The 32-9 story file is NEW (not yet drafted)**; align the event type + field names before merge. Soft-degrades: cost columns are `null`/`0` until usage events exist, backfilled on rebuild.
- **Related — Story 32-7** (Multi-agent panels): `AGENT.PANEL.AGGREGATED` supplies persona/strategy attribution for the persona dimension; gracefully degrades if panels are off (single-agent runs still benchmark by agent/provider/prompt).
- **Related — Story 32-12** (Agent personas & persona-aware benchmarking): the `persona` dimension is wired here; 32-12 enriches persona attribution. This story ships the dimension; 32-12 populates richer persona semantics.
- **Related — Story 32-13** (Agent management & benchmark dashboards): the dashboard consumes the leaderboard + admin-rollup endpoints this story exposes.
- **Epic 4** (DCB event sourcing): `DomainEvent`, `IEventRepository`, and the `SequenceNumber` `BIGSERIAL` cursor are the projection substrate — all reused, no new event infrastructure.
- **Epic 36** (Analytics & Reporting Platform): the dimensional analytics store (36-1) and DCB-driven projection population (36-2) are the platform-wide analogue; this story is the **agent-benchmark** read model in the **tenant** schema. They share the cursor-tracked, idempotent, replayable projection pattern (36-1/36-2 establish it for the CP fleet store; this story applies it per-tenant for agent benchmarks). No code dependency — pattern alignment only.

**External Dependencies:**

- None new. Reuses EF Core 9 / Npgsql, the existing tenant event store, `ITenantDbContextFactory`, and the `RequireTenantMembershipFilter` tenant-path gate.

## Testing Strategy

1. **Metric-correctness tests (highest priority — AC 2, 10a)** (`Tamma.Api.Tests/Agents/BenchmarkProjectionServiceTests.cs`): build a fixture `DomainEvent` stream with a known mix — e.g. 10 runs (7 success / 2 fail / 1 partial), `iterationsToDone` {1,2,3,…}, defects {3 functional, 1 security}, run latencies {100,150,200,…ms}, costs {0.01,0.02,…} — and assert the folded row's `successRate == 0.7`, `avgIterationsToDone`, `defectRateByCategory["functional"]`, `latencyP50Ms`/`latencyP95Ms` (against a hand-computed percentile), `avgCostBasisUsd`, `avgBillableUsd`, `runCount` to exact values.
2. **Idempotency + rebuild-equivalence tests (AC 3, 10b/f):** fold a stream; fold again → identical row + no double-count (cursor skip). Then `RebuildAsync` (cursor reset → replay) → byte-identical rows to the incremental path. Append more events → incremental advance equals a fresh full rebuild.
3. **Leaderboard ordering tests (AC 4, 10c):** seed rows with deliberate ties on `successRate`; assert the full tie-break chain (`avgIterationsToDone` asc → `avgCostBasisUsd` asc → key asc); assert `runCount < minRuns` rows land in `belowThreshold`, not `ranked`.
4. **Tenant-isolation tests (docker-bound, `sg docker -c "dotnet test ..."` — AC 5, 10d):** seed benchmark rows for tenant A and B; assert `GET /api/v1/orgs/{B}/agents/leaderboard` returns only B's; a member of A hitting B's path is rejected by `RequireTenantMembershipFilter` (403/404); a platform owner has no per-tenant leaderboard route; a public agent run by A leaves benchmark rows only in A's schema.
5. **k-anonymity tests (AC 6, 10e):** stand up k-1 tenants contributing to a public agent → admin rollup suppressed (404/empty); k tenants → rollup returned with aggregated metrics and **no `tenantId` field anywhere in the payload** (assert via JSON inspection); a **private** agent → 404 regardless of tenant count.
6. **Non-blocking / background tests (AC 9):** inject a projection-write failure; assert it is logged + retried and never surfaces to a run; assert the projector runs off the run path (the managed-agent execution does not call the fold synchronously).

Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/`; docker-bound suites run via `sg docker -c "dotnet test ..."`. TDD: write the metric-correctness + isolation + k-anonymity tests first.

## Estimated Effort

5-6 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/BenchmarkProjection.cs` | Create (NEW) |
| `apps/tamma-elsa/src/Tamma.Data/Entities/BenchmarkProjectionCursor.cs` | Create (NEW) |
| `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | Modify (add `DbSet<BenchmarkProjection>` + `DbSet<BenchmarkProjectionCursor>`) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (configure the two tenant entities: unique key, JSON column, transitional `TenantId`) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/<ts>_AddBenchmarkProjections.cs` | Create (additive Tenant migration; baseline `InitialTenant` already exists) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IBenchmarkProjectionService.cs` | Create (NEW) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/BenchmarkProjectionService.cs` | Create (NEW — the idempotent, cursor-tracked fold + leaderboard read) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/BenchmarkProjectorBackgroundService.cs` | Create (NEW — off-run-path projector; interval + on-demand) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/BenchmarkEventTypes.cs` | Create (NEW — `BENCHMARK.PROJECTION.UPDATED` / `.REBUILT` lifecycle event constants + the consumed source-type prefixes) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentLeaderboardEndpoints.cs` | Create (NEW — tenant leaderboard + rebuild + admin public-agent rollup) |
| `apps/tamma-elsa/src/Tamma.Api/Dtos/Agents/BenchmarkDtos.cs` | Create (NEW — leaderboard / row / rollup wire shapes) |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` | Modify (add `QueryAgentEventsForProjectionAsync`) |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs` | Modify (implement it; `SequenceNumber > cursor`, tenant-scoped, null-tenant guard) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (DI: `IBenchmarkProjectionService` + projector hosted service; map leaderboard + rebuild + admin-rollup routes) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/BenchmarkProjectionServiceTests.cs` | Create (NEW) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/BenchmarkLeaderboardEndpointsTests.cs` | Create (NEW) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/BenchmarkIsolationTests.cs` | Create (NEW) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/BenchmarkKAnonymityTests.cs` | Create (NEW) |

> Note: `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs`, `ProviderDiagnostic.cs`, and `AnalyticsService.cs` are **referenced, not modified** — this story reads the existing DCB row shape and diagnostics, and does not overload the mentorship `AnalyticsService`. (`packages/api` is deleted; all work is in the C# `apps/tamma-elsa` stack.)

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes/bugs/findings/decisions — especially `.dev/decisions/story-28-1-design-calls.md` (Decision #2: cross-tenant read routing / per-tenant fan-out — the basis for the public-agent fleet rollup's fan-out and for why per-tenant rows are structurally unreachable)
3. Read sibling stories **32-6** (action trail — the source events + `SequenceNumber` cursor), **32-8** (outcome/defect events + `BugCategory`), and the **32-9** design intent (usage/cost events) — align event type + tag/data field names exactly before merge
4. Read the Epic 32 design spec `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` (the tenancy rule: definition ownership ≠ data ownership; **leaderboards are computed within a tenant's own data**; platform admin cannot read tenant performance)
5. Reviewed the **backend-development projection-patterns** (idempotent fold, cursor-tracked read models, replayable from the event stream) — and 36-1/36-2 for the in-repo precedent of a cursor-tracked, idempotent analytics projection
6. Planned the TDD cycle (metric-correctness + isolation + k-anonymity tests first)

### Key design decisions

- **Projection is a derived cache; events are the source of truth.** The benchmark row can be dropped and rebuilt from the tenant's `domain_events` at any time. `RebuildAsync` (reset cursor → replay) is the safety valve and the equivalence-test target. This is the backend-development projection pattern applied per-tenant.
- **`SequenceNumber` is the fold cursor, never `CreatedAt`.** `CreatedAt` has same-millisecond collisions; `SequenceNumber` is the server-side `BIGSERIAL` total order. Idempotency (skip `SequenceNumber <= cursor`) and incremental-vs-rebuild equivalence both ride on it.
- **Isolation is structural, not an app filter.** Projection rows live in the tenant's `t_<hex>` schema and are read/written only through `ITenantDbContextFactory`; there is no cross-tenant route for per-tenant rows, and `IEventRepository` throws on null-tenant scoped reads. A platform owner who owns a public agent sees zero of any tenant's benchmark — exactly the design-spec tenancy rule.
- **The single cross-tenant view is narrow, public-only, and k-anonymous.** The admin fleet rollup answers "is my public default any good?" without ever exposing a single tenant's data, a tenant id, or a private-agent rollup. k-anonymity (≥ minTenants) is the privacy floor; below it, suppress.
- **Min-sample guard, not silent ranking.** A reviewer with 2 runs and 100% success is not "the best reviewer" — it is provisional. The leaderboard splits `ranked` (≥ minRuns) from `belowThreshold` so a tiny sample never masquerades as a meaningful ranking.
- **Fold is off the run path.** Benchmark projection never executes inside a managed-agent run; a projector background service folds on an interval / on-demand. A projection failure can never fail or delay an agent run (matches the non-blocking precedent across 32-6/32-8).
- **Do not overload the mentorship `AnalyticsService`.** It is a separate (mostly-stub) concern; agent benchmarking gets its own `BenchmarkProjectionService`. They are siblings in the analytics read-model family, not the same service.

### Integration points

- **32-6 trail + 32-8 outcomes/defects + 32-9 usage** are the producers; this story is a pure consumer of their DCB events. Coordinate event-type + tag/data field names before merge (especially 32-9's `AGENT.USAGE.RECORDED` shape, which is NEW).
- **`RequireTenantMembershipFilter` + `/api/v1/orgs/{tenantId}` group** is the tenant path-gate the leaderboard rides — member-read, owner/admin rebuild.
- **`ITenantDbContextFactory`** is the structural isolation plane for both the fold (per-tenant write) and the admin fan-out (per-tenant read of pre-folded rows for a public agent).
- **36-1/36-2** are the platform-wide analytics analogue; share the projection pattern, not code.

### Risks and Mitigations

| Risk | Severity | Mitigation |
| ---- | --------- | ---------- |
| Cross-tenant leakage of performance data | Critical | Per-tenant schema + `ITenantDbContextFactory`; no per-tenant cross-tenant route; null-tenant scoped reads throw; explicit isolation tests incl. platform-admin-denied |
| Admin rollup de-anonymizes a tenant | Critical | Public-agent-only; k-anonymity (≥ minTenants) suppression; assert no `tenantId` in payload; private agent → 404; negative-case test (k-1 suppressed) |
| Incremental fold diverges from a full rebuild | High | `SequenceNumber` cursor + pure reducer; rebuild-equivalence test is a gate |
| 32-9 usage events not yet defined | Medium | Cost columns soft-degrade to null/0; backfill on rebuild once usage events exist; align names before merge — do NOT invent a parallel usage event |
| Projection fold inside a run aborts/delays it | High | Off-run-path background projector; non-blocking failure (log + retry); test induces failure and asserts run unaffected |
| Tiny-sample rows ranked as meaningful | Medium | Min-sample guard splits `ranked` vs `belowThreshold` |
| p50/p95 wrong on skewed latency | Medium | Percentiles from the run-latency distribution (sorted-sample / t-digest), not averaged; hand-computed fixture assertion |

### Success Metrics

- [ ] Folded benchmark rows match hand-computed metrics on the fixture stream (exact)
- [ ] Incremental and full-rebuild rows are byte-identical (equivalence test green)
- [ ] 0 cross-tenant benchmark reads possible (isolation suite green)
- [ ] Admin public-agent rollup never exposes a tenant id and is suppressed below k tenants
- [ ] Leaderboard ordering, tie-break, and min-sample guard are deterministic and tested

## Logging Requirements

- **INFO**: projection fold completed (`tenantId`, `dimension`, rows updated, events folded, `lastSequenceNumber`); leaderboard served (`tenantId`, `dimension`, `window`, ranked count, belowThreshold count); rebuild completed (`tenantId`, duration, rows)
- **DEBUG**: per-bucket fold step (`dimensionKey`, `sequenceNumber`); cursor advance; admin rollup tenant fan-out (count of contributing tenants — never tenant ids at INFO)
- **WARN**: projection write failed/retried (`tenantId`, `dimension`, error); admin rollup suppressed below k-anonymity threshold (`agentId`, contributing-tenant count, k); leaderboard requested with unknown dimension
- **ERROR**: terminal projection-write failure after retries (run still unaffected); repository/migration failure
- **Structured context**: include `{ tenantId, dimension, dimensionKey, window, sequenceNumber, runCount }` where applicable
- **Credential / privacy safety**: NEVER log a tenant id inside the admin fleet-rollup result path; NEVER log raw prompt/tool content (only `promptRef`); the admin rollup logs counts, never per-tenant identifiers

## Related

- Design spec: `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` (§Ownership/data-scoping — leaderboards are PER-TENANT; §Tracking — performance projections)
- Implementation plan: `docs/superpowers/plans/2026-06-17-32-10-benchmark-projections-and-leaderboards-plan.md`
- Sibling stories: `docs/stories/epic-32/story-32-6` (action trail), `docs/stories/epic-32/story-32-8` (outcome/defect), `docs/stories/epic-32/story-32-9` (usage/cost — NEW), `docs/stories/epic-32/story-32-7` (panels), `docs/stories/epic-32/story-32-12` (personas), `docs/stories/epic-32/story-32-13` (dashboards)
- Pattern precedent: `docs/superpowers/plans/2026-06-17-36-1-dimensional-analytics-projection-schema-and-store-plan.md` (cursor-tracked, idempotent, replayable analytics projection — CP fleet analogue)

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
</content>
</invoke>
