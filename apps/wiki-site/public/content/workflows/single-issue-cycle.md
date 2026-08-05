---
title: "Workflow: Single Issue Cycle"
---

**Definition ID:** `single-issue-cycle`
**Class:** `SingleIssueCycleWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`

## Purpose

The Single Issue Cycle implements the **15-step autonomous development cycle** for one GitHub issue. It receives a pre-selected work item from the ADL Orchestrator and orchestrates the full lifecycle through plan review, task creation, TDD, code review, merge, and deployment -- dispatching specialized sub-workflows for each phase.

Every step fires a parallel **UpdateIssueStatus** sub-workflow (fire-and-forget) so the GitHub issue is kept up to date with a tech-writer LLM summary of progress.

## Flow Diagram

```
+---------------------+
| 1. Validate Work    |
|    Item             |
| (from ADL)          |
+----------+----------+
           |
           v
+---------------------+
| 2. Gather Context   |
| (context-gathering) |
| PO LLM + vector DB  |
+----------+----------+
           |
           v
+---------------------+
| 3. Generate Plan    |
| (plan-generation)   |
| Architect LLM,      |
| task DAG             |
+----------+----------+
           |
           v
+---------------------+
| 4. Review Plan      |
| (plan-review)       |
| 7-role LLM panel    |
+--+------+------+----+
   |      |      |     \
approved defer  split  needsHuman
   |      |      |       |
   |      v      v       v
   |   Create  Create  Notify
   |   Deferred Sub-    Issue
   |   Issues  Issues    |
   |      |      |       v
   |      v      v    Report
   |   Close   Close  Complete
   |   Issue   Issue  --> Finish
   |      |      |
   |      v      v
   |   Report  Report
   |   Complete Complete
   |   --> Finish --> Finish
   v
+---------------------+
| 5. Create Tasks     |
| (task-creation)     |
| Senior dev LLM,     |
| deep impl plans     |
+----------+----------+
           |
           v
+---------------------+
| 6. Review Tasks     |
| (task-review)       |
| 4-role LLM panel    |
+---+-------------+---+
    |             |
 approved    needsChanges
    |             |
    |             +---> back to step 5
    v
+---------------------+
| 7. Create Branch    |
| (branch-creation)   |
+----------+----------+
           |
           v
+---------------------+
| 8. Create Draft PR  |
| (pull-request)      |
| Plan .md files      |
| committed first     |
+----------+----------+
           |
           v
+---------------------+
| 9. Create Test Cases|
| (test-case-creation)|
| From task plans      |
+----------+----------+
           |
           v
+---------------------+
| 10. TDD Loop        |
| For each task in     |
| dependency order:    |
|  red -> green -> CI  |
|  -> refactor -> commit|
+----------+----------+
           |
           v
+---------------------+      +---------------------+
| 11. Dispatch Code   |----->| Wait for PR         |
|     Review           |      | Approval (bookmark) |
| (fire & forget)      |      +----------+----------+
+---------------------+                  |
                                          v
+---------------------+      +---------------------+
| 12. Dispatch Merge  |----->| Wait for PR         |
| (fire & forget)      |      | Merged (bookmark)   |
+---------------------+      +----------+----------+
                                          |
                                          v
                              +---------------------+
                              | 13. Update & Close  |
                              |     Issue            |
                              +----------+----------+
                                          |
                                          v
                              +---------------------+
                              | 14. Deployment       |
                              |     Pipeline          |
                              | (deployment-pipeline) |
                              | QA -> UAT -> Prod     |
                              +----------+----------+
                                          |
                                          v
                              +---------------------+
                              | 15. Report Complete  |
                              |     --> Finish        |
                              +---------------------+
```

## Steps

