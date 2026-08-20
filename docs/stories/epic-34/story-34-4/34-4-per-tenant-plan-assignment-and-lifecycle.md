# Story 34-4: Per-Tenant Plan Assignment & Lifecycle

Status: done
<!-- Flipped drafted -> done 2026-08-18. The deliverable named in the acceptance criteria
     was located in apps/tamma-elsa/src (and its suites in apps/tamma-elsa/tests) before this
     header was changed — not taken from a changelog. The per-story evidence is recorded
     inline on this story's line in docs/sprint-status.yaml.
-->

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase workflow (Read → Research → Break Down → TDD →
Quality Gates → Failure Handling), the `.dev/` knowledge-base usage rules, TRACE/DEBUG logging
requirements, the test-first (TDD) mandate, 100% critical-path coverage, and build-success
enforcement. Failure to follow it results in rework.

## User Story

As a **platform owner (and, in SaaS mode, a tenant owner/admin)**,
I want plan assignment to be a first-class, audited operation that pins each tenant to a specific
plan **version** with effective dates and proper upgrade/downgrade/cancel transitions,
so that deprecating a plan never silently re-prices existing tenants, every price change is
reproducible from the audit trail, and Billing (Epic 35) receives clean proration boundaries.

## Priority

P0 — the assignment layer is the source of truth for "what plan is this tenant on right now",
which Billing (Epic 35) charges against and Enforcement consumes. Until this lands, the loose
`Tenant.Plan` string and the ad-hoc `PATCH /api/admin/tenants/{id}/plan` path
(`AdminTenantsEndpoints.UpdateTenantPlan`) are the only mechanism, and neither records a version,
effective dates, nor a proration boundary.

## Acceptance Criteria

