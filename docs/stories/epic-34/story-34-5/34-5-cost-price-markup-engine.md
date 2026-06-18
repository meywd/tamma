# Story 34-5: Cost->Price Markup Engine (platform-provided usage)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase workflow (Read -> Research -> Break Down -> TDD ->
Quality Gates -> Failure Handling), knowledge-base usage (`.dev/`), TRACE/DEBUG logging
requirements, test-first development, and the build/coverage gates. **Failure to follow this
process will result in rework.**

## User Story

As a **platform owner monetizing platform-provided AI usage**,
I want a pure, deterministic engine that turns a measured usage event into a priced amount by
applying a configurable margin policy on top of the provider cost basis (and applies **no** token
markup for BYOK tenants),
so that Billing (Epic 35), the cost dashboard, and the upgrade/estimate UI all compute the same
sell price from a single canonical source instead of each re-implementing markup math.

## Priority

P0 - The cost->price engine is the canonical pricing primitive that Billing (Epic 35), usage
analytics (Epic 36-7), and the entitlement/quota UI all consume. Without it those layers would each
re-derive markup, drift, and produce inconsistent invoices.

## Acceptance Criteria

1. New control-plane entity `MarginPolicy` (`Tamma.Data.Entities.MarginPolicy`) added to
   `ControlPlaneDbContext` with columns: `Id` (UUIDv7), `Scope` (`global|plan|provider`), `RefKey`
   (NULL for global; plan slug for plan scope; canonical provider key for provider scope),
   `MarkupMultiplier` (nullable `decimal`), `FixedUsdPer1M` (nullable `decimal`), `EffectiveFrom`
   (`timestamptz`), `Status` (`active|superseded`), `CreatedAt`, `UpdatedAt`. An EF migration is
   added under `Tamma.Data/Migrations/ControlPlane/` and `dotnet ef migrations
   has-pending-model-changes` reports none afterward.
2. A CHECK constraint enforces that **at least one** of `MarkupMultiplier` / `FixedUsdPer1M` is
   non-null (no all-null policy row), and a partial unique index enforces exactly one `active`
   policy per `(Scope, RefKey)` (`RefKey` compared with NULLS NOT DISTINCT for the global row).
3. `MarginPolicySeeder.SeedAsync(ControlPlaneDbContext)` seeds a deterministic-UUIDv7 default
   **global** policy of `MarkupMultiplier = 1.3` (insert-missing-only, never reverts admin edits —
   mirrors the convention/plan system-defaults ownership rule). Registered in `Program.cs`
   alongside `PlansSeeder.SeedAsync` (~line 2030).
4. Resolution order in `IMarginPolicyResolver.ResolveAsync(provider, planSlug, atTimestamp)` is
   strictly **provider-override -> plan -> global -> error**: pick the most-specific `active`
   policy whose `EffectiveFrom <= atTimestamp`; if none matches at any level, throw
   `TammaError("PRICING.MARGIN.NO_POLICY", ..., severity High)` — **never** silently price at a
   zero margin (mirrors the no-empty-fallback rule).
5. `IUsagePricingEngine.PriceUsage(UsageLine)` returns a `PricedUsage` record
   `{ CostBasisUsd, MarginUsd, SellPriceUsd, PricingMode }`. For `PricingMode.PlatformProvided`:
   `CostBasisUsd = ProviderPricingService.Compute(provider, model, inputTokens, outputTokens)` and
   `SellPriceUsd = CostBasisUsd * MarkupMultiplier (+ FixedUsdPer1M * totalTokens/1_000_000)`,
   `MarginUsd = SellPriceUsd - CostBasisUsd`. For `PricingMode.Byok`: `CostBasisUsd` is still
   computed (for reporting/analytics) but the **token component of** `SellPriceUsd` is `0` and
   `MarginUsd` is `0` (the plan/seat fee is Billing's concern, not this engine's).
