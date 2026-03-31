# Story 7-8B: TDD Prompt Overhaul — Implementation Plan

## Overview

This plan covers the complete rewrite of LLM prompts across the TDD workflow (RED/GREEN/REFACTOR phases), the creation of two new pre-RED-phase activities, and the wiring of mock test execution to the real `TestingWorkflow` via `DispatchWorkflow`.

**Prerequisite**: Story 7-1C (TestingWorkflow) must be dispatchable. It already exists at `Tamma.ElsaServer/Workflows/TestingWorkflow.cs`.

---

## Phase 1: New Models

### File: `Tamma.Activities/TDD/Models/TestPatternProfile.cs` (CREATE)

```csharp
namespace Tamma.Activities.TDD.Models;

/// <summary>
/// Detected test patterns from a repository. Populated by DetectTestPatternsActivity
/// before any test generation occurs. Used to ground LLM prompts in project reality.
/// </summary>
public class TestPatternProfile
{
    /// <summary>Primary language (TypeScript, CSharp, Python, Go, Java, Rust)</summary>
    public string Language { get; set; } = "TypeScript";

    /// <summary>Test framework (vitest, jest, nunit, xunit, pytest, go-test, cargo-test)</summary>
    public string TestFramework { get; set; } = "vitest";

    /// <summary>Assertion style (expect, assert, should, Assert, Expect)</summary>
    public string AssertionStyle { get; set; } = "expect";

    /// <summary>Mocking library (vi.mock, jest.mock, msw, moq, nsubstitute, unittest.mock, none)</summary>
    public string MockingLibrary { get; set; } = "vi.mock";

    /// <summary>Test file naming convention (*.test.ts, *.spec.ts, *Tests.cs, *_test.py, *_test.go)</summary>
    public string FileNamingConvention { get; set; } = "*.test.ts";

    /// <summary>Test file location convention (colocated, __tests__, tests/, test/)</summary>
    public string FileLocationConvention { get; set; } = "colocated";

    /// <summary>Import style (esm, cjs, namespace, package)</summary>
    public string ImportStyle { get; set; } = "esm";

    /// <summary>An actual test file from the project for few-shot style reference</summary>
    public string SampleTestSnippet { get; set; } = string.Empty;

    /// <summary>Project-specific conventions extracted from config files (CLAUDE.md, .editorconfig, etc.)</summary>
    public string ProjectConventions { get; set; } = string.Empty;

    /// <summary>Project name for prompt context</summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>Coding conventions: naming, error handling, state management, DI pattern</summary>
    public string CodingConventions { get; set; } = string.Empty;

    /// <summary>Architecture notes: module structure, existing abstractions, patterns in use</summary>
    public string ArchitectureNotes { get; set; } = string.Empty;

    /// <summary>Linter tool and key rules summary</summary>
    public string LinterSummary { get; set; } = string.Empty;

    /// <summary>Existing utility functions/classes the LLM should reuse</summary>
    public string ExistingUtilities { get; set; } = string.Empty;
}
```

### File: `Tamma.Activities/TDD/Models/TestCaseSpec.cs` (CREATE)

```csharp
namespace Tamma.Activities.TDD.Models;

/// <summary>
/// A structured test case specification extracted from a story or implementation plan.
/// </summary>
public class TestCaseSpec
{
    /// <summary>Test case description</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Test type: unit, integration, e2e, contract</summary>
    public string Type { get; set; } = "unit";

    /// <summary>Input values or setup description</summary>
    public string Inputs { get; set; } = string.Empty;

    /// <summary>Expected output or behavior</summary>
    public string ExpectedOutputs { get; set; } = string.Empty;

    /// <summary>Whether this is an edge case</summary>
    public bool IsEdgeCase { get; set; }

    /// <summary>Reference to acceptance criterion (e.g., "AC1", "AC3")</summary>
    public string? AcceptanceCriterionRef { get; set; }
}

/// <summary>
/// Result of analyzing a story/plan for test cases.
/// </summary>
public class TestCaseAnalysisResult
{
    /// <summary>Whether the analysis was successful</summary>
    public bool Success { get; set; }

    /// <summary>Extracted test case specifications</summary>
    public List<TestCaseSpec> TestCases { get; set; } = new();

    /// <summary>Whether the plan is missing explicit acceptance criteria</summary>
    public bool AcceptanceCriteriaMissing { get; set; }

    /// <summary>Raw acceptance criteria text (if found)</summary>
    public string? RawAcceptanceCriteria { get; set; }

    /// <summary>Test type distribution summary</summary>
    public Dictionary<string, int> TestTypeDistribution { get; set; } = new();
}

/// <summary>
/// Represents a single file change in an LLM response (multi-file format).
/// </summary>
public class FileChange
{
    /// <summary>Relative file path</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Complete file content</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Action: create or modify</summary>
    public string Action { get; set; } = "create";
}
```

