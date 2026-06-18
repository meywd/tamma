# Story 35-12: Billing Audit, Reconciliation & Revenue Analytics

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-Phase Development Workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), the `.dev/` knowledge-base usage rules, TRACE/DEBUG logging requirements, the Test-Driven Development workflow, the 100%-critical-path coverage target, and build/quality-gate enforcement. **Failure to follow this process will result in rework.**

## User Story

As a **Tamma platform operator (and finance/compliance stakeholder)**,
I want every `BILLING.*` event projected into a queryable audit timeline, a daily reconciliation job that proves the local billing mirrors agree with Stripe and flags drift, and revenue analytics (MRR/ARR, churn, BYOK-vs-platform split, realized margin) computed on the existing platform-analytics substrate,
so that billing is fully auditable, operationally trustworthy, and reportable — drift is caught before it becomes a revenue leak or a customer dispute, and I can answer "what is our MRR, churn, and margin" without exporting to Stripe.

## Priority

P1 — The cross-cutting integrity + reporting layer for Epic 35. The metering (35-3), subscription (35-4), webhook projection (35-5), dunning (35-8), and wallet (35-10) stories each own their own mirror and their own narrow DCB events; this story is the only place that proves those mirrors are *correct* against Stripe and turns the audit stream into revenue intelligence. It is the safety net the whole epic relies on once tenants are being charged real money.

## Acceptance Criteria

