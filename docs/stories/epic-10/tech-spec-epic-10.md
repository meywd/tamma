# Epic 10 Technical Specification: Engine Core — Workflow-Driven Architecture

Date: 2026-03-26
Author: Tamma Architecture Team
Epic ID: 10
Status: Draft
Depends On: Epic 1 (Providers), Epic 4 (Event Sourcing Foundation), Epic 6 (Context & Knowledge)

---

## Overview

Epic 10 redefines the Tamma Engine from a hardcoded imperative state machine into a **workflow-driven orchestration service** where:

1. The **Engine** is a standalone service that CLI, web, mobile, and desktop clients connect to
2. The Engine has its own **static workflow** (hardcoded brain) that receives all inputs, consults an LLM for decision-making, and routes to workflow execution or direct answers
3. **Elsa** (or any workflow provider) handles task execution workflows, but is a **replaceable provider** behind an abstraction
4. The **Event Store** is the single source of truth — every actor (engine, Elsa, UI, platform) writes events; state is reconstructed from the event stream
5. All inputs — whether from a user typing in CLI or a GitHub webhook — are normalized and processed through the same engine brain

This epic replaces the current `TammaEngine.runPipeline()` 8-step imperative loop with a properly separated architecture.

## Architectural Principles

### 1. Engine Is the Brain, Not the Hands

The engine does NOT execute development tasks. It:
- Receives inputs (user commands, platform events, workflow callbacks)
- Loads current state from the event store
- Calls the LLM to understand context and decide what to do
- Routes decisions to workflow triggers, direct answers, or queued intents
- Records every decision to the event store

### 2. Elsa Is Replaceable

All workflow interaction goes through `IWorkflowProvider` (evolved from current `IWorkflowEngine`). The engine never imports Elsa-specific types. The provider can be swapped for Temporal, Conductor, or a simple in-process runner.

### 3. Event Store Is Truth

No component trusts its own memory. State is always derived from the event stream. Every action — from every actor — is recorded. Raw and sanitized content are stored as separate events. The sanitization action itself is an event.

### 4. Security Is an Event, Not Just a Filter

LLM calls produce: raw request event → sanitization event → dispatched event → raw response event → sanitization event → completed event. When reading, only sanitized content is served unless elevated access is granted. Output encoding happens at the API boundary.

---

## System Architecture

```
┌──────────────────────────────────────────────────────────────────────────┐
│                         INPUT CHANNELS                                    │
├──────────────────┬────────────────┬───────────────┬──────────────────────┤
│   CLI (Ink)      │  Web (React)   │  Mobile/Desktop│  Platform Events    │
│   InProcess      │  HTTP/SSE      │  HTTP/SSE      │  Webhooks           │
│   Transport      │  Transport     │  Transport     │  (GitHub, Gitea,    │
│                  │                │                │   GitLab, etc.)     │
└────────┬─────────┴───────┬────────┴───────┬────────┴──────────┬─────────┘
         │                 │                │                   │
         └─────────────────┴────────────────┴───────────────────┘
                                    │
                           NORMALIZE TO EVENT
                           Store immediately
                                    │
                                    ▼
┌──────────────────────────────────────────────────────────────────────────┐
│                     ENGINE (Static Workflow / Brain)                       │
│                                                                           │
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │  1. INTAKE: Receive normalized input event                         │  │
│  │  2. STATE:  Load current state from Event Store                    │  │
│  │  3. DECIDE: Call LLM brain — "given state + input, what to do?"    │  │
│  │  4. ROUTE:                                                         │  │
│  │     ├─ Answer directly → respond to client                         │  │
│  │     ├─ Trigger workflow → enqueue to Smart Queue                   │  │
│  │     ├─ Signal workflow → enqueue signal to Smart Queue             │  │
│  │     └─ Reject → duplicate/invalid, respond with reason             │  │
│  │  5. RECORD: Write decision event to Event Store                    │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                           │
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │  SMART QUEUE                                                       │  │
│  │  ├─ Ordered intents (start workflow, send signal, etc.)            │  │
│  │  ├─ Before dispatch: re-validate against current event store state │  │
│  │  ├─ Deduplication: "workflow already running for #42" → drop       │  │
│  │  ├─ Priority: signals before triggers, approvals before new work   │  │
│  │  ├─ Drains to workflow provider when available                     │  │
│  │  └─ Holds when workflow provider is down                           │  │
│  └──────────────┬─────────────────────────────────────────────────────┘  │
│                 │                                                         │
│  ┌──────────────▼─────────────────────────────────────────────────────┐  │
│  │  EVENT STORE (Single Source of Truth)                               │  │
│  │                                                                     │  │
│  │  ← Engine writes: decisions, commands, responses                    │  │
│  │  ← Workflow provider writes: step progress, completions             │  │
│  │  ← UI writes: user actions                                         │  │
│  │  ← Platform writes: webhook events                                  │  │
│  │  → All components read from here to build state                     │  │
│  │                                                                     │  │
│  │  Security layers:                                                   │  │
│  │  ├─ Raw content stored with restricted access                       │  │
│  │  ├─ Sanitized content stored as separate linked event               │  │
│  │  ├─ Sanitization action itself is an event                          │  │
│  │  └─ Output encoding at API read boundary                            │  │
│  └─────────────────────────────────────────────────────────────────────┘  │
└──────────────────┬───────────────────────────────────────────────────────┘
                   │
          ┌────────┴────────┐
          ▼                 ▼
┌──────────────────┐  ┌──────────────────────────────────────────────────┐
│ Workflow Provider │  │ LLM Engine (existing — Epic 1)                   │
│ (Elsa / other)   │  │ ├─ Role-based agent resolver                     │
│                  │  │ ├─ Provider chains with fallback                 │
│ IWorkflowProvider│  │ ├─ Budget enforcement                            │
│ abstraction      │  │ ├─ Prompt registry                               │
│                  │  │ └─ SecureAgentProvider wrapping                  │
│ Writes events    │  └──────────────────────────────────────────────────┘
│ to Event Store   │
│                  │
│ Calls back to    │
│ Engine on step   │
│ completion       │
└──────────────────┘
```

