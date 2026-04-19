# Finding 017: `sanitization_rules.TenantId` has no FK — tenant delete leaves orphans

**Scope**: providers
**Severity**: P3 (data-integrity drift; cleanup regression)
**Status**: Incomplete port
**Estimated port effort**: 0.5h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:database/archived-sql-migrations/016_sanitization_rules.sql`.

- Archived migration `016_sanitization_rules.sql:12`: `account_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE` — deleting a tenant deletes its sanitization rules row.
- Archived migration `013_agent_configs.sql:11` has the same FK on `agent_configs.account_id`.

```sql
-- database/archived-sql-migrations/016_sanitization_rules.sql — line 12
account_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
```

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:305-317`
- Contract/behavior: The EF model declares no `HasOne(e => e.Tenant).WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Cascade)`. `TenantId` is a plain nullable GUID column with no referential integrity.

```csharp
// apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs — lines 305-317
modelBuilder.Entity<SanitizationRule>(entity =>
{
    entity.ToTable("sanitization_rules");
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    entity.Property(e => e.Rules).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
    entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
    entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
    // no HasOne(...)  no OnDelete(Cascade)  no HasIndex(TenantId)
    ...
});
```

- Compare with `TenantMembership` in the same file (`:152-158`) which does wire `HasOne(e => e.Tenant).WithMany().HasForeignKey(e => e.TenantId)` with cascade delete — the pattern exists in the codebase, it just wasn't applied here.
- Similar FK is also missing on `AgentConfig`, `ProviderHealth`, `ProviderDiagnostic`, `BudgetConfig` (if ever added for finding 005).

## 3. The gap

- Deleting a tenant (soft-delete via `tenants.deleted_at` or hard-delete) leaves orphaned `sanitization_rules.tenant_id` pointing at a nonexistent UUID.
- These orphan rows are invisible to query filter (the `HasQueryFilter` only runs `tenantId == null || e.TenantId == tenantId`) but they consume disk space and accumulate over time.
- In practice the Tamma `DELETE /api/v1/tenants/:id` endpoint (if it exists or will exist) would need to manually clean up:
  - `sanitization_rules` where `tenant_id = :id`
  - `agent_configs` where `tenant_id = :id`
  - `provider_health` where `tenant_id = :id`
  - `provider_diagnostics` where `tenant_id = :id`
  - `prompt_overrides` where `tenant_id = :id`
- For a caller doing `DELETE /api/v1/tenants/{tenantId}`:
  - TS: cascade cleans up automatically in the same transaction.
  - C#: tenant row deleted, child rows orphan. Subsequent `CREATE TENANT` with the same id (statistically impossible with UUIDs, but for test fixtures common) would resurrect the stale rows and the new tenant would inherit the old tenant's sanitization rules.

Error paths:
- Silent — no error surface; the bug manifests only at cleanup time.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-7/9-7-content-sanitization.md`, `docs/stories/epic-17/17-1-tenant-model-database-schema.md`, `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md`.
- The tenant-isolation story (`17-2`) implies cascade cleanup as part of tenant lifecycle management.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression).
  - [ ] Matches C# behavior.

## 5. Status

- **Classification**: Incomplete port (schema constraint dropped).
- **What's needed to finish**:
  1. Add `HasOne(...).WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Cascade)` to `SanitizationRule` in `TammaDbContext.cs`.
  2. Do the same for `AgentConfig`, `ProviderHealth`, `ProviderDiagnostic`, and any other tenant-scoped entity that currently lacks it. (Audit `grep "HasIndex(e => e.TenantId)"` for candidates.)
  3. Write a migration that adds the FK constraints. On existing databases with orphan rows, clean them up first (`DELETE FROM sanitization_rules WHERE tenant_id IS NOT NULL AND tenant_id NOT IN (SELECT id FROM tenants)`).
- **Is it "just a stub" or is scope missing?** The FK was simply forgotten — the pattern is well-established elsewhere in the same DbContext.
- **Blockers**: None. Easy fix, low risk.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` (5 entity blocks)
  - `apps/tamma-elsa/src/Tamma.Data/Entities/SanitizationRule.cs` (optional: add `public Tenant? Tenant {get;set;}` nav)
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/<next>_CascadeTenantDeleteOnProviderTables.cs`
- Tests to add:
  - `Tenant_Delete_CascadesSanitizationRuleRows`
  - `Tenant_Delete_CascadesAgentConfigRows`
  - `Tenant_Delete_CascadesProviderHealthRows`
  - `Tenant_Delete_CascadesProviderDiagnosticRows`
- Estimated effort: 30min.

## Remediation status

- **Confirmed**: 2026-04-19 by agent
- **Outcome**: Already-fixed at the schema level
- **Commit**: schema hardening migration `20260419015726_SchemaHardeningPhase1` (landed before this sweep).
- **Notes**: `TammaDbContext.cs:366-369` declares the FK + cascade: `entity.HasOne(e => e.Tenant).WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Cascade)`. `AgentConfig` (line 270) already had it; `ProviderHealth` and `ProviderDiagnostic` use the partial unique + tenant-id partition pattern instead of cascade FKs (the comment on `ProviderDiagnostic` at line 343 explains why — diagnostics is a write-once event sink that may receive rows before the tenant row commits). Tenant deletion now cleanly cascades on the configured tables.

## References

- TS source: archived `database/archived-sql-migrations/013_agent_configs.sql:11`, `014_provider_diagnostics.sql:12`, `016_sanitization_rules.sql:12` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:235-317`
- Story: `docs/stories/epic-17/17-1-tenant-model-database-schema.md`, `docs/stories/epic-17/17-2-row-level-security-tenant-isolation.md`
- Related findings: `016-sanitization-missing-unique-tenant.md`, `015-sanitization-data-model-rewrite.md`
- Archived SQL migrations: `database/archived-sql-migrations/013_agent_configs.sql`, `014_provider_diagnostics.sql`, `016_sanitization_rules.sql`
