---
title: "Epic 6: Context & Knowledge Management"
sidebar:
  order: 6
---

**Status:** Completed
**Stories:** 10 (6-1 through 6-10)
**Packages:** `@tamma/intelligence`, `@tamma/mcp-client`, `@tamma/cost-monitor`, `@tamma/gates`, `@tamma/scrum-master`, `@tamma/dashboard`, `@tamma/api`

## Overview

Epic 6 implements advanced context gathering and knowledge management capabilities, enabling agents to access rich, relevant context through multiple sources including vector databases, RAG systems, MCP servers, and a knowledge base. It also covers LLM cost monitoring, agent permissions, and the scrum master task loop.

## Architecture

```
+-- SCRUM MASTER TASK LOOP (6-10) ----------------------------------------+
|  PLAN -> APPROVE -> IMPLEMENT -> REVIEW -> LEARN -> COMPLETE             |
|                        |                                                  |
|                      ALERT -> ADJUST -> retry                             |
+--------------------------------------------------------------------------+
         |                |                              |
         v                v                              v
  +-- KNOWLEDGE --+  +-- PERMISSIONS --+  +-- COST MONITOR --+
  |    BASE (6-9) |  |   SYSTEM (6-8)  |  |     (6-7)        |
  | Recommend     |  | Per agent       |  | Track usage       |
  | Prohibit      |  | Per project     |  | Alerts            |
  | Learnings     |  | Enforce         |  | Limits            |
  +--------------+  +----------------+  +------------------+

  +-- CONTEXT AGGREGATOR (6-5) ----------------------------------------+
  |  Combines context | Manages token budgets | Ranks by relevance      |
  +--------------------------------------------------------------------+
         |                |                |              |
         v                v                v              v
   +-- VECTOR --+    +-- RAG ---+    +-- MCP ---+  +-- LIVE --+
   |    DB (6-2)|    | SYSTEM   |    | SERVERS  |  | SEARCH   |
   +-----+-----+    | (6-3)    |    | (6-4)    |  |          |
         |          +----------+    +----------+  +----------+
         v
   +-- INDEXER --+
   |  (6-1)      |
   +-------------+
```

## Implemented Packages

### `@tamma/intelligence` (94 source files, 57 tests)

The largest package in the codebase. Contains:

**Codebase Indexer (`src/indexer/`):**
- File discovery with gitignore-aware filtering
- Git diff detection for incremental indexing
- TypeScript-aware chunking and generic text chunking
- Embedding service with multiple providers (OpenAI, Cohere, Ollama, mock)
- Token counting and hash-based deduplication
- Configurable triggers: file watcher, git hooks, scheduler

**Vector Store (`src/vector-store/`):**
- Base interface with 5 provider implementations:
  - ChromaDB (primary for deployment)
  - pgvector (PostgreSQL extension)
  - Pinecone (cloud)
  - Qdrant (open-source)
  - Weaviate (open-source)
- Query caching layer
- Distance metrics utilities
- Metadata filtering

**RAG Pipeline (`src/rag/`):**
- Query processor with intent classification
- Multi-source retrieval (docs, GitHub, keyword, vector)
- Relevance ranking with configurable weights
- Context assembly with token budget management
- Result caching with TTL
- User feedback collection for RAG quality improvement

**Knowledge Base (`src/knowledge-base/`):**
- Knowledge service for managing recommendations, prohibitions, and learnings
- Learning capture from completed tasks with duplicate detection
- Pre-task checker that queries knowledge base before agent execution
- Multiple matchers: keyword, pattern, semantic, relevance ranking
- Prompt builder that injects relevant knowledge into agent prompts
- In-memory store (with interface for database-backed stores)

**Context System (`src/context/`):**
- Context aggregator combining multiple sources
- Token budget manager for LLM context window optimization
- Content deduplication across sources
- Relevance ranking
- Multiple sources: MCP, RAG, vector DB, web search
- Caching (memory and Redis-compatible)

### `@tamma/mcp-client` (30 source files, 21 tests)

Full Model Context Protocol client:

