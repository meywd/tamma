---
title: "Workflow: Deployment Pipeline"
---

**Definition ID:** `deployment-pipeline`
**Class:** `DeploymentPipelineWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DeploymentPipelineWorkflow.cs`

## Purpose

The Deployment Pipeline workflow handles post-merge deployment through three sequential stages: QA, UAT, and Production. Each stage dispatches an `llm-call` with `role=devops` and `action=deploy` to generate deployment instructions, then evaluates the result. If any stage fails, the pipeline stops and reports which stage failed.

## Flow Diagram

```
+------------------+
|   Initialize     |
| (read inputs)    |
+--------+---------+
         |
         v
+------------------+
| QA Deploy        |
| (llm-call:       |
|  devops, deploy) |
+--------+---------+
         |
         v
+------------------+
| Extract QA       |
| Result           |
+--------+---------+
         |
         v
    +----------+
    | QA OK?   |
    +---+--+---+
   Yes  |  |  No
        |  |
        v  +------+
+------------------+  |
| UAT Deploy       |  v
| (llm-call:       |  +--------+
|  devops, deploy) |  | QA     |
+--------+---------+  | Failed |
         |            +---+----+
         v                |
+------------------+      |
| Extract UAT      |      |
| Result           |      |
+--------+---------+      |
         |                |
         v                |
    +----------+          |
    | UAT OK?  |          |
    +---+--+---+          |
   Yes  |  |  No          |
        |  |              |
        v  +------+       |
+------------------+  |   |
| Prod Deploy      |  v   |
| (llm-call:       |  +--------+
|  devops, deploy) |  | UAT    |
+--------+---------+  | Failed |
         |            +---+----+
         v                |
+------------------+      |
| Extract Prod     |      |
| Result           |      |
+--------+---------+      |
         |                |
         v                |
    +----------+          |
    | Prod OK? |          |
    +---+--+---+          |
   Yes  |  |  No          |
        |  |              |
        v  +------+       |
  +---------+     |       |
  | Set     |     v       |
  | Success |  +--------+ |
  +----+----+  | Prod   | |
       |       | Failed | |
       v       +---+----+ |
  +---------+      |      |
  | Set     |<-----+------+
  | Outputs |
  +----+----+
       |
       v
  +---------+
  | Finish  |
  +---------+
```

## Deployment Stages

| Stage | LLM Variables | Description |
|-------|---------------|-------------|
| QA | `stage=qa` | Deploy to QA environment |
| UAT | `stage=uat` | Deploy to UAT environment |
| Production | `stage=production` | Deploy to production |

Each stage dispatches `llm-call` with:
- `role=devops`, `action=deploy`
- Variables: `stage`, `repository`, `mergeSha`, `issueNumber`, `branchName`, `completedStages`
- `enableTools=true`

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `Repository` | string | Repository identifier |
| `MergeSha` | string | Merge commit SHA to deploy |
| `IssueNumber` | int | Issue number |
| `BranchName` | string | Branch name |
| `DeploymentStatus` | string | Overall status: `pending`, `success`, or `failed:<stage>` |
| `CompletedStages` | string | JSON array of completed stage names |
| `CurrentStage` | string | Currently executing stage |
| `StageResult` | string | Result of current stage (`success` or `failed`) |

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
| `deploymentStatus` | string | `success` or `failed:<stage>` (e.g., `failed:qa`) |
| `completedStages` | string | JSON array of stages that completed successfully |

## Stage Result Extraction

Each stage's LLM response is parsed for a `status` field. If the response contains valid JSON with `{"status": "failed"}`, the stage is marked as failed. Otherwise the stage is treated as successful (optimistic default). Completed stages are accumulated in the `CompletedStages` array.

---

_See also: [Merge](/workflows/merge) | [Single Issue Cycle](/workflows/single-issue-cycle) | [Workflows Index](/workflows)_
