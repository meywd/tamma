# Story 34-2: Plan Catalog Admin API & Custom Enterprise Plans

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase development workflow, the `.dev/` knowledge base
(spikes, bugs, findings, decisions), TRACE/DEBUG logging requirements, the test-first (TDD)
workflow, the 100% critical-path coverage requirement, and quality-gate enforcement.

**Failure to follow this process will result in rework.**

## User Story

As a **platform owner**,
I want a platform-owner-gated admin API to create, version, and deprecate price-book plans, plus a
way to mint a custom enterprise plan bound to a single tenant — while any authenticated tenant
member can read the active public catalog —
so that I can run the public pricing/upgrade UI off a live catalog and close bespoke enterprise
deals with negotiated entitlements and pricing without polluting the public plan list.

## Priority

P0 - The catalog admin surface and custom-plan minting are prerequisites for plan assignment
(34-4) and the pricing/upgrade UI; Billing (Epic 35) and Enforcement charge/consume against plan
versions that this story lets operators create and deprecate.

## Acceptance Criteria

1. `GET /api/pricing/plans` returns the list of **active, public** (`Status='active'` AND
   `IsCustom=false`) resolved `PlanSnapshot` rows (features + entitlements + prices), readable by
   any authenticated tenant member (`MemberAccess`); deprecated, draft, and custom plans are
   excluded from this list.
2. `GET /api/pricing/plans/{slug}` returns the single active public `PlanSnapshot` for that slug via
   `IPlanCatalogService` (the read seam from Story 34-1); a slug that has no active public version
   returns 404; a custom plan's slug is never resolvable through this public route (404).
3. `POST /api/admin/pricing/plans` creates a new plan (initial version) and `PUT /api/admin/pricing/plans/{slug}` versions an existing plan (new `Version` row, prior active version flipped to `deprecated`) — both gated by `PlatformOwnerAccess`; the one-active-version-per-slug invariant from Story 34-1 holds.
4. `POST /api/admin/pricing/plans/custom` mints an `IsCustom=true` plan whose snapshot is bound to a
   target `tenantId` (recorded in plan metadata / a `CustomTenantId` column), gated by
   `PlatformOwnerAccess`; the response includes the new plan id, slug, and version.
5. A custom plan request that sets `IsPublic`/surfaces the plan in the public catalog (e.g.
   `Status='active'` with `IsCustom=true` intended for `GET /api/pricing/plans`) is rejected with
   **400** — a custom plan must never appear in the public list; `GET /api/pricing/plans` proves the
   exclusion in tests.
6. Custom plans are bound to exactly one tenant: this story records the binding (`CustomTenantId`);
   the **enforcement** that a custom plan may only be *assigned* to its bound tenant is owned by
   Story 34-4 (assignment) — this story does not implement assignment, only the catalog row, the
   binding column, and the validation that a custom plan stays out of the public catalog.
