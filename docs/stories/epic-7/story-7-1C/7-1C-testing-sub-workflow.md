# Story 7-1C: Testing Sub-Workflow

## User Story

As the **Tamma mentorship engine**, I need a reusable ELSA workflow that runs the full testing and quality pipeline with skill-level-aware thresholds, auto-fix loops, and teaching-oriented feedback so that every code submission goes through a consistent, auditable quality gate process.

## Description

Implement an ELSA code-first workflow (`TestingWorkflow`) that orchestrates the full testing/quality pipeline. The workflow triggers CI, waits for results via bookmark (pausing the workflow until a webhook callback), evaluates results against skill-level-aware thresholds, and optionally runs auto-fix loops using the LLM Call sub-workflow (7-1B). Each step — CI trigger, result evaluation, auto-fix attempt — is a separate ELSA activity visible in Studio.

**Enhances**: Stories 7-7 (Quality Gate), 7-8 (Progress Tracking)

## Acceptance Criteria

### AC1: Workflow Registration
- [ ] Workflow defined as C# code-first `IWorkflow` in `Tamma.ElsaServer/Workflows/TestingWorkflow.cs`
- [ ] Registered at startup via `services.AddWorkflow<TestingWorkflow>()`
- [ ] Visible in ELSA Studio as "Testing Pipeline" workflow
- [ ] Can be invoked standalone via ELSA REST API
- [ ] Can be invoked as child workflow via `RunWorkflow`

### AC2: Input/Output Contract
- [ ] **Inputs**:
  - `sessionId` (Guid) — mentorship session ID
  - `storyId` (string) — story identifier
  - `repositoryUrl` (string) — repository to test
  - `branchName` (string) — branch to test
  - `skillLevel` (int, 1-5) — junior's skill level for threshold selection
  - `qualityTierOverride` (string, optional) — override default tier
  - `testSubset` (string, optional) — run only specific tests (e.g., "new" for TDD red phase)
- [ ] **Outputs**: `QualityGateResult` record containing:
  - `passed` (bool) — overall pass/fail
  - `score` (int, 0-100) — composite quality score
  - `issues` (Issue[]) — list of issues found
  - `teachingFeedback` (string[]) — educational feedback for the junior
  - `coveragePercent` (decimal) — test coverage percentage
  - `lintErrors` (int) — linting error count
  - `securityIssues` (int) — security issue count
  - `autoFixAttempts` (int) — number of auto-fix attempts made
  - `autoFixSucceeded` (bool) — whether auto-fix resolved all issues

### AC3: CI Trigger and Wait
- [ ] `TriggerCI` activity: triggers GitHub Actions workflow (or configured CI system) via API
  - Supports GitHub Actions, GitLab CI, or generic webhook trigger
  - Passes branch name and optional test subset
  - Returns CI run ID for tracking
- [ ] `WaitForResults` activity: creates bookmark and pauses workflow
  - Bookmark name: `ci-result-{sessionId}-{runId}`
  - Resumes when webhook callback hits ELSA REST API with CI results
  - Timeout: 10 minutes (configurable) — if CI doesn't report back, fault
- [ ] CI result payload schema: `{ passed, coverage, lintErrors, securityIssues, testResults[], buildOutput }`

### AC4: Skill-Level-Aware Thresholds
- [ ] Quality thresholds vary by skill level:
  | Metric | Level 1 | Level 2 | Level 3 | Level 4 | Level 5 |
  |--------|---------|---------|---------|---------|---------|
  | Coverage | 60% | 70% | 75% | 80% | 90% |
  | Lint Errors | 10 | 5 | 3 | 1 | 0 |
  | Security Issues | 0 | 0 | 0 | 0 | 0 |
  | Build | Pass | Pass | Pass | Pass | Pass |
- [ ] Thresholds configurable via `appsettings.json` under `QualityThresholds`
- [ ] `qualityTierOverride` input can force a specific tier regardless of skill level

### AC5: Result Evaluation and Routing
- [ ] `EvaluateResults` activity classifies CI results:
  - **AllPass**: all metrics meet or exceed thresholds → `QualityGatePass` outcome
  - **MinorIssues**: lint errors or coverage slightly below threshold → `AutoFix` outcome
  - **MajorIssues**: significant failures, multiple categories → `ManualFix` outcome
  - **Critical**: security vulnerabilities or build failure → `Critical` outcome
- [ ] Classification logic:
  - Minor: (lint errors > threshold AND lint errors <= threshold * 2) OR (coverage within 5% of threshold)
  - Major: multiple categories failing OR test failures
  - Critical: any security issue OR build failure

