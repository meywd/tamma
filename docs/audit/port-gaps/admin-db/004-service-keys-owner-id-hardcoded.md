# Finding 004: POST `/api/admin/service-keys` hardcodes `OwnerId = "system"`

**Scope**: admin-db
**Severity**: P1
**Status**: Behavioral drift
**Estimated port effort**: 2h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/admin/service-keys.ts`.

- File: `packages/api/src/routes/admin/service-keys.ts:39-75`
- Contract/behavior: the request body **requires** `serviceName: string` and records it as `ownerId` on the `api_keys` row. That lets platform operators mint separate keys for `"elsa-server"`, `"tamma-api-dotnet"`, `"dashboard-bff"`, etc. — and revoke one without affecting the others. The response echoes `serviceName` so the caller can label it in their vault.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/admin/service-keys.ts (9e9a57c~1)
const serviceName = (body as Record<string, unknown>)['serviceName'];
if (!serviceName || typeof serviceName !== 'string') {
  return reply.status(400).send({ error: 'serviceName is required' });
}
...
const record = await apiKeyStore.createApiKey({
  scope: 'service',
  ownerId: serviceName,   // ← caller-provided, e.g. "elsa-server"
  keyHash,
  keyPrefix,
  label,
  permissions,
  tenantId: null,
});
...
return reply.status(201).send({
  id: record.id,
  serviceName: record.ownerId,   // ← echoed to response
  label: ..., permissions: ..., keyPrefix: ..., createdAt: ..., rawKey, warning: ...
});
```

- Dependencies: `IApiKeyStore.createApiKey`, `api_keys.owner_id TEXT NOT NULL` from migration 009.
- Tests that exercised this: `create-app-admin-auth.test.ts` indirectly (it sends `serviceName: 'elsa-server'` in the 401 assertion).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:17-39`
- Contract/behavior: the body DTO `CreateServiceKeyRequest` has no `ServiceName` field. The handler writes `OwnerId = "system"` verbatim for every row created.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs (current)
public static async Task<IResult> CreateServiceKey(
    CreateServiceKeyRequest req,
    IApiKeyRepository apiKeyRepo,
    ITenantContext tenantContext)
{
    var rawKey = $"tamma_sk_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";
    ...
    var apiKey = await apiKeyRepo.CreateAsync(new ApiKey
    {
        Scope = "service",
        OwnerId = "system",        // ← hardcoded constant
        KeyHash = keyHash,
        KeyPrefix = prefix,
        Label = req.Label,
        Permissions = req.Permissions,
        TenantId = tenantContext.TenantId   // ← see finding 005
    });
    return Results.Created(..., new ServiceKeyResponse(apiKey.Id, apiKey.Label, apiKey.KeyPrefix, apiKey.Permissions, apiKey.CreatedAt, rawKey));
}
```

- Dependencies: `IApiKeyRepository`, `ITenantContext`, DTO `CreateServiceKeyRequest(Label, Permissions)` in `Tamma.Api/Dtos/Admin/`.
- Tests: none in `Tamma.Api.Tests`.

## 3. The gap

- TS did: record `ownerId = <request.body.serviceName>` per row; two keys for `"elsa-server"` and `"tamma-api-dotnet"` are distinguishable and independently revocable.
- C# does: write `owner_id = "system"` for every service key ever minted.
- For a caller sending `POST { label:"ELSA prod", permissions:["diagnostics:write"] }`, TS would require the missing `serviceName` and return 400; C# accepts it and writes `(scope="service", owner_id="system", label="ELSA prod", ...)`.
- In production with existing data / deployed clients, this means: you cannot tell which service a compromised key belongs to. The `idx_api_keys_scope_owner` index (migration 009) collapses to a single bucket. Revocation by `ownerId` would revoke every service key at once.

Error paths:
- TS error path: `400 { error: "serviceName is required" }` when missing.
- C# error path: no validation — the request body is accepted even without a service name.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-16/16-7-service-to-service-auth.md`.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

Story 16-7 specifies per-service keys (`elsa`, `tamma-api-dotnet`) for rotation isolation.

## 5. Status

- **Classification**: Behavioral drift (column semantics changed silently)
- **What's needed to finish**:
  1. Add `ServiceName` to `CreateServiceKeyRequest`; validate non-empty on entry.
  2. Persist `OwnerId = req.ServiceName`.
  3. Extend `ServiceKeyResponse` with `ServiceName`.
- **Is it "just a stub" or is scope missing?** The scope was understood and the field was silently dropped. Fast fix.
- **Blockers**: none. `api_keys.owner_id` column already exists.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Dtos/Admin/CreateServiceKeyRequest.cs`, `AdminEndpoints.cs:17-39`, `ServiceKeyResponse.cs`.
- Files to create: none.
- Tests to add: `ServiceKeyCreateTests.cs` — missing `serviceName` → 400; distinct service names → distinct rows; `ListByScope("service")` returns distinct `OwnerId` values.
- Estimated effort: 2h broken down as:
  - DTO + handler wiring: 1h
  - Tests: 1h

## References

- TS source: `packages/api/src/routes/admin/service-keys.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs`
- Story: `docs/stories/epic-16/16-7-service-to-service-auth.md`
- Related findings: `003`, `005`, `016-schema-api-keys-diff.md`
- Archived SQL migration: `database/archived-sql-migrations/009_unified_api_keys.sql`
