# Story 41-18: Refactor Planning Workflow

Status: drafted

## User Story

As a **senior developer** (or eligible role-holder), I want a workflow that turns a refactor need (from
41-11 tech-debt triage or a review concern) into a typed `Plan` on the lifecycle, so that refactors are
scoped, sequenced, and accepted with a behavior-preservation strategy before the coding step runs —
instead of an unbounded ad-hoc rewrite.

## Priority

P3 / Wave 3 — turns tech-debt findings into safe, planned work.

## Scope

Thin binding over `document-lifecycle`. `consumes: [tech-debt TriageDecision (41-11), context-scan, Review
concerns]` / `produces: Plan`. Produce cell `(senior_developer, plan-refactor)` — an existing, unbound
cell (`AgentAction.cs`; in `SeniorDeveloper`'s eligible set, `RolePhaseMap.cs:80-92`).

## Produced document

`Plan` (39-4): per-step file map, dependency ordering, behavior-preservation/testing stated per step (the
`refactor` action's characterization-test requirement expressed as plan content). `repository`/`issueId`
lineage. Reviewed via panel.

## Events

`REFACTOR.PLAN.STARTED` → `.DRAFTED` → `.ACCEPTED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Accept gate routes per autonomy; accepted plan hands off to the coding step (Epic 40) step-by-step. A
refactor touching a public API can be an always-escalate class.

## Autonomy behavior

- **70–84:** agent drafts; senior dev accepts before work.
- **85–100:** agent drafts and self-accepts contained refactors; API-affecting refactors always escalate.

## Acceptance Criteria

1. Thin lifecycle binding on `(senior_developer, plan-refactor)`, adding one `ContractBindingTests`
   `Bindings` entry with authority `PlanDocumentType.Validate`.
2. `Plan` validation is exercised by one fixture per rule: no steps ⇒ `EMPTY_PLAN`; a step with no file map
   ⇒ `TASK_MISSING_FILE_MAP`; a step whose `testing` field is empty ⇒ `TASK_MISSING_TESTING`; a
   self-dependent step ⇒ `SELF_DEPENDS_ON`; a cyclic pair ⇒ `CYCLIC_DEPENDS_ON` (`Plan.cs:50-71`).
3. **Behavior preservation is enforced as structure, not as judgement.** Every step's `testing` field must
   name a characterization or regression test that exists before the step runs; a step whose `testing` is
   empty or names no test is rejected by rule (`TASK_MISSING_TESTING` plus a story-local
   `STEP_MISSING_CHARACTERIZATION_TEST`). *Corrected: AC1 previously asserted the `Plan` was "validated
   (… behavior preservation)". `PlanDocumentType` has no behavior-preservation rule (`Plan.cs:47-71`
   lists all nine), so as written the criterion could not fail.*
4. The run records the `documentId` of the 41-11 tech-debt `TriageDecision` it consumed (or `null` when
   triggered from a `Review` concern instead), and fails loud if a referenced id is unreadable.
5. An accepted `Plan` is retrievable by `issueId`/`repository` through 39-11 and is read by a coding-step
   dispatch in an integration test.
6. `[ResumeBehavior(LatestStateReEntry)]` (a thin binding owns no suspend node — the accept gate
   suspends inside the dispatched `document-lifecycle` child); 39-10 structural test green without an
   allowlist entry. A new
   `WorkflowDocumentInterface` row is declared and `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned`
   is bumped in the same change.

> Whether the refactor is *actually* behavior-preserving is not decidable from the plan document — that is
> what the characterization tests in AC3 and the review panel are for. No AC asserts it.

## Dependencies

- **Blocking:** Epic 39 (`Plan`, lifecycle, review-panel, store).
- **Blocking for the execution hand-off only (AC5's downstream, not this workflow):** **Epic 40**.
  *Corrected: this previously read "Epic 40 for execution", which reads as a durability nicety. Epic 40
  ships the missing **execution substrate** — `.github/workflows/tamma-agent.yml` does not exist in this
  repo, so the coding step's dispatch fails loud with `WorkflowNotFound`
  (`AgentDispatchMediationService.cs:109`) today. Producing and accepting the `Plan` has no Epic 40
  dependency; only working the accepted plan does.*
- **Related:** consumes 41-11.

## Estimated Effort

3–4 days
