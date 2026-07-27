# Implementation Plan — Story 45-2: Entry Points

## Scope & Deliverable

When this story is done, every URL `Tamma.Api` emails or redirects a customer to resolves in
`packages/dashboard-user`, an unmatched path renders a real 404 instead of a blank pane, the sidebar
links only to pages that exist, a render throw shows a recoverable error instead of a white screen,
and a table-driven test asserts that every declared route renders something. Three of the six entry
points ship as named placeholders that Story 45-3 replaces; the other three ship complete.

## Pre-Reading

- `docs/stories/epic-45/README.md` — Gap 1 and Gap 3, and D-notes on aliasing
- `packages/dashboard-user/src/App.tsx:35-92` — the whole router; eleven routes, no catch-all
- `packages/dashboard-user/src/layouts/AppLayout.tsx:20-36` — the sidebar with three dead links
- `packages/dashboard-user/src/pages/DashboardHome.tsx:56-71` — the empty state linking to `/onboarding`
- `packages/dashboard-user/src/pages/auth/VerifyEmailPage.tsx:1-89` — already correct; only its mount path is wrong
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:29-39` — `BuildVerificationUrl`, `BuildResetUrl`
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:358-364` and `:499-506` — the two invite URLs
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs:22-24, 32-60` — the install callback and
  **its actual redirect contract**. Read this before writing AC3's pages; do not infer the query params.
- `packages/dashboard/src/pages/admin/AdminErrorBoundary.tsx:19-70` — the boundary to port (AC7)
- `packages/dashboard/src/router.tsx:54-90` — how the admin app structures `/onboarding/*`; useful shape, different app
- **All referenced paths exist.** NOT FOUND (this story creates them): `src/pages/NotFoundPage.tsx`,
  `src/components/ErrorBoundary.tsx`, `src/pages/onboarding/InstallSuccess.tsx`,
  `src/pages/onboarding/InstallError.tsx`, `src/pages/placeholders/*.tsx`.

## Design Decisions

- **D1 — Alias, never redirect, when a query string is load-bearing.** `/verify` and `/verify-email`
  both mount `<VerifyEmailPage />` as two `<Route>` elements. A `<Navigate>` between them drops
  `?token=` unless the redirect is built to carry it, and building that is more code and more failure
  modes than a second one-line route. `/onboarding` → `/onboarding/platforms` **is** a redirect (AC4)
  precisely because it carries nothing.

- **D2 — The server's path wins; the app's path becomes the alias.** `AuthEndpoints.cs:32` emits
  `/verify` and has done for every user who ever registered. Those links are in inboxes. The client
  can add a path for free; the server cannot un-send an email. This generalizes to all six URLs and it
  is why no server file is touched in this story.

- **D3 — Three placeholders, deliberately, with a named owner.** `/reset-password`,
  `/invites/accept` and `/invites/pending` get one-screen components saying the feature is arriving
  and linking to `/login`. The alternative — building all three here — makes a six-day story on the
  critical path and blocks the deployment stories behind a page build they do not need.
  **The risk is that placeholders become permanent.** Two mitigations, both mechanical: each file
  lives under `src/pages/placeholders/`, carries a `// Story 45-3 replaces this file` comment, and is
  listed by path in 45-3's Definition of Done as a **deletion**. A placeholder in its own directory is
  greppable; one inline in `App.tsx` is not.

- **D4 — `/onboarding/success` and `/onboarding/error` are real, not placeholders.** They are two
  short terminal-state pages, they are the tail of a flow a customer is already mid-way through when
  they arrive, and a placeholder at the end of a successful GitHub App install reads as a failure.
  The cost is a few hours; the confusion is permanent.

- **D5 — The catch-all is declared twice.** React Router resolves `path="*"` relative to its parent.
  Declared only inside the `AuthGuard`-wrapped layout, an anonymous hit on `/nonsense` goes through
  the guard to `/login?redirect=%2Fnonsense` and, after a successful login, lands back on
  `/nonsense` — a loop that ends in the blank pane we are removing. Declared only outside, a
  signed-in user's typo loses the shell. Both, and a test for each.

- **D6 — Remove the dead nav links rather than route them to the new 404.** A sidebar link that
  reliably 404s is not better than one that blanks; both are the same lie with different pixels.
  `/repos`, `/runs` and `/settings` are removed and the sidebar links to what the app actually has:
  Dashboard (`/`), Alerts (`/alerts`), Platforms (`/settings/platforms`), Billing
  (`/settings/billing`). Building the missing pages is a product question this epic does not own.

- **D7 — Mount the error boundary at the root, not around a subtree.** The admin app's boundary
  (`AdminErrorBoundary.tsx:19`) is good and its **placement** is not — `router.tsx:176-180` wraps only
  the lazy admin routes, and `index.tsx:15` renders the root unprotected. Port the component; fix the
  placement. Wrapping `<App />` in `main.tsx` covers the router itself, which is where an unmatched
  route or a bad lazy import would throw.

- **D8 — One table-driven route test, not eleven.** AC8's test iterates a route table and asserts
  each renders a non-empty container. Written per-route it would be seventeen near-identical tests
  nobody updates; written as a table, adding a route without adding a case is impossible because the
  table *is* the route list. It is the artefact that makes this bug class non-recurring.

## Implementation Steps

1. **Read `GitHubEndpoints.cs:32-60` first.** Determine exactly what the install callback appends to
   `/onboarding/success` and `/onboarding/error` — installation id, setup action, error code, or
   nothing. AC3's pages render what is actually sent. Record the finding in the PR; if the redirect
   carries nothing useful, say so and keep the pages informational.
2. **Create `src/pages/NotFoundPage.tsx`** — path echoed back (escaped), a link to `/`, and a link to
   `/login` when anonymous. No API call.
3. **Create `src/components/ErrorBoundary.tsx`** — class component ported from
   `AdminErrorBoundary.tsx:19-70`: `getDerivedStateFromError`, `componentDidCatch` (log via
   `console.error`, matching the source), Retry that resets state, a home link, and a stack dump
   gated on `import.meta.env.DEV`.
4. **Wrap the root** — `src/main.tsx:12`, `<ErrorBoundary><App /></ErrorBoundary>`.
5. **Create `src/pages/onboarding/InstallSuccess.tsx` and `InstallError.tsx`** per step 1's findings.
   `success` links to `/settings/platforms`; `error` links to `/onboarding/platforms`.
6. **Create `src/pages/placeholders/{PasswordResetPlaceholder,InviteAcceptPlaceholder,InvitePendingPlaceholder}.tsx`** —
   one screen each, the D3 comment, a link to `/login`. Keep them near-identical; they are being
   deleted.
7. **Rewrite the route tree** — `src/App.tsx`. Public routes gain `/verify` (aliasing
   `/verify-email`), `/reset-password`, `/invites/accept`, `/invites/pending`, and a `path="*"`
   `NotFoundPage`. Inside the `AppLayout` tree add `/onboarding` (→ `<Navigate to="/onboarding/platforms" replace />`),
   `/onboarding/success`, `/onboarding/error`, and a second `path="*"`.
   **Decide deliberately whether `/invites/accept` is public or guarded.** An invited user may not
   have an account yet; check `OrgEndpoints.AcceptInvite`'s authorization
   (`Program.cs:2301` — it is under the `orgs` group, `MapGroup("/api/v1/orgs")` at `:2299`, which
   carries `.RequireAuthorization("MemberAccess")`). If the endpoint requires auth, the page is
   guarded and must preserve the token across the login redirect — note it for 45-3 rather than
   solving it in a placeholder.
8. **Update `src/App.tsx`'s file-header route comment** (`:1-14`). It is currently accurate and would
   otherwise become the next stale artefact in this story's own subject area.
9. **Fix the sidebar** — `src/layouts/AppLayout.tsx:20-36`. Remove `/repos`, `/runs`, `/settings`; add
   Alerts and Platforms; keep Dashboard and Billing.
10. **Fix `DashboardHome.tsx:64`** — it can now keep linking to `/onboarding` because step 7 made that
    resolve. Verify rather than change.
11. **Update `index.html`** — `<title>Tamma — Dashboard</title>` and a `<meta name="description">`.
12. **Write the route table test** — `src/App.test.tsx`. A `const ROUTES` array, `it.each` over it,
    each rendering `<MemoryRouter initialEntries={[path]}>` with a mocked authenticated `useAuth` and
    asserting `container.textContent` is non-empty. Include a deliberately-unknown path asserting the
    404 renders, once authenticated and once anonymous (D5).

## Data & Migrations

None.

## Events

None. No DCB event is emitted by any page in this story.

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | `App.test.tsx` — `Every_declared_route_renders_something` (table-driven) | non-empty container for all ~17 paths — AC8 |
| 2 | `App.test.tsx` — `Unknown_path_renders_404_when_authenticated` | `NotFoundPage` inside the shell |
| 3 | `App.test.tsx` — `Unknown_path_renders_404_when_anonymous` | `NotFoundPage`, and **no** redirect to `/login` — D5 |
| 4 | `App.test.tsx` — `Verify_alias_preserves_the_token_query` | `/verify?token=abc` renders `VerifyEmailPage` and it reads `abc` — D1 |
| 5 | `App.test.tsx` — `Onboarding_root_redirects_to_platforms` | `/onboarding` → `/onboarding/platforms` |
| 6 | `ErrorBoundary.test.tsx` — `Renders_fallback_when_a_child_throws` | fallback + Retry present, app content absent |
| 7 | `ErrorBoundary.test.tsx` — `Retry_resets_and_re_renders` | after Retry with a non-throwing child, content returns |
| 8 | `ErrorBoundary.test.tsx` — `Stack_is_hidden_outside_DEV` | no stack text when `import.meta.env.DEV` is false |
| 9 | `AppLayout.test.tsx` — `Sidebar_links_only_to_declared_routes` | every `<Link to>` in the sidebar is in the route table — the pin that stops AC6 regressing |
| 10 | `InstallSuccess` / `InstallError` | render, and link to the right next step |
| 11 | Full suite | existing 103 + ~15 new, green |
| 12 | `pnpm --filter @tamma/dashboard-user run typecheck` | exit 0 |

**Test 9 is the durable one.** It cross-references the sidebar against the route table, so the next
copied-from-the-admin-app link fails CI instead of shipping.

## Definition of Done

- All six API-emitted URLs resolve; verified by rendering each path, and by a manual click-through of
  a real verification email against a local API.
- `path="*"` declared in both trees; both covered by tests.
- **No `<Link to>` in the app points at an undeclared route** (test 9, and grep-checked in review).
- `<ErrorBoundary>` wraps `<App />` at `main.tsx`.
- Three placeholder files exist under `src/pages/placeholders/`, each carrying the
  `// Story 45-3 replaces this file` comment, and each named in 45-3's DoD.
- `App.tsx`'s header comment matches the route tree.
- ~118 tests green; typecheck exit 0.
- **No file under `apps/tamma-elsa/` changed** (grep-checked) — D2.

## Dependencies & Sequencing

- **Blocked by:** nothing hard. Land 45-0 first so the typecheck is green before adding ~8 files.
- **Blocks:** 45-3 (replaces the placeholders), 45-7 (must not repoint `Dashboard:Url` at an app where
  these routes do not resolve).
- **Shared-edit register:** `src/App.tsx` is also edited by **45-3** (swapping placeholder imports for
  real pages). That is the intended hand-off, not a conflict — but the two stories must not run
  concurrently on the same file. `src/layouts/AppLayout.tsx` is edited by nobody else in this epic.
- **Open question 1 (epic README)** — whether the GitHub install redirect should target this app or
  the admin console — does **not** block this story. AC3's pages are built here either way; 45-7
  decides the host.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **The placeholders ship and stay.** The single most likely bad outcome of D3. | Own directory (greppable), a comment naming the replacing story, and an explicit deletion line in 45-3's DoD. If 45-3 slips, the placeholders are at least *honest* — they say the feature is not there, which the current blank pane does not. |
| **`/invites/accept` needs auth and the token is lost across the login redirect.** | Step 7 says check `Program.cs:2299-2301` and *record the answer for 45-3* rather than half-solving it. `AuthGuard.tsx:34-35` already preserves `pathname + search` in `?redirect=`, so the machinery exists — but proving it round-trips a token is 45-3's job. |
| **`GitHubEndpoints`' redirect contract is guessed and AC3's pages render nothing useful.** | Step 1 makes reading it the first action, before any page is written, and requires the finding in the PR. If the redirect carries nothing, the pages stay informational — which is still correct and still better than blank. |
| **Removing three sidebar links reads as a regression to a reviewer** who assumes the pages exist. | The story's AC6 and change log both state the pages have never existed and the links were copied from `packages/dashboard/src/router.tsx`. Test 9 makes the invariant permanent. |
| **The double catch-all shadows a real route** if `path="*"` is declared before its siblings. | React Router ranks by specificity, not declaration order, so this is safe — but test 1 renders every declared route and would catch it immediately if a future router change altered that. |
| **The route table test becomes a rubber stamp** — a route added to `App.tsx` but not to the test table. | Derive the table from a single exported `ROUTES` constant that `App.tsx` itself maps over, so the test cannot disagree with the router. If that proves too invasive for React Router's element syntax, keep the literal table and rely on test 9's sidebar cross-check plus review. |

## Effort Breakdown

| Task | Days |
|---|---|
| Step 1 (read the GitHub callback contract) + steps 5, 10 (two real onboarding pages) | 0.5 |
| Steps 2–4 (404 page, error boundary port, root mount) | 0.5 |
| Steps 6–9, 11 (placeholders, route tree, sidebar, header comment, `index.html`) | 0.5 |
| Step 12 (route table test, error-boundary tests, sidebar cross-check) | 0.5 |
| **Total** | **2.0** |

The routing is a few hours. The half-day on tests buys the two pins — every route renders, and no
link points anywhere undeclared — that make six missing entry points a thing that cannot happen twice.
