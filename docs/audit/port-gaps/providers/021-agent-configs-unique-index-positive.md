# Finding 021: `agent_configs.TenantId` unique index — already present (POSITIVE)

**Scope**: providers
**Severity**: None (positive finding; audit-summary assertion re-checked)
**Status**: No gap
**Estimated port effort**: 0h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:database/archived-sql-migrations/013_agent_configs.sql`.

- Archived TS migration declared two partial unique indices: one for non-null `account_id` (tenant row), one for the system default (`account_id IS NULL`).

```sql
-- database/archived-sql-migrations/013_agent_configs.sql — lines 20-28
CREATE UNIQUE INDEX IF NOT EXISTS idx_agent_configs_account_id
  ON agent_configs (account_id)
  WHERE account_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS idx_agent_configs_system_default
  ON agent_configs ((1))
  WHERE account_id IS NULL;
```

- This allowed the TS upsert in `pg-agent-config-store.ts:75-101` to use `ON CONFLICT (account_id) WHERE account_id IS NOT NULL` for tenant rows and `ON CONFLICT ((1)) WHERE account_id IS NULL` for the singleton system default.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:235-250`
- Contract/behavior: The EF entity declares `HasIndex(e => e.TenantId).IsUnique()`. The initial schema migration `20260416172234_InitialSchema.cs` emits the matching unique index DDL.

```csharp
// apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs — lines 235-250
modelBuilder.Entity<AgentConfig>(entity =>
{
    entity.ToTable("agent_configs");
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    entity.Property(e => e.Config).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
    entity.Property(e => e.Version).HasDefaultValue(1);
    entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
    entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

    entity.HasIndex(e => e.TenantId).IsUnique();
    ...
});
```

- The difference from TS is **one full-column unique index** vs TS's **two partial indexes**. Both Postgres indexes prevent duplicates; the C# version uses more index storage (both NULL and non-NULL rows indexed) but tolerates a single `TenantId == NULL` row as the system default (Postgres unique indexes treat `NULL` as distinct from each other by default — which means **multiple `TenantId = NULL` rows CAN exist** in C#, contrary to TS which forced a singleton via `((1))`).

## 3. The gap

- The `/tmp/tamma-audit/31-providers.md` summary line 79 asserted: "`agent_configs.TenantId`: No FK to tenants; no unique index → duplicate rows possible, nondeterministic GetAsync". The **unique index part is wrong** — EF has declared it at line 246 and migrated it.
- The **FK part is correct and separate** — see finding 017 (no cascade FK declared on `AgentConfig.TenantId`).
- The TS-singleton-via-`((1))` semantic is missing: C# permits multiple `TenantId = NULL` system-default rows because Postgres treats NULL as distinct in unique constraints. This is an edge-case consistency bug for the "seed a system default" path.
- For a caller doing `configRepo.UpsertAsync(null, '{...}', null)` twice (seeding system defaults twice, e.g. via a startup hook re-running):
  - TS: `ON CONFLICT ((1))` catches the second insert, does an UPDATE. Row count stays at 1.
  - C#: `AgentConfigRepository.UpsertAsync` at `AgentConfigRepository.cs:13-38` first tries `FirstOrDefaultAsync(c => c.TenantId == tenantId)` — if it finds the existing NULL row, updates; if not (e.g. two concurrent seeders), both insert, producing two NULL rows.

Error paths:
- TS: no error — race-free.
- C#: no error — two concurrent seeders succeed and the system ends up with two system-default rows. `ResolveAsync` then picks one nondeterministically (`apps/tamma-elsa/src/Tamma.Data/Repositories/AgentConfigRepository.cs:60-62` uses `FirstOrDefaultAsync` without `OrderBy`).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-1/9-1-configuration-schema.md`.
- Story 9-1 AC 3: "System defaults (NULL `account_id`) are seeded via migration." Implies the migration is the only seeder and there's one row.
- Story alignment:
  - [x] Matches TS behavior on the main case (one row per tenant).
  - [ ] Matches C# behavior on the system-default edge case (C# allows multiple NULL rows).

## 5. Status

- **Classification**: No gap for the tenant case; minor drift for the system-default case.
- **What's needed to finish**:
  1. (Optional polish) Add a filtered index `CREATE UNIQUE INDEX ... ON agent_configs (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000')) WHERE tenant_id IS NULL`, or enforce single-row system default at application level with a pessimistic lock.
  2. Update the `/tmp/tamma-audit/31-providers.md` summary — original assertion was false.
- **Is it "just a stub" or is scope missing?** Not applicable — the index is present.
- **Blockers**: None. This is primarily a documentation correction.

## Remediation

This finding is informational. No code change required beyond the system-default-singleton polish. Upstream should correct the audit summary.

If the system-default-singleton polish is pursued:

- Files to modify: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:246`
- Files to create: `apps/tamma-elsa/src/Tamma.Data/Migrations/<next>_SingletonSystemDefaultAgentConfig.cs`
- Tests to add: `AgentConfigRepository_UpsertSystemDefaultTwice_ResultsInOneRow`
- Estimated effort: 1h.

## Remediation status

- **Confirmed**: 2026-04-19 by agent
- **Outcome**: Already-fixed (positive finding — kickoff binding clause: "agent_config schema has unique-index hardening from admin-db finding 031. Don't undo.")
- **Commit**: n/a — schema state at `TammaDbContext.cs:268` matches the audit's positive finding (`HasIndex(e => e.TenantId).IsUnique().HasFilter("\"TenantId\" IS NOT NULL")`).
- **Notes**: Verified the partial unique index is intact. The system-default-singleton edge case the audit flagged (multiple NULL rows possible) remains as a polish item but is harmless in current usage — defaults are seeded by the migration, not at runtime.

## References

- TS source: archived `database/archived-sql-migrations/013_agent_configs.sql:20-28`, `packages/api/src/persistence/pg-agent-config-store.ts:75-101` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:235-250`, `apps/tamma-elsa/src/Tamma.Data/Repositories/AgentConfigRepository.cs:13-38`
- Story: `docs/stories/epic-9/story-9-1/9-1-configuration-schema.md` AC 3
- Related findings: `017-sanitization-missing-cascade-fk.md` (FK part of the same class of issue)
- Archived SQL migration: `database/archived-sql-migrations/013_agent_configs.sql`
