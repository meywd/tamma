# Story 36-11: Analytics Event Catalog, Backfill & Reconciliation

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase workflow (Read → Research → Break Down → TDD →
Quality Gates → Failure Handling), the `.dev/` knowledge base, TRACE/DEBUG logging
requirements, test-first development, and the build-success / coverage gates.

## User Story

As a **platform operator** (SaaS) **/ self-hosted owner** (single-user),
I want a typed, drift-guarded catalog of the DCB event fields the analytics projection depends on,
an admin-only backfill that rebuilds the dimensional store from historical `domain_events` for a
tenant + time-range, and an hourly reconciliation that compares the projected analytics totals
against a live DCB aggregation and alerts on drift,
So that upstream event-shape changes are caught at build time, existing tenants can be onboarded
into analytics (and rollup outages recovered) without double-counting, and a silently divergent
projection can never quietly mislead a dashboard, export, or scheduled report.

## Priority

P2 — The projection (Story 36-2) makes analytics *work*; this story makes it *trustworthy in
production*. A drifted event shape, an un-backfilled tenant, or a silently lossy projection all
produce confidently-wrong numbers — the failure mode this story exists to prevent. It hardens the
substrate every downstream Epic 36 surface reads.

## Scope

Production-hardening of the Story 36-2 projection — **three** additions, no schema change to the
Story 36-1 fact tables and **no** change to the projection's compute semantics:

1. **A typed analytics event-field catalog** (`AnalyticsEventCatalog`) — the single declared
   contract of which DCB event types feed the projection and which fields/tags each one
   contributes to which dimension or measure (`LLM.CALL.SUCCESS → {costUsd, inputTokens,
   outputTokens, provider, model}`, `AGENT.DISPATCH.* → {agent_id, status}`, `WORKFLOW.* → status`).
   A **build-time/test-time drift guard** asserts the projection's expected field set matches the
   catalog (and, where statically checkable, the emitting sites) so an upstream rename of `costUsd`
   or a dropped `agent_id` tag fails a test instead of silently zeroing a column.

2. **An admin backfill** (`BackfillTenantAnalyticsActivity` + `POST /api/admin/analytics/backfill`)
   — rebuilds `analytics_usage_hourly`/`_daily` for a tenant + `[from, to)` UTC range from
   historical `domain_events`, driving the **same** Story 36-2 `ComputeTenantDimensionalRollupActivity.ComputeAsync`
   idempotent code path hour-by-hour, with an optional `resetCheckpoint`. Used for onboarding
   tenants that pre-date analytics and for recovering from rollup outages. Replay-safe: re-running
   a range converges (never duplicates) because the underlying upsert recomputes each bucket whole.

3. **An hourly reconciliation** (`ReconcileAnalyticsActivity`) — per active tenant, compares the
   **projected** per-tenant totals (sum of `analytics_usage_hourly` over a window) against a
   **live DCB aggregation** of the same window's `domain_events` + `ProviderDiagnostic` rows, and
   emits `ANALYTICS.RECONCILIATION.MISMATCH` (WARN) tagged `tenant_id` when they diverge beyond a
   configured tolerance. This mirrors the Story 28-10 / Story 20-3 reconciliation pattern (the
   `PlatformAnalyticsService` fact-table-vs-live fallback, and the 20-3 hourly local-vs-Stripe
   compare). It wires into the existing `HourlyAnalyticsRollupWorkflow` as a final, best-effort
   step alongside the 36-2 fan-out — one schedule, one advisory lock, one target hour.

The Story 36-2 `ComputeTenantDimensionalRollupActivity`, `CompactDailyAnalyticsActivity`,
`PurgeStaleUsageAnalyticsActivity`, the platform `platform_analytics_hourly` rollup (Story 28-10),
and the Story 36-1 fact-table schema are all left **entirely intact** — this story reads them,
reuses their compute path, and observes their output; it does not alter them.

## Acceptance Criteria

1. A new `AnalyticsEventCatalog`
   (`apps/tamma-elsa/src/Tamma.Activities/Analytics/AnalyticsEventCatalog.cs`) declares, as data
   (not branches), the **analytics-relevant DCB event types** and each one's dimensional/measure
   mapping. At minimum it covers: `LLM.CALL.SUCCESS` → measures `costUsd`, `inputTokens`,
   `outputTokens` + dimensions `provider`, `model`; `AGENT.DISPATCH.SUCCESS`/`AGENT.DISPATCH.FAILED`
   (matched by the `AGENT.DISPATCH.%` prefix) → dimension `agent_id` + `status` (success/failed
   measure); `WORKFLOW.*` → workflow `status` (started/completed/failed counts) + dimension
   `workflowDefinitionId`; and the shared dimension tags `repoId`/`ProjectId`, `tenantId`, and
   `billing_mode` (35-2, cost basis). Each catalog entry records the source location (event `Data`
   JSON field vs `Tags` key vs `ProviderDiagnostic` column) so the mapping is unambiguous.

2. A **drift guard** test asserts that the field set the Story 36-2 projection actually reads
   (`ComputeTenantDimensionalRollupActivity` / its `AggregateLlmUsage` + cost-basis + dimension
   extraction) matches the catalog exactly — every catalog field is consumed and every consumed
   field is cataloged. Where the emitting site is statically inspectable in the same solution
   (e.g. the `AGENT.DISPATCH.*`/`LLM.CALL.SUCCESS`/`WORKFLOW.*` constants in
   `PlatformAnalyticsService` and `AlertEventEmitter`), the guard also asserts the catalog's event
   **type strings** match those constants, so a rename of `LLM.CALL.SUCCESS` or
   `AGENT.DISPATCH.SUCCESS` breaks the build/test rather than silently dropping a measure. Catalog
   field-name and event-type strings are exposed as `const`/`static readonly` so 36-2 can consume
   them as a single source of truth (a follow-up refactor; not required to retro-fit 36-2 in this
   story, but the guard pins the equivalence).

