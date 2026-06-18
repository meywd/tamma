# Story 35-11 — Tenant Billing Dashboard & Admin Billing Console — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan phase-by-phase. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every phase writes tests
> before implementation. Story file:
> `docs/stories/epic-35/story-35-11/35-11-tenant-billing-dashboard-and-admin-billing-console.md`.

**Goal:** Surface the entire Epic 35 billing domain in the two existing React dashboards —
a tenant-facing Billing area in `packages/dashboard-user` (plan + BYOK/platform mode, usage split,
invoices + PDF, payment method + portal, wallet, quota/dunning banners) and an admin Billing console
in `packages/dashboard` (per-tenant subscription/MRR, catalog/webhook/reconciliation health, manual
credit grant, suspend/reinstate) — plus the thin C# read endpoints needed to compose those screens.
Re-targets the deprecated TypeScript Story 20-5 onto the current C# control-plane + React stack.

---

## Non-goals (YAGNI guard)

- **NO billing business logic in React or in this story's endpoints.** Margin, proration, credit
  application, dunning advancement, metering — all owned by Stories 35-1…35-10. This story only
  projects/reads. New server code is read-composition only.
- **NO re-implementation of operator actions.** Suspend/reinstate (35-8) and admin credit grant
  (35-10) already have endpoints; the console calls them. We do not add new mutate paths for them.
- **NO Stripe secret or PAN in the browser.** Card capture is Stripe Elements/portal; BYOK keys are
  shown by cabinet handle only (Epic 29 reveal stays server-side).
- **NO single-user billing.** Single-user mode hides Billing entirely via a server capability read —
  no separate build, no `import.meta.env` flag.
- **NO new DCB events.** Operator actions surface existing `BILLING.*` events; reads emit nothing.
- **NO dependency on a live Stripe account for tests.** Stripe is reached only by the backend; tests
  mock the service seam / endpoint responses.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### Dashboards (both exist; both are mounted in prod)

- **Tenant dashboard** `packages/dashboard-user/` (TS/React, Vitest + jsdom; `vitest.config.ts`
  includes `src/**/*.test.{ts,tsx}`, setup `src/test/setup.ts`).
  - Router: `packages/dashboard-user/src/App.tsx` — `BrowserRouter`; authenticated routes nest under
    `AuthGuard → AppLayout`; tenant-admin routes wrap children in `TenantAdminGuard`
    (`/settings/alerts`, `/onboarding/platforms`, …). Add `/billing` here.
  - API client: `packages/dashboard-user/src/api/client.ts` — `apiClient` singleton,
    `credentials:'include'`, single-shot refresh-on-401, `VITE_API_URL` base. Reuse it.
  - Existing tenant-scope client to mirror: `packages/dashboard-user/src/api/alerts.ts`
    (DTOs + `/api/v1/orgs/{tenantId}/...` calls, no plaintext creds — exactly the posture billing needs).
  - Guard: `packages/dashboard-user/src/guards/TenantAdminGuard.tsx` — `ADMIN_OR_HIGHER = {admin,owner}`,
    fails closed; renders inline "Admin-only" for members. Auth: `hooks/useAuth.tsx` exposes
    `AuthUser { id,email,displayName,tenantId?,role? }` from `/api/auth/me`.
- **Admin dashboard** `packages/dashboard/` (TS/React, Vitest).
  - Tab shell: `packages/dashboard/src/pages/admin/AdminLayout.tsx` — `type AdminTab = 'users' |
    'tenants' | 'api-keys' | 'health' | 'links' | 'audit-log'`; `TABS` array; switch on `activeTab`.
    Add `'billing'`.
  - Router: `packages/dashboard/src/router.tsx` — admin routes behind `AdminGuard`
    (`guards/AdminGuard.tsx`: `useCurrentUser().isAdmin`, redirects non-admins).
  - Admin service-client convention: `packages/dashboard/src/services/admin/admin-api-client.ts`
    (+ `admin-tenants-client.ts`, `prompts-api-client.ts`). Mirror for `billing-admin-client.ts`.

### Backend (C# `apps/tamma-elsa`)

- **Mode provider** `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs` — verified:
  `ITammaModeProvider.Mode` (`TammaMode.SaaS | SingleUser`), process-wide singleton; `Resolve(...)`
  pure helper for tests. This is the single source for the billing-enabled gate.
