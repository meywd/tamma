# Story 34-5: Cost->Price Markup Engine — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan phase-by-phase. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every phase writes failing
> tests before implementation.

**Story:** `docs/stories/epic-34/story-34-5/34-5-cost-price-markup-engine.md` | **Epic 34** Pricing,
Plans & Entitlements | **Priority** P0 | **Effort** 4-5 days | **Date** 2026-06-17

---

## Goal

Build the canonical, pure, deterministic engine that turns a measured usage event into a priced
amount: provider cost basis (`ProviderPricingService`) × a versioned, configurable margin policy ->
sell price for platform-provided usage, and **zero** token markup for BYOK usage. Ship the
control-plane `MarginPolicy` entity + resolver, the platform-owner admin API to view/version
margins, and a tenant-facing estimate endpoint. The engine moves no money; Billing (Epic 35), cost
analytics (36-7), and the producer (32-9) all consume it.

## Non-goals (YAGNI guard)

- **NO money movement, no invoices, no Stripe.** Sell price computation only; Epic 35 charges.
- **NO provider cost table changes.** `ProviderPricingService` is the cost-basis source of truth;
  this story consumes `Compute`/`IsKnown`, it does not edit the rate sheet.
- **NO BYOK-vs-platform mode selection.** That is Story 34-3 (`TenantProviderBilling`,
  `IProviderKeyResolver`, `ProviderDiagnostic.BillingMode`). This engine *reads* the mode.
- **NO usage-event production.** Epic 32-9 emits the usage line; this engine prices a line handed to
  it.
- **NO per-tenant margin rows.** Margins are platform-owned global config (global/plan/provider
  scope). Per-tenant customization of markup is intentionally not a feature.
- **NO TypeScript-side pricing.** Pricing lives in the C# control plane (`apps/tamma-elsa`);
  `packages/*` providers/dashboards consume it over HTTP.

## Current-state findings (verified 2026-06-17, repo @ main)

| Site | What exists today |
|---|---|
| `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderPricingService.cs` + `IProviderPricingService.cs` | Frozen-table cost basis. `Compute(provider, model, in, out)` returns USD; **unknown `(provider, model)` returns `0m`** (silent zero). `IsKnown(provider, model)` is the gate this story must use to avoid inheriting the silent zero. Registered `TryAddSingleton` in `Tamma.Api/Extensions/ProviderSessionServiceCollectionExtensions.cs:27`. |
| `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs` | Per-call diagnostic row: `InputTokens`, `OutputTokens`, `TokensUsed`, `Cost`, `ProviderKey`, `Model`, `TenantId`, `CorrelationId`. Story 34-3 adds `BillingMode` (byok|platform) here (or as a DCB tag) — the engine reads it via `UsageLine.PricingMode`. |
| `apps/tamma-elsa/src/Tamma.Api/Services/Providers/HttpProviderClient.cs:202,248` | Already calls `_pricing.Compute(...)` to set `ProviderDiagnostic.Cost`. Confirms the cost-basis seam and the split input/output token model. |
| `apps/tamma-elsa/src/Tamma.Data/Entities/Plan.cs` + `Services/.../PlansSeeder.cs` (Story 34-1) | Plan catalog; 34-1 adds `PlanPrice` with `PricingMode platform_provided|byok` and `IPlanCatalogService`/`PlanSnapshot`. Used to resolve the caller's plan slug for the estimate. |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | CP DbSets (Plans:76, ProviderDiagnostics:191, DomainEvents:199, …). `OnModelCreating` is where entity config + CHECK constraints + indexes go. Add `DbSet<MarginPolicy>` here. |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/` | EF migrations live here. `margin_policies` is **additive** — normal `dotnet ef migrations add`, then `has-pending-model-changes` -> none. |
| `apps/tamma-elsa/src/Tamma.Api/Services/Alerts/PostgresAlertSink.cs:133,180` | Canonical DCB emit pattern: `await _events.AppendAsync(new DomainEvent { Type=..., TenantId=..., Tags=JsonSerializer.Serialize(...), Metadata="""{"eventSource":"system"}""", Data=... })`, wrapped in try/catch + log-on-failure. Copy this for `PRICING.MARGIN.UPDATED`. `IEventRepository` is scoped (`AlertServiceCollectionExtensions.cs:66`). |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs` | Static-handler endpoint style for `PlatformOwnerAccess`/`OwnerAccess` admin reads. Model `AdminPricingEndpoints` on this. |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/ConventionStoreEndpoints.cs` + `Program.cs:1769` | `var adminConventions = app.MapGroup("/api/admin/conventions").RequireAuthorization("PlatformOwnerAccess")` — exact pattern for the new `/api/admin/pricing` group. |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs:1512` | `var orgs = app.MapGroup("/api/v1/orgs").RequireAuthorization("MemberAccess")` — tenant-scoped group pattern for the estimate endpoint (`/api/pricing`). |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs:2030` | `await Tamma.Data.Seeders.PlansSeeder.SeedAsync(dbContext);` inside startup seed scope — register `MarginPolicySeeder.SeedAsync` right beside it. |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/PromptEndpoints.cs:36` | `if (modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid tenantId)` — the per-mode + `ITenantContext.TenantId` pattern the estimate endpoint uses. `ITammaModeProvider` is in `Services/PromptStore/TammaMode.cs`. |
| Auth policies in `Program.cs` | `PlatformOwnerAccess` (986), `MemberAccess` (991), `OwnerAccess` (971) — all already registered; reuse, don't add. |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Providers/ProviderChainResolverTests.cs` | Test conventions: NUnit (`[TestFixture]`/`[Test]`/`[SetUp]`), FluentAssertions, Moq. Mirror for the new `tests/Tamma.Api.Tests/Pricing/` suite. |

