# Story 36-2: DCB-to-Analytics Projection Pipeline (Dimensional Rollup)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase workflow (Read → Research → Break Down → TDD →
Quality Gates → Failure Handling), the `.dev/` knowledge base, TRACE/DEBUG logging
requirements, test-first development, and the build-success / coverage gates.

## User Story

As a **tenant administrator** (SaaS) **/ self-hosted owner** (single-user),
I want my per-tenant DCB event stream (`LLM.CALL.SUCCESS`, `AGENT.DISPATCH.*`, `WORKFLOW.*`) and
`ProviderDiagnostic` rows projected, every hour, into the dimensional fact tables from Story 36-1
— broken down by provider, agent, workflow definition, repo, and a BYOK-vs-platform cost basis —
so that every Epic 36 dashboard, export, and scheduled report reads pre-aggregated,
multi-dimensional facts instead of re-scanning the raw event stream, and the projection is
idempotent, resumable, and per-tenant isolated.

## Priority

P0 — The schema (Story 36-1) is inert until something populates it. Every downstream Epic 36
surface (query API, exports, scheduled reports) reads `analytics_usage_hourly` /
`analytics_usage_daily`; nothing renders a tenant chart until this projection fills them.

## Scope

Population pipeline **only** — this story writes the Story 36-1 tables; it does **not** alter
their schema. It **extends, does not replace,** the existing Story 28-10
`HourlyAnalyticsRollupWorkflow`: the new per-tenant dimensional rollup runs as an additional fan-out
step **alongside** the existing platform fact-table rollup, sharing the same idempotent-upsert +
replay-safe + per-tenant-failure-tolerant shape. Concretely it adds:

- a `ComputeTenantDimensionalRollupActivity` that aggregates one tenant's hour of
  `domain_events` + `ProviderDiagnostic` rows into `analytics_usage_hourly` rows, one per
  `(Provider, AgentId, WorkflowDefinitionId, RepoId, CostBasis)` tuple;
- a `CompactDailyAnalyticsActivity` that rolls `analytics_usage_hourly` up into
  `analytics_usage_daily` (a lossless `GROUP BY`) and a hourly-retention purge mirroring
  `PurgeStaleAnalyticsActivity`;
- a `SequenceNumber` checkpoint per tenant so the projection is resumable and a re-run never
  double-counts;
- backfill support (drive an arbitrary historical `hour` through the same code path).

The control-plane `platform_analytics_hourly` rollup (Story 28-10) and its purge are left
**entirely intact** — the platform fact table and the per-tenant dimensional store are two
different consumers of the same fan-out.

## Acceptance Criteria

1. A new `ComputeTenantDimensionalRollupActivity`
   (`apps/tamma-elsa/src/Tamma.Activities/Analytics/ComputeTenantDimensionalRollupActivity.cs`,
   extending `Tamma.Activities.Core.TammaAsyncActivity`) reads one tenant's `domain_events` (via
   `ITenantDbContextFactory.CreateAsync`) and `ProviderDiagnostic` rows for a single UTC
   top-of-hour bucket and **emits one `AnalyticsUsageHourly` row per
   `(Provider, AgentId, WorkflowDefinitionId, RepoId, CostBasis)` tuple**, with summed measures
   `TokensIn`, `TokensOut`, `CostUsd`, `PlatformBilledUsd`, `WorkflowsStarted`,
   `WorkflowsCompleted`, `WorkflowsFailed`, `AgentDispatches`. It exposes a static pure-DI
   `ComputeAsync(...)` entry point (taking `ITenantDbContextFactory`, `ITenantDbContextFactory`-
   resolved write context, `IPlatformEventPublisher`, `tenantId`, `hour`, pricing config, logger,
   `CancellationToken`) so the fan-out, the backfill endpoint, and unit tests drive the same code
   without an `ActivityExecutionContext` — mirroring `ComputeTenantRollupActivity.ComputeAsync`.

2. The **provider** dimension is read from each usage row's provider key (`ProviderDiagnostic.ProviderKey`
   for diagnostic-sourced measures; the `provider` field on the `LLM.CALL.SUCCESS` data/`Tags` for
   event-sourced measures). The **workflow** dimension is read from the `WORKFLOW.*` event's
   workflow-definition id (and the `workflowDefinitionId` tag on LLM/agent events when present);
   the **repo** dimension from the `repoId`/`ProjectId` tag. Rows whose dimension value is absent
   bucket under that dimension `= NULL` so totals still reconcile — never dropped, never coerced
   to a sentinel string.

3. The **agent** dimension is read from the `agent_id` DCB tag (Epic 32 action trail) on each
   event — and from `ProviderDiagnostic.AgentType` for diagnostic-sourced measures. Rows with no
   agent tag bucket under `AgentId = NULL`, so a tenant's per-agent breakdown and its grand total
   reconcile exactly (the `NULL` bucket is "unattributed", not lost).

