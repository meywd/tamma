# Epic 6: Context & Knowledge Management

**Status:** Near Complete (9/10 done; 6-2 in progress — production KB/RAG sidecar wired to a real vector store + self-hosted Ollama embeddings, 2026-07-05/06)
**Stories:** 10 (6-1 through 6-10) — plus 6-11 (drafted)
**Packages:** `@tamma/intelligence`, `@tamma/intelligence-server` (sidecar), `@tamma/mcp-client`, `@tamma/cost-monitor`, `@tamma/gates`, `@tamma/scrum-master`, `@tamma/dashboard`, `@tamma/api`

## Overview

Epic 6 gives Tamma agents **access to the right context**. Without it, agents hallucinate on unfamiliar code, burn tokens re-reading the same files, or give up on ambiguous tasks. With it, a plan-generation prompt can be seeded with the 5 most relevant code chunks, the project's coding conventions, the last 3 lessons learned from similar tasks, and a live MCP server's tool catalog — all token-budgeted to fit in 100k tokens instead of flooding the context window.

The epic spans four interlocking capabilities. **Knowledge ingestion** — codebase indexer (6-1), vector database (6-2 — 5 backends), RAG pipeline (6-3), MCP client for external tool integration (6-4). **Context orchestration** — aggregator (6-5) that combines sources, ranks by relevance, deduplicates, and enforces token budgets; knowledge base UI (6-6) for operator management. **Agent governance** — cost monitoring (6-7), permissions (6-8), agent knowledge base for recommendations / prohibitions / learnings (6-9). **Task supervision** — the Scrum Master task loop (6-10) that plans / approves / implements / reviews / learns, with alert-and-adjust retries.

This is the largest single epic in the codebase by source-file count: `@tamma/intelligence` alone has ~144 TS files. The dashboard exposes all of it through an admin UI for the knowledge-base functions. The API exposes it over REST so that the Elsa workflows (Epic 2) and the Scrum Master (6-10) can pull context on demand.

## Architecture

Four packages implement the bulk. `@tamma/intelligence` holds the indexer, vector store, RAG pipeline, knowledge base, and context aggregator. `@tamma/mcp-client` is a full Model Context Protocol client with stdio/SSE/WebSocket transports, a connection pool, tool/resource/prompt interceptors, and rate limiting. `@tamma/cost-monitor` tracks token spend per session / project / global with configurable limits and alerts. `@tamma/gates` enforces per-agent / per-project permissions on every tool call. `@tamma/scrum-master` orchestrates the plan-approve-implement-review-learn loop.

The aggregator is the runtime centerpiece. When a workflow needs context for a task, it calls `contextAggregator.gather(request, hints)` with an optional task-type hint. The aggregator fans out to configured sources (vector DB, RAG retriever, MCP servers, live web search), scores results by relevance weights, deduplicates overlapping chunks, enforces a token budget, and returns a `ContextResponse` with attributions. Caching (in-memory + Redis-compatible) cuts repeat latency to <20ms.

Knowledge base entries live separately from code chunks. `KnowledgeService` manages `Recommendation` ("prefer async/await"), `Prohibition` ("never catch without logging"), and `Learning` ("last time we skipped tests, CI failed twice") entries. Pre-task checker runs before each agent task; matched entries are injected into the prompt via the template system (see Prompt Store in `CLAUDE.md`).

## Components

