# Story 34-4 — Per-Tenant Plan Assignment & Lifecycle (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: use superpowers:executing-plans (or
> superpowers:subagent-driven-development) to run this plan phase-by-phase. Steps use checkbox
> (`- [ ]`) tracking. Project is test-first (TDD) — every phase writes failing tests before
> implementation. C# docker-bound suites run via `sg docker -c "dotnet test …"`; build needs no
> wrapper. | Epic 34 (Pricing, Plans & Entitlements) | Story file:
> `docs/stories/epic-34/story-34-4/34-4-per-tenant-plan-assignment-and-lifecycle.md`

**Goal:** Make plan assignment a first-class, audited, *version-pinned* operation on the control
plane. A `TenantPlanAssignment` row becomes the source of truth for "what plan is this tenant on
right now" (and *which version*), with effective dates and upgrade/downgrade/cancel transitions
that hand Billing (Epic 35) a clean proration boundary. Deprecating a plan (34-1/34-2) must never
silently re-price an existing tenant.

## Non-goals (YAGNI guard)

- **NO catalog/version model here.** `Plan` (Version/Status/IsCustom/supersedes),
  `PlanEntitlement`, `PlanPrice`, `EntitlementMetricKey`, and `IPlanCatalogService` are **34-1**.
  This plan *consumes* them. If 34-1 has not landed, stop — it is a hard prerequisite.
- **NO admin catalog CRUD / custom-plan minting.** That is **34-2**. This plan only *reads*
  `IsCustom`/`BoundTenantId` and *adds* the subscribe handler to the `PricingEndpoints.cs` file
  34-2 introduces.
- **NO quota enforcement.** Downgrades are *flagged, never blocked* (epic boundary). Enforcement
  is a sibling story; this plan ships only the warning surface behind `ITenantUsageReader`.
- **NO Billing/proration math, NO Stripe.** This plan emits the `prorationBoundaryAt` marker on
  `TENANT.PLAN.CHANGED`; Epic 35 does the charging. `ITenantUsageReader` ships as a null/no-op
  default so no metering dependency leaks into the assignment path.
- **NO new scheduler.** Boundary activation reuses the existing `PlatformTaskQueueProcessor`
  (`MoveTenant` 202+poll pattern), not a bespoke timer.
- **NO change to resolution/tenancy semantics.** Tenant routing, schema-per-tenant, and the
  Epic-28 shadow columns are untouched apart from keeping `Tenant.Plan`/`PlanId` in lockstep with
  the active assignment.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### What exists today (the loose path this story replaces)

| Site | Behaviour today | File:line |
|---|---|---|
| `PATCH /api/admin/tenants/{id}/plan` → `AdminTenantsEndpoints.UpdateTenantPlan` | Inline: validates `req.PlanId`, checks `plan.IsActive`, flips the **shadow** `EF.Property<Guid?>(t,"PlanId")` + legacy `tenant.Plan` slug, emits a `PLAN.UPDATED` platform event. **No version, no effective dates, no proration boundary, no assignment row.** | `Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs:590-644` |
| `Tenant.PlanId` | NOT a property on the POCO — an Epic-28 **shadow** column declared `entity.Property<Guid?>("PlanId")`, indexed, FK→plans (Restrict). Read via `EF.Property`. | `Tamma.Data/Entities/Tenant.cs` (no PlanId field); `Tamma.Data/TammaModelConfiguration.cs:214,288,292-295` |
| `Tenant.Plan` | Legacy string slug (`"free"` default), still rendered by `AdminTenantsEndpoints.ListTenants`. | `Tamma.Data/Entities/Tenant.cs:11` |
| `Plan` entity | Today: `Id, Slug, DisplayName, MonthlyPriceUsd, Quotas(json), IsActive, PlacementPolicy`. **34-1 extends it** with `Version`, `Status` (active/deprecated/draft), `IsCustom`, `BillingInterval`, supersedes. This plan assumes the 34-1 shape. | `Tamma.Data/Entities/Plan.cs:14-47` |
| `PlansSeeder` | Seeds `free`/`team`/`enterprise` with stable UUIDs. 34-1 extends it to structured rows. | `Tamma.Data/Seeders/PlansSeeder.cs:57,69,81` |

### Reusable infrastructure (verified)

- **DbContext:** `ControlPlaneDbContext` exposes `Tenants`, `Plans`, `PlatformEvents`,
  `PlatformQueuedTasks`, `DomainEvents` DbSets — `Tamma.Data/ControlPlaneDbContext.cs:36,76,77,78,199`.
  Single model-config file `Tamma.Data/TammaModelConfiguration.cs` is the established source for
  entity config (partial unique indexes + CHECKs already used, e.g. `:249-250`, `:270-283`).
