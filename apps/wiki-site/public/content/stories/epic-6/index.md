---
title: "Epic 6: Context & Knowledge Management"
---

## Overview

This epic covers the implementation of advanced context gathering and knowledge management capabilities for Tamma. It enables agents to access rich, relevant context through multiple sources including vector databases, RAG systems, and MCP servers.

## Stories

| Story | Title | Priority | Status |
|-------|-------|----------|--------|
| 6-1 | Codebase Indexer Implementation | P1 | Planned |
| 6-2 | Vector Database Integration | P1 | Planned |
| 6-3 | RAG Pipeline Implementation | P1 | Planned |
| 6-4 | MCP Client Integration | P2 | Planned |
| 6-5 | Context Aggregator Service | P2 | Planned |
| 6-6 | Knowledge Base Management UI | P3 | Planned |
| 6-7 | LLM Cost Monitoring & Reporting | P1 | Planned |
| 6-8 | Agent Permissions System | P1 | Planned |
| 6-9 | Agent Knowledge Base (Recommendations, Prohibited, Learnings) | P1 | Planned |
| 6-10 | Scrum Master Task Loop | P1 | Planned |

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                           EPIC 6 ARCHITECTURE                                    │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                  │
│  ┌─────────────────────────────────────────────────────────────────────────┐   │
│  │                     SCRUM MASTER TASK LOOP (6-10)                        │   │
│  │                                                                          │   │
│  │   PLAN ──► APPROVE ──► IMPLEMENT ──► REVIEW ──► LEARN ──► COMPLETE     │   │
│  │     │         │            │           │          │                      │   │
│  │     └─────────┴────────────┴───────────┴──────────┘                     │   │
│  │                        ▼ (on issues)                                     │   │
│  │                      ALERT ──► ADJUST ──► retry                         │   │
│  │                                                                          │   │
│  └─────────────────────────────────────────────────────────────────────────┘   │
│                          │                                                      │
│         ┌────────────────┼────────────────────────────────┐                    │
│         ▼                ▼                                ▼                    │
│  ┌──────────────┐ ┌──────────────┐              ┌──────────────┐              │
│  │  KNOWLEDGE   │ │ PERMISSIONS  │              │     COST     │              │
│  │    BASE      │ │   SYSTEM     │              │   MONITOR    │              │
│  │   (6-9)      │ │   (6-8)      │              │    (6-7)     │              │
│  │              │ │              │              │              │              │
│  │ • Recommend  │ │ • Per agent  │              │ • Track usage│              │
│  │ • Prohibit   │ │ • Per project│              │ • Alerts     │              │
│  │ • Learnings  │ │ • Enforce    │              │ • Limits     │              │
│  └──────────────┘ └──────────────┘              └──────────────┘              │
│                                                                                  │
│  ┌─────────────────────────────────────────────────────────────────────────┐   │
│  │                     CONTEXT AGGREGATOR (6-5)                             │   │
│  │                                                                          │   │
│  │   Combines context • Manages token budgets • Ranks by relevance         │   │
│  │                                                                          │   │
│  └─────────────────────────────────────────────────────────────────────────┘   │
│                          │                                                      │
│         ┌────────────────┼────────────────┬──────────────┐                     │
│         ▼                ▼                ▼              ▼                     │
│   ┌───────────┐    ┌───────────┐    ┌───────────┐  ┌────────┐                 │
│   │  VECTOR   │    │    RAG    │    │    MCP    │  │  LIVE  │                 │
│   │    DB     │    │  SYSTEM   │    │  SERVERS  │  │ SEARCH │                 │
│   │  (6-2)    │    │  (6-3)    │    │  (6-4)    │  │        │                 │
│   └─────┬─────┘    └───────────┘    └───────────┘  └────────┘                 │
│         │                                                                      │
│         ▼                                                                      │
│   ┌───────────┐                              ┌───────────────────────────┐    │
│   │  INDEXER  │                              │   KNOWLEDGE BASE UI (6-6) │    │
│   │  (6-1)    │                              └───────────────────────────┘    │
│   └───────────┘                                                                │
│                                                                                  │
└─────────────────────────────────────────────────────────────────────────────────┘
```

## Dependencies

- Epic 1: Provider interfaces (for embedding providers)
- Epic 2: Engine integration (context passed to agents)
- Epic 5: Observability (monitoring context retrieval)

## Implementation Phases

### Phase 1: Core Infrastructure (Stories 6-1, 6-2, 6-7)
- Codebase indexer with chunking strategies
- Vector database integration (ChromaDB/pgvector)
- LLM cost monitoring and reporting

### Phase 2: RAG & MCP (Stories 6-3, 6-4)
- RAG pipeline with hybrid search
- MCP client for external tool integration

### Phase 3: Agent Management (Stories 6-8, 6-9)
- Agent permissions system (global + per-project)
- Agent knowledge base (recommendations, prohibitions, learnings)

### Phase 4: Integration & Orchestration (Stories 6-5, 6-10)
- Context aggregator service
- Scrum Master task loop (plan/approve/implement/review/learn)

### Phase 5: UI (Story 6-6)
- Knowledge base management dashboard

## Success Metrics

- Context retrieval latency < 200ms (p95)
- Relevant code found in top-5 results > 85%
- Agent task success rate improvement > 15%
- Reduced token usage through better context selection
- Cost tracking accuracy: 100%
- Permission violation rate < 1%
- Learning capture rate > 80%
- Task completion rate (with Scrum Master loop) > 85%
