---
title: "Story 13.1: TDD Debug Retry Sub-Workflow - Implementation Plan"
sidebar:
  order: 130
---

## Overview

Extract the TDD retry loop (5 activities) from `SingleIssueCycleWorkflow.cs` into a new
`TddWithDebugRetryWorkflow.cs` sub-workflow. Also remove the dead `reviewFixAttempt` variable.

**Source file**: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` (783 lines)
**Pattern reference**: `TddWorkflow.cs`, `DebuggingWorkflow.cs` (existing sub-workflows dispatched by this workflow)

---

## Step 1: Remove Dead Variable `reviewFixAttempt`

**File**: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`
**Line**: 57

### Remove

```csharp
// Line 57 — DELETE this entire line:
        var reviewFixAttempt = builder.WithVariable<int>("ReviewFixAttempt", 0);
```

### Verification

- Build the project: `dotnet build apps/tamma-elsa/src/Tamma.ElsaServer/`
- Confirm no compile errors (variable is declared but never used)
- Grep the entire repo to confirm no references: `grep -r "reviewFixAttempt\|ReviewFixAttempt" apps/tamma-elsa/`

---

## Step 2: Create `TddWithDebugRetryWorkflow.cs`

**New file**: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TddWithDebugRetryWorkflow.cs`

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
/// TDD with Debug Retry sub-workflow — encapsulates the TDD cycle dispatch
/// with up to 3 debug retry iterations on failure.
///
/// Flow:
///   tddCycle -> tddSuccess?
///     YES -> finish(success=true)
///     NO  -> tddDebugGuard (< 3)?
///       NO  -> finish(success=false, errorMessage)
///       YES -> incrementTddDebug -> dispatchTddDebugging -> (loop to tddCycle)
///
/// Inputs:  storyId, planJson, repositoryUrl, branchName, skillLevel
/// Outputs: success (bool), errorMessage (string)
/// </summary>
public class TddWithDebugRetryWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "TDD with Debug Retry";
        builder.DefinitionId = "tdd-with-debug-retry";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Dispatches TDD cycle with up to 3 debug retry iterations on failure";

        // ================================================================
        // Variables
        // ================================================================
        var storyId = builder.WithVariable<string>("StoryId", "");
        var planJson = builder.WithVariable<string>("PlanJson", "");
        var repositoryUrl = builder.WithVariable<string>("RepositoryUrl", "");
        var branchName = builder.WithVariable<string>("BranchName", "");
        var skillLevel = builder.WithVariable<int>("SkillLevel", 5);
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0);
        var tddDebugAttempt = builder.WithVariable<int>("TddDebugAttempt", 0);

        // DispatchWorkflow result capture
        var tddResult = builder.WithVariable<IDictionary<string, object>?>();
        var debugResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // INIT: Capture inputs
        // ================================================================
        var initInputs = new SetVariable
        {
            Id = "InitTddRetryInputs",
            Name = "Init Inputs",
            Variable = storyId,
            Value = new Input<object?>(ctx =>
            {
                var plan = ctx.GetInput<string>("planJson");
                if (!string.IsNullOrEmpty(plan)) planJson.Set(ctx, plan);
                var repo = ctx.GetInput<string>("repositoryUrl");
                if (!string.IsNullOrEmpty(repo)) repositoryUrl.Set(ctx, repo);
                var branch = ctx.GetInput<string>("branchName");
                if (!string.IsNullOrEmpty(branch)) branchName.Set(ctx, branch);
                var skill = ctx.GetInput<int>("skillLevel");
                if (skill > 0) skillLevel.Set(ctx, skill);
                var issue = ctx.GetInput<int>("issueNumber");
                if (issue > 0) issueNumber.Set(ctx, issue);
                return (object)(ctx.GetInput<string>("storyId") ?? "");
            })
        };
        initInputs.SetDisplayText("Init Inputs");

        // ================================================================
        // TDD Cycle (dispatch to existing tdd-cycle workflow)
        // ================================================================
        var tddCycle = new DispatchWorkflow
        {
            Id = "DispatchTddCycle",
            Name = "TDD Cycle",
            WorkflowDefinitionId = new("tdd-cycle"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["sessionId"] = Guid.NewGuid(),
                ["storyId"] = storyId.Get(ctx),
                ["taskDescription"] = planJson.Get(ctx),
                ["taskFiles"] = new List<string>(),
                ["repositoryUrl"] = repositoryUrl.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["skillLevel"] = skillLevel.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(tddResult)
        };
        tddCycle.SetDisplayText("TDD Cycle");

        // ================================================================
        // TDD Success check
        // ================================================================
        var tddSuccess = new FlowDecision(ctx =>
        {
            var result = tddResult.Get(ctx);
            if (result != null && result.TryGetValue("success", out var s))
                return s is true || s?.ToString() == "True";
            return false;
        })
        { Id = "TddSuccess", Name = "TDD Passed?" };
        tddSuccess.SetDisplayText("TDD Passed?");

        // ================================================================
        // TDD Debug retry guard (< 3 attempts)
        // ================================================================
        var tddDebugGuard = new FlowDecision(ctx => tddDebugAttempt.Get(ctx) < 3)
        { Id = "TddDebugGuard", Name = "TDD Debug < 3?" };
        tddDebugGuard.SetDisplayText("TDD Debug < 3?");

        // ================================================================
        // Increment TDD debug counter
        // ================================================================
        var incrementTddDebug = new SetVariable
        {
            Id = "IncrTddDebug",
            Name = "Increment TDD Debug",
            Variable = tddDebugAttempt,
            Value = new Input<object?>(ctx => (object)(tddDebugAttempt.Get(ctx) + 1))
        };
        incrementTddDebug.SetDisplayText("Increment TDD Debug");

        // ================================================================
        // Dispatch debugging for TDD failure
        // ================================================================
        var dispatchTddDebugging = new DispatchWorkflow
        {
            Id = "DispatchTddDebugging",
            Name = "Debug TDD Failure",
            WorkflowDefinitionId = new("debugging"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["sessionId"] = Guid.NewGuid(),
                ["storyId"] = storyId.Get(ctx),
                ["debugContextMode"] = "TddFailure",
                ["errorOutput"] = GetTddErrorOutput(tddResult.Get(ctx)),
                ["repositoryUrl"] = repositoryUrl.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["skillLevel"] = skillLevel.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(debugResult)
        };
        dispatchTddDebugging.SetDisplayText("Debug TDD Failure");

        // ================================================================
        // Finish: Success outputs
        // ================================================================
        var finishSuccessOutputs = new Sequence
        {
            Id = "TddRetryFinishSuccess",
            Name = "Finish Success",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetTddRetrySuccess", Name = "Set Success", OutputName = new("success"), OutputValue = new(_ => (object)true) }, "Set Success"),
                WithLabel(new SetOutput { Id = "SetTddRetryErrorEmpty", Name = "Set Error Empty", OutputName = new("errorMessage"), OutputValue = new(_ => (object)"") }, "Set Error Empty")
            }
        };
        finishSuccessOutputs.SetDisplayText("Finish Success");

        // ================================================================
        // Finish: Failure outputs
        // ================================================================
        var finishFailureOutputs = new Sequence
        {
            Id = "TddRetryFinishFailure",
            Name = "Finish Failure",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetTddRetryFailed", Name = "Set Failed", OutputName = new("success"), OutputValue = new(_ => (object)false) }, "Set Failed"),
                WithLabel(new SetOutput { Id = "SetTddRetryErrorMsg", Name = "Set Error Message", OutputName = new("errorMessage"), OutputValue = new(_ => (object)"TDD debug retry limit reached (3 attempts)") }, "Set Error Message")
            }
        };
        finishFailureOutputs.SetDisplayText("Finish Failure");

        var finish = new Finish { Id = "TddRetryFinish", Name = "Complete: TDD Retry Done" };
        finish.SetDisplayText("Complete: TDD Retry Done");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "TddWithDebugRetryFlowchart",
            Name = "TDD with Debug Retry Flowchart",
            Start = initInputs,
            Activities =
            {
                initInputs,
                tddCycle, tddSuccess, tddDebugGuard,
                incrementTddDebug, dispatchTddDebugging,
                finishSuccessOutputs, finishFailureOutputs, finish
            },
            Connections =
            {
                // Init -> TDD Cycle
                Connect(initInputs, tddCycle),

                // TDD Cycle -> Success check
                Connect(tddCycle, tddSuccess),

                // TDD passed -> finish success
                ConnectOutcome(tddSuccess, "True", finishSuccessOutputs),

                // TDD failed -> debug guard
                ConnectOutcome(tddSuccess, "False", tddDebugGuard),

                // Debug retries remaining -> increment + debug
                ConnectOutcome(tddDebugGuard, "True", incrementTddDebug),
                ConnectOutcome(tddDebugGuard, "False", finishFailureOutputs),

                // Increment -> dispatch debugging -> loop back to TDD
                Connect(incrementTddDebug, dispatchTddDebugging),
                Connect(dispatchTddDebugging, tddCycle),

                // Both finish outputs -> terminal
                Connect(finishSuccessOutputs, finish),
                Connect(finishFailureOutputs, finish)
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

    private static string GetTddErrorOutput(IDictionary<string, object>? result)
    {
        if (result != null && result.TryGetValue("errorMessage", out var err))
            return err?.ToString() ?? "TDD cycle failed";
        return "TDD cycle failed with unknown error";
    }
}
```

