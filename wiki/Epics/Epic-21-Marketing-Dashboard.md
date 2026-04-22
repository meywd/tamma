# Epic 21: Marketing Site & User Dashboard

**Status:** Partially Implemented (Midnight Ocean marketing redesign live on tamma.dev; 4 stories still drafted/in-progress)
**Stories:** 5 (21-1 through 21-5)
**Estimated Effort:** 128 hours (20 delivered; ~108 remaining)
**Packages:** `apps/marketing-site`, `apps/wiki-site`, `packages/dashboard`

## Overview

Epic 21 is the public-facing half of the Tamma SaaS. It pairs a marketing site at `tamma.dev` (acquisition funnel: hero, pricing, docs, blog, legal) with a user-facing dashboard at `app.tamma.dev/user/*` (self-service: repos, runs, settings, billing). Together they serve every visitor who has not yet signed up, every user managing their own workflows, and every tenant-admin managing organization billing.

The marketing landing page is live — the Midnight Ocean redesign ships as a Cloudflare Worker running vanilla HTML/CSS with a `/api/signup` endpoint backed by Cloudflare KV. The wiki is live at `wiki.tamma.dev`. The pricing page, docs section, user-dashboard repos view, user-dashboard settings/billing view, and Stripe checkout are all still planned.

The admin dashboard at `app.tamma.dev` already exists (Epic 5 / Epic 16) and hosts settings, agents, provider health, knowledge-base management, and — as of 2026-04-22 — the Story 27-4 prompt store admin UI. The user dashboard lives in the **same** React SPA under a `/user/*` route prefix so both surfaces share auth, layout, and the `tamma_session` JWT cookie.

## Architecture

```
tamma.dev   (Cloudflare Worker — marketing-site)
  /                     Landing (hero, features, how-it-works, email signup)
  /pricing              Plan comparison + Stripe checkout         (21-2 planned)
  /docs                 Getting-started, CLI, API reference       (21-3 in-progress)
  /blog                 Changelog, tutorials                      (21-3 scope)
  /legal                Terms, privacy
  /api/signup           Cloudflare KV (email capture, 5/hr/IP)

wiki.tamma.dev  (Cloudflare Worker — wiki-site, Epic 25)
  /epics, /stories, /workflows, /roadmap, /architecture

app.tamma.dev   (Hetzner VPS — packages/dashboard React SPA)
  /                     Admin shell (KB, agents, settings, prompts, health)
  /user/repos           Connected repositories                    (21-4 planned)
  /user/runs            Workflow run history + SSE live status    (21-4 planned)
  /user/runs/:runId     Event timeline, logs, 14-step progress    (21-4 planned)
  /user/settings        Profile, org, API keys                    (21-5 planned)
  /user/billing         Subscription, invoices, Stripe portal     (21-5 planned)

api.tamma.dev   (Fastify on VPS)
  /api/v1/repos         User-scoped repo CRUD
  /api/v1/runs          Workflow run list + detail
  /api/v1/events/stream SSE for live run status
  /webhooks/stripe      Stripe checkout / invoice webhooks
```

All three public surfaces share the root domain `*.tamma.dev` with Cloudflare Full SSL and origin certificates. The SPA and the marketing site share the `tamma_session` JWT cookie on `.tamma.dev`.

## Components

| Layer | Surface | Component | Technology | Responsibility |
|------|---------|-----------|------------|----------------|
| Marketing | Worker | `tamma-marketing-site` (`apps/marketing-site/`) | TS + HTML + Cloudflare KV | Static hosting, signup API, rate limiting, KV writes |
| Marketing | Worker | `functions/signup.ts` | Cloudflare Pages Function | Legacy signup handler |
| Wiki | Worker | `tamma-wiki-site` (`apps/wiki-site/`) | Vite + React 19 + React Router | Delivered under Epic 25 |
| Dashboard | SPA shell | `packages/dashboard/src/router.tsx` | React Router v6 | Route registration, guards |
| Dashboard | Auth | `AdminGuard`, `UserGuard` | React context | RBAC enforcement before mount |
| Dashboard | Admin pages | `pages/admin/*` + `pages/settings/*` | React 18 + Zustand | Existing admin surface (knowledge base, agents, prompts, security) |
| Dashboard | User pages | `pages/user/*` (21-4, 21-5) | React 18 + Zustand | Repos, runs, settings, billing (planned) |
| API | REST | `packages/api/src/routes/repos/*` | Fastify | Repo list / connect / pause / disconnect |
| API | REST | `packages/api/src/routes/runs/*` | Fastify | Workflow run list / detail / logs |
| API | SSE | `/api/v1/events/stream` | Fastify reply.hijack | Real-time run status |
| API | Webhooks | `/webhooks/stripe` | Fastify + Stripe SDK | Checkout, invoice.paid, subscription.updated |
| Payments | External | Stripe Checkout | Stripe-hosted | Plan selection, card capture |
| Payments | External | Stripe Customer Portal | Stripe-hosted | Self-service invoice + card update |

