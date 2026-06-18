# Story 36-11 — Analytics Event Catalog, Backfill & Reconciliation (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes its
> tests RED before implementation. Story file:
> `docs/stories/epic-36/story-36-11/36-11-analytics-event-catalog-backfill-and-reconciliation.md`.

**Goal:** Make the Epic 36 analytics substrate *trustworthy in production*. Three additions on top
of the Story 36-2 projection: (a) a typed, drift-guarded **event-field catalog** so an upstream
rename of `costUsd`/`agent_id` fails a test instead of silently zeroing a column; (b) an admin-only
**backfill** that rebuilds `analytics_usage_*` for a tenant + time-range from historical
`domain_events` by replaying the *same* 36-2 idempotent compute hour-by-hour (onboarding existing
tenants, recovering rollup outages); (c) an hourly **reconciliation** that compares the projected
per-tenant totals against a live DCB aggregation and emits `ANALYTICS.RECONCILIATION.MISMATCH`
(WARN) on drift — the in-process twin of the 28-10 fact-table-vs-live fallback and the 20-3
local-vs-Stripe compare.

**Seed note:** Spec `/tmp/pab_stories/36-11.json` (P2, est 3-4 days). Dependencies: 36-1 (fact
tables), 36-2 (`ComputeTenantDimensionalRollupActivity.ComputeAsync` + checkpoint), Epic 4 (DCB),
28-10 (fan-out + fact-table fallback pattern), 20-3 (reconciliation pattern), 28-6 (platform task
queue / 202).

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa engine).
C# activities under `Tamma.Activities/Analytics/`; admin endpoints in `Tamma.Api/Endpoints/`; tests
under `apps/tamma-elsa/tests/Tamma.Activities.Tests/Analytics/` and
`tests/Tamma.Api.Tests/Analytics/` (xUnit; docker-bound suites run via
`sg docker -c "dotnet test ..."`; the build itself needs no wrapper).

> **ARCH guard:** the target is the C# `apps/tamma-elsa` app. `packages/api` (TypeScript) is DELETED —
> never cite or target it. This story extends the schema-per-tenant, per-tenant `TenantDbContext`
> projection seam from 36-2, never the old TS billing path.

---

## Non-goals (YAGNI guard)

- **NO change to the 36-2 projection compute or the 36-1 fact-table schema.** Backfill *reuses*
  `ComputeTenantDimensionalRollupActivity.ComputeAsync` unchanged; reconciliation *reads* the fact
  tables. Any PR that alters projection semantics or fact-table DDL is out of scope.
- **NO tenant self-service backfill.** `POST /api/admin/analytics/backfill` is `PlatformOwnerAccess`
  only — it is an operator recovery/onboarding tool, never a tenant-facing endpoint.
- **NO new dashboard surface.** Mismatch is observable via DCB events + the built-in alert rule +
  logs; backfill status is a JSON `GET`. Tenant-facing analytics UI is later Epic 36 stories.
- **NO reconciliation HTTP endpoint.** Reconciliation is a scheduled best-effort workflow step;
  operators force one via the existing `POST /workflows/run/hourly-analytics-rollup` with `hour`.
- **NO inline backfill.** A multi-month replay runs off-thread on the Story 28-6 `PlatformTaskWorker`
  (202 + pollable status), never on the admin request thread.
- **NO mismatch-as-failure.** A `RECONCILIATION.MISMATCH` is WARN — it never fails a rollup that
  already wrote useful rows.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

