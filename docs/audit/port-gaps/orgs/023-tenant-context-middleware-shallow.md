# Finding 023: `TenantContextMiddleware` — JWT-Only, No 403, No Installation/User Fallback

**Scope**: orgs
**Severity**: P0 (cutover-blocking)
**Status**: Incomplete (3 of 4 resolution sources missing + fail-open on the one present)
**Estimated port effort**: 6h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/middleware/tenant-context.ts`.

- File: `packages/api/src/middleware/tenant-context.ts:58-132`.
- Contract/behavior: four-source resolution with a hard-failing 403 when none resolves. Source precedence: (1) `AuthPrincipal.tenantId` (unified API key auth), (2) JWT `tenantId` claim, (3) `installationContext.installationId` → `tenantStore.getTenantByExternalId(installationId)`, (4) `authUser.id` → `userStore.getUser(userId).tenantId`. If `enableAuth` is false, fall back to `DEFAULT_TENANT_ID`. On failure, return 403 and stop; on success, decorate `request.tenantId` and attach `tenantId` to the request's Pino child logger.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/middleware/tenant-context.ts (9e9a57c~1) L67-L132
fastify.addHook('onRequest', async (request, reply) => {
  if (TENANT_FREE_PATHS.some((p) => request.url === p || request.url.startsWith(p + '/'))) {
    return;
  }

  let tenantId: string | undefined;

  if (!enableAuth) {
    tenantId = DEFAULT_TENANT_ID;
  } else {
    // Source 1: AuthPrincipal (unified API key auth)
    const principal = (request as FastifyRequest & { authPrincipal?: AuthPrincipal }).authPrincipal;
    if (principal) {
      if (principal.tenantId !== null) {
        tenantId = principal.tenantId;
      }
    }

    // Source 2: JWT tenantId claim
    if (tenantId === undefined) {
      const authUser = (request as FastifyRequest & { authUser?: { tenantId?: string } }).authUser;
      if (authUser?.tenantId) {
        tenantId = authUser.tenantId;
      }
    }

    // Source 3: Installation context → tenant lookup
    if (tenantId === undefined) {
      const installCtx = (request as FastifyRequest & { installationContext?: { installationId: number } }).installationContext;
      if (installCtx?.installationId !== undefined) {
        const tenant = await tenantStore.getTenantByExternalId(
          String(installCtx.installationId),
        );
        if (tenant) {
          tenantId = tenant.id;
        }
      }
    }

    // Source 4: User's tenant
    if (tenantId === undefined) {
      const authUser = (request as FastifyRequest & { authUser?: { id?: string } }).authUser;
      if (authUser?.id) {
        const user = await userStore.getUser(authUser.id);
        if (user?.tenantId !== null && user?.tenantId !== undefined) {
          tenantId = user.tenantId;
        }
      }
    }
  }

  if (tenantId === undefined) {
    reply.status(403).send({
      error: 'Tenant context could not be resolved',
    });
    return;
  }

  request.tenantId = tenantId;
  request.log = request.log.child({ tenantId });
});
```

- Dependencies: `ITenantStore.getTenantByExternalId`, `IUserStore.getUser`, `DEFAULT_TENANT_ID` constant.
- Tests: `packages/api/src/middleware/__tests__/tenant-context.test.ts` (deleted) covered all 4 sources plus the CLI fallback and the 403 path.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs:26-52`.
- Contract/behavior: one source only — JWT `tid` claim. No installation-context lookup, no user-row fallback, no `DEFAULT_TENANT_ID` / CLI fallback, no 403 on unresolved. Silent forward to `next(context)` regardless of resolution outcome.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs (current) L26-L52
public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
{
    var path = context.Request.Path.Value ?? "";

    // Skip tenant resolution for public paths
    if (TenantFreePaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
    {
        await next(context);
        return;
    }

    // Not authenticated? Let auth handle it
    if (context.User.Identity?.IsAuthenticated != true)
    {
        await next(context);
        return;
    }

    // Extract tenant ID from JWT claim
    var tidClaim = context.User.FindFirst("tid")?.Value;
    if (tidClaim is not null && Guid.TryParse(tidClaim, out var tenantId))
    {
        tenantContext.SetTenantId(tenantId);
    }

    await next(context);          // ← silently forwards on failure
}
```

- Dependencies: `ITenantContext` (`Tamma.Data/ITenantContext.cs`). No `ITenantRepository`, no `IUserRepository` in the constructor — the two fallback sources are not even wired.
- Tests: none.

## 3. The gap

Concrete behavioral difference.

