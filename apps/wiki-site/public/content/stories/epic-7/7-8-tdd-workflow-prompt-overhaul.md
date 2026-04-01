---
title: "Story 7-8B: TDD Workflow Prompt Overhaul"
sidebar:
  order: 70
---

Status: ready-for-dev

## Story

As a **workflow engineer**,
I want the TDD workflow's LLM prompts to be context-aware, project-pattern-aware, and grounded in the story's acceptance criteria,
so that the LLM produces tests and implementations that actually match the project under development instead of generating generic boilerplate.

## Problem Statement — Audit Findings

An audit of every LLM-calling activity in the TDD, Testing, and Debugging workflows surfaced **14 critical prompt quality weaknesses**. These are not minor wording issues -- they cause the LLM to produce code that fails on first contact with a real repository.

### Finding 1: WriteTestsActivity sends zero project context

**File**: `Tamma.Activities/TDD/WriteTestsActivity.cs`, line 172-186

The RED phase prompt says:

```
You are a TDD tester. Write failing tests for the following task.
...
4. Follow the project's existing test patterns
```

But the prompt never supplies what those patterns ARE. The LLM has no information about:
- Test framework (Vitest? Jest? NUnit? xUnit? pytest?)
- Assertion library (expect/assert/should/chai)
- File naming convention (`*.test.ts` vs `*.spec.ts` vs `*Tests.cs`)
- Mocking approach (vi.mock? jest.mock? MSW? Moq? NSubstitute?)
- Test file location convention (colocated? `__tests__/`? `tests/`?)
- Import style (ESM vs CJS, relative vs aliases)

**Impact**: The mock `SimulateTestGeneration` hardcodes `describe`/`it`/`expect` (Jest/Vitest style), which means even the mock path assumes TypeScript+Vitest. A C# project would get completely wrong output.

### Finding 2: No acceptance criteria or test cases in the prompt

The story/plan feeding the TDD workflow may contain explicit test cases and acceptance criteria. The `TaskDescription` input is a plain string -- no structured data. The prompt does not:
- Check if the story has enumerated test cases
- Parse acceptance criteria into test requirements
- Distinguish between unit, integration, and e2e test expectations

**Impact**: The LLM invents test scenarios rather than implementing the ones the story already specifies.

### Finding 3: WriteImplementationActivity has no architectural context

**File**: `Tamma.Activities/TDD/WriteImplementationActivity.cs`, line 142-161

The GREEN phase prompt says "Follow the project's coding conventions" but provides none. The LLM does not know:
- Language and runtime version
- Module system (ESM/CJS/namespaces)
- Dependency injection pattern
- Error handling conventions (custom error classes, Result types)
- Naming conventions (camelCase, PascalCase, snake_case)
- State management patterns (immutable, mutable)

**Impact**: The implementation may compile but violates every project convention, causing refactoring to be a full rewrite.

### Finding 4: AnalyzeCodeActivity reviews in a vacuum

**File**: `Tamma.Activities/TDD/AnalyzeCodeActivity.cs`, line 129-162

The REFACTOR phase analyzes code without knowing:
- Project-specific quality standards
- Existing code style (enforced by linter rules)
- Architecture patterns already in use
- Performance constraints
- Whether there are existing abstractions the code should use

**Impact**: Refactoring suggestions conflict with project patterns or duplicate existing utilities.

### Finding 5: Mock test execution bypasses real CI entirely

**File**: `Tamma.ElsaServer/Workflows/TddWorkflow.cs`, lines 113-119, 175-181, 257

Three `TODO` comments mark mock test runs that should dispatch to `TestingWorkflow`:
```
// TODO: Replace mock test runs with DispatchWorkflow calls to testing-pipeline (7-1C)
```

Tests are simulated as "always fail" (RED) or "always pass" (GREEN/REFACTOR). This means:
- The RED phase guard never actually validates that tests fail
- The GREEN phase never validates that implementation passes
- The REFACTOR phase never detects broken refactorings

**Impact**: The entire TDD loop is theater -- no tests actually run.

### Finding 6: Hardcoded TypeScript assumptions in mocks

**File**: `Tamma.Activities/TDD/WriteTestsActivity.cs`, line 229-249