---

## Hardware Sizing & Capacity Analysis

### Event Volume Estimation

For a **single project** running autonomous development:

| Activity | Events per Occurrence | Occurrences per Issue | Events per Issue |
|----------|----------------------|----------------------|-----------------|
| Input intake + decision | 3-5 | 1 | 5 |
| Issue analysis (LLM calls) | 6 (raw + sanitize + dispatch + response + sanitize + complete) | 2-3 | 18 |
| Plan generation | 6 | 1 | 6 |
| Approval flow | 3-4 | 1 | 4 |
| Implementation (multiple LLM rounds) | 6 | 10-50 | 300 |
| Git operations | 2-3 | 5-10 | 25 |
| PR creation + CI monitoring | 3-5 | 3-5 | 20 |
| Quality gates | 4-6 | 2-3 | 15 |
| State snapshots | 1 | periodic | 10 |
| **Total per issue** | | | **~400-500** |

**Scaling projections:**

| Scale | Issues/Day | Events/Day | Events/Month | Storage/Month |
|-------|-----------|------------|-------------|--------------|
| Solo developer (1 project) | 2-5 | 1,000-2,500 | 30K-75K | 30-75 MB |
| Small team (5 projects) | 10-25 | 5K-12.5K | 150K-375K | 150-375 MB |
| Medium team (20 projects) | 40-100 | 20K-50K | 600K-1.5M | 0.6-1.5 GB |
| Enterprise (100 projects) | 200-500 | 100K-250K | 3M-7.5M | 3-7.5 GB |

**Assumed average event size:** ~1 KB (JSONB payload with metadata, excluding blob-stored raw content)

### PostgreSQL Performance Characteristics

**Write throughput (event appends) — real benchmarks:**
- PostgreSQL with JSONB: 5,000-18,500 inserts/second (production-measured at 30M row scale)
- Commanded (Elixir/PG): 5,000-8,600 events/sec with 50 concurrent writers
- Custom PG batched: ~10,000 events/sec (batches of 20)
- Custom PG on AWS t2.micro: ~1,000 events/sec (minimal hardware)
- SoftwareMill Reactive: ~2,500 req/sec single node, ~4,000 req/sec 3 nodes (20ms P99)
- **Tamma requirement (enterprise):** ~3 events/second sustained, ~50 events/second burst
- **Verdict:** PostgreSQL handles this trivially — even minimal hardware exceeds our needs by 20x

