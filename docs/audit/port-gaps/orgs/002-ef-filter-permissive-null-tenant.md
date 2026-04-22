# Finding 002: EF Query Filter Permissive When `TenantContext.TenantId` is Null

**Scope**: orgs
**Severity**: P0 (cutover-blocking)
**Status**: Behavioral drift (fail-open instead of fail-closed)
**Estimated port effort**: 3h

## Remediation status

- **Confirmed**: 2026-04-18 by agent; downgraded 2026-04-20 after code review.
- **Outcome**: Partial — scaffold only, not live
- **Commit**: 549f10d (partial — resolution widen), e53c5a1 (Phase-3 dual context), 9e20e05 (interceptor wiring), 159f12a (fail-closed filter + closure-capture fix + integration tests)
- **Notes**: Scaffold landed in commits `e53c5a1`, `9e20e05`, `159f12a` (TammaAppDbContext, TenantContextInterceptor, fail-closed filter). BUT: zero production code paths inject TammaAppDbContext — all 21 repositories and all endpoints still use the permissive admin TammaDbContext. RLS is dormant. Full remediation requires wiring tenant-scoped repositories onto the app-role connection — tracked as follow-up story in `docs/stories/epic-19/story-19-6-wire-app-role-context.md`.
- **Scaffold shape (unchanged)**: Phase-3 landed a dual-DbContext architecture. `TammaAppDbContext` (subclass, intended for per-request endpoints, connects as `tamma_app`) emits `e.TenantId == CurrentTenantId` — fail-closed when the tenant context is null. The base `TammaDbContext` (admin / migrations / background services) keeps the permissive `CurrentTenantId == null || e.TenantId == CurrentTenantId` form to avoid breaking `TaskQueueProcessor`, `OutboxSmtpSender`, `WorkflowSyncService`, and `EnsurePersonalTenantMiddleware` (which all read cross-tenant by design). Admin and app contexts share the model graph (same migrations history table) but have distinct cached models. Fallback behavior: if `ConnectionStrings:TammaAppDb` is unset, `AddTammaData` logs a warning and points the app context at the admin connection — local dev keeps working with a single Postgres role; production must set the app-role password explicitly.
- **Why "scaffold only"**: a `grep -rn "TammaAppDbContext" apps/tamma-elsa/src/` returns hits only in `Tamma.Data` itself (definition + doc cross-refs). All 21 repositories in `Tamma.Data/Repositories/` inject `TammaDbContext`, and every endpoint that takes a DbContext directly (`DashboardEndpoints`, `OrgEndpoints`, `CranlTenantProvisioner`, `CranlProvisioningWorkflow`, `NullTenantProvisioner`) also uses `TammaDbContext`. The query-filter code path where `EnforceTenantFilter == true` executes only in `QueryFilterAndInterceptorTests`; production never reaches it. The policies + role + interceptor are correct; they are simply not on the runtime hot path.
- **Follow-up (Phase-3.1)**: Repositories currently inject `TammaDbContext`. Migrating them to `TammaAppDbContext` per-finding is the work that flips per-request endpoints onto the fail-closed + RLS-enforced plane. Integration tests (`Tamma.Api.Tests/Tenancy/QueryFilterAndInterceptorTests`) prove both context shapes behave correctly end-to-end against a real Postgres 17 testcontainer — flipping a repository is a one-line change with existing coverage. See `docs/stories/epic-19/story-19-6-wire-app-role-context.md`.

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/middleware/tenant-context.ts`.

- File: `packages/api/src/middleware/tenant-context.ts:58-132`.
- Contract/behavior: the `onRequest` hook must resolve a `tenantId` from one of four sources or **hard-fail with 403**. There is no "allow unscoped queries" mode. Combined with RLS (archived migration `010_rls_tenant_isolation.sql`), even if a handler forgot a `WHERE tenant_id = $N`, `current_setting('app.current_tenant_id', true)::uuid` would be NULL and the row-level policy `tenant_id = NULL::uuid` would return zero rows.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/middleware/tenant-context.ts (9e9a57c~1) L67-L125
fastify.addHook('onRequest', async (request, reply) => {
  if (TENANT_FREE_PATHS.some((p) => request.url === p || request.url.startsWith(p + '/'))) {
    return;
  }

  let tenantId: string | undefined;

  if (!enableAuth) {
    tenantId = DEFAULT_TENANT_ID;
  } else {
    // Source 1: AuthPrincipal (unified API key auth)
    // Source 2: JWT tenantId claim
    // Source 3: Installation context → tenant lookup
    // Source 4: User's tenant
    // …
  }

  if (tenantId === undefined) {
    reply.status(403).send({ error: 'Tenant context could not be resolved' });
    return;
  }

  request.tenantId = tenantId;
  request.log = request.log.child({ tenantId });
});
```

Archived RLS defense (`database/archived-sql-migrations/010_rls_tenant_isolation.sql:43-45`):

```sql
CREATE POLICY tenant_isolation_policy ON tenants
  USING (id = current_setting('app.current_tenant_id', true)::uuid)
  WITH CHECK (id = current_setting('app.current_tenant_id', true)::uuid);
```

- Dependencies: `ITenantStore.getTenantByExternalId`, `IUserStore.getUser`, `DEFAULT_TENANT_ID` from `@tamma/shared`.
- Tests: `packages/api/src/middleware/__tests__/tenant-context.test.ts` (deleted).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:61-80, 248-249, 267-268, 284-285, 301-302, 315-316, 334-335, 358-359, 402-403`, and `apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs:44-49`.
- Contract/behavior: every tenant-scoped `HasQueryFilter` is written as `tenantId == null || e.TenantId == tenantId`. When `_tenantContext.TenantId` is null (because JWT had no `tid`, because the request was routed before the middleware, because tests don't set it, or because a background service runs on a scoped DbContext with no scope), the filter evaluates to TRUE for every row — all tenants' data is returned.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs (current) L77-L80
// Soft delete + tenant isolation filter
var tenantId = _tenantContext?.TenantId;
entity.HasQueryFilter(e => e.DeletedAt == null && (tenantId == null || e.TenantId == tenantId));
```