`SimulateTestGeneration` hardcodes:
```csharp
"describe('Task Tests', () => {\n" +
"  it('should implement the required behavior', () => {\n" +
"    expect(true).toBe(false);\n" +
```

And the file extension transformation:
```csharp
taskFiles[0].Replace(".ts", ".test.ts").Replace(".cs", ".Tests.cs")
```

This assumes the repository is either TypeScript or C#. Python, Go, Rust, Java -- all produce wrong output.

### Finding 7: SkillLevelPromptDetail is vague guidance, not structural

**File**: `Tamma.Activities/TDD/Models/TddModels.cs`, lines 163-189

The skill level system returns strings like "Provide very detailed test structure with comments explaining each test case" but these are meta-instructions, not structural changes to the prompt. The LLM interprets "very detailed" differently every time.

**Impact**: Skill adaptation is non-deterministic and unverifiable.

### Finding 8: JSON response format has no schema enforcement

All three TDD activities (WriteTests, WriteImplementation, AnalyzeCode) ask the LLM to "Respond with JSON: {...}" but:
- No JSON schema is provided
- No structural validation beyond property existence checks
- The `testCode` field returns a monolithic string, not file-by-file content
- No file path information for multi-file changes
- The response format is embedded in the prompt string, not parameterized

**Impact**: Fragile parsing, lost code when multiple files need changes.

### Finding 9: Debugging workflow prompts lack project context too

**File**: `Tamma.Activities/Debug/AIDiagnosisActivity.cs`

The AI diagnosis prompt provides error context but no project architecture context. The LLM cannot reason about:
- Which modules exist and their responsibilities
- Dependency graph
- Configuration system
- Database schema (if applicable)

**Impact**: Root cause hypotheses are generic ("logic error in condition evaluation") rather than specific ("the `EventStore.append()` method expects UUID v7 but receives UUID v4").

### Finding 10: WriteRegressionTestActivity has same pattern-blindness as WriteTestsActivity

**File**: `Tamma.Activities/Debug/WriteRegressionTestActivity.cs`

Hardcoded output format assumes `*.test.ts` files:
```
"test_file_path": "tests/regression/bug-{storyId}.test.ts"
```

No project pattern detection.

### Finding 11: RefineHypothesisActivity loses code context between iterations

**File**: `Tamma.Activities/Debug/RefineHypothesisActivity.cs`

The refinement prompt includes the tried hypothesis and test results but does NOT re-include the code context. After a partial fix, the code may have changed, but the LLM only sees the original context from the initial diagnosis.

**Impact**: Hypothesis refinement works against stale code context.

### Finding 12: ApplyRefactoringActivity returns monolithic code string

**File**: `Tamma.Activities/TDD/ApplyRefactoringActivity.cs`

The response format is `{"refactoredCode": "...", "filesChanged": [...]}` -- a single code string for potentially multiple files. The LLM has no way to express changes across files.

### Finding 13: No test type discrimination

The prompts do not distinguish between:
- Unit tests (isolated, mocked dependencies)
- Integration tests (real dependencies, test containers)
- E2E tests (full stack, browser/API)
- Contract tests (API schema compliance)

The task description may implicitly require integration tests, but the prompt always generates unit-style tests.

### Finding 14: 4096 max_tokens is too small for implementation

**File**: `Tamma.Activities/TDD/WriteImplementationActivity.cs`, line 180

`max_tokens = 4096` for implementation generation. A single file with 200 lines of TypeScript code with tests is roughly 2000-3000 tokens. Multi-file implementations easily exceed 4096 tokens and get truncated silently.

## Acceptance Criteria

### AC1: Pre-Prompt Test Case Analysis
- [ ] New `AnalyzeTestCasesActivity` extracts structured test cases from story/plan input
- [ ] If the plan has explicit test cases (bulleted lists, numbered scenarios, Given/When/Then), parse them into a `TestCaseSpec[]`
- [ ] If no test cases found, the activity outputs an `AcceptanceCriteriaMissing` flag
- [ ] When `AcceptanceCriteriaMissing` is true, the workflow requests test cases from the orchestrator before proceeding to RED phase
- [ ] Each `TestCaseSpec` has: description, type (unit/integration/e2e), inputs, expected outputs, edge case flag

