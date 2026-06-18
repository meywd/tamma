# Story 36-1: Dimensional Analytics Projection Schema & Store

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase workflow (Read → Research → Break Down → TDD →
Quality Gates → Failure Handling), the `.dev/` knowledge base, TRACE/DEBUG logging
requirements, test-first development, and the build-success / coverage gates.

## User Story

As a **tenant administrator** (SaaS) **/ self-hosted owner** (single-user),
I want my usage, cost, and workflow performance facts stored per-tenant at hourly and daily
grain with the dimensions that matter — provider, agent, workflow definition, repo, and a
BYOK-vs-platform cost discriminator,
so that every tenant-facing dashboard, export, and scheduled report in Epic 36 reads from one
isolated, queryable, multi-dimensional analytics store instead of re-aggregating the raw DCB
event stream on every request.

## Priority

P0 - The schema is the foundation the rest of Epic 36 (population, query API, exports, reports)
builds on. Nothing in the epic ships until the fact tables and their EF model exist.

## Scope

Schema + EF model + migration **only**. This story defines and migrates the per-tenant
`AnalyticsUsageHourly` and `AnalyticsUsageDaily` fact tables (in each tenant schema, via the
`TenantDbContext` migration graph), wires them into the EF model, and proves InMemory/Npgsql
parity and migration application. **Population is out of scope** — Story 36-2 owns the
projection pipeline that fills these tables from DCB events. The control-plane
`platform_analytics_hourly` fact table (Story 28-10) is left **entirely intact** for
platform-owner business analytics.

## Acceptance Criteria

1. A new per-tenant EF entity `AnalyticsUsageHourly` is added at
   `apps/tamma-elsa/src/Tamma.Data/Entities/AnalyticsUsageHourly.cs` with dimension columns
   `Hour` (UTC top-of-hour, `timestamp with time zone`), `Provider` (string, required),
   `AgentId` (nullable string), `WorkflowDefinitionId` (nullable `Guid`), `RepoId` (nullable
   string), and `CostBasis` (enum `byok|platform`), plus measure columns `TokensIn`,
   `TokensOut`, `WorkflowsStarted`, `WorkflowsCompleted`, `WorkflowsFailed`, `AgentDispatches`
   (all `long`), `CostUsd` and `PlatformBilledUsd` (both `decimal(20,4)`), and a `ComputedAt`
   write timestamp.

2. A rolled-up per-tenant EF entity `AnalyticsUsageDaily` is added at
   `apps/tamma-elsa/src/Tamma.Data/Entities/AnalyticsUsageDaily.cs` carrying the identical
   dimension + measure shape, except the time bucket is `Day` (UTC midnight,
   `timestamp with time zone`) instead of `Hour`. The two entities share their dimension and
   measure contract exactly so the daily roll-up (Story 36-2) is a lossless `GROUP BY` of the
   hourly grain.

3. A new `CostBasis` enum is added at
   `apps/tamma-elsa/src/Tamma.Core/Enums/CostBasis.cs` with members `Byok` and `Platform`, and
   is mapped to a Postgres `text` column via `.HasConversion<string>()` so a `byok`/`platform`
   discriminator round-trips identically on both the InMemory and Npgsql providers (mirrors the
   `MentorshipState`/`SessionStatus` `HasConversion<string>()` precedent in
   `TammaModelConfiguration.ConfigureMentorshipEntities`).

4. Both entities are exposed as `DbSet`s on `TenantDbContext`
   (`apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs`) — `AnalyticsUsageHourly` and
   `AnalyticsUsageDaily` — and are configured exclusively through a new
   `TammaModelConfiguration.ConfigureAnalyticsUsageEntities(...)` invoked from
   `ConfigureTenantEntities`, mapping to tables `analytics_usage_hourly` and
   `analytics_usage_daily`. They are **NOT** added to `ControlPlaneDbContext`, proving
   per-tenant data isolation (AC tied to the spec: tenant schema only).

5. The control-plane `platform_analytics_hourly` table, its entity
   (`Tamma.Data.Entities.PlatformAnalyticsHourly`), and `ControlPlaneDbContext` mapping are
   left unchanged — a `ControlPlaneDbContextModelTests` assertion confirms the CP model graph
   still maps `platform_analytics_hourly` and does **not** map `analytics_usage_*`.

