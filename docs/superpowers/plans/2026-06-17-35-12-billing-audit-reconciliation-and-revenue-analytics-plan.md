# Story 35-12: Billing Audit, Reconciliation & Revenue Analytics — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation. Read
> [BEFORE_YOU_CODE.md](../../guides/BEFORE_YOU_CODE.md) first.

**Goal:** Make Epic 35 billing fully **auditable** and **operationally trustworthy**. Three
deliverables, one integrity/reporting layer:

1. A **billing audit timeline** — a read-side projection over the already-durable `BILLING.*`
   `DomainEvent` stream, exposed per-tenant (tenant members) and platform-wide (`OwnerAccess`),
   strictly **redacted** (whitelist, drop-by-default) so no card/PAN/secret/raw-payload ever
   surfaces.
2. A **daily reconciliation job** (`billing.reconcile` `PlatformQueuedTask`) that proves the four
   local mirrors — subscription (35-4), invoice (35-8), usage rollup (35-3), wallet (35-10) — agree
   with Stripe, emitting `BILLING.RECONCILIATION.DRIFT_DETECTED` on any mismatch and a per-run
   `BILLING.RECONCILIATION.COMPLETED`.
3. **Revenue analytics** (MRR/ARR, status counts, logo+revenue churn, BYOK-vs-platform split,
   realized margin) computed on the existing `PlatformAnalyticsHourly` / `ComputePlatformRollupActivity`
   substrate, snapshotted to a new `BillingRevenueDaily` table and served at
   `GET /api/v1/admin/billing/metrics`.