### AC2: Project Test Pattern Detection
- [ ] New `DetectTestPatternsActivity` inspects the repository before any test generation
- [ ] Detects and returns a `TestPatternProfile`:
  - `language`: string (TypeScript, C#, Python, Go, etc.)
  - `testFramework`: string (vitest, jest, nunit, xunit, pytest, go test, etc.)
  - `assertionStyle`: string (expect/assert/should)
  - `mockingLibrary`: string (vi.mock, jest.mock, MSW, Moq, NSubstitute, unittest.mock)
  - `fileNamingConvention`: string (*.test.ts, *.spec.ts, *Tests.cs, *_test.py, *_test.go)
  - `fileLocationConvention`: string (colocated, __tests__, tests/, test/)
  - `importStyle`: string (ESM, CJS, namespace, package)
  - `sampleTestSnippet`: string (an actual test from the repo for few-shot guidance)
  - `projectConventions`: string (extracted from CLAUDE.md, .editorconfig, etc.)
- [ ] Detection uses code index or file system scan (not LLM call)
- [ ] Profile cached per repository+branch

### AC3: Restructured RED Phase Prompt
- [ ] `WriteTestsActivity.BuildTestPrompt` is replaced with a multi-section structured prompt
- [ ] Prompt includes: test pattern profile, parsed test cases, code context, skill-level-specific structural guidance
- [ ] The prompt template references specific acceptance criteria from the story
- [ ] Response format changed to per-file output: `{"files": [{"path": "...", "content": "..."}], "testCount": N}`
- [ ] Mock path uses detected patterns (not hardcoded Jest/Vitest)
- [ ] Rewrite prompt includes the previous test code AND the reason the tests incorrectly passed

### AC4: Restructured GREEN Phase Prompt
- [ ] `WriteImplementationActivity.BuildImplementationPrompt` includes project architecture context
- [ ] Prompt includes: coding conventions, module structure, dependency injection patterns, error handling style
- [ ] Response format changed to per-file output: `{"files": [{"path": "...", "content": "..."}]}`
- [ ] `max_tokens` increased to 16384 (or configurable per model)
- [ ] Test failure output from RED phase is always included (not nullable)

### AC5: Restructured REFACTOR Phase Prompt
- [ ] `AnalyzeCodeActivity.BuildAnalysisPrompt` includes project quality standards
- [ ] Prompt includes: linter rules summary, existing abstractions/utilities, architecture patterns
- [ ] Response format supports per-file suggestions with before/after snippets
- [ ] Suggestions reference specific lines/functions, not vague categories

### AC6: Wire Mock Test Execution to TestingWorkflow
- [ ] All three mock test run blocks in `TddWorkflow.cs` replaced with `DispatchWorkflow` calls to `testing-pipeline`
- [ ] RED phase dispatches `testing-pipeline` with `testSubset=new` (only new test files)
- [ ] GREEN phase dispatches `testing-pipeline` with full suite
- [ ] REFACTOR phase dispatches `testing-pipeline` with full suite
- [ ] Test results flow back through workflow variables to guard conditions
- [ ] Graceful degradation: if `testing-pipeline` dispatch fails, fall back to mock with warning log

### AC7: Improved Skill-Level Adaptation
- [ ] Skill-level guidance is structural, not advisory:
  - L1-2: prompt includes commented test templates showing EXACT structure to follow
  - L3: prompt includes a reference test from the project as few-shot example
  - L4-5: prompt provides acceptance criteria and lets the developer choose structure
- [ ] Implementation guidance for L1-2 includes step-by-step comments in the generated code
- [ ] Refactoring for L1-2 is limited to naming and extraction (no pattern changes)

### AC8: Multi-File Response Format
- [ ] All LLM responses use consistent per-file format:
  ```json
  {
    "files": [
      {"path": "relative/path/to/file.test.ts", "content": "full file content", "action": "create|modify"},
      {"path": "relative/path/to/impl.ts", "content": "full file content", "action": "create|modify"}
    ],
    "summary": "brief description of what was generated"
  }
  ```
- [ ] Parsing handles single-file responses (backward compat with old format)
- [ ] File paths are validated against repository structure

## Technical Context

### Files to Modify

| File | Changes |
|------|---------|
| `Tamma.Activities/TDD/WriteTestsActivity.cs` | Rewrite `BuildTestPrompt`, add `TestPatternProfile` input, add `TestCaseSpecs` input, change response parsing to multi-file format, increase `max_tokens` |
| `Tamma.Activities/TDD/WriteImplementationActivity.cs` | Rewrite `BuildImplementationPrompt`, add `ProjectConventions` input, change response parsing, increase `max_tokens` to 16384 |
| `Tamma.Activities/TDD/AnalyzeCodeActivity.cs` | Rewrite `BuildAnalysisPrompt`, add `ProjectConventions` input, change response format |
| `Tamma.Activities/TDD/ApplyRefactoringActivity.cs` | Update `BuildRefactoringPrompt`, change response to multi-file format |
| `Tamma.Activities/TDD/Models/TddModels.cs` | Add `TestCaseSpec`, `TestPatternProfile`, `FileChange` models; restructure `SkillLevelPromptDetail` to return structural templates |
| `Tamma.ElsaServer/Workflows/TddWorkflow.cs` | Replace 3 mock test run blocks with `DispatchWorkflow("testing-pipeline")`, wire `TestPatternProfile` and `TestCaseSpecs` through workflow, add `DetectTestPatterns` and `AnalyzeTestCases` activities before RED phase |

### New Files to Create

| File | Purpose |
|------|---------|
| `Tamma.Activities/TDD/DetectTestPatternsActivity.cs` | Inspects repo for test framework, conventions, sample tests |
| `Tamma.Activities/TDD/AnalyzeTestCasesActivity.cs` | Parses story/plan text to extract structured test case specs |
| `Tamma.Activities/TDD/Models/TestPatternProfile.cs` | Model for detected test patterns |
| `Tamma.Activities/TDD/Models/TestCaseSpec.cs` | Model for structured test case specifications |

## Exact Prompt Templates

### RED Phase: WriteTestsActivity — New Prompt

```
SYSTEM:
You are a test engineer for the {language} project "{projectName}". You write tests using {testFramework} with {assertionStyle} assertions and {mockingLibrary} for mocking. Tests are placed in {fileLocationConvention} with the naming pattern {fileNamingConvention}. Import style: {importStyle}.

USER:
# Task
Write failing tests for the following task. These tests MUST fail initially because the implementation does not exist yet.

## Task Description
{taskDescription}

## Acceptance Criteria to Test
{foreach testCase in testCases}
- [{testCase.type}] {testCase.description}
  - Input: {testCase.inputs}
  - Expected: {testCase.expectedOutputs}
  {if testCase.isEdgeCase}(edge case){/if}
{/foreach}

{if testCases.empty}
WARNING: No explicit test cases were provided in the story. Generate test cases based on the task description, covering:
1. Happy path (main success scenario)
2. Input validation (invalid/missing inputs)
3. Error handling (expected failure modes)
4. Edge cases (boundary values, empty collections, null)
{/if}

## Project Test Pattern Reference
Here is an actual test from this project for style reference:
```{language}
{sampleTestSnippet}
```

## Relevant Source Files
{foreach file in taskFiles}
### {file.path}
```{language}
{file.content}
```
{/foreach}

{if codeContext}
## Additional Code Context
{codeContext}
{/if}

## Project Conventions
{projectConventions}

{skillGuidance}

## Output Format
Respond with JSON. Each file must be complete and independently valid:
```json
{
  "files": [
    {
      "path": "relative/path/to/test-file{fileNamingConvention}",
      "content": "complete test file content",
      "action": "create"
    }
  ],
  "testCount": <number of individual test cases>,
  "testCases": [
    {"name": "test name", "type": "unit|integration|e2e", "acceptanceCriterion": "AC reference if applicable"}
  ]
}
```
```

### RED Phase: Rewrite Prompt (tests incorrectly passed)

```
SYSTEM:
You are a test engineer. The tests you previously wrote PASS without any implementation, which means they do not test the NEW behavior. You must rewrite them to be genuine tests.

USER:
# Problem
The following tests were supposed to FAIL (because the implementation doesn't exist), but they PASS. This means they are testing something that already exists or they have tautological assertions.

## Task Description
{taskDescription}

## Tests That Incorrectly Passed
```{language}
{previousTestCode}
```

## Why They Passed (analysis)
{passAnalysis — e.g., "tests assert existing behavior" or "assertions are always-true tautologies"}

## Acceptance Criteria That MUST Be Tested
{foreach testCase in testCases}
- [{testCase.type}] {testCase.description}
{/foreach}

## Requirements for Rewritten Tests
1. Each test MUST call a function/method that does NOT yet exist, ensuring a compile/runtime error
2. Each test MUST assert a value that the current code does NOT produce
3. Do NOT test existing behavior -- test ONLY the new behavior described in the task
4. Use the same test framework and patterns as the project ({testFramework}, {assertionStyle})

## Output Format
Same JSON format as before:
```json
{
  "files": [...],
  "testCount": N,
  "testCases": [...]
}
```
```

### GREEN Phase: WriteImplementationActivity — New Prompt

```
SYSTEM:
You are an implementation engineer for the {language} project "{projectName}". You follow these conventions:
- Naming: {namingConventions}
- Error handling: {errorHandlingPattern}
- State management: {stateManagementPattern}
- Module structure: {moduleStructure}
- Dependency injection: {diPattern}

USER:
# Task
Write the MINIMUM implementation needed to make ALL the following tests pass. Do not over-engineer -- write just enough code to satisfy the tests.

## Task Description
{taskDescription}

## Tests to Satisfy
{foreach testFile in testFiles}
### {testFile.path}
```{language}
{testFile.content}
```
{/foreach}

## Test Failure Output
```
{testFailureOutput}
```

## Existing Code Context
{foreach file in relevantFiles}
### {file.path}
```{language}
{file.content}
```
{/foreach}

## Project Architecture
{projectArchitectureNotes}

## Project Conventions
{projectConventions}

{skillGuidance}

## Requirements
1. Write the minimum code to make ALL tests pass
2. Do NOT break any existing tests
3. Follow the project's coding conventions exactly
4. Use existing abstractions and utilities where appropriate
5. Handle errors using the project's error handling pattern

## Output Format
```json
{
  "files": [
    {
      "path": "relative/path/to/implementation-file",
      "content": "complete file content",
      "action": "create|modify"
    }
  ],
  "summary": "brief description of what was implemented"
}
```
```

### REFACTOR Phase: AnalyzeCodeActivity — New Prompt

```
SYSTEM:
You are a code reviewer for the {language} project "{projectName}". You know the project's quality standards:
- Linter: {linterTool} with rules: {linterRulesSummary}
- Existing utilities: {existingUtilities}
- Architecture patterns: {architecturePatterns}
- Performance requirements: {performanceRequirements}

USER:
# Refactoring Analysis
Identify refactoring opportunities in the code that was just written during a TDD cycle. Only suggest changes that maintain correctness AND align with project conventions.

## Tests (must continue to pass after any refactoring)
{foreach testFile in testFiles}
### {testFile.path}
```{language}
{testFile.content}
```
{/foreach}

## Implementation (candidate for refactoring)
{foreach implFile in implFiles}
### {implFile.path}
```{language}
{implFile.content}
```
{/foreach}

## Existing Project Patterns to Align With
{projectPatterns}

{skillGuidance}

## Analysis Categories
1. **Convention violations**: code that doesn't follow project naming, structure, or style
2. **Duplication with existing code**: reimplementations of existing utility functions
3. **Simplification**: unnecessarily complex code that can be simplified
4. **Pattern alignment**: code that should use project-standard patterns (e.g., Result type, custom errors)
5. **Performance**: obvious performance issues (only if project has performance requirements)

## Output Format
```json
{
  "hasSuggestions": true,
  "confidence": 0.0-1.0,
  "suggestions": [
    {
      "description": "what to change and why",
      "category": "convention|duplication|simplification|pattern|performance",
      "confidence": 0.0-1.0,
      "filePath": "affected file",
      "location": "function/class name or line range",
      "before": "current code snippet",
      "after": "suggested code snippet"
    }
  ]
}
```
```

### REFACTOR Phase: ApplyRefactoringActivity — New Prompt

```
SYSTEM:
You are an implementation engineer applying safe refactorings. All existing tests MUST continue to pass after your changes. You follow {projectName}'s coding conventions.

USER:
# Apply Refactoring
Apply the following refactoring suggestions. Produce the complete modified files.

## Current Implementation
{foreach file in implFiles}
### {file.path}
```{language}
{file.content}
```
{/foreach}

## Tests That Must Continue to Pass
{foreach testFile in testFiles}
### {testFile.path}
```{language}
{testFile.content}
```
{/foreach}

## Refactoring Suggestions to Apply
{foreach suggestion in suggestions}
{suggestion.rank}. [{suggestion.category}] {suggestion.description} (confidence: {suggestion.confidence})
   File: {suggestion.filePath}
   Location: {suggestion.location}
   Before: {suggestion.before}
   After: {suggestion.after}
{/foreach}

## Output Format
```json
{
  "files": [
    {
      "path": "relative/path/to/file",
      "content": "complete refactored file content",
      "action": "modify"
    }
  ],
  "summary": "what was refactored and why"
}
```
```

## New TDD Cycle Flow (After Overhaul)

```
INIT
  |
  v
DetectTestPatterns (new) -- inspects repo, returns TestPatternProfile
  |
  v
AnalyzeTestCases (new) -- parses plan/story, returns TestCaseSpec[]
  |
  v
[AcceptanceCriteriaMissing?]
  |YES                        |NO
  v                           |
RequestTestCases (escalate)   |
  |                           |
  v                           v
[RED PHASE]
  WriteTests (restructured prompt with TestPatternProfile + TestCaseSpecs)
  |
  v
  DispatchWorkflow("testing-pipeline", testSubset=new) <-- REAL CI, not mock
  |
  v
  CheckTestsFail guard
  |PASS (bad)                 |FAIL (good)
  v                           v
  [max rewrites?] ---NO-->  WriteTests (rewrite prompt)
  |YES
  v
[GREEN PHASE]
  WriteImplementation (restructured prompt with project conventions)
  |
  v
  DispatchWorkflow("testing-pipeline", fullSuite) <-- REAL CI, not mock
  |
  v
  [all pass?]
  |YES                        |NO
  v                           v
  [REFACTOR PHASE]            debug loop (max 3)
  AnalyzeCode                   |
  |                           WriteImplementation (with failure context)
  [suggestions?]                |
  |YES           |NO          DispatchWorkflow("testing-pipeline")
  v              |              |
  ApplyRefactoring |           [pass?] --YES--> REFACTOR
  |              |              |NO
  v              |            [max debug?] --YES--> FAILED
  DispatchWorkflow |
  ("testing-pipeline")
  |              |
  [pass?]        |
  |YES    |NO    |
  v       v      v
  COMMIT  REVERT COMMIT
          then
          COMMIT
```

## Dependencies

- Story 7-1H (TDD Sub-Workflow) -- already implemented, being modified
- Story 7-1C (Testing Sub-Workflow) -- must exist and be dispatchable
- Code Index infrastructure -- for `DetectTestPatternsActivity` repository scanning

## Estimated Effort

5 days

## Testing Strategy

### Unit Tests
- `DetectTestPatternsActivity`: given a repo with Vitest tests, returns correct profile
- `DetectTestPatternsActivity`: given a repo with NUnit tests, returns correct profile
- `AnalyzeTestCasesActivity`: given a plan with "AC1: ...", extracts structured specs
- `AnalyzeTestCasesActivity`: given a plan with no test cases, sets `AcceptanceCriteriaMissing=true`
- `WriteTestsActivity`: prompt includes TestPatternProfile sections
- `WriteTestsActivity`: prompt includes parsed TestCaseSpecs
- `WriteImplementationActivity`: prompt includes project conventions
- `AnalyzeCodeActivity`: prompt includes project quality standards
- Response parsing: multi-file JSON format parsed correctly
- Response parsing: legacy single-file format still works (backward compat)

### Integration Tests
- Full TDD cycle with `DispatchWorkflow("testing-pipeline")` instead of mocks
- RED phase with test pattern detection feeding into prompt
- GREEN phase with project conventions feeding into prompt
- Skill level 1 prompt vs skill level 5 prompt structural differences

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-31 | 1.0 | Initial story from TDD/Testing/Debugging prompt audit | Architecture Team |
