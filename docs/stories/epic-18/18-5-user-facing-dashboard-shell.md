# Story 18-5: User-Facing Dashboard Shell

Status: planned

## Story

As an **end user**,
I want a dedicated dashboard at `dash.tamma.dev` where I can manage my repositories, view workflow runs, and configure settings,
so that I have a clear interface separate from the admin/operations dashboard.

## Acceptance Criteria

1. **New React application** (or route namespace within the existing dashboard package) served at `dash.tamma.dev`
2. **Login page** at `/login` with email+password form and "Sign in with GitHub" button
3. **Registration page** at `/register` with email, password, name fields and "Sign up with GitHub" button
4. **Email verification page** at `/verify-email?token=<token>` that auto-verifies on load
5. **Onboarding wizard** at `/onboarding` with stepper: Create Org -> Install GitHub App -> Select Repos -> First Run
6. **Dashboard home** at `/` showing: active repos, recent workflow runs, quick stats (success rate, avg duration)
7. **Organization switcher** in the nav bar for users in multiple orgs
8. **Repository list** at `/repos` showing connected repos with status, last run, and toggle for active/inactive
9. **Workflow run detail** at `/runs/:runId` showing step-by-step progress, logs, and generated diffs
10. **Settings pages** at `/settings/*` for: profile, organization, connected accounts, notifications
11. **Responsive layout** works on desktop (1024px+) and tablet (768px+); mobile is not required for MVP
12. **Authentication guard**: All routes except `/login`, `/register`, `/verify-email`, `/forgot-password` require authenticated session
13. **Onboarding guard**: Authenticated users without a completed onboarding are redirected to `/onboarding`
14. **Shared session**: The `tamma_session` cookie (set on `.tamma.dev`) works across `dash.tamma.dev` and `api.tamma.dev`

## Tasks / Subtasks

- [ ] Task 1: Set up the user dashboard application
  - [ ] Subtask 1.1: Create `packages/user-dashboard/` package (or `packages/dashboard/src/user/` namespace within existing dashboard)
  - [ ] Subtask 1.2: Configure Vite build with entry point at `packages/user-dashboard/src/index.tsx`
  - [ ] Subtask 1.3: Set up React Router v7 with route definitions
  - [ ] Subtask 1.4: Configure Tailwind CSS (or share existing config from admin dashboard)
  - [ ] Subtask 1.5: Configure API client pointing to `api.tamma.dev` with credentials (cookies)
  - [ ] Subtask 1.6: Add Docker build stage and nginx config for `dash.tamma.dev`

- [ ] Task 2: Implement authentication pages
  - [ ] Subtask 2.1: Create `LoginPage` component with email+password form
  - [ ] Subtask 2.2: Add "Sign in with GitHub" button that redirects to `GET /api/v1/auth/github`
  - [ ] Subtask 2.3: Create `RegisterPage` component with name, email, password fields
  - [ ] Subtask 2.4: Add "Sign up with GitHub" button (same OAuth flow, creates account if new)
  - [ ] Subtask 2.5: Create `VerifyEmailPage` component that calls `POST /api/v1/auth/verify-email` with token from URL
  - [ ] Subtask 2.6: Create `ForgotPasswordPage` placeholder (implementation in future story)
  - [ ] Subtask 2.7: Implement `useAuth` hook: login, logout, register, refresh, current user state
  - [ ] Subtask 2.8: Implement `AuthGuard` component that redirects unauthenticated users to `/login`
  - [ ] Subtask 2.9: Write component tests for login/register flows

- [ ] Task 3: Implement onboarding wizard
  - [ ] Subtask 3.1: Create `OnboardingLayout` with step indicator (Create Org -> Connect GitHub -> Select Repos -> First Run)
  - [ ] Subtask 3.2: Create `CreateOrgStep` component: org name + slug input, slug preview, submit
  - [ ] Subtask 3.3: Create `ConnectGitHubStep` component: "Install GitHub App" button, waiting state, success confirmation
  - [ ] Subtask 3.4: Create `SelectReposStep` component: checkbox list of repos from installation, activate selected
  - [ ] Subtask 3.5: Create `FirstRunStep` component: select repo, trigger first run, show real-time progress via SSE
  - [ ] Subtask 3.6: Create `OnboardingGuard` that redirects users with incomplete onboarding to `/onboarding`
  - [ ] Subtask 3.7: Fetch onboarding status from `GET /api/v1/onboarding/status` to determine starting step
  - [ ] Subtask 3.8: Write component tests for each step

