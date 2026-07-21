# Implementation Plan — Story 39-1: Workflow I/O & Lifecycle Audit (consumes/produces map, gap analysis)

## Scope & Deliverable

When this story is done, a single audit document exists at `.dev/findings/epic-39-workflow-io-lifecycle-audit.md` containing one row per workflow file in `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/` (40 `*Workflow.cs` files today), with Consumes / Produces / Decision-point / Review-loop / Resumability / Gap-tags / Closed-by columns, an explicit exclusion note for scheduler/trigger shims, a verified consumer-edge list, and a cross-check against the Epic 39 README's 10-type document table. The Epic 39 README's story table row for 39-1 links to it. Zero `.cs` files are modified; any live bug discovered is filed separately under `.dev/bugs/` (directory created on first use). This document is the discovery gate that 39-2..39-16 build on.

## Pre-Reading

All story-referenced paths were verified to exist. Read in this order:

- `docs/stories/epic-39/story-39-1/39-1-workflow-io-and-lifecycle-audit.md` — the story (ACs 1–9)
- `docs/stories/epic-39/README.md` — settled design principles, the 10-type document table, the story table for "Closed by" pointers
- `docs/guides/BEFORE_YOU_CODE.md` — mandatory process
- `.dev/templates/finding-template.md` — findings format to adapt (see Design Decision 3)
- Enumeration machinery (reuse, do not hand-walk):
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaxonomyDriftBuildTests.cs` — `EnumerateAllDispatchPairs()` (internal static, returns `DispatchPair(Workflow, DispatchId, Role, Action)`), `ExpectedContributingWorkflows` (20 known dispatch-bearing workflows), `MinExpectedDispatchPairs` (44 pairs observed at authoring time)
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` — the `Bindings` map: per `(role, action)` cell, the named fail-closed parser and its required JSON tokens (pre-verified parser evidence, cite it)
  - `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/WorkflowTestHelper.cs` — `BuildWorkflow` used by the reflection