| File | Status | Relevance |
|---|---|---|
| `src/Tamma.Activities/Analytics/AnalyticsRollupEvents.cs` | EXISTS | Event catalogue + `BuildEvent`/`TruncateToHour` helpers — extend with `RECONCILIATION.*` / `BACKFILL.*` constants. |
| `src/Tamma.Activities/Analytics/ComputeTenantRollupActivity.cs` | EXISTS | `AggregateLlmUsage` + `AGENT.DISPATCH.%` `EF.Functions.Like` count + `WorkflowInstance.Status` counts — REUSE for the live-totals reconciliation path; `ComputeAsync` pure-DI shape is the backfill template. |
| `src/Tamma.Activities/Analytics/FanOutTenantRollupsActivity.cs` | EXISTS | Per-tenant try/catch + `…_FAILED` emit + continue (28-10 AC5). Mirror exactly for the reconcile fan-out and the backfill per-hour loop. CP-directory target-set query is the tenant-list pattern. |
| `src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupWorkflow.cs` | EXISTS | `Sequence` of steps — append the reconcile fan-out as the final best-effort step (after the 36-2 dimensional fan-out + `EmitHourCompleted` + purges). |
| `src/Tamma.Activities/Analytics/ComputeTenantDimensionalRollupActivity.cs` | 36-2 (drafted) | `ComputeAsync(...)` is the per-hour compute the backfill loops over. **Mark forward** — author 36-2 first or the backfill loop has nothing to call. |
| `src/Tamma.Data/Entities/AnalyticsUsageHourly.cs`, `AnalyticsProjectionCheckpoint.cs`, `CostBasis` enum | 36-1/36-2 (drafted) | Projected-totals source + checkpoint cursor. **Mark forward** — not yet in the codebase. |
| `src/Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs` | EXISTS (28-10) | 3 GET handlers, gated `PlatformOwnerAccess` at `Program.cs:1346-1351`. Add `Backfill` (POST 202) + `GetBackfillStatus` here; wire under the same `admin` group + policy. |
| `src/Tamma.Api/Services/Analytics/PlatformAnalyticsService.cs` | EXISTS | The fact-table-first-with-live-fallback (`ShouldPreferFactTableAsync`, `GetXFromFactTableAsync` vs `GetXAsync`) is the **reconciliation pattern reference** — projected vs live, same helpers. Also holds the event-type constants (`LlmCallSuccess`, `AgentDispatchPrefix/Success/Failed`, `WfStatus*`) the drift guard pins. |
| `src/Tamma.Api/Services/PlatformTasks/IPlatformTaskHandler.cs` + `PlatformTaskWorker.cs` | EXISTS (28-6) | The off-thread 202 execution seam. Add `AnalyticsBackfillTaskHandler : IPlatformTaskHandler` (`TaskType = "analytics.backfill"`). `PlatformTaskTerminalException` for malformed payloads. |
| `src/Tamma.Api/Services/Alerts/Rules/BuiltInAlertRules.cs` | EXISTS | Add `analytics-reconciliation-mismatch` spec; idempotent seeder picks it up by `built_in_key`. |
| `src/Tamma.Data/Entities/DomainEvent.cs` | EXISTS | `Type`, `Tags`, `Data`, `SequenceNumber` (the `BIGSERIAL` total-order cursor — checkpoint column; doc-comment names the `AlertRuleEvaluator` precedent). |
| `src/Tamma.Data/Entities/ProviderDiagnostic.cs` | EXISTS | `ProviderKey`, `InputTokens`/`OutputTokens`, `Cost`, `AgentType`, `ProjectId`. **`BillingMode` is NOT yet present** (35-2 not landed) — catalog marks it forward; cost basis defaults to `platform`. |
| `src/Tamma.Api/Services/PromptStore/TammaMode.cs` | EXISTS | `ITammaModeProvider` (SingleUser/SaaS) — process-stable mode source for the per-mode answers. |
| `docs/guides/BEFORE_YOU_CODE.md` | EXISTS | Mandatory pre-code guide (linked from the story). |

**Key reuse insight:** backfill = a loop over 36-2's `ComputeAsync`; reconciliation = 28-10's
live-aggregation helpers run un-bucketed and compared to the fact-table sum. Neither forks the
counting logic — that is what makes backfill idempotent-by-construction and reconciliation a true
drift detector rather than a second, divergent counter.

---

## Architecture

**Catalog → guard; backfill → replay-the-projection; reconcile → projected-vs-live → WARN event → alert.**

1. **`AnalyticsEventCatalog`** (`Tamma.Activities/Analytics/`) — declares, as `static readonly`
   data, every analytics-relevant DCB event type and its field/tag/column → dimension/measure
   mapping (table in the story AC1). Exposes event-type + field-name constants. The **drift guard**
   test asserts catalog ↔ 36-2-projection field-set equality and catalog ↔ in-solution emit-site
   type-string equality.
2. **`BackfillTenantAnalyticsActivity`** + static `BackfillAsync` — truncate `[from, to)` to
   top-of-hour, optionally rewind the 36-2 checkpoint, loop `ComputeTenantDimensionalRollupActivity.ComputeAsync`
   per hour, report progress, emit `ANALYTICS.BACKFILL.*`. Idempotent via 36-2's whole-bucket upsert.
