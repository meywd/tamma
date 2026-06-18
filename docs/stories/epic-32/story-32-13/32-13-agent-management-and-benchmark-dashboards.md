# Story 32-13: Agent Management & Benchmark Dashboards (admin public + tenant private)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

## User Story

As a **platform admin** (managing the shared agent catalog) **and as a tenant owner/admin/member** (running and observing my own agents),
I want **two dashboards over the agent domain** — a platform-admin view to create, version, and govern **public/system** agents, personas, and margin config and to inspect anonymized cross-tenant public-agent leaderboards; and a tenant view to create and version **private** agents and personas, choose which agent serves each role, register my own provider keys (BYOK), and inspect **my own** action trail, run history, benchmarks, leaderboards, outcomes, and billable usage,
So that **definition governance and performance observability are surfaced where each principal owns them**, while the platform admin never sees a single tenant's performance data and a tenant never sees another tenant's.

## Priority

P2 — The human-facing surface over the Epic 32 agent domain. The data and APIs exist (32-2/32-6/32-9/32-10/32-12); this story makes them usable without the API substrate being reachable only by curl.

## Acceptance Criteria

1. **Admin: public agent + persona CRUD/versioning.** The admin dashboard (`packages/dashboard`) extends `settings/AgentsPage.tsx` with a tab/section to list, create, publish a new pinned version of, and archive **public/system** agents (and their personas from 32-12), driving the 32-2 endpoints with `visibility: "public"`. All write calls go through the in-handler `PlatformOwnerAccess` gate; a non-owner never reaches these controls (route is already behind `AdminGuard`). System (shipped-default) agents render as read-only catalog rows with a "system default" badge.
2. **Admin: margin/markup config + anonymized public-agent leaderboard.** The admin view includes a margin/markup config panel (a thin form over the 32-9/Epic-34 markup config it owns) and an **anonymized, cross-tenant** public-agent leaderboard view (success rate, avg iterations-to-done, cost basis, latency per public agent) — aggregated so **no single tenant's data is identifiable**; the view never calls a tenant-scoped trail/leaderboard endpoint and never accepts a `tenantId`.
3. **Tenant: private agent + persona management.** The tenant dashboard (`packages/dashboard-user`) adds a page to list (public ∪ own-private), create, version, and archive **private** agents and their personas, driving the 32-2 endpoints scoped to the caller's active tenant. Public agents appear as browsable, non-editable rows (an "Adopt"/"Select for role" affordance, no edit). Attempting to edit a public agent is not offered in the UI and 403s on the API.
4. **Tenant: per-role agent selection.** A "Roles" view lets a tenant owner/admin choose which agent (public or own-private) serves each role via `PUT /api/agents/role-selections/{role}` (32-2); the current resolved selection and its provenance (`system-public` / `tenant-public` / `tenant-private`) is shown per role; absent a tenant selection the UI shows the system-default agent the resolver would pick (never "none"/blank).
5. **Tenant: BYOK provider-key registration (reveal-once).** A "Provider keys" section registers/rotates/removes a tenant's own provider API key per provider via the 32-3 endpoints (`POST/DELETE /api/v1/agents/providers/{provider}/credential` and `…/rotate`). On create/rotate the plaintext is shown **exactly once** via the Epic 29 reveal-once UX (mirrors `SecretRevealModal`); the stored key is **never** re-displayed afterward — the list shows provider, source (`byok`/`platform`), storage-key ref, and last-rotated, never the secret.
6. **Tenant: own action trail + run history.** A per-agent detail view renders the tenant's **own** run history (`GET /api/v1/orgs/{tenantId}/agents/{agentId}/runs`) and flat action trail (`…/agents/{agentId}/trail`) from 32-6, with the documented filters (`from`/`to`, `role`, `provider`, `outcome`, `type` prefix) and cursor pagination. Every trail/runs call is bound to the caller's **own** `tenantId` (from `useAuth`); the UI never constructs a path with a foreign tenant id.
7. **Tenant: benchmarks, leaderboards, outcomes, usage.** A "Benchmarks" view renders the tenant's own leaderboards and benchmark charts from 32-10 sliceable by **agent / provider / prompt / persona** dimensions with **window selection** (e.g. 7d/30d/90d/all) and **min-sample messaging** (a "not enough samples (n<N)" note instead of a misleading bar when the window has too few runs), plus outcome breakdown (bug counts by `bugType`) and billable usage (tokens/cost from 32-9).
8. **Strict tenant isolation in the UI (asserted in tests).** No tenant-dashboard code path can request another tenant's trail/benchmarks/usage: the active tenant id is sourced only from the authenticated session (`useAuth().tenantId`), never from a route param the user can edit, a query string, or a dropdown of other tenants. A test asserts that every tenant-data fetch uses the session tenant id and that there is no UI affordance to enter or switch to a foreign tenant id. The API enforces it too (32-6 AC 4); the UI does not rely on that alone.
9. **Admin never sees a single tenant's data.** No admin-dashboard code path calls a tenant-scoped trail/leaderboard/usage endpoint or passes a `tenantId` to read performance; the admin leaderboard is the anonymized cross-tenant aggregate only. A test asserts the admin agents bundle imports no tenant-trail client and issues no `/orgs/{tenantId}/agents/.../trail` request.
10. **Member read-only gating.** SaaS `member`-role users get **read-only** views in the tenant dashboard: create/version/archive, role-selection writes, and BYOK register/rotate/remove controls are **hidden or disabled** (gated by `TenantAdminGuard` / role check) and the underlying API returns **403** for members per 32-2/32-3 RBAC. A 403 from the API surfaces as a "you need admin/owner role" message rather than a crash.
11. **BYOK reveal-once correctness.** The reveal modal shows the plaintext once, requires an "I have saved this value" acknowledgement before close, copies to clipboard, and zeroes its local state on dismiss; the parent burns the one-shot reveal exactly once and drops the plaintext from its own state — mirroring the 29-3/29-5 secrets UI exactly (reuse the component/pattern, do not re-implement loosely).
12. **Reuse existing primitives.** Both dashboards reuse existing patterns — the admin side reuses the `useAgentsConfig`/settings store + service-client conventions and common components (`Card`, `Badge`, `ConfirmDialog`, `LoadingSpinner`, `SecretRevealModal`); the tenant side reuses `apiClient` (`packages/dashboard-user/src/api/client.ts`), `useAuth`, `AuthGuard`/`TenantAdminGuard`, and the existing `api/*.ts` typed-module convention — **no new data layer, no new HTTP wrapper, no new state library** is introduced.
13. **Routing & navigation wiring.** Admin: the public-agent + leaderboard surface is reachable from `settings/AgentsPage` (extended in place, not replaced) and registered in `packages/dashboard/src/router.tsx` behind `AdminGuard`. Tenant: new routes (`/agents`, `/agents/:agentId`, `/settings/provider-keys`) are registered in `packages/dashboard-user/src/App.tsx` behind `AuthGuard` (+ `TenantAdminGuard` for the management/BYOK/role-selection routes) and linked from `layouts/AppLayout.tsx`.
14. **Tests.** Component/unit tests (Vitest + Testing Library, colocated `__tests__/` per existing pattern) with **mocked APIs** cover: admin-vs-tenant separation (AC 8/9), member read-only gating (AC 10), BYOK reveal-once (AC 11), per-role selection + provenance (AC 4), leaderboard rendering with window selection + min-sample messaging (AC 7), and 403/empty/error states. A small e2e/integration-style flow (admin creates a public agent; tenant browses it, selects it for a role, registers a BYOK key, views its own trail) runs against mocked endpoints.
15. **No regression.** The existing `AgentsOverview` role-config UI keeps working unchanged (the public-agent surface is additive to `AgentsPage`); `pnpm test --filter @tamma/dashboard` and `pnpm test --filter @tamma/dashboard-user` stay green; no new lint errors.

