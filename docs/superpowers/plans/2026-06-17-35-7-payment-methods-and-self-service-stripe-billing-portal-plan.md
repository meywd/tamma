# Story 35-7 — Payment Methods & Self-Service Stripe Billing Portal — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan phase-by-phase. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every phase writes tests
> before implementation.

**Story:** `docs/stories/epic-35/story-35-7/35-7-payment-methods-and-self-service-stripe-billing-portal.md` · **Epic 35** (Billing & Payments, C#) · **Priority P1** · **Est. 3-4 days** · **Today: 2026-06-17**

**Goal:** Let tenant admins manage payment methods and billing details without leaving Tamma —
integrate the Stripe Customer Portal (session creation + allowlisted return URL), a SetupIntent flow
for adding/replacing the default card, and a local masked `BillingPaymentMethod` mirror (brand/last4/
exp/default/status) kept in sync by Story 35-5 webhooks. Everything is SaaS-RBAC-gated, per-tenant
isolated, and audited via DCB events; a paid tenant left without a usable card emits a signal for the
Story 35-8 dunning path.

---

## Non-goals (YAGNI guard)

- **NO webhook ingestion.** Story 35-5 owns the verified Stripe webhook endpoint and the
  `IBillingEventHandler` dispatch registry. 35-7 only registers `PaymentMethodBillingEventHandler`
  into it — it does not parse signatures, dedupe deliveries, or resolve tenancy from customer ids.
- **NO dunning / notification delivery.** 35-7 emits `BILLING.PAYMENT_METHOD.MISSING`/`REMOVED` DCB
  events and surfaces `hasUsableDefault`. Story 35-8 builds the escalation, retries, emails, and
  alert-rule wiring. Boundary is strict — do not implement another story's responsibility.
- **NO subscription / invoice mirrors.** `BillingSubscription` (35-4) and `BillingInvoice` (35-8)
  are not touched. Paid-plan detection reads existing subscription/plan state **read-only**.
- **NO raw card storage.** PAN/CVC never reach any Tamma column. Cards are collected client-side via
  Stripe Elements + SetupIntent; only brand/last4/exp are mirrored. Keeps Tamma in SAQ-A scope.
- **NO single-user Stripe coupling.** In `SingleUser` mode the routes are not mapped and the
  `NullStripeCustomerPortalClient` no-op wins (parallels 35-1 `NullBillingProvider`).
- **NO Stripe round-trip on read.** `GET /payment-methods` reads only the local mirror.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### What exists and is reused

| Seam | File (verified) | Use in 35-7 |
|---|---|---|
| Tenant→Stripe customer mapping | `BillingCustomer` (Story 35-1, `apps/tamma-elsa/src/Tamma.Data/Entities/BillingCustomer.cs`) | resolve `StripeCustomerId` from `TenantId` for portal/setup-intent |
| Billing provider seam + Null impl | `IBillingProvider` / `NullBillingProvider` (Story 35-1) | mirror the Null-seam pattern for `IStripeCustomerPortalClient` |
| Webhook dispatch registry | `IBillingEventHandler` + `BillingWebhookContext` (Story 35-5) | register `PaymentMethodBillingEventHandler`; `ctx.TenantId` pre-resolved |
| Secret cabinet | `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/ISecretStore.cs`; `SecretScope.Platform`, `SecretPurpose.ApiKey` (`Secrets/SecretPurpose.cs`) | resolve Stripe secret key — never raw env |
| DCB event append | `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` → `AppendAsync(DomainEvent)`; entity `Entities/DomainEvent.cs` (`Type`, `TenantId`, `Tags` JSON, `Data`, `SequenceNumber`) | emit `BILLING.PAYMENT_METHOD.*` / `BILLING.PORTAL_SESSION.CREATED` |
| Alert pipeline (downstream) | `Services/Alerts/IAlertSink.cs` (`RaiseAsync(AlertPayload)`); `AlertRuleEvaluator` polls `DomainEvents` | 35-8 subscribes to our DCB events — no work here |
| Tenant RBAC | `Authorization/TenantRoleHierarchy.cs` (owner=2/admin=1/member=0); `RequireTenantMembershipFilter.TenantRoleItemKey` (used across `OrgEndpoints.cs`, `AlertEndpoints.cs:1010`) | gate portal/setup-intent to owner+admin; GET to any member |
| Authz policies | `Program.cs:966-1038` — `PromptManage`/`ConventionManage` are the owner+admin precedent (`PermissionRequirement("prompts:manage")`) | add `BillingManage` = `PermissionRequirement("billing:manage")` the same way |
| Mode provider | `Services/PromptStore/TammaMode.cs` — `ITammaModeProvider.Mode` (`SingleUser`/`SaaS`), process-stable | gate route mapping |
| At-rest crypto pattern | `Services/Provisioning/TenantSecretProtector.cs` (AES-GCM) | reference only — 35-7 stores no secrets, only masked card data |
| CP DbContext + model config | `ControlPlaneDbContext.cs` (DbSets, line ~33+); `TammaModelConfiguration.cs` (single source for entity config) | register `BillingPaymentMethod` here |
| CP migrations dir | `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/` (snapshot `ControlPlaneDbContextModelSnapshot.cs`) | additive migration here |
| Endpoint mapping | `Program.cs:1443+` (`v1Admin.MapGet("/alerts", ...)`) and tenant `/api/v1/orgs/{tenantId}/...` sections | map billing routes conditionally on SaaS mode |
| Tenant dashboard | `packages/dashboard-user/src/api/alerts.ts`, `pages/alerts/TenantAlertFeed.tsx`, `pages/settings/ConnectedPlatforms.tsx` | mirror conventions for `api/billing.ts` + settings page |

### Where the gaps are

- **No `BillingPaymentMethod` entity / table** exists — `ControlPlaneDbContext` has billing-adjacent
  sets (35-1's `BillingCustomers` lands first) but no payment-method mirror. **NEW.**
- **No portal/setup-intent endpoints** — `Endpoints/` has no `Billing/` directory yet. **NEW.**
- **No Stripe portal client seam** — `Services/Billing/` is created by 35-1; 35-7 adds the portal
  client + payment-method service + the `IBillingEventHandler` impl. **NEW.**
- **No `BillingManage` policy** — `Program.cs` has `PromptManage`/`ConventionManage`; add the
  billing analogue. **MODIFY.**
- **Tenant tests dir** — `apps/tamma-elsa/tests/Tamma.Api.Tests/` exists (xUnit; docker-bound suites
  run `sg docker -c "dotnet test ..."` per `reference_dotnet_test_docker.md`). Add `Billing/`.

### Hard dependency status

35-1 and 35-5 are **drafted, not implemented** (verified: both story files exist under
`docs/stories/epic-35/`, statuses `drafted`). 35-7 must not start until both land — it consumes
`BillingCustomer` (35-1) and the `IBillingEventHandler` registry + `BillingWebhookContext` (35-5).
If a phase needs a seam that 35-1/35-5 haven't shipped, **stop and flag** rather than re-implement it.

---

## Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns a payment method? | n/a — routes not mapped; `NullStripeCustomerPortalClient` registered; no Stripe coupling | the tenant; `BillingPaymentMethod` keyed by `TenantId` |
| Who can open the portal / add a card? | n/a | `tenant_owner` / `tenant_admin` (`BillingManage`); `member` → 403 |
| Who can read masked methods? | n/a | any tenant member (`MemberAccess` + membership filter) |
| Where do DCB events go? | n/a | CP `DomainEvents`, `TenantId` set → tenant-scoped alert feeds (35-8) |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) | same |

---

## Phased task breakdown (TDD — tests first each phase)

### Phase 1 — `BillingPaymentMethod` entity, table, migration (core data)

**Files:** new `Tamma.Data/Entities/BillingPaymentMethod.cs`; modify `ControlPlaneDbContext.cs`
(`DbSet<BillingPaymentMethod> BillingPaymentMethods`) + `TammaModelConfiguration.cs` (table config,
unique index on `StripePaymentMethodId`, index on `TenantId`, partial unique `WHERE is_default`,
CHECK on `Status`); new additive migration under `Migrations/ControlPlane/` (+ snapshot update).

**Tests first:** `tests/Tamma.Api.Tests/Billing/BillingPaymentMethodEntityTests.cs` — round-trip
insert/read; two `IsDefault=true` rows for one tenant violate the partial unique index;
invalid `Status` rejected by CHECK; `StripePaymentMethodId` collision rejected.

**Approach:** mirror 35-1's `billing_customers` config in `TammaModelConfiguration` (single source —
do NOT use data annotations). Run `dotnet ef migrations add AddBillingPaymentMethods --context
ControlPlaneDbContext --output-dir Migrations/ControlPlane`; then
`dotnet ef migrations has-pending-model-changes` MUST report none; apply + down to prove reversible.

