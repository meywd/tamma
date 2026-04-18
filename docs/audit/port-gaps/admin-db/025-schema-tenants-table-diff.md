# Finding 025: `tenants` table diff — lost plan CHECK, no sentinel seed

**Scope**: admin-db
**Severity**: P2
**Status**: Data-model regression
**Estimated port effort**: 1h

## 1. What's in TS

Archived at `database/archived-sql-migrations/008_tenants.sql`.

- File: `packages/api/database/migrations/008_tenants.sql:9-27`
- Contract/behavior: narrow `tenants` table with a `plan` CHECK constraint restricting values to `free/pro/enterprise`, a sentinel row seeded at migration time, and partial indexes `WHERE deleted_at IS NULL` for soft-delete filtering.
- Key code (verbatim quote, annotated):

```sql
-- 008_tenants.sql
CREATE TABLE IF NOT EXISTS tenants (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name          TEXT NOT NULL,
  slug          TEXT UNIQUE NOT NULL,
  external_id   TEXT UNIQUE,
  plan          TEXT NOT NULL DEFAULT 'free' CHECK (plan IN ('free', 'pro', 'enterprise')),
  settings      JSONB NOT NULL DEFAULT '{}',
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  deleted_at    TIMESTAMPTZ
);
CREATE INDEX IF NOT EXISTS idx_tenants_deleted_at ON tenants (deleted_at) WHERE deleted_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_tenants_external_id ON tenants (external_id) WHERE external_id IS NOT NULL;

INSERT INTO tenants (id, name, slug, external_id, plan)
VALUES ('00000000-0000-0000-0000-000000000000', 'Default', 'default', NULL, 'free')
ON CONFLICT (id) DO NOTHING;
```

- Dependencies: `gen_random_uuid()` (pgcrypto).
- Tests that exercised this: fresh-install smoke tests.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:391-410, 638-655`
- Contract/behavior: wider `tenants` table (adds `Type` and `OwnerId`), but no CHECK constraint on `Plan`, no seed row. Indexes on `ExternalId` and `Slug` include the soft-delete filter (good), plus an `IX_tenants_OwnerId` index (new).
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs (current)
migrationBuilder.CreateTable(
    name: "tenants",
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
        Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
        Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
        Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "personal"),  // ← new
        OwnerId = table.Column<Guid>(type: "uuid", nullable: true),  // ← new
        ExternalId = table.Column<string>(type: "text", nullable: true),
        Plan = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "free"),  // ← no CHECK
        Settings = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
        ...
    },
    constraints: table => { table.PrimaryKey("PK_tenants", x => x.Id); });

migrationBuilder.CreateIndex(
    name: "IX_tenants_ExternalId", table: "tenants", column: "ExternalId",
    unique: true, filter: "\"ExternalId\" IS NOT NULL AND \"DeletedAt\" IS NULL");
migrationBuilder.CreateIndex(
    name: "IX_tenants_Slug", table: "tenants", column: "Slug",
    unique: true, filter: "\"DeletedAt\" IS NULL");
```

No `INSERT` statement anywhere in the migration body.

- Dependencies: later FK to `users.OwnerId` (set nullable for soft-delete).
- Tests: none assert the constraints.

## 3. The gap

| Aspect | TS | C# | Impact |
|---|---|---|---|
| `plan` CHECK | `IN ('free','pro','enterprise')` | none | Arbitrary strings accepted |
| Default sentinel seed | inserted | not inserted | See finding 023 |
| `Type`, `OwnerId` columns | absent | present | New fields for personal-tenant pattern (net positive, but undocumented in migrations/stories) |
| `idx_tenants_deleted_at` partial index | explicit index on `deleted_at WHERE deleted_at IS NULL` | absent | Slightly slower soft-delete scans (low impact) |

- For a caller creating a tenant with `plan = "custom"`, TS raises CHECK violation; C# accepts silently. Billing reports grouping by plan then have a new bucket "custom" that doesn't correspond to a billing tier.
- In production: minor. The CHECK is belt-and-suspenders vs. app-layer enum validation, but valuable because raw SQL and data migrations bypass app layers.

Error paths:
- TS: `23514 check_violation` on invalid plan.
- C#: silent insertion.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-17/17-1-tenant-model-database-schema.md`.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression
- **What's needed to finish**:
  1. Add `CHECK` constraint on `Plan` via raw SQL: `ALTER TABLE tenants ADD CONSTRAINT ck_tenants_plan CHECK (plan IN ('free','pro','enterprise'));`.
  2. Resolve sentinel seed decision (finding 023).
  3. Add `idx_tenants_deleted_at` partial index if ORM queries frequently filter on it.
- **Is it "just a stub" or is scope missing?** Partial port; hardening dropped.
- **Blockers**: none.

## Remediation

- Files to modify: none existing.
- Files to create: `20260418000005_TenantPlanCheck.cs`.
- Tests to add: insert plan `"foo"` → expect `23514`; list plan distinct values is exactly `{free, pro, enterprise}`.
- Estimated effort: 1h.

## References

- TS source: `database/archived-sql-migrations/008_tenants.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`
- Story: `docs/stories/epic-17/17-1-tenant-model-database-schema.md`
- Related findings: `023-schema-default-tenant-sentinel.md`
