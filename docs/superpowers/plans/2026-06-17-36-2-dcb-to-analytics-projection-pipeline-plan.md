# Story 36-2 — DCB-to-Analytics Projection Pipeline / Dimensional Rollup (implementation plan)

> Epic 36 (Analytics & Reporting Platform) · P0 · est. 4-5 days · author Claude · 2026-06-17 ·
> SUB-SKILL: use `superpowers:test-driven-development` — every task writes its failing test first.
> Docker-bound suites run via `sg docker -c "dotnet test ..."`; the build itself needs no wrapper.

**Goal:** Populate the Story 36-1 per-tenant dimensional fact tables (`analytics_usage_hourly` /
`analytics_usage_daily`) from each tenant's DCB event stream + `ProviderDiagnostic` rows, broken
down by provider / agent / workflow / repo / cost-basis. Do it by **extending** the Story 28-10
`HourlyAnalyticsRollupWorkflow` with an additional per-tenant dimensional fan-out step (sharing its
schedule, advisory lock, target hour, fan-out shape, idempotent-upsert + per-tenant-failure
tolerance), plus a daily compaction step and a per-tenant retention purge. Make the projection
idempotent (whole-bucket overwrite), resumable (per-tenant `SequenceNumber` checkpoint), backfillable
(historical `hour` input), per-tenant isolated (physical schema), and SLO-observable (projection lag
event + OTel gauge). **Population only — no fact-table schema change, no query/export endpoint, no
dashboard.**

**Non-goals (YAGNI guard):**
- NO change to the Story 36-1 `analytics_usage_*` table shape (only the additive
  `analytics_projection_checkpoint` table is new schema).
- NO change to the control-plane `platform_analytics_hourly` rollup (28-10), its events, or its
  purge — the platform fact table stays the owner-only fleet store.
- NO query / export / scheduled-report endpoint and NO dashboard — later Epic 36 stories.
- NO margin/markup math implemented here — consumed from Story 36-7 via `IAnalyticsPricingConfig`.
- NO secret/cabinet read in the analytics path — cost basis comes from the 35-2 `billing_mode`
  tag/column, not a fresh BYOK lookup.
- NO second scheduler — the dimensional rollup, daily compaction, and usage purge ride the existing
  hourly workflow.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### What exists and is the pattern to mirror

