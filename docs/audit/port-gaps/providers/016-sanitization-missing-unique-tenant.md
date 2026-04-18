# Finding 016: `sanitization_rules` has no `UNIQUE(TenantId)` — duplicate rule rows permitted

**Scope**: providers
**Severity**: P2 (data-integrity drift)
**Status**: Incomplete port
**Estimated port effort**: 1h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:database/archived-sql-migrations/016_sanitization_rules.sql`.

- Archived migration `016_sanitization_rules.sql:22` declares `UNIQUE (account_id)` at the table level. This is what makes the TS upsert logic in `pg-sanitization-store.ts:100-154` safe — `ON CONFLICT (account_id) DO UPDATE` depends on the uniqueness.
- For the `account_id IS NULL` system-default row, the TS upsert uses `ON CONFLICT (account_id) WHERE account_id IS NULL` (partial conflict target).

```sql
-- database/archived-sql-migrations/016_sanitization_rules.sql — lines 10-22
CREATE TABLE IF NOT EXISTS sanitization_rules (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  account_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
  enabled BOOLEAN NOT NULL DEFAULT true,
  extra_injection_patterns TEXT[] DEFAULT '{}',
  blocked_command_patterns TEXT[] DEFAULT '{}',
  max_fetch_size_bytes INTEGER DEFAULT 10485760,
  validate_urls BOOLEAN DEFAULT true,
  gate_actions BOOLEAN DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE (account_id)
);
```

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:305-317`
- Contract/behavior: The `SanitizationRule` entity is declared with a `Guid PK`, a query filter, and no unique index on `TenantId`.

```csharp
// apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs — lines 305-317
// ── SanitizationRule ──
modelBuilder.Entity<SanitizationRule>(entity =>
{
    entity.ToTable("sanitization_rules");
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    entity.Property(e => e.Rules).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
    entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
    entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

    var tenantId = _tenantContext?.TenantId;
    entity.HasQueryFilter(e => tenantId == null || e.TenantId == tenantId);
});
```

- `SanitizationRepository.LoadOrCreateRowAsync:156-172` fetches **the first row** matching `TenantId` and only creates a new one when none exists. Concurrent callers can race and both see "no row exists", both insert, and produce two rows for the same tenant.
- `SanitizationRepository.GetRulesAsync:68-83`'s call `FirstOrDefaultAsync(r => r.TenantId == tenantId)` returns one of the duplicate rows nondeterministically — the tenant sees their sanitization policy "randomly" change between calls once a race has occurred.

## 3. The gap

- Without the unique index, the upsert pattern is racy. Two concurrent `PUT /sanitize/rules` requests for the same tenant (e.g. two admins click "Save" at the same time, or a retry arrives while the first request is still in `SaveChangesAsync`) will insert two rows.
- Subsequent `GET /sanitize/rules` returns an arbitrary one of them — the one EF's query planner picks. The tenant might see their edit, or they might see the older row.
- For a caller doing `PUT /sanitize/rules` twice concurrently with the same body:
  - TS: one INSERT + one UPDATE via `ON CONFLICT (account_id) DO UPDATE`. Final state has exactly one row with the latest `updated_at`.
  - C#: two INSERTs (both saw no existing row). Final state has two rows. Subsequent reads return an arbitrary row.

Error paths:
- TS: race-free — the DB UNIQUE enforces convergence.
- C#: no errors; silent data corruption.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-7/9-7-content-sanitization.md`.
- Story 9-7 AC 2 implies one-row-per-account: "Sanitization rules stored in Postgres per account with system default fallback." Archived SQL makes this explicit with `UNIQUE (account_id)`.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression).
  - [ ] Matches C# behavior.

## 5. Status

- **Classification**: Incomplete port (schema constraint dropped).
- **What's needed to finish**:
  1. Add `entity.HasIndex(e => e.TenantId).IsUnique();` to the EF model.
  2. Write a migration that:
     a. Deduplicates any existing rows (`DELETE … USING (SELECT …) dupes WHERE sanitization_rules.id <> dupes.keep_id AND sanitization_rules.tenant_id = dupes.tenant_id`).
     b. Creates the unique index.
  3. Refactor `SanitizationRepository.LoadOrCreateRowAsync` to catch `DbUpdateException` from the unique violation and reload on conflict (cleaner upsert-or-fetch pattern than the current get-or-create).
  4. Consider adding the partial-index shape for the `TenantId IS NULL` system default (mirrors archived TS `ON CONFLICT (account_id) WHERE account_id IS NULL` pattern).
- **Is it "just a stub" or is scope missing?** The index was simply forgotten.
- **Blockers**: None.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:305-317`
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/SanitizationRepository.cs:156-172`
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/<next>_UniqueSanitizationTenantId.cs`
- Tests to add:
  - `Sanitization_ConcurrentUpsert_ResultsInOneRow`
  - `Sanitization_UniqueIndexMigration_DeduplicatesExistingRows`
- Estimated effort: 1h.

## References

- TS source: archived `database/archived-sql-migrations/016_sanitization_rules.sql:22`, `packages/api/src/services/pg-sanitization-store.ts:100-154` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:305-317`, `apps/tamma-elsa/src/Tamma.Data/Repositories/SanitizationRepository.cs:156-172`
- Story: `docs/stories/epic-9/story-9-7/9-7-content-sanitization.md` AC 2
- Related findings: `015-sanitization-data-model-rewrite.md`, `017-sanitization-missing-cascade-fk.md`
- Archived SQL migration: `database/archived-sql-migrations/016_sanitization_rules.sql`
