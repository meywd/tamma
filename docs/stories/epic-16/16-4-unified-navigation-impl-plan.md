# Story 16-4: Unified Navigation Header — Implementation Plan

## Overview

Ship a single navigation bar that appears on top of every Tamma-facing UI — the React dashboard (`app.tamma.dev`), ELSA Studio (`elsa.tamma.dev`), and OpenSearch Dashboards (`logs.tamma.dev`) — so users see consistent cross-links, their identity, and a sign-out control regardless of which service they are currently viewing.

The delivery mechanism is hybrid by necessity, driven by how much control we have over each host:

- **First-party (React dashboard)**: `packages/dashboard/src/components/layout/NavHeader.tsx` imports the nav directly and is rendered inside `AppLayout`. This path is **already wired** (see the existing file); this plan makes it production-ready (auth wiring, accessibility, admin-role gating, tests).
- **Third-party (OpenSearch Dashboards, upstream ELSA Studio)**: nginx `sub_filter` injects a single `<script>` tag into the upstream HTML response pointing at `https://app.tamma.dev/tamma-nav.js`. That script fetches `/tamma-nav.html` and inlines it into `document.body`. The inline script self-wires active-state detection, user fetch, and the sign-out handler. This path is **partially wired** — the static assets exist under `docker/nav-header/`, and nginx already serves them and injects into `logs.tamma.dev`. This plan adds injection for `elsa.tamma.dev`, locks down CORS, and adds tests.

