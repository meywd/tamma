---
title: "Workflow: Merge"
---

**Definition ID:** `merge`
**Class:** `MergeWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/MergeWorkflow.cs`

## Purpose

The Merge workflow squash-merges a pull request, closes the associated issue, and deletes the feature branch -- all handled internally by the single `MergePullRequestActivity`. There is no separate "Close Issue" or "Delete Branch" step in the workflow flowchart. The workflow then checks whether the merge SHA is non-empty to determine success, and exposes both the success flag and merge SHA as outputs.

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
| `branchName` | string | Feature branch name (passed to `MergePullRequestActivity` for deletion) |

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
