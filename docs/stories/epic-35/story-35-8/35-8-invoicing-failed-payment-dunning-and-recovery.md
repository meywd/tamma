# Story 35-8: Invoicing, Failed-Payment Dunning & Recovery

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-Phase Development Workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), the `.dev/` knowledge base usage rules, TRACE/DEBUG logging requirements, the Test-Driven Development workflow, and the build/coverage quality gates. Failure to follow this process will result in rework.

## User Story

As a **Tamma tenant owner/admin (and the platform operator)**,
I want my Stripe invoices mirrored locally with a clear line-item breakdown (base plan + metered overage + credits), an invoice history with PDF links, and a robust failed-payment recovery flow that retries, escalates emails, and only suspends after a grace period,
So that I always have an accurate billing record, I get timely warnings before service is cut off, BYOK tenants are still correctly billed the platform/seat fee, and a recovered payment instantly reinstates service — all with a complete DCB audit trail.

## Priority

P0 - Revenue recovery and billing transparency. Failed-payment dunning is the difference between churn and recovered MRR; invoice mirroring is required for the billing portal (35-7) and for the suspension signal that quota enforcement (35-6) reads.

## Acceptance Criteria

1. New control-plane entities `BillingInvoice` (`apps/tamma-elsa/src/Tamma.Data/Entities/BillingInvoice.cs`) and `BillingInvoiceLine` (`apps/tamma-elsa/src/Tamma.Data/Entities/BillingInvoiceLine.cs`) are registered as `DbSet`s on `ControlPlaneDbContext` and configured in `TammaModelConfiguration.ConfigureControlPlaneEntities`. `BillingInvoice` carries `{ Id, TenantId (FK tenants.Id), StripeInvoiceId (UNIQUE, nullable until finalized), StripeSubscriptionId, Status (text domain: draft|open|paid|uncollectible|void), AmountDue, AmountPaid, AmountRemaining, Currency, HostedInvoiceUrl, PdfUrl, PeriodStart, PeriodEnd, AttemptCount, NextPaymentAttempt, CreatedAt, UpdatedAt, FinalizedAt, PaidAt }`. `BillingInvoiceLine` carries `{ Id, InvoiceId (FK), Kind (text domain: base|metered_overage|credit), Description, Quantity, UnitAmount, Amount, Currency }`.
2. `InvoiceService` (`apps/tamma-elsa/src/Tamma.Api/Services/Billing/InvoiceService.cs`, contract `IInvoiceService`) projects Stripe `invoice.*` payloads into the `BillingInvoice` + `BillingInvoiceLine` mirror **idempotently** (upsert keyed on `StripeInvoiceId`), classifying each Stripe line item into `base` / `metered_overage` / `credit` and emitting the corresponding `BILLING.INVOICE.*` DCB event via `IEventRepository.AppendAsync`.
3. Projection is wired into the Story 35-5 webhook pipeline as `InvoiceWebhookHandler : IBillingEventHandler` (registered via `services.AddBillingEventHandler<InvoiceWebhookHandler>()`), claiming `invoice.created`, `invoice.finalized`, `invoice.paid`, `invoice.payment_failed`, and `charge.dispute.created`. 35-8 owns these handlers; 35-5 owns the dispatch seam, dedup, and the `NullBillingEventHandler` fallback (per 35-5 AC9 — 35-8 does **not** create `BillingWebhookEvent` or the endpoint).
4. `GET /api/v1/billing/invoices` (paged, default 50/max 200, newest first) lists the authenticated tenant's invoices; `GET /api/v1/billing/invoices/{id}` returns invoice detail with line items and the `hosted_invoice_url` / `pdf_url`. Both are exposed by a new `InvoiceEndpoints` class under `MemberAccess` with in-handler tenant scoping, so `tenant_owner` / `tenant_admin` / `member` can all **read** their own tenant's invoices and never another tenant's (cross-tenant id → 404, not 403).
5. A `DunningStateMachine` (`apps/tamma-elsa/src/Tamma.Api/Services/Billing/DunningStateMachine.cs`, contract `IDunningStateMachine`) advances a tenant's billing dunning state on `invoice.payment_failed`: `active → past_due → grace → suspended`, driven by an attempt counter and a retry schedule, with each transition scheduled on a `PlatformQueuedTask` (`Type = "billing.dunning.advance"`) processed by `DunningAdvanceTaskHandler : IPlatformTaskHandler`.
6. A later `invoice.paid` (or `invoice.payment_succeeded`) **recovers** the tenant from any non-terminal dunning stage back to `active`, cancels any pending `billing.dunning.advance` task, and emits `BILLING.TENANT.REINSTATED`. Recovery from `suspended` is supported and lifts the suspension.
7. Escalating dunning emails are **enqueued only** through `PlatformEmailOutboxMessage` (system/platform mail) and/or `EmailOutboxMessage` (tenant-scoped mail) — never via direct SMTP/transport calls. Each stage uses a distinct template key (`dunning-past-due`, `dunning-grace`, `dunning-suspended`, `dunning-recovered`) and the email carries the attempt count and next-retry timestamp; these same values are surfaced on the invoice/portal API.
8. On terminal failure after the grace period, the tenant is moved to a `suspended` billing state persisted on a new `BillingDunningState` row (`apps/tamma-elsa/src/Tamma.Data/Entities/BillingDunningState.cs`, one-per-tenant) that Story 35-6's `QuotaService` reads to **hard-block platform-provided usage** (`QuotaDecision.HardBlock` with reason `billing_suspended`). BYOK token usage is **not** blocked by suspension (BYOK tenants are token-exempt per 35-6), but the platform/seat fee remains owed and the invoice stays `open`.
9. BYOK awareness: suspension and dunning apply to **every** tenant with an unpaid platform/seat-fee invoice regardless of `BillingCustomer.BillingMode` (`PlatformProvided | Byok`). The only mode difference at suspend time is what gets hard-blocked: platform-provided usage is blocked; BYOK token calls continue (gated only by seat/feature limits, per 35-6).
10. DCB events are emitted via `IEventRepository.AppendAsync` (CP store, the store `AlertRuleEvaluator` polls): `BILLING.INVOICE.FINALIZED`, `BILLING.INVOICE.PAID`, `BILLING.PAYMENT.FAILED`, `BILLING.DUNNING.ESCALATED`, `BILLING.TENANT.SUSPENDED`, `BILLING.TENANT.REINSTATED`, and `BILLING.INVOICE.DISPUTED`. Tags JSON include `{ tenantId, invoiceId, stage }` (plus `stripeInvoiceId`, `attemptCount` where relevant); Metadata is `{ "workflowVersion": "1.0.0", "eventSource": "system" }`.
11. A dispute/chargeback (`charge.dispute.created`) flips the affected invoice and the tenant to a `flagged` state (`BillingDunningState.Stage = flagged`), emits `BILLING.INVOICE.DISPUTED`, and notifies platform admins by enqueuing a `PlatformEmailOutboxMessage` (template `dispute-opened`) — never auto-suspending on a dispute (a dispute is an operator decision).
12. The dunning retry schedule and grace window are configuration-driven (`Billing:Dunning:RetryDelaysHours` default `[24, 72, 120]`, `Billing:Dunning:GraceHours` default `168`), so the cadence is tunable without code change; the `DunningStateMachine` is pure/deterministic given the schedule + attempt count and the clock is injected (`TimeProvider`) for test determinism.
13. In **single-user mode** (`ITammaModeProvider.Mode == TammaMode.SingleUser` — no `Tamma:TenantSharedSecret` / `ConnectionStrings:ControlPlane`), no Stripe webhooks arrive (35-5 leaves the route unmapped, 35-1 registers `NullBillingProvider`), so `InvoiceEndpoints` short-circuits with a "billing is SaaS-only" response and the dunning machine / task handler are not registered — mirroring the 35-1/35-5 single-user seam.
14. Unit + integration tests (xUnit, `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/`) cover: invoice projection + idempotent upsert on replay; line-item split (`base` / `metered_overage` / `credit`); dunning advance through every stage and recovery from each stage including `suspended`; email-outbox enqueue per stage (asserting **no** direct transport call); `suspended → reinstated` lifting the quota hard-block (35-6 read path); dispute → `flagged` + admin notify (no auto-suspend); and **tenant isolation** (tenant A's invoice/dunning never visible to or mutated for tenant B). Stripe SDK and provider HTTP are mocked.

## Technical Design

### C# namespace / file structure

```
apps/tamma-elsa/src/Tamma.Data/Entities/
  BillingInvoice.cs                    # NEW — CP invoice mirror (StripeInvoiceId UNIQUE)
  BillingInvoiceLine.cs                # NEW — CP invoice line (Kind: base|metered_overage|credit)
  BillingDunningState.cs               # NEW — one-per-tenant dunning/suspension state

apps/tamma-elsa/src/Tamma.Data/
  ControlPlaneDbContext.cs             # MODIFY — DbSet<BillingInvoice/Line/DunningState>
  TammaModelConfiguration.cs           # MODIFY — entity config + indexes + CHECK constraints
  Migrations/ControlPlane/<ts>_BillingInvoicesAndDunning.cs   # NEW — additive migration

apps/tamma-elsa/src/Tamma.Api/Services/Billing/
  IInvoiceService.cs                   # NEW — projection + read seam
  InvoiceService.cs                    # NEW — idempotent upsert + line split + DCB
  IDunningStateMachine.cs              # NEW — pure transition fn + schedule
  DunningStateMachine.cs               # NEW — active->past_due->grace->suspended + recover
  DunningOptions.cs                    # NEW — RetryDelaysHours, GraceHours (IOptions)
  InvoiceWebhookHandler.cs             # NEW — IBillingEventHandler (35-5 registration)
  DunningAdvanceTaskHandler.cs         # NEW — IPlatformTaskHandler ("billing.dunning.advance")
  BillingInvoiceEvents.cs              # NEW — BILLING.INVOICE.*/DUNNING.*/TENANT.* DCB constants
  BillingDunningEmailComposer.cs       # NEW — stage -> template/subject/body (outbox rows)

apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/
  InvoiceEndpoints.cs                  # NEW — GET list + GET detail (MemberAccess, tenant-scoped)

apps/tamma-elsa/src/Tamma.Api/Extensions/
  BillingServiceCollectionExtensions.cs  # MODIFY (35-1 created) — register 35-8 services/handlers
```

### Key entity signatures

```csharp
// Tamma.Data/Entities/BillingInvoice.cs
public class BillingInvoice
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }                    // FK tenants.Id
    public string? StripeInvoiceId { get; set; }          // UNIQUE (filtered: NOT NULL)
    public string? StripeSubscriptionId { get; set; }
    public string Status { get; set; } = "draft";         // draft|open|paid|uncollectible|void (CHECK)
    public long AmountDue { get; set; }                   // minor units (cents)
    public long AmountPaid { get; set; }
    public long AmountRemaining { get; set; }
    public string Currency { get; set; } = "usd";
    public string? HostedInvoiceUrl { get; set; }
    public string? PdfUrl { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? NextPaymentAttempt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? FinalizedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public ICollection<BillingInvoiceLine> Lines { get; set; } = [];
}

// Tamma.Data/Entities/BillingInvoiceLine.cs
public class BillingInvoiceLine
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }                   // FK billing_invoices.Id (cascade)
    public string Kind { get; set; } = "base";            // base|metered_overage|credit (CHECK)
    public string Description { get; set; } = "";
    public long Quantity { get; set; }
    public long UnitAmount { get; set; }
    public long Amount { get; set; }                      // negative for credits
    public string Currency { get; set; } = "usd";
}

// Tamma.Data/Entities/BillingDunningState.cs — one row per tenant
public class BillingDunningState
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }                    // UNIQUE FK tenants.Id
    public string Stage { get; set; } = "active";         // active|past_due|grace|suspended|flagged (CHECK)
    public int FailedAttempts { get; set; }
    public Guid? CurrentInvoiceId { get; set; }           // the invoice driving dunning
    public DateTime? StageEnteredAt { get; set; }
    public DateTime? NextAdvanceAt { get; set; }          // when the scheduled advance task fires
    public Guid? PendingAdvanceTaskId { get; set; }       // PlatformQueuedTask to cancel on recovery
    public DateTime? SuspendedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

### Service signatures

```csharp
// IInvoiceService.cs
public interface IInvoiceService
{
    /// Idempotent upsert of a Stripe invoice into the CP mirror + line split + DCB event.
    Task<BillingInvoice> ProjectAsync(Stripe.Invoice invoice, Guid tenantId, CancellationToken ct);

    Task<(IReadOnlyList<BillingInvoice> Items, int Total)> ListAsync(
        Guid tenantId, int limit, int offset, CancellationToken ct);

    Task<BillingInvoice?> GetAsync(Guid tenantId, Guid invoiceId, CancellationToken ct);
}

// IDunningStateMachine.cs
public interface IDunningStateMachine
{
    /// Pure: given current stage + failed-attempt count + schedule, compute the next stage
    /// and the delay until the next advance (null = terminal/suspend now).
    DunningTransition Next(string currentStage, int failedAttempts, DunningOptions opts);

    /// Apply a payment_failed: bump attempts, transition, schedule the advance task,
    /// enqueue the stage email, emit BILLING.DUNNING.ESCALATED / BILLING.PAYMENT.FAILED.
    Task OnPaymentFailedAsync(Guid tenantId, BillingInvoice invoice, CancellationToken ct);

    /// Apply a payment success: recover to active, cancel pending advance task,
    /// enqueue dunning-recovered email, emit BILLING.TENANT.REINSTATED.
    Task OnPaymentRecoveredAsync(Guid tenantId, BillingInvoice invoice, CancellationToken ct);

    /// Advance one stage (invoked by DunningAdvanceTaskHandler when NextAdvanceAt fires).
    Task AdvanceAsync(Guid tenantId, CancellationToken ct);
}

public sealed record DunningTransition(string NextStage, TimeSpan? DelayUntilNext, bool Suspend);
```

### Line-item classification (Stripe → `Kind`)

`InvoiceService.ProjectAsync` walks `Stripe.Invoice.Lines.Data`:
- A line with a **negative** amount or a discount/credit-note origin → `Kind = "credit"`.
- A line whose price/metadata marks it metered (Stripe `Price.Recurring.UsageType == "metered"`, or our Story 35-3 meter-event line metadata `tamma_meter = true`) → `Kind = "metered_overage"`.
- Everything else (the recurring base subscription price) → `Kind = "base"`.

The mapping is a pure helper (`ClassifyLine`) with its own unit tests; it never trusts a single field exclusively (negative amount wins for credits even on a base price id).

### Dunning state machine

```
                 invoice.payment_failed (attempt 1)
   active ───────────────────────────────────────────▶ past_due
     ▲                                                    │  schedule advance (+RetryDelaysHours[1])
     │ invoice.paid / payment_succeeded                   ▼
     │   (BILLING.TENANT.REINSTATED)                    grace
     │                                                    │  GraceHours elapsed, still unpaid
     └────────────────────────────────────────────────  ▼
                                                       suspended  ──▶ (35-6 hard-blocks platform usage)
```

- Each `invoice.payment_failed` increments `FailedAttempts` and either schedules the next retry (`PlatformQueuedTask Type="billing.dunning.advance"` with `RunAt = now + RetryDelaysHours[idx]`) or, once retries are exhausted, enters `grace`. After `GraceHours` with no successful payment, `AdvanceAsync` transitions `grace → suspended` and emits `BILLING.TENANT.SUSPENDED`.
- `OnPaymentRecoveredAsync` short-circuits the schedule: it cancels `PendingAdvanceTaskId` (via `IPlatformQueuedTaskRepository.DeadLetterAsync`/complete), resets `Stage = active`, clears counters, and emits `BILLING.TENANT.REINSTATED` from any non-terminal stage **and** from `suspended`.
- The transition table itself (`Next`) is pure and clock-free; scheduling and persistence live in `OnPaymentFailedAsync` / `AdvanceAsync`. `TimeProvider` is injected so tests assert grace expiry deterministically.

### DCB event names (`BillingInvoiceEvents`)

```
BILLING.INVOICE.FINALIZED   BILLING.INVOICE.PAID        BILLING.INVOICE.DISPUTED
BILLING.PAYMENT.FAILED
BILLING.DUNNING.ESCALATED
BILLING.TENANT.SUSPENDED    BILLING.TENANT.REINSTATED
```

All appended via `IEventRepository.AppendAsync(new DomainEvent { Type, TenantId, Tags, Metadata, Data })`. `Tags` JSON `{ tenantId, invoiceId, stage }` (+ `stripeInvoiceId`, `attemptCount` where relevant); `Metadata` `{ "workflowVersion": "1.0.0", "eventSource": "system" }`. These are CP-resident system-source events so the `AlertRuleEvaluator` and analytics see them with no extra wiring — a future `BILLING.TENANT.SUSPENDED` alert rule (Epic 5/23) costs nothing here.

### Integration points

- **Story 35-5 (webhook ingestion)** — `InvoiceWebhookHandler : IBillingEventHandler` registered via `services.AddBillingEventHandler<InvoiceWebhookHandler>()`. The processor resolves `TenantId` (from `BillingCustomer`) and passes a `BillingWebhookContext`; the handler returns a `BillingFollowup("billing.dunning.advance", payload)` for heavy/scheduled work so the webhook fast-acks (<2s).
- **Story 35-1 (foundation)** — `BillingCustomer` (tenant ↔ Stripe customer), `BillingMode` enum (`Tamma.Core/Billing/BillingMode.cs`), `NullBillingProvider` single-user seam, Stripe.net.
- **Story 35-4 (subscription)** — `BillingSubscription.{Status, PlanSlug, CurrentPeriodStart/End}` for the period stamped on invoices and the seat/plan context in dunning emails.
- **Story 35-6 (quota enforcement)** — `QuotaService` reads `BillingDunningState.Stage == suspended` → `QuotaDecision.HardBlock(reason="billing_suspended")` on platform-provided usage; BYOK token calls remain exempt. 35-8 calls `IQuotaService.InvalidateAsync(tenantId)` on suspend/reinstate so the block lifts on the next request with no restart.
- **Platform task queue (Epic 28)** — `IPlatformQueuedTaskRepository.EnqueueAsync` for `billing.dunning.advance`; `DunningAdvanceTaskHandler : IPlatformTaskHandler` drained by the existing `PlatformTaskWorker`.
- **Email outbox (Epic 28 / Story 28-6)** — dunning + dispute mail rows inserted into `PlatformEmailOutbox` (`DbSet<PlatformEmailOutboxMessage>`) / `EmailOutbox` (`DbSet<EmailOutboxMessage>`) and drained by the existing `OutboxSmtpSender`/`ResendEmailService` — 35-8 never calls a transport directly.
- **DCB event store** — `IEventRepository.AppendAsync` (CP `domain_events`).

### API shape

```
GET /api/v1/billing/invoices            (MemberAccess; tenant-scoped; ?limit&offset; newest first)
    -> { items: [ { id, status, amountDue, amountPaid, currency, periodStart, periodEnd,
                    attemptCount, nextPaymentAttempt, hostedInvoiceUrl, pdfUrl } ], total }
GET /api/v1/billing/invoices/{id}       (MemberAccess; tenant-scoped; cross-tenant -> 404)
    -> { ...invoice, lines: [ { kind, description, quantity, unitAmount, amount, currency } ],
         dunning: { stage, failedAttempts, nextAdvanceAt } }
```

Tenant scoping: `MemberAccess` authenticates; the handler reads the active tenant id from the principal (`ClaimsPrincipalExtensions`, same path `OrgEndpoints`/`AlertEndpoints` use) and filters every query by it. There is **no** admin cross-tenant invoice route in this story — platform-side invoice inspection rides the 35-5 admin webhook-events endpoint and the DCB stream.

### Per-mode + per-tenant handling

| Concern | single-user mode | SaaS mode |
|---|---|---|
| Webhook handler / dunning machine registered? | No — `NullBillingProvider` (35-1), 35-5 route unmapped; `InvoiceEndpoints` returns "billing is SaaS-only". | Yes. |
| Invoice ownership | N/A (no Stripe invoices) | `BillingInvoice.TenantId`; read-only list for owner/admin/member. |
| Suspension read | N/A | `BillingDunningState` per tenant; 35-6 `QuotaService` reads `Stage == suspended`. |
| Email scope | N/A | tenant dunning mail → `EmailOutbox` (TenantId set); dispute/admin mail → `PlatformEmailOutbox`. |
| DCB event `TenantId` | N/A | always the resolved tenant; never null for a projected/dunning event. |
| Mode source | `ITammaModeProvider` (`Services/PromptStore/TammaMode.cs`) — process-stable. | same |

Tenant isolation is enforced two ways: (1) every `BillingInvoice`/`BillingDunningState` row carries `TenantId` and every read filters by the principal's tenant; (2) every `DomainEvent` is tagged with the resolved `TenantId` and the email outbox rows carry it, so no path lets tenant A read or mutate tenant B's billing state.

## Dependencies

**Internal:**
- **Prerequisite — Story 35-5** (Stripe webhook ingestion): supplies `IBillingEventHandler` dispatch seam, `BillingWebhookContext`, `BillingFollowup`, `AddBillingEventHandler<T>()`, the verified webhook endpoint, and the `BillingCustomer.StripeCustomerId → TenantId` resolution. **Hard blocker** — 35-8 registers handlers into this pipeline; it does not own the endpoint or `BillingWebhookEvent`.
- **Prerequisite — Story 35-3** (BYOK-aware metering): supplies the metered-overage usage that materializes as `metered_overage` invoice lines and the `BillingUsageRollup`/`billing_mode` split that distinguishes BYOK (seat-fee-only) from platform-provided tenants.
- **Prerequisite — Story 35-6** (quota enforcement): the consumer of `BillingDunningState.Stage == suspended`; 35-8 writes the suspension signal and calls `IQuotaService.InvalidateAsync`, 35-6 reads it to hard-block platform usage.
- **Related — Story 35-1**: `BillingCustomer`, `BillingMode` enum, `NullBillingProvider`, Stripe.net.
- **Related — Story 35-4**: `BillingSubscription` for invoice period + plan/seat context.
- **Related — Story 35-7** (billing portal): the portal renders the invoice history this story projects.
- **Related — Epic 28**: `PlatformQueuedTask` + `PlatformTaskWorker` (dunning advance), `PlatformEmailOutboxMessage`/`EmailOutboxMessage` + `OutboxSmtpSender` (escalation mail).
- **Related — Epic 5/23**: DCB events feed the alert evaluator / analytics.

**External:**
- **Stripe.net** SDK (added by 35-1) — typed `Stripe.Invoice` / `Stripe.Charge` / `Stripe.Dispute` event objects.
- A Stripe **webhook signing secret** (consumed by 35-5) and a configured invoice/dunning Stripe setup (smart retries off — Tamma owns the retry cadence locally).
- `STRIPE_SECRET_KEY_TEST` + Stripe test fixtures/CLI for integration tests.

## Testing Strategy

1. **Invoice projection (unit)** — `InvoiceServiceTests`: a Stripe `invoice.finalized` payload projects one `BillingInvoice` + N `BillingInvoiceLine`; replaying the same `StripeInvoiceId` upserts (no duplicate row, no second `BILLING.INVOICE.FINALIZED`); status transitions `open → paid` stamp `PaidAt` and emit `BILLING.INVOICE.PAID`.
2. **Line-item split (unit)** — `ClassifyLineTests`: base price → `base`; metered price / `tamma_meter` metadata → `metered_overage`; negative amount / credit-note → `credit` (negative-amount wins even on a base price id). Mixed invoice splits into the right three buckets and totals reconcile to `AmountDue`.
3. **Dunning transitions (unit, pure)** — `DunningStateMachineTests` with an injected `TimeProvider` and a fixed schedule: `active → past_due → grace → suspended` over the attempt sequence; recovery to `active` from `past_due`, `grace`, **and** `suspended`; grace expiry triggers suspend exactly at `GraceHours`; schedule is config-driven (custom `RetryDelaysHours` honored).
4. **Email-outbox enqueue (unit)** — `BillingDunningEmailComposerTests` + machine tests: each stage inserts a `PlatformEmailOutboxMessage`/`EmailOutboxMessage` row with the right template key, attempt count, and next-retry timestamp; assert via a fake repository that **no** SMTP/transport method is ever called (mock `IEmailTransport` / `OutboxSmtpSender` proves zero direct sends).
5. **Suspend → quota block (integration)** — drive `payment_failed` to `suspended`, then assert `QuotaService` (35-6) returns `HardBlock(billing_suspended)` for a platform-provided call and `Allowed` for a BYOK call (same tenant, `BillingMode = Byok`); after `invoice.paid`, `QuotaService` returns `Allowed` for both (reinstatement + `InvalidateAsync`).
6. **Dispute handling (unit + integration)** — `charge.dispute.created` flips the invoice + `BillingDunningState.Stage = flagged`, emits `BILLING.INVOICE.DISPUTED`, enqueues a `PlatformEmailOutboxMessage` (template `dispute-opened`), and does **not** auto-suspend.
7. **Webhook handler wiring (integration)** — register `InvoiceWebhookHandler` in the 35-5 registry; feed `invoice.payment_failed` through `StripeWebhookProcessor`; assert the mirror update, the `BILLING.PAYMENT.FAILED` DCB event, and the `billing.dunning.advance` `PlatformQueuedTask` enqueue (fast-ack, no inline email send).
8. **Tenant isolation (integration)** — tenant A's invoices never appear in tenant B's `GET /api/v1/billing/invoices`; a cross-tenant `GET /invoices/{id}` returns 404; a `payment_failed` for tenant A never mutates tenant B's `BillingDunningState`; DCB events carry the correct `tenantId`.
9. **RBAC (integration)** — `member` can read invoices; an unauthenticated request is 401; there is no PUT/DELETE surface to over-authorize (read-only story).
10. **Mocks** — Stripe SDK fully mocked (no live calls); email transport stubbed (assert outbox-only); `IPlatformQueuedTaskRepository` faked for unit tests, real for the advance/recovery integration test. Coverage per `CLAUDE.md` (80% line / 75% branch / 85% function); dunning transition logic, line classification, and the suspend/reinstate path are **critical paths → 100%**.

## Estimated Effort

5-6 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/BillingInvoice.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/BillingInvoiceLine.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/BillingDunningState.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (add 3 `DbSet`s) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config, indexes, CHECKs) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_BillingInvoicesAndDunning.cs` | Create (additive) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/IInvoiceService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/InvoiceService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/IDunningStateMachine.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/DunningStateMachine.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/DunningOptions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/InvoiceWebhookHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/DunningAdvanceTaskHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingInvoiceEvents.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingDunningEmailComposer.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/InvoiceEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/BillingServiceCollectionExtensions.cs` | Modify (register 35-8 services/handlers) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map `InvoiceEndpoints`, SaaS-gated) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/InvoiceServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/DunningStateMachineTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/InvoiceWebhookHandlerTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/InvoiceEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/DunningSuspensionIntegrationTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md).
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions (especially any billing/Stripe or outbox findings).
3. Read Story 35-5 (the webhook dispatch seam you register into) and Story 35-6 (the suspension read contract you must honor) end-to-end.
4. Reviewed Stripe Invoices + Dispute object docs and the local-vs-Stripe retry decision (Tamma owns the cadence; Stripe smart-retries off).
5. Planned the TDD approach (Red-Green-Refactor) — start with the pure `DunningStateMachine.Next` transition table and the `ClassifyLine` helper before any DB/webhook wiring.

### Key Design Decisions

- **35-8 owns invoice + dunning, 35-5 owns the pipe.** Per 35-5 AC9, this story registers `IBillingEventHandler` implementations and creates `BillingInvoice`/`BillingInvoiceLine`/`BillingDunningState`; it never touches `BillingWebhookEvent`, the webhook endpoint, signature verification, or dedup. Boundary respected.
- **Tamma owns the retry cadence, not Stripe.** Stripe smart-retries are disabled; the `DunningStateMachine` + `billing.dunning.advance` `PlatformQueuedTask` schedule is the single source of truth for the retry/escalation/grace timeline, so the portal can surface the exact next-retry time and the cadence is config-tunable.
- **Pure transition core.** `DunningStateMachine.Next` is a clock-free pure function; all I/O (scheduling, email enqueue, DCB, persistence) is in the imperative shell. This makes the 100%-coverage critical path trivially testable with an injected `TimeProvider`.
- **Suspension is a read-signal, not an enforcement.** 35-8 writes `BillingDunningState.Stage = suspended`; 35-6's `QuotaService` does the actual hard-block. 35-8 must not block requests itself (no enforcement code in the LLM/dispatch path) — that is 35-6's boundary. It only calls `IQuotaService.InvalidateAsync` so the read is fresh.
- **Outbox-only mail.** Every dunning/dispute email is an `INSERT` into `PlatformEmailOutbox`/`EmailOutbox`; the existing `OutboxSmtpSender`/`ResendEmailService` deliver. This keeps delivery idempotent, retried, and PII-safe (bodies never logged), and a unit test asserts zero direct transport calls.
- **BYOK still owes the seat fee.** Suspension applies to any tenant with an unpaid platform/seat-fee invoice regardless of mode; the only mode-difference is what gets blocked (platform usage blocked, BYOK token calls continue).

### Money Handling

- All amounts are stored in **minor units** (`long` cents) exactly as Stripe returns them — never `decimal` dollars, never float. Currency travels with every row. Credit lines are **negative** amounts; the projected `AmountDue` reconciles to the sum of line `Amount`s.

### Idempotency & Reconciliation

- Projection upserts on `StripeInvoiceId`; a webhook replay (Stripe at-least-once) is a no-op beyond a timestamp bump. The 35-5 `BillingWebhookEvent` dedup is the first line of defense; the upsert is the second.
- A `payment_failed` whose attempt count is already reflected (replayed webhook) does not double-advance dunning — `DunningStateMachine.OnPaymentFailedAsync` is idempotent on `(invoiceId, attemptCount)`.

### Graceful Degradation

- If the CP DB is briefly unavailable when a webhook arrives, the 35-5 processor records the failure and the admin replay endpoint re-drives it; 35-8 handlers must be safe to re-run.
- Email-outbox enqueue failures must not block the webhook ack or the dunning transition (the transition is the source of truth; the email is best-effort and the outbox itself retries).

## Logging Requirements

- **INFO**: invoice projected (`invoiceId`, `status`, `lineCount`), dunning stage transition (`tenantId`, `fromStage`, `toStage`, `attemptCount`), tenant suspended/reinstated, dispute opened.
- **DEBUG**: line classification decision (`stripeLineId`, `kind`), advance task scheduled (`runAt`), email-outbox row enqueued (`template`, `outboxId` — never recipient/body).
- **WARN**: invoice projection for a customer with no `BillingCustomer` match (skipped), dunning advance fired for an already-recovered tenant (no-op), email-outbox enqueue failed (retry on next cycle).
- **ERROR**: DCB append failure, suspension write failure, `QuotaService.InvalidateAsync` failure after a suspend/reinstate (block may be stale until cache TTL).
- **Structured context**: include `{ tenantId, invoiceId, stripeInvoiceId, stage, attemptCount }` where applicable.
- **Credential / PII safety**: NEVER log Stripe secret keys, `hosted_invoice_url`/`pdf_url` query tokens, recipient addresses, or email bodies. Scrub via `CredentialRedactor.Clean` before any error log.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