| Component | Purpose | Key files | Status |
|-----------|---------|-----------|--------|
| **Codebase Indexer** | Discover files (gitignore-aware), chunk (TS-aware + text), embed, dedupe, persist | `packages/intelligence/src/indexer/*` | Done (6-1) |
| Triggers | File-watch, git-hook, scheduler | `packages/intelligence/src/indexer/triggers/*` | Done |
| Embedding service | Multi-provider: OpenAI / Cohere / Ollama / mock | `packages/intelligence/src/indexer/embedding-service.ts` | Done |
| **Vector Store** interface | `IVectorStore` — upsert / query / delete / list | `packages/intelligence/src/vector-store/interfaces.ts` | Done (6-2) |
| ChromaDB adapter | Primary production store | `packages/intelligence/src/vector-store/chromadb-store.ts` | Done |
| pgvector adapter | Postgres extension backend | `packages/intelligence/src/vector-store/pgvector-store.ts` | Done |
| Pinecone / Qdrant / Weaviate | Cloud + self-hosted alternatives | `packages/intelligence/src/vector-store/*-store.ts` | Done |
| Query cache | LRU cache for recent queries | `packages/intelligence/src/vector-store/query-cache.ts` | Done |
| **RAG Pipeline** | Query processor + multi-source retriever + ranker + assembler + feedback | `packages/intelligence/src/rag/*` | Done (6-3) |
| **MCP Client** | Full MCP protocol client | `packages/mcp-client/src/*` | Done (6-4) |
| MCP transports | stdio / SSE / WebSocket | `packages/mcp-client/src/transports/` | Done |
| MCP security | Rate limiter, sandbox, validator, interceptors | `packages/mcp-client/src/security/`, `interceptors.ts` | Done |
| MCP registry + cache | Server registration, capability + resource cache | `packages/mcp-client/src/registry.ts`, `cache/` | Done |
| **Context Aggregator** | Combine + rank + dedupe + budget + cache across sources | `packages/intelligence/src/context/aggregator.ts` | Done (6-5) |
| Token Budget Manager | LLM context-window-aware allocation | `packages/intelligence/src/context/budget-manager.ts` | Done |
| **Knowledge Base** | `KnowledgeService` for Recommendations / Prohibitions / Learnings | `packages/intelligence/src/knowledge-base/*` | Done (6-9) |
| Learning capture | From completed tasks; duplicate detection | `packages/intelligence/src/knowledge-base/learning-capture.ts` | Done |
| Pre-task checker | Queries knowledge base before agent execution | `packages/intelligence/src/knowledge-base/pre-task-checker.ts` | Done |
| Matchers | Keyword, pattern, semantic, relevance ranking | `packages/intelligence/src/knowledge-base/matchers/` | Done |
| **Knowledge-base UI** | Dashboard pages for index / vector / RAG / MCP / context / analytics | `packages/dashboard/src/pages/knowledge-base/` | Done (6-6) |
| **Intelligence sidecar composition root** | Env-driven wiring of a real vector store (ChromaDB/pgvector) + embedder + RAG pipeline into the KB sidecar; graceful degrade to `not_configured` when no env is set | `packages/intelligence-server/src/env-composition.ts`, `server.ts` | Done (2026-07-05) |
| **Self-hosted embeddings (Ollama)** | Local `nomic-embed-text` (768-dim) model server in Docker Compose — KB/RAG embeddings with no OpenAI key or per-token cost | `docker/docker-compose.yml`, `docker-compose.prod.yml` (`ollama` service) | Done (2026-07-05) |
| **LLM Cost Monitor** | `CostTracker`, `LimitManager`, `AlertManager`, pricing config, storage | `packages/cost-monitor/src/*` | Done (6-7) |
| **Agent Permissions** | `PermissionEnforcer`, `PermissionResolver`, violations | `packages/gates/src/permissions/`, `violations/` | Done (6-8) |
| **Scrum Master** | Plan / approve / implement / review / learn loop orchestration | `packages/scrum-master/src/*` | Done (6-10) |
| Task Supervisor | Monitors agent execution, detects stalls | `packages/scrum-master/src/services/task-supervisor.ts` | Done |
| Approval Workflow | Human approval for critical decisions | `packages/scrum-master/src/services/approval-workflow.ts` | Done |
| API routes | `/knowledge-base/*`, `/settings/*` REST endpoints | `packages/api/src/routes/knowledge-base/` | Done |

## Class diagram

