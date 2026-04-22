# Finding 021: `tamma_app` non-superuser role not created by EF migrations

**Scope**: admin-db
**Severity**: P0
**Status**: Data-model regression
**Estimated port effort**: 3h

## Remediation status

- **Confirmed**: 2026-04-18 by agent; downgraded 2026-04-20 after code review; re-promoted 2026-04-18 after Story 19-6 endpoint + repo swap.
- **Outcome**: Fixed
- **Notes**: Role exists in the migration AND now carries per-request traffic. `Phase2RlsAndTriggers` migration creates `tamma_app` role idempotently via `pg_roles` probe + grants `CONNECT`, `USAGE` on schema, `SELECT/INSERT/UPDATE/DELETE` on all current and future tables, `USAGE/SELECT` on sequences. Production deploys must `ALTER ROLE tamma_app PASSWORD '<secret>'` to take the role off the placeholder; without that step `ConnectionStrings:TammaAppDb` will fail auth at startup and the API container won't come up.
- **Live runtime contract**: per-request endpoints (`DashboardEndpoints`, `OrgEndpoints` tx contexts) and the migrated repositories (`PromptRepository`, `ProviderHealthRepository`, `SanitizationRepository`) resolve `TammaAppDbContext`, which binds the `tamma_app` connection string. Background services + auth-time + webhook-dispatch repositories intentionally stay on `TammaDbContext` (admin role) — see Story 19-6 commit messages for the explicit allow-list rationale. `AppRoleRegressionTests` covers the regression vectors against a Phase-3 Postgres testcontainer.

### Deployment runbook (operator-driven)

1. SSH to the Postgres host (or run via `psql -U tamma`): `ALTER ROLE tamma_app WITH PASSWORD '<new-strong-password>';`
2. Set `TAMMA_APP_DB_PASSWORD=<same-password>` in the API deployment env (docker-compose `.env` or Kubernetes secret).
3. Recreate the API service so the new connection string is picked up. Watch the startup log — the "ConnectionStrings:TammaAppDb is not configured" warning should be GONE.
4. Verify via integration probe: a per-request endpoint that reads a tenant-scoped table should still return rows for an authenticated user. If it returns zero, the interceptor didn't bind (check `set_config` command logs with Npgsql logging at debug level).

## 1. What's in TS

Archived at `database/archived-sql-migrations/010_rls_tenant_isolation.sql`.

- File: `packages/api/database/migrations/010_rls_tenant_isolation.sql`
- Contract/behavior: creates a login-capable, non-superuser role `tamma_app` and grants it `SELECT/INSERT/UPDATE/DELETE` on all public-schema tables, plus default privileges for future tables. This is the role under which the application connects; because it is **not** a superuser and does **not** have `BYPASSRLS`, RLS policies actually apply. Migrations run as a different, privileged role.
- Key code (verbatim quote, annotated):

```sql
-- 010_rls_tenant_isolation.sql
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'tamma_app') THEN
    CREATE ROLE tamma_app LOGIN PASSWORD 'changeme';
  END IF;
END $$;

DO $$
BEGIN
  EXECUTE format('GRANT CONNECT ON DATABASE %I TO tamma_app', current_database());
END $$;
GRANT USAGE ON SCHEMA public TO tamma_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO tamma_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO tamma_app;

ALTER DEFAULT PRIVILEGES IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tamma_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO tamma_app;
```

- Dependencies: must run under a privileged role (the migration bootstrap uses the Postgres superuser or the DB-owner).
- Tests that exercised this: AC #4-5 of story 17-2 explicitly check that `tamma_app` exists and lacks `BYPASSRLS`.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/*.cs`, `Program.cs:103-106`
- Contract/behavior: no role creation. The application connects as whatever the connection string credentials specify (typically the DB owner in dev, or an operator-configured role in prod). EF migrations run under that same role, so even if RLS were enabled (finding 020), the connecting role would likely have `BYPASSRLS` — making RLS a no-op.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Program.cs (current)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
builder.Services.AddTammaData(connectionString);
// No separate "migrations connection string" vs "application connection string".
// No CREATE ROLE in any migration.
```

Grepping all 5 EF migration files for `ROLE`, `GRANT`, `tamma_app`: zero hits.

- Dependencies: `appsettings.*.json` or env var for `ConnectionStrings__DefaultConnection`.
- Tests: none.

## 3. The gap

- TS did: create a non-superuser role, grant it minimal privileges, and expect the app to connect as that role.
- C# does: connect as whatever the single configured connection string credentials are. Typically a privileged role.
- For a caller operating via the app, TS forces the query to go through RLS policies; C# goes through whatever privileges the connection role has — no policy evaluation at all.
- In production with existing data / deployed clients, this means: even if finding 020's policies were added tomorrow, they would not take effect because the connecting role has `BYPASSRLS` (which superusers implicitly have). **RLS cannot function without this role**.

Error paths: none — the breakage is that RLS silently no-ops.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md` AC #4-5.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression
- **What's needed to finish**:
  1. Add a migration that creates `tamma_app` role if absent, grants schema usage and CRUD, and sets default privileges.
  2. Split connection strings: `DefaultConnection` (runtime, as `tamma_app`) vs `MigrationConnection` (privileged).
  3. Wire `dbContext.Database.Migrate()` (in `Program.cs:569`) to use the migration connection; the normal `AddDbContext` uses `DefaultConnection`.
  4. Update `docker-compose.yml` / deployment manifests to provision both roles with different passwords.
- **Is it "just a stub" or is scope missing?** Scope is fully spec'd in story 17-2. Not implemented.
- **Blockers**: RLS (finding 020) depends on this; this finding depends on two-connection-string plumbing that the app currently lacks.

## Remediation

- Files to modify: `Program.cs:103-106` (two connection strings), `appsettings.json`, `docker-compose.yml`, `infra/` deploy scripts.
- Files to create: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260418000001_CreateAppRole.cs`.
- Tests to add: integration test asserting `SELECT rolbypassrls FROM pg_roles WHERE rolname='tamma_app'` returns `false`.
- Estimated effort: 3h broken down as:
  - Migration + role creation: 1h
  - Connection-string split + DI: 1h
  - Tests + deploy config: 1h

## References

- TS source: `database/archived-sql-migrations/010_rls_tenant_isolation.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/`, `apps/tamma-elsa/src/Tamma.Api/Program.cs`
- Story: `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md`
- Related findings: `020-schema-rls-policies-missing.md`
- CLAUDE.md section: security — credential handling, multi-tenant
