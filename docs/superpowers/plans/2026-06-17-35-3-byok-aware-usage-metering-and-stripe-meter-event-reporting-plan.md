# Story 35-3 — BYOK-Aware Usage Metering & Stripe Meter Event Reporting (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan phase-by-phase. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every phase writes tests
> before implementation. Story file: `docs/stories/epic-35/story-35-3/35-3-byok-aware-usage-metering-and-stripe-meter-event-reporting.md`.

**Goal:** Turn the per-call usage facts already produced by 32-9 (cost basis) / 35-2 (`billing_mode`
tag) and priced by 34-5 (`IUsagePricingEngine`) into billable usage. Platform-provided token usage
is priced (cost basis × margin, from 34-5) and reported to Stripe Billing Meters
(`tamma.platform_tokens_input/output`, customer-mapped via 35-1's `BillingCustomer`); BYOK token
usage is recorded for analytics but **never** reported as billable tokens. Aggregate locally first
(a CP `billing_usage_rollup`) for real-time reads + resilience, batch-flush meter events on the
control-plane `PlatformTaskWorker`, and reconcile hourly against Stripe summaries.

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa engine);
`Stripe.net` SDK (introduced by Story 35-1). Tests in `apps/tamma-elsa/tests/Tamma.Api.Tests/`
(xUnit); docker-bound + Stripe integration suites run via `sg docker -c "dotnet test ..."`.

---

## Non-goals (YAGNI guard)

- **NO markup/margin math.** `BillableAmountUsd` is whatever 34-5's `IUsagePricingEngine.PriceUsage`
  returns. This story's `boundaryNote`: "does not own markup." If you multiply by a margin here,
  stop.
- **NO `BillingCustomer`, Stripe SDK registration, plan catalog, or meter creation.** All owned by
  Story 35-1. This story consumes `IBillingProvider`, `BillingCustomer`, and the meter ids.
- **NO `billing_mode` decision or tagging.** Story 35-2 owns `BillingCustomer.BillingMode` and the
  `billing_mode` tag on `ProviderDiagnostic`/`LLM.CALL.*`. This story trusts the tag.
- **NO new BackgroundService.** Flush + reconcile ride the existing `PlatformTaskWorker` via
  `IPlatformTaskHandler` (Story 28-6), not a bespoke timer.
- **NO billing dashboard UI, invoicing, or dunning.** Later Epic 35 stories; this story stops at
  `GET /api/v1/billing/usage` + the metering pipeline.