- [ ] Task 4: Implement dashboard home page
  - [ ] Subtask 4.1: Create `DashboardHome` page component
  - [ ] Subtask 4.2: Build `ActiveReposList` widget: shows activated repos with last run status
  - [ ] Subtask 4.3: Build `RecentRunsList` widget: shows last 10 workflow runs across all repos
  - [ ] Subtask 4.4: Build `QuickStats` widget: success rate, average duration, runs this week
  - [ ] Subtask 4.5: Create API hooks: `useRepos()`, `useRecentRuns()`, `useDashboardStats()`
  - [ ] Subtask 4.6: Write component tests

- [ ] Task 5: Implement repository and run pages
  - [ ] Subtask 5.1: Create `RepoListPage` at `/repos` with filterable/sortable table
  - [ ] Subtask 5.2: Create `RepoDetailPage` at `/repos/:repoId` with run history, settings, activation toggle
  - [ ] Subtask 5.3: Create `RunDetailPage` at `/runs/:runId` with step progress, logs panel, generated diff viewer
  - [ ] Subtask 5.4: Implement SSE subscription for live run updates
  - [ ] Subtask 5.5: Write component tests

- [ ] Task 6: Implement settings pages
  - [ ] Subtask 6.1: Create `SettingsLayout` with sidebar navigation
  - [ ] Subtask 6.2: Create `ProfileSettings` page: name, email, password change, connected accounts
  - [ ] Subtask 6.3: Create `OrgSettings` page: org name, slug, plan, member management (uses 18-3 APIs)
  - [ ] Subtask 6.4: Create `ConnectedAccounts` page: GitHub account linkage status, unlink option
  - [ ] Subtask 6.5: Create `NotificationSettings` placeholder page
  - [ ] Subtask 6.6: Write component tests

- [ ] Task 7: Implement layout and navigation
  - [ ] Subtask 7.1: Create `AppLayout` component: sidebar + top bar + content area
  - [ ] Subtask 7.2: Create `Sidebar` with navigation links: Dashboard, Repos, Runs, Settings
  - [ ] Subtask 7.3: Create `TopBar` with: org switcher, user menu (profile, logout), notification bell
  - [ ] Subtask 7.4: Implement `OrgSwitcher` component: dropdown showing user's orgs, calls `POST /api/v1/auth/switch-org`
  - [ ] Subtask 7.5: Implement responsive layout (sidebar collapses to hamburger on tablet)
  - [ ] Subtask 7.6: Write component tests

- [ ] Task 8: Configure deployment
  - [ ] Subtask 8.1: Add Cloudflare DNS record for `dash.tamma.dev` pointing to VPS
  - [ ] Subtask 8.2: Add nginx server block for `dash.tamma.dev` serving the user dashboard SPA
  - [ ] Subtask 8.3: Configure CORS on API to allow `dash.tamma.dev` origin with credentials
  - [ ] Subtask 8.4: Add to Docker Compose: build stage for user dashboard, nginx config
  - [ ] Subtask 8.5: Test end-to-end login flow across subdomains

## Technical Context

### Existing Code Reference

| File | Relevance |
|------|-----------|
| `packages/dashboard/` | Existing admin dashboard -- reference for patterns, shared components |
| `packages/dashboard/src/hooks/useAuth.ts` | Existing auth hook pattern |
| `packages/dashboard/src/router.tsx` | Existing routing pattern |
| `packages/dashboard/src/guards/AdminGuard.tsx` | Existing guard pattern |
| `packages/dashboard/src/components/layout/` | Existing layout components (can share via shared package) |
| `packages/dashboard/src/components/common/` | Reusable UI components (Card, Badge, Toggle, etc.) |

### New Package vs Namespace

**Decision: New package `packages/user-dashboard/`**

Reasons:
- Separate build artifact (smaller bundle for end users)
- Different deployment target (`dash.tamma.dev` vs `app.tamma.dev`)
- Different auth flow (end-user JWT vs admin OAuth proxy)
- Can share common components via a `packages/ui/` shared package or direct imports

### Technology Stack

