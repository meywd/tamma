# Finding 002: EF Query Filter Permissive When `TenantContext.TenantId` is Null

**Scope**: orgs
**Severity**: P0 (cutover-blocking)
**Status**: Behavioral drift (fail-open instead of fail-closed)
**Estimated port effort**: 3h

## Remediation status

- **Confirmed**: 2026-04-18 by agent; downgraded 2026-04-20 after code review; re-promoted 2026-04-18 after Story 19-6 endpoint + repo swap.
- **Outcome**: Fixed
- **Commit**: 549f10d (partial — resolution widen), e53c5a1 (Phase-3 dual context), 9e20e05 (interceptor wiring), 159f12a (fail-closed filter + closure-capture fix + integration tests), Story 19-6 (route DashboardEndpoints + OrgEndpoints + Prompt/ProviderHealth/Sanitization repositories through `TammaAppDbContext`; add `AppRoleRegressionTests` covering the NULL-tenant + cross-tenant + empty-binding paths against a Phase-3 Postgres testcontainer).
- **Live runtime contract**: per-request endpoint handlers and the migrated repositories now resolve the `TammaAppDbContext` subclass — its `EnforceTenantFilter` override emits `e.TenantId == CurrentTenantId` and the dual-context split keeps the `TenantContextInterceptor` binding `app.current_tenant_id` on connection open. With `ConnectionStrings:TammaAppDb` set, the connection binds as `tamma_app` and Phase-2 RLS policies enforce isolation at the DB layer. With it unset (dev fallback), the EF query filter alone provides the fail-closed guarantee.
- **Scope kept on `TammaDbContext`**: platform-admin + auth-time + background-service repositories — `UserRepository`, `ApiKeyRepository`, `RefreshTokenRepository`, `PasswordResetRepository`, `TenantRepository`, `TenantMembershipRepository`, `InviteRepository`, `InstallationRepository`, `GitHubWebhookDeliveryRepository`, `EmailOutboxRepository`, `QueuedTaskRepository`, `EventRepository`, `WorkflowRepository`, `AgentConfigRepository`, `DiagnosticsRepository`, `BudgetConfigRepository`, `MentorshipSessionRepository`. Each of these is invoked from a code path where the tenant context is not yet bound (auth, middleware, webhook dispatch) or from a singleton-scope-factory background service. Migrating them safely requires plumbing explicit tenant binding into each background path; tracked as follow-up.

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

## Repositories with split-usage follow-up

Story 19-6 migrated 3 of 21 tenant-scoped repositories to `TammaAppDbContext` and classified the remaining 18 as *legitimately admin* (auth-time / middleware / background-service paths where `ITenantContext` is not bound or safe to bind). That blanket classification is correct at the file level but hides a real architectural split: **6 of those 18 repositories have BOTH an admin call path and one or more per-request, tenant-scoped call paths**. Keeping them on `TammaDbContext` means the per-request paths bypass the fail-closed EF filter and (when `ConnectionStrings:TammaAppDb` is set) the `tamma_app` DB role + RLS defence. Each of these should be split into an admin-only repository + an app-role repository in a follow-up pass.

This section is **documentation only** — it does not mandate the work. Implementation is tracked as a separate follow-up story candidate so this audit doc stays the canonical list for the next pass.

### Repositories needing an admin + app-role split

