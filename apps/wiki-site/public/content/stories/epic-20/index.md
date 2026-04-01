---
title: "Epic 20: Billing & Payments for Tamma SaaS"
---

## Overview

This epic introduces Stripe-based billing and payments to the Tamma SaaS platform, enabling monetization through tiered subscription plans with usage-based metering. The system tracks workflow runs, LLM tokens consumed, and connected repositories per tenant, enforces usage limits at the orchestrator level, and provides a billing dashboard for self-service plan management.

## Plans

| Plan | Monthly Price | Workflow Runs/mo | LLM Tokens/mo | Connected Repos | Support |
|------|--------------|------------------|----------------|-----------------|---------|
| **Free** | $0 | 50 | 500K | 3 | Community |
| **Pro** | $29 | 2,000 | 10M | 25 | Email (48h) |
| **Enterprise** | Custom | Unlimited | Custom | Unlimited | Dedicated SLA |

Overage pricing (Pro plan only):
- Workflow runs: $0.02/run beyond limit
- LLM tokens: $2.00/1M tokens beyond limit
- Additional repos: $1.50/repo/month beyond limit

## Stories

| Story | Title | Priority | Status | Est. Effort |
|-------|-------|----------|--------|-------------|
| 20-1 | Stripe Integration & Plan Model | P0 | Planned | 3-4 days |
| 20-2 | Subscription Management | P0 | Planned | 3-4 days |
| 20-3 | Usage Metering | P0 | Planned | 4-5 days |
| 20-4 | Usage Limits Enforcement | P0 | Planned | 3-4 days |
| 20-5 | Billing Dashboard | P1 | Planned | 4-5 days |

## Architecture

```
+-----------------------------------------------------------------------------+
|                    EPIC 20: BILLING & PAYMENTS                               |
+-----------------------------------------------------------------------------+
|                                                                             |
|  +-- LAYER 1: Stripe Foundation (20-1) --------------------------------+   |
|  |                                                                      |   |
|  |  +------------------+  +------------------+  +------------------+    |   |
|  |  | Stripe SDK Setup |  | Plan/Product     |  | Customer Create  |    |   |
|  |  | & Configuration  |  | Definitions      |  | on User Signup   |    |   |
|  |  +------------------+  +------------------+  +------------------+    |   |
|  +----------------------------------------------------------------------+   |
|                              |                                              |
|  +-- LAYER 2: Subscriptions (20-2) ------------------------------------+   |
|  |                              |                                       |   |
|  |  +------------------+  +------------------+  +------------------+    |   |
|  |  | Checkout Session |  | Upgrade/Down-    |  | Stripe Webhook   |    |   |
|  |  | & Portal         |  | grade/Cancel     |  | Handler          |    |   |
|  |  +------------------+  +------------------+  +------------------+    |   |
|  +----------------------------------------------------------------------+   |
|                              |                                              |
|  +-- LAYER 3: Metering (20-3) -----------------------------------------+   |
|  |                              |                                       |   |
|  |  +------------------+  +------------------+  +------------------+    |   |
|  |  | Event Capture    |  | Stripe Billing   |  | Usage Aggregation|    |   |
|  |  | (runs, tokens,   |  | Meters & Meter   |  | & Reporting      |    |   |
|  |  |  repos)          |  | Events           |  |                  |    |   |
|  |  +------------------+  +------------------+  +------------------+    |   |
|  +----------------------------------------------------------------------+   |
|                              |                                              |
|  +-- LAYER 4: Enforcement (20-4) --------------------------------------+   |
|  |                              |                                       |   |
|  |  +------------------+  +------------------+  +------------------+    |   |
|  |  | Pre-Dispatch     |  | Graceful Degrad- |  | Overage Billing  |    |   |
|  |  | Limit Check      |  | ation & Alerts   |  | (Pro plan)       |    |   |
|  |  +------------------+  +------------------+  +------------------+    |   |
|  +----------------------------------------------------------------------+   |
|                              |                                              |
|  +-- LAYER 5: Dashboard (20-5) ----------------------------------------+   |
|  |                              |                                       |   |
|  |  +------------------+  +------------------+  +------------------+    |   |
|  |  | Usage Charts &   |  | Plan Management  |  | Invoice History  |    |   |
|  |  | Current Period   |  | & Payment Method |  | & Receipts       |    |   |
|  |  +------------------+  +------------------+  +------------------+    |   |
|  +----------------------------------------------------------------------+   |
|                                                                             |
+-----------------------------------------------------------------------------+
```

## Key Technical Decisions

### Stripe SDK Version

Use `stripe` npm package v18+ (API version `2025-03-31.basil` or later). The v18 SDK drops legacy usage records APIs and requires all metered prices to be backed by Stripe Billing Meters.

### Metering Strategy

Use Stripe Billing Meters (v2 API) for all usage tracking:
- **`tamma.workflow_runs`** meter: counts completed workflow executions per tenant
- **`tamma.llm_tokens`** meter: sums LLM tokens consumed across all providers
- **`tamma.connected_repos`** meter: gauge of active repo connections (reported daily)