- **Authorization policies** `apps/tamma-elsa/src/Tamma.Api/Program.cs` (~956-1082): `OwnerAccess`,
  `PlatformOwnerAccess`, `MemberAccess`, `SettingsView/Manage`, `PromptManage`, etc. (verified).
  Admin analytics routes use `OwnerAccess`. Use `MemberAccess` for tenant reads, `OwnerAccess` for
  admin billing.
- **Analytics precedent to mirror** `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminAnalyticsEndpoints.cs`
  (verified — NOT under `Endpoints/Admin/`; the spec path is wrong) + service interface
  `apps/tamma-elsa/src/Tamma.Api/Services/Analytics/IPlatformAnalyticsService.cs`. Cross-tenant
  read-only sum/group projector — the exact shape `IBillingAdminReadService` should take.
- **Tenant-scoped dashboard precedent** `apps/tamma-elsa/src/Tamma.Api/Endpoints/UserDashboardEndpoints.cs`
  — `/api/v1/orgs/{tenantId:guid}/dashboard/*`, validated by `RequireTenantMembershipFilter` before the
  handler; uses `ITenantDbContextFactory` for per-tenant reads. Pattern for any tenant-scoped read this
  story adds (match whichever convention the sibling `/api/v1/billing/*` endpoints land on).
- **Entities present today (verified):** `Plan.cs` (`Slug`, `DisplayName`, `MonthlyPriceUsd`, `Quotas`,
  `PlacementPolicy`), `Tenant.cs`, `BudgetConfig.cs`, `ProviderDiagnostic.cs`. **Billing entities are
  owned by siblings** (NEW in their stories, do not pre-create): `BillingUsageRollup` (35-3),
  `BillingCustomer`/`BillingMode` (35-2), `BillingPaymentMethod` (35-7), `BillingInvoice`/`Line` (35-8),
  `BillingWalletLedger` (35-10), subscription mirror (35-4).
- **Endpoints dir** `apps/tamma-elsa/src/Tamma.Api/Endpoints/` — no `Billing/` subdir yet (siblings
  create it). `Program.cs` maps endpoints explicitly with `.RequireAuthorization("<policy>")`.

### Sibling-endpoint contract this UI consumes (from specs `/tmp/pab_stories/35-*.json`)

