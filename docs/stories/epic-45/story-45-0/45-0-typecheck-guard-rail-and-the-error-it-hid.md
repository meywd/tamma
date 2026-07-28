# Story 45-0: Guard Rails — Typecheck the Customer App in CI, and the One Error That Hid Behind Its Absence

Status: done (code) — conformance-reviewed 2026-07-28; AC4's red-CI proof remains open (needs a scratch-branch push, owner permission — see .dev/findings/2026-07-28-epic45-cutover-evidence.md item 6); root-typecheck double-run accepted and documented in tsconfig.json

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

**Read this one specifically:** `.dev/findings/dashboard-user-is-the-unshipped-saas-customer-app.md`
— and note that its claim about the tests not running is **corrected**. This story is where the
correction is made permanent.

## User Story

As a **platform engineer** about to deploy the customer application for the first time,
I want `packages/dashboard-user` typechecked in CI and its one existing type error fixed,
So that the first thing we ship to customers is not the first thing we have never compiled, and so
the record about which dashboard's tests run stops being wrong.

## Priority

P0 — Wave 0. Ships standalone, is independently valuable, and is one fix from green. Nothing else in
Epic 45 depends on it, and it should land first anyway because every subsequent story edits this
package.

## Architectural Context (READ FIRST)

- **The root typecheck script does not include either dashboard.** `package.json:24` is
  `"typecheck": "tsc --build packages/shared packages/platforms packages/providers packages/orchestrator packages/cli"`.
  `.github/workflows/ci.yml:39-40` runs it, and `:42-43` adds `pnpm --filter @tamma/wiki-site run typecheck`.
  **No workflow typechecks either dashboard.**
- **`vite build` does not typecheck.** `packages/dashboard-user/package.json:7` is `vite build`;
  esbuild strips types without checking them. The build is green (289 kB, 46 modules, clean) and the
  code does not compile under `tsc`. That is the entire mechanism by which the error below survived.
- **There is exactly one error.** `pnpm --filter @tamma/dashboard-user run typecheck` →
  `src/pages/alerts/TenantAlertFeed.tsx(63,53): error TS2379` — `exactOptionalPropertyTypes` on
  `ListAlertsParams`. Verified 2026-07-27 against the working tree.
- **`packages/dashboard` has 68.** Including four in `src/hooks/useAuth.ts:25,39,45,49` (`AuthState`
  declares `logout`, every `setState` omits it) and two in `src/pages/AccountPage.tsx:46,49` (reads
  `.email` off a `CurrentUser` that has no such field — `services/admin/admin-api-client.ts:30-35`).
  Those are live bugs in the **deployed** app and they are **out of scope here** — see
  Out of Scope, and the coordination note with Story 44-6.
- **The tests already run.** `ci.yml:49-50` is `pnpm --filter @tamma/dashboard-user test`, and it
  passes: 20 files, 103 tests, all green. `vitest.config.ts:64`'s exclusion is deliberate and
  correct — the package has its own jsdom + jest-dom config, exactly as `packages/dashboard` does at
  `:60`. **The package whose tests do not run is `packages/dashboard`** (excluded at `:60`, no filter
  line anywhere). Story 44-6 owns that; this story owns saying so out loud.
- **`tsconfig.json:46-52` excludes `packages/dashboard` and not `packages/dashboard-user`.** The root
  tsconfig has no `include`, `rootDir: "."`, `lib: ["ES2023"]` (no DOM) and no `jsx` setting.
  ESLint's type-aware parser points at it (`eslint.config.js:15`), so the customer app's `.tsx` files
  currently sit inside a project that cannot type them. The exclusion is an omission, not a decision.
- **Both packages' tsconfigs exclude `*.test.ts` but not `*.test.tsx`** —
  `dashboard-user/tsconfig.json:13`, `dashboard/tsconfig.json:16`. So `.tsx` test files **are**
  typechecked. For the customer app that is currently harmless (they pass); it is stated here so the
  CI step's scope is not a surprise.

## Acceptance Criteria

1. **`TenantAlertFeed.tsx:63` compiles.** `pnpm --filter @tamma/dashboard-user run typecheck` exits 0.
   The fix is at the **type declaration**, not the call site: `ListAlertsParams`
   (`src/api/alerts.ts:89-94`) declares `status?: AlertStatus` under `exactOptionalPropertyTypes`,
   which means "the key may be absent" and **not** "the key may be `undefined`". The caller builds an
   object literal with all four keys present and some set to `undefined`. Widen the interface to
   `status?: AlertStatus | undefined` (and the other three optional members likewise), because the
   interface's intent has always been "these are optional" and the call site is expressing that
   correctly. Do **not** fix it by conditionally spreading keys at the call site — that pushes a
   type-system detail into rendering logic in four places and the next caller repeats it.

