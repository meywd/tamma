---
title: "Story 20-2: Subscription Management"
sidebar:
  order: 200
---

Status: planned

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **Tamma SaaS user**,
I want to subscribe to a plan, upgrade, downgrade, or cancel my subscription through a self-service flow,
So that I can choose and manage the billing plan that fits my usage without contacting support.

## Priority

P0 - Required for monetization

## Acceptance Criteria

1. A `POST /api/v1/billing/checkout` endpoint creates a Stripe Checkout Session for the requested plan and returns the session URL; the user is redirected to Stripe's hosted checkout page
2. After successful checkout, Stripe redirects the user to `{DASHBOARD_URL}/billing?session_id={CHECKOUT_SESSION_ID}` where the dashboard confirms the subscription
3. A `POST /api/v1/billing/portal` endpoint creates a Stripe Customer Portal Session and returns the URL; the portal allows users to update payment methods, view invoices, and cancel subscriptions
4. A `POST /api/stripe/webhooks` endpoint receives and verifies Stripe webhook events using `stripe.webhooks.constructEvent()` with the raw request body and signing secret
5. The webhook handler processes these events and updates local state:
   - `customer.subscription.created` -- sets plan + limits on the installation
   - `customer.subscription.updated` -- handles upgrade/downgrade, updates plan + limits
   - `customer.subscription.deleted` -- reverts to Free plan and limits
   - `invoice.payment_succeeded` -- logs success, clears any payment failure flags
   - `invoice.payment_failed` -- sets `payment_failed` flag, emits alert event
   - `customer.subscription.trial_will_end` -- emits notification event (3 days before trial ends)
6. All webhook events are stored in the `billing_events` table with `stripe_event_id` for idempotent processing (duplicate events are acknowledged but not re-processed)
7. Plan changes (upgrade/downgrade) use Stripe's proration behavior: upgrades are prorated immediately, downgrades take effect at the end of the current billing period
8. A `GET /api/v1/billing/subscription` endpoint returns the current subscription status including plan name, current period start/end, payment status, and next invoice date
9. Domain events are emitted: `BILLING.SUBSCRIPTION.CREATED`, `BILLING.SUBSCRIPTION.UPDATED`, `BILLING.SUBSCRIPTION.CANCELLED`, `BILLING.PAYMENT.SUCCEEDED`, `BILLING.PAYMENT.FAILED`
10. Free plan users do not have a Stripe Subscription (they only have a Stripe Customer); upgrading from Free creates a new subscription
11. All billing API endpoints require JWT authentication (existing auth middleware) and verify the user has `billing:manage` permission
12. Unit tests cover: checkout session creation, portal session creation, all webhook event types, idempotent event processing, plan change proration logic
13. Integration tests (require `STRIPE_SECRET_KEY_TEST`): create checkout session, simulate webhook delivery with test clock

## Technical Design

### API Routes

```
packages/api/src/routes/billing/
  index.ts                    # Route registration
  checkout.ts                 # POST /api/v1/billing/checkout
  portal.ts                   # POST /api/v1/billing/portal
  subscription.ts             # GET  /api/v1/billing/subscription
  stripe-webhook.ts           # POST /api/stripe/webhooks
  __tests__/
    checkout.test.ts
    portal.test.ts
    subscription.test.ts
    stripe-webhook.test.ts
```

### Checkout Session Creation

```typescript
// POST /api/v1/billing/checkout
// Body: { plan: 'pro' | 'enterprise' }
// Returns: { url: string }

app.post('/api/v1/billing/checkout', async (request, reply) => {
  const { plan } = request.body as { plan: PlanName };
  const user = request.user; // from JWT auth middleware

  const installation = await installationStore.get(user.installationId);
  if (!installation?.stripe_customer_id) {
    return reply.status(400).send({ error: 'No billing account found' });
  }

  // Resolve price ID from billing_config
  const priceId = await billingService.getPriceId(plan);

  const session = await stripe.checkout.sessions.create({
    customer: installation.stripe_customer_id,
    mode: 'subscription',
    line_items: [{ price: priceId, quantity: 1 }],
    success_url: `${DASHBOARD_URL}/billing?session_id={CHECKOUT_SESSION_ID}`,
    cancel_url: `${DASHBOARD_URL}/billing?cancelled=true`,
    subscription_data: {
      metadata: { installation_id: user.installationId },
    },
    // For metered items on Pro plan, add usage-based line items
    ...(plan === 'pro' ? {
      line_items: [
        { price: priceId, quantity: 1 },
        { price: await billingService.getPriceId('pro_workflow_overage') },
        { price: await billingService.getPriceId('pro_token_overage') },
        { price: await billingService.getPriceId('pro_repo_overage') },
      ],
    } : {}),
  });

  return reply.send({ url: session.url });
});
```

### Webhook Handler

