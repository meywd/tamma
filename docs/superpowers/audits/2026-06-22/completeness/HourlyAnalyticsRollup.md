# Completeness Audit — `HourlyAnalyticsRollupWorkflow`

**Date:** 2026-06-22
**Auditor:** automated completeness assessment (read-only)
**Workflow:** `HourlyAnalyticsRollupWorkflow`
**File:** `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupWorkflow.cs`

---

## Purpose & Owner

Hourly Elsa workflow on the **global-Elsa host** that rolls `platform_events` (control-plane) plus
each active tenant's `domain_events` into the control-plane `platform_analytics_hourly` fact table,
so the platform-owner "all tenants" admin view answers fleet-wide questions ("how many workflows did
the platform run this week") with a single CP query instead of fanning out across tenant DBs.

- **Owning story:** Story 28-10 (`docs/stories/epic-28/story-28-10/28-10-platform-analytics-rollup.md`)
  — marked **DONE (2026-06-05)**, with two explicit accepted deferrals.
- **Extending epic:** Epic 36 — Analytics & Reporting Platform
  (`docs/stories/epic-36/README.md`). Story 36-2 (`docs/stories/epic-36/story-36-2/36-2-dcb-to-analytics-projection-pipeline.md`)
  designates **this exact workflow** as the host for the dimensional projection fan-out
  ("an additional fan-out step ON the existing HourlyAnalyticsRollupWorkflow").

---

## Maturity: **partial**

This is **not** a thin happy-path skeleton like the `PullRequest` example. The workflow as it stands
for Story 28-10 is a robust, production-grade flow: 5 sequenced steps, real EF aggregation, idempotent
read-then-upsert, per-tenant failure isolation (28-10 AC5 tolerance shape), best-effort retention
purge, DCB audit events on every milestone, a leader-elected multi-pod-safe scheduler, backfill input,
and a pure-DI compute seam on each activity for tests/admin reruns.

It rates **partial** (not **complete**) for two reasons:

1. **Story 28-10's own AC5 partial-failure *signalling*** (the DEGRADED / above-threshold FAILED +
   30-min retry escalation) was **not built** — the fan-out counts failures but never classifies the
   bucket against the 95% threshold and never escalates. (The wide-row metric reduction of AC2/AC3,
   by contrast, *is* an accepted-by-design deferral per the story header, so it is not counted as a
   gap.)
2. **Epic 36's dimensional projection (Story 36-2) is unbuilt and unwired**, yet its fact tables
   (`analytics_usage_hourly` / `analytics_usage_daily`, Story 36-1) are **already landed in the
   schema** (`Tamma.Data/Entities/AnalyticsUsageHourly.cs`, `AnalyticsUsageDaily.cs`, migration
   `20260618232930_AddAnalyticsUsageFactTables`, `TammaModelConfiguration`). Those tables are
   currently **write-orphaned** — nothing populates them — and the intended writer is a fan-out step
   on this workflow. That is a material, in-design scope gap, not a hypothetical.

---

## Current capabilities (what it does today)

Step sequence (`builder.Root = new Sequence`):

1. **`InitBucket`** (`SetVariable`) — resolves the target UTC top-of-hour from an optional `hour`
   input (`DateTime` / `DateTimeOffset` / parseable string) for backfill, else defaults to
   `UtcNow.AddHours(-1)`, then truncates via `AnalyticsRollupEvents.TruncateToHour`.
2. **`ComputePlatformRollupActivity`** — CP-only aggregation: counts `AGENT.DISPATCH.%`
   `platform_events` in the bucket + `ActiveTenantsAtHourEnd` gauge from the tenants directory
   (excludes soft-deleted, requires `CreatedAt < hourEnd`); read-then-upsert on the platform-wide
   partial unique index (`TenantId IS NULL`); emits `ANALYTICS.ROLLUP.PLATFORM_COMPLETED`.
3. **`FanOutTenantRollupsActivity`** — lists active tenants from the CP directory
   (`DeletedAt == null`, `CreatedAt < hourEnd`, `Status == null || "active"`), loops **serially**,
   calls `ComputeTenantRollupActivity.ComputeAsync` per tenant. Per-tenant failure is caught: emits
   `ANALYTICS.ROLLUP.TENANT_FAILED` (best-effort, double-try/catch), increments a failure counter,
   **continues** (28-10 AC5 tolerance). Outputs `TenantsSuccess` / `TenantsFailed`.