6. Each fact table carries a composite breakdown index on
   `(Hour|Day, Provider, AgentId, WorkflowDefinitionId, CostBasis)` named
   `IX_analytics_usage_hourly_breakdown` / `IX_analytics_usage_daily_breakdown` to serve the
   per-dimension breakdown queries the Epic 36 query API will run.

7. Each fact table enforces idempotent upsert with a **unique** business-key index over the
   full dimension tuple `(Hour|Day, Provider, AgentId, WorkflowDefinitionId, RepoId, CostBasis)`
   using `.AreNullsDistinct(false)` (NULLS NOT DISTINCT, PG15+; production runs PG17) so the
   nullable `AgentId`/`WorkflowDefinitionId`/`RepoId` dimensions dedupe on NULL exactly like
   `prompt_overrides` / `conventions` do — one row per dimension-tuple per bucket. Index names:
   `UX_analytics_usage_hourly_dims` / `UX_analytics_usage_daily_dims`.

8. Measure columns use `long` for counters and `decimal` with `HasPrecision(20, 4)` for
   `CostUsd` and `PlatformBilledUsd`, matching `PlatformAnalyticsHourly` conventions so values
   round-trip losslessly between the two stores when an owner-side query joins/compares them.

9. All counter measures default to `0L`, both decimals default to `0m`, `ComputedAt` defaults
   to `now()`, and `Id` defaults to `gen_random_uuid()` — mirroring the
   `ConfigurePlatformAnalyticsHourly` defaults so a partial upsert never writes a NULL measure.

10. An EF model test asserts that **both** the InMemory provider and the Npgsql provider map
    the `CostBasis` enum (as a `string`/text column) and the nullable dimension columns
    (`AgentId`, `WorkflowDefinitionId`, `RepoId`) identically — column names, nullability, and
    CLR-to-store type — following the `EF.Property<string?>(...)` projection-parity pattern used
    by `PlatformAnalyticsService.GetTenantCountsAsync`.

11. A new additive EF migration is generated under
    `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/` via
    `dotnet ef migrations add AddAnalyticsUsageFactTables -c TenantDbContext` (resolved by
    `TenantDesignTimeDbContextFactory`); `dotnet ef migrations has-pending-model-changes
    -c TenantDbContext` reports **no** pending changes afterward, and the migration applies
    cleanly under `EfTenantDbMigrator.MigrateTenantAppAsync` into a `t_<hex>` search-path schema.

12. A migration smoke test (Postgres 17 Testcontainer, following
    `SchemaPerTenantMigrationTests` / `ConventionStoreMigrationTests`) migrates two distinct
    tenant schemas into one database and asserts: (a) both schemas carry their own
    `analytics_usage_hourly` + `analytics_usage_daily` + `__TenantMigrationsHistory`; (b) a row
    inserted into schema A's `analytics_usage_hourly` is invisible through schema B's context
    (search-path isolation, no EF query filter); (c) a second insert of the same full
    dimension-tuple within one bucket raises a unique-constraint violation (NULLS NOT DISTINCT
    proven for NULL `AgentId`/`WorkflowDefinitionId`/`RepoId`).

13. No DCB event is emitted by this story (schema-only). The DCB event catalogue for population
    (`ANALYTICS.PROJECTION.*`) is owned by Story 36-2; the entity XML doc-comments reference the
    existing `LLM.CALL.SUCCESS`, `AGENT.DISPATCH.*`, and `WORKFLOW.STEP_COMPLETED` event types
    as the *future* projection sources, without consuming them here.

## Tasks / Subtasks

- [ ] Task 1: `CostBasis` enum (AC: 3)
  - [ ] Add `apps/tamma-elsa/src/Tamma.Core/Enums/CostBasis.cs` with `Byok`, `Platform`.
  - [ ] Unit test: enum members and `ToString()` lower-case round-trip expectation documented.

- [ ] Task 2: `AnalyticsUsageHourly` + `AnalyticsUsageDaily` entities (AC: 1, 2, 8, 9)
  - [ ] Add both entity classes under `Tamma.Data/Entities/` with shared dimension + measure
        shape (Hour/Day differs only).
  - [ ] XML doc-comments cite the Story 28-10 `PlatformAnalyticsHourly` precedent and the
        future Story 36-2 projection sources.