### AC6: Auto-Fix Loop
- [ ] Auto-fix loop attempts to resolve minor issues automatically:
  1. `CallLlm` via RunWorkflow: LlmCall (7-1B, role=`implementer`, prompt="fix these lint issues: {issues}")
  2. `CommitFix` — commit the LLM-generated fix to the branch
  3. `RetriggerCI` — re-trigger CI pipeline
  4. `WaitForResults` — bookmark again for new results
  5. `EvaluateResults` — check if issues resolved
- [ ] Maximum 3 auto-fix attempts (configurable)
- [ ] Each attempt is a separate iteration visible in ELSA Studio
- [ ] If all 3 attempts fail → escalate to `ManualFix` outcome

### AC7: Teaching Feedback
- [ ] On any quality issue (minor, major, or critical), generate teaching feedback:
  - `CallLlm` via RunWorkflow: LlmCall (7-1B, role=`reviewer`, prompt="explain these quality issues to a Level {skillLevel} developer")
  - Feedback is educational, not just a list of errors
  - Adapts to skill level: Level 1 gets step-by-step explanations, Level 5 gets concise pointers
- [ ] Teaching feedback included in output regardless of pass/fail

### AC8: Progressive Quality Standards
- [ ] After 3 consecutive passes at current threshold, auto-tighten:
  - Coverage threshold increases by 5%
  - Lint error tolerance decreases by 1
  - Progressive tightening tracked in session variables
- [ ] Tightening is per-session (resets for new sessions)
- [ ] Tightening is optional and configurable (`ProgressiveQuality.Enabled`)

### AC9: Coverage, Linting, and Security Checks
- [ ] `CheckCoverage` activity: extracts coverage % from CI results, compares to threshold
- [ ] `CheckLinting` activity: extracts lint error count, compares to threshold
- [ ] `CheckSecurity` activity: extracts security issue count (must be 0 for all levels)
- [ ] `GenerateQualityReport` activity: produces composite score (0-100):
  - Coverage: 40% weight
  - Lint: 25% weight
  - Security: 25% weight
  - Build: 10% weight

## Technical Design

### Workflow Structure (Pseudocode)

```
Flowchart: TestingWorkflow
├── ValidateInputs
├── ResolveThresholds (skill level → thresholds)
├── TriggerCI
├── WaitForResults (bookmark — pauses until webhook)
├── EvaluateResults
│   ├── AllPass → GenerateQualityReport → GenerateTeachingFeedback → SetOutputs(passed=true)
│   ├── MinorIssues → AutoFixLoop:
│   │   ├── RunWorkflow: LlmCall (7-1B, role=implementer, "fix issues")
│   │   ├── CommitFix
│   │   ├── TriggerCI
│   │   ├── WaitForResults (bookmark)
│   │   ├── EvaluateResults
│   │   │   ├── Pass → break loop
│   │   │   └── Still failing → next iteration (max 3)
│   │   └── Loop exhausted → ManualFix outcome
│   ├── MajorIssues → GenerateTeachingFeedback → SetOutputs(passed=false)
│   └── Critical → SetOutputs(passed=false, critical=true)
├── CheckCoverage
├── CheckLinting
├── CheckSecurity
├── GenerateQualityReport
├── UpdateProgressiveThresholds (if 3 consecutive passes)
└── SetOutputs
```

### Custom Activities

```csharp
[Activity("Tamma.Testing", "Trigger CI", "Trigger CI pipeline via API")]
public class TriggerCIActivity : CodeActivity<CITriggerResult> { ... }

[Activity("Tamma.Testing", "Wait For CI Results", "Pause workflow until CI reports back")]
public class WaitForCIResultsActivity : Activity  // uses bookmarks
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var bookmarkName = $"ci-result-{sessionId}-{runId}";
        context.CreateBookmark(bookmarkName, OnCIResultReceived);
    }
}

[Activity("Tamma.Testing", "Evaluate Results", "Classify CI results against thresholds")]
[FlowNode("AllPass", "MinorIssues", "MajorIssues", "Critical")]
public class EvaluateResultsActivity : Activity { ... }

[Activity("Tamma.Testing", "Check Coverage", "Verify test coverage meets threshold")]
public class CheckCoverageActivity : CodeActivity<CoverageCheckResult> { ... }

[Activity("Tamma.Testing", "Check Linting", "Verify lint errors within threshold")]
public class CheckLintingActivity : CodeActivity<LintCheckResult> { ... }

[Activity("Tamma.Testing", "Check Security", "Verify no security vulnerabilities")]
public class CheckSecurityActivity : CodeActivity<SecurityCheckResult> { ... }

[Activity("Tamma.Testing", "Generate Quality Report", "Produce composite quality score")]
public class GenerateQualityReportActivity : CodeActivity<QualityReport> { ... }

[Activity("Tamma.Testing", "Commit Fix", "Commit auto-fix changes to branch")]
public class CommitFixActivity : CodeActivity<CommitResult> { ... }
```

