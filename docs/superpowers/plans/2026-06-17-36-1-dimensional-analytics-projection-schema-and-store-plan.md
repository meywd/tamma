# Story 36-1 — Dimensional Analytics Projection Schema & Store (implementation plan)

> Epic 36 (Analytics & Reporting Platform) · P0 · est. 3-4 days · author Claude · 2026-06-17 ·
> SUB-SKILL: use `superpowers:test-driven-development` — every task writes its failing test first.
> Docker-bound suites run via `sg docker -c "dotnet test ..."`; the build itself needs no wrapper.

**Goal:** Stand up the per-tenant dimensional analytics store that the rest of Epic 36 reads
from. Add two per-tenant fact entities — `AnalyticsUsageHourly` and `AnalyticsUsageDaily` —
carrying the dimensions the single-grain `platform_analytics_hourly` model lacks (provider,
agent, workflow definition, repo, and a `byok|platform` cost basis), wire them into the
`TenantDbContext` EF model + Tenant migration graph, and prove InMemory/Npgsql parity, schema
isolation, and idempotent upsert keys. **Schema + EF model + migration only.**

**Non-goals (YAGNI guard):**
- NO population/projection logic — that is Story 36-2 (it writes these tables from DCB events).
- NO query/export/report endpoints — later Epic 36 stories.
- NO change to the control-plane `platform_analytics_hourly` table/entity/mapping — it stays the
  owner-only fleet-wide store (Story 28-10). This story only *adds* tenant-resident tables.
- NO DCB events emitted here. The `ANALYTICS.PROJECTION.*` catalogue belongs to 36-2; this story
  only references existing source events (`LLM.CALL.SUCCESS`, `AGENT.DISPATCH.*`,
  `WORKFLOW.STEP_COMPLETED`) in doc-comments.
- NO `TenantId` column on the new tables — tenancy is the schema (Doc 01 §1.4 target shape), not
  a column. Do not copy the legacy transitional `TenantId` from `agent_configs`/`conventions`.
- NO dashboard surface.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### What exists and is the pattern to mirror

