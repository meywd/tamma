---
title: "Workflow: Update Issue Status"
---

**Definition ID:** `update-issue-status`
**Class:** `UpdateIssueStatusWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/UpdateIssueStatusWorkflow.cs`

## Purpose

The Update Issue Status workflow is a small fire-and-forget sub-workflow that posts a status comment on a GitHub issue and optionally adds or removes labels. It is typically dispatched with `WaitForCompletion=false`. The underlying `UpdateIssueStatusActivity` has built-in retries.

## Flow Diagram

```
+------------------+
| Update Issue     |
| (UpdateIssue     |
|  StatusActivity) |
+------------------+
```

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier (owner/repo) |
| `issueNumber` | int | Issue number to update |
| `message` | string | Comment body to post |
| `addLabels` | string[] | Labels to add (optional) |
| `removeLabels` | string[] | Labels to remove (optional) |

## Key Details

- Dispatched as fire-and-forget (`WaitForCompletion=false`) by parent workflows
- The `UpdateIssueStatusActivity` has built-in retry logic for transient GitHub API failures
- No outputs -- this is a one-way notification workflow

---

_See also: [ADL Orchestrator](/workflows/adl-orchestrator) | [Single Issue Cycle](/workflows/single-issue-cycle) | [Workflows Index](/workflows)_
