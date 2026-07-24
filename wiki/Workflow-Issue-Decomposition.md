---
title: "Workflow: Issue Decomposition"
---

**Definition ID:** `issue-decomposition`
**Class:** `IssueDecompositionWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/IssueDecompositionWorkflow.cs`

> **Epic 39 (Story 39-12) — now a `document-lifecycle` binding (produces `Decomposition`).** This workflow is a thin binding over the generic [Document Lifecycle](Document-Lifecycle) (`produce → validate → review → revise → accept`). It assembles the issue context and dispatches `document-lifecycle` with `documentType = decomposition` and the `(senior_developer, decompose-issue)` producer cell, then exposes typed outcomes. The old bespoke pipeline — `llm-call` → hand parser (`DecompositionParsing`) → success-flag gate → error-`Finish` terminal — is **deleted**; the lifecycle's generic rings own all validation, review-with-notes, bounded revision, and typed escalation (`validation-exhausted` / `rounds-exhausted` / `review-undecidable`) with full lineage instead of a dead terminal. The `DECOMPOSITION.*` events still emit, now **alongside** the generic `DOCUMENT.*` events. The Flow Diagram and "Fail-Closed Parsing" section below describe the retired bespoke flow, kept for historical reference.

## Purpose

The Issue Decomposition workflow (Story 2.14) breaks a complex issue/requirement into an ORDERED set of smaller, implementable sub-tasks — each with a stable id, title, description, acceptance criteria, an effort estimate (hours), a complexity bucket, and declared `dependsOn` prerequisite edges. It gathers codebase/prior-art context by reusing the `context-gathering` sub-workflow, then decomposes via the MEDIATED `llm-call` path (role=`senior_developer`, action=`decompose-issue`) — the engine holds no LLM credential. Every transition is emitted as a `DECOMPOSITION.*` DCB event.

The sub-task output shape (`IssueDecomposition` — ordered sub-tasks with ids + `dependsOn` edges) is the input contract for Story 2.15 (dependency mapping) and Story 2.16 (sequencing).

Decomposition is AUTONOMOUS — there is no in-workflow human gate/bookmark. Human approval before executing decomposed tasks is a downstream orchestration concern: a parent flow presents the emitted sub-task set for approval before dispatching implementation.

## Flow Diagram

```
+------------------+
| Read Inputs      |
| (issueId, title, |
|  repo, tenant)   |
+--------+---------+
         |
         v
+------------------+
| Emit             |
| DECOMPOSITION.   |
| STARTED          |
+--------+---------+
         |
         v
+------------------+
| Gather Context   |
| (context-        |
|  gathering)      |
+--------+---------+
         |
         v
+------------------+
| Store Context    |
| Result           |
+--------+---------+
         |
         v
+------------------+
| Emit             |
| DECOMPOSITION.   |
| CONTEXT_GATHERED |
+--------+---------+
         |
         v
+------------------+
| Decompose Issue  |
| (llm-call:       |
|  senior_developer|
|  /decompose-     |
|  issue)          |
+--------+---------+
         |
         v
+------------------+
| Parse            |
| Decomposition    |
| (fail-closed)    |
+--------+---------+
         |
         v
+------------------+
| Decomposition    |
| LLM OK?          |
+--+------------+--+
  YES            NO
   |              |
   v              v
+----------+ +------------------+
| Emit     | | Emit             |
| DECOMPO- | | DECOMPOSITION.   |
| SITION.  | | FAILED (LOUD)    |
| COMPLETED| +--------+---------+
+----+-----+          |
     |                v
     v         +------------------+
+----------+   | Decomposition    |
| Set      |   | Error (Finish)   |
| Output   |   +------------------+
| Result   |
+----+-----+
     |
     v
+------------------+
| Expose Output    |
| (decomposition,  |
|  subtaskCount)   |
+------------------+
```

## Sub-Workflows Dispatched

| Workflow | Wait? | Purpose |
|----------|-------|---------|
| `context-gathering` | Yes | Multi-role codebase/prior-art scan; scope/dependency signal informs the complexity assessment |
| `llm-call` | Yes | Decomposition — role=`senior_developer`, action=`decompose-issue`, tools disabled |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `sessionId` | Guid | Session identifier (a new one is minted if empty) |
| `issueId` | string | Issue identifier |
| `issueTitle` | string | Issue title (wrapped into a minimal work-item JSON when no `workItemJson` is supplied) |
| `repository` | string | Repository identifier |
| `issueNumber` | int | Issue number |
| `workItemJson` | string | Work item JSON (preferred over `issueTitle` when present) |
| `tenantId` | string | Tenant id (GUID string, or empty in single-user mode) |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `sessionId` | string | Session identifier |
| `status` | string | `completed` on success |
| `decomposition` | string | The serialized `IssueDecomposition` JSON (`{}` on failure) |
| `subtaskCount` | int | Number of usable sub-tasks recovered |
| `contextIds` | string | Context IDs JSON array from context gathering |

## Events Emitted

| Event | Status | When |
|-------|--------|------|
| `DECOMPOSITION.STARTED` | success | Decomposition begins |
| `DECOMPOSITION.CONTEXT_GATHERED` | success | Context-gathering sub-workflow returned |
| `DECOMPOSITION.COMPLETED` | success | A valid decomposition was parsed (carries the sub-task count) |
| `DECOMPOSITION.FAILED` | error (LOUD) | The `llm-call` failed or output was empty/unparseable — never a false success |

## Fail-Closed Parsing

`DecompositionParsing.ParseDecomposition` returns `null` (routing to the error terminal — no fabricated breakdown) on:

1. Empty/unparseable LLM output
2. Missing/empty `summary` (the rationale is load-bearing — it records how the breakdown preserves the parent issue's intent)
3. Zero usable sub-tasks

It also cleans the sub-task set: shell (empty) sub-tasks and duplicate-id sub-tasks are dropped, `dependsOn` references are pruned to ids that exist in the same decomposition, and self-references are removed. Complexity labels are normalized onto the closed `low`/`medium`/`high` set (unknown labels fold to `medium`).

## Sub-Task Shape

```json
{
  "summary": "How the breakdown preserves the parent intent",
  "subtasks": [
    {
      "id": "ST-1",
      "title": "...",
      "description": "...",
      "acceptanceCriteria": "...",
      "estimateHours": 4,
      "complexity": "medium",
      "dependsOn": []
    }
  ]
}
```

Sub-tasks live in DCB events + workflow state only — there is no dedicated table.

---

_See also: [Context Gathering](/workflows/context-gathering) | [LLM Call](/workflows/llm-call) | [Research](/workflows/research) | [Workflows Index](/workflows)_
