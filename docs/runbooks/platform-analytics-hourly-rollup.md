# Runbook — `platform_analytics_hourly` hourly rollup

**Story**: 28-10
**Owners**: Platform operations
**Last reviewed**: 2026-04-22

---

## 1. What this runbook covers

The `HourlyAnalyticsRollupWorkflow` (global Elsa) fires at minute 5 of
every hour UTC and writes one row per `(Hour, TenantId)` tuple into
`platform_analytics_hourly` on the control-plane DB. Admin queries
(`/api/admin/analytics/summary`) read from this fact table first and
fall back to live aggregation if the table is empty or stale.

This runbook covers three ops scenarios:

1. **Backfilling** a range of hours (e.g. after a fresh deploy or a
   multi-hour outage).
2. **Rerunning** a single hour that failed or produced bad data.
3. **Interpreting** gaps — "why does 2026-04-18T12:00 have no row?"

## 2. Architecture one-pager

```
 [ global-Elsa host ]                    [ Control Plane DB ]
        │
        │ cron "0 5 * * * *" UTC
        ▼
 HourlyAnalyticsRollupWorkflow
   1. InitBucket           ← derive target hour from input or UtcNow-1h
   2. ComputePlatformRollup ─────────────► platform_analytics_hourly
                                              (Hour, TenantId=NULL row)
   3. FanOutTenantRollups
      for each active tenant:                [ Tenant DB ]
        ComputeTenantRollupActivity ────►    read domain_events (window)
                                         ─── upsert platform_analytics_hourly
                                              (Hour, TenantId=<tid> row)
      catch per-tenant failure:
        emit ANALYTICS.ROLLUP.TENANT_FAILED to platform_events
   4. EmitHourCompleted    ─────────────────► platform_events
                                                 (ANALYTICS.ROLLUP.HOUR_COMPLETED)
```

Every step emits a `platform_events` row so the audit trail stays intact
even when a per-tenant rollup throws.

## 3. Schedule + configuration

- **Cron expression**: `0 5 * * * *` (six-field Quartz-style: second,
  minute, hour, day, month, weekday) — minute 5 of each hour UTC.
- **Workflow id**: `hourly-analytics-rollup` (constant on
  `HourlyAnalyticsRollupWorkflow.DefinitionId`).
- **Input** (optional): `hour` — an ISO-8601 timestamp. If set, the
  workflow truncates to the top of that hour and rolls up that bucket;
  if omitted, the workflow rolls up `UtcNow - 1 hour`.

The cron trigger itself is NOT auto-wired — operators attach it via the
Elsa admin UI or a scheduled-trigger config file. Until attached, the
workflow is inert (this matches the Epic 28 rollout plan: ship the
wiring cold, turn it on after the migration is applied).

## 4. Backfilling a range of hours

Use when:

- Migration `20260422105157_PlatformAnalyticsHourly` was just applied.
- The rollup was disabled for an outage ≥ 1 hour.
- A platform admin adds the first active tenant and wants historical
  rollup rows for their dashboards.

**Procedure**:

```bash
# 1. Identify the range. Example: last 24 hours.
START="$(date -u -d '24 hours ago' '+%Y-%m-%dT%H:00:00Z')"
END="$(date -u -d 'now' '+%Y-%m-%dT%H:00:00Z')"

# 2. Fire the workflow once per hour bucket. The Elsa REST API accepts
#    a run request against the definition id with the "hour" input.
for OFFSET in $(seq 0 23); do
  TARGET="$(date -u -d "${START} + ${OFFSET} hour" '+%Y-%m-%dT%H:00:00Z')"
  echo "Rolling up hour: ${TARGET}"
  curl -sf -X POST "${ELSA_URL}/elsa/api/workflow-definitions/hourly-analytics-rollup/execute" \
    -H "Authorization: Bearer ${ELSA_ADMIN_KEY}" \
    -H 'Content-Type: application/json' \
    -d "{\"input\":{\"hour\":\"${TARGET}\"}}"
done
```

Each run is independent; the workflow emits its own
`ANALYTICS.ROLLUP.HOUR_COMPLETED` event per bucket so the audit trail
captures the backfill.

**Concurrency**: serialise the loop (do NOT fire all 24 in parallel) —
the tenant-pool cache is LRU-capped at 256 and a parallel backfill on
10k tenants would thrash it. 24 sequential runs complete in
~24 × 15 min = 6 hours for a 10k-tenant fleet.

## 5. Rerunning a single failed hour

Use when:

- `ANALYTICS.ROLLUP.HOUR_COMPLETED` for a given hour shows
  `tenantsFailed > 0`.
- A dashboard complaint pinpoints a specific bucket showing zeros.
- A tenant DB came back online after a provisioning failure.

**Check which hour failed**:

```sql
-- Run on the control-plane DB.
SELECT
  (tags->>'hour')::timestamptz AS hour,
  (data->>'tenantsSuccess')::int AS success,
  (data->>'tenantsFailed')::int AS failed
FROM platform_events
WHERE type = 'ANALYTICS.ROLLUP.HOUR_COMPLETED'
ORDER BY "CreatedAt" DESC
LIMIT 48;
```

