---
title: "Story 21.5: User Dashboard — Settings & Billing"
sidebar:
  order: 210
---

Status: planned

## Story

As a **logged-in Tamma user**,
I want to manage my profile, organization settings, API keys, and billing subscription from a settings area,
so that I can control my account and subscription without contacting support.

## Acceptance Criteria

1. A "Settings" page at `/user/settings` displays a tabbed interface with sections: Profile, Organization, API Keys, and Notifications
2. The Profile tab shows the user's avatar (from GitHub), display name, email, GitHub username, and account creation date, with an option to update display name and email notification preferences
3. The Organization tab (visible to admins and owners only) shows: organization name, default AI provider, default Git platform, team members list with role badges, and an "Invite Member" button
4. The API Keys tab lets users create, view (masked), copy, and revoke personal API keys with labels and expiry dates
5. The Notifications tab lets users toggle email/webhook notifications for: workflow completed, workflow failed, PR created, PR merged, billing events
6. A "Billing" page at `/user/billing` shows: current plan name and tier badge, billing cycle (monthly/annual), next billing date, usage summary (repos connected / limit, runs this month / limit), and payment method summary
7. A "Manage Subscription" button redirects to the Stripe Customer Portal where users can upgrade/downgrade plans, update payment method, view invoices, and cancel subscription
8. A usage bar or progress indicator shows current usage vs. plan limits for repos and workflow runs, with a warning state at 80% and a hard limit indicator at 100%
9. An invoice history table shows recent invoices with: date, amount, status (paid/pending/failed), and a "Download PDF" link (via Stripe)
10. The "Danger Zone" section (visible to owners only) allows: transferring ownership and deleting the organization (with double confirmation)
11. All settings changes emit appropriate DCB events (`USER.SETTINGS.UPDATED`, `API_KEY.CREATED`, `API_KEY.REVOKED`, etc.)
12. The pages are accessible only to authenticated users with appropriate role checks per section
13. The pages are responsive and follow the existing dashboard design system

## Technical Context

### Page Structure

```
/user/settings
├── /user/settings            (default: Profile tab)
├── /user/settings?tab=profile
├── /user/settings?tab=organization
├── /user/settings?tab=api-keys
└── /user/settings?tab=notifications

/user/billing
├── /user/billing             (subscription overview + usage)
└── /user/billing/invoices    (optional: separate invoice history page)
```

### API Endpoints Required

| Method | Endpoint | Purpose |
|--------|----------|---------|
| `GET` | `/api/v1/users/me` | Get current user profile |
| `PATCH` | `/api/v1/users/me` | Update profile (display name, email prefs) |
| `GET` | `/api/v1/organizations/:orgId` | Get organization details |
| `PATCH` | `/api/v1/organizations/:orgId` | Update org settings |
| `GET` | `/api/v1/organizations/:orgId/members` | List team members |
| `POST` | `/api/v1/organizations/:orgId/invites` | Invite a member |
| `DELETE` | `/api/v1/organizations/:orgId/members/:userId` | Remove a member |
| `PATCH` | `/api/v1/organizations/:orgId/members/:userId` | Update member role |
| `GET` | `/api/v1/api-keys` | List user's API keys |
| `POST` | `/api/v1/api-keys` | Create a new API key |
| `DELETE` | `/api/v1/api-keys/:keyId` | Revoke an API key |
| `GET` | `/api/v1/billing/subscription` | Get current subscription |
| `GET` | `/api/v1/billing/usage` | Get current period usage |
| `POST` | `/api/v1/billing/portal-session` | Create Stripe Customer Portal session |
| `GET` | `/api/v1/billing/invoices` | List invoices from Stripe |

### Component Architecture

```
packages/dashboard/src/pages/user/
├── SettingsPage.tsx            Tabbed settings container
├── settings/
│   ├── ProfileTab.tsx         User profile form
│   ├── OrganizationTab.tsx    Org settings + team members
│   ├── MemberRow.tsx          Team member row with role badge
│   ├── InviteMemberDialog.tsx Invite member modal
│   ├── ApiKeysTab.tsx         API key management
│   ├── ApiKeyRow.tsx          Individual key row (masked value, copy, revoke)
│   ├── CreateApiKeyDialog.tsx Create key modal with label + expiry
│   ├── NotificationsTab.tsx   Notification toggles
│   └── DangerZone.tsx         Ownership transfer + delete org
├── BillingPage.tsx            Billing overview
├── billing/
│   ├── SubscriptionCard.tsx   Current plan display
│   ├── UsageBar.tsx           Usage vs. limit progress bar
│   ├── UsageSummary.tsx       Repos + runs usage cards
│   ├── PaymentMethod.tsx      Payment method summary
│   └── InvoiceTable.tsx       Invoice history table
```