**Confirmed absent (this story creates):** `MarginPolicy`, `IUsagePricingEngine`/`UsagePricingEngine`,
`IMarginPolicyResolver`, `PRICING.*` events, `AdminPricingEndpoints`,
`Tamma.Api/Services/Pricing/` (34-1 creates the dir), and `PricingEndpoints` (34-3 creates the file;
this story adds `GetEstimate`).

**Key gotcha:** `ProviderPricingService.Compute` returns `0m` for unknown pairs — the engine MUST
call `IsKnown` first and throw `PRICING.UNKNOWN_MODEL`, otherwise an unpriced model silently bills
at zero (the exact misconfig the AC forbids).

---

## Phased task breakdown (test-first / TDD)

### Phase 0 — Prereq gate (no code)

- [ ] Confirm Stories 34-1 and 34-3 are merged (or stub their seams): `IPlanCatalogService`/
      `PlanSnapshot`, the shared `PricingMode` concept, and `ProviderDiagnostic.BillingMode`. If 34-3
      isn't done, the engine + admin API + golden tests can still land; the estimate endpoint's mode
      lookup degrades to "platform-provided" until 34-3 wires `TenantProviderBilling`.

### Phase 1 — `MarginPolicy` entity, migration, seeder (control plane)

- **Files:** `Tamma.Data/Entities/MarginPolicy.cs` (new), `Tamma.Data/ControlPlaneDbContext.cs`
  (DbSet + `OnModelCreating` config), `Tamma.Data/Migrations/ControlPlane/<ts>_AddMarginPolicy.cs`
  (new), `Tamma.Data/Seeders/MarginPolicySeeder.cs` (new),
  `Tamma.Core/Enums/PricingMode.cs` (new, unless 34-1 ships a shared enum to reuse).
- **Tests first:** `tests/Tamma.Api.Tests/Pricing/MarginPolicySeederTests.cs` — fresh DB gets the
  global `1.3x` row with a deterministic UUIDv7; second run is a no-op; an admin-edited multiplier
  is NOT reverted (insert-missing-only). Schema test: all-null knobs rejected by
  `ck_margin_has_knob`; two `active` rows for the same `(scope, refKey)` rejected by the partial
  unique index.
- **Approach:** entity + EF config per the story's model-config block (3 CHECK constraints +
  filtered unique index with `AreNullsDistinct(false)`). `MarginPolicySeeder.SeedAsync` mirrors
  `PlansSeeder` (deterministic ids, `AnyAsync` guard before insert). Run `dotnet ef migrations add
  AddMarginPolicy -c ControlPlaneDbContext`; verify `has-pending-model-changes` -> none.

### Phase 2 — Pure engine (`IUsagePricingEngine`) + records

- **Files:** `Tamma.Api/Services/Pricing/UsageLine.cs`, `PricedUsage.cs`, `IUsagePricingEngine.cs`,
  `UsagePricingEngine.cs`, `PricingEventTypes.cs` (constants).
