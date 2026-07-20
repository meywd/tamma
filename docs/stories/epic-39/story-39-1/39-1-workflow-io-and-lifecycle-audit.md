# Story 39-1: Workflow I/O & Lifecycle Audit (consumes/produces map, gap analysis)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **platform architect designing Epic 39's typed-document layer**,
I want a **complete, per-workflow map of what every Elsa workflow consumes and produces today (informal shapes), how it decides "ok vs not-ok", whether it has any review-with-notes loop, and whether it can resume after a stop**,
So that the document type definitions (39-2..39-4), the acceptance rules (39-5), and the generic lifecycle (39-6) are grounded in the real current behavior — and every gap (parse-ok?→done:dead decisions, missing review loops, restart-from-scratch workflows) is classified and traceable to the story that closes it.

## Priority

P0 — This is the epic's discovery gate. Every later story either defines a type for a shape found here (39-3/39-4), replaces a decision point found here (39-5/39-6/39-7), or fixes a resumability gap found here (39-8/39-10). Skipping the audit means guessing at the informal shapes and re-discovering them mid-implementation — exactly the scatter PR #475 exposed.

## Architectural Context (READ FIRST)

This story produces a **document, not code**. The deliverable lives in `.dev/findings/` (preferred, so it enters the knowledge base) or this story directory.

The audit surface is the **entire workflow catalog**:

- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/` — every `*Workflow.cs` file (30+ today), including but not limited to:
  - Producer workflows: `IssueDecompositionWorkflow.cs`, `ResearchWorkflow.cs`, `AmbiguityScoringWorkflow.cs`, `ClarifyingQuestionsWorkflow.cs`, `DesignProposalWorkflow.cs`, `PlanGenerationWorkflow.cs`, `TaskCreationWorkflow.cs`, `TestCaseCreationWorkflow.cs`, `BlockerDiagnosisWorkflow.cs`, `DebuggingWorkflow.cs`
  - Review/verdict workflows: `PlanReviewWorkflow.cs`, `TaskReviewWorkflow.cs`, `CodeReviewWorkflow.cs`, `ReviewFixWorkflow.cs`, `TriagePanelReviewWorkflow.cs`, `TriagePODecisionWorkflow.cs`
  - Orchestrating workflows: `AdlOrchestratorWorkflow.cs`, `SingleIssueCycleWorkflow.cs`, `TriageItemCycleWorkflow.cs`, `AssessmentWorkflow.cs`
  - Mechanical/infra workflows (audited to prove they are *out* of document scope): `BranchCreationWorkflow.cs`, `MergeWorkflow.cs`, `PullRequestWorkflow.cs`, `DeploymentPipelineWorkflow.cs`, `TddWorkflow.cs`, `TddWithDebugRetryWorkflow.cs`, `CiWithDebugRetryWorkflow.cs`, `TestingWorkflow.cs`, `MergeApprovalWorkflow.cs`, `MentorshipWorkflow.cs`, `ContextGatheringWorkflow.cs`, `TriageContextGatheringWorkflow.cs`, `UpdateIssueStatusWorkflow.cs`, `LlmCallWorkflow.cs`, tenant-lifecycle workflows (`CreateTenantWorkflow.cs`, `DeleteTenantWorkflow.cs`, `CleanUpFailedTenantWorkflow.cs`, `RotateSecretWorkflow.cs`), analytics rollup workflows
- The fail-closed parsers that define today's informal shapes: `apps/tamma-elsa/src/Tamma.Activities/Decomposition/DecompositionParsing.cs`, `Research/ResearchParsing.cs`, `Ambiguity/AmbiguityParsing.cs`, `Clarify/ClarifyParsing.cs`, `Design/DesignParsing.cs`
- The verdict/aggregation helpers: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/` (`ReviewAggregationHelper.cs`, `PlanValidationHelper.cs`, `ValidationFeedbackHelper.cs`, `TriagePoDecisionHelper.cs`, `TriagePanelAggregationHelper.cs`, `TriageItemCycleHelper.cs`)
- The existing enumeration machinery to reuse (do NOT hand-walk dispatches): `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs` and the `TaxonomyDriftBuildTests.EnumerateAllDispatchPairs` reflection it reuses — this already enumerates every `(role, action)` pair each workflow dispatches via `llm-call`
- The existing resumability positive examples: `apps/tamma-elsa/src/Tamma.ElsaServer/Endpoints/DesignResumeEndpoint.cs`, `ClarifyResumeEndpoint.cs`, `MergeApprovalResumeEndpoint.cs`, `DeploymentApprovalResumeEndpoint.cs`, `BlockerResumeEndpoint.cs` — workflows served by these are the bookmark-suspend baseline the audit measures everything else against

## Acceptance Criteria

1. **Complete workflow inventory.** The audit document contains one row per workflow file in `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/` (excluding pure scheduler/trigger shims, which are listed once in an explicit exclusion note with a one-line reason each). No workflow is silently missing — the row count is stated and reconciled against an `ls` of the directory.

