# Story 39-4: Document Types Batch 2 — Plan, Design, Review (unified), TriageDecision, Diagnosis, TestSpec

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

As a **platform developer completing the typed-document vocabulary**,
I want **the remaining six document types — `Plan`, `Design`, the unified `Review`, `TriageDecision`, `Diagnosis`, `TestSpec` — implemented as records + domain validators + contract renderers + examples**,
So that every producer the 39-1 audit mapped has a first-class type, and — critically — the **three forked review/verdict shapes collapse into one `Review` document with a subject reference**, making the "verdict parsed one way here, another way there" class of bug structurally unrepresentable.

## Priority

P0 — `Review` is the keystone of the whole epic: 39-6's lifecycle emits a `Review` for every reviewed document, 39-7's single-reviewer and panel producers write it, and 39-5's acceptance policy reads its decision. The other five types unblock the planning-family (39-14) and remaining-producer (39-15) migrations. Until `Review` exists, the lifecycle has no typed way to say "concerns, with notes."

## Architectural Context (READ FIRST)

Types land in `apps/tamma-elsa/src/Tamma.Core/Documents/Types/` implementing the 39-2 contract, same layout as 39-3.

**`Review` unifies three forked shapes today — read all three before designing it:**

1. **Legacy string verdict + PlanReview object verdict**, both parsed by `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReviewAggregationHelper.cs` (`ParseRoleVerdict` accepts `{"verdict":"approve",...}` AND `{"verdict":{"decision":"APPROVE|REQUEST_CHANGES|NEEDS_DISCUSSION","summary":...,"blockingIssues":[...]}}`, with a pessimistic `"concerns"` default on parse failure). The object form is specified in the prompt template `apps/tamma-elsa/src/Tamma.Api/Prompts/architect/plan-review.md` (and sibling cells: `senior_developer/plan-review.md`, `security/plan-review-security.md`, `product_owner/review-scope.md`, `developer/review-feasibility.md`, `tester/review-testability.md`, `devops/review-operability.md`).
2. **`TaskReviewWorkflow`'s inline parse** — `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskReviewWorkflow.cs` (~line 148) hand-parses `verdict` with its own `"concerns"` fallback: a second, divergent copy of the same idea.
3. **The code-review contract** — the code-review result family in `apps/tamma-elsa/src/Tamma.Activities/Review/` (`BuildCodeReviewResultActivity.cs`, `Models/`, `EscalateReviewActivity.cs`) plus the `code-review-*` prompt cells (e.g. `Prompts/architect/code-review-architecture.md`), consumed by `CodeReviewWorkflow.cs` / `ReviewFixWorkflow.cs`.

**Baselines for the other five types:**

- `Plan` — `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanGenerationWorkflow.cs` + `TaskCreationWorkflow.cs`, with today's deterministic rules in `Workflows/Helpers/PlanValidationHelper.cs` and its feedback loop in `ValidationFeedbackHelper.cs`
- `Design` — `apps/tamma-elsa/src/Tamma.Activities/Design/DesignParsing.cs` consumed by `DesignProposalWorkflow.cs`
- `TriageDecision` — `Workflows/Helpers/TriagePoDecisionHelper.cs` (fail-closed vocabulary clamping, `llm-failed`/`unparsed` markers) + `TriagePanelAggregationHelper.cs`, consumed by `TriagePODecisionWorkflow.cs` / `TriagePanelReviewWorkflow.cs` / `IssueTriageWorkflow.cs`
- `Diagnosis` — the debug report family in `apps/tamma-elsa/src/Tamma.Activities/Debug/` (`AIDiagnosisActivity.cs`, `SelectHypothesisActivity.cs`, `RefineHypothesisActivity.cs`, `CompileDebugReportActivity.cs`, `Models/`), consumed by `DebuggingWorkflow.cs` / `BlockerDiagnosisWorkflow.cs`
- `TestSpec` — `TestCaseCreationWorkflow.cs` and the TDD write-tests path (`apps/tamma-elsa/src/Tamma.Activities/TDD/WriteTestsActivity.cs`, `Models/`; parsing behavior pinned in `tests/Tamma.Activities.Tests/TDD/ResponseParsingTests.cs`)

Domain rules per type come from the Epic 39 README document-type table, restated below. Per the README: **code is NOT a document type** — a `Review` may have a *subject* that is a diff, but the diff itself gets no schema.

## Acceptance Criteria

1. **Six types registered.** `Plan`, `Design`, `Review`, `TriageDecision`, `Diagnosis`, `TestSpec` each ship record + `IDocumentType` implementation (`Validate`/`RenderContract`/`Examples`) + registration; the registry count pin is consciously bumped by exactly 6 (vocabulary complete at 10 per the README table), and all 39-2 drift tests pass.

