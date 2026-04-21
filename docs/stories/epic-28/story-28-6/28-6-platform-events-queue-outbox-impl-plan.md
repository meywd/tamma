# Story 28-6 Implementation Plan — Platform Events + Queue + Email Outbox

**Status**: Planned (2026-04-20)
**Story brief**: [`28-6-platform-events-queue-outbox.md`](./28-6-platform-events-queue-outbox.md)
**Epic 28 phase**: B (parallel with 28-4)
**Branch**: `feat/story-28-6-platform-events-queue-outbox`

---

## 1. Objective

Three CP-resident durable tables (`platform_events`,
`platform_queued_tasks`, `platform_email_outbox`) + their repositories
+ background workers that drain them. Story 28-5 depends on these to
emit lifecycle events, queue post-provision tasks, and send welcome
emails. Includes a reflection guard that asserts these tables never
land in `TenantDbContext`.

## 2. Dependencies

Hard blockers:

- **Story 28-1** — tables scaffolded in the CP migration.
- **Story 28-2** — `ControlPlaneDbContext` owns the entities.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Entities/PlatformEvent.cs` | Entity. |
| `.../Entities/PlatformQueuedTask.cs` | Entity. |
| `.../Entities/PlatformEmailOutbox.cs` | Entity. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Repositories/PlatformEventsRepository.cs` | `AppendAsync`, `QueryAsync`; swallows unique-constraint dedup. |
| `.../Repositories/PlatformQueuedTaskRepository.cs` | `EnqueueAsync`, `ReserveNextAsync(workerId)`, `CompleteAsync(id, outcome)`. |
| `.../Repositories/PlatformEmailOutboxRepository.cs` | `QueueAsync`, `ReserveNextAsync`, `MarkSentAsync`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Background/PlatformTaskDispatcher.cs` | Background `IHostedService` that drains `platform_queued_tasks`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Background/PlatformEmailDispatcher.cs` | Drains `platform_email_outbox` via SMTP. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Data.Tests/ReflectionTests/PlatformTablesLocationTests.cs` | Reflection guard: these three entities are only in `ControlPlaneDbContext`. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Platform/PlatformEventsRepositoryTests.cs` | Append, dedup, query filters. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Platform/PlatformTaskDispatcherTests.cs` | Concurrency, retry, dead-letter. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Add 3 `DbSet`s + fluent configs for indexes. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Register repositories + background dispatchers. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/appsettings.json` | `PlatformTaskDispatcher:WorkerCount`, `PlatformEmailDispatcher:SmtpHost` etc. |
| `/home/meywd/tamma/docs/runbooks/platform-events.md` | Retention, purge, query patterns. |

## 5. Sequence of changes

### Step 1 — Entities + configs + reflection guard (3h)

- Entities as per brief + fluent configs with the three indexes
  (tenant-timeline; `TENANT.PROVISION.STEP_*` dedup; admin cross-tenant).
- Reflection test that scans `ControlPlaneDbContext` for the entities,
  fails if any appears on `TenantDbContext` too.
- **Commit**: `feat(db): platform_events/queue/outbox entities`.

### Step 2 — Repositories (3h)

- `PlatformEventsRepository.AppendAsync` swallows
  `23505 unique_violation` for the step-dedup index.
- `PlatformQueuedTaskRepository.ReserveNextAsync(workerId)` uses
  `UPDATE ... WHERE status='queued' RETURNING *` with
  `SKIP LOCKED` for concurrent workers.
- `PlatformEmailOutboxRepository` similar.
- **Commit**: `feat(platform): repositories for events/queue/outbox`.

### Step 3 — Task dispatcher (4h)

- `PlatformTaskDispatcher`:
  - Reads `WorkerCount` from config (default 4).
  - Each worker loop: `ReserveNext` → dispatch by `TaskType`
    (strategy pattern) → `CompleteAsync(id, outcome)`.
  - On exception: increment `Attempts`, reschedule with exponential
    backoff; after `MaxAttempts=5`, mark `dead_letter` with error.
- Metrics: `platform_task_processed_total{type, outcome}`.
- **Commit**: `feat(platform): task dispatcher background service`.

### Step 4 — Email dispatcher (3h)

- Wraps `MailKit.SmtpClient`.
- Idempotent via `platform_email_outbox.Status`; reserve→send→mark_sent.
- Retry backoff for 4xx SMTP responses; dead-letter on 5xx permanent.
- Unit tests via fake SMTP.
- **Commit**: `feat(platform): email outbox dispatcher`.

### Step 5 — Tests + runbook (3h)

- Repository + dispatcher tests.
- `docs/runbooks/platform-events.md`: retention (90-day rolling
  delete via background job — out of scope here, flagged as 28-10
  follow-up), query patterns.
- **Commit**: `test(platform): repositories + dispatchers + runbook`.

### Step 6 — Metrics + dashboards (2h)

- Prometheus counters + histograms.
- Grafana panel JSON shipped at `ops/grafana/dashboards/platform.json`.
- **Commit**: `feat(ops): platform metrics dashboard`.

## 6. Test strategy

### Unit

- Repository dedup; queue reserve concurrency; email retry.

### Integration

- Multi-worker concurrency: 3 workers drain a 100-task queue, assert
  each task processed exactly once (no double-reserve).
- Dead-letter test: failing task hits `MaxAttempts`, ends in
  `dead_letter` with error message preserved.

## 7. Rollback plan

- **Schema rollback**: migrations from 28-1 include these tables;
  revert migration drops tables. Dogfood data safe to lose.
- **Dispatcher safety**: off if `WorkerCount=0`.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Entities + guard | 3 |
| 2. Repositories | 3 |
| 3. Task dispatcher | 4 |
| 4. Email dispatcher | 3 |
| 5. Tests + runbook | 3 |
| 6. Metrics | 2 |
| **Total** | **18** (matches brief) |

## 9. Open questions

- **Retention**: 90 days is a default; compliance may want 7 years.
  Configurable via `PlatformEvents:RetentionDays`. Purge job tracked
  as 28-10 follow-up.
- **Email transport for prod**: SendGrid vs. Postmark vs. SMTP relay?
  Confirm with Deploy Coordinator. First ship supports SMTP only.
- **`SKIP LOCKED` portability**: Postgres-specific. Npgsql supports
  it. Document in runbook.
- **Dead-letter queue UI**: Platform admin UI needs a "stuck tasks"
  view — tracked as 28-11 scope extension or a new follow-up story.
