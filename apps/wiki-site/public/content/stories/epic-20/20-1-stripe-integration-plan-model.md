---
title: "Story 20-1: Stripe Integration & Plan Model"
sidebar:
  order: 200
---

Status: planned

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **platform operator**,
I want Stripe integrated as the billing provider with well-defined subscription plans,
So that every tenant is automatically associated with a Stripe Customer and assigned a plan from signup.

## Priority

P0 - Foundation for all billing functionality

## Acceptance Criteria

1. The `stripe` npm package (v18+) is installed and a `StripeClient` service wraps it with typed configuration, automatic retries, and idempotency keys for all mutating calls
2. Three plans are defined as Stripe Products with corresponding Prices: Free ($0/mo flat), Pro ($29/mo flat + metered overages), Enterprise (custom, quote-based)
3. A seed/migration script (`scripts/stripe-seed.ts`) creates or updates Products, Prices, and Billing Meters in Stripe via the API, storing resulting IDs in a `billing_config` table for runtime lookup
4. On user signup (GitHub OAuth callback), a Stripe Customer is created with `email`, `name`, and `metadata.installation_id`, and the `stripe_customer_id` is persisted on the `installations` row
5. The `installations` table is extended with `stripe_customer_id TEXT UNIQUE`, `plan TEXT NOT NULL DEFAULT 'free'`, and `plan_limits JSONB NOT NULL DEFAULT '{}'`
6. A `BillingService` class in `packages/api/src/services/billing/` provides methods: `createCustomer()`, `getCustomer()`, `getPlanLimits()`, `syncPlanFromStripe()`
7. Plan limits are defined as a typed constant map and written to `plan_limits` JSONB on subscription changes:
   - Free: `{ workflow_runs: 50, llm_tokens: 500000, connected_repos: 3 }`
   - Pro: `{ workflow_runs: 2000, llm_tokens: 10000000, connected_repos: 25 }`
   - Enterprise: `{ workflow_runs: -1, llm_tokens: -1, connected_repos: -1 }` (-1 = unlimited)
8. All Stripe API calls are wrapped in the existing retry-with-backoff pattern with circuit breaker (5 failures in 60s opens for 300s)
9. Environment variables `STRIPE_SECRET_KEY` and `STRIPE_WEBHOOK_SECRET` are required; the app logs a clear warning and disables billing routes if missing
10. Domain events `BILLING.CUSTOMER.CREATED` and `BILLING.PLAN.ASSIGNED` are emitted for audit trail
11. Unit tests cover: customer creation, plan limit resolution, idempotent customer creation (duplicate signup), missing Stripe config graceful degradation
12. Integration test (requires `STRIPE_SECRET_KEY_TEST`) creates a real test customer and verifies it appears in Stripe

## Technical Design

### Package Structure

```
packages/api/src/services/billing/
  stripe-client.ts        # Configured Stripe SDK wrapper
  billing-service.ts      # Core billing operations
  plan-config.ts          # Plan definitions and limits
  billing.types.ts        # TypeScript interfaces
  billing-service.test.ts # Unit tests
  stripe-client.test.ts   # Unit tests
```

### Stripe Client Wrapper

```typescript
// packages/api/src/services/billing/stripe-client.ts
import Stripe from 'stripe';

export interface StripeClientConfig {
  secretKey: string;
  apiVersion?: string;   // defaults to '2025-03-31.basil'
  maxRetries?: number;    // defaults to 3
  timeout?: number;       // defaults to 30000ms
}

export function createStripeClient(config: StripeClientConfig): Stripe {
  return new Stripe(config.secretKey, {
    apiVersion: config.apiVersion ?? '2025-03-31.basil',
    maxNetworkRetries: config.maxRetries ?? 3,
    timeout: config.timeout ?? 30_000,
    typescript: true,
  });
}
```

### Plan Configuration

```typescript
// packages/api/src/services/billing/plan-config.ts
export const PLAN_NAMES = ['free', 'pro', 'enterprise'] as const;
export type PlanName = (typeof PLAN_NAMES)[number];

export interface PlanLimits {
  workflow_runs: number;    // -1 = unlimited
  llm_tokens: number;       // -1 = unlimited
  connected_repos: number;  // -1 = unlimited
}

export const PLAN_LIMITS: Record<PlanName, PlanLimits> = {
  free: { workflow_runs: 50, llm_tokens: 500_000, connected_repos: 3 },
  pro: { workflow_runs: 2_000, llm_tokens: 10_000_000, connected_repos: 25 },
  enterprise: { workflow_runs: -1, llm_tokens: -1, connected_repos: -1 },
};

export interface PlanDefinition {
  name: PlanName;
  displayName: string;
  stripePriceId: string;         // resolved at runtime from billing_config
  monthlyPriceCents: number;
  limits: PlanLimits;
  features: string[];
}
```

### BillingService

