# Finding 033: `provider_health` diff — key→Id uuid PK, circuit_open bool→Status string, no CHECK, no partial index

**Scope**: admin-db
**Severity**: P2
**Status**: Data-model regression
**Estimated port effort**: 1.5h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Notes**: `Phase1` migration adds (a) `ck_provider_health_status CHECK Status IN ('healthy','degraded','down','unknown')` matching what `CircuitBreakerService` actually writes (NOT TS's open/closed/half-open), (b) `ix_provider_health_open ON ProviderKey WHERE Status = 'down'` partial index, (c) replaces the plain unique on `(ProviderKey, TenantId)` with a partial unique `WHERE TenantId IS NOT NULL` plus a companion `ix_provider_health_system_default ON ProviderKey WHERE TenantId IS NULL` enforcing one global row per provider key.

## 1. What's in TS

Archived at `database/archived-sql-migrations/015_provider_health.sql`.

- File: `packages/api/database/migrations/015_provider_health.sql`
- Contract/behavior: circuit breaker state for each provider+key combination. PK is a natural `key TEXT` (provider-scoped identifier). Circuit state is tracked with a BOOLEAN `circuit_open` + TIMESTAMPTZ `circuit_open_until`. One partial index `WHERE circuit_open = true` for fast "which circuits are open?" queries.
- Key code (verbatim quote, annotated):

```sql
-- 015_provider_health.sql
CREATE TABLE IF NOT EXISTS provider_health (
  key TEXT PRIMARY KEY,                                                   -- ← natural PK
  circuit_open BOOLEAN NOT NULL DEFAULT false,                            -- ← boolean
  circuit_open_until TIMESTAMPTZ,
  failure_count INTEGER NOT NULL DEFAULT 0,
  last_failure_at TIMESTAMPTZ,
  last_success_at TIMESTAMPTZ,
  half_open_in_progress BOOLEAN NOT NULL DEFAULT false,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_provider_health_open
  ON provider_health (circuit_open)
  WHERE circuit_open = true;                                              -- ← partial
```

- Dependencies: none (no FK).
- Tests that exercised this: circuit-breaker tests.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs:138-155, 600-604`, plus `20260416192411_ProviderHealthCircuitBreakerState.cs` (adds `CircuitOpenUntil`, `FailureWindowStart`, `HalfOpenInProgress`).
- Contract/behavior: surrogate `Id` uuid PK. `ProviderKey` demoted to a non-unique key, uniqueness enforced via `(ProviderKey, TenantId)` compound unique index. `Status` is `character varying(20)` with no CHECK — replaces `circuit_open BOOLEAN`. No partial index on open circuits. Adds `TenantId` (tenant-scoped circuit breaker).
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs (current)
migrationBuilder.CreateTable(
    name: "provider_health",
    columns: table => new
    {
        Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),     // ← surrogate PK
        ProviderKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
        Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "unknown"),  // ← string enum, no CHECK
        LastSuccess = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
        LastFailure = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
        FailureCount = table.Column<int>(type: "integer", nullable: false),
        TenantId = table.Column<Guid>(type: "uuid", nullable: true),                                        // ← new
        CreatedAt = ..., UpdatedAt = ...
    },
    constraints: table => { table.PrimaryKey("PK_provider_health", x => x.Id); });

migrationBuilder.CreateIndex(
    name: "IX_provider_health_ProviderKey_TenantId",
    table: "provider_health",
    columns: new[] { "ProviderKey", "TenantId" },
    unique: true);
// No partial index on Status = 'open'
```

The 20260416192411 migration adds `CircuitOpenUntil`, `FailureWindowStart`, `HalfOpenInProgress` as follow-ups.

- Dependencies: none.
- Tests: none.

## 3. The gap

| Aspect | TS | C# | Impact |
|---|---|---|---|
| PK | natural `key TEXT` | surrogate `Id uuid` | Extra FK indirection |
| `circuit_open BOOLEAN` | yes/no | **`Status` string with no CHECK** | Any string can be inserted; app must agree on the vocabulary (`"open"/"closed"/"half-open"/"unknown"`) |
| `(ProviderKey, TenantId)` uniqueness on nullable TenantId | — (single key, TenantId didn't exist) | unique with nullable — multiple NULL-tenant rows permitted (same issue as 031) | Multiple "global" circuit breaker rows for one provider key |
| Partial index on open circuits | `WHERE circuit_open = true` | **absent** | "Which circuits are open?" scans all rows |
| `HalfOpenInProgress` | present | present (via follow-up migration) | OK |

- For a caller running `SELECT key FROM provider_health WHERE circuit_open = true`, TS hits the partial index; C# must scan the full table and filter.
- For a caller inserting `Status = "banana"`, TS's BOOLEAN column rejects non-boolean; C# silently accepts.

Error paths:
- TS: type mismatch on invalid state.
- C#: silent acceptance.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/9-3-provider-health-tracker.md`.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Data-model regression + semantic rewrite
- **What's needed to finish**:
  1. Add CHECK constraint: `Status IN ('closed','open','half-open','unknown')`.
  2. Add partial index: `ON provider_health (ProviderKey) WHERE "Status" = 'open'`.
  3. Fix the nullable-`TenantId` uniqueness via partial unique indexes (same pattern as finding 031).
- **Is it "just a stub" or is scope missing?** Partial port.
- **Blockers**: none.

## Remediation

- Files to modify: none existing.
- Files to create: `20260418000019_ProviderHealthHardening.cs`.
- Tests to add: invalid status → CHECK violation; partial index hit via `EXPLAIN`.
- Estimated effort: 1.5h.

## References

- TS source: `database/archived-sql-migrations/015_provider_health.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Migrations/20260416172234_InitialSchema.cs`, `20260416192411_ProviderHealthCircuitBreakerState.cs`
- Story: `docs/stories/epic-9/9-3-provider-health-tracker.md`
- Related findings: `031-schema-agent-configs-diff.md`
