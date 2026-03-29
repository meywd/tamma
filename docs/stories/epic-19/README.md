# Epic 19: GitHub App Agent Dispatch

## Overview

**Goal**: Enable Tamma Cloud to orchestrate autonomous development agents that run on the **user's own GitHub Actions runners**, so that user code never leaves their GitHub environment. Tamma dispatches work, monitors execution, and collects results exclusively through the GitHub API.

**Value Delivered**:
- User code stays on user infrastructure -- zero data exfiltration risk
- Tamma Cloud is a pure orchestrator -- no compute cost for agent execution
- Users bring their own API keys (Anthropic, OpenAI, etc.) via GitHub Actions secrets
- Same agent behavior works in CLI mode (local) and SaaS mode (dispatched to runner)
- Full audit trail: every dispatch, status check, and result collection is an event

## Architecture

```
User's GitHub Repository
    .github/workflows/tamma-agent.yml   <-- Story 19-1 (template)
    (Claude Code / other agent runs here)

         ^                    |
         | workflow_dispatch  | PR created, checks pass, artifacts
         |                    v

Tamma Cloud (ELSA Workflows)
    DispatchAgentActivity      <-- Story 19-2
    MonitorAgentActivity       <-- Story 19-3
    CollectResultsActivity     <-- Story 19-4

         ^
         |
    IAgentExecutor             <-- Story 19-5
    +-- LocalExecutor          (CLI mode: runs agent in-process)
    +-- GitHubActionsExecutor  (SaaS mode: dispatches to user's runner)
```

### Security Model

1. **Tamma Cloud never clones user code.** The agent runs on the user's runner and operates within the repo checkout.
2. **API keys for LLM providers are GitHub Actions secrets** in the user's repo, configured during Tamma installation.
3. **Tamma authenticates as a GitHub App** with `actions:write` permission to dispatch workflows and `checks:read` + `actions:read` to monitor.
4. **Results flow through GitHub API only**: PR metadata, check run status, workflow run logs/artifacts.
5. **The workflow template is open source** and auditable -- users can inspect exactly what runs.

### Data Flow

```
1. ELSA workflow reaches "agent execution" step
2. DispatchAgentActivity calls GitHub API:
   POST /repos/{owner}/{repo}/actions/workflows/tamma-agent.yml/dispatches
   {
     "ref": "tamma/issue-42-fix-login",
     "inputs": {
       "issue_number": "42",
       "task": "implement",
       "plan_json": "...",
       "tamma_callback_url": "https://api.tamma.dev/callbacks/..."
     }
   }
3. GitHub Actions runs tamma-agent.yml on user's runner
4. Agent (Claude Code) checks out branch, reads plan, writes code, creates PR
5. MonitorAgentActivity polls workflow_run status (or receives webhook)
6. CollectResultsActivity reads PR data, check results, changed files via GitHub API
7. ELSA workflow continues with collected results
```

## Stories

| Story | Title | Priority | Dependencies | Effort |
|-------|-------|----------|-------------|--------|
| 19-1 | Tamma Agent GitHub Actions Workflow Template | P0 | None | M |
| 19-2 | Workflow Dispatch from ELSA | P0 | 19-1, GitHub App permissions | L |
| 19-3 | Agent Execution Monitoring | P0 | 19-2 | L |
| 19-4 | Result Collection | P0 | 19-3 | M |
| 19-5 | CLI / SaaS Mode Abstraction (IAgentExecutor) | P0 | 19-2, 19-3, 19-4 | L |

## Implementation Phases

### Phase 1: Template & Dispatch (Stories 19-1, 19-2)
- Create the reusable GitHub Actions workflow template
- Implement ELSA activity to dispatch workflow_run via GitHub App API
- Validate end-to-end: ELSA dispatches, runner picks up, agent executes

### Phase 2: Monitoring & Results (Stories 19-3, 19-4)
- Poll/webhook for workflow_run completion
- Read PR data, check results, file changes from the completed run
- Wire results back into ELSA workflow variables

### Phase 3: Abstraction (Story 19-5)
- Define IAgentExecutor interface
- Implement LocalExecutor (CLI mode) and GitHubActionsExecutor (SaaS mode)
- Wire into SingleIssueCycleWorkflow so the same workflow works in both modes

## Dependencies

- **Epic 1** (Providers): GitHub platform implementation (`GitHubPlatform`, Octokit)
- **Epic 10** (Engine Core): Event store for recording dispatch/monitor/collect events
- **GitHub App**: Must have `actions:write`, `contents:write`, `checks:read`, `pull_requests:read` permissions
- **User Setup**: User must add `tamma-agent.yml` to their repo and configure secrets

## GitHub App Permissions Required

| Permission | Access | Purpose |
|-----------|--------|---------|
| `actions` | write | Dispatch workflow_run, read workflow status |
| `contents` | write | Create branches (already have this) |
| `checks` | read | Read check run results on PRs |
| `pull_requests` | read | Read PR metadata, changed files |
| `issues` | read/write | Read issue details, post comments |
| `metadata` | read | Repository metadata (already have this) |

## Success Metrics

- Workflow dispatch latency: <2s from ELSA activity execution to GitHub API response
- Monitoring poll interval: 30s (configurable), webhook-based <5s
- End-to-end cycle time: <15 min for a typical issue (depends on agent + CI)
- Zero user code on Tamma infrastructure
- 100% audit trail: every dispatch, status check, and result collection recorded as event

---

**Last Updated**: 2026-03-28
**Epic Owner**: Architecture Team
**Implementation Start**: TBD
