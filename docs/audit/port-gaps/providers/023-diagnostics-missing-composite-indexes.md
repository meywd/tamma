# Finding 023: `provider_diagnostics` missing composite indexes — full-table scans on common queries

**Scope**: providers
**Severity**: P2 (query performance regression)
**Status**: Incomplete port
**Estimated port effort**: 1h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:database/archived-sql-migrations/014_provider_diagnostics.sql`.

- Archived migration 014 declared **seven** indexes sized to support the TS query patterns.

```sql
-- database/archived-sql-migrations/014_provider_diagnostics.sql — lines 32-38
CREATE INDEX IF NOT EXISTS idx_diagnostics_account_created ON provider_diagnostics (account_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_diagnostics_provider      ON provider_diagnostics (provider_name, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_diagnostics_model         ON provider_diagnostics (model, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_diagnostics_event_type    ON provider_diagnostics (event_type, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_diagnostics_engine        ON provider_diagnostics (engine_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_diagnostics_correlation   ON provider_diagnostics (correlation_id) WHERE correlation_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_diagnostics_budget        ON provider_diagnostics (account_id, created_at) WHERE success = true;
```

- `idx_diagnostics_budget` is a **partial** index specifically for the budget query (`SELECT SUM(cost_usd) WHERE account_id = :id AND success = true`).
- `idx_diagnostics_account_created` supports `query(options) ORDER BY created_at DESC LIMIT N` with `account_id` filter — the primary dashboard query pattern.
- `idx_diagnostics_correlation` is partial so the index doesn't bloat with the majority of rows that have `NULL` correlation.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:288-303`
- Contract/behavior: Only **one** index is declared — `(ProviderKey, CreatedAt)`. Six TS indexes are missing.

```csharp
// apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs — lines 288-303
modelBuilder.Entity<ProviderDiagnostic>(entity =>
{
    entity.ToTable("provider_diagnostics");
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    entity.Property(e => e.ProviderKey).IsRequired().HasMaxLength(100);
    entity.Property(e => e.Cost).HasPrecision(18, 6);
    entity.Property(e => e.Success).HasDefaultValue(true);
    entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

    entity.HasIndex(e => new { e.ProviderKey, e.CreatedAt });

    var tenantId = _tenantContext?.TenantId;
    entity.HasQueryFilter(e => tenantId == null || e.TenantId == tenantId);
});
```

- Missing indexes:
  1. `(TenantId, CreatedAt DESC)` — for the tenant-scoped dashboard list.
  2. `WHERE Success = true` partial on `(TenantId, CreatedAt, Cost)` — for budget sum queries.
  3. `CorrelationId` partial — for trace-by-correlation.
  4. `Model` composite — for groupBy=model (finding 009).
  5. `AgentType` — not applicable because the column is also missing (finding 008).
  6. `EventType` — not applicable for the same reason.

## 3. The gap

- Dashboard query `GET /api/providers/diagnostics/query?from=...&to=...` will scan the whole table and sort — `O(N log N)` for a tenant whose diagnostics volume is small but rides in the same table as all other tenants. After one week of production traffic (say 10 tenants × 1M events/week = 10M rows), each dashboard refresh does a seq-scan + sort.
- Budget sum `DiagnosticsRepository.GetCostSumAsync` (`SELECT SUM(cost_usd) FROM provider_diagnostics WHERE tenant_id = ... AND created_at BETWEEN ...`) also seq-scans without a `(TenantId, CreatedAt)` index.
- The one declared index, `(ProviderKey, CreatedAt)`, is useful for "show all calls to anthropic in the last hour" across tenants (cross-tenant, not dashboard-relevant).
- For a caller doing `GET /api/providers/diagnostics/query?limit=50&offset=0` on a 10M-row table:
  - TS: index scan via `idx_diagnostics_account_created`, typically < 20ms.
  - C#: full table scan + sort, 500ms–5s depending on shared_buffers.

Error paths:
- Both return data; only latency differs. Under enough load C# can start timing out HTTP requests.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-2/9-2-provider-diagnostics.md`.
- Story 9-2 embeds the full DDL including the seven indexes (see AC 1).
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS).
  - [ ] Matches C# behavior.

## 5. Status

- **Classification**: Incomplete port.
- **What's needed to finish**:
  1. Extend EF `ProviderDiagnostic` entity model to declare six more indexes.
  2. Write EF migration.
  3. Some indexes depend on missing columns (finding 008); add the columns and indexes together in a single migration.
  4. Use EF Core 9 `HasFilter("\"Success\" = true")` for the partial budget index.
- **Is it "just a stub" or is scope missing?** Scope was clearly understood (archived SQL is explicit); the indexes were simply not ported.
- **Blockers**: Depends on finding 008 for the `AgentType`, `EventType`, `CorrelationId` columns to exist before their indexes can be added.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:288-303` (declare six more `HasIndex`)
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/<next>_DiagnosticsCompositeIndexes.cs`
- Tests to add:
  - `DiagnosticsRepository_TenantCreatedAtQuery_UsesIndex` (EXPLAIN-based assertion or warm-cache benchmark)
  - `DiagnosticsRepository_BudgetSumQuery_FastPath`
  - `DiagnosticsRepository_CorrelationIdLookup_UsesPartialIndex`
- Estimated effort: 1h (mostly EF boilerplate; depends on finding 008).

## Remediation status

- **Confirmed**: 2026-04-19 by agent
- **Outcome**: Already-fixed at the schema level
- **Commit**: schema hardening migration `20260419015726_SchemaHardeningPhase1` (landed before this sweep).
- **Notes**: `TammaDbContext.cs:331-341` declares the missing indexes — `(ProviderKey, CreatedAt)` (was already there), `(TenantId, CreatedAt)`, `(EngineId, CreatedAt)`, `(Model, CreatedAt)`, `(RequestType, CreatedAt)`, partial `CorrelationId` (filter `"CorrelationId" IS NOT NULL`). The TS partial budget index `(account_id, created_at) WHERE success = true` is **deferred** as the application path uses the `(TenantId, CreatedAt)` index already and `Success` is part of the standard sort/filter set; can be added later if EXPLAIN ANALYZE shows the budget query is slow on production volume.

## References

- TS source: archived `database/archived-sql-migrations/014_provider_diagnostics.sql:32-38`, `packages/api/src/services/pg-diagnostics-store.ts:102-232` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:288-303`
- Story: `docs/stories/epic-9/story-9-2/9-2-provider-diagnostics.md` AC 1 (DDL block)
- Related findings: `008-diagnostics-taxonomy-collapsed.md`, `009-diagnostics-report-groupby-dropped.md`, `004-cost-accounting-hardcoded-zero.md`
- Archived SQL migration: `database/archived-sql-migrations/014_provider_diagnostics.sql`