**Story file:** `docs/stories/epic-35/story-35-12/35-12-billing-audit-reconciliation-and-revenue-analytics.md`
(15 acceptance criteria — this plan implements them in dependency order).

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane `Tamma.Api` +
`Tamma.Activities` Elsa engine). Stripe.net (added by Story 35-1). Tests live in
`apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit; docker-bound suites run via
`sg docker -c "dotnet test ..."`, build needs no wrapper). **`packages/api` is deleted — never
reference it; all billing lives in the C# control plane.**

---

## Non-goals (YAGNI guard — honor the epic split)

- **NO markup/pricing math.** Margin is `BillableAmountUsd − PlatformCostUsd` read straight off
  35-3's `BillingUsageRollup` columns (already the output of 34-5's `IUsagePricingEngine`). MRR uses
  35-1's `BillingPlanPrice`. If you multiply a cost by a margin here, you have crossed into 34-5.
- **NO dashboard / React code.** Zero `packages/dashboard*` files. This story produces the
  MRR/churn/margin numbers; Story 35-11's admin Billing console renders them. The `metrics`/`timeline`
  endpoints are the contract between the two.
- **NO second usage-meter reconciliation.** The usage mirror is reconciled by 35-3's
  `billing.usage_reconcile` (→ `BILLING.USAGE.RECONCILIATION_MISMATCH`); this story **consumes** that
  signal and folds it into the unified drift view. Do **not** call `Billing.Meters.ListEventSummaries`
  again here — double Stripe load + two contradictory verdicts.
- **NO Stripe *writes*.** Reconciliation is read-only against Stripe (`Subscriptions.List`,
  `Invoices.List`, customer balance). It observes drift and raises an alert; remediation is an
  operator decision (35-11 console) or a sibling handler — the integrity layer must not mask the bug
  it surfaces.
- **NO new billing write path on the hot path.** The audit timeline is a derive-on-read projection;
  the revenue snapshot recomputes from mirrors (idempotent `(Day, TenantId)` upsert). A missed run
  loses nothing; a replay overwrites.
- **NO new mirror entities.** This story creates exactly one new entity, `BillingRevenueDaily`. It
  reads `BillingCustomer`/`BillingPlanPrice` (35-1/35-2), `BillingSubscription` (35-4),
  `BillingInvoice`/`BillingInvoiceLine`/`BillingDunningState` (35-8), `BillingUsageRollup` (35-3),
  `BillingWalletLedger` (35-10) — read-only.
- **NO new BackgroundService for the snapshot.** It rides the existing leader-locked
  `HourlyAnalyticsRollupScheduler` cadence by extending `ComputePlatformRollupActivity.ComputeAsync`.

---

## Current-state findings (verified 2026-06-17, repo @ main)

### Substrate this story extends (all real, all verified)

| Seam | File | Shape relied on |
|---|---|---|
| DCB event store | `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` | `Type`, `TenantId` (nullable), `Tags`/`Metadata`/`Data` (JSON strings), `CreatedAt`, `SequenceNumber` (long). The audit projection filters `Type LIKE 'BILLING.%'` and orders by `SequenceNumber DESC`. |
| Event append | `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` | `AppendAsync(DomainEvent)` — CP `domain_events`; the same store `AlertRuleEvaluator` polls. |
| Analytics fact | `apps/tamma-elsa/src/Tamma.Data/Entities/PlatformAnalyticsHourly.cs` | Dual **partial unique indexes** on `(Hour, TenantId)` — one `TenantId IS NULL`, one `IS NOT NULL` — the idempotency pattern `BillingRevenueDaily` copies for `(Day, TenantId)`. |
| Rollup activity | `apps/tamma-elsa/src/Tamma.Activities/Analytics/ComputePlatformRollupActivity.cs` | `TammaAsyncActivity` with `RunAsync` + a **static `ComputeAsync(factory, publisher, hour, logger, ct)`** that writes the `TenantId = null` platform row. The daily revenue snapshot is a SaaS-gated tail step on this static path (testable without an Elsa context). |
| Task queue | `apps/tamma-elsa/src/Tamma.Api/Services/PlatformTasks/IPlatformTaskHandler.cs` | `TaskType` (dot-snake-case) + `HandleAsync`. Throw `Exception` → retryable; throw `PlatformTaskTerminalException` → dead-letter. The reconciliation handler self-reschedules the next run, mirroring 35-3's `billing.meter_flush`. |
| Alert built-ins | `apps/tamma-elsa/src/Tamma.Api/Services/Alerts/Rules/BuiltInAlertRules.cs` | Positional `record BuiltInAlertRuleSpec(BuiltInKey, Name, Description, Severity, EventType, Predicate, ThrottleSeconds)`. **5 built-ins today** (budget-exhausted, agent-dispatch-failed, workflow-retry-exceeded, platform-api-unhealthy, secret-rotation-failed). This story adds 3. |
| Alert seeder | `apps/tamma-elsa/src/Tamma.Api/Services/Alerts/Rules/BuiltInAlertRuleSeeder.cs` | Idempotent insert-by-`built_in_key` — new specs are picked up automatically, no seeder edit. |
| Alert evaluator | `apps/tamma-elsa/src/Tamma.Api/Services/Alerts/Rules/AlertRuleEvaluator.cs` | Polls `DomainEvents` + `PlatformEvents`; throttle keyed `(rule.Id, payload.TenantId)`; synthesizes feed from `TenantId` (null → platform/admin feed, set → tenant feed). Predicates supported: `always`, `count_gte`. The three new built-ins use exactly those. |
| Admin analytics API | `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs` | The real analytics endpoint file (**NOT** an `Endpoints/Admin/` sub-path). `metrics` + admin `timeline` mount here with `.RequireAuthorization("OwnerAccess")`. |
| Mode source | `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs` | `ITammaModeProvider` (SingleUser | SaaS), process-stable. Single-user → handler/snapshot not registered, `NullBillingProvider` (35-1) → zero Stripe surface. |

### Sibling mirrors this story reads (owned elsewhere — do not create or modify)

`BillingCustomer` (tenant↔Stripe, `BillingMode`) + `BillingPlanPrice` (35-1/35-2);
`BillingUsageRollup` (`PlatformCostUsd`, `BillableAmountUsd`) + `billing.usage_reconcile` (35-3);
`BillingSubscription` (status/seats/plan/period) (35-4); `BillingWebhookEvent` + the `BILLING.*`
emission seam (35-5); `BillingInvoice`/`BillingInvoiceLine`/`BillingDunningState` (35-8);
`BillingWalletLedger` (35-10). **If a sibling mirror is not yet merged, that mirror's reconciliation
check and its contribution to the revenue snapshot degrade gracefully (skipped + logged), so 35-12
can land and be partially exercised before every sibling is in.**

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Principal for a billing timeline entry | the sole user (their instance, their feed) | the tenant (`BillingCustomer.TenantId`) / platform owner |
| `billing.reconcile` handler | **not registered** (`NullBillingProvider`, no `BillingCustomer` rows) | registered; iterates `BillingCustomer` rows |
| `BillingRevenueDaily` snapshot | not computed (no subs/customers) | computed daily in `ComputePlatformRollupActivity` |
| `GET /api/v1/admin/billing/metrics` | zeroed/empty payload | revenue analytics over `BillingRevenueDaily` (`OwnerAccess`) |
| `GET /api/v1/admin/billing/timeline` | sole-user feed (any `BILLING.*` events) | platform-wide, `OwnerAccess` |
| `GET /api/v1/orgs/{id}/billing/timeline` | sole user / 404 if N/A (mirrors 35-3 seam) | tenant members; **own-tenant rows only** (`ITenantContext`, never a spoofable route param) |
| Stripe calls | none | reconciliation **reads** only (never writes Stripe) |

---

## Architecture

**Read-side, derive-don't-capture.** Two read services + one reconciliation handler + one revenue
snapshot tail + three alert built-ins:

```
            BILLING.* DomainEvents (CP)  ────────────►  IBillingAuditService
            (35-1/3/4/5/8/10 emit them)                 (redacted timeline projection)
                                                            │
                                                            ▼
                                          GET /orgs/{id}/billing/timeline  (tenant members)
                                          GET /admin/billing/timeline      (OwnerAccess)

  sibling mirrors (read-only) ──► BillingMirrorReconciler ──► BillingReconciliationTaskHandler
  (sub / invoice / usage / wallet)   (per-mirror compare)      "billing.reconcile" daily, self-rescheduled
                                                            │
                                            BILLING.RECONCILIATION.DRIFT_DETECTED  (tenant-scoped)
                                            BILLING.RECONCILIATION.COMPLETED       (platform, per run)
                                                            │
                                                            ▼
                                          BuiltInAlertRules (3 new) → AlertRuleEvaluator → IAlertSink

  sibling mirrors (read-only) ──► IBillingRevenueService.ComputeDailySnapshotAsync
  (sub price, usage cost/sell)        (MRR/ARR/churn/margin/BYOK-split, no markup math)
                                                            │
                                  ComputePlatformRollupActivity.ComputeAsync tail (SaaS only, daily)
                                                            │
                                            upsert BillingRevenueDaily  (Day, TenantId) idempotent
                                                            │
                                                            ▼
                                          GET /admin/billing/metrics  (OwnerAccess, from snapshot)