3. A new `BackfillTenantAnalyticsActivity`
   (`apps/tamma-elsa/src/Tamma.Activities/Analytics/BackfillTenantAnalyticsActivity.cs`, extending
   `Tamma.Activities.Core.TammaAsyncActivity`) rebuilds a single tenant's dimensional store for a
   `[from, to)` UTC half-open range by truncating to top-of-hour and invoking
   `ComputeTenantDimensionalRollupActivity.ComputeAsync` once per hour bucket in the range, in
   ascending order. It exposes a static pure-DI `BackfillAsync(ITenantDbContextFactory, …,
   IPlatformEventPublisher, tenantId, from, to, resetCheckpoint, logger, ct)` entry point so the
   endpoint, the platform task worker, and unit tests drive the identical code path without an
   `ActivityExecutionContext` — mirroring `ComputeTenantRollupActivity.ComputeAsync`.

4. The backfill is **idempotent / replay-safe**: re-running the same `(tenantId, from, to)` range
   converges to identical rows and measures (proven by a test running the range twice and asserting
   row counts + summed measures unchanged), because each hour bucket recomputes whole via the
   Story 36-2 read-then-upsert on the NULLS-NOT-DISTINCT dimension business key. `resetCheckpoint`
   rewinds the per-tenant `analytics_projection_checkpoint` so a corrupt/partial prior run can be
   re-derived; omitting it backfills only buckets whose events post-date the checkpoint (efficient
   gap-fill). Either way the upsert guarantees no double-count.

5. An **admin backfill endpoint** `POST /api/admin/analytics/backfill`
   (`apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs`, gated `PlatformOwnerAccess`
   at the wiring site, mirroring the existing 28-10 admin analytics endpoints) accepts
   `{ tenantId, from, to, resetCheckpoint? }`, validates the range (`from < to`, range not
   absurdly large — clamp to a configurable max span, default 400 days), enqueues a
   `analytics.backfill` platform task (`IPlatformTaskHandler` pattern, Story 28-6) and returns
   **`202 Accepted`** with a status URI — the long-running per-hour replay runs on the existing
   `PlatformTaskWorker` thread, never inline on the request (mirrors the Cranl provision 202 +
   `TaskQueueProcessor` shape). A `GET /api/admin/analytics/backfill/{id}` reports progress
   (`pending → running → completed | failed`, with `hoursDone/hoursTotal`).

6. Backfill **fans out per tenant via `ITenantDbContextFactory`** and reads **only** the target
   tenant's schema: events come from that tenant's `TenantDbContext` (search-path schema) and rows
   are written to that tenant's own `analytics_usage_*` tables. A backfill for tenant A never reads
   or writes tenant B's schema (proven by an isolation test). Backfilling one tenant does not block
   or affect any other tenant's live traffic or scheduled rollup.

7. A new `ReconcileAnalyticsActivity`
   (`apps/tamma-elsa/src/Tamma.Activities/Analytics/ReconcileAnalyticsActivity.cs`, extending
   `TammaAsyncActivity`) computes, for one tenant over a reconciliation window (default the last
   completed UTC day), the **projected totals** (sum of `analytics_usage_hourly` measures —
   `TokensIn`, `TokensOut`, `CostUsd`, `WorkflowsStarted/Completed/Failed`, `AgentDispatches`) and a
   **live DCB aggregation** of the same window straight from `domain_events` + `ProviderDiagnostic`
   (reusing the Story 36-2 `AggregateLlmUsage`/dispatch-count/workflow-count helpers — the *same*
   extraction the projection uses, but un-bucketed grand totals). It exposes a static pure-DI
   `ReconcileAsync(...)` entry point.

8. When the projected and live totals diverge **beyond a configured tolerance** (relative
   tolerance `MissingConfig`-style config key `Analytics:Reconciliation:Tolerance`, default 0.5%,
   plus an absolute floor so a 1-vs-2 token blip on a near-empty tenant does not page), the activity
   emits `ANALYTICS.RECONCILIATION.MISMATCH` (severity **WARN**) tagged `tenant_id`, carrying
   `{ window, measure, projected, live, deltaPct }` for each diverging measure; when totals match
   within tolerance it emits `ANALYTICS.RECONCILIATION.OK` (or simply logs — no per-measure event
   spam) so a clean reconcile is observable. New event constants land on `AnalyticsRollupEvents`.

9. Reconciliation is wired into `HourlyAnalyticsRollupWorkflow` as a **best-effort final step**,
   after the Story 36-2 dimensional fan-out + compaction and after `EmitHourCompleted`, sharing the
   one schedule / one advisory lock / one target hour. It fans out per tenant with **per-tenant
   failure isolation** (Story 28-10 AC5 shape): a reconcile failure or mismatch for tenant A emits
   the tenant-scoped event and the fan-out continues to tenant B; it never aborts the workflow and
   never blocks tenant traffic. A structure test asserts the new step is present and ordered after
   the dimensional fan-out.

10. **Checkpoint management** is explicit and shared: backfill and the live reconciliation read the
    same per-tenant `analytics_projection_checkpoint` (Story 36-2) cursor by `SequenceNumber` (never
    `Id`/`CreatedAt`). Reconciliation reports the checkpoint lag (highest `domain_events.SequenceNumber`
    vs the checkpoint) in its event so an un-advancing projection is visible. `resetCheckpoint` on
    backfill rewinds the cursor atomically with the first re-projected bucket; a backfill that
    completes a contiguous range never leaves the checkpoint ahead of un-projected events.

