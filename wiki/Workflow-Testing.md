# Workflow: Testing Pipeline

**Definition ID:** `testing-pipeline`
**Class:** `TestingWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TestingWorkflow.cs`

## Purpose

The Testing Pipeline runs the full **testing and quality gate pipeline** with skill-level-aware thresholds, bookmark-based CI wait, auto-fix loops, and teaching feedback generation.

## Flow Diagram

```
+--------------------+
| Trigger CI Pipeline|
| (TriggerCI         |
|  Activity)         |
+---------+----------+
          |
          v
+--------------------+
| Wait for CI Results|
| (bookmark: waits   |
|  for webhook)      |
+---------+----------+
          |
          v
+--------------------+
| Store CI Results   |
+---------+----------+
          |
          v
+--------------------+
| Evaluate CI Results|
| (EvaluateResults   |
|  Activity)         |
+--+--+------+----+--+
   |  |      |    |
   |  |      |    |
AllPass |  Major  Critical
   |  |  Issues    |
   |  |      |     |
   | Minor   |     v
   | Issues  |  Check Coverage (Critical)
   |  |      |     |
   |  +------+     v
   |  |      |  Check Lint (Critical)
   v  v      |     |
Check        |     v
Coverage     |  Check Security (Critical)
   |         |     |
   v         |     v
Check        |  Generate Report (Critical)
Linting      |     |
   |         |     v
   v         |  Output: Quality Report (Fail)
Check        |     |
Security     |     v
   |         |  Output: Passed Flag (Fail)
   v         |     |
Generate     |     v
Quality      |  Output: Teaching Feedback (Fail)
Report       |     |
   |         |     v
   v         |  Finish: Tests Failed
Output:      |
Quality      |
Report       +---> Fix Attempts Remaining?
(Pass)             +--+-------------+--+
   |                 YES              NO
   v                  |                |
Output:               v                v
Passed Flag    +-------------+  Output: Quality Report (Fail)
(Pass)         | Commit Fix  |     |
   |           +------+------+     v
   v                  |        Finish: Tests Failed
Output:               v
Teaching        +-------------+
Feedback        | Update Code |
(Pass)          | Index       |
   |            +------+------+
   v                   |
Finish:                v
Tests           +-------------+
Passed          | Increment   |
                | Fix Attempt |
                +------+------+
                       |
                       v
                +-------------+
                | Re-Trigger  |
                | CI          |
                +------+------+
                       |
                       v
                +-------------+
                | Wait for CI |
                | (Retry)     |
                +------+------+
                       |
                       v
                +-------------+
                | Store Retry |
                | Results     |
                +------+------+
                       |
                       v
                +-------------------+
                | Evaluate Retry    |
                | Results           |
                +--+--+------+--+--+
                   |  |      |  |
                AllPass |  Major Critical
                   |  Minor |    |
                   |  |     |    v
                   +--+     | [Fail]
                   |        |
                   v        +---> Fix Attempts
                Check            Remaining?
                Coverage         (loop)
                (Retry)
                   |
                   v
                Check Lint (Retry)
                   |
                   v
                Check Security (Retry)
                   |
                   v
                Generate Report (Retry)
                   |
                   v
                Output: Quality Report (Retry Pass)
                   |
                   v
                Finish: Tests Passed After Retry
```

## Evaluation Outcomes

The `EvaluateResultsActivity` classifies CI results into four categories:

| Outcome | Description | Action |
|---------|-------------|--------|
| **AllPass** | All tests pass, no issues | Run detailed checks, generate report, finish pass |
| **MinorIssues** | Non-critical warnings | Same as AllPass (report captures status) |
| **MajorIssues** | Fixable failures | Auto-fix loop (up to 3 attempts) |
| **Critical** | Unfixable failures | Run detailed checks, generate report, finish fail |

## Quality Checks Pipeline

Regardless of the evaluation outcome, three checks run in sequence:

1. **Check Coverage** (`CheckCoverageActivity`) -- Verifies code coverage meets skill-level thresholds
2. **Check Linting** (`CheckLintingActivity`) -- Validates linting rules are satisfied
3. **Check Security** (`CheckSecurityActivity`) -- Scans for security issues

Results feed into `GenerateQualityReportActivity` which produces a comprehensive quality report with teaching feedback.

## Auto-Fix Loop (MajorIssues)

When major issues are detected:

1. **Guard check** -- If fix attempts < max (3), proceed
2. **Commit Fix** (`CommitFixActivity`) -- Applies auto-fixes for fixable issues
3. **Update Code Index** -- Refreshes the vector DB code index
4. **Increment attempt counter**
5. **Re-trigger CI** -- Triggers a new CI run
6. **Wait for CI results** -- Bookmark-based wait
7. **Re-evaluate** -- If AllPass/MinorIssues, run checks and finish pass. If MajorIssues, loop. If Critical, fail.

If max attempts are reached, the workflow fails with the quality report.

## Skill-Level Adaptation

The `SkillLevel` input affects:
- Coverage threshold requirements (lower for beginners)
- Linting strictness
- Security check sensitivity
- Teaching feedback verbosity and depth

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `SessionId` | Guid | Session identifier |
| `Repository` | string | Repository URL |
| `Branch` | string | Branch to test |
| `SkillLevel` | int | Developer skill level (1-5) |
| `ConsecutivePassCount` | int | Number of consecutive passes (affects thresholds) |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `qualityReport` | string | Serialized `QualityReport` JSON |
| `passed` | bool | Whether the pipeline passed |
| `teachingFeedback` | string | Skill-level-appropriate feedback for the developer |

---

## CI with Debug Retry

**Definition ID:** `ci-with-debug-retry`
**Class:** `CiWithDebugRetryWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CiWithDebugRetryWorkflow.cs`

### Purpose

Wraps the Testing Pipeline with an outer debug retry loop. If CI fails, it dispatches the [Debugging workflow](Workflow-Debugging) and retries (up to 3 times).

### Flow Diagram

```
+-------------+
| Init Inputs |
+------+------+
       |
       v
+------------------+<-----------+
| Testing Pipeline |            |
| (testing-pipeline)|            |
+--------+---------+            |
         |                      |
         v                      |
+------------------+            |
| Tests Passed?    |            |
+--+------------+--+            |
  YES            NO             |
   |              |              |
   v              v              |
[Finish     +------------------+ |
 Pass]      | CI Retries < 3?  | |
            +--+------------+--+ |
              YES            NO  |
               |              |   |
               v              v   |
       +---------------+ [Finish  |
       | Increment     |  Fail]   |
       | CI Retry      |          |
       +-------+-------+          |
               |                   |
               v                   |
       +---------------+          |
       | Debug CI      |          |
       | Failure       |          |
       | (debugging)   |          |
       +-------+-------+          |
               |                   |
               +-------------------+
```

### Debug Context

When CI fails, the debugging workflow is dispatched with:
- `debugContextMode: "RuntimeError"` -- Broader investigation mode
- `errorOutput` -- The error message from the testing pipeline
- Repository, branch, and skill level context

### Outputs

| Output | Type | Description |
|--------|------|-------------|
| `passed` | bool | Whether CI passed within retry limit |
| `errorMessage` | string | Error message (on failure) |
| `ciRetryCount` | int | Final retry count (passed through to parent) |

### Known Issue

The `ciRetryCount` is passed through from the parent workflow, meaning it persists across re-entries (review-fix, merge re-test). This can cause premature failure if earlier CI runs consumed retries. Fix is tracked as a separate ticket.

---

_See also: [TDD Cycle](Workflow-TDD-Cycle) | [Debugging](Workflow-Debugging) | [Single Issue Cycle](Workflow-Single-Issue-Cycle) | [Workflows Index](Workflows)_