7. `DELETE /api/admin/pricing/plans/{slug}/versions/{version}` (deprecate a specific version) with
   active tenant assignments referencing that version returns **409** plus the count of affected
   tenants, **unless** `?force=true` is supplied (force deprecates and leaves existing tenants on the
   deprecated version — re-pricing is never silent, per Story 34-1's immutability rule); affected
   tenants are counted by querying `Tenant` rows whose assignment pins the version (assignment model
   from Story 34-4; until 34-4 lands, the count comes from the legacy `Tenant.Plan` slug match —
   see Dev Notes).
8. Entitlement metric keys submitted on create/version are validated against the
   `EntitlementMetricKey` enum (`Tamma.Core/Enums`, from Story 34-1); pricing-mode is validated
   against `platform_provided | byok`; any invalid metric key or pricing mode returns **422** with a
   field-level error naming the offending key.
9. Every mutation emits a DCB platform event via `IPlatformEventPublisher` to `platform_events`:
   `PLAN.CATALOG.UPDATED` (create/version/deprecate) and `PLAN.CUSTOM.CREATED` (custom mint), each
   carrying `actorUserId` + `actorEmail` (from the JWT `sub`/`email` claims), `slug`, `version`, and
   for custom plans the bound `tenantId` — in both `tags` (queryable) and `data` (immutable record),
   mirroring `AdminTenantsEndpoints.BuildAdminEvent`.
10. A `member`-role caller receives **403** on every `/api/admin/pricing/*` route (mutations and
    custom mint); a `PlatformOwnerAccess` caller can create, version, and deprecate.
11. A custom plan round-trips: `POST .../custom` then `GET /api/admin/pricing/plans` lists it,
    `GET /api/pricing/plans` (public) excludes it, and `GET /api/pricing/plans/{slug}` returns 404.
12. All mutating endpoints are idempotent-safe under repeated PlatformOwnerAccess submission only in
    that they validate inputs before writing; a `PUT` that would create a duplicate `(Slug, Version)`
    is rejected by the Story 34-1 `UNIQUE(Slug, Version)` constraint → translated to **409**.
13. Reads through `IPlanCatalogService` never leak the encrypted/internal plan-price metered JSON in
    a malformed shape — the DTO mapping returns the typed `PlanSnapshot` projection only (no raw EF
    entity is serialized).
14. Unit + integration tests cover: public list excludes custom/deprecated/draft; member 403 on
    admin routes; platform owner version + deprecate; deprecate-with-assignments 409 + force=true
    path; invalid metric key 422; custom plan round-trip + public exclusion; both DCB events emitted
    with actor id; tenant-isolation (a custom plan bound to tenant A is not surfaced to tenant B's
    public catalog — which is trivially true since custom plans are excluded from public, but the
    test pins it).

## Technical Design

### Namespace / File Structure

```
apps/tamma-elsa/src/Tamma.Api/
  Endpoints/
    PricingEndpoints.cs                     # NEW — public read routes (MemberAccess)
    Admin/
      AdminPricingEndpoints.cs              # NEW — admin CRUD + custom mint (PlatformOwnerAccess)
  Dtos/
    Pricing/
      PlanDtos.cs                           # NEW — request/response DTOs + PlanSnapshot projection
  Services/
    Pricing/
      PlanCatalogService.cs                 # MODIFY (created in 34-1) — add admin write methods
      IPlanCatalogService.cs               # MODIFY (created in 34-1) — extend with write contract
  Program.cs                                # MODIFY — register routes + service
```

> Story 34-1 creates `IPlanCatalogService` / `PlanCatalogService`, the `PlanSnapshot` read model,
> the `Plan`/`PlanFeature`/`PlanEntitlement`/`PlanPrice` entities, and the `EntitlementMetricKey`
> enum. Story 34-2 **extends** the catalog service with the write/admin path and adds the two
> endpoint files. The `Plan` entity gains an additive `CustomTenantId` column (see migration sketch).

### Plan entity extension (additive migration on the 34-1 schema)

Story 34-1 already extends `Plan` with `Version`, `Status` (`active|deprecated|draft`), `IsCustom`,
`BillingInterval`, and the supersede chain. Story 34-2 adds one nullable column to bind a custom
plan to its tenant:

```csharp
// apps/tamma-elsa/src/Tamma.Data/Entities/Plan.cs  (MODIFY — add property)
/// <summary>
/// Set only when <see cref="IsCustom"/> is true. The single tenant this
/// bespoke plan was negotiated for. Story 34-4 enforces that a custom plan
/// may only be ASSIGNED to this tenant; Story 34-2 only records the binding
/// and uses it to keep the plan out of the public catalog.
/// </summary>
public Guid? CustomTenantId { get; set; }
```

EF migration sketch (additive — normal `dotnet ef migrations add`, verify
`has-pending-model-changes` reports none afterward):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddCustomTenantIdToPlan.cs
migrationBuilder.AddColumn<Guid>(
    name: "CustomTenantId",
    schema: null,                 // control-plane (public) schema
    table: "plans",
    type: "uuid",
    nullable: true);

// Partial CHECK: CustomTenantId is set IFF IsCustom is true.
migrationBuilder.Sql(
    "ALTER TABLE plans ADD CONSTRAINT ck_plans_custom_tenant " +
    "CHECK ((is_custom = false AND custom_tenant_id IS NULL) " +
    "    OR (is_custom = true  AND custom_tenant_id IS NOT NULL));");
```

### Service contract extension

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Pricing/IPlanCatalogService.cs  (MODIFY)
public interface IPlanCatalogService
{
    // --- read (from Story 34-1) ---
    Task<IReadOnlyList<PlanSnapshot>> ListActivePublicAsync(CancellationToken ct = default);
    Task<PlanSnapshot?> GetActivePublicBySlugAsync(string slug, CancellationToken ct = default);
    Task<PlanSnapshot?> GetSnapshotAsync(string slug, int version, CancellationToken ct = default);

    // --- admin (Story 34-2) ---
    Task<PlanSnapshot> CreatePlanAsync(CreatePlanRequest req, ActorContext actor, CancellationToken ct = default);
    Task<PlanSnapshot> VersionPlanAsync(string slug, VersionPlanRequest req, ActorContext actor, CancellationToken ct = default);
    Task<PlanSnapshot> CreateCustomPlanAsync(CreateCustomPlanRequest req, ActorContext actor, CancellationToken ct = default);
    Task<DeprecateResult> DeprecateVersionAsync(string slug, int version, bool force, ActorContext actor, CancellationToken ct = default);
    Task<IReadOnlyList<PlanSnapshot>> ListAllForAdminAsync(PlanListFilter filter, CancellationToken ct = default);
}

public sealed record DeprecateResult(bool Deprecated, int AffectedTenantCount);
public sealed record ActorContext(string? UserId, string? Email, string? PlatformRole);
```

`PlanCatalogService` validates every `EntitlementMetricKey` and `PricingMode` BEFORE any write,
throws `TammaError` with stable codes (`PLAN.METRIC_KEY.INVALID`, `PLAN.PRICING_MODE.INVALID`,
`PLAN.CUSTOM.PUBLIC_REJECTED`) that the endpoints translate to 422/400, and emits the platform event
on success via the injected `IPlatformEventPublisher`.

### Endpoint shape

Public (any authenticated tenant member):

```
GET  /api/pricing/plans                              -> 200 PlanSnapshot[]   (active+public only)   [MemberAccess]
GET  /api/pricing/plans/{slug}                       -> 200 PlanSnapshot | 404                       [MemberAccess]
```

Admin (platform owner only — `PlatformOwnerAccess`, mirroring `/api/admin/tenant-databases`):

```
GET    /api/admin/pricing/plans                      -> 200 PlanSnapshot[]   (filter: status, isCustom, tenantId)
POST   /api/admin/pricing/plans                      -> 201 PlanSnapshot     (create initial version)
PUT    /api/admin/pricing/plans/{slug}               -> 200 PlanSnapshot     (new version; prior -> deprecated)
POST   /api/admin/pricing/plans/custom               -> 201 PlanSnapshot     (mint IsCustom bound to tenantId)
DELETE /api/admin/pricing/plans/{slug}/versions/{version}?force=true|false
                                                     -> 204 | 409 {affectedTenantCount} (deprecate)
```

Request DTOs (`Dtos/Pricing/PlanDtos.cs`):

```csharp
public sealed record CreatePlanRequest(
    string Slug, string DisplayName, string BillingInterval,
    IReadOnlyList<PlanFeatureDto> Features,
    IReadOnlyList<PlanEntitlementDto> Entitlements,
    IReadOnlyList<PlanPriceDto> Prices);

public sealed record CreateCustomPlanRequest(
    Guid TenantId, string DisplayName, string BillingInterval,
    IReadOnlyList<PlanFeatureDto> Features,
    IReadOnlyList<PlanEntitlementDto> Entitlements,
    IReadOnlyList<PlanPriceDto> Prices);
    // slug is server-derived (e.g. custom-{tenantSlug}-{n}); IsPublic is never accepted.

public sealed record PlanEntitlementDto(string MetricKey, long? LimitValue, string Period, string OverageMode);
public sealed record PlanPriceDto(string PricingMode, decimal RecurringUsd, decimal SeatUsd, string? MeteredComponentJson);
```

### Per-mode + per-tenant handling

- **single-user mode**: the public read routes (`/api/pricing/plans*`) return the active public
  catalog to the sole user; the admin routes are gated by `PlatformOwnerAccess` — in single-user the
  sole user holds the `platform_admin` platformRole and can manage the catalog. Custom plans are a
  SaaS-deal concept but the route stays available (the bound tenant is the user's own tenant).
- **SaaS mode**: public read routes return the catalog to any tenant member (used by the pricing /
  upgrade UI in `packages/dashboard-user`); admin/custom routes require platform-owner — a
  `tenant_owner`/`tenant_admin`/`member` all receive 403 (the route gate is `PlatformOwnerAccess`,
  which keys off the `platformRole` claim, NOT the per-tenant role). Custom-plan minting is the
  primary SaaS use: a custom plan is bound to one tenant via `CustomTenantId`. Tenant isolation is
  preserved by construction: custom plans are excluded from the public catalog, so tenant B can
  never see tenant A's custom plan via `/api/pricing/plans*`.
- The price-book lives entirely in the **control plane** (`ControlPlaneDbContext`); there is no
  per-tenant schema write here, so no `TenantDbContext` involvement. This is consistent with the
  existing `Plan`/`PlansSeeder` location.

### DCB events

| Event | When | Tags (queryable) | Data (immutable) |
|---|---|---|---|
| `PLAN.CATALOG.UPDATED` | create / version / deprecate | `slug`, `version`, `action` (`created\|versioned\|deprecated`), `actorUserId`, `actorEmail`, `source=admin` | full request echo + prior version + `affectedTenantCount` (on deprecate) + actor fields |
| `PLAN.CUSTOM.CREATED` | custom mint | `slug`, `version`, `tenantId`, `actorUserId`, `actorEmail`, `source=admin` | full custom request + actor fields |

Emitted to `platform_events` (control-plane) via `IPlatformEventPublisher.AppendAndPublishAsync`,
following the `AdminTenantsEndpoints.BuildAdminEvent` actor-capture pattern (JWT `sub`/`email` into
both `tags` and `data`). Event names follow the `AGGREGATE.ACTION.STATUS`-style convention used
across the codebase (`PLAN.UPDATED` already referenced in the `Plan` entity doc-comment;
`PLAN.VERSION.CREATED`/`PLAN.DEPRECATED` are the 34-1 lifecycle events — 34-2 adds the
admin-surface-level `PLAN.CATALOG.UPDATED`/`PLAN.CUSTOM.CREATED`).

### Integration points

- **Story 34-1**: `IPlanCatalogService`, `PlanSnapshot`, the four entities, `EntitlementMetricKey`
  enum, and the `UNIQUE(Slug, Version)` + one-active-version invariant are all consumed here.
- **Story 34-4**: consumes the deprecate-with-assignments count and the `CustomTenantId` binding to
  enforce that a custom plan is only assignable to its bound tenant (out of scope here).
- **`packages/dashboard`** (admin) renders the admin catalog CRUD; **`packages/dashboard-user`**
  (tenant) renders the public pricing/upgrade UI from `GET /api/pricing/plans` (UI is a later
  story / sibling concern — this story ships the API only).
- **Auth**: `PlatformOwnerAccess` and `MemberAccess` policies already exist in `Program.cs`; reused
  verbatim with explicit `.RequireAuthorization(...)` per route (mirrors
  `AdminTenantDatabasesEndpoints` registration).

## Dependencies

**Internal:**
- **Prerequisite**: Story 34-1 (Plan & Price-Book Catalog Data Model) — entities, enum,
  `IPlanCatalogService`, `PlanSnapshot`, immutability/one-active-version invariants.
- **Blocks**: Story 34-4 (Per-Tenant Plan Assignment & Lifecycle) — uses the deprecate
  affected-count, custom-plan binding, and active public catalog for upgrade/downgrade.
- **Related**: Epic 35 (Billing) charges against plan versions created here; Story 34-3 (BYOK vs
  platform-provided pricing mode) shares the `PricingMode` enum validated here.
- **Related**: Epic 28 (control-plane/tenancy) — `ControlPlaneDbContext`, `PlatformEvent` store,
  `PlatformOwnerAccess` policy.

**External:**
- No new third-party libraries. (Billing provider — Stripe — integration is Epic 35; this story has
  no Stripe dependency.)
- EF Core 9 / Npgsql migration tooling for the additive `CustomTenantId` column.

## Testing Strategy

**Unit tests** (`tests/Tamma.Api.Tests/Pricing/PlanCatalogServiceTests.cs`):
1. `ListActivePublicAsync` excludes `IsCustom=true`, `Status='deprecated'`, and `Status='draft'`.
2. `CreatePlanAsync` writes a version-1 active row and emits `PLAN.CATALOG.UPDATED` (action=created)
   with the actor id; `VersionPlanAsync` flips the prior active to deprecated and creates version N+1
   (one-active-version invariant preserved).
3. Invalid `MetricKey` -> `TammaError("PLAN.METRIC_KEY.INVALID")`; invalid `PricingMode` ->
   `TammaError("PLAN.PRICING_MODE.INVALID")` — asserted to map to 422 at the endpoint.
4. `CreateCustomPlanAsync` sets `IsCustom=true` + `CustomTenantId`, emits `PLAN.CUSTOM.CREATED` with
   the bound `tenantId`; a custom request flagged public -> `TammaError("PLAN.CUSTOM.PUBLIC_REJECTED")`
   -> 400.
5. `DeprecateVersionAsync` with affected tenants -> `DeprecateResult(false, count)` (no write);
   with `force=true` -> deprecates and returns `DeprecateResult(true, count)`; with zero affected ->
   deprecates.
6. Duplicate `(Slug, Version)` -> unique-violation -> translated 409.

**Integration tests** (`tests/Tamma.Api.Tests/Pricing/PricingEndpointsTests.cs`,
`AdminPricingEndpointsTests.cs` — `WebApplicationFactory` + Testcontainers Postgres, docker-bound;
run via `sg docker -c "dotnet test ..."`):
7. Member-role JWT -> 403 on every `/api/admin/pricing/*` route; platform-owner -> 200/201.
8. Custom plan round-trip: `POST .../custom` -> 201; `GET /api/admin/pricing/plans` lists it;
   `GET /api/pricing/plans` excludes it; `GET /api/pricing/plans/{slug}` -> 404.
9. **Tenant-isolation**: mint a custom plan bound to tenant A; assert tenant B's
   `GET /api/pricing/plans` does not contain it and `GET /api/pricing/plans/{customSlug}` -> 404 for
   tenant B (and A).
10. Deprecate-with-assignments 409 surfaces `affectedTenantCount`; `?force=true` deprecates.
11. Both DCB events land in `platform_events` with `actorUserId`/`actorEmail` in tags and data.

**Mocks**: no Stripe/provider calls in this story; the platform-event publisher is stubbed in unit
tests (`IPlatformEventPublisher` test double recording calls) and real in integration tests.
`ITammaModeProvider` is driven SingleUser vs SaaS to assert the public route works in both modes and
the admin route stays platform-owner-gated.

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/PricingEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminPricingEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Dtos/Pricing/PlanDtos.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/IPlanCatalogService.cs` | Modify (extend write contract — created in 34-1) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PlanCatalogService.cs` | Modify (add admin write methods — created in 34-1) |
| `apps/tamma-elsa/src/Tamma.Data/Entities/Plan.cs` | Modify (add `CustomTenantId`) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddCustomTenantIdToPlan.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (register routes + service) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PlanCatalogServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PricingEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/AdminPricingEndpointsTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md).
2. Searched `.dev/` for related spikes, bugs, findings, and decisions.
3. Read Story 34-1's implementation (entities, `IPlanCatalogService`, `PlanSnapshot`, the
   one-active-version invariant) — 34-2 builds directly on it; do not duplicate the schema.
