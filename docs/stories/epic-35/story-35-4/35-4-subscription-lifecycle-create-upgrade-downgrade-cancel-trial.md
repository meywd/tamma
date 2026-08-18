# Story 35-4: Subscription Lifecycle — Create, Upgrade/Downgrade, Cancel, Trial & Proration

Status: done
<!-- Flipped drafted -> done 2026-08-18. The deliverable named in the acceptance criteria
     was located in apps/tamma-elsa/src (and its suites in apps/tamma-elsa/tests) before this
     header was changed — not taken from a changelog. The per-story evidence is recorded
     inline on this story's line in docs/sprint-status.yaml.
-->

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), `.dev/` knowledge-base usage (spikes, bugs, findings, decisions), TRACE/DEBUG logging requirements, the test-first (TDD) mandate, and the build-success / coverage gates.

## User Story

As a **tenant owner/admin** of a Tamma SaaS organization,
I want to subscribe to a plan via Stripe Checkout, upgrade or downgrade with correct proration, start and convert trials, change seat counts, and cancel (now or at period end) — with every change mirrored on the control plane,
So that my billing reflects what I actually use, my plan/quota state never drifts from Stripe, and quota enforcement (Story 35-6) reads one authoritative subscription record.

## Priority

P0 - Required for monetization. Without a managed subscription lifecycle there is no recurring revenue, no plan changes, and no single source of truth for the quota enforcement in Story 35-6. Re-targets TypeScript Epic 20-2 to the current C# control plane.

## Acceptance Criteria

1. A new control-plane entity `BillingSubscription` is added at `apps/tamma-elsa/src/Tamma.Data/Entities/BillingSubscription.cs` with: `Id` (Guid PK), `TenantId` (Guid FK to `tenants.Id`), `StripeSubscriptionId` (string, nullable until Stripe acks), `PlanSlug` (string — `free`/`team`/`enterprise`), `Status` (string text domain — `trialing`/`active`/`past_due`/`canceled`/`incomplete`/`incomplete_expired`/`unpaid`), `CurrentPeriodStart`/`CurrentPeriodEnd` (DateTime), `CancelAtPeriodEnd` (bool), `TrialEnd` (DateTime?), `Seats` (int, default 1), `ScheduledPlanSlug` (string?, set for a pending downgrade), `ScheduledEffectiveAt` (DateTime?), `StripeScheduleId` (string?), `CreatedAt`/`UpdatedAt`. Registered as `DbSet<BillingSubscription> BillingSubscriptions` on `ControlPlaneDbContext` and configured in `TammaModelConfiguration.ConfigureControlPlaneEntities` (table `billing_subscriptions`, FK to `tenants` with `OnDelete(Cascade)`, CHECK on `Status` text domain, **partial unique index** enforcing at most one *non-terminal* subscription per tenant: `UNIQUE (TenantId) WHERE Status NOT IN ('canceled','incomplete_expired')`).

2. `POST /api/v1/billing/subscription/checkout` (tenant-scoped, `MemberAccess` group + `RequireTenantMembershipFilter`) accepts `{ planSlug, seats?, trialDays? }`, resolves the tenant's `BillingCustomer` + the `BillingPlanPrice` for the slug (both from Story 35-1), and returns a Stripe Checkout Session URL (`mode: "subscription"`). The caller must be `tenant_owner` or `tenant_admin` (`TenantRoleHierarchy.IsAtLeast(role, Admin)`); a `member`-role caller receives **403** before any Stripe call. No local `BillingSubscription` row is created here — the subscription is materialized when the `customer.subscription.created` webhook arrives (Story 35-5).

3. `POST /api/v1/billing/subscription/change` with `{ planSlug }` performs **upgrade with immediate proration** (Stripe `SubscriptionService.UpdateAsync` with `ProrationBehavior = "create_prorations"`) when the new plan's `MonthlyPriceUsd` ≥ the current plan's, and **schedules a downgrade at period end** (Stripe Subscription Schedule via `SubscriptionScheduleService`) when it is lower. A downgrade does **not** change the active `PlanSlug` immediately; it records `ScheduledPlanSlug` + `ScheduledEffectiveAt` (= `CurrentPeriodEnd`) + `StripeScheduleId` on the local mirror and leaves `PlanSlug`/quota at the current (higher) plan until the period rolls over.

