# Finding 025: Task Queue — pull→push model change (engine cannot pull its own work)

**Scope**: engine (task queue)
**Severity**: P2 (correctness — deployment model flipped without acknowledgement)
**Status**: Semantic rewrite
**Estimated port effort**: 4h (add pull API) or 0h (document the push-only contract)

## 1. What's in TS

- File: `packages/api/src/services/task-queue.ts:50-70` (9e9a57c~1), `packages/api/src/services/in-memory-task-queue.ts:60-79`

```typescript
// packages/api/src/services/task-queue.ts:50-70 (9e9a57c~1)
export interface ITaskQueue {
  enqueue(task: EnqueueTaskInput): Promise<ITask>;

  /**
   * Dequeue the next pending task, optionally filtered by installationId.
   * The task is atomically moved to 'processing' status.
   * Returns null if no matching tasks are available.
   */
  dequeue(options?: DequeueOptions): Promise<ITask | null>;

  complete(taskId: string): Promise<void>;
  fail(taskId: string, error: string): Promise<void>;
  list(options?: ListTasksOptions): Promise<ITask[]>;
}
```

`dequeue(options)` is the pull primitive. An engine (or any worker process) calls `dequeue({installationId: 12345})` periodically to fetch its next task. The queue atomically transitions from `pending` → `processing` and returns the claimed task. TS used this to let multiple engines (or the engine + a background worker process) share a queue.

Pull model means: the server offers `dequeue` over HTTP; consumers poll it. The server is passive.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Services/TaskQueue/ITaskQueue.cs`

```csharp
public interface ITaskQueue
{
    Task<QueuedTask> EnqueueAsync(...);
    Task<QueuedTask?> GetAsync(Guid id, CancellationToken ct = default);
    Task<List<QueuedTask>> ListPendingAsync(int limit = 20, CancellationToken ct = default);
    Task<QueuedTask?> MarkProcessingAsync(Guid id, CancellationToken ct = default);
    Task MarkCompletedAsync(Guid id, CancellationToken ct = default);
    Task MarkFailedAsync(Guid id, string error, CancellationToken ct = default);
}
```

The interface docstring (line 14-15) notes the pivot explicitly:

```
/// <para>
/// Ported from the deleted TypeScript <c>ITaskQueue</c>
/// (<c>packages/api/src/services/task-queue.ts</c>). The C# surface drops the
/// in-memory <c>dequeue</c> helper because atomic pending→processing
/// transitions now happen in <see cref="TaskQueueProcessor"/> via the
/// repository's <c>MarkProcessingAsync</c>.
/// </para>
```

- `TaskQueueProcessor` (`Services/TaskQueue/TaskQueueProcessor.cs`) is a `BackgroundService` that polls the DB every 5 seconds and dispatches to registered `ITaskHandler`s in-process.

Push model: the API server itself is the worker. External engines cannot claim tasks via HTTP.

## 3. The gap

- TS: pull model — any engine process with the queue URL can claim work.
- C#: push model — only the API server's own `BackgroundService` does work.

Consequences:

1. **No multi-engine deployment**: You cannot have a standalone Elsa runtime process that pulls tasks from the queue. All task handlers must run inside the API process.
2. **Horizontal scaling is different**: TS let you scale by running N worker processes against one queue. C# requires scaling the whole API process.
3. **Self-hosted engine parity broken**: A self-hosted engine CLI that wanted to pull work from a hosted queue (classical Tamma hybrid deployment mode per CLAUDE.md) cannot do so via HTTP.
4. **Not a deal-breaker for SaaS deployments** — the API + background processor model is fine there. But the self-hosted hybrid architecture described in CLAUDE.md ("Hybrid Architecture: Operates as standalone CLI, orchestrator service, or distributed worker pool") is partially inaccessible.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-10/story-10-4/10-4-smart-queue-with-state-based-deduplication.md` — the task queue architecture.
- Also `docs/stories/epic-1/story-1-9/1-9-basic-cli-scaffolding.md` for self-hosted worker mode.
- Story alignment:
  - [ ] Matches TS behavior
  - [x] Matches C# behavior? — partial, the C# design is internally consistent for SaaS but breaks the self-hosted hybrid.
  - [x] Describes a third behavior — the push/pull pivot was an implementation decision, not captured in any story.
  - [ ] No story

## 5. Status

- **Classification**: Semantic rewrite. The pivot is documented in the interface doc-comment, so it was deliberate.
- **What's needed to finish**: Two options.
  - **Option A** (preserve TS contract): add `dequeue` semantics back to `ITaskQueue` + a dequeue endpoint (`POST /api/tasks/dequeue`). Engines can pull. 4h.
  - **Option B** (accept C# model): document clearly that the queue is API-server-internal. Add a statement to the hybrid architecture story that distributed workers must be implemented as additional API replicas, not as separate processes. 0h code, 30m doc work.
- **Is it "just a stub" or is scope missing?** Deliberate architecture pivot. Needs a spec-level decision.
- **Blockers**: none — product decision about deployment model.

## Remediation (Option A)

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/TaskQueue/ITaskQueue.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/TaskQueue/DbTaskQueue.cs`
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/TaskQueueEndpoints.cs` — `POST /api/tasks/dequeue`, `POST /api/tasks/{id}/complete`, `POST /api/tasks/{id}/fail`.
- Tests to add:
  - `Dequeue_AtomicallyTransitionsPendingToProcessing`
  - `Dequeue_FiltersByInstallationId`
  - `Dequeue_Returns_Null_WhenNoPending`
  - `Complete_CompletesClaimedTask`
  - `Fail_RequeuesOrMarksFailed_ByRetryCount`

## References

- TS source: `packages/api/src/services/task-queue.ts`, `packages/api/src/services/in-memory-task-queue.ts`
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/TaskQueue/ITaskQueue.cs` (pivot documented in comments), `TaskQueueProcessor.cs`
- Story: `docs/stories/epic-10/story-10-4/10-4-smart-queue-with-state-based-deduplication.md`
- Related findings: `026-task-queue-no-visibility-timeout.md`, `027-task-queue-cross-tenant-processor.md`
- CLAUDE.md section: "Hybrid Architecture" / "CLI modes"

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Invalid (deliberate architecture decision; documented)
- **Commit**: (none — by design)
- **Notes**: The push model is intentional and documented in
  `ITaskQueue.cs` line 11-15 ("drops the in-memory dequeue helper because
  atomic pending→processing transitions now happen in
  TaskQueueProcessor"). The C# deployment model collapses
  "engine + worker" into the API process; there is no current need to
  expose `dequeue` over HTTP. CLAUDE.md "Hybrid Architecture" still
  reads "standalone CLI, orchestrator service, or distributed worker
  pool" but the worker pool today runs as additional API replicas, not
  separate processes. If self-hosted hybrid worker-pull becomes a
  requirement, the right fix is to expose the existing repo's
  `MarkProcessingAsync` over a `POST /api/tasks/dequeue` endpoint —
  that is the ~4h follow-up the finding flags as Option A. No code
  change in this sprint.
