# Story 20-5: Billing Dashboard

Status: planned

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **Tamma SaaS user**,
I want a billing dashboard in the user portal that shows my usage, current plan, invoices, and allows me to manage my subscription,
So that I have full visibility into my billing status and can self-service all plan changes.

## Priority

P1 - Required for production readiness (users need billing visibility)

## Acceptance Criteria

1. A `/billing` page in the React dashboard (`packages/dashboard/`) displays the billing overview with four sections: Plan Summary, Usage Charts, Invoice History, and Payment Method
2. The Plan Summary section shows: current plan name and price, billing period (start/end), subscription status (active, past_due, trialing, canceled), and a "Change Plan" button that opens Stripe Checkout (upgrade) or Stripe Portal (downgrade/cancel)
3. The Usage Charts section displays three progress bars (or gauge charts) for: workflow runs used/limit, LLM tokens used/limit, connected repos used/limit -- each with percentage and raw numbers; for unlimited (Enterprise), the bar shows "Unlimited" instead of a fill percentage
4. Usage data refreshes every 60 seconds via polling `GET /api/v1/billing/usage` and `GET /api/v1/billing/quota`
5. An overage banner is displayed when a Pro-tier tenant exceeds their base allocation, showing the estimated overage cost for the current period
6. The Invoice History section lists the last 12 invoices with: date, amount, status (paid, open, void, uncollectible), and a "Download PDF" link that opens the Stripe-hosted invoice PDF
7. A `GET /api/v1/billing/invoices` endpoint returns the last 12 invoices for the authenticated tenant, fetched from Stripe's API with `stripe.invoices.list({ customer, limit: 12 })`
8. The Payment Method section shows the last 4 digits and brand of the current payment method (card); a "Update" button redirects to the Stripe Customer Portal for payment method changes
9. When no subscription exists (Free plan), the page shows a prominent "Upgrade to Pro" call-to-action card with feature comparison and pricing
10. The billing page handles error states gracefully: Stripe unreachable shows "Billing information temporarily unavailable" with a retry button; loading states show skeleton placeholders
11. All billing dashboard components are accessible (WCAG 2.1 AA): proper ARIA labels, keyboard navigation, color-contrast compliant progress bars
12. Unit tests cover: component rendering for all plan states (free, pro, enterprise), loading states, error states, usage bar percentage calculations, overage banner visibility logic
13. E2E test: navigate to `/billing`, verify plan summary loads, verify usage bars render, verify invoice list is accessible

## Technical Design

### Dashboard Components

```
packages/dashboard/src/pages/billing/
  BillingPage.tsx                   # Main billing page layout
  PlanSummary.tsx                   # Current plan card
  UsageCharts.tsx                   # Usage progress bars
  InvoiceHistory.tsx                # Invoice list table
  PaymentMethod.tsx                 # Payment method card
  UpgradePrompt.tsx                 # Free-tier upgrade CTA
  OverageBanner.tsx                 # Pro-tier overage alert
  billing.hooks.ts                  # React hooks for billing data
  billing.types.ts                  # TypeScript types
  __tests__/
    BillingPage.test.tsx
    PlanSummary.test.tsx
    UsageCharts.test.tsx
    InvoiceHistory.test.tsx
```

### BillingPage Layout

```tsx
// packages/dashboard/src/pages/billing/BillingPage.tsx
export function BillingPage(): React.ReactElement {
  const { quota, isLoading: quotaLoading, error: quotaError } = useQuota();
  const { usage, isLoading: usageLoading } = useUsage();
  const { invoices, isLoading: invoicesLoading } = useInvoices();

  if (quotaError) {
    return <BillingErrorState onRetry={() => void refetch()} />;
  }

  return (
    <div className="billing-page">
      <h1>Billing & Usage</h1>

      {quota?.overage_active && <OverageBanner quota={quota} />}

      <div className="billing-grid">
        <PlanSummary
          plan={quota?.plan}
          status={subscription?.status}
          periodEnd={subscription?.currentPeriodEnd}
          isLoading={quotaLoading}
        />

        <UsageCharts
          usage={usage}
          limits={quota?.limits}
          isLoading={usageLoading}
        />
      </div>

      {quota?.plan === 'free' && <UpgradePrompt />}

      <InvoiceHistory invoices={invoices} isLoading={invoicesLoading} />

      <PaymentMethod
        plan={quota?.plan}
        hasSubscription={!!subscription}
      />
    </div>
  );
}
```

