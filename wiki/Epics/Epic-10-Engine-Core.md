# Epic 10: Engine Core — Workflow-Driven Architecture

**Status:** Drafted + partially implemented. Stories 10-1..10-8 are design docs driving an in-flight refactor; Story 10-9 (TammaActivity base class + standardized workflow event emission) is in progress in ELSA.
**Stories:** 9 (10-1..10-9).
**Primary code:** `packages/orchestrator/`, `apps/tamma-elsa/src/Tamma.Activities/Core/`, `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/`.

## Overview

Epic 10 turns the Tamma engine from a hard-coded imperative state machine into a workflow-driven orchestration service. The engine becomes an intelligent *brain* running a static agentic tool-calling loop — an LLM configured under the `orchestrator` role receives tools (`query_state`, `trigger_workflow`, `signal_workflow`, `query_events`, `answer_user`, `queue_intent`) and decides on each turn what the system should do. There is no hand-rolled router, no fast-path decision tree, and no if/else state machine any more; the LLM is the router, same pattern every major AI coding tool (Claude Code, OpenCode, Codex CLI, Cline, Aider, Copilot) uses.

Behind the brain sit three hard commitments that make the rest of the platform tractable. First, the **event store is the single source of truth** — every action, every LLM call, every security check, every workflow step is an immutable typed event; state is reconstructed via projections, not memory, so the engine survives restarts and supports time-travel debugging. Second, **the workflow provider is replaceable** — `IWorkflowProvider` isolates ELSA behind an interface so it can be swapped for Temporal / Conductor / a future successor without engine code changes. Third, **the engine stays useful when the workflow provider is down** — queries can be answered directly from projections, intents can be queued, and the LLM can observe the degraded state and tell the user.

All inputs — CLI, web, mobile, desktop, GitHub/Gitea/GitLab webhooks — normalize into a shared event shape before the brain sees them. A smart queue re-validates intents against the event store before dispatch, killing duplicates and stale signals.

## Architecture

```
CLI  Web  Mobile  Desktop  GitHub  Gitea  GitLab
  \   |    |       |        |       |      /
   \__|____|_______|________|_______|_____/
                       |
                   NORMALIZE
                       |
                       v
+-------------------------------------------------------------+
|  ENGINE BRAIN  (Story 10-1 — static agentic tool loop)      |
|                                                             |
|  while LLM_response has tool_calls:                         |
|    for each tool_call: execute -> record event              |
|    feed results back                                        |
|  -> final text response -> answer_user                      |
|                                                             |
|  Tools: query_state | query_events | trigger_workflow       |
|         signal_workflow | answer_user | queue_intent        |
+-------------------------------------------------------------+
                       |
                       v
+-------------------------------------------------------------+
|  SMART QUEUE  (Story 10-4)                                  |
|  re-validates intent against event store before dispatch    |
|  deduplication by state-based fingerprint                   |
+-------------------------------------------------------------+
                       |                                ^
                       v                                |
+------------------------------------------+   +---------------------+
|  WORKFLOW PROVIDER (Story 10-5)          |   |  State Projections  |
|  IWorkflowProvider -> ElsaProvider       |   |  (Story 10-8)       |
|  (swappable: Temporal/Conductor/other)   |   |  - project state    |
+------------------------------------------+   |  - cost state       |
                       |                        |  - queue state      |
                       v                        |  - workflow state   |
+------------------------------------------+   +---------------------+
|  EVENT STORE  (Stories 10-2, 10-3, 10-7) |                 ^
|  PostgreSQL / Emmett - single source     |                 |
|  Raw + sanitized pair for LLM content    |-----------------+
|  Security events first-class             |
|  Typed discriminated union, schemaVersion|
+------------------------------------------+
```

## Components

