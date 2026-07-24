# Story 39-15 slice 3 — Triage family migration (Steps 7–9 + 10–12 triage parts) + the 39-7 extension

Status: **complete and green** (fast filter + Core.Tests). Completes Story 39-15 after
slice 1 (creation family + cross-doc seam) and slice 2 (debug diagnosis). The triage family
now rides the document lifecycle: `triage-context-gathering` → a Findings binding,
`triage-po-decision` → a TriageDecision binding whose REVIEW stage is the 39-7 panel, and
`triage-item-cycle` stays a resumable orchestrator over the two.

## STEP 1 — the 39-7 extension (doc-type-aware panel), tests in 39-7

The panel's per-member action resolution was hardcoded to the plan/task review lens and was
NOT doc-type-aware. Extended, keeping ONE aggregation/roster engine:

- **`RolePhaseMap.GetPanelActionForRole(role, docTypeKey)`** (new) — composes the two existing
  maps: `GetTriageActionForRole(role)` when `docTypeKey == "triage-decision"`, else
  `GetReviewActionForRole(role)`. The two existing methods are untouched.
- **`ReviewerSelectionHelper.Resolve`** threads the `documentTypeKey` parameter (already carried
  on the review subject) into `ResolveDocumentAction(role, docTypeKey)` → `GetPanelActionForRole`.
  So a `triage-decision` review dispatches each panellist with its TRIAGE action; every other
  document type resolves the review lens exactly as before — **the document path is byte-identical**.
- **Roster**: the panel's runtime roster already comes from `ReviewerSelection.PanelRoles` (39-5
  acceptance rules); the triage default rules ship the four triage roles. `PanelReviewWorkflow`'s
  static roster is the 7-role superset (the four triage roles are a subset), so the graph gates the
  non-roster roles out with no code change. `ReviewerSelectionHelper.TriagePanelRoster` (new) backs
  the classification pins.
- **`AllDispatchablePairs`** grew 12 → 16 (added the 4 triage-panel pairs).