```

**Redaction is a whitelist, not a denylist.** `BillingAuditService` projects only
`{ amountUsd, currency, status, last4, invoiceId, stage }` from a `BILLING.*` event's `data` through
a `private static readonly HashSet<string> _allowedSummaryKeys`. Anything else is dropped — so a
sibling story that later puts a new sensitive field in a billing event cannot leak it through the
audit surface without an explicit whitelist add. A redaction test injects `cardNumber`/`apiKey`/
`rawPayload` and asserts they do not survive.

**`tagGap` marker.** Where the projection sees a `BILLING.*` event missing a `tenantId`/
`stripeCustomerId` tag that should be present (a sibling emission bug), it surfaces the row with
`tagGap = true` and WARN-logs once — making a tagging regression visible to compliance export rather
than silently dropping it.

---

## Task breakdown (TDD; tests before implementation in every task)

### 35-12-T1: `BillingRevenueDaily` entity + EF config + additive migration

**Scope:** The CP daily-revenue snapshot table only. No computation, no endpoint.

**Files:**
- New: `apps/tamma-elsa/src/Tamma.Data/Entities/BillingRevenueDaily.cs` — columns per the story's
  entity sketch (`Day` UTC midnight, nullable `TenantId`, `MrrUsd`/`ArrUsd`, four status counts,
  `LogoChurnCount`/`RevenueChurnUsd`, `ByokRevenueUsd`/`PlatformRevenueUsd`,
  `PlatformUsageCostUsd`/`PlatformUsageMarginUsd`, `ComputedAt`).
- Modify: `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` — `DbSet<BillingRevenueDaily>`.
- Modify: `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs`
  (`ConfigureControlPlaneEntities` — the established single source) — `gen_random_uuid()` default,
  **two partial unique indexes** on `(Day)` `WHERE TenantId IS NULL` and `(Day, TenantId)`
  `WHERE TenantId IS NOT NULL` (mirroring `PlatformAnalyticsHourly`), `HasPrecision(20,4)` on all
  decimals.
- New: `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddBillingRevenueDaily.cs` —
  additive (`dotnet ef migrations add AddBillingRevenueDaily`). New table, no CHECK edits on
  existing tables.

**Tests (first):** entity round-trips through the CP context; the two partial unique indexes reject
a duplicate `(Day, null)` and `(Day, tenantId)` respectively but allow the same `Day` across
distinct tenants and one platform row.

**Acceptance:**
- [ ] `dotnet ef migrations has-pending-model-changes` reports **none** after generation.
- [ ] Migration applies + rolls back cleanly; full suite stays green.

### 35-12-T2: `IBillingAuditService` + redacted timeline projection

**Scope:** The read-side projection over `BILLING.%` `DomainEvent` rows. No endpoints yet.

**Files:**
- New: `IBillingAuditService.cs`, `BillingAuditService.cs`, `BillingTimelineEntry.cs`
  (DTO + `BillingTimelineFilter` + `BillingTimelinePage` records) in
  `apps/tamma-elsa/src/Tamma.Api/Services/Billing/`.
- The service exposes `GetTenantTimelineAsync(tenantId, filter, ct)` (hard-scoped
  `TenantId == tenantId AND Type LIKE 'BILLING.%'`) and `GetPlatformTimelineAsync(filter, ct)`
  (all tenants + `TenantId IS NULL` platform billing events), newest-first by `SequenceNumber DESC`,
  cursor-paged (default 50, max 200), `eventType` prefix + `from`/`to` filters.
- Redaction: `private static readonly HashSet<string> _allowedSummaryKeys`
  (`amountUsd`, `currency`, `status`, `last4`, `invoiceId`, `stage`) — drop-by-default.
- `tagGap` derivation: set when an expected `tenantId`/`stripeCustomerId` tag is absent.

**Tests (first):** `BillingAuditServiceTests` — ordering (newest-first), cursor paging,
`BILLING.` prefix filter, `from`/`to` window; **redaction whitelist** (injected
`cardNumber`/`apiKey`/`rawPayload` key does NOT survive); `tagGap = true` for a
`BILLING.INVOICE.PAID` missing its `tenantId` tag; tenant-scope query returns only the caller
tenant's rows (cross-tenant isolation).

**Acceptance:**
- [ ] No non-whitelisted `data` key ever appears in a `BillingTimelineEntry.Summary`.
- [ ] Tenant timeline never returns another tenant's or a platform-scoped event.

### 35-12-T3: `BillingMirrorReconciler` + `BillingReconciliationTaskHandler` + drift events

**Scope:** The daily cross-mirror reconciliation. Reads sibling mirrors, reads Stripe via 35-1's
`IBillingProvider`, emits drift/completed events. Consumes 35-3's usage signal (does not re-run the
meter check).

**Files:**
- New: `BillingMirrorReconciler.cs` — pure-ish per-mirror compare helpers (subscription / invoice /
  usage / wallet), each returning a drift descriptor or none.
- New: `BillingReconciliationTaskHandler.cs` (`IPlatformTaskHandler`, `TaskType => "billing.reconcile"`)
  — iterates active `BillingCustomer` rows, runs the four checks each in its own try/catch
  (**per-mirror + per-customer fail isolation**), emits `BILLING.RECONCILIATION.DRIFT_DETECTED`
  (tenant-scoped) on mismatch + a single `BILLING.RECONCILIATION.COMPLETED` (platform, `TenantId = null`)
  per run, self-reschedules the next run at `now + IntervalHours`.
- New: `BillingReconciliationOptions.cs` (`Billing:Reconciliation:IntervalHours` default `24`,
  `InvoiceToleranceUsd`, `WalletToleranceUsd`).
- New: `BillingAuditEventTypes.cs` (`RECONCILIATION.*` DCB type constants).
- **Usage mirror:** read the latest `BILLING.USAGE.RECONCILIATION_MISMATCH` events + the local
  `BillingUsageRollup`; surface as `mirror = "usage"` — **no** `Billing.Meters.ListEventSummaries`
  call here.
- Defensive single-user guard in `HandleAsync` even though it is not registered in single-user mode.

**Tests (first):** `BillingReconciliationTaskHandlerTests` — per-mirror drift (subscription status
mismatch, invoice total beyond tolerance, usage drift consumed from 35-3 signal, wallet-balance
mismatch) each emit one drift event with the correct `mirror` tag; clean pass emits zero drift +
one `COMPLETED`; **per-mirror fail isolation** (a thrown Stripe error on the invoice mirror does not
stop the others or the run); per-customer iteration never cross-tags; self-reschedule enqueues the
next `billing.reconcile` task.

**Acceptance:**
- [ ] One drifting mirror → exactly one `DRIFT_DETECTED` with `tags.mirror` set + a WARN log.
- [ ] One customer's Stripe error never aborts the run; `COMPLETED` carries the partial result.
- [ ] No second Stripe meter-summary call for the usage mirror.

### 35-12-T4: `IBillingRevenueService` + snapshot tail on `ComputePlatformRollupActivity`

**Scope:** MRR/ARR/churn/margin/BYOK-split computation + idempotent daily snapshot. No new pricing.

**Files:**
- New: `IBillingRevenueService.cs`, `BillingRevenueService.cs` — `ComputeDailySnapshotAsync(day, ct)`
  (the SQL + math, callable from unit tests without an Elsa context) and a windowed read
  (`GetMetricsAsync(from, to, ct)` over `BillingRevenueDaily`).
  - MRR = `Σ` over `BillingSubscription` in (`active`,`trialing`,`past_due`) of
    `flatPlanFee + seats × perSeatPrice` (prices from 35-1 `BillingPlanPrice`; annual `/12`).
    ARR = MRR × 12.
  - BYOK-vs-platform split by `BillingCustomer.BillingMode` (`PlatformProvided` vs `Byok`).
  - Margin = `Σ(BillableAmountUsd − PlatformCostUsd)` over `PlatformProvided` tenants;
    `PlatformUsageCostUsd = Σ PlatformCostUsd`; usage revenue folds into `PlatformRevenueUsd`.
  - Churn: `LogoChurnCount` = `BILLING.SUBSCRIPTION.CANCELED` events in `[day, day+1)`;
    `RevenueChurnUsd` = the MRR those subs represented at cancellation.
- Modify: `apps/tamma-elsa/src/Tamma.Activities/Analytics/ComputePlatformRollupActivity.cs` — a
  **SaaS-gated, once-per-day (top-of-day hour)** tail step in the static `ComputeAsync` path calls
  `IBillingRevenueService.ComputeDailySnapshotAsync` and upserts the `BillingRevenueDaily` platform
  row. Fail-isolated so the hourly analytics rollup is never blocked by a snapshot error.

**Tests (first):** `BillingRevenueServiceTests` — MRR from a seeded subscription set (seat × per-seat
+ flat fee, annual `/12`); ARR = MRR × 12; status counts; **BYOK-vs-platform split** (a `Byok`
customer's seat fee lands in `ByokRevenueUsd` only; its usage contributes zero usage revenue);
margin = `Σ(BillableAmountUsd − PlatformCostUsd)`; logo + revenue churn from `CANCELED` events;
**idempotent upsert** (running twice = one row, same totals).

**Acceptance:**
- [ ] No markup multiplication anywhere — prices come from `BillingPlanPrice`, margin from rollup columns.
- [ ] Replaying a day overwrites its single `(Day, null)` row (idempotent).
- [ ] Snapshot failure is logged + isolated; the hourly analytics rollup still completes.

### 35-12-T5: Endpoints — tenant timeline, admin timeline, admin metrics

**Scope:** The three read endpoints; per-mode RBAC; the `metrics`-reconciles-to-invoices invariant.

**Files:**
- New: `apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/BillingTimelineEndpoints.cs` —
  `GET /api/v1/orgs/{tenantId}/billing/timeline` (SaaS: `MemberAccess` + `RequireTenantMembershipFilter`;
  single-user: sole user / 404 if N/A). Resolve `tenantId` from `ITenantContext`; reject a
  route/context mismatch (no cross-tenant read).
- Modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs` —
  `GET /api/v1/admin/billing/timeline` (`OwnerAccess`, platform-wide via `GetPlatformTimelineAsync`)
  and `GET /api/v1/admin/billing/metrics` (`OwnerAccess`, windowed `?from&to`, default current
  calendar month, served from `BillingRevenueDaily` — no live fan-out). Note: billing routes use the
  `/api/v1/...` epic-35 convention; legacy analytics routes stay `/api/admin/analytics/*`.

