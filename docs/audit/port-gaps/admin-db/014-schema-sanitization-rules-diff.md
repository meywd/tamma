# Finding 014: `sanitization_rules` flattened — 6 typed columns → `Rules jsonb`, UNIQUE(account_id) lost, cascade FK lost

**Scope**: admin-db
**Severity**: P1
**Status**: Data-model regression
**Estimated port effort**: 2h

## 1. What's in TS

Archived at `database/archived-sql-migrations/016_sanitization_rules.sql`.

- File: `packages/api/database/migrations/016_sanitization_rules.sql`
- Contract/behavior: per-account sanitization with typed columns — each switch and list exposed as its own column so SQL queries can filter/aggregate ("how many accounts have `validate_urls=false`?"). UNIQUE constraint on `account_id` enforces one row per account. Cascade delete when the tenant is removed.
- Key code (verbatim quote, annotated):

```sql
-- 016_sanitization_rules.sql
CREATE TABLE IF NOT EXISTS sanitization_rules (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  account_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,             -- ← cascade
  enabled BOOLEAN NOT NULL DEFAULT true,
  extra_injection_patterns TEXT[] DEFAULT '{}',
  blocked_command_patterns TEXT[] DEFAULT '{}',
  max_fetch_size_bytes INTEGER DEFAULT 10485760,
  validate_urls BOOLEAN DEFAULT true,
  gate_actions BOOLEAN DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE (account_id)                                                        -- ← one row per account
);
```

- Dependencies: `tenants(id)` FK with CASCADE.
- Tests that exercised this: sanitization-service tests (story 9-7).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:157-170`
- Contract/behavior: columns collapsed into a single `Rules jsonb` blob. No FK visible in the migration. No UNIQUE on `TenantId`. No indexes.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs (current)
migrationBuilder.CreateTable(
    name: "sanitization_rules",
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
        TenantId = table.Column<Guid>(type: "uuid", nullable: true),
        Rules = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),   // ← single blob
        CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
        UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
    },
    constraints: table => { table.PrimaryKey("PK_sanitization_rules", x => x.Id); });
// No index on TenantId, no UNIQUE, no FK
```

- Dependencies: none visible.
- Tests: none.

## 3. The gap

| Aspect | TS | C# | Impact |
|---|---|---|---|
| Typed columns | 6 typed columns | single `Rules jsonb` | App must serialize/deserialize; SQL-level filtering (`WHERE validate_urls = false`) impossible without JSONB operators |
| `UNIQUE (account_id)` | present | **absent** | Multiple rows per tenant possible; resolver picks arbitrarily |
| FK `ON DELETE CASCADE` | present | **absent** | Orphaned rows when tenant deleted |
| Index on `TenantId` | implicit via UNIQUE | **absent** | Per-tenant lookup scans all rows |

- For a caller updating sanitization via `PUT /api/config/sanitize/rules` twice, TS rejects the second insert (unique violation — must `UPDATE` existing row); C# inserts both; `GET` then returns one, arbitrarily.
- For a caller running a compliance report "list all tenants with `gate_actions = false`", TS runs `WHERE gate_actions = false`; C# runs `WHERE (Rules->>'gateActions')::boolean = false` (casting JSONB at query time) or must fetch + filter in-memory.

Error paths:
- TS: `23505 unique_violation` on duplicate insert; `23503 foreign_key_violation` on invalid tenant.
- C#: silent duplicate insertion; orphaned rows.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/9-7-sanitization-service.md` (if exists) — otherwise the migration header is the spec.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression (uniqueness + typing)
- **What's needed to finish**:
  1. Decide: keep `Rules jsonb` (simpler, loses typed queries) or restore typed columns.
  2. Add UNIQUE constraint on `TenantId` (partial — same pattern as finding 031 for nullable).
  3. Add FK `TenantId → tenants(Id) ON DELETE CASCADE`.
  4. If keeping JSONB, add a GIN index for common predicate filters.
- **Is it "just a stub" or is scope missing?** Deliberate simplification in shape, but correctness constraints dropped.
- **Blockers**: product decision on typed vs JSONB.

## Remediation

- Files to modify: `SanitizationRule.cs` entity if changing shape.
- Files to create: `20260418000020_SanitizationRulesHardening.cs`.
- Tests to add: insert second rule for same tenant → uniqueness violation; delete tenant → cascade-delete rule.
- Estimated effort: 2h.

## References

- TS source: `database/archived-sql-migrations/016_sanitization_rules.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`
- Story: none (migration header is the de facto spec)
- Related findings: `031-schema-agent-configs-diff.md`
