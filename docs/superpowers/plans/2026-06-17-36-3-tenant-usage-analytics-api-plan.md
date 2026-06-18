# Story 36-3 — Tenant Usage Analytics API — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan phase-by-phase. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every phase writes its
> failing test before the implementation. Story file:
> `docs/stories/epic-36/story-36-3/36-3-tenant-usage-analytics-api.md`.

**Goal:** Expose a per-tenant, RBAC-gated, read-only query API over the Story 36-1 dimensional
fact tables (`analytics_usage_hourly` / `analytics_usage_daily`, populated by Story 36-2) that
returns time-bucketed usage (workflows, agent dispatches, tokens, cost) broken down by provider,
agent, workflow, and repo over an arbitrary window at hourly or daily grain — plus a top-N
single-dimension breakdown. Endpoints are tenant-scoped under `/api/v1/orgs/{tenantId}/…`, gated by
the existing tenant-membership filter (any member may read; cross-tenant route → 403), window-
bounded (365-day max, hour-granularity cap), and UTC-binding-correct, serving the dashboard
(36-6) and exporter (36-8) one stable contract.

**Seed note:** Epic 36 turns the DCB stream + per-tenant operational data into a multi-dimensional
analytics product (`docs/stories/epic-36/`). 36-1 built the schema, 36-2 the projection; 36-3 is
the **first read surface**. Per-mode: single-user resolves the principal to the sole user's tenant
schema, SaaS to the tenant's schema — identical endpoint shape, auth middleware decides binding
(CLAUDE.md prompt-store precedent). Member-read RBAC mirrors prompt-store "GET resolved → any
member".

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa
engine). Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/` (xUnit; docker-bound
suites run via `sg docker -c "dotnet test ..."` — the build itself needs no wrapper; the session
docker group is stale).

---

## Non-goals (YAGNI guard)

- **NO schema change.** This story reads 36-1's tables verbatim. It adds no column, index, or
  migration. (If a read needs an index 36-1 didn't ship, raise it against 36-1 — don't add it
  here.)
- **NO population logic.** Story 36-2 owns the DCB→fact projection. The query API reads only the
  pre-aggregated rows; it never re-scans `domain_events` and never reads `ProviderDiagnostic` (a
  36-2 source).
- **NO export format / NO dashboard UI.** 36-8 owns CSV/JSON exports; 36-6 owns the
  `packages/dashboard-user` UI. This story ships the JSON contract both consume — and nothing more.
- **NO change to the platform-owner analytics surface.** `AdminAnalyticsEndpoints`,
  `IPlatformAnalyticsService`, and `platform_analytics_hourly` (Story 28-10) stay the cross-tenant
  `OwnerAccess` business-analytics path, untouched. This story is the parallel tenant-facing
  mirror.
- **NO duplication of Epic 5 / Epic 23 OPS/health metrics.** Usage analytics only (workflows,
  dispatches, tokens, cost). System health / liveness / readiness stays where it is.
- **NO mutations.** GET only. No ack/resolve/write — so no admin+ RBAC gate is introduced (unlike
  `AlertEndpoints`' tenant mutations).
- **NO per-mode handler branch.** The endpoint shape is identical across single-user/SaaS; the
  membership filter + per-tenant context do the mode work.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### Target architecture (C# `apps/tamma-elsa` — `packages/api` is DELETED, never a target)

| Concern | Verified path | Note |
|---|---|---|
| Per-tenant context factory (read seam) | `apps/tamma-elsa/src/Tamma.Data/Abstractions/ITenantDbContextFactory.cs`, `apps/tamma-elsa/src/Tamma.Data/TenantDbContextFactory.cs` | `CreateAsync(tenantId)` → schema-scoped `TenantDbContext`; the physical isolation plane. |
| Fact tables (read targets) | `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` → `AnalyticsUsageHourly` / `AnalyticsUsageDaily` DbSets | **Authored by 36-1 (drafted).** Buckets `Hour`/`Day` (`timestamp with time zone`, UTC); dims `Provider`/`AgentId`/`WorkflowDefinitionId`/`RepoId`/`CostBasis`; measures `TokensIn/Out`, `Workflows*`, `AgentDispatches` (long), `CostUsd`/`PlatformBilledUsd` (decimal(20,4)). No `TenantId` column. |
| Tenant resolution middleware | `apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs` | Resolves tenant + warms the per-tenant pool. **Bypasses `/api/v1/admin/` but NOT `/api/v1/orgs/`** — org routes get tenant binding + the membership filter. |
| Cross-tenant 403 guard | `apps/tamma-elsa/src/Tamma.Api/Authorization/RequireTenantMembershipFilter.cs` | Endpoint filter: 401 unauth, 400 bad route `tenantId`, **403 if caller has no membership in route `{tenantId}`**; stashes role in `HttpContext.Items["TenantRole"]`. This is the AC4/AC12 guard. |
| Member-read policy | `apps/tamma-elsa/src/Tamma.Api/Program.cs` ~line 991, `options.AddPolicy("MemberAccess", …RequireAuthenticatedUser())` | Authenticated-only; the `orgs` group already carries it (`MapGroup("/api/v1/orgs").RequireAuthorization("MemberAccess")`, ~line 1512). |
| **Exact precedent endpoint** | `apps/tamma-elsa/src/Tamma.Api/Endpoints/UserDashboardEndpoints.cs` | `GET /api/v1/orgs/{tenantId:guid}/dashboard/{summary,runs,stats}` — route `{tenantId}` + `ITenantDbContextFactory.CreateAsync(tenantId)` + `.AddEndpointFilter<RequireTenantMembershipFilter>()` (Program.cs ~1599). **Copy this shape.** |
| Forced-UTC binding fix | `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs` `GetEventHistogram` | `DateTime.SpecifyKind(since.Value.ToUniversalTime(), DateTimeKind.Utc)` — replicate for `from`/`to` (AC9). |
| Owner analytics surface (LEAVE INTACT) | `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs`, `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/PlatformAnalyticsService.cs` + `IPlatformAnalyticsService.cs` | Cross-tenant, `OwnerAccess`/admin, reads CP `platform_analytics_hourly`. Do not read/modify. |
| Tenant dashboard SPA (36-6 consumer) | `packages/dashboard-user/` | Consumes this contract later; out of scope here. |
| Tenant-scope mutation RBAC precedent (for contrast) | `apps/tamma-elsa/src/Tamma.Api/Endpoints/AlertEndpoints.cs` (`ListTenantAlerts` etc.) | Tenant alert reads use the same membership filter; mutations inline-gate admin+ via `RequireTenantAdmin`. 36-3 has **no** mutations → no admin+ gate. |

### Key reuse / divergence facts

- **The read seam is `ITenantDbContextFactory`, not a repository.** `UserDashboardEndpoints.GetStats`
  is the canonical pattern: `await using var db = await tenantDbFactory.CreateAsync(tenantId);` then
  LINQ over the DbSet. No `TenantId` predicate is needed for the new fact tables (they have no such
  column — 36-1 §1.4); the filter is on the bucket + dimension only.
- **The cross-tenant guard is already built.** `RequireTenantMembershipFilter` is registered
  (Program.cs ~352) and attached per-route via `.AddEndpointFilter<…>()`. 36-3 just attaches it to
  the two new routes. No new guard code.
- **`MemberAccess` is the right policy.** It only requires an authenticated user; combined with the
  membership filter it yields "any member of the route tenant may read" — exactly AC5. Do **not**
  reach for `OwnerAccess`/`SettingsManage` (owner-only) or `PromptManage`/`ConventionManage`
  (admin+).
- **UTC binding is a known fix, not a discovery.** `AdminAnalyticsEndpoints` already documents the
  Local-vs-UTC binding hazard; 36-3 inherits the exact normalization for `from`/`to`.
- **InMemory vs Postgres test split is established.** Model/grouping shape on InMemory; isolation +
  real `GROUP BY` / `timestamp with time zone` on a Postgres 17 Testcontainer (per
  `SchemaPerTenantMigrationTests` / `ConventionStoreMigrationTests`).

---

## Architecture

**Thin endpoints → one read service → per-tenant fact tables**, reusing the user-dashboard +
membership-filter shape end-to-end:

1. **`TenantAnalyticsDtos.cs`** — request DTOs (`UsageQuery`, `BreakdownQuery`), the
   `AnalyticsWindow` clamp record (default window, 365-day max, hour-granularity cap, forced-UTC),
   the `AnalyticsGranularity`/`AnalyticsDimension`/`AnalyticsMetric` enums + parse helpers (400 on
   bad enum), and response contracts (`UsageResponse`/`BreakdownResponse` with
   `period_start`/`period_end`/`granularity` echoes — AC7/8/9).
2. **`ITenantAnalyticsService` / `TenantAnalyticsService`** — the single read seam. Opens the
   per-tenant context via `ITenantDbContextFactory.CreateAsync(tenantId)`, picks `AnalyticsUsageDaily`
   (day) or `AnalyticsUsageHourly` (hour), applies the half-open `[from, to)` filter on the bucket
   column, and builds the grouped/breakdown projection (NULL dimension preserved) — unit-testable in
   isolation against a seeded InMemory context (AC1/2/3/10).
3. **`TenantAnalyticsEndpoints`** — `GetUsage` / `GetBreakdown` handlers: parse+clamp the window,
   400 on invalid, delegate to the service, return `Results.Ok(...)`. Thin (AC1/3).
4. **Program.cs wiring** — map both routes under the `orgs` group with
   `.AddEndpointFilter<RequireTenantMembershipFilter>()` (member-read + cross-tenant 403) and
   register `ITenantAnalyticsService` (AC4/5/6). CP owner surface untouched (AC11).

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who is the principal? | The sole user — `{tenantId}` = their (only) tenant schema. | The tenant — `{tenantId}` = its `t_<hex>` schema. |
| Who can read (GET)? | The user (no role). | Any tenant member (`MemberAccess` + membership filter); `member` reads succeed (no 403 on GET). |
| Endpoint shape | identical | identical — no per-mode branch; the membership filter + per-tenant context do the work. |
| Isolation plane | search-path schema + connection string (no `TenantId` column/filter). | same — physically separate schema per tenant; schema A unreachable from schema B's context. |
| Cross-tenant leakage | N/A (one tenant). | `RequireTenantMembershipFilter` 403s a non-member of the route tenant; physical schema isolation backstops it. |

---

## Phased TDD tasks

### Phase 1 — DTOs, enums, and the `AnalyticsWindow` clamp (pure, no DB)

- [ ] Write `TenantAnalyticsServiceTests` clamp/UTC cases **red first**: default window;
      400-day → truncate to 365 (effective echoed); `granularity=hour` over 31d → invalid;
      `from >= to` → invalid; bad `granularity`/`groupBy`/`dimension`/`metric` enum → invalid; a
      `+02:00` `from` normalizes to the same UTC instant/bucket as its `Z` equivalent.
- [ ] Add `Tamma.Api/Services/Analytics/TenantAnalyticsDtos.cs`: `UsageQuery`, `BreakdownQuery`,
      `UsageBucketRow`, `BreakdownRow`, `UsageResponse`, `BreakdownResponse`; `AnalyticsGranularity`
      / `AnalyticsDimension` / `AnalyticsMetric` enums + `TryParse`; `AnalyticsWindow.Resolve(...)`
      with forced-UTC (`DateTime.SpecifyKind(v.ToUniversalTime(), DateTimeKind.Utc)`), 30d default,
      365d max-range truncation, hour-granularity 31d cap.
- [ ] Green: clamp/UTC tests pass. **No DB touched yet.**

### Phase 2 — `TenantAnalyticsService` aggregation (InMemory)

- [ ] Write grouping tests **red first** against a seeded InMemory `TenantDbContext`
      (`AnalyticsUsageDaily` + `Hourly` rows spanning multiple providers/agents/workflows/repos,
      including NULL-dimension rows):
  - groupBy absent → one summed row per bucket;
  - groupBy=provider|agent|workflow|repo → one row per `(bucket, dim)`, NULL key surfaced not
    dropped;
  - **reconciliation:** `Σ(grouped per bucket) == ungrouped bucket`;
  - breakdown top-N by each metric ordered desc, `limit` clamp respected.
- [ ] Add `ITenantAnalyticsService` + `TenantAnalyticsService(ITenantDbContextFactory)`:
      `GetUsageAsync` (granularity switch → `AnalyticsUsageDaily`/`Hourly`, half-open `[from,to)`
      bucket filter, GROUP BY bucket [+ dim], summed measures) and `GetBreakdownAsync` (GROUP BY
      dim over window, OrderByDescending(metric), Take(limit)).
- [ ] Green: grouping + reconciliation pass on InMemory.

### Phase 3 — `TenantAnalyticsEndpoints` + Program.cs wiring (RBAC matrix)

- [ ] Write `TenantAnalyticsEndpointsTests` **red first**: member/tenant_admin/tenant_owner →
      200; non-member of route tenant → 403; unauth → 401; single-user sole user → 200; handlers
      reference only `ITenantAnalyticsService` (assert no `IPlatformAnalyticsService` /
      `ControlPlaneDbContext` usage — AC11).
- [ ] Add `Tamma.Api/Endpoints/TenantAnalyticsEndpoints.cs`: `GetUsage` / `GetBreakdown` (parse +
      `AnalyticsWindow.Resolve` → 400 on invalid → service → `Results.Ok`).
- [ ] Wire Program.cs: `orgs.MapGet("/{tenantId:guid}/analytics/usage", …GetUsage)` and
      `…/analytics/usage/breakdown` each `.AddEndpointFilter<RequireTenantMembershipFilter>()`;
      `builder.Services.AddScoped<ITenantAnalyticsService, TenantAnalyticsService>()`.
- [ ] Green: RBAC matrix passes; CP-untouched assertion holds.

### Phase 4 — Postgres 17 integration (isolation + grouping + clamp + UTC)

- [ ] Write `TenantAnalyticsIntegrationTests` **red first** (Postgres 17 Testcontainer, two tenant
      schemas seeded with distinct fact rows, per `SchemaPerTenantMigrationTests`):
  - tenant-A caller on tenant-A route sees only A's rows;
  - tenant-A caller on tenant-B route → 403 and **zero** B rows returned (AC4/AC12);
  - grouping correctness + reconciliation against real Npgsql `GROUP BY`;
  - range clamp (400-day → 365) and hour-cap rejection on real columns;
  - UTC: a non-UTC-offset `from` selects the same buckets as its UTC equivalent on real
    `timestamp with time zone`.
- [ ] Make green; run via `sg docker -c "dotnet test --filter TenantAnalytics ..."`.
- [ ] Full suite green; no `has-pending-model-changes` drift (this story adds no migration —
      confirm none was generated).

---

## Sequencing

Phase 1 → Phase 2 → Phase 3 → Phase 4. Phase 1 is pure (no DB) and unblocks everything. Phase 2
needs the DTOs/enums from Phase 1. Phase 3 needs the service from Phase 2. Phase 4 needs the wired
endpoints from Phase 3 (it drives them through the real host). Phases 1–2 can be done by one agent;
Phase 4's Testcontainer suite is the long pole — start its scaffolding (two-schema seed helper)
early but it can only assert once Phase 3 lands.

**Hard prerequisites:** Story 36-1 (the fact tables/DbSets must exist to compile the service) and
Epic 18/28 (membership filter + factory, already merged). Story 36-2 is a *soft* prerequisite for
**meaningful** integration data — the integration suite seeds fact rows directly (it does not run
the 36-2 projection), so 36-3 can land and be tested before 36-2 merges; the reconciliation
assertion verifies the read-side math, which is independent of how the rows were written.

---

## Risks

- **Cross-tenant leakage is the headline risk.** Mitigation is layered: (1) the route sits under
  the `orgs` group so `RequireTenantMembershipFilter` 403s a non-member of `{tenantId}`; (2)
  `ITenantDbContextFactory.CreateAsync(tenantId)` binds a physically separate schema/connection, so
  even a filter bug can't read another tenant's rows. The integration test asserts BOTH the 403 and
  zero leaked rows. **Do not** use a shared `ControlPlaneDbContext` with a `TenantId` predicate for
  these reads — that would be the leak.
- **Forced-UTC binding off-by-one.** A `from`/`to` parsed as Local shifts the window off the UTC
  bucket boundary and silently drops/double-counts edge buckets. Replicate the
  `AdminAnalyticsEndpoints` normalization exactly and pin it with the offset-equivalence test (the
  bug is invisible without it).
- **Unbounded hourly scan.** `from=1970..to=now&granularity=hour` would scan years of hourly rows.
  The 31-day hour cap (400) + 365-day max range bound the cost. Make the caps configurable but
  shipped with safe defaults.
- **NULL-dimension reconciliation.** Coercing a NULL `AgentId`/`RepoId`/`WorkflowDefinitionId` to
  `"unknown"` would fork the per-dimension total away from the ungrouped total. Surface NULL as a
  `null` key and assert `Σ(grouped) == ungrouped` per bucket — the 36-2 reconciliation contract
  must hold at read time.
- **Accidental CP coupling.** It is tempting to "reuse" `IPlatformAnalyticsService`. That surface
  is cross-tenant `OwnerAccess` over CP `platform_analytics_hourly` — reusing it would either leak
  cross-tenant data or 403 every member. The CP-untouched assertion (AC11) guards against this
  regressing.
- **InMemory does not model schema isolation or date-bucket `GROUP BY` faithfully.** Grouping
  *shape* is fine on InMemory, but isolation + real `timestamp with time zone` window selection +
  Npgsql `GROUP BY` semantics must run on a Postgres 17 Testcontainer (same rationale as
  `ConventionStoreMigrationTests`). Don't certify isolation on InMemory.
- **Schema-edit temptation.** If a grouped read wants an index 36-1 didn't ship, the fix belongs in
  36-1 — adding it here violates the query-only boundary and breaks the
  `has-pending-model-changes` clean gate. Raise it upstream.

---

## Acceptance criteria (plan-level — maps to story ACs)

- [ ] `GET /api/v1/orgs/{tenantId}/analytics/usage` returns time-bucketed usage reading
      `AnalyticsUsageDaily`/`Hourly` via `ITenantDbContextFactory`, with `from`/`to`/`granularity`/
      `groupBy` params and `period_start`/`period_end` echoes (story AC1/2/8).
- [ ] `GET …/analytics/usage/breakdown` returns top-N rows for a single dimension by the selected
      metric (story AC3).
- [ ] Both routes sit behind `RequireTenantMembershipFilter`; a cross-tenant route returns 403 with
      a test asserting **no** cross-tenant rows leak (story AC4/12).
- [ ] GET is member-readable — `MemberAccess` + membership filter, **no** owner/admin gate;
      single-user requires no role (story AC5/6).
- [ ] Window bounded (365-day max, hour cap), forced-UTC binding mirrors `AdminAnalyticsEndpoints`
      (story AC7/9).
- [ ] CP owner analytics surface (`AdminAnalyticsEndpoints`/`IPlatformAnalyticsService`/
      `platform_analytics_hourly`) left intact; handlers reference neither (story AC11).
- [ ] Integration tests (Postgres 17, two schemas) cover grouping correctness + reconciliation,
      range clamping, UTC handling, and cross-tenant 403 (story AC12).
- [ ] No EF migration generated; `has-pending-model-changes` clean; full suite green via
      `sg docker -c "dotnet test ..."`.