- **NO single-user billing.** Single-user mode registers no handlers, makes no Stripe calls, and the
  usage endpoint is absent (mirrors 35-1's `NullBillingProvider`).
- **NO capture-on-call hook in `LlmProxyService`.** Rollups are *derived* from durable facts, so the
  LLM hot path is untouched and metering is automatically fail-open.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### Facts the rollup will read

| Source | Where | Shape |
|---|---|---|
| `ProviderDiagnostic` | `src/Tamma.Data/Entities/ProviderDiagnostic.cs` | Per-call: `InputTokens`, `OutputTokens`, `Cost` (decimal), `TenantId?`, `Model`, `ProviderKey`, `AgentType`, `CreatedAt`. **Per-tenant DB** (Story 28-1 PR D) — NOT directly queryable from a CP worker without fan-out. 35-2 adds the `billing_mode` tag here. |
| `PlatformAnalyticsHourly` | `src/Tamma.Data/Entities/PlatformAnalyticsHourly.cs` | **CP-resident** hourly fact: `Hour`, `TenantId?`, `TokensIn`, `TokensOut` (long), `CostUsd` (decimal 20,4). Per-tenant rows (`TenantId` non-null) + a cross-tenant row (`TenantId` null). Populated by Elsa `HourlyAnalyticsRollupWorkflow` → `ComputeTenantRollupActivity`. **Today it does NOT split tokens by `billing_mode`** — this is the single cross-epic gap (see Phase 1). |
| Priced `LLM.CALL.SUCCESS` DCB events | `IEventRepository` (`src/Tamma.Data/Repositories/EventRepository.cs`, iface `IEventRepository.cs`) | Carry `billingMode`, `costBasisUsd`, `sellPriceUsd` (32-9/34-5). Tenant-scoped; CP read for tenant-less is the platform-events union. Fallback source if `PlatformAnalyticsHourly` split lands later. |

**Decision:** the rollup is **CP-resident** and aggregates the **CP** `PlatformAnalyticsHourly`
table (extended with a `billing_mode` split), so the flush/reconcile workers never fan out to tenant
DBs. `DiagnosticsService.GetReportAsync` (`src/Tamma.Api/Services/Diagnostics/DiagnosticsService.cs`
~113-163) already demonstrates a CP-resident cross-tenant rollup over `ProviderDiagnostic` via raw
SQL — but the doc-comment there (~86-112) warns `ProviderDiagnostic` moves off CP in PR D, so we do
NOT depend on that path; `PlatformAnalyticsHourly` is the durable CP source.

### Pipeline infrastructure (reuse, do not rebuild)

- **Task queue:** `PlatformQueuedTask` entity (`src/Tamma.Data/Entities/PlatformQueuedTask.cs`,
  DbSet at `ControlPlaneDbContext.cs:78`) + `IPlatformTaskHandler`
  (`src/Tamma.Api/Services/PlatformTasks/IPlatformTaskHandler.cs`) +
  `PlatformTaskWorker : BackgroundService` (`PlatformTaskWorker.cs`). `ProcessOnceAsync` is the
  testable drive-once entry; retry/dead-letter/reaper already implemented.
  ⚠ `PlatformTaskWorkerOptions.RunOnStartup` defaults `false` and the XML doc
  (`PlatformTaskWorker.cs:40-75`) warns it MUST NOT be enabled in prod until type-aware reservation
  exists, because `ReserveNextAsync` claims the oldest pending row of **any** type (would
  dead-letter `RETIRE_SECRET_VERSION` etc.). See `.dev/findings/platform-task-worker-runonstartup-hazard.md`.
- **DCB events:** `IEventRepository.AppendAsync(DomainEvent)` (`EventRepository.cs:53`);
  `DomainEvent` = `{ Type, TenantId?, Tags (json), Metadata (json), Data (json), CreatedAt,
  SequenceNumber }` (`src/Tamma.Data/Entities/DomainEvent.cs`). Tenant-scope events carry
  `TenantId`; tenant-less events route to `platform_events` automatically.
- **Alerts:** `IAlertSink.RaiseAsync(AlertPayload)` (`src/Tamma.Api/Services/Alerts/IAlertSink.cs`);
  `AlertRuleEvaluator` (`Services/Alerts/Rules/AlertRuleEvaluator.cs`) polls `DomainEvents` and would
  pick up a future `config-missing`-style built-in rule on `BILLING.USAGE.RECONCILIATION_MISMATCH`
  (rule seeding is out of scope; the event + `IAlertSink` call is in scope).
- **EF model config:** entities are configured in `src/Tamma.Data/TammaModelConfiguration.cs` (single
  source of truth); DbSets exposed on `src/Tamma.Data/ControlPlaneDbContext.cs`. Migrations under
  `src/Tamma.Data/Migrations/ControlPlane/` (baseline `20260609205701_InitialControlPlane`).
- **Mode seam:** `ITammaModeProvider` (`src/Tamma.Api/Services/PromptStore/TammaMode.cs`) — process-
  stable SingleUser | SaaS.
- **Auth policies** (`src/Tamma.Api/Program.cs:966-1118`): `MemberAccess` (any authenticated tenant
  member), `OwnerAccess`, `PlatformOwnerAccess` (platform admin). Tenant-scope reads use
  `MemberAccess`; the caller's tenant comes from `ITenantContext` (ambient), never a route param.
- **Endpoint pattern:** `src/Tamma.Api/Endpoints/AlertEndpoints.cs` — static methods, `IResult`
  returns, `ControlPlaneDbContext` injected, paging defaults (50/500). Mirror for
  `BillingUsageEndpoints`.

### Dependency seams this story consumes (must exist first)

- **35-1:** `src/Tamma.Api/Services/Billing/IBillingProvider.cs` + `StripeBillingProvider.cs`,
  `BillingCustomer` entity (tenant→`StripeCustomerId`, `BillingMode`), `BillingPlanPrice`/catalog
  (meter ids), `NullBillingProvider` single-user seam. **`Services/Billing/` does not exist yet** —
  35-1 creates it; this story adds to it.
- **35-2:** `BillingCustomer.BillingMode`; `billing_mode` tag on facts; `BillingModeService`.
- **34-5:** `src/Tamma.Api/Services/Pricing/UsagePricingEngine.cs` implementing `IUsagePricingEngine`
  (`PriceUsage` → `{ costBasisUsd, marginUsd, sellPriceUsd, pricingMode }`; BYOK token component 0).
- **32-9:** producer of per-call usage + cost-basis events.

---

## Phased task breakdown (test-first / TDD)

### Phase 0 — Prerequisite confirmation (no code)

- [ ] Confirm Stories 35-1, 35-2, 34-5 are merged: `IBillingProvider`, `BillingCustomer`,
      `IUsagePricingEngine`, and the `billing_mode` tag all exist. If any is absent, block this
      story (it is a pure consumer).
- [ ] Decide the `PlatformAnalyticsHourly` `billing_mode` split route (Phase 1) vs the
      `LLM.CALL.SUCCESS`-events fallback. Confirm with the Epic 5/23/28-10 owner whether adding
      `PlatformTokensIn/Out` + `ByokTokensIn/Out` columns to `PlatformAnalyticsHourly` (populated by
      `ComputeTenantRollupActivity`) belongs in this story or theirs.

### Phase 1 — Source-fact billing_mode split (coordinate; may be a dependency PR)

**Approach:** extend the CP fact so platform vs BYOK tokens are separable without a per-tenant scan.

- [ ] **Test first:** `ComputeTenantRollupActivityTests` (in `Tamma.Activities.Tests`) — a tenant
      with mixed platform+BYOK `LLM.CALL.SUCCESS` events in an hour produces a `PlatformAnalyticsHourly`
      row with correct `PlatformTokensIn/Out` and `ByokTokensIn/Out`.
- [ ] Files: `src/Tamma.Data/Entities/PlatformAnalyticsHourly.cs` (+4 long columns),
      `TammaModelConfiguration.cs` (config), additive migration; the `ComputeTenantRollupActivity`
      (in `Tamma.Activities`) populates the split from the `billing_mode` tag.
- [ ] **If this lands in the analytics epic instead:** implement the Phase-2 aggregation against the
      priced `LLM.CALL.SUCCESS` events via `IEventRepository` (CP read) as the fallback source and
      skip this phase. Document which path was taken in the story Dev Notes.

### Phase 2 — `BillingUsageRollup` entity + EF migration

- [ ] **Test first:** `BillingUsageRollupModelTests` / use an EF in-memory or Postgres test fixture —
      insert two rows for the same `(TenantId, PeriodStart)` → unique-index violation; precision on
      `PlatformCostUsd`/`BillableAmountUsd` is 20,4.
- [ ] Files: `src/Tamma.Data/Entities/BillingUsageRollup.cs`,
      `src/Tamma.Data/Entities/BillingMeterEventBuffer.cs`; DbSets on `ControlPlaneDbContext.cs`;
      config + indexes in `TammaModelConfiguration.cs` (unique `(tenant_id, period_start)`; unique
      `idempotency_key`; partial index `reported_to_stripe = false`).
- [ ] Generate additive migration: `dotnet ef migrations add AddBillingUsageRollup` under
      `Migrations/ControlPlane/`; then `dotnet ef migrations has-pending-model-changes` → expect none.
- [ ] Verify migration applies + rolls back cleanly against a throwaway Postgres
      (`sg docker -c "dotnet test ..."` fixture).

### Phase 3 — `UsageMeteringService` (rollup upsert + current-period read + buffer writes)

- [ ] **Test first:** `UsageMeteringServiceTests` (`tests/Tamma.Api.Tests/Billing/`) — mock
      `IUsagePricingEngine`, `IBillingProvider` (for `BillingCustomer` lookup), `IEventRepository`:
  - platform-vs-BYOK split: BYOK tokens land in `Byok*`, never buffered as meter events;
  - `BillableAmountUsd` equals the summed mocked `PriceUsage(...)` (no margin arithmetic in the service);
  - `UpsertRollupAsync` idempotent (run twice → one row, identical totals, watermark advances);
  - missing `billing_mode` → treated as `platform` + WARN;
  - exception in fact-read → logged + swallowed (fail-open), no throw;
  - `GetCurrentUsageAsync` maps the rollup row → `UsageSummaryDto`;
  - no `BillingCustomer` (single-user / unprovisioned) → no-op.
- [ ] Files: `src/Tamma.Api/Services/Billing/IUsageMeteringService.cs`, `UsageMeteringService.cs`,
      `BillingUsageOptions.cs` (`MeterFlushIntervalSeconds=60`, `ReconcileIntervalMinutes=60`,
      `DriftToleranceUsd`), `BillingMeterEventTypes.cs` (`BILLING.USAGE.RECORDED`,
      `BILLING.USAGE.FLUSH_FAILED`, `BILLING.USAGE.RECONCILIATION_MISMATCH`).
- [ ] `UpsertRollupAsync`: resolve `BillingCustomer` → aggregate period facts split by `billing_mode`
      → call `IUsagePricingEngine.PriceUsage` for platform lines → upsert row → append
      `BILLING.USAGE.RECORDED` → swallow on error.
- [ ] `BufferMeterEventsAsync`: write `BillingMeterEventBuffer` rows for platform token totals only
      (idempotency key `{tenantId}:{period}:{eventName}:{watermark}`).

### Phase 4 — `MeterEventFlushTaskHandler : IPlatformTaskHandler`

- [ ] **Test first:** `MeterEventFlushTaskHandlerTests` — mock `IBillingProvider`:
  - pending buffer rows flushed; success sets `ReportedToStripe=true` + `StripeEventId`;
  - failure leaves row pending, bumps `AttemptCount`, emits `BILLING.USAGE.FLUSH_FAILED`, does not throw past the handler contract;
  - idempotency key prevents double-billing on retry;
  - empty buffer → no-op;
  - malformed batch → `PlatformTaskTerminalException`.
- [ ] Files: `src/Tamma.Api/Services/Billing/MeterEventFlushTaskHandler.cs` (`TaskType =>
      "billing.meter_flush"`). Self-reschedule: on completion enqueue the next `billing.meter_flush`
      `PlatformQueuedTask` with a delay of `MeterFlushIntervalSeconds`.
- [ ] Drive via `PlatformTaskWorker.ProcessOnceAsync` in an integration-ish test to prove the
      handler is resolved by `IPlatformTaskHandlerRegistry`.

### Phase 5 — `UsageReconciliationTaskHandler : IPlatformTaskHandler`

- [ ] **Test first:** `UsageReconciliationTaskHandlerTests` — mock `IBillingProvider`
      (`ListEventSummaries`) + `IAlertSink`:
  - matching local/Stripe totals → no event, no alert;
  - drift > `DriftToleranceUsd` → `BILLING.USAGE.RECONCILIATION_MISMATCH` appended + `IAlertSink.RaiseAsync` called once (TenantId set);
  - iterates every active `BillingCustomer`; one customer's failure doesn't abort the rest.
- [ ] Files: `src/Tamma.Api/Services/Billing/UsageReconciliationTaskHandler.cs` (`TaskType =>
      "billing.usage_reconcile"`); hourly self-reschedule.

### Phase 6 — `GET /api/v1/billing/usage` endpoint

- [ ] **Test first:** `BillingUsageEndpointsTests` — returns caller-tenant `UsageSummaryDto`;
      **tenant isolation**: tenant A cannot read tenant B (ambient `ITenantContext`, no route param);
      single-user mode → endpoint absent/404; `MemberAccess` allows any tenant member, unauthenticated
      → 401.
- [ ] Files: `src/Tamma.Api/Endpoints/Billing/BillingUsageEndpoints.cs` (static, mirror
      `AlertEndpoints.cs`); `UsageSummaryDto` record.

### Phase 7 — DI wiring + mode gating

- [ ] **Test first:** `BillingModeSeamTests` — single-user mode registers no flush/reconcile handlers
      and makes zero Stripe calls; SaaS mode registers both handlers + maps the endpoint.
- [ ] Files: `src/Tamma.Api/Extensions/BillingUsageServiceCollectionExtensions.cs`
      (`AddBillingUsageMetering(this IServiceCollection, IConfiguration)` — register
      `IUsageMeteringService`, the two `IPlatformTaskHandler`s via
      `AddPlatformTaskHandler<...>()`, bind `BillingUsageOptions`; gate the handler registration on
      `ITammaModeProvider == SaaS`). Wire in `Program.cs` and map the endpoint (SaaS only), mirroring
      the alert/`AddPlatformTaskHandler` registration pattern.
- [ ] ⚠ Do NOT flip `PlatformTaskWorker:RunOnStartup` to true in prod as part of this story — call
      out the prod-enablement coordination in the PR description (see Risks).

### Phase 8 — Integration tests (Stripe test key)

- [ ] `tests/Tamma.Api.IntegrationTests` (or docker-bound suite): gated on `STRIPE_SECRET_KEY_TEST`.
      Buffer + flush real `tamma.platform_tokens_input/output` events to a Stripe test customer; after
      Stripe's processing delay, `ListEventSummaries` returns matching values; reconciliation reports
      no drift; rollup aggregation against a seeded mixed platform+BYOK fact set yields the correct
      split.
- [ ] Run full suite: `sg docker -c "dotnet test apps/tamma-elsa/Tamma.sln"`; expect green.

---

## Sequencing & dependencies

```
Phase 0 (confirm 35-1/35-2/34-5 merged)
  → Phase 1 (billing_mode split on PlatformAnalyticsHourly — or fallback to events)
  → Phase 2 (BillingUsageRollup + buffer entities + migration)
  → Phase 3 (UsageMeteringService)
  → Phase 4 (flush handler) ─┐
  → Phase 5 (reconcile handler) ─┤ (4 and 5 parallel-safe after 3)
  → Phase 6 (usage endpoint)   ─┘
  → Phase 7 (DI + mode gating)  → Phase 8 (Stripe integration tests)
```

Hard prerequisites: 35-1, 35-2, 34-5 merged (Phase 0). Phase 2 blocks everything after it. Phases 4,
5, 6 only need Phase 3. The Phase-1 fact split is the one cross-epic coordination point — resolve its
ownership before starting.

## Risks + mitigations

- **`PlatformAnalyticsHourly` has no `billing_mode` split (the real gap).** Mitigation: Phase 1 adds
  it, or fall back to aggregating priced `LLM.CALL.SUCCESS` events via `IEventRepository`. Pin the
  decision in Phase 0; the rollup design supports either source behind `UsageMeteringService`.
- **`PlatformTaskWorker:RunOnStartup` is `false` in prod (hazard).** Enabling billing handlers in
  prod requires either type-aware reservation or handlers for every producer type
  (`PlatformTaskWorker.cs:40-75`). Mitigation: this story registers the handlers but does NOT enable
  the worker in prod; flag the prod-enablement as a separate ops step; consider a dedicated
  billing-scoped worker if type-aware reservation isn't ready.
- **Double-billing on flush retry.** Mitigation: every meter event carries a deterministic
  idempotency key (`{tenantId}:{period}:{eventName}:{watermark}`); Stripe dedups on `identifier`, and
  `ReportedToStripe` gates re-buffering.
- **Re-implementing markup (boundary violation).** Mitigation: `BillableAmountUsd` is *only* ever
  the sum of `IUsagePricingEngine.PriceUsage` results; a unit test asserts the service performs no
  margin arithmetic (mock returns a sentinel sell price, service must echo it).
- **BYOK leaking into token meters (revenue/compliance bug).** Mitigation: BYOK lines never reach
  `BufferMeterEventsAsync`; a dedicated test asserts the buffer contains zero `byok` rows.
- **CP/per-tenant topology drift (Story 28-1 / Epic 30).** Mitigation: rollup + buffer + events are
  CP-resident by design (sourced from CP `PlatformAnalyticsHourly`), so the metering workers never
  fan out to tenant DBs; `BILLING.USAGE.*` events append via the CP `IEventRepository` path the
  `AlertRuleEvaluator` already polls.
- **Stripe summary lag causing false reconciliation mismatches.** Mitigation: reconciliation tolerance
  (`DriftToleranceUsd`) + WARN (not ERROR); the API never reads Stripe (always the local rollup).
- **Migration discipline.** `billing_usage_rollup`/`billing_meter_event_buffer` are additive (new
  tables), but still run `has-pending-model-changes` after the migration and mirror config in
  `TammaModelConfiguration.cs` only (single source).

## Acceptance criteria (mirror the story)

- [ ] `BillingUsageRollup` CP entity aggregates per-tenant-per-period platform/BYOK token split,
      `PlatformCostUsd`, `BillableAmountUsd`, `Seats`; idempotent on `(TenantId, PeriodStart)`.
- [ ] Only `billing_mode = platform` usage becomes `tamma.platform_tokens_input/output` meter events;
      BYOK increments counters and is explicitly skipped.
- [ ] `BillableAmountUsd` comes entirely from 34-5's `IUsagePricingEngine`; no margin math in this
      story's services.
- [ ] Meter events buffered + flushed via `MeterEventFlushTaskHandler` (`IPlatformTaskHandler`,
      default 60s, configurable); failed flushes persist `reported_to_stripe = false` and retry; emit
      `BILLING.USAGE.FLUSH_FAILED`.
- [ ] `GET /api/v1/billing/usage` returns the caller-tenant current-period summary from the local
      rollup; tenant-isolated; single-user → absent/404.
- [ ] Hourly `UsageReconciliationTaskHandler` compares local vs Stripe summaries and emits
      `BILLING.USAGE.RECONCILIATION_MISMATCH` (+ alert) on drift.
- [ ] DCB events `BILLING.USAGE.RECORDED` / `FLUSH_FAILED` / `RECONCILIATION_MISMATCH` emitted via
      `IEventRepository`; metering is fail-open (never blocks the LLM call path).
- [ ] Single-user mode registers no handlers, makes no Stripe calls (`NullBillingProvider` seam).
- [ ] Unit + integration tests (`STRIPE_SECRET_KEY_TEST`) cover platform-vs-BYOK split, billable
      delegation, flush success/failure/retry, rollup idempotency, reconciliation drift, tenant
      isolation, and the single-user no-op seam; full suite green.