### Zustand Stores

```typescript
// packages/dashboard/src/stores/settingsStore.ts
interface SettingsStore {
  user: UserProfile | null;
  organization: Organization | null;
  members: TeamMember[];
  apiKeys: ApiKey[];
  loading: Record<string, boolean>;
  fetchProfile: () => Promise<void>;
  updateProfile: (data: Partial<UserProfile>) => Promise<void>;
  fetchOrganization: () => Promise<void>;
  updateOrganization: (data: Partial<Organization>) => Promise<void>;
  fetchMembers: () => Promise<void>;
  inviteMember: (email: string, role: string) => Promise<void>;
  removeMember: (userId: string) => Promise<void>;
  updateMemberRole: (userId: string, role: string) => Promise<void>;
  fetchApiKeys: () => Promise<void>;
  createApiKey: (label: string, expiresAt: string | null) => Promise<ApiKey>;
  revokeApiKey: (keyId: string) => Promise<void>;
}

// packages/dashboard/src/stores/billingStore.ts
interface BillingStore {
  subscription: Subscription | null;
  usage: UsageSummary | null;
  invoices: Invoice[];
  loading: boolean;
  fetchSubscription: () => Promise<void>;
  fetchUsage: () => Promise<void>;
  fetchInvoices: () => Promise<void>;
  createPortalSession: () => Promise<string>;  // returns portal URL
}
```

### Data Models

```typescript
interface UserProfile {
  id: string;
  githubUsername: string;
  displayName: string;
  email: string;
  avatarUrl: string;
  role: 'member' | 'admin' | 'owner';
  createdAt: string;
  notificationPreferences: {
    workflowCompleted: boolean;
    workflowFailed: boolean;
    prCreated: boolean;
    prMerged: boolean;
    billingEvents: boolean;
  };
}

interface Organization {
  id: string;
  name: string;
  defaultProvider: string;
  defaultPlatform: string;
  memberCount: number;
  createdAt: string;
}

interface TeamMember {
  id: string;
  githubUsername: string;
  displayName: string;
  avatarUrl: string;
  email: string;
  role: 'member' | 'admin' | 'owner';
  joinedAt: string;
}

interface ApiKey {
  id: string;
  label: string;
  keyPrefix: string;           // First 8 chars for identification (e.g., "tamma_ab")
  maskedKey: string;           // "tamma_ab...xy3z"
  fullKey?: string;            // Only returned once at creation time
  createdAt: string;
  expiresAt: string | null;
  lastUsedAt: string | null;
}

interface Subscription {
  id: string;
  plan: 'free' | 'pro' | 'enterprise';
  status: 'active' | 'trialing' | 'past_due' | 'cancelled';
  interval: 'monthly' | 'annual';
  currentPeriodStart: string;
  currentPeriodEnd: string;
  cancelAtPeriodEnd: boolean;
  paymentMethod: {
    brand: string;             // "visa", "mastercard"
    last4: string;
    expiryMonth: number;
    expiryYear: number;
  } | null;
}

interface UsageSummary {
  repos: { used: number; limit: number };
  runs: { used: number; limit: number };
  periodStart: string;
  periodEnd: string;
}

interface Invoice {
  id: string;
  date: string;
  amount: number;              // cents
  currency: string;
  status: 'paid' | 'open' | 'void' | 'uncollectible';
  pdfUrl: string;
}
```

### Stripe Customer Portal

The "Manage Subscription" button creates a Stripe Customer Portal session via the API:

```typescript
// packages/dashboard/src/services/billingService.ts
export async function createPortalSession(): Promise<string> {
  const response = await fetch('/api/v1/billing/portal-session', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
  });
  const { url } = await response.json();
  return url;  // Redirect to this Stripe-hosted URL
}
```

The API handler:

```typescript
// packages/api (conceptual)
const session = await stripe.billingPortal.sessions.create({
  customer: user.stripeCustomerId,
  return_url: 'https://app.tamma.dev/user/billing',
});
return { url: session.url };
```

### Files to Create

