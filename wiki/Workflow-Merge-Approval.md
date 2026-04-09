---
title: "Workflow: Merge Approval"
---

**Definition ID:** `merge-approval`
**Class:** `MergeApprovalWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/MergeApprovalWorkflow.cs`

## Purpose

The Merge Approval workflow waits for a human merge/test/reject decision via bookmark. It uses `WaitForMergeApprovalActivity` to suspend execution until a human reviewer makes a decision, then outputs that decision along with any feedback.

## Flow Diagram

```
+------------------+
| Wait Merge       |
| Approval         |
| (bookmark:       |
|  human decision) |
+--------+---------+
         |
         v
+------------------+
| Output Decision  |
+--------+---------+
         |
         v
+------------------+
| Output Feedback  |
+------------------+
```

## Bookmark Points

| Bookmark | Activity | Waits For | Outcomes |
|----------|----------|-----------|----------|
| Merge approval | `WaitForMergeApprovalActivity` | Human reviewer decision (merge/test/reject) | Decision string + feedback |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `issueNumber` | int | Issue number |
| `prNumber` | int | Pull request number |
| `prUrl` | string | Pull request URL |

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `Decision` | string | The human's decision |
| `Feedback` | string | Optional feedback from the reviewer |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `decision` | string | The reviewer's decision (defaults to "reject" if null) |
| `feedback` | string | Reviewer feedback text |

---

_See also: [Merge](/workflows/merge) | [Code Review](/workflows/code-review) | [Single Issue Cycle](/workflows/single-issue-cycle) | [Workflows Index](/workflows)_
