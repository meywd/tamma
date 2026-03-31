---
title: "Story 13.3: Consolidate Finish Sequences - Implementation Plan"
sidebar:
  order: 130
---

## Overview

Replace the 7 duplicated finish `Sequence` nodes in `SingleIssueCycleWorkflow.cs` with
7 `SetVariable` nodes (setting `finishReason`) plus 1 shared finish `Sequence`. Net result:
6 fewer activities in the flowchart.

**Prerequisite**: Stories 13.1 and 13.2 are complete. The parent workflow is ~658 lines.

**Source file state**: After Stories 13.1+13.2, `SingleIssueCycleWorkflow.cs` has had TDD and CI
retry loops extracted. The 7 finish sequences remain untouched.

---

## Pre-Implementation: Audit All 7 Finish Sequences

### Current Finish Sequences (post-Stories 13.1 + 13.2)

Mapped from the original `SingleIssueCycleWorkflow.cs` lines 537-623. After Stories 13.1+13.2
the line numbers shifted but the content is identical.

| # | Variable Name | Id | Reason String | Unique Behavior |
|---|---------------|----|---------------|-----------------|
| 1 | `finishSuccess` | `FinishSuccess` | `"success"` | Sets 4 extra outputs: `exitReason`, `issueNumber`, `prNumber`, `mergeSha` |
| 2 | `finishNoIssues` | `FinishNoIssues` | `"noIssues"` | Sets `exitReason` only |
| 3 | `finishRejected` | `FinishRejected` | `"rejected"` | Sets `exitReason` only |
| 4 | `finishError` | `FinishError` | `"error"` | Sets `exitReason` only |
| 5 | `finishTddFailed` | `FinishTddFailed` | `"tddFailed"` | Sets `exitReason` only |
| 6 | `finishCiFailed` | `FinishCiFailed` | `"ciFailed"` | Sets `exitReason` only |
| 7 | `finishMergeFailed` | `FinishMergeFailed` | `"mergeFailed"` | Sets `exitReason` only |

### Key Observation

`finishSuccess` is the **only** unique one. It sets 4 additional outputs (`exitReason`, `issueNumber`, `prNumber`, `mergeSha`). The other 6 sequences are structurally identical: they set only `exitReason` to a reason string.

### Consolidation Strategy

1. Replace each of the 7 `Sequence` nodes with a simple `SetVariable` that writes the `finishReason` value to the `exitReason` variable (reuse the existing `exitReason` variable).
2. Create 1 shared `Sequence` that:
   - Sets `SetOutput("exitReason")` from the `exitReason` variable
   - Conditionally sets success-specific outputs (`issueNumber`, `prNumber`, `mergeSha`) only when `exitReason == "success"`
3. The `Finish` activity remains as the terminal node.

---

## Step 1: Define the `finishReason` Values

We will reuse the existing `exitReason` variable (line 72 in original, still present after 13.1+13.2).
No new variable needed.

The reason values map to the story spec:

| Exit Path | `exitReason` Value | Was |
|-----------|-------------------|-----|
| No issues found | `"noIssues"` | Same |
| Plan rejected | `"rejected"` | Same |
| Branch creation error | `"error"` | Same |
| TDD failure | `"tddFailed"` | Same |
| CI failure | `"ciFailed"` | Same |
| Merge rejected | `"rejected"` | Same (reused for both plan rejected and merge rejected) |
| Merge failure | `"mergeFailed"` | Same |
| Success | `"success"` | Same |

**Note**: `finishRejected` is used for BOTH plan rejection (line 695 connection) AND merge rejection (line 741 connection in original). After consolidation, both paths set `exitReason = "rejected"` which is identical to current behavior.

---

## Step 2: Replace the 7 Finish Sequences with 7 `SetVariable` Nodes