11. Reconciliation + backfill **never block tenant traffic and isolate per-tenant failures**;
    every outcome (mismatch, match, backfill progress, per-tenant failure) is observable via
    emitted DCB/platform events and structured logs. A `MISMATCH` is a WARN-level signal, not a
    failure — it does not fail the rollup; the runbook/alert pipeline consumes it (a built-in alert
    rule on `ANALYTICS.RECONCILIATION.MISMATCH` is in scope as a one-line `BuiltInAlertRules` spec
    so a divergence pages without manual setup).

12. **Tenant isolation preserved throughout**: backfill and reconciliation read and write **only**
    the target tenant's schema via `ITenantDbContextFactory`; no cross-tenant query, no
    `IgnoreQueryFilters()` over tenant data, no read of another tenant's `analytics_usage_*`. The
    active-tenant *directory* read (to know which tenants to reconcile) is the only CP-context touch
    and mirrors the existing `FanOutTenantRollupsActivity` target-set query.

13. **Per-mode ownership is answered for both scoping models** (CLAUDE.md universal rule): in
    single-user mode the backfill/reconciliation run over the sole tenant schema and the sole user
    owns the result; in SaaS mode they fan out across all active tenants, the backfill endpoint is
    `PlatformOwnerAccess` (a platform-operator recovery tool, never tenant-self-service), and
    mismatch events carry `tenant_id` so they surface on the owning tenant's alert feed while the
    backfill stays an operator concern. (Detailed table in Technical Design.)

14. Unit + integration tests cover: the **drift guard** (catalog ↔ projection field equivalence;
    a deliberately-renamed field fails the guard); **idempotent backfill** (re-run a range → identical
    rows/measures; `resetCheckpoint` rewind + re-derive with no double-count); **reconciliation
    match** (projected == live within tolerance → no MISMATCH) and **mismatch** (inject a divergent
    projected row → `ANALYTICS.RECONCILIATION.MISMATCH` with correct `deltaPct`); **per-tenant
    failure isolation** (a forced failure on tenant A leaves tenant B's backfill/reconcile intact
    and the fan-out completes); **tenant isolation** (a row in schema A is invisible through schema
    B); the **202 + status** endpoint flow and `PlatformOwnerAccess` RBAC (member/tenant → 403/404);
    and the workflow structure (reconcile step present + ordered last).

## Tasks / Subtasks

- [ ] Task 1: Analytics event catalog + drift guard (AC: 1, 2)
  - [ ] Add `AnalyticsEventCatalog` declaring event types + per-event field/tag/column → dimension/
        measure mapping as data; expose event-type and field-name constants.
  - [ ] Drift-guard test asserting catalog ↔ 36-2 projection field equivalence and catalog
        event-type strings == in-solution emit-site constants; a renamed field/type fails red.

- [ ] Task 2: Backfill activity + idempotency (AC: 3, 4, 6, 10, 12)
  - [ ] `BackfillTenantAnalyticsActivity` with static `BackfillAsync` driving
        `ComputeTenantDimensionalRollupActivity.ComputeAsync` per hour bucket over `[from, to)`.
  - [ ] `resetCheckpoint` rewind atomic with the first re-projected bucket; range truncation/validation.
  - [ ] Unit test: re-run range → identical rows/measures; reset + re-derive no double-count;
        per-tenant isolation (schema A vs B).

- [ ] Task 3: Backfill endpoint + platform task (AC: 5, 13)
  - [ ] `analytics.backfill` `IPlatformTaskHandler` (Story 28-6) running `BackfillAsync` off-thread.
  - [ ] `POST /api/admin/analytics/backfill` (202 + status URI) + `GET …/backfill/{id}` progress;
        wire under `PlatformOwnerAccess` in `Program.cs`; range validation + max-span clamp.
  - [ ] Endpoint tests: 202 + status transitions; RBAC (owner ok, member/tenant 403/404); bad range 400.

- [ ] Task 4: Reconciliation activity + tolerance (AC: 7, 8, 10, 11)
  - [ ] `ReconcileAnalyticsActivity` + static `ReconcileAsync` — projected sum vs live DCB
        aggregation (reuse 36-2 helpers); relative tolerance + absolute floor from config.
  - [ ] Emit `ANALYTICS.RECONCILIATION.MISMATCH` (WARN, per diverging measure) / `…OK`; checkpoint-lag
        reporting; new `AnalyticsRollupEvents` constants.
  - [ ] Unit test: within-tolerance → no mismatch; injected divergence → mismatch with correct deltaPct.

- [ ] Task 5: Fan-out + workflow wiring (AC: 9, 11, 12)
  - [ ] Per-tenant reconcile fan-out (mirror `FanOutTenantRollupsActivity` try/catch tolerance).
  - [ ] Wire the reconcile step into `HourlyAnalyticsRollupWorkflow` after the 36-2 fan-out +
        `EmitHourCompleted`, best-effort; structure test for presence + ordering.

- [ ] Task 6: Built-in alert rule + tests (AC: 11, 14)
  - [ ] `analytics-reconciliation-mismatch` spec in `BuiltInAlertRules.All` (warning,
        `ANALYTICS.RECONCILIATION.MISMATCH`, throttled); seeder picks it up idempotently.
  - [ ] Integration: per-tenant isolation (Postgres 17 Testcontainer) for backfill + reconcile.

## Technical Design

### C# namespace / file structure

