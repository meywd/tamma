# Story 45-2: Entry Points — The Six URLs the API Emails, the Missing Catch-All, and Four Honest Nav Links

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **customer who has just registered**,
I want the link in my verification email to open a page,
So that I can finish signing up — and so that every other link the platform emails me (password
reset, org invite, GitHub install result) also lands somewhere rather than on a blank pane.

## Priority

P0 — Wave 0, and it is the story that turns Epic 45 from a deployment epic into a shipping epic.
Deploying the app without this produces a working billing screen behind a registration flow whose
verification link 404s. **That is worse than not shipping, because it looks shipped.**

## Architectural Context (READ FIRST)

- **The API generates six URLs into the customer app. The app implements none of them.** Verified
  2026-07-27 against `apps/tamma-elsa/src/Tamma.Api/` and `packages/dashboard-user/src/App.tsx:39-88`:

  | URL | Generated at | Route in the customer app? |
  |---|---|---|
  | `{Dashboard:Url}/verify?token=` | `Endpoints/AuthEndpoints.cs:31-33` | ❌ the app has `/verify-email` |
  | `{Dashboard:Url}/reset-password?token=` | `Endpoints/AuthEndpoints.cs:36-39` | ❌ no route, no page |
  | `{Dashboard:Url}/invites/accept?token=` | `Endpoints/OrgEndpoints.cs:361-362` | ❌ no route, no page |
  | `{Dashboard:Url}/invites/pending?inviteId=` | `Endpoints/OrgEndpoints.cs:501-502` | ❌ no route, no page |
  | `{Dashboard:Url}/onboarding/success` | `Endpoints/GitHubEndpoints.cs:23,40` | ❌ (admin app has it) |
  | `{Dashboard:Url}/onboarding/error` | `Endpoints/GitHubEndpoints.cs:24,40` | ❌ (admin app has it) |

- **There is no catch-all route.** `App.tsx:39-88` declares eleven routes and no `path="*"`. An
  unmatched path inside the authenticated tree renders `AppLayout` with an empty `<Outlet />` — the
  sidebar and header draw, the content pane is blank, no error, no 404. Outside the tree it renders
  nothing at all. **Every one of the six URLs above fails this way today**, and so do the four dead
  nav links below.
- **Four nav links point at routes that do not exist.** `layouts/AppLayout.tsx:24,27,33` link to
  `/repos`, `/runs` and `/settings`; `pages/DashboardHome.tsx:64` links to `/onboarding`. All four
  exist in the **admin** app (`packages/dashboard/src/router.tsx:106,107,58`) and none exists here.
  They were copied, not mistyped.
- **`/verify` vs `/verify-email` is the sharpest instance.** The page is built, tested
  (`VerifyEmailPage.test.tsx`, 92 lines) and correct — `VerifyEmailPage.tsx:17` reads `?token=` and
  `:27` POSTs it in the body, matching `VerifyEmailRequest(string Token)`
  (`Dtos/Auth/AuthDtos.cs:34`). It is mounted at `/verify-email` (`App.tsx:42`) and the API emails
  `/verify`. One word.
- **The backends for the unbuilt pages all exist.** `POST /api/v1/auth/password-reset/request` and
  `/confirm` (`Program.cs:1850-1851`); `POST /api/v1/orgs/invites/accept` (`Program.cs:2301`).
  Nothing here is blocked on server work.
- **Scope split with 45-3.** This story ships the **routes, the aliases, the catch-all and the
  navigation** — the routing skeleton, provably reachable. The three genuinely new *pages* (password
  reset, invite accept, invite pending) are **45-3**, which depends on this. The split exists because
  the routing fix is small, independently verifiable and unblocks the deployment stories, while the
  pages are a four-day build.

## Acceptance Criteria

1. **`/verify` resolves.** The verification route is reachable at both `/verify` (what the API emails)
   and `/verify-email` (what the app has always used and what its tests target). Implement as a real
   route pair rendering the same `VerifyEmailPage`, **not** a redirect — a redirect drops the
   `?token=` query unless carefully preserved, and preserving it is more code than mounting the
   element twice.
   **Do not "fix" this by changing `AuthEndpoints.cs:32`.** That URL is already in the inbox of every
   user who has ever registered, and those links must keep working.

2. **`/reset-password` and the two `/invites/*` paths resolve to a real route each**, rendering a
   named placeholder component that states the feature is arriving and links to `/login`. **The
   placeholder is deliberate and time-boxed to 45-3** — a stated "password reset is coming, contact
   support" beats a blank pane, and it means the deployment stories are not gated on a four-day page
   build. Each placeholder carries a `// Story 45-3 replaces this` comment and is listed in 45-3's
   Definition of Done as a file that must be deleted.

3. **`/onboarding/success` and `/onboarding/error` exist as real pages.** These are not placeholders —
   they are the two terminal states of the GitHub App install flow and each is a short page.
   `success` confirms the installation and links to `/settings/platforms` (which exists,
   `App.tsx:80`). `error` shows the failure and links back to `/onboarding/platforms` (`App.tsx:64`).
   `GitHubEndpoints.cs:40` appends no query parameters beyond what the redirect carries — read it and
   render whatever it actually sends rather than inventing a contract.

4. **`/onboarding` resolves.** `DashboardHome.tsx:64`'s empty-state button links there. Redirect it to
   `/onboarding/platforms`, which is the real first step (`App.tsx:64`). A redirect is correct here —
   unlike AC1 there is no query string to preserve.