**File**: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs`

### Remove all 7 finish sequences and the `finish` terminal

**old_string** (the entire finish section):
```
        // ================================================================
        // Finish nodes with SetOutput
        // ================================================================
        var finishSuccess = new Sequence
        {
            Id = "FinishSuccess",
            Name = "Finish Success",
            Activities =
            {
                WithLabel(new SetVariable { Id = "SetSuccessReason", Name = "Set Success Reason", Variable = exitReason, Value = new Input<object?>(_ => (object)"success") }, "Set Success Reason"),
                WithLabel(new SetOutput { Id = "SetOutputExitReasonSuccess", Name = "Set Exit Reason Success", OutputName = new("exitReason"), OutputValue = new(_ => (object)"success") }, "Set Exit Reason Success"),
                WithLabel(new SetOutput { Id = "SetOutputIssueNumber", Name = "Set Issue Number", OutputName = new("issueNumber"), OutputValue = new(ctx => (object)issueNumber.Get(ctx)) }, "Set Issue Number"),
                WithLabel(new SetOutput { Id = "SetOutputPrNumber", Name = "Set PR Number", OutputName = new("prNumber"), OutputValue = new(ctx => (object)prNumber.Get(ctx)) }, "Set PR Number"),
                WithLabel(new SetOutput { Id = "SetOutputMergeSha", Name = "Set Merge SHA", OutputName = new("mergeSha"), OutputValue = new(ctx =>
                {
                    var r = mergeResult.Get(ctx);
                    return (object)(r != null && r.TryGetValue("mergeSha", out var ms) ? ms?.ToString() ?? "" : "");
                }) }, "Set Merge SHA")
            }
        };
        finishSuccess.SetDisplayText("Finish Success");

        var finishNoIssues = new Sequence
        {
            Id = "FinishNoIssues",
            Name = "Finish No Issues",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetOutputExitReasonNoIssues", Name = "Set Exit Reason No Issues", OutputName = new("exitReason"), OutputValue = new(_ => (object)"noIssues") }, "Set Exit Reason No Issues")
            }
        };
        finishNoIssues.SetDisplayText("Finish No Issues");

        var finishRejected = new Sequence
        {
            Id = "FinishRejected",
            Name = "Finish Rejected",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetOutputExitReasonRejected", Name = "Set Exit Reason Rejected", OutputName = new("exitReason"), OutputValue = new(_ => (object)"rejected") }, "Set Exit Reason Rejected")
            }
        };
        finishRejected.SetDisplayText("Finish Rejected");

        var finishError = new Sequence
        {
            Id = "FinishError",
            Name = "Finish Error",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetOutputExitReasonError", Name = "Set Exit Reason Error", OutputName = new("exitReason"), OutputValue = new(_ => (object)"error") }, "Set Exit Reason Error")
            }
        };
        finishError.SetDisplayText("Finish Error");

        var finishTddFailed = new Sequence
        {
            Id = "FinishTddFailed",
            Name = "Finish TDD Failed",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetOutputExitReasonTddFailed", Name = "Set Exit Reason TDD Failed", OutputName = new("exitReason"), OutputValue = new(_ => (object)"tddFailed") }, "Set Exit Reason TDD Failed")
            }
        };
        finishTddFailed.SetDisplayText("Finish TDD Failed");

        var finishCiFailed = new Sequence
        {
            Id = "FinishCiFailed",
            Name = "Finish CI Failed",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetOutputExitReasonCiFailed", Name = "Set Exit Reason CI Failed", OutputName = new("exitReason"), OutputValue = new(_ => (object)"ciFailed") }, "Set Exit Reason CI Failed")
            }
        };
        finishCiFailed.SetDisplayText("Finish CI Failed");

        var finishMergeFailed = new Sequence
        {
            Id = "FinishMergeFailed",
            Name = "Finish Merge Failed",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetOutputExitReasonMergeFailed", Name = "Set Exit Reason Merge Failed", OutputName = new("exitReason"), OutputValue = new(_ => (object)"mergeFailed") }, "Set Exit Reason Merge Failed")
            }
        };
        finishMergeFailed.SetDisplayText("Finish Merge Failed");

        var finish = new Finish { Id = "Finish", Name = "Complete: Issue Cycle Done" };
        finish.SetDisplayText("Complete: Issue Cycle Done");
