# Agent Dispatch (Epic 19 — complete)

Tamma's **agent dispatch** layer abstracts _how_ an agent actually runs. Workflows always do the same thing — "execute this task with this agent, then report back" — but the concrete execution surface changes with the deployment mode.

- **CLI Mode**: the agent runs as a subprocess on the operator's machine (`LocalExecutor`).
- **SaaS Mode**: the agent runs inside a `workflow_dispatch` on the tenant's GitHub Actions runners (`GitHubActionsExecutor`). User code never leaves the tenant's infrastructure.

Epic 19 is **complete** (2026-04-21). All four stories shipped: 19-2 (dispatch), 19-3 (monitor — with webhook-mode resume), 19-4 (collect), 19-5 (execute wrapper). Two security hardening fixes followed from the 2026-04-20 code review.

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
| `GitHubActions` | `GitHubActionsExecutor` | GitHub Actions `workflow_dispatch` | GitHub App configured + (auto-detect \| `TAMMA_AGENT_MODE=GitHubActions`) |

### LocalExecutor

- Wraps `IProcessRunner` (default: `DefaultProcessRunner`) so tests can substitute a deterministic fake.
- Captures stdout / stderr; enforces a per-dispatch timeout; deduplicates concurrent dispatches via an in-memory registry.
- Artifact collection reads the working-directory outputs the subprocess wrote before exiting.
- Working dir defaults to `Path.GetTempPath() / "tamma" / SafeId(sessionId)`; operators can override with `TAMMA_AGENT_TMP`. The default path is flagged as potentially attackable on shared hosts (review finding 7 — P2, not yet patched but documented).
- **TS `execute-agent` CLI**: the subprocess entry point is `packages/cli/src/commands/execute-agent.ts` — the TS CLI that reads `exec-request-<sessionId>.json`, calls the agent provider, and writes `exec-result-<sessionId>.json` back for the executor to collect. See `packages/cli/src/commands/execute-agent.test.ts` for the deterministic test harness.

### GitHubActionsExecutor

- Uses Octokit via `IGitHubActionsClient` (`OctokitGitHubActionsClient` when the GitHub App is wired, `NullGitHubActionsClient` otherwise — so the Null seam reports `NotConfigured` instead of silently succeeding).
- `DispatchAsync` POSTs a `workflow_dispatch` to the tenant's repo with the agent inputs encoded as workflow inputs.
- `MonitorAsync` supports **two modes**:
  - **Polling**: polls `GET /repos/{owner}/{repo}/actions/runs` for the matching run.
  - **Webhook resume**: registers a bookmark via `WebhookSignalRegistry` and resumes when the matching `workflow_run` webhook fires. This is the default on SaaS deployments to cut polling load.
- `CollectAsync` downloads the run's artifacts (the `tamma-result` ZIP) and parses the embedded `result.json`.

### WebhookSignalRegistry

`apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/WebhookSignalRegistry.cs` provides the bookmark-alias mapping for webhook-mode monitoring. Three alias forms:

| Form | Shape |
|------|-------|
| `run`    | `install:{installId}:run:{repo}:{runId}` |
| `branch` | `install:{installId}:branch:{repo}:{branch}` |
| `branch-session` | `install:{installId}:branch:{repo}:{branch}:{sessionId}` |

The `install:{installId}:` prefix is a **security fix** landed as commit `9160db1` (review finding 5). Without it, two tenants with Tamma installed on the same `owner/repo` could cross-wake each other's `AgentMonitorService` via the branch-fallback alias and download each other's artifacts. `InstallationRouterService` propagates the installation id on every publish.

## Executor selection

`AgentExecutorFactory` resolves mode by precedence:

1. **Explicit override** passed by the caller (`ExecuteAgentActivity` reads the mode from workflow input).
2. **Environment variable** `TAMMA_AGENT_MODE=Local|GitHubActions`.
3. **Configuration** `Agent:ExecutorMode` (`Local`, `GitHubActions`, or `Auto`).
4. **Auto-detection** — `GitHubActions` if a GitHub App is configured (`GitHub:AppId` > 0 **and** `GitHub:PrivateKey` non-empty); otherwise `Local`.

A misconfiguration (e.g. `TAMMA_AGENT_MODE=GitHubActions` with no GitHub App) **fails fast at dispatch time** — the `NullGitHubActionsClient` reports `NotConfigured` and the dispatch surfaces a clean operator error.

## Activity pipeline

The `(role, action)` vocabulary is the single shared taxonomy — see [Role/Action Taxonomy](Role-Action-Taxonomy.md).

```
ExecuteAgentActivity
  ├─ DispatchAgentWorkflowActivity   → AgentDispatchService.DispatchAsync()
  │                                    → IAgentExecutor.DispatchAsync()
  ├─ MonitorAgentWorkflowActivity    → AgentMonitorService.MonitorAsync()
  │                                    → IAgentExecutor.MonitorAsync()
  │                                    [may suspend via WebhookSignalRegistry]
  └─ CollectAgentResultsActivity     → AgentResultCollectorService.CollectAsync()
                                       → IAgentExecutor.CollectAsync()
```

