# Story 36-6: Tenant Analytics Dashboard UI

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

## User Story

As a **tenant member**,
I want an Analytics section in my dashboard with usage, cost, and agent-performance views that I can slice by time range and dimension,
So that I can see how my organization is consuming Tamma, what it is costing me (platform-billed vs my own BYOK tokens), and which agents perform best — all without ever seeing another tenant's data.

## Priority

P1 - Surfaces the Epic 36 analytics APIs (36-3/36-4/36-5) to the tenant; the read-only consumer that turns the dimensional projection pipeline into a product.

## Acceptance Criteria

1. New `/analytics` route tree is added to `packages/dashboard-user` under `AuthGuard → AppLayout`, with three sub-views — `/analytics/usage`, `/analytics/cost`, `/analytics/agents` — reachable from a new "Analytics" entry in the `AppLayout` sidebar nav for **any** authenticated tenant member (no admin/owner gate). `/analytics` redirects to `/analytics/usage`.
2. A shared **TimeRange + GroupBy control bar** (presets: 7d / 30d / 90d / MTD / custom; granularity hour|day; GroupBy: provider | agent | workflow | repo) lives in one reusable component and drives all three views; changing a control re-queries the active view (no full-page reload) and is reflected in the URL query string so views are linkable/refreshable.
3. **Usage view** renders a time-series (workflows, dispatches, tokens) plus a dimension breakdown table/bar driven by the shared control bar, wired to `GET /api/v1/orgs/{tenantId}/analytics/usage` (time-series) and `GET /api/v1/orgs/{tenantId}/analytics/usage/breakdown` (top-N by the selected dimension). The view echoes the API's `period_start`/`period_end`.
4. **Cost view** wired to `GET /api/v1/orgs/{tenantId}/analytics/cost` shows a spend time-series split into `platformBilledUsd` (billable) and `byokCostUsd` (informational, labelled "BYOK — not billed"), plus month-to-date `platformBilledUsd`, projected end-of-month spend, and a budget marker (budget + alert threshold from the response). A fully-BYOK tenant renders `platformBilledUsd = $0.00` with a non-zero informational BYOK figure and no false "over budget" state.
5. **Agents view** wired to `GET /api/v1/orgs/{tenantId}/analytics/agents` lists per-agent rows (name, runs, success rate, avg duration, tokens/run, cost/run, platform-billed) with a selectable order metric, plus a drill-down that calls `GET /api/v1/orgs/{tenantId}/analytics/agents/{agentId}/trend` to render that agent's daily success-rate / cost / tokens trend. Agents with zero activity render an empty trend (not an error); an unknown/deleted agent id renders a placeholder name.
6. **Tenant isolation (hard):** every view resolves `tenantId` from the active org context (org switcher) and **only** ever issues requests to `/api/v1/orgs/{activeTenantId}/...`. A test asserts that no analytics call is ever made to any tenant id other than the active one (no cross-tenant leakage), mirroring the `TenantAlertFeed` isolation test.
7. **Active-tenant resolution + switch:** views read the active tenant from the org context (today `useAuth().user.tenantId`; future org switcher from Story 18-5). When the active tenant changes, all in-flight requests are cancelled and the view re-queries against the new tenant. When the user has no active tenant, the section shows the "No organization" zero-state and issues **zero** API calls.
8. **Loading / empty / error states** are handled per view and per panel: a skeleton/`Loading…` while fetching; a distinct **zero-state** ("No analytics data yet for this period") when the store returns empty rows — this is NOT treated as an error; and an inline `role="alert"` error panel only on an actual request failure (non-2xx / network). An empty store therefore never surfaces a red error.
9. API client modules are added under `packages/dashboard-user/src/api` (`analytics.ts`) returning typed DTOs that mirror the 36-3/36-4/36-5 response shapes, going through the existing `apiClient` so the refresh-on-401 dance and `credentials: 'include'` are inherited. Date-range params are sent as UTC ISO 8601 strings to match the API's UTC binding.
10. Data-fetching hooks are added under `packages/dashboard-user/src/hooks` (`useAnalyticsUsage`, `useAnalyticsCost`, `useAnalyticsAgents`, `useAnalyticsControls`) that encapsulate fetch + abort-on-change + loading/empty/error state, keyed on `(tenantId, range, groupBy, granularity)`.
11. An **export hook seam** is wired into each view's header: an "Export" affordance that posts `{ type, format, from, to, groupBy }` to `POST /api/v1/orgs/{tenantId}/analytics/exports` (Story 36-8). The client function is added now and the button is rendered, gated behind a `VITE_FEATURE_ANALYTICS_EXPORT` flag (default off) so the UI ships before 36-8 lands; when off the affordance is hidden. No PDF/CSV rendering logic lives in the dashboard — that is server-side in 36-8.
12. Components are colocated with Vitest tests; tests cover: (a) selector-driven refetch (changing range/groupBy fires a new request with the right query params), (b) BYOK vs platform cost rendering (fully-BYOK shows $0 billed), (c) empty-store zero-state vs error-state distinction, (d) tenant-switch cancels + refetches against the new tenant, and (e) the cross-tenant non-leakage assertion from AC 6. The `analytics.ts` API client has URL-construction tests in the `dashboard.test.ts` style. An e2e smoke spec is added to the root `e2e/` Playwright suite that loads `/analytics/usage` and asserts the section renders for an authenticated tenant member.

