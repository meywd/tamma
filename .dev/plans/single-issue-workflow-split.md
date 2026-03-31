# Plan: SingleIssueCycleWorkflow Split

## Summary
Split 783-line / 39-activity workflow into smaller sub-workflows. Target: ~500 lines / ~29 activities.

## Phases

### Phase 1: Delete Dead Variable
- Remove `reviewFixAttempt` (line 57) — declared, never used
- Zero risk

### Phase 2: TddWithDebugRetryWorkflow
- New file: `Workflows/TddWithDebugRetryWorkflow.cs` (~150 lines)
- DefinitionId: `tdd-with-debug-retry`
- Extracts 5 activities: tddCycle, tddSuccess, tddDebugGuard, incrementTddDebug, dispatchTddDebugging
- Inputs: storyId, planJson, repositoryUrl, branchName, skillLevel
- Outputs: success (bool), errorMessage (string)
- Parent gets 1 DispatchWorkflow + 1 FlowDecision

### Phase 3: CiWithDebugRetryWorkflow
- New file: `Workflows/CiWithDebugRetryWorkflow.cs` (~150 lines)
- DefinitionId: `ci-with-debug-retry`
- Extracts 5 activities: testingPipeline, testsPassed, ciRetryGuard, incrementCiRetry, dispatchCiDebugging
- Inputs: repository, branchName, issueNumber, skillLevel, ciRetryCount (pass-through to preserve behavior)
- Outputs: passed (bool), errorMessage, ciRetryCount (return final value)
- **Critical**: Update all 3 loop-back paths (review-fix, merge-approval "test", CI debug) to target new dispatch

### Phase 4: Consolidate Finish Sequences
- Replace 7 Sequence nodes with 7 SetVariable (reason) + 1 shared output Sequence
- Net: -6 activities from flowchart

### Phase 5: Testing
- Build verification (auto-registered via AddWorkflowsFrom)
- WorkflowVersions auto-bumps (new .cs files change hash)
- Visual inspection in ELSA Studio
- Path equivalence for all exit paths
- CI retry counter preservation test

## CI Retry Counter Decision
Pass `ciRetryCount` as input to preserve current behavior (counter persists across re-entries). Note: this is likely a bug — re-entry after review fix should reset. Fix as separate ticket.

## Sequencing
Phase 1 → Phase 2 → Phase 3 → Phase 4 → Phase 5 (each a separate commit)