4. `POST /api/v1/billing/subscription/cancel` with `{ atPeriodEnd: bool }` supports both **at-period-end** (`CancelAtPeriodEnd = true` via `SubscriptionService.UpdateAsync(CancelAtPeriodEnd = true)` — keeps `Status = active` until the period ends) and **immediate** (`SubscriptionService.CancelAsync` — flips `Status = canceled` and recomputes quota to free now). The local mirror is updated to match in the same control-plane transaction as the Stripe call's confirmed result.

5. Trial handling: a checkout with `trialDays` starts a trial subscription (`Status = trialing`, `TrialEnd` populated). On trial conversion the subscription transitions `trialing → active`; on trial expiry without a payment method it transitions to `canceled`/`unpaid`. When the trial ends, the service emits `BILLING.SUBSCRIPTION.TRIAL_ENDED` (tags `{ tenantId, planSlug, status }`). (The `customer.subscription.trial_will_end` and terminal transitions arrive via the Story 35-5 webhook; this story owns the *mirror update + event emission* helper the webhook processor calls.)

6. `POST /api/v1/billing/subscription/seats` with `{ seats }` updates the Stripe subscription's seat quantity on the `tamma.seats` meter/price (from Story 35-1) and updates `BillingSubscription.Seats` in lockstep; the quota snapshot is recomputed so seat-cap enforcement (Story 35-6) sees the new count immediately. Decreasing seats below current active membership count is rejected with **409** and a stable error code (`seats_below_active_members`) — no Stripe call is made.

7. `Tenant.Plan` (the legacy string column) **and** the shadow `Tenant.PlanId` (Guid? FK to `plans.Id`) are updated atomically with `BillingSubscription` whenever the **effective** plan changes (upgrade applied, downgrade rolled over, cancellation to free, trial conversion), mirroring the lockstep already done in `AdminTenantsEndpoints.UpdateTenantPlan` (`AdminTenantsEndpoints.cs:620-623`). After any lifecycle transition there is **no drift**: `Tenant.Plan == BillingSubscription.PlanSlug` for the active plan, asserted by an invariant test.

8. DCB events are emitted via `IEventRepository.AppendAsync` (tenant-scoped → tenant `DomainEvents` store): `BILLING.SUBSCRIPTION.CREATED` (on first materialization), `BILLING.SUBSCRIPTION.UPDATED` (upgrade applied, downgrade scheduled, seat change, plan rollover), `BILLING.SUBSCRIPTION.CANCELED` (immediate or at-period-end recorded), and `BILLING.SUBSCRIPTION.TRIAL_ENDED`. All carry tags `{ tenantId, planSlug, status }` (the event-type names follow the `AGGREGATE.ACTION.STATUS` convention).

9. `GET /api/v1/billing/subscription` (tenant-scoped, any tenant member) returns the current `BillingSubscription` projection for the route tenant: `{ planSlug, status, currentPeriodStart, currentPeriodEnd, cancelAtPeriodEnd, trialEnd, seats, scheduledPlanSlug, scheduledEffectiveAt }`. A tenant with no subscription returns the free-tier default (`status: "active"`, `planSlug: "free"`, `seats: 1`). The row is **never** returned for a tenant the caller is not a member of (`RequireTenantMembershipFilter` + tenant-scoped query).

10. All mutating endpoints are **idempotent against Stripe**: every Stripe mutating call passes a deterministic `RequestOptions.IdempotencyKey` (e.g. `sub-change-{tenantId}-{planSlug}-{periodEnd:yyyyMMdd}`) so a retried request never double-applies proration or mints a duplicate schedule.

11. Single-user mode (`ITammaModeProvider.Mode == TammaMode.SingleUser`): the `/api/v1/billing/subscription/*` endpoints are **not mapped** (or short-circuit with a clear "billing is SaaS-only" 404/501), and `SubscriptionService` resolves the `NullBillingProvider` (from Story 35-1, `IsEnabled = false`) so zero Stripe calls are made. No `BillingSubscription` rows exist in single-user mode.

12. Tenant isolation: every lifecycle operation resolves the tenant from the route (`/api/v1/orgs/{tenantId}` membership filter) or the caller's active tenant, and every `BillingSubscription` query is filtered by `TenantId`; a partial-unique index guarantees at most one non-terminal subscription per tenant. A cross-tenant subscription read or mutation returns 404/403 — proven by a tenant-isolation integration test.