4. **`ComputeTenantRollupActivity`** (invoked inline by the fan-out) — per-tenant aggregation from
   `domain_events`: `WorkflowsStarted/Completed/Failed` (by `WorkflowInstances.Status`),
   `AGENT.DISPATCH.%` count, and `LLM.CALL.SUCCESS` cost/token sums via `AggregateLlmUsage`
   (tolerant JSON parse, skips malformed). Read-then-upsert on `(Hour, TenantId)`; emits
   `ANALYTICS.ROLLUP.TENANT_COMPLETED`.
5. **`EmitHourCompletedActivity`** — terminal `ANALYTICS.ROLLUP.HOUR_COMPLETED` with
   `{ tenantsSuccess, tenantsFailed, totalTenants }`.
6. **`PurgeStaleAnalyticsActivity`** — best-effort 13-month retention sweep
   (`ExecuteDeleteAsync WHERE Hour < cutoff`); emits `ANALYTICS.PURGE.HOURLY` on success,
   `ANALYTICS.PURGE.FAILED` otherwise; **never rethrows** except on host-shutdown cancellation.

Cross-cutting strengths already present:

- **Idempotency:** every write is read-then-upsert backed by partial unique indexes; replay/backfill
  is a no-op on row count.
- **Per-tenant failure isolation:** one bad tenant DB does not fail the bucket; the failure is durably
  recorded as a DCB event.
- **DCB audit events:** every milestone emits an `ANALYTICS.*` domain event via
  `IPlatformEventPublisher`, plus the base `TammaActivity` auto-emits `.STARTED`/`.COMPLETED`/`.FAILED`
  lifecycle events per activity (`Tamma.Activities/Core/TammaActivity.cs`).
- **Scheduler (`HourlyAnalyticsRollupScheduler`)** — `BackgroundService`, fires at minute 5 UTC,
  in-process last-fired dedup, **multi-pod-safe** via `pg_try_advisory_lock` keyed on
  `(year, dayOfYear, hour)` (Round-2 H9), dispatch failure logged at WARN with next-hour recovery.
- **No external-provider calls.** This is a pure read-aggregate-write job over Postgres; it does not
  call any LLM/agent/git provider, so the project's "steps never call external providers directly /
  route via tamma-api call-LLM mediation" rule is **not applicable** here. (32-5 mediation / Epic 38
  are not dependencies of this workflow.)

---

## Intended full scope (with citations)