| Component | Purpose | Key files | Status |
|-----------|---------|-----------|--------|
| Engine static workflow | LLM-driven agentic tool loop — the brain | `packages/orchestrator/src/engine.ts` (refactor in flight) | 10-1 / Drafted |
| Event catalog | Typed discriminated union of every event type, validators, schemaVersion | `packages/shared/src/events/` (planned), `Tamma.Core/Events/` | 10-2 / Drafted |
| Event store (Postgres/Emmett) | Append-only stream with JSONB tags, indices for DCB queries | `packages/events/` + Postgres schema | 10-3 / Drafted |
| Smart queue | State-based dedup, pre-dispatch revalidation | `packages/orchestrator/src/smart-queue.ts` (planned) | 10-4 / Drafted |
| `IWorkflowProvider` abstraction | Interface + ELSA implementation | `packages/orchestrator/src/workflow-engine.ts`, `elsa-client.ts` | 10-5 / Drafted (ELSA impl exists) |
| Input channel unification | Webhook receivers + normalizer | `packages/api/src/webhooks/` (planned) | 10-6 / Drafted |
| Event store security pipeline | Sanitization at write; raw + sanitized pair | `packages/events/src/sanitization-pipeline.ts` | 10-7 / Drafted |
| State reconstruction | Projections rebuilt from events | `packages/events/src/projections/` | 10-8 / Drafted |
| `TammaActivity` base classes | Sync / async / outcome activities that emit STARTED / COMPLETED / FAILED events | `apps/tamma-elsa/src/Tamma.Activities/Core/TammaActivity.cs` | 10-9 / In progress |
| Existing ELSA workflows | 20+ code-first C# workflows registered at startup | `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/*.cs` | Done (pre-existing) |
| Existing engine | Imperative state machine being replaced | `packages/orchestrator/src/engine.ts` | Live; to be replaced |

## Class / type structure

```
packages/shared/src/events/ (planned)
  interface BaseEvent {
    eventId        : string        // UUID v7
    timestamp      : string        // ISO 8601 ms
    eventType      : EventType     // discriminant
    schemaVersion  : string        // "1.0.0"
    actor          : EventActor    // user|system|ai|workflow|platform|engine
    metadata       : EventMetadata // correlationId, causationId, workflowId, ...
  }
  type TammaEvent =
    | IntakeEvent           // user input, webhook received
    | DecisionEvent         // LLM tool call / final response
    | QueueEvent            // enqueue / dispatch / drop
    | WorkflowEvent         // started / completed / failed / signaled
    | LlmEvent              // request / sanitized / dispatched / response / completed
    | SecurityEvent         // sanitization / PII / injection / action-block / URL-block
    | PlatformEvent         // issue / PR / branch / CI / webhook
    | StateEvent            // snapshot / projection update
  fn validateEvent(e: TammaEvent): void

packages/orchestrator/src/
  class TammaEngine                   — entry point (replaces imperative loop)
    runBrainLoop(correlationId, input): AsyncIterable<BrainTurn>
    getTool(name): ToolExecutor
  interface IWorkflowProvider
    triggerWorkflow(name, input) : Promise<{instanceId}>
    signalWorkflow(id, signal, payload?) : Promise<void>
    isHealthy() : Promise<boolean>
  class ElsaWorkflowProvider : IWorkflowProvider  (wraps ElsaClient)
  class SmartQueue
    enqueue(intent: Intent): Promise<IntentRef>
    drain(): AsyncIterable<Dispatch>

packages/events/src/
  interface IEventStore
    append(event: TammaEvent): Promise<void>
    query(filter: EventFilter): AsyncIterable<TammaEvent>
    readAll(correlationId): Promise<TammaEvent[]>
  class EmmettEventStore : IEventStore
  interface IProjection<S>
    apply(state: S, event: TammaEvent): S
    initialState: S
  class ProjectionRunner
    materialize<S>(projection: IProjection<S>, filter?): Promise<S>

apps/tamma-elsa/src/Tamma.Activities/Core/
  interface ITammaActivity
  abstract class TammaActivity : CodeActivity, ITammaActivity
    protected abstract void Run(ActivityExecutionContext ctx);
    // Emits {EventType}.STARTED / .COMPLETED / .FAILED
  abstract class TammaAsyncActivity : CodeActivity, ITammaActivity
    protected abstract Task RunAsync(ActivityExecutionContext ctx);
  abstract class TammaOutcomeActivity : CodeActivity, ITammaActivity
    // Activities with multiple named outcomes
```

