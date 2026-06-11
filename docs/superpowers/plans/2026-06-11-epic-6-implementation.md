# Epic 6 Implementation Plan — REFRESHED 2026-06-11

**Supersedes**: `~/.claude/plans/epic-6-implementation.md` (written pre-Epic-19; its Phase 1 file targets no longer exist)
**Verified against**: main @ `98cfb1c2` (post wave-b tenancy merge)
**Epic docs**: `docs/stories/epic-6/README.md` (10 stories + drafted 6-11)
**Companion audit**: `docs/audit/port-gaps/kb/index.md` (16 findings, all deferred to "a dedicated TypeScript work item" — this plan IS that work item)

---

## 1. What Changed Since the Old Plan (Delta from previous plan)

| Old plan claim | Current reality (verified 2026-06-10) |
|---|---|
| 6 mock KB services live in `packages/api/src/services/knowledge-base/` | **STALE — `packages/api` was deleted** (Epic 19 Phase 3, commit `9e9a57cc`). The 6 services were ported to a new Fastify sidecar: `packages/intelligence-server/src/services/`. |
| Services return hardcoded fake data (`vectorCount: 12450`) | **STALE — no hardcoded fakes remain.** Every service now wraps a narrow adapter interface and falls back to zeros/empty/`"(stub — no X configured)"` strings when the dependency is `null`. The string `12450` no longer exists anywhere in the repo. |
| Old Phase 1: "rewrite 6 mock services to take DI deps" | **ALREADY DONE structurally.** Services accept optional deps (`IntelligenceServicesBundle`), 32 route tests pass. **The remaining gap moved up a level: no composition root ever constructs the real deps.** `startServer()` at `server.ts:~210` builds an empty bundle; `adapters.ts` factories are never called from source; `CHROMADB_URL` (set in docker-compose) is read nowhere in the sidecar. |
| Old 1.7: update `createKBServices()` in `packages/api/src/routes/...` + `serve.ts` | **STALE — files gone.** Replacement: the C# API (`apps/tamma-elsa/src/Tamma.Api`) forwards all 30 `/api/kb/*` routes (Program.cs:1907-1943, behind `SettingsView`/`SettingsManage` policies) via `IntelligenceHttpClient` → sidecar `/kb/*`. C# contract layer is verified fine ("Not-a-gap" per audit). |
| Old Phase 4: pgvector migration in `database/migrations/004_pgvector.sql` | **STALE — `database/migrations/` no longer exists** (SQL files moved to `database/archived-sql-migrations/`; control-plane schema is now owned by EF Core in `apps/tamma-elsa/src/Tamma.Data`). `docker/init-db.sql` creates `uuid-ossp` only — **no `vector` extension**. Deployment uses ChromaDB 1.5.8 container as the vector backend; pgvector is optional. |
| "Only TS/JS chunker, no Python/Go" (6-1) | **PARTIALLY STALE**: `generic-chunker.ts` now exists as a language-agnostic fallback alongside `typescript-chunker.ts`. A dedicated Python/Go AST chunker still does not exist. |
| WebSearchSource is empty placeholder (6-5) | **STILL TRUE** — 21 lines, returns nothing. |
| Scrum Master `generatePlan()` is stub (6-10) | **STILL TRUE** — mock plan at `packages/scrum-master/src/scrum-master-service.ts:811`. |
| Old 3.5: create `violations/recorder.ts`, `violations/alerter.ts` | **DONE** — `packages/gates/src/violations/{violation-recorder,violation-alerter}.ts` exist with tests. Permission *persistence* (PG store) still missing. |
| KB/cost/permission stores are in-memory only | **STILL TRUE** — `knowledge-base/stores/` has only `in-memory-store.ts`; `cost-monitor/src/storage/` has in-memory + file (sync I/O) only; gates has no PG store. |
| Qdrant/Pinecone/Weaviate are stubs | **STILL TRUE** — each is a 78-line stub that throws "only chromadb and pgvector are production-ready". |
| (not in old plan) | **NEW BLOCKER**: `@tamma/intelligence` has **153 strict-mode TS errors** across 34 files (`npx tsc --noEmit`). The committed `dist/` (Mar 19) + stale `.tsbuildinfo` mask this — `tsc --build` reports "up to date". The sidecar deliberately avoids compile-time imports of `@tamma/intelligence` because of this (see `adapters.ts` header comment). Audit finding #014. |
| (not in old plan) | **NEW**: dashboard ↔ contract drift. `packages/dashboard/src/services/knowledge-base/api-client.ts` calls ~12 routes **not in** the 30-route C#/sidecar contract: `/kb/index/cancel`, `/kb/index/history`, `/kb/analytics/quality`, `/kb/context/test`, `/kb/rag/test`, `/kb/mcp/servers/{name}/restart`, `/kb/mcp/servers/{name}/logs`, `/kb/mcp/servers/{name}/tools` (contract: `/kb/mcp/tools?serverName=`), `/kb/vector-db/storage`, `/kb/vector-db/collections/{name}/stats`, `POST/DELETE /kb/vector-db/collections...`. Those dashboard features 404 today. |
| (not in old plan) | **NEW**: Story 6-11 (Context API Wiring) drafted at `docs/stories/epic-6/story-6-11/6-11-context-api-wiring.md` — but it targets the deleted `packages/api/src/routes/`; needs re-targeting before execution. |
| (not in old plan) | `adapters.ts` only covers vectorStore + ragPipeline. **No adapters exist** for indexer, MCP client, context aggregator, or cost tracker — and the sidecar's `IIndexer` interface doesn't match `CodebaseIndexer` (`getIndexStatus()` vs `getIndexStatus(projectPath)`; `indexProject(path, {fullReindex})` vs `indexProject(path)`). |
| (not in old plan) | `@tamma/mcp-client` and `@tamma/cost-monitor` are **not dependencies** of `@tamma/intelligence-server` (package.json has only `@tamma/intelligence`, `@tamma/shared`, fastify, pino, dotenv). Wiring MCP/Analytics requires adding workspace deps. |