```typescript
// packages/api/src/services/billing/billing-service.ts
export class BillingService {
  constructor(
    private stripe: Stripe,
    private pool: pg.Pool,
    private logger: ILogger,
  ) {}

  async createCustomer(input: {
    installationId: string;
    email: string;
    name: string;
  }): Promise<Stripe.Customer> {
    // Idempotency: check if customer already exists
    const existing = await this.getCustomerByInstallation(input.installationId);
    if (existing) return existing;

    const customer = await this.stripe.customers.create({
      email: input.email,
      name: input.name,
      metadata: { installation_id: input.installationId },
    }, {
      idempotencyKey: `create-customer-${input.installationId}`,
    });

    await this.pool.query(
      `UPDATE installations
       SET stripe_customer_id = $1, plan = 'free', plan_limits = $2
       WHERE id = $3`,
      [customer.id, JSON.stringify(PLAN_LIMITS.free), input.installationId],
    );

    // Emit domain event
    // await eventStore.append({ type: 'BILLING.CUSTOMER.CREATED', ... });

    return customer;
  }

  async getPlanLimits(installationId: string): Promise<PlanLimits> { /* ... */ }
  async syncPlanFromStripe(customerId: string): Promise<void> { /* ... */ }
  async getCustomer(customerId: string): Promise<Stripe.Customer> { /* ... */ }
}
```

### Database Migration

```sql
-- migrations/20260328_001_add_billing_columns.sql
ALTER TABLE installations ADD COLUMN IF NOT EXISTS stripe_customer_id TEXT UNIQUE;
ALTER TABLE installations ADD COLUMN IF NOT EXISTS plan TEXT NOT NULL DEFAULT 'free';
ALTER TABLE installations ADD COLUMN IF NOT EXISTS plan_limits JSONB NOT NULL DEFAULT '{"workflow_runs":50,"llm_tokens":500000,"connected_repos":3}';

CREATE TABLE IF NOT EXISTS billing_config (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL,
  updated_at TIMESTAMPTZ DEFAULT NOW()
);
-- Stores: stripe_product_free, stripe_price_free, stripe_meter_workflow_runs, etc.
```

### Stripe Seed Script

```typescript
// scripts/stripe-seed.ts
// Creates Products, Prices, and Meters in Stripe
// Stores their IDs in billing_config table for runtime lookup
// Idempotent: checks for existing resources before creating
// Run: pnpm stripe:seed (development) or as part of deploy pipeline
```

### Integration with Signup Flow

The existing GitHub OAuth callback in `packages/api/src/routes/auth/github-oauth.ts` is extended: after `userStore.upsert()` succeeds, call `billingService.createCustomer()` to provision a Stripe Customer for the user's installation. This is a fire-and-forget call that logs errors but does not block login.

## Dependencies

- **Prerequisite**: Epic 1 (installation model, user auth, Fastify API)
- **Prerequisite**: PostgreSQL persistence layer (`pg` pool)
- **Blocks**: Story 20-2 (subscription management needs customer + plan model)
- **Blocks**: Story 20-3 (metering needs plan limits)

## Testing Strategy

1. **Unit tests**: Mock `stripe` SDK, verify customer creation flow, plan limit resolution, idempotency guard, graceful degradation when Stripe is unconfigured
2. **Integration tests**: (require `STRIPE_SECRET_KEY_TEST`) Create test customer, verify in Stripe, clean up
3. **Migration test**: Verify `ALTER TABLE` is idempotent (run twice)
4. **Seed script test**: Verify Products/Prices/Meters are created or found existing

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `packages/api/src/services/billing/stripe-client.ts` | Create |
| `packages/api/src/services/billing/billing-service.ts` | Create |
| `packages/api/src/services/billing/plan-config.ts` | Create |
| `packages/api/src/services/billing/billing.types.ts` | Create |
| `packages/api/src/services/billing/index.ts` | Create |
| `packages/api/src/services/billing/billing-service.test.ts` | Create |
| `packages/api/src/services/billing/stripe-client.test.ts` | Create |
| `database/migrations/20260328_001_add_billing_columns.sql` | Create |
| `scripts/stripe-seed.ts` | Create |
| `packages/api/src/routes/auth/github-oauth.ts` | Modify (add customer creation) |
| `packages/api/src/serve.ts` | Modify (pass billing config) |
| `packages/api/src/index.ts` | Modify (export billing types) |
| `packages/api/package.json` | Modify (add `stripe` dependency) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. Reviewed the Stripe Node.js SDK v18 migration guide
4. Set up a Stripe test account with test API keys
5. Planned TDD approach (Red-Green-Refactor cycle)

### Stripe SDK v18 Notes

- v18 uses API version `2025-03-31.basil` -- legacy usage records APIs are removed
- All metered prices must be backed by Billing Meters (not the old `usage_records` endpoint)
- Constructor: `new Stripe(secretKey, { apiVersion: '2025-03-31.basil' })`
- The SDK has built-in `maxNetworkRetries` -- no need for our own retry wrapper on the HTTP level

### Security

- `STRIPE_SECRET_KEY` must never appear in logs, error messages, or API responses
- Use `STRIPE_SECRET_KEY_TEST` (prefixed `sk_test_`) for all non-production environments
- The Stripe customer ID is safe to store and log (it is not a secret)

## Logging Requirements

- **INFO**: Customer created (installation_id, customer_id), plan assigned, seed script completed
- **DEBUG**: Stripe API call details (endpoint, duration), plan limit resolution
- **WARN**: Stripe config missing (billing disabled), duplicate customer creation attempt
- **ERROR**: Stripe API failure after retries, database migration failure, seed script error
- **Structured context**: Include `{ installationId, customerId, plan, operation, duration }` where applicable
- **Credential safety**: NEVER log `STRIPE_SECRET_KEY` or `STRIPE_WEBHOOK_SECRET`

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-03-28 | 1.0.0   | Initial story creation | Claude |