**Tests (first):** `BillingTimelineEndpointTests` — tenant RBAC (member read OK; cross-tenant never
returns another tenant's rows), admin timeline `OwnerAccess` + 403 tenant role, single-user seam.
`BillingMetricsEndpointTests` — windowed snapshot for `OwnerAccess`; **403** tenant role;
single-user → zeroed payload; the **`metrics`-reconciles-to-invoices invariant**
(`platformRevenueUsd + byokRevenueUsd` ≈ `Σ BillingInvoice` totals for the window within tolerance)
on a seeded mirror set.

**Acceptance:**
- [ ] Tenant timeline is hard-scoped to `ITenantContext` — no spoofable route param read.
- [ ] `metrics` reconciles to the sum of per-tenant invoices within the documented tolerance.

### 35-12-T6: Three billing alert built-ins

**Scope:** Add three specs to `BuiltInAlertRules.All` (seeder picks them up automatically). No
seeder/evaluator changes.

**Files:** Modify `apps/tamma-elsa/src/Tamma.Api/Services/Alerts/Rules/BuiltInAlertRules.cs` —
- `billing-reconciliation-drift` — `Warning`, `BILLING.RECONCILIATION.DRIFT_DETECTED`,
  `{"op":"always"}`, `ThrottleSeconds: 3600`.
- `billing-dunning-spike` — `Critical`, `BILLING.DUNNING.ESCALATED`,
  `{"op":"count_gte","window_seconds":3600,"threshold":5}`, `ThrottleSeconds: 3600`.
- `billing-meter-flush-backlog` — `Warning`, `BILLING.USAGE.FLUSH_FAILED`,
  `{"op":"count_gte","window_seconds":1800,"threshold":10}`, `ThrottleSeconds: 1800`.

All ship with empty `ChannelIds` per the existing convention (no auto-spam). The evaluator already
synthesizes the feed from `TenantId` (tenant-scoped drift → tenant feed; platform → admin feed).

**Tests (first):** `BillingAuditAlertRuleTests` — `BuiltInAlertRuleSeeder` creates the three rules;
the evaluator fires on an appended `BILLING.RECONCILIATION.DRIFT_DETECTED` (tenant-scoped → tenant
feed; platform → admin feed); the `count_gte` thresholds fire only over threshold and the throttle
suppresses a burst.

**Acceptance:**
- [ ] Drift/dunning/backlog events produce alerts with no manual rule setup; built-ins have empty `ChannelIds`.

### 35-12-T7: DI wiring + mode gating + single-user seam

**Scope:** Register the audit/revenue services + reconciliation handler (SaaS only); map endpoints.

**Files:**
- New: `apps/tamma-elsa/src/Tamma.Api/Extensions/BillingAuditServiceCollectionExtensions.cs` —
  `AddBillingAuditAndAnalytics()`: registers `IBillingAuditService`, `IBillingRevenueService`,
  `BillingReconciliationOptions`, and (SaaS only) `AddPlatformTaskHandler<BillingReconciliationTaskHandler>()`.
- Modify: `apps/tamma-elsa/src/Tamma.Api/Program.cs` — call the extension; map the tenant + admin
  endpoints (SaaS-gated where applicable, mirroring 35-3/35-5 mode gating).

**Tests (first):** `BillingAuditSingleUserSeamTests` — single-user mode registers no
`billing.reconcile` handler, computes no snapshot, makes **zero** Stripe calls (`NullBillingProvider`);
`GET /api/v1/admin/billing/metrics` returns a zeroed payload; the org timeline returns the sole-user
feed / 404 per the 35-3 seam.

**Acceptance:**
- [ ] Single-user: no reconciliation handler, no snapshot, no Stripe surface.
- [ ] SaaS: all surfaces mounted + tenant-scoped per T2/T5.

---

## Task order & dependencies

```
T1 (entity/migration) ──► T4 (revenue service/snapshot) ──► T5 (endpoints)
                       └─► (T2, T3 independent of T1) ─────► T5
T2 (audit projection) ─────────────────────────────────────► T5
T3 (reconciliation) ──► T6 (alerts, needs T3's event types) ──► T7 (wiring)
T6 ──────────────────────────────────────────────────────────► T7
```

T2 and T3 are independent of T1 and of each other (parallel-safe). T4 needs T1 (the snapshot table).
T5 needs T2 + T4. T6 needs T3's event-type constants. T7 (wiring + single-user seam) is last and
ties everything together. **Each sibling mirror is read-only and degrades gracefully if not yet
merged**, so the wave can start the moment 35-1/35-5 are in and backfill reconciliation/revenue
coverage as 35-3/35-4/35-8/35-10 land.

---

## Risks

- **`PlatformTaskWorker:RunOnStartup` is `false` in prod today (tenancy residual).** The daily
  reconciliation rides the CP task queue; enabling it in prod must coordinate with that gate — the
  same hazard 35-3's `billing.meter_flush` flags. Note it in the rollout checklist; do not flip the
  gate as part of this story.
- **Sibling-mirror skew during the wave.** Tasks read 35-3/35-4/35-8/35-10 mirrors that may not all
  be merged when 35-12 starts. The per-mirror reconciliation and per-mirror revenue contribution
  must skip-and-log a missing mirror, not throw — so the integrity layer can ship and grow coverage.
- **Redaction must be drop-by-default.** A denylist would leak the next sensitive field a sibling
  adds. The whitelist test (`cardNumber`/`apiKey`/`rawPayload` injected) is load-bearing — keep it.
- **Double-counting revenue.** Usage revenue contributes to `PlatformRevenueUsd` via
  `BillableAmountUsd`; subscription seat/plan fees contribute separately. The
  `metrics`-reconciles-to-invoices invariant test pins that the two paths sum to the invoice total —
  if it breaks, suspect double-counting platform usage in both the rollup and an invoice line.
- **Flapping drift re-alerts by design.** A mirror that drifts then clears re-fires; `ThrottleSeconds`
  on the built-in + the sink rate limiter cap the noise. If flapping is observed in prod, a
  reopen-cooldown is a cheap follow-up — out of scope here.
- **Snapshot must never block the hourly rollup.** The `ComputePlatformRollupActivity` tail is
  fail-isolated (try/catch + ERROR log); a revenue-snapshot bug must not stop
  `PlatformAnalyticsHourly` from being written.
- **Migration discipline.** `BillingRevenueDaily` is additive; still verify
  `has-pending-model-changes` reports none and mirror the entity config in
  `TammaModelConfiguration.cs` only (the established single source).

## Verification before completion

- [ ] `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests"` green (unit + docker-bound).
- [ ] `dotnet ef migrations has-pending-model-changes` → none.
- [ ] No reference to `packages/api` anywhere in the diff (deleted package).
- [ ] No `packages/dashboard*` files touched (35-11 owns the UI).
- [ ] No markup/pricing multiplication, no second `Billing.Meters.ListEventSummaries` call.
- [ ] Single-user mode: zero Stripe calls, zeroed `metrics`, no `billing.reconcile` handler registered.

## Change Log

| Date       | Version | Changes              | Author |
| ---------- | ------- | -------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial plan         | Claude |
