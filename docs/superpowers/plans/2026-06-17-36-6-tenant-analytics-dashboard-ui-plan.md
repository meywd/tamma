# Story 36-6: Tenant Analytics Dashboard UI — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan step-by-step. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every step writes tests
> before implementation. Read `docs/guides/BEFORE_YOU_CODE.md` first.

**Goal:** Add a tenant-facing **Analytics** section to `packages/dashboard-user` (`dash.tamma.dev`)
with three read-only views — **Usage**, **Cost (BYOK vs platform)**, and **Agents** — sliced by a
shared TimeRange + GroupBy control bar, wired to the Story 36-3/36-4/36-5 query APIs. Every view is
hard-scoped to the active tenant (no cross-tenant leakage), handles loading/empty/error distinctly
(empty store ≠ error), and carries a flag-gated **Export** seam for Story 36-8.

**Story file:** `docs/stories/epic-36/story-36-6/36-6-tenant-analytics-dashboard-ui.md`

**Spec source:** `/tmp/pab_stories/36-6.json` (P1, est 4-5 days, boundaryNote empty).

**Tech stack (verified in `packages/dashboard-user/package.json`):** React 19, `react-router-dom` 7,
Tailwind 4, Vite 8 (pinned to an es2020/chrome87 browser floor), Vitest 4 + `@testing-library/react`
16 (jsdom). Tests are colocated `*.test.tsx`; run via `pnpm test --filter @tamma/dashboard-user`.
No charting library is installed — default to dependency-free inline SVG.

---

## Non-goals (YAGNI guard)

- **NO backend code.** The analytics queries, RBAC 403, UTC binding, and audit events live in
  `apps/tamma-elsa` (Stories 36-3/36-4/36-5) and are out of scope. `packages/api` is deleted — never
  target it.
- **NO export file generation.** CSV/PDF rendering is server-side (36-8). This story only adds the
  `requestAnalyticsExport` client + a flag-gated button.
- **NO new org-switcher.** The active tenant is resolved through a single `useActiveTenant` hook that
  wraps `useAuth().user.tenantId` today; the Story 18-5 switcher drops in later by editing only that
  hook.
- **NO per-user analytics personalization, saved dashboards, or alerting UI.** Read-only views only.
- **NO new chart dependency by default.** Inline SVG + a11y table. `recharts` is a documented opt-in
  if the team wants it (must respect the vite browser floor).
- **NO admin/owner gate.** Analytics is member-read (spec + CLAUDE.md prompt-store GET-resolved RBAC
  precedent). No `TenantAdminGuard` wrapper.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### What exists in `packages/dashboard-user` (reuse it)

| Seam | File | Reuse |
|---|---|---|
| API client w/ refresh-on-401 + `credentials:'include'` | `src/api/client.ts` (`apiClient`, `ApiError`, `UnauthorizedError`) | All analytics calls go through `apiClient.get/post`. |
| Tenant-scoped API idiom | `src/api/dashboard.ts`, `src/api/alerts.ts` | Path shape `/api/v1/orgs/${tenantId}/...`; `analytics.ts` mirrors it. |
| Auth/session + active tenant | `src/hooks/useAuth.tsx` (`user.tenantId`, `user.role`) | `useActiveTenant` wraps it. |
| Route tree under guard+shell | `src/App.tsx` (`AuthGuard → AppLayout` element route) | Nest `/analytics/*` here. |
| Sidebar nav | `src/layouts/AppLayout.tsx` (Dashboard/Repos/Runs/Settings links) | Add "Analytics" link. |
| No-tenant zero-state + cancel-on-unmount fetch idiom | `src/pages/DashboardHome.tsx` | Hooks copy the early-return-when-no-tenant + cancel pattern (upgraded to AbortController). |
| Tenant-isolation test idiom | `src/pages/alerts/TenantAlertFeed.test.tsx` (lines 36–90) | Cross-tenant non-leakage test copies this `fetchMock.mock.calls[i][0]` URL assertion. |
| API URL-construction test idiom | `src/api/dashboard.test.ts` | `analytics.test.ts` copies it. |
| jsdom matchMedia/ResizeObserver mocks | `src/test/setup.ts` | Already wired via `vitest.config.ts`. |

### What does NOT exist (build it)

