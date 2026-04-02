---
title: "Epic 10: Engine Core -- Workflow-Driven Architecture"
sidebar:
  order: 10
---

**Status:** Drafted (engine exists as imperative state machine; stories define target agentic architecture)
**Stories:** 8 (10-1 through 10-8)
**Packages:** `@tamma/orchestrator`, `apps/tamma-elsa/`

## Overview

Epic 10 refactors the Tamma Engine from a hardcoded imperative state machine into a workflow-driven orchestration service. The engine acts as an intelligent brain (with its own static workflow) that routes work to a replaceable workflow provider (ELSA), with the event store as the single source of truth for all system state.

## Architecture

```
CLI / Web / Mobile / Desktop / GitHub / Gitea / GitLab
                        |
                   NORMALIZE TO EVENT
                        |
                        v
+----------------------------------------------------------+
|  ENGINE BRAIN (Static Workflow -- Story 10.1)             |
|                                                           |
|  Intake -> Load State -> LLM Decision -> Route -> Record  |
|  - Answer directly (from event store)                     |
|  - Trigger workflow (via Smart Queue -> ELSA)             |
|  - Signal workflow (via Smart Queue -> ELSA)              |
|  - Reject (duplicate/invalid)                             |
+-----------------------------------------------------------+
|  SMART QUEUE (Story 10.4)                                 |
|  Re-validates intents against event store before dispatch  |
+-----------------------------------------------------------+
|  EVENT STORE (Stories 10.2, 10.3, 10.7, 10.8)            |
|  PostgreSQL/Emmett -- single source of truth               |
|  Raw + sanitized content -- security at every layer        |
|  State reconstructed via projections                       |
+-----------------------------------------------------------+
|  WORKFLOW PROVIDER (Story 10.5)                           |
|  IWorkflowProvider -> ElsaWorkflowProvider (replaceable)   |
+-----------------------------------------------------------+
```

## Key Design Decisions

- **ELSA is a replaceable provider** -- zero coupling, swappable for Temporal/Conductor/other workflow engines
- **Event store is the single source of truth** -- state derived from events, not memory; survives restarts
- **Engine functions when workflow provider is down** -- can answer queries and queue intents
- **All inputs normalized to events** -- user commands, webhooks, and platform events go through one brain

## Implementation

### TypeScript Engine (`packages/orchestrator/`)

| File | Purpose |
|------|---------|
| `engine.ts` | Main engine with workflow phase routing |
| `workflow-engine.ts` | Workflow execution engine |
| `elsa-client.ts` | HTTP bridge to ELSA workflow server |
| `saas-coordinator.ts` | SaaS multi-installation coordinator |
| `transports/in-process.ts` | In-process transport for CLI mode |
| `transports/remote.ts` | HTTP transport for server mode |

### ELSA Workflow Engine (`apps/tamma-elsa/`)

20+ code-first C# workflows registered at startup, visible and editable in ELSA Studio. The main workflows:

- **AdlOrchestratorWorkflow** -- Top-level orchestrator that manages the autonomous development loop
- **SingleIssueCycleWorkflow** -- Complete issue lifecycle from selection through merge
- **LlmCallWorkflow** -- Provider chain with budget check, circuit breaker, diagnostics recording

## Stories

| Story | Title | Priority | Status |
|-------|-------|----------|--------|
| 10-1 | Engine Static Workflow & Brain | P0 | Drafted |
| 10-2 | Comprehensive Event Catalog & Typed Schema | P0 | Drafted |
| 10-3 | Event Store -- PostgreSQL/Emmett Implementation | P0 | Drafted |
| 10-4 | Smart Queue with State-Based Deduplication | P0 | Drafted |
| 10-5 | Workflow Provider Abstraction & ELSA Integration | P0 | Drafted |
| 10-6 | Input Channel Unification (UI + Platform Events) | P1 | Drafted |
| 10-7 | Event Store Security & Sanitization Pipeline | P0 | Drafted |
| 10-8 | State Reconstruction from Event Stream | P0 | Drafted |

## Performance Targets

- Engine responds to queries in <200ms without workflow provider
- Workflow dispatch via Smart Queue adds <100ms overhead
- Event store handles 50 writes/second sustained
- State reconstruction: <50ms for 500 events
- Zero data loss: every action has a corresponding event
- Raw LLM content never served to UI without sanitization

---

_For story details, see [docs/stories/epic-10/](/stories/epic-10/) in the repository._
