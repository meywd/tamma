# Story 28.6: Platform Tables (`platform_events` + `platform_queued_tasks` + `platform_email_outbox`)

**Epic**: Epic 28 - Database-per-Tenant Isolation
**Category**: Provisioning
**Status**: DONE — see audit `docs/superpowers/plans/2026-05-29-epic-28-status-audit.md` (RabbitMQ bus is a separate concern; in-memory bus + Postgres LISTEN/NOTIFY cover the 28-6 surface)
**Priority**: High (Story 28-5 cannot emit lifecycle events or queue
welcome emails without these tables; pulled forward between Phase 1
and Phase 2 per `00-sequencing.md`)
**Estimated Effort**: M (8-20h) — target 18h

## User Story

As a **platform engineer**, I want **three control-plane tables and
their repositories + background workers — `platform_events`,
`platform_queued_tasks`, `platform_email_outbox` — with a reflection
test that enforces they live only in CP**, so that **cross-tenant
lifecycle events, tenant-independent background tasks, and registration
emails all have a durable home that survives tenant creation and
deletion**.

## Acceptance Criteria

### AC1: `platform_events` table + entity + repository

- [ ] Entity `PlatformEvent` under
      `apps/tamma-elsa/src/Tamma.Data/Entities/PlatformEvent.cs` with
      the same shape as `DomainEvent` (append-only audit log) per
      Doc 01 §5.1: `Id`, `Type`, `OccurredAt`, `TenantId` (nullable
      — some events are user-scoped or platform-scoped),
      `ActorUserId`, `Payload` (JSONB), `CorrelationId`, `Tags`
      (JSONB), `Metadata` (JSONB).
