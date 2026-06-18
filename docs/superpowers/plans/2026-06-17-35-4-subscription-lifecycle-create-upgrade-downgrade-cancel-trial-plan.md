# Story 35-4 — Subscription Lifecycle (Create, Upgrade/Downgrade, Cancel, Trial & Proration)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation. Story file:
> `docs/stories/epic-35/story-35-4/35-4-subscription-lifecycle-create-upgrade-downgrade-cancel-trial.md`.

**Goal:** Implement the full Stripe subscription lifecycle for SaaS tenants on the C# control
plane — Checkout-based subscribe, upgrade (immediate proration) / downgrade (scheduled at period
end), cancel (immediate or at-period-end), trial start/convert/expire, and seat changes — with a
local `BillingSubscription` mirror that keeps `Tenant.Plan`/`Tenant.PlanId` and the quota state in
lockstep so Story 35-6 enforcement reads a single source of truth. Every transition is audited via
`BILLING.SUBSCRIPTION.*` DCB events. SaaS-only; single-user mode is a hard no-op.

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (`Tamma.Api` minimal-API +
`Tamma.Data` EF Core control plane). `Stripe.net` (added by Story 35-1). Tests: xUnit in
`apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/`; docker-bound suites run via
`sg docker -c "dotnet test ..."` (session docker group is stale per project memory).

---

## Non-goals (YAGNI guard)

- **NO Stripe webhook endpoint / signature verification / `billing_webhook_events` dedup** — that
  is Story 35-5. This story *provides* the `SubscriptionMirrorUpdater` the 35-5 webhook processor
  calls; it does not own ingestion.
- **NO quota computation, enforcement gates, or over-quota API responses** — Story 35-6. This story
  only keeps `Tenant.Plan`/`Seats` correct so 35-6 reads one truth.
- **NO customer mapping, plan catalog, meters, or Stripe-key wiring** — Story 35-1 (consumed here).
- **NO invoicing, dunning, tax, billing portal, or credits wallet** — later Epic 35 stories.
- **NO tenant-facing subscription UI** — API + control-plane only; the GET projection is what the
  dashboard stories render.
- **NO single-user subscription support** — billing is SaaS-only; in single-user mode the endpoints
  are unmapped and `NullBillingProvider` makes zero Stripe calls.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### What exists

