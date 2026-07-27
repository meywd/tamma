# Implementation Plan — Story 45-0: Guard Rails — Typecheck the Customer App in CI

## Scope & Deliverable

When this story is done, `pnpm --filter @tamma/dashboard-user run typecheck` exits 0, CI runs it on
every push and PR, the root `tsconfig.json` no longer pretends the customer app's `.tsx` files belong
to a DOM-less non-JSX project, and the finding that says the customer app's tests do not run has been
corrected to say what is actually true — that they do, and that the app whose tests never run is the
*deployed admin console*. One interface widened, one CI step, one tsconfig line, one regression test,
one finding edit. No behavioural change ships.

## Pre-Reading

- `docs/stories/epic-45/README.md` — the audit, especially "Correction to the record" and Gap 5
- `packages/dashboard-user/src/api/alerts.ts:89-112` — `ListAlertsParams` and `listTenantAlerts`
- `packages/dashboard-user/src/pages/alerts/TenantAlertFeed.tsx:55-70` — the failing call site
- `.github/workflows/ci.yml:36-50` — the `typescript-tests` job; note the wiki-site filter step
  (`:42-43`) is the precedent for AC2 and the dashboard-user test step (`:49-50`) is the placement anchor
- `package.json:24` — the root `typecheck` script, and why the dashboards are not in it
- `tsconfig.json:46-52` — the `exclude` array with `packages/dashboard` and not its sibling
- `packages/dashboard-user/tsconfig.json:1-15` — `composite: false`, `jsx: react-jsx`, DOM libs,
  and the `*.test.ts`-but-not-`*.test.tsx` exclusion at `:13`
- `.dev/findings/dashboard-user-is-the-unshipped-saas-customer-app.md:32-34` — the claim to correct
- **All referenced paths exist.** Nothing in this story creates a file.

## Design Decisions

- **D1 — A separate `--filter` CI step, not an addition to the root `typecheck` script.** The root
  script is `tsc --build` over a five-package project-reference graph. Both dashboards are Vite apps
  with `composite: false`, `lib: ["ES2023","DOM","DOM.Iterable"]` and `jsx: react-jsx`. Folding them
  into a `--build` graph means flipping `composite: true`, adding reference edges and changing their
  emit layout — real churn, zero benefit, and it would couple the customer app's compile to
  `packages/cli`'s. The repo already has the right precedent: `@tamma/wiki-site` gets its own filter
  step at `ci.yml:42-43`. Copy that.

- **D2 — Fix the type at the declaration, not the call site.** `ListAlertsParams`
  (`alerts.ts:89-94`) declares `status?: AlertStatus`. Under `exactOptionalPropertyTypes` that means
  *the key may be absent*, not *the key may be `undefined`*. `TenantAlertFeed.tsx:63` builds an
  object literal with all four keys present, some holding `undefined` — the natural way to build a
  filter object from four pieces of component state.
  The two candidate fixes:
  1. **Widen the interface** to `status?: AlertStatus | undefined` (and `severity`, `sinceDays`,
     `limit` likewise).
  2. Conditionally spread at the call site: `...(status ? { status } : {})` × 4.
  Take (1). The interface's *intent* has always been "these are optional filters" and the call site
  is expressing that correctly; (2) pushes a type-system detail into rendering logic, does it four
  times, and the next caller of `listTenantAlerts` — 45-2 and 45-3 both touch this area — repeats the
  ceremony. The runtime guard that actually matters (`alerts.ts:100-108`, each key checked before it
  reaches the query string) already exists and is untouched by either fix; AC7 pins it.

- **D3 — Exclude `packages/dashboard-user` from the root tsconfig, matching `packages/dashboard`.**
  This is the asymmetry that made the error invisible in two directions at once. The root project has
  no `include`, `rootDir: "."`, no DOM lib and no `jsx` setting; ESLint's type-aware parser
  (`eslint.config.js:15`) resolves `project: './tsconfig.json'`, so the customer app's `.tsx` files
  are being parsed against a project that structurally cannot type them. The admin app was excluded
  at `tsconfig.json:51`; its sibling was not. Nothing decided that — it is an omission from the day
  the package was added, and the fix is one line plus a comment saying why *both* are out.

