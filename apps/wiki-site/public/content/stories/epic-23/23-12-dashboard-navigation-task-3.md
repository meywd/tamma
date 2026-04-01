---
title: "Task 3: Shared Monitoring UI Primitives"
sidebar:
  order: 230
---

**Story:** 23-12-dashboard-navigation
**Epic:** 23

## Task Description

Create the shared monitoring UI primitive components that all monitoring screens reuse: StatusBadge, MetricCard, MetricGrid, TimeSeriesChart, DataTable, EmptyState, ErrorBanner, ProgressRing, and LatencyBar. All components use Tailwind CSS and require no external charting libraries.

## Acceptance Criteria

- All 9 primitive components created in `packages/dashboard/src/components/monitoring/`
- `StatusBadge` renders colored badge (green/yellow/red/gray) with label
- `MetricCard` shows label, value, unit, trend arrow, and sparkline
- `MetricGrid` renders responsive 1-4 column grid of MetricCards
- `TimeSeriesChart` renders SVG line/area chart with hover tooltips
- `DataTable` supports sorting, filtering, pagination, and column visibility
- `EmptyState` shows icon, title, description, and optional action button
- `ErrorBanner` is dismissible with retry button
- `ProgressRing` renders circular SVG progress indicator
- `LatencyBar` shows p50/p95/p99 latency as horizontal segmented bar
- All components use Tailwind CSS consistent with existing dashboard

## Implementation Details

### Technical Requirements

- [ ] Create `packages/dashboard/src/components/monitoring/StatusBadge.tsx`:
  ```typescript
  export interface StatusBadgeProps {
    status: 'healthy' | 'degraded' | 'unhealthy' | 'unknown';
    label?: string;
    pulse?: boolean;  // pulsing animation for unhealthy
  }
  ```
  - Color map: healthy=green-500, degraded=yellow-500, unhealthy=red-500, unknown=gray-400
  - Renders a small circle + label text
  - When `pulse` is true and status is unhealthy, adds `animate-pulse` class

- [ ] Create `packages/dashboard/src/components/monitoring/MetricCard.tsx`:
  ```typescript
  export interface MetricCardProps {
    label: string;
    value: string | number;
    unit?: string;
    trend?: 'up' | 'down' | 'flat';
    trendColor?: 'green' | 'red' | 'gray';  // up can be good or bad
    sparklineData?: number[];      // last N values for inline sparkline
    onClick?: () => void;
  }
  ```
  - Renders a card with: label (top, gray text), value (large bold), unit (small text beside value)
  - Trend arrow: up-arrow, down-arrow, or horizontal line
  - Sparkline: tiny SVG (80x24px) rendering data as a line chart

- [ ] Create `packages/dashboard/src/components/monitoring/MetricGrid.tsx`:
  ```typescript
  export interface MetricGridProps {
    children: React.ReactNode;
    columns?: 1 | 2 | 3 | 4;  // default: responsive
  }
  ```
  - Uses Tailwind grid: `grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4`
  - If `columns` is set, uses fixed column count

- [ ] Create `packages/dashboard/src/components/monitoring/TimeSeriesChart.tsx`:
  ```typescript
  export interface TimeSeriesDataPoint {
    timestamp: number;  // epoch ms
    value: number;
    label?: string;
  }

  export interface TimeSeriesLine {
    data: TimeSeriesDataPoint[];
    color: string;     // Tailwind color class or hex
    label: string;
    fill?: boolean;    // area chart (fill below line)
  }

  export interface TimeSeriesChartProps {
    lines: TimeSeriesLine[];
    height?: number;           // default 200
    yAxisLabel?: string;
    xAxisFormat?: 'time' | 'date' | 'datetime';
    thresholdLine?: { value: number; color: string; label: string };
  }
  ```
  - Pure SVG implementation, no external library
  - Auto-scales Y axis based on data min/max
  - X axis shows time labels (formatted per `xAxisFormat`)
  - Hover tooltip: shows timestamp, value, and line label at cursor position
  - Uses `<path>` for lines and `<polygon>` for filled areas
  - Optional horizontal threshold line with dashed style

- [ ] Create `packages/dashboard/src/components/monitoring/DataTable.tsx`:
  ```typescript
  export interface DataTableColumn<T> {
    key: string;
    header: string;
    sortable?: boolean;
    filterable?: boolean;
    render?: (row: T) => React.ReactNode;
    width?: string;
    hidden?: boolean;
  }

  export interface DataTableProps<T> {
    columns: DataTableColumn<T>[];
    data: T[];
    pageSize?: number;           // default 25
    pageSizeOptions?: number[];  // default [25, 50, 100]
    searchable?: boolean;        // global search bar
    onRowClick?: (row: T) => void;
    emptyMessage?: string;
    loading?: boolean;
    sortBy?: string;
    sortDirection?: 'asc' | 'desc';
  }
  ```
  - Renders a Tailwind-styled table with striped rows
  - Sortable columns show sort indicator arrows; clicking toggles sort
  - Global search bar filters all visible columns (case-insensitive string match)
  - Pagination at bottom: page number, page size selector, total count
  - Column visibility toggle via dropdown menu
  - Loading state shows skeleton rows
  - Empty state delegates to `EmptyState` component