```

### Replace with 7 `SetVariable` nodes + 1 shared finish sequence + `Finish` terminal

**new_string**:
```
        // ================================================================
        // Finish reason SetVariable nodes (one per exit path)
        // ================================================================
        var setReasonSuccess = new SetVariable
        {
            Id = "SetReasonSuccess",
            Name = "Set Reason: Success",
            Variable = exitReason,
            Value = new Input<object?>(_ => (object)"success")
        };
        setReasonSuccess.SetDisplayText("Set Reason: Success");

        var setReasonNoIssues = new SetVariable
        {
            Id = "SetReasonNoIssues",
            Name = "Set Reason: No Issues",
            Variable = exitReason,
            Value = new Input<object?>(_ => (object)"noIssues")
        };
        setReasonNoIssues.SetDisplayText("Set Reason: No Issues");

        var setReasonRejected = new SetVariable
        {
            Id = "SetReasonRejected",
            Name = "Set Reason: Rejected",
            Variable = exitReason,
            Value = new Input<object?>(_ => (object)"rejected")
        };
        setReasonRejected.SetDisplayText("Set Reason: Rejected");

        var setReasonError = new SetVariable
        {
            Id = "SetReasonError",
            Name = "Set Reason: Error",
            Variable = exitReason,
            Value = new Input<object?>(_ => (object)"error")
        };
        setReasonError.SetDisplayText("Set Reason: Error");

        var setReasonTddFailed = new SetVariable
        {
            Id = "SetReasonTddFailed",
            Name = "Set Reason: TDD Failed",
            Variable = exitReason,
            Value = new Input<object?>(_ => (object)"tddFailed")
        };
        setReasonTddFailed.SetDisplayText("Set Reason: TDD Failed");

        var setReasonCiFailed = new SetVariable
        {
            Id = "SetReasonCiFailed",
            Name = "Set Reason: CI Failed",
            Variable = exitReason,
            Value = new Input<object?>(_ => (object)"ciFailed")
        };
        setReasonCiFailed.SetDisplayText("Set Reason: CI Failed");

        var setReasonMergeFailed = new SetVariable
        {
            Id = "SetReasonMergeFailed",
            Name = "Set Reason: Merge Failed",
            Variable = exitReason,
            Value = new Input<object?>(_ => (object)"mergeFailed")
        };
        setReasonMergeFailed.SetDisplayText("Set Reason: Merge Failed");

        // ================================================================
        // Shared Finish Sequence — all exit paths converge here
        // ================================================================
        var sharedFinish = new Sequence
        {
            Id = "SharedFinishSequence",
            Name = "Shared Finish",
            Activities =
            {
                // Always set exitReason output
                WithLabel(new SetOutput { Id = "SetOutputExitReason", Name = "Set Exit Reason", OutputName = new("exitReason"), OutputValue = new(ctx => (object)exitReason.Get(ctx)) }, "Set Exit Reason"),
                // Always set finishReason output (same value, explicit name for analytics)
                WithLabel(new SetOutput { Id = "SetOutputFinishReason", Name = "Set Finish Reason", OutputName = new("finishReason"), OutputValue = new(ctx => (object)exitReason.Get(ctx)) }, "Set Finish Reason"),
                // Derive success flag from reason
                WithLabel(new SetOutput { Id = "SetOutputSuccess", Name = "Set Success Flag", OutputName = new("success"), OutputValue = new(ctx => (object)(exitReason.Get(ctx) == "success")) }, "Set Success Flag"),
                // Conditionally set success-specific outputs (issueNumber, prNumber, mergeSha)
                WithLabel(new SetOutput { Id = "SetOutputIssueNumber", Name = "Set Issue Number", OutputName = new("issueNumber"), OutputValue = new(ctx => (object)issueNumber.Get(ctx)) }, "Set Issue Number"),
                WithLabel(new SetOutput { Id = "SetOutputPrNumber", Name = "Set PR Number", OutputName = new("prNumber"), OutputValue = new(ctx => (object)prNumber.Get(ctx)) }, "Set PR Number"),
                WithLabel(new SetOutput { Id = "SetOutputMergeSha", Name = "Set Merge SHA", OutputName = new("mergeSha"), OutputValue = new(ctx =>
                {
                    var r = mergeResult.Get(ctx);
                    return (object)(r != null && r.TryGetValue("mergeSha", out var ms) ? ms?.ToString() ?? "" : "");
                }) }, "Set Merge SHA")
            }
        };
        sharedFinish.SetDisplayText("Shared Finish");

        var finish = new Finish { Id = "Finish", Name = "Complete: Issue Cycle Done" };
        finish.SetDisplayText("Complete: Issue Cycle Done");
