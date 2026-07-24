---
title: "Epic 2: Autonomous Development Loop"
sidebar:
  order: 2
---

**Status:** Near Complete (14/20 done — 2-14 landed 2026-07-05; 2-15/2-16 ready-for-dev; 2-17..2-20 drafted for workflow redesign)
**Stories:** 20 (2-1 through 2-20)
**Tech Spec:** [tech-spec-epic-2.md](/stories/epic-2//tech-spec-epic-2.md)
**Retrospective:** Completed

## Overview

Epic 2 is the heart of Tamma — the **Autonomous Development Loop (ADL)** that takes a GitHub/GitLab issue and drives it through plan → approve → code → test → PR → merge without manual intervention. It turns the Epic-1 engine skeleton into a full 14-step pipeline, adds approval checkpoints for the two decisions a human still owns (plan OK? merge OK?), and layers in the "intelligence" capabilities that keep the loop from getting stuck: intelligent provider selection, prompt optimization, issue decomposition, and priority-based work-item selection.

The loop is implemented as a two-level Elsa workflow in C#: an outer **ADL Orchestrator** that runs continuously, picks the highest-priority work item, and fires off a fire-and-forget inner **SingleIssueCycle** for that item. The inner cycle is the actual 14-step pipeline — validate → context → plan → review → tasks → branch → TDD → PR → CI → review → merge → report. The TypeScript `@tamma/orchestrator` package mirrors the logic for the CLI self-hosted mode; the C# workflows run in the production Elsa server.

The "advanced intelligence" stories (2-12..2-16) add provider-aware routing (cost + capability + availability), prompt engineering feedback loops, and the ability to decompose one large issue into a dependency graph of smaller tasks that run incrementally. The "workflow optimization" stories (2-17..2-20) are a second wave that redesigns context gathering, plan generation, and plan review, and generalizes issue selection into multi-source priority selection (issues, security alerts, failed CI, stale PRs).

## Architecture

The outer loop lives in `AdlOrchestratorWorkflow` (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AdlOrchestratorWorkflow.cs`). Each iteration calls `SelectWorkItemActivity` (priority-based multi-source), `CheckLimitsActivity` (enforces concurrency cap by querying the Elsa instance store), then `DispatchCycleActivity` which starts a fresh `SingleIssueCycleWorkflow` as fire-and-forget. A cooldown applies between iterations. Exit paths (no work, limits hit, fatal error) flow through `SetExitReasonActivity`.

The inner `SingleIssueCycleWorkflow` is a flowchart: each activity extends `TammaActivity` / `TammaAsyncActivity` / `TammaOutcomeActivity`, which automatically emit lifecycle events (start/success/failure) to the event store. Happy-path activities chain through context gathering, plan generation, plan review (with approval outcome branching to approved / defer / split / needsHuman), task creation, TDD, PR creation, CI polling, code review, merge approval, merge, and final reporting back to the orchestrator.

The TypeScript mirror (`packages/orchestrator/src/engine.ts` — `TammaEngine`) follows the same state machine (`EngineState` enum) but runs in-process for CLI mode. Both paths consume the same `IGitPlatform` (Epic 1) and the same agent providers; only the scheduling surface differs.

## Components

| Component | Purpose | Key files | Status |
|-----------|---------|-----------|--------|
| `AdlOrchestratorWorkflow` | Outer priority-based continuous loop | `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AdlOrchestratorWorkflow.cs` | Done |
| `SingleIssueCycleWorkflow` | Inner 14-step pipeline per issue | `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` | Done |
| `SelectWorkItemActivity` | Multi-source priority-based selection (issues, alerts, CI, stale PRs) | `apps/tamma-elsa/src/Tamma.Activities/ADL/SelectWorkItemActivity.cs` | Done (2-1, 2-20) |
| `ValidateWorkItemActivity` | Pre-flight validation (repo exists, permissions, label) | `Tamma.Activities/ADL/ValidateWorkItemActivity.cs` | Done |
| `ContextGatheringWorkflow` | Gather code, docs, learnings (Epic 6 RAG) | `Tamma.ElsaServer/Workflows/ContextGatheringWorkflow.cs` | Done (2-2, redesigned 2-17) |
| `PlanGenerationWorkflow` | LLM plan generation with structured output | `Tamma.ElsaServer/Workflows/PlanGenerationWorkflow.cs` | Done (2-3, redesigned 2-18) |
| `WaitForPlanApprovalActivity` | Block for human approval of plan | `Tamma.Activities/ADL/WaitForPlanApprovalActivity.cs` | Done (2-3) |
| `PlanReviewWorkflow` | Multi-outcome review: approved / defer / split / needsHuman | `Tamma.ElsaServer/Workflows/PlanReviewWorkflow.cs` | Done (2-3, redesigned 2-19) |
| `TaskCreationWorkflow` | Decompose approved plan into ordered tasks | `Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` | Done (2-14..2-16) |
| `BranchCreationWorkflow` | Create feature branch from base ref | `Tamma.ElsaServer/Workflows/BranchCreationWorkflow.cs` | Done (2-4) |
| `TddWorkflow` + `TddWithDebugRetryWorkflow` | Red-green-refactor per task with retry on debug context | `Tamma.ElsaServer/Workflows/Tdd*.cs` | Done (2-5..2-7) |
| `CreatePullRequestActivity` | Create PR via `IGitPlatform` | `Tamma.Activities/ADL/CreatePullRequestActivity.cs` | Done (2-8) |
| `CiWithDebugRetryWorkflow` | Poll CI, on failure collect debug context, retry | `Tamma.ElsaServer/Workflows/CiWithDebugRetryWorkflow.cs` | Done (2-9) |
| `CodeReviewWorkflow` + `ReviewFixWorkflow` | AI review + apply fixes | `Tamma.ElsaServer/Workflows/CodeReviewWorkflow.cs`, `ReviewFixWorkflow.cs` | Done |
| `MergeApprovalWorkflow` + `MergeWorkflow` | Human approval → merge PR | `Tamma.ElsaServer/Workflows/Merge*.cs` | Done (2-10) |
| `TammaEngine` (TS mirror) | In-process engine for CLI mode | `packages/orchestrator/src/engine.ts` | Done |
| Intelligent provider selection | Route task to best provider by task type + cost + availability | `packages/providers/src/provider-chain.ts` | Done (2-12) |
| Prompt engineering | Per-role prompt library + A/B testing scaffold | `packages/providers/src/agent-prompt-registry.ts` | Done (2-13) |
| `IssueDecompositionWorkflow` | Break a complex issue into an ordered, dependency-declared sub-task set via mediated LLM; `DECOMPOSITION.*` events | `Tamma.ElsaServer/Workflows/IssueDecompositionWorkflow.cs`, `Tamma.Activities/Decomposition/*` | Done (2-14) |
| Task dependency graph + incremental sequencing | Full graph-based dependency resolution / execution | `docs/stories/epic-2/story-2-15/`, `story-2-16` | Ready-for-dev |

## Class diagram

```
    AdlOrchestratorWorkflow  (Elsa WorkflowBase)
    - name = "ADL Orchestrator"
    + Build(builder)
         |
         | calls
         v
    +---- InitAdlConfigActivity ----+
    |                               |
    |   loops:                      |
    |   SelectWorkItemActivity      ---> 3 outcomes: Selected | NothingFound | NeedsTriage
    |        |                      |
    |        +--> CheckLimitsActivity ---> 2 outcomes: Continue | Stop
    |        |         |
    |        |         +--> DispatchCycleActivity (fire-and-forget)
    |        |                     |
    |        |                     v
    |        +--> CooldownActivity  SingleIssueCycleWorkflow <----+
    |                  |                                          |
    +<-----------------+                                          |
                                                                  |
    SingleIssueCycleWorkflow  (Elsa WorkflowBase)                  |
    + Build(builder) flowchart:                                    |
         ValidateWorkItem                                           |
           -> ContextGatheringWorkflow                              |
           -> PlanGenerationWorkflow                                |
           -> PlanReviewWorkflow (approved|defer|split|needsHuman) |
                |                                                   |
                +- approved -> TaskCreationWorkflow                 |
                                -> BranchCreationWorkflow           |
                                -> foreach task: TddWorkflow        |
                                -> CreatePullRequestActivity        |
                                -> CiWithDebugRetryWorkflow         |
                                -> CodeReviewWorkflow               |
                                -> MergeApprovalWorkflow            |
                                -> MergeWorkflow                    |
                                -> ReportCycleResultActivity -------+

    Every activity extends one of:
      TammaActivity       (sync, auto event emission)
      TammaAsyncActivity  (async)
      TammaOutcomeActivity (multi-outcome branching)

    Parallel TS mirror:
    TammaEngine
    + run()            // continuous poll
    + processOneIssue()// single shot
    - state : EngineState (IDLE|SELECTING|ANALYZING|PLANNING|...)
```

## Data flow — full single-issue happy path

```
ADL Orch    SingleIssueCycle   AI Agent       GitHub      Human (approve)    Event Store
   |             |                |             |             |                 |
   | select      |                |             |             |                 |
   |--> pick WorkItem by priority |             |             |                 |
   |---- dispatchCycle(wi) ------>|             |             |                 |
   |             | validateWI    |             |             |                 |
   |             |                |             |             |                 |
   |             | gatherContext -|------ fetch repo + RAG -->|                 |
   |             |                |                          |                 |
   |             | generatePlan --|--> LLM with context       |                 |
   |             |                |<-- structured plan JSON   |                 |
   |             |                |                          |                 |
   |             | reviewPlan   --|--> plan+self-review       |                 |
   |             |                |<-- approved|defer|split|  |                 |
   |             |                |     needsHuman outcome    |                 |
   |             |                |                          |                 |
   |             | awaitPlanApproval ------ PR comment ------>|                 |
   |             |   (suspended on bookmark)                  |                 |
   |             |<---------------- approve comment --------- |                 |
   |             |                                                              |
   |             | createTasks ---|--> decompose into ordered task list         |
   |             |                                                              |
   |             | createBranch -|------ POST /git/refs --->|                   |
   |             |                                                              |
   |             | foreach task: TDD cycle                                       |
   |             |   writeTests  -|--> red                                       |
   |             |   writeImpl   -|--> green                                     |
   |             |   refactor    -|--> still green                              |
   |             |   commit      -|----- POST /git/commits ->                   |
   |             |                | ....on CI fail -> DebuggingWorkflow -> retry|
   |             |                                                              |
   |             | createPR     -|----- POST /pulls -------->|                  |
   |             |                                                              |
   |             | pollCI       -|----- GET /check-runs ---->|                  |
   |             |                |<---- success ------------|                  |
   |             |                                                              |
   |             | codeReview    -|--> LLM review             |                 |
   |             | reviewFix     -|--> apply suggestions      |                 |
   |             |                                                              |
   |             | awaitMergeApproval ---- PR comment ------->|                 |
   |             |<---------- approve comment --------- |                       |
   |             |                                                              |
   |             | mergePR      -|----- PUT /merge --------->|                  |
   |             |                                                              |
   |             | reportCycleResult ----- emit SUCCESS ------------------->|    |
   |<----- result (success|failure|deferred|split|needsHuman) ---|               |
   |                                                              |              |
   | next iteration                                                              |
```

Every activity auto-emits `{activity}.{action}.{status}` events to the event store (Epic 4).

## Use cases

- **Maintainer triages backlog** wants **Tamma to work only on labelled issues**: label an issue `tamma-auto` → `SelectWorkItemActivity` picks it up at next poll → cycle runs → PR opened on the repo.
- **Security lead** wants **Dependabot criticals addressed first**: `SelectWorkItemActivity` ranks "security alerts (critical)" highest → outer loop always picks them before normal-priority issues (Story 2-20).
- **Dev wants to review plan before any code is written**: plan generates → `WaitForPlanApprovalActivity` posts PR comment with plan → workflow suspends on bookmark → dev replies `approve` / `defer` / `split` → flow resumes on that branch.
- **Ops wants to cap concurrency** so the engine doesn't spawn 50 parallel cycles: set `max_concurrent_cycles=3` → `CheckLimitsActivity` queries `IWorkflowInstanceStore` for active `SingleIssueCycle` instances; if ≥3, loop yields via cooldown.
- **Dev files a big-bang issue**: "add user auth end-to-end" — plan review returns `split` outcome → `SingleIssueCycle` creates sub-issues via `CreateDeferredIssuesActivity` → each sub-issue gets picked up in later iterations (Stories 2-14..2-16).
- **CI fails flakily on PR**: `CiWithDebugRetryWorkflow` polls check runs, on failure launches `DebuggingWorkflow` which gathers error + reproduction context → LLM proposes fix → commit → re-poll; max retries enforced.

## Dependencies

**Upstream:**
- [Epic 1](Epic-1-Foundation.md) — `IGitPlatform`, `IAgentProvider`, `TammaEngine`, CLI.
- [Epic 1.5](Epic-1.5-Infrastructure.md) — runtime modes (engine / service / SaaS) that host the loop.

**Downstream:**
- [Epic 3](Epic-3-Quality-Gates.md) — wraps loop steps with build/test/security gates.
- [Epic 4](Epic-4-Event-Sourcing.md) — every activity emits events.
- [Epic 5](Epic-5-Observability.md) — dashboards visualize loop state.
- [Epic 9](Epic-9-Agent-Management.md) — multi-agent extension of single-agent loop.
- [Epic 13](Epic-13-Workflow-Decomposition.md) — further decomposition of large workflows.

## Current state

**Landed:**

- **Core ADL (2-1..2-11)** — all 11 stories done; running in production on Hetzner. `AdlOrchestratorWorkflow` + `SingleIssueCycleWorkflow` executing continuously.
- **Advanced intelligence (2-12, 2-13)** — intelligent provider selection and prompt optimization done.
- 33 Elsa workflow files in `Tamma.ElsaServer/Workflows/`, backed by ~150 activity classes in `Tamma.Activities/`.
- Priority-based selection (2-20) — `SelectWorkItemActivity` has 3 outcomes (Selected / NothingFound / NeedsTriage) and ranks across issues, security alerts (Dependabot + CodeQL), failed CI on main, stale PRs.
- `TammaEngine` TypeScript mirror for CLI mode — same pipeline, in-process.
- **Issue Decomposition (2-14)** — landed 2026-07-05 as `IssueDecompositionWorkflow` (post-pivot architecture). Gathers codebase/prior-art context by reusing `DispatchWorkflow("context-gathering")`, then decomposes the issue via the mediated `llm-call` path (role `senior_developer` / action `decompose-issue` — the engine holds no LLM credential) into an ORDERED set of sub-tasks, each with rationale, definition-of-done, sizing, complexity, and declared prerequisite dependencies. Parsing is fail-closed (empty/unparseable/no-subtasks → `DECOMPOSITION.FAILED` + error terminal, never a fabricated breakdown). Emits `DECOMPOSITION.STARTED / CONTEXT_GATHERED / COMPLETED / FAILED` DCB events. Decomposition is autonomous — the AC7 "human approval before executing sub-tasks" is a downstream orchestration concern of the parent flow.

**Ready for dev:**

- 2-15 Task Dependency Mapping, 2-16 Incremental Task Sequencing — context XMLs exist; 2-14's workflow declares per-sub-task prerequisite dependencies, but full graph-based dependency resolution / incremental execution is not implemented.

**Drafted (workflow optimization wave 2):**

- 2-17 Context Gathering Redesign, 2-18 Plan Generation Redesign, 2-19 Plan Review Redesign — story briefs exist; current `ContextGatheringWorkflow`, `PlanGenerationWorkflow`, `PlanReviewWorkflow` are the v1 implementations due for overhaul.
- 2-20 Priority-Based Work Item Selection — brief written; implementation landed ahead of full brief completion.

**Drift from briefs:**

- The original loop was described as "14 steps"; the current implementation is structured as 2 outer activities + 1 inner flowchart with ~18 distinct activities, but the naming of "14-step loop" is retained for stakeholder comms.
- Stories 2-1..2-11 are TS-first in the briefs; actual execution is primarily in C# Elsa workflows. The TS `TammaEngine` remains as a lightweight mirror for the CLI self-hosted mode.
- Story 2-11 "Auto-Next Issue Selection" is now part of `AdlOrchestratorWorkflow`'s loop semantics rather than a separate activity.
- `SelectWorkItemActivity` (Story 2-20) replaced the older `SelectIssueActivity` from Story 2-1 — the brief still names the old activity; the new wiki page uses the current name.

## See also

- **Docs:** [docs/stories/epic-2/](/stories/epic-2/) — all 20 story briefs + task plans + context XML.
- **Tech spec:** [tech-spec-epic-2.md](/stories/epic-2//tech-spec-epic-2.md).
- **Related wiki pages:**
  - [Workflow: ADL Orchestrator](Workflow-ADL-Orchestrator) — outer-loop flow.
  - [Workflow: Single Issue Cycle](Workflow-Single-Issue-Cycle) — inner-cycle flow.
  - [Workflow: Plan Generation](Workflow-Plan-Generation), [Plan Review](Workflow-Plan-Review), [Task Creation](Workflow-Task-Creation), [TDD Cycle](Workflow-TDD-Cycle), [TDD with Debug Retry](Workflow-TDD-With-Debug-Retry), [Pull Request](Workflow-Pull-Request), [CI with Debug Retry](Workflow-CI-With-Debug-Retry), [Code Review](Workflow-Code-Review), [Review Fix](Workflow-Review-Fix), [Merge Approval](Workflow-Merge-Approval), [Merge](Workflow-Merge).
  - [Architecture](/architecture/) — how the loop fits into the overall system.
- **Code paths:**
  - `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/` — 33 Elsa workflow definitions.
  - `apps/tamma-elsa/src/Tamma.Activities/ADL/` — top-level ADL activities.
  - `apps/tamma-elsa/src/Tamma.Activities/TDD/` — TDD-cycle activities.
  - `packages/orchestrator/src/engine.ts` — TypeScript engine mirror.
