---
title: "Tamma ELSA Workflows"
---

Tamma uses [ELSA Workflows](https://elsa-workflows.github.io/elsa-core/) (C# .NET 8) as its orchestration engine. All workflows are **code-first** (defined in C# classes extending `WorkflowBase`) and rendered as visual flowcharts in ELSA Studio.

This page is the index for all 21 workflows in the system.

## Workflow Inventory

| # | Workflow | Definition ID | Description | Page |
|---|----------|---------------|-------------|------|
| 1 | **ADL Orchestrator** | `adl-orchestrator` | Priority-based work item selection, fire-and-forget cycle dispatch, triage integration | [Details](Workflow-ADL-Orchestrator) |
| 2 | **Single Issue Cycle** | `single-issue-cycle` | 14-step autonomous development cycle for one issue (receives pre-selected work item) | [Details](Workflow-Single-Issue-Cycle) |
| 3 | **Issue Triage** | `issue-triage` | LLM classification, labeling, and priority assignment for untriaged items | [Details](Workflow-Triage) |
| 4 | **Issue Selection** | `issue-selection` | Select and assign the next GitHub issue (legacy, now handled by ADL Orchestrator) | [Details](Workflow-Single-Issue-Cycle#issue-selection) |
| 5 | **Context Gathering** | `context-gathering` | Parallel context fetching from 6 sources with budget trimming | [Details](Workflow-Context-Gathering) |
| 6 | **Plan Generation** | `plan-generation` | AI plan generation with human approval loop | [Details](Workflow-Single-Issue-Cycle#plan-generation) |
| 7 | **Branch Creation** | `branch-creation` | Create a feature branch for the issue | [Details](Workflow-Single-Issue-Cycle#branch-creation) |
| 8 | **TDD Cycle** | `tdd-cycle` | Red-green-refactor TDD cycle for a single task | [Details](Workflow-TDD-Cycle) |
| 9 | **TDD with Debug Retry** | `tdd-with-debug-retry` | TDD cycle with up to 3 debug retry iterations | [Details](Workflow-TDD-Cycle#tdd-with-debug-retry) |
| 10 | **Pull Request** | `pull-request` | Create a PR with plan and test summary | [Details](Workflow-Single-Issue-Cycle#pull-request) |
| 11 | **Testing Pipeline** | `testing-pipeline` | CI trigger, wait, evaluate, auto-fix loop | [Details](Workflow-Testing) |
| 12 | **CI with Debug Retry** | `ci-with-debug-retry` | Testing pipeline with up to 3 debug retry iterations | [Details](Workflow-Testing#ci-with-debug-retry) |
| 13 | **Code Review** | `code-review` | Full PR lifecycle: create, review, fix, merge | [Details](Workflow-Code-Review) |
| 14 | **Review Fix** | `review-fix` | Analyze PR review comments and apply AI fixes | [Details](Workflow-Code-Review#review-fix) |
| 15 | **Merge Approval** | `merge-approval` | Bookmark-based human merge/test/reject decision | [Details](Workflow-Single-Issue-Cycle#merge-approval) |
| 16 | **Merge Complete** | `merge-complete` | Squash-merge PR, close issue, delete branch | [Details](Workflow-Single-Issue-Cycle#merge) |
| 17 | **LLM Call** | `llm-call` | Universal LLM call with provider chain and circuit breaker | [Details](Workflow-LLM-Call) |
| 18 | **Mentorship** | `mentorship` | 28-state mentorship session orchestration | [Details](Workflow-Mentorship) |
| 19 | **Assessment** | `assessment` | Junior developer skill assessment with AI | [Details](Workflow-Mentorship#assessment) |
| 20 | **Blocker Diagnosis** | `blocker-diagnosis` | 4-level progressive blocker resolution | [Details](Workflow-Blocker-Diagnosis) |
| 21 | **Debugging** | `debugging` | Systematic AI-driven debugging with 3 entry modes | [Details](Workflow-Debugging) |

## Dependency Diagram

The following shows which workflows dispatch which sub-workflows via `DispatchWorkflow`:

```
ADL Orchestrator (selects work items, manages concurrency)
  |
  +-- Issue Triage (fire & forget, when NeedsTriage)
  |
  +-- Single Issue Cycle (fire & forget, receives pre-selected work item)
        |
        +-- Context Gathering
        +-- Plan Generation
        |     +-- LLM Call
        +-- Branch Creation
        +-- TDD with Debug Retry
        |     +-- TDD Cycle
        |     +-- Debugging
        |           +-- LLM Call
        |           +-- Testing Pipeline
        +-- Pull Request
        +-- CI with Debug Retry
        |     +-- Testing Pipeline
        |     +-- Debugging
        |           +-- LLM Call
        |           +-- Testing Pipeline
        +-- Review Fix
        |     +-- LLM Call
        +-- Merge Approval
        +-- Merge Complete

Mentorship (independent top-level)
  |
  +-- Context Gathering
  +-- LLM Call
  +-- Assessment
  |     +-- Context Gathering
  |     +-- LLM Call (via GenerateQuestions)
  +-- TDD Cycle
  +-- Testing Pipeline
  +-- Code Review
  +-- Blocker Diagnosis
  |     +-- LLM Call
  +-- Debugging
        +-- LLM Call
        +-- Testing Pipeline
```

## Versioning

All workflows share a computed version number (`WorkflowVersions.ComputedVersion`) derived from a SHA256 hash of all workflow `.cs` files. When any workflow file changes, the hash changes, and ELSA publishes new versions on startup. This ensures changes to display text, structure, and logic always reach ELSA Studio without manual DB cleanup.

## Common Patterns

### DispatchWorkflow (Sub-Workflow Invocation)

Most inter-workflow calls use `DispatchWorkflow` with `WaitForCompletion = true`. Inputs are passed as `Dictionary<string, object>` and results come back as `IDictionary<string, object>?`.

**Exception:** The ADL Orchestrator dispatches Single Issue Cycle and Issue Triage as **fire-and-forget** (`WaitForCompletion = false`), allowing concurrent cycle execution while the orchestrator continues its selection loop.

### Bookmark-Based Waiting

Several workflows pause and wait for external events (human approval, CI results, webhook notifications) using ELSA bookmarks. Key bookmark points:

- **Plan Generation** -- `WaitForPlanApprovalActivity` pauses for human plan approval
- **Merge Approval** -- `WaitForMergeApprovalActivity` pauses for merge/test/reject decision
- **Testing Pipeline** -- `WaitForCIResultsActivity` pauses for CI webhook callback
- **Code Review** -- `MonitorReviewActivity` and `WaitForFixesActivity` pause for PR events
- **Assessment** -- `WaitForResponseActivity` pauses for junior developer response
- **Blocker Diagnosis** -- `DetectProgressActivity` and `EscalateToSeniorActivity` pause for progress/senior input

### Security

All workflows that construct LLM prompts from user-supplied data (issue titles, bodies, review comments) sanitize inputs via `SecurityHelpers.SanitizeForPrompt()` before inclusion in prompts.

### Code Index Updates

Workflows that modify code files (`TDD Cycle`, `Testing Pipeline`, `Review Fix`, `Debugging`) fire `UpdateCodeIndexActivity` after commits to keep the vector DB code index current.

---

_Source: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/`_
