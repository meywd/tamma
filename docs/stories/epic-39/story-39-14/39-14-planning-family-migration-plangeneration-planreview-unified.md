# Story 39-14: Planning Family Migration — PlanGeneration + PlanReview onto the Unified Review

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

As the **orchestrator running the planning phase** of an issue,
I want PlanGeneration and PlanReview rebuilt on `DocumentLifecycleWorkflow` — a typed `Plan` document reviewed via the unified `Review` type, with the bespoke validation-feedback retry loop subsumed by the lifecycle and the panel's discussion rounds expressed as lifecycle revise rounds,
So that plan quality flows through the same produce/validate/review/revise/accept loop as every other document, and the class of bug where a reviewer's verdict shape forks from its consumer becomes impossible by construction.

## Priority

P1 — The planning family carries the platform's only existing review-with-notes loop (PlanReview's discussion/revision rounds), which the generic lifecycle absorbed (epic README "Supersedes"). Migrating it retires the largest bespoke quality loop in the codebase and puts the unified `Review` document through its hardest real test (panel aggregation).

## Architectural Context (READ FIRST)

**The workflows being migrated (both `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/`):**
- `PlanGenerationWorkflow.cs` → produces `Plan` (39-4: file map per task; dependencies resolvable; testing stated per task). **The bespoke retry loop to retire:** on validation failure it sets a `ValidationErrors` variable (line ~136) and loops back into the produce dispatch, folding the errors into the prompt via `Helpers/ValidationFeedbackHelper.AppendFeedback` (line ~102-106). Note the history: the ORIGINAL `{{validationErrors}}` dispatch key was undeclared in the template and **silently dropped at render** — the model never saw the feedback and retried blind. That was already fixed to render (see `ValidationFeedbackHelper.cs`'s doc comment); this story now **subsumes** the whole hand-rolled loop under the lifecycle's validate → repair/revise rings, and deletes the workflow-local retry plumbing (`ValidationErrors` variable, loop-back edge, `OutErr` terminal at line ~177).
- `PlanReviewWorkflow.cs` → today a separate workflow producing an informal verdict; becomes the **review stage of the Plan lifecycle**, producing a typed `Review` document (39-4: subject reference; issues carry severity+category+fix; decision enum; blocking issues ⇒ not approvable). Its **panel discussion rounds become lifecycle revise rounds**: each round = Review documents produced (per panel member), aggregated (39-7), notes fed to a revise turn, bounded by the lifecycle round cap.
- Parsing/verdict helpers: `PlanValidationHelper` (named in the `ContractBindingTests` binding map) plus the inline validators in `PlanGenerationWorkflow`; decision logic pinned today by `tests/Tamma.Activities.Tests/Workflows/PlanReviewDecisionTests.cs`.
- Prompt cells kept: `apps/tamma-elsa/src/Tamma.Api/Prompts/architect/plan-review.md`, and the plan-producing cells (`developer/plan-implementation.md`, `architect/plan-system-design.md`, etc. — the exact produce cell(s) per today's dispatch, unchanged by this story).

**Why "verdict forks impossible by construction":** today PlanReview's verdict is one of the three forked review/verdict shapes in the codebase (epic README). Its consumer decodes the shape by hand; a template or parser drift forks them silently. After migration, the reviewer produces a typed `Review` whose `decision` is a closed enum and whose domain rule "blocking issues ⇒ not approvable" is executable — the consumer reads the type, so there is no second shape to fork.

## Acceptance Criteria

1. **Plan lifecycle binding.** PlanGeneration is re-implemented as a `DocumentLifecycleWorkflow` binding declaring `consumes: [Decomposition]` / `produces: Plan` (consuming the accepted `Decomposition` from the 39-11 store — the 39-12 pilot's output is its input). The bespoke validation-retry loop, `ValidationErrors` loop-back plumbing, and the `OutErr` error terminal are deleted; validation failure flows through the lifecycle rings (39-9 repair if gated on, else review/escalation) and exits at worst as typed `ValidationExhausted` with lineage.

2. **PlanReview becomes the review stage.** `PlanReviewWorkflow` no longer exists as an independent produce-verdict pipeline: plan review runs as the Plan lifecycle's review stage via the 39-7 review producers (single reviewer or panel per policy), emitting typed `Review` documents persisted to the store with subject references to the exact `Plan` revision reviewed.

3. **Panel rounds = revise rounds.** The panel discussion/revision loop is expressed entirely in lifecycle terms: round N = panel `Review` documents → 39-7 aggregation → concerns-as-notes → revise turn producing `Plan` revision N+1 → re-review. Rounds are bounded by lifecycle config (39-5/39-6); exhaustion exits as typed `RoundsExhausted` with the full round history in lineage. No bespoke round counter or discussion state survives in workflow code.

4. **Blocking-issue rule enforced by the type.** A `Review` carrying blocking-severity issues cannot be an approval (39-4 domain rule). A test attempts to accept a Plan on a blocking-issue review and asserts the lifecycle refuses at the accept gate — the decision comes from the executable rule, not from workflow branch logic. `PlanReviewDecisionTests` is ported to assert decisions through the typed `Review` (same behavioral cases, new mechanism).

5. **Verdict-fork regression pinned.** A test demonstrates the by-construction guarantee: the review stage's output and the accept gate's input are the same static `Review` type (compile-time), and the old free-form verdict parsing path no longer exists in the compiled graphs (structure test asserts no parser-backed verdict dispatch remains for the planning family).

6. **Events preserved alongside `DOCUMENT.*`.** The planning family's existing event vocabulary (plan generation/review event types currently emitted by the two workflows) continues at equivalent transitions alongside `DOCUMENT.*`; a replay test asserts both families with matching `issueId` tags and that the round count is reconstructable from events.

7. **Resumable per the standard.** Both bindings declare resume behavior and pass the 39-10 structural test without allowlist entries (allowlist shrinks by two). Crash-re-entry works mid-round: a crash after round-2 reviews are persisted re-enters at the round-2 revise step — no re-review of already-reviewed revisions, no duplicate `Review` documents (asserted by an integration test).

8. **Test migration, none skipped.** Planning-family structure tests are rewritten against the lifecycle bindings; `ValidationFeedbackHelper` either retires (feedback rendering is now the lifecycle/repair ring's job) or reduces to the shared helper the lifecycle uses, with its doc-commented render lesson preserved wherever the logic lands. Full `dotnet test` passes with no planning test disabled.

## Technical Notes

- **The render-drop lesson must not regress.** Whatever mechanism feeds validator/reviewer notes back into a revise turn MUST have a test proving the notes actually reach the rendered prompt (the `{{validationErrors}}` silent-drop bug class). The lifecycle's revise-notes path should carry a template-render assertion equivalent to what `ValidationFeedbackHelper`'s fix established.
- **Aggregation semantics live in 39-7,** not here: how a split panel resolves (majority, veto-on-blocking, `ReviewUndecidable` on deadlock) is the review producer's policy. This story only asserts the planning family consumes it; if panel aggregation needs a change, patch 39-7 with its tests.
- **`consumes: [Decomposition]` is real, not decorative** — the build-time graph test (39-1/39-6 declaration checking) must see Plan's consumption typed against Decomposition so the producer/consumer pair type-checks. This is the first two-link chain in the migrated graph.
- **Round budget defaults:** carry over today's effective PlanReview round limit as the lifecycle default for `Plan` (do not silently change quality/cost behavior in the same story as the mechanism swap).
- Keep the workflow definition ids/dispatch names stable as in 39-12 so orchestrator call sites are untouched.

## Dependencies

- **Blocking:** 39-12 (pilot + accepted `Decomposition` as input), 39-4 (`Plan` + unified `Review` types), 39-5 (round/acceptance policy), 39-6 (lifecycle revise rounds), 39-7 (single/panel review producers + aggregation), 39-10/39-11 (resume, store).
- **Optional:** 39-9 (repair ring) for plan validation failures — gated by data, off by default.
- **Unblocks:** 39-15 (last migration wave), full Issue → Decomposition → Plan → Reviews lineage for dashboards and Stories 2-15/2-16 consumers.

## Estimated Effort

5–7 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-19 | 1.0.0   | Initial story creation | Claude |
