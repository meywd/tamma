# Finding 003: RLS Policies from Migration 010 Entirely Absent

**Scope**: orgs
**Severity**: P0 (cutover-blocking)
**Status**: Not-yet-implemented (the whole defense-in-depth layer is missing)
**Estimated port effort**: 6h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Already-fixed
- **Commit**: 6f86086 (admin-db Phase-2)
- **Notes**: Phase-2 migration `Phase2RlsAndTriggers` (2026-04-19) installs `ENABLE ROW LEVEL SECURITY` + `FORCE ROW LEVEL SECURITY` on all 14 tenant-scoped tables, creates the `tenant_isolation_policy` (tenants by `Id`, others by `TenantId`, `github_installation_repos` via parent installation, `api_keys` with `Scope='service'` carve-out), creates the `tamma_app` non-superuser role with idempotent `pg_roles` probe + minimal CRUD grants. Policies are dormant — the runtime still connects as superuser (which bypasses RLS by design). Phase-3 will swap the connection string and turn the dormant policies into a live safety net. Scope: orgs scope inherits this defense-in-depth at no additional cost.

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:database/archived-sql-migrations/010_rls_tenant_isolation.sql`.

- File: `database/archived-sql-migrations/010_rls_tenant_isolation.sql` (107 lines).
- Contract/behavior: creates a non-superuser Postgres role `tamma_app`; enables `ROW LEVEL SECURITY` and `FORCE ROW LEVEL SECURITY` on `tenants`, `github_installations`, `users`, `user_api_keys`, `user_invites`; installs a `tenant_isolation_policy` that reads `current_setting('app.current_tenant_id', true)::uuid` for both USING (read) and WITH CHECK (write); and defines a `prevent_tenant_id_change` trigger. `database/archived-sql-migrations/011_tenant_scoped_stores.sql` extends the same pattern to `engine_events` and `workflow_instances`.
- Key code (verbatim quote, annotated):

```sql
-- database/archived-sql-migrations/010_rls_tenant_isolation.sql (archived) L13-L33
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'tamma_app') THEN
    CREATE ROLE tamma_app LOGIN PASSWORD 'changeme';
  END IF;
END $$;

GRANT USAGE ON SCHEMA public TO tamma_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO tamma_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO tamma_app;

ALTER DEFAULT PRIVILEGES IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tamma_app;
```

```sql
-- database/archived-sql-migrations/010_rls_tenant_isolation.sql (archived) L40-L77
ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenants FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_policy ON tenants
  USING (id = current_setting('app.current_tenant_id', true)::uuid)
  WITH CHECK (id = current_setting('app.current_tenant_id', true)::uuid);

ALTER TABLE github_installations ENABLE ROW LEVEL SECURITY;
ALTER TABLE github_installations FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_policy ON github_installations
  USING (tenant_id = current_setting('app.current_tenant_id', true)::uuid)
  WITH CHECK (tenant_id = current_setting('app.current_tenant_id', true)::uuid);

ALTER TABLE users ENABLE ROW LEVEL SECURITY;
-- …
ALTER TABLE user_api_keys ENABLE ROW LEVEL SECURITY;
-- …
ALTER TABLE user_invites ENABLE ROW LEVEL SECURITY;
-- …
```

- Dependencies: `withTenantContext` (finding 004) sets `app.current_tenant_id` on each connection via `SET LOCAL … set_config(..., true)`.
- Tests: `packages/api/src/persistence/__tests__/rls-tenant-isolation.integration.test.ts` (deleted).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` (531 lines) — defines EF entity configs and `HasQueryFilter` expressions only. No `CREATE POLICY`, no `ALTER TABLE … ENABLE ROW LEVEL SECURITY`, no `tamma_app` role.
- Contract/behavior: tenant isolation exists only in the EF Core LINQ-translation layer. Any code path that bypasses EF — Elsa Workflows connecting via its own Postgres connection string, ADO.NET ad-hoc queries, `psql` from the VPS, `pg_dump` — reads rows across all tenants.
- Key code (verbatim quote, annotated):

```bash
# No file to quote — the migration simply does not exist.
$ ls apps/tamma-elsa/src/Tamma.Data/Migrations | grep -i rls
# (no output)
```

```csharp
// apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs (current) L119-L141 — the only isolation defense
modelBuilder.Entity<Tenant>(entity =>
{
    entity.ToTable("tenants");
    // …
    entity.HasQueryFilter(e => e.DeletedAt == null);   // ← note: no tenant filter at all on tenants!
    // …
});
```

- Dependencies: Postgres connection string in `appsettings.json` uses the superuser account, so even if RLS were added, queries would bypass it unless a dedicated `tamma_app` role is introduced.
- Tests: none verifying cross-tenant isolation via raw SQL.

## 3. The gap

Concrete behavioral difference — what a operator or attacker experiences differently.

- TS did: RLS policies enforced by Postgres regardless of application code. An application bug, SQL injection, or ORM mis-translation was still blocked.
- C# does: the database is flat — one big pool of rows that EF happens to filter. Any of the following bypasses the filter:
  - A repository method that uses `db.Database.ExecuteSqlRaw(...)`.
  - Elsa workflow runtime reading/writing `workflow_instances` directly (it runs on the same Postgres with its own connection).
  - Dashboard operator running `psql` for a support ticket.
  - A new developer writing a reporting query in `db.Tenants.IgnoreQueryFilters()` and forgetting to add `WHERE tenant_id = ...`.
