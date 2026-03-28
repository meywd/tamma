# Story 10.4: Smart Queue with State-Based Deduplication

Status: ready-for-dev

## Story

As a **platform architect**,
I want a smart queue that holds workflow intents (triggers and signals), re-validates each intent against current event store state before dispatch, deduplicates based on live state, and holds items when the workflow provider is down,
so that the engine never dispatches duplicate or stale workflow requests, maintains ordering, and gracefully degrades when Elsa is unavailable.

## Acceptance Criteria

1. Smart Queue accepts intents of two types: `trigger_workflow` (start new) and `signal_workflow` (send signal to running)
2. Intents are persisted to event store immediately on enqueue (INTENT_QUEUED event) — survives engine restart
3. Before dispatching any intent, queue re-validates against current event store state by passing back through engine brain logic
4. Deduplication: if a workflow is already running for the same issue/context, the intent is dropped with INTENT_DROPPED event
5. Priority ordering: signals (approvals, resume) dispatch before triggers (new workflow starts)
6. When workflow provider is healthy, queue drains continuously — intents dispatched within 1 second of enqueue
7. When workflow provider is down, queue holds all intents — records QUEUE_STALLED event and notifies clients
8. When workflow provider recovers, queue re-validates ALL held intents (not just replays — state may have changed) before dispatching
9. Queue depth, oldest item age, and drain rate are exposed as metrics
10. Queue implements backpressure: when depth exceeds configurable limit, engine brain receives backpressure signal and can reject new triggers

## Technical Context

### Intent Types

```typescript
interface WorkflowIntent {
  intentId: string;          // UUID
  type: 'trigger_workflow' | 'signal_workflow';
  priority: number;          // Lower = higher priority. Signals: 10, Triggers: 50
  createdAt: string;         // ISO 8601
  correlationId: string;     // Links to originating user interaction

  // For trigger_workflow
  workflowName?: string;
  workflowInput?: Record<string, unknown>;

  // For signal_workflow
  workflowInstanceId?: string;
  signal?: string;
  signalPayload?: unknown;

  // Deduplication context
  deduplicationKey: string;  // e.g., "workflow:autonomous-dev:issue:42"

  // State at enqueue time (for re-validation comparison)
  stateAtEnqueue: {
    activeWorkflows: string[];
    pendingApprovals: string[];
  };
}
```

### Queue Processing Loop

```
QUEUE PROCESSOR (runs continuously when workflow provider is healthy):

1. HEALTH CHECK
   - Ping workflow provider
   - If unhealthy: record QUEUE_STALLED, wait, retry with backoff
   - If healthy: proceed

2. PEEK
   - Get highest-priority intent from queue
   - If empty: sleep briefly, loop

3. RE-VALIDATE
   - Load current state from event store
   - Pass intent + current state back through validation:
     "Is this intent still valid given what's happened since it was queued?"
   - Check deduplication key: is a workflow already running for this key?
   - If invalid/duplicate: record INTENT_DROPPED, remove from queue, loop
   - If valid: proceed

4. DISPATCH
   - Call IWorkflowProvider.startWorkflow() or .sendSignal()
   - Record INTENT_DISPATCHED event with workflow instance ID
   - Remove from queue
   - Loop

5. ERROR HANDLING
   - Dispatch failure: record error, mark intent for retry (max 3 retries)
   - After max retries: record INTENT_FAILED, remove from queue, notify client
```

### Re-validation Examples

**Scenario: User requests "start working on #42" twice in 5 seconds**
```
T=0: Intent queued: trigger autonomous-dev for #42
     deduplicationKey: "workflow:autonomous-dev:issue:42"
T=1: Queue dispatches intent, WORKFLOW_STARTED recorded
T=3: Second intent queued: trigger autonomous-dev for #42 (same dedup key)
T=4: Queue re-validates: checks event store → WORKFLOW_STARTED exists for #42
     → INTENT_DROPPED (reason: "workflow already running for issue #42")
```

**Scenario: Approval queued but plan changed before dispatch**
```
T=0: User approves plan for #42
     Intent queued: signal approval to workflow xyz
T=1: Elsa callback: plan was regenerated (PLAN_GENERATED with new plan)
T=2: Queue re-validates: checks state → plan version changed since approval
     → INTENT_DROPPED (reason: "plan changed since approval, re-approval needed")
     → Engine notifies user: "Plan was updated, please review the new plan"
```

**Scenario: Elsa goes down, comes back**
```
T=0: 3 intents queued while Elsa was healthy, dispatched normally
T=5: Elsa goes down. 2 new intents queued, held.
T=10: 1 more intent queued (duplicate of held intent). Also held.
T=15: Elsa recovers. Queue re-validates all 3 held intents:
      - Intent 1: still valid → dispatch
      - Intent 2: still valid → dispatch
      - Intent 3: duplicate of intent 1 → INTENT_DROPPED
```

## Tasks / Subtasks

- [ ] Task 1: Define Smart Queue interfaces and types (AC: 1, 5)
  - [ ] Subtask 1.1: Define `WorkflowIntent` type with all fields
  - [ ] Subtask 1.2: Define `ISmartQueue` interface: `enqueue(intent)`, `peek()`, `dequeue()`, `getDepth()`, `getMetrics()`
  - [ ] Subtask 1.3: Define `IIntentValidator` interface for re-validation logic
  - [ ] Subtask 1.4: Define priority constants (signals=10, triggers=50, configurable)
  - [ ] Subtask 1.5: Define deduplication key generation strategy per workflow type