- **Events:** tenant-scope DCB append `IEventRepository.AppendAsync(DomainEvent)`
  (`Tamma.Data/Repositories/IEventRepository.cs`); platform-audit mirror
  `IPlatformEventPublisher.AppendAndPublishAsync(PlatformEvent)`
  (`Tamma.Data/Abstractions/IPlatformEventPublisher.cs:44`). Both `DomainEvent`/`PlatformEvent`
  carry `Type, TenantId, Tags(json), Metadata, Data, CreatedAt, SequenceNumber`. Actor-tag
  builder pattern: `AdminTenantsEndpoints.BuildAdminEvent`/`ExtractActor` (`:900-969`).
- **Platform queue (boundary work):** `IPlatformQueuedTaskRepository.EnqueueAsync(PlatformQueuedTask)`
  + the 202+poll pattern in `AdminTenantsEndpoints.MoveTenant` (`:659-735`) and its
  `MoveTenantTaskPayload`. `PlatformTaskQueueProcessor` claims tasks (RunOnStartup gate noted in
  MEMORY). Reuse this for `activate-scheduled-plan`.
- **Auth policies (`Tamma.Api/Program.cs:986-1005`):** `PlatformOwnerAccess` (JWT `platformRole =
  platform_admin`) for admin routes; `SettingsManage` (`settings:manage`, owner-only) for tenant
  self-service. Admin routes mapped under the `admin` group (`Program.cs:1292-1388`), e.g. the
  existing `admin.MapPatch("/tenants/{tenantId:guid}/plan", …UpdateTenantPlan)` at `:1378-1379`.
- **Tenant role gate (member 403):** `RequireTenantAdmin(HttpContext, out IResult? forbid)` reading
  `HttpContext.Items[RequireTenantMembershipFilter.TenantRoleItemKey]` +
  `TenantRoleHierarchy.IsAtLeast(role, Admin)` — `Tamma.Api/Endpoints/AlertEndpoints.cs:1008-1028`.
  Mirror this for `/api/pricing/subscribe`.
- **Mode + tenant context:** `ITammaModeProvider.Mode` (`Tamma.Api/Services/PromptStore/TammaMode.cs:48-50`,
  SingleUser|SaaS) and `ITenantContext.TenantId` (`Tamma.Data/ITenantContext.cs:5`) — the same pair
  `PromptEndpoints` uses to pick the override key (`PromptEndpoints.cs:36,70`).
- **DI extension precedent:** `Tamma.Api/Extensions/AlertServiceCollectionExtensions.cs` (and
  siblings) — add `PricingServiceCollectionExtensions` or extend 34-1's, wired in `Program.cs`.
- **Migration layout:** `Tamma.Data/Migrations/ControlPlane/` (e.g.
  `20260609205701_InitialControlPlane.cs`); `dotnet ef migrations add` produces the additive table;
  verify `has-pending-model-changes` reports none.

### Key gap this story closes

There is no record of *which plan version* a tenant is on, no effective dating, and no proration
boundary for Billing — only a mutable `(PlanId, Plan-slug)` pair that 34-1/34-2 deprecation would
silently re-interpret. The `TenantPlanAssignment` row + pinned `PlanVersion` is the fix.

---

## Architecture

```
caller (admin route | /api/pricing/subscribe)
        │
        ▼
IPlanAssignmentService.AssignAsync / CancelAsync / ActivateScheduledAsync
        │   1. resolve PlanSnapshot via IPlanCatalogService (34-1) → pin Version
        │   2. guards: draft → reject; deprecated → reject unless Force; custom → binding check
        │   3. classify direction (price/interval); read ITenantUsageReader → EntitlementWarnings
        │   4. TRANSACTION: cancel prior active (stamp EffectiveTo) → insert active/scheduled →
        │                   update Tenant.PlanId + Tenant.Plan
        │   5. emit DCB TENANT.PLAN.CHANGED (+ platform mirror) with proration marker
        ▼
ControlPlaneDbContext.TenantPlanAssignments  (ux_tpa_one_active_per_tenant partial unique index)
        │
boundary work: cancel → schedule plan_free row + enqueue activate-scheduled-plan task
        ▼
PlatformTaskQueueProcessor → ActivateScheduledAsync (promote scheduled → active)
```

### Per-mode ownership (mandatory two-scoping-model answer)