```typescript
// POST /api/stripe/webhooks
// IMPORTANT: Must receive raw body, not parsed JSON

app.post('/api/stripe/webhooks', {
  config: { rawBody: true },
}, async (request, reply) => {
  const sig = request.headers['stripe-signature'] as string;
  let event: Stripe.Event;

  try {
    event = stripe.webhooks.constructEvent(
      request.rawBody,
      sig,
      STRIPE_WEBHOOK_SECRET,
    );
  } catch (err) {
    logger.warn('Webhook signature verification failed', { error: err });
    return reply.status(400).send({ error: 'Invalid signature' });
  }

  // Idempotency check
  const existing = await pool.query(
    'SELECT id FROM billing_events WHERE stripe_event_id = $1',
    [event.id],
  );
  if (existing.rows.length > 0) {
    return reply.send({ received: true, duplicate: true });
  }

  // Process event
  await webhookProcessor.handle(event);

  // Store event
  await pool.query(
    `INSERT INTO billing_events (installation_id, stripe_event_id, event_type, data)
     VALUES ($1, $2, $3, $4)`,
    [installationId, event.id, event.type, JSON.stringify(event.data)],
  );

  return reply.send({ received: true });
});
```

### Webhook Processor

```typescript
// packages/api/src/services/billing/webhook-processor.ts
export class WebhookProcessor {
  constructor(
    private billingService: BillingService,
    private pool: pg.Pool,
    private logger: ILogger,
  ) {}

  async handle(event: Stripe.Event): Promise<void> {
    switch (event.type) {
      case 'customer.subscription.created':
        await this.handleSubscriptionCreated(event.data.object as Stripe.Subscription);
        break;
      case 'customer.subscription.updated':
        await this.handleSubscriptionUpdated(event.data.object as Stripe.Subscription);
        break;
      case 'customer.subscription.deleted':
        await this.handleSubscriptionDeleted(event.data.object as Stripe.Subscription);
        break;
      case 'invoice.payment_succeeded':
        await this.handlePaymentSucceeded(event.data.object as Stripe.Invoice);
        break;
      case 'invoice.payment_failed':
        await this.handlePaymentFailed(event.data.object as Stripe.Invoice);
        break;
      case 'customer.subscription.trial_will_end':
        await this.handleTrialWillEnd(event.data.object as Stripe.Subscription);
        break;
      default:
        this.logger.debug('Unhandled webhook event type', { type: event.type });
    }
  }

  private async handleSubscriptionCreated(sub: Stripe.Subscription): Promise<void> {
    const installationId = sub.metadata['installation_id'];
    const plan = this.resolvePlanFromSubscription(sub);
    await this.billingService.updatePlan(installationId, plan);
    // Emit BILLING.SUBSCRIPTION.CREATED
  }

  private async handleSubscriptionUpdated(sub: Stripe.Subscription): Promise<void> {
    const installationId = sub.metadata['installation_id'];
    const plan = this.resolvePlanFromSubscription(sub);
    await this.billingService.updatePlan(installationId, plan);
    // Emit BILLING.SUBSCRIPTION.UPDATED
  }

  private async handleSubscriptionDeleted(sub: Stripe.Subscription): Promise<void> {
    const installationId = sub.metadata['installation_id'];
    await this.billingService.updatePlan(installationId, 'free');
    // Emit BILLING.SUBSCRIPTION.CANCELLED
  }

  private async handlePaymentFailed(invoice: Stripe.Invoice): Promise<void> {
    const customerId = typeof invoice.customer === 'string'
      ? invoice.customer
      : invoice.customer?.id;
    if (!customerId) return;
    await this.billingService.setPaymentFailed(customerId, true);
    // Emit BILLING.PAYMENT.FAILED
  }
}
```

### Fastify Raw Body Configuration

The Stripe webhook endpoint requires the raw (unparsed) request body for signature verification. Fastify needs to be configured to retain the raw body:

```typescript
// Register raw body content type parser for the webhook route
app.addContentTypeParser(
  'application/json',
  { parseAs: 'buffer' },
  (req, body, done) => {
    // Store raw body for webhook verification
    try {
      const json = JSON.parse(body.toString());
      (req as any).rawBody = body;
      done(null, json);
    } catch (err) {
      done(err as Error, undefined);
    }
  },
);
```

Alternatively, use `@fastify/raw-body` plugin which is cleaner and handles this automatically.

### Customer Portal Integration

```typescript
// POST /api/v1/billing/portal
// Returns: { url: string }

app.post('/api/v1/billing/portal', async (request, reply) => {
  const user = request.user;
  const installation = await installationStore.get(user.installationId);

  const session = await stripe.billingPortal.sessions.create({
    customer: installation.stripe_customer_id,
    return_url: `${DASHBOARD_URL}/billing`,
  });

  return reply.send({ url: session.url });
});
```

