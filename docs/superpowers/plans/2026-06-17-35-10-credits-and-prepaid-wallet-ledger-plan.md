# Story 35-10: Credits & Prepaid Wallet Ledger — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes its
> xUnit tests before the implementation it covers.

**Goal:** Give SaaS tenants a prepaid credits wallet whose balance is derived from an append-only,
double-entry-style ledger (grant / topup / consume / expire / refund / adjustment) on the control
plane, applied to invoices before the card is charged. Top-ups via Stripe one-time PaymentIntents
credit the wallet only on `payment_intent.succeeded` (idempotent by intent id); available credit
offsets invoice `amount_due` at finalize (BYOK ⇒ platform/seat fee, platform-provided ⇒ also token
charges); expired lots are swept on the platform task queue; every mutation emits a `BILLING.CREDIT.*`
DCB event. Balance is never stored mutably — it is `SUM(AmountUsd)` per tenant.

**Story file:** `docs/stories/epic-35/story-35-10/35-10-credits-and-prepaid-wallet-ledger.md`

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API `Tamma.Api`,
data `Tamma.Data`, core `Tamma.Core`). Stripe.net (added by Story 35-1). Tests in
`apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/` (xUnit; docker-bound CP-Postgres suites run via
`sg docker -c "dotnet test ..."`).

---

## Non-goals (YAGNI guard)

- NO invoice mirror, invoice finalize state machine, or dunning — that is **Story 35-8**. This plan
  only adds the credit-apply call site (`IWalletCreditService.ApplyToInvoiceAsync`) and the `credit`
  line amount; it does not own `BillingInvoice`/`BillingInvoiceLine`.
- NO webhook endpoint / signature verification / dedup row — that is **Story 35-5**. This plan only
  *registers* `IBillingEventHandler` implementations (`WalletTopupWebhookHandler`,
  `WalletRefundWebhookHandler`) into 35-5's registry.
- NO Stripe customer mapping / catalog / customer creation — that is **Story 35-1**. This plan reuses
  35-1's `BillingCustomer`, `StripeClientFactory`/`IBillingProvider`, and `NullBillingProvider` seam.
- NO subscription lifecycle (35-4), payment methods/portal (35-7), metering/markup (35-3), or quota
  enforcement/suspension (35-6).
- NO tenant-facing dashboard UI (would live in `packages/dashboard-user`, consuming
  `GET /api/v1/orgs/{tenantId}/billing/wallet`). Backend only.
- NO mutable `tenant.credit_balance` column. Balance stays derived from the ledger.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### What exists to build on

| Seam | File / location | Use |
|---|---|---|
| Control-plane DbContext + DbSets | `src/Tamma.Data/ControlPlaneDbContext.cs` (DbSets ~33-102) | add `DbSet<BillingWalletLedger>`. |
| Single source of EF model config | `src/Tamma.Data/TammaModelConfiguration.cs` (`ConfigureControlPlaneEntities`) | table/CHECK/index/FK config goes here ONLY. |
| CP migrations dir | `src/Tamma.Data/Migrations/ControlPlane/` (baseline `20260609205701_InitialControlPlane.cs` + `ControlPlaneDbContextModelSnapshot.cs`) | additive migration lands here. |
| DCB event append | `src/Tamma.Data/Repositories/IEventRepository.cs` → `AppendAsync(DomainEvent)`; entity `src/Tamma.Data/Entities/DomainEvent.cs` (`Type`, `TenantId`, `Tags`, `Metadata`, `Data`, server-side `SequenceNumber`) | emit `BILLING.CREDIT.*`. |
| Platform task queue | `src/Tamma.Data/Entities/PlatformQueuedTask.cs`; `src/Tamma.Data/Repositories/IPlatformQueuedTaskRepository.cs` → `EnqueueAsync(task, ct)` (line 29) | enqueue the expiry sweep. |
| Platform task handler contract | `src/Tamma.Api/Services/PlatformTasks/IPlatformTaskHandler.cs` (`TaskType`, `HandleAsync`; `PlatformTaskTerminalException` for non-retryable); exemplar `Services/Provisioning/MoveTenantTaskHandler.cs`; worker `Services/PlatformTasks/PlatformTaskWorker.cs` (`RunOnStartup` gate line 75, `MaxRetries=5`, `VisibilityTimeout=10m`) | `WalletExpirySweepTaskHandler`. |
| Per-tenant advisory lock + serialized work | `src/Tamma.Api/Services/Provisioning/TenantMoveService.cs:843-923` (`pg_try_advisory_lock(hashtextextended(@tid,0))`) | the double-spend guard pattern for `AppendAsync`. |
| Tenant-scoped routing + RBAC | `Program.cs:1512` (`app.MapGroup("/api/v1/orgs").RequireAuthorization("MemberAccess")` + `.AddEndpointFilter<RequireTenantMembershipFilter>()`); filter `src/Tamma.Api/Authorization/RequireTenantMembershipFilter.cs` (`TenantRoleItemKey="TenantRole"`, line 30); inline admin gate `Endpoints/AlertEndpoints.cs:1008` `RequireTenantAdmin(http, out forbid)` reading `HttpContext.Items["TenantRole"]` | tenant top-up/read routes + member-403 on top-up. |
| Platform-owner gate | `Program.cs:986` `PlatformOwnerAccess` policy (`PlatformPermissionRequirement("platform_admin")`) | admin grant/refund/read routes. |
| Org+admin endpoint exemplar | `src/Tamma.Api/Endpoints/AlertEndpoints.cs` (admin section + tenant `/api/v1/orgs/{tenantId}/...` section, paging `DefaultPageSize=50`/`MaxPageSize=500`, cross-tenant 404 invariant ~617) | `WalletEndpoints` structure. |
| Tenant entity / FK | `src/Tamma.Data/Entities/Tenant.cs` (`Id`, `Slug`, `Plan`) | `BillingWalletLedger.TenantId` FK. |

