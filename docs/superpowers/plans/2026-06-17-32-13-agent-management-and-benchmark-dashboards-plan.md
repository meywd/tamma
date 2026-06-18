# Story 32-13 — Agent Management & Benchmark Dashboards (admin public + tenant private)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan wave-by-wave. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every wave writes tests
> before implementation.

**Goal:** Surface the Epic 32 agent domain in the two React dashboards. A **platform-admin** view
(`packages/dashboard`) to manage **public/system** agents, personas, and margin config and to view
an **anonymized** cross-tenant public-agent leaderboard; and a **tenant** view
(`packages/dashboard-user`) to manage **private** agents/personas, select which agent serves each
role, register **BYOK** provider keys (reveal-once), and view its **own** action trail, run history,
benchmarks, leaderboards, outcomes, and usage. Strictly: a tenant sees only its own performance
data; a platform admin never sees a single tenant's data. `settings/AgentsPage` is **extended**, not
replaced.

**Story file:** `docs/stories/epic-32/story-32-13/32-13-agent-management-and-benchmark-dashboards.md`
**Design spec:** `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`
(§"Ownership, visibility & data scoping" is the load-bearing tenancy rule).

**Tech stack:** TypeScript + React + Vite, Vitest 3.x + Testing Library, colocated `__tests__/`.
Two dashboards with two HTTP conventions:
- `packages/dashboard` (admin) — bare-`fetch` `fetchJSON`, `VITE_API_BASE_URL ?? '/api'`
  (see `services/settings/settings-api-client.ts`, `services/secrets/secrets-api-client.ts`).
- `packages/dashboard-user` (tenant) — `ApiClient` class with `credentials: 'include'` +
  refresh-on-401 (`api/client.ts`), per-domain `api/*.ts` modules (see `api/dashboard.ts`).

**Backend:** already built in `apps/tamma-elsa` (`Tamma.Api`) by 32-2 (agent CRUD/RBAC + provenance),
32-3 (BYOK reveal-once), 32-6 (trail/runs + isolation), 32-9 (usage/cost + margin), 32-10
(leaderboards), 32-12 (personas). **`packages/api` is deleted and must never be referenced.**

---

## Non-goals (YAGNI guard)

- **NO backend code.** Every endpoint exists per the dependency stories; this plan is dashboards only.
  If a needed read endpoint (e.g. 32-10 admin anonymized aggregate, 32-9 usage) is genuinely absent,
  render an explicit empty state — do **not** synthesize it client-side from a tenant-scoped call.
- **NO new HTTP wrapper, state library, or data layer.** Reuse each package's existing client +
  hook/store conventions (story AC 12). Admin reuses `useAgentsConfig`/settings store + `fetchJSON`;
  tenant reuses `apiClient` + `api/*.ts` modules.
- **NO tenant-switcher / impersonation UI.** The tenant id is `useAuth().tenantId`, full stop.
- **NO per-user (member) personalization layer.** Members are read-only (mirrors Prompt Store).
- **NO replacing `AgentsOverview`.** `AgentsPage` gains a tab shell; the role-config card stays.
- **NO new charting dependency** unless the repo already has one — otherwise render simple
  CSS/SVG bars (leaderboards are tables-with-bars, not a BI tool).

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### Admin dashboard (`packages/dashboard`)
- `pages/settings/AgentsPage.tsx` — thin wrapper rendering `<AgentsOverview/>`; route
  `/settings/agents` is behind `AdminGuard` in `router.tsx`. **Extend this page in place.**
- `components/settings/agents/` already holds `AgentsOverview`, `AgentRoleCard`,
  `ProviderChainEditor`, `ProviderEntryForm` — the new public-agent UI lives alongside them.
- `hooks/settings/useAgentsConfig.ts` + `stores/settings/store.ts` — the load/save pattern to mirror
  for `usePublicAgents`/`useMarginConfig`.
- `services/settings/settings-api-client.ts` — `fetchJSON<T>` against `VITE_API_BASE_URL ?? '/api'`;
  `services/secrets/secrets-api-client.ts` — richer example incl. `credentials: 'include'`,
  typed error class, reveal envelope types. Model `agents-admin-client.ts` on these.
- `components/secrets/SecretRevealModal.tsx` — the reveal-once contract (acknowledge-before-close,
  parent burns the one-shot GET, local-state zeroing). The BYOK modal mirrors this.