| UI need | Endpoint | Story |
|---|---|---|
| plan + status | `GET /api/v1/billing/subscription` (or this story's composed read) | 35-4 |
| mode toggle | `PUT /api/v1/billing/mode` (422 if BYOK key absent; member 403) | 35-2 |
| usage split | `GET /api/v1/billing/usage` → `{platform tokens, byok tokens, platform_cost_usd, billable_usd, seats, period}` | 35-3 |
| payment method | `GET /api/v1/billing/payment-methods`, `POST .../portal-session`, `POST .../payment-methods/setup-intent` | 35-7 |
| invoices | `GET /api/v1/billing/invoices`, `GET .../invoices/{id}` (+ `pdf_url`/`hosted_invoice_url`) | 35-8 |
| wallet | `GET /api/v1/billing/wallet`, `POST .../wallet/topup`, admin grant | 35-10 |
| suspend/reinstate | 35-8 admin surface | 35-8 |

DCB events surfaced (not emitted here): `BILLING.MODE.CHANGED` (35-2), `BILLING.USAGE.*` (35-3),
`BILLING.PAYMENT_METHOD.*` (35-7), `BILLING.INVOICE.*`/`BILLING.PAYMENT.FAILED`/`BILLING.DUNNING.ESCALATED`/
`BILLING.TENANT.SUSPENDED|REINSTATED` (35-8), `BILLING.CREDIT.GRANTED|CONSUMED|EXPIRED|REFUNDED` (35-10).
All `AGGREGATE.ACTION.STATUS` with `{tenantId, ...}` JSONB tags.

---

## Phased task breakdown (test-first)

### Phase 1 — Backend: capabilities + admin read composition (C#)

**Goal:** the thin server reads the dashboards need that no sibling endpoint provides.

- [ ] **1.1 Tests first** — `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingCapabilitiesEndpointsTests.cs`:
  `GetCapabilities` returns `billingEnabled=true` for `TammaMode.SaaS` and `false` for `SingleUser`
  (drive a fake `ITammaModeProvider`); `mode` string matches.
- [ ] **1.2** `apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/BillingCapabilitiesEndpoints.cs` (NEW):
  static `GetCapabilities(ITammaModeProvider)` → `{ billingEnabled, mode }`. No business logic.
- [ ] **1.3 Tests first** — `BillingAdminReadServiceTests.cs`: seed a subscription mirror + `Plan` rows +
  `BillingInvoice` rows + catalog/webhook/reconciliation state; assert `GetOverviewAsync` computes
  `totalMrrUsd`/counts/per-tenant `mrrUsd`/`lifetimeRevenueUsd`/`currentPeriodRevenueUsd` by sum/group
  ONLY (no writes), and `GetHealthAsync` projects catalog-sync/webhook/reconciliation. Limit clamped.
  (Gate behind whichever sibling mirrors exist; stub missing mirrors with the merged entity types.)
- [ ] **1.4** `IBillingAdminReadService` + `BillingAdminReadService`
  (`apps/tamma-elsa/src/Tamma.Api/Services/Billing/`): read-only projector over 35-1/35-3/35-4/35-5/35-8
  mirrors, mirroring `PlatformAnalyticsService` style. No mutation, no Stripe calls.
- [ ] **1.5** `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminBillingEndpoints.cs` (NEW):
  `GET /api/v1/admin/billing/overview?limit=N`, `GET /api/v1/admin/billing/health` — both `OwnerAccess`,
  delegate to the service (mirror `AdminAnalyticsEndpoints`).
- [ ] **1.6** Subscription composed read — IF Story 35-4 already exposes `GET /api/v1/billing/subscription`,
  skip; else add `BillingSubscriptionReadEndpoints.cs` (NEW, `MemberAccess`, tenant-scoped) projecting
  subscription mirror + `Plan` + `BillingCustomer.BillingMode` + billing state. Projection only.
- [ ] **1.7** Wire in `Program.cs` (map routes with `RequireAuthorization`) + register
  `IBillingAdminReadService` in DI (extend `BillingServiceCollectionExtensions` if siblings created it,
  else create it). Run `dotnet build` + targeted tests via `sg docker -c "dotnet test ..."`.

### Phase 2 — Tenant dashboard: API client + capabilities gate + page shell

- [ ] **2.1 Tests first** — `packages/dashboard-user/src/api/billing.test.ts`: client methods hit the right
  paths/verbs, parse DTOs, propagate `ApiError`/`UnauthorizedError` from `apiClient`.
- [ ] **2.2** `packages/dashboard-user/src/api/billing.ts` (NEW): typed DTOs (`SubscriptionDto`,
  `UsageDto`, `InvoiceDto`, `InvoiceDetailDto`, `PaymentMethodDto`, `WalletDto`, `BillingCapabilitiesDto`)
  + read/mutation wrappers over the 35-x endpoints, on `apiClient` (mirror `api/alerts.ts`).
- [ ] **2.3 Tests first** — `useBillingCapabilities.test.ts(x)`: hook returns `{billingEnabled, mode, loading}`,
  fetches `GET /api/v1/billing/capabilities` once, treats fetch error as `billingEnabled=false` (fail-safe hide).
- [ ] **2.4** `packages/dashboard-user/src/hooks/useBillingCapabilities.ts` (NEW).
- [ ] **2.5 Tests first** — `pages/billing/__tests__/BillingPage.test.tsx`: renders the tab shell; when
  capabilities `billingEnabled=false`, renders nothing (single-user hide); lazy-loads non-Overview tabs.
- [ ] **2.6** `pages/billing/BillingPage.tsx` (NEW): tab shell (Overview/Usage/Invoices/Payment/Wallet),
  per-tab lazy fetch. Register `/billing` route + nav entry in `App.tsx` (read route open to members).

### Phase 3 — Tenant dashboard: tabs + banners

- [ ] **3.1 OverviewTab** (tests first): plan name + `BillingMode` toggle (`BillingModeToggle.tsx`);
  toggle calls `PUT /api/v1/billing/mode`; happy path + 422 (BYOK key missing) + 403 (member) handled;
  manage control hidden for members.
- [ ] **3.2 UsageTab** (tests first): renders platform-vs-BYOK split exactly from mocked `GET .../usage`;
  BYOK labelled "not billed for tokens"; manual refresh; "figures lag ≤ flush interval" note.
- [ ] **3.3 InvoicesTab** (tests first): paged list; detail with base/overage/credit line split; PDF link
  to mocked `pdf_url` (no bytes served by Tamma); empty state "No invoices yet".
- [ ] **3.4 PaymentTab** (tests first): masked PM mirror (brand/last4/exp/default); "Open billing portal"
  → redirect to mocked URL; "Add/replace card" via setup-intent client secret; empty "No payment method
  on file"; manage controls hidden for members; assert no PAN/secret in DOM.
- [ ] **3.5 WalletTab** (tests first): derived balance + paged ledger; "Top up" → `POST .../wallet/topup`;
  empty state; manage hidden for members.
- [ ] **3.6 Banners** (tests first): `QuotaBanner` shows at/over quota (35-6 state); `DunningBanner` shows
  for `past_due`/`grace`/`suspended` with stage + retry count + next-retry, deep-links to Payment tab.
- [ ] **3.7** Loading/empty/error states for every tab (skeleton/spinner, empty copy, retryable error).

### Phase 4 — Admin dashboard: console tab + client + actions

- [ ] **4.1 Tests first** — `services/admin/billing-admin-client.test.ts`: methods hit
  `/api/v1/admin/billing/overview|health` + reuse 35-10 grant / 35-8 suspend-reinstate paths; parse DTOs.
- [ ] **4.2** `packages/dashboard/src/services/admin/billing-admin-client.ts` (NEW), mirroring
  `admin-api-client.ts`.
- [ ] **4.3 Tests first** — `pages/admin/billing/__tests__/*`: `TenantRevenueTable` renders rows + total MRR
  from mocked overview; `BillingHealthPanel` renders catalog/webhook/reconciliation incl. a drift warning;
  `SuspendReinstateDialog` + `GrantCreditDialog` confirm → call client → optimistic update → re-fetch;
  errors surface inline.
- [ ] **4.4** Implement `BillingConsoleTab.tsx`, `TenantRevenueTable.tsx`, `BillingHealthPanel.tsx`,
  `GrantCreditDialog.tsx`, `SuspendReinstateDialog.tsx`.
- [ ] **4.5** Register the tab in `AdminLayout.tsx`: extend `AdminTab` union (`| 'billing'`), add to
  `TABS`, render `{activeTab === 'billing' && <BillingConsoleTab />}`. (Shell already behind `AdminGuard`.)
- [ ] **4.6 Single-user hide:** admin Billing tab absent when capabilities `billingEnabled=false`
  (reuse capabilities read on the admin side, or the existing admin health/mode signal). Test it.

### Phase 5 — Isolation, RBAC hardening, polish

- [ ] **5.1** `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingDashboardIsolationTests.cs`:
  tenant-A token never sees tenant-B subscription/invoice/wallet (cross-tenant → 404/empty); member → 403
  on manage endpoints (35-2/35-7/35-10) at the composition layer; non-owner → 403 on `/api/v1/admin/billing/*`.
  Stripe/provider clients mocked at the service seam.
- [ ] **5.2** Frontend RBAC tests across both dashboards: member sees read-only (no manage controls in DOM);
  admin/owner sees controls; non-admin blocked at admin route.
- [ ] **5.3** No-secret-render assertions: grep-style DOM assertions that no BYOK key value, Stripe secret,
  or PAN appears (only masked last4).
- [ ] **5.4** Performance: confirm per-tab lazy fetch + paging; admin overview is one composed read (no N+1).
- [ ] **5.5** Logging per the story's Logging Requirements (INFO/DEBUG/WARN/ERROR; never log keys/PAN/portal
  URLs). Run full suites: `pnpm test --filter @tamma/dashboard-user`,
  `pnpm test --filter @tamma/dashboard`, and `sg docker -c "dotnet test apps/tamma-elsa/..."`.

