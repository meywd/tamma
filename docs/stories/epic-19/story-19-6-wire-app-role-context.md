# Story 19-6: Wire Per-Request Repositories onto the App-Role Context

Status: todo (follow-up from Phase-3 scaffold; code-review session 2026-04-20)

## Story

As a **platform operator**,
I want every per-request DB read and write to go through `TammaAppDbContext` (connects as the non-superuser `tamma_app` role) instead of the superuser `TammaDbContext`,
so that the RLS policies installed by migration `20260419021119_Phase2RlsAndTriggers` actually enforce tenant isolation at the database layer, rather than being a dormant scaffold.

## Problem statement

The Phase-3 commits (`e53c5a1`, `9e20e05`, `159f12a`) landed three correctly-built pieces:

1. `TammaAppDbContext` — subclass of `TammaDbContext` that enables a fail-closed EF query filter (`e.TenantId == CurrentTenantId`) and is intended to connect as `tamma_app`.
2. `TenantContextInterceptor` — runs `SELECT set_config('app.current_tenant_id', @tenantId, false)` on connection open.
3. Phase-2 migration — creates the `tamma_app` role, grants CRUD, enables `ENABLE ROW LEVEL SECURITY` + `FORCE ROW LEVEL SECURITY` + a uniform `tenant_isolation_policy` on 14 tenant-scoped tables.

**What is missing**: zero production code paths inject `TammaAppDbContext`. All 21 repositories in `apps/tamma-elsa/src/Tamma.Data/Repositories/` inject `TammaDbContext`. Every endpoint that takes a DbContext directly (`DashboardEndpoints`, `OrgEndpoints`, `CranlTenantProvisioner`, `CranlProvisioningWorkflow`, `NullTenantProvisioner`) also uses `TammaDbContext`. Because `TammaDbContext` connects as the admin/superuser role, Postgres bypasses RLS entirely — the scaffold's `set_config(...)` call runs but has no effect.

Audit findings `orgs/002`, `orgs/004`, `admin-db/020`, and `admin-db/021` have been downgraded from "Fixed" to "Partial — scaffold only, not live" until this story lands.

## Acceptance criteria

1. **All 21 repositories in `apps/tamma-elsa/src/Tamma.Data/Repositories/` that are used on a per-request code path inject `TammaAppDbContext` instead of `TammaDbContext`.** Background-service repositories that legitimately need cross-tenant access (task queue processor, outbox dispatcher, workflow sync service, `EnsurePersonalTenantMiddleware`) continue to use `TammaDbContext` with an explicit comment at the call site.
2. **All endpoint handlers that take a DbContext directly resolve `TammaAppDbContext`.** Specifically: `DashboardEndpoints.cs:21,81`, `OrgEndpoints.cs:512,570`, `CranlTenantProvisioner.cs:47,52`, `CranlProvisioningWorkflow.cs:31,39`, `NullTenantProvisioner.cs:19,22`, plus any callers added after 2026-04-20.
3. **A regression integration test asserts fail-closed behavior at the DB layer**: insert a row with `TenantId = NULL` as admin, open an app-role connection with tenant context bound to a random uuid, run `SELECT COUNT(*)` on each tenant-scoped table, and assert the NULL row is not returned.
4. **Rollout plan is documented**: new `docs/runbooks/enable-app-role-rls.md` captures (a) rotating `tamma_app` password via `ALTER ROLE tamma_app WITH PASSWORD …`, (b) setting `ConnectionStrings:TammaAppDb` / `TAMMA_APP_DB_PASSWORD` in deployment env, (c) the startup log line to look for ("TammaAppDb connection configured"), (d) the probe endpoint to verify RLS is live (a tenant-scoped read returns zero rows when the tenant context is cleared mid-session).
5. **No production behaviour regression**: existing tests pass. Migration + background-service paths continue to use the admin connection. The fallback where `TammaAppDb` is unset and the app context points at the admin connection is preserved for local dev.
6. **Audit findings `orgs/002`, `orgs/004`, `admin-db/020`, `admin-db/021` are marked `Outcome: Fixed` again** after this story merges, with a link back to the delivering PR.

## Files to touch

### Production code (21 repositories)

- `apps/tamma-elsa/src/Tamma.Data/Repositories/*Repository.cs` — audit each, swap `TammaDbContext` → `TammaAppDbContext` on per-request paths. Keep `TammaDbContext` for the intentionally-cross-tenant ones (see below).

### Production code (endpoints)

- `apps/tamma-elsa/src/Tamma.Api/Endpoints/DashboardEndpoints.cs` — lines 21, 81
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` — lines 512, 570
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/CranlTenantProvisioner.cs` — lines 47, 52
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/CranlProvisioningWorkflow.cs` — lines 31, 39
- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/NullTenantProvisioner.cs` — lines 19, 22

### Intentional cross-tenant consumers (keep on `TammaDbContext`, add `// cross-tenant by design` comment)

- `TaskQueueProcessor` (background service — dequeues for any tenant)
- `OutboxSmtpSender` (background service — sends for any tenant)
- `WorkflowSyncService` (background service — reconciles all workflows)
- `EnsurePersonalTenantMiddleware` (runs before tenant context is resolved)
- Migration bootstrap in `Program.cs`

### Tests

- `apps/tamma-elsa/tests/Tamma.Api.Tests/Tenancy/AppRoleRegressionTests.cs` (new) — AC 3 regression test: null-tenant row is not returned via the app-role connection even when the tenant context matches nothing.
- Extend `QueryFilterAndInterceptorTests` if any repository-level assertion is useful.

### Docs

- `docs/runbooks/enable-app-role-rls.md` (new) — AC 4 rollout plan.
- `docs/audit/port-gaps/orgs/002-ef-filter-permissive-null-tenant.md` — re-promote to "Fixed".
- `docs/audit/port-gaps/orgs/004-with-tenant-context-set-local-gone.md` — re-promote to "Fixed".
- `docs/audit/port-gaps/admin-db/020-schema-rls-policies-missing.md` — re-promote to "Fixed".
- `docs/audit/port-gaps/admin-db/021-schema-tamma-app-role-missing.md` — re-promote to "Fixed".

## Non-goals

- No new RLS policies. The migration's policies are correct; this story only activates them.
- No connection-string resolver changes. The Phase-3 resolver already handles admin / app fallback correctly.
- No trigger or schema changes. Phase-2 covered those.
- This story does not address the P1 finding #2 (NULL-tenant policy leak) or P1 finding #5 (webhook key cross-tenant wake) — those are tracked separately.

## Risk notes

- **Connection pooling**: Npgsql pools connections per connection string. Switching 21 repositories onto the app connection string will create a second pool; keep an eye on `MaxPoolSize` tuning in deploy env.
- **Migration ordering**: Since the `tamma_app` password starts as `changeme`, any deploy that flips the connection string without rotating the password will fail auth at startup. The `AddTammaData` warning on missing `TammaAppDb` already covers the happy path, but the runbook needs to call this out.
- **Superuser privilege leaks**: any code path that silently resolves `TammaDbContext` (typed injection vs generic `DbContext`) will look correct but bypass RLS. Use a Roslyn analyzer or a grep CI check to block new direct `TammaDbContext` injection sites outside the approved list.

## References

- Code review: `docs/review/session-2026-04-20.md` §2.1 Finding 1
- Related findings: `orgs/002`, `orgs/004`, `admin-db/020`, `admin-db/021`
- Phase-3 scaffold commits: `e53c5a1`, `9e20e05`, `159f12a`
- Phase-2 migration: `20260419021119_Phase2RlsAndTriggers.cs`
