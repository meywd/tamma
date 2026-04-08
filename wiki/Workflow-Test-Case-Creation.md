---
title: "Workflow: Test Case Creation"
---

**Definition ID:** `test-case-creation`
**Class:** `TestCaseCreationWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TestCaseCreationWorkflow.cs`

## Purpose

The Test Case Creation workflow generates test cases from task plans for the TDD red phase. It dispatches `llm-call` with `role=tester` and `action=write-tests`, then validates the output contains test case JSON. This ensures the Red phase of TDD has pre-existing failing tests to work against.

The workflow retries up to 2 times on invalid output, feeding validation errors back into the prompt.

## Flow Diagram

```
+------------------+
|   Initialize     |
| (read inputs)    |
+--------+---------+
         |
         v
+------------------+
| Generate Tests   |
| (llm-call:       |
|  tester,         |
|  write-tests)    |
+--------+---------+
         |
         v
+------------------+
| Extract &        |
| Validate         |
| (parse JSON,     |
|  check tests[])  |
+--------+---------+
         |
         v
    +----------+
    | Tests    |
    | Valid?   |
    +---+--+---+
   Yes  |  |  No
        |  |
        v  +------+
  +---------+     |
  | Output  |     v
  | Tests   |  +--+------------+
  +----+----+  | Increment     |
       |       | Retry         |
       v       +-------+-------+
  +--------+           |
  | Finish |           v
  +--------+     +----------+
       ^         | Can      |
       |         | Retry?   |
       |         +---+--+---+
       |        Yes  |  |  No
       |             |  |
       |             v  +------+
       |    (loop to           |
       |     Generate Tests)   v
       |              +--+----------+
       |              | Error       |
       |              | Outputs     |
       +--------------+-------------+
```

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `Repository` | string | Repository identifier |
| `BranchName` | string | PR branch to commit tests to |
| `TasksJson` | string | Task plans JSON array |
| `ContextIds` | string | Context IDs JSON array |
| `TestCasesJson` | string | Extracted test cases JSON |
| `TestsValid` | bool | Whether extracted tests passed validation |
| `ValidationErrors` | string | Validation error messages |
| `RetryCount` | int | Current retry count (max 2) |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier |
| `branchName` | string | PR branch to commit tests to |
| `tasksJson` | string | Task plans JSON |
| `contextIds` | string | Context IDs JSON array |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `testCasesJson` | string | Generated test cases JSON (or `[]` on failure) |
| `error` | string | Validation errors (only on failure) |

## Validation Rules

- Response must contain valid JSON (array or object with `testCases` or `tests` property)
- The test cases array must be non-empty
- If the response is an object with a `testCases` or `tests` property, the array is extracted
- Invalid JSON, empty arrays, or missing properties trigger a retry

## Retry Behavior

- Maximum 2 retries (3 total attempts)
- Validation errors are fed back to the LLM in the next attempt via the `validationErrors` variable
- If all retries are exhausted, outputs `testCasesJson = "[]"` and the error message

---

_See also: [TDD Cycle](/workflows/tdd-cycle) | [Task Creation](/workflows/task-creation) | [Testing Pipeline](/workflows/testing) | [Workflows Index](/workflows)_