### File: `Tamma.Activities/TDD/Models/TddModels.cs` (MODIFY)

Add to `TestGenerationResult`:

```csharp
// ADD these properties:
public List<FileChange> Files { get; set; } = new();
public List<TestCaseSpec> TestCasesGenerated { get; set; } = new();
```

Add to `ImplementationResult`:

```csharp
// ADD this property:
public List<FileChange> Files { get; set; } = new();
public string? Summary { get; set; }
```

Add to `RefactoringSuggestion`:

```csharp
// ADD these properties:
public string? Location { get; set; }
public string? Before { get; set; }
public string? After { get; set; }
```

Add to `RefactoringResult`:

```csharp
// ADD this property:
public List<FileChange> Files { get; set; } = new();
public string? Summary { get; set; }
```

Replace `SkillLevelPromptDetail` methods with structural templates:

```csharp
public static string GetTestPromptGuidance(int skillLevel) => skillLevel switch
{
    1 or 2 => @"## Skill Level Guidance (Detailed)
Provide VERY detailed test structure:
- Include a commented template showing the exact test structure to follow
- Add a comment above each test explaining WHY this test exists
- Show setup/teardown patterns with comments
- Explain each assertion's purpose
- Include example of how to mock dependencies with comments",

    3 => @"## Skill Level Guidance (Standard)
Write standard test cases:
- Follow the project's existing test patterns shown above
- Cover happy path, error conditions, and edge cases
- Use descriptive test names that explain the expected behavior",

    4 or 5 => @"## Skill Level Guidance (Minimal)
Provide high-level test specifications:
- List what to test (acceptance criteria coverage)
- Use concise, idiomatic test patterns
- Focus on WHAT to test, not HOW — the developer knows the framework",

    _ => @"## Skill Level Guidance (Standard)
Write standard test cases covering happy path, edge cases, and error conditions."
};

public static string GetImplementationGuidance(int skillLevel) => skillLevel switch
{
    1 or 2 => @"## Skill Level Guidance (Detailed)
Provide step-by-step implementation:
- Add a comment before each function explaining its purpose and parameters
- Add inline comments for any non-obvious logic
- Explain design pattern choices with comments
- Show error handling with comments explaining each catch clause",

    3 => @"## Skill Level Guidance (Standard)
Write a clean implementation:
- Follow project conventions exactly
- Add brief comments only for complex logic
- Use existing project abstractions where appropriate",

    4 or 5 => @"## Skill Level Guidance (Minimal)
Write minimal implementation:
- Focus on clean design and SOLID principles
- No unnecessary comments — code should be self-documenting
- Use advanced patterns where appropriate",

    _ => @"## Skill Level Guidance (Standard)
Write a clean implementation following project conventions."
};

public static string GetRefactoringGuidance(int skillLevel) => skillLevel switch
{
    1 or 2 => @"## Skill Level Guidance (Conservative Refactoring)
Suggest ONLY simple, safe refactorings:
- Variable/function renaming for clarity
- Extract repeated code into named functions
- Remove unused imports or variables
Do NOT suggest pattern changes or architectural refactoring.",

    3 => @"## Skill Level Guidance (Standard Refactoring)
Suggest standard refactorings:
- Apply project-standard patterns where code deviates
- Extract shared logic into utility functions
- Improve code organization",

    4 or 5 => @"## Skill Level Guidance (Advanced Refactoring)
Suggest advanced refactorings:
- Design pattern application (Strategy, Observer, etc.)
- Performance optimizations with measurable impact
- Architectural alignment improvements
- Generics/polymorphism to reduce duplication",

    _ => @"## Skill Level Guidance (Standard Refactoring)
Suggest standard refactorings including design pattern application and code organization."
};
```