| # | Repository | Admin path (stays on `TammaDbContext`) | App-role path (move to `TammaAppDbContext`) |
| - | ---------- | -------------------------------------- | ------------------------------------------- |
| 1 | `EmailOutboxRepository` | Drainer / dispatcher loop (outbox worker — tenant context not set when the BackgroundService fires) | `EnqueueAsync` called from request handlers (Register, PasswordReset, CreateInvite, ResendInvite, ResendVerification, TenantInviteEmail, etc.) — tenant context IS bound here |
| 2 | `QueuedTaskRepository` | `TaskQueueProcessor` drain loop (BackgroundService, singleton scope — tenant context deliberately unbound so the processor can see every tenant's queue) | Per-request enqueue sites: webhook dispatch, provisioning trigger, agent-dispatch handlers — all already carry a bound tenant context |
| 3 | `EventRepository` | Cross-tenant platform-wide reads (DCB time-travel / audit aggregation where the platform operator must see every tenant), migration backfills | Per-request domain-event append from handlers (`AppendAsync` in `OrgEndpoints`, `AuthEndpoints`, `AgentEndpoints`, etc.) and scoped reads like `ListByTenantAsync` hit from `OrgEndpoints.ListTenantAudit` |
| 4 | `WorkflowRepository` | Elsa engine wiring (Elsa persistence provider runs outside the request pipeline; the engine needs to see every workflow instance across tenants for dispatch / resume) | Per-tenant workflow CRUD surfaced via `WorkflowEndpoints` (create / list / delete for the path tenant) — caller's tenant is already bound by `TenantContextMiddleware` |
| 5 | `AgentConfigRepository` | System-default config seeding + platform-wide `UpsertSystemDefaultAsync` admin surface | Tenant-override CRUD exposed via `AgentEndpoints` (`PutTenantOverrideAsync`, `GetResolvedAsync`, `DeleteTenantOverrideAsync`) — these are per-request and tenant-scoped by definition |
| 6 | `DiagnosticsRepository` | Platform-wide aggregate views (cross-tenant rollups consumed by the platform-admin dashboard — legitimately needs every tenant's rows) | Per-tenant diagnostics reads surfaced via `DiagnosticsEndpoints` (`GetTenantSummaryAsync`, `GetRecentFailuresForTenantAsync`, etc.) — these should be guarded by the fail-closed filter, not the permissive admin one |

### Why this is deferred, not done

1. **Cost of the split is non-trivial per repo**: each one needs an `I{Name}AdminRepository` + `I{Name}Repository` interface pair, two implementations (one bound to `TammaDbContext`, one to `TammaAppDbContext`), DI rewiring at every call site, and a test suite covering both paths under the Phase-3 Postgres testcontainer (mirrors the `AppRoleRegressionTests` suite Story 19-6 added for the migrated trio).
2. **None of these are P0 right now**: the immediate fail-open exposure (tenant context not bound → filter returns every row) was closed for the three hot repositories in 19-6 (`DashboardRepository`, `OrgRepository`, and the prompt/provider-health/sanitization trio). The 6 repos listed here still rely on the admin filter, but they are only reachable through admin-gated endpoints today — the gap is architectural rather than an immediate leak vector.
3. **Blocker**: requires one audit pass per repo to enumerate every call site and classify it admin vs per-request. Budget: ≈ 0.5 day per repo (discover + split + DI + tests) = 3 dev-days total. Worth scheduling as a standalone audit-pass story once the Phase-3 RLS policies from finding 003 land, because the follow-up story's acceptance criteria can then be written as "app-role path must fail closed under the RLS policies" (a testable DB-layer contract) rather than "app-role path must use `TammaAppDbContext`" (a structural claim that requires code review to verify).

### Follow-up story sketch

> **Title**: Split 6 dual-use repositories into admin + app-role halves.
>
> **Scope**: the 6 repositories listed above. Each gets two interfaces, two implementations, and a migration of call sites from the old one-name-for-both pattern to the new split. No behavioral change at runtime — the admin half keeps the current connection binding, the app-role half picks up the fail-closed filter and the `tamma_app` role binding.
>
> **Out of scope**: the other 12 admin-classified repositories (`UserRepository`, `ApiKeyRepository`, `RefreshTokenRepository`, `PasswordResetRepository`, `TenantRepository`, `TenantMembershipRepository`, `InviteRepository`, `InstallationRepository`, `GitHubWebhookDeliveryRepository`, `BudgetConfigRepository`, `MentorshipSessionRepository`, plus whichever of `EmailOutboxRepository` / `QueuedTaskRepository` ultimately stays admin-only). These are genuinely admin-only in every call site.
>
> **Tests**: per split, an `AppRoleRegressionTests`-shaped suite that exercises (a) the app-role path under a bound tenant context returns only that tenant's rows, (b) the app-role path with no tenant context returns zero rows, (c) the admin path under no tenant context returns every tenant's rows (deliberate).

## References

- TS source: `packages/api/src/middleware/tenant-context.ts:58-132` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs`, `apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs`
- Story: `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md` (AC 6), `docs/stories/epic-17/17-5-api-tenant-context-middleware.md` (AC 6)
- Related findings: `003-rls-policies-absent.md`, `004-with-tenant-context-set-local-gone.md`, `023-tenant-context-middleware-shallow.md`
- Archived SQL migration: `database/archived-sql-migrations/010_rls_tenant_isolation.sql`
