---
title: "Workflow: Task Review"
---

**Definition ID:** `task-review`
**Class:** `TaskReviewWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskReviewWorkflow.cs`

## Purpose

**Status:** Stub -- structure defined, implementation pending.

The Task Review workflow will run a 4-role panel (Architect, Senior Developer, Developer, QA) to review implementation tasks before execution. Each role assesses the tasks from their perspective and provides a verdict.

## Flow Diagram

```
+------------------+
| Stub: Task       |
| Review -- TODO   |
+------------------+
```

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
| `decision` | string | Review decision: approved, needsChanges, or needsHuman (not yet implemented) |
| `tasksJson` | string | Potentially modified tasks JSON (not yet implemented) |

---

_See also: [Task Creation](/workflows/task-creation) | [Plan Review](/workflows/plan-review) | [Single Issue Cycle](/workflows/single-issue-cycle) | [Workflows Index](/workflows)_
