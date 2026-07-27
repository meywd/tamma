# Implementation Plan — Story 46-2: Platform Admin Provider Settings UI

## Scope & Deliverable

When this story is done, the admin console has `/admin/providers`: a roster of all catalogue
providers with key status, enabled state, and current model + provenance; a per-provider searchable
model picker fed live from `GET /api/admin/providers/{key}/models`; save/reset against the 46-1
settings routes; a free-text path for unlistable providers; and a link panel in the admin tab
strip. All state comes from the server; the page restates no provider or model knowledge.

## Pre-Reading

- `docs/stories/epic-46/README.md` — D5/D6 (what the API guarantees the UI)
- `docs/stories/epic-46/story-46-0/…` + `story-46-1/…` — the exact response contracts
- `packages/dashboard/src/services/admin/conventions-api-client.ts:1-60` — client module shape
- `packages/dashboard/src/pages/admin/conventions/ConventionsAdminPage.tsx` and
  `pages/admin/acceptance-rules/AcceptanceRulesAdminPage.tsx` (+ their `__tests__/`) — page,
  loading/error, and test conventions
- `packages/dashboard/src/router.tsx:50,173-244` — lazy admin routes; `.js` import suffixes
- `packages/dashboard/src/pages/admin/AdminLayout.tsx:26-53` — tab strip + `TenantsLinkPanel`
  precedent
- `docs/stories/epic-45/README.md` Gap 4 — the fixture-from-DTO lesson this story's tests must
  honour

## Design Decisions

- **D1 — Fetch-on-open for model lists.** The roster endpoint is static-data-fast by design
  (46-0 keeps model fetches out of it). Fetching 15 model lists on page load would hammer
  providers and spend the 5-minute cache on rows nobody opens. The picker fetches when opened;
  the server cache makes reopening cheap.

- **D2 — One page component + three small children, no new shared primitives.**
  `ProvidersAdminPage` (roster + state), `ProviderRow` (status cells + expand),
  `ModelPicker` (search/list/pin/deprecated/stale/free-text), `ResetConfirm` (inline confirm).
  Epic 43-7/44-6 are already contending over shared admin UI primitives; this story stays out of
  that by keeping its components local to `pages/admin/providers/`.

- **D3 — Search is client-side filtering of the fetched list.** Lists are at most a few hundred
  entries (OpenRouter is the largest); a `useMemo` filter over id + displayName is enough. No
  server-side search parameter exists and none is requested.

- **D4 — Provenance badge mapping is the page's only text-mapping table**, kept in one exported
  const so tests can import it rather than restating strings.

## Implementation Steps

1. **`providers-api-client.ts`** — types copied from the C# DTOs (roster row, model list envelope,
   settings PUT/DELETE payloads + responses incl. `pricingKnown`/`warning`), `fetchJSON` module
   pattern, exported functions: `listProviders`, `listProviderModels(key)`,
   `putProviderSettings(key, body)`, `deleteProviderSettings(key)`.
2. **`ProvidersAdminPage`** — load roster, render table, loading/error-with-retry states.
3. **`ProviderRow`** — status cells (three-state key status, source badge via D4's map, enabled
   toggle), expand affordance; non-HTTP/`modelsSupported:false` variants.
4. **`ModelPicker`** — fetch-on-open, search filter, current-pinned (+ delisted marker),
   deprecated marking/ordering, stale/empty banners, free-text fallback, save + pricing warning.
5. **`ResetConfirm`** — confirm with fallback statement, DELETE, row refresh.
6. **Router + link panel** — lazy route beside `/admin/conventions`; `ProvidersLinkPanel` +
   `providers` tab in `AdminLayout` (follow `TenantsLinkPanel`, `AdminLayout.tsx:36-53`).
7. **Tests** — per AC7, mocking the client module; fixture objects pasted from the C# DTO shapes
   with a comment naming the source file.
8. **Local verification** — `pnpm --filter @tamma/dashboard test` (the package-local run works
   even though CI excludes it — 44-6's finding) and package-local typecheck for the new files.

## Data & Migrations

None (server work is 46-0/46-1).

## Events

None emitted client-side; mutations are audited server-side by 46-1.

## Test Plan

Vitest + jsdom in `pages/admin/providers/__tests__/`:

| # | Test | Asserts |
|---|---|---|
| 1 | roster renders | one row per fixture provider; names/dialects from fixture only |
| 2 | key status | three states rendered distinctly; `not_required` not shown as configured |
| 3 | picker laziness | client's `listProviderModels` called only after expand |
| 4 | search | filter over id + displayName |
| 5 | current pinned | pre-selected, top position |
| 6 | delisted current | marker shown when fixture omits current from `models` but envelope injects it flagged |
| 7 | deprecated | marked and ordered after fresh entries |
| 8 | stale banner | rendered with errorCode from fixture |
| 9 | empty list | banner + free-text input usable, save enabled |
| 10 | free-text providers | `modelsSupported:false` renders text input path |
| 11 | save | PUT body exact; roster row refreshed; `pricingKnown:false` warning rendered |
| 12 | reset | confirm shows fallback source; DELETE called; row refreshed |
| 13 | disabled provider | greyed, controls inert except re-enable |
| 14 | error + retry | roster error state retries |

## Definition of Done

- ACs met; tests 1–14 green in the package-local run.
- New files clean under `packages/dashboard`'s tsconfig (stated + shown in PR).
- No raw `fetch` outside the client module (lint green).
- No hardcoded provider keys, model ids, or precedence text anywhere in `pages/admin/providers/`
  (grep-checked in review; the D4 badge map is the sole allowed mapping).
- Zero files changed under `packages/dashboard-user/` (that is 46-3).

## Dependencies & Sequencing

- **Blocked by:** 46-0 + 46-1 (contracts). UI scaffolding against fixture data can start once
  46-0's DTOs merge; do not invent shapes ahead of them.
- **Shared-edit register:** `router.tsx` and `AdminLayout.tsx` are touched by several epics'
  UI stories; the edits here are additive single blocks — coordinate ordering at merge, not
  design.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Fixtures drift from C# DTOs (the 45-1 failure mode) | fixtures carry a source-file comment; review checklist item; contract fields exercised (esp. `pricingKnown`, `stale`, `current`) |
| Admin app test debt hides breakage (tests excluded from CI) | package-local runs in the DoD; 44-6 owns the CI repair — noted, not absorbed |
| Picker UX grows a component library | D2 keeps components local; Out of Scope bans new dependencies |

## Effort Breakdown

| Task | Days |
|---|---|
| API client + types + fixtures | 0.5 |
| Page + row + toggle + states | 0.75 |
| Picker (search/pin/deprecated/stale/free-text) + save/reset | 1.0 |
| Router/layout wiring + tests + polish | 0.75 |
| **Total** | **3.0** |