### Usage Progress Bars

```tsx
// packages/dashboard/src/pages/billing/UsageCharts.tsx
interface UsageBarProps {
  label: string;
  used: number;
  limit: number;          // -1 = unlimited
  unit: string;
  formatValue?: (n: number) => string;
}

function UsageBar({ label, used, limit, unit, formatValue }: UsageBarProps): React.ReactElement {
  const isUnlimited = limit === -1;
  const percentage = isUnlimited ? 0 : Math.min(100, (used / limit) * 100);
  const isWarning = percentage >= 80 && percentage < 100;
  const isDanger = percentage >= 100;

  const format = formatValue ?? ((n: number) => n.toLocaleString());

  return (
    <div className="usage-bar" role="meter" aria-valuenow={used} aria-valuemin={0} aria-valuemax={limit === -1 ? undefined : limit} aria-label={`${label} usage`}>
      <div className="usage-bar__header">
        <span className="usage-bar__label">{label}</span>
        <span className="usage-bar__value">
          {isUnlimited
            ? `${format(used)} ${unit} (Unlimited)`
            : `${format(used)} / ${format(limit)} ${unit}`}
        </span>
      </div>
      <div className="usage-bar__track">
        <div
          className={`usage-bar__fill ${isWarning ? 'usage-bar__fill--warning' : ''} ${isDanger ? 'usage-bar__fill--danger' : ''}`}
          style={{ width: isUnlimited ? '0%' : `${percentage}%` }}
        />
      </div>
      {isDanger && !isUnlimited && (
        <span className="usage-bar__overage" role="alert">
          Overage: {format(used - limit)} {unit} over limit
        </span>
      )}
    </div>
  );
}

export function UsageCharts({ usage, limits, isLoading }: UsageChartsProps): React.ReactElement {
  if (isLoading) return <UsageChartsSkeleton />;

  return (
    <div className="usage-charts">
      <h2>Current Period Usage</h2>
      <UsageBar
        label="Workflow Runs"
        used={usage.workflow_runs}
        limit={limits.workflow_runs}
        unit="runs"
      />
      <UsageBar
        label="LLM Tokens"
        used={usage.llm_tokens}
        limit={limits.llm_tokens}
        unit="tokens"
        formatValue={(n) => {
          if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
          if (n >= 1_000) return `${(n / 1_000).toFixed(0)}K`;
          return n.toLocaleString();
        }}
      />
      <UsageBar
        label="Connected Repos"
        used={usage.connected_repos}
        limit={limits.connected_repos}
        unit="repos"
      />
    </div>
  );
}
```

### React Hooks

```typescript
// packages/dashboard/src/pages/billing/billing.hooks.ts
import { useQuery } from '@tanstack/react-query';

export function useQuota() {
  return useQuery({
    queryKey: ['billing', 'quota'],
    queryFn: () => api.get<QuotaSnapshot>('/api/v1/billing/quota'),
    refetchInterval: 60_000, // refresh every 60s
  });
}

export function useUsage() {
  return useQuery({
    queryKey: ['billing', 'usage'],
    queryFn: () => api.get<UsageSummary>('/api/v1/billing/usage'),
    refetchInterval: 60_000,
  });
}

export function useSubscription() {
  return useQuery({
    queryKey: ['billing', 'subscription'],
    queryFn: () => api.get<BillingSubscriptionResponse>('/api/v1/billing/subscription'),
    refetchInterval: 60_000,
  });
}

export function useInvoices() {
  return useQuery({
    queryKey: ['billing', 'invoices'],
    queryFn: () => api.get<InvoiceListResponse>('/api/v1/billing/invoices'),
    staleTime: 5 * 60_000, // invoices rarely change
  });
}

export function useCheckout() {
  return useMutation({
    mutationFn: (plan: PlanName) =>
      api.post<{ url: string }>('/api/v1/billing/checkout', { plan }),
    onSuccess: (data) => {
      window.location.href = data.url;
    },
  });
}

export function usePortal() {
  return useMutation({
    mutationFn: () =>
      api.post<{ url: string }>('/api/v1/billing/portal'),
    onSuccess: (data) => {
      window.location.href = data.url;
    },
  });
}
```

### Invoices API Endpoint

