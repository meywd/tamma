# Finding 004: `withTenantContext` / `SET LOCAL app.current_tenant_id` Gone

> **SUPERSEDED (Wave A.5, 2026-04-18)**: Epic 28 db-per-tenant isolation
> replaces the `set_config('app.current_tenant_id', …)` + RLS plane
> entirely. With one physical DB per tenant there is no shared
> connection that needs per-request tenant binding — the connection
> string itself encodes tenancy. `TenantContextInterceptor` and both
> obsolete DbContexts were deleted in Wave A.5 commits `7289d4b`,
> `3548f12`, `fc3be04`. Tenant-scoped repositories now use
> `ITenantDbContextFactory.CreateAsync(tenantId)` which returns a
> `TenantDbContext` bound to a (future) per-tenant connection. This
> finding's remediation is architecturally resolved.

**Scope**: orgs
**Severity**: P0 (cutover-blocking) — **Superseded by Epic 28 db-per-tenant**
**Status**: Superseded
**Estimated port effort**: 4h (resolved by Epic 28 architectural shift, not per-finding fix)

## Remediation status

- **Confirmed**: 2026-04-18 by agent; downgraded 2026-04-20 after code review; re-promoted 2026-04-18 after Story 19-6 endpoint + repo swap.
- **Outcome**: Fixed
- **Commit**: e53c5a1 (Phase-3 connection-string split + TenantContextInterceptor), 9e20e05 (interceptor wired to both contexts), 159f12a (fail-closed filter + integration tests), Story 19-6 (route DashboardEndpoints + OrgEndpoints + Prompt/ProviderHealth/Sanitization repositories through `TammaAppDbContext`; add `AppRoleRegressionTests` proving NULL-tenant rows are not visible via the app-role plane).
- **Live runtime contract**: per-request endpoints and migrated repositories resolve the `TammaAppDbContext` subclass; the `TenantContextInterceptor` runs `set_config('app.current_tenant_id', …, false)` on every connection open. With `ConnectionStrings:TammaAppDb` set, the bound role is `tamma_app` and the Phase-2 RLS policies enforce isolation. With it unset, the EF fail-closed query filter alone provides isolation; raw-SQL paths still get the interceptor's binding so policy enforcement is one env-var flip away.
- **Scaffold shape (unchanged)**: Phase-3 landed `Tamma.Data.Interceptors.TenantContextInterceptor` — an EF Core `DbConnectionInterceptor` that runs `SELECT set_config('app.current_tenant_id', @tenantId, false)` on `ConnectionOpenedAsync` (and its sync twin). Registered as scoped so it reads the current request's `ITenantContext`; attached to BOTH `TammaDbContext` (admin) and `TammaAppDbContext` (app-role subclass) so the binding lands whichever context the caller resolves. Third arg `false` = session-scope (not transaction-scope), matching the pool-lifetime semantics of Npgsql pooled connections (which issue `DISCARD ALL` on release, so the binding is re-applied the next time the interceptor runs). Non-Postgres providers (EF InMemory / SQLite — test path) are no-oped. Integration tests (`QueryFilterAndInterceptorTests`) verify:
  - `current_setting('app.current_tenant_id', true)` returns the bound GUID after the first query.
  - Empty string marker when no tenant is bound (so RLS NULLIF → NULL → fail-closed).
  - Direct Npgsql read as `tamma_app` only sees the bound tenant's rows under RLS.
  - Superuser connection bypasses policies (expected admin behavior).
- **Deployment note**: Activating RLS requires the app to connect as `tamma_app`. Operators must (a) rotate the `tamma_app` password via `ALTER ROLE tamma_app WITH PASSWORD …`, (b) set `ConnectionStrings:TammaAppDb` / `TAMMA_APP_DB_PASSWORD` in the deployment env, (c) restart the API container. If `TammaAppDb` is unset the app falls back to the admin connection with a startup warning — the interceptor still runs (so raw-SQL paths still see the binding) but RLS remains bypassed because the superuser role skips policies. This is the day-1 bring-up shape. **Even with `TammaAppDb` wired, the runtime won't benefit from RLS until `TammaAppDbContext` is threaded through to per-request endpoints and repositories (see follow-up story 19-6).**

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/persistence/with-tenant-context.ts`.

- File: `packages/api/src/persistence/with-tenant-context.ts:18-36`.
- Contract/behavior: every tenant-scoped DB operation ran inside a transaction on a dedicated pool client that first called `SELECT set_config('app.current_tenant_id', $1, true)`. The third arg `true` scopes the setting to the transaction (`SET LOCAL` semantics) so the pool client is safe to release afterward. This is the hook that activated the RLS policies (finding 003).
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/persistence/with-tenant-context.ts (9e9a57c~1) L18-L36
export async function withTenantContext<T>(
  pool: pg.Pool,
  tenantId: string,
  fn: (client: pg.PoolClient) => Promise<T>,
): Promise<T> {
  const client = await pool.connect();
  try {
    await client.query('BEGIN');
    await client.query("SELECT set_config('app.current_tenant_id', $1, true)", [tenantId]);
    const result = await fn(client);
    await client.query('COMMIT');
    return result;
  } catch (err) {
    await client.query('ROLLBACK');
    throw err;
  } finally {
    client.release();
  }
}
```

