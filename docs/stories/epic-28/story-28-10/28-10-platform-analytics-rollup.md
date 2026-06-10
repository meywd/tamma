# Story 28.10: `platform_analytics_hourly` Rollup Workflow

**Epic**: Epic 28 - Database-per-Tenant Isolation
**Category**: Operations
**Priority**: Medium (feature enables platform-admin dashboards —
Story 28-11 blocks on this rollup existing — and carries the
orchestrator scale benchmark per Epic 28 README cross-doc resolution
#3; no runtime tenant traffic depends on this story, so it can slip
a release cycle if needed without breaking provisioning)
**Estimated Effort**: L (28h)
**Status**: DONE (2026-06-05) — all ACs satisfied; the two below are accepted deferred-by-design decisions, not open work. See audit `docs/superpowers/plans/2026-05-29-epic-28-status-audit.md`. **2026-05-31 decisions (deferred-by-design):** (1) **Metric model — wide-row accepted.** The shipped `platform_analytics_hourly` is a WIDE-ROW fact table (one row per tenant per hour, fixed metric columns) covering the subset of metrics actually surfaced today (workflows / llm-cost / api-requests / errors). The spec's long-narrow `MetricKey/Tags` table was NOT built; the wide-row model is accepted as the current design and this AC is reworded to match. Adding a new metric requires a column + migration (acceptable at current scale). A future long-narrow migration is a separate story if metric cardinality grows. (2) **1k/5k/10k idle-orchestrator benchmark — deferred to the Story 30 production-scale gate.** It only matters before crossing ~500 production tenants; Tamma has zero today. Recorded as a known deferred scale risk, not a 28-10 blocker. **2026-06-05:** 13-month retention sweeper (`PURGE_ANALYTICS_HOURLY`) shipped — `PurgeStaleAnalyticsActivity` (best-effort, set-based `ExecuteDeleteAsync` of `platform_analytics_hourly` rows older than 13 months) runs as the final step of `HourlyAnalyticsRollupWorkflow`, reusing the existing hourly schedule + advisory lock (no second scheduler). No remaining residuals.

## User Story