6. The engine reads `inputTokens`, `outputTokens`, `provider`, `model`, and `pricingMode` from the
   `UsageLine` input, which is built from the `ProviderDiagnostic` row (its `InputTokens` /
   `OutputTokens` / `ProviderKey` / `Model` / `BillingMode` columns, the latter added by Story 34-3)
   or from the equivalent DCB usage event emitted in Epic 32-9 — so per-call cost basis is exact
   (input and output billed at different rates by `ProviderPricingService`).
7. Pricing is **reproducible / byte-stable**: given the same `UsageLine` plus the `MarginPolicy`
   that was `active` and `EffectiveFrom <= UsageLine.OccurredAt`, the output is identical across
   runs — internal arithmetic carries 6 decimal places (`Math.Round(x, 6, MidpointRounding.ToEven)`
   at each accumulation boundary), invoice-facing values round to 2dp. This is pinned by a
   golden-file test (`PriceUsage_GoldenScenarios_AreByteStable`).
8. Unknown `(provider, model)` (i.e. `ProviderPricingService.IsKnown` returns false) surfaces a
   typed `TammaError("PRICING.UNKNOWN_MODEL", ..., severity Medium)` — the engine does **not**
   silently price at `0` so misconfiguration is visible. A WARN is logged with `provider` + `model`.
9. Admin API `GET /api/admin/pricing/margins` and `PUT /api/admin/pricing/margins`
   (`AdminPricingEndpoints`, gated by the `PlatformOwnerAccess` policy) view and version margin
   policies. A `PUT` that changes an existing `active` policy flips the prior row to `superseded`
   and inserts a new `active` row (versioning, not in-place mutation), and emits
   `PRICING.MARGIN.UPDATED` to the control-plane `DomainEvents` store via `IEventRepository.AppendAsync`
   with tags `{ scope, refKey, actorUserId }`.
10. Tenant API `GET /api/pricing/estimate` (`PricingEndpoints`, `MemberAccess`) returns a priced
    estimate for a hypothetical `UsageLine` (provider, model, inputTokens, outputTokens) under the
    **current tenant's plan + the tenant's pricing mode for that provider** (BYOK vs platform-
    provided, resolved through the Story 34-3 seam) — powering the upgrade/cost UI in
    `packages/dashboard-user`. In single-user mode the sole user's instance plan is used.
11. The engine is **pure / side-effect-free**: `PriceUsage` does no I/O, takes the resolved
    `MarginPolicy` (or a `MarginContext`) and `UsageLine` as inputs, and returns the `PricedUsage`
    result — so Billing and the dashboard can both call it deterministically. Policy resolution
    (the DB read) lives in `IMarginPolicyResolver`, kept separate from the pure engine.
12. Per-mode + per-tenant: margin policies are platform-owned global config (no per-tenant margin
    rows) — only `PlatformOwnerAccess` may mutate them in SaaS; in single-user mode the sole user
    owns them. The **pricing mode** (BYOK vs platform) is per-`(tenant, provider)` and is read from
    Story 34-3's `TenantProviderBilling` / `ProviderDiagnostic.BillingMode`, never invented here.
13. Unit tests cover: platform markup math (multiplier-only, fixed-per-1M-only, both combined),
    BYOK zero-token-markup with non-zero cost basis, margin resolution order
    (provider > plan > global), timestamp-effective policy selection (an event priced under the
    policy that was active at its `OccurredAt`, not the latest), unknown-model error, no-policy
    error, and rounding determinism (6dp internal / 2dp invoice).
14. Tenant-isolation test: a tenant's `GET /api/pricing/estimate` resolves only its own plan and
    its own `(tenant, provider)` pricing mode; it can never read or mutate `MarginPolicy` rows
    (403 on any `/api/admin/pricing/*` route for a non-platform-owner).

## Technical Design

### Namespace / File Structure