Meter events are batched locally and flushed to Stripe every 60 seconds to stay within the 1,000 events/second rate limit. For higher throughput, use Meter Event Streams (v2) which supports 10,000 events/second.

### Webhook Security

All Stripe webhooks are verified using `stripe.webhooks.constructEvent()` with the raw body and signing secret. The webhook endpoint is registered at `/api/stripe/webhooks` and handles:
- `customer.subscription.created`
- `customer.subscription.updated`
- `customer.subscription.deleted`
- `invoice.payment_succeeded`
- `invoice.payment_failed`
- `customer.subscription.trial_will_end`

### Tenant Isolation

Each Tamma installation (tenant) maps to exactly one Stripe Customer. The `stripe_customer_id` is stored in the `installations` table. Usage is tracked and enforced per-tenant, not per-user.

### Event Sourcing Integration

All billing events emit DCB domain events for audit trail:
- `BILLING.SUBSCRIPTION.CREATED`
- `BILLING.SUBSCRIPTION.UPDATED`
- `BILLING.USAGE.LIMIT_REACHED`
- `BILLING.PAYMENT.SUCCEEDED`
- `BILLING.PAYMENT.FAILED`

## Dependencies

### On Other Epics

- **Epic 1**: Installation/tenant model, user authentication, API framework (Fastify)
- **Epic 2**: Orchestrator dispatch loop (usage limit check injection point)
- **Epic 4**: Event sourcing infrastructure (DCB events for billing audit trail)
- **Epic 5**: Dashboard framework (billing dashboard extends existing portal)

### External Dependencies

- **`stripe`**: Stripe Node.js SDK v18+ (`^18.0.0`)
- **PostgreSQL**: Billing tables (`subscriptions`, `usage_records`, `billing_events`)
- **Stripe Dashboard**: Product/Price/Meter configuration (seeded via migration script)

## Database Schema

```sql
-- Stripe customer mapping (extends installations table)
ALTER TABLE installations ADD COLUMN stripe_customer_id TEXT UNIQUE;
ALTER TABLE installations ADD COLUMN plan TEXT NOT NULL DEFAULT 'free';
ALTER TABLE installations ADD COLUMN plan_limits JSONB NOT NULL DEFAULT '{}';

-- Local usage tracking (aggregated before reporting to Stripe)
CREATE TABLE usage_records (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  installation_id UUID NOT NULL REFERENCES installations(id),
  meter_name TEXT NOT NULL,           -- 'workflow_runs', 'llm_tokens', 'connected_repos'
  value BIGINT NOT NULL,
  period_start TIMESTAMPTZ NOT NULL,
  period_end TIMESTAMPTZ NOT NULL,
  reported_to_stripe BOOLEAN DEFAULT FALSE,
  stripe_event_id TEXT,
  created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_usage_records_lookup
  ON usage_records(installation_id, meter_name, period_start);

-- Billing events log (webhook receipts + local events)
CREATE TABLE billing_events (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  installation_id UUID NOT NULL REFERENCES installations(id),
  stripe_event_id TEXT UNIQUE,
  event_type TEXT NOT NULL,
  data JSONB NOT NULL,
  processed_at TIMESTAMPTZ DEFAULT NOW()
);
```

## Implementation Phases

### Phase 1: Foundation (Stories 20-1, 20-2) - Week 1
- Stripe SDK setup, plan model, customer creation
- Checkout flow, subscription CRUD, webhook handler
- Estimated: 6-8 days

### Phase 2: Metering & Enforcement (Stories 20-3, 20-4) - Week 2
- Usage capture hooks, Stripe meter integration, aggregation
- Pre-dispatch limit checks, graceful degradation, overage billing
- Estimated: 7-9 days

### Phase 3: Dashboard (Story 20-5) - Week 3
- Usage visualization, plan management UI, invoice history
- Estimated: 4-5 days

## Success Metrics

- 100% of tenants have a Stripe Customer record within 1 minute of signup
- Usage meter events delivered to Stripe within 120 seconds of occurrence
- Limit enforcement blocks dispatches within 500ms (p99)
- Webhook processing latency < 2 seconds (p95)
- Billing dashboard loads within 1 second (p95)
- Zero missed invoice events (verified via event sourcing reconciliation)

## Reference Documents

- [Stripe Billing Meters API](https://docs.stripe.com/api/billing/meter)
- [Stripe Meter Events API](https://docs.stripe.com/api/billing/meter-event)
- [Stripe Checkout Subscriptions](https://docs.stripe.com/payments/checkout/build-subscriptions)
- [Stripe Customer Portal](https://docs.stripe.com/customer-management/integrate-customer-portal)
- [Stripe Webhooks](https://docs.stripe.com/webhooks)
- [Stripe Node.js SDK v18 Migration](https://github.com/stripe/stripe-node/wiki/Migration-guide-for-v18)
- [Usage-Based Billing Implementation Guide](https://docs.stripe.com/billing/subscriptions/usage-based/implementation-guide)

---

**Last Updated**: 2026-03-28
**Epic Owner**: TBD
**Implementation Start**: TBD
**Total Estimated Effort**: 17-22 days