- [ ] write entity + EF-config tests (red)
- [ ] add entity, DbSet, model config
- [ ] generate migration; verify no pending model changes; apply + roll back
- [ ] green

### Phase 2 — `IPaymentMethodService` mirror + DCB events + missing-card signal

**Files:** new `Services/Billing/IPaymentMethodService.cs`, `PaymentMethodService.cs`,
`BillingPaymentMethodEventTypes.cs` (constants for the five event names),
`Services/Billing/Dtos` (`PaymentMethodView`, `PaymentMethodMirrorInput`).

**Tests first:** `tests/Tamma.Api.Tests/Billing/PaymentMethodServiceTests.cs` — attach upserts +
emits `BILLING.PAYMENT_METHOD.ADDED`; detach flips `Status=detached` + clears `IsDefault` + emits
`REMOVED`; default re-point clears prior default atomically + emits `DEFAULT_CHANGED`; last usable
card detached on a **paid** tenant emits `BILLING.PAYMENT_METHOD.MISSING`; **free** tenant never
emits MISSING; `ListAsync` returns masked view + correct `hasUsableDefault`; idempotent re-attach
(same `StripePaymentMethodId`) → one row.

**Approach:** all writes inside a CP transaction; DCB append via `IEventRepository.AppendAsync` with
`TenantId` set and `Tags` JSON `{ tenantId, last4, brand, stripePaymentMethodId }`. Paid-plan check
reads subscription/plan state from 35-1/35-4 read-only (inject a small `IBillingPlanState` query
seam if 35-4's surface isn't directly queryable — keep it a thin read).