## Technical Design

### Target package

This is a **TypeScript / React** story in `packages/dashboard-user` (the tenant-facing SPA, `dash.tamma.dev`). It renders the C# analytics APIs from Stories 36-3/36-4/36-5 — those endpoints live in `apps/tamma-elsa` and are **out of scope** here. There is no `packages/api` involvement (that package is deleted; never target it).

Stack already in the package (verified): React 19, `react-router-dom` 7, Tailwind 4, Vitest 4 + `@testing-library/react` 16 (jsdom). No charting library is installed today.

### Page / component structure (NEW)

```
packages/dashboard-user/src/
  pages/analytics/
    AnalyticsLayout.tsx            # NEW — sub-nav (Usage|Cost|Agents) + shared control bar + <Outlet/>
    AnalyticsLayout.test.tsx       # NEW
    UsageView.tsx                  # NEW — time-series + dimension breakdown
    UsageView.test.tsx             # NEW
    CostView.tsx                   # NEW — platform vs BYOK spend, MTD, projection, budget marker
    CostView.test.tsx              # NEW
    AgentsView.tsx                 # NEW — per-agent rows + per-agent trend drill-down
    AgentsView.test.tsx            # NEW
    components/
      TimeRangeControls.tsx        # NEW — presets + custom range + granularity + GroupBy selectors
      TimeRangeControls.test.tsx   # NEW
      TimeSeriesChart.tsx          # NEW — lightweight inline SVG/CSS series (no chart dep); a11y table fallback
      BreakdownTable.tsx           # NEW — top-N dimension rows
      MetricCard.tsx               # NEW — reuse DashboardHome StatCard idiom
      ExportButton.tsx             # NEW — 36-8 seam, flag-gated
      EmptyState.tsx               # NEW — shared zero-state vs error panel
  api/
    analytics.ts                   # NEW — typed client for usage/cost/agents/exports
    analytics.test.ts              # NEW — URL-construction + UTC param tests (dashboard.test.ts style)
  hooks/
    useAnalyticsControls.tsx       # NEW — range/groupBy/granularity state synced to URL query
    useAnalyticsControls.test.tsx  # NEW
    useAnalyticsUsage.tsx          # NEW
    useAnalyticsCost.tsx           # NEW
    useAnalyticsAgents.tsx         # NEW
    useActiveTenant.tsx            # NEW — single source for the active tenant id (wraps useAuth today)
```

Routing change in `App.tsx` (nested under the existing `AuthGuard → AppLayout` element route):

```tsx
<Route path="/analytics" element={<AnalyticsLayout />}>
  <Route index element={<Navigate to="usage" replace />} />
  <Route path="usage" element={<UsageView />} />
  <Route path="cost" element={<CostView />} />
  <Route path="agents" element={<AgentsView />} />
</Route>
```

Nav change in `AppLayout.tsx`: add an `Analytics` `<Link to="/analytics/usage">` to the sidebar. No `TenantAdminGuard` wrapper — analytics is member-read (AC 1).

### Active-tenant resolution

There is no dedicated org-switcher context in `dashboard-user` yet (the `AppLayout` header comment marks it as a later Story 18-5 sub-task). To stay forward-compatible, isolate the resolution behind one hook:

