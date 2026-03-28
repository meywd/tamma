# Story 10.8: State Reconstruction from Event Stream

Status: ready-for-dev

## Story

As a **platform architect**,
I want system state (active workflows, pending approvals, issue statuses, cost totals, queue depth) to be derived entirely from the event stream rather than stored in separate mutable state, with periodic snapshots for performance optimization,
so that no component trusts its own memory, state survives restarts without corruption, and any state can be reconstructed to any point in time for debugging.

## Acceptance Criteria

1. `IStateProjection` interface defines how state is built by folding events sequentially
2. Engine state (active workflows, pending approvals, current issues) is derived from event stream via `ProjectionEngine`
3. No component stores authoritative state in memory — all state queries go through the projection
4. Periodic snapshots (STATE_SNAPSHOT event) are written to reduce reconstruction cost
5. State reconstruction path: load latest snapshot → fold events since snapshot → return current state
6. Reconstruction of typical workflow state (500 events, with snapshot) completes in <50ms
7. Multiple projection types supported: `ProjectState`, `WorkflowState`, `CostState`, `QueueState`
8. Projections are deterministic — same events always produce same state
9. Point-in-time state reconstruction: given a timestamp, reconstruct state as it was at that moment
10. Snapshot frequency is configurable (default: every 100 events or 5 minutes, whichever comes first)
11. Corrupted or missing snapshots are handled gracefully — full reconstruction from event stream as fallback
12. Projections are used by: engine brain (for decisions), API (for status queries), dashboard (for display)

## Technical Context

### Projection Architecture

```
Event Store (source of truth)
  │
  ├─ Events flow in chronologically
  │
  ▼
ProjectionEngine
  │
  ├─ Load latest STATE_SNAPSHOT (if exists)
  ├─ Query events since snapshot timestamp
  ├─ Fold each event through registered projections
  │
  ├── ProjectStateProjection
  │   └─ { activeWorkflows, pendingApprovals, issuesInProgress, recentActivity }
  │
  ├── WorkflowStateProjection
  │   └─ { instances: Map<id, { status, currentStep, startedAt, steps[] }> }
  │
  ├── CostStateProjection
  │   └─ { totalCostUsd, costByProvider, costByRole, budgetRemaining }
  │
  ├── QueueStateProjection
  │   └─ { depth, oldestItemAge, pendingIntents[], stalledSince? }
  │
  └─ Return composed state object
```

### Projection Interface

```typescript
interface IStateProjection<TState> {
  name: string;
  initialState(): TState;
  fold(state: TState, event: TammaEvent): TState;
  snapshotType: string; // Used to filter STATE_SNAPSHOT events
}

interface IProjectionEngine {
  // Build current state (snapshot + delta)
  getState<TState>(projection: IStateProjection<TState>): Promise<TState>;

  // Build state at specific point in time
  getStateAt<TState>(projection: IStateProjection<TState>, at: string): Promise<TState>;

  // Subscribe to real-time state updates
  subscribe<TState>(projection: IStateProjection<TState>, handler: (state: TState) => void): Subscription;

  // Force snapshot creation
  createSnapshot(projection: IStateProjection<unknown>): Promise<void>;
}

class ProjectionEngine implements IProjectionEngine {
  constructor(
    private eventStore: IEventStore,
    private snapshotConfig: SnapshotConfig,
  ) {}

  async getState<TState>(projection: IStateProjection<TState>): Promise<TState> {
    // 1. Try to load latest snapshot
    const snapshot = await this.eventStore.getLastSnapshot(projection.snapshotType);

    // 2. Determine starting state and time
    let state: TState;
    let since: string;

    if (snapshot) {
      state = snapshot.payload.state as TState;
      since = snapshot.timestamp;
    } else {
      state = projection.initialState();
      since = '1970-01-01T00:00:00.000Z'; // Beginning of time
    }

    // 3. Fold events since snapshot
    const events = await this.eventStore.query({
      since,
      orderBy: 'timestamp_asc',
    });

    for (const event of events) {
      state = projection.fold(state, event);
    }

    // 4. Maybe create snapshot (if enough events folded)
    if (events.length >= this.snapshotConfig.eventThreshold) {
      await this.createSnapshot(projection);
    }

    return state;
  }
}
```

### Projection Definitions