- **Tests first:** `tests/Tamma.Api.Tests/Pricing/UsagePricingEngineTests.cs` (mock
  `IProviderPricingService`): multiplier-only, fixed-per-1M-only, combined; BYOK -> non-zero
  `CostBasisUsd`, `SellPriceUsd==0`, `MarginUsd==0`; unknown model -> `PRICING.UNKNOWN_MODEL` + WARN;
  rounding determinism (6dp even, 2dp invoice projection); plus the **golden-file** test
  `PriceUsage_GoldenScenarios_AreByteStable` comparing serialized `PricedUsage` to
  `Pricing/golden/pricing-scenarios.json`.
- **Approach:** implement exactly the arithmetic in the story (gate on `IsKnown` -> throw on miss;
  `Round6` at every boundary; BYOK short-circuit). Pure, no I/O, no DB. The golden JSON is generated
  once from the agreed scenarios and committed.

### Phase 3 — Margin policy resolver (impure, DB-backed)

- **Files:** `Tamma.Api/Services/Pricing/IMarginPolicyResolver.cs`, `MarginPolicyResolver.cs`.
- **Tests first:** `MarginPolicyResolverTests.cs` (Sqlite/in-memory `ControlPlaneDbContext` or
  mocked DbSet): resolution order provider > plan > global; timestamp-effective selection (event at
  `t` resolves the row whose `EffectiveFrom <= t`, newest-first, not the latest overall); no policy
  at any scope -> `TammaError("PRICING.MARGIN.NO_POLICY")`.
- **Approach:** query `MarginPolicies` per scope in priority order, filtering `EffectiveFrom <=
  atTimestamp`, ordered `EffectiveFrom desc`, take first; fall through scopes; throw on total miss.
  Scoped service (`ControlPlaneDbContext` is scoped).

### Phase 4 — Admin API (view/version margins, `PlatformOwnerAccess`)

- **Files:** `Tamma.Api/Endpoints/Admin/AdminPricingEndpoints.cs` (new), `Program.cs` (map new
  `/api/admin/pricing` group + DI in `PricingServiceCollectionExtensions.cs`).
- **Tests first:** `AdminPricingEndpointsTests.cs`: `PUT` supersedes prior active + inserts new
  active + emits exactly one `PRICING.MARGIN.UPDATED` (assert via captured `IEventRepository`); `GET`
  returns active + history; non-platform-owner -> 403.
- **Approach:** static handlers like `AdminAnalyticsEndpoints`; `PUT` runs supersede+insert in a
  transaction, then `IEventRepository.AppendAsync` (try/catch+log per `PostgresAlertSink`). Map under
  `app.MapGroup("/api/admin/pricing").RequireAuthorization("PlatformOwnerAccess")`. Wire DI:
  `TryAddSingleton<IUsagePricingEngine,…>`, `TryAddScoped<IMarginPolicyResolver,…>`; call
  `MarginPolicySeeder.SeedAsync` beside `PlansSeeder.SeedAsync` (~Program.cs:2030).

### Phase 5 — Tenant estimate endpoint (`MemberAccess`)

- **Files:** `Tamma.Api/Endpoints/PricingEndpoints.cs` (add `GetEstimate`; file from 34-3),
  `Program.cs` (map `/api/pricing` group).
- **Tests first:** `PricingEstimateEndpointTests.cs`: platform-provided tenant -> marked-up
  estimate; BYOK tenant -> `sellPriceUsd==0` token component; unknown model -> 4xx
  `PRICING.UNKNOWN_MODEL`; **tenant-isolation** — tenant A's estimate uses only A's plan/mode; any
  `/api/admin/pricing/*` call by a tenant role -> 403.
- **Approach:** `GetEstimate` reads `ITenantContext.TenantId` + `ITammaModeProvider` (single-user ->
  instance plan), resolves plan slug via `IPlanCatalogService` (34-1), resolves `(tenant, provider)`
  mode via the 34-3 seam, calls `IMarginPolicyResolver.ResolveAsync` then
  `IUsagePricingEngine.PriceUsage`, returns `PricedUsage`. Map under
  `app.MapGroup("/api/pricing").RequireAuthorization("MemberAccess")`.

### Phase 6 — Wire-up, quality gates, docs

- [ ] `dotnet build` clean; `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests"` green.
- [ ] `dotnet ef migrations has-pending-model-changes -c ControlPlaneDbContext` -> none; migration
      applies + rolls back cleanly.
