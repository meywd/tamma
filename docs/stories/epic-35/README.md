# Epic 35: Billing & Payments (C#) — Stripe, BYOK-Aware Metering, Subscriptions, Invoicing & Dunning

## Overview

Epic 35 monetizes the Tamma SaaS platform on the **current C# control plane** (`apps/tamma-elsa`:
`Tamma.Api` minimal-API + `Tamma.Data` `ControlPlaneDbContext` + `Tamma.Activities`/Elsa +
`TaskQueueProcessor`). It maps each tenant to a Stripe customer, manages the full subscription
lifecycle, and charges for platform-provided AI usage (cost basis + margin) while treating BYOK
tenants as a flat platform/seat fee with **zero token markup**. On top of that foundation it builds
production-grade invoicing, payment methods, dunning, tax, a self-service billing portal, and a
prepaid credits wallet — all per-tenant-isolated and audited via DCB `PlatformEvent`s.

This epic **supersedes the deleted TypeScript Epic 20** (which lived in `packages/api`, now removed).
Epic 35 re-targets every Epic 20 capability onto the live C# stack and splits the old "plan model"
and "usage-limit model" concerns out to **Epic 34 (Pricing, Plans & Entitlements)** — the pricing
model layer. **Epic 34 owns the price-book** (plans, features, typed entitlements, prices, the
cost→price margin engine `IUsagePricingEngine`); **Epic 35 charges against it** (Stripe customers,
subscriptions, metered usage reporting, invoicing, dunning, wallet). Epic 35 never re-implements
margin math — it calls Epic 34's `IUsagePricingEngine.PriceUsage(...)`.

The defining constraint is **BYOK-awareness**: platform-provided LLM usage is metered, cost-priced,
and reported to Stripe Billing Meters, while BYOK tenants (provider key supplied by the tenant and
held in the **Epic 29 secret cabinet**) are billed only the plan/seat fee with no per-token charge.

**Per-mode (CLAUDE.md "two scoping models" rule):** billing is **SaaS-only**. In single-user mode the
principal is the user, the deployment is self-hosted, and there is no Stripe coupling — a
`NullBillingProvider` seam makes every billing path a no-op (zero Stripe SDK calls, billing endpoints
absent/404). In SaaS mode the principal is the tenant; billing endpoints are RBAC-gated
(`tenant_owner`/`tenant_admin` manage, `member` read-only), mirroring the prompt/convention store
ownership pattern. State is schema-per-tenant for tenant-resident data; billing state itself
(Stripe mappings, usage rollups, invoice mirrors, wallet ledger) is **control-plane resident, keyed
by `tenant_id`**, because that is where the tenant registry, plans, and `PlatformAnalyticsHourly`
already live.

## Plans & Pricing

> **Pricing model lives in Epic 34** (Pricing, Plans & Entitlements). Epic 35 does **not** define
> prices, quotas, or margin — it reads the resolved plan/price from Epic 34's control-plane price-book
> and **charges against it** through Stripe. The table below is the Epic 34 baseline catalog, shown
> here only to ground what Epic 35 bills. Concrete numeric limits are seeded as `PlanEntitlement`
> rows (Epic 34) and are admin-editable.

| Plan | Pricing mode | Recurring (USD/mo) | LLM Tokens | Seats | What Epic 35 charges |
|------|-------------|--------------------|-----------|-------|----------------------|
| **Free** | platform-provided | $0 | metered / block at quota | 1 | no recurring; usage blocked at quota (35-6) |
| **Team** | platform-provided · BYOK | paid · seat fee | platform: cost + margin · BYOK: 0 token markup | multi | base/seat fee + metered platform tokens (BYOK tokens not billed) |
| **Enterprise** | platform-provided · BYOK | custom | custom (`NULL` = unlimited) | custom | custom contract; base + metered platform tokens |

- **Platform-provided usage** → metered to Stripe meters (`tamma.platform_tokens_input`,
  `tamma.platform_tokens_output`), priced `cost basis × (1 + margin)` via Epic 34's
  `IUsagePricingEngine` (default global margin `1.3×`).
- **BYOK usage** → recorded for analytics only (`Byok*` token counters); **never** emitted as a token
  meter event. BYOK tenants pay the plan/seat fee alone.

## Stories