```
apps/tamma-elsa/src/
  Tamma.Data/
    Entities/
      MarginPolicy.cs                         # NEW entity (control plane)
    ControlPlaneDbContext.cs                   # MODIFY: add DbSet<MarginPolicy> + model config
    Migrations/ControlPlane/
      <ts>_AddMarginPolicy.cs                  # NEW EF migration (additive)
    Seeders/
      MarginPolicySeeder.cs                    # NEW seeder (global 1.3x default, insert-missing-only)
  Tamma.Core/
    Enums/
      PricingMode.cs                           # NEW enum (PlatformProvided | Byok) — or reuse 34-1's
                                               #   PlanPrice.PricingMode if it lands as a shared enum
  Tamma.Api/
    Services/Pricing/                          # dir created by 34-1; this story adds to it
      IUsagePricingEngine.cs                   # NEW — pure engine contract
      UsagePricingEngine.cs                    # NEW — pure engine impl
      UsageLine.cs                             # NEW — engine input record
      PricedUsage.cs                           # NEW — engine output record
      IMarginPolicyResolver.cs                 # NEW — DB-backed policy resolution (impure)
      MarginPolicyResolver.cs                  # NEW
      PricingEventTypes.cs                      # NEW — DCB event-name constants
    Endpoints/
      Admin/AdminPricingEndpoints.cs           # NEW — GET/PUT /api/admin/pricing/margins
      PricingEndpoints.cs                      # 34-3 creates this; this story adds GET /estimate
    Extensions/
      PricingServiceCollectionExtensions.cs    # NEW (or extend 34-1's) — DI wiring
    Program.cs                                  # MODIFY: map endpoints + register seeder + DI
```

> **Boundary note (honored):** this story is the CANONICAL owner of the cost->price markup/margin
> engine (cost basis from `ProviderPricingService` + margin -> sell price). It does **not**
> re-implement the provider cost table (that is `ProviderPricingService`, Epic providers), does
> **not** select BYOK-vs-platform mode (that is Story 34-3's `TenantProviderBilling` /
> `IProviderKeyResolver`), does **not** produce the usage event (Epic 32-9), and does **not** move
> money or render invoices (Epic 35 Billing). Those epics **consume** this engine and MUST NOT
> re-implement markup.

### Entity: `MarginPolicy`

```csharp
namespace Tamma.Data.Entities;

/// <summary>
/// Control-plane margin policy applied by the cost->price engine (Story 34-5).
/// Versioned: an edit supersedes the prior active row rather than mutating it,
/// so a usage event is always priced under the policy that was active at its
/// timestamp. Platform-owned global config — no per-tenant margin rows exist.
/// </summary>
public class MarginPolicy
{
    public Guid Id { get; set; }                       // UUIDv7

    /// <summary>"global" | "plan" | "provider".</summary>
    public string Scope { get; set; } = null!;

    /// <summary>NULL for global; plan slug for plan scope; canonical provider key for provider scope.</summary>
    public string? RefKey { get; set; }

    /// <summary>Multiplicative markup on the provider cost basis (e.g. 1.3 = +30%). Nullable.</summary>
    public decimal? MarkupMultiplier { get; set; }

    /// <summary>Additive USD per 1,000,000 total tokens. Nullable. At least one of the two is set.</summary>
    public decimal? FixedUsdPer1M { get; set; }

    public DateTime EffectiveFrom { get; set; }        // timestamptz, UTC
    public string Status { get; set; } = "active";     // "active" | "superseded"
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

EF model config (in `ControlPlaneDbContext.OnModelCreating`, mirroring existing entity config):

```csharp
modelBuilder.Entity<MarginPolicy>(e =>
{
    e.ToTable("margin_policies", t =>
    {
        t.HasCheckConstraint("ck_margin_scope",
            "scope IN ('global','plan','provider')");
        t.HasCheckConstraint("ck_margin_status",
            "status IN ('active','superseded')");
        // At least one knob must be set — never an all-null policy.
        t.HasCheckConstraint("ck_margin_has_knob",
            "markup_multiplier IS NOT NULL OR fixed_usd_per_1m IS NOT NULL");
    });
    // Exactly one active policy per (scope, ref_key); NULLS NOT DISTINCT so the
    // single global row (ref_key NULL) is unique too.
    e.HasIndex(p => new { p.Scope, p.RefKey })
        .HasFilter("status = 'active'")
        .IsUnique()
        .AreNullsDistinct(false);
});
```

### Engine input / output records

```csharp
namespace Tamma.Api.Services.Pricing;

