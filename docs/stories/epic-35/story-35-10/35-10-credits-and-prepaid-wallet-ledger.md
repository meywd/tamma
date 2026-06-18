# Story 35-10: Credits & Prepaid Wallet Ledger

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-Phase Development Workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), the `.dev/` knowledge-base usage rules (spikes, bugs, findings, decisions), TRACE/DEBUG logging requirements, the Test-Driven Development workflow, and the build-success / 100%-critical-path coverage gates. Failure to follow this process will result in rework.

## User Story

As a **tenant_owner or tenant_admin** of a SaaS Tamma tenant,
I want a prepaid credits wallet whose balance is derived from an append-only, double-entry-style ledger (grants, Stripe top-ups, consumption against invoices, expirations, refunds) and is automatically applied to my invoices before my card is charged,
So that I can hold promotional/support credits or prepaid balance, see exactly where every cent went, and reduce or eliminate card charges — while BYOK tenants can still offset the platform/seat fee and platform-provided tenants can additionally offset metered token charges.

## Priority

P2 - Monetization quality-of-life. Builds on the foundation (35-1), webhook ingestion (35-5), and invoicing (35-8) backbones; not required for the first paying tenant but required for promo credits, prepaid plans, and support goodwill.

## Acceptance Criteria

1. A new append-only control-plane entity `BillingWalletLedger` (`apps/tamma-elsa/src/Tamma.Data/Entities/BillingWalletLedger.cs`, `DbSet<BillingWalletLedger> BillingWalletLedger` on `ControlPlaneDbContext`, configured in `TammaModelConfiguration.ConfigureControlPlaneEntities`, table `billing_wallet_ledger`) records one immutable row per ledger entry with columns: `Id` (Guid PK), `TenantId` (Guid FK → `tenants.Id`, indexed), `EntryType` (text, CHECK-constrained to `grant|topup|consume|expire|refund|adjustment`), `AmountUsd` (`numeric(12,4)`; positive credits the balance for `grant`/`topup`/`refund`, negative debits for `consume`/`expire`, signed for `adjustment`), `BalanceAfter` (`numeric(12,4)`; the derived running balance snapshot computed inside the same serialized transaction that appends the row), `Reference` (text, nullable — `invoiceId`/`usage period`/`promo code`/Stripe `payment_intent` id), `ReferenceKind` (text, nullable — `invoice|payment_intent|promo|usage_period|grant`), `ExpiresAt` (`timestamptz`, nullable — only meaningful for `grant`/`topup` lots), `IdempotencyKey` (text, nullable, **UNIQUE NULLS NOT DISTINCT**), `Note` (text, nullable), `CreatedBy` (Guid, nullable — admin/user who issued a grant), `CreatedAt` (`timestamptz`). **Rows are never UPDATEd or DELETEd** — corrections are new `adjustment`/`refund`/`expire` rows. The current balance is always derived as `SUM(AmountUsd)` per tenant (or read from the latest row's `BalanceAfter`), never stored mutably on the tenant.

2. `POST /api/v1/billing/wallet/topup` (tenant route under `/api/v1/orgs/{tenantId}/...` gated by `RequireTenantMembershipFilter`; tenant_owner/tenant_admin only — `member` → 403) creates a Stripe **one-time** `PaymentIntent` for the requested USD amount via the Story 35-1 `IBillingProvider`/Stripe client (resolved through the Epic 29 cabinet), tagged with metadata `{ tenantId, purpose: "wallet_topup" }`, and returns the PaymentIntent `client_secret` to the caller. **No ledger row is written at intent-creation time** — the credit lands only when the payment actually succeeds (AC3).

3. On `payment_intent.succeeded` for a wallet-topup PaymentIntent, the Story 35-5 `IBillingEventHandler` registry routes to a new `WalletTopupWebhookHandler` (registered via `services.AddBillingEventHandler<WalletTopupWebhookHandler>()`), which appends a `topup` ledger entry **idempotently keyed by the Stripe `payment_intent` id** (`IdempotencyKey = "topup:{paymentIntentId}"`); a duplicate webhook delivery (Stripe at-least-once) collides on the UNIQUE index and is a no-op. The handler emits `BILLING.CREDIT.TOPPED_UP`. Non-wallet PaymentIntents (e.g. invoice payment) are ignored by this handler (no metadata `purpose: "wallet_topup"` → skip).

4. Available (non-expired) credit is applied to a tenant's invoice **before the card is charged**, inside the Story 35-8 invoice finalize path: a new `WalletCreditService.ApplyToInvoiceAsync(...)` is invoked from the `invoice.finalized` projection (35-8's `InvoiceWebhookHandler` / `InvoiceService`) before the invoice transitions to charge. The applied amount is `min(availableBalance, invoiceAmountDue)`, recorded as (a) a Stripe invoice **credit line item / customer balance transaction** so Stripe charges the reduced amount, and (b) a `consume` ledger entry with `Reference = invoiceId`, `ReferenceKind = "invoice"`, `IdempotencyKey = "consume:{invoiceId}"` (idempotent against re-finalization / webhook replay). The handler emits `BILLING.CREDIT.CONSUMED`.

5. An admin grant endpoint `POST /api/v1/admin/billing/wallet/{tenantId}/grant` (policy `PlatformOwnerAccess` — platform-owner only) issues promotional/support credits: body `{ amountUsd, expiresAt?, note?, promoCode? }`, appends a `grant` ledger entry (`IdempotencyKey = "grant:{guid}"` minted server-side or `"promo:{promoCode}:{tenantId}"` when a promo code is supplied so the same promo is not double-granted), and emits `BILLING.CREDIT.GRANTED` with `CreatedBy` = the platform admin. Negative grants are rejected (use the adjustment path).