| Story | Title | Priority | Status | Est. Effort |
|-------|-------|----------|--------|-------------|
| 35-1 | Stripe Integration Foundation, Billing Plan Catalog & Customer Mapping (C#) | P0 | drafted | 4-5 days |
| 35-2 | BYOK vs Platform-Provided Billing Mode & Per-Tenant Provider Key Cabinet Integration | P0 | drafted | 3-4 days |
| 35-3 | BYOK-Aware Usage Metering & Stripe Meter Event Reporting | P0 | drafted | 5-6 days |
| 35-4 | Subscription Lifecycle — Create, Upgrade/Downgrade, Cancel, Trial & Proration | P0 | drafted | 4-5 days |
| 35-5 | Stripe Webhook Ingestion, Idempotency & Billing Event Projection | P0 | drafted | 4-5 days |
| 35-6 | Plan Quota & Usage-Limit Enforcement (BYOK-Aware) | P0 | drafted | 3-4 days |
| 35-7 | Payment Methods & Self-Service Stripe Billing Portal | P1 | drafted | 3-4 days |
| 35-8 | Invoicing, Failed-Payment Dunning & Recovery | P0 | drafted | 4-5 days |
| 35-9 | Tax Calculation & Compliance (Stripe Tax / VAT) | P1 | drafted | 3-4 days |
| 35-10 | Credits & Prepaid Wallet Ledger | P2 | drafted | 4-5 days |
| 35-11 | Tenant Billing Dashboard (dashboard-user) & Admin Billing Console (dashboard) | P1 | drafted | 5-6 days |
| 35-12 | Billing Audit, Reconciliation & Revenue Analytics | P1 | drafted | 3-4 days |

## Architecture

```
+-----------------------------------------------------------------------------------+
|        EPIC 35: BILLING & PAYMENTS (C#) — apps/tamma-elsa control plane            |
|        (SaaS-only; single-user => NullBillingProvider no-op seam)                  |
+-----------------------------------------------------------------------------------+
|                                                                                   |
|  +-- LAYER 1: Stripe Foundation (35-1, 35-2) ------------------------------------+ |
|  |  Tamma.Api/Services/Billing + Tamma.Data/Entities (ControlPlaneDbContext)     | |
|  |  +------------------+  +------------------+  +------------------+              | |
|  |  | IBillingProvider |  | BillingCustomer  |  | BYOK vs Platform |              | |
|  |  | StripeClient     |  | (tenant->Stripe) |  | BillingMode      |              | |
|  |  | (Stripe.net SDK) |  | + plan catalog   |  | (Epic 29 cabinet)|              | |
|  |  | key via cabinet  |  | (Stripe ids/mtrs)|  |                  |              | |
|  |  +------------------+  +------------------+  +------------------+              | |
|  +--------------------------------------|----------------------------------------+ |
|                                         v                                          |
|  +-- LAYER 2: Subscriptions (35-4) ----------------------------------------------+ |
|  |  +------------------+  +------------------+  +------------------+              | |
|  |  | Create / Trial   |  | Upgrade / Down-  |  | Cancel +         |              | |
|  |  | (charge vs Epic34|  | grade + Proration|  | Reactivate       |              | |
|  |  |  resolved price) |  |                  |  |                  |              | |
|  |  +------------------+  +------------------+  +------------------+              | |
|  +--------------------------------------|----------------------------------------+ |
|                                         v                                          |
|  +-- LAYER 3: BYOK-Aware Metering (35-3) ----------------------------------------+ |
|  |  source: DCB LLM.CALL.* events + ProviderDiagnostic cost substrate            | |
|  |  +------------------+  +------------------+  +------------------+              | |
|  |  | BillingUsage     |  | platform-only -> |  | BYOK -> counters |              | |
|  |  | Rollup (CP, per  |  | Stripe Meters    |  | only (NO token   |              | |
|  |  | tenant/period)   |  | (batched flush)  |  | meter, NO markup)|              | |
|  |  | price=Epic34 eng |  |                  |  |                  |              | |
|  |  +------------------+  +------------------+  +------------------+              | |
|  +--------------------------------------|----------------------------------------+ |
|                                         v                                          |
|  +-- LAYER 4: Enforcement (35-6) ------------------------------------------------+ |
|  |  +------------------+  +------------------+  +------------------+              | |
|  |  | Pre-dispatch     |  | Graceful degrade |  | BYOK exempt from |              | |
|  |  | quota check      |  | + over-quota     |  | token quotas     |              | |
|  |  | (Epic34 entitl.) |  | alerts/events    |  |                  |              | |
|  |  +------------------+  +------------------+  +------------------+              | |
|  +--------------------------------------|----------------------------------------+ |
|                                         v                                          |
|  +-- LAYER 5: Invoicing & Dunning (35-8) + Webhooks (35-5) ----------------------+ |
|  |  +------------------+  +------------------+  +------------------+              | |
|  |  | Invoice mirror   |  | Failed-payment   |  | Stripe webhook   |              | |
|  |  | + line items     |  | dunning ladder   |  | ingest + idem-   |              | |
|  |  | (PDF via Stripe) |  | (past_due/grace/ |  | potency + event  |              | |
|  |  |                  |  |  suspended)      |  | projection       |              | |
|  |  +------------------+  +------------------+  +------------------+              | |
|  +--------------------------------------|----------------------------------------+ |
|                                         v                                          |
|  +-- LAYER 6: Tax (35-9) --------------------------------------------------------+ |
|  |  Stripe Tax / VAT calc, tax id collection, reverse-charge handling            | |
|  +--------------------------------------|----------------------------------------+ |
|                                         v                                          |
|  +-- LAYER 7: Portal & Payment Methods (35-7) + Credits Wallet (35-10) ----------+ |
|  |  +------------------+  +------------------+  +------------------+              | |
|  |  | Stripe Billing   |  | Payment-method   |  | Prepaid wallet   |              | |
|  |  | Portal session   |  | mirror + setup   |  | ledger + top-up  |              | |
|  |  |                  |  | intents          |  | (Epic34 credits) |              | |
|  |  +------------------+  +------------------+  +------------------+              | |
|  +--------------------------------------|----------------------------------------+ |
|                                         v                                          |
|  +-- LAYER 8: Dashboards & Analytics (35-11, 35-12) -----------------------------+ |
|  |  +-----------------------------+  +-----------------------------+              | |
|  |  | packages/dashboard-user     |  | packages/dashboard (admin)  |              | |
|  |  | tenant billing area:        |  | admin billing console:      |              | |
|  |  | plan/mode/usage/invoices/   |  | per-tenant subs/MRR, catalog|              | |
|  |  | payment/wallet/dunning      |  | sync, webhook/recon health  |              | |
|  |  +-----------------------------+  +-----------------------------+              | |
|  |       + 35-12: billing audit, reconciliation & revenue analytics              | |
|  +------------------------------------------------------------------------------+ |
|                                                                                   |
|  All mutations emit DCB PlatformEvents (BILLING.*) via IEventRepository.AppendAsync |
+-----------------------------------------------------------------------------------+
```

## Key Technical Decisions

### Stripe SDK in C# (`Stripe.net`)

Billing uses the **`Stripe.net`** NuGet package (latest stable) registered in `Tamma.Api`, not the
TypeScript `stripe` package the deleted Epic 20 used. Stripe access is wrapped behind an
`IBillingProvider` seam (`StripeBillingProvider` for SaaS, `NullBillingProvider` for single-user) so
the rest of the codebase never references the SDK directly. **Research the latest `Stripe.net` API
surface** (`Billing.MeterService`, `Billing.MeterEvents`, `RequestOptions.IdempotencyKey`) before
writing any call — do not assume method shapes.

### Stripe Billing Meters for usage

Usage is reported through **Stripe Billing Meters** (modern meter-event API), not the legacy usage
records API. Three meters are seeded by Story 35-1's `seed-billing` command:
`tamma.platform_tokens_input` (SUM), `tamma.platform_tokens_output` (SUM), and `tamma.seats`
(LAST/gauge). Token counts are integers, so meter `value` payloads are whole-number strings. Events
are pre-aggregated to one event per tenant-per-meter-per-period and batch-flushed (default 60s),
keeping us orders of magnitude under Stripe's ~1,000 events/sec limit.

### BYOK excluded from billable metering

The hard rule: **only `billing_mode = platform` usage becomes a Stripe token meter event.** BYOK
usage is recorded on the rollup (`Byok*` counters) for analytics but is *never* buffered as a token
meter event and is exempt from token quotas. BYOK status flows from the **Epic 29 secret cabinet**
(tenant supplied its own provider key) and is recorded on `BillingCustomer.BillingMode` (35-2). A
missing `billing_mode` tag is treated as `platform` and WARN-logged so a wiring gap surfaces as
revenue, not silent zeroing.

### Derive usage from durable facts (don't capture on the hot path)

Rather than hook the LLM call path to enqueue a meter event per call, metering **recomputes rollups
from already-persisted facts** (DCB `LLM.CALL.*` events + `ProviderDiagnostic`/`PlatformAnalyticsHourly`
cost substrate). This is idempotent (recompute = same answer), resilient (a worker crash loses
nothing — facts are durable), and **fail-open for billing** — a Stripe/pricing failure never blocks
an LLM call or a tenant workflow.

### Webhook idempotency

Stripe webhooks (Story 35-5) are signature-verified with the cabinet-sourced signing secret and
processed **exactly once**: each `stripe_event_id` is recorded in a control-plane table with a unique
constraint, so a redelivered webhook is a no-op. Verified events are projected into the local billing
mirror and re-emitted as DCB `BILLING.*` events for the audit trail. Outbound mutating Stripe calls
use **deterministic idempotency keys** (e.g. `billing-customer-{tenantId}`,
`{tenantId}:{period}:{eventName}`) so retries never mint duplicate Stripe objects or double-bill.

### Control-plane resident, per-mode, per-tenant isolated

All billing state lives on the `ControlPlaneDbContext` keyed by `tenant_id` (one query answers "what
does every tenant owe"). RBAC and ownership follow the CLAUDE.md per-mode model: SaaS read =
`member`, manage = `tenant_owner`/`tenant_admin`; single-user = the sole user with no Stripe at all.
A tenant resolves its own billing data from the ambient `ITenantContext` (never a route param), and
cross-tenant isolation is asserted by dedicated tests.

## Supersedes Epic 20

Epic 35 replaces the deleted TypeScript **Epic 20: Billing & Payments for Tamma SaaS** (which lived in
the now-removed `packages/api` on Fastify + the `stripe` npm package). The plan/pricing and
usage-limit *model* concerns were split out to **Epic 34 (Pricing, Plans & Entitlements)**; the Stripe
integration, metering, subscriptions, invoicing, dunning, and dashboards moved to **Epic 35** on the
C# stack. The mapping:

| Epic 20 (deleted TS, `packages/api`) | Replacement | Notes |
|--------------------------------------|-------------|-------|
| **20-1** Stripe Integration & Plan Model | **35-1** (Stripe integration foundation, customer mapping, billing plan catalog) **+ Epic 34-1** (plan & price-book data model) | Stripe-side foundation → 35-1 (C#, `Stripe.net`, cabinet-sourced key). The **plan/price model** → Epic 34's typed catalog (replaces the opaque `Plan.Quotas` JSON). |
| **20-2** Subscription Management | **35-4** (subscription lifecycle: create/upgrade/downgrade/cancel/trial/proration) **+ 35-5** (webhook ingestion & event projection) | Subscription CRUD + checkout → 35-4; the Stripe webhook handler is promoted to its own hardened, idempotent story 35-5. |
| **20-3** Usage Metering | **35-3** (BYOK-aware usage metering & Stripe meter event reporting) | Re-targeted to C# Billing Meters with the new **BYOK split** (platform metered + priced, BYOK counters-only) and derive-from-durable-facts design; pricing math delegated to Epic 34-5. |
| **20-4** Usage Limits Enforcement | **35-6** (plan quota & usage-limit enforcement, BYOK-aware) **+ Epic 34** (entitlement/quota model) | Enforcement mechanism → 35-6 (BYOK exempt from token quotas); the quota/entitlement *model* → Epic 34's typed `PlanEntitlement` rows. |
| **20-5** Billing Dashboard | **35-11** (tenant billing dashboard `dashboard-user` + admin billing console `dashboard`) | Re-targeted to the two React dashboards; reads existing 35-x endpoints only (no business logic in React). |
| *(new in Epic 35, no Epic 20 equivalent)* | **35-7** portal/payment methods · **35-8** invoicing & dunning · **35-9** tax · **35-10** credits wallet · **35-12** billing audit/reconciliation/revenue analytics | Production-grade capabilities the TS epic never reached. |

> **Do not reference `packages/api`** — it is deleted and is where the stale Epic 20 lived. All Epic 35
> work lands in `apps/tamma-elsa` (C#) and the two dashboards (`packages/dashboard-user`,
> `packages/dashboard`).

## Dependencies

### On other epics

- **Epic 28** — control plane: `Tenant`/`Plan` entities, `ControlPlaneDbContext`,
  `PlatformQueuedTask` + `IPlatformTaskHandler` / `TaskQueueProcessor`, `OrgEndpoints`/`AuthEndpoints`
  tenant-create paths, `ITammaModeProvider`, schema-per-tenant tenancy.
- **Epic 29** — secret cabinet: `ISecretStore`/`ISecretStoreBackend`/`IRuntimeSecretResolver`,
  `SecretScope.Platform`, `SecretPurpose.ApiKey`/`Webhook` (Stripe secret key + webhook signing
  secret; per-tenant BYOK provider keys that drive `BillingMode`).
- **Epic 34** — pricing model layer: the plan/price-book catalog, typed `PlanEntitlement` quotas, and
  the cost→price margin engine `IUsagePricingEngine` (35-3 calls it; 35-6 reads entitlements; 35-1
  maps `Plan.Slug` → Stripe ids). **Epic 35 charges against Epic 34; it never re-implements pricing.**
- **Epic 4** — DCB events: `DomainEvent`/`PlatformEvent`, `IEventRepository.AppendAsync` (the
  `BILLING.*` audit trail; reconciliation/dunning events feed `AlertRuleEvaluator`).
- **Epic 5 / 23 / 28-10** — `PlatformAnalyticsHourly` CP fact table (the metering aggregation source;
  35-3 needs a `billing_mode` token split).
- **Epic 9 / 32-9** — `ProviderDiagnostic` cost substrate + per-call usage/cost-basis events.

### External

- **`Stripe.net`** NuGet package (latest stable) — added to `Tamma.Api.csproj`.
- **Stripe account** with test + live keys, Billing Meters and Stripe Tax enabled.
- **Stripe Dashboard** product/price/meter configuration, seeded idempotently via `seed-billing`.

## Database Schema (control-plane, `ControlPlaneDbContext`)

All billing tables are control-plane resident and keyed by `tenant_id`. Sketch (authoritative
column lists live in each story's `Technical Design`):

```sql
-- 35-1: tenant -> Stripe customer mapping + billing mode
CREATE TABLE billing_customers (
  "Id"               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "TenantId"         UUID NOT NULL UNIQUE REFERENCES tenants("Id") ON DELETE CASCADE,
  "StripeCustomerId" TEXT,                                   -- null until Stripe acks (retry path)
  "BillingMode"      TEXT NOT NULL DEFAULT 'PlatformProvided'
                       CHECK ("BillingMode" IN ('PlatformProvided','Byok')),
  "DefaultCurrency"  TEXT NOT NULL DEFAULT 'usd',
  "TaxStatus"        TEXT NOT NULL DEFAULT 'none',           -- none | taxable | reverse_charge
  "CreatedAt"        TIMESTAMPTZ NOT NULL,
  "UpdatedAt"        TIMESTAMPTZ NOT NULL
);
CREATE UNIQUE INDEX ix_billing_customers_stripe
  ON billing_customers ("StripeCustomerId") WHERE "StripeCustomerId" IS NOT NULL;

-- 35-1: Stripe id catalog per plan slug (NOT overloading Plan.Quotas; platform-global)
CREATE TABLE billing_plan_prices (
  "Id"                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "PlanSlug"             TEXT NOT NULL UNIQUE,                -- free | team | enterprise
  "StripeProductId"      TEXT,
  "StripePriceId"        TEXT,                                -- base (flat seat/platform) price
  "TokensInputMeterId"   TEXT, "TokensInputPriceId"  TEXT,
  "TokensOutputMeterId"  TEXT, "TokensOutputPriceId" TEXT,
  "SeatsMeterId"         TEXT, "SeatsPriceId"         TEXT,
  "CreatedAt"            TIMESTAMPTZ NOT NULL,
  "UpdatedAt"            TIMESTAMPTZ NOT NULL
);

-- 35-4: subscription mirror (lifecycle state projected from Stripe webhooks)
CREATE TABLE billing_subscriptions (
  "Id"                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "TenantId"              UUID NOT NULL REFERENCES tenants("Id") ON DELETE CASCADE,
  "StripeSubscriptionId"  TEXT UNIQUE,
  "PlanSlug"              TEXT NOT NULL,
  "Status"                TEXT NOT NULL,                      -- trialing|active|past_due|canceled|...
  "CurrentPeriodStart"    TIMESTAMPTZ,
  "CurrentPeriodEnd"      TIMESTAMPTZ,
  "CancelAtPeriodEnd"     BOOLEAN NOT NULL DEFAULT false,
  "TrialEnd"              TIMESTAMPTZ,
  "DunningStage"          TEXT,                               -- 35-8: null|past_due|grace|suspended
  "CreatedAt"             TIMESTAMPTZ NOT NULL,
  "UpdatedAt"             TIMESTAMPTZ NOT NULL
);

-- 35-3: per-tenant per-period usage rollup (token split by billing mode)
CREATE TABLE billing_usage_rollup (
  "Id"                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "TenantId"             UUID NOT NULL REFERENCES tenants("Id") ON DELETE CASCADE,
  "PeriodStart"          TIMESTAMPTZ NOT NULL,
  "PeriodEnd"            TIMESTAMPTZ NOT NULL,
  "PlatformInputTokens"  BIGINT NOT NULL DEFAULT 0,
  "PlatformOutputTokens" BIGINT NOT NULL DEFAULT 0,
  "ByokInputTokens"      BIGINT NOT NULL DEFAULT 0,          -- analytics only; never metered
  "ByokOutputTokens"     BIGINT NOT NULL DEFAULT 0,
  "PlatformCostUsd"      NUMERIC(20,4) NOT NULL DEFAULT 0,   -- cost basis (no markup)
  "BillableAmountUsd"    NUMERIC(20,4) NOT NULL DEFAULT 0,   -- from Epic 34 IUsagePricingEngine
  "Seats"                INTEGER NOT NULL DEFAULT 0,
  "LastSourceCursor"     BIGINT NOT NULL DEFAULT 0,          -- incremental recompute watermark
  "UpdatedAt"            TIMESTAMPTZ NOT NULL,
  CONSTRAINT uq_billing_usage_rollup_period UNIQUE ("TenantId","PeriodStart")
);

-- 35-3: pending Stripe meter events (buffered, idempotent flush)
CREATE TABLE billing_meter_event_buffer (
  "Id"               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "TenantId"         UUID NOT NULL REFERENCES tenants("Id") ON DELETE CASCADE,
  "StripeCustomerId" TEXT NOT NULL,
  "EventName"        TEXT NOT NULL,                          -- tamma.platform_tokens_input|output
  "Value"            TEXT NOT NULL,                          -- whole-number string per Stripe
  "IdempotencyKey"   TEXT NOT NULL UNIQUE,                   -- {tenantId}:{period}:{eventName}
  "ReportedToStripe" BOOLEAN NOT NULL DEFAULT false,
  "StripeEventId"    TEXT,
  "AttemptCount"     INTEGER NOT NULL DEFAULT 0,
  "LastAttemptAt"    TIMESTAMPTZ,
  "CreatedAt"        TIMESTAMPTZ NOT NULL
);

-- 35-8: invoice mirror (PDF bytes always served by Stripe, not Tamma)
CREATE TABLE billing_invoices (
  "Id"                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "TenantId"          UUID NOT NULL REFERENCES tenants("Id") ON DELETE CASCADE,
  "StripeInvoiceId"   TEXT UNIQUE,
  "Status"            TEXT NOT NULL,                          -- draft|open|paid|uncollectible|void
  "AmountDueUsd"      NUMERIC(20,4),
  "AmountPaidUsd"     NUMERIC(20,4),
  "PeriodStart"       TIMESTAMPTZ, "PeriodEnd" TIMESTAMPTZ,
  "HostedInvoiceUrl"  TEXT, "PdfUrl" TEXT,
  "AttemptCount"      INTEGER NOT NULL DEFAULT 0,             -- dunning
  "CreatedAt"         TIMESTAMPTZ NOT NULL,
  "UpdatedAt"         TIMESTAMPTZ NOT NULL
);

-- 35-5: webhook idempotency + raw projection log
CREATE TABLE billing_webhook_events (
  "Id"             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "StripeEventId"  TEXT NOT NULL UNIQUE,                      -- exactly-once guard
  "TenantId"       UUID,                                      -- resolved from customer mapping
  "EventType"      TEXT NOT NULL,
  "Payload"        JSONB NOT NULL,
  "ProcessedAt"    TIMESTAMPTZ
);

-- 35-10: prepaid credits wallet ledger (append-only; balance is derived)
CREATE TABLE billing_wallet_ledger (
  "Id"             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  "TenantId"       UUID NOT NULL REFERENCES tenants("Id") ON DELETE CASCADE,
  "EntryType"      TEXT NOT NULL,                             -- topup|consume|refund|adjust|promo
  "AmountUsd"      NUMERIC(20,4) NOT NULL,                    -- signed
  "StripeRef"      TEXT,                                      -- PaymentIntent / charge id
  "Description"    TEXT,
  "CreatedAt"      TIMESTAMPTZ NOT NULL
);
```

> EF Core migrations land under `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/`;
> `dotnet ef migrations has-pending-model-changes` must report none after each migration.

## Implementation Phases

### Phase 1: Stripe Foundation & Billing Mode (35-1, 35-2)
Stripe.net registration, cabinet-sourced keys, `BillingCustomer` mapping on tenant-create, the
`billing_plan_prices` catalog + `seed-billing` command, three Billing Meters, and BYOK-vs-platform
mode resolution from the Epic 29 cabinet. **Est. 7-9 days.**

### Phase 2: Subscriptions, Webhooks & Metering (35-4, 35-5, 35-3)
Subscription lifecycle (create/trial/upgrade/downgrade/cancel/proration), hardened idempotent webhook
ingestion + event projection, and BYOK-aware usage metering (rollup → buffered meter events →
batch flush → reconciliation). **Est. 13-16 days.**

### Phase 3: Enforcement, Invoicing & Dunning (35-6, 35-8)
Pre-dispatch quota enforcement (BYOK exempt from token quotas) and the invoice mirror + failed-payment
dunning ladder (`past_due` → `grace` → `suspended`) with recovery. **Est. 7-9 days.**

### Phase 4: Tax, Portal, Payment Methods & Wallet (35-9, 35-7, 35-10)
Stripe Tax / VAT calculation and tax-id collection, self-service Stripe Billing Portal + payment-method
mirror, and the prepaid credits wallet ledger with top-up. **Est. 10-13 days.**

### Phase 5: Dashboards & Analytics (35-11, 35-12)
Tenant billing area (`dashboard-user`) + admin billing console (`dashboard`), and billing audit,
Stripe↔local reconciliation, and revenue analytics. **Est. 8-10 days.**

## Success Metrics

- 100% of SaaS tenants have exactly one `BillingCustomer` row (unique `TenantId`) within 1 minute of
  signup; `seed-billing` is idempotent (second run = 0 Stripe create calls).
- Single-user boot makes **0 Stripe SDK calls** (asserted by tests via `NullBillingProvider`).
- Platform-provided usage meter events delivered to Stripe within 120 seconds of the flush cycle;
  **0 BYOK token meter events** ever emitted (asserted).
- Quota enforcement blocks over-quota dispatch within 500ms (p99); BYOK tenants are never blocked on
  token quotas.
- Webhook processing is exactly-once (redelivery is a no-op) with p95 latency < 2s.
- Local↔Stripe usage/revenue reconciliation drift stays within tolerance; any drift raises
  `BILLING.USAGE.RECONCILIATION_MISMATCH` (zero silent revenue loss).
- Dunning recovers failed payments through the `past_due`/`grace`/`suspended` ladder with full DCB
  audit; tenant and admin billing dashboards load within 1 second (p95).

## Reference Documents

- Epic 34 (Pricing, Plans & Entitlements): `docs/stories/epic-34/README.md`
- Epic 35 stories: `docs/stories/epic-35/story-35-1/` … `story-35-12/`
- [Stripe Billing Meters API](https://docs.stripe.com/api/billing/meter)
- [Stripe Meter Events API](https://docs.stripe.com/api/billing/meter-event)
- [Stripe Subscriptions](https://docs.stripe.com/billing/subscriptions/overview)
- [Stripe Customer / Billing Portal](https://docs.stripe.com/customer-management/integrate-customer-portal)
- [Stripe Webhooks](https://docs.stripe.com/webhooks)
- [Stripe Tax](https://docs.stripe.com/tax)
- [Stripe.net (.NET SDK)](https://github.com/stripe/stripe-dotnet)

---

**Last Updated**: 2026-06-17
