---
title: "Workflow: Debugging"
---

**Definition ID:** `debugging`
**Class:** `DebuggingWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DebuggingWorkflow.cs`

## Purpose

The Debugging workflow provides **systematic AI-driven debugging** with 3 entry modes. It collects context from 5 sources in parallel, uses AI to generate ranked hypotheses, then iteratively applies fixes and verifies them with tests (up to 5 iterations).

## Flow Diagram

```
+--------------------+
| Initialize         |
| (start time,       |
|  counters, flags)  |
+---------+----------+
          |
          v
+--------------------+
| Classify Debug     |
| Context            |
+--+------+------+---+
   |      |      |
 TDD    Runtime  Bug
 Failure Error  Invest.
   |      |      |
   v      v      v
 [TDD   [Runtime [Bug
  Emph]  Emph]    Emph]
   |      |      |
   +------+------+
          |
          v
+--------------------+
| Context Fork       |
| (parallel)         |
|                    |
| +-Collect Errors-+ |
| +-Collect Code---+ |
| +-Collect Git----+ |
| +-Collect Tests--+ |
| +-Collect Repro--+ |
+---------+----------+
          |
          v
+--------------------+
| Context Join       |
| (WaitAll)          |
+---------+----------+
          |
          v
+--------------------+
| AI Diagnosis       |
| (AIDiagnosis       |
|  Activity)         |
+---------+----------+
          |
          v
+--------------------+<-----------+
| Select Hypothesis  |            |
+---------+----------+            |
          |                       |
          v                       |
+--------------------+            |
| Has Hypothesis?    |            |
+--+--------------+--+            |
  YES              NO             |
   |                |             |
   |                v             |
   |         +-------------+     |
   |         | Compile     |     |
   |         | Debug Report|     |
   |         +------+------+     |
   |                |             |
   |                v             |
   |         [Set Escalated       |
   |          Outputs -> Finish]  |
   |                              |
   v                              |
+--------------------+            |
| Is Bug Mode?       |            |
+--+--------------+--+            |
  YES              NO             |
   |                |             |
   v                |             |
+--------------------+            |
| Write Regression   |            |
| Test               |            |
+---------+----------+            |
          |                       |
          v                       |
+--------------------+            |
| Mark Regression    |            |
| Test Written       |            |
+---------+----------+            |
          |                       |
          +------+                |
                 |                |
   +-------------+                |
   |                              |
   v                              |
+--------------------+            |
| Apply Fix          |            |
| (llm-call)         |            |
+---------+----------+            |
          |                       |
          v                       |
+--------------------+            |
| Run Tests          |            |
| (testing-pipeline) |            |
+---------+----------+            |
          |                       |
          v                       |
+--------------------+            |
| Tests Pass?        |            |
+--+--------------+--+            |
  YES              NO             |
   |                |             |
   v                v             |
+----------------+ +-------------+|
| Record         | | Refine      ||
| Resolution     | | Hypothesis  ||
+-------+--------+ +------+------+|
        |                  |       |
        v                  v       |
+----------------+ +------+------+|
| Update Code    | | Increment   ||
| Index          | | Iteration   |+
+-------+--------+ +-------------+
        |
        v
+--------------------+
| Set Resolved       |
| Outputs            |
+---------+----------+
          |
          v
+--------------------+
| Finish             |
+--------------------+
```

## Three Entry Modes

| Mode | Context | Focus | Typical Caller |
|------|---------|-------|---------------|
| **TddFailure** | Tests fail during GREEN phase | Making tests pass | TDD with Debug Retry |
| **RuntimeError** | Unexpected runtime errors | Broader investigation | CI with Debug Retry |
| **BugInvestigation** | Pre-implementation bug | TDD for bugs (writes regression test first) | Mentorship (bug fast path) |

The mode determines:
1. Which context collectors get emphasized
2. Whether a regression test is written before fixing
3. The emphasis in log messages

## Parallel Context Gathering

Five context sources are collected simultaneously using `FlowFork`/`FlowJoin`:

| Source | Activity | What It Collects |
|--------|----------|-----------------|
| Error Messages | `CollectErrorMessagesActivity` | Stack traces, error output, build errors |
| Relevant Code | `CollectRelevantCodeActivity` | Source files related to the failure |
| Git History | `CollectGitHistoryActivity` | Recent commits and changes |
| Test Results | `CollectTestResultsActivity` | Test pass/fail details |
| Reproduction Steps | `CollectReproductionStepsActivity` | Steps to reproduce the issue |

## AI Diagnosis

`AIDiagnosisActivity` sends all gathered context to an LLM to produce **ranked hypotheses**. Each hypothesis includes:
- Description of the suspected root cause
- Confidence score
- Suggested fix approach
- Evidence from the collected context

Previous iteration context is also provided so the LLM can refine hypotheses based on what has already been tried.

## Debug Loop (max 5 iterations)

For each iteration:

1. **Select Hypothesis** (`SelectHypothesisActivity`) -- Picks the highest-confidence untried hypothesis
2. **Guard: Has Hypothesis?** -- If no hypotheses remain, escalate
3. **Bug Mode Guard** -- If `BugInvestigation` mode and no regression test yet, write one first
4. **Apply Fix** -- Dispatches [LLM Call](Workflow-LLM-Call) with role `"implementer"` to apply the fix
5. **Run Tests** -- Dispatches [Testing Pipeline](Workflow-Testing) to verify
6. **Tests Pass?**
   - **Yes** -- Record resolution, update code index, output success
   - **No** -- Refine hypothesis, increment iteration, loop back

## Bug Investigation Mode

When `debugContextMode` is `"BugInvestigation"`:
1. Before the first fix attempt, `WriteRegressionTestActivity` generates a regression test
2. The regression test is committed to ensure the bug is captured
3. This flag is tracked to avoid rewriting the test on subsequent iterations

## Escalation

If all hypotheses are exhausted or max iterations (5) are reached:
1. `CompileDebugReportActivity` generates a comprehensive report
2. The report includes all hypotheses tried, test results, and remaining failures
3. The workflow outputs `success: false` with the debug report

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `sessionId` | Guid | Session identifier |
| `storyId` | string | Story identifier |
| `debugContextMode` | string | `"TddFailure"`, `"RuntimeError"`, or `"BugInvestigation"` |
| `errorOutput` | string | Error output from the failed operation |
| `relevantFiles` | string | JSON array of relevant file paths |
| `issueDescription` | string | Issue description (for BugInvestigation) |
| `repositoryUrl` | string | Repository URL |
| `branchName` | string | Branch name |
| `skillLevel` | int | Developer skill level |

## Outputs

### Success (resolved)

| Output | Type | Description |
|--------|------|-------------|
| `success` | bool | `true` |
| `resolution` | string | Debug result JSON |
| `iterations` | int | Number of iterations to resolve |

### Failure (escalated)

| Output | Type | Description |
|--------|------|-------------|
| `success` | bool | `false` |
| `debugReport` | string | Comprehensive debug report JSON |
| `iterations` | int | Number of iterations attempted |

## Sub-Workflows Dispatched

| Workflow | Purpose |
|----------|---------|
| [LLM Call](Workflow-LLM-Call) | Apply fix via AI |
| [Testing Pipeline](Workflow-Testing) | Verify fix by running tests |

## Security

Fix hypothesis descriptions are sanitized via `SecurityHelpers.SanitizeForPrompt()` before inclusion in LLM prompts.

---

_See also: [TDD Cycle](Workflow-TDD-Cycle) | [Testing](Workflow-Testing) | [Blocker Diagnosis](Workflow-Blocker-Diagnosis) | [Workflows Index](Workflows)_