**Proof the doc path stayed green + triage works** (all in 39-7's suites):
- `ReviewerSelectionHelperTests.AllDispatchablePairs_AreSixteenAndAllEligible` (was `…Twelve…`).
- `ReviewerSelectionHelperTests.Resolve_TriageDecisionSubject_YieldsTriagePerRoleActions` (NEW) —
  security→assess-vulnerability, developer/tester→triage-defect, devops→diagnose-incident.
- `ReviewerSelectionHelperTests.Resolve_NonTriageDocument_StillYieldsReviewActions_DocPathUnchanged`
  (NEW) — plan/null still yield the review lens.
- `ReviewerSelectionHelperTests.AllDispatchablePairs_ContainsTheFourTriagePanelPairs` (NEW).
- Every pre-existing 39-7 test (`Resolve_DocumentSubject_*`, `Resolve_DiffSubject_*`,
  `PanelReviewWorkflowStructureTests`, `SingleReviewerWorkflowStructureTests`, parity suites) stays
  green — the document path is unchanged.
- `ContractBindingTests.ReviewerSelectionHelper_AllDispatchablePairs_HasSixteenEligiblePairs`.

## STEP 2 — the triage migration

- **`triage-context-gathering` → Findings binding** (39-13 Research recipe): NEW split action
  `(developer, triage-context-scan)` — a distinct `AgentAction.TriageContextScan` (wire
  `triage-context-scan`), a new `RolePhaseMap` developer cell, a new prompt
  `Prompts/developer/triage-context-scan.md` (Findings shape). Legacy `contextJson` (= accepted
  Findings body) / `contextStatus` (`"ok"` on accept, `"failed"` otherwise) preserved; additive
  `findingsDocumentId`. `TriageContextHelper.DetectItemType` kept; `ExtractContext` no longer used.
  `ContextGatheringWorkflow` + its `context-scan` cell stay free-text (unmigrated).
- **`triage-po-decision` → TriageDecision binding** (DefinitionId kept): documentType
  `triage-decision`, PRODUCE `(product_owner, triage-intake)`, VALIDATE
  `TriageDecisionDocumentType`, REVIEW = the doc-type-aware panel over the triage roster, ACCEPT =
  orchestrator gate. Ships `TriageBindingHelper.DefaultTriageRulesJson()` (39-5 mechanism: panel
  roster + quorum 2 + a `triage-intake` needs-human always-escalate class). Legacy `decisionJson`
  (accepted TriageDecision → wire via `ProjectLegacyDecisionJson`), `callSucceeded` (accept→true),
  `providerUsed`/`costUsd`/`rawResponse` (empty audit) preserved. Empty-input SKIPPED short-circuit
  kept (emitted before any dispatch). `triage-intake.md`'s contract block rewritten P0–P3 → the
  26-1 TriageDecision wire (version bumped 1→2).
- **NEW `Helpers/TriageBindingHelper.cs`** — `ProjectLegacyDecisionJson` (fail-closed to an honest
  needs-human on a null/field-incomplete body), `ReadPanelMirror`, `BuildFailureDetail`,
  `DefaultTriageRulesJson`, and the relocated `ParseItemNumber`.
- **DELETED `TriagePanelReviewWorkflow.cs` + `Helpers/TriagePanelAggregationHelper.cs`** — the panel
  semantics are now 39-7 lifecycle config. `ParseItemNumber` moved to `TriageBindingHelper`;
  `TriagePoDecisionHelper.ParseItemNumber` now delegates there. The four panel cells
  (`security/assess-vulnerability`, `developer/triage-defect`, `tester/triage-defect`,
  `devops/diagnose-incident`) regenerated to the unified Review verdict contract.
- **`triage-item-cycle`** stays a non-binding orchestrator: `panelReview`/`extractPanelResult`/
  `panelUsable` nodes deleted; the decision gate deserializes the TYPED TriageDecision
  (`TriageItemCycleHelper.ReadTypedDecision`); `findingsDocumentId` threads into the po-decision
  dispatch; a `ComputeReEntryPositionActivity` + `AlreadyComplete` gate give apply-idempotence (a
  crash re-entry after accept short-circuits to one idempotent `TRIAGE.ISSUE.COMPLETED`, no
  re-apply). `ValidateLabels`/`RenderComment`/`ApplyTriageResultActivity` Success/Failure /
  `ContinueWithIncidentsStrategy` / seeded fail-closed `itemResult` preserved.
- **`IssueTriageWorkflow`** unchanged (fan-out, no llm-call).
- **Events (D6)**: `TRIAGE.CONTEXT.STARTED/COMPLETED/FAILED` (Findings binding),
  `TRIAGE.PO_DECISION.STARTED/COMPLETED/FAILED/SKIPPED`, `TRIAGE.PANEL.STARTED/COMPLETED/FAILED`
  (mirrored at the REVIEW boundary via `ReadPanelMirror`), `TRIAGE.ISSUE.*` + `TRIAGE.LABELS.INVALID`
  in the cycle. Terminal emits gated on re-entry position (no double-emit).
- **Resume (D8)**: all three triage workflows declare `[ResumeBehavior(LatestStateReEntry)]` with a
  `ComputeReEntryPositionActivity`.

## Behavior change recorded (panel input-side → REVIEW-of-a-draft)

The 4-role triage panel changed from an **input-side** stage (feeding the PO raw per-role
assessments to reason over) to a **REVIEW-of-a-draft-decision** stage inside the TriageDecision
lifecycle: the PO now produces a *draft* `TriageDecision`, and the panel critiques THAT draft
(APPROVE / REQUEST_CHANGES / NEEDS_DISCUSSION) rather than pre-feeding it. This is a real semantic
change. Mitigation: the producer variables still carry the gathered Findings context
(`contextFindings`), so the draft is context-informed; the panel's informational role moves into
review issues; quality is rules-tunable (rounds/roster/quorum), not code. The 39-7 panel's blocking
veto + quorum-2 majority preserve the old "a wholly-failed/undecidable panel does not silently
approve" guarantee.

## Universal pin (D7c) — what it asserts + the documented residual

Three assertions, placed where the private data lives:

- **(a)** `ContractBindingTests.UniversalPin_EveryBindingAuthority_IsDocumentTypeValidate_OrDocumentedResidual`
  — every `Bindings` parser authority ends in `DocumentType.Validate` EXCEPT a documented
  **non-document-producer residual**: `(product_owner, generate-assessment-questions)`,
  `(product_owner, analyze-assessment-response)`, `(devops, deploy)`, `(devops, rollback)`. These
  four cells are NOT document producers (assessment intake feeds a loop; deploy/rollback are
  side-effect gates) and still use inline parsers. The residual set is ratcheted (stale entries
  fail).
- **(b)** `ContractBindingTests.UniversalPin_EveryIntentionallyUnbound_IsProseOrCode` — every
  remaining allowlist justification classifies as prose (free-text) or code
  (success-flag/file-format/lenient-degrade). No document producer hides there.
- **(c)** `ResumableStandardStructuralTests.UniversalPin_EveryLegacyResumeAllowlistEntry_IsNonDocumentProducer`
  — no workflow that dispatches a document-lifecycle binding remains on the resume allowlist. The
  allowlist is **NOT empty** (the aspirational D7 "empty" is reached only when the non-producer
  residual — context-gathering, mentorship, code-review, blocker-diagnosis, testing/tdd composites,
  platform sagas, side-effect leaves — is also migrated), but it is HONEST: every un-migrated
  document producer has been removed.

**Documented residual (why the graph is not yet 100% typed):** the Assessment intake cells and the
deploy/rollback stage-status cells are the only non-`DocumentType.Validate` binding authorities left,
and they are non-document-producers. This is the expected end-of-wave state, not a regression.

## D9 adapter-reduction deviation (recorded)

The plan's D9 said to RETIRE `TriagePoDecisionHelper.ParseDecision`/`Clamp`. They were KEPT (the
production workflow no longer calls them, but `TriageDecisionCrossParserTests` and
`TriageDecisionTypeTests`' round-trip pin reference `ParseDecision` as the fail-safe legacy baseline,
and `TriageItemCycleHelper.IsDecisionApplicable(bool, string?)` still uses it). Retiring them would
churn passing cross-parser tests for no functional gain. `TriageBindingHelper.ProjectLegacyDecisionJson`
uses `ParseDecision(null)` as its honest-fallback renderer. `TriageItemCycleHelper` gained
`ReadTypedDecision` (the typed-exit adapter) as planned; the cycle routes on it.