### Sibling-story seams this plan consumes (must exist or be co-developed)

- **35-1** (`docs/stories/epic-35/story-35-1/...`, drafted): `BillingCustomer` (`StripeCustomerId ↔ TenantId`),
  Stripe.net on `Tamma.Api.csproj`, `StripeClientFactory`/`IBillingProvider`/`NullBillingProvider`,
  `BillingEvents` event-helper precedent, `BillingServiceCollectionExtensions.AddTammaBilling`,
  `Services/Billing/` namespace, single-user no-op seam (AC9).
- **35-5** (`docs/stories/epic-35/story-35-5/...`, drafted): `IBillingEventHandler`
  (`HandledEventTypes` + `HandleAsync(BillingWebhookContext, ct) → BillingFollowup?`),
  `BillingEventHandlerRegistry`, `BillingWebhookContext(StripeEvent, TenantId, RawPayload)`,
  `services.AddBillingEventHandler<T>()`, `NullBillingEventHandler` default. 35-10's handlers plug in here.
- **35-8** (spec `/tmp/pab_stories/35-8.json`, not yet authored): `InvoiceService` / `InvoiceWebhookHandler`
  finalize path, `BillingInvoice`/`BillingInvoiceLine` mirrors, `credit` line item type. 35-8 calls
  `IWalletCreditService.ApplyToInvoiceAsync` at finalize. **Coordinate the call site.**

### Gaps / decisions

- **No billing infrastructure exists on `main` yet** — `find apps/tamma-elsa/src -iname "*billing*"` is empty.
  Everything billing (incl. 35-1/35-5/35-8) is in-flight under Epic 35. This plan assumes 35-1 + 35-5 land
  first (hard prerequisites) and treats the 35-8 finalize call site as a published seam.
- **No `tenant.credit_balance` column and we will not add one** — derived balance only (AC1).
- **Stripe minor-unit ⇄ USD** — Stripe amounts are integer minor units (cents); convert `amount/100m`
  to the `numeric(12,4)` USD ledger. Research the exact Stripe.net field (`AmountReceived`) before coding.

---

## Architecture

**Append-only ledger → derived balance → Stripe-coupled mutations → DCB events**, all per-tenant-isolated:

1. **`BillingWalletLedger`** (CP entity/table) — immutable rows; `BalanceAfter` snapshot computed at
   append time; balance = `SUM(AmountUsd)`; unique idempotency key (`topup:{pi}`, `consume:{invoice}`,
   `expire:{lot}`, `refund:{charge}`, `grant:{guid}`/`promo:{code}:{tenant}`).
2. **`IWalletLedgerRepository.AppendAsync`** — the single race-safe write path: `pg_advisory_xact_lock`
   per tenant + `Serializable`, re-read balance, compute `BalanceAfter`, insert; idempotency-key
   collision returns the existing row (no throw, no double).