- [ ] Table created by the CP migration from Story 28-1 (confirmed
      in this story's acceptance) with the required indexes:
  - `(tenant_id, created_at DESC)` — general tenant timeline scan.
  - Partial unique on
    `(tenant_id, type, (tags->>'step'), (tags->>'attempt'))
    WHERE type LIKE 'TENANT.PROVISION.STEP_%'` per Doc 03 §2.4.
  - `(type, created_at DESC)` — admin cross-tenant dashboards.
- [ ] Interface `IPlatformEventsRepository` with `AppendAsync(...)`
      and `QueryAsync(filters, cancellationToken)` methods. Inserts
      swallow unique-constraint violations (dedupe semantics per
      Doc 03 §2.4).
- [ ] Event type whitelist matches Doc 01 §5.2 (CP tier): `TENANT.*`,
      `USER.REGISTERED`, `USER.LOGIN.SUCCESS/FAILED`,
      `USER.SWITCHED_ORG`, `ORCHESTRATOR.TICK.*`, `GITHUB.INSTALLATION.*`
      (pre-tenant), `PLATFORM_ADMIN.IMPERSONATED.SUCCESS`.

### AC2: `platform_queued_tasks` table + entity + repository

- [ ] Entity `PlatformQueuedTask` under
      `.../Entities/PlatformQueuedTask.cs` per Doc 01 §1.2 row 24:
      `Id`, `TaskKey` (string, maps to a registered handler),
      `Payload` (JSONB), `Status` ∈
      `{pending, leased, running, succeeded, failed, dead}`,
      `CreatedAt`, `NextAttemptAt`, `AttemptCount`,
      `MaxAttempts`, `LastError`, `LeaseOwner`, `LeaseExpiresAt`.
- [ ] Interface `IPlatformQueuedTasksRepository` with:
  - `EnqueueAsync(task, ct)` — insert a new task.
  - `LeaseNextAsync(leaseOwner, leaseDuration, ct)` — uses
    `SELECT ... FOR UPDATE SKIP LOCKED` to lease a pending task
    whose `NextAttemptAt <= now()`.
  - `CompleteAsync(taskId, ct)` / `FailAsync(taskId, error, ct)` —
    mark task as `succeeded` or schedule retry with exponential
    backoff (base 30s, cap 1h, `MaxAttempts` default 5).
  - `DeadLetterAsync(taskId, ct)` — move to `dead` when
    `AttemptCount >= MaxAttempts`.
- [ ] Exponential backoff uses the same formula as Doc 03 §5.1:
      `NextAttemptAt = now() + min(30s * 2^attempt, 60min)`.
- [ ] Composite index `(Status, NextAttemptAt)` from Story 28-1 is
      the primary lookup path.

### AC3: `platform_email_outbox` table + entity + repository

- [ ] Entity `PlatformEmailOutboxMessage` under
      `.../Entities/PlatformEmailOutboxMessage.cs` per Doc 01 §1.2 row
      26: `Id`, `TenantId` (nullable for pre-tenant emails like
      verify-email), `UserId` (nullable), `Template`, `RecipientEmail`,
      `Subject`, `BodyHtml`, `BodyText`, `Status` ∈
      `{queued, sending, sent, failed, dead}`, `CreatedAt`, `SentAt`,
      `NextAttemptAt`, `AttemptCount`, `LastError`.
- [ ] Interface `IPlatformEmailOutboxRepository` with `EnqueueAsync`,
      `LeaseNextAsync`, `MarkSentAsync`, `MarkFailedAsync`.
- [ ] Composite index `(Status, CreatedAt)` from Story 28-1.
- [ ] Welcome-email uniqueness constraint:
      `UNIQUE (tenant_id, template) WHERE status <> 'failed'` when
      `template = 'welcome'` — matches Doc 03 §3.2 exactly-once
      semantics.
- [ ] Sent-mail retention: a scheduled `PlatformQueuedTask` with key
      `purge_sent_emails` runs daily, purges rows with `Status='sent'
      AND SentAt < now() - 30 days`.

### AC4: Reflection test — all three tables are CP-only

- [ ] Unit test in `apps/tamma-elsa/tests/Tamma.Data.Tests/
      PlatformTablesReflectionTest.cs` asserts:
  - `typeof(TenantDbContext).GetProperties()` contains **no**
    `DbSet<PlatformEvent>`, `DbSet<PlatformQueuedTask>`, or
    `DbSet<PlatformEmailOutboxMessage>`.
  - `typeof(ControlPlaneDbContext).GetProperties()` contains exactly
    one `DbSet` for each.
  - Entity classes have no `[Tenant]` attribute (if such an attribute
    exists in the codebase) and no `HasQueryFilter` on their model
    configuration.
- [ ] Test is wired into CI and fails the build on regression.

### AC5: Background `PlatformTaskWorker` hosted service

- [ ] Hosted service under
      `apps/tamma-elsa/src/Tamma.Api/Services/Platform/PlatformTaskWorker.cs`
      that loops: lease next task → resolve handler via registry →
      execute → complete or fail-with-retry → sleep (default 1s when
      idle, 0s when tasks available).
- [ ] Handler registry pattern per AC7:
      `services.AddPlatformTaskHandler<T>(key)` where `T :
      IPlatformTaskHandler` and `key` is the `TaskKey` string. The
      worker resolves handlers via DI on each lease.
- [ ] Concurrency: single worker per API pod by default
      (configurable via `PlatformTasks:Concurrency`, default 1). At
      scale, additional workers coordinate via `SKIP LOCKED`.
- [ ] Graceful shutdown: leased tasks are released (status back to
      `pending`, `NextAttemptAt=now()`) when the worker stops.
- [ ] Observability: metric
      `tamma_platform_tasks_leased_total{task_key, outcome}`.

### AC6: Background `PlatformEmailWorker` hosted service

- [ ] Hosted service under
      `.../Services/Platform/PlatformEmailWorker.cs` reuses the
      existing `IEmailSender` (unchanged per Doc 03 §7.1 and Epic 28
      conflict resolution #2).
- [ ] Loop: lease next email → call `IEmailSender.SendAsync` →
      `MarkSentAsync` or `MarkFailedAsync`. Exponential backoff
      matches the existing tenant-outbox behaviour (Doc 03 §7.2:
      60s → 5m → 30m → 2h → 6h).
- [ ] Emit `EMAIL.SENT.SUCCESS` / `EMAIL.SENT.FAILED` events to
      `platform_events` on terminal outcome (reuses the existing
      `OutboxSmtpSender` event emission pattern).
- [ ] Respects the `UNIQUE (tenant_id, template) WHERE status <>
      'failed'` constraint on `welcome` template — operators can
      manually flip a failed row to `queued` to retry.

### AC7: Handler registry pattern

- [ ] New interface `IPlatformTaskHandler`:
  ```csharp
  public interface IPlatformTaskHandler
  {
      Task ExecuteAsync(string payload, CancellationToken ct);
  }
  ```
- [ ] Extension method
      `services.AddPlatformTaskHandler<T>(string key)` registers a
      handler under a string key (the `TaskKey` column).
- [ ] Registry is a singleton keyed dictionary resolved by the
      worker: unknown `TaskKey` → task fails with
      `"no handler registered for key '<key>'"` and goes to dead
      letter after retries.
- [ ] Built-in handlers registered in this story:
  - `purge_sent_emails` — scheduled handler used by AC3 retention.
  - Others (analytics rollup, etc.) land in the stories that need
    them.

### AC8: Admin diagnostics endpoint

- [ ] `GET /api/v1/admin/diagnostics/platform-queues` (platform
      admin only) returns:
  - `platform_queued_tasks` counts grouped by `Status` and
    `TaskKey`.
  - `platform_email_outbox` counts grouped by `Status` and
    `Template`.
  - Top 10 oldest `pending` tasks.
  - Top 10 oldest `queued` emails.
- [ ] Response shape is JSON, documented in the admin runbook.

## Technical Context

- **Design docs**:
  - `plans/db-per-tenant/01-control-plane-split.md` §1.2 (entity
    placement rows 22, 24, 26), §5.1–5.2 (event tier), Appendix B
    item 6 (this story).
  - `plans/db-per-tenant/03-async-tenant-provisioning.md` §2 (event
    taxonomy), §3.2 (unique constraints), §7 (welcome email in CP
    outbox).
  - `plans/db-per-tenant/04-connection-pool-and-delete.md` §10.3
    (ghost-resource reconcile is a `PlatformQueuedTask`).
  - Epic 28 README conflict resolution #2 (welcome outbox in CP),
    `00-sequencing.md` Phase 2 prerequisite pull-forward.
- **File layout**:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/PlatformEvent.cs`
  - `.../Entities/PlatformQueuedTask.cs`
  - `.../Entities/PlatformEmailOutboxMessage.cs`
  - `.../Repositories/IPlatformEventsRepository.cs` (+ impl)
  - `.../Repositories/IPlatformQueuedTasksRepository.cs` (+ impl)
  - `.../Repositories/IPlatformEmailOutboxRepository.cs` (+ impl)
  - `apps/tamma-elsa/src/Tamma.Api/Services/Platform/PlatformTaskWorker.cs`
  - `.../Services/Platform/PlatformEmailWorker.cs`
  - `.../Services/Platform/IPlatformTaskHandler.cs`
  - `.../Services/Platform/PlatformTaskRegistryExtensions.cs`
  - `.../Endpoints/Admin/PlatformDiagnosticsEndpoints.cs`
- **`FOR UPDATE SKIP LOCKED` pattern**: Npgsql supports row-level
  locking natively; leasing query is
  `SELECT * FROM platform_queued_tasks
   WHERE status='pending' AND next_attempt_at <= now()
   ORDER BY next_attempt_at LIMIT 1 FOR UPDATE SKIP LOCKED`.
  Lease is recorded with a server-generated
  `LeaseExpiresAt = now() + lease_duration` so crashed leases are
  reclaimed by the startup recovery check.
- **Startup recovery**: on API pod start, the worker scans for
  tasks where `Status='leased' AND LeaseExpiresAt < now()` and
  resets them to `pending` (analogous to Doc 04 §10.2 startup
  recovery check for stuck deletes).

## Dependencies

- **Blocks**: 28-5 (workflow writes to these tables), 28-7 (API-key
  prefix routing reads CP), 28-10 (analytics rollup consumes
  `platform_events`), 28-11 (admin UX reads queue diagnostics).
- **Blocked by**: 28-1 (tables exist in CP schema), 28-2
  (`ControlPlaneDbContext` hosts the `DbSet<T>` properties).
- **External**: Existing `IEmailSender` implementation, Npgsql 8+
  `SKIP LOCKED` support.

## Test Plan

### Unit tests

- `IPlatformEventsRepository.AppendAsync` dedupes on the partial
  unique index when called twice with the same
  `(tenant_id, type, step, attempt)`.
- `IPlatformQueuedTasksRepository.LeaseNextAsync` returns the
  oldest eligible pending task and advances `Status='leased'`.
- Exponential backoff formula: unit test with attempts 0..10 yields
  the expected `NextAttemptAt` values (capped at 1h).
- Welcome-email uniqueness: second insert for `(tenantId,
  'welcome')` with `Status='queued'` fails on the unique index;
  after flip to `'failed'`, third insert succeeds.
- Handler registry: registering two handlers under the same key
  throws at startup.

### Integration tests (Testcontainers.PostgreSQL)

- Concurrent leasing: 4 workers call `LeaseNextAsync` simultaneously
  against 10 pending tasks; each task is leased by exactly one
  worker (no double-lease).
- Full lifecycle: enqueue task → worker leases → handler runs →
  worker calls `CompleteAsync` → row is `succeeded`. Assert
  `tamma_platform_tasks_leased_total{outcome="success"}` incremented.
- Retry path: handler throws transient, task `FailAsync` schedules
  retry; after 5 attempts, task goes to `dead`.
- Startup recovery: leave a leased task with expired lease, start
  worker, assert row reset to `pending`.
- Reflection test from AC4 passes.
- End-to-end: `CreateTenantWorkflow` step 10 (welcome email) + step
  11 (emit event) writes to CP tables correctly (integration test
  shared with Story 28-5).
- Diagnostic endpoint returns populated counts after seeded data.

### Manual verification

- Enqueue a test task via a dev-only endpoint, observe worker log
  output and `platform_queued_tasks` row transitions in `psql`.
- Trigger a stuck welcome email (SMTP unreachable), observe retry
  backoff in `NextAttemptAt` column.

## Definition of Done

- [ ] Acceptance criteria all green
- [ ] Unit + integration tests added, suite passes
- [ ] No new CodeQL alerts
- [ ] Design-doc references updated if the impl deviated
- [ ] Reviewed by a second engineer (cross-stream)

## Risks / Open Questions

- **Design docs are silent on `platform_queued_tasks.MaxAttempts`
  default.** Chose 5 attempts by analogy to Doc 03 §5.1 workflow
  retries. Operators can override per task via the column value;
  document the default in the admin runbook.
- **Clock skew across workers.** `FOR UPDATE SKIP LOCKED` with
  `NextAttemptAt <= now()` uses Postgres' clock as the single source
  of truth — worker-host clock drift doesn't affect leasing
  correctness. Flagged so reviewers don't worry about it.
- **`platform_email_outbox` retention of 30 days for `sent`
  messages** is this story's decision; design docs don't specify.
  Choose 30 days to match typical audit windows; configurable via
  `PlatformEmailOutbox:SentRetentionDays`.
- **Worker concurrency default of 1.** Doc 06 doesn't specify; at
  launch scale (100 tenants, 10s emails/day) a single worker
  suffices. Raise via `PlatformTasks:Concurrency` if queue depth
  becomes a hot signal. Admin diagnostics endpoint surfaces queue
  depth to make the tuning decision data-driven.
