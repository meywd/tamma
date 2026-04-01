---
title: "Story 19-3: Agent Execution Monitoring"
sidebar:
  order: 190
---

Status: ready-for-dev

## Story

As the **ELSA workflow engine**,
I want a `MonitorAgentWorkflowActivity` that tracks the status of a dispatched GitHub Actions workflow run until it completes (or times out),
so that the orchestration can wait for the agent to finish and then proceed to result collection.

## Acceptance Criteria

1. A new ELSA activity `MonitorAgentWorkflowActivity` exists in `Tamma.Activities/AgentDispatch/`
2. The activity accepts inputs:
   - `Repository` (string): `owner/repo` format
   - `BranchName` (string): The branch the workflow was dispatched on
   - `SessionId` (string): Tamma correlation ID (to match the dispatched run)
   - `DispatchedAfter` (DateTime): Timestamp from the dispatch step (to filter out old runs)
   - `PollIntervalSeconds` (int, default: 30)
   - `TimeoutMinutes` (int, default: 35) -- slightly longer than agent timeout
3. The activity resolves the correct workflow_run by querying:
   `GET /repos/{owner}/{repo}/actions/runs?branch={branch}&created=>{dispatched_after}&event=workflow_dispatch`
   and matching the most recent run on the target branch dispatched after the known dispatch time
4. The activity polls the workflow_run status until it reaches a terminal state:
   - `completed` (conclusion: `success`, `failure`, `cancelled`, `timed_out`)
5. The activity outputs:
   - `WorkflowRunId` (long): The GitHub workflow run ID
   - `Status` (string): Final status (`completed`)
   - `Conclusion` (string): `success`, `failure`, `cancelled`, `timed_out`
   - `WorkflowRunUrl` (string): HTML URL to the workflow run
   - `DurationSeconds` (int): Total execution time
   - `ArtifactsUrl` (string): API URL to download artifacts
6. The activity handles:
   - Workflow run not found within initial poll window (may take a few seconds to appear after dispatch)
   - Timeout exceeded -- activity completes with conclusion `timed_out`
   - GitHub API errors during polling -- retry with backoff, fail after 5 consecutive errors
7. The activity supports two monitoring modes:
   - **Poll mode** (default): Periodically query the workflow_run status
   - **Webhook mode** (future): ELSA bookmark that resumes when a `workflow_run.completed` webhook arrives
8. Events recorded:
   - `AGENT.MONITOR.STARTED` -- when monitoring begins
   - `AGENT.MONITOR.POLL` -- each poll (at debug level, not every poll recorded at info)
   - `AGENT.MONITOR.COMPLETED` -- when workflow_run reaches terminal state
   - `AGENT.MONITOR.TIMEOUT` -- if monitoring times out

## Technical Context

### The workflow_run Discovery Problem

The `workflow_dispatch` API (Story 19-2) returns `204 No Content` with no workflow_run ID. We must discover the workflow_run after dispatch. The approach:

1. Wait 5 seconds after dispatch (GitHub needs time to create the run)
2. Query `GET /repos/{owner}/{repo}/actions/runs` with filters:
   - `branch={branch_name}` -- match the dispatched branch
   - `event=workflow_dispatch` -- only workflow_dispatch events
   - `created>=<dispatched_after_iso>` -- only runs created after our dispatch
3. Take the most recent matching run
4. If no run found, retry with increasing intervals (5s, 10s, 20s) up to 2 minutes
5. If still not found, fail with clear error

### GitHub API: List Workflow Runs

```
GET /repos/{owner}/{repo}/actions/runs
  ?branch=tamma/issue-42-fix-login
  &event=workflow_dispatch
  &created=>=2026-03-28T10:00:00Z
  &per_page=5
  &sort=created
  &direction=desc

Response:
{
  "total_count": 1,
  "workflow_runs": [
    {
      "id": 12345678,
      "status": "in_progress",    // queued, in_progress, completed
      "conclusion": null,          // success, failure, cancelled, timed_out
      "html_url": "https://github.com/owner/repo/actions/runs/12345678",
      "created_at": "2026-03-28T10:00:05Z",
      "updated_at": "2026-03-28T10:05:00Z",
      "head_branch": "tamma/issue-42-fix-login",
      "event": "workflow_dispatch",
      "artifacts_url": "https://api.github.com/repos/owner/repo/actions/runs/12345678/artifacts"
    }
  ]
}
```

### GitHub API: Get a Workflow Run

Once the run ID is known, poll the specific run:

```
GET /repos/{owner}/{repo}/actions/runs/{run_id}

Response: Same schema as above, single object
```

### Polling Strategy

```
Phase 1: Discovery (0-120s after dispatch)
  Poll every 5s for the workflow_run to appear
  Max 24 attempts (120 seconds)
  If not found: AGENT.MONITOR.TIMEOUT with "workflow_run never appeared"

Phase 2: Monitoring (once run is found)
  Poll every 30s (configurable)
  Until status == "completed" OR timeout exceeded
  Log each poll at debug level
  Record AGENT.MONITOR.POLL event every 5th poll (reduce noise)

Phase 3: Terminal
  Record AGENT.MONITOR.COMPLETED with full details
  Return result to ELSA workflow
```

### Activity Class Structure