```typescript
// packages/api/src/routes/billing/invoices.ts
// GET /api/v1/billing/invoices

interface InvoiceItem {
  id: string;
  date: string;                // ISO 8601
  amount: number;              // cents
  currency: string;
  status: 'paid' | 'open' | 'void' | 'uncollectible' | 'draft';
  pdfUrl: string | null;       // Stripe-hosted PDF
  hostedUrl: string | null;    // Stripe-hosted invoice page
}

interface InvoiceListResponse {
  invoices: InvoiceItem[];
  hasMore: boolean;
}

app.get('/api/v1/billing/invoices', async (request, reply) => {
  const user = request.user;
  const installation = await installationStore.get(user.installationId);

  if (!installation?.stripe_customer_id) {
    return reply.send({ invoices: [], hasMore: false });
  }

  const stripeInvoices = await stripe.invoices.list({
    customer: installation.stripe_customer_id,
    limit: 12,
  });

  const invoices: InvoiceItem[] = stripeInvoices.data.map((inv) => ({
    id: inv.id,
    date: new Date((inv.created ?? 0) * 1000).toISOString(),
    amount: inv.amount_due ?? 0,
    currency: inv.currency ?? 'usd',
    status: inv.status as InvoiceItem['status'],
    pdfUrl: inv.invoice_pdf ?? null,
    hostedUrl: inv.hosted_invoice_url ?? null,
  }));

  return reply.send({
    invoices,
    hasMore: stripeInvoices.has_more,
  });
});
```

### Overage Cost Estimation

```tsx
// packages/dashboard/src/pages/billing/OverageBanner.tsx
export function OverageBanner({ quota }: { quota: QuotaSnapshot }): React.ReactElement {
  const overageRuns = Math.max(0, quota.usage.workflow_runs - quota.limits.workflow_runs);
  const overageTokens = Math.max(0, quota.usage.llm_tokens - quota.limits.llm_tokens);
  const overageRepos = Math.max(0, quota.usage.connected_repos - quota.limits.connected_repos);

  // Pricing: $0.02/run, $2.00/1M tokens, $1.50/repo/month
  const estimatedCost =
    overageRuns * 0.02 +
    (overageTokens / 1_000_000) * 2.00 +
    overageRepos * 1.50;

  return (
    <div className="overage-banner" role="alert">
      <strong>Overage Active</strong>
      <p>
        Your usage exceeds the Pro plan base allocation.
        Estimated overage charges this period: <strong>${estimatedCost.toFixed(2)}</strong>
      </p>
      <details>
        <summary>Breakdown</summary>
        <ul>
          {overageRuns > 0 && <li>{overageRuns} extra workflow runs at $0.02/run = ${(overageRuns * 0.02).toFixed(2)}</li>}
          {overageTokens > 0 && <li>{(overageTokens / 1_000_000).toFixed(2)}M extra tokens at $2.00/M = ${((overageTokens / 1_000_000) * 2.00).toFixed(2)}</li>}
          {overageRepos > 0 && <li>{overageRepos} extra repos at $1.50/mo = ${(overageRepos * 1.50).toFixed(2)}</li>}
        </ul>
      </details>
    </div>
  );
}
```

### Plan Comparison for Upgrade CTA

```tsx
// packages/dashboard/src/pages/billing/UpgradePrompt.tsx
const PLAN_FEATURES = {
  free: ['50 workflow runs/mo', '500K LLM tokens/mo', '3 repos', 'Community support'],
  pro: ['2,000 workflow runs/mo', '10M LLM tokens/mo', '25 repos', 'Pay-as-you-go overages', 'Email support (48h)'],
  enterprise: ['Unlimited workflow runs', 'Custom token allocation', 'Unlimited repos', 'Dedicated support SLA', 'Custom integrations'],
};
```

## Dependencies

- **Prerequisite**: Story 20-1 (plan config, Stripe customer)
- **Prerequisite**: Story 20-2 (subscription endpoints: checkout, portal, subscription status)
- **Prerequisite**: Story 20-3 (usage endpoint)
- **Prerequisite**: Story 20-4 (quota endpoint)
- **Related**: Epic 5 (dashboard framework, existing React portal)

## Testing Strategy