---

## Phase 2: New Activities

### File: `Tamma.Activities/TDD/DetectTestPatternsActivity.cs` (CREATE)

This activity scans the repository to detect test patterns. It does NOT call the LLM -- it uses file system inspection and pattern matching.

**Detection strategy**:
1. Scan for test runner config files: `vitest.config.ts`, `jest.config.js`, `*.csproj` with NUnit/xUnit refs, `pytest.ini`, `go.mod`
2. Find test files by common patterns: `*.test.ts`, `*.spec.ts`, `*Tests.cs`, `*_test.py`, `*_test.go`
3. Read the first found test file as `SampleTestSnippet`
4. Detect assertion style from test file content (`expect(`, `assert.`, `Assert.`, `should.`)
5. Detect mocking library from imports (`vi.mock`, `jest.mock`, `Mock<`, `patch(`)
6. Read `CLAUDE.md`, `.editorconfig`, `tsconfig.json`, `*.csproj` for project conventions
7. Determine file location convention (are tests colocated with source or in separate dir?)

**Inputs**:
- `RepositoryUrl` (string)
- `BranchName` (string)
- `CodeIndexAvailable` (bool) — whether to use code index API or file system scan

**Output**: `TestPatternProfile`

**Key implementation detail**: This activity calls the Engine callback API endpoint `/api/engine/detect-test-patterns` which has access to the cloned repository. If the Engine is unavailable, it returns a default TypeScript/Vitest profile with a warning log.

```csharp
[Activity("Tamma.TDD", "Detect Test Patterns",
    "Inspect repository to detect test framework, conventions, and patterns",
    Kind = ActivityKind.Task)]
public class DetectTestPatternsActivity : CodeActivity<TestPatternProfile>
{
    [Input(Description = "Repository URL")]
    public Input<string> RepositoryUrl { get; set; } = default!;

    [Input(Description = "Branch name")]
    public Input<string> BranchName { get; set; } = default!;

    [Input(Description = "Whether code index is available", DefaultValue = false)]
    public Input<bool> CodeIndexAvailable { get; set; } = new(false);

    // Implementation calls /api/engine/detect-test-patterns
    // Falls back to default profile on error
}
```

### File: `Tamma.Activities/TDD/AnalyzeTestCasesActivity.cs` (CREATE)

This activity parses the task description / plan JSON to extract structured test cases. It uses lightweight text parsing (not LLM) for structured formats and falls back to LLM for unstructured descriptions.

**Parsing strategy** (in order):
1. Look for numbered acceptance criteria: `AC1:`, `AC 1:`, `1.`, `- [ ]`
2. Look for Given/When/Then blocks
3. Look for bullet points with test-like descriptions ("should ...", "must ...", "returns ...")
4. Look for explicit test case tables
5. If none found, set `AcceptanceCriteriaMissing = true`
6. For each found test case, classify as unit/integration/e2e based on keywords:
   - "database", "API", "HTTP", "queue" --> integration
   - "browser", "UI", "page", "click" --> e2e
   - everything else --> unit

**Inputs**:
- `TaskDescription` (string) — the plan/story text
- `StoryId` (string) — for reference

**Output**: `TestCaseAnalysisResult`

```csharp
[Activity("Tamma.TDD", "Analyze Test Cases",
    "Extract structured test cases from story/plan text",
    Kind = ActivityKind.Task)]
public class AnalyzeTestCasesActivity : CodeActivity<TestCaseAnalysisResult>
{
    [Input(Description = "Task description / plan text")]
    public Input<string> TaskDescription { get; set; } = default!;

    [Input(Description = "Story identifier")]
    public Input<string> StoryId { get; set; } = default!;
}
```

---

## Phase 3: Rewrite Existing Activities

### File: `Tamma.Activities/TDD/WriteTestsActivity.cs` (MODIFY)

**Changes**:

1. **Add new inputs**:
```csharp
[Input(Description = "Detected test pattern profile")]
public Input<TestPatternProfile?> TestPatternProfile { get; set; } = default!;

[Input(Description = "Structured test case specifications")]
public Input<List<TestCaseSpec>?> TestCaseSpecs { get; set; } = default!;
```

2. **Replace `BuildTestPrompt` entirely** with the structured prompt from the story. The new method:
   - Builds a system message with test framework context from `TestPatternProfile`
   - Includes parsed `TestCaseSpec` entries as structured requirements
   - Includes `SampleTestSnippet` as few-shot reference
   - Includes `ProjectConventions`
   - Uses structural skill guidance instead of advisory strings
   - Uses the multi-file response format

3. **Replace `CallLlm`** to use increased `max_tokens`:
```csharp
max_tokens = 8192,  // up from 4096 — test files can be large
```

4. **Replace `ParseTestGenerationResponse`** to handle both new multi-file format and legacy format:
```csharp
private static TestGenerationResult ParseTestGenerationResponse(string response, List<string> fallbackFiles)
{
    var json = JsonSerializer.Deserialize<JsonElement>(response);

    // Try new format first: {"files": [...], "testCount": N}
    if (json.TryGetProperty("files", out var filesArr))
    {
        var files = new List<FileChange>();
        foreach (var f in filesArr.EnumerateArray())
        {
            files.Add(new FileChange
            {
                Path = f.GetProperty("path").GetString() ?? "",
                Content = f.GetProperty("content").GetString() ?? "",
                Action = f.TryGetProperty("action", out var a) ? a.GetString() ?? "create" : "create"
            });
        }
        // ... build TestGenerationResult from files
    }

    // Fall back to legacy format: {"testCode": "...", "testFiles": [...]}
    // ... existing parsing logic
}
```

5. **Replace `SimulateTestGeneration`** to use `TestPatternProfile` for generating framework-appropriate mock output.

### File: `Tamma.Activities/TDD/WriteImplementationActivity.cs` (MODIFY)

**Changes**:

1. **Add new inputs**:
```csharp
[Input(Description = "Detected test pattern profile (includes project conventions)")]
public Input<TestPatternProfile?> TestPatternProfile { get; set; } = default!;
```

2. **Replace `BuildImplementationPrompt`** with structured prompt from the story:
   - System message includes language, naming conventions, error handling, DI pattern
   - Includes per-file test content (not monolithic string)
   - Includes project architecture notes
   - Uses multi-file response format

3. **Increase `max_tokens`**:
```csharp
max_tokens = 16384,  // up from 4096 — implementations can span multiple files
```

4. **Replace `ParseImplementationResponse`** to handle multi-file format.

### File: `Tamma.Activities/TDD/AnalyzeCodeActivity.cs` (MODIFY)

**Changes**:

1. **Add new input**:
```csharp
[Input(Description = "Detected test pattern profile (includes quality standards)")]
public Input<TestPatternProfile?> TestPatternProfile { get; set; } = default!;
```

2. **Replace `BuildAnalysisPrompt`** with structured prompt:
   - System message includes linter tool, rules, existing utilities, architecture patterns
   - Suggestions include `location`, `before`, `after` fields
   - Analysis categories are project-specific

3. **Update `ParseAnalysisResponse`** to capture new suggestion fields.

### File: `Tamma.Activities/TDD/ApplyRefactoringActivity.cs` (MODIFY)

**Changes**:

1. **Add new input**:
```csharp
[Input(Description = "Detected test pattern profile")]
public Input<TestPatternProfile?> TestPatternProfile { get; set; } = default!;
```

2. **Replace `BuildRefactoringPrompt`** with structured prompt using per-file format.

3. **Increase `max_tokens`**:
```csharp
max_tokens = 16384,
```

4. **Replace `ParseRefactoringResponse`** to handle multi-file format.

---

## Phase 4: Wire TddWorkflow to Real TestingWorkflow

### File: `Tamma.ElsaServer/Workflows/TddWorkflow.cs` (MODIFY)

This is the most structurally impactful change. The workflow gains two new pre-RED activities and replaces three mock blocks with real `DispatchWorkflow` calls.

**Step 4a: Add new variables**

