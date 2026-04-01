---
title: "Story 22.2: CLI Standalone Workflow Engine"
sidebar:
  order: 220
---

Status: planned

## Story

As a **developer using Tamma locally**,
I want ELSA workflows to run without cloud dependencies,
so that `tamma start` works completely standalone with a local-only event store and no requirement for a remote ELSA server or Tamma Cloud account.

## Acceptance Criteria

1. `tamma start` runs the full issue-to-PR pipeline without requiring an external ELSA server when `config.mode === 'standalone'`
2. ELSA workflows can execute via either a local embedded engine or a sidecar Docker container, selected by config
3. A `LocalEventStore` implementation persists events to a file (`~/.tamma/events.jsonl`) for local audit trail and debugging
4. The `IWorkflowEngine` interface is satisfied by both the remote `ElsaClient` (existing) and a new `LocalWorkflowAdapter` that wraps the `TammaEngine` pipeline directly
5. No internet connectivity is required beyond the target Git platform (GitHub/GitLab)
6. No Tamma Cloud account is required
7. No external database (PostgreSQL) is required for standalone mode -- SQLite or file-based storage only
8. The same ELSA workflow definitions work in both standalone and SaaS modes
9. Unit tests for `LocalWorkflowAdapter` and `LocalEventStore` achieve 90%+ coverage
10. Integration test: `tamma start --once --dry-run` completes with `LocalWorkflowAdapter` and `LocalEventStore` without any external services

## Technical Context

### Current Architecture

The orchestrator currently has two paths:

1. **Direct engine** (`TammaEngine`): Runs the select-analyze-plan-approve-implement-PR-merge pipeline directly in Node.js. This is what `tamma start` uses today. No ELSA involvement.

2. **ELSA client** (`ElsaClient`): Talks to a remote ELSA Server via HTTP REST API. Used when `config.elsa` is configured. Requires a running ELSA .NET server (typically via Docker).

The gap: when ELSA workflows are the canonical orchestration layer (Epic 10), standalone CLI users would be forced to run a Docker-based ELSA server. This story ensures that is never required.

### Design: Two Standalone Strategies

#### Strategy A: LocalWorkflowAdapter (No ELSA)

For users who do not want to run Docker at all, the `LocalWorkflowAdapter` implements `IWorkflowEngine` by mapping workflow operations directly to `TammaEngine` pipeline steps:

```typescript
// packages/orchestrator/src/workflow-adapters/local-workflow-adapter.ts

class LocalWorkflowAdapter implements IWorkflowEngine {
  constructor(
    private readonly engine: TammaEngine,
    private readonly logger: ILogger,
  ) {}

  async startWorkflow(name: string, input: Record<string, unknown>): Promise<string> {
    // Map ELSA workflow name to engine pipeline method
    const instanceId = randomUUID();
    // Start the pipeline in background, track by instanceId
    void this.engine.processOneIssue();
    return instanceId;
  }

  async getWorkflowStatus(instanceId: string): Promise<WorkflowInstanceStatus> {
    const state = this.engine.getState();
    return {
      instanceId,
      definitionId: 'tamma-issue-pipeline',
      status: this.mapEngineState(state),
      variables: {},
    };
  }

  // pause, resume, cancel delegate to engine
  // sendSignal maps to approval handler
}
```

#### Strategy B: Sidecar ELSA (Docker)

For users who want full ELSA workflow fidelity, a helper command `tamma elsa:start` can launch the ELSA Docker container as a sidecar, then `ElsaClient` connects to `localhost:13000`. This is optional and documented, not required.

### LocalEventStore (File-Backed)

Replace the in-memory `IEventStore` with a file-backed implementation for standalone mode:

```typescript
// packages/orchestrator/src/event-stores/local-event-store.ts

class LocalEventStore implements IEventStore {
  private events: EngineEvent[] = [];
  private readonly filePath: string;

  constructor(storagePath?: string) {
    this.filePath = storagePath ?? path.join(os.homedir(), '.tamma', 'events.jsonl');
    this.loadFromDisk();
  }

  record(event: Omit<EngineEvent, 'id' | 'timestamp'>): EngineEvent {
    const full: EngineEvent = {
      id: randomUUID(),
      timestamp: new Date().toISOString(),
      ...event,
    };
    this.events.push(full);
    this.appendToDisk(full);
    return full;
  }

  getEvents(issueNumber?: number): EngineEvent[] {
    if (issueNumber !== undefined) {
      return this.events.filter(e => e.issueNumber === issueNumber);
    }
    return [...this.events];
  }

  getLastEvent(type: EngineEventType): EngineEvent | undefined {
    for (let i = this.events.length - 1; i >= 0; i--) {
      if (this.events[i]!.type === type) return this.events[i];
    }
    return undefined;
  }

  clear(): void {
    this.events = [];
    // Truncate file
  }

  private loadFromDisk(): void {
    // Read JSONL file line-by-line, parse each as EngineEvent
  }

  private appendToDisk(event: EngineEvent): void {
    // Append JSON line to file (atomic write with fsync)
  }
}
```

### Standalone Config Detection

The engine factory inspects config to determine which components to use:

