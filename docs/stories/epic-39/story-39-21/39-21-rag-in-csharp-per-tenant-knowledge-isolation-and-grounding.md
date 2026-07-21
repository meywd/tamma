# Story 39-21: RAG in C# — per-tenant knowledge isolation and grounding

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As the **orchestrator agent answering questions and grounding decisions, the lifecycle producing documents, and a tenant whose knowledge must never leak**,
I want **the RAG/KB capability ported to the C# backend — indexing, embeddings, vector search, and the retrieval pipeline — with vector data living inside each tenant's schema (pgvector) so isolation rides the existing schema-per-tenant model, exposed to the 39-17 agent as knowledge tools and to the lifecycle as optional retrieval grounding**,
So that RAG stops being a dashboard-only TypeScript sidecar nothing in the generation path consumes, becomes a first-class, tenant-isolated C# capability, and every agent answer or produced document can be grounded in the tenant's own indexed knowledge.

## Priority

P1 — Not blocking the lifecycle pillar (39-2..39-6 run without retrieval), but the 39-17 agent's chat duty ("ask about things") and decision grounding are materially weaker without it, and the current TS-sidecar RAG has no tenant isolation — a hard blocker for using it in SaaS at all.

## Architectural Context (READ FIRST)

**What exists today (working, but TS and tenant-blind):**

- `packages/intelligence` (`@tamma/intelligence`) — a complete TS RAG library: indexer (gitignore/git-diff discovery, AST + generic chunking, Ollama `nomic-embed-text` / Cohere / mock embedding providers), a vector-store abstraction with five providers (ChromaDB, pgvector, Pinecone, Qdrant, Weaviate), and a real pipeline: query processor → multi-source retriever (vector, keyword, docs, GitHub issues) → ranker (RRF/weighted fusion, MMR, recency boost) → token-budgeted context assembler (XML/markdown) + query cache + relevance feedback.
- `packages/intelligence-server` — a Fastify sidecar (port 4100) wrapping it; the C# API proxies **30 `/api/kb/*` routes** verbatim (`apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs` → `IIntelligenceHttpClient`).
- Deployment: all on the VPS via Docker Compose (`docker/docker-compose.yml` — `intelligence-server`, `chromadb`, `ollama` services); nothing RAG-related is on Cloudflare (only the wiki site is).
- **The gap that motivates this story:** nothing in the C# generation path consumes retrieval — `LlmCallWorkflow` never calls `/kb/rag/query`; the index has no tenant partitioning; the sidecar is single-instance.

**Port, don't re-invent the design.** The TS pipeline's shape (sources → fusion ranking → budgeted assembly, config surface with weights/topK/mmrLambda/recency/cache) is proven — the C# port (`Tamma.Intelligence` project or a namespace in `Tamma.Api`) reimplements that shape, it does not design a new one. CLAUDE.md's "no migration anxiety" applies: the ChromaDB index is rebuildable, not migrated.

**Per-tenant isolation comes from the tenancy model, not from the vector store.** Vector data lands in **pgvector tables inside each tenant's `t_<hex>` schema**, reached through the existing per-tenant connection path (`LruPooledTenantConnectionResolver`, `TenantDbContext` conventions, Epic 28). A tenant's embeddings, chunks, and feedback are physically inside that tenant's schema — cross-tenant retrieval is impossible at the connection layer, the same guarantee every other tenant-aware table already has. This is the 39-17 "hard tool scoping" requirement applied to knowledge, and it completes the per-tenant triad's fourth leg: config, orchestrator process, channels, **knowledge**.

**Embedding runtime stays.** Ollama (`nomic-embed-text`) remains the local embedding service — the C# indexer calls its HTTP API directly. The provider seam stays pluggable (mirror the TS `EmbeddingService` abstraction; a mock provider for tests).

## Acceptance Criteria

1. **C# indexing pipeline.** A `Tamma.Intelligence` component (project placement decided and documented) implements: source discovery (repo files honoring gitignore, git-diff incremental re-index), chunking (generic + language-aware seam; C# chunker welcome, parity with the TS generic chunker is the bar), embedding via a pluggable provider (Ollama HTTP default, mock for tests), and writes to **pgvector tables in the tenant schema** (chunks, embeddings, metadata — additive EF migration, `has-pending-model-changes` clean). Index triggers: manual (API), scheduled, and on-event (see AC5). Every index run emits `INDEX.*` DCB events (started/completed/failed, counts, tenant-tagged).