1. **Component unit tests** (Vitest + React Testing Library):
   - `BillingPage`: renders all sections, handles loading/error states
   - `PlanSummary`: renders correct plan name, status badge, period dates for all plan types
   - `UsageCharts`: renders three bars, correct percentages, "Unlimited" label for enterprise, overage styling at 100%+
   - `InvoiceHistory`: renders table rows, formats amounts as currency, shows PDF links
   - `OverageBanner`: calculates correct overage cost, hidden when no overage
   - `UpgradePrompt`: renders feature comparison, checkout button works
2. **Hook tests**: Mock API responses, verify `useQuota`/`useUsage`/`useInvoices` return correct data shapes, verify refetch intervals
3. **Accessibility tests**: Verify ARIA roles on progress bars, keyboard navigation through plan change buttons, color contrast on overage indicators
4. **E2E test** (Playwright or similar): Navigate to `/billing`, verify page renders, interact with "Change Plan" button, verify redirect to Stripe

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `packages/dashboard/src/pages/billing/BillingPage.tsx` | Create |
| `packages/dashboard/src/pages/billing/PlanSummary.tsx` | Create |
| `packages/dashboard/src/pages/billing/UsageCharts.tsx` | Create |
| `packages/dashboard/src/pages/billing/InvoiceHistory.tsx` | Create |
| `packages/dashboard/src/pages/billing/PaymentMethod.tsx` | Create |
| `packages/dashboard/src/pages/billing/UpgradePrompt.tsx` | Create |
| `packages/dashboard/src/pages/billing/OverageBanner.tsx` | Create |
| `packages/dashboard/src/pages/billing/billing.hooks.ts` | Create |
| `packages/dashboard/src/pages/billing/billing.types.ts` | Create |
| `packages/dashboard/src/pages/billing/billing.css` | Create |
| `packages/dashboard/src/pages/billing/__tests__/BillingPage.test.tsx` | Create |
| `packages/dashboard/src/pages/billing/__tests__/PlanSummary.test.tsx` | Create |
| `packages/dashboard/src/pages/billing/__tests__/UsageCharts.test.tsx` | Create |
| `packages/dashboard/src/pages/billing/__tests__/InvoiceHistory.test.tsx` | Create |
| `packages/api/src/routes/billing/invoices.ts` | Create |
| `packages/api/src/routes/billing/__tests__/invoices.test.ts` | Create |
| `packages/api/src/routes/billing/index.ts` | Modify (register invoices route) |
| `packages/dashboard/src/App.tsx` | Modify (add /billing route) |
| `packages/dashboard/src/components/Sidebar.tsx` | Modify (add Billing nav item) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. Reviewed the existing dashboard architecture in `packages/dashboard/`
4. Verified the dashboard uses React + @tanstack/react-query (or equivalent)
5. Planned TDD approach (Red-Green-Refactor cycle)

### Dashboard Framework

The billing page integrates into the existing dashboard in `packages/dashboard/`. Follow existing patterns for:
- Page layout and navigation integration
- API client usage (whatever HTTP client the dashboard uses)
- State management (react-query or equivalent)
- CSS/styling approach (CSS modules, Tailwind, or whatever is established)

### Stripe Customer Portal vs Custom UI

For payment method management and subscription cancellation, we redirect to Stripe's hosted Customer Portal rather than building custom UI. This is intentional:
- PCI compliance: we never handle card numbers
- Reduced maintenance: Stripe handles 3DS, card updates, retry logic
- Consistent UX: users familiar with Stripe's portal from other services

We build custom UI only for usage visualization and plan comparison, which Stripe's portal does not provide.

### Responsive Design

The billing page must work on desktop (1024px+) and tablet (768px+). Mobile is not required for the initial implementation since Tamma is primarily a developer tool used on desktop, but the layout should not break on smaller screens.

### Invoice PDF Access

Invoice PDFs are hosted by Stripe and accessible via a time-limited URL. The `invoice_pdf` field from the Stripe API returns a direct download URL. No need to proxy these through our API.

## Logging Requirements

- **INFO**: Billing page loaded (installation_id, plan), checkout initiated (plan), portal session created
- **DEBUG**: API responses for usage/quota/invoices (counts, not content), component render timings
- **WARN**: Stripe API timeout when fetching invoices (show cached/stale data), usage poll failure
- **ERROR**: Invoice API returns error, checkout session creation failed, portal session creation failed
- **Structured context**: Include `{ installationId, plan, page }` where applicable
- **Credential safety**: NEVER log payment method details, invoice amounts, or customer email

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-03-28 | 1.0.0   | Initial story creation | Claude |
