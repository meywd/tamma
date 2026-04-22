# Finding 029: Unified-auth middleware missing (X-Tenant-Id, suspended check, grace period, audit log)

**Scope**: auth
**Severity**: P1 (service-scope auth broken; missing per-request audit trail)
**Status**: Semantic rewrite (replaced `authenticateApiKey` with a narrower `ApiKeyAuthHandler`)
**Estimated port effort**: 6h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/auth/unified-auth.ts`.

- File: `packages/api/src/auth/unified-auth.ts:1-170`.
- Contract: A single Fastify `onRequest` hook that authenticates any bearer token against the unified `api_keys` table and populates `request.authPrincipal` with a scope-aware tagged union. Key behaviors:
  1. Extract `Authorization: Bearer <token>`.
  2. Hash and look up via `apiKeyStore.findByKeyHash`.
  3. If `revokedAt` is set but in the future → WARN log but still allow (rotation grace period).
  4. Fire-and-forget `updateLastUsed`.
  5. Build `AuthPrincipal` based on `scope`:
     - `user`: resolve `role` from `userStore`; `tenantId` from the key record.
     - `installation`: `installationId = parseInt(ownerId)`; `tenantId` from key record.
     - `service`: read `X-Tenant-Id` header, validate the tenant exists; 400 if invalid; null if absent.
  6. Structured audit log on success (INFO): `{keyId, scope, ownerId, tenantId, method, path}`.
  7. Structured audit log on failure (WARN): `{reason, keyPrefix, method, path}` — four distinct failure modes.
- Key code:

```typescript
// packages/api/src/auth/unified-auth.ts:65-92 (9e9a57c~1, excerpted)
const keyHash = hashApiKey(token);
const keyRecord = await apiKeyStore.findByKeyHash(keyHash);

if (!keyRecord) {
  request.log.warn({ reason: 'invalid-key', keyPrefix, method, path }, 'Auth failure: invalid API key');
  reply.status(401).send({ error: 'Invalid API key' });
  return;
}

// Rotation grace period: revoked_at is set but still in the future
if (keyRecord.revokedAt !== null) {
  const revokedAt = new Date(keyRecord.revokedAt);
  if (revokedAt > new Date()) {
    request.log.warn({
      keyId: keyRecord.id,
      scope: keyRecord.scope,
      gracePeriodEnd: keyRecord.revokedAt,
    }, 'rotating-key-still-in-use');
  }
}

apiKeyStore.updateLastUsed(keyRecord.id).catch(() => {});
```

```typescript
// packages/api/src/auth/unified-auth.ts:114-138 (service scope)
case 'service': {
  const tenantIdHeader = request.headers['x-tenant-id'];
  let tenantId: string | null = null;

  if (typeof tenantIdHeader === 'string' && tenantIdHeader.length > 0) {
    const tenant = await tenantStore.getTenant(tenantIdHeader);
    if (!tenant) {
      request.log.warn({ keyId: keyRecord.id, tenantId: tenantIdHeader, path }, 'Auth failure: tenant not found for X-Tenant-Id header');
      reply.status(400).send({ error: 'Invalid X-Tenant-Id: tenant not found' });
      return;
    }
    tenantId = tenantIdHeader;
  }

  principal = { scope: 'service', keyId, serviceName: keyRecord.ownerId, permissions: keyRecord.permissions, tenantId };
  break;
}
```

- Also handles a suspended-installation case via `IGitHubInstallationStore` (checked separately in the installation-scoped middleware `api-key-auth.ts:53-55`).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Auth/ApiKeyAuthHandler.cs:18-63`.
- Contract: A single `AuthenticationHandler<AuthenticationSchemeOptions>`. Handles only the header-extract + hash-lookup + revoked-check + `last_used_at` update. Populates claims (no tagged union).
- Behaviors MISSING:
  1. **Rotation grace period logging**: C# treats any non-null `RevokedAt` as "revoked", regardless of whether the timestamp is in the past or future (line 40-41: `if (apiKey.RevokedAt is not null) return AuthenticateResult.Fail("API key has been revoked");`). TS allowed future-dated revocations as an in-grace rotation signal.
  2. **X-Tenant-Id resolution for service scope**: no code reads or validates `X-Tenant-Id`.
  3. **Suspended-installation check**: no code checks `github_installations.SuspendedAt` for installation-scope keys.
  4. **Structured audit logging**: no per-request INFO or WARN log.
  5. **Header scheme**: C# expects `Authorization: ApiKey <token>` (line 24); TS expected `Authorization: Bearer <token>`. Wire-incompat for existing CLI clients.