- **D4 — Do not touch `packages/dashboard`.** Turning its typecheck on means fixing 68 errors, four
  of which are a live bug in the auth hook the deployed `AuthGuard` depends on
  (`dashboard/src/hooks/useAuth.ts:25,39,45,49` — `AuthState` declares `logout`, every `setState`
  omits it) and two of which are a live rendering bug (`AccountPage.tsx:46,49` reads `.email` off a
  `CurrentUser` that has no such member, `services/admin/admin-api-client.ts:30-35`). Those deserve a
  story. Folding them here converts a bounded one-day guard rail into an unbounded repair on the
  critical path of a shipping epic. **44-6 owns them.**

- **D5 — Correct the finding visibly, not silently.** `.dev/findings/…:32-34` is cited by
  `docs/stories/epic-44/README.md` and by Story 44-6. Deleting the wrong sentence makes those
  citations point at text that no longer says what the citing document claims it says. Strike it and
  annotate, so a reader arriving from Epic 44 sees both the original claim and why it was wrong.

## Implementation Steps

1. **Widen `ListAlertsParams`** — `packages/dashboard-user/src/api/alerts.ts:89-94`. Four members get
   `| undefined`. Add a one-line comment naming `exactOptionalPropertyTypes` so the next reader does
   not "tidy" it back.
2. **Confirm the fix is total.** Run `pnpm --filter @tamma/dashboard-user run typecheck`. Expect exit
   0 and zero errors. If a second error appears, it was masked by the first — record it in the PR and
   fix it; do not expand scope beyond `packages/dashboard-user`.
3. **Add the regression pin** — `packages/dashboard-user/src/api/alerts.test.ts`. Call
   `listTenantAlerts('…', { limit: 25 })` with the other three keys genuinely absent, and assert the
   captured URL is `/api/v1/orgs/{id}/alerts?limit=25` exactly. Then call it with
   `{ status: undefined, severity: undefined, sinceDays: undefined, limit: 25 }` — the shape the
   component actually passes — and assert the **same** URL. That second case is the one that would
   have caught a call-site "fix" that changed behaviour.
4. **Add the CI step** — `.github/workflows/ci.yml`, in `typescript-tests`, immediately after the
   wiki-site typecheck (`:42-43`) and **before** the existing dashboard-user test step:
   ```yaml
         - name: Typecheck dashboard-user
           run: pnpm --filter @tamma/dashboard-user run typecheck
   ```
   Placement before the test step is deliberate: a type error should fail in seconds, not after a
   20-file jsdom run.
5. **Exclude the package from the root tsconfig** — `tsconfig.json:46-52`. Add
   `"packages/dashboard-user"` beside `"packages/dashboard"` and replace the surrounding comment with
   one that states the shared reason (DOM+JSX Vite apps, own tsconfigs, own CI steps, root project has
   neither `lib: DOM` nor `jsx`).
6. **Verify ESLint still passes** — `pnpm lint`. Step 5 changes which project ESLint's type-aware
   rules resolve the customer app against; the no-raw-`fetch` rule (`eslint.config.js:71-99`) covers
   both dashboards and must still fire. If the rule now reports differently, that difference is the
   *correct* behaviour appearing for the first time — record it, and fix any genuine violation.
7. **Prove the guard rail (AC4).** On a scratch branch, revert step 1, push, and confirm the new CI
   step goes red with the `TS2379`. Screenshot or paste the run URL into the PR description. Then
   restore.
8. **Correct the finding** — `.dev/findings/dashboard-user-is-the-unshipped-saas-customer-app.md`.
   Strike the "even those tests do not run" clause, add a dated correction stating: `ci.yml:49-50` is
   the filter line; the exclusion at `vitest.config.ts:64` is deliberate and mirrors `:60`; the
   package with unrun tests is `packages/dashboard`. Add a pointer to `docs/stories/epic-45/README.md`.

## Data & Migrations

None. No schema, no entity, no migration, no seed.

## Events