- [ ] Task 2: Implement queue storage (AC: 2)
  - [ ] Subtask 2.1: Queue state derived from event store (INTENT_QUEUED minus INTENT_DISPATCHED minus INTENT_DROPPED)
  - [ ] Subtask 2.2: In-memory priority queue as hot cache, event store as durable backing
  - [ ] Subtask 2.3: On engine restart: rebuild queue from event store (unresolved intents)
  - [ ] Subtask 2.4: Record INTENT_QUEUED event on every enqueue

- [ ] Task 3: Implement re-validation and deduplication (AC: 3, 4, 8)
  - [ ] Subtask 3.1: Implement `IntentValidator` that loads current state from event store
  - [ ] Subtask 3.2: Check deduplication key against active workflows
  - [ ] Subtask 3.3: Check for state drift (e.g., plan changed since approval intent was queued)
  - [ ] Subtask 3.4: Record INTENT_REVALIDATED event with `stillValid` flag
  - [ ] Subtask 3.5: Record INTENT_DROPPED event when intent is no longer valid

- [ ] Task 4: Implement queue processor (AC: 6, 7)
  - [ ] Subtask 4.1: Create `QueueProcessor` class with continuous drain loop
  - [ ] Subtask 4.2: Implement workflow provider health checking with configurable interval
  - [ ] Subtask 4.3: Implement dispatch to `IWorkflowProvider.startWorkflow()` and `.sendSignal()`
  - [ ] Subtask 4.4: Handle dispatch errors with retry (max 3, exponential backoff)
  - [ ] Subtask 4.5: Record QUEUE_STALLED when provider is down, QUEUE_DRAINED on recovery
  - [ ] Subtask 4.6: Implement notification to clients when queue is stalled/recovered

- [ ] Task 5: Implement backpressure (AC: 10)
  - [ ] Subtask 5.1: Configure max queue depth (default: 100)
  - [ ] Subtask 5.2: When depth exceeded, return backpressure signal to engine brain
  - [ ] Subtask 5.3: Engine brain can reject new triggers with "system busy, try later"
  - [ ] Subtask 5.4: Record QUEUE_BACKPRESSURE event

- [ ] Task 6: Implement metrics (AC: 9)
  - [ ] Subtask 6.1: Expose queue depth (current pending items)
  - [ ] Subtask 6.2: Expose oldest item age (time since oldest intent was queued)
  - [ ] Subtask 6.3: Expose drain rate (intents dispatched per minute)
  - [ ] Subtask 6.4: Expose drop rate (intents dropped per minute)
  - [ ] Subtask 6.5: Wire metrics into engine health check endpoint

- [ ] Task 7: Testing (AC: all)
  - [ ] Subtask 7.1: Unit test enqueue records INTENT_QUEUED event
  - [ ] Subtask 7.2: Unit test re-validation drops duplicate intents
  - [ ] Subtask 7.3: Unit test priority ordering (signals before triggers)
  - [ ] Subtask 7.4: Unit test queue holds when provider is down
  - [ ] Subtask 7.5: Unit test re-validation on provider recovery
  - [ ] Subtask 7.6: Unit test queue rebuild from event store on restart
  - [ ] Subtask 7.7: Unit test backpressure at max depth
  - [ ] Subtask 7.8: Integration test: enqueue -> dispatch -> verify workflow started
  - [ ] Subtask 7.9: Integration test: provider down -> queue holds -> provider up -> dispatch

## Dev Notes

### Project Structure Notes

- New types: `packages/shared/src/types/smart-queue.ts`
- New implementation: `packages/orchestrator/src/queue/smart-queue.ts`
- New implementation: `packages/orchestrator/src/queue/queue-processor.ts`
- New implementation: `packages/orchestrator/src/queue/intent-validator.ts`
- Modified: `packages/orchestrator/src/engine.ts` (inject ISmartQueue)

### References

- **Epic 10 Tech Spec:** `docs/stories/epic-10/tech-spec-epic-10.md`
- **Story 10.1:** Engine brain routes decisions to queue
- **Story 10.3:** Event store used for durable queue backing
- **Story 10.5:** IWorkflowProvider that queue dispatches to

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-26 | 1.0 | Initial story creation | Architecture Team |

## Logging Requirements

Engine core is the most critical path — logging must be comprehensive without being noisy.

- **INFO**: Engine started/stopped, workflow dispatched (workflow ID, issue ID), step transition (from state -> to state), queue item enqueued/dequeued
- **DEBUG**: State reconstruction details, event replay progress, queue deduplication decisions, ELSA workflow variable snapshots
- **WARN**: Queue backpressure detected, state reconstruction took >5s, event gap in stream, workflow execution slow
- **ERROR**: Engine crash (with full context for restart), state reconstruction failed, event store unreachable, workflow dispatch failed, queue corruption
- **Structured context**: Always include `{ workflowInstanceId, issueId, engineState, queueDepth }`
- **Idempotency**: Log enough context to verify idempotent replay (event IDs, sequence numbers, dedup keys)
