---
title: "Workflow: Task Creation"
---

**Definition ID:** `task-creation`
**Class:** `TaskCreationWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs`

> **Epic 39 (Story 39-15) — now a `document-lifecycle` binding (produces a task-breakdown `Plan`, consumes `Plan`).** This workflow is a thin binding over the generic [Document Lifecycle](Document-Lifecycle) (`produce → validate → review → revise → accept`). On a fresh run it fetches the latest accepted system `Plan` for the issue and dispatches `document-lifecycle` with `documentType = plan` (39-4 maps `create-tasks` → `plan`) and the `(senior_developer, create-tasks)` producer cell. The old bespoke `Extract & Validate` → `Can Retry?` retry loop and error terminal are **deleted**; validation, review-with-notes, bounded revision, and typed escalation with full lineage are owned by the lifecycle. The Flow Diagram, "Validation Rules", and "Retry Behavior" sections below describe the retired bespoke flow, kept for historical reference.

## Purpose

The Task Creation workflow uses a senior developer LLM to break the approved implementation plan into detailed implementation tasks. Each task includes files to modify, code changes, test approach, and dependencies forming a DAG (directed acyclic graph).

The workflow dispatches `llm-call` with `role=senior_developer` and `action=create-tasks`, then validates the response contains a non-empty `tasks` JSON array. If validation fails, it retries up to 2 times, feeding validation errors back into the prompt.

## Flow Diagram

```
+------------------+
|   Initialize     |
| (read inputs)    |
+--------+---------+
         |
         v
+------------------+
| Generate Tasks   |
| (llm-call:       |
|  senior_developer|
|  create-tasks)   |
+--------+---------+
         |
         v
+------------------+
| Extract &        |
| Validate         |
| (parse JSON,     |
|  check tasks[])  |
+--------+---------+
         |
         v
    +----------+
    | Tasks    |
    | Valid?   |
    +---+--+---+
   Yes  |  |  No
        |  |
        v  +------+
  +---------+     |
  | Output  |     v
  | Tasks   |  +--+------------+
  +----+----+  | Increment     |
       |       | Retry         |
       v       +-------+-------+
  +--------+           |
  | Finish |           v
  +--------+     +----------+
       ^         | Can      |
       |         | Retry?   |
       |         +---+--+---+
       |        Yes  |  |  No
       |             |  |
       |             v  +------+
       |    (loop to           |
       |     Generate Tasks)   v
       |              +--+----------+
       |              | Error       |
       |              | Outputs     |
       +--------------+-------------+
```

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `Repository` | string | Repository identifier |
| `IssueNumber` | int | Issue number |
| `PlanJson` | string | Implementation plan JSON |
| `ContextIds` | string | Context IDs JSON array |
| `WorkItemJson` | string | Work item JSON |
| `TasksJson` | string | Extracted tasks JSON array |
| `TasksValid` | bool | Whether extracted tasks passed validation |
| `ValidationErrors` | string | Validation error messages |
| `RetryCount` | int | Current retry count (max 2) |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier |
| `issueNumber` | int | Issue number |
| `planJson` | string | Implementation plan JSON |
| `contextIds` | string | Context IDs JSON array |
| `workItemJson` | string | Work item JSON |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `tasksJson` | string | JSON array of detailed task plans (or `[]` on failure) |
| `error` | string | Validation errors (only on failure) |

## Validation Rules

- Response must contain valid JSON (array or object with `tasks` property)
- The `tasks` array must be non-empty
- If the response is an object with a `tasks` property, the array is extracted and normalized
- Invalid JSON, empty arrays, or missing `tasks` properties trigger a retry

## Retry Behavior

- Maximum 2 retries (3 total attempts)
- Validation errors are fed back to the LLM in the next attempt via the `validationErrors` variable
- If all retries are exhausted, outputs `tasksJson = "[]"` and the error message

---

_See also: [Task Review](/workflows/task-review) | [Plan Generation](/workflows/plan-generation) | [Single Issue Cycle](/workflows/single-issue-cycle) | [Workflows Index](/workflows)_