3. **`WalletService`** — top-up PaymentIntent (no ledger row at creation), admin grant, admin refund,
   wallet read DTO.
4. **`WalletCreditService.ApplyToInvoiceAsync`** — `min(available, amountDue)` as a Stripe credit +
   one `consume` row, idempotent by invoice id; called by 35-8 finalize before card charge.
5. **`WalletTopupWebhookHandler` / `WalletRefundWebhookHandler`** — `IBillingEventHandler` plug-ins for
   `payment_intent.succeeded` (wallet-only via metadata) / `charge.refunded`.
6. **`WalletExpirySweepScheduler` + `WalletExpirySweepTaskHandler`** — daily `PlatformQueuedTask` that
   appends `expire` rows for past-expiry lots, idempotent.
7. **`WalletEndpoints`** — tenant top-up/read (`RequireTenantMembershipFilter`, member-read,
   member-403-on-topup) + admin grant/refund/read (`PlatformOwnerAccess`).
8. **Mode gating** — SaaS only; single-user registers a no-op `IWalletCreditService` and maps no routes.

### Per-mode ownership (mandatory two-scoping answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns the wallet? | N/A — no Stripe (`NullBillingProvider`); routes unmapped, credit-apply is a no-op. | The tenant (`tenant_id`-keyed ledger). |
| Who can top-up? | N/A | `tenant_owner` / `tenant_admin` (`member` → 403). |
| Who can read balance/history? | N/A | `tenant_owner` / `tenant_admin` / `member` (read-only); platform owner any tenant. |
| Who can grant / refund / adjust? | N/A | platform owner only (`PlatformOwnerAccess`). |
| Mode source | `ITammaModeProvider` (`Services/PromptStore/TammaMode.cs`) — process-stable. | same |

---

## Task breakdown (TDD — tests first per task)

### Task 1 — `BillingWalletLedger` entity + migration + repository (core, AC1/AC7/AC12)

**Files:**
- New `src/Tamma.Data/Entities/BillingWalletLedger.cs` (see story Technical Design for the full shape).
- Modify `src/Tamma.Data/ControlPlaneDbContext.cs` — `public DbSet<BillingWalletLedger> BillingWalletLedger => Set<BillingWalletLedger>();`.
- Modify `src/Tamma.Data/TammaModelConfiguration.cs` (`ConfigureControlPlaneEntities`) — `billing_wallet_ledger` table, `ck_billing_wallet_ledger_type` CHECK, `numeric(12,4)` on amount columns, `(TenantId, CreatedAt)` index, **UNIQUE filtered** index on `IdempotencyKey` (NULLS NOT DISTINCT semantics), filtered open-lots index, FK to `tenants` cascade.
- New `src/Tamma.Data/Repositories/IWalletLedgerRepository.cs` + `WalletLedgerRepository.cs`:
  `AppendAsync` (advisory-lock + `Serializable`, compute `BalanceAfter`, idempotency-collision → return existing), `GetBalanceAsync`, `GetBalanceViewAsync`, `ListAsync` (paged), `FindExpiredLotsAsync`.
- Migration: `dotnet ef migrations add AddBillingWalletLedger --context ControlPlaneDbContext --output-dir Migrations/ControlPlane` (+ Designer + snapshot).

**Approach:** entity + config first (compile), then `dotnet ef migrations has-pending-model-changes` → must report none. `AppendAsync` opens a transaction, runs `SELECT pg_advisory_xact_lock(hashtextextended(@tid,0))` (mirror `TenantMoveService.cs:871`), `SELECT COALESCE(SUM(amount_usd),0)` for the tenant, sets `BalanceAfter = balance + entry.AmountUsd`, inserts; on `DbUpdateException` from the idempotency unique index, roll back and re-query the existing row to return it.

**Tests (first) — `WalletLedgerRepositoryTests` (real Postgres via `sg docker`):** append each entry type; `BalanceAfter` == running `SUM`; balance/availability derivation; idempotency collision returns existing row (no throw, no duplicate); `FindExpiredLotsAsync` only returns past-expiry open lots with positive remaining. Plus a migration apply/rollback + `has-pending-model-changes`=none test.

### Task 2 — `WalletService` + `WalletEvents` + `WalletEventTypes` (top-up intent, grant, refund, read; AC2/AC5/AC9/AC10)