## Technical Design

### Where each surface lives (two dashboards, two principals)

Per the Epic 32 design spec tenancy rule and CLAUDE.md "Operating Modes", **definition ownership** and **data ownership** are separate, and the two dashboards map onto the two principals:

| Surface | Package | Principal | Guard |
|---|---|---|---|
| Public/system agent + persona CRUD/versioning | `packages/dashboard` (admin) | platform owner | `AdminGuard` + in-handler `PlatformOwnerAccess` |
| Margin/markup config | `packages/dashboard` (admin) | platform owner | `AdminGuard` |
| Anonymized public-agent leaderboard | `packages/dashboard` (admin) | platform owner | `AdminGuard` |
| Private agent + persona CRUD/versioning | `packages/dashboard-user` (tenant) | tenant owner/admin | `TenantAdminGuard` |
| Per-role agent selection | `packages/dashboard-user` (tenant) | tenant owner/admin | `TenantAdminGuard` |
| BYOK provider-key registration | `packages/dashboard-user` (tenant) | tenant owner/admin | `TenantAdminGuard` |
| Own action trail / runs / benchmarks / leaderboards / outcomes / usage | `packages/dashboard-user` (tenant) | tenant member (read) | `AuthGuard` |

> **The deleted `packages/api` is never referenced.** Backend endpoints are served by the C# control-plane in `apps/tamma-elsa` (`Tamma.Api`) per 32-2/32-3/32-6/32-9/32-10/32-12. This story is **dashboards only** — it wires React UIs to those already-built endpoints.

