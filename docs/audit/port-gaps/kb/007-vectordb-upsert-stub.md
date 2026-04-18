# Finding 007: `VectorDbManagementService.upsert` returns literal "(stub — no store configured)"

**Scope**: kb
**Severity**: P1 (user-visible, silent data loss)
**Status**: Behavioral drift (TS would null-deref; sidecar returns misleading success)
**Estimated port effort**: 0.25h (subset of #002 fix)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/knowledge-base/VectorDBManagementService.ts`.

The deleted TS VectorDB service handled reads (`listCollections`, `getCollectionStats`) with an empty-state fallback but did **not** define an explicit `upsert` path — the TS API's `/api/knowledge-base/vector-db/*` routes did not include a write endpoint. Vector writes went through the indexer pipeline (Story 6-1) rather than a direct HTTP PUT.

A search of the deleted `VectorDBManagementService.ts` confirms no `upsert` or `delete` method existed:

```typescript
// packages/api/src/services/knowledge-base/VectorDBManagementService.ts (9e9a57c~1)
export class VectorDBManagementService {
  private readonly store: IVectorStoreService | null;
  private queryCounters: Map<string, number> = new Map();
  constructor(store?: IVectorStoreService) { this.store = store ?? null; }

  async listCollections(): Promise<CollectionInfo[]> { /* read only */ }
  async getCollectionStats(name: string): Promise<CollectionStatsInfo> { /* read only */ }
  async searchCollection(name: string, req: VectorSearchRequest): Promise<VectorSearchResult[]> { /* read only */ }
  async getStorageUsage(): Promise<StorageUsage> { /* read only */ }
  // no upsert, no delete
}
```

- Dependencies: none for `upsert` — it didn't exist in TS.
- Tests: none — the TS API never shipped a `/vector-db/upsert` endpoint.

## 2. What's in C#

### C# side
The endpoint exists because Epic 19 added it to the C# contract:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs:69-73 (current)
public static async Task<IResult> UpsertVectors(
    [FromServices] IIntelligenceHttpClient client,
    [FromBody] VectorUpsertRequest body,
    CancellationToken ct)
    => Results.Ok(await client.UpsertVectorsAsync(body, ct));
```

Forwarded verbatim to the sidecar.

### Sidecar side — the stub string

```typescript
// packages/intelligence-server/src/services/VectorDbManagementService.ts:92-98 (current)
async upsert(req: VectorUpsertRequest): Promise<{ message: string; count: number }> {
  if (!this.store) {
    return { message: 'Vectors upserted (stub — no store configured)', count: 0 };
  }
  await this.store.upsert(req.collection, req.documents);
  return { message: 'Vectors upserted', count: req.documents.length };
}
```

- Dependencies: `IVectorStoreAdapter` from narrow types; bridges to real `IVectorStore` via `adaptVectorStore` (which is never called — see #004).
- Tests: `packages/intelligence-server/src/__tests__/services/VectorDbManagementService.test.ts` asserts on the stub string.

## 3. The gap

- TS did: **no endpoint existed** — attempts to POST to `/api/knowledge-base/vector-db/upsert` returned 404.
- C# + sidecar does: respond HTTP 200 with `{ "message": "Vectors upserted (stub — no store configured)", "count": 0 }`. Caller believes N documents were written; actually zero.

For a user of the dashboard's "Ingest documents" form or an external ETL script calling `/api/kb/vector-db/upsert`:
- TS: 404 Not Found. Client script errors loudly, logs fail, nothing thinks work succeeded.
- C# + sidecar: HTTP 200 with success message AND a plausible-looking `count: 0`. Client script continues to the next batch. ChromaDB contains nothing. Hours later, RAG queries return empty and the investigation begins.

Error paths:
- TS: HTTP 404 (endpoint didn't exist).
- C# + sidecar: HTTP 200 with literal "(stub …)" string but `count: 0` is a honest clue. Count being zero while input had N documents is the only signal that anything is wrong.

**Silent data loss** is the worst failure mode. A write call that thinks it succeeded but produced nothing is harder to detect than either a clear error or a no-op that returns a 501.

## 4. Gap from stories

`docs/stories/epic-6/story-6-2/6-2-vector-database-integration.md` AC1:

> **AC1: Vector Store Interface**
> - [ ] Support CRUD operations for vectors
> - [ ] Support batch operations for efficiency

CRUD implies write + delete. The story describes these at the `IVectorStore` interface level but doesn't specify a REST surface for them — Epic 19 added the HTTP endpoint without a matching story AC.

Story alignment:
- [ ] Matches TS behavior (TS had no endpoint — cleanest)
- [ ] Matches C# behavior (C# correctly forwards; sidecar is the problem)
- [ ] Describes a third behavior
- [x] No story — the `/vector-db/upsert` endpoint was introduced in Epic 19 without a covering story. Spec gap.

## 5. Status

- **Classification**: Behavioral drift + new feature without story. The endpoint shouldn't silently succeed when the backend is null.
- **What's needed to finish**:
  1. Replace the stub return with `throw new DependencyNotConfiguredError('vectorStore')` (see #002).
  2. Sidecar route handler wraps to HTTP 503.
  3. Update test assertion to expect error.
  4. Backfill Epic 6 story AC or add a new story for direct vector upsert via HTTP.
- **Is it "just a stub" or is scope missing?** Both: the scope was never written down (no story AC), AND the stub-fallback is drift from TS behavior (no endpoint).
- **Blockers**: none — independent 0.25h fix.

## Remediation

- Files to modify:
  - `packages/intelligence-server/src/services/VectorDbManagementService.ts:92-98` — throw instead of return stub.
  - `packages/intelligence-server/src/server.ts:92-97` — wrap route in try/catch → 503.
  - `packages/intelligence-server/src/__tests__/services/VectorDbManagementService.test.ts` — update stub-string assertion.
- Files to create: none (reuses helper from #002).
- Tests to add:
  - `POST /kb/vector-db/upsert` with null store → HTTP 503.
  - `POST /kb/vector-db/upsert` with real store → HTTP 200 and `count` equals input document count.
- Estimated effort: 0.25h

## References

- TS: no endpoint (the `/vector-db/upsert` route did not exist in `packages/api/src/routes/knowledge-base/vector-db-routes.ts` at 9e9a57c~1).
- Sidecar source: `packages/intelligence-server/src/services/VectorDbManagementService.ts:92-98`
- C# endpoint: `apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs:69-73`
- Story: `docs/stories/epic-6/story-6-2/6-2-vector-database-integration.md` AC1 (partial)
- Related findings: #001, #002, #008 (sibling delete endpoint)