**Files:**
- New `src/Tamma.Api/Services/Billing/WalletEventTypes.cs` (`BILLING.CREDIT.GRANTED|TOPPED_UP|CONSUMED|EXPIRED|REFUNDED` constants).
- New `src/Tamma.Api/Services/Billing/WalletEvents.cs` (static `DomainEvent` builders, mirroring 35-1 `BillingEvents`: `Tags = { tenantId, amountUsd, balanceAfter, reference, entryType }`, `Metadata = {"workflowVersion":"1.0.0","eventSource":"system"}`).
- New `IWalletService.cs` + `WalletService.cs`: `CreateTopupIntentAsync` (Stripe `PaymentIntentService.CreateAsync`, `Metadata {tenantId, purpose:"wallet_topup"}`, deterministic idempotency key, **no ledger row**), `GrantAsync` (append `grant`, reject negative, promo dedup via `promo:{code}:{tenant}`, emit `BILLING.CREDIT.GRANTED`), `RefundAsync` (append `refund`, clamp-non-negative + WARN, emit `BILLING.CREDIT.REFUNDED`), `GetWalletAsync` (DTO + paged entries).

**Approach:** resolve the Stripe client through 35-1's `StripeClientFactory`/`IBillingProvider`. All ledger writes go through `IWalletLedgerRepository.AppendAsync`. All events via `IEventRepository.AppendAsync`. Research the Stripe.net `PaymentIntentCreateOptions`/`PaymentIntentService` surface before coding.

**Tests (first) — `WalletServiceTests`:** top-up creates intent with correct metadata + idempotency key and writes no row (mocked `PaymentIntentService`); grant appends + emits, negative rejected, promo double-grant blocked; refund clamps at zero + WARN.

### Task 3 — `WalletTopupWebhookHandler` + `WalletRefundWebhookHandler` (35-5 plug-ins; AC3/AC9)

**Files:** new `Services/Billing/Handlers/WalletTopupWebhookHandler.cs` (`IBillingEventHandler`, claims `payment_intent.succeeded`, wallet-only via `purpose=wallet_topup` metadata, append `topup` keyed `topup:{pi}`, emit `BILLING.CREDIT.TOPPED_UP`) + `WalletRefundWebhookHandler.cs` (`charge.refunded`, wallet-only, `refund:{charge}`, emit `BILLING.CREDIT.REFUNDED`).

**Approach:** consume 35-5's `BillingWebhookContext` (`StripeEvent`, resolved `TenantId`, raw payload). Cast `ctx.StripeEvent.Data.Object` to the typed `Stripe.PaymentIntent`/`Stripe.Charge`. Return `null` (no follow-up) — work is inline + idempotent. Non-wallet objects → return `null` without a row.

**Tests (first) — `WalletTopupWebhookHandlerTests` / refund cases:** wallet `payment_intent.succeeded` → one `topup` row + event; duplicate delivery → no second row; non-wallet PaymentIntent → no row; `charge.refunded` for a wallet top-up → `refund` row + event.

### Task 4 — `WalletCreditService.ApplyToInvoiceAsync` + 35-8 finalize call site (AC4/AC7)

**Files:** new `IWalletCreditService.cs` + `WalletCreditService.cs` (`min(available, amountDue)` → Stripe credit (`CustomerBalanceTransactionService` / negative invoice line) + `consume` row keyed `consume:{invoice}` + emit `BILLING.CREDIT.CONSUMED`, all in one serialized append); modify `Services/Billing/InvoiceService.cs` (35-8) to inject `IWalletCreditService` and call `ApplyToInvoiceAsync` at `invoice.finalized` **before** the charge transition.

**Approach:** the apply is itself an `AppendAsync` (so it inherits the per-tenant advisory lock + `Serializable` double-spend guard). Compute apply amount from `GetBalanceViewAsync(...).AvailableUsd`. If 35-8 has not landed, publish the seam + a unit test against a fake invoice; coordinate the one-line injection when 35-8 merges. Single-user registers a no-op `IWalletCreditService` (returns 0).

**Tests (first) — `WalletCreditServiceTests`:** apply records `min(balance, amountDue)` as credit + `consume` row + event; zero balance → zero applied, no row; re-finalization (same invoiceId) → no second consume (idempotency key).

### Task 5 — Double-spend concurrency test (AC7/AC13 #5)

**Files:** new `tests/.../Billing/WalletDoubleSpendTests.cs` (real Postgres).