| Site | What it gives us |
|---|---|
| `src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupWorkflow.cs` | The host workflow to extend. `Build()` is a `Sequence` of `initBucket → platformRollup → fanOut → emitCompleted → purgeStale`, schedule `0 5 * * * *`, optional `hour` backfill input via `InitBucket`. Insert the dimensional fan-out after `fanOut`, daily compaction after it, the usage purge last. The structure test (`HourlyAnalyticsRollupWorkflowStructureTests`) pins the step graph — extend it. |
| `src/Tamma.Activities/Analytics/ComputeTenantRollupActivity.cs` | The canonical per-tenant compute: static `ComputeAsync(cpFactory, tenantFactory, publisher, tenantId, hour, logger, ct)` pure-DI entry point; reads `WorkflowInstances` by `Status`, counts `AGENT.DISPATCH.%` via `EF.Functions.Like` (InMemory+Npgsql safe), pulls `LLM.CALL.SUCCESS` `Data` blobs and aggregates with `AggregateLlmUsage` (skips malformed JSON, rounds cost to 4dp). Read-then-upsert on the business key. **Copy this verbatim, but GROUP BY the dimension tuple instead of collapsing to one tenant row, and write to the tenant's own `analytics_usage_hourly` via `TenantDbContext` instead of CP.** |
| `src/Tamma.Activities/Analytics/FanOutTenantRollupsActivity.cs` | The fan-out: lists active tenants from the CP directory (`DeletedAt == null`, `CreatedAt < hour+1`, `Status` null-or-`active`), loops serially, per-tenant `try/catch` → emits `ANALYTICS.ROLLUP.TENANT_FAILED` + increments `failed` + continues, sets `TenantsSuccess`/`TenantsFailed` outputs, has a `ComputeOneOverride` test seam. Copy for `FanOutTenantDimensionalRollupsActivity` (or extend it with a second compute call per tenant). |
| `src/Tamma.Activities/Analytics/PurgeStaleAnalyticsActivity.cs` | The retention sweeper: `ExecuteDeleteAsync` of rows older than `ComputeCutoff(now, 13mo)`, best-effort (catches all but `OperationCanceledException`, emits `ANALYTICS.PURGE.HOURLY`/`…FAILED`, never rethrows), static pure-DI `PurgeAsync`. Copy for `PurgeStaleUsageAnalyticsActivity` (per-tenant `analytics_usage_hourly` instead of CP `platform_analytics_hourly`). |
| `src/Tamma.Activities/Analytics/AnalyticsRollupEvents.cs` | Event catalogue + `BuildEvent(type, hour, tenantId?, data?)` + `TruncateToHour`. Add the new dimensional/compact/purge/lag constants here, same `AGGREGATE.ACTION.STATUS` shape; the `ANALYTICS.PROJECTION.*` family was reserved for this story by 36-1 §"DCB events". |
| `src/Tamma.Data/Entities/DomainEvent.cs` | The source stream: `Type`, JSONB `Tags` (`agent_id` Epic 32, `billing_mode` 35-2, `provider`, `repoId`, `workflowDefinitionId`), JSONB `Data` (`costUsd`/`inputTokens`/`outputTokens`), and **`SequenceNumber`** — the monotonic `BIGSERIAL` total-order cursor (doc-comment names `AlertRuleEvaluator` as the precedent consumer). This is the checkpoint column. |
| `src/Tamma.Data/Entities/ProviderDiagnostic.cs` | The diagnostic source: `ProviderKey` (provider dim), `InputTokens`/`OutputTokens`/`Cost`, `AgentType` (agent dim), `ProjectId` (repo dim), `Model`, and **`BillingMode`** (`byok`/`platform`, added by 35-2 → cost basis). DbSet `ProviderDiagnostics` on `TenantDbContext` (line 56). |
| `src/Tamma.Data/Entities/AnalyticsUsageHourly.cs` / `AnalyticsUsageDaily.cs` (Story 36-1) | The write targets: dims `Provider`(req)/`AgentId?`/`WorkflowDefinitionId?`/`RepoId?`/`CostBasis`; measures `TokensIn/Out`, `CostUsd`, `PlatformBilledUsd`, `WorkflowsStarted/Completed/Failed`, `AgentDispatches`; `Hour`/`Day`; `ComputedAt`. `UX_*_dims` NULLS-NOT-DISTINCT unique business key is the upsert backstop; `CostBasis` enum is in `Tamma.Core/Enums`. (Authored, drafted — see `docs/stories/epic-36/story-36-1/`.) |
| `src/Tamma.Core/Enums/CostBasis.cs` (Story 36-1) | `Byok`/`Platform`, lowercase-text persisted. The cost-basis helper returns this. |
| `src/Tamma.Data/Abstractions/ITenantDbContextFactory.cs` | `ValueTask<TenantDbContext> CreateAsync(tenantId, ct)` — the per-tenant read+write seam (search-path schema, no cross-tenant filter). |
| `src/Tamma.Activities/Core/TammaActivity.cs` (`TammaAsyncActivity`) | The activity base every analytics activity extends (`Logger`, `EventType`, `RunAsync`). |
| `src/Tamma.Data/Abstractions/IPlatformEventPublisher.cs` | `AppendAndPublishAsync(PlatformEvent, ct)` — durable event emission (best-effort in the catch paths). |
| KekRotationMetrics (Story 28-12, `tamma.kek_rotation.remaining` OTel gauge) | The OTel gauge precedent for the `tamma.analytics.projection_lag_seconds` SLO gauge (per project memory: 28-12 added a gauge via a metrics class). |

### Where cost basis really comes from (resolve the spec's Epic-29 framing)

