# Story 13.1: TDD Debug Retry Sub-Workflow

Status: ready-for-dev

## Story

As a **workflow engineer**,
I want the TDD cycle with debug retry logic extracted from `SingleIssueCycleWorkflow` into a standalone `TddWithDebugRetryWorkflow`,
so that the parent workflow is smaller and easier to reason about, and the TDD retry logic is independently testable and reusable.

## Acceptance Criteria

1. Dead variable `reviewFixAttempt` (line ~57 of `SingleIssueCycleWorkflow.cs`) is removed as a prerequisite cleanup
2. New file `TddWithDebugRetryWorkflow.cs` exists with `DefinitionId: "tdd-with-debug-retry"`
3. The sub-workflow contains the 5 extracted activities: `tddCycle`, `tddSuccess`, `tddDebugGuard`, `incrementTddDebug`, `dispatchTddDebugging`
4. Sub-workflow inputs: `storyId`, `planJson`, `repositoryUrl`, `branchName`, `skillLevel`
5. Sub-workflow outputs: `success` (bool), `errorMessage` (string)
6. `SingleIssueCycleWorkflow` replaces the extracted section with 1 `DispatchWorkflow` activity targeting `tdd-with-debug-retry` and 1 `FlowDecision` checking the sub-workflow's `success` output
7. The sub-workflow auto-registers via ELSA's `AddWorkflowsFrom<TAssembly>()` (no manual registration needed)
8. `WorkflowVersions` auto-bumps when the new `.cs` file changes the assembly hash
9. Path equivalence: all TDD success, TDD failure, and TDD debug retry paths produce identical outcomes to the original
10. Visual verification in ELSA Studio: both parent and child workflows render correctly

## Technical Context

### Extracted Activities

The following activities are extracted from `SingleIssueCycleWorkflow`:

1. **tddCycle** — Dispatches the TDD workflow to run tests with generated code
2. **tddSuccess** — `FlowDecision` checking if TDD passed
3. **tddDebugGuard** — `FlowDecision` checking if TDD debug retry count is under the limit
4. **incrementTddDebug** — `SetVariable` incrementing the TDD debug retry counter
5. **dispatchTddDebugging** — Dispatches the debugging workflow for TDD failures

### Sub-Workflow Shape

```
START
  |
  v
tddCycle (DispatchWorkflow: TddWorkflow)
  |
  v
tddSuccess? --YES--> FINISH(success=true)
  |
  NO
  |
  v
tddDebugGuard? --NO--> FINISH(success=false, errorMessage="TDD debug retry limit reached")
  |
  YES
  |
  v
incrementTddDebug
  |
  v
dispatchTddDebugging (DispatchWorkflow: DebuggingWorkflow)
  |
  v
(loop back to tddCycle)
```

### Parent Workflow After Extraction

Where the 5 activities used to be, the parent gets:

```
... (prior activities)
  |
  v
DispatchWorkflow("tdd-with-debug-retry", { storyId, planJson, repositoryUrl, branchName, skillLevel })
  |
  v
FlowDecision(tddResult.success)
  |
  YES --> (continue to testing/CI)
  NO --> (finish with TDD failure)
```

### Files to Create

- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TddWithDebugRetryWorkflow.cs` (~150 lines)

### Files to Modify

- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` — remove 5 activities, add 1 DispatchWorkflow + 1 FlowDecision, remove dead `reviewFixAttempt` variable

## Implementation Notes

1. **Delete dead variable first**: Remove `reviewFixAttempt` (declared, never used) as a separate, zero-risk first step. Verify build succeeds before proceeding.
2. **Copy, then cut**: Create the sub-workflow by copying the 5 activities from the parent, then remove them from the parent once the sub-workflow builds and renders correctly.
3. **Variable mapping**: The sub-workflow needs its own workflow variables for `tddDebugCount`. The parent passes initial values via DispatchWorkflow input and reads results from the output.
4. **DispatchWorkflow**: Use ELSA's `DispatchWorkflow` activity (not `RunWorkflow`) to dispatch the sub-workflow. This runs the sub-workflow asynchronously and waits for completion.
5. **DefinitionId**: Set to `"tdd-with-debug-retry"` (kebab-case, matching project convention for workflow IDs).
6. **Preserve all FlowDecision logic**: The `tddSuccess` and `tddDebugGuard` decisions must use the exact same expressions as in the original workflow.

## Testing Strategy

- **Build verification**: Project compiles with no errors after extraction
- **Workflow registration**: Sub-workflow auto-discovered by `AddWorkflowsFrom<TAssembly>()`
- **Path equivalence tests** (3):
  - TDD passes on first try: parent dispatches sub-workflow, sub-workflow returns `success=true`, parent continues to testing
  - TDD fails, debug retries, then passes: sub-workflow loops internally, eventually returns `success=true`
  - TDD fails, debug retry limit reached: sub-workflow returns `success=false`, parent finishes with TDD failure
- **Visual inspection**: Load both workflows in ELSA Studio, verify graph renders correctly, all connections intact
- **Dead variable test**: Verify `reviewFixAttempt` does not appear anywhere in the codebase after removal
- **Test file**: `apps/tamma-elsa/tests/Tamma.ElsaServer.Tests/Workflows/TddWithDebugRetryWorkflowTests.cs`

## Dependencies

- **None** (first story in Epic 13)

## Estimated Effort

1.5 days

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation from `.dev/plans/single-issue-workflow-split.md` Phases 1+2 | Architecture Team |