| Asset | Path | Note |
|---|---|---|
| `Tenant` entity | `apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs:11` | `Plan` string column (default `"free"`); a **shadow** `PlanId` (Guid?) FK exists in EF config (set via `db.Entry(tenant).Property("PlanId")`, see below) — both kept in lockstep. |
| `Plan` entity | `apps/tamma-elsa/src/Tamma.Data/Entities/Plan.cs:14` | Keyed by `Slug` (`free`/`team`/`enterprise`), `MonthlyPriceUsd` (decimal — used to decide upgrade vs downgrade), `Quotas` JSON, `IsActive`, `PlacementPolicy`. Seeded by `PlansSeeder`. |
| `ControlPlaneDbContext` | `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Control-plane DbSets; entity config centralized in `TammaModelConfiguration.cs`. |
| Plan-change lockstep precedent | `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminTenantsEndpoints.cs:619-624` | `db.Entry(tenant).Property("PlanId").CurrentValue = plan.Id; tenant.Plan = plan.Slug;` then `SaveChangesAsync` — **copy this lockstep exactly** for the effective-plan update. |
| `DomainEvent` entity | `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs:3` | `Type`, `TenantId?`, `Tags`/`Metadata`/`Data` JSON, server `SequenceNumber`. |
| Tenant-scoped DCB append | `apps/tamma-elsa/src/Tamma.Data/Repositories/EventRepository.cs:49-87` | `AppendAsync`: `TenantId` set → tenant `DomainEvents` store; `TenantId` null → `IPlatformEventRepository` (platform). Subscription events are tenant-scoped → set `TenantId`. |
| Tenant event emit shape | `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:1036-1060` (`EmitTenantEvent`) | `Metadata = {"workflowVersion":"1.0.0","eventSource":"system"}`; mirror this row shape in `BillingEvents`. |
| Tenant-route group + membership gate | `apps/tamma-elsa/src/Tamma.Api/Program.cs:1505-1533` (`app.MapGroup("/api/v1/orgs").RequireAuthorization("MemberAccess")` + `.AddEndpointFilter<RequireTenantMembershipFilter>()`) | Role read from `httpContext.Items[RequireTenantMembershipFilter.TenantRoleItemKey]`; member-403 via `TenantRoleHierarchy.IsAtLeast(role, Admin)` (see `OrgEndpoints.cs:254`, `AlertEndpoints.cs:1019`). |
| `IPlatformTaskHandler` | `apps/tamma-elsa/src/Tamma.Api/Services/PlatformTasks/IPlatformTaskHandler.cs:25` | `TaskType` + `HandleAsync`; `PlatformTaskTerminalException` for non-retryable. (Not strictly needed here; checkout is synchronous — kept as a reference for any async retry.) |
| Mode seam | `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs:14,48` (`enum TammaMode`, `ITammaModeProvider`) | `Mode == SingleUser` short-circuits endpoint mapping + provider selection. |
| Auth policies | `apps/tamma-elsa/src/Tamma.Api/Program.cs:959-1095` | `MemberAccess` (any authenticated) is the tenant-route group policy; per-route role gating is done in the handler via the membership-filter item key (not a policy). `OwnerAccess`/`PlatformOwnerAccess` are platform/admin gates — NOT used for tenant-scoped subscription mutations. |

### What Story 35-1 provides (consumed here; do NOT re-create)

`BillingCustomer` (`Tamma.Data/Entities/BillingCustomer.cs`, TenantId-unique), `BillingPlanPrice`
(`Tamma.Data/Entities/BillingPlanPrice.cs`, slug → Stripe Product/Price/Meter ids incl. `SeatsPriceId`/
`SeatsMeterId`), `IBillingProvider`/`StripeBillingProvider`/`NullBillingProvider`
(`Tamma.Api/Services/Billing/`), `IBillingCatalog`/`BillingCatalog`, `BillingMode` enum
(`Tamma.Core/Billing/BillingMode.cs`), `BillingEvents` static helper, `BillingServiceCollectionExtensions`
(`AddTammaBilling`), and the three seeded meters (`tamma.platform_tokens_input/output`, `tamma.seats`).
Story 35-1 also resolves the Stripe key from the Epic 29 cabinet and registers `NullBillingProvider`
in single-user mode.

### What does NOT exist yet (all NEW in this story or sibling 35-x)

- No `BillingSubscription` entity / table / repository.
- No `ISubscriptionService` / `SubscriptionService` / `SubscriptionMirrorUpdater` / `SubscriptionProjection`.
- No `Endpoints/Billing/` directory or `SubscriptionEndpoints.cs`.
- No Stripe code in the repo at all yet (`Stripe` only appears in a Studio Razor page string). `Stripe.net`
  is added by 35-1; this story assumes the package is present.

### Verified pitfalls

- `EventRepository.AppendAsync` **throws** for a null-`TenantId` event when no `IPlatformEventRepository`
  is wired (`EventRepository.cs:61-68`). Subscription events MUST set `TenantId` (they are tenant-scoped),
  so they route to the tenant `DomainEvents` store — correct and unaffected.
- `Tenant.PlanId` is an EF **shadow** property (not a CLR property on `Tenant.cs`); update it via
  `db.Entry(tenant).Property("PlanId").CurrentValue`, exactly as `AdminTenantsEndpoints.cs:620`.
- Per-route role gating for tenant routes is done **in the handler** via the membership-filter item key,
  not by an authorization policy (the group policy `MemberAccess` only requires authentication).

---

## Architecture

**Checkout → webhook-materialize → API transitions (proration/schedule/cancel/seats) → mirror +
`Tenant.Plan` lockstep → DCB event → 35-6 reads quota.** One service (`SubscriptionService`) owns the
API verbs; one updater (`SubscriptionMirrorUpdater`, shared with Story 35-5) owns turning a Stripe
`Subscription` object into the local mirror + lockstep + event.

```
HTTP (tenant owner/admin)                 Stripe webhook (Story 35-5)
   │ checkout/change/cancel/seats               │ subscription.created/updated/deleted
   ▼                                            ▼
SubscriptionService ──Stripe.net──►  (returns Stripe.Subscription)
   │                                            │
   └──────────────► SubscriptionMirrorUpdater.ApplyAsync(tenantId, stripeSub, transition) ◄──┘
                          │ one CP transaction:
                          ├─ upsert BillingSubscription (status/period/trialEnd FROM stripeSub)
                          ├─ on EFFECTIVE plan change: Tenant.Plan + Tenant.PlanId lockstep
                          └─ IEventRepository.AppendAsync(BILLING.SUBSCRIPTION.<transition>)
