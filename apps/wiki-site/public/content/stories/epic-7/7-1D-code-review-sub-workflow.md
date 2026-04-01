---
title: "Story 7-1D: Code Review Sub-Workflow"
sidebar:
  order: 70
---

## User Story

As the **Tamma mentorship engine**, I need a reusable ELSA workflow that manages the full PR lifecycle — from creation through review monitoring, fix guidance, and merge — so that code reviews are auditable, resumable, and provide teaching-oriented feedback to junior developers.

## Description

Implement an ELSA code-first workflow (`CodeReviewWorkflow`) that orchestrates the complete pull request lifecycle. The workflow creates a PR, assigns reviewers, waits for review results via bookmark (pausing until webhook callback), and handles the review outcome: approve → merge, changes requested → guide fixes → re-request review. Each review iteration is a visible ELSA activity with full audit trail.

**Enhances**: Story 7-9 (Code Review & Merge Workflow)

## Acceptance Criteria

### AC1: Workflow Registration
- [ ] Workflow defined as C# code-first `IWorkflow` in `Tamma.ElsaServer/Workflows/CodeReviewWorkflow.cs`
- [ ] Registered at startup via `services.AddWorkflow<CodeReviewWorkflow>()`
- [ ] Visible in ELSA Studio as "Code Review" workflow
- [ ] Can be invoked standalone via ELSA REST API
- [ ] Can be invoked as child workflow via `RunWorkflow`

### AC2: Input/Output Contract
- [ ] **Inputs**:
  - `sessionId` (Guid) — mentorship session ID
  - `storyId` (string) — story identifier
  - `repositoryUrl` (string) — repository URL
  - `branchName` (string) — source branch for PR
  - `baseBranch` (string, default: "main") — target branch
  - `juniorId` (string) — junior developer identifier
  - `reviewerIds` (string[], optional) — specific reviewers to assign
- [ ] **Outputs**: `ReviewResult` record containing:
  - `status` (enum: `Approved`, `Rejected`, `Escalated`, `Timeout`)
  - `prNumber` (int) — created PR number
  - `prUrl` (string) — PR URL
  - `mergeCommit` (string, optional) — merge commit SHA
  - `reviewIterations` (int) — number of review rounds
  - `reviewComments` (ReviewComment[]) — all review comments
  - `guidanceFeedback` (string[]) — teaching feedback provided to junior

### AC3: PR Creation
- [ ] `CreatePR` activity creates a pull request via Git platform API:
  - Title auto-generated from story ID and description
  - Body includes: story link, implementation summary (from session context), checklist
  - Labels: `mentorship`, skill level label, story label
  - Draft PR if `draft` option is set
- [ ] PR number stored in workflow variables for subsequent activities
- [ ] Failure to create PR → fault with context (branch not found, permissions, etc.)

### AC4: Reviewer Assignment
- [ ] `RequestReview` activity assigns reviewers:
  - Uses `reviewerIds` from input if provided
  - Otherwise: selects reviewers from configured reviewer pool
  - Team-based assignment supported (e.g., `@team/backend-reviewers`)
  - Minimum 1 reviewer required
- [ ] Notification sent to reviewers (via configured channel: Slack, email, or GitHub notification)

### AC5: Review Monitoring (Bookmark-Based)
- [ ] `MonitorReview` activity creates bookmark and pauses workflow:
  - Bookmark name: `review-{sessionId}-{prNumber}`
  - Resumes when webhook callback reports review event
  - Handles: `approved`, `changes_requested`, `commented`, `dismissed`
- [ ] Review timeout: 24 hours default (configurable)
- [ ] Timeout → `Escalate` outcome

### AC6: Review Outcome Routing
- [ ] `FlowDecision` routes based on review status:
  - **Approved** → `MergeAndComplete` activity
  - **Changes Requested** → `AnalyzeChanges` → `GenerateGuidance` → `DeliverGuidance` → `WaitForFixes`
  - **Commented** (no decision) → continue waiting
  - **Timeout** → `Escalate`

