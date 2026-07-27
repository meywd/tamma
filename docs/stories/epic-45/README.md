# Epic 45: Ship the customer application — `packages/dashboard-user` from built to reachable

## Overview

Tamma has two React SPAs. One is deployed. The other is the product.

`packages/dashboard` is the **admin console**: it sits at `app.tamma.dev` behind oauth2-proxy
(`docker/nginx-proxy.conf.template:155-172`), and every operator surface in the platform lives there.

`packages/dashboard-user` is the **SaaS customer application**: login, register, verify-email,
onboarding, alerts, and the Epic 34-9 billing screen with its upgrade modal, entitlement bar and
cost-estimate widget. 3,661 lines of source across 25 files, with 20 test files and 103 passing
tests. It was built in three commits on 2–4 July 2026.

**It has never been deployed.** Its complete wiring outside its own directory is three lines:

| File:line | What |
|---|---|
| `.github/workflows/ci.yml:49-50` | `pnpm --filter @tamma/dashboard-user test` |
| `vitest.config.ts:64` | excluded from the root vitest run (deliberate — it has its own jsdom config) |
| `eslint.config.js:75-76` | the no-raw-`fetch` rule's scope |

There is no `docker/Dockerfile.dashboard-user`, no compose service in any of the four compose files,
no nginx server block, no upstream, no entry in `docker-publish.yml` / `deploy.yml` /
`docker-smoke-test.yml`, no e2e spec, no hostname. Grep for `dash.tamma.dev` across `docker/`,
`.github/` and `docs/architecture/` returns **zero hits** — despite that hostname being the app's own
`package.json:4` description *and* the hardcoded default in `Tamma.Api/Endpoints/GitHubEndpoints.cs:25`.

Someone built the billing UI, wrote tests for it, and stopped immediately before shipping.

## What this epic unblocks

Three planned things are silently waiting on this, and none of them has said so in its own plan:

1. **Story 39-19 — orchestrator chat.** Targets the customer app. Blocked on infrastructure nobody
   has scheduled.
2. **Story 44-6 — tracker UI.** Epic 44's plan places it in `packages/dashboard` *because* the
   customer app is not deployed — putting a customer-facing board in the console customers cannot
   reach. Epic 44's README carries this as open question 1. This epic answers it.
3. **Epic 34-9's own deliverable.** Plan pricing, the upgrade modal and the entitlement bar are
   shipped code no customer can open. Either customers are changing plans some other way, or plan
   self-service does not exist in the product. It is the latter.

## The audit — is it actually finished?

The product owner did not know. So this epic was scoped from an audit, not an assumption. The audit
ran on 2026-07-27 against the working tree.

### Verdict: **mostly finished, with a named gap list that is bigger than deployment**

Every screen that exists is real — no stubs, no TODOs, no placeholder pages. Every one of the **25
HTTP routes the app calls exists in the C# API** and is reachable. But the app's *entry points* — the
URLs the API emails to customers — land nowhere, and that is not a deployment problem.

### What is genuinely finished

- **All 25 endpoints exist.** Verified route-by-route against `apps/tamma-elsa/src/Tamma.Api/Program.cs`:
  auth (`:1838-1859`), tenant alerts and alert-channels (`:2428-2445`), dashboard summary/runs/stats
  (`:2396-2400`), onboarding platforms/install/installations (`:2523-2528`), pricing
  entitlements/plans/estimate/subscribe (`:2265-2283`). No missing routes, no prefix mismatches, no
  orphaned `MapXxxEndpoints` extension. **A call to a nonexistent endpoint would have been the
  clearest possible "unfinished" signal, and there is not one.**
- **Auth works against the real surface.** `useAuth.tsx:47,78,86,100` binds `/api/auth/me`,
  `/api/v1/auth/login`, `/api/v1/auth/register`, `/api/auth/logout` — all present with the policies
  the guards assume. `VerifyEmailPage.tsx:17,27` reads `?token=` from the URL and POSTs it in the
  **body**, which is exactly what `AuthEndpoints.VerifyEmail` binds
  (`Dtos/Auth/AuthDtos.cs:34` — `VerifyEmailRequest(string Token)`). No mismatch.