```

### Per-mode ownership (mandatory two-scoping-model answer)

| Question | single-user | SaaS |
|---|---|---|
| Are subscription endpoints mapped? | No (`ITammaModeProvider.Mode == SingleUser` → not mapped / 404). | Yes, tenant-scoped + membership-gated. |
| Provider | `NullBillingProvider` (`IsEnabled=false`, zero Stripe). | `StripeBillingProvider`. |
| Who owns the subscription? | n/a (none). | The tenant: `tenant_owner`/`tenant_admin` mutate; `member` is read-only (403 on mutate, 200 on GET). |
| Mirror rows | none | one non-terminal `BillingSubscription` per tenant (partial-unique). |
| Quota source for 35-6 | n/a | active `BillingSubscription.PlanSlug` + `Seats`. |

---

## Task breakdown (test-first / TDD)

### Task 1 — `BillingSubscription` entity, EF config, migration, repository (story AC1, AC12)

**Files to touch**
- New: `src/Tamma.Data/Entities/BillingSubscription.cs`.
- Modify: `src/Tamma.Data/ControlPlaneDbContext.cs` (add `DbSet<BillingSubscription> BillingSubscriptions`).
- Modify: `src/Tamma.Data/TammaModelConfiguration.cs` (`ConfigureControlPlaneEntities`: table
  `billing_subscriptions`, status CHECK, FK→`tenants` cascade, **partial unique** on `TenantId`
  filtered `Status NOT IN ('canceled','incomplete_expired')`, partial unique on `StripeSubscriptionId`).
- New: `src/Tamma.Data/Migrations/ControlPlane/<ts>_AddBillingSubscription.cs` (+ Designer + snapshot)
  via `dotnet ef migrations add AddBillingSubscription --context ControlPlaneDbContext --output-dir Migrations/ControlPlane`.
- New: `src/Tamma.Data/Repositories/IBillingSubscriptionRepository.cs`,
  `BillingSubscriptionRepository.cs` (`GetActiveByTenantAsync`, `UpsertAsync`, all `TenantId`-scoped).

**Approach:** entity per the story signature. CHECK + partial-unique mirror the 35-1 `BillingCustomer`
config style. Update the snapshot; run `dotnet ef migrations has-pending-model-changes` → expect none.

**Tests first** (`tests/Tamma.Api.Tests/Billing/BillingSubscriptionRepositoryTests.cs`,
`BillingSubscriptionMigrationTests.cs` — docker-bound): repository CRUD is `TenantId`-scoped;
`GetActiveByTenantAsync` ignores terminal rows; the partial-unique index rejects a second non-terminal
row for one tenant but allows a canceled + a new active row; migration applies + rolls back; pending
model changes = none.

### Task 2 — `SubscriptionProjection` + `SubscriptionMirrorUpdater` (story AC5, AC7, AC8, AC13)

**Files to touch**
- New: `src/Tamma.Api/Services/Billing/SubscriptionProjection.cs` (read DTO + `FreeDefault(tenantId)`).
- New: `src/Tamma.Api/Services/Billing/SubscriptionMirrorUpdater.cs` —
  `ApplyAsync(Guid tenantId, Stripe.Subscription stripeSub, string transition, CancellationToken ct)`:
  one CP transaction that (a) upserts the mirror with status/period/trialEnd/seats **from `stripeSub`**,
  (b) on an effective-plan change applies the `Tenant.Plan` + shadow `PlanId` lockstep (copy
  `AdminTenantsEndpoints.cs:619-624`), (c) appends the right `BILLING.SUBSCRIPTION.*` event via
  `IEventRepository.AppendAsync` (TenantId set).
- Modify: `src/Tamma.Api/Services/Billing/BillingEvents.cs` (35-1's helper) — add
  `SubscriptionCreated/Updated/Canceled/TrialEnded` builders, tags `{ tenantId, planSlug, status }`,
  metadata `{"workflowVersion":"1.0.0","eventSource":"system"}`.

**Approach:** the updater is the *single* place mirror logic lives, shared with Story 35-5's webhook
processor. It never reads status/period from a request — always from the Stripe object (AC13). It maps
the Stripe `status` string to the entity's CHECK-constrained domain.

**Tests first** (`SubscriptionMirrorUpdaterTests.cs`): applying a Stripe object upserts the mirror and
the `Tenant.Plan`/`PlanId` lockstep (no-drift invariant); status/period come from the Stripe object not
the caller; a `transition="trial_ended"` apply emits `BILLING.SUBSCRIPTION.TRIAL_ENDED`; each transition
emits exactly one event with the right tags; a scheduled-downgrade apply does NOT change `Tenant.Plan`.

### Task 3 — `ISubscriptionService` / `SubscriptionService`: checkout (story AC2, AC10, AC11)

**Files to touch**
- New: `src/Tamma.Api/Services/Billing/ISubscriptionService.cs`, `SubscriptionService.cs`.
- Modify: `src/Tamma.Api/Extensions/BillingServiceCollectionExtensions.cs` — register
  `ISubscriptionService`, `SubscriptionMirrorUpdater`, `IBillingSubscriptionRepository` (SaaS only;
  in single-user the 35-1 `NullBillingProvider` is already wired so the service is a no-op seam).

**Approach:** `CreateCheckoutSessionAsync` resolves `BillingCustomer` (35-1) + `BillingPlanPrice` (35-1)
for the slug, builds a Stripe `Checkout.Session` (`mode=subscription`, line items = base price +
optional `tamma.seats` quantity, `subscription_data.trial_period_days = trialDays`), with a deterministic
`RequestOptions.IdempotencyKey`. Returns `CheckoutResult(url, sessionId)`. **No** local row created here
(materialized by the 35-5 `customer.subscription.created` webhook via the Task-2 updater). Guard on
`IBillingProvider.IsEnabled` — single-user returns a SaaS-only result.

**Tests first** (`SubscriptionServiceCheckoutTests.cs`): builds the session with the right price id,
seat quantity, trial days, and idempotency key (Stripe `Checkout.SessionService` mocked); no local row;
single-user/`NullBillingProvider` → zero Stripe calls.

### Task 4 — change plan: upgrade proration vs scheduled downgrade (story AC3, AC7, AC8, AC10)

**Files to touch**
- Modify: `src/Tamma.Api/Services/Billing/SubscriptionService.cs` (add `ChangePlanAsync`).

**Approach:** load the active mirror + the current and target `Plan` rows; compare `MonthlyPriceUsd`.
- **Upgrade (target ≥ current):** `SubscriptionService.UpdateAsync(stripeSubId, { Items=newPrice,
  ProrationBehavior="create_prorations" }, idempotencyKey)`; pass the returned `Stripe.Subscription` to
  `SubscriptionMirrorUpdater.ApplyAsync(..., "upgraded")` → applies new slug + lockstep + `UPDATED` event.
- **Downgrade (target < current):** create a `SubscriptionSchedule` (`SubscriptionScheduleService`)
  with a phase change at `CurrentPeriodEnd`; record `ScheduledPlanSlug`/`ScheduledEffectiveAt`/
  `StripeScheduleId` on the mirror, emit `UPDATED` (tag `scheduledPlanSlug`), and **leave `PlanSlug`/
  `Tenant.Plan` unchanged**. The actual rollover happens via the 35-5 webhook → updater.

**Tests first** (`SubscriptionServiceChangeTests.cs`): upgrade calls `UpdateAsync` with
`create_prorations` and applies the slug now; downgrade creates a schedule, records the scheduled
fields, leaves `PlanSlug`/`Tenant.Plan` at the current plan; idempotency key deterministic; unknown slug
rejected.

### Task 5 — cancel (immediate vs at-period-end) + trial transitions (story AC4, AC5, AC8)

**Files to touch**
- Modify: `src/Tamma.Api/Services/Billing/SubscriptionService.cs` (add `CancelAsync`).

**Approach:**
- `atPeriodEnd=true`: `UpdateAsync(CancelAtPeriodEnd=true)` → updater applies (`Status` stays `active`,
  `CancelAtPeriodEnd=true`), emit `CANCELED` (recorded-pending).
- `atPeriodEnd=false`: `CancelAsync(stripeSubId)` → updater applies (`Status=canceled`), recompute
  `Tenant.Plan` to `free` now, emit `CANCELED`.
- Trial conversion/expiry transitions are *driven by webhooks* (35-5) but flow through the same Task-2
  updater with `transition="trial_ended"` emitting `TRIAL_ENDED`; ensure the updater handles a Stripe
  object whose `status` moves `trialing → active` or `trialing → canceled/unpaid`.

**Tests first** (`SubscriptionServiceCancelTests.cs`, `SubscriptionServiceTrialTests.cs`): at-period-end
keeps `active`+`CancelAtPeriodEnd`; immediate flips to `canceled` + `Tenant.Plan=free`; trial
conversion/expiry through the updater emits `TRIAL_ENDED` and lands the right status.

### Task 6 — seat changes with active-member floor (story AC6, AC7, AC8)

**Files to touch**
- Modify: `src/Tamma.Api/Services/Billing/SubscriptionService.cs` (add `ChangeSeatsAsync`).

**Approach:** count active members via `ITenantMembershipRepository`; if `seats < activeMembers` →
throw a typed conflict mapped to **409 `seats_below_active_members`**, no Stripe call. Otherwise update
the Stripe subscription item quantity on the `tamma.seats` price (35-1 `SeatsPriceId`) with a
deterministic idempotency key; updater applies new `Seats`, emit `UPDATED`. (Recompute hook for 35-6 is
just the persisted `Seats` — 35-6 reads it.)

**Tests first** (`SubscriptionServiceSeatsTests.cs`): increase updates Stripe quantity + `Seats`;
decrease below active members → 409 `seats_below_active_members`, zero Stripe calls; equal-to floor
allowed.

### Task 7 — `SubscriptionEndpoints` + Program.cs mapping + RBAC (story AC2, AC9, AC11, AC12)

**Files to touch**
- New: `src/Tamma.Api/Endpoints/Billing/SubscriptionEndpoints.cs` —
  `MapSubscriptionEndpoints(IEndpointRouteBuilder)`: `POST checkout/change/cancel/seats`, `GET ` (current).
  Mount under the tenant-scoped membership-gated group (`/api/v1/orgs/{tenantId}/billing/subscription`
  + `RequireTenantMembershipFilter`), matching `OrgEndpoints`/`AlertEndpoints` tenant sections. Each
  mutation reads the role from `httpContext.Items[RequireTenantMembershipFilter.TenantRoleItemKey]` and
  returns 403 unless `TenantRoleHierarchy.IsAtLeast(role, TenantRoleHierarchy.Admin)`. GET allowed for
  any member; free-tier default when no row.
- Modify: `src/Tamma.Api/Program.cs` — call `MapSubscriptionEndpoints` only when
  `ITammaModeProvider.Mode == TammaMode.SaaS` (mirror how other SaaS-only wiring guards on mode).

**Approach:** thin endpoints → `ISubscriptionService`. Map the typed conflict to 409 with the stable
code; map Stripe failures to 502 (with a WARN log + the webhook reconcile note).

**Tests first** (`SubscriptionEndpointsRbacTests.cs`): member → 403 on every mutation, owner/admin pass;
GET 200 for member; cross-tenant access (not a member of the route tenant) → 404/403 via the membership
filter; single-user mode → endpoints unmapped (404).

### Task 8 — event-emission + lifecycle integration + tenant-isolation tests (story AC8, AC12, AC14)

**Files to touch**
- New: `tests/Tamma.Api.Tests/Billing/SubscriptionEventEmissionTests.cs`,
  `NullBillingSubscriptionTests.cs`, `SubscriptionLifecycleIntegrationTests.cs` (docker-bound).

**Approach:** assert each transition appends exactly one `BILLING.SUBSCRIPTION.*` event with
`{tenantId,planSlug,status}` tags in the tenant `DomainEvents` store; run the full lifecycle through the
HTTP endpoints on a real CP+tenant DB (Stripe mocked) and assert one mirror row, the matching events,
and the `Tenant.Plan == BillingSubscription.PlanSlug` no-drift invariant after each step; tenant
isolation (A cannot read/mutate B); single-user no-op (zero Stripe, no rows, endpoints unmapped).

---

## Sequencing & dependencies

```
Task 1 (entity/migration/repo)
   └─► Task 2 (projection + mirror updater + events)
          ├─► Task 3 (checkout)
          ├─► Task 4 (change: upgrade/downgrade)   ── parallel-safe with 3,5,6 once Task 2 lands
          ├─► Task 5 (cancel + trial)
          └─► Task 6 (seats)
                 └─► Task 7 (endpoints + RBAC + Program mapping)
                        └─► Task 8 (event/integration/isolation tests)
