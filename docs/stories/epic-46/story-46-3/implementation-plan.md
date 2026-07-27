# Implementation Plan — Story 46-3: Tenant Model Settings UI

## Scope & Deliverable

When this story is done, the customer app has `/settings/models`: a roster of the providers the
platform has enabled, each showing the tenant's effective model and whether it comes from their
override or the platform default; a live searchable picker (BYOK-key-fetched server-side when the
tenant has one); override save/reset for tenant admins; read-only rendering for members. The page
merges and tests green now; customers reach it when Epic 45 deploys the app.

## Pre-Reading

- `docs/stories/epic-46/README.md` — RBAC table, D5/D6
- `docs/stories/epic-46/story-46-1/…` — the tenant route contracts (roster, model GET/PUT/DELETE)
- `packages/dashboard-user/src/api/client.ts` (esp. `:88-113`) — the sanctioned ApiClient
- `packages/dashboard-user/src/api/alerts.ts` — API-module + params-typing conventions, including
  the `exactOptionalPropertyTypes` posture 45-0 fixed
- `packages/dashboard-user/src/App.tsx:39-88` + `AppLayout.tsx` — routes and nav, AS REWORKED BY
  45-2 (read the merged state, not today's)
- `packages/dashboard-user/src/hooks|context useAuth` (`useAuth.tsx`) — what `/api/auth/me`
  exposes; decides the `canEdit` derivation
- `docs/stories/epic-45/README.md` — Gap 4 (fixture-from-DTO), the app's conventions list
- `docs/stories/epic-46/story-46-2/implementation-plan.md` — the picker behaviours; mirror them,
  do not import admin-app code

## Design Decisions

- **D1 — No shared picker component across the two apps.** The apps deliberately do not share UI
  code (Epic 45 README records the per-package divergence as sanctioned; extracting shared
  primitives is 43-7/44-6 contested ground). The picker logic is small (a filter + a sort + three
  banners); duplicate it with a comment cross-linking 46-2's component so a future extraction
  story can find both.

- **D2 — Server-enforced RBAC, client-cosmetic `canEdit`.** The server's `AgentManage` gate is
  the enforcement. The client derives `canEdit` from the auth payload if the role is present,
  else optimistically and downgrade-on-403. Never hardcode role names into rendering logic beyond
  the boolean.

- **D3 — Tenants see two provenance states, not four.** The server reports
  `tenant-override | platform-db | config | descriptor`; the page maps everything ≠
  `tenant-override` to "platform default". Exposing the platform's config/descriptor internals to
  customers is noise and leaks deployment detail. The mapping is one exported const (testable),
  mirroring 46-2's D4 discipline.

- **D4 — `provider-models.ts` params/types follow the widened-optional convention** established by
  45-0 (`status?: X | undefined`) so `exactOptionalPropertyTypes` stays green — stated so a
  copy-paste from the admin app's looser tsconfig doesn't reintroduce the class of error 45-0
  fixed.

## Implementation Steps

1. **`src/api/provider-models.ts`** — types from the C# DTOs (roster row, model-list envelope,
   model GET/PUT/DELETE shapes incl. `pricingKnown`/`warning`), functions on the ApiClient:
   `listProviderModelSettings`, `listProviderModels(key)`, `getProviderModel(key)`,
   `putProviderModel(key, model)`, `deleteProviderModel(key)`.
2. **`useAuth` inspection** — settle the `canEdit` source (D2); record the answer in the PR.
3. **`ModelSettingsPage`** — roster, provenance mapping (D3), BYOK indicator, states.
4. **`TenantModelPicker`** — fetch-on-open, search, pin/delisted, deprecated ordering,
   stale/empty banners, free-text path, save + warning, reset confirm naming the platform
   default, 403 downgrade.
5. **Route + nav** — `/settings/models` in `App.tsx`; nav entry per the post-45-2 layout.
6. **Tests** — per AC6; fixtures with DTO source comments.
7. **Verification** — `pnpm --filter @tamma/dashboard-user test` and
   `pnpm --filter @tamma/dashboard-user run typecheck` both green (both run in CI).

## Data & Migrations

None.

## Events

None client-side; tenant mutations are audited server-side by 46-1
(`PROVIDER.SETTINGS_CHANGED.SUCCESS`, scope `tenant`).

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | roster render | rows from fixture; enabled-only is the server's concern (fixture reflects it) |
| 2 | provenance mapping | `tenant-override` → "your override"; each other source → "platform default" (table-driven over the exported map) |
| 3 | BYOK indicator | shown iff `byokKeyPresent` |
| 4 | picker laziness | list fetch only on open |
| 5–9 | search / pin / delisted / deprecated / stale+empty banners | same assertions as 46-2's picker suite |
| 10 | free-text path | `modelsSupported:false` |
| 11 | save | PUT body; provenance flips to override; `pricingKnown:false` warning rendered |
| 12 | reset | confirm names platform default; DELETE; provenance flips back |
| 13 | 403 downgrade | PUT 403 → read-only state + message; no retry loop |
| 14 | member read-only | `canEdit:false` renders disclosure without save/reset |

## Definition of Done

- ACs met; tests 1–14 green in CI (this package's tests and typecheck both run there).
- No import from `packages/dashboard` (grep-checked); no raw `fetch` (lint).
- No hardcoded provider keys/model ids; provenance map is the sole text mapping.
- Route reachable by deep link in local dev (`vite` on port 3002) against a running API.
- Zero files changed under `packages/dashboard/` (that is 46-2).

## Dependencies & Sequencing

- **Blocked by:** 46-0, 46-1 (contracts); 45-2 (shared `App.tsx`/`AppLayout.tsx` edits — land
  after); 45-0 (CI typecheck gate this story's AC7 relies on).
- **Reachability:** Epic 45's infrastructure half (45-4/45-5/45-6) gates customer access, not this
  story's merge.
- **Shared-edit register:** `App.tsx` + `AppLayout.tsx` (45-2, 45-3 also edit them — additive
  route/nav blocks; merge-order coordination only).

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Epic 45 slips and this ships unreachable | stated in the story Priority + epic open question 3; merging early is deliberate (the code is small and its API contracts are pinned by then) |
| Auth payload lacks the role and `canEdit` guesses wrong | D2 makes the server the enforcement; test 13 pins the 403 downgrade so the wrong guess is a cosmetic flash, not a broken page |
| Duplicated picker drifts from 46-2's | cross-linking comments both ways; a future extraction story has both anchors; behaviours are pinned by parallel test suites |
| Fixture drift from DTOs | source comments + review checklist (the 45-1 lesson) |

## Effort Breakdown

| Task | Days |
|---|---|
| API module + types + auth inspection | 0.5 |
| Page + provenance + states | 0.75 |
| Picker + override lifecycle + 403 downgrade | 1.0 |
| Route/nav + tests + polish | 0.75 |
| **Total** | **3.0** |