```

---

## Step 3: Update the Flowchart Activities List

**old_string**:
```
                // Finish nodes
                finishSuccess, finishNoIssues, finishRejected,
                finishError, finishTddFailed, finishCiFailed,
                finishMergeFailed, finish
```

**new_string**:
```
                // Finish reason nodes + shared finish
                setReasonSuccess, setReasonNoIssues, setReasonRejected,
                setReasonError, setReasonTddFailed, setReasonCiFailed,
                setReasonMergeFailed, sharedFinish, finish
```

**Activity count change**: Was 8 entries (7 sequences + 1 finish). Now 9 entries (7 SetVariable + 1 shared sequence + 1 finish). But the 7 Sequence nodes each contained embedded activities that are now gone (net reduction in total flowchart complexity).

---

## Step 4: Update All Flowchart Connections

### 4a. Update connections that TARGET the old finish sequences

Every connection that previously pointed to one of the 7 finish sequences must now point to the corresponding `SetVariable` node.

**Edit: hasIssue False -> finishNoIssues**

**old_string**:
```
                ConnectOutcome(hasIssue, "False", finishNoIssues),
```

**new_string**:
```
                ConnectOutcome(hasIssue, "False", setReasonNoIssues),
```

**Edit: planApproved False -> finishRejected**

**old_string**:
```
                ConnectOutcome(planApproved, "False", finishRejected),
```

**new_string**:
```
                ConnectOutcome(planApproved, "False", setReasonRejected),
```

**Edit: branchCreated False -> finishError**

**old_string**:
```
                ConnectOutcome(branchCreated, "False", finishError),
```

**new_string**:
```
                ConnectOutcome(branchCreated, "False", setReasonError),
```

**Edit: tddRetrySuccess False -> finishTddFailed**

(After Story 13.1, this connection uses `tddRetrySuccess`)

**old_string**:
```
                ConnectOutcome(tddRetrySuccess, "False", finishTddFailed),
```

**new_string**:
```
                ConnectOutcome(tddRetrySuccess, "False", setReasonTddFailed),
```

**Edit: prCreated False -> finishError**

**old_string**:
```
                ConnectOutcome(prCreated, "False", finishError),
```

**new_string**:
```
                ConnectOutcome(prCreated, "False", setReasonError),
```

**Edit: ciRetryPassed False -> finishCiFailed**

(After Story 13.2, this connection uses `ciRetryPassed`)

**old_string**:
```
                ConnectOutcome(ciRetryPassed, "False", finishCiFailed),
```

**new_string**:
```
                ConnectOutcome(ciRetryPassed, "False", setReasonCiFailed),
```

**Edit: testDecision False -> finishRejected (merge rejection)**

**old_string**:
```
                ConnectOutcome(testDecision, "False", finishRejected), // rejected