- For an attacker able to trigger a SQL-injection in any repository (e.g., a future `WHERE slug = '{slug}'` concatenation): TS returns 0 rows because RLS filters; C# returns the full table.
- In production, this means Tamma has lost its SOC2 / GDPR defensible position on tenant isolation: the sole barrier is a LINQ expression in `TammaDbContext.cs`.

Error paths:
- TS error path: RLS silently filters (zero rows); violating writes raise `insufficient_privilege` (SQLSTATE 42501).
- C# error path: no error — cross-tenant reads/writes succeed silently.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md`.
- Story's acceptance criteria for this behavior:
  - AC 1: "RLS is enabled (`ALTER TABLE ... ENABLE ROW LEVEL SECURITY`) on: `tenants`, `github_installations`, `users`, `user_api_keys`, `user_invites`".
  - AC 2: "Each table has a policy named `tenant_isolation_policy` that restricts SELECT, INSERT, UPDATE, DELETE to rows where `tenant_id = current_setting('app.current_tenant_id')::uuid`".
  - AC 4: "A PostgreSQL role `tamma_app` exists (or is reused) that is subject to RLS (is NOT a superuser, does NOT have `BYPASSRLS`)".
  - AC 6: "When `app.current_tenant_id` is not set, all queries on RLS-protected tables return zero rows (fail-closed behavior)".
  - AC 12: "A migration test confirms cross-tenant reads are blocked: set tenant A, insert row, set tenant B, SELECT returns zero rows".
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Not-yet-implemented. The entire RLS layer (archived `010_rls_tenant_isolation.sql` + `011_tenant_scoped_stores.sql`) was dropped during the port. No EF Migration replicates it.
- **What's needed to finish**:
  1. Create an EF `MigrationBuilder.Sql(...)` migration that re-creates the `tamma_app` role, enables RLS on every tenant-scoped table (currently: `tenants`, `users`, `tenant_memberships`, `user_invites`, `api_keys`, `github_installations`, `github_installation_repos`, `agent_configs`, `prompt_overrides`, `provider_health`, `provider_diagnostics`, `sanitization_rules`, `workflow_definitions`, `workflow_instances`, `queued_tasks`, `domain_events`, `email_outbox`), and installs `tenant_isolation_policy` on each.
  2. Switch the runtime connection string to the `tamma_app` (non-superuser) role; keep a migration-time connection string with `BYPASSRLS`.
  3. Port `withTenantContext` (finding 004) so every EF query runs inside a transaction with `SELECT set_config('app.current_tenant_id', $1, true)`.
  4. Add an integration test that proves cross-tenant reads return zero rows.
- **Is it "just a stub" or is scope missing?** Scope was documented in Story 17-2; the port consciously or unconsciously skipped the entire migration. The EF `HasQueryFilter` was likely seen as "good enough" in practice but does not satisfy the story's ACs.
- **Blockers**: Depends on findings 004 (SET LOCAL), 023 (middleware), 006 (default tenant seed). Requires a Postgres superuser operation (`CREATE ROLE`) at deploy time.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Data/DependencyInjection.cs` (connection string switch), `apps/tamma-elsa/appsettings*.json` (add `TammaApp` and `TammaOwner` connection strings).
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/XXXXXXXXXXXX_AddTenantRlsPolicies.cs` (idempotent `MigrationBuilder.Sql(...)` with DO-blocks mirroring `010_rls_tenant_isolation.sql` and `011_tenant_scoped_stores.sql`).
  - `apps/tamma-elsa/tests/Tamma.Data.Tests/Tenancy/RlsCrossTenantTests.cs`.
- Tests to add:
  - `CrossTenantRead_ReturnsZeroRows_WhenAppCurrentTenantIdSetToOtherTenant` (raw ADO.NET, not EF).
  - `CrossTenantUpdate_RaisesInsufficientPrivilege_WhenAppCurrentTenantIdSetToOtherTenant`.
  - `AppCurrentTenantIdUnset_ReturnsZeroRows_FailClosed`.
  - `TammaAppRole_DoesNotHaveBypassRls_Assertion`.
- Estimated effort: 6h broken down as:
  - Write migration (14 tables × ENABLE+FORCE+POLICY+trigger): 2.5h
  - Connection string split + DI wiring: 1h
  - Integration tests: 2h
  - Smoke-test migration against local Postgres: 0.5h

## References

- TS source: n/a (this layer was database-side, not TS)
- C# source: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs`, `apps/tamma-elsa/src/Tamma.Data/Migrations/*`
- Story: `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md` (ACs 1, 2, 4, 6, 12)
- Related findings: `002-ef-filter-permissive-null-tenant.md`, `004-with-tenant-context-set-local-gone.md`, `005-prevent-tenant-id-change-trigger-gone.md`, `006-default-tenant-sentinel-not-seeded.md`
- Archived SQL migration: `database/archived-sql-migrations/010_rls_tenant_isolation.sql`, `database/archived-sql-migrations/011_tenant_scoped_stores.sql`