### Admin dashboard — `packages/dashboard`

`settings/AgentsPage.tsx` is **extended in place** (AC 1/15): the existing `<AgentsOverview/>` role-config card stays; a tabbed shell is added around it with new tabs **Public Agents**, **Margin**, and **Leaderboard**.

```
packages/dashboard/src/
  pages/settings/AgentsPage.tsx                        (MODIFY — add tab shell; keep AgentsOverview)
  components/settings/agents/
    PublicAgentsPanel.tsx           (NEW — list/create/version/archive public agents)
    PublicAgentForm.tsx             (NEW — create + new-version form; provider chain / model / prompt / budget / persona)
    PersonaManager.tsx              (NEW — persona CRUD for an agent, 32-12)
    MarginConfigPanel.tsx           (NEW — markup/margin config form, 32-9/Epic-34)
    PublicLeaderboardPanel.tsx      (NEW — anonymized cross-tenant public-agent leaderboard)
    LeaderboardChart.tsx            (NEW — shared chart; window selector + min-sample note)
  hooks/settings/
    usePublicAgents.ts              (NEW — list/create/version/archive via service client)
    usePublicLeaderboard.ts         (NEW — anonymized aggregate; NO tenantId param)
    useMarginConfig.ts              (NEW)
  services/settings/
    agents-admin-client.ts          (NEW — typed client for /api/admin/agents + /api/agents public writes + admin leaderboard + margin)
  router.tsx                                            (MODIFY — keep /settings/agents behind AdminGuard)
```

Admin API calls (gated by `AdminGuard` + server `PlatformOwnerAccess`):
- `GET    /api/agents?visibility=public` — list public/system agents (32-2).
- `POST   /api/agents` with `{ visibility: "public", ... }` — create public agent (32-2; server 403s non-owner).
- `POST   /api/agents/{id}/versions` — publish pinned version (32-2).
- `POST   /api/agents/{id}/archive` — archive (32-2).
- Persona CRUD for an agent (32-12 endpoints).
- `GET    /api/admin/agents/leaderboard` — **anonymized cross-tenant** public-agent aggregate (32-10 admin projection; no `tenantId`).
- Margin/markup config GET/PUT (32-9 / Epic-34).

> **Isolation guarantee (AC 9):** `agents-admin-client.ts` exposes **no** method that takes a `tenantId` and **no** `/orgs/{tenantId}/...` path. A unit test greps the compiled client surface to prove it.

### Tenant dashboard — `packages/dashboard-user`

