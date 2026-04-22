# Story 12-5e: CI Retry Counter Bug Fix — Implementation Plan

**Parent story**: `12-5-prompt-engineering-framework.md` (sub-story 12-5e)
**Parent impl plan**: `12-5-prompt-engineering-impl-plan.md`
**Layer**: Layer 2, Team E (quick wins)
**Branch**: `fix/story-12-5e-ci-retry-counter`
**Effort**: ~2 hours

---

## Problem

The `ciRetryCount` counter in the CI debug retry loop is expected to persist across a workflow instance's lifetime, but the implementer framing the bug reports it getting "reset to zero on every workflow suspension/resumption" — i.e., the counter is behaving like a local/transient variable instead of persisted workflow state.

The symptom in practice: after the workflow suspends (e.g. waiting for a CI run) and resumes, the counter is back to 0, so the 3-retry budget is not actually enforced across the entire lifetime. A misbehaving test run can consume unlimited retries by repeatedly suspend-resume.

## Contested root cause — verify first

There is a documentation/code mismatch between two sources:

1. **Parent story (12-5 framework doc) says**: "Lines 349-351 of SingleIssueCycleWorkflow.cs contain a self-documented bug: the CI retry counter passes through to `ci-with-debug-retry` sub-workflow and isn't reset when re-entering from review-fix or merge re-test." — i.e., the counter persists across re-entries when it should NOT.
2. **This plan's framing (per Team E ticket)**: "counter stored in a local/transient variable instead of workflow state. Current code declares the counter inside an activity, not on the workflow instance." — i.e., the counter resets when it should NOT.

