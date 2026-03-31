---
title: "Story 13.2: CI Debug Retry Sub-Workflow - Implementation Plan"
sidebar:
  order: 130
---

## Overview

Extract the CI pipeline with debug retry loop (5 activities) from `SingleIssueCycleWorkflow.cs`
into a new `CiWithDebugRetryWorkflow.cs` sub-workflow. Update all 3 loop-back paths in the parent
that previously targeted CI activities.

**Prerequisite**: Story 13.1 is complete. The parent workflow no longer contains TDD retry activities.

**Source file state**: After Story 13.1, `SingleIssueCycleWorkflow.cs` is ~698 lines with the TDD
sub-workflow extracted. The CI section (activities + connections) is the next extraction target.

---

## Pre-Implementation: Map All Loop-Back Paths

Before writing any code, identify the 3 paths in `SingleIssueCycleWorkflow` that currently target
`testingPipeline` (the first activity in the CI section):

### Path 1: CI Debug Retry (internal to sub-workflow)

**Connection** (original line ~729):
```csharp
Connect(dispatchCiDebugging, testingPipeline), // retry CI
```
This path becomes **internal** to the new sub-workflow. No parent change needed.

### Path 2: Review-Fix Loop

**Connection** (original line ~733):
```csharp
ConnectOutcome(hasReviewComments, "True", testingPipeline), // re-run CI after fixes
```
Must change target from `testingPipeline` to the new `dispatchCiRetry` activity.

### Path 3: Merge-Approval "Test" Signal

**Connection** (original line ~740):
```csharp
ConnectOutcome(testDecision, "True", testingPipeline), // re-run tests
```
Must change target from `testingPipeline` to the new `dispatchCiRetry` activity.

---

## Step 1: Create `CiWithDebugRetryWorkflow.cs`