5. **A catch-all route exists, and it is a real 404 page.** `path="*"` rendering a `NotFoundPage` that
   states the path was not found and links to `/`. It must be declared **twice** — once inside the
   authenticated `AppLayout` tree so an unknown path for a signed-in user renders inside the shell,
   and once outside so an unknown path for an anonymous user renders standalone rather than bouncing
   through `AuthGuard` to `/login?redirect=<garbage>`.

6. **The four dead nav links are made honest.** `AppLayout.tsx:24,27,33` — `/repos`, `/runs` and
   `/settings` are **removed** from the sidebar, not routed. There is no repos page, no runs page and
   no settings index in this app, and shipping links to a 404 we just built is not an improvement over
   shipping links to a blank pane. Replace them with links to what exists: `/alerts` (`App.tsx:51`),
   `/settings/platforms` (`:80`) and `/settings/billing` (`:54`) — the last of which is already there.
   **Record the removal in the story's change log**, because "the customer app has a Runs page" is a
   reasonable thing for someone to believe from reading the sidebar, and it is not true.

7. **An error boundary wraps the router.** `main.tsx:11-12` renders `<App />` bare, so any render
   throw blanks the page. Add a class boundary — `getDerivedStateFromError` + `componentDidCatch`, a
   Retry button, a link home, and a dev-only stack dump gated on `import.meta.env.DEV`. Model it on
   `packages/dashboard/src/pages/admin/AdminErrorBoundary.tsx:19-70`, but mount it at the **root**,
   not around a subtree — the admin app mounts it only around its lazy admin routes
   (`router.tsx:176-180`) and its own root render at `index.tsx:15` is unprotected. Copy the
   component, not the placement.

8. **Every route in `App.tsx` has a test that it renders something.** A single table-driven test
   iterating the route list, rendering each path through `MemoryRouter`, asserting the result is not
   an empty container. This is the test that would have caught all six missing entry points, and it is
   the reason none of them can silently return.

9. **`index.html` gets a real `<title>` and a description.** `index.html:6` is `<title>Tamma</title>`;
   the admin app's is `Tamma Dashboard` (`packages/dashboard/index.html:10`). This is the
   customer-facing surface — give it a title and a `<meta name="description">`. Favicons are 45-4
   (they need the `public/` directory that story creates).

## Technical Notes

- **Why alias rather than change the server.** AC1's rule generalizes: every one of these six URLs may
  already exist in a sent email or a GitHub App configuration. The client is the side that can add a
  path for free; the server is the side whose emitted strings are durable. Where the two disagree,
  **the client adopts the server's path and keeps its own as an alias.** 45-7 revisits whether the
  server should also emit a nicer URL going forward — but never by breaking the old one.
- **The placeholder decision (AC2) is the load-bearing scope call in this story.** The alternative is
  to fold 45-3 in here and make a six-day story on the critical path. Splitting means the deployment
  stories can run against an app where every URL resolves, while the three real pages land in
  parallel. The risk is that placeholders ship to production and stay — mitigated by AC2's comment
  convention and by 45-3's DoD naming the files to delete.
- **The catch-all must be declared twice (AC5)** and this is easy to get wrong. React Router matches
  `path="*"` within the layout route's children for authenticated paths; an anonymous user hitting an
  unknown path needs a sibling declaration outside the `AuthGuard`-wrapped element or the guard
  redirects them to `/login?redirect=%2Fnonsense`, which after login redirects them straight back to
  the same unknown path. Test both.
- **AC6 removes rather than builds.** Someone will argue the links should stay and the pages be built.
  That is a product decision about what the customer app is *for*, and it is much larger than this
  epic — a runs list for customers is Story 39-19/44-6 territory. Removing a link that has never
  worked is not a regression.

## Dependencies

- **Blocked by:** nothing. All six backends exist; all routing is client-side.
- **Blocks:** **45-3** (the three real pages replace this story's placeholders) and **45-7** (which
  repoints `Dashboard:Url` at the customer app — pointing it at an app where these routes do not
  resolve would move the breakage rather than fix it).
- **Related:** open question 1 in the epic README — whether the GitHub install callback should target
  the customer app or the admin console. This story builds AC3's pages in the customer app regardless;
  45-7 decides which host the redirect names.

## Blocks / Blocked by

- **Blocks:** 45-3, 45-7.
- **Blocked by:** nothing. (Soft: land 45-0 first so the typecheck is green before adding files.)

## Out of Scope

- **Building the password-reset, invite-accept and invite-pending pages** — 45-3. This story ships
  their routes and placeholders only.
- A repos page, a runs page or a settings index — AC6 removes the links instead; those pages are a
  product question outside this epic (39-19, 44-6).
- Favicons, `robots.txt`, `public/` — 45-4, which creates the directory.
- Changing any URL the API emits — 45-7, and only additively.
- Code splitting the new routes. The bundle is 289 kB; six small pages do not change that.

## Estimated Effort

**2 days.** The routing changes are a few hours. The rest is AC3's two real pages (read
`GitHubEndpoints.cs` for the actual redirect contract rather than assuming), AC7's error boundary
ported from the admin app, and AC8's table-driven route test — which is the artefact that makes this
class of bug non-recurring and is worth the half-day it costs.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-27 | 1.0.0   | Initial story creation from the Epic 45 audit | Claude |
