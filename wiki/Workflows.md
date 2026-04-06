# Tamma ELSA Workflows

Tamma uses [ELSA Workflows](https://elsa-workflows.github.io/elsa-core/) (C# .NET 8) as its orchestration engine. All workflows are **code-first** (defined in C# classes extending `WorkflowBase`) and rendered as visual flowcharts in ELSA Studio.

This page is the index for all 30 workflows in the system.

## Workflow Inventory

| # | Workflow | Definition ID | Description | Page |
|---|----------|---------------|-------------|------|
| 1 | **ADL Orchestrator** | `adl-orchestrator` | Priority-based work item selection, fire-and-forget cycle dispatch, triage integration | [Details](Workflow-ADL-Orchestrator) |
| 2 | **Single Issue Cycle** | `single-issue-cycle` | 15-step autonomous development cycle for one issue (receives pre-selected work item from ADL) | [Details](Workflow-Single-Issue-Cycle) |
| 3 | **Issue Triage** | `issue-triage` | Fetch untriaged items, panel review, PO decision, apply labels | [Details](Workflow-Triage) |
| 4 | **Context Gathering** | `context-gathering` | Sequential role-based codebase scanning via LLM Call sub-workflow | [Details](Workflow-Context-Gathering) |
| 5 | **Plan Generation** | `plan-generation` | AI plan generation with human approval loop | [Details](Workflow-Single-Issue-Cycle#plan-generation) |
| 6 | **Plan Review** | `plan-review` | 7-role LLM panel review (architect, dev, QA, security, devops, PO, orchestrator) with iterative discussion rounds | [Details](Workflow-Single-Issue-Cycle#plan-review) |
| 7 | **Task Creation** | `task-creation` | Senior dev LLM breaks plan into deep implementation plans per task | [Details](Workflow-Single-Issue-Cycle#task-creation) |
| 8 | **Task Review** | `task-review` | 4-role LLM panel review (architect, senior dev, dev, QA) of implementation tasks | [Details](Workflow-Single-Issue-Cycle#task-review) |
| 9 | **Branch Creation** | `branch-creation` | Create a feature branch for the issue | [Details](Workflow-Single-Issue-Cycle#branch-creation) |
| 10 | **TDD Cycle** | `tdd-cycle` | Red-green-refactor TDD cycle for a single task | [Details](Workflow-TDD-Cycle) |
| 11 | **TDD with Debug Retry** | `tdd-with-debug-retry` | TDD cycle with up to 3 debug retry iterations | [Details](Workflow-TDD-Cycle#tdd-with-debug-retry) |
| 12 | **Test Case Creation** | `test-case-creation` | Generate test cases from task plans for TDD red phase | [Details](Workflow-Single-Issue-Cycle#test-case-creation) |
| 13 | **Pull Request** | `pull-request` | Create a draft PR with implementation plan `.md` files | [Details](Workflow-Single-Issue-Cycle#pull-request) |
| 14 | **Testing Pipeline** | `testing-pipeline` | CI trigger, wait, evaluate, auto-fix loop | [Details](Workflow-Testing) |
| 15 | **CI with Debug Retry** | `ci-with-debug-retry` | Testing pipeline with up to 3 debug retry iterations | [Details](Workflow-Testing#ci-with-debug-retry) |
| 16 | **Code Review** | `code-review` | Full PR lifecycle: create, review, fix, merge | [Details](Workflow-Code-Review) |
| 17 | **Review Fix** | `review-fix` | Analyze PR review comments and apply AI fixes | [Details](Workflow-Code-Review#review-fix) |
| 18 | **Merge Approval** | `merge-approval` | Bookmark-based human merge/test/reject decision | [Details](Workflow-Single-Issue-Cycle#merge-approval) |
| 19 | **Merge Complete** | `merge-complete` | Squash-merge PR, close issue, delete branch | [Details](Workflow-Single-Issue-Cycle#merge) |
| 20 | **Deployment Pipeline** | `deployment-pipeline` | Post-merge deployment: QA -> UAT -> Production | [Details](Workflow-Single-Issue-Cycle#deployment-pipeline) |
| 21 | **Update Issue Status** | `update-issue-status` | Fire-and-forget issue updates with tech-writer LLM summaries | [Details](Workflow-Single-Issue-Cycle#update-issue-status) |
| 22 | **LLM Call** | `llm-call` | Universal LLM call with provider chain and circuit breaker | [Details](Workflow-LLM-Call) |
| 23 | **Mentorship** | `mentorship` | 28-state mentorship session orchestration | [Details](Workflow-Mentorship) |
| 24 | **Assessment** | `assessment` | Junior developer skill assessment with AI | [Details](Workflow-Mentorship#assessment) |
| 25 | **Blocker Diagnosis** | `blocker-diagnosis` | 4-level progressive blocker resolution | [Details](Workflow-Blocker-Diagnosis) |
| 26 | **Debugging** | `debugging` | Systematic AI-driven debugging with 3 entry modes | [Details](Workflow-Debugging) |
| 27 | **Triage Item Cycle** | `triage-item-cycle` | Singleton: context → panel → PO → labels for one item | [Details](Workflow-Triage-Item-Cycle) |
| 28 | **Triage Context Gathering** | `triage-context-gathering` | Gather context for triage: code usage, deps, CVE, changelog | [Details](Workflow-Triage#triage-context-gathering) |
| 29 | **Triage Panel Review** | `triage-panel-review` | 4-role panel reviews item for triage (security/dev/devops/qa) | [Details](Workflow-Triage#triage-panel-review) |
| 30 | **Triage PO Decision** | `triage-po-decision` | PO makes final triage decision based on panel review | [Details](Workflow-Triage#triage-po-decision) |

## Dependency Diagram

The following shows which workflows dispatch which sub-workflows via `DispatchWorkflow`:

```
ADL Orchestrator (selects work items, manages concurrency)
  |
  +-- Issue Triage (fire & forget, when NeedsTriage)
  |     +-- Fetch Untriaged Items (issues + Dependabot + CodeQL)
  |     +-- For Each Item:
  |           +-- Triage Item Cycle (fire & forget, singleton — queued)
  |                 +-- Triage Context Gathering (wait)
  |                 +-- Triage Panel Review (wait) — security/dev/devops/qa
  |                 +-- Triage PO Decision (wait) — priority, labels, automation
  |                 +-- Apply Labels & Post Comment
  |
  +-- Single Issue Cycle (fire & forget, receives pre-selected work item)
        |
        +-- [parallel, every step] Update Issue Status (fire & forget)
        |     +-- LLM Call (tech-writer summary)
        |
        +-- Context Gathering (wait)
        +-- Plan Generation (wait)
        |     +-- LLM Call
        +-- Plan Review (wait)
        |     +-- LLM Call (7-role panel: arch/dev/qa/sec/devops/po/orch)
        +-- Task Creation (wait)
        |     +-- LLM Call (senior dev)
        +-- Task Review (wait)
        |     +-- LLM Call (4-role panel: arch/senior dev/dev/qa)
        +-- Branch Creation
        +-- Pull Request (draft, with plan .md files)
        +-- Test Case Creation (wait)
        |     +-- LLM Call
        +-- TDD Loop (per task in dependency order)
        |     +-- TDD Cycle
        |     +-- Testing Pipeline (CI inside TDD)
        |     +-- Debugging
        |           +-- LLM Call
        |           +-- Testing Pipeline
        +-- Code Review (fire & forget)
        |     +-- LLM Call
        |     +-- WaitForPRApproval (bookmark, blocks)
        +-- Merge Complete (fire & forget)
        |     +-- WaitForPRMerged (bookmark, blocks)
        +-- Deployment Pipeline (wait)
              +-- QA stage
              +-- UAT stage
              +-- Production stage

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

**Fire-and-forget dispatches:**
- ADL Orchestrator dispatches Single Issue Cycle and Issue Triage (`WaitForCompletion = false`)
- Issue Triage dispatches Triage Context Gathering, Triage Panel Review, and Triage PO Decision (`WaitForCompletion = true`)
- Single Issue Cycle dispatches Update Issue Status at every step (`WaitForCompletion = false`)
- Single Issue Cycle dispatches Code Review (`WaitForCompletion = false`), then blocks on PR approval bookmark
- Single Issue Cycle dispatches Merge Complete (`WaitForCompletion = false`), then blocks on PR merged bookmark

### Bookmark-Based Waiting

Several workflows pause and wait for external events (human approval, CI results, webhook notifications) using ELSA bookmarks. Key bookmark points:

- **Plan Generation** -- `WaitForPlanApprovalActivity` pauses for human plan approval
- **PR Approval** -- `WaitForPRApprovalActivity` pauses until code review approves the PR
- **PR Merged** -- `WaitForPRMergedActivity` pauses until PR is merged
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
