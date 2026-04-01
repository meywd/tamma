---
title: "Story 10.5: Workflow Provider Abstraction & Elsa Integration"
sidebar:
  order: 100
---

Status: ready-for-dev

## Story

As a **platform architect**,
I want Elsa (and any future workflow engine) accessed through a clean `IWorkflowProvider` abstraction that the engine never bypasses, with Elsa as the first concrete implementation,
so that the workflow engine is a replaceable provider and the engine has zero direct coupling to Elsa-specific types, APIs, or concepts.

## Acceptance Criteria

1. `IWorkflowProvider` interface defines all workflow operations: start, signal, pause, resume, cancel, getStatus, listActive
2. Engine and Smart Queue interact with workflows ONLY through `IWorkflowProvider` — no Elsa imports anywhere outside the provider implementation
3. `ElsaWorkflowProvider` implements `IWorkflowProvider` by communicating with the Elsa Server REST API
4. Provider includes health checking with circuit breaker pattern (healthy/degraded/unhealthy states)
5. Elsa writes events to the shared event store (WORKFLOW_STARTED, WORKFLOW_STEP_COMPLETED, etc.) via callback to engine
6. Elsa callbacks arrive through the existing `/api/engine/callback` route and are processed as engine intake events (Story 10.1)
7. Provider is configured via dependency injection — swappable without code changes
8. Provider handles Elsa-specific concepts (bookmarks, activity execution, workflow definitions) internally — none leak through the interface
9. `InProcessWorkflowProvider` exists as a simple fallback that executes workflow steps in-process (for testing or when no external workflow engine is available)
10. Provider emits events for: connection established, connection lost, health status changes

## Technical Context

### Interface Definition

```typescript
interface IWorkflowProvider {
  // Lifecycle
  initialize(): Promise<void>;
  dispose(): Promise<void>;

  // Health
  getHealth(): Promise<WorkflowProviderHealth>;
  isHealthy(): Promise<boolean>;

  // Workflow management
  startWorkflow(name: string, input: Record<string, unknown>): Promise<WorkflowInstance>;
  sendSignal(instanceId: string, signal: string, payload?: unknown): Promise<void>;
  pauseWorkflow(instanceId: string): Promise<void>;
  resumeWorkflow(instanceId: string): Promise<void>;
  cancelWorkflow(instanceId: string, reason: string): Promise<void>;

  // Status
  getWorkflowStatus(instanceId: string): Promise<WorkflowStatus>;
  listActiveWorkflows(filter?: WorkflowFilter): Promise<WorkflowInstance[]>;

  // Definitions
  listWorkflowDefinitions(): Promise<WorkflowDefinition[]>;
  getWorkflowDefinition(name: string): Promise<WorkflowDefinition | null>;
}

interface WorkflowProviderHealth {
  status: 'healthy' | 'degraded' | 'unhealthy';
  latencyMs: number;
  lastCheckedAt: string;
  consecutiveFailures: number;
  details?: Record<string, unknown>;
}

interface WorkflowInstance {
  instanceId: string;
  definitionName: string;
  status: 'running' | 'paused' | 'completed' | 'failed' | 'cancelled';
  currentStep?: string;
  startedAt: string;
  updatedAt: string;
  input: Record<string, unknown>;
  output?: Record<string, unknown>;
}

interface WorkflowStatus {
  instance: WorkflowInstance;
  steps: WorkflowStepInfo[];
  signals: WorkflowSignalInfo[];
}

interface WorkflowStepInfo {
  name: string;
  status: 'pending' | 'running' | 'completed' | 'failed' | 'skipped';
  startedAt?: string;
  completedAt?: string;
  durationMs?: number;
  output?: unknown;
}

interface WorkflowFilter {
  definitionName?: string;
  status?: string;
  issueId?: string;
  since?: string;
}
```

### Elsa REST API Mapping

