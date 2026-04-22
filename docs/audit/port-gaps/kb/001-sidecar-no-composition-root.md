# Finding 001: Sidecar composition root never constructs real backends

**Scope**: kb
**Severity**: P1 (feature broken — root cause for every other KB finding)
**Status**: Incomplete (partial port, missing composition wiring)
**Estimated port effort**: 8-12h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/knowledge-base/index.ts`.

The deleted TS API exposed `createKBServices(deps)` and explicitly documented that **empty state is the fallback when deps are missing**:

```typescript
// packages/api/src/routes/knowledge-base/index.ts (9e9a57c~1)
/** Optional real dependencies that can be injected into service creation */
export interface KBDependencies {
  indexer?: ICodebaseIndexer;
  vectorStore?: IVectorStoreService;
  ragPipeline?: IRAGPipeline;
  contextAggregator?: IContextAggregator;
  mcpClient?: IMCPClientService;
  costTracker?: ICostTracker;
  projectPath?: string;
}

/**
 * Create service instances, optionally wired to real implementations.
 * When a dependency is not provided the service returns empty/zero state.
 */
export function createKBServices(deps: KBDependencies = {}): KBServices {
  return {
    indexService: new IndexManagementService(deps.indexer, deps.projectPath),
    vectorDBService: new VectorDBManagementService(deps.vectorStore),
    ragService: new RAGManagementService(deps.ragPipeline),
    mcpService: new MCPManagementService(deps.mcpClient),
    contextService: new ContextTestingService(deps.contextAggregator),
    analyticsService: new AnalyticsService(deps.costTracker),
  };
}
```

- The deleted API had a matching gap: no composition root ever called `createKBServices` with a non-empty `deps`. The TS API shipped with empty-state fallback in production.
- This means: **the user-visible feature was already broken before Epic 19**. The Epic 19 cut did not make it worse; it simply preserved the broken fallback.

- Dependencies: `@tamma/intelligence` (real `ICodebaseIndexer`, `IVectorStoreService`, `IRAGPipeline`, etc.).
- Tests: the TS unit tests under `packages/api/src/__tests__/routes/knowledge-base/` passed by explicitly injecting fakes into `createKBServices`, so they never covered the production path.

## 2. What's in C#

Current state on `feat/auth-foundation`.

### C# layer (passthrough — correct)

`apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs:24-54` — all 30 endpoints forward `IIntelligenceHttpClient` calls 1-to-1 to the sidecar. No logic is missing on the C# side.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs (current)
public static async Task<IResult> GetIndexStatus(
    [FromServices] IIntelligenceHttpClient client,
    CancellationToken ct)
    => Results.Ok(await client.GetIndexStatusAsync(ct));
```

### Sidecar layer (where the gap lives)

`packages/intelligence-server/src/server.ts:41-50`:

```typescript
// packages/intelligence-server/src/server.ts (current)
function buildServices(bundle?: IntelligenceServicesBundle): RegisteredServices {
  return {
    index: new IndexManagementService(bundle?.indexer),
    vectorDb: new VectorDbManagementService(bundle?.vectorStore),
    rag: new RagManagementService(bundle?.ragPipeline),
    mcp: new McpManagementService(bundle?.mcpClient),
    context: new ContextTestingService(bundle?.contextAggregator),
    analytics: new AnalyticsService(bundle?.costTracker),
  };
}
```

