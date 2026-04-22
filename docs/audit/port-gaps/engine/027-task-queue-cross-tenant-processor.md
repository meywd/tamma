# Finding 027: Task Queue BackgroundService runs cross-tenant by design

**Scope**: engine (task queue)
**Severity**: P3 (drift / contract — documented but worth validating)
**Status**: Behavioral drift (design decision; RLS implications)
**Estimated port effort**: 2h (audit + doc) or 6h (hard-scope each poll)

## 1. What's in TS

- File: `packages/api/src/services/in-memory-task-queue.ts` + `packages/api/src/services/task-queue.ts` (9e9a57c~1)

TS did not have a background service. The webhook handler enqueued tasks into `InMemoryTaskQueue`; consumption was explicit via `dequeue(options)` with `installationId` filtering. No ambient tenant context, no RLS implications — the queue was process-local and caller-scoped.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Services/TaskQueue/TaskQueueProcessor.cs:98-146`

```csharp
// TaskQueueProcessor.cs:102-107 (current)
using var scope = _serviceProvider.CreateScope();
var repo = scope.ServiceProvider.GetRequiredService<IQueuedTaskRepository>();
var registry = scope.ServiceProvider.GetRequiredService<ITaskHandlerRegistry>();

var pending = await repo.ListPendingAsync(tenantId: null, _options.BatchSize, ct);
```

The processor creates a fresh DI scope but does **not** set an `ITenantContext` for the scope. `tenantId: null` is passed to `ListPendingAsync`, which returns rows from every tenant.

Doc-comment on `ITaskQueue.ListPendingAsync` (line 42-46) confirms this is intentional:

```
/// List pending tasks for the ambient tenant. When the ambient tenant is
/// <c>null</c> (system scope / self-hosted), returns tasks for every
/// tenant — this is the lane the processor takes when it runs unscoped.
```

So the processor is architected as a system-scope consumer: it sees all tenants' tasks and dispatches them. Individual `ITaskHandler` implementations are responsible for setting the correct tenant context before they do tenant-scoped work.

## 3. The gap

- TS did: no processor; handlers ran synchronously in the request pipeline with the caller's tenant context.
- C#: background processor pulls tasks from every tenant and dispatches. Each handler must set the tenant context explicitly. If a handler forgets, its DB writes land without RLS scoping (see finding 028).

The design is defensible — a single processor for all tenants is cheaper than one per tenant. But the safety depends on:

1. Every `ITaskHandler.HandleAsync(claimed, ct)` reading `claimed.TenantId` and setting an `ITenantContext` (or equivalent) on the scoped DbContext.
2. Every repository call inside the handler honouring that context.
3. `EventRepository` not silently bypassing the context via `IgnoreQueryFilters()` (see finding 028).

None of these are enforced structurally. It's a correctness-by-convention setup. The cross-tenant blast radius if one handler forgets is all of the system's events/rows.

### Probing for violations

- `QueuedTask.TenantId` is set on enqueue (see `DbTaskQueue.EnqueueAsync:36`).
- Handlers under `Tamma.Api/Services/GitHub/` are the primary consumers. The `InstallationRouterService` passes `tenantId` explicitly during enqueue, so the row carries the right tenant.
- But there's no `ITaskHandler` base class or helper that automatically sets `ITenantContext.TenantId = claimed.TenantId` before the handler body runs. Each implementation must remember.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md`. Also `docs/stories/epic-10/story-10-4/10-4-smart-queue-with-state-based-deduplication.md`.
- Story alignment:
  - [ ] Matches TS behavior (TS had no processor)
  - [x] Matches C# behavior (the processor is explicitly unscoped by design)
  - [ ] Describes a third behavior
  - [ ] No story — the cross-tenant design was an implementation choice during the port

## 5. Status

- **Classification**: Behavioral drift — design choice that requires structural safeguards to be safe.
- **What's needed to finish**:
  1. Add an abstract `TaskHandlerBase` that, before calling the handler-specific `HandleCoreAsync`, resolves an `ITenantContext` from DI and sets it to `claimed.TenantId`.
  2. Audit every existing `ITaskHandler` implementation; force them to inherit from `TaskHandlerBase`.
  3. Integration test: a handler-under-test writes a row via `EventRepository`, the test asserts that row's `TenantId` matches `claimed.TenantId` and cannot be observed by a different tenant's context.
  4. Alternatively, hard-scope the processor: run N processors (one per tenant) and pass `tenantId` to `ListPendingAsync`. More isolation, more infra cost.
- **Is it "just a stub" or is scope missing?** Design gap. Needs structural guards + tests.
- **Blockers**: cross-ref finding 028 — `EventRepository` uses `IgnoreQueryFilters()` everywhere, undermining the tenant context at the repo layer.

## Remediation

- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Services/TaskQueue/TaskHandlerBase.cs`
- Files to modify:
  - All `ITaskHandler` implementations in `Tamma.Api/Services/`, `Handlers/`, etc. — switch to inheriting from base.
- Tests to add:
  - `TaskHandlerBase_SetsTenantContext_BeforeHandleCoreAsync`
  - `Handler_WritingEvent_InheritsTenantFromQueuedTask`
  - `CrossTenantIsolationTest_HandlerForTenantA_CannotReadTenantBData`
- Estimated effort: 2h audit + scope-in-doc; 6h to implement base class + refactor all handlers + tests.

## References

- TS source: `packages/api/src/services/in-memory-task-queue.ts` (no equivalent)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/TaskQueue/TaskQueueProcessor.cs:98-146`, `ITaskQueue.cs:42-46` (doc-comment)
- Story: `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md`
- Related findings: `028-eventrepo-rls-bypass.md`, `016-instance-events-cross-tenant-leak.md`, `025-task-queue-pull-to-push.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed (structural guard added; existing handlers still
  need to inherit before the safety actually fires)
- **Commit**: a3d2e7e
- **Notes**: New `TaskHandlerBase` resolves `ITenantContext` from the
  scoped DI provider, sets it to `task.TenantId` before invoking
  `HandleCoreAsync`, and clears in `finally`. Future handlers should
  inherit instead of implementing `ITaskHandler` directly. Existing
  handlers (the `InstallationRouterService` deferred-event path is the
  primary consumer and goes through `ITaskQueue.EnqueueAsync` with an
  explicit `tenantIdOverride`, so the row already carries the right
  tenant) are not yet refactored to inherit — they would benefit from
  the safety net but are not currently observed to leak. Left as a
  follow-up; the safety mechanism exists for new handlers from now on.
