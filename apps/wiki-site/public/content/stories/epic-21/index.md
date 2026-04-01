---
title: "Epic 21: Marketing Site & User Dashboard"
---

## Overview

**Goal**: Build a production-quality marketing site at tamma.dev and a user-facing dashboard at app.tamma.dev/user/ that together serve as the public face and self-service portal of the Tamma SaaS platform.

**Value Delivered**:
- SEO-optimized marketing site with landing, pricing, docs, and blog pages driving organic acquisition
- User dashboard for managing connected repos, viewing workflow runs, adjusting settings, and handling billing
- Clear separation between marketing (static/SSG) and admin dashboard (SPA) concerns
- Stripe-integrated billing with plan comparison and self-service portal

## Current State

| Asset | Status | Framework | Notes |
|-------|--------|-----------|-------|
| Marketing site (`apps/marketing-site/`) | Exists — static HTML served via Cloudflare Workers | Vanilla HTML/CSS + Wrangler | Has hero, features, how-it-works, email signup via KV; no pricing, docs, or blog pages |
| Admin dashboard (`packages/dashboard/`) | Exists — React SPA with Vite | React 18 + react-router-dom + Zustand + Tailwind 4 | Admin-only: knowledge base, agents, security, budget, prompts, provider health |
| User dashboard | Does not exist | — | No user-facing views for repos, runs, billing, or settings |

### Marketing Site Gaps
- No pricing page or plan comparison
- No documentation / getting-started section
- No blog or changelog
- No Stripe checkout integration
- Current site is hand-coded HTML — difficult to maintain at scale

### User Dashboard Gaps
- Existing dashboard is admin-only (settings, agents, prompts, health)
- No "My Repos" view showing connected repositories
- No workflow run history or log viewer for individual users
- No billing or subscription management
- No user profile or API key management outside admin panel

## Target Architecture

```
tamma.dev (marketing — SSG)
├── /              Landing page (hero, features, how-it-works, testimonials, CTA)
├── /pricing       Plan comparison, FAQ, Stripe checkout
├── /docs          Getting started, CLI reference, API docs, GitHub App setup
├── /blog          Changelog, tutorials, announcements
└── /legal         Terms, privacy policy

app.tamma.dev (React SPA)
├── /              Admin dashboard (existing — knowledge base, settings, admin)
├── /user/repos        Connected repositories
├── /user/runs         Workflow run history + logs
├── /user/settings     Profile, org, API keys
└── /user/billing      Subscription, invoices, Stripe portal
```

### Marketing Site Technology Decision

The existing marketing site is vanilla HTML on Cloudflare Workers. For the expanded scope (pricing, docs, blog), the site should migrate to **Astro** for SSG with the following rationale:

- **Astro** generates static HTML with zero JS by default — ideal for SEO and Cloudflare Pages
- Supports MDX for docs and blog content with frontmatter-based routing
- Component islands allow interactive elements (pricing toggle, search) without full-page hydration
- Cloudflare Pages adapter available (`@astrojs/cloudflare`)
- Existing HTML/CSS can be ported incrementally as Astro components

Alternatively, **Next.js** with `output: 'export'` could be used if team familiarity is higher, but Astro is lighter for a content-heavy site.

### User Dashboard Integration

The user dashboard lives inside the existing `packages/dashboard/` React SPA, added as new routes under `/user/`. This avoids a separate deployment and shares the auth session (`tamma_session` JWT + `_oauth2_proxy` cookie from Epic 16).

## Roles & Permissions

| Page | member | admin | owner |
|------|--------|-------|-------|
| Marketing site (all pages) | Public | Public | Public |
| /user/repos | Own repos | All repos | All repos |
| /user/runs | Own runs | All runs | All runs |
| /user/settings | Own profile + keys | Org settings | Org settings + danger zone |
| /user/billing | View plan | Manage plan | Manage plan + cancel |

## Stories

| Story | Title | Priority | Dependencies | Status |
|-------|-------|----------|-------------|--------|
| 21.1 | Marketing Landing Page | P0 (Critical) | None | Planned |
| 21.2 | Pricing Page + Stripe Checkout | P1 (High) | Story 21.1 | Planned |
| 21.3 | Documentation Site | P1 (High) | Story 21.1 | Planned |
| 21.4 | User Dashboard — Repos & Workflow Runs | P0 (Critical) | Epic 16 (auth) | Planned |
| 21.5 | User Dashboard — Settings & Billing | P1 (High) | Story 21.4 | Planned |

## Dependency Graph

```
Story 21.1 (marketing landing page)
  |
  +---> Story 21.2 (pricing + Stripe)
  |
  +---> Story 21.3 (documentation site)

Epic 16 (unified auth, RBAC)
  |
  +---> Story 21.4 (user dashboard — repos & runs)
          |
          +---> Story 21.5 (user dashboard — settings & billing)
```

## Estimated Total Effort

| Story | Estimate |
|-------|----------|
| 21.1 Marketing Landing Page | 20 hours |
| 21.2 Pricing Page + Stripe Checkout | 24 hours |
| 21.3 Documentation Site | 28 hours |
| 21.4 User Dashboard — Repos & Workflow Runs | 32 hours |
| 21.5 User Dashboard — Settings & Billing | 24 hours |
| **Total** | **128 hours** |

## Host Constraints

- **Marketing site**: Deployed to Cloudflare Pages (free tier, global CDN, automatic SSL)
- **User dashboard**: Served from existing `packages/dashboard/` SPA on app.tamma.dev (Hetzner VPS via nginx)
- **Stripe webhooks**: Routed through `api.tamma.dev/webhooks/stripe` to the Tamma API

---

**Last Updated**: 2026-03-28
**Epic Owner**: Product & Frontend Engineering
