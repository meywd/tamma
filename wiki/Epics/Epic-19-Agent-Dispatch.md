# Epic 19: GitHub App Agent Dispatch

**Status:** **Complete** (5 stories shipped 2026-04-21; follow-up Story 19-6 pending for app-role wiring)
**Stories:** 5 active (19-1 through 19-5) + 1 follow-up (19-6) + 1 consolidation note (19-1 API consolidation)

> **Overview**: [Agent Dispatch](Agent-Dispatch) — root-level topic page with the full executor abstraction, security hardening details, and source-file map.

## Overview

Epic 19 enables Tamma Cloud to orchestrate autonomous development agents that run on the user's own GitHub Actions runners, so that user code never leaves their GitHub environment. Tamma dispatches work, monitors execution, and collects results exclusively through the GitHub API.

The epic shipped end-to-end during the auth-foundation sprint (2026-04-18 to 2026-04-21). All four implementation stories landed; two security hardening fixes followed from the 2026-04-20 code review.

## Goals

1. Create reusable GitHub Actions workflow template for agent execution (Story 19-1, done)
2. Implement Elsa activity to dispatch `workflow_dispatch` events (Story 19-2, done)
3. Build agent execution monitoring with polling and webhook modes (Story 19-3, done — `WebhookSignalRegistry`)
4. Collect results from completed runs with 4 MB artifact cap (Story 19-4, done)
5. Abstract CLI / SaaS mode via `IAgentExecutor` interface (Story 19-5, done)

## Value delivered

- User code stays on user infrastructure — zero data exfiltration risk
- Tamma Cloud is a pure orchestrator — no compute cost for agent execution
- Users bring their own API keys via GitHub Actions secrets
- Same agent behavior works in CLI mode (`LocalExecutor`) and SaaS mode (`GitHubActionsExecutor`)
- Webhook-mode monitoring cuts polling load on SaaS deployments
- Tenant-scoped signal keys prevent cross-tenant artifact leakage
- 4 MB artifact cap + string clamps prevent OOM from oversized run outputs
- Full audit trail: every dispatch, status check, and result collection is an event

## Stories

| Story | Title | Priority | Effort | Status |
|-------|-------|----------|--------|--------|
| 19-1 | Tamma Agent GitHub Actions Workflow Template | P0 | M | **Done** |
| 19-1 (consolidation) | API consolidation to C# | P0 | L | Done — see `19-1-api-consolidation-to-csharp.md` |
| 19-2 | Workflow Dispatch from Elsa | P0 | L | **Done** |
| 19-3 | Agent Execution Monitoring (polling + webhook) | P0 | L | **Done** |
| 19-4 | Result Collection (4 MB cap) | P0 | M | **Done** |
| 19-5 | CLI / SaaS Mode Abstraction (`IAgentExecutor`) | P0 | L | **Done** |
| 19-6 | Wire `TammaAppDbContext` app-role context (follow-up) | P1 | M | In Progress |

## Architecture

```
User's GitHub Repository
    .github/workflows/tamma-agent.yml   ← Template (19-1)
    (Claude Code / other agent runs here)

         ↑                    ↓
         | workflow_dispatch  | PR created, checks pass, artifacts
         |                    |

Tamma Cloud (Elsa Workflows)
    DispatchAgentWorkflowActivity  ← Story 19-2
    MonitorAgentWorkflowActivity   ← Story 19-3 (polling + webhook)
    CollectAgentResultsActivity    ← Story 19-4 (4 MB cap)
    ExecuteAgentActivity           ← Story 19-5 (orchestrator)

         ↑
         |
    IAgentExecutor                 ← Story 19-5
    ├── LocalExecutor              (CLI: subprocess on operator machine)
    └── GitHubActionsExecutor      (SaaS: dispatches to user's runner)
```

## The `IAgentExecutor` abstraction

`apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/IAgentExecutor.cs`:

```csharp
public interface IAgentExecutor
{
    Task<DispatchResult> DispatchAsync(DispatchRequest req, CancellationToken ct);
    Task<MonitorResult> MonitorAsync(MonitorRequest req, CancellationToken ct);
    Task<CollectResult> CollectAsync(CollectRequest req, CancellationToken ct);
}
```

The four Elsa activities are thin shells around these three methods — activities own workflow state (inputs, outputs, bookmarks); the executor owns the execution surface.

### Mode resolution (`AgentExecutorFactory`)

1. **Explicit override** passed by the caller (`ExecuteAgentActivity` reads mode from workflow input)
2. **Environment variable** `TAMMA_AGENT_MODE=Local|GitHubActions`
3. **Configuration** `Agent:ExecutorMode` (`Local`, `GitHubActions`, or `Auto`)
4. **Auto-detection** — `GitHubActions` if a GitHub App is configured; otherwise `Local`