| Site | What it gives us |
|---|---|
| `src/Tamma.Data/Entities/PlatformAnalyticsHourly.cs` | The single-grain CP fact entity. Measures: `WorkflowsStarted/Completed/Failed`, `AgentDispatches`, `TokensIn/Out` (all `long`), `CostUsd` (`decimal`), `ActiveTenantsAtHourEnd` (`int`), `ComputedAt`. **No** provider/agent/workflow/repo/cost-basis dimensions — exactly the gap 36-1 fills. Copy its `long`/`decimal` conventions verbatim. |
| `src/Tamma.Data/ControlPlaneDbContext.cs` — `ConfigurePlatformAnalyticsHourly` (lines 713-762) | The mapping precedent: `gen_random_uuid()` PK default, `timestamp with time zone` on the bucket, `HasDefaultValue(0L)` on counters, `HasPrecision(20, 4).HasDefaultValue(0m)` on `CostUsd`, `HasDefaultValueSql("now()")` on `ComputedAt`, and partial-unique indexes with `IsUnique()` + `HasFilter(...)`. **Note:** the CP analytics entity is configured *here in ControlPlaneDbContext*, not in `TammaModelConfiguration` — but our new tenant tables go through `TammaModelConfiguration.ConfigureTenantEntities` (the tenant graph's single config source). |
| `src/Tamma.Data/TenantDbContext.cs` (lines 51-93) | Where tenant DbSets are declared and `OnModelCreating` calls `TammaModelConfiguration.ConfigureTenantEntities(modelBuilder, fixedTenantId: TenantId)`. Add the two new DbSets here; no extra `OnModelCreating` wiring needed once the new configurator is invoked from `ConfigureTenantEntities`. |
| `src/Tamma.Data/TammaModelConfiguration.cs` — `ConfigureTenantEntities` (lines 642-756+) | The single source for tenant-entity mapping. `fixedTenantId is not null` ⇒ tenant context; CP POCOs are `Ignore`d. Each entity ends with `ApplyTenantFilter(entity, fixedTenantId, e => e.TenantId)`. New tables have no `TenantId`, so the filter lambda returns `null` (the filter is a deliberate no-op — see line 1137-1160). |
| `TammaModelConfiguration` `prompt_overrides` (lines 749-752) / `conventions` index | The `.IsUnique().AreNullsDistinct(false)` (NULLS NOT DISTINCT, EF 9 model-expressible, PG15+) precedent for the idempotent business key over nullable dimensions. |
| `TammaModelConfiguration.ConfigureMentorshipEntities` (lines 1216-1226) | The `.HasConversion<string>()` enum-to-text precedent (`CurrentState`, `Status` enums) — copy for `CostBasis`. |
| `src/Tamma.Core/Enums/` (`MentorshipState.cs`, `QualityGateResult.cs`, `AssessmentResult.cs`, `BlockerType.cs`) | Where the new `CostBasis` enum lives. |
| `src/Tamma.Data/TenantDesignTimeDbContextFactory.cs` | Resolves `dotnet ef ... -c TenantDbContext`; history table `__TenantMigrationsHistory`. Migrations land in `src/Tamma.Data/Migrations/Tenant/`. |
| `src/Tamma.Data/Pooling/EfTenantDbMigrator.cs` — `MigrateTenantAppAsync` | Applies the Tenant migration graph into a `t_<hex>` search-path schema; the smoke test drives it. |
| `src/Tamma.Api/Services/Analytics/PlatformAnalyticsService.cs` (lines 462-478) | The `EF.Property<string?>(t, "...")` projection-parity idiom — InMemory and Npgsql see the same shadow/text column. The model-parity test follows this. |

### Existing test patterns to follow

| Test | Pattern |
|---|---|
| `tests/Tamma.Api.Tests/Epic28/TenantDbContextModelTests.cs` | InMemory/Npgsql model-shape assertions over `ctx.Model.GetEntityTypes()` — `GetTableName()`, column presence. Copy for `AnalyticsUsageModelTests`. |
| `tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs` | CP model-shape assertions — extend to confirm `platform_analytics_hourly` stays and `analytics_usage_*` are absent. |
| `tests/Tamma.Api.Tests/Tenancy/SchemaPerTenantMigrationTests.cs` | Postgres 17 Testcontainer; migrate two schemas into one DB; prove search-path isolation (row in A invisible in B). Copy for `AnalyticsUsageMigrationTests`. |
| `tests/Tamma.Api.Tests/Conventions/ConventionStoreMigrationTests.cs` | Postgres 17 Testcontainer driving NULLS NOT DISTINCT unique-collision assertions via raw SQL. Copy for the dimension-tuple collision test. |

### Confirmed absent (so these are genuinely NEW)

`grep -rln "AnalyticsUsage|analytics_usage|CostBasis" src/ tests/` → **no matches**. The entities,
table names, and enum do not exist yet. `InternalsVisibleTo("Tamma.Api.Tests")` is declared in
`Tamma.Data.csproj` (line 47), so the test project can reach internal config if needed.

---

## Phased task breakdown (test-first)

### Task 1 — `CostBasis` enum (AC3)

- **Test first:** `tests/Tamma.Core.Tests/Enums/CostBasisTests.cs` (or colocated with existing
  enum tests) — assert the two members exist and pin the lowercase `byok`/`platform` text
  expectation that the model config will enforce (`Enum.GetName(...)?.ToLowerInvariant()`).
- **Implement:** `src/Tamma.Core/Enums/CostBasis.cs` — `enum CostBasis { Byok = 0, Platform = 1 }`
  with the XML doc-comment from the story (BYOK vs platform-fronted cost).
- **Run:** `dotnet test --filter "FullyQualifiedName~CostBasis"`.

### Task 2 — fact entities (AC1, AC2, AC8, AC9)

- **Test first:** add stub assertions to `AnalyticsUsageModelTests` (Task 4) referencing the
  entity properties — they won't compile until the entities exist (red).
- **Implement:**
  - `src/Tamma.Data/Entities/AnalyticsUsageHourly.cs` — dimensions
    (`Provider` required, `AgentId?`, `WorkflowDefinitionId?` Guid, `RepoId?`, `CostBasis`),
    measures (`TokensIn/Out`, `WorkflowsStarted/Completed/Failed`, `AgentDispatches` as `long`;
    `CostUsd`, `PlatformBilledUsd` as `decimal`), `Hour`, `Id`, `ComputedAt`.
  - `src/Tamma.Data/Entities/AnalyticsUsageDaily.cs` — identical shape, `Day` instead of `Hour`.
  - Doc-comments cite Story 28-10 `PlatformAnalyticsHourly` + the future 36-2 projection sources.
- **Approach:** keep the dimension/measure contract byte-identical across the two so the daily
  roll-up is a pure `GROUP BY`. Do **not** add a `TenantId` property.

### Task 3 — EF model configuration + DbSets (AC4, AC6, AC7, AC9)

- **Test first:** the index/column/nullability assertions in `AnalyticsUsageModelTests` (Task 4)
  drive this (red → green).
- **Implement:**
  - `TammaModelConfiguration`: add `private static void ConfigureAnalyticsUsageEntities(
    ModelBuilder modelBuilder, Guid? fixedTenantId)` mirroring `ConfigurePlatformAnalyticsHourly`
    for defaults/precision; map `CostBasis` with `.HasConversion<string>().HasMaxLength(20)`;
    `Provider` required `HasMaxLength(100)`; `AgentId` `HasMaxLength(200)`; `RepoId`
    `HasMaxLength(400)`. Add `IX_analytics_usage_hourly_breakdown` (`Hour, Provider, AgentId,
    WorkflowDefinitionId, CostBasis`) and `UX_analytics_usage_hourly_dims`
    (`Hour, Provider, AgentId, WorkflowDefinitionId, RepoId, CostBasis`,
    `.IsUnique().AreNullsDistinct(false)`). Same for daily with `Day`.
    End each entity with `ApplyTenantFilter(entity, fixedTenantId, _ => null)` (no `TenantId`).
  - Call `ConfigureAnalyticsUsageEntities(modelBuilder, fixedTenantId)` from the end of
    `ConfigureTenantEntities`.
  - `TenantDbContext`: add `DbSet<AnalyticsUsageHourly>` + `DbSet<AnalyticsUsageDaily>`.
- **Approach:** a single shared private configurator with one inner `Action<EntityTypeBuilder<T>>`
  applied to both entities avoids drift between hourly/daily (parameterise the bucket column).

### Task 4 — model-parity unit tests (AC5, AC10)

- **Files:** `tests/Tamma.Api.Tests/Analytics/AnalyticsUsageModelTests.cs` (new);
  extend `tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs`.
- **Assertions (InMemory + Npgsql, per `TenantDbContextModelTests`):**
  - `TenantDbContext` model maps `analytics_usage_hourly` + `analytics_usage_daily`.
  - `CostBasis` is a `string`/text store type on both providers; column name correct.
  - `AgentId`/`WorkflowDefinitionId`/`RepoId` nullable; `Provider`/`CostBasis` required.
  - counters are `bigint`; `CostUsd`/`PlatformBilledUsd` are `numeric(20,4)`.
  - `IX_*_breakdown` and `UX_*_dims` present with expected column sets; `UX_*_dims` is unique.
  - CP test: `platform_analytics_hourly` still mapped on `ControlPlaneDbContext`;
    `analytics_usage_*` absent from the CP model graph.
- **Run:** `dotnet test --filter "FullyQualifiedName~AnalyticsUsageModel|ControlPlaneDbContextModel"`.

### Task 5 — Tenant migration + smoke test (AC11, AC12)

- **Generate:** `dotnet ef migrations add AddAnalyticsUsageFactTables -c TenantDbContext`
  (project `src/Tamma.Data`). Verify the `Up()` creates both tables + both indexes with the
  NULLS NOT DISTINCT clause on `UX_*_dims`; `Down()` drops both.
- **Gate:** `dotnet ef migrations has-pending-model-changes -c TenantDbContext` ⇒ **none**
  (regenerate snapshot if it reports drift).
- **Test first/alongside:** `tests/Tamma.Api.Tests/Analytics/AnalyticsUsageMigrationTests.cs`
  (Postgres 17 Testcontainer, per `SchemaPerTenantMigrationTests` + `ConventionStoreMigrationTests`):
  1. migrate two `t_<hex>` schemas into one DB; both carry `analytics_usage_hourly` +
     `analytics_usage_daily` + `__TenantMigrationsHistory`.
  2. insert a row through schema A's `TenantDbContext`; assert invisible through schema B's.
  3. insert two rows with the same full dimension tuple (NULL `AgentId`/`WorkflowDefinitionId`/
     `RepoId`) in one bucket → second raises a unique violation (NULLS NOT DISTINCT proven).
  4. re-running the migrator for an already-migrated schema is a no-op.
- **Run:** `sg docker -c "dotnet test --filter 'FullyQualifiedName~AnalyticsUsageMigration'"`.

### Task 6 — full-suite + CI gate

- **Run:** `sg docker -c "dotnet test apps/tamma-elsa/Tamma.sln"` (or the Tamma.Api.Tests +
  Tamma.Core.Tests projects) — full green.
- **Verify** the migration applies + rolls back cleanly and `has-pending-model-changes` stays
  clean (CI gate).

---

## Sequencing & dependencies

Task 1 (enum) → Task 2 (entities) → Task 3 (config + DbSets) → Task 4 (model tests, red→green) →
Task 5 (migration + smoke test) → Task 6 (full suite). Tasks 1-3 are a tight compile chain; Task
4 can be written red against Task 2's stubs and turned green by Task 3. Task 5 must follow Task 3
(migration is generated from the finished model). No external story is a hard blocker beyond the
already-merged Epic 28 / Story 28-10 infrastructure.

## Risks + mitigations

- **NULLS NOT DISTINCT not emitted by the migration.** EF 9 supports `AreNullsDistinct(false)`
  but the generated SQL must carry `NULLS NOT DISTINCT`. *Mitigation:* the Postgres Testcontainer
  collision test (Task 5) is the real proof — InMemory cannot verify it (same caveat documented
  in `ConventionStoreMigrationTests`). If EF emits a plain unique index, fix the model and
  regenerate before the test passes.
- **Accidental `TenantId` column.** Copy-pasting from `agent_configs`/`conventions` would
  reintroduce the legacy transitional column. *Mitigation:* the `ApplyTenantFilter(..., _ => null)`
  signature + an explicit model test asserting **no** `TenantId` column on either table.
- **CP table touched by mistake.** *Mitigation:* AC5 + the extended `ControlPlaneDbContextModelTests`
  assert `platform_analytics_hourly` is unchanged and `analytics_usage_*` are absent from CP.
- **`has-pending-model-changes` drift.** The model snapshot must regenerate exactly.
  *Mitigation:* run the gate after `migrations add`; regenerate snapshot if drift; CI enforces it.
- **Hourly/daily drift.** Two hand-maintained mappings could diverge. *Mitigation:* a single
  shared private configurator parameterised on the bucket column.
- **Docker-group staleness in the test session.** *Mitigation:* run Testcontainer suites via
  `sg docker -c "dotnet test ..."` (per project memory `reference_dotnet_test_docker`).

## Acceptance criteria (mirror of the story)

- [ ] `AnalyticsUsageHourly` + `AnalyticsUsageDaily` entities exist with the full dimension
      (`Provider`, `AgentId?`, `WorkflowDefinitionId?`, `RepoId?`, `CostBasis`) + measure
      (`TokensIn/Out`, `CostUsd`, `PlatformBilledUsd`, `WorkflowsStarted/Completed/Failed`,
      `AgentDispatches`) contract; bucket is `Hour` / `Day`.
- [ ] `CostBasis` enum (`Byok`/`Platform`) maps to a lowercase text column via
      `HasConversion<string>()`, identical on InMemory + Npgsql.
- [ ] Both tables live in the tenant schema via `TenantDbContext` / `Migrations/Tenant` and carry
      **no** `TenantId` column; they are absent from `ControlPlaneDbContext`;
      `platform_analytics_hourly` is unchanged.
- [ ] Composite breakdown index `(bucket, Provider, AgentId, WorkflowDefinitionId, CostBasis)`
      and a NULLS NOT DISTINCT unique business-key index over the full dimension tuple exist.
- [ ] Counters are `long`, costs are `decimal(20,4)` matching `PlatformAnalyticsHourly`; measures
      default to 0, `ComputedAt` defaults to `now()`, `Id` to `gen_random_uuid()`.
- [ ] Model-parity tests pass on both providers; CP-isolation test passes.
- [ ] `AddAnalyticsUsageFactTables` migration applies cleanly under the Tenant design-time
      factory + `EfTenantDbMigrator`, `has-pending-model-changes` reports none, and the two-schema
      smoke test proves search-path isolation + NULLS NOT DISTINCT collision.
- [ ] No DCB events, population logic, endpoints, or dashboard changes introduced.
- [ ] Full test suite green.