3. **`AnalyticsBackfillTaskHandler`** (`IPlatformTaskHandler`, `"analytics.backfill"`) + status
   store — runs `BackfillAsync` off-thread on the `PlatformTaskWorker`. `POST /api/admin/analytics/backfill`
   validates + enqueues + returns 202; `GET …/{id}` reports progress.
4. **`ReconcileAnalyticsActivity`** + static `ReconcileAsync` — projected sum (`analytics_usage_hourly`)
   vs live DCB aggregation (reuse 28-10 helpers, un-bucketed) over a window; emit
   `ANALYTICS.RECONCILIATION.MISMATCH` (WARN, per diverging measure, tagged `tenant_id`) past
   tolerance, else `…OK`; report `checkpointLag`.
5. **`FanOutTenantReconcileActivity`** — per-tenant fan-out (28-10 try/catch tolerance) wired as the
   final best-effort step in `HourlyAnalyticsRollupWorkflow`.
6. **`analytics-reconciliation-mismatch`** built-in alert rule — zero-config paging once a channel
   is linked.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Fan-out target (reconcile) | the sole tenant schema | all active tenants (28-10 CP-directory set) |
| Who triggers a backfill | the sole user (owns the instance) | platform operator only — `PlatformOwnerAccess`, never tenant self-service |
| Who owns a mismatch | the sole user — their feed (`tenant_id` = their tenant) | the tenant — `MISMATCH` carries `tenant_id` → tenant alert feed |
| Isolation plane | search-path schema + connection string | physically separate schema per tenant; A unreachable from B |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) | same |

Mode never changes the unit-of-work shape (one tenant schema per backfill/reconcile); only the
fan-out set size and the endpoint's effective audience differ.

---

## Task breakdown

### T1: Analytics event catalog + drift guard (AC: 1, 2)

**Scope:** Declare the contract; enforce it with a test. No backfill/reconcile yet.

**Files:**
- New: `src/Tamma.Activities/Analytics/AnalyticsEventCatalog.cs` — event-type + field-name constants;
  `static readonly` mapping data (event → fields → dimension/measure + source location).
- Test (first): `tests/Tamma.Activities.Tests/Analytics/AnalyticsEventCatalogDriftTests.cs`.

**Tests (first):** catalog field set == the set the 36-2 projection reads (every catalog field
consumed; every consumed field cataloged); catalog event-type strings == in-solution emit-site
constants (`PlatformAnalyticsService.LlmCallSuccess`/`AgentDispatchPrefix`/`AgentDispatchSuccess`/
`AgentDispatchFailed`, the `"completed"`/`"failed"` workflow-status literals); a mutated copy of the
catalog (renamed field / dropped type) fails the equivalence — proving the guard bites.

**Acceptance:**
- [ ] Catalog declares ≥ the AC1 event types + mappings as data, with source-location per field.
- [ ] Drift guard is green against 36-2 as authored; a deliberate rename fails it.
- [ ] Constants are reusable by 36-2 as a single source of truth (equivalence pinned; no 36-2 retrofit required here).

### T2: Backfill activity + idempotency (AC: 3, 4, 6, 10, 12)

**Scope:** `BackfillTenantAnalyticsActivity` + static `BackfillAsync` looping 36-2's `ComputeAsync`
per hour over `[from, to)`; `resetCheckpoint` rewind; per-tenant isolation via `ITenantDbContextFactory`.

**Files:**
- New: `src/Tamma.Activities/Analytics/BackfillTenantAnalyticsActivity.cs`.
- Test (first): `tests/Tamma.Activities.Tests/Analytics/BackfillTenantAnalyticsTests.cs`.

**Tests (first):** backfill a 6-hour range → N rows; re-run → identical row count + summed measures
(idempotent); `resetCheckpoint=true` rewinds the cursor + re-derives with no double-count;
`from >= to` → throws; isolation (a row written to schema A invisible through schema B — InMemory
shape unit + Postgres integration in T6).

**Acceptance:**
- [ ] `BackfillAsync` pure-DI entry point drives `ComputeTenantDimensionalRollupActivity.ComputeAsync` per hour.
- [ ] Re-running a range converges (no duplicates); `resetCheckpoint` rewind is atomic with the first re-projected bucket.
- [ ] Reads/writes only the target tenant's schema.