- Dependencies: Postgres 15+ (`set_config(..., true)` local-scope variant), `pg.Pool`.
- Tests: covered indirectly by RLS integration tests (`packages/api/src/persistence/__tests__/rls-tenant-isolation.integration.test.ts`, deleted).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: no equivalent. Searched `apps/tamma-elsa/src/Tamma.Data/` for `set_config`, `app.current_tenant_id`, `SET LOCAL` — zero hits.
- Contract/behavior: none. The `TammaDbContext` carries `ITenantContext` to evaluate `HasQueryFilter`, but it never sets `app.current_tenant_id` on the underlying Npgsql connection. RLS, if it existed (finding 003), would see `app.current_tenant_id` unset on every query and fail-closed to zero rows, but there are no RLS policies to evaluate either.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs (current) L1-L22 — no connection-level hook
using Microsoft.EntityFrameworkCore;
using Tamma.Core.Entities;
using Tamma.Core.Enums;
using Tamma.Data.Entities;

namespace Tamma.Data;

public class TammaDbContext : DbContext
{
    private readonly ITenantContext? _tenantContext;

    public TammaDbContext(DbContextOptions<TammaDbContext> options)
        : base(options)
    {
    }

    public TammaDbContext(DbContextOptions<TammaDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }
```

No `SavingChangesAsync` override, no `DbConnectionInterceptor`, no `SaveChangesInterceptor` that would run `SELECT set_config(...)` before queries.

- Dependencies: `ITenantContext`, `TenantContext` (`apps/tamma-elsa/src/Tamma.Data/TenantContext.cs`) — these track the in-memory value only; they do not push it to Postgres.
- Tests: none.

## 3. The gap

Concrete behavioral difference — what the database sees.

- TS did: every query ran with `current_setting('app.current_tenant_id', true)::uuid` populated, so RLS policies could evaluate `tenant_id = current_setting(...)` correctly.
- C# does: `app.current_tenant_id` is never set. Even if an operator installs the archived RLS migration manually, every query would hit the fail-closed branch (NULL comparison) and return zero rows — the app would look broken.
- For a query like `SELECT * FROM tenants WHERE id = $1` via EF, TS issued:
  1. `BEGIN`
  2. `SELECT set_config('app.current_tenant_id', '...', true)`
  3. `SELECT * FROM tenants WHERE …`
  4. `COMMIT`
  C# issues only step 3 — one statement on an auto-commit connection.
- In production, this closes off the path to reintroducing RLS without also introducing an EF interceptor. The audit summary explicitly calls out this as making "any raw SQL from Elsa, psql, pg_dump, or ADO.NET bypass tenant isolation".

Error paths:
- TS error path: if `tenantId` is missing upstream, `withTenantContext` is never called and the handler returns 403 (from `tenant-context.ts:121`).
- C# error path: no error — queries return rows from all tenants (finding 002).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-17/17-5-api-tenant-context-middleware.md`.
- Story's acceptance criteria for this behavior:
  - AC 4: "Before any database query, the middleware calls `SET app.current_tenant_id = '<tenantId>'` on the PostgreSQL connection, activating RLS policies (Story 17.2)".
  - AC 10: "The task queue (`ITaskQueue`) uses `tenantId` (mapped from `installationId`) for enqueue and dequeue operations".
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Not-yet-implemented. No direct port, no equivalent interceptor, no middleware hook.
- **What's needed to finish**:
  1. Implement an `EfCore.Npgsql` `DbConnectionInterceptor` that runs `SELECT set_config('app.current_tenant_id', @tenantId, false)` on `ConnectionOpenedAsync` (session-scoped, since EF Core uses connection-per-context-lifetime by default) OR wrap every `SaveChangesAsync` / query execution in a transaction that does `set_config(..., true)` (local-scoped).
  2. Register the interceptor in `DependencyInjection.cs` via `AddDbContext(opts => opts.AddInterceptors(...))`.
  3. Ensure the interceptor fetches `TenantContext.TenantId` from the DI scope (not a singleton) so it matches the request's scope.
  4. Verify with a raw ADO.NET read that `SHOW app.current_tenant_id;` returns the expected value after the interceptor runs.
- **Is it "just a stub" or is scope missing?** The scope was understood in Story 17-5 AC 4; the port apparently decided EF `HasQueryFilter` was sufficient. It is not — the feature has no defense-in-depth without this hook.
- **Blockers**: Depends on finding 003 (add RLS) to be useful. Depends on finding 023 (fix middleware) so `TenantContext.TenantId` is reliably set before queries.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/DependencyInjection.cs` — register interceptor.
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Data/Interceptors/TenantContextConnectionInterceptor.cs` (implements `DbConnectionInterceptor`, overrides `ConnectionOpenedAsync`).
  - `apps/tamma-elsa/tests/Tamma.Data.Tests/Interceptors/TenantContextConnectionInterceptorTests.cs`.
- Tests to add:
  - `ConnectionOpened_SetsAppCurrentTenantId_FromTenantContext`
  - `ConnectionOpened_SetsNullMarker_WhenTenantContextIsUnset` (expect RLS fail-closed)
  - `SelectCurrentSetting_ReturnsExpectedTenantId_AfterQuery` (raw Npgsql read-back)
- Estimated effort: 4h broken down as:
  - Interceptor implementation: 1h
  - DI wiring + scope-resolution check: 0.5h
  - Tests: 2h
  - Validation against Postgres 15/17: 0.5h

## References

- TS source: `packages/api/src/persistence/with-tenant-context.ts:18-36` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` (no equivalent)
- Story: `docs/stories/epic-17/17-5-api-tenant-context-middleware.md` (AC 4)
- Related findings: `003-rls-policies-absent.md`, `005-prevent-tenant-id-change-trigger-gone.md`, `023-tenant-context-middleware-shallow.md`
- Archived SQL migration: `database/archived-sql-migrations/010_rls_tenant_isolation.sql`, `database/archived-sql-migrations/011_tenant_scoped_stores.sql`
