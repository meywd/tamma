# Finding 004: `adapters.ts` factories never called anywhere in shipping code

**Scope**: kb
**Severity**: P1 (dead code blocks the composition root)
**Status**: Incomplete (helpers written, never invoked)
**Estimated port effort**: 1-2h (once #014 clears — actual invocation is trivial)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/knowledge-base/index.ts`.

The deleted TS API did not have a separate adapter layer — its service classes accepted the concrete `ICodebaseIndexer` / `IVectorStoreService` etc. from `@tamma/intelligence` directly. There was no pre-built factory that bridged them to a narrow interface. The lack of such a factory was a design omission in the TS API too.

- Dependencies: none (this is about the absence of adapters).
- Tests: none.

## 2. What's in C#

### C# side
N/A — the C# HTTP client just forwards JSON.

### Sidecar side — `adapters.ts` exports `adaptVectorStore` and `adaptRagPipeline` but nothing imports them

```typescript
// packages/intelligence-server/src/adapters.ts:34-99 (current)
export function adaptVectorStore(
  real: {
    listCollections(): Promise<string[]>;
    createCollection(name: string, opts?: unknown): Promise<void>;
    deleteCollection(name: string): Promise<void>;
    upsert(collection: string, docs: Array<{ id: string; embedding: number[]; /* ... */ }>): Promise<void>;
    delete(collection: string, ids: string[]): Promise<void>;
    search(collection: string, query: { embedding: number[]; topK: number; /* ... */ }): Promise<Array<{ /* ... */ }>>;
    hybridSearch?(collection: string, query: { text: string; embedding: number[]; /* ... */ }): Promise<Array<{ /* ... */ }>>;
    getCollectionStats?(name: string): Promise<{ vectorCount: number; dimensions: number; storageBytes: number }>;
  },
  embedText: (text: string) => Promise<number[]>,
): IVectorStoreAdapter {
  // ... bridges embedding-first query to text-first query
}

export function adaptRagPipeline(
  real: { /* ... */ },
): IRagPipeline {
  // ... pass-through
}
```

The JSDoc at the top of `adapters.ts:11-18` even names the missing factories:

```typescript
// packages/intelligence-server/src/adapters.ts (current)
/**
 * Typical wiring (called from a composition root, e.g. a deploy harness):
 *
 *   const bundle: IntelligenceServicesBundle = {
 *     vectorStore: await createVectorStoreFromEnv(),
 *     ragPipeline: await createRagPipelineFromEnv(),
 *   };
 *   await startServer({ services: bundle });
 */
```

But `createVectorStoreFromEnv` and `createRagPipelineFromEnv` are **not** exported from `adapters.ts`. A grep for either symbol across `packages/intelligence-server/` finds matches only in that JSDoc comment:

```
$ grep -rn 'createVectorStoreFromEnv\|createRagPipelineFromEnv\|createFromEnv' \
    packages/intelligence-server/src/
packages/intelligence-server/src/adapters.ts:14: *     vectorStore: await createVectorStoreFromEnv(),
packages/intelligence-server/src/adapters.ts:15: *     ragPipeline: await createRagPipelineFromEnv(),
```

Likewise for `adaptVectorStore` and `adaptRagPipeline` — they are re-exported from `src/index.ts:10` but not consumed anywhere in runtime code. Tests do not exercise them either.

- Dependencies: the adapters expect objects matching the real `@tamma/intelligence` `IVectorStore` / `IRAGPipeline` interfaces — so calling them requires `@tamma/intelligence` to compile cleanly (blocked by #014).
- Tests: none call `adaptVectorStore` or `adaptRagPipeline`. The sidecar unit tests use plain object mocks that implement `IVectorStoreAdapter` directly.

## 3. The gap

- TS did: no adapter factories — services took concrete types directly. Same root cause: no composition.
- C# + sidecar does: adapter factories written but never invoked. The `adaptVectorStore()` function (with its embedding-first → text-first translation logic) is dead code.

For an operator enabling `@tamma/intelligence` wiring:
- Without `createVectorStoreFromEnv()`: must hand-write the composition code themselves (read env, instantiate `ChromaVectorStore` from `packages/intelligence`, build embedder, wrap with `adaptVectorStore()`, pass to `startServer`). Six services × ~5 lines each = ~30-50 lines of boilerplate that should live once in the sidecar.

Error paths:
- Runtime: no error — the sidecar just starts with null adapters (see #001, #005).
- Build time: `adapters.ts` compiles cleanly (types are locally defined), so TypeScript flags no issue.

## 4. Gap from stories

No story references `adaptVectorStore` or `createVectorStoreFromEnv` — these helpers are an Epic 19 implementation detail. The JSDoc hints they were intended but the implementation was deferred ("called from a composition root, e.g. a deploy harness").

Story alignment:
- [ ] Matches TS behavior
- [ ] Matches C# behavior
- [ ] Describes a third behavior
- [x] No story — this is a pure implementation gap introduced by the Epic 19 sidecar split.

The decision to split intelligence into a sidecar (rather than a native C# port) is documented in Epic 19 phase notes but the composition-root subtask was never explicitly carved out.

## 5. Status

- **Classification**: Incomplete. The adapter bridging is half-done: interfaces exist (`adapters.ts`), factories that chain env → real → adapter are missing.
- **What's needed to finish**:
  1. Implement `createVectorStoreFromEnv(): Promise<IVectorStoreAdapter>` in `adapters.ts`:
     - Read `CHROMADB_URL`, `CHROMADB_AUTH_TOKEN` (if any) env vars.
     - `import { ChromaVectorStore } from '@tamma/intelligence/vector-store/providers/chromadb.js'` (or equivalent).
     - Instantiate `new ChromaVectorStore({ url })`, call `initialize()`.
     - Build an embedder (`OpenAIEmbedder` or similar) from `OPENAI_API_KEY`, `EMBEDDING_MODEL` env.
     - Return `adaptVectorStore(chroma, (text) => embedder.embed(text))`.
  2. Implement `createRagPipelineFromEnv(): Promise<IRagPipeline>` similarly — depends on `createVectorStoreFromEnv()` for the retriever.
  3. Implement `createIndexerFromEnv()`, `createMcpClientFromEnv()`, `createContextAggregatorFromEnv()`, `createCostTrackerFromEnv()` for the remaining four bundle slots.
  4. Call all six from `startServer` when no bundle is provided.
- **Is it "just a stub" or is scope missing?** The scope was understood (JSDoc names the factories) but the implementation was explicitly deferred. Implementation, not spec, gap.
- **Blockers**:
  - #014 — cannot `import` from `@tamma/intelligence` until strict-mode errors are resolved.
  - #003 — env-var contract must be finalized.

## Remediation

- Files to modify:
  - `packages/intelligence-server/src/adapters.ts` — add the six `create*FromEnv` factories.
  - `packages/intelligence-server/src/server.ts` — call them.
  - `packages/intelligence-server/src/index.ts` — re-export the new factories so tests / custom harnesses can opt in.
- Files to create: none (all logic fits in `adapters.ts`).
- Tests to add:
  - Unit (mocked): `adapters.test.ts` — assert that `adaptVectorStore` translates `{ text }` queries into `{ embedding }` queries by calling the injected `embedText`. Already partly covered but currently unused; add coverage for the `hybridSearch` branch.
  - Integration: startup boot test — given env vars set, `startServer()` returns a running app with non-null services (`getStatus()` returns `'ready'` instead of `'not_configured'`).
- Estimated effort: 1-2h (assuming #014 is resolved)
  - Factory implementations: 1h
  - Unit test coverage uplift: 30m
  - Startup integration test: 30m

## References

- Sidecar adapters: `packages/intelligence-server/src/adapters.ts`
- Sidecar entrypoint: `packages/intelligence-server/src/server.ts`
- Sidecar exports: `packages/intelligence-server/src/index.ts`
- Real impls: `packages/intelligence/src/vector-store/providers/chromadb.ts`, `.../rag/rag-pipeline.ts`
- Related findings: #001, #003, #005, #014, #015