```tsx
// useActiveTenant.tsx
export function useActiveTenant(): { tenantId: string | null } {
  const { user } = useAuth();
  // TODAY: the active tenant is the one on the session (/api/auth/me).
  // FUTURE (Story 18-5 org switcher): swap this for the switcher's selected org.
  return { tenantId: user?.tenantId ?? null };
}
```

Every analytics hook depends on `tenantId` from this hook and lists it in its effect deps, so a tenant switch re-runs the effect (which aborts the prior `AbortController` and refetches). When `tenantId` is `null`, hooks early-return without fetching (mirrors `DashboardHome`).

### API client (`analytics.ts`)

DTOs mirror the 36-3/36-4/36-5 response contracts. All paths are `/api/v1/orgs/${tenantId}/analytics/...`; date params are UTC ISO 8601 to match the server's forced-UTC `DateTime` binding (36-3 AC).

```ts
import { apiClient } from './client';

export type Granularity = 'hour' | 'day';
export type GroupBy = 'provider' | 'agent' | 'workflow' | 'repo';

export interface UsagePoint { bucket: string; workflows: number; dispatches: number; tokens: number; }
export interface UsageSeriesResponse {
  tenantId: string;
  periodStart: string;   // echoes server period_start
  periodEnd: string;
  granularity: Granularity;
  points: UsagePoint[];
}
export interface UsageBreakdownRow { key: string; label: string; workflows: number; dispatches: number; tokens: number; }
export interface UsageBreakdownResponse { tenantId: string; dimension: GroupBy; rows: UsageBreakdownRow[]; }

export interface CostPoint { bucket: string; platformBilledUsd: number; byokCostUsd: number; }
export interface CostResponse {
  tenantId: string;
  periodStart: string;
  periodEnd: string;
  points: CostPoint[];
  monthToDatePlatformBilledUsd: number;
  projectedMonthPlatformBilledUsd: number;
  budgetUsd: number | null;
  alertThresholdPct: number | null;
}

export interface AgentRollupRow {
  agentId: string; name: string; runs: number; successRate: number;
  avgDurationMs: number; tokensPerRun: number; costUsdPerRun: number; platformBilledUsd: number;
}
export interface AgentRollupResponse { tenantId: string; rows: AgentRollupRow[]; }
export interface AgentTrendPoint { day: string; successRate: number; costUsd: number; tokens: number; }
export interface AgentTrendResponse { tenantId: string; agentId: string; name: string; points: AgentTrendPoint[]; }

export interface RangeParams { from: string; to: string; granularity?: Granularity; groupBy?: GroupBy; }

function qs(params: Record<string, string | undefined>): string {
  const sp = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined) sp.set(k, v);
  const s = sp.toString();
  return s ? `?${s}` : '';
}

export async function fetchUsageSeries(tenantId: string, p: RangeParams): Promise<UsageSeriesResponse> {
  return apiClient.get(`/api/v1/orgs/${tenantId}/analytics/usage${qs({ from: p.from, to: p.to, granularity: p.granularity, groupBy: p.groupBy })}`);
}
export async function fetchUsageBreakdown(tenantId: string, p: RangeParams & { groupBy: GroupBy }): Promise<UsageBreakdownResponse> {
  return apiClient.get(`/api/v1/orgs/${tenantId}/analytics/usage/breakdown${qs({ from: p.from, to: p.to, groupBy: p.groupBy })}`);
}
export async function fetchCost(tenantId: string, p: RangeParams): Promise<CostResponse> {
  return apiClient.get(`/api/v1/orgs/${tenantId}/analytics/cost${qs({ from: p.from, to: p.to, groupBy: p.groupBy })}`);
}
export async function fetchAgentRollups(tenantId: string, p: RangeParams & { orderBy?: string }): Promise<AgentRollupResponse> {
  return apiClient.get(`/api/v1/orgs/${tenantId}/analytics/agents${qs({ from: p.from, to: p.to, orderBy: p.orderBy })}`);
}
export async function fetchAgentTrend(tenantId: string, agentId: string, p: RangeParams): Promise<AgentTrendResponse> {
  return apiClient.get(`/api/v1/orgs/${tenantId}/analytics/agents/${agentId}/trend${qs({ from: p.from, to: p.to })}`);
}

// Story 36-8 seam — present now, gated by VITE_FEATURE_ANALYTICS_EXPORT.
export type ExportType = 'usage' | 'cost' | 'agents';
export type ExportFormat = 'csv' | 'pdf';
export interface ExportRequest { type: ExportType; format: ExportFormat; from: string; to: string; groupBy?: GroupBy; }
export async function requestAnalyticsExport(tenantId: string, body: ExportRequest): Promise<{ jobId?: string; downloadUrl?: string }> {
  return apiClient.post(`/api/v1/orgs/${tenantId}/analytics/exports`, body);
}
```

