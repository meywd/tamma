# Finding: `PlatformTaskWorker:RunOnStartup=true` is unsafe in production (shared-queue type collision)

**Date**: 2026-06-10
**Context**: tenancy residual #3 — "enable PlatformTaskWorker:RunOnStartup in prod when queued
moves should execute" (post unified-tenancy merge, PR #343).
**Verdict**: NOT enabled. Enabling it today silently destroys scheduled secret retirements.

## Background

`PlatformTaskWorker` (Story 28-6, `apps/tamma-elsa/src/Tamma.Api/Services/PlatformTasks/PlatformTaskWorker.cs`)
drains `platform_queued_tasks`. It defaults to `RunOnStartup = false` (Round-2 H8) and is gated
off in production, which is why queued `tenant.move` tasks from
`POST /api/admin/tenants/{id}/move` do not execute. The naive fix is to set
`PlatformTaskWorker:RunOnStartup=true` — its registry handlers (`tenant.move` via
`MoveTenantTaskHandler`, `provisioning.tenant.v2` via `ProvisionTenantV2TaskHandler`) ARE wired
in `Program.cs` / `ProvisioningServiceCollectionExtensions`.

## The hazard

`PlatformQueuedTaskRepository.ReserveNextAsync` claims **the oldest `pending` row of ANY type**:

```sql
WHERE "Status" = 'pending' ORDER BY "CreatedAt" ASC FOR UPDATE SKIP LOCKED LIMIT 1
```

No type filter, and `PlatformQueuedTask` has **no run-after column**. The table is shared by
producers whose rows must remain `pending` and must NOT be consumed by this worker:

1. **`RETIRE_SECRET_VERSION` (Story 29-6 secret rotation)** — `RotateSecretSagaActivity` /
   `ScheduleRetireOldActivity` → `RetireScheduler.ScheduleRetireAsync` enqueues a pending row
   whose `runAfter` (grace period before retiring the old secret version) lives ONLY in the JSON
   payload. The companion `SweepDueRetireTasksAsync` reserves rows, releases not-yet-due/wrong-type
   ones with `maxRetries: int.MaxValue`, and retires due ones.
   With the worker enabled: no `IPlatformTaskHandler` exists for this type, so the worker parks
   the row (`ParkUnprocessableAsync`: `RetryCount += 1`, status back to `pending`, original
   `CreatedAt` kept). The row stays oldest-pending, so the worker re-claims it every
   `PollInterval` (5s) tick and **dead-letters it after `MaxRetries` (5) observations — ~25
   seconds after enqueue**, long before its `runAfter`. Old secret versions would never retire;
   rotation is silently broken with no error surfaced to the rotation flow.

2. **Orphan-webhook fallback rows** — `InstallationRouterService` enqueues webhook payloads onto
   the platform queue when no installation→tenant mapping exists yet ("handlers can decide at
   dispatch time"). No handler is registered → same park→dead-letter fate; orphan webhooks are
   destroyed instead of waiting for the mapping.

3. **v1 Cranl rows** (`provisioning.tenant`, `provisioning.tenant.deprovision`) — enqueued by
   `CranlTenantProvisioner` and (deliberately, via the v1 constants) by `CranlTenantProviderV2`.
   Their consumer is NOT an `IPlatformTaskHandler` → park→dead-letter. Moot while `Cranl:ApiKey`
   is unset in prod (Null seam), but a footgun the moment Cranl is enabled.

Secondary wart: the rotation sweeper's "not ours" release path calls `FailAsync` which still
increments `RetryCount` on `tenant.move` / `provisioning.tenant.v2` rows it touches, eroding
their real retry budget against the worker's `MaxRetries = 5`.

## Prerequisite to enable

Either of:

- **Type-aware reservation** (preferred): `ReserveNextAsync(workerId, types[], ct)` so
  `PlatformTaskWorker` only claims types present in its `IPlatformTaskHandlerRegistry`, and the
  rotation sweeper only claims `RETIRE_SECRET_VERSION` (also removes the retry-budget erosion).
  A first-class `RunAfter` column on `platform_queued_tasks` would let retire rows rejoin the
  general worker afterwards.
- Register an `IPlatformTaskHandler` for **every** producer type with correct semantics
  (the retire handler must re-park until `runAfter` without consuming retry budget — i.e. it
  still effectively needs run-after support).

Until then `PlatformTaskWorker:RunOnStartup` must stay unset/false in production; queued
`tenant.move` rows wait harmlessly in `pending` and execute on the first deploy that enables the
worker safely.
