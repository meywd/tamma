# Finding 020: RLS tenant isolation policies entirely absent from EF schema

**Scope**: admin-db
**Severity**: P0
**Status**: Data-model regression
**Estimated port effort**: 12-16h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed (policies installed; dormant pending Phase-3 connection-string swap)
- **Notes**: `20260419021119_Phase2RlsAndTriggers` migration installs `ENABLE ROW LEVEL SECURITY` + `FORCE ROW LEVEL SECURITY` + a `tenant_isolation_policy` on 14 tenant-scoped tables: `tenants`, `tenant_memberships`, `users`, `github_installations`, `github_installation_repos` (joined via parent), `user_invites`, `api_keys` (with service-scope cross-tenant exemption), `domain_events`, `workflow_instances`, `workflow_definitions`, `agent_configs`, `provider_diagnostics`, `provider_health`, `sanitization_rules`, `prompt_overrides`. Policies use `current_setting('app.current_tenant_id', true)::uuid` matching the existing `TenantContextMiddleware`. **Policies are dormant today** because the application still connects as a privileged role (which bypasses RLS); they activate the moment finding 021's connection-string split lands. The `tamma_app` role is also created here so Phase-3 only needs the connection-string change. Tested: full Tamma.Api.Tests suite (540 tests) passes — RLS does not affect privileged-role connections.

## 1. What's in TS

Archived at `database/archived-sql-migrations/010_rls_tenant_isolation.sql` (and `011_tenant_scoped_stores.sql` for event/workflow tables).

- File: `packages/api/database/migrations/010_rls_tenant_isolation.sql`, `011_tenant_scoped_stores.sql`
- Contract/behavior: eight tables — `tenants`, `github_installations`, `users`, `user_api_keys`, `user_invites`, `engine_events`, `workflow_instances`, (and implicitly `agent_configs`/`provider_diagnostics` in later migrations) — have PostgreSQL Row-Level Security enabled with `FORCE ROW LEVEL SECURITY` and a uniform `tenant_isolation_policy` that reads `current_setting('app.current_tenant_id', true)::uuid`. This is defense-in-depth: even if the ORM forgets a `WHERE tenant_id = ?`, Postgres returns zero rows.
- Key code (verbatim quote, annotated):

```sql
-- 010_rls_tenant_isolation.sql
ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON tenants
  USING (id = current_setting('app.current_tenant_id', true)::uuid)
  WITH CHECK (id = current_setting('app.current_tenant_id', true)::uuid);

-- github_installations
ALTER TABLE github_installations ENABLE ROW LEVEL SECURITY;
ALTER TABLE github_installations FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON github_installations
  USING (tenant_id = current_setting('app.current_tenant_id', true)::uuid)
  WITH CHECK (tenant_id = current_setting('app.current_tenant_id', true)::uuid);

-- users, user_api_keys, user_invites: identical policy shape

-- 011_tenant_scoped_stores.sql (engine_events, workflow_instances): identical policy
ALTER TABLE engine_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE engine_events FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON engine_events
  USING (tenant_id = current_setting('app.current_tenant_id', true)::uuid)
  WITH CHECK (tenant_id = current_setting('app.current_tenant_id', true)::uuid);
```

- Dependencies: requires (a) a non-superuser application role (`tamma_app`, see finding 021), (b) per-request `SET app.current_tenant_id = '<uuid>'`, (c) migration-time superuser connection that bypasses RLS.
- Tests that exercised this: AC #12 on story 17-2 specifies a cross-tenant test.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs` (all 821 lines), `20260416192411_ProviderHealthCircuitBreakerState.cs`, `20260417010406_WorkflowInstanceResult.cs`, `20260417010625_TaskQueue.cs`, `20260417114431_EmailOutbox.cs` — *none* contain the text `ROW LEVEL SECURITY`, `CREATE POLICY`, or `tenant_isolation_policy`.
- Contract/behavior: every table is plain-old EF Core with a `TenantId` column but **no row-level enforcement**. EF's `HasQueryFilter` (if set on entities) runs at the LINQ layer only — it's bypassed by raw SQL, Dapper, `.IgnoreQueryFilters()`, pgAdmin, and database compromises.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs (current)
migrationBuilder.CreateTable(
    name: "github_installations",
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false, ...),
        ...
        TenantId = table.Column<Guid>(type: "uuid", nullable: true),   // ← nullable! No RLS
        ...
    },
    constraints: table => { table.PrimaryKey("PK_github_installations", x => x.Id); });