**Net effect**: the epic's center of gravity moved from "rewrite 6 mock services" (done) to **"build the composition root + fix the strict-mode debt that blocks it"**, exactly as the KB audit's remediation order says.

---

## 2. Current-State Table — the 6 KB Services

All live in `packages/intelligence-server/src/services/` (Fastify sidecar, port 4100, deployed in `docker/docker-compose.yml` as `intelligence-server`, health-checked, consumed by C# API via `IntelligenceServer__Url`). All 6 are constructed dep-less in production today.

| # | Service (file) | What it does today without deps | Real implementation it must wire to | Adapter status |
|---|---|---|---|---|
| 1 | `IndexManagementService.ts` (6 routes: status/trigger/config GET+PUT/stats/clear) | `getStatus()` → idle/0; `triggerIndex()` → `"Indexing triggered (stub — no indexer configured)"`; stats → zeros | `CodebaseIndexer` (`packages/intelligence/src/indexer/codebase-indexer.ts`, `indexProject(projectPath): Promise<IndexResult>`, `getIndexStatus(projectPath)`, `updateIndex(...)`, `stop()`) + `EmbeddingService` (openai/ollama/cohere/mock providers) | **Missing** — needs `adaptIndexer()`; signature mismatches (projectPath arg, no options bag) |
| 2 | `VectorDbManagementService.ts` (6 routes: status/search/upsert/delete/collections/stats) | status → `not_configured`; search → `[]`; upsert/delete → `"(stub — no store configured)"`; stats → zeros | `createChromaDBStore()` / `createPgVectorStore()` from `packages/intelligence/src/vector-store/factory.ts` (ChromaDB 834-line impl, pgvector 954-line impl) + embed function for text queries | **Exists** — `adaptVectorStore(real, embedText)` in `adapters.ts`, never called |
| 3 | `RagManagementService.ts` (4 routes: config GET+PUT/query/metrics) | query → blank answer, empty sources; metrics → zeros | `RAGPipeline` (`packages/intelligence/src/rag/rag-pipeline.ts`, `retrieve(query: RAGQuery): Promise<RAGResult>`) with vector/keyword/docs/github sources | **Exists** — `adaptRagPipeline(real)` in `adapters.ts`, never called |
| 4 | `McpManagementService.ts` (8 routes: servers list/get/start/stop, config GET+PUT, tools list/invoke) | servers → `[]`; start/stop → stub strings; `invokeTool` → always errors | `MCPClient` (`packages/mcp-client/src/client.ts` — `listServers(): ServerInfo[]`, `listTools(serverName?)`, connect/disconnect via connection pool, audit logging) | **Missing** — needs `adaptMcpClient()` AND `@tamma/mcp-client` added to sidecar package.json |
| 5 | `ContextTestingService.ts` (3 routes: history/feedback/config) | history → in-process array (lost on restart); feedback → in-process Map; `runQuery()` helper exists but no aggregator | `ContextAggregator` (`packages/intelligence/src/context/aggregator.ts`, `getContext(request: ContextRequest): Promise<ContextResponse>`; sources: VectorDB/RAG/MCP/WebSearch) | **Missing** — needs `adaptContextAggregator()` (shape is close) |
| 6 | `AnalyticsService.ts` (3 routes: analytics/usage/costs) | all three → zeros / empty arrays | `CostTracker` (`packages/cost-monitor/src/cost-tracker.ts`, `createCostTracker`) + storage (currently in-memory/file only) | **Missing** — needs `adaptCostTracker()` AND `@tamma/cost-monitor` added to sidecar package.json; also needs a usage feed (nothing currently records LLM usage into cost-monitor in the deployed C# path) |

**Request flow**: Dashboard (`VITE_API_BASE_URL`, default `/api`) → C# `Tamma.Api` `/api/kb/*` (auth: `SettingsView` read / `SettingsManage` write; 10s timeout; degraded-payload fallback on sidecar 5xx) → sidecar `/kb/*` → service → (today: null dep → zero state).

---

## 3. Phased Task Breakdown

Conventions for every task: test-first (Vitest 3/4, colocated `*.test.ts`), ESM imports with `.js` extension, strict TS with `exactOptionalPropertyTypes` (use conditional assignment for optional props, never assign `undefined`), no `.then()/.catch()`, structured pino logging, no secrets in logs.

### Phase 0 — Unblock: `@tamma/intelligence` strict-mode debt (audit #014) — P0

The composition root cannot import `@tamma/intelligence` types/classes cleanly until this lands. 153 errors, top files: `typescript-chunker.ts` (28), `generic-chunker.ts` (15), `codebase-indexer.ts` (13), `embedding-service.ts` (9), `knowledge-service.ts` (7), `token-counter.ts` (7), `chromadb.ts` (6), `rag/ranker.ts` (6), `rag/assembler.ts` (6), `index.ts` (6), remainder ≤5 each.

| Task | Files | Tests |
|---|---|---|
| 0.1 Fix indexer module errors (chunking, discovery, embedding, metadata, triggers, codebase-indexer, config) | `packages/intelligence/src/indexer/**` | Existing `__tests__` stay green; add cases only where a fix changes behavior |
| 0.2 Fix rag + vector-store + knowledge-base + context errors | `packages/intelligence/src/{rag,vector-store,knowledge-base}/**`, `src/index.ts` | Same |
| 0.3 Clean-build gate: `rm -rf dist .tsbuildinfo && pnpm --filter @tamma/intelligence build` green; add `typecheck` to CI path for this package so the stale-`.tsbuildinfo` masking can't recur | `packages/intelligence/tsconfig.json`, CI workflow if needed | `npx tsc --noEmit` → 0 errors |
| 0.4 Triage the ~15 pre-existing failing tests across gates/intelligence/scrum-master/mcp-client (noted in `docs/architecture/mvp-status.md:34`) — fix or explicitly quarantine with linked issues | varies | Full Epic-6-package suites green |

Known gotchas (from project memory): `exactOptionalPropertyTypes` → `if (val !== undefined) obj.prop = val;`; `noUncheckedIndexedAccess` → indexed access is `T | undefined`; chromadb client `Where` type needs proper narrowing, not `Record<string, unknown>`.

### Phase 1 — Composition root: real backends in the sidecar (audit #001/#003/#004/#005) — P0

| Task | Files | Tests |
|---|---|---|
| 1.1 Env config loader: `CHROMADB_URL`, `VECTOR_STORE_PROVIDER` (chromadb\|pgvector), `PGVECTOR_URL`, `EMBEDDING_PROVIDER` (openai\|ollama\|cohere\|mock), `OPENAI_API_KEY`/`OLLAMA_URL`, `INDEX_PROJECT_PATH`, `MCP_CONFIG_PATH` | NEW `packages/intelligence-server/src/config.ts` | NEW `src/config.test.ts` — env permutations, missing-var behavior (unset ⇒ dep stays null, never throw at boot) |
| 1.2 Missing adapters: `adaptIndexer` (bind projectPath; map `getIndexStatus(path)` → no-arg; map `IndexResult` → status shape), `adaptMcpClient` (map `ServerInfo`/connection-pool ops), `adaptContextAggregator`, `adaptCostTracker` | `packages/intelligence-server/src/adapters.ts` (extend) | NEW `src/adapters.test.ts` — each adapter against a fake "real" impl; signature-mismatch regression cases |
| 1.3 Add workspace deps `@tamma/mcp-client`, `@tamma/cost-monitor` to the sidecar; update Dockerfile manifest-copy list (it enumerates package.json files per workspace pkg) | `packages/intelligence-server/package.json`, `packages/intelligence-server/Dockerfile`, `pnpm-lock.yaml` | Docker build in CI |
| 1.4 `buildBundleFromEnv(): Promise<IntelligenceServicesBundle>` composition root; call it from `startServer()` when no explicit bundle is passed | NEW `packages/intelligence-server/src/composition.ts`; `src/server.ts` entry block | NEW `src/composition.test.ts` (mock the factory imports); existing `routes.test.ts` (32) stays green via explicit-bundle injection |
| 1.5 Kill user-visible `"(stub — …)"` strings (audit #002/#006-#010): when a dep is genuinely unconfigured return explicit `503 { error: 'not_configured', dependency: '...' }` (the C# client already renders degraded payloads on 5xx; the deleted TS API threw — never silently fake success) | all 6 service files + `server.ts` error mapping | Update `routes.test.ts` expectations; per-service `*.test.ts` for the 503 path |
| 1.6 Compose/env wiring: add `OPENAI_API_KEY` (or `OLLAMA_URL`) + `VECTOR_STORE_PROVIDER` to the `intelligence-server` service; mount/decide the indexing target (see Risk R3); document in `.env.example` | `docker/docker-compose.yml`, `docker/docker-compose.prod.yml`, env examples | `docker/post-deploy-tests.sh` addition: `GET /kb/vector-db/status` returns `ready` |
| 1.7 One live-compose smoke test (audit #016): boot sidecar + chromadb, upsert → search → non-empty result | NEW `packages/intelligence-server/src/__tests__/compose-smoke.integration.test.ts` (gated `INTEGRATION_TEST_ENABLED=true`) | Itself |

Exit criterion: deployed dashboard KB pages show real (initially zero-but-live) data; `vector-db/status` = `ready`; triggering an index populates collections.

### Phase 2 — Contract drift: dashboard ↔ C# ↔ sidecar — P1

| Task | Files | Tests |
|---|---|---|
| 2.1 Decide per-route: extend contract vs trim dashboard. Recommended: ADD `index/history`+`index/cancel`, `mcp/servers/{id}/logs`+`restart`, `context/test` (wire to existing `ContextTestingService.runQuery()`), `rag/test` (alias of `rag/query`), `vector-db/collections` POST/DELETE + `{name}/stats` + `storage`; DROP `analytics/quality` from the dashboard (no backing data) | sidecar `server.ts` + services; C# `KbEndpoints.cs`, `IIntelligenceHttpClient(.cs)`, `IntelligenceHttpClient.cs`, `Program.cs` route map, `KnowledgeBaseDtos.cs`; `packages/dashboard/src/services/knowledge-base/api-client.ts`; `@tamma/shared` KB types | sidecar `routes.test.ts` additions; C# `KbEndpointsIntegrationTests`; dashboard service tests |
| 2.2 Re-target Story 6-11 (engine context wiring): rewrite its "New API Routes (in packages/api/...)" section against the real surfaces (C# Tamma.Api or the sidecar); then implement per the revised story | `docs/stories/epic-6/story-6-11/...` first (doc), then code | per story |

### Phase 3 — Persistence (carried over, re-pathed) — P1

`database/migrations/` is archived; control-plane schema is EF-owned. Sidecar-owned tables need a decision (Task 3.0).

| Task | Files | Tests |
|---|---|---|
| 3.0 Decide schema ownership for sidecar tables (recommended: a small sidecar-owned migration runner reusing `database/migrate.ts` pattern against a dedicated `intelligence` schema; do NOT touch EF control-plane). Note tenancy: sidecar is a single shared instance — tables must carry a tenant key or the KB stays explicitly instance-global (document which; see R6) | ADR in `.dev/decisions/` | n/a |
| 3.1 Knowledge-base PG store (Story 6-9): implement `IKnowledgeStore` over PG | NEW `packages/intelligence/src/knowledge-base/stores/database-store.ts` + migration | NEW colocated `database-store.test.ts` (testcontainer or gated integration) |
| 3.2 Cost-monitor PG store (Story 6-7): async PG store replacing sync FileStore in server contexts; feed it from the C# LLM call path or engine usage events (without a producer, analytics stay zero even when wired) | NEW `packages/cost-monitor/src/storage/pg-store.ts` + migration; producer wiring TBD in 6-11 scope | NEW `pg-store.test.ts` |
| 3.3 Permission PG store (Story 6-8): persist project overrides + approval requests (violations recorder/alerter already exist) | NEW `packages/gates/src/permissions/pg-permission-store.ts` + migration | NEW colocated test |
| 3.4 Context history + feedback persistence (audit #012); index-run history for `index/history` (2.1) | `ContextTestingService`, `IndexManagementService` + small tables | service tests |

### Phase 4 — Quality gaps (carried over, still valid) — P2

| Task | Files | Tests |
|---|---|---|
| 4.1 Python chunker via tree-sitter; route `.py` in `ChunkerFactory` (generic-chunker remains fallback for other langs) | NEW `packages/intelligence/src/indexer/chunking/python-chunker.ts`; `chunker-factory.ts` | NEW colocated test with Python fixtures |
| 4.2 WebSearchSource real impl (Brave/SerpAPI/Tavily; key via env; **WebSearch latest docs before picking SDK/flags**) | `packages/intelligence/src/context/sources/web-search-source.ts` (currently 21 lines) | Colocated test w/ MSW mock |
| 4.3 Learning summarizer (Story 6-9) — LLM summarization of task outcomes | NEW `packages/intelligence/src/knowledge-base/capture/learning-summarizer.ts` | Colocated test, mocked provider |
| 4.4 Scrum Master `generatePlan()` → real LLM via engine pool (Story 6-10); blocked on engine execute-task surface (Story 6-11 / Phase 2.2) | `packages/scrum-master/src/scrum-master-service.ts:811` | Existing suite + new plan-generation tests |
| 4.5 MCP tool invocation end-to-end through sidecar (audit #010) incl. server config discovery (`MCP_CONFIG_PATH`) | `McpManagementService`, composition root | Integration test against a local stdio MCP fixture |

---

## 4. Risks

- **R1 — Strict-mode debt is the long pole.** 153 errors / 34 files; some fixes (chromadb `Where`, chunker index-access) may alter behavior. Mitigation: Phase 0 task split per module, suite green after each, no `as any` escapes.
- **R2 — Stale-build masking.** Committed `dist/` (Mar 19) + `.tsbuildinfo` make `tsc --build` report "up to date" while `--noEmit` fails. Anything "working" today may be running months-old JS. Mitigation: Phase 0.3 clean-build gate in CI.
- **R3 — Indexer filesystem access.** The sidecar container has no repo checkout; `CodebaseIndexer.indexProject(path)` needs real files. Options: mount a volume, accept `repositoryPath` per request against a workspace dir the engine clones, or run indexing in the engine and only query via sidecar. Decide in 1.6 (ADR-worthy).
- **R4 — Analytics has no producer.** Wiring `CostTracker` shows zeros until something records LLM usage (the C# API / engine path doesn't feed cost-monitor today). Don't claim Story 6-7 done on Phase 1 alone — needs 3.2 producer wiring.
- **R5 — Triple-surface contract changes.** Phase 2 touches dashboard TS, C# (endpoints+client+DTOs), and sidecar in lockstep; the C# `IIntelligenceHttpClient` is hand-mirrored. Mitigation: one route-set table as source of truth in the PR description; C# integration tests + sidecar route tests updated together.
- **R6 — Tenancy.** KB routes sit behind per-tenant auth in the C# API, but the sidecar (and ChromaDB) is a single shared instance with no tenant scoping — in SaaS mode all tenants would share one index. Per the universal tenancy rule (CLAUDE.md), define both scoping models before exposing writes broadly; near-term mitigation: routes already require `SettingsView`/`SettingsManage`; document instance-global semantics, design tenant-scoped collections (e.g. collection-name prefix `t_<hex>_`) in the 3.0 ADR.
- **R7 — Embedding cost/keys.** Real indexing burns embedding tokens; OPENAI_API_KEY in compose env. Mitigation: default `EMBEDDING_PROVIDER=ollama` option, batch-size caps, mock provider in tests.
- **R8 — ChromaDB client/server version skew.** Compose pins server `chromadb/chroma:1.5.8`; verify the JS client in `@tamma/intelligence` matches its API (v2 heartbeat already needed a healthcheck workaround).

---

## 5. Acceptance Criteria

From `docs/stories/epic-6/README.md` success metrics + audit remediation, restated for this plan:

**Phase 0**
- `pnpm --filter @tamma/intelligence build` from clean (`rm -rf dist .tsbuildinfo`) → 0 errors; `tsc --noEmit` → 0 errors.
- Epic-6 package test suites (intelligence, gates, mcp-client, scrum-master, cost-monitor, intelligence-server) green; previously-failing ~15 tests fixed or quarantined with linked issues.

**Phase 1 (the epic's headline)**
- Sidecar started via `node dist/server.js` with compose env constructs real ChromaDB store + embedder + RAG pipeline (no code change needed to enable).
- No response anywhere contains a literal "stub" string; unconfigured deps → 503 `not_configured`, which the C# client surfaces as its degraded envelope.
- Live smoke: trigger index → `GET /api/kb/index/stats` shows documents/chunks > 0; `POST /api/kb/vector-db/search` returns real scored chunks; `GET /api/kb/vector-db/status` = `ready`.
- Dashboard KB pages render real data with zero-state honesty (0 vectors when nothing indexed — never fabricated numbers).
- All 32 existing sidecar route tests still pass; new config/adapters/composition tests added; one gated live-compose integration test exists (audit #016 closed).

**Phase 2**
- Every route the dashboard `api-client.ts` calls exists in the C# contract AND the sidecar (or is removed from the dashboard); no KB page issues a 404.
- Story 6-11 doc re-targeted; C# activities listed in it no longer call nonexistent endpoints.

**Phase 3**
- KB entries, permissions, cost records, context history survive a sidecar/API restart (PG-backed).
- Cost analytics show real usage once the producer is wired (tracking accuracy target: 100% of routed LLM calls).

**Phase 4**
- `.py` files chunked via AST (not generic fallback); WebSearchSource returns live results behind an env-keyed provider; `generatePlan()` produces an LLM-generated plan; MCP tool invocation works end-to-end through `/api/kb/mcp/tools/invoke`.

**Epic-level metrics (unchanged from epic README)**: context retrieval p95 < 200ms; top-5 relevance > 85%; permission violation rate < 1%; learning capture > 80%.

---

## 6. Verification Sources (for future re-validation)

- Sidecar: `packages/intelligence-server/src/{server,adapters,types}.ts`, `src/services/*.ts`, `src/__tests__/routes.test.ts` (32 tests, pass as of 2026-06-10)
- C# bridge: `apps/tamma-elsa/src/Tamma.Api/Program.cs:1907-1943`, `Endpoints/KbEndpoints.cs`, `Services/KnowledgeBase/IntelligenceHttpClient.cs`, `Extensions/KnowledgeBaseServiceCollectionExtensions.cs`
- Real impls: `packages/intelligence/src/{indexer,vector-store,rag,context,knowledge-base}/`, `packages/mcp-client/src/client.ts`, `packages/cost-monitor/src/`
- Deploy: `docker/docker-compose.yml` (`chromadb` @ 1.5.8, `intelligence-server` @ 4100, `IntelligenceServer__Url`)
- Audit: `docs/audit/port-gaps/kb/` (findings 001-016 + remediation-order rationale)
- Pre-delete TS API snapshot: `git show 9e9a57cc~1:packages/api/src/services/knowledge-base/`