As a **platform operations engineer**, I want **an hourly Elsa
workflow on global Elsa that rolls `platform_events` plus per-tenant
`domain_events` into a `platform_analytics_hourly` fact table, running
per-tenant with best-effort isolation and publishing the rollup
results to the admin dashboard**, so that **the "all tenants" admin
view answers "how many issues did tenant X process this week" with
one CP query instead of a fan-out across tenant DBs, and the platform
operations team has the data to make scaling decisions about
orchestrator-instance count** (this story also runs the 1k / 5k / 10k
idle-instance benchmark required by Epic 28 README conflict resolution
#3 before we cross 500 production tenants).

## Acceptance Criteria

### AC1: `platform_analytics_hourly` fact table

- [ ] Table shipped in this story's migration
      `04x_platform_analytics_hourly.cs` (reserve number coordinated
      with 28-11 per `00-sequencing.md` "Safe with caveats"):

      ```sql
      CREATE TABLE platform_analytics_hourly (
        HourBucket  TIMESTAMPTZ NOT NULL,   -- truncated to hour, UTC
        TenantId    UUID NULL,              -- NULL = platform-wide
        MetricKey   TEXT NOT NULL,          -- see AC2 keys
        Tags        JSONB NOT NULL DEFAULT '{}'::jsonb,
        Value       NUMERIC(20,4) NOT NULL, -- generic numeric slot
        UpdatedAt   TIMESTAMPTZ NOT NULL DEFAULT now(),
        PRIMARY KEY (HourBucket, TenantId, MetricKey, Tags)
      );
      CREATE INDEX ix_pah_bucket_metric
        ON platform_analytics_hourly (HourBucket DESC, MetricKey);
      CREATE INDEX ix_pah_tenant_bucket
        ON platform_analytics_hourly (TenantId, HourBucket DESC)
        WHERE TenantId IS NOT NULL;
      ```

- [ ] `PRIMARY KEY` is the composite `(HourBucket, TenantId,
      MetricKey, Tags)` — a row identity is a unique
      `(hour, tenant?, metric, tag-combo)` tuple. Replaying the
      workflow for the same hour must not duplicate rows (see AC5
      idempotency via `ON CONFLICT DO UPDATE`).
- [ ] `NUMERIC(20,4)` holds both counter sums (integer-ish) and cost
      accumulations (four-decimal precision to handle
      micro-cent LLM pricing). Tags column holds
      breakout dimensions like `{ "provider": "anthropic",
      "model": "claude-opus-4.7" }` so a single `MetricKey` of
      `llm.tokens.input` can have multiple rows per bucket.
- [ ] 13-month retention enforced by a **separate** scheduled task
      `PURGE_ANALYTICS_HOURLY` (AC6) writing a row to
      `platform_queued_tasks` (Story 28-6) every week at Sunday
      02:00 UTC.

### AC2: Per-tenant metric set

Every hour, for every tenant with `tenants.Status='active'`, compute
the following metrics from the tenant DB's `domain_events` for the
previous hour (hour `H-1` at time `H:05`):

- [ ] `issues.created` — COUNT events of type `ISSUE.ASSIGNED.SUCCESS`
      in the bucket.
- [ ] `workflows.executed{workflowName}` — COUNT events of type
      `WORKFLOW.COMPLETED.SUCCESS`, tag-broken-out by
      `tags.workflowName`.
- [ ] `workflows.failed{workflowName}` — COUNT of
      `WORKFLOW.COMPLETED.FAILED`.
- [ ] `llm.tokens.input{provider,model}` — SUM of
      `LLM.CALL.SUCCESS.data.inputTokens`.
- [ ] `llm.tokens.output{provider,model}` — SUM of
      `data.outputTokens`.
- [ ] `llm.cost_usd{provider,model}` — SUM of `data.costUsd`,
      rounded to 4 decimals.
- [ ] `api.requests{endpoint_class}` — COUNT of
      `API.REQUEST.COMPLETED` tagged by the endpoint class (pulled
      from `tags.endpointClass`, a coarse bucket like `/issues`,
      `/workflows`, not the full path).
- [ ] `api.errors_5xx{endpoint_class}` — COUNT of `API.REQUEST.FAILED`
      with `tags.status >= 500`.

All per-tenant metrics write rows with `TenantId=<tid>`.

### AC3: Platform-wide metric set

Every hour, compute from `platform_events` (Story 28-6):

- [ ] `tenants.provisioned.success` — COUNT of
      `TENANT.PROVISIONED.SUCCESS` in the bucket.
- [ ] `tenants.provisioned.failed{failure_reason}` — COUNT of
      `TENANT.PROVISION.FAILED`, tag-broken-out by
      `data.failureReason` (`clean` vs `partial`).
- [ ] `tenants.deleted` — COUNT of `TENANT.DELETED.SUCCESS`.
- [ ] `tenants.active{plan}` — gauge value written from `SELECT
      COUNT(*) FROM tenants WHERE Status='active'` at the end of
      the bucket, tag-broken-out by `tenants.Plan`.
- [ ] `auth.logins.success` — COUNT of `USER.LOGIN.SUCCESS`.
- [ ] `auth.logins.failed{reason}` — COUNT of
      `USER.LOGIN.FAILED` tag-broken-out by `data.reason`.

All platform-wide metrics write rows with `TenantId=NULL`.

### AC4: `HourlyRollupWorkflow` on global Elsa, cron at minute 5

- [ ] Workflow at
      `apps/tamma-elsa/src/Tamma.ElsaServer.Global/Workflows/PlatformAnalyticsRollupWorkflow.cs`,
      registered on the global-Elsa host only (not on tenant
      Elsa — per Doc 02 §3 workflow placement).
- [ ] Cron trigger `0 5 * * * *` (every hour at minute 5) — the
      5-minute offset absorbs late-arriving `platform_events`
      writes from long-running workflows. Timezone: UTC.
- [ ] Input: the target hour bucket (defaults to the previous hour
      from `now()`, clamped to the top of the hour).
- [ ] Steps:
  1. **ListActiveTenants** — query CP `tenants WHERE Status='active'
     OR (Status='deleted' AND DeletedAt > <bucket-start>)`. The
     second predicate catches tenants deleted during the bucket
     whose events still need rolling.
  2. **RollupPlatformWide** — aggregate `platform_events` per AC3.
     Writes with `TenantId=NULL`.
  3. **RollupPerTenant** — fan-out one Elsa sub-workflow instance
     per tenant from step 1. Each sub-workflow opens a short-lived
     tenant connection via `ITenantConnectionResolver` and
     aggregates `domain_events` per AC2. Max 10 concurrent
     sub-workflows (Elsa `Parallel` activity with
     `ParallelismDegree=10`) to avoid saturating the pool cache.
  4. **AggregateResults** — collect sub-workflow outcomes; count
     successes and failures.
  5. **EmitCompletionEvent** — emit
     `PLATFORM_ANALYTICS.ROLLUP_COMPLETED` to `platform_events`
     with `{ data: { bucket, tenantsSuccess, tenantsFailed,
     durationMs } }`.

### AC5: Idempotency + partial-failure tolerance

- [ ] Every write uses `INSERT ... ON CONFLICT (HourBucket,
      TenantId, MetricKey, Tags) DO UPDATE SET Value=EXCLUDED.Value,
      UpdatedAt=now()`. Replays for the same bucket overwrite, not
      duplicate.
- [ ] A per-tenant sub-workflow failure does NOT fail the parent
      workflow. The parent tolerates up to `(1 - 0.95) × N` failures
      (i.e. must succeed for 95% of tenants). Below threshold →
      emit `PLATFORM_ANALYTICS.ROLLUP_DEGRADED` with the failed
      tenant ids in `data.failedTenants`, continue.
- [ ] At or above threshold failure → emit
      `PLATFORM_ANALYTICS.ROLLUP_FAILED`, retry the whole workflow
      30 minutes later (one retry budget per bucket). Second failure
      pages ops.
- [ ] Per-tenant failure modes caught and classified:
  - Tenant DB unreachable → log WARN, skip tenant, add to
    `failedTenants`.
  - Pool eviction mid-query → retry once in-sub-workflow, then
    skip.
  - Tenant `Status` flipped to `deleted` mid-workflow → skip
    gracefully (not a failure).

### AC6: 13-month retention + purge task

> **Implementation note (2026-06-05):** shipped as `PurgeStaleAnalyticsActivity`
> appended as the final step of `HourlyAnalyticsRollupWorkflow` rather than a
> separate weekly `WeeklyMaintenanceWorkflow`. Rationale: riding the existing
> hourly schedule + advisory lock means no second scheduler to operate, and
> the per-hour delete is cheap (after the first sweep only the single bucket
> that just crossed the boundary qualifies). The delete is a single set-based
> EF `ExecuteDeleteAsync` (`WHERE Hour < now - 13 months`) — Postgres handles
> it without the manual 10000-row batching the spec described. The sweep is
> best-effort (never throws) so a transient CP hiccup cannot fail the rollup;
> it emits `ANALYTICS.PURGE.HOURLY` (rows deleted + cutoff) on success and
> `ANALYTICS.PURGE.FAILED` otherwise.

- [ ] `PURGE_ANALYTICS_HOURLY` task enqueued into
      `platform_queued_tasks` by a separate `WeeklyMaintenanceWorkflow`
      (cron `0 0 2 ? * SUN *` Sunday 02:00 UTC). The task body is
      `DELETE FROM platform_analytics_hourly WHERE HourBucket <
      NOW() - INTERVAL '13 months'` — batched at 10000 rows per
      statement to avoid long locks.
- [ ] Purge metrics: `tamma_analytics_rows_purged_total` counter,
      `tamma_analytics_oldest_bucket_age_days` gauge.
- [ ] Alert if `oldest_bucket_age_days > 400` for > 7 days — purge
      task is stuck.

### AC7: Orchestrator-scale benchmark (Epic 28 README resolution #3)

This story **owns** the benchmark task that Epic 28 README conflict
resolution #3 requires before we cross 500 production tenants:

- [ ] Benchmark rig: a dedicated integration-test profile that
      seeds a global-Elsa instance with N idle `OrchestratorWorkflow`
      instances (bookmarks set, no activity), at `N ∈ {1000, 5000,
      10000}`. Uses Testcontainers to stand up Postgres + RabbitMQ +
      a real Elsa global-host container.
- [ ] For each N, measure over a 10-minute observation window:
  - **Bookmark-scan latency p95** (Elsa metric
    `elsa_bookmark_scan_duration_ms` per Doc 02 §10.3). Threshold
    from README: **< 500ms**.
  - **Instance RAM** — `container_memory_rss` for the global-Elsa
    process. Threshold from README: **< 2 GB at N=5000**.
  - **Postgres pool usage** — `pg_stat_activity` count connected
    as the Elsa user. Threshold: must stay under `max_connections
    - 20` (reserve for admin).
- [ ] Output: a benchmark report at
      `docs/stories/epic-28/story-28-10/benchmark-report.md` with
      the three metrics per N, pass/fail per threshold, and a
      recommendation line. If any N trips a threshold, open a
      follow-up ticket to split the orchestrator into a tenant-fanout
      singleton (per README resolution #3 last sentence) before the
      platform reaches 500 production tenants.
- [ ] Benchmark runs in CI **nightly**, not on every PR (too
      expensive). Results published to Grafana alongside existing
      dashboards.

## Technical Context

- **Design docs**:
  - `plans/db-per-tenant/01-control-plane-split.md` §5 (two-tier
    analytics: `platform_events` in CP + `domain_events` per
    tenant), §5.3 (aggregation job is nightly — **this story
    upgrades to hourly** per the finer-grained demand from
    Story 28-11's dashboards; nightly was a starting guess).
  - `plans/db-per-tenant/02-elsa-two-tier.md` §3 (global-Elsa
    hosts platform crons), §10.4 (control-plane rollup — different
    scope: Elsa health; this story is domain rollup), §12
    open-decision #2 (orchestrator lifetime at scale — this story
    executes the benchmark that resolves that open decision).
  - Epic 28 README conflict resolution #3 — **this story executes
    the benchmark**.
- **File layout**:
  - `apps/tamma-elsa/src/Tamma.ElsaServer.Global/Workflows/PlatformAnalyticsRollupWorkflow.cs`
    — new parent workflow.
  - `apps/tamma-elsa/src/Tamma.ElsaServer.Global/Workflows/TenantRollupSubWorkflow.cs`
    — new sub-workflow fanned out per tenant.
  - `apps/tamma-elsa/src/Tamma.Activities/Analytics/AggregatePlatformEventsActivity.cs`
    — new, does the `platform_events` SUM/COUNT aggregation.
  - `apps/tamma-elsa/src/Tamma.Activities/Analytics/AggregateTenantEventsActivity.cs`
    — new, does the per-tenant `domain_events` aggregation.
  - `apps/tamma-elsa/src/Tamma.Activities/Analytics/UpsertAnalyticsRowActivity.cs`
    — new, batched `INSERT ... ON CONFLICT DO UPDATE`.
  - `apps/tamma-elsa/src/Tamma.Data/Entities/PlatformAnalyticsHourly.cs`
    — new CP entity.
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/04x_platform_analytics_hourly.cs`
    — new migration.
  - `apps/tamma-elsa/tests/Tamma.ElsaServer.Global.Benchmarks/OrchestratorScaleBenchmark.cs`
    — new benchmark rig for AC7.
  - `docs/stories/epic-28/story-28-10/benchmark-report.md` — new,
    published as part of the story's deliverables.
- **Interaction with Story 28-11**: the admin dashboard's
  "all-tenants" view (Story 28-11 AC3) reads
  `platform_analytics_hourly` directly via the CP API. This story
  ships the table and the workflow; the dashboard UI lives in
  28-11. A shared DTO `AnalyticsBucketDto` at
  `apps/tamma-elsa/src/Tamma.Api/Dtos/Analytics/AnalyticsBucketDto.cs`
  is defined here and consumed there.

## Dependencies

- **Blocks**: 28-11 (admin UX reads the rollup table).
- **Blocked by**: 28-5 (workflow state machine emits the
  `TENANT.PROVISIONED.SUCCESS`, `TENANT.DELETED.SUCCESS` events
  this story aggregates), 28-6 (`platform_events` table and
  `platform_queued_tasks` table — the purge task queues here).
- **External**: global-Elsa host container, RabbitMQ (for Elsa's
  internal bookmark-delivery mechanism), the benchmark rig's
  Testcontainers instances.

## Test Plan

### Unit tests

- `AggregatePlatformEventsActivityTests` — table-driven across
  event shapes, assert the SUM/COUNT output for one bucket on a
  seeded in-memory event stream.
- `AggregateTenantEventsActivityTests` — same for tenant events,
  including tag-breakout correctness (two rows for two distinct
  `{provider, model}` combos).
- `UpsertAnalyticsRowActivityTests` — happy path insert, then
  re-run with modified `Value` → asserts update-not-duplicate via
  `ON CONFLICT DO UPDATE`.
- `PurgeAnalyticsHourlyActivityTests` — seed rows at various ages,
  run purge, assert deletion of rows older than 13 months in
  10000-row batches.

### Integration tests (Testcontainers.PostgreSQL + RabbitMQ + Elsa)

- **T1 Single-tenant happy path**: seed one tenant with 1h of
  tenant-DB events → run the workflow → assert per-tenant rows
  exist in `platform_analytics_hourly` with correct values.
- **T2 Platform-wide happy path**: seed CP with `TENANT.PROVISIONED
  .SUCCESS` × 5 in the bucket → workflow runs → row
  `(<bucket>, NULL, 'tenants.provisioned.success', {})` has
  `Value=5`.
- **T3 Idempotency**: run the workflow twice for the same bucket →
  row count identical → values unchanged.
- **T4 Partial failure tolerance**: seed 20 tenants, make 1 DB
  unreachable (`iptables -A` in the container) → workflow
  completes, 19 tenant rollups succeed, 1 reported in
  `ROLLUP_COMPLETED.data.tenantsFailed`, workflow status is
  `Completed` (not `Faulted`).
- **T5 Above-threshold failure**: make 2 of 20 tenants unreachable
  (10% — above the 5% threshold) → workflow emits
  `ROLLUP_DEGRADED`, status still `Completed`. Make 5 of 20
  unreachable (25%) → workflow emits `ROLLUP_FAILED`, retried 30
  min later per Elsa retry config.
- **T6 Tenant deleted mid-workflow**: flip a tenant to `deleted`
  between steps 1 and 3 → the sub-workflow catches the eviction,
  skips the tenant gracefully, neither success nor failure count.
- **T7 Purge task**: seed `platform_analytics_hourly` with rows
  spanning 15 months → run `PurgeAnalyticsHourlyActivity` → rows
  older than 13 months are gone; count matches the 10000-batch
  boundary.
- **T8 Dashboard DTO contract**: Story 28-11's dashboard query
  (once land) consumes `AnalyticsBucketDto` with the contract
  defined in this story. Cross-story integration test asserts
  the dashboard endpoint's response shape matches the DTO.

### Benchmark (AC7)

- Run `dotnet test --filter Category=Benchmark` against the
  benchmark rig with N=1000, 5000, 10000. Assert all thresholds
  per AC7 on a CI runner matching the production Hetzner CPX42
  instance class. Report attached to the story file.

### Manual verification

- Local dev: run the workflow manually via Elsa Studio "Run" →
  observe rows land in `platform_analytics_hourly`. Query via
  `psql` to confirm shape.
- Trigger the purge manually → confirm the oldest bucket age
  gauge drops.

## Definition of Done

- [ ] AC all green
- [ ] Unit + integration tests added, suite passes
- [ ] Benchmark report produced per AC7 and committed to
      `docs/stories/epic-28/story-28-10/benchmark-report.md`
- [ ] No new CodeQL alerts (pay attention to SQL-injection in the
      dynamic MetricKey / Tags JSONB path — all user-influenced
      inputs must go through parameterised queries)
- [ ] Design-doc references updated if the impl deviated (expected:
      update `01-control-plane-split.md` §5.3 to note rollup is
      hourly, not nightly)
- [ ] Reviewed by a second engineer (cross-stream)

## Risks / Open Questions

- **Sub-workflow fan-out at 500+ tenants.** `ParallelismDegree=10`
  means a 500-tenant rollup takes ~50 serial batches. If per-tenant
  aggregation averages 500ms, that's 25s — well inside the hour
  budget. At 10k tenants the math is 500s = 8 minutes — still
  fine, but monitor `tamma_analytics_rollup_duration_ms` as a
  leading indicator before scale trips it.
- **Benchmark flakiness on Testcontainers.** Idle-instance memory
  is sensitive to Elsa's internal bookkeeping intervals. Smooth
  flakiness by taking 3 samples per N and reporting p95, not
  single runs. Document the sampling method in
  `benchmark-report.md`.
- **13-month retention may conflict with GDPR erasure.** The per-
  tenant rows include `TenantId` and are rolled up from
  `domain_events`. A future right-to-be-forgotten request for a
  tenant should delete all `platform_analytics_hourly WHERE
  TenantId=<id>` as part of the tenant-erase workflow. Story 28-5's
  `DeleteTenantWorkflow` step I handles `platform_queued_tasks`
  but not this table — add a follow-up to extend that workflow
  (or accept the aggregate-only rollup as not-PII per Doc 01 §10.3
  and document the legal position).