- No `/analytics` route, no analytics pages/components, no analytics API client/hooks.
- **No org-switcher context** — `AppLayout` header comment marks it a later Story 18-5 sub-task. We
  isolate resolution in `useActiveTenant` so the switcher lands without touching views.
- No charting library. No `recharts`/`d3`/`victory`/`nivo` in the package.
- The 36-3/36-4/36-5 story files are not yet authored — only their JSON specs exist. **The endpoint
  paths are fixed; the exact DTO field names must be reconciled when those stories land** (Step 0).

### API contracts being rendered (from the dependency specs)

| Story | Endpoint | Notes |
|---|---|---|
| 36-3 | `GET /api/v1/orgs/{tenantId}/analytics/usage` | params `from,to,granularity(hour\|day),groupBy(provider\|agent\|workflow\|repo)`; echoes `period_start/period_end`. |
| 36-3 | `GET /api/v1/orgs/{tenantId}/analytics/usage/breakdown` | top-N rows for one dimension. |
| 36-4 | `GET /api/v1/orgs/{tenantId}/analytics/cost` | `platformBilledUsd` (billable) + `byokCostUsd` (informational), MTD, projection, budget + alert threshold. BYOK never contributes to billed. |
| 36-5 | `GET /api/v1/orgs/{tenantId}/analytics/agents` | per-agent rollups, selectable order metric. |
| 36-5 | `GET /api/v1/orgs/{tenantId}/analytics/agents/{agentId}/trend` | daily success/cost/tokens; zero-activity → empty trend not error. |
| 36-8 | `POST /api/v1/orgs/{tenantId}/analytics/exports` | `{type,format,from,to,groupBy}` → download or async jobId. Flag-gated seam. |

All endpoints are tenant-member read; cross-tenant route id → 403 (server-side). DateTime is UTC.

---

## Architecture

**Three views over a shared control bar, one tenant resolver, typed client → hooks → presentational
components.**

```
AppLayout (sidebar: + Analytics)
  └─ /analytics  → AnalyticsLayout (sub-nav Usage|Cost|Agents + <TimeRangeControls/> + <Outlet/>)
        ├─ usage  → UsageView   → useAnalyticsUsage  → fetchUsageSeries + fetchUsageBreakdown
        ├─ cost   → CostView    → useAnalyticsCost   → fetchCost
        └─ agents → AgentsView  → useAnalyticsAgents → fetchAgentRollups + fetchAgentTrend

useActiveTenant ──(tenantId)──> every hook (effect dep; abort+refetch on change)
useAnalyticsControls ──(range/groupBy/granularity, synced to URL ?range=&groupBy=)──> control bar + hooks
```

**Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md):** the dashboard is a pure
consumer; ownership is enforced server-side. In **SaaS** the active tenant is the caller's org
membership (`user.tenantId`); in **single-user** the API resolves the principal to the sole user and
the same `/orgs/{tenantId}` path is served. The dashboard treats both identically — it always sends
the one tenant id it knows. No UI branch on mode.

**Load-bearing invariant:** the client never builds an analytics URL with any org id other than the
active tenant. Tenant id flows from `useActiveTenant` only — never from a list response, query param,
or drill-down link. AC 6 non-leakage test is the regression guard.

---

## Step breakdown

### S0: Reconcile DTO field names with 36-3/36-4/36-5 (gate)

**Files:** read-only — `docs/stories/epic-36/story-36-3/...`, `story-36-4/...`, `story-36-5/...`
(when authored) or their `/tmp/pab_stories/36-3..5.json` specs.

- [ ] Confirm the exact response field names for usage series/breakdown, cost (billed vs byok, MTD,
      projection, budget/threshold), and agent rollups/trend.
- [ ] If they differ from the story's draft DTOs, update `analytics.ts` types accordingly. Endpoint
      **paths** are fixed; only field names are negotiable.

**Acceptance:** `analytics.ts` types match the dependency contracts (or the deltas are noted in the
story Change Log).

### S1: API client `analytics.ts` + tests (foundation, TDD)

**Files:** new `src/api/analytics.ts`, `src/api/analytics.test.ts`.

- [ ] Write `analytics.test.ts` FIRST (dashboard.test.ts style): each function builds the correct
      `/api/v1/orgs/{tenantId}/analytics/...` URL; `from`/`to` are UTC ISO; omitted optional params
      absent from the query string; `requestAnalyticsExport` POSTs the right body.
