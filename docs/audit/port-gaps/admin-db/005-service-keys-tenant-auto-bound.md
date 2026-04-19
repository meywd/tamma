# Finding 005: POST `/api/admin/service-keys` auto-binds `TenantId` instead of null

**Scope**: admin-db
**Severity**: P1
**Status**: Behavioral drift
**Estimated port effort**: 1h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Notes**: `CreateServiceKey` now writes `TenantId = null` and the handler signature drops the `ITenantContext` injection. Inline comment cites the cross-tenant rationale to discourage regression.

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/admin/service-keys.ts`.

- File: `packages/api/src/routes/admin/service-keys.ts:67-76`
- Contract/behavior: service keys are platform-level — they're used by ELSA or `tamma-api-dotnet` to call endpoints on behalf of *any* tenant. TS explicitly set `tenantId: null` at creation time. The comment even says "service keys are not tenant-scoped at creation".
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/admin/service-keys.ts (9e9a57c~1)
const record = await apiKeyStore.createApiKey({
  scope: 'service',
  ownerId: serviceName,
  keyHash,
  keyPrefix,
  label,
  permissions,
  tenantId: null, // service keys are not tenant-scoped at creation
});
```

- Dependencies: `IApiKeyStore.createApiKey`. Migration 009 declares `api_keys.tenant_id UUID REFERENCES tenants(id)` as **nullable** — specifically to support cross-tenant service keys.
- Tests that exercised this: `InMemoryApiKeyStore` usage in `create-app-admin-auth.test.ts` implicitly.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:17-39`
- Contract/behavior: the handler takes `ITenantContext tenantContext` and assigns `TenantId = tenantContext.TenantId`. When an owner mints a service key while logged into tenant `Acme`, the resulting row has `tenant_id = Acme.id`. A later RLS filter scoped to `tenant_id = Acme` will exclude the service key from other tenants' scopes.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs (current)
var apiKey = await apiKeyRepo.CreateAsync(new ApiKey
{
    Scope = "service",
    OwnerId = "system",
    KeyHash = keyHash,
    KeyPrefix = prefix,
    Label = req.Label,
    Permissions = req.Permissions,
    TenantId = tenantContext.TenantId   // ← silently bound to caller's active tenant
});
```

- Dependencies: `ITenantContext` populated by `TenantContextMiddleware` (`Program.cs:304`).
- Tests: none assert the null-tenant contract.

## 3. The gap

- TS did: store `tenant_id = NULL` so the key works for cross-tenant platform calls.
- C# does: store `tenant_id = <caller's active tenant>`.
- For a caller (owner of tenant `Acme`) creating a service key intended for ELSA to call `/api/v1/llm/chat` for tenants `Acme`, `Beta`, `Gamma`, TS produces one key that works for all three; C# produces a key scoped to `Acme`, and once RLS is restored (finding 020), calls on behalf of `Beta`/`Gamma` will silently see zero rows.
- In production with existing data / deployed clients, this means: the moment RLS is re-enabled, every service-to-service flow (Elsa → API on behalf of a user, dashboard BFF → API) breaks for every tenant *except* whichever tenant the key was minted from.

Error paths:
- TS error path: none — null is valid.
- C# error path: none on write; the breakage surfaces as silent data-invisibility at read time.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-16/16-7-service-to-service-auth.md` (service key semantics) and `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md` (RLS contract).
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

Story 16-7's AC treats service keys as a platform credential, not a tenant-scoped artifact.

## 5. Status

- **Classification**: Behavioral drift
- **What's needed to finish**:
  1. Change `AdminEndpoints.CreateServiceKey` to write `TenantId = null`.
  2. If we want the caller's owner identity for audit, record it in an audit event rather than binding the row.
  3. Ensure RLS policies (finding 020) explicitly exempt `scope = 'service'` rows or leave `tenant_id IS NULL` unfiltered.
- **Is it "just a stub" or is scope missing?** The scope was reversed during the port. Simple one-line fix.
- **Blockers**: depends on finding 020 RLS restoration to fully validate cross-tenant behavior.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:17-39`.
- Files to create: none.
- Tests to add: integration test — create service key as tenant A, use it to fetch under tenant B, expect 200 (not 403/empty).
- Estimated effort: 1h broken down as:
  - Fix + test: 1h

## References

- TS source: `packages/api/src/routes/admin/service-keys.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs`
- Story: `docs/stories/epic-16/16-7-service-to-service-auth.md`
- Related findings: `004`, `016-schema-api-keys-diff.md`, `020-schema-rls-policies-missing.md`
- Archived SQL migration: `database/archived-sql-migrations/009_unified_api_keys.sql`