| Question | single-user | SaaS |
|---|---|---|
| Who assigns? | the sole user (authenticated; no RBAC) | platform owner via `/api/admin/tenants/{id}/plan` (`PlatformOwnerAccess`) |
| Who self-subscribes? | the sole user via `/api/pricing/subscribe` | `tenant_owner` (`SettingsManage`); `member` → 403 |
| Tenant resolution | `ITenantContext.TenantId` | `ITenantContext.TenantId` (subscribe) / route `{tenantId}` (admin) |
| Cross-tenant guard | n/a | subscribe ignores body tenant id; admin 404s unknown tenant |
| Actor stamped | the user → `AssignedByUserId` | platform/tenant owner → `AssignedByUserId` |
| Mode source | `ITammaModeProvider` | same |

---

## Phased task breakdown (TDD — tests first in every phase)

### Phase 1 — Entity, model config, migration, back-fill (AC1-3)

**Tests first** (`tests/Tamma.Data.Tests/Migrations/TenantPlanAssignmentMigrationTests.cs`,
Postgres-backed):
- Migration applies + rolls back cleanly.
- Partial unique index rejects a second `Status='active'` insert for the same tenant (raw insert).
- CHECKs reject bad `Status`, `EffectiveTo < EffectiveFrom`, `PlanVersion < 1`.
- Back-fill: N existing tenants → exactly N `active` assignments, each pinning the plan's current
  `Version`; a tenant with NULL shadow `PlanId` back-fills via `Plan` slug, else `plan_free`.

**Implement:**
- [ ] New `Tamma.Data/Entities/TenantPlanAssignment.cs` (entity per story).
- [ ] New `Tamma.Core/Enums/PlanAssignmentStatus.cs`, `PlanChangeDirection.cs` (string-backed
      constants to match the catalog enum style in 34-1).
- [ ] `ControlPlaneDbContext.cs` — add `DbSet<TenantPlanAssignment> TenantPlanAssignments`.
- [ ] `TammaModelConfiguration.cs` — entity config: `gen_random_uuid()` default, `Status` len 16,
      `ux_tpa_one_active_per_tenant` partial unique index, `(TenantId,Status)` + `PlanId` indexes,
      FKs (TenantId Cascade, PlanId Restrict), three CHECKs.
- [ ] `Tenant.cs` — doc-comment note only: `Plan`/`PlanId` are derived from the active assignment.
- [ ] `dotnet ef migrations add AddTenantPlanAssignment -c ControlPlaneDbContext` →
      `Migrations/ControlPlane/<ts>_AddTenantPlanAssignment.cs`; hand-add the partial unique index
      + the raw-SQL back-fill `INSERT … SELECT` (story sketch); run `has-pending-model-changes` → none.

**Approach note:** `Tenant.PlanId` stays a shadow column (Epic-28 owns the POCO); the back-fill and
the service both read/write it via `EF.Property<Guid?>` exactly as `AdminTenantsEndpoints` does.

### Phase 2 — `PlanAssignmentService` core: assign + guards + direction + pin (AC4-7, 13)

**Tests first** (`tests/Tamma.Api.Tests/Pricing/PlanAssignmentServiceTests.cs`, mocked
`IPlanCatalogService`/`ITenantUsageReader`/`IEventRepository`/`IPlatformEventPublisher`,
deterministic `TimeProvider`):
- Pin: assign v=N, then deprecate (catalog now returns v=N+1) → `GetActiveAsync` still v=N.
- One-active: second assign flips prior→cancelled (EffectiveTo stamped) + inserts new active;
  forced `DbUpdateException`/unique-violation path → translated 409, no second active row.
- Custom binding: misbound `IsCustom` plan → `PLAN.ASSIGN.CUSTOM_PLAN_MISBOUND`; bound → ok.
- Draft → reject always; deprecated → reject unless `Force=true`.
- Direction classification upgrade/downgrade/lateral; idempotent re-assign of same (PlanId,Version)
  → lateral no-op (no new event).
- Downgrade with over-limit usage (mock reader) → `Warnings` populated, event
  `entitlementWarnings=true`; reader unavailable → no warnings, no throw.
- Event-shape: `TENANT.PLAN.CHANGED` carries all tags + `prorationBoundaryAt`/`billingIntervalAnchor`.

**Implement:**
- [ ] `Services/Pricing/PlanAssignmentModels.cs` — `AssignPlanOptions`, `CancelPlanOptions`,
      `PlanAssignmentResult`, `EntitlementWarning`.
- [ ] `Services/Pricing/ITenantUsageReader.cs` + a `NullTenantUsageReader` default (returns
      "unknown" → no warnings). Real impl is Epic 35.