## Build & test

- `dotnet build` (full solution): **0 errors**. Touched triage workflow files carry the same
  CS8603/CS8601 nullable-lambda warnings the merged 39-14 `PlanGenerationWorkflow` binding carries
  (identical `.Get(ctx)`/`.Set(ctx,…)` house pattern) — no new warning categories; the ElsaServer
  baseline is ~786 warnings.
- `Tamma.Activities.Tests` (fast filter `!~Execution & !~Integration`): **2522 passed, 0 failed**
  (incl. the 12 pre-existing 39-7 panel tests, the new 39-7 triage tests, and the universal-pin tests).
- `Tamma.Core.Tests`: **440 passed, 0 failed**.
- `Tamma.Api.Tests`: compiles.
- `dotnet ef migrations has-pending-model-changes` clean for both `TenantDbContext` and
  `ControlPlaneDbContext`.
- No `[Ignore]`/`Assert.Ignore`/`.Ignore()` in the wave's tests; no model identifiers introduced.

## Taxonomy count pins (consciously bumped)

- `AgentActionTests`: `Enum.GetValues<AgentAction>().Length` 79 → 80.
- `RolePhaseMapTests`: `ValidActions` 79 → 80.
- `SystemPromptsTests`: prompt-cell count is computed live from `RolePhaseMap.EligibleActions`
  (auto-adjusts; no literal to bump — the new `(developer, triage-context-scan)` cell + prompt file
  are covered by the coverage/loader tests).
- `WorkflowInterfaceGraphTests`: interface count 15 → 16; `triage-context-gathering` +
  `triage-po-decision` added to the reconciled (non-provisional) list.
- `TaxonomyDriftBuildTests`: `MinExpectedDispatchPairs` 25 → 21 (the 4 panel dispatch sites vanish;
  the context/PO pairs become lifecycle-binding-walk pairs); `TriagePanelReviewWorkflow` removed from
  `ExpectedContributingWorkflows`.

## CI-only / filed back

- `RemainingProducersLifecycleExecutionTests` triage end-to-end scenarios (Testcontainers,
  `[Explicit]`, CI-Postgres) — NOT added this pass, matching the slice-1/slice-2 precedent (they
  filed their execution scenarios back too). The fast-filter structure + helper suites cover AC1–AC8's
  static halves; the runtime halves (accept-resume, mid-panel crash re-entry, per-family replay) are
  the filed follow-up.
- `TriageItemCycleApplyFaultExecutionTests` (excluded by the `~Execution` filter) targets ONLY the
  preserved apply-branch wiring (Success/Failure → COMPLETED/FAILED terminals) — no panel references,
  compiles clean, unchanged in behavior.
