# Story 39-15 slice 2 — Debug-diagnosis seam (Part A) landed; Triage family (Part B) deferred

Status: **Part A complete and green; Part B not implemented this pass** (see "Part B precise state").
Follows slice 1 (`39-15-remaining-producers-migration.md`, creation family + cross-doc seam).

## Part A — Debug diagnosis (Step 6, D4) — DONE, green

The `AIDiagnosisActivity` hand-built-prompt + direct mediated-call path is retired; diagnosis
production now rides a lifecycle binding, restoring the llm-call-mediation invariant.

- **NEW `Workflows/DebugDiagnosisWorkflow.cs`** — DefinitionId `debug-diagnosis`, documentType
  `diagnosis`, produce cell `(senior_developer, debug-rootcause)`. Thin binding over
  `document-lifecycle`: folds the debug context into the cell's DECLARED variables
  (`errorContext` / `stackTrace` / `relevantCode` / `recentChanges` / `conventions` — the
  render-drop lesson; `feedbackVariableName="errorContext"`), reads the typed exit fail-closed,
  outputs `hypothesesJson` (legacy bridge) + `accepted` + `diagnosisDocumentId` + `outcome` +
  `failureReason`. `[ResumeBehavior(LatestStateReEntry)]` + `ComputeReEntryPositionActivity`.
- **NEW `Workflows/Helpers/DiagnosisBindingHelper.cs`** — `ToLegacyHypothesesJson(documentJson)`
  PROJECTS `Diagnosis.Hypotheses → Debug.Models.Hypothesis[]` (Untried) — NOT literally
  `Diagnosis.ToLegacyJson` (the snake_case wire the loop never sees; the slice-1 finding's
  warning). `HasUsableHypotheses` (caller gate), `BuildFailureReason(exit)` maps
  `validation-exhausted → diagnosis-parse-failure`, else `diagnosis-call-failed`. Fail-closed,
  no fabrication.
- **`DebuggingWorkflow.cs` rewired** — `aiDiagnosis` + `serializeDiagnosis` + the
  `IsDiagnosisProduced(DiagnosisResult)` gate replaced by `dispatchDiagnosis`
  (`DispatchWorkflow("debug-diagnosis")`) + `readDiagnosisExit` + a typed produced-flag gate. On
  accept, `hypothesesJson` is populated via the bridge so `SelectHypothesisActivity` /
  `RefineHypothesisActivity` / `CompileDebugReportActivity` and the fix/test/refine loop are
  UNTOUCHED. `DEBUG.DIAGNOSIS.SUCCESS/FAILED` still emit at the same transitions (FAILED reason
  from `DiagnosisBindingHelper.BuildFailureReason`). Additive output `diagnosisDocumentId`.
  Now declares `[ResumeBehavior(LatestStateReEntry)]` + a `ComputeReEntryPositionActivity` node
  (documentType `diagnosis`), removed from `LegacyResumeAllowlist`.
- **`debug-rootcause.md` rewritten** to the canonical camelCase `Diagnosis` wire
  (`analysisSummary`/`hypotheses[].rank/description/confidence/suggestedFix/affectedFiles`), so the
  now-bound cell satisfies `ContractBindingTests`. (The old diagnosis/fix/verification shape is
  gone; version bumped 1→2.) Declared variable list unchanged (no prompt-cell count-pin churn).
- **`AIDiagnosisActivity.cs` deleted** (+ `AIDiagnosisActivityTests.cs`). Corpus ported to
  `DiagnosisBindingHelperTests`. `DiagnosisCrossParserTests` re-pointed from
  `AIDiagnosisActivity.ParseDiagnosisResponse` onto the surviving typed reader
  `Diagnosis.FromLegacyJson`. `NoDirectLlmCallTests` cut-over list entry removed.
- **Drift/declarations:** `ContractBindingTests.Bindings` gains
  `(senior_developer, debug-rootcause) → DiagnosisDocumentType.Validate` (7 tokens);
  `TaxonomyDriftBuildTests.ExpectedContributingWorkflows` gains `DebugDiagnosisWorkflow`
  (discovered via the lifecycle-binding walk); `DocumentTypeRegistry` gains a non-provisional
  `debug-diagnosis` → produces `diagnosis` edge (interface count 14→15,
  `WorkflowInterfaceGraphTests` reconciled list + count updated).