- [ ] `Services/Pricing/PlanAssignmentEventTypes.cs` — `TENANT.PLAN.CHANGED`, `TENANT.PLAN.CANCELLED`.
- [ ] `Services/Pricing/IPlanAssignmentService.cs` + `PlanAssignmentService.cs`:
      `AssignAsync` (resolve+pin via catalog, guards, direction, warnings, transactional swap,
      lockstep `Tenant.PlanId`/`Plan` via `EF.Property`, dual event emit). Translate unique
      violations to a typed 409.
- [ ] Reuse the actor-tag pattern from `AdminTenantsEndpoints.BuildAdminEvent` for the platform mirror.

### Phase 3 — Cancel + scheduled activation via platform queue (AC8-9)

**Tests first** (extend `PlanAssignmentServiceTests.cs` + a processor integration test):
- `CancelAsync`: stamps active `EffectiveTo` at period end, inserts `scheduled` `plan_free` row at
  that `EffectiveFrom`, emits `TENANT.PLAN.CANCELLED`; `Immediate=true` cancels now; already-free →
  no-op.
- `ActivateScheduledAsync`: promotes a due scheduled row, flips expiring active→cancelled, updates
  tenant columns, emits `TENANT.PLAN.CHANGED` `source=scheduled-activation`; idempotent by
  `assignmentId` (re-run = no-op).
- Integration: cancel → enqueue → run `PlatformTaskQueueProcessor` → scheduled row becomes active.

**Implement:**
- [ ] `PlanAssignmentService.CancelAsync` + `ActivateScheduledAsync` (period-end boundary via
      `Plan.BillingInterval` from 34-1; `TimeProvider`-driven).
- [ ] `Services/Provisioning/ActivateScheduledPlanTaskPayload.cs` (mirror `MoveTenantTaskPayload`:
      `TaskType` const + `TenantId` + `AssignmentId`).
- [ ] Enqueue the activation task in `CancelAsync`/`AssignAsync`-with-future-EffectiveFrom via
      `IPlatformQueuedTaskRepository.EnqueueAsync`; add a claim handler in the processor that
      dispatches to `ActivateScheduledAsync`.

### Phase 4 — Admin + tenant self-service endpoints (AC10-12)

**Tests first** (`tests/Tamma.Api.Tests/Pricing/PlanAssignmentEndpointsTests.cs`, WebApplicationFactory):
- `PATCH`/`PUT /api/admin/tenants/{id}/plan` round-trip (PlatformOwnerAccess); non-platform JWT →
  403; unknown tenant → 404; draft/deprecated/custom-misbound → 422; idempotent PUT.
- `POST /api/pricing/subscribe`: tenant_owner → 200; `member` → 403; custom/draft/deprecated/
  non-public → 422; **tenant isolation** — caller A's body tenant id is ignored, route uses
  `ITenantContext`, cannot touch tenant B.
- `POST /api/admin/tenants/{id}/plan/cancel` → 200, scheduled `plan_free` row created.
- Single-user mode: the sole user can both admin-assign and self-subscribe.

**Implement:**
- [ ] Refactor `AdminTenantsEndpoints.UpdateTenantPlan` to delegate to `AssignAsync` (drop the
      inline plan-flip + `PLAN.UPDATED`; keep `PLAN.UPDATED` as an alias tag on `TENANT.PLAN.CHANGED`
      for one release).
- [ ] Add `AdminTenantsEndpoints.PutTenantPlan` + `CancelTenantPlan`.
- [ ] Add `PricingEndpoints.Subscribe` (file from 34-2): resolve tenant via `ITenantContext`,
      `RequireTenantAdmin` member-403 gate (mirror `AlertEndpoints`), public-plan validation via
      `IPlanCatalogService`, then `AssignAsync`.
- [ ] `Program.cs` — map `admin.MapPut("/tenants/{tenantId:guid}/plan", …)` +
      `admin.MapPost("/tenants/{tenantId:guid}/plan/cancel", …)` (`PlatformOwnerAccess`) and
      `MapPost("/api/pricing/subscribe", …)` (`SettingsManage`); register
      `IPlanAssignmentService`/`ITenantUsageReader` via `PricingServiceCollectionExtensions`.

### Phase 5 — Wire-up, full suite, docs (AC14)

- [ ] `PricingServiceCollectionExtensions.cs` registers `IPlanAssignmentService`,
      `NullTenantUsageReader`, and the activation task handler; called from `Program.cs`.
- [ ] Run `dotnet build` then `sg docker -c "dotnet test apps/tamma-elsa"` — full green.
- [ ] Verify `has-pending-model-changes` → none; migration up/down clean.
- [ ] Confirm `AdminTenantsEndpoints.ListTenants` still renders plan (lockstep columns intact).

---

