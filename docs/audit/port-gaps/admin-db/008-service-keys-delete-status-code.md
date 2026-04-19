# Finding 008: DELETE `/api/admin/service-keys/:id` returns 200 not 204, no 404 path

**Scope**: admin-db
**Severity**: P3
**Status**: Behavioral drift
**Estimated port effort**: 30min

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Notes**: Handler probes `GetByIdAsync` first (returns 404 if missing) and now returns `Results.NoContent()` on success.

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/admin/service-keys.ts`.

- File: `packages/api/src/routes/admin/service-keys.ts:184-212`
- Contract/behavior: idiomatic REST — 204 No Content on successful revocation, 404 when the id doesn't exist. The logic emits a structured `keyId` log line before returning.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/admin/service-keys.ts (9e9a57c~1)
app.delete<{Params:{id:string}}>('/api/admin/service-keys/:id', {...}, async (request, reply) => {
  const { id } = request.params;
  try {
    await apiKeyStore.revokeApiKey(id);
    request.log.info({ keyId: id }, 'Service key revoked');
    return reply.status(204).send();
  } catch (err) {
    const message = err instanceof Error ? err.message : 'Unknown error';
    if (message.includes('not found')) {
      return reply.status(404).send({ error: 'Service key not found' });
    }
    throw err;
  }
});
```

- Dependencies: `IApiKeyStore.revokeApiKey`.
- Tests that exercised this: dashboard integration.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:58-62`
- Contract/behavior: returns `200 OK { message:"Service key revoked" }`. No 404 path; `RevokeAsync` on a missing id bubbles a 500.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs (current)
public static async Task<IResult> DeleteServiceKey(Guid id, IApiKeyRepository apiKeyRepo)
{
    await apiKeyRepo.RevokeAsync(id);
    return Results.Ok(new { message = "Service key revoked" });
}
```

- Dependencies: `IApiKeyRepository.RevokeAsync`.
- Tests: none.

## 3. The gap

- TS did: 204 on success, 404 on missing.
- C# does: 200 on success, 500 on missing.
- For a caller using standard REST clients that branch on status code (`if (204) { markRevoked() }`), the C# endpoint's 200 breaks the flow. Typo'd id becomes a 500 with stack trace.
- In production: minor but observable. Docs that say "204 No Content" (if any) are out of sync. Monitoring on 5xx sees noise from `DELETE` typos.

Error paths:
- TS: 204 success, 404 missing.
- C#: 200 success, 500 missing.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-16/16-7-service-to-service-auth.md` (doesn't specify status codes).
- Story alignment:
  - [x] Matches TS behavior (which followed REST convention)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

CLAUDE.md "Naming Conventions → API Endpoints" lists `DELETE /api/v1/issues/:issueId` etc. without specifying 204-vs-200 — but 204 is the web standard.

## 5. Status

- **Classification**: Behavioral drift
- **What's needed to finish**:
  1. Change `Results.Ok(...)` to `Results.NoContent()`.
  2. Add a try/catch for missing id → `Results.NotFound(new { error = "..." })`.
- **Is it "just a stub" or is scope missing?** Fully understood, just differently shaped.
- **Blockers**: none.

## Remediation

- Files to modify: `AdminEndpoints.cs:58-62`.
- Files to create: none.
- Tests to add: DELETE returning 204; DELETE on non-existent id returning 404.
- Estimated effort: 30min.

## References

- TS source: `packages/api/src/routes/admin/service-keys.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs`
- Story: `docs/stories/epic-16/16-7-service-to-service-auth.md`
- Related findings: `007`
