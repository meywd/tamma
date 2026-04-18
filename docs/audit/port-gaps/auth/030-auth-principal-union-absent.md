# Finding 030: `AuthPrincipal` tagged union has no C# analog

**Scope**: auth
**Severity**: P1 (downstream handlers cannot safely branch on scope)
**Status**: Semantic rewrite (replaced with flat claims bag)
**Estimated port effort**: 4h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/auth/principal.ts`.

- File: `packages/api/src/auth/principal.ts:1-19`.
- Contract: A three-variant discriminated union. Every authenticated request has exactly one principal whose `scope` field determines which other fields are present. TypeScript narrows correctly on `if (principal.scope === 'service')`.
- Key code:

```typescript
// packages/api/src/auth/principal.ts:13-19 (9e9a57c~1)
export type AuthPrincipal =
  | { scope: 'user'; keyId: string; userId: string; role: Role; tenantId: string }
  | { scope: 'installation'; keyId: string; installationId: number; tenantId: string }
  | {
      scope: 'service';
      keyId: string;
      serviceName: string;
      permissions: string[];
      tenantId: string | null; // null until X-Tenant-Id header is parsed
    };
```

- Populated by `unified-auth.ts` (see Finding 029). Attached to the Fastify request as `request.authPrincipal`.
- Consumers:
  - `requireScope(scope)` (`require-scope.ts`) — branches on `principal.scope !== 'service'` to skip; only service-scope keys need scope-permission checks.
  - `requirePermission(perm)` — reads `principal.role` when `scope === 'user'`.
  - Route handlers — e.g. a per-installation resource handler reads `principal.installationId` safely, knowing it only exists on installation-scope.
- Tests: TS compile-time safety is the main test; runtime narrowing happens in every consumer.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- No file matching `AuthPrincipal`, `Principal.cs`, or any related name exists in `apps/tamma-elsa/src/Tamma.Api/` — verified via Glob for `Principal*`, `AuthPrincipal*`.
- `ApiKeyAuthHandler.cs:46-58` populates a flat `List<Claim>`:
  ```csharp
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
  ```
- Downstream handlers read these via `ClaimsPrincipal`:
  - `principal.FindFirst("scope")?.Value` — returns `"user"`, `"installation"`, `"service"`, or null.
  - `principal.FindFirst(ClaimTypes.NameIdentifier)?.Value` — is user GUID OR installation ID OR service name depending on scope; always a string.
  - `principal.FindAll("permission")` — list of permission strings.
- No per-scope field differentiation. `installationId` in the claim bag is the same string as `ownerId`; no type safety distinguishes a GUID from an integer.

## 3. The gap

Type safety and semantic clarity:

1. **Scope branching is string-typed**: every consumer must `if (claim == "user") ... else if (claim == "installation") ...`. No compiler enforcement. Missing a case is a runtime bug.
2. **Field presence isn't scope-aware**: code reading `tid` can't know if it's populated (never for service-scope pre-Finding-029-fix, sometimes for user/installation).
3. **Mixed semantics on NameIdentifier**: `ClaimTypes.NameIdentifier` = "user UUID" OR "installation numeric id as string" OR "service name". Casting to `Guid.Parse` works for user-scope, throws for installation-scope. Downstream handlers must know the scope before casting.
4. **No way to express "this endpoint requires a user-scope principal"**: TS `requireScope` checks `principal.scope === 'service'`; `requirePermission` reads `principal.role` on user scope. In C#, every such branch has to re-extract `scope` claim and string-compare.

Production scenario: an engineer adds a new endpoint that requires `tenantId`. In TS they write `if (principal.scope === 'service') assert(principal.tenantId !== null, 'X-Tenant-Id required');` — the type system forces awareness. In C# they write `var tid = principal.FindFirst("tid")?.Value;` — could be null for many reasons (user-scope with no active tenant, service-scope without header, installation-scope with null tenant). No type-guided thinking.

Error paths: the loss is fail-open drift — code that assumes a claim is present runs as member-privileged or tenant-scoped when it shouldn't.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-16/16-7-service-to-service-auth.md`
- §95-102: the story defines the `AuthPrincipal` type in exact TypeScript. Quotes:
  > ```
  > | { scope: 'user'; keyId: string; userId: string; role: Role; tenantId: string }
  > | { scope: 'installation'; keyId: string; installationId: number; tenantId: string }
  > | {
  >     scope: 'service';
  >     keyId: string;
  >     serviceName: string;
  >     permissions: string[];
  >     tenantId: string | null;  // null until X-Tenant-Id header is parsed
  >   }
  > ```
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

Story is explicit about the discriminated-union design.

## 5. Status

- **Classification**: Semantic rewrite. The C# model is technically functional but loses all structural guarantees.
- **What's needed to finish**:
  1. Create an `AuthPrincipal` abstract record (or sealed-record class hierarchy) in C# using record inheritance or a Union-style pattern:
     ```csharp
     public abstract record AuthPrincipal(string KeyId);
     public sealed record UserPrincipal(string KeyId, Guid UserId, string Role, Guid TenantId) : AuthPrincipal(KeyId);
     public sealed record InstallationPrincipal(string KeyId, long InstallationId, Guid TenantId) : AuthPrincipal(KeyId);
     public sealed record ServicePrincipal(string KeyId, string ServiceName, IReadOnlyList<string> Permissions, Guid? TenantId) : AuthPrincipal(KeyId);
     ```
  2. Build the principal in `ApiKeyAuthHandler` and attach to `HttpContext.Items["AuthPrincipal"] = principal`.
  3. Create an extension method `HttpContext.GetAuthPrincipal() → AuthPrincipal`.
  4. Refactor endpoint handlers to `if (httpContext.GetAuthPrincipal() is UserPrincipal up) { ... }` or use C# 11's list/switch patterns.
  5. Create a `RequireScope<T>()` authorization filter that requires a specific concrete type.
  6. Update existing claim-based consumers to the new pattern (incremental — keep claims populated for backward compat).
- **Is it "just a stub" or is scope missing?** Scope visibly re-implemented without the tagged-union structure. Semantic rewrite.
- **Blockers**: Finding 029 (auth handler must populate the new type).

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Auth/ApiKeyAuthHandler.cs`, eventually all endpoint handlers that read scope/role/installation.
- Files to create: `apps/tamma-elsa/src/Tamma.Api/Auth/AuthPrincipal.cs` (record hierarchy), `apps/tamma-elsa/src/Tamma.Api/Auth/HttpContextExtensions.cs` (`.GetAuthPrincipal()`).
- Tests to add:
  - `AuthPrincipal_UserRecord_ExposesRole`.
  - `AuthPrincipal_InstallationRecord_ExposesInstallationId`.
  - `AuthPrincipal_ServiceRecord_TenantIdNullable`.
  - `ApiKeyAuthHandler_PopulatesHttpContextItem` (integration with Finding 029).
  - Pattern-match test showing compile-time exhaustiveness via switch expression.
- Estimated effort: 4h
  - Records + extensions: 1h
  - Handler integration: 1h
  - Consumer refactor (sample 2-3 endpoints): 1h
  - Tests: 1h

## References

- TS source: `packages/api/src/auth/principal.ts` (commit `9e9a57c~1`)
- C# source: None — `AuthPrincipal` does not exist
- Story: `docs/stories/epic-16/16-7-service-to-service-auth.md` (§95-102)
- Related findings: `029-unified-auth-missing.md` (the populator)
