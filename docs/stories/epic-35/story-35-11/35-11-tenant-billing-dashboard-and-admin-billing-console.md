# Story 35-11: Tenant Billing Dashboard (dashboard-user) & Admin Billing Console (dashboard)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-phase development workflow (Read → Research → Break
Down → TDD → Quality Gates → Failure Handling), the `.dev/` knowledge-base usage rules,
TRACE/DEBUG logging requirements, test-driven development, the 100%-critical-path coverage
target, and build/quality-gate enforcement.

## User Story

As a **tenant admin (SaaS) and as a platform owner**,
I want a Billing area in the tenant dashboard (`packages/dashboard-user`) and a Billing console
in the admin dashboard (`packages/dashboard`) that surface my plan, BYOK/platform mode, usage,
invoices, payment method, wallet balance and dunning state (tenant) and per-tenant
subscription/MRR, catalog-sync, webhook/reconciliation health and operator actions (admin),
so that billing is fully self-service through the UI and operators can manage the revenue
estate without touching Stripe or the database directly.

## Priority

P1 — The Epic 35 billing backend is invisible to users without these two surfaces; this story
is the customer-facing and operator-facing front end for the whole billing domain. It re-targets
the original TypeScript Story 20-5 ("billing dashboard") onto the current C# control-plane and
the two React dashboards.

## Acceptance Criteria

This story is **frontend-only plus thin endpoint composition**. It renders data already produced
by Stories 35-1 … 35-10 and MUST NOT re-implement any billing business logic in React or add new
billing math on the server. Where a screen needs a single composed read that no existing 35-x
endpoint provides, a thin aggregation endpoint may be added (see Technical Design), but it only
reads from existing services/mirrors.

1. **Tenant Billing area exists.** `packages/dashboard-user` gains a Billing section rooted at
   `packages/dashboard-user/src/pages/billing/` with a route `/billing` (and sub-tabs Overview /
   Usage / Invoices / Payment / Wallet) registered in `packages/dashboard-user/src/App.tsx`,
   reachable from the app nav. The Overview tab shows the current plan name (from `Plan.DisplayName`
   via `GET /api/v1/billing/subscription`) and the current `BillingMode` (PlatformProvided | Byok)
   with a mode toggle (Story 35-2 `PUT /api/v1/billing/mode`).
2. **Current-period usage with platform-vs-BYOK split** is rendered on the Usage tab from
   `GET /api/v1/billing/usage` (Story 35-3): platform input/output tokens, BYOK input/output tokens,
   `platform_cost_usd`, `billable_usd`, seats, and `period_start`/`period_end`. The platform and
   BYOK figures are visually distinguished, and BYOK token usage is labelled "not billed for tokens".
   Displayed numbers equal the endpoint payload and refresh on a manual refresh and on mount; a note
   states figures lag billable Stripe state by at most the metering flush interval (Story 35-3, default 60s).
3. **Quota & dunning banners.** When the tenant is at/over a plan quota (Story 35-6 quota state on the
   subscription/usage read) a quota banner renders; when the tenant is in a non-`active` billing state
   (`past_due` | `grace` | `suspended` from Story 35-8) a dunning banner renders with the current stage,
   retry-attempt count, and next-retry time, deep-linking to the Payment tab.
4. **Stripe portal + payment method.** The Payment tab shows the masked payment-method mirror from
   `GET /api/v1/billing/payment-methods` (brand/last4/exp/default — Story 35-7), an "Open billing portal"
   button that calls `POST /api/v1/billing/portal-session` and redirects to the returned Stripe URL, and
   an "Add/replace card" action that uses the `POST /api/v1/billing/payment-methods/setup-intent` client
   secret. No PAN/card data is ever entered into or stored by Tamma UI (Stripe Elements / portal handle it).
5. **Invoice history + PDF.** The Invoices tab lists invoices (paged) from `GET /api/v1/billing/invoices`
   (Story 35-8) showing period, amount due/paid, status, and a base/overage/credit line-item breakdown on
   the detail view (`GET /api/v1/billing/invoices/{id}`), with "View / Download PDF" linking to the
   Stripe-hosted `pdf_url`/`hosted_invoice_url`. Tamma never serves invoice PDF bytes itself.
