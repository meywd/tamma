# Finding 006: Default Tenant Sentinel `00000000-…` Not Seeded

**Scope**: orgs
**Severity**: P1 (feature broken)
**Status**: Not-yet-implemented
**Estimated port effort**: 1h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:database/archived-sql-migrations/008_tenants.sql` and `git show 9e9a57c~1:packages/api/src/persistence/tenant-store.ts`.

- File: `database/archived-sql-migrations/008_tenants.sql:24-27`, `packages/api/src/persistence/tenant-store.ts:37-51`, `packages/shared/src/types/tenant.ts` (exports `DEFAULT_TENANT_ID`).
- Contract/behavior: On first boot, the migration inserts a well-known sentinel tenant with id `00000000-0000-0000-0000-000000000000`, slug `default`, name `Default`. CLI/self-hosted mode, dev-mode requests without a JWT, and existing-data backfills all use this sentinel so nothing has a null tenant. The in-memory store also pre-seeds it in its constructor.
- Key code (verbatim quote, annotated):

```sql
-- database/archived-sql-migrations/008_tenants.sql (archived) L24-L27
-- 2. Insert default tenant sentinel
INSERT INTO tenants (id, name, slug, external_id, plan)
VALUES ('00000000-0000-0000-0000-000000000000', 'Default', 'default', NULL, 'free')
ON CONFLICT (id) DO NOTHING;
```

```typescript
// packages/api/src/persistence/tenant-store.ts (9e9a57c~1) L37-L51 — InMemoryTenantStore
constructor() {
  // Pre-seed the default tenant sentinel
  const now = new Date().toISOString();
  this.tenants.set(DEFAULT_TENANT_ID, {
    id: DEFAULT_TENANT_ID,
    name: 'Default',
    slug: 'default',
    externalId: null,
    plan: 'free',
    settings: {},
    createdAt: now,
    updatedAt: now,
    deletedAt: null,
  });
}
```

```typescript
// packages/api/src/middleware/tenant-context.ts (9e9a57c~1) L75-L77
if (!enableAuth) {
  // CLI/self-hosted/dev mode — use default tenant
  tenantId = DEFAULT_TENANT_ID;
}
```

- Dependencies: `DEFAULT_TENANT_ID` constant in `@tamma/shared`.
- Tests: covered in `tenant-store.test.ts` and `tenant-context.test.ts` (deleted).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: searched `apps/tamma-elsa/src/Tamma.Data/Migrations/*.cs` for `00000000`, `Default`, `sentinel`, and `apps/tamma-elsa/src/Tamma.Data/DependencyInjection.cs` — zero hits.
- Contract/behavior: no default-tenant seed. Fresh database has zero rows in `tenants`. The `EnsurePersonalTenantMiddleware` (see finding 022) creates a per-user personal tenant on first request instead, but there is no fallback for unauthenticated CLI mode or for any code path that needs a "must-belong-to-some-tenant" default.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs (current) L118-L141 — Tenant config
modelBuilder.Entity<Tenant>(entity =>
{
    entity.ToTable("tenants");
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    // …
    // (no HasData seed for the sentinel row)
});
```

```csharp
// apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs (current) L26-L52
// No branch that falls back to a DEFAULT_TENANT_ID.
```

- Dependencies: none reference a sentinel UUID.
- Tests: none.

## 3. The gap

Concrete behavioral difference — what CLI/dev-mode requests see.

- TS did: `DEFAULT_TENANT_ID` was always addressable. CLI runs, self-hosted deployments without SSO, and existing-data backfills pointed at a real row in `tenants` and therefore satisfied every FK constraint.
- C# does: the value `00000000-0000-0000-0000-000000000000` is not a row in the database. Any code path that tries to insert a row with `tenant_id = '00000000-...'` (e.g., the archived backfill logic for `github_installations`, `users.tenant_id` nullable default, `user_api_keys` default, `user_invites` default — all defined with `DEFAULT '00000000-...'` in `008_tenants.sql:32, 44, 50`) would hit a FK violation if those columns had defaults.
- In production, this removes the "escape hatch" for self-hosted deployments. The TS implementation explicitly supported a single-tenant CLI mode; the C# port assumes multi-tenant with per-user personal tenants. This conflicts with AC 12 of Story 17-1: "CLI/self-hosted mode continues to work without any tenant configuration (uses default tenant implicitly)".

Error paths:
- TS error path: n/a — default tenant always resolves.
- C# error path: in any place that references `DEFAULT_TENANT_ID` (none ported, but any future code needing it would 500 on FK violation).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-17/17-1-tenant-model-database-schema.md`.
- Story's acceptance criteria for this behavior:
  - AC 2: "A sentinel 'default' tenant row is inserted with `id = '00000000-0000-0000-0000-000000000000'`, `name = 'Default'`, `slug = 'default'`, `external_id = NULL` for CLI/self-hosted mode".
  - AC 12: "CLI/self-hosted mode continues to work without any tenant configuration (uses default tenant implicitly)".
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Not-yet-implemented. The sentinel was defined in Story 17-1 and seeded by migration 008; no equivalent ported.
- **What's needed to finish**:
  1. Add `modelBuilder.Entity<Tenant>().HasData(new Tenant { Id = Guid.Parse("00000000-0000-0000-0000-000000000000"), Name = "Default", Slug = "default", Plan = "free", Type = "system", CreatedAt = ..., UpdatedAt = ... })` in `TammaDbContext.ConfigureNewEntities`.
  2. Generate an EF migration `AddDefaultTenantSeed` which produces `INSERT INTO tenants ... ON CONFLICT (Id) DO NOTHING`.
  3. Add a `DefaultTenantId` constant to `Tamma.Core` (or a new `Tamma.Shared.Constants`) mirroring the TS `DEFAULT_TENANT_ID`.
  4. Update `TenantContextMiddleware` to fall back to `DefaultTenantId` in CLI/self-hosted mode (`when enableAuth = false` — see finding 023 for the broader fix).
- **Is it "just a stub" or is scope missing?** Scope defined in 17-1; port missed it. No architectural blocker.
- **Blockers**: none. Can ship as a standalone migration.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` (add `HasData` in `Tenant` config).
  - `apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs` (CLI fallback).
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Core/Constants/TenantConstants.cs` (contains `DefaultTenantId`).
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/XXXXXXXXXXXX_SeedDefaultTenant.cs`.
- Tests to add:
  - `GetById_ReturnsDefaultTenant_AfterMigrationApplied`.
  - `TenantContextMiddleware_SetsDefaultTenant_InCliMode`.
- Estimated effort: 1h broken down as:
  - HasData + migration: 0.5h
  - Constant + middleware wiring: 0.25h
  - Tests: 0.25h

## References

- TS source: `database/archived-sql-migrations/008_tenants.sql:24-27`, `packages/api/src/persistence/tenant-store.ts:37-51`, `packages/api/src/middleware/tenant-context.ts:75-77` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:118-141`
- Story: `docs/stories/epic-17/17-1-tenant-model-database-schema.md` (ACs 2, 12)
- Related findings: `023-tenant-context-middleware-shallow.md`, `022-personal-tenant-slug-drift.md`
- Archived SQL migration: `database/archived-sql-migrations/008_tenants.sql`