### AC7: Fix Guidance Loop
- [ ] On "Changes Requested":
  1. `AnalyzeChanges`: RunWorkflow: LlmCall (7-1B, role=`reviewer`, "what needs fixing based on these review comments: {comments}")
  2. `GenerateGuidance`: RunWorkflow: LlmCall (7-1B, role=`analyst`, "explain to a Level {skillLevel} developer how to address: {analysis}")
  3. `DeliverGuidance`: send guidance via configured channel (Slack DM, PR comment, or API)
  4. `WaitForFixes`: bookmark — pauses until new commits pushed to PR branch
     - Bookmark name: `fixes-{sessionId}-{prNumber}-{iteration}`
     - Timeout: 60 minutes default (configurable)
  5. `ReRequestReview`: re-request review from same reviewers via API
  6. `MonitorReview`: loop back to review monitoring
- [ ] Maximum 5 review iterations (configurable)
- [ ] Max iterations exceeded → `Escalate`

### AC8: Merge and Completion
- [ ] `MergeAndComplete` activity:
  - Merge strategy: squash (default), merge, or rebase (configurable)
  - Delete source branch after merge (configurable, default: true)
  - Verify CI passes before merge (configurable, default: true)
  - Returns merge commit SHA
- [ ] Merge failure (conflict, CI failure) → retry once, then escalate

### AC9: Escalation
- [ ] `Escalate` activity:
  - Notifies senior developer / team lead via configured channel
  - Includes: PR link, review history, guidance provided, reason for escalation
  - Creates bookmark — pauses until senior responds
  - Senior can: approve override, request more changes, or cancel

### AC10: Observability
- [ ] Each review iteration logged: iteration number, reviewer, decision, timestamp, duration
- [ ] Guidance feedback logged: what was explained, skill level, channel used
- [ ] Metrics: `review.iterations.total`, `review.time_to_approve`, `review.escalations.total`

## Technical Design

### Workflow Structure (Pseudocode)

```
Flowchart: CodeReviewWorkflow
├── ValidateInputs
├── CreatePR
├── RequestReview
├── MonitorReview (bookmark — wait for webhook)
├── FlowDecision: ReviewStatus
│   ├── Approved → VerifyCIBeforeMerge → MergeAndComplete → SetOutputs
│   ├── ChangesRequested:
│   │   ├── AnalyzeChanges (RunWorkflow: LlmCall, role=reviewer)
│   │   ├── GenerateGuidance (RunWorkflow: LlmCall, role=analyst)
│   │   ├── DeliverGuidance (Slack/PR comment)
│   │   ├── WaitForFixes (bookmark — wait for commits)
│   │   ├── ReRequestReview
│   │   ├── FlowDecision: MaxIterations?
│   │   │   ├── No → MonitorReview (loop)
│   │   │   └── Yes → Escalate
│   │   └── [loop max 5 times]
│   ├── Timeout → Escalate
│   └── Commented → MonitorReview (continue waiting)
├── Escalate (bookmark — wait for senior)
└── SetOutputs
```

### Custom Activities

```csharp
[Activity("Tamma.Review", "Create PR", "Create pull request via Git platform API")]
public class CreatePRActivity : CodeActivity<PRCreationResult> { ... }

[Activity("Tamma.Review", "Request Review", "Assign reviewers to pull request")]
public class RequestReviewActivity : CodeActivity<ReviewRequestResult> { ... }

[Activity("Tamma.Review", "Monitor Review", "Wait for review decision via bookmark")]
public class MonitorReviewActivity : Activity { ... }  // bookmark-based

[Activity("Tamma.Review", "Deliver Guidance", "Send fix guidance to junior via channel")]
public class DeliverGuidanceActivity : CodeActivity<DeliveryResult> { ... }

[Activity("Tamma.Review", "Wait For Fixes", "Wait for new commits on PR branch")]
public class WaitForFixesActivity : Activity { ... }  // bookmark-based

[Activity("Tamma.Review", "Re-Request Review", "Re-request review after fixes")]
public class ReRequestReviewActivity : CodeActivity<ReviewRequestResult> { ... }

[Activity("Tamma.Review", "Merge And Complete", "Merge PR and clean up")]
public class MergeAndCompleteActivity : CodeActivity<MergeResult> { ... }

[Activity("Tamma.Review", "Escalate Review", "Escalate to senior developer")]
public class EscalateReviewActivity : Activity { ... }  // bookmark-based
```

### Output Schema