2. **C# retrieval pipeline.** Query → multi-source retrieval (vector + keyword full-text as the launch set; docs/issues sources behind the same `ISource` seam) → fusion ranking (RRF/weighted + MMR + recency, config-driven) → token-budgeted context assembly (markdown/XML) with the config surface preserved (weights, topK, mmrLambda, recencyBoost, maxTokens, cache TTL). Config is per-tenant (stored with the tenant, admin-editable, prompt-store RBAC pattern). Retrieval never crosses the tenant connection — an isolation test drives two tenants with disjoint corpora and asserts zero cross-tenant hits.

3. **Knowledge tools in the 39-17 agent toolset.** `kb_search` (raw retrieval) and `rag_query` (assembled context) register in the orchestrator agent's closed tool registry, scoped to the agent's tenant like every other tool; invocations emit the standard `ORCHESTRATOR.TOOL_INVOKED` audit events. Chat answers and acceptance/routing decisions can ground on them; the per-user attenuation rule (39-19) applies — a chat-serving retrieval is additionally filtered to repos the asking user can access (39-20 resolver) where source-level access applies.

4. **Optional PRODUCE-stage grounding.** A lifecycle binding (39-6) can opt into retrieval grounding: a server-side resolved `{{retrievedContext}}` variable (the `{{conventions}}` seam in `LlmCallWorkflow`) filled from `rag_query` for the work item — off by default, enabled per document type in configuration. The prompt contract stays byte-stable when disabled; a test pins that enabling it only adds the variable's content.

5. **Accepted documents feed the index.** `DOCUMENT.ACCEPTED` (39-6/39-11) triggers indexing of the accepted document into the tenant's knowledge — the 39-11 store becomes a retrieval source, which is Story 32-11's "learning into RAG" loop meeting Epic 39 from the other side (32-11 is updated to reference this story as its indexing substrate rather than duplicating it).

6. **API surface preserved, sidecar retired.** The `/api/kb/*` route shape keeps working for the dashboards — served natively by the C# implementation instead of proxying (`KbEndpoints` handlers re-pointed; `IIntelligenceHttpClient` and the `intelligence-server` + `chromadb` compose services retired once parity is demonstrated; `ollama` stays). Route-level tests assert response-shape compatibility for the routes the dashboards actually consume; any deliberately dropped route is documented. The TS `packages/intelligence`/`intelligence-server` move to the legacy tier (README stack note updated).

7. **Two scoping models.** Single-user mode: the sole user's schema holds their knowledge, all tools scoped to it — zero special-casing. SaaS: per-tenant as above. Both proven by the AC2 isolation test matrix.

## Technical Notes

- **pgvector over ChromaDB**: Postgres 17 is already the platform store and schema-per-tenant already solves isolation; dropping the ChromaDB container also frees its 1G limit on the 16GB VPS. Record the decision (and the retirement of the five-provider abstraction down to pgvector + a seam) as an ADR in `.dev/decisions/`.
- Embedding dimension/model changes rebuild the index (no migration anxiety) — but stamp the model+dimension on every embedding row so a mismatch fails loud instead of returning garbage neighbors.
- Keyword source: Postgres full-text (`tsvector`) in the same tenant schema — no extra infrastructure.
- Indexing is background work: run it on the platform-task/worker seam (respecting the one-task-per-process caveat noted in CLAUDE.md), never inline in a request; large index runs are resumable (per-file hash skip, the TS hash-calculator precedent).
- The relevance-feedback loop (TS `feedback.ts`) ports as a thin table + API now, feeding ranking later — do not grow this story's scope into learning policy (that remains 32-11's concern).
- Per-user filtering inside a tenant (AC3) is a filter over retrieval results by repo provenance metadata — index rows carry `repoId` so the 39-20 scope applies as a WHERE clause, not a post-hoc LLM instruction.

## Dependencies

- **Prerequisite (in place):** schema-per-tenant + `LruPooledTenantConnectionResolver` (Epic 28), Ollama service, `/api/kb/*` route shape + dashboards, the TS pipeline as the design reference.
- **Prerequisite:** 39-17 (tool registry to mount `kb_search`/`rag_query` — lockstep), 39-11 (document store as source + the `DOCUMENT.ACCEPTED` hook), 39-20 (per-user repo scope for chat-serving retrieval).
- **Coordinates with:** Story 32-11 (learning persistence — this story is its indexing substrate), 32-22 (response cache — separate concern, keep apart).
- **Feeds:** 39-19 (grounded chat answers), 39-6 (PRODUCE grounding), dashboards (KB screens keep working).

## Estimated Effort

8–10 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-21 | 1.0.0   | Initial story creation — C# port of the TS RAG pipeline with per-tenant (schema/pgvector) isolation, agent knowledge tools, PRODUCE grounding, accepted-document indexing, sidecar retirement | Claude |