13. Concurrency / out-of-order safety: a webhook-driven mirror update (Story 35-5) and an API-driven update can race. `BillingSubscription` updates apply Stripe's returned object as the source of truth and use the Stripe-supplied `current_period_*`/`status` so the last write that reflects Stripe's confirmed state wins; the service never blindly overwrites a newer Stripe state with a stale one (period/status are taken from the Stripe response, not the request).

14. Unit + integration tests (xUnit, `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/`) cover: checkout-session creation (Stripe mocked), upgrade-with-proration, scheduled-downgrade (schedule created, mirror reflects `scheduledPlanSlug`/`scheduledEffectiveAt`), immediate vs at-period-end cancel, trial start + conversion + expiry event emission, seat increase/decrease (incl. the `seats_below_active_members` 409), RBAC (member → 403 on mutations), `Tenant.Plan`/`PlanId` lockstep (no-drift invariant), DCB event emission for each transition, tenant isolation, and the single-user no-op seam.

15. Logging follows the project standard: INFO on each confirmed lifecycle transition (`tenantId`, `planSlug`, `status`, `seats`), WARN on a Stripe call that fails and is surfaced as a 502/retry, ERROR on a mirror/Stripe divergence detected during reconciliation; **the Stripe secret key and any customer payment details are NEVER logged**.

## Technical Design

### Namespace / file structure

```
apps/tamma-elsa/src/Tamma.Data/
  Entities/
    BillingSubscription.cs           # NEW — control-plane mirror (one non-terminal per tenant)
  ControlPlaneDbContext.cs           # MODIFY — add DbSet<BillingSubscription>
  TammaModelConfiguration.cs         # MODIFY — table/index/CHECK/FK config
  Migrations/ControlPlane/
    <ts>_AddBillingSubscription.cs   # NEW (+ .Designer.cs + snapshot update)
  Repositories/
    IBillingSubscriptionRepository.cs  # NEW — tenant-scoped CRUD + GetActiveByTenant
    BillingSubscriptionRepository.cs   # NEW

apps/tamma-elsa/src/Tamma.Api/
  Services/Billing/
    ISubscriptionService.cs          # NEW — lifecycle seam (checkout/change/cancel/seats/trial)
    SubscriptionService.cs           # NEW — orchestrates Stripe (via IBillingProvider) + mirror + events
    SubscriptionProjection.cs        # NEW — read DTO (GET response + free-tier default)
    SubscriptionMirrorUpdater.cs     # NEW — applies a Stripe Subscription object onto the mirror
                                     #        (called by both the API and the 35-5 webhook processor)
    BillingEvents.cs                 # MODIFY (35-1 created it) — add SUBSCRIPTION.* builders
  Endpoints/Billing/
    SubscriptionEndpoints.cs         # NEW — checkout/change/cancel/seats + GET; tenant-scoped
  Extensions/
    BillingServiceCollectionExtensions.cs  # MODIFY (35-1 created it) — register subscription svc/repo
  Program.cs                         # MODIFY — map SubscriptionEndpoints (SaaS only)
```

> **Why `SubscriptionService` lives in `Tamma.Api/Services/Billing/` and not `Tamma.Activities`.** Subscription lifecycle is a control-plane HTTP concern (Checkout, plan changes, RBAC), not an Elsa workflow activity. The engine never changes a subscription; it only *reads* quota (Story 35-6 via `QuotaService`). Keeping the service in `Tamma.Api` matches the 35-1 `StripeBillingProvider` placement.

### Key entity signature

```csharp
// Tamma.Data/Entities/BillingSubscription.cs
namespace Tamma.Data.Entities;

public class BillingSubscription
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }                  // FK -> tenants.Id
    public string? StripeSubscriptionId { get; set; }   // null until Stripe acks
    public string PlanSlug { get; set; } = "free";      // current EFFECTIVE plan
    public string Status { get; set; } = "active";      // text domain; CHECK-constrained
    public DateTime CurrentPeriodStart { get; set; }
    public DateTime CurrentPeriodEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public DateTime? TrialEnd { get; set; }
    public int Seats { get; set; } = 1;

    // Pending downgrade (scheduled at period end via a Stripe Subscription Schedule)
    public string? ScheduledPlanSlug { get; set; }
    public DateTime? ScheduledEffectiveAt { get; set; }
    public string? StripeScheduleId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Tenant? Tenant { get; set; }
}
```

### EF model configuration sketch (`TammaModelConfiguration.ConfigureControlPlaneEntities`)

