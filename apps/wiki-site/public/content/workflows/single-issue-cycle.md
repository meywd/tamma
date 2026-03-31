---
title: "Workflow: Single Issue Cycle"
---

**Definition ID:** `single-issue-cycle`
**Class:** `SingleIssueCycleWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`

## Purpose

The Single Issue Cycle implements the **14-step autonomous development cycle** for one GitHub issue. It orchestrates the full lifecycle from issue selection through merge, dispatching specialized sub-workflows for each phase.

## Flow Diagram

```
+-------------+
| Init Config |
+------+------+
       |
       v
+-----------------+     +-----------+     +----------------+
| Select Issue    |---->| Extract   |---->| Issue Found?   |
| (issue-selection)|     | Issue Data|     +---+--------+---+
+-----------------+     +-----------+        |            |
                                           YES           NO
                                             |            |
                                             v            v
                                    +----------------+  [noIssues] --> Finish
                                    | Gather Context |
                                    | (context-      |
                                    |  gathering)    |
                                    +-------+--------+
                                            |
                                            v
                                    +----------------+     +-----------+
                                    | Generate Plan  |---->| Extract   |
                                    | (plan-         |     | Plan Data |
                                    |  generation)   |     +-----+-----+
                                    +----------------+           |
                                                                 v
                                                         +--------------+
                                                         |Plan Approved?|
                                                         +--+--------+--+
                                                           YES        NO
                                                            |          |
                                                            v          v
                                                    +-------------+  [plan_rejected]
                                                    | Create Branch|     --> Finish
                                                    | (branch-    |
                                                    |  creation)  |
                                                    +------+------+
                                                           |
                                                           v
                                                   +--------------+
                                                   |Branch Created?|
                                                   +--+--------+--+
                                                     YES        NO
                                                      |          |
                                                      v          v
                                              +----------------+ [error]
                                              | TDD with Debug |    --> Finish
                                              | Retry (tdd-    |
                                              | with-debug-    |
                                              | retry)         |
                                              +-------+--------+
                                                      |
                                                      v
                                              +-----------+
                                              |TDD Passed?|
                                              +--+-----+--+
                                                YES     NO
                                                 |       |
                                                 v       v
                                         +-----------+ [tddFailed]
                                         | Create PR |    --> Finish
                                         | (pull-    |
                                         |  request) |
                                         +-----+-----+
                                               |
                                               v
                                         +-----------+
                                         |PR Created?|
                                         +--+-----+--+
                                           YES     NO
                                            |       |
                                            v       v
              +--------------------+ [error] --> Finish
              | CI with Debug Retry|<---------+
              | (ci-with-debug-    |          |
              |  retry)            |          |
              +---------+----------+          |
                        |                     |
                        v                     |
                  +-----------+               |
                  | CI Passed?|               |
                  +--+-----+--+               |
                    YES     NO                |
                     |       |                |
                     v       v                |
            +----------------+ [ciFailed]     |
            | Review Fix     |   --> Finish   |
            | (review-fix)   |                |
            +-------+--------+                |
                    |                          |
                    v                          |
            +----------------+                |
            | Has Comments?  |                |
            +--+----------+--+                |
              YES          NO                 |
               |            |                 |
               +------->---+                  |
               |  (re-run   |                 |
               |   CI)      |                 |
               |            v                 |
               |    +----------------+        |
               |    | Merge Approval |        |
               |    | (merge-        |        |
               |    |  approval)     |        |
               |    +-------+--------+        |
               |            |                 |
               |            v                 |
               |    +---------------+         |
               |    |Merge Approved?|         |
               |    +--+---------+--+         |
               |      YES    NO               |
               |       |      |               |
               |       |      v               |
               |       | +------------+       |
               |       | |Run Tests?  |       |
               |       | +--+------+--+       |
               |       |   YES      NO        |
               |       |    |        |         |
               |       |    +--------+-------->+
               |       |             v
               |       |     [review_rejected]
               |       |        --> Finish
               |       v
               |  +-----------+
               |  | Merge PR  |
               |  | (merge-   |
               |  |  complete)|
               |  +-----+-----+
               |        |
               |        v
               |  +---------+
               |  | Merged? |
               |  +--+---+--+
               |    YES   NO
               |     |     |
               |     v     v
               | [success] [mergeFailed]
               |  --> Finish  --> Finish
```

## Steps

