# Story 46-3: Tenant UI — model settings page in `packages/dashboard-user`

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

As a **tenant admin** (or the sole user of a single-user install),
I want a page in the customer app where I pick my organisation's model per provider from the
provider's live model list — fetched with my own BYOK key when I have one,
So that my team runs on the model I chose, newly released models are available to me the day the
provider lists them, and I can see whether a value comes from my override or the platform default.

## Priority

P1 — the tenant half of the product requirement. P1 rather than P0 only because its user-visible
value is gated on Epic 45 deploying the app it lives in; the code itself should land with the epic.

## Architectural Context (READ FIRST)

### The app this lives in — and its deployment reality

`packages/dashboard-user` is the SaaS customer app: built, tested (20 files / 103 tests, running
in CI via `ci.yml:49-50`), and **not yet deployed** — Epic 45 ships it (container 45-4, vhost
45-5, deploy 45-6, URL split 45-7). The same posture Story 44-6 takes toward its UI applies here:
**this story is implementable and mergeable today; it becomes reachable when Epic 45 lands.** If
Epic 45 slips badly, the interim option (NOT planned here) is a tenant-admin screen in the admin
console — epic README open question 3.

### App conventions (follow, do not innovate — and do not port admin-app patterns in)

- **API access:** `dashboard-user/src/api/client.ts` — the typed `ApiClient` with error hierarchy
  and single-shot refresh-on-401 (`client.ts:88-113`). The per-package client divergence from the
  admin app is **sanctioned** (Epic 45 README, "What is not a gap"); use this client, do not copy
  the admin app's `fetchJSON`. Note 45-1 adds `ApiClient.patch` — this story needs only
  get/put/delete.
- **API modules:** typed functions per area in `src/api/` (`alerts.ts`, `pricing.ts` precedents).
  Add `src/api/provider-models.ts`.
- **Pages/routes:** pages under `src/pages/{area}/`, routes declared in `App.tsx:39-88`. 45-2
  adds a catch-all and reworks nav links (`AppLayout.tsx:24,27,33`) — **coordination gate**: this
  story adds a real `/settings/models` route and nav entry; land after 45-2 so the nav rework and
  this addition do not fight over the same lines.
- **Auth/roles:** `useAuth.tsx` binds `/api/auth/me`. **Implementation task:** confirm whether the
  auth payload carries the caller's tenant role; if it does, derive `canEdit` from it; if not, do
  NOT invent a roles endpoint — render the editor optimistically and degrade on the server's 403
  (the server is the enforcement per the epic's RBAC table; the client's `canEdit` is cosmetic).

### The API this page binds (46-0 + 46-1)

| Call | Use |
|---|---|
| `GET /api/v1/agents/providers/models` | roster: enabled providers with `key`, `displayName`, `modelsSupported`, resolved `model`, `source`, `hasOverride`, `byokKeyPresent` |
| `GET /api/v1/agents/providers/{provider}/models` | picker payload — same envelope as the admin route; **server-side the fetch uses the tenant's BYOK key when present, else the platform key** (epic D5); the browser never sees a key |
| `GET /api/v1/agents/providers/{provider}/model` | resolved model + source + override |
| `PUT /api/v1/agents/providers/{provider}/model` | set the tenant override `{model}`; response carries `pricingKnown` + `warning?` |
| `DELETE /api/v1/agents/providers/{provider}/model` | remove the override → platform default |

Member users: all GETs succeed; PUT/DELETE return 403 (`AgentManage`). Single-user mode: the sole
user passes everything.

## Acceptance Criteria

1. **Roster page** at `/settings/models` (`src/pages/models/ModelSettingsPage.tsx`): one card/row
   per provider from the tenant roster route — display name, "your key" indicator when
   `byokKeyPresent` (metadata only), effective model, and a provenance line that says plainly
   whether it is *your override* (`tenant-override`) or *the platform default* (every other
   source renders as "platform default" — tenants do not see the platform's internal
   config/descriptor distinction). Loading/error/empty states; the error state keeps the frame
   and offers retry.

2. **Picker.** Same behaviours as 46-2 AC2, restated as binding here: fetch-on-open; searchable;
   current pinned + "no longer listed" marker; deprecated marked and sorted last; stale banner
   with error code; empty-list banner + free-text input; free-text path for
   `modelsSupported: false` providers.

3. **Override lifecycle.** Save PUTs the override and re-renders provenance as "your override";
   `pricingKnown: false` surfaces the server's `warning` inline, non-blocking. "Use platform
   default" issues the DELETE behind a confirm that names the platform default it will fall back
   to (from the row's resolved data). A 403 on either renders a clear "your role can view but not
   change models" state — and flips the page's `canEdit` so remaining controls render read-only.

4. **Member read-only.** With `canEdit` false (role-derived or 403-derived per the context note),
   pickers render as read-only disclosure (current model + provenance + the live list viewable),
   with no save/reset affordances.

5. **Nav + route.** Route registered in `App.tsx`; nav entry added under the app's settings
   grouping as reworked by 45-2. No dead links introduced; deep-link works (the app's auth guard
   handles the unauthenticated case).

6. **Tests** (vitest + jsdom, alongside the app's existing 20 test files, so they run in CI via
   the existing `ci.yml:49-50` filter): roster render; provenance wording for
   override vs platform; BYOK indicator; picker laziness/search/pin/delisted/deprecated/stale/
   empty/free-text; save + warning; reset confirm + fallback naming; 403 → read-only flip;
   member read-only render. **Fixtures copied from the C# DTOs with a source comment** — the
   45-1 lesson is a review checklist item here too.

7. **Typecheck stays green in CI.** `pnpm --filter @tamma/dashboard-user run typecheck` (in CI
   since 45-0) passes with the new files — including under `exactOptionalPropertyTypes`, the
   setting that produced 45-0's bug.

## Dependencies

- **Blocked by:** 46-0, 46-1 (routes); **45-2** (nav/catch-all rework — same files, land after);
  **45-0** (typecheck gate exists — otherwise AC7's guard is unverifiable in CI).
- **Reachability (not a code block):** Epic 45's 45-4/45-5/45-6 make the app deployable; this
  story merges independently of them.
- **Blocks:** nothing.

## Out of Scope

- BYOK key entry/rotation UI (exists on its own surface; this page only indicates presence).
- Any platform-level control (enable/disable, platform default) — 46-2's page.
- Per-member or per-agent model choice.
- The interim "tenant picker inside the admin console" fallback — decision-gated (epic open
  question 3), not speculatively built.

## Estimated Effort

3 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-27 | 1.0.0   | Initial story creation | Claude |