**New file**: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CiWithDebugRetryWorkflow.cs`

### Complete File Content

```csharp
using Elsa.Expressions.Models;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// CI with Debug Retry sub-workflow — encapsulates the CI testing pipeline dispatch
/// with up to 3 debug retry iterations on failure.
///
/// Flow:
///   testingPipeline -> testsPassed?
///     YES -> finish(passed=true, ciRetryCount)
///     NO  -> ciRetryGuard (< 3)?
///       NO  -> finish(passed=false, errorMessage, ciRetryCount)
///       YES -> incrementCiRetry -> dispatchCiDebugging -> (loop to testingPipeline)
///
/// Inputs:  repository, branchName, issueNumber, skillLevel, ciRetryCount
/// Outputs: passed (bool), errorMessage (string), ciRetryCount (int)
///
/// NOTE: ciRetryCount is passed through to preserve existing behavior.
/// This means the counter persists across re-entries (review-fix, merge re-test).
/// This is likely a bug — re-entry should reset the counter.
/// Fix tracked as a separate ticket.
/// </summary>
public class CiWithDebugRetryWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "CI with Debug Retry";
        builder.DefinitionId = "ci-with-debug-retry";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Dispatches CI testing pipeline with up to 3 debug retry iterations on failure";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var branchName = builder.WithVariable<string>("BranchName", "");
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0);
        var skillLevel = builder.WithVariable<int>("SkillLevel", 5);
        // NOTE: ciRetryCount is passed through to preserve existing behavior.
        // This means the counter persists across re-entries (review-fix, merge re-test).
        // This is likely a bug — re-entry should reset the counter.
        // Fix tracked as a separate ticket.
        var ciRetryCount = builder.WithVariable<int>("CiRetryCount", 0);

        // DispatchWorkflow result capture
        var testResult = builder.WithVariable<IDictionary<string, object>?>();
        var debugResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // INIT: Capture inputs
        // ================================================================
        var initInputs = new SetVariable
        {
            Id = "InitCiRetryInputs",
            Name = "Init Inputs",
            Variable = repository,
            Value = new Input<object?>(ctx =>
            {
                var branch = ctx.GetInput<string>("branchName");
                if (!string.IsNullOrEmpty(branch)) branchName.Set(ctx, branch);
                var issue = ctx.GetInput<int>("issueNumber");
                if (issue > 0) issueNumber.Set(ctx, issue);
                var skill = ctx.GetInput<int>("skillLevel");
                if (skill > 0) skillLevel.Set(ctx, skill);
                var retryCount = ctx.GetInput<int>("ciRetryCount");
                if (retryCount > 0) ciRetryCount.Set(ctx, retryCount);
                return (object)(ctx.GetInput<string>("repository") ?? "");
            })
        };
        initInputs.SetDisplayText("Init Inputs");

        // ================================================================
        // Testing Pipeline (dispatch to existing testing-pipeline workflow)
        // ================================================================
        var testingPipeline = new DispatchWorkflow
        {
            Id = "DispatchTestingPipeline",
            Name = "Testing Pipeline",
            WorkflowDefinitionId = new("testing-pipeline"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["SessionId"] = Guid.NewGuid(),
                ["Repository"] = repository.Get(ctx),
                ["Branch"] = branchName.Get(ctx),
                ["SkillLevel"] = skillLevel.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(testResult)
        };
        testingPipeline.SetDisplayText("Testing Pipeline");

        // ================================================================
        // Tests Passed check
        // ================================================================
        var testsPassed = new FlowDecision(ctx =>
        {
            var result = testResult.Get(ctx);
            if (result != null && result.TryGetValue("passed", out var p))
                return p is true || p?.ToString() == "True";
            return false;
        })
        { Id = "TestsPassed", Name = "Tests Passed?" };
        testsPassed.SetDisplayText("Tests Passed?");

        // ================================================================
        // CI retry guard (< 3 retries)
        // ================================================================
        var ciRetryGuard = new FlowDecision(ctx => ciRetryCount.Get(ctx) < 3)
        { Id = "CiRetryGuard", Name = "CI Retries < 3?" };
        ciRetryGuard.SetDisplayText("CI Retries < 3?");

        // ================================================================
        // Increment CI retry counter
        // ================================================================
        var incrementCiRetry = new SetVariable
        {
            Id = "IncrCiRetry",
            Name = "Increment CI Retry",
            Variable = ciRetryCount,
            Value = new Input<object?>(ctx => (object)(ciRetryCount.Get(ctx) + 1))
        };
        incrementCiRetry.SetDisplayText("Increment CI Retry");

        // ================================================================
        // Dispatch debugging for CI failure
        // ================================================================
        var dispatchCiDebugging = new DispatchWorkflow
        {
            Id = "DispatchCiDebugging",
            Name = "Debug CI Failure",
            WorkflowDefinitionId = new("debugging"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["sessionId"] = Guid.NewGuid(),
                ["storyId"] = $"adl-{issueNumber.Get(ctx)}",
                ["debugContextMode"] = "RuntimeError",
                ["errorOutput"] = GetTestErrorOutput(testResult.Get(ctx)),
                ["repositoryUrl"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["skillLevel"] = skillLevel.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(debugResult)
        };
        dispatchCiDebugging.SetDisplayText("Debug CI Failure");

        // ================================================================
        // Finish: Pass outputs
        // ================================================================
        var finishPassOutputs = new Sequence
        {
            Id = "CiRetryFinishPass",
            Name = "Finish Pass",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetCiRetryPassed", Name = "Set Passed", OutputName = new("passed"), OutputValue = new(_ => (object)true) }, "Set Passed"),
                WithLabel(new SetOutput { Id = "SetCiRetryErrorEmpty", Name = "Set Error Empty", OutputName = new("errorMessage"), OutputValue = new(_ => (object)"") }, "Set Error Empty"),
                WithLabel(new SetOutput { Id = "SetCiRetryCountPass", Name = "Set CI Retry Count", OutputName = new("ciRetryCount"), OutputValue = new(ctx => (object)ciRetryCount.Get(ctx)) }, "Set CI Retry Count")
            }
        };
        finishPassOutputs.SetDisplayText("Finish Pass");

        // ================================================================
        // Finish: Failure outputs
        // ================================================================
        var finishFailOutputs = new Sequence
        {
            Id = "CiRetryFinishFail",
            Name = "Finish Fail",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetCiRetryFailed", Name = "Set Failed", OutputName = new("passed"), OutputValue = new(_ => (object)false) }, "Set Failed"),
                WithLabel(new SetOutput { Id = "SetCiRetryErrorMsg", Name = "Set Error Message", OutputName = new("errorMessage"), OutputValue = new(_ => (object)"CI debug retry limit reached (3 attempts)") }, "Set Error Message"),
                WithLabel(new SetOutput { Id = "SetCiRetryCountFail", Name = "Set CI Retry Count", OutputName = new("ciRetryCount"), OutputValue = new(ctx => (object)ciRetryCount.Get(ctx)) }, "Set CI Retry Count")
            }
        };
        finishFailOutputs.SetDisplayText("Finish Fail");

        var finish = new Finish { Id = "CiRetryFinish", Name = "Complete: CI Retry Done" };
        finish.SetDisplayText("Complete: CI Retry Done");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "CiWithDebugRetryFlowchart",
            Name = "CI with Debug Retry Flowchart",
            Start = initInputs,
            Activities =
            {
                initInputs,
                testingPipeline, testsPassed, ciRetryGuard,
                incrementCiRetry, dispatchCiDebugging,
                finishPassOutputs, finishFailOutputs, finish
            },
            Connections =
            {
                // Init -> Testing Pipeline
                Connect(initInputs, testingPipeline),

                // Testing Pipeline -> Pass check
                Connect(testingPipeline, testsPassed),

                // Tests passed -> finish pass
                ConnectOutcome(testsPassed, "True", finishPassOutputs),

                // Tests failed -> retry guard
                ConnectOutcome(testsPassed, "False", ciRetryGuard),

                // Retries remaining -> increment + debug
                ConnectOutcome(ciRetryGuard, "True", incrementCiRetry),
                ConnectOutcome(ciRetryGuard, "False", finishFailOutputs),

                // Increment -> dispatch debugging -> loop back to testing
                Connect(incrementCiRetry, dispatchCiDebugging),
                Connect(dispatchCiDebugging, testingPipeline),

                // Both finish outputs -> terminal
                Connect(finishPassOutputs, finish),
                Connect(finishFailOutputs, finish)
            }
        };
    }

    // ================================================================
    // Helper methods
    // ================================================================

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));

    private static string GetTestErrorOutput(IDictionary<string, object>? result)
    {
        if (result != null && result.TryGetValue("errorMessage", out var err))
            return err?.ToString() ?? "Testing pipeline failed";
        return "Testing pipeline failed with unknown error";
    }
}
```

---

## Step 2: Modify `SingleIssueCycleWorkflow.cs` — Remove CI Activities

**File**: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`

