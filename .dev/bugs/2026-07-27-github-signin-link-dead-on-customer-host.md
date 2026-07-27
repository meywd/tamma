# Bug: "Sign in with GitHub" on the customer app is a dead control on dash.tamma.dev

**Date Discovered**: 2026-07-27
**Reporter**: Claude (Epic 45 implementation)
**Severity**: 🟡 Medium
**Status**: 🐛 Open

## 📋 Summary

`LoginPage.tsx` in `packages/dashboard-user` renders a "Sign in with GitHub" button anchored to
`/oauth2/start?rd=%2F`. That path only exists on hosts fronted by oauth2-proxy (`app.tamma.dev`,
`elsa.tamma.dev`, `logs.tamma.dev` — see `docker/nginx-proxy.conf.template`). The Epic 45
customer vhost `dash.tamma.dev` deliberately has **no** `/oauth2/` location (D1 — the customer
app must be reachable anonymously), so on the customer host the click falls through to
`location /` → the SPA container → the `try_files … /index.html` fallback → **the login page
re-renders with no feedback**. A silent dead button, same defect class as Epic 45's six dead
entry doors.

## 🔍 Details

### Affected Components
- Package: `@tamma/dashboard-user`
- File: `packages/dashboard-user/src/pages/auth/LoginPage.tsx` (the `/oauth2/start?rd=%2F` anchor)
- Infra: `docker/nginx-proxy.conf.template` — `dash.tamma.dev` block (no `/oauth2/` location, by design)

### Reproducibility
- [x] Always reproducible (once dash.tamma.dev is live)

## 🔬 Steps to Reproduce

1. Deploy the Epic 45 stack; open `https://dash.tamma.dev/login`.
2. Click "Sign in with GitHub".
3. Browser navigates to `https://dash.tamma.dev/oauth2/start?rd=%2F` → nginx proxies to the SPA
   → SPA fallback serves `index.html` → React router's catch-all renders — nothing happens from
   the user's point of view.

## 💡 Why it was not fixed inside Epic 45

Fixing it is an auth-architecture decision, not a routing fix: GitHub browser sign-in today is
oauth2-proxy's job, and the proxy session is bridged into a `tamma_session` JWT by
`ProxyHeaderAuthMiddleware` on hosts where the proxy fronts the traffic. Options include
(a) pointing the button at `https://app.tamma.dev/oauth2/start?rd=https://dash.tamma.dev/`
(cookie domain `.tamma.dev` makes the session travel, but the rd allow-list must be checked),
(b) adding the `/oauth2/` locations to the dash vhost WITHOUT the `auth_request` gate, or
(c) removing the button from the customer app until GitHub sign-in is a supported customer
flow. Each changes customer auth behaviour; Epic 45's stories scope routing/deployment only,
so it is recorded here instead of half-decided in a story that never mentions it.

## Related

- `docs/stories/epic-45/story-45-5/…` — D1/D2 (no auth_request on the customer vhost)
- `docker/nginx-proxy.conf.template` — the `dash.tamma.dev` server block
- `packages/dashboard-user/src/pages/auth/LoginPage.test.tsx` — pins the current href
