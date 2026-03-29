# Task 2: Infrastructure Monitor Frontend Components

**Story:** 23-8-infrastructure-monitor
**Epic:** 23

## Task Description

Build the InfrastructureMonitorPage with tabbed panels for each backing service: PostgreSQL, RabbitMQ, ChromaDB, OpenSearch, Docker containers, and network latency. Each panel provides deep insight into that service's operational metrics.

## Acceptance Criteria

- PostgreSQL panel: connection pool, query performance, table sizes, slow queries, DB size
- RabbitMQ panel: queue overview, message rates, consumers, dead letter inspection/purge
- ChromaDB panel: collections, query latency, embedding coverage
- OpenSearch panel: cluster health, indices, disk usage
- Docker panel: container list with CPU/memory/network bars, expandable detail with logs
- Network latency matrix: inter-service latency heatmap
- Per-service request rate chart
- Auto-refresh every 5 seconds via SSE stream

## Implementation Details

### Technical Requirements

- [ ] Replace placeholder `packages/dashboard/src/pages/monitoring/InfrastructureMonitorPage.tsx`:
  - MonitoringLayout with title "Infrastructure Monitor"
  - Tab navigation: PostgreSQL, RabbitMQ, ChromaDB, OpenSearch, Docker, Network
  - SSE connection to `/api/monitoring/infra/stream`

- [ ] Create `packages/dashboard/src/hooks/monitoring/useInfrastructureMonitor.ts`

- [ ] Create `packages/dashboard/src/components/monitoring/infra/PostgresPanel.tsx`:
  - Parent panel containing PgConnectionPool, PgQueryPerformance (MetricCards), PgTableSizes, PgSlowQueries

- [ ] Create `packages/dashboard/src/components/monitoring/infra/PgConnectionPool.tsx`:
  - ProgressRing for utilization percentage (red at >80%)
  - MetricCards: total, active, idle, waiting connections
  - Max connections label

- [ ] Create `packages/dashboard/src/components/monitoring/infra/PgTableSizes.tsx`:
  - DataTable: Table Name, Row Count, Table Size, Index Size, Total Size
  - Sorted by total size descending
  - Size values formatted as human-readable (KB/MB/GB)

- [ ] Create `packages/dashboard/src/components/monitoring/infra/PgSlowQueries.tsx`:
  - DataTable: Query (truncated), Mean Time, Calls, Total Time
  - "Show full query" expands to full text in monospace

- [ ] Create `packages/dashboard/src/components/monitoring/infra/RabbitMQPanel.tsx`:
  - Parent panel containing RmqQueueList, RmqMessageRates, RmqConsumerStatus, RmqDeadLetters

- [ ] Create `packages/dashboard/src/components/monitoring/infra/RmqQueueList.tsx`:
  - DataTable: Queue Name, Messages Ready, Unacknowledged, Consumers, Rate In/Out
  - Sparkline for queue depth (last 30 min)
  - Color: >100 yellow, >1000 red

- [ ] Create `packages/dashboard/src/components/monitoring/infra/RmqMessageRates.tsx`:
  - TimeSeriesChart: publish, deliver, acknowledge rates over time
  - MetricCards: current publish/deliver/ack rates

- [ ] Create `packages/dashboard/src/components/monitoring/infra/RmqDeadLetters.tsx`:
  - Dead letter queue count
  - "Inspect" shows last 10 messages (headers + truncated body)
  - "Purge" button with confirmation dialog (owner-only)

- [ ] Create `packages/dashboard/src/components/monitoring/infra/ChromaDBPanel.tsx`:
  - Collection table: name, documents, dimensions, metric, size, status
  - Query latency: avg/p50/p95/p99 using LatencyBar
  - Embedding coverage from IndexManagementService

- [ ] Create `packages/dashboard/src/components/monitoring/infra/OpenSearchPanel.tsx`:
  - Cluster health: StatusBadge, node count, shard stats
  - Index table: name, documents, size, health, age
  - Disk usage: per-node ProgressRing with watermark thresholds

- [ ] Create `packages/dashboard/src/components/monitoring/infra/DockerPanel.tsx`:
  - Container table: name, image, status, uptime, CPU bar, memory bar, network I/O, restarts
  - Restart count flagged red if >0
  - Row click expands DockerContainerDetail

