# Story 32-10 — Benchmark Projections & Leaderboards (per agent/provider/prompt, per-tenant) (implementation plan)

> Epic 32 (Agents — First-Class Agent Entities, Managed Execution, Benchmarking & Learning) · P1 ·
> est. 5-6 days · author Claude · 2026-06-17 ·
> REQUIRED SUB-SKILL: use `superpowers:subagent-driven-development` (recommended) or
> `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`)
> syntax for tracking. Project is test-first (TDD) — every task writes its failing test first.
> Docker-bound C# suites run via `sg docker -c "dotnet test ..."`; the build itself needs no wrapper.

**Goal:** Turn the per-tenant agent **action trail + outcome + usage** event families into materialized
**benchmark projections** and **leaderboards**. Build cursor-tracked, idempotent, replayable read-model
projections — per **agent / provider / prompt / persona** — computing `successRate`,
`avgIterationsToDone`, `defectRateByCategory` (6 taxonomy buckets + `other`), `latencyP50Ms`/`P95Ms`,
`avgCostBasisUsd`, `avgBillableUsd`, and `runCount` over a selectable window. Expose tenant-scoped
leaderboards (ranked, tie-broken, min-sample-guarded). **All projections are PER-TENANT**: a tenant
sees only its own data; the only cross-tenant view is a **k-anonymized, public-agent-only fleet rollup**
for the platform admin that never exposes a single tenant's data or a tenant id.

**Story file:** `docs/stories/epic-32/story-32-10/32-10-benchmark-projections-and-leaderboards.md`

**Design source:** `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`
(§"Ownership, visibility & data scoping" — *leaderboards are computed within a tenant's own data,
platform admin cannot read tenant performance*; §"Tracking: actions + performance + learning" —
performance projections).

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa engine).
Tests in `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/` (xUnit). **`packages/api` is deleted** — all
work is C#.

---

## Non-goals (YAGNI guard)

- **NO new source event family.** This story is a pure *consumer* of events that 32-6 (action trail),
  32-8 (outcome/defect), 32-7 (panels), and 32-9 (usage/cost) emit. It emits only `BENCHMARK.PROJECTION.*`
  lifecycle breadcrumbs. Do **not** invent a parallel usage/outcome event.
- **NO cross-tenant per-tenant view.** Per-tenant benchmark rows are structurally unreachable across
  tenants. The single cross-tenant surface is the public-agent, k-anonymized fleet rollup — and even
  that returns no tenant id and no private-agent data. This is the load-bearing privacy invariant.
- **NO recompute inside an agent run.** The fold runs on a background projector / on-demand, never in
  the managed-agent execution path. A projection failure must never fail or delay a run.
- **NO overloading the mentorship `AnalyticsService`.** Agent benchmarking gets its own
  `BenchmarkProjectionService`. `AnalyticsService.cs` (mentorship, mostly TODO stubs) is referenced as
  a sibling in the analytics read-model family, not modified.
- **NO `DomainEvent`/`ProviderDiagnostic` schema change.** Reuse the DCB row + the diagnostics entity.
  The projection is a derived cache in the tenant schema; events are the source of truth.
- **NO dashboard surface.** 32-13 consumes these endpoints; the UI is out of scope here.
- **NO A/B experiment / statistical-significance machinery.** That is 32-14 (Phase 2).

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### What exists and is the pattern to mirror

