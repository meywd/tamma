---
title: "Workflow: TDD with Debug Retry"
---

**Definition ID:** `tdd-with-debug-retry`
**Class:** `TddWithDebugRetryWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TddWithDebugRetryWorkflow.cs`

## Purpose

The TDD with Debug Retry workflow wraps the TDD Cycle with an outer debug retry loop. If the TDD cycle fails, it dispatches the Debugging workflow in `TddFailure` mode and retries the TDD cycle (up to 3 times).

## Flow Diagram

```
+------------------+
| Init Inputs      |
+--------+---------+
         |
         v
+------------------+<------------------+
| TDD Cycle        |                   |
| (tdd-cycle)      |                   |
+--------+---------+                   |
         |                             |
         v                             |
+------------------+                   |
| TDD Passed?      |                   |
+--+------------+--+                   |
  YES            NO                    |
   |              |                    |
   v              v                    |
+----------+ +------------------+     |
| Finish   | | TDD Debug < 3?  |     |
| Success  | +--+------------+--+     |
+----+-----+   YES            NO      |
     |          |              |       |
     |          v              v       |
     |  +---------------+ +----------+|
     |  | Increment     | | Finish   ||
     |  | TDD Debug     | | Failure  ||
     |  +-------+-------+ +----+-----+|
     |          |               |      |
     |          v               |      |
     |  +---------------+      |      |
     |  | Debug TDD     |      |      |
     |  | Failure       |      |      |
     |  | (debugging)   |      |      |
     |  +-------+-------+      |      |
     |          |               |      |
     |          +---------------+------+
     |                          |
     v                          v
+----------------------------------+
| Complete: TDD Retry Done         |
+----------------------------------+
```

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `storyId` | string | Story identifier |
| `planJson` | string | Implementation plan JSON |
| `repositoryUrl` | string | Repository URL |
| `branchName` | string | Branch name |
| `skillLevel` | int | Developer skill level |
| `issueNumber` | int | Issue number |

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `StoryId` | string | Story identifier |
| `PlanJson` | string | Implementation plan JSON |
| `RepositoryUrl` | string | Repository URL |
| `BranchName` | string | Branch name |
| `SkillLevel` | int | Skill level |
| `IssueNumber` | int | Issue number |
| `TddDebugAttempt` | int | Debug attempt counter (0-3) |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `success` | bool | Whether TDD completed within retry limit |
| `errorMessage` | string | Error message (on failure: "TDD debug retry limit reached (3 attempts)") |

## Debug Retry Behavior

On TDD failure:
1. Checks if debug attempt count < 3
2. Increments the TDD debug attempt counter
3. Dispatches the `debugging` workflow with `debugContextMode: "TddFailure"` and the error output from the failed TDD cycle
4. The debugging workflow applies a fix
5. Loops back to run the TDD cycle again
6. After 3 failed debug retries, outputs `success: false` with `"TDD debug retry limit reached (3 attempts)"`

---

_See also: [TDD Cycle](/workflows/tdd-cycle) | [Debugging](/workflows/debugging) | [Single Issue Cycle](/workflows/single-issue-cycle) | [Workflows Index](/workflows)_