```csharp
public record ReviewResult
{
    public ReviewStatus Status { get; init; }
    public int PrNumber { get; init; }
    public string PrUrl { get; init; } = string.Empty;
    public string? MergeCommit { get; init; }
    public int ReviewIterations { get; init; }
    public List<ReviewComment> ReviewComments { get; init; } = new();
    public List<string> GuidanceFeedback { get; init; } = new();
}

public enum ReviewStatus { Approved, Rejected, Escalated, Timeout }

public record ReviewComment
{
    public string Reviewer { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string? File { get; init; }
    public int? Line { get; init; }
    public DateTime Timestamp { get; init; }
}
```

## Dependencies

- **7-1B (LLM Call)**: for change analysis and guidance generation
- `Tamma.Activities.Integration.GitHubActivity` (existing) — for PR operations
- `Tamma.Activities.Integration.SlackActivity` (existing) — for notifications
- Git platform API (GitHub, GitLab, etc.) for PR creation, review, merge
- Webhook endpoint for review events (resumes bookmarks)
- ELSA 3.x `Flowchart`, `FlowDecision`, `RunWorkflow`, bookmark system

## Files to Create/Modify

| File | Action | Purpose |
|------|--------|---------|
| `Tamma.ElsaServer/Workflows/CodeReviewWorkflow.cs` | Create | Code-first workflow |
| `Tamma.Activities/Review/CreatePRActivity.cs` | Create | PR creation |
| `Tamma.Activities/Review/RequestReviewActivity.cs` | Create | Reviewer assignment |
| `Tamma.Activities/Review/MonitorReviewActivity.cs` | Create | Bookmark-based wait |
| `Tamma.Activities/Review/DeliverGuidanceActivity.cs` | Create | Guidance delivery |
| `Tamma.Activities/Review/WaitForFixesActivity.cs` | Create | Bookmark-based wait |
| `Tamma.Activities/Review/ReRequestReviewActivity.cs` | Create | Re-request review |
| `Tamma.Activities/Review/MergeAndCompleteActivity.cs` | Create | PR merge |
| `Tamma.Activities/Review/EscalateReviewActivity.cs` | Create | Escalation |
| `Tamma.Activities/Review/Models/` | Create | DTOs |
| `Tamma.ElsaServer/Program.cs` | Modify | Register workflow |

## Testing Strategy

### Unit Tests
- PR creation with correct title, body, labels
- Reviewer assignment from pool when no explicit reviewers
- Review routing: approved → merge, changes_requested → guidance loop
- Max iteration guard after 5 review cycles
- Merge strategy selection (squash/merge/rebase)

### Integration Tests
- Full workflow: create PR → review approved → merge (mock Git API)
- Review iteration: changes_requested → guidance → fixes → re-review → approved
- Bookmark resume: workflow pauses at review wait, resumes on webhook
- Escalation: max iterations → senior notification
- Standalone invocation via ELSA REST API

## Configuration

```json
{
  "CodeReview": {
    "MaxReviewIterations": 5,
    "ReviewTimeoutHours": 24,
    "FixTimeoutMinutes": 60,
    "MergeStrategy": "squash",
    "DeleteBranchAfterMerge": true,
    "VerifyCIBeforeMerge": true,
    "NotificationChannel": "slack",
    "ReviewerPool": ["senior-dev-1", "senior-dev-2"],
    "AutoAssignReviewers": true
  }
}
```

## Success Metrics

- PR created with correct metadata in <5 seconds
- Review webhook resumes workflow within 2 seconds of receipt
- Guidance generation completes within 30 seconds
- Fix guidance loop correctly limits to max iterations
- Merge succeeds with configured strategy
- All review iterations visible in ELSA Studio

## Logging Requirements

All ELSA activities MUST inject `ILogger<T>` and log at these levels:

- **INFO**: Activity started (with session/issue ID), activity completed (with outcome), state transitions
- **DEBUG**: Input parameters received, intermediate LLM/API call details, decision rationale
- **WARN**: Retryable failures, timeout approaching, degraded quality gate result
- **ERROR**: Unrecoverable failures (with exception), invalid state transition, missing required data
- **Structured context**: Always include `{ sessionId, juniorId, storyId, currentState }` in all log entries
- **Sensitive data**: NEVER log student PII, credentials, or full LLM response content — log token counts and summary only