```typescript
// Project-level state (used by engine brain)
interface ProjectState {
  activeWorkflows: Array<{
    instanceId: string;
    workflowName: string;
    issueNumber: number;
    currentStep: string;
    startedAt: string;
  }>;
  pendingApprovals: Array<{
    type: string;
    issueNumber: number;
    planVersion: string;
    requestedAt: string;
  }>;
  issuesInProgress: Array<{
    issueNumber: number;
    title: string;
    status: string;
    assignedAt: string;
  }>;
  recentCompletions: Array<{
    issueNumber: number;
    prNumber: number;
    completedAt: string;
  }>;
  engineStatus: 'running' | 'stopped' | 'error';
  lastActivityAt: string;
}

// Cost tracking state
interface CostState {
  totalCostUsd: number;
  costByProvider: Record<string, number>;
  costByRole: Record<string, number>;
  costByIssue: Record<string, number>;
  budgetLimit: number;
  budgetRemaining: number;
  callCount: number;
  averageCostPerCall: number;
}

// Queue state
interface QueueState {
  depth: number;
  pendingIntents: Array<{
    intentId: string;
    type: string;
    priority: number;
    createdAt: string;
    deduplicationKey: string;
  }>;
  stalledSince: string | null;
  providerHealthy: boolean;
  itemsDispatchedTotal: number;
  itemsDroppedTotal: number;
}
```

### Snapshot Event

```typescript
// STATE_SNAPSHOT event payload
{
  eventType: 'STATE_SNAPSHOT',
  payload: {
    snapshotType: 'project_state', // or 'cost_state', 'queue_state'
    state: { /* serialized projection state */ },
    eventCountSinceLastSnapshot: 142,
    reconstructionTimeMs: 23,
  }
}
```

### Performance Strategy

```
Without snapshots (500 events):
  - Read 500 events: ~2ms (B-tree index)
  - Fold 500 events: ~3ms (in-memory)
  - Total: ~5ms ✓

Without snapshots (10,000 events):
  - Read 10,000 events: ~15ms
  - Fold 10,000 events: ~25ms
  - Total: ~40ms (acceptable but getting slow)

With snapshots (10,000 events, snapshot every 100):
  - Read 1 snapshot: ~1ms
  - Read ~100 events since snapshot: ~1ms
  - Fold 100 events: ~0.5ms
  - Total: ~2.5ms ✓

Rule: snapshot every 100 events OR 5 minutes, whichever first
```

## Tasks / Subtasks

- [ ] Task 1: Define projection interfaces and types (AC: 1, 7, 8)
  - [ ] Subtask 1.1: Define `IStateProjection<TState>` with `name`, `initialState()`, `fold()`, `snapshotType`
  - [ ] Subtask 1.2: Define `IProjectionEngine` with `getState()`, `getStateAt()`, `subscribe()`, `createSnapshot()`
  - [ ] Subtask 1.3: Define `SnapshotConfig` (eventThreshold, timeThreshold)
  - [ ] Subtask 1.4: Define state interfaces: `ProjectState`, `CostState`, `QueueState`

- [ ] Task 2: Implement ProjectionEngine (AC: 2, 3, 5, 11)
  - [ ] Subtask 2.1: Implement `getState()`: load snapshot → query delta events → fold → return
  - [ ] Subtask 2.2: Implement snapshot loading with graceful fallback (missing/corrupted → full rebuild)
  - [ ] Subtask 2.3: Implement auto-snapshot creation when event threshold exceeded
  - [ ] Subtask 2.4: Implement event store subscription for real-time projection updates
  - [ ] Subtask 2.5: Add caching layer (cache projection result, invalidate on new events)

- [ ] Task 3: Implement ProjectStateProjection (AC: 2, 12)
  - [ ] Subtask 3.1: Implement `fold()` for workflow events (WORKFLOW_STARTED → add to active, WORKFLOW_COMPLETED → remove)
  - [ ] Subtask 3.2: Implement `fold()` for approval events (PLAN_GENERATED → add pending, PLAN_APPROVED → remove)
  - [ ] Subtask 3.3: Implement `fold()` for issue events (ISSUE_FETCHED → add in progress, ISSUE_CLOSED → remove)
  - [ ] Subtask 3.4: Implement `fold()` for engine lifecycle events (ENGINE_STARTED, ENGINE_STOPPED)
  - [ ] Subtask 3.5: Wire into engine brain for decision-making context

