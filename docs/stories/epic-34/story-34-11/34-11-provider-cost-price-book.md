# Story 34-11: Provider Cost Price-Book (DB-backed `Provider` + `ProviderModelPrice` behind `IProviderPricingService`)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide covers the 7-phase workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), the `.dev/` knowledge base, TRACE/DEBUG logging, test-first development, 100% coverage on critical paths, and build-success enforcement.

## User Story

As a **platform owner**,
I want the provider COST rate-sheet — today a hard-coded `FrozenDictionary` in `ProviderPricingService` — promoted to a first-class, admin-editable, immutable-versioned control-plane entity model (`Provider` + `ProviderModelPrice`) **behind the unchanged `IProviderPricingService` seam**,
so that the platform's cost basis is a single canonical, auditable, time-windowed source of truth that the markup engine (34-5), usage metering (32-9), and analytics (36-2/36-7) all read without re-deriving rates — and so a usage event always prices under the cost rate that was effective when the call actually happened, even after a provider re-prices its models.

## Priority

P0 — Sequence step **A** of the Epic-34 pivot. This is the *cost* book; 34-1 owns the *price* book (sell) and 34-5 owns *markup* (cost × margin). 34-5's markup math (`CostBasisUsd = ProviderPricingService.Compute(...)`) and its `IsKnown` unknown-model gate consume this story's output. Without a DB-backed, versioned cost entity the cost basis is frozen at deploy time, cannot be re-priced without a code release, and provides no `EffectiveFrom` window for reproducible historical pricing. **Sequenced BEFORE 34-5** (its cost-basis input), alongside 34-1 (reuses its CP-entity + versioning + insert-missing-only-seeder patterns).

## Context

A cost-pricing layer already exists as the single cost-basis source of truth:
`apps/tamma-elsa/src/Tamma.Api/Services/Providers/IProviderPricingService` / `ProviderPricingService` — a hard-coded `FrozenDictionary<provider, FrozenDictionary<model, Rate(InputPerToken, OutputPerToken)>>` ported verbatim from `packages/cost-monitor/src/pricing-config.ts` (commit `9e9a57c~1`). It carries the alias map, loose prefix match, and `null`/`"default"`→first-model rule that **34-5's `IsKnown` gate and the diagnostic write path depend on**. There is **no DB-backed `Provider` entity today**; the rate sheet is immutable at deploy time.

The revised agent architecture (design §3, §4) requires the cost basis to be:
- **first-class** (admin-editable, auditable) — a provider re-prices a model without a code release;
- **versioned/immutable** — an edit *supersedes* rather than mutates, like `Plan` (34-1) and `MarginPolicy` (34-5), so a historical usage event re-prices under the rate active at its `OccurredAt` (byte-stable / reproducible — the cost-side companion of 34-5 AC7);
- **platform-global, NOT tenant-scoped** — cost is the *provider's published rate*, identical for every tenant (`PricingMode`/BYOK affects only the *sell* side, never the cost basis — design §4.4).