**State at this point**: After Story 13.1, the file is ~698 lines. Line numbers below are
approximate and reference the **post-Story-13.1** state. The exact old strings are used for
matching instead of relying on line numbers.

### 2a. Remove `ciRetryCount` variable declaration

**old_string**:
```
        var ciRetryCount = builder.WithVariable<int>("CiRetryCount", 0);
```

**new_string**:
*(empty — delete the line)*

**Note**: The `ciRetryCount` variable is no longer needed in the parent. It is managed within the sub-workflow. However, we need to capture the returned `ciRetryCount` from the sub-workflow output to pass it back on re-entries. We will use the result dictionary for this.

**REVISED Decision**: Actually, we DO still need `ciRetryCount` in the parent to pass it into re-dispatches (review-fix loop, merge re-test). Keep it, but it's now only set from the sub-workflow output.

**Keep `ciRetryCount` as-is.** Do NOT delete it.

### 2b. Remove `testResult` variable declaration

**old_string**:
```
        var testResult = builder.WithVariable<IDictionary<string, object>?>();
```

**REVISED**: Rename to `ciRetryResult` to make its purpose clear. But actually, we can keep `testResult` — it just captures the sub-workflow result now. Keeping it avoids renaming downstream references. Let's repurpose it.

**Decision**: Keep `testResult` — it will capture the CI sub-workflow dispatch result instead of the testing-pipeline result.

