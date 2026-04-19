# Finding 023: Default tenant sentinel `00000000-0000-0000-0000-000000000000` not seeded

**Scope**: admin-db
**Severity**: P1
**Status**: Data-model regression
**Estimated port effort**: 1h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Invalid (intentional design divergence — `EnsurePersonalTenantMiddleware` replaces the sentinel)
- **Notes**: Finding's own analysis acknowledges C# diverged to per-user personal tenants on first request. No table column in the C# schema declares `DEFAULT '00000000-…'` or relies on the sentinel; the divergence is consistent end-to-end. Per CLAUDE.md "No migration anxiety", this is the canonical approach and reseeding the sentinel would re-introduce the dual-pattern complexity the port deliberately removed.

## 1. What's in TS

Archived at `database/archived-sql-migrations/008_tenants.sql`.

- File: `packages/api/database/migrations/008_tenants.sql:24-51`
- Contract/behavior: the migration explicitly inserts a well-known sentinel tenant `00000000-0000-0000-0000-000000000000` named "Default" (slug `default`, plan `free`). Several later tables declare `tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000' REFERENCES tenants(id)`. CLI/self-hosted mode relies on this sentinel to give single-user deployments a working tenant without the full registration flow.
- Key code (verbatim quote, annotated):

```sql
-- 008_tenants.sql
-- 2. Insert default tenant sentinel
INSERT INTO tenants (id, name, slug, external_id, plan)
VALUES ('00000000-0000-0000-0000-000000000000', 'Default', 'default', NULL, 'free')
ON CONFLICT (id) DO NOTHING;

-- 3. Add tenant_id to github_installations
ALTER TABLE github_installations
  ADD COLUMN IF NOT EXISTS tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'
  REFERENCES tenants(id);

-- 5. Add tenant_id to user_api_keys
ALTER TABLE user_api_keys
  ADD COLUMN IF NOT EXISTS tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'
  REFERENCES tenants(id);

-- 6. Add tenant_id to user_invites
ALTER TABLE user_invites
  ADD COLUMN IF NOT EXISTS tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'
  REFERENCES tenants(id);
```

- Dependencies: `tenants` table existing before the INSERT.
- Tests that exercised this: CLI smoke tests, fresh-install flows.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:391-409` (tenants table) — no INSERT anywhere in any migration.
- Contract/behavior: the `tenants` table is created empty. `TenantId` columns on `github_installations`, `users`, `user_invites`, `workflow_instances`, `domain_events`, etc. are all **nullable** with no default — a conscious divergence (see findings for those tables). There's no sentinel row. The `EnsurePersonalTenantMiddleware` (`Program.cs:305`) creates a per-user tenant on first request instead, which is a different approach.
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
        Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "personal"),
        OwnerId = table.Column<Guid>(type: "uuid", nullable: true),
        ...
    },
    constraints: table => { table.PrimaryKey("PK_tenants", x => x.Id); });
// No INSERT / seed data.
```

- Dependencies: `EnsurePersonalTenantMiddleware` replaces the sentinel pattern with per-user personal tenants.
- Tests: none assert the sentinel exists.

## 3. The gap

- TS did: seed one well-known tenant row so CLI/self-hosted installs work out-of-the-box, and so tables with `DEFAULT '00000000-…'` FKs have a valid target.
- C# does: leave the table empty; no row has a well-known id; columns with `NOT NULL DEFAULT '00000000-…'` (if any remain, e.g. in `engine_events` — see finding 026) would violate FK on first insert.
- For a caller running the CLI (`tamma start`) against a freshly-migrated DB, TS boots successfully with `tenant_id = '00000000-…'`; C# either fails (if it still expects the sentinel) or creates a personal tenant on the fly (the middleware path).
- In production with existing data / deployed clients, this means:
  - Any ported code or SQL that still assumes `'00000000-…'` exists will FK-fail.
  - Dashboards linking to "the default tenant" have no canonical URL.
  - Data imported from the TS era that references `tenant_id = '00000000-…'` will have dangling FKs once we re-enable FK enforcement.

Error paths: FK violation on insert if any path uses the sentinel.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-17/17-1-tenant-model-database-schema.md` (defines sentinel).
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior (the port shifted to personal-tenants-on-demand)
  - [ ] Describes a third behavior
  - [ ] No story

C# intentionally diverged to the `EnsurePersonalTenantMiddleware` pattern. The sentinel is still referenced by older code; the divergence needs either full removal of sentinel references or reseeding.

## 5. Status

- **Classification**: Data-model regression (with a design rationale — see story 18-3)
- **What's needed to finish**:
  1. Decide: keep the sentinel or commit fully to personal tenants.
  2. If keeping: add `migrationBuilder.Sql("INSERT INTO tenants (id, name, slug, plan) VALUES ('00000000-0000-0000-0000-000000000000', 'Default', 'default', 'free') ON CONFLICT DO NOTHING;")`.
  3. If dropping: audit all code and SQL for the literal `00000000-0000-0000-0000-000000000000` and replace with personal-tenant lookups.
- **Is it "just a stub" or is scope missing?** Intentional divergence, underdocumented.
- **Blockers**: product decision.

## Remediation

- Files to modify: `EnsurePersonalTenantMiddleware.cs` (or its absence means decision is personal-tenants).
- Files to create: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260418000003_SeedDefaultTenant.cs` if retaining sentinel.
- Tests to add: assertion that `SELECT COUNT(*) FROM tenants WHERE id = '00000000-0000-0000-0000-000000000000'` is either always 1 or always 0 (explicit decision).
- Estimated effort: 1h if retaining; ~6h if fully purging.

## References

- TS source: `database/archived-sql-migrations/008_tenants.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`
- Story: `docs/stories/epic-17/17-1-tenant-model-database-schema.md`, `docs/stories/epic-18/18-3-organization-tenant-creation.md`
- Related findings: `020`, `026-schema-engine-events-domain-events-rename.md`