Every service is constructed with `bundle?.x` — so every one receives `undefined` unless the caller of `startServer()` provides a bundle. The entrypoint does not (see finding #005).

- Dependencies: `@tamma/intelligence` (real backends) + `@tamma/mcp-client` + OpenAI SDK for embeddings, all available in node_modules but never imported here.
- Tests: `packages/intelligence-server/src/__tests__/` exercise service classes with object mocks. The sidecar entrypoint path (no-bundle fallback) is never exercised against real ChromaDB.

## 3. The gap

- **TS did**: import `createKBServices()` with no deps → empty-state. Broken in production; tests passed with fakes.
- **C# + sidecar does**: start Fastify with no bundle → sidecar services construct with `null` adapters → every surface returns empty / zero / literal "(stub …)" strings.
- For any caller (dashboard, CLI) hitting `/api/kb/index/status`, `/api/kb/vector-db/stats`, `/api/kb/analytics`, etc., the response is a zero-state JSON payload. For `/api/kb/index/trigger`, `/api/kb/vector-db/upsert`, and `/api/kb/mcp/servers/:id/start`, the response body is literally `{"message":"… (stub — no … configured)"}` (see #006-#009).

Error paths:
- TS: inherited the "empty state on missing dep" contract. Except indexer / MCP, where the TS services threw errors (see #006, #009, #010).
- Sidecar: never throws for missing deps — returns stub strings or zero objects. Strictly less honest than the TS version for the index / MCP / vector-db write paths.

In production:
- Dashboard renders "0 documents indexed, 0 vectors, 0 queries" on every load regardless of actual ChromaDB contents.
- Orchestrator tools calling RAG retrieve empty context and proceed without knowledge-base grounding.
- No monitoring alarm fires because the responses are 200 OK.

## 4. Gap from stories

Epic 6 covers the KB feature set:
- `docs/stories/epic-6/story-6-1/6-1-codebase-indexer.md` — indexer impl, defines `ICodebaseIndexer`.
- `docs/stories/epic-6/story-6-2/6-2-vector-database-integration.md` — ChromaDB as default backend.
- `docs/stories/epic-6/story-6-3/6-3-rag-pipeline.md` — RAG pipeline.
- `docs/stories/epic-6/story-6-4/6-4-mcp-client-integration.md` — MCP client.
- `docs/stories/epic-6/story-6-5/6-5-context-aggregator.md` — context aggregator.

Story 6-2 AC2 explicitly calls for "ChromaDB Integration (Default) … Implement ChromaDB adapter (embedded mode) … Support collection management (create, delete, list) … Handle connection pooling". The real implementation exists in `packages/intelligence/src/vector-store/providers/chromadb.ts` — it is simply never constructed by the sidecar entrypoint.

Story alignment:
- [ ] Matches TS behavior (no, sidecar uses literal stub strings that TS didn't)
- [x] Matches C# behavior (C# → sidecar contract is correct; gap is TS sidecar composition)
- [ ] Describes a third behavior
- [ ] No story — Epic 6 fully spec'd this. The gap is implementation, not specification.

## 5. Status

- **Classification**: Incomplete. All the pieces exist (`packages/intelligence` real impls, `adapters.ts` factories, `IntelligenceServicesBundle` type, `startServer` accepts a bundle arg) — they just aren't wired together at the entrypoint.
- **What's needed to finish**:
  1. Implement `createFromEnv()` or equivalent composition-root function (alluded to in `adapters.ts:14-17` JSDoc) that reads `CHROMADB_URL`, `OPENAI_API_KEY`, `EMBEDDING_MODEL` env vars and constructs real `IVectorStore`, embedder, `RAGPipeline`, `ICodebaseIndexer`, `MCPClient`, `CostTracker`.
  2. Call `adaptVectorStore()` / `adaptRagPipeline()` (exported from `adapters.ts`) to bridge real types to sidecar adapter types.
  3. Pass the constructed bundle to `startServer({ services: bundle })` in the direct-invocation block of `server.ts:210`.
  4. Fix `@tamma/intelligence` strict-mode errors (finding #014) — currently blocks importing real types.
- **Is it "just a stub" or is scope missing?** Scope is fully spec'd (Epic 6), interfaces exist, real backends exist. This is a pure wiring gap.
- **Blockers**:
  - Finding #014 — `@tamma/intelligence` strict-mode build errors. The Dockerfile comment at `packages/intelligence-server/Dockerfile:46-48` says the sidecar deliberately avoids compiling `@tamma/intelligence`; that must be reversed.
  - Finding #003 — no env vars are read anywhere in the sidecar source.

## Remediation

- Files to modify:
  - `packages/intelligence-server/src/server.ts` (add env-driven composition at lines 197-216)
  - `packages/intelligence-server/src/adapters.ts` (implement the `createVectorStoreFromEnv` / `createRagPipelineFromEnv` functions currently only mentioned in comments)
  - `packages/intelligence-server/Dockerfile` (remove strict-mode skip, add `@tamma/intelligence` to build)
  - `packages/intelligence/` strict-mode fixes (scope per #014)
- Files to create: none; all structural pieces exist.
- Tests to add:
  - Unit: `intelligence-server/src/__tests__/composition-root.test.ts` — assert that given stubbed env vars, the bundle contains real wrapped instances (not null).
  - Integration: `intelligence-server/tests/integration/chromadb.test.ts` — spin up ChromaDB via testcontainers, call `/kb/vector-db/upsert` then `/kb/vector-db/search`, assert non-empty results.
- Estimated effort: 8-12h broken down as:
  - Composition root + adapter factory wiring: 3-4h
  - Fix `@tamma/intelligence` strict-mode (see #014): 5-10h (unknown; the Dockerfile comment suggests pre-existing errors)
  - Dockerfile update + smoke test against real compose: 2h
  - Integration test with testcontainers ChromaDB: 2-3h

## References

- TS source: `packages/api/src/routes/knowledge-base/index.ts` (commit `9e9a57c~1`)
- Sidecar entrypoint: `packages/intelligence-server/src/server.ts`
- Sidecar adapters (unused): `packages/intelligence-server/src/adapters.ts`
- Real impls: `packages/intelligence/src/vector-store/`, `packages/intelligence/src/rag/`, `packages/intelligence/src/indexer/`
- Story: `docs/stories/epic-6/story-6-1/` through `story-6-5/`
- Related findings: #003, #004, #005, #014, #015

## Remediation status

**Status (2026-04-18):** Deferred — out of scope for the C# port pass.

The composition-root gap lives entirely in the TypeScript sidecar
(`packages/intelligence-server/src/server.ts`) and depends on the
TypeScript `@tamma/intelligence` package being wired in. The C# port
surface is correctly limited to a pass-through HTTP client
(`apps/tamma-elsa/src/Tamma.Api/Services/KnowledgeBase/IntelligenceHttpClient.cs`)
plus the 30 `KbEndpoints` handlers that forward verbatim and a degraded-payload
fallback envelope when the sidecar is 5xx/unreachable — which the audit explicitly
acknowledges is "fine" (see `index.md` "Not-a-gap"). There is no C# code that
could meaningfully wire ChromaDB, embedders, RAG pipelines, indexers, MCP
clients, or cost trackers; the sidecar owns all of that. Remediation requires
either (a) doing the TypeScript composition-root work in
`packages/intelligence-server/`, or (b) porting the entire intelligence
sidecar stack to C# as a substantive new project. Both are explicitly out of
the scope and time-budget for this port-gap remediation pass.

**To unblock:** open a dedicated TypeScript work item against
`packages/intelligence-server/` covering findings 001/003/004/005/014/015
together (they're a single composition-root chain). Estimated 8-12h per the
finding's own scoping.
