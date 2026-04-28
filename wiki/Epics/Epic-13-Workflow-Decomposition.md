# Epic 13: Workflow Decomposition

**Status:** Done. All 3 stories landed (13-1, 13-2, 13-3).
**Stories:** 3 (13-1..13-3).
**Primary code:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`, `TddWithDebugRetryWorkflow.cs`, `CiWithDebugRetryWorkflow.cs`.

## Overview

Epic 13 attacks the `SingleIssueCycleWorkflow` size problem. Before the epic, the workflow was 783 lines / 39 activities — a single flowchart where the full issue lifecycle (branch → implement → TDD → CI → review → merge) was inlined. Two retry loops (TDD with debug retry, CI with debug retry) each appeared once inside that flow but contained 5 activities + counter + guard, and seven separate "finish" sequences (success, failure variants, escalations) were copy-pasted with minor variations. The graph was hard to read in ELSA Studio, hard to reason about in the diff, and harder still to test in isolation.

Epic 13 extracts those loops and consolidates the finish sequences. `TddWithDebugRetryWorkflow` (13-1) and `CiWithDebugRetryWorkflow` (13-2) are standalone sub-workflows with typed inputs/outputs — `DispatchWorkflow` from the parent replaces five activities plus the counter with a single box. Story 13-3 collapses the seven finish sequences into one parameterized shared sequence (`FinishSequence` helper / shared tail). Net effect: parent workflow drops to ~500 lines / ~29 activities, each extracted sub-workflow is independently testable, and Studio's graph renders cleanly per workflow.

The extraction is semantics-preserving. Path equivalence was proven by 26 structure tests asserting that every TDD success/failure/retry path, every CI success/failure/retry path, and every finish branch produces the same outcome as the original flow (see commit `b0803486`).

## Architecture

```
Before Epic 13 (SingleIssueCycleWorkflow — 783 lines, 39 activities):

START
  v
... 20 pre-TDD activities ...
  v
+---------------- TDD loop inline -----------------+
| tddCycle                                          |
| tddSuccess? -YES-> ...                            |
| NO                                                |
| tddDebugGuard? -NO-> FINISH(tdd_retry_exhausted)  |
| YES                                               |
| incrementTddDebug                                 |
| dispatchTddDebugging                              |
| loop back to tddCycle                             |
+---------------------------------------------------+
  v
+---------------- CI loop inline  -----------------+
| ciRun                                             |
| ciPass?  -YES-> ...                               |
| NO                                                |
| ciDebugGuard? -NO-> FINISH(ci_retry_exhausted)    |
| ...                                               |
+---------------------------------------------------+
  v
review + merge
  v
7 copy-pasted FINISH sequences


After Epic 13 (SingleIssueCycleWorkflow — ~500 lines, ~29 activities):

START
  v
... 20 pre-TDD activities ...
  v
[DispatchWorkflow("tdd-with-debug-retry", {storyId, planJson, ...})]
  v
FlowDecision(tddResult.success)  -NO-> SharedFinishSequence('tdd_failed')
  v YES
[DispatchWorkflow("ci-with-debug-retry", {...})]
  v
FlowDecision(ciResult.success)  -NO-> SharedFinishSequence('ci_failed')
  v YES
review + merge
  v
SharedFinishSequence('merged_ok')


Sub-workflows produced by the epic:

TddWithDebugRetryWorkflow (DefinitionId: tdd-with-debug-retry)
  inputs : storyId, planJson, repositoryUrl, branchName, skillLevel
  outputs: success : bool, errorMessage : string
  graph  : tddCycle → success? → debugGuard? → incrementDebug → dispatchDebugging → loop

CiWithDebugRetryWorkflow   (DefinitionId: ci-with-debug-retry)
  inputs : prNumber, repositoryUrl, branchName, skillLevel
  outputs: success : bool, errorMessage : string
  graph  : ciRun → pass? → debugGuard? → incrementDebug → dispatchDebugging → loop

SharedFinishSequence (shared tail or helper workflow)
  inputs : outcomeKey, storyId, prNumber?, errorMessage?
  actions: emit completion event, update issue state, post summary comment,
           notify dispatcher, close down workflow