| IWorkflowProvider Method | Elsa REST API |
|--------------------------|---------------|
| `startWorkflow(name, input)` | `POST /elsa/api/workflow-definitions/{name}/execute` |
| `sendSignal(id, signal, payload)` | `POST /elsa/api/signals/{signal}/execute` |
| `pauseWorkflow(id)` | `POST /elsa/api/workflow-instances/{id}/suspend` |
| `resumeWorkflow(id)` | `POST /elsa/api/workflow-instances/{id}/resume` |
| `cancelWorkflow(id)` | `POST /elsa/api/workflow-instances/{id}/cancel` |
| `getWorkflowStatus(id)` | `GET /elsa/api/workflow-instances/{id}` |
| `listActiveWorkflows(filter)` | `GET /elsa/api/workflow-instances?status=Running` |
| `listWorkflowDefinitions()` | `GET /elsa/api/workflow-definitions` |
| `getHealth()` | `GET /elsa/api/health` (custom endpoint) |

### Circuit Breaker Pattern

```
HEALTHY (default)
  │
  ├─ On failure (3 consecutive) ──► DEGRADED
  │                                    │
  │                                    ├─ On failure (5 more) ──► UNHEALTHY
  │                                    │                            │
  │                                    ├─ On success ──────────────► HEALTHY
  │                                    │
  │                                    └─ Check every 10s
  │
  └─ On success ──► stay HEALTHY

UNHEALTHY
  │
  ├─ Check every 30s
  ├─ On success ──► DEGRADED (then HEALTHY on next success)
  └─ Smart Queue holds all intents
```

### Callback Flow (Elsa → Engine)

When Elsa completes a workflow step, it calls back to the engine:

```
Elsa Activity completes
  │
  ├─ POST /api/engine/callback
  │   Body: { type: "step_completed", instanceId, step, result }
  │
  ├─ Engine normalizes to NormalizedInput { type: 'workflow_callback' }
  │
  ├─ Processed through static workflow (Story 10.1):
  │   - Records WORKFLOW_STEP_COMPLETED event
  │   - Brain decides: trigger next step? wait? notify user?
  │
  └─ If approval needed: Brain notifies user
     If next step: Brain signals workflow to continue
     If error: Brain records and notifies
```

### Relationship to Existing Code

| Existing | Action |
|----------|--------|
| `IWorkflowEngine` in `workflow-engine.ts` | **Evolved** into `IWorkflowProvider` with richer interface |
| `ElsaClient` in `elsa-client.ts` | **Replaced** by `ElsaWorkflowProvider` with health, circuit breaker |
| Callback route in `engine-callback.ts` | **Preserved** — intake normalized through Story 10.1 |

## Tasks / Subtasks

- [ ] Task 1: Define IWorkflowProvider interface (AC: 1, 8)
  - [ ] Subtask 1.1: Define `IWorkflowProvider` with all workflow operations
  - [ ] Subtask 1.2: Define `WorkflowProviderHealth` with circuit breaker states
  - [ ] Subtask 1.3: Define `WorkflowInstance`, `WorkflowStatus`, `WorkflowStepInfo` types
  - [ ] Subtask 1.4: Define `WorkflowFilter` for listing active workflows
  - [ ] Subtask 1.5: Ensure no Elsa-specific concepts in interface (no bookmarks, activities, etc.)

- [ ] Task 2: Implement ElsaWorkflowProvider (AC: 3, 5, 8)
  - [ ] Subtask 2.1: Implement HTTP client wrapping Elsa REST API
  - [ ] Subtask 2.2: Map IWorkflowProvider methods to Elsa REST endpoints
  - [ ] Subtask 2.3: Translate Elsa-specific response formats to `WorkflowInstance`/`WorkflowStatus`
  - [ ] Subtask 2.4: Handle Elsa authentication (API key via config)
  - [ ] Subtask 2.5: Configure base URL, timeout, and retry settings via config

