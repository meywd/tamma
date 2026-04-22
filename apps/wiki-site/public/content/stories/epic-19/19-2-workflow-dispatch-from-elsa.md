---
title: "Story 19-2: Workflow Dispatch from ELSA"
sidebar:
  order: 190
---

Status: done

## Implementation Notes

- Shipped as `DispatchAgentWorkflowActivity` + `AgentDispatchService` in `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/`.
- Octokit-backed `OctokitGitHubActionsClient` lives in `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/` and uses the existing `OctokitGitHubAppClient` + `IRepoInstallationResolver` infrastructure (satisfies AC-7 without duplicating auth code).
- AC coverage: all 8 ACs done. Events emitted via `TammaEventEmitter` in the workflow event bag with type prefix `AGENT.DISPATCH`.
- Commit: `ed92e54` `feat(agent-dispatch): DispatchAgentWorkflowActivity + service [story 19-2]`. Types + interface in `67901c3`.

## Story

As the **ELSA workflow engine**,
I want a `DispatchAgentWorkflowActivity` that triggers a `workflow_dispatch` event on the user's repository via the GitHub App API,
so that the TDD cycle, code generation, and other agent-dependent steps can be executed on the user's own GitHub Actions runner instead of locally.

## Acceptance Criteria

1. A new ELSA activity `DispatchAgentWorkflowActivity` exists in `Tamma.Activities/AgentDispatch/`
2. The activity accepts inputs:
   - `Repository` (string): `owner/repo` format
   - `BranchName` (string): Target branch for the agent to work on
   - `IssueNumber` (int): The issue being worked on
   - `Task` (string): Task type (`implement`, `fix`, `debug`, `review`, `test`)
   - `PlanJson` (string): Serialized development plan
   - `SessionId` (string): Tamma correlation ID
   - `AgentProvider` (string, default: `claude-code`)
   - `AgentConfigJson` (string, optional)
   - `WorkflowFileName` (string, default: `tamma-agent.yml`)
   - `TimeoutMinutes` (int, default: 30)
3. The activity uses the GitHub App installation token (resolved from `InstallationRouter`) to call:
   `POST /repos/{owner}/{repo}/actions/workflows/{workflow_id}/dispatches`
4. The activity outputs:
   - `DispatchSuccess` (bool): Whether the dispatch API call succeeded
   - `WorkflowRunUrl` (string): URL to the workflow run (if available)
   - `ErrorMessage` (string): Error details if dispatch failed
5. The activity handles these error cases:
   - Workflow file not found in repo (404) -- clear error message telling user to add template
   - Permission denied (403) -- clear error about GitHub App permissions
   - Rate limited (429) -- retry with exponential backoff
   - Branch not found -- clear error
6. The activity records events to the event store:
   - `AGENT.DISPATCH.REQUESTED` -- before the API call
   - `AGENT.DISPATCH.SUCCESS` -- after successful dispatch
   - `AGENT.DISPATCH.FAILED` -- if dispatch fails
7. The activity resolves the GitHub App installation token using the existing `InstallationRouter` infrastructure
8. The activity validates that the repository has the tamma-agent workflow file before dispatching (via `GET /repos/{owner}/{repo}/actions/workflows`)

## Technical Context

### GitHub API: Create a Workflow Dispatch Event

```
POST /repos/{owner}/{repo}/actions/workflows/{workflow_id}/dispatches
Authorization: token <installation_token>
Accept: application/vnd.github+json

{
  "ref": "tamma/issue-42-fix-login",
  "inputs": {
    "issue_number": "42",
    "task": "implement",
    "plan_json": "{...}",
    "branch_name": "tamma/issue-42-fix-login",
    "tamma_session_id": "sess_abc123",
    "agent_provider": "claude-code",
    "agent_config_json": "{}"
  }
}
```

Response: `204 No Content` on success. No body. No workflow_run ID returned.

This is a critical design constraint: the dispatch API does **not** return a workflow_run ID. Story 19-3 must handle this by polling for the workflow_run that matches the branch + timing.

### Workflow ID Resolution

The `workflow_id` parameter can be either:
- A numeric ID (from `GET /repos/{owner}/{repo}/actions/workflows`)
- A filename (e.g., `tamma-agent.yml`)

We use the filename approach for simplicity. GitHub resolves it server-side.

### Installation Token Resolution

The existing `InstallationRouter` (in `packages/api/src/services/installation-router.ts`) caches installation tokens. The ELSA activity needs access to this service. Options:

1. **Inject via DI**: Register `InstallationRouter` in the ELSA DI container and inject into the activity
2. **Call Tamma API**: The activity calls `GET /api/internal/installation/{installationId}/token` to get a fresh token

Option 1 is preferred for performance. The activity receives `IInstallationRouter` via constructor injection.

### Activity Class Structure

