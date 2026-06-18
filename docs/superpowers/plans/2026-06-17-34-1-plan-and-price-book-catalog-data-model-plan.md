# Story 34-1: Plan & Price-Book Catalog Data Model — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan phase-by-phase. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every phase writes tests
> before implementation. C# suites that touch Postgres run via `sg docker -c "dotnet test ..."`.

**Goal:** Replace the thin `Plan.Quotas` opaque-JSON skeleton with a structured, immutable,
versioned price-book on the control plane: extended `Plan` + new `PlanFeature`, `PlanEntitlement`
(typed quota rows), and `PlanPrice` (recurring + per-seat + metered components, split by BYOK vs
platform-provided pricing mode). Plans become immutable, versioned rows — a new version supersedes
rather than mutates, so historical tenant assignments stay reproducible. Reads go through
`IPlanCatalogService → PlanSnapshot`. This is the schema foundation every other Epic 34 story
builds on.

**Story file:** `docs/stories/epic-34/story-34-1/34-1-plan-and-price-book-catalog-data-model.md`

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa engine).
Catalog entities live in `Tamma.Data`, the quota-key enum in `Tamma.Core`, the catalog service in
`Tamma.Api/Services/Pricing/` (NEW directory). Tests in `apps/tamma-elsa/tests/Tamma.Api.Tests/` and
`tests/Tamma.Core.Tests/` (xUnit; docker-bound migration/constraint suites via
`sg docker -c "dotnet test ..."`).

---

## Non-goals (YAGNI guard)

- **NO Stripe / billing / charging.** This story stores pricing data; Epic 35 charges against it.
- **NO usage metering writes.** This story only defines the `EntitlementMetricKey` keys metering
  will use later.
- **NO quota enforcement.** Limits are stored; no request is blocked on them in this story (later
  Epic 34 enforcement stories consume `PlanSnapshot.Entitlements`).
- **NO admin write/CRUD endpoints.** The version editor ships as a tested service method; wiring it
  behind admin endpoints is Story 34-2. This story adds only three read-only `GET` endpoints.
- **NO cost→price markup computation.** `PlanPrice.MeteredComponent` is stored verbatim; the markup
  engine is a separate Epic 34 story.
- **NO per-tenant plan overrides.** The catalog is platform-global. Custom enterprise plans are
  platform rows with `IsCustom = true`, assigned via the existing `Tenant.PlanId` FK.
- **NO drop of the legacy `Plan.Quotas` / `MonthlyPriceUsd` columns.** Kept for one deprecation
  window so not-yet-migrated readers compile; a follow-up story removes them.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### What exists today