```csharp
modelBuilder.Entity<BillingSubscription>(entity =>
{
    entity.ToTable("billing_subscriptions", t =>
        t.HasCheckConstraint("ck_billing_subscriptions_status",
            "\"Status\" IN ('trialing','active','past_due','canceled'," +
            "'incomplete','incomplete_expired','unpaid')"));
    entity.HasKey(e => e.Id);
    // At most ONE non-terminal subscription per tenant.
    entity.HasIndex(e => e.TenantId)
        .IsUnique()
        .HasFilter("\"Status\" NOT IN ('canceled','incomplete_expired')");
    entity.HasIndex(e => e.StripeSubscriptionId).IsUnique()
        .HasFilter("\"StripeSubscriptionId\" IS NOT NULL");
    entity.HasOne(e => e.Tenant).WithMany()
        .HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Cascade);
});
```

### EF migration sketch

`dotnet ef migrations add AddBillingSubscription --context ControlPlaneDbContext --output-dir Migrations/ControlPlane` produces an **additive** migration (new table — not a baseline CHECK edit):

```csharp
migrationBuilder.CreateTable(name: "billing_subscriptions", columns: table => new {
    Id = table.Column<Guid>(nullable: false),
    TenantId = table.Column<Guid>(nullable: false),
    StripeSubscriptionId = table.Column<string>(nullable: true),
    PlanSlug = table.Column<string>(nullable: false, defaultValue: "free"),
    Status = table.Column<string>(nullable: false, defaultValue: "active"),
    CurrentPeriodStart = table.Column<DateTime>(nullable: false),
    CurrentPeriodEnd = table.Column<DateTime>(nullable: false),
    CancelAtPeriodEnd = table.Column<bool>(nullable: false, defaultValue: false),
    TrialEnd = table.Column<DateTime>(nullable: true),
    Seats = table.Column<int>(nullable: false, defaultValue: 1),
    ScheduledPlanSlug = table.Column<string>(nullable: true),
    ScheduledEffectiveAt = table.Column<DateTime>(nullable: true),
    StripeScheduleId = table.Column<string>(nullable: true),
    CreatedAt = table.Column<DateTime>(nullable: false),
    UpdatedAt = table.Column<DateTime>(nullable: false),
}, constraints: table => {
    table.PrimaryKey("PK_billing_subscriptions", x => x.Id);
    table.CheckConstraint("ck_billing_subscriptions_status",
        "\"Status\" IN ('trialing','active','past_due','canceled','incomplete','incomplete_expired','unpaid')");
    table.ForeignKey("FK_billing_subscriptions_tenants_TenantId",
        x => x.TenantId, "tenants", "Id", onDelete: ReferentialAction.Cascade);
});
// + partial unique IX on TenantId (filter Status NOT IN terminal) and partial unique IX on StripeSubscriptionId
```
Verify `dotnet ef migrations has-pending-model-changes` reports none afterwards; `Update` then down rolls back cleanly.

### Service seam

```csharp
// Services/Billing/ISubscriptionService.cs
public interface ISubscriptionService
{
    Task<CheckoutResult> CreateCheckoutSessionAsync(
        Guid tenantId, string planSlug, int? seats, int? trialDays, CancellationToken ct = default);

    Task<SubscriptionProjection> ChangePlanAsync(
        Guid tenantId, string newPlanSlug, CancellationToken ct = default);   // upgrade=prorate, downgrade=schedule

    Task<SubscriptionProjection> CancelAsync(
        Guid tenantId, bool atPeriodEnd, CancellationToken ct = default);

    Task<SubscriptionProjection> ChangeSeatsAsync(
        Guid tenantId, int seats, CancellationToken ct = default);            // 409 if < active members

    Task<SubscriptionProjection> GetAsync(Guid tenantId, CancellationToken ct = default); // free default if none
}

public sealed record CheckoutResult(string CheckoutUrl, string StripeSessionId);
```

`SubscriptionService` depends on: `IBillingProvider` (35-1, gives the `Stripe.StripeClient` / `IsEnabled`), `IBillingCatalog` (35-1, resolves `BillingPlanPrice` by slug), `IBillingSubscriptionRepository`, `ITenantRepository` (for the `Tenant.Plan`/`PlanId` lockstep), `IEventRepository` (DCB events), `ITenantMembershipRepository` (active-member count for the seats floor), and `ILogger`. Upgrade vs downgrade is decided by comparing `Plan.MonthlyPriceUsd` of the current vs target slug (read from `ControlPlaneDbContext.Plans`).

