# Finding 006: GET `/api/admin/service-keys` response missing `rotatedFrom`, `lastUsedAt`, `revokedAt`

**Scope**: admin-db
**Severity**: P2
**Status**: Incomplete
**Estimated port effort**: 1h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/admin/service-keys.ts`.

- File: `packages/api/src/routes/admin/service-keys.ts:101-119`
- Contract/behavior: the list endpoint serializes nine fields including the full lifecycle signal needed by the admin dashboard: `id, serviceName (from ownerId), label, permissions, keyPrefix, createdAt, lastUsedAt, revokedAt, rotatedFrom`.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/admin/service-keys.ts (9e9a57c~1)
const keys = await apiKeyStore.listByScope('service');
const sanitized = keys.map((k) => ({
  id: k.id,
  serviceName: k.ownerId,
  label: k.label,
  permissions: k.permissions,
  keyPrefix: k.keyPrefix,
  createdAt: k.createdAt,
  lastUsedAt: k.lastUsedAt,    // last time this key authenticated
  revokedAt: k.revokedAt,       // null if still active
  rotatedFrom: k.rotatedFrom,   // id of previous key if this is a rotation result
}));
return reply.send(sanitized);
```

- Dependencies: `IApiKeyStore.listByScope`, `api_keys` columns `last_used_at`, `revoked_at`, `rotated_from`.
- Tests that exercised this: none direct; implicit via dashboard.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:41-47`
- Contract/behavior: serializes a 6-tuple `ServiceKeyResponse(Id, Label, KeyPrefix, Permissions, CreatedAt)`. Omits `serviceName`, `lastUsedAt`, `revokedAt`, `rotatedFrom`.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs (current)
public static async Task<IResult> ListServiceKeys(IApiKeyRepository apiKeyRepo)
{
    var keys = await apiKeyRepo.ListByScopeAsync("service");
    var response = keys.Select(k =>
        new ServiceKeyResponse(k.Id, k.Label, k.KeyPrefix, k.Permissions, k.CreatedAt)).ToList();
    return Results.Ok(response);
}
```

- Dependencies: `IApiKeyRepository.ListByScopeAsync`. The `ApiKey` entity already has `LastUsedAt`, `RevokedAt`, `RotatedFromId` columns per `InitialSchema.cs:332-336`. The fields exist; the response just doesn't project them.
- Tests: none.

## 3. The gap

- TS did: return all lifecycle signals so the dashboard can show "Revoked 3 days ago", "Last used 2m ago", "Rotated from key-abcd".
- C# does: strip them; the dashboard cannot tell active keys from dormant ones, or visualize a rotation chain.
- For a caller, TS returns 9 fields per key, C# returns 5. For the admin dashboard's "rotate before this key is used again" flow, the missing `lastUsedAt` field is load-bearing.
- In production with existing data / deployed clients, this means: operators can't audit whether a key is still in use before revoking it. Rotation chains are invisible.

Error paths: none (both 200).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-16/16-7-service-to-service-auth.md` (AC mentions "display last-used timestamp and revocation status").
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Incomplete
- **What's needed to finish**:
  1. Extend `ServiceKeyResponse` with `ServiceName`, `LastUsedAt? DateTime`, `RevokedAt? DateTime`, `RotatedFromId? Guid`.
  2. Update `ListServiceKeys` and `CreateServiceKey`/`RotateServiceKey` to project the new fields.
- **Is it "just a stub" or is scope missing?** Incomplete port — DB columns exist, DTO is narrow.
- **Blockers**: none.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Dtos/Admin/ServiceKeyResponse.cs`, `Endpoints/AdminEndpoints.cs` (Create/List/Rotate).
- Files to create: none.
- Tests to add: `ServiceKeyListTests.cs` — create, use, revoke, rotate; assert fields present at each step.
- Estimated effort: 1h.

## References

- TS source: `packages/api/src/routes/admin/service-keys.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs`
- Story: `docs/stories/epic-16/16-7-service-to-service-auth.md`
- Related findings: `007-service-keys-rotate-no-grace-warning.md`, `016-schema-api-keys-diff.md`