| Site | State |
|---|---|
| `src/Tamma.Data/Entities/Plan.cs` | Thin skeleton: `Id`, `Slug`, `DisplayName`, `MonthlyPriceUsd`, `Quotas` (opaque JSON string), `IsActive`, `PlacementPolicy`, timestamps. No version/status. Docstring already anticipates `PLAN.UPDATED` to `platform_events`. |
| `src/Tamma.Data/Seeders/PlansSeeder.cs` | Seeds 3 rows (`free`/`team`/`enterprise`) with stable UUIDs (`FreePlanId`/`TeamPlanId`/`EnterprisePlanId` = `aaaaaaaa-…-0001/0002/0003`). Idempotency = `Plans.AnyAsync()` short-circuit (whole-table, not per-row). Quotas seeded as inline JSON strings. Wired at `src/Tamma.Api/Program.cs:2030`. |
| `src/Tamma.Data/ControlPlaneDbContext.cs:76` | `DbSet<Plan> Plans`. `OnModelCreating` (line 215) calls a series of private `Configure*` methods (`ConfigureAlertRules`, `ConfigureTenantDatabases`, etc.) — the established pattern to mirror for the new tables. No `ConfigurePlans` exists; Plan config lives in `TammaModelConfiguration`. |
| `src/Tamma.Data/TammaModelConfiguration.cs:203-296` | Epic 28 **shadow columns** on `tenants`: `Status`, **`PlanId` (Guid?)**, `EncryptedConnectionString`, `KekVersion`, etc. `PlanId` has an index (line 288) and an FK to `plans` with `OnDelete(Restrict)` (line 292). This is the seam `GetForTenantAsync` reads via `EF.Property<Guid?>(tenant, "PlanId")`. |
| `src/Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs:590-644` | `UpdateTenantPlan` (PATCH `/api/admin/tenants/{id}/plan`) sets the `PlanId` shadow column to a `Plan.Id`, keeps `Tenant.Plan` string in lockstep, emits `PLAN.UPDATED` to `platform_events` via `IPlatformEventPublisher`. Validates `plan.IsActive`. Mapped at `Program.cs:1378`. **Must keep working unchanged.** |
| `src/Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs:900-940` | `BuildAdminEvent(type, tenantId, principal, data)` helper — builds a `PlatformEvent` with `tags` (`tenantId`, `source`, actor) + enriched `data` + `Metadata = {"workflowVersion":"1.0.0","eventSource":"system"}`. Model the new `BuildPlanEvent` helper on this. |
| `src/Tamma.Api/Services/PlatformEvents/PlatformEventPublisher.cs` + `IPlatformEventBus.cs` | `IPlatformEventPublisher.AppendAndPublishAsync(PlatformEvent, ct)` durably appends to `platform_events` then publishes in-process. Singleton; resolves a scoped `IPlatformEventRepository` per call. This is the DCB-event write seam (Epic 4). |
| `src/Tamma.Data/Entities/PlatformEvent.cs` | CP-resident event row: `Id`, `Type`, `TenantId?`, `UserId?`, `Tags`/`Metadata`/`Data` (JSON strings), `CreatedAt`, `SequenceNumber` (BIGSERIAL). `AlertRuleEvaluator` polls this table. |
| `src/Tamma.Data/Migrations/ControlPlane/` | `20260609205701_InitialControlPlane.cs` (Phase-0 collapsed baseline) + snapshot. New migration lands here via `dotnet ef migrations add … --context ControlPlaneDbContext`. |
| `src/Tamma.Core/Enums/` | `AssessmentResult`, `BlockerType`, `MentorshipState`, `QualityGateResult`. Style = plain `public enum` with XML doc per member. `EntitlementMetricKey` lands here. |
| `src/Tamma.Api/Services/Pricing/` | **Does not exist.** New directory for the catalog service. |
| Auth policies (`src/Tamma.Api/Program.cs:971-1012`) | `OwnerAccess`, `PlatformOwnerAccess`, `MemberAccess`, `PromptManage` registered. Admin group `app.MapGroup("/api/admin").RequireAuthorization("AdminAccess")` (line 1244). The three read endpoints attach `OwnerAccess`. |
| Test layout | `tests/Tamma.Api.Tests/` has per-area dirs (Admin, Alerts, Epic28, Provisioning, …) + `tests/Tamma.Core.Tests/`. New: `tests/Tamma.Api.Tests/Pricing/`, `tests/Tamma.Core.Tests/Enums/`. |
| `docs/stories/epic-34/story-34-1/` | Exists, empty until this plan's story file. |

### Key constraints derived from findings

- The `Tenant.PlanId` FK to `plans(Id)` with `OnDelete(Restrict)` already exists — versioned plans
  must not break it. A deprecated version is a normal `plans` row; the FK still resolves. Never
  hard-delete a plan row that any tenant references (Restrict enforces this).
- `PlansSeeder` currently uses a whole-table `AnyAsync` short-circuit. To seed child rows
  insert-missing-only **and** backfill children on a DB that already has the 3 bare plan rows, the
  short-circuit must become per-table / per-row existence checks.
- `platform_events` is the correct store for `PLAN.VERSION.CREATED` / `PLAN.DEPRECATED` (CP-resident,
  cross-tenant, polled by the alert evaluator) — not the per-tenant `domain_events`.

---

## Architecture

**Versioned immutable catalog → typed entitlements/prices → resolved snapshot → DCB events.**

1. **`EntitlementMetricKey`** (Tamma.Core) — closed quota-key enum, persisted as snake_case text via
   a value converter. The single source of truth shared by entitlement, pricing, metering,
   enforcement.
2. **Extended `Plan` + `PlanFeature` / `PlanEntitlement` / `PlanPrice`** (Tamma.Data) — one
   immutable version row per `(Slug, Version)`; `UNIQUE(Slug, Version)` + partial unique
   `UX_plans_OneActivePerSlug WHERE Status='active'` enforce one current version per slug. Children
   FK to `plans(Id)` with `OnDelete(Restrict)`.