## Sequence — "start working on issue #42"

```
User        Engine Brain       LLM (orchestrator)     Tool exec       Event Store      Workflow Provider
  |              |                     |                   |                |                   |
  | "start #42" >|                     |                   |                |                   |
  |              | normalize -> IntakeEvent ---------------|--------------> |                   |
  |              | load state via projections                               |                   |
  |              | <--- state snapshot --------------------------------------|                  |
  |              | system prompt + tools + state --> |                      |                   |
  |              | <-- tool_call: query_events(filter=issueId:42) --|       |                   |
  |              | execute tool --> |                                       |                   |
  |              |                   | query events            ------------>|                   |
  |              |                   | <--- events[] ---------------------- |                   |
  |              | record ToolExecutedEvent -------------------------------->|                  |
  |              | feed result back to LLM                                                       |
  |              | <-- tool_call: trigger_workflow('single-issue-cycle', {issueId:42}) --       |
  |              | smart queue: dedup against event store                                        |
  |              | enqueue -> dispatch                                                           |
  |              | provider.triggerWorkflow(...) ------------------------------------------>     |
  |              | <--- { instanceId: 'wf-7b2...' } ---------------------------------------      |
  |              | record WorkflowStartedEvent --------------> |             |                   |
  |              | feed result back to LLM                                                       |
  |              | <-- final text: "Started cycle wf-7b2... for #42"                             |
  |              | record DecisionCompletedEvent ------------->|             |                   |
  | <-- answer_user -|                                                                           |
  |                                                                                              |
  | (hours later)                                                                                |
  | "status?"   >|                                                                               |
  |              | loop again: LLM decides to call query_state, answers from projections only   |
  | <-- "cycle wf-7b2... is in CODE_GENERATION"                                                   |
```

## Sequence — workflow provider down

```
User       Engine Brain       LLM                 Provider       Queue          Event Store
 |              |              |                      |             |                |
 | "run CI" --->| ...          |                      |             |                |
 |              | tool: trigger_workflow ----------->(provider down)|                |
 |              | <-- error: CONNECTION_REFUSED -------|            |                |
 |              | record WorkflowDispatchFailedEvent ----------------------------> |
 |              | LLM sees tool error in next turn                 |                 |
 |              | <-- tool_call: queue_intent({type:'trigger', workflow, input}) -> |
 |              | queue append                                    +--intentId-->    |
 |              | record IntentQueuedEvent  --------------------------------------->|
 |              | <-- final text: "Workflow engine is down; I've queued this intent; it will run when the engine comes back." |
 | <-- answer_user                                                                  |
 |                                                                                   |
 | (later, engine recovers)                                                          |
 |              | smart queue drain -> provider.triggerWorkflow ---> ok ----------->|
 |              | record WorkflowStartedEvent                                         |
```

## Use cases

- **User asks a state question** — "how much did I spend on provider X this week?" The brain calls `query_state('cost', { provider: 'X', window: '7d' })`, reads from the cost projection, and answers directly without touching a workflow. < 200 ms end-to-end target.
- **Webhook from GitHub fires a workflow** — platform event normalized into `IntakeEvent`, brain decides to `trigger_workflow('issue-triaged-cycle', ...)`. The smart queue sees a duplicate intent within the dedup window (same issue, same state fingerprint) and drops it.
- **LLM decides to chain multiple actions** — on "ship the auth story end-to-end", the brain calls `query_state`, then `query_events`, then `trigger_workflow`, then `answer_user` in one session. Every tool call is an event; the whole conversation is replayable from `correlationId`.
- **Time-travel debugging** — operator wants to know why a workflow kicked off at 03:14. Query events by `correlationId`; replay in the projection runner; inspect exactly what state the brain saw and which tool call it made.
- **Swapping ELSA for Temporal** — implement `ITemporalWorkflowProvider : IWorkflowProvider`, register it, flip a config flag. No engine code changes; C# activities repackaged as Temporal activities.

