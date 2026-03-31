---
title: "Story 23-12: Dashboard Navigation & Layout"
sidebar:
  order: 230
---

Status: planned

## Summary

Add a "Monitoring" navigation section to the dashboard sidebar and create the shared layout components that all monitoring screens use: page shell, tab navigation, time range selector, auto-refresh toggle, and status indicator primitives.

## Acceptance Criteria

1. The Sidebar component (`packages/dashboard/src/components/layout/Sidebar.tsx`) gains a new "Monitoring" nav group with entries:
   - System Health (`/monitoring/health`)
   - Agent Monitor (`/monitoring/agents`)
   - Event Explorer (`/monitoring/events`)
   - Workflows (`/monitoring/workflows`)
   - Providers (`/monitoring/providers`)
   - Logs (`/monitoring/logs`)
   - Infrastructure (`/monitoring/infrastructure`)
   - Knowledge Base (`/monitoring/knowledge-base`)
   - Config Audit (`/monitoring/config`)
   - Security Audit (`/monitoring/security`)
2. The monitoring section appears between "Settings" and "Administration" for admin/owner users only.
3. A `MonitoringLayout` component at `packages/dashboard/src/components/monitoring/MonitoringLayout.tsx`:
   - Renders a page header with title, description, last-updated timestamp, and refresh button
   - Includes an auto-refresh toggle (off/5s/10s/30s/60s) stored in localStorage
   - Includes a global time range selector (Last 1h / 6h / 24h / 7d / 30d / Custom)
   - Shows a connection status indicator (connected/disconnected/reconnecting for SSE streams)
4. Shared monitoring primitives in `packages/dashboard/src/components/monitoring/`:
   - `StatusBadge` -- colored badge (green/yellow/red/gray) with label
   - `MetricCard` -- card showing a single metric with label, value, unit, trend arrow (up/down/flat), and sparkline
   - `MetricGrid` -- responsive grid of MetricCards (1-4 columns based on viewport)
   - `TimeSeriesChart` -- lightweight SVG line/area chart with hover tooltips (no external charting library)
   - `DataTable` -- sortable, filterable table with pagination and column visibility toggle
   - `EmptyState` -- consistent empty state with icon, title, description, and optional action button
   - `ErrorBanner` -- dismissible error banner with retry button
   - `ProgressRing` -- circular progress indicator for percentage metrics
   - `LatencyBar` -- horizontal bar showing p50/p95/p99 latency breakdown
5. A `useMonitoringSSE` hook at `packages/dashboard/src/hooks/monitoring/useMonitoringSSE.ts`:
   - Connects to an SSE endpoint
   - Handles reconnection with exponential backoff (1s, 2s, 4s, max 30s)
   - Exposes `{ data, connected, error, reconnectAttempt }`
   - Cleans up on unmount
6. A `useAutoRefresh` hook at `packages/dashboard/src/hooks/monitoring/useAutoRefresh.ts`:
   - Calls a fetch function at the configured interval
   - Pauses when the browser tab is not visible (using `document.visibilityState`)
   - Exposes `{ loading, error, lastUpdated, refresh, interval, setInterval }`
7. A `useTimeRange` hook at `packages/dashboard/src/hooks/monitoring/useTimeRange.ts`:
   - Manages the selected time range
   - Converts presets to `{ start: Date; end: Date }` for API calls
   - Persists selection in URL query params
8. React Router routes are added for all monitoring pages (lazy-loaded via `React.lazy`).
9. All components use Tailwind CSS classes consistent with the existing dashboard styling.
10. Unit tests for all hooks and component rendering.

## API Endpoints Needed

None (this story is frontend-only layout and navigation).

## Dashboard Components

- `MonitoringLayout` -- page shell with header, time range, auto-refresh
- `StatusBadge` -- health status indicator
- `MetricCard` -- single metric display
- `MetricGrid` -- responsive card grid
- `TimeSeriesChart` -- SVG time series
- `DataTable` -- sortable/filterable table
- `EmptyState` -- empty state
- `ErrorBanner` -- error display
- `ProgressRing` -- circular percentage
- `LatencyBar` -- latency percentile bar

## Data Sources

- localStorage for auto-refresh interval and time range preferences
- URL query params for time range
- SSE endpoints for real-time connection status

## Files to Create

- `packages/dashboard/src/components/monitoring/MonitoringLayout.tsx`
- `packages/dashboard/src/components/monitoring/StatusBadge.tsx`
- `packages/dashboard/src/components/monitoring/MetricCard.tsx`
- `packages/dashboard/src/components/monitoring/MetricGrid.tsx`
- `packages/dashboard/src/components/monitoring/TimeSeriesChart.tsx`
- `packages/dashboard/src/components/monitoring/DataTable.tsx`
- `packages/dashboard/src/components/monitoring/EmptyState.tsx`
- `packages/dashboard/src/components/monitoring/ErrorBanner.tsx`
- `packages/dashboard/src/components/monitoring/ProgressRing.tsx`
- `packages/dashboard/src/components/monitoring/LatencyBar.tsx`
- `packages/dashboard/src/hooks/monitoring/useMonitoringSSE.ts`
- `packages/dashboard/src/hooks/monitoring/useAutoRefresh.ts`
- `packages/dashboard/src/hooks/monitoring/useTimeRange.ts`
- `packages/dashboard/src/pages/monitoring/index.tsx` (route definitions)

## Files to Modify

- `packages/dashboard/src/components/layout/Sidebar.tsx` -- add Monitoring nav group
- `packages/dashboard/src/App.tsx` (or equivalent router config) -- add monitoring routes