| Asset | Location | What it gives us |
|---|---|---|
| `DomainEvent` (DCB row) | `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` | `Type`, `TenantId`, JSONB `Tags`/`Metadata`/`Data`, `CreatedAt`, **`SequenceNumber` (`BIGSERIAL`)** — the fold cursor. Reused verbatim, **no change**. |
| `IEventRepository` / `EventRepository` | `apps/tamma-elsa/src/Tamma.Data/Repositories/{IEventRepository,EventRepository}.cs` | `QueryWithPaginationAsync`/`ListByTenantAsync` are tenant-scoped and **throw `NotSupportedException` on a null tenant** — the isolation backbone. `AppendAsync` routes `evt.TenantId ?? tenantContext.TenantId` to `t_<hex>` via `ITenantDbContextFactory`. We add `QueryAgentEventsForProjectionAsync` (cursor-paged, type-prefix filtered, null-tenant guard). |
| Per-tenant DbContext | `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | Owns per-tenant business data (agent configs, diagnostics, domain events…). We add `DbSet<BenchmarkProjection>` + `DbSet<BenchmarkProjectionCursor>`. Model config via `TammaModelConfiguration.ConfigureTenantEntities` (strips `TenantId` in the target schema-per-tenant shape; keep a transitional `TenantId` like `agent_configs`). |
| Tenant migration baseline | `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/20260610013731_InitialTenant.cs` (+ snapshot) | The Tenant context has a baseline; our migration is **additive** (`dotnet ef migrations add` against the Tenant context), not a baseline edit. Verify `has-pending-model-changes` reports none after. |
| Schema-per-tenant routing | `ITenantDbContextFactory` / `ITenantContext` (`Tamma.Data`) | Structural isolation plane for fold-write + admin fan-out-read. No new plumbing. |
| DCB emission precedent | `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs` (`UpdateConfig`, ~93-113) | Canonical `DomainEvent { Type, TenantId, Tags=flat strings, Metadata=envelope, Data }`. We **read** this shape; emit only `BENCHMARK.PROJECTION.*`. |
| Cost/latency source | `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs` | `CorrelationId`, `AgentType`, `InputTokens`/`OutputTokens`, `Cost`, `RequestDurationMs`. 32-9 surfaces these as `AGENT.USAGE.RECORDED` events; we fold the events (replayable), not the diagnostics table directly. |
| Tenant-scope endpoint + path gate | `apps/tamma-elsa/src/Tamma.Api/Authorization/RequireTenantMembershipFilter.cs`; `/api/v1/orgs/{tenantId}/…` group | Exact member-readable tenant-path pattern the leaderboard rides. |
| Auth policies | `apps/tamma-elsa/src/Tamma.Api/Program.cs` (~971-991) | `MemberAccess` (tenant leaderboard read), tenant_owner/tenant_admin (rebuild), `PlatformOwnerAccess` (public-agent fleet rollup). |
| Analytics sibling | `apps/tamma-elsa/src/Tamma.Api/Services/AnalyticsService.cs` + `apps/tamma-elsa/src/Tamma.Core/Interfaces/IAnalyticsService.cs` | Mentorship analytics (mostly stubs). Referenced as family; **not** modified. |
| Projection-pattern precedent (in-repo) | `docs/superpowers/plans/2026-06-17-36-1-dimensional-analytics-projection-schema-and-store-plan.md` (36-1) + 36-2 | 36-1/36-2 establish the cursor-tracked, idempotent, replayable analytics projection for the **CP fleet** store; we apply the same pattern **per-tenant** for agent benchmarks. Pattern alignment only — no code dependency. |

### Source events the fold consumes (no new family added)

| Event | Producer story | Fold contribution |
|---|---|---|
| `AGENT.TASK.SUCCESS`/`.FAILED`/`.PARTIAL` | 32-6 | terminal-run count; `successRate`; run `durationMs` (latency sample); cost fallback |
| `AGENT.OUTCOME.RECORDED` | 32-8 | authoritative `outcome` + `iterationsToDone` |
| `AGENT.DEFECT.RECORDED` | 32-8 | per-`category` defect count → `defectRateByCategory` |
| `AGENT.ITERATION.COMPLETED` | 32-6/32-7 | iteration fallback |
| `AGENT.PANEL.AGGREGATED` | 32-7 | persona/strategy attribution |
| `AGENT.USAGE.RECORDED` | **32-9 (NEW — story not yet drafted)** | `costBasisUsd` + `billableUsd` + tokens; provider/model dimension keys |

> **32-9 coupling:** the `AGENT.USAGE.RECORDED` type + `costBasisUsd`/`billableUsd` fields are owned by
> 32-9, whose story file does not exist yet. Align names before merge. Until usage events exist, cost
> columns soft-degrade to `null`/`0` and backfill on the next `RebuildAsync`.

---

## Architecture

**Events (tenant `domain_events`, source of truth) → cursor-tracked idempotent fold → materialized
`BenchmarkProjection` rows (tenant schema) → leaderboard read (tenant) / k-anonymous fleet rollup (admin,
public agents only).**

```
tenant t_<hex>.domain_events  (AGENT.TASK.* / AGENT.OUTCOME.* / AGENT.DEFECT.* / AGENT.USAGE.* / …)
        │  IEventRepository.QueryAgentEventsForProjectionAsync(tenant, typePrefix, SequenceNumber > cursor)
        ▼