`SingleIssueCycleWorkflow` has been refactored to use `ExecuteAgentActivity` in place of its previous inline agent-dispatch logic.

## Security hardening (from code-review 2026-04-20)

Two P1 findings were closed before merge; others are scheduled follow-ups.

| Finding | Severity | Status | Commit |
|---------|----------|--------|--------|
| #5 webhook signal registry not tenant-scoped | P1 | **closed** | `9160db1` — `install:{id}:` prefix on all alias forms |
| #6 unbounded artifact download → OOM | P1 | **closed** | `ced59bc` — 4 MB cap in `LimitedStream`; string clamps in `ParseResultJson` |
| #7 LocalExecutor temp path on shared hosts | P2 | follow-up | documented; `TAMMA_AGENT_TMP` env var planned |
| #8 arbitrary-length string fields in `AgentResultArtifact` | P2 | **closed** | included in `ced59bc` (2 KB / 32 KB caps per field) |
| #9 `DefaultProcessRunner` 250 ms race for stream drain | P2 | follow-up | trivial; swap to `Task.WhenAll` after `WaitForExitAsync` |

## Related source files

| Path | Purpose |
|------|---------|
| `Tamma.Activities/AgentDispatch/IAgentExecutor.cs` | Executor contract + DTOs |
| `Tamma.Activities/AgentDispatch/LocalExecutor.cs` | CLI-mode subprocess executor |
| `Tamma.Activities/AgentDispatch/GitHubActionsExecutor.cs` | SaaS-mode Actions executor |
| `Tamma.Activities/AgentDispatch/AgentExecutorFactory.cs` | Mode resolution |
| `Tamma.Activities/AgentDispatch/WebhookSignalRegistry.cs` | Bookmark alias mapping (tenant-scoped) |
| `Tamma.Activities/AgentDispatch/AgentDispatchService.cs` | Dispatch service wrapper |
| `Tamma.Activities/AgentDispatch/AgentMonitorService.cs` | Monitor service wrapper (polling + webhook) |
| `Tamma.Activities/AgentDispatch/AgentResultCollectorService.cs` | Collect service wrapper (size-capped) |
| `Tamma.Activities/AgentDispatch/DispatchAgentWorkflowActivity.cs` | Story 19-2 activity |
| `Tamma.Activities/AgentDispatch/MonitorAgentWorkflowActivity.cs` | Story 19-3 activity |
| `Tamma.Activities/AgentDispatch/CollectAgentResultsActivity.cs` | Story 19-4 activity |
| `Tamma.Activities/AgentDispatch/ExecuteAgentActivity.cs` | Story 19-5 orchestrator activity |
| `Tamma.Api/Services/GitHub/OctokitGitHubActionsClient.cs` | Real Octokit implementation (4 MB cap) |
| `Tamma.Activities/AgentDispatch/NullGitHubActionsClient.cs` | Null seam for unconfigured deployments |
| `packages/cli/src/commands/execute-agent.ts` | TS CLI for `LocalExecutor` shell-out |

## Tests

End-to-end dispatch / monitor / collect tests cover both executors with mock clients; see `apps/tamma-elsa/tests/Tamma.Activities.Tests/AgentDispatch/`:

- `AgentExecutorFactoryTests.cs` — mode resolution precedence
- `LocalExecutorTests.cs` — subprocess dispatch, timeout, stdout capture
- `GitHubActionsExecutorTests.cs` — Octokit client contract, null-seam fallback
- `WebhookSignalRegistryTests.cs` — tenant-scoped alias keying + resume on signal
- `AgentDispatchServiceTests.cs`, `AgentMonitorServiceTests.cs`, `AgentResultCollectorServiceTests.cs` — service-layer idempotency
- `AgentDispatchActivitiesTests.cs` — Elsa activity wiring
- `packages/cli/src/commands/execute-agent.test.ts` — TS CLI deterministic harness

## Follow-up: Story 19-6

`docs/stories/epic-19/story-19-6-wire-app-role-context.md` was the follow-up story to wire the app-role (`tamma_app`) connection into endpoints and repositories (review finding 1). The landscape has since changed: the legacy `TammaAppDbContext` + RLS scaffold was superseded by the unified schema-per-tenant model (tenant isolation is schema + per-tenant role; the RLS layer was removed in unified-tenancy Phase 5), and Story 30-8 closed the per-tenant endpoint-routing half. `tamma_app` survives as the least-privilege runtime role for the control plane.

## Related

- [Architecture → Agent Dispatch](Architecture#agent-dispatch-epic-19)
- [Deployment → Cranl activation](Deployment#cranl-per-tenant-provisioning-optional)
- [GitHub Integration](GitHub-Integration)
- [Port Audit](Port-Audit) — code-review findings
- [Epic 19 stories](Epics/Epic-19-Agent-Dispatch)
