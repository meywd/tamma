---
title: "Workflow: TDD Cycle"
---

**Definition ID:** `tdd-cycle`
**Class:** `TddWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TddWorkflow.cs`

## Purpose

The TDD Cycle drives the **Red-Green-Refactor** test-driven development loop for a single implementation task. It generates tests, writes implementation to make them pass, optionally refactors, and commits changes.

## Flow Diagram

```
+------------------+
| INIT             |
| (capture inputs, |
|  init counters)  |
+--------+---------+
         |
         v
+------------------+
| RED PHASE        |
+--------+---------+
         |
         v
+------------------+
| Write Tests      |<-------------------+
| (WriteTests      |                    |
|  Activity)       |                    |
+--------+---------+                    |
         |                              |
         v                              |
+------------------+                    |
| Mock: Run Tests  |                    |
| (expect FAIL)    |                    |
+--------+---------+                    |
         |                              |
         v                              |
+------------------+                    |
| Check Tests Fail |                    |
+---+----------+---+                    |
    |          |                        |
 TestsFail  TestsPass                   |
 (correct)  (bad tests)                 |
    |          |                        |
    |          v                        |
    |  +----------------+              |
    |  | Max Rewrites?  |              |
    |  +--+----------+--+              |
    |    YES          NO               |
    |     |            |                |
    |     |            v                |
    |     |    +-------------------+    |
    |     |    | Increment Rewrite |----+
    |     |    +-------------------+
    |     |
    v     v
+------------------+
| GREEN PHASE      |
+--------+---------+
         |
         v
+---------------------+
| Write Implementation|<-----------+
| (WriteImplementation|            |
|  Activity)          |            |
+---------+-----------+            |
          |                        |
          v                        |
+---------------------+           |
| Mock: Run Full Tests|           |
+---------+-----------+           |
          |                        |
          v                        |
+------------------+               |
| Tests Pass?      |               |
+--+------------+--+               |
  YES            NO                |
   |              |                |
   |              v                |
   |      +---------------+       |
   |      | Mark Debug    |       |
   |      +-------+-------+       |
   |              |                |
   |              v                |
   |      +---------------+       |
   |      | Increment     |       |
   |      | Debug Attempt |       |
   |      +-------+-------+       |
   |              |                |
   |              v                |
   |      +---------------+       |
   |      | Max Debug?    |       |
   |      +--+--------+--+       |
   |        YES        NO        |
   |         |          |         |
   |         v          +---------+
   |  +-------------+
   |  | Set Failed  |
   |  | Outputs     |---> Finish Failed
   |  +-------------+
   |
   v
+------------------+
| REFACTOR PHASE   |
+--------+---------+
         |
         v
+------------------+
| Analyze Code     |
| (AnalyzeCode     |
|  Activity)       |
+--------+---------+
         |
         v
+--------------------+
| Refactoring Needed?|
+--+--------------+--+
  YES              NO
   |                |
   v                |
+------------------+|
| Apply Refactoring||
+--------+---------+|
         |          |
         v          |
+------------------+|
| Mock: Run Tests  ||
+--------+---------+|
         |          |
         v          |
+------------------+|
| Refactor Tests   ||
| Pass?            ||
+--+------------+--+|
  YES            NO  |
   |              |  |
   |              v  |
   |  +---------------+
   |  | Revert        |
   |  | Refactoring   |
   |  +-------+-------+
   |          |
   +----+-----+
        |
        v
+------------------+
| Commit Changes   |
| (CommitChanges   |
|  Activity)       |
+--------+---------+
         |
         v
+------------------+
| Update Code Index|
+--------+---------+
         |
         v
+------------------+
| Set Completed    |
| Outputs          |---> Finish Success
+------------------+
```

## Three Phases

### RED Phase

1. **Write Tests** (`WriteTestsActivity`) -- Uses LLM to generate test code based on the task description, code context, and skill level. If this is a rewrite attempt, the previous test code is provided.
2. **Run Tests** -- Currently mocked (all tests expected to fail). Will be replaced with `testing-pipeline` dispatch.
3. **Check Tests Fail** (`CheckTestsFailActivity`) -- Validates that the new tests fail (correct TDD). If tests pass (bad tests), the workflow increments the rewrite counter and retries writing tests (max 2 rewrites). If max rewrites are exhausted, it proceeds to GREEN phase anyway.

### GREEN Phase