2. **CI typechecks the customer app.** `.github/workflows/ci.yml` gains a step in the
   `typescript-tests` job, adjacent to the existing wiki-site step (`:42-43`) and the existing
   dashboard-user test step (`:49-50`):
   ```yaml
   - name: Typecheck dashboard-user
     run: pnpm --filter @tamma/dashboard-user run typecheck
   ```
   It must be placed **before** the test step so a type error fails fast, and it must be a separate
   step rather than folded into the root `pnpm typecheck` — the root script is a `tsc --build` over a
   project-reference graph the dashboards are deliberately outside of (D1).

3. **The root tsconfig excludes `packages/dashboard-user`.** `tsconfig.json:51` gains the sibling
   line, with a comment stating why both dashboards are out: they are DOM+JSX Vite apps with their own
   tsconfigs and their own CI steps, and the root project has neither `lib: DOM` nor a `jsx` setting.
   This is what makes ESLint's type-aware parse of the customer app's `.tsx` files stop being
   nonsense.

4. **The CI step is proven to fail on a regression.** The PR description records the result of
   deliberately reintroducing the `TenantAlertFeed` error and confirming the new step goes red. A
   guard rail nobody has watched fail is a guard rail nobody knows is wired.

5. **`packages/dashboard` is untouched.** No changes to its source, its tsconfig, or its CI wiring.
   Its 68 errors and its 449 unrun tests are Story 44-6's, and folding them in here turns a one-day
   guard rail into an open-ended repair.

6. **The finding is corrected.** `.dev/findings/dashboard-user-is-the-unshipped-saas-customer-app.md`
   is edited to state that `ci.yml:49-50` runs the customer app's tests and that the package with
   excluded-and-never-run tests is `packages/dashboard`. The original claim stays visible as a struck
   correction rather than being silently deleted — the finding is cited by Epic 44's README and a
   silent edit makes the citation lie.

7. **A test asserts the fix, not just the compiler.** `src/api/alerts.test.ts` gains a case calling
   `listTenantAlerts(tenantId, { limit: 25 })` with `status`/`severity`/`sinceDays` genuinely absent,
   and asserts the built query string contains only `limit`. The type error was a symptom; the
   behaviour it guarded — that absent filters do not become `status=undefined` in the URL — deserves
   a pin. Verified present today (`alerts.ts:100-108` guards each key), so this is a regression pin,
   not a bug fix.

## Technical Notes

- **Why not add the dashboards to the root `typecheck` script instead?** The root script is
  `tsc --build` over five packages wired as project references. The dashboards are Vite apps with
  `composite: false` (`dashboard-user/tsconfig.json:6`), DOM libs and `jsx: react-jsx`. Adding them to
  a `--build` graph means giving them `composite: true` and a reference edge, which changes their
  build output layout for no benefit. A separate `--filter` step is what the repo already does for
  `@tamma/wiki-site` (`ci.yml:42-43`) and it is the precedent to follow.
- **`exactOptionalPropertyTypes` is on repo-wide** and is a good setting; nothing here weakens it.
  Widening a declared-optional member to `| undefined` is the sanctioned expression of "this key may
  be present and unset", which is exactly what a filter object is.
- The same `exactOptionalPropertyTypes` class of error appears four times in `packages/dashboard`
  (`hooks/knowledge-base/useContextTest.ts:48`, `useIndexStatus.ts:62`, `useVectorDB.ts:63`,
  `services/admin/conventions-api-client.ts:38`). Same fix shape. Noted for 44-6, not done here.

## Dependencies

- **Blocked by:** nothing. Day 0 of the epic.
- **Blocks:** nothing hard. Every other story in Epic 45 edits `packages/dashboard-user`, so landing
  this first means they inherit a working typecheck rather than discovering one — but none of them is
  gated on it.
- **Coordination gate — Story 44-6.** Its story file
  (`docs/stories/epic-44/story-44-6/44-6-tracker-ui-in-the-deployed-dashboard-and-the-missing-ci-line.md`)
  claims "typechecking both dashboards". Whichever story lands first takes the customer app; 44-6
  keeps the admin app and its 68 errors either way. **Agree the split before either starts** or the
  ci.yml step gets written twice.

## Blocks / Blocked by

- **Blocks:** nothing (soft: all of 45-1 … 45-7 benefit).
- **Blocked by:** nothing.

## Out of Scope

- `packages/dashboard`'s 68 typecheck errors — Story 44-6.
- `packages/dashboard`'s 449 excluded tests and the missing CI filter line — Story 44-6.
- Turning on ESLint for either dashboard as a CI gate — neither is linted in CI today and that is a
  separate decision with its own error backlog.
- Raising coverage thresholds — the root `vitest.config.ts:77-82` thresholds do not apply to either
  dashboard's own config, and changing that is not this story.
- Any behavioural change to the alert feed beyond AC1's type widening.

## Estimated Effort

**1 day.** The fix is one interface, the CI step is three lines, the tsconfig exclusion is one line,
and the finding correction is a paragraph. The day is mostly AC4 — pushing a deliberate regression to
watch CI go red is the only part that costs real wall-clock, and it is the only part that proves the
story did anything.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-27 | 1.0.0   | Initial story creation from the Epic 45 audit | Claude |
