# Story 36-5: Agent & Tenant Performance Rollups API — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation. Read [BEFORE_YOU_CODE.md](../../guides/BEFORE_YOU_CODE.md) first.

**Goal:** Ship a **tenant-scoped reporting API** over the Epic 36 dimensional analytics store: a
per-agent performance rollup (runs, success rate, avg duration, tokens/run, cost/run,
platform-billed), a per-agent daily trend, and a tenant-level performance summary — read **only**
from the resolving tenant's `analytics_usage_*` facts (Story 36-1, populated by 36-2), with agent
identity/name resolved from the Epic 32 registry. Performance is **strictly per-tenant**: a public
agent shows only this tenant's numbers, no platform-admin per-tenant read exists, and this story is
a **sibling, not a duplicate,** of Story 32-10's leaderboard projection — it consumes the
dimensional store rather than re-folding the action trail.

**Story file:** `docs/stories/epic-36/story-36-5/36-5-agent-and-tenant-performance-rollups-api.md`

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (Tamma.Api + Tamma.Data +
Tamma.Core). Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/` (xUnit; docker-bound
suites run via `sg docker -c "dotnet test ..."` — session docker group is stale; the build itself
needs no wrapper). `packages/api` is **deleted** — never a target.

---

## Non-goals (YAGNI guard)

- **NO new schema or migration.** This story reads the Story 36-1 fact tables
  (`analytics_usage_hourly` / `analytics_usage_daily`) and the Epic 9 `ProviderDiagnostic`; it adds
  no entity, no column, no EF migration. Any PR that alters the 36-1 schema or the 36-2 projection
  under cover of this story is out of scope.
- **NO re-folding the action trail.** Story 32-10's `BenchmarkProjectionService` owns the
  agent/provider/prompt/persona leaderboard (defect-by-category, p50/p95 latency, k-anonymous
  public-agent fleet rollup) folded directly from `AGENT.*` events. This story reads the **pre-folded
  dimensional facts** (`GROUP BY AgentId`) — it does not re-implement the leaderboard, and it
  references 32-10 for the shared per-agent success-rate definition.
- **NO cross-tenant or platform-admin per-tenant performance path.** Performance rollups are
  per-tenant only. There is no admin variant of these routes. The only cross-tenant agent analytics
  that exists is 32-10's k-anonymous public-agent fleet rollup, which this story does not extend.
- **NO mutation surface.** Performance rows are derived from the facts; there is no edit and no
  rebuild trigger here (the dimensional rollup is rebuilt by the 36-2 workflow / its backfill input).
- **NO event-stream rescan on the read path.** The reads aggregate the pre-folded facts; they never
  scan `domain_events`.
- **NO dashboard surface, export, or scheduled report** — those are later Epic 36 stories that
  consume these endpoints.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### Sources this story reads

| Component | File (verified, unless marked NEW) | Role |
|---|---|---|
| Per-tenant usage facts (primary source) | `src/Tamma.Data/Entities/AnalyticsUsageDaily.cs` + `AnalyticsUsageHourly.cs` — **Story 36-1 NEW (drafted, not merged)** | `AgentId`, `Provider` dims; measures `TokensIn/Out`, `WorkflowsCompleted/Failed`, `CostUsd`, `PlatformBilledUsd`; `Day`/`Hour`. The rollup substrate. |
| Per-tenant DbContext | `src/Tamma.Data/TenantDbContext.cs` (verified) | Owns the `analytics_usage_*` DbSets (added by 36-1); structural isolation plane (no cross-tenant query filter). |
| Schema-per-tenant routing | `ITenantDbContextFactory` / `ITenantContext` (`Tamma.Data`, verified) | Resolves the tenant's schema per request — no new plumbing. |
| Diagnostic outcome substrate | `src/Tamma.Data/Entities/ProviderDiagnostic.cs` (verified — `AgentType`, `RequestDurationMs`, `InputTokens`/`OutputTokens`, `Cost`, `Success`) | Lineage of `avgDurationMs`/success the 36-2 projection folds into the facts. Read/referenced only. |
| Agent registry (identity only) | Epic 32 agent registry (`AgentConfig` + `src/Tamma.Api/Endpoints/AgentEndpoints.cs`, verified present) | `AgentId → name` + public/system vs private/tenant. Identity only — never a performance source. |
| Tenant-path gate | `src/Tamma.Api/Authorization/RequireTenantMembershipFilter.cs` (verified) + `/api/v1/orgs/{tenantId}` group (`Program.cs`, `AlertEndpoints.cs`, `OrgEndpoints.cs`, verified) | The member-readable tenant-path pattern these endpoints ride. |
| Auth policies | `src/Tamma.Api/Program.cs` — `MemberAccess` (~991, verified: `RequireAuthenticatedUser`), `OwnerAccess` (~971), `PlatformOwnerAccess` (~986) | `MemberAccess` for the reads; no `PlatformOwnerAccess` per-tenant performance route exists. |
| Placeholder + clamp precedent | `src/Tamma.Api/Services/Analytics/PlatformAnalyticsService.cs` — `GetTopTenantsAsync` `(unknown)` fallback (~344-350), limit clamp (~280-281), per-tenant fan-out via `ITenantDbContextFactory` (~288-311) | The `(unknown)` placeholder + limit-clamp + per-tenant-read conventions this story mirrors. |
| Sibling read model (NOT duplicated) | `src/Tamma.Api/Services/Agents/BenchmarkProjectionService.cs` — **Story 32-10 NEW (drafted)** | The leaderboard fold from the action trail. Referenced; this story does the dimensional cost/throughput rollup instead. |

### Files this story creates (all NEW)

`AgentPerformanceAnalyticsService.cs` + `IAgentPerformanceAnalyticsService.cs`,
`AgentNameResolver.cs` + `IAgentNameResolver.cs`, `AgentPerformanceDtos.cs` (all in
`src/Tamma.Api/Services/Analytics/`), and `TenantAnalyticsEndpoints.cs` in
`src/Tamma.Api/Endpoints/`. `Program.cs` is modified for DI + route mapping. None of these exist
today (verified via glob).

### Key constraints carried from the codebase

- **Isolation is structural** — every fact read goes through `ITenantDbContextFactory.CreateAsync`
  to the tenant's search-path schema; there is no cross-tenant query filter, so a tenant context
  can only ever see that tenant's rows. This is what makes "a public agent shows only this tenant's
  numbers" true by construction, not by an app filter.
- **`MemberAccess` is authenticate-only** (`Program.cs` ~991); the tenant-scoping is enforced by
  `RequireTenantMembershipFilter` on the `{tenantId}` route, exactly as `AlertEndpoints` tenant
  routes do. Reuse that group — do not invent a new gate.
- **`(unknown)` placeholder + limit clamp** are established in `GetTopTenantsAsync` — reuse the same
  shape for deleted-agent names and window/limit bounds.

---

## Architecture

**Read the dimensional store → group by agent → resolve name (identity only) → serve.** A thin
reporting service over pre-folded facts, behind member-read tenant-scoped endpoints.

1. **`IAgentPerformanceAnalyticsService` / `AgentPerformanceAnalyticsService`** (new,
   `src/Tamma.Api/Services/Analytics/`) — the read-side seam. Three methods:
   `GetAgentRollupsAsync` (`GROUP BY AgentId` over `analytics_usage_daily`, `analytics_usage_hourly`
   for sub-day windows → per-agent metrics, ordered by a selectable metric + `agentId` tie-break),
   `GetAgentTrendAsync` (daily series for one agent, sparse, empty for zero activity), and
   `GetTenantSummaryAsync` (blended success rate, total spend, most/least efficient, min-run
   guarded). A static pure-DI `ComputeAgentRollupsAsync(...)` entry point drives the endpoint and
   unit tests without an HTTP context (the `PlatformAnalyticsService` aggregation shape).
2. **`IAgentNameResolver` / `AgentNameResolver`** (new) — the **only** place the Epic 32 registry is
   touched, reading **only** `AgentId → name` (+ public/system vs private/tenant). Missing/deleted →
   `"(unknown)"`; `NULL` bucket → `"(unattributed)"`. It never reads a performance number — this is
   the isolation guarantee for public agents.
3. **`TenantAnalyticsEndpoints.cs`** (new) — maps the three reads under `/api/v1/orgs/{tenantId}`
   with `MemberAccess` + `RequireTenantMembershipFilter`; window parse + 400 on bad input; optional
   best-effort `ANALYTICS.REPORT.*` observability events.
4. **`Program.cs`** (modify) — DI for the service + resolver; map the three routes.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns the performance data? | The sole user — their (single) tenant schema. | The tenant that generated it; one public definition → many per-tenant datasets. |
| Who reads rollups/trend/summary? | The user (member-read). | Any tenant member (`MemberAccess`) for their own tenant only. |
| Can a platform admin read a tenant's agent performance? | N/A (sole user). | **No** — no admin per-tenant route; per-tenant facts are structurally unreachable cross-tenant. |
| Where do the facts live? | The single tenant store. | The originating tenant's `t_<hex>` schema. |
| Mode source | `ITammaModeProvider` (process-stable). | same |

Endpoint shape is identical between modes (the prompt-store precedent); the auth middleware +
`RequireTenantMembershipFilter` decide scope; the per-tenant `TenantDbContext` enforces isolation
physically.

---

## Task breakdown

### Task 1: DTOs + service/resolver contracts (story AC: 1, 2, 4, 5)

**Scope:** Wire shapes + interfaces only — no aggregation logic yet.

**Files:**
- New: `src/Tamma.Api/Services/Analytics/AgentPerformanceDtos.cs` — `AgentRollupRow`
  (`agentId, name, runs, successRate, avgDurationMs, tokensPerRun, costUsdPerRun, platformBilledUsd`),
  `AgentTrendPoint` (`date, runs, successRate, costUsd, tokensIn, tokensOut, platformBilledUsd`),
  `TenantPerformanceSummary` (`blendedSuccessRate, totalRuns, totalCostUsd, totalPlatformBilledUsd,
  mostEfficientAgents[], leastEfficientAgents[]`).
- New: `src/Tamma.Api/Services/Analytics/IAgentPerformanceAnalyticsService.cs`,
  `IAgentNameResolver.cs`.

**Tests (first):** a contract/DTO-shape test asserting record fields + nullability (`agentId`
nullable for the `(unattributed)` bucket).

**Acceptance:**
- [ ] DTOs + interfaces compile; `AgentRollupRow.agentId` is nullable (NULL-bucket support).

### Task 2: Agent name resolution (story AC: 3, 7)

**Scope:** `AgentNameResolver` — registry identity only.

**Files:**
- New: `src/Tamma.Api/Services/Analytics/AgentNameResolver.cs` — resolves `AgentId → name` from the
  Epic 32 registry (public/system vs private/tenant); `"(unknown)"` for a missing/deleted id;
  `"(unattributed)"` for `null`. Reads only identity/name — never a metric.

**Tests (first):** `AgentPerformanceAnalyticsServiceTests` (name section) — known agent → registry
name; missing/deleted id → `(unknown)` (the `GetTopTenantsAsync` placeholder precedent); `null` →
`(unattributed)`; a resolver injected with a metric-throwing registry double proves identity-only
reads.

**Acceptance:**
- [ ] All three name paths covered; resolver never touches performance data.

### Task 3: `AgentPerformanceAnalyticsService` aggregation (story AC: 1, 2, 5, 8, 9)

**Scope:** The read-side aggregation over the 36-1 facts — no event-stream rescan.

**Files:**
- New: `src/Tamma.Api/Services/Analytics/AgentPerformanceAnalyticsService.cs`:
  - `GetAgentRollupsAsync` / static `ComputeAgentRollupsAsync` — `GROUP BY AgentId` over
    `analytics_usage_daily` (`analytics_usage_hourly` for sub-day windows); `runs = Σ(Completed +
    Failed)`, `successRate = Σ Completed / runs`, `tokensPerRun = Σ(TokensIn+TokensOut)/runs`,
    `costUsdPerRun = Σ CostUsd / runs`, `platformBilledUsd = Σ PlatformBilledUsd`, `avgDurationMs`
    from the duration substrate (coordinate with 36-2; fall back to tenant-scoped
    `ProviderDiagnostic.RequestDurationMs` if the fact carries no duration measure — duration only,
    no new column); ordering by `orderBy` (default `runs` desc) + `agentId` asc tie-break; window
    validation (`from < to`), window cap (default 366d), `limit` clamp (default 50, max 500) —
    mirroring `GetTopTenantsAsync`.
  - `GetTenantSummaryAsync` — blended success rate, total cost/billable, most/least efficient
    (default `costUsdPerRun` asc, min-run guarded, top/bottom-N default 5 max 50).

**Tests (first):** metric-correctness (InMemory) against a fact-row fixture — exact `runs`,
`successRate`, `tokensPerRun`, `costUsdPerRun`, `platformBilledUsd`, summary `blendedSuccessRate`,
`totalPlatformBilledUsd`, most/least-efficient ordering; ordering-option matrix + tie-break;
window validation (`from >= to` → throw/400-mapped; clamps applied); the `NULL` bucket surfaces as
an `(unattributed)` row reconciling to the grand total.

**Acceptance:**
- [ ] Rollup + summary metrics exact on the fixture; ordering deterministic with `agentId`
      tie-break; window/limit bounded; no event-stream rescan (assert facts-only reads).

### Task 4: Daily trend (story AC: 4, 7)

**Scope:** `GetAgentTrendAsync` — daily series for one agent.

**Files:**
- Modify (same file): `AgentPerformanceAnalyticsService.GetAgentTrendAsync` — read
  `analytics_usage_daily` filtered to `AgentId == agentId`, one ordered point per active UTC day,
  sparse (inactive days omitted); zero activity → empty list (HTTP 200 at the endpoint), never 404.

**Tests (first):** sparse trend ordering; zero-activity agent → `[]`; deleted-agent trend still
returns its fact rows (name not needed for the trend points).

**Acceptance:**
- [ ] Trend ordered + sparse; zero activity → empty (200), never 404.

### Task 5: Tenant analytics endpoints (story AC: 2, 4, 5, 6, 9, 10)

**Scope:** The three member-read routes + DI + best-effort telemetry events.

**Files:**
- New: `src/Tamma.Api/Endpoints/TenantAnalyticsEndpoints.cs` — map
  `GET /api/v1/orgs/{tenantId}/analytics/agents`,
  `GET /api/v1/orgs/{tenantId}/analytics/agents/{agentId}/trend`,
  `GET /api/v1/orgs/{tenantId}/analytics/summary` under the `/api/v1/orgs/{tenantId}` group with
  `MemberAccess` + `RequireTenantMembershipFilter`; window query parse (ISO-8601, default trailing
  30d) → 400 on `from >= to` / bad `orderBy`; optional best-effort `ANALYTICS.REPORT.*` events via
  `IPlatformEventPublisher` (never block the read).
- Modify: `src/Tamma.Api/Program.cs` — register `IAgentPerformanceAnalyticsService` +
  `IAgentNameResolver`; call `TenantAnalyticsEndpoints.Map(...)`.

**Tests (first):** `TenantAnalyticsEndpointsTests` — member reads own tenant (200); window `from
>= to` → 400; bad `orderBy` → 400; zero-activity trend → 200 `[]`; best-effort event emission does
not fail the read when the publisher throws.

**Acceptance:**
- [ ] Three routes mapped + gated; window/order 400s; zero-activity 200; telemetry best-effort.

### Task 6: Isolation + RBAC integration tests (story AC: 3, 6, 11 — docker-bound)

**Scope:** The Postgres-17-Testcontainer suite proving structural isolation.

**Files:**
- New: `src/.../tests/Tamma.Api.Tests/Analytics/AgentPerformanceIsolationTests.cs` — migrate two
  tenant schemas; a public/system agent run by tenant A and tenant B; seed each tenant's
  `analytics_usage_daily` independently; assert `…/orgs/{A}/analytics/agents` returns only A's
  metrics for that agent and `…/orgs/{B}/…` only B's (no sum, no leak); a member of A hitting
  `/orgs/{B}/analytics/*` is rejected by `RequireTenantMembershipFilter` (403/404); a platform owner
  is denied a tenant's performance like any non-member (no admin route exists).

**Tests (first):** the suite itself is the test.

**Acceptance:**
- [ ] Public-agent metrics isolated across two schemas; cross-tenant + platform-owner denied; suite
      green via `sg docker -c "dotnet test ..."`.

---

## Task order & dependencies

Task 1 → Task 2 → Task 3 → Task 4 → Task 5 → Task 6.
Tasks 1-4 are pure-service (InMemory tests, no docker); Task 5 wires the HTTP surface; Task 6 is the
docker-bound isolation proof and should run last. **Hard prerequisite:** Stories 36-1 (fact tables)
and 36-2 (population) must be merged — this story has nothing to read until they are. Coordinate the
per-agent duration measure and the per-agent success-rate definition with 36-2 / 32-10 before merge.

## Risks

- **Cross-tenant leakage of a public agent's performance (critical):** mitigated structurally —
  per-tenant schema via `ITenantDbContextFactory`, registry resolves name only, no admin per-tenant
  route. The public-agent isolation test (Task 6) is the gate.
- **Duplicating 32-10's leaderboard (high):** this story reads the dimensional facts (`GROUP BY
  AgentId`), never re-folds the action trail; it references 32-10 and aligns the per-agent
  success-rate definition. If the two surfaces disagree on success rate, reconcile to the fact-based
  definition (`WorkflowsCompleted ÷ terminal runs`) and note it in the PR.
- **`avgDurationMs` source ambiguous (medium):** the 36-1 fact set may not carry a duration measure
  at merge time. Coordinate with 36-2; if absent, read `ProviderDiagnostic.RequestDurationMs`
  tenant-scoped for duration **only** — do not add a fact column under this story.
- **36-1/36-2 not merged (high, blocking):** hard prerequisite; do not start until the fact tables +
  projection exist. Mark the NEW source files and align measure/tag names first.
- **Unbounded window scans (medium):** `from < to` validation, window cap, `limit`/top-N clamps (the
  `GetTopTenantsAsync` precedent) keep a malformed request a 400, not a runaway scan.
- **Idle agent 404 vs empty (low):** zero activity → empty trend (200); deleted agent → `(unknown)`;
  `NULL` bucket → `(unattributed)` so per-agent rows reconcile to the grand total.
