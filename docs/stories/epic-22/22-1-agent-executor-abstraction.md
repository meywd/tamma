# Story 22.1: IAgentExecutor Abstraction

Status: planned

## Story

As a **platform engineer**,
I want a unified `IAgentExecutor` interface with `LocalExecutor` and `RemoteExecutor` implementations,
so that the `TammaEngine` pipeline can execute agent work without knowing whether agents run locally or on remote runners.

## Acceptance Criteria

1. `IAgentExecutor` interface defined in `packages/shared/src/types/` with `execute(task)`, `getStatus(taskId)`, `cancel(taskId)`, and `isAvailable()` methods
2. `LocalExecutor` implementation wraps existing `IAgentProvider` / `IRoleBasedAgentResolver` for local CLI agent execution (Claude Code, OpenCode on user's machine)
3. `RemoteExecutor` implementation dispatches GitHub Actions workflows via `IGitPlatform.dispatchWorkflow()` and polls for completion
4. `TammaEngine.EngineContext` accepts `IAgentExecutor` instead of directly accepting `IAgentProvider` and `IRoleBasedAgentResolver`
5. Backward compatibility: existing code that passes `agent` or `agentResolver` to `EngineContext` still works (adapter layer)
6. Config-driven selection: `config.mode === 'standalone'` produces `LocalExecutor`, `config.mode === 'saas'` produces `RemoteExecutor`
7. `ExecutionResult` type encapsulates outcome regardless of execution backend (success/failure, cost, duration, output, logs)
8. Unit tests achieve 90%+ coverage on both executor implementations
9. Integration test demonstrates swapping executors without changing engine code

## Technical Context

### Current Architecture

The `TammaEngine` (in `packages/orchestrator/src/engine.ts`) currently accepts agent providers in two ways:

```typescript
interface EngineContext {
  config: TammaConfig;
  platform: IGitPlatform;
  agent?: IAgentProvider;           // Single provider (legacy)
  agentResolver?: IRoleBasedAgentResolver;  // Multi-role resolver (preferred)
  logger: ILogger;
  eventStore?: IEventStore;
  onStateChange?: OnStateChangeCallback;
  approvalHandler?: ApprovalHandler;
}
```

The engine calls `getAgentForPhase()` internally, which resolves to either the single `agent` or the resolver chain. This tight coupling means:
- `generatePlan()` always calls `agent.executeTask()` directly
- `implementCode()` always calls `agent.executeTask()` directly
- There is no abstraction point where we could swap local execution for remote dispatch

### Target Architecture

```typescript
// packages/shared/src/types/agent-executor.ts

interface IAgentExecutor {
  /** Execute an agent task. Returns a task ID for tracking. */
  execute(task: AgentExecutionTask): Promise<AgentExecutionHandle>;

  /** Poll for task status and result. */
  getStatus(taskId: string): Promise<AgentExecutionStatus>;

  /** Cancel a running task. */
  cancel(taskId: string): Promise<void>;

  /** Check if this executor backend is available. */
  isAvailable(): Promise<boolean>;

  /** Release resources. */
  dispose(): Promise<void>;
}

interface AgentExecutionTask {
  /** Workflow phase this execution serves. */
  phase: WorkflowPhase;
  /** Agent role (resolved from phase if not provided). */
  role?: AgentType;
  /** The prompt / task config for the agent. */
  taskConfig: AgentTaskConfig;
  /** Context for tracing. */
  context: {
    issueNumber: number;
    projectId: string;
    engineId: string;
    branch?: string;
  };
}

interface AgentExecutionHandle {
  taskId: string;
  /** Resolves when execution completes. For LocalExecutor this is immediate.
   *  For RemoteExecutor this polls GitHub Actions. */
  result: Promise<AgentExecutionResult>;
}

interface AgentExecutionResult {
  success: boolean;
  output: string;
  error?: string;
  costUsd: number;
  durationMs: number;
  logs?: string[];
}

type AgentExecutionStatus =
  | { state: 'pending' }
  | { state: 'running'; progress?: string }
  | { state: 'completed'; result: AgentExecutionResult }
  | { state: 'failed'; error: string }
  | { state: 'cancelled' };
```

### LocalExecutor

Wraps the existing `IRoleBasedAgentResolver` (or a single `IAgentProvider`):

```typescript
// packages/orchestrator/src/executors/local-executor.ts

class LocalExecutor implements IAgentExecutor {
  constructor(
    private readonly agentResolver: IRoleBasedAgentResolver,
    private readonly logger: ILogger,
  ) {}

  async execute(task: AgentExecutionTask): Promise<AgentExecutionHandle> {
    const taskId = randomUUID();
    const agent = await this.agentResolver.getAgentForPhase(task.phase, {
      projectId: task.context.projectId,
      engineId: task.context.engineId,
    });

    const result = agent.executeTask(task.taskConfig).then(
      (r) => ({ success: r.success, output: r.output, costUsd: r.costUsd, durationMs: r.durationMs }),
    ).finally(() => agent.dispose());

    return { taskId, result };
  }
  // ...
}
```

### RemoteExecutor

Dispatches a `repository_dispatch` or `workflow_dispatch` event, then polls the Actions API:

```typescript
// packages/orchestrator/src/executors/remote-executor.ts

class RemoteExecutor implements IAgentExecutor {
  constructor(
    private readonly platform: IGitPlatform,
    private readonly config: RemoteExecutorConfig,
    private readonly logger: ILogger,
  ) {}

  async execute(task: AgentExecutionTask): Promise<AgentExecutionHandle> {
    const taskId = randomUUID();

    // Dispatch workflow
    await this.platform.dispatchWorkflow(
      this.config.owner,
      this.config.repo,
      this.config.workflowId,
      {
        taskId,
        phase: task.phase,
        issueNumber: task.context.issueNumber,
        branch: task.context.branch,
        taskConfig: JSON.stringify(task.taskConfig),
      },
    );

    // Return handle that polls for completion
    const result = this.pollForCompletion(taskId);
    return { taskId, result };
  }

  private async pollForCompletion(taskId: string): Promise<AgentExecutionResult> {
    // Poll GitHub Actions run status via platform API
    // ...
  }
}
```

### Files to Create

- `packages/shared/src/types/agent-executor.ts` -- interface and types
- `packages/orchestrator/src/executors/local-executor.ts` -- local implementation
- `packages/orchestrator/src/executors/local-executor.test.ts` -- unit tests
- `packages/orchestrator/src/executors/remote-executor.ts` -- remote implementation
- `packages/orchestrator/src/executors/remote-executor.test.ts` -- unit tests
- `packages/orchestrator/src/executors/index.ts` -- barrel export
- `packages/orchestrator/src/executor-factory.ts` -- config-driven factory
- `packages/orchestrator/src/executor-factory.test.ts` -- factory tests

### Files to Modify

- `packages/orchestrator/src/engine.ts` -- accept `IAgentExecutor` in `EngineContext`, refactor `getAgentForPhase()` to delegate to executor
- `packages/shared/src/types/index.ts` -- export new types
- `packages/cli/src/commands/start.tsx` -- construct executor from config and pass to engine
- `packages/cli/src/commands/process-issue.ts` -- same for worker mode

## Implementation Notes

1. **Backward compatibility is critical.** The `EngineContext` must continue to accept `agent?` and `agentResolver?` for existing code. When those are provided but `executor` is not, automatically wrap them in a `LocalExecutor`. This ensures zero breakage for existing callers.

2. **The LocalExecutor is a thin adapter.** It wraps the existing `IRoleBasedAgentResolver` pipeline (provider chain, health tracking, instrumentation, security). All existing behavior (budget clamping, tool clamping, content sanitization) is preserved because `LocalExecutor` delegates to the same stack.

3. **The RemoteExecutor needs `dispatchWorkflow()` on IGitPlatform.** This method may not exist yet. If missing, it must be added to the interface and implemented for `GitHubPlatform`. GitHub's API supports `POST /repos/{owner}/{repo}/actions/workflows/{workflow_id}/dispatches` -- use this. The method returns void; we get the run ID by querying the runs list after dispatch.

4. **ExecutionHandle.result is a Promise.** For `LocalExecutor`, this promise resolves when the agent finishes (synchronous from engine's perspective). For `RemoteExecutor`, this promise internally polls and resolves when the GitHub Actions run completes or times out.

5. **Config-driven factory pattern:**
   ```typescript
   function createExecutor(config: TammaConfig, deps: ExecutorDeps): IAgentExecutor {
     if (config.mode === 'standalone') {
       return new LocalExecutor(deps.agentResolver, deps.logger);
     }
     if (config.mode === 'saas') {
       return new RemoteExecutor(deps.platform, {
         owner: config.github.owner,
         repo: config.github.repo,
         workflowId: config.engine.remoteWorkflowId ?? 'tamma-process-issue.yml',
       }, deps.logger);
     }
     // Hybrid: local executor with optional cloud sync (Story 22.3)
     return new LocalExecutor(deps.agentResolver, deps.logger);
   }
   ```

6. **Progress reporting.** `LocalExecutor` can forward `AgentProgressEvent` from the underlying provider. `RemoteExecutor` can poll run logs from GitHub Actions for progress. Both surface progress through an optional callback on `execute()`.

7. **Error handling.** Both executors must translate their backend-specific errors into the common `AgentExecutionResult` shape. `LocalExecutor` catches `WorkflowError` from agent providers. `RemoteExecutor` catches HTTP errors from GitHub API calls.

## Dependencies

- `packages/providers/src/agent-types.ts` -- `IAgentProvider`, `AgentTaskConfig`
- `packages/providers/src/role-based-agent-resolver.ts` -- `IRoleBasedAgentResolver`
- `packages/platforms/` -- `IGitPlatform` (may need `dispatchWorkflow()` addition)
- `packages/shared/src/types/` -- `WorkflowPhase`, `AgentType`, `AgentTaskResult`

## Estimated Effort

**12 hours**

- Interface definition + types: 2 hours
- LocalExecutor + tests: 3 hours
- RemoteExecutor + tests: 4 hours
- Engine refactor + backward compat: 2 hours
- Factory + integration test: 1 hour

## Testing Strategy

- **Unit tests**: Mock `IRoleBasedAgentResolver` for `LocalExecutor`, mock `IGitPlatform` for `RemoteExecutor`. Test success, failure, cancellation, timeout, and progress callback.
- **Integration test**: Create both executors, verify same engine code works with either by swapping executor in config.
- **Backward compatibility test**: Construct `EngineContext` with `agent` and `agentResolver` (no `executor`), verify engine auto-wraps in `LocalExecutor`.
- **Error mapping test**: Verify `WorkflowError` from local agents and HTTP 500 from remote dispatch both produce consistent `AgentExecutionResult { success: false }`.

---

**Last Updated**: 2026-03-28