## Class diagram (user-dashboard slice, planned)

```
 UserLayout ────────────── SidebarUser
      │                         │
      │ Outlet                  └── NavItem("Repos", "Runs", "Settings", "Billing")
      ▼
 ┌────────────────┬────────────────┬──────────────────┐
 │                │                │                  │
ReposPage      RunsPage        SettingsPage       BillingPage
 │ useRepos()    │ useRuns()     │ useProfile()      │ useSubscription()
 │ useRepoMutations │ useRunsSSE │ useApiKeys()      │ useInvoices()
 │                │                │                  │
 ├─ RepoCard      ├─ RunsFilter    ├─ ProfileForm     ├─ PlanSummary
 ├─ AddRepoDialog ├─ RunRow        ├─ OrgPanel        ├─ InvoiceTable
 └─ DisconnectConfirm └─ RunDetailPage  ├─ ApiKeyTab  ├─ StripePortalButton
                        ├─ RunTimeline  └─ DangerZone └─ CancelConfirmDialog
                        ├─ RunSteps
                        └─ RunLogs

 api-client (packages/dashboard/src/services/user-api-client.ts)
  ├─ listRepos(), connectRepo(), pauseRepo(), disconnectRepo()
  ├─ listRuns(filters), getRun(id), getRunLogs(id)
  ├─ getProfile(), updateProfile(), listApiKeys()
  └─ getSubscription(), getInvoices(), createPortalSession()
```

The user dashboard reuses the same `ApiClient`, `AuthContext`, and `ErrorBoundary` primitives as the admin shell. New routes mount through `router.tsx`; `UserGuard` short-circuits to `/login` if there is no session cookie, and to `/user/repos` (empty state) if the user has no tenant yet.

## Sequence diagram (landing → signup → dashboard)

```
Visitor                tamma.dev         api.tamma.dev        Stripe         app.tamma.dev
  │                       │                  │                  │                 │
  │ GET /                 │                  │                  │                 │
  │──────────────────────▶│                  │                  │                 │
  │ 200 HTML (SSR static) │                  │                  │                 │
  │◀──────────────────────│                  │                  │                 │
  │                       │                  │                  │                 │
  │ POST /api/signup      │                  │                  │                 │
  │──────────────────────▶│  KV write        │                  │                 │
  │                       │ (rate-limited)   │                  │                 │
  │◀──────────────────────│                  │                  │                 │
  │                       │                  │                  │                 │
  │ GET /pricing → Checkout                  │                  │                 │
  │──────────────────────▶│──────────────────────────────────▶  │                 │
  │                       │                  │  redirect to Stripe                │
  │                       │                  │                  │                 │
  │ Complete payment      │                  │                  │                 │
  │──────────────────────────────────────────────────────────▶ │                 │
  │                       │                  │                  │ webhook         │
  │                       │                  │◀─────────────────│ checkout.done   │
  │                       │                  │  upgrade tenant  │                 │
  │                       │                  │                  │                 │
  │ Redirect → app.tamma.dev/user/repos                         │                 │
  │─────────────────────────────────────────────────────────────────────────────▶ │
  │                       │                  │                  │                 │
  │                       │                  │ GET /api/v1/repos│                 │
  │                       │                  │◀─────────────────────────────────  │
  │                       │                  │──────────────────────────────────▶ │
  │                       │                  │ SSE /api/v1/events/stream          │
  │                       │                  │◀══════════════════════════════════▶│
```

## Use cases

