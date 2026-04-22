# Finding 015: `packages/intelligence/` real implementations exist but are never imported at runtime

**Scope**: kb
**Severity**: P2 (dead wiring — surface-level symptom of #001/#004/#014)
**Status**: Incomplete (the implementations are ready; composition is absent)
**Estimated port effort**: 0 additional effort (covered by #001 remediation)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/intelligence/` and `packages/api/`.

The `@tamma/intelligence` package has been in the tree since Epic 6 landed. It contains:

```
packages/intelligence/src/
├── vector-store/
│   ├── providers/
│   │   ├── chromadb.ts        ← real ChromaDB client
│   │   ├── pgvector.ts        ← real PostgreSQL pgvector adapter
│   │   ├── pinecone.ts
│   │   ├── qdrant.ts
│   │   └── weaviate.ts
│   ├── base-vector-store.ts
│   ├── cache/
│   ├── factory.ts             ← vector-store factory (can pick provider by name)
│   └── interfaces.ts
├── rag/
│   ├── rag-pipeline.ts        ← real RAGPipeline
│   ├── retriever.ts
│   ├── ranker.ts
│   ├── query-processor.ts
│   └── sources/               ← multi-source retrieval impls
├── indexer/
│   ├── codebase-indexer.ts    ← real codebase indexer
│   ├── chunking/
│   ├── discovery/
│   ├── embedding/             ← real OpenAI/Cohere embedder impls
│   └── triggers/              ← git hooks, file watchers
├── context/                   ← real context aggregator
└── knowledge-base/            ← knowledge service / capture / matchers
```

The deleted TS API (pre-9e9a57c) imported types from `@tamma/intelligence` but never constructed the concrete classes. The server entrypoint would have been the sensible place; it wasn't done.

- Dependencies: the package self-contains (relies on `@tamma/shared`, `@tamma/providers`, OpenAI SDK, ChromaDB client, etc. — all resolvable via pnpm workspace).
- Tests: `packages/intelligence/src/**/__tests__/*.test.ts` — extensive unit tests, run in CI, pass at runtime.

## 2. What's in C#

### C# side
N/A.

### Sidecar side — `packages/intelligence` is installed at runtime but never imported in sidecar source

The sidecar Dockerfile copies `packages/intelligence` into the build image:

```dockerfile
# packages/intelligence-server/Dockerfile:41 (current)
COPY packages/intelligence packages/intelligence
```

So the real code is physically present in the container at `/app/packages/intelligence/dist/`. But:

```
$ grep -rn "@tamma/intelligence\|packages/intelligence" packages/intelligence-server/src/
packages/intelligence-server/src/adapters.ts:2: * Adapter factories that bridge concrete @tamma/intelligence implementations
packages/intelligence-server/src/adapters.ts:6: * WITHOUT requiring @tamma/intelligence to typecheck cleanly. Intelligence
# (only JSDoc references; no `import` statement for the package)
```

No `import` of `@tamma/intelligence` or any submodule exists in `packages/intelligence-server/src/`. The package is effectively dead weight in the container.

The sidecar's `package.json` does list `@tamma/intelligence` as a workspace dependency — so pnpm symlinks it — but the source never resolves a symbol from it.

- Dependencies: pnpm workspace symlinks; no runtime import.
- Tests: no sidecar tests import from `@tamma/intelligence` either.

## 3. The gap

- TS did: same — real implementations present in the repo, not wired at the `packages/api/` entrypoint.
- C# + sidecar does: same — real implementations present in the container, not wired at the `packages/intelligence-server/` entrypoint.

This is not a user-visible gap on its own — it's the **root cause** of the other P1 findings. Calling it out separately serves as a reminder during remediation:

- Before writing new provider classes, check `packages/intelligence/src/vector-store/providers/`.
- Before writing a new RAG pipeline, check `packages/intelligence/src/rag/`.
- Before writing a codebase indexer, check `packages/intelligence/src/indexer/`.
- All five listed providers (ChromaDB, pgvector, Pinecone, Qdrant, Weaviate) already have adapter classes. Story 6-2 AC2-AC4 is largely implemented; only the composition wiring is missing.

Error paths:
- Compile time: the sidecar compiles without the real impls.
- Runtime: the impls never execute.

## 4. Gap from stories

This overlaps every Epic 6 story (6-1 through 6-5). The real implementations were the Epic 6 delivery; the composition was never built. Epic 19 intended to revive them via the sidecar but stopped at the contract layer.

Story alignment:
- [x] Matches TS behavior (same latent gap)
- [x] Matches C# behavior (same)
- [ ] Describes a third behavior
- [ ] No story — Epic 6 governs; fully spec'd.

## 5. Status

- **Classification**: Incomplete (dead wiring).
- **What's needed to finish**: no new code beyond #001's composition-root wiring. When `createVectorStoreFromEnv()` is implemented, it should:
  - `import { ChromaVectorStore } from '@tamma/intelligence/vector-store/providers/chromadb.js';` (static import, after #014 lands)
  - `new ChromaVectorStore({ url: env.CHROMADB_URL }); await store.initialize(); return adaptVectorStore(store, embedder);`
- **Is it "just a stub" or is scope missing?** Neither — the scope is complete at the implementation level. Gap is pure composition.
- **Blockers**:
  - #014 (strict-mode) — static imports require clean compile.
  - #001 (composition root) — this is where the import lands.

## Remediation

- Files to modify: covered by #001, #004.
- Files to create: none.
- Tests to add: covered by #001 integration tests — a passing ChromaDB round-trip test proves `packages/intelligence` is live.
- Estimated effort: 0 (subsumed by #001; included here for visibility).

## Cross-reference map

Each sidecar service has a matching real implementation that is currently orphaned:

| Sidecar service (null in prod)    | Real impl (present, not imported)                                       |
|-----------------------------------|-------------------------------------------------------------------------|
| `VectorDbManagementService`       | `packages/intelligence/src/vector-store/providers/chromadb.ts`          |
| `RagManagementService`            | `packages/intelligence/src/rag/rag-pipeline.ts`                         |
| `IndexManagementService`          | `packages/intelligence/src/indexer/codebase-indexer.ts`                 |
| `ContextTestingService`           | `packages/intelligence/src/context/` (aggregator class)                 |
| `McpManagementService`            | `packages/mcp-client/` (separate package, same pattern)                 |
| `AnalyticsService` (cost tracker) | `packages/providers/` cost-tracker exports (separate package)           |

## References

- Real impls index: `packages/intelligence/src/index.ts`
- Sidecar package.json: `packages/intelligence-server/package.json` (`dependencies` includes `@tamma/intelligence` but source doesn't import)
- Dockerfile (copies real impl but skips build): `packages/intelligence-server/Dockerfile:41,48`
- Adapter rationale: `packages/intelligence-server/src/adapters.ts:1-18`
- Related findings: #001 (root wiring), #004 (factories), #014 (strict-mode blocker)

## Remediation status

**Status (2026-04-18):** Deferred — out of scope for the C# port pass.

The real implementations live in `packages/intelligence/` (TypeScript) and
the orphan-imports are inside `packages/intelligence-server/src/` (TypeScript).
The cross-reference table in this finding makes the situation clear:
every real impl is in TS, and every sidecar service that should import it
is in TS. There is no C# analog — the entire intelligence stack is owned
by the TS sidecar by design.

**To unblock:** subsumed by finding 001's composition-root work — no
additional effort. Blocked on finding 014 for static imports.