1. A new control-plane entity `Tamma.Data.Entities.TenantPlanAssignment` exists with columns
   `Id` (UUIDv7), `TenantId` (FK → `tenants.Id`), `PlanId` (FK → `plans.Id`, which 34-1 made a
   versioned row), `PlanVersion` (int, denormalized copy of the pinned `Plan.Version` so a later
   plan deprecation cannot retro-mutate this assignment's effective version), `Status`
   (`active` | `scheduled` | `cancelled`), `EffectiveFrom` (UTC), `EffectiveTo` (UTC, nullable),
   `AssignedByUserId` (Guid?, the actor), `Reason` (text, nullable), `CreatedAt`, `UpdatedAt`. It
   is registered as `ControlPlaneDbContext.TenantPlanAssignments` and configured in
   `TammaModelConfiguration.cs`, with an additive EF migration under
   `Tamma.Data/Migrations/ControlPlane/`.

2. A partial unique index enforces **at most one `active` assignment per tenant**:
   `CREATE UNIQUE INDEX ... ON tenant_plan_assignments (TenantId) WHERE Status = 'active'`
   (modeled via `entity.HasIndex(e => e.TenantId).IsUnique().HasFilter("\"Status\" = 'active'")`),
   plus CHECK constraints on `Status` ∈ {active, scheduled, cancelled} and
   `EffectiveTo IS NULL OR EffectiveTo >= EffectiveFrom`. A `(TenantId, Status)` index backs the
   "current assignment" lookup.

3. `Tenant.PlanId` (today an Epic-28 **shadow** property, `EF.Property<Guid?>(t, "PlanId")`) and
   the legacy `Tenant.Plan` slug string become **derived / back-filled** from the tenant's active
   assignment — the assignment row is the source of truth. The EF migration back-fills exactly one
   `active` `TenantPlanAssignment` per existing tenant from its current `PlanId`/`Plan` (pinning
   the plan's current `Version`); after back-fill, `AssignAsync` is the only writer of those two
   tenant columns and keeps them in lockstep with the active assignment.

4. A new `IPlanAssignmentService` (`Tamma.Api/Services/Pricing/PlanAssignmentService.cs`) exposes
   `AssignAsync(Guid tenantId, Guid planId, AssignPlanOptions opts, CancellationToken ct)` that
   (a) resolves the plan version via 34-1's `IPlanCatalogService` and **pins** `PlanVersion`,
   (b) refuses to assign a plan whose `Status` is `draft`, (c) refuses a plan whose `Status` is
   `deprecated` unless `opts.Force == true`, and (d) validates custom-plan binding: an
   `IsCustom == true` plan (34-2) may only be assigned to the tenant it is bound to — otherwise
   the call is rejected with a typed `TammaError("PLAN.ASSIGN.CUSTOM_PLAN_MISBOUND", …)`.

5. A successful assignment is transactional: within one DB transaction it flips the prior `active`
   assignment to `cancelled` (stamping its `EffectiveTo`), inserts the new `active` row, and
   updates `Tenant.PlanId` + `Tenant.Plan`. The one-active-per-tenant invariant (AC2) is enforced
   by the partial unique index; a concurrent double-assign loses the race with a unique-violation
   that the service translates to a `409`/retry rather than two active rows.

6. Upgrade and downgrade transitions emit a DCB `TENANT.PLAN.CHANGED` event (via
   `IEventRepository.AppendAsync` for tenant-scope, mirrored to `PlatformEvent` via
   `IPlatformEventPublisher` for the platform audit timeline) carrying `tenantId`,
   `oldPlanId`+`oldPlanVersion`, `newPlanId`+`newPlanVersion`, `direction`
   (`upgrade`|`downgrade`|`lateral`), `mode` (single-user|saas), `actorUserId`, and a
   **proration-boundary marker** (`prorationBoundaryAt` = the change instant, plus
   `billingIntervalAnchor`) that Epic 35 Billing consumes. The event name follows the
   `AGGREGATE.ACTION.STATUS` convention.

7. Downgrades whose target entitlements are below the tenant's **current** usage are **flagged,
   not blocked** (enforcement is a sibling epic): `AssignAsync` returns a
   `PlanAssignmentResult` whose `Warnings` collection lists each over-limit
   `EntitlementMetricKey` (from 34-1's enum) with current-vs-new values, and tags the
   `TENANT.PLAN.CHANGED` event `entitlementWarnings=true`. This story does NOT compute live usage
   itself — it reads the usage signal from the metering surface (Epic 35 / Epic 20) behind an
   `ITenantUsageReader` seam, and degrades to "no warnings" if usage data is unavailable.

8. Cancellation (`CancelAsync(Guid tenantId, CancelPlanOptions opts, ct)`) does NOT immediately
   drop the tenant: it stamps `EffectiveTo` on the current `active` assignment at the **period
   end** (default = current billing-interval boundary; `opts.Immediate` allows now) and inserts a
   `scheduled` `TenantPlanAssignment` row pointing at `plan_free` with `EffectiveFrom` = that
   boundary. A `TENANT.PLAN.CANCELLED` event is emitted with the scheduled `effectiveAt`.

9. A boundary-activation path promotes a `scheduled` assignment to `active` once its `EffectiveFrom`
   is reached: the existing `PlatformTaskQueueProcessor` enqueues an ` activate-scheduled-plan`
   task at the boundary (mirroring the `MoveTenant` 202+poll pattern in `AdminTenantsEndpoints`),
   which calls `IPlanAssignmentService.ActivateScheduledAsync`. Activation flips the expiring
   `active` row to `cancelled`, the `scheduled` row to `active`, updates the tenant columns, and
   emits `TENANT.PLAN.CHANGED` with `source=scheduled-activation`.

10. Admin self-service: `PATCH /api/admin/tenants/{id}/plan` is refactored to delegate to
    `IPlanAssignmentService.AssignAsync` (replacing the inline plan-flip + `PLAN.UPDATED` emit in
    `AdminTenantsEndpoints.UpdateTenantPlan`), and a new
    `PUT /api/admin/tenants/{id}/plan` (idempotent assign with version + reason body) is gated by
    `PlatformOwnerAccess`. The legacy `PLAN.UPDATED` platform event is superseded by
    `TENANT.PLAN.CHANGED` (kept as an alias tag for one release for dashboard back-compat).

11. Tenant self-service: a new `POST /api/pricing/subscribe` (in `PricingEndpoints.cs`, the file
    34-2 introduces) lets a tenant pick a **public** (`IsCustom == false`, `Status == active`)
    plan for their own tenant; it resolves the caller's tenant via `ITenantContext` and is gated by
    `SettingsManage` (tenant_owner). A `member`-role caller is rejected `403` via the
    `RequireTenantAdmin`/membership-filter pattern used by `AlertEndpoints`. Subscribing to a
    custom or draft/deprecated plan, or to a plan bound to another tenant, returns `422`/`403`.

12. Per-mode + per-tenant ownership is honored end-to-end: in **single-user** mode the sole user
    owns assignment (no RBAC beyond authentication; `AssignedByUserId` = the user); in **SaaS**
    mode platform owners assign any tenant via the admin route while `tenant_owner` self-subscribes
    via `/api/pricing/subscribe` and `member` is read-only (403 on subscribe). Mode is read from
    `ITammaModeProvider`.

13. Version pinning survives plan deprecation: assigning version N then deprecating the plan
    (creating version N+1 and flipping N → `deprecated`, per 34-1/34-2) leaves the tenant's
    assignment on `PlanVersion = N` with no re-price; reading the tenant's effective plan resolves
    the pinned `(PlanId, PlanVersion)` snapshot, never the latest version.

14. Unit + integration tests cover: version pinning survives deprecation, the one-active-assignment
    invariant under concurrency, custom-plan misassignment rejection, draft/deprecated guard,
    upgrade/downgrade `direction` classification, downgrade entitlement-warning surfacing,
    cancel→`plan_free` scheduling and boundary activation, DCB `TENANT.PLAN.CHANGED` /
    `TENANT.PLAN.CANCELLED` emission, RBAC (member 403 on subscribe, cross-tenant 404), and
    tenant-isolation (a tenant can never assign/subscribe another tenant's plan).

## Technical Design

### Namespace / File Structure

```
apps/tamma-elsa/src/
  Tamma.Data/
    Entities/
      TenantPlanAssignment.cs            # NEW — assignment row
      Tenant.cs                          # MODIFY — doc note: Plan/PlanId derived from active assignment
    ControlPlaneDbContext.cs             # MODIFY — DbSet<TenantPlanAssignment>
    TammaModelConfiguration.cs           # MODIFY — entity config, partial unique index, CHECKs, FKs
    Migrations/ControlPlane/
      <ts>_AddTenantPlanAssignment.cs    # NEW — additive table + back-fill
  Tamma.Core/
    Enums/
      PlanAssignmentStatus.cs            # NEW — active|scheduled|cancelled (string-backed constants)
      PlanChangeDirection.cs             # NEW — upgrade|downgrade|lateral
  Tamma.Api/
    Services/Pricing/
      IPlanAssignmentService.cs          # NEW — assign / cancel / activate / read-effective
      PlanAssignmentService.cs           # NEW — core logic (txn, pin, guards, events)
      PlanAssignmentModels.cs            # NEW — AssignPlanOptions, CancelPlanOptions,
                                         #       PlanAssignmentResult, EntitlementWarning
      ITenantUsageReader.cs              # NEW — usage seam (downgrade-warning input; Epic 35 impl)
      PlanAssignmentEventTypes.cs        # NEW — TENANT.PLAN.CHANGED / .CANCELLED constants
    Services/Provisioning/
      ActivateScheduledPlanTaskPayload.cs # NEW — platform-queue payload for boundary activation
    Endpoints/
      Admin/AdminTenantsEndpoints.cs     # MODIFY — UpdateTenantPlan delegates; add PutTenantPlan
      PricingEndpoints.cs                # MODIFY (NEW in 34-2) — add POST /api/pricing/subscribe
    Extensions/
      PricingServiceCollectionExtensions.cs # NEW (or extend 34-1's) — register the services
    Program.cs                           # MODIFY — map PUT plan route + subscribe route; DI
```

> **Boundary note (Epic 34 ↔ siblings):** this story owns *assignment + lifecycle only*. The
> versioned `Plan`/`PlanEntitlement`/`PlanPrice` catalog and `IPlanCatalogService` belong to
> **34-1**; the admin catalog CRUD, `PricingEndpoints.cs` file, and custom-plan minting/binding
> belong to **34-2** — this story *consumes* them (resolve version, read `IsCustom`, list public
> plans) and *adds* the subscribe handler to the existing file. Quota **enforcement** and live
> **usage metering** belong to Epic 35 / Epic 20 — this story only *reads behind a seam* and
> *flags*, never blocks or charges. Proration/Billing math belongs to **Epic 35** — this story
> only *emits the boundary marker*.

### Key Entity (`TenantPlanAssignment.cs`)

```csharp
namespace Tamma.Data.Entities;

/// <summary>
/// Audited, versioned record of which plan a tenant is on. Replaces the loose
/// Tenant.Plan string as the source of truth (Story 34-4). At most one row per
/// tenant has Status='active' (partial unique index). PlanVersion is a pinned
/// copy of Plan.Version (34-1) so a later plan deprecation never re-prices an
/// existing tenant.
/// </summary>
public class TenantPlanAssignment
{
    public Guid Id { get; set; }                 // UUIDv7
    public Guid TenantId { get; set; }           // FK tenants.Id
    public Guid PlanId { get; set; }             // FK plans.Id (versioned row, 34-1)
    public int PlanVersion { get; set; }         // pinned copy of Plan.Version
    public string Status { get; set; } = "active"; // active|scheduled|cancelled
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public Guid? AssignedByUserId { get; set; }  // actor; null = system/scheduler
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

### EF Model Config (sketch, in `TammaModelConfiguration.cs`)

```csharp
modelBuilder.Entity<TenantPlanAssignment>(entity =>
{
    entity.ToTable("tenant_plan_assignments", t =>
    {
        t.HasCheckConstraint("ck_tpa_status",
            "\"Status\" IN ('active','scheduled','cancelled')");
        t.HasCheckConstraint("ck_tpa_effective_window",
            "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" >= \"EffectiveFrom\"");
        t.HasCheckConstraint("ck_tpa_version_positive", "\"PlanVersion\" >= 1");
    });
    entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    entity.Property(e => e.Status).HasMaxLength(16);

    // AC2 — at most one active assignment per tenant.
    entity.HasIndex(e => e.TenantId)
        .IsUnique()
        .HasFilter("\"Status\" = 'active'")
        .HasDatabaseName("ux_tpa_one_active_per_tenant");
    entity.HasIndex(e => new { e.TenantId, e.Status });
    entity.HasIndex(e => e.PlanId);

    entity.HasOne<Tenant>().WithMany()
        .HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Cascade);
    entity.HasOne<Plan>().WithMany()
        .HasForeignKey(e => e.PlanId).OnDelete(DeleteBehavior.Restrict);
});
```

### EF Migration Sketch (additive table + back-fill)

```csharp
// Up()
migrationBuilder.CreateTable(name: "tenant_plan_assignments", columns: …);
migrationBuilder.CreateIndex(
    name: "ux_tpa_one_active_per_tenant",
    table: "tenant_plan_assignments",
    column: "TenantId", unique: true, filter: "\"Status\" = 'active'");

// Back-fill one active assignment per existing tenant (AC3). Raw SQL so the
// pinned PlanVersion is read from the plans row at migration time. Tenants
// with a NULL shadow PlanId fall back to the plan whose Slug matches Tenant.Plan;
// remaining NULLs fall back to plan_free.
migrationBuilder.Sql(@"
  INSERT INTO tenant_plan_assignments
    (""Id"",""TenantId"",""PlanId"",""PlanVersion"",""Status"",
     ""EffectiveFrom"",""AssignedByUserId"",""Reason"",""CreatedAt"",""UpdatedAt"")
  SELECT gen_random_uuid(), t.""Id"",
         COALESCE(t.""PlanId"", p.""Id"", f.""Id""),
         COALESCE(p.""Version"", f.""Version"", 1),
         'active', t.""CreatedAt"", NULL, 'backfill: 34-4 migration',
         now(), now()
  FROM tenants t
  LEFT JOIN plans p ON p.""Id"" = t.""PlanId""
  LEFT JOIN plans f ON f.""Slug"" = 'free' AND f.""Status"" = 'active'
  WHERE t.""DeletedAt"" IS NULL;");
```

> `has-pending-model-changes` must report **none** after the migration; the table is additive (no
> CHECK edits on existing tables, so Phase-0 collapsed-baseline rules do not apply).

### Service Interface (`IPlanAssignmentService.cs`)

```csharp
public interface IPlanAssignmentService
{
    /// Pin tenant to a plan version. Guards draft/deprecated + custom binding;
    /// classifies direction; flags (never blocks) over-limit downgrades.
    Task<PlanAssignmentResult> AssignAsync(
        Guid tenantId, Guid planId, AssignPlanOptions opts, CancellationToken ct);

    /// Schedule cancel → plan_free at period end (or immediate).
    Task<PlanAssignmentResult> CancelAsync(
        Guid tenantId, CancelPlanOptions opts, CancellationToken ct);

    /// Promote a due `scheduled` row to `active` (called by the platform-queue task).
    Task<PlanAssignmentResult> ActivateScheduledAsync(
        Guid tenantId, Guid assignmentId, CancellationToken ct);

    /// Resolve the tenant's current effective (PlanId, PlanVersion) snapshot.
    Task<TenantPlanAssignment?> GetActiveAsync(Guid tenantId, CancellationToken ct);
}

public sealed record AssignPlanOptions(
    Guid? ActorUserId, string? Reason, bool Force = false);

public sealed record CancelPlanOptions(
    Guid? ActorUserId, string? Reason, bool Immediate = false);

public sealed record PlanAssignmentResult(
    TenantPlanAssignment Assignment,
    PlanChangeDirection Direction,
    IReadOnlyList<EntitlementWarning> Warnings,
    DateTime? ScheduledEffectiveAt);

public sealed record EntitlementWarning(
    string MetricKey, long? CurrentUsage, long? NewLimit);  // MetricKey ⊂ EntitlementMetricKey (34-1)
```

### DCB Event Names (`AGGREGATE.ACTION.STATUS`)

- `TENANT.PLAN.CHANGED` — emitted on assign + scheduled-activation.
  Tags: `tenantId`, `oldPlanId`, `oldPlanVersion`, `newPlanId`, `newPlanVersion`, `direction`,
  `mode`, `actorUserId`, `source` (`admin|self-service|scheduled-activation`),
  `entitlementWarnings` (`true|false`). Data also carries `prorationBoundaryAt`,
  `billingIntervalAnchor`, and the full `warnings` array for Billing/audit.
- `TENANT.PLAN.CANCELLED` — emitted on cancel scheduling.
  Tags: `tenantId`, `currentPlanId`, `effectiveAt`, `mode`, `actorUserId`, `immediate`.

Tenant-scope events append via `IEventRepository.AppendAsync(DomainEvent)`; the platform audit
mirror uses `IPlatformEventPublisher.AppendAndPublishAsync(PlatformEvent)` with actor tags built
the same way `AdminTenantsEndpoints.BuildAdminEvent` does. (System-scope mirror keeps the
evaluator-visible audit on the CP store, consistent with the Story-28-1 event-topology note.)

### API Shape

```
# Admin (platform owner) — PlatformOwnerAccess
PATCH /api/admin/tenants/{tenantId}/plan      # refactored: delegates to AssignAsync
PUT   /api/admin/tenants/{tenantId}/plan      # idempotent assign; body { planId, reason?, force? }
  → 200 { tenantId, planId, planVersion, status, direction, warnings[] }
  → 409 illegal_transition | concurrent_assign
  → 422 plan_draft | plan_deprecated_no_force | custom_plan_misbound
POST  /api/admin/tenants/{tenantId}/plan/cancel  # body { reason?, immediate? } → 200 (scheduled)

# Tenant self-service — SettingsManage (tenant_owner); member → 403
POST  /api/pricing/subscribe                  # body { planSlug } ; tenant from ITenantContext
  → 200 { planId, planVersion, status, direction, warnings[] }
  → 403 member_role | cross_tenant
  → 422 plan_not_public | plan_draft_or_deprecated
```

### Per-Mode + Per-Tenant Handling

| Concern | single-user mode | SaaS mode |
|---|---|---|
| Who may assign | the sole user (authenticated) | platform owner (admin route) |
| Who may self-subscribe | the sole user via `/api/pricing/subscribe` | `tenant_owner` (`SettingsManage`); `member` → 403 |
| `AssignedByUserId` | the user | the platform owner / tenant owner |
| Tenant resolution | `ITenantContext.TenantId` (the lone tenant) | `ITenantContext.TenantId` (caller's tenant); admin route takes `{tenantId}` route param |
| Cross-tenant guard | n/a (one tenant) | subscribe ignores any body tenant id and uses `ITenantContext`; admin route 404s unknown tenant |
| Mode source | `ITammaModeProvider.Mode` | same |

### Integration Points

- **34-1 `IPlanCatalogService`** — resolve `(planId → PlanSnapshot{Version, Status, IsCustom,
  BoundTenantId, Entitlements})` for pinning + guards. `EntitlementMetricKey` enum from
  `Tamma.Core/Enums` is reused verbatim in `EntitlementWarning.MetricKey`.
- **34-2 `PricingEndpoints.cs` / `AdminPricingEndpoints`** — public-plan list reuse for subscribe
  validation; custom-plan binding flag is read, not written, here.
- **`AdminTenantsEndpoints`** — `UpdateTenantPlan` is refactored to delegate; `BuildAdminEvent`
  actor-extraction pattern is reused for the platform mirror; the `MoveTenant` 202+poll pattern is
  mirrored for boundary activation.
- **`PlatformTaskQueueProcessor` / `IPlatformQueuedTaskRepository`** — boundary activation task is
  enqueued/claimed exactly like `MoveTenantTaskPayload`.
- **`IEventRepository` / `IPlatformEventPublisher`** — DCB + platform-audit emission.
- **Epic 35 / Epic 20 `ITenantUsageReader`** — downgrade-warning input (this story ships the
  interface + a null/no-op default; Epic 35 supplies the real reader).

## Dependencies

**Internal — prerequisite:**
- Story 34-1 (Plan & Price-Book Catalog) — versioned `Plan` (Version/Status/IsCustom),
  `PlanEntitlement`, `EntitlementMetricKey` enum, `IPlanCatalogService`/`PlanSnapshot`.
- Story 34-2 (Plan Catalog Admin API & Custom Plans) — `PricingEndpoints.cs` file, public-catalog
  reads, custom-plan binding semantics.
- Epic 28 (control-plane tenancy) — `Tenant` shadow `PlanId`/`Status`, `ControlPlaneDbContext`,
  `PlatformQueuedTask`/`PlatformTaskQueueProcessor`, `IPlatformEventPublisher`.
- Epic 4 (DCB events) — `DomainEvent`/`IEventRepository` append path.

**Internal — blocks:**
- Epic 35 (Billing) — consumes `TENANT.PLAN.CHANGED` proration boundary + `ScheduledEffectiveAt`.
- Epic 34 Enforcement story — consumes the active `(PlanId, PlanVersion)` snapshot for quota gating.

**External:**
- None in this story. The `ITenantUsageReader` seam keeps Stripe/metering (Epic 35) out of the
  assignment path; tests mock it.

## Testing Strategy

**Unit (xUnit, `tests/Tamma.Api.Tests/Pricing/`):**
1. `AssignAsync` pins `PlanVersion` from the catalog snapshot; reads of the active assignment after
   a later deprecation still report the pinned version (AC13).
2. One-active invariant: assigning twice flips prior → `cancelled` and inserts new `active`; never
   two `active` rows; concurrent assign → unique-violation translated to 409 (mock the unique
   index via the in-memory provider plus a forced `DbUpdateException` path).
3. Custom-plan binding: assigning an `IsCustom` plan to a non-bound tenant throws
   `PLAN.ASSIGN.CUSTOM_PLAN_MISBOUND`; to the bound tenant succeeds.
4. Draft guard (always rejected) + deprecated guard (rejected unless `Force`).
5. Direction classification (upgrade/downgrade/lateral) from `MonthlyPriceUsd`/`PlanPrice`
   ordering; downgrade with over-limit usage (mock `ITenantUsageReader`) surfaces
   `EntitlementWarning`s and sets `entitlementWarnings=true`; usage unavailable → no warnings,
   no throw.
6. `CancelAsync` stamps `EffectiveTo` at period end and inserts a `scheduled` `plan_free` row;
   `Immediate` cancels now; emits `TENANT.PLAN.CANCELLED`.
7. `ActivateScheduledAsync` promotes a due `scheduled` row, flips the expiring `active`, emits
   `TENANT.PLAN.CHANGED` with `source=scheduled-activation`.
8. Event-shape tests: `TENANT.PLAN.CHANGED` carries all required tags + the proration marker.

**Integration (xUnit + Postgres via `sg docker -c "dotnet test …"`):**
9. Migration applies + rolls back cleanly; back-fill produces exactly one `active` assignment per
   existing tenant; partial unique index rejects a second `active` insert at the DB level.
10. `PATCH`/`PUT /api/admin/tenants/{id}/plan` round-trip (PlatformOwnerAccess); a non-platform
    JWT → 403; unknown tenant → 404.
11. `POST /api/pricing/subscribe`: tenant_owner subscribes to a public plan (200); `member` → 403;
    custom/draft/deprecated → 422; **tenant isolation** — caller A cannot subscribe/affect
    tenant B (body tenant id is ignored; route is tenant-from-context).
12. Cancel → boundary activation end-to-end via the platform queue: enqueue, run the processor,
    assert the `scheduled` `plan_free` row becomes `active` and the tenant columns flip.

**Mocks:** `ITenantUsageReader` (no Stripe in this story), `IPlanCatalogService` (34-1),
`IEventRepository`/`IPlatformEventPublisher` (assert emission), `TimeProvider` (deterministic
period-end boundaries).

## Estimated Effort

4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/TenantPlanAssignment.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs` | Modify (doc note: Plan/PlanId derived) |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (add DbSet) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config, partial unique index, CHECKs, FKs) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddTenantPlanAssignment.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Enums/PlanAssignmentStatus.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Enums/PlanChangeDirection.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/IPlanAssignmentService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PlanAssignmentService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PlanAssignmentModels.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/ITenantUsageReader.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PlanAssignmentEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/ActivateScheduledPlanTaskPayload.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs` | Modify (delegate + PUT) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/PricingEndpoints.cs` | Modify (add subscribe; file from 34-2) |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/PricingServiceCollectionExtensions.cs` | Create/Modify |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map PUT plan + subscribe; DI) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PlanAssignmentServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PlanAssignmentEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Data.Tests/Migrations/TenantPlanAssignmentMigrationTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md).
2. Searched `.dev/` for related spikes/bugs/findings/decisions (tenancy, plans, events).
3. Reviewed 34-1 and 34-2 stories — this story is strictly downstream of their catalog/version
   model; do NOT re-implement the catalog here.
4. Confirmed the C# test runner contract: `sg docker -c "dotnet test …"` for docker-bound suites
   (build needs no wrapper). See `reference_dotnet_test_docker`.
5. Planned a TDD (Red-Green-Refactor) approach — tests for each AC before implementation.

### Key Design Decisions

- **Pin the version, don't reference "latest".** `PlanVersion` is a denormalized column on the
  assignment, not a join to the current plan row. This is the whole point of the story: a 34-1/34-2
  deprecation flips the plan row but cannot retro-price an existing tenant.
- **Assignment is the source of truth; `Tenant.Plan`/`PlanId` are a cache.** Keep them in lockstep
  only so existing dashboards (`AdminTenantsEndpoints.ListTenants`, which reads
  `EF.Property<Guid?>(t,"PlanId")`) render the right plan without a rewrite. New reads should call
  `IPlanAssignmentService.GetActiveAsync`.
- **Flag, don't block, downgrades.** Per the scope and the epic boundary, enforcement lives
  elsewhere. `AssignAsync` always succeeds for a valid plan; it merely returns warnings. The
  `ITenantUsageReader` seam keeps usage/Billing out of the assignment transaction.
- **Reuse the platform queue for boundary work.** Boundary activation and period-end cancel mirror
  `MoveTenant`'s enqueue + processor pattern instead of inventing a new scheduler.
- **Supersede, don't shadow-emit.** `TENANT.PLAN.CHANGED` replaces the ad-hoc `PLAN.UPDATED`; keep
  a one-release alias tag so the admin dashboard timeline doesn't blank out mid-migration.

### Edge Cases

- Assigning the tenant's current `(PlanId, PlanVersion)` again = no-op `lateral`; return 200 with
  the existing row, no new event (idempotent PUT).
- Cancel when already on `plan_free` = no-op (already free); return 200, no scheduled row.
- A `scheduled` row whose boundary passed while the host was down → caught by the activation task
  on next processor tick (the task is idempotent by `assignmentId`).
- Concurrent admin + self-service assign → partial unique index makes one lose with a translated
  409; never two `active` rows.

## Logging Requirements

- **INFO**: plan assigned (tenantId, oldPlan→newPlan, version, direction, source), plan cancel
  scheduled (effectiveAt), scheduled activation completed.
- **DEBUG**: catalog snapshot resolved, direction classification inputs, usage-reader lookup
  result, transaction begin/commit.
- **WARN**: downgrade with entitlement over-limit (metricKey, current, newLimit),
  deprecated-plan assign with `Force=true`, usage reader unavailable (degraded to no warnings),
  concurrent-assign unique violation retried/409.
- **ERROR**: assignment transaction rollback, custom-plan misbinding attempt, FK/CHECK violation,
  platform-queue enqueue failure for boundary activation.
- **Structured context**: include `{ tenantId, planId, planVersion, direction, mode, actorUserId,
  source }` where applicable.
- **Credential / PII safety**: never log connection strings or actor PII beyond the user GUID +
  (already-logged) email captured by the platform-event actor breadcrumb.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
