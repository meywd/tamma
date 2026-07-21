# Implementation Plan — Story 39-21: RAG in C# — per-tenant knowledge isolation and grounding

## Scope & Deliverable

When this story is done, a new `apps/tamma-elsa/src/Tamma.Intelligence/` project is the platform's RAG capability: a C# port of the proven TS pipeline shape (discovery → chunking → Ollama embeddings → pgvector write; query → vector+keyword sources → RRF/MMR/recency fusion → token-budgeted assembly) with all vector/chunk/feedback data in **pgvector tables inside each tenant's `t_<hex>` schema**, reached only through the existing per-tenant connection path. The `/api/kb/*` route shape is served natively (sidecar + ChromaDB retired from compose; `ollama` stays), `kb_search`/`rag_query` back the 39-17 agent tools through one query-service seam, an optional `{{retrievedContext}}` variable grounds PRODUCE turns via the `{{conventions}}` seam in `LlmCallWorkflow` (off by default), and `DOCUMENT.ACCEPTED` documents feed the index through a trigger seam. Every index run emits tenant-tagged `INDEX.*` DCB events. An ADR records pgvector-over-ChromaDB and the retirement of the five-provider vector-store abstraction.

## Pre-Reading

- `docs/stories/epic-39/story-39-21/39-21-rag-in-csharp-per-tenant-knowledge-isolation-and-grounding.md` — the story (ACs are source of truth)
- `docs/stories/epic-39/README.md` — settled principles: per-tenant triad's fourth leg (knowledge), hard tool scoping, llm-call mediation
- `docs/guides/BEFORE_YOU_CODE.md` — mandatory process
- **TS design reference (port, don't re-invent):** `packages/intelligence/src/rag/rag-pipeline.ts` + `rag/types.ts` (the `RAGConfig` surface: sources/ranking/assembly/caching/timeouts — preserve it), `rag/ranker.ts`, `rag/assembler.ts`, `rag/retriever.ts`, `rag/query-processor.ts`, `rag/cache.ts`, `rag/feedback.ts`, `rag/sources/keyword-source.ts` + `vector-source.ts`; `packages/intelligence/src/indexer/codebase-indexer.ts`, `indexer/chunking/generic-chunker.ts` (+ its tests — the parity bar), `indexer/discovery/{file-discovery,gitignore-parser,git-diff-detector}.ts`, `indexer/embedding/{embedding-service,ollama-embedding-provider,mock-embedding-provider}.ts`, `indexer/metadata/hash-calculator.ts`; `packages/intelligence/src/vector-store/providers/pgvector.ts` (SQL shapes to steal); `packages/intelligence-server/src/server.ts` (sidecar route responses)
- **C# proxy being retired:** `apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs` (30 handlers), `Tamma.Api/Services/KnowledgeBase/IIntelligenceHttpClient.cs` + `IntelligenceHttpClient.cs`, `Tamma.Api/Dtos/KnowledgeBase/KnowledgeBaseDtos.cs`, `Tamma.Api/Extensions/KnowledgeBaseServiceCollectionExtensions.cs`, `Tamma.Api/Program.cs` (~L2990 `/api/kb` MapGroup, `SettingsView`/`SettingsManage` policies), `apps/tamma-elsa/tests/Tamma.Api.Tests/KnowledgeBase/{IntelligenceHttpClientTests,KbEndpointsIntegrationTests}.cs`
- **Dashboard consumer:** `packages/dashboard/src/services/knowledge-base/api-client.ts` — the routes the KB screens actually call (note: several, e.g. `/kb/index/cancel`, `/kb/rag/test`, `/kb/analytics/quality`, are *already* unserved by the 30-route proxy — see D9)
- **Tenancy substrate:** `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs`, `TammaModelConfiguration.cs` (prompt_overrides XOR block ~L780-900), `TenantDbContextFactory.cs`, `Abstractions/ITenantDbContextFactory.cs`, `ITenantContext.cs`, `Pooling/{LruPooledTenantConnectionResolver,TenantDatabasePool,TenantNaming,EfTenantDbMigrator,NpgsqlTenantAdminConnection}.cs` (Search Path = tenant schema only; tenant-role migrations CANNOT `CREATE EXTENSION` — drives D2), `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/TenantProvisioningService.cs` (`CreateSchemaAsync` — the admin-credentialed bootstrap point)
- **Background-work seam:** `Tamma.Api/Services/TaskQueue/{ITaskQueue,ITaskHandler,TaskQueueProcessor,DbTaskQueue}.cs` (tenant-scoped queue), `Tamma.Api/Services/PlatformTasks/IPlatformTaskHandler.cs` (failure semantics precedent)
- **Grounding seam:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` (~L207-292: ResolveConventions → MergeConventions → ResolvePrompt), `Tamma.Activities/Context/ResolveConventionsActivity.cs` (the engine→API callback activity to copy, incl. fail-loud/two-exception posture)
- **Events + audit:** `Tamma.Data/Repositories/{IEventRepository,EventRepository}.cs`, `Tamma.Data/Entities/DomainEvent.cs`
- **Sibling plans (lockstep contracts):** `docs/stories/epic-39/story-39-17/implementation-plan.md` (D6 tool names `kb_search`/`rag_query`, `KnowledgeSearchTool` backed by the sidecar path "until 39-21" — this story is the re-point), `story-39-11/implementation-plan.md` (`IDocumentInstanceRepository.SetStatusAsync` — the `DOCUMENT.ACCEPTED` wiring point), `story-39-6/implementation-plan.md` (PRODUCE dispatches `llm-call` with producer variables — where `{{retrievedContext}}` lands), `docs/stories/epic-32/story-32-11/32-11-learning-persistence-and-auto-learning-into-rag.md` (to be re-pointed at this substrate)
- **Test precedents:** `tests/Tamma.Api.Tests/Analytics/TenantAnalyticsIntegrationTests.cs` (two-tenant Testcontainers isolation), `tests/Tamma.Api.Tests/PromptStore/{PromptEndpointsTenantAdminTests,PromptOverridesPrincipalXorMigrationTests}.cs` (RBAC + XOR migration pins)
- **Deployment:** `docker/docker-compose.yml` (`chromadb`, `ollama`, `intelligence-server` services; `IntelligenceServer__Url`/`ChromaDb__Url` env on the API)
- **NOT FOUND:** none — every story-referenced path exists. (39-17/39-11/39-6/39-20 *code* does not exist yet — plan-complete only; handled in Dependencies & Sequencing.)

## Design Decisions

- **D1 — New `Tamma.Intelligence` project (AC1 "placement decided and documented").** `apps/tamma-elsa/src/Tamma.Intelligence/Tamma.Intelligence.csproj`, referencing `Tamma.Core` + `Tamma.Data`; referenced by `Tamma.Api` only (the engine reaches it over the existing engine→API HTTP callbacks, per the Epic 32 pivot — no provider/DB access in `Tamma.Activities`/`Tamma.ElsaServer`). Pipeline classes are Elsa-free and DI-registered from `Tamma.Api`. Recorded with D2/D3 in one ADR: `.dev/decisions/story-39-21-rag-port-design.md` (house naming: `story-28-1-design-calls.md`).
- **D2 — pgvector rides the pool database, not tenant migrations.** `EfTenantDbMigrator` runs migrations AS THE TENANT ROLE (its Phase-2 comment is explicit) — `CREATE EXTENSION` there would 42501. So: `CREATE EXTENSION IF NOT EXISTS vector SCHEMA public` runs once per pool database through the admin connection at the same point `TenantProvisioningService.CreateSchemaAsync` runs (and in test bootstrap). The embedding column type is schema-qualified `public.vector(768)` and similarity SQL uses `OPERATOR(public.<=>)`, because tenant connections carry `Search Path = t_<hex>` only (`TenantDatabasePool` ~L181) and would not resolve unqualified names. NuGet: `Pgvector` + `Pgvector.EntityFrameworkCore` (EF8-compatible line matching `Npgsql.EntityFrameworkCore.PostgreSQL 8.0.0`); `o.UseVector()` added at every `UseNpgsql` site that builds a `TenantDbContext`.
- **D3 — Five-provider vector-store abstraction retires to pgvector + one seam.** No `IVectorStore` port. The seam that stays pluggable is `IRetrievalSource` (the TS `sources/` shape): launch set = `VectorSource` + `KeywordSource` (Postgres `tsvector`, same table, zero new infra); docs/issues sources are future implementations of the same interface (AC2). ChromaDB/Pinecone/Qdrant/Weaviate are not ported — ADR records why (Postgres 17 already the store; schema-per-tenant already the isolation; frees the 1G ChromaDB container).
- **D4 — Embedding provider seam mirrors the TS `EmbeddingService`.** `IEmbeddingProvider` with `OllamaEmbeddingProvider` (HTTP `POST {baseUrl}/api/embed`, model `nomic-embed-text`, 768 dims, batch — the TS provider's contract) and `MockEmbeddingProvider` (deterministic hash-derived vectors for tests). Config section `Intelligence:Embedding` (`Provider`, `Model`, `BaseUrl` default `http://ollama:11434`, `TimeoutSeconds`). Every chunk row stamps `embedding_model` + `embedding_dimensions`; retrieval filters `WHERE embedding_model = @current` and the indexer throws `TammaError INDEX.EMBEDDING.MODEL_MISMATCH` when stamped rows disagree with the configured model — fail loud, never garbage neighbors (story technical note). A dimension/model change is a rebuild (no migration anxiety), documented in the ADR.
- **D5 — Per-tenant RAG config = `rag_configs` row, user_id XOR tenant_id, JSONB payload.** `RagOptions` record mirrors the TS `RAGConfig` exactly (sources{enabled,weight,topK} per source, ranking{fusionMethod,rrfK,mmrLambda,recencyBoost,recencyDecayDays}, assembly{maxTokens,format,includeScores,deduplicationThreshold}, caching{enabled,ttlSeconds,maxEntries}, timeouts) plus `Indexing` (include/exclude patterns, schedule) and `Grounding.EnabledDocumentTypes` (default empty = AC4 off-by-default). Defaults are the TS defaults, drift-pinned. Storage/RBAC copy the prompt_overrides pattern (`TammaModelConfiguration` XOR block); the API surface is the existing `/api/kb/{index,rag}/config` GET/PUT routes, already gated `SettingsView`/`SettingsManage` — single-user resolves by user_id, SaaS by tenant_id (AC7's two scoping models, zero special-casing).
- **D6 — Indexing is a tenant-queue task, never inline.** Handlers on the existing tenant `TaskQueueProcessor` seam (`ITaskHandler`): `kb.index.repo` (full/incremental repo index; payload `{repositoryPath, repoId, fullReindex, changedFiles?}`) and `kb.index.document` (one accepted document). `POST /api/kb/index/trigger` and the scheduled trigger only enqueue. Resumability = per-file `content_hash` skip in `knowledge_indexed_files` (the TS hash-calculator precedent): a crashed run re-enqueued re-scans but only re-embeds changed files. The CLAUDE.md one-task-per-process caveat is inherited and noted in the ADR (index runs serialize behind other tenant tasks — acceptable for background work).
- **D7 — `kb_search`/`rag_query` are one service seam: `IKnowledgeQueryService`.** `SearchAsync` (raw ranked chunks — kb_search) and `QueryAsync` (assembled, token-budgeted context — rag_query). The 39-17 `KnowledgeSearchTool` re-points from the sidecar path to this seam (lockstep; tool names, closed registry, and `ORCHESTRATOR.TOOL_INVOKED` emission are 39-17's `AuditedToolRegistry` — nothing re-implemented here). Per-user attenuation (AC3) is a **parameter, not a dependency**: `KnowledgeQueryScope { Guid TenantId; IReadOnlyCollection<string>? RepoFilter }` — a null filter means agent-as-principal (no source-level restriction); a chat-serving turn passes the asking user's repo set resolved by 39-20. Enforcement is a `WHERE repo_id = ANY(@repos) OR repo_id IS NULL`-class clause over provenance metadata (story technical note: a WHERE clause, not a post-hoc LLM instruction). Repo-less rows (tenant documents) pass the filter; document-level ACLs are explicitly not in scope.
- **D8 — PRODUCE grounding rides the conventions seam byte-for-byte (AC4).** New `ResolveRetrievedContextActivity` (copy `ResolveConventionsActivity`: engine→API HTTP, `X-Tenant-Id`, same two-exception posture but **fail-soft to empty** on retrieval outage — grounding is optional context, unlike conventions which are contract) calling `POST {Engine:CallbackUrl}/api/kb/grounding/resolve` with `{documentTypeKey, queryText}`. `LlmCallWorkflow` gains optional input `groundingKey` (default `""`); the server checks `Grounding.EnabledDocumentTypes` and returns `{enabled, context}`. A `MergeRetrievedContext` SetVariable (clone of `MergeConventions`, same IMP-1 fail-loud JSON posture) **always** writes `variables["retrievedContext"]` — empty string when disabled/empty-key — so a template that declares `{{retrievedContext}}` renders an empty block when off and the rendered prompt is byte-stable (pinned test). Templates that don't declare it are untouched (supplied-but-undeclared variables drop at render — the `ValidationFeedbackHelper` lesson). 39-6 threads `groundingKey: documentTypeKey` on its PRODUCE dispatch when it lands; no 39-6 file is touched here.
- **D9 — Native route parity is honest, not theatrical (AC6).** The 30 mapped routes keep their URL + response shape. Natively implemented: index (6), vector-db (6 — "collection" maps to source-kind views over the tenant tables), rag (4), context (3 — history from `rag_query_log`, feedback into `rag_feedback`, config from `RagOptions`), analytics (3 — counts/durations from `rag_query_log` + index runs; costs report zeros with `"provider":"ollama-local"`, documented). The 8 **MCP routes return static degraded payloads** (empty lists / `{status:"retired"}`) — MCP server management was a sidecar capability, not RAG; the dashboard already tolerates the proxy's sidecar-down degraded shapes (`IntelligenceHttpClient` doc-comment). Dashboard-called routes that the proxy never mapped (`/kb/index/cancel`, `/kb/index/history`, `/kb/rag/test`, `/kb/context/test`, `/kb/analytics/quality`, `/kb/vector-db/storage`, per-collection stats) stay unmapped — pre-existing, listed in the completion notes as deliberately dropped. All of this is the AC6 "documented" list.
- **D10 — `DOCUMENT.ACCEPTED` feeds the index through a trigger seam (AC5).** `IAcceptedDocumentIndexingTrigger.OnDocumentAcceptedAsync(tenantId, documentId, documentTypeKey, issueId, ct)` in `Tamma.Intelligence`; the shipped implementation enqueues `kb.index.document`. The invocation site is 39-11's `DocumentInstanceRepository.SetStatusAsync(accepted)` / its API status endpoint (one line, lockstep — 39-11 is plan-complete, unimplemented; until it lands the trigger is exercised by tests and the manual `POST /api/kb/index/trigger` document path). The handler renders the document body to markdown, chunks, embeds, and writes chunks with `source='document'`, `document_id`, `document_type`, `issue_id` provenance. Story 32-11's story doc is updated to name this substrate (its `IIntelligenceHttpClient` ingestion path is superseded).
- **D11 — Feedback ports as a thin table + API only.** `rag_feedback` rows + the existing `/kb/context/feedback` route; no ranking-weight learning (that is 32-11's concern — story technical note). `rag_query_log` records per-query source counts/durations to back metrics/analytics.
- **Story-vs-canon tensions: none.** Canon's 39-21 line and the story agree verbatim. One judgment call: canon says "optional {{retrievedContext}} PRODUCE grounding (off by default)" keyed "per document type in configuration" — D8's `groundingKey` input is the minimal way for the generic `LlmCallWorkflow` (which knows role/action, not document type) to learn the document type; flagged for review.

## Implementation Steps

1. **CREATE `.dev/decisions/story-39-21-rag-port-design.md`** — D1 placement, D2 extension/schema-qualification strategy, D3 pgvector-over-ChromaDB + provider-abstraction retirement, D4 rebuild-on-model-change, D6 queue caveat, D9 route disposition table.

2. **CREATE `apps/tamma-elsa/src/Tamma.Intelligence/Tamma.Intelligence.csproj`; MODIFY `apps/tamma-elsa/Tamma.sln`, `apps/tamma-elsa/src/Tamma.Api/Tamma.Api.csproj`** (project + reference, beside the `Tamma.Data` reference). Packages: `Pgvector`, `Pgvector.EntityFrameworkCore` (on `Tamma.Data`).

3. **Data layer (D2/D5).** CREATE `Tamma.Data/Entities/{KnowledgeChunk,KnowledgeIndexedFile,KnowledgeIndexRun,RagFeedback,RagQueryLog,RagConfigRow}.cs`; MODIFY `Tamma.Data/TenantDbContext.cs` (DbSets) + `Tamma.Data/TammaModelConfiguration.cs` (new `ConfigureKnowledgeEntities` called from `ConfigureTenantEntities`; `rag_configs` copies the prompt_overrides XOR block — CHECK `ck_rag_configs_principal_xor`, unique `(UserId, TenantId)` NULLS NOT DISTINCT; `KnowledgeChunk.Embedding` is `Pgvector.Vector?` with `HasColumnType("public.vector(768)")`; `Tsv` computed `to_tsvector('english', content)` stored). MODIFY `Tamma.Data/TenantDbContextFactory.cs`, `TenantDesignTimeDbContextFactory.cs`, `Pooling/EfTenantDbMigrator.cs` (`npgsql.UseVector()`). MODIFY `Tamma.Api/Services/Provisioning/TenantProvisioningService.cs` — admin-connection `CREATE EXTENSION IF NOT EXISTS vector SCHEMA public` before `CreateSchemaAsync`'s DDL. Migration `AddKnowledgeBaseTables` (see Data & Migrations; HNSW + GIN indexes via `migrationBuilder.Sql`).

4. **Embedding seam (D4).** CREATE `Tamma.Intelligence/Embeddings/{IEmbeddingProvider,OllamaEmbeddingProvider,MockEmbeddingProvider,EmbeddingOptions}.cs`:

   ```csharp
   public interface IEmbeddingProvider
   {
       string Model { get; }
       int Dimensions { get; }
       Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
   }
   ```

5. **Indexer (AC1).** CREATE `Tamma.Intelligence/Indexing/{FileDiscovery,GitignoreParser,GitDiffDetector,HashCalculator}.cs` (port `indexer/discovery` + `metadata`; git-diff shells `git diff --name-only` like the TS detector), `Chunking/{IChunker,GenericChunker,ChunkerFactory}.cs` (parity with `generic-chunker.ts` is the bar; language-aware chunkers are later `IChunker`s), `Indexing/KnowledgeIndexer.cs` (discover → hash-skip → chunk → embed → upsert `knowledge_chunks` + `knowledge_indexed_files`, one `knowledge_index_runs` row per run, emits `INDEX.*` via `IEventRepository`), `IndexEvents.cs` (constants — see Events).

6. **Index execution (D6).** CREATE `Tamma.Api/Services/KnowledgeBase/{KbIndexRepoTaskHandler,KbIndexDocumentTaskHandler}.cs` (`ITaskHandler`, `TypePrefix "kb.index."` split by exact type; construct the indexer over `ITenantDbContextFactory` for the task's tenant) and `KbIndexScheduler.cs` (options-bound `BackgroundService`, `Enabled` default false — the opt-in posture; enqueues per-tenant `kb.index.repo` on the configured cadence). MODIFY `Tamma.Api/Program.cs` (handler + scheduler DI).

7. **Retrieval pipeline (AC2).** CREATE `Tamma.Intelligence/Retrieval/{IRetrievalSource,VectorSource,KeywordSource}.cs` (VectorSource: raw SQL `ORDER BY embedding OPERATOR(public.<=>) @query::public.vector` + model-stamp WHERE + repo filter; KeywordSource: `websearch_to_tsquery`/`ts_rank`), `RankerService.cs` (RRF k=60 / weighted fusion, MMR over embeddings, recency boost — port `ranker.ts`), `ContextAssembler.cs` (dedup threshold, token budget, markdown/XML — port `assembler.ts`), `QueryCache.cs` (in-memory TTL — port `cache.ts` memory path), `KnowledgeQueryScope.cs`, `IKnowledgeQueryService.cs` + `KnowledgeQueryService.cs`:

   ```csharp
   public interface IKnowledgeQueryService
   {
       Task<KbSearchResult> SearchAsync(KnowledgeQueryScope scope, string query, int? topK,
           IReadOnlyList<string>? sources, CancellationToken ct);           // kb_search
       Task<RagQueryResult> QueryAsync(KnowledgeQueryScope scope, string query, int? maxTokens,
           IReadOnlyList<string>? sources, CancellationToken ct);           // rag_query (assembled)
   }
   ```

   Writes one `rag_query_log` row per query (D11).

8. **Config service (D5).** CREATE `Tamma.Intelligence/Config/{RagOptions,RagOptionsDefaults}.cs` (drift-pinned TS defaults), `Tamma.Data/Repositories/{IRagConfigRepository,RagConfigRepository}.cs` (mirror `ConventionRepository`; XOR principal resolution per `ITammaModeProvider`), `Tamma.Api/Services/KnowledgeBase/RagConfigService.cs` (resolve → merge over defaults).

9. **Native endpoints (AC6).** MODIFY `Tamma.Api/Endpoints/KbEndpoints.cs` — handlers re-pointed from `IIntelligenceHttpClient` to `IKnowledgeQueryService`/`KnowledgeIndexer` status reads/`RagConfigService`/`ITaskQueue` (D9 dispositions; MCP handlers return the static degraded payloads). MODIFY `Tamma.Api/Extensions/KnowledgeBaseServiceCollectionExtensions.cs` (register the native services). DELETE `Tamma.Api/Services/KnowledgeBase/{IIntelligenceHttpClient,IntelligenceHttpClient}.cs` + `tests/.../KnowledgeBase/IntelligenceHttpClientTests.cs`. `Program.cs` route map unchanged (same 30 routes, same policies).

10. **Grounding (AC4, D8).** CREATE `Tamma.Activities/Context/ResolveRetrievedContextActivity.cs` (copy `ResolveConventionsActivity`; fail-soft-to-empty on 5xx/timeout, fail-loud on taxonomy/config faults). MODIFY `Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` — optional `groundingKey` input, `ResolveRetrievedContext` node + `MergeRetrievedContext` SetVariable between `MergeConventions` and `ResolvePrompt`. CREATE `Tamma.Api/Endpoints/KbGroundingEndpoints.cs` (`POST /api/kb/grounding/resolve`, engine-auth like the conventions resolve endpoint) + map in `Program.cs`.

11. **Accepted-document trigger (AC5, D10).** CREATE `Tamma.Intelligence/Indexing/IAcceptedDocumentIndexingTrigger.cs` + `Tamma.Api/Services/KnowledgeBase/QueueingDocumentIndexingTrigger.cs` (enqueue `kb.index.document`). MODIFY `docs/stories/epic-32/story-32-11/32-11-learning-persistence-and-auto-learning-into-rag.md` (indexing-substrate note pointing here). Hand-off note for 39-11: one call in `SetStatusAsync(accepted)`.

12. **Retirement + docs (AC6).** MODIFY `docker/docker-compose.yml` (remove `intelligence-server` + `chromadb` services and the API's `IntelligenceServer__Url`/`ChromaDb__Url` env; keep `ollama`; API gains `Intelligence__Embedding__BaseUrl: http://ollama:11434`), `README.md` + `CLAUDE.md` stack notes (`packages/intelligence`/`intelligence-server` → legacy tier). Only after step-13 parity tests pass.

13. **Tests + checks** (Test Plan) under `apps/tamma-elsa/tests/Tamma.Intelligence.Tests/` (new project, added to sln, house NUnit+FluentAssertions+Moq stack) and `tests/Tamma.Api.Tests/KnowledgeBase/`; run `dotnet ef migrations has-pending-model-changes --context TenantDbContext` (clean) + `dotnet test`.

## Data & Migrations

All tenant-resident (no TenantId columns — isolation is the schema), one additive migration `AddKnowledgeBaseTables` → `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/<utc-stamp>_AddKnowledgeBaseTables.cs` (+ Designer + snapshot), house command `dotnet ef migrations add AddKnowledgeBaseTables --context TenantDbContext --output-dir Migrations/Tenant`:

- `knowledge_chunks`: `id uuid PK`, `source text` (`repo|document|docs`), `repo_id text NULL`, `file_path text NULL`, `document_id uuid NULL`, `document_type text NULL`, `issue_id text NULL`, `content text`, `start_line/end_line int NULL`, `language text NULL`, `symbols text[] NULL`, `content_hash text`, `embedding public.vector(768) NULL`, `embedding_model text`, `embedding_dimensions int`, `tsv tsvector GENERATED STORED`, `created_at/updated_at timestamptz`. Raw-SQL indexes: `USING hnsw (embedding public.vector_cosine_ops)`, `USING gin (tsv)`, btree on `repo_id`, `document_id`.
- `knowledge_indexed_files`: `id`, `repo_id`, `file_path`, `content_hash`, `chunk_count`, `indexed_at`; unique `(repo_id, file_path)`.
- `knowledge_index_runs`: `id`, `kind` (`repo|document`), `status` (`running|completed|failed`), `files_scanned/files_indexed/chunks_written int`, `error text NULL`, `started_at/completed_at`.
- `rag_feedback`: `id`, `query_id`, `chunk_id`, `rating`, `comment NULL`, `created_at`.
- `rag_query_log`: `id`, `query_hash`, `source_counts jsonb`, `duration_ms`, `assembled_tokens int NULL`, `created_at`.
- `rag_configs`: `id`, `user_id uuid NULL`, `tenant_id uuid NULL`, `config_json jsonb`, timestamps; CHECK `ck_rag_configs_principal_xor`; unique `(user_id, tenant_id)` NULLS NOT DISTINCT.

`CREATE EXTENSION vector` is NOT in the migration (D2 — tenant role lacks privilege); it runs via the admin connection at pool-DB bootstrap (step 3). `has-pending-model-changes` clean after scaffold.

## Events

Constants in `apps/tamma-elsa/src/Tamma.Intelligence/Indexing/IndexEvents.cs`, emitted via `IEventRepository.AppendAsync`, tenant-scoped by construction, tags `repoId`/`documentId`/`issueId` when known:

- **Emits:** `INDEX.RUN.STARTED`, `INDEX.RUN.COMPLETED` (data: kind, filesScanned, filesIndexed, chunksWritten, durationMs), `INDEX.RUN.FAILED` (error, counts so far) — one triple per run, repo and document kinds alike (AC1/AC5).
- **Consumes (via other stories' surfaces):** `DOCUMENT.ACCEPTED` (39-6/39-11 — reaches this story through `IAcceptedDocumentIndexingTrigger`, not by stream-polling); `ORCHESTRATOR.TOOL_INVOKED` for `kb_search`/`rag_query` is emitted by 39-17's `AuditedToolRegistry`, not here (AC3).

## Test Plan

- **`GenericChunkerParityTests`** (`Tamma.Intelligence.Tests/Chunking/`) — fixtures ported from `generic-chunker.test.ts`; boundary/overlap/size parity with the TS generic chunker (the AC1 bar). **AC1.**
- **`RankerServiceTests` / `ContextAssemblerTests`** (`Tamma.Intelligence.Tests/Retrieval/`) — RRF (k=60) and weighted fusion orderings, MMR diversity at λ extremes, recency boost decay; assembler token budget, dedup threshold, markdown vs XML output — expectations ported from `ranker.test.ts`/`assembler.test.ts`. **AC2.**
- **`EmbeddingProviderTests`** (`Tamma.Intelligence.Tests/Embeddings/`) — Ollama request/response contract against a fake `HttpMessageHandler` (batch split, error surface); mock provider determinism; **model-stamp mismatch throws `INDEX.EMBEDDING.MODEL_MISMATCH`**. **AC1 (seam + fail-loud stamp).**
- **`KnowledgeIndexerTests`** (`Tamma.Intelligence.Tests/Indexing/`, temp-dir repos + Moq'd `IEventRepository` + mock embeddings) — gitignore honored; unchanged-hash files skipped on re-index; git-diff incremental set indexed; `INDEX.RUN.STARTED/COMPLETED/FAILED` emitted with correct counts and tenant tags. **AC1.**
- **`KbIndexTaskHandlerTests`** (`Tamma.Api.Tests/KnowledgeBase/`) — enqueue-only trigger endpoint; handler failure → retry semantics (`ITaskHandler` contract); document handler renders/chunks/writes provenance columns. **AC1, AC5.**
- **`RagConfigResolutionTests` + `RagConfigMigrationTests`** (`Tamma.Api.Tests/KnowledgeBase/`; migration test on Testcontainers, `PromptOverridesPrincipalXorMigrationTests` style) — defaults drift-pin (exact TS values); user_id-XOR-tenant_id resolution per mode; XOR CHECK + NULLS NOT DISTINCT enforced; PUT gated `SettingsManage` (`PromptEndpointsTenantAdminTests` style). **AC2 (config surface), AC7.**
- **`KnowledgeIsolationIntegrationTests`** (`Tamma.Api.Tests/KnowledgeBase/`, **Testcontainers image `pgvector/pgvector:pg17`**, two tenant schemas — `TenantAnalyticsIntegrationTests` pattern) — disjoint corpora indexed per tenant through the real per-tenant connection path; vector AND keyword retrieval for tenant A returns zero tenant-B rows; single-user variant (sole user's schema, same code path); the AC2/AC7 isolation matrix.
- **`PerUserRepoFilterTests`** (same fixture) — chunks under `repoId` R1/R2 + a document chunk; `KnowledgeQueryScope.RepoFilter=[R1]` returns only R1 + repo-less document rows; null filter returns all. **AC3 (WHERE-clause attenuation).**
- **`KbRouteShapeCompatibilityTests`** (`Tamma.Api.Tests/KnowledgeBase/`, extends `KbEndpointsIntegrationTests`) — for every dashboard-consumed route in `api-client.ts` that the 30-route map serves: native response deserializes into the dashboard's expected TS shape (field-presence assertions); MCP routes return the pinned degraded payloads; the D9 dropped-route list asserted absent (404). **AC6.**
- **`ResolveRetrievedContextActivityTests`** (`Tamma.Activities.Tests/Context/`, beside the conventions activity tests) — enabled type → context body; disabled/empty-key → empty; retrieval 5xx → empty (fail-soft) with WARN. **AC4.**
- **`LlmCallGroundingByteStabilityTests`** (`Tamma.Api.Tests/` prompt-render level) — render a template with and without `{{retrievedContext}}` declared: disabled ⇒ rendered prompt byte-identical to pre-story output; enabled ⇒ diff is exactly the variable's content. **AC4 (the pinned clause).**
- **`AcceptedDocumentIndexingTests`** (`Tamma.Api.Tests/KnowledgeBase/`) — `QueueingDocumentIndexingTrigger` enqueues `kb.index.document` with correct payload; end-to-end on the Testcontainers fixture: trigger → handler → chunk rows with `document_id`/`issue_id` → `SearchAsync` retrieves the accepted document's content. **AC5.**
- **Knowledge-tool re-point** — MODIFY 39-17's `OrchestratorToolCatalogDriftTests`/tool tests *only if 39-17 has merged*: `KnowledgeSearchTool` backed by a Moq'd `IKnowledgeQueryService`, scope carries the agent's tenant, chat turns pass the user's repo filter. Otherwise the seam-level tests above stand and the re-point is the 39-17 lockstep note. **AC3.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — C# indexing pipeline, pgvector-in-tenant-schema, triggers, INDEX.* events | 2, 3, 4, 5, 6 | GenericChunkerParityTests, EmbeddingProviderTests, KnowledgeIndexerTests, KbIndexTaskHandlerTests, migration clean check |
| 2 — retrieval pipeline, config surface preserved + per-tenant, zero cross-tenant hits | 7, 8 | RankerServiceTests, ContextAssemblerTests, RagConfigResolutionTests, KnowledgeIsolationIntegrationTests |
| 3 — kb_search/rag_query in the 39-17 closed toolset, audited, per-user filtered | 7 (D7), 11 hand-off | PerUserRepoFilterTests; 39-17 tool re-point tests (lockstep) |
| 4 — optional {{retrievedContext}} PRODUCE grounding, off by default, byte-stable | 10 (D8) | ResolveRetrievedContextActivityTests, LlmCallGroundingByteStabilityTests |
| 5 — DOCUMENT.ACCEPTED triggers indexing; 32-11 re-pointed | 11 (D10), 6 | AcceptedDocumentIndexingTests; 32-11 doc edit in step 11 |
| 6 — /api/kb/* preserved natively, sidecar retired, drops documented | 9 (D9), 12, 1 | KbRouteShapeCompatibilityTests; ADR + completion-notes route table |
| 7 — two scoping models proven | 3, 8 (D5) | RagConfigResolutionTests (XOR), KnowledgeIsolationIntegrationTests (single-user + SaaS matrix) |

## Dependencies & Sequencing

- **In place, verified:** schema-per-tenant + `LruPooledTenantConnectionResolver`/`ITenantDbContextFactory`, tenant task queue (`ITaskQueue`/`TaskQueueProcessor`), `IEventRepository`, `ollama` compose service, `/api/kb/*` map + dashboards, the TS pipeline as reference. Steps 1–9 have no Epic-39 code dependency and can start immediately.
- **Lockstep — 39-17 (AC3):** tool names/registry/audit live there; this story ships `IKnowledgeQueryService` and the re-point is one constructor change in `KnowledgeSearchTool` whichever story lands second. Until then the agent tools keep the sidecar path 39-17 planned.
- **Lockstep — 39-11 (AC5):** the `SetStatusAsync(accepted)` → `IAcceptedDocumentIndexingTrigger` call is one line on 39-11's repository; this story ships the trigger + handler and tests them directly. Stub in tests: Moq'd trigger + direct enqueue.
- **Lockstep — 39-6 (AC4):** `LlmCallWorkflow.groundingKey` defaults `""` — safe with zero 39-6 changes; 39-6 threads `documentTypeKey` when it lands.
- **Stubbed, not pulled in:** 39-20 (repo scope arrives as `KnowledgeQueryScope.RepoFilter` values — tests use literal repo sets; the resolver is 39-20's), 32-11 (learning policy; only its story doc is touched), 32-22 (response cache — explicitly kept apart; `QueryCache` here is retrieval-level only).
- **Ordering within the story:** 3 (data) → 4/5 (indexer) → 6 → 7 (retrieval) → 8 → 9 (endpoints) → 10/11 → 12 (retirement LAST, gated on parity tests) → 13 throughout.

## Risks & Mitigations

- **pgvector type/operator resolution under `Search Path = t_<hex>`.** The subtlest risk. Mitigation: D2 schema-qualifies everything (`public.vector(768)`, `OPERATOR(public.<=>)`), and `KnowledgeIsolationIntegrationTests` runs against real per-tenant connection strings on the pgvector image — resolution failures surface in CI, not production.
- **Tenant-role privilege for `CREATE EXTENSION`.** Handled by design (D2: admin-connection bootstrap), but existing pool databases need the extension applied once — the step-3 provisioning change must also run idempotently at startup for pool member #1 (central). Covered in the ADR's rollout note.
- **`Pgvector.EntityFrameworkCore` vs `has-pending-model-changes`.** A mis-mapped vector column makes the check dirty forever. Mitigation: scaffold the migration immediately after the entity config, pin with the migration test; fallback documented in the ADR (drop to raw-SQL column + `SqlQueryRaw` reads, keeping EF unaware of the column).
- **Parity theater on 30 routes.** Shapes are `object`-typed today, so "compatible" could rot. Mitigation: `KbRouteShapeCompatibilityTests` asserts against the dashboard client's field expectations (`api-client.ts` interfaces), not against the old proxy.
- **Ollama unavailability stalls indexing/grounding.** Mitigation: indexing is queued + retryable (D6); grounding fails soft to empty (D8); retrieval's vector source degrades to keyword-only when embedding the query fails (logged WARN) — pinned in `KnowledgeQueryService` tests.
- **Scope creep into learning policy / extra sources.** Mitigation: D3/D11 draw the line (two launch sources behind `IRetrievalSource`; feedback is a table, not a loop); 32-11 named as the owner.
- **Sidecar retired before parity proven.** Mitigation: step 12 is explicitly last and gated on the step-13 route tests; compose change is a single revertable commit.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1, 2 | ADR + project scaffold/packages | 0.5 |
| 3 | Entities, model config, UseVector wiring, extension bootstrap, migration | 1.25 |
| 4 | Embedding seam (Ollama + mock + options) | 0.5 |
| 5 | Discovery/chunking/hash + indexer + events | 1.5 |
| 6 | Task handlers + scheduler + DI | 0.5 |
| 7 | Sources, ranker, assembler, cache, query service | 1.5 |
| 8 | Config record/repository/service | 0.5 |
| 9 | Native endpoint re-point + service registration + deletions | 1.0 |
| 10 | Grounding activity + LlmCallWorkflow + engine endpoint | 0.75 |
| 11 | Accepted-document trigger + 32-11 doc note | 0.5 |
| 12 | Compose/README retirement | 0.25 |
| 13 | Test suites (incl. pgvector Testcontainers) + checks | 1.75 |
| **Total** | | **10.5** (story estimate: 8–10 days; the 0.5 overhang is the D2 rollout note — trim by deferring the scheduler in step 6 if needed) |