- **The billing screens bind real pricing endpoints.** `api/pricing.ts:130-149` → the `/api/pricing`
  group (`Program.cs:2265`), with `subscribe` correctly gated `SettingsManage` server-side
  (`:2283`) and mirrored client-side (`PlanPricingPage.tsx:38-40`). The estimate endpoint really does
  strip cost-basis and margin (`PricingEndpoints.cs:90-102` projects seven fields and neither of
  those two) — the security AC holds.
- **The tests run, and they pass.** 20 files, 103 tests, all green. See the correction below.
- **`vite build` succeeds** — 289 kB bundle, 46 modules, clean.

### Correction to the record: the tests *do* run in CI

`.dev/findings/dashboard-user-is-the-unshipped-saas-customer-app.md:32-34` states the tests do not
run because `vitest.config.ts:62` excludes them and no workflow supplies the filter. **That is
wrong, and it is backwards.**

- `ci.yml:49-50` **is** the filter line, and it has been there since the app landed.
- The exclusion at `vitest.config.ts:64` is deliberate and correct — the package has its own jsdom +
  jest-dom config, exactly like `packages/dashboard` at `:60`.
- The package with excluded-and-never-run tests is **`packages/dashboard`** — the *deployed* admin
  app, ~449 tests, excluded at `vitest.config.ts:60` with no CI filter line anywhere. That is Story
  44-6's finding, and the customer app inherited the blame for it.

The finding has been corrected. **Nothing in this epic is needed to turn the customer app's tests on.**

### What is genuinely unfinished

**Gap 1 — every URL the API emails to a customer lands nowhere.** This is the largest finding and it
is not a deployment gap; the pages do not exist.

| URL the API generates | Source | Route in `dashboard-user/src/App.tsx`? |
|---|---|---|
| `{Dashboard:Url}/verify?token=` | `AuthEndpoints.cs:31-33` | ❌ the app has `/verify-email`, not `/verify` |
| `{Dashboard:Url}/reset-password?token=` | `AuthEndpoints.cs:36-39` | ❌ **no route, no page** |
| `{Dashboard:Url}/invites/accept?token=` | `OrgEndpoints.cs:361-362` | ❌ **no route, no page** |
| `{Dashboard:Url}/invites/pending?inviteId=` | `OrgEndpoints.cs:501-502` | ❌ **no route, no page** |
| `{Dashboard:Url}/onboarding/success` | `GitHubEndpoints.cs:23,40` | ❌ (admin app has it, `router.tsx:77`) |
| `{Dashboard:Url}/onboarding/error` | `GitHubEndpoints.cs:24,40` | ❌ (admin app has it, `router.tsx:85`) |

Six URLs, zero implemented. And `App.tsx:39-88` has **no catch-all route**, so all six render the
router with nothing matched — a blank page, no 404, no error. A customer who registers today cannot
verify their email even after this app is deployed, because the link goes to `/verify` and the page
is at `/verify-email`.

The backends are all real: `POST /api/v1/auth/password-reset/request` and `/confirm`
(`Program.cs:1850-1851`), `POST /api/v1/orgs/invites/accept` (`Program.cs:2301`). The work is
front-end only, and it is a story, not a line.

**Gap 2 — `Dashboard:Url` is one value doing two jobs, and it currently points at the admin app.**
`docker/docker-compose.yml:257` sets `Dashboard__Url` to `https://app.tamma.dev`. That single value
drives all six links above *and* the CORS allow-list (`Program.cs:1169-1177`, `WithOrigins` — one
origin). So verification and invite emails today point into an oauth2-proxy-gated console, where the
customer hits a GitHub OAuth wall for an account they have not created yet.