| File | Purpose |
|------|---------|
| `packages/dashboard/src/pages/user/SettingsPage.tsx` | Tabbed settings container |
| `packages/dashboard/src/pages/user/settings/ProfileTab.tsx` | User profile form |
| `packages/dashboard/src/pages/user/settings/OrganizationTab.tsx` | Org settings + team list |
| `packages/dashboard/src/pages/user/settings/MemberRow.tsx` | Team member row |
| `packages/dashboard/src/pages/user/settings/InviteMemberDialog.tsx` | Invite dialog |
| `packages/dashboard/src/pages/user/settings/ApiKeysTab.tsx` | API key management |
| `packages/dashboard/src/pages/user/settings/ApiKeyRow.tsx` | Key row component |
| `packages/dashboard/src/pages/user/settings/CreateApiKeyDialog.tsx` | Create key dialog |
| `packages/dashboard/src/pages/user/settings/NotificationsTab.tsx` | Notification prefs |
| `packages/dashboard/src/pages/user/settings/DangerZone.tsx` | Danger zone section |
| `packages/dashboard/src/pages/user/BillingPage.tsx` | Billing overview page |
| `packages/dashboard/src/pages/user/billing/SubscriptionCard.tsx` | Plan display card |
| `packages/dashboard/src/pages/user/billing/UsageBar.tsx` | Usage progress bar |
| `packages/dashboard/src/pages/user/billing/UsageSummary.tsx` | Usage cards |
| `packages/dashboard/src/pages/user/billing/PaymentMethod.tsx` | Payment method summary |
| `packages/dashboard/src/pages/user/billing/InvoiceTable.tsx` | Invoice history |
| `packages/dashboard/src/stores/settingsStore.ts` | Settings Zustand store |
| `packages/dashboard/src/stores/billingStore.ts` | Billing Zustand store |
| `packages/dashboard/src/services/settingsService.ts` | Settings API client |
| `packages/dashboard/src/services/billingService.ts` | Billing API client |

### Files to Modify

| File | Change |
|------|--------|
| `packages/dashboard/src/router.tsx` | Add `/user/settings` and `/user/billing` routes |
| `packages/dashboard/src/pages/user/UserLayout.tsx` | Add Settings and Billing links to user nav |

## Implementation Notes

- **API key security**: The full API key value is returned exactly once at creation time. Store and display it in a copy-to-clipboard dialog with a warning that it will not be shown again. After creation, only the masked version (`tamma_ab...xy3z`) is stored and displayed.
- **Stripe Customer Portal**: Prefer the Stripe-hosted portal for all plan changes, payment method updates, invoice downloads, and cancellations. This minimizes PCI compliance scope and keeps billing logic in Stripe. The Tamma UI only shows current state; mutations happen in the portal.
- **Usage limits**: The API should return current usage counts. The UI displays usage bars. When usage exceeds 80%, show a yellow warning. At 100%, show a red indicator with an upgrade CTA. The API enforces hard limits — the UI is informational.
- **Role-based visibility**: Use the user's role from the auth context to conditionally render tabs. Members see Profile, API Keys, Notifications, and Billing. Admins add Organization. Owners add Danger Zone.
- **Optimistic updates**: For toggles (notification preferences, pause/resume), use optimistic updates in Zustand with rollback on API failure.
- **Form validation**: Use HTML5 validation for simple fields. For complex validation (email format, API key labels), validate client-side before submission.
- **Danger zone styling**: Use red/destructive color scheme with prominent warnings. Require typing the organization name to confirm deletion (similar to GitHub's repo deletion).
- **Admin API Keys tab**: Note that an `ApiKeysTab` already exists in `packages/dashboard/src/pages/admin/ApiKeysTab.tsx`. The user-facing version manages personal keys only. Reuse shared components where possible but keep the pages separate.
- **Invoice amounts**: Stripe amounts are in cents. Format as currency: `(amount / 100).toFixed(2)` with the correct currency symbol.

## Dependencies

- **Story 21.4** (User Dashboard — Repos & Runs) — provides `UserLayout`, router integration, and dashboard navigation patterns
- **Story 21.2** (Pricing + Stripe) — Stripe integration must exist for billing to function
- **Epic 16** (Auth + RBAC) — user auth context, role enforcement, user/org data model

## Estimated Effort

**24 hours**

| Task | Hours |
|------|-------|
| SettingsPage tabbed container | 2 |
| ProfileTab (form, avatar, preferences) | 3 |
| OrganizationTab + MemberRow + InviteDialog | 4 |
| ApiKeysTab + CreateApiKeyDialog | 3 |
| NotificationsTab (toggles) | 1 |
| DangerZone (confirmation dialogs) | 2 |
| BillingPage + SubscriptionCard + UsageBars | 4 |
| InvoiceTable + PaymentMethod | 2 |
| Stores + API services | 2 |
| Role-based visibility + testing | 1 |

---

**Last Updated**: 2026-03-28
