# Story 35-7: Payment Methods & Self-Service Stripe Billing Portal

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-Phase Development Workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), the `.dev/` knowledge-base usage rules (spikes, bugs, findings, decisions), TRACE/DEBUG logging requirements, the Test-Driven Development workflow, and the build/coverage quality gates. Failure to follow this process will result in rework.

## User Story

As a **tenant owner or admin** running on Tamma in SaaS mode,
I want to manage my payment methods and billing details from inside Tamma — open the Stripe Customer Portal for card management, add or replace a default card via a setup-intent, and see my current card's brand/last4 at a glance,
So that I can keep my billing current without leaving the product, never expose raw card data to Tamma, and avoid involuntary churn from a missing or expired card.

## Priority

P1 - Self-service billing management. Required for tenant retention and feeds the dunning/notifications path (Story 35-8): a paid-plan tenant with no payment method is a churn risk that must be surfaced and recoverable.

## Acceptance Criteria

1. `POST /api/v1/billing/portal-session` creates a Stripe Customer Portal session for the caller's tenant (resolving `BillingCustomer.StripeCustomerId` via `BillingCustomer.TenantId` from Story 35-1) and returns `{ url }`. The endpoint is gated to `tenant_owner`/`tenant_admin` (via the dedicated `BillingManage` policy); a `member`-role caller receives `403`. A cross-tenant caller (not a member of the path/active tenant) receives `404`.