### Simplification recorded (Tdd/Ci escalation read)
`TddWithDebugRetryWorkflow` + `CiWithDebugRetryWorkflow` capture the additive
`diagnosisDocumentId` from the debugging dispatch result and thread it back as
`priorDiagnosisDocumentId` on the next attempt (so attempt N's `Diagnosis` supersedes N-1's —
the time-travel lineage), via a pure `ReadDiagnosisDocumentId` helper. Retry loops / budgets /
the `debuggerEscalated` gate are UNTOUCHED (`CiRetryCounterPersistenceTests` stays green). The
plan's fuller "read the accepted `Diagnosis` body via `FetchLatestAcceptedDocumentActivity` for
the escalation detail" was reduced to threading the id (the id satisfies the escalation-detail
need and avoids adding a store-read node + issueId anchor to the delicate retry graphs); the
full body-read on escalation is a low-value CI-only follow-up.

### Per-attempt supersedes
`DocumentLifecycleWorkflow` exposes no `supersedesDocumentId`/`parentDocumentId` input (same as
the 39-14 PlanGeneration precedent). So `DebugDiagnosisWorkflow` takes an optional
`supersedesDocumentId` input, folds a pointer into the DECLARED `recentChanges` carrier, and the
retry orchestrators thread the prior attempt's id. A native supersedes-lineage lifecycle input
remains filed to 39-6/39-11 (the same gap the creation family noted).

## Part B — Triage family (Steps 7–9, D5/D6/D9) — NOT implemented this pass

Deferred as a coherent second slice (the task's sanctioned fallback: "Part A complete + Part B's
precise state" — a green Debug-only slice is acceptable, as in slice 1). Nothing triage-related
was changed; all triage workflows/helpers/tests remain as merged.

**The 39-7 extension is UNBLOCKED — the resolved design (not yet coded):** add
`RolePhaseMap.GetPanelActionForRole(AgentRole role, string docTypeKey)` returning the triage
actions (`Security→AssessVulnerability`, `Developer/Tester→TriageDefect`,
`Devops→DiagnoseIncident`, via the existing `GetTriageActionForRole`) when
`docTypeKey == "triage-decision"` and the existing `GetReviewActionForRole` otherwise; route
`ReviewerSelectionHelper.Resolve` / `ResolvePanelRoster` and `PanelReviewWorkflow`'s roster
through the doc-type parameter (triage roster = the four triage roles). Tests land IN 39-7's
suites (`PanelReviewWorkflowTests` / `ReviewerSelectionHelperTests` / parity) proving the triage
doc-type yields the triage per-role actions + roster, with every existing 39-7 test byte-identical
for the document path. This is ONE aggregation/roster engine, doc-type-parameterized — never a
triage-local aggregator.

Then the migration per D5/D6/D9 (TriageContextGathering→Findings on a NEW `triage-context-scan`
split action; TriagePODecision→TriageDecision binding; delete `TriagePanelReviewWorkflow` +
`TriagePanelAggregationHelper`; TriageItemCycle rewire with apply-idempotence; helper adapter
reduction; drift moves; universal pin; `RemainingProducersLifecycleExecutionTests`). The panel
input→REVIEW-of-draft behavior change (recorded in slice-1's findings) applies when this lands.

## Build & test (this pass)
- `dotnet build` (ElsaServer, Activities.Tests, Core.Tests, Api.Tests): 0 errors, 0 new
  warnings on touched files.
- `Tamma.Activities.Tests` fast filter (`!~Execution & !~Integration`): **2553 passed, 0 failed**.
- `Tamma.Core.Tests`: **440 passed, 0 failed**.
- `Tamma.Api.Tests`: compiles.
- `has-pending-model-changes` clean for both `TenantDbContext` and `ControlPlaneDbContext`.
- No `[Ignore]`/`Assert.Ignore`/`.Ignore()` in the wave's tests; no model identifiers introduced.

## CI-only / filed back
- `RemainingProducersLifecycleExecutionTests` diagnosis-seam scenario (Testcontainers, `[Explicit]`,
  CI-Postgres) not added — it belongs with the full slice; the fast-filter suites cover the
  structural + helper halves. Filed as CI-only follow-up.
- Native lifecycle `supersedesDocumentId` input (39-6/39-11).
