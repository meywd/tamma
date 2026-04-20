# Agent Dispatch (Epic 19)

Tamma's **agent dispatch** layer abstracts _how_ an agent actually runs. Workflows always do the same thing — "execute this task with this agent, then report back" — but the concrete execution surface changes with the deployment mode.

- **CLI Mode**: the agent runs as a subprocess on the operator's machine (`LocalExecutor`).
- **SaaS Mode**: the agent runs inside a workflow_dispatch on the tenant's GitHub Actions runners (`GitHubActionsExecutor`). User code never leaves the tenant's infrastructure.

This page covers Epic 19 stories **19-2, 19-3, 19-4, 19-5**, which landed during the auth-foundation sprint.

## The `IAgentExecutor` abstraction

Source: `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/IAgentExecutor.cs`

```csharp
public interface IAgentExecutor
{
    Task<DispatchResult> DispatchAsync(DispatchRequest req, CancellationToken ct);
    Task<MonitorResult> MonitorAsync(MonitorRequest req, CancellationToken ct);
    Task<CollectResult> CollectAsync(CollectRequest req, CancellationToken ct);
}
```

The four Elsa activities (`DispatchAgentWorkflowActivity`, `MonitorAgentWorkflowActivity`, `CollectAgentResultsActivity`, `ExecuteAgentActivity`) are thin shells around these three methods — activities own workflow state (inputs, outputs, bookmarks); the executor owns the execution surface.

## Execution modes

| Mode | Class | Surface | Activated by |
|------|-------|---------|--------------|
| `Local` | `LocalExecutor` | subprocess on operator machine | CLI mode, or `TAMMA_AGENT_MODE=Local` |
| `GitHubActions` | `GitHubActionsExecutor` | GitHub Actions `workflow_dispatch` | GitHub App configured + (implicit auto-detect \| `TAMMA_AGENT_MODE=GitHubActions`) |

### LocalExecutor

- Wraps `IProcessRunner` (default: `DefaultProcessRunner`) so tests can substitute a deterministic fake.
- Captures stdout / stderr; enforces a per-dispatch timeout; deduplicates concurrent dispatches via an in-memory registry.
- Artifact collection reads the working-directory outputs the subprocess wrote before exiting.

### GitHubActionsExecutor

- Uses Octokit via `IGitHubActionsClient` (`OctokitGitHubActionsClient` when the GitHub App is wired, `NullGitHubActionsClient` otherwise — so the Null seam reports `NotConfigured` instead of silently succeeding).
- `DispatchAsync` POSTs a `workflow_dispatch` to the tenant's repo with the agent inputs encoded as workflow inputs.
- `MonitorAsync` polls `GET /repos/{owner}/{repo}/actions/runs` for the matching run; `CollectAsync` downloads the run's artifacts.
- Supports a `WebhookSignalRegistry` seam so the monitor can be woken by `workflow_run` webhooks instead of hot-polling in future sprints.

## Executor selection

`AgentExecutorFactory` (`AgentExecutorFactory.cs`) resolves mode by precedence:

1. **Explicit override** passed by the caller (`ExecuteAgentActivity` reads the mode from workflow input).
2. **Environment variable** `TAMMA_AGENT_MODE=Local|GitHubActions`.
3. **Configuration** `Agent:ExecutorMode` (`Local`, `GitHubActions`, or `Auto`).
4. **Auto-detection** — `GitHubActions` if a GitHub App is configured (`GitHub:AppId` > 0 **and** `GitHub:PrivateKey` non-empty); otherwise `Local`.

A misconfiguration (e.g. `TAMMA_AGENT_MODE=GitHubActions` with no GitHub App) **fails fast at dispatch time** — the `NullGitHubActionsClient` reports `NotConfigured` and the dispatch surfaces a clean operator error.

## Activity pipeline

```
ExecuteAgentActivity
  ├─ DispatchAgentWorkflowActivity   → AgentDispatchService.DispatchAsync()
  │                                    → IAgentExecutor.DispatchAsync()
  ├─ MonitorAgentWorkflowActivity    → AgentMonitorService.MonitorAsync()
  │                                    → IAgentExecutor.MonitorAsync()
  └─ CollectAgentResultsActivity     → AgentResultCollectorService.CollectAsync()
                                       → IAgentExecutor.CollectAsync()
```

Services wrap the executors and encapsulate logic shared by the activities (e.g. idempotent dispatch keys, retry/backoff envelopes, structured logging). Each activity is idempotent on replay — the services deduplicate by `(tenantId, dispatchId)`.

## Related source files

| Path | Purpose |
|------|---------|
| `Tamma.Activities/AgentDispatch/IAgentExecutor.cs` | Executor contract + DTOs |
| `Tamma.Activities/AgentDispatch/LocalExecutor.cs` | CLI-mode subprocess executor |
| `Tamma.Activities/AgentDispatch/GitHubActionsExecutor.cs` | SaaS-mode Actions executor |
| `Tamma.Activities/AgentDispatch/AgentExecutorFactory.cs` | Mode resolution |
| `Tamma.Activities/AgentDispatch/AgentDispatchService.cs` | Dispatch service wrapper |
| `Tamma.Activities/AgentDispatch/AgentMonitorService.cs` | Monitor service wrapper |
| `Tamma.Activities/AgentDispatch/AgentResultCollectorService.cs` | Collect service wrapper |
| `Tamma.Activities/AgentDispatch/DispatchAgentWorkflowActivity.cs` | Story 19-2 activity |
| `Tamma.Activities/AgentDispatch/MonitorAgentWorkflowActivity.cs` | Story 19-3 activity |
| `Tamma.Activities/AgentDispatch/CollectAgentResultsActivity.cs` | Story 19-4 activity |
| `Tamma.Activities/AgentDispatch/ExecuteAgentActivity.cs` | Story 19-5 orchestrator activity |
| `Tamma.Api/Services/GitHub/OctokitGitHubActionsClient.cs` | Real Octokit implementation |
| `Tamma.Activities/AgentDispatch/NullGitHubActionsClient.cs` | Null seam for unconfigured deployments |

## Tests

End-to-end dispatch/monitor/collect tests cover both executors with mock clients; see `apps/tamma-elsa/tests/Tamma.Activities.Tests/AgentDispatch/`:

- `AgentExecutorFactoryTests.cs` — mode resolution precedence
- `LocalExecutorTests.cs` — subprocess dispatch, timeout, stdout capture
- `GitHubActionsExecutorTests.cs` — Octokit client contract, null-seam fallback
- `AgentDispatchServiceTests.cs`, `AgentMonitorServiceTests.cs`, `AgentResultCollectorServiceTests.cs` — service-layer idempotency
- `AgentDispatchActivitiesTests.cs` — Elsa activity wiring

## Related

- [Architecture → Agent Dispatch](Architecture#agent-dispatch-epic-19)
- [Deployment → Cranl activation](Deployment#cranl-per-tenant-provisioning-optional)
- [GitHub Integration](GitHub-Integration)
- [Epic 19 stories](Epics/Epic-19-GitHub-App-Agent-Dispatch)