4. `CostBasis` is resolved **per usage record**: `byok` when the call ran against a tenant
   BYOK key, else `platform`. The discriminator is read from the `billing_mode` tag on the
   `LLM.CALL.SUCCESS`/`LLM.CALL.FAILED` event and from the `ProviderDiagnostic.BillingMode`
   column — both produced by Story 35-2 / Epic 35 (`byok | platform`). When no `billing_mode` is
   present (single-user mode, or events predating 35-2) the row defaults to `CostBasis.Platform`
   for self-hosted/legacy and is documented as such; the resolution helper is a single pure
   function so the rule is testable in isolation.

5. For `platform`-basis rows the activity computes
   `PlatformBilledUsd = CostUsd * (1 + margin)` using the pricing/margin config from Story 36-7
   (consumed via an injected `IAnalyticsPricingConfig` seam — this story **does not own** the
   margin math); `byok`-basis rows carry `PlatformBilledUsd = 0` (Tamma never marks up a BYOK
   call). When the 36-7 config is unavailable the seam yields a zero margin and the activity logs
   a WARN — it never hardcodes a margin and never fails the rollup.

6. The rollup is **idempotent**: for each computed tuple the activity reads the existing
   `analytics_usage_hourly` row on the full dimension business key
   (`Hour, Provider, AgentId, WorkflowDefinitionId, RepoId, CostBasis`) and **upserts in place**
   (read-then-update, with the Story 36-1 `UX_analytics_usage_hourly_dims` NULLS-NOT-DISTINCT
   unique index as the concurrent-replay backstop) rather than inserting duplicates. A replay test
   re-runs the same hour twice and asserts row counts and measures are unchanged.

7. The projection is **resumable via a `SequenceNumber` checkpoint**: a per-tenant
   `analytics_projection_checkpoint` row records the highest `DomainEvent.SequenceNumber` already
   folded into the dimensional store. A run processes events with `SequenceNumber > checkpoint`
   for buckets it owns and advances the checkpoint atomically with the upsert; a crash mid-run
   re-processes only un-checkpointed events (which the idempotent upsert absorbs without
   double-counting). `SequenceNumber` — not `Id` or `CreatedAt` — is the cursor (it is the
   monotonic `BIGSERIAL` total-order column on `DomainEvent`, immune to same-millisecond ties).

8. **Per-tenant isolation** is physical: each tenant's events are read through that tenant's
   `TenantDbContext` (search-path schema, no cross-tenant query filter) and written to that
   tenant's own `analytics_usage_*` tables. A tenant's projection never reads or writes another
   tenant's schema; a fan-out failure or skip for tenant A leaves tenant B's rollup untouched
   (proven by an isolation test).

9. **Backfill support**: the dimensional rollup accepts an explicit historical `hour` input (the
   same `hour` backfill input the Story 28-10 workflow already threads through `InitBucket`) and an
   optional `resetCheckpoint` flag, so an operator can re-project an arbitrary past window
   (e.g. after a schema/pricing fix) through the identical idempotent code path. Backfilling a
   bucket that was already projected is a no-op on measures (idempotent upsert) and re-derives
   `PlatformBilledUsd` from the current 36-7 margin.

10. A new `CompactDailyAnalyticsActivity`
    (`apps/tamma-elsa/src/Tamma.Activities/Analytics/CompactDailyAnalyticsActivity.cs`) rolls a
    tenant's `analytics_usage_hourly` rows for a completed UTC day into `analytics_usage_daily` as
    a lossless `GROUP BY date_trunc('day', Hour), <all dims>` with summed measures, upserting on
    the daily `UX_analytics_usage_daily_dims` business key (idempotent, same read-then-upsert
    shape). It runs once per day (when the rollup's target hour is the first hour of a new UTC
    day, compacting the day that just ended) so no second scheduler is introduced.

11. An hourly **retention purge** for `analytics_usage_hourly` mirrors
    `PurgeStaleAnalyticsActivity`: it deletes per-tenant hourly rows older than the configured
    retention window (default 13 months, Doc 04 §7) via `ExecuteDeleteAsync`, runs last (after the
    fresh bucket and daily compaction are persisted), is best-effort (never rethrows a transient
    failure into a rollup that already wrote useful rows), and emits a terminal purge event.
    Daily rows retain on the longer daily-retention window (config-driven, default longer than
    hourly).

12. Per `tenant × hour`, the activity emits
    `ANALYTICS.ROLLUP.TENANT_DIMENSIONAL_COMPLETED` on success and
    `ANALYTICS.ROLLUP.TENANT_DIMENSIONAL_FAILED` on failure (new constants on
    `AnalyticsRollupEvents`), carrying `{ hour, tenantId, rowsWritten, tuples, tokensIn, tokensOut,
    costUsd, platformBilledUsd, checkpoint }` (success) or `{ hour, tenantId, errorType, message }`
    (failure). **A single tenant's failure does not abort the fan-out** — the fan-out catches,
    emits `…_FAILED`, increments a failure counter, and continues to the next tenant (Story 28-10
    AC5 tolerance shape). Compaction and purge emit
    `ANALYTICS.COMPACT.DAILY` / `ANALYTICS.PURGE.USAGE_HOURLY` terminal events.