- `components/common/` — `Card`, `Badge`, `ConfirmDialog`, `LoadingSpinner`, `Toggle`, `Slider`,
  `FormField`. Reuse, don't rebuild.
- `guards/AdminGuard.tsx` — role check via `useCurrentUser`; already wraps the agents route.
- Tabbed-shell precedent: `pages/admin/AdminLayout.tsx` (`AdminTab` union + `TABS` + button nav).
  Copy this pattern into `AgentsPage`.

### Tenant dashboard (`packages/dashboard-user`)
- `api/client.ts` — `ApiClient` (`get/post/put/delete`, `credentials: 'include'`, refresh-on-401,
  `ApiError`/`UnauthorizedError`); singleton `apiClient`. **All tenant calls go through this.**
- `api/dashboard.ts` — the per-domain typed-module pattern (`/api/v1/orgs/${tenantId}/...`). Model
  `agents.ts`/`agent-trail.ts`/`provider-keys.ts` on it.
- `hooks/useAuth.tsx` — `AuthUser { id, ..., tenantId?, role? }`; `useAuth()` gives the **only**
  source of the active tenant id. (AC 8 hinges on this.)
- `guards/AuthGuard.tsx` (any authenticated) + `guards/TenantAdminGuard.tsx` (admin/owner; renders
  an "Admin-only" panel for members) — the read-vs-write gate.
- `App.tsx` — `BrowserRouter`/`Routes`; nested routes under `AuthGuard → AppLayout`, with
  `TenantAdminGuard` wrapping admin-only routes (see `/settings/alerts`). Add agent routes here.
- `layouts/AppLayout.tsx` — minimal sidebar `<Link>` nav; add Agents links.
- **No agent UI exists yet** in this package — green field for the tenant surface.

### Endpoint shapes (from dependency stories)
- **32-2:** `GET /api/agents?role=&visibility=&status=`, `POST /api/agents` (body `visibility`
  `public|private`; server 403 `agent_public_write_forbidden` for tenant→public),
  `GET /api/agents/{id}`, `POST /api/agents/{id}/versions`, `POST /api/agents/{id}/archive`,
  `PUT /api/agents/role-selections/{role}`, `GET /api/agents/resolve?role=&phase=` →
  `ResolvedAgentConfig { AgentId, AgentVersion, Source: system-public|tenant-public|tenant-private }`.
  Member → 403 on writes; cross-tenant private read → 404.
- **32-3:** `POST/DELETE /api/v1/agents/providers/{provider}/credential`, `POST …/rotate`;
  create/rotate → reveal envelope (token/url, **no plaintext in body**); list shows
  source `byok|platform` + storage-key ref, never the secret. RBAC: tenant_owner/admin; member → 403.
- **32-6:** `GET /api/v1/orgs/{tenantId}/agents/{agentId}/runs` (filters `from/to/role/provider/
  outcome`) and `…/trail` (+ `type` prefix), cursor/`SequenceNumber` paging,
  `RequireTenantMembershipFilter` (MemberAccess). **No cross-tenant / platform-admin read path.**
- **32-10:** per-tenant leaderboards sliceable by agent/provider/prompt; admin anonymized
  cross-tenant public-agent aggregate (separate projection — no `tenantId`).
- **32-12:** persona CRUD per agent + the **persona** leaderboard dimension.
- **32-9:** usage/cost reads (own tenant); margin/markup config (admin; Epic-34 owns the engine).

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who manages **public/system** agents + personas + margin? | The sole user edits their instance's shipped+own catalog; "public" = shipped system agents. | Platform owner only (`PlatformOwnerAccess`); admin dashboard, `AdminGuard`. |
| Who manages **private** agents + personas + role selection + BYOK? | The sole user. | `tenant_owner`/`tenant_admin`; `member` → read-only + 403. Tenant dashboard, `TenantAdminGuard`. |
| Whose performance/trail/benchmarks does the tenant view show? | The user's own. | The caller's own tenant only (`useAuth().tenantId`); never another tenant's. |
| What does the admin leaderboard show? | The user's instance public-agent aggregate. | **Anonymized cross-tenant** aggregate; admin never sees a single tenant. |
| Tenant id source | session (`useAuth`) | session (`useAuth`) — never user input |

---

## Architecture

**Admin (`packages/dashboard`):** extend `AgentsPage` into a tabbed shell — keep `AgentsOverview`;
add **Public Agents** (CRUD/version/archive + personas), **Margin** (markup config), **Leaderboard**
(anonymized aggregate). One typed client `agents-admin-client.ts` with **zero** tenant-scoped methods
(isolation asserted by a test). Hooks mirror `useAgentsConfig`.