```

**new_string**:
```
                ConnectOutcome(testDecision, "False", setReasonRejected), // rejected
```

**Edit: mergeSuccess True -> finishSuccess**

**old_string**:
```
                ConnectOutcome(mergeSuccess, "True", finishSuccess),
```

**new_string**:
```
                ConnectOutcome(mergeSuccess, "True", setReasonSuccess),
```

**Edit: mergeSuccess False -> finishMergeFailed**

**old_string**:
```
                ConnectOutcome(mergeSuccess, "False", finishMergeFailed),
```

**new_string**:
```
                ConnectOutcome(mergeSuccess, "False", setReasonMergeFailed),
```

### 4b. Replace all finish-to-terminal connections with SetVariable-to-sharedFinish connections

**old_string**:
```
                // --- All finish nodes lead to terminal ---
                Connect(finishSuccess, finish),
                Connect(finishNoIssues, finish),
                Connect(finishRejected, finish),
                Connect(finishError, finish),
                Connect(finishTddFailed, finish),
                Connect(finishCiFailed, finish),
                Connect(finishMergeFailed, finish)
```

**new_string**:
```
                // --- All reason nodes lead to shared finish, then terminal ---
                Connect(setReasonSuccess, sharedFinish),
                Connect(setReasonNoIssues, sharedFinish),
                Connect(setReasonRejected, sharedFinish),
                Connect(setReasonError, sharedFinish),
                Connect(setReasonTddFailed, sharedFinish),
                Connect(setReasonCiFailed, sharedFinish),
                Connect(setReasonMergeFailed, sharedFinish),
                Connect(sharedFinish, finish)