4. Reviewed `AdminTenantDatabasesEndpoints.cs` (route registration + `PlatformOwnerAccess` pattern)
   and `AdminTenantsEndpoints.BuildAdminEvent` (actor-capture for platform events).
5. Planned the TDD Red-Green-Refactor cycle (tests first).

### Key Design Decisions

- **Reuse the existing policies verbatim.** `PlatformOwnerAccess` (platformRole-keyed) gates all
  mutations and the custom mint; `MemberAccess` gates the public read. Do NOT invent a new policy —
  the existing two cover the exact RBAC matrix (platform owner writes, any member reads).
- **Custom plans stay out of the public catalog by construction.** The public `ListActivePublicAsync`
  filters `IsCustom=false`, so a custom plan is invisible to the pricing UI even if its `Status` is
  `active`. The 400 on an attempt to publish a custom plan is a guard against an operator request
  shape that would imply public visibility — the filter is the real defence, the 400 is the
  fail-loud signal.
- **Deprecate is non-destructive.** `?force=true` deprecates the version but leaves assigned tenants
  on it (immutability rule from 34-1) — re-pricing existing tenants is an explicit assignment
  operation (34-4), never a side effect of catalog deprecation.
- **Affected-tenant count source.** Until Story 34-4's tracked assignment lands, the count comes
  from matching `Tenant.Plan` slug (the legacy string column). Once 34-4 ships the version-pinned
  assignment, switch the count query to the assignment table (single-line change behind
  `IPlanCatalogService`). Note this transitional behaviour in the code with a TODO referencing 34-4.