- [ ] Implement `analytics.ts` (DTOs + `fetchUsageSeries`, `fetchUsageBreakdown`, `fetchCost`,
      `fetchAgentRollups`, `fetchAgentTrend`, `requestAnalyticsExport`) through `apiClient`.

**Acceptance:** URL-construction tests green; all calls inherit refresh-on-401.

### S2: `useActiveTenant` + `useAnalyticsControls` + tests

**Files:** new `src/hooks/useActiveTenant.tsx`, `src/hooks/useAnalyticsControls.tsx`,
`src/hooks/useAnalyticsControls.test.tsx`.

- [ ] `useActiveTenant` returns `{ tenantId: user?.tenantId ?? null }` with a TODO marking the
      Story 18-5 org-switcher swap point.
- [ ] `useAnalyticsControls` holds `{ rangePreset, from, to, granularity, groupBy }` synced to
      `useSearchParams` (react-router 7); presets (7d/30d/90d/MTD/custom) compute UTC `from`/`to`.
- [ ] Tests: preset ↔ URL query round-trip; preset computes UTC bounds; custom range validates
      `from <= to`.

**Acceptance:** controls hook round-trips to URL; tenant resolver is the single source.

### S3: View hooks `useAnalyticsUsage` / `useAnalyticsCost` / `useAnalyticsAgents`

**Files:** new `src/hooks/useAnalyticsUsage.tsx`, `useAnalyticsCost.tsx`, `useAnalyticsAgents.tsx`.

- [ ] Each hook: depends on `useActiveTenant().tenantId` + control args; `AbortController`
      cancel-on-change; tri-state `{ data, loading, error }`; early-return (no fetch) when
      `tenantId === null`.
- [ ] `useAnalyticsAgents` also exposes a `loadTrend(agentId)` for the drill-down.

**Acceptance:** changing controls/tenant aborts the prior request and refetches; no fetch without a
tenant. (Covered by view tests in S5–S7.)

### S4: Shared presentational components

**Files:** new `src/pages/analytics/components/TimeRangeControls.tsx` (+ test),
`TimeSeriesChart.tsx`, `BreakdownTable.tsx`, `MetricCard.tsx`, `ExportButton.tsx`, `EmptyState.tsx`.

- [ ] `TimeRangeControls` — presentational; presets + custom date inputs + granularity + GroupBy
      selects; calls back into `useAnalyticsControls`. Test: selecting a preset/groupBy invokes the
      callback with the right values.
- [ ] `TimeSeriesChart` — inline SVG line/area + visually-hidden `<table>` (a11y + test target). No
      chart dependency.
- [ ] `BreakdownTable` — top-N dimension rows. `MetricCard` — reuse the `DashboardHome.StatCard`
      idiom. `EmptyState` — shared zero-state vs `role="alert"` error panel (two variants).
- [ ] `ExportButton` — renders only when `import.meta.env.VITE_FEATURE_ANALYTICS_EXPORT === 'true'`;
      on click calls `requestAnalyticsExport` with the active view's params.

**Acceptance:** components render in isolation; `ExportButton` hidden when the flag is unset.

### S5: Usage view + tests

**Files:** new `src/pages/analytics/UsageView.tsx`, `UsageView.test.tsx`.

- [ ] Time-series (workflows/dispatches/tokens) + dimension breakdown driven by the control bar;
      echoes `period_start/period_end`; loading/empty/error states.
- [ ] Tests: selector-driven refetch fires `?groupBy=agent`; empty `points:[]` → zero-state; 500 →
      error panel (distinct from empty).

**Acceptance:** AC 3 + AC 8 (usage) + AC 12a green.

### S6: Cost view + tests

**Files:** new `src/pages/analytics/CostView.tsx`, `CostView.test.tsx`.

- [ ] Platform-billed vs BYOK(informational) series, MTD billed, projected spend, budget marker +
      alert threshold; "BYOK — not billed" label.
- [ ] Tests: fully-BYOK response (`platformBilledUsd:0`, non-zero `byokCostUsd`) renders `$0.00`
      billed + label + no over-budget; budget marker renders when budget present.

**Acceptance:** AC 4 + AC 12b green.

### S7: Agents view + drill-down + tests