| Step | Sub-Workflow | Description |
|------|-------------|-------------|
| 1 | `issue-selection` | Queries GitHub for the next unassigned issue matching labels, assigns it to the bot |
| 2 | `context-gathering` | Gathers codebase context (story metadata, commits, files, tests, history, patterns) |
| 3 | `plan-generation` | Generates an AI implementation plan, waits for human approval (bookmark) |
| 4 | `branch-creation` | Creates a feature branch (e.g., `tamma/42-fix-login-bug`) |
| 5-7 | `tdd-with-debug-retry` | Runs TDD cycle with up to 3 debug retry iterations |
| 8 | `pull-request` | Creates a PR with plan summary, test results, and issue reference |
| 9 | `ci-with-debug-retry` | Runs CI pipeline with up to 3 debug retry iterations |
| 10 | `review-fix` | Analyzes PR review comments and applies AI-generated fixes |
| 11 | `merge-approval` | Waits for human merge/test/reject decision (bookmark) |
| 12 | `merge-complete` | Squash-merges PR, closes issue, deletes branch |

## Exit Reasons

All exit paths converge to a shared finish sequence that emits consistent outputs:

| Exit Reason | Trigger |
|-------------|---------|
| `success` | PR merged successfully |
| `noIssues` | No matching issues found |
| `plan_rejected` | Human rejected the plan |
| `error` | Branch creation or PR creation failed |
| `tddFailed` | TDD cycle failed after 3 debug retries |
| `ciFailed` | CI pipeline failed after 3 debug retries |
| `review_rejected` | Human rejected the merge |
| `mergeFailed` | Merge operation failed |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `exitReason` | string | Why the cycle ended (see table above) |
| `finishReason` | string | Same as exitReason (explicit name for analytics) |
| `success` | bool | `true` only if exitReason is `"success"` |
| `issueNumber` | int | GitHub issue number processed |
| `prNumber` | int | PR number created |
| `mergeSha` | string | Merge commit SHA (on success) |

## Review-Fix Loop

When `review-fix` finds actionable review comments:
1. The comments are analyzed and AI-generated fixes are applied
2. The workflow loops back to `ci-with-debug-retry` to re-run CI
3. After CI passes, merge approval is requested again

## Merge Approval Decisions

The human reviewer has three options at the merge approval bookmark:
- **merge** -- Proceeds to merge the PR
- **test** -- Loops back to `ci-with-debug-retry` for another test run
- **reject** -- Terminates with `review_rejected`

## Known Issues

The `ciRetryCount` variable is passed through across re-entries (review-fix, merge re-test). This means the debug retry counter persists across these loops, which may cause premature failure if earlier CI runs already consumed retries. This is tracked as a separate fix ticket.

---

## Sub-Workflow Details

### Issue Selection

**Definition ID:** `issue-selection`
**Class:** `IssueSelectionWorkflow`

A simple linear workflow:
1. `SelectIssueActivity` -- Queries GitHub for unassigned issues matching the label filter and assigns the selected issue to the bot
2. Outputs: `success` (bool), `issueJson`, `issueNumber`, `issueTitle`

### Plan Generation

**Definition ID:** `plan-generation`
**Class:** `PlanGenerationWorkflow`

Uses a While loop for an approval cycle:
1. Dispatches `llm-call` to generate a plan from issue + context
2. Creates a bookmark (`WaitForPlanApprovalActivity`) for human review
3. If the human requests edits, the feedback is fed back into the LLM prompt and the loop repeats
4. If approved, outputs the plan JSON

The plan prompt is sanitized via `SecurityHelpers.SanitizeForPrompt()` before being sent to the LLM.

### Branch Creation

**Definition ID:** `branch-creation`
**Class:** `BranchCreationWorkflow`

A simple linear workflow:
1. `CreateBranchActivity` -- Creates a feature branch from the base branch
2. Outputs: `success` (bool), `branchName` (string)

### Pull Request

**Definition ID:** `pull-request`
**Class:** `PullRequestWorkflow`

A simple linear workflow:
1. `CreatePullRequestActivity` -- Creates a PR with issue title, plan summary, and test results
2. Outputs: `success` (bool), `prNumber` (int), `prUrl` (string)

### Merge Approval

**Definition ID:** `merge-approval`
**Class:** `MergeApprovalWorkflow`

A bookmark-based waiting workflow:
1. `WaitForMergeApprovalActivity` -- Suspends execution and waits for a human decision
2. The human can respond with `merge`, `test`, or `reject`
3. Outputs: `decision` (string), `feedback` (string)

### Merge

**Definition ID:** `merge-complete`
**Class:** `MergeWorkflow`

A simple linear workflow:
1. `MergePullRequestActivity` -- Squash-merges the PR, closes the issue, deletes the branch
2. Outputs: `success` (bool), `mergeSha` (string)

---

_See also: [ADL Orchestrator](Workflow-ADL-Orchestrator) | [TDD Cycle](Workflow-TDD-Cycle) | [Testing](Workflow-Testing) | [Workflows Index](Workflows)_