> The exact field names above must be reconciled against the final 36-3/36-4/36-5 DTOs when those stories land; this story owns the client and may adjust property names to match. The endpoint **paths** are fixed by the dependency specs.

### Data-fetching hook pattern

Each view-hook follows the `DashboardHome` cancel-on-unmount idiom, upgraded to `AbortController` + an empty/loading/error tri-state:

```tsx
export function useAnalyticsUsage(args: { from: string; to: string; granularity: Granularity; groupBy: GroupBy }) {
  const { tenantId } = useActiveTenant();
  const [state, setState] = useState<{ data: UsageSeriesResponse | null; loading: boolean; error: string | null }>(
    { data: null, loading: false, error: null },
  );
  useEffect(() => {
    if (!tenantId) { setState({ data: null, loading: false, error: null }); return; }
    const ctrl = new AbortController();
    setState((s) => ({ ...s, loading: true, error: null }));
    (async () => {
      try {
        const data = await fetchUsageSeries(tenantId, args);
        if (!ctrl.signal.aborted) setState({ data, loading: false, error: null });
      } catch (err) {
        if (ctrl.signal.aborted) return;
        setState({ data: null, loading: false, error: err instanceof Error ? err.message : 'Failed to load usage' });
      }
    })();
    return () => ctrl.abort();
  }, [tenantId, args.from, args.to, args.granularity, args.groupBy]);
  return state;
}
```

Empty is derived in the view (`data && points.length === 0`) and rendered as the zero-state, never as an error (AC 8).

### Controls + URL sync

`useAnalyticsControls` holds `{ rangePreset, from, to, granularity, groupBy }` and reads/writes them via `useSearchParams` (react-router 7) so a `/analytics/usage?range=30d&groupBy=agent` URL is shareable and survives refresh. Presets compute `from`/`to` as UTC ISO at read time; custom range uses two date inputs. `TimeRangeControls` is presentational and calls back into this hook.

### Charts without a new dependency (default)

To avoid adding a chart library to the SPA's bundle floor, `TimeSeriesChart` renders a small inline SVG (line/area) plus a visually-hidden `<table>` for accessibility and for test assertions (tests assert on the table rows, not pixels). If the team prefers a library, `recharts` is the suggested option (tree-shakeable, React 19 compatible) — that is a one-line `package.json` add and a swap of `TimeSeriesChart`'s internals; the rest of the story is unaffected. Default: zero-dependency inline SVG.

### Export seam (Story 36-8)

`ExportButton` renders only when `import.meta.env.VITE_FEATURE_ANALYTICS_EXPORT === 'true'`. On click it calls `requestAnalyticsExport(tenantId, { type, format, from, to, groupBy })` with the active view's params. For an async `jobId` response it shows a "preparing export…" toast; for a `downloadUrl` it triggers the download. All file generation is server-side (36-8) — the dashboard only initiates and links.

## Dependencies

- **Prerequisite (API contracts):** Story 36-3 (`GET /analytics/usage[/breakdown]`), Story 36-4 (`GET /analytics/cost`), Story 36-5 (`GET /analytics/agents[/{id}/trend]`). These define the response DTOs this UI renders; the client types must be reconciled to the final shapes.
- **Prerequisite (shell):** Epic 18 Story 18-5 — dashboard shell, `AuthGuard`, `AppLayout`, org context. Active-tenant resolution is isolated in `useActiveTenant` so the future org switcher drops in without touching the views.
- **Prerequisite (pages):** Epic 21 Story 21-4 — user dashboard pages (this section sits alongside them).
- **Forward seam:** Story 36-8 (Analytics Exports) — `requestAnalyticsExport` + `ExportButton` are added now behind a feature flag; 36-8 provides the server endpoint and turns the flag on.
- **Related:** existing `packages/dashboard-user` `apiClient` (refresh-on-401), `useAuth`, `DashboardHome` (empty-state idiom), `TenantAlertFeed` (tenant-isolation test idiom), root `e2e/` Playwright suite.

