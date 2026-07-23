# Story 41-16: Regression & Flaky-Test Management Workflow

Status: drafted

## User Story

As a **tester** (or eligible role-holder), I want a scheduled workflow that mines CI history + the DCB
stream for repeated failures and non-deterministic tests, triages them, and drafts a regression `TestSpec`
for genuine regressions, so that flaky tests are quarantined and regressions are captured instead of
silently eroding the gate.

## Priority

P1 / Wave 2 — protects the quality gate that everything else leans on; recurring and event-sourced.

## Scope

Scheduled sweep → thin binding over `document-lifecycle`. `consumes: [CI run history, GATE.*/TEST.* DCB
events]` / `produces: TriageDecision` (per suspect test: regression | flaky | environmental) and, for a
confirmed regression, a follow-on `produces: TestSpec` (a bound regression case). Produce cells
`(tester, manage-regression)` (41-1) and `(tester, write-regression-test)`.

## Produced documents

`TriageDecision` (closed-enum classification + reasoning per suspect test) and, when regression is
confirmed, a `TestSpec` case bound to the affected task/behavior.

## Events

`REGRESSION.SWEEP.STARTED`/`.ITEM`/`.COMPLETED`; `FLAKY.QUARANTINE.PROPOSED` alongside `DOCUMENT.*`.

## Orchestrator / user interaction

Each triage decision routes through the accept gate; a "flaky ⇒ quarantine" or "regression ⇒ add test"
action is assigned to a tester/dev role or self-decided at high autonomy. The regression `TestSpec` can
hand off to the coding step (Epic 40) to land the test.

## Autonomy behavior

- **70–84:** agent triages; a human tester approves quarantine/regression-test actions.
- **85–100:** agent triages and self-accepts classification; quarantine + regression-test creation auto
  -assigned within the eligible set; destructive quarantine of a load-bearing test can be an always
  -escalate class.

## Acceptance Criteria

1. Scheduled, tenant-scoped, idempotent per window; fail-closed per suspect (recorded, not dropped).
2. Classification uses closed enums with required reasoning; no false "flaky" that hides a real regression.
3. Confirmed regression yields a validated `TestSpec` bound to a task/behavior id.
4. `[ResumeBehavior(LatestStateReEntry)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** 41-1 (`manage-regression` cell), Epic 39 (`TriageDecision`, `TestSpec`, lifecycle, store),
  scheduler pattern; Epic 40 for landing the regression test.

## Estimated Effort

5–6 days