**Gap 3 — four dead nav links.** `AppLayout.tsx:24,27,33` links to `/repos`, `/runs` and `/settings`;
`DashboardHome.tsx:64` links to `/onboarding`. None of the four is a route. They are copies of the
*admin* app's routes (`packages/dashboard/src/router.tsx:106,107,58`) — aspirational, not accidental.
Combined with the missing catch-all, every one is a silent blank pane.

**Gap 4 — a client/server contract the tests certify as correct while it is wrong.**
`UpgradePlanModal.tsx:171-172` reads `resp.violations` (`string[]`) to warn a customer about a
downgrade. The server never sends that field: `PricingEndpoints.cs:289` returns
`PlanAssignmentResponse` (`Dtos/Admin/AdminTenantDtos.cs:135-142`) whose field is **`warnings`**, an
array of `{metricKey, currentUsage, newLimit}` objects. `UpgradePlanModal.test.tsx:160` mocks
`violations: ['seats over limit']` — **the test asserts against a server shape that does not exist**,
so it passes and the downgrade warning silently renders nothing in production. Also unshipped:
`version` (server says `planVersion`), `planSlug`, `planName`, `message`.

**Gap 5 — one blocking type error, and nothing in CI would catch it.**
`pnpm --filter @tamma/dashboard-user run typecheck` fails with exactly one error:
`TenantAlertFeed.tsx:63` — `exactOptionalPropertyTypes` on `ListAlertsParams.status`. It never
surfaced because the root `typecheck` script (`package.json:24`) is
`tsc --build packages/shared packages/platforms packages/providers packages/orchestrator packages/cli`
— **neither dashboard is in it**, and no workflow typechecks either. `vite build` does not typecheck,
which is why the build is green and the code is not.

Related: `tsconfig.json:51` excludes `packages/dashboard` from the root project but **not**
`packages/dashboard-user`, so ESLint's type-aware parser (`eslint.config.js:15`) is pointed at a
project with no `jsx` setting and no DOM lib for the customer app's `.tsx` files.

**Gap 6 — no error boundary.** `main.tsx:11-12` renders `<App />` bare. Any render throw blanks the
page with no recovery. The admin app has `AdminErrorBoundary.tsx:19` (mounted only around its lazy
admin subtree, `router.tsx:176-180` — so its coverage is partial too, but it exists).

**Gap 7 — no `public/`.** No favicon, no `robots.txt`, no manifest. `index.html:1-12` has no
`<link rel="icon">`; the admin app has four (`packages/dashboard/index.html:6-9`). `robots.txt`
matters here in a way it never did for the admin app, because `app.tamma.dev` is behind oauth2-proxy
and was never crawlable — a customer-facing host is.

### What is *not* a gap (checked, and fine)

- **No version skew.** Every shared dependency is pinned identically to the admin app — React 19.2.5,
  vite 8.0.10, tailwind 4.2.4, vitest 4.1.5, TypeScript 6.0.3. Scripts are identical.
- **The `VITE_API_URL` / `VITE_API_BASE_URL` name divergence is cosmetic.** Neither variable is set
  anywhere in the repo — not in a Dockerfile, compose file, workflow or `.env`. The admin app works
  by its `'/api'` fallback landing on same-origin nginx (`docker/nginx-dashboard.conf:17-24`). The
  customer app's `''` fallback plus its already-absolute paths (`/api/auth/me`) produces the same
  same-origin result. Both are build-time-only with no runtime injection. Do not "fix" this by
  introducing a runtime config mechanism that neither app has.
- **The API-client divergence is sanctioned.** `dashboard-user/src/api/client.ts` is strictly more
  sophisticated than the admin app's ~20 copies of a private `fetchJSON` — it has a typed error
  hierarchy and single-shot refresh-on-401 (`:88-113`) that the admin app lacks entirely.
  `docs/superpowers/plans/2026-06-17-32-13-agent-management-and-benchmark-dashboards-plan.md:23`
  records per-package convention as deliberate. **Do not port one into the other.**