## Testing Strategy

1. **Component tests (Vitest + Testing Library, jsdom)** — colocated `*.test.tsx`. Mock `globalThis.fetch` (the `TenantAlertFeed.test` / `dashboard.test` idiom). Render each view inside `<MemoryRouter><AuthProvider>…`.
   - **Selector-driven refetch:** mount UsageView, change GroupBy to `agent`, assert a new fetch fires with `?groupBy=agent` and the breakdown table updates.
   - **Cost BYOK vs platform:** feed a fully-BYOK response (`platformBilledUsd:0`, non-zero `byokCostUsd`); assert `$0.00` billed, the "BYOK — not billed" label, and no over-budget state.
   - **Empty vs error:** an empty `points: []` response renders the zero-state ("No analytics data yet…"); a 500 renders the `role="alert"` error panel. Assert they are distinct and an empty store is NOT an error.
   - **Tenant switch:** start with `tenantId: tnt-A`, assert the first call hits `/orgs/tnt-A/...`; re-render with the auth user on `tnt-B`, assert the prior request is aborted and a new call hits `/orgs/tnt-B/...`.
   - **Cross-tenant non-leakage (AC 6):** inspect every `fetch` URL recorded during a render and assert none contains an org id other than the active tenant (mirrors `TenantAlertFeed` test line 86–89).