- [ ] Task 4: Implement CostStateProjection (AC: 7)
  - [ ] Subtask 4.1: Implement `fold()` for LLM_CALL_COMPLETED (accumulate costs by provider, role, issue)
  - [ ] Subtask 4.2: Implement `fold()` for LLM_BUDGET_EXCEEDED
  - [ ] Subtask 4.3: Calculate running averages and budget remaining
  - [ ] Subtask 4.4: Wire into cost query API endpoint

- [ ] Task 5: Implement QueueStateProjection (AC: 7)
  - [ ] Subtask 5.1: Implement `fold()` for queue events (INTENT_QUEUED, INTENT_DISPATCHED, INTENT_DROPPED)
  - [ ] Subtask 5.2: Track stall state from QUEUE_STALLED/QUEUE_DRAINED events
  - [ ] Subtask 5.3: Wire into queue metrics endpoint

- [ ] Task 6: Implement periodic snapshotting (AC: 4, 10)
  - [ ] Subtask 6.1: Create snapshot scheduler (event count threshold + time threshold)
  - [ ] Subtask 6.2: Write STATE_SNAPSHOT event to event store
  - [ ] Subtask 6.3: Make thresholds configurable (default: 100 events or 5 minutes)
  - [ ] Subtask 6.4: Implement snapshot cleanup (keep last N snapshots per type, default 10)

- [ ] Task 7: Implement point-in-time reconstruction (AC: 9)
  - [ ] Subtask 7.1: Implement `getStateAt(projection, timestamp)` — load snapshot before timestamp, fold events up to timestamp
  - [ ] Subtask 7.2: Handle edge case: no snapshot before requested time → full rebuild up to timestamp
  - [ ] Subtask 7.3: Wire into time-travel debugging API endpoint

- [ ] Task 8: Wire projections into consumers (AC: 3, 12)
  - [ ] Subtask 8.1: Engine brain uses `ProjectionEngine.getState(projectStateProjection)` for context
  - [ ] Subtask 8.2: API status endpoint uses `ProjectionEngine.getState()` instead of engine memory
  - [ ] Subtask 8.3: Dashboard SSE uses `ProjectionEngine.subscribe()` for real-time updates
  - [ ] Subtask 8.4: Smart Queue re-validation uses projections for current state check

- [ ] Task 9: Testing (AC: all)
  - [ ] Subtask 9.1: Unit test each projection's `fold()` with representative event sequences
  - [ ] Subtask 9.2: Test determinism: same events → same state (run fold 100 times, verify identical)
  - [ ] Subtask 9.3: Test snapshot + delta reconstruction matches full reconstruction
  - [ ] Subtask 9.4: Test point-in-time reconstruction at various timestamps
  - [ ] Subtask 9.5: Test corrupted snapshot → graceful fallback to full rebuild
  - [ ] Subtask 9.6: Performance benchmark: 500 events in <50ms, 10K events with snapshot in <5ms
  - [ ] Subtask 9.7: Integration test: events appended → projection updates → API returns correct state

## Dev Notes

### Project Structure Notes

- New types: `packages/shared/src/types/projections.ts`
- New implementation: `packages/orchestrator/src/projections/projection-engine.ts`
- New implementation: `packages/orchestrator/src/projections/project-state.ts`
- New implementation: `packages/orchestrator/src/projections/cost-state.ts`
- New implementation: `packages/orchestrator/src/projections/queue-state.ts`
- Modified: `packages/orchestrator/src/brain/static-workflow.ts` (use projections for state)
- Modified: `packages/api/src/routes/engine/index.ts` (use projections for status queries)

### Key Design Decision: Projections Are Read-Only

Projections NEVER write back to the event store (except snapshots). They are pure functions: `(state, event) → newState`. This ensures:
- No circular dependencies (events → state → events)
- Deterministic replay
- Safe concurrent reads

### References

- **Epic 10 Tech Spec:** `docs/stories/epic-10/tech-spec-epic-10.md`
- **Story 10.2:** Event catalog defines all event types that projections fold
- **Story 10.3:** Event store provides query and subscription mechanisms
- **Story 10.1:** Engine brain consumes projected state for decisions
- **Story 4.7:** `docs/stories/epic-4/story-4-7/` (event query API)
- **Story 4.8:** `docs/stories/epic-4/story-4-8/` (black-box replay)

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
