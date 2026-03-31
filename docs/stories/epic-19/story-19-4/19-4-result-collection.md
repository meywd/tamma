# Story 19-4: Result Collection

Status: ready-for-dev

## Story

As the **ELSA workflow engine**,
I want a `CollectAgentResultsActivity` that reads the outputs of a completed agent workflow run -- PR data, check results, changed files, and the structured result artifact,
so that the orchestration can continue with the agent's work products as if it had run the agent locally.

## Acceptance Criteria

1. A new ELSA activity `CollectAgentResultsActivity` exists in `Tamma.Activities/AgentDispatch/`
2. The activity accepts inputs:
   - `Repository` (string): `owner/repo` format
   - `WorkflowRunId` (long): From the monitoring step
   - `BranchName` (string): The branch the agent worked on
   - `IssueNumber` (int): The issue the agent was working on
   - `SessionId` (string): Tamma correlation ID
   - `Conclusion` (string): From the monitoring step (`success`, `failure`, etc.)
3. The activity collects:
   - **Result artifact**: Downloads `.tamma/result.json` from workflow run artifacts
   - **PR data**: If the agent created a PR, reads PR number, title, body, changed files count
   - **Check results**: Reads check run results on the branch (CI passed/failed)
   - **Changed files**: Lists files changed on the branch since branching point
   - **Commit history**: Lists commits on the branch since branching point
4. The activity outputs a unified `AgentExecutionResult`:
   - `Success` (bool): Whether the agent completed its task successfully
   - `PrNumber` (int?): PR number if created
   - `PrUrl` (string?): PR HTML URL
   - `CommitSha` (string): HEAD commit SHA on the branch
   - `FilesChanged` (string[]): List of changed file paths
   - `CommitsCount` (int): Number of commits made by the agent
   - `ChecksPassed` (bool?): Whether CI checks passed (null if not yet run)
   - `TokensUsed` (int): Total tokens consumed by the agent
   - `DurationSeconds` (int): Agent execution time
   - `ErrorMessage` (string?): Error details if the agent failed
   - `AgentLogSummary` (string?): Summary from the agent's logs
5. The activity handles these cases:
   - Workflow completed with `success` but no PR created (agent may have only committed)
   - Workflow completed with `failure` -- still collect what's available
   - Result artifact not found (agent crashed before producing it) -- infer from git state
   - PR not found for the branch -- agent may not have created one yet
6. Events recorded:
   - `AGENT.RESULTS.COLLECTED` -- successful collection with summary
   - `AGENT.RESULTS.PARTIAL` -- some data unavailable (artifact missing, etc.)
   - `AGENT.RESULTS.FAILED` -- collection itself failed

## Technical Context

### Data Collection Sequence

```
1. Download result artifact from workflow run
   GET /repos/{owner}/{repo}/actions/runs/{run_id}/artifacts
   GET /repos/{owner}/{repo}/actions/artifacts/{artifact_id}/zip
   → Parse .tamma/result.json

2. Find PR for the branch
   GET /repos/{owner}/{repo}/pulls?head={owner}:{branch}&state=open
   → PR number, title, body, changed_files_count

3. Get changed files on the branch
   GET /repos/{owner}/{repo}/compare/{base}...{branch}
   → files[].filename, files[].status, files[].changes

4. Get check results on the branch HEAD
   GET /repos/{owner}/{repo}/commits/{sha}/check-runs
   → check_runs[].conclusion

5. Get commits on the branch
   GET /repos/{owner}/{repo}/compare/{base}...{branch}
   → commits[].sha, commits[].commit.message
```

### Artifact Download

GitHub Actions artifacts are zip files. The download flow:

```
GET /repos/{owner}/{repo}/actions/runs/{run_id}/artifacts
→ { "artifacts": [{ "id": 123, "name": "tamma-result", "expired": false }] }

GET /repos/{owner}/{repo}/actions/artifacts/{artifact_id}/zip
→ Binary zip content

Unzip → result.json
```

The artifact name is `tamma-result` (matching the upload step in the workflow template from Story 19-1).

### Result Artifact Schema (from Story 19-1)

```typescript
interface AgentResult {
  success: boolean;
  task: string;
  issue_number: number;
  branch_name: string;
  tamma_session_id: string;
  files_changed: string[];
  pr_number: number | null;
  commit_sha: string;
  error_message: string | null;
  agent_log_summary: string;
  tokens_used: number;
  duration_seconds: number;
  agent_provider: string;
  agent_version: string;
}
```

### Activity Class Structure

