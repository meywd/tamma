---
title: "Story 7-1I: Debugging Sub-Workflow"
sidebar:
  order: 70
---

## User Story

As the **Tamma mentorship engine**, I need a reusable ELSA workflow for systematic debugging that handles three entry contexts — TDD test failures, unexpected runtime errors, and pre-implementation bug investigation — so that every debugging attempt is auditable, hypothesis-driven, and produces regression tests for future prevention.

## Description

Implement an ELSA code-first workflow (`DebuggingWorkflow`) that provides systematic, AI-driven debugging. The workflow is called from three contexts:

1. **From TDD (7-1H)**: when tests fail during GREEN phase — diagnose why implementation doesn't pass
2. **From MONITOR_PROGRESS**: when unexpected errors/failures occur during implementation
3. **Pre-implementation for bug issues**: when the assigned issue IS a bug — investigate before writing any code

Each context has different context-gathering strategies but shares the same diagnosis → fix → verify loop. The workflow is hypothesis-driven: the AI generates ranked root cause hypotheses, attempts fixes, and refines hypotheses based on results. For bug investigation mode, a regression test is ALWAYS written before attempting a fix (TDD for bugs).

**New story** — not covered by existing epics

## Acceptance Criteria

### AC1: Workflow Registration
- [ ] Workflow defined as C# code-first `IWorkflow` in `Tamma.ElsaServer/Workflows/DebuggingWorkflow.cs`
- [ ] Registered at startup via `services.AddWorkflow<DebuggingWorkflow>()`
- [ ] Visible in ELSA Studio as "Debugging" workflow
- [ ] Can be invoked standalone via ELSA REST API
- [ ] Can be invoked as child workflow via `RunWorkflow`

### AC2: Input/Output Contract
- [ ] **Inputs**:
  - `sessionId` (Guid) — mentorship session ID
  - `storyId` (string) — story identifier
  - `debugContext` (enum: `TddFailure`, `RuntimeError`, `BugInvestigation`)
  - `errorOutput` (string, optional) — error messages, stack traces, test output
  - `relevantFiles` (string[], optional) — files involved in the error
  - `issueDescription` (string, optional) — for `BugInvestigation` mode
  - `repositoryUrl` (string) — repository URL
  - `branchName` (string) — working branch
  - `skillLevel` (int, 1-5) — junior's skill level
- [ ] **Outputs**: `DebugResult` record containing:
  - `status` (enum: `Resolved`, `Unresolved`, `Escalated`)
  - `rootCause` (string) — identified root cause
  - `fixApplied` (string) — description of the fix
  - `attempts` (int) — number of fix attempts
  - `hypotheses` (Hypothesis[]) — all hypotheses generated and their outcomes
  - `regressionTestAdded` (bool) — whether a regression test was created
  - `filesChanged` (string[]) — files modified by the fix
  - `debugReport` (string) — comprehensive debug report (especially for escalation)

### AC3: Context Classification and Routing
- [ ] `ClassifyDebugContext` activity routes based on `debugContext`:
  - **TddFailure**: known test failures, implementation exists — focus on making tests pass
  - **RuntimeError**: unexpected error during implementation — broader investigation
  - **BugInvestigation**: pre-implementation for bug issues — investigate first, then fix
- [ ] Each mode triggers different context gathering strategies

### AC4: Debug Context Gathering (Parallel)
- [ ] ELSA `Fork` gathers debug-specific context in parallel:
  - `CollectErrorMessages`: stack traces, log output, test failure messages
  - `CollectRelevantCode`: files involved, recent changes to those files
  - `CollectGitHistory`: what changed recently (diff, blame)
  - `CollectTestResults`: which tests fail, which pass, coverage gaps
  - `CollectReproductionSteps` (BugInvestigation only): from issue description
- [ ] `Join` waits for all (timeout: 15 seconds)
- [ ] Mode-specific emphasis:
  - TddFailure: emphasize test output and implementation code
  - RuntimeError: emphasize stack traces and recent changes
  - BugInvestigation: emphasize issue description and reproduction steps

### AC5: AI Diagnosis
- [ ] `AIDiagnosis` activity: RunWorkflow: LlmCall (7-1B, role=`debugger`)
  - New agent role: `debugger` — specialized for debugging analysis
  - LLM returns:
    - Ranked root cause hypotheses (by confidence)
    - Affected files/functions
    - Suggested fix approach per hypothesis
  - Prompt includes: all gathered context, previous failed attempts (if retrying)
- [ ] Hypotheses stored in workflow variables for iteration tracking

