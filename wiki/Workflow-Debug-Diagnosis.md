---
title: "Workflow: Debug Diagnosis"
---

**Definition ID:** `debug-diagnosis`
**Class:** `DebugDiagnosisWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DebugDiagnosisWorkflow.cs`

## Purpose

Produce a typed root-cause `Diagnosis` through the generic document lifecycle (Story
39-15). This workflow is a **thin binding** over
[Workflow: Document Lifecycle](Workflow-Document-Lifecycle) — it dispatches
`document-lifecycle` with the diagnosis producer cell and consumes the typed exit. It
replaces the retired `AIDiagnosisActivity`'s hand-built prompt + direct mediated call:
production now runs on the registry cell `(senior_developer, debug-rootcause)`, restoring
the `llm-call`-mediation invariant, and the output is validated + reviewed + accepted like
every other work document.

The accepted diagnosis is consumed by `DebuggingWorkflow`'s **unchanged** fix/retry loop —
the binding surfaces it both as a typed store id (`diagnosisDocumentId`) and, via
`DiagnosisBindingHelper.ToLegacyHypothesesJson`, as the bare `hypothesesJson` the loop's
`SelectHypothesisActivity` already slices, so that loop is byte-stable.

## Flow Diagram

```
+---------------------+
| Read Inputs         |   (debug context: error/code/git/test/repro/previous)
+----------+----------+
           |
           v
+---------------------+
| Compute Re-Entry    |   (39-10 latest-state re-entry for documentType=diagnosis)
| Position            |
+----------+----------+
           |
           v
+---------------------+
| Dispatch            |   WorkflowDefinitionId = "document-lifecycle"
| Document Lifecycle  |   producerRole=senior_developer, action=debug-rootcause
| (WaitForCompletion) |   (accept-gate suspend happens INSIDE the child lifecycle)
+----------+----------+
           |
           v
+---------------------+
| Read Lifecycle Exit |   accepted? -> legacy hypotheses ; else failure reason
| (fail-closed)       |
+----------+----------+
           |
           v
+---------------------+
| Expose Output       |
+---------------------+
```

The debug context is folded into the `debug-rootcause` cell's **declared** variables
(`errorContext`, `stackTrace`, `relevantCode`, `recentChanges`, `conventions`) — an
undeclared key is silently dropped at render, so the binding folds test/repro context into
`stackTrace` and git-history/previous-attempts (+ any superseded-diagnosis pointer) into
`recentChanges`. Repair/revise notes land in the declared `errorContext` carrier.

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `sessionId` | string | Debug session id |
| `issueId` | string | Explicit issue anchor; else derived as `debug#{session}` / `debug#{story}` |
| `mode` | string | Debug mode (default `RuntimeError`) |
| `errorContext` | string | Error output / stack |
| `codeContext` | string | Relevant source |
| `gitContext` | string | Recent commits |
| `testContext` | string | Test results |
| `reproductionContext` | string | Reproduction steps |
| `previousContext` | string | Prior failed attempts (do NOT repeat) |
| `supersedesDocumentId` | string | Prior diagnosis this run supersedes |
| `tenantId` | string | Tenant scope |
| `acceptanceRulesJson` | string | Resolved acceptance rules for the diagnosis type |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `hypothesesJson` | string | Legacy ranked-hypotheses JSON the fix/retry loop slices (`[]` if not accepted) |
| `accepted` | bool | Accepted AND has usable hypotheses |
| `status` | string | `completed` when accepted, else the lifecycle status |
| `outcome` | string | The typed escalation outcome, if escalated |
| `diagnosisDocumentId` | string | The accepted `Diagnosis` envelope id |
| `failureReason` | string | Built from the lifecycle exit when not accepted |

## Produced document

`Diagnosis` (`apps/tamma-elsa/src/Tamma.Core/Documents/Types/Diagnosis.cs`): an analysis
summary plus ranked hypotheses (`confidence ∈ [0,1]`, rank 1 highest); a non-empty
suggested fix must name the files it touches. The canonical wire is camelCase; the legacy
snake_case shape lives only in the paired `FromLegacyJson`/`ToLegacyJson` bridge.

## Events Emitted

The `DOCUMENT.*` transition events are emitted by the dispatched `document-lifecycle` child
(see [Workflow: Document Lifecycle](Workflow-Document-Lifecycle)), tagged
`documentType=diagnosis`. This binding itself only reads the child's typed result.

## Resume behavior

`[ResumeBehavior(ResumeMode.LatestStateReEntry)]` with a `ComputeReEntryPositionActivity`
gate — a fresh run re-enters from the latest accepted diagnosis rather than re-diagnosing.
The accept-gate bookmark suspend lives inside the dispatched child lifecycle, which the
parent awaits via `WaitForCompletion`. See [Resumable Workflows](Resumable-Workflows).

---

_See also: [Debugging](Workflow-Debugging) | [Blocker Diagnosis](Workflow-Blocker-Diagnosis) | [Document Lifecycle](Document-Lifecycle) | [Workflows Index](Workflows)_