3. **`PlanVersionEditor`** (Tamma.Api) — the only write path. `CreateNewVersionAsync` inserts a new
   active version, flips the prior to `deprecated`, emits events. An immutability guard rejects any
   mutation of an `active`/`deprecated` row with `TammaError("PLAN.VERSION.IMMUTABLE")`.
4. **`IPlanCatalogService → PlanSnapshot`** (Tamma.Api) — all reads. `GetActiveBySlugAsync`,
   `GetByIdAsync`, `GetForTenantAsync` (via the `PlanId` shadow column), `ListActiveAsync`.
5. **DCB events** `PLAN.VERSION.CREATED` / `PLAN.DEPRECATED` to `platform_events` via
   `IPlatformEventPublisher`.
6. **Read-only API** — three `GET /api/admin/plans*` endpoints (OwnerAccess). Write endpoints are
   Story 34-2.

### Per-mode ownership (CLAUDE.md two-scoping-model rule)

| Question | single-user | SaaS |
|---|---|---|
| Who owns the catalog? | Platform (global rows); sole user reads it. | Platform owner (`OwnerAccess`); tenants read-only snapshots. |
| Who creates/deprecates a version? | Sole user (same code path, no extra RBAC gate). | `OwnerAccess` only. |
| Per-tenant override? | None. | None — custom plans are platform rows `IsCustom=true`. |
| Reads consult mode? | No — catalog data is identical in both modes. | No. |

---

## Phased task breakdown (test-first)

### Phase 1 — `EntitlementMetricKey` enum + value converter

**Files:**
- New: `src/Tamma.Core/Enums/EntitlementMetricKey.cs` — enum with 7 members
  (`Agents`, `WorkflowRuns`, `LlmTokens`, `Seats`, `Repos`, `RagStorageMb`,
  `BenchmarkRetentionDays`).
- New: `src/Tamma.Core/Enums/EntitlementMetricKeyExtensions.cs` — `ToMetricString()` and
  `Parse(string)` with the snake_case map; `Parse` throws `TammaError("PLAN.METRIC_KEY.UNKNOWN")`
  on an unmapped string.

**Tests first:** `tests/Tamma.Core.Tests/Enums/EntitlementMetricKeyTests.cs` —
every member round-trips; `LlmTokens ↔ "llm_tokens"`, `RagStorageMb ↔ "rag_storage_mb"`,
`BenchmarkRetentionDays ↔ "benchmark_retention_days"` pinned; unknown string throws; the snake_case
set has no duplicates and exactly 7 entries (guards against adding an enum member without a mapping).

**Approach:** Plain enum (ordinals never persisted). Extensions own the string contract. No DB in
this phase — fast unit suite.

### Phase 2 — Entities + EF model config

**Files:**
- New: `src/Tamma.Data/Entities/PlanFeature.cs`, `PlanEntitlement.cs`, `PlanPrice.cs`.
- Modify: `src/Tamma.Data/Entities/Plan.cs` — add `Version`, `Status`, `IsCustom`,
  `BillingInterval`, `SupersedesPlanId`, and nav collections `Features`/`Entitlements`/`Prices`.
  Keep `Quotas`, `MonthlyPriceUsd`, `IsActive`, `PlacementPolicy`.
- Modify: `src/Tamma.Data/ControlPlaneDbContext.cs` — add `DbSet<PlanFeature>`,
  `DbSet<PlanEntitlement>`, `DbSet<PlanPrice>`; add `ConfigurePlans`, `ConfigurePlanFeatures`,
  `ConfigurePlanEntitlements`, `ConfigurePlanPrices` and call them from `OnModelCreating` (mirror
  `ConfigureAlertRules` / `ConfigureTenantDatabases` exactly).

**Model config details:**
- `plans`: CHECK `ck_plans_status` (`active`/`deprecated`/`draft`), `ck_plans_billing_interval`
  (`monthly`/`annual`); `UX_plans_Slug_Version` unique; `UX_plans_OneActivePerSlug` partial unique
  `WHERE "Status" = 'active'`; self-FK `SupersedesPlanId → plans(Id)` `OnDelete(Restrict)`.