**Tenant (`packages/dashboard-user`):** new `api/agents.ts` / `agent-trail.ts` / `provider-keys.ts`
over `apiClient`; pages for list/detail/role-selection/provider-keys; the detail page tabs
Overview · Runs · Trail · Benchmarks · Outcomes · Usage. A single `getActiveTenantId()` helper reads
`useAuth().tenantId` — the only tenant-id source (AC 8). Management routes behind `TenantAdminGuard`;
read routes behind `AuthGuard`; in-page write affordances hidden for members.

**Reveal-once BYOK:** parent posts create/rotate → reveal envelope → GET reveal once → mount reveal
modal (mirrors `SecretRevealModal`) → acknowledge/copy/close → drop plaintext. List never re-shows it.

---

## Wave order & dependencies

W1 (admin client + isolation test) and W4 (tenant client + helper) are independent and parallel-safe.
W2/W3 depend on W1; W5/W6/W7 depend on W4. W8 is the cross-cutting e2e + regression gate.

```
W1 ─┬─ W2 ─ W3
    │
W4 ─┴─ W5 ─ W6 ─ W7 ──┐
                      └─ W8
```

---

## Wave 1 — Admin agent client + isolation guarantee (foundation)

**Scope:** Typed admin client for public-agent CRUD/version/archive, persona CRUD, margin config,
and the anonymized leaderboard — with a structural guarantee it can never read a single tenant's
data.

**Files:**
- New: `packages/dashboard/src/services/settings/agents-admin-client.ts` — `fetchJSON` against
  `/api/agents` (public reads/writes), persona endpoints, `/api/admin/agents/leaderboard`
  (anonymized), and margin GET/PUT. **No method takes a `tenantId`; no `/orgs/{tenantId}/...` path.**
- New: `packages/dashboard/src/services/settings/__tests__/agents-admin-client.test.ts`.

**Tests (first):**
- [ ] Each method calls the expected path/verb with the right body (mock `fetch`).
- [ ] **Isolation:** no exported method accepts a `tenantId` arg and no path string contains
      `/orgs/` — assert by reflecting the client surface + scanning the source/paths.
- [ ] `create` with `visibility: "public"` is the only create path; error class surfaces 403 message.

**Acceptance criteria:**
- [ ] Client compiles strict; covers list/create/version/archive/persona/margin/leaderboard.
- [ ] Isolation test proves no tenant-scoped capability exists.

---

## Wave 2 — Admin public-agent + persona + margin UI

**Scope:** The management surface inside `AgentsPage`'s new tab shell.

**Files:**
- Modify: `packages/dashboard/src/pages/settings/AgentsPage.tsx` — add `AdminLayout`-style tab shell
  (`Roles` = existing `AgentsOverview`, `Public Agents`, `Margin`, `Leaderboard`); keep
  `AgentsOverview` untouched.
- New: `components/settings/agents/PublicAgentsPanel.tsx`, `PublicAgentForm.tsx`,
  `PersonaManager.tsx`, `MarginConfigPanel.tsx`.
- New: `hooks/settings/usePublicAgents.ts`, `useMarginConfig.ts` (mirror `useAgentsConfig`).
- New: `components/settings/agents/__tests__/PublicAgentsPanel.test.tsx`.

**Tests (first):**
- [ ] List renders public agents; system-default rows are read-only with a "system default" badge.
- [ ] Create/version/archive call the client; `ConfirmDialog` gates archive.
- [ ] Persona CRUD round-trips; margin panel GET/PUT round-trips.
- [ ] A 403 (defense-in-depth) renders an inline message, no crash.

**Acceptance criteria:**
- [ ] `AgentsOverview` still works (no-regression); story AC 1, AC 2 (margin), AC 15 met.
- [ ] All writes use `visibility: "public"` and the admin client only.

---

## Wave 3 — Admin anonymized leaderboard

**Scope:** Cross-tenant, anonymized public-agent leaderboard — provably not a tenant view.

**Files:**
- New: `components/settings/agents/PublicLeaderboardPanel.tsx`, `LeaderboardChart.tsx` (shared
  window selector + min-sample note; CSS/SVG bars).
- New: `hooks/settings/usePublicLeaderboard.ts` (no `tenantId`).
- New: `components/settings/agents/__tests__/PublicLeaderboardPanel.test.tsx`.