```
packages/dashboard-user/src/
  api/
    agents.ts            (NEW — list/create/version/archive private agents; role-selections; resolve)
    agent-trail.ts       (NEW — runs + trail for OWN tenant; benchmarks/leaderboards/outcomes/usage)
    provider-keys.ts     (NEW — BYOK register/rotate/remove via 32-3)
  pages/agents/
    AgentsListPage.tsx           (NEW — public ∪ own-private; create/version/archive [admin]; browse [member])
    AgentDetailPage.tsx          (NEW — tabs: Overview · Runs · Trail · Benchmarks · Outcomes · Usage)
    RoleSelectionsPage.tsx       (NEW — per-role agent picker + provenance)
  pages/settings/
    ProviderKeysPage.tsx         (NEW — BYOK register/rotate/remove; reveal-once)
  components/agents/
    AgentCard.tsx                (NEW)
    AgentForm.tsx                (NEW — create/version; persona section)
    RunsTable.tsx                (NEW — paginated runs; filters)
    ActionTrailTable.tsx         (NEW — paginated flat trail; type/role/provider/outcome filters)
    LeaderboardView.tsx          (NEW — agent/provider/prompt/persona dimension + window selector + min-sample note)
    OutcomeBreakdown.tsx         (NEW — bug counts by bugType)
    UsagePanel.tsx               (NEW — tokens/cost)
    ProviderKeyRow.tsx           (NEW)
    ProviderKeyRevealModal.tsx   (NEW — mirrors dashboard SecretRevealModal reveal-once contract)
  App.tsx                        (MODIFY — register routes under AuthGuard / TenantAdminGuard)
  layouts/AppLayout.tsx          (MODIFY — add Agents nav links)
```

Tenant API calls (all bound to `useAuth().tenantId`):
- `GET  /api/agents?role=&visibility=&status=` — list (public ∪ own-private) (32-2).
- `POST /api/agents` (private create), `POST /api/agents/{id}/versions`, `POST /api/agents/{id}/archive` (32-2; member → 403).
- `PUT  /api/agents/role-selections/{role}` + `GET /api/agents/resolve?role=&phase=` — selection + provenance (32-2).
- `GET  /api/v1/orgs/{tenantId}/agents/{agentId}/runs` and `…/trail` — own runs + trail (32-6).
- 32-10 leaderboard/benchmark + 32-9 usage/cost reads (own tenant only).
- `POST/DELETE /api/v1/agents/providers/{provider}/credential`, `POST …/rotate` — BYOK (32-3; create/rotate → reveal-once envelope).

> **Isolation guarantee (AC 8):** every method in `agent-trail.ts` takes the tenant id from a single `getActiveTenantId()` helper that reads `useAuth().tenantId`; there is no parameter, route segment, or input that lets the user supply a different tenant. A test renders the pages and asserts each network call's path contains the session tenant id and that no tenant-switcher control exists.

### RBAC gating in the UI (AC 10)

- Management routes (`/agents` create/version/archive, `/settings/provider-keys`, `/agents/roles`) sit behind `TenantAdminGuard` (admin/owner only); members hitting the route see the existing "Admin-only" panel.
- Read routes (`/agents/:agentId` detail, runs/trail/benchmarks/usage) sit behind `AuthGuard` only — members can observe.
- Within a page reachable by members, write affordances are hidden when `useAuth().role` is `member`; if the API still returns 403 (defense in depth), the error is rendered as a role message, not a crash.

### Reveal-once BYOK (AC 5/11)

Reuse the exact 29-3 contract embodied by `packages/dashboard/src/components/secrets/SecretRevealModal.tsx`: parent posts to the 32-3 credential create/rotate endpoint → server returns a reveal envelope (`revealToken`/`revealUrl`, **no plaintext in the create body**) → parent GETs the reveal URL **once** → mounts `ProviderKeyRevealModal` with the plaintext → user copies, acknowledges, closes → both component and parent drop the plaintext. The provider-key list never has a "show" action — only register / rotate / remove.

### Min-sample & window messaging (AC 7)

`LeaderboardChart` / `LeaderboardView` accept a `window` (`7d|30d|90d|all`) and per-row sample count `n`. When `n < minSamples` (config, default e.g. 5), the row renders a muted "not enough samples (n={n})" note instead of a bar/number, so a single lucky run never tops a leaderboard. Dimensions are a tab/segmented control: **agent · provider · prompt · persona**.