- `plan_entitlements`: `MetricKey` via `HasConversion(k => k.ToMetricString(), s => Parse(s))`
  stored as `text`; CHECK `ck_plan_entitlements_period` + `ck_plan_entitlements_overage`;
  `UX_plan_entitlements_PlanId_MetricKey` unique; FK `PlanId → plans(Id)` `OnDelete(Restrict)`.
- `plan_features`: `UX_plan_features_PlanId_FeatureKey` unique; FK Restrict.
- `plan_prices`: CHECK `ck_plan_prices_mode`; `RecurringUsd`/`SeatUsd` `HasPrecision(20,4)`;
  `MeteredComponent` jsonb default `'{}'::jsonb`; `UX_plan_prices_PlanId_PricingMode` unique; FK
  Restrict.

**Tests first:** these are validated by the migration + constraint suite in Phase 4 (constraints
are DB-level). Add a focused `ControlPlaneDbContext` model-shape unit test asserting the three
DbSets resolve and `MetricKey` has the value converter configured (model metadata assertion, no DB).

**Approach:** Configuration lives in `ControlPlaneDbContext.OnModelCreating` only (the established
single source for CP entity config). Do not split across files.

### Phase 3 — Snapshot, catalog service, version editor, events, DI

**Files:**
- New: `src/Tamma.Api/Services/Pricing/PlanSnapshot.cs` — immutable `record` + `PlanFeatureView` /
  `PlanEntitlementView` / `PlanPriceView` projections.
- New: `src/Tamma.Api/Services/Pricing/IPlanCatalogService.cs` + `PlanCatalogService.cs` —
  `GetActiveBySlugAsync`, `GetByIdAsync`, `GetForTenantAsync` (reads `EF.Property<Guid?>(tenant,
  "PlanId")`), `ListActiveAsync`. Eager-loads `Include(p => p.Features/Entitlements/Prices)`.
- New: `src/Tamma.Api/Services/Pricing/PlanCatalogEventTypes.cs` — `PLAN.VERSION.CREATED`,
  `PLAN.DEPRECATED`.
- New: `src/Tamma.Api/Services/Pricing/PlanVersionEditor.cs` — `CreateNewVersionAsync(slug,
  draftSpec, principal, ct)`: in one transaction, insert new active version (`Version+1`,
  `SupersedesPlanId`), flip prior to `deprecated`, emit both events via `IPlatformEventPublisher`
  (`BuildPlanEvent` helper modelled on `BuildAdminEvent`). Immutability guard throws
  `TammaError("PLAN.VERSION.IMMUTABLE", severity High)` on any attempt to mutate an
  `active`/`deprecated` row (implement as an explicit pre-write check in the editor; optionally a
  `SavingChanges` interceptor for defence-in-depth).
- New: `src/Tamma.Api/Extensions/PricingServiceCollectionExtensions.cs` — `AddPlanCatalog(services)`
  registers `IPlanCatalogService` + `PlanVersionEditor` (scoped).
- Modify: `src/Tamma.Api/Program.cs` — call `AddPlanCatalog`; map three read endpoints.

**Tests first:**
- `tests/Tamma.Api.Tests/Pricing/PlanCatalogServiceTests.cs` — snapshot assembly; active-only by
  slug; frozen deprecated version by id; `GetForTenantAsync` resolves via shadow column; missing →
  null.
- `tests/Tamma.Api.Tests/Pricing/PlanVersionEditorTests.cs` — v1→v2 supersede link, prior flipped
  to deprecated, both events emitted with correct tags (fake `IPlatformEventPublisher`); 3-version
  chain.
- `tests/Tamma.Api.Tests/Pricing/PlanImmutabilityTests.cs` — mutate active → throws; mutate
  deprecated → throws; mutate draft → ok.

**Approach:** Service is read-only and never throws on missing (returns null); editor owns all
writes + events. Use an in-memory/Sqlite-backed `ControlPlaneDbContext` for service/editor unit
tests where the partial-unique/CHECK behaviour is not under test; the DB-level invariants are
covered in Phase 4.

### Phase 4 — Seeder + migration + DB-level invariants

**Files:**
- Modify: `src/Tamma.Data/Seeders/PlansSeeder.cs` — keep the 3 plan UUIDs; set `Version=1`,
  `Status="active"` on each; seed structured `PlanFeature`/`PlanEntitlement`/`PlanPrice` rows
  (deterministic UUIDs) **insert-missing-only** per row (replace the whole-table `AnyAsync`
  short-circuit with per-row `AnyAsync(predicate)` checks so children backfill and admin edits are
  never reverted).
