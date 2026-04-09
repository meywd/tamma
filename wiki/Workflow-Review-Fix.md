---
title: "Workflow: Review Fix"
---

**Definition ID:** `review-fix`
**Class:** `ReviewFixWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ReviewFixWorkflow.cs`

## Purpose

The Review Fix workflow analyzes PR review comments and applies AI-generated fixes. It fetches review comments via `AnalyzeReviewActivity`, determines if any are actionable, and if so dispatches the LLM Call workflow (role=implementer) to generate fixes. All user-supplied review text is sanitized via `SecurityHelpers.SanitizeForPrompt()` before inclusion in the LLM prompt. After fixes are applied, the code index is updated.

## Flow Diagram

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
| (llm-call:      |  |
|  implementer)   |  |
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
| Update Code      |  |
| Index            |  |
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

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `repository` | string | Repository identifier (owner/repo) |
| `prNumber` | int | Pull request number |
| `branchName` | string | Branch to apply fixes to |

## Variables

| Variable | Type | Description |
|----------|------|-------------|
| `HasActionable` | bool | Whether actionable review comments were found |
| `AnalysisJson` | string | Review analysis JSON |
| `FixesApplied` | bool | Whether AI fixes were applied |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `success` | bool | Always `true` (workflow itself does not fail) |
| `hasComments` | bool | Whether actionable review comments were found |
| `fixesApplied` | bool | Whether AI fixes were applied |

## Key Details

- `AnalyzeReviewActivity` fetches PR review comments and determines if any are actionable
- Fix generation dispatches the LLM Call workflow with role `"implementer"`
- All user-supplied review text is sanitized via `SecurityHelpers.SanitizeForPrompt()` before inclusion in the LLM prompt
- The code index is updated after fixes are applied (passes fixed file paths from the fix result, or `null` for git-diff detection fallback)

---

_See also: [Code Review](/workflows/code-review) | [LLM Call](/workflows/llm-call) | [Single Issue Cycle](/workflows/single-issue-cycle) | [Workflows Index](/workflows)_