13. A **projection-lag SLO** is observable: the workflow records, per fan-out pass, the wall-clock
    lag between the rolled-up `hour` bucket and the time the projection completed, and emits a
    `ANALYTICS.ROLLUP.DIMENSIONAL_LAG` event (and an OTel gauge `tamma.analytics.projection_lag_seconds`,
    following the `KekRotationMetrics` gauge precedent) when the lag exceeds the configured SLO
    budget (default 2 hours). The SLO breach is a WARN-level structured log + event, not a failure
    — the runbook/alerts consume it.

14. The new dimensional fan-out is wired into `HourlyAnalyticsRollupWorkflow` as an **additional
    sequence step after** the existing `FanOutTenantRollups` (platform fact table) step and before
    `EmitHourCompleted`/`PurgeStaleAnalytics`, so the platform rollup and the dimensional rollup
    share one schedule, one advisory lock, and one target-hour. The existing platform rollup,
    its events, and `PurgeStaleAnalyticsActivity` are unchanged (a structure test asserts the new
    step is present and the platform step still precedes it).

15. Unit + integration tests cover: provider/agent/workflow/repo/cost-basis bucketing (including
    the `NULL` buckets reconciling to the grand total); BYOK-vs-platform classification from the
    `billing_mode` tag / `ProviderDiagnostic.BillingMode`; `PlatformBilledUsd` margin application
    (platform only; BYOK → 0) against a mocked `IAnalyticsPricingConfig`; JSON measure extraction
    reuse (shared `AggregateLlmUsage`-style helper); idempotent replay (re-run an hour → identical
    rows/measures); checkpoint advance + crash-resume (re-run un-checkpointed events → no
    double-count); daily compaction lossless-sum; per-tenant isolation (row in schema A invisible
    in schema B; tenant A failure leaves tenant B intact); and the projection-lag SLO event firing
    past the budget.

## Tasks / Subtasks

- [ ] Task 1: Event-catalogue + cost-basis helper (AC: 4, 12, 13)
  - [ ] Add `TenantDimensionalRollupCompleted`/`…Failed`, `DailyCompacted`,
        `UsageHourlyPurged`/`…Failed`, `DimensionalLag` constants to
        `AnalyticsRollupEvents` (new section, same `AGGREGATE.ACTION.STATUS` shape).
  - [ ] Add a pure `ResolveCostBasis(billingModeTag, providerDiagnosticBillingMode)` helper
        returning `CostBasis` with the documented default; unit test all branches.

- [ ] Task 2: `IAnalyticsPricingConfig` seam (AC: 5)
  - [ ] Define the read-only margin seam (consumes Story 36-7); add a `NullAnalyticsPricingConfig`
        zero-margin fallback + WARN log for when 36-7 is not yet wired.
  - [ ] Unit test: `byok → 0`; `platform → CostUsd * (1 + margin)`; null config → zero margin.

- [ ] Task 3: `analytics_projection_checkpoint` (AC: 7)
  - [ ] Add the per-tenant checkpoint entity + Tenant EF mapping + additive Tenant migration
        (one row, highest folded `SequenceNumber`); resumable read + atomic advance.
  - [ ] Unit/integration test: checkpoint advances; crash-resume re-folds only un-checkpointed
        events with no double-count.

- [ ] Task 4: `ComputeTenantDimensionalRollupActivity` (AC: 1, 2, 3, 6, 8, 9)
  - [ ] Aggregate `domain_events` + `ProviderDiagnostic` for the hour into per-tuple measures;
        reuse the JSON measure-extraction helper; read-then-upsert on the dimension business key.
  - [ ] Static `ComputeAsync` pure-DI entry point; backfill `hour` + `resetCheckpoint` inputs.

- [ ] Task 5: `CompactDailyAnalyticsActivity` + usage-hourly purge (AC: 10, 11)
  - [ ] Daily `GROUP BY` compaction (lossless) upserting on the daily business key.
  - [ ] Best-effort hourly retention purge mirroring `PurgeStaleAnalyticsActivity`.

- [ ] Task 6: Fan-out + workflow wiring + lag SLO (AC: 12, 13, 14)
  - [ ] Add a dimensional fan-out (iterate active tenants, per-tenant try/catch, emit
        `…COMPLETED`/`…FAILED`, count success/failed) — same shape as
        `FanOutTenantRollupsActivity`, or extend it with a second compute step.
  - [ ] Wire the new step into `HourlyAnalyticsRollupWorkflow` after the platform fan-out; record
        + emit projection lag and the OTel gauge.