1. **Prospect lands on tamma.dev** and sees Midnight Ocean hero, features, how-it-works, and "Get started free" CTA. Submitting the email form POSTs to `api/signup` which writes to Cloudflare KV with a 5-per-hour IP rate limit. (Live today.)
2. **Prospect browses docs at tamma.dev/docs** — getting-started, CLI reference, API reference, GitHub App setup. (21-3 in progress; see Epic 25 for the separate wiki surface.)
3. **Prospect picks a plan on tamma.dev/pricing**, clicks "Subscribe", is redirected into Stripe Checkout, returns to `app.tamma.dev/user/repos` with a verified session and a provisioned tenant. (21-2 planned.)
4. **Logged-in user opens `/user/repos`** to see their connected GitHub repositories, click "View Runs" on a repo, "Pause" a repo, or "Disconnect" one with a confirm dialog. (21-4 planned.)
5. **Logged-in user opens `/user/runs`**, filters by repo + status + date range, and clicks a row to open the run detail with the DCB event timeline, the 14-step orchestrator progress, logs, and PR link. Live-running workflows pulse in the list via SSE. (21-4 planned.)
6. **Logged-in user manages settings** at `/user/settings`: profile name, org name, personal API keys (create, name, revoke). (21-5 planned.)
7. **Tenant owner manages billing** at `/user/billing`: current plan, usage metrics, invoice history, change plan, and "Open Stripe Portal" for card updates. (21-5 planned.)
8. **Admin user still sees the existing admin shell** (knowledge base, agents, prompts, health) — user routes are additive, no existing surface breaks.

## Permissions

| Page | member | admin | owner |
|------|--------|-------|-------|
| Marketing site (all) | Public | Public | Public |
| `/user/repos` | Own repos | All tenant repos | All tenant repos |
| `/user/runs` | Own runs | All tenant runs | All tenant runs |
| `/user/settings` | Own profile + keys | Org settings | Org + danger zone |
| `/user/billing` | View plan | Manage plan | Manage + cancel |
| `/admin/*` (existing) | Denied | Allowed | Allowed |

## Stories

| Story | Title | Priority | Effort | Status |
|-------|-------|----------|--------|--------|
| 21-1 | Marketing Landing Page (Midnight Ocean) | P0 | 20h | Done |
| 21-2 | Pricing Page + Stripe Checkout | P1 | 24h | Drafted |
| 21-3 | Documentation Site | P1 | 28h | In Progress (wiki live via Epic 25; marketing-side docs pending) |
| 21-4 | User Dashboard — Repos & Workflow Runs | P0 | 32h | Drafted |
| 21-5 | User Dashboard — Settings & Billing | P1 | 24h | Drafted |

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| Unified Auth & RBAC | Epic 16 | `tamma_session` cookie shared across `.tamma.dev` |
| End-User Auth & Registration | Epic 18 | User-dashboard entry after sign-up + GitHub App install |
| Observability Dashboard | Epic 5 | User dashboard extends the existing SPA shell |
| GitHub App | Epic 1.5 | Installation data fuels the repos view |
| Billing & Payments | Epic 20 | Stripe checkout, customer portal, webhook handling |
| Wiki Site | Epic 25 | Shared documentation surface at wiki.tamma.dev |
| Multi-Tenancy | Epic 17 | All user-scoped queries carry tenant context |

## Current state

- **Live**: marketing site at tamma.dev with Midnight Ocean redesign (hero, features, how-it-works, 6-feature grid, 4-step flow, CTA, dark mode, Lighthouse >= 90). Email signup backed by Cloudflare KV with IP rate limiting. Full SEO metadata (Open Graph, Twitter, JSON-LD). wiki.tamma.dev also live via Epic 25.
- **In progress**: documentation site — marketing docs section scope still planned; the standalone wiki at wiki.tamma.dev (Epic 25) covers public documentation today.
- **Drafted**: pricing page with Stripe Checkout (21-2), user dashboard repos + runs (21-4), user dashboard settings + billing (21-5).
- **Deferred**: blog / changelog (will likely land inside the wiki site rather than marketing-site to avoid duplicating markdown renderers).

## See also

- [Epic 5 — Observability Dashboard & Docs](Epic-5-Observability.md) — the existing dashboard framework the user routes extend.
- [Epic 16 — Unified Auth & RBAC](Epic-16-Auth-Admin.md) — session cookie and permissions model.
- [Epic 18 — End-User Auth & Registration](Epic-18-User-Auth.md) — the pre-dashboard onboarding funnel.
- [Epic 20 — Billing & Payments](Epic-20-Billing.md) — Stripe integration consumed by 21-2 and 21-5.
- [Epic 25 — Documentation & Wiki Site](Epic-25-Wiki-Site.md) — the public wiki at wiki.tamma.dev.
- [Roadmap](Roadmap.md) — where this epic sits in the overall plan.

## Story files

[Epic 21 stories on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-21)

---

_Last updated: 2026-04-22_
