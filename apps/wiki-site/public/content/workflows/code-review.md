---
title: "Workflow: Code Review"
---

**Definition ID:** `code-review`
**Class:** `CodeReviewWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CodeReviewWorkflow.cs`

## Purpose

The Code Review workflow manages the **full PR lifecycle** from creation through review, fix guidance, and merge. It uses bookmark-based waiting for external events (PR reviews, fix submissions) and supports up to 5 fix iterations before escalating to a senior developer.

## Flow Diagram

```
+------------------+
| Create PR        |
| (CreatePR        |
|  Activity)       |
+--------+---------+
         |
         v
+------------------+
| Store PR Result  |
+--------+---------+
         |
         v
+------------------+
| PR Created?      |
+--+------------+--+
  YES            NO
   |              |
   v              v
+------------------+  +------------------+
| Request Review   |  | Emit Failure     |
+--------+---------+  | Outputs          |
         |            +------------------+
         v
+------------------+
| Monitor Review   |<---------+
| (bookmark: waits |          |
|  for webhook)    |          |
+--+--+--+-----+--+          |
   |  |  |     |              |
   |  |  |     |              |
Approved |  Commented         |
   |  |  |     |              |
   |  |  |     +---->(self)   |
   |  |  |                    |
   | Changes   TimedOut       |
   | Requested    |           |
   |  |           v           |
   |  |  +------------------+ |
   |  |  | Escalate:        | |
   |  |  | Review Timeout   | |
   |  |  +--+----------+--+ |
   |  |    Resolved  Rejected |
   |  |       |          |    |
   |  |       v          v    |
   |  |   [Merge]   [Failure] |
   |  |                       |
   |  v                       |
   |  +------------------+   |
   |  | Store Review     |   |
   |  | Comments         |   |
   |  +--------+---------+   |
   |           |              |
   |           v              |
   |  +------------------+   |
   |  | Increment        |   |
   |  | Iteration        |   |
   |  +--------+---------+   |
   |           |              |
   |           v              |
   |  +------------------+   |
   |  | Deliver Fix      |   |
   |  | Guidance         |   |
   |  +--------+---------+   |
   |           |              |
   |           v              |
   |  +------------------+   |
   |  | Wait for Fixes   |   |
   |  | (bookmark)       |   |
   |  +--+------------+--+   |
   |   Fixes      TimedOut   |
   |   Received       |      |
   |     |            v      |
   |     |   [Escalate:      |
   |     |    Timeout]       |
   |     v                   |
   |  +------------------+  |
   |  | Re-Request Review|  |
   |  +--------+---------+  |
   |           |             |
   |           v             |
   |  +------------------+  |
   |  | Max Iterations?  |  |
   |  +--+------------+--+  |
   |    YES            NO   |
   |     |              |    |
   |     v              +----+
   |  +------------------+
   |  | Escalate:        |
   |  | Max Iterations   |
   |  +--+----------+--+
   |    Resolved  Rejected
   |       |          |
   |       v          v
   |   [Merge]   [Failure]
   |
   v
+------------------+
| Merge and        |
| Complete Review  |
+--------+---------+
         |
         v
+------------------+
| Emit Success     |
| Outputs          |
+------------------+
```

## Bookmark Points

This workflow has **4 bookmark suspension points** where it waits for external events:

| Bookmark | Activity | Waits For | Timeout |
|----------|----------|-----------|---------|
| Review monitoring | `MonitorReviewActivity` | PR review webhook (approved/changes requested) | 24 hours |
| Fix submission | `WaitForFixesActivity` | Push event on PR branch | 24 hours |
| Escalation (max iterations) | `EscalateReviewActivity` | Senior developer response | No timeout |
| Escalation (timeout) | `EscalateReviewActivity` | Senior developer response | No timeout |

## Review Outcomes

| Outcome | Source | Action |
|---------|--------|--------|
| **Approved** | `MonitorReview` | Proceed to merge |
| **ChangesRequested** | `MonitorReview` | Store comments, deliver guidance, wait for fixes |
| **Commented** | `MonitorReview` | Loop back to monitor (informational comment) |
| **TimedOut** | `MonitorReview` | Escalate to senior |
| **FixesReceived** | `WaitForFixes` | Re-request review |
| **Resolved** | `EscalateReview` | Proceed to merge |
| **Rejected** | `EscalateReview` | Fail the workflow |

## Fix Iteration Loop

When changes are requested:

1. Review comments are stored as JSON
2. Iteration counter is incremented
3. `DeliverGuidanceActivity` sends fix guidance to the junior developer
4. `WaitForFixesActivity` suspends until fixes are pushed (or times out)
5. `ReRequestReviewActivity` requests another review
6. If iterations < 5, loops back to `MonitorReview`
7. If iterations >= 5, escalates to senior

## Escalation

Two escalation scenarios:

| Reason | Trigger | Activity |
|--------|---------|----------|
| `MaxIterationsReached` | 5+ fix iterations without approval | `EscalateReview` |
| `ReviewTimeout` | Review or fix submission timed out (24h) | `EscalateTimeout` |

Both escalation activities support two outcomes: `Resolved` (merge) and `Rejected` (fail).

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `SessionId` | string | Session identifier |
| `StoryId` | string | Story being reviewed |
| `JuniorId` | string | Junior developer ID |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `success` | bool | Whether the review completed successfully |
| `prUrl` | string | PR URL (on success) |
| `iterations` | int | Number of review iterations |
| `errorMessage` | string | Error message (on failure) |

---

## Review Fix

**Definition ID:** `review-fix`
**Class:** `ReviewFixWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ReviewFixWorkflow.cs`

### Purpose

Analyzes PR review comments and applies AI-generated fixes. Used by the [Single Issue Cycle](Workflow-Single-Issue-Cycle) to process review feedback between CI runs.

### Flow

```
+------------------+
| Analyze Review   |
| (AnalyzeReview   |
|  Activity)       |
+--------+---------+
         |
         v
+------------------+
| Has Actionable?  |
+--+------------+--+
  YES            NO
   |              |
   v              |
+------------------+  |
| Generate Fixes  |  |
| (llm-call)      |  |
+--------+---------+  |
         |            |
         v            |
+------------------+  |
| Apply Fixes     |  |
| (ApplyReview    |  |
|  FixesActivity) |  |
+--------+---------+  |
         |            |
         v            |
+------------------+  |
| Update Code Index|  |
+--------+---------+  |
         |            |
         +-----+------+
               |
               v
       +------------------+
       | Output Success   |
       +--------+---------+
                |
                v
       +------------------+
       | Output Has       |
       | Comments         |
       +--------+---------+
                |
                v
       +------------------+
       | Output Fixes     |
       | Applied          |
       +------------------+
```

### Key Details

- `AnalyzeReviewActivity` fetches PR review comments and determines if any are actionable
- Fix generation dispatches the [LLM Call](Workflow-LLM-Call) workflow with role `"implementer"`
- All user-supplied review text is sanitized via `SecurityHelpers.SanitizeForPrompt()` before inclusion in the LLM prompt
- The code index is updated after fixes are applied (passes `null` for file paths so the indexer falls back to git-diff detection)

### Outputs

| Output | Type | Description |
|--------|------|-------------|
| `success` | bool | Always `true` (workflow itself doesn't fail) |
| `hasComments` | bool | Whether actionable review comments were found |
| `fixesApplied` | bool | Whether AI fixes were applied |

---

_See also: [Single Issue Cycle](Workflow-Single-Issue-Cycle) | [Mentorship](Workflow-Mentorship) | [Workflows Index](Workflows)_
