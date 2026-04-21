# Story 18-5 Implementation Plan — User-Facing Dashboard Shell

**Status**: Planned (2026-04-20)
**Story brief**: [`18-5-user-facing-dashboard-shell.md`](./18-5-user-facing-dashboard-shell.md)
**Team**: Layer 4 Team C (Epic 18 completion)
**Branch**: `feat/story-18-5-user-dashboard`
**Worktree**: `/home/meywd/tamma-worktrees/layer-4-team-c-18-5-user-dashboard`

---

## 1. Objective

Ship a dedicated React SPA at `dash.tamma.dev` for end users (separate
from the operator-focused `app.tamma.dev`). The shell covers: login
/register/verify, onboarding wizard, dashboard home, repo list, run
detail view, and settings pages. Authentication is direct JWT cookie
(no oauth2-proxy); session cookie `tamma_session` is shared across
`*.tamma.dev` so `api.tamma.dev` requests authenticate transparently.
The package is `packages/dashboard-user/`, deployed via the existing
Docker + nginx pipeline with a new subdomain cert.

## 2. Dependencies

Hard blockers:

- **Story 18-2** (login / session / refresh endpoints).
- **Story 18-3** (org creation endpoints).
- **Story 18-4** (GitHub App onboarding endpoints).
- **Hardening task 8** (email / verify-email endpoint) — needed for the
  profile flow.
- **Story 28-9** (JWT tenantId claim + `/auth/switch-org`) — the org
  switcher calls this.
- Cloudflare DNS + a wildcard cert or fresh cert for `dash.tamma.dev`.

Soft:

- **Story 27-5** (prompt store tenant UI) — depends on this story's
  shell but is independent code.
- **Story 29-5** (tenant secret UI) — same.

## 3. Files to create

