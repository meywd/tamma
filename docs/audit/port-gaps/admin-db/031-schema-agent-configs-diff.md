# Finding 031: `agent_configs` diff — account_id→TenantId, non-partial unique on nullable column

**Scope**: admin-db
**Severity**: P2
**Status**: Data-model regression
**Estimated port effort**: 1h

## 1. What's in TS

Archived at `database/archived-sql-migrations/013_agent_configs.sql`.

- File: `packages/api/database/migrations/013_agent_configs.sql`
- Contract/behavior: one row per account, with a seeded system-default row where `account_id IS NULL`. Two **partial unique indexes** handle the NULL vs non-NULL uniqueness: one ensures at most one system default (`WHERE account_id IS NULL`), the other ensures one row per non-NULL account.
- Key code (verbatim quote, annotated):

```sql
-- 013_agent_configs.sql
CREATE TABLE IF NOT EXISTS agent_configs (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  account_id    UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
  config        JSONB NOT NULL,
  version       INTEGER NOT NULL DEFAULT 1,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  created_by    UUID NULL, updated_by    UUID NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_agent_configs_account_id
  ON agent_configs (account_id)
  WHERE account_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_agent_configs_system_default
  ON agent_configs ((1))                                           -- ← constant-expression index
  WHERE account_id IS NULL;

-- Seed system default
INSERT INTO agent_configs (account_id, config, version) VALUES (NULL, '{...}'::jsonb, 1) ON CONFLICT DO NOTHING;
```

- Dependencies: `tenants(id)` FK with CASCADE.
- Tests that exercised this: agent-resolver unit tests.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:15-31, 469-473`
- Contract/behavior: renamed `account_id` → `TenantId` (consistent naming, fine). Single `unique: true` index on `TenantId`. No partial index discriminating NULL from non-NULL. No seed row (defaults live in application code).
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs (current)
migrationBuilder.CreateTable(
    name: "agent_configs",
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
        TenantId = table.Column<Guid>(type: "uuid", nullable: true),             // ← renamed from account_id
        Config = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
        Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
        CreatedAt = ..., UpdatedAt = ...,
        CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
        UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
    },
    constraints: table => { table.PrimaryKey("PK_agent_configs", x => x.Id); });

migrationBuilder.CreateIndex(
    name: "IX_agent_configs_TenantId",
    table: "agent_configs",
    column: "TenantId",
    unique: true);                    // ← plain unique on nullable column
// No partial index, no FK.
```

- Dependencies: no FK visible; no seed row.
- Tests: none.

## 3. The gap

- TS did: enforce at most one system default row (`account_id IS NULL`) AND at most one row per account.
- C# does: `unique: true` on a nullable column — in Postgres, **multiple rows with `NULL` are allowed** because `NULL != NULL`. This means multiple "system default" rows (`TenantId IS NULL`) can coexist, each with a different `Config`. The agent resolver picks one at random via EF ordering.
- For a caller seeding two system defaults (which the code paths in `AgentResolverService` may do accidentally on app startup), TS rejects the second via the partial unique index; C# accepts both, and subsequent reads return whichever row EF picks first.
- In production with existing data / deployed clients, this means:
  - Running `POST /api/v1/agents/config` with a null tenant (system-wide) twice produces two rows.
  - The "system default" agent config is non-deterministic.
  - Deleting a tenant does not cascade-delete the tenant's row (no FK).

Error paths:
- TS: `23505 unique_violation` on duplicate system default.
- C#: silent insert of duplicates.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/9-1-configuration-schema.md` (agent config).
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression
- **What's needed to finish**:
  1. Drop `IX_agent_configs_TenantId` as-is.
  2. Add two partial unique indexes: one on `TenantId WHERE "TenantId" IS NOT NULL` and one on a constant expression `WHERE "TenantId" IS NULL`.
  3. Add FK on `TenantId` → `tenants(Id)` with `ON DELETE CASCADE`.
- **Is it "just a stub" or is scope missing?** Partial port — critical uniqueness constraint broken silently.
- **Blockers**: may need to deduplicate existing duplicate-system-default rows first.

## Remediation

- Files to modify: none existing.
- Files to create: `20260418000017_AgentConfigsPartialUnique.cs` using `migrationBuilder.Sql(@"CREATE UNIQUE INDEX ... WHERE ...");`.
- Tests to add: insert two NULL-tenant rows → expect uniqueness violation; delete tenant → cascade-delete row.
- Estimated effort: 1h.

## References

- TS source: `database/archived-sql-migrations/013_agent_configs.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`
- Story: `docs/stories/epic-9/9-1-configuration-schema.md`
- Related findings: none