| Technology | Version | Purpose |
|-----------|---------|---------|
| React | 18.x | UI framework (match existing dashboard) |
| React Router | v7 | Client-side routing |
| Vite | 6.x | Build tool (match existing dashboard) |
| Tailwind CSS | 4.x | Styling |
| Zustand | 5.x | State management (match existing dashboard) |
| @tanstack/react-query | 5.x | Server state / API caching |

### Route Structure

```
/login                          — LoginPage
/register                       — RegisterPage
/verify-email                   — VerifyEmailPage
/forgot-password                — ForgotPasswordPage (placeholder)
/onboarding                     — OnboardingWizard
/onboarding/create-org          — CreateOrgStep
/onboarding/connect-github      — ConnectGitHubStep
/onboarding/select-repos        — SelectReposStep
/onboarding/first-run           — FirstRunStep
/                               — DashboardHome (requires auth + onboarding)
/repos                          — RepoListPage
/repos/:repoId                  — RepoDetailPage
/runs/:runId                    — RunDetailPage
/settings                       — SettingsLayout
/settings/profile               — ProfileSettings
/settings/organization          — OrgSettings
/settings/accounts              — ConnectedAccounts
/settings/notifications         — NotificationSettings
```

### API Client Configuration

```typescript
// packages/user-dashboard/src/api/client.ts
const apiClient = createApiClient({
  baseUrl: import.meta.env.VITE_API_URL ?? 'https://api.tamma.dev',
  credentials: 'include', // Send tamma_session cookie
  onUnauthorized: () => {
    // Redirect to login, save current path for post-login redirect
    window.location.href = `/login?redirect=${encodeURIComponent(window.location.pathname)}`;
  },
  onTokenRefresh: async () => {
    // Call POST /api/v1/auth/refresh
    const res = await fetch(`${baseUrl}/api/v1/auth/refresh`, {
      method: 'POST',
      credentials: 'include',
    });
    if (!res.ok) throw new Error('Refresh failed');
  },
});
```

### Nginx Configuration

```nginx
server {
    listen 443 ssl http2;
    server_name dash.tamma.dev;

    ssl_certificate     /etc/nginx/ssl/tamma.dev.pem;
    ssl_certificate_key /etc/nginx/ssl/tamma.dev.key;

    root /usr/share/nginx/html/user-dashboard;
    index index.html;

    # SPA fallback
    location / {
        try_files $uri $uri/ /index.html;
    }

    # API proxy
    location /api/ {
        proxy_pass http://tamma-api:3000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

### Admin vs User Dashboard Comparison

| Aspect | Admin (`app.tamma.dev`) | User (`dash.tamma.dev`) |
|--------|------------------------|------------------------|
| Auth | oauth2-proxy + GitHub OAuth | Direct JWT (email/password + GitHub OAuth) |
| Users | Platform admins/operators | End users / org members |
| Scope | All installations, all workflows | Org-scoped: user's org resources only |
| Features | Engine management, ELSA workflows, system health, logs | Repos, workflow runs, org settings |
| RBAC | `admin`, `operator`, `viewer` | `owner`, `admin`, `member` (within org) |

## Implementation Notes

- The user dashboard shares no authentication mechanism with the admin dashboard. Admin uses `oauth2-proxy`, user uses direct JWT cookies. The cookie name `tamma_session` is shared by convention but the JWT payloads differ.
- For MVP, the user dashboard is read-focused: view repos, view runs, manage settings. Triggering workflows is limited to the "first run" onboarding step.
- The SSE connection for live run updates should use the existing SSE infrastructure from `packages/api/src/routes/` but scoped to the user's org.
- Consider extracting shared UI components (Card, Badge, LoadingSpinner, FormField, Toggle) from `packages/dashboard/src/components/common/` into a `packages/ui/` shared package. This is optional for MVP and can be done as a follow-up.

## Dependencies

- **18-2**: Login, register, logout API endpoints
- **18-3**: Organization APIs (for onboarding wizard org creation step)
- **18-4**: GitHub App installation APIs (for onboarding wizard connect step)

## Estimated Effort

**Large (5 days)**:
- Day 1: Project setup, build config, router, API client, auth hook
- Day 2: Auth pages (login, register, verify) + auth guard + onboarding guard
- Day 3: Onboarding wizard (4 steps) + integration with backend APIs
- Day 4: Dashboard home + repo pages + run detail page
- Day 5: Settings pages + layout + deployment config + E2E testing

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0.0 | Initial story creation | Architecture Team |