2. `POST /api/v1/billing/payment-methods/setup-intent` creates a Stripe `SetupIntent` (usage `off_session`, attached to the tenant's customer) and returns `{ clientSecret, setupIntentId }` so the dashboard can collect/replace the default card with Stripe Elements. Same `tenant_owner`/`tenant_admin` RBAC; `member` → `403`.

3. A new control-plane entity `BillingPaymentMethod` (`apps/tamma-elsa/src/Tamma.Data/Entities/BillingPaymentMethod.cs`) mirrors the masked card for display and dunning decisions: `Id` (Guid PK), `TenantId` (Guid FK to `tenants.Id`), `StripeCustomerId`, `StripePaymentMethodId` (unique), `Brand`, `Last4`, `ExpMonth`, `ExpYear`, `IsDefault` (bool), `Status` (`active|expiring|expired|detached`), `CreatedAt`, `UpdatedAt`. Registered as `DbSet<BillingPaymentMethod>` on `ControlPlaneDbContext` and configured in `TammaModelConfiguration` (table `billing_payment_methods`, unique index on `StripePaymentMethodId`, index on `TenantId`, partial unique index enforcing at most one `IsDefault = true` per tenant, CHECK constraint on `Status`). **Raw PAN, CVC, or full card numbers never touch any Tamma column** — only brand/last4/exp are stored.

4. `GET /api/v1/billing/payment-methods` returns the masked mirror for the caller's tenant (`{ id, brand, last4, expMonth, expYear, isDefault, status }[]`) **from the local table with no Stripe round-trip on read**. Any tenant member (including `member`) may read.

5. The mirror is updated by an `IBillingEventHandler` (Story 35-5 dispatch seam) — `PaymentMethodBillingEventHandler` — that handles `payment_method.attached`, `payment_method.detached`, `payment_method.automatically_updated`, and `customer.updated` (for `invoice_settings.default_payment_method` changes). Attach upserts the mirror row; detach flips `Status = detached` (and clears `IsDefault`); a default change re-points `IsDefault` to the new method (atomically clearing the prior default). 35-7 owns ONLY this handler and the `BillingPaymentMethod` entity — it does not own webhook ingestion (35-5) or subscription/invoice mirrors (35-4/35-8).

6. Portal `return_url` and any client-supplied `returnUrl` are validated against an **allowlist** of configured app origins (`Billing:PortalReturnUrlAllowlist`, defaulting to the dashboard origin) to prevent open-redirect; a `returnUrl` whose origin is not on the allowlist is rejected with `400` and the request is logged WARN. Portal sessions and setup-intents are short-lived/single-use (Stripe-enforced) and Tamma never persists the session URL or client secret.

7. DCB events are emitted via `IEventRepository.AppendAsync(DomainEvent)` (CP store) following the `AGGREGATE.ACTION.STATUS` convention: `BILLING.PAYMENT_METHOD.ADDED` (on attach), `BILLING.PAYMENT_METHOD.REMOVED` (on detach), and `BILLING.PAYMENT_METHOD.DEFAULT_CHANGED` (on default re-point), each with JSONB `tags = { tenantId, last4, brand, stripePaymentMethodId }` and `TenantId` set. A `BILLING.PORTAL_SESSION.CREATED` event is emitted on portal-session creation (`tags = { tenantId, userId }`).

8. A paid-plan tenant (subscription not on the `free` plan slug per Story 35-1's `BillingPlanPrice`/35-4 subscription state) with **zero** `active`/`expiring` `BillingPaymentMethod` rows is flagged for the dunning/notifications path: 35-7 emits a `BILLING.PAYMENT_METHOD.MISSING` DCB event when a detach/expiry leaves a paid tenant with no usable card, and `GET /api/v1/billing/payment-methods` returns a `hasUsableDefault: false` flag. **35-7 does not implement dunning escalation or notification delivery — that is Story 35-8's responsibility**; this story only emits the signal.

9. In **single-user mode** (`ITammaModeProvider` → `SingleUser`) the portal-session, setup-intent, and payment-method routes are **not mapped** and the `NullBillingProvider` seam (Story 35-1 AC4/AC7) means no Stripe wiring is registered. In **SaaS mode** the routes are mapped and tenant resolution is mandatory. This mirrors the per-mode contract in CLAUDE.md "Operating Modes".

10. Secret material (Stripe secret key) is resolved exclusively through the Epic 29 cabinet via `ISecretStore` / the `IBillingProvider` seam established in Story 35-1 (`SecretScope.Platform`, `SecretPurpose.ApiKey`) — never a raw `IConfiguration`/env read. In production billing refuses portal/setup-intent operations if the key is unresolvable, returning `503` and logging ERROR (never fails open).

11. Stripe round-trips (`portal-session`, `setup-intent`) go through an `IStripeCustomerPortalClient` seam wrapping `Stripe.BillingPortal.SessionService` and `Stripe.SetupIntentService`, so the endpoints are unit-testable against a mock and the integration suite can drive Stripe test mode. Stripe API failures map to `502 Bad Gateway` with a `TammaError` (`code "BILLING.PORTAL.STRIPE_ERROR"`, retryable), logged ERROR with the Stripe request id but never card data.

12. Unit + integration tests cover: portal-session creation + RBAC (owner/admin 200, member 403, cross-tenant 404), setup-intent flow, webhook-driven mirror updates for attach/detach/default-change (one DCB event each), masked read with no Stripe call, redirect-allowlist validation (allowed → 200, disallowed origin → 400), `BILLING.PAYMENT_METHOD.MISSING` emission on last-card-detach for a paid tenant, single-user route-not-mapped, and **tenant-isolation** (a `payment_method.attached` webhook for tenant A never writes a `BillingPaymentMethod` row or DCB event for tenant B; a portal session for tenant A never returns tenant B's customer).

## Technical Design

### Namespace & file structure

```
apps/tamma-elsa/src/Tamma.Api/
  Endpoints/Billing/
    BillingPortalEndpoints.cs            # NEW — portal-session, setup-intent, GET payment-methods
  Services/Billing/
    IStripeCustomerPortalClient.cs       # NEW — Stripe portal/setup-intent seam
    StripeCustomerPortalClient.cs        # NEW — Stripe.BillingPortal + Stripe.SetupIntent impl
    NullStripeCustomerPortalClient.cs    # NEW — single-user no-op (parallels NullBillingProvider, 35-1)
    PaymentMethodService.cs              # NEW — mirror upsert/read + DCB emission + missing-card signal
    IPaymentMethodService.cs             # NEW
    PaymentMethodBillingEventHandler.cs  # NEW — IBillingEventHandler (registered into 35-5 dispatch)
    ReturnUrlAllowlist.cs                # NEW — origin allowlist validator
    BillingPaymentMethodEventTypes.cs    # NEW — DCB event-type constants
  Authorization/
    (reuse / add BillingManage policy in Program.cs — see RBAC below)
  Extensions/
    BillingPaymentMethodServiceCollectionExtensions.cs  # NEW — DI wiring (mirrors AlertServiceCollectionExtensions)

apps/tamma-elsa/src/Tamma.Data/
  Entities/BillingPaymentMethod.cs       # NEW
  ControlPlaneDbContext.cs               # MODIFY — DbSet<BillingPaymentMethod>
  TammaModelConfiguration.cs             # MODIFY — entity config (indexes, CHECK, partial-unique default)
  Migrations/ControlPlane/<ts>_AddBillingPaymentMethods.cs  # NEW (+ snapshot update)

packages/dashboard-user/src/
  api/billing.ts                         # NEW — client (mirror api/alerts.ts conventions)
  pages/settings/BillingPaymentMethods.tsx  # NEW — portal button, add-card (SetupIntent + Stripe Elements), masked list
```

### Entity sketch (`BillingPaymentMethod.cs`)

```csharp
namespace Tamma.Data.Entities;

public class BillingPaymentMethod
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string StripeCustomerId { get; set; } = null!;
    public string StripePaymentMethodId { get; set; } = null!; // pm_... (unique)
    public string Brand { get; set; } = null!;                 // visa, mastercard, ...
    public string Last4 { get; set; } = null!;                 // "4242" — NEVER full PAN
    public int ExpMonth { get; set; }
    public int ExpYear { get; set; }
    public bool IsDefault { get; set; }
    public string Status { get; set; } = "active";             // active|expiring|expired|detached
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

EF config (in `TammaModelConfiguration.ConfigureControlPlaneEntities`): `ToTable("billing_payment_methods")`; `HasIndex(StripePaymentMethodId).IsUnique()`; `HasIndex(TenantId)`; partial unique index `CREATE UNIQUE INDEX ix_bpm_one_default ON billing_payment_methods (tenant_id) WHERE is_default`; CHECK `status IN ('active','expiring','expired','detached')`. No navigation to `BillingCustomer` is strictly required — keyed by `TenantId` like the rest of the billing mirrors.

### EF migration sketch

`dotnet ef migrations add AddBillingPaymentMethods --context ControlPlaneDbContext --output-dir Migrations/ControlPlane` →

```sql
CREATE TABLE billing_payment_methods (
  id UUID PRIMARY KEY,
  tenant_id UUID NOT NULL,
  stripe_customer_id TEXT NOT NULL,
  stripe_payment_method_id TEXT NOT NULL,
  brand TEXT NOT NULL,
  last4 TEXT NOT NULL,
  exp_month INT NOT NULL,
  exp_year INT NOT NULL,
  is_default BOOLEAN NOT NULL DEFAULT FALSE,
  status TEXT NOT NULL DEFAULT 'active'
    CHECK (status IN ('active','expiring','expired','detached')),
  created_at TIMESTAMPTZ NOT NULL,
  updated_at TIMESTAMPTZ NOT NULL,
  CONSTRAINT fk_bpm_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id)
);
CREATE UNIQUE INDEX ix_bpm_stripe_pm ON billing_payment_methods (stripe_payment_method_id);
CREATE INDEX ix_bpm_tenant ON billing_payment_methods (tenant_id);
CREATE UNIQUE INDEX ix_bpm_one_default ON billing_payment_methods (tenant_id) WHERE is_default;
```

Additive table — `dotnet ef migrations has-pending-model-changes` must report **none** after the migration; `database update` then down must apply/roll back cleanly. (Mirror the snapshot-update discipline used by Story 35-1's `billing_customers`/`billing_plan_prices` migration.)

### Service signatures

```csharp
public interface IStripeCustomerPortalClient
{
    Task<string> CreatePortalSessionAsync(string stripeCustomerId, string returnUrl, CancellationToken ct);
    Task<(string ClientSecret, string SetupIntentId)> CreateSetupIntentAsync(string stripeCustomerId, CancellationToken ct);
}

public interface IPaymentMethodService
{
    Task<IReadOnlyList<PaymentMethodView> Methods, bool HasUsableDefault> ListAsync(Guid tenantId, CancellationToken ct);
    Task UpsertFromStripeAsync(PaymentMethodMirrorInput input, CancellationToken ct);   // attach/auto-update
    Task MarkDetachedAsync(Guid tenantId, string stripePaymentMethodId, CancellationToken ct);
    Task SetDefaultAsync(Guid tenantId, string stripePaymentMethodId, CancellationToken ct);
}
```

`PaymentMethodService` writes the mirror, emits the DCB event(s), and — on a detach/expiry that leaves a paid tenant with no `active|expiring` card — emits `BILLING.PAYMENT_METHOD.MISSING`. Paid-plan detection reads the tenant's subscription/plan state owned by 35-1/35-4 (read-only); 35-7 does not mutate it.

### Webhook integration (Story 35-5 seam)

`PaymentMethodBillingEventHandler : IBillingEventHandler` declares `HandledEventTypes = { "payment_method.attached", "payment_method.detached", "payment_method.automatically_updated", "customer.updated" }` and implements `HandleAsync(BillingWebhookContext ctx, ct)` — `ctx.TenantId` is already resolved by 35-5 from `BillingCustomer.StripeCustomerId → TenantId`, so the handler never re-derives tenancy (tenant-isolation comes free from the dispatch contract). The handler calls `IPaymentMethodService` and returns; heavy follow-up (dunning) is 35-8's enqueued task, not this handler's job.

### API shape

```
POST /api/v1/billing/portal-session              → 200 { url }            (BillingManage; member 403)
POST /api/v1/billing/payment-methods/setup-intent → 200 { clientSecret, setupIntentId } (BillingManage; member 403)
GET  /api/v1/billing/payment-methods             → 200 { methods: PaymentMethodView[], hasUsableDefault }  (any member)
```

`PaymentMethodView = { id, brand, last4, expMonth, expYear, isDefault, status }`. All three resolve the tenant from the membership filter (active-tenant / path-tenant), exactly like `OrgEndpoints`/`AlertEndpoints` (`http.Items[RequireTenantMembershipFilter.TenantRoleItemKey]`).

### RBAC — per-mode + per-tenant

| Action | single-user | SaaS |
|---|---|---|
| Open portal / create setup-intent | route not mapped (NullBillingProvider) | `tenant_owner` / `tenant_admin` (member → 403) |
| GET masked payment methods | route not mapped | any tenant member (member-read) |
| Mirror update (webhook) | n/a | system, via 35-5 dispatch (tenant resolved upstream) |

The `BillingManage` policy is added in `Program.cs` mirroring `PromptManage`/`ConventionManage` (a `PermissionRequirement("billing:manage")` mapped to owner+admin in the role matrix). GET routes use `MemberAccess` + the membership filter. This matches the prompt-store precedent: identical endpoint shape across modes, auth middleware decides scope.

### DCB event names

`BILLING.PAYMENT_METHOD.ADDED`, `BILLING.PAYMENT_METHOD.REMOVED`, `BILLING.PAYMENT_METHOD.DEFAULT_CHANGED`, `BILLING.PAYMENT_METHOD.MISSING`, `BILLING.PORTAL_SESSION.CREATED` — all appended to the CP `DomainEvents` store via `IEventRepository.AppendAsync`, tags carry `tenantId` (and the relevant `last4`/`brand`/`stripePaymentMethodId`). These are exactly the events the alert-rule evaluator polls, so dunning/notification rules (35-8) can subscribe with no engine changes.

## Dependencies

**Internal (prerequisite):**
- **Story 35-1** (Stripe Integration Foundation): provides `BillingCustomer` (the `TenantId → StripeCustomerId` mapping the portal/setup-intent endpoints resolve), the `IBillingProvider` + `NullBillingProvider` seam, and the Epic 29 cabinet wiring for the Stripe secret key.
- **Story 35-5** (Stripe Webhook Ingestion): provides the verified webhook endpoint and the `IBillingEventHandler` dispatch registry into which `PaymentMethodBillingEventHandler` registers — 35-7 does **not** build webhook ingestion.

**Internal (blocks / feeds):**
- **Story 35-8** (Invoicing & Dunning): consumes `BILLING.PAYMENT_METHOD.MISSING` / `REMOVED` events and the `hasUsableDefault` signal to drive dunning escalation and notifications. 35-7 emits the signal; 35-8 acts on it.

**Internal (reuse, no change):**
- Epic 29 secret cabinet (`ISecretStore`, `SecretScope.Platform`, `SecretPurpose.ApiKey`).
- Story 5.6 alert pipeline (`IEventRepository` → `AlertRuleEvaluator`) — DCB events feed it for free.
- `RequireTenantMembershipFilter` + `TenantRoleHierarchy` for tenant RBAC.
- `IPlatformQueuedTaskRepository` (only if a portal-session retry is desired; not required for AC).

**External:**
- `Stripe.net` SDK (`Stripe.BillingPortal.SessionService`, `Stripe.SetupIntentService`, `Stripe.PaymentMethodService`) — already a dependency from Story 35-1/35-5.
- Stripe test mode + `@stripe/stripe-js` / `@stripe/react-stripe-js` for the dashboard add-card UX.
- Test secret: `STRIPE_SECRET_KEY_TEST` for the integration suite.

## Testing Strategy

**Unit tests** (`apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/`):
1. `BillingPortalEndpointsTests` — portal-session: owner/admin → 200 with mocked `IStripeCustomerPortalClient`; member → 403; non-member/cross-tenant → 404; unresolvable Stripe customer → 409/422; Stripe error → 502; emits `BILLING.PORTAL_SESSION.CREATED`.
2. setup-intent: owner/admin → 200 `{ clientSecret, setupIntentId }`; member → 403.
3. `GET payment-methods` — returns masked mirror, **asserts `IStripeCustomerPortalClient` is never called** (no Stripe round-trip on read); `hasUsableDefault` true/false.
4. `PaymentMethodServiceTests` — attach upserts + emits `BILLING.PAYMENT_METHOD.ADDED`; detach flips `Status=detached`, clears `IsDefault`, emits `BILLING.PAYMENT_METHOD.REMOVED`; default change re-points atomically + emits `BILLING.PAYMENT_METHOD.DEFAULT_CHANGED`; last usable card detached on a paid tenant emits `BILLING.PAYMENT_METHOD.MISSING`; never emits MISSING for a free-plan tenant.
5. `ReturnUrlAllowlistTests` — allowed origin passes; foreign origin / scheme-downgrade / open-redirect attempt rejected.
6. `PaymentMethodBillingEventHandlerTests` — `HandledEventTypes` cover the four event names; dispatch routes attach/detach/default to the right service call; unknown sub-type is a no-op.
7. Single-user mode: routes are not mapped (404) and no Stripe client is registered.

**Integration tests** (`STRIPE_SECRET_KEY_TEST`; xUnit, docker-bound suites via `sg docker -c "dotnet test ..."`):
8. Create a portal session against Stripe test mode for a seeded test customer → non-empty `https://billing.stripe.com/...` URL.
9. Create a setup-intent, confirm a Stripe test card off-session, verify the `payment_method.attached` webhook (replayed through 35-5's processor) upserts the `BillingPaymentMethod` mirror.
10. **Tenant-isolation**: replay a `payment_method.attached`/`detached` for tenant A's customer; assert tenant B has zero `billing_payment_methods` rows and no DCB event tagged tenant B; assert a portal session for tenant A's customer never returns tenant B's `StripeCustomerId`.

**Mocks:** `IStripeCustomerPortalClient` mocked for all endpoint unit tests (no live Stripe); Stripe SDK driven against test mode only in the integration suite. `ISecretStore` stubbed to return a test key in unit tests.

**Dashboard tests** (`packages/dashboard-user`, Vitest + Testing Library, colocated): `BillingPaymentMethods.test.tsx` — masked list renders rows; "Manage billing" calls the portal client and redirects; "Add card" mounts the SetupIntent flow; member-role hides mutate buttons; empty-state shows the missing-card banner.

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/BillingPortalEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/IStripeCustomerPortalClient.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/StripeCustomerPortalClient.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/NullStripeCustomerPortalClient.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/IPaymentMethodService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/PaymentMethodService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/PaymentMethodBillingEventHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/ReturnUrlAllowlist.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingPaymentMethodEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/BillingPaymentMethodServiceCollectionExtensions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/BillingPaymentMethod.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddBillingPaymentMethods.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (add `DbSet<BillingPaymentMethod>`) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/ControlPlaneDbContextModelSnapshot.cs` | Modify (snapshot) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (add `BillingManage` policy; map routes in SaaS mode; call DI extension) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingPortalEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/PaymentMethodServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/PaymentMethodBillingEventHandlerTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/ReturnUrlAllowlistTests.cs` | Create |
| `packages/dashboard-user/src/api/billing.ts` | Create |
| `packages/dashboard-user/src/pages/settings/BillingPaymentMethods.tsx` | Create |
| `packages/dashboard-user/src/pages/settings/BillingPaymentMethods.test.tsx` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions (billing/Stripe, tenant-isolation)
3. Read Story 35-1 (`BillingCustomer`, `IBillingProvider`, cabinet wiring) and Story 35-5 (`IBillingEventHandler`, `BillingWebhookContext`) — they define the seams this story plugs into
4. Reviewed `OrgEndpoints.cs` + `AlertEndpoints.cs` for the tenant-membership-filter RBAC pattern (`http.Items[RequireTenantMembershipFilter.TenantRoleItemKey]`)
5. Planned the TDD approach (Red-Green-Refactor)

### Key Design Decisions

- **No raw card data, ever.** Tamma stores only brand/last4/exp from Stripe's `Card` object on a `PaymentMethod`. PAN/CVC are PCI-scope and stay entirely in Stripe; the dashboard collects cards client-side via Stripe Elements + SetupIntent, so card numbers never traverse Tamma's backend. This keeps Tamma out of PCI-DSS SAQ-D scope (SAQ-A territory).
- **Mirror, don't query.** `GET /payment-methods` reads the local `billing_payment_methods` table; Stripe is the source of truth but is kept in sync by 35-5 webhooks, not polled on every read. A unit test asserts the portal client is never invoked on read.
- **One default per tenant** enforced by a partial unique index (`WHERE is_default`), so a race during a default re-point fails loudly at the DB instead of silently keeping two defaults.
- **35-7 emits the missing-card signal; 35-8 acts on it.** Honoring the epic boundary: dunning escalation, retries, and notification delivery belong to Story 35-8. 35-7 stops at the DCB event + the `hasUsableDefault` flag.
- **Open-redirect guard is mandatory.** A portal return URL is a redirect target; allowlisting the origin prevents an attacker from crafting a `returnUrl` that bounces a logged-in admin to a phishing page after Stripe.
- **Per-mode seam parity with 35-1/35-5.** `NullStripeCustomerPortalClient` keeps single-user deployments Stripe-free; routes are simply not mapped when `ITammaModeProvider.Mode == SingleUser`.

### Security Requirements

- Stripe secret key via Epic 29 cabinet only; never `IConfiguration`/env in production. Unresolvable → `503`, never fail open (mirrors 35-1 AC5 / 35-5 AC2).
- Never log card data, client secrets, or session URLs. Log the Stripe request id (for support) and the masked last4 only.
- `returnUrl` origin validated against `Billing:PortalReturnUrlAllowlist` before any Stripe call.
- Tenant-isolation enforced by the membership filter on read/mutate endpoints and by 35-5's upstream tenant resolution on the webhook handler — both covered by dedicated isolation tests.

### Edge Cases

- Tenant with no `BillingCustomer.StripeCustomerId` yet (35-1 enqueued a retry): portal/setup-intent return `409 Conflict` with a "billing not yet provisioned" message rather than 500.
- Card expiry: a `customer.updated`/`payment_method.automatically_updated` that bumps exp updates the mirror; a `Status=expiring` heuristic (exp within current month) feeds the missing-card calculation conservatively (still "usable" until actually declined).
- Duplicate `payment_method.attached` (Stripe at-least-once): upsert keyed on `StripePaymentMethodId` is idempotent — one row, and 35-5's dedupe already gates re-projection.
- Detaching a non-default card never emits `DEFAULT_CHANGED` or `MISSING` if a default remains.

## Logging Requirements

- **INFO**: portal session created (tenantId, userId — no URL), setup-intent created (tenantId, setupIntentId), mirror upsert/detach/default-change (tenantId, last4, eventType), missing-card signal emitted (tenantId, planSlug).
- **DEBUG**: incoming webhook sub-type routed to handler, masked-list query executed (row count), allowlist check result (origin, allowed).
- **WARN**: `returnUrl` origin rejected (origin — never full URL), portal requested for a tenant with no Stripe customer, paid tenant left with no usable card.
- **ERROR**: Stripe API failure (Stripe request id, event/customer id — never card data), Stripe secret unresolvable (`503` path), DB write failure on mirror upsert.
- **Structured context**: `{ tenantId, userId, stripePaymentMethodId, brand, last4, eventType }` where applicable.
- **Credential safety**: NEVER log Stripe secret keys, client secrets, session URLs, or any card number beyond the masked last4.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
