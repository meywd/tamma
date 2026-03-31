---
title: "Story 21.2: Pricing Page + Stripe Checkout Integration"
sidebar:
  order: 210
---

Status: planned

## Story

As a **potential customer evaluating Tamma**,
I want to see a clear pricing page with plan comparison, FAQ, and the ability to subscribe via Stripe checkout,
so that I can choose the right plan and start paying without friction.

## Acceptance Criteria

1. A pricing page is accessible at `tamma.dev/pricing` with a plan comparison table showing at least 3 tiers (Free, Pro, Enterprise)
2. Each plan tier displays: name, monthly/annual price, feature list with checkmarks/crosses, usage limits (repos, runs/month, AI tokens), and a CTA button
3. A monthly/annual toggle switches prices and shows the annual discount percentage (e.g., "Save 20%")
4. The Free tier CTA links to the GitHub App install flow or sign-up page
5. The Pro tier CTA initiates a Stripe Checkout session (redirect to Stripe-hosted checkout page)
6. The Enterprise tier CTA opens a "Contact Sales" form or mailto link
7. A FAQ section below the pricing table answers at least 8 common questions (billing cycle, cancellation, team seats, overage, refund policy, data retention, self-hosted vs SaaS, open-source vs paid)
8. The pricing page is fully responsive and supports dark mode consistent with the landing page
9. Stripe Checkout integration uses a Cloudflare Pages Function (`functions/api/checkout.ts`) that creates a Stripe Checkout Session with the selected plan's `price_id`
10. Stripe webhook handling exists at `api.tamma.dev/webhooks/stripe` (or a Cloudflare Function) to process `checkout.session.completed` and `customer.subscription.*` events
11. The page includes structured data (JSON-LD) for pricing/offers to improve search visibility
12. Plan feature data is defined in a single source-of-truth configuration file (not hardcoded in HTML)

## Technical Context

### Pricing Tiers (Initial)

| Feature | Free | Pro ($29/mo) | Enterprise (Custom) |
|---------|------|-------------|---------------------|
| Connected repos | 3 | Unlimited | Unlimited |
| Workflow runs / month | 50 | 2,000 | Unlimited |
| AI providers | 2 | All 8+ | All 8+ + custom |
| Git platforms | GitHub only | All 7 | All 7 + on-prem |
| Team members | 1 | 10 | Unlimited |
| Audit trail retention | 7 days | 90 days | Unlimited |
| Priority support | — | Email | Dedicated Slack + SLA |
| Self-hosted option | — | — | Yes |
| SSO / SAML | — | — | Yes |

Prices are illustrative and stored in a config file that can be updated without code changes.

### Stripe Integration Architecture

```
tamma.dev/pricing
  |
  | (user clicks "Subscribe to Pro")
  |
  v
Cloudflare Pages Function: /api/checkout
  |
  | stripe.checkout.sessions.create({
  |   mode: 'subscription',
  |   line_items: [{ price: 'price_xxx', quantity: 1 }],
  |   success_url: 'https://app.tamma.dev/user/billing?session_id={CHECKOUT_SESSION_ID}',
  |   cancel_url: 'https://tamma.dev/pricing',
  | })
  |
  v
Stripe Checkout (hosted page)
  |
  | (user completes payment)
  |
  v
Stripe Webhook --> api.tamma.dev/webhooks/stripe
  |
  | Event: checkout.session.completed
  | --> Create/update subscription in PostgreSQL
  | --> Emit BILLING.SUBSCRIPTION.CREATED event
  |
  v
User redirected to app.tamma.dev/user/billing?session_id=...
```

### Files to Create

| File | Purpose |
|------|---------|
| `apps/marketing-site/src/pages/pricing.astro` | Pricing page layout |
| `apps/marketing-site/src/components/PricingTable.astro` | Plan comparison table component |
| `apps/marketing-site/src/components/PricingToggle.astro` | Monthly/annual toggle (interactive island) |
| `apps/marketing-site/src/components/PricingFAQ.astro` | FAQ accordion component |
| `apps/marketing-site/src/data/pricing.ts` | Plan definitions (single source of truth) |
| `apps/marketing-site/functions/api/checkout.ts` | Cloudflare Pages Function for Stripe Checkout session creation |

### Files to Modify

| File | Change |
|------|--------|
| `apps/marketing-site/src/components/Header.astro` | Add "Pricing" link to navigation |
| `apps/marketing-site/src/components/Footer.astro` | Add "Pricing" link to footer nav |
| `apps/marketing-site/package.json` | Add `stripe` dependency |
| `apps/marketing-site/wrangler.toml` | Add `STRIPE_SECRET_KEY` and `STRIPE_WEBHOOK_SECRET` as secrets |

