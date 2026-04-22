# Finding 026: Task Queue — no visibility timeout → zombie `processing` rows

**Scope**: engine (task queue)
**Severity**: P2 (correctness — tasks stuck forever when a worker dies)
**Status**: Incomplete (production-readiness gap)
**Estimated port effort**: 3h

## 1. What's in TS

The TS in-memory queue **also** lacked a visibility timeout (it was flagged as development-only — see header note on `in-memory-task-queue.ts:9-10`: "This implementation is suitable for development and testing. Production deployments should use a PostgreSQL-backed implementation.").

However, the production migration path envisioned a PostgreSQL-backed queue using a classic `SELECT ... FOR UPDATE SKIP LOCKED` pattern with a `claimed_until` timestamp — the industry-standard Postgres job queue. This was never built in TS either, so this finding measures **C# vs. the industry-standard** rather than C# vs. TS.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Services/TaskQueue/TaskQueueProcessor.cs:98-146`

The processor polls, claims (`MarkProcessingAsync`), and dispatches:

```csharp
// TaskQueueProcessor.cs:102-120 (current)
using var scope = _serviceProvider.CreateScope();
var repo = scope.ServiceProvider.GetRequiredService<IQueuedTaskRepository>();
var registry = scope.ServiceProvider.GetRequiredService<ITaskHandlerRegistry>();

var pending = await repo.ListPendingAsync(tenantId: null, _options.BatchSize, ct);
if (pending.Count == 0) return 0;

var processed = 0;
foreach (var task in pending)
{
    if (ct.IsCancellationRequested) break;
    var claimed = await repo.MarkProcessingAsync(task.Id, ct);
    if (claimed is null) continue;
    // ... handler.HandleAsync(claimed, ct) ...
}
```

When `MarkProcessingAsync` succeeds, the row's status is `'processing'`. If the handler crashes between that call and `MarkCompletedAsync` / `IncrementRetryAndRequeueAsync` — for example, the process is killed (OOM, SIGKILL, deploy rollout), the row stays in `'processing'` forever.

- `ListPendingAsync` filters for `status = 'pending'`. Zombie `processing` rows are invisible to the next poll and never get retried.

No timestamp is stored when `MarkProcessingAsync` succeeds (confirmed by inspection: no `ClaimedAt` or `ProcessingSince` column on `QueuedTask`).

## 3. The gap

For an engine deploy-rollout while tasks are executing:

- Industry standard (SQS / Postgres-based queues): tasks with `claimed_until < now()` are reclaimed by the next poll. Crashed workers have their work picked up by the next generation.
- C#: zombie tasks sit in `processing` forever. Manual DB surgery required. Running counters (dashboard) show nonzero "processing" indefinitely.

Realistic failure modes:
- Kubernetes pod eviction while `HandleAsync` is running
- App process OOM
- Database connection drop at the wrong moment
- Deploy rollout

Every one of these produces at least one zombie row.

Minor sibling issue: `ListPendingAsync` does not use `FOR UPDATE SKIP LOCKED`, so two processor replicas racing to poll will both pull the same `pending` row. `MarkProcessingAsync` will succeed for one and return `null` (handled at line 115) for the other — acceptable but wasteful.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-10/story-10-4/10-4-smart-queue-with-state-based-deduplication.md` — does not explicitly cover visibility timeout. The TS version noted production needed a proper PostgreSQL implementation.
- Story alignment:
  - [ ] Matches TS behavior (TS also lacked it but was flagged dev-only)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — spec gap; must be backfilled

## 5. Status

- **Classification**: Incomplete — production-readiness gap shared with TS but unaddressed during the port.
- **What's needed to finish**:
  1. Add `ClaimedAt DateTime?` column to `QueuedTask`.
  2. `MarkProcessingAsync` sets `ClaimedAt = NOW()`.
  3. Add a "reaper" path in `ListPendingAsync` (or a separate poll): rows where `status = 'processing' AND ClaimedAt < NOW() - interval '10 min'` → reset to `pending` (and bump retry count).
  4. Optionally adopt `SELECT ... FOR UPDATE SKIP LOCKED` for the claim path to eliminate the replica race.
  5. Make the visibility timeout configurable via `TaskQueueProcessorOptions.VisibilityTimeout` (default 10m).
- **Is it "just a stub" or is scope missing?** Scope missing — production queue semantics never built.
- **Blockers**: EF migration for new column. None beyond that.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/QueuedTask.cs` — add `ClaimedAt`.
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/IQueuedTaskRepository.cs` + impl.
  - `apps/tamma-elsa/src/Tamma.Api/Services/TaskQueue/TaskQueueProcessor.cs:94-146` — reaper pass.
  - `TaskQueueProcessorOptions` — `VisibilityTimeout` knob.
- Tests to add:
  - `ReapStale_RecoversProcessingOlderThanTimeout`
  - `ReapStale_IncrementsRetryCount`
  - `ReapStale_MaxRetriesReached_FlipsToFailed`
  - `MarkProcessing_SetsClaimedAt`
  - `ConcurrentProcessors_DoNotDoubleClaim`
- Estimated effort: 3h
  - Column + migration: 30m
  - Reaper + tests: 2h
  - SKIP LOCKED (stretch): 30m

## References

- TS source: `packages/api/src/services/in-memory-task-queue.ts` (dev-only, same gap)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/TaskQueue/TaskQueueProcessor.cs:98-146`
- Story: `docs/stories/epic-10/story-10-4/10-4-smart-queue-with-state-based-deduplication.md`
- Related findings: `025-task-queue-pull-to-push.md`, `027-task-queue-cross-tenant-processor.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: a3d2e7e
- **Notes**: Added `QueuedTask.ClaimedAt` (with EF migration
  `TaskQueueClaimedAt`) — `MarkProcessingAsync` stamps it. New
  `IQueuedTaskRepository.ReapStaleProcessingAsync(visibilityTimeout,
  maxRetries)` resets stale `processing` rows back to `pending` (or
  flips to `failed` when retry budget exhausted).
  `TaskQueueProcessorOptions.VisibilityTimeout` defaults to 10 minutes
  and is invoked at the head of every poll. Logs reaped counts at
  WARN. `SELECT ... FOR UPDATE SKIP LOCKED` for the claim path is
  noted as a stretch and not implemented (current `FindAsync + check`
  pattern already handles the replica race correctly, just wastefully).
