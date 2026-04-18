# Finding 013: `RagManagementService.query` returns blank answers; all sources `enabled: false`

**Scope**: kb
**Severity**: P2 (RAG unusable in production; honest-but-empty)
**Status**: Not-yet-implemented (RAG pipeline never constructed; default config disables all sources)
**Estimated port effort**: 2-3h (depends on #001 + composition)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/knowledge-base/RAGManagementService.ts`.

The deleted TS `RAGManagementService` used the same null-fallback pattern:

```typescript
// packages/api/src/services/knowledge-base/RAGManagementService.ts (9e9a57c~1)
const DEFAULT_RAG_CONFIG: RAGConfigInfo = {
  sources: {
    vectorDb: { enabled: false, weight: 0, topK: 0 },
    keyword: { enabled: false, weight: 0, topK: 0 },
    docs: { enabled: false, weight: 0, topK: 0 },
    issues: { enabled: false, weight: 0, topK: 0 },
  },
  ranking: { fusionMethod: 'rrf', mmrLambda: 0.7, recencyBoost: 0.1 },
  assembly: { maxTokens: 4000, format: 'xml', includeScores: false },
  caching: { enabled: true, ttlSeconds: 300, maxEntries: 1000 },
};

export class RAGManagementService {
  private readonly pipeline: IRAGPipeline | null;
  private config: RAGConfigInfo;
  // ...
  constructor(pipeline?: IRAGPipeline) {
    this.pipeline = pipeline ?? null;
    this.config = { ...DEFAULT_RAG_CONFIG };
  }
}
```

Both versions default every RAG source to `enabled: false`. This is a **safety default** — if no operator has explicitly enabled a source, the retriever won't silently try to hit a broken backend.

- Dependencies: `IRAGPipeline` from `@tamma/intelligence/rag/`.
- Tests: `packages/api/src/__tests__/services/knowledge-base/RAGManagementService.test.ts`.

## 2. What's in C#

### C# side
Four endpoints (get config, update config, query, metrics) — all forwarded:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs:93-113 (current)
public static async Task<IResult> GetRagConfig( /* ... */ ) => /* ... */;
public static async Task<IResult> UpdateRagConfig( /* ... */ ) => /* ... */;
public static async Task<IResult> QueryRag( /* ... */ ) => /* ... */;
public static async Task<IResult> GetRagMetrics( /* ... */ ) => /* ... */;
```

### Sidecar side

```typescript
// packages/intelligence-server/src/services/RagManagementService.ts:108-137 (current)
async query(req: RagQueryRequest): Promise<RagQueryResponse> {
  if (!this.pipeline) {
    return { answer: '', sources: [], queryId: '', latencyMs: 0 };
  }
  const start = Date.now();
  this.queryCount++;
  const retrieveQuery: { text: string; maxResults?: number; sources?: string[] } = {
    text: req.query,
  };
  if (req.topK !== undefined) retrieveQuery.maxResults = req.topK;
  if (req.sources) retrieveQuery.sources = req.sources;
  const options: Record<string, unknown> = {};
  if (req.maxTokens !== undefined) options.maxTokens = req.maxTokens;

  const result = await this.pipeline.retrieve(retrieveQuery, options);
  const latencyMs = result.latencyMs ?? Date.now() - start;
  this.totalLatencyMs += latencyMs;

  return {
    answer: result.retrievedChunks.map((c) => c.content).join('\n\n'),
    sources: result.retrievedChunks.map((c) => ({ /* ... */ })),
    queryId: result.queryId,
    latencyMs,
  };
}
```

On null pipeline:
```json
{ "answer": "", "sources": [], "queryId": "", "latencyMs": 0 }
```

- Dependencies: `IRagPipeline` (narrow). Null in production.
- Tests: sidecar unit test asserts the empty response is returned.

Constructor sets `enabled: Boolean(pipeline)`:

```typescript
// packages/intelligence-server/src/services/RagManagementService.ts:80-83 (current)
constructor(pipeline?: IRagPipeline) {
  this.pipeline = pipeline ?? null;
  this.config = { ...DEFAULT_CONFIG, enabled: Boolean(pipeline) };
}
```

So `GET /kb/rag/config` always returns `enabled: false` in production — a slightly better signal than "blank answer with no error".

## 3. The gap

- TS did: same null-fallback; blank answer with no error on null pipeline.
- C# + sidecar does: same. Additionally, `sources.vectorDb.enabled: false` is visible on `GET /kb/rag/config` — minimal signal.

For an orchestrator calling `POST /api/kb/rag/query` with `{ "query": "how do I configure retry logic?" }`:
- Both: HTTP 200 with `{ "answer": "", "sources": [] }`.
- Orchestrator: cannot distinguish "no relevant context found" from "RAG pipeline not running".

For a dashboard user:
- Clicks "Test RAG query" with a real question. Sees empty answer. No error.
- Navigates to config. Sees `enabled: false` for all sources. Understands *something* is off, but the UI doesn't say "backend not wired" — it says "vectorDb.enabled: false" which is a config field, not a wiring status.

Error paths:
- TS + C# + sidecar: HTTP 200, empty payload.

The `queryId: ""` is the strongest honesty signal — a real pipeline always assigns a UUID. Empty string = uninitialized.

## 4. Gap from stories

`docs/stories/epic-6/story-6-3/6-3-rag-pipeline.md` covers the RAG pipeline. Relevant AC:

> - Multi-source retrieval (vector DB, keyword, docs, issues)
> - Reciprocal Rank Fusion across sources
> - Context assembly with token budget
> - Query caching with TTL

All ACs are implementable in `packages/intelligence/src/rag/rag-pipeline.ts` (real impl exists) — the sidecar just never constructs it.

Story alignment:
- [x] Matches TS behavior (both fall short)
- [x] Matches C# behavior (same)
- [ ] Describes a third behavior
- [x] Partial — story exists; impl partly in `packages/intelligence` but not wired.

## 5. Status

- **Classification**: Not-yet-implemented (composition-root gap specific to RAG).
- **What's needed to finish**:
  1. `adapters.ts` `createRagPipelineFromEnv()` — constructs `RAGPipeline` from `packages/intelligence/src/rag/rag-pipeline.ts` with a real retriever backed by `createVectorStoreFromEnv()`.
  2. Pass to bundle.
  3. On null pipeline, throw instead of returning blank response — caller needs clear signal.
  4. Enable default sources when a pipeline is present: if `pipeline` provided, seed `sources.vectorDb.enabled: true` (or whatever matches the wired backend).
  5. Emit structured log on empty retrieval (helps distinguish "no docs indexed" from "query is bad").
- **Is it "just a stub" or is scope missing?** Implementation gap; story 6-3 is clear.
- **Blockers**:
  - #001, #003, #004, #014 — same composition-root chain.
  - Decision: when sidecar restarts, should persisted config override defaults? (Requires persistence layer — #012-style.)

## Remediation

- Files to modify:
  - `packages/intelligence-server/src/adapters.ts` — add `createRagPipelineFromEnv()`.
  - `packages/intelligence-server/src/services/RagManagementService.ts:108-111` — throw on null instead of blank-return.
  - `packages/intelligence-server/src/services/RagManagementService.ts:82` — richer defaults when pipeline is present.
- Files to create: none.
- Tests to add:
  - `POST /kb/rag/query` with real pipeline + seeded vector store returns non-empty `answer` and `sources.length > 0`.
  - `POST /kb/rag/query` with null pipeline → HTTP 503 `{ "error": "KB feature unavailable: no RAG pipeline configured" }` (change from 200).
  - `GET /kb/rag/config` after startup with pipeline → `enabled: true` and at least `sources.vectorDb.enabled: true`.
- Estimated effort: 2-3h
  - Factory + composition: 1h
  - Service behavior change: 30m
  - Tests: 1-1.5h

## References

- TS source: `packages/api/src/services/knowledge-base/RAGManagementService.ts` (commit `9e9a57c~1`)
- Sidecar source: `packages/intelligence-server/src/services/RagManagementService.ts`
- Real impl: `packages/intelligence/src/rag/rag-pipeline.ts`
- Story: `docs/stories/epic-6/story-6-3/6-3-rag-pipeline.md`
- Related findings: #001, #002, #004, #014
