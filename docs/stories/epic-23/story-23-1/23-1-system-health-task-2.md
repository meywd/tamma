# Task 2: System Health Dashboard Frontend Components

**Story:** 23-1-system-health-dashboard
**Epic:** 23

## Task Description

Build the `SystemHealthPage` and all child components: ServiceStatusGrid, ServiceStatusCard, SystemMetricsBar, DependencyGraph, DiskUsagePanel, and HealthTimeline. Uses the shared monitoring primitives from Story 23-12 and the health API endpoints from Task 1.

## Acceptance Criteria

- `SystemHealthPage` renders all sections: service grid, metrics bar, dependency graph, disk usage, timeline
- Service cards show status (color-coded), uptime, response time, error count, memory, CPU
- Unhealthy services have pulsing red animation
- Metrics bar shows request rate, error rate, latency percentiles, SSE connections, engine count, daily cost
- Dependency graph renders SVG nodes and edges color-coded by health
- Disk usage panel shows per-volume bars with percentage
- Health timeline shows service status over time as a heatmap
- Auto-refresh polls health every 10 seconds
- "Refresh All" button triggers immediate health check

## Implementation Details

### Technical Requirements

- [ ] Replace placeholder in `packages/dashboard/src/pages/monitoring/SystemHealthPage.tsx`:
  ```typescript
  import { MonitoringLayout } from '../../components/monitoring/MonitoringLayout.js';
  import { ServiceStatusGrid } from '../../components/monitoring/health/ServiceStatusGrid.js';
  import { SystemMetricsBar } from '../../components/monitoring/health/SystemMetricsBar.js';
  import { DependencyGraph } from '../../components/monitoring/health/DependencyGraph.js';
  import { DiskUsagePanel } from '../../components/monitoring/health/DiskUsagePanel.js';
  import { HealthTimeline } from '../../components/monitoring/health/HealthTimeline.js';
  import { useSystemHealthMonitor } from '../../hooks/monitoring/useSystemHealthMonitor.js';

  export function SystemHealthPage(): JSX.Element;
  ```

- [ ] Create `packages/dashboard/src/hooks/monitoring/useSystemHealthMonitor.ts`:
  ```typescript
  export interface UseSystemHealthResult {
    services: ServiceHealthResult[];
    metrics: SystemMetrics | null;
    dependencies: DependencyGraph | null;
    diskUsage: DiskUsageEntry[];
    history: HealthHistoryEntry[];
    loading: boolean;
    error: string | null;
    refreshAll: () => Promise<void>;
  }

  export function useSystemHealthMonitor(): UseSystemHealthResult;
  ```
  - Fetches all 5 health endpoints on mount
  - Passes `refreshAll` as `autoRefreshFn` to `MonitoringLayout`

- [ ] Create `packages/dashboard/src/components/monitoring/health/ServiceStatusGrid.tsx`:
  - Renders `MetricGrid` containing `ServiceStatusCard` components
  - Passes service data from hook

- [ ] Create `packages/dashboard/src/components/monitoring/health/ServiceStatusCard.tsx`:
  ```typescript
  export interface ServiceStatusCardProps {
    service: string;
    status: 'healthy' | 'degraded' | 'unhealthy' | 'unknown';
    uptime: string | null;
    responseTimeMs: number;
    errorCountLastHour: number;
    lastCheckAt: string;
    memoryMb: number | null;
    cpuPercent: number | null;
  }
  ```
  - Card with colored left border (green/yellow/red/gray)
  - Pulsing animation for unhealthy via `animate-pulse`
  - Service name + icon (mapped from service type)
  - StatusBadge component for status indicator
  - Stats: uptime ("Xd Xh Xm"), response time (ms), errors, memory, CPU
  - "12s ago" relative timestamp for last check

- [ ] Create `packages/dashboard/src/components/monitoring/health/SystemMetricsBar.tsx`:
  - Horizontal bar of MetricCards using MetricGrid
  - Cards: Request Rate, Error Rate, p50/p95/p99 Latency, SSE Connections, Active Engines, Daily Cost
  - Each card includes sparkline from last 30 data points

- [ ] Create `packages/dashboard/src/components/monitoring/health/DependencyGraph.tsx`:
  - SVG rendering with fixed-position nodes (since topology is small and static)
  - Node: circle with service icon/initial, colored by health status
  - Edge: line between nodes, green if both healthy, red if either unhealthy
  - Collapsible panel (collapsed by default)
  - Click node to navigate to infrastructure monitor for that service

- [ ] Create `packages/dashboard/src/components/monitoring/health/DiskUsagePanel.tsx`:
  - List of DiskUsageBar entries
  - Each shows: volume name, used/total, percentage bar (ProgressRing or horizontal bar)
  - Color: green (<70%), yellow (70-90%), red (>90%)
  - Growth trend indicator if available

- [ ] Create `packages/dashboard/src/components/monitoring/health/HealthTimeline.tsx`:
  - X-axis: time (from time range context)
  - Y-axis: one row per service
  - Color blocks: green (healthy), yellow (degraded), red (unhealthy), gray (no data)
  - Hover shows exact status and timestamp
  - SVG-based, reuses TimeSeriesChart patterns

### Files to Create

- CREATE `packages/dashboard/src/components/monitoring/health/ServiceStatusGrid.tsx`
- CREATE `packages/dashboard/src/components/monitoring/health/ServiceStatusCard.tsx`
- CREATE `packages/dashboard/src/components/monitoring/health/SystemMetricsBar.tsx`
- CREATE `packages/dashboard/src/components/monitoring/health/DependencyGraph.tsx`
- CREATE `packages/dashboard/src/components/monitoring/health/DiskUsagePanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/health/HealthTimeline.tsx`
- CREATE `packages/dashboard/src/hooks/monitoring/useSystemHealthMonitor.ts`
- CREATE `packages/dashboard/src/services/monitoring/health-api-client.ts`

### Files to Modify

- MODIFY `packages/dashboard/src/pages/monitoring/SystemHealthPage.tsx` -- replace placeholder

### Dependencies

- Story 23-12: MonitoringLayout, MetricCard, MetricGrid, StatusBadge, ProgressRing, TimeSeriesChart
- Task 1: Health API endpoints

## Testing Strategy

### Unit Tests

- [ ] ServiceStatusCard: renders correct status color for each status
- [ ] ServiceStatusCard: pulsing animation on unhealthy
- [ ] ServiceStatusCard: displays uptime, response time, error count
- [ ] SystemMetricsBar: renders all 6 metric cards
- [ ] DependencyGraph: renders nodes for all services
- [ ] DependencyGraph: edge colors reflect health status
- [ ] DiskUsagePanel: renders bars with correct percentage
- [ ] DiskUsagePanel: color changes at thresholds (70%, 90%)
- [ ] HealthTimeline: renders rows for each service
- [ ] useSystemHealthMonitor: fetches data on mount
- [ ] useSystemHealthMonitor: refreshAll triggers all fetches

## Completion Checklist

- [ ] SystemHealthPage wired with all sections
- [ ] All 6 child components created and rendering
- [ ] Hook fetches from all health endpoints
- [ ] Auto-refresh at 10s intervals
- [ ] Refresh All button works
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