```

## Components

| Component | Purpose | Key files | Status |
|-----------|---------|-----------|--------|
| `TddWithDebugRetryWorkflow` | Extracted TDD cycle + debug retry counter | `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TddWithDebugRetryWorkflow.cs` | 13-1 / Done |
| Parent refactor (TDD) | Replace 5 inlined activities with `DispatchWorkflow` + 1 `FlowDecision` | `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` | 13-1 / Done |
| `CiWithDebugRetryWorkflow` | Extracted CI cycle + debug retry counter | `.../CiWithDebugRetryWorkflow.cs` | 13-2 / Done |
| Parent refactor (CI) | Replace inlined CI retry loop with `DispatchWorkflow` | `.../SingleIssueCycleWorkflow.cs` | 13-2 / Done |
| Shared finish sequence | Consolidate 7 duplicated finish branches into one parameterized path | `.../SingleIssueCycleWorkflow.cs` (+ helper) | 13-3 / Done |
| Structure tests | Prove path equivalence pre/post extraction | `apps/tamma-elsa/tests/.../SingleIssueCycleStructureTests.cs` | Done (26 tests) |
| Dead-variable cleanup | Delete pre-existing `reviewFixAttempt` at line ~57 | `.../SingleIssueCycleWorkflow.cs` | Folded into 13-1 |

## Class / type structure

```
apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/

  abstract class WorkflowBase
    protected IActivity DispatchWorkflow(defId, inputs, outputMap)
    protected IActivity SetOutput<T>(name, value)
    protected IActivity FlowDecision(exprResult)

  class SingleIssueCycleWorkflow : WorkflowBase
    DefinitionId: "single-issue-cycle"
    (down to ~29 activities after Epic 13)
    Uses: DispatchWorkflow("tdd-with-debug-retry", ...)
          DispatchWorkflow("ci-with-debug-retry", ...)
          SharedFinishSequence(...)

  class TddWithDebugRetryWorkflow : WorkflowBase
    DefinitionId: "tdd-with-debug-retry"
    Inputs : storyId, planJson, repositoryUrl, branchName, skillLevel
    Outputs: success, errorMessage
    Activities: tddCycle (DispatchWorkflow: TddWorkflow)
                tddSuccess (FlowDecision)
                tddDebugGuard (FlowDecision with counter)
                incrementTddDebug (SetVariable)
                dispatchTddDebugging (DispatchWorkflow: DebuggingWorkflow)

  class CiWithDebugRetryWorkflow : WorkflowBase
    DefinitionId: "ci-with-debug-retry"
    Inputs : prNumber, repositoryUrl, branchName, skillLevel
    Outputs: success, errorMessage
    Activities: ciRun (DispatchWorkflow or Activity hitting GitHub Actions)
                ciPass (FlowDecision)
                ciDebugGuard (FlowDecision with counter)
                incrementCiDebug (SetVariable)
                dispatchCiDebugging (DispatchWorkflow: DebuggingWorkflow)

  static class FinishSequenceFactory   // 13-3 consolidation
    IActivity Build(outcomeKey, storyId, prNumber?, errorMessage?)
    // Parameterized: emits completion event, updates issue state,
    // writes summary comment, notifies dispatcher, closes the workflow
```

## Sequence — full issue cycle post-Epic-13

```
AdlOrchestrator   SingleIssueCycle   TddWithDebugRetry     TddWorkflow   DebuggingWorkflow   CiWithDebugRetry   FinishSequence
     |                  |                    |                  |                |                 |                |
     | dispatch -------->|                    |                  |                |                 |                |
     |                  | ... pre-TDD (context, plan, branch, implement) ...      |                 |                |
     |                  | DispatchWorkflow('tdd-with-debug-retry', ...) -->       |                 |                |
     |                  |                    | tddCycle (Dispatch TddWorkflow) -->|                |                 |                |
     |                  |                    | <-- red (tests fail) --------------|                |                 |                |
     |                  |                    | tddSuccess? NO                    |                |                 |                |
     |                  |                    | tddDebugGuard (attempt 1 < 3)? YES                |                 |                |
     |                  |                    | incrementTddDebug                  |                |                 |                |
     |                  |                    | dispatchTddDebugging --------------|--------------> |                 |                |
     |                  |                    | <-- fix applied -------------------|--------------- |                 |                |
     |                  |                    | loop: tddCycle again             -->                |                 |                |
     |                  |                    | <-- green --                       |                |                 |                |
     |                  |                    | SetOutput(success=true, errorMessage='')           |                 |                |
     |                  | <-- tddResult.success=true                              |                |                 |                |
     |                  | DispatchWorkflow('ci-with-debug-retry', ...) ---------------------------------->|                          |
     |                  |                                                         |                |                 |                |
     |                  |                                                         |                | ciRun           |                |
     |                  |                                                         |                | ciPass? YES     |                |
     |                  |                                                         |                | SetOutput(success=true)         |
     |                  | <-- ciResult.success=true -------------------------------------------------------|                          |
     |                  | merge                                                   |                |                 |                |
     |                  | SharedFinishSequence('merged_ok', storyId, prNumber)--------------------------------|--------------------> |
     |                  |                                                         |                |                 | emit completion event
     |                  |                                                         |                |                 | update issue state
     |                  |                                                         |                |                 | post summary comment
     |                  |                                                         |                |                 | notify dispatcher
     | <-- workflow complete                                                       |                |                 |                |