```
   IVectorStore  <<interface>>
   + upsert(chunks) ; query(vec, k) ; delete(ids) ; list()
        ^
   +----+----+----------+----------+-----------+
   |         |          |          |           |
ChromaDB pgvector   Pinecone    Qdrant     Weaviate


   CodebaseIndexer
   - embedding : IEmbeddingService
   - store     : IVectorStore
   - triggers  : [FileWatcher|GitHook|Scheduler]
   + index(root, filters) : Promise<IndexResult>
   + reindexIncremental(diffSet)


   RAGPipeline
   - processor : QueryProcessor
   - retriever : MultiSourceRetriever
   - ranker    : RelevanceRanker
   - assembler : ContextAssembler
   - cache     : ResultCache
   + query(text, hints) : Promise<RAGResponse>
                                    ^
                                    |
                            (feedback via Feedback module)


   MCPClient
   - transport : IMCPTransport  (stdio|SSE|WebSocket)
   - interceptors : ToolHookRegistry  (pre/post hooks — content sanitization etc)
   - pool       : ConnectionPool
   - cache      : CapabilityCache + ResourceCache
   + invokeTool(serverId, tool, args) : Promise<ToolResult>
   + listResources(serverId) ; getPrompt(serverId, name)


   ContextAggregator
   - vectorStore : IVectorStore
   - rag         : RAGPipeline
   - mcpClient   : MCPClient
   - liveSearch  : ILiveSearchProvider
   - budget      : TokenBudgetManager
   - ranker      : RelevanceRanker
   - dedup       : Deduplicator
   + gather(request, hints) : Promise<ContextResponse>


   KnowledgeService
   - store : IKnowledgeStore
   + addRecommendation / addProhibition / addLearning
   + match(taskContext) : MatchedKnowledge[]
   + capture(completedTask) : LearningEntry[]


   CostTracker  (packages/cost-monitor)
   - storage    : ICostStorage
   - calculator : CostCalculator
   - pricing    : PricingConfig
   + record(usage) ; getRunningTotal(scope) ; getReport(timeframe)
   LimitManager + AlertManager hang off CostTracker


   PermissionEnforcer  (packages/gates)  — see Epic 3 for diagram


   ScrumMasterService  (packages/scrum-master)
   - supervisor : TaskSupervisor
   - approval   : ApprovalWorkflow
   - learning   : LearningCapture
   - coordinator: AgentCoordinator
   + planTask() ; approveTask() ; implementTask() ; reviewTask() ; learnTask()
     loop:  plan -> approve -> implement -> review -> learn
            on alert -> adjust -> retry
```

## Data flow — "agent asks for context, aggregator composes answer" sequence

```
Elsa workflow      ContextAggregator    RAGPipeline    IVectorStore   KnowledgeService   MCPClient
     |                    |                 |                 |               |                |
     | agent task begins  |                 |                 |               |                |
     |                    |                 |                 |               |                |
     | gather(request,    |                 |                 |               |                |
     |   { taskType: 'implement',           |                 |               |                |
     |     issueBody, files[] })            |                 |               |                |
     |------------------->|                 |                 |               |                |
     |                    |                 |                 |               |                |
     |                    | 1. extract query embeddings                       |                |
     |                    |                 |                 |               |                |
     |                    | 2. retrieve from sources in parallel:             |                |
     |                    |                                                                    |
     |                    |-------- RAG.query(text, hints) -->|                                |
     |                    |                                   | retrieve chunks by embedding   |
     |                    |                                   | + keyword + metadata filter    |
     |                    |<----------- RAGResponse ----------|                                |
     |                    |                                                                    |
     |                    |-------- IVectorStore.query(vec, k=10) -----------|                |
     |                    |                                                                    |
     |                    |-------- KnowledgeService.match(taskCtx) ------->|                 |
     |                    |<-------- MatchedKnowledge (recs + prohibs + learnings) ---       |
     |                    |                                                                    |
     |                    |-------- MCPClient.invokeTool("docs-search", {...}) ------------->|
     |                    |<------- ToolResult (external doc chunks)  ------------------------|
     |                    |                                                                    |
     |                    | 3. rank + deduplicate                                               |
     |                    | 4. enforce token budget  (e.g. 100k => trim lowest-ranked)         |
     |                    | 5. cache result with 60s TTL                                       |
     |                    |                                                                    |
     |<---- ContextResponse {                                                                  |
     |        chunks: [ ... ordered by relevance ... ],                                        |
     |        attributions: { rag: 7, vector: 3, mcp: 1, knowledge: 2 },                       |
     |        tokensUsed: 94_321,                                                              |
     |        metrics: { latencyMs, cacheHit: false }                                          |
     |      }                                                                                  |
     |                                                                                          |
     | inject into agent prompt via PromptBuilder                                              |
     | agent executes task                                                                     |
     | emit CONTEXT.GATHERED.SUCCESS  (Epic 4)                                                 |
     |                                                                                          |
     | on completion:                                                                          |
     |   KnowledgeService.capture(completedTask)  --> new learning entry added                 |
     |   CostTracker.record(tokenUsage, provider, model)  --> budget decrement                 |
```