- [ ] Create `packages/dashboard/src/components/monitoring/infra/DockerContainerList.tsx`:
  - DataTable with progress bars for CPU and memory
  - Status badge: running=green, stopped=red, restarting=yellow

- [ ] Create `packages/dashboard/src/components/monitoring/infra/DockerContainerDetail.tsx`:
  - Environment variables (redacted secrets)
  - Port mappings
  - Health check status and output
  - Last 50 log lines in monospace terminal-style view

- [ ] Create `packages/dashboard/src/components/monitoring/infra/NetworkLatencyMatrix.tsx`:
  - Grid table: rows and columns = services
  - Cell: latency in ms with background color (green/yellow/red)
  - "N/A" for services that don't communicate

- [ ] Create `packages/dashboard/src/components/monitoring/infra/ServiceRequestRates.tsx`:
  - TimeSeriesChart: request rates per service over time
  - Lines: API, ELSA, OpenSearch, RabbitMQ, PostgreSQL, ChromaDB

- [ ] Create `packages/dashboard/src/services/monitoring/infra-api-client.ts`

### Files to Create

- CREATE `packages/dashboard/src/components/monitoring/infra/PostgresPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/infra/PgConnectionPool.tsx`
- CREATE `packages/dashboard/src/components/monitoring/infra/PgTableSizes.tsx`
- CREATE `packages/dashboard/src/components/monitoring/infra/PgSlowQueries.tsx`
- CREATE `packages/dashboard/src/components/monitoring/infra/RabbitMQPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/infra/RmqQueueList.tsx`
- CREATE `packages/dashboard/src/components/monitoring/infra/RmqMessageRates.tsx`
- CREATE `packages/dashboard/src/components/monitoring/infra/RmqDeadLetters.tsx`
- CREATE `packages/dashboard/src/components/monitoring/infra/ChromaDBPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/infra/OpenSearchPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/infra/DockerPanel.tsx`
- CREATE `packages/dashboard/src/components/monitoring/infra/DockerContainerList.tsx`
- CREATE `packages/dashboard/src/components/monitoring/infra/DockerContainerDetail.tsx`
- CREATE `packages/dashboard/src/components/monitoring/infra/NetworkLatencyMatrix.tsx`
- CREATE `packages/dashboard/src/components/monitoring/infra/ServiceRequestRates.tsx`
- CREATE `packages/dashboard/src/hooks/monitoring/useInfrastructureMonitor.ts`
- CREATE `packages/dashboard/src/services/monitoring/infra-api-client.ts`

### Files to Modify

- MODIFY `packages/dashboard/src/pages/monitoring/InfrastructureMonitorPage.tsx` -- replace placeholder

### Dependencies

- Story 23-12: MonitoringLayout, MetricCard, MetricGrid, DataTable, TimeSeriesChart, ProgressRing, LatencyBar, StatusBadge
- Task 1: Infrastructure API endpoints

## Testing Strategy

### Unit Tests

- [ ] PgConnectionPool: utilization bar turns red at >80%
- [ ] PgTableSizes: formats sizes as human-readable (MB/GB)
- [ ] PgSlowQueries: "Show full query" expands truncated query
- [ ] RmqQueueList: highlights queues by depth threshold
- [ ] RmqDeadLetters: purge button requires confirmation
- [ ] DockerContainerList: CPU/memory bars render correctly
- [ ] DockerContainerList: restart count flagged red when >0
- [ ] DockerContainerDetail: secrets are redacted in env vars
- [ ] NetworkLatencyMatrix: colors cells by latency threshold
- [ ] OpenSearchPanel: cluster health shows correct status badge
- [ ] useInfrastructureMonitor: fetches per-tab data

## Completion Checklist

- [ ] All 15 child components created
- [ ] 6-tab navigation per service
- [ ] PostgreSQL deep metrics
- [ ] RabbitMQ queue and dead letter management
- [ ] ChromaDB collection health
- [ ] OpenSearch cluster monitoring
- [ ] Docker container monitoring with fallback
- [ ] Network latency heatmap
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
