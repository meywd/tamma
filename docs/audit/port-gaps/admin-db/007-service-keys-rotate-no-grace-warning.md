# Finding 007: POST `/api/admin/service-keys/:id/rotate` missing 24h grace warning + `rotatedFrom`

**Scope**: admin-db
**Severity**: P2
**Status**: Incomplete
**Estimated port effort**: 2h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/admin/service-keys.ts`.

- File: `packages/api/src/routes/admin/service-keys.ts:126-174`
- Contract/behavior: rotation is not a hard cutover. The TS `rotateApiKey` store method creates a new key but leaves the old one valid for a 24h grace period (by setting `revoked_at` to NOW() + 24h on the old row, not NOW()). The HTTP response advertises this to the caller via the `warning` string, and returns `rotatedFrom: <oldId>` so the caller can track the chain and update their secret store knowing the old key still works.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/admin/service-keys.ts (9e9a57c~1)
const newRecord = await apiKeyStore.rotateApiKey(id, keyHash, keyPrefix);
...
return reply.send({
  id: newRecord.id,
  serviceName: newRecord.ownerId,
  label: newRecord.label,
  permissions: newRecord.permissions,
  keyPrefix: newRecord.keyPrefix,
  createdAt: newRecord.createdAt,
  rotatedFrom: newRecord.rotatedFrom,   // ← preserves chain
  rawKey,
  warning: 'Store this key securely. It cannot be retrieved again. Old key is valid for 24h.',
});
```

404 path:
```typescript
} catch (err) {
  const message = err instanceof Error ? err.message : 'Unknown error';
  if (message.includes('not found')) {
    return reply.status(404).send({ error: 'Service key not found' });
  }
  throw err;
}
```

- Dependencies: `IApiKeyStore.rotateApiKey` which writes `api_keys.rotated_from = <oldId>` and `revoked_at = NOW() + INTERVAL '24 hours'` on the old row.
- Tests that exercised this: dashboard integration tests.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:49-56`
- Contract/behavior: calls `apiKeyRepo.RotateAsync(id, keyHash, prefix)` and returns the same truncated `ServiceKeyResponse` as create. No warning, no `rotatedFrom`, no 404 handling, no grace-period disclosure. The underlying `RotateAsync` implementation's behavior around the old row is an open question (see finding 016).
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs (current)
public static async Task<IResult> RotateServiceKey(Guid id, IApiKeyRepository apiKeyRepo)
{
    var rawKey = $"tamma_sk_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";
    var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();
    var prefix = rawKey[..16];
    var newKey = await apiKeyRepo.RotateAsync(id, keyHash, prefix);
    return Results.Ok(new ServiceKeyResponse(newKey.Id, newKey.Label, newKey.KeyPrefix, newKey.Permissions, newKey.CreatedAt, rawKey));
}
```

If `id` doesn't exist, the repository throws — with no catch, ASP.NET returns a 500, not 404.

- Dependencies: `IApiKeyRepository.RotateAsync` in `apps/tamma-elsa/src/Tamma.Data/Repositories/ApiKeyRepository.cs`.
- Tests: none.

## 3. The gap

- TS did: return `{ rotatedFrom, warning: "... 24h ..." }` + proper 404.
- C# does: return a bare `ServiceKeyResponse` + 500 on missing id.
- For a caller rotating a non-existent id, TS returns `404 { error: "Service key not found" }` and C# returns `500` with a stack trace.
- In production with existing data / deployed clients, this means:
  - Operators rotating a key receive no indication that the old key still works for 24h — they're likely to hurry-update every secret store and race-condition themselves into outages.
  - Chain visibility (`rotatedFrom`) is invisible, so "who is this key's predecessor?" is unanswerable from the API.
  - Typo'd rotation id returns 500 instead of 404, so clients can't distinguish "bug" from "user error".

Error paths:
- TS: 404 for missing; 200 with grace warning on success.
- C#: 500 for missing; 200 bare on success.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-16/16-7-service-to-service-auth.md` AC: "rotation retains old key for 24h grace period".
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Incomplete
- **What's needed to finish**:
  1. `ApiKeyRepository.RotateAsync` should set old row's `RevokedAt = DateTime.UtcNow.AddHours(24)`, not `UtcNow` (verify impl).
  2. Handler should catch a `NotFoundException` and return 404.
  3. Response should include `RotatedFromId` and a `warning` string.
- **Is it "just a stub" or is scope missing?** Incomplete — handler-level polish missing.
- **Blockers**: depends on finding 016 `api_keys.rotated_from` FK + lifecycle semantics.

## Remediation

- Files to modify: `AdminEndpoints.cs:49-56`, `ApiKeyRepository.cs` (if grace logic missing), `ServiceKeyResponse.cs` (per finding 006).
- Files to create: `NotFoundException` or use existing ProblemDetails pattern.
- Tests to add: rotate → assert old row `RevokedAt ≈ now+24h`; rotate non-existent id → 404; response has `rotatedFrom`.
- Estimated effort: 2h.

## References

- TS source: `packages/api/src/routes/admin/service-keys.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs`
- Story: `docs/stories/epic-16/16-7-service-to-service-auth.md`
- Related findings: `006`, `008`, `016-schema-api-keys-diff.md`