```csharp
var testPatternProfile = builder.WithVariable<TestPatternProfile>();
var testCaseAnalysis = builder.WithVariable<TestCaseAnalysisResult>();
var testingResult = builder.WithVariable<IDictionary<string, object>?>();
```

**Step 4b: Add DetectTestPatterns activity (before RED phase)**

```csharp
var detectTestPatterns = new DetectTestPatternsActivity
{
    Id = "DetectTestPatterns",
    Name = "Detect Test Patterns",
    RepositoryUrl = new Input<string>(ctx => repositoryUrl.Get(ctx)),
    BranchName = new Input<string>(ctx => branchName.Get(ctx)),
    Result = new Output<TestPatternProfile>(testPatternProfile)
};
detectTestPatterns.SetDisplayText("Detect Test Patterns");
```

**Step 4c: Add AnalyzeTestCases activity (before RED phase)**

```csharp
var analyzeTestCases = new AnalyzeTestCasesActivity
{
    Id = "AnalyzeTestCases",
    Name = "Analyze Test Cases",
    TaskDescription = new Input<string>(ctx => taskDescription.Get(ctx)),
    StoryId = new Input<string>(ctx => storyId.Get(ctx)),
    Result = new Output<TestCaseAnalysisResult>(testCaseAnalysis)
};
analyzeTestCases.SetDisplayText("Analyze Test Cases");
```

**Step 4d: Wire TestPatternProfile and TestCaseSpecs into WriteTests**

```csharp
var writeTests = new WriteTestsActivity
{
    // ... existing inputs ...
    TestPatternProfile = new Input<TestPatternProfile?>(ctx => testPatternProfile.Get(ctx)),
    TestCaseSpecs = new Input<List<TestCaseSpec>?>(ctx =>
    {
        var analysis = testCaseAnalysis.Get(ctx);
        return analysis?.TestCases;
    }),
    // ... rest unchanged ...
};
```

**Step 4e: Wire TestPatternProfile into WriteImplementation, AnalyzeCode, ApplyRefactoring**

Add `TestPatternProfile` input to each, wired from the `testPatternProfile` workflow variable.

**Step 4f: Replace RED phase mock test runs (lines 113-119) with DispatchWorkflow**

Remove:
```csharp
var mockNewTestsFail = Assign(testRunAllPassed, _ => (object)false, ...);
var mockNewTestsFailCount = Assign(testRunFailedCount, ctx => ...);
var mockNewTestsPassCount = Assign(testRunPassedCount, _ => (object)0, ...);
```

Replace with:
```csharp
var runRedPhaseTests = new DispatchWorkflow
{
    Id = "RunRedPhaseTests",
    Name = "Run RED Phase Tests",
    WorkflowDefinitionId = new("testing-pipeline"),
    Input = new(ctx => new Dictionary<string, object>
    {
        ["SessionId"] = sessionId.Get(ctx),
        ["Repository"] = repositoryUrl.Get(ctx),
        ["Branch"] = branchName.Get(ctx),
        ["SkillLevel"] = skillLevel.Get(ctx)
    }),
    WaitForCompletion = new(true),
    Result = new(testingResult)
};
runRedPhaseTests.SetDisplayText("Run RED Phase Tests");

// Extract test results from DispatchWorkflow output
var extractRedTestResults = Assign(testRunAllPassed, ctx =>
{
    var output = testingResult.Get(ctx);
    if (output != null && output.TryGetValue("passed", out var p) && p is bool passed)
        return (object)passed;
    return (object)false;
}, "ExtractRedTestResults", "Extract RED Test Results");
```

**Step 4g: Replace GREEN phase mock test runs (lines 175-181) with DispatchWorkflow**

Same pattern as RED phase but with `runGreenPhaseTests` ID.

**Step 4h: Replace REFACTOR phase mock test runs (line 257) with DispatchWorkflow**

Same pattern with `runRefactorPhaseTests` ID.

**Step 4i: Update connections**

Replace all connections that pointed to mock activities with connections to the new `DispatchWorkflow` and result-extraction activities.

New connection flow for RED phase:
```
logRedPhaseStart -> detectTestPatterns -> analyzeTestCases -> writeTests
    -> runRedPhaseTests -> extractRedTestResults -> checkTestsFail
```

