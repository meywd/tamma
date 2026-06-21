# Story 34-11 — Provider Cost Price-Book (`Provider` + `ProviderModelPrice` behind `IProviderPricingService`)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Date:** 2026-06-21
**Sequence:** Epic-34 pivot step **A** — implement BEFORE 34-5 (its cost-basis input).

**Goal:** Promote the hard-coded provider COST rate-sheet (`FrozenDictionary` in
`ProviderPricingService`) to a DB-backed, admin-editable, immutable-versioned control-plane entity
model (`Provider` + `ProviderModelPrice`) **behind the UNCHANGED `IProviderPricingService` seam**.
Swap the frozen impl for `DbProviderPricingService`; preserve the alias map / loose-prefix / default
rules verbatim; add `EffectiveFrom`-windowed resolution so a usage event prices under the cost rate
active at its `OccurredAt`. Downstream (34-5, 36-2, 36-7, 32-9) needs at most a one-line DI edit.

**Story file:** `docs/stories/epic-34/story-34-11/34-11-provider-cost-price-book.md`
**Design of record:** `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§3, §4)

**Tech stack:** .NET 9 / EF Core in `apps/tamma-elsa`. Entities + migrations + seeder in `Tamma.Data`;
services + endpoints in `Tamma.Api`. Tests in `apps/tamma-elsa/tests/Tamma.Api.Tests/` (NUnit +
FluentAssertions). Docker-bound suites run via `sg docker -c "dotnet test ..."` (session docker group
is stale; plain `dotnet build` / `dotnet ef` need no wrapper). **There is no TypeScript path — all C#.**

---

## Non-goals (YAGNI guard)

- **NO change to the `IProviderPricingService` interface.** `Compute(provider, model?, in, out)` +
  `IsKnown(provider, model?)` stay byte-identical. The only addition is an `EffectiveFrom`-aware
  `ComputeAt` on the concrete impl / a sibling resolver — additive, not a contract change.
- **NO markup / sell price / margin.** That is 34-5 (`MarginPolicy`, `IUsagePricingEngine`). This
  story produces ONLY the cost basis. `MarginPolicy.Scope='provider'` overrides margin, never cost.
- **NO subscription/seat price.** That is 34-1 (`PlanPrice`). `ProviderModelPrice` ≠ `PlanPrice`.
- **NO per-tenant / per-user cost rows.** Cost is the provider's published rate — global in both
  modes (design §4.4). No `TenantId`/`UserId` column on either entity.
- **NO tenant-schema tables.** Both entities are CONTROL-PLANE (`ControlPlaneDbContext`), so they go
  in the `Program.cs` DROP list + the strict CP model test — NOT the per-tenant `EfTenantDbMigrator`.
- **NO consumer rewiring.** 34-5 / 32-9 / 36-2 keep their `IProviderPricingService` dependency; the
  swap is a registration change in `Program.cs` only.

---

## Current-state findings (verified 2026-06-21, in the `epic32-specs` worktree)

| Seam | Where it is today | How 34-11 uses it |
|---|---|---|
| **Cost rate sheet** | `Tamma.Api/Services/Providers/ProviderPricingService.cs` — `FrozenDictionary<provider, FrozenDictionary<model, Rate(InputPerToken, OutputPerToken)>>` ported from `packages/cost-monitor` @ `9e9a57c~1`; `s_aliases` map; `TryGetRate` (canonicalize → model-map → null/"default"→first → exact → loose-prefix); unknown→`0m`. | **Port verbatim** as v1 seed rows; **extract** the alias+lookup helper for sharing; **retain** the frozen class as seed source / boot fallback. |
| **Seam** | `Tamma.Api/Services/Providers/IProviderPricingService.cs` — `Compute` / `IsKnown`. | **Unchanged.** New `DbProviderPricingService` implements it over rows. |
| **CP entity + versioning pattern** | `Tamma.Data/Entities/Plan.cs` + `Tamma.Data/Seeders/PlansSeeder.cs` (34-1); `MarginPolicy` + `MarginPolicySeeder` (34-5): UUIDv7, `Status` flip on edit, partial unique `WHERE active`, insert-missing-only. | **Mirror exactly** for `Provider`/`ProviderModelPrice`. |
| **CP context** | `Tamma.Data/ControlPlaneDbContext.cs` — `OnModelCreating` with `Configure*` methods (`ConfigurePlans`, `ConfigureAlertRules`). | Add `ConfigureProviders` / `ConfigureProviderModelPrices` + 2 DbSets. |
| **DROP list** | `Tamma.Api/Program.cs` ~line 2110 — `DROP TABLE IF EXISTS ... plan_features, plan_entitlements, plan_prices, plans, ... CASCADE`. | **Append** `providers, provider_model_prices`. |
| **Strict CP model test** | `tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs` — `Model_Has_ExpectedControlPlaneEntities` strict `BeEquivalentTo`. | **Add** `providers` + `provider_model_prices` to the list. |
| **Seed call site** | `Program.cs` ~line 2154 — `await PlansSeeder.SeedAsync(dbContext);` then `AgentEntitySeeder.SeedAsync`. | Add `await ProviderPricingSeeder.SeedAsync(dbContext);`. |
| **RBAC** | `PlatformOwnerAccess` policy (NOT `OwnerAccess`) for platform-global admin routes. | Gate `/api/admin/providers*`. |
| **DCB** | `IEventRepository.AppendAsync(DomainEvent)` (control-plane store for admin events). | Emit `PROVIDER.PRICE.VERSIONED` / `PROVIDER.REGISTERED` / `PROVIDER.STATUS_CHANGED`. |

**Key insight:** the genuinely new code is two entities, the EF config + migration, a verbatim-porting
seeder, a `DbProviderPricingService` that reuses an EXTRACTED copy of the existing lookup algorithm,
and admin CRUD. The risk surface is the two list-extensions (DROP + model test) and behaviour parity.

---

## Architecture

```
                  IProviderPricingService  (UNCHANGED seam: Compute / IsKnown)
                          ▲
          ┌───────────────┴────────────────┐
   ProviderPricingService            DbProviderPricingService   ← registered impl (DI swap)
   (frozen — SEED SOURCE + fallback)  reads providers/provider_model_prices
          │  shared static helper: alias map + TryGetRate (canonicalize→default→exact→prefix)
          ▼                                   │  + ComputeAt(atTimestamp)  → EffectiveFrom window
   ProviderPricingSeeder  ──(insert-missing-only, UUIDv7, v1 rows)──►  ControlPlaneDbContext
                                                                          providers
                                                                          provider_model_prices
   AdminProviderPricingEndpoints (/api/admin/providers*, PlatformOwnerAccess)
       PUT prices → supersede active + insert new active → PROVIDER.PRICE.VERSIONED (IEventRepository)