- [ ] Task 7: Tests (AC: 15)
  - [ ] Unit (InMemory) for bucketing/classification/margin/idempotency/checkpoint/compaction.
  - [ ] Integration (Postgres 17 Testcontainer) for per-tenant isolation + NULLS-NOT-DISTINCT
        upsert collision + checkpoint resume.

## Technical Design

### C# namespace / file structure

```
apps/tamma-elsa/src/
  Tamma.Activities/Analytics/
    ComputeTenantDimensionalRollupActivity.cs      # NEW — per-tenant dimensional projection
    CompactDailyAnalyticsActivity.cs               # NEW — hourly→daily lossless compaction
    PurgeStaleUsageAnalyticsActivity.cs            # NEW — per-tenant analytics_usage_hourly retention purge
    FanOutTenantDimensionalRollupsActivity.cs      # NEW — fan-out (mirror of FanOutTenantRollupsActivity)
    AnalyticsRollupEvents.cs                        # MODIFY — + dimensional/compact/purge/lag event constants
    IAnalyticsPricingConfig.cs                      # NEW — margin seam (consumes Story 36-7)
    NullAnalyticsPricingConfig.cs                   # NEW — zero-margin fallback + WARN
  Tamma.Data/Entities/
    AnalyticsProjectionCheckpoint.cs               # NEW — per-tenant SequenceNumber cursor
    ProviderDiagnostic.cs                          # READ — BillingMode (35-2), ProviderKey, AgentType, ProjectId, Input/OutputTokens, Cost
    DomainEvent.cs                                 # READ — Type, Tags (agent_id/billing_mode/provider/repoId), Data, SequenceNumber
  Tamma.Data/TenantDbContext.cs                    # MODIFY — + DbSet<AnalyticsProjectionCheckpoint>
  Tamma.Data/TammaModelConfiguration.cs            # MODIFY — + ConfigureAnalyticsProjectionCheckpoint (tenant graph)
  Tamma.Data/Migrations/Tenant/<ts>_AddAnalyticsProjectionCheckpoint.cs   # NEW (generated)
  Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupWorkflow.cs             # MODIFY — + dimensional fan-out + compaction + usage purge steps

apps/tamma-elsa/tests/
  Tamma.Activities.Tests/Analytics/
    ComputeTenantDimensionalRollupTests.cs         # NEW — bucketing / classification / margin / idempotency
    CompactDailyAnalyticsActivityTests.cs          # NEW — lossless daily compaction
    AnalyticsProjectionCheckpointTests.cs          # NEW — checkpoint advance + crash-resume
    HourlyAnalyticsRollupWorkflowStructureTests.cs # MODIFY — assert new step present + ordering
    AnalyticsRollupEventsTests.cs                  # MODIFY — new event constants
  Tamma.Api.Tests/Analytics/
    DimensionalRollupIsolationTests.cs             # NEW — Postgres 17 Testcontainer: per-tenant isolation + upsert collision
```

### Source events + diagnostic columns (verified, real)

The projection reads two per-tenant sources, both in the tenant schema:

- **`DomainEvent`** (`Tamma.Data/Entities/DomainEvent.cs`) — `Type`, JSONB `Tags`
  (carries `agent_id` (Epic 32), `billing_mode` (35-2), `provider`, `repoId`,
  `workflowDefinitionId`/`tenantId`), JSONB `Data` (`LLM.CALL.SUCCESS` carries `costUsd`,
  `inputTokens`, `outputTokens` per Story 9-2), and `SequenceNumber` (monotonic `BIGSERIAL`
  total-order cursor — the checkpoint column).
- **`ProviderDiagnostic`** (`Tamma.Data/Entities/ProviderDiagnostic.cs`) — `ProviderKey`,
  `InputTokens`/`OutputTokens`/`TokensUsed`, `Cost`, `AgentType` (agent dimension),
  `ProjectId` (repo dimension), `Model`, and `BillingMode` (added by Story 35-2 → cost basis).

The dimensional rollup mirrors `ComputeTenantRollupActivity` exactly for workflow counts
(`WorkflowInstances` by `Status`), `AGENT.DISPATCH.%` counts (`EF.Functions.Like`, InMemory-and-
Npgsql-safe), and `LLM.CALL.SUCCESS` JSON measure extraction (`AggregateLlmUsage`) — but groups
those measures by the dimension tuple instead of collapsing to one tenant row.

### Cost-basis resolution (pure helper)

```csharp
// CostBasis (Story 36-1 enum) from the 35-2 billing_mode signal.
internal static CostBasis ResolveCostBasis(string? billingModeTag, string? diagnosticBillingMode)
{
    var mode = billingModeTag ?? diagnosticBillingMode;   // event tag wins; diagnostic backs it
    return string.Equals(mode, "byok", StringComparison.OrdinalIgnoreCase)
        ? CostBasis.Byok
        : CostBasis.Platform;                              // platform | absent (single-user/legacy) → platform
}
```