6. `GET /api/v1/orgs/{tenantId}/billing/wallet` (tenant_owner/tenant_admin/**member-read**, behind `RequireTenantMembershipFilter`) returns `{ balanceUsd, availableUsd, expiringSoonUsd, currency }` plus a paged ledger history (`entries[]` with `entryType`, `amountUsd`, `balanceAfter`, `reference`, `referenceKind`, `expiresAt`, `createdAt`; default page 50, max 200, most-recent first). `availableUsd` excludes already-expired lots. An admin read `GET /api/v1/admin/billing/wallet/{tenantId}` (`PlatformOwnerAccess`) returns the same shape for any tenant.

6b. RBAC follows CLAUDE.md per-mode ownership: in **SaaS** the tenant wallet is owned by `tenant_owner`/`tenant_admin` (top-up + read), `member` is read-only (403 on `topup`); grants/adjustments are platform-owner-only (`PlatformOwnerAccess`). In **single-user** mode there is no Stripe wiring (Story 35-1 `NullBillingProvider`); the wallet routes are **not mapped** and the credit-application hook is a no-op (matching 35-1 AC9 / 35-5 AC13).

7. Credit application is **atomic and race-safe** (no double-spend) under concurrent invoice finalization for the same tenant: `WalletCreditService.ApplyToInvoiceAsync` runs inside a `Serializable` (or `pg_advisory_xact_lock(hashtextextended(tenantId, 0))`-guarded) control-plane transaction that re-reads the live balance, computes `BalanceAfter`, and appends the `consume` row in one unit — two concurrent finalizations for the same tenant cannot both spend the same dollar. The `consume` `IdempotencyKey` UNIQUE index is the second line of defence (one consume per invoice).

8. An expired credit lot is swept by a scheduled `PlatformQueuedTask` (`Type = "billing.wallet.expire_sweep"`, handled by a new `WalletExpirySweepTaskHandler : IPlatformTaskHandler` on the existing `PlatformTaskWorker`): for each `grant`/`topup` lot whose `ExpiresAt < now` and whose remaining (unconsumed) balance is positive, it appends an `expire` ledger entry (negative `AmountUsd`, `Reference` = the originating lot id, `ReferenceKind = "grant"`, `IdempotencyKey = "expire:{lotId}"`) reducing the balance, and emits `BILLING.CREDIT.EXPIRED`. The sweep is idempotent (an already-swept lot collides on the UNIQUE key and is skipped).

9. Refunds: when a wallet top-up is refunded (`charge.refunded` / `payment_intent` reversal surfaced by 35-5, or an admin-initiated `POST /api/v1/admin/billing/wallet/{tenantId}/refund` with `PlatformOwnerAccess`), a `refund` ledger entry (negative `AmountUsd`, `Reference` = the refunded `payment_intent`/`charge` id, `IdempotencyKey = "refund:{chargeId}"`) is appended and `BILLING.CREDIT.REFUNDED` emitted. A refund cannot drive the balance below zero (clamp + WARN log if it would).

10. DCB events are emitted via `IEventRepository.AppendAsync(DomainEvent)` (CP store, `AGGREGATE.ACTION.STATUS`): `BILLING.CREDIT.GRANTED`, `BILLING.CREDIT.TOPPED_UP`, `BILLING.CREDIT.CONSUMED`, `BILLING.CREDIT.EXPIRED`, `BILLING.CREDIT.REFUNDED`, each with `TenantId` set and JSONB `tags = { tenantId, amountUsd, balanceAfter, reference, entryType }` and `Metadata = { "workflowVersion": "1.0.0", "eventSource": "system" }`. (These CP-resident events are visible to the Story 5.6 `AlertRuleEvaluator` for free — no alert rule is added here.)

11. Tenant isolation: every ledger read/write filters `tenant_id = {tenantId}`; the tenant routes are behind `RequireTenantMembershipFilter` (cross-tenant 404), and `availableUsd`/balance are computed per tenant. A wallet entry for tenant A is never returned to tenant B and never offsets tenant B's invoice.

12. An EF Core migration under `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/` adds the `billing_wallet_ledger` table (plus snapshot update). `dotnet ef migrations has-pending-model-changes` reports none after the migration; `Update` then down/`Remove` applies and rolls back cleanly. Entity config lives **only** in `TammaModelConfiguration` (the established single source).

13. Unit + integration tests (xUnit, `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/`) cover: ledger append + derived-balance correctness across all six entry types; idempotent top-up (duplicate `payment_intent.succeeded` → one `topup` row); credit-before-card application in the finalize path (`min(balance, amountDue)`, Stripe credit line + `consume` row); **double-spend prevention** under concurrent finalization (two parallel applies for one tenant never over-spend); expiry sweep (expired lot → one `expire` row, idempotent re-run); admin grant RBAC (`PlatformOwnerAccess` only; promo double-grant blocked); tenant top-up/read RBAC (`member` 403 on top-up, read OK); single-user no-op seam; and tenant isolation.

14. Logging follows the project standard: INFO on grant/top-up/consume/expire/refund (tenantId, entryType, amountUsd, balanceAfter); WARN on refund-would-go-negative clamp, sweep skip-on-collision; ERROR on Stripe PaymentIntent failure and migration/transaction failure. **Stripe secret key, `client_secret`, `payment_intent` secrets, and customer payment details are NEVER logged.**

## Technical Design

### C# namespace / file structure

```
apps/tamma-elsa/src/Tamma.Data/
  Entities/
    BillingWalletLedger.cs              # NEW — append-only ledger row (CP, TenantId-keyed)
  ControlPlaneDbContext.cs             # MODIFY — add DbSet<BillingWalletLedger>
  TammaModelConfiguration.cs           # MODIFY — table/indexes/CHECK/FK/unique-idempotency
  Migrations/ControlPlane/
    <ts>_AddBillingWalletLedger.cs     # NEW (+ .Designer.cs + snapshot update)
  Repositories/
    IWalletLedgerRepository.cs         # NEW — append + balance + paged history (CP)
    WalletLedgerRepository.cs          # NEW — EF-backed, serialized append

apps/tamma-elsa/src/Tamma.Api/
  Services/Billing/
    IWalletService.cs                  # NEW — top-up intent + grant + refund + read seam
    WalletService.cs                   # NEW — orchestrates Stripe PaymentIntent + ledger
    IWalletCreditService.cs            # NEW — invoice credit-application seam (35-8 consumes)
    WalletCreditService.cs             # NEW — atomic min(balance, amountDue) apply + consume
    WalletEventTypes.cs                # NEW — BILLING.CREDIT.* DCB type constants
    Handlers/
      WalletTopupWebhookHandler.cs     # NEW — IBillingEventHandler (payment_intent.succeeded)
      WalletRefundWebhookHandler.cs    # NEW — IBillingEventHandler (charge.refunded)
    Tasks/
      WalletExpirySweepTaskHandler.cs  # NEW — IPlatformTaskHandler (billing.wallet.expire_sweep)
      WalletExpirySweepScheduler.cs    # NEW — BackgroundService enqueues the sweep task
  Endpoints/Billing/
    WalletEndpoints.cs                 # NEW — tenant (top-up/read) + admin (grant/refund/read)
  Extensions/
    WalletServiceCollectionExtensions.cs # NEW — AddTammaWallet(mode-gated DI)
  Program.cs                           # MODIFY — AddTammaWallet(); map routes (SaaS-gated)
```

> **Stripe.net** is already on `Tamma.Api.csproj` (added by Story 35-1). 35-10 consumes `Stripe.PaymentIntentService` (one-time top-up), `Stripe.CustomerBalanceTransactionService` / invoice credit, and the typed `Stripe.PaymentIntent` / `Stripe.Charge` event objects already delivered by 35-5.

> **Boundary callout — `InvoiceService.cs` is owned by Story 35-8.** The spec listed `apps/tamma-elsa/src/Tamma.Api/Services/Billing/InvoiceService.cs` as a primary component for 35-10. Inspection of the epic shows the invoice mirror (`BillingInvoice`/`BillingInvoiceLine`) and the finalize path are 35-8's responsibility. 35-10 therefore exposes `IWalletCreditService.ApplyToInvoiceAsync` and 35-8's `InvoiceWebhookHandler`/`InvoiceService` **calls it** at finalize — 35-10 does not create or own the invoice entities. If 35-8 lands first, the call site is a one-line injection of `IWalletCreditService`; if 35-10 lands first, the seam is published and 35-8 wires it. This story modifies `InvoiceService.cs` only to add that single call site (a documented integration hook), never the invoice entity/mirror logic.

### Key entity signature

```csharp
// Tamma.Data/Entities/BillingWalletLedger.cs
namespace Tamma.Data.Entities;

/// <summary>
/// Append-only, double-entry-style prepaid-credit ledger row (control plane).
/// Rows are NEVER updated or deleted — corrections are new adjustment/refund/
/// expire rows. Balance is derived (SUM(AmountUsd) per tenant) or read from the
/// latest row's BalanceAfter; it is never stored mutably on the tenant.
/// </summary>
public class BillingWalletLedger
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }                  // FK -> tenants.Id (indexed)
    public string EntryType { get; set; } = null!;      // grant|topup|consume|expire|refund|adjustment (CHECK)
    public decimal AmountUsd { get; set; }              // signed; + credits, - debits
    public decimal BalanceAfter { get; set; }           // running balance snapshot (serialized append)
    public string? Reference { get; set; }              // invoiceId | payment_intent | promo | usage period | lot id
    public string? ReferenceKind { get; set; }          // invoice|payment_intent|promo|usage_period|grant
    public DateTime? ExpiresAt { get; set; }            // only for grant/topup lots
    public string? IdempotencyKey { get; set; }         // UNIQUE NULLS NOT DISTINCT
    public string? Note { get; set; }
    public Guid? CreatedBy { get; set; }                // admin/user for grants
    public DateTime CreatedAt { get; set; }
    public Tenant? Tenant { get; set; }
}
```

### EF model configuration sketch (`TammaModelConfiguration.ConfigureControlPlaneEntities`)

```csharp
modelBuilder.Entity<BillingWalletLedger>(entity =>
{
    entity.ToTable("billing_wallet_ledger", t =>
        t.HasCheckConstraint("ck_billing_wallet_ledger_type",
            "\"EntryType\" IN ('grant','topup','consume','expire','refund','adjustment')"));
    entity.HasKey(e => e.Id);
    entity.Property(e => e.AmountUsd).HasColumnType("numeric(12,4)");
    entity.Property(e => e.BalanceAfter).HasColumnType("numeric(12,4)");
    entity.HasIndex(e => new { e.TenantId, e.CreatedAt });
    entity.HasIndex(e => e.IdempotencyKey)
        .IsUnique()
        .HasFilter("\"IdempotencyKey\" IS NOT NULL"); // UNIQUE NULLS NOT DISTINCT semantics
    // sweep query: open lots with an expiry
    entity.HasIndex(e => new { e.TenantId, e.ExpiresAt })
        .HasFilter("\"ExpiresAt\" IS NOT NULL AND \"EntryType\" IN ('grant','topup')");
    entity.HasOne(e => e.Tenant).WithMany()
        .HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.Cascade);
});
```

### EF migration sketch (`dotnet ef migrations add AddBillingWalletLedger --context ControlPlaneDbContext --output-dir Migrations/ControlPlane`)

```sql
CREATE TABLE billing_wallet_ledger (
    id               UUID PRIMARY KEY,
    tenant_id        UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    entry_type       TEXT NOT NULL,
    amount_usd       NUMERIC(12,4) NOT NULL,
    balance_after    NUMERIC(12,4) NOT NULL,
    reference        TEXT NULL,
    reference_kind   TEXT NULL,
    expires_at       TIMESTAMPTZ NULL,
    idempotency_key  TEXT NULL,
    note             TEXT NULL,
    created_by       UUID NULL,
    created_at       TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_billing_wallet_ledger_type
        CHECK (entry_type IN ('grant','topup','consume','expire','refund','adjustment'))
);
CREATE INDEX ix_wallet_ledger_tenant_created ON billing_wallet_ledger (tenant_id, created_at DESC);
CREATE UNIQUE INDEX ux_wallet_ledger_idem ON billing_wallet_ledger (idempotency_key)
    WHERE idempotency_key IS NOT NULL;
CREATE INDEX ix_wallet_ledger_open_lots ON billing_wallet_ledger (tenant_id, expires_at)
    WHERE expires_at IS NOT NULL AND entry_type IN ('grant','topup');
```

After adding, run `dotnet ef migrations has-pending-model-changes` → expect none (entity config in `TammaModelConfiguration` only).

### Repository seam — `IWalletLedgerRepository`

```csharp
namespace Tamma.Data.Repositories;

public interface IWalletLedgerRepository
{
    /// Serialized append: re-reads live balance, computes BalanceAfter, inserts the row,
    /// all under pg_advisory_xact_lock(hashtextextended(tenantId,0)) + Serializable.
    /// Returns the persisted row. A unique-idempotency-key collision returns the EXISTING
    /// row (idempotent no-op) instead of throwing.
    Task<BillingWalletLedger> AppendAsync(BillingWalletLedger entry, CancellationToken ct = default);

    /// Live balance = SUM(AmountUsd) for the tenant (NOT excluding expiry — that is "available").
    Task<decimal> GetBalanceAsync(Guid tenantId, CancellationToken ct = default);

    /// Available = balance minus the remaining of any lot whose ExpiresAt < now and not yet swept.
    Task<WalletBalanceView> GetBalanceViewAsync(Guid tenantId, DateTime now, CancellationToken ct = default);

    Task<(IReadOnlyList<BillingWalletLedger> Entries, int Total)> ListAsync(
        Guid tenantId, int limit, int offset, CancellationToken ct = default);

    /// Open (unconsumed) lots past expiry across all tenants — drives the sweep.
    Task<IReadOnlyList<WalletExpiredLot>> FindExpiredLotsAsync(DateTime now, int batch, CancellationToken ct = default);
}

public sealed record WalletBalanceView(decimal BalanceUsd, decimal AvailableUsd, decimal ExpiringSoonUsd);
public sealed record WalletExpiredLot(Guid TenantId, Guid LotId, decimal RemainingUsd, DateTime ExpiresAt);
```

`AppendAsync` is the single race-safe write path. Every service (`WalletService`, `WalletCreditService`, the webhook + sweep handlers) appends through it — `BalanceAfter` is therefore always correct because it is computed under the per-tenant advisory lock inside one transaction (AC1, AC7). The advisory-lock + `Serializable` pattern mirrors `TenantMoveService` (`pg_try_advisory_lock(hashtextextended(@tid, 0))`, `TenantMoveService.cs:871`).

### Service seams

```csharp
// Services/Billing/IWalletService.cs
public interface IWalletService
{
    Task<TopupIntentResult> CreateTopupIntentAsync(Guid tenantId, decimal amountUsd, CancellationToken ct = default);
    Task<BillingWalletLedger> GrantAsync(Guid tenantId, decimal amountUsd, DateTime? expiresAt,
        string? note, string? promoCode, Guid grantedBy, CancellationToken ct = default);
    Task<BillingWalletLedger> RefundAsync(Guid tenantId, string chargeOrIntentId, decimal amountUsd,
        Guid refundedBy, CancellationToken ct = default);
    Task<WalletDto> GetWalletAsync(Guid tenantId, int limit, int offset, CancellationToken ct = default);
}
public sealed record TopupIntentResult(string PaymentIntentId, string ClientSecret, decimal AmountUsd);

// Services/Billing/IWalletCreditService.cs  (consumed by Story 35-8 InvoiceService finalize)
public interface IWalletCreditService
{
    /// Apply min(availableBalance, amountDueUsd) to the invoice as a Stripe credit + a 'consume'
    /// ledger row, atomically (serialized per tenant). Idempotent by invoiceId. Returns applied USD.
    Task<decimal> ApplyToInvoiceAsync(Guid tenantId, string invoiceId, string stripeCustomerId,
        decimal amountDueUsd, CancellationToken ct = default);
}
```

`WalletService.CreateTopupIntentAsync` builds a Stripe `PaymentIntent` (`PaymentIntentService.CreateAsync`) for `amountUsd` with `Metadata = { tenantId, purpose = "wallet_topup" }`, deterministic `RequestOptions.IdempotencyKey = "topup-intent-{tenantId}-{amount}-{minuteBucket}"`, and **writes no ledger row** (the credit lands on `payment_intent.succeeded`). `WalletCreditService.ApplyToInvoiceAsync` reduces what Stripe will charge via a customer-balance credit / negative invoice line, then appends the `consume` row through `IWalletLedgerRepository.AppendAsync` with `IdempotencyKey = "consume:{invoiceId}"`.

### Webhook handlers (Story 35-5 `IBillingEventHandler` seam)

```csharp
// Services/Billing/Handlers/WalletTopupWebhookHandler.cs
public sealed class WalletTopupWebhookHandler : IBillingEventHandler
{
    public IReadOnlyCollection<string> HandledEventTypes => new[] { "payment_intent.succeeded" };

    public async Task<BillingFollowup?> HandleAsync(BillingWebhookContext ctx, CancellationToken ct)
    {
        var pi = (Stripe.PaymentIntent)ctx.StripeEvent.Data.Object;
        if (pi.Metadata is null ||
            !pi.Metadata.TryGetValue("purpose", out var purpose) || purpose != "wallet_topup")
            return null; // not a wallet top-up — leave for other handlers / NullBillingEventHandler

        await _ledger.AppendAsync(new BillingWalletLedger {
            TenantId = ctx.TenantId, EntryType = "topup",
            AmountUsd = pi.AmountReceived / 100m,           // Stripe minor units → USD
            Reference = pi.Id, ReferenceKind = "payment_intent",
            IdempotencyKey = $"topup:{pi.Id}",
        }, ct);
        await _events.AppendAsync(WalletEvents.ToppedUp(ctx.TenantId, pi.AmountReceived / 100m, pi.Id));
        return null;
    }
}
```

`WalletRefundWebhookHandler` claims `charge.refunded` and appends a `refund` row keyed `refund:{chargeId}` (only for charges whose originating PaymentIntent metadata is `wallet_topup`). Both are registered through `services.AddBillingEventHandler<T>()` from `WalletServiceCollectionExtensions`. Because 35-5 ships the `NullBillingEventHandler` default, these handlers can be registered and tested independently of 35-8.

### Expiry sweep

`WalletExpirySweepScheduler : BackgroundService` (mode-gated, default daily; `RunOnStartup` gate mirroring `PlatformTaskWorkerOptions`) enqueues a `PlatformQueuedTask { Type = "billing.wallet.expire_sweep" }` via `IPlatformQueuedTaskRepository.EnqueueAsync`. `WalletExpirySweepTaskHandler : IPlatformTaskHandler` (`TaskType = "billing.wallet.expire_sweep"`) calls `IWalletLedgerRepository.FindExpiredLotsAsync(now, batch)` and, per lot, appends an `expire` row (`IdempotencyKey = "expire:{lotId}"`) + emits `BILLING.CREDIT.EXPIRED`. Idempotent: an already-swept lot collides on the unique key (no double-expire). Malformed payload → `PlatformTaskTerminalException`; transient DB error rethrows (worker retry).

### DCB event names (`WalletEventTypes`)

| Event | When | Tags | `TenantId` |
|---|---|---|---|
| `BILLING.CREDIT.GRANTED` | admin grant | `{ tenantId, amountUsd, balanceAfter, reference, entryType:"grant" }` | set |
| `BILLING.CREDIT.TOPPED_UP` | `payment_intent.succeeded` (wallet) | `{ tenantId, amountUsd, balanceAfter, reference:paymentIntentId, entryType:"topup" }` | set |
| `BILLING.CREDIT.CONSUMED` | invoice finalize credit-apply | `{ tenantId, amountUsd, balanceAfter, reference:invoiceId, entryType:"consume" }` | set |
| `BILLING.CREDIT.EXPIRED` | sweep | `{ tenantId, amountUsd, balanceAfter, reference:lotId, entryType:"expire" }` | set |
| `BILLING.CREDIT.REFUNDED` | refund | `{ tenantId, amountUsd, balanceAfter, reference:chargeId, entryType:"refund" }` | set |

All appended via `IEventRepository.AppendAsync(new DomainEvent { Type, TenantId, Tags, Metadata = """{"workflowVersion":"1.0.0","eventSource":"system"}""", Data })`, matching the `OrgEndpoints.EmitTenantEvent` / `BillingEvents` (35-1) shape. A `WalletEvents` static helper builds the rows.

### API shape

```
# tenant routes (SaaS only; behind RequireTenantMembershipFilter on {tenantId})
POST /api/v1/orgs/{tenantId}/billing/wallet/topup      (tenant_owner/tenant_admin; member→403)  → { paymentIntentId, clientSecret, amountUsd }
GET  /api/v1/orgs/{tenantId}/billing/wallet            (tenant_owner/tenant_admin/member-read)   → { balanceUsd, availableUsd, expiringSoonUsd, currency, entries[] }

# admin routes (PlatformOwnerAccess — platform-owner only)
POST /api/v1/admin/billing/wallet/{tenantId}/grant     body { amountUsd, expiresAt?, note?, promoCode? }
POST /api/v1/admin/billing/wallet/{tenantId}/refund    body { chargeId, amountUsd? }
GET  /api/v1/admin/billing/wallet/{tenantId}           (any tenant; same DTO as tenant GET)
```

Tenant routes are registered in the `app.MapGroup("/api/v1/orgs")` block (Program.cs:1512) with `.AddEndpointFilter<RequireTenantMembershipFilter>()`; mutating tenant routes (top-up) gate `member` out inline via the `RequireTenantAdmin(http, out forbid)` helper pattern (`AlertEndpoints.cs:1008`) reading `HttpContext.Items[RequireTenantMembershipFilter.TenantRoleItemKey]`. Admin routes register under `app.MapGroup("/api/v1/admin")` / `/api/admin` with `.RequireAuthorization("PlatformOwnerAccess")`.

### Per-mode + per-tenant handling

| Concern | single-user mode | SaaS mode |
|---|---|---|
| Wallet routes mapped? | No — `NullBillingProvider` (35-1) means no Stripe; tenant + admin wallet routes unmapped. | Yes. |
| Top-up | N/A | tenant_owner/tenant_admin create a Stripe PaymentIntent; `member` → 403. |
| Grant / refund / adjustment | N/A | platform-owner only (`PlatformOwnerAccess`). |
| Credit-apply hook (35-8 finalize) | no-op (`IWalletCreditService` registered as a no-op when billing disabled) | `min(balance, amountDue)` applied before card charge. |
| Read | N/A | tenant_owner/tenant_admin/member-read scoped to `{tenantId}`; admin can read any tenant. |
| Ledger ownership | N/A | one ledger stream per `TenantId`; never cross-tenant. |
| Mode source | `ITammaModeProvider` (`Services/PromptStore/TammaMode.cs`) — process-stable. | same |

### Integration points

- **Story 35-1 foundation** — `BillingCustomer` (tenant ↔ Stripe customer), Stripe.net package + cabinet-resolved Stripe client (`StripeClientFactory`/`IBillingProvider`), `BillingEvents` event-helper precedent, `NullBillingProvider` single-user seam.
- **Story 35-5 webhook ingestion** — registers `WalletTopupWebhookHandler`/`WalletRefundWebhookHandler` via the `IBillingEventHandler` registry; consumes `BillingWebhookContext` (`Stripe.Event`, resolved `TenantId`, raw payload). 35-10 owns no webhook endpoint.
- **Story 35-8 invoicing** — 35-8's `InvoiceService`/`InvoiceWebhookHandler` finalize path calls `IWalletCreditService.ApplyToInvoiceAsync` before charge; the applied amount becomes a `credit` invoice line in 35-8's `BillingInvoiceLine` mirror. 35-10 owns the ledger + the credit-apply seam, not the invoice mirror.
- **Platform task queue (Epic 28)** — `IPlatformQueuedTaskRepository.EnqueueAsync` + a new `IPlatformTaskHandler` (`billing.wallet.expire_sweep`) on the existing `PlatformTaskWorker`.
- **DCB event store (Epic 4)** — `IEventRepository.AppendAsync` (CP `domain_events`), the store `AlertRuleEvaluator` polls.
- **Mode + RBAC** — `ITammaModeProvider`, `RequireTenantMembershipFilter` + `RequireTenantAdmin` helper, `PlatformOwnerAccess` policy.

## Dependencies

**Internal:**
- **Prerequisite — Story 35-1**: `BillingCustomer`, Stripe.net + cabinet Stripe client, `IBillingProvider`/`NullBillingProvider`, `BillingEvents` precedent, single-user no-op.
- **Prerequisite — Story 35-5**: `IBillingEventHandler` registry + `BillingWebhookContext`; routes top-up/refund webhooks. 35-10 declares a dependency on 35-5.
- **Prerequisite — Story 35-8**: invoice finalize path (`InvoiceService`/`InvoiceWebhookHandler`) that invokes `IWalletCreditService.ApplyToInvoiceAsync` before card charge; `BillingInvoiceLine` credit line item.
- **Related — Epic 28**: `PlatformQueuedTask` + `PlatformTaskWorker` for the expiry sweep; `Tenant` FK; `ITammaModeProvider`.
- **Related — Epic 29**: secret cabinet (Stripe key) via 35-1's `StripeClientFactory`.
- **Related — Epic 4/5/23**: DCB events feed the alert evaluator / analytics.

**External:**
- **Stripe.net** SDK (added by 35-1) — `PaymentIntentService` (one-time top-up), `CustomerBalanceTransactionService` / invoice credit, typed `PaymentIntent`/`Charge` event objects.
- A Stripe account with test + live keys; `STRIPE_SECRET_KEY_TEST` for opt-in live integration tests.

## Testing Strategy

**Unit tests** (`apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/`, xUnit; in-memory/SQLite CP context where determinism allows, real Postgres for the concurrency + serialized-append tests):
1. **`WalletLedgerRepositoryTests`** — append each entry type; `BalanceAfter` equals running `SUM(AmountUsd)`; `GetBalanceAsync`/`GetBalanceViewAsync` derive correctly; `availableUsd` excludes expired-but-unswept lots; idempotency-key collision returns the existing row (no duplicate, no throw).
2. **`WalletServiceTests`** — `CreateTopupIntentAsync` calls Stripe `PaymentIntentService.CreateAsync` with `purpose=wallet_topup` metadata + deterministic idempotency key and writes **no** ledger row; `GrantAsync` appends a `grant` row + emits `BILLING.CREDIT.GRANTED`; negative grant rejected; promo double-grant blocked by `promo:{code}:{tenantId}` key.
3. **`WalletTopupWebhookHandlerTests`** — `payment_intent.succeeded` with `wallet_topup` metadata → one `topup` row (`topup:{pi}` key) + `BILLING.CREDIT.TOPPED_UP`; duplicate delivery → no second row; non-wallet PaymentIntent → handler returns null (no row).
4. **`WalletCreditServiceTests`** — finalize apply records `min(balance, amountDue)` as a Stripe credit + a `consume` row (`consume:{invoice}` key) + `BILLING.CREDIT.CONSUMED`; zero balance → zero applied, no row; re-finalization (same invoiceId) → no second consume.
5. **`WalletDoubleSpendTests`** (real Postgres) — seed balance 10 USD; two concurrent `ApplyToInvoiceAsync` for the same tenant against two invoices summing > 10 → total consumed ≤ 10, balance never negative, exactly the right number of `consume` rows; assert the per-tenant advisory lock serializes them.
6. **`WalletExpirySweepTests`** — expired lot with remaining balance → one `expire` row (`expire:{lot}` key) + `BILLING.CREDIT.EXPIRED`; re-run sweep → no second expire; non-expired lot untouched; fully-consumed expired lot → no expire row (remaining is zero).
7. **`WalletRefundTests`** — `charge.refunded` for a wallet top-up → `refund` row + `BILLING.CREDIT.REFUNDED`; admin refund endpoint same; refund that would go negative is clamped + WARN.
8. **`WalletRbacTests`** — tenant `topup` as `member` → 403, as `tenant_admin`/`tenant_owner` → 200; tenant `GET wallet` as `member` → 200 (read); admin `grant`/`refund` requires `PlatformOwnerAccess` (403 for non-platform-admin); cross-tenant route → 404.
9. **`WalletSingleUserSeamTests`** — single-user mode: wallet routes unmapped (404), `IWalletCreditService` is the no-op, zero Stripe calls.

**Integration tests** (`Tamma.Api.Tests/Billing/` via `WebApplicationFactory`, docker-bound CP Postgres run as `sg docker -c "dotnet test ..."`):
10. **Migration** applies + rolls back on real Postgres; `has-pending-model-changes` reports none.
11. **End-to-end top-up** — POST top-up → PaymentIntent (Stripe mocked) → simulate `payment_intent.succeeded` through 35-5's processor → wallet `GET` shows the credited balance + a `topup` entry; replayed webhook → no balance change.
12. **Credit-before-card** — seed credit, run 35-8 finalize (Stripe mocked) → Stripe charged `amountDue − applied`, `consume` row present, `BILLING.CREDIT.CONSUMED` in `DomainEvents`.
13. **Tenant isolation** — tenant A's credit never appears in tenant B's `GET wallet`, never offsets tenant B's invoice; two tenants' ledgers are independent.

**Mocks/fixtures**: Stripe is never live in unit tests — mock `PaymentIntentService`/`CustomerBalanceTransactionService` at the service-interface boundary (35-1's `StripeClientFactory` seam) and feed `payment_intent.succeeded`/`charge.refunded` as fixture `Stripe.Event` objects via 35-5's `BillingWebhookContext`. `IEventRepository`, `IPlatformQueuedTaskRepository`, `IWalletLedgerRepository` are faked. Live-Stripe integration is opt-in behind `STRIPE_SECRET_KEY_TEST` and excluded from default CI.

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/BillingWalletLedger.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (add `DbSet<BillingWalletLedger>`) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config + CHECK + indexes + unique idempotency) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddBillingWalletLedger.cs` | Create (+ Designer + snapshot) |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/IWalletLedgerRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/WalletLedgerRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/IWalletService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/WalletService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/IWalletCreditService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/WalletCreditService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/WalletEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/WalletEvents.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/Handlers/WalletTopupWebhookHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/Handlers/WalletRefundWebhookHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/Tasks/WalletExpirySweepTaskHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/Tasks/WalletExpirySweepScheduler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/WalletEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/WalletServiceCollectionExtensions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/InvoiceService.cs` | Modify (35-8) — add `IWalletCreditService.ApplyToInvoiceAsync` call at finalize |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (AddTammaWallet + map routes, SaaS-gated) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/WalletLedgerRepositoryTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/WalletServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/WalletTopupWebhookHandlerTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/WalletCreditServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/WalletDoubleSpendTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/WalletExpirySweepTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/WalletRefundTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/WalletEndpointsTests.cs` | Create (RBAC + isolation) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md).
2. Searched `.dev/` for billing/Stripe/ledger/secret-cabinet spikes, bugs, findings, decisions.
3. Confirmed Story 35-1 (`BillingCustomer`, Stripe.net, `StripeClientFactory`, `NullBillingProvider`) and Story 35-5 (`IBillingEventHandler` registry, `BillingWebhookContext`) are merged. Coordinate with Story 35-8 on the finalize call site.
4. **Researched the latest Stripe.net API** (`PaymentIntentService.CreateAsync`, customer-balance / invoice credit, `RequestOptions.IdempotencyKey`, minor-unit ⇄ USD conversion) via current docs before writing any SDK call — do not assume method shapes.
5. Planned the TDD (Red-Green-Refactor) cycle for every new type — tests first per the table above.

### Key Design Decisions

- **Append-only ledger, derived balance.** Per AC1, rows are immutable; balance is `SUM(AmountUsd)` (or the latest `BalanceAfter` snapshot). This is the auditable, time-travel-friendly model the DCB philosophy demands — no mutable `tenant.credit_balance` column that can drift from history.
- **`BalanceAfter` computed under a per-tenant advisory lock + `Serializable`.** This is the load-bearing double-spend guard (AC7). The lock is `pg_advisory_xact_lock(hashtextextended(tenantId,0))` (same family as `TenantMoveService.cs:871`); the unique idempotency key (`consume:{invoiceId}`, `topup:{pi}`, `expire:{lot}`) is the belt-and-suspenders second line.
- **Top-up credit lands on `payment_intent.succeeded`, never at intent creation.** A created-but-unpaid PaymentIntent must not credit the wallet — only the 35-5 webhook handler appends the `topup` row, keyed by the PaymentIntent id for at-least-once idempotency (AC2/AC3).
- **Credit application is a seam (`IWalletCreditService`) that 35-8 calls, not an invoice mirror this story owns.** Honors the epic story boundary; 35-10 owns the ledger + apply logic, 35-8 owns the invoice entity and the finalize state machine.
- **Stripe-side credit before card charge.** Applying credit as a Stripe customer-balance transaction / negative invoice line means Stripe itself charges the reduced amount — the local ledger and Stripe stay consistent and the card is never over-charged then refunded.
- **Expiry via the existing `PlatformQueuedTask` worker**, not a bespoke timer — reuses the dead-letter / retry / visibility-timeout machinery (Epic 28).
- **Single-user is a hard no-op** (`NullBillingProvider` seam from 35-1) — zero Stripe surface, routes unmapped, credit-apply no-op.

### Boundary Notes (do not implement sibling-story scope)

- No invoice mirror (`BillingInvoice`/`BillingInvoiceLine`), no dunning, no invoice finalize state machine — that is Story 35-8. 35-10 only **calls** the finalize hook and contributes the `credit` line amount.
- No webhook endpoint / signature verification / dedup row — that is Story 35-5. 35-10 only **registers** `IBillingEventHandler` implementations.
- No Stripe customer mapping / catalog / customer creation — that is Story 35-1.
- No subscription lifecycle (35-4), no payment-method/portal (35-7), no metering/markup (35-3), no quota enforcement/suspension (35-6).
- No tenant-facing wallet UI is mandated here; if a dashboard surface is added, it belongs in `packages/dashboard-user` (tenant) and would consume `GET /api/v1/orgs/{tenantId}/billing/wallet` — out of scope for this backend story.

### Risks and Mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| Double-spend under concurrent invoice finalization | High | `Serializable` + `pg_advisory_xact_lock(tenantId)` serialized append in `IWalletLedgerRepository.AppendAsync`; unique `consume:{invoiceId}` key as backstop; dedicated `WalletDoubleSpendTests` on real Postgres. |
| Duplicate top-up credit from at-least-once webhooks | High | `topup:{paymentIntentId}` UNIQUE idempotency key; collision returns existing row (no second credit). |
| Crediting on an unpaid/failed PaymentIntent | High | Ledger row only on `payment_intent.succeeded` via 35-5 handler — never at intent creation. |
| Stripe ⇄ ledger drift (charge full then refund credit) | Medium | Apply credit as a Stripe customer-balance / negative line **before** charge so Stripe charges the net amount. |
| Refund driving balance negative | Medium | Clamp to zero + WARN; refund cannot exceed remaining credited-from-that-charge balance. |
| Expiry sweep double-expiring a lot | Medium | `expire:{lotId}` UNIQUE key; sweep skips collisions; idempotent re-run. |
| Single-user accidental Stripe coupling | Medium | `NullBillingProvider` seam; routes unmapped; tests assert zero SDK calls. |

### Success Metrics

- [ ] Derived balance always equals `SUM(AmountUsd)` for every tenant (property test across random entry sequences).
- [ ] Concurrent finalization never over-spends (double-spend test green on real Postgres).
- [ ] Duplicate top-up / sweep / consume webhooks are exact no-ops (idempotency tests green).
- [ ] Single-user boot makes 0 Stripe calls; wallet routes return 404.
- [ ] Migration applies + rolls back; `has-pending-model-changes` = none.

## Logging Requirements

- **INFO**: credit granted / topped-up / consumed / expired / refunded (`tenantId`, `entryType`, `amountUsd`, `balanceAfter`, `reference`); top-up PaymentIntent created (`tenantId`, `amountUsd` — never the `client_secret`); sweep run summary (`lotsExpired`, `totalUsd`).
- **DEBUG**: ledger append issued (`entryType`, `idempotencyKey`); advisory lock acquired/released (`tenantId`); idempotency-collision no-op (`idempotencyKey`).
- **WARN**: refund-would-go-negative clamp (`tenantId`, `requestedUsd`, `remainingUsd`); sweep skip-on-collision; promo double-grant blocked.
- **ERROR**: Stripe PaymentIntent create failure (`tenantId`, error class), serialized-append transaction failure, migration failure, `PlatformQueuedTask` dead-lettered.
- **Structured context**: include `{ tenantId, entryType, amountUsd, balanceAfter, idempotencyKey, reference }` where applicable.
- **Credential safety**: NEVER log the Stripe secret key, PaymentIntent `client_secret`, `Stripe-Signature`, or any customer payment details.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
