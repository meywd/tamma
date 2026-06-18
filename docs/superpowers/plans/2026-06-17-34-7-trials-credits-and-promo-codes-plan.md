# Story 34-7 — Trials, Credits & Promo Codes (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is **test-first (TDD)** — every task writes its
> tests before implementation. C# test runner: `sg docker -c "dotnet test …"` for docker-bound
> suites (build needs no wrapper) — see `reference_dotnet_test_docker`.

**Story:** `docs/stories/epic-34/story-34-7/34-7-trials-credits-and-promo-codes.md` ·
**Epic 34 — Pricing, Plans & Entitlements** · Priority P1 · Est. 4-5 days · Deps: 34-1, 34-4, 34-5

---

## Goal

Add the three acquisition/retention pricing primitives — **time-boxed trials**, **prepaid/granted
USD credits** (append-only, never-negative ledger), and **promo codes** (percent or fixed
discount) — to the C# control plane (`apps/tamma-elsa`). Surface a **credit-aware net price**
(`net = max(0, sell − promo − credits)`, recurring charge waived during a trial) that Billing
(Epic 35) consumes. Every mutation is audited via DCB + platform events, gated by per-mode RBAC.

## Non-goals (YAGNI guard)

- **No markup/margin math.** `sellPriceUsd` comes from 34-5's `IUsagePricingEngine`; this story
  decorates it, never re-implements `MarginPolicy`/cost-basis (34-5 boundary note: it is the
  canonical owner).
- **No plan-assignment logic.** Trial start/convert/downgrade *delegates* to 34-4's
  `IPlanAssignmentService` (`AssignAsync`/`CancelAsync`); we do not duplicate `TenantPlanAssignment`
  transitions or the partial-unique-active-row machinery.
- **No money movement / invoicing / proration.** Epic 35 charges; this story computes the *net* and
  emits the trial-conversion + remainder signals.
- **No quota/usage enforcement.** Epic 34 enforcement + Epic 20 metering own that; we only flag the
  unfunded consume remainder.
- **No new payment SDK dependency.** Stripe stays in Epic 35; tests mock the upstream seams.
- **No per-user credit/trial layer in SaaS.** Credits/trials are tenant-scoped (CLAUDE.md
  two-scoping rule: SaaS principal = `tenant_id`); member users are read-only.

---

## Current-state findings (verified 2026-06-17, repo @ main)

### What exists and is reused

