---
title: "Workflow: Document Lifecycle"
---

**Definition ID:** `document-lifecycle`
**Class:** `DocumentLifecycleWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DocumentLifecycleWorkflow.cs`

## Purpose

The generic **produce → validate → review → revise → accept** sub-workflow (Story 39-6).
It runs the same quality lifecycle for **any** registered document type, driven purely by
its inputs — a producer dispatch spec, a document type key, lineage anchors, and resolved
acceptance rules. Every document-producing workflow now dispatches this workflow as a thin
binding rather than hand-rolling its own parse-and-decide loop. See
[Document Lifecycle](Document-Lifecycle) for the concept, this page for the mechanics.

All decision logic lives in the pure `DocumentLifecycleHelper`
(`Workflows/Helpers/DocumentLifecycleHelper.cs`); the Elsa flowchart only routes.

## Flow Diagram

```
+------------------------------------------------------------------+
|                   DocumentLifecycle (generic)                    |
|                                                                  |
| Init --> ComputeReEntry --> [Complete? -> DOCUMENT.REENTERED]    |
|   |                          [Review?   -> jump to REVIEW]        |
|   |                          [Accept?   -> jump to ACCEPT]        |
|   v                                                              |
| PRODUCE (llm-call: producerRole/producerAction/variables)        |
|   |                                                              |
| VALIDATE (type validator, deterministic)                         |
|   |  invalid --> Can Repair? --yes--> Prepare/Dispatch Repair -->|
|   |             (bounded)      --no--> Escalate ValidationExhausted
|   v valid                                                        |
| Ambiguity Over Threshold? --yes--> Escalate AmbiguityAboveThreshold
|   | no                                                           |
| REVIEW (dispatch document-review -> Review doc)                  |
|   |  approve --> ACCEPT                                          |
|   |  revise  --> Prepare Revision -> Dispatch Revise -> VALIDATE |
|   |  rounds out --> Escalate RoundsExhausted                     |
|   |  undecidable --> Escalate ReviewUndecidable                  |
|   v                                                              |
| ACCEPT: BuildAcceptanceRequest -> [optional delivery] ->         |
|         PublishAcceptanceRequest -> WaitForDocumentDecision      |
|         (SUSPEND) -> ApplyGuardrails                             |
|            accept  --> DOCUMENT.ACCEPTED  -> Persist             |
|            reject  --> DOCUMENT.REJECTED  -> Persist             |
|            revise  --> back to REVISE                            |
|            escalate--> DOCUMENT.ESCALATED -> Persist             |
+------------------------------------------------------------------+
```

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `documentType` | string | `DocumentTypeKey` wire string (e.g. `decomposition`, `plan`, `diagnosis`) |
| `producerRole` | string | Agent role for the PRODUCE `llm-call` (fed as `agentRole`) |
| `producerAction` | string | Agent action for the PRODUCE `llm-call` |
| `producerVariablesJson` | string | JSON of the declared prompt variables for the producer cell |
| `feedbackVariableName` | string | Which declared variable carries repair/revise notes (default `feedback`) |
| `issueId` | string | Lineage anchor (required) |
| `correlationId` | string | Correlation lineage anchor |
| `tenantId` | string | Tenant scope (empty ⇒ single-user, platform scope) |
| `acceptanceRulesJson` | string | Resolved `AcceptanceRules` (autonomy dial, bounds, reviewer selection, guidance) |
| `reviewWorkflowDefinitionId` | string | Review producer (default `document-review`) |
| `validationContextJson` | string | Optional cross-document validation context (e.g. TestSpec ↔ Plan) |
| `deliveryWorkflowDefinitionId` | string | Optional sub-workflow dispatched before ACCEPT (e.g. post the doc to the issue) |
| `ambiguityScore` | number | Optional input ambiguity score checked against the escalation threshold |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `status` | string | `accepted` \| `rejected` \| `escalated` — a binding switches on this first |
| `outcome` | string | On escalate: `review-undecidable` \| `ambiguity-above-threshold` \| `rounds-exhausted` \| `validation-exhausted` |
| `documentId` | string | The accepted/terminal envelope's UUID v7 |
| `documentJson` | string | The accepted revision's payload body (for a binding's domain output) |
| `lifecycleResult` | string | The full lineage result (id + state + rounds) |
| `decisionNotes` | string | The decider's feedback/notes |
| `sessionId` | string | The decision-session id (stable across suspend/resume) |

## The accept gate

ACCEPT is an **actor, not a branch**. It always builds an `AcceptanceRequest`, publishes it
on the workflow↔orchestrator channel (`PublishAcceptanceRequestActivity`), and suspends on
`WaitForDocumentDecisionActivity`. The orchestrator reads the acceptance rules + the
autonomy dial (70–100) and routes the decision to itself or to a tenant role's Task View;
`AcceptanceGuardrails.Clamp` enforces the hard invariants (round bounds, blocking-review,
always-escalate classes) around whatever decision returns. There is no accept-decision
`llm-call` and no branch that skips the decision.

## Events Emitted

`DOCUMENT.*` on every transition (`Tamma.Activities/Documents/DocumentEvents.cs`):

| Event | When |
|-------|------|
| `DOCUMENT.PRODUCED.SUCCESS` / `.FAILED` | after PRODUCE |
| `DOCUMENT.VALIDATED.SUCCESS` / `.FAILED` | after VALIDATE |
| `DOCUMENT.REVIEW_REQUESTED` | entering REVIEW |
| `DOCUMENT.REVIEWED` | review landed |
| `DOCUMENT.REVISION_STARTED` | entering a REVISE round |
| `DOCUMENT.ACCEPTED` / `DOCUMENT.REJECTED` / `DOCUMENT.ESCALATED` | terminal |
| `DOCUMENT.REENTERED` | crash re-entry short-circuit (never a second `ACCEPTED`) |

`.FAILED`, `REJECTED`, and `ESCALATED` are LOUD (error-status) rows. The acceptance-gate
`APPROVAL.*` / `ESCALATION.*` families are emitted by 39-8's own activities. See
[Event Schema & Catalog](Event-Schema-and-Catalog).

## Document store

Each distinct draft is projected into `document_instances` exactly once (insert-only; a
revise inserts `revision + 1` and flips its predecessor to `superseded`). The persist is
fail-loud — the document is the product, not telemetry — and shares its `CorrelatingEventId`
with the adjacent `DOCUMENT.*` event so store and stream cross-check.

## Resume behavior

`[ResumeBehavior(ResumeMode.Both, SuspendActivities = { WaitForDocumentDecisionActivity })]`
— it suspends on the canonical accept-gate bookmark AND re-enters from the latest accepted
state after a crash. Never allowlisted. See [Resumable Workflows](Resumable-Workflows).

---

_See also: [Document Lifecycle](Document-Lifecycle) | [Workflow: Debug Diagnosis](Workflow-Debug-Diagnosis) | [Resumable Workflows](Resumable-Workflows) | [Workflows Index](Workflows)_
