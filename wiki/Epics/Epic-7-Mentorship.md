# Epic 7: Autonomous Mentorship Workflow

**Status:** Near complete. 18 of 19 stories landed (7-1 core + 7-1A..7-1G, 7-1I sub-workflows done; 7-1H TDD sub-workflow in progress; 7-11/7-12 follow-up refinements landed).
**Stories:** 19 (7-1..7-10 + 7-1A..7-1I sub-workflows + 7-11/7-12 prompt refinement).
**Primary code:** `apps/tamma-elsa/` (C# / .NET 8 ELSA workflows + activities), `packages/orchestrator/` (TypeScript bridge).

## Overview

Epic 7 is the end-to-end autonomous mentorship pipeline. When a developer (human or agent) is assigned a story, the system walks the work from "I don't fully understand this ticket" through implementation, quality gates, review, and merge — adapting tone, guidance depth, and escalation thresholds to the developer's measured skill level. It is the workflow that makes Tamma more than a code generator; the platform observes progress, diagnoses when work stalls, and intervenes with targeted guidance rather than raw code.

Under the hood, mentorship is a 28-state machine expressed as a code-first ELSA 3 flowchart (`MentorshipWorkflow.cs`) composed from eight sub-workflows: LLM call, context gathering, assessment, code review, testing, blocker diagnosis, TDD, and debugging. Every decision, LLM call, and external side-effect is an ELSA activity, so the whole session is pausable, resumable, visible in Studio, and audit-logged. The TypeScript `@tamma/orchestrator` package exposes the session lifecycle to CLI / API callers through a thin HTTP client (`elsa-client.ts`) so it can be driven from the engine without duplicating workflow logic in TypeScript.

Mentorship is a consumer of nearly every other pillar: provider chains (Epic 9), content sanitization (Epic 11), the agentic tool loop (Epic 12), and the event store (Epic 10). It's also the source of most of the workflow decomposition pressure that drove Epic 13.

## Architecture

```
+---------------------------------------------------------------+
|           Mentorship Session (correlationId)                   |
+---------------------------------------------------------------+
|                                                                |
|   TS Engine / CLI / API                                        |
|        |   POST /sessions   GET /sessions/:id   SSE /events    |
|        v                                                       |
|   @tamma/orchestrator  ->  elsa-client.ts  (HTTP + webhooks)   |
|                                                                |
+-----------------------+----------------------------------------+
                        |
                        v
+---------------------------------------------------------------+
|   ELSA Server (apps/tamma-elsa)  MentorshipWorkflow (28-state) |
|                                                                |
|   INIT --> ASSESS --> PLAN --> IMPLEMENT --> QUALITY --> REVIEW|
|     |        |         |          |            |        |     |
|     |        |         |          |            |        v     |
|     |        |         |          |            |     MERGE    |
|     |        |         |          |            |        |     |
|     +--------+---------+----------+-- DIAGNOSE BLOCKER --+     |
|                                     |                          |
|                          DETECT PATTERN --> ESCALATE           |
|                                                                |
+---------------------------------------------------------------+
|   Sub-workflows (all DispatchWorkflow from parent):            |
|   - LlmCallWorkflow            (7-1B — universal LLM gate)     |
|   - ContextGatheringWorkflow   (7-1F — parallel fetchers)      |
|   - AssessmentWorkflow         (7-1E — question pipeline)      |
|   - TestingWorkflow            (7-1C — run + parse tests)      |
|   - CodeReviewWorkflow         (7-1D — PR review lifecycle)    |
|   - BlockerDiagnosisWorkflow   (7-1G — signal collection)      |
|   - TddWorkflow / TddWithDebugRetry (7-1H — red/green/refactor)|
|   - DebuggingWorkflow          (7-1I — 3-mode systematic)      |
|                                                                |
|   All backed by ELSA activities under                          |
|   Tamma.Activities/{Mentorship,AI,Assessment,Blocker,Context,  |
|                     Debug,Integration,LlmCall,TDD,Testing}     |
+---------------------------------------------------------------+
          |                         |                   |
          v                         v                   v
   Provider Chain (Epic 9)   Tool Loop (Epic 12)   Event Store (Epic 10)
```

## Components

| Component | Purpose | Key Files | Status |
|-----------|---------|-----------|--------|
| MentorshipWorkflow | Main 28-state flowchart wiring every sub-workflow together | `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/MentorshipWorkflow.cs` | Done (7-1A) |
| LlmCallWorkflow | Universal LLM gate — resolve config, check budget + circuit breaker, call provider, record diagnostics | `.../LlmCallWorkflow.cs`, `Tamma.Activities/LlmCall/*` | Done (7-1B) |
| TestingWorkflow | Compile + run tests, parse results, emit structured outcome | `.../TestingWorkflow.cs` | Done (7-1C) |
| CodeReviewWorkflow | Prepare review, monitor PR comments, guide fixes, re-request review | `.../CodeReviewWorkflow.cs` | Done (7-1D) |
| AssessmentWorkflow | Generate → deliver → wait → analyze → classify skill level | `.../AssessmentWorkflow.cs`, `Tamma.Activities/Assessment/*` | Done (7-1E) |
| ContextGatheringWorkflow | Parallel-fetch story metadata, files, commits, tests, similar patterns; budget-trim | `.../ContextGatheringWorkflow.cs`, `Tamma.Activities/Context/*` | Done (7-1F) |
| BlockerDiagnosisWorkflow | Collect CI status, git activity, inactivity, communication signals; classify blocker type | `.../BlockerDiagnosisWorkflow.cs`, `Tamma.Activities/Blocker/*` | Done (7-1G) |
| DebuggingWorkflow | Three debug modes (TDD-driven, CI-driven, user-reported); hypothesis → regression test → fix | `.../DebuggingWorkflow.cs`, `Tamma.Activities/Debug/*` | Done (7-1I) |
| TddWorkflow / TddWithDebugRetryWorkflow | Red-green-refactor with automatic debug retry on failure | `.../TddWorkflow.cs`, `.../TddWithDebugRetryWorkflow.cs` | In progress (7-1H) |
| Mentorship activities | Assess, monitor, diagnose, guide, quality-gate, review, merge | `Tamma.Activities/Mentorship/*.cs` | Done (7-1..7-9) |
| AI activities | Claude analysis, context gathering, suggestion generation | `Tamma.Activities/AI/*.cs` | Done (7-4) |
| Integration activities | GitHub + Slack helpers | `Tamma.Activities/Integration/*.cs` | Done |
| TS bridge | Start / signal / poll ELSA workflows from TypeScript engine | `packages/orchestrator/src/elsa-client.ts`, `engine.ts` | Done (7-10) |

## Class / type structure (primary types)

```
Tamma.Core.Enums
  MentorshipState (28 values: INIT_STORY_PROCESSING .. COMPLETED + PAUSED/CANCELLED/FAILED/TIMEOUT)
  BlockerType, SkillLevel, ReviewOutcome, QualityGateResult

Tamma.ElsaServer.Workflows
  WorkflowBase (abstract)       — shared helpers: DispatchWorkflow, SetOutput, ObserveEvent
    MentorshipWorkflow          — 28-state Flowchart
    LlmCallWorkflow             — provider chain + budget + circuit breaker
    ContextGatheringWorkflow    — parallel fetch + budget
    AssessmentWorkflow          — question → analyze
    TestingWorkflow             — compile + run + parse
    CodeReviewWorkflow          — PR lifecycle
    BlockerDiagnosisWorkflow    — signal collection + classify
    TddWorkflow                 — red/green/refactor
    TddWithDebugRetryWorkflow   — wraps TddWorkflow + DebuggingWorkflow
    DebuggingWorkflow           — 3-mode systematic debugging

Tamma.Activities.Core
  TammaActivity (CodeActivity)  — base class emitting STARTED/COMPLETED/FAILED events
  TammaAsyncActivity            — async variant
  TammaOutcomeActivity          — activities with multiple named outcomes
  ITammaActivity                — common interface

Tamma.Activities.Mentorship
  AssessJuniorCapabilityActivity : TammaActivity
  MonitorImplementationActivity  : TammaActivity
  DiagnoseBlockerActivity        : TammaActivity
  ProvideGuidanceActivity        : TammaActivity
  QualityGateCheckActivity       : TammaActivity
  CodeReviewActivity             : TammaActivity
  MergeCompleteActivity          : TammaActivity

Tamma.Activities.AI
  ClaudeAnalysisActivity         : TammaAsyncActivity
  ContextGatheringActivity       : TammaAsyncActivity
  SuggestionGeneratorActivity    : TammaAsyncActivity

@tamma/orchestrator
  class ElsaClient
    startWorkflow(defId, input): Promise<InstanceId>
    signalWorkflow(instanceId, signal, payload): Promise<void>
    getStatus(instanceId): Promise<WorkflowStatus>
    streamEvents(instanceId): AsyncIterable<WorkflowEvent>
  class TammaEngine (uses ElsaClient for mentorship sessions)
```

## Sequence — happy path mentorship session

```
Developer (or engine)     @tamma/orchestrator      ELSA Server             MentorshipWorkflow          Sub-workflows
         |                        |                     |                          |                         |
         | start story #42 -----> |                     |                          |                         |
         |                        | POST /workflows/run |                          |                         |
         |                        | ------------------> |                          |                         |
         |                        |                     | create instance -------> | INIT_STORY_PROCESSING   |
         |                        |                     |                          | VALIDATE_STORY          |
         |                        |                     |                          |                         |
         |                        |                     |                          | ASSESS_JUNIOR_CAPABILITY|
         |                        |                     |                          | --- dispatch ----------> AssessmentWorkflow
         |                        |                     |                          |                         |  Generate → deliver
         |                        |                     |                          |                         |  (bookmark: wait for response)
         |                        |                     |                          | <--- result (skillLvl) -- Analyze → classify
         |                        |                     |                          |                         |
         |                        |                     |                          | PLAN_DECOMPOSITION      |
         |                        |                     |                          | --- dispatch ----------> LlmCallWorkflow (architect role)
         |                        |                     |                          | <--- plan --------------
         |                        |                     |                          |                         |
         |                        |                     |                          | START_IMPLEMENTATION    |
         |                        |                     |                          | --- dispatch ----------> ContextGatheringWorkflow
         |                        |                     |                          | <--- context -----------
         |                        |                     |                          |                         |
         |                        |                     |                          | MONITOR_PROGRESS (loop) |
         |                        |                     |                          |   PROVIDE_GUIDANCE      |
         |                        |                     |                          |   DETECT_PATTERN? --->  |  pattern → blocker flow
         |                        |                     |                          |                         |
         |                        |                     |                          | QUALITY_GATE_CHECK      |
         |                        |                     |                          | --- dispatch ----------> TestingWorkflow
         |                        |                     |                          | <--- pass/fail ---------
         |                        |                     |                          |   fail → AUTO_FIX_ISSUES|
         |                        |                     |                          |                         |
         |                        |                     |                          | PREPARE_CODE_REVIEW     |
         |                        |                     |                          | --- dispatch ----------> CodeReviewWorkflow
         |                        |                     |                          | <--- approved ----------
         |                        |                     |                          |                         |
         |                        |                     |                          | MERGE_AND_COMPLETE      |
         |                        |                     |                          | GENERATE_REPORT         |
         |                        |                     |                          | UPDATE_SKILL_PROFILE    |
         |                        |                     |                          | COMPLETED               |
         | <------ SSE event ---- |  webhook / SSE <--- | instance complete        |                         |
```

## Use cases

- **New junior picks up a story** — developer is assessed, given a decomposed plan sized to their skill level, and monitored through implementation. Pattern detection catches circular work (same files edited in a loop with no net progress) and escalates early rather than burning cycles.
- **Blocker on an otherwise-healthy PR** — implementation stalls. `BlockerDiagnosisWorkflow` collects CI status + git activity + inactivity signals, classifies the blocker (dependency vs. ambiguity vs. missing context), and routes to either targeted guidance, context re-gathering, or senior escalation.
- **TDD cycle for a brittle feature** — `TddWithDebugRetryWorkflow` runs red-green-refactor; on red (test failure after implementation) it dispatches the debugging workflow, which walks systematic hypothesis generation and produces a regression test before re-attempting. Epic 13 extracted this loop so it is reusable outside mentorship.
- **Autonomous agent operating in mentorship mode** — the same state machine drives a pure-agent session (skill level = senior by default), giving the agent the same monitoring, quality-gate, and review plumbing the human path gets. The mentorship session becomes the audit trail for agent work.
- **Re-entering a paused session** — operator pauses the instance; ELSA persists bookmark state. On resume, the workflow continues from the exact activity it was waiting on (e.g. `WAIT_FOR_RESPONSE` in assessment).

## Dependencies

**Upstream (needed before / by this epic)**
- Epic 1 — provider interfaces (`IAgentProvider`, `IAIProvider`) the LLM activities call through.
- Epic 6 — context & knowledge base used by `ContextGatheringWorkflow` for similar-pattern lookup.
- Epic 9 — role-based agent resolver, provider chain, content sanitizer, diagnostics queue, circuit breaker.
- Epic 11 — `ContentSanitizer` C# port gates every LLM input and output in `LlmCallWorkflow`.
- Epic 12 — agentic tool loop in `CallLlmInlineActivity` gives mentorship activities real tool use.

**Downstream (consumers / extenders)**
- Epic 13 — extracts mentorship's internal TDD + CI retry loops into reusable sub-workflows.
- Epic 14 — ELSA Studio visualizes and debugs running mentorship sessions (custom UI hints).
- Epic 2 — main autonomous loop invokes mentorship sessions as the primary execution surface.
- Epic 19 / Agent Dispatch — dispatcher starts mentorship workflows per assigned issue.

## Current state

Landed:
- `ac05aa0f docs: Epic 7 stories 7-1..7-10 + 7-1A..7-1I sub-workflows`
- `b10e90f docs: add implementation plans for all 15 stories (epics 11-14)` (sibling landings)
- 9ac2fcb / 44d7ef7 — TDD / CI retry extractions (Epic 13) that simplified the parent workflow.
- Sub-workflows 7-1A, 7-1B, 7-1C, 7-1D, 7-1E, 7-1F, 7-1G, 7-1I merged via `bb61173`, `e07a263`, and predecessors.
- Story 7-11 (blocker diagnosis improvements) and 7-12 (debugging workflow prompt improvements) landed as follow-ups.

Still outstanding:
- Story 7-1H (TDD sub-workflow) — TDD prompt overhaul impl plan written, implementation in flight.
- A subset of activities still use simulated logic (flagged in `docs/stories/epic-7/README.md` §"Existing Implementation"); real-API conversion is ongoing.

Stubs / deferrals:
- `AssessJuniorCapabilityActivity` still mixes simulated and real signals; full Claude-backed version pending.
- Skill-level profile persistence presently in-memory; backed by the event store once Epic 10 state reconstruction lands.

## See also

- [Workflow: Mentorship](../Workflow-Mentorship.md) — walk-through of the 28-state machine.
- [Workflow: ADL Orchestrator](../Workflow-ADL-Orchestrator.md) — parent loop that starts mentorship sessions.
- [Workflow: Single Issue Cycle](../Workflow-Single-Issue-Cycle.md) — issue lifecycle workflow.
- [Epic 9: Agent Management](Epic-9-Agent-Management.md) — provider chain + prompt resolution consumed here.
- [Epic 11: Security](Epic-11-Security.md) — sanitization gates in `LlmCallWorkflow`.
- [Epic 12: Tool Loop](Epic-12-Tool-Loop.md) — agentic loop inside LLM activities.
- [Epic 13: Workflow Decomposition](Epic-13-Workflow-Decomposition.md) — extraction of TDD / CI retry loops.
- Impl plans: [`docs/stories/epic-7/`](https://github.com/meywd/tamma/tree/main/docs/stories/epic-7).
- Source: `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/`, `apps/tamma-elsa/src/Tamma.Activities/`, `packages/orchestrator/src/elsa-client.ts`.

---

_Last refreshed 2026-04-22._