6. **Wallet balance + top-up.** The Wallet tab shows the derived balance and paged ledger history from
   `GET /api/v1/billing/wallet` (Story 35-10) and a "Top up" action that calls
   `POST /api/v1/billing/wallet/topup` to start the Stripe one-time PaymentIntent flow.
7. **Tenant RBAC-aware rendering.** Read views are visible to any tenant member; manage controls
   (mode toggle, portal/setup-intent, top-up) are rendered ONLY for `tenant_owner`/`tenant_admin`
   (gated client-side by `TenantAdminGuard` / `user.role`), and the backend already returns 403 for a
   `member` calling those endpoints (Stories 35-2/35-7/35-10). A member sees read-only views with no
   manage buttons rendered.
8. **Admin Billing console exists.** `packages/dashboard` gains a `billing` admin tab registered in
   `packages/dashboard/src/pages/admin/AdminLayout.tsx` (`AdminTab` union + `TABS`), rendered behind the
   admin route (`AdminGuard`) and gated on the server by `OwnerAccess`/`PlatformOwnerAccess`. It shows a
   per-tenant subscription/revenue overview (plan, status, MRR/lifetime + period revenue) and total MRR.
9. **Admin catalog & health panels.** The console surfaces plan-price-catalog sync status (Story 35-1
   catalog) and webhook + reconciliation health (Stories 35-5 webhook ingest, 35-3 reconciliation):
   last webhook received, unprocessed/dead-letter count, and last reconciliation result / drift count.
10. **Admin operator actions.** The console exposes manual credit-grant (Story 35-10 admin grant,
    `OwnerAccess`) and tenant suspend/reinstate (Story 35-8 `BILLING.TENANT.SUSPENDED`/`REINSTATED`)
    actions, each behind a confirmation dialog and `OwnerAccess`. Successful actions optimistically update
    the row and re-fetch.
11. **All data comes from 35-x endpoints — no frontend business logic.** No billing computation
    (margin, proration, credit application, dunning advancement) is performed in React or in any new
    endpoint added by this story; aggregation endpoints only project existing service/mirror reads.
12. **Single-user mode hides Billing entirely.** When the process is in `SingleUser` mode there is no
    Stripe billing: the tenant Billing nav entry and route are hidden, and the admin Billing tab is hidden.
    A new `GET /api/v1/billing/capabilities` (or the existing settings/me payload) exposes
    `{ billingEnabled: boolean, mode }` derived from `ITammaModeProvider`; the dashboards gate on it
    (never on a hardcoded build flag, so one build serves both modes).
13. **Loading / empty / error states.** Every panel handles loading (skeleton/spinner), empty
    ("No invoices yet", "No payment method on file", "Billing not provisioned"), and error (retryable
    message, never a blank screen). Target p95 page load < 1s (data fetched lazily per tab, not all at once).
14. **No secret/PAN exposure.** No BYOK provider key value, Stripe secret, or card PAN is ever rendered;
    BYOK keys are referenced only by cabinet handle/last-4-style label (Epic 29 reveal path is server-side
    only). The mode toggle shows BYOK as "configured / not configured", never the key.
15. **Tests.** Vitest + Testing Library component/integration tests in both packages cover: RBAC-gated
    rendering (member vs admin), platform-vs-BYOK usage split display, invoice list + PDF link, mode
    toggle happy/error path, single-user hiding, loading/empty/error states, and the admin
    suspend/reinstate + credit-grant actions — all against mocked endpoints (no live Stripe).

## Technical Design

This is a UI story spanning the two React dashboards, with a thin C# composition layer where a screen
needs a single read no 35-x endpoint already provides. The C# entities, services and most endpoints are
owned by sibling stories; this story consumes them.

### Backend (C#) — thin composition only

Namespace `Tamma.Api.Endpoints.Billing` (new dir `apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/`,
also created by 35-2/35-3/35-7/35-8/35-10) and `Tamma.Api.Endpoints` for the admin file.

