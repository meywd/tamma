---
title: "Workflow: Pull Request"
---

**Definition ID:** `pull-request`
**Class:** `PullRequestWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PullRequestWorkflow.cs`

## Purpose

The Pull Request workflow creates a pull request with a plan and test summary. It uses `CreatePullRequestActivity` to open a PR on the Git platform, then outputs the PR number and URL.

## Flow Diagram

```
+------------------+
| Create PR        |
| (CreatePullReq   |
|  Activity)       |
+--------+---------+
         |
         v
+------------------+
| Output Success   |
| (prNumber > 0?)  |
+--------+---------+
         |
         v
+------------------+
| Output PR Number |
+--------+---------+
         |
         v
+------------------+
| Output PR URL    |
+------------------+
```

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier (owner/repo) |
| `branchName` | string | Feature branch name |
| `baseBranch` | string | Base branch to merge into (defaults to "main") |
| `issueNumber` | int | Issue number to reference |
| `issueTitle` | string | Issue title for the PR title |
| `planJson` | string | Implementation plan JSON (included in PR body) |

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `PrNumber` | int | Created PR number |
| `PrUrl` | string | Created PR URL |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `success` | bool | Whether the PR was created (PR number > 0) |
| `prNumber` | int | The pull request number |
| `prUrl` | string | The pull request URL |

---

_See also: [Code Review](/workflows/code-review) | [Merge](/workflows/merge) | [Single Issue Cycle](/workflows/single-issue-cycle) | [Workflows Index](/workflows)_