### Per-tenant dimensional upsert (idempotent, read-then-upsert)

```csharp
// One AnalyticsUsageHourly row per (Provider, AgentId, WorkflowDefinitionId, RepoId, CostBasis).
foreach (var (key, m) in measuresByDimension)
{
    var existing = await tenantDb.AnalyticsUsageHourly.FirstOrDefaultAsync(
        r => r.Hour == hour && r.Provider == key.Provider && r.AgentId == key.AgentId
             && r.WorkflowDefinitionId == key.WorkflowDefinitionId && r.RepoId == key.RepoId
             && r.CostBasis == key.CostBasis, ct);

    var billed = key.CostBasis == CostBasis.Platform
        ? Math.Round(m.CostUsd * (1 + pricing.MarginFor(key.Provider)), 4, MidpointRounding.AwayFromZero)
        : 0m;

    if (existing is null)
        tenantDb.AnalyticsUsageHourly.Add(new AnalyticsUsageHourly { /* dims + measures + billed */ });
    else
        { existing.TokensIn = m.TokensIn; /* …overwrite all measures… */ existing.PlatformBilledUsd = billed; existing.ComputedAt = now; }
}
await AdvanceCheckpointAsync(tenantDb, maxSequenceNumber, ct);   // atomic with the upsert SaveChanges
await tenantDb.SaveChangesAsync(ct);
```

Overwrite (not increment) on re-run keeps replay/backfill idempotent: the activity recomputes the
**whole** bucket from source each pass, so a second run writes the same values. The
`UX_analytics_usage_hourly_dims` NULLS-NOT-DISTINCT index (Story 36-1 AC7) is the concurrent-replay
backstop.

### Checkpoint (resumable)

`AnalyticsProjectionCheckpoint` is a single per-tenant row (`Stream = "dimensional"`,
`LastSequenceNumber long`, `UpdatedAt`). A run folds `domain_events` with
`SequenceNumber > LastSequenceNumber` for the buckets it owns and advances the checkpoint in the
same `SaveChanges` as the upsert. A crash before commit replays the same events on the next pass;
the idempotent upsert (whole-bucket overwrite) absorbs the replay with no double-count. The
cursor is `SequenceNumber` — never `Id`/`CreatedAt` — per the `DomainEvent.SequenceNumber`
doc-comment (the `AlertRuleEvaluator` cursor precedent).

### Daily compaction (lossless GROUP BY)

`CompactDailyAnalyticsActivity` runs when the target hour is `00:xx` of a new UTC day (compacting
the day that just ended). It is a pure `GROUP BY date_trunc('day', Hour), Provider, AgentId,
WorkflowDefinitionId, RepoId, CostBasis` with summed measures, upserted on the daily business key
— lossless because Story 36-1 made the hourly and daily entities share their dimension+measure
contract exactly.

### Retention purge

`PurgeStaleUsageAnalyticsActivity` mirrors `PurgeStaleAnalyticsActivity`: per-tenant
`ExecuteDeleteAsync` of `analytics_usage_hourly` rows older than the retention window (13-month
default), best-effort (catches all but `OperationCanceledException`, emits
`ANALYTICS.PURGE.USAGE_HOURLY` / `…FAILED`, never rethrows), runs last in the sequence.

### Workflow wiring

```
HourlyAnalyticsRollupWorkflow.Build (MODIFY) — Sequence:
  initBucket
  platformRollup              (ComputePlatformRollupActivity)        — unchanged (28-10)
  fanOut                      (FanOutTenantRollupsActivity)          — unchanged (28-10, platform fact table)
  fanOutDimensional           (FanOutTenantDimensionalRollupsActivity) — NEW
  compactDaily                (CompactDailyAnalyticsActivity, day-boundary only) — NEW
  emitCompleted               (EmitHourCompletedActivity)            — unchanged
  purgeStale                  (PurgeStaleAnalyticsActivity)          — unchanged (CP)
  purgeUsageStale             (PurgeStaleUsageAnalyticsActivity)     — NEW (per-tenant)
```

One schedule (`0 5 * * * *`), one advisory lock, one target hour — the platform rollup and the
dimensional rollup are two consumers of the same fan-out, exactly as the scope demands.

### DCB events (new — owned by this story)

Story 36-1 reserved the `ANALYTICS.PROJECTION.*` family for this story; we land the concrete
names on `AnalyticsRollupEvents` (same `AGGREGATE.ACTION.STATUS` convention as 28-10):