New connection flow for GREEN phase:
```
logGreenPhaseStart -> writeImplementation -> runGreenPhaseTests
    -> extractGreenTestResults -> greenTestsPassCheck
```

New connection flow for REFACTOR phase:
```
applyRefactoring -> markRefactored -> runRefactorPhaseTests
    -> extractRefactorTestResults -> refactorTestsPassCheck
```

---

## Phase 5: Debugging Workflow Prompt Improvements

These are smaller fixes to the debugging activities identified in the audit.

### File: `Tamma.Activities/Debug/AIDiagnosisActivity.cs` (MODIFY)

**Change 1**: Add project context input
```csharp
[Input(Description = "Project architecture context")]
public Input<string?> ProjectContext { get; set; } = default!;
```

**Change 2**: Include project context in prompt after "## Test Results" section:
```csharp
if (!string.IsNullOrWhiteSpace(projectCtx))
{
    sb.AppendLine("## Project Architecture");
    sb.AppendLine("Use this context to generate SPECIFIC hypotheses tied to the actual project structure, not generic guesses.");
    sb.AppendLine(projectCtx);
    sb.AppendLine();
}
```

### File: `Tamma.Activities/Debug/RefineHypothesisActivity.cs` (MODIFY)

**Change 1**: Add updated code context input
```csharp
[Input(Description = "Updated code context (may have changed after partial fix)")]
public Input<string?> UpdatedCodeContext { get; set; } = default!;
```

**Change 2**: Include in prompt:
```csharp
if (!string.IsNullOrWhiteSpace(updatedCode))
{
    sb.AppendLine("## Current Code (After Partial Fix)");
    sb.AppendLine("NOTE: This code may have changed since the initial diagnosis. Analyze the CURRENT state.");
    sb.AppendLine(updatedCode);
    sb.AppendLine();
}
```

### File: `Tamma.Activities/Debug/WriteRegressionTestActivity.cs` (MODIFY)

**Change 1**: Add test pattern profile input
```csharp
[Input(Description = "Test pattern profile for the project")]
public Input<TestPatternProfile?> TestPatternProfile { get; set; } = default!;
```

**Change 2**: Replace hardcoded `tests/regression/bug-{storyId}.test.ts` with pattern-aware path:
```csharp
var profile = TestPatternProfile.Get(context);
var testDir = profile?.FileLocationConvention ?? "tests/regression";
var extension = profile?.FileNamingConvention?.Replace("*", "") ?? ".test.ts";
var testPath = $"{testDir}/bug-{storyId}{extension}";
```

**Change 3**: Include test framework info in prompt system message.

### File: `Tamma.ElsaServer/Workflows/DebuggingWorkflow.cs` (MODIFY)

**Change**: Wire `TestPatternProfile` into `WriteRegressionTestActivity` by adding a `DetectTestPatterns` step early in the flow (after classify, before fork), or by accepting it as a workflow input from the caller (TddWithDebugRetryWorkflow already has repo context).

---

## Verification Checklist

### Phase 1: Models
- [ ] `TestPatternProfile.cs` created with all fields
- [ ] `TestCaseSpec.cs` created with `TestCaseSpec`, `TestCaseAnalysisResult`, `FileChange`
- [ ] `TddModels.cs` updated with `Files` property on `TestGenerationResult`, `ImplementationResult`, `RefactoringResult`
- [ ] `SkillLevelPromptDetail` methods return structural templates
- [ ] `dotnet build` succeeds

### Phase 2: New Activities
- [ ] `DetectTestPatternsActivity.cs` created, compiles, returns `TestPatternProfile`
- [ ] `AnalyzeTestCasesActivity.cs` created, compiles, parses AC from plan text
- [ ] Both activities handle empty/missing input gracefully