**Check which tenants failed**:

```sql
SELECT
  (tags->>'tenantId')::uuid AS tenant_id,
  data->>'errorType' AS error_type,
  data->>'message' AS message
FROM platform_events
WHERE type = 'ANALYTICS.ROLLUP.TENANT_FAILED'
  AND (tags->>'hour')::timestamptz = '2026-04-18T12:00:00Z'
ORDER BY "CreatedAt";
```

**Rerun the whole hour** (replays all tenants — upsert makes it safe):

```bash
curl -sf -X POST "${ELSA_URL}/elsa/api/workflow-definitions/hourly-analytics-rollup/execute" \
  -H "Authorization: Bearer ${ELSA_ADMIN_KEY}" \
  -H 'Content-Type: application/json' \
  -d '{"input":{"hour":"2026-04-18T12:00:00Z"}}'
```

The workflow's `ComputeTenantRollupActivity` reads the existing row
for `(Hour, TenantId)` and updates it in place, so replaying a fully-
or partially-rolled-up hour does NOT create duplicates. The
`UX_platform_analytics_hourly_Hour_TenantId` partial unique index is
the hard backstop if two replays race.

## 6. Interpreting missing rows

**Scenario A — no platform-wide row for a given hour**:

```sql
SELECT * FROM platform_analytics_hourly
WHERE "Hour" = '2026-04-18T12:00:00Z' AND "TenantId" IS NULL;
```

Empty result means `ComputePlatformRollupActivity` never ran. Check
the Elsa workflow-instance table for a failed instance at that time.
If the instance never started (cron misfire), fire a manual rerun per
§5.

**Scenario B — platform-wide row present but per-tenant rows missing
for some tenants**:

Expected when tenants were created or reactivated mid-window. The
`FanOutTenantRollupsActivity` only iterates tenants whose `CreatedAt <
Hour + 1h`, so a tenant created at 12:45 would NOT have a row in
`2026-04-18T12:00:00Z` — that's correct (they have zero activity in
the first 15 min of the hour, and their domain_events window starts
at CreatedAt, so the row would be an all-zeros entry). If you want
the zero row for UX consistency, trigger a rerun for that hour AFTER
the tenant row was created.

**Scenario C — sudden drop in workflow counts for one hour**:

```sql
-- Compare the bucket against its neighbours.
SELECT "Hour", SUM("WorkflowsCompleted") AS completed
FROM platform_analytics_hourly
WHERE "TenantId" IS NOT NULL
  AND "Hour" >= '2026-04-18T10:00:00Z'
  AND "Hour" <= '2026-04-18T14:00:00Z'
GROUP BY "Hour"
ORDER BY "Hour";
```

A 10x drop compared to neighbours usually means per-tenant failures.
Cross-reference with the `ANALYTICS.ROLLUP.TENANT_FAILED` query in §5.

## 7. Health checks

- **Freshness probe**: `/api/admin/analytics/summary` returns
  `GeneratedAt ≈ UtcNow` and numbers from the fact table when the
  most-recent bucket is ≤ 90 minutes old. If the returned counters
  look stale (e.g. zeros despite known activity), the service fell
  back to the live path — see §3 of the service source for the
  `ShouldPreferFactTableAsync` gating.
- **Event rate**: `SELECT COUNT(*) FROM platform_events WHERE type =
  'ANALYTICS.ROLLUP.HOUR_COMPLETED' AND "CreatedAt" >= NOW() -
  INTERVAL '24 hours'` should return 24. Lower = missed crons.
- **Disk footprint**: at 10k tenants × 24 hours × 365 days × ~200
  bytes/row = ~17 GB/year. Retention policy (not in this story)
  should trim after 13 months.

## 8. Known gaps (follow-up tickets)

- **Cron trigger registration is manual**. Future operator script
  should attach the `hourly-analytics-rollup` cron to the global-Elsa
  scheduler at deploy time.
- **Running-count straddle**. A workflow that starts in hour H and
  completes in H+1 is counted as "started" in H and "completed" in
  H+1 — its H bucket shows `started=1, completed=0, failed=0`,
  implying `running=1`. When we sum across the window, this clamps at
  zero in `PlatformAnalyticsService.GetWorkflowCountsFromFactTableAsync`
  — a future enhancement should track storage-state at bucket close.
- **Retention**. Story 28-10 AC6 calls for a weekly `PURGE_ANALYTICS_HOURLY`
  task (not shipped in this wave). Until then, ops can manually trim:
  `DELETE FROM platform_analytics_hourly WHERE "Hour" < NOW() -
  INTERVAL '13 months';` batched at 10000 rows per statement.

## 9. Escalation

- **Rollup hasn't run in > 3 hours** → page the on-call platform
  engineer. Either the cron trigger fell off or the global-Elsa host
  is down.
- **Fact table has > 10% empty tenant rows** for the latest hour →
  investigate per-tenant connection pool. A saturated pool causes
  timeouts that log as `ANALYTICS.ROLLUP.TENANT_FAILED` with
  `errorType: TimeoutException`.
- **`ShouldPreferFactTableAsync` consistently returns false in
  production** → the rollup is stale. Page + investigate per above.