```csharp
// apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs (current) L247-L249 — AgentConfig filter
var tenantId = _tenantContext?.TenantId;
entity.HasQueryFilter(e => tenantId == null || e.TenantId == tenantId);
```

```csharp
// apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs (current) L26-L52
public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
{
    var path = context.Request.Path.Value ?? "";
    if (TenantFreePaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
    {
        await next(context);
        return;
    }

    if (context.User.Identity?.IsAuthenticated != true)
    {
        await next(context);   // ← forwards; no 403
        return;
    }

    var tidClaim = context.User.FindFirst("tid")?.Value;
    if (tidClaim is not null && Guid.TryParse(tidClaim, out var tenantId))
    {
        tenantContext.SetTenantId(tenantId);
    }

    await next(context);   // ← even if tid missing, continues; tenantContext stays null
}
```

- Dependencies: `ITenantContext` (`Tamma.Data/ITenantContext.cs`), `TenantContext` (Tamma.Data/TenantContext.cs). Registered `AddScoped` in `DependencyInjection.cs:11`.
- Tests: no tests verify the null-tenant fail-closed contract.

## 3. The gap

Concrete behavioral difference — what a caller or user experiences differently.

- TS did: resolve tenant or 403. Queries always ran with `app.current_tenant_id` set, and RLS enforced isolation at the DB level.
- C# does: if `TenantContext.TenantId` is null (JWT missing `tid`, middleware bypassed, BackgroundService, tests, etc.), EF query filters evaluate to TRUE and all tenants' rows are returned. There is no RLS fallback (findings 003, 004).
- For a caller with a valid JWT lacking the `tid` claim hitting `GET /api/prompts`, `GET /api/v1/workflows`, `GET /api/v1/agents/config`, etc.: TS returns `403 { "error": "Tenant context could not be resolved" }`; C# returns prompts / workflows / configs **from every tenant** in the database.
- In production, any code path that constructs a `TammaDbContext` without a scoped `ITenantContext` (background task queue processor, workflow sync service, Elsa activities running outside the request pipeline) queries the whole table. The fail-open filter makes a "forgot to scope" mistake silent.

Error paths:
- TS error path: `403 { "error": "Tenant context could not be resolved" }`.
- C# error path: no error — cross-tenant data silently leaks into the response.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md` and `docs/stories/epic-17/17-5-api-tenant-context-middleware.md`.
- Story's acceptance criteria for this behavior:
  - 17-2 AC 6: "When `app.current_tenant_id` is not set, all queries on RLS-protected tables return zero rows (fail-closed behavior)".
  - 17-5 AC 6: "If tenant resolution fails (unknown installation, user not linked to a tenant), the request is rejected with 403".
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Behavioral drift. The EF filter is present but written as fail-open; the middleware short-circuits to `next` when `tid` is missing.
- **What's needed to finish**:
  1. Rewrite every tenant-scoped `HasQueryFilter` to drop the `tenantId == null ||` disjunction: `entity.HasQueryFilter(e => e.TenantId == _tenantContext.TenantId);`. Null then produces the fail-closed behavior (no rows match `TenantId == null`).
  2. For services that truly need unscoped reads (migrations, health checks, outbox dispatch), use `.IgnoreQueryFilters()` at the call site so the choice is explicit.
  3. In `TenantContextMiddleware`, when `context.User.Identity?.IsAuthenticated == true` but no `tid` claim can be resolved, return 403 (see finding 023).
  4. Layer RLS back in at the DB level for defense-in-depth (see findings 003, 004).
- **Is it "just a stub" or is scope missing?** Scope understood by the story; the port introduced the `null ||` disjunction deliberately, probably to let tests pass without setting a tenant, but kept it in production code.
- **Blockers**: Paired with findings 023, 003, 004, 006. Requires auditing every consumer of `TammaDbContext` to decide which need `.IgnoreQueryFilters()` explicitly.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` — all 9 `HasQueryFilter` sites listed above.
  - `apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs` — add 403 on unresolved.
  - Any repository that intentionally needs cross-tenant reads (e.g., outbox, task queue) — add `.IgnoreQueryFilters()` with a comment.
- Files to create: none.
- Tests to add (in `apps/tamma-elsa/tests/Tamma.Api.Tests/Tenancy/QueryFilterTests.cs`):
  - `Query_ReturnsZeroRows_WhenTenantContextIsNull`
  - `Query_ReturnsOnlyTenantRows_WhenTenantSet`
  - `IgnoreQueryFilters_ReturnsAllRows_ForExplicitUnscopedCallSites`
  - `Middleware_Returns403_WhenAuthenticatedButNoTidClaim`
- Estimated effort: 3h broken down as:
  - Rewrite query filters: 0.5h
  - Audit consumers / add `.IgnoreQueryFilters()` where needed: 1h
  - Tests: 1.5h

## References

- TS source: `packages/api/src/middleware/tenant-context.ts:58-132` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs`, `apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs`
- Story: `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md` (AC 6), `docs/stories/epic-17/17-5-api-tenant-context-middleware.md` (AC 6)
- Related findings: `003-rls-policies-absent.md`, `004-with-tenant-context-set-local-gone.md`, `023-tenant-context-middleware-shallow.md`
- Archived SQL migration: `database/archived-sql-migrations/010_rls_tenant_isolation.sql`