Both paths fetch user identity from `GET /api/auth/me` (already implemented in `packages/api/src/routes/auth/me-route.ts` — PR #328), which returns `{ user: { id, username, githubId, role } }` by verifying the `tamma_session` JWT cookie. Because the cookie is set with `domain=.tamma.dev`, it is transmitted cross-subdomain automatically.

Nav JS budget: **< 20KB gzipped**. Cached aggressively (`Cache-Control: public, max-age=300`) with no versioning for now — cache bust via config reload.

---

## Step-by-Step Implementation Tasks

### Task 1: Harden the Static Nav Assets (3 hours)

**File to modify**: `docker/nav-header/tamma-nav.html`
**File to modify**: `docker/nav-header/tamma-nav.js`
**File to create**: `docker/nav-header/tamma-nav.css` (extract inline styles for caching)

The current `tamma-nav.html` inlines styles inside a `<style>` block. Keep it inlined (simpler deployment, one file fetch) but audit for:

1. **Accessibility**
   - Replace `<div id="tamma-nav">` with `<nav id="tamma-nav" role="navigation" aria-label="Tamma services">`
   - Add a visually-hidden skip-to-content link as the first child: `<a href="#main" class="tn-skip">Skip to main content</a>` (shown on focus only)
   - Ensure user menu trigger is `<button>` not `<div>` with `aria-haspopup="menu"`, `aria-expanded` toggled on open/close
   - User menu itself: `role="menu"`, items `role="menuitem"`
   - Avatar `<img>` gets `alt=""` when username already rendered adjacent (avoid screen reader duplication)
   - Keyboard: Escape closes menu, Tab cycles through menu items, focus returns to trigger on close

2. **Active state robustness**
   - Current implementation compares `window.location.hostname` to a static map. Extend to also match `localhost` → `app` for local dev.
   - Add `aria-current="page"` on the active link.

3. **Role gating**
   - Admin link is hidden by default (`style="display:none"` or CSS class) and only shown after `/api/auth/me` returns `role === 'admin' || role === 'owner'`. This is already partially wired — verify and add a test.

4. **Sign-out**
   - Current handler does `fetch('/api/auth/logout', {method:'POST'})` then redirects. Confirm the endpoint path matches what PR #328 exposed. If PR #328 uses `/api/auth/sign-out` or similar, update.
   - On failure, still redirect to `/login` so the user is not stuck.

5. **Performance**
   - Measure gzipped size of `tamma-nav.html` + `tamma-nav.js`. Target < 20KB combined. Current html is ~7KB raw, js ~1.5KB raw — well under target even raw.
   - Set `defer` on the injected `<script>` so the host page is not blocked.

**Notes**:
- Keep all selectors under `#tamma-nav` to avoid leaking styles into the host page.
- The `body { padding-top: 48px !important; }` hack must stay — it pushes the host's first painted content below the fixed bar. Document why.

---

### Task 2: Extend nginx Injection to elsa.tamma.dev (2 hours)

**File to modify**: `docker/nginx-proxy.conf.template`

The current template already:
- Serves `/tamma-nav.html` and `/tamma-nav.js` from the `app.tamma.dev` server block (lines ~88–102), aliased to `/etc/nginx/nav-header/`.
- Injects the nav script into `logs.tamma.dev` via `sub_filter '</head>' '<script src="https://app.tamma.dev/tamma-nav.js" defer></script></head>';` (line ~422).

Add the same injection to `elsa.tamma.dev`:

```nginx
# elsa.tamma.dev server block, inside location / { ... proxy_pass ... }

# --- Tamma Nav Bar Injection (Story 16.4) ---
proxy_set_header Accept-Encoding "";  # sub_filter requires uncompressed response
sub_filter '</head>' '<script src="https://app.tamma.dev/tamma-nav.js" defer></script></head>';
sub_filter_once on;
sub_filter_types text/html;
```

**Caveats**:
1. Blazor WASM serves `index.html` once and then owns the DOM. The nav bar must survive Blazor hot-reloading. Because the nav is positioned `fixed` and lives outside Blazor's render root, it should persist. Test by navigating between ELSA Studio pages and confirming the bar stays.
2. If the custom `Tamma.Studio` Blazor project (Story 14.1) exists, prefer a native `<TammaNavBar />` Razor component over nginx injection. For now, assume upstream ELSA image is used → nginx injection.
3. Verify `proxy_set_header Accept-Encoding ""` does not meaningfully hurt transfer size — ELSA Studio HTML is small (< 5KB) and gzip at the browser layer still applies for static assets loaded later.

**File to modify**: `docker/nav-header/` mount in `docker-compose.yml` — verify volume mount exists for the nginx-proxy container at `/etc/nginx/nav-header`. If missing, add.

---

### Task 3: First-Party React NavHeader — Production Polish (3 hours)

**File to modify**: `packages/dashboard/src/components/layout/NavHeader.tsx`
**File to modify**: `packages/dashboard/src/components/layout/NavHeader.css`

The file exists and is wired into `AppLayout.tsx`. This task closes the gaps:

1. **Auth source of truth**
   - Current implementation uses `useAuth()` hook from `../../hooks/useAuth.js`. Verify the hook fetches from `/api/auth/me` (not localStorage). If it reads localStorage, refactor so `useAuth` calls `/api/auth/me` on mount, caches the result in a context, and exposes `user`, `isLoading`, `error`, `refetch`.
   - Add a React context (`AuthContext`) at the `AppLayout` level so nav + other components share one fetch.

2. **Admin link gating**
   - Current code filters out non-app services for non-admins. Revisit: per AC-2, **all users** should see Dashboard/Workflows/Logs. Only the **Admin** link is gated by role (AC-5). Fix the filter to always show `ALL_SERVICES` and only conditionally render the `Admin` link.

3. **Active state**
   - Current `isActiveService` matches on `hostname`. For `localhost` dev it short-circuits `app`. Add `aria-current="page"` on the active link.

4. **Accessibility**
   - Wrap links in `<nav aria-label="Tamma services">`.
   - User menu trigger must be a `<button>` with `aria-haspopup="menu"`, `aria-expanded={menuOpen}`, and Escape key handling.
   - Add `role="menu"` and `role="menuitem"` to the dropdown.
   - Add a skip link targeting `#main-content` — update `AppLayout` to put `id="main-content"` on the `<main>` element.

5. **Responsive layout**
   - At `< 640px`, collapse service labels to icons (or a hamburger). Current CSS hides `.tn-username` on mobile but keeps all nav labels visible, which overflows. Add a CSS `@media` rule or a condensed state.

6. **Sign-out**
   - POST `/api/auth/logout` with `credentials: 'include'`, then redirect to `/login`. Confirm the actual endpoint path matches the one registered in `packages/api/src/routes/auth/`. If mismatch, fix.

---

### Task 4: Shared Color Tokens and Theme Consistency (1 hour)

**File to modify**: `docker/nav-header/tamma-nav.html` (inline CSS)
**File to modify**: `packages/dashboard/src/components/layout/NavHeader.css`

Extract the nav color palette into one source of truth and duplicate (manually, since the injected nav has no build step) into both:

```
--tn-bg:        #7B61FF   /* Tamma purple */
--tn-bg-hover:  rgba(255,255,255,0.15)
--tn-bg-active: rgba(255,255,255,0.20)
--tn-fg:        #ffffff
--tn-fg-muted:  rgba(255,255,255,0.80)
--tn-menu-bg:   #ffffff
--tn-menu-fg:   #333333
```

Both copies must render identically. Add a comment at the top of each file: `/* Token source: docs/stories/epic-16/16-4-unified-navigation-impl-plan.md — keep in sync */`.

Dark/light mode is **deferred** for this story — current target is a single purple bar on both sites. Dark mode flag can be added in a follow-up story if needed.

---

### Task 5: CORS Configuration (1 hour)

**File to verify**: `packages/api/src/server.ts` (or wherever CORS is registered)
**File to verify**: nginx `app.tamma.dev` server block — the existing `/tamma-nav.html` and `/tamma-nav.js` locations set `Access-Control-Allow-Origin "*"`.

1. **Static nav assets**: confirm `Access-Control-Allow-Origin "*"` is set (already is per the template). No credentials needed for the script itself.

2. **`/api/auth/me` cross-origin**: the nav script fetches this from `elsa.tamma.dev` and `logs.tamma.dev`. The Tamma API's CORS config must allow these origins **with credentials**:

```typescript
await app.register(cors, {
  origin: [
    'https://app.tamma.dev',
    'https://elsa.tamma.dev',
    'https://logs.tamma.dev',
  ],
  credentials: true,
});
```

Wildcard (`*`) does NOT work with `credentials: true` per the CORS spec — must be an explicit allowlist.

3. **`/api/auth/logout`**: same CORS rules apply, since the inline nav script POSTs it cross-origin from ELSA/Logs.

---

### Task 6: Tests (3 hours)

**File to create**: `packages/dashboard/src/components/layout/NavHeader.test.tsx`

| # | Test | Assertion |
|---|------|-----------|
| 1 | Renders all three service links for authenticated user | Dashboard, Workflows, Logs visible |
| 2 | Active link has `aria-current="page"` | When hostname matches, that link is marked |
| 3 | Admin link hidden for `member` role | `queryByText('Admin')` returns null |
| 4 | Admin link visible for `admin` role | `getByText('Admin')` present |
| 5 | Admin link visible for `owner` role | `getByText('Admin')` present |
| 6 | User menu button has correct ARIA | `aria-haspopup="menu"`, `aria-expanded="false"` initially |
| 7 | Clicking user menu toggles `aria-expanded` | Becomes `"true"` after click |
| 8 | Escape key closes open user menu | Menu closes, focus returns to button |
| 9 | Outside click closes menu | Menu hidden after `mousedown` on document |
| 10 | Sign-out calls `POST /api/auth/logout` then redirects | Mocked fetch called, `window.location.href` set to `/login` |
| 11 | Sign-out redirects even when fetch fails | `.finally` block fires |
| 12 | Renders without crashing when user is null | Nav renders with service links but no user menu |

**File to create**: `packages/api/src/routes/auth/me-route.test.ts` (if missing — verify PR #328 shipped tests)

| # | Test | Assertion |
|---|------|-----------|
| 13 | Returns `{ user }` with valid JWT cookie | 200, payload shape |
| 14 | Returns 401 when no cookie | Error message |
| 15 | Returns 401 when JWT invalid | Error message |
| 16 | Returns 401 when JWT expired | Error message |

**File to create**: `docker/nav-header/smoke-test.sh`

Shell-based smoke tests runnable against a deployed environment:

```bash
#!/usr/bin/env bash
set -euo pipefail
BASE="${TAMMA_BASE:-https://app.tamma.dev}"

# 1. Nav assets serve with correct CORS
curl -sfI -H "Origin: https://elsa.tamma.dev" "$BASE/tamma-nav.js" \
  | grep -qi 'access-control-allow-origin'

# 2. Nav HTML contains the expected nav element
curl -sf "$BASE/tamma-nav.html" | grep -q 'id="tamma-nav"'

# 3. Nav script is valid JS (no syntax errors)
curl -sf "$BASE/tamma-nav.js" | node --check -

# 4. Nav injected into elsa.tamma.dev
curl -sfL https://elsa.tamma.dev/ | grep -q 'tamma-nav.js'

# 5. Nav injected into logs.tamma.dev
curl -sfL https://logs.tamma.dev/ | grep -q 'tamma-nav.js'

# 6. Auth me endpoint reachable and returns 401 without cookie
curl -s -o /dev/null -w '%{http_code}' "$BASE/api/auth/me" | grep -q '401'
```

**Total tests**: ~16 unit + 6 smoke-test steps.

---

### Task 7: Integration with Story 16-3 Admin Dashboard (1 hour)

**File to modify**: `packages/dashboard/src/components/layout/NavHeader.tsx`

Story 16-3 (admin dashboard) ships an admin UI mounted at `/admin`. Requirements:

1. Admin link in the nav must route to `/admin` on `app.tamma.dev` — full URL `https://app.tamma.dev/admin` so it also works when the user is on `elsa.*` or `logs.*`.
2. Admin link is visible only if `user.role === 'admin' || user.role === 'owner'` (AC-5). This is the same gating used in the injected nav.
3. When the user is already on `/admin`, the Admin link should carry `aria-current="page"`. This requires `NavHeader` to inspect `window.location.pathname`, not just `hostname`.

Verify: if Story 16-3 has not yet landed, the Admin link still renders but routes to a 404. Document this in the story dependency section and open a tracking issue to wire it up post-16-3.

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `docker/nav-header/tamma-nav.css` | Extracted CSS (optional; may stay inline) |
| 2 | `docker/nav-header/smoke-test.sh` | Curl-based smoke tests for deployed env |
| 3 | `packages/dashboard/src/components/layout/NavHeader.test.tsx` | React unit tests |
| 4 | `packages/api/src/routes/auth/me-route.test.ts` | Auth endpoint tests (if missing) |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `docker/nav-header/tamma-nav.html` | Accessibility, button semantics, ARIA, skip link |
| 2 | `docker/nav-header/tamma-nav.js` | Prevent double-injection, confirm endpoints |
| 3 | `docker/nginx-proxy.conf.template` | Add `sub_filter` injection for `elsa.tamma.dev` |
| 4 | `packages/dashboard/src/components/layout/NavHeader.tsx` | Fix service filter, admin gating, ARIA, escape key |
| 5 | `packages/dashboard/src/components/layout/NavHeader.css` | Responsive breakpoint, shared color tokens |
| 6 | `packages/dashboard/src/components/layout/AppLayout.tsx` | Add `id="main-content"` to `<main>` for skip link |
| 7 | `packages/dashboard/src/hooks/useAuth.ts` | Ensure it fetches from `/api/auth/me`, not localStorage |
| 8 | `packages/api/src/server.ts` | CORS allowlist for subdomain origins with credentials |

---

## Dependencies

- **Story 16.1** (oauth2-proxy / GitHub OAuth) — sets the `tamma_session` cookie with `domain=.tamma.dev` so nav can fetch `/api/auth/me` cross-subdomain. **Already landed (PR #328).**
- **Story 16.3** (admin dashboard) — Admin link destination. If not yet landed, the link is still rendered for admin users but routes nowhere useful. No hard blocker.
- **Story 14.1** (custom Tamma.Studio Blazor) — optional. If present, the nav can be a native Razor component. Otherwise, nginx injection is the fallback.
- **Story 15.1** (OpenSearch Dashboards deployment) — the `logs.tamma.dev` service must be reachable for end-to-end verification.

---

## Performance & Operational Notes

- **Bundle size**: target < 20KB gzipped for `tamma-nav.html` + `tamma-nav.js` combined. Measure with `gzip -c docker/nav-header/tamma-nav.{html,js} | wc -c` before and after changes.
- **Caching**: assets served with `Cache-Control: public, max-age=300` (5 minutes). For cache busting during development, append a querystring: `tamma-nav.js?v=<git-sha>`. In production, rely on the 5-minute TTL.
- **Cross-subdomain cookie**: `tamma_session` is set by the GitHub OAuth callback (PR #328) with `Domain=.tamma.dev`, `HttpOnly`, `Secure`, `SameSite=Lax`. The nav script calling `/api/auth/me` with `credentials: 'include'` will attach it automatically across all `*.tamma.dev` subdomains.
- **Graceful degradation**: if `/api/auth/me` is unreachable (API down, network error), the nav bar still renders with service links — just no username or admin link. This is intentional; nav must not block host page functionality.

---

## Estimated Effort

| Task | Hours |
|------|-------|
| Harden static nav assets (a11y, semantics) | 3 |
| nginx injection for elsa.tamma.dev | 2 |
| React NavHeader production polish | 3 |
| Shared color tokens | 1 |
| CORS configuration | 1 |
| Tests (16 unit + smoke script) | 3 |
| Story 16-3 integration (admin link) | 1 |
| **Total** | **14 hours** |

Layer-2 Team D estimate was 12 hours; this plan trends slightly higher because the existing static assets need accessibility work that was not originally scoped.

---

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-15 | 1.0 | Initial implementation plan | Architecture Team |