- New: `src/Tamma.Data/Migrations/ControlPlane/<timestamp>_PlanPriceBookCatalog.cs` — generate via
  `dotnet ef migrations add PlanPriceBookCatalog --context ControlPlaneDbContext`. Additive: new
  `plans` columns (defaults backfill the 3 existing rows to `Version=1`/`Status='active'`), 3 new
  tables, CHECKs, FKs, partial unique index.

**Tests first:**
- `tests/Tamma.Api.Tests/Pricing/PlansSeederTests.cs` — first seed populates child rows for all 3
  slugs with known UUIDs + `Version=1`/`Status=active`; second seed is a no-op; an admin-edited
  (draft v2) row is NOT reverted by re-seed.
- `tests/Tamma.Api.Tests/Pricing/PlanCatalogMigrationTests.cs` (docker-bound) — apply migration to a
  clean DB, assert tables/columns/CHECKs/partial-unique exist; rollback clean.
- One-active-version invariant test (docker-bound) — inserting a second `Status='active'` row for an
  existing slug raises a unique-violation `DbUpdateException`.
- **Tenant-isolation test** — catalog is platform-global; `GetForTenantAsync` returns only the
  tenant's own assigned plan version and never leaks `draft` versions; two tenants on different
  versions each resolve their own frozen snapshot.

**Approach:** Run `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext`
after generating — must report none. Docker-bound suites via `sg docker -c "dotnet test ..."`.

### Phase 5 — Read endpoints + verification

**Files:**
- Modify: `src/Tamma.Api/Program.cs` — map (under the `admin` group, `OwnerAccess`):
  `GET /api/admin/plans`, `GET /api/admin/plans/{slug}`, `GET /api/admin/plans/{slug}/versions`.
- Optional new: `src/Tamma.Api/Endpoints/Admin/PlanCatalogEndpoints.cs` for the handlers (mirror
  `AdminTenantsEndpoints` static-handler style) — or inline if trivial.

**Tests first:** `tests/Tamma.Api.Tests/Pricing/PlanCatalogEndpointsTests.cs` — OwnerAccess required
(non-owner → 403); list returns active snapshots; `{slug}` returns active version; `{slug}/versions`
returns the active+deprecated chain; unknown slug → 404.

**Verification:** full C# suite green (`sg docker -c "dotnet test apps/tamma-elsa"`), build clean,
`has-pending-model-changes` clean, migration up+down clean.

---

## Sequencing & dependencies

Phase 1 (enum) → Phase 2 (entities/config) → Phase 3 (service/editor/events) →
Phase 4 (seeder/migration/DB-invariants) → Phase 5 (endpoints).

- Phase 1 is the only hard prerequisite for everything else (the enum is referenced by entities,
  service, seeder).
- Phase 2 must land before Phase 4's migration (the migration is generated from the model).
- Phase 3 and Phase 4's seeder can proceed in parallel once Phase 2 lands; the DB-invariant tests in
  Phase 4 need the migration applied.
- **External prerequisites:** Epic 28 (ControlPlaneDbContext, `Tenant.PlanId` shadow FK, seeder
  pipeline, `Migrations/ControlPlane/`) and Epic 4 (`platform_events`, `IPlatformEventPublisher`) —
  both already shipped on main.
- **This story blocks:** Story 34-2 (admin CRUD wires the editor behind endpoints), the markup-engine
  story (reads `PlanPrice.MeteredComponent`), Epic 35 Billing, and the entitlement-enforcement
  stories.

---

## Risks + mitigations

- **Breaking the existing `Tenant.PlanId` FK / `UpdateTenantPlan`.** The FK targets `plans(Id)`;
  versioned plans are still normal `plans` rows, so the FK resolves to a specific version. Mitigation:
  keep `Slug`/`DisplayName`/`IsActive` columns; do not change `UpdateTenantPlan`; add a regression
  test that asserts the existing PATCH `/api/admin/tenants/{id}/plan` flow still works against a
  versioned plan row.
