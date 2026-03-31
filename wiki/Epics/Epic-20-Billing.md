# Epic 20: Billing & Payments

**Status:** Drafted
**Stories:** 5 (20-1 through 20-5)
**Estimated Effort:** 17-22 days

## Overview

Epic 20 introduces Stripe-based billing and payments to the Tamma SaaS platform, enabling monetization through tiered subscription plans with usage-based metering. The system tracks workflow runs, LLM tokens consumed, and connected repositories per tenant, enforces usage limits at the orchestrator level, and provides a billing dashboard for self-service plan management.

## Goals

1. Integrate Stripe SDK and define plan/product model
2. Implement subscription management (checkout, upgrade, downgrade, cancel)
3. Build usage metering with Stripe Billing Meters (workflow runs, LLM tokens, repos)
4. Enforce usage limits at the orchestrator dispatch level
5. Create billing dashboard with usage charts, plan management, and invoice history

## Plans

| Plan | Monthly Price | Workflow Runs/mo | LLM Tokens/mo | Connected Repos |
|------|--------------|------------------|----------------|-----------------|
| **Free** | $0 | 50 | 500K | 3 |
| **Pro** | $29 | 2,000 | 10M | 25 |
| **Enterprise** | Custom | Unlimited | Custom | Unlimited |

Overage pricing (Pro plan only):
- Workflow runs: $0.02/run beyond limit
- LLM tokens: $2.00/1M tokens beyond limit
- Additional repos: $1.50/repo/month beyond limit

## Stories

| Story | Title | Priority | Effort | Status |
|-------|-------|----------|--------|--------|
| 20-1 | Stripe Integration & Plan Model | P0 | 3-4 days | Planned |
| 20-2 | Subscription Management | P0 | 3-4 days | Planned |
| 20-3 | Usage Metering | P0 | 4-5 days | Planned |
| 20-4 | Usage Limits Enforcement | P0 | 3-4 days | Planned |
| 20-5 | Billing Dashboard | P1 | 4-5 days | Planned |

## Key Technical Details

### Stripe SDK

Use `stripe` npm package v18+ (API version `2025-03-31.basil` or later). The v18 SDK requires all metered prices to be backed by Stripe Billing Meters.

### Metering Strategy

Uses Stripe Billing Meters (v2 API):
- `tamma.workflow_runs`: counts completed workflow executions per tenant
- `tamma.llm_tokens`: sums LLM tokens consumed across all providers
- `tamma.connected_repos`: gauge of active repo connections (reported daily)

Meter events batched locally and flushed to Stripe every 60 seconds.

### Webhook Events Handled

- `customer.subscription.created` / `updated` / `deleted`
- `invoice.payment_succeeded` / `payment_failed`
- `customer.subscription.trial_will_end`

### Database Schema Additions

- `installations` table gains `stripe_customer_id`, `plan`, `plan_limits` columns
- New `usage_records` table for local aggregation before Stripe reporting
- New `billing_events` table for webhook receipts and local events

### Event Sourcing Integration

All billing events emit DCB domain events:
- `BILLING.SUBSCRIPTION.CREATED` / `UPDATED`
- `BILLING.USAGE.LIMIT_REACHED`
- `BILLING.PAYMENT.SUCCEEDED` / `FAILED`

### Implementation Phases

| Phase | Stories | Estimated |
|-------|---------|-----------|
| Phase 1: Foundation | 20-1, 20-2 | 6-8 days |
| Phase 2: Metering & Enforcement | 20-3, 20-4 | 7-9 days |
| Phase 3: Dashboard | 20-5 | 4-5 days |

### Success Metrics

- 100% of tenants have Stripe Customer record within 1 minute of signup
- Usage meter events delivered to Stripe within 120 seconds
- Limit enforcement blocks dispatches within 500ms (p99)
- Webhook processing latency < 2 seconds (p95)
- Zero missed invoice events

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| Installation/Tenant Model | Epic 1, 17 | Tenant maps to Stripe Customer |
| Orchestrator Dispatch | Epic 2 | Limit check injection point |
| Event Sourcing | Epic 4, 10 | Billing audit trail |
| Dashboard Framework | Epic 5 | Billing dashboard extends existing portal |
| User Auth | Epic 18 | Customer created on signup |

## Story Files

[Story documents on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-20)