### Phase 3: Prompt Rewrites
- [ ] `WriteTestsActivity.BuildTestPrompt` uses `TestPatternProfile` and `TestCaseSpecs`
- [ ] `WriteTestsActivity` system message includes framework-specific context
- [ ] `WriteImplementationActivity.BuildImplementationPrompt` includes project conventions
- [ ] `WriteImplementationActivity` `max_tokens` is 16384
- [ ] `AnalyzeCodeActivity.BuildAnalysisPrompt` includes quality standards
- [ ] `ApplyRefactoringActivity.BuildRefactoringPrompt` uses multi-file format
- [ ] All activities parse multi-file response format
- [ ] All activities still parse legacy single-file format (backward compat)

### Phase 4: Workflow Wiring
- [ ] `TddWorkflow` has `DetectTestPatterns` before RED phase
- [ ] `TddWorkflow` has `AnalyzeTestCases` before RED phase
- [ ] All three mock test run blocks replaced with `DispatchWorkflow("testing-pipeline")`
- [ ] Test results extracted from `DispatchWorkflow` output into guard variables
- [ ] All connections updated to reflect new activities
- [ ] Flowchart renders correctly in ELSA Studio

### Phase 5: Debugging Fixes
- [ ] `AIDiagnosisActivity` accepts `ProjectContext` input
- [ ] `RefineHypothesisActivity` accepts `UpdatedCodeContext` input
- [ ] `WriteRegressionTestActivity` uses `TestPatternProfile` for file paths
- [ ] `dotnet build` succeeds for all projects

---

## Summary of All File Changes

| Action | File | Key Changes |
|--------|------|-------------|
| CREATE | `Tamma.Activities/TDD/Models/TestPatternProfile.cs` | New model with 14 fields |
| CREATE | `Tamma.Activities/TDD/Models/TestCaseSpec.cs` | `TestCaseSpec`, `TestCaseAnalysisResult`, `FileChange` models |
| CREATE | `Tamma.Activities/TDD/DetectTestPatternsActivity.cs` | Repo scanning activity, ~200 lines |
| CREATE | `Tamma.Activities/TDD/AnalyzeTestCasesActivity.cs` | Text parsing activity, ~250 lines |
| MODIFY | `Tamma.Activities/TDD/Models/TddModels.cs` | Add `Files` to result types, restructure `SkillLevelPromptDetail` |
| MODIFY | `Tamma.Activities/TDD/WriteTestsActivity.cs` | Rewrite `BuildTestPrompt`, add inputs, new response parsing, `max_tokens=8192` |
| MODIFY | `Tamma.Activities/TDD/WriteImplementationActivity.cs` | Rewrite `BuildImplementationPrompt`, add inputs, new response parsing, `max_tokens=16384` |
| MODIFY | `Tamma.Activities/TDD/AnalyzeCodeActivity.cs` | Rewrite `BuildAnalysisPrompt`, add inputs, new response format |
| MODIFY | `Tamma.Activities/TDD/ApplyRefactoringActivity.cs` | Rewrite `BuildRefactoringPrompt`, add inputs, `max_tokens=16384` |
| MODIFY | `Tamma.ElsaServer/Workflows/TddWorkflow.cs` | Add 2 pre-RED activities, replace 3 mock blocks with `DispatchWorkflow`, update connections |
| MODIFY | `Tamma.Activities/Debug/AIDiagnosisActivity.cs` | Add `ProjectContext` input, include in prompt |
| MODIFY | `Tamma.Activities/Debug/RefineHypothesisActivity.cs` | Add `UpdatedCodeContext` input, include in prompt |
| MODIFY | `Tamma.Activities/Debug/WriteRegressionTestActivity.cs` | Add `TestPatternProfile` input, pattern-aware file paths |
| MODIFY | `Tamma.ElsaServer/Workflows/DebuggingWorkflow.cs` | Wire `TestPatternProfile` into regression test step |

**Total**: 4 new files, 10 modified files

---

## Estimated Implementation Order

1. **Day 1**: Phase 1 (models) + Phase 2 (new activities) — foundations
2. **Day 2**: Phase 3a (WriteTestsActivity rewrite) — most complex prompt change
3. **Day 3**: Phase 3b-d (WriteImplementation, AnalyzeCode, ApplyRefactoring rewrites)
4. **Day 4**: Phase 4 (TddWorkflow DispatchWorkflow wiring) — most structurally impactful
5. **Day 5**: Phase 5 (debugging fixes) + integration testing + ELSA Studio verification
