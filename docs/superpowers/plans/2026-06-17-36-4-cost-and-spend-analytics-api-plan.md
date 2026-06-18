# Story 36-4 — Cost & Spend Analytics API (BYOK vs Platform) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation. Story file:
> `docs/stories/epic-36/story-36-4/36-4-cost-and-spend-analytics-api.md`.

**Goal:** Ship the tenant-facing **read side** for cost & spend. A new
`GET /api/v1/orgs/{tenantId}/analytics/cost` endpoint, backed by a `CostAnalyticsService`, reads the
per-tenant `analytics_usage_daily` fact table (Story 36-1, populated by Story 36-2) and returns a
daily spend time-series that **separates BYOK token cost** (raw provider cost, informational, never
marked up) **from platform-provided billable spend** (`PlatformBilledUsd`, already materialized
upstream), plus month-to-date + linear-run-rate projection, a `BudgetConfig` join for
budget-vs-actual, and a trend vs the prior window. When the projection crosses the budget the
service emits one tenant-tagged `ANALYTICS.COST.BUDGET_PROJECTED_EXCEEDED` DCB event (best-effort,
per-period deduped) for the alerting / scheduled-report pipeline.

**Seed note:** PAB spec `/tmp/pab_stories/36-4.json` — "analytics read side; the billing/charging
mechanics remain in (re-targeted) Epic 20; reuses BudgetConfig for budget context." The markup
*engine* is Story 34-5; `PlatformBilledUsd` is materialized by Story 36-2 — this story READS
cost/spend, it does **not** compute markup.

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa engine).
Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/` (xUnit; docker-bound suites run via
`sg docker -c "dotnet test ..."`). The legacy TypeScript `packages/api` is **deleted** — never a
target.

---

## Non-goals (YAGNI guard)

- **NO markup / margin math.** The markup engine is Story 34-5; the margin is baked into
  `analytics_usage_daily.PlatformBilledUsd` by Story 36-2. This endpoint SUMS that column and never
  multiplies by a margin. (Story AC4 is the guardrail: "margin config has zero effect on output".)
- **NO billing / charging / metering / limit enforcement.** Invoicing, overage line items, and
  caps live in (re-targeted) Epic 20. This story reports spend; it never charges, blocks, or caps.
- **NO projection / population.** The fact tables are filled by Story 36-2's
  `HourlyAnalyticsRollupWorkflow` fan-out. This story writes **no** fact row.
- **NO schema change to the fact tables.** It reads `analytics_usage_daily`. The only new persisted
  artefact is the DCB event it emits.
- **NO budget write/limit surface.** `BudgetConfig` is read-only here; write/limits stay in Epic 20
  and the existing budget API.
- **NO new auth policy.** Reads ride the established `/api/v1/orgs/{tenantId:guid}` group
  (`MemberAccess` + `RequireTenantMembershipFilter`).
- **NO hourly-grain endpoint.** Daily grain only (`analytics_usage_daily`); budget projection works
  on days.

---

## Current-state findings (verified 2026-06-17, repo @ main)

### Targets that DO exist (read / extend)

| Artefact | Path | Role for 36-4 |
|---|---|---|
| `BudgetConfig` entity | `apps/tamma-elsa/src/Tamma.Data/Entities/BudgetConfig.cs` | Read-only budget context. Keyed `(TenantId, AccountId)`; `AccountId` is the **tenant GUID string** today; carries `LimitUsd`, `AlertThreshold`, `PeriodDays`. |
| `BudgetConfigRepository` | `apps/tamma-elsa/src/Tamma.Data/Repositories/BudgetConfigRepository.cs` | `GetAsync(Guid? tenantId, string accountId, CancellationToken)` — the budget join. |
| `ProviderDiagnostic` | `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs` | Background only — the raw per-call cost source 36-2 projects from. **No `BillingMode` column yet** (Story 35-2 adds it). 36-4 does NOT read this directly. |
| `ITenantDbContextFactory` | `apps/tamma-elsa/src/Tamma.Data/Abstractions/ITenantDbContextFactory.cs` | `CreateAsync(tenantId, ct)` — the per-tenant data plane. Each call returns a fresh `await using` context. |
| `IEventRepository` | `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` | `AppendAsync(DomainEvent)` + `GetLastByTypeAsync(tenantId, type)` — the DCB emit + dedup-check path. |
| `DomainEvent` | `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` | `Type`, `TenantId`, JSONB `Tags`/`Metadata`/`Data`, `SequenceNumber`. The event shape. |
| `/api/v1/orgs/{tenantId:guid}` group | `apps/tamma-elsa/src/Tamma.Api/Program.cs` (~1512) | `MapGroup("/api/v1/orgs").RequireAuthorization("MemberAccess")`; every `{tenantId:guid}/...` route adds `.AddEndpointFilter<RequireTenantMembershipFilter>()`. The exact RBAC pattern 36-4's route uses. |
| `RequireTenantMembershipFilter` | `apps/tamma-elsa/src/Tamma.Api/Authorization/RequireTenantMembershipFilter.cs` | Rejects cross-tenant callers before the handler runs. |
| `AdminAnalyticsEndpoints` | `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs` | Owner-only `/api/admin/analytics/*` precedent for endpoint shape + UTC query-binding gotcha (force `DateTimeKind.Utc`). |
| `ITammaModeProvider` | `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs` | Process-stable mode (`SingleUser`/`SaaS`) for the event tag. |

### Targets that do NOT exist yet (NEW in this story or upstream)

| Artefact | Status |
|---|---|
| `Tamma.Api/Endpoints/TenantAnalyticsEndpoints.cs` | **NEW (this story).** No tenant-scoped analytics endpoint exists today. |
| `Tamma.Api/Services/Analytics/CostAnalyticsService.cs` + `ICostAnalyticsService.cs` | **NEW (this story).** |
| `AnalyticsUsageDaily` entity / `analytics_usage_daily` table | **NEW upstream — Story 36-1** (drafted, not yet built). 36-4 reads it; cannot land until 36-1 + 36-2 are merged (or stubbed). |
| `analytics_usage_daily.PlatformBilledUsd` populated | **Story 36-2** (drafted). The single source of truth 36-4 sums; BYOK rows already 0. |
| `billing_mode` / `ProviderDiagnostic.BillingMode` | **Story 35-2** (Epic 35) — not in the C# code yet. Soft dependency: absent ⇒ 36-2 defaults rows to `platform` ⇒ 36-4 reports all spend as `platformBilledUsd`, `byokCostUsd=0`. Well-formed either way. |

**Key gotcha:** `BudgetConfig.AccountId` is the tenant GUID **as a string** — call
`budgets.GetAsync(tenantId, tenantId.ToString(), ct)`. A NULL-`TenantId` row is the platform default
(fallback only in single-user). Do not invent a new scope key.

---

## Architecture

**Read fact table → split by cost basis → project → join budget → (maybe) emit event → respond.**

1. **`ICostAnalyticsService.GetCostAsync(tenantId, from, to, groupBy, mode, ct)`** — opens the
   tenant's `TenantDbContext` via `ITenantDbContextFactory`, reads `analytics_usage_daily` for the
   window, and aggregates two separate measures per day (and per provider/agent when grouped):
   - `byokCostUsd` = Σ `CostUsd` where `CostBasis = byok` (informational).
   - `platformBilledUsd` = Σ `PlatformBilledUsd` (billable; byok rows are already 0 — structural).
2. **Projection** — month-to-date `platformBilledUsd` + linear run-rate
   (`mtd / daysElapsed * daysInMonth`) against an injected `TimeProvider`.
3. **Budget join** — `BudgetConfigRepository.GetAsync(tenantId, tenantId.ToString(), ct)` →
   `budgetUsd`, `alertThreshold`, and the budget-vs-actual fields (`budgetUtilizationPct`,
   `projectedUtilizationPct`, `projectedToExceedBudget`). All `null` when no budget configured.
4. **Trend** — same aggregation over the prior equivalent window → delta % (platform + byok).
5. **DCB event** — when `projectedToExceedBudget` and no `ANALYTICS.COST.BUDGET_PROJECTED_EXCEEDED`
   event exists for `(tenantId, period)` this month, append one (best-effort, swallowed on failure,
   tenant-tagged).
6. **Endpoint** — `GET /api/v1/orgs/{tenantId:guid}/analytics/cost` on the existing `orgs`
   `MemberAccess` group + `RequireTenantMembershipFilter`; validates/defaults/clamps the window and
   `groupBy`.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns the cost data? | The sole user — their (only) tenant schema. | The tenant — its `t_<hex>` schema. |
| Who may read it? | The user (no RBAC). | Any tenant member (`MemberAccess` + membership filter); `member` reads with **no** elevation (read/limits split per CLAUDE.md prompt-store RBAC). |
| Cost-basis split | Usually all `platform` (no BYOK signal self-hosted) ⇒ `byokCostUsd` may be 0. | `byok`/`platform` per the 35-2 `billing_mode` baked into `CostBasis` by 36-2. |
| Budget source | `BudgetConfig` for the sole tenant (or platform-default NULL row). | `BudgetConfig` for that tenant. |
| Budget-exceeded event | Lands in the user's (only) tenant stream (`TenantId` set). | Tenant's stream (`TenantId` set) ⇒ tenant-scoped alerting. |
| Cross-tenant leakage | N/A. | Prevented twice: membership filter + physical per-tenant schema. |

Mode never changes the wire shape — both resolve to exactly one tenant schema per request; `mode`
is carried only as an event tag.

---

## Task breakdown

### T1: DTOs + DCB event constant (core scaffolding)

**Scope:** Response shape + the event name. No logic yet.

**Files:**
- New: `src/Tamma.Api/Dtos/Analytics/CostAnalyticsResponse.cs` — `CostAnalyticsResponse`,
  `CostSeriesBucket` (`Day`, `Group?`, `ByokCostUsd`, `PlatformBilledUsd`), `CostSummary`
  (`ByokCostUsd`, `MtdPlatformBilledUsd`, `ProjectedPlatformBilledUsd`, `BudgetUsd?`,
  `AlertThreshold?`, `BudgetUtilizationPct?`, `ProjectedUtilizationPct?`, `ProjectedToExceedBudget`),
  `CostTrend`, and a `CostGroupBy` enum (`None|Provider|Agent`).
- New: `src/Tamma.Api/Services/Analytics/CostAnalyticsEvents.cs` —
  `public const string BudgetProjectedExceeded = "ANALYTICS.COST.BUDGET_PROJECTED_EXCEEDED";`
  (AGGREGATE.ACTION.STATUS).

**Tests (first):** trivial DTO/enum presence + the event-constant value (`AnalyticsRollupEventsTests`
precedent — assert the string literal and `.` segment shape).

**Acceptance:**
- [ ] DTOs compile; `CostGroupBy` parses `provider`/`agent` (case-insensitive), rejects others.
- [ ] Event constant equals `ANALYTICS.COST.BUDGET_PROJECTED_EXCEEDED`.

### T2: `ICostAnalyticsService` + `CostAnalyticsService` (the read + project + budget core)

**Scope:** All aggregation, projection, budget join, trend, and the deduped event emit. **No markup
math, no fact write.**

**Files:**
- New: `src/Tamma.Api/Services/Analytics/ICostAnalyticsService.cs`,
  `CostAnalyticsService.cs`. Ctor deps: `ITenantDbContextFactory`, `IBudgetConfigRepository`
  (or `BudgetConfigRepository`), `IEventRepository`, `TimeProvider`, `ILogger<CostAnalyticsService>`.
- Aggregation: read `analytics_usage_daily` for `[fromUtc, toUtcExclusive)`; split
  `byokCostUsd` (Σ `CostUsd` where `CostBasis=Byok`) and `platformBilledUsd` (Σ `PlatformBilledUsd`);
  group by `Day` (+ `Provider`/`AgentId` when requested; `AgentId NULL → "unattributed"`).
- Projection: MTD `platformBilledUsd` + `mtd/daysElapsed*daysInMonth` via `TimeProvider`.
- Budget join: `GetAsync(tenantId, tenantId.ToString(), ct)`; budget-vs-actual fields; `null` when
  absent.
- Trend: prior equivalent window aggregation → delta % (platform + byok); `null` when prior empty.
- Event: when `projectedToExceedBudget`, check `GetLastByTypeAsync(tenantId, BudgetProjectedExceeded)`
  for a same-period event; if none, `AppendAsync` one (tenant-tagged, `mode`, budget/projection in
  `Data`); swallow append failures (WARN), never throw into the read.

**Tests (first) — `CostAnalyticsServiceTests` (InMemory):** split; projection (fixed
`TimeProvider`, incl. day-1 + zero-MTD); budget join present/absent; grouping +
`NULL→"unattributed"` reconciling to total; trend present/empty; **no-re-markup** (two mocked margin
configs → identical output); event-on-breach + per-period dedup + no-emit-under + swallowed
append failure; empty-state (zero rows → well-formed empty series, zeroed summary, null trend).

**Acceptance:**
- [ ] `byokCostUsd` sums only `byok` rows' `CostUsd`; `platformBilledUsd` sums `PlatformBilledUsd`
      (byok rows contribute 0).
- [ ] Projection == `mtd/daysElapsed*daysInMonth`; no divide-by-zero on day-1/zero-MTD.
- [ ] Budget-vs-actual fields correct with a budget; all `null` without one.
- [ ] Grouped buckets reconcile to the ungrouped total; `AgentId NULL` → `"unattributed"`.
- [ ] Output is byte-identical under two different margin configs (no re-markup).
- [ ] Exactly one event per `(tenantId, period)` on breach; none under budget; append failure does
      not fail the response.

### T3: `TenantAnalyticsEndpoints` + Program.cs wiring (the HTTP surface)

**Scope:** Minimal-API handler + validation/defaulting/clamping + DI/route registration.

**Files:**
- New: `src/Tamma.Api/Endpoints/TenantAnalyticsEndpoints.cs` — `GetCost(Guid tenantId,
  [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? groupBy,
  ICostAnalyticsService svc, ITammaModeProvider mode, CancellationToken ct)`. Default window =
  current calendar month; force `DateTimeKind.Utc` (AdminAnalyticsEndpoints gotcha); `400` on
  `from > to` or bad `groupBy`; clamp window to ≤400 days.
- Modify: `src/Tamma.Api/Program.cs` —
  `orgs.MapGet("/{tenantId:guid}/analytics/cost", TenantAnalyticsEndpoints.GetCost)
  .AddEndpointFilter<Tamma.Api.Authorization.RequireTenantMembershipFilter>();` and register
  `ICostAnalyticsService` (scoped) + `TimeProvider.System` if not already registered.

**Tests (first) — `CostAnalyticsEndpointTests`:** validation matrix (`from>to`→400, bad
`groupBy`→400, omitted window→current month); default `groupBy=None`; member read returns 200 with
a well-formed body; `mode` tag threaded into the service call.

**Acceptance:**
- [ ] Route mounted under the `MemberAccess` `orgs` group with `RequireTenantMembershipFilter`.
- [ ] Validation/defaulting/clamping behave per AC12; UTC kind forced on the window.

### T4: Integration suite (Postgres 17 — BYOK split, isolation, RBAC)

**Scope:** Prove the structural BYOK=0 guarantee, physical isolation, and RBAC end-to-end against a
real schema (InMemory can't model per-tenant schemas — `ConventionStoreMigrationTests` rationale).

**Files:**
- New: `tests/Tamma.Api.Tests/Analytics/CostAnalyticsIsolationTests.cs` (Postgres 17 Testcontainer,
  following `SchemaPerTenantMigrationTests`).

**Tests:**
- Fully-BYOK tenant (all `analytics_usage_daily` rows `byok`, so `PlatformBilledUsd=0`) →
  `summary.platformBilledUsd==0`, `projectedPlatformBilledUsd==0`, `byokCostUsd>0`.
- Two tenant schemas, different cost profiles → request for A returns only A; budget join reads only
  A's `BudgetConfig`.
- RBAC: tenant `member` → 200; non-member / cross-tenant → 404/403 (filter rejects pre-handler).

**Acceptance:**
- [ ] Fully-BYOK tenant shows `platformBilledUsd=0` + non-zero `byokCostUsd`.
- [ ] A's request never returns B's rows; budget join is tenant-scoped.
- [ ] Cross-tenant caller blocked before the handler; member read succeeds.
- [ ] Suite green via `sg docker -c "dotnet test ..."`.

---

## Task order & dependencies

T1 → T2 → T3 → T4. T2 is the heart (TDD it hardest). T1 is pure scaffolding; T3 wraps T2 in HTTP;
T4 proves the cross-cutting guarantees (BYOK=0, isolation, RBAC) that unit tests can't.

**Upstream gating:** the service reads `AnalyticsUsageDaily` (Story 36-1) populated with
`PlatformBilledUsd` (Story 36-2). Implement against the **drafted** 36-1 entity shape; if 36-1/36-2
have not merged when 36-4 starts, T2/T4 are blocked on the real table — coordinate the merge order
(36-1 → 36-2 → 36-4) or land a thin local test-fixture entity that matches 36-1 AC1/AC2 exactly and
swap to the real one on merge.

## Risks

- **Re-markup leak (highest correctness risk):** the temptation is to "compute billed = cost ×
  margin" in the endpoint. That double-charges and forks the source of truth. Mitigation: AC4's
  "margin config has zero effect" test is mandatory and the service takes **no** dependency on the
  markup engine (34-5) or any pricing config. If a reviewer sees a margin import in this story,
  reject it.
- **BYOK split correctness depends on 36-2's invariant** (`byok ⇒ PlatformBilledUsd=0`). If 36-2
  ever writes a non-zero billed amount on a byok row, the split breaks silently. Mitigation: the
  fully-BYOK integration test (T4) asserts `platformBilledUsd==0` end-to-end, catching a 36-2
  regression at this layer too.
- **Projection edge cases:** day-1 (`daysElapsed=1`), zero-MTD (`projected=0`, no divide-by-zero),
  month boundaries, leap-Feb `daysInMonth`. Inject `TimeProvider` and pin each case in T2.
- **Event spam from dashboard polling:** the budget-exceeded DCB event must be per-period deduped
  (one per `(tenantId, period)`), or a polled dashboard floods the alert pipeline. Dedup is a
  pre-append `GetLastByTypeAsync` + period-tag check; secondary throttle lives in the downstream
  alert rule. Best-effort emission must never throw into the read.
- **Budget scope key:** `BudgetConfig.AccountId == tenantId.ToString()` today. Hardcoding a
  different key returns no budget silently. Pin the exact call in T2/T4.
- **Window default timezone:** "current calendar month" / MTD is UTC (matching the fact table's UTC
  `Day` bucket). Force `DateTimeKind.Utc` on bound query params (AdminAnalyticsEndpoints precedent)
  so a Local-parsed `from`/`to` doesn't shift the window.
- **Upstream not merged:** see "Upstream gating" — the single biggest schedule risk is starting
  36-4 before 36-1/36-2 land. Sequence the merge or use a matching test fixture entity.
