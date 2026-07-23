# Story 41-17: Standalone Code Review & PR Triage Workflow

Status: drafted

## User Story

As an **engineer / senior developer**, I want first-class code review of an arbitrary diff and a routed
PR-triage queue — independent of the mentorship engine — so that any PR (human- or agent-authored,
inside or outside the autonomous loop) gets a typed `Review` and open PRs are prioritised and assigned,
instead of code review only existing bolted into `code-review`/`mentorship`.

## Priority

P0 / Wave 1 — code review is the most universal team activity and today has no stand-alone lifecycle
workflow.

## Scope

Two thin bindings sharing this story:
- **Code review:** `document-lifecycle`, `consumes: [diff/PR, Plan?, AcceptanceCriteria?]` /
  `produces: Review`. Produce cell `(senior_developer, code-review)` (developer/security/tester lenses
  available via panel policy). Subject is a diff — *code is not a document type* (Epic 39), so the review
  subject is a git reference, not a stored code doc.
- **PR triage:** scheduled (`HourlyAnalyticsRollupScheduler` pattern) sweep of open PRs →
  `document-lifecycle`, `produces: TriageDecision` per PR (priority, staleness, needs-review/needs-author,
  suggested reviewer role). Produce cell `(senior_developer, triage-pr)` (41-1).

## Produced documents

`Review` (per diff) and `TriageDecision` (per open PR, with closed-enum classification + reasoning).

## Events

`CODE_REVIEW.STARTED`/`.VERDICT`; `PR_TRIAGE.SWEEP.STARTED`/`.ITEM`/`.COMPLETED` alongside `DOCUMENT.*`,
tagged `prId`/`repository`.

## Orchestrator / user interaction

Review verdict + each PR-triage decision route through the accept gate; the orchestrator assigns a
reviewer/author-follow-up to the appropriate tenant role's Task View, or self-decides at high autonomy.

## Autonomy behavior

- **70–84:** agent drafts the review/triage; a human reviewer signs off.
- **85–94:** agent review accepted for non-blocking verdicts; blocking issues escalate.
- **95–100:** agent review self-accepted; PR-triage assignments made automatically within the eligible set.

## Acceptance Criteria

1. Code-review binding produces a validated unified `Review` over a diff subject; blocking issues ⇒ not
   approvable; no launder-to-concerns path (the `PlanReviewWorkflow.ExtractReview` anti-pattern stays dead).
2. PR-triage sweep is idempotent, fail-closed per item (a failed item is recorded, not dropped), and
   tenant-scoped.
3. Both declare resume behavior (review `Both`; sweep `LatestStateReEntry`) and pass 39-10 without allowlist.
4. Reviewer-role selection comes from acceptance/review rules, not hardcoded.

## Dependencies

- **Blocking:** Epic 39 (`Review`, `TriageDecision`, lifecycle, review producers, task routing).
- **Related:** reuses 39-7 panel; complements `review-fix`.

## Estimated Effort

5–6 days