- **Client**: MCP session management with tool/resource/prompt access
- **Transports**: stdio, SSE, WebSocket
- **Connections**: Connection pool with health monitoring
- **Security**: Rate limiter, sandbox, validator
- **Interceptors**: Pre/post hook pipeline for tool calls
- **Built-in interceptors**: Content sanitization, URL validation
- **Registry**: Server registration and discovery
- **Pagination**: Cursor-based result pagination
- **Streaming**: Stream handling for MCP resources
- **Caching**: Capability and resource caching

### `@tamma/cost-monitor` (12 source files, 8 tests)

LLM usage cost tracking:

- **Cost Calculator**: Token-based cost calculation per provider+model
- **Cost Tracker**: Running totals per session/project/global
- **Limit Manager**: Budget limits with enforcement
- **Alert Manager**: Cost threshold alerting
- **Usage Tracker**: Detailed usage recording
- **Pricing Config**: Configurable per-model pricing tables
- **Storage**: File-based and in-memory storage backends

### `@tamma/gates` (15 source files, 10 tests)

Agent permission system:

- **Permission Enforcer**: Evaluates tool/command requests against policy
- **Permission Resolver**: Resolves effective permissions from hierarchy (global -> project -> agent)
- **Permission Service**: High-level permission management API
- **Matchers**: Tool matcher, command matcher, glob matcher
- **Violation Recorder**: Records and reports permission violations
- **Violation Alerter**: Alerts on violation patterns
- **Defaults**: Default permission sets for agent roles

### `@tamma/scrum-master` (12 source files, 8 tests)

Task orchestration and supervision:

- **Scrum Master Service**: Main coordination service
- **Task Supervisor**: Monitors agent task execution, detects stalls
- **Approval Workflow**: Human approval for critical decisions
- **Learning Capture**: Records lessons from completed tasks
- **Alert Manager**: Escalation and notification management
- **Agent Coordinator**: Coordinates multiple agents working on related tasks

### Dashboard Integration (`@tamma/dashboard`)

Knowledge base management UI:
- Index status monitoring
- Vector DB management
- RAG configuration
- MCP server management
- Context testing
- Analytics dashboards

### API Integration (`@tamma/api`)

Knowledge base API routes:
- `GET/POST /knowledge-base/index` -- Index management
- `GET/POST /knowledge-base/vector-db` -- Vector DB operations
- `GET/POST /knowledge-base/rag` -- RAG configuration
- `GET/POST /knowledge-base/mcp` -- MCP server management
- `POST /knowledge-base/context` -- Context testing
- `GET /knowledge-base/analytics` -- Usage analytics

Settings API routes (related to Epic 6):
- `GET/PUT /settings/agents` -- Agent configuration
- `GET /settings/diagnostics` -- Provider diagnostics
- `GET /settings/providers` -- Provider health
- `GET/PUT /settings/security` -- Security configuration
- `GET/PUT /settings/prompts` -- Prompt templates

---

## Stories

| Story | Title | Package(s) | Status |
|-------|-------|-----------|--------|
| 6-1 | Codebase Indexer Implementation | intelligence | Done |
| 6-2 | Vector Database Integration | intelligence | Done |
| 6-3 | RAG Pipeline Implementation | intelligence | Done |
| 6-4 | MCP Client Integration | mcp-client | Done |
| 6-5 | Context Aggregator Service | intelligence | Done |
| 6-6 | Knowledge Base Management UI | dashboard, api | Done |
| 6-7 | LLM Cost Monitoring & Reporting | cost-monitor | Done |
| 6-8 | Agent Permissions System | gates | Done |
| 6-9 | Agent Knowledge Base | intelligence | Done |
| 6-10 | Scrum Master Task Loop | scrum-master | Done |

---

## Implementation Notes

From the MEMORY.md, 6 API knowledge base services were initially mock implementations and needed wiring to real `@tamma/intelligence` packages. The dashboard hooks and API routes exist but some service connections may still use stub/mock data rather than full intelligence package integration.

---

_For story details, see [docs/stories/epic-6/](/stories/epic-6/) in the repository._