// No ALTER TABLE ... ENABLE ROW LEVEL SECURITY anywhere in the file.
// No CREATE POLICY anywhere.
```

- Dependencies: `TenantContextMiddleware` (`Program.cs:304`) runs `SET app.current_tenant_id = <tenantId>` per request, but there are no policies to consult the session var. The middleware is effectively a no-op for isolation.
- Tests: no test ensures cross-tenant reads are blocked at the database layer.

## 3. The gap

- TS did: enforce tenant isolation at Postgres level on eight tables with `FORCE` mode.
- C# does: nothing. Isolation is purely an application-layer concern, violable by any raw-SQL path.
- For a caller running `SELECT * FROM github_installations` (via Dapper, a console script, `dotnet ef`, pgAdmin, or a SQL-injection bypass), TS would silently return zero rows if `app.current_tenant_id` isn't set (or only matching rows if it is); C# returns every row of every tenant.
- In production with existing data / deployed clients, this means: **a single missed `.Where(x => x.TenantId == current)` in an EF query leaks all tenants' data**. The CLAUDE.md guarantee that "each tenant's data is isolated" currently depends on 100% correct application code; the RLS safety net is absent.

Error paths: none — data simply leaks, silently.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md` — AC 1-12 enumerate the required policies, role, and triggers. None are present in C#.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

Story 17-2 is *the* spec for this work.

## 5. Status

- **Classification**: Data-model regression — the most severe finding in this audit.
- **What's needed to finish**:
  1. Create EF migration(s) that run raw SQL equivalent to `010_rls_tenant_isolation.sql` + `011_tenant_scoped_stores.sql`, using `migrationBuilder.Sql(@"ALTER TABLE ... ENABLE ROW LEVEL SECURITY; FORCE ROW LEVEL SECURITY; CREATE POLICY ...");`.
  2. Ensure the non-superuser `tamma_app` role exists (finding 021) — RLS is pointless without a role that doesn't bypass it.
  3. Ensure `TenantContextMiddleware` runs `SET LOCAL app.current_tenant_id` in the same Npgsql connection as the EF context (not a parallel pool).
  4. Ensure `prevent_tenant_id_change` trigger is installed (finding 022).
  5. Add a cross-tenant assertion test.
- **Is it "just a stub" or is scope missing?** Scope was fully specified in story 17-2 and implemented in TS. It was dropped during the C# port — unambiguous regression.
- **Blockers**: findings 021 (tamma_app role), 022 (trigger), 023 (default tenant sentinel). Also depends on `TenantContextMiddleware` setting the session var on the same connection.

## Remediation

- Files to modify: none.
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/20260418000000_EnableRowLevelSecurity.cs` with raw SQL for each policy.
  - Update `TammaDbContextModelSnapshot.cs` accordingly.
- Tests to add:
  - `Tamma.Data.Tests/Rls/CrossTenantIsolationTests.cs` — set tenant A, insert row; set tenant B, SELECT returns empty; set tenant A, SELECT returns the row.
  - `Tamma.Data.Tests/Rls/RawSqlBypassTests.cs` — raw `SELECT * FROM users` with `tamma_app` role returns zero rows when session var unset.
- Estimated effort: 14h broken down as:
  - Migration SQL: 4h
  - Role + session var wiring: 3h
  - Integration tests: 5h
  - Documentation + runbook: 2h

## References

- TS source: `database/archived-sql-migrations/010_rls_tenant_isolation.sql`, `011_tenant_scoped_stores.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`
- Story: `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md`
- Related findings: `021-schema-tamma-app-role-missing.md`, `022-schema-prevent-tenant-id-change-trigger.md`, `023-schema-default-tenant-sentinel.md`
- CLAUDE.md section: multi-tenant isolation guarantee (implicit)