### API-Side Webhook Handler (Tamma API)

The Stripe webhook endpoint on the Tamma API processes subscription lifecycle events. This may already exist or need to be created in `packages/api/`:

| Stripe Event | Action |
|-------------|--------|
| `checkout.session.completed` | Create subscription record, associate with user, emit `BILLING.SUBSCRIPTION.CREATED` |
| `customer.subscription.updated` | Update plan/status, emit `BILLING.SUBSCRIPTION.UPDATED` |
| `customer.subscription.deleted` | Mark subscription cancelled, emit `BILLING.SUBSCRIPTION.CANCELLED` |
| `invoice.payment_failed` | Flag account, send notification, emit `BILLING.PAYMENT.FAILED` |

### Pricing Data Source of Truth

```typescript
// apps/marketing-site/src/data/pricing.ts
export interface PricingPlan {
  id: string;
  name: string;
  description: string;
  monthlyPrice: number | null;   // null = custom/contact
  annualPrice: number | null;
  stripePriceIdMonthly: string | null;
  stripePriceIdAnnual: string | null;
  features: PlanFeature[];
  cta: {
    label: string;
    href: string;
    variant: 'primary' | 'secondary' | 'outline';
  };
  highlighted: boolean;           // visually emphasize (e.g., "Most Popular")
}

export interface PlanFeature {
  name: string;
  included: boolean | string;     // true/false or a specific value like "10 seats"
  tooltip?: string;
}

export const plans: PricingPlan[] = [
  { id: 'free', name: 'Free', /* ... */ },
  { id: 'pro', name: 'Pro', /* ... */ },
  { id: 'enterprise', name: 'Enterprise', /* ... */ },
];
```

### Stripe Checkout Function

```typescript
// apps/marketing-site/functions/api/checkout.ts
import Stripe from 'stripe';

interface Env {
  STRIPE_SECRET_KEY: string;
}

export const onRequestPost: PagesFunction<Env> = async (context) => {
  const stripe = new Stripe(context.env.STRIPE_SECRET_KEY);
  const { priceId, interval } = await context.request.json();

  const session = await stripe.checkout.sessions.create({
    mode: 'subscription',
    line_items: [{ price: priceId, quantity: 1 }],
    success_url: 'https://app.tamma.dev/user/billing?session_id={CHECKOUT_SESSION_ID}',
    cancel_url: 'https://tamma.dev/pricing',
    allow_promotion_codes: true,
  });

  return Response.json({ url: session.url });
};
```

## Implementation Notes

- **Stripe test mode**: Use Stripe test keys during development. Store `STRIPE_SECRET_KEY` as a Cloudflare secret, never in code or `.env` files committed to git.
- **Price IDs**: Create products and prices in the Stripe Dashboard first. Store the `price_xxx` IDs in the pricing data file. These are not secrets.
- **Annual discount**: Calculate and display the savings percentage dynamically from the pricing data (e.g., `Math.round((1 - annualPrice / (monthlyPrice * 12)) * 100)`).
- **FAQ content**: Write FAQ answers in a data file or MDX collection for easy editing.
- **Accessibility**: The pricing toggle must be a proper toggle button with `aria-pressed`. The FAQ accordion must use `<details>`/`<summary>` or proper ARIA disclosure pattern.
- **No client-side Stripe.js needed**: We use Stripe Checkout (redirect mode), which is entirely server-side session creation followed by a redirect. No need for Stripe Elements on the pricing page.
- **Webhook security**: Always verify the Stripe webhook signature using `stripe.webhooks.constructEvent()` with `STRIPE_WEBHOOK_SECRET`.

## Dependencies

- **Story 21.1** (Marketing Landing Page) — provides the Astro project, layout, header, footer
- **Epic 16** (Auth) — Stripe webhook handler on `api.tamma.dev` needs authenticated user context for subscription association

## Estimated Effort

**24 hours**

| Task | Hours |
|------|-------|
| Pricing data model + configuration file | 2 |
| PricingTable component (responsive, dark mode) | 6 |
| Monthly/annual toggle island | 2 |
| FAQ accordion component + content | 3 |
| Stripe Checkout Pages Function | 4 |
| Stripe webhook handler (API side) | 4 |
| SEO structured data + meta tags | 1 |
| Testing (Stripe test mode, responsive, a11y) | 2 |

---

**Last Updated**: 2026-03-28