## Sequencing & dependencies

- **Hard prerequisite:** 34-1 (versioned `Plan` + `IPlanCatalogService` + `EntitlementMetricKey`)
  and 34-2 (`PricingEndpoints.cs` + custom-plan binding). Do not start Phase 2 until 34-1's catalog
  service compiles.
- Internal order: **Phase 1 → 2 → 3 → 4 → 5.** Phase 1 (entity+migration) gates everything;
  Phases 2-3 are the service core; Phase 4 is the API surface; Phase 5 is wiring + green-suite.
- **Blocks:** Epic 35 Billing (consumes `TENANT.PLAN.CHANGED` proration boundary +
  `ScheduledEffectiveAt`); Epic 34 Enforcement (consumes the active pinned snapshot).

---

## Risks + mitigations

- **Plan deprecation silently re-prices tenants** (the headline risk). Mitigation: `PlanVersion`
  is a *denormalized pinned column*, never a "latest" join; Phase-1 test asserts the pin survives a
  deprecate. Phase-2 test re-asserts at the service layer.
- **Two active assignments via a race** (concurrent admin + self-service). Mitigation: partial
  unique index `ux_tpa_one_active_per_tenant` is the DB-level invariant; the service catches the
  unique violation and returns 409 rather than two rows. Phase-1 (DB) + Phase-2 (service) tests.
- **Cancel drops the tenant immediately instead of at period end.** Mitigation: cancel writes a
  *scheduled* row at the boundary and only the queue-driven `ActivateScheduledAsync` promotes it;
  default is period end, `Immediate` is opt-in. Phase-3 tests pin both paths.
- **Downgrade accidentally blocks** (scope creep into enforcement). Mitigation: `AssignAsync`
  always succeeds for a valid plan and only returns `Warnings`; `ITenantUsageReader` is a no-op
  default so no metering coupling. Boundary note + Phase-2 test guard this.
- **Boundary activation lost if host was down at the boundary.** Mitigation: the activation task is
  idempotent by `assignmentId` and re-evaluated on the next `PlatformTaskQueueProcessor` tick;
  `RunOnStartup` gating (per MEMORY) means prod must enable the processor for queued boundary work.
- **Dashboard timeline blanks on `PLAN.UPDATED` → `TENANT.PLAN.CHANGED` rename.** Mitigation: keep
  `PLAN.UPDATED` as an alias tag on the new event for one release.
- **Migration discipline.** Table is additive (no CHECK edits to existing tables, so Phase-0
  collapsed-baseline rules don't apply), but still mirror entity config in
  `TammaModelConfiguration.cs` only and verify `has-pending-model-changes` → none after the add.
- **34-1 shape drift.** If 34-1 named `Plan.Status` values or `IPlanCatalogService` differently,
  align the guard logic to the real names at Phase-2 start (read 34-1's merged code first).

---

## Acceptance criteria (mirror of the story)

- [ ] `TenantPlanAssignment` entity + table + additive migration; partial unique index gives one
      `active` assignment per tenant; CHECKs on status/window/version. (AC1-2)
- [ ] `Tenant.PlanId`/`Plan` derived & back-filled from the active assignment; assignment is the
      source of truth thereafter. (AC3)
- [ ] `IPlanAssignmentService.AssignAsync` pins the plan version, refuses draft, refuses
      deprecated-without-force, validates custom-plan binding. (AC4)
- [ ] Transactional swap keeps exactly one active row + lockstep tenant columns; concurrent assign
      → 409, never two active rows. (AC5)
- [ ] Upgrade/downgrade emit `TENANT.PLAN.CHANGED` with old/new plan+version, direction, and a
      proration boundary marker for Epic 35. (AC6)
- [ ] Over-limit downgrades are flagged in `PlanAssignmentResult.Warnings` (not blocked); usage
      read behind `ITenantUsageReader`. (AC7)
- [ ] Cancel schedules `EffectiveTo` at period end + a `scheduled` `plan_free` row; boundary
      activation promotes it via the platform queue. (AC8-9)
- [ ] `PATCH`+`PUT /api/admin/tenants/{id}/plan` (PlatformOwnerAccess) delegate to the service;
      `POST /api/pricing/subscribe` (SettingsManage) self-serves a public plan; `member` → 403. (AC10-11)
- [ ] Per-mode + per-tenant ownership honored; tenant isolation enforced. (AC12)
- [ ] Version pinning survives plan deprecation (no re-price). (AC13)
- [ ] Unit + integration tests cover pinning, one-active invariant, custom misassignment,
      cancel→free scheduling, DCB emission, RBAC, and tenant isolation; full suite green. (AC14)
