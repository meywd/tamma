# Finding 027: `tenant_memberships` diff — composite PK→surrogate, role CHECK lost

**Scope**: admin-db
**Severity**: P2
**Status**: Data-model regression
**Estimated port effort**: 1h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed (CHECK added; surrogate PK retained)
- **Notes**: `Phase1` migration adds `ck_tenant_memberships_role`. Surrogate PK kept — same convention as the rest of the C# port; the unique compound index on `(TenantId, UserId)` enforces the same uniqueness invariant.

## 1. What's in TS

Archived at `database/archived-sql-migrations/017_tenant_memberships.sql`.

- File: `packages/api/database/migrations/017_tenant_memberships.sql:7-16`
- Contract/behavior: M:N relationship between tenants and users, composite natural PK `(tenant_id, user_id)`, role CHECK, indexed lookup by `user_id`.
- Key code (verbatim quote, annotated):

```sql
-- 017_tenant_memberships.sql
CREATE TABLE IF NOT EXISTS tenant_memberships (
  tenant_id   UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  role        TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('owner', 'admin', 'member')),
  joined_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (tenant_id, user_id)                                                   -- ← composite PK
);

CREATE INDEX IF NOT EXISTS idx_tenant_memberships_user_id ON tenant_memberships(user_id);
```

- Dependencies: `tenants`, `users`.
- Tests that exercised this: membership flows in story 18-3.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:375-388, 628-636, 725-739`
- Contract/behavior: introduces a surrogate `Id` uuid PK, moves the composite key to a unique secondary index. Lost the role CHECK.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs (current)
migrationBuilder.CreateTable(
    name: "tenant_memberships",
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),  // ← surrogate PK
        TenantId = table.Column<Guid>(type: "uuid", nullable: false),
        UserId = table.Column<Guid>(type: "uuid", nullable: false),
        Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "member"),  // ← no CHECK
        JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
    },
    constraints: table => { table.PrimaryKey("PK_tenant_memberships", x => x.Id); });

migrationBuilder.CreateIndex(
    name: "IX_tenant_memberships_TenantId_UserId",
    table: "tenant_memberships",
    columns: new[] { "TenantId", "UserId" },
    unique: true);  // ← composite now a unique index, not PK
migrationBuilder.CreateIndex(
    name: "IX_tenant_memberships_UserId", table: "tenant_memberships", column: "UserId");

migrationBuilder.AddForeignKey(
    name: "FK_tenant_memberships_tenants_TenantId", ... onDelete: ReferentialAction.Cascade);
migrationBuilder.AddForeignKey(
    name: "FK_tenant_memberships_users_UserId", ... onDelete: ReferentialAction.Cascade);
```

- Dependencies: `tenants`, `users` (both cascade-delete).
- Tests: none assert the CHECK behavior.

## 3. The gap

| Aspect | TS | C# | Impact |
|---|---|---|---|
| PK | composite `(tenant_id, user_id)` | surrogate `Id uuid` + unique index | Slightly larger footprint; joins are surrogate-based |
| `role` CHECK | `IN ('owner','admin','member')` | none | Arbitrary roles possible |

- For a caller inserting `(tenant_id=X, user_id=Y, role='member')` twice, both TS and C# reject (PK conflict in TS, unique-index conflict in C#) — net equivalent.
- For a caller inserting `role = "janitor"`, TS rejects (CHECK), C# accepts, and downstream RBAC treats it as "role not recognized, no permissions" — silent demotion to zero privileges.

Error paths:
- TS: CHECK violation on invalid role; PK violation on duplicate.
- C#: unique-index violation on duplicate; silent on invalid role.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression (CHECK) + semantic rewrite (surrogate PK — acceptable)
- **What's needed to finish**:
  1. Add CHECK constraint on `Role`.
- **Is it "just a stub" or is scope missing?** Partial port.
- **Blockers**: none.

## Remediation

- Files to modify: none existing.
- Files to create: `20260418000010_TenantMembershipRoleCheck.cs`.
- Tests to add: insert role `"janitor"` → expect CHECK violation.
- Estimated effort: 1h.

## References

- TS source: `database/archived-sql-migrations/017_tenant_memberships.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`
- Related findings: `028-schema-tenant-invites-table-absent.md`