The spec lists **Epic 29 (secret cabinet) to detect BYOK key origin**. In this codebase the BYOK
decision is already surfaced one layer up, as the `billing_mode` discriminator produced by
**Story 35-2 / Epic 35** (verified in `docs/stories/epic-35/story-35-2/...`, ACs 7-8):

- `ProviderDiagnostic.BillingMode` string column (`byok | platform`) — 35-2 AC7.
- `LLM.CALL.SUCCESS` / `LLM.CALL.FAILED` DCB `Tags` carry `billing_mode` (`byok|platform`),
  `provider`, `model`, `tenantId` — 35-2 AC8.

35-2 itself reads the cabinet (`ISecretStore`) to decide the mode, so 36-2 stays a pure projection:
read the `billing_mode` tag/column, never touch a secret. **Honor the spec's dependency list** (it
names Epic 29 / 32 / 36-7) but point the implementation at the real `billing_mode` signal, and
degrade gracefully (`CostBasis.Platform` default) when 35-2 hasn't landed.

### Confirmed absent (so these are genuinely NEW)

- `ComputeTenantDimensionalRollupActivity`, `CompactDailyAnalyticsActivity`,
  `PurgeStaleUsageAnalyticsActivity`, `FanOutTenantDimensionalRollupsActivity`,
  `IAnalyticsPricingConfig`, `AnalyticsProjectionCheckpoint` — none exist
  (`grep -rln "Dimensional|AnalyticsProjectionCheckpoint|IAnalyticsPricingConfig" src/` → no match).
- Story 36-7 (`IAnalyticsPricingConfig` source), Epic 32 `agent_id` tag wiring, and Story 35-2
  `billing_mode` are **not yet present in `src/`** (`grep` for `billing_mode`/`agent_id` in
  analytics paths → no match). All three are forward/soft deps — design degrades gracefully.

---

## Phased task breakdown (test-first)

### Task 1 — event catalogue + cost-basis helper (AC 4, 12, 13)

- **Test first:** extend `tests/Tamma.Activities.Tests/Analytics/AnalyticsRollupEventsTests.cs` —
  assert the new constants (`TenantDimensionalRollupCompleted`/`…Failed`, `DailyCompacted`,
  `UsageHourlyPurged`/`…Failed`, `DimensionalLag`) follow `AGGREGATE.ACTION.STATUS`; add a
  `CostBasis ResolveCostBasis(...)` test covering tag `byok`/`platform`, diagnostic fallback,
  absent → `Platform`.
- **Implement:** add the constants to `AnalyticsRollupEvents`; add the pure
  `ResolveCostBasis(string? billingModeTag, string? diagnosticBillingMode)` helper.
- **Run:** `dotnet test --filter "FullyQualifiedName~AnalyticsRollupEvents|ResolveCostBasis"`.

### Task 2 — `IAnalyticsPricingConfig` margin seam (AC 5)

- **Test first:** a pricing-application test — `platform` → `CostUsd*(1+margin)`; `byok` → `0`;
  `NullAnalyticsPricingConfig` → zero margin + WARN.
- **Implement:** `IAnalyticsPricingConfig` (`decimal MarginFor(string provider)` or similar,
  shaped to consume Story 36-7) + `NullAnalyticsPricingConfig` zero-margin fallback. Register the
  Null seam in DI so the rollup is green before 36-7 lands.
- **Run:** `dotnet test --filter "FullyQualifiedName~AnalyticsPricing"`.

### Task 3 — `AnalyticsProjectionCheckpoint` entity + migration (AC 7)

- **Test first:** `AnalyticsProjectionCheckpointTests` (red against the entity stub) — read default
  (0), advance to max `SequenceNumber`, re-run folds only `> checkpoint`.
