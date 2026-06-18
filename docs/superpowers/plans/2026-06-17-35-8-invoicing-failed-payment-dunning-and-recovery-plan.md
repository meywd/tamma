# Story 35-8 — Invoicing, Failed-Payment Dunning & Recovery (implementation plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan phase-by-phase. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every phase writes tests
> before implementation. C# suites run via `sg docker -c "dotnet test ..."` (session docker group
> is stale; build needs no wrapper).

**Story:** `docs/stories/epic-35/story-35-8/35-8-invoicing-failed-payment-dunning-and-recovery.md`
· **Epic 35** (Billing & Payments, C#) · **P0** · est **5-6 days** · today **2026-06-17**.

---

## Goal

Mirror Stripe invoices into the control plane with a line-item breakdown (base / metered-overage /
credit), expose a tenant-scoped invoice history + PDF retrieval, and run a deterministic, config-
driven failed-payment dunning state machine (`active → past_due → grace → suspended`, recover on
later payment) that escalates via the existing email outbox, suspends only after a grace period,
and writes the suspension signal Story 35-6 reads to hard-block platform-provided usage — all
BYOK-aware and audited via DCB events.

## Non-goals (YAGNI guard)

- NO webhook endpoint, signature verification, dedup, or `BillingWebhookEvent` — that is **Story
  35-5**. 35-8 only registers `IBillingEventHandler` implementations into 35-5's dispatch seam.
- NO quota/usage enforcement in the request path — that is **Story 35-6**. 35-8 writes
  `BillingDunningState.Stage = suspended` and calls `IQuotaService.InvalidateAsync`; 35-6 reads it
  and blocks. 35-8 ships zero code in the LLM/dispatch gate.
- NO subscription lifecycle (create/upgrade/downgrade/cancel/trial) — **Story 35-4**. 35-8 only
  reads `BillingSubscription` for invoice period + plan/seat context.
- NO metering pipeline / Stripe meter reporting — **Story 35-3**. 35-8 consumes the metered-overage
  lines that already appear on the Stripe invoice.
- NO direct SMTP / transport calls. Dunning + dispute mail is `INSERT` into the existing outbox
  tables only (`OutboxSmtpSender`/`ResendEmailService` deliver).
- NO new delivery channels, no portal UI (Story 35-7 renders this data), no admin cross-tenant
  invoice route (platform-side inspection rides 35-5's webhook-events endpoint + the DCB stream).
- NO Stripe smart-retries — Tamma owns the retry/escalation cadence locally.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### Real infrastructure 35-8 builds on (all confirmed present)

| Seam | File | Notes |
|---|---|---|
| Control-plane DbContext | `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | DbSets declared here; `DomainEvents`, `PlatformQueuedTasks`, `PlatformEmailOutbox`, `EmailOutbox`, `Plans`, `Tenants` all present (lines 36–213). 35-8 adds `BillingInvoices`, `BillingInvoiceLines`, `BillingDunningStates`. |
| Entity model config (single source) | `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` → `ConfigureControlPlaneEntities` | Per 35-5, this is where indexes/CHECKs live — same place `AlertRule`/`GitHubWebhookDelivery` are configured. |
| DCB append | `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` (`AppendAsync(DomainEvent)`, line 7); impl `EventRepository.cs:49` | `DomainEvent` = `{ Id, Type, TenantId, Tags(json), Metadata(json), Data(json), CreatedAt, SequenceNumber }` (`Entities/DomainEvent.cs`). |
| Platform task queue | `IPlatformQueuedTaskRepository.cs` (`EnqueueAsync`, `CompleteAsync`, `FailAsync`, `DeadLetterAsync`, `GetAsync`); handler contract `Services/PlatformTasks/IPlatformTaskHandler.cs` (`TaskType` + `HandleAsync`); worker `PlatformTaskWorker.cs`; registry `IPlatformTaskHandlerRegistry.cs` | `PlatformQueuedTask` = `{ Id, Type, TenantId?, InstallationId?, Payload, Status, RetryCount, ... }` (`Entities/PlatformQueuedTask.cs`). Register via `services.AddPlatformTaskHandler<T>()`. |
| Email outbox (no direct SMTP) | `Entities/PlatformEmailOutboxMessage.cs` (system mail) + `Entities/EmailOutboxMessage.cs` (tenant mail) | Both carry `{ Template, ToAddress, Subject, HtmlBody, TextBody, Status, Attempts, NextAttemptAt, TenantId? }`. Bodies are CodeQL-tainted private data — never log. Drained by existing `OutboxSmtpSender`/`ResendEmailService`. |
| Tenant + plan | `Entities/Tenant.cs` (`Id, Slug, Plan, ...`), `Entities/Plan.cs` (`Slug, MonthlyPriceUsd, Quotas`) | Invoice `TenantId` FK → `tenants.Id`. |
| Auth policies | `Tamma.Api/Program.cs` — `OwnerAccess` (971), `PlatformOwnerAccess` (986), `MemberAccess` (991), `PromptManage` (1012) | Tenant RBAC via `Authorization/RequireTenantMembershipFilter.cs` (`TenantRoleItemKey`) + `Authorization/TenantRoleHierarchy.cs` (`Level(role)`), as used in `Endpoints/OrgEndpoints.cs:181-210` and `Endpoints/AlertEndpoints.cs`. |
| Mode | `Tamma.Api/Services/PromptStore/TammaMode.cs` (`ITammaModeProvider`, `TammaMode.SingleUser|SaaS`) | Process-stable; gates SaaS-only registration (same pattern 35-1/35-5 use). |
| Endpoint exemplar | `Tamma.Api/Endpoints/AlertEndpoints.cs` | Admin section (`/api/v1/admin/alerts/*`) + tenant section (`/api/v1/orgs/{tenantId}/alerts/*`, lines 562–760). 35-8's `InvoiceEndpoints` mirrors the **tenant** read pattern at `/api/v1/billing/invoices`. |

### Sibling-story contracts 35-8 plugs into (from drafted story files)

- **35-5** (`docs/stories/epic-35/story-35-5/...md`): the dispatch seam —
  `IBillingEventHandler { IReadOnlyCollection<string> HandledEventTypes; Task<BillingFollowup?>
  HandleAsync(BillingWebhookContext ctx, ct); }`, `BillingWebhookContext(Stripe.Event, Guid
  TenantId, string RawPayload)`, `BillingFollowup(string TaskType, string Payload)`,
  `BillingEventHandlerRegistry` (mirrors `PlatformTaskHandlerRegistry`),
  `services.AddBillingEventHandler<T>()`, and the `billing.webhook.followup` `PlatformQueuedTask`
  fast-ack path. 35-5 emits `BILLING.INVOICE.FINALIZED/PAID/PAYMENT_FAILED` + `BILLING.DISPUTE.OPENED`
  via the **NullBillingEventHandler** even before 35-8's handler exists — 35-8 takes over the
  mirror + dunning logic for those types.
- **35-1**: `BillingCustomer { TenantId(unique), StripeCustomerId, BillingMode }`,
  `BillingMode { PlatformProvided, Byok }` (`Tamma.Core/Billing/BillingMode.cs`),
  `NullBillingProvider` single-user seam, `BillingServiceCollectionExtensions`, Stripe.net.
- **35-4**: `BillingSubscription { Status, PlanSlug, CurrentPeriodStart/End, Seats }`.
- **35-6**: `IQuotaService` with `InvalidateAsync(tenantId)` and a `QuotaDecision.HardBlock` reason
  taxonomy; 35-6 reads `BillingDunningState.Stage == suspended` for platform-provided calls; BYOK
  token calls are exempt.

### Gaps / risks surfaced by the scan

- **No Stripe code exists in C# yet** (`grep -ril stripe apps/tamma-elsa/src` → only a Studio razor
  page). Stripe.net, `BillingCustomer`, the webhook seam are **prerequisites** (35-1/35-5). This
  plan assumes they are merged; if not, 35-8 is blocked.
- The 35-5 handler returns a **single** `BillingFollowup`; dunning may need to both schedule an
  advance *and* enqueue an email. Resolution: the email enqueue is cheap (an `INSERT`) and runs
  inline in the handler; only the *time-delayed* advance becomes the `billing.webhook.followup`/
  `billing.dunning.advance` task. Keep heavy/Stripe round-trips out of the inline path.

---

## Architecture

**Pure core + imperative shell.** `DunningStateMachine.Next(stage, attempts, opts)` and
`InvoiceService.ClassifyLine(...)` are clock-free pure functions (100%-coverage critical paths).
All I/O — projection upsert, DCB append, task scheduling, email-outbox enqueue, quota
invalidation — lives in the imperative shell with an injected `TimeProvider`.

```
Stripe invoice.*  ──(35-5 webhook + dispatch)──▶  InvoiceWebhookHandler : IBillingEventHandler
                                                       │
                                 ┌─────────────────────┼──────────────────────────┐
                                 ▼                      ▼                          ▼
                         InvoiceService           DunningStateMachine        BillingInvoiceEvents
                       (upsert mirror +          (transition + schedule +     (DCB append via
                        line split + DCB)         email-outbox enqueue)        IEventRepository)
                                 │                      │
                                 ▼                      ▼
                      BillingInvoice/Line       BillingDunningState ──(suspended)──▶ 35-6 QuotaService
                                                        │  (+IQuotaService.InvalidateAsync)
                                                        ▼
                                          PlatformQueuedTask "billing.dunning.advance"
                                                        │ (fires at NextAdvanceAt)
                                                        ▼
                                          DunningAdvanceTaskHandler : IPlatformTaskHandler
```

---

## Phased task breakdown (test-first / TDD)

### Phase 0 — Confirm prerequisites & read contracts (no code)

- [ ] Verify `BillingCustomer`, `BillingMode`, `NullBillingProvider`, `BillingServiceCollectionExtensions`
      (35-1) and the `IBillingEventHandler`/`BillingWebhookContext`/`BillingFollowup`/`AddBillingEventHandler<T>`
      seam (35-5) and `BillingSubscription` (35-4) and `IQuotaService.InvalidateAsync` + the
      `HardBlock` reason taxonomy (35-6) exist in the tree. If any is missing, **stop** — 35-8 is blocked.
- [ ] Re-read 35-5 §"Handler contract" and 35-6 §"suspension read path" to pin exact signatures.
- [ ] `grep -rn "billing" apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` to see how
      sibling billing entities are configured (copy the index/CHECK idiom).

### Phase 1 — Pure cores (TDD, zero I/O)

**Files:** `Services/Billing/DunningOptions.cs`, `Services/Billing/IDunningStateMachine.cs`
(+ `DunningTransition` record), `Services/Billing/DunningStateMachine.cs` (transition table only),
`Services/Billing/InvoiceService.cs` (`ClassifyLine` static helper stub).

- [ ] **Tests first** — `tests/Tamma.Api.Tests/Billing/DunningStateMachineTests.cs`:
      `Next("active", 1, opts)` → `past_due` + delay `RetryDelaysHours[0]`; `Next` walks
      `past_due → grace → suspended` as attempts climb / retries exhaust; grace→suspend has
      `Suspend = true, DelayUntilNext = null`; custom `RetryDelaysHours`/`GraceHours` honored;
      recovery target is always `active`. Pure — no DB, no clock except via the schedule.
- [ ] **Tests first** — `tests/Tamma.Api.Tests/Billing/InvoiceServiceTests.cs` (classification
      subset): `ClassifyLine` → `base` (recurring price), `metered_overage` (metered/`tamma_meter`),
      `credit` (negative amount / credit-note); negative-amount wins on a base price id; mixed
      invoice splits + totals reconcile to `AmountDue`.
- [ ] Implement `DunningOptions` (bound to `Billing:Dunning:*`, defaults `[24,72,120]`h /
      `168`h), `DunningTransition`, `DunningStateMachine.Next`, and `ClassifyLine`. Green.

### Phase 2 — Entities + EF migration (TDD around model)

**Files:** `Entities/BillingInvoice.cs`, `Entities/BillingInvoiceLine.cs`,
`Entities/BillingDunningState.cs`; modify `ControlPlaneDbContext.cs` (3 DbSets) +
`TammaModelConfiguration.cs` (config, indexes, CHECKs); new additive migration under
`Migrations/ControlPlane/`.

- [ ] Add entities exactly as the story's "Key entity signatures" section specifies (minor-unit
      `long` money; `Kind`/`Status`/`Stage` text domains).
- [ ] In `TammaModelConfiguration.ConfigureControlPlaneEntities`: `BillingInvoice` → filtered
      UNIQUE on `StripeInvoiceId` (`WHERE StripeInvoiceId IS NOT NULL`), index on
      `(TenantId, CreatedAt DESC)`, CHECK on `Status`; `BillingInvoiceLine` → FK→invoice cascade,
      CHECK on `Kind`; `BillingDunningState` → UNIQUE on `TenantId`, CHECK on `Stage`.
- [ ] `dotnet ef migrations add BillingInvoicesAndDunning` (CP context). Run
      `dotnet ef migrations has-pending-model-changes` → expect **none**. Verify up/down apply
      cleanly (additive — no baseline CHECK edit, so Phase-0 collapsed-baseline rules don't bite).
- [ ] **Test:** a DbContext round-trip persists + reads an invoice with lines; the filtered unique
      index rejects a duplicate `StripeInvoiceId`; `BillingDunningState` rejects a second row per
      tenant.

### Phase 3 — InvoiceService projection + DCB (TDD)

**Files:** `Services/Billing/IInvoiceService.cs`, `Services/Billing/InvoiceService.cs` (full),
`Services/Billing/BillingInvoiceEvents.cs` (DCB constants).

- [ ] **Tests first** (extend `InvoiceServiceTests.cs`): `ProjectAsync(invoice, tenantId)` inserts
      one `BillingInvoice` + N lines and appends the right `BILLING.INVOICE.*` DCB event
      (FINALIZED on finalize, PAID on paid) with tags `{ tenantId, invoiceId, stage }`; **replay**
      same `StripeInvoiceId` upserts (no dup row, no second event); `open→paid` stamps `PaidAt`;
      money in minor units; tenant-isolation (project for A never touches B).
- [ ] Implement `ProjectAsync` (idempotent upsert keyed on `StripeInvoiceId`, line split via
      `ClassifyLine`, DCB append via `IEventRepository.AppendAsync`), `ListAsync`, `GetAsync`
      (both tenant-filtered). `BillingInvoiceEvents` constants:
      `BILLING.INVOICE.FINALIZED/PAID/DISPUTED`, `BILLING.PAYMENT.FAILED`,
      `BILLING.DUNNING.ESCALATED`, `BILLING.TENANT.SUSPENDED/REINSTATED`.

### Phase 4 — Dunning shell: transitions + scheduling + email + suspend/reinstate (TDD)

**Files:** `Services/Billing/DunningStateMachine.cs` (imperative methods),
`Services/Billing/BillingDunningEmailComposer.cs`, `Services/Billing/DunningAdvanceTaskHandler.cs`.

- [ ] **Tests first** — extend `DunningStateMachineTests.cs` (now with fakes for
      `IPlatformQueuedTaskRepository`, `IEventRepository`, the outbox DbSets, `IQuotaService`,
      injected `TimeProvider`): `OnPaymentFailedAsync` bumps attempts, transitions, schedules a
      `billing.dunning.advance` task at `now + delay`, enqueues the stage email
      (`PlatformEmailOutbox`/`EmailOutbox`), emits `BILLING.PAYMENT.FAILED` + `BILLING.DUNNING.ESCALATED`;
      idempotent on `(invoiceId, attemptCount)` (replay = no double-advance);
      `AdvanceAsync` grace→suspended emits `BILLING.TENANT.SUSPENDED`, sets `SuspendedAt`, calls
      `IQuotaService.InvalidateAsync`; `OnPaymentRecoveredAsync` from **every** stage incl.
      `suspended` → `active`, cancels the pending task, enqueues `dunning-recovered`, emits
      `BILLING.TENANT.REINSTATED`, calls `InvalidateAsync`.
- [ ] **Email test** — `BillingDunningEmailComposerTests.cs`: each stage → distinct template
      (`dunning-past-due|dunning-grace|dunning-suspended|dunning-recovered`) with attempt count +
      next-retry in the body; **assert no direct transport call** (mock `IEmailTransport`/
      `OutboxSmtpSender` never invoked — outbox `INSERT` only).
- [ ] Implement the shell + `DunningAdvanceTaskHandler : IPlatformTaskHandler`
      (`TaskType = "billing.dunning.advance"`, calls `DunningStateMachine.AdvanceAsync`).

### Phase 5 — Webhook handler wiring (TDD against the 35-5 seam)

**Files:** `Services/Billing/InvoiceWebhookHandler.cs`; modify
`Extensions/BillingServiceCollectionExtensions.cs` (`AddBillingEventHandler<InvoiceWebhookHandler>()`,
`AddPlatformTaskHandler<DunningAdvanceTaskHandler>()`, register `IInvoiceService`/`IDunningStateMachine`/
`DunningOptions`).

- [ ] **Tests first** — `tests/Tamma.Api.Tests/Billing/InvoiceWebhookHandlerTests.cs`:
      `HandledEventTypes` = `{ invoice.created, invoice.finalized, invoice.paid,
      invoice.payment_failed, charge.dispute.created }`; `invoice.finalized` → projects + DCB;
      `invoice.paid` → projects + `OnPaymentRecoveredAsync`; `invoice.payment_failed` →
      projects + `OnPaymentFailedAsync` and returns a `BillingFollowup` (no inline Stripe round-trip);
      `charge.dispute.created` → `Stage = flagged` + `BILLING.INVOICE.DISPUTED` +
      `PlatformEmailOutbox` `dispute-opened` + **no auto-suspend**.
- [ ] Implement the handler dispatching on `ctx.StripeEvent.Type`; resolve the typed object
      (`(Stripe.Invoice)evt.Data.Object` etc.). Wire registration; assert DI resolves it in a
      `Program.cs`-host test (SaaS mode).

### Phase 6 — Invoice read API (TDD)

**Files:** `Endpoints/Billing/InvoiceEndpoints.cs`; map in `Program.cs` (SaaS-gated, `MemberAccess`).

- [ ] **Tests first** — `tests/Tamma.Api.Tests/Billing/InvoiceEndpointsTests.cs`:
      `GET /api/v1/billing/invoices` returns the caller's tenant invoices paged newest-first;
      `GET /invoices/{id}` returns detail + lines + `hosted_invoice_url`/`pdf_url` + dunning summary;
      **cross-tenant id → 404**; `member`/`tenant_admin`/`tenant_owner` all read; unauthenticated → 401;
      single-user mode → "billing is SaaS-only".
- [ ] Implement endpoints mirroring `AlertEndpoints` tenant section: read active tenant id from the
      principal (`ClaimsPrincipalExtensions`), filter every query by it, paging defaults 50/max 200.

### Phase 7 — Suspend↔reinstate ↔ 35-6 integration + isolation (TDD)

**Files:** `tests/Tamma.Api.Tests/Billing/DunningSuspensionIntegrationTests.cs`.

- [ ] Drive `payment_failed` → `suspended`; assert `QuotaService` (35-6) returns
      `HardBlock(billing_suspended)` for a platform-provided call and `Allowed` for a BYOK call on
      the same tenant; after `invoice.paid`, both `Allowed` (reinstate + `InvalidateAsync`).
- [ ] Tenant-isolation: A's `payment_failed` never mutates B's `BillingDunningState`; A's invoices
      never appear in B's list; DCB events carry the right `tenantId`.
- [ ] Full suite green: `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests"`.

---

## Sequencing & dependencies

```
Phase 0 (verify prereqs)
  └─▶ Phase 1 (pure cores)
        └─▶ Phase 2 (entities + migration)
              └─▶ Phase 3 (InvoiceService projection + DCB)
                    └─▶ Phase 4 (dunning shell + email + suspend/reinstate + task handler)
                          └─▶ Phase 5 (webhook handler wiring into 35-5 seam)
                                └─▶ Phase 6 (read API)        ── parallel-safe with Phase 7 ──
                                      └─▶ Phase 7 (35-6 integration + isolation)
```

- **Hard external prerequisites (merged before starting):** 35-5 (dispatch seam), 35-1
  (`BillingCustomer`/`BillingMode`/Stripe.net/`NullBillingProvider`), 35-6 (`IQuotaService`), and
  35-4 (`BillingSubscription`, for period/seat context).
- Phases 1–2 have no sibling dependency and can begin as soon as the entity/EF tooling is set up.
- Phase 5 is the only phase that hard-couples to the 35-5 seam; if 35-5 is mid-flight, stub
  `IBillingEventHandler` locally to the agreed signature and re-point at merge.

## Risks + mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| Stripe smart-retries fight Tamma's local cadence (double dunning) | High | Disable Stripe smart-retries; Tamma's `billing.dunning.advance` schedule is the single source of truth. Document in Stripe setup runbook. |
| Webhook replay double-advances dunning or double-charges line items | High | Idempotent upsert on `StripeInvoiceId` (second line of defense behind 35-5's `BillingWebhookEvent` dedup); `OnPaymentFailedAsync` idempotent on `(invoiceId, attemptCount)`. |
| Suspending a BYOK tenant blocks their token usage (wrong) | High | Suspension writes a *signal*; 35-6 enforcement exempts BYOK token calls. 35-8 ships no enforcement; Phase-7 test pins BYOK-allowed-while-suspended. |
| Money rounding / currency drift | High | Store minor-unit `long` (never decimal/float); currency on every row; reconcile line sum to `AmountDue` in a test. |
| Dunning email storm on a flapping gap | Medium | One email per *stage transition* (not per webhook); outbox dedup is at the row level; recovery cancels the pending advance task. |
| Stale quota block after reinstate (cache TTL) | Medium | Call `IQuotaService.InvalidateAsync` on both suspend and reinstate; ERROR-log if it fails (block may be stale until TTL — acceptable, bounded). |
| Single `BillingFollowup` can't carry both schedule + email | Low | Email is a cheap inline `INSERT` in the handler; only the time-delayed advance becomes the task. |
| Migration discipline | Low | `config`/entity config in `TammaModelConfiguration` only; additive table; verify `has-pending-model-changes` → none; up/down clean. |

## Acceptance criteria (mirror of the story)

- [ ] `BillingInvoice` + `BillingInvoiceLine` mirrors populated idempotently from `invoice.*`
      webhooks with `{ amount_due, amount_paid, status, hosted_invoice_url, pdf_url, period }` and
      lines split `base` / `metered_overage` / `credit`.
- [ ] `GET /api/v1/billing/invoices` (paged) + `GET /api/v1/billing/invoices/{id}` (detail + PDF
      link) readable by `tenant_owner`/`tenant_admin`/`member`; cross-tenant → 404.
- [ ] Dunning state machine advances `active → past_due → grace → suspended` on
      `invoice.payment_failed` via a `PlatformQueuedTask` schedule and recovers to `active` on a
      later `invoice.paid` (from any stage incl. `suspended`).
- [ ] Escalating dunning emails enqueued through `PlatformEmailOutboxMessage`/`EmailOutboxMessage`
      (no direct SMTP), with attempt count + next-retry surfaced on the invoice API.
- [ ] Terminal failure after grace → `BillingDunningState.Stage = suspended` that Story 35-6 reads
      to hard-block platform-provided usage; BYOK token usage continues but the platform/seat fee
      stays owed.
- [ ] DCB events `BILLING.INVOICE.FINALIZED/PAID`, `BILLING.PAYMENT.FAILED`,
      `BILLING.DUNNING.ESCALATED`, `BILLING.TENANT.SUSPENDED/REINSTATED`, `BILLING.INVOICE.DISPUTED`
      emitted with tags `{ tenantId, invoiceId, stage }`.
- [ ] `charge.dispute.created` flips invoice/tenant to `flagged` and notifies platform admins (no
      auto-suspend).
- [ ] Unit + integration tests cover projection + line split, dunning advance/recover, email-outbox
      enqueue (no direct send), suspend→reinstate (35-6 read), dispute handling, and tenant isolation;
      Stripe SDK + providers mocked. Coverage targets met (critical paths 100%).