### `SubscriptionMirrorUpdater` — shared with Story 35-5

```csharp
// Services/Billing/SubscriptionMirrorUpdater.cs
public sealed class SubscriptionMirrorUpdater
{
    /// Apply a Stripe Subscription object onto the local mirror + Tenant.Plan/PlanId
    /// in one control-plane transaction, emit the right BILLING.SUBSCRIPTION.* event,
    /// and return the projection. Status/period/trialEnd are taken from the Stripe
    /// object (source of truth) — never from the API request (AC13).
    public Task<SubscriptionProjection> ApplyAsync(
        Guid tenantId, Stripe.Subscription stripeSub, string transition, CancellationToken ct);
}
```
Both `SubscriptionService` (after a synchronous Stripe call) and the Story 35-5 `StripeWebhookProcessor` (`customer.subscription.created/updated/deleted`) call `ApplyAsync`, so the mirror logic and the no-drift `Tenant.Plan`/`PlanId` lockstep exist in exactly one place. The 35-5 boundary is the *webhook endpoint, signature verification, and dedup*; the *mirror projection* is owned here so both code paths share it.

### `Tenant.Plan` / `Tenant.PlanId` lockstep (no drift — AC7)

When the **effective** plan changes, mirror both columns exactly as `AdminTenantsEndpoints.UpdateTenantPlan` does:

```csharp
var plan = await db.Plans.AsNoTracking().FirstAsync(p => p.Slug == effectiveSlug, ct);
db.Entry(tenant).Property("PlanId").CurrentValue = plan.Id;   // shadow FK
tenant.Plan = plan.Slug;                                      // legacy string column
tenant.UpdatedAt = DateTime.UtcNow;
```
A **scheduled downgrade** does NOT touch `Tenant.Plan` until the rollover (the `customer.subscription.updated` webhook at period end, applied via `SubscriptionMirrorUpdater`). This keeps the higher plan's quota live until the user has actually paid through the period.

### DCB event names

| Event | When | Tags | `TenantId` |
|---|---|---|---|
| `BILLING.SUBSCRIPTION.CREATED` | First materialization (checkout completed / first webhook) | `{ tenantId, planSlug, status }` | set |
| `BILLING.SUBSCRIPTION.UPDATED` | Upgrade applied, downgrade scheduled, seat change, plan rollover | `{ tenantId, planSlug, status }` (+ `scheduledPlanSlug` on a downgrade) | set |
| `BILLING.SUBSCRIPTION.CANCELED` | Immediate cancel or at-period-end cancel recorded | `{ tenantId, planSlug, status }` | set |
| `BILLING.SUBSCRIPTION.TRIAL_ENDED` | Trial converted or expired | `{ tenantId, planSlug, status }` | set |

These are **tenant-scoped** (`TenantId` set), so `IEventRepository.AppendAsync` routes them to the tenant's own `DomainEvents` store (see `EventRepository.cs:53-86`). The event row shape mirrors `OrgEndpoints.EmitTenantEvent` (`Metadata = {"workflowVersion":"1.0.0","eventSource":"system"}`). A `BillingEvents` static helper (created in 35-1) gains `SubscriptionCreated/Updated/Canceled/TrialEnded` builders.

### API shape

| Endpoint | Method | Auth | Body / Response |
|---|---|---|---|
| `/api/v1/billing/subscription/checkout` | POST | tenant owner/admin (member → 403) | `{ planSlug, seats?, trialDays? }` → `{ checkoutUrl, stripeSessionId }` |
| `/api/v1/billing/subscription/change` | POST | tenant owner/admin | `{ planSlug }` → `SubscriptionProjection` |
| `/api/v1/billing/subscription/cancel` | POST | tenant owner/admin | `{ atPeriodEnd }` → `SubscriptionProjection` |
| `/api/v1/billing/subscription/seats` | POST | tenant owner/admin | `{ seats }` → `SubscriptionProjection` (409 `seats_below_active_members`) |
| `/api/v1/billing/subscription` | GET | any tenant member | → `SubscriptionProjection` (free default if none) |

