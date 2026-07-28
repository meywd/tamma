# Decision: three-host layout — app (users), dash (tenant admins), admin (platform)

**Date**: 2026-07-28
**Decided by**: product owner
**Status**: ✅ Binding for all future stories

## The layout

| Host | Audience | Serves |
|---|---|---|
| `app.tamma.dev` | Normal users (tenant members) | The day-to-day product surface: work, chat (39-19), tracker board (44-6), account pages |
| `dash.tamma.dev` | Tenant admins (`tenant_owner`/`tenant_admin`) | Tenant analytics + tenant admin pages: model settings (46-3), billing/plan management, alert config, member management |
| `admin.tamma.dev` | Platform owner | The oauth2-proxy-gated platform admin console (`packages/dashboard`) |

## Where we are today (transition state)

As of the 2026-07-28 rehost (PR #506): `admin.tamma.dev` carries the platform console;
`app.tamma.dev` AND `dash.tamma.dev` both serve the same `packages/dashboard-user` bundle.
That is deliberate: the customer app's user-facing and tenant-admin-facing pages live in one
bundle today, so both hostnames answering identically is correct until the split exists.

## What future stories must do

1. **New user-facing pages** (chat, tracker board, work views) target `app.tamma.dev` —
   route them in `packages/dashboard-user` as today; when the host split lands they stay on app.
2. **New tenant-admin pages** (analytics, settings/admin surfaces) are dash-destined —
   route them under a recognizable prefix (the existing `/settings/*` convention works) so the
   eventual split is a routing rule, not a rewrite.
3. **The split story itself** (file it when dash-destined pages accumulate): host-aware
   routing or two builds of `dashboard-user`; emailed CUSTOMER links (verify/reset/invites)
   flip canonical from `dash.tamma.dev` to `app.tamma.dev` (`DashboardUrls.DefaultCustomerUrl`)
   because those flows belong to normal users; tenant-admin deep links (e.g. from alert
   emails aimed at admins) become dash links.
4. **Do not** put customer-facing UI in `packages/dashboard` (the platform console) — that
   rule predates this decision and survives it.

## Why

One product surface per audience: members never see admin chrome, tenant admins get a
dedicated analytics/admin home, and the platform console stays behind the OAuth wall on its
own name. The hostname split also lets the tenant-admin surface grow heavier (charts,
analytics queries) without weighing down the member bundle.

## Related

- `docker/nginx-proxy.conf.template` (current vhosts), `Endpoints/DashboardUrls.cs` (canonical
  customer URL — flips to app at split time)
- Epic 44 (44-6 tracker UI → app), Story 39-19 (chat → app), Story 46-3 (model settings →
  dash-destined), Epic 45 (entry/account pages → app; its emailed-link canonical flips at
  split time)