## Dependencies

**Upstream**
- Epic 1 — provider interfaces used by the orchestrator role.
- Epic 4 — earlier event sourcing stories 4-2..4-8 are superseded by 10-2/10-3/10-7/10-8.
- Epic 6 — knowledge base as a richer tool the brain can call (`search_code_semantic`, etc.).
- Epic 2 — the current imperative `engine.ts` is the thing being replaced.

**Downstream**
- Epic 7 — mentorship workflow runs under the new `IWorkflowProvider`.
- Epic 11 — security pipeline integrates with event store security events.
- Epic 12 — the tool loop inside `CallLlmInlineActivity` is the analog pattern inside workflow activities.
- Epic 13 — workflow decomposition depends on the `IWorkflowProvider` abstraction.

**External**
- Emmett (event sourcing library).
- PostgreSQL 17.
- ELSA Workflows 3.5.x (first `IWorkflowProvider` implementation).

## Current state

Landed:
- `34155be5 docs(epic-10): rewrite Story 10.1 — orchestrator tool loop, no fast paths`
- `fe9bd962 docs(epic-10): add Engine Core workflow-driven architecture epic`
- Story 10-9 in progress — `TammaActivity` base classes exist under `Tamma.Activities/Core/`; all activities in `Tamma.Activities/` are migrating to inherit from them so every activity run emits start + end events.

Architectural pieces already live on `main`:
- `apps/tamma-elsa` is the first `IWorkflowProvider` implementation and drives production workloads today.
- `packages/orchestrator/src/engine.ts` owns the current imperative loop; it will be lifted into the static workflow once Stories 10-1..10-4 land.
- Event-store prototype exists in `packages/events/` but the typed catalog (10-2) + Emmett-backed store (10-3) are the blockers.

Stubs / deferrals:
- Cross-platform webhook normalizer (10-6) — GitHub path works; GitLab / Gitea / Bitbucket normalizers are scoped but not written.
- Distributed smart-queue (10-4) — Redis-backed version deferred; in-process dedup is the first-pass target.
- Projection replay performance target (<50 ms for 500 events) measured on local Postgres; benchmarks on managed Postgres are a follow-up.

Performance targets (from tech spec):
- Engine query response < 200 ms without workflow provider.
- Workflow dispatch via smart queue adds < 100 ms overhead.
- Event store sustains 50 writes/s.
- State reconstruction < 50 ms for 500 events.

## See also

- [Architecture](../Architecture.md) — overall technical architecture.
- [Workflow: ADL Orchestrator](../Workflow-ADL-Orchestrator.md) — current imperative loop this epic replaces.
- [Epic 4: Event Sourcing](Epic-4-Event-Sourcing.md) — earlier event-store work being superseded.
- [Epic 7: Mentorship](Epic-7-Mentorship.md) — primary `IWorkflowProvider` consumer.
- [Epic 11: Security](Epic-11-Security.md) — sanitization pipeline integrated into event writes.
- [Epic 12: Tool Loop](Epic-12-Tool-Loop.md) — analog pattern inside LLM activities.
- Tech spec: `docs/stories/epic-10/tech-spec-epic-10.md`.
- Impl plans: [`docs/stories/epic-10/`](https://github.com/meywd/tamma/tree/main/docs/stories/epic-10).
- Source: `packages/orchestrator/src/`, `packages/events/src/`, `apps/tamma-elsa/src/Tamma.Activities/Core/`.

---

_Last refreshed 2026-04-22._