### Boundary note (sibling-epic ownership)

- **34-1** owns the schema, the read seam, and the immutability/version invariants — do not reshape
  them; only add the `CustomTenantId` binding column.
- **34-3** owns BYOK-vs-platform-provided pricing-MODE selection per `(tenant, provider)`. This
  story only validates the `PricingMode` enum value on a `PlanPrice` row; it does NOT resolve a
  tenant's mode at LLM-call time or touch the secret cabinet.
- **34-4** owns plan assignment and the enforcement that a custom plan is only assignable to its
  bound tenant — this story records the binding but does not assign.

## Logging Requirements

- **INFO**: plan created/versioned/deprecated (slug, version, actorUserId), custom plan minted
  (slug, version, boundTenantId, actorUserId), public catalog listed (count).
- **DEBUG**: snapshot resolution (slug, version), entitlement/pricing-mode validation pass,
  affected-tenant count query result.
- **WARN**: deprecate blocked by active assignments (slug, version, affectedTenantCount) without
  force; custom plan publish attempt rejected (slug, tenantId).
- **ERROR**: platform-event append failure (do not fail the mutation if the event append errors —
  log ERROR and continue, mirroring the resilient append pattern; the mutation is the source of
  truth), DB constraint violation surfaced as 409.
- **Structured context**: include `{ slug, version, isCustom, boundTenantId, actorUserId, mode }`
  where applicable.
- **Credential safety**: never log JWTs, encrypted connection strings, or any secret-cabinet
  material; this story handles none, but the logging discipline is mandatory.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