```

COST only. Sell price (34-1 `PlanPrice`) and markup (34-5 `MarginPolicy`) sit ABOVE this; 36-7 reads
the persisted `CostUsd` written from this basis. No tenant scoping — global in both modes.

---

## Task breakdown

Order: **T1** (entities + EF config) → **T2** (migration + DROP list + model test) → **T3** (extract
shared lookup helper) → **T4** (`DbProviderPricingService` + `ComputeAt` + parity) → **T5** (seeder) →
**T6** (admin endpoints + events + RBAC) → **T7** (DI swap + Program.cs wiring). T1→T2 are sequential
(migration follows the model). T3 is independent and can land before T4. Implemented SEQUENTIALLY —
one EF migration snapshot; this plan **amends/extends the existing CP migration chain, it does not
branch the snapshot**.

### T1 — Entities + EF model config

**Scope:** `Provider` + `ProviderModelPrice` (`Tamma.Data.Entities`); `ConfigureProviders` /
`ConfigureProviderModelPrices` + 2 DbSets in `ControlPlaneDbContext`. Unique `Key`; CHECKs on
`AuthModel`/`Status`/`Source`; partial unique `UX_provider_model_prices_OneActivePerModel`
(`WHERE "Status" = 'active'`); window index `(ProviderKey, Model, EffectiveFrom)`; FK
`ProviderKey → providers.Key` `OnDelete(Restrict)`.

**Tests (first):** extend `ControlPlaneDbContextModelTests` — `Provider` has unique `Key` index + the
two CHECKs; `ProviderModelPrice` has the partial unique index with filter `"Status" = 'active'` and
the window index; **neither entity has a `TenantId`/`UserId` property** (AC3); the FK is `Restrict`.

**Acceptance:**
- [ ] Both entities map; indexes/CHECKs/FK present on the design-time model.
- [ ] No `TenantId`/`UserId` on either entity (global-by-design assertion passes).
- [ ] Builds clean; no analyzer warnings.

### T2 — Migration + DROP list + strict model-test list (the two gotchas)

**Scope:** `dotnet ef migrations add ProviderCostPriceBook -c ControlPlaneDbContext`
(`Tamma.Data/Migrations/ControlPlane/`) — additive `CreateTable` for both. Append
`providers, provider_model_prices` to the `Program.cs` DROP list (~line 2110). Add both table names
to `ControlPlaneDbContextModelTests.Model_Has_ExpectedControlPlaneEntities`'s `BeEquivalentTo`.

**Tests (first):** `Model_Has_ExpectedControlPlaneEntities` now lists the two tables (RED → GREEN). A
boot-twice host test (or reuse the existing harness) asserts the second boot does NOT throw
`relation "providers" already exists`.

**Acceptance:**
- [ ] `dotnet ef migrations has-pending-model-changes -c ControlPlaneDbContext` reports none.
- [ ] DROP list contains both tables; second test-host boot succeeds.
- [ ] Strict CP model test passes with the two tables added.

### T3 — Extract the shared alias + lookup helper (pure refactor, no behaviour change)

**Scope:** Move `ProviderPricingService.s_aliases` + the `TryGetRate` algorithm (canonicalize →
model-map → `null`/`"default"`→first → exact → loose-prefix) into a shared static helper
(`ProviderRateLookup`) consumed by BOTH the frozen `ProviderPricingService` (unchanged outputs) and
the new `DbProviderPricingService`. The frozen class delegates to the helper over its
`FrozenDictionary`; the DB class delegates over a row snapshot.

**Tests (first):** the **existing** `ProviderPricingService` tests must pass unchanged (regression net
proving the extraction is a pure move). Add a small unit test of the helper directly (alias map,
prefix, default, unknown).

**Acceptance:**
- [ ] Existing frozen-service tests green, unedited.
- [ ] One copy of the alias map + lookup algorithm (grep confirms no fork).

### T4 — `DbProviderPricingService` + `ComputeAt` + parity

**Scope:** `DbProviderPricingService : IProviderPricingService` over `provider_model_prices` (active
rows), using `ProviderRateLookup`. Holds a short-lived snapshot cache invalidated on admin write. Add
`ComputeAt(provider, model?, in, out, atTimestamp)` — selects the row where `EffectiveFrom <=
atTimestamp` and is the most-recent for `(ProviderKey, Model)` (active or superseded). No-timestamp
`Compute`/`IsKnown` resolve against the current `active` row.

**Tests (first):** `DbProviderPricingServiceTests` — alias/prefix/default/unknown→`0m`+`IsKnown=false`;
`ComputeAt` windowed selection (event at `t` prices under the row effective at `t`, not latest).
`ProviderPricingParityTests` — **for every seeded `(provider, model)`, `DbProviderPricingService.Compute`
== frozen `ProviderPricingService.Compute` byte-for-byte** (depends on T5's seed; write the test, seed
in-test, assert parity).

**Acceptance:**
- [ ] DB impl reproduces the frozen behaviour for every quirk.
- [ ] `ComputeAt` selects the time-correct row.
- [ ] Parity test green for 100% of seeded pairs (incl. sub-cent rates).

### T5 — `ProviderPricingSeeder` (verbatim port, insert-missing-only)

**Scope:** `ProviderPricingSeeder.SeedAsync(ControlPlaneDbContext)` — for each provider in the frozen
`s_pricing`, upsert-if-missing a `Provider` row (with `AuthModel`: `claude-code`→`cli-token`, else
`api-key`); for each `(model, Rate)`, upsert-if-missing a `ProviderModelPrice` v1 row
(`Status='active'`, `Source='seed'`, `EffectiveFrom=SEED_EPOCH`,
`InputUsdPer1M = Rate.InputPerToken * 1_000_000m`, `OutputUsdPer1M = Rate.OutputPerToken * 1_000_000m`).
Deterministic UUIDv7 per `(key)`/`(key, model)`. Never reverts a `Source='admin'` row.

**Tests (first):** `ProviderPricingSeederTests` — first run seeds all providers/models; second run is a
no-op (counts unchanged); an admin-edited (`Source='admin'`) row is NOT reverted; UUIDv7 ids are
deterministic across runs; the USD-per-token → USD-per-1M conversion round-trips to byte-identical
`Compute` (feeds T4 parity).

**Acceptance:**
- [ ] Idempotent; admin edits survive a re-seed.
- [ ] All frozen-table entries become rows; AuthModel mapping correct.

### T6 — Admin CRUD endpoints + events + RBAC

**Scope:** `AdminProviderPricingEndpoints` (`Tamma.Api/Endpoints/Admin/`), `PlatformOwnerAccess`:
`GET /api/admin/providers`, `POST /api/admin/providers`, `PATCH /api/admin/providers/{key}`,
`GET /api/admin/providers/{key}/prices`, `PUT /api/admin/providers/{key}/prices`. `PUT prices`:
normalize `ProviderKey` via the alias helper, flip current `active` → `superseded`, insert new
`active` (`Source='admin'`), emit `PROVIDER.PRICE.VERSIONED`. Mutating a `superseded` row throws
`TammaError("PROVIDER.PRICE.IMMUTABLE", ...)`. `ProviderPricingEventTypes` constants.

**Tests (first):** `AdminProviderPricingEndpointsTests` — `PlatformOwnerAccess` 403 for a non-owner on
every route; a platform owner succeeds; `PUT prices` versions (prior `superseded`, new `active`, the
partial unique index rejects a 2nd active); `PROVIDER.PRICE.VERSIONED` appended with tags
`{ providerKey, model, effectiveFrom, supersededPriceId, source='admin', actorUserId }`; immutability
violation throws `PROVIDER.PRICE.IMMUTABLE`.

**Acceptance:**
- [ ] All routes gated by `PlatformOwnerAccess`; non-owner → 403.
- [ ] Versioning produces supersede + insert; one-active invariant holds; events tagged per AC9.

### T7 — DI swap + `Program.cs` wiring

**Scope:** `ProviderPricingServiceCollectionExtensions` registers `DbProviderPricingService` as
`IProviderPricingService` (in place of the frozen impl), `IProviderCostResolver`, and the admin
endpoints. `Program.cs`: register the seeder call (`await ProviderPricingSeeder.SeedAsync(dbContext);`
~line 2154), map the admin endpoint group, and swap the `IProviderPricingService` registration. Keep
the frozen `ProviderPricingService` available as the seed source + boot fallback (if the table is empty
at boot, fall back to frozen and log a WARN — fail-LOUD, never silently price at 0).

**Tests (first):** DI smoke test (`WebApplicationFactory`) resolves `IProviderPricingService` → the DB
impl and the admin endpoints at host startup; a boot-with-empty-table test logs the fallback WARN.

**Acceptance:**
- [ ] `IProviderPricingService` resolves to `DbProviderPricingService`; consumers unchanged.
- [ ] Seeder runs at startup; endpoints mapped; DI resolves the whole chain.

---

## Story order & dependencies

**Sibling pattern source (not a hard blocker):** 34-1 (`Plan`/`PlanPrice`/`PlansSeeder`) and 34-5
(`MarginPolicy`/`MarginPolicySeeder`) — reuse their CP-entity + immutable-versioning +
insert-missing-only + `PlatformOwnerAccess` patterns. **Implement BEFORE 34-5** (34-5's
`CostBasisUsd = IProviderPricingService.Compute(...)` consumes this seam). Downstream consumers
(34-5, 32-5, 32-9, 36-2/36-7) need at most a one-line DI dependency edit — the seam preservation is
the contract.

Internal task order: T1 → T2 → (T3 ∥) → T4 → T5 → T6 → T7. SEQUENTIAL EF snapshot: this plan amends
the existing CP migration chain; do NOT run alongside another EF-touching story.

## Verification

```bash
# build (no docker wrapper needed)
dotnet build apps/tamma-elsa/Tamma.sln
# migration is additive + complete
dotnet ef migrations has-pending-model-changes -c ControlPlaneDbContext \
  -p apps/tamma-elsa/src/Tamma.Data -s apps/tamma-elsa/src/Tamma.Api