2. **Consumes/produces columns with today's informal shapes.** Every row states what the workflow consumes (inputs: issue payload, prior workflow output JSON, config) and what it produces (output: JSON shape, markdown, git side-effects, events), naming the *actual* informal shape — e.g. "produces decomposition JSON parsed by `DecompositionParsing.Parse` (`tasks[]` with `id`/`dependsOn`/`estimate`)" — with a file/line pointer to the parser or inline parse site for each.

3. **Decision-point column.** Every row states how the workflow currently decides success: which parser or helper (e.g. `ReviewAggregationHelper.ParseRoleVerdict`, `TriagePoDecisionHelper`, inline `TryGetProperty("verdict")` in `TaskReviewWorkflow.cs`), what happens on parse failure (fail-closed dead-end, pessimistic default, silent fallback), and who — if anyone — reviews the content beyond schema/parse success.

4. **Review-loop column.** Every row states whether a review-with-notes/revise loop exists (`PlanReviewWorkflow`'s discussion/revision rounds being the known positive case), is absent, or is a one-shot verdict with no revision path. The three forked review/verdict shapes (legacy string verdict, `PlanReview` object verdict, code-review result) are each identified with file pointers, feeding 39-4's unified `Review` type.

5. **Resumability column.** Every row is classified as one of: (a) suspends on a bookmark with a working resume endpoint (cite which of the five `*ResumeEndpoint.cs` files), (b) resumable in principle via Elsa persistence but never exercised, (c) restarts from scratch on any stop/crash, or (d) not applicable (stateless/mechanical). Evidence (bookmark name, suspend activity, or its absence) is cited per row.

6. **Gap classification.** Every identified gap is tagged with one of the epic's gap classes — `UNTYPED_OUTPUT`, `PARSE_OK_IS_ACCEPT` (no content review), `NO_REVISE_LOOP`, `FORKED_REVIEW_SHAPE`, `NOT_RESUMABLE`, `BARE_FAILURE_ESCALATION` (fails without lineage) — and mapped to the Epic 39 story that closes it (39-2..39-16 per the README story table).

7. **Document-type coverage check.** The audit cross-checks the README's 10-type document table (Findings, AmbiguityAssessment, Clarification, Decomposition, Plan, Design, Review, TriageDecision, Diagnosis, TestSpec) against reality: every producer found in the audit maps to exactly one document type, or is explicitly recorded as out of scope (code, prose/tech-writer output, mechanical side-effects) per the README's "Code is NOT a document type" / "Prose stays prose" principles. Any producer that fits no type is flagged as an open question for 39-3/39-4 — not quietly dropped.

8. **Consumer edges captured.** For each produced shape, the audit lists its downstream consumer(s) (e.g. `SingleIssueCycleWorkflow` consuming decomposition output; `AdlOrchestratorWorkflow` consuming assessment outputs), so 39-2's `consumes:[X]/produces:Y` declarations and the graph-walking build test have a verified edge list to start from.

9. **Findings document lands in the knowledge base.** The deliverable is committed as `.dev/findings/epic-39-workflow-io-lifecycle-audit.md` (or, if the team prefers, in this story directory with a pointer file in `.dev/findings/`), follows the `.dev/templates/` findings format, and is linked from the Epic 39 README's story table row for 39-1.

## Technical Notes

- **Reuse the reflection, verify by reading.** `TaxonomyDriftBuildTests.EnumerateAllDispatchPairs` (referenced from `ContractBindingTests.cs`) already walks compiled workflow graphs for every `llm-call` dispatch — use it to seed the producer list, then read each workflow to fill the non-LLM columns (side-effects, suspend points, consumers). Do not hand-maintain what the reflection already yields.
- **The audit is descriptive, not prescriptive.** Record what the code does today, including ugly truths (pessimistic `"concerns"` defaults, laundered failures that `TriagePoDecisionHelper` documents fixing). The gap map is where prescriptions live, and each prescription is a story pointer, not a fix in this story.
- **No code changes.** This story modifies zero `.cs` files. If the audit finds a live bug (not a design gap), file it in `.dev/bugs/` separately rather than folding it into this deliverable.
- Suggested table shape (one row per workflow): `Workflow | Consumes | Produces (informal shape + parser) | Decision point | Review loop | Resumability | Gap tags | Closed by`.

## Dependencies

- **Prerequisite:** PR #475 substrate — the file-backed prompt registry (`apps/tamma-elsa/src/Tamma.Api/Prompts/{role}/{action}.md`, `PromptFileLoader`), the one-cell-one-contract taxonomy, and `ContractBindingTests` — must be on the branch, since the audit leans on its enumeration and cites its binding map.
- **Feeds:** 39-2 (interface declarations + registry seeded from the consumer edges), 39-3/39-4 (type definitions grounded in the informal shapes + parser pointers), 39-5 (decision-point inventory), 39-6/39-7 (review-loop inventory), 39-8/39-10 (resumability classification).

## Estimated Effort

3–4 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-19 | 1.0.0   | Initial story creation | Claude |