| Event | When | Key data |
|---|---|---|
| `ANALYTICS.ROLLUP.TENANT_DIMENSIONAL_COMPLETED` | per tenant×hour, success | rowsWritten, tuples, tokensIn/Out, costUsd, platformBilledUsd, checkpoint |
| `ANALYTICS.ROLLUP.TENANT_DIMENSIONAL_FAILED` | per tenant×hour, failure (fan-out continues) | errorType, message |
| `ANALYTICS.COMPACT.DAILY` | per tenant×day, after compaction | day, rowsWritten |
| `ANALYTICS.PURGE.USAGE_HOURLY` / `…FAILED` | per tenant, after retention sweep | cutoff, rowsDeleted |
| `ANALYTICS.ROLLUP.DIMENSIONAL_LAG` | per pass when lag > SLO budget | lagSeconds, hour, budgetSeconds |

Per-tenant events carry `tenantId` so the Story 28-6 step-dedup index applies; events are appended
via `IPlatformEventPublisher.AppendAndPublishAsync` (best-effort emission, same as the 28-10 path).

### API shape

**No new HTTP endpoints in this story.** Population is a scheduled workflow. A future Epic 36
story exposes the query API over the populated tables (behind `MemberAccess`); an operator
backfill/force-run uses the existing `POST /workflows/run/hourly-analytics-rollup` with an
optional `hour` (and `resetCheckpoint`) input, the same control surface Story 28-10 already
documents.

### Per-mode + per-tenant handling

| Question | single-user mode | SaaS mode |
|---|---|---|
| What does the fan-out iterate? | The sole tenant (one schema). | All active tenants from the CP directory (28-10 fan-out target set). |
| Who owns a projected row? | The sole user — it lives in their (only) tenant schema. | The tenant — its `t_<hex>` schema. |
| Cost basis default | `platform` (no `billing_mode` tag; self-hosted key) — never marked up unless a margin is configured. | `byok`/`platform` from the 35-2 `billing_mode` tag per call. |
| Isolation plane | Search-path schema + connection string. | Same — physically separate schema per tenant; a row in A is unreachable from B. |
| Margin / `PlatformBilledUsd` | Typically 0 (no platform billing). | `CostUsd × (1 + margin)` from 36-7 for `platform` rows; `0` for `byok`. |

Mode does not change the projection shape — both resolve to exactly one tenant schema per run.
The platform-wide owner rollup stays on the CP `platform_analytics_hourly` table (28-10), untouched.

## Dependencies

**Prerequisite (internal):**
- **Story 36-1** — the per-tenant `AnalyticsUsageHourly` / `AnalyticsUsageDaily` fact tables +
  `CostBasis` enum + `UX_*_dims` business-key indexes this story writes. (Authored; drafted.)
- **Story 28-10** — the `HourlyAnalyticsRollupWorkflow` + `FanOutTenantRollupsActivity` +
  `ComputeTenantRollupActivity.AggregateLlmUsage` + `PurgeStaleAnalyticsActivity` +
  `AnalyticsRollupEvents` this story extends/mirrors. (Merged.)
- **Epic 4 (DCB `DomainEvent`)** — the per-tenant event stream (`LLM.CALL.SUCCESS`,
  `AGENT.DISPATCH.*`, `WORKFLOW.*`) and its `SequenceNumber` total-order cursor the projection
  reads from. (In place.)
- **Epic 28** — per-tenant schema + `ITenantDbContextFactory` + `EfTenantDbMigrator` (checkpoint
  migration rides the Tenant graph). (Merged.)

**Soft / forward (degrade gracefully if absent):**
- **Story 35-2 (Epic 35)** — the `billing_mode` tag on `LLM.CALL.*` events + the
  `ProviderDiagnostic.BillingMode` column that resolve `CostBasis`. The spec frames this as
  "Epic 29 secret cabinet to detect BYOK key origin"; in this codebase the BYOK origin is already
  surfaced as the `billing_mode` discriminator by 35-2 (which itself reads the cabinet via
  `ISecretStore`). If 35-2 has not landed, all rows resolve to `CostBasis.Platform` and the
  classification test pins that default.
- **Epic 32 (agent_id DCB tags)** — the `agent_id` tag the agent dimension reads. If absent, rows
  bucket under `AgentId = NULL` (totals still reconcile).
- **Story 36-7 (pricing/margin config)** — consumed via `IAnalyticsPricingConfig` for
  `PlatformBilledUsd`. If unavailable, the `NullAnalyticsPricingConfig` zero-margin fallback
  applies and a WARN is logged.

**Blocks (internal):**
- Downstream Epic 36 stories — tenant analytics query API, exports, scheduled reports — all read
  the tables this story populates.

**External:**
- PostgreSQL 17 (NULLS NOT DISTINCT upsert backstop; per-tenant `ExecuteDeleteAsync`).
- EF Core 9 / Npgsql.
- Testcontainers + Docker for the isolation/upsert/resume integration suite (run via
  `sg docker -c "dotnet test ..."`).

## Testing Strategy