**A. Story 28-10 (the workflow's own charter)** — `28-10-platform-analytics-rollup.md`:

- AC4 §Steps 1–5: ListActiveTenants → RollupPlatformWide → RollupPerTenant → AggregateResults →
  EmitCompletionEvent. **Built** (the fan-out is a single serial activity rather than Elsa
  `Parallel ParallelismDegree=10` sub-workflows — an accepted, documented design choice in
  `FanOutTenantRollupsActivity`'s class doc; serial keeps the Story 28-4 pool cache hot).
- AC2/AC3 rich metric sets (`issues.created`, `workflows.executed{name}`, `api.requests{class}`,
  `api.errors_5xx`, `auth.logins.*`, `tenants.provisioned/deleted/active{plan}` with JSONB `Tags`
  breakouts, long-narrow `MetricKey/Value` schema). **Explicitly accepted as a wide-row reduction**
  in the story header (2026-05-31): the shipped table carries only workflows / llm-cost-tokens /
  agent-dispatches / active-tenants. **Not counted as a gap** — it is a recorded design decision.
- AC5 partial-failure signalling: `ROLLUP_DEGRADED` below the 5% threshold, `ROLLUP_FAILED` at/above
  threshold with a one-budget 30-minute retry then page-ops. **Not built** — the fan-out counts
  failures but never evaluates the threshold or escalates. Gap.
- AC6 retention purge. **Built** (as the in-workflow `PurgeStaleAnalyticsActivity`, documented
  deviation from the separate weekly task). The AC6 purge *metrics/alerts*
  (`tamma_analytics_rows_purged_total`, `tamma_analytics_oldest_bucket_age_days` gauge, "stuck purge"
  alert) are **not built**. Minor gap.
- AC7 orchestrator-scale benchmark (1k/5k/10k idle instances + `benchmark-report.md`).
  **Explicitly deferred** to the Story 30 production-scale gate (story header). Not counted as a
  near-term gap.

**B. Epic 36 (the workflow's designated extension surface)** — `epic-36/README.md` "Projection is
idempotent, resumable… an additional fan-out step ON the existing HourlyAnalyticsRollupWorkflow";
Story 36-2 AC14 + §Workflow wiring spell out the exact target shape:

```
initBucket
platformRollup                 (unchanged, 28-10)
fanOut                         (unchanged, 28-10 platform fact table)
fanOutDimensional              (NEW — FanOutTenantDimensionalRollupsActivity)
compactDaily                   (NEW — CompactDailyAnalyticsActivity, day-boundary only)
emitCompleted                  (unchanged)
purgeStale                     (unchanged, CP)
purgeUsageStale                (NEW — PurgeStaleUsageAnalyticsActivity, per-tenant)
```

with: a `SequenceNumber`-checkpointed, resumable per-tenant dimensional projection into
`analytics_usage_hourly` (one row per `(Provider, AgentId, WorkflowDefinitionId, RepoId, CostBasis)`
tuple); BYOK-vs-platform cost-basis classification from the 35-2 `billing_mode` signal;
`PlatformBilledUsd = CostUsd × (1+margin)` via the `IAnalyticsPricingConfig` seam (36-7); lossless
daily compaction; a 13-month usage purge; new `ANALYTICS.ROLLUP.TENANT_DIMENSIONAL_*` /
`ANALYTICS.COMPACT.DAILY` / `ANALYTICS.PURGE.USAGE_HOURLY` / `ANALYTICS.ROLLUP.DIMENSIONAL_LAG`
events; and a projection-lag SLO (event + OTel gauge `tamma.analytics.projection_lag_seconds`).

**Verified state:** the 36-1 fact tables + `CostBasis` enum + model config + migration are present in
`Tamma.Data`, but **no** `ComputeTenantDimensionalRollupActivity`, `CompactDailyAnalyticsActivity`,
`PurgeStaleUsageAnalyticsActivity`, `FanOutTenantDimensionalRollupsActivity`,
`AnalyticsProjectionCheckpoint`, `IAnalyticsPricingConfig`, or any dimensional event constant exists
(`grep` over `Tamma.Activities/Analytics` + `Tamma.ElsaServer/Workflows` returns nothing). The tables
are write-orphaned.

**C. Domain best-practice for a fleet rollup/projection job** — also relevant to "complete":
operator backfill control surface (run a past hour), monotonic-cursor resumability, lag/SLO
observability, retention metrics + stuck-purge alerting, and structure tests pinning step presence +
ordering.

---

## Missing capabilities (gap to "complete")

| # | Capability | Priority | Depends on |
|---|-----------|----------|-----------|
| 1 | **Dimensional per-tenant projection fan-out** (`ComputeTenantDimensionalRollupActivity` + `FanOutTenantDimensionalRollupsActivity`) writing `analytics_usage_hourly` per `(Provider,AgentId,WorkflowDefinitionId,RepoId,CostBasis)` tuple — the currently-orphaned 36-1 tables' sole writer | P0 | Story 36-2 (schema 36-1 already landed) |
| 2 | **`SequenceNumber` checkpoint** (`analytics_projection_checkpoint` entity + tenant migration) for resumable, crash-safe, no-double-count projection | P0 | Story 36-2 |
| 3 | **Cost-basis classification** (`ResolveCostBasis`) from the 35-2 `billing_mode` tag / `ProviderDiagnostic.BillingMode`, defaulting to `platform` when absent (never a sentinel; tenant→signal→documented-default, never empty) | P0 | Story 36-2; soft on Story 35-2 (degrades to `platform`) |
| 4 | **`PlatformBilledUsd` margin** via `IAnalyticsPricingConfig` (+ `NullAnalyticsPricingConfig` zero-margin WARN fallback) — never hardcode a margin, never fail the rollup if 36-7 is absent | P0 | Story 36-2 + Story 36-7 (soft; zero-margin fallback) |
| 5 | **28-10 AC5 partial-failure signalling**: classify the bucket against the 95% threshold → emit `ANALYTICS.ROLLUP.DEGRADED` (below) or `ANALYTICS.ROLLUP.FAILED` (at/above) with `failedTenants`; one-budget 30-min retry then page-ops escalation | P1 | none (28-10 follow-up) |
| 6 | **Lossless daily compaction** (`CompactDailyAnalyticsActivity`, day-boundary only) hourly→`analytics_usage_daily` via `GROUP BY date_trunc('day',Hour),<dims>`, idempotent upsert | P1 | Story 36-2 |
| 7 | **Per-tenant usage retention purge** (`PurgeStaleUsageAnalyticsActivity`) mirroring the CP purge for `analytics_usage_hourly` (best-effort, runs last) | P1 | Story 36-2 |
| 8 | **Projection-lag SLO**: per-pass wall-clock lag (bucket→completion), `ANALYTICS.ROLLUP.DIMENSIONAL_LAG` event + OTel gauge `tamma.analytics.projection_lag_seconds` past budget (default 2h) | P1 | Story 36-2 |
| 9 | **Operator backfill / force-run endpoint** `POST /workflows/run/hourly-analytics-rollup` (referenced in the workflow's own XML doc but no handler found in `Tamma.Api`), threading the `hour` input + a `resetCheckpoint` flag | P2 | none (28-10 / 36-2 backfill) |
| 10 | **AC6 purge observability**: `tamma_analytics_rows_purged_total` counter, `tamma_analytics_oldest_bucket_age_days` gauge, "purge stuck > 400 days for 7 days" alert | P2 | none (28-10 AC6 residual) |
| 11 | **GDPR/right-to-be-forgotten coupling**: ensure tenant-erase deletes `platform_analytics_hourly WHERE TenantId=<id>` (and the new per-tenant `analytics_usage_*` rows live in the tenant schema and die with it) — extend Story 28-5 `DeleteTenantWorkflow` or document the not-PII legal position | P2 | Story 28-5 (Epic 37 audit/GDPR) |
| 12 | **Structure tests** pinning the expanded step set + ordering (platform fan-out precedes dimensional; `EmitHourCompleted` stays terminal-before-purge) once steps 1/6/7 land | P3 | items 1,6,7 |

> Out of scope / accepted-by-design (NOT gaps): the long-narrow `MetricKey/Tags` metric model and the
> full AC2/AC3 metric catalogue (accepted wide-row reduction, story header); the 1k/5k/10k
> orchestrator-scale benchmark (deferred to the Story 30 production-scale gate); Elsa `Parallel`
> sub-workflow fan-out (intentional serial single-instance design per the activity doc-comment).

---

## Ordered build-out spec (to reach complete & robust)

The dimensional projection (Story 36-2) is the dominant, in-design gap and the bulk of the work;
the 28-10 AC5 signalling is an independent, smaller follow-up. Order:

### Phase 1 — Dimensional projection foundation (Story 36-2, P0)

1. **Add event constants + cost-basis helper** to `AnalyticsRollupEvents`:
   `TenantDimensionalRollupCompleted` (`ANALYTICS.ROLLUP.TENANT_DIMENSIONAL_COMPLETED`),
   `…Failed`, `DailyCompacted` (`ANALYTICS.COMPACT.DAILY`),
   `UsageHourlyPurged`/`…Failed` (`ANALYTICS.PURGE.USAGE_HOURLY`/`…FAILED`),
   `DimensionalLag` (`ANALYTICS.ROLLUP.DIMENSIONAL_LAG`). Add pure
   `ResolveCostBasis(billingModeTag, diagnosticBillingMode)` → `CostBasis` (event tag wins,
   diagnostic backs it, absent → `Platform`). Unit-test every branch.
2. **`IAnalyticsPricingConfig` + `NullAnalyticsPricingConfig`** (zero-margin + WARN). Margin is
   consumed, never owned here; null config must keep the rollup green.
3. **`AnalyticsProjectionCheckpoint`** entity (`Stream="dimensional"`, `LastSequenceNumber long`,
   `UpdatedAt`) + `TenantDbContext` DbSet + `TammaModelConfiguration` (tenant graph) + additive
   tenant migration. Resumable read + atomic advance in the same `SaveChanges` as the upsert.
4. **`ComputeTenantDimensionalRollupActivity`** (+ static `ComputeAsync` pure-DI seam, mirroring
   `ComputeTenantRollupActivity.ComputeAsync`): for one `(tenantId, hour)`, read `domain_events`
   (`SequenceNumber > checkpoint`) + `ProviderDiagnostic` rows; group measures by the dimension
   tuple `(Provider, AgentId, WorkflowDefinitionId, RepoId, CostBasis)`; absent dimension → `NULL`
   bucket (never a sentinel, so `Σ per-dimension == grand total`); reuse `AggregateLlmUsage` for
   JSON measure extraction; compute `PlatformBilledUsd = CostUsd×(1+margin)` for `platform` rows,
   `0` for `byok`; **read-then-upsert** on the full dimension business key (NULLS-NOT-DISTINCT
   `UX_analytics_usage_hourly_dims` is the concurrent-replay backstop); advance the checkpoint
   atomically. Accept `hour` (backfill) + optional `resetCheckpoint`. Emit
   `TENANT_DIMENSIONAL_COMPLETED` with `{ hour, tenantId, rowsWritten, tuples, tokensIn/Out, costUsd,
   platformBilledUsd, checkpoint }`.

### Phase 2 — Fan-out, compaction, purge, lag (Story 36-2, P0/P1)

5. **`FanOutTenantDimensionalRollupsActivity`** — clone the `FanOutTenantRollupsActivity` shape:
   same active-tenant target set, serial loop, per-tenant `try/catch` → emit
   `TENANT_DIMENSIONAL_FAILED` (best-effort double-try/catch, no connection strings in the message)
   and **continue**; output success/failed counts. Record per-pass lag.
6. **`CompactDailyAnalyticsActivity`** (P1) — when `targetHour` is `00:xx` of a new UTC day, lossless
   `GROUP BY date_trunc('day',Hour),<dims>` of `analytics_usage_hourly` → upsert
   `analytics_usage_daily` on its business key; emit `ANALYTICS.COMPACT.DAILY`. Branch condition is a
   day-boundary check inside the activity (no second scheduler).
7. **`PurgeStaleUsageAnalyticsActivity`** (P1) — per-tenant `ExecuteDeleteAsync` of
   `analytics_usage_hourly` rows older than the retention window (13-month default), best-effort
   (rethrow only `OperationCanceledException` on host shutdown), emit
   `ANALYTICS.PURGE.USAGE_HOURLY` / `…FAILED`.
8. **Projection-lag SLO** (P1) — emit `ANALYTICS.ROLLUP.DIMENSIONAL_LAG` + OTel gauge
   `tamma.analytics.projection_lag_seconds` (KekRotationMetrics precedent) when bucket→completion lag
   exceeds budget (default 2h). WARN-level, never a failure edge.
9. **Wire into `HourlyAnalyticsRollupWorkflow.Build`** — insert into the `Sequence` exactly per 36-2
   AC14: `…, fanOut, fanOutDimensional, compactDaily, emitCompleted, purgeStale, purgeUsageStale`.
   One schedule, one advisory lock, one target hour. Add structure tests asserting the new steps +
   ordering (item 12).

### Phase 3 — 28-10 AC5 partial-failure signalling (independent, P1)

10. In the **platform** fan-out (`FanOutTenantRollupsActivity`) — after the loop, classify:
    `failed / total > 0.05` (and below an at-threshold cutoff) → emit `ANALYTICS.ROLLUP.DEGRADED`
    with `data.failedTenants`; at/above the failure threshold → emit `ANALYTICS.ROLLUP.FAILED`,
    schedule a single-budget 30-minute retry of the bucket (Elsa retry config / re-dispatch), and on
    second failure page ops (alert event). Keep the workflow status `Completed` for DEGRADED; only a
    second above-threshold failure escalates. Add the `RollupDegraded`/`RollupFailed` constants to
    `AnalyticsRollupEvents`. (Failure-path edge + escalation — honors "no silent-failure /
    false-success": today an all-tenants-failed bucket still emits a green `HOUR_COMPLETED`.)

### Phase 4 — Operator + observability + compliance polish (P2/P3)

11. **Backfill / force-run endpoint** `POST /workflows/run/hourly-analytics-rollup` in `Tamma.Api`
    (the workflow's XML doc already advertises it) dispatching the workflow with `{ hour,
    resetCheckpoint }` input behind `PlatformOwnerAccess`. (P2)
12. **Purge observability** — `tamma_analytics_rows_purged_total` counter,
    `tamma_analytics_oldest_bucket_age_days` gauge, "purge stuck" alert (28-10 AC6 residual). (P2)
13. **GDPR coupling** — extend Story 28-5 `DeleteTenantWorkflow` to delete
    `platform_analytics_hourly WHERE TenantId=<id>` on tenant erase (per-tenant `analytics_usage_*`
    rows die with the schema); or document the aggregate-only not-PII legal position (Doc 01 §10.3).
    (P2, Epic 37) 
14. **Structure tests** pinning the final step set + ordering. (P3)

### Guardrails honored throughout

- **tenant→system→error, never empty/plain:** cost-basis defaults to a *documented* `platform`
  classification (never a sentinel/empty); margin falls back to a *logged* zero (WARN) — never a
  hardcoded number, never a swallowed failure.
- **No silent-failure / false-success:** per-tenant failures are durably emitted as DCB events;
  Phase 3 adds the missing bucket-level DEGRADED/FAILED signal so a wholesale failure stops masquerading
  as a green hour.
- **DCB audit events** on every milestone (existing `ANALYTICS.*` + new dimensional/compact/purge/lag
  families), carrying `tenantId` so the Story 28-6 step-dedup index applies.
- **No external-provider calls** — pure read-aggregate-write; the 32-5 call-LLM mediation / Epic 38
  rules do not apply to this workflow.
