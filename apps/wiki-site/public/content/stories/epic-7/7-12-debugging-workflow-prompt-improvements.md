---
title: "Story 7-12: Debugging Workflow Prompt Improvements"
sidebar:
  order: 70
---

Status: ready-for-dev

## Story

As a **workflow engineer**,
I want the Debugging workflow's LLM prompts to include project architecture context, maintain fresh code context across iterations, and generate pattern-aware regression tests,
so that debugging hypotheses are specific to the actual project rather than generic guesses, and fix attempts succeed on fewer iterations.

## Problem Statement — Audit Findings

An audit of the debugging workflow activities identified 5 prompt quality issues that directly reduce debugging effectiveness.

### Finding 1: AIDiagnosisActivity generates generic hypotheses

**File**: `Tamma.Activities/Debug/AIDiagnosisActivity.cs`, `BuildDiagnosisPrompt`

The diagnosis prompt sends error context, code context, git history, test results, and reproduction steps. But it does NOT send:
- **Project architecture description** (what modules exist, their responsibilities, how they interact)
- **Dependency graph** (which module depends on which)
- **Configuration system** (environment variables, config files, feature flags)
- **Database schema** (table structure, migration state)
- **External service dependencies** (APIs, message queues, caches)

Without this, the LLM produces generic hypotheses like "Logic error in condition evaluation -- incorrect operator or boundary check" instead of specific ones like "The `EventStore.append()` call in `code-generation.handler.ts` fails because the `tags` object includes an `undefined` value for `prId` when the issue has no associated PR, and PostgreSQL JSONB rejects null values in the tag index."

**Impact**: Generic hypotheses require more iterations to converge on the real root cause. The 5-iteration limit is frequently exhausted without resolution.

### Finding 2: RefineHypothesisActivity works against stale code

**File**: `Tamma.Activities/Debug/RefineHypothesisActivity.cs`, `BuildRefinementPrompt`

After iteration 1 applies a partial fix, the code in the repository has changed. But the refinement prompt only includes:
- The tried hypothesis
- Test results after the fix attempt
- Updated error messages
- Previous iteration context

It does NOT include the current state of the modified files. The LLM refines hypotheses based on what the code USED to look like, not what it looks like after the partial fix.

**Impact**: Refined hypotheses may suggest fixes to code that was already changed, causing conflicting edits or duplicate fixes.

### Finding 3: WriteRegressionTestActivity hardcodes TypeScript file paths

**File**: `Tamma.Activities/Debug/WriteRegressionTestActivity.cs`, `BuildTestPrompt`

The response format template hardcodes:
```json
"test_file_path": "tests/regression/bug-{storyId}.test.ts"
```

For a C# project, this produces TypeScript test paths. For Python, it produces TypeScript test paths. The activity has no knowledge of the project's test conventions.

**Impact**: Generated regression tests are placed in wrong directories with wrong file extensions.

### Finding 4: ApplyFix prompt in DebuggingWorkflow is a one-liner

**File**: `Tamma.ElsaServer/Workflows/DebuggingWorkflow.cs`, line 292

The `applyFix` `DispatchWorkflow` passes this as the task prompt:
```
"Apply fix for hypothesis: {hypothesisJson} (mode: {debugContextMode}, iteration: {currentIteration})"
```