- **Seeder reverting admin edits.** The current whole-table `AnyAsync` short-circuit would mask the
  need for per-row insert-missing-only. Mitigation: switch to per-row existence checks; a dedicated
  seeder test asserts an edited row survives a re-seed (mirrors convention system-defaults ownership,
  `feedback_resolution` / `project_convention_system_defaults_ownership`).
- **Two active versions slipping through under concurrency.** The application-level flip-then-insert
  could race. Mitigation: the partial unique index `UX_plans_OneActivePerSlug` is the load-bearing
  guard — the DB rejects a second active row regardless of app logic; the editor runs the flip+insert
  in one transaction and surfaces the unique-violation as a clear error. A docker-bound test inserts
  a second active row directly and asserts the violation.
- **Migration not additive / pending model changes.** Mitigation: generate with EF tooling, run
  `has-pending-model-changes` (must be none), test up+down on a clean DB. New columns carry
  backfilling defaults so the 3 existing seeded rows become `Version=1`/`Status='active'` without a
  data migration.
- **`EntitlementMetricKey` drift between layers.** A new enum member without a string mapping would
  silently break persistence. Mitigation: the enum test asserts the snake_case map has exactly one
  entry per member (count + no-duplicates check), so adding a member without a mapping fails the
  suite.
- **Event-store topology shift (Story 28-1 / Epic 30).** `PLAN.*` events are platform-scope and must
  stay CP-resident. Mitigation: emit via `IPlatformEventPublisher` (writes `platform_events`)
  explicitly, never the per-tenant `domain_events` path — same precedent as `PLAN.UPDATED`.
- **Legacy `Quotas` JSON divergence.** Keeping the JSON column alongside typed rows risks two sources
  of truth. Mitigation: the column is read-only legacy for one deprecation window; new code reads
  `Entitlements`; the seeder writes both consistently; a follow-up story drops the JSON once Epic 35
  is off it. Documented in the story's boundary notes.

---

## Acceptance criteria (mirror the story)

- [ ] `Plan` extended with `Version`, `Status` (`active`/`deprecated`/`draft`), `IsCustom`,
      `BillingInterval` (`monthly`/`annual`), `SupersedesPlanId`; legacy `Slug`/`DisplayName`/
      `IsActive`/`PlacementPolicy` + `Tenant.PlanId` FK preserved.
- [ ] `PlanFeature`, `PlanEntitlement`, `PlanPrice` entities + DbSets added to
      `ControlPlaneDbContext`; FKs `OnDelete(Restrict)` to `plans(Id)`.
- [ ] `EntitlementMetricKey` closed enum in `Tamma.Core/Enums` with 7 members, persisted as
      snake_case text, shared by entitlement/pricing/metering/enforcement.
- [ ] Plan versions immutable after activation: mutating an `active`/`deprecated` row throws
      `PLAN.VERSION.IMMUTABLE`; editing creates `Version+1` + flips prior to `deprecated`.
- [ ] `UNIQUE(Slug, Version)` + partial unique `UX_plans_OneActivePerSlug WHERE Status='active'`;
      CHECKs pin `Status`/`BillingInterval`/`Period`/`OverageMode`/`PricingMode`.
- [ ] Additive EF migration under `Migrations/ControlPlane/`; `has-pending-model-changes` reports
      none; up+down clean.
- [ ] `PlansSeeder` seeds structured feature/entitlement/price rows for free/team/enterprise with
      deterministic UUIDs, insert-missing-only, never reverting admin edits.
- [ ] `IPlanCatalogService` returns fully-resolved immutable `PlanSnapshot`
      (`GetActiveBySlugAsync`/`GetByIdAsync`/`GetForTenantAsync`/`ListActiveAsync`).
- [ ] Catalog mutations emit `PLAN.VERSION.CREATED` / `PLAN.DEPRECATED` to `platform_events` via
      `IPlatformEventPublisher` with `slug`/`version`/`planId`/`supersedesPlanId` tags.
- [ ] Three read-only `GET /api/admin/plans*` endpoints gated by `OwnerAccess`; write endpoints
      deferred to Story 34-2.
- [ ] Per-mode: catalog platform-owned in both modes; SaaS owner-only create/deprecate, tenant
      read-only snapshots; no per-tenant override layer.
- [ ] Unit + integration tests cover immutability, supersede chain, one-active-version invariant,
      seeder idempotency, snapshot resolution, event emission, and tenant-isolation; full suite
      green; build clean.