### 2c. Remove `debugResult` variable declaration

After Story 13.1, `debugResult` was kept because CI debugging still used it. Now that CI debugging is extracted, `debugResult` is no longer needed in the parent.

**old_string**:
```
        var debugResult = builder.WithVariable<IDictionary<string, object>?>();
```

**new_string**:
*(empty — delete the line)*

### 2d. Remove CI pipeline activities

Remove the 5 CI activities that are being extracted. In the post-Story-13.1 file, these are the activities for the testing pipeline, tests passed decision, CI retry guard, increment CI retry, and dispatch CI debugging.

**old_string**:
```
        // ================================================================
        // Step 9: Testing Pipeline (existing workflow)
        // ================================================================
        var testingPipeline = new DispatchWorkflow
        {
            Id = "DispatchTestingPipeline",
            Name = "Testing Pipeline",
            WorkflowDefinitionId = new("testing-pipeline"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["SessionId"] = Guid.NewGuid(),
                ["Repository"] = repository.Get(ctx),
                ["Branch"] = branchName.Get(ctx),
                ["SkillLevel"] = 5
            }),
            WaitForCompletion = new(true),
            Result = new(testResult)
        };
        testingPipeline.SetDisplayText("Testing Pipeline");

        var testsPassed = new FlowDecision(ctx =>
        {
            var result = testResult.Get(ctx);
            if (result != null && result.TryGetValue("passed", out var p))
                return p is true || p?.ToString() == "True";
            return false;
        })
        { Id = "TestsPassed", Name = "Tests Passed?" };
        testsPassed.SetDisplayText("Tests Passed?");

        // CI retry guard
        var ciRetryGuard = new FlowDecision(ctx => ciRetryCount.Get(ctx) < 3)
        { Id = "CiRetryGuard", Name = "CI Retries < 3?" };
        ciRetryGuard.SetDisplayText("CI Retries < 3?");

        var incrementCiRetry = new SetVariable
        {
            Id = "IncrCiRetry",
            Name = "Increment CI Retry",
            Variable = ciRetryCount,
            Value = new Input<object?>(ctx => (object)(ciRetryCount.Get(ctx) + 1))
        };
        incrementCiRetry.SetDisplayText("Increment CI Retry");

        var dispatchCiDebugging = new DispatchWorkflow
        {
            Id = "DispatchCiDebugging",
            Name = "Debug CI Failure",
            WorkflowDefinitionId = new("debugging"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["sessionId"] = Guid.NewGuid(),
                ["storyId"] = $"adl-{issueNumber.Get(ctx)}",
                ["debugContextMode"] = "RuntimeError",
                ["errorOutput"] = GetTestErrorOutput(testResult.Get(ctx)),
                ["repositoryUrl"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["skillLevel"] = 5
            }),
            WaitForCompletion = new(true),
            Result = new(debugResult)
        };
        dispatchCiDebugging.SetDisplayText("Debug CI Failure");
```

