# Story 46-2: Platform admin UI — provider settings page in `packages/dashboard`

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

As the **platform owner**,
I want an admin-console page that lists every catalogue provider with its real status and lets me
pick each provider's platform default model from the provider's own live model list,
So that adopting a newly released model is a dropdown click — no code update, no config edit, no
redeploy — and a provider being down or unkeyed is visible instead of mysterious.

## Priority

P0 — the admin half of the product requirement's UI. Without it, 46-0/46-1 are API-only.

## Architectural Context (READ FIRST)

### The app and its conventions (follow, do not innovate)

- **Page placement:** standalone admin pages live under
  `packages/dashboard/src/pages/admin/{area}/` with an `__tests__/` sibling — the shape of
  `pages/admin/conventions/ConventionsAdminPage.tsx` and
  `pages/admin/acceptance-rules/AcceptanceRulesAdminPage.tsx`. This story adds
  `pages/admin/providers/ProvidersAdminPage.tsx` (+ components + tests).
- **Routing:** lazy-loaded route registered in `src/router.tsx` beside `/admin/prompts`
  (`router.tsx:184-192`) and `/admin/conventions` (`:193-201`), inheriting the same admin
  guard/layout wrapper. Path: `/admin/providers`. Imports use the repo's `.js`-suffix convention
  (`router.tsx:12-27`).
- **API client:** `src/services/admin/providers-api-client.ts` following the
  `conventions-api-client.ts` shape exactly — module-level `fetchJSON` with
  `VITE_API_BASE_URL ?? '/api'`, `credentials: 'include'`, typed `ApiError` with `status`/`code`
  (`conventions-api-client.ts:20-45`). Raw `fetch` outside a client module trips the repo's
  no-raw-fetch lint rule (`eslint.config.js:75-76` scope) — use the client.
- **Discoverability:** `AdminLayout.tsx` is a tab strip (`AdminLayout.tsx:26-34`) whose tabs are
  inline components, while the prompts/conventions/acceptance-rules pages are standalone routes
  that are NOT in the strip. Follow the standalone-route pattern, and add a link panel the way
  Tenants does (`TenantsLinkPanel`, `AdminLayout.tsx:36-53`): a `providers` tab entry rendering a
  `ProvidersLinkPanel` that links to `/admin/providers`.

### The API this page binds (46-0 + 46-1 — the contract, restated once)

| Call | Use |
|---|---|
| `GET /api/admin/providers` | the roster: `key`, `displayName`, `dialect`, `effectiveBaseUrl`, `keyStatus` (`configured`/`missing`/`not_required`), `modelsSupported`, `enabled`, `currentModel`, `source` (`platform-db`/`config`/`descriptor`), `aliases`, `transport?` |
| `GET /api/admin/providers/{key}/models` | the picker payload: `{models: [{id, displayName?, deprecated, current}], fetchedAt, stale, errorCode?}` — always 200 for a known key, current model always present |
| `PUT /api/admin/providers/{key}/settings` | save `{defaultModel?, enabled?}`; response carries `pricingKnown` + `warning?` |
| `DELETE /api/admin/providers/{key}/settings` | reset to config/descriptor |

The provenance rule from Story 43-1 applies: **the UI renders what the server reports and restates
nothing** — no hardcoded provider names, no baked-in model lists, no client-side copy of the
precedence order. The `source` badge text is the one permitted mapping (`platform-db` → "set here",
`config` → "from deployment config", `descriptor` → "built-in default").

## Acceptance Criteria

1. **Roster table.** `/admin/providers` renders one row per provider from
   `GET /api/admin/providers`: display name (+ key and aliases in a muted sub-line), dialect,
   key status (three-state, with `not_required` rendered as such — not as a green "configured"
   lie), enabled toggle (platform rows only), current model + source badge. Non-HTTP providers
   render with their transport and no model controls. Loading and error states per the page
   conventions (`ConventionsAdminPage` precedent); the error state keeps the page frame and
   offers retry.

2. **Model picker.** Expanding a row (or an "Edit" affordance) opens the picker for providers with
   `modelsSupported: true`:
   - fetches `GET /api/admin/providers/{key}/models` on open — not for all rows on page load;
   - a searchable dropdown (text filter over `id` + `displayName`; plain listbox + input filter is
     fine — do NOT add a combobox dependency the app doesn't have);
   - the entry flagged `current: true` is pre-selected and pinned at the top; when it is absent
     from the provider's live list it renders with a "no longer listed by the provider" marker;
   - entries with `deprecated: true` are visibly marked and sorted after non-deprecated ones;
   - `stale: true` renders a banner: "shown from cache — the provider could not be reached
     (`{errorCode}`)"; an empty list renders the banner plus a free-text input so the admin is
     never dead-ended.

3. **Free-text fallback.** Providers with `modelsSupported: false` (z-ai, azure-openai,
   github-copilot, non-HTTP) get a plain text input pre-filled with the current model, with helper
   text saying the provider does not expose a model list.

4. **Save / reset.** Save issues the PUT and re-fetches the roster row; a `pricingKnown: false`
   response surfaces the server's `warning` inline (non-blocking). Reset issues the DELETE behind
   a confirm step whose copy states what the fallback will be (from the row's would-be `source`).
   Both surface `ApiError` failures as inline errors, not toasts-only.

5. **Enable/disable.** The toggle PUTs `{enabled}` and reflects the response. Disabled providers
   render greyed with their controls inert except re-enable.

6. **No key material anywhere.** The page never renders, requests, or links an input for an API
   key. The key-status cell links to the existing secrets admin page (`/admin/secrets`,
   `router.tsx:237` area) for remediation.

7. **Tests** (vitest + jsdom, `pages/admin/providers/__tests__/ProvidersAdminPage.test.tsx`,
   mocking the API client module the way `AcceptanceRulesAdminPage.test.tsx` does): roster
   renders from mock; three-state key status; picker fetch-on-open only; search filters; delisted
   current selection shown + marked; deprecated marking + ordering; stale banner; empty-list
   free-text fallback; save PUT payload + pricing warning render; reset confirm + DELETE;
   disabled-provider inert state; error-state retry. **Fixtures are copied from the C# DTO shapes,
   not invented** — the Epic 45 lesson (45-1) stated as a review checklist item.

8. **Typecheck honesty.** `pnpm --filter @tamma/dashboard typecheck` is not in CI (Story 44-6
   owns that repair) — this story must still leave its own files clean under the package's
   tsconfig, verified locally and stated in the PR.

## Dependencies

- **Blocked by:** 46-0 (roster + models routes), 46-1 (settings routes, `source`/`enabled`).
- **Coordination — Story 44-6:** the admin app's excluded-from-CI test debt and typecheck repair.
  This story adds tests to that same excluded set; it does not fix the exclusion (44-6's job) and
  must not make it worse (AC8).
- **Blocks:** nothing.

## Out of Scope

- The tenant-facing picker (46-3).
- Any pricing display or editing (existing `pages/admin/pricing/` surface).
- BYOK/platform key entry (existing secrets surfaces).
- Model capability metadata in the picker.
- Adding a shared combobox/autocomplete component library.

## Estimated Effort

3 days

## Change Log

| Date       | Version | Changes                  | Author |
| ---------- | ------- | ------------------------ | ------ |
| 2026-07-27 | 1.0.0   | Initial story creation   | Claude |