`SubscriptionEndpoints.MapSubscriptionEndpoints(app)` registers these under the existing tenant-scoped group pattern (`app.MapGroup("/api/v1/orgs/{tenantId}/billing/subscription")` + `RequireTenantMembershipFilter`, *or* a current-active-tenant `/api/v1/billing/subscription` group resolving the caller's active tenant — match whichever the sibling billing endpoints from 35-1/35-5 settle on; default to the `/api/v1/orgs/{tenantId}/...` membership-gated group, consistent with `OrgEndpoints` and `AlertEndpoints` tenant sections). Role is read from `httpContext.Items[RequireTenantMembershipFilter.TenantRoleItemKey]` and gated with `TenantRoleHierarchy.IsAtLeast(role, TenantRoleHierarchy.Admin)` for every mutation.

### Per-mode + per-tenant handling

| Concern | single-user | SaaS |
|---|---|---|
| Endpoints mapped | no (`ITammaModeProvider.Mode == SingleUser` short-circuits / not mapped) | yes (tenant-scoped, membership-gated) |
| Provider | `NullBillingProvider` (`IsEnabled=false`) | `StripeBillingProvider` |
| Principal / owner | n/a (no subscriptions in single-user) | the tenant; `tenant_owner`/`tenant_admin` mutate, `member` read-only (403 on mutate) |
| Mirror rows | none | one non-terminal `BillingSubscription` per tenant (partial-unique) |
| Quota source for 35-6 | n/a | the active `BillingSubscription.PlanSlug` + `Seats` |

## Dependencies

**Internal (prerequisite):**
- **Story 35-1** — `BillingCustomer` mapping, `BillingPlanPrice` catalog (slug → Stripe Product/Price/Meter ids), `IBillingProvider`/`StripeBillingProvider`/`NullBillingProvider`, `IBillingCatalog`, `BillingMode` enum, `BillingServiceCollectionExtensions`, the three meters (`tamma.platform_tokens_input/output`, `tamma.seats`), and the cabinet-resolved Stripe key.
- **Story 35-5** — Stripe webhook ingestion + idempotent dedup. This story owns the `SubscriptionMirrorUpdater` that 35-5's `StripeWebhookProcessor` calls; 35-5 owns signature verification + the `billing_webhook_events` dedup table. (Listed as a dependency because trial-end/rollover/cancel-confirmation transitions are *driven* by webhooks; the API-side calls here update the mirror optimistically and the webhook reconciles to Stripe's confirmed state.)
- **Epic 28** — control plane, `Tenant`/`Plan`/`ControlPlaneDbContext`, `ITenantMembershipRepository`, `TenantRoleHierarchy`, `RequireTenantMembershipFilter`, `ITammaModeProvider`.
- **Epic 4** — DCB events (`DomainEvent`, `IEventRepository.AppendAsync`).

**Internal (blocks):**
- **Story 35-6** — Plan Quota & Usage-Limit Enforcement reads the active `BillingSubscription` (PlanSlug + Seats) as its single source of truth; quota must recompute on every transition here.
- Invoicing / dunning / portal / credits stories (display + act on subscription state).

**External:**
- `Stripe.net` NuGet (added in 35-1). Research the current API surface (`SubscriptionService.UpdateAsync` + `ProrationBehavior`, `SubscriptionScheduleService`, `Checkout.SessionService`, `RequestOptions.IdempotencyKey`) before coding — do not assume method shapes.
- A Stripe account (test + live) with the catalog seeded by 35-1.

## Testing Strategy

**Unit (xUnit, `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/`):**
1. `SubscriptionServiceCheckoutTests` — checkout builds a session with the right price id + seats + trial (Stripe `Checkout.SessionService` mocked); no local row created.
2. `SubscriptionServiceChangeTests` — upgrade calls `UpdateAsync` with `ProrationBehavior=create_prorations` and applies the new slug now; downgrade creates a `SubscriptionSchedule`, records `ScheduledPlanSlug`/`ScheduledEffectiveAt`/`StripeScheduleId`, and leaves `PlanSlug`/`Tenant.Plan` unchanged.
3. `SubscriptionServiceCancelTests` — `atPeriodEnd=true` sets `CancelAtPeriodEnd` + keeps `Status=active`; immediate cancel flips `Status=canceled` and recomputes `Tenant.Plan` to free now.
4. `SubscriptionServiceTrialTests` — checkout with `trialDays` → `Status=trialing` + `TrialEnd`; `SubscriptionMirrorUpdater` conversion/expiry emits `BILLING.SUBSCRIPTION.TRIAL_ENDED`.
5. `SubscriptionServiceSeatsTests` — seat increase updates Stripe quantity + `Seats`; decrease below active membership → 409 `seats_below_active_members`, zero Stripe calls.
6. `SubscriptionMirrorUpdaterTests` — applying a Stripe object updates the mirror **and** `Tenant.Plan`/`PlanId` in lockstep (no-drift invariant); status/period taken from the Stripe object, not the request (AC13).
7. `SubscriptionEndpointsRbacTests` — member-role caller gets 403 on checkout/change/cancel/seats; owner/admin pass; GET allowed for member.
8. `SubscriptionEventEmissionTests` — each transition appends exactly the expected `BILLING.SUBSCRIPTION.*` event with `{tenantId,planSlug,status}` tags.
9. `NullBillingSubscriptionTests` — single-user mode: endpoints unmapped / short-circuit, `SubscriptionService` makes zero Stripe calls.

