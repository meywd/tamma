# Finding 022: `provider_health (ProviderKey, TenantId)` unique — already present (POSITIVE)

**Scope**: providers
**Severity**: None (positive finding; audit-summary assertion re-checked)
**Status**: No gap
**Estimated port effort**: 0h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:database/archived-sql-migrations/015_provider_health.sql`.

- Archived migration 015 declared `key TEXT PRIMARY KEY` on a **single-column primary key** — `key` was already `"provider:model"` format, so uniqueness was on that compound-string. TS had no per-tenant scoping on the health table — the key was global.

```sql
-- database/archived-sql-migrations/015_provider_health.sql — line 10
CREATE TABLE IF NOT EXISTS provider_health (
  key TEXT PRIMARY KEY,
  ...
);
```

- TS upsert: `INSERT INTO provider_health (key, ...) VALUES (...) ON CONFLICT (key) DO UPDATE ...`. The PK made this safe.
- TS did not record per-tenant health separately — if tenant A tripped the circuit for `anthropic:claude-sonnet-4`, tenant B was also affected. **Cross-tenant leakage** of circuit state.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:271-286`
- Contract/behavior: The entity declares `HasIndex(e => new { e.ProviderKey, e.TenantId }).IsUnique()` and uses a Guid PK (`Id`) separate from the natural key. Circuit state is **per-tenant**, fixing TS's cross-tenant leakage.

```csharp
// apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs — lines 271-286
modelBuilder.Entity<ProviderHealth>(entity =>
{
    entity.ToTable("provider_health");
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    entity.Property(e => e.ProviderKey).IsRequired().HasMaxLength(100);
    entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("unknown");
    entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
    entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

    entity.HasIndex(e => new { e.ProviderKey, e.TenantId }).IsUnique();

    var tenantId = _tenantContext?.TenantId;
    entity.HasQueryFilter(e => tenantId == null || e.TenantId == tenantId);
});
```

- `ProviderHealthRepository.GetOrCreateAsync` at `ProviderHealthRepository.cs:21-36` relies on this uniqueness to lookup-or-create. The `CircuitBreakerService` then mutates the returned entity in-place.

## 3. The gap

- The `/tmp/tamma-audit/31-providers.md` summary line 83 asserted: "No unique on `(ProviderKey, TenantId)` — concurrent `RecordFailure` creates duplicate rows → circuit state diverges". This assertion is **wrong** — the unique index exists and has been migrated.
- There is still a concurrency consideration: `GetOrCreateAsync` does a SELECT-then-INSERT without `ON CONFLICT`/`INSERT ON CONFLICT DO NOTHING`. A race between two concurrent `RecordFailureAsync` for the same (key, tenant) where neither has a row yet will:
  - Both SELECT return null.
  - Both call `db.ProviderHealths.Add(new ProviderHealth{...})`.
  - First `SaveChangesAsync` succeeds; second throws `DbUpdateException` (unique violation).
- The second caller will surface a 500 to the user unless `CircuitBreakerService` catches `DbUpdateException` and retries. It does **not** catch it today (`CircuitBreakerService.cs:59-99`).
- This is a cold-start race on a first-failure event — rare but possible. Once the row exists, both writers update the same row and the race vanishes.

For a caller doing two concurrent `POST /health/providers/anthropic:claude-sonnet-4/failure` at cold start:
- TS: both succeed (`ON CONFLICT DO UPDATE` on PK).
- C#: one succeeds, the other returns `500 Internal Server Error`.

Error paths:
- TS: `200 {circuitOpen, failures}` for both.
- C#: `200` for one, `500` for the concurrent one.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-3/9-3-provider-health-tracker.md`.
- Story 9-3 AC 2: "State is shared: a failure recorded by Elsa trips the circuit for the TS engine and vice versa." The TS shape (no tenant column) was overly shared (global). C# per-tenant partitioning is **stronger and better** for multi-tenant SaaS — it matches multi-tenant isolation requirements (Epic 17).
- Story alignment:
  - [x] Matches C# behavior (C# is actually BETTER than TS on this surface).
  - [ ] Matches TS behavior.

## 5. Status

- **Classification**: No gap on the unique-index claim. Minor drift on cold-start race.
- **What's needed to finish**:
  1. (Race-hardening, optional) Wrap `GetOrCreateAsync` + `SaveChangesAsync` in a try/catch for `DbUpdateException` with a reload-and-merge retry.
  2. (Alternative) Switch to Postgres-specific `INSERT ... ON CONFLICT (ProviderKey, TenantId) DO UPDATE RETURNING *` via raw SQL in the repository.
- **Is it "just a stub" or is scope missing?** Not applicable — the index is correct and provides stronger isolation than TS.
- **Blockers**: None.

## Remediation

This finding is informational. The unique index is correctly declared. The cold-start race is a minor hardening item.

- Files to modify (optional hardening): `apps/tamma-elsa/src/Tamma.Api/Services/Providers/CircuitBreakerService.cs:59-99`, `apps/tamma-elsa/src/Tamma.Data/Repositories/ProviderHealthRepository.cs:21-36`
- Tests to add (optional): `CircuitBreaker_ConcurrentColdStartFailure_BothResolveTo200`
- Estimated effort: 1h.

## References

- TS source: archived `database/archived-sql-migrations/015_provider_health.sql:10`, `packages/api/src/services/pg-health-store.ts:117-159` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:271-286`, `apps/tamma-elsa/src/Tamma.Data/Repositories/ProviderHealthRepository.cs:21-36`, `apps/tamma-elsa/src/Tamma.Api/Services/Providers/CircuitBreakerService.cs:59-99`
- Story: `docs/stories/epic-9/story-9-3/9-3-provider-health-tracker.md` AC 2
- Related findings: `026-circuit-breaker-stronger-positive.md`, `013-health-key-validation-missing.md`
- Archived SQL migration: `database/archived-sql-migrations/015_provider_health.sql`