**new_string**:
```
        // ================================================================
        // Step 9: CI Pipeline (dispatched to sub-workflow)
        // ================================================================
        var dispatchCiRetry = new DispatchWorkflow
        {
            Id = "DispatchCiWithDebugRetry",
            Name = "CI with Debug Retry",
            WorkflowDefinitionId = new("ci-with-debug-retry"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["issueNumber"] = issueNumber.Get(ctx),
                ["skillLevel"] = 5,
                // NOTE: ciRetryCount is passed through to preserve existing behavior.
                // This means the counter persists across re-entries (review-fix, merge re-test).
                // This is likely a bug — re-entry should reset the counter.
                // Fix tracked as a separate ticket.
                ["ciRetryCount"] = ciRetryCount.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(testResult)
        };
        dispatchCiRetry.SetDisplayText("CI with Debug Retry");

        // Extract ciRetryCount from sub-workflow output to preserve across re-entries
        var extractCiRetryCount = new SetVariable
        {
            Id = "ExtractCiRetryCount",
            Name = "Extract CI Retry Count",
            Variable = ciRetryCount,
            Value = new Input<object?>(ctx =>
            {
                var result = testResult.Get(ctx);
                if (result != null && result.TryGetValue("ciRetryCount", out var c) && c is int count)
                    return (object)count;
                return (object)ciRetryCount.Get(ctx);
            })
        };
        extractCiRetryCount.SetDisplayText("Extract CI Retry Count");

        var ciRetryPassed = new FlowDecision(ctx =>
        {
            var result = testResult.Get(ctx);
            if (result != null && result.TryGetValue("passed", out var p))
                return p is true || p?.ToString() == "True";
            return false;
        })
        { Id = "CiRetryPassed", Name = "CI Passed?" };
        ciRetryPassed.SetDisplayText("CI Passed?");
```

### 2e. Update the Flowchart Activities list

**old_string**:
```
                // Step 9: Testing Pipeline + Debug
                testingPipeline, testsPassed, ciRetryGuard,
                incrementCiRetry, dispatchCiDebugging,
```

**new_string**:
```
                // Step 9: CI Pipeline (sub-workflow)
                dispatchCiRetry, extractCiRetryCount, ciRetryPassed,
```

### 2f. Update the Flowchart Connections — PR Created target

**old_string**:
```
                ConnectOutcome(prCreated, "True", testingPipeline),
```

**new_string**:
```
                ConnectOutcome(prCreated, "True", dispatchCiRetry),
```

### 2g. Update the Flowchart Connections — CI section

**old_string**:
```
                // --- Step 9: Testing Pipeline ---
                Connect(testingPipeline, testsPassed),
                ConnectOutcome(testsPassed, "True", reviewFixCheck),
                ConnectOutcome(testsPassed, "False", ciRetryGuard),

                // CI Debug retry loop
                ConnectOutcome(ciRetryGuard, "True", incrementCiRetry),
                ConnectOutcome(ciRetryGuard, "False", finishCiFailed),
                Connect(incrementCiRetry, dispatchCiDebugging),
                Connect(dispatchCiDebugging, testingPipeline), // retry CI
```

**new_string**:
```
                // --- Step 9: CI Pipeline (sub-workflow) ---
                Connect(dispatchCiRetry, extractCiRetryCount),
                Connect(extractCiRetryCount, ciRetryPassed),
                ConnectOutcome(ciRetryPassed, "True", reviewFixCheck),
                ConnectOutcome(ciRetryPassed, "False", finishCiFailed),
```

### 2h. Update Review-Fix loop-back (Path 2)

**old_string**:
```
                ConnectOutcome(hasReviewComments, "True", testingPipeline), // re-run CI after fixes
```

**new_string**:
```
                ConnectOutcome(hasReviewComments, "True", dispatchCiRetry), // re-run CI after fixes
```

### 2i. Update Merge-Approval "test" signal loop-back (Path 3)

**old_string**:
```
                ConnectOutcome(testDecision, "True", testingPipeline), // re-run tests
```

**new_string**:
```
                ConnectOutcome(testDecision, "True", dispatchCiRetry), // re-run tests
```

### 2j. Remove `GetTestErrorOutput` helper method

**old_string**:
```
    private static string GetTestErrorOutput(IDictionary<string, object>? result)
    {
        if (result != null && result.TryGetValue("errorMessage", out var err))
            return err?.ToString() ?? "Testing pipeline failed";
        return "Testing pipeline failed with unknown error";
    }
```

**new_string**:
*(empty — delete the method)*

This helper is now in `CiWithDebugRetryWorkflow.cs`.

---