| Step | Activity / Sub-Workflow | Description |
|------|------------------------|-------------|
| 1 | `ValidateWorkItemActivity` | Validates the work item received from ADL (does not self-select issues) |
| 2 | `context-gathering` (sub-workflow, wait) | PO LLM + vector DB gather codebase context, story metadata, patterns |
| 3 | `plan-generation` (sub-workflow, wait) | Architect LLM generates implementation plan as a task DAG |
| 4 | `plan-review` (sub-workflow, wait) | 7-role LLM panel (architect, dev, QA, security, devops, PO, orchestrator) reviews plan through iterative discussion rounds |
| 5 | `task-creation` (sub-workflow, wait) | Senior dev LLM breaks plan into deep implementation plans per task |
| 6 | `task-review` (sub-workflow, wait) | 4-role LLM panel (architect, senior dev, dev, QA) reviews tasks |
| 7 | `branch-creation` | Creates a feature branch for the issue |
| 8 | `pull-request` | Creates a **draft** PR with implementation plan `.md` files committed (before any code) |
| 9 | `test-case-creation` (sub-workflow, wait) | Generates test cases from task plans so the red phase has specs ready |
| 10 | TDD loop | For each task in dependency order: red (test first) -> green (implement) -> CI -> refactor -> commit |
| 11 | CI gate | `ci-with-debug-retry` runs once the TDD loop is done. **Only a CI pass proceeds** — a failure fails the cycle loudly and can never reach the merge gate |
| 11a | **Mark PR ready for review** | The PR was opened as a **draft** at step 8, and GitHub refuses to merge a draft. This step flips it to ready-for-review before anyone is asked to approve a merge. A failure here fails the cycle and **never** opens the merge gate — see [The draft step](#the-draft-step-why-it-exists) |
| 12 | Merge-approval gate + bookmark | Dispatches `merge-approval` and blocks on the human decision. The gate returns `merge` / `reject` / `escalated`; **only `merge`** proceeds to wait for the real merge webhook. `reject` and `escalated` finish at a human-handoff terminal — routing them to the merge wait would hang the cycle forever, since no merge webhook would ever fire |
| 13 | Update & close issue | Updates the GitHub issue with final summary and closes it |
| 14 | `deployment-pipeline` (sub-workflow, wait) | QA -> UAT -> Prod deployment pipeline after merge |
| 15 | `ReportCycleResultActivity` | Reports result to orchestrator via engine callback API, then finishes |

## Key Design Changes (from v1)

1. **Receives work item from ADL** -- the cycle no longer selects its own issue; it receives a pre-validated work item
2. **Plan Review with 7-role LLM panel** -- iterative discussion rounds between architect, dev, QA, security, devops, PO, and orchestrator roles
3. **Create Tasks step** -- a senior dev LLM breaks the plan into deep implementation plans per task
4. **Task Review with 4-role panel** -- architect, senior dev, dev, and QA review tasks before implementation begins
5. **Draft PR created before code** -- plan `.md` files are committed first so reviewers can see the approach, and the PR is marked ready once CI passes (see below)
6. **Test cases created before TDD** -- the red phase has specs ready from the start
7. **TDD loop per task** -- each task is independently tested and CI-verified (no separate CI step)
8. **CI inside TDD** -- CI runs after each task's green phase, not as a separate workflow step
9. **Code review is fire-and-forget** -- dispatched asynchronously, blocks on PR approval bookmark
10. **Merge is fire-and-forget** -- dispatched asynchronously, blocks on PR merged bookmark
11. **Deployment pipeline** -- QA -> UAT -> Prod stages after merge
12. **Issue updated at every step** -- tech writer LLM summarizes progress via `UpdateIssueStatus` sub-workflow (fire-and-forget)
13. **Every exit reports to orchestrator** -- via engine callback API through `ReportCycleResultActivity`

## The draft step: why it exists

The cycle opens its pull request as a **draft** at step 8, deliberately — the plan `.md` files are committed before any code, and presenting that as ready-for-review would be misleading.

GitHub, however, **refuses to merge a draft pull request**. So the draft has to be undone before anyone is asked to approve a merge. Step 11a is where that happens.

Until this step existed, the cycle would build the change, pass CI, ask a human to approve the merge — and then attempt a merge that could not succeed. The failure landed at the very last moment, after all the expensive work and after pulling a person in.

Two properties of the step are worth knowing:

- **It sits behind the CI gate.** Only a CI pass reaches it, so a red build never produces a ready-for-review PR.
- **A failure never opens the merge gate.** If the PR cannot be marked ready, the cycle fails loudly instead. Asking a human to approve a merge that cannot complete is precisely the failure this removes — so an error here routes to the shared fail-the-cycle sink, not onward.

The underlying capability is the governed `set-draft` verb described in [Multi-Git-Platform](/multi-git-platform/); like every other PR operation it is catalogued and gated (level 35).

## Plan Review Decisions

The 7-role LLM panel can reach four outcomes:

| Decision | Action |
|----------|--------|
| **approved** | Proceed to task creation (step 5) |
| **defer** | Create deferred issues, close current issue, report complete, finish |
| **split** | Create sub-issues, close current issue, report complete, finish |
| **needsHuman** | Notify issue for human attention, report complete, finish |

## Task Review Decisions

The 4-role LLM panel can reach two outcomes:

| Decision | Action |
|----------|--------|
| **approved** | Proceed to branch creation (step 7) |
| **needsChanges** | Loop back to task creation (step 5) with feedback |

## Exit Reasons

All exit paths converge to `ReportCycleResultActivity` which reports back to the ADL Orchestrator:

| Exit Reason | Trigger |
|-------------|---------|
| `success` | Deployment pipeline completed successfully |
| `deferred` | Plan review decided to defer the issue |
| `split` | Plan review decided to split into sub-issues |
| `needsHuman` | Plan review determined human intervention needed |
| `error` | Branch creation, PR creation, or other unrecoverable failure |
| `tddFailed` | TDD loop failed after retries |
| `reviewRejected` | PR review rejected the changes |
| `mergeFailed` | Merge operation failed |
| `deployFailed` | Deployment pipeline failed |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `exitReason` | string | Why the cycle ended (see table above) |
| `finishReason` | string | Same as exitReason (explicit name for analytics) |
| `success` | bool | `true` only if exitReason is `"success"` |
| `issueNumber` | int | GitHub issue number processed |
| `prNumber` | int | PR number created |
| `mergeSha` | string | Merge commit SHA (on success) |

## New Activities

| Activity | Event Type | Description |
|----------|-----------|-------------|
| `ValidateWorkItemActivity` | `CYCLE.WORKITEM.VALIDATE` | Validates incoming work item from ADL |
| `ReportCycleResultActivity` | `CYCLE.RESULT.REPORT` | Reports cycle outcome to orchestrator via engine callback API |
| `WaitForPRApprovalActivity` | `CYCLE.PR.APPROVAL.WAIT` | Bookmark that blocks until PR is approved |
| `WaitForPRMergedActivity` | `CYCLE.PR.MERGE.WAIT` | Bookmark that blocks until PR is merged |
| `UpdateIssueStatusActivity` | `CYCLE.ISSUE.UPDATE` | Updates GitHub issue with tech-writer LLM summary (used by `update-issue-status` sub-workflow) |

---

## Sub-Workflow Details

### Plan Review

**Definition ID:** `plan-review`
**Class:** `PlanReviewWorkflow`

Orchestrates a 7-role LLM review panel that evaluates the generated plan through iterative discussion rounds:

- **Roles:** Architect, Developer, QA, Security, DevOps, Product Owner, Orchestrator
- **Process:** Each role provides feedback; the panel iterates until consensus is reached
- **Outcomes:** `approved`, `defer`, `split`, `needsHuman`

### Task Creation

**Definition ID:** `task-creation`
**Class:** `TaskCreationWorkflow`

A senior dev LLM breaks the approved plan into granular tasks with deep implementation plans:

1. Receives the approved plan and codebase context
2. Generates a task DAG with dependency ordering
3. Each task includes detailed implementation instructions, file paths, and expected changes
4. Outputs: task list with implementation plans

### Task Review

**Definition ID:** `task-review`
**Class:** `TaskReviewWorkflow`

A 4-role LLM panel reviews the generated tasks:

- **Roles:** Architect, Senior Developer, Developer, QA
- **Outcomes:** `approved` (proceed) or `needsChanges` (loop back to task creation with feedback)

### Test Case Creation

**Definition ID:** `test-case-creation`
**Class:** `TestCaseCreationWorkflow`

Generates test cases from task implementation plans so the TDD red phase has specs ready:

1. Receives task plans and codebase context
2. Generates test cases for each task
3. Outputs: test specifications ready for the TDD loop

### Deployment Pipeline

**Definition ID:** `deployment-pipeline`
**Class:** `DeploymentPipelineWorkflow`

Manages the post-merge deployment stages:

1. **QA Environment** -- Deploy and run integration/E2E tests
2. **UAT Environment** -- Deploy for user acceptance testing
3. **Production** -- Deploy to production with monitoring

### Update Issue Status

**Definition ID:** `update-issue-status`
**Class:** `UpdateIssueStatusWorkflow`

Fired as fire-and-forget at every step of the cycle:

1. Receives current step name and context
2. Tech-writer LLM generates a human-readable summary of progress
3. `UpdateIssueStatusActivity` posts the summary as a GitHub issue comment

### Issue Triage

**Definition ID:** `issue-triage`
**Class:** `IssueTriageWorkflow`

LLM-based classification and labeling for untriaged issues:

1. Analyzes issue title, body, and labels
2. Assigns priority, complexity, and category labels
3. Routes to appropriate team or project board

### Context Gathering

**Definition ID:** `context-gathering`
**Class:** `ContextGatheringWorkflow`

Parallel context fetching from multiple sources with budget trimming. See [Context Gathering](Workflow-Context-Gathering) for details.

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

Creates a draft PR with implementation plan files:
1. `CreatePullRequestActivity` -- Creates a draft PR with plan `.md` files, issue title, and task summary
2. Outputs: `success` (bool), `prNumber` (int), `prUrl` (string)

### Merge Complete

**Definition ID:** `merge-complete`
**Class:** `MergeWorkflow`

A simple linear workflow:
1. `MergePullRequestActivity` -- Squash-merges the PR, closes the issue, deletes the branch
2. Outputs: `success` (bool), `mergeSha` (string)

---

_See also: [ADL Orchestrator](Workflow-ADL-Orchestrator) | [TDD Cycle](Workflow-TDD-Cycle) | [Testing](Workflow-Testing) | [Workflows Index](Workflows)_