**Tests (first):**
- [ ] Renders aggregate rows (success rate / avg iterations / cost / latency) per public agent.
- [ ] Window selector (7d/30d/90d/all) re-queries; min-sample rows show "not enough samples (n=…)".
- [ ] **Isolation (AC 9):** the panel never issues a `/orgs/{tenantId}/...` request and the bundle
      imports no tenant-trail client; if the aggregate endpoint 404s, an explicit empty state shows
      (no tenant-scoped fallback).

**Acceptance criteria:**
- [ ] Story AC 2, AC 9 met; admin never reads a single tenant's data.

---

## Wave 4 — Tenant agent clients + active-tenant helper (foundation)

**Scope:** Typed tenant clients over `apiClient`, all bound to the session tenant id via one helper.

**Files:**
- New: `packages/dashboard-user/src/api/agents.ts` — list (public ∪ own-private), create/version/
  archive (private), `role-selections/{role}` PUT + `resolve` GET (provenance).
- New: `packages/dashboard-user/src/api/agent-trail.ts` — runs + trail (32-6), benchmarks/
  leaderboards (32-10), outcomes, usage (32-9); **every method takes the tenant id from a single
  `getActiveTenantId()` that reads `useAuth().tenantId`** (no other source).
- New: `packages/dashboard-user/src/api/provider-keys.ts` — BYOK register/rotate/remove + reveal
  envelope handling (32-3).
- New: colocated `*.test.ts` for each.

**Tests (first):**
- [ ] Each method hits the expected `/api/v1/orgs/{tenantId}/...` or `/api/agents/...` path/verb.
- [ ] **Isolation (AC 8):** `agent-trail.ts` exposes no parameter/route/input for a foreign tenant
      id; every path uses the session tenant id; a duplicated/foreign id is unreachable from the API.
- [ ] Reveal-envelope parsing: create/rotate return token/url and **no plaintext** in the body.

**Acceptance criteria:**
- [ ] Clients compile strict; reuse `apiClient` (no new wrapper); story AC 8, AC 12 foundations met.

---

## Wave 5 — Tenant agent list, form, role selection (management)

**Scope:** Private agent CRUD + per-role selection, gated for members.

**Files:**
- New: `pages/agents/AgentsListPage.tsx`, `pages/agents/RoleSelectionsPage.tsx`.
- New: `components/agents/AgentCard.tsx`, `AgentForm.tsx` (create/version + persona section).
- Modify: `App.tsx` (routes: `/agents` + `/agents/roles` under `AuthGuard`, management actions/
  routes under `TenantAdminGuard`), `layouts/AppLayout.tsx` (nav links).
- New: colocated tests.

**Tests (first):**
- [ ] List shows public ∪ own-private; edit/version/archive offered only on private rows; public rows
      are browse + "Select for role" only (no edit) — 403 on a forced public write.
- [ ] Role selection PUT works; current selection + provenance (`system-public`/`tenant-public`/
      `tenant-private`) is shown per role; absent selection shows the system-default agent (never blank).
- [ ] **Member gating (AC 10):** `role === 'member'` hides write controls; management route renders
      the `TenantAdminGuard` "Admin-only" panel; a simulated 403 renders a role message, no crash.

**Acceptance criteria:**
- [ ] Story AC 3, AC 4, AC 10, AC 13 met.

---

## Wave 6 — Tenant agent detail: runs + trail (own data)

**Scope:** Per-agent observability over the tenant's own trail.

**Files:**
- New: `pages/agents/AgentDetailPage.tsx` (tabs: Overview · Runs · Trail · Benchmarks · Outcomes ·
  Usage — Benchmarks/Outcomes/Usage filled in W7).
- New: `components/agents/RunsTable.tsx`, `ActionTrailTable.tsx` (paginated; filters
  `from/to/role/provider/outcome/type`).
- New: colocated tests incl. the tenant-isolation assertion.

**Tests (first):**
- [ ] Runs + trail tables render mocked 32-6 responses; filters re-query; cursor paging has no
      dupes/skips at boundaries.
- [ ] **Isolation (AC 8):** every captured request path contains the session `tenantId`; no UI
      control accepts/switches a foreign tenant id; opening a foreign agent id 404s gracefully.

**Acceptance criteria:**
- [ ] Story AC 6, AC 8 met; detail view shows only the caller's own data.

---

## Wave 7 — Tenant benchmarks, outcomes, usage + BYOK provider keys