- [ ] Task 3: EF model configuration (AC: 4, 6, 7, 9)
  - [ ] Add `TammaModelConfiguration.ConfigureAnalyticsUsageEntities(ModelBuilder, Guid? fixedTenantId)`.
  - [ ] Map tables, defaults, `CostBasis` `HasConversion<string>()`, breakdown index, NULLS NOT
        DISTINCT unique business-key index; call `ApplyTenantFilter` for graph parity.
  - [ ] Invoke from `ConfigureTenantEntities`; add `DbSet`s to `TenantDbContext`.

- [ ] Task 4: Model-parity unit tests (AC: 5, 10)
  - [ ] `AnalyticsUsageModelTests` — InMemory + Npgsql model graph asserts column names,
        nullability, `CostBasis` text mapping, table presence on `TenantDbContext`, absence on
        `ControlPlaneDbContext`.
  - [ ] Extend `ControlPlaneDbContextModelTests` — `platform_analytics_hourly` still mapped,
        `analytics_usage_*` absent.

- [ ] Task 5: Migration + smoke test (AC: 11, 12)
  - [ ] Generate `AddAnalyticsUsageFactTables` Tenant migration; confirm
        `has-pending-model-changes` is clean.
  - [ ] `AnalyticsUsageMigrationTests` — two-schema isolation + NULLS NOT DISTINCT collision.

## Technical Design

### C# namespace / file structure

```
apps/tamma-elsa/src/
  Tamma.Core/Enums/CostBasis.cs                              # NEW enum
  Tamma.Data/Entities/AnalyticsUsageHourly.cs               # NEW per-tenant fact entity
  Tamma.Data/Entities/AnalyticsUsageDaily.cs                # NEW per-tenant rolled-up fact entity
  Tamma.Data/TenantDbContext.cs                             # MODIFY: + 2 DbSets
  Tamma.Data/TammaModelConfiguration.cs                     # MODIFY: + ConfigureAnalyticsUsageEntities
  Tamma.Data/Migrations/Tenant/<ts>_AddAnalyticsUsageFactTables.cs            # NEW (generated)
  Tamma.Data/Migrations/Tenant/<ts>_AddAnalyticsUsageFactTables.Designer.cs   # NEW (generated)
  Tamma.Data/Migrations/Tenant/TenantDbContextModelSnapshot.cs               # MODIFY (regenerated)

apps/tamma-elsa/tests/Tamma.Api.Tests/
  Analytics/AnalyticsUsageModelTests.cs                     # NEW model-parity tests
  Analytics/AnalyticsUsageMigrationTests.cs                 # NEW Postgres smoke test
  Epic28/ControlPlaneDbContextModelTests.cs                 # MODIFY: assert CP untouched
```

### `CostBasis` enum (Tamma.Core)

```csharp
namespace Tamma.Core.Enums;

/// <summary>
/// Discriminates whether the LLM/agent cost on an analytics fact row was
/// paid against the tenant's own provider key (BYOK — Tamma never bills it)
/// or against a Tamma-platform key (Tamma fronts the cost and may bill the
/// tenant via <c>PlatformBilledUsd</c>). Persisted as lowercase text
/// (<c>byok</c> / <c>platform</c>) via <c>HasConversion&lt;string&gt;()</c>.
/// </summary>
public enum CostBasis
{
    Byok = 0,
    Platform = 1,
}
```

### `AnalyticsUsageHourly` entity (shared shape; `AnalyticsUsageDaily` is identical except `Day`)

```csharp
namespace Tamma.Data.Entities;

/// <summary>
/// Epic 36 Story 36-1 — per-tenant hourly usage/cost/performance fact row.
/// Lives in the tenant schema (NOT the control-plane platform_analytics_hourly
/// table — that stays owner-only, Story 28-10). Populated by the Story 36-2
/// projection pipeline from LLM.CALL.SUCCESS / AGENT.DISPATCH.* /
/// WORKFLOW.STEP_COMPLETED DCB events. Measure types mirror
/// <see cref="PlatformAnalyticsHourly"/> so an owner-side comparison is lossless.
/// </summary>
public class AnalyticsUsageHourly
{
    public Guid Id { get; set; }

    /// <summary>UTC top-of-hour bucket (e.g. 2026-06-17T12:00:00Z).</summary>
    public DateTime Hour { get; set; }

    // ── Dimensions ──
    public string Provider { get; set; } = null!;        // required
    public string? AgentId { get; set; }                 // nullable dimension
    public Guid? WorkflowDefinitionId { get; set; }      // nullable dimension
    public string? RepoId { get; set; }                  // nullable dimension
    public CostBasis CostBasis { get; set; }             // byok | platform (text)

    // ── Measures (counters: long; cost: decimal(20,4)) ──
    public long TokensIn { get; set; }
    public long TokensOut { get; set; }
    public decimal CostUsd { get; set; }
    public decimal PlatformBilledUsd { get; set; }
    public long WorkflowsStarted { get; set; }
    public long WorkflowsCompleted { get; set; }
    public long WorkflowsFailed { get; set; }
    public long AgentDispatches { get; set; }

    /// <summary>Wall-clock write/last-update timestamp (replay-friendly).</summary>
    public DateTime ComputedAt { get; set; }
}
```