```

## Use cases

- **Operator inspecting a stuck TDD retry loop** — session is paused at 2/3 retries. In Studio the operator opens the `tdd-with-debug-retry` sub-workflow instance directly, sees `incrementTddDebug` state and the `dispatchTddDebugging` bookmark, and understands the situation without wading through the 39-activity parent graph.
- **Reusing the TDD retry loop outside the cycle** — a one-off "run TDD on this branch and retry on failure" command can call `tdd-with-debug-retry` directly; no need to re-create the retry machinery.
- **Tuning retry limits independently** — change the counter ceiling in `TddWithDebugRetryWorkflow` alone; no need to touch `SingleIssueCycleWorkflow` or `CiWithDebugRetryWorkflow`.
- **Structure test safety net** — a future refactor of the parent workflow (e.g. splitting out the review + merge leg) can be verified with the existing 26 structure tests; if any finish-sequence path changes, a test fails and flags the regression.
- **Shared finish sequence reuse** — when Epic 2 or Epic 7 introduce a new outcome type (e.g. `abandoned_by_reviewer`), it becomes a new `outcomeKey` in `FinishSequenceFactory.Build` rather than a new copy-pasted 4-activity branch.

## Dependencies

**Upstream**
- Epic 7 — `TddWorkflow`, `DebuggingWorkflow`, `CodeReviewWorkflow` are dispatched targets; Epic 13 depends on these being stable definitions.
- Epic 12 — `CallLlmInlineActivity` tool loop is invoked inside `DebuggingWorkflow`, which both retry loops dispatch.
- Epic 11 — security pipeline applies to every LLM call inside both extracted loops.

**Downstream**
- Epic 2 — autonomous development loop calls the simpler `SingleIssueCycleWorkflow`.
- Epic 14 — ELSA Studio renders the three separate workflows cleanly; Studio usage improves with decomposition.
- Future workflow work — the pattern (`WorkflowBase` + `DispatchWorkflow`) is the template for further decomposition as more retry/lifecycle loops land.

## Current state

Landed:
- `9ac2fcb feat(elsa): extract TddWithDebugRetryWorkflow from SingleIssueCycle [13-1]`
- `44d7ef7 feat(elsa): extract CiWithDebugRetryWorkflow from SingleIssueCycle [13-2]`
- `29843ba refactor(elsa): consolidate 7 finish sequences into shared output [13-3]`
- `b0803486 test(workflows): add 26 structure tests for Epic 13 decomposition`
- `d351bcb fix: review fixes for 13-2 ciRetryCount + 12-1 rm pattern bypass` — follow-up fix on CI counter.

Path-equivalence asserted:
- 26 structure tests cover: TDD success on first try, TDD success after 1 debug, TDD exhausted retry, CI success on first try, CI success after 1 debug, CI exhausted retry, seven finish-branch outcomes (merged, rejected, escalated, tdd_failed, ci_failed, reviewer_abandoned, timeout).

Parent-workflow size drop:
- `SingleIssueCycleWorkflow.cs` went from 783 lines / 39 activities to ~500 lines / ~29 activities.

Stubs / deferrals:
- None in-scope for Epic 13.
- Further decomposition (e.g. extracting the review+merge leg) is out-of-scope; tracked separately if needed.

## See also

- [Workflow: Single Issue Cycle](../Workflow-Single-Issue-Cycle.md) — parent workflow this epic simplifies.
- [Workflow: TDD Cycle](../Workflow-TDD-Cycle.md) — dispatched TDD workflow.
- [Workflow: TDD with Debug Retry](../Workflow-TDD-With-Debug-Retry.md) — extracted sub-workflow.
- [Workflow: CI with Debug Retry](../Workflow-CI-With-Debug-Retry.md) — extracted sub-workflow.
- [Workflow: Debugging](../Workflow-Debugging.md) — dispatched from both retry loops.
- [Epic 7: Mentorship](Epic-7-Mentorship.md) — source of the TDD/CI activities.
- [Epic 12: Tool Loop](Epic-12-Tool-Loop.md) — powers the LLM calls inside debugging.
- [Epic 14: ELSA Studio](Epic-14-ELSA-Studio.md) — visualizes the decomposed workflow set.
- Source plan: `.dev/plans/single-issue-workflow-split.md`.
- Impl plans: [`docs/stories/epic-13/`](https://github.com/meywd/tamma/tree/main/docs/stories/epic-13).
- Source: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/*.cs`.

---

_Last refreshed 2026-04-22._