### AC6: Debug Loop
- [ ] Iterative fix loop with maximum iterations (default: 5, configurable):

  **For each iteration**:
  1. Select highest-confidence untried hypothesis
  2. Attempt fix based on `debugContext`:
     - **TddFailure**: `ModifyImplementation` (RunWorkflow: LlmCall, role=`implementer`, guided by hypothesis)
     - **RuntimeError**: `ApplyFix` (RunWorkflow: LlmCall, role=`implementer`, targeted fix)
     - **BugInvestigation**: `WriteRegressionTest` FIRST (RunWorkflow: LlmCall, role=`tester`), THEN `WriteFix` (RunWorkflow: LlmCall, role=`implementer`)
  3. Run tests (RunWorkflow: Testing, 7-1C):
     - **Pass** → resolved, break loop
     - **Fail** → refine hypothesis:
       - `RefineHypothesis`: RunWorkflow: LlmCall (role=`debugger`, "previous fix didn't work because {testResults}, refine hypothesis")
       - Updated hypothesis fed into next iteration
  4. Max iterations reached → `EscalateWithContext`

### AC7: Bug Investigation Mode — TDD for Bugs
- [ ] For `BugInvestigation` mode:
  1. ALWAYS write a regression test BEFORE fixing
  2. Regression test must reproduce the bug (test should FAIL initially)
  3. Guard: regression test must fail — if it passes, the bug might be fixed or test is wrong
  4. Then implement fix
  5. Guard: regression test (and all other tests) must pass after fix
  - This ensures the bug is properly tested and won't regress

### AC8: Context Accumulation
- [ ] Each iteration accumulates context:
  - Previous hypotheses and their outcomes
  - Previous fix attempts and why they failed
  - Updated test results
  - New error messages (may differ after partial fix)
- [ ] All accumulated context passed to LLM in subsequent iterations
- [ ] This prevents the LLM from repeating failed approaches

### AC9: Escalation with Full Report
- [ ] When max iterations reached without resolution:
  - `CompileDebugReport` activity generates comprehensive report:
    - All hypotheses attempted (ranked by confidence)
    - All fix attempts and their outcomes
    - Remaining test failures
    - Files investigated
    - Suggested next steps for human developer
  - Report stored in `debugReport` output
  - Status set to `Escalated` (not `Unresolved` — active escalation)
  - Notification sent to senior developer with report

### AC10: Resolution Recording
- [ ] When a fix resolves the issue:
  - `RecordResolution` activity stores:
    - Root cause category (for pattern analysis)
    - Fix approach that worked
    - Files involved
    - Debugging time
  - Resolution data feeds into Context Gathering (7-1F) for similar future issues
  - Commit message includes: `fix({storyId}): {rootCause} [debug]`

### AC11: Observability
- [ ] Each hypothesis logged: rank, description, confidence, outcome
- [ ] Each fix attempt logged: iteration, approach, test result, duration
- [ ] Debug session metrics: `debug.total`, `debug.resolved_rate`, `debug.avg_iterations`, `debug.escalation_rate`
- [ ] Per-mode metrics: `debug.tdd_failure.*`, `debug.runtime_error.*`, `debug.bug_investigation.*`

## Technical Design

### Workflow Structure (Pseudocode)

```
Flowchart: DebuggingWorkflow
├── ValidateInputs
├── ClassifyDebugContext
│   ├── TddFailure → context emphasis: test output + implementation
│   ├── RuntimeError → context emphasis: stack traces + recent changes
│   └── BugInvestigation → context emphasis: issue description + reproduction
│
├── Fork (parallel debug context gathering):
│   ├── CollectErrorMessages
│   ├── CollectRelevantCode
│   ├── CollectGitHistory
│   ├── CollectTestResults
│   └── CollectReproductionSteps (BugInvestigation only)
├── Join (timeout 15s)
│
├── AIDiagnosis (RunWorkflow: LlmCall, role=debugger)
│   └── Produces: ranked hypotheses[]
│
├── DebugLoop (max iterations):
│   ├── SelectHypothesis (highest confidence untried)
│   ├── FlowDecision: debugContext?
│   │   ├── TddFailure:
│   │   │   └── ModifyImplementation (RunWorkflow: LlmCall, role=implementer)
│   │   ├── RuntimeError:
│   │   │   └── ApplyFix (RunWorkflow: LlmCall, role=implementer)
│   │   └── BugInvestigation:
│   │       ├── WriteRegressionTest (RunWorkflow: LlmCall, role=tester)
│   │       ├── RunRegressionTest → Guard: must FAIL
│   │       └── WriteFix (RunWorkflow: LlmCall, role=implementer)
│   ├── RunTests (RunWorkflow: Testing, 7-1C)
│   ├── FlowDecision: Tests pass?
│   │   ├── Yes → RecordResolution → CommitFix → SetOutputs(Resolved)
│   │   └── No → RefineHypothesis (RunWorkflow: LlmCall, role=debugger)
│   │       └── Loop with accumulated context
│   └── FlowDecision: Max iterations?
│       └── Yes → CompileDebugReport → EscalateWithContext → SetOutputs(Escalated)
│
└── SetOutputs (DebugResult)
```

