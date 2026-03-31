# Story 7-1H: TDD Sub-Workflow

## User Story

As the **Tamma mentorship engine**, I need a reusable ELSA workflow that drives the red-green-refactor TDD cycle during implementation so that every task in the implementation plan is developed with test-first discipline, each phase is auditable, and the junior developer learns proper TDD practices.

## Description

Implement an ELSA code-first workflow (`TddWorkflow`) that enforces the red-green-refactor cycle for each task in the implementation plan. The workflow is called in a loop from the main workflow's `START_IMPLEMENTATION` state — once per task. Each TDD phase (RED: write failing tests, GREEN: make tests pass, REFACTOR: improve code) is a visible ELSA activity with guard conditions ensuring discipline (tests MUST fail in RED phase, MUST pass in GREEN phase).

When the GREEN phase fails (tests don't pass after implementation), the Debugging sub-workflow (7-1I) is invoked with mode=`tdd_failure`. Refactoring is optional and guarded by test stability — if refactored code breaks tests, the refactoring is reverted.

**New story** — not covered by existing epics

## Acceptance Criteria

### AC1: Workflow Registration
- [ ] Workflow defined as C# code-first `IWorkflow` in `Tamma.ElsaServer/Workflows/TddWorkflow.cs`
- [ ] Registered at startup via `services.AddWorkflow<TddWorkflow>()`
- [ ] Visible in ELSA Studio as "TDD Cycle" workflow
- [ ] Can be invoked standalone via ELSA REST API
- [ ] Can be invoked as child workflow via `RunWorkflow`

### AC2: Input/Output Contract
- [ ] **Inputs**:
  - `sessionId` (Guid) — mentorship session ID
  - `storyId` (string) — story identifier
  - `task` (object) — task from implementation plan (description, files, scope)
  - `repositoryUrl` (string) — repository URL
  - `branchName` (string) — working branch
  - `skillLevel` (int, 1-5) — junior's skill level
- [ ] **Outputs**: `TaskResult` record containing:
  - `status` (enum: `Completed`, `Failed`, `Skipped`)
  - `testsWritten` (int) — number of new tests
  - `testsPassing` (int) — number of passing tests
  - `filesChanged` (string[]) — list of changed files
  - `commitSha` (string) — final commit SHA
  - `redPhaseResult` (PhaseResult) — RED phase details
  - `greenPhaseResult` (PhaseResult) — GREEN phase details
  - `refactorPhaseResult` (PhaseResult, optional) — REFACTOR phase details
  - `debuggingInvoked` (bool) — whether debugging workflow was needed

### AC3: RED Phase (Write Failing Tests)
- [ ] `WriteTests` activity: RunWorkflow: LlmCall (7-1B, role=`tester`)
  - Prompt: "Write failing tests for this task: {task.description}"
  - Context includes: task details, existing code, project patterns, skill level
  - Skill-level adaptation:
    - Level 1-2: detailed test structure with comments explaining each test
    - Level 3: standard test generation
    - Level 4-5: high-level test specs, developer fills in details
- [ ] `RunTests` activity: RunWorkflow: Testing (7-1C, testSubset="new")
  - Runs ONLY the newly written tests
- [ ] **Guard: tests MUST fail**
  - If tests PASS → tests are wrong (they don't test anything meaningful)
  - `RewriteTests`: RunWorkflow: LlmCall (7-1B, role=`tester`, "these tests pass without implementation — rewrite to actually test the new behavior")
  - Max 2 rewrite attempts — if tests still pass, warn and proceed (edge case: task might be already implemented)

### AC4: GREEN Phase (Make Tests Pass)
- [ ] `WriteImplementation` activity: RunWorkflow: LlmCall (7-1B, role=`implementer`)
  - Prompt: "Write the minimum implementation to make these tests pass: {tests}"
  - Context includes: failing test output, task description, existing code
- [ ] `RunTests` activity: RunWorkflow: Testing (7-1C, full test suite)
  - Runs ALL tests (not just new ones) — implementation must not break existing tests
- [ ] **Guard: ALL tests must pass**
  - If tests fail → invoke Debugging sub-workflow (7-1I, mode=`tdd_failure`)
  - Debugging workflow gets: test failures, implementation code, task context
  - Max 3 debug iterations (configurable) — if still failing, return `Failed`

### AC5: REFACTOR Phase (Improve Code Quality)
- [ ] `AnalyzeCode` activity: RunWorkflow: LlmCall (7-1B, role=`reviewer`)
  - Prompt: "Identify refactoring opportunities in the code just written"
  - Returns: refactoring suggestions with confidence
- [ ] **Decision: refactoring needed?**
  - If no suggestions or low confidence → skip refactoring, proceed to commit
  - If suggestions with high confidence → apply refactoring
- [ ] `ApplyRefactoring` activity: RunWorkflow: LlmCall (7-1B, role=`implementer`)
  - Applies the suggested refactoring
- [ ] `RunTests` activity: RunWorkflow: Testing (7-1C, full suite)
  - Refactored code must still pass all tests
  - If tests break → **revert refactoring** (git checkout the refactoring changes)
  - Reverted refactoring logged but not considered a failure

### AC6: Commit and Complete
- [ ] `CommitChanges` activity: atomic commit with descriptive message
  - Commit message format: `feat({storyId}): {task.description} [TDD]`
  - Includes: test files and implementation files
  - Does NOT commit if RED/GREEN failed
- [ ] Output variables set with final state

### AC7: Skill-Level Adaptation
- [ ] TDD guidance varies by skill level:
  - **Level 1-2**: LLM provides very detailed test templates with comments; implementation gets step-by-step guidance; refactoring suggestions are simpler
  - **Level 3**: Standard TDD prompts; balanced guidance
  - **Level 4-5**: High-level test specs; minimal implementation guidance; focus on design patterns in refactoring
- [ ] Time expectations adjusted: lower levels get more patience in GREEN phase

### AC8: Observability
- [ ] Each TDD phase logged: phase name, duration, outcome, files changed
- [ ] Phase transitions logged: RED→GREEN, GREEN→REFACTOR, REFACTOR→COMMIT
- [ ] Debugging invocations logged with context
- [ ] Metrics: `tdd.red_phase.duration`, `tdd.green_phase.duration`, `tdd.refactor_applied_rate`, `tdd.debug_invocation_rate`

## Technical Design

### Workflow Structure (Pseudocode)

```
Flowchart: TddWorkflow
├── ValidateInputs
├── GatherContext (RunWorkflow: ContextGathering, 7-1F, purpose=Implementation)
│
├── [RED PHASE]
│   ├── WriteTests (RunWorkflow: LlmCall, role=tester)
│   ├── RunNewTests (RunWorkflow: Testing, subset=new)
│   ├── FlowDecision: Tests fail?
│   │   ├── Yes (correct!) → proceed to GREEN
│   │   └── No (tests pass = bad tests):
│   │       ├── RewriteTests (RunWorkflow: LlmCall, role=tester)
│   │       ├── RerunNewTests
│   │       ├── FlowDecision: Max rewrite attempts?
│   │       │   ├── No → loop
│   │       │   └── Yes → warn and proceed (task may be pre-implemented)
│
├── [GREEN PHASE]
│   ├── WriteImplementation (RunWorkflow: LlmCall, role=implementer)
│   ├── RunFullTests (RunWorkflow: Testing, full suite)
│   ├── FlowDecision: All tests pass?
│   │   ├── Yes → proceed to REFACTOR
│   │   └── No:
│   │       ├── RunWorkflow: Debugging (7-1I, mode=tdd_failure)
│   │       ├── FlowDecision: Debugging resolved?
│   │       │   ├── Yes → rerun tests
│   │       │   └── No (max debug iterations) → return Failed
│
├── [REFACTOR PHASE]
│   ├── AnalyzeCode (RunWorkflow: LlmCall, role=reviewer)
│   ├── FlowDecision: Refactoring needed?
│   │   ├── No → CommitChanges
│   │   └── Yes:
│   │       ├── SaveCheckpoint (git stash or commit marker)
│   │       ├── ApplyRefactoring (RunWorkflow: LlmCall, role=implementer)
│   │       ├── RunFullTests
│   │       ├── FlowDecision: Tests still pass?
│   │       │   ├── Yes → CommitChanges
│   │       │   └── No → RevertRefactoring → CommitChanges (without refactoring)
│
├── CommitChanges
└── SetOutputs (TaskResult)
```

### Custom Activities

```csharp
[Activity("Tamma.TDD", "Write Tests", "Generate failing tests for the task")]
public class WriteTestsActivity : CodeActivity<TestGenerationResult> { ... }

[Activity("Tamma.TDD", "Write Implementation", "Generate implementation to pass tests")]
public class WriteImplementationActivity : CodeActivity<ImplementationResult> { ... }

[Activity("Tamma.TDD", "Analyze Code", "Identify refactoring opportunities")]
public class AnalyzeCodeActivity : CodeActivity<RefactoringAnalysis> { ... }

[Activity("Tamma.TDD", "Apply Refactoring", "Apply suggested refactoring")]
public class ApplyRefactoringActivity : CodeActivity<RefactoringResult> { ... }

[Activity("Tamma.TDD", "Revert Refactoring", "Revert failed refactoring changes")]
public class RevertRefactoringActivity : CodeActivity { ... }

[Activity("Tamma.TDD", "Commit Changes", "Create atomic TDD commit")]
public class CommitChangesActivity : CodeActivity<CommitResult> { ... }

[Activity("Tamma.TDD", "Check Tests Fail", "Guard: verify tests fail in RED phase")]
[FlowNode("TestsFail", "TestsPass")]
public class CheckTestsFailActivity : Activity { ... }
```

### Output Schema

```csharp
public record TaskResult
{
    public TaskStatus Status { get; init; }
    public int TestsWritten { get; init; }
    public int TestsPassing { get; init; }
    public List<string> FilesChanged { get; init; } = new();
    public string CommitSha { get; init; } = string.Empty;
    public PhaseResult RedPhaseResult { get; init; } = new();
    public PhaseResult GreenPhaseResult { get; init; } = new();
    public PhaseResult? RefactorPhaseResult { get; init; }
    public bool DebuggingInvoked { get; init; }
}

public record PhaseResult
{
    public string Phase { get; init; } = string.Empty;  // RED, GREEN, REFACTOR
    public bool Succeeded { get; init; }
    public TimeSpan Duration { get; init; }
    public int Iterations { get; init; }
    public string? Notes { get; init; }
}

public enum TaskStatus { Completed, Failed, Skipped }
```

## Dependencies

- **7-1B (LLM Call)**: for test writing, implementation, code analysis, refactoring
- **7-1C (Testing)**: for running test suites
- **7-1F (Context Gathering)**: for task context
- **7-1I (Debugging)**: for resolving GREEN phase failures
- `Tamma.Activities.Integration.GitHubActivity` (existing) — for git operations
- ELSA 3.x `Flowchart`, `FlowDecision`, `RunWorkflow` activities

## Files to Create/Modify

| File | Action | Purpose |
|------|--------|---------|
| `Tamma.ElsaServer/Workflows/TddWorkflow.cs` | Create | Code-first workflow |
| `Tamma.Activities/TDD/WriteTestsActivity.cs` | Create | Test generation |
| `Tamma.Activities/TDD/WriteImplementationActivity.cs` | Create | Implementation generation |
| `Tamma.Activities/TDD/AnalyzeCodeActivity.cs` | Create | Refactoring analysis |
| `Tamma.Activities/TDD/ApplyRefactoringActivity.cs` | Create | Apply refactoring |
| `Tamma.Activities/TDD/RevertRefactoringActivity.cs` | Create | Revert refactoring |
| `Tamma.Activities/TDD/CommitChangesActivity.cs` | Create | Atomic commit |
| `Tamma.Activities/TDD/CheckTestsFailActivity.cs` | Create | RED phase guard |
| `Tamma.Activities/TDD/Models/` | Create | DTOs |
| `Tamma.ElsaServer/Program.cs` | Modify | Register workflow |

## Testing Strategy

### Unit Tests
- RED phase guard: tests that pass → trigger rewrite
- RED phase guard: tests that fail → proceed to GREEN
- GREEN phase: all tests pass → proceed to REFACTOR
- GREEN phase: tests fail → invoke Debugging workflow
- REFACTOR: no suggestions → skip to commit
- REFACTOR: suggestion applied, tests break → revert
- Skill-level adaptation: correct prompt detail for each level

### Integration Tests
- Full TDD cycle: RED → GREEN → REFACTOR → COMMIT (mock LLM + CI)
- RED phase rewrite: tests pass initially, rewritten, then fail correctly
- GREEN phase debug: implementation fails, debugging resolves, tests pass
- Refactoring revert: refactoring breaks tests, reverted successfully
- Standalone invocation via ELSA REST API
- Loop invocation: 3 tasks in sequence, each completing independently

### Performance Tests
- Single TDD cycle overhead (excluding LLM/CI calls): <1 second
- Context gathering for implementation: <500ms

## Configuration

```json
{
  "TDD": {
    "MaxRedPhaseRewrites": 2,
    "MaxGreenPhaseDebugIterations": 3,
    "CommitMessageFormat": "feat({storyId}): {taskDescription} [TDD]",
    "RefactoringConfidenceThreshold": 0.6,
    "SkillLevelPromptDetail": {
      "1": "very_detailed",
      "2": "detailed",
      "3": "standard",
      "4": "concise",
      "5": "minimal"
    }
  }
}
```

## Success Metrics

- RED phase produces genuinely failing tests >90% of the time
- GREEN phase passes after first implementation attempt >70% of the time
- Refactoring applied without breaking tests >85% of the time
- All 3 phases visible as distinct activities in ELSA Studio
- TDD compliance auditable: every task has RED → GREEN → COMMIT trail
- Debugging sub-workflow resolves GREEN failures >60% of the time

## Logging Requirements

All ELSA activities MUST inject `ILogger<T>` and log at these levels:

- **INFO**: Activity started (with session/issue ID), activity completed (with outcome), state transitions
- **DEBUG**: Input parameters received, intermediate LLM/API call details, decision rationale
- **WARN**: Retryable failures, timeout approaching, degraded quality gate result
- **ERROR**: Unrecoverable failures (with exception), invalid state transition, missing required data
- **Structured context**: Always include `{ sessionId, juniorId, storyId, currentState }` in all log entries
- **Sensitive data**: NEVER log student PII, credentials, or full LLM response content — log token counts and summary only