`AnalyticsUsageDaily` is byte-for-byte the same with `public DateTime Day { get; set; }` in
place of `Hour`, so the daily roll-up (Story 36-2) is `GROUP BY date_trunc('day', Hour), <dims>`.

### EF model configuration (`TammaModelConfiguration`)

A new private static `ConfigureAnalyticsUsageEntities(ModelBuilder modelBuilder, Guid? fixedTenantId)`
is called near the end of `ConfigureTenantEntities` (the same place `AgentConfig`,
`PromptOverride`, `Convention` are configured). It mirrors
`ControlPlaneDbContext.ConfigurePlatformAnalyticsHourly` for defaults/precision and the
`prompt_overrides`/`conventions` indexes for NULLS NOT DISTINCT:

```csharp
private static void ConfigureAnalyticsUsageEntities(
    ModelBuilder modelBuilder, Guid? fixedTenantId)
{
    modelBuilder.Entity<AnalyticsUsageHourly>(entity =>
    {
        entity.ToTable("analytics_usage_hourly");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
        entity.Property(e => e.Hour).HasColumnType("timestamp with time zone");
        entity.Property(e => e.Provider).IsRequired().HasMaxLength(100);
        entity.Property(e => e.AgentId).HasMaxLength(200);
        entity.Property(e => e.RepoId).HasMaxLength(400);
        entity.Property(e => e.CostBasis).HasConversion<string>().IsRequired().HasMaxLength(20);

        entity.Property(e => e.TokensIn).HasDefaultValue(0L);
        entity.Property(e => e.TokensOut).HasDefaultValue(0L);
        entity.Property(e => e.WorkflowsStarted).HasDefaultValue(0L);
        entity.Property(e => e.WorkflowsCompleted).HasDefaultValue(0L);
        entity.Property(e => e.WorkflowsFailed).HasDefaultValue(0L);
        entity.Property(e => e.AgentDispatches).HasDefaultValue(0L);
        entity.Property(e => e.CostUsd).HasPrecision(20, 4).HasDefaultValue(0m);
        entity.Property(e => e.PlatformBilledUsd).HasPrecision(20, 4).HasDefaultValue(0m);
        entity.Property(e => e.ComputedAt).HasDefaultValueSql("now()");

        // Breakdown index (AC6) — drives "by provider / agent / workflow / cost-basis".
        entity.HasIndex(e => new
            { e.Hour, e.Provider, e.AgentId, e.WorkflowDefinitionId, e.CostBasis })
            .HasDatabaseName("IX_analytics_usage_hourly_breakdown");

        // Idempotent business key (AC7) — full dimension tuple, NULLS NOT DISTINCT.
        entity.HasIndex(e => new
            { e.Hour, e.Provider, e.AgentId, e.WorkflowDefinitionId, e.RepoId, e.CostBasis })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("UX_analytics_usage_hourly_dims");

        // Migration-graph parity only (no-op filter on tenant DB).
        ApplyTenantFilter(entity, fixedTenantId, _ => null);  // no TenantId column
    });

    modelBuilder.Entity<AnalyticsUsageDaily>(entity => { /* same, Day instead of Hour */ });
}
```

> **Tenant-isolation note (Doc 01 §1.4).** These tables carry **no `TenantId` column** —
> tenancy is implicit in the per-tenant schema + connection string. That matches the
> target architecture (`TenantDbContext` carries no query filters; the search-path schema is
> the isolation plane). `ApplyTenantFilter` is the deliberate no-op the codebase already uses;
> the lambda returns `null` because there is no `TenantId` to filter on. This is the *correct*
> shape for new tenant-resident tables — unlike the legacy `agent_configs`/`conventions` which
> retain a transitional `TenantId` column.