### Custom Activities

```csharp
[Activity("Tamma.Debug", "Classify Debug Context",
    "Route based on debug mode: TDD failure, runtime error, or bug investigation")]
[FlowNode("TddFailure", "RuntimeError", "BugInvestigation")]
public class ClassifyDebugContextActivity : Activity { ... }

[Activity("Tamma.Debug", "Collect Error Messages",
    "Gather stack traces, logs, and test output")]
public class CollectErrorMessagesActivity : CodeActivity<ErrorMessages> { ... }

[Activity("Tamma.Debug", "AI Diagnosis",
    "Generate ranked root cause hypotheses")]
public class AIDiagnosisActivity : CodeActivity<DiagnosisResult> { ... }

[Activity("Tamma.Debug", "Select Hypothesis",
    "Pick highest-confidence untried hypothesis")]
public class SelectHypothesisActivity : CodeActivity<Hypothesis> { ... }

[Activity("Tamma.Debug", "Refine Hypothesis",
    "Update hypotheses based on failed fix attempt")]
public class RefineHypothesisActivity : CodeActivity<DiagnosisResult> { ... }

[Activity("Tamma.Debug", "Write Regression Test",
    "Write test that reproduces the bug (BugInvestigation mode)")]
public class WriteRegressionTestActivity : CodeActivity<TestGenerationResult> { ... }

[Activity("Tamma.Debug", "Compile Debug Report",
    "Generate comprehensive report for escalation")]
public class CompileDebugReportActivity : CodeActivity<DebugReport> { ... }

[Activity("Tamma.Debug", "Record Resolution",
    "Store resolution data for future reference")]
public class RecordResolutionActivity : CodeActivity { ... }
```

### Output Schema

```csharp
public record DebugResult
{
    public DebugStatus Status { get; init; }
    public string RootCause { get; init; } = string.Empty;
    public string FixApplied { get; init; } = string.Empty;
    public int Attempts { get; init; }
    public List<Hypothesis> Hypotheses { get; init; } = new();
    public bool RegressionTestAdded { get; init; }
    public List<string> FilesChanged { get; init; } = new();
    public string DebugReport { get; init; } = string.Empty;
}

public enum DebugStatus { Resolved, Unresolved, Escalated }

public record Hypothesis
{
    public int Rank { get; init; }
    public string Description { get; init; } = string.Empty;
    public decimal Confidence { get; init; }
    public string? FixAttempted { get; init; }
    public HypothesisOutcome Outcome { get; init; }
    public string? FailureReason { get; init; }
}

public enum HypothesisOutcome { Untried, FixedIssue, DidNotFix, MadeWorse }

public record DebugReport
{
    public List<Hypothesis> AllHypotheses { get; init; } = new();
    public List<FixAttempt> AllAttempts { get; init; } = new();
    public List<string> RemainingFailures { get; init; } = new();
    public List<string> FilesInvestigated { get; init; } = new();
    public List<string> SuggestedNextSteps { get; init; } = new();
    public TimeSpan TotalDebugTime { get; init; }
}
```

## Dependencies

- **7-1B (LLM Call)**: for AI diagnosis, fix generation, hypothesis refinement
- **7-1C (Testing)**: for running tests after each fix attempt
- **7-1F (Context Gathering)**: implicitly via debug context collection
- `Tamma.Activities.Integration.GitHubActivity` (existing) — for git operations
- ELSA 3.x `Flowchart`, `Fork`/`Join`, `FlowDecision`, `RunWorkflow` activities
- New agent role: `debugger` in `AgentsConfig.ProviderChains`

## Files to Create/Modify