```
- **Hard prerequisite:** Story 35-1 merged (provides `BillingCustomer`, `BillingPlanPrice`,
  `IBillingProvider`/`Null...`, `IBillingCatalog`, `BillingEvents`, `BillingServiceCollectionExtensions`,
  `Stripe.net`, seeded meters, mode-aware billing DI).
- **Co-dependent:** Story 35-5 (webhook ingestion) — this story exposes `SubscriptionMirrorUpdater` for
  it. If 35-5 is not yet merged, the trial/rollover/cancel-confirmation transitions are tested by calling
  `SubscriptionMirrorUpdater.ApplyAsync` directly with a fabricated `Stripe.Subscription` (the same entry
  point the webhook will use).
- **Blocks:** Story 35-6 (quota enforcement reads the active `BillingSubscription`).
- Task 2 is the linchpin (the shared updater); Tasks 3–6 only need Task 2.

---

## Risks + mitigations

- **Mirror drift from Stripe.** Apply Stripe's returned object inside one CP transaction; status/period
  taken from the object, not the request (AC13); the 35-5 webhook reconciles to confirmed state; a
  no-drift invariant test (`Tenant.Plan == BillingSubscription.PlanSlug`) runs after each transition.
- **Double-applied proration / duplicate schedule on retry.** Deterministic
  `RequestOptions.IdempotencyKey` on every mutating Stripe call.
- **API ↔ webhook race.** Single shared `SubscriptionMirrorUpdater`; last write that reflects Stripe's
  confirmed state wins; never overwrite a newer Stripe state with a stale one.
- **Downgrade lowering quota mid-period.** Scheduled downgrade leaves `PlanSlug`/`Tenant.Plan` at the
  higher plan until the rollover webhook fires.
- **Seat decrease orphaning members.** Reject below the active-member count with 409 before any Stripe
  call.
- **`Tenant.PlanId` is a shadow property.** Update via `db.Entry(tenant).Property("PlanId")`, exactly as
  `AdminTenantsEndpoints.cs:620` — not a CLR setter.
- **Stripe.net API drift.** Research current docs (`SubscriptionService.UpdateAsync`+`ProrationBehavior`,
  `SubscriptionScheduleService`, `Checkout.SessionService`, `RequestOptions.IdempotencyKey`) before
  writing SDK calls; mock at the service-interface boundary; live-Stripe test opt-in behind
  `STRIPE_SECRET_KEY_TEST`.
- **Single-user accidental coupling.** Endpoints unmapped by mode + `NullBillingProvider`; tests assert
  zero SDK calls and no rows.
- **Migration discipline.** `billing_subscriptions` is additive; still run
  `dotnet ef migrations has-pending-model-changes` (expect none) and put entity config only in
  `TammaModelConfiguration.cs` (the established single source); docker-bound migration test asserts
  apply + rollback.

---

## Acceptance criteria (mirror of the story)

- [ ] `BillingSubscription` entity + table + partial-unique (one non-terminal per tenant) + status CHECK
      + FK; migration applies/rolls back, `has-pending-model-changes` = none.
- [ ] `POST .../checkout` returns a Stripe Checkout Session for the slug; owner/admin only (member 403);
      no local row created (materialized by the 35-5 webhook).
- [ ] `POST .../change`: upgrade applies immediate proration; downgrade schedules at period end and is
      reflected as `scheduledPlanSlug`/`scheduledEffectiveAt` without changing the live plan.
- [ ] `POST .../cancel`: immediate (`canceled` + `Tenant.Plan=free` now) and at-period-end
      (`CancelAtPeriodEnd`, stays `active`) both supported; trial convert/expire emits
      `BILLING.SUBSCRIPTION.TRIAL_ENDED`.
- [ ] `POST .../seats`: updates the `tamma.seats` quantity + `Seats`; decrease below active members →
      409 `seats_below_active_members` (no Stripe call); quota reads the new seat count.
- [ ] `Tenant.Plan` + `Tenant.PlanId` updated atomically with the mirror on every effective-plan change;
      no-drift invariant holds.
- [ ] `BILLING.SUBSCRIPTION.CREATED/UPDATED/CANCELED/TRIAL_ENDED` emitted with `{tenantId,planSlug,status}`.
- [ ] `GET .../subscription` returns the projection (free default when none); never cross-tenant.
- [ ] Single-user mode: endpoints unmapped, `NullBillingProvider`, zero Stripe calls, no rows.
- [ ] Unit + integration tests green (checkout, upgrade proration, scheduled downgrade, immediate vs
      end-of-period cancel, trial conversion, seat change incl. floor 409, RBAC, no-drift, event
      emission, tenant isolation); docker-bound suites run via `sg docker -c "dotnet test ..."`.