```typescript
function createStandaloneEngine(config: TammaConfig, deps: EngineDeps): TammaEngine {
  // Event store: file-backed for standalone, in-memory for dry-run
  const eventStore = config.engine.dryRun
    ? new InMemoryEventStore()
    : new LocalEventStore();

  // Workflow adapter: local (no ELSA) unless config.elsa is set
  const workflowEngine = config.elsa
    ? new ElsaClient(config.elsa)
    : undefined; // Engine runs pipeline directly

  // Agent executor: always local for standalone mode
  const executor = new LocalExecutor(deps.agentResolver, deps.logger);

  return new TammaEngine({
    config,
    platform: deps.platform,
    executor,
    logger: deps.logger,
    eventStore,
    onStateChange: deps.onStateChange,
    approvalHandler: deps.approvalHandler,
  });
}
```

### Files to Create

- `packages/orchestrator/src/event-stores/local-event-store.ts` -- file-backed event store
- `packages/orchestrator/src/event-stores/local-event-store.test.ts` -- tests
- `packages/orchestrator/src/event-stores/in-memory-event-store.ts` -- extract existing in-memory impl to standalone class
- `packages/orchestrator/src/event-stores/in-memory-event-store.test.ts` -- tests
- `packages/orchestrator/src/event-stores/index.ts` -- barrel export
- `packages/orchestrator/src/workflow-adapters/local-workflow-adapter.ts` -- local ELSA alternative
- `packages/orchestrator/src/workflow-adapters/local-workflow-adapter.test.ts` -- tests
- `packages/orchestrator/src/workflow-adapters/index.ts` -- barrel export
- `packages/orchestrator/src/standalone-factory.ts` -- creates standalone engine with all local deps
- `packages/orchestrator/src/standalone-factory.test.ts` -- tests

### Files to Modify

- `packages/orchestrator/src/engine.ts` -- accept `IAgentExecutor` (from Story 22.1), use `eventStore` if provided
- `packages/cli/src/commands/start.tsx` -- use `createStandaloneEngine()` for standalone mode
- `packages/shared/src/types/index.ts` -- export `IEventStore` implementations (if types change)

## Implementation Notes

1. **The LocalEventStore uses JSONL format** (one JSON object per line). This is append-only, crash-safe (partial writes are detectable), and trivially parseable. The file is stored at `~/.tamma/events.jsonl` by default, configurable via `config.engine.eventStorePath`.

2. **No SQLite required for MVP.** The JSONL file store is sufficient for standalone CLI use. If performance becomes an issue with very large event histories (10k+ events), a SQLite backend can be added later as another `IEventStore` implementation. The interface is already clean enough for this.

3. **The LocalWorkflowAdapter is a compatibility shim, not a full ELSA port.** It does not execute BPMN or ELSA activity graphs. It maps the `IWorkflowEngine` interface to the existing `TammaEngine` pipeline steps. This means standalone mode runs the same pipeline logic as today, but through the `IWorkflowEngine` interface so code that depends on that interface works in both modes.

4. **File permissions**: The `~/.tamma/` directory and `events.jsonl` file are created with `0700` (directory) and `0600` (file) permissions to protect event data which may contain issue content.

5. **Startup detection**: The `start` command should detect the current mode and log it clearly:
   ```
   Tamma engine starting in standalone mode
     Event store: ~/.tamma/events.jsonl
     Workflow engine: local (no ELSA server required)
     Agent executor: local (Claude Code)
   ```

6. **JSONL rotation**: For long-running standalone instances, events older than 30 days can be rotated to `events.jsonl.1`, `events.jsonl.2`, etc. (configurable via `config.engine.eventRetentionDays`). Not required for MVP but the file structure supports it.

7. **Event replay for debugging**: `tamma events list` and `tamma events replay <issueNumber>` commands can read the JSONL file to provide time-travel debugging in standalone mode. This is a future enhancement, not in scope for this story, but the data format supports it.

## Dependencies

- **Story 22.1**: `IAgentExecutor` and `LocalExecutor` must exist before this story can wire them into `createStandaloneEngine()`
- `packages/orchestrator/src/engine.ts` -- `TammaEngine`, `EngineContext`
- `packages/orchestrator/src/workflow-engine.ts` -- `IWorkflowEngine` interface
- `packages/shared/src/types/index.ts` -- `IEventStore`, `EngineEvent`, `EngineEventType`

## Estimated Effort

**16 hours**

- LocalEventStore (JSONL) + tests: 4 hours
- InMemoryEventStore extraction + tests: 1 hour
- LocalWorkflowAdapter + tests: 4 hours
- Standalone factory + config detection: 3 hours
- Start command integration + mode logging: 2 hours
- Integration test (end-to-end standalone): 2 hours

## Testing Strategy

- **Unit tests (LocalEventStore)**: Write events, read back, filter by issue number, verify JSONL format on disk, test `clear()`, test crash recovery (partial line at EOF), test file permission enforcement.
- **Unit tests (LocalWorkflowAdapter)**: Mock `TammaEngine`, verify `startWorkflow()` triggers pipeline, `getWorkflowStatus()` maps engine state correctly, `cancel()` calls engine dispose, `sendSignal()` resolves approval.
- **Unit tests (standalone factory)**: Verify factory produces correct components for `mode: 'standalone'` vs `mode: 'saas'` configs.
- **Integration test**: Run `tamma start --once --dry-run` with a mock GitHub platform, verify full pipeline completes using only local components (no Docker, no ELSA, no PostgreSQL).
- **Regression test**: Ensure existing `tamma start` behavior (service mode, interactive mode, dry-run) is unchanged when `config.mode === 'standalone'`.

---

**Last Updated**: 2026-03-28
