# Epic 19: GitHub App Agent Dispatch

**Status:** Partially Implemented (1 done, 1 in progress, 3 drafted)
**Stories:** 5 (19-1 through 19-5)

## Overview

Epic 19 enables Tamma Cloud to orchestrate autonomous development agents that run on the **user's own GitHub Actions runners**, so that user code never leaves their GitHub environment. Tamma dispatches work, monitors execution, and collects results exclusively through the GitHub API.

## Goals

1. Create reusable GitHub Actions workflow template for agent execution
2. Implement ELSA activity to dispatch `workflow_dispatch` events
3. Build agent execution monitoring (polling + webhook)
4. Collect results from completed runs (PR data, check results, file changes)
5. Abstract CLI/SaaS mode via `IAgentExecutor` interface

## Value Delivered

- User code stays on user infrastructure -- zero data exfiltration risk
- Tamma Cloud is a pure orchestrator -- no compute cost for agent execution
- Users bring their own API keys via GitHub Actions secrets
- Same agent behavior works in CLI mode (local) and SaaS mode (dispatched)
- Full audit trail: every dispatch, status check, and result collection is an event

## Stories

| Story | Title | Priority | Effort | Status |
|-------|-------|----------|--------|--------|
| 19-1 | Tamma Agent GitHub Actions Workflow Template | P0 | M | Done |
| 19-2 | Workflow Dispatch from ELSA | P0 | L | Drafted |
| 19-3 | Agent Execution Monitoring | P0 | L | Drafted |
| 19-4 | Result Collection | P0 | M | In Progress |
| 19-5 | CLI / SaaS Mode Abstraction (IAgentExecutor) | P0 | L | Drafted |

## Key Technical Details

### Architecture

```
User's GitHub Repository
    .github/workflows/tamma-agent.yml   <-- Template (19-1)
    (Claude Code / other agent runs here)

         ^                    |
         | workflow_dispatch  | PR created, checks pass, artifacts
         |                    v

Tamma Cloud (ELSA Workflows)
    DispatchAgentActivity      <-- (19-2)
    MonitorAgentActivity       <-- (19-3)
    CollectResultsActivity     <-- (19-4)

         ^
         |
    IAgentExecutor             <-- (19-5)
    +-- LocalExecutor          (CLI: runs agent in-process)
    +-- GitHubActionsExecutor  (SaaS: dispatches to user's runner)
```

### Security Model

1. Tamma Cloud **never clones user code** -- agent runs on user's runner
2. LLM API keys are **GitHub Actions secrets** in user's repo
3. Tamma authenticates as GitHub App with `actions:write` permission
4. Results flow through GitHub API only (PR metadata, check status, workflow logs)
5. Workflow template is open source and auditable

### GitHub App Permissions Required

| Permission | Access | Purpose |
|-----------|--------|---------|
| `actions` | write | Dispatch workflow_run, read status |
| `contents` | write | Create branches |
| `checks` | read | Read check run results on PRs |
| `pull_requests` | read | Read PR metadata, changed files |
| `issues` | read/write | Read issue details, post comments |

### Implementation Phases

| Phase | Stories | Description |
|-------|---------|-------------|
| Phase 1 | 19-1, 19-2 | Template & Dispatch |
| Phase 2 | 19-3, 19-4 | Monitoring & Results |
| Phase 3 | 19-5 | CLI/SaaS Abstraction |

### Success Metrics

- Workflow dispatch latency: < 2s
- Monitoring poll interval: 30s (configurable), webhook-based < 5s
- End-to-end cycle time: < 15 min for typical issue
- Zero user code on Tamma infrastructure
- 100% audit trail coverage

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| GitHub Platform | Epic 1 | Octokit for GitHub API calls |
| Engine Core | Epic 10 | Event store for audit trail |
| ELSA Workflows | Epic 7 | Activities run inside ELSA |
| GitHub App Auth | Epic 1.5 | App credentials for dispatch |
| CLI Mode Preservation | Epic 22 | IAgentExecutor shared between modes |

## Story Files

[Story documents on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-19)