## Dependencies

- **Prerequisite (hard): Story 32-2** — Agent registry, resolution & RBAC API. Supplies `GET/POST /api/agents`, `/api/agents/{id}/versions`, `/archive`, `PUT /api/agents/role-selections/{role}`, `GET /api/agents/resolve`, the `system-public`/`tenant-public`/`tenant-private` provenance, and the `PlatformOwnerAccess` / member-403 gates this UI drives.
- **Prerequisite (hard): Story 32-6** — Agent action trail in tenant store. Supplies `GET /api/v1/orgs/{tenantId}/agents/{agentId}/runs` and `…/trail` with filters/pagination and the structural tenant isolation the tenant detail view relies on.
- **Prerequisite (hard): Story 32-10** — Benchmark projections & leaderboards. Supplies the per-tenant leaderboard/benchmark reads (agent/provider/prompt slices) and the anonymized cross-tenant public-agent aggregate the admin view renders.
- **Prerequisite (hard): Story 32-12** — Agent personas & persona-aware benchmarking. Supplies persona CRUD endpoints and the **persona** leaderboard dimension.
- **Prerequisite (hard): Story 32-3** — Per-tenant provider credential resolution (BYOK → platform). Supplies the BYOK register/rotate/remove endpoints and the reveal-once envelope.
- **Reuses:** `packages/dashboard` — `useAgentsConfig` + settings store, `services/settings/settings-api-client.ts` conventions, `components/secrets/SecretRevealModal.tsx`, `components/common/*` (`Card`, `Badge`, `ConfirmDialog`, `LoadingSpinner`, `Toggle`, `Slider`), `guards/AdminGuard.tsx`. `packages/dashboard-user` — `api/client.ts` (`apiClient`), `hooks/useAuth.tsx`, `guards/AuthGuard.tsx` + `TenantAdminGuard.tsx`, `layouts/AppLayout.tsx`, the `api/*.ts` typed-module pattern.
- **Related:** Epic 27 (Prompt Store — the per-mode RBAC + admin/tenant dashboard split this mirrors), Epic 29 (secret cabinet — the reveal-once UX), Epic 34/35/36 (consume the margin/usage surfaced here; not implemented by this story).

## Testing Strategy

1. **Admin component tests** (`packages/dashboard`, Vitest + Testing Library, colocated `__tests__/`): `PublicAgentsPanel` renders list/create/version/archive against mocked `agents-admin-client`; system-default rows are read-only with the badge; `MarginConfigPanel` GET/PUT round-trip; `PublicLeaderboardPanel` renders the anonymized aggregate with window selector and min-sample notes.
2. **Admin isolation test (AC 9):** assert `agents-admin-client.ts` exposes no `tenantId`-taking method and no `/orgs/{tenantId}/...` path; render the admin agents surface and assert no request to a tenant-scoped trail/leaderboard endpoint is made.
3. **Tenant component tests** (`packages/dashboard-user`): `AgentsListPage` shows public ∪ own-private with edit only on private; `RoleSelectionsPage` shows provenance and writes selections; `AgentDetailPage` runs/trail tables paginate and apply filters; `LeaderboardView` renders agent/provider/prompt/persona dimensions with window + min-sample messaging; `OutcomeBreakdown`/`UsagePanel` render from mocked 32-9/32-10 responses.
4. **Tenant isolation test (AC 8):** mock `apiClient`; render each tenant page; assert every captured request path contains the session `tenantId` from `useAuth` and that there is **no** input/route/dropdown to supply a foreign tenant id; attempt to manually craft a foreign-tenant fetch is not reachable from any rendered control.
5. **Member read-only test (AC 10):** with `useAuth().role === 'member'`, assert create/version/archive/select/BYOK controls are hidden/disabled and that management routes render the `TenantAdminGuard` "Admin-only" panel; simulate a 403 from a write call and assert the role message renders (no crash).
6. **BYOK reveal-once test (AC 11):** mock create/rotate → reveal envelope → reveal GET; assert plaintext shows once, "Close" disabled until acknowledged, copy works, and after close the plaintext is gone from state and the list never re-displays it; assert the reveal GET is invoked exactly once.
7. **e2e-style flow (mocked endpoints):** admin creates a public agent → tenant lists it (browse-only) → tenant selects it for a role (provenance `tenant-public`) → tenant registers a BYOK key (reveal-once) → tenant opens the agent detail and sees its own (empty-then-populated) trail. All against mocked clients; asserts the admin and tenant code paths never cross tenant/aggregate boundaries.
8. **No-regression:** existing `AgentsOverview` tests and the rest of both dashboards' suites stay green; `pnpm test --filter @tamma/dashboard` and `--filter @tamma/dashboard-user`.