- [ ] Task 3: Implement circuit breaker health checking (AC: 4, 10)
  - [ ] Subtask 3.1: Implement health check ping (GET /elsa/api/health or similar)
  - [ ] Subtask 3.2: Track consecutive failures and state transitions (healthy/degraded/unhealthy)
  - [ ] Subtask 3.3: Configure thresholds: degraded at 3 failures, unhealthy at 8 failures
  - [ ] Subtask 3.4: Record PROVIDER_HEALTH_CHANGED event on state transitions
  - [ ] Subtask 3.5: Expose health status to Smart Queue for stall decisions

- [ ] Task 4: Implement callback processing (AC: 5, 6)
  - [ ] Subtask 4.1: Update `/api/engine/callback` route to normalize callbacks as EngineIntakeEvent
  - [ ] Subtask 4.2: Map callback types to event store events (WORKFLOW_STEP_COMPLETED, etc.)
  - [ ] Subtask 4.3: Route callbacks through engine static workflow for brain processing
  - [ ] Subtask 4.4: Handle callback authentication (validate API key from Elsa)

- [ ] Task 5: Implement InProcessWorkflowProvider (AC: 9)
  - [ ] Subtask 5.1: Simple provider that executes workflow logic in-process (for testing)
  - [ ] Subtask 5.2: Implements same IWorkflowProvider interface
  - [ ] Subtask 5.3: Records same workflow events to event store
  - [ ] Subtask 5.4: Useful for integration testing without Elsa running

- [ ] Task 6: Wire dependency injection (AC: 2, 7)
  - [ ] Subtask 6.1: Register IWorkflowProvider in engine DI container
  - [ ] Subtask 6.2: Configure provider selection via config (elsa, in-process)
  - [ ] Subtask 6.3: Verify zero Elsa imports outside `ElsaWorkflowProvider` class
  - [ ] Subtask 6.4: Wire provider into Smart Queue for dispatch

- [ ] Task 7: Migrate from existing code (AC: all)
  - [ ] Subtask 7.1: Deprecate `IWorkflowEngine` interface (replace with `IWorkflowProvider`)
  - [ ] Subtask 7.2: Deprecate `ElsaClient` class (replace with `ElsaWorkflowProvider`)
  - [ ] Subtask 7.3: Update all imports to use new provider
  - [ ] Subtask 7.4: Remove old `workflow-engine.ts` and `elsa-client.ts` after migration

- [ ] Task 8: Testing (AC: all)
  - [ ] Subtask 8.1: Unit test ElsaWorkflowProvider with mocked HTTP client
  - [ ] Subtask 8.2: Unit test circuit breaker state transitions
  - [ ] Subtask 8.3: Unit test callback normalization to intake events
  - [ ] Subtask 8.4: Unit test InProcessWorkflowProvider
  - [ ] Subtask 8.5: Integration test: start workflow -> callback -> event recorded
  - [ ] Subtask 8.6: Integration test: circuit breaker opens on Elsa failure, closes on recovery

## Dev Notes

### Project Structure Notes

- New types: `packages/shared/src/types/workflow-provider.ts`
- New implementation: `packages/orchestrator/src/providers/elsa-workflow-provider.ts`
- New implementation: `packages/orchestrator/src/providers/in-process-workflow-provider.ts`
- New implementation: `packages/orchestrator/src/providers/workflow-health.ts`
- Deprecated: `packages/orchestrator/src/workflow-engine.ts` → replaced
- Deprecated: `packages/orchestrator/src/elsa-client.ts` → replaced
- Modified: `packages/api/src/routes/engine-callback.ts` (normalize to intake)

### References

- **Epic 10 Tech Spec:** `docs/stories/epic-10/tech-spec-epic-10.md`
- **Current IWorkflowEngine:** `packages/orchestrator/src/workflow-engine.ts`
- **Current ElsaClient:** `packages/orchestrator/src/elsa-client.ts`
- **Callback Route:** `packages/api/src/routes/engine-callback.ts`
- **Elsa Server:** `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs`
- **Story 10.1:** Engine brain processes callbacks
- **Story 10.4:** Smart Queue dispatches via this provider

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