### New package skeleton

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/packages/dashboard-user/package.json` | pnpm workspace member; deps: React 18, React Router v7, Vite 6, Tailwind 4, Zustand 5, @tanstack/react-query 5. |
| `/home/meywd/tamma/packages/dashboard-user/vite.config.ts` | Vite build; entry `src/main.tsx`; outputs `dist/`. |
| `/home/meywd/tamma/packages/dashboard-user/tsconfig.json` | extends root with `jsx=react-jsx`, `paths` to `@tamma/shared`. |
| `/home/meywd/tamma/packages/dashboard-user/tailwind.config.ts` | extends admin dashboard config; same design tokens. |
| `/home/meywd/tamma/packages/dashboard-user/index.html` | SPA root. |
| `/home/meywd/tamma/packages/dashboard-user/src/main.tsx` | React Router v7 provider + root render. |
| `/home/meywd/tamma/packages/dashboard-user/src/App.tsx` | Router tree + React Query provider. |

### Auth + layout

| Absolute path | Purpose |
|---|---|
| `.../src/api/client.ts` | Fetch wrapper with credentials + refresh-on-401. |
| `.../src/api/hooks.ts` | Shared React Query hooks. |
| `.../src/hooks/useAuth.ts` | Login/logout/register/refresh + current-user state. |
| `.../src/guards/AuthGuard.tsx` | Redirects unauthenticated users to `/login`. |
| `.../src/guards/OnboardingGuard.tsx` | Redirects users with incomplete onboarding to `/onboarding`. |
| `.../src/layouts/AppLayout.tsx` | Sidebar + top bar shell. |
| `.../src/components/OrgSwitcher.tsx` | Dropdown of user's orgs; calls `/auth/switch-org`. |
| `.../src/components/Sidebar.tsx` | Nav: Dashboard, Repos, Runs, Settings. |
| `.../src/components/TopBar.tsx` | User menu + notification bell. |

### Pages

| Absolute path | Purpose |
|---|---|
| `.../src/pages/auth/LoginPage.tsx` | Email+password form + "Sign in with GitHub" button. |
| `.../src/pages/auth/RegisterPage.tsx` | Registration form. |
| `.../src/pages/auth/VerifyEmailPage.tsx` | Auto-verifies on mount. |
| `.../src/pages/auth/ForgotPasswordPage.tsx` | Placeholder (future story). |
| `.../src/pages/onboarding/OnboardingLayout.tsx` | Stepper indicator. |
| `.../src/pages/onboarding/CreateOrgStep.tsx` | Org creation. |
| `.../src/pages/onboarding/ConnectGitHubStep.tsx` | Triggers install redirect. |
| `.../src/pages/onboarding/SelectReposStep.tsx` | Repo checkboxes. |
| `.../src/pages/onboarding/FirstRunStep.tsx` | Kicks off first-run + streams progress via SSE. |
| `.../src/pages/DashboardHome.tsx` | Active repos, recent runs, quick stats. |
| `.../src/pages/repos/RepoListPage.tsx` | Table + filters. |
| `.../src/pages/repos/RepoDetailPage.tsx` | Run history + settings. |
| `.../src/pages/runs/RunDetailPage.tsx` | SSE-driven run timeline + logs. |
| `.../src/pages/settings/SettingsLayout.tsx` | Sidebar nav. |
| `.../src/pages/settings/ProfileSettings.tsx` | Name/email/password. |
| `.../src/pages/settings/OrgSettings.tsx` | Org name/slug/plan/members. |
| `.../src/pages/settings/ConnectedAccounts.tsx` | GitHub linkage status. |
| `.../src/pages/settings/NotificationSettings.tsx` | Placeholder. |

### Tests

| Absolute path | Purpose |
|---|---|
| `.../src/pages/**/__tests__/*.test.tsx` | Component tests (React Testing Library + Vitest). |
| `.../e2e/onboarding.spec.ts` | Playwright E2E: register → verify → create org → install GitHub → first-run. |

### Deploy

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/docker/dashboard-user/Dockerfile` | Multi-stage build: `pnpm build` → nginx:alpine serving `dist/`. |
| `/home/meywd/tamma/nginx-proxy/conf.d/dash.tamma.dev.conf` | Nginx server block for `dash.tamma.dev`. |
| `/home/meywd/tamma/docker/docker-compose.yml` | Add `dashboard-user` service. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/pnpm-workspace.yaml` | Add `packages/dashboard-user`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | CORS: allow `https://dash.tamma.dev` with credentials. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Cookies/SessionCookieOptions.cs` | `Domain=.tamma.dev`, `Secure`, `HttpOnly`, `SameSite=Lax`. |
| `/home/meywd/tamma/.github/workflows/deploy.yml` | Build + publish `dashboard-user` Docker image. |
| `/home/meywd/tamma/docs/stories/epic-27/27-5-prompt-store-account-ui.md` | Cross-ref: this story's shell is the host for 27-5 pages. |

## 5. Sequence of changes

### Step 1 — Package scaffold + Vite build (3h)

- Create `packages/dashboard-user/` with minimal `main.tsx` rendering
  "Hello Tamma".
- Configure Vite, Tailwind, TS paths.
- Add to `pnpm-workspace.yaml`.
- `pnpm --filter @tamma/dashboard-user build` produces `dist/`.
- **Commit**: `feat(dashboard-user): package scaffold`.

### Step 2 — API client + auth hook (3h)

- `api/client.ts` wraps `fetch` with `credentials: 'include'` and
  `Accept: application/json`.
- 401 handler: call `POST /api/v1/auth/refresh`; on refresh failure,
  redirect to `/login?redirect=...`.
- `useAuth()` exposes `{ user, login, logout, register, refresh }`
  with React Query state.
- Unit test: refresh-on-401 retries once; second 401 redirects.
- **Commit**: `feat(dashboard-user): API client + auth hook`.

### Step 3 — Router + guards (2h)

- `App.tsx` with React Router v7 route config.
- `AuthGuard` redirects to `/login` when `user` is null.
- `OnboardingGuard` fetches `/api/v1/onboarding/status` and redirects
  to the appropriate step if incomplete.
- **Commit**: `feat(dashboard-user): router + auth guards`.

### Step 4 — Auth pages (4h)

- `LoginPage`: form → `POST /auth/login` → redirect to `/`.
- `RegisterPage`: form → `POST /auth/register` → redirect to `/verify-email`.
- `VerifyEmailPage`: reads `?token=` → `POST /auth/verify-email` → redirect to `/onboarding`.
- "Sign in/up with GitHub" button → redirect to `GET /auth/github`.
- Component tests with MSW-mocked API.
- **Commit**: `feat(dashboard-user): auth pages`.

### Step 5 — Layout + OrgSwitcher (3h)

- `AppLayout` with `Sidebar` + `TopBar`.
- `OrgSwitcher` lists memberships from `/auth/me`; on pick, calls
  `POST /auth/switch-org`; on 200, re-fetches user state.
- Responsive: sidebar collapses to hamburger at <1024px.
- **Commit**: `feat(dashboard-user): layout + org switcher`.

### Step 6 — Onboarding wizard (6h)

- `OnboardingLayout` stepper: derive current step from status API.
- `CreateOrgStep`: form → `POST /orgs` → advance.
- `ConnectGitHubStep`: button → `GET /onboarding/install-github`
  (full redirect, not fetch).
- `SelectReposStep`: list from `/orgs/:tid/repos` → activate selected.
- `FirstRunStep`: button → `POST /orgs/:tid/repos/:rid/first-run` →
  subscribe SSE for progress → on success, mark onboarding complete
  and redirect to `/`.
- Component tests for each step.
- **Commit**: `feat(dashboard-user): onboarding wizard`.

### Step 7 — Dashboard home + repo pages (4h)

- `DashboardHome`: three widgets, each its own React Query hook.
- `RepoListPage`: table with search + status filter.
- `RepoDetailPage`: run history + settings panel.
- **Commit**: `feat(dashboard-user): home + repo pages`.

### Step 8 — Run detail + SSE (3h)

- `RunDetailPage` fetches base run + opens SSE to
  `/api/v1/runs/:id/events`.
- Renders step timeline, live logs, final diff viewer.
- **Commit**: `feat(dashboard-user): run detail + SSE updates`.

### Step 9 — Settings pages (4h)

- `ProfileSettings`, `OrgSettings`, `ConnectedAccounts`,
  `NotificationSettings` (placeholder).
- RBAC: member-only actions hidden when `userRole=viewer`.
- **Commit**: `feat(dashboard-user): settings pages`.

### Step 10 — Deployment (4h)

- Dockerfile builds SPA, serves via `nginx:alpine`.
- nginx config: `dash.tamma.dev.conf` with SPA fallback + `/api/`
  proxy pass to `tamma-api`.
- Docker Compose: add service + volume mount.
- Cloudflare DNS A record for `dash.tamma.dev`.
- Cookie config: verify `Domain=.tamma.dev` on session set.
- CORS config: add `https://dash.tamma.dev` to allow list.
- Manual smoke: login on dash.tamma.dev, navigate to api.tamma.dev in
  browser tab, assert auth carries.
- **Commit**: `feat(deploy): dash.tamma.dev subdomain`.

### Step 11 — E2E test (4h)

- Playwright spec: register → verify → create org → install GitHub
  (mock) → select repos → first-run → land on `/`.
- Runs nightly on CI.
- **Commit**: `test(e2e): onboarding happy path`.

## 6. Test strategy

### Unit (Vitest + RTL)

- `useAuth` — login, logout, refresh-on-401, state transitions.
- Each page component — render, interaction, MSW-mocked API.
- Guards — redirect semantics (MemoryRouter fixtures).

### Integration

- Component-level API integration with MSW: onboarding wizard
  advances through steps based on mocked status API responses.

### E2E (Playwright)

- Onboarding happy path (nightly + PR-gated).
- Org switcher atomic handover (log in, switch org, verify new org
  is reflected in header and repo list).

### Accessibility

- Run axe-core on every page in CI (catches missing labels, contrast,
  keyboard traps).

## 7. Rollback plan

- **Feature flag**: none needed — the new subdomain is isolated from
  production `app.tamma.dev`. Rolling back means stopping the
  `dashboard-user` Docker service.
- **DNS rollback**: delete the Cloudflare record or point it back to
  a 404 page.
- **Session cookie risk**: changing cookie `Domain` to `.tamma.dev`
  affects all existing `app.tamma.dev` sessions. Mitigate by forcing
  all users to re-login on deploy (acceptable for non-production).
- **Non-reversible**: none — pure additive UI + infra.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Package scaffold | 3 |
| 2. API client + auth | 3 |
| 3. Router + guards | 2 |
| 4. Auth pages | 4 |
| 5. Layout + OrgSwitcher | 3 |
| 6. Onboarding wizard | 6 |
| 7. Home + repo pages | 4 |
| 8. Run detail + SSE | 3 |
| 9. Settings | 4 |
| 10. Deployment | 4 |
| 11. E2E | 4 |
| **Total** | **40** (matches brief) |

## 9. Open questions

- **Shared UI components**: should common components (Card, Badge,
  Toggle, FormField) be extracted into `packages/ui/`? Brief says
  "optional for MVP, follow-up later". Plan: inline-copy first, refactor
  in a later story once the admin dashboard converges.
- **React Router v7**: stable or experimental? At 2026-04-20, v7 is
  stable; Vite 6 compatibility verified. Confirm the exact minor
  pinning (`^7.4.0` or similar).
- **SSE behind nginx**: default nginx config buffers SSE. `proxy_buffering off`
  and `proxy_read_timeout 3600s` required in the conf. Documented in
  step 10 but needs operator verification.
- **Cookie domain `Tamma.dev` vs `.tamma.dev`**: RFC 6265 strips the
  leading dot. Plan: set `Domain=tamma.dev`. Verify that all browsers
  interpret this the same way for subdomain sharing (they do; leading
  dot was deprecated).
- **CORS credentials**: must enumerate allowed origins; no wildcard.
  List: `https://dash.tamma.dev`, `https://app.tamma.dev`,
  `http://localhost:3001`, `http://localhost:3002`.
- **Bundle size budget**: target first-load < 200 KB gzip. React 18 +
  Router + Tailwind should fit; Tanstack Query adds ~12 KB. If over,
  lazy-load settings pages via `React.lazy`.
- **Accessibility sign-off**: axe-core auto-checks are a baseline, not
  proof. Book a 2-hour manual review with a screen-reader user before
  marking "done". Not in scope for this story's hour budget.