- [ ] Create `packages/dashboard/src/components/monitoring/EmptyState.tsx`:
  ```typescript
  export interface EmptyStateProps {
    icon?: React.ReactNode;
    title: string;
    description?: string;
    actionLabel?: string;
    onAction?: () => void;
  }
  ```
  - Centered layout with icon, title, description, and optional CTA button

- [ ] Create `packages/dashboard/src/components/monitoring/ErrorBanner.tsx`:
  ```typescript
  export interface ErrorBannerProps {
    message: string;
    onRetry?: () => void;
    onDismiss?: () => void;
  }
  ```
  - Red background banner with error icon, message, retry button, and X dismiss button
  - Dismissed state hidden via internal boolean state

- [ ] Create `packages/dashboard/src/components/monitoring/ProgressRing.tsx`:
  ```typescript
  export interface ProgressRingProps {
    percent: number;     // 0-100
    size?: number;       // px, default 48
    strokeWidth?: number; // px, default 4
    color?: string;      // default blue-500
    label?: string;      // text inside ring
  }
  ```
  - SVG circle with `stroke-dasharray` and `stroke-dashoffset` for progress
  - Background circle in gray-200
  - Percentage text centered inside the ring

- [ ] Create `packages/dashboard/src/components/monitoring/LatencyBar.tsx`:
  ```typescript
  export interface LatencyBarProps {
    p50: number;
    p95: number;
    p99: number;
    maxMs?: number;     // scale reference, default: p99 * 1.2
    showLabels?: boolean; // default true
  }
  ```
  - Horizontal bar divided into 3 segments:
    - 0 to p50 (green)
    - p50 to p95 (yellow)
    - p95 to p99 (red)
  - Labels below each segment showing the value in ms
  - Total width relative to `maxMs`

- [ ] Create barrel export `packages/dashboard/src/components/monitoring/index.ts`:
  ```typescript
  export { StatusBadge } from './StatusBadge.js';
  export { MetricCard } from './MetricCard.js';
  export { MetricGrid } from './MetricGrid.js';
  export { TimeSeriesChart } from './TimeSeriesChart.js';
  export { DataTable } from './DataTable.js';
  export { EmptyState } from './EmptyState.js';
  export { ErrorBanner } from './ErrorBanner.js';
  export { ProgressRing } from './ProgressRing.js';
  export { LatencyBar } from './LatencyBar.js';
  export { MonitoringLayout } from './MonitoringLayout.js';
  ```

### Files to Create

- CREATE `packages/dashboard/src/components/monitoring/StatusBadge.tsx`
- CREATE `packages/dashboard/src/components/monitoring/MetricCard.tsx`
- CREATE `packages/dashboard/src/components/monitoring/MetricGrid.tsx`
- CREATE `packages/dashboard/src/components/monitoring/TimeSeriesChart.tsx`
- CREATE `packages/dashboard/src/components/monitoring/DataTable.tsx`
- CREATE `packages/dashboard/src/components/monitoring/EmptyState.tsx`
- CREATE `packages/dashboard/src/components/monitoring/ErrorBanner.tsx`
- CREATE `packages/dashboard/src/components/monitoring/ProgressRing.tsx`
- CREATE `packages/dashboard/src/components/monitoring/LatencyBar.tsx`
- CREATE `packages/dashboard/src/components/monitoring/index.ts`
- CREATE `packages/dashboard/src/components/monitoring/__tests__/StatusBadge.test.tsx`
- CREATE `packages/dashboard/src/components/monitoring/__tests__/MetricCard.test.tsx`
- CREATE `packages/dashboard/src/components/monitoring/__tests__/DataTable.test.tsx`
- CREATE `packages/dashboard/src/components/monitoring/__tests__/TimeSeriesChart.test.tsx`
- CREATE `packages/dashboard/src/components/monitoring/__tests__/ProgressRing.test.tsx`
- CREATE `packages/dashboard/src/components/monitoring/__tests__/LatencyBar.test.tsx`
- CREATE `packages/dashboard/src/components/monitoring/__tests__/ErrorBanner.test.tsx`

### Dependencies

- `react` (existing)
- Tailwind CSS classes (existing)
- No external charting libraries

## Testing Strategy

### Unit Tests

- [ ] StatusBadge: renders correct color for each status, pulse animation on unhealthy
- [ ] MetricCard: renders label, value, unit, trend arrow, sparkline SVG
- [ ] MetricGrid: renders correct grid columns, responsive breakpoints
- [ ] TimeSeriesChart: renders SVG with paths for data lines, hover tooltip appears
- [ ] DataTable: renders columns, sorts on click, filters on search, paginates
- [ ] DataTable: shows EmptyState when data is empty
- [ ] DataTable: shows skeleton rows when loading
- [ ] EmptyState: renders title, description, action button
- [ ] ErrorBanner: renders error message, calls onRetry, dismisses on X click
- [ ] ProgressRing: renders SVG circle with correct stroke-dashoffset for 0%, 50%, 100%
- [ ] LatencyBar: renders three segments with correct widths proportional to values
- [ ] LatencyBar: labels show correct ms values

## Completion Checklist

- [ ] All 9 primitive components created
- [ ] Barrel export at `index.ts`
- [ ] Tailwind CSS classes used consistently with existing dashboard
- [ ] No external charting dependencies
- [ ] SVG components render correctly
- [ ] All unit tests written and passing
- [ ] TypeScript strict mode compiles without errors