## Estimated Effort

6-7 days

## Files Created/Modified

| File | Action |
|------|--------|
| `packages/dashboard/src/pages/settings/AgentsPage.tsx` | Modify (add tab shell; keep `AgentsOverview`) |
| `packages/dashboard/src/components/settings/agents/PublicAgentsPanel.tsx` | Create |
| `packages/dashboard/src/components/settings/agents/PublicAgentForm.tsx` | Create |
| `packages/dashboard/src/components/settings/agents/PersonaManager.tsx` | Create |
| `packages/dashboard/src/components/settings/agents/MarginConfigPanel.tsx` | Create |
| `packages/dashboard/src/components/settings/agents/PublicLeaderboardPanel.tsx` | Create |
| `packages/dashboard/src/components/settings/agents/LeaderboardChart.tsx` | Create |
| `packages/dashboard/src/components/settings/agents/__tests__/PublicAgentsPanel.test.tsx` | Create |
| `packages/dashboard/src/components/settings/agents/__tests__/PublicLeaderboardPanel.test.tsx` | Create |
| `packages/dashboard/src/hooks/settings/usePublicAgents.ts` | Create |
| `packages/dashboard/src/hooks/settings/usePublicLeaderboard.ts` | Create |
| `packages/dashboard/src/hooks/settings/useMarginConfig.ts` | Create |
| `packages/dashboard/src/services/settings/agents-admin-client.ts` | Create |
| `packages/dashboard/src/services/settings/__tests__/agents-admin-client.test.ts` | Create (isolation assertion) |
| `packages/dashboard-user/src/api/agents.ts` | Create |
| `packages/dashboard-user/src/api/agent-trail.ts` | Create |
| `packages/dashboard-user/src/api/provider-keys.ts` | Create |
| `packages/dashboard-user/src/pages/agents/AgentsListPage.tsx` | Create |
| `packages/dashboard-user/src/pages/agents/AgentDetailPage.tsx` | Create |
| `packages/dashboard-user/src/pages/agents/RoleSelectionsPage.tsx` | Create |
| `packages/dashboard-user/src/pages/settings/ProviderKeysPage.tsx` | Create |
| `packages/dashboard-user/src/components/agents/AgentCard.tsx` | Create |
| `packages/dashboard-user/src/components/agents/AgentForm.tsx` | Create |
| `packages/dashboard-user/src/components/agents/RunsTable.tsx` | Create |
| `packages/dashboard-user/src/components/agents/ActionTrailTable.tsx` | Create |
| `packages/dashboard-user/src/components/agents/LeaderboardView.tsx` | Create |
| `packages/dashboard-user/src/components/agents/OutcomeBreakdown.tsx` | Create |
| `packages/dashboard-user/src/components/agents/UsagePanel.tsx` | Create |
| `packages/dashboard-user/src/components/agents/ProviderKeyRow.tsx` | Create |
| `packages/dashboard-user/src/components/agents/ProviderKeyRevealModal.tsx` | Create |
| `packages/dashboard-user/src/pages/agents/__tests__/AgentsListPage.test.tsx` | Create |
| `packages/dashboard-user/src/pages/agents/__tests__/AgentDetailPage.test.tsx` | Create (incl. tenant-isolation) |
| `packages/dashboard-user/src/pages/agents/__tests__/RoleSelectionsPage.test.tsx` | Create |
| `packages/dashboard-user/src/pages/settings/__tests__/ProviderKeysPage.test.tsx` | Create (reveal-once) |
| `packages/dashboard-user/src/components/agents/__tests__/LeaderboardView.test.tsx` | Create (window + min-sample) |
| `packages/dashboard-user/src/App.tsx` | Modify (register routes + guards) |
| `packages/dashboard-user/src/layouts/AppLayout.tsx` | Modify (add Agents nav links) |
| `packages/dashboard/src/router.tsx` | Modify (keep `/settings/agents` AdminGuard; no new admin route needed if tabbed) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions
3. Read the dependency stories: 32-2 (agent CRUD/RBAC + provenance), 32-3 (BYOK reveal-once), 32-6 (trail/runs API + isolation), 32-10 (leaderboards), 32-12 (personas)
4. Read the Epic 32 design spec `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` (the tenancy rule in §"Ownership, visibility & data scoping" is load-bearing)
5. Planned a TDD approach (Red-Green-Refactor) — tests for separation/isolation/member-gating/reveal-once first

