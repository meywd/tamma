# Finding 005: `prevent_tenant_id_change` Trigger Not Ported

**Scope**: orgs
**Severity**: P0 (cutover-blocking)
**Status**: Not-yet-implemented (trigger + function absent)
**Estimated port effort**: 1.5h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:database/archived-sql-migrations/010_rls_tenant_isolation.sql`.

- File: `database/archived-sql-migrations/010_rls_tenant_isolation.sql:80-106`, extended by `database/archived-sql-migrations/011_tenant_scoped_stores.sql:39-42, 76-79`.
- Contract/behavior: a `BEFORE UPDATE` trigger on every tenant-scoped table raises an exception if `NEW.tenant_id IS DISTINCT FROM OLD.tenant_id`. This means that once a row is created inside tenant A, it cannot be "moved" to tenant B by any code path — including a maliciously crafted `UPDATE ... SET tenant_id = ...` statement that got past RLS (because RLS only checks tenant_id against the current setting).
- Key code (verbatim quote, annotated):

```sql
-- database/archived-sql-migrations/010_rls_tenant_isolation.sql (archived) L82-L106
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
  BEFORE UPDATE ON github_installations
  FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();

CREATE TRIGGER trg_prevent_tenant_change_users
  BEFORE UPDATE ON users
  FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();

CREATE TRIGGER trg_prevent_tenant_change_api_keys
  BEFORE UPDATE ON user_api_keys
  FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();

CREATE TRIGGER trg_prevent_tenant_change_invites
  BEFORE UPDATE ON user_invites
  FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();
```

```sql
-- database/archived-sql-migrations/011_tenant_scoped_stores.sql (archived) L40-L42, L77-L79
CREATE TRIGGER trg_prevent_tenant_change_engine_events
  BEFORE UPDATE ON engine_events
  FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();

CREATE TRIGGER trg_prevent_tenant_change_workflow_instances
  BEFORE UPDATE ON workflow_instances
  FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();
```

- Dependencies: `plpgsql` extension (standard).
- Tests: integration test confirmed `UPDATE users SET tenant_id = '<other>'` raises.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: no equivalent. No EF `MigrationBuilder.Sql` calls `CREATE FUNCTION prevent_tenant_id_change`, and no EF model configures `tenant_id` as read-only.
- Contract/behavior: `TenantId` is a plain writable property on every entity. `db.Users.Update(user)` with a tampered `TenantId` succeeds; `db.TenantMemberships.Update(m)` with a new `TenantId` succeeds.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Entities/User.cs (current) — TenantId is plain writable
// (searched: no [Column(ReadOnly=true)], no ValueGeneratedOnAddOrUpdate, no HasValueGenerator)
public Guid? TenantId { get; set; }
```

```csharp
// apps/tamma-elsa/src/Tamma.Data/Entities/TenantMembership.cs (current) L1-L13
public class TenantMembership
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }        // ← writable
    public Guid UserId { get; set; }
    public string Role { get; set; } = "member";
    public DateTime JoinedAt { get; set; }
    // …
}
```

- Dependencies: none.
- Tests: none.

## 3. The gap

Concrete behavioral difference — what an attacker or bug can do.

- TS did: the database itself rejected any `UPDATE ... SET tenant_id = ...` with `ERROR: Cannot change tenant_id on existing row`.
- C# does: `UPDATE` statements can freely reassign rows between tenants. An RLS bypass (finding 003) combined with a membership in another tenant would let a caller rewrite `UPDATE users SET tenant_id = <own_tenant> WHERE id = <victim>` and pull the user into their org.
- For an attacker with SQL injection or an overly permissive repository method, TS raises a plpgsql exception; C# silently succeeds.
- In production, this removes one of the layered defenses specifically called out in Story 17-1 around "unambiguous owner" invariants. Combined with findings 002, 003, 004, the `tenant_id` boundary is effectively advisory.

Error paths:
- TS error path: `ERROR: Cannot change tenant_id on existing row` (SQLSTATE `P0001`).
- C# error path: no error.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-17/17-1-tenant-model-database-schema.md` and `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md`.
- Story's acceptance criteria for this behavior:
  - 17-2 AC 9: "UPDATE operations cannot change the `tenant_id` column (policy on UPDATE restricts both old and new row)".
  - 17-1 intent: "every row in the database has an unambiguous owner".
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

Note: the story described the defense as part of the RLS `WITH CHECK` clause, and the archived migration implements it both via the trigger and via `WITH CHECK` on the UPDATE policy. C# has neither.

## 5. Status

- **Classification**: Not-yet-implemented.
- **What's needed to finish**:
  1. Add `MigrationBuilder.Sql(...)` in the same migration that reinstates RLS (finding 003). Create `prevent_tenant_id_change` function once; attach `BEFORE UPDATE` triggers to every table with a `TenantId` column.
  2. In EF, mark `TenantId` as `ValueGeneratedOnAdd()` and configure it with `Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw)` so EF itself refuses to send `UPDATE tenant_id = ...`. This provides a second layer of defense inside the app before hitting the trigger.
  3. Remove any repository method that sets `entity.TenantId` after creation.
- **Is it "just a stub" or is scope missing?** Scope understood (Story 17-2 AC 9); simply not ported.
- **Blockers**: Best shipped together with finding 003 (RLS migration).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` — add `entity.Property(e => e.TenantId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw)` to every entity with `TenantId`.
- Files to create:
  - Combined into the migration from finding 003: `apps/tamma-elsa/src/Tamma.Data/Migrations/XXXXXXXXXXXX_AddTenantRlsPolicies.cs`.
  - `apps/tamma-elsa/tests/Tamma.Data.Tests/Tenancy/PreventTenantIdChangeTests.cs`.
- Tests to add:
  - `UpdateUser_Throws_WhenTenantIdChanged` (raw SQL expectation: `P0001`).
  - `EfUpdate_ThrowsInvalidOperation_WhenTenantIdPropertyChanged` (EF after-save behavior).
  - `UpdateTenantMembership_DoesNotAllowTenantIdRebinding`.
- Estimated effort: 1.5h broken down as:
  - SQL migration (function + N triggers): 0.5h
  - EF property configuration: 0.25h
  - Tests: 0.75h

## References

- TS source: n/a (schema-side)
- C# source: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs`, all entity classes
- Story: `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md` (AC 9), `docs/stories/epic-17/17-1-tenant-model-database-schema.md`
- Related findings: `003-rls-policies-absent.md`, `002-ef-filter-permissive-null-tenant.md`
- Archived SQL migration: `database/archived-sql-migrations/010_rls_tenant_isolation.sql`, `database/archived-sql-migrations/011_tenant_scoped_stores.sql`
