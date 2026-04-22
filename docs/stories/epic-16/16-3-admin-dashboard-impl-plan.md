# Story 16-3: Admin Dashboard — Implementation Plan

## Overview

Story 16-3 delivers the React admin panel for platform operators (owner + admin roles). It consumes the user-management REST API shipped in PR #328 (`/api/admin/users/*`, `/api/admin/users/:id/keys`, `/api/admin/users/invite`, `/api/admin/health`) and surfaces four core operator capabilities: **user management**, **installation management**, **system health**, and **audit log viewing**. All pages are gated behind `AdminGuard` (admin/owner only), with navigation links conditionally rendered in the sidebar.

**Current state**: a substantial scaffold already exists in `packages/dashboard/src/pages/admin/` (`AdminLayout`, `UsersTab`, `ApiKeysTab`, `HealthTab`, `QuickLinksTab`), along with a Zustand admin store (`stores/admin/store.ts`), typed API client (`services/admin/admin-api-client.ts`), hooks (`hooks/admin/*`), and `AdminGuard` route protection. The existing scaffold covers ACs 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 and is wired through `router.tsx`.

**What's missing** (the work this plan covers):

1. **Installation management tab** — list GitHub App installations, revoke (not in current scaffold; story ACs don't explicitly mention it but the Layer-2 admin-UI brief does)
2. **Audit log viewer tab** — paginated audit events with filters (event type, scope, date range)
3. **Dashboard test infrastructure** — Vitest + @testing-library/react + jsdom are not yet set up in `packages/dashboard`; zero tests exist
4. **Per-page test coverage** — tests for all existing and new tabs, dialogs, hooks, store actions, guards
5. **Accessibility & performance polish** — focus trapping in dialogs, axe-core scan, Vite bundle-split for `/admin` route
6. **Dependency wiring** — backing API routes for installations + audit log (documented with `[API]` tag; implemented only if not owned by another story)

**Non-goals**: unified nav header across subdomains (Story 16-4). Service-to-service key management (Story 16-2 `service-keys.ts` already exists and is out of scope here).

**Routing**: the admin panel is reached at `/admin`. `router.tsx` already declares this route wrapped in `<AdminGuard><AdminLayout /></AdminGuard>`. AdminLayout renders a local tab bar (no nested routes). This plan keeps the tab-bar-in-state design; if deep-linking to a specific tab is needed later, switch to `useSearchParams` for `?tab=users`.

---

## Step-by-Step Implementation Tasks

### Task 1: Set up Vitest test infrastructure in `packages/dashboard` (2 hours)

**Files to create**:

- `packages/dashboard/vitest.config.ts` — Vitest config extending the Vite config, environment `jsdom`, globals enabled, setup file path
- `packages/dashboard/src/test/setup.ts` — global test setup: `@testing-library/jest-dom/vitest` imports, mocks for `window.matchMedia`, `ResizeObserver`, `navigator.clipboard.writeText`, and `fetch`

**Files to modify**:

- `packages/dashboard/package.json` — add devDependencies: `vitest`, `@vitest/coverage-v8`, `@testing-library/react`, `@testing-library/jest-dom`, `@testing-library/user-event`, `jsdom`, `msw` (align versions with `packages/providers` which already uses Vitest 3.x)
- `packages/dashboard/package.json` — add scripts: `"test": "vitest run"`, `"test:watch": "vitest"`, `"test:coverage": "vitest run --coverage"`
- `packages/dashboard/tsconfig.json` — add `"types": ["vitest/globals", "@testing-library/jest-dom"]`

**Acceptance**: `pnpm --filter @tamma/dashboard test` runs and reports zero tests (no failures). `pnpm --filter @tamma/dashboard typecheck` still passes.

---

### Task 2: Tests for existing guard and current-user plumbing (2 hours)

**Files to create**:

- `packages/dashboard/src/guards/__tests__/AdminGuard.test.tsx`
- `packages/dashboard/src/hooks/admin/__tests__/useCurrentUser.test.ts`
- `packages/dashboard/src/stores/admin/__tests__/store.test.ts` (current-user actions only; other actions covered in Task 5)

**AdminGuard test matrix**:

| # | Scenario | Expected |
|---|----------|----------|
| 1 | `loading: true` | renders `<LoadingSpinner>` |
| 2 | `user: null, loading: false` | redirects to `/account` (`<Navigate>`) |
| 3 | `user.role: 'member'` | redirects to `/account` |
| 4 | `user.role: 'admin'` | renders `children` |
| 5 | `user.role: 'owner'` | renders `children` |

Mocking strategy: mock `useCurrentUser` via `vi.mock('../hooks/admin/useCurrentUser.js', ...)` and return varying fixture values. Wrap `<AdminGuard>` in a `<MemoryRouter>` for each test.

**useCurrentUser test matrix**:

| # | Scenario | Expected |
|---|----------|----------|
| 6 | Store has no currentUser; calls `load()` on mount | `loadCurrentUser` called once |
| 7 | Store has `currentUser` already | `loadCurrentUser` not called |
| 8 | `currentUser.role === 'owner'` | `isOwner: true`, `isAdmin: true` |
| 9 | `currentUser.role === 'admin'` | `isOwner: false`, `isAdmin: true` |
| 10 | `currentUser.role === 'member'` | `isOwner: false`, `isAdmin: false` |

---

### Task 3: Tests for `UsersTab` + inline `RoleSelector` + `InviteDialog` (3 hours)

**File to create**: `packages/dashboard/src/pages/admin/__tests__/UsersTab.test.tsx`

Mocks: `vi.mock('../../../hooks/admin/useUsers.js')`, `vi.mock('../../../hooks/admin/useCurrentUser.js')`. Clipboard mocked via `Object.assign(navigator, { clipboard: { writeText: vi.fn() } })`.

**Test matrix**:

| # | Scenario | Assertion |
|---|----------|-----------|
| 11 | Loading, empty users | `<LoadingSpinner>` visible |
| 12 | Error state | error banner visible with message |
| 13 | Empty state (no users) | shows "No users yet" + "Invite User" button |
| 14 | Renders user rows | avatar src contains `githubLogin.png`, email displayed or `-` |
| 15 | Current user row: role shown as `<Badge>` (no dropdown) | `<select>` is absent for `user.id === currentUser.id` |
| 16 | Owner viewing another user: role dropdown enabled | `<select>` present, all 3 options enabled |
| 17 | Admin (non-owner) viewing another user: only `member` selectable | dropdown present but `owner`/`admin` options disabled |
| 18 | Owner clicks role change → confirm dialog opens | `ConfirmDialog` rendered with correct message |
| 19 | Owner confirms role change → `updateRole(userId, newRole)` called | spy assertion |
| 20 | Owner clicks Remove on another user → confirm dialog → `remove(userId)` called | spy assertion |
| 21 | Non-owner: Remove button not rendered | `queryByText('Remove')` null |
| 22 | Current user: Remove button not rendered | `queryByText('Remove')` null |
| 23 | Invite dialog: submit with email+role calls `invite`, shows generated link | `InviteResult.inviteLink` rendered in readonly input |
| 24 | Invite dialog: Copy button calls `navigator.clipboard.writeText` with link | spy assertion |
| 25 | Invite dialog: error from API surfaces as `<p className="text-sm text-red-600">` | error message visible |
| 26 | Invite dialog: Cancel button closes dialog | `onClose` called |

---

### Task 4: Tests for `ApiKeysTab` + `CreateApiKeyDialog` (2 hours)

**File to create**: `packages/dashboard/src/pages/admin/__tests__/ApiKeysTab.test.tsx`

| # | Scenario | Assertion |
|---|----------|-----------|
| 27 | Loading | spinner visible |
| 28 | Error | error banner visible |
| 29 | Empty list | "No API keys" empty state |
| 30 | Renders key rows with prefix, label, user name, created, last-used | table cells match fixture |
| 31 | Click Revoke → confirm dialog → `revoke(userId, keyId)` called | spy assertion |
| 32 | Create dialog: requires label (error on empty submit) | "Label is required" shown |
| 33 | Create dialog: requires user selection | "User is required" shown |
| 34 | Create dialog: successful creation shows the key **once** in a `<code>` block | key visible |
| 35 | Create dialog: warning banner "You will not be able to see it again" visible | text visible |
| 36 | Copy button writes key to clipboard and transiently shows "Copied!" | clipboard called, button text changes |
| 37 | After key created, closing dialog clears state (next open shows form again) | unmount/remount check |
| 38 | Security: generated key is NOT persisted to `localStorage` or `sessionStorage` | `localStorage.setItem` spy NOT called with key |

---

### Task 5: Tests for admin Zustand store (2 hours)

**File to create**: `packages/dashboard/src/stores/admin/__tests__/store.test.ts`

Mock `admin-api-client.ts` via `vi.mock`. Use `useAdminStore.setState` to reset state between tests.

| # | Action | Assertion |
|---|--------|-----------|
| 39 | `loadUsers()` success | `users`, `usersTotal` set; `usersLoading` false |
| 40 | `loadUsers()` failure | `usersError` set; `usersLoading` false |
| 41 | `updateUserRole()` success triggers `loadUsers()` reload | `usersApi.updateRole` + `usersApi.list` both called |
| 42 | `updateUserRole()` failure sets `usersError` and re-throws | `.rejects.toThrow()` |
| 43 | `removeUser()` success triggers reload | both API calls made |
| 44 | `createInvite()` returns result | result matches fixture |
| 45 | `loadAllApiKeys()` iterates all users, tolerates per-user errors | skipped users don't fail the whole load |
| 46 | `createApiKey()` reloads keys after creation | `loadAllApiKeys` called after create |
| 47 | `revokeApiKey()` success → reload; failure → sets error + throws | both branches |
| 48 | `loadHealth()` success sets `services` | `services` array populated |
| 49 | `loadCurrentUser()` success sets `currentUser` | single API call |

---

### Task 6: Tests for `HealthTab` + `QuickLinksTab` + `AdminLayout` (2 hours)

**Files to create**:

- `packages/dashboard/src/pages/admin/__tests__/HealthTab.test.tsx`
- `packages/dashboard/src/pages/admin/__tests__/QuickLinksTab.test.tsx`
- `packages/dashboard/src/pages/admin/__tests__/AdminLayout.test.tsx`

**HealthTab**:

| # | Scenario | Assertion |
|---|----------|-----------|
| 50 | Loading + no data | spinner visible |
| 51 | Error state | error banner visible |
| 52 | Empty services array | "No health data" empty state |
| 53 | Renders service cards with status dot, label, response time, checked-at | card contents match fixture |
| 54 | Unhealthy service shows red dot (`bg-red-500`) + details string | className + text checks |
| 55 | Unknown status shows grey dot (`bg-gray-400`) | className check |
| 56 | Refresh button calls `reload()` | spy assertion |
| 57 | Refresh button disabled while `loading: true` | `button.disabled === true` |

**QuickLinksTab**:

| # | Scenario | Assertion |
|---|----------|-----------|
| 58 | Renders all 4 link cards | ELSA, OpenSearch, GitHub, RabbitMQ names present |
| 59 | Links have `target="_blank"` and `rel="noopener noreferrer"` | DOM attribute assertions |

**AdminLayout**:

| # | Scenario | Assertion |
|---|----------|-----------|
| 60 | Defaults to Users tab | `UsersTab` rendered |
| 61 | Clicking each tab switches content | one tab's content shown at a time |
| 62 | Active tab has `border-blue-500 text-blue-600` classes | className check |
| 63 | Tab buttons have `aria-label="Admin tabs"` container | `<nav>` has `aria-label` |
| 64 | Keyboard focus moves between tabs via Tab key | fireEvent Tab key + activeElement check |

---

### Task 7: Installation management tab (3 hours)

**Rationale**: the task prompt lists installation management as a required admin capability. The story ACs (1-15) do **not** mention installations — they were reserved for Story 16-2. Verify whether `/api/admin/installations` exists before implementing. If the API is missing, file a follow-up with the 16-2 owner rather than building it here (API work is out of this story's scope).

**Investigation step** (30 min, pre-work): grep `packages/api/src/routes/admin/` for `installation`. If present → implement the tab. If absent → mark the tab as "deferred, blocked on API", add a placeholder tab with a "Coming soon" notice gated by a feature flag, and record the gap in this plan's "Open questions" section.

**Assuming the API exists or is added** (`GET /api/admin/installations`, `DELETE /api/admin/installations/:id`):

**Files to create**:

- `packages/dashboard/src/pages/admin/InstallationsTab.tsx`
- `packages/dashboard/src/hooks/admin/useInstallations.ts`
- `packages/dashboard/src/pages/admin/__tests__/InstallationsTab.test.tsx`

**Files to modify**:

- `packages/dashboard/src/services/admin/admin-api-client.ts` — add `installationsApi` with `list()` and `revoke(id)`; add `AdminInstallation` type (id, githubInstallationId, accountLogin, accountType, targetType, createdAt, suspendedAt | null)
- `packages/dashboard/src/stores/admin/store.ts` — add `installations` slice (`installations`, `installationsLoading`, `installationsError`, `loadInstallations`, `revokeInstallation`)
- `packages/dashboard/src/pages/admin/AdminLayout.tsx` — insert `'installations'` into `AdminTab` union and `TABS` array (between `'users'` and `'api-keys'`)

**InstallationsTab behavior**:

- Table columns: Account (avatar + login), Type (org/user), Installation ID, Installed At, Status (active / suspended), Actions (Revoke)
- Revoke button → confirm dialog → calls `revoke(id)` → reloads list
- Empty state: "No installations" with link to Tamma GitHub App install URL
- Owner-only: revoke action hidden for non-owners

**Test matrix** (tests 65-72):

| # | Scenario | Assertion |
|---|----------|-----------|
| 65 | Loading | spinner |
| 66 | Error state | banner |
| 67 | Empty state shows install URL | `<a>` to GitHub App install page |
| 68 | Renders installation rows | cell contents |
| 69 | Revoke → confirm dialog → `revoke(id)` called | spy |
| 70 | Non-owner: Revoke button hidden | `queryByText('Revoke')` null |
| 71 | Suspended installation shows "Suspended" badge | badge visible |
| 72 | After revoke, list reloads | `loadInstallations` called post-revoke |

---

### Task 8: Audit log viewer tab (4 hours)

**Rationale**: task prompt requires paginated audit log with event-type, scope, and date-range filters. The story ACs do not list audit log as a requirement (ACs 1-15 cover Users / API Keys / Health / Quick Links). This tab is an **addendum** from Layer-2 Team-D scope.

**API dependency**: no `/api/admin/audit-log` endpoint exists today (verified via grep). This tab **cannot ship** without the backing route. Either:

- **(a)** implement the route in this same PR (~4 extra hours, pulled from Story 16-2 buffer) — requires an `audit_events` table or reuse of the Epic-17 event store with `tags.scope = 'admin'`
- **(b)** defer and ship a placeholder tab behind `VITE_FEATURE_ADMIN_AUDIT_LOG=false`

**Recommendation**: ship **(b)** in this PR to avoid cross-package coupling, open a separate ticket for the API (`16-3-audit-api.md`), and enable the flag once the API lands. The placeholder keeps the nav link visible for admins and renders a "Coming soon" card with an ETA.

**Files to create** (placeholder path):

- `packages/dashboard/src/pages/admin/AuditLogTab.tsx` — renders the feature flag state: when off, shows coming-soon card; when on, renders full table (code ready for flip)
- `packages/dashboard/src/hooks/admin/useAuditLog.ts` — `useQuery`-style hook with filter state (no-op when flag off)
- `packages/dashboard/src/pages/admin/__tests__/AuditLogTab.test.tsx` — feature-flag-on and feature-flag-off paths

**Full behavior** (when API lands):

- Filters row: event type multi-select, scope dropdown (admin / user / system), date range pickers (from / to), "Apply" button, "Reset" button
- Paginated table: Timestamp, Event Type, Actor (user / system), Scope, Target, Details (JSON collapsible)
- Pagination: offset/limit, 50 rows/page, prev/next
- Empty state: "No audit events in this range"
- Download: "Export CSV" button → calls `/api/admin/audit-log?format=csv&...filters`

**Files to modify**:

- `packages/dashboard/src/services/admin/admin-api-client.ts` — add `auditLogApi.list(filters)` and `AuditEvent` type; stub implementation returns mock data when `VITE_FEATURE_ADMIN_AUDIT_LOG` is falsy
- `packages/dashboard/src/stores/admin/store.ts` — add `auditEvents`, `auditLoading`, `auditError`, `auditFilters`, `loadAuditLog(filters)`
- `packages/dashboard/src/pages/admin/AdminLayout.tsx` — add `'audit-log'` to `AdminTab` union

**Test matrix** (tests 73-82):

| # | Scenario | Assertion |
|---|----------|-----------|
| 73 | Flag off → renders coming-soon card | text visible, no table |
| 74 | Flag on + loading | spinner |
| 75 | Flag on + error | banner |
| 76 | Flag on + empty result | empty-state text |
| 77 | Renders rows with all columns | fixture match |
| 78 | Event-type filter → `loadAuditLog` called with filters | spy |
| 79 | Scope filter → spy | spy |
| 80 | Date range filter → spy | spy |
| 81 | Pagination Next → offset advances by 50 | spy |
| 82 | Export CSV triggers `<a download>` with correct href | DOM assertion |

---

### Task 9: Accessibility + focus management polish (2 hours)

**Current gaps** (found via code review of existing scaffold):

1. `InviteDialog`, `CreateApiKeyDialog` use `fixed inset-0` overlays with no focus trap — Tab key escapes the dialog
2. `ConfirmDialog` (likely has same issue — verify)
3. Closing a dialog doesn't return focus to the trigger button
4. `Esc` key doesn't close dialogs

**Files to modify**:

- `packages/dashboard/src/components/common/ConfirmDialog.tsx` — add `useEffect` listening for `Escape` key to call `onCancel`; focus the confirm button on mount; return focus to previous `document.activeElement` on unmount
- `packages/dashboard/src/pages/admin/UsersTab.tsx` → `InviteDialog` — same treatment + focus trap (tabbable elements cycle inside dialog). Use `useRef` + `KeyboardEvent` listener, or pull in `focus-trap-react` as a dependency (preferred: stay zero-dep given existing scaffold uses no a11y libs — implement manually)
- `packages/dashboard/src/pages/admin/ApiKeysTab.tsx` → `CreateApiKeyDialog` — same

**Test additions** (tests 83-86):

| # | Scenario | Assertion |
|---|----------|-----------|
| 83 | `Escape` key in open `InviteDialog` closes dialog | `onClose` called |
| 84 | `Escape` key in open `CreateApiKeyDialog` closes dialog | `onClose` called |
| 85 | Opening dialog moves focus into dialog | `document.activeElement` inside dialog |
| 86 | Tab key from last focusable in dialog cycles to first | focus stays in dialog |

**axe-core scan**: add `vitest-axe` as devDep; one smoke test per tab renders the tab and runs `axe` expecting zero violations. Scope: `UsersTab`, `ApiKeysTab`, `HealthTab`, `QuickLinksTab`, `InstallationsTab`, `AuditLogTab` (tests 87-92, six total).

---

### Task 10: Performance budget — route-level code splitting (1 hour)

**Goal**: initial admin-panel load ≤ 2s, subsequent tab switches < 300ms. Current `router.tsx` imports `AdminLayout` eagerly; this pulls all 4-6 tab components + the Zustand admin slice into the main bundle.

**File to modify**: `packages/dashboard/src/router.tsx`

Replace:

```tsx
import { AdminLayout } from './pages/admin/AdminLayout.js';
```

with a `React.lazy` import and wrap the admin route element in `<Suspense fallback={<LoadingSpinner />}>`:

```tsx
const AdminLayout = React.lazy(() =>
  import('./pages/admin/AdminLayout.js').then((m) => ({ default: m.AdminLayout })),
);
```

Route entry:

```tsx
{
  path: '/admin',
  element: (
    <AdminGuard>
      <Suspense fallback={<LoadingSpinner size="lg" />}>
        <AdminLayout />
      </Suspense>
    </AdminGuard>
  ),
}
```

**Verification**: run `pnpm --filter @tamma/dashboard build` and inspect `dist/assets/`. Expected: a separate `AdminLayout-*.js` chunk (~30-60 kB gzipped). Record the number in the PR description.

**Tab-level lazy loading** (stretch): each heavy tab (`InstallationsTab`, `AuditLogTab`) can also be `React.lazy` to keep tab-switch time under 300 ms. Non-heavy tabs (Quick Links) stay eager.

---

### Task 11: Error boundary for admin panel (1 hour)

**File to create**: `packages/dashboard/src/pages/admin/AdminErrorBoundary.tsx`

Catches any render/runtime error in the admin subtree, shows a friendly card with "Something went wrong. Retry | Return to dashboard", logs the error (when telemetry exists) and offers a reload button. Class component because React 18 error boundaries still require classes.

**File to modify**: `packages/dashboard/src/router.tsx` — wrap `<AdminLayout />` in `<AdminErrorBoundary>` inside the Suspense.

**Test**: `packages/dashboard/src/pages/admin/__tests__/AdminErrorBoundary.test.tsx` — render a child that throws, assert error UI visible; click Retry, assert child re-renders (test 93).

---

### Task 12: RBAC verification audit (1 hour)

**Goal**: cross-check that every admin API endpoint used by the dashboard enforces role `admin` or `owner` server-side (defense in depth — client-side nav hiding is NOT sufficient).

**Endpoints consumed by the dashboard**:

| Endpoint | Method | Server-side role check | Note |
|----------|--------|------------------------|------|
| `/api/auth/me` | GET | none (any authenticated user) | correct — needed to decide admin vs. member |
| `/api/admin/users` | GET | verify — should require `admin\|owner` | in `routes/users/user-routes.ts` |
| `/api/admin/users/:id/role` | PUT | verify — must require `owner` for admin/owner role changes | |
| `/api/admin/users/:id` | DELETE | verify — must require `owner` | |
| `/api/admin/users/invite` | POST | verify — should require `admin\|owner` | |
| `/api/admin/users/:id/keys` | GET/POST/DELETE | verify | |
| `/api/admin/health` | GET | verified — `health-routes.ts:72-80` enforces `admin\|owner` via JWT | |
| `/api/admin/installations` | GET/DELETE | must add on API side | |
| `/api/admin/audit-log` | GET | must add on API side | |

**Deliverable**: section in PR description listing each endpoint and a link to the Fastify route where the role check lives. Any endpoint missing a check blocks the PR.

**No code changes in this task** — it's a documentation/audit step. If a gap is found, file a bug against Story 16-2 and block the 16-3 merge.

---

### Task 13: OAuth2 wiring smoke test (1 hour)

**Goal**: confirm that an unauthenticated request to `/admin` hits the auth flow configured by Story 16-1 (oauth2-proxy) rather than rendering a broken SPA.

**Files to create**:

- `packages/dashboard/src/guards/__tests__/AuthGuard.test.tsx` — verify `AuthGuard` redirects to `/login` when no session cookie is present (test 94-95)

**Manual verification** (documented in PR description, not automated):

1. Clear cookies for `app.tamma.dev`
2. Visit `https://app.tamma.dev/admin`
3. Expected: redirected to oauth2-proxy → GitHub OAuth → back to `/admin`
4. Verify Users tab loads with the logged-in user's role reflected in the sidebar

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `packages/dashboard/vitest.config.ts` | Vitest config |
| 2 | `packages/dashboard/src/test/setup.ts` | Global test setup |
| 3 | `packages/dashboard/src/guards/__tests__/AdminGuard.test.tsx` | Guard tests |
| 4 | `packages/dashboard/src/guards/__tests__/AuthGuard.test.tsx` | Auth guard smoke |
| 5 | `packages/dashboard/src/hooks/admin/__tests__/useCurrentUser.test.ts` | Hook tests |
| 6 | `packages/dashboard/src/stores/admin/__tests__/store.test.ts` | Store action tests |
| 7 | `packages/dashboard/src/pages/admin/__tests__/UsersTab.test.tsx` | Users tab tests |
| 8 | `packages/dashboard/src/pages/admin/__tests__/ApiKeysTab.test.tsx` | API keys tab tests |
| 9 | `packages/dashboard/src/pages/admin/__tests__/HealthTab.test.tsx` | Health tab tests |
| 10 | `packages/dashboard/src/pages/admin/__tests__/QuickLinksTab.test.tsx` | Quick links tab tests |
| 11 | `packages/dashboard/src/pages/admin/__tests__/AdminLayout.test.tsx` | Layout tab switching tests |
| 12 | `packages/dashboard/src/pages/admin/InstallationsTab.tsx` | New installations tab |
| 13 | `packages/dashboard/src/hooks/admin/useInstallations.ts` | Installations hook |
| 14 | `packages/dashboard/src/pages/admin/__tests__/InstallationsTab.test.tsx` | Installations tests |
| 15 | `packages/dashboard/src/pages/admin/AuditLogTab.tsx` | Audit log tab (feature-flagged) |
| 16 | `packages/dashboard/src/hooks/admin/useAuditLog.ts` | Audit log hook |
| 17 | `packages/dashboard/src/pages/admin/__tests__/AuditLogTab.test.tsx` | Audit log tests |
| 18 | `packages/dashboard/src/pages/admin/AdminErrorBoundary.tsx` | Error boundary |
| 19 | `packages/dashboard/src/pages/admin/__tests__/AdminErrorBoundary.test.tsx` | Boundary test |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/dashboard/package.json` | Add Vitest + RTL devDeps, test scripts |
| 2 | `packages/dashboard/tsconfig.json` | Add Vitest / jest-dom types |
| 3 | `packages/dashboard/src/services/admin/admin-api-client.ts` | Add `installationsApi`, `auditLogApi`, new types |
| 4 | `packages/dashboard/src/stores/admin/store.ts` | Add installations slice + audit log slice |
| 5 | `packages/dashboard/src/pages/admin/AdminLayout.tsx` | Add Installations + Audit Log tabs |
| 6 | `packages/dashboard/src/router.tsx` | Lazy-load `AdminLayout`, wrap in `Suspense` + `AdminErrorBoundary` |
| 7 | `packages/dashboard/src/components/common/ConfirmDialog.tsx` | Esc key close + focus return |
| 8 | `packages/dashboard/src/pages/admin/UsersTab.tsx` | InviteDialog focus trap + Esc |
| 9 | `packages/dashboard/src/pages/admin/ApiKeysTab.tsx` | CreateApiKeyDialog focus trap + Esc |

## Files NOT to Modify (already correct in the scaffold)

- `packages/dashboard/src/guards/AdminGuard.tsx` — works as-is, just add tests
- `packages/dashboard/src/components/layout/Sidebar.tsx` — already conditionally renders `Admin Panel` link via `isAdmin`
- `packages/dashboard/src/hooks/admin/{useUsers,useApiKeys,useSystemHealth}.ts` — thin wrappers over store, no logic to change
- `packages/dashboard/src/pages/admin/QuickLinksTab.tsx` — static content
- `packages/dashboard/src/services/admin/admin-api-client.ts` user/api-key sections — already match Story 16-2 contract

---

## RBAC Notes

- **Client-side nav hiding**: Sidebar already hides the "Administration" group when `!isAdmin` (member users). `AdminGuard` redirects any direct `/admin` visit by a member to `/account`.
- **Server-side gating**: every `/api/admin/*` route MUST enforce role `admin|owner` at the Fastify `preHandler`. Task 12 audits this. The `/api/admin/health` endpoint is the gold standard (role check at lines 72-80 of `health-routes.ts`).
- **Owner-only operations**: role changes that promote to `admin`/`owner`, user deletions, installation revocations. The dashboard hides these buttons when `!isOwner`, but the API MUST double-check — client hiding is cosmetic, not security.
- **Self-protection**: the current user cannot change their own role or delete themselves. Enforced client-side in `UsersTab` (`disabled={user.id === currentUser.id}`); the server must also reject these operations.
- **Future**: when Story 16-5 (RBAC) lands, replace the string comparison `role === 'admin' || role === 'owner'` with a `requirePermission('admin:access')` helper consistent with the unified RBAC model. Tag the TODOs with `// TODO(16-5): use requirePermission`.

---

## Integration with Story 16-4 (Unified Navigation Header)

Story 16-4 ships a shared nav header (`@tamma/shared-ui` or similar) that appears across `app.tamma.dev`, `elsa.tamma.dev`, and `logs.tamma.dev`. 16-3 and 16-4 can ship independently:

- 16-3 keeps the existing `AppLayout` (`packages/dashboard/src/components/layout/AppLayout.tsx`) with the left `Sidebar`. No changes to the layout chrome.
- 16-4 will later inject a top nav bar above `AppLayout` or replace part of it. The admin pages are layout-agnostic — they render into whatever slot the layout provides.
- No file conflicts expected. If 16-4 merges first, 16-3 rebases trivially. If 16-3 merges first, 16-4 wraps the existing layout.
- **Coordination point**: the "Admin Panel" link currently lives in the left sidebar (`Sidebar.tsx`). 16-4 may choose to promote it into the top nav. Decide during 16-4 review; 16-3 does not pre-empt the decision.

---

## Performance Budgets

| Metric | Target | Measurement |
|--------|--------|-------------|
| Initial `/admin` route load (cold cache) | < 2.0 s | Lighthouse + `performance.now()` in e2e |
| Tab switch (Users → API Keys) | < 300 ms | `React.Profiler` in a test, or manual devtools |
| `/admin` main chunk size | < 80 kB gzipped | `pnpm build` output inspection |
| API round trip (`/api/admin/users`, p50) | < 200 ms | server-side metric, confirmed by network tab |
| API round trip (`/api/admin/health`) | < 1000 ms | slower because it pings 6 services sequentially |

Tab-switch budget requires each tab to:

- fetch on mount via the Zustand action (already done)
- not block on fetch before first render (render loading skeleton first — already done)
- memoize expensive renders — watch the users table if it grows > 100 rows (use `useMemo` on sorted/filtered list)

---

## Accessibility Requirements

Every admin page must pass axe-core with zero critical/serious violations. Concrete requirements:

1. **Keyboard navigation**: all interactive elements reachable via Tab; no keyboard traps except modal dialogs (intentional); Esc closes dialogs; Enter/Space activate buttons.
2. **Focus management**: opening a dialog moves focus into the dialog; closing returns focus to the trigger.
3. **Screen reader**: tables have proper `<thead>` / `<tbody>` / `<th scope="col">`; dialogs use `role="dialog"` + `aria-modal="true"` + `aria-labelledby` (the scaffold already does this for invite + create-key dialogs).
4. **Color contrast**: Tailwind defaults used throughout, already 4.5:1 minimum. Verify the red `text-red-600` on white background in error banners.
5. **Status announcements**: use `aria-live="polite"` regions for async status changes ("User role updated", "API key revoked", "Health refresh complete").
6. **Form labels**: every `<input>` / `<select>` has an associated `<label>` — verified in scaffold.

Automated: `vitest-axe` smoke test per tab (Task 9). Manual: keyboard-only walkthrough + VoiceOver/NVDA sanity check before merge.

---

## Open Questions

1. **Installations API** — does `/api/admin/installations` exist? (grep said no in `packages/api/src/routes/admin/`.) **Action**: verify with 16-2 owner before Task 7. If missing, either pull the API work into 16-3 or scope Task 7 down to a stub.
2. **Audit log storage** — piggyback on Epic-17 event store (with a `scope: 'admin'` tag filter) or a dedicated `audit_events` table? **Action**: defer until API design ticket filed.
3. **Feature flag mechanism** — dashboard uses `import.meta.env.VITE_*` (Vite env). Confirm this is fine for runtime gating of the audit log tab; no feature-flag service exists yet.
4. **Tab deep linking** — current implementation uses `useState`, so refreshing `/admin` always lands on Users. If product wants `/admin?tab=health`, add `useSearchParams` in Task 12 (not currently scoped).

---

## Dependencies

- **Story 16-1** (OAuth2 Proxy) — prerequisite for `/api/auth/me` to return a meaningful user object. Already merged per PR #328.
- **Story 16-2** (User Management API) — all `/api/admin/users/*` routes. Already merged per PR #328.
- **Story 16-5** (RBAC) — future; replaces string-role checks with permission checks. Not blocking 16-3.
- **React Router 7.13** — already installed.
- **Zustand 5** — already installed (no React Query in this stack).
- **@testing-library/react 14+, vitest 3+, jsdom 25+** — to add in Task 1.
- **vitest-axe** — to add in Task 9.

Note: the story file mentions React Query, but the existing scaffold uses Zustand. This plan stays with Zustand to avoid a second state-management library in the same bundle. If React Query is desired later, that's a separate refactor ticket.

---

## Testing Strategy Summary

- **Unit tests** (Vitest + React Testing Library, jsdom): 95 tests across guards, hooks, store, and tab components. Target ≥ 80% line coverage on `packages/dashboard/src/{guards,hooks,stores,pages/admin,services/admin}`.
- **Accessibility tests** (vitest-axe): 6 smoke tests, one per tab, zero critical/serious violations.
- **Integration smoke**: `AuthGuard` + `AdminGuard` + `AdminLayout` rendered with a fixture store for each role (member, admin, owner) and each route outcome asserted.
- **E2E (Playwright)** — deliberately **out of scope** for this story. Dashboard has no Playwright setup today; adding it is a separate infra ticket. Manual verification (documented in PR body) covers the browser-side smoke.
- **No server-side test changes** — Story 16-2 already covered `/api/admin/users/*` routes in `packages/api/src/routes/users/__tests__/`.

---

## Migration / Rollout

- Ship behind no feature flag (admin panel is already in production as an empty scaffold; this fills it in).
- Audit log tab ships behind `VITE_FEATURE_ADMIN_AUDIT_LOG` (default `false`) until the API lands.
- Installations tab ships with the API or as a deferred stub (see Task 7 decision).
- No database migrations.
- No deploy ordering constraints (dashboard build is independent).

---

## Estimated Effort

| Task | Hours |
|------|-------|
| 1. Vitest test infrastructure | 2 |
| 2. Guard + useCurrentUser tests | 2 |
| 3. UsersTab tests (+ inline components) | 3 |
| 4. ApiKeysTab tests (+ inline dialog) | 2 |
| 5. Store action tests | 2 |
| 6. HealthTab + QuickLinksTab + AdminLayout tests | 2 |
| 7. Installations tab (assuming API exists) | 3 |
| 8. Audit log tab (feature-flagged placeholder) | 4 |
| 9. A11y focus management + axe smoke tests | 2 |
| 10. Route-level code splitting | 1 |
| 11. Error boundary + test | 1 |
| 12. RBAC verification audit (doc only) | 1 |
| 13. OAuth2 wiring smoke test | 1 |
| **Total** | **26 hours** |

Note: Layer-2 plan budgets 24 hours. The +2 reflects the audit log placeholder and a11y work that the original story ACs did not mention but the task prompt does. If constrained, Task 8 (audit log placeholder) can be deferred to a follow-up PR — drops to 22 hours.

---

## Success Criteria

- [ ] All existing admin pages (`UsersTab`, `ApiKeysTab`, `HealthTab`, `QuickLinksTab`) have passing unit tests with ≥ 80% line coverage
- [ ] `AdminGuard` redirects non-admin users and allows admin/owner users (test-verified)
- [ ] Sidebar hides admin links for members (test-verified)
- [ ] Installations tab renders and revoke works (if API exists) OR ships as a documented stub
- [ ] Audit log tab ships as a feature-flagged placeholder with test coverage for both flag states
- [ ] Every admin API endpoint enforces role check at the Fastify layer (documented in PR description)
- [ ] axe-core zero critical/serious violations on all admin tabs
- [ ] Dialogs trap focus, respond to Esc, return focus on close
- [ ] `/admin` route is lazy-loaded; main bundle excludes admin code
- [ ] Manual smoke test in PR description: fresh login → navigate to `/admin` as owner, admin, member
- [ ] PR passes `pnpm --filter @tamma/dashboard test` and `pnpm --filter @tamma/dashboard typecheck`

---

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-15 | 1.0 | Initial implementation plan written against existing scaffold | Layer-2 Team D |
