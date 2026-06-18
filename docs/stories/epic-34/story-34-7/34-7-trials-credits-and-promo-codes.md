# Story 34-7: Trials, Credits & Promo Codes

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase workflow (Read → Research → Break Down → TDD →
Quality Gates → Failure Handling), the `.dev/` knowledge-base usage rules, TRACE/DEBUG logging
requirements, the test-first (TDD) mandate, 100% critical-path coverage, and build-success
enforcement. Failure to follow it results in rework.

## User Story

As a **platform owner (and, in SaaS mode, a tenant owner/admin)**,
I want time-boxed trials, prepaid/granted USD credits, and promo codes to be first-class,
audited primitives that lower the net price a tenant pays,
so that we can run acquisition and retention offers (free trial windows, "$50 free credit",
"20% off your first invoice") without hand-editing invoices, and so Billing (Epic 35) always sees
a clean credit-aware *net* price that is reproducible from the audit trail.

## Priority

P1 — acquisition/retention pricing primitives. Plan assignment (34-4) and the cost→price markup
engine (34-5) establish *list price*; this story layers the discount/credit primitives that turn
list price into the *net* price Billing charges. Not on the P0 critical path, but required before
Epic 35 can issue a real first invoice with a trial window or applied credit.

## Acceptance Criteria

