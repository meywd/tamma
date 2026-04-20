# Story 19-5: CLI / SaaS Mode Abstraction (IAgentExecutor)

Status: done (all ACs complete — AC-6 landed as follow-up)

## Implementation Notes

- Shared types (`AgentExecutionRequest` / `AgentExecutionResult` / `ExecutionMode`) and the `IAgentExecutor` interface landed in `67901c3`.
- `LocalExecutor` shells out to the Tamma CLI (Node) over a JSON request/result-file protocol. The TypeScript `execute-agent` CLI command is a follow-up story — the C# side is complete and the protocol is documented in `LocalExecutor.cs`. `IProcessRunner` + `DefaultProcessRunner` cleanly separate the process-spawning concern.
- `GitHubActionsExecutor` chose "Option B" (service reuse) over programmatically executing Elsa activities. The three phase services (`IAgentDispatchService`, `IAgentMonitorService`, `IAgentResultCollectorService`) are the shared substrate.
- `AgentExecutorFactory` precedence: `modeOverride` > `TAMMA_AGENT_MODE` env > `Agent:ExecutorMode` config > auto-detect (GitHub App configured -> github_actions).
- `ExecuteAgentActivity` is the single-activity Elsa wrapper (AC-5). DI wired in both `Tamma.Api/Program.cs` (Octokit impl when GitHub App configured, Null impl otherwise) and `Tamma.ElsaServer/Program.cs` (Null impl; ElsaServer doesn't reference Tamma.Api).
- AC coverage: 1, 2, 3, 4, 5, 6, 7, 8 — **all done**.
- Commits:
  - `a0963d8` `feat(agent-dispatch): Local + GitHubActions executors + ExecuteAgentActivity [story 19-5]` (AC-1..5, 7, 8)
  - Tests in `fa314c9`.
  - `8bdf860` `refactor(workflows): swap direct agent dispatch for ExecuteAgentActivity [story 19-5 AC-6]` — replaces the per-task `TddForTask` `DispatchWorkflow(tdd-cycle)` in `SingleIssueCycleWorkflow` with the mode-aware `ExecuteAgentActivity`. The `tdd-cycle` + `tdd-with-debug-retry` workflows remain registered (still consumed by `MentorshipWorkflow`) so there is no sub-workflow breakage.
  - `cdfb7c1` `test(workflows): verify ExecuteAgentActivity wiring in SingleIssueCycle [story 19-5 AC-6]` — 8 new structural tests in `SingleIssueCycleRoutingTests` asserting the `TddForTask` activity is an `ExecuteAgentActivity`, that connections into/out of it (including `Completed`/`Failed` outcomes looping back to `IncrementTask`) are correct, and that required inputs (`Task`, `AgentProvider`, `TimeoutMinutes`) are configured. Test count: 1782 → 1790.
- AC-6 scope cap: only the per-task agent-execution site in `SingleIssueCycleWorkflow` was migrated. `DebuggingWorkflow`, `ReviewFixWorkflow`, `llm-call`-based sub-workflows (`PlanGenerationWorkflow`, `TaskCreationWorkflow`, review panels, etc.), and the inner `TddWorkflow` activities (`WriteTestsActivity`, `WriteImplementationActivity`, refactor activities) are **not** swapped — they perform additional orchestration that `ExecuteAgentActivity` does not model at the same granularity. If a future story needs mode-aware invocation for those sites, each should be assessed individually rather than force-fitted through `ExecuteAgentActivity`.

## Story

As a **platform architect**,
I want an `IAgentExecutor` interface with `LocalExecutor` (CLI mode, runs agent in the same process/machine) and `GitHubActionsExecutor` (SaaS mode, dispatches to user's runner),
so that the ELSA workflows operate identically regardless of deployment mode, and the execution strategy is a configuration choice rather than a code change.

## Acceptance Criteria

1. An `IAgentExecutor` interface is defined with a single method:
   ```
   ExecuteAsync(request: AgentExecutionRequest): Promise<AgentExecutionResult>
   ```
2. `LocalExecutor` implements `IAgentExecutor`:
   - Runs the agent (Claude Code CLI) locally as a child process
   - Collects output from the agent's stdout/result file
   - Returns the same `AgentExecutionResult` schema as `GitHubActionsExecutor`
   - Works in CLI mode (`tamma start`) and self-hosted mode (`tamma server`)
3. `GitHubActionsExecutor` implements `IAgentExecutor`:
   - Internally orchestrates Stories 19-2 (dispatch), 19-3 (monitor), 19-4 (collect)
   - Returns `AgentExecutionResult`
   - Works in SaaS mode (`tamma api`) when the GitHub App is installed
4. A factory or configuration-based resolver selects the correct executor:
   - CLI mode / self-hosted: `LocalExecutor`
   - SaaS / GitHub App: `GitHubActionsExecutor`
   - Configurable via environment variable or ELSA agent config
5. An ELSA activity `ExecuteAgentActivity` wraps `IAgentExecutor`:
   - Replaces direct agent invocation in workflows
   - Accepts the same inputs as the TDD/implementation activities
   - Outputs `AgentExecutionResult`
6. The `SingleIssueCycleWorkflow` (or its sub-workflows) uses `ExecuteAgentActivity` instead of calling agent-specific activities directly for code generation steps
7. The `AgentExecutionRequest` and `AgentExecutionResult` types are shared between both executors and the ELSA activity
8. Events recorded:
   - `AGENT.EXECUTION.STARTED` with `mode: "local" | "github_actions"`
   - `AGENT.EXECUTION.COMPLETED` with unified result
   - `AGENT.EXECUTION.FAILED` with error details

## Technical Context

### Interface Design

```csharp
namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// Abstraction for executing an AI agent to perform a development task.
/// Implementations handle the execution environment (local process, GitHub Actions, etc.)
/// </summary>
public interface IAgentExecutor
{
    /// <summary>
    /// Execute the agent and return the result.
    /// This is a potentially long-running operation (minutes).
    /// </summary>
    Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// The execution mode this executor operates in.
    /// </summary>
    string Mode { get; }
}

public record AgentExecutionRequest(
    string Repository,
    string BranchName,
    int IssueNumber,
    string IssueTitle,
    string Task,               // "implement", "fix", "debug", "review", "test"
    string PlanJson,
    string SessionId,
    string AgentProvider,      // "claude-code", "aider", etc.
    string? AgentConfigJson,
    int TimeoutMinutes
);

public record AgentExecutionResult(
    bool Success,
    int? PrNumber,
    string? PrUrl,
    string CommitSha,
    string[] FilesChanged,
    int CommitsCount,
    bool? ChecksPassed,
    int TokensUsed,
    int DurationSeconds,
    string? ErrorMessage,
    string? AgentLogSummary,
    string AgentProvider,
    string? AgentVersion,
    string ExecutionMode       // "local" or "github_actions"
);
```

### LocalExecutor

```csharp
public class LocalExecutor : IAgentExecutor
{
    public string Mode => "local";

    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        // 1. Resolve the agent CLI command (claude-code, aider, etc.)
        // 2. Write plan file to local .tamma/ directory
        // 3. Spawn child process with plan file and timeout
        // 4. Stream stdout for logging
        // 5. Wait for completion
        // 6. Read result.json from .tamma/
        // 7. Map to AgentExecutionResult
    }
}
```

The `LocalExecutor` invokes the agent CLI directly:
```bash
claude-code --headless \
  --plan-file .tamma/plan.json \
  --output-file .tamma/result.json \
  --timeout 1800
```

This is the execution path used when:
- Running `tamma start` (CLI mode with local agent)
- Running `tamma server` (self-hosted, agent on same machine)

### GitHubActionsExecutor

```csharp
public class GitHubActionsExecutor : IAgentExecutor
{
    private readonly IGitHubActionsClient _actionsClient;
    private readonly IInstallationRouter _installationRouter;

    public string Mode => "github_actions";

    public async Task<AgentExecutionResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        // 1. Dispatch workflow (Story 19-2 logic)
        var dispatchResult = await DispatchAsync(request, cancellationToken);
        if (!dispatchResult.Success)
            return FailedResult(dispatchResult.ErrorMessage);

        // 2. Monitor workflow run (Story 19-3 logic)
        var monitorResult = await MonitorAsync(request, dispatchResult, cancellationToken);
        if (monitorResult.Conclusion != "success")
            return FailedResult($"Workflow run concluded: {monitorResult.Conclusion}");

        // 3. Collect results (Story 19-4 logic)
        var collectResult = await CollectAsync(request, monitorResult, cancellationToken);
        return collectResult;
    }
}
```

The `GitHubActionsExecutor` composes the three activities (19-2, 19-3, 19-4) into a single execution flow. The individual ELSA activities still exist for fine-grained workflow control, but the executor provides a simpler high-level API.

### Executor Resolution

```csharp
public class AgentExecutorFactory
{
    private readonly IServiceProvider _services;

    public IAgentExecutor Create(string? modeOverride = null)
    {
        var mode = modeOverride
            ?? Environment.GetEnvironmentVariable("TAMMA_AGENT_MODE")
            ?? DetectMode();

        return mode switch
        {
            "local" => _services.GetRequiredService<LocalExecutor>(),
            "github_actions" => _services.GetRequiredService<GitHubActionsExecutor>(),
            _ => throw new ArgumentException($"Unknown agent execution mode: {mode}")
        };
    }

    private string DetectMode()
    {
        // If GitHub App credentials are configured -> github_actions
        // If running as CLI -> local
        // If running as API server with GitHub App -> github_actions
        var hasGitHubApp = !string.IsNullOrEmpty(
            Environment.GetEnvironmentVariable("TAMMA_GITHUB_APP_ID"));
        return hasGitHubApp ? "github_actions" : "local";
    }
}
```

### ELSA Activity Wrapper

```csharp
[Activity("Tamma", "Execute Agent",
    Description = "Executes an AI agent via the configured execution mode (local or GitHub Actions)")]
public class ExecuteAgentActivity : CodeActivity<AgentExecutionResult>
{
    private readonly AgentExecutorFactory _factory;

    [Input] public Input<string> Repository { get; set; } = default!;
    [Input] public Input<string> BranchName { get; set; } = default!;
    [Input] public Input<int> IssueNumber { get; set; } = default!;
    [Input] public Input<string> IssueTitle { get; set; } = default!;
    [Input] public Input<string> Task { get; set; } = default!;
    [Input] public Input<string> PlanJson { get; set; } = default!;
    [Input] public Input<string> SessionId { get; set; } = default!;
    [Input] public Input<string> AgentProvider { get; set; } = new("claude-code");
    [Input] public Input<string?> AgentConfigJson { get; set; } = new(default(string));
    [Input] public Input<int> TimeoutMinutes { get; set; } = new(30);
    [Input] public Input<string?> ModeOverride { get; set; } = new(default(string));

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var executor = _factory.Create(ModeOverride.Get(context));
        var request = new AgentExecutionRequest(
            Repository: Repository.Get(context),
            BranchName: BranchName.Get(context),
            IssueNumber: IssueNumber.Get(context),
            IssueTitle: IssueTitle.Get(context),
            Task: Task.Get(context),
            PlanJson: PlanJson.Get(context),
            SessionId: SessionId.Get(context),
            AgentProvider: AgentProvider.Get(context),
            AgentConfigJson: AgentConfigJson.Get(context),
            TimeoutMinutes: TimeoutMinutes.Get(context)
        );

        var result = await executor.ExecuteAsync(request, context.CancellationToken);
        context.SetResult(result);
    }
}
```

### Workflow Integration Point

In the `SingleIssueCycleWorkflow`, the TDD sub-workflow dispatch can be replaced with `ExecuteAgentActivity`:

```
Current flow (local only):
  DispatchTddRetry → TDD sub-workflow → direct LLM calls → commit

New flow (mode-aware):
  ExecuteAgentActivity (mode=auto)
    local:          → spawn agent process → collect results
    github_actions: → dispatch → monitor → collect results
```

The key insight is that the `ExecuteAgentActivity` produces the same output (`AgentExecutionResult`) regardless of mode. The `SingleIssueCycleWorkflow` only needs to know about success/failure, PR number, and files changed.

### Event Schema

```typescript
// AGENT.EXECUTION.STARTED
{
  type: "AGENT.EXECUTION.STARTED",
  tags: {
    repository: "owner/repo",
    sessionId: "sess_abc123",
    issueId: "42",
    mode: "github_actions"
  },
  data: {
    task: "implement",
    agentProvider: "claude-code",
    timeoutMinutes: 30,
    branchName: "tamma/issue-42-fix-login"
  }
}

// AGENT.EXECUTION.COMPLETED
{
  type: "AGENT.EXECUTION.COMPLETED",
  tags: {
    repository: "owner/repo",
    sessionId: "sess_abc123",
    issueId: "42",
    mode: "github_actions"
  },
  data: {
    success: true,
    prNumber: 99,
    filesChanged: 5,
    commitsCount: 3,
    tokensUsed: 45000,
    durationSeconds: 420,
    agentProvider: "claude-code"
  }
}
```

## Implementation Notes

### Files to Create

- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/IAgentExecutor.cs` -- interface + shared models
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/LocalExecutor.cs`
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/GitHubActionsExecutor.cs`
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/AgentExecutorFactory.cs`
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/ExecuteAgentActivity.cs`

### Files to Modify

- `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` -- register executors and factory in DI
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TddWithDebugRetryWorkflow.cs` -- optionally wire in `ExecuteAgentActivity`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` -- optionally add agent execution step

### Migration Strategy

The introduction of `IAgentExecutor` should be non-breaking:

1. **Phase 1**: Add the interface, executors, factory, and ELSA activity alongside existing activities
2. **Phase 2**: Add `ExecuteAgentActivity` as an alternative to the existing TDD/implementation dispatch
3. **Phase 3**: Gradually migrate workflows to use `ExecuteAgentActivity`
4. **Phase 4**: Deprecate direct agent invocation in workflows

This allows both paths to coexist during development and testing.

### LocalExecutor: Agent CLI Discovery

The `LocalExecutor` needs to find the agent CLI. Strategy:

1. Check if `claude-code` is in PATH
2. Check for a global npm install
3. Check for a local install in the repository's `node_modules`
4. Fall back to an error with installation instructions

For other agents (Aider, etc.), similar discovery logic applies.

### GitHubActionsExecutor: Reuse vs Inline

Two implementation strategies:

**Option A: Reuse ELSA activities** -- The executor creates and runs `DispatchAgentWorkflowActivity`, `MonitorAgentWorkflowActivity`, and `CollectAgentResultsActivity` programmatically.

**Option B: Inline the logic** -- The executor implements dispatch/monitor/collect directly using `IGitHubActionsClient`, without going through ELSA activities.

Option B is simpler and avoids the complexity of programmatically executing ELSA activities outside a workflow. The individual activities (Stories 19-2, 19-3, 19-4) remain available for users who want fine-grained workflow control.

**Recommendation: Option B.** The activities and the executor share the same underlying `IGitHubActionsClient`, but the executor composes the logic directly.

## Dependencies

- **Story 19-2**: `IGitHubActionsClient` for dispatch
- **Story 19-3**: Monitoring logic / `IGitHubActionsClient` for workflow_run queries
- **Story 19-4**: Collection logic / artifact download
- **Existing Infrastructure**: `InstallationRouter`, `GitHubPlatform`
- **CLI Agent Support**: Claude Code CLI must support headless mode (verify)

## Estimated Effort

**Size**: L (Large)
- IAgentExecutor interface + shared models: 0.5 day
- LocalExecutor: 2 days (process spawning, output parsing, error handling)
- GitHubActionsExecutor: 2 days (composing dispatch/monitor/collect)
- AgentExecutorFactory: 0.5 day
- ExecuteAgentActivity (ELSA wrapper): 1 day
- Event recording: 0.5 day
- Unit tests: 2 days
- Integration tests: 1.5 days

**Total**: ~10 days

## Testing Strategy

### Unit Tests
- `LocalExecutor`: Test process spawning with mock agent (shell script that writes result.json)
- `LocalExecutor`: Test timeout handling (process killed after timeout)
- `LocalExecutor`: Test agent not found (CLI not in PATH)
- `GitHubActionsExecutor`: Test full dispatch -> monitor -> collect with mock GitHub API
- `GitHubActionsExecutor`: Test dispatch failure (no result collection attempted)
- `GitHubActionsExecutor`: Test monitor timeout (partial result returned)
- `AgentExecutorFactory`: Test mode resolution (env var, auto-detect)
- `ExecuteAgentActivity`: Test with mock executor

### Integration Tests
- `LocalExecutor` with a real Claude Code invocation (requires API key)
- `GitHubActionsExecutor` end-to-end on `tamma-test-github`
- Mode switching: same workflow runs with both executors

### Contract Tests
- Verify `LocalExecutor` and `GitHubActionsExecutor` produce equivalent `AgentExecutionResult` for the same task
- Verify the ELSA activity correctly maps inputs/outputs

### Edge Cases
- Agent CLI updates mid-execution (version mismatch)
- Network loss during GitHub Actions monitoring (reconnect and resume)
- Concurrent executions on same repository, different branches
- Mode override from ELSA agent config vs environment variable