1. **Unit — cost-basis classification (`ComputeTenantDimensionalRollupTests`):** `billing_mode`
   tag `byok`/`platform`, `ProviderDiagnostic.BillingMode` fallback, absent → `Platform` default.

2. **Unit — dimensional bucketing:** events spanning multiple providers / agents / workflows /
   repos produce one row per tuple; events missing a dimension bucket under `NULL`; assert
   `Σ(per-dimension rows) == grand total` (reconciliation).

3. **Unit — margin / `PlatformBilledUsd`:** mocked `IAnalyticsPricingConfig` → `platform` rows
   carry `CostUsd × (1 + margin)`; `byok` rows carry `0`; null config → zero margin + WARN.

4. **Unit — JSON measure extraction reuse:** the shared `AggregateLlmUsage`-style helper sums
   `costUsd`/`inputTokens`/`outputTokens` and skips malformed blobs (same tolerance as 28-10).

5. **Unit/integration — idempotent replay:** run an hour twice → identical row count + measures
   (whole-bucket overwrite); concurrent replay collapses on the `UX_*_dims` index (Testcontainer).

6. **Integration — checkpoint resume (`AnalyticsProjectionCheckpointTests`, Postgres 17):**
   process a batch → checkpoint advances to max `SequenceNumber`; simulate a crash before commit →
   re-run re-folds only un-checkpointed events with no double-count.

7. **Unit — daily compaction (`CompactDailyAnalyticsActivityTests`):** 24 hourly buckets → one
   daily set; measures sum losslessly; re-compaction is idempotent.

8. **Integration — per-tenant isolation (`DimensionalRollupIsolationTests`, Postgres 17
   Testcontainer, per `SchemaPerTenantMigrationTests`):** project into two schemas; a row written
   to schema A's `analytics_usage_hourly` is invisible through schema B; a forced failure on
   tenant A leaves tenant B's projection intact and the fan-out completes.

9. **Unit — workflow structure (`HourlyAnalyticsRollupWorkflowStructureTests`, extended):** the
   new dimensional fan-out + compaction + usage-purge steps are present; the platform fan-out
   still precedes the dimensional one; `EmitHourCompleted` stays terminal-before-purge.

10. **Unit — projection-lag SLO:** a pass whose completion lags the bucket beyond the budget emits
    `ANALYTICS.ROLLUP.DIMENSIONAL_LAG` + the OTel gauge; under budget emits neither.

**Mocks:** No external provider/Stripe calls (read-only aggregation). InMemory provider for
bucketing/classification/margin shape; a real Postgres 17 Testcontainer for isolation, NULLS NOT
DISTINCT upsert collision, and checkpoint resume (EF InMemory honours neither unique-NULL collapse
nor `ExecuteDeleteAsync` semantics — same rationale as `ConventionStoreMigrationTests`).

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Activities/Analytics/ComputeTenantDimensionalRollupActivity.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/Analytics/CompactDailyAnalyticsActivity.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/Analytics/PurgeStaleUsageAnalyticsActivity.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/Analytics/FanOutTenantDimensionalRollupsActivity.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/Analytics/IAnalyticsPricingConfig.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/Analytics/NullAnalyticsPricingConfig.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/Analytics/AnalyticsRollupEvents.cs` | Modify (add dimensional/compact/purge/lag event constants) |
| `apps/tamma-elsa/src/Tamma.Data/Entities/AnalyticsProjectionCheckpoint.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | Modify (add checkpoint DbSet) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (configure checkpoint entity in tenant graph) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/<ts>_AddAnalyticsProjectionCheckpoint.cs` | Create (generated) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/<ts>_AddAnalyticsProjectionCheckpoint.Designer.cs` | Create (generated) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/TenantDbContextModelSnapshot.cs` | Modify (regenerated) |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupWorkflow.cs` | Modify (add dimensional fan-out + compaction + usage purge steps) |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Analytics/ComputeTenantDimensionalRollupTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Analytics/CompactDailyAnalyticsActivityTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Analytics/AnalyticsProjectionCheckpointTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Analytics/HourlyAnalyticsRollupWorkflowStructureTests.cs` | Modify (assert new steps + ordering) |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Analytics/AnalyticsRollupEventsTests.cs` | Modify (new event constants) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/DimensionalRollupIsolationTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions (analytics, rollup, tenancy,
   event sourcing).
3. Reviewed Story 36-1 (the tables/enum/business-key indexes this story writes) and Story 28-10
   (`HourlyAnalyticsRollupWorkflow`, `FanOutTenantRollupsActivity`,
   `ComputeTenantRollupActivity.AggregateLlmUsage`, `PurgeStaleAnalyticsActivity`,
   `AnalyticsRollupEvents`) — this story is a near-verbatim extension of that shape.
4. Confirmed the test runner: docker-bound suites run via `sg docker -c "dotnet test ..."`
   (session docker group is stale; the build itself needs no wrapper).
