---
title: "Workflow: Branch Creation"
---

**Definition ID:** `branch-creation`
**Class:** `BranchCreationWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/BranchCreationWorkflow.cs`

## Purpose

The Branch Creation workflow creates a feature branch for autonomous development of an issue. It calls `CreateBranchActivity` to create the branch on the Git platform, then outputs whether the operation succeeded and the resulting branch name.

## Flow Diagram

```
+------------------+
| Create Branch    |
| (CreateBranch    |
|  Activity)       |
+--------+---------+
         |
         v
+------------------+
| Set Success      |
| (branch name     |
|  not empty?)     |
+--------+---------+
         |
         v
+------------------+
| Output Success   |
+--------+---------+
         |
         v
+------------------+
| Output Branch    |
| Name             |
+------------------+
```

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier (owner/repo) |
| `issueNumber` | int | Issue number to create the branch for |
| `issueTitle` | string | Issue title (used to generate branch name) |

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `BranchName` | string | The created branch name |
| `Success` | bool | Whether branch creation succeeded |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `success` | bool | Whether the branch was created (branch name is non-empty) |
| `branchName` | string | The created branch name |

---

_See also: [Single Issue Cycle](/workflows/single-issue-cycle) | [ADL Orchestrator](/workflows/adl-orchestrator) | [Workflows Index](/workflows)_