- **No state library, no code splitting, no dark mode, no shared UI primitives.** All true, all
  absent versus the admin app, and none of them blocks shipping. Deferred, listed below.

### Verdict, stated plainly

**Mostly finished — but not deployment-only.** Roughly 60% of this epic is the infrastructure the
finding identified, and it is exactly as mechanical as the finding said. The other 40% is that the
customer signup journey has six front doors and none of them opens. A deployment-only epic would put
a working billing page behind a registration flow whose verification email 404s. That is worse than
not shipping, because it looks shipped.

## Scope

**In:** the container, the compose service, the vhost, the hostname, the CI/CD path, the six missing
entry points, the `Dashboard:Url` split, the contract fix, the type error and the CI guard rail that
would have caught it.

**Out (deferred, with reasons):**

- **Dark mode / theme store.** The admin app's `index.css` carries a 56-line theming system and a
  `stores/theme-store.ts`. The customer app has a one-line `index.css`. Real, cosmetic, not a
  blocker.
- **Shared UI primitives (`components/common/`).** The customer app has ad-hoc loading strings
  (`AuthGuard.tsx:21-30` renders a literal `"Loading..."`). Extracting primitives across two apps is
  its own piece of work and Epic 43-7 / 44-6 are already contending over the same question.
- **Code splitting.** `App.tsx:21-31` imports every page eagerly. The bundle is 289 kB / 86 kB gzip.
  Not a problem at this size.
- **Fixing `packages/dashboard`'s 68 typecheck errors and turning on its 449 tests.** That is Story
  44-6's, and it is a repair job, not a wiring job. Story 45-0 deliberately does **only** the customer
  app, which is one fix from green.
- **The cross-subdomain nav bar** (`docker/nav-header/`, injected by nginx `sub_filter`). It is an
  operator navigation aid across admin surfaces; a customer should not see links to ELSA Studio and
  OpenSearch.
- **Orchestrator chat (39-19) and the tracker UI (44-6) themselves.** This epic unblocks them; it
  does not build them.

## Stories

| Story | Title | Effort | Blocked by |
|---|---|---|---|
| **45-0** | Guard rails: typecheck the customer app in CI, and the one error that hid behind its absence | 1 d | — |
| **45-1** | The contract the tests certified wrong: `violations` → `warnings`, and the PATCH that skips refresh | 1.5 d | — |
| **45-2** | Entry points: the six API-emailed URLs, the catch-all, and four honest nav links | 2 d | — |
| **45-3** | The missing account pages: password reset, invite accept, invite pending | 4 d | 45-2 |
| **45-4** | `docker/Dockerfile.dashboard-user` + `docker/nginx-dashboard-user.conf` | 1.5 d | — |
| **45-5** | Compose service, `dash.tamma.dev` vhost, TLS and DNS | 2 d | 45-4 |
| **45-6** | Build, push, deploy, verify: `docker-publish.yml`, `deploy.yml`, smoke tests | 2 d | 45-4, 45-5 |
| **45-7** | `Dashboard:Url` split: customer links stop pointing at the admin console | 2 d | 45-3, 45-5 |

**Total: 16 person-days. Critical path: 8 days** (`45-2 → 45-3 → 45-7`). See `EXECUTION-PLAN.md`.

## Decisions

**D1 — The customer app gets its own vhost with no `auth_request`, not a path under
`app.tamma.dev`.** The admin app's server block gates `location /` behind oauth2-proxy
(`nginx-proxy.conf.template:156-157`). The customer app ships its own `/login`, `/register` and
cookie-session `AuthProvider` and must be reachable anonymously. Mounting it under the existing block
would put a GitHub OAuth wall in front of a registration form. A sibling `server { server_name
dash.tamma.dev; }` block with **no** `auth_request` line is the shape.

**D2 — The hostname is `dash.tamma.dev`.** Not invented here: it is already the app's own
`package.json:4` description and already the hardcoded fallback at `GitHubEndpoints.cs:25`
(`DefaultDashboardUrl = "https://dash.tamma.dev"`). Two independent places in the codebase have
already assumed it. Adopting it costs nothing and contradicting it costs a grep.