This story promotes the frozen table to two control-plane entities — `Provider` (the cost identity + `AuthModel` that feeds 32-4's SaaS-eligibility) and `ProviderModelPrice` (the per-model versioned cost) — **behind the existing `IProviderPricingService` seam**. The interface is the contract; the entity is the implementation detail. The frozen `ProviderPricingService` is retained as the deterministic seed/fallback source; a new `DbProviderPricingService` reads the entity table. **Downstream stories (34-5, 36-2, 36-7, 32-9) need at most a one-line DI dependency edit; none need AC or code changes** — that is the whole point of preserving the seam.

> **COST vs PRICE — the three layers (design §4.3).** This story owns **COST only**. It is **NOT** `Plan`/`PlanPrice` (the subscription/seat *sell* price — 34-1), **NOT** `MarginPolicy` (the *markup* applied to cost — 34-5), and **NOT** 36-7 (the read-only margin *view*). `ProviderModelPrice` is per-token cost keyed by `(ProviderKey, Model)`; `PlanPrice` is subscription sell price keyed by `(PlanId, PricingMode)` — no overlap. 34-5 reads this entity for its cost basis and applies `MarginPolicy` on top; 36-7 reads neither — it reports the already-persisted `CostUsd` (this story's basis) minus revenue.

## Acceptance Criteria

1. New control-plane entity **`Provider`** (`Tamma.Data.Entities.Provider`) added to `ControlPlaneDbContext`: `Id` (UUIDv7), `Key` (canonical string — `anthropic|openai|google|openrouter|local|claude-code`), `DisplayName` (string), `AuthModel` (`api-key|cli-token` — feeds 32-4 SaaS-eligibility), `Status` (`active|retired`), `CreatedAt`, `UpdatedAt`. `Key` is unique. A CHECK pins `AuthModel ∈ ('api-key','cli-token')` and `Status ∈ ('active','retired')`.

2. New control-plane entity **`ProviderModelPrice`** (`Tamma.Data.Entities.ProviderModelPrice`) added to `ControlPlaneDbContext`: `Id` (UUIDv7), `ProviderKey` (canonical, **alias-normalized on write**), `Model` (e.g. `claude-sonnet-4-20250514`, `gpt-4o`), `InputUsdPer1M` (`decimal`), `OutputUsdPer1M` (`decimal`), `CacheReadUsdPer1M` (`decimal?` — nullable, reserved), `CacheWriteUsdPer1M` (`decimal?` — nullable, reserved), `EffectiveFrom` (`timestamptz`, UTC), `Status` (`active|superseded`), `Source` (`seed|admin`), `CreatedAt`, `UpdatedAt`. CHECK constraints pin `Status ∈ ('active','superseded')` and `Source ∈ ('seed','admin')`.

3. **Platform-global, not tenant-scoped.** Neither entity carries a `TenantId`/`UserId` column or a tenant query filter. Cost is the provider's published rate — identical for every tenant in both modes. (BYOK vs platform-provided is a *sell-side* concern owned by 34-3/34-5, never a cost-basis concern.) A model test asserts no `TenantId` property exists on either entity.

4. **Immutable-versioned like `Plan`/`MarginPolicy`.** An edit **supersedes** rather than mutates: a partial unique index `UX_provider_model_prices_OneActivePerModel` on `(ProviderKey, Model) WHERE "Status" = 'active'` enforces exactly one `active` row per `(ProviderKey, Model)` at the DB level. A `PUT` that changes an existing `active` price flips the prior row to `superseded` and inserts a new `active` row with a later `EffectiveFrom` — never an in-place rate mutation. Attempting to mutate a `superseded` row throws `TammaError("PROVIDER.PRICE.IMMUTABLE", ...)`.

5. **`EffectiveFrom`-windowed resolution.** A usage event prices under the cost rate that was `active`/`superseded` with `EffectiveFrom <= OccurredAt` and is the most-recent such row for `(ProviderKey, Model)`. This makes the cost chain byte-stable/reproducible (the cost-side companion of 34-5 AC7): given the same `(provider, model, in, out, occurredAt)` the cost is identical across runs. A `Compute(...)` overload (or the resolver) accepts an `atTimestamp` and selects the effective row; the existing no-timestamp `Compute` resolves against the current `active` row.

6. **Load-bearing behaviours from the frozen table are preserved verbatim** in the DB-backed resolver (34-5's `IsKnown` gate and the diagnostic write path depend on these — they move into the entity-backed lookup, they are NOT dropped):
   - **alias normalization** — `anthropic-claude`→`anthropic`, `claude`→`anthropic`, `gemini`→`google`, `github-copilot`→`openai`, `ollama`/`lmstudio`→`local` (the existing `s_aliases` map), applied to the lookup key AND on write to `ProviderModelPrice.ProviderKey`;
   - **loose prefix match** — a request for `claude-sonnet-4` matches the stored `claude-sonnet-4-20250514` (first row whose `Model` starts with the requested id);
   - **`null`/`"default"` → first-model rule** — resolves to the provider's first known model;
   - **unknown `(provider, model)` returns `0m` from `Compute` and `false` from `IsKnown`** (the existing robustness contract — `Compute` never throws on an unpriced model so the diagnostic write path stays robust).

7. **`DbProviderPricingService : IProviderPricingService`** (`Tamma.Api/Services/Providers/`) implements `Compute`/`IsKnown` over the entity table (with a short-lived in-memory snapshot cache invalidated on admin write), and is registered **in place of** the frozen `ProviderPricingService` in `Program.cs`. The `IProviderPricingService` interface is **unchanged** (`Compute(provider, model?, in, out)` + `IsKnown(provider, model?)`); the only addition is an optional `atTimestamp`-aware path on the concrete impl / a sibling resolver. Downstream consumers keep their `IProviderPricingService` dependency — a one-line registration swap, no consumer code change.

8. **`ProviderPricingSeeder`** (`Tamma.Data/Seeders/ProviderPricingSeeder.cs`) ports the current frozen table **verbatim as v1 rows** (`Source = seed`, `Status = active`, `EffectiveFrom = <fixed seed epoch>`) with **deterministic UUIDv7 ids**, **insert-missing-only** (never reverts admin edits — mirrors `PlansSeeder`/`MarginPolicySeeder` and the convention system-defaults ownership rule). Each provider in `s_pricing` becomes a `Provider` row (with its `AuthModel`: `anthropic|openai|google|openrouter` = `api-key`, `claude-code` = `cli-token`, `local` = `api-key`/n-a) and each `(model, Rate)` becomes a `ProviderModelPrice` row (USD-per-token re-expressed as USD-per-1M). The frozen `ProviderPricingService` is **retained** as the seed source / boot fallback. The seeder is invoked in `Program.cs` alongside `PlansSeeder.SeedAsync` / `AgentEntitySeeder.SeedAsync`.

9. **Admin CRUD endpoints** (`AdminProviderPricingEndpoints` in `Tamma.Api/Endpoints/Admin/`), gated by the **`PlatformOwnerAccess`** policy (NOT `OwnerAccess`, which admits every personal-tenant owner):
   - `GET  /api/admin/providers` — list providers;
   - `GET  /api/admin/providers/{key}/prices` — list (active + superseded) price rows for a provider;
   - `PUT  /api/admin/providers/{key}/prices` — create/version a model price (supersede + insert, per AC4);
   - `POST /api/admin/providers` — register a provider; `PATCH /api/admin/providers/{key}` — set `Status`/`DisplayName`/`AuthModel`.
   Mutations emit DCB events to the control-plane store via `IEventRepository.AppendAsync`: `PROVIDER.PRICE.VERSIONED` (tags `providerKey, model, effectiveFrom, supersededPriceId, source=admin, actorUserId`) and `PROVIDER.REGISTERED` / `PROVIDER.STATUS_CHANGED`. A non-platform-owner gets **403** on every `/api/admin/providers*` route.

10. **DB wiring + migration.** An EF Core migration under `Tamma.Data/Migrations/ControlPlane/` adds `providers` and `provider_model_prices` (purely additive), and `dotnet ef migrations has-pending-model-changes` reports none afterward. **Both new tables are appended to the `Program.cs` startup-reset DROP list** ("Wiping Tamma-managed public-schema tables", ~line 2110) AND to the strict `ControlPlaneDbContextModelTests.Model_Has_ExpectedControlPlaneEntities` `BeEquivalentTo` list — a second test-host boot otherwise fails with `relation "providers" already exists`, and the model test otherwise fails its strict equivalence assertion.

11. **Per-mode handling.** The cost book is **platform-owned in both modes** — global control-plane rows, never tenant-scoped. Single-user mode: the sole user reads the cost book (no overrides; no per-user cost layer). SaaS mode: only platform owners (`PlatformOwnerAccess`) may register providers or version prices; tenant members get no read access to the admin routes (cost is internal). There is no per-tenant cost-rate override layer.

12. **Unit + integration tests** cover: alias normalization (all five aliases resolve to the canonical rate), loose prefix match, `null`/`"default"`→first-model, unknown-model → `Compute=0m` + `IsKnown=false` (no throw), the supersede/version chain (v1 → v2 with correct `Status` flip + one-active invariant rejected by the partial unique index), `EffectiveFrom`-windowed selection (an event at `t` prices under the row effective at `t`, NOT the latest), seeder idempotency (second `SeedAsync` is a no-op and does not revert an admin-edited row), DB-vs-frozen **parity** (the seeded `DbProviderPricingService` produces byte-identical `Compute` output to the frozen `ProviderPricingService` for every seeded `(provider, model)`), `PlatformOwnerAccess` 403 for non-owners, and event emission with correct tags.

## Technical Design

### Namespace & file structure

```
apps/tamma-elsa/src/
  Tamma.Data/Entities/
    Provider.cs                              # NEW (Tamma.Data.Entities) — cost identity + AuthModel
    ProviderModelPrice.cs                    # NEW — per-model versioned cost (USD-per-1M)
  Tamma.Data/
    ControlPlaneDbContext.cs                 # MODIFIED — 2 new DbSets + ConfigureProviders / ConfigureProviderModelPrices
    Seeders/ProviderPricingSeeder.cs         # NEW — ports frozen table verbatim as v1 rows (insert-missing-only)
    Migrations/ControlPlane/
      <timestamp>_ProviderCostPriceBook.cs   # NEW EF migration (additive)
  Tamma.Api/Services/Providers/
    IProviderPricingService.cs               # UNCHANGED (the seam — Compute / IsKnown)
    ProviderPricingService.cs                # KEPT — now the seed source / boot fallback (frozen table)
    DbProviderPricingService.cs              # NEW — IProviderPricingService over the entity table (+ EffectiveFrom-aware path)
    IProviderCostResolver.cs                 # NEW — EffectiveFrom-windowed row resolution (impure, DB-backed)
    ProviderCostResolver.cs                  # NEW
    ProviderPricingEventTypes.cs             # NEW — PROVIDER.PRICE.VERSIONED / PROVIDER.REGISTERED / PROVIDER.STATUS_CHANGED
  Tamma.Api/Endpoints/Admin/
    AdminProviderPricingEndpoints.cs         # NEW — /api/admin/providers* (PlatformOwnerAccess)
  Tamma.Api/Extensions/
    ProviderPricingServiceCollectionExtensions.cs  # NEW — DI wiring (swap registration)
  Tamma.Api/Program.cs                       # MODIFIED — DROP list (+2 tables); seed call; endpoint map; DI swap
```

### Entities

```csharp
namespace Tamma.Data.Entities;

/// <summary>
/// Control-plane PROVIDER entity (Story 34-11): the platform's COST identity
/// for an external LLM provider. Platform-global — NOT tenant-scoped (cost is
/// the provider's published rate, identical for every tenant). AuthModel feeds
/// 32-4 SaaS-eligibility. This is the *cost* primitive; sell price is 34-1
/// (PlanPrice) and markup is 34-5 (MarginPolicy).
/// </summary>
public class Provider
{
    public Guid Id { get; set; }                      // UUIDv7
    public string Key { get; set; } = null!;          // canonical: anthropic|openai|google|openrouter|local|claude-code
    public string DisplayName { get; set; } = null!;
    public string AuthModel { get; set; } = "api-key"; // api-key | cli-token (feeds 32-4)
    public string Status { get; set; } = "active";     // active | retired
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<ProviderModelPrice> Prices { get; set; } = [];
}

/// <summary>
/// The COST pricing — per model, versioned. An edit SUPERSEDES rather than
/// mutates (partial unique index on (ProviderKey, Model) WHERE Status='active');
/// EffectiveFrom-windowed so a usage event prices under the rate active at its
/// OccurredAt (reproducible/byte-stable). USD-per-1M tokens.
/// </summary>
public class ProviderModelPrice
{
    public Guid Id { get; set; }                      // UUIDv7
    public string ProviderKey { get; set; } = null!;  // canonical, alias-normalized on write
    public string Model { get; set; } = null!;        // e.g. claude-sonnet-4-20250514
    public decimal InputUsdPer1M { get; set; }
    public decimal OutputUsdPer1M { get; set; }
    public decimal? CacheReadUsdPer1M { get; set; }   // reserved (nullable)
    public decimal? CacheWriteUsdPer1M { get; set; }  // reserved (nullable)
    public DateTime EffectiveFrom { get; set; }       // UTC
    public string Status { get; set; } = "active";    // active | superseded
    public string Source { get; set; } = "seed";      // seed | admin (insert-missing-only seeder)
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

### EF model config (in `ControlPlaneDbContext.OnModelCreating`)

Mirrors the `ConfigurePlans` / `ConfigureAlertRules` style. `Provider` gets a unique index on `Key` and CHECKs on `AuthModel` / `Status`. `ProviderModelPrice` gets the immutability invariant in SQL:

```csharp
modelBuilder.Entity<Provider>(e =>
{
    e.ToTable("providers", t =>
    {
        t.HasCheckConstraint("ck_providers_auth_model", "\"AuthModel\" IN ('api-key','cli-token')");
        t.HasCheckConstraint("ck_providers_status", "\"Status\" IN ('active','retired')");
    });
    e.HasKey(x => x.Id);
    e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
    e.Property(x => x.Key).IsRequired().HasMaxLength(50);
    e.HasIndex(x => x.Key).IsUnique().HasDatabaseName("UX_providers_Key");
});

modelBuilder.Entity<ProviderModelPrice>(e =>
{
    e.ToTable("provider_model_prices", t =>
    {
        t.HasCheckConstraint("ck_provider_model_prices_status", "\"Status\" IN ('active','superseded')");
        t.HasCheckConstraint("ck_provider_model_prices_source", "\"Source\" IN ('seed','admin')");
    });
    e.HasKey(x => x.Id);
    e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
    e.Property(x => x.ProviderKey).IsRequired().HasMaxLength(50);
    e.Property(x => x.Model).IsRequired().HasMaxLength(200);
    e.Property(x => x.InputUsdPer1M).HasColumnType("decimal(20,8)");
    e.Property(x => x.OutputUsdPer1M).HasColumnType("decimal(20,8)");

    // Exactly one active price per (ProviderKey, Model) — the immutability
    // invariant in SQL (mirrors UX_plans_OneActivePerSlug / MarginPolicy).
    e.HasIndex(x => new { x.ProviderKey, x.Model })
        .HasDatabaseName("UX_provider_model_prices_OneActivePerModel")
        .HasFilter("\"Status\" = 'active'").IsUnique();

    // Resolution-window lookup (provider+model, ordered by EffectiveFrom).
    e.HasIndex(x => new { x.ProviderKey, x.Model, x.EffectiveFrom })
        .HasDatabaseName("IX_provider_model_prices_Window");

    e.HasOne<Provider>().WithMany(p => p.Prices)
        .HasForeignKey(x => x.ProviderKey).HasPrincipalKey(p => p.Key)
        .OnDelete(DeleteBehavior.Restrict);
});
```

### The `IProviderPricingService` seam (UNCHANGED) + the DB impl

```csharp
// IProviderPricingService.cs — NO CHANGE. The contract stays:
//   decimal Compute(string provider, string? model, int inputTokens, int outputTokens);
//   bool    IsKnown(string provider, string? model);

// DbProviderPricingService.cs — NEW. Reads the entity table; preserves the
// frozen table's alias map, loose prefix match, null/"default" rule, and the
// unknown→0m / IsKnown=false robustness contract — verbatim, just sourced from
// rows instead of a FrozenDictionary. Holds a short-lived snapshot cache
// (active rows) invalidated on admin write.
public sealed class DbProviderPricingService : IProviderPricingService
{
    public decimal Compute(string provider, string? model, int inputTokens, int outputTokens) { /* active-row lookup */ }
    public bool IsKnown(string provider, string? model) { /* active-row lookup */ }

    // EffectiveFrom-windowed overload used by the metering path (34-5 / 32-9):
    public decimal ComputeAt(string provider, string? model, int inputTokens, int outputTokens, DateTime atTimestamp);
}
```

The alias normalization map (`s_aliases`) and the `TryGetRate` algorithm (canonicalize → model-map → `null`/`"default"`→first → exact → loose-prefix) move into a shared static helper consumed by BOTH the frozen `ProviderPricingService` (seed source) and `DbProviderPricingService` (over rows), so the two cannot drift. AC12's parity test pins this.

### Cost vs price boundary (design §4.3 — explicit)

| Layer | This story owns? | Entity / seam | Answers |
|---|---|---|---|
| **COST** | **YES** | `Provider` + `ProviderModelPrice` behind `IProviderPricingService` | "What did this call cost us at the provider?" |
| PRICE — subscription | NO (34-1) | `Plan` / `PlanPrice` | "What does the plan cost the tenant?" |
| PRICE — markup | NO (34-5) | `MarginPolicy` / `IUsagePricingEngine` | "What sell price for platform-provided tokens?" |
| VIEW | NO (36-7) | `MarginAnalyticsService` (pure read) | "Are we making money?" |

`CostBasisUsd = ProviderPricingService.Compute(provider, model, in, out)` is the **input to 34-5**; `SellPriceUsd = CostBasisUsd × MarkupMultiplier (+ FixedUsdPer1M)`. `MarginPolicy.Scope='provider'` can override the *margin* per provider — **never** the *cost rate* (which lives here). 36-7 reads neither: it reads the already-persisted `CostUsd` (this basis) + `PlatformBilledUsd` (34-5 sell) columns 36-2 wrote.

### Seeder (insert-missing-only, verbatim port)

```csharp
// ProviderPricingSeeder.SeedAsync(ControlPlaneDbContext)
//   For each provider in ProviderPricingService.s_pricing:
//     upsert-if-missing a Provider row { Key, DisplayName, AuthModel, Status='active' }
//   For each (model, Rate) under that provider:
//     upsert-if-missing a ProviderModelPrice row {
//       ProviderKey = canonical, Model, EffectiveFrom = SEED_EPOCH,
//       Status='active', Source='seed',
//       InputUsdPer1M  = Rate.InputPerToken  * 1_000_000m,
//       OutputUsdPer1M = Rate.OutputPerToken * 1_000_000m }
//   Deterministic UUIDv7 per (key)/(key,model). Second run = no-op; never
//   reverts a Source='admin' row.
```

The frozen `ProviderPricingService` is retained verbatim (the seed source + a boot fallback if the table is empty). AuthModel mapping: `claude-code` → `cli-token` (CLI harness); all of `anthropic|openai|google|openrouter|local` → `api-key` (feeds 32-4's SaaS-eligibility — only `api-key` providers are SaaS-eligible).

### Admin endpoints (`PlatformOwnerAccess`)

```
GET    /api/admin/providers                      # list providers
POST   /api/admin/providers                      # register provider
PATCH  /api/admin/providers/{key}                # set Status / DisplayName / AuthModel
GET    /api/admin/providers/{key}/prices         # list active + superseded prices
PUT    /api/admin/providers/{key}/prices         # version a model price (supersede + insert)
```

`PUT .../prices` body `{ model, inputUsdPer1M, outputUsdPer1M, cacheReadUsdPer1M?, cacheWriteUsdPer1M?, effectiveFrom }`: normalize `ProviderKey` via the alias map, flip the current `active` row for `(key, model)` to `superseded`, insert a new `active` row (`Source='admin'`), emit `PROVIDER.PRICE.VERSIONED`. All routes gated by `PlatformOwnerAccess`; non-owner → 403.

### Migration sketch

```csharp
// <timestamp>_ProviderCostPriceBook.cs (Tamma.Data.Migrations.ControlPlane) — additive
migrationBuilder.CreateTable("providers", ...);            // Id, Key (UX), DisplayName, AuthModel, Status, timestamps
migrationBuilder.CreateTable("provider_model_prices", ...); // Id, ProviderKey FK→providers.Key, Model, In/Out + cache nullables, EffectiveFrom, Status, Source, timestamps
// CHECK + partial-unique (UX_provider_model_prices_OneActivePerModel) + window index per the model config above.
```

`has-pending-model-changes` must report none after generation.

## Dependencies

**Internal:**

- **Story 34-1** (Plan & Price-Book Catalog Data Model) — the CP-entity + immutable-versioning + insert-missing-only-seeder + `PlatformOwnerAccess` admin patterns this story reuses (sibling, not a hard blocker; both are sequence-A foundations).
- **Epic 1 / providers** — `IProviderPricingService` / `ProviderPricingService` (the seam being promoted) and the `s_aliases` map / `s_pricing` table being ported.
- **Epic 28** — `ControlPlaneDbContext`, the CP migration chain, `ControlPlaneDbContextModelTests`, and the `Program.cs` startup-reset DROP list this story extends.
- **`IEventRepository` (DCB)** — control-plane event store for `PROVIDER.*` admin events.
- **`PlatformOwnerAccess` policy** — platform-owner RBAC for `/api/admin/providers*`.

**Consumers (downstream — one-line DI dependency edit at most, NO AC/code change):**

- **Story 34-5** (Cost→Price Markup Engine) — `CostBasisUsd = IProviderPricingService.Compute(...)` and its `IsKnown` unknown-model gate; gains the `EffectiveFrom`-aware `ComputeAt` for reproducible historical pricing (34-5 AC7 cost side). **Sequenced after this story.**
- **Story 32-9** (cost-basis + margin metering) — emits the usage/cost event whose `CostUsd` comes from this seam.
- **Story 32-5** (managed agent execution) — `AgentRunResult.CostUsd = _pricing.Compute(...)` (the producer).
- **Story 36-2 / 36-7** (analytics) — read the persisted `CostUsd` written from this basis (36-7 does NOT re-read this entity).

**External:** none new.

## Testing Strategy

1. **Parity (AC12, the load-bearing one):** for every seeded `(provider, model)`, assert `DbProviderPricingService.Compute(...)` equals the frozen `ProviderPricingService.Compute(...)` byte-for-byte (input/output rates, integer-token math). Proves the promotion is behaviour-preserving.
2. **Alias normalization:** `anthropic-claude`, `claude` → anthropic rate; `gemini` → google; `github-copilot` → openai; `ollama`/`lmstudio` → local; both for lookup AND for `ProviderModelPrice.ProviderKey` on write.
3. **Loose prefix + default rules:** `claude-sonnet-4` matches stored `claude-sonnet-4-20250514`; `null`/`"default"` → first model; unknown → `Compute=0m`, `IsKnown=false`, no throw.
4. **Versioning / supersede:** `PUT` an edited price → prior row `superseded`, new row `active`; the partial unique index rejects a second `active` row for one `(ProviderKey, Model)`; mutating a `superseded` row throws `PROVIDER.PRICE.IMMUTABLE`.
5. **`EffectiveFrom`-windowed selection:** seed v1 at `t0`, version v2 at `t1`; `ComputeAt(..., t)` for `t0 <= t < t1` uses v1, for `t >= t1` uses v2 (priced under the rate active at the event time, not the latest).
6. **Seeder idempotency:** second `SeedAsync` is a no-op; an admin-edited (`Source='admin'`) row is NOT reverted.
7. **RBAC:** every `/api/admin/providers*` route returns 403 for a non-`PlatformOwnerAccess` caller; a platform owner succeeds.
8. **Events:** `PROVIDER.PRICE.VERSIONED` / `PROVIDER.REGISTERED` / `PROVIDER.STATUS_CHANGED` appended with the AC9 tags.
9. **Model-shape (AC3/AC10):** `Provider`/`ProviderModelPrice` carry no `TenantId`; `ControlPlaneDbContextModelTests.Model_Has_ExpectedControlPlaneEntities` includes `providers` + `provider_model_prices`; the partial unique index + CHECKs are present on the design-time model.
10. **Migration:** `dotnet ef migrations has-pending-model-changes` reports none; a second test-host boot does not fail with `relation already exists` (DROP-list extension verified).

Docker-bound C# suites run via `sg docker -c "dotnet test apps/tamma-elsa/..."` (session docker group is stale; plain `dotnet build` needs no wrapper).

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/Provider.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderModelPrice.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (2 DbSets + Configure* methods) |
| `apps/tamma-elsa/src/Tamma.Data/Seeders/ProviderPricingSeeder.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_ProviderCostPriceBook.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Providers/DbProviderPricingService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Providers/IProviderCostResolver.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderCostResolver.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderPricingEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderPricingService.cs` | Modify (extract shared alias/lookup helper; retain as seed/fallback) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminProviderPricingEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/ProviderPricingServiceCollectionExtensions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (DROP list +2 tables; seed call; endpoint map; DI swap) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs` | Modify (add `providers` + `provider_model_prices` to strict list) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Providers/DbProviderPricingServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Providers/ProviderPricingParityTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Providers/ProviderPricingSeederTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Endpoints/AdminProviderPricingEndpointsTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions
3. Reviewed `ProviderPricingService.cs` (the frozen table + alias map + `TryGetRate` you are porting), `Plan`/`PlansSeeder` and `MarginPolicy`/`MarginPolicySeeder` (the CP-entity + versioning + insert-missing-only patterns you mirror), and the `Program.cs` DROP list + `ControlPlaneDbContextModelTests` (the two lists you MUST extend)
4. Confirmed `PlatformOwnerAccess` (NOT `OwnerAccess`) is the policy for the admin routes
5. Planned TDD approach (Red-Green-Refactor; write the parity + versioning tests first)

### Key Design Decisions

- **Promote behind the seam, don't replace the seam.** `IProviderPricingService` (`Compute`/`IsKnown`) is the contract every downstream story already depends on. Swapping `ProviderPricingService` → `DbProviderPricingService` is a one-line DI edit; no consumer changes. The frozen table stays as the deterministic seed source + boot fallback. This is the explicit design §4.5 instruction: "swap the frozen table for a DB read; keep the interface."
- **Cost is mode-independent and tenant-independent.** The most important boundary: BYOK vs platform-provided (32-3 `credentialSource`) changes only the *sell* price (34-5: markup when platform, 0 token price when BYOK) — the cost basis is computed identically. There is therefore NO tenant column on either entity (design §4.4).
- **Immutable-versioned = reproducible cost.** Reuse 34-1/34-5's exact pattern: partial unique index for one-active, `EffectiveFrom` window for time-travel. A re-priced model must not retroactively change historical invoices — the cost-side companion of 34-5 AC7.
- **Don't drop the load-bearing quirks.** The alias map, loose prefix match, and `null`/`"default"`→first rule are not incidental — 34-5's `IsKnown` gate and the diagnostic write path depend on them. They move into the entity-backed resolver verbatim (shared helper), proven by the parity test.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns the cost book? | The platform (the engine ships it); the **sole user reads it**. It is global, not user-scoped — there is no per-user cost-rate override. | The platform (control-plane rows). It is global across all tenants — there is no per-tenant cost-rate override. |
| Who may register a provider / version a price? | The sole user (acting as platform owner). | **`PlatformOwnerAccess` only** (NOT `OwnerAccess`). Tenant owners/admins/members cannot — cost config is internal. |
| Where do the entities live? | `ControlPlaneDbContext` public-schema (`providers` / `provider_model_prices`). | Same — control-plane, never the tenant `t_<hex>` schema. |
| Where do `PROVIDER.*` admin events land? | Control-plane DCB store (`IEventRepository`). | Same control-plane store; tagged with `actorUserId`. |
| Does BYOK/tenant affect the cost? | No — cost is the provider's published rate. | No — cost is identical for every tenant; only the *sell* price (34-5) differs by `credentialSource`. |
| Mode source | `ITammaModeProvider` — process-stable. | same |

### Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| Forgetting to add `providers`/`provider_model_prices` to the `Program.cs` DROP list → 2nd test-host boot fails `relation already exists` | High | Explicit AC10 + a test that boots the host twice; checklist item; cite the exact line (~2110). |
| Forgetting to update `ControlPlaneDbContextModelTests` strict `BeEquivalentTo` list → CP model test fails | High | Explicit AC10; the model test is part of the suite; add the two tables in the same commit as the entities. |
| DB resolver drifts from the frozen behaviour (alias / prefix / default / unknown→0) | High | Extract ONE shared alias+lookup helper consumed by both impls; the parity test (AC12.1) asserts byte-identical `Compute` for every seeded pair. |
| Tenant accidentally scoped onto a cost entity | High | AC3 model test asserts no `TenantId`/`UserId` property; cost is global by design §4.4. |
| Using `OwnerAccess` instead of `PlatformOwnerAccess` (admits every personal-tenant owner) | High | AC9 pins `PlatformOwnerAccess`; RBAC test asserts 403 for a non-platform-owner. |
| Mutating cost retroactively breaks historical invoices | Medium | Immutable-versioning (supersede + `EffectiveFrom` window); `ComputeAt` used by the metering path; windowed-selection test. |
| Seeder reverts admin edits on redeploy | Medium | Insert-missing-only (mirrors `PlansSeeder`); idempotency test asserts a `Source='admin'` row survives a re-seed. |
| Decimal precision drift (USD-per-token ↔ USD-per-1M round-trip) | Medium | Store `decimal(20,8)` USD-per-1M; integer-token math at `Compute`; parity test covers every seeded model including sub-cent rates (`gemini-1.5-flash` @ $0.075/1M). |

### Success Metrics

- [ ] `IProviderPricingService` interface unchanged; `DbProviderPricingService` registered in place of the frozen impl with a one-line DI swap; zero downstream consumer code edits.
- [ ] Parity test green for 100% of seeded `(provider, model)` pairs (DB == frozen).
- [ ] `dotnet ef migrations has-pending-model-changes` reports none; the test host boots twice without `relation already exists`.
- [ ] `ControlPlaneDbContextModelTests.Model_Has_ExpectedControlPlaneEntities` passes with `providers` + `provider_model_prices` added.

## Related

- Design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§3 PROVIDER cost entity, §4 cost-vs-price three layers + §4.5 "new story behind the seam")
- Re-plan: `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md`
- Implementation plan: `docs/superpowers/plans/2026-06-21-34-11-provider-cost-price-book-plan.md`
- Sibling stories: `docs/stories/epic-34/story-34-1/` (price book — sell), `story-34-5/` (markup engine — consumer), `docs/stories/epic-32/story-32-5/` (managed execution — consumer), `story-32-9/` (metering — consumer)
- Promoted seam: `apps/tamma-elsa/src/Tamma.Api/Services/Providers/IProviderPricingService.cs` + `ProviderPricingService.cs`

## Logging Requirements

- **INFO**: provider registered (`providerKey`, `authModel`), price versioned (`providerKey`, `model`, `effectiveFrom`, `supersededPriceId`), seeder summary (providers/prices inserted vs skipped).
- **DEBUG**: cost resolution (`providerKey`, `model`, `atTimestamp`, resolved `priceId`, `effectiveFrom`), alias normalization (`requested` → `canonical`), snapshot-cache hit/miss + invalidation on admin write.
- **WARN**: unknown `(provider, model)` lookups (returns `0m` — visible misconfiguration signal, same as `IsKnown=false`); a `ComputeAt` with no row effective at the timestamp (returns `0m`); seeder skip of an admin-edited row.
- **ERROR**: immutability violation (`PROVIDER.PRICE.IMMUTABLE` attempt on a `superseded` row); DCB event append failure (operation still returns; the append failure is logged, not silently swallowed); migration / DROP-list mismatch at boot.
- **Structured context**: include `{ providerKey, model, effectiveFrom, status, source, actorUserId }` where applicable.
- **Credential safety**: this story handles COST RATES, not credentials — but never log API keys, tokens, or BYOK material (the cost entity carries none; the credential resolver is 32-3). The `AuthModel` label (`api-key`/`cli-token`) is safe to log; no key ever touches this path.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-21 | 1.0.0   | Initial story creation | Claude |