2. **API client tests (`analytics.test.ts`)** — `dashboard.test.ts` style: assert each function builds the correct `/api/v1/orgs/{tenantId}/analytics/...` URL, that range params are UTC ISO, and that omitted optional params are absent from the query string.
3. **Controls/hook tests** — `useAnalyticsControls` round-trips preset ↔ URL query; presets compute UTC `from`/`to`; custom range validates `from <= to`.
4. **E2E smoke (root `e2e/` Playwright)** — add `e2e/tests/analytics.spec.ts`: authenticated session loads `/analytics/usage`, the Analytics nav link is visible, the three sub-tabs render, and switching to `/analytics/cost` shows the spend panels. Network-only smoke (no seeded-data assertions) to match the existing `dashboard.spec.ts` posture.
5. **No backend/integration tests here** — the analytics query correctness, RBAC 403, and UTC binding are owned and tested by 36-3/36-4/36-5.

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `packages/dashboard-user/src/pages/analytics/AnalyticsLayout.tsx` | Create |
| `packages/dashboard-user/src/pages/analytics/AnalyticsLayout.test.tsx` | Create |
| `packages/dashboard-user/src/pages/analytics/UsageView.tsx` | Create |
| `packages/dashboard-user/src/pages/analytics/UsageView.test.tsx` | Create |
| `packages/dashboard-user/src/pages/analytics/CostView.tsx` | Create |
| `packages/dashboard-user/src/pages/analytics/CostView.test.tsx` | Create |
| `packages/dashboard-user/src/pages/analytics/AgentsView.tsx` | Create |
| `packages/dashboard-user/src/pages/analytics/AgentsView.test.tsx` | Create |
| `packages/dashboard-user/src/pages/analytics/components/TimeRangeControls.tsx` | Create |
| `packages/dashboard-user/src/pages/analytics/components/TimeRangeControls.test.tsx` | Create |
| `packages/dashboard-user/src/pages/analytics/components/TimeSeriesChart.tsx` | Create |
| `packages/dashboard-user/src/pages/analytics/components/BreakdownTable.tsx` | Create |
| `packages/dashboard-user/src/pages/analytics/components/MetricCard.tsx` | Create |
| `packages/dashboard-user/src/pages/analytics/components/ExportButton.tsx` | Create |
| `packages/dashboard-user/src/pages/analytics/components/EmptyState.tsx` | Create |
| `packages/dashboard-user/src/api/analytics.ts` | Create |
| `packages/dashboard-user/src/api/analytics.test.ts` | Create |
| `packages/dashboard-user/src/hooks/useActiveTenant.tsx` | Create |
| `packages/dashboard-user/src/hooks/useAnalyticsControls.tsx` | Create |
| `packages/dashboard-user/src/hooks/useAnalyticsControls.test.tsx` | Create |
| `packages/dashboard-user/src/hooks/useAnalyticsUsage.tsx` | Create |
| `packages/dashboard-user/src/hooks/useAnalyticsCost.tsx` | Create |
| `packages/dashboard-user/src/hooks/useAnalyticsAgents.tsx` | Create |
| `packages/dashboard-user/src/App.tsx` | Modify (register `/analytics` route tree) |
| `packages/dashboard-user/src/layouts/AppLayout.tsx` | Modify (add Analytics nav link) |
| `e2e/tests/analytics.spec.ts` | Create (Playwright smoke) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes/bugs/findings/decisions (dashboard, analytics, tenant-isolation).
3. Read the **final** 36-3/36-4/36-5 story files to lock the exact response DTO field names before writing `analytics.ts` (this story's types are the consumer of those contracts).
4. Reviewed the existing `dashboard-user` idioms: `apiClient` (refresh-on-401), `useAuth`, `DashboardHome` (no-tenant zero-state), `TenantAlertFeed.test.tsx` (tenant-isolation assertion).
5. Planned a TDD approach (Red-Green-Refactor) — write the API-client URL tests and the cross-tenant non-leakage test first.

### Tenant isolation is the load-bearing invariant

The dashboard must never construct an analytics URL with any org id other than the active tenant. The server (36-3) is authoritative and 403s a mismatched route tenant, but the client must not even attempt it. Keep tenant resolution in the single `useActiveTenant` hook; never pass a tenant id down from a query param, a list response, or a drill-down link. The non-leakage test (AC 6) is the regression guard.

### Active tenant: today vs the org switcher

Today the session's `user.tenantId` (from `/api/auth/me`) is the active tenant — there is exactly one membership exposed. The org switcher (Story 18-5 follow-up) will let a multi-org user pick. Because all views depend on `useActiveTenant`, swapping its body for the switcher's selection is the **only** change needed; the abort-and-refetch on tenant change already covers the switch behaviour (AC 7).

### Empty store is not an error

A brand-new tenant (or a quiet period) returns 200 with empty arrays. Treat `data && rows/points empty` as the zero-state. Only non-2xx / network failures are errors. This mirrors `DashboardHome` ("No runs yet.") and is explicitly tested (AC 8, AC 12c) so a future API change that returns 204/empty doesn't trip a red banner.

### UTC everywhere

Send `from`/`to` as UTC ISO 8601 (`new Date(...).toISOString()`); the API forces UTC binding (36-3 AC) so windows must align with stored UTC buckets. Preset math (7d/30d/90d/MTD) computes against `Date.now()` then `.toISOString()`. Display can localize via `toLocaleString()` (as `DashboardHome` does for run timestamps), but the wire format is always UTC.

### Export ships dark

`ExportButton` + `requestAnalyticsExport` land in this story behind `VITE_FEATURE_ANALYTICS_EXPORT` (default unset → hidden) so the analytics section ships before 36-8. When 36-8 lands, flip the flag; no dashboard code change needed beyond enabling it. The dashboard never generates CSV/PDF — it only POSTs the request and links the result.

### Charting choice

Default to the dependency-free inline `TimeSeriesChart` (SVG + a11y table). The `vite.config.ts` deliberately pins an older browser baseline (`es2020`, chrome87…) — any chart lib added must respect that floor. If `recharts` is adopted, confirm it builds under that target before committing the dependency.

## Logging Requirements

This is a browser SPA — there is no Pino logger. Observability conventions for this story:

- **Console (dev only):** surface fetch failures via the inline error panel; do not `console.log` response bodies (may contain tenant cost figures).
- **No sensitive data in the URL beyond the tenant id** the user already owns — never put another tenant's id, tokens, or cost figures in query strings or `localStorage`.
- **Audit trail is server-side:** the analytics-read audit and the `ANALYTICS.EXPORT.REQUESTED` events are emitted by 36-3/36-4/36-5/36-8 on the API; the dashboard emits nothing to the DCB store.
- **Error panels** must show a user-safe message (`error.message` from `ApiError`), never raw stack traces or other tenants' data.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
