# Bug: admin dashboard shipped with 68 tsc errors — useAuth state-shape lie and AccountPage untyped `email` read — because nothing ever typechecked it

**Date**: 2026-07-27
**Status**: ✅ Fixed
**Severity**: 🟡 Medium — type-level rot in the deployed admin console; runtime damage limited but only by luck
**Found by**: dashboard typecheck audit (branch `claude/wiki-docs-sync-r31nvo`)

## Summary

`packages/dashboard` (the deployed admin console) had **68 `tsc --noEmit` errors** and
`packages/dashboard-user` had 1. None of them could ever fail a build or a CI run:

- the root `typecheck` script (`package.json:24`) ran `tsc --build` over five legacy packages —
  neither dashboard included;
- no CI workflow step typechecked either dashboard (only `wiki-site` had a dedicated step);
- `vite build` uses esbuild transpile-only — it strips types without checking them, so
  `pnpm --filter @tamma/dashboard build` succeeded with all 68 errors present;
- on top of that, `packages/dashboard`'s ~449 vitest tests were excluded from the root
  `vitest.config.ts` (line 60) *and* had no `pnpm --filter @tamma/dashboard test` line in any
  workflow — that suite had **never run in CI** (it was green all along; 449/449 pass).

Two of the errors were live-code bugs rather than hygiene:

### 1. `src/hooks/useAuth.ts` — the state type lied about its own shape (4 errors)

`AuthState` declared `logout: () => void` as part of the **React state object**, but the
`useState` initializer and all three `setState` calls constructed `{ user, loading, error }`
without it. Every state transition therefore produced an object violating its declared type.

**What it did at runtime:** nothing visibly broke — but only by accident. The hook's return
statement is `return { ...state, logout }`, which re-attaches a fresh `logout` after the spread,
so every current consumer (`NavHeader`, `AccountPage`, `AuthGuard`, `MyApiKeysPage`) got a working
`logout`. The hazard was latent: any code typed against `AuthState` that read `state.logout`
directly (or stored/rehydrated the state object) would have gotten `undefined` and thrown
`logout is not a function` on click. The compiler flagged exactly this contract violation on the
day it was written; nothing ran the compiler.

**Fix:** split the types — `AuthState` (what `useState` holds: `user`/`loading`/`error`) and
`UseAuthResult extends AuthState` (the public contract, adds `logout`). Also made `logout` a
`useCallback` so its identity is stable across renders.

### 2. `src/pages/AccountPage.tsx` — rendered `fullUser.email`, a property that did not exist on `CurrentUser` (2 errors)

`AccountPage` renders `{fullUser?.email && (<dt>Email</dt><dd>{fullUser.email}</dd>)}`, but the
`CurrentUser` interface (`src/services/admin/admin-api-client.ts:30`) was
`{ id, username, githubId, role }` — no `email`. TypeScript rejected both reads.

**What it did at runtime:** the page reached through the type into the raw JSON. The backend
`/api/auth/me` (`MeUserPayload` in `apps/tamma-elsa/src/Tamma.Api/Dtos/Auth/AuthDtos.cs`,
camelCase serialization) *does* send `email`, so the Email row happened to render for users whose
account has one — the code worked only because the type was wrong in the same direction twice.
Had the payload field ever been renamed, the row would have silently vanished with zero compile or
test signal; the truthiness guard hides absence gracefully, which is exactly what makes it
undetectable. **Fix:** added `email: string` to `CurrentUser` (documented as possibly empty for
OAuth-only accounts) and updated `src/test/fixtures.ts` accordingly.

**Adjacent latent mismatch (documented, not fixed here):** the same payload serializes
`GitHubId` as `gitHubId` (System.Text.Json camelCase), while the frontend `AuthUser`/`CurrentUser`
declare `githubId`. If that holds at runtime, `AccountPage`'s "GitHub ID: {user.githubId}" line
renders blank. Fixing it touches the C# DTO or ~20 frontend call sites/fixtures, so it is out of
scope of this typecheck pass — verify against a live `/api/auth/me` response and align.

## Why nothing caught them

Three independent gaps had to line up, and all three did:

1. **No typecheck**: neither dashboard was in the root `typecheck` script or any CI step.
2. **Build doesn't check**: vite/esbuild transpiles type-blind, so "it builds" meant nothing.
3. **Tests never ran (dashboard)**: the one suite that exercised these components was excluded
   from root vitest *and* absent from CI — and its `NavHeader`/`AuthGuard` tests mock `useAuth`
   wholesale anyway, so even running them wouldn't have hit the state-shape lie.

## Fix / enforcement (this pass)

- All 68 + 1 errors fixed code-side (no tsconfig weakening, no `any`, no suppressions).
- `.github/workflows/ci.yml`: added `Typecheck dashboard` and `Typecheck dashboard-user` steps
  (mirroring the wiki-site step) and a `Run dashboard tests` step beside the dashboard-user one.
- Root `package.json` `typecheck` now chains both dashboard typechecks.
- Verified: both `tsc --noEmit` clean; dashboard 449/449 and dashboard-user 103/103 tests green;
  both `vite build` clean.

## Lessons

- A vite/esbuild "successful build" is not a typecheck. Any package built that way needs an
  explicit `tsc --noEmit` gate in CI or type errors accumulate invisibly (68 here).
- When a hook's return type and its `useState` type are the same interface, actions bundled into
  it will drift from the stored state. Keep "state" and "state + actions" as separate types.
- A conditional render behind a truthiness guard (`x?.field && ...`) fails silently when the
  field's very existence is wrong — only the compiler can see that class of bug; make sure it runs.