```csharp
namespace Tamma.Activities.AgentDispatch;

[Activity("Tamma", "Agent Monitor",
    Description = "Monitors a dispatched GitHub Actions workflow run until completion")]
public class MonitorAgentWorkflowActivity : CodeActivity<MonitorResult>
{
    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Branch the workflow was dispatched on")]
    public Input<string> BranchName { get; set; } = default!;

    [Input(Description = "Tamma session ID for correlation")]
    public Input<string> SessionId { get; set; } = default!;

    [Input(Description = "Timestamp of the dispatch (to filter old runs)")]
    public Input<DateTime> DispatchedAfter { get; set; } = default!;

    [Input(Description = "Poll interval in seconds")]
    public Input<int> PollIntervalSeconds { get; set; } = new(30);

    [Input(Description = "Timeout in minutes")]
    public Input<int> TimeoutMinutes { get; set; } = new(35);

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        // Phase 1: Discover the workflow_run
        // Phase 2: Poll until terminal state
        // Phase 3: Record events and return result
    }
}

public record MonitorResult(
    long WorkflowRunId,
    string Status,
    string Conclusion,
    string WorkflowRunUrl,
    int DurationSeconds,
    string ArtifactsUrl
);
```

### Webhook Mode (Future Enhancement)

Instead of polling, ELSA can create a bookmark and wait for a `workflow_run.completed` webhook:

1. `DispatchAgentWorkflowActivity` records the expected branch + session ID
2. `MonitorAgentWorkflowActivity` creates an ELSA bookmark with a key like `agent-run:{session_id}`
3. The GitHub webhook handler (Story 19-2 addition to `github-webhook.ts`) matches incoming `workflow_run.completed` events to bookmarks
4. ELSA resumes the workflow with the webhook payload

This eliminates polling entirely but requires webhook infrastructure. Implement poll mode first, add webhook mode as an optimization.

### Event Schema

```typescript
// AGENT.MONITOR.STARTED
{
  type: "AGENT.MONITOR.STARTED",
  tags: {
    repository: "owner/repo",
    sessionId: "sess_abc123",
    issueId: "42"
  },
  data: {
    branchName: "tamma/issue-42-fix-login",
    pollIntervalSeconds: 30,
    timeoutMinutes: 35,
    mode: "poll"
  }
}

// AGENT.MONITOR.COMPLETED
{
  type: "AGENT.MONITOR.COMPLETED",
  tags: {
    repository: "owner/repo",
    sessionId: "sess_abc123",
    issueId: "42"
  },
  data: {
    workflowRunId: 12345678,
    conclusion: "success",
    durationSeconds: 420,
    workflowRunUrl: "https://github.com/owner/repo/actions/runs/12345678"
  }
}
```

## Implementation Notes

### Files to Create

- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/MonitorAgentWorkflowActivity.cs`
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/Models/MonitorModels.cs`
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/WorkflowRunDiscovery.cs` -- discovery logic (testable)

### Files to Modify

- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/IGitHubActionsClient.cs` -- add `ListWorkflowRuns`, `GetWorkflowRun`
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/GitHubActionsClient.cs` -- implement new methods

### Long-Running Activity Concerns

The monitor activity will run for minutes. ELSA handles this correctly with `CodeActivity` + async execution, but:
- The activity must be cancellable (check `context.CancellationToken` in the poll loop)
- If the ELSA server restarts mid-poll, the activity should resume from the last known state (store `WorkflowRunId` in ELSA variables as soon as discovered)
- Consider using ELSA `Delay` activity in a loop instead of `Task.Delay` for better ELSA-native behavior

### Rate Limit Budget

Polling every 30s for 30 minutes = 60 API calls. At 1,000 req/hr per installation, this is well within limits even with 10 concurrent agent runs. Discovery phase (every 5s for 2 min) adds ~24 calls.

## Dependencies

- **Story 19-2**: `DispatchAgentWorkflowActivity` outputs `DispatchedAfter` timestamp
- **IGitHubActionsClient**: Interface from Story 19-2, extended with list/get workflow_run methods
- **InstallationRouter**: For token resolution (same as 19-2)

## Estimated Effort

**Size**: L (Large)
- Workflow run discovery logic: 2 days
- Poll loop with ELSA integration: 2 days
- Error handling (timeout, API errors, run not found): 1 day
- Event recording: 0.5 day
- Unit tests (mock GitHub API responses for all states): 2 days
- Integration tests (real GitHub Actions): 1 day

**Total**: ~8.5 days

## Testing Strategy

### Unit Tests
- Test workflow_run discovery with various API responses (0 runs, 1 run, multiple runs)
- Test polling through status transitions: `queued` -> `in_progress` -> `completed`
- Test conclusion types: `success`, `failure`, `cancelled`, `timed_out`
- Test discovery timeout (run never appears)
- Test monitoring timeout (run never completes)
- Test API error handling (5 consecutive errors -> fail)
- Test cancellation token handling

### Integration Tests
- Dispatch + monitor on `tamma-test-github` repository
- Verify discovery finds the correct workflow_run
- Verify polling completes when the run finishes
- Test with a workflow that fails (verify `conclusion: failure`)

### Edge Cases
- Multiple concurrent dispatches on different branches (verify correct run is selected)
- Re-run of a failed workflow (verify the new run is picked up, not the old one)
- GitHub Actions queue delay (run takes >1 minute to appear)