## Step 3: Build Verification

```bash
cd apps/tamma-elsa/src/Tamma.ElsaServer
dotnet build
```

Expected: 0 errors, 0 warnings.

---

## Step 4: Test Cases

**Test file**: `apps/tamma-elsa/tests/Tamma.ElsaServer.Tests/Workflows/CiWithDebugRetryWorkflowTests.cs`

### Test Methods

1. **`CiWithDebugRetryWorkflow_HasCorrectDefinitionId()`**
   - Instantiate `CiWithDebugRetryWorkflow`, verify `DefinitionId == "ci-with-debug-retry"`

2. **`CiWithDebugRetryWorkflow_HasCorrectName()`**
   - Verify `Name == "CI with Debug Retry"`

3. **`CiWithDebugRetryWorkflow_FlowchartHasExpectedActivityCount()`**
   - Expected: 9 activities (initInputs, testingPipeline, testsPassed, ciRetryGuard, incrementCiRetry, dispatchCiDebugging, finishPassOutputs, finishFailOutputs, finish)

4. **`CiWithDebugRetryWorkflow_FlowchartHasExpectedConnectionCount()`**
   - Expected: 10 connections

5. **`CiWithDebugRetryWorkflow_OutputsIncludeCiRetryCount()`**
   - Verify both finish sequences include a `SetOutput` with `OutputName == "ciRetryCount"`

6. **`SingleIssueCycleWorkflow_DispatchesCiWithDebugRetry()`**
   - Build `SingleIssueCycleWorkflow`, find a `DispatchWorkflow` with `WorkflowDefinitionId == "ci-with-debug-retry"`

7. **`SingleIssueCycleWorkflow_NoLongerContainsDirectTestingPipelineDispatch()`**
   - Build `SingleIssueCycleWorkflow`, verify no `DispatchWorkflow` with `WorkflowDefinitionId == "testing-pipeline"` exists

8. **`SingleIssueCycleWorkflow_ReviewFixLoopsBackToCiDispatch()`**
   - Build `SingleIssueCycleWorkflow`, find the `hasReviewComments` FlowDecision
   - Verify its "True" outcome connects to the `DispatchCiWithDebugRetry` activity

9. **`SingleIssueCycleWorkflow_MergeTestLoopsBackToCiDispatch()`**
   - Build `SingleIssueCycleWorkflow`, find the `testDecision` FlowDecision
   - Verify its "True" outcome connects to the `DispatchCiWithDebugRetry` activity

10. **`SingleIssueCycleWorkflow_NoCiRetryGuardInParent()`**
    - Verify no `FlowDecision` with `Id == "CiRetryGuard"` exists in the parent workflow

---

## Summary of Changes

| Action | File | What |
|--------|------|------|
| CREATE | `CiWithDebugRetryWorkflow.cs` | ~215 lines, complete new sub-workflow |
| DELETE | `SingleIssueCycleWorkflow.cs` | `debugResult` variable declaration |
| DELETE | `SingleIssueCycleWorkflow.cs` | 5 CI activities (~60 lines) |
| INSERT | `SingleIssueCycleWorkflow.cs` | `dispatchCiRetry` + `extractCiRetryCount` + `ciRetryPassed` (~40 lines) |
| MODIFY | `SingleIssueCycleWorkflow.cs` | Activities list: replace 5 entries with 3 |
| MODIFY | `SingleIssueCycleWorkflow.cs` | Connections: replace 8 CI connections with 4 |
| MODIFY | `SingleIssueCycleWorkflow.cs` | `prCreated` True target: `testingPipeline` -> `dispatchCiRetry` |
| MODIFY | `SingleIssueCycleWorkflow.cs` | `hasReviewComments` True target: `testingPipeline` -> `dispatchCiRetry` |
| MODIFY | `SingleIssueCycleWorkflow.cs` | `testDecision` True target: `testingPipeline` -> `dispatchCiRetry` |
| DELETE | `SingleIssueCycleWorkflow.cs` | `GetTestErrorOutput` helper method |
| CREATE | `CiWithDebugRetryWorkflowTests.cs` | ~150 lines test file |