This is dispatched to the `llm-call` workflow with `role=implementer`, but:
- No test file content is included (implementer doesn't know what tests to satisfy)
- No code context is included (implementer doesn't know what code to modify)
- No project conventions are included
- The hypothesis JSON is raw serialized JSON, not a human-readable description

**Impact**: The implementer LLM guesses at what code to write and where to put it.

### Finding 5: Parallel context gathering outputs are not connected to variables

**File**: `Tamma.ElsaServer/Workflows/DebuggingWorkflow.cs`, lines 135-191

The five `Collect*Activity` instances write their outputs, but the workflow variables `errorMessages`, `relevantCode`, `gitHistory`, `testResults`, `reproductionSteps` are never explicitly wired to receive those outputs. The activities set internal results but the workflow variables remain at their default empty values.

This means `AIDiagnosisActivity` receives empty strings for all context inputs:
```csharp
ErrorContext = new Input<string>(ctx => errorMessages.Get(ctx) ?? ""),    // always ""
CodeContext = new Input<string>(ctx => relevantCode.Get(ctx) ?? ""),       // always ""
GitContext = new Input<string>(ctx => gitHistory.Get(ctx) ?? ""),          // always ""
TestContext = new Input<string>(ctx => testResults.Get(ctx) ?? ""),        // always ""
ReproductionContext = new Input<string>(ctx => reproductionSteps.Get(ctx) ?? ""),  // always ""
```

**Impact**: AI diagnosis operates with no context at all. The entire parallel context gathering is dead code. All hypotheses are fabricated from the system prompt alone.

## Acceptance Criteria

### AC1: Wire Context Gathering Outputs to Workflow Variables
- [ ] Each `Collect*Activity` output is connected to its corresponding workflow variable via `Output<T>` or `SetVariable` after the activity
- [ ] `AIDiagnosisActivity` receives populated context strings, not empty strings
- [ ] Verified by adding a log activity after `Join` that prints context variable lengths

### AC2: Add Project Architecture Context to AI Diagnosis
- [ ] `AIDiagnosisActivity` gains a `ProjectContext` input (string)
- [ ] `DebuggingWorkflow` populates `ProjectContext` from:
  - Code index summary (if available)
  - `CLAUDE.md` content from the repository
  - Recent git log summary (module-level)
- [ ] The diagnosis prompt includes `## Project Architecture` section between test results and previous attempts
- [ ] The section instructs the LLM: "Use this context to generate SPECIFIC hypotheses tied to the actual project structure"

### AC3: Fresh Code Context in Hypothesis Refinement
- [ ] `RefineHypothesisActivity` gains an `UpdatedCodeContext` input (string)
- [ ] After each fix attempt, `DebuggingWorkflow` re-collects the code context for modified files
- [ ] The refinement prompt includes `## Current Code (After Partial Fix)` section
- [ ] The section notes: "This code may have changed since the initial diagnosis. Analyze the CURRENT state."

### AC4: Pattern-Aware Regression Tests
- [ ] `WriteRegressionTestActivity` gains a `TestPatternProfile` input (reuses model from 7-8B)
- [ ] Test file path uses detected naming convention and location convention
- [ ] Test code uses detected framework and assertion style
- [ ] Sample test snippet from the project is included in the prompt for style reference
- [ ] Hardcoded `tests/regression/bug-{storyId}.test.ts` removed

### AC5: Rich ApplyFix Prompt
- [ ] The `applyFix` `DispatchWorkflow` input includes:
  - Hypothesis description (human-readable, not raw JSON)
  - Suggested fix approach
  - Affected file contents (current state)
  - Test file contents that must pass
  - Project conventions summary
- [ ] The prompt follows the same structured format as the TDD WriteImplementation prompt

### AC6: Reconnect DebuggingWorkflow from TddWithDebugRetry
- [ ] When `TddWithDebugRetryWorkflow` dispatches debugging, it passes `TestPatternProfile` as an input
- [ ] `DebuggingWorkflow` accepts `TestPatternProfile` as optional input
- [ ] If provided, `TestPatternProfile` is used by `WriteRegressionTestActivity` and `applyFix`

## Technical Context

### Files to Modify

| File | Changes |
|------|---------|
| `Tamma.Activities/Debug/AIDiagnosisActivity.cs` | Add `ProjectContext` input, include in prompt |
| `Tamma.Activities/Debug/RefineHypothesisActivity.cs` | Add `UpdatedCodeContext` input, include in prompt |
| `Tamma.Activities/Debug/WriteRegressionTestActivity.cs` | Add `TestPatternProfile` input, replace hardcoded paths |
| `Tamma.ElsaServer/Workflows/DebuggingWorkflow.cs` | Wire Collect* outputs to variables, add project context gathering, pass TestPatternProfile, restructure applyFix input, add code re-collection after fix |
| `Tamma.ElsaServer/Workflows/TddWithDebugRetryWorkflow.cs` | Pass `TestPatternProfile` to debugging dispatch |

### Corrected DebuggingWorkflow Variable Wiring

The five `Collect*Activity` instances need their `Output<T>` bound to the workflow variables. Currently, none of them have output bindings in the workflow.

For each activity, add the `Result` output binding. Example for `CollectErrorMessages`:

```csharp
var collectErrors = new CollectErrorMessagesActivity
{
    Id = "collectErrors",
    Name = "Collect Error Messages",
    ErrorOutput = new Input<string>(ctx => errorOutput.Get(ctx) ?? ""),
    DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? "RuntimeError"),
    RepositoryUrl = new Input<string>(ctx => repositoryUrl.Get(ctx) ?? ""),
    BranchName = new Input<string>(ctx => branchName.Get(ctx) ?? ""),
    // ADD THIS: wire output to workflow variable
    Result = new Output<string>(errorMessages)   // <-- MISSING
};
```

Repeat for all five collectors:
- `collectErrors.Result -> errorMessages`
- `collectCode.Result -> relevantCode`
- `collectGit.Result -> gitHistory`
- `collectTests.Result -> testResults`
- `collectRepro.Result -> reproductionSteps`

### Corrected ApplyFix Dispatch Input

Replace:
```csharp
["taskPrompt"] = $"Apply fix for hypothesis: {SecurityHelpers.SanitizeForPrompt(selectedHypothesisJson.Get(ctx) ?? "unknown")} (mode: {debugContextMode.Get(ctx)}, iteration: {currentIteration.Get(ctx)})",
```

With:
```csharp
["taskPrompt"] = BuildFixPrompt(
    selectedHypothesisJson.Get(ctx),
    debugContextMode.Get(ctx),
    relevantCode.Get(ctx),
    testResults.Get(ctx),
    currentIteration.Get(ctx)),
```

Where `BuildFixPrompt` is a new static helper:
```csharp
private static string BuildFixPrompt(
    string? hypothesisJson, string? mode,
    string? codeContext, string? testContext, int iteration)
{
    var sb = new System.Text.StringBuilder();

    // Parse hypothesis for readable description
    string description = "unknown issue";
    string suggestedFix = "investigate and fix";
    try
    {
        var h = System.Text.Json.JsonSerializer.Deserialize<Hypothesis>(hypothesisJson ?? "{}");
        if (h != null)
        {
            description = h.Description;
            suggestedFix = h.SuggestedFix ?? suggestedFix;
        }
    }
    catch { /* use defaults */ }

    sb.AppendLine($"# Debug Fix — Iteration {iteration}");
    sb.AppendLine($"Mode: {mode}");
    sb.AppendLine();
    sb.AppendLine("## Root Cause");
    sb.AppendLine(description);
    sb.AppendLine();
    sb.AppendLine("## Suggested Fix Approach");
    sb.AppendLine(suggestedFix);
    sb.AppendLine();

    if (!string.IsNullOrWhiteSpace(codeContext))
    {
        sb.AppendLine("## Code to Modify");
        sb.AppendLine(codeContext);
        sb.AppendLine();
    }

    if (!string.IsNullOrWhiteSpace(testContext))
    {
        sb.AppendLine("## Tests That Must Pass");
        sb.AppendLine(testContext);
        sb.AppendLine();
    }

    sb.AppendLine("## Requirements");
    sb.AppendLine("1. Apply the fix described above");
    sb.AppendLine("2. Do not break any existing tests");
    sb.AppendLine("3. Make the minimum change necessary");
    sb.AppendLine("4. Follow project coding conventions");

    return sb.ToString();
}
```

### Code Re-Collection After Fix

After `applyFix` and before `runTests`, add a step to re-collect code context for the modified files. This ensures `RefineHypothesisActivity` gets fresh code if the fix fails.

Add a new `CollectRelevantCodeActivity` instance (`collectCodeAfterFix`) between `applyFix` and `runTests` in the flowchart:
```
applyFix -> collectCodeAfterFix -> runTests
```

The output of `collectCodeAfterFix` updates `relevantCode`, so `RefineHypothesisActivity` sees the current state.

## Dependencies

- Story 7-8B (TDD Prompt Overhaul) -- `TestPatternProfile` model defined there
- Story 7-1I (Debugging Sub-Workflow) -- already implemented, being modified
- Story 13.1 (TDD Debug Retry Sub-Workflow) -- already implemented, being modified

## Estimated Effort

3 days

## Testing Strategy

### Unit Tests
- `AIDiagnosisActivity`: with `ProjectContext` populated, prompt contains `## Project Architecture` section
- `AIDiagnosisActivity`: with `ProjectContext` empty, prompt omits the section
- `RefineHypothesisActivity`: with `UpdatedCodeContext` populated, prompt contains `## Current Code` section
- `WriteRegressionTestActivity`: with `TestPatternProfile` for C#, generates `*Tests.cs` file path
- `WriteRegressionTestActivity`: with `TestPatternProfile` for Python, generates `*_test.py` file path

### Integration Tests
- `DebuggingWorkflow`: after parallel context gathering, `AIDiagnosisActivity` receives non-empty strings
- `DebuggingWorkflow`: `applyFix` prompt contains hypothesis description, code context, and test context
- Full debug cycle: diagnosis -> fix -> code re-collection -> refinement (with fresh code)

### Regression Tests
- All existing debugging workflow tests still pass
- `TddWithDebugRetryWorkflow` passes `TestPatternProfile` to debugging dispatch

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-31 | 1.0 | Initial story from TDD/Testing/Debugging prompt audit | Architecture Team |