1. A **billing audit/timeline read model** is derived on demand from the CP `DomainEvent` rows whose `Type` matches `BILLING.%` (the events already appended by Stories 35-1/35-3/35-4/35-5/35-8/35-10 via `IEventRepository.AppendAsync`). A new `IBillingAuditService` (`apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingAuditService.cs`) exposes `GetTenantTimelineAsync(tenantId, filter, ct)` and `GetPlatformTimelineAsync(filter, ct)` returning chronological, paged `BillingTimelineEntry` records (newest-first by `(CreatedAt, SequenceNumber)`); filters: `eventType` prefix, `from`/`to`, `cursor`/`limit` (default 50, max 200).
2. `GET /api/v1/orgs/{tenantId}/billing/timeline` (SaaS: `MemberAccess` group + `RequireTenantMembershipFilter`, any tenant member may read; single-user: the sole user) returns the per-tenant billing history; `GET /api/v1/admin/billing/timeline` (`OwnerAccess`) returns the platform-wide view across all tenants (and platform-scoped, `TenantId IS NULL`, billing events). A tenant timeline query is hard-scoped to the caller's `TenantId` (from `ITenantContext`, never a spoofable route param mismatch) so tenant A can never read tenant B's billing events.
3. Each `BillingTimelineEntry` projects only **non-sensitive** fields: `eventType`, `createdAt`, `sequenceNumber`, decoded `tags` (`tenantId`, `stripeCustomerId`, `billingMode`, `stripeObjectId`, `invoiceId`, `stage` where present), and a small whitelisted `summary` (`amountUsd`, `currency`, `status`, `last4`). No raw card numbers, no full PANs, no API keys, no Stripe secrets, and no raw webhook payloads ever appear in the projection (AC verified by a redaction test asserting only whitelisted keys survive).
4. A new `BillingReconciliationTaskHandler` (`apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingReconciliationTaskHandler.cs`) implementing `IPlatformTaskHandler` with `TaskType => "billing.reconcile"` runs **daily** (re-enqueued via the self-rescheduling `PlatformQueuedTask` pattern, cadence configurable via `Billing:Reconciliation:IntervalHours` default `24`) and, for each active `BillingCustomer`, cross-checks the four local mirrors against Stripe: `BillingSubscription` (35-4) vs Stripe `Subscriptions.List`, `BillingInvoice`/`BillingInvoiceLine` (35-8) vs Stripe `Invoices.List`, `BillingUsageRollup` (35-3) vs Stripe `Billing.Meters.ListEventSummaries`, and `BillingWalletLedger` (35-10) balance vs the Stripe credit-grant/customer-balance source.
5. On any mismatch the handler emits a `BILLING.RECONCILIATION.DRIFT_DETECTED` DCB event (via `IEventRepository.AppendAsync`, CP store) with JSONB `tags = { tenantId, stripeCustomerId, mirror, billing_mode? }` and `data = { mirror, field, local, stripe, deltaUsd?, deltaCount? }`, plus a WARN structured log; a clean per-customer pass emits a single per-run `BILLING.RECONCILIATION.COMPLETED` platform event (`TenantId IS NULL`) carrying `{ customersChecked, mirrorsChecked, driftCount }`. The handler is **fail-isolated per customer and per mirror**: one customer's Stripe error or one mirror's exception is caught, logged, counted, and does not abort the run.
6. The reconciliation handler **does not re-implement the per-meter usage check** owned by Story 35-3 (`billing.usage_reconcile` → `BILLING.USAGE.RECONCILIATION_MISMATCH`); for the usage mirror it consumes the existing `IUsageMeteringService`/35-3 drift signal (reads the latest `BILLING.USAGE.RECONCILIATION_MISMATCH` events and the local rollup) and rolls them up into the unified `BILLING.RECONCILIATION.DRIFT_DETECTED` view with `mirror = "usage"`, rather than calling `Billing.Meters.ListEventSummaries` a second time itself.
7. **Revenue analytics** extend the existing `PlatformAnalyticsHourly` fact table + `ComputePlatformRollupActivity` substrate: `ComputePlatformRollupActivity.ComputeAsync` is augmented to additionally compute and persist a daily revenue snapshot onto a **new** `BillingRevenueDaily` CP entity (`apps/tamma-elsa/src/Tamma.Data/Entities/BillingRevenueDaily.cs`) carrying, per day (`Day` UTC midnight, `TenantId IS NULL` platform-wide): `MrrUsd`, `ArrUsd`, `ActiveSubscriptions`, `TrialingSubscriptions`, `PastDueSubscriptions`, `SuspendedSubscriptions`, `LogoChurnCount`, `RevenueChurnUsd`, `ByokRevenueUsd`, `PlatformRevenueUsd`, `PlatformUsageCostUsd`, `PlatformUsageMarginUsd`, with a partial unique index on `(Day, TenantId)` mirroring `PlatformAnalyticsHourly`'s idempotency pattern (replay overwrites).
8. MRR is computed from the `BillingSubscription` (35-4) mirror: sum the monthly-normalized recurring price (seat count × per-seat plan price from 35-1's `BillingPlanPrice`, plus flat plan fee) over subscriptions in `active`/`trialing`/`past_due` status, normalizing annual plans to `/12`; ARR = MRR × 12. The BYOK-vs-platform split classifies each subscription's contribution by its `BillingCustomer.BillingMode` (`PlatformProvided` vs `Byok`) → `PlatformRevenueUsd` / `ByokRevenueUsd`. **No new pricing or markup math is implemented here** — per-seat/plan prices come from 35-1's `BillingPlanPrice` and platform-usage cost/sell come from the already-priced `BillingUsageRollup` (`PlatformCostUsd`, `BillableAmountUsd` from 35-3, which in turn calls 34-5's `IUsagePricingEngine`).
9. Realized margin on platform-provided usage = `Σ BillingUsageRollup.BillableAmountUsd − Σ BillingUsageRollup.PlatformCostUsd` for the period over `PlatformProvided` tenants → `PlatformUsageMarginUsd`; `PlatformUsageCostUsd = Σ PlatformCostUsd`. Logo churn = count of subscriptions that transitioned to `canceled` in the day window (read from `BILLING.SUBSCRIPTION.CANCELED` events); revenue churn = the MRR those canceled subscriptions represented at cancellation.
10. `GET /api/v1/admin/billing/metrics` (`OwnerAccess`, mounted in `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs` — the real analytics endpoint file, NOT a `Admin/` sub-path) returns the revenue analytics for a requested window (`?from&to`, default current calendar month): `{ mrrUsd, arrUsd, activeSubscriptions, trialingSubscriptions, pastDueSubscriptions, suspendedSubscriptions, logoChurnCount, revenueChurnUsd, byokRevenueUsd, platformRevenueUsd, platformUsageCostUsd, platformUsageMarginUsd, periodStart, periodEnd, generatedAt }`, served from `BillingRevenueDaily` (no live fan-out). The returned `platformRevenueUsd + byokRevenueUsd` figures **reconcile to the sum of per-tenant `BillingInvoice` totals** for the same period within a documented tolerance (asserted by an integration test).
11. **Alerting hook**: a new built-in alert rule `billing-reconciliation-drift` (severity `warning`, `EventType = "BILLING.RECONCILIATION.DRIFT_DETECTED"`, predicate `{"op":"always"}`, `ThrottleSeconds = 3600`) is added to `BuiltInAlertRules.All` so drift fires through the existing Story 5.6 `AlertRuleEvaluator` → `IAlertSink` path with no manual rule setup; tenant-scoped drift (event carries `TenantId`) raises a tenant-feed alert, platform-scoped raises an admin-feed alert. A second built-in `billing-dunning-spike` (severity `critical`, `EventType = "BILLING.DUNNING.ESCALATED"`, predicate `{"op":"count_gte","window_seconds":3600,"threshold":5}`) and `billing-meter-flush-backlog` (severity `warning`, `EventType = "BILLING.USAGE.FLUSH_FAILED"`, `{"op":"count_gte","window_seconds":1800,"threshold":10}`) cover dunning spikes and meter-flush backlog over threshold. Built-ins ship with empty `ChannelIds` per the existing convention (no auto-spam).
12. **Consistent DCB tags for audit/compliance**: this story's events and the audit projection assume every `BILLING.*` event already carries the canonical tags (`tenantId`, `stripeCustomerId` where applicable, `billing_mode`). Where the audit projection observes a `BILLING.*` event **missing** a `tenantId` or `stripeCustomerId` tag that should be present (a sibling-story emission bug), it surfaces the event in the timeline with a `tagGap = true` marker and WARN-logs once — making a tagging regression visible to compliance export rather than silently dropping the row.
13. **Per-mode handling**: in single-user mode (`ITammaModeProvider == SingleUser`) the reconciliation handler and revenue snapshot are **not registered** (no Stripe, no `BillingCustomer` rows), `GET /api/v1/admin/billing/metrics` and the admin timeline return an empty/zeroed payload (or 404 for the org timeline, mirroring 35-3's seam), and no Stripe calls are ever made; the sole user may still read their own billing timeline if any `BILLING.*` events exist. In SaaS mode all surfaces are mounted and tenant-scoped per AC 2.
14. **Tenant isolation** is covered by tests: a tenant timeline request never returns another tenant's or a platform-scoped billing event; the reconciliation handler iterates `BillingCustomer` rows and never tags one tenant's drift event with another tenant's id; the admin metrics/timeline endpoints require `OwnerAccess` and 403 a tenant-role caller.
15. Unit + integration tests cover: the audit projection query (ordering, paging, prefix filter, redaction whitelist, `tagGap` marker), reconciliation drift detection across **each** of the four mirrors (subscription/invoice/usage/wallet) including the clean-pass and per-mirror fail-isolation paths, MRR/ARR/churn/margin math against a seeded mirror set, the BYOK-vs-platform split correctness, the `metrics`-reconciles-to-invoices invariant, alert emission for each new built-in rule, and the single-user no-op seam.

## Technical Design

### Boundary statement (honor the epic split)

This story is the **integrity + reporting** layer. It **reads** the mirrors and events the sibling stories own; it **owns** the cross-mirror reconciliation, the billing audit projection, and the revenue-analytics computation/snapshot.

| Concern | Owner | This story's relationship |
|---|---|---|
| `BillingCustomer` (tenant↔Stripe, `BillingMode`), `BillingPlanPrice` | 35-1 / 35-2 | reads (customer iteration, MRR per-seat price, mode split) |
| `BillingUsageRollup` (`PlatformCostUsd`, `BillableAmountUsd`), `billing.usage_reconcile` | 35-3 | reads for margin + consumes its usage-drift signal (does NOT re-run the per-meter check) |
| `BillingSubscription` (status, seats, plan, period) | 35-4 | reads for MRR/churn + subscription-mirror reconciliation |
| `BillingWebhookEvent` + the `BILLING.*` DCB emissions | 35-5 | the audit projection's source stream |
| `BillingInvoice`/`BillingInvoiceLine`, `BillingDunningState`, dunning events | 35-8 | reads for invoice reconciliation + the `metrics`-reconciles-to-invoices invariant + dunning-spike alert |
| `BillingWalletLedger` | 35-10 | reads for wallet-balance reconciliation |
| Billing **dashboards** + `GET /api/v1/admin/billing/overview`/`health` (thin per-tenant table, sum/group) | 35-11 | **NOT this story** — 35-11 renders; this story produces the MRR/churn/margin numbers its admin console consumes. No React/dashboard files are created here. |
| `IUsagePricingEngine` (cost→price markup) | 34-5 | never re-implemented; margin is read from already-priced rollup columns |

**No markup/pricing math, no dashboard code, no second usage-meter reconciliation.** If you find yourself multiplying a cost by a margin, or editing `packages/dashboard*`, or calling `Billing.Meters.ListEventSummaries` for the usage mirror, stop — those belong to 34-5, 35-11, and 35-3 respectively.

### C# namespace / file structure

```
apps/tamma-elsa/src/Tamma.Data/
  Entities/
    BillingRevenueDaily.cs                 # NEW — CP daily revenue snapshot (platform-wide, idempotent on (Day, TenantId))
  ControlPlaneDbContext.cs                  # MODIFY — DbSet<BillingRevenueDaily>
  TammaModelConfiguration.cs               # MODIFY — entity config + (Day, TenantId) partial unique indexes + precision
  Migrations/ControlPlane/<ts>_AddBillingRevenueDaily.cs   # NEW — additive migration

apps/tamma-elsa/src/Tamma.Api/Services/Billing/
  IBillingAuditService.cs                   # NEW — timeline read model port
  BillingAuditService.cs                    # NEW — projects BILLING.% DomainEvents → BillingTimelineEntry (redacted)
  BillingTimelineEntry.cs                   # NEW — DTO + filter record
  IBillingRevenueService.cs                 # NEW — revenue metrics read port (over BillingRevenueDaily)
  BillingRevenueService.cs                  # NEW — windowed read + the snapshot computation helper
  BillingReconciliationTaskHandler.cs       # NEW — IPlatformTaskHandler "billing.reconcile"
  BillingReconciliationOptions.cs           # NEW — IntervalHours, drift tolerances per mirror
  BillingMirrorReconciler.cs                # NEW — pure-ish per-mirror compare helpers (subscription/invoice/usage/wallet)
  BillingAuditEventTypes.cs                 # NEW — RECONCILIATION.* DCB type constants

apps/tamma-elsa/src/Tamma.Activities/Analytics/
  ComputePlatformRollupActivity.cs          # MODIFY — also write the daily BillingRevenueDaily snapshot (SaaS only)

apps/tamma-elsa/src/Tamma.Api/Endpoints/
  AdminAnalyticsEndpoints.cs                # MODIFY — add GetBillingMetrics + GetBillingTimeline (OwnerAccess)
apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/
  BillingTimelineEndpoints.cs               # NEW — GET /api/v1/orgs/{tenantId}/billing/timeline (tenant)

apps/tamma-elsa/src/Tamma.Api/Services/Alerts/Rules/
  BuiltInAlertRules.cs                      # MODIFY — add 3 billing built-ins (drift / dunning-spike / flush-backlog)

apps/tamma-elsa/src/Tamma.Api/Extensions/
  BillingAuditServiceCollectionExtensions.cs  # NEW — wire audit/revenue services + reconcile handler (SaaS only)
apps/tamma-elsa/src/Tamma.Api/Program.cs       # MODIFY — call AddBillingAuditAndAnalytics(); map endpoints
```

### Entity: `BillingRevenueDaily` (CP-resident)

```csharp
namespace Tamma.Data.Entities;

/// <summary>
/// Story 35-12 — control-plane daily revenue snapshot. Platform-wide row
/// (TenantId == null) carries fleet MRR/ARR/churn/margin; a future
/// per-tenant breakdown can reuse the same table with TenantId set. Lives
/// on the control plane next to PlatformAnalyticsHourly so a single SELECT
/// answers "what is our MRR / margin this month" without per-tenant fan-out.
/// Idempotent upsert keyed by (Day, TenantId) — a replay of a day overwrites.
/// </summary>
public class BillingRevenueDaily
{
    public Guid Id { get; set; }
    public DateTime Day { get; set; }            // UTC midnight bucket
    public Guid? TenantId { get; set; }          // null = platform-wide

    public decimal MrrUsd { get; set; }
    public decimal ArrUsd { get; set; }

    public int ActiveSubscriptions { get; set; }
    public int TrialingSubscriptions { get; set; }
    public int PastDueSubscriptions { get; set; }
    public int SuspendedSubscriptions { get; set; }

    public int LogoChurnCount { get; set; }
    public decimal RevenueChurnUsd { get; set; }

    public decimal ByokRevenueUsd { get; set; }      // seat/plan fee from Byok tenants
    public decimal PlatformRevenueUsd { get; set; }  // seat/plan fee + billable usage from PlatformProvided tenants
    public decimal PlatformUsageCostUsd { get; set; }   // Σ BillingUsageRollup.PlatformCostUsd
    public decimal PlatformUsageMarginUsd { get; set; } // Σ (BillableAmountUsd - PlatformCostUsd)

    public DateTime ComputedAt { get; set; }
}
```

EF config (in `TammaModelConfiguration.ConfigureControlPlaneEntities`):

```csharp
modelBuilder.Entity<BillingRevenueDaily>(e =>
{
    e.ToTable("billing_revenue_daily");
    e.HasKey(x => x.Id);
    e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
    // Two partial unique indexes mirror PlatformAnalyticsHourly's (Hour, TenantId) idempotency.
    e.HasIndex(x => x.Day).HasFilter("\"TenantId\" IS NULL").IsUnique()
        .HasDatabaseName("UX_billing_revenue_daily_Day_PlatformWide");
    e.HasIndex(x => new { x.Day, x.TenantId }).HasFilter("\"TenantId\" IS NOT NULL").IsUnique()
        .HasDatabaseName("UX_billing_revenue_daily_Day_Tenant");
    foreach (var p in new[] { nameof(BillingRevenueDaily.MrrUsd), nameof(BillingRevenueDaily.ArrUsd),
        nameof(BillingRevenueDaily.RevenueChurnUsd), nameof(BillingRevenueDaily.ByokRevenueUsd),
        nameof(BillingRevenueDaily.PlatformRevenueUsd), nameof(BillingRevenueDaily.PlatformUsageCostUsd),
        nameof(BillingRevenueDaily.PlatformUsageMarginUsd) })
        e.Property(p).HasPrecision(20, 4);
});
```

### Audit timeline service

```csharp
public interface IBillingAuditService
{
    Task<BillingTimelinePage> GetTenantTimelineAsync(
        Guid tenantId, BillingTimelineFilter filter, CancellationToken ct = default);

    Task<BillingTimelinePage> GetPlatformTimelineAsync(
        BillingTimelineFilter filter, CancellationToken ct = default);
}

public sealed record BillingTimelineFilter(
    string? EventTypePrefix = "BILLING.",
    DateTime? From = null,
    DateTime? To = null,
    long? Cursor = null,          // SequenceNumber cursor (newest-first)
    int Limit = 50);             // clamped 1..200

public sealed record BillingTimelineEntry(
    string EventType,
    DateTime CreatedAt,
    long SequenceNumber,
    Guid? TenantId,
    string? StripeCustomerId,
    string? BillingMode,
    string? StripeObjectId,
    IReadOnlyDictionary<string, object?> Summary, // whitelisted: amountUsd, currency, status, last4, invoiceId, stage
    bool TagGap);                // true when an expected tenantId/stripeCustomerId tag is missing

public sealed record BillingTimelinePage(
    IReadOnlyList<BillingTimelineEntry> Entries, long? NextCursor, int Total);
```

`GetTenantTimelineAsync` reads `DomainEvent` rows where `TenantId == tenantId AND Type LIKE 'BILLING.%'` via `IEventRepository` (or a direct `ControlPlaneDbContext.DomainEvents` query — same store), ordered by `SequenceNumber DESC`, deserializes `Tags`/`Data` JSON, and projects **only** the whitelisted summary keys through a `BillingAuditRedactor` (a `private static readonly HashSet<string> _allowedSummaryKeys`). Anything not on the whitelist is dropped — the redaction test asserts that injecting a `cardNumber`/`apiKey`/`rawPayload` key into a `BILLING.*` event's `data` does not survive the projection.

### Reconciliation handler

`BillingReconciliationTaskHandler : IPlatformTaskHandler`, `TaskType => "billing.reconcile"`. `HandleAsync(PlatformQueuedTask task, CancellationToken ct)`:

1. If `ITammaModeProvider == SingleUser` → no-op (handler is not even registered, but defensive guard stays).
2. Load active `BillingCustomer` rows (CP). For each customer, run four mirror checks via `BillingMirrorReconciler`, each wrapped in its own try/catch (per-mirror fail isolation):
   - **subscription**: `BillingSubscription` rows vs `IBillingProvider`/Stripe `Subscriptions.List(customer)` → compare status + period + seats.
   - **invoice**: `BillingInvoice` rows vs Stripe `Invoices.List(customer)` → compare count + status + `AmountDue`/`AmountPaid` totals within `Billing:Reconciliation:InvoiceToleranceUsd`.
   - **usage**: consume 35-3's signal — read the most recent `BILLING.USAGE.RECONCILIATION_MISMATCH` events for the customer + the current `BillingUsageRollup`; surface as `mirror = "usage"` drift (no second meter-summary call).
   - **wallet**: `BillingWalletLedger` running balance vs the Stripe credit-grant/customer-balance value within `Billing:Reconciliation:WalletToleranceUsd`.
3. On any mismatch → `IEventRepository.AppendAsync(new DomainEvent { Type = "BILLING.RECONCILIATION.DRIFT_DETECTED", TenantId = customer.TenantId, Tags = {...}, Data = {...} })` + WARN log.
4. After all customers → append a platform `BILLING.RECONCILIATION.COMPLETED` event (`TenantId = null`) with run totals.
5. Self-reschedule: enqueue the next `billing.reconcile` `PlatformQueuedTask` at `now + IntervalHours` (rides the existing `PlatformTaskWorker` loop, mirroring 35-3's `billing.meter_flush` self-rescheduling pattern). A transient Stripe/DB error throws (worker retries the task); a structurally impossible run throws `PlatformTaskTerminalException`.

### Revenue snapshot (extends `ComputePlatformRollupActivity`)

`ComputePlatformRollupActivity.ComputeAsync` (the existing static pure-DI entry point, currently writes `PlatformAnalyticsHourly` for `TenantId = null`) gains a **SaaS-mode-gated** tail step that, once per day (on the top-of-day hour), computes and upserts a `BillingRevenueDaily` platform row via `IBillingRevenueService.ComputeDailySnapshotAsync(day, ct)`:

- **MRR**: `Σ` over `BillingSubscription` in (`active`,`trialing`,`past_due`) of `monthlyNormalizedPrice(sub)` where `monthlyNormalizedPrice = flatPlanFee + seats × perSeatPrice` (prices from 35-1 `BillingPlanPrice`; annual plans `/12`). Split each contribution by `BillingCustomer.BillingMode` → `PlatformRevenueUsd` / `ByokRevenueUsd`.
- **ARR** = MRR × 12.
- **status counts** from the `BillingSubscription` mirror (`active`/`trialing`/`past_due`) + `BillingDunningState.Stage == suspended` for `SuspendedSubscriptions`.
- **churn**: `LogoChurnCount` = `BILLING.SUBSCRIPTION.CANCELED` events in `[day, day+1)`; `RevenueChurnUsd` = the MRR those subs represented (read sub mirror at cancellation / event data).
- **margin**: `PlatformUsageCostUsd = Σ BillingUsageRollup.PlatformCostUsd`, `PlatformUsageMarginUsd = Σ (BillableAmountUsd − PlatformCostUsd)` over `PlatformProvided` tenants for the period; add `BillingUsageRollup.BillableAmountUsd` into `PlatformRevenueUsd`.

Keeping the computation in `IBillingRevenueService` (not inline in the activity) lets the unit tests exercise the math without an Elsa execution context (same split `RunAsync` vs `ComputeAsync` pattern the activity already uses). The activity only orchestrates; the service owns the SQL + math.

### DCB event names (`AGGREGATE.ACTION.STATUS`) — owned by this story

```
BILLING.RECONCILIATION.DRIFT_DETECTED   # tenant-scoped (TenantId set) per drifting mirror
BILLING.RECONCILIATION.COMPLETED        # platform event (TenantId null), per run summary
```

Consumed (read-only) from sibling stories: `BILLING.USAGE.RECONCILIATION_MISMATCH`, `BILLING.USAGE.FLUSH_FAILED` (35-3); `BILLING.SUBSCRIPTION.CANCELED`, `BILLING.SUBSCRIPTION.UPDATED` (35-4); `BILLING.INVOICE.*`, `BILLING.DUNNING.ESCALATED`, `BILLING.TENANT.SUSPENDED` (35-8); `BILLING.CREDIT.*` (35-10); `BILLING.CUSTOMER.CREATED` (35-1). All `BILLING.%`-prefixed events feed the audit timeline projection.

### Alert built-ins (added to `BuiltInAlertRules.All`)

```csharp
new BuiltInAlertRuleSpec(
    BuiltInKey: "billing-reconciliation-drift",
    Name: "billing-reconciliation-drift",
    Description: "Local billing mirror drifted from Stripe for tenant {tenantId} (mirror {mirror}).",
    Severity: AlertSeverity.Warning,
    EventType: "BILLING.RECONCILIATION.DRIFT_DETECTED",
    Predicate: """{"op":"always"}""",
    ThrottleSeconds: 3600),

new BuiltInAlertRuleSpec(
    BuiltInKey: "billing-dunning-spike",
    Name: "billing-dunning-spike",
    Description: "5+ dunning escalations in an hour — a payment-processing or pricing regression may be affecting many tenants.",
    Severity: AlertSeverity.Critical,
    EventType: "BILLING.DUNNING.ESCALATED",
    Predicate: """{"op":"count_gte","window_seconds":3600,"threshold":5}""",
    ThrottleSeconds: 3600),

new BuiltInAlertRuleSpec(
    BuiltInKey: "billing-meter-flush-backlog",
    Name: "billing-meter-flush-backlog",
    Description: "10+ meter-event flush failures in 30 minutes — usage may not be reaching Stripe; revenue at risk.",
    Severity: AlertSeverity.Warning,
    EventType: "BILLING.USAGE.FLUSH_FAILED",
    Predicate: """{"op":"count_gte","window_seconds":1800,"threshold":10}""",
    ThrottleSeconds: 1800),
```

`BuiltInAlertRuleSeeder` picks them up automatically (idempotent insert by `built_in_key`). The `AlertRuleEvaluator` already synthesizes a `scope` tag (`platform` | `tenant:<guid>`) from the event's `TenantId`, so a tenant-scoped drift event raises a tenant-feed alert and a platform event raises an admin-feed alert with no rule changes.

### API shape

```
GET /api/v1/orgs/{tenantId}/billing/timeline    (SaaS: MemberAccess + RequireTenantMembershipFilter; single-user: sole user)
GET /api/v1/admin/billing/timeline               (OwnerAccess)  — platform-wide billing audit
GET /api/v1/admin/billing/metrics                (OwnerAccess)  — revenue analytics (MRR/ARR/churn/margin)
```

Tenant timeline DTO entries are the redacted `BillingTimelineEntry`; the metrics DTO mirrors the `BillingRevenueDaily` columns summed over the window. The tenant timeline resolves `tenantId` from `ITenantContext` and rejects a route/context mismatch (no cross-tenant read). `GET /api/v1/admin/billing/metrics` and `.../timeline` are mounted on the existing `/api/admin` group's analytics section in `AdminAnalyticsEndpoints.cs` with `.RequireAuthorization("OwnerAccess")` (matching the existing `/api/admin/analytics/*` wiring style; note the billing routes use the `/api/v1/...` epic-35 convention while the legacy analytics routes stay `/api/admin/analytics/*`).

### EF migration sketch

Additive migration under `Tamma.Data/Migrations/ControlPlane/` (`dotnet ef migrations add AddBillingRevenueDaily`). New table `billing_revenue_daily` with the two partial unique indexes above; numeric columns `NUMERIC(20,4)`. After generating, run `dotnet ef migrations has-pending-model-changes` → expect none. No CHECK edits on existing tables (additive only).

### Integration points

- **`PlatformAnalyticsHourly` + `ComputePlatformRollupActivity` (Story 28-10 / Epic 5/23):** the analytics substrate this story extends; the daily revenue snapshot is computed in the same activity's pure-DI `ComputeAsync` path so it inherits the leader-locked `HourlyAnalyticsRollupScheduler` cadence (no new BackgroundService for the snapshot).
- **`IPlatformTaskHandler` / `PlatformTaskWorker` (Story 28-6):** the daily reconciliation rides the existing CP task queue (`billing.reconcile`). ⚠ `PlatformTaskWorker:RunOnStartup` is `false` in prod today (tenancy residual) — enabling the reconciliation handler in prod must coordinate with that gate (same hazard 35-3 flags).
- **`IEventRepository` (Epic 4):** DCB append for `BILLING.RECONCILIATION.*`; the audit projection reads the same CP `domain_events` table.
- **`IAlertSink` / `AlertRuleEvaluator` / `BuiltInAlertRules` (Story 5.6):** drift/dunning/backlog alerts fan out through the existing pipeline.
- **`IBillingProvider`/`NullBillingProvider` (Story 35-1):** all Stripe reads (`Subscriptions.List`, `Invoices.List`, customer balance) go through the foundation seam; `NullBillingProvider` makes single-user a no-op.
- **Sibling mirrors:** `BillingSubscription` (35-4), `BillingInvoice`/`BillingInvoiceLine` (35-8), `BillingUsageRollup` (35-3), `BillingWalletLedger` (35-10), `BillingCustomer`/`BillingPlanPrice` (35-1/35-2) — read-only.

### Per-mode + per-tenant handling

| Concern | single-user mode | SaaS mode |
|---|---|---|
| Principal | the sole user | the tenant (`BillingCustomer.TenantId`) / platform owner |
| `billing.reconcile` handler | not registered (`NullBillingProvider`) | registered; iterates `BillingCustomer` rows |
| `BillingRevenueDaily` snapshot | not computed (no subs/customers) | computed daily in `ComputePlatformRollupActivity` |
| `GET /api/v1/admin/billing/metrics` | zeroed/empty | revenue analytics over `BillingRevenueDaily` |
| `GET /api/v1/admin/billing/timeline` | sole-user feed (any `BILLING.*` events) | platform-wide, `OwnerAccess` |
| `GET /api/v1/orgs/{id}/billing/timeline` | sole user / 404 if not applicable | tenant members; own-tenant rows only |
| Stripe calls | none | reconciliation reads only (never writes Stripe) |

## Dependencies

**Prerequisite (internal):**
- **Story 35-3** — `BillingUsageRollup` (`PlatformCostUsd`, `BillableAmountUsd`), `IUsageMeteringService`, `BILLING.USAGE.RECONCILIATION_MISMATCH`/`BILLING.USAGE.FLUSH_FAILED`, the `billing.usage_reconcile` per-meter check this story consumes (does not duplicate).
- **Story 35-4** — `BillingSubscription` mirror (status/seats/plan/period), `BILLING.SUBSCRIPTION.CREATED/UPDATED/CANCELED/TRIAL_ENDED`.
- **Story 35-5** — the `BILLING.*` DCB emission seam (the audit projection's source stream) + `BillingWebhookEvent`.
- **Story 35-8** — `BillingInvoice`/`BillingInvoiceLine`, `BillingDunningState`, `BILLING.INVOICE.*`/`BILLING.DUNNING.ESCALATED`/`BILLING.TENANT.SUSPENDED`.
- **Story 35-10** — `BillingWalletLedger` (wallet-balance reconciliation), `BILLING.CREDIT.*`.
- **Story 35-1 / 35-2** — `BillingCustomer` (tenant↔Stripe, `BillingMode`), `BillingPlanPrice`, `IBillingProvider`/`NullBillingProvider`, single-user seam.
- **Epic 5/23 + Story 28-10** — `PlatformAnalyticsHourly` + `ComputePlatformRollupActivity` + `HourlyAnalyticsRollupScheduler` substrate.
- **Story 28-6** — `PlatformTaskWorker` / `IPlatformTaskHandler` CP queue.
- **Story 5.6** — `IAlertSink` / `AlertRuleEvaluator` / `BuiltInAlertRules` pipeline.
- **Epic 4** — `IEventRepository` DCB store.
- **Story 34-5** — `IUsagePricingEngine` (consumed only transitively via 35-3's already-priced rollup columns; no direct call here).

**Blocks:**
- **Story 35-11** — the admin Billing console renders this story's `GET /api/v1/admin/billing/metrics` (MRR/churn/margin) and timeline; 35-11 must not re-implement the math.

**External:**
- **Stripe.net** SDK (added by 35-1) — read-only `Subscriptions.List`, `Invoices.List`, customer balance.
- `STRIPE_SECRET_KEY_TEST` for reconciliation integration tests.

## Testing Strategy

**Unit (xUnit, `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/`):**
1. `BillingAuditServiceTests` — projection ordering (newest-first by `SequenceNumber`), cursor paging, `BILLING.` prefix filter, `from`/`to` window; **redaction whitelist** (an injected `cardNumber`/`apiKey`/`rawPayload` key in a `BILLING.*` event's `data` does NOT survive the projection); `tagGap = true` when a `BILLING.INVOICE.PAID` event is missing its `tenantId` tag; tenant-scope query returns only the caller-tenant's events (cross-tenant isolation).
2. `BillingReconciliationTaskHandlerTests` — per-mirror drift detection (subscription status mismatch, invoice total mismatch beyond tolerance, usage drift consumed from 35-3 signal, wallet-balance mismatch) each emit one `BILLING.RECONCILIATION.DRIFT_DETECTED` with correct `mirror` tag; clean pass emits zero drift events + one `BILLING.RECONCILIATION.COMPLETED`; **per-mirror fail isolation** (a thrown Stripe error on the invoice mirror does not stop the subscription/usage/wallet checks or the run); per-customer iteration never cross-tags; self-reschedule enqueues the next `billing.reconcile` task.
3. `BillingRevenueServiceTests` — MRR from a seeded `BillingSubscription` set (seat × per-seat + flat fee, annual `/12`); ARR = MRR × 12; status counts; **BYOK-vs-platform split** (a `Byok` customer's seat fee lands in `ByokRevenueUsd`, never `PlatformRevenueUsd`; its usage contributes zero usage revenue); margin = `Σ(BillableAmountUsd − PlatformCostUsd)`; logo + revenue churn from `BILLING.SUBSCRIPTION.CANCELED` events; idempotent daily snapshot upsert (running twice = one row, same totals).
4. `BillingMetricsEndpointTests` — `GET /api/v1/admin/billing/metrics` returns the windowed snapshot for `OwnerAccess`; **403** for a tenant-role caller; single-user mode → zeroed payload; the **`metrics`-reconciles-to-invoices invariant** (`platformRevenueUsd + byokRevenueUsd` ≈ `Σ BillingInvoice` totals for the window within tolerance) on a seeded mirror set.
5. `BillingTimelineEndpointTests` — tenant timeline RBAC (member read OK; cross-tenant → never another tenant's rows); admin timeline `OwnerAccess` + 403 for tenant role; single-user seam.
6. `BillingAuditAlertRuleTests` — `BuiltInAlertRuleSeeder` creates the three new rules; the evaluator fires on an appended `BILLING.RECONCILIATION.DRIFT_DETECTED` (tenant-scoped → tenant feed via the synthesized `scope` tag; platform → admin feed); dunning-spike + flush-backlog `count_gte` thresholds fire only over threshold and the throttle suppresses a burst.
7. `BillingAuditSingleUserSeamTests` — single-user mode registers no `billing.reconcile` handler, computes no snapshot, makes zero Stripe calls (`NullBillingProvider`).

**Integration (gated on `STRIPE_SECRET_KEY_TEST`, docker-bound CP Postgres via `sg docker -c "dotnet test ..."`):**
8. Seed `BillingSubscription`/`BillingInvoice`/`BillingUsageRollup`/`BillingWalletLedger` rows + matching Stripe test-customer state → reconciliation reports no drift; mutate one mirror → exactly one `BILLING.RECONCILIATION.DRIFT_DETECTED` for that mirror.
9. End-to-end revenue snapshot against a mixed platform+BYOK seeded fleet → correct MRR/ARR/split/margin; `GET /api/v1/admin/billing/metrics` reconciles to the seeded `BillingInvoice` sum.

**Mocks:** `IBillingProvider` (Stripe reads), `IUsageMeteringService`/35-3 drift signal, `IEventRepository`, `IAlertSink`, and `ITammaModeProvider` are mocked/faked in unit tests; CP context is EF InMemory/SQLite for unit, docker Postgres for integration. Stripe is never live in unit tests.

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/BillingRevenueDaily.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (add `DbSet<BillingRevenueDaily>`) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config + partial unique indexes + precision) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddBillingRevenueDaily.cs` | Create (additive migration) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/IBillingAuditService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingAuditService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingTimelineEntry.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/IBillingRevenueService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingRevenueService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingReconciliationTaskHandler.cs` | Create (`IPlatformTaskHandler`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingMirrorReconciler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingReconciliationOptions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingAuditEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/Analytics/ComputePlatformRollupActivity.cs` | Modify (daily revenue snapshot tail, SaaS only) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs` | Modify (`GetBillingMetrics`, `GetBillingTimeline`) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/BillingTimelineEndpoints.cs` | Create (tenant timeline) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Alerts/Rules/BuiltInAlertRules.cs` | Modify (3 billing built-ins) |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/BillingAuditServiceCollectionExtensions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (wire services + map endpoints, SaaS-gated) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingAuditServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingReconciliationTaskHandlerTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingRevenueServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingMetricsEndpointTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingTimelineEndpointTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingAuditAlertRuleTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingAuditSingleUserSeamTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions (especially `platform-task-worker-runonstartup-hazard` and the Story 28-10 analytics-rollup design notes)
3. Confirmed Stories 35-1, 35-3, 35-4, 35-5, 35-8, 35-10 are merged (this story is a pure reader/aggregator of their mirrors + events)
4. Reviewed Stripe.net read APIs (`Subscriptions.List`, `Invoices.List`, customer balance) — research the latest list-pagination + meter-summary flags before use; do NOT assume a flag exists
5. Planned the TDD Red-Green-Refactor cycle (tests first per the table above)

### Key design decisions

- **Read-side, derive-don't-capture.** The audit timeline is a projection over the already-durable `BILLING.*` `DomainEvent` stream — no new write path on the billing hot path, no duplicate store. The revenue snapshot recomputes from mirrors, so a missed run loses nothing and a replay is idempotent (same `(Day, TenantId)` upsert pattern as `PlatformAnalyticsHourly`).
- **Reconciliation is read-only against Stripe.** This story never *writes* Stripe — it observes drift and raises an alert; remediation is an operator decision (35-11 console actions) or a sibling-story handler. This keeps the integrity layer from masking the bug it is meant to surface.
- **Don't duplicate 35-3's usage check.** The usage mirror is reconciled by 35-3's `billing.usage_reconcile`; this story consumes that signal and folds it into the unified drift view. Re-running `Billing.Meters.ListEventSummaries` here would double the Stripe API load and risk two contradictory verdicts.
- **No margin math, no pricing.** Margin is `BillableAmountUsd − PlatformCostUsd` read straight off 35-3's rollup (those columns are already the output of 34-5's `IUsagePricingEngine`). MRR uses 35-1's `BillingPlanPrice`. If you multiply by a margin here, you have crossed into 34-5's territory.
- **Backend only — 35-11 owns the UI.** This story creates zero `packages/dashboard*` files. It produces the numbers; 35-11's admin Billing console renders them. The `metrics`/`timeline` endpoints are the contract between the two.
- **Compliance-first redaction.** The timeline projection is a strict whitelist (drop-by-default), not a denylist, so a sibling story that starts putting a new sensitive field in a `BILLING.*` event's `data` cannot leak it through the audit surface without an explicit whitelist add.

### Reconciliation tolerance + drift semantics

- Per-mirror tolerances are config-driven (`Billing:Reconciliation:InvoiceToleranceUsd`, `WalletToleranceUsd`) so currency-rounding noise does not page operators. Subscription drift is exact (status/seats/plan are categorical). A drift event always carries the precise `{ local, stripe, delta }` so the operator can act without re-querying.
- A flapping mirror (drift on one run, clean the next) re-alerts by design; the `ThrottleSeconds: 3600` on the built-in rule + the sink rate limiter cap the noise.

### Graceful degradation

If Stripe is unreachable: the reconciliation run logs + counts the per-customer failure, emits `BILLING.RECONCILIATION.COMPLETED` with the partial result, and the `PlatformTaskWorker` retries the task — the audit timeline and `metrics` endpoint keep serving the last-good local data. The revenue snapshot reads only local mirrors, so it is unaffected by a Stripe outage.

## Logging Requirements

- **INFO**: reconciliation run started/completed (`customersChecked`, `mirrorsChecked`, `driftCount`), revenue snapshot computed (`day`, `mrrUsd`, `platformUsageMarginUsd`), timeline/metrics endpoint queried (`tenantId?`, `window`).
- **DEBUG**: per-customer mirror check result (`tenantId`, `mirror`, `local`, `stripe`), audit projection page served (`prefix`, `cursor`, `count`), self-reschedule enqueued (`nextRunAt`).
- **WARN**: drift detected (`tenantId`, `mirror`, `field`, `local`, `stripe`, `delta`), `tagGap` on a `BILLING.*` event (`eventType`, `sequenceNumber`, missing tag), per-customer Stripe error during reconciliation (`tenantId`, scrubbed error).
- **ERROR**: reconciliation handler unrecoverable failure (CP DB write), revenue snapshot compute failure (logged; the activity tail is fail-isolated so the hourly analytics rollup is never blocked), `metrics`-to-invoices invariant breach beyond tolerance (a real revenue-integrity problem).
- **Structured context**: include `{ tenantId, stripeCustomerId, mirror, day, window, driftCount }` where applicable.
- **Credential safety**: NEVER log Stripe API keys, signing secrets, raw card/PAN data, full webhook payloads, or BYOK provider keys. Scrub any Stripe-error message through `CredentialRedactor.Clean` before persistence/logging. The audit timeline projection is whitelist-only — sensitive `data` fields are dropped before they reach a log or an API response.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
