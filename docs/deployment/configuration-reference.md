# Configuration & Feature-Flag Reference

**Audience**: platform operators / oncall engineers
**Last updated**: 2026-06-05

Canonical index of Tamma's operator-facing configuration sections and
feature flags. Settings are bound from `appsettings.json` (env-var form:
`Section__Key`). This page is an index — deep per-feature tuning lives in
the linked docs.

> Operating mode matters: most tenant-aware settings behave differently in
> **single-user** vs **SaaS** mode. See the root `CLAUDE.md` "Operating
> Modes" section.

---

## Feature flags (default OFF unless noted)

| Flag | Default | Host | Effect | Docs |
|---|---|---|---|---|
| `Backup:DeletionBackup` | `false` | elsa-server | `pg_dump` snapshot of a tenant DB before `DROP DATABASE` in `DeleteTenantWorkflow`. Requires `pg_dump` in the image + a durable mounted `Backup:Directory`. | [tenant-deletion-backup.md](./tenant-deletion-backup.md) |
| `TenantConnectionPool:Warmup:Enabled` | `false` | api | Pre-warm the top-N tenant connection pools at startup. | [connection-pool-tuning.md](./connection-pool-tuning.md) |
| `HourlyAnalyticsRollup:Enabled` | `true` | elsa-server | Hourly `platform_analytics_hourly` rollup scheduler. | [../runbooks/platform-analytics-hourly-rollup.md](../runbooks/platform-analytics-hourly-rollup.md) |
| `TenantCleanupTrigger:Enabled` | `true` | elsa-server | Consumes `TENANT.CLEANUP_REQUESTED` events to drive `DeleteTenantWorkflow`. | Story 28-5 |

> **Removed:** `Tamma:RequireTenantIsolation` was deleted in unified-tenancy Phase 3 — the `LruPooledTenantConnectionResolver` is now the only tenant connection path (the stub fallback it guarded no longer exists), so the knob has no effect and is ignored.

## Configuration sections

| Section | Host | Purpose | Docs |
|---|---|---|---|
| `ConnectionStrings` | all | `DefaultConnection`, optional `TenantAdmin`, `ControlPlane`. | Story 28-1/28-4 |
| `Backup` | api + elsa-server | Pre-drop tenant backup (`DeletionBackup`, `Directory`, `PgDumpPath`, `TimeoutSeconds`). | [tenant-deletion-backup.md](./tenant-deletion-backup.md) |
| `TenantConnectionPool` | api | Per-tenant LRU `NpgsqlDataSource` pool sizing + warmup. | [connection-pool-tuning.md](./connection-pool-tuning.md) |
| `HourlyAnalyticsRollup` | elsa-server | Rollup scheduler cadence (`FireAtMinute`, `PollInterval`). | [runbook](../runbooks/platform-analytics-hourly-rollup.md) |
| `Cranl` | api | Per-tenant infra provisioning (see `CLAUDE.md` "Multi-tenant provisioning"). | — |

## Non-configurable behaviours worth knowing

These are **not** flags (compile-time constants / fixed policy), listed so
operators don't go hunting for a setting that doesn't exist:

- **Analytics retention = 13 months.** `PurgeStaleAnalyticsActivity`
  (final step of the hourly rollup) deletes `platform_analytics_hourly`
  rows older than 13 months. The window is the activity's `RetentionMonths`
  input default — change it in `HourlyAnalyticsRollupWorkflow.Build()`, not
  via config. See the [runbook §7.1](../runbooks/platform-analytics-hourly-rollup.md).
- **Admin tenant-events SSE long-poll fallback.**
  `GET /api/admin/tenants/{id}/events/stream?fallback=poll` returns a
  one-shot JSON `PollSnapshot { events, nextEventId, hasMore }` (cap 200
  events) instead of `text/event-stream`, for clients behind proxies that
  buffer streaming. The client echoes `nextEventId` back via the
  `Last-Event-ID` header on the next poll — the same resume token the
  stream uses. Story 28-11 AC3.

## New audit event types (Epic 28 Wave 5)

Emitted to `platform_events`:

- `ANALYTICS.PURGE.HOURLY` — retention sweep ran (`rowsDeleted`, `cutoff`).
- `ANALYTICS.PURGE.FAILED` — retention sweep threw (best-effort; rollup unaffected).
- `TENANT.LIFECYCLE.BACKUP_DATABASE` (`STEP_STARTED`/`STEP_COMPLETED`/`STEP_FAILED`) — pre-drop backup step.