```csharp
namespace Tamma.Activities.AgentDispatch;

[Activity("Tamma", "Agent Dispatch",
    Description = "Dispatches a workflow_dispatch event to run an agent on the user's GitHub Actions runner")]
public class DispatchAgentWorkflowActivity : CodeActivity<DispatchResult>
{
    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Branch for the agent to work on")]
    public Input<string> BranchName { get; set; } = default!;

    [Input(Description = "Issue number")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Task type: implement, fix, debug, review, test")]
    public Input<string> Task { get; set; } = default!;

    [Input(Description = "Serialized development plan")]
    public Input<string> PlanJson { get; set; } = default!;

    [Input(Description = "Tamma session ID for correlation")]
    public Input<string> SessionId { get; set; } = default!;

    [Input(Description = "Agent provider (claude-code, aider, etc.)")]
    public Input<string> AgentProvider { get; set; } = new("claude-code");

    [Input(Description = "Additional agent config JSON")]
    public Input<string> AgentConfigJson { get; set; } = new("{}");

    [Input(Description = "Workflow file name in the repo")]
    public Input<string> WorkflowFileName { get; set; } = new("tamma-agent.yml");

    [Input(Description = "Timeout in minutes for the agent workflow")]
    public Input<int> TimeoutMinutes { get; set; } = new(30);

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        // 1. Parse repository into owner/repo
        // 2. Resolve installation token via IInstallationRouter
        // 3. Validate workflow file exists in repo
        // 4. Record AGENT.DISPATCH.REQUESTED event
        // 5. Call GitHub API to create workflow_dispatch
        // 6. Record AGENT.DISPATCH.SUCCESS or AGENT.DISPATCH.FAILED
        // 7. Set output
    }
}

public record DispatchResult(
    bool Success,
    string? WorkflowRunUrl,
    string? ErrorMessage,
    DateTime DispatchedAt
);
```

### Integration with SingleIssueCycleWorkflow

The `DispatchAgentWorkflowActivity` replaces the direct agent invocation in the TDD cycle. In SaaS mode, instead of calling the LLM directly to generate code, the workflow:

1. Dispatches the agent to the user's runner
2. Waits for completion (Story 19-3)
3. Collects results (Story 19-4)

The existing `TddWorkflow` will need modification to use the `IAgentExecutor` abstraction (Story 19-5) rather than calling this activity directly.

### Event Schema

```typescript
// AGENT.DISPATCH.REQUESTED
{
  type: "AGENT.DISPATCH.REQUESTED",
  tags: {
    repository: "owner/repo",
    issueId: "42",
    sessionId: "sess_abc123",
    agentProvider: "claude-code"
  },
  data: {
    branchName: "tamma/issue-42-fix-login",
    task: "implement",
    workflowFileName: "tamma-agent.yml",
    timeoutMinutes: 30
  }
}

// AGENT.DISPATCH.SUCCESS
{
  type: "AGENT.DISPATCH.SUCCESS",
  tags: {
    repository: "owner/repo",
    issueId: "42",
    sessionId: "sess_abc123"
  },
  data: {
    dispatchedAt: "2026-03-28T10:00:00.000Z",
    ref: "tamma/issue-42-fix-login",
    httpStatus: 204
  }
}

// AGENT.DISPATCH.FAILED
{
  type: "AGENT.DISPATCH.FAILED",
  tags: {
    repository: "owner/repo",
    issueId: "42",
    sessionId: "sess_abc123"
  },
  data: {
    errorMessage: "Workflow file tamma-agent.yml not found in repository",
    httpStatus: 404,
    retryable: false
  }
}
```

## Implementation Notes

### Files to Create

- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/DispatchAgentWorkflowActivity.cs`
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/Models/DispatchModels.cs`
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/IGitHubActionsClient.cs` -- interface for testability
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/GitHubActionsClient.cs` -- Octokit-based implementation

### Files to Modify

- `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` -- register new activity and services
- GitHub App manifest -- add `actions:write` permission if not present

### GitHub App Permission Change

The Tamma GitHub App must request `actions: write` permission. This is a **breaking change** for existing installations -- users will need to approve the new permission. The app should handle the case where the permission has not yet been granted (403 response).

### Rate Limiting

GitHub Actions API has a rate limit of 1,000 requests per hour for GitHub App installations. The dispatch call is lightweight (one API call per agent invocation), so this is unlikely to be a bottleneck. Still, implement retry with exponential backoff for 429 responses.

### Input Size Limit

GitHub Actions `workflow_dispatch` inputs are limited to 10 inputs, each max 65,535 characters. The `plan_json` input could be large for complex plans. If it exceeds the limit:
1. Upload the plan as a gist or repository file
2. Pass a reference (gist URL or file path) instead of the full JSON
3. The workflow template downloads the plan before invoking the agent

## Dependencies

- **Story 19-1**: The workflow template must exist in the user's repo
- **GitHub App Permissions**: `actions:write` must be granted
- **InstallationRouter**: Must be accessible from ELSA activity context
- **Existing Infrastructure**: `GitHubPlatform` class, Octokit SDK

## Estimated Effort

**Size**: L (Large)
- Activity implementation: 2 days
- GitHub Actions client (API wrapper): 1.5 days
- Installation token resolution wiring: 1 day
- Event recording: 0.5 day
- Error handling and edge cases: 1 day
- Unit tests: 1.5 days
- Integration tests (real GitHub API): 1 day
- GitHub App permission update: 0.5 day

**Total**: ~9 days

## Testing Strategy

### Unit Tests
- Test dispatch with valid inputs (mock HTTP 204 response)
- Test dispatch with workflow not found (mock HTTP 404)
- Test dispatch with permission denied (mock HTTP 403)
- Test dispatch with rate limit (mock HTTP 429 + retry)
- Test repository parsing (`owner/repo` format validation)
- Test installation token resolution
- Test event recording (mock event store)
- Test input size validation (plan_json exceeds limit)

### Integration Tests
- Dispatch to `tamma-test-github` repository (requires real GitHub App)
- Verify workflow_run appears in GitHub Actions UI
- Test with suspended installation (should fail gracefully)
- Test with revoked `actions` permission (should fail with clear message)

### Validation Steps
1. Set up test repository with `tamma-agent.yml`
2. Configure GitHub App with `actions:write`
3. Run ELSA workflow that includes `DispatchAgentWorkflowActivity`
4. Verify dispatch succeeds (HTTP 204)
5. Verify workflow_run appears on GitHub
6. Verify events recorded in event store