**Capabilities (mode gate) — NEW, this story.** A tiny read so the dashboards know whether to render
Billing at all:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/BillingCapabilitiesEndpoints.cs  (NEW)
public static class BillingCapabilitiesEndpoints
{
    // GET /api/v1/billing/capabilities  (MemberAccess)
    public static IResult GetCapabilities(ITammaModeProvider mode) =>
        Results.Ok(new
        {
            billingEnabled = mode.Mode == TammaMode.SaaS,
            mode = mode.Mode.ToString(),   // "SaaS" | "SingleUser"
        });
}
```

`ITammaModeProvider` lives at `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs`
(verified) and is already a process-wide singleton. No new business logic.

**Tenant subscription read — NEW thin endpoint, this story (only if 35-4 didn't already ship it).**
The Overview tab needs `{ planSlug, planDisplayName, billingMode, subscriptionStatus, billingState,
quotaState, periodStart, periodEnd }` in one call. If Story 35-4 already exposes
`GET /api/v1/billing/subscription`, consume it as-is and add nothing. Otherwise add a read-only
composition endpoint that reads the local subscription mirror (35-4), `Tenant.PlanId` →
`Plan` (`apps/tamma-elsa/src/Tamma.Data/Entities/Plan.cs`, verified: `Slug`, `DisplayName`,
`MonthlyPriceUsd`, `Quotas`), `BillingCustomer.BillingMode` (35-2) and the billing state (35-8) —
projection only, no computation. Tenant scoping reuses the existing
`/api/v1/orgs/{tenantId:guid}/...` membership filter pattern (see `UserDashboardEndpoints` +
`RequireTenantMembershipFilter`) OR the caller-tenant pattern the 35-x billing endpoints already use
(`GET /api/v1/billing/*` scoped to the caller's active tenant); MATCH whichever the sibling billing
endpoints land on to keep one convention.

**Admin billing overview — NEW thin endpoint, this story.** The admin console needs a per-tenant
revenue/subscription roll-up. Add to a new
`apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminBillingEndpoints.cs` (NEW; the spec's
`Endpoints/Admin/AdminAnalyticsEndpoints.cs` path is wrong — the real analytics file is
`apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs`, verified; mirror its
`OwnerAccess`-gated static-class style):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminBillingEndpoints.cs  (NEW)
public static class AdminBillingEndpoints
{
    // GET /api/v1/admin/billing/overview?limit=N   (OwnerAccess)
    //   -> { totalMrrUsd, activeSubscriptions, pastDue, suspended,
    //        tenants: [{ tenantId, name, planSlug, status, billingState,
    //                    mrrUsd, lifetimeRevenueUsd, currentPeriodRevenueUsd }] }
    public static Task<IResult> GetOverview([FromQuery] int? limit,
        IBillingAdminReadService svc, CancellationToken ct);

    // GET /api/v1/admin/billing/health   (OwnerAccess)
    //   -> { catalogSync:{ inSync, lastSyncedAt, drift[] },
    //        webhooks:{ lastReceivedAt, unprocessed, deadLettered },
    //        reconciliation:{ lastRunAt, mismatches } }
    public static Task<IResult> GetHealth(IBillingAdminReadService svc, CancellationToken ct);
}
```

`IBillingAdminReadService` (NEW, `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingAdminReadService.cs`)
is a **read-only projector** over the sibling mirrors: the 35-4 subscription mirror + `Plan` for MRR,
35-8 `BillingInvoice` for lifetime/period revenue, 35-1 catalog state, 35-5 webhook ingest table, and
the 35-3 reconciliation result. It performs sum/group reads only — the same posture as the existing
`IPlatformAnalyticsService` (`apps/tamma-elsa/src/Tamma.Api/Services/Analytics/`). Manual credit-grant
and suspend/reinstate are NOT re-implemented here — the console calls the already-owned admin endpoints:
Story 35-10's admin grant endpoint (`Endpoints/Admin/`) and Story 35-8's suspend/reinstate
(`InvoiceService`/`DunningStateMachine` surface).

**Wiring.** Map the new endpoints in `apps/tamma-elsa/src/Tamma.Api/Program.cs` next to the other
billing/admin maps: capabilities + subscription under `MemberAccess`/tenant-membership; admin overview
+ health under `OwnerAccess` (mirroring how `AdminAnalyticsEndpoints` are mapped). Register
`IBillingAdminReadService` in DI alongside the other billing services.

**DCB events.** This story emits NO new DCB events of its own — operator actions surface
existing ones from sibling stories: `BILLING.CREDIT.GRANTED` (35-10), `BILLING.TENANT.SUSPENDED` /
`BILLING.TENANT.REINSTATED` (35-8). The read endpoints are queries (no events). All DCB events stay
`AGGREGATE.ACTION.STATUS` with JSONB tags `{ tenantId, ... }` per the existing convention.

### Frontend — tenant dashboard (`packages/dashboard-user`, TS/React)

```
packages/dashboard-user/src/
  api/
    billing.ts                         # NEW — typed client over the 35-x /api/v1/billing/* reads + capabilities
    billing.test.ts                    # NEW
  pages/billing/
    BillingPage.tsx                    # NEW — tab shell (Overview/Usage/Invoices/Payment/Wallet)
    OverviewTab.tsx                    # NEW — plan + BillingMode toggle (35-2)
    UsageTab.tsx                       # NEW — platform-vs-BYOK split (35-3)
    InvoicesTab.tsx                    # NEW — list + detail + PDF link (35-8)
    PaymentTab.tsx                     # NEW — masked PM mirror + portal + setup-intent (35-7)
    WalletTab.tsx                      # NEW — balance + ledger + top-up (35-10)
    components/
      QuotaBanner.tsx                  # NEW — quota at/over (35-6)
      DunningBanner.tsx                # NEW — past_due/grace/suspended (35-8)
      BillingModeToggle.tsx            # NEW
    __tests__/*.test.tsx               # NEW — colocated Vitest + Testing Library
```

- `api/billing.ts` reuses the existing `apiClient` (`packages/dashboard-user/src/api/client.ts`,
  verified — `credentials:'include'`, refresh-on-401) and mirrors the `api/alerts.ts` shape. It
  exposes typed DTOs (`SubscriptionDto`, `UsageDto`, `InvoiceDto`, `PaymentMethodDto`, `WalletDto`,
  `BillingCapabilitiesDto`) matching the 35-x payloads, plus mutation wrappers
  (`setBillingMode`, `openPortalSession`, `createSetupIntent`, `topUpWallet`).
- Routing: add the `/billing` route group under the authenticated `AppLayout` in
  `packages/dashboard-user/src/App.tsx`. The route itself is rendered for any member (read views);
  manage controls inside tabs are gated with the existing `TenantAdminGuard` / `user.role`
  (`packages/dashboard-user/src/guards/TenantAdminGuard.tsx`, verified — `admin`|`owner` set).
- Mode gating: a small `useBillingCapabilities()` hook fetches `GET /api/v1/billing/capabilities`;
  when `billingEnabled === false` the nav entry and route render nothing (single-user). The hook is
  the single source for the gate so no hardcoded `import.meta.env` build flag is used.
- Lazy per-tab fetching keeps the p95 < 1s budget: Overview loads on mount, other tabs fetch on first
  activation. Each tab owns its own loading/empty/error state.

### Frontend — admin dashboard (`packages/dashboard`, TS/React)

```
packages/dashboard/src/
  services/admin/
    billing-admin-client.ts            # NEW — over /api/v1/admin/billing/* + reuse 35-10 grant / 35-8 suspend
    billing-admin-client.test.ts       # NEW
  pages/admin/billing/
    BillingConsoleTab.tsx              # NEW — top-level tab content (overview + health + actions)
    TenantRevenueTable.tsx             # NEW — per-tenant subscription/MRR/revenue rows
    BillingHealthPanel.tsx             # NEW — catalog-sync + webhook + reconciliation
    GrantCreditDialog.tsx              # NEW — manual credit grant (35-10)
    SuspendReinstateDialog.tsx         # NEW — suspend/reinstate (35-8)
    __tests__/*.test.tsx               # NEW
```

- Register the tab in `packages/dashboard/src/pages/admin/AdminLayout.tsx`: extend the `AdminTab`
  union (`... | 'billing'`), add `{ id: 'billing', label: 'Billing' }` to `TABS`, and render
  `{activeTab === 'billing' && <BillingConsoleTab />}`. The admin shell is already behind `AdminGuard`
  (`packages/dashboard/src/guards/AdminGuard.tsx`, verified) and the endpoints behind `OwnerAccess`.
- `billing-admin-client.ts` follows the existing `services/admin/admin-api-client.ts` conventions.
- Operator actions confirm before firing and re-fetch the overview on success; errors surface inline.

### API shape consumed (owned by sibling stories — referenced, not built here)

| Endpoint | Owner story |
|---|---|
| `GET /api/v1/billing/subscription` (or composed read) | 35-4 (this story may add the thin composition) |
| `PUT /api/v1/billing/mode` | 35-2 |
| `GET /api/v1/billing/usage` | 35-3 |
| `GET /api/v1/billing/payment-methods`, `POST .../portal-session`, `POST .../setup-intent` | 35-7 |
| `GET /api/v1/billing/invoices`, `GET .../invoices/{id}` | 35-8 |
| `GET /api/v1/billing/wallet`, `POST .../wallet/topup`, admin grant | 35-10 |
| suspend / reinstate | 35-8 |
| `GET /api/v1/billing/capabilities`, `GET /api/v1/admin/billing/overview`, `GET .../health` | **35-11 (this story)** |

### Per-mode + per-tenant handling

- **SingleUser:** `billingEnabled=false` → no Stripe billing surfaces anywhere (AC 12). The capabilities
  read is the single gate; both dashboards hide Billing without separate builds.
- **SaaS:** tenant Billing is scoped to the caller's active tenant (every `/api/v1/billing/*` read is
  tenant-scoped server-side; the admin overview is cross-tenant under `OwnerAccess`). RBAC: member =
  read-only (no manage controls rendered AND server 403s on manage); `tenant_owner`/`tenant_admin` =
  manage; platform owner = admin console + operator actions.

## Dependencies

**Internal (prerequisite — must ship first):**

- **Story 35-2** — BillingMode + `PUT /api/v1/billing/mode` (Overview mode toggle).
- **Story 35-3** — `GET /api/v1/billing/usage` (Usage split tab; reconciliation health for admin).
- **Story 35-4** — subscription lifecycle + local subscription mirror (Overview plan/status; admin MRR).
- **Story 35-7** — payment methods + portal/setup-intent (Payment tab).
- **Story 35-8** — invoices + dunning state + suspend/reinstate (Invoices tab, dunning banner, admin action).
- **Story 35-10** — wallet ledger + top-up + admin credit grant (Wallet tab, admin grant action).

**Internal (related / consumed):** Story 35-1 (plan-price catalog → admin catalog-sync panel),
Story 35-5 (webhook ingest → admin webhook health), Story 35-6 (quota state → quota banner),
Epic 29 secret cabinet (BYOK referenced by handle only).

**Internal infrastructure (already in repo, verified):** `apiClient`
(`packages/dashboard-user/src/api/client.ts`), `TenantAdminGuard` (dashboard-user),
`AdminGuard`/`AdminLayout` (dashboard), `ITammaModeProvider` (`TammaMode.cs`),
`Plan` entity, `OwnerAccess`/`MemberAccess` policies (`Program.cs`).

**Blocks:** none downstream in Epic 35 — this is the terminal UI story for the billing domain.

**External:** Stripe Customer Portal + Stripe.js/Elements (loaded client-side for the portal redirect
and setup-intent; no Stripe secret in the browser). React 18, react-router-dom, Vitest 3 + Testing
Library, Tailwind (existing dashboard stack).

## Testing Strategy

All tests use **mocked endpoints** — no live Stripe, no live API. Stripe is only ever reached via the
backend; the browser only opens a returned portal URL, so frontend tests mock the URL response.

1. **Tenant dashboard component tests** (`packages/dashboard-user`, Vitest + Testing Library, colocated
   `__tests__/`): Overview renders plan + mode and toggles mode (happy + 422/403 error path); Usage tab
   renders the platform-vs-BYOK split exactly as the mocked `GET /api/v1/billing/usage` payload and labels
   BYOK as not-token-billed; Invoices list renders rows + a PDF link to the mocked `pdf_url` and never
   renders bytes; Payment tab shows masked last4 and triggers a portal redirect with the mocked URL;
   Wallet tab shows derived balance + ledger and fires top-up.
2. **RBAC-gated rendering tests:** with `user.role='member'`, manage controls (mode toggle, portal,
   setup-intent, top-up) are NOT in the DOM; with `admin`/`owner` they are. Assert read views still render
   for member.
3. **Single-user hiding tests:** with mocked `capabilities.billingEnabled=false`, the Billing nav entry +
   route render nothing in dashboard-user and the Billing tab is absent in dashboard.
4. **State tests:** loading shows a skeleton/spinner; empty shows the right empty copy ("No invoices yet",
   "No payment method on file", "Billing not provisioned"); error shows a retryable message, never blank.
5. **Admin console tests** (`packages/dashboard`): TenantRevenueTable renders rows + total MRR from mocked
   overview; BillingHealthPanel renders catalog/webhook/reconciliation states (including a drift warning);
   suspend/reinstate and grant-credit dialogs confirm, call the client, optimistically update, and re-fetch;
   a non-owner is blocked by `AdminGuard` (existing behaviour, asserted at the route).
6. **Backend unit tests** (`apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/`, xUnit): `GetCapabilities`
   returns `billingEnabled=true` in SaaS and `false` in SingleUser (drive `ITammaModeProvider`);
   `BillingAdminReadService` projects MRR/revenue/health from seeded mirrors with sum/group only and never
   mutates; tenant-scoped subscription read returns only the caller's tenant.
7. **Tenant-isolation / integration tests** (`apps/tamma-elsa/tests/Tamma.Api.Tests/`): a tenant-A token
   hitting the tenant billing reads never sees tenant-B subscription/invoice/wallet data (cross-tenant →
   404/empty); a `member` token gets 403 on the manage endpoints owned by 35-2/35-7/35-10 (asserted at the
   composition layer); a non-owner gets 403 on `/api/v1/admin/billing/*`. Stripe/provider clients are
   mocked at the service seam (no `STRIPE_SECRET_KEY` needed for these).

Coverage target: 80% line / 75% branch on the new React + endpoint code; RBAC, mode-gating, and the
no-secret-render assertions are treated as critical paths (100%).

## Estimated Effort

5-6 days (2 days tenant dashboard, 2 days admin console, 1 day thin endpoints + DI/wiring, 1 day tests
+ isolation/RBAC hardening).

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/BillingCapabilitiesEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/BillingSubscriptionReadEndpoints.cs` | Create (only if 35-4 lacks the composed read) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminBillingEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/IBillingAdminReadService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingAdminReadService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/BillingServiceCollectionExtensions.cs` | Modify (register read service) or Create if absent |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map capabilities/subscription/admin-billing routes + DI) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingCapabilitiesEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingAdminReadServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingDashboardIsolationTests.cs` | Create |
| `packages/dashboard-user/src/api/billing.ts` | Create |
| `packages/dashboard-user/src/api/billing.test.ts` | Create |
| `packages/dashboard-user/src/pages/billing/BillingPage.tsx` | Create |
| `packages/dashboard-user/src/pages/billing/OverviewTab.tsx` | Create |
| `packages/dashboard-user/src/pages/billing/UsageTab.tsx` | Create |
| `packages/dashboard-user/src/pages/billing/InvoicesTab.tsx` | Create |
| `packages/dashboard-user/src/pages/billing/PaymentTab.tsx` | Create |
| `packages/dashboard-user/src/pages/billing/WalletTab.tsx` | Create |
| `packages/dashboard-user/src/pages/billing/components/QuotaBanner.tsx` | Create |
| `packages/dashboard-user/src/pages/billing/components/DunningBanner.tsx` | Create |
| `packages/dashboard-user/src/pages/billing/components/BillingModeToggle.tsx` | Create |
| `packages/dashboard-user/src/hooks/useBillingCapabilities.ts` | Create |
| `packages/dashboard-user/src/pages/billing/__tests__/*.test.tsx` | Create |
| `packages/dashboard-user/src/App.tsx` | Modify (register `/billing` route + nav) |
| `packages/dashboard/src/services/admin/billing-admin-client.ts` | Create |
| `packages/dashboard/src/services/admin/billing-admin-client.test.ts` | Create |
| `packages/dashboard/src/pages/admin/billing/BillingConsoleTab.tsx` | Create |
| `packages/dashboard/src/pages/admin/billing/TenantRevenueTable.tsx` | Create |
| `packages/dashboard/src/pages/admin/billing/BillingHealthPanel.tsx` | Create |
| `packages/dashboard/src/pages/admin/billing/GrantCreditDialog.tsx` | Create |
| `packages/dashboard/src/pages/admin/billing/SuspendReinstateDialog.tsx` | Create |
| `packages/dashboard/src/pages/admin/billing/__tests__/*.test.tsx` | Create |
| `packages/dashboard/src/pages/admin/AdminLayout.tsx` | Modify (add `billing` tab) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md).
2. Searched `.dev/` for related spikes/bugs/findings/decisions (billing UI, RBAC rendering).
3. Confirmed which `/api/v1/billing/*` endpoints have actually merged from Stories 35-2…35-10 before
   wiring the client — build the UI against the real merged contract, not this story's assumptions, and
   reconcile any shape drift.
4. Verified the tenant-scoping convention the sibling billing endpoints landed on
   (`/api/v1/billing/*` caller-tenant vs `/api/v1/orgs/{tenantId}/...`) and matched it exactly.
5. Planned TDD (Red-Green-Refactor) for both React packages and the backend reads.

### Key design decisions

- **No business logic in the frontend or in this story's endpoints.** The dashboards are pure
  projections; the only server code added is read composition (capabilities, subscription roll-up,
  admin overview/health). This keeps the single source of truth in the 35-x services and satisfies AC 11.
  Operator actions reuse the already-owned 35-8/35-10 endpoints rather than re-implementing suspend/grant.
- **Mode gate is a server read, not a build flag.** `GET /api/v1/billing/capabilities` from
  `ITammaModeProvider` lets one artifact serve both single-user and SaaS, matching how the rest of the
  app stays mode-agnostic at build time.
- **RBAC is defence-in-depth.** Client-side hiding (`TenantAdminGuard`/`AdminGuard`) is UX only; the
  backend (35-2/35-7/35-10/admin policies) is the real gate. Tests assert both layers.
- **No secrets/PAN in the browser, ever.** Card capture happens in Stripe Elements/portal; BYOK keys are
  shown as configured/not-configured by cabinet handle. The setup-intent flow only ever hands the browser
  a client secret, never a Stripe secret key.

### Performance

- Per-tab lazy fetching (Overview eager, others on activation) and paged invoice/ledger lists keep the
  p95 page-load budget < 1s. The admin overview is a single composed read (sum/group), not N+1 per tenant.

### Security requirements

- No BYOK key value, Stripe secret, or PAN rendered (AC 14).
- All manage/operator endpoints stay server-gated (`MemberAccess` for reads, tenant owner/admin for
  tenant manage, `OwnerAccess` for admin actions); client gating is cosmetic.
- Portal/return URLs come pre-validated from 35-7's allowlist; the UI only follows the returned URL.

## Logging Requirements

- **INFO:** billing page/tab opened (tenantId, tab), portal session opened, mode toggle submitted, admin
  overview/health fetched, operator action invoked (admin credit grant / suspend / reinstate — log the
  action + target tenantId, never amounts as secrets but amounts are fine for credit grants).
- **DEBUG:** capabilities resolved (mode, billingEnabled), each tab data fetch start/finish + row counts.
- **WARN:** a billing read returned an error surfaced to the user (endpoint, status), reconciliation drift
  shown in the admin health panel, webhook backlog non-zero.
- **ERROR:** admin overview/health composition read failed; operator action failed after confirmation.
- **Structured context:** include `{ tenantId, tab, action }` where applicable. **Credential safety:**
  NEVER log Stripe keys, BYOK key values, card PAN/last4 beyond the masked display value, or portal URLs
  containing session tokens.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