- Fail-closed parsers (today's informal shapes):
  - `apps/tamma-elsa/src/Tamma.Activities/Decomposition/DecompositionParsing.cs` — `ParseDecomposition` (`summary` + `subtasks[]` with `id`/`title`/`description`/`acceptanceCriteria`/`estimateHours`/`complexity`/`dependsOn`; null = fail-closed)
  - `apps/tamma-elsa/src/Tamma.Activities/Research/ResearchParsing.cs` — `ParseReport`
  - `apps/tamma-elsa/src/Tamma.Activities/Ambiguity/AmbiguityParsing.cs` — `ParseAssessment`
  - `apps/tamma-elsa/src/Tamma.Activities/Clarify/ClarifyParsing.cs` — `ParseQuestions` (bare string array), `ParseClarification`
  - `apps/tamma-elsa/src/Tamma.Activities/Design/DesignParsing.cs` — `ParseProposal`
- Verdict/aggregation helpers, `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/`:
  - `ReviewAggregationHelper.cs` — `ParseRoleVerdict` (pessimistic `"concerns"` default; accepts BOTH the legacy string verdict and the `{"verdict":{"decision":...}}` object shape)
  - `PlanValidationHelper.cs` — `ExtractJson` / `ValidatePlan` (`tasks|steps`, `fileMap|files|filesToModify`)
  - `ValidationFeedbackHelper.cs` — `AppendFeedback` (the existing validate→retry feedback ring, precursor of 39-9)
  - `TriagePoDecisionHelper.cs` — status vocabulary `ok|unparsed|llm-failed|skipped`, documents the laundered-failure fixes
  - `TriagePanelAggregationHelper.cs`, `TriageItemCycleHelper.cs` (also present but unreferenced: `TriageContextHelper.cs` — include it in the audit's evidence pass)
- Resumability positive baseline, `apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/`: `DesignResumeEndpoint.cs`, `ClarifyResumeEndpoint.cs`, `MergeApprovalResumeEndpoint.cs`, `DeploymentApprovalResumeEndpoint.cs`, `BlockerResumeEndpoint.cs` — note each documents its bookmark-name scheme (e.g. `design-approval-{tenant}-{session}` via `WaitForDesignApprovalActivity.ApprovalBookmarkName`)
- Suspend activities to trace (grep `class WaitFor` under `apps/tamma-elsa/src/Tamma.Activities/`): `ADL/WaitForMergeApprovalActivity.cs`, `ADL/WaitForPRMergedActivity.cs`, `ADL/WaitForCycleCallbackActivity.cs`, `ADL/WaitForDeploymentApprovalActivity.cs`, `ADL/WaitForPlanApprovalActivity.cs`, `ADL/WaitForPRApprovalActivity.cs`, `Review/WaitForFixesActivity.cs`, `Assessment/WaitForResponseActivity.cs`, `Design/WaitForDesignApprovalActivity.cs`, `Clarify/WaitForClarifyingAnswersActivity.cs`, `Testing/WaitForCIResultsActivity.cs`
- Third review shape: `apps/tamma-elsa/src/Tamma.Activities/Review/Models/ReviewModels.cs` (`ReviewResult`, `CodeReviewWorkflowResult`) + `Review/BuildCodeReviewResultActivity.cs`
- NOT FOUND: none — every story-referenced path exists. (`.dev/bugs/` does not exist yet; create it only if a live bug must be filed.)

## Design Decisions

1. **Deliverable location: `.dev/findings/epic-39-workflow-io-lifecycle-audit.md` (the story's preferred option), no pointer file.** AC9 allows a story-directory copy with a pointer; a single canonical file in the knowledge base avoids a two-file sync problem, and the epic README link (AC9's second half) covers discoverability from the story table.

2. **Honoring "modifies zero `.cs` files" while reusing the reflection.** `TaxonomyDriftBuildTests.EnumerateAllDispatchPairs()` is `internal static` in the test assembly and prints nothing. Recommended: add a throwaway, NEVER-committed dump test in `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/` (sketch in Step 2), run it once, paste its output into the audit, delete the file before commit. Rejected alternatives: hand-walking dispatch sites (the story forbids it — "do NOT hand-walk dispatches"), and promoting the enumeration to a shared utility (a real `.cs` change, which is 39-2's business when it seeds its registry). The zero-`.cs`-files rule constrains the *committed* diff.

3. **Findings-template adaptation.** `.dev/templates/finding-template.md` is a TS-era generic (code-snippet-heavy, TammaError examples). Keep its identity header (`Date / Author / Type / Category`), `## 📋 Summary`, and `## 🔍 Context` sections — Type: `📚 Lesson Learned`, Category: `Architecture` — then replace the body with the audit-specific sections listed in Step 1. The story says "follows the `.dev/templates/` findings format"; matching the header + summary/context skeleton satisfies that without forcing a workflow inventory into a code-gotcha template. Note the adaptation explicitly in the document's Context section.

4. **One master table + per-family evidence subsections.** AC1 wants one row per workflow; ACs 2–5 want file/line evidence per row, which would make a single table unreadable. Recommended layout: a master table using the story's suggested columns (`Workflow | Consumes | Produces (informal shape + parser) | Decision point | Review loop | Resumability | Gap tags | Closed by`) with terse cell values, plus one evidence subsection per workflow family carrying the `path:line` pointers, JSON shape excerpts, and bookmark names the table cells summarize.

5. **Record reality, not the story's illustrative examples.** AC2's example cites `DecompositionParsing.Parse` producing `tasks[]` with `id/dependsOn/estimate`; the actual API is `DecompositionParsing.ParseDecomposition` producing `subtasks[]` with `estimateHours` (verified at `apps/tamma-elsa/src/Tamma.Activities/Decomposition/DecompositionParsing.cs:44`). The story is explicit that the audit is descriptive — the audit records the verified names/shapes, and this plan flags the discrepancy so nobody "corrects" the audit back to the example.

6. **Resumability classification decision procedure (AC5).** To keep (a)/(b)/(c)/(d) judgments reproducible, apply this rule per row: (a) requires citing a suspend activity AND one of the five `*ResumeEndpoint.cs` files (or another proven resume seam, e.g. webhook-driven resume for `WaitForCIResultsActivity` / `WaitForFixesActivity` — cite the driver); (b) = the workflow registers a bookmark or is a long-lived Elsa instance that would survive restart, but no exercised resume path exists (cite the bookmark name and the absence); (c) = no suspend point and meaningful multi-step LLM/git state that restarts from step one; (d) = stateless/mechanical single-shot (e.g. `UpdateIssueStatusWorkflow`, tenant lifecycle). Every (b)/(c) row gets `NOT_RESUMABLE` → closed by 39-10 (and 39-8 where the missing piece is a decision gate).

7. **Gap tags are multi-valued.** A row like `TaskReviewWorkflow` will carry `UNTYPED_OUTPUT + PARSE_OK_IS_ACCEPT + NO_REVISE_LOOP + FORKED_REVIEW_SHAPE` simultaneously; each tag maps independently to its closing story (39-3/39-4, 39-5/39-6, 39-6/39-7, 39-4 respectively, per the README story table). Do not collapse to "worst gap wins" — 39-2's edge list and 39-5's decision inventory each consume a different tag.

8. **Exclusion note scope.** The `ls`-reconciled row set is exactly `*Workflow.cs` (40 files). The exclusion note lists, with one-line reasons: `TenantCleanupRequestedTrigger.cs`, `TenantDeleteRequestedTrigger.cs`, `HourlyAnalyticsRollupScheduler.cs` (pure trigger/scheduler shims), and `WorkflowVersions.cs`, `ActivityDisplayTextExtensions.cs` (not workflows). `HourlyAnalyticsRollupWorkflow.cs` IS a row (mechanical, likely class (d)) — only its scheduler shim is excluded.

## Implementation Steps

All paths repo-relative. Only Step 1's file and Step 9's README edit are committed; Step 2's harness is created and deleted locally.

1. **CREATE `.dev/findings/epic-39-workflow-io-lifecycle-audit.md` — skeleton + inventory + exclusion note.**
   Copy the header style from an existing dated finding (`.dev/findings/platform-task-worker-runonstartup-hazard.md` is a good in-house precedent for "audit-style" findings prose). Sections: header block; Summary; Context (story link, template-adaptation note); **Exclusion note** (Design Decision 8); **Master table** (one row per workflow, initially just the 40 names from `ls apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/*Workflow.cs`); empty family-evidence subsections; **Reconciliation appendix** embedding the `ls` output and the stated row count (AC1's "stated and reconciled").

2. **Seed the producer map from the existing reflection (temporary, uncommitted).**
   Create `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/DispatchPairDumpScratch.cs`:
   ```csharp
   [TestFixture, Explicit]
   public class DispatchPairDumpScratch
   {
       [Test]
       public void Dump() // scratch only — DELETE before commit
       {
           foreach (var p in TaxonomyDriftBuildTests.EnumerateAllDispatchPairs()
                        .OrderBy(p => p.Workflow).ThenBy(p => p.DispatchId))
               TestContext.Out.WriteLine($"{p.Workflow} | {p.DispatchId} | {p.Role} | {p.Action}");
       }
   }
   ```
   Run `dotnet test --filter DispatchPairDumpScratch` from `apps/tamma-elsa/`, paste the output into a "Dispatch-pair seed" appendix of the audit, then **delete the file**. Cross-check: every workflow in `TaxonomyDriftBuildTests.ExpectedContributingWorkflows` (20 names) appears; pair count ≥ 44. Pull the parser-per-cell pointers straight from `ContractBindingTests.Bindings` (each entry already names the parser and its required tokens with file references) into the relevant rows' Produces cells.

3. **Fill the assessment + decomposition family rows (AC2, AC3 partial).**
   Read `ResearchWorkflow.cs`, `AmbiguityScoringWorkflow.cs`, `ClarifyingQuestionsWorkflow.cs`, `DesignProposalWorkflow.cs`, `AssessmentWorkflow.cs`, `IssueDecompositionWorkflow.cs` under `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/`, plus their parsers and `Emit*EventActivity.cs` / event-constant files (`ResearchEvents.cs`, `AmbiguityEvents.cs`, `ClarifyEvents.cs`, `DesignEvents.cs`, `DecompositionEvents.cs` under `apps/tamma-elsa/src/Tamma.Activities/{Research,Ambiguity,Clarify,Design,Decomposition}/`). Per row record: consumes (issue payload / prior JSON / config vars), produces (parser + `path:line` + shape), decision point (null-parse → which FAILED terminal), events emitted. `AssessmentWorkflow` (dispatches `generate-assessment-questions` / `analyze-assessment-response`, suspends on `Assessment/WaitForResponseActivity.cs`) is a likely AC7 open-question producer — flag, don't force-fit.

4. **Fill the planning + review family rows and pin the three forked review shapes (AC3, AC4).**
   Read `PlanGenerationWorkflow.cs`, `PlanReviewWorkflow.cs`, `TaskCreationWorkflow.cs`, `TaskReviewWorkflow.cs`, `TestCaseCreationWorkflow.cs`, `CodeReviewWorkflow.cs`, `ReviewFixWorkflow.cs`. Pin with `path:line`:
   - Shape 1 (legacy string verdict): inline `TryGetProperty("verdict")` aggregation in `TaskReviewWorkflow.cs` (~lines 148–183, pessimistic `"concerns"`, hardcoded fallback at ~line 376) and the string branch of `ReviewAggregationHelper.ParseRoleVerdict` (`Helpers/ReviewAggregationHelper.cs:26`).
   - Shape 2 (PlanReview object verdict): `ReviewAggregationHelper.ParseObjectVerdict` (`{"verdict":{"decision":"APPROVE|REQUEST_CHANGES|NEEDS_DISCUSSION",...}}`), consumed at `PlanReviewWorkflow.cs:237`; document PlanReview's discussion/revision rounds as the known positive review-loop case.
   - Shape 3 (code-review result): `Tamma.Activities/Review/Models/ReviewModels.cs` (`ReviewResult`, `CodeReviewWorkflowResult`) built by `Review/BuildCodeReviewResultActivity.cs`.
   Also record `PlanValidationHelper.ValidatePlan` + `ValidationFeedbackHelper.AppendFeedback` as the existing bounded validate→retry ring (evidence for 39-9's "Closed by" cell).

5. **Fill the triage + debugging/blocker family rows (AC3).**
   Read `IssueTriageWorkflow.cs`, `TriageItemCycleWorkflow.cs`, `TriagePanelReviewWorkflow.cs`, `TriagePODecisionWorkflow.cs`, `TriageContextGatheringWorkflow.cs`, `BlockerDiagnosisWorkflow.cs`, `DebuggingWorkflow.cs` plus `TriagePoDecisionHelper.cs` (record its `ok|unparsed|llm-failed|skipped` status vocabulary and the laundered-failure history its XML doc narrates — this is AC3's "ugly truths" exemplar), `TriagePanelAggregationHelper.cs`, `TriageItemCycleHelper.cs`, `TriageContextHelper.cs`.

6. **Fill the orchestrating + mechanical/infra rows (AC1 completeness, AC8 seed).**
   Read `AdlOrchestratorWorkflow.cs`, `SingleIssueCycleWorkflow.cs` (1,405 lines — budget time), `LlmCallWorkflow.cs`, and the mechanical set (`BranchCreationWorkflow`, `MergeWorkflow`, `PullRequestWorkflow`, `DeploymentPipelineWorkflow`, `TddWorkflow`, `TddWithDebugRetryWorkflow`, `CiWithDebugRetryWorkflow`, `TestingWorkflow`, `MergeApprovalWorkflow`, `MentorshipWorkflow`, `ContextGatheringWorkflow`, `UpdateIssueStatusWorkflow`, tenant lifecycle ×4, `HourlyAnalyticsRollupWorkflow`). For mechanical rows the point is proving they are *out* of document scope (side-effects, not documents) per the README's "Code is NOT a document type" principle. While reading the orchestrators, record every consumer edge: which variable/JSON each sub-workflow result lands in and who reads it next (e.g. `SingleIssueCycleWorkflow` consuming decomposition output; `AdlOrchestratorWorkflow` consuming assessment outputs).

7. **Resumability classification pass over all 40 rows (AC5).**
   Apply Design Decision 6. For class (a), pair each of the five endpoints with its suspend activity and bookmark name (e.g. `WaitForDesignApprovalActivity.ApprovalBookmarkName(tenant, session)` ↔ `DesignResumeEndpoint`; `BlockerResumeEndpoint` documents two bookmark families — record both). Trace the six ADL `WaitFor*` activities + `WaitForCIResultsActivity`/`WaitForFixesActivity` to whatever resumes them (endpoint, webhook, signal, or nothing) and classify accordingly. Every row cites evidence: bookmark name, suspend activity, or "no suspend point".

8. **Gap classification, closed-by mapping, document-type coverage, consumer-edge list (AC6, AC7, AC8).**
   Tag every gap with the closed set `UNTYPED_OUTPUT | PARSE_OK_IS_ACCEPT | NO_REVISE_LOOP | FORKED_REVIEW_SHAPE | NOT_RESUMABLE | BARE_FAILURE_ESCALATION` and map each tag to its closing story (39-2..39-16) per the README table. Add the **Document-type coverage** section: a second table crossing the README's 10 types (Findings, AmbiguityAssessment, Clarification, Decomposition, Plan, Design, Review, TriageDecision, Diagnosis, TestSpec) against audited producers — every producer maps to exactly one type, is explicitly out of scope (code / prose / mechanical), or is flagged as an **open question for 39-3/39-4** (expected flags: `AssessmentWorkflow` Q&A outputs, `MentorshipWorkflow` guidance, `ContextGatheringWorkflow`/`TriageContextGatheringWorkflow` context bundles vs the Findings type). Add the **Consumer edges** section from Step 6's notes, formatted as `producer → shape → consumer(s)` so 39-2 can transcribe it into `consumes:[X]/produces:Y` declarations.

9. **Finalize (AC9).** MODIFY `docs/stories/epic-39/README.md`: in the story table's 39-1 row, link the title to `../../../.dev/findings/epic-39-workflow-io-lifecycle-audit.md` (keep column shape intact). Verify the reconciliation appendix count still matches `ls`. Confirm the deleted scratch harness is not in the diff (only `.md` files changed). File any live bugs found during reading in `.dev/bugs/` (create the directory, use `.dev/templates/bug-template.md`) — referenced from, not folded into, the audit.

## Data & Migrations

None. This story is documentation-only; no tables, no EF changes, no migrations under `apps/tamma-elsa/src/Tamma.Data/Migrations/`.

## Events

None emitted or consumed by this story. The audit *catalogs* existing event constants per row as data (e.g. `DecompositionEvents`, `ClarifyEvents`, `DesignEvents`, `ResearchEvents`, `AmbiguityEvents`, `BlockerEvents`, `CodeReviewEvents` in `apps/tamma-elsa/src/Tamma.Activities/*/`), and its gap map references the future `DOCUMENT.*` (39-6) and `APPROVAL.*`/`ESCALATION.*` (39-8) families only as "Closed by" pointers.

## Test Plan

No committed test code (doc-only story; the zero-`.cs`-files rule). Verification is procedural, anchored on existing named tests:

- **Reconciliation check (AC1):** `ls apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/*Workflow.cs | wc -l` equals the stated row count (40 at planning time); the exclusion note names every non-`*Workflow.cs` file in the directory. Re-run at finalize in case the branch moved.
- **Enumeration cross-check (AC2/AC8 seeding):** the Step 2 dump ⊇ `TaxonomyDriftBuildTests.ExpectedContributingWorkflows` (20 workflows) and ≥ 44 pairs; any delta is investigated, not hand-waved. `dotnet test --filter TaxonomyDriftBuildTests` green confirms the enumeration itself is healthy before trusting its output.
- **Binding cross-check (AC2/AC3):** every cell in `ContractBindingTests.Bindings` appears in exactly one row's Produces/Decision-point cells with the same parser name; `dotnet test --filter ContractBindingTests` green confirms the cited contracts are current.
- **Resumability evidence check (AC5):** each class-(a) row's cited bookmark builder exists in the named `WaitFor*Activity`/`*ResumeEndpoint` pair (spot-verify by grep); the five endpoints each appear in at least one row.
- **Coverage checks (AC6/AC7/AC8):** every gap tag drawn from the closed six-tag set and every "Closed by" from the README story table (39-2..39-16); every audited producer appears in the document-type coverage table exactly once; every non-mechanical Produces cell has ≥1 consumer edge or an explicit "terminal output" note.
- **Diff check (story constraint + AC9):** the final diff touches only `.dev/findings/epic-39-workflow-io-lifecycle-audit.md`, `docs/stories/epic-39/README.md`, and any `.dev/bugs/` filings — no `.cs`.
- **Peer review:** a second reader samples 5 rows (one per family) against the source before the PR merges.

## Definition of Done

| AC | Satisfied by | Verified by |
|---|---|---|
| 1. Complete inventory + exclusion note + reconciled count | Steps 1, 3–6 | Reconciliation check; diff check |
| 2. Consumes/produces with informal shapes + parser pointers | Steps 2–6 | Enumeration cross-check; binding cross-check |
| 3. Decision-point column (parser/helper, failure behavior, who reviews) | Steps 3–5 (helpers: `ReviewAggregationHelper`, `TriagePoDecisionHelper`, `PlanValidationHelper`, inline `TaskReviewWorkflow`) | Binding cross-check; peer review |
| 4. Review-loop column + three forked shapes with pointers | Step 4 | Peer review (shape 1/2/3 pointers resolve) |
| 5. Resumability column, (a)–(d) with evidence | Step 7 | Resumability evidence check |
| 6. Gap classification with closed tag set + closing story | Step 8 | Coverage checks |
| 7. Document-type coverage cross-check, open questions flagged | Step 8 | Coverage checks; peer review |
| 8. Consumer edges captured | Steps 6, 8 | Coverage checks |
| 9. Lands in `.dev/findings/`, follows template, linked from README | Steps 1, 9 | Diff check; link resolves from README row |

## Dependencies & Sequencing

- **Must be on the branch first:** the PR #475 substrate — verified present in the repo: `apps/tamma-elsa/src/Tamma.Api/Prompts/{role}/` (8 role directories), `ContractBindingTests.cs`, `TaxonomyDriftBuildTests.cs`. Nothing else blocks this story.
- **Nothing is stubbed.** This story consumes only existing code and produces a document; no interfaces or fakes are needed. It deliberately does NOT create any 39-2 types (`DocumentEnvelope`, `DocumentTypeRegistry`), 39-5 policy (`AcceptanceRules`), or 39-6 workflow (`DocumentLifecycleWorkflow`, `DocumentEvents`) — those names appear only in "Closed by" cells.
- **Everything downstream sequences on this:** 39-2 (registry + edge list) and 39-3/39-4 (type definitions) start from the audit's shapes/parser pointers; 39-5 from the decision-point inventory; 39-6/39-7 from the review-loop inventory; 39-8/39-10 from the resumability classification. Land this before any of them are planned in detail; if 39-2 starts in parallel, hand it the consumer-edge section as soon as Step 8 stabilizes, flagged draft.

## Risks & Mitigations

- **Reading volume blows the estimate** (SingleIssueCycle 1,405 / Debugging 1,286 / Mentorship 1,278 lines). Mitigate: mechanical rows (Step 6) need only enough reading to justify (d)/out-of-scope + consumer edges — do not line-audit them like producers; timebox per family and mark any row `[shallow — revisit]` rather than stalling.
- **Enumeration output mistaken for the whole picture.** The reflection yields only `llm-call` dispatch pairs; side-effects, suspend points, and consumers are invisible to it. Mitigate: the story's own rule — seed from reflection, verify by reading — is baked into Steps 3–7 as separate passes.
- **Scope creep into fixing.** Ugly truths (pessimistic defaults, laundered failures) invite drive-by patches, breaking the zero-`.cs` rule. Mitigate: the audit is descriptive; live bugs go to `.dev/bugs/` (Step 9); prescriptions are story pointers only.
- **Scratch harness leaks into the commit.** Mitigate: `[Explicit]` attribute, `Scratch` suffix, explicit delete in Step 2, and the diff check in the Test Plan.
- **Drift between audit and branch while later stories land.** Mitigate: the reconciliation appendix records the commit SHA audited; 39-2's build test becomes the living version of this map, so the audit is a dated snapshot by design.
- **Misclassification of resumability (b) vs (c).** Mitigate: Design Decision 6's evidence rule; peer-review sampling targets at least one (b) and one (c) row.

## Effort Breakdown

| Step | Work | Days |
|---|---|---|
| 1 | Skeleton, inventory, exclusion note, reconciliation appendix | 0.25 |
| 2 | Reflection dump harness, run, seed, delete | 0.25 |
| 3 | Assessment + decomposition family rows | 0.5 |
| 4 | Planning + review families, three forked shapes | 0.75 |
| 5 | Triage + debugging/blocker families | 0.5 |
| 6 | Orchestrating + mechanical rows, consumer-edge notes | 0.5 |
| 7 | Resumability classification pass | 0.5 |
| 8 | Gap map, coverage table, consumer-edge section | 0.5 |
| 9 | README link, final checks, bug filings | 0.25 |
| **Total** | | **4.0** |

Matches the story's 3–4 day estimate at the top of the range; Steps 3–6 compress if family reading goes faster than budgeted.