/// <summary>One measured usage event to be priced. Built from a ProviderDiagnostic
/// row or the equivalent DCB usage event (Epic 32-9 / Story 34-3).</summary>
public sealed record UsageLine(
    string Provider,
    string? Model,
    int InputTokens,
    int OutputTokens,
    PricingMode PricingMode,
    DateTime OccurredAt);            // UTC — drives timestamp-effective policy selection

/// <summary>Result of pricing a single usage line.</summary>
public sealed record PricedUsage(
    decimal CostBasisUsd,            // provider cost (always computed)
    decimal MarginUsd,               // SellPriceUsd - CostBasisUsd (0 for BYOK token component)
    decimal SellPriceUsd,            // what the tenant is charged for tokens
    PricingMode PricingMode);
```

### Pure engine contract

```csharp
public interface IUsagePricingEngine
{
    /// <summary>Pure, deterministic. Throws PRICING.UNKNOWN_MODEL if the
    /// (provider, model) is unpriced. The resolved policy is passed in so the
    /// engine does no I/O; resolution lives in IMarginPolicyResolver.</summary>
    PricedUsage PriceUsage(UsageLine line, MarginPolicy policy);
}
```

Engine arithmetic (6dp internal, even-rounding at each boundary):

```csharp
public PricedUsage PriceUsage(UsageLine line, MarginPolicy policy)
{
    if (!_pricing.IsKnown(line.Provider, line.Model))
        throw new TammaError("PRICING.UNKNOWN_MODEL", $"No pricing for {line.Provider}/{line.Model}",
            new() { ["provider"] = line.Provider, ["model"] = line.Model ?? "" }, retryable: false,
            severity: "medium");

    var costBasis = Round6(_pricing.Compute(line.Provider, line.Model,
        line.InputTokens, line.OutputTokens));

    if (line.PricingMode == PricingMode.Byok)
        return new PricedUsage(costBasis, 0m, 0m, PricingMode.Byok); // no token markup for BYOK

    var totalTokens = (long)Math.Max(0, line.InputTokens) + Math.Max(0, line.OutputTokens);
    var multiplied = Round6(costBasis * (policy.MarkupMultiplier ?? 1m));
    var fixedAdd   = Round6((policy.FixedUsdPer1M ?? 0m) * (totalTokens / 1_000_000m));
    var sell       = Round6(multiplied + fixedAdd);
    return new PricedUsage(costBasis, Round6(sell - costBasis), sell, PricingMode.PlatformProvided);
}

private static decimal Round6(decimal v) => Math.Round(v, 6, MidpointRounding.ToEven);
```

### Policy resolver (impure — the only DB read)

```csharp
public interface IMarginPolicyResolver
{
    /// <summary>provider-override -> plan -> global -> throw PRICING.MARGIN.NO_POLICY.
    /// Picks the most-specific active policy whose EffectiveFrom &lt;= atTimestamp.</summary>
    Task<MarginPolicy> ResolveAsync(string provider, string? planSlug, DateTime atTimestamp, CancellationToken ct);
}
```

`MarginPolicyResolver` queries `ControlPlaneDbContext.MarginPolicies` for `Status == "active"`
(plus `superseded` rows whose window covers `atTimestamp`, ordered by `EffectiveFrom desc`) at each
scope in priority order, returning the first hit. No match -> `TammaError("PRICING.MARGIN.NO_POLICY")`.

### DCB events

| Event | Store | Tags | When |
|---|---|---|---|
| `PRICING.MARGIN.UPDATED` | CP `DomainEvents` via `IEventRepository.AppendAsync` | `scope`, `refKey`, `actorUserId` | admin `PUT /api/admin/pricing/margins` supersedes + inserts |

Event name follows the `AGGREGATE.ACTION.STATUS` convention (`PRICING.MARGIN.UPDATED`). Constants
live in `PricingEventTypes.cs`. No event is emitted by the pure engine itself (it does no I/O);
pricing reads are side-effect-free.

### API shape

```
# Admin (PlatformOwnerAccess) — mounted on the existing /api/admin group
GET  /api/admin/pricing/margins            -> { policies: MarginPolicyDto[] }   (all scopes, active + history)
PUT  /api/admin/pricing/margins            body: { scope, refKey?, markupMultiplier?, fixedUsdPer1M?, effectiveFrom? }
                                            -> 200 { policy: MarginPolicyDto }   (supersede + insert; emits PRICING.MARGIN.UPDATED)