| File | Action | Purpose |
|------|--------|---------|
| `Tamma.ElsaServer/Workflows/DebuggingWorkflow.cs` | Create | Code-first workflow |
| `Tamma.Activities/Debug/ClassifyDebugContextActivity.cs` | Create | Context routing |
| `Tamma.Activities/Debug/CollectErrorMessagesActivity.cs` | Create | Error collection |
| `Tamma.Activities/Debug/CollectRelevantCodeActivity.cs` | Create | Code collection |
| `Tamma.Activities/Debug/CollectGitHistoryActivity.cs` | Create | Git history |
| `Tamma.Activities/Debug/CollectTestResultsActivity.cs` | Create | Test results |
| `Tamma.Activities/Debug/CollectReproductionStepsActivity.cs` | Create | Bug repro steps |
| `Tamma.Activities/Debug/AIDiagnosisActivity.cs` | Create | Hypothesis generation |
| `Tamma.Activities/Debug/SelectHypothesisActivity.cs` | Create | Hypothesis selection |
| `Tamma.Activities/Debug/RefineHypothesisActivity.cs` | Create | Hypothesis refinement |
| `Tamma.Activities/Debug/WriteRegressionTestActivity.cs` | Create | Regression test |
| `Tamma.Activities/Debug/CompileDebugReportActivity.cs` | Create | Escalation report |
| `Tamma.Activities/Debug/RecordResolutionActivity.cs` | Create | Resolution recording |
| `Tamma.Activities/Debug/Models/` | Create | DTOs |
| `Tamma.ElsaServer/Program.cs` | Modify | Register workflow |
| `appsettings.json` | Modify | Add `debugger` role to provider chains |

## Testing Strategy

### Unit Tests
- Context classification: correct routing for all 3 modes
- Hypothesis ranking: highest confidence selected first
- Context accumulation: previous failures included in next iteration
- Bug investigation guard: regression test must fail before fix attempt
- Max iteration guard: loop exits after configured max
- Escalation report: includes all hypotheses and attempts

### Integration Tests
- **TddFailure mode**: test fails → diagnosis → fix → tests pass (mock LLM + CI)
- **RuntimeError mode**: error → diagnosis → fix → resolved
- **BugInvestigation mode**: investigate → regression test → fix → verify
- **Full escalation**: all hypotheses fail → comprehensive report generated
- Context accumulation: 3 iterations with progressively refined hypotheses
- Standalone invocation via ELSA REST API

### Performance Tests
- Debug context gathering: <500ms overhead
- Single debug iteration (excluding LLM/CI): <300ms overhead
- Debug report compilation: <100ms

## Configuration

```json
{
  "Debugging": {
    "MaxIterations": 5,
    "ContextCollectionTimeoutSeconds": 15,
    "EscalationChannel": "slack",
    "CommitMessageFormat": "fix({storyId}): {rootCause} [debug]",
    "BugInvestigation": {
      "RequireRegressionTest": true,
      "MaxReproductionAttempts": 3
    }
  }
}
```

## Integration with Main Workflow

The debugging workflow integrates with the main mentorship workflow at three points:

1. **Bug fast path** (from `INIT_STORY_PROCESSING`):
   - When issue is labeled as bug → `RunWorkflow: Debugging(mode=BugInvestigation)`
   - After resolution → skip to `QUALITY_GATE_CHECK`

2. **TDD failure** (from TDD workflow 7-1H):
   - When GREEN phase tests fail → `RunWorkflow: Debugging(mode=TddFailure)`
   - After resolution → return to TDD GREEN phase verification

3. **Unexpected error** (from `MONITOR_PROGRESS`):
   - When runtime error detected → `RunWorkflow: Debugging(mode=RuntimeError)`
   - After resolution → return to `MONITOR_PROGRESS`

## Success Metrics

- Debugging resolves issues without escalation >60% of the time
- Average iterations to resolution: <3 for TDD failures, <4 for bugs
- Regression test written for 100% of BugInvestigation fixes
- Hypothesis refinement produces better fix on subsequent iteration >70% of the time
- All hypotheses and fix attempts visible in ELSA Studio
- Debug report quality sufficient for human developer to continue (no re-investigation needed)

## Logging Requirements

All ELSA activities MUST inject `ILogger<T>` and log at these levels:

- **INFO**: Activity started (with session/issue ID), activity completed (with outcome), state transitions
- **DEBUG**: Input parameters received, intermediate LLM/API call details, decision rationale
- **WARN**: Retryable failures, timeout approaching, degraded quality gate result
- **ERROR**: Unrecoverable failures (with exception), invalid state transition, missing required data
- **Structured context**: Always include `{ sessionId, juniorId, storyId, currentState }` in all log entries
- **Sensitive data**: NEVER log student PII, credentials, or full LLM response content — log token counts and summary only