**Integration (docker-bound via `sg docker -c "dotnet test ..."`):**
10. Migration applies + rolls back on a real Postgres CP DB; `has-pending-model-changes` = none; the partial-unique index rejects a second non-terminal subscription for one tenant.
11. Full lifecycle through the HTTP endpoints (Stripe mocked) on a real CP+tenant DB: checkout → webhook-materialize (via `SubscriptionMirrorUpdater`) → upgrade → seat change → schedule downgrade → cancel; assert one `BillingSubscription` row, the matching `BILLING.SUBSCRIPTION.*` events in the tenant `DomainEvents` store, and `Tenant.Plan == BillingSubscription.PlanSlug` after each step.
12. **Tenant isolation** — two tenants each get a subscription; tenant A's owner cannot read or mutate tenant B's subscription (404/403); a `BillingSubscription` query is always `TenantId`-filtered.

**Mocks:** Stripe SDK mocked at the `SubscriptionService` / `SubscriptionScheduleService` / `Checkout.SessionService` interface boundary (no live Stripe in CI). `IBillingProvider.IsEnabled` toggled per mode. Live-Stripe lifecycle test is opt-in behind `STRIPE_SECRET_KEY_TEST`, excluded from default CI.

## Estimated Effort

5-6 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/BillingSubscription.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (add DbSet) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddBillingSubscription.cs` | Create (+ Designer + snapshot) |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/IBillingSubscriptionRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/BillingSubscriptionRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/ISubscriptionService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/SubscriptionService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/SubscriptionProjection.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/SubscriptionMirrorUpdater.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingEvents.cs` | Modify (add SUBSCRIPTION.* builders — created by 35-1) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/SubscriptionEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/BillingServiceCollectionExtensions.cs` | Modify (register svc/repo — created by 35-1) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map SubscriptionEndpoints, SaaS only) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/SubscriptionServiceCheckoutTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/SubscriptionServiceChangeTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/SubscriptionServiceCancelTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/SubscriptionServiceTrialTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/SubscriptionServiceSeatsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/SubscriptionMirrorUpdaterTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/SubscriptionEndpointsRbacTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/SubscriptionEventEmissionTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/NullBillingSubscriptionTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/SubscriptionLifecycleIntegrationTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md).
2. Searched `.dev/` for billing/Stripe/subscription/proration spikes, bugs, findings, decisions.
3. Reviewed the Story 35-1 billing seam (`IBillingProvider`, `IBillingCatalog`, `BillingPlanPrice`, `BillingEvents`) and Story 35-5's webhook processor contract.
4. **Researched the latest Stripe.net subscription API** (`SubscriptionService.UpdateAsync` + `ProrationBehavior`, `SubscriptionScheduleService`, `Checkout.SessionService`, `RequestOptions.IdempotencyKey`) via current docs before writing any SDK call — do not assume method names.
5. Planned the TDD (Red-Green-Refactor) cycle for every new type.

### Key Design Decisions

- **Mirror is the single source of truth for enforcement; Stripe is the source of truth for state.** The local `BillingSubscription` exists so Story 35-6 can enforce quota in < 100ms without calling Stripe. But on every transition the *state* (status/period/trialEnd) is copied from the Stripe object, never inferred from the request, so the mirror can never claim a state Stripe hasn't confirmed (AC13).
- **Upgrade = immediate proration; downgrade = scheduled at period end.** Charging immediately for an upgrade matches user expectation; deferring a downgrade avoids refund complexity and keeps the paid-for quota live through the period. The downgrade uses a Stripe Subscription Schedule and only touches `Tenant.Plan` when the rollover webhook fires.
- **`SubscriptionMirrorUpdater` is shared with Story 35-5.** Both the synchronous API path and the asynchronous webhook path must produce the identical mirror + `Tenant.Plan`/`PlanId` lockstep + DCB event, so that logic lives in exactly one place — the webhook reconciles, it does not re-implement.
- **Partial-unique index, not a plain unique.** A tenant can have many *historical* canceled subscriptions but only one live one; the filtered unique index (`Status NOT IN terminal`) expresses that without a soft-delete column.
- **`Tenant.Plan` (string) + `Tenant.PlanId` (shadow FK) kept in lockstep** exactly as `AdminTenantsEndpoints.UpdateTenantPlan` already does — dashboards reading the legacy string column and the FK-based admin views stay consistent.
- **Seats floor is enforced before any Stripe call** (active membership count via `ITenantMembershipRepository`) so a rejected seat decrease never mutates Stripe.

### Boundary Notes (do not implement sibling-story scope)

- **No webhook endpoint, signature verification, or `billing_webhook_events` dedup table** — that is Story 35-5. This story provides the `SubscriptionMirrorUpdater` the webhook processor *calls*.
- **No quota computation / enforcement / over-quota responses** — that is Story 35-6. This story keeps `Tenant.Plan`/`Seats` correct so 35-6 reads one truth.
- **No customer mapping, plan catalog, or Stripe key wiring** — that is Story 35-1 (consumed here).
- **No invoicing, dunning, tax, billing portal, or credits wallet** — later stories.
- **No tenant-facing subscription *UI*** beyond what other Epic 35 dashboard stories add; this story is API + control-plane only. (The GET projection is what those UIs will render.)

### Risks and Mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| Mirror drifts from Stripe after a failed mid-transaction Stripe call | High | Apply Stripe's returned object inside one CP transaction; webhook (35-5) reconciles; no-drift invariant test. |
| Double-applied proration / duplicate schedule on a retried request | High | Deterministic `RequestOptions.IdempotencyKey` on every mutating Stripe call. |
| Race between API update and webhook update | Medium | `SubscriptionMirrorUpdater` takes status/period from the Stripe object; last-confirmed-state wins (AC13). |
| Downgrade silently lowers quota mid-period | Medium | Scheduled downgrade leaves `PlanSlug`/`Tenant.Plan` at the higher plan until the rollover webhook. |
| Seat decrease orphans active members | Medium | Reject below active-member count with 409 before any Stripe call. |
| Stripe.net API drift vs assumptions | Medium | Research current docs before coding; mock at the service-interface boundary. |
| Single-user accidental Stripe coupling | Medium | Endpoints unmapped + `NullBillingProvider`; tests assert zero SDK calls. |

### Success Metrics

- [ ] After any lifecycle transition, `Tenant.Plan == BillingSubscription.PlanSlug` for the active plan (no drift) — asserted by the invariant test.
- [ ] Each tenant has at most one non-terminal `BillingSubscription` (partial-unique enforced).
- [ ] Every transition emits exactly one `BILLING.SUBSCRIPTION.*` DCB event with `{tenantId,planSlug,status}` tags.
- [ ] Single-user boot maps no subscription endpoints and makes 0 Stripe calls.
- [ ] Migration applies + rolls back; `has-pending-model-changes` = none.

## Logging Requirements

- **INFO**: confirmed lifecycle transition (`tenantId`, `planSlug`, `status`, `seats`, `transition`), checkout session created (`tenantId`, `planSlug`, `stripeSessionId`).
- **DEBUG**: each Stripe SDK call issued (resource type, idempotency key — never the value), upgrade-vs-downgrade decision (`currentSlug`, `targetSlug`, prices compared).
- **WARN**: Stripe call failed → surfaced as 502 / pending webhook reconcile (`tenantId`, error class), seat decrease rejected (`tenantId`, requested, active members).
- **ERROR**: mirror/Stripe divergence detected, DCB event append failure, `Tenant.Plan`/subscription lockstep violation.
- **Structured context**: include `{ tenantId, planSlug, status, seats, idempotencyKey, transition }` where applicable.
- **Credential safety**: NEVER log the Stripe secret key, webhook signing secret, or any customer payment details.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
