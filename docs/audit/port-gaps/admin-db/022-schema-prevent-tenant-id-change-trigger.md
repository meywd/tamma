# Finding 022: `prevent_tenant_id_change()` trigger missing from EF schema

**Scope**: admin-db
**Severity**: P0
**Status**: Data-model regression
**Estimated port effort**: 2h

## 1. What's in TS

Archived at `database/archived-sql-migrations/010_rls_tenant_isolation.sql` + `011_tenant_scoped_stores.sql`.

- File: `packages/api/database/migrations/010_rls_tenant_isolation.sql:82-106`, `011_tenant_scoped_stores.sql:40-42, 77-79`
- Contract/behavior: defines a plpgsql function `prevent_tenant_id_change()` that raises an exception if `tenant_id` is modified on an UPDATE, and installs `BEFORE UPDATE` row-level triggers on six tables: `github_installations`, `users`, `user_api_keys`, `user_invites`, `engine_events`, `workflow_instances`. Combined with RLS's `WITH CHECK` clause, this makes tenant membership of a row effectively immutable once written.
- Key code (verbatim quote, annotated):

```sql
-- 010_rls_tenant_isolation.sql
CREATE OR REPLACE FUNCTION prevent_tenant_id_change()
RETURNS TRIGGER AS $$
BEGIN
  IF OLD.tenant_id IS DISTINCT FROM NEW.tenant_id THEN
    RAISE EXCEPTION 'Cannot change tenant_id on existing row';
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_prevent_tenant_change_installations
  BEFORE UPDATE ON github_installations FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();
CREATE TRIGGER trg_prevent_tenant_change_users
  BEFORE UPDATE ON users FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();
CREATE TRIGGER trg_prevent_tenant_change_api_keys
  BEFORE UPDATE ON user_api_keys FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();
CREATE TRIGGER trg_prevent_tenant_change_invites
  BEFORE UPDATE ON user_invites FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();

-- 011_tenant_scoped_stores.sql
CREATE TRIGGER trg_prevent_tenant_change_engine_events
  BEFORE UPDATE ON engine_events FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();
CREATE TRIGGER trg_prevent_tenant_change_workflow_instances
  BEFORE UPDATE ON workflow_instances FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();
```

- Dependencies: `plpgsql` extension (default in Postgres).
- Tests that exercised this: story 17-2 AC #9 specifies that UPDATE cannot change `tenant_id`.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/*.cs` — zero occurrences of `CREATE TRIGGER`, `prevent_tenant_id_change`, or `BEFORE UPDATE`.
- Contract/behavior: EF has no equivalent to row-level triggers. An UPDATE statement that changes `TenantId` will succeed silently, effectively "moving" a row from one tenant to another.
- Key code: n/a (absence).
- Dependencies: none.
- Tests: none.

## 3. The gap

- TS did: raise `Cannot change tenant_id on existing row` exception whenever a cross-tenant move is attempted.
- C# does: allow any `TenantId` update.
- For a caller running `UPDATE users SET tenant_id = '<attacker-tenant>' WHERE id = '<victim>'`, TS raises; C# succeeds.
- In production with existing data / deployed clients, this means:
  - An insider with UPDATE rights (or any code path reusing an entity with a stale `TenantId`) can silently migrate records between tenants, destroying audit trails.
  - Combined with absent RLS (finding 020), this is a direct exfiltration vector: flip `tenant_id`, refetch, done.

Error paths: none — the operation silently succeeds in C#.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md` AC #9.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression
- **What's needed to finish**:
  1. Add a migration that creates the `prevent_tenant_id_change()` function and six triggers, via `migrationBuilder.Sql(@"...")`.
  2. Adjust the trigger list if C# merged/renamed any tables (e.g., `user_api_keys` was merged into `api_keys` in migration 009 — apply the trigger to `api_keys` instead).
- **Is it "just a stub" or is scope missing?** Scope was implemented in TS and dropped in port. Simple restore.
- **Blockers**: pair with finding 020 (RLS).

## Remediation

- Files to modify: none.
- Files to create: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260418000002_PreventTenantIdChange.cs` with the plpgsql function + triggers.
- Tests to add: attempt an update changing `TenantId` → expect Npgsql exception mentioning "Cannot change tenant_id".
- Estimated effort: 2h.

## References

- TS source: `database/archived-sql-migrations/010_rls_tenant_isolation.sql`, `011_tenant_scoped_stores.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/`
- Story: `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md`
- Related findings: `020-schema-rls-policies-missing.md`, `021-schema-tamma-app-role-missing.md`
