# Story 23-1: System Health Dashboard (Overview)

Status: planned

## Summary

Build the primary monitoring overview screen that gives operators an at-a-glance view of every service in the Tamma platform: health status, uptime, resource usage, response times, error rates, request rates, and a service dependency graph. This is the landing page for `/monitoring/health`.

## Acceptance Criteria

### Service Status Grid

1. The page displays a grid of service health cards for ALL services:
   - PostgreSQL
   - RabbitMQ
   - ChromaDB
   - OpenSearch
   - ELSA Server
   - Tamma API (Node.js)
   - Tamma Engine (per registered engine in EngineRegistry)
   - Tamma Dashboard (self-check via meta tag or build info)
   - Nginx (reverse proxy)
2. Each service card shows:
   - Service name and icon
   - Status indicator: healthy (green), degraded (yellow), unhealthy (red), unknown (gray)
   - Uptime since last restart (formatted as "Xd Xh Xm")
   - Current response time in milliseconds (from last health check)
   - Error count in the last hour
   - Last health check timestamp (relative, e.g., "12s ago")
   - Memory usage (MB) and CPU percentage where available
3. Cards are color-coded by status with a pulsing animation for unhealthy services.
4. A "Refresh All" button triggers an immediate health check across all services.
5. Auto-refresh polls `/api/monitoring/health/all` every 10 seconds.

### System-Wide Metrics Bar

6. A metrics bar at the top of the page shows:
   - Total request rate (req/s across all API endpoints)
   - Total error rate (percentage of 4xx+5xx responses)
   - Latency percentiles: p50, p95, p99 (from MetricsCollector)
   - Active SSE connections count
   - Active engine count (from EngineRegistry)
   - Total cost accrued today (from CostTracker)
7. Each metric shows a small sparkline (last 30 data points, 1 per minute).

### Service Dependency Graph

8. A collapsible panel shows a visual dependency graph:
   - Nodes: each service
   - Edges: dependency relationships (e.g., tamma-api depends on PostgreSQL, ELSA, RabbitMQ)
   - Node color reflects current health status
   - Edge color: green if both endpoints healthy, red if either unhealthy
   - Clicking a node navigates to the detailed infrastructure view for that service
9. The dependency graph data is hardcoded (not discovered) since the topology is known from docker-compose.

### Disk Usage

10. A disk usage panel shows storage consumption per Docker volume:
    - `postgres_data` -- estimated from PostgreSQL `pg_database_size()`
    - `rabbitmq_data` -- from RabbitMQ management API `/api/overview`
    - `elsa_storage` -- from ELSA health endpoint if available, otherwise "N/A"
    - OpenSearch indices -- from `/_cat/indices?format=json`
    - ChromaDB storage -- from existing `/api/knowledge-base/vector-db/storage`
11. Each entry shows: volume name, used space, percentage bar, growth trend (last 7 days).

### Historical Health Timeline

12. A timeline chart at the bottom shows service health status over the selected time range:
    - X-axis: time
    - Y-axis: services (one row per service)
    - Color blocks: green (healthy), yellow (degraded), red (unhealthy), gray (no data)
    - Hover shows exact status and timestamp
    - This requires persisting health check results (see API endpoints below).

## API Endpoints Needed

- GET /api/monitoring/health/all -- aggregated health check for all services (extends existing `/api/admin/health` with additional fields: uptime, error count, memory, CPU)
- GET /api/monitoring/health/history -- historical health status entries, query params: `service`, `since`, `until`, `limit`
- GET /api/monitoring/health/stream -- SSE stream of health updates (emits on every check cycle)
- GET /api/monitoring/health/dependencies -- returns the service dependency graph as `{ nodes: [...], edges: [...] }`
- GET /api/monitoring/health/disk-usage -- returns disk/storage usage per volume/service
- GET /api/monitoring/metrics/summary -- returns the system-wide metrics bar data

## Dashboard Components

- `SystemHealthPage` -- page container with MonitoringLayout
- `ServiceStatusGrid` -- responsive grid of ServiceStatusCards
- `ServiceStatusCard` -- individual service health card with status, uptime, response time, errors, memory
- `SystemMetricsBar` -- horizontal bar of system-wide metrics with sparklines
- `DependencyGraph` -- SVG/Canvas service dependency visualization
- `DependencyNode` -- single node in the graph
- `DependencyEdge` -- connection line between nodes
- `DiskUsagePanel` -- collapsible panel showing storage per volume
- `DiskUsageBar` -- single volume usage bar
- `HealthTimeline` -- historical health status heatmap
- `HealthTimelineRow` -- single service row in the timeline

## Data Sources

- `/api/admin/health` (existing) -- health checks for PostgreSQL, ELSA, OpenSearch, RabbitMQ, ChromaDB
- EngineRegistry (existing) -- engine count and per-engine state
- MetricsCollector (from story 23-11) -- request rate, error rate, latency percentiles
- CostTracker (existing) -- daily cost total
- PostgreSQL `pg_database_size()` -- database size
- RabbitMQ Management API `/api/overview` -- queue counts, message rates, disk usage
- OpenSearch `/_cat/indices?format=json` -- index sizes
- ChromaDB `/api/knowledge-base/vector-db/storage` (existing) -- vector DB storage

## Implementation Notes

- Health check history requires a new in-memory ring buffer (last 24h of checks, one per minute = 1440 entries per service). Optionally persisted to PostgreSQL in a future story.
- The dependency graph uses a simple force-directed layout or fixed positioning since the topology is small and static.
- Uptime tracking: store a `startedAt` timestamp per service when health check first succeeds. Reset on first failure.
- For nginx health: the API server can probe `http://nginx:80/health` (a location block returning 200).
- Memory/CPU for Docker containers: if running in Docker, query the Docker socket `/containers/{id}/stats`. If not available, show "N/A".

## Files to Create

- `packages/api/src/routes/monitoring/health-routes.ts`
- `packages/api/src/services/monitoring/health-aggregator.ts`
- `packages/api/src/services/monitoring/health-history.ts`
- `packages/api/src/services/monitoring/dependency-graph.ts`
- `packages/dashboard/src/pages/monitoring/SystemHealthPage.tsx`
- `packages/dashboard/src/components/monitoring/health/ServiceStatusGrid.tsx`
- `packages/dashboard/src/components/monitoring/health/ServiceStatusCard.tsx`
- `packages/dashboard/src/components/monitoring/health/SystemMetricsBar.tsx`
- `packages/dashboard/src/components/monitoring/health/DependencyGraph.tsx`
- `packages/dashboard/src/components/monitoring/health/DiskUsagePanel.tsx`
- `packages/dashboard/src/components/monitoring/health/HealthTimeline.tsx`
- `packages/dashboard/src/hooks/monitoring/useSystemHealthMonitor.ts`
- Tests for all API routes and services
