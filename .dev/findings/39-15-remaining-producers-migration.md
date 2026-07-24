# Story 39-15 — Remaining Producers Migration (partial: creation family + cross-doc seam)

Status: **partial** — the cross-document validation seam and the two creation-family
producers (TaskCreation, TestCaseCreation) are migrated and green; the Debug-diagnosis
seam and the entire Triage family are **not yet done** (see "Deferred" below).

## Landed this pass (Steps 2–5 + gate/test slices)

- **Cross-document validation seam (D3).** `IDocumentType` gained an additive default
  interface member `ValidateWithContext(JsonElement payload, string validationContextJson)
  => Validate(payload)` — every existing type is source-compatible.
  `TestSpecDocumentType` overrides it (as an ordinary implicit-interface method, NOT
  `override` — a DIM is not virtual on the class) to emit `CASE_UNKNOWN_TASK_ID` for any
  case whose `taskId` is absent from the consumed plan. `DocumentLifecycleWorkflow` reads an
  optional `validationContextJson` input (default `""`) and, when non-empty, VALIDATE calls
  `ValidateWithContext` instead of `Validate`. Additive + fail-closed (unreadable context →
  payload-only validation, never a throw).
- **TaskCreation → binding** producing documentType `plan`, cell `(senior_developer,
  create-tasks)`, `feedbackVariableName="contextFindings"`, `tasksJson` = the accepted
  Plan's `tasks` array raw text (`"[]"` on non-accept, so the frozen SingleIssueCycle
  tasks-gate + empty-tasks edge fire unchanged). All `ValidationErrors`/`maxRetries`/inline
  extract/`OutErr`/`Finish` plumbing deleted.
- **TestCaseCreation → binding** producing `test-spec`, consuming `[plan]`, cell `(tester,
  write-tests)`, `feedbackVariableName="testTarget"`, `validationContextJson` = the consumed
  task breakdown (task-ID ring).
- Both declare `[ResumeBehavior(LatestStateReEntry)]` + `ComputeReEntryPositionActivity`.
  Removed from `LegacyResumeAllowlist`. `ContractBindingTests` parser authorities retargeted
  to `PlanDocumentType.Validate` / `TestSpecDocumentType.Validate` (token groups unchanged).
  `DocumentTypeRegistry` interface edges for `task-creation` (consumes `[plan]`/produces
  `plan`) and `test-case-creation` (consumes `[plan]`/produces `test-spec`) flipped
  non-provisional; `WorkflowInterfaceGraphTests` reconciled list updated.

## Decision — two-plans-per-issue disambiguation (D2)

Both `plan-generation` (system plan) and `task-creation` (task breakdown) produce
documentType `plan` for the same issue. The 39-11 latest-accepted / re-entry read
(`LifecycleReEntryService.ReconstructAsync` → `GetLatestAcceptedAsync`) scopes ONLY by
`(issueId, documentType)` — it has **NO producer filter**. A task-creation lifecycle keyed on
the bare issue id would `ComputeReEntryPosition("plan", issueId)` onto the accepted SYSTEM
plan and short-circuit to `Complete` on EVERY run, never producing the task breakdown.

**Resolution (no type fork, per D2 discipline):** the task-creation lifecycle is keyed on a
producer-scoped issue id `{issueId}#task-creation` (`CreationBindingHelper.ScopeIssueId`),
isolating its accepted-doc + event slice from the system plan. The `planJson` input remains
the runtime carrier for the consumed content. TestSpec/Diagnosis/TriageDecision use unique
type keys and need no scoping.

**FILED GAP → 39-11:** add a producer filter (`ProducedBy.WorkflowDefinitionId`) to the
latest-accepted / re-entry read so same-type-different-producer documents disambiguate
natively; the `#producer` issue-id suffix is the interim workaround. Trade-off: the
task-plan's `DOCUMENT.*` events carry the scoped issue id, so AC6 replay for task-creation
matches the scoped id (documented), not the bare issue id.

**Lineage anchor note:** the lifecycle exposes no `parentDocumentId` input (PlanGeneration,
the 39-14 template, also folds the consumed doc into the DECLARED carrier rather than
threading a parent id). So consumed-parent lineage is folded into `contextFindings` and the
consumed system-plan id is surfaced as an additive `parentDocumentId` OUTPUT only.

## Deferred (NOT done — remaining 39-15 scope)

- **Step 6 — Debug diagnosis seam (D4):** `DebugDiagnosisWorkflow` + `DiagnosisBindingHelper`,
  rewiring `DebuggingWorkflow`'s tightly-coupled `aiDiagnosis`→`serializeDiagnosis`→
  `diagnosisProduced` region, deleting `AIDiagnosisActivity`, and the Tdd/Ci escalation reads.
  Deferred: the debug loop is deeply integrated (selectHypothesis reads a bare `Hypothesis[]`
  from `hypothesesJson`, so the helper must project `Diagnosis.Hypotheses → Hypothesis[]`, NOT
  literally `Diagnosis.ToLegacyJson`) and rewiring risks the existing DebuggingWorkflowTests /
  TddWithDebugRetryWorkflowTests. Also needs `debug-rootcause.md` rewritten to the Diagnosis
  contract (analysisSummary/hypotheses/rank/description/confidence/suggestedFix/affectedFiles).
- **Steps 7–9 — Triage family (D5/D6/D9):** the `triage-context-scan` split (new AgentAction +
  RolePhaseMap cell + prompt file + count-pin bumps), TriageContextGathering→Findings binding,
  TriagePODecision→TriageDecision binding, **deletion of `TriagePanelReviewWorkflow` +
  `TriagePanelAggregationHelper`** with reconciliation into 39-7 config, TriageItemCycle rewire.
  **Non-mechanical blocker flagged by the plan:** whether 39-7's `ReviewerSelection`/
  `ReviewerSpec` can express per-role triage review actions (`GetTriageActionForRole`) — if not,
  39-7 must be EXTENDED (lockstep), which is why this family was not attempted in the same pass
  as the mechanical creation clones.
- **Step 10 (triage parts) / Steps 11–12:** the triage drift-gate moves, the universal-pin
  test, and `RemainingProducersLifecycleExecutionTests` (`[Explicit]`/CI-Postgres).

## Panel-semantics behavior change (recorded per plan Risks)

When the triage family lands, the 4-role panel changes from an INPUT-side panel (feeding the
PO raw assessments) to a REVIEW-of-draft stage inside the TriageDecision lifecycle — a real
behavioral change (the panel now critiques a draft decision). Not yet implemented; recorded
here ahead of that work.
