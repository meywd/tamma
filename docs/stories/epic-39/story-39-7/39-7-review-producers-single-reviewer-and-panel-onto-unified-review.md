# Story 39-7: Review Producers — single reviewer + panel onto the unified Review type

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

As a **consumer of the document lifecycle (39-6's REVIEW step, and any workflow needing a verdict on a document or diff)**,
I want **reusable review-producer steps — a single-reviewer producer and a panel producer — that dispatch reviewer roles selected from policy, produce validated unified `Review` documents, and express quorum/aggregation as rules over sets of `Review` documents**,
So that PlanReview's proven panel machinery becomes the platform's one way to review anything, every reviewer's output is a typed `Review` (no forked verdict shapes), and "how many reviewers, which roles, what counts as approval" is policy — not code copied per workflow.

## Priority

P0 — The 39-6 lifecycle's REVIEW step is a hole without this. It also carries the absorption mandate: the Epic 39 README names PlanReviewWorkflow's bespoke discussion/revision loop as superseded by 39-6/39-7/39-8, with the one-off retired in 39-14 — this story builds the generic replacement the retirement depends on.

## Architectural Context (READ FIRST)

New workflows land beside their siblings in `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/` (e.g. `SingleReviewerWorkflow.cs`, `PanelReviewWorkflow.cs` — or one `ReviewProducerWorkflow` with a mode input; decided in design), with aggregation logic in pure helpers under `Workflows/Helpers/`.

**The machinery being absorbed — read all of it first:**

- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanReviewWorkflow.cs` — today's multi-role panel: parallel role dispatches via `llm-call`, verdict parsing, aggregation, discussion rounds
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/ReviewAggregationHelper.cs` — `ParseRoleVerdict` (both legacy verdict shapes, pessimistic `"concerns"` default) and `AggregateVerdicts` (the current quorum math). Its parsing half is superseded by the 39-4 `Review` validator; its aggregation half is the behavioral baseline the new aggregation rules must subsume
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/TriagePanelAggregationHelper.cs` — the second panel-aggregation fork (triage panel), evidence for what the generic aggregation must express before 39-15 migrates it
- The reviewer prompt cells (per-role review contracts, all rendering verdicts today): `apps/tamma-elsa/src/Tamma.Api/Prompts/architect/plan-review.md`, `senior_developer/plan-review.md`, `security/plan-review-security.md`, `product_owner/review-scope.md`, `product_owner/review-acceptance.md`, `developer/review-feasibility.md`, `tester/review-testability.md`, `devops/review-operability.md`, plus the `code-review-*` cells
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TriagePanelReviewWorkflow.cs` — the sibling panel pattern (fan-out shape reference; migrated later in 39-15)

**Contracts consumed:** the 39-4 unified `Review` type (subject reference, closed decision enum, blocking-issues⇒not-approvable); 39-5 policy (reviewer-role selection, panel composition, quorum); the `llm-call` mediation invariant (`LlmCallWorkflow.cs`, `DefinitionId = "llm-call"` — reviewer dispatches are `(role, action)` pairs subject to `ContractBindingTests`' coverage guard); 39-2 registry (the reviewed document's type key drives which reviewer actions are eligible).

## Acceptance Criteria

1. **Single-reviewer producer.** A dispatchable workflow that, given a subject (document reference or diff reference), a reviewer `(role, action)` selected from the effective 39-5 policy, and lineage anchors, dispatches one `llm-call` and yields exactly one **validated** `Review` envelope (39-4 `Validate` applied; blocking-issues⇒not-approvable enforced). Unparseable/invalid reviewer output is NOT laundered into a defaulted `"concerns"` review — it surfaces through the bounded validation/repair seam and, on exhaustion, as a typed failure the caller (39-6) maps to `ReviewUndecidable`/`ValidationExhausted`.

2. **Panel producer.** A dispatchable workflow that fans out N single-reviewer runs (roles/panel composition from policy — the PlanReview role set as the canonical example), collects the individual `Review` documents (each persisted with lineage — individual reviews are never discarded post-aggregation), and yields an **aggregate `Review`** whose provenance marks it as an aggregation referencing its member review ids.

3. **Quorum/aggregation rules operate on `Review` documents.** Aggregation is a pure helper over `IReadOnlyList<Review>` (no JSON re-parsing — the fork `ReviewAggregationHelper.ParseRoleVerdict` exists to serve dies here): configurable quorum (e.g. unanimous / majority / any-blocker-blocks), with the invariant that **any member review with blocking issues forces the aggregate decision to non-approve** regardless of quorum math. The default configuration reproduces `AggregateVerdicts`' current behavior (documented mapping, divergences listed deliberately), so 39-14's migration is behavior-preserving by default.

4. **Reviewer selection from policy, validated against the taxonomy.** Reviewer `(role, action)` pairs come from the 39-5 policy (per document type), are validated against `AgentRole`/`AgentAction` wire values and the `RolePhaseMap`, and every pair the producers can dispatch is bound or allowlisted in `ContractBindingTests` — a new reviewer cell without a contract binding fails the build, per the existing coverage guard.

5. **Both subject kinds exercised.** Tests cover a document subject (e.g. a `Plan` envelope) and a diff subject (repo + PR reference, the code-review case) through the single-reviewer producer — proving the unified `Review` actually spans the three previously forked shapes.

6. **Undecidable panels surface, never resolve silently.** A panel that cannot reach quorum under the configured rule (e.g. split with discussion disabled or exhausted) yields a typed undecidable result carrying all member reviews — the 39-6 caller maps it to `ReviewUndecidable` and escalates with lineage. No pessimistic-default aggregate is fabricated.

7. **Events.** Producer runs emit the 39-6 `DOCUMENT.*` family consistently for the `Review` documents they produce (`DOCUMENT.PRODUCED.*`/`DOCUMENT.VALIDATED.*` with `documentType=review`), plus panel-specific markers (e.g. `DOCUMENT.REVIEW_PANEL_STARTED`/`_COMPLETED` with member counts) — constants in the same `DocumentEvents.cs`, tagged with `issueId` + subject reference.

8. **Behavioral parity harness against PlanReview.** A test replays the recorded verdict fixtures used by the existing `ReviewAggregationHelper` tests through the new pipeline (legacy JSON → 39-4 `Review` mapping → new aggregation) and asserts the aggregate decision matches today's `AggregateVerdicts` outcome for every fixture — the evidence 39-14 needs to retire the bespoke loop safely.

9. **No caller migrated.** `PlanReviewWorkflow` and the triage panel remain untouched (retirement is 39-14/39-15). Diff surface: new producer workflows + helpers + tests + `WorkflowVersions.cs` registration.

## Technical Notes

- **Aggregation is not averaging.** Survey what `AggregateVerdicts` and `TriagePanelAggregationHelper` actually do (pessimistic dominance, role weighting if any) before choosing the quorum vocabulary; the configured rule set must express both existing behaviors or the gap is documented for 39-15.
- Panel fan-out should reuse the sibling fan-out idiom from `PlanReviewWorkflow`/`TriagePanelReviewWorkflow` rather than inventing new parallel-dispatch plumbing.
- The discussion/refine loop between panel rounds stays OUT of this story: revision-with-notes belongs to the 39-6 lifecycle (the reviewed *producer* revises, not the reviewers). If a use case genuinely needs reviewer-to-reviewer discussion rounds, record it as a finding for 39-14 rather than building it speculatively.
- Reviewer prompt cells will eventually render their contract from the `Review` type (39-16); until then, keep `RenderContract()`'s tokens aligned with the existing cell templates so the binding map stays green.

## Dependencies

- **Prerequisite:** 39-4 (unified `Review`), 39-5 (reviewer selection/panel/quorum policy), 39-2 (envelope/registry).
- **Lockstep:** 39-6 (REVIEW step contract — agree the definition ids and outcome mapping early).
- **Feeds:** 39-14 (PlanGeneration + PlanReview migration retires the bespoke panel), 39-15 (triage panel migration), 39-8 (undecidable results escalate with member-review lineage).

## Estimated Effort

4–6 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-19 | 1.0.0   | Initial story creation | Claude |