BenchmarkProjectionService.ProjectAsync(tenant, dimension)      ← background projector / on-demand (OFF the run path)
   foreach event ordered by SequenceNumber:
       key = dimensionKeyFor(dimension, Tags)   // agentId | provider | promptRef | personaId
       bucket[key].apply(event)                 // counts, successRate, iterations, defects, latency sample, cost/billable
       cursor[key] = SequenceNumber             // idempotency + incremental resume
   upsert BenchmarkProjection rows  +  persist BenchmarkProjectionCursor   (tenant t_<hex>)
        │
        ├── GET /api/v1/orgs/{tenantId}/agents/leaderboard      (MemberAccess)  → ranked + belowThreshold
        ├── POST /api/v1/orgs/{tenantId}/agents/leaderboard/rebuild (owner/admin) → 202 → RebuildAsync (reset→replay)
        └── GET /api/v1/admin/agents/{agentId}/benchmark        (PlatformOwnerAccess, PUBLIC agent only)
                → per-tenant fan-out over warm tenants reading pre-folded rows
                → k-anonymity (≥ minTenants) aggregate; NEVER a tenant id / per-tenant row / private agent
```

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns the benchmark data? | The sole user (single tenant). | The tenant that generated it — ALWAYS tenant-scoped; one public agent → many per-tenant datasets. |
| Who reads the leaderboard? | The user (member-read). | Any tenant member, own tenant only (`MemberAccess`). |
| Who can rebuild? | The user. | tenant_owner/tenant_admin; `member` → 403. |
| Can a platform admin read a tenant's benchmark? | N/A. | **No** — only the k-anonymized public-agent fleet rollup; never a single tenant's data or a tenant id. |
| Where do rows + cursors live? | The single tenant store. | The originating tenant's `t_<hex>` schema (`ITenantDbContextFactory`). |
| Mode source | `ITammaModeProvider` (process-stable). | same |

---

## Task breakdown (TDD — failing test first per task)

### Task 1 — Projection entities + tenant migration (AC 1, 3, 7)

**Scope:** Two tenant-resident entities (`BenchmarkProjection`, `BenchmarkProjectionCursor`), DbSets,
model config (unique key, JSON column, transitional `TenantId`), additive Tenant migration.

**Files:**
- New: `src/Tamma.Data/Entities/BenchmarkProjection.cs`, `BenchmarkProjectionCursor.cs`
  (shapes per the story Technical Design; `UNIQUE (TenantId, Dimension, DimensionKey, Window)` NULLS NOT
  DISTINCT on both).
- Modify: `src/Tamma.Data/TenantDbContext.cs` (DbSets); `src/Tamma.Data/TammaModelConfiguration.cs`
  (`ConfigureTenantEntities` additions — unique index, `DefectRateByCategory` as JSON/text column).
- New: `src/Tamma.Data/Migrations/Tenant/<ts>_AddBenchmarkProjections.cs` (`dotnet ef migrations add
  AddBenchmarkProjections --context TenantDbContext`).

**Tests (first):** `tests/Tamma.Api.Tests/Agents/BenchmarkProjectionEntityTests.cs` (or fold into the
service tests) — InMemory + Npgsql parity for the two entities; unique-key upsert collapses a duplicate
(dimension, key, window) to one row; migration applies + rolls back cleanly; `has-pending-model-changes`
reports none.

**Acceptance:**
- [ ] Entities map on `TenantDbContext`; unique key enforced; JSON `DefectRateByCategory` round-trips.
- [ ] Additive Tenant migration applies/rolls back; no pending model changes after.

### Task 2 — Projection engine: idempotent cursor-tracked fold (AC 1, 2, 3, 7, 9)

**Scope:** `IBenchmarkProjectionService` + `BenchmarkProjectionService` (the reducer), the additive
event-query method, percentiles, `ProjectAsync` (incremental) + `RebuildAsync` (reset→replay), and the
off-run-path background projector.

**Files:**
- New: `src/Tamma.Api/Services/Agents/IBenchmarkProjectionService.cs`, `BenchmarkProjectionService.cs`
  (`dimensionKeyFor`; per-bucket reducer: counts → successRate; `iterationsToDone` mean; per-category
  defect rate; latency p50/p95 from a sorted-sample/t-digest; cost-basis/billable means; cursor upsert).
- New: `src/Tamma.Api/Services/Agents/BenchmarkProjectorBackgroundService.cs` (interval + on-demand;
  iterate warm tenants; non-blocking failure → log + retry, never throw into a run).
- New: `src/Tamma.Api/Services/Agents/BenchmarkEventTypes.cs` (`BENCHMARK.PROJECTION.UPDATED`/`.REBUILT`
  + the consumed source-type prefix constants).
- Modify: `src/Tamma.Data/Repositories/IEventRepository.cs` + `EventRepository.cs` — add
  `QueryAgentEventsForProjectionAsync(Guid tenantId, string? typePrefix, long afterSequence, int limit)`
  (tenant-scoped, `SequenceNumber > afterSequence` ordered ascending, `NotSupportedException` on null
  tenant — same hard guard as `QueryWithPaginationAsync`).

**Tests (first):** `tests/Tamma.Api.Tests/Agents/BenchmarkProjectionServiceTests.cs` —
1. **Metric correctness** on a hand-built fixture stream (known success/fail/partial, iteration counts,
   defect categories, latency distribution, costs) → assert exact `successRate`, `avgIterationsToDone`,
   `defectRateByCategory[*]`, `latencyP50Ms`/`P95Ms` (hand-computed), `avgCostBasisUsd`/`avgBillableUsd`,
   `runCount`.
2. **Idempotency** — fold twice → one result, no double-count (cursor skip).
3. **Incremental-vs-rebuild equivalence** — incremental advance == fresh full rebuild (byte-identical).
4. **Soft-degrade** — stream with no `AGENT.USAGE.RECORDED` → cost columns null/0; add usage + rebuild →
   backfilled.
5. **Non-blocking** — induced write failure logged + retried, never thrown.

**Acceptance:**
- [ ] Fold is pure + idempotent on `SequenceNumber`; incremental == full rebuild.
- [ ] All 9 metrics computed correctly (percentiles from distribution, not averaged).
- [ ] Fold runs off the run path; failure never propagates.

### Task 3 — Tenant leaderboard API (AC 4, 5, 8)

**Scope:** Member-readable leaderboard read (ranked + belowThreshold), owner/admin rebuild trigger.

**Files:**
- New: `src/Tamma.Api/Endpoints/AgentLeaderboardEndpoints.cs`
  (`GET /api/v1/orgs/{tenantId}/agents/leaderboard?dimension=&window=&minRuns=` → tie-break chain
  `successRate desc → avgIterationsToDone asc → avgCostBasisUsd asc → key asc`; `ranked` vs
  `belowThreshold` split; `POST .../leaderboard/rebuild` → 202 → `RebuildAsync`, owner/admin only).
- New: `src/Tamma.Api/Dtos/Agents/BenchmarkDtos.cs` (leaderboard / row wire shapes).
- Modify: `src/Tamma.Api/Program.cs` — map both under `/api/v1/orgs/{tenantId}` with
  `RequireTenantMembershipFilter`; rebuild gated to tenant_owner/tenant_admin.

**Tests (first):** `tests/Tamma.Api.Tests/Agents/BenchmarkLeaderboardEndpointsTests.cs` —
1. **Ordering + tie-break** — deliberate `successRate` ties resolve down the documented chain.
2. **Min-sample guard** — `runCount < minRuns` → `belowThreshold`, not `ranked`.
3. **RBAC** — member can read; member → 403 on rebuild; owner/admin → 202.
4. **Dimension validation** — unknown dimension → 400.

**Acceptance:**
- [ ] Leaderboard ordering + tie-break + min-sample guard deterministic.
- [ ] Read is member-level; rebuild is owner/admin; member rebuild → 403.

### Task 4 — Platform-admin public-agent fleet rollup, k-anonymous (AC 6)

**Scope:** The only cross-tenant view: `GET /api/v1/admin/agents/{agentId}/benchmark`
(`PlatformOwnerAccess`), public agents only, k-anonymized.

**Files:**
- Modify: `src/Tamma.Api/Endpoints/AgentLeaderboardEndpoints.cs` (add the admin rollup handler);
  `src/Tamma.Api/Program.cs` (map under `/api/v1/admin/agents/{agentId}/benchmark`, `PlatformOwnerAccess`).
- Service: add `GetPublicAgentFleetRollupAsync(agentId, window, minTenants)` to
  `BenchmarkProjectionService` — reject non-public/private agent (404); per-tenant fan-out over warm
  tenants reading their pre-folded rows for `agentId`; aggregate; suppress below `minTenants` (default 5).

**Tests (first):** `tests/Tamma.Api.Tests/Agents/BenchmarkKAnonymityTests.cs` (docker-bound) —
1. k-1 contributing tenants → suppressed (404/empty).
2. k tenants → aggregated metrics returned; **no `tenantId` anywhere in the JSON** (assert via inspection).
3. private agent → 404 regardless of tenant count.
4. a member / tenant_owner (non-platform) → 403 on the admin route.

**Acceptance:**
- [ ] Public agent only; below-k suppressed; no tenant id / per-tenant row ever returned.
- [ ] Private agent → 404; non-platform-owner → 403.

### Task 5 — Tenant isolation suite + DI wiring (AC 5, 10)

**Scope:** The cross-cutting isolation guarantee + Program.cs DI.

**Files:**
- Modify: `src/Tamma.Api/Program.cs` (DI: `IBenchmarkProjectionService`, `BenchmarkProjectorBackgroundService`
  hosted service).
- New: `tests/Tamma.Api.Tests/Agents/BenchmarkIsolationTests.cs` (docker-bound).

**Tests (first):**
1. Seed benchmark rows for tenant A + B; `GET /orgs/{B}/agents/leaderboard` returns only B's.
2. Member of A hitting B's path → 403/404 (`RequireTenantMembershipFilter`).
3. No per-tenant cross-tenant leaderboard route exists; platform owner has no per-tenant read.
4. Public agent run by A leaves benchmark rows only in A's schema (none on CP, none visible to admin
   per-tenant).

**Acceptance:**
- [ ] 0 cross-tenant benchmark reads possible; platform admin denied per-tenant.
- [ ] Full suite green; build clean; `sg docker -c "dotnet test ..."` green for docker-bound suites.

---

## Task order & dependencies

Task 1 (entities/migration) → Task 2 (fold engine + event query) → Task 3 (tenant leaderboard) →
Task 4 (admin k-anonymous rollup) → Task 5 (isolation suite + DI). Task 4 depends on Task 2's pre-folded
rows + the fan-out; Task 5 ties the whole isolation story and wires DI (do it last so all routes exist).

**Cross-story prerequisites:** 32-6 (source trail events + `SequenceNumber` convention) and 32-8
(outcome/defect events + `BugCategory`) must be merged (hard). 32-9 (usage/cost events) is soft — cost
columns degrade until it lands; align event/field names before merge. 32-7 (panels) is soft for the
persona dimension.

## Risks

- **Cross-tenant leakage (Critical):** mitigated structurally — per-tenant schema + `ITenantDbContextFactory`,
  no per-tenant cross-tenant route, null-tenant scoped reads throw, explicit isolation suite. The admin
  rollup is the one cross-tenant path and is public-agent-only + k-anonymized; the negative tests
  (k-1 suppressed, no tenant id, private→404) are gates.
- **Incremental fold diverges from full rebuild (High):** `SequenceNumber` cursor + a pure reducer; the
  rebuild-equivalence test is a merge gate. Never key the fold on `CreatedAt` (same-ms collisions).
- **32-9 usage event undefined (Medium):** soft-degrade to null/0 cost columns + rebuild backfill;
  align `AGENT.USAGE.RECORDED` type + `costBasisUsd`/`billableUsd` field names with 32-9 before merge —
  do not invent a parallel event.
- **Fold on the run path (High):** off-run-path background projector; non-blocking failure (log+retry);
  test induces a write failure and asserts the run is unaffected.
- **Percentile accuracy on skewed latency (Medium):** compute p50/p95 from the run-latency distribution
  (sorted-sample or t-digest), not by averaging; hand-computed fixture assertion.
- **Tiny-sample rows ranked as meaningful (Medium):** min-sample guard splits `ranked` vs `belowThreshold`.
- **Migration discipline (Low):** the Tenant context has an `InitialTenant` baseline; this migration is
  additive — still verify `has-pending-model-changes` reports none, and mirror entity config in
  `TammaModelConfiguration.cs` (the single source).

## Definition of done

- [ ] All 5 tasks' ACs checked; story ACs 1-10 satisfied.
- [ ] Metric-correctness, idempotency/rebuild-equivalence, ordering/tie-break/min-sample,
      tenant-isolation, and k-anonymity test suites green (docker-bound via `sg docker -c "dotnet test ..."`).
- [ ] `has-pending-model-changes` clean after the Tenant migration; build clean.
- [ ] Event type + tag/data field names aligned with 32-6/32-8/32-9 (no parallel event invented).
- [ ] No cross-tenant per-tenant read path; admin rollup public-only + k-anonymized + no tenant id.
</content>