- [ ] Logging per the story's Logging Requirements (INFO/DEBUG/WARN/ERROR, no secrets).
- [ ] Update epic/consumer notes so Epic 35/36-7/32-9 reference `IUsagePricingEngine` rather than
      re-implementing markup (boundary note).

## Sequencing & dependencies

Phase 0 (prereq gate) -> Phase 1 (entity/migration/seeder) -> Phase 2 (pure engine) -> Phase 3
(resolver) -> Phase 4 (admin API) -> Phase 5 (estimate) -> Phase 6 (gates). Phases 2 and 3 are
parallel-safe after Phase 1. Phase 4 needs 1+3; Phase 5 needs 1+2+3 plus the 34-1/34-3 seams.
Hard prerequisites: Story 34-1 (`IPlanCatalogService`, shared `PricingMode`) and Story 34-3
(`ProviderDiagnostic.BillingMode` / `TenantProviderBilling`). The pure engine + admin API can land
ahead of 34-3 with the mode defaulting to platform-provided.

## Risks + mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| `ProviderPricingService.Compute` silent-zero leaks into sell price | High | Gate on `IsKnown`, throw `PRICING.UNKNOWN_MODEL`; explicit unit test |
| Rounding/float drift breaks reproducibility | Medium | `decimal` only, even-rounding at fixed 6dp boundaries, golden-file regression test |
| In-place margin edit re-prices historical usage | High | Versioned supersede + `EffectiveFrom`-windowed resolution; timestamp-effective test |
| Consumer epics (35/36-7/32-9) duplicate markup math | Medium | Canonical `IUsagePricingEngine`; boundary note; expose engine, not raw math |
| Cross-tenant leak in estimate | High | Resolve plan/mode strictly from `ITenantContext`/`ITammaModeProvider`; tenant-isolation test; admin routes 403 for tenant roles |
| 34-3 `BillingMode` not yet present | Medium | `UsageLine.PricingMode` is an explicit input; estimate defaults to platform-provided until 34-3 lands; engine unchanged |
| Migration discipline (additive table) | Low | Normal `ef migrations add`; verify `has-pending-model-changes` none; config only in `OnModelCreating` |

## Acceptance criteria (mirror of the story)

- [ ] `MarginPolicy` entity + `margin_policies` table (scope/refKey/markupMultiplier/fixedUsdPer1M/
      effectiveFrom/status), CHECK `ck_margin_has_knob` (>=1 knob non-null), CHECK on scope/status,
      partial unique index (one active per `(scope, refKey)`, NULLS NOT DISTINCT). `has-pending-
      model-changes` -> none.
- [ ] `MarginPolicySeeder` seeds global `1.3x` (deterministic id, insert-missing-only, never reverts
      admin edits).
- [ ] `IMarginPolicyResolver` resolves provider-override -> plan -> global -> `PRICING.MARGIN.NO_POLICY`
      (never silent zero), honoring `EffectiveFrom <= timestamp`.
- [ ] `IUsagePricingEngine.PriceUsage` returns `{ CostBasisUsd, MarginUsd, SellPriceUsd, PricingMode }`;
      platform-provided -> cost × margin (+ fixed/1M); BYOK -> cost basis computed but token sell
      price = 0.
- [ ] Engine reads input/output tokens + provider + model + mode from the `UsageLine`
      (ProviderDiagnostic / DCB usage event); per-call basis exact (input/output at different rates).
- [ ] Reproducible / byte-stable (6dp internal even-rounding, 2dp invoice) — golden-file test.
- [ ] Unknown `(provider, model)` -> `PRICING.UNKNOWN_MODEL` (not silent zero), WARN logged.
- [ ] `GET/PUT /api/admin/pricing/margins` (`PlatformOwnerAccess`); `PUT` versions + emits
      `PRICING.MARGIN.UPDATED`.
- [ ] `GET /api/pricing/estimate` (tenant `MemberAccess`) returns a priced estimate under the
      tenant's plan + mode.
- [ ] Unit tests: platform markup math, BYOK zero-markup, resolution order, timestamp-effective
      selection, unknown-model error, no-policy error, rounding determinism.
- [ ] Tenant-isolation test: estimate uses only the caller's plan/mode; tenant roles 403 on
      `/api/admin/pricing/*`.
- [ ] Full `Tamma.Api.Tests` suite green; build clean; migration applies + rolls back.
