# Story 34-2 — Plan Catalog Admin API & Custom Enterprise Plans

> **For agentic workers:** REQUIRED SUB-SKILL: use superpowers:executing-plans (or
> superpowers:subagent-driven-development) to work this plan task-by-task. Steps use checkbox
> (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests before
> implementation. C# suites that touch Postgres run via `sg docker -c "dotnet test ..."`.

**Story:** `docs/stories/epic-34/story-34-2/34-2-plan-catalog-admin-api-and-custom-enterprise-plans.md`
**Epic:** 34 — Pricing, Plans & Entitlements · **Priority:** P0 · **Est:** 3-4 days ·
**Depends on:** Story 34-1 (catalog data model) · **Blocks:** 34-4 (assignment)

## Goal

Ship the platform-owner-gated admin API over the price-book catalog (create / version / deprecate)
plus custom (per-tenant) enterprise-plan minting, and the public read API any authenticated tenant
member uses for the pricing/upgrade UI. Catalog reads resolve through `IPlanCatalogService`
(Story 34-1). Every mutation is audited via a DCB `platform_events` row carrying the actor.

## Non-goals (YAGNI guard)

- NO plan **assignment** logic. Pinning a tenant to a plan version, upgrade/downgrade/cancel,
  proration, and enforcing "a custom plan is only assignable to its bound tenant" are **Story 34-4**.
  This story records the `CustomTenantId` binding and the affected-tenant count, nothing more.
- NO BYOK-vs-platform-provided pricing-MODE resolution. This story validates the `PricingMode` enum
  value on a `PlanPrice`; resolving a tenant's mode at LLM-call time and the secret cabinet are
  **Story 34-3 / Epic 29**.
- NO reshaping of the 34-1 schema, read seam, or immutability invariants — only an additive
  `CustomTenantId` column + CHECK.
- NO Stripe / billing integration (Epic 35). No new third-party libraries.
- NO dashboard UI in this story — API only. (`packages/dashboard` admin CRUD and
  `packages/dashboard-user` pricing UI are sibling/later work.)
- NO new auth policy. `PlatformOwnerAccess` (mutations) + `MemberAccess` (reads) already exist and
  cover the exact RBAC matrix.

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### What 34-1 provides (prerequisite — assume present at implementation time)

Per `/tmp/pab_stories/34-1.json`: extended `Plan` entity (`Version`, `Status active|deprecated|draft`,
`IsCustom`, `BillingInterval`, supersede chain), new `PlanFeature` / `PlanEntitlement` / `PlanPrice`
entities, `EntitlementMetricKey` closed enum in `Tamma.Core/Enums`, `IPlanCatalogService` returning
`PlanSnapshot`, `UNIQUE(Slug, Version)` + a partial index enforcing one `active` version per slug,
and lifecycle events `PLAN.VERSION.CREATED` / `PLAN.DEPRECATED` on `platform_events`. 34-2 extends
the service and adds the endpoint layer.

### Existing entity + seed (today, pre-34-1)

- `apps/tamma-elsa/src/Tamma.Data/Entities/Plan.cs` — thin entity (`Slug`, `DisplayName`,
  `MonthlyPriceUsd`, opaque `Quotas` JSON, `IsActive`, `PlacementPolicy`). Doc-comment already
  anticipates admin mutation emitting `PLAN.UPDATED` to `platform_events`.
- `apps/tamma-elsa/src/Tamma.Data/Seeders/PlansSeeder.cs` — three deterministic-UUID seed rows
  (`free`/`team`/`enterprise`), insert-missing-only.
- `apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs:11` — `public string Plan { get; set; } = "free";`
  the legacy plan-slug string used for the transitional affected-tenant count until 34-4.

### Auth policies (`apps/tamma-elsa/src/Tamma.Api/Program.cs`)

- `PlatformOwnerAccess` (Program.cs:986) — `PlatformPermissionRequirement("platform_admin")`, keys
  off the JWT `platformRole` claim. This is the gate for ALL `/api/admin/pricing/*` routes (mutations
  + custom mint). Comment at 976-985 mandates platform-scoped admin work use this, not `OwnerAccess`.
- `MemberAccess` (Program.cs:991) — any authenticated user. Gate for the public `/api/pricing/*`
  reads. (`AuthenticatedAny` at 1082 is an alternative; `MemberAccess` matches "any tenant member".)
- Admin routes register under `var admin = app.MapGroup("/api/admin").RequireAuthorization("AdminAccess");`
  (Program.cs:1244) with a **per-route** `.RequireAuthorization("PlatformOwnerAccess")` override —
  see the `tenant-databases` block at Program.cs:1400-1414 (the canonical pattern to copy).

### Platform-event emission (the audit seam)

- `IPlatformEventPublisher.AppendAndPublishAsync(PlatformEvent, ct)` —
  `apps/tamma-elsa/src/Tamma.Data/Abstractions/IPlatformEventPublisher.cs`; impl
  `apps/tamma-elsa/src/Tamma.Api/Services/PlatformEvents/PlatformEventPublisher.cs` (singleton,
  opens its own DI scope for the repo). Appends to `platform_events` (control-plane) and fans out
  in-process.
- `PlatformEvent` entity — `apps/tamma-elsa/src/Tamma.Data/Entities/PlatformEvent.cs`
  (`Type`, `TenantId?`, `UserId?`, `Tags` JSON, `Metadata` JSON, `Data` JSON, server-assigned
  `SequenceNumber`).
- Actor-capture precedent — `AdminTenantsEndpoints.BuildAdminEvent` /
  `ExtractActor(ClaimsPrincipal)` (`apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs:900-969`):
  pulls JWT `sub`/`email`/`platformRole` into both `tags` (queryable) and `data` (immutable). Copy
  this pattern for `PLAN.CATALOG.UPDATED` / `PLAN.CUSTOM.CREATED`.

### Endpoint + DTO conventions

- Endpoints are `public static` minimal-API handler classes (e.g.
  `AdminTenantDatabasesEndpoints.cs`): handlers take EF context / services / `ClaimsPrincipal` /
  `CancellationToken` as parameters, return `IResult` (`Results.Ok/Created/NotFound/Conflict/
  Problem(...)`). Enum validation via static `HashSet<string>` allow-lists
  (`AllowedPlacementClasses`, `AllowedStatuses` at AdminTenantDatabasesEndpoints.cs:39-43).
- DTOs live under `Tamma.Api/Dtos/<Area>/`; no `Pricing` folder yet (verified — must be created).
- `Tamma.Api/Services/Pricing/` does not exist yet (34-1 creates it).
- `TammaError` — `apps/tamma-elsa/src/Tamma.Core/TammaError.cs` (`Code`, message, severity). The
  service throws stable-coded `TammaError`; endpoints translate to HTTP status.

### Test layout

- `apps/tamma-elsa/tests/Tamma.Api.Tests/` — per-area folders (Admin, Alerts, PromptStore,
  Conventions, Providers, Tenancy, …). Add a `Pricing/` folder. xUnit. Docker-bound integration
  suites run via `sg docker -c "dotnet test ..."`; pure-unit suites run plain `dotnet test`.

## Architecture (one screen)

```
GET /api/pricing/plans*            ──MemberAccess──▶ PricingEndpoints
                                                       └─▶ IPlanCatalogService.ListActivePublic / GetActivePublicBySlug
POST/PUT/DELETE /api/admin/pricing/* ─PlatformOwnerAccess─▶ AdminPricingEndpoints
   POST .../custom                                          └─▶ IPlanCatalogService.{Create,Version,CreateCustom,DeprecateVersion}
                                                                  ├─ validate EntitlementMetricKey + PricingMode  (422)
                                                                  ├─ reject custom-as-public                       (400)
                                                                  ├─ write Plan/PlanFeature/PlanEntitlement/PlanPrice (ControlPlaneDbContext)
                                                                  ├─ enforce UNIQUE(Slug,Version)                  (409)
                                                                  └─ IPlatformEventPublisher.AppendAndPublish(PLAN.CATALOG.UPDATED | PLAN.CUSTOM.CREATED)
```

All catalog state lives in the **control plane** — no `TenantDbContext` write. Custom plans are
excluded from the public list by the `IsCustom=false` filter (tenant isolation by construction).

## Phased task breakdown (test-first)

### Phase 0 — Branch + verify 34-1 prerequisites

- [ ] Branch off `main` (wave pattern, e.g. `feat/wave-c` or `feat/34-2`).
- [ ] Confirm 34-1 merged: `Plan` has `Version`/`Status`/`IsCustom`/`BillingInterval`,
      `EntitlementMetricKey` exists in `Tamma.Core/Enums`, `IPlanCatalogService`/`PlanSnapshot`
      exist, `UNIQUE(Slug, Version)` + one-active-version index present. If not merged, this story is
      blocked — stop.

### Phase 1 — `CustomTenantId` binding column (additive migration)

**Files:** `Tamma.Data/Entities/Plan.cs`, `Tamma.Data/ControlPlaneDbContext.cs` /
`TammaModelConfiguration.cs` (model config), new migration under
`Tamma.Data/Migrations/ControlPlane/`.

- [ ] **Test first:** `tests/Tamma.Api.Tests/Pricing/PlanCustomBindingTests.cs` — inserting a
      `Plan` with `IsCustom=true` and null `CustomTenantId` violates the CHECK; `IsCustom=false`
      with a non-null `CustomTenantId` violates it; the valid combinations persist.
- [ ] Add nullable `Guid? CustomTenantId` to `Plan.cs` with the binding doc-comment.
- [ ] Configure the column + CHECK constraint in `TammaModelConfiguration.cs` (single source of
      entity config — mirror existing CHECK config style).
- [ ] `dotnet ef migrations add AddCustomTenantIdToPlan -c ControlPlaneDbContext` (additive); then
      `dotnet ef migrations has-pending-model-changes` → must report none.
- [ ] Verify migration applies + rolls back cleanly on a throwaway DB.

### Phase 2 — Extend `IPlanCatalogService` with the admin write path

**Files:** `Tamma.Api/Services/Pricing/IPlanCatalogService.cs`,
`Tamma.Api/Services/Pricing/PlanCatalogService.cs`, `Tamma.Api/Dtos/Pricing/PlanDtos.cs`,
`Tamma.Api/Services/Pricing/MissingConfigEventTypes`-style `PlanCatalogEventTypes.cs` (constants:
`PLAN.CATALOG.UPDATED`, `PLAN.CUSTOM.CREATED`).

- [ ] **Test first:** `tests/Tamma.Api.Tests/Pricing/PlanCatalogServiceTests.cs` covering:
  - `CreatePlanAsync` → version-1 active row + `PLAN.CATALOG.UPDATED` (action=created) with actor id.
  - `VersionPlanAsync` → prior active flipped to deprecated, new version active, one-active invariant.
  - invalid `MetricKey` → `TammaError("PLAN.METRIC_KEY.INVALID")`; invalid `PricingMode` →
    `TammaError("PLAN.PRICING_MODE.INVALID")`.
  - `CreateCustomPlanAsync` → `IsCustom=true` + `CustomTenantId` + `PLAN.CUSTOM.CREATED` with bound
    tenantId; custom-flagged-public → `TammaError("PLAN.CUSTOM.PUBLIC_REJECTED")`.
  - `DeprecateVersionAsync`: affected>0 no-force → `DeprecateResult(false, count)`, no write;
    affected>0 force → `DeprecateResult(true, count)`; affected==0 → deprecates.
  - duplicate `(Slug, Version)` → unique violation surfaced.
  - platform-event append failure does NOT roll back the write (logged ERROR, write committed).
- [ ] Define DTOs in `PlanDtos.cs` (`CreatePlanRequest`, `VersionPlanRequest`,
      `CreateCustomPlanRequest`, `PlanEntitlementDto`, `PlanPriceDto`, `PlanFeatureDto`,
      `PlanListFilter`, response `PlanSnapshot` projection — reuse 34-1's `PlanSnapshot` if it is the
      same shape; otherwise add an admin projection). No raw EF entity is serialized.
- [ ] Add `ActorContext` + `DeprecateResult` records to the service contract.
- [ ] Implement validation helpers: `EntitlementMetricKey` enum parse (closed set) and
      `PricingMode` allow-list (`platform_provided`, `byok`) — static `HashSet`/`Enum.TryParse`,
      mirroring the AdminTenantDatabases allow-list style.
- [ ] Implement `CreatePlanAsync` / `VersionPlanAsync` / `CreateCustomPlanAsync` /
      `DeprecateVersionAsync` / `ListAllForAdminAsync` against `ControlPlaneDbContext`; emit the
      platform event via injected `IPlatformEventPublisher` (build the event with actor from
      `ActorContext`, tags + data, following `BuildAdminEvent`).
- [ ] Affected-tenant count: `ControlPlaneDbContext.Tenants.Count(t => t.Plan == slug && t.DeletedAt == null)`
      with a `// TODO(34-4): switch to version-pinned assignment table` comment.

### Phase 3 — Public read endpoints (`PricingEndpoints.cs`)

**Files:** `Tamma.Api/Endpoints/PricingEndpoints.cs`, `Program.cs` (route registration).

- [ ] **Test first:** `tests/Tamma.Api.Tests/Pricing/PricingEndpointsTests.cs`
      (`WebApplicationFactory`): `GET /api/pricing/plans` returns active+public only (excludes
      custom/deprecated/draft); `GET /api/pricing/plans/{slug}` → 200 for active public, 404 for
      custom slug, 404 for unknown slug; both modes (SingleUser/SaaS) return the catalog to any
      authenticated member.
- [ ] Implement static handlers `ListPublic` / `GetPublicBySlug` calling
      `IPlanCatalogService.ListActivePublicAsync` / `GetActivePublicBySlugAsync`.
- [ ] Register in `Program.cs`: `app.MapGet("/api/pricing/plans", ...).RequireAuthorization("MemberAccess")`
      and the `{slug}` variant (place near the other `/api/v1`/public read routes).

### Phase 4 — Admin CRUD + custom mint endpoints (`AdminPricingEndpoints.cs`)

**Files:** `Tamma.Api/Endpoints/Admin/AdminPricingEndpoints.cs`, `Program.cs`.

- [ ] **Test first:** `tests/Tamma.Api.Tests/Pricing/AdminPricingEndpointsTests.cs`:
  - member-role JWT → 403 on POST/PUT/DELETE/custom; platform-owner → 201/200/204.
  - `POST` create → 201 + snapshot; `PUT` version → 200 + new version, prior deprecated.
  - invalid metric key / pricing mode → 422 with field-level error.
  - custom flagged public → 400.
  - custom round-trip: `POST .../custom` 201 → `GET /api/admin/pricing/plans` lists it →
    `GET /api/pricing/plans` excludes it → `GET /api/pricing/plans/{slug}` 404.
  - **tenant-isolation**: custom plan bound to tenant A absent from tenant B's public catalog +
    404 on the slug for tenant B.
  - deprecate-with-assignments → 409 `{ affectedTenantCount }`; `?force=true` → 204.
  - `PLAN.CATALOG.UPDATED` + `PLAN.CUSTOM.CREATED` rows land in `platform_events` with
    `actorUserId`/`actorEmail` in tags and data.
- [ ] Implement static handlers (`ListForAdmin`, `CreatePlan`, `VersionPlan`, `CreateCustomPlan`,
      `DeprecateVersion`) taking `ClaimsPrincipal` → build `ActorContext` via an `ExtractActor`
      helper (reuse the `AdminTenantsEndpoints` pattern; consider lifting `ExtractActor` to a shared
      helper if practical, otherwise duplicate the small method).
- [ ] Map `TammaError` codes → HTTP: `PLAN.METRIC_KEY.INVALID`/`PLAN.PRICING_MODE.INVALID` → 422,
      `PLAN.CUSTOM.PUBLIC_REJECTED` → 400, unique violation → 409, `DeprecateResult(false, n)` → 409
      with `{ affectedTenantCount = n }`.
- [ ] Register routes in `Program.cs` under the `admin` group with explicit
      `.RequireAuthorization("PlatformOwnerAccess")` per route (copy the `tenant-databases` block at
      Program.cs:1400-1414).

### Phase 5 — Wire-up, quality gates, docs

- [ ] Register `IPlanCatalogService` write dependencies in `Program.cs` (if 34-1 already registers
      the service, confirm the `IPlatformEventPublisher` dependency resolves in its scope).
- [ ] Full suite green: `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests"`.
- [ ] `dotnet ef migrations has-pending-model-changes` → none. Build clean (no warnings-as-errors).
- [ ] Update CLAUDE.md "API Endpoints" / pricing notes if a catalog section exists; add an entry to
      the epic-34 status memory.

## Sequencing & dependencies

Phase 0 → 1 → 2 → (3 ∥ 4) → 5. Phase 1 (column) and Phase 2 (service) are the hard prerequisites;
Phase 3 (public reads) and Phase 4 (admin writes) can proceed in parallel once the service contract
exists. The whole story is blocked on **Story 34-1** being merged. Story **34-4** depends on this
story's `CustomTenantId` binding and the deprecate affected-count.

## Risks + mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| 34-1 schema not yet merged / shape differs from the spec | High | Phase 0 verifies the exact entity/enum/service shape before any code; stop if absent. Reference 34-1's actual files, not the spec, at implementation time. |
| Affected-tenant count is wrong because assignment model (34-4) doesn't exist yet | Medium | Use the legacy `Tenant.Plan` slug match as the transitional source with a `TODO(34-4)` to swap to the version-pinned assignment table; the 409/force contract is stable across the swap. |
| Custom plan leaks into the public catalog | High | Defence in depth: `ListActivePublicAsync` filters `IsCustom=false` (the real guard) AND a 400 rejects a custom-as-public request; explicit tenant-isolation integration test pins both. |
| Platform-event append failure rolls back a valid mutation | Medium | Treat the event append as best-effort after the DB commit: log ERROR and continue (the catalog row is the source of truth), matching the resilient-append convention; unit test asserts the write survives an append failure. |
| Using the wrong auth policy (e.g. `OwnerAccess` lets every personal-tenant owner through) | High | Use `PlatformOwnerAccess` (platformRole-keyed) verbatim for all mutations — the Program.cs:976-985 comment is explicit; member-403 integration test catches a wrong gate. |
| Migration is a baseline CHECK edit (Phase-0 collapsed-baseline rules) | Low | `CustomTenantId` is an additive column + new CHECK, not an edit to an existing baseline CHECK; still verify `has-pending-model-changes` reports none and put entity config in `TammaModelConfiguration.cs` only. |
| Scope creep into 34-3/34-4 | Medium | Honor the boundary note: validate `PricingMode` value only (no mode resolution); record `CustomTenantId` only (no assignment/enforcement). |

## Acceptance criteria (mirror of the story)

- [ ] `GET /api/pricing/plans` returns active public snapshots only (excludes custom/deprecated/draft); `GET /api/pricing/plans/{slug}` resolves one or 404; both readable by any `MemberAccess` caller.
- [ ] `POST`/`PUT /api/admin/pricing/plans` create/version a plan; `POST /api/admin/pricing/plans/custom` mints an `IsCustom` plan bound to `tenantId` — all gated by `PlatformOwnerAccess`; member → 403.
- [ ] A custom plan never appears in the public catalog (filter + 400 on publish attempt); `GET /api/pricing/plans/{customSlug}` → 404.
- [ ] Deprecate a version with active assignments → 409 + affected count, unless `?force=true` (leaves tenants on the deprecated version).
- [ ] Invalid `EntitlementMetricKey` or pricing-mode (not `platform_provided|byok`) → 422.
- [ ] Each mutation emits `PLAN.CATALOG.UPDATED` / `PLAN.CUSTOM.CREATED` to `platform_events` with `actorUserId`/`actorEmail` in tags and data.
- [ ] Tenant-isolation: a custom plan bound to tenant A is not surfaced to tenant B's public catalog.
- [ ] Additive `CustomTenantId` migration applies + rolls back; `has-pending-model-changes` reports none; full `Tamma.Api.Tests` suite green.