---

## Sequencing & dependencies

```
Phase 1 (backend reads) ─┬─► Phase 2 (tenant client + shell) ─► Phase 3 (tenant tabs)
                         └─► Phase 4 (admin console)            ─► Phase 5 (isolation/RBAC/polish)
```

- **Hard external prerequisites:** Stories 35-2, 35-3, 35-4, 35-7, 35-8, 35-10 must have merged their
  endpoints — this story's UI binds to their real contracts. **Before Phase 2**, confirm the merged
  `/api/v1/billing/*` shapes and the tenant-scoping convention (caller-tenant vs `/orgs/{tenantId}`) and
  reconcile the client + composed reads to match.
- Phase 1 has no UI dependency and can start as soon as the sibling mirrors exist (it reads them).
- Phases 3 and 4 are parallel-safe (different packages); Phase 5 depends on both.

## Risks + mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| Sibling endpoint shapes drift from this story's assumptions | High | Build the TS client against the **merged** contracts; reconcile DTOs in Phase 2 before tabs; isolate the contract in `api/billing.ts` so drift is one-file. |
| Tenant-scoping convention mismatch (`/api/v1/billing/*` vs `/orgs/{tenantId}/...`) | Medium | Inspect the merged sibling billing endpoints first; match exactly; isolation test (5.1) catches leakage. |
| Single-user accidentally exposes Billing | High | Server `capabilities` read is the single gate; fail-safe hide on fetch error; explicit hide tests in both dashboards (2.5, 4.6). |
| Member sees/uses manage controls | High | Defence-in-depth: client guard (cosmetic) + server 403 (real); RBAC tests on both layers (5.1, 5.2). |
| Secret/PAN leakage into the DOM | Critical | Stripe Elements/portal only; BYOK by cabinet handle; no-secret-render DOM assertions (5.3); never log keys/portal URLs. |
| Adding business logic into the dashboard "for convenience" | Medium | Non-goal #1; new endpoints are read-projection only; code review checks for any math in React/new endpoints. |
| Admin overview N+1 over tenants | Low | One composed sum/group read in `BillingAdminReadService` (mirrors `PlatformAnalyticsService`); perf check 5.4. |
| Stripe needed in CI | Low | All tests mock the service seam / endpoint responses; no `STRIPE_SECRET_KEY` required. |

