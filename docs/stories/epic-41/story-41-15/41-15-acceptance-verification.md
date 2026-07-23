# Story 41-15: Acceptance Verification Workflow

Status: drafted

## User Story

As a **tester** (or eligible role-holder), I want a workflow that verifies an implemented change against
its accepted `AcceptanceCriteria` and emits a typed `Review` verdict on the lifecycle, so that the cycle
answers *"does this meet the requirement?"* — not just *"do the tests pass?"* — before merge/close.

## Priority

P0 / Wave 1 — closes the loop 41-2 opens. Without it, acceptance criteria are authored but never checked.

## Scope

Thin binding over `document-lifecycle`. `consumes: [AcceptanceCriteria, diff/PR, TestSpec?, CI results]`
/ `produces: Review` (subject = the change; each criterion mapped pass/fail with evidence). Produce cell
`(tester, verify-acceptance)`.

## Produced document

Unified `Review` whose issues carry the failing criterion id + evidence; a blocking failure ⇒ not
approvable (39-4 invariant). Decision enum drives merge routing.

## Events

`ACCEPTANCE.VERIFY.STARTED` → `.VERDICT` (approved/changes/undecidable) alongside `DOCUMENT.*`, tagged
`issueId`/`prId`/`repository`.

## Orchestrator / user interaction

Accept gate routes the verdict per autonomy. A "changes-requested" verdict escalates with lineage
(criteria + failing evidence) so the orchestrator can loop back to `review-fix`/coding or assign a human.

## Autonomy behavior

- **70–84:** a human tester verifies; verdict acceptance is human.
- **85–94:** agent verifies; a human confirms an "approved" verdict on the merge path.
- **95–100:** agent verifies and self-accepts an unambiguous pass; any failing criterion always escalates.

## Acceptance Criteria

1. Reads the latest accepted `AcceptanceCriteria` (41-2) via 39-11; hard-fails loud if none exists (never
   silently "passes" an issue with no criteria).
2. Produces a validated unified `Review`; blocking failures cannot be laundered into approval.
3. Verdict integrates with `single-issue-cycle`/`merge-approval` as a gate input.
4. `[ResumeBehavior(Both)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** 41-2, Epic 39 (`Review`, lifecycle, store), Epic 40 (change under test).
- **Unblocks:** requirement-complete merge gating.

## Estimated Effort

4–5 days