A misconfiguration (`TAMMA_AGENT_MODE=GitHubActions` with no GitHub App) **fails fast at dispatch time** via `NullGitHubActionsClient` reporting `NotConfigured`.

## Security model

1. Tamma Cloud **never clones user code** — agent runs on user's runner
2. LLM API keys are **GitHub Actions secrets** in user's repo
3. Tamma authenticates as GitHub App with `actions:write` permission
4. Results flow through GitHub API only (PR metadata, check status, workflow logs)
5. Workflow template is open source and auditable

### GitHub App permissions

| Permission | Access | Purpose |
|-----------|--------|---------|
| `actions` | write | Dispatch `workflow_dispatch`, read run status |
| `contents` | write | Create branches |
| `checks` | read | Read check run results on PRs |
| `pull_requests` | read | Read PR metadata, changed files |
| `issues` | read/write | Read issue details, post comments |

## Security hardening (code review 2026-04-20)

Two P1 findings closed before merge:

| Finding | Severity | Status | Commit |
|---------|----------|--------|--------|
| #5 webhook signal registry not tenant-scoped | P1 | **closed** | `9160db1` — `install:{id}:` prefix on all alias forms |
| #6 unbounded artifact download → OOM | P1 | **closed** | `ced59bc` — 4 MB cap in `LimitedStream`; string clamps in `ParseResultJson` |
| #8 arbitrary-length string fields in `AgentResultArtifact` | P2 | **closed** | included in `ced59bc` (2 KB / 32 KB caps per field) |
| #7 `LocalExecutor` temp path on shared hosts | P2 | follow-up | documented; `TAMMA_AGENT_TMP` env var planned |
| #9 `DefaultProcessRunner` 250 ms race for stream drain | P2 | follow-up | trivial swap to `Task.WhenAll` after `WaitForExitAsync` |

Without the `install:{id}:` prefix (finding 5), two tenants with Tamma installed on the same `owner/repo` could cross-wake each other's `AgentMonitorService` via the branch-fallback alias and download each other's artifacts.

## Webhook signal registry

`WebhookSignalRegistry.cs` provides bookmark-alias mapping for webhook-mode monitoring. Three alias forms, all tenant-scoped:

| Form | Shape |
|------|-------|
| `run`             | `install:{installId}:run:{repo}:{runId}` |
| `branch`          | `install:{installId}:branch:{repo}:{branch}` |
| `branch-session`  | `install:{installId}:branch:{repo}:{branch}:{sessionId}` |

`InstallationRouterService` propagates the installation id on every publish.

## Success metrics

- Workflow dispatch latency: < 2s ✅
- Monitoring poll interval: 30s (configurable), webhook-based < 5s ✅
- End-to-end cycle time: < 15 min for typical issue
- Zero user code on Tamma infrastructure ✅
- 100% audit trail coverage ✅

## Follow-up: Story 19-6

`docs/stories/epic-19/story-19-6-wire-app-role-context.md` is the follow-up to **actually wire** `TammaAppDbContext` into endpoints and repositories. Phase-3 RLS scaffolding is shipped but the runtime is still on the permissive admin connection (review finding 1). 19-6 closes the app-role-wiring half; Story 30-8 (Epic 30) closes the per-tenant endpoint-routing half.

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| GitHub Platform | Epic 1 | Octokit for GitHub API calls |
| Engine Core | Epic 10 | Event store for audit trail |
| Elsa Workflows | Epic 7 | Activities run inside Elsa |
| GitHub App Auth | Epic 1.5 | App credentials for dispatch |
| Tenant model | Epic 17 | Tenant scoping for `WebhookSignalRegistry` |
| App-role context | Epic 28 | Story 19-6 wires `TammaAppDbContext` factory |

## Tests

End-to-end dispatch / monitor / collect tests cover both executors with mock clients; see `apps/tamma-elsa/tests/Tamma.Activities.Tests/AgentDispatch/`:

- `AgentExecutorFactoryTests.cs` — mode resolution precedence
- `LocalExecutorTests.cs` — subprocess dispatch, timeout, stdout capture
- `GitHubActionsExecutorTests.cs` — Octokit client contract, null-seam fallback
- `WebhookSignalRegistryTests.cs` — tenant-scoped alias keying + resume on signal
- `AgentDispatchServiceTests.cs`, `AgentMonitorServiceTests.cs`, `AgentResultCollectorServiceTests.cs` — service-layer idempotency
- `AgentDispatchActivitiesTests.cs` — Elsa activity wiring
- `packages/cli/src/commands/execute-agent.test.ts` — TS CLI deterministic harness

## Story files

[Epic 19 stories on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-19)

---

_Last updated: 2026-04-21 (auth-foundation sprint complete)_