**Approach:** seed 10 USD; fire two concurrent `ApplyToInvoiceAsync` for the same tenant against two invoices summing > 10 USD; assert total consumed ≤ 10, balance never negative, correct `consume` row count. This test *is* the proof that the advisory-lock serialized append works — write it before finalizing `AppendAsync`'s locking.

### Task 6 — Expiry sweep: scheduler + task handler (AC8)

**Files:** new `Services/Billing/Tasks/WalletExpirySweepScheduler.cs` (`BackgroundService`, mode-gated, default daily, `RunOnStartup` gate mirroring `PlatformTaskWorkerOptions`, enqueues `PlatformQueuedTask{Type="billing.wallet.expire_sweep"}` via `IPlatformQueuedTaskRepository.EnqueueAsync`) + `Tasks/WalletExpirySweepTaskHandler.cs` (`IPlatformTaskHandler`, `TaskType="billing.wallet.expire_sweep"`, per lot append `expire` keyed `expire:{lot}` + emit `BILLING.CREDIT.EXPIRED`; malformed payload → `PlatformTaskTerminalException`; transient → rethrow for retry).

**Tests (first) — `WalletExpirySweepTests`:** expired lot with remaining → one `expire` row + event; re-run → no second expire (idempotent); non-expired untouched; fully-consumed expired lot → no expire row.

### Task 7 — `WalletEndpoints` + route mapping + DI extension (AC2/AC5/AC6/AC6b/AC11)

**Files:** new `src/Tamma.Api/Endpoints/Billing/WalletEndpoints.cs` (tenant: `POST topup` [member→403 via `RequireTenantAdmin` helper], `GET wallet` [member-read]; admin: `POST grant`, `POST refund`, `GET wallet/{tenantId}`); new `src/Tamma.Api/Extensions/WalletServiceCollectionExtensions.cs` (`AddTammaWallet` — mode-aware: SaaS registers services + handlers + scheduler; single-user registers no-op `IWalletCreditService` only); modify `Program.cs` — `AddTammaWallet()` + map tenant routes in the `/api/v1/orgs` group (line 1512) with `RequireTenantMembershipFilter`, admin routes under `/api/v1/admin` with `PlatformOwnerAccess`, **SaaS-mode-gated** route mapping.

**Approach:** mirror `AlertEndpoints` admin+tenant split. Tenant routes filter `tenant_id = {tenantId}` (cross-tenant 404). Register `WalletTopupWebhookHandler`/`WalletRefundWebhookHandler` via `services.AddBillingEventHandler<T>()` inside `AddTammaWallet` (SaaS only).

**Tests (first) — `WalletEndpointsTests`:** RBAC matrix (member 403 on top-up, member 200 on read; admin grant/refund require `PlatformOwnerAccess`; cross-tenant 404); tenant isolation (A's credit never in B's wallet); single-user routes 404; top-up returns `{paymentIntentId, clientSecret}` (Stripe mocked).

### Task 8 — Single-user seam + tenant-isolation integration tests (AC6b/AC11/AC13)

**Files:** new `tests/.../Billing/WalletSingleUserSeamTests.cs`, extend `WalletEndpointsTests` / add an isolation integration test via `WebApplicationFactory`.

**Approach:** single-user markers → routes unmapped (404), `IWalletCreditService` no-op, zero Stripe calls. Two-tenant isolation: independent ledgers, no cross-tenant read/offset.

---

## Sequencing & dependencies

```
Task 1 (entity + migration + repository)  ── hard prerequisite for everything
   ├── Task 2 (WalletService + events)
   │      └── Task 3 (webhook handlers)         needs 35-5 registry
   │      └── Task 4 (credit-apply service)      needs 35-8 finalize call site
   │             └── Task 5 (double-spend test)  pins Task 1 locking + Task 4
   │      └── Task 6 (expiry sweep)              needs Epic 28 task worker
   └── Task 7 (endpoints + DI + routing)         needs Tasks 2/4
          └── Task 8 (single-user + isolation tests)
```

- **External prerequisites:** Story 35-1 (Stripe.net, `BillingCustomer`, `StripeClientFactory`,
  `NullBillingProvider`) and Story 35-5 (`IBillingEventHandler` registry, `BillingWebhookContext`)
  must be merged before Tasks 2-4/7 can wire fully. Story 35-8's finalize call site (Task 4) is
  coordinated — publish the `IWalletCreditService` seam first if 35-8 is not yet merged.