- **Implement:**
  - `src/Tamma.Data/Entities/AnalyticsProjectionCheckpoint.cs` — `{ Id, Stream("dimensional"),
    LastSequenceNumber long, UpdatedAt }` (one row per stream; no `TenantId` column — tenant schema
    is the isolation plane, per 36-1 §"Tenant-isolation note").
  - `TammaModelConfiguration.ConfigureAnalyticsProjectionCheckpoint(...)` in the tenant graph
    (unique on `Stream`, `ApplyTenantFilter(entity, fixedTenantId, _ => null)`); add the DbSet to
    `TenantDbContext`.
  - `dotnet ef migrations add AddAnalyticsProjectionCheckpoint -c TenantDbContext`
    (`TenantDesignTimeDbContextFactory`, history `__TenantMigrationsHistory`,
    `Migrations/Tenant/`); `has-pending-model-changes -c TenantDbContext` → none.
- **Run:** `sg docker -c "dotnet test --filter 'FullyQualifiedName~AnalyticsProjectionCheckpoint'"`.

### Task 4 — `ComputeTenantDimensionalRollupActivity` (AC 1, 2, 3, 6, 8, 9)

- **Test first:** `ComputeTenantDimensionalRollupTests` (InMemory) — bucketing across
  provider/agent/workflow/repo/cost-basis; `NULL` buckets reconcile to grand total; idempotent
  replay (run twice → identical rows/measures); margin applied to `platform`, `0` for `byok`.
- **Implement:** new activity extending `TammaAsyncActivity`; static `ComputeAsync(...)` pure-DI
  entry point. Algorithm:
  1. `tenantDb = tenantFactory.CreateAsync(tenantId)`; window `[hour, hour+1)`.
  2. Read `WorkflowInstances` counts (started/completed/failed) — these have no provider/agent
     dimension, so they land on a `(Provider=?, Agent=NULL, ...)` workflow-dimensioned bucket keyed
     by the workflow's definition id (or a workflow-only tuple — pin the exact rule in the test).
  3. `AGENT.DISPATCH.%` events → group by `(agent_id, provider, repoId, workflowDefinitionId,
     billing_mode→CostBasis)` → `AgentDispatches`.
  4. `LLM.CALL.SUCCESS` events → parse `Data` (reuse the `AggregateLlmUsage` JSON shape) → group by
     the dimension tuple → `TokensIn/Out`, `CostUsd`. `ProviderDiagnostic` rows in-window contribute
     the diagnostic-sourced measures + dims (`ProviderKey`, `AgentType`, `ProjectId`, `BillingMode`).
  5. For each tuple compute `PlatformBilledUsd` (Task 2 seam; `platform` only).
  6. Read-then-upsert each tuple on the full business key into `tenantDb.AnalyticsUsageHourly`;
     advance the checkpoint to the max `SequenceNumber` seen; one `SaveChanges`.
  7. Emit `ANALYTICS.ROLLUP.TENANT_DIMENSIONAL_COMPLETED`.
- **Approach:** **overwrite** measures on re-run (whole-bucket recompute) so replay/backfill are
  idempotent; the checkpoint is a skip-optimisation, not the idempotency mechanism. Accept `hour` +
  `resetCheckpoint` inputs for backfill.

### Task 5 — daily compaction + usage-hourly purge (AC 10, 11)

- **Test first:** `CompactDailyAnalyticsActivityTests` — 24 hourly buckets → one daily set; measures
  sum losslessly; re-compaction idempotent. Purge test (Postgres) — rows older than cutoff deleted,
  best-effort on failure.
- **Implement:**
  - `CompactDailyAnalyticsActivity` — `GROUP BY date_trunc('day', Hour), <all dims>` → upsert on the
    daily business key; runs when target hour is `00:xx` (compacts the day that just ended); emits
    `ANALYTICS.COMPACT.DAILY`.
  - `PurgeStaleUsageAnalyticsActivity` — copy `PurgeStaleAnalyticsActivity`, target
    `tenantDb.AnalyticsUsageHourly`, default 13-month window, best-effort, emits
    `ANALYTICS.PURGE.USAGE_HOURLY`/`…FAILED`.

### Task 6 — fan-out + workflow wiring + lag SLO (AC 12, 13, 14)

