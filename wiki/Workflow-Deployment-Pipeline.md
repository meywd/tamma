---
title: "Workflow: Deployment Pipeline"
---

**Definition ID:** `deployment-pipeline`
**Class:** `DeploymentPipelineWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DeploymentPipelineWorkflow.cs`

## Purpose

**Status:** Stub -- structure defined, implementation pending.

The Deployment Pipeline workflow will handle deployment through QA, UAT, and Production environments with gates. It will manage releases, tags, changelog generation, and environment promotion.

## Flow Diagram

```
+------------------+
| Stub: Deployment |
| Pipeline -- TODO |
+------------------+
```

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier |
| `mergeSha` | string | Merge commit SHA to deploy |
| `issueNumber` | int | Issue number |
| `branchName` | string | Branch name |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `deploymentResult` | string | Deployment result (not yet implemented) |

---

_See also: [Merge](/workflows/merge) | [Single Issue Cycle](/workflows/single-issue-cycle) | [Workflows Index](/workflows)_