**Read throughput (state reconstruction):**
- JSONB GIN index containment (`@>`) queries: <30ms on 10M rows (98% faster than unindexed)
- Production measurement at 30M rows: P95 query time = 78ms
- Aggregate rehydration: 10,000 events loaded in ~50ms (Patchlevel benchmark)
- State reconstruction (read all events for one workflow): <5ms for typical 400-500 events
- **Critical optimization:** Periodic state snapshots reduce reconstruction to: read snapshot + events since snapshot
- **Critical rule:** Use `@>` containment operator, NOT `->>` extraction — GIN cannot optimize `->>` queries
- Use `jsonb_path_ops` operator class for containment-only queries (smaller, faster index)

**JSONB GIN Index scaling (production data at 30M rows):**
- Table size: ~15 GB compressed
- GIN index size: ~2.5 GB (~17% of table size)
- Write overhead: significant at >1,000 updates/min on single table (GitLab production finding)
- **Mandatory:** Time-based table partitioning beyond ~50M total events
- **Mandatory:** Periodic `REINDEX CONCURRENTLY` to manage bloat

**Per-event storage (measured):**
- JSON payload: 200-500 bytes
- PostgreSQL row overhead (tuple header + alignment): 60-100 bytes
- Index entries per row: 24-50 bytes
- **Total per event on disk: ~300-700 bytes** (use 500 bytes for planning, 1 KB with blob references)
- Production reference: 8,610 events = 5 MB (~580 bytes/event average)

**Emmett-specific notes:**
- Emmett v0.42+ has no published benchmarks — expect PG-range performance (1,000-10,000 events/sec)
- Inline projections update read models in same transaction (adds latency to appends)
- One event store instance per application recommended (internal connection pool)
- If Emmett becomes a constraint, `IEventStore` interface allows direct PG or EventStoreDB swap

### Hardware Tiers

#### Tier 1: Solo/Small Team (1-5 projects)
```
PostgreSQL:
  CPU: 2 vCPU
  RAM: 4 GB (shared_buffers: 1 GB)
  Storage: 20 GB SSD
  Est. cost: $20-40/month (cloud VM)

Engine + API:
  CPU: 2 vCPU
  RAM: 2 GB
  Est. cost: $15-30/month

Elsa Server (.NET):
  CPU: 2 vCPU
  RAM: 2 GB
  Est. cost: $15-30/month

Total: $50-100/month
Can run on single 4 vCPU / 8 GB machine: $40-60/month
```

#### Tier 2: Medium Team (5-20 projects)
```
PostgreSQL:
  CPU: 4 vCPU
  RAM: 16 GB (shared_buffers: 4 GB)
  Storage: 100 GB SSD
  Est. cost: $80-150/month

Engine + API:
  CPU: 4 vCPU
  RAM: 4 GB
  Est. cost: $40-80/month

Elsa Server:
  CPU: 4 vCPU
  RAM: 4 GB
  Est. cost: $40-80/month

Total: $160-310/month
```

#### Tier 3: Enterprise (20-100+ projects)
```
PostgreSQL:
  CPU: 8-16 vCPU
  RAM: 32-64 GB (shared_buffers: 8-16 GB)
  Storage: 500 GB-1 TB SSD (with partitioning by month)
  Connection pooling: PgBouncer
  Read replicas: 1-2 for dashboard queries
  Est. cost: $300-800/month

Engine + API (horizontally scaled):
  2-4 instances × (4 vCPU, 4 GB RAM)
  Load balancer
  Est. cost: $200-400/month

Elsa Server (horizontally scaled):
  2-4 instances × (4 vCPU, 4 GB RAM)
  Est. cost: $200-400/month

Blob storage (raw LLM content):
  S3/MinIO: $0.023/GB/month
  Est: 50-200 GB/month = $1-5/month

Total: $700-1,600/month
```

### PostgreSQL Configuration for Event Sourcing

```ini
# Tier 1 (minimal)
shared_buffers = 1GB
effective_cache_size = 3GB
work_mem = 4MB
maintenance_work_mem = 256MB
wal_level = replica
synchronous_commit = on
max_connections = 50

# Tier 3 (enterprise)
shared_buffers = 16GB
effective_cache_size = 48GB
work_mem = 16MB
maintenance_work_mem = 2GB
wal_level = replica
synchronous_commit = on
max_connections = 200
max_wal_size = 4GB

# Event table partitioning (enterprise)
# Partition by month for efficient retention and vacuuming
# Archive partitions older than retention period to cold storage
```

### Emmett Integration Notes

Emmett (v0.23+) provides:
- **Native DCB support** with PostgreSQL — single stream, tag-based filtering
- **Append-only semantics** built in
- **Optimistic concurrency** via stream position
- **Subscription support** for real-time event consumption
- **Schema registry** integration for event versioning

