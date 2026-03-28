# Story 13.2: CI Debug Retry Sub-Workflow

Status: ready-for-dev

## Story

As a **workflow engineer**,
I want the CI pipeline with debug retry logic extracted from `SingleIssueCycleWorkflow` into a standalone `CiWithDebugRetryWorkflow`,
so that the CI retry loop is independently testable, the parent workflow is further simplified, and the `ciRetryCount` variable handling is explicit and documented.

## Acceptance Criteria

1. New file `CiWithDebugRetryWorkflow.cs` exists with `DefinitionId: "ci-with-debug-retry"`
2. The sub-workflow contains the 5 extracted activities: `testingPipeline`, `testsPassed`, `ciRetryGuard`, `incrementCiRetry`, `dispatchCiDebugging`
3. Sub-workflow inputs: `repository`, `branchName`, `issueNumber`, `skillLevel`, `ciRetryCount` (initial value, passed through from parent to preserve current behavior)
4. Sub-workflow outputs: `passed` (bool), `errorMessage` (string), `ciRetryCount` (final value, returned to parent)
5. `SingleIssueCycleWorkflow` replaces the extracted section with 1 `DispatchWorkflow` activity targeting `ci-with-debug-retry` and 1 `FlowDecision` checking the sub-workflow's `passed` output
6. All 3 loop-back paths in the parent that previously targeted CI activities are updated to target the new DispatchWorkflow: (a) review-fix loop, (b) merge-approval "test" signal, (c) CI debug retry re-entry
7. `ciRetryCount` is passed as input to the sub-workflow and returned as output, preserving the current behavior where the counter persists across re-entries
8. Path equivalence: all CI pass, CI failure, and CI debug retry paths produce identical outcomes to the original
9. The `ciRetryCount` pass-through behavior is documented with a code comment noting it is likely a bug (re-entry after review fix should reset the counter) to be fixed in a separate ticket

## Technical Context

### Extracted Activities

1. **testingPipeline** — Dispatches the `TestingWorkflow` to run CI/CD pipeline
2. **testsPassed** — `FlowDecision` checking if tests passed
3. **ciRetryGuard** — `FlowDecision` checking if CI retry count is under the limit
4. **incrementCiRetry** — `SetVariable` incrementing the CI retry counter
5. **dispatchCiDebugging** — Dispatches the `DebuggingWorkflow` for CI failures

### Critical: Loop-Back Paths

This is the highest-risk part of the extraction. The parent workflow has 3 paths that loop back to the CI section:

1. **Review-fix loop**: After applying review fixes, the workflow re-runs CI. Currently targets `testingPipeline` activity. Must now target the new `DispatchWorkflow("ci-with-debug-retry")`.
2. **Merge-approval "test" signal**: When merge approval returns "test" (re-test requested), workflow re-runs CI. Currently targets `testingPipeline`. Must now target the dispatch.
3. **CI debug retry re-entry**: Internal to the sub-workflow (stays within `CiWithDebugRetryWorkflow`).

### ciRetryCount Decision

The plan documents a deliberate decision: pass `ciRetryCount` as input to preserve current behavior. The current behavior is that `ciRetryCount` persists across re-entries (review-fix loop, merge-approval re-test). This means if CI failed 2 times before a review fix, after the fix it starts at 2 (not 0). This is likely a bug — but fixing it is a separate ticket. Document with:

```csharp
// NOTE: ciRetryCount is passed through to preserve existing behavior.
// This means the counter persists across re-entries (review-fix, merge re-test).
// This is likely a bug — re-entry should reset the counter.
// Fix tracked as a separate ticket.
```

### Sub-Workflow Shape

```
START (inputs: repository, branchName, issueNumber, skillLevel, ciRetryCount)
  |
  v
testingPipeline (DispatchWorkflow: TestingWorkflow)
  |
  v
testsPassed? --YES--> FINISH(passed=true, ciRetryCount)
  |
  NO
  |
  v
ciRetryGuard? --NO--> FINISH(passed=false, errorMessage="CI retry limit reached", ciRetryCount)
  |
  YES
  |
  v
incrementCiRetry (ciRetryCount++)
  |
  v
dispatchCiDebugging (DispatchWorkflow: DebuggingWorkflow)
  |
  v
(loop back to testingPipeline)
```

### Files to Create

- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CiWithDebugRetryWorkflow.cs` (~150 lines)

### Files to Modify

- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` — remove 5 activities, add 1 DispatchWorkflow + 1 FlowDecision, update 3 loop-back paths

## Implementation Notes

1. **Map all loop-back targets first**: Before writing any code, identify all 3 loop-back paths in the parent workflow by searching for activity names that reference the CI section. Document each path and its new target.
2. **Update loop-backs atomically**: Change all 3 loop-back targets in the same commit. A partial update would leave the workflow in a broken state.
3. **ciRetryCount as bidirectional**: The parent passes the current counter value as input, the sub-workflow increments it internally, and returns the final value as output. The parent reads the output and stores it in its own variable for the next dispatch (in case of review-fix loop-back).
4. **Test the "test" signal path**: The merge-approval "test" signal is the least-obvious loop-back. Verify it re-dispatches the CI sub-workflow with the correct inputs.
5. **DispatchWorkflow, not RunWorkflow**: Same as Story 13.1 — use ELSA's `DispatchWorkflow` activity.

## Testing Strategy

- **Build verification**: Project compiles with no errors after extraction
- **Workflow registration**: Sub-workflow auto-discovered by `AddWorkflowsFrom<TAssembly>()`
- **Path equivalence tests** (5):
  - CI passes on first try: parent dispatches, sub-workflow returns `passed=true`, parent continues to merge
  - CI fails, debug retries, then passes: sub-workflow loops internally, returns `passed=true`
  - CI fails, retry limit reached: sub-workflow returns `passed=false`, parent finishes
  - Review-fix loop: parent applies review fixes, re-dispatches CI sub-workflow, CI passes
  - Merge-approval "test" signal: parent receives "test" signal, re-dispatches CI sub-workflow
- **CI retry counter test**: Verify `ciRetryCount` value is preserved across re-entries (document as known behavior, not a test of correct behavior)
- **Visual inspection**: Load workflow in ELSA Studio, verify all 3 loop-back paths connect to the new DispatchWorkflow node
- **Test file**: `apps/tamma-elsa/tests/Tamma.ElsaServer.Tests/Workflows/CiWithDebugRetryWorkflowTests.cs`

## Dependencies

- **Story 13.1** (TDD Debug Retry Sub-Workflow) — establishes the sub-workflow extraction pattern, reduces parent workflow size first

## Estimated Effort

2 days

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation from `.dev/plans/single-issue-workflow-split.md` Phase 3 | Architecture Team |