### Subscription Status Endpoint

```typescript
// GET /api/v1/billing/subscription
// Returns current plan, subscription details, usage summary

interface BillingSubscriptionResponse {
  plan: PlanName;
  status: 'active' | 'past_due' | 'canceled' | 'trialing' | 'none';
  currentPeriodStart: string;     // ISO 8601
  currentPeriodEnd: string;       // ISO 8601
  cancelAtPeriodEnd: boolean;
  paymentFailed: boolean;
  limits: PlanLimits;
  nextInvoiceDate: string | null;
}
```

## Dependencies

- **Prerequisite**: Story 20-1 (Stripe client, customer model, plan config)
- **Blocks**: Story 20-5 (billing dashboard needs subscription endpoints)
- **Related**: Story 20-3 (metering hooks into subscription line items for overages)

## Testing Strategy

1. **Unit tests**: Mock Stripe SDK for checkout session creation, portal session creation, all 6 webhook event handlers, idempotent event processing, signature verification failure
2. **Integration tests**: (require `STRIPE_SECRET_KEY_TEST`)
   - Create a checkout session and verify its URL structure
   - Use Stripe test clocks to simulate subscription lifecycle
   - Send test webhook events via `stripe trigger` CLI
3. **Webhook idempotency test**: Send the same event twice, verify only one `billing_events` row is created
4. **Auth test**: Verify endpoints reject unauthenticated requests and users without `billing:manage` permission

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `packages/api/src/routes/billing/index.ts` | Create |
| `packages/api/src/routes/billing/checkout.ts` | Create |
| `packages/api/src/routes/billing/portal.ts` | Create |
| `packages/api/src/routes/billing/subscription.ts` | Create |
| `packages/api/src/routes/billing/stripe-webhook.ts` | Create |
| `packages/api/src/services/billing/webhook-processor.ts` | Create |
| `packages/api/src/services/billing/webhook-processor.test.ts` | Create |
| `packages/api/src/routes/billing/__tests__/checkout.test.ts` | Create |
| `packages/api/src/routes/billing/__tests__/portal.test.ts` | Create |
| `packages/api/src/routes/billing/__tests__/subscription.test.ts` | Create |
| `packages/api/src/routes/billing/__tests__/stripe-webhook.test.ts` | Create |
| `packages/api/src/index.ts` | Modify (export billing routes) |
| `packages/api/src/serve.ts` | Modify (register billing routes, configure raw body) |
| `packages/api/src/auth/permissions.ts` | Modify (add `billing:manage` permission) |
| `database/migrations/20260328_002_add_billing_events.sql` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. Set up Stripe CLI for local webhook testing: `stripe listen --forward-to localhost:3100/api/stripe/webhooks`
4. Configured Stripe Customer Portal in the Stripe Dashboard (settings -> Customer portal)
5. Planned TDD approach (Red-Green-Refactor cycle)

### Stripe Webhook Testing

Use the Stripe CLI to forward webhook events to your local server during development:

```bash
# Install Stripe CLI
brew install stripe/stripe-cli/stripe

# Login
stripe login

# Forward events to local server
stripe listen --forward-to localhost:3100/api/stripe/webhooks

# Trigger specific events for testing
stripe trigger customer.subscription.created
stripe trigger invoice.payment_failed
```

### Proration Behavior

Stripe handles proration automatically when using their hosted checkout and customer portal. Key behaviors:
- **Upgrade**: Customer is charged the prorated difference immediately
- **Downgrade**: Credit is applied to the next invoice; the new plan takes effect at period end
- **Cancel**: Subscription remains active until the end of the current billing period (`cancel_at_period_end: true`)

### Raw Body for Webhook Verification

Stripe webhook signature verification requires the raw request body (before JSON parsing). Two approaches:
1. Use `@fastify/raw-body` plugin (recommended)
2. Custom content type parser that stores raw buffer

Choose approach 1 if the plugin is already a dependency or easily added; otherwise approach 2 works fine.

### RBAC Integration

Add `billing:manage` to the existing permission system in `packages/api/src/auth/permissions.ts`. This permission should be granted to the `admin` and `owner` roles. Regular `member` users can view billing status (`billing:read`) but cannot modify subscriptions.

## Logging Requirements

- **INFO**: Checkout session created (plan, installation_id), subscription created/updated/cancelled, payment succeeded/failed
- **DEBUG**: Webhook event received (event_id, type), portal session created, proration calculation
- **WARN**: Webhook signature verification failed (IP address), duplicate event received, payment retry scheduled
- **ERROR**: Stripe API failure, webhook processing error (with event_id for manual retry), database write failure
- **Structured context**: Include `{ installationId, customerId, plan, eventId, eventType, operation }` where applicable
- **Credential safety**: NEVER log `STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET`, or payment method details

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-03-28 | 1.0.0   | Initial story creation | Claude |