**Files:** new `src/pages/analytics/AgentsView.tsx`, `AgentsView.test.tsx`.

- [ ] Per-agent rollup rows with selectable order metric; row click → trend drill-down via
      `fetchAgentTrend`; zero-activity agent → empty trend (not error); unknown agent id →
      placeholder name.
- [ ] Tests: rows render + order metric changes the request; drill-down loads a trend; zero-activity
      trend renders empty-not-error.

**Acceptance:** AC 5 green.

### S8: Layout, routing, nav wiring + isolation/tenant-switch tests

**Files:** new `src/pages/analytics/AnalyticsLayout.tsx` (+ test); modify `src/App.tsx`,
`src/layouts/AppLayout.tsx`.

- [ ] `AnalyticsLayout` — sub-nav (Usage|Cost|Agents) + `<TimeRangeControls/>` + `<Outlet/>`.
- [ ] `App.tsx` — nest `/analytics` (index → redirect to `usage`; `usage`/`cost`/`agents`) under the
      existing `AuthGuard → AppLayout` element route. **No `TenantAdminGuard`.**
- [ ] `AppLayout.tsx` — add `Analytics` sidebar link (`/analytics/usage`).
- [ ] Tests: **cross-tenant non-leakage** (every recorded fetch URL contains only the active
      tenant id — `TenantAlertFeed.test` idiom); **tenant switch** (auth user `tnt-A` → first call
      `/orgs/tnt-A/...`; re-render on `tnt-B` → prior request aborted, new call `/orgs/tnt-B/...`);
      **no-tenant** → zero-state, zero fetches.

**Acceptance:** AC 1, 2, 6, 7 green; nav link visible to any member.

### S9: E2E smoke + final verification

**Files:** new `e2e/tests/analytics.spec.ts` (root Playwright suite, `dashboard.spec.ts` style).

- [ ] Authenticated session loads `/analytics/usage`; Analytics nav link visible; three sub-tabs
      render; switch to `/analytics/cost` shows spend panels. Network-only smoke (no seeded-data
      assertions), targeting `E2E_BASE_URL` (default `app.tamma.dev`).
- [ ] Run `pnpm test --filter @tamma/dashboard-user` (all green), `pnpm --filter
      @tamma/dashboard-user typecheck`, and `pnpm --filter @tamma/dashboard-user build` (respect the
      vite es2020/chrome87 floor — fail if a dependency tightens it).

**Acceptance:** suite green; typecheck + build clean; e2e smoke passes against a deployed env.

---

## Step order & dependencies

```
S0 (gate: lock DTO names)
  → S1 (client) → S2 (controls/tenant hooks) → S3 (view hooks)
  → S4 (components) → S5 / S6 / S7 (views, parallelizable) → S8 (layout+routing+isolation)
  → S9 (e2e + verify)
```

S5/S6/S7 are independent once S1–S4 land and may be parallelized (one subagent each). S8's isolation
+ tenant-switch tests are the gating quality checks; do not mark the story Ready until they pass.

## Risks & mitigations

| Risk | Mitigation |
|---|---|
| 36-3/36-4/36-5 DTO field names not final | S0 gate; story owns `analytics.ts` types and reconciles when those stories land. Endpoint paths are fixed. |
| Cross-tenant leakage via a stray query-param/drill-down id | Tenant id flows from `useActiveTenant` only; AC 6 non-leakage test asserts every fetch URL; drill-down passes `agentId` only, never a tenant. |
| Empty store mistaken for error (red banner on a new tenant) | `EmptyState` two-variant component; explicit empty-vs-error tests (S5). Mirrors `DashboardHome`. |
| Adding a chart lib tightens the browser floor | Default zero-dependency inline SVG; if `recharts` adopted, verify `pnpm build` under `es2020/chrome87` first. |
| Org switcher (18-5) not yet present | `useActiveTenant` isolates resolution; abort+refetch already handles a switch — only the hook body changes later. |
| Export endpoint (36-8) not live | `ExportButton` flag-gated (`VITE_FEATURE_ANALYTICS_EXPORT`, default off); ships dark, no code change to enable. |
| Timezone drift (local vs UTC buckets) | Send `from`/`to` as `.toISOString()` UTC; display localizes only. Matches 36-3 forced-UTC binding. |
```
