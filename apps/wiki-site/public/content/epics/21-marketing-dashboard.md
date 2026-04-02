---
title: "Epic 21: Marketing Site & User Dashboard"
sidebar:
  order: 21
---

**Status:** Partially Implemented (1 done, 1 in progress, 3 drafted)
**Stories:** 5 (21-1 through 21-5)
**Estimated Effort:** 128 hours

## Overview

Epic 21 builds a production-quality marketing site at tamma.dev and a user-facing dashboard at app.tamma.dev/user/ that together serve as the public face and self-service portal of the Tamma SaaS platform.

## Current State

| Asset | Status | Framework |
|-------|--------|-----------|
| Marketing site (`apps/marketing-site/`) | Exists -- static HTML via Cloudflare Workers | Vanilla HTML/CSS + Wrangler |
| Admin dashboard (`packages/dashboard/`) | Exists -- React SPA | React 18 + Vite + Zustand + Tailwind 4 |
| User dashboard | Does not exist | -- |

### Gaps
- No pricing page or plan comparison on marketing site
- No documentation / getting-started section
- No blog or changelog
- No user-facing views for repos, runs, billing, or settings

## Goals

1. Build SEO-optimized marketing landing page (migrate to Astro for SSG)
2. Create pricing page with plan comparison and Stripe checkout
3. Build documentation site with getting-started guides and API reference
4. Create user dashboard for connected repos and workflow run history
5. Add user settings and billing management

## Stories

| Story | Title | Priority | Effort | Status |
|-------|-------|----------|--------|--------|
| 21-1 | Marketing Landing Page | P0 (Critical) | 20 hours | Done |
| 21-2 | Pricing Page + Stripe Checkout | P1 (High) | 24 hours | Drafted |
| 21-3 | Documentation Site | P1 (High) | 28 hours | In Progress |
| 21-4 | User Dashboard -- Repos & Workflow Runs | P0 (Critical) | 32 hours | Drafted |
| 21-5 | User Dashboard -- Settings & Billing | P1 (High) | 24 hours | Drafted |

## Key Technical Details

### Target Architecture

```
tamma.dev (marketing -- SSG via Astro)
+-- /              Landing page
+-- /pricing       Plan comparison + Stripe checkout
+-- /docs          Getting started, CLI reference, API docs
+-- /blog          Changelog, tutorials
+-- /legal         Terms, privacy

app.tamma.dev (React SPA)
+-- /              Admin dashboard (existing)
+-- /user/repos    Connected repositories
+-- /user/runs     Workflow run history + logs
+-- /user/settings Profile, org, API keys
+-- /user/billing  Subscription, invoices, Stripe portal
```

### Marketing Site Technology

Recommended migration from vanilla HTML to **Astro** for SSG:
- Zero JS by default -- ideal for SEO and Cloudflare Pages
- MDX support for docs and blog content
- Component islands for interactive elements
- Cloudflare Pages adapter available

### User Dashboard Integration

User dashboard lives inside existing `packages/dashboard/` React SPA as new routes under `/user/`. Shares auth session (`tamma_session` JWT cookie).

### Permissions

| Page | member | admin | owner |
|------|--------|-------|-------|
| Marketing site | Public | Public | Public |
| /user/repos | Own repos | All repos | All repos |
| /user/runs | Own runs | All runs | All runs |
| /user/settings | Own profile | Org settings | Org + danger zone |
| /user/billing | View plan | Manage plan | Manage + cancel |

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| Unified Auth & RBAC | Epic 16 | Auth session for user dashboard |
| Billing & Payments | Epic 20 | Pricing page and billing management |
| GitHub App | Epic 1.5 | Installation data for repos view |
| Dashboard Framework | Epic 5 | User dashboard extends existing SPA |

## Story Files

[Story documents on GitHub](/stories/epic-21/)