# tests (docker-bound suites need the sg wrapper; session docker group is stale)
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~Providers"
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~ControlPlaneDbContextModelTests"
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~AdminProviderPricing"
# no-fork check: exactly one alias map + lookup algorithm
grep -rn "anthropic-claude\|TryGetRate\|class ProviderRateLookup" apps/tamma-elsa/src/Tamma.Api/Services/Providers
# DROP-list extension present
grep -n "providers, provider_model_prices\|provider_model_prices" apps/tamma-elsa/src/Tamma.Api/Program.cs
```

## Risks

- **DROP-list omission (T2):** the #1 recurring CP-table gotcha — a second test-host boot fails with
  `relation "providers" already exists`. Mitigation: AC10 + boot-twice test + the explicit `grep`
  verification; add the two tables to the DROP list in the SAME commit as the migration.
- **Strict model-test omission (T2):** `Model_Has_ExpectedControlPlaneEntities` uses a strict
  `BeEquivalentTo` — adding CP entities without updating it fails the suite. Mitigation: write the
  list edit as the RED step of T2.
- **Behaviour parity drift (T3/T4):** the alias map / loose-prefix / default / unknown→0 quirks are
  load-bearing for 34-5's `IsKnown` gate. Mitigation: ONE extracted helper consumed by both impls; the
  parity test asserts byte-identical `Compute` for every seeded pair; the existing frozen-service tests
  stay unedited as the regression net for the extraction.
- **Tenant scoping leak:** cost must be global (design §4.4). Mitigation: AC3 model test asserts no
  `TenantId`/`UserId` on either entity.
- **`OwnerAccess` vs `PlatformOwnerAccess`:** `OwnerAccess` admits every personal-tenant owner.
  Mitigation: AC9 pins `PlatformOwnerAccess`; RBAC test asserts 403 for a non-platform-owner.
- **Retroactive cost mutation:** re-pricing a model must not change historical invoices. Mitigation:
  immutable-versioning (supersede + `EffectiveFrom` window) + `ComputeAt` on the metering path +
  windowed-selection test.
- **Seeder reverting admin edits:** Mitigation: insert-missing-only (mirrors `PlansSeeder`);
  idempotency test asserts a `Source='admin'` row survives a re-seed.
- **Empty-table boot:** if the DB has no rows at boot, NEVER price at 0 silently. Mitigation: fall back
  to the retained frozen table + WARN (fail-loud, consistent with `feedback_resolution_no_empty_fallback`).