3. **Actual code** (as of this plan's writing):
   - `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CiWithDebugRetryWorkflow.cs` lines 51–53 already declare `ciRetryCount` via `builder.WithVariable<int>("CiRetryCount", 0)` (a workflow-instance variable)
   - Lines 76–78 explicitly reset it to 0 in `initInputs` with a comment: "Always reset ciRetryCount to 0 on entry so each invocation ... gets full retry budget"
   - `SingleIssueCycleWorkflow.cs` does **not** currently contain any `ciRetryCount` reference; lines 349–351 are unrelated (task-review dispatch)

**Task 1 is therefore**: verify the bug before writing any fix. Run the failing scenario locally (or add a failing test) before editing.

## Tasks

### 1. Reproduce and confirm the actual bug (30 min)

- Check `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CiWithDebugRetryWorkflow.cs` and any other workflow that tracks a CI retry counter (`grep -rn "CiRetryCount\|ciRetryCount" apps/tamma-elsa/src`).
- Write a failing integration test in `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/` that:
  - Runs `CiWithDebugRetryWorkflow` with a stub testing pipeline that always fails.
  - Suspends the workflow mid-loop (e.g. via a bookmark on `DispatchTestingPipeline`).
  - Resumes the workflow from the persisted instance.
  - Asserts the counter value **after resume** matches the value **before suspend**.
- Run the test and observe what actually happens. Use the test output to decide which framing is correct:
  - **(a)** If counter is lost across suspend/resume → Elsa persistence issue with `builder.WithVariable`, OR the variable is being declared in a scope that isn't serialized. Fix by ensuring the variable is declared on the workflow builder (it is), and that the Elsa workflow instance store is actually persisting variables (check migration + `WorkflowInstanceStore` config).
  - **(b)** If counter persists across suspend/resume but is wrongly carried across re-entries from review-fix / merge re-test → the `initInputs` reset is not being hit on those re-entry paths. Fix in `SingleIssueCycleWorkflow.cs` by dispatching a fresh `CiWithDebugRetryWorkflow` instance for each re-entry (which already invokes `initInputs` and resets to 0).
  - **(c)** If the test shows both behave correctly → the bug is stale; update the parent story and close the sub-story with a note.

### 2. Apply the fix matching the confirmed root cause (45 min)

- **Case (a) — persistence scoping**: Ensure `ciRetryCount` is declared at workflow-builder scope (already is). If Elsa is dropping it, the likely issue is the variable's storage driver. Audit `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` for `AddWorkflowsCore()` options; confirm `UseMemoryStores` is **not** set in production and that `UseEntityFrameworkCore` persistence is enabled with `WorkflowInstances` table up to date. Migration check: `apps/tamma-elsa/src/Tamma.ElsaServer/Migrations/` must include the Elsa workflow-instance schema.
- **Case (b) — re-entry reset**: Confirm each re-entry path in `SingleIssueCycleWorkflow.cs` dispatches a fresh `ci-with-debug-retry` instance (not reusing a bookmarked one). `DispatchWorkflow { WorkflowDefinitionId = new("ci-with-debug-retry") }` inside a flowchart node creates a new child instance each time the node is entered, which already exercises `initInputs`. If any path re-enters via a bookmark on the sub-workflow instead, replace the bookmark with a fresh dispatch.
- **Case (c) — stale bug**: Update `docs/stories/epic-12/story-12-5/12-5-prompt-engineering-framework.md` sub-story 12-5e to `Status: Invalid / Superseded`, cite the confirming test, and mark Team E hours as recovered.

In all cases, update the inline comment in `CiWithDebugRetryWorkflow.cs` lines 51–53 and 76–78 to reflect the final, verified behavior.

### 3. Regression test (30 min)

Convert the Task 1 reproduction test into a permanent regression test:

- File: `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/CiRetryCounterPersistenceTests.cs` (new)
- Pattern: follow `WorkflowStructureTests.cs` and `LlmCallWorkflowTests.cs` for Elsa workflow harness setup.
- Coverage:
  - Fresh invocation starts at 0.
  - Three consecutive failures → counter at 3, workflow takes the "retries exhausted" branch.
  - Mid-loop suspend → resume → counter unchanged across the boundary.
  - Re-entering CI from a review-fix path → counter starts from whatever the confirmed semantics dictate (persisted or reset).
- Assert via `WorkflowInstance.Variables` or the Elsa variable bag API. Consult `WorkflowTestHelper.cs` for the existing pattern.

### 4. Docs + verification (15 min)

- Update parent story `12-5-prompt-engineering-framework.md` sub-story 12-5e status: `Done` (with PR link) or `Invalid` per Task 1 outcome.
- Update `docs/stories/plans/layer-2-parallel-infra.md` Team E entry: mark 12-5e as merged.
- No new migrations expected. If Case (a) needed a persistence fix, note it in the PR body with the migration number if one was added.

## Files to modify

| File | Change |
|---|---|
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CiWithDebugRetryWorkflow.cs` | Update inline comments; possibly adjust `initInputs` reset logic (Case a/b). |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` | Only if Case (b): verify re-entry dispatches are fresh. |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` | Only if Case (a): workflow persistence config. |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/CiRetryCounterPersistenceTests.cs` | **New** — regression test file. |
| `docs/stories/epic-12/story-12-5/12-5-prompt-engineering-framework.md` | Status update on sub-story 12-5e. |
| `docs/stories/plans/layer-2-parallel-infra.md` | Team E status update. |

## Test command

```bash
dotnet test apps/tamma-elsa/tests/Tamma.Activities.Tests/Tamma.Activities.Tests.csproj \
  --filter "FullyQualifiedName~CiRetryCounterPersistenceTests"
```

Full suite sanity check:

```bash
dotnet test apps/tamma-elsa/Tamma.ElsaServer.sln
```

## Acceptance criteria

1. Regression test exists and fails on the current `main` (proves the bug is real) OR documentation is updated to mark the bug invalid.
2. If a fix was applied: regression test passes; existing `WorkflowStructureTests` and `LlmCallWorkflowTests` still pass.
3. Inline comments in `CiWithDebugRetryWorkflow.cs` accurately describe the verified behavior.
4. Parent story sub-story 12-5e status updated.

## Out of scope

- Refactoring the CI retry loop structure itself.
- Generalizing workflow variable persistence beyond this one counter.
- Changing the default `maxRetries` value (3).
- Touching `TddWithDebugRetryWorkflow.cs` — that workflow has its own counter handled in its own story.