- [ ] write service tests (red)
- [ ] implement service (upsert/detach/default/list + event emission + missing signal)
- [ ] green

### Phase 3 — Stripe portal client seam + Null impl

**Files:** new `Services/Billing/IStripeCustomerPortalClient.cs`, `StripeCustomerPortalClient.cs`
(wraps `Stripe.BillingPortal.SessionService` + `Stripe.SetupIntentService`),
`NullStripeCustomerPortalClient.cs`. Stripe secret key resolved via the 35-1 cabinet path
(`ISecretStore` / `IBillingProvider`), never `IConfiguration`.

**Tests first:** `tests/Tamma.Api.Tests/Billing/StripeCustomerPortalClientTests.cs` — Null impl
throws/returns no-op as designed; secret unresolvable surfaces the 503 path (assert at endpoint level
in Phase 4). (Live Stripe behaviour is covered in the integration suite, Phase 6.)

**Approach:** parallel the `StripeBillingProvider`/`NullBillingProvider` split from 35-1. The real
client constructs Stripe service options from the cabinet-resolved key per call (or a cached client
refreshed on rotation, matching 35-1's resolver).

- [ ] write client seam tests (red)
- [ ] implement real + null clients
- [ ] green

### Phase 4 — Return-URL allowlist + endpoints + `BillingManage` policy + route mapping

**Files:** new `Services/Billing/ReturnUrlAllowlist.cs`; new
`Endpoints/Billing/BillingPortalEndpoints.cs` (`CreatePortalSession`, `CreateSetupIntent`,
`ListPaymentMethods`); modify `Program.cs` — add `BillingManage` policy (mirror `PromptManage`),
map the three routes **only when `ITammaModeProvider.Mode == SaaS`**, call the DI extension; new
`Extensions/BillingPaymentMethodServiceCollectionExtensions.cs`.

**Tests first:** `tests/Tamma.Api.Tests/Billing/ReturnUrlAllowlistTests.cs` (allowed/foreign/
scheme-downgrade) and `BillingPortalEndpointsTests.cs` — portal: owner/admin 200, member 403,
cross-tenant 404, no Stripe customer 409, Stripe error 502, secret unresolvable 503, emits
`BILLING.PORTAL_SESSION.CREATED`; setup-intent: owner/admin 200, member 403; GET: masked list,
**asserts `IStripeCustomerPortalClient` never called** on read; disallowed `returnUrl` → 400;
single-user mode → routes not mapped (404).

**Approach:** resolve tenant + role from `http.Items[RequireTenantMembershipFilter.TenantRoleItemKey]`
exactly like `OrgEndpoints`/`AlertEndpoints`. Validate `returnUrl` origin before any Stripe call.

- [ ] write allowlist + endpoint tests (red)
- [ ] implement allowlist, endpoints, policy, DI extension, conditional route mapping
- [ ] green

### Phase 5 — `PaymentMethodBillingEventHandler` (register into 35-5 dispatch)

**Files:** new `Services/Billing/PaymentMethodBillingEventHandler.cs` implementing 35-5's
`IBillingEventHandler`; register in the DI extension.

**Tests first:** `tests/Tamma.Api.Tests/Billing/PaymentMethodBillingEventHandlerTests.cs` —
`HandledEventTypes` cover `payment_method.attached/detached/automatically_updated` + `customer.updated`;
dispatch routes each to the right `IPaymentMethodService` call; uses `ctx.TenantId` (never re-derives
tenancy); unknown sub-type is a no-op; **tenant-isolation** — a handler invocation for tenant A never
writes a row/event for tenant B.

**Approach:** thin adapter — parse the Stripe object from `BillingWebhookContext`, call the service.
No heavy work inline (35-5's fast-ack contract); dunning follow-up is 35-8.

- [ ] write handler tests (red)
- [ ] implement handler + register it
- [ ] green

### Phase 6 — Integration tests (Stripe test mode + tenant isolation)

**Files:** integration tests in `tests/Tamma.Api.Tests/Billing/` gated on `STRIPE_SECRET_KEY_TEST`.

**Tests:** create a portal session against Stripe test mode → real `https://billing.stripe.com/...`
URL; create a setup-intent + confirm a test card off-session → replay the `payment_method.attached`
webhook through 35-5's processor → mirror upserted; **tenant-isolation** — replay tenant A's
attach/detach → tenant B has zero rows and no DCB event tagged B; portal for tenant A never returns
tenant B's customer.

- [ ] write integration tests (run via `sg docker -c "dotnet test ..."`)
- [ ] verify against Stripe test mode; full suite green

### Phase 7 — Tenant dashboard surface

**Files:** new `packages/dashboard-user/src/api/billing.ts` (mirror `api/alerts.ts`); new
`pages/settings/BillingPaymentMethods.tsx` (Manage-billing portal button, Add-card via SetupIntent +
Stripe Elements, masked list, missing-card banner); register in the settings router.

**Tests first:** colocated `BillingPaymentMethods.test.tsx` (Vitest + Testing Library) — list renders
masked rows; Manage-billing calls client + redirects to portal URL; Add-card mounts SetupIntent flow;
member-role hides mutate buttons; empty state shows missing-card banner.

**Approach:** load `@stripe/stripe-js` lazily; never render anything but masked last4. `pnpm test
--filter @tamma/dashboard-user` green; no new lint errors.

- [ ] write component tests (red)
- [ ] implement client + page; register route
- [ ] green

---

## Sequencing & dependencies

External prerequisites (must be merged first): **35-1** (`BillingCustomer`, `IBillingProvider`/Null,
cabinet wiring) and **35-5** (`IBillingEventHandler`, `BillingWebhookContext`, webhook ingestion).

Internal order: **P1 → P2 → P3 → P4 → P5 → P6 → P7.** P3 (Stripe client) and P2 (service) are
parallel-safe after P1. P5 needs P2 (service) + 35-5 (registry). P6 needs P4+P5. P7 needs P4 (API
shape). 35-8 (dunning) consumes the DCB events this story emits — it is downstream, not a blocker.

## Risks & mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| 35-1 / 35-5 seams not yet merged | High | Hard-gate: do not start until both land; if a needed seam is absent, stop and flag — never re-implement another story's surface |
| Storing card data → PCI scope blowout | High | Mirror only brand/last4/exp; cards collected client-side via Elements + SetupIntent; PAN/CVC never reach the backend; assert in tests no full-PAN column exists |
| Open-redirect via `returnUrl` | High | `ReturnUrlAllowlist` validates origin against `Billing:PortalReturnUrlAllowlist` before any Stripe call; disallowed → 400 + WARN |
| Two defaults per tenant on a race | Medium | Partial unique index `WHERE is_default` fails loudly; service clears prior default in the same transaction |
| Stripe at-least-once duplicate `attached` | Medium | Upsert keyed on `StripePaymentMethodId` is idempotent; 35-5 dedupe gates re-projection |
| Cross-tenant leak | High | Read/mutate endpoints behind membership filter; webhook handler uses pre-resolved `ctx.TenantId`; dedicated isolation tests in P5+P6 |
| Secret in raw env in prod | High | Resolve via Epic 29 cabinet only; unresolvable → 503, never fail open (mirrors 35-1 AC5 / 35-5 AC2) |
| Logging card data | High | Log only masked last4 + Stripe request id; explicit WARN/ERROR rules forbid URLs/client-secrets/PAN |
| Migration drift | Medium | Additive table; `has-pending-model-changes` must report none; config in `TammaModelConfiguration` only; apply + down verified |

## Acceptance criteria (mirror of the story)

- [ ] `POST /api/v1/billing/portal-session` returns a tenant-scoped Stripe Portal URL; owner/admin only (member 403, cross-tenant 404). Emits `BILLING.PORTAL_SESSION.CREATED`.
- [ ] `POST /api/v1/billing/payment-methods/setup-intent` returns `{ clientSecret, setupIntentId }`; owner/admin only (member 403).
- [ ] `BillingPaymentMethod` mirror stores `{ brand, last4, exp, isDefault, status }`, updated by `PaymentMethodBillingEventHandler` via 35-5 webhooks; raw PAN/card data never stored.
- [ ] `GET /api/v1/billing/payment-methods` returns the masked mirror with **no Stripe round-trip on read**; includes `hasUsableDefault`.
- [ ] `returnUrl` validated against the allowlist (disallowed → 400); sessions/setup-intents short-lived; URLs/secrets never persisted.
- [ ] DCB `BILLING.PAYMENT_METHOD.ADDED/REMOVED/DEFAULT_CHANGED` emitted with `tags { tenantId, last4, brand, stripePaymentMethodId }`.
- [ ] A paid-plan tenant with no usable card emits `BILLING.PAYMENT_METHOD.MISSING` and `hasUsableDefault: false` (signal only — 35-8 acts).
- [ ] Single-user mode: routes not mapped, `NullStripeCustomerPortalClient` registered, no Stripe coupling.
- [ ] Unit + integration tests pass, including tenant-isolation (tenant A webhook/portal never touches tenant B) and redirect-allowlist validation; full C# suite green via `sg docker -c "dotnet test ..."`; `pnpm test --filter @tamma/dashboard-user` green.