**D3 — `Dashboard:Url` splits into a customer URL and keeps its name for the admin.** All six of its
link-building consumers are customer-facing (verify, reset, two invite paths, two GitHub-install
redirects); only CORS is shared, and CORS needs both origins regardless. 45-7 introduces
`Dashboard:CustomerUrl` falling back to `Dashboard:Url`, so an unconfigured deployment behaves
exactly as it does today. **The fallback is the whole point** — a single-user self-hosted install has
one dashboard and must not be forced to configure two.

**D4 — The Dockerfile is a simplification of `Dockerfile.dashboard`, not a copy.**
`docker/Dockerfile.dashboard:16,21,24` copies and builds `@tamma/shared` because the admin app
depends on it. The customer app's `package.json:17-21` declares exactly three runtime dependencies —
`react`, `react-dom`, `react-router-dom` — and **no `@tamma/shared`**, and its `tsconfig.json` has no
`references` block. So the shared stage comes out. Everything else — the node:22-alpine build stage,
the corepack/pnpm setup, the nginx:1.27-alpine runtime, the non-root user, the IPv4-literal
healthcheck and its comment — is copied exactly, including the reason.

**D5 — Port 3002, matching the app's own dev-server port.** `dashboard-user/vite.config.ts:19` uses
3002; the admin app uses 3000 dev / 3001 container. 3002 keeps dev and container agreed and does not
collide.

**D6 — 45-0 fixes the customer app's typecheck only.** Turning typecheck on for `packages/dashboard`
means fixing 68 errors — including four in `hooks/useAuth.ts:25,39,45,49` where `AuthState` declares
`logout` and every `setState` omits it, and two in `AccountPage.tsx:46,49` reading a `.email` that
`CurrentUser` does not have. Those are real bugs in the deployed app and they deserve a story, not a
subtask of a shipping epic. **Coordination gate with 44-6**, which claims "typechecking both
dashboards" — whichever runs first takes the customer app; 44-6 keeps the admin app either way.

**D7 — No runtime config injection.** Neither app has it; both are build-time-only and both resolve
to same-origin `/api/` through their own nginx. Adding a `window.__CONFIG__` or an `envsubst` pass
for the customer app would make it the only SPA in the repo with a second config mechanism. If a
future deployment needs the API on a different origin, that is when to add it — and then to both.

## Open questions for the product owner

1. **Does the GitHub App install callback land in the customer app or the admin console?**
   `GitHubEndpoints.cs:23-24` redirects to `/onboarding/success` / `/onboarding/error`, which exist in
   the admin app (`router.tsx:77,85`) and not in the customer app — but the *customer* is the one
   installing the app. 45-2 builds both routes in the customer app; 45-7 decides which host the
   redirect targets. **If the answer is "admin", say so and 45-7 shrinks.**
2. **Is `dash.tamma.dev` correct, or should the customer app take `app.tamma.dev` and the admin
   console move?** D2 takes the low-risk option. The alternative — customers at `app.`, operators at
   `admin.` — is arguably the better long-term naming and is a strictly larger change (it moves a
   live oauth2-proxy'd vhost). Not taken; flagged.
3. **Should the customer app be crawlable?** 45-4 ships a `robots.txt`. Whether it disallows
   everything or allows a future marketing surface is a product call, not an engineering one.
   Defaulting to `Disallow: /`.

## Related

- `.dev/findings/dashboard-user-is-the-unshipped-saas-customer-app.md` — the finding that opened
  this, with its test-coverage claim corrected on 2026-07-27.
- `docs/stories/epic-44/README.md` open question 1, and Story 44-6 — the tracker UI whose placement
  this epic decides.
- Story 39-19 (orchestrator chat), Epic 34-9 (pricing & plan management).
- `packages/dashboard/` — the admin console, and the template every deployment story here cites.