1. Three new control-plane entities exist under `Tamma.Data.Entities`, registered as `DbSet`s on
   `ControlPlaneDbContext` and configured in `TammaModelConfiguration.cs` with an additive EF
   migration under `Tamma.Data/Migrations/ControlPlane/`:
   - `TenantTrial` — `Id` (UUIDv7), `TenantId` (FK → `tenants.Id`), `PlanId` (FK → `plans.Id`),
     `PlanVersion` (int, pinned copy mirroring 34-4's `TenantPlanAssignment.PlanVersion`),
     `StartsAt` (UTC), `EndsAt` (UTC), `Status` (`active`|`converted`|`expired`),
     `CreatedAt`, `UpdatedAt`.
   - `CreditLedger` — `Id` (UUIDv7), `TenantId` (FK → `tenants.Id`), `DeltaUsd` (decimal(18,6),
     signed), `Reason` (`grant`|`promo`|`consume`|`refund`), `BalanceAfter` (decimal(18,6)),
     `RefEventId` (Guid?, the `DomainEvent.Id` that recorded the mutation), `Note` (text, nullable),
     `CreatedByUserId` (Guid?, the actor; null = system), `CreatedAt`.
   - `PromoCode` — `Id` (UUIDv7), `Code` (text, **case-insensitive unique** via a normalized
     lowercase column/index), `DiscountKind` (`percent`|`fixed`), `Value` (decimal(18,6)),
     `MaxRedemptions` (int?, null = unlimited), `RedemptionCount` (int, default 0), `Expiry`
     (UTC, nullable), `AppliesTo` (text[] of plan slugs, empty/null = all plans), `IsActive`
     (bool), `CreatedAt`, `UpdatedAt`. A companion `PromoRedemption` entity records each redeem
     (`Id`, `PromoCodeId` FK, `TenantId` FK, `RedeemedByUserId`, `PlanSlug`, `RefEventId`,
     `CreatedAt`) with a partial unique index `(PromoCodeId, TenantId)` so a tenant can redeem a
     given code at most once.

2. The credit ledger is **append-only and double-entry-safe**: `CreditLedger` rows are never
   updated or deleted; `BalanceAfter` is the running sum of `DeltaUsd` for the tenant. A `consume`
   entry may **never drive `BalanceAfter` below zero** — `CreditService.ConsumeAsync` reads the
   current balance under a row-level lock (or serializable transaction), applies
   `min(requestedConsume, currentBalance)`, and returns the *unfunded remainder* so the caller
   (the net-price path) can flow it to Billing as a real charge. A consume of more than the
   balance is partially applied (balance → 0), never rejected as a hard error; an *explicit*
   overdraft attempt with `allowPartial = false` is rejected with
   `TammaError("CREDIT.CONSUME.OVERDRAFT", …)`.

3. `POST /api/pricing/promo/redeem` (gated by `SettingsManage`; tenant resolved from
   `ITenantContext`) validates the submitted code and is rejected with `422` + a structured
   `reason` when the code is: not found, `IsActive == false`, past `Expiry`, at/over
   `MaxRedemptions`, already redeemed by this tenant, or not applicable to the chosen plan
   (`AppliesTo` does not contain the plan slug). A `member`-role caller is rejected `403`
   (via the `RequireTenantAdmin` membership pattern used by `AlertEndpoints`). A valid redeem
   inserts a `PromoRedemption`, increments `RedemptionCount` atomically (guarded so concurrent
   redeems cannot exceed `MaxRedemptions`), and — for a `fixed`-USD code — grants a matching
   `CreditLedger` `Reason = promo` entry; a `percent` code is recorded for application at the
   tenant's next invoice (no immediate credit).

4. Trial assignment: subscribing to a **trial-eligible** plan (a plan whose `Quotas`/catalog flag
   marks it trial-eligible) via `CreditService.StartTrialAsync` creates a `TenantTrial` row
   (`Status = active`, `EndsAt = StartsAt + trialDays`) **and** delegates the plan assignment to
   34-4's `IPlanAssignmentService.AssignAsync` with an `AssignPlanOptions` carrying a trial marker
   (a `Reason`/flag the assignment records). At most one `active` trial per tenant is enforced by a
   partial unique index `(TenantId) WHERE Status = 'active'`.

5. Trial expiry is driven by a boundary task that mirrors 34-4's scheduled-activation pattern
   (`PlatformTaskQueueProcessor` + a `TrialExpiryTaskPayload`): when `EndsAt` is reached the trial
   either **converts** (`Status → converted`, plan assignment stays, recurring billing starts) or
   **downgrades to free** (`Status → expired`, calls `IPlanAssignmentService.CancelAsync` /
   assign `plan_free`). A `TENANT.TRIAL.ENDED` DCB event is emitted in both cases carrying
   `tenantId`, `planId`, `planVersion`, `outcome` (`converted`|`downgraded`), `mode`, and the
   trial window.

6. The net-price seam: a new `ICreditAwarePricingEngine` (or a thin decorator
   `CreditAwareUsagePricingEngine` wrapping 34-5's `IUsagePricingEngine`) returns a net result
   `{ sellPriceUsd, creditsAppliedUsd, promoDiscountUsd, netPriceUsd, trialWaived }` where
   `netPriceUsd = max(0, sellPriceUsd − promoDiscountUsd − creditsAppliedUsd)`. During an `active`
   trial window the **recurring/plan charge component is zeroed** (`trialWaived = true`); usage
   credit application still runs for any non-waived component. **This story does NOT re-implement
   the markup math** (34-5 owns `sellPriceUsd`) and **does NOT move money** (Epic 35 owns
   invoicing) — it only computes the credit-aware net the producer/Billing consumes.

7. Admin credit grant: `POST /api/admin/tenants/{id}/credits` (gated by `PlatformOwnerAccess`,
   mounted via `AdminPricingEndpoints`) with body `{ amountUsd, reason?, note? }` appends a
   `CreditLedger` `Reason = grant` entry (positive `DeltaUsd`), recomputing `BalanceAfter`, and
   `GET /api/admin/tenants/{id}/credits` returns the tenant's current balance + recent ledger.
   A negative `amountUsd` (clawback) is supported as `Reason = refund`/`consume` per the body and
   is still floor-clamped at zero balance.

8. All ledger and promo mutations emit DCB events (`AGGREGATE.ACTION.STATUS`) appended via
   `IEventRepository.AppendAsync` and mirrored to the platform audit timeline via
   `IPlatformEventPublisher.AppendAndPublishAsync`: `CREDIT.GRANTED` (admin/promo grant),
   `CREDIT.CONSUMED` (consume, with applied + remainder), `CREDIT.REFUNDED` (clawback/refund),
   `PROMO.REDEEMED` (redeem), and `TENANT.TRIAL.STARTED` / `TENANT.TRIAL.ENDED`. Each carries
   `tenantId`, `mode`, `actorUserId`, and the relevant amounts; the `RefEventId` on the inserted
   `CreditLedger`/`PromoRedemption` row points back at the appended `DomainEvent.Id`.

9. Per-mode + per-tenant ownership is honored: in **single-user** mode the sole user owns trials
   and credits (no RBAC beyond authentication; `CreatedByUserId`/`AssignedByUserId` = the user);
   in **SaaS** mode platform owners grant credits and create promo codes via the admin routes,
   `tenant_owner` redeems promo codes and starts trials via `/api/pricing/*` (`SettingsManage`),
   and `member` is read-only (403 on redeem/subscribe-trial). Mode is read from
   `ITammaModeProvider`. Tenant-scope reads never leak another tenant's ledger or trial.

10. Admin promo-code CRUD: `GET/POST/PATCH /api/admin/pricing/promo-codes` (PlatformOwnerAccess)
    lets a platform owner list, create, and deactivate promo codes; creation validates
    `DiscountKind`/`Value` (percent ∈ (0,100], fixed > 0) and normalizes `Code` to its
    case-insensitive key. Mutations emit `PROMO.CODE.CREATED` / `PROMO.CODE.UPDATED`.

11. A tenant-facing `GET /api/pricing/credits` (`SettingsView`) returns the caller's tenant
    current balance, recent ledger entries, and active trial (if any); the credit-aware estimate
    surface extends 34-5's `GET /api/pricing/estimate` response with `creditsAppliedUsd` /
    `netPriceUsd` / `trialWaived` so the upgrade/cost UI shows net price.

12. Reproducibility / determinism: given the same usage line, the same `MarginPolicy` (34-5), the
    same ledger balance, and the same applicable promo, `ICreditAwarePricingEngine` produces a
    **byte-stable** net result (decimals rounded 6dp internal / 2dp invoice, matching 34-5) —
    covered by a golden-file test. Concurrency: two simultaneous `ConsumeAsync` calls on the same
    tenant never sum to more than the available balance (serialized via lock/serializable txn).

13. Unit + integration tests cover: ledger never goes negative (single + concurrent consume);
    promo validation matrix (not-found / inactive / expired / cap-reached / already-redeemed /
    plan-not-applicable / valid); trial convert vs expire→free; credit-aware net price
    (`netPrice = sell − promo − credits`, floored at 0); trial window zeroes the recurring charge;
    redemption cap enforced under concurrency; RBAC (member 403 on redeem, cross-tenant 404);
    tenant isolation (tenant A cannot redeem against / read tenant B's ledger); DCB event emission
    for every mutation.

## Technical Design

### Namespace / File Structure

```
apps/tamma-elsa/src/
  Tamma.Data/
    Entities/
      TenantTrial.cs                      # NEW — trial row (one active per tenant)
      CreditLedger.cs                     # NEW — append-only USD ledger
      PromoCode.cs                        # NEW — promo definition
      PromoRedemption.cs                  # NEW — per-tenant redemption record
    ControlPlaneDbContext.cs              # MODIFY — 4 new DbSets
    TammaModelConfiguration.cs            # MODIFY — entity config, CHECKs, partial unique indexes, FKs
    Migrations/ControlPlane/
      <ts>_AddTrialsCreditsPromos.cs      # NEW — additive tables + indexes
  Tamma.Core/
    Enums/
      TrialStatus.cs                      # NEW — active|converted|expired (string-backed constants)
      CreditReason.cs                     # NEW — grant|promo|consume|refund
      DiscountKind.cs                     # NEW — percent|fixed
  Tamma.Api/
    Services/Pricing/
      ICreditService.cs                   # NEW — grant / consume / balance / trial
      CreditService.cs                    # NEW — ledger logic (serialized consume, floor-at-zero)
      IPromoCodeService.cs                # NEW — validate / redeem / CRUD
      PromoCodeService.cs                 # NEW — redemption + cap-guard + plan-applicability
      ICreditAwarePricingEngine.cs        # NEW — net-price seam over 34-5 IUsagePricingEngine
      CreditAwareUsagePricingEngine.cs    # NEW — decorator: sell → net (credits/promo/trial)
      TrialsCreditsModels.cs              # NEW — GrantCreditOptions, ConsumeResult, NetPriceResult,
                                          #       PromoValidationResult, StartTrialOptions
      TrialsCreditsEventTypes.cs          # NEW — CREDIT.* / PROMO.* / TENANT.TRIAL.* constants
    Services/Provisioning/
      TrialExpiryTaskPayload.cs           # NEW — platform-queue payload for trial-end boundary
    Endpoints/
      PricingEndpoints.cs                 # MODIFY (file from 34-2) — promo/redeem, credits, estimate ext
      Admin/AdminPricingEndpoints.cs      # MODIFY (file from 34-5) — credits grant, promo-code CRUD
    Extensions/
      PricingServiceCollectionExtensions.cs # MODIFY (from 34-1/34-5) — register the new services
    Program.cs                            # MODIFY — map new routes; DI; decorate IUsagePricingEngine
```

> **Boundary note (Epic 34 ↔ siblings):** this story owns *trials, credits, and promo codes only*.
> The versioned `Plan` catalog and `IPlanCatalogService` belong to **34-1**; the
> `PricingEndpoints.cs`/`AdminPricingEndpoints.cs` files and custom-plan binding belong to
> **34-2**; **plan assignment lifecycle** (`IPlanAssignmentService`, `TenantPlanAssignment`,
> the scheduled-activation queue pattern) belongs to **34-4** — this story *delegates* trial
> assignment/conversion/downgrade to it and *reuses* its boundary-task pattern, never duplicating
> assignment logic. The **cost→price markup engine** (`IUsagePricingEngine`, `sellPriceUsd`,
> `MarginPolicy`) belongs to **34-5** — this story *wraps* it and consumes `sellPriceUsd`, never
> re-implements markup. **Invoicing, proration, and actually charging money** belong to **Epic 35**;
> **quota/usage enforcement** belongs to the Epic 34 enforcement story and Epic 20 metering — this
> story only computes the credit-aware *net* and flags overdraft remainder; it does not move money.

### Key Entities (sketch)

```csharp
namespace Tamma.Data.Entities;

/// <summary>One time-boxed trial per tenant (at most one active). Pins PlanVersion like
/// TenantPlanAssignment (34-4) so a later plan deprecation can't re-price the trial.</summary>
public class TenantTrial
{
    public Guid Id { get; set; }                 // UUIDv7
    public Guid TenantId { get; set; }           // FK tenants.Id
    public Guid PlanId { get; set; }             // FK plans.Id
    public int PlanVersion { get; set; }         // pinned
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public string Status { get; set; } = "active"; // active|converted|expired
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Append-only, double-entry-safe USD credit ledger. BalanceAfter is the running sum;
/// a 'consume' row can never push BalanceAfter below zero.</summary>
public class CreditLedger
{
    public Guid Id { get; set; }                 // UUIDv7
    public Guid TenantId { get; set; }           // FK tenants.Id
    public decimal DeltaUsd { get; set; }        // signed; + grant/refund, - consume
    public string Reason { get; set; } = null!;  // grant|promo|consume|refund
    public decimal BalanceAfter { get; set; }    // >= 0 (CHECK)
    public Guid? RefEventId { get; set; }        // DomainEvent.Id that recorded this
    public string? Note { get; set; }
    public Guid? CreatedByUserId { get; set; }   // actor; null = system/scheduler
    public DateTime CreatedAt { get; set; }
}

public class PromoCode
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!;        // display form
    public string CodeKey { get; set; } = null!;     // lower(code), unique
    public string DiscountKind { get; set; } = null!; // percent|fixed
    public decimal Value { get; set; }               // percent ∈ (0,100] | fixed > 0
    public int? MaxRedemptions { get; set; }         // null = unlimited
    public int RedemptionCount { get; set; }
    public DateTime? Expiry { get; set; }
    public string[]? AppliesTo { get; set; }         // plan slugs; null/empty = all
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class PromoRedemption
{
    public Guid Id { get; set; }
    public Guid PromoCodeId { get; set; }            // FK promo_codes.Id
    public Guid TenantId { get; set; }               // FK tenants.Id
    public Guid? RedeemedByUserId { get; set; }
    public string PlanSlug { get; set; } = null!;
    public Guid? RefEventId { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

### EF Model Config (sketch, in `TammaModelConfiguration.cs`)

```csharp
modelBuilder.Entity<CreditLedger>(e =>
{
    e.ToTable("credit_ledger", t =>
        t.HasCheckConstraint("ck_credit_balance_nonneg", "\"BalanceAfter\" >= 0"));
    e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
    e.Property(x => x.DeltaUsd).HasColumnType("numeric(18,6)");
    e.Property(x => x.BalanceAfter).HasColumnType("numeric(18,6)");
    e.Property(x => x.Reason).HasMaxLength(16);
    e.HasIndex(x => new { x.TenantId, x.CreatedAt });   // running-balance / ledger read
    e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
});

modelBuilder.Entity<TenantTrial>(e =>
{
    e.ToTable("tenant_trials", t =>
        t.HasCheckConstraint("ck_trial_status", "\"Status\" IN ('active','converted','expired')"));
    e.HasIndex(x => x.TenantId).IsUnique()
        .HasFilter("\"Status\" = 'active'")
        .HasDatabaseName("ux_trial_one_active_per_tenant");   // AC4
    e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
});

modelBuilder.Entity<PromoCode>(e =>
{
    e.ToTable("promo_codes", t =>
        t.HasCheckConstraint("ck_promo_kind", "\"DiscountKind\" IN ('percent','fixed')"));
    e.HasIndex(x => x.CodeKey).IsUnique();                   // case-insensitive uniqueness
});

modelBuilder.Entity<PromoRedemption>(e =>
{
    e.HasIndex(x => new { x.PromoCodeId, x.TenantId }).IsUnique(); // one redeem per tenant per code
    e.HasOne<PromoCode>().WithMany().HasForeignKey(x => x.PromoCodeId).OnDelete(DeleteBehavior.Cascade);
    e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
});
```

> `dotnet ef migrations add AddTrialsCreditsPromos` — additive tables only (no CHECK edits on
> existing tables, so the Phase-0 collapsed-baseline rules do not apply). `has-pending-model-changes`
> must report **none** afterwards; mirror entity config in `TammaModelConfiguration.cs` (the single
> source), not in `OnModelCreating` of the context.

### Service Interfaces (sketch)

```csharp
public interface ICreditService
{
    /// Append a positive grant (admin/promo). Returns the new balance + appended event id.
    Task<CreditMutationResult> GrantAsync(Guid tenantId, decimal amountUsd, CreditReason reason,
        GrantCreditOptions opts, CancellationToken ct);

    /// Apply up to `amountUsd` of credit (serialized; floor at 0). Returns applied + remainder.
    Task<ConsumeResult> ConsumeAsync(Guid tenantId, decimal amountUsd,
        bool allowPartial, CancellationToken ct);

    Task<decimal> GetBalanceAsync(Guid tenantId, CancellationToken ct);
    Task<IReadOnlyList<CreditLedger>> GetLedgerAsync(Guid tenantId, int limit, CancellationToken ct);

    /// Start a trial: create TenantTrial + delegate assignment to 34-4 IPlanAssignmentService.
    Task<TenantTrial> StartTrialAsync(Guid tenantId, Guid planId, StartTrialOptions opts,
        CancellationToken ct);

    /// Boundary task target: convert or downgrade an ended trial (idempotent by trialId).
    Task EndTrialAsync(Guid tenantId, Guid trialId, CancellationToken ct);
}

public interface IPromoCodeService
{
    Task<PromoValidationResult> ValidateAsync(string code, string planSlug, Guid tenantId,
        CancellationToken ct);
    Task<PromoRedemption> RedeemAsync(string code, string planSlug, Guid tenantId,
        Guid? actorUserId, CancellationToken ct);  // throws TammaError(422-mapped) on invalid
}

public interface ICreditAwarePricingEngine
{
    /// sell (34-5) → net = max(0, sell - promoDiscount - creditsApplied); trial zeroes recurring.
    Task<NetPriceResult> PriceNetAsync(UsageLine usageLine, Guid tenantId, CancellationToken ct);
}

public sealed record ConsumeResult(decimal AppliedUsd, decimal RemainderUsd, decimal BalanceAfter);
public sealed record NetPriceResult(decimal SellPriceUsd, decimal PromoDiscountUsd,
    decimal CreditsAppliedUsd, decimal NetPriceUsd, bool TrialWaived);
public sealed record PromoValidationResult(bool Ok, string? Reason, PromoCode? Code);
```

### Concurrency-safe consume (the load-bearing invariant)

`ConsumeAsync` runs inside a `Serializable` (or `SELECT … FOR UPDATE` row-lock on the latest
ledger row's tenant scope) transaction:

```
BEGIN ISOLATION LEVEL SERIALIZABLE;
  balance := COALESCE((SELECT "BalanceAfter" FROM credit_ledger
                       WHERE "TenantId" = @t ORDER BY "CreatedAt" DESC, "Id" DESC LIMIT 1), 0);
  applied := LEAST(@amount, balance);                 -- never more than balance
  IF applied = 0 AND NOT @allowPartial AND @amount > 0 -> throw CREDIT.CONSUME.OVERDRAFT
  INSERT credit_ledger(... DeltaUsd = -applied, BalanceAfter = balance - applied, Reason='consume');
COMMIT;  -- a concurrent consume retries on serialization failure (40001) → re-reads balance
return ConsumeResult(applied, @amount - applied, balance - applied);
```

The `ck_credit_balance_nonneg` CHECK is the belt-and-suspenders backstop: even a logic bug cannot
persist a negative balance.

### DCB Event Names (`AGGREGATE.ACTION.STATUS`)

- `CREDIT.GRANTED` — admin grant / fixed-promo grant. Tags: `tenantId`, `mode`, `actorUserId`,
  `reason`; data: `amountUsd`, `balanceAfter`.
- `CREDIT.CONSUMED` — consume on the net-price path. Tags: `tenantId`, `mode`; data: `requestedUsd`,
  `appliedUsd`, `remainderUsd`, `balanceAfter`.
- `CREDIT.REFUNDED` — clawback/refund. Tags: `tenantId`, `mode`, `actorUserId`; data: `amountUsd`,
  `balanceAfter`.
- `PROMO.REDEEMED` — tenant redeem. Tags: `tenantId`, `mode`, `actorUserId`, `code`; data:
  `discountKind`, `value`, `planSlug`.
- `PROMO.CODE.CREATED` / `PROMO.CODE.UPDATED` — admin CRUD (platform audit).
- `TENANT.TRIAL.STARTED` — Tags: `tenantId`, `planId`, `planVersion`, `mode`; data: `startsAt`,
  `endsAt`.
- `TENANT.TRIAL.ENDED` — Tags: `tenantId`, `planId`, `planVersion`, `outcome`
  (`converted`|`downgraded`), `mode`; data: trial window.

Tenant-scope events append via `IEventRepository.AppendAsync(DomainEvent)` with `Tags`/`Data`
JSON-serialized (the `OrgEndpoints` emit pattern); the platform-audit mirror uses
`IPlatformEventPublisher.AppendAndPublishAsync(PlatformEvent)`. The appended `DomainEvent.Id` is
written back into the inserted ledger/redemption row's `RefEventId` for forensic linkage.

### API Shape

```
# Admin (platform owner) — PlatformOwnerAccess (AdminPricingEndpoints.cs)
GET  /api/admin/tenants/{tenantId}/credits        # balance + recent ledger
POST /api/admin/tenants/{tenantId}/credits        # body { amountUsd, reason?, note? } → CREDIT.GRANTED/REFUNDED
GET  /api/admin/pricing/promo-codes               # list
POST /api/admin/pricing/promo-codes               # body { code, discountKind, value, maxRedemptions?, expiry?, appliesTo[] }
PATCH/api/admin/pricing/promo-codes/{id}          # deactivate / edit
  → 200 created/updated ; 422 invalid_discount | duplicate_code

# Tenant self-service — SettingsManage (tenant_owner); member → 403 (PricingEndpoints.cs)
POST /api/pricing/promo/redeem                     # body { code, planSlug } ; tenant from ITenantContext
  → 200 { applied: 'credit'|'percent', balanceAfter? }
  → 422 code_not_found | code_inactive | code_expired | cap_reached | already_redeemed | plan_not_applicable
  → 403 member_role
GET  /api/pricing/credits                          # SettingsView — balance + ledger + active trial
GET  /api/pricing/estimate                         # 34-5 surface, extended: + creditsApplied/netPrice/trialWaived
```

### Per-Mode + Per-Tenant Handling

| Concern | single-user mode | SaaS mode |
|---|---|---|
| Grant credits | the sole user (admin route, authenticated) | platform owner (`PlatformOwnerAccess`) |
| Create/manage promo codes | the sole user | platform owner |
| Redeem promo / start trial | the sole user via `/api/pricing/*` | `tenant_owner` (`SettingsManage`); `member` → 403 |
| Read balance/ledger/trial | the user | tenant members (`SettingsView`); never cross-tenant |
| `CreatedByUserId` / actor | the user | the platform owner / tenant owner |
| Tenant resolution | `ITenantContext.TenantId` (lone tenant) | `ITenantContext.TenantId` (caller's tenant); admin route takes `{tenantId}` |
| Cross-tenant guard | n/a (one tenant) | redeem ignores any body tenant id, uses `ITenantContext`; admin route 404s unknown tenant |
| Mode source | `ITammaModeProvider.Mode` | same |

### Integration Points

- **34-4 `IPlanAssignmentService`** — `StartTrialAsync` delegates the plan flip to `AssignAsync`
  (trial marker in `AssignPlanOptions.Reason`); trial end delegates convert (keep) or downgrade
  (`CancelAsync` / assign `plan_free`). The `TenantPlanAssignment.PlanVersion` pinning convention
  is mirrored onto `TenantTrial.PlanVersion`.
- **34-5 `IUsagePricingEngine`** — `CreditAwareUsagePricingEngine` decorates it: `sellPriceUsd`
  comes from `PriceUsage`; this story subtracts promo discount + applied credit. Registered via
  the existing `PricingServiceCollectionExtensions` so DI resolves the decorator.
- **34-1 `IPlanCatalogService`** — resolve plan slug ↔ `(PlanId, Version)`, read trial-eligibility
  + `IsCustom`/`Status` for trial/promo applicability guards.
- **`AdminTenantsEndpoints.BuildAdminEvent`** — reuse the actor-extraction breadcrumb for the
  platform-event mirror; the `MoveTenant` 202+poll / `PlatformTaskQueueProcessor` pattern is
  mirrored for the trial-expiry boundary task (`TrialExpiryTaskPayload`).
- **`IEventRepository` / `IPlatformEventPublisher`** — DCB + platform-audit emission.
- **`ITammaModeProvider` (`TammaMode.cs`) / `ITenantContext`** — mode + tenant resolution.
- **Epic 35 (Billing)** — consumes `NetPriceResult` (net price + `creditsApplied` + `remainder`)
  and the `TENANT.TRIAL.ENDED` conversion signal; this story emits, Billing charges.

## Dependencies

**Internal — prerequisite:**
- Story 34-4 (Per-Tenant Plan Assignment & Lifecycle) — `IPlanAssignmentService`,
  `TenantPlanAssignment`, `AssignPlanOptions`/`CancelPlanOptions`, the `PlatformTaskQueueProcessor`
  boundary-task pattern. Trial assignment/conversion/downgrade delegates to it.
- Story 34-5 (Cost→Price Markup Engine) — `IUsagePricingEngine`, `NetPriceResult` inputs
  (`sellPriceUsd`, `pricingMode`), the 6dp/2dp rounding convention, and the
  `AdminPricingEndpoints.cs` / `PricingEndpoints.cs` files this story extends.
- Story 34-1 (Plan & Price-Book Catalog) — `IPlanCatalogService`, plan slugs, trial-eligibility +
  `Status`/`IsCustom` flags consumed for guards.
- Epic 28 (control-plane tenancy) — `Tenant`, `ControlPlaneDbContext`, `PlatformQueuedTask` /
  `PlatformTaskQueueProcessor`, `IPlatformEventPublisher`, `ITenantContext`.
- Epic 4 (DCB events) — `DomainEvent` / `IEventRepository` append path.
- Story 5.6 (alerts) RBAC precedent — `RequireTenantAdmin` / membership-filter pattern reused for
  the tenant routes.

**Internal — blocks:**
- Epic 35 (Billing) — consumes the credit-aware net price, applied-credit + remainder split, and
  the trial conversion signal to issue real invoices.

**External:**
- None required in this story. Stripe/payment movement is Epic 35; the unit/integration tests mock
  `IUsagePricingEngine`, `IPlanAssignmentService`, and the providers — no live billing SDK is on
  the path.

## Testing Strategy

**Unit (xUnit, `tests/Tamma.Api.Tests/Pricing/`):**
1. `ConsumeAsync` floor-at-zero: consume 30 from a 20 balance → applied 20, remainder 10,
   balance 0; never negative. `allowPartial = false` on an empty balance throws
   `CREDIT.CONSUME.OVERDRAFT`.
2. `ConsumeAsync` concurrency: two parallel consumes of 15 each on a 20 balance net to applied 20
   total (one full, one partial), never > 20 (serializable retry path / forced
   `DbUpdateException` simulation on the in-memory + real-Postgres provider).
3. `GrantAsync` / refund: appends positive/clamped rows, recomputes `BalanceAfter`, emits
   `CREDIT.GRANTED` / `CREDIT.REFUNDED`, writes `RefEventId`.
4. Promo validation matrix: not-found, inactive, expired, cap-reached, already-redeemed (same
   tenant), plan-not-applicable, and the valid case — each maps to the right `reason` / 422 or
   success.
5. `RedeemAsync` cap-guard under concurrency: `MaxRedemptions = 1`, two tenants redeem → exactly
   one succeeds, one 422 `cap_reached`; same tenant twice → `already_redeemed`.
6. Fixed-promo grants a `CreditLedger Reason = promo` entry; percent-promo records redemption but
   grants no credit (applied at invoice).
7. Trial: `StartTrialAsync` creates `active` trial + delegates `AssignAsync` (mock); a second
   active trial is rejected (partial unique index / pre-check). `EndTrialAsync` convert → status
   `converted`, assignment kept, `TENANT.TRIAL.ENDED outcome=converted`; downgrade → status
   `expired`, `CancelAsync`/assign-free called, `outcome=downgraded`. Idempotent by `trialId`.
8. `CreditAwareUsagePricingEngine`: `net = max(0, sell − promo − credits)` (mock
   `IUsagePricingEngine`); active trial zeroes the recurring component (`trialWaived = true`);
   net floored at 0 when discounts exceed sell.
9. Golden-file determinism: a fixed usage line + balance + promo produces a byte-stable
   `NetPriceResult` (6dp/2dp).
10. DCB event-shape tests: each mutation appends the right event type with required tags + amounts.

**Integration (xUnit + Postgres via `sg docker -c "dotnet test …"`):**
11. Migration applies + rolls back cleanly; `ck_credit_balance_nonneg` rejects a negative
    `BalanceAfter` at the DB level; the trial partial unique index rejects a second `active` trial;
    the `(PromoCodeId, TenantId)` unique index rejects a duplicate redeem.
12. `POST /api/admin/tenants/{id}/credits` (PlatformOwnerAccess) grants + `GET` returns the balance;
    a non-platform JWT → 403; unknown tenant → 404.
13. `POST /api/pricing/promo/redeem`: `tenant_owner` redeems a valid code (200); `member` → 403;
    invalid code variants → 422 with reason; **tenant isolation** — caller A's redeem cannot
    affect / read tenant B's ledger (body tenant id ignored; route is tenant-from-context).
14. Trial-end boundary end-to-end via the platform queue: enqueue a `TrialExpiryTaskPayload`, run
    the processor, assert convert vs downgrade outcome + emitted `TENANT.TRIAL.ENDED`.
15. Concurrent consume against a real Postgres balance never oversells (serializable transaction).

**Mocks:** `IUsagePricingEngine` (34-5, returns a fixed `sellPriceUsd`), `IPlanAssignmentService`
(34-4, assert delegation), `IPlanCatalogService` (34-1), `IEventRepository` /
`IPlatformEventPublisher` (assert emission), `TimeProvider` (deterministic trial windows / expiry),
`ITammaModeProvider` + `ITenantContext` (mode + tenant). No Stripe/payment SDK on the path.

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/TenantTrial.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/CreditLedger.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/PromoCode.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/PromoRedemption.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (4 DbSets) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config, CHECKs, indexes, FKs) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddTrialsCreditsPromos.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Enums/TrialStatus.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Enums/CreditReason.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Core/Enums/DiscountKind.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/ICreditService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/CreditService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/IPromoCodeService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PromoCodeService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/ICreditAwarePricingEngine.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/CreditAwareUsagePricingEngine.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/TrialsCreditsModels.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/TrialsCreditsEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/TrialExpiryTaskPayload.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/PricingEndpoints.cs` | Modify (redeem, credits, estimate ext; file from 34-2) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminPricingEndpoints.cs` | Modify (credits grant, promo CRUD; file from 34-5) |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/PricingServiceCollectionExtensions.cs` | Modify (DI + decorate IUsagePricingEngine) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map routes; DI) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/CreditServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/PromoCodeServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/CreditAwarePricingEngineTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Pricing/TrialsCreditsEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Data.Tests/Migrations/TrialsCreditsMigrationTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md).
2. Searched `.dev/` for related spikes/bugs/findings/decisions (pricing, tenancy, events, ledger).
3. Reviewed 34-1, 34-4 and 34-5 — this story is strictly downstream of the catalog, assignment
   lifecycle, and markup engine; do NOT re-implement any of them here.
4. Confirmed the C# test-runner contract: `sg docker -c "dotnet test …"` for docker-bound suites
   (build needs no wrapper). See `reference_dotnet_test_docker`.
5. Planned a TDD (Red-Green-Refactor) approach — tests for each AC before implementation.

### Key Design Decisions

- **Append-only ledger, never UPDATE.** Every credit movement is a new immutable `CreditLedger`
  row carrying its own `BalanceAfter`. This gives a forensic audit trail (matching the DCB/event
  spirit) and makes balance reconstruction a pure running sum. Editing/deleting a ledger row is
  never a code path.
- **Floor consume at zero; remainder flows to Billing.** Credits *offset* a charge, they never
  create debt. `ConsumeAsync` applies `min(request, balance)` and hands the unfunded remainder back
  — Billing (Epic 35) charges the remainder as real money. The `ck_credit_balance_nonneg` CHECK is
  the DB-level backstop.
- **Serialize the consume, don't trust read-then-write.** Two simultaneous consumes on the same
  tenant must not double-spend the same balance. A serializable transaction (retry on `40001`) or a
  per-tenant row lock is load-bearing — pin it in the concurrency test.
- **Decorate the markup engine, don't fork it.** `CreditAwareUsagePricingEngine` wraps 34-5's
  `IUsagePricingEngine` so `sellPriceUsd` math lives in exactly one place; this story only subtracts
  promo + credit and zeroes the trial-waived component.
- **Pin the trial's plan version.** Mirror 34-4: `TenantTrial.PlanVersion` is denormalized so a
  later plan deprecation cannot re-price an in-flight trial.
- **Reuse the platform queue for the trial boundary.** Trial expiry mirrors 34-4's
  scheduled-activation enqueue + processor pattern instead of inventing a scheduler; the task is
  idempotent by `trialId` so a host that was down at `EndsAt` catches up on the next tick.
- **Percent vs fixed split.** A `fixed` code grants a `CreditLedger` entry immediately (it *is* a
  credit). A `percent` code is recorded as a redemption and applied at invoice time by Billing
  (no ledger row) — keeping the ledger purely USD-denominated.

### Edge Cases

- Redeeming the same code twice for one tenant → `already_redeemed` 422 (unique
  `(PromoCodeId, TenantId)`), `RedemptionCount` not double-incremented.
- Concurrent redeem of the last remaining redemption → exactly one wins (atomic
  `UPDATE … SET RedemptionCount = RedemptionCount + 1 WHERE RedemptionCount < MaxRedemptions`);
  the loser gets `cap_reached`.
- Promo `AppliesTo` empty/null = applies to all plans; non-empty must contain the chosen slug.
- Trial `EndsAt` passed while host was down → caught by the boundary task on next processor tick.
- Net price when promo + credits exceed sell → floored at 0, `creditsApplied` capped at the
  pre-floor remainder (never "over-apply" credit beyond what the charge needs).
- Admin negative grant (clawback) → `Reason = refund`/`consume`, still floor-clamped at 0.

## Logging Requirements

- **INFO**: credit granted (tenantId, amount, reason, balanceAfter), promo redeemed
  (tenantId, code, kind), trial started (tenantId, planId, endsAt), trial ended
  (tenantId, outcome).
- **DEBUG**: consume request vs applied vs remainder, balance read under lock, promo validation
  decision (which guard matched), trial boundary-task claim, transaction begin/commit/retry.
- **WARN**: consume could not be fully funded (remainder flows to Billing), serializable retry on
  concurrent consume, promo redeem rejected (reason), admin clawback applied.
- **ERROR**: ledger transaction rollback, `ck_credit_balance_nonneg` violation surfaced (bug),
  trial-boundary enqueue/claim failure, promo cap atomic-update failure.
- **Structured context**: include `{ tenantId, amountUsd, balanceAfter, reason, code, planSlug,
  trialId, outcome, mode, actorUserId }` where applicable.
- **Credential / PII safety**: never log payment details or actor PII beyond the user GUID; promo
  codes are low-sensitivity but still logged only on the audit path, never in client error bodies
  beyond the validation reason.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