2. **Review: one type, subject-referenced.** `Review.Subject` is a typed reference — either a document reference (`documentId` + `documentType`) or a diff/PR reference (repo + PR/commit identifiers) — so one shape serves plan review, task review, and code review. The payload carries: `decision` as a closed enum (single spelling set, e.g. `Approve | RequestChanges | NeedsDiscussion`, with documented mapping from ALL legacy spellings both `ReviewAggregationHelper.ParseRoleVerdict` shapes accept); `summary`; `issues[]` each carrying **severity + category + a concrete suggested fix**; and reviewer provenance (role, via the envelope's `ProducedBy`).

3. **Blocking-issues ⇒ not-approvable rule.** `Review.Validate` rejects any payload whose `decision` is approve while `issues[]` contains a blocking-severity issue — the domain-phrased violation names the blocking issues. This is the epic's flagship "executable domain rule": the state that caused the forked-verdict bug class becomes unrepresentable as a valid document.

4. **Plan domain rules.** `Validate` enforces: a file map per task (each task names the files it touches); task dependencies resolvable within the plan (no dangling/cyclic references — reuse the graph-check approach from 39-3's `Decomposition`); testing stated per task (non-empty test approach). The rules subsume what `PlanValidationHelper` deterministically checks today — feed its current checks in as the baseline the validator must not regress.

5. **Design domain rules.** `Validate` enforces: ≥1 alternative, each with trade-offs stated; a recommendation that references one of the listed alternatives by ID (a recommendation naming no listed alternative is a violation). Subsumes `DesignParsing`'s fail-closed rules.

6. **TriageDecision domain rules.** Every classification field (`priority`, `type`, `complexity`, `automation`) is a **closed enum** matching the Story 26-1 vocabulary that `TriagePoDecisionHelper` clamps to today; out-of-vocab values are violations (not silent clamps — the clamp-and-flag behavior moves to the repair/review layer where it is visible); `reasoning` is required non-empty. The helper's honest-failure distinctions (`llm-failed`, `unparsed`) remain representable as *lifecycle outcomes*, not as fake clean decisions.

7. **Diagnosis and TestSpec domain rules.** `Diagnosis`: hypotheses ranked by confidence (confidence ∈ [0,1], no duplicate ranks); the proposed fix references affected files. `TestSpec`: each case bound to a task ID; one behavior per case (single expected behavior statement); duplicate case-per-behavior collisions flagged.

8. **Fail-closed subsumption + round-trip.** For each type with an existing parser/helper baseline, the negative cases those baselines reject are rejected by the new validator (violation, never default), and existing passing fixtures round-trip through the typed payload back to JSON the old consumer still parses — same discipline as 39-3 AC6/AC7. For `Review` specifically, both legacy verdict shapes deserialize into the unified type, and a test table pins the decision mapping (`"approve"`→`Approve`, `"APPROVE"`→`Approve`, `"REQUEST_CHANGES"`→`RequestChanges`, `"NEEDS_DISCUSSION"`→`NeedsDiscussion`, parse-failure → *no document* + typed error, never a defaulted `"concerns"` document).

9. **No consumer rewiring.** `ReviewAggregationHelper`, `TaskReviewWorkflow`, the code-review activities, and all other baselines stay untouched (migrations are 39-14/39-15; panel machinery absorption is 39-7). Diff surface: `Tamma.Core/Documents/**`, `Tamma.Core.Tests/**` only.

## Technical Notes

- **The pessimistic-default question is settled by the lifecycle, not the type.** Today parse failure launders into `"concerns"`. Under Epic 39, unparseable output is a *validation failure* → repair ring (39-9) → `ValidationExhausted` outcome (39-6) — a `Review` document is only ever a successfully validated one. Do not encode a "default decision" into the type.
- Severity for `Review.issues[]` needs a closed enum with a defined blocking threshold (e.g. `Blocker | Major | Minor | Nit`, blocking = `Blocker`); AC3 hangs off it. Pick the vocabulary by surveying what the three baselines emit today (the `blockingIssues[]` array in the PlanReview object verdict is the strongest prior).
- `Review` aggregation (quorum, panel math) is deliberately NOT in this story — the type models a *single reviewer's* review; 39-7 expresses aggregation over sets of `Review` documents.
- `TriageDecision`'s closed enums should be defined once here and referenced by the (future) migrated triage prompts — check `Prompts/product_owner/` triage cells against the vocabulary before finalizing spellings.
- Illustrative signatures only in the story; no implementation code.

## Dependencies

- **Prerequisite:** 39-2 (core contracts, registry), 39-1 (audit: confirms the three review shapes and the six baselines' consumers). 39-3 establishes the subsumption/round-trip test pattern this story copies.
- **Feeds:** 39-5 (policy reads `Review.decision`), 39-6 (lifecycle produces/consumes `Review`), 39-7 (review producers + panel aggregation over `Review`), 39-14 (planning family migration onto unified `Review`), 39-15 (triage/testspec/diagnosis migrations), 39-16 (contract generation).

## Estimated Effort

5–6 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-19 | 1.0.0   | Initial story creation | Claude |