### `TenantDbContext` additions

```csharp
public DbSet<AnalyticsUsageHourly> AnalyticsUsageHourly => Set<AnalyticsUsageHourly>();
public DbSet<AnalyticsUsageDaily> AnalyticsUsageDaily => Set<AnalyticsUsageDaily>();
```

`OnModelCreating` already calls `TammaModelConfiguration.ConfigureTenantEntities(modelBuilder,
fixedTenantId: TenantId)`; the new `ConfigureAnalyticsUsageEntities` is invoked from inside
that method, so no extra `OnModelCreating` wiring is needed.

### EF migration sketch

`dotnet ef migrations add AddAnalyticsUsageFactTables -c TenantDbContext` (resolved by
`TenantDesignTimeDbContextFactory`, history table `__TenantMigrationsHistory`). The generated
`Up()` creates two tables in the current search-path schema:

```csharp
migrationBuilder.CreateTable(
    name: "analytics_usage_hourly",
    columns: table => new
    {
        Id = table.Column<Guid>(nullable: false, defaultValueSql: "gen_random_uuid()"),
        Hour = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
        Provider = table.Column<string>(maxLength: 100, nullable: false),
        AgentId = table.Column<string>(maxLength: 200, nullable: true),
        WorkflowDefinitionId = table.Column<Guid>(nullable: true),
        RepoId = table.Column<string>(maxLength: 400, nullable: true),
        CostBasis = table.Column<string>(maxLength: 20, nullable: false),
        TokensIn = table.Column<long>(nullable: false, defaultValue: 0L),
        TokensOut = table.Column<long>(nullable: false, defaultValue: 0L),
        CostUsd = table.Column<decimal>(type: "numeric(20,4)", nullable: false, defaultValue: 0m),
        PlatformBilledUsd = table.Column<decimal>(type: "numeric(20,4)", nullable: false, defaultValue: 0m),
        WorkflowsStarted = table.Column<long>(nullable: false, defaultValue: 0L),
        WorkflowsCompleted = table.Column<long>(nullable: false, defaultValue: 0L),
        WorkflowsFailed = table.Column<long>(nullable: false, defaultValue: 0L),
        AgentDispatches = table.Column<long>(nullable: false, defaultValue: 0L),
        ComputedAt = table.Column<DateTime>(nullable: false, defaultValueSql: "now()"),
    },
    constraints: table => table.PrimaryKey("PK_analytics_usage_hourly", x => x.Id));
// + analytics_usage_daily (Day instead of Hour)
// + IX_*_breakdown and UX_*_dims (UX uses NULLS NOT DISTINCT — EF 9 emits it for AreNullsDistinct(false))
```

`Down()` drops both tables. The migration is **additive** (new tables only) — no baseline CHECK
edit, so a plain `migrations add` is correct and `has-pending-model-changes` must report none
afterward (AC11). Both `ControlPlaneDesignTimeDbContextFactory` and
`TenantDesignTimeDbContextFactory` build cleanly; only the Tenant graph changes.

### DCB events

**None emitted by this story.** Population events (`ANALYTICS.PROJECTION.*` — e.g.
`ANALYTICS.PROJECTION.HOUR_COMPLETED`) are Story 36-2's. This story only documents, in entity
doc-comments, the *source* DCB events the future projection reads: `LLM.CALL.SUCCESS`
(`data.inputTokens`/`outputTokens`/`costUsd`), `AGENT.DISPATCH.SUCCESS|FAILED`, and
`WORKFLOW.STEP_COMPLETED` — the same prefixes `PlatformAnalyticsService` and
`AnalyticsRollupEvents` already track.

### Integration points

- **`TenantDbContext` + `EfTenantDbMigrator.MigrateTenantAppAsync`** — the migrator applies the
  new Tenant migration into each `t_<hex>` schema; new tenants get the tables on provisioning,
  existing tenants on the next migrate pass (no data backfill in this story).
- **Story 36-2 (population)** — reads/writes these tables via `TenantDbContext` resolved through
  `ITenantDbContextFactory`; the `UX_*_dims` index is its upsert target.
- **Story 28-10 `PlatformAnalyticsHourly`** — untouched; remains the owner-side CP fact table.
  The measure types/precision deliberately match so an owner-side reconciliation join is lossless.