- Task 1 + Task 5 are the load-bearing correctness work; do them on real Postgres (not SQLite — the
  advisory lock + `Serializable` semantics need the real engine).

---

## Risks + mitigations

- **Double-spend under concurrent finalization (High):** per-tenant `pg_advisory_xact_lock` +
  `Serializable` serialized append in `WalletLedgerRepository.AppendAsync`; unique `consume:{invoiceId}`
  backstop; dedicated `WalletDoubleSpendTests` on real Postgres is the proof (Task 5).
- **Duplicate top-up credit from at-least-once webhooks (High):** `topup:{paymentIntentId}` UNIQUE
  idempotency key; collision returns the existing row.
- **Crediting an unpaid PaymentIntent (High):** ledger row only on `payment_intent.succeeded` via the
  35-5 handler — never at intent creation (Task 2 writes no row; Task 3 does).
- **Stripe ⇄ ledger drift (Medium):** apply credit as a Stripe customer-balance / negative invoice line
  **before** charge so Stripe charges the net amount (Task 4).
- **Refund below zero (Medium):** clamp + WARN; refund cannot exceed credited-from-that-charge balance.
- **Expiry double-expire (Medium):** `expire:{lotId}` UNIQUE key; idempotent re-run (Task 6).
- **Migration discipline (Medium):** `billing_wallet_ledger` is additive — still run
  `has-pending-model-changes` after the migration (expect none); entity config in
  `TammaModelConfiguration` only (the established single source).
- **Sibling-story drift (Medium):** 35-1/35-5/35-8 are concurrently in-flight — pin the consumed seams
  (`StripeClientFactory`, `IBillingEventHandler`/`BillingWebhookContext`, `InvoiceService` finalize) in
  the first task that touches each, and fail the build loudly if a seam signature drifts.
- **Single-user accidental Stripe coupling (Medium):** `NullBillingProvider` seam; routes unmapped;
  `WalletSingleUserSeamTests` asserts zero SDK calls (Task 8).
- **Stripe.net API drift (Medium):** research current `PaymentIntentService` /
  `CustomerBalanceTransactionService` / `RequestOptions.IdempotencyKey` / minor-unit fields before
  coding; mock at the service-interface boundary.

---

## Acceptance criteria (mirror of the story)

- [ ] Append-only `BillingWalletLedger` (CP) with `{grant|topup|consume|expire|refund|adjustment}`,
      signed `AmountUsd`, computed `BalanceAfter`, `Reference`/`ReferenceKind`, `ExpiresAt`, unique
      `IdempotencyKey`; balance derived (`SUM`), never mutated in place. (Task 1)
- [ ] `POST /api/v1/orgs/{tenantId}/billing/wallet/topup` creates a Stripe one-time PaymentIntent;
      `payment_intent.succeeded` appends a `topup` row idempotently by intent id. (Tasks 2, 3)
- [ ] Available credit applied to invoices before card charge in the 35-8 finalize path, as a Stripe
      credit line + a `consume` ledger entry. (Task 4)
- [ ] Admin grant endpoint (`PlatformOwnerAccess`) issues promo/support credits with optional expiry;
      emits `BILLING.CREDIT.GRANTED`. (Tasks 2, 7)
- [ ] `GET .../billing/wallet` returns balance + paged ledger history for owner/admin/member-read. (Tasks 2, 7)
- [ ] Credit application atomic + race-safe (no double-spend) under concurrent finalization; expired
      entries swept by a `PlatformQueuedTask` recording an `expire` entry. (Tasks 4, 5, 6)
- [ ] DCB events `BILLING.CREDIT.GRANTED|TOPPED_UP|CONSUMED|EXPIRED|REFUNDED` with
      `{tenantId, amountUsd, reference, ...}` tags. (Tasks 2, 3, 4, 6)
- [ ] Per-mode + per-tenant: SaaS RBAC (member-read, admin-grant), single-user no-op + unmapped routes,
      tenant isolation. (Tasks 7, 8)
- [ ] Migration applies + rolls back; `has-pending-model-changes` = none. (Task 1)
- [ ] Unit + integration tests: ledger append + derived balance, idempotent top-up, credit-before-card,
      double-spend prevention, expiry sweep, admin grant RBAC, single-user seam, tenant isolation.
      (Tasks 1-8)