- **Test first:** extend `HourlyAnalyticsRollupWorkflowStructureTests` — assert the dimensional
  fan-out + compaction + usage-purge steps exist and the platform fan-out precedes the dimensional
  one; a lag-over-budget test emits `ANALYTICS.ROLLUP.DIMENSIONAL_LAG` + gauge.
- **Implement:**
  - `FanOutTenantDimensionalRollupsActivity` — mirror `FanOutTenantRollupsActivity` (same tenant
    target set, serial loop, per-tenant `try/catch` → `…_FAILED` + continue, success/failed
    outputs, `ComputeOneOverride` test seam). (Alternative considered: extend the existing fan-out
    to call both compute helpers per tenant — keeps one tenant iteration; decide in implementation
    based on the journal/coupling tradeoff and pin the choice in the structure test.)
  - Wire into `HourlyAnalyticsRollupWorkflow.Build` after `fanOut`: `fanOutDimensional →
    compactDaily(day-boundary) → emitCompleted → purgeStale → purgeUsageStale`.
  - Record projection lag (`now − hour`) per pass; emit `ANALYTICS.ROLLUP.DIMENSIONAL_LAG` + the
    `tamma.analytics.projection_lag_seconds` OTel gauge (KekRotationMetrics precedent) when over
    the SLO budget (default 2h, config-driven).

### Task 7 — isolation integration suite + full-suite gate (AC 8, 15)

- **Test first/alongside:** `tests/Tamma.Api.Tests/Analytics/DimensionalRollupIsolationTests.cs`
  (Postgres 17 Testcontainer, per `SchemaPerTenantMigrationTests`):
  1. project into two `t_<hex>` schemas; a row in A's `analytics_usage_hourly` is invisible in B.
  2. a forced failure on tenant A leaves tenant B's projection intact + fan-out completes.
  3. concurrent re-run of the same tuple collapses on `UX_*_dims` (NULLS NOT DISTINCT).
  4. checkpoint resume re-folds only un-checkpointed events with no double-count.
- **Run:** `sg docker -c "dotnet test apps/tamma-elsa/Tamma.sln"` — full green; confirm the
  checkpoint migration applies + rolls back and `has-pending-model-changes -c TenantDbContext`
  stays clean (CI gate).

---

## Sequencing & dependencies

Task 1 (events + cost-basis) → Task 2 (margin seam) → Task 3 (checkpoint entity/migration) →
Task 4 (dimensional compute, depends on 1-3) → Task 5 (compaction + purge) → Task 6 (fan-out +
workflow + lag) → Task 7 (isolation suite + full gate). Tasks 1-2 are leaf helpers; Task 3 is an
independent additive migration; Task 4 is the core and depends on all three; Tasks 5-6 build on
Task 4's compute; Task 7 proves the whole pipeline. Hard prereqs are already merged (28-10, Epic 4,
Epic 28) plus the drafted 36-1 schema; soft deps (35-2 `billing_mode`, Epic 32 `agent_id`, 36-7
margin) degrade gracefully and are pinned by their default-path tests.

## Risks + mitigations

- **Double-counting on re-run/backfill.** *Mitigation:* whole-bucket overwrite (recompute the
  entire `(tenant, hour)` from source each pass) makes the upsert idempotent regardless of the
  checkpoint; the checkpoint is a skip-optimisation only. A replay test asserts identical
  rows/measures after a second run.
- **`SequenceNumber` cursor vs `CreatedAt`.** Using `CreatedAt` would skip/double same-millisecond
  events. *Mitigation:* cursor strictly on `DomainEvent.SequenceNumber` (the `BIGSERIAL` total
  order); the resume test exercises a same-millisecond batch.