```csharp
namespace Tamma.Activities.AgentDispatch;

[Activity("Tamma", "Collect Results",
    Description = "Collects results from a completed agent workflow run")]
public class CollectAgentResultsActivity : CodeActivity<AgentExecutionResult>
{
    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Workflow run ID from monitoring step")]
    public Input<long> WorkflowRunId { get; set; } = default!;

    [Input(Description = "Branch the agent worked on")]
    public Input<string> BranchName { get; set; } = default!;

    [Input(Description = "Issue number")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Tamma session ID")]
    public Input<string> SessionId { get; set; } = default!;

    [Input(Description = "Workflow conclusion from monitoring")]
    public Input<string> Conclusion { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        // 1. Download and parse result artifact
        // 2. Find PR for the branch
        // 3. Get changed files via compare API
        // 4. Get check run results
        // 5. Merge data into AgentExecutionResult
        // 6. Record events
        // 7. Set output
    }
}
```

### Unified Result Model

```csharp
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
    string? AgentVersion
);
```

This model is designed to be a drop-in replacement for what the existing TDD/implementation steps produce. Story 19-5 maps this to the existing workflow variable schema.

### Fallback When Artifact Is Missing

If the agent crashed or timed out, the result artifact may not exist. In this case:

1. Check if there are commits on the branch (via compare API)
2. Check if a PR exists for the branch
3. Construct a partial result:
   - `Success = false`
   - `FilesChanged` from compare API
   - `CommitSha` from branch HEAD
   - `ErrorMessage = "Agent workflow completed with conclusion: {conclusion}, no result artifact found"`

### Event Schema

```typescript
// AGENT.RESULTS.COLLECTED
{
  type: "AGENT.RESULTS.COLLECTED",
  tags: {
    repository: "owner/repo",
    sessionId: "sess_abc123",
    issueId: "42"
  },
  data: {
    workflowRunId: 12345678,
    success: true,
    prNumber: 99,
    filesChanged: 5,
    commitsCount: 3,
    tokensUsed: 45000,
    durationSeconds: 420,
    agentProvider: "claude-code",
    checksPassed: true
  }
}
```

## Implementation Notes

### Files to Create

- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/CollectAgentResultsActivity.cs`
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/Models/ResultModels.cs`
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/ArtifactDownloader.cs` -- zip download + parse

### Files to Modify

- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/IGitHubActionsClient.cs` -- add artifact methods
- `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/GitHubActionsClient.cs` -- implement artifact download

### Existing IGitPlatform Reuse

The compare API, PR listing, and check run queries can leverage the existing `GitHubPlatform` class:
- `getCIStatus(owner, repo, ref)` -- for check runs
- `listCommits(owner, repo, { sha: branch })` -- for commits

The artifact download is GitHub Actions-specific and not in `IGitPlatform`, so it goes in `IGitHubActionsClient`.

### ZIP Handling

Use `System.IO.Compression.ZipArchive` to extract the result JSON from the downloaded artifact zip. The artifact contains:
```
tamma-result/
  result.json
```

Handle cases where the zip structure is different or the file is missing.

### Mapping to Existing Workflow Variables

The `AgentExecutionResult` maps to the variables used by `SingleIssueCycleWorkflow`:
- `Success` -> used by `TddRetrySuccess` and `CiRetryPassed` decisions
- `PrNumber` -> used by `prNumber` variable
- `PrUrl` -> used by `prUrl` variable
- `CommitSha` -> used by `mergeSha` output
- `FilesChanged` -> used by TDD output `filesChanged`

Story 19-5 handles the actual variable mapping.

## Dependencies

- **Story 19-1**: Defines the artifact schema and upload format
- **Story 19-3**: Provides `WorkflowRunId` and `Conclusion`
- **IGitHubActionsClient**: Extended with artifact methods
- **GitHubPlatform**: For PR, compare, and check run queries

## Estimated Effort

**Size**: M (Medium)
- Artifact download and parsing: 1.5 days
- PR and git data collection: 1 day
- Check run collection: 0.5 day
- Fallback logic (missing artifact): 1 day
- Event recording: 0.5 day
- Unit tests: 1.5 days
- Integration tests: 1 day

**Total**: ~7 days

## Testing Strategy

### Unit Tests
- Test artifact download and zip extraction (mock HTTP responses)
- Test result JSON parsing (valid, invalid, missing fields)
- Test PR discovery (found, not found, multiple PRs)
- Test compare API parsing (files changed, commits)
- Test check run aggregation (all pass, some fail, pending)
- Test fallback when artifact is missing
- Test when workflow conclusion is `failure` vs `success`

### Integration Tests
- Run full dispatch -> monitor -> collect cycle on `tamma-test-github`
- Verify artifact download works with real GitHub Actions artifact
- Verify PR data matches what the agent created
- Test with a workflow that produces no artifact (agent failure scenario)

### Edge Cases
- Artifact expired (GitHub deletes after retention period)
- Branch deleted after agent run
- PR created but CI still running (checks pending)
- Agent created multiple PRs (should take the one matching the branch)
- Empty compare (agent made no changes)