5. Planned the TDD cycle (write the bucketing/classification/idempotency tests red first, then the
   activity).

### Key Design Decisions

- **Extend the 28-10 fan-out, don't fork the schedule.** The dimensional rollup is one more
  sequence step on the existing hourly workflow — one schedule, one advisory lock, one target
  hour. Two cron jobs racing the same tenants would double the per-tenant pool churn and split the
  runbook story. The platform fact-table rollup and the per-tenant dimensional store are two
  consumers of the same fan-out.
- **Whole-bucket overwrite, not increment.** The activity recomputes the entire `(tenant, hour)`
  bucket from source each pass and overwrites the measures, so replay and backfill are naturally
  idempotent without delta bookkeeping. The `SequenceNumber` checkpoint is a *resumability /
  efficiency* cursor (skip already-folded events on the happy path), not the idempotency mechanism
  — the upsert is. This is the safest combination: a checkpoint bug can re-fold without corrupting
  totals.
- **`SequenceNumber`, never `Id`/`CreatedAt`, for the cursor.** `DomainEvent.SequenceNumber` is the
  monotonic `BIGSERIAL` total-order column built precisely to tiebreak same-millisecond events
  (its doc-comment names the `AlertRuleEvaluator` precedent). Using `CreatedAt` would skip or
  double-count events that share a millisecond.
- **`NULL` dimension buckets, never sentinels.** An event missing `agent_id`/`provider`/`repo`/
  `workflowDefinitionId` buckets under that dimension `= NULL` — preserved by Story 36-1's NULLS
  NOT DISTINCT business key — so per-dimension breakdowns and the grand total always reconcile.
  Coercing to a `"unknown"` string would fork the total and break the unique key's dedupe.
- **Cost basis from the 35-2 `billing_mode` signal, not a re-derived cabinet lookup.** The spec
  frames BYOK detection as "Epic 29 secret cabinet"; the codebase already surfaces that decision
  as the `billing_mode` tag/column produced by Story 35-2 (which reads the cabinet). Reusing the
  tag keeps this story a pure projection — no secret reads in the analytics path, no credential
  exposure surface.
- **Margin is consumed, not owned.** `PlatformBilledUsd` calls the `IAnalyticsPricingConfig` seam
  (Story 36-7). This story implements zero margin math itself; a `NullAnalyticsPricingConfig`
  zero-margin fallback keeps the rollup green before 36-7 lands.
- **Per-tenant failure tolerance + best-effort housekeeping.** The dimensional fan-out catches per
  tenant, emits `…_FAILED`, and continues (28-10 AC5 shape); compaction and purge are best-effort
  (never rethrow a transient failure into a rollup that already wrote useful rows), exactly like
  `PurgeStaleAnalyticsActivity`.

### Population-only boundary

This story creates **no** schema change to the Story 36-1 fact tables (only the additive
`analytics_projection_checkpoint`), **no** query/export/report endpoint, and **no** dashboard
surface — those are later Epic 36 stories. It does **not** touch the control-plane
`platform_analytics_hourly` rollup, its events, or its purge. Any PR that adds a query API or
alters the 36-1 fact-table schema under cover of this story is out of scope — keep the diff to the
projection activities, the checkpoint entity/migration, the workflow wiring, and tests.

## Logging Requirements

- **INFO**: dimensional fan-out start (`hour`, tenantCount); per-tenant dimensional rollup
  completed (`tenantId`, `hour`, rowsWritten, tuples, tokensIn/Out, costUsd, platformBilledUsd,
  checkpoint); daily compaction completed (`tenantId`, day, rowsWritten); usage-hourly purge
  completed (`cutoff`, rowsDeleted); fan-out completed (`success`, `failed`).
- **DEBUG**: per-tuple measure aggregation; checkpoint read/advance (`from`, `to` SequenceNumber);
  cost-basis resolution per source.
- **WARN**: per-tenant rollup failed (`tenantId`, `hour`, errorType) — fan-out continues;
  projection lag over SLO budget (`lagSeconds`, `budgetSeconds`); 36-7 pricing config unavailable
  → zero margin; usage-hourly purge failed (best-effort).
- **ERROR**: dimensional fan-out aborted by host shutdown/cancellation (re-thrown
  `OperationCanceledException`); checkpoint write failure that prevents commit.
- **Structured context**: include `{ tenantId, hour, rowsWritten, tuples, checkpoint, lagSeconds }`
  where applicable.
- **Credential safety**: NEVER log tenant connection strings, search-path schema secrets, or
  provider API keys; per-tenant exception messages surfaced into `…_FAILED` events must not carry
  connection strings (the compute activity reads only tenant DB data — same guarantee as
  `FanOutTenantRollupsActivity`). The projection never reads raw provider-key plaintext — it reads
  only the `billing_mode` discriminator.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