- **`NULL` dimension buckets breaking the unique key / reconciliation.** *Mitigation:* rely on
  36-1's `UX_*_dims` NULLS NOT DISTINCT key; a bucketing test asserts `Σ(per-dim rows) == grand
  total`. The Postgres collision test (Task 7) proves the NULL dedupe (InMemory can't).
- **Cost-basis source not yet shipped (35-2).** *Mitigation:* resolve from the `billing_mode`
  tag/column with a documented `CostBasis.Platform` default; a classification test pins the default
  so the rollup is correct pre-35-2 and flips automatically once tags appear.
- **36-7 margin not yet shipped.** *Mitigation:* `IAnalyticsPricingConfig` seam +
  `NullAnalyticsPricingConfig` zero-margin fallback + WARN; never hardcode a margin.
- **Per-tenant failure cascading.** *Mitigation:* copy the 28-10 fan-out tolerance — per-tenant
  `try/catch` → `…_FAILED` + continue; isolation test proves A's failure leaves B intact.
- **Two schedulers racing the same tenants.** *Mitigation:* extend the single 28-10 workflow (one
  schedule, one advisory lock, one target hour); the structure test pins step ordering.
- **`has-pending-model-changes` drift on the checkpoint migration.** *Mitigation:* run the gate
  after `migrations add`; regenerate the snapshot if it drifts; CI enforces it.
- **Memory/cost of pulling LLM `Data` blobs.** *Mitigation:* same bounded hour-window read as
  28-10's `ComputeTenantRollupActivity` (≤ tens of thousands/tenant/hour); aggregate in memory with
  the shared helper; if a tenant proves pathological, the per-tenant failure tolerance contains it.
- **Docker-group staleness in the test session.** *Mitigation:* run Testcontainer suites via
  `sg docker -c "dotnet test ..."` (per project memory `reference_dotnet_test_docker`).

## Acceptance criteria (mirror of the story)

- [ ] `ComputeTenantDimensionalRollupActivity` projects one tenant's hour of `domain_events` +
      `ProviderDiagnostic` into one `analytics_usage_hourly` row per `(Provider, AgentId,
      WorkflowDefinitionId, RepoId, CostBasis)` tuple with summed measures; static `ComputeAsync`
      pure-DI entry point.
- [ ] Provider/agent/workflow/repo dimensions read from the DCB tags / diagnostic columns; absent
      dimension → `NULL` bucket; per-dim rows reconcile to the grand total.
- [ ] Agent dimension from the `agent_id` tag (Epic 32) / `ProviderDiagnostic.AgentType`; no agent
      → `AgentId = NULL`.
- [ ] `CostBasis` resolved per record from the 35-2 `billing_mode` tag/`ProviderDiagnostic.BillingMode`
      (default `platform`); `PlatformBilledUsd = CostUsd*(1+margin)` for `platform` (36-7 seam),
      `0` for `byok`.
- [ ] Idempotent: re-running an hour upserts on the full dimension business key (whole-bucket
      overwrite) — replay test shows unchanged rows/measures.
- [ ] Resumable: per-tenant `analytics_projection_checkpoint` on `DomainEvent.SequenceNumber`;
      crash-resume re-folds only un-checkpointed events with no double-count.
- [ ] Per-tenant isolation: read+write through the tenant's own schema; A's failure leaves B
      intact (Postgres isolation test).
- [ ] Backfill: explicit historical `hour` (+ `resetCheckpoint`) re-projects through the same
      idempotent path.
- [ ] `CompactDailyAnalyticsActivity` rolls hourly → daily losslessly (idempotent upsert on the
      daily business key); usage-hourly retention purge mirrors `PurgeStaleAnalyticsActivity`
      (best-effort, 13-month default, runs last).
- [ ] `ANALYTICS.ROLLUP.TENANT_DIMENSIONAL_COMPLETED/FAILED` per tenant×hour; one tenant's failure
      doesn't abort the fan-out; compaction/purge/lag events emitted.
- [ ] Projection-lag SLO observable via `ANALYTICS.ROLLUP.DIMENSIONAL_LAG` + the
      `tamma.analytics.projection_lag_seconds` OTel gauge past the budget.
- [ ] Wired into `HourlyAnalyticsRollupWorkflow` after the platform fan-out; 28-10 platform rollup
      + CP purge unchanged (structure test).
- [ ] No fact-table schema change, no query/export endpoint, no dashboard, no CP-table change.
- [ ] Full test suite green; checkpoint migration applies + rolls back; `has-pending-model-changes`
      clean.
