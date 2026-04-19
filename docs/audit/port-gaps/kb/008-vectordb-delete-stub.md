# Finding 008: `VectorDbManagementService.delete` returns literal "(stub — no store configured)"

**Scope**: kb
**Severity**: P1 (user-visible; dangerous silent success on a destructive verb)
**Status**: Behavioral drift (TS had no endpoint; sidecar returns misleading success)
**Estimated port effort**: 0.25h (subset of #002 fix)

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/knowledge-base/VectorDBManagementService.ts`.

As with #007 (upsert), the deleted TS API did not expose a direct `/vector-db/delete` endpoint. Deletions went through:
- The indexer's incremental update path (removing chunks for deleted files).
- A `DELETE /api/knowledge-base/collections/:name` that dropped an entire collection (existed in TS).

The TS `VectorDBManagementService` had no per-id delete method. A search of the file confirms:

```typescript
// packages/api/src/services/knowledge-base/VectorDBManagementService.ts (9e9a57c~1)
// (no `delete` method that takes ids[])
```

- Dependencies: none (endpoint didn't exist).
- Tests: none.

## 2. What's in C#

### C# side

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs:75-79 (current)
public static async Task<IResult> DeleteVectors(
    [FromServices] IIntelligenceHttpClient client,
    [FromBody] VectorDeleteRequest body,
    CancellationToken ct)
    => Results.Ok(await client.DeleteVectorsAsync(body, ct));
```

Forwards to the sidecar. Critically, the C# method uses HTTP DELETE with a JSON body (see `IntelligenceHttpClient.DeleteAsync` implementation).

### Sidecar side

```typescript
// packages/intelligence-server/src/services/VectorDbManagementService.ts:100-106 (current)
async delete(req: VectorDeleteRequest): Promise<{ message: string }> {
  if (!this.store) {
    return { message: 'Vectors deleted (stub — no store configured)' };
  }
  await this.store.delete(req.collection, req.ids);
  return { message: 'Vectors deleted' };
}
```

- Dependencies: `IVectorStoreAdapter` (narrow interface; null in production per #001).
- Tests: sidecar unit test asserts the stub string.

## 3. The gap

- TS did: **no endpoint existed** — `DELETE /api/knowledge-base/vector-db/delete` returned 404.
- C# + sidecar does: respond HTTP 200 with `{ "message": "Vectors deleted (stub — no store configured)" }`. Caller believes the ids were removed; actually nothing was deleted.

For a user or script calling `DELETE /api/kb/vector-db/delete` with `{ "collection": "codebase", "ids": ["a", "b", "c"] }`:
- TS: HTTP 404. Caller knows to use a different API (per-file re-index or drop-collection).
- C# + sidecar: HTTP 200 with success message. Caller assumes the three ids are gone. They remain.

This is particularly bad because **delete is destructive-intent**:
- If the caller is implementing a GDPR deletion request and polls for confirmation, they now think the data was erased. It was not.
- If the caller is clearing stale vectors before a re-index, the re-index overwrites by id — so end state may be correct by coincidence. Not always.

Error paths:
- TS: HTTP 404 (endpoint didn't exist).
- C# + sidecar: HTTP 200 with developer-debug string. No body field indicating the count.

Unlike the upsert case (which at least returns `count: 0` as a honest clue), `delete` returns no count field, so there is **no observable signal** that the stub path was taken other than the literal "(stub)" substring in `message`. UI toasts typically display `message` verbatim; alert systems parsing JSON typically look at HTTP status, not body strings.

## 4. Gap from stories

`docs/stories/epic-6/story-6-2/6-2-vector-database-integration.md` AC1:

> **AC1: Vector Store Interface**
> - [ ] Support CRUD operations for vectors
> - [ ] Support batch operations for efficiency

The interface supports delete. The REST surface exposing it is not story'd.

`docs/stories/epic-1.5` (secrets/RBAC epic) has GDPR-deletion implications that may depend on this endpoint working — a separate lineage finding if compliance work lands.

Story alignment:
- [ ] Matches TS behavior (TS had no endpoint)
- [ ] Matches C# behavior (C# forwards cleanly; sidecar bug)
- [ ] Describes a third behavior
- [x] No story — new endpoint in Epic 19, no covering AC.

## 5. Status

- **Classification**: Behavioral drift + new feature without story — same pattern as #007.
- **What's needed to finish**:
  1. Replace stub return with `throw new DependencyNotConfiguredError('vectorStore')`.
  2. Route handler → HTTP 503.
  3. When wiring lands, consider returning `{ deleted: <count>, notFound: <count> }` so callers can distinguish "deleted 3 of 3 requested" from "deleted 0 of 3 requested (ids didn't exist)".
  4. Add audit-trail event emission for deletes: `VECTOR.DELETE.SUCCESS` with the collection + id list, per CLAUDE.md DCB event-sourcing pattern.
- **Is it "just a stub" or is scope missing?** Both — endpoint was introduced in Epic 19 with no AC.
- **Blockers**: none for the error-throw fix; #001 for the actual backend wiring.

## Remediation

- Files to modify:
  - `packages/intelligence-server/src/services/VectorDbManagementService.ts:100-106` — throw.
  - `packages/intelligence-server/src/server.ts:98-103` — wrap DELETE route in try/catch → 503.
  - `packages/intelligence-server/src/__tests__/services/VectorDbManagementService.test.ts` — update assertion.
- Files to create: none.
- Tests to add:
  - `DELETE /kb/vector-db/delete` with null store → HTTP 503.
  - `DELETE /kb/vector-db/delete` with real store + existing ids → HTTP 200 + count.
  - `DELETE /kb/vector-db/delete` with real store + missing ids → HTTP 200 + `notFound` field (per remediation note above).
- Estimated effort: 0.25h (matches #007)
  - Change: 5m
  - Tests: 10-15m

## References

- TS: no endpoint (commit `9e9a57c~1`).
- Sidecar source: `packages/intelligence-server/src/services/VectorDbManagementService.ts:100-106`
- C# endpoint: `apps/tamma-elsa/src/Tamma.Api/Endpoints/KbEndpoints.cs:75-79`
- C# client: `apps/tamma-elsa/src/Tamma.Api/Services/KnowledgeBase/IntelligenceHttpClient.cs:68-69` and `DeleteAsync` helper at `196-212`.
- Story: `docs/stories/epic-6/story-6-2/6-2-vector-database-integration.md` AC1 (partial)
- Related findings: #001, #002, #007 (sibling upsert)

## Remediation status

**Status (2026-04-18):** Deferred — out of scope for the C# port pass.

Same root cause as finding 007: the stub-string return is in
`packages/intelligence-server/src/services/VectorDbManagementService.ts:100-106`.
The C# DELETE handler and `IntelligenceHttpClient.DeleteAsync` correctly
forward the body and verb (DELETE-with-body is uncommon but properly
implemented with `HttpRequestMessage`). The "destructive verb returning
silent success" semantics can only be fixed at the sidecar service layer.

**To unblock:** 15-minute sidecar change as part of the finding-002 sweep.