## Acceptance criteria (mirror the story)

- [ ] Tenant Billing area (`packages/dashboard-user/src/pages/billing/`) with Overview/Usage/Invoices/
  Payment/Wallet, route + nav registered in `App.tsx`; Overview shows plan + `BillingMode` toggle (35-2).
- [ ] Usage tab renders platform-vs-BYOK split from `GET /api/v1/billing/usage` (35-3), BYOK labelled
  not-token-billed, with the flush-interval lag note.
- [ ] Quota banner (35-6) and dunning banner (35-8 stage/retry/next-retry) render on the right states.
- [ ] Payment tab: masked PM mirror (35-7), portal redirect, setup-intent add/replace — no PAN/secret in UI.
- [ ] Invoices tab: paged list + detail line-split + Stripe-hosted PDF link (35-8); Tamma serves no bytes.
- [ ] Wallet tab: derived balance + ledger + top-up (35-10).
- [ ] Tenant RBAC: member read-only (no manage controls); owner/admin manage; backend 403 on manage.
- [ ] Admin Billing console tab in `AdminLayout.tsx`: per-tenant subscription + MRR/revenue + total MRR
  (OwnerAccess).
- [ ] Admin catalog-sync (35-1) + webhook (35-5) + reconciliation (35-3) health panel.
- [ ] Admin operator actions: manual credit grant (35-10) + suspend/reinstate (35-8), confirmed, OwnerAccess.
- [ ] No frontend/new-endpoint billing business logic — projection only.
- [ ] Single-user mode hides Billing in both dashboards via `GET /api/v1/billing/capabilities`
  (`ITammaModeProvider`), no build flag.
- [ ] Loading/empty/error states everywhere; p95 page load < 1s (lazy per-tab + paging).
- [ ] No BYOK key value / Stripe secret / PAN ever rendered; BYOK by cabinet handle.
- [ ] Vitest + Testing Library tests (both packages) + xUnit backend/isolation tests cover RBAC,
  usage split, invoices, mode toggle, single-user hide, states, and admin suspend/grant against mocks.
