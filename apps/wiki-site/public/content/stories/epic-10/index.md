---
title: "Epic 10: Engine Core — Workflow-Driven Architecture"
---

## Overview

**Goal**: Refactor the Tamma Engine from a hardcoded imperative state machine into a workflow-driven orchestration service where the engine is an intelligent brain (with its own static workflow) that routes work to a replaceable workflow provider (Elsa), with the event store as the single source of truth for all system state.

**Value Delivered**:
- Engine is a standalone service that CLI, web, mobile, and desktop clients connect to
- All inputs (user commands, GitHub/Gitea/GitLab webhooks) processed through one brain
- Elsa is a replaceable provider — zero coupling, swappable for Temporal/Conductor/other
- Event store records everything: raw + sanitized content, security actions, workflow progress
- State derived from events, not memory — survives restarts, enables time-travel debugging
- Engine functions when workflow provider is down (answers queries, queues intents)

## Stories

| Story | Title | Priority | Dependencies | Status |
|-------|-------|----------|-------------|--------|
| 10.1 | Engine Static Workflow & Brain | P0 | Epic 1 (LLM providers) | Planned |
| 10.2 | Comprehensive Event Catalog & Typed Schema | P0 | None | Planned |
| 10.3 | Event Store — PostgreSQL/Emmett Implementation | P0 | Story 10.2 | Planned |
| 10.4 | Smart Queue with State-Based Deduplication | P0 | Stories 10.1, 10.3 | Planned |
| 10.5 | Workflow Provider Abstraction & Elsa Integration | P0 | Story 10.3 | Planned |
| 10.6 | Input Channel Unification (UI + Platform Events) | P1 | Story 10.1 | Planned |
| 10.7 | Event Store Security & Sanitization Pipeline | P0 | Stories 10.2, 10.3 | Planned |
| 10.8 | State Reconstruction from Event Stream | P0 | Stories 10.2, 10.3 | Planned |

## Architecture

```
CLI / Web / Mobile / Desktop / GitHub / Gitea / GitLab
                        │
                   NORMALIZE TO EVENT
                        │
                        ▼
┌──────────────────────────────────────────────────────────┐
│  ENGINE BRAIN (Static Workflow — Story 10.1)              │
│                                                           │
│  Intake → Load State → LLM Decision → Route → Record     │
│  ├─ Answer directly (from event store)                    │
│  ├─ Trigger workflow (via Smart Queue → Elsa)             │
│  ├─ Signal workflow (via Smart Queue → Elsa)              │
│  └─ Reject (duplicate/invalid)                            │
├───────────────────────────────────────────────────────────┤
│  SMART QUEUE (Story 10.4)                                 │
│  Re-validates intents against event store before dispatch  │
├───────────────────────────────────────────────────────────┤
│  EVENT STORE (Stories 10.2, 10.3, 10.7, 10.8)            │
│  PostgreSQL/Emmett — single source of truth               │
│  Raw + sanitized content — security at every layer        │
│  State reconstructed via projections                      │
├───────────────────────────────────────────────────────────┤
│  WORKFLOW PROVIDER (Story 10.5)                           │
│  IWorkflowProvider → ElsaWorkflowProvider (replaceable)   │
└──────────────────────────────────────────────────────────┘
```

## Implementation Phases

### Phase 1: Foundation (Stories 10.2, 10.3, 10.8)
- Define all event types with typed schemas
- Implement persistent event store with Emmett/PostgreSQL
- Build state reconstruction projections

### Phase 2: Engine Brain (Stories 10.1, 10.7)
- Implement static workflow with LLM decision-making
- Wire security/sanitization into event pipeline

### Phase 3: Integration (Stories 10.4, 10.5)
- Smart queue with deduplication
- Elsa workflow provider wired through abstraction

### Phase 4: Input Channels (Story 10.6)
- Webhook receivers for all git platforms
- Unified normalization layer

## Dependencies

- **Epic 1** (Providers): LLM Engine used by engine brain for decisions
- **Epic 4** (Event Sourcing): Foundation schema — we supersede Stories 4.2-4.8
- **Epic 6** (Context): Knowledge base used for richer brain decisions
- **Epic 2** (Autonomous Loop): Current engine.ts — **replaced** by this epic

## Hardware Sizing

| Scale | Events/Day | Storage/Month | PostgreSQL | Total Infra |
|-------|-----------|--------------|------------|-------------|
| Solo (1 project) | 1K-2.5K | 30-75 MB | 2 vCPU / 4 GB | $50-100/mo |
| Small team (5 projects) | 5K-12.5K | 150-375 MB | 2 vCPU / 4 GB | $50-100/mo |
| Medium (20 projects) | 20K-50K | 0.6-1.5 GB | 4 vCPU / 16 GB | $160-310/mo |
| Enterprise (100+ projects) | 100K-250K | 3-7.5 GB | 8-16 vCPU / 32-64 GB | $700-1,600/mo |

See `tech-spec-epic-10.md` for detailed benchmarks and PostgreSQL configuration.

## Success Metrics

- Engine responds to queries in <200ms without workflow provider
- Workflow dispatch via Smart Queue adds <100ms overhead
- Event store handles 50 writes/second sustained
- State reconstruction: <50ms for 500 events
- Zero data loss: every action has a corresponding event
- Raw LLM content never served to UI without sanitization

---

**Last Updated**: 2026-03-26
**Epic Owner**: Architecture Team
**Technical Spec**: `tech-spec-epic-10.md`