- Key code (copied in Finding 003 but reproduced here):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Auth/ApiKeyAuthHandler.cs:18-63
protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
{
    if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
        return AuthenticateResult.NoResult();

    var headerValue = authHeader.ToString();
    if (!headerValue.StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase))
        return AuthenticateResult.NoResult();

    var rawKey = headerValue["ApiKey ".Length..].Trim();
    // ... hash, lookup ...

    if (apiKey.RevokedAt is not null)
        return AuthenticateResult.Fail("API key has been revoked");

    await apiKeyRepo.UpdateLastUsedAsync(apiKey.Id);

    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, apiKey.OwnerId),
        new("scope", apiKey.Scope),
        new("key_id", apiKey.Id.ToString()),
    };
    if (apiKey.TenantId.HasValue)
        claims.Add(new Claim("tid", apiKey.TenantId.Value.ToString()));
    foreach (var perm in apiKey.Permissions)
        claims.Add(new Claim("permission", perm));
    // ...
}
```

## 3. The gap

Side by side:

| Behavior | TS | C# |
|---|---|---|
| Auth header | `Bearer` | `ApiKey` |
| Unknown token logged | WARN with reason=invalid-key | No log |
| Revoked-in-past | 401 | 401 |
| Revoked-in-future (grace) | WARN, allow through | 401 Fail (blocks valid rotating key) |
| `last_used_at` update | fire-and-forget | awaited (blocks response) |
| X-Tenant-Id resolution | Yes, validated | No |
| Suspended-installation | Yes | No (this table has `SuspendedAt` column — unused) |
| Success audit log | INFO with 6 fields | No |
| Typed principal | AuthPrincipal tagged union | flat `ClaimsPrincipal` claims (see Finding 030) |

For a caller presenting a service key with `X-Tenant-Id: <uuid>`:
- TS: resolves the tenant, embeds in principal, downstream tenant-scoped endpoints work.
- C#: ignores the header. The principal's `tid` claim stays null. Downstream tenant-scoped handler fails with "no tenant context".

For a caller presenting a rotating key during its grace period:
- TS: warns but succeeds — caller has time to discover and rotate.
- C#: fails outright — caller's service breaks at the rotation moment.

For a suspended installation whose key is still in the DB with `revoked_at IS NULL`:
- TS: checks `installation.SuspendedAt IS NOT NULL` → 403 "Installation is suspended".
- C#: no check → authenticates successfully. The suspended installation continues to operate.

For audit/compliance:
- TS: every auth attempt (success or fail) emits a structured line. Downstream logs aggregation can answer "who authenticated with key X in the last hour".
- C#: auth handler emits nothing. Only the ASP.NET request log fires, which has much less detail.

Error paths:
- TS: 401 "Missing or invalid Authorization header" / 401 "Invalid API key" / 400 "Invalid X-Tenant-Id: tenant not found" / 403 "Installation is suspended" / 401 "Invalid API key scope".
- C#: `AuthenticateResult.NoResult()` / `AuthenticateResult.Fail("Invalid API key")` / `Fail("API key has been revoked")`. Two of the five TS error paths unreachable.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-16/16-7-service-to-service-auth.md`
- Story §33: *"A unified auth middleware (`authenticateApiKey`) validates any bearer token by a single lookup against `api_keys` and populates `request.authPrincipal` with a tagged union"*.
- Story §35: *"Service-scope keys are not tenant-scoped at creation time; callers must supply an `X-Tenant-Id` header on every tenant-scoped request, and the middleware validates the tenant exists and populates `request.authPrincipal.tenantId`"*.
- Story §43: *"Audit logging: every authenticated request (all three scopes) emits a structured Pino log at INFO level with `keyId`, `scope`, `ownerId`, `tenantId` (if any), `method`, `path`, and `statusCode`; failed auth attempts log at WARN"*.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