### T3: Backfill endpoint + platform task (AC: 5, 11, 13)

**Scope:** Off-thread 202 execution + pollable status, `PlatformOwnerAccess`.

**Files:**
- New: `src/Tamma.Api/Services/Analytics/AnalyticsBackfillTaskHandler.cs` (`IPlatformTaskHandler`,
  `"analytics.backfill"`), `IAnalyticsBackfillStatusStore.cs` + `AnalyticsBackfillStatusStore.cs`
  (status pending/running/completed/failed + hoursDone/hoursTotal).
- Modify: `src/Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs` — `Backfill` (POST 202 + `Location`)
  + `GetBackfillStatus` (GET). `src/Tamma.Api/Program.cs` — map both under the `admin` group with
  `RequireAuthorization("PlatformOwnerAccess")` (mirror lines 1346-1351); register the task handler
  via the Story 28-6 `AddPlatformTaskHandler<>` extension.
- Test (first): `tests/Tamma.Api.Tests/Analytics/AnalyticsBackfillEndpointTests.cs`.

**Tests (first):** `POST …/backfill` → 202 + `Location`; the enqueued task drives status
`pending → running → completed`; `GET …/{id}` reflects progress; RBAC (platform owner 200; tenant
member 403; cross-tenant/non-owner 403/404); `from >= to` or over-span → 400; unknown tenant →
`PlatformTaskTerminalException` → dead-letter (not retried).

**Acceptance:**
- [ ] Request returns 202 immediately; the per-hour replay never runs inline.
- [ ] Endpoint gated `PlatformOwnerAccess` (verified by the wiring + RBAC test).
- [ ] Range validation + max-span clamp (default 400 days).

### T4: Reconciliation activity + tolerance (AC: 7, 8, 10, 11)

**Scope:** Projected-vs-live compare per tenant over a window; WARN mismatch + checkpoint-lag.

**Files:**
- New: `src/Tamma.Activities/Analytics/ReconcileAnalyticsActivity.cs` + `ReconcileAsync` static
  entry; `AnalyticsReconciliationOptions` (relative tolerance default 0.5% + absolute floor).
- Modify: `src/Tamma.Activities/Analytics/AnalyticsRollupEvents.cs` — add
  `ReconciliationMismatch`, `ReconciliationOk`, `BackfillStarted/Completed/Failed` constants.
- Test (first): `tests/Tamma.Activities.Tests/Analytics/ReconcileAnalyticsTests.cs`,
  `…/AnalyticsRollupEventsTests.cs` (modify).

**Tests (first):** projected == live within tolerance → no `MISMATCH` (optionally `…OK`); inject a
divergent `analytics_usage_hourly` row → `ANALYTICS.RECONCILIATION.MISMATCH` with correct
`measure`/`projected`/`live`/`deltaPct`/`checkpointLag`; sub-floor divergence on a near-empty tenant
→ no event; tolerance is config-driven; new event constants exist + match convention.

**Acceptance:**
- [ ] `ReconcileAsync` reuses the 28-10 live-aggregation helpers (same counting rule as the projection).
- [ ] Mismatch is WARN, per diverging measure, tagged `tenant_id`; within-tolerance is quiet.
- [ ] `checkpointLag` (max `SequenceNumber` − checkpoint) is reported.

### T5: Fan-out + workflow wiring (AC: 9, 11, 12)

**Scope:** Per-tenant reconcile fan-out as the final best-effort workflow step.

**Files:**
- New: `src/Tamma.Activities/Analytics/FanOutTenantReconcileActivity.cs` (mirror
  `FanOutTenantRollupsActivity` try/catch tolerance + CP-directory target set + `ComputeOneOverride`
  test seam).
- Modify: `src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupWorkflow.cs` — append the reconcile
  fan-out after the 36-2 dimensional fan-out + `EmitHourCompleted` + purges.
- Test (first): `tests/Tamma.Activities.Tests/Analytics/HourlyAnalyticsRollupWorkflowStructureTests.cs`
  (modify).

**Tests (first):** reconcile step present + ordered after the 36-2 dimensional fan-out; a forced
failure on tenant A leaves tenant B's reconcile intact + the fan-out completes; platform +
dimensional steps unchanged.

**Acceptance:**
- [ ] Reconcile shares one schedule / one advisory lock / one target hour with the existing rollup.
- [ ] Per-tenant failure isolation (28-10 AC5); never aborts the workflow; never blocks tenant traffic.