1. **Write Implementation** (`WriteImplementationActivity`) -- Uses LLM to write implementation code that makes the tests pass. Receives the test code, failure output, code context, and skill level.
2. **Run Full Tests** -- Currently mocked (all tests expected to pass). Will be replaced with `testing-pipeline` dispatch.
3. **Check Tests Pass** -- If tests pass, proceeds to REFACTOR. If tests fail, enters debug loop (up to 3 iterations) where `WriteImplementation` is called again with failure context.

### REFACTOR Phase

1. **Analyze Code** (`AnalyzeCodeActivity`) -- Uses LLM to analyze test and implementation code for refactoring opportunities. Returns a confidence score and list of suggestions.
2. **Refactoring Check** -- Only applies refactoring if confidence >= 0.6 and suggestions exist.
3. **Apply Refactoring** (`ApplyRefactoringActivity`) -- Applies the suggested refactoring changes.
4. **Verify Tests** -- Re-runs tests after refactoring. If tests fail, reverts the refactoring changes (`RevertRefactoringActivity`) and proceeds to commit with the pre-refactoring code.

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `sessionId` | Guid | Session ID for tracking |
| `storyId` | string | Story identifier |
| `taskDescription` | string | Description of the task to implement |
| `taskFiles` | List\<string\> | Files related to the task |
| `repositoryUrl` | string | Repository URL |
| `branchName` | string | Branch to commit to |
| `skillLevel` | int | Developer skill level (affects LLM prompts) |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `success` | bool | Whether the TDD cycle completed |
| `testCount` | int | Number of tests generated |
| `commitSha` | string | SHA of the commit |
| `filesChanged` | string | JSON array of changed file paths |
| `errorMessage` | string | Error message (on failure only) |

## Skill Level Adaptation

The `skillLevel` input affects:
- Test complexity and coverage expectations
- Implementation approach (more/fewer hints)
- Refactoring confidence threshold

## Current Mocked State

Test execution is currently mocked:
- RED phase: tests always "fail" (correct TDD behavior)
- GREEN phase: tests always "pass"
- REFACTOR phase: tests always "pass" after refactoring

These mocks will be replaced with `DispatchWorkflow` calls to the `testing-pipeline` workflow.

---

## TDD with Debug Retry

**Definition ID:** `tdd-with-debug-retry`
**Class:** `TddWithDebugRetryWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TddWithDebugRetryWorkflow.cs`

### Purpose

Wraps the TDD Cycle with an outer debug retry loop. If the TDD cycle fails, it dispatches the [Debugging workflow](Workflow-Debugging) and retries (up to 3 times).

### Flow Diagram

```
+-------------+
| Init Inputs |
+------+------+
       |
       v
+-------------+<------------------+
| TDD Cycle   |                   |
| (tdd-cycle) |                   |
+------+------+                   |
       |                          |
       v                          |
+-------------+                   |
| TDD Passed? |                   |
+--+-------+--+                   |
  YES       NO                    |
   |         |                    |
   v         v                    |
[Success] +----------------+     |
          | TDD Debug < 3? |     |
          +--+---------+---+     |
            YES         NO       |
             |           |        |
             v           v        |
     +---------------+ [Failure]  |
     | Increment     |            |
     | TDD Debug     |            |
     +-------+-------+            |
             |                    |
             v                    |
     +---------------+            |
     | Debug TDD     |            |
     | Failure       |            |
     | (debugging)   |            |
     +-------+-------+            |
             |                    |
             +--------------------+
```

### Inputs

| Input | Type | Description |
|-------|------|-------------|
| `storyId` | string | Story identifier |
| `planJson` | string | Implementation plan JSON |
| `repositoryUrl` | string | Repository URL |
| `branchName` | string | Branch name |
| `skillLevel` | int | Developer skill level |
| `issueNumber` | int | Issue number |

### Outputs

| Output | Type | Description |
|--------|------|-------------|
| `success` | bool | Whether TDD completed within retry limit |
| `errorMessage` | string | Error message (on failure) |

### Debug Retry Behavior

On TDD failure:
1. Increments the debug attempt counter
2. Dispatches the `debugging` workflow with `debugContextMode: "TddFailure"` and the error output from the failed TDD cycle
3. The debugging workflow applies a fix
4. Loops back to run TDD cycle again
5. After 3 failed debug retries, outputs `success: false` with `"TDD debug retry limit reached (3 attempts)"`

---

_See also: [Testing](Workflow-Testing) | [Debugging](Workflow-Debugging) | [Single Issue Cycle](Workflow-Single-Issue-Cycle) | [Workflows Index](Workflows)_
