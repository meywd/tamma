---
title: "Workflow: Task Creation"
---

**Definition ID:** `task-creation`
**Class:** `TaskCreationWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs`

## Purpose

**Status:** Stub -- structure defined, implementation pending.

The Task Creation workflow will use a senior developer LLM to break the implementation plan into deep implementation tasks. Each task will include files to modify, code changes, test approach, and dependencies forming a DAG (directed acyclic graph).

## Flow Diagram

```
+------------------+
| Stub: Task       |
| Creation -- TODO |
+------------------+
```

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier |
| `issueNumber` | int | Issue number |
| `planJson` | string | Implementation plan JSON |
| `contextIds` | string | Context IDs JSON array |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `tasksJson` | string | Array of detailed task plans (not yet implemented) |

---

_See also: [Task Review](/workflows/task-review) | [Plan Generation](/workflows/plan-generation) | [Single Issue Cycle](/workflows/single-issue-cycle) | [Workflows Index](/workflows)_
