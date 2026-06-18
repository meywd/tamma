# Story 35-3: BYOK-Aware Usage Metering & Stripe Meter Event Reporting

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), the `.dev/` knowledge-base usage rule, TRACE/DEBUG logging requirements, the test-first (TDD) mandate, the 100% critical-path coverage target, and build-success enforcement. **Failure to follow this process will result in rework.**

## User Story

As a **platform operator monetizing the Tamma SaaS control plane**,
I want platform-provided AI token usage metered, priced (cost basis + margin), and reported to Stripe Billing Meters while BYOK token usage is recorded for analytics but never billed as tokens,
so that each tenant is invoiced correctly — platform tenants pay for marked-up usage, BYOK tenants pay only the plan/seat fee — with a real-time local usage read, resilient batch flushing, and a reconciliation safety net against Stripe drift.

## Priority

P0 — Required to actually charge for platform-provided usage; the rest of Epic 35 (invoicing, dunning, portal) depends on accurate metered usage landing in Stripe.

## Acceptance Criteria

1. A new control-plane entity `BillingUsageRollup` (`Tamma.Data/Entities/BillingUsageRollup.cs`, registered on `ControlPlaneDbContext`) aggregates **per tenant per billing period** the columns: `TenantId`, `PeriodStart`, `PeriodEnd`, `PlatformInputTokens`, `PlatformOutputTokens`, `ByokInputTokens`, `ByokOutputTokens`, `PlatformCostUsd`, `BillableAmountUsd`, `Seats`, `LastSourceCursor`, `UpdatedAt`. A partial unique index on `(TenantId, PeriodStart)` makes period upserts idempotent (mirrors the `(Hour, TenantId)` idempotency pattern on `PlatformAnalyticsHourly`).
2. The rollup is sourced from the `billing_mode`-tagged usage facts produced by Stories 35-2 (`ProviderDiagnostic.billing_mode` tag) and 32-9/34-5 (`LLM.CALL.SUCCESS` DCB events carrying `billingMode`, `costBasisUsd`, `sellPriceUsd`) — read from the CP `PlatformAnalyticsHourly` fact table (which must split tokens by `billing_mode`) and/or the priced usage events; **no new pricing/markup math is implemented in this story** (consumed from 34-5's `IUsagePricingEngine`).
3. Only `billing_mode = platform` usage is enqueued as Stripe meter events to `tamma.platform_tokens_input` and `tamma.platform_tokens_output` (meter event names + `stripe_customer_id` customer mapping defined by Story 35-1's `BillingPlanCatalog`); `billing_mode = byok` usage increments `ByokInputTokens`/`ByokOutputTokens` on the rollup and is **explicitly skipped** for token meters (asserted by a dedicated test).
4. `BillableAmountUsd` is computed as platform cost basis × (1 + margin) by calling 34-5's `IUsagePricingEngine.PriceUsage(...)`; the margin and price table are **config-driven via 34-5's `MarginPolicy` + 35-1's `billing_plan_prices`**, never hardcoded in `LlmProxyService` or this story's `UsageMeteringService`.
5. `UsageMeteringService` (`Tamma.Api/Services/Billing/UsageMeteringService.cs`) exposes `UpsertRollupAsync(tenantId, period, ct)` (recompute a tenant-period from facts) and `GetCurrentUsageAsync(tenantId, ct)` (read the current-period rollup), and a `BufferMeterEventsAsync(...)` that writes pending meter events for the flush handler — all on the control plane, all tenant-scoped.
6. Meter events are buffered (persisted as `PlatformQueuedTask` rows of type `billing.meter_flush`, or an equivalent CP-resident pending-events table) and flushed by `MeterEventFlushTaskHandler` (`Tamma.Api/Services/Billing/MeterEventFlushTaskHandler.cs`) implementing `IPlatformTaskHandler` (default cadence 60s, configurable via `Billing:MeterFlushIntervalSeconds`), staying within Stripe's meter-event rate limit (1,000 events/sec standard API); a successful flush marks the buffered row `reported_to_stripe = true`.
7. Failed flushes persist with `reported_to_stripe = false` and are retried on the next cycle (the `PlatformTaskWorker` retry/dead-letter semantics apply); a flush failure emits `BILLING.USAGE.FLUSH_FAILED` and never throws back into the LLM call path.
8. `GET /api/v1/billing/usage` (`Tamma.Api/Endpoints/Billing/BillingUsageEndpoints.cs`) returns the **current-period** `{ platformInputTokens, platformOutputTokens, byokInputTokens, byokOutputTokens, platformCostUsd, billableUsd, seats, periodStart, periodEnd }` for the **caller's tenant**, read from the local `BillingUsageRollup` (NOT Stripe, which lags); RBAC: SaaS tenant member read access (`MemberAccess`), single-user mode = the sole user.
9. A reconciliation `IPlatformTaskHandler` of type `billing.usage_reconcile` (scheduled hourly) compares local `BillableAmountUsd`/token totals to Stripe meter event summaries (`Stripe.Billing.Meters.ListEventSummaries`) per active billing customer and emits `BILLING.USAGE.RECONCILIATION_MISMATCH` on drift beyond a configurable tolerance, with WARN-level structured logs.
10. DCB events `BILLING.USAGE.RECORDED` (per rollup upsert / batch flush), `BILLING.USAGE.FLUSH_FAILED`, and `BILLING.USAGE.RECONCILIATION_MISMATCH` are appended via `IEventRepository.AppendAsync` with tags `{ tenantId, billingMode?, periodStart, stripeCustomerId? }` following the `AGGREGATE.ACTION.STATUS` convention; the reconciliation event also feeds the existing `AlertRuleEvaluator`.
11. Metering is **fail-open for billing**: if the rollup write, Stripe call, or reconciliation throws, the LLM call path is never blocked — facts already live in `ProviderDiagnostic`/`PlatformAnalyticsHourly`, so the rollup is recomputed idempotently on the next cycle.
12. Single-user mode (`ITammaModeProvider == SingleUser`) registers no flush/reconcile handlers and the `/api/v1/billing/usage` endpoint is absent (or returns 404), mirroring 35-1's `NullBillingProvider` seam; no Stripe calls are ever made.
13. Tenant isolation: a tenant can only read its own rollup via `GET /api/v1/billing/usage`; the meter-flush and reconciliation handlers iterate `BillingCustomer` rows on the CP and never leak one tenant's usage into another's meter events (covered by a cross-tenant isolation test).
14. Unit + integration tests (integration gated on `STRIPE_SECRET_KEY_TEST`) cover: platform-vs-BYOK token split, the margin/billable math path (delegated to a mocked `IUsagePricingEngine`), batch flush success/failure/retry, rollup aggregation query idempotency, reconciliation drift detection, and the single-user no-op seam.

## Technical Design

### Boundary statement (honor the epic split)

This story is a **consumer**, not an owner, of pricing:

- **32-9 (producer):** agents emit per-call usage + cost-basis facts tagged `agent_id/tenant/provider/model/billing_mode`. This story reads those facts.
- **35-2 (mode):** owns `BillingCustomer.BillingMode` and the `billing_mode` tag on `ProviderDiagnostic`/`LLM.CALL.*` events. This story trusts that tag.
- **34-5 (markup engine):** the **canonical** cost→price markup engine (`IUsagePricingEngine.PriceUsage`). This story **calls** it and must NOT re-implement margin math (`boundaryNote`: "does not own markup").
- **35-1 (foundation):** owns `BillingCustomer` (tenant→`StripeCustomerId`), `BillingPlanCatalog`/`billing_plan_prices` (meter ids `tamma.platform_tokens_input/output`, `tamma.seats`), and the `Stripe.net` SDK registration + `ISecretStore`-sourced key.

This story **reports PRICED usage to Stripe meters** and owns the rollup + flush + reconciliation pipeline.

### Namespace / file structure (C#)

```
apps/tamma-elsa/src/Tamma.Data/
  Entities/
    BillingUsageRollup.cs                 # NEW — CP entity (token split + cost + billable + seats)
    BillingMeterEventBuffer.cs            # NEW — CP pending meter-event row (idempotency + reported_to_stripe flag)
  ControlPlaneDbContext.cs                # MODIFY — DbSet<BillingUsageRollup>, DbSet<BillingMeterEventBuffer>
  TammaModelConfiguration.cs             # MODIFY — entity config: unique indexes, CHECK on billing_mode
  Migrations/ControlPlane/               # NEW additive migration (billing_usage_rollup + billing_meter_event_buffer)

apps/tamma-elsa/src/Tamma.Api/Services/Billing/
  IUsageMeteringService.cs               # NEW
  UsageMeteringService.cs                # NEW — rollup upsert from facts + current-period read + buffer writes
  MeterEventFlushTaskHandler.cs          # NEW — IPlatformTaskHandler "billing.meter_flush"
  UsageReconciliationTaskHandler.cs      # NEW — IPlatformTaskHandler "billing.usage_reconcile"
  BillingUsageOptions.cs                 # NEW — MeterFlushIntervalSeconds, ReconcileIntervalMinutes, DriftToleranceUsd
  BillingMeterEventTypes.cs             # NEW — DCB event-type constants

apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/
  BillingUsageEndpoints.cs               # NEW — GET /api/v1/billing/usage

apps/tamma-elsa/src/Tamma.Api/Extensions/
  BillingUsageServiceCollectionExtensions.cs  # NEW — wire services + handlers (SaaS only)

apps/tamma-elsa/src/Tamma.Api/Program.cs       # MODIFY — call AddBillingUsageMetering(); map endpoints (SaaS only)
```

### Entity: `BillingUsageRollup` (CP-resident)

```csharp
namespace Tamma.Data.Entities;

/// <summary>
/// Story 35-3 — control-plane per-tenant per-billing-period usage rollup.
/// Token counts are split by billing mode so only platform-provided tokens
/// become Stripe meter events; BYOK tokens are recorded for analytics only.
/// CP-resident (like PlatformAnalyticsHourly) so the flush + reconciliation
/// workers answer "what does this tenant owe this period" without per-tenant
/// fan-out. Idempotent upsert keyed by (TenantId, PeriodStart).
/// </summary>
public class BillingUsageRollup
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public DateTime PeriodStart { get; set; }   // UTC, first instant of the billing period
    public DateTime PeriodEnd { get; set; }     // UTC, exclusive

    public long PlatformInputTokens { get; set; }
    public long PlatformOutputTokens { get; set; }
    public long ByokInputTokens { get; set; }
    public long ByokOutputTokens { get; set; }

    public decimal PlatformCostUsd { get; set; }     // cost basis (no markup) — for ops/audit
    public decimal BillableAmountUsd { get; set; }   // cost basis x (1 + margin) from IUsagePricingEngine
    public int Seats { get; set; }                   // last-known seat count (gauge)

    /// <summary>Watermark into the source facts so a recompute is incremental and idempotent.</summary>
    public long LastSourceCursor { get; set; }

    public DateTime UpdatedAt { get; set; }
}
```

EF model config (in `TammaModelConfiguration.cs`):

```csharp
modelBuilder.Entity<BillingUsageRollup>(e =>
{
    e.ToTable("billing_usage_rollup");
    e.HasKey(x => x.Id);
    e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
    e.HasIndex(x => new { x.TenantId, x.PeriodStart }).IsUnique();
    e.Property(x => x.PlatformCostUsd).HasPrecision(20, 4);
    e.Property(x => x.BillableAmountUsd).HasPrecision(20, 4);
});
```

`BillingMeterEventBuffer` carries one pending Stripe meter event: `Id`, `TenantId`, `StripeCustomerId`, `EventName` (`tamma.platform_tokens_input` etc.), `Value` (whole-number string per Stripe), `IdempotencyKey` (`{tenantId}:{period}:{eventName}` so re-flush is safe), `ReportedToStripe`, `StripeEventId?`, `CreatedAt`, `LastAttemptAt?`, `AttemptCount`.

### Service: `UsageMeteringService`

```csharp
public interface IUsageMeteringService
{
    /// <summary>Recompute a tenant's rollup for a period from the billing_mode-tagged facts. Idempotent.</summary>
    Task UpsertRollupAsync(Guid tenantId, BillingPeriod period, CancellationToken ct = default);

    /// <summary>Read the current-period rollup for the API endpoint (local, not Stripe).</summary>
    Task<UsageSummaryDto> GetCurrentUsageAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Buffer platform-only token meter events for the flush handler (BYOK skipped here).</summary>
    Task BufferMeterEventsAsync(Guid tenantId, BillingPeriod period, CancellationToken ct = default);
}
```

`UpsertRollupAsync` (sketch):
1. Resolve the active `BillingCustomer` (35-1) for `tenantId`; if none (single-user / not provisioned), no-op.
2. Aggregate facts for the period **split by `billing_mode`** — sum input/output tokens for `platform` vs `byok` from `PlatformAnalyticsHourly` (CP) rows in `[PeriodStart, PeriodEnd)` (requires `PlatformAnalyticsHourly` to carry a `billing_mode` dimension — see Integration Points; until then read the priced `LLM.CALL.SUCCESS` events via `IEventRepository`).
3. For platform usage, call `IUsagePricingEngine.PriceUsage(...)` (34-5) per provider/model line; sum `costBasisUsd` → `PlatformCostUsd` and `sellPriceUsd` → `BillableAmountUsd`. BYOK lines contribute token counters only (sell price token component = 0 by 34-5 contract).
4. Upsert the `BillingUsageRollup` row (insert-or-update on `(TenantId, PeriodStart)`).
5. Append `BILLING.USAGE.RECORDED` via `IEventRepository.AppendAsync` (tags `{ tenantId, periodStart }`).
6. Wrap the whole body so an exception is logged + swallowed (fail-open for billing).

### Handler: `MeterEventFlushTaskHandler : IPlatformTaskHandler`

`TaskType => "billing.meter_flush"`. `HandleAsync`:
1. Load pending `BillingMeterEventBuffer` rows where `ReportedToStripe == false` (bounded batch).
2. For each, call `Stripe.Billing.MeterEvents.CreateAsync` (via 35-1's `IBillingProvider`) with the `IdempotencyKey` → on success set `ReportedToStripe = true`, `StripeEventId`; on failure increment `AttemptCount`, set `LastAttemptAt`, append `BILLING.USAGE.FLUSH_FAILED`, and leave the row pending.
3. A normal throw signals the `PlatformTaskWorker` to retry the *task* (re-enqueue); a malformed batch throws `PlatformTaskTerminalException`.

The handler is re-enqueued every `Billing:MeterFlushIntervalSeconds` (default 60) by a self-rescheduling pattern (enqueue next `billing.meter_flush` row at the end of a successful run) so it rides the existing `PlatformTaskWorker` loop without a new BackgroundService.

### Handler: `UsageReconciliationTaskHandler : IPlatformTaskHandler`

`TaskType => "billing.usage_reconcile"`. Hourly. For each active `BillingCustomer`: read local rollup totals, call `Stripe.Billing.Meters.ListEventSummariesAsync` for the platform token meters over the current period, compare; if `|local - stripe| > Billing:DriftToleranceUsd` (or token-count tolerance) emit `BILLING.USAGE.RECONCILIATION_MISMATCH` (tags `{ tenantId, meter, local, stripe }`) and WARN-log. Drift events flow into `AlertRuleEvaluator` (a built-in rule can be added later in Epic 35 dashboards work — out of scope here).

### DCB event names (`AGGREGATE.ACTION.STATUS`)

```
BILLING.USAGE.RECORDED
BILLING.USAGE.FLUSH_FAILED
BILLING.USAGE.RECONCILIATION_MISMATCH
```

Appended via `IEventRepository.AppendAsync(new DomainEvent { Type = ..., TenantId = ..., Tags = JsonSerializer.Serialize(...), ... })`. Tenant-scope events carry `TenantId`; the reconciliation summary may also raise via `IAlertSink.RaiseAsync` (TenantId set → tenant feed).

### API shape

```
GET /api/v1/billing/usage        (SaaS: MemberAccess; single-user: sole user) → 200 UsageSummaryDto
```

```jsonc
// UsageSummaryDto
{
  "platformInputTokens": 1840221,
  "platformOutputTokens": 412980,
  "byokInputTokens": 90112,
  "byokOutputTokens": 21044,
  "platformCostUsd": 12.4310,
  "billableUsd": 16.1603,
  "seats": 7,
  "periodStart": "2026-06-01T00:00:00.000Z",
  "periodEnd": "2026-07-01T00:00:00.000Z"
}
```

The caller's tenant is resolved from `ITenantContext` (ambient JWT/API-key tenant) — never a route param — so a tenant cannot read another tenant's usage.

### EF migration sketch

Additive migration under `Tamma.Data/Migrations/ControlPlane/` (normal `dotnet ef migrations add AddBillingUsageRollup` — new tables, not a baseline CHECK edit). After generating, run `dotnet ef migrations has-pending-model-changes` → expect none. Tables: `billing_usage_rollup` (unique `(tenant_id, period_start)`), `billing_meter_event_buffer` (unique `idempotency_key`; partial index on `reported_to_stripe = false`).

### Integration points

- **35-1 `IBillingProvider` / `BillingPlanCatalog`:** Stripe SDK calls + meter ids + `StripeCustomerId` lookup. The `NullBillingProvider` seam makes single-user a no-op.
- **35-2 `billing_mode` tag:** the rollup's platform-vs-BYOK split *depends on* every `ProviderDiagnostic`/`LLM.CALL.*` carrying `billing_mode`. If a fact is missing the tag, treat it as `platform` and WARN (so a 35-2 wiring gap is visible, not silently free).
- **34-5 `IUsagePricingEngine`:** the only source of `billable` amounts. Mocked in unit tests.
- **`PlatformAnalyticsHourly` (Epic 5/23/28-10):** the CP fact table the rollup aggregates. **This story requires `PlatformAnalyticsHourly` to split `TokensIn`/`TokensOut` by `billing_mode`** (today it has flat `TokensIn`/`TokensOut`). The minimal change is two added columns (`PlatformTokensIn/Out`, `ByokTokensIn/Out`) populated by the existing `ComputeTenantRollupActivity`; if that change lands outside this story, fall back to reading priced `LLM.CALL.SUCCESS` events directly via `IEventRepository`. Flag this as the single cross-epic dependency to confirm before implementation.
- **`PlatformTaskWorker` / `IPlatformTaskHandler` (Story 28-6):** flush + reconcile ride the existing CP task queue. ⚠ `PlatformTaskWorker:RunOnStartup` is `false` in prod today (tenancy-residuals hazard); enabling billing handlers in prod must coordinate with that gate (or use a dedicated worker scoped to billing types — see Risks).
- **`IEventRepository` / `IAlertSink` (Epic 4 / Story 5.6):** DCB events + reconciliation alerts.

### Per-mode + per-tenant handling

| Concern | single-user mode | SaaS mode |
|---|---|---|
| Principal | the sole user (no Stripe, no billing) | the tenant (`BillingCustomer.TenantId`) |
| Stripe meter events | none (`NullBillingProvider`) | `tamma.platform_tokens_input/output` per `BillingCustomer.StripeCustomerId` |
| `/api/v1/billing/usage` | absent / 404 | `MemberAccess`; read own tenant rollup |
| Flush + reconcile handlers | not registered | registered; iterate `BillingCustomer` rows |
| BYOK | n/a (self-owned usage) | excluded from token meters; counters only |

## Dependencies

**Prerequisite (internal):**
- Story 35-1 — `BillingCustomer`, `BillingPlanCatalog`/`billing_plan_prices`, `IBillingProvider`/`StripeBillingProvider`, meter ids, `ISecretStore`-sourced Stripe key, `NullBillingProvider` single-user seam.
- Story 35-2 — `BillingCustomer.BillingMode`, the `billing_mode` tag on `ProviderDiagnostic` + `LLM.CALL.*` DCB events.
- Story 34-5 — `IUsagePricingEngine.PriceUsage` (cost basis × margin; BYOK token markup = 0); `MarginPolicy`.
- Story 32-9 — per-call usage + cost-basis events (the producer side).
- Epic 5/23 + Story 28-10 — `PlatformAnalyticsHourly` CP fact table (the aggregation source; needs a `billing_mode` split).
- Epic 9 — `DiagnosticsService` / `ProviderDiagnostic` cost fields (`InputTokens`, `OutputTokens`, `Cost`).
- Story 28-6 — `PlatformTaskWorker` / `IPlatformTaskHandler` queue.
- Epic 4 — `IEventRepository` DCB events.

**Blocks:**
- Story 35-x invoicing / dunning / billing portal (consume metered usage landing in Stripe).
- Epic 36 analytics views of billable usage.

**External:**
- `Stripe.net` SDK (introduced by 35-1) — `Billing.MeterEvents`, `Billing.Meters.ListEventSummaries`.
- `STRIPE_SECRET_KEY_TEST` for integration tests.

## Testing Strategy

**Unit (xUnit, `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/`):**
1. `UsageMeteringServiceTests` — platform-vs-BYOK token split (BYOK tokens land in `Byok*` counters, never buffered as meter events); `BillableAmountUsd` equals the mocked `IUsagePricingEngine.PriceUsage` sum (no margin math in this service); `UpsertRollupAsync` is idempotent (running twice on the same facts yields one row, same totals); missing `billing_mode` tag → treated as `platform` + WARN; service never throws (fact-read failure logged + swallowed).
2. `MeterEventFlushTaskHandlerTests` — buffered rows flushed via mocked `IBillingProvider`; success sets `ReportedToStripe = true` + `StripeEventId`; failure leaves row pending, increments `AttemptCount`, emits `BILLING.USAGE.FLUSH_FAILED`; idempotency key prevents double-billing on retry; empty buffer is a no-op.
3. `UsageReconciliationTaskHandlerTests` — matching local/Stripe totals → no event; drift beyond `DriftToleranceUsd` → `BILLING.USAGE.RECONCILIATION_MISMATCH` + `IAlertSink.RaiseAsync` once; per-customer iteration.
4. `BillingUsageEndpointsTests` — `GET /api/v1/billing/usage` returns the caller-tenant rollup; **tenant-isolation**: tenant A's token cannot read tenant B's rollup (ambient `ITenantContext`, no route param); single-user mode → endpoint absent/404.
5. `BillingModeSeamTests` — single-user mode registers no flush/reconcile handlers and makes zero Stripe calls (`NullBillingProvider`).

**Integration (gated on `STRIPE_SECRET_KEY_TEST`, `apps/tamma-elsa/tests/Tamma.Api.IntegrationTests` or docker-bound suite via `sg docker -c "dotnet test ..."`):**
6. Buffer + flush real `tamma.platform_tokens_input/output` events to a Stripe test customer; after Stripe processing delay, `ListEventSummaries` returns matching values; reconciliation reports no drift.
7. Rollup aggregation against a seeded `PlatformAnalyticsHourly`/event set with mixed platform+BYOK rows → correct split and billable totals.

**Mocks:** `IBillingProvider` (Stripe) and `IUsagePricingEngine` (34-5) are mocked in all unit tests; `ITammaModeProvider` is switched per test to exercise both modes.

## Estimated Effort

5-6 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/BillingUsageRollup.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/BillingMeterEventBuffer.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (DbSets) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config + indexes) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/*_AddBillingUsageRollup.cs` | Create (additive migration) |
| `apps/tamma-elsa/src/Tamma.Data/Entities/PlatformAnalyticsHourly.cs` | Modify (billing_mode token split) — coordinate w/ Epic 5/23/28-10 |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/IUsageMeteringService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/UsageMeteringService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/MeterEventFlushTaskHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/UsageReconciliationTaskHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingUsageOptions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingMeterEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/BillingUsageEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/BillingUsageServiceCollectionExtensions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (wire services + map endpoint, SaaS only) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/UsageMeteringServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/MeterEventFlushTaskHandlerTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/UsageReconciliationTaskHandlerTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingUsageEndpointsTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions (esp. `platform-task-worker-runonstartup-hazard.md` and `story-28-1-design-calls.md`)
3. Reviewed Stripe Billing Meters docs + rate limits (research latest via the Stripe.net docs before using any meter-event API flag)
4. Confirmed Stories 35-1, 35-2, 34-5 are merged (this story is a pure consumer of their seams)
5. Planned the TDD approach (Red-Green-Refactor)

### Key design decisions

- **Rollup is CP-resident, not per-tenant.** Billing is a control-plane concern: the flush + reconciliation workers must answer "what does every tenant owe" in one query without fanning out to every tenant DB. This mirrors `PlatformAnalyticsHourly`'s rationale exactly. `ProviderDiagnostic` itself is per-tenant (Story 28-1 PR D), so the rollup is derived from the **CP** `PlatformAnalyticsHourly` fact table (or CP-resident priced events), never by scanning tenant DBs from the worker.
- **Derive, don't capture-on-call.** Rather than hook `LlmProxyService` to enqueue a meter event per call (the 20-3 TS approach), this story recomputes rollups from already-persisted facts. This is resilient (a worker crash loses nothing — facts are durable), idempotent (recompute = same answer), and keeps the LLM hot path untouched (fail-open is automatic).
- **Idempotency keys on every meter event.** Stripe meter events are deduplicated by `identifier`; `{tenantId}:{period}:{eventName}:{watermark}` guarantees a retry after a partial flush never double-bills.
- **No margin math here.** `BillableAmountUsd` is whatever 34-5's `IUsagePricingEngine` returns. If you find yourself multiplying by a margin in this story, stop — that is a 34-5 responsibility and the `boundaryNote` forbids it.
- **BYOK is a hard exclusion, not a zero.** BYOK token usage is recorded (`Byok*` counters) but is *never* buffered as a `tamma.platform_tokens_*` meter event. A dedicated test asserts the buffer contains zero BYOK rows.

### Stripe meter-event constraints

- `value` payloads accept whole-number strings only — token counts are already integers.
- `timestamp` is Unix seconds.
- Events are processed asynchronously by Stripe — summaries lag, which is *why* the API reads the local rollup and reconciliation runs hourly.
- Standard `Billing.MeterEvents.Create` rate limit is ~1,000/sec; pre-aggregating to one event per tenant-per-meter-per-period keeps us orders of magnitude under it.

### Graceful degradation (fail-open for billing)

If Stripe is unreachable or pricing is misconfigured: facts stay durable in `ProviderDiagnostic`/`PlatformAnalyticsHourly`; the rollup is recomputed next cycle; buffered events stay `reported_to_stripe = false` and retry; the LLM call path and tenant workflows are never blocked. A missing `billing_mode` tag is treated as `platform` and WARN-logged so a 35-2 gap surfaces rather than silently zeroing revenue.

## Logging Requirements

- **INFO:** rollup upserted (tenantId, period, platformTokens, billableUsd), meter batch flushed (count, succeeded, failed), reconciliation completed (customersChecked, mismatches), usage endpoint queried.
- **DEBUG:** individual meter event buffered (eventName, value), flush cycle started, Stripe API response id, pricing-engine call (provider, model, sellPriceUsd).
- **WARN:** meter event flush failed (eventName, attemptCount, error), reconciliation mismatch (meter, local, stripe, drift), `billing_mode` tag missing on a fact (treated as platform), unreported buffer backlog over threshold.
- **ERROR:** rollup recompute failed (swallowed, fail-open — logged for visibility), Stripe meter not found, CP DB write failure.
- **Structured context:** include `{ tenantId, stripeCustomerId, periodStart, eventName, value, batchSize, billingMode }` where applicable.
- **Credential safety:** NEVER log the Stripe API key, customer payment details, or any BYOK provider key (reads go through Epic 29's reveal/audit path; this story never touches BYOK key material directly).

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