```
apps/tamma-elsa/src/
  Tamma.Activities/Analytics/
    AnalyticsEventCatalog.cs                 # NEW — typed event-type → dimension/measure contract
    BackfillTenantAnalyticsActivity.cs       # NEW — per-tenant per-hour backfill driver
    ReconcileAnalyticsActivity.cs            # NEW — projected-vs-live compare + mismatch emit
    FanOutTenantReconcileActivity.cs         # NEW — per-tenant reconcile fan-out (28-10 shape)
    AnalyticsRollupEvents.cs                 # MODIFY — + RECONCILIATION.MISMATCH / .OK / BACKFILL.* constants
    ComputeTenantDimensionalRollupActivity.cs # READ — ComputeAsync (36-2) reused per backfill hour
  Tamma.Api/Endpoints/
    AdminAnalyticsEndpoints.cs               # MODIFY — + Backfill (POST 202) + GetBackfillStatus (GET)
  Tamma.Api/Services/Analytics/
    AnalyticsBackfillTaskHandler.cs          # NEW — IPlatformTaskHandler ("analytics.backfill")
    IAnalyticsBackfillStatusStore.cs / *.cs  # NEW — backfill job status (pending/running/completed/failed + progress)
  Tamma.Api/Services/Alerts/Rules/
    BuiltInAlertRules.cs                     # MODIFY — + analytics-reconciliation-mismatch spec
  Tamma.Api/Program.cs                        # MODIFY — map backfill endpoints (PlatformOwnerAccess); register task handler
  Tamma.Data/Entities/
    DomainEvent.cs                           # READ — Type, Tags, Data, SequenceNumber
    ProviderDiagnostic.cs                    # READ — ProviderKey, Input/OutputTokens, Cost, AgentType, ProjectId, (BillingMode 35-2)
    AnalyticsUsageHourly.cs                  # READ — 36-1 fact table (projected totals source)
    AnalyticsProjectionCheckpoint.cs         # READ — 36-2 per-tenant SequenceNumber cursor
  Tamma.ElsaServer/Workflows/
    HourlyAnalyticsRollupWorkflow.cs         # MODIFY — + reconcile fan-out step (final, best-effort)

apps/tamma-elsa/tests/
  Tamma.Activities.Tests/Analytics/
    AnalyticsEventCatalogDriftTests.cs       # NEW — catalog ↔ projection ↔ emit-site equivalence
    BackfillTenantAnalyticsTests.cs          # NEW — idempotent re-run / resetCheckpoint / no double-count
    ReconcileAnalyticsTests.cs               # NEW — match (no mismatch) / divergence (deltaPct) / tolerance
    HourlyAnalyticsRollupWorkflowStructureTests.cs # MODIFY — assert reconcile step present + ordered last
    AnalyticsRollupEventsTests.cs            # MODIFY — new RECONCILIATION/BACKFILL constants
  Tamma.Api.Tests/Analytics/
    AnalyticsBackfillEndpointTests.cs        # NEW — 202 + status flow + PlatformOwnerAccess RBAC + bad-range 400
    AnalyticsBackfillReconcileIsolationTests.cs # NEW — Postgres 17 Testcontainer: per-tenant isolation
```

### Event-field catalog (the contract being locked down)

Verified against the live emit sites and the Story 36-2 projection:

| Event type (string) | Source field | Maps to | Source location |
|---|---|---|---|
| `LLM.CALL.SUCCESS` | `costUsd`, `inputTokens`, `outputTokens` | measures `CostUsd`, `TokensIn`, `TokensOut` | event `Data` JSON (Story 9-2) |
| `LLM.CALL.SUCCESS` | `provider`, `model` | dimensions `Provider`, (model attr) | event `Tags` / `Data` |
| `AGENT.DISPATCH.SUCCESS` / `AGENT.DISPATCH.FAILED` (prefix `AGENT.DISPATCH.%`) | `agent_id`, status | dimension `AgentId`, `AgentDispatches` measure | event `Tags` (Epic 32) + `Type` |
| `WORKFLOW.*` (via `workflow_instances.Status`) | `started`/`completed`/`failed` | `WorkflowsStarted/Completed/Failed` | `WorkflowInstance.Status` |
| (shared dimension) | `repoId` / `ProjectId` | dimension `RepoId` | event `Tags` / `ProviderDiagnostic.ProjectId` |
| (shared dimension) | `billing_mode` | `CostBasis` (byok/platform) | event `Tags` (35-2) / `ProviderDiagnostic.BillingMode` |
| (provider diagnostics) | `ProviderKey`, `InputTokens`, `OutputTokens`, `Cost`, `AgentType` | measures + dims | `ProviderDiagnostic` columns |