### `packages/api` is gone — this is a TS React story only

Do **not** create or reference anything under `packages/api`. The backend lives in `apps/tamma-elsa` (`Tamma.Api`) and is implemented by 32-2/32-3/32-6/32-9/32-10/32-12. This story consumes those HTTP endpoints from React.

### Two dashboards, two clients

`packages/dashboard` uses the bare-`fetch` `fetchJSON` convention (see `services/settings/settings-api-client.ts` and `services/secrets/secrets-api-client.ts`) with `VITE_API_BASE_URL ?? '/api'`. `packages/dashboard-user` uses the `ApiClient` class (`api/client.ts`) with `credentials: 'include'` + refresh-on-401 and per-domain `api/*.ts` modules (see `api/dashboard.ts`). **Follow each package's own convention** — do not port one into the other.

### Tenant id is session-only, never user input

In `packages/dashboard-user`, the active tenant id comes from `useAuth().tenantId`. Build every `/api/v1/orgs/{tenantId}/...` path from that single source via a small helper; never accept a tenant id from a route param, query string, form field, or dropdown. This is AC 8 and the difference between "isolation enforced by the server" and "isolation the UI can't even ask to violate."

### Anonymized admin leaderboard is a different endpoint

The admin public-agent leaderboard (AC 2) is **not** a tenant leaderboard with the tenant id stripped client-side — it must be a server-side anonymized aggregate (32-10's admin projection). The admin client must not have the ability to read a single tenant's data at all (AC 9). If the 32-10 admin aggregate endpoint is not yet present, this story's admin leaderboard panel renders an explicit "aggregate not available" empty state rather than falling back to a tenant-scoped call.

### Reuse `SecretRevealModal`, don't re-invent

The BYOK reveal-once flow must match the secrets UI contract precisely (one-shot GET burned by the parent, acknowledge-before-close, local-state zeroing). Prefer importing/adapting the existing `SecretRevealModal` behaviour over a fresh modal; a loosely-built reveal modal that re-fetches or re-renders the plaintext is a security defect.

### Min-sample messaging is a correctness feature, not polish

A leaderboard that ranks an agent #1 on a single successful run is misleading. The `n < minSamples` muted-note path (AC 7) is required, not optional, and is unit-tested.

## Logging Requirements

(Dashboards are browser SPAs — "logging" here is client-side observability + error surfacing, not Pino server logs.)

- **INFO/console.debug (dev only):** route entered, list/leaderboard fetched (counts + window), role selection saved, BYOK key registered/rotated/removed (provider + storage-key ref **only**).
- **User-visible WARN/ERROR:** API 403 → "you need admin/owner role" message; API 404 (e.g. cross-tenant/foreign agent) → "not found"; reveal GET failure → "could not reveal key, rotate to retry"; min-sample → muted "not enough samples (n={n})" note.
- **Credential safety:** **NEVER** log, store in component state beyond the reveal modal's one-shot, or render-after-reveal a BYOK plaintext or any reveal token. The provider-key list shows provider/source/storage-key/last-rotated only. No secret in URLs, query strings, or telemetry.
- **Isolation safety:** never log or render another tenant's id; the only tenant id in scope is the session's own.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