---

## Step 3: Modify `SingleIssueCycleWorkflow.cs` — Remove Extracted Activities

**File**: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`

### 3a. Remove `tddDebugAttempt` variable declaration

**Line 55** — DELETE:

```csharp
        var tddDebugAttempt = builder.WithVariable<int>("TddDebugAttempt", 0);
```

After step 1, `reviewFixAttempt` on line 57 is already gone, so `tddDebugAttempt` is now on line 55. The three variable lines 55-56 (originally 55-57) become just line 55 (`ciRetryCount`).

### 3b. Remove `debugResult` variable declaration

**Line 70** (original) — DELETE:

```csharp
        var debugResult = builder.WithVariable<IDictionary<string, object>?>();
```

This variable was only used by `dispatchTddDebugging` and `dispatchCiDebugging`. After extracting TDD debugging to the sub-workflow, the TDD usage is gone. However, `dispatchCiDebugging` also uses `debugResult` (line 433). We must keep `debugResult` for now — it will be removed in Story 13.2.

**Decision**: Keep `debugResult` in this step. It is still used by CI debugging.

### 3c. Remove TDD cycle activities (lines 262-324)

**Lines 262-324** — DELETE the following activity definitions:

```csharp
        // ================================================================
        // Steps 5-7: TDD Cycle (existing workflow)
        // ================================================================
        var tddCycle = new DispatchWorkflow
        {
            Id = "DispatchTddCycle",
            Name = "TDD Cycle",
            WorkflowDefinitionId = new("tdd-cycle"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["sessionId"] = Guid.NewGuid(),
                ["storyId"] = $"adl-{issueNumber.Get(ctx)}",
                ["taskDescription"] = planJson.Get(ctx),
                ["taskFiles"] = new List<string>(),
                ["repositoryUrl"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["skillLevel"] = 5
            }),
            WaitForCompletion = new(true),
            Result = new(tddResult)
        };
        tddCycle.SetDisplayText("TDD Cycle");

        var tddSuccess = new FlowDecision(ctx =>
        {
            var result = tddResult.Get(ctx);
            if (result != null && result.TryGetValue("success", out var s))
                return s is true || s?.ToString() == "True";
            return false;
        })
        { Id = "TddSuccess", Name = "TDD Passed?" };
        tddSuccess.SetDisplayText("TDD Passed?");

        // TDD debug retry guard
        var tddDebugGuard = new FlowDecision(ctx => tddDebugAttempt.Get(ctx) < 3)
        { Id = "TddDebugGuard", Name = "TDD Debug < 3?" };
        tddDebugGuard.SetDisplayText("TDD Debug < 3?");

        var incrementTddDebug = new SetVariable
        {
            Id = "IncrTddDebug",
            Name = "Increment TDD Debug",
            Variable = tddDebugAttempt,
            Value = new Input<object?>(ctx => (object)(tddDebugAttempt.Get(ctx) + 1))
        };
        incrementTddDebug.SetDisplayText("Increment TDD Debug");

        var dispatchTddDebugging = new DispatchWorkflow
        {
            Id = "DispatchTddDebugging",
            Name = "Debug TDD Failure",
            WorkflowDefinitionId = new("debugging"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["sessionId"] = Guid.NewGuid(),
                ["storyId"] = $"adl-{issueNumber.Get(ctx)}",
                ["debugContextMode"] = "TddFailure",
                ["errorOutput"] = GetTddErrorOutput(tddResult.Get(ctx)),
                ["repositoryUrl"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["skillLevel"] = 5
            }),
            WaitForCompletion = new(true),
            Result = new(debugResult)
        };
        dispatchTddDebugging.SetDisplayText("Debug TDD Failure");