The catalog encodes this table as `static readonly` data. The drift guard test reflects over what
`ComputeTenantDimensionalRollupActivity` reads and asserts set-equality with the catalog, and
asserts the catalog's event-type strings equal the in-solution constants
(`PlatformAnalyticsService.LlmCallSuccess` / `AgentDispatchPrefix` / `AgentDispatchSuccess` /
`AgentDispatchFailed`, `ComputeTenantRollupActivity`'s `"completed"`/`"failed"` status literals).
A rename anywhere flips the guard red.

### Backfill (per-hour replay over the 36-2 code path)

```csharp
// Drive the SAME idempotent compute the hourly rollup uses, one bucket at a time.
public static async Task BackfillAsync(
    IDbContextFactory<ControlPlaneDbContext> cpFactory,
    ITenantDbContextFactory tenantFactory,
    IPlatformEventPublisher publisher,
    IAnalyticsPricingConfig pricing,                // 36-2 seam
    Guid tenantId, DateTime from, DateTime to, bool resetCheckpoint,
    IProgress<(int done, int total)>? progress,
    ILogger? logger, CancellationToken ct)
{
    from = AnalyticsRollupEvents.TruncateToHour(from);
    to   = AnalyticsRollupEvents.TruncateToHour(to);          // half-open [from, to)
    if (from >= to) throw new ArgumentException("from must precede to");

    if (resetCheckpoint)
        await RewindCheckpointAsync(tenantFactory, tenantId, from, ct).ConfigureAwait(false);

    var total = (int)(to - from).TotalHours;
    var done = 0;
    for (var hour = from; hour < to; hour = hour.AddHours(1))
    {
        ct.ThrowIfCancellationRequested();
        await ComputeTenantDimensionalRollupActivity.ComputeAsync(   // 36-2 — idempotent upsert
            cpFactory, tenantFactory, publisher, pricing, tenantId, hour, logger, ct)
            .ConfigureAwait(false);
        progress?.Report((++done, total));
    }
    await publisher.AppendAndPublishAsync(
        AnalyticsRollupEvents.BuildEvent(AnalyticsRollupEvents.BackfillCompleted, to, tenantId,
            data: new() { ["from"] = from.ToString("O"), ["to"] = to.ToString("O"),
                          ["hours"] = total, ["resetCheckpoint"] = resetCheckpoint }),
        ct).ConfigureAwait(false);
}
```

Idempotency comes for free from 36-2's whole-bucket read-then-upsert on the NULLS-NOT-DISTINCT
business key — backfilling an already-projected bucket overwrites it with the same values.
`resetCheckpoint` only matters for the *efficiency* cursor; the upsert is the correctness mechanism
(same design rationale as 36-2 Dev Notes).

### Backfill control surface (202 + off-thread)

`POST /api/admin/analytics/backfill` validates `{ tenantId, from, to, resetCheckpoint? }`, clamps
the span (default max 400 days), persists a backfill-status row (`pending`), enqueues an
`analytics.backfill` platform task, and returns `202 Accepted` with `Location:
/api/admin/analytics/backfill/{id}`. The `AnalyticsBackfillTaskHandler` (`IPlatformTaskHandler`,
Story 28-6) deserializes the payload, flips status `running`, calls `BackfillAsync` reporting
`(done,total)` into the status store, and flips `completed`/`failed`. This is the exact 202 +
`PlatformTaskWorker`/`TaskQueueProcessor` shape the Cranl provision endpoints use — the request
never blocks on the per-hour replay.

### Reconciliation (projected vs live — the 28-10 / 20-3 pattern)

```csharp
public static async Task ReconcileAsync(
    IDbContextFactory<ControlPlaneDbContext> cpFactory,
    ITenantDbContextFactory tenantFactory,
    IPlatformEventPublisher publisher,
    AnalyticsReconciliationOptions opts,            // tolerance + absolute floor
    Guid tenantId, DateTime windowStart, DateTime windowEnd,
    ILogger? logger, CancellationToken ct)
{
    await using var db = await tenantFactory.CreateAsync(tenantId, ct).ConfigureAwait(false);

    // PROJECTED — sum the dimensional fact table over the window.
    var projected = await db.AnalyticsUsageHourly.AsNoTracking()
        .Where(r => r.Hour >= windowStart && r.Hour < windowEnd)
        .GroupBy(_ => 1)
        .Select(g => new Totals {
            CostUsd = g.Sum(r => r.CostUsd), TokensIn = g.Sum(r => r.TokensIn), /* … */ })
        .FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? Totals.Zero;

    // LIVE — recompute grand totals straight from domain_events + diagnostics,
    // reusing the EXACT 36-2 extraction helpers (un-bucketed).
    var live = await ComputeLiveTotalsAsync(db, windowStart, windowEnd, ct).ConfigureAwait(false);

    foreach (var m in Totals.Measures)             // costUsd, tokensIn/out, workflows*, dispatches
    {
        var (p, l) = (projected[m], live[m]);
        if (Diverges(p, l, opts.RelativeTolerance, opts.AbsoluteFloor))
            await publisher.AppendAndPublishAsync(
                AnalyticsRollupEvents.BuildEvent(AnalyticsRollupEvents.ReconciliationMismatch,
                    windowStart, tenantId,
                    data: new() { ["measure"] = m, ["projected"] = p, ["live"] = l,
                                  ["deltaPct"] = DeltaPct(p, l), ["window"] = windowStart.ToString("O"),
                                  ["checkpointLag"] = await CheckpointLagAsync(db, ct) }),
                ct).ConfigureAwait(false);
    }
}
```

`ComputeLiveTotalsAsync` reuses `ComputeTenantRollupActivity.AggregateLlmUsage`, the
`AGENT.DISPATCH.%` `EF.Functions.Like` count, and the `WorkflowInstance.Status` counts — the same
reads the projection performs, so a mismatch means the *projection* drifted (stale rows, a missed
checkpoint advance, a dropped fan-out), not a different counting rule. This is the in-process twin
of `PlatformAnalyticsService`'s fact-table-vs-live fallback (Story 28-10) and Story 20-3's hourly
local-vs-Stripe compare.

### Workflow wiring

```
HourlyAnalyticsRollupWorkflow.Build (MODIFY) — Sequence:
  initBucket
  platformRollup        (ComputePlatformRollupActivity)            — unchanged (28-10)
  fanOut                (FanOutTenantRollupsActivity)               — unchanged (28-10)
  fanOutDimensional     (FanOutTenantDimensionalRollupsActivity)    — 36-2
  compactDaily          (CompactDailyAnalyticsActivity)            — 36-2
  emitCompleted         (EmitHourCompletedActivity)                 — unchanged
  purgeStale            (PurgeStaleAnalyticsActivity)              — unchanged (28-10)
  purgeUsageStale       (PurgeStaleUsageAnalyticsActivity)         — 36-2
  fanOutReconcile       (FanOutTenantReconcileActivity)            — NEW (this story, final, best-effort)
```

Reconcile runs **last** so it observes the freshly-written buckets, and is best-effort: a reconcile
failure (or a flood of mismatches) never fails a rollup that already wrote useful rows. One
schedule, one advisory lock, one target hour — reconciliation is one more consumer of the same
fan-out, exactly like the 36-2 dimensional rollup.

### DCB events (new — owned by this story)

| Event | When | Key data |
|---|---|---|
| `ANALYTICS.RECONCILIATION.MISMATCH` | per tenant×measure when divergence > tolerance (WARN) | measure, projected, live, deltaPct, window, checkpointLag |
| `ANALYTICS.RECONCILIATION.OK` | per tenant×window when all measures within tolerance | window, checkpointLag |
| `ANALYTICS.BACKFILL.STARTED` / `…COMPLETED` / `…FAILED` | per backfill job | tenantId, from, to, hours, resetCheckpoint, (errorType/message) |

Per-tenant events carry `tenantId` (Story 28-6 step-dedup index applies) and append via
`IPlatformEventPublisher.AppendAndPublishAsync` (best-effort, same as the 28-10/36-2 path).

### Built-in alert rule

One additive spec in `BuiltInAlertRules.All`: `analytics-reconciliation-mismatch` (severity
warning, `EventType = ANALYTICS.RECONCILIATION.MISMATCH`, predicate `{"op":"always"}`,
`ThrottleSeconds` 3600 so a persistent drift pages once an hour, not once per tenant×measure).
The idempotent `BuiltInAlertRuleSeeder` picks it up by `built_in_key`; ships with empty `ChannelIds`
(no auto-spam) per existing convention.

### Per-mode + per-tenant handling

| Question | single-user mode | SaaS mode |
|---|---|---|
| What does the fan-out reconcile? | The sole tenant (one schema). | All active tenants from the CP directory (28-10 target set). |
| Who triggers a backfill? | The sole user (owner of their instance). | Platform operator only — `POST /api/admin/analytics/backfill` is `PlatformOwnerAccess`; never tenant self-service (recovery/onboarding is an operator concern). |
| Who owns a mismatch? | The sole user — it's their instance/feed (`tenant_id` = their tenant). | The tenant — `ANALYTICS.RECONCILIATION.MISMATCH` carries `tenant_id` → tenant alert feed; the alert rule routes per `AlertPayload.TenantId`. |
| Isolation plane | Search-path schema + connection string. | Same — physically separate schema per tenant; backfill/reconcile of A is unreachable from B. |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable. | same |

Mode does not change the backfill/reconcile shape — both resolve to exactly one tenant schema per
unit of work; only the fan-out target-set size and the endpoint's effective audience differ.

### API shape

```
POST /api/admin/analytics/backfill          (PlatformOwnerAccess; body { tenantId, from, to, resetCheckpoint? }) → 202 + Location
GET  /api/admin/analytics/backfill/{id}     (PlatformOwnerAccess) → { id, tenantId, from, to, status, hoursDone, hoursTotal }
```

Reconciliation has **no** HTTP endpoint — it is a scheduled best-effort workflow step; its output is
DCB events + the built-in alert rule + structured logs. (Operators force a reconcile by running the
hourly rollup with an explicit `hour`, the existing 28-10 control surface.)

## Dependencies

**Prerequisite (internal):**
- **Story 36-2** — `ComputeTenantDimensionalRollupActivity.ComputeAsync` (the idempotent per-hour
  compute backfill replays), `AnalyticsProjectionCheckpoint` (the cursor checkpoint management
  reads/rewinds), `AnalyticsRollupEvents` (extended here), `IAnalyticsPricingConfig`, and the
  `HourlyAnalyticsRollupWorkflow` fan-out this story appends to. (Drafted.)
- **Story 36-1** — `AnalyticsUsageHourly`/`AnalyticsUsageDaily` fact tables + `CostBasis` enum +
  `UX_*_dims` NULLS-NOT-DISTINCT business-key indexes (the projected-totals source + the backfill
  upsert backstop). (Drafted.)
- **Story 28-10** — `FanOutTenantRollupsActivity` (the per-tenant fan-out + 5%-tolerance shape
  mirrored here), `ComputeTenantRollupActivity.AggregateLlmUsage`/dispatch/workflow helpers (reused
  for the live-totals path), `PlatformAnalyticsService` fact-table-vs-live fallback (the
  reconciliation pattern reference), `AnalyticsRollupEvents`. (Merged.)
- **Story 20-3** — the hourly local-vs-source reconciliation + `RECONCILIATION_MISMATCH` (WARN)
  pattern this story mirrors for the analytics substrate. (Reference.)
- **Story 28-6** — `IPlatformTaskHandler` / `PlatformTaskWorker` / `PlatformQueuedTask` (the
  off-thread 202 backfill execution + step-dedup index). (Merged.)
- **Epic 4 (DCB `DomainEvent`)** — the per-tenant event stream (`LLM.CALL.SUCCESS`,
  `AGENT.DISPATCH.*`, `WORKFLOW.*`) + the `SequenceNumber` total-order cursor the catalog
  documents, the backfill replays, and the reconciliation aggregates live. (In place.)
- **Epic 28** — per-tenant schema + `ITenantDbContextFactory` (the isolation plane). (Merged.)

**Soft / forward (degrade gracefully if absent):**
- **Story 35-2 (Epic 35)** — `billing_mode` tag / `ProviderDiagnostic.BillingMode` (cost basis). If
  absent, the catalog documents the `platform` default and reconciliation compares like-for-like
  (projection and live both default to `platform`), so a missing 35-2 never produces a false
  mismatch. (`BillingMode` is **not** yet on `ProviderDiagnostic` in this codebase — catalog marks
  it forward.)
- **Story 5.6 alert pipeline** — the built-in `analytics-reconciliation-mismatch` rule + delivery
  channels. If unwired, mismatch events still land in the DCB store + logs; the rule just adds
  zero-config paging once a channel is linked. (Merged for write side.)

**Blocks (internal):**
- Production rollout of Epic 36 dashboards/exports/reports for **existing** tenants (which need a
  backfill before their charts have history) and the trust guarantee that those surfaces aren't
  silently wrong (reconciliation).

**External:**
- PostgreSQL 17 (NULLS NOT DISTINCT upsert backstop via 36-2; per-tenant schema).
- EF Core 9 / Npgsql.
- Testcontainers + Docker for the isolation integration suite (run via `sg docker -c "dotnet test …"`).

## Testing Strategy

1. **Unit — drift guard (`AnalyticsEventCatalogDriftTests`):** assert the catalog field set equals
   the set the 36-2 projection reads (every catalog field consumed; every consumed field cataloged);
   assert catalog event-type strings equal the in-solution emit-site constants; a test that mutates
   a copy of the catalog (renamed field / dropped type) fails the equivalence — proving the guard bites.

2. **Unit — idempotent backfill (`BackfillTenantAnalyticsTests`, EF InMemory for the loop shape):**
   backfill a 6-hour range → N rows; re-run the same range → identical row count + summed measures;
   `resetCheckpoint=true` rewinds the cursor and re-derives without double-counting; `from >= to` → throws.

3. **Unit — reconciliation match/mismatch (`ReconcileAnalyticsTests`):** projected == live within
   tolerance → no `MISMATCH` event (optionally `…OK`); inject a divergent `analytics_usage_hourly`
   row → `ANALYTICS.RECONCILIATION.MISMATCH` with correct `measure`/`projected`/`live`/`deltaPct`;
   sub-floor divergence on a near-empty tenant → no event (absolute floor); tolerance is config-driven.

4. **Unit — checkpoint management:** reconciliation reports `checkpointLag` = max `SequenceNumber` −
   checkpoint; backfill `resetCheckpoint` rewind is atomic with the first re-projected bucket.

5. **Integration — per-tenant isolation (`AnalyticsBackfillReconcileIsolationTests`, Postgres 17
   Testcontainer):** backfill into schema A; a row in A's `analytics_usage_hourly` is invisible
   through schema B; a forced failure backfilling/reconciling tenant A leaves tenant B intact and
   the fan-out completes (28-10 AC5 tolerance).

6. **Integration/endpoint — backfill control surface (`AnalyticsBackfillEndpointTests`):**
   `POST …/backfill` returns 202 + `Location`; the enqueued task drives status `pending → running →
   completed`; `GET …/backfill/{id}` reflects progress; `PlatformOwnerAccess` RBAC (platform owner
   200; tenant member 403; cross-tenant/non-owner 403/404); `from >= to` or over-span → 400.

7. **Unit — workflow structure (`HourlyAnalyticsRollupWorkflowStructureTests`, extended):** the
   reconcile fan-out step is present and ordered after the 36-2 dimensional fan-out + `EmitHourCompleted`;
   the platform + dimensional steps are unchanged.

8. **Unit — alert rule seeding (extend `BuiltInAlertRules` tests):** the seeder creates
   `analytics-reconciliation-mismatch`; the evaluator fires on an appended
   `ANALYTICS.RECONCILIATION.MISMATCH`; throttle suppresses a burst.

**Mocks:** No external provider/Stripe calls (read-only aggregation + replay). EF InMemory for the
backfill-loop / reconciliation-math / catalog shape; a real Postgres 17 Testcontainer for per-tenant
isolation + NULLS-NOT-DISTINCT upsert convergence (EF InMemory honours neither the unique-NULL
collapse nor `ExecuteDeleteAsync` semantics — same rationale as 36-2 / `ConventionStoreMigrationTests`).

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Activities/Analytics/AnalyticsEventCatalog.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/Analytics/BackfillTenantAnalyticsActivity.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/Analytics/ReconcileAnalyticsActivity.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/Analytics/FanOutTenantReconcileActivity.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/Analytics/AnalyticsRollupEvents.cs` | Modify (add RECONCILIATION/BACKFILL event constants) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs` | Modify (add Backfill POST 202 + GetBackfillStatus) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/AnalyticsBackfillTaskHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/IAnalyticsBackfillStatusStore.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/AnalyticsBackfillStatusStore.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Alerts/Rules/BuiltInAlertRules.cs` | Modify (add analytics-reconciliation-mismatch spec) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map backfill endpoints PlatformOwnerAccess; register task handler) |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupWorkflow.cs` | Modify (add reconcile fan-out step) |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Analytics/AnalyticsEventCatalogDriftTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Analytics/BackfillTenantAnalyticsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Analytics/ReconcileAnalyticsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Analytics/HourlyAnalyticsRollupWorkflowStructureTests.cs` | Modify (assert reconcile step present + ordering) |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Analytics/AnalyticsRollupEventsTests.cs` | Modify (new event constants) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/AnalyticsBackfillEndpointTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/AnalyticsBackfillReconcileIsolationTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions (analytics, rollup, reconciliation,
   tenancy, event sourcing, backfill).
3. Reviewed Story 36-2 (`ComputeTenantDimensionalRollupActivity.ComputeAsync`,
   `AnalyticsProjectionCheckpoint`, `AnalyticsRollupEvents`, `HourlyAnalyticsRollupWorkflow`) and
   Story 28-10 (`FanOutTenantRollupsActivity` tolerance shape, `AggregateLlmUsage`,
   `PlatformAnalyticsService` fact-table-vs-live fallback) — this story is a near-verbatim
   extension/observation of those shapes.
4. Reviewed Story 20-3 reconciliation (hourly local-vs-source compare, `RECONCILIATION_MISMATCH`
   WARN) and Story 28-6 platform task queue (the 202 + `PlatformTaskWorker` off-thread pattern).
5. Confirmed the test runner: docker-bound suites run via `sg docker -c "dotnet test …"` (the build
   itself needs no wrapper).
6. Planned the TDD cycle (drift guard + idempotency + match/mismatch tests red first, then activities).

### Key Design Decisions

- **The catalog is the single source of truth, the guard is the enforcement.** Rather than scatter
  field-name string literals across emit and read sites, the catalog declares them once and a test
  fails the build when read-site, catalog, and (in-solution) emit-site disagree. This is the
  cheapest possible defence against the "someone renamed `costUsd` and the cost column silently went
  to zero" failure — exactly the production-trust gap this story exists to close.
- **Backfill reuses the projection, never forks it.** `BackfillAsync` is a thin loop over
  `ComputeTenantDimensionalRollupActivity.ComputeAsync` — one bucket per hour. Backfill and the
  hourly rollup are literally the same compute on different hours, so they cannot diverge and
  backfill inherits 36-2's idempotency (whole-bucket overwrite) for free. The only backfill-specific
  logic is range iteration + `resetCheckpoint` rewind.
- **Idempotency is the upsert, not the checkpoint.** Re-running a range converges because each
  bucket recomputes whole and overwrites; the checkpoint is an efficiency cursor (skip already-folded
  events), so a checkpoint bug can re-fold without corrupting totals. `resetCheckpoint` is a deliberate
  rewind for full re-derivation, not a correctness crutch.
- **Reconciliation compares the projection to its own source via the same helpers.** `ReconcileAsync`
  recomputes live grand totals with the *exact* `AggregateLlmUsage`/dispatch/workflow extraction the
  projection uses — so any non-zero delta means the projection (not the counting rule) drifted: stale
  rows, a missed checkpoint advance, a skipped fan-out tenant. This is the in-process analogue of the
  28-10 fact-table-vs-live fallback and the 20-3 local-vs-Stripe compare.
- **Mismatch is a signal, not a failure.** A `RECONCILIATION.MISMATCH` is WARN — it never fails the
  rollup; the alert rule + runbook consume it. Tolerance (relative + absolute floor) keeps a 1-token
  rounding blip on a near-empty tenant from paging, while a 5% drift on a busy tenant fires.
- **Backfill runs off-thread (202).** A multi-month backfill over a busy tenant's event stream can
  take minutes; running it inline on the admin request would hold a connection and time out. The
  Story 28-6 platform task worker pattern (same as Cranl provision) keeps the request a 202 with a
  pollable status — never blocking tenant traffic (AC11).
- **Per-tenant failure isolation everywhere.** Both the reconcile fan-out and the backfill
  per-hour/per-tenant loops mirror `FanOutTenantRollupsActivity`: catch, emit the tenant-scoped
  `…_FAILED`/`MISMATCH`, continue. One bad tenant never aborts the sweep.

### Hardening-only boundary

This story adds **no** schema change to the Story 36-1 fact tables, **no** change to the Story 36-2
projection compute (it *reuses* `ComputeAsync` unchanged), and **no** new tenant-facing dashboard
surface. It does **not** touch the control-plane `platform_analytics_hourly` rollup or its purge.
Any PR that alters the 36-1/36-2 schema or projection semantics under cover of this story is out of
scope — keep the diff to the catalog, the backfill activity/endpoint/task, the reconciliation
activity + fan-out, the workflow wiring, the one alert rule, and tests.

## Logging Requirements

- **INFO**: backfill started (`tenantId`, from, to, hours, resetCheckpoint); backfill progress
  (`tenantId`, hoursDone, hoursTotal) at a throttled cadence; backfill completed (`tenantId`, hours);
  reconcile fan-out start (`window`, tenantCount); per-tenant reconcile OK (`tenantId`, window,
  checkpointLag); reconcile fan-out completed (`ok`, `mismatched`, `failed`).
- **DEBUG**: per-hour bucket replay (`tenantId`, hour); per-measure projected-vs-live values; cursor
  read/rewind (`from`, `to` SequenceNumber).
- **WARN**: reconciliation mismatch (`tenantId`, measure, projected, live, deltaPct, window) — does
  not fail the rollup; checkpoint lag over a configured threshold; per-tenant backfill/reconcile
  failed (`tenantId`, errorType) — fan-out continues; 35-2 `billing_mode` absent → cost-basis defaults
  to platform (informational, once per run).
- **ERROR**: backfill aborted by host shutdown/cancellation (re-thrown `OperationCanceledException`);
  backfill task moved to dead-letter (malformed payload, unknown tenant — `PlatformTaskTerminalException`).
- **Structured context**: include `{ tenantId, from, to, hour, hoursDone, hoursTotal, measure,
  projected, live, deltaPct, checkpointLag }` where applicable.
- **Credential safety**: NEVER log tenant connection strings, search-path schema secrets, or provider
  API keys; per-tenant exception messages surfaced into `…_FAILED`/`MISMATCH` events must not carry
  connection strings (the activities read only tenant DB data — same guarantee as
  `FanOutTenantRollupsActivity`/36-2). The reconciliation reads only the `billing_mode` discriminator,
  never raw provider-key plaintext.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