**Scope:** The remaining detail tabs + the BYOK reveal-once page.

**Files:**
- New: `components/agents/LeaderboardView.tsx` (agent/provider/prompt/persona dimension control +
  window selector + min-sample note), `OutcomeBreakdown.tsx` (bug counts by `bugType`),
  `UsagePanel.tsx` (tokens/cost).
- New: `pages/settings/ProviderKeysPage.tsx`, `components/agents/ProviderKeyRow.tsx`,
  `ProviderKeyRevealModal.tsx` (mirror `SecretRevealModal`).
- Modify: `App.tsx` (route `/settings/provider-keys` under `TenantAdminGuard`),
  `layouts/AppLayout.tsx` (link).
- New: colocated tests (`LeaderboardView.test.tsx`, `ProviderKeysPage.test.tsx`).

**Tests (first):**
- [ ] Leaderboard renders all four dimensions; window selector re-queries; `n < minSamples` shows
      the muted "not enough samples (n=…)" note instead of a bar (AC 7 correctness path).
- [ ] Outcome breakdown + usage render from mocked 32-9/32-10 responses.
- [ ] **BYOK reveal-once (AC 11):** create/rotate → reveal envelope → reveal GET (invoked **exactly
      once**) → plaintext shown once → "Close" disabled until acknowledged → copy works → on close
      plaintext is gone from state; the list never re-displays the secret (shows provider/source/
      storage-key/last-rotated only); remove works; member → controls hidden + 403 message.

**Acceptance criteria:**
- [ ] Story AC 5, AC 7, AC 11 met; no secret ever rendered after the one-shot reveal.

---

## Wave 8 — Cross-cutting e2e flow + regression gate

**Scope:** Tie the surfaces together and prove the boundaries hold; lock no-regression.

**Steps:**
- [ ] e2e-style test (mocked endpoints, both packages where feasible): admin creates a public agent →
      tenant lists it (browse-only) → tenant selects it for a role (provenance `tenant-public`) →
      tenant registers a BYOK key (reveal-once) → tenant opens the agent detail and sees its own
      trail (empty → populated). Assert admin path issues no tenant-scoped request and tenant path
      issues no aggregate/admin request.
- [ ] Run `pnpm test --filter @tamma/dashboard` and `pnpm test --filter @tamma/dashboard-user` —
      both green; existing `AgentsOverview` + secrets + auth suites unaffected.
- [ ] `pnpm lint` clean for both packages; strict TS compile (no `any`, honor
      `exactOptionalPropertyTypes` / `noUncheckedIndexedAccess` per project memory).

**Acceptance criteria:**
- [ ] Story AC 14, AC 15 met; the admin/tenant and tenant/tenant boundaries are asserted, not assumed.

---

## Risks

- **Boundary leakage is the headline risk.** The whole story is "two principals, never each other's
  data." Mitigations are structural, not policy: the admin client has **no** tenant-scoped method
  (W1 test); the tenant trail client takes its tenant id only from `useAuth` (W4 helper + W6 test);
  the admin leaderboard hits a server-side anonymized aggregate, never a stripped tenant call (W3).
  Get any of these wrong and a single bug exposes cross-tenant performance data.
- **Missing upstream endpoints.** 32-10's admin anonymized aggregate and 32-9 usage reads may lag.
  Contract: render an explicit empty state, **never** fabricate the aggregate from tenant data.
  Confirm the exact endpoint paths against the 32-9/32-10 story files before W3/W7 (those dirs were
  empty at draft time — coordinate or stub the client behind a typed interface).
- **Reveal-once correctness.** A loosely-built reveal modal that re-fetches or re-renders the
  plaintext is a security defect. Reuse the `SecretRevealModal` contract verbatim; the parent owns
  the one-shot GET and burns it exactly once (W7 test asserts the single invocation).
- **Two HTTP conventions.** Admin uses `fetchJSON`; tenant uses `apiClient`. Don't port one into the
  other — match each package's existing files (`settings-api-client.ts` vs `api/dashboard.ts`).
- **Min-sample is correctness, not polish.** A #1 ranking on n=1 is misleading; the muted-note path
  is required and unit-tested (W3/W7).
- **`AgentsPage` regression.** It's extended, not replaced — the tab shell must leave `AgentsOverview`
  byte-for-byte behaviourally intact; W2/W8 guard this.
- **Charting dependency.** Prefer CSS/SVG bars over adding a charting lib unless one already exists
  in the package — check `package.json` before W3.