# Tenant (MemberAccess) — mounted on a new /api/pricing group
GET  /api/pricing/estimate?provider=&model=&inputTokens=&outputTokens=
                                            -> { costBasisUsd, marginUsd, sellPriceUsd, pricingMode }
```

`AdminPricingEndpoints` follows the static-handler style of `AdminAnalyticsEndpoints` /
`ConventionStoreEndpoints`; mapped in `Program.cs` under a new
`app.MapGroup("/api/admin/pricing").RequireAuthorization("PlatformOwnerAccess")` group (mirrors the
`adminConventions` group at ~line 1769). `PricingEndpoints.GetEstimate` is added to the `/api/pricing`
group with `RequireAuthorization("MemberAccess")` (mirrors the `orgs` group at ~line 1512); it reads
the caller's tenant via `ITenantContext.TenantId` (SaaS) / falls back to the single-user instance
plan, resolves the plan slug via `IPlanCatalogService` (Story 34-1) and the `(tenant, provider)`
pricing mode via the Story 34-3 seam, then calls `IMarginPolicyResolver` + `IUsagePricingEngine`.

### DI wiring (`PricingServiceCollectionExtensions`)

```csharp
services.TryAddSingleton<IUsagePricingEngine, UsagePricingEngine>(); // pure, depends on IProviderPricingService (already singleton)
services.TryAddScoped<IMarginPolicyResolver, MarginPolicyResolver>(); // scoped — reads ControlPlaneDbContext (scoped)
```

`Program.cs` calls `MarginPolicySeeder.SeedAsync(dbContext)` next to `PlansSeeder.SeedAsync`
(~line 2030) inside the same startup migration/seed scope.

### Per-mode + per-tenant handling

| Concern | single-user mode | SaaS mode |
|---|---|---|
| Who owns `MarginPolicy` rows? | The sole user (their instance). | Platform owner ONLY (`PlatformOwnerAccess`). Tenants never see or set margins. |
| `GET/PUT /api/admin/pricing/margins` | sole user (admin of own instance). | `PlatformOwnerAccess`; tenant roles -> 403. |
| Pricing mode (BYOK vs platform) | default platform-provided unless the user sets BYOK. | per-`(tenant, provider)` from Story 34-3 `TenantProviderBilling`. |
| `GET /api/pricing/estimate` | sole user's instance plan. | caller's tenant plan (`ITenantContext`), tenant's provider mode. |

## Dependencies

**Internal (prerequisite):**
- **Story 34-1** (Plan & Price-Book Catalog) — provides `IPlanCatalogService` / `PlanSnapshot` (to
  resolve the caller's plan slug for the estimate) and the `PricingMode` (`platform_provided|byok`)
  shared concept on `PlanPrice`. Also creates the `Services/Pricing/` directory this story extends.
- **Story 34-3** (BYOK vs Platform-Provided Pricing Mode) — provides per-`(tenant, provider)` mode
  (`TenantProviderBilling`) and the `ProviderDiagnostic.BillingMode` column / DCB usage tag that the
  engine reads to decide whether to apply markup.
- **Epic 28** (control-plane / tenancy) — `ControlPlaneDbContext`, `ITenantContext`,
  `ITammaModeProvider`, `IEventRepository` (CP `DomainEvents`).
- **`ProviderPricingService`** (existing, `Tamma.Api/Services/Providers/`) — the cost-basis source
  (`Compute` / `IsKnown`).

**Internal (blocks / consumers — must NOT re-implement markup):**
- **Epic 32-9** — usage-event producer; emits the usage line this engine prices.
- **Epic 35** (Billing) — calls `IUsagePricingEngine` to compute invoice line sell prices.
- **Epic 36-7** (analytics/cost view) — calls the engine for priced cost reporting.

**External:** none for the engine itself. Billing's Stripe integration (Epic 35) and provider APIs
are out of scope; tests mock `IProviderPricingService` and any provider/Stripe seams.

## Testing Strategy

Test framework: NUnit + FluentAssertions + Moq (matches `Tamma.Api.Tests`). DB-bound suites run via
`sg docker -c "dotnet test ..."`. Test-first (TDD) — every behavior below gets a failing test first.

1. **Engine unit tests** (`tests/Tamma.Api.Tests/Pricing/UsagePricingEngineTests.cs`, pure — mock
   `IProviderPricingService`):
   - multiplier-only markup (`1.3x` of a known cost basis),
   - fixed-per-1M-only markup,
   - multiplier + fixed combined,
   - BYOK: non-zero `CostBasisUsd`, `SellPriceUsd == 0`, `MarginUsd == 0`,
   - unknown model -> `PRICING.UNKNOWN_MODEL` thrown, WARN logged,
   - rounding determinism: a cost basis that would produce >6dp is rounded even at 6dp; invoice
     projection at 2dp,
   - **golden-file test** `PriceUsage_GoldenScenarios_AreByteStable`: a fixed set of `(UsageLine,
     MarginPolicy)` -> serialized `PricedUsage` compared byte-for-byte against a committed
     `golden/pricing-scenarios.json`.
2. **Resolver unit tests** (`MarginPolicyResolverTests.cs`, in-memory/Sqlite `ControlPlaneDbContext`
   or mocked DbSet):
   - resolution order provider > plan > global,
   - timestamp-effective selection (two `superseded`/`active` rows with different `EffectiveFrom` —
     an event at `t` resolves the row active at `t`, not the latest),
   - no policy at any scope -> `PRICING.MARGIN.NO_POLICY`.
3. **Seeder test:** `MarginPolicySeeder` inserts the global `1.3x` row on a fresh DB; a second run
   is a no-op and does NOT revert an admin-edited global multiplier (insert-missing-only).
4. **Admin endpoint integration** (`AdminPricingEndpointsTests.cs`): `PUT` supersedes the prior
   active row, inserts a new active row, emits exactly one `PRICING.MARGIN.UPDATED` event; `GET`
   returns active + history; a non-platform-owner gets 403.
5. **Estimate endpoint integration** (`PricingEstimateEndpointTests.cs`): platform-provided tenant
   gets a marked-up estimate; BYOK tenant gets `sellPriceUsd == 0` for the token component;
   unknown model -> 4xx with `PRICING.UNKNOWN_MODEL`.
6. **Tenant-isolation test:** tenant A's estimate resolves only A's plan and A's `(tenant,
   provider)` mode; any `/api/admin/pricing/*` call by a tenant role -> 403; an estimate never reads
   another tenant's data.
7. **Mocks:** `IProviderPricingService` is mocked/stubbed in engine unit tests (cost basis is a
   controlled input); Stripe/provider HTTP never touched (this engine moves no money).

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/MarginPolicy.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (DbSet + model config) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddMarginPolicy.cs` | Create (EF migration) |
| `apps/tamma-elsa/src/Tamma.Data/Seeders/MarginPolicySeeder.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Enums/PricingMode.cs` | Create (or reuse 34-1 shared enum) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/IUsagePricingEngine.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/UsagePricingEngine.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/UsageLine.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PricedUsage.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/IMarginPolicyResolver.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/MarginPolicyResolver.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PricingEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminPricingEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/PricingEndpoints.cs` | Modify (add `GetEstimate`; file created by 34-3) |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/PricingServiceCollectionExtensions.cs` | Create (or extend 34-1's) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map endpoints, register seeder + DI) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/UsagePricingEngineTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/MarginPolicyResolverTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/MarginPolicySeederTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/AdminPricingEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PricingEstimateEndpointTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/golden/pricing-scenarios.json` | Create (golden file) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions (cost-monitor, pricing).
3. Read Story 34-1 and 34-3 first — this story depends on `IPlanCatalogService`, the `PricingMode`
   shared concept, and `ProviderDiagnostic.BillingMode`.
4. Verified `ProviderPricingService.Compute` / `IsKnown` semantics (existing code: unknown pairs
   return `0m` from `Compute`, so this engine MUST gate on `IsKnown` to surface `PRICING.UNKNOWN_MODEL`
   instead of inheriting the silent-zero).
5. Planned the TDD approach (Red-Green-Refactor), starting with the pure engine tests.

### Key Design Decisions

- **Pure engine, impure resolver.** `PriceUsage` is total and side-effect-free; the DB read for the
  applicable policy is isolated in `IMarginPolicyResolver`. This is what lets Billing and the
  dashboard call the engine deterministically and is what makes the golden-file test possible.
- **Versioned policies, not in-place edits.** A `PUT` supersedes; old usage events stay priced under
  the policy that was active at their `OccurredAt`. This mirrors the immutable-version discipline in
  Story 34-1's plan catalog and is required for reproducible historical invoices.
- **Two independent fail-loud paths.** Unknown model -> `PRICING.UNKNOWN_MODEL`; no applicable
  policy -> `PRICING.MARGIN.NO_POLICY`. Neither silently prices at zero — this is the no-empty-
  fallback rule applied to pricing. `ProviderPricingService.Compute` returns `0m` for unknown pairs;
  we deliberately gate on `IsKnown` first so a misconfigured model is loud, not free.
- **BYOK token markup is exactly zero, but cost basis is still computed.** Analytics/reporting want
  the would-be cost even for BYOK; only the chargeable token component is zeroed. Plan/seat fees are
  Billing's (Epic 35) concern, not this engine's.
- **6dp internal / 2dp invoice rounding with `MidpointRounding.ToEven`.** Banker's rounding at each
  accumulation boundary keeps the golden file stable and avoids per-call drift.

### Risks and Mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| `ProviderPricingService` silent-zero for unknown pairs leaks into pricing | High | Gate on `IsKnown` first; throw `PRICING.UNKNOWN_MODEL`; covered by a unit test |
| Floating-point / rounding drift across runs breaks reproducibility | Medium | `decimal` throughout, even-rounding at fixed boundaries, golden-file regression test |
| Margin policy edited in place would re-price historical usage | High | Versioned supersede + `EffectiveFrom`-windowed resolution; timestamp-effective test |
| Consumer epics (35, 36-7, 32-9) re-implement markup | Medium | This story is the canonical owner; expose `IUsagePricingEngine`; boundary note in epic |
| Estimate leaks cross-tenant plan/mode | High | Resolve plan/mode strictly from `ITenantContext`; tenant-isolation test; admin routes 403 for tenants |

## Logging Requirements

- **INFO:** `PRICING.MARGIN.UPDATED` applied (scope, refKey, actorUserId); estimate served
  (tenantId, provider, pricingMode).
- **DEBUG:** policy resolved (scope hit, effectiveFrom); priced line (costBasisUsd, sellPriceUsd) —
  amounts only, never secrets.
- **WARN:** unknown `(provider, model)` -> `PRICING.UNKNOWN_MODEL` (provider, model); no applicable
  policy -> `PRICING.MARGIN.NO_POLICY` (provider, planSlug).
- **ERROR:** margin-policy persistence failure on `PUT`; estimate handler unexpected failure.
- **Structured context:** include `{ provider, model, pricingMode, scope, refKey, tenantId }` where
  applicable.
- **Credential safety:** NEVER log provider API keys, secret cabinet refs, or BYOK key material —
  this engine only sees token counts and USD amounts.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