### API shape

**No new HTTP endpoints in this story.** The Epic 36 tenant query API (a later story) reads
these tables behind `MemberAccess` (any tenant member may read their tenant's analytics) and
`OwnerAccess`/`PlatformOwnerAccess` for the CP business-analytics surface. Endpoint shape stays
identical across modes per the CLAUDE.md prompt-store precedent; the auth middleware resolves
the tenant from the request, and the per-tenant `TenantDbContext` enforces isolation physically.

### Per-mode + per-tenant handling

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns an `analytics_usage_*` row? | The sole user — it lives in their (only) tenant schema. | The tenant — it lives in that tenant's `t_<hex>` schema. |
| Isolation plane | Search-path schema + connection string (no `TenantId` column, no query filter). | Same — physically separate schema per tenant. |
| Who can read it (future query API)? | The user (no RBAC). | Any tenant member (`MemberAccess`); `member` is read-only by default. |
| Cross-tenant leakage risk | N/A (one tenant). | Prevented at the storage layer — a row in schema A is unreachable from schema B's context (proven by AC12 smoke test). |

Mode itself does not change the schema — the per-tenant store is mode-agnostic because both
modes resolve to exactly one tenant schema per request. The platform-wide owner analytics stays
on the CP `platform_analytics_hourly` table, which only `PlatformOwnerAccess` can read.

## Dependencies

**Prerequisite (internal):**
- **Epic 28 (per-tenant schema + `TenantDbContext` + `EfTenantDbMigrator`)** — the migration
  graph and per-tenant schema routing these tables live in.
- **Story 28-10** — the `PlatformAnalyticsHourly` entity + `ConfigurePlatformAnalyticsHourly`
  conventions (precision, defaults, partial-unique index style) this story mirrors.
- **Epic 4 (DCB `DomainEvent`)** — the event stream the future projection (36-2) reads; only
  referenced in doc-comments here.

**Blocks (internal):**
- **Story 36-2** (projection/population pipeline) — writes these tables.
- All downstream Epic 36 stories (tenant analytics query API, exports, scheduled reports,
  owner business analytics) — read these tables.

**External:**
- PostgreSQL 17 (NULLS NOT DISTINCT requires PG15+; production runs PG17).
- EF Core 9 / Npgsql (model-expressible `AreNullsDistinct(false)`).
- Testcontainers + Docker for the migration smoke test (run via
  `sg docker -c "dotnet test ..."`).

## Testing Strategy

1. **Unit — `CostBasis` enum:** members present; documents the lowercase-text persistence
   expectation that the model config enforces.

2. **Unit — model parity (`AnalyticsUsageModelTests`, InMemory + Npgsql):**
   - `TenantDbContext` model maps `analytics_usage_hourly` and `analytics_usage_daily`.
   - `CostBasis` is mapped as a `string`/text column on both providers (assert store type +
     column name, following the `EF.Property<string?>` parity pattern).
   - Nullable dimension columns (`AgentId`, `WorkflowDefinitionId`, `RepoId`) are nullable;
     `Provider`/`CostBasis` are required.
   - Counter columns are `bigint`, `CostUsd`/`PlatformBilledUsd` are `numeric(20,4)`.
   - The breakdown index and the `UX_*_dims` unique index exist with the expected column sets.

3. **Unit — CP isolation (`ControlPlaneDbContextModelTests`, extended):** CP model still maps
   `platform_analytics_hourly`; CP model does **not** map `analytics_usage_*` (tenant-only).

4. **Integration — tenant isolation + idempotency (`AnalyticsUsageMigrationTests`, Postgres 17
   Testcontainer):** following `SchemaPerTenantMigrationTests`:
   - Migrate two tenant schemas into one DB; both carry `analytics_usage_hourly` +
     `analytics_usage_daily` + `__TenantMigrationsHistory`.
   - Insert a row through schema A's context; assert it is invisible through schema B's context
     (search-path isolation — no EF query filter).
   - Insert two rows with the same full dimension tuple (including NULL `AgentId`/
     `WorkflowDefinitionId`/`RepoId`) in one bucket → second insert raises a unique violation
     (NULLS NOT DISTINCT proven).
   - Re-running the migrator for an already-migrated schema is a no-op.

5. **Migration discipline:** after generating the migration, `dotnet ef
   migrations has-pending-model-changes -c TenantDbContext` reports none; the new migration
   applies and rolls back cleanly.

**Mocks:** No Stripe or external provider calls in this story (schema-only). Tests use the
InMemory provider for model-shape assertions and a real Postgres 17 Testcontainer for
constraint/isolation assertions (EF InMemory does not honour NULLS NOT DISTINCT or CHECK
constraints — same rationale documented in `ConventionStoreMigrationTests`).

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Core/Enums/CostBasis.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/AnalyticsUsageHourly.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/AnalyticsUsageDaily.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | Modify (add 2 DbSets) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (add `ConfigureAnalyticsUsageEntities`, call from `ConfigureTenantEntities`) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/<ts>_AddAnalyticsUsageFactTables.cs` | Create (generated) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/<ts>_AddAnalyticsUsageFactTables.Designer.cs` | Create (generated) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/TenantDbContextModelSnapshot.cs` | Modify (regenerated) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/AnalyticsUsageModelTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/AnalyticsUsageMigrationTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs` | Modify (assert CP untouched) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions (analytics, tenancy, EF
   migrations).
3. Reviewed Story 28-10 (`PlatformAnalyticsHourly`, `ConfigurePlatformAnalyticsHourly`) and the
   `prompt_overrides`/`conventions` NULLS NOT DISTINCT index precedent in
   `TammaModelConfiguration`.
4. Confirmed the test runner: docker-bound suites run via `sg docker -c "dotnet test ..."`
   (session docker group is stale; the build itself needs no wrapper).
5. Planned the TDD cycle (write `AnalyticsUsageModelTests` red first, then the entities/config).

### Key Design Decisions

- **Mirror `PlatformAnalyticsHourly`, don't extend it.** The CP table stays the owner-only
  fleet-wide store (a single SELECT answers "platform this week"). The new per-tenant tables add
  the dimensions (`Provider`, `AgentId`, `WorkflowDefinitionId`, `RepoId`, `CostBasis`) the CP
  single-grain row deliberately omits, and live in the tenant schema so a tenant query never
  touches CP data. Matching measure types/precision keeps an owner-side reconciliation lossless.
- **No `TenantId` column.** New tenant-resident tables follow the Doc 01 §1.4 target shape:
  isolation is the schema, not a column. `ApplyTenantFilter` is the established no-op; the lambda
  returns `null`. Do **not** copy the legacy transitional `TenantId` column from `agent_configs`.
- **NULLS NOT DISTINCT business key.** The nullable dimensions (`AgentId`,
  `WorkflowDefinitionId`, `RepoId`) mean a naive unique index would let duplicate "unknown-agent"
  rows accumulate. `AreNullsDistinct(false)` (PG15+/PG17) makes one NULL collapse to one row per
  bucket — the upsert target Story 36-2 relies on. Same pattern as `prompt_overrides`.
- **`CostBasis` as text, not int.** `HasConversion<string>()` (lowercase `byok`/`platform`)
  keeps the column human-readable in ad-hoc SQL and InMemory/Npgsql parity simple — mirrors the
  mentorship `HasConversion<string>()` precedent.
- **Hourly + daily as separate tables, identical shape.** Keeping the dimension/measure contract
  identical makes the daily roll-up a pure `GROUP BY`; a single shared private configurator avoids
  drift.

### Schema-only boundary

This story creates **no** projection logic, **no** population workflow, **no** query endpoint,
**no** DCB events, and **no** dashboard surface. Those are owned by later Epic 36 stories. Any PR
that adds population or query code under cover of this story is out of scope — keep the diff to
entities + enum + model config + migration + tests.

## Logging Requirements

This story adds no runtime code paths beyond EF model registration, so it introduces no new
application logging. The standard EF/migration logging applies:
- **INFO**: migration applied (table created) — emitted by `EfTenantDbMigrator` /
  `dotnet ef database update`; per-schema (`t_<hex>`).
- **DEBUG**: model-validation output during `OnModelCreating` (EF default).
- **WARN/ERROR**: migration apply failure or pending-model-changes drift — surfaced by the
  migrator and the CI `has-pending-model-changes` gate.
- **Credential safety**: never log tenant connection strings or search-path schema secrets (the
  `t_<hex>` connection string is AES-GCM-encrypted at rest via `TenantSecretProtector`).

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