### Net Effect on `SingleIssueCycleWorkflow.cs`

- **Variables removed**: 1 (`debugResult`)
- **Variables kept**: `ciRetryCount` (still needed for pass-through)
- **Activities removed**: 5 (`testingPipeline`, `testsPassed`, `ciRetryGuard`, `incrementCiRetry`, `dispatchCiDebugging`)
- **Activities added**: 3 (`dispatchCiRetry`, `extractCiRetryCount`, `ciRetryPassed`)
- **Connections removed**: 8 (CI section + 2 loop-back targets)
- **Connections added**: 6 (CI sub-workflow dispatch + extract + decision + 2 loop-back updates)
- **Helper methods removed**: 1 (`GetTestErrorOutput`)
- **Net lines removed**: ~40 lines
- **Estimated new line count**: ~658 lines (down from ~698 after Story 13.1)

### All Loop-Back Verification Table

| Path | Original Target | New Target | Connection Edit |
|------|----------------|------------|-----------------|
| PR Created -> CI | `testingPipeline` | `dispatchCiRetry` | Edit 2f |
| Review-fix loop | `testingPipeline` | `dispatchCiRetry` | Edit 2h |
| Merge "test" signal | `testingPipeline` | `dispatchCiRetry` | Edit 2i |
| CI debug retry (internal) | `testingPipeline` | *(now inside sub-workflow)* | N/A — handled in sub-workflow |

---

## Complete Exact Edit Operations

### Edit 1: Remove `debugResult` variable

**old_string**:
```
        var debugResult = builder.WithVariable<IDictionary<string, object>?>();

        var exitReason = builder.WithVariable<string>("ExitReason", "");
```

**new_string**:
```
        var exitReason = builder.WithVariable<string>("ExitReason", "");
```

### Edit 2: Replace CI activities section

(See section 2d above for complete old/new strings)

### Edit 3: Update Activities list

(See section 2e above)

### Edit 4: Update prCreated connection target

(See section 2f above)

### Edit 5: Replace CI connections section

(See section 2g above)

### Edit 6: Update review-fix loop-back

(See section 2h above)

### Edit 7: Update merge "test" signal loop-back

(See section 2i above)

### Edit 8: Remove `GetTestErrorOutput` helper

(See section 2j above)

---

## Verification Checklist

- [ ] `CiWithDebugRetryWorkflow.cs` created with `DefinitionId: "ci-with-debug-retry"`
- [ ] Sub-workflow contains 5 extracted activities + init + 2 finish sequences + finish terminal
- [ ] `ciRetryCount` passed as input and returned as output from sub-workflow
- [ ] `ciRetryCount` pass-through documented with code comment in both sub-workflow and parent
- [ ] `SingleIssueCycleWorkflow` dispatches `ci-with-debug-retry` instead of `testing-pipeline` directly
- [ ] `prCreated` True outcome targets `dispatchCiRetry`
- [ ] `hasReviewComments` True outcome targets `dispatchCiRetry` (review-fix loop)
- [ ] `testDecision` True outcome targets `dispatchCiRetry` (merge "test" signal)
- [ ] `extractCiRetryCount` captures the returned `ciRetryCount` from sub-workflow output
- [ ] `ciRetryPassed` True outcome targets `reviewFixCheck`
- [ ] `ciRetryPassed` False outcome targets `finishCiFailed`
- [ ] `debugResult` variable removed from parent
- [ ] `GetTestErrorOutput` removed from parent, exists in sub-workflow
- [ ] `dotnet build` succeeds with 0 errors
- [ ] No remaining references to `testingPipeline`, `testsPassed`, `ciRetryGuard`, `incrementCiRetry`, or `dispatchCiDebugging` in `SingleIssueCycleWorkflow.cs`
- [ ] All 3 loop-back paths verified in ELSA Studio visual inspection
