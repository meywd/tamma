---
title: "Workflow: Merge"
---

**Definition ID:** `merge-complete`
**Class:** `MergeWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/MergeWorkflow.cs`

## Purpose

The Merge workflow performs a squash-merge of a pull request, closes the associated issue, and deletes the feature branch. It uses `MergePullRequestActivity` for the actual merge operation, then outputs whether the merge succeeded and the resulting merge SHA.

## Flow Diagram

```
+------------------+
| Merge PR         |
| (MergePullReq    |
|  Activity)       |
+--------+---------+
         |
         v
+------------------+
| Set Success      |
| (merge SHA       |
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
| Output Merge SHA |
+------------------+
```

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier (owner/repo) |
| `prNumber` | int | Pull request number to merge |
| `issueNumber` | int | Issue number to close |
| `branchName` | string | Feature branch to delete after merge |

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `MergeSha` | string | The SHA of the merge commit |
| `Success` | bool | Whether the merge succeeded |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `success` | bool | Whether the merge was successful (merge SHA is non-empty) |
| `mergeSha` | string | SHA of the merge commit |

---

_See also: [Merge Approval](/workflows/merge-approval) | [Pull Request](/workflows/pull-request) | [Single Issue Cycle](/workflows/single-issue-cycle) | [Workflows Index](/workflows)_