None. No DCB event is emitted or consumed by this story.

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | `alerts.test.ts` — `Absent_filters_are_omitted_from_the_query_string` | `{ limit: 25 }` → `?limit=25`, nothing else |
| 2 | `alerts.test.ts` — `Explicitly_undefined_filters_are_also_omitted` | `{ status: undefined, …, limit: 25 }` → the **same** URL; the component's actual call shape |
| 3 | Existing 103 tests | still green — this story changes one type and no runtime path |
| 4 | `pnpm --filter @tamma/dashboard-user run typecheck` | exit 0 |
| 5 | `pnpm lint` | exit 0 after the tsconfig exclusion |
| 6 | Manual (AC4) | reverting step 1 turns the new CI step red |

No new test infrastructure. No Testcontainers, no fixtures, no mocks beyond the `fetch` stub
`alerts.test.ts` already installs.

## Definition of Done

- `pnpm --filter @tamma/dashboard-user run typecheck` exits 0 on `main`.
- `.github/workflows/ci.yml` runs it, positioned before the test step, and a recorded run proves it
  fails on a reintroduced error.
- `tsconfig.json` excludes both dashboards, with a comment stating the shared reason.
- Two new cases in `alerts.test.ts`; all 105 tests green.
- **Zero files changed under `packages/dashboard/`** (grep-checked in review).
- `.dev/findings/dashboard-user-is-the-unshipped-saas-customer-app.md` carries a dated correction that
  leaves the original claim legible.
- No change to `package.json:24` — the root `typecheck` script is untouched (D1).

## Dependencies & Sequencing

- **Blocked by:** nothing. Day 0.
- **Blocks:** nothing hard. Land it first anyway — 45-1, 45-2, 45-3 and 45-7 all edit
  `packages/dashboard-user/src`, and they should inherit a green typecheck rather than each
  discovering the same error.
- **Shared-edit register:** `.github/workflows/ci.yml` is also edited by **45-6** (which adds the
  image build/deploy wiring, in `docker-publish.yml` and `deploy.yml` — different files, but review
  them together). `tsconfig.json` is edited by nobody else in this epic.
- **Coordination gate — Story 44-6** claims "typechecking both dashboards". Whichever lands first
  takes the customer app; 44-6 keeps the admin app. Settle it before either starts.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **The single error is masking others.** `tsc` reports what it reaches; fixing one can reveal five. | Step 2 makes discovery explicit. The blast radius is bounded — the package is 25 source files and 3,661 lines, and its 20 test files already exercise most of it. If more appear they are in scope; if they reach into `packages/dashboard`, they are not (D4). |
| **Widening the interface hides a real bug elsewhere** — some future caller passes `status: undefined` meaning "all" and a handler reads it as a filter. | AC7 / tests 1–2 pin the runtime behaviour on both shapes, so the guarantee is "absent and explicitly-undefined are identical at the URL", asserted rather than assumed. |
| **The tsconfig exclusion changes ESLint's results and the lint job goes red.** | Step 6 runs `pnpm lint` before the PR opens. A new report here is the correct behaviour surfacing for the first time, not a regression — but it must be resolved in this story, not deferred, or the story leaves CI worse than it found it. |
| **The story is mistaken for "turn on the dashboard tests" and grows to 449 tests + 68 errors.** | D4 and Out of Scope both name it; the DoD grep-check on `packages/dashboard/` is the mechanical stop. |
| **44-6 lands the same `ci.yml` step concurrently and the two conflict.** | The coordination gate is stated in both this plan and the story file. The conflict is a three-line YAML collision — annoying, not dangerous — but agreeing ownership avoids two people each fixing `TenantAlertFeed.tsx` differently. |

## Effort Breakdown

| Task | Days |
|---|---|
| Steps 1–3 (widen the interface, confirm, two regression tests) | 0.25 |
| Steps 4–6 (CI step, tsconfig exclusion, lint verification) | 0.25 |
| Step 7 (prove the guard rail fails — the only real wall-clock) | 0.25 |
| Step 8 + review (finding correction, PR write-up) | 0.25 |
| **Total** | **1.0** |

The code is an hour. The day is AC4 and the finding correction — the two parts that make this a guard
rail rather than a one-line commit nobody can tell worked.