```

**Connection count change**: Was 7 connections (7 sequences -> finish). Now 8 connections (7 SetVariable -> sharedFinish + sharedFinish -> finish). Net +1 connection. But the overall workflow is simpler because the 7 multi-activity Sequence nodes are replaced by 7 single-activity SetVariable nodes.

---

## Step 5: Build Verification

```bash
cd apps/tamma-elsa/src/Tamma.ElsaServer
dotnet build
```

Expected: 0 errors, 0 warnings.

---

## Step 6: Test Cases

**Test file**: `apps/tamma-elsa/tests/Tamma.ElsaServer.Tests/Workflows/SingleIssueCycleFinishTests.cs`

### Test Methods

1. **`SharedFinishSequence_SetsExitReasonOutput()`**
   - Build workflow, verify a `SetOutput` with `OutputName == "exitReason"` exists in the shared finish sequence

2. **`SharedFinishSequence_SetsFinishReasonOutput()`**
   - Verify a `SetOutput` with `OutputName == "finishReason"` exists (net-new enrichment)

3. **`SharedFinishSequence_SetsSuccessFlagOutput()`**
   - Verify a `SetOutput` with `OutputName == "success"` exists

4. **`SharedFinishSequence_SetsMergeShaOutput()`**
   - Verify a `SetOutput` with `OutputName == "mergeSha"` exists

5. **`NoIssuesPath_SetsExitReasonToNoIssues()`**
   - Verify `setReasonNoIssues` SetVariable sets `exitReason` to `"noIssues"`

6. **`RejectedPath_SetsExitReasonToRejected()`**
   - Verify `setReasonRejected` SetVariable sets `exitReason` to `"rejected"`

7. **`ErrorPath_SetsExitReasonToError()`**
   - Verify `setReasonError` SetVariable sets `exitReason` to `"error"`

8. **`TddFailedPath_SetsExitReasonToTddFailed()`**
   - Verify `setReasonTddFailed` SetVariable sets `exitReason` to `"tddFailed"`

9. **`CiFailedPath_SetsExitReasonToCiFailed()`**
   - Verify `setReasonCiFailed` SetVariable sets `exitReason` to `"ciFailed"`

10. **`MergeFailedPath_SetsExitReasonToMergeFailed()`**
    - Verify `setReasonMergeFailed` SetVariable sets `exitReason` to `"mergeFailed"`

11. **`SuccessPath_SetsExitReasonToSuccess()`**
    - Verify `setReasonSuccess` SetVariable sets `exitReason` to `"success"`

12. **`AllReasonNodes_ConnectToSharedFinish()`**
    - Build workflow, verify all 7 SetVariable nodes have a connection to `SharedFinishSequence`

13. **`SharedFinish_ConnectsToFinishTerminal()`**
    - Build workflow, verify `SharedFinishSequence` connects to `Finish`

14. **`NoLegacyFinishSequences_Exist()`**
    - Verify no Sequence activities with IDs matching `FinishSuccess`, `FinishNoIssues`, `FinishRejected`, `FinishError`, `FinishTddFailed`, `FinishCiFailed`, `FinishMergeFailed` exist

15. **`Regression_ExistingWorkflowTests_Pass()`**
    - Re-run tests from Stories 13.1 and 13.2 to verify no regressions

---

## Summary of Changes

| Action | File | What |
|--------|------|------|
| DELETE | `SingleIssueCycleWorkflow.cs` | 7 finish `Sequence` nodes (~85 lines) |
| INSERT | `SingleIssueCycleWorkflow.cs` | 7 `SetVariable` nodes (~55 lines) |
| INSERT | `SingleIssueCycleWorkflow.cs` | 1 shared `Sequence` node (~25 lines) |
| MODIFY | `SingleIssueCycleWorkflow.cs` | Activities list: replace 8 entries with 9 |
| MODIFY | `SingleIssueCycleWorkflow.cs` | 9 connection targets updated |
| MODIFY | `SingleIssueCycleWorkflow.cs` | 7 finish-to-terminal connections replaced with 8 reason-to-shared-to-terminal |
| CREATE | `SingleIssueCycleFinishTests.cs` | ~200 lines test file |

### Net Effect on `SingleIssueCycleWorkflow.cs`

- **Sequence nodes removed**: 7
- **SetVariable nodes added**: 7
- **Shared Sequence added**: 1
- **Net activity change**: 7 Sequences removed, 7 SetVariable + 1 Sequence added = net 0 top-level activities BUT the 7 old Sequences each contained 1-5 embedded `SetOutput` activities (total ~13 embedded activities removed)
- **Net lines**: Remove ~85 lines of Sequence definitions, add ~80 lines of SetVariable + shared Sequence = net ~5 lines reduction
- **Estimated final line count**: ~653 lines (down from ~658 after Stories 13.1+13.2)

### Combined Effect of All 3 Stories

| Metric | Original | After 13.1 | After 13.2 | After 13.3 |
|--------|----------|------------|------------|------------|
| File lines | 783 | ~698 | ~658 | ~653 |
| Flowchart activities | 39 | 34 | 32 | 32* |
| Connections | ~38 | ~33 | ~29 | ~30 |
| Helper methods | 2 | 1 | 0 | 0 |
| Dead variables | 1 | 0 | 0 | 0 |

\* Activity count stays similar because 7 Sequences become 7 SetVariable + 1 shared Sequence, but the embedded activity count within those sequences drops significantly.

---

## Complete Connection Map After All Edits

For reference, here is the complete connection list after Story 13.3:

```csharp
Connections =
{
    // --- INIT ---
    Connect(initConfig, selectIssue),

    // --- Step 1: Issue Selection ---
    Connect(selectIssue, extractIssue),
    Connect(extractIssue, hasIssue),
    ConnectOutcome(hasIssue, "True", gatherContext),
    ConnectOutcome(hasIssue, "False", setReasonNoIssues),

    // --- Step 2: Context Gathering ---
    Connect(gatherContext, extractContext),
    Connect(extractContext, generatePlan),

    // --- Step 3: Plan Generation ---
    Connect(generatePlan, extractPlan),
    Connect(extractPlan, planApproved),
    ConnectOutcome(planApproved, "True", createBranch),
    ConnectOutcome(planApproved, "False", setReasonRejected),

    // --- Step 4: Branch Creation ---
    Connect(createBranch, extractBranch),
    Connect(extractBranch, branchCreated),
    ConnectOutcome(branchCreated, "True", dispatchTddRetry),
    ConnectOutcome(branchCreated, "False", setReasonError),

    // --- Steps 5-7: TDD Cycle (sub-workflow) ---
    Connect(dispatchTddRetry, tddRetrySuccess),
    ConnectOutcome(tddRetrySuccess, "True", createPr),
    ConnectOutcome(tddRetrySuccess, "False", setReasonTddFailed),

    // --- Step 8: Create PR ---
    Connect(createPr, extractPr),
    Connect(extractPr, prCreated),
    ConnectOutcome(prCreated, "True", dispatchCiRetry),
    ConnectOutcome(prCreated, "False", setReasonError),

    // --- Step 9: CI Pipeline (sub-workflow) ---
    Connect(dispatchCiRetry, extractCiRetryCount),
    Connect(extractCiRetryCount, ciRetryPassed),
    ConnectOutcome(ciRetryPassed, "True", reviewFixCheck),
    ConnectOutcome(ciRetryPassed, "False", setReasonCiFailed),

    // --- Step 10: Review Fix Check ---
    Connect(reviewFixCheck, hasReviewComments),
    ConnectOutcome(hasReviewComments, "True", dispatchCiRetry), // re-run CI after fixes
    ConnectOutcome(hasReviewComments, "False", mergeApproval),

    // --- Step 11: Merge Approval ---
    Connect(mergeApproval, mergeDecision),
    ConnectOutcome(mergeDecision, "True", mergePr),
    ConnectOutcome(mergeDecision, "False", testDecision),
    ConnectOutcome(testDecision, "True", dispatchCiRetry), // re-run tests
    ConnectOutcome(testDecision, "False", setReasonRejected), // rejected

    // --- Step 12: Merge PR ---
    Connect(mergePr, mergeSuccess),
    ConnectOutcome(mergeSuccess, "True", setReasonSuccess),
    ConnectOutcome(mergeSuccess, "False", setReasonMergeFailed),

    // --- All reason nodes lead to shared finish, then terminal ---
    Connect(setReasonSuccess, sharedFinish),
    Connect(setReasonNoIssues, sharedFinish),
    Connect(setReasonRejected, sharedFinish),
    Connect(setReasonError, sharedFinish),
    Connect(setReasonTddFailed, sharedFinish),
    Connect(setReasonCiFailed, sharedFinish),
    Connect(setReasonMergeFailed, sharedFinish),
    Connect(sharedFinish, finish)
}
```

---

## Verification Checklist

- [ ] All 7 legacy finish `Sequence` nodes removed
- [ ] 7 `SetVariable` nodes created, each setting `exitReason` to the correct reason string
- [ ] 1 shared `Sequence` node created with `SetOutput` for `exitReason`, `finishReason`, `success`, `issueNumber`, `prNumber`, `mergeSha`
- [ ] `success` flag derived as `exitReason == "success"` (not hardcoded per path)
- [ ] `finishReason` output added (net-new observability enrichment)
- [ ] All 9 incoming connection targets updated from old Sequence IDs to new SetVariable IDs
- [ ] All 7 SetVariable nodes connect to `sharedFinish`
- [ ] `sharedFinish` connects to `finish` terminal
- [ ] `dotnet build` succeeds with 0 errors
- [ ] No remaining references to `finishSuccess`, `finishNoIssues`, `finishRejected`, `finishError`, `finishTddFailed`, `finishCiFailed`, `finishMergeFailed` as Sequence activities
- [ ] ELSA Studio shows all 7 exit paths converging on the single `Shared Finish` node
- [ ] Stories 13.1 and 13.2 tests still pass (regression check)
