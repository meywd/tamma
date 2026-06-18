# Story 34-1: Plan & Price-Book Catalog Data Model

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide covers the 7-phase workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), the `.dev/` knowledge base, TRACE/DEBUG logging, test-first development, 100% coverage on critical paths, and build-success enforcement.

## User Story

As a **platform owner**,
I want a structured, immutable, versioned price-book on the control plane — plans with typed feature flags, typed quota entitlements, and recurring + metered pricing components — instead of the current opaque `Plan.Quotas` JSON blob,
so that billing (Epic 35), the markup engine, and entitlement enforcement all read the same canonical catalog, and a tenant assigned a plan keeps reproducible pricing/quotas forever even after the plan is re-priced.

## Priority

P0 — This is the schema foundation every other Epic 34 story builds on. Without typed entitlements and a versioned, immutable catalog, billing charges and quota enforcement cannot reference a stable source of truth.

## Acceptance Criteria

1. New control-plane entity `Plan` is extended (replacing the thin skeleton in `apps/tamma-elsa/src/Tamma.Data/Entities/Plan.cs`) with: `Version` (int), `Status` (`active`|`deprecated`|`draft`), `IsCustom` (bool), `BillingInterval` (`monthly`|`annual`), and `SupersedesPlanId` (`Guid?` pointing at the prior version). The legacy `Slug`, `DisplayName`, `PlacementPolicy`, and `Tenant.PlanId` FK contract are preserved so `AdminTenantsEndpoints.UpdateTenantPlan` keeps working unchanged.
2. New entity `PlanFeature` (`Id`, `PlanId`, `FeatureKey`, `BoolValue` nullable, `StringValue` nullable) added to `ControlPlaneDbContext` — boolean capability flags (e.g. `byok_allowed`) and string feature values (e.g. `support_tier = priority`) per plan version.
3. New entity `PlanEntitlement` (`Id`, `PlanId`, `MetricKey` (enum), `LimitValue` `long?` where `NULL` = unlimited, `Period` (`monthly`|`total`), `OverageMode` (`block`|`allow`|`meter`)) added to `ControlPlaneDbContext` — one typed quota row per metric per plan version.
4. New entity `PlanPrice` (`Id`, `PlanId`, `PricingMode` (`platform_provided`|`byok`), `RecurringUsd` `decimal`, `SeatUsd` `decimal`, `MeteredComponent` jsonb) added to `ControlPlaneDbContext` — recurring + per-seat + metered/overage pricing, split by pricing mode so BYOK tenants get a distinct price row from platform-provided tenants.
5. `EntitlementMetricKey` is a closed enum in `Tamma.Core/Enums/EntitlementMetricKey.cs` with members `Agents`, `WorkflowRuns`, `LlmTokens`, `Seats`, `Repos`, `RagStorageMb`, `BenchmarkRetentionDays`, persisted as snake_case strings (`agents`, `workflow_runs`, `llm_tokens`, `seats`, `repos`, `rag_storage_mb`, `benchmark_retention_days`), shared by entitlement, pricing, metering, and enforcement layers so quota keys never drift.
6. A plan version is immutable after activation: any attempt to mutate a `Plan` row (or its child `PlanFeature`/`PlanEntitlement`/`PlanPrice` rows) whose `Status = active` or `Status = deprecated` throws a `TammaError("PLAN.VERSION.IMMUTABLE", ...)`. Editing produces a NEW `Plan` row with `Version = prior + 1`, `SupersedesPlanId = priorId`, `Status = active`, and flips the prior row to `deprecated`.
7. A `UNIQUE (Slug, Version)` constraint plus a partial unique index `UX_plans_OneActivePerSlug` filtered on `Status = 'active'` enforce exactly one `active` version per slug at the database level; `CHECK` constraints pin `Status`, `BillingInterval` on `Plan`, `Period`/`OverageMode` on `PlanEntitlement`, and `PricingMode` on `PlanPrice` to their closed enums.
8. An EF Core migration in `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/` adds the three new tables (`plan_features`, `plan_entitlements`, `plan_prices`) and the new `Plan` columns, with FK `OnDelete(Restrict)` from each child to `plans(Id)` and a self-referencing FK `SupersedesPlanId → plans(Id)`. `dotnet ef migrations has-pending-model-changes` reports none after the migration is added.
9. `PlansSeeder.cs` is extended to seed structured `PlanFeature` / `PlanEntitlement` / `PlanPrice` rows for `free` / `team` / `enterprise` with deterministic UUIDv7 ids, **insert-missing-only** (never reverts admin edits — mirrors convention system-defaults ownership). Existing `FreePlanId` / `TeamPlanId` / `EnterprisePlanId` constants stay; their version-1 rows get `Version = 1`, `Status = active`.
10. All catalog reads go through `IPlanCatalogService` (`apps/tamma-elsa/src/Tamma.Api/Services/Pricing/`) returning a fully-resolved immutable `PlanSnapshot` (plan header + features + entitlements + prices for a given plan version). `GetActiveBySlugAsync(slug)`, `GetByIdAsync(planId)`, and `GetForTenantAsync(tenantId)` (resolves the tenant's `PlanId` shadow column then snapshots) are the read seams.
11. Catalog mutations are admin-only (`OwnerAccess` policy) and emit DCB events to `platform_events` via `IPlatformEventPublisher`: `PLAN.VERSION.CREATED` on a new version, `PLAN.DEPRECATED` when a prior version flips to deprecated. Tags include `slug`, `version`, `planId`, `supersedesPlanId`, `source = admin`.
12. Per-mode handling: the catalog is **platform-owned** in both modes (single-user and SaaS) — plans are global control-plane rows, never tenant-scoped. In single-user mode the sole user reads the catalog (no overrides). In SaaS mode, only platform owners (`OwnerAccess`) may create/deprecate versions; tenant members get read-only resolved snapshots for their assigned plan. There is no per-tenant plan override layer.
13. Unit tests cover: immutability guard (mutating an `active`/`deprecated` plan throws `PLAN.VERSION.IMMUTABLE`), version-supersede chain (v1 → v2 → v3 with correct `SupersedesPlanId` links), one-active-version invariant (two active rows for one slug rejected by the partial unique index), seeder idempotency (second `SeedAsync` is a no-op and does not revert an edited row), snapshot resolution (features+entitlements+prices assembled correctly per version), and event emission (`PLAN.VERSION.CREATED` / `PLAN.DEPRECATED` appended with correct tags).

## Technical Design

### Namespace & file structure

```
apps/tamma-elsa/src/
  Tamma.Core/Enums/
    EntitlementMetricKey.cs            # NEW — closed quota-key enum (Tamma.Core.Enums)
  Tamma.Data/Entities/
    Plan.cs                            # MODIFIED — versioning + status + pricing-mode columns
    PlanFeature.cs                     # NEW (Tamma.Data.Entities)
    PlanEntitlement.cs                 # NEW
    PlanPrice.cs                       # NEW
  Tamma.Data/
    ControlPlaneDbContext.cs           # MODIFIED — 3 new DbSets + Configure* methods
    Seeders/PlansSeeder.cs             # MODIFIED — seed structured child rows
    Migrations/ControlPlane/
      <timestamp>_PlanPriceBookCatalog.cs   # NEW EF migration
  Tamma.Api/Services/Pricing/          # NEW directory
    IPlanCatalogService.cs             # NEW (Tamma.Api.Services.Pricing)
    PlanCatalogService.cs              # NEW
    PlanSnapshot.cs                    # NEW — immutable read DTO record
    PlanVersionEditor.cs               # NEW — immutability + supersede orchestration
    PlanCatalogEventTypes.cs           # NEW — PLAN.VERSION.CREATED / PLAN.DEPRECATED
  Tamma.Api/Extensions/
    PricingServiceCollectionExtensions.cs  # NEW — DI wiring (mirror Alert extension pattern)
```

### Enum — `EntitlementMetricKey` (Tamma.Core)

```csharp
namespace Tamma.Core.Enums;

/// <summary>
/// Closed set of meterable/limitable quota dimensions. Shared by
/// PlanEntitlement (limits), PlanPrice metered components (pricing),
/// usage metering (Epic 35), and enforcement (Epic 34 later stories) so a
/// quota key is identical across every layer. Persisted as the snake_case
/// string (see <see cref="EntitlementMetricKeyExtensions"/>), never the
/// numeric ordinal — ordinals are not a stable wire/DB contract.
/// </summary>
public enum EntitlementMetricKey
{
    Agents,
    WorkflowRuns,
    LlmTokens,
    Seats,
    Repos,
    RagStorageMb,
    BenchmarkRetentionDays,
}
```

A companion `EntitlementMetricKeyExtensions.ToMetricString()` / `Parse(string)` maps to/from the snake_case persisted form; EF uses a `HasConversion` value converter so the DB column stores `text`, not an int.

### Entities

`Plan.cs` (modified — new fields, legacy fields kept):

```csharp
public class Plan
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = null!;            // free | team | enterprise (kept)
    public string DisplayName { get; set; } = null!;      // (kept)
    public int Version { get; set; } = 1;                 // NEW
    public string Status { get; set; } = "draft";         // NEW: active | deprecated | draft
    public bool IsCustom { get; set; }                    // NEW: bespoke enterprise plan
    public string BillingInterval { get; set; } = "monthly"; // NEW: monthly | annual
    public Guid? SupersedesPlanId { get; set; }           // NEW: prior version
    public decimal MonthlyPriceUsd { get; set; }          // kept (display convenience; canonical price now in PlanPrice)
    public string PlacementPolicy { get; set; } = "shared"; // kept (unified tenancy)
    public bool IsActive { get; set; } = true;            // kept (signup-surface flag)
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<PlanFeature> Features { get; set; } = [];
    public ICollection<PlanEntitlement> Entitlements { get; set; } = [];
    public ICollection<PlanPrice> Prices { get; set; } = [];
}
```

> The opaque `Quotas` string column is retained on the row (and migration) for one deprecation window so any not-yet-migrated reader still compiles; new code reads `Entitlements`. A follow-up story drops it once Epic 35 is off the JSON.

`PlanEntitlement.cs`:

```csharp
public class PlanEntitlement
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public EntitlementMetricKey MetricKey { get; set; }   // value-converted to text
    public long? LimitValue { get; set; }                 // NULL = unlimited
    public string Period { get; set; } = "monthly";       // monthly | total
    public string OverageMode { get; set; } = "block";    // block | allow | meter
}
```

`PlanFeature.cs`: `Id`, `PlanId`, `FeatureKey` (text), `BoolValue` (`bool?`), `StringValue` (`string?`).
`PlanPrice.cs`: `Id`, `PlanId`, `PricingMode` (`platform_provided`|`byok`), `RecurringUsd` (`decimal(20,4)`), `SeatUsd` (`decimal(20,4)`), `MeteredComponent` (jsonb, default `'{}'`).

### EF model config (in `ControlPlaneDbContext.OnModelCreating`)

Add `ConfigurePlans`, `ConfigurePlanFeatures`, `ConfigurePlanEntitlements`, `ConfigurePlanPrices` mirroring the existing `ConfigureAlertRules` / `ConfigureTenantDatabases` style:

```csharp
modelBuilder.Entity<Plan>(entity =>
{
    entity.ToTable("plans", t =>
    {
        t.HasCheckConstraint("ck_plans_status",
            "\"Status\" IN ('active','deprecated','draft')");
        t.HasCheckConstraint("ck_plans_billing_interval",
            "\"BillingInterval\" IN ('monthly','annual')");
    });
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    entity.Property(e => e.Slug).IsRequired().HasMaxLength(100);
    entity.Property(e => e.Version).HasDefaultValue(1);
    entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("draft");
    entity.Property(e => e.BillingInterval).IsRequired().HasMaxLength(20).HasDefaultValue("monthly");

    entity.HasIndex(e => new { e.Slug, e.Version })
        .HasDatabaseName("UX_plans_Slug_Version").IsUnique();

    // Exactly one active version per slug — the immutability invariant in SQL.
    entity.HasIndex(e => e.Slug)
        .HasDatabaseName("UX_plans_OneActivePerSlug")
        .HasFilter("\"Status\" = 'active'").IsUnique();

    entity.HasOne<Plan>().WithMany()
        .HasForeignKey(e => e.SupersedesPlanId)
        .OnDelete(DeleteBehavior.Restrict);
});
```

`PlanEntitlement` config adds CHECKs `ck_plan_entitlements_period` (`'monthly','total'`) and `ck_plan_entitlements_overage` (`'block','allow','meter'`); `PlanPrice` adds `ck_plan_prices_mode` (`'platform_provided','byok'`); each child gets `HasOne<Plan>().WithMany(p => p.Features/...).HasForeignKey(PlanId).OnDelete(Restrict)` and a unique index per natural key (`UX_plan_entitlements_PlanId_MetricKey`, `UX_plan_features_PlanId_FeatureKey`, `UX_plan_prices_PlanId_PricingMode`).

### EF migration sketch

```csharp
// <timestamp>_PlanPriceBookCatalog.cs  (Tamma.Data.Migrations.ControlPlane)
migrationBuilder.AddColumn<int>("Version", "plans", defaultValue: 1);
migrationBuilder.AddColumn<string>("Status", "plans", maxLength: 20, defaultValue: "active"); // existing 3 rows become active
migrationBuilder.AddColumn<bool>("IsCustom", "plans", defaultValue: false);
migrationBuilder.AddColumn<string>("BillingInterval", "plans", maxLength: 20, defaultValue: "monthly");
migrationBuilder.AddColumn<Guid>("SupersedesPlanId", "plans", nullable: true);

migrationBuilder.CreateTable("plan_features", ...);       // Id, PlanId FK, FeatureKey, BoolValue, StringValue
migrationBuilder.CreateTable("plan_entitlements", ...);   // Id, PlanId FK, MetricKey text, LimitValue, Period, OverageMode
migrationBuilder.CreateTable("plan_prices", ...);         // Id, PlanId FK, PricingMode, RecurringUsd, SeatUsd, MeteredComponent jsonb

// CHECK + partial-unique indexes per the model config above.
```

This is a purely additive migration (new columns with defaults backfill the 3 seeded rows, new tables) — `has-pending-model-changes` must report none after generation.

### Snapshot + catalog service

```csharp
namespace Tamma.Api.Services.Pricing;

public sealed record PlanSnapshot(
    Guid PlanId, string Slug, string DisplayName, int Version, string Status,
    bool IsCustom, string BillingInterval, Guid? SupersedesPlanId,
    IReadOnlyList<PlanFeatureView> Features,
    IReadOnlyList<PlanEntitlementView> Entitlements,
    IReadOnlyList<PlanPriceView> Prices);

public interface IPlanCatalogService
{
    Task<PlanSnapshot?> GetActiveBySlugAsync(string slug, CancellationToken ct = default);
    Task<PlanSnapshot?> GetByIdAsync(Guid planId, CancellationToken ct = default);
    Task<PlanSnapshot?> GetForTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<PlanSnapshot>> ListActiveAsync(CancellationToken ct = default);
}
```

`GetForTenantAsync` reads the tenant's `PlanId` shadow column via `EF.Property<Guid?>(tenant, "PlanId")` (the existing shadow column wired in `TammaModelConfiguration`) and snapshots that exact version — historical reproducibility, since a deprecated version's rows are immutable.

### Version editor (immutability + supersede)

`PlanVersionEditor.CreateNewVersionAsync(slug, draftSpec, principal, ct)`:
1. Load current `active` plan + children for `slug`.
2. Insert a new `Plan` row: `Version = current.Version + 1`, `SupersedesPlanId = current.Id`, `Status = active`, copy/override features/entitlements/prices from `draftSpec`.
3. Flip `current.Status` to `deprecated`.
4. Both in one transaction; the partial unique index `UX_plans_OneActivePerSlug` guarantees the flip-then-insert ordering can never leave two active rows.
5. Emit `PLAN.VERSION.CREATED` (new) + `PLAN.DEPRECATED` (prior) via `IPlatformEventPublisher.AppendAndPublishAsync` using a `BuildPlanEvent` helper modelled on `AdminTenantsEndpoints.BuildAdminEvent`.

Immutability guard: a `SaveChanges` interceptor (or an explicit guard in the editor before any direct child mutation) rejects modifying a tracked `Plan`/child whose `Status` is `active` or `deprecated`, throwing `TammaError("PLAN.VERSION.IMMUTABLE", ..., severity: High)`. Only `draft` rows and the controlled deprecate-flip are writable.

### DCB event names

| Event | When | Tags |
|---|---|---|
| `PLAN.VERSION.CREATED` | new immutable version activated | `slug`, `version`, `planId`, `supersedesPlanId`, `source=admin`, `actorUserId` |
| `PLAN.DEPRECATED` | prior version flipped to `deprecated` | `slug`, `version`, `planId`, `supersededByPlanId`, `source=admin`, `actorUserId` |

Both append to `platform_events` (control-plane resident — the `AlertRuleEvaluator` already polls this table, so a later story can alert on catalog drift with no new plumbing).

### Integration points

- `AdminTenantsEndpoints.UpdateTenantPlan` (existing) keeps assigning `Tenant.PlanId` to a `Plan.Id`; it now points at a specific *version* row. No change required, but a follow-up may validate the target is `Status = active`.
- `PlansSeeder.SeedAsync` (existing, wired at `Program.cs:2030`) is extended to also seed child rows; the `AnyAsync` short-circuit becomes per-table insert-missing-only so it can backfill children on a DB that already had bare plan rows.
- `PricingServiceCollectionExtensions.AddPlanCatalog(services)` registers `IPlanCatalogService`, `PlanVersionEditor` (scoped) and is called from `Program.cs` composition root.

### API shape (read-only surface added in this story; full admin CRUD lands in 34-2)

```
GET /api/admin/plans                 (OwnerAccess) → list active PlanSnapshots
GET /api/admin/plans/{slug}          (OwnerAccess) → active PlanSnapshot for slug
GET /api/admin/plans/{slug}/versions (OwnerAccess) → version chain (active + deprecated)
```

These read endpoints map to `IPlanCatalogService`. Mutating endpoints (create/deprecate version) are explicitly **deferred to Story 34-2** — this story ships the service method + the events, exercised by unit tests, so 34-2 only wires the endpoint.

### Per-mode + per-tenant handling

- **Single-user mode**: catalog is global; the sole user reads it. No RBAC; no overrides. `ITammaModeProvider` is not consulted for reads (the data is identical in both modes).
- **SaaS mode**: catalog is platform-owned. Create/deprecate is `OwnerAccess` only. Tenant members get read-only snapshots of *their assigned plan version* via `GetForTenantAsync`. No per-tenant plan customization layer (custom enterprise plans are still platform-owned rows with `IsCustom = true`, assigned via `PlanId`).

## Dependencies

**Internal (prerequisite):**
- Epic 28 (control-plane / tenancy) — provides `ControlPlaneDbContext`, the `Tenant.PlanId` shadow column + FK to `plans`, `PlansSeeder`, and the migration pipeline under `Migrations/ControlPlane/`.
- Epic 4 (DCB events) — provides the `platform_events` store, `PlatformEvent` entity, and `IPlatformEventPublisher.AppendAndPublishAsync`.

**Internal (blocks):**
- Story 34-2 (plan admin CRUD / version-management API) — wires the editor/service behind admin endpoints.
- Story 34-x (cost→price markup engine) — reads `PlanPrice.MeteredComponent` + `PlanEntitlement`.
- Epic 35 (Billing) — charges against `PlanEntitlement` / `PlanPrice`; metering keys off `EntitlementMetricKey`.
- Entitlement enforcement stories — consume `EntitlementMetricKey` + `PlanSnapshot.Entitlements`.

**External:**
- PostgreSQL 17 (partial unique index `WHERE Status = 'active'`, jsonb `MeteredComponent`).
- EF Core 9 / Npgsql (value converter for `EntitlementMetricKey`, additive migration).
- No Stripe dependency in this story — pricing data is stored, not charged (Epic 35 owns Stripe).

## Testing Strategy

**Unit tests** (`tests/Tamma.Api.Tests/Pricing/` + `tests/Tamma.Core.Tests/Enums/`), test-first:

1. `EntitlementMetricKeyTests` — every member round-trips through `ToMetricString()` / `Parse()`; unknown string throws; snake_case mapping pinned (`LlmTokens ↔ "llm_tokens"`, `RagStorageMb ↔ "rag_storage_mb"`).
2. `PlanCatalogServiceTests` — `GetActiveBySlugAsync` assembles features+entitlements+prices for the active version only; `GetByIdAsync` returns a deprecated version's frozen snapshot; `GetForTenantAsync` resolves via the `PlanId` shadow column; missing slug → null.
3. `PlanVersionEditorTests` — `CreateNewVersionAsync` produces v2 with `SupersedesPlanId = v1.Id`, flips v1 to `deprecated`, emits `PLAN.VERSION.CREATED` + `PLAN.DEPRECATED` with correct tags (assert against a fake `IPlatformEventPublisher`); a 3-version chain links correctly.
4. `PlanImmutabilityTests` — mutating an `active` plan throws `PLAN.VERSION.IMMUTABLE`; mutating a `deprecated` plan throws; mutating a `draft` plan succeeds.
5. `PlansSeederTests` — first `SeedAsync` populates structured child rows for all three slugs with the known UUIDs and `Version = 1`/`Status = active`; second `SeedAsync` is a no-op; an admin-edited (`draft` v2) row is NOT reverted by re-seed (insert-missing-only).

**Integration / DB tests** (docker-bound, run via `sg docker -c "dotnet test ..."`):

6. `PlanCatalogMigrationTests` — apply the migration to a clean DB, assert the 3 new tables + columns + CHECK constraints + partial unique index exist; migration rolls back cleanly.
7. `OneActiveVersionInvariantTests` — directly inserting a second `Status = active` row for an existing slug is rejected by `UX_plans_OneActivePerSlug` (catch the unique-violation `DbUpdateException`).
8. **Tenant-isolation test** — the catalog is platform-global, so the test asserts a tenant-scoped read (`GetForTenantAsync`) never returns another tenant's *assigned* plan and never leaks `draft` versions to a tenant snapshot; two tenants on different plan versions each resolve their own frozen snapshot.

**Mocks:** `IPlatformEventPublisher` is faked to capture emitted events (no real `platform_events` write in unit tests). No Stripe/provider mocks needed in this story (no external billing call). DB-bound tests use the real Npgsql provider against the docker Postgres (the project's standard for migration/constraint tests).

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Core/Enums/EntitlementMetricKey.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Enums/EntitlementMetricKeyExtensions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/PlanFeature.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/PlanEntitlement.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/PlanPrice.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/Plan.cs` | Modify (versioning + status + nav collections) |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (3 DbSets + 4 Configure* methods) |
| `apps/tamma-elsa/src/Tamma.Data/Seeders/PlansSeeder.cs` | Modify (seed structured child rows, insert-missing-only) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<timestamp>_PlanPriceBookCatalog.cs` | Create (EF migration) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/IPlanCatalogService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PlanCatalogService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PlanSnapshot.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PlanVersionEditor.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PlanCatalogEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/PricingServiceCollectionExtensions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (call `AddPlanCatalog`; map 3 read endpoints) |
| `apps/tamma-elsa/tests/Tamma.Core.Tests/Enums/EntitlementMetricKeyTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PlanCatalogServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PlanVersionEditorTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PlanImmutabilityTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PlansSeederTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PlanCatalogMigrationTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md).
2. Searched `.dev/` for related spikes/bugs/findings/decisions (especially any pricing/quota or migration discipline notes).
3. Reviewed how `ControlPlaneDbContext` configures existing CP tables (`ConfigureAlertRules`, `ConfigureTenantDatabases`) — mirror that exact style.
4. Reviewed `PlansSeeder.cs` and the convention system-defaults ownership rule (insert-missing-only, never revert admin edits).
5. Planned the TDD Red-Green-Refactor cycle (enum + immutability tests first).

### Key Design Decisions

- **Immutable versioned rows over in-place plan edits.** A tenant assigned `team v1` must keep `team v1` pricing/quotas even after `team v2` ships at a higher price. Mutating in place would silently re-price existing tenants and break billing reproducibility. The partial unique index enforces "one active per slug" so the catalog always has exactly one current version to assign new tenants.
- **`EntitlementMetricKey` is the single source of quota-key truth.** Entitlement limits, metered pricing components, usage metering, and enforcement all key off the same enum so a typo can never split `llm_tokens` from `llmTokens` across layers. Persist as snake_case text, never the ordinal.
- **`PlanPrice` split by `PricingMode`.** BYOK tenants (bring-your-own AI keys) and platform-provided tenants get different price rows under the same plan version — the markup engine and billing each pick the row matching the tenant's mode. This is the hook for the epic's "BYOK-vs-platform-provided pricing modes" theme.
- **Keep the legacy `Quotas` column + `MonthlyPriceUsd` for one deprecation window.** Avoids a big-bang change to every reader; new code reads structured rows. A later story drops the JSON.
- **Read-only API in this story.** The data model + service + events are the foundation; admin write endpoints are Story 34-2's job. Shipping the editor as a tested service method (not an endpoint) keeps the boundary clean.

### Boundary Notes (what this story does NOT do)

- No Stripe / billing integration, no charging, no invoices (Epic 35).
- No usage metering writes (Epic 35) — this story only defines the keys metering will use.
- No quota *enforcement* (later Epic 34 stories) — this story stores limits; it does not block on them.
- No admin write/CRUD endpoints (Story 34-2).
- No cost→price markup computation (separate Epic 34 story) — `PlanPrice.MeteredComponent` is stored verbatim.
- No tenant-scoped plan overrides — catalog is platform-global by design.

### Migration discipline

This is an additive migration (new columns with backfilling defaults, new tables). Generate with `dotnet ef migrations add PlanPriceBookCatalog --context ControlPlaneDbContext` and then verify `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` reports none. Mirror the entity configuration in `ControlPlaneDbContext.OnModelCreating` only (the established single source for CP entity config) — do not split config across files.

### Per-mode ownership recap (CLAUDE.md "two scoping models" rule)

| Question | single-user | SaaS |
|---|---|---|
| Who owns the plan catalog? | Platform (global rows); sole user reads it. | Platform owner (`OwnerAccess`); tenants read-only. |
| Who creates/deprecates a version? | The sole user (no RBAC gate, but same code path). | `OwnerAccess` (platform owner) only. |
| Per-tenant overrides? | None — global catalog. | None — custom plans are platform rows with `IsCustom = true`. |
| Mode source | `ITammaModeProvider` (not consulted for reads — data identical). | same |

## Logging Requirements

- **INFO**: plan version created (`slug`, `version`, `planId`), plan deprecated (`slug`, `version`), seeder inserted N child rows, catalog snapshot served (`slug`, `version`).
- **DEBUG**: snapshot assembly (counts of features/entitlements/prices), `GetForTenantAsync` resolved `PlanId` for a tenant, version-editor transaction begin/commit.
- **WARN**: attempted mutation of an immutable (`active`/`deprecated`) plan version (before the throw), `GetForTenantAsync` found a tenant with a NULL `PlanId`.
- **ERROR**: version-create transaction rollback, one-active-version unique-violation surfaced from the DB (should be impossible via the editor — log the unexpected race), migration apply failure.
- **Structured context**: include `{ slug, version, planId, supersedesPlanId, metricKey, pricingMode }` where applicable.
- **Credential safety**: pricing data is not secret, but never log encrypted connection strings or tenant secrets if a snapshot path ever touches a tenant row.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