```

### 3d. Replace with new DispatchWorkflow + FlowDecision

**INSERT** at the same location (where the TDD section was):

```csharp
        // ================================================================
        // Steps 5-7: TDD Cycle (dispatched to sub-workflow)
        // ================================================================
        var dispatchTddRetry = new DispatchWorkflow
        {
            Id = "DispatchTddWithDebugRetry",
            Name = "TDD with Debug Retry",
            WorkflowDefinitionId = new("tdd-with-debug-retry"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["storyId"] = $"adl-{issueNumber.Get(ctx)}",
                ["planJson"] = planJson.Get(ctx),
                ["repositoryUrl"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["skillLevel"] = 5,
                ["issueNumber"] = issueNumber.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(tddResult)
        };
        dispatchTddRetry.SetDisplayText("TDD with Debug Retry");

        var tddRetrySuccess = new FlowDecision(ctx =>
        {
            var result = tddResult.Get(ctx);
            if (result != null && result.TryGetValue("success", out var s))
                return s is true || s?.ToString() == "True";
            return false;
        })
        { Id = "TddRetrySuccess", Name = "TDD Passed?" };
        tddRetrySuccess.SetDisplayText("TDD Passed?");
```

### 3e. Update the Flowchart Activities list

**Replace** in the Activities collection (lines 650-652):

```csharp
                // OLD (DELETE):
                // Steps 5-7: TDD Cycle + Debug
                tddCycle, tddSuccess, tddDebugGuard,
                incrementTddDebug, dispatchTddDebugging,
```

**With**:

```csharp
                // NEW:
                // Steps 5-7: TDD Cycle (sub-workflow)
                dispatchTddRetry, tddRetrySuccess,
```

### 3f. Update the Flowchart Connections

**Replace** connections (lines 700-712):

```csharp
                // OLD (DELETE):
                // --- Steps 5-7: TDD Cycle ---
                Connect(tddCycle, tddSuccess),
                ConnectOutcome(tddSuccess, "True", createPr),
                ConnectOutcome(tddSuccess, "False", tddDebugGuard),

                // TDD Debug retry loop
                ConnectOutcome(tddDebugGuard, "True", incrementTddDebug),
                ConnectOutcome(tddDebugGuard, "False", finishTddFailed),
                Connect(incrementTddDebug, dispatchTddDebugging),
                Connect(dispatchTddDebugging, tddCycle), // retry TDD
```

**With**:

```csharp
                // NEW:
                // --- Steps 5-7: TDD Cycle (sub-workflow) ---
                Connect(dispatchTddRetry, tddRetrySuccess),
                ConnectOutcome(tddRetrySuccess, "True", createPr),
                ConnectOutcome(tddRetrySuccess, "False", finishTddFailed),
```

### 3g. Update the Branch Created connection

**Line 700** — change target from `tddCycle` to `dispatchTddRetry`:

```csharp
                // OLD:
                ConnectOutcome(branchCreated, "True", tddCycle),

                // NEW:
                ConnectOutcome(branchCreated, "True", dispatchTddRetry),
```

### 3h. Remove `GetTddErrorOutput` helper method

**Lines 770-775** — DELETE:

```csharp
    private static string GetTddErrorOutput(IDictionary<string, object>? result)
    {
        if (result != null && result.TryGetValue("errorMessage", out var err))
            return err?.ToString() ?? "TDD cycle failed";
        return "TDD cycle failed with unknown error";
    }
```

This helper is now in `TddWithDebugRetryWorkflow.cs`.

---

## Step 4: Build Verification

```bash
cd apps/tamma-elsa/src/Tamma.ElsaServer
dotnet build
```

Expected: 0 errors, 0 warnings related to removed symbols.

---

## Step 5: Verify WorkflowVersions Auto-Bumps

No code changes needed. The `WorkflowVersions.ComputeSourceHash()` method at line 50 of `WorkflowVersions.cs` hashes all `*.cs` files in the `Workflows/` directory. Adding `TddWithDebugRetryWorkflow.cs` automatically changes the hash, causing all workflows to get a new version number on next startup.

---

## Step 6: Test Cases

**Test file**: `apps/tamma-elsa/tests/Tamma.ElsaServer.Tests/Workflows/TddWithDebugRetryWorkflowTests.cs`

This file needs a new test project or an addition to an existing one. Since `Tamma.ElsaServer.Tests` does not exist yet, either add it or put tests in `Tamma.Activities.Tests`. For now, define the test file with NUnit + FluentAssertions pattern matching the existing test project.

### Test Methods

1. **`TddWithDebugRetryWorkflow_HasCorrectDefinitionId()`**
   - Instantiate `TddWithDebugRetryWorkflow`, verify `DefinitionId == "tdd-with-debug-retry"`
   - Verifies workflow auto-registration will use the correct ID

2. **`TddWithDebugRetryWorkflow_HasCorrectName()`**
   - Verify `Name == "TDD with Debug Retry"`

3. **`TddWithDebugRetryWorkflow_FlowchartHasExpectedActivityCount()`**
   - Build the workflow, count activities in the root Flowchart
   - Expected: 9 activities (initInputs, tddCycle, tddSuccess, tddDebugGuard, incrementTddDebug, dispatchTddDebugging, finishSuccessOutputs, finishFailureOutputs, finish)

4. **`TddWithDebugRetryWorkflow_FlowchartHasExpectedConnectionCount()`**
   - Build the workflow, count connections
   - Expected: 10 connections

5. **`SingleIssueCycleWorkflow_DoesNotContainReviewFixAttempt()`**
   - Verify the string "ReviewFixAttempt" does not appear in the workflow source
   - Alternatively: build the workflow and verify no variable named "ReviewFixAttempt" exists

6. **`SingleIssueCycleWorkflow_DispatchesTddWithDebugRetry()`**
   - Build `SingleIssueCycleWorkflow`, find a `DispatchWorkflow` activity with `WorkflowDefinitionId == "tdd-with-debug-retry"`
   - Verify it exists

7. **`SingleIssueCycleWorkflow_NoLongerContainsDirectTddCycleDispatch()`**
   - Build `SingleIssueCycleWorkflow`, verify no `DispatchWorkflow` with `WorkflowDefinitionId == "tdd-cycle"` exists (it now goes through the sub-workflow)

---

## Summary of Changes

| Action | File | Lines Affected |
|--------|------|---------------|
| DELETE | `SingleIssueCycleWorkflow.cs` | Line 57 (`reviewFixAttempt`) |
| DELETE | `SingleIssueCycleWorkflow.cs` | Line 55 (`tddDebugAttempt`) |
| DELETE | `SingleIssueCycleWorkflow.cs` | Lines 262-324 (5 TDD activities) |
| DELETE | `SingleIssueCycleWorkflow.cs` | Lines 650-652 (activities list entries) |
| DELETE | `SingleIssueCycleWorkflow.cs` | Lines 700-712 (TDD connections) |
| MODIFY | `SingleIssueCycleWorkflow.cs` | Line 700 (branchCreated target) |
| DELETE | `SingleIssueCycleWorkflow.cs` | Lines 770-775 (`GetTddErrorOutput`) |
| INSERT | `SingleIssueCycleWorkflow.cs` | New `dispatchTddRetry` + `tddRetrySuccess` (~25 lines) |
| INSERT | `SingleIssueCycleWorkflow.cs` | New activities list entries (2 lines) |
| INSERT | `SingleIssueCycleWorkflow.cs` | New connections (3 lines) |
| CREATE | `TddWithDebugRetryWorkflow.cs` | ~190 lines (complete new file above) |
| CREATE | `TddWithDebugRetryWorkflowTests.cs` | ~100 lines (test file) |

### Net Effect on `SingleIssueCycleWorkflow.cs`

- **Variables removed**: 2 (`tddDebugAttempt`, `reviewFixAttempt`)
- **Activities removed**: 5 (`tddCycle`, `tddSuccess`, `tddDebugGuard`, `incrementTddDebug`, `dispatchTddDebugging`)
- **Activities added**: 2 (`dispatchTddRetry`, `tddRetrySuccess`)
- **Connections removed**: 8 (all TDD cycle connections)
- **Connections added**: 3 (dispatch -> decision -> createPr / finishTddFailed)
- **Helper methods removed**: 1 (`GetTddErrorOutput`)
- **Net lines removed**: ~85 lines
- **Estimated new line count**: ~698 lines (down from 783)

---

## Exact Edit Operations (for automated application)

### Edit 1: Remove `reviewFixAttempt` (line 57)

**old_string**:
```
        var tddDebugAttempt = builder.WithVariable<int>("TddDebugAttempt", 0);
        var ciRetryCount = builder.WithVariable<int>("CiRetryCount", 0);
        var reviewFixAttempt = builder.WithVariable<int>("ReviewFixAttempt", 0);
```

**new_string**:
```
        var tddDebugAttempt = builder.WithVariable<int>("TddDebugAttempt", 0);
        var ciRetryCount = builder.WithVariable<int>("CiRetryCount", 0);
```

### Edit 2: Remove `tddDebugAttempt` (now line 55)

**old_string**:
```
        var tddDebugAttempt = builder.WithVariable<int>("TddDebugAttempt", 0);
        var ciRetryCount = builder.WithVariable<int>("CiRetryCount", 0);
```

**new_string**:
```
        var ciRetryCount = builder.WithVariable<int>("CiRetryCount", 0);
```

### Edit 3: Replace TDD activities section (lines 259-324)

**old_string**:
```
        // ================================================================
        // Steps 5-7: TDD Cycle (existing workflow)
        // ================================================================
        var tddCycle = new DispatchWorkflow
        {
            Id = "DispatchTddCycle",
            Name = "TDD Cycle",
            WorkflowDefinitionId = new("tdd-cycle"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["sessionId"] = Guid.NewGuid(),
                ["storyId"] = $"adl-{issueNumber.Get(ctx)}",
                ["taskDescription"] = planJson.Get(ctx),
                ["taskFiles"] = new List<string>(),
                ["repositoryUrl"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["skillLevel"] = 5
            }),
            WaitForCompletion = new(true),
            Result = new(tddResult)
        };
        tddCycle.SetDisplayText("TDD Cycle");

        var tddSuccess = new FlowDecision(ctx =>
        {
            var result = tddResult.Get(ctx);
            if (result != null && result.TryGetValue("success", out var s))
                return s is true || s?.ToString() == "True";
            return false;
        })
        { Id = "TddSuccess", Name = "TDD Passed?" };
        tddSuccess.SetDisplayText("TDD Passed?");

        // TDD debug retry guard
        var tddDebugGuard = new FlowDecision(ctx => tddDebugAttempt.Get(ctx) < 3)
        { Id = "TddDebugGuard", Name = "TDD Debug < 3?" };
        tddDebugGuard.SetDisplayText("TDD Debug < 3?");

        var incrementTddDebug = new SetVariable
        {
            Id = "IncrTddDebug",
            Name = "Increment TDD Debug",
            Variable = tddDebugAttempt,
            Value = new Input<object?>(ctx => (object)(tddDebugAttempt.Get(ctx) + 1))
        };
        incrementTddDebug.SetDisplayText("Increment TDD Debug");

        var dispatchTddDebugging = new DispatchWorkflow
        {
            Id = "DispatchTddDebugging",
            Name = "Debug TDD Failure",
            WorkflowDefinitionId = new("debugging"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["sessionId"] = Guid.NewGuid(),
                ["storyId"] = $"adl-{issueNumber.Get(ctx)}",
                ["debugContextMode"] = "TddFailure",
                ["errorOutput"] = GetTddErrorOutput(tddResult.Get(ctx)),
                ["repositoryUrl"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["skillLevel"] = 5
            }),
            WaitForCompletion = new(true),
            Result = new(debugResult)
        };
        dispatchTddDebugging.SetDisplayText("Debug TDD Failure");
```

**new_string**:
```
        // ================================================================
        // Steps 5-7: TDD Cycle (dispatched to sub-workflow)
        // ================================================================
        var dispatchTddRetry = new DispatchWorkflow
        {
            Id = "DispatchTddWithDebugRetry",
            Name = "TDD with Debug Retry",
            WorkflowDefinitionId = new("tdd-with-debug-retry"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["storyId"] = $"adl-{issueNumber.Get(ctx)}",
                ["planJson"] = planJson.Get(ctx),
                ["repositoryUrl"] = repository.Get(ctx),
                ["branchName"] = branchName.Get(ctx),
                ["skillLevel"] = 5,
                ["issueNumber"] = issueNumber.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(tddResult)
        };
        dispatchTddRetry.SetDisplayText("TDD with Debug Retry");

        var tddRetrySuccess = new FlowDecision(ctx =>
        {
            var result = tddResult.Get(ctx);
            if (result != null && result.TryGetValue("success", out var s))
                return s is true || s?.ToString() == "True";
            return false;
        })
        { Id = "TddRetrySuccess", Name = "TDD Passed?" };
        tddRetrySuccess.SetDisplayText("TDD Passed?");
```

### Edit 4: Update Activities list

**old_string**:
```
                // Steps 5-7: TDD Cycle + Debug
                tddCycle, tddSuccess, tddDebugGuard,
                incrementTddDebug, dispatchTddDebugging,
```

**new_string**:
```
                // Steps 5-7: TDD Cycle (sub-workflow)
                dispatchTddRetry, tddRetrySuccess,
```

### Edit 5: Update branchCreated connection target + TDD connections

**old_string**:
```
                ConnectOutcome(branchCreated, "True", tddCycle),
                ConnectOutcome(branchCreated, "False", finishError),

                // --- Steps 5-7: TDD Cycle ---
                Connect(tddCycle, tddSuccess),
                ConnectOutcome(tddSuccess, "True", createPr),
                ConnectOutcome(tddSuccess, "False", tddDebugGuard),

                // TDD Debug retry loop
                ConnectOutcome(tddDebugGuard, "True", incrementTddDebug),
                ConnectOutcome(tddDebugGuard, "False", finishTddFailed),
                Connect(incrementTddDebug, dispatchTddDebugging),
                Connect(dispatchTddDebugging, tddCycle), // retry TDD
```

**new_string**:
```
                ConnectOutcome(branchCreated, "True", dispatchTddRetry),
                ConnectOutcome(branchCreated, "False", finishError),

                // --- Steps 5-7: TDD Cycle (sub-workflow) ---
                Connect(dispatchTddRetry, tddRetrySuccess),
                ConnectOutcome(tddRetrySuccess, "True", createPr),
                ConnectOutcome(tddRetrySuccess, "False", finishTddFailed),
```

### Edit 6: Remove `GetTddErrorOutput` helper

**old_string**:
```
    private static string GetTddErrorOutput(IDictionary<string, object>? result)
    {
        if (result != null && result.TryGetValue("errorMessage", out var err))
            return err?.ToString() ?? "TDD cycle failed";
        return "TDD cycle failed with unknown error";
    }

    private static string GetTestErrorOutput(IDictionary<string, object>? result)
```

**new_string**:
```
    private static string GetTestErrorOutput(IDictionary<string, object>? result)
```

---

## Verification Checklist

- [ ] `reviewFixAttempt` variable removed from `SingleIssueCycleWorkflow.cs`
- [ ] `tddDebugAttempt` variable removed from `SingleIssueCycleWorkflow.cs`
- [ ] `TddWithDebugRetryWorkflow.cs` created with `DefinitionId: "tdd-with-debug-retry"`
- [ ] Sub-workflow contains 5 extracted activities + init + 2 finish sequences + finish terminal
- [ ] `SingleIssueCycleWorkflow` dispatches `tdd-with-debug-retry` instead of `tdd-cycle` directly
- [ ] `branchCreated` True outcome targets `dispatchTddRetry` (not `tddCycle`)
- [ ] `tddRetrySuccess` True outcome targets `createPr`
- [ ] `tddRetrySuccess` False outcome targets `finishTddFailed`
- [ ] `GetTddErrorOutput` removed from `SingleIssueCycleWorkflow`, exists in `TddWithDebugRetryWorkflow`
- [ ] `dotnet build` succeeds with 0 errors
- [ ] `WorkflowVersions` hash changes (automatic — new .cs file in Workflows/)
- [ ] No remaining references to `tddCycle`, `tddDebugGuard`, `incrementTddDebug`, or `dispatchTddDebugging` in `SingleIssueCycleWorkflow.cs`