Explicitly required per AC. C# implements a subset.

## 5. Status

- **Classification**: Semantic rewrite (TS middleware → narrower C# handler). The overall concept survived; several critical AC items were dropped.
- **What's needed to finish**:
  1. Change the expected scheme from `ApiKey` to `Bearer` (line 24). Document the change or support both for transition.
  2. Rewrite the `revoked_at` check to treat future-dated values as grace-period — log WARN, allow through.
  3. Add X-Tenant-Id resolution when `apiKey.Scope == "service"`:
     - Read `Request.Headers["X-Tenant-Id"]`.
     - If present, validate via `ITenantRepository.GetByIdAsync`.
     - If invalid, `AuthenticateResult.Fail` or throw with 400.
     - If valid, add `"tid"` claim.
  4. Add suspended-installation check for `scope == "installation"`: fetch the corresponding `GitHubInstallation` row via `IInstallationRepository`, check `SuspendedAt is null`; otherwise 403.
  5. Make `UpdateLastUsedAsync` fire-and-forget (`_ = Task.Run(...)` or similar) — currently blocks the response.
  6. Add structured logging: on success `LogInformation("Authenticated request")` with enriched scope; on failure `LogWarning`.
  7. Coordinate with Finding 030: populate a typed `AuthPrincipal` in `HttpContext.Items` for downstream endpoint handlers.
- **Is it "just a stub" or is scope missing?** Scope understood but re-scoped to a narrower subset. Semantic rewrite.
- **Blockers**: Finding 003 (hash algorithm alignment for existing keys), Finding 030 (typed principal).

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Auth/ApiKeyAuthHandler.cs`.
- Files to create: possibly `apps/tamma-elsa/src/Tamma.Api/Auth/AuthPrincipal.cs` (per Finding 030).
- Tests to add:
  - `ApiKeyAuthHandler_BearerScheme_Accepted`.
  - `ApiKeyAuthHandler_ApiKeyScheme_Accepted` (transition).
  - `ApiKeyAuthHandler_RevokedInFuture_AllowsWithWarning`.
  - `ApiKeyAuthHandler_RevokedInPast_Fails`.
  - `ApiKeyAuthHandler_ServiceScope_ValidTenantHeader_PopulatesTid`.
  - `ApiKeyAuthHandler_ServiceScope_InvalidTenantHeader_Returns400`.
  - `ApiKeyAuthHandler_ServiceScope_MissingTenantHeader_TidIsNull`.
  - `ApiKeyAuthHandler_InstallationScope_Suspended_Returns403`.
  - `ApiKeyAuthHandler_EmitsStructuredSuccessLog`.
  - `ApiKeyAuthHandler_EmitsStructuredFailureLog`.
- Estimated effort: 6h
  - Bearer scheme + grace period + fire-and-forget: 1h
  - X-Tenant-Id validation: 1h
  - Suspended installation check: 1h
  - Structured logging wiring: 1h
  - Tests (10+ cases): 2h

## References

- TS source: `packages/api/src/auth/unified-auth.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Auth/ApiKeyAuthHandler.cs`
- Story: `docs/stories/epic-16/16-7-service-to-service-auth.md` (§33, §35, §43)
- Related findings: `003-api-key-hash-algorithm.md`, `030-auth-principal-union-absent.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `e56b04d`
- **Notes**: ApiKeyAuthHandler now: accepts Bearer + ApiKey headers; tries scrypt fallback hash; treats future-dated RevokedAt as 24h grace period (WARN + allow); validates X-Tenant-Id for service scope; checks SuspendedAt for installation scope; fire-and-forget UpdateLastUsedAsync; emits structured INFO/WARN audit log per request.
