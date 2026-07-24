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
`(tester, manage-regression)` (**41-1a**) and `(tester, write-regression-test)` (exists today, unbound).

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

1. The sweep is tenant-scoped, fires at most once per window per tenant across a restart (the fired window
   is persisted, not held in memory), and is fail-closed per suspect: a failed suspect emits
   `REGRESSION.SWEEP.ITEM` with the failure and the sweep continues — an integration test kills the
   process mid-sweep and asserts no suspect is double-triaged and none is dropped.
2. Classification is closed-enum: an out-of-vocabulary value ⇒ `OUT_OF_VOCABULARY`, a classification with
   no reasoning ⇒ `REASONING_REQUIRED` (`TriageDecision.cs:146-149`).
3. **A `flaky` classification requires deterministic evidence of non-determinism**: at least one pass and
   at least one failure of the same test at the *same commit sha* inside the sweep window. A `flaky`
   classification without that evidence is rejected by a story-local rule (`FLAKY_WITHOUT_SPLIT_RESULT`)
   and the suspect is re-routed as `regression`/`environmental`. *Corrected: the old criterion read "no
   false 'flaky' that hides a real regression", which asserts a property of the model's judgement and
   cannot fail a test. The commit-sha split-result rule is the falsifiable form of the same intent.*
4. A confirmed regression yields a `TestSpec` that validates: no cases ⇒ `EMPTY_TEST_SPEC`; a case with no
   task id ⇒ `CASE_MISSING_TASK_ID`; no behavior ⇒ `CASE_MISSING_BEHAVIOR`; a task id absent from the
   referenced plan ⇒ `CASE_UNKNOWN_TASK_ID` (`TestSpec.cs:40-57`).
5. `[ResumeBehavior(LatestStateReEntry)]`; 39-10 structural test green without an allowlist entry. New
   `WorkflowDocumentInterface` rows are declared and
   `WorkflowInterfaceGraphTests.Declared_edge_count_is_pinned` is bumped in the same change.

## Dependencies

- **Blocking:**
  - **41-1a** — the `(tester, manage-regression)` cell (absent from `AgentAction.cs` today).
  - Epic 39 (`TriageDecision`, `TestSpec`, lifecycle, store).
  - **The tenant-aware scheduled-trigger seam — unowned; no story writes it.** *Corrected: "scheduler
    pattern" named no artifact. `HourlyAnalyticsRollupScheduler` is not a reusable pattern: it is
    hardcoded to one workflow (`:198-199`), has one `FireAtMinute` int rather than a window/cron shape
    (`:34`), threads no `tenantId` into the dispatch (`:202-203`), keeps its last-fired window in a
    per-process field, and its advisory-lock key has no tenant component
    (`ComputeAdvisoryLockKey(year, dayOfYear, hour)`, `:241`) — one tenant's leader would suppress every
    other tenant's fire for that hour. AC1 is unreachable without the seam.*
- **Blocking for landing the regression test only (not for this workflow):** **Epic 40**. *Corrected: this
  previously read "Epic 40 for landing the regression test" inside the blanket Blocking line. Producing
  the `TriageDecision` and the `TestSpec` has no Epic 40 dependency; committing the test does — Epic 40
  ships the missing execution substrate, since `.github/workflows/tamma-agent.yml` does not exist in this
  repo and the coding step's dispatch fails loud with `WorkflowNotFound`
  (`AgentDispatchMediationService.cs:109`) today.*

## Estimated Effort

5–6 days
