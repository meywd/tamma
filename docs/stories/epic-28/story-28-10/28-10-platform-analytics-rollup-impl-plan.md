# Story 28-10 Implementation Plan — `platform_analytics_hourly` Rollup

**Status**: Planned (2026-04-20)
**Story brief**: [`28-10-platform-analytics-rollup.md`](./28-10-platform-analytics-rollup.md)
**Epic 28 phase**: Ops stream (parallel with Phase D)
**Branch**: `feat/story-28-10-platform-analytics-rollup`

---

## 1. Objective

Ship an hourly global-Elsa workflow that rolls `platform_events` +
per-tenant `domain_events` into `platform_analytics_hourly`. This
fact table answers cross-tenant admin questions ("how many issues
did tenant X process this week") with a single CP query instead of
fanning out to tenant DBs. Also runs the 1k/5k/10k idle-instance
orchestrator benchmark required by Epic 28 cross-doc resolution #3.

## 2. Dependencies

Hard blockers:

- **Story 28-1** — `platform_events` table (Story 28-6 confirms).
- **Story 28-6** — `platform_events` repository + `platform_queued_tasks`
  (the rollup enqueues a task per tenant).

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Entities/PlatformAnalyticsHourly.cs` | Entity. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/28_10_platform_analytics_hourly.cs` | Migration + indexes. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer.Global/Workflows/AnalyticsRollupWorkflow.cs` | Hourly cron workflow. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer.Global/Activities/Analytics/ComputeTenantRollupActivity.cs` | Per-tenant fan-out activity. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AnalyticsEndpoints.cs` | `GET /api/v1/admin/analytics?metric=&since=`. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.ElsaServer.Tests/AnalyticsRollupWorkflowTests.cs` | Happy + fan-out + idempotency. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Benchmarks/OrchestratorIdleTenantBench.cs` | 1k/5k/10k benchmark harness. |
| `/home/meywd/tamma/docs/runbooks/platform-analytics.md` | Metric catalogue + SQL recipes. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Add `DbSet<PlatformAnalyticsHourly>`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer.Global/Program.cs` | Schedule workflow via Elsa's cron trigger every hour at :05. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs` (when 28-11 lands) | Surface analytics in the tenant detail view. |

## 5. Sequence of changes

### Step 1 — Schema + entity (2h)

- Migration: `platform_analytics_hourly` with indexes
  `(HourBucket, MetricKey)`, `(TenantId, HourBucket DESC)`.
- Entity + fluent config.
- **Commit**: `feat(db): platform_analytics_hourly schema`.

### Step 2 — Metric catalogue (3h)

- `MetricKey` constants: `workflow_runs`, `llm_token_usage`,
  `llm_cost_cents`, `api_requests`, `active_users`, `new_tenants`,
  `failed_workflow_runs`, `provider_health_open_time_seconds`, etc.
- `metric-catalogue.md` with formulas.
- **Commit**: `docs(analytics): metric catalogue`.

### Step 3 — ComputeTenantRollupActivity (6h)

- For one tenant + one hour bucket:
  1. Query `domain_events` (tenant DB) for the bucket; aggregate.
  2. Query `platform_events` (CP) for the bucket; aggregate.
  3. Upsert into `platform_analytics_hourly`
     `(HourBucket, TenantId, MetricKey) UNIQUE`.
- Idempotent: re-running a bucket replaces its rows.
- **Commit**: `feat(analytics): compute tenant rollup activity`.

### Step 4 — Master workflow (4h)

- `AnalyticsRollupWorkflow` fires at :05 of each hour.
- Enumerates `tenants` where `Status='active'`.
- Enqueues one `platform_queued_tasks` row per tenant × hour.
- Workers (28-6 dispatcher) execute `ComputeTenantRollupActivity` in
  parallel with concurrency limit.
- Emits `ANALYTICS.HOUR.COMPLETED` at the end.
- **Commit**: `feat(analytics): hourly rollup workflow`.

### Step 5 — Admin endpoint (2h)

- `GET /api/v1/admin/analytics?metric=&since=&until=&tenantId=`
  returns series from `platform_analytics_hourly`.
- RBAC: platform admin only.
- **Commit**: `feat(api): platform analytics endpoint`.

### Step 6 — Idle-instance benchmark (5h)

- `OrchestratorIdleTenantBench`:
  - Provision 1k, 5k, 10k tenants via `CreateTenantWorkflow` (or
    fixture-loaded seed data).
  - Measure: CPU, RAM, connection count, API p95.
  - Results logged to `benchmark-results-28-10.md`.
- Runs on-demand, not in CI (too expensive).
- **Commit**: `bench(orchestrator): 1k/5k/10k idle-tenant harness`.

### Step 7 — Runbook (2h)

- `platform-analytics.md`: retention (keep 13 months),
  reprocessing (re-enqueue task for a bucket), troubleshooting
  missed buckets.
- **Commit**: `docs(runbooks): platform analytics operations`.

## 6. Test strategy

### Unit

- Metric aggregation functions per metric.
- Upsert idempotency.

### Integration

- Seed 3 tenants with events; run rollup; assert rows.
- Re-run same bucket; assert no duplicates.

### Benchmark

- On-demand suite results captured as an artifact in
  `.dev/findings/`.

## 7. Rollback plan

- **Schedule off**: disable the Elsa cron trigger; rollup stops. CP
  data retained.
- **Schema revert**: drop `platform_analytics_hourly` (no downstream
  dependency outside the admin endpoint; hide endpoint behind a flag
  if we want to keep it alive during rollback).

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Schema | 2 |
| 2. Metric catalogue | 3 |
| 3. Per-tenant activity | 6 |
| 4. Master workflow | 4 |
| 5. Admin endpoint | 2 |
| 6. Idle bench | 5 |
| 7. Runbook | 2 |
| **Total** | **24** (brief 28; plan comes under because activities
reuse 28-6 queue + dispatcher). |

## 9. Open questions

- **Fan-out concurrency**: how many parallel tenants? Plan: 8 at
  once (matches 28-6 dispatcher default). Tune via config.
- **Late-arriving events**: domain events written after the bucket
  rolled up won't be counted. Mitigation: rollup runs at :05 for
  the prior hour (5-min lag tolerance).
- **Retention strategy**: 13 months (covers YoY comparisons).
  Beyond that, summarise to monthly buckets. Tracked as a later
  story.
- **Benchmark reproducibility**: CI can't run 10k tenant
  provisioning in a reasonable window. Plan: benchmark runs
  manually on a dedicated Hetzner node, results committed to
  `.dev/findings/`.
- **How does the benchmark discover the orchestrator's idle
  footprint?** Tag the process via `ApplicationName` (28-4) so
  `pg_stat_activity` and Prometheus can isolate it.
