---
title: "Workflow: CI with Debug Retry"
---

**Definition ID:** `ci-with-debug-retry`
**Class:** `CiWithDebugRetryWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CiWithDebugRetryWorkflow.cs`

## Purpose

The CI with Debug Retry workflow encapsulates the CI testing pipeline dispatch with up to 3 debug retry iterations on failure. When tests fail, it dispatches the Debugging workflow in `RuntimeError` mode and loops back to re-run the testing pipeline.

## Flow Diagram

```
+------------------+
| Init Inputs      |
+--------+---------+
         |
         v
+------------------+<------------------+
| Testing Pipeline |                   |
| (testing-        |                   |
|  pipeline)       |                   |
+--------+---------+                   |
         |                             |
         v                             |
+------------------+                   |
| Tests Passed?    |                   |
+--+------------+--+                   |
  YES            NO                    |
   |              |                    |
   v              v                    |
+----------+ +------------------+     |
| Finish   | | CI Retries < 3?  |     |
| Pass     | +--+------------+--+     |
+----+-----+   YES            NO      |
     |          |              |       |
     |          v              v       |
     |  +---------------+ +----------+|
     |  | Increment CI  | | Finish   ||
     |  | Retry         | | Fail     ||
     |  +-------+-------+ +----+-----+|
     |          |               |      |
     |          v               |      |
     |  +---------------+      |      |
     |  | Debug CI      |      |      |
     |  | Failure       |      |      |
     |  | (debugging)   |      |      |
     |  +-------+-------+      |      |
     |          |               |      |
     |          +---------------+------+
     |                          |
     v                          v
+----------------------------------+
| Complete: CI Retry Done          |
+----------------------------------+
```

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier (owner/repo) |
| `branchName` | string | Branch to test |
| `issueNumber` | int | Issue number |
| `skillLevel` | int | Developer skill level |
| `ciRetryCount` | int | Current CI retry counter (passed through, preserves state across re-entries) |

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `Repository` | string | Repository identifier |
| `BranchName` | string | Branch name |
| `IssueNumber` | int | Issue number |
| `SkillLevel` | int | Skill level |
| `CiRetryCount` | int | CI retry counter (0-3) |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `passed` | bool | Whether tests passed |
| `errorMessage` | string | Error message (on failure: "CI debug retry limit reached (3 attempts)") |
| `ciRetryCount` | int | Final CI retry count |

## Debug Retry Behavior

On test failure:
1. Checks if retry count < 3
2. Increments the CI retry counter
3. Dispatches the `debugging` workflow with `debugContextMode: "RuntimeError"` and the error output from the testing pipeline
4. Loops back to run the testing pipeline again
5. After 3 failed debug retries, outputs `passed: false`

## Known Issue

The `ciRetryCount` is passed through from the caller, which means the counter persists across re-entries (review-fix, merge re-test). This is likely a bug -- re-entry should reset the counter. Fix is tracked as a separate ticket.

---

_See also: [Testing Pipeline](/workflows/testing) | [Debugging](/workflows/debugging) | [Single Issue Cycle](/workflows/single-issue-cycle) | [Workflows Index](/workflows)_