Emmett sits as a thin layer over PostgreSQL — it does NOT add significant overhead. The bottleneck is always PostgreSQL I/O, which as shown above is not a concern at Tamma's scale.

**If Emmett becomes a bottleneck or constraint**, the event store interface (`IEventStore`) allows swapping to:
- Direct PostgreSQL with custom SQL (drop Emmett, keep the table)
- EventStoreDB (dedicated event store, higher throughput, different operational model)
- Apache Kafka (for extreme scale, adds operational complexity)

---

## Stories Breakdown

| Story | Title | Priority | Dependencies |
|-------|-------|----------|-------------|
| 10.1 | Engine Static Workflow & Brain | P0 | Epic 1 (LLM providers) |
| 10.2 | Comprehensive Event Catalog & Typed Schema | P0 | None |
| 10.3 | Event Store — PostgreSQL/Emmett Implementation | P0 | Story 10.2 |
| 10.4 | Smart Queue with State-Based Deduplication | P0 | Stories 10.1, 10.3 |
| 10.5 | Workflow Provider Abstraction & Elsa Integration | P0 | Story 10.3 |
| 10.6 | Input Channel Unification (UI + Platform Events) | P1 | Story 10.1 |
| 10.7 | Event Store Security & Sanitization Pipeline | P0 | Stories 10.2, 10.3 |
| 10.8 | State Reconstruction from Event Stream | P0 | Stories 10.2, 10.3 |

### Implementation Phases

**Phase 1: Foundation (Stories 10.2, 10.3, 10.8)**
- Define all event types with typed schemas
- Implement persistent event store with Emmett/PostgreSQL
- Build state reconstruction engine

**Phase 2: Engine Brain (Stories 10.1, 10.7)**
- Implement static workflow with LLM decision-making
- Wire security/sanitization into event pipeline

**Phase 3: Integration (Stories 10.4, 10.5)**
- Smart queue with deduplication
- Elsa workflow provider wired through abstraction

**Phase 4: Input Channels (Story 10.6)**
- Webhook receivers for all git platforms
- Unified normalization layer

---

## Dependencies on Existing Epics

| Epic | What We Use | What Changes |
|------|------------|-------------|
| Epic 1 (Providers) | LLM Engine (roles, chains, budget, prompts) | No changes — engine brain calls LLM engine as-is |
| Epic 4 (Event Sourcing) | Event schema foundation (Story 4.1) | We supersede Stories 4.2-4.8 with production implementation |
| Epic 6 (Context) | Knowledge base, permissions | Engine brain uses context for better decisions |
| Epic 2 (Autonomous Loop) | Current engine.ts | **Replaced** — runPipeline() removed, replaced by static workflow + Elsa |

---

## Risks and Mitigations

### Risk 1: LLM Decision Latency
- **Risk:** Engine brain LLM call adds 1-3 seconds to every user interaction
- **Mitigation:** Use fast model (Haiku-class) for routing decisions; cache common decision patterns; bypass LLM for unambiguous commands (e.g., "approve" when one plan pending)

### Risk 2: Event Store as Bottleneck
- **Risk:** Reading full event stream for state reconstruction becomes slow at scale
- **Mitigation:** Periodic state snapshots; read snapshot + delta events; CQRS-style read projections for dashboards

### Risk 3: Elsa Unavailability
- **Risk:** Elsa server down means no workflow execution
- **Mitigation:** Smart queue holds intents; engine still answers queries from event store; health check with reconnect on Elsa recovery; queued intents re-validated before dispatch

### Risk 4: Event Schema Evolution
- **Risk:** Adding new event types or changing payloads breaks consumers
- **Mitigation:** Schema version field; additive-only changes for minor versions; consumer-driven contract tests; old events remain valid forever

### Risk 5: Security Surface
- **Risk:** Raw LLM content in event store could contain XSS/injection payloads
- **Mitigation:** Raw content in separate blob storage with restricted access; event store contains sanitized content only; output encoding at every read boundary; Content-Security-Policy headers on all API responses

---

## Success Metrics

- Engine responds to user queries (status, history) in <200ms without Elsa
- Workflow dispatch via Smart Queue adds <100ms overhead
- Event store handles 50 writes/second sustained (10x enterprise peak)
- State reconstruction from events: <50ms for typical workflow (500 events)
- Zero data loss: every action has a corresponding event
- Elsa downtime does not crash engine or lose user commands
- Raw LLM content never served to UI without sanitization
