---
title: "Workflow: Plan Generation"
---

**Definition ID:** `plan-generation`
**Class:** `PlanGenerationWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanGenerationWorkflow.cs`

> **Epic 39 (Story 39-14) — now a `document-lifecycle` binding (produces `Plan`, consumes `Decomposition`).** This workflow is a thin binding over the generic [Document Lifecycle](Document-Lifecycle) (`produce → validate → review → revise → accept`). On a fresh run it fetches the latest accepted `Decomposition` for the issue and folds it into the producer's `contextFindings`, then dispatches `document-lifecycle` with `documentType = plan` and the `(architect, plan-system-design)` producer cell. Plan **review now runs inside the lifecycle's REVIEW stage** as the unified `Review` (39-7 panel) — the old inline `Extract & Validate` → `Can Retry?` retry loop is **deleted**; validation, review-with-notes, bounded revision, and typed escalation (`validation-exhausted` / `rounds-exhausted` / `review-undecidable`) with full lineage are owned by the lifecycle. The Flow Diagram and "Validation Rules" section below describe the retired bespoke flow, kept for historical reference.

## Purpose

The Plan Generation workflow uses an architect-role LLM to produce an implementation blueprint for an issue. Prompts come from the prompt registry (role=architect, action=plan) with no inline prompts. The generated plan is validated for required fields (tasks/steps and file map), with up to 2 retries on invalid output, feeding validation errors back into the next attempt.

## Flow Diagram

```
+------------------+
| Initialize       |
| (read inputs)    |
+--------+---------+
         |
         v
+------------------+<---------+
| Generate Plan    |          |
| (llm-call:       |          |
|  architect/plan) |          |
+--------+---------+          |
         |                    |
         v                    |
+------------------+          |
| Extract &        |          |
| Validate         |          |
+--------+---------+          |
         |                    |
         v                    |
+------------------+          |
| Plan Valid?      |          |
+--+------------+--+          |
  YES            NO           |
   |              |           |
   v              v           |
+----------+ +-----------+   |
| Output   | | Increment |   |
| Plan     | | Retry     |   |
+----+-----+ +-----+-----+   |
     |              |         |
     |              v         |
     |       +-----------+   |
     |       | Can Retry? |   |
     |       +--+------+--+   |
     |         YES      NO    |
     |          |        |    |
     |          +--------+    |
     |          |   +----+    |
     |          |   |         |
     |          |   v         |
     |          | +----------+|
     |          | | Error    ||
     |          | | Outputs  ||
     |          | +----+-----+|
     |          |      |      |
     |          +------+------+
     |                 |
     v                 v
+----------------------------------+
| Complete                         |
+----------------------------------+
```

## Validation Rules

The plan JSON is validated for:
1. Must not be empty or `{}`
2. Must contain `tasks` or `steps` property
3. Must contain `fileMap`, `files`, or `filesToModify` property
4. Must be valid JSON

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier |
| `issueNumber` | int | Issue number |
| `poSummary` | string | Product owner summary |
| `contextIds` | string | Context IDs JSON array |
| `workItemJson` | string | Work item JSON |
| `reviewNotes` | string | Review notes from a previous plan review revision |
| `revisionNumber` | int | Plan revision number |

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `PlanJson` | string | Generated plan JSON |
| `PlanValid` | bool | Whether the plan passed validation |
| `ValidationErrors` | string | Validation error messages |
| `RetryCount` | int | Current retry count (max 2) |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `planJson` | string | The generated plan JSON (empty on failure) |
| `error` | string | Validation errors (on failure only) |

---

_See also: [Plan Review](/workflows/plan-review) | [LLM Call](/workflows/llm-call) | [Single Issue Cycle](/workflows/single-issue-cycle) | [Workflows Index](/workflows)_