### T6: Built-in alert rule + integration isolation (AC: 11, 14)

**Scope:** Zero-config paging + the Postgres-backed isolation proof.

**Files:**
- Modify: `src/Tamma.Api/Services/Alerts/Rules/BuiltInAlertRules.cs` — `analytics-reconciliation-mismatch`
  (warning, `ANALYTICS.RECONCILIATION.MISMATCH`, `{"op":"always"}`, `ThrottleSeconds` 3600, empty `ChannelIds`).
- Test (first): extend `tests/Tamma.Api.Tests/Alerts/` (seeder + evaluator + throttle); new
  `tests/Tamma.Api.Tests/Analytics/AnalyticsBackfillReconcileIsolationTests.cs` (Postgres 17 Testcontainer).

**Tests (first):** seeder creates the rule; evaluator fires on an appended
`ANALYTICS.RECONCILIATION.MISMATCH`; throttle suppresses a burst. Isolation: backfill into schema A
invisible through B; NULLS-NOT-DISTINCT upsert convergence on re-run; per-tenant failure isolation.

**Acceptance:**
- [ ] A mismatch pages with no manual rule setup (ships with empty `ChannelIds` per convention).
- [ ] Per-tenant isolation + idempotent convergence proven against real Postgres 17 (`sg docker -c "dotnet test …"`).

---

## Task order & dependencies

T1 (catalog/guard) is independent and first — it pins the contract everything else honours.
T2 (backfill activity) needs 36-2's `ComputeAsync`; T3 (endpoint/task) needs T2; T4 (reconcile) is
parallel-safe with T2/T3; T5 (workflow wiring) needs T4; T6 (alert + integration) needs T4 (event)
and T2 (backfill) for the isolation suite. Critical path: **T1 → T2 → T3** and **T1 → T4 → T5 → T6**.

> **Hard prerequisite:** Story 36-2 must be authored/implemented far enough that
> `ComputeTenantDimensionalRollupActivity.ComputeAsync`, `AnalyticsProjectionCheckpoint`,
> `AnalyticsUsageHourly`, and the extended `AnalyticsRollupEvents` exist — T2/T4 call into them
> directly. If implementing 36-11 before 36-2 lands, stub the 36-2 seam behind an interface and
> mark it forward, or sequence 36-2 first.

## Risks

- **36-2 not yet landed.** T2/T4 depend on 36-2's `ComputeAsync` + checkpoint + fact tables (drafted,
  not implemented). Mitigation: sequence 36-2 first, or gate T2/T4 behind a thin seam. The catalog
  (T1) and the drift guard can be authored against the 36-2 *story* contract before 36-2 code lands.
- **Backfill cost on busy tenants.** A multi-month replay reads a large event window per hour.
  Mitigation: off-thread 202 (T3) + honour cancellation + the max-span clamp; the 36-2 per-bucket
  read is already hour-bounded.
- **Reconciliation false positives.** Same-source-different-window or async-event-arrival could
  trigger a spurious mismatch. Mitigation: reconcile a *completed* window (last full UTC day), reuse
  the *exact* projection helpers (so the counting rule can't differ), and apply relative tolerance +
  absolute floor. If flapping persists, widen the window or add a settle delay (cheap follow-up).
- **Alert noise.** A persistently drifted tenant could page every hour. Mitigation: rule
  `ThrottleSeconds` 3600 + per-measure dedup; the registry of mismatch events stays in the DCB store
  for the runbook regardless of paging.
- **`BillingMode` (35-2) absent.** Cost basis defaults to `platform` on both projected and live
  sides, so the catalog marks it forward and reconciliation compares like-for-like — no false
  mismatch from a missing 35-2. Pin this in the T4 tolerance test.
- **Event-store topology shift (Story 28-1 / Epic 30).** Per-tenant events move under per-tenant
  fan-out later; the reconcile already reads each tenant's `TenantDbContext`, and the mismatch
  events carry `tenant_id`, so the migration only touches the CP-directory target-set read — keep
  the directory read isolated (mirror `FanOutTenantRollupsActivity`).
- **Catalog drift-guard brittleness.** Over-strict reflection could break on benign refactors.
  Mitigation: assert on declared field-name/event-type *strings* (the contract), not on private
  member shapes; the guard's job is to catch a rename, not to freeze the implementation.