## Use cases

- **Agent planning a new feature** needs **relevant existing code**: `ContextAggregator.gather` → RAG returns the 5 most similar chunks from the codebase → vector store returns tests that exercise affected modules → knowledge base injects "last time we added an auth feature, we forgot CSRF" → plan includes CSRF from the start.
- **Operator** wants **to add a company-specific prohibition**: opens Knowledge Base UI → navigates to Prohibitions → adds "never log customer PII" with pattern match on common PII fields → all future agent tasks check against this before execution (6-9).
- **Platform engineer** wants **to integrate Confluence docs**: configures an MCP server for Confluence → `MCPClient` registers it → context aggregator pulls Confluence pages when queries match → attribution tracks which chunks came from Confluence (6-4).
- **Finance** wants **to enforce a $100/day budget for AI usage on experimental projects**: sets `CostLimit(project='experiments', daily=100)` → `CostTracker` routes each token charge through `LimitManager` → at 90% operator is alerted → at 100% agents pause (6-7).
- **Dev lead** wants **to restrict which tools the researcher agent can use**: configures agent permissions — `{ allowed: ['Read', 'WebSearch'], denied: ['Bash', 'Write'] }` → `PermissionEnforcer` blocks any `Bash` call from researcher → violation recorded + alerted (6-8).
- **Scrum Master** coordinates **a 3-task breakdown**: plans all 3, requests approval on the plan → human approves → supervises each implement → reviews output → captures one learning per task → marks cycle complete (6-10).

## Dependencies

**Upstream:**
- [Epic 1](Epic-1-Foundation.md) — `IAgentProvider` + `ILLMProvider` for embeddings.
- [Epic 2](Epic-2-Autonomous-Loop.md) — `ContextGatheringWorkflow` calls the aggregator.
- [Epic 4](Epic-4-Event-Sourcing.md) — context gather + knowledge capture emit events.

**Downstream:**
- [Epic 3](Epic-3-Quality-Gates.md) — research activity (3-4) consumes RAG; cost-monitor + permission packages are co-owned.
- [Epic 5](Epic-5-Observability.md) — analytics dashboards aggregate cost + knowledge usage.
- [Epic 9](Epic-9-Agent-Management.md) — multi-agent coordination builds on the Scrum Master pattern.
- [Epic 11](Epic-11-Security.md) — MCP client's content-sanitization interceptor implements the security plan.
- [Epic 27](Epic-27-Prompt-Store.md) — prompt builder injects knowledge into templates.

## Current state

**Landed:**