### Output Schema

```csharp
public record QualityGateResult
{
    public bool Passed { get; init; }
    public int Score { get; init; }
    public List<QualityIssue> Issues { get; init; } = new();
    public List<string> TeachingFeedback { get; init; } = new();
    public decimal CoveragePercent { get; init; }
    public int LintErrors { get; init; }
    public int SecurityIssues { get; init; }
    public int AutoFixAttempts { get; init; }
    public bool AutoFixSucceeded { get; init; }
}

public record QualityIssue
{
    public string Category { get; init; } = string.Empty;   // coverage, lint, security, build
    public string Severity { get; init; } = string.Empty;   // minor, major, critical
    public string Message { get; init; } = string.Empty;
    public string? File { get; init; }
    public int? Line { get; init; }
}
```

## Dependencies

- **7-1B (LLM Call)**: for auto-fix generation and teaching feedback
- `Tamma.Activities.Integration.GitHubActivity` (existing) — for CI trigger and commit
- `IHttpClientFactory` for CI API calls
- ELSA 3.x `Flowchart`, `FlowDecision`, `RunWorkflow`, bookmark system
- CI system webhook endpoint (receives results and resumes bookmark)

## Files to Create/Modify

| File | Action | Purpose |
|------|--------|---------|
| `Tamma.ElsaServer/Workflows/TestingWorkflow.cs` | Create | Code-first workflow |
| `Tamma.Activities/Testing/TriggerCIActivity.cs` | Create | CI trigger |
| `Tamma.Activities/Testing/WaitForCIResultsActivity.cs` | Create | Bookmark-based wait |
| `Tamma.Activities/Testing/EvaluateResultsActivity.cs` | Create | Result classification |
| `Tamma.Activities/Testing/CheckCoverageActivity.cs` | Create | Coverage check |
| `Tamma.Activities/Testing/CheckLintingActivity.cs` | Create | Lint check |
| `Tamma.Activities/Testing/CheckSecurityActivity.cs` | Create | Security check |
| `Tamma.Activities/Testing/GenerateQualityReportActivity.cs` | Create | Composite score |
| `Tamma.Activities/Testing/CommitFixActivity.cs` | Create | Auto-fix commit |
| `Tamma.Activities/Testing/Models/` | Create | DTOs |
| `Tamma.ElsaServer/Program.cs` | Modify | Register workflow |

## Testing Strategy

### Unit Tests
- Threshold resolution: correct thresholds per skill level
- Result evaluation: AllPass/MinorIssues/MajorIssues/Critical classification
- Auto-fix loop: exits after max attempts
- Progressive quality: thresholds tighten after 3 consecutive passes
- Quality report scoring: weighted composite calculation

### Integration Tests
- Full workflow with mock CI (WireMock.Net webhook callback)
- Auto-fix loop: first fix fails, second succeeds
- Bookmark resume: workflow pauses at CI wait, resumes on callback
- Standalone invocation via ELSA REST API

## Configuration

```json
{
  "QualityThresholds": {
    "1": { "CoveragePercent": 60, "MaxLintErrors": 10, "MaxSecurityIssues": 0 },
    "2": { "CoveragePercent": 70, "MaxLintErrors": 5, "MaxSecurityIssues": 0 },
    "3": { "CoveragePercent": 75, "MaxLintErrors": 3, "MaxSecurityIssues": 0 },
    "4": { "CoveragePercent": 80, "MaxLintErrors": 1, "MaxSecurityIssues": 0 },
    "5": { "CoveragePercent": 90, "MaxLintErrors": 0, "MaxSecurityIssues": 0 }
  },
  "Testing": {
    "MaxAutoFixAttempts": 3,
    "CITimeoutSeconds": 600,
    "ProgressiveQuality": { "Enabled": true, "ConsecutivePassesRequired": 3 }
  }
}
```

## Success Metrics

- Skill-level thresholds correctly applied for all 5 levels
- Auto-fix loop resolves minor lint issues >60% of the time
- Bookmark-based CI wait survives server restart
- Teaching feedback generated for all non-passing results
- Composite quality score accurately reflects weighted metrics
- All activities visible in ELSA Studio execution log

## Logging Requirements

All ELSA activities MUST inject `ILogger<T>` and log at these levels:

- **INFO**: Activity started (with session/issue ID), activity completed (with outcome), state transitions
- **DEBUG**: Input parameters received, intermediate LLM/API call details, decision rationale
- **WARN**: Retryable failures, timeout approaching, degraded quality gate result
- **ERROR**: Unrecoverable failures (with exception), invalid state transition, missing required data
- **Structured context**: Always include `{ sessionId, juniorId, storyId, currentState }` in all log entries
- **Sensitive data**: NEVER log student PII, credentials, or full LLM response content — log token counts and summary only