- TS did: 4 resolution sources + CLI fallback + 403-on-failure. Every authenticated request had a guaranteed tenant context or was refused; every unauthenticated request was either on a public allow-list path or got rejected further down the pipeline.
- C# does: 1 source (JWT `tid`), fail-open on everything else. Consequences:
  - API-key callers (`ApiKey` auth scheme, used by the CLI and GitHub App webhooks) never resolve a tenant via this middleware because the scheme does not populate `tid` (the API-key → tenant mapping was source 1 in TS — entirely missing). Downstream handlers run without `TenantContext.TenantId`, which combined with the fail-open EF filter (finding 002) returns all tenants' rows.
  - GitHub webhook callers with `InstallationContext` never resolve a tenant. The archived path was: installation ID → `tenants.external_id` → `tenant.id`. This was explicitly called out as Source 3 in Story 17-5 AC 2.b.
  - Users whose JWT is missing `tid` (e.g., legacy tokens, tokens issued before a tenant was assigned, tokens from a social-login flow that didn't set it) slip through. `users.tenant_id` might have the value, but the middleware does not read the user row to fall back.
  - CLI/self-hosted mode has no `DEFAULT_TENANT_ID` fallback.
- For a GitHub webhook from an installation in tenant A: TS resolves tenant A via source 3; C# leaves `TenantContext.TenantId = null`, triggers finding 002's fail-open, EF returns all tenants' rows to whatever query follows.
- In production: this is the root cause for the cross-tenant leak via task queue and webhook processing. Every webhook-triggered workflow runs without a tenant scope.

Error paths:
- TS error path: `403 { "error": "Tenant context could not be resolved" }`.
- C# error path: none.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-17/17-5-api-tenant-context-middleware.md`.
- Story's acceptance criteria for this behavior:
  - AC 1: "A Fastify plugin `registerTenantContextPlugin` exists that runs as an `onRequest` hook after authentication". (C# has a middleware, parity is reasonable.)
  - AC 2: "The plugin resolves the current tenant from one of three sources (in priority order): (a) **JWT claims**, (b) **API key**: `InstallationContext.installationId` => lookup `tenants.external_id` => `tenant_id`, (c) **oauth2-proxy headers**: `X-Auth-Request-User` => lookup user => user's `tenant_id`".
  - AC 5: "CLI/self-hosted mode (auth disabled) uses `DEFAULT_TENANT_ID` as the implicit tenant".
  - AC 6: "If tenant resolution fails (unknown installation, user not linked to a tenant), the request is rejected with 403".
  - AC 13: "Superadmin/platform operations (future) can set tenant context explicitly via a header for cross-tenant management". (Future — not in scope here.)
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Incomplete. The shape exists, but 3 of 4 resolution sources are missing and the fail-open behavior inverts the story's AC 6.
- **What's needed to finish**:
  1. Inject `ITenantRepository` and `IUserRepository` into the middleware.
  2. Add source 1: look up the API key principal's tenant. In C# this is likely in the `ApiKeyAuthHandler` — either surface `tenantId` on `ClaimsPrincipal` or on a new `HttpContext.Items["InstallationContext"]`.
  3. Add source 2 (today's C# implementation).
  4. Add source 3: if `HttpContext.Items["InstallationContext"]?.InstallationId` exists (set by GitHub webhook handler), call `tenantRepo.GetByExternalIdAsync(installationId.ToString())`.
  5. Add source 4: if authenticated user has no `tid` claim, load `userRepo.GetByIdAsync(userId)` and read `user.TenantId`.
  6. If `enableAuth == false`, fallback to `DefaultTenantId` (finding 006).
  7. After all four sources, if still null, return `403 { "error": "Tenant context could not be resolved" }` and short-circuit.
  8. Attach `tenantId` to the Serilog / Pino-equivalent structured log context (`LogContext.PushProperty("TenantId", tenantId)` if Serilog).
- **Is it "just a stub" or is scope missing?** Scope was fully documented in Story 17-5. The port shipped "source 2 only" as a placeholder.
- **Blockers**: Depends on finding 006 (default tenant seed), finding 002 (rewrite EF filter to fail-closed) — without those, adding 403-on-failure would start rejecting requests that currently pass through unscoped.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs`, `apps/tamma-elsa/src/Tamma.Api/Auth/ApiKeyAuthHandler.cs` (surface `tenantId`), `apps/tamma-elsa/src/Tamma.Api/Program.cs` (wire DI if needed).
- Files to create: `apps/tamma-elsa/tests/Tamma.Api.Tests/Tenancy/TenantContextMiddlewareTests.cs`.
- Tests to add:
  - `Middleware_Resolves_FromApiKeyPrincipalTenantId`
  - `Middleware_Resolves_FromJwtTidClaim`
  - `Middleware_Resolves_FromInstallationContext_ViaExternalIdLookup`
  - `Middleware_Resolves_FromUserRow_WhenJwtLacksTid`
  - `Middleware_UsesDefaultTenantId_InCliMode`
  - `Middleware_Returns403_WhenNoSourceResolves_AndAuthenticated`
  - `Middleware_SkipsTenantFreePaths_WithoutAttemptingResolution`
- Estimated effort: 6h broken down as:
  - Wire 4 sources: 2h
  - CLI fallback + 403 behavior: 0.5h
  - ApiKey handler surface changes: 1h
  - Tests (7 cases): 2.5h

## References

- TS source: `packages/api/src/middleware/tenant-context.ts:58-132` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs:26-52`
- Story: `docs/stories/epic-17/17-5-api-tenant-context-middleware.md` (ACs 1, 2, 5, 6)
- Related findings: `002-ef-filter-permissive-null-tenant.md`, `004-with-tenant-context-set-local-gone.md`, `006-default-tenant-sentinel-not-seeded.md`, `024-require-tenant-missing.md`
