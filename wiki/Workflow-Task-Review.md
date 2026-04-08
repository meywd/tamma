---
title: "Workflow: Task Review"
---

**Definition ID:** `task-review`
**Class:** `TaskReviewWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskReviewWorkflow.cs`

## Purpose

The Task Review workflow runs a 4-role LLM panel to review implementation tasks before execution. Each role assesses the tasks from their perspective and provides a verdict (approve or concerns). All 4 roles must approve for the overall decision to be "approved"; otherwise the decision is "needsChanges" with consolidated review notes.

## Flow Diagram

```
+------------------+
|   Initialize     |
| (read inputs)    |
+--------+---------+
         |
         v
+------------------+
| Architect Review |
| (llm-call:       |
|  architect,      |
|  task-review)    |
+--------+---------+
         |
         v
+------------------+
| Extract Architect|
| Review           |
+--------+---------+
         |
         v
+------------------+
| Sr Dev Review    |
| (llm-call:       |
|  senior_developer|
|  task-review)    |
+--------+---------+
         |
         v
+------------------+
| Extract Sr Dev   |
| Review           |
+--------+---------+
         |
         v
+------------------+
| Developer Review |
| (llm-call:       |
|  developer,      |
|  task-review)    |
+--------+---------+
         |
         v
+------------------+
| Extract Developer|
| Review           |
+--------+---------+
         |
         v
+------------------+
| Tester Review    |
| (llm-call:       |
|  tester,         |
|  task-review)    |
+--------+---------+
         |
         v
+------------------+
| Extract Tester   |
| Review           |
+--------+---------+
         |
         v
+------------------+
| Aggregate        |
| Verdicts         |
+--------+---------+
         |
         v
    +-----------+
    | All       |
    | Approved? |
    +---+---+---+
   Yes  |   |  No
        |   |
        v   +------+
  +---------+      |
  | Set     |      v
  | Approved|  +---+--------+
  +----+----+  | Set Needs  |
       |       | Changes    |
       v       +-----+------+
  +---------+        |
  | Set     |<-------+
  | Outputs |
  +----+----+
       |
       v
  +---------+
  | Finish  |
  +---------+
```

## Review Roles

| Role | Focus |
|------|-------|
| Architect | Architecture alignment, design patterns, scalability |
| Senior Developer | Implementation quality, code structure, best practices |
| Developer | Feasibility, clarity of task descriptions, dependencies |
| Tester | Test coverage, testability, edge cases |

Each role receives the `tasksJson`, `planJson`, and any `previousReviews` (accumulated review JSON from prior roles in the same run).

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `Repository` | string | Repository identifier |
| `IssueNumber` | int | Issue number |
| `TasksJson` | string | Implementation tasks JSON |
| `PlanJson` | string | Original plan JSON |
| `ArchitectReview` | string | Architect review result JSON |
| `SeniorDevReview` | string | Senior dev review result JSON |
| `DeveloperReview` | string | Developer review result JSON |
| `TesterReview` | string | Tester review result JSON |
| `AllReviewsJson` | string | Aggregated reviews JSON array |
| `AllApproved` | bool | Whether all 4 reviewers approved |
| `Decision` | string | Final decision (approved/needsChanges/needsHuman) |
| `ReviewNotes` | string | Consolidated review comments |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier |
| `issueNumber` | int | Issue number |
| `tasksJson` | string | Implementation tasks JSON |
| `planJson` | string | Original plan JSON |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `decision` | string | Review decision: `approved` or `needsChanges` |
| `tasksJson` | string | Tasks JSON (unchanged) |
| `reviewNotes` | string | Consolidated review notes from non-approving roles |

## Review Extraction

Each role's LLM response is parsed as JSON. Expected response format:

```json
{
  "verdict": "approve" | "concerns",
  "comments": "...",
  "suggestedChanges": "..."
}
```

If the response is not valid JSON, it is wrapped as a `concerns` verdict with the raw text as comments.

## Aggregation Logic

- If all 4 roles have `verdict: "approve"`, the decision is `approved`
- Otherwise, the decision is `needsChanges` with review notes from all non-approving roles

---

_See also: [Task Creation](/workflows/task-creation) | [Plan Review](/workflows/plan-review) | [Single Issue Cycle](/workflows/single-issue-cycle) | [Workflows Index](/workflows)_