| Seam | Location (verified) | Use |
|---|---|---|
| Control-plane DbContext | `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` (DbSets at ~33-209; e.g. `Plans` L76, `BudgetConfigs` L205, `DomainEvents` L199) | Add 4 new `DbSet`s |
| Entity model config (single source) | `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | CHECKs, partial unique indexes, FKs (mirror the 34-4 `TenantPlanAssignment` config sketch) |
| Migrations | `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/` (baseline `20260609205701_InitialControlPlane`) | Additive `AddTrialsCreditsPromos` migration |
| DCB append + tags pattern | `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` (`AppendAsync(DomainEvent)`); emit style at `Tamma.Api/Endpoints/OrgEndpoints.cs:1043-1058` (`Tags = JsonSerializer.Serialize(...)`, `Data = ...`) | Emit `CREDIT.*` / `PROMO.*` / `TENANT.TRIAL.*` |
| `DomainEvent` shape | `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` (`Type`, `TenantId`, `Tags`, `Metadata`, `Data`, `SequenceNumber`) | Event rows |
| Platform-audit mirror | `apps/tamma-elsa/src/Tamma.Data/Abstractions/IPlatformEventPublisher.cs` (`AppendAndPublishAsync(PlatformEvent)`) | Admin/platform audit timeline |
| Tenant-scoped financial entity precedent | `apps/tamma-elsa/src/Tamma.Data/Entities/BudgetConfig.cs` (decimal `LimitUsd`, nullable `TenantId`) | Pattern for USD decimal columns |
| Plan entity | `apps/tamma-elsa/src/Tamma.Data/Entities/Plan.cs` (`Slug`, `MonthlyPriceUsd`, `Quotas`, `IsActive`, `PlacementPolicy`) | Plan slug ↔ id; trial-eligibility (34-1 adds the flag) |
| Markup engine (34-5) | `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/UsagePricingEngine.cs` (NEW in 34-5), cost basis `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderPricingService.cs` (`Compute(provider, model, in, out)` L53) | `sellPriceUsd` source to decorate |
| Assignment lifecycle (34-4) | `apps/tamma-elsa/src/Tamma.Api/Services/Pricing/PlanAssignmentService.cs` + `IPlanAssignmentService` (NEW in 34-4) | Trial start/convert/downgrade delegation |
| Pricing endpoint files | `PricingEndpoints.cs` (NEW in 34-2), `Endpoints/Admin/AdminPricingEndpoints.cs` (NEW in 34-5) | Extend with redeem/credits/promo CRUD |
| Auth policies | `Tamma.Api/Program.cs`: `PlatformOwnerAccess` (L986), `SettingsManage` (L1001), `SettingsView` (L996), `MemberAccess` (L991) | Route gating |
| Tenant-route RBAC precedent | `Tamma.Api/Endpoints/AlertEndpoints.cs` — `RequireTenantAdmin` (L1008), `RequireTenantMembershipFilter.TenantRoleItemKey`, admin/tenant section split, paging 50/500 | Mirror for `/api/pricing/*` + member 403 |
| Mode | `Tamma.Api/Services/PromptStore/TammaMode.cs` (`enum TammaMode` L14, `ITammaModeProvider` L48, SaaS detection L74-95) | Per-mode ownership |
| Tenant ctx | `apps/tamma-elsa/src/Tamma.Data/ITenantContext.cs` (`TenantId`, `SetTenantId`, `ClearTenantId`) | Resolve caller tenant; ignore body tenant id |
| Boundary-task pattern | `PlatformQueuedTask` entity + `PlatformTaskQueueProcessor` (Epic 28); 34-4 mirrors it with `ActivateScheduledPlanTaskPayload` | Mirror with `TrialExpiryTaskPayload` |
| Error type | `apps/tamma-elsa/src/Tamma.Core/TammaError.cs` (`TammaError(code, message, context?, retryable?, severity?)` L44) | Typed rejections (mapped to 422/403) |
| Test layout | `apps/tamma-elsa/tests/Tamma.Api.Tests/` (per-area dirs incl. `Pricing` to be added, `Providers`, `Alerts`); docker suites via `sg docker -c "dotnet test …"` | xUnit tests |

### Key facts that shape the design

- **34-5 is the canonical markup owner** (its boundary note explicitly forbids consumers from
  re-implementing markup) — so this story is a *decorator* on `IUsagePricingEngine`, registered in
  DI so `ICreditAwarePricingEngine` (or the decorated `IUsagePricingEngine`) is what Billing/UI
  resolves.
- **34-4 owns plan assignment** including version pinning and the scheduled-activation queue
  pattern — trial start/end reuse it verbatim (`AssignAsync` with a trial marker; `CancelAsync` /
  assign `plan_free` on downgrade).
- **`ck_*` CHECK constraints + partial unique indexes** are the established invariant-enforcement
  mechanism (34-4 sketch). Use `ck_credit_balance_nonneg`, `ux_trial_one_active_per_tenant`,
  unique `(PromoCodeId, TenantId)`, unique `CodeKey`.
- **Append-only ledger** matches the DCB/event-sourcing spirit already pervasive in the repo;
  `BalanceAfter` makes balance a running sum, no stateful "balance" column to race on.
- **Migration discipline:** all new tables are *additive*; `dotnet ef migrations add` (not a
  baseline CHECK edit), then `dotnet ef migrations has-pending-model-changes` must report none, and
  config goes only in `TammaModelConfiguration.cs`.

---

## Phased task breakdown (TDD — tests first in every task)

### Task 1 — Entities, enums, EF config, additive migration (core)

**Files (create):** `Tamma.Data/Entities/{TenantTrial,CreditLedger,PromoCode,PromoRedemption}.cs`;
`Tamma.Core/Enums/{TrialStatus,CreditReason,DiscountKind}.cs`. **(modify):**
`Tamma.Data/ControlPlaneDbContext.cs` (4 DbSets), `Tamma.Data/TammaModelConfiguration.cs` (config),
new migration under `Tamma.Data/Migrations/ControlPlane/`.

**Approach:**
- Entities per the story sketch. USD columns `numeric(18,6)` (mirror `BudgetConfig` decimal style).
- `TammaModelConfiguration.cs`: `ck_credit_balance_nonneg` (`"BalanceAfter" >= 0`),
  `ck_trial_status`, `ck_promo_kind`; partial unique `ux_trial_one_active_per_tenant`
  (`HasFilter("\"Status\" = 'active'")`); unique `PromoCode.CodeKey`; unique
  `(PromoRedemption.PromoCodeId, TenantId)`; index `CreditLedger (TenantId, CreatedAt)` for the
  running-balance read; FKs to `Tenant`/`Plan` (`OnDelete` Cascade for tenant, Restrict for plan).
- `dotnet ef migrations add AddTrialsCreditsPromos -c ControlPlaneDbContext`; then
  `dotnet ef migrations has-pending-model-changes` → expect none.

**Tests first:** `tests/Tamma.Data.Tests/Migrations/TrialsCreditsMigrationTests.cs` — migration
applies + rolls back cleanly on real Postgres; `ck_credit_balance_nonneg` rejects a negative
`BalanceAfter`; `ux_trial_one_active_per_tenant` rejects a second active trial; `(PromoCodeId,
TenantId)` unique rejects a duplicate redeem; `CodeKey` unique rejects a case-insensitive dup.

### Task 2 — `ICreditService` / `CreditService` (append-only, never-negative ledger)

**Files (create):** `Tamma.Api/Services/Pricing/{ICreditService,CreditService,TrialsCreditsModels,
TrialsCreditsEventTypes}.cs`.

**Approach:**
- `GetBalanceAsync` = latest `BalanceAfter` for tenant (or 0).
- `GrantAsync` — append positive `DeltaUsd`, `BalanceAfter = prev + delta`, emit `CREDIT.GRANTED`
  (or `CREDIT.REFUNDED` for negative/clawback, floor-clamped), write back `RefEventId`.
- `ConsumeAsync` — **serializable transaction** (retry on `40001`): `applied = min(request,
  balance)`; if `applied == 0 && !allowPartial && request > 0` → throw
  `TammaError("CREDIT.CONSUME.OVERDRAFT", …)`; else insert `Reason='consume'`, `DeltaUsd=-applied`,
  `BalanceAfter=balance-applied`; emit `CREDIT.CONSUMED`; return `ConsumeResult(applied, remainder,
  balanceAfter)`.
- Event emission via `IEventRepository.AppendAsync` (tenant scope, JSON `Tags`/`Data` per
  `OrgEndpoints` style) + `IPlatformEventPublisher.AppendAndPublishAsync` (platform mirror).

**Tests first:** `tests/Tamma.Api.Tests/Pricing/CreditServiceTests.cs` — floor-at-zero (consume 30
of 20 → applied 20, remainder 10, balance 0); overdraft throw when `allowPartial=false` on empty;
**concurrency** (two parallel consumes never oversell — real-Postgres serializable + simulated
`DbUpdateException` retry); grant/refund balance math + event emission + `RefEventId` linkage.

### Task 3 — `IPromoCodeService` / `PromoCodeService` (validation + atomic cap)

**Files (create):** `Tamma.Api/Services/Pricing/{IPromoCodeService,PromoCodeService}.cs`.

**Approach:**
- `ValidateAsync(code, planSlug, tenantId)` → `PromoValidationResult(Ok, Reason?, Code?)` checking,
  in order: found (by `CodeKey = lower(code)`), `IsActive`, not past `Expiry`, under
  `MaxRedemptions`, not already redeemed by tenant (`PromoRedemption` lookup), `AppliesTo`
  empty-or-contains slug. Each failure → distinct `Reason`.
- `RedeemAsync` — validate; insert `PromoRedemption`; **atomic** `UPDATE promo_codes SET
  RedemptionCount = RedemptionCount + 1 WHERE Id=@id AND (MaxRedemptions IS NULL OR RedemptionCount
  < MaxRedemptions)` (0 rows ⇒ cap raced ⇒ `cap_reached`); for `fixed` kind, call
  `CreditService.GrantAsync(Reason=promo)`; for `percent`, record only. Emit `PROMO.REDEEMED`.
  Invalid → `TammaError` (endpoint maps to 422 with reason).

**Tests first:** `tests/Tamma.Api.Tests/Pricing/PromoCodeServiceTests.cs` — full validation matrix
(not-found / inactive / expired / cap-reached / already-redeemed / plan-not-applicable / valid);
fixed grants a `promo` ledger row, percent does not; **concurrency** (last redemption: one wins,
one `cap_reached`; same tenant twice → `already_redeemed`); `PROMO.REDEEMED` emitted.

### Task 4 — Trials: start + boundary expiry (delegates to 34-4)

**Files (create):** `Tamma.Api/Services/Provisioning/TrialExpiryTaskPayload.cs`; extend
`CreditService` (or a `TrialService` partial) for `StartTrialAsync` / `EndTrialAsync`.

**Approach:**
- `StartTrialAsync(tenantId, planId, opts)` — guard plan trial-eligibility (34-1
  `IPlanCatalogService`); insert `TenantTrial` (`active`, pinned `PlanVersion`, `EndsAt = now +
  trialDays`); delegate `IPlanAssignmentService.AssignAsync` with a trial marker in
  `AssignPlanOptions.Reason`; emit `TENANT.TRIAL.STARTED`; second active trial rejected (pre-check +
  partial unique index).
- Enqueue a `TrialExpiryTaskPayload(trialId)` on the platform queue at `EndsAt` (mirror 34-4's
  `ActivateScheduledPlanTaskPayload` enqueue).
- `EndTrialAsync(tenantId, trialId)` — idempotent by `trialId`: convert (`Status→converted`, keep
  assignment) or downgrade (`Status→expired`, `IPlanAssignmentService.CancelAsync` / assign
  `plan_free`); emit `TENANT.TRIAL.ENDED` with `outcome`.
- Wire the processor branch for `TrialExpiryTaskPayload` (mirror the `MoveTenant`/activate-scheduled
  handler).

**Tests first:** extend `CreditServiceTests` (or `TrialServiceTests`) — start creates trial +
delegates `AssignAsync` (mock); second active trial rejected; convert keeps assignment, downgrade
calls cancel/assign-free; idempotent `EndTrialAsync`; `TENANT.TRIAL.STARTED/ENDED` shapes.
Integration: enqueue payload → run processor → assert outcome.

### Task 5 — `ICreditAwarePricingEngine` decorator (net price)

**Files (create):** `Tamma.Api/Services/Pricing/{ICreditAwarePricingEngine,
CreditAwareUsagePricingEngine}.cs`. **(modify):**
`Tamma.Api/Extensions/PricingServiceCollectionExtensions.cs` (DI + decorate).

**Approach:**
- `PriceNetAsync(usageLine, tenantId)` — call 34-5 `IUsagePricingEngine.PriceUsage` for
  `sellPriceUsd`; resolve any applicable percent-promo discount + available credit balance; compute
  `net = max(0, sell − promoDiscount − creditsApplied)` (credits capped at the pre-floor remainder);
  if an `active` trial covers the recurring component, zero it and set `trialWaived = true`;
  optionally call `CreditService.ConsumeAsync` on the billing path (or return the figure for Epic 35
  to consume) — keep "compute" vs "commit consume" cleanly separated. Round 6dp internal / 2dp
  invoice (34-5 convention).
- Register so DI hands consumers the credit-aware result; do not shadow 34-5's engine for callers
  that need raw sell.

**Tests first:** `tests/Tamma.Api.Tests/Pricing/CreditAwarePricingEngineTests.cs` — `net = max(0,
sell − promo − credits)` (mock `IUsagePricingEngine`); trial zeroes recurring (`trialWaived`); net
floored at 0 when discounts exceed sell, credit not over-applied; **golden-file** byte-stable
determinism (fixed usage line + balance + promo, 6dp/2dp).

### Task 6 — Endpoints (admin + tenant) with per-mode RBAC

**Files (modify):** `Tamma.Api/Endpoints/Admin/AdminPricingEndpoints.cs` (credits grant/read, promo
CRUD), `Tamma.Api/Endpoints/PricingEndpoints.cs` (redeem, credits read, estimate extension),
`Tamma.Api/Program.cs` (map routes + DI), `PricingServiceCollectionExtensions.cs`.

**Approach:**
- Admin (`PlatformOwnerAccess`): `GET/POST /api/admin/tenants/{id}/credits`,
  `GET/POST/PATCH /api/admin/pricing/promo-codes`. Promo create validates `DiscountKind`/`Value`
  (percent ∈ (0,100], fixed > 0), normalizes `CodeKey`; emit `PROMO.CODE.CREATED/UPDATED`.
- Tenant (`SettingsManage`): `POST /api/pricing/promo/redeem` (member → 403 via `RequireTenantAdmin`
  membership pattern; tenant from `ITenantContext`, body tenant id ignored; invalid → 422 reason);
  `GET /api/pricing/credits` (`SettingsView`); extend 34-5 `GET /api/pricing/estimate` response with
  `creditsApplied`/`netPrice`/`trialWaived`.
- Map `TammaError` codes → 422/403 in the endpoint catch (mirror existing prompt/convention 404
  translation style).

**Tests first:** `tests/Tamma.Api.Tests/Pricing/TrialsCreditsEndpointsTests.cs` — admin grant +
read (PlatformOwnerAccess; non-platform JWT 403; unknown tenant 404); redeem RBAC (tenant_owner
200, member 403); invalid-code 422 matrix; **tenant isolation** (caller A cannot redeem/read tenant
B's ledger); single-user mode (sole user sees own credits/trial).

### Task 7 — Wire-up, full-suite green, migration verification

**Approach:** register all services + the decorator in `PricingServiceCollectionExtensions`; map
all routes in `Program.cs`; run the full `Tamma.Api.Tests` + `Tamma.Data.Tests` suites via
`sg docker -c "dotnet test …"`; confirm `has-pending-model-changes` reports none; lint/build clean.

**Tests first:** N/A (integration gate) — the gate is the green full suite + clean migration check.

---

## Sequencing & dependencies

```
Task 1 (entities/migration)
   ├─> Task 2 (CreditService)
   │      └─> Task 4 (Trials)         ──┐
   ├─> Task 3 (PromoCodeService) ───────┤
   │                                    ├─> Task 6 (Endpoints) ─> Task 7 (wire-up + suite gate)
   └─> Task 2 + 3 ─> Task 5 (decorator)─┘
```

- **Task 1 is the only hard prerequisite for everything.** Tasks 2 and 3 are parallel-safe after 1.
- Task 4 needs Task 2 (grant on fixed conversion is reused) + 34-4's `IPlanAssignmentService`.
- Task 5 needs Tasks 2 + 3 (balance + promo) and 34-5's `IUsagePricingEngine`.
- Task 6 needs 2-5; Task 7 closes the wave.
- **External story prerequisites:** 34-1 (`IPlanCatalogService`, trial-eligibility flag), 34-4
  (`IPlanAssignmentService`, boundary-task pattern), 34-5 (`IUsagePricingEngine`,
  `PricingEndpoints.cs`/`AdminPricingEndpoints.cs` files, rounding convention). If those are not yet
  merged, stub their interfaces behind the seams already named in the story and swap to the real
  impls when they land — do **not** re-implement them here (epic boundary).

## Risks & mitigations

- **Double-spend on concurrent consume** (the load-bearing invariant). *Mitigation:* serializable
  transaction with `40001` retry (or per-tenant row lock) **and** the `ck_credit_balance_nonneg`
  CHECK as a DB backstop; pin both in a real-Postgres concurrency test. A logic bug must still be
  unable to persist a negative balance.
- **Re-implementing 34-5 markup or 34-4 assignment** (boundary violation). *Mitigation:* the
  decorator only subtracts; trials only delegate. Code review check: no `MarginPolicy` /
  `ProviderPricingService` math and no `TenantPlanAssignment` transition logic in this story's files.
- **Promo cap race / over-redemption.** *Mitigation:* conditional atomic `UPDATE … WHERE
  RedemptionCount < MaxRedemptions` (0 rows ⇒ cap raced) + unique `(PromoCodeId, TenantId)`; never a
  read-then-write.
- **Trial boundary missed while host down.** *Mitigation:* reuse 34-4's idempotent-by-id platform
  queue task; `EndTrialAsync` is idempotent so a late catch-up tick is safe.
- **Net price not reproducible** (Epic 35 + cost dashboard must agree). *Mitigation:* pure
  decorator + 34-5's fixed 6dp/2dp rounding + a golden-file determinism test.
- **Cross-tenant leakage.** *Mitigation:* tenant routes resolve from `ITenantContext` and ignore
  any body tenant id; reuse `AlertEndpoints` membership/`RequireTenantAdmin` gate; tenant-isolation
  test for read + redeem.
- **Migration discipline.** *Mitigation:* additive tables only; `has-pending-model-changes` →
  none; entity config exclusively in `TammaModelConfiguration.cs`; up/down round-trip test.
- **Event-topology shift (Story 28-1 / Epic 30).** *Mitigation:* tenant-scope `CREDIT.*`/`PROMO.*`/
  `TENANT.TRIAL.*` events append via `IEventRepository`; the platform-audit mirror via
  `IPlatformEventPublisher` keeps the platform timeline CP-resident — consistent with the existing
  CP/per-tenant split, no special-casing needed now.

## Acceptance criteria (mirror the story)

- [ ] `TenantTrial`, `CreditLedger`, `PromoCode`, `PromoRedemption` entities + DbSets + EF config +
      additive migration; `has-pending-model-changes` reports none.
- [ ] Ledger is append-only and `BalanceAfter` never goes below zero (CHECK + serialized consume);
      over-consume is partially applied with remainder returned; `allowPartial=false` overdraft →
      `CREDIT.CONSUME.OVERDRAFT`.
- [ ] `POST /api/pricing/promo/redeem` (`SettingsManage`) validates code (active/expiry/cap/
      applicability/already-redeemed); invalid → 422 reason; member → 403; valid records redemption +
      (fixed) grants credit.
- [ ] Trial start creates `TenantTrial` + delegates `IPlanAssignmentService.AssignAsync` (trial
      flag); one active trial per tenant; expiry emits `TENANT.TRIAL.ENDED` and converts or
      downgrades to free via the boundary task.
- [ ] `ICreditAwarePricingEngine` returns `net = max(0, sell − promo − credits)` with
      `creditsApplied`; trial zeroes the recurring charge (`trialWaived`); deterministic (golden file).
- [ ] Admin grants credits `POST /api/admin/tenants/{id}/credits` (`PlatformOwnerAccess`); all
      mutations emit `CREDIT.GRANTED` / `CREDIT.CONSUMED` / `CREDIT.REFUNDED` / `PROMO.REDEEMED` /
      `TENANT.TRIAL.STARTED` / `TENANT.TRIAL.ENDED` DCB events + platform mirror.
- [ ] Per-mode + per-tenant ownership honored (single-user sole user; SaaS owner/admin/member with
      member read-only; cross-tenant 404/403; tenant isolation).
- [ ] Unit + integration tests: ledger never negative (incl. concurrent), promo validation matrix +
      cap under concurrency, trial convert vs expire, credit-aware net price, RBAC + tenant
      isolation, DCB emission — all green via `sg docker -c "dotnet test …"`.