- **Codebase Indexer** (6-1) — full indexer with TS-aware chunking, git-diff incremental reindex, file-watch / git-hook / scheduler triggers, multi-provider embeddings.
- **RAG Pipeline** (6-3) — multi-source retriever, ranker, assembler, feedback capture, result cache.
- **MCP Client** (6-4) — protocol-complete client with stdio/SSE/WebSocket transports, interceptors for content sanitization, connection pool, capability cache.
- **Context Aggregator** (6-5) — source fan-out, token budget, relevance ranking, deduplication, caching.
- **Knowledge Base UI** (6-6) — dashboard pages for index / vector / RAG / MCP / context test / analytics.
- **Cost Monitor** (6-7) — `CostTracker`, `LimitManager`, `AlertManager`, pricing config, file + in-memory storage backends.
- **Agent Permissions** (6-8) — enforcer + resolver + defaults + violation recorder + alerter.
- **Agent Knowledge Base** (6-9) — recommendations / prohibitions / learnings with 4 matcher types.
- **Scrum Master** (6-10) — full service with supervisor, approval workflow, learning capture, agent coordinator.
- **Production sidecar wiring** (2026-07-05/06) — the `intelligence-server` KB/RAG sidecar's composition root (`env-composition.ts`) now builds a **real** `@tamma/intelligence` vector store (ChromaDB preferred, pgvector fallback) + embedding service + RAG pipeline from environment variables, replacing the `not_configured` stubs whenever a store is configured (unconfigured deployments still boot and degrade gracefully). The RAG collection is bootstrapped at composition time (created empty at the embedder's dimensions if missing) so a fresh, never-indexed deployment boots `configured` instead of `not_configured`.
- **Self-hosted embeddings** (2026-07-05) — Docker Compose now runs a local **Ollama** service serving `nomic-embed-text` (768-dim); the model is pulled once into a persisted volume on first boot and the sidecar's Ollama embedder initializes config-only, so embeddings run with **no OpenAI key and no per-token cost**.
- **Prod-image hardening** (2026-07-05) — the sidecar imports `EmbeddingService` via the narrow `@tamma/intelligence/embedding` subpath instead of the `/indexer` barrel (which transitively value-imported the `typescript` devDependency and crash-looped the `--prod`-pruned Docker image); guarded by a prod-import-graph test + a prod-pruned boot smoke test, plus a Dockerfile fix.

**In progress:**

- 6-2 Vector Database Integration — 5 adapters (ChromaDB, pgvector, Pinecone, Qdrant, Weaviate) exist; production uses ChromaDB (wired end-to-end via the sidecar composition root above); some adapters need integration tests.

**Drift from briefs:**

- 6 API knowledge-base routes were initially stubbed mocks (per `MEMORY.md`). The Epic 6 implementation plan added a phase to wire them to the real `@tamma/intelligence` services. Some routes may still return partial data.
- The brief has 10 stories; the codebase shows `story-6-11` in `docs/stories/epic-6/` suggesting an additional story has been drafted but is not in the original README.
- The `@tamma/scrum-master` package implements 6-10 as its own package rather than inside `@tamma/intelligence` — structurally cleaner, tracks separately.
- Permission system (6-8) lives in `@tamma/gates` (shared with Epic 3), not inside `@tamma/intelligence`. Cost monitor (6-7) lives in `@tamma/cost-monitor` — same reason (used by Epic 3 as well).

## See also

- **Docs:** [docs/stories/epic-6/](https://github.com/meywd/tamma/tree/main/docs/stories/epic-6) — story briefs.
- **Related wiki pages:**
  - [Architecture](Architecture) — overall context / knowledge strategy.
  - [Workflow: Context Gathering](Workflow-Context-Gathering) — workflow consumer of the aggregator.
  - [Epic 3: Quality Gates](Epic-3-Quality-Gates.md) — co-owned permission + cost packages.
  - [Epic 9: Agent Management](Epic-9-Agent-Management.md) — extends Scrum Master coordination.
  - [Epic 11: Security](Epic-11-Security.md) — MCP content-sanitization plan.
  - [Epic 27: Prompt Store](Epic-27-Prompt-Store.md) — prompt template injection of knowledge.
- **Code paths:**
  - `packages/intelligence/src/` — indexer, vector store, RAG, knowledge base, context aggregator.
  - `packages/intelligence-server/src/env-composition.ts` — sidecar composition root (real vector store + embedder + RAG from env).
  - `docker/docker-compose.yml`, `docker/docker-compose.prod.yml` — `ollama` service for self-hosted embeddings.
  - `packages/mcp-client/src/` — MCP client.
  - `packages/cost-monitor/src/` — cost tracking + limits + alerts.
  - `packages/gates/src/permissions/` — agent permissions.
  - `packages/scrum-master/src/` — task loop orchestration.
  - `packages/dashboard/src/pages/knowledge-base/` — dashboard UI.
  - `packages/api/src/routes/knowledge-base/` — REST endpoints.
