# Tamma ELSA Workflows

Tamma uses [ELSA Workflows](https://elsa-workflows.github.io/elsa-core/) (C# .NET 8) as its orchestration engine. All workflows are **code-first** (defined in C# classes extending `WorkflowBase`) and rendered as visual flowcharts in ELSA Studio.

This page is the index for the 35 development-orchestration workflows in the system. Five additional platform/operations workflows (tenant provisioning, secret rotation, analytics rollup) are listed [at the end](#platform--operations-workflows) without dedicated pages — see [Multi-Tenant Provisioning](Multi-Tenant-Provisioning) for the tenant lifecycle ones.

## Workflow Inventory

| # | Workflow | Definition ID | Description | Page |
|---|----------|---------------|-------------|------|
| 1 | **ADL Orchestrator** | `adl-orchestrator` | Priority-based work item selection, fire-and-forget cycle dispatch, triage integration | [Details](Workflow-ADL-Orchestrator) |
| 2 | **Single Issue Cycle** | `single-issue-cycle` | 15-step autonomous development cycle for one issue (receives pre-selected work item from ADL) | [Details](Workflow-Single-Issue-Cycle) |
| 3 | **Issue Triage** | `issue-triage` | Fetch untriaged items, panel review, PO decision, apply labels | [Details](Workflow-Triage) |
| 4 | **Context Gathering** | `context-gathering` | Sequential role-based codebase scanning via LLM Call sub-workflow | [Details](Workflow-Context-Gathering) |
| 5 | **Plan Generation** | `plan-generation` | AI plan generation with human approval loop | [Details](Workflow-Plan-Generation) |
| 6 | **Plan Review** | `plan-review` | 7-role LLM panel review (architect, dev, QA, security, devops, PO, orchestrator) with iterative discussion rounds | [Details](Workflow-Plan-Review) |
| 7 | **Task Creation** | `task-creation` | Senior dev LLM breaks plan into deep implementation plans per task | [Details](Workflow-Task-Creation) |
| 8 | **Task Review** | `task-review` | 4-role LLM panel review (architect, senior dev, dev, QA) of implementation tasks | [Details](Workflow-Task-Review) |
| 9 | **Branch Creation** | `branch-creation` | Create a feature branch for the issue | [Details](Workflow-Branch-Creation) |
| 10 | **TDD Cycle** | `tdd-cycle` | Red-green-refactor TDD cycle for a single task | [Details](Workflow-TDD-Cycle) |
| 11 | **TDD with Debug Retry** | `tdd-with-debug-retry` | TDD cycle with up to 3 debug retry iterations | [Details](Workflow-TDD-With-Debug-Retry) |
| 12 | **Test Case Creation** | `test-case-creation` | Generate test cases from task plans for TDD red phase | [Details](Workflow-Test-Case-Creation) |
| 13 | **Pull Request** | `pull-request` | Create a draft PR with implementation plan `.md` files | [Details](Workflow-Pull-Request) |
| 14 | **Testing Pipeline** | `testing-pipeline` | CI trigger, wait, evaluate, auto-fix loop | [Details](Workflow-Testing) |
| 15 | **CI with Debug Retry** | `ci-with-debug-retry` | Testing pipeline with up to 3 debug retry iterations | [Details](Workflow-CI-With-Debug-Retry) |
| 16 | **Code Review** | `code-review` | Full PR lifecycle: create, review, fix, merge | [Details](Workflow-Code-Review) |
| 17 | **Review Fix** | `review-fix` | Analyze PR review comments and apply AI fixes | [Details](Workflow-Review-Fix) |
| 18 | **Merge Approval** | `merge-approval` | Bookmark-based human merge/test/reject decision | [Details](Workflow-Merge-Approval) |
| 19 | **Merge Complete** | `merge-complete` | Squash-merge PR, close issue, delete branch | [Details](Workflow-Merge) |
| 20 | **Deployment Pipeline** | `deployment-pipeline` | Post-merge deployment: QA -> UAT -> Production | [Details](Workflow-Deployment-Pipeline) |
| 21 | **Update Issue Status** | `update-issue-status` | Fire-and-forget issue updates with tech-writer LLM summaries | [Details](Workflow-Update-Issue-Status) |
| 22 | **LLM Call** | `llm-call` | Universal LLM call with provider chain and circuit breaker | [Details](Workflow-LLM-Call) |
| 23 | **Mentorship** | `mentorship` | 28-state mentorship session orchestration | [Details](Workflow-Mentorship) |
| 24 | **Assessment** | `assessment` | Junior developer skill assessment with AI | [Details](Workflow-Assessment) |
| 25 | **Blocker Diagnosis** | `blocker-diagnosis` | 4-level progressive blocker resolution | [Details](Workflow-Blocker-Diagnosis) |
| 26 | **Debugging** | `debugging` | Systematic AI-driven debugging with 3 entry modes | [Details](Workflow-Debugging) |
| 27 | **Triage Item Cycle** | `triage-item-cycle` | Singleton: context → panel → PO → labels for one item | [Details](Workflow-Triage-Item-Cycle) |
| 28 | **Triage Context Gathering** | `triage-context-gathering` | Gather context for triage: code usage, deps, CVE, changelog | [Details](Workflow-Triage-Context-Gathering) |
| 29 | **Triage Panel Review** | `triage-panel-review` | 4-role panel reviews item for triage (security/dev/devops/qa) | [Details](Workflow-Triage-Panel-Review) |
| 30 | **Triage PO Decision** | `triage-po-decision` | PO makes final triage decision based on panel review | [Details](Workflow-Triage-PO-Decision) |
| 31 | **Issue Decomposition** | `issue-decomposition` | Decompose a complex issue into ordered sub-tasks with dependencies via mediated LLM | [Details](Workflow-Issue-Decomposition) |
| 32 | **Research** | `research` | Autonomous investigation: context gathering + ranked, confidence-scored research report | [Details](Workflow-Research) |
| 33 | **Ambiguity Scoring** | `ambiguity-scoring` | Score requirement ambiguity (0..1 + itemised breakdown), threshold decides clarify vs proceed | [Details](Workflow-Ambiguity-Scoring) |
| 34 | **Clarifying Questions** | `clarifying-questions` | LLM-generated clarifying questions, human answers via bookmark, incorporate into clarified requirement | [Details](Workflow-Clarifying-Questions) |
| 35 | **Design Proposal** | `design-proposal` | Generate design proposal (alternatives + trade-offs), deliver to issue, human approve/reject gate | [Details](Workflow-Design-Proposal) |

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

Requirement-intelligence sub-workflows (Epics 2/3 — dispatched on demand by a parent flow)
  |
  +-- Ambiguity Scoring (autonomous, no bookmark)
  |     +-- LLM Call (product_owner/score-ambiguity)
  |     +-- decision output: "clarify" → parent dispatches Clarifying Questions;
  |         "proceed" → parent continues
  +-- Clarifying Questions
  |     +-- LLM Call (product_owner/clarify-requirements — generate questions)
  |     +-- WaitForClarifyingAnswers (bookmark + durable SLA, blocks)
  |     +-- LLM Call (product_owner/clarify-requirements — incorporate answers)
  +-- Research (autonomous, no bookmark)
  |     +-- Context Gathering
  |     +-- LLM Call (product_owner/research)
  +-- Issue Decomposition (autonomous, no bookmark)
  |     +-- Context Gathering
  |     +-- LLM Call (senior_developer/decompose-issue)
  +-- Design Proposal
        +-- LLM Call (architect/plan-system-design)
        +-- Deliver Design Proposal (mediated git seam, issue comment)
        +-- WaitForDesignApproval (bookmark + durable SLA, blocks)
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
- **Clarifying Questions** -- `WaitForClarifyingAnswersActivity` pauses for human answers (durable SLA timeout, resumed via `POST /api/adl/clarify/resume`)
- **Design Proposal** -- `WaitForDesignApprovalActivity` pauses for the reviewer's approve/reject decision (durable SLA timeout, resumed via `POST /api/adl/design/resume`)

### Security

All workflows that construct LLM prompts from user-supplied data (issue titles, bodies, review comments) sanitize inputs via `SecurityHelpers.SanitizeForPrompt()` before inclusion in prompts.

### Code Index Updates

Workflows that modify code files (`TDD Cycle`, `Testing Pipeline`, `Review Fix`, `Debugging`) fire `UpdateCodeIndexActivity` after commits to keep the vector DB code index current.

## Platform / Operations Workflows

Five further Elsa workflows run platform plumbing rather than the development loop. They live in the same `Workflows/` folder but have no dedicated wiki pages:

| Workflow | Definition ID | Description |
|----------|---------------|-------------|
| **Create Tenant** | `create-tenant` | Provision a new tenant: placement + role + schema + migration + encrypted creds + activate |
| **Delete Tenant** | `delete-tenant` | Tear down a tenant: mark deleting, evict pool, backup, drop schema + role (continue-on-error; triggered via `TenantDeleteRequestedTrigger`) |
| **Clean Up Failed Tenant** | `clean-up-failed-tenant` | Operator-triggered best-effort teardown for a tenant in a damaged state (triggered via `TenantCleanupRequestedTrigger`) |
| **Rotate Secret** | `rotate-secret` | Generic secret-rotation saga: mint → push → probe → activate → retire (postgres/cranl/hmac/generic-http handlers) |
| **Hourly Analytics Rollup** | `hourly-analytics-rollup` | Rolls `platform_events` + per-tenant `domain_events` into `platform_analytics_hourly` (armed by `HourlyAnalyticsRollupScheduler`) |

See [Multi-Tenant Provisioning](Multi-Tenant-Provisioning) for the tenant lifecycle context.

---

_Source: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/`_
