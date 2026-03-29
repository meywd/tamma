# Story 23-8: Infrastructure Monitor

Status: planned

## Summary

Build a detailed infrastructure monitoring screen for every backing service: PostgreSQL (connections, query performance, table sizes, slow queries), RabbitMQ (queue depth, message rates, consumers, dead letters), ChromaDB (collections, documents, query latency), OpenSearch (indices, cluster health, shards), Docker containers (status, resource usage, restarts), and inter-service network latency.

## Acceptance Criteria

### PostgreSQL Panel

1. Connection pool metrics:
   - Total connections / max connections (from `pg_stat_activity`)
   - Active connections (state = 'active')
   - Idle connections
   - Waiting connections
   - Connection utilization percentage (progress bar, red at >80%)
2. Query performance:
   - Queries per second (from `pg_stat_statements` if available)
   - Average query duration (ms)
   - Total queries since startup
   - Cache hit ratio (from `pg_stat_user_tables`: `heap_blks_hit / (heap_blks_hit + heap_blks_read)`)
3. Table sizes:
   - Table name, row count estimate, table size, index size, total size
   - Sorted by total size descending
   - Tables: users, api_keys, workflow_definitions, workflow_instances, events (once event store uses PG)
4. Slow queries:
   - Top 10 slowest queries (from `pg_stat_statements`)
   - Each shows: query text (truncated to 200 chars), mean time, calls, total time
   - "Show full query" expands to full text
5. Database size: total size, growth over last 7 days, projected growth

### RabbitMQ Panel

6. Queue overview:
   - Queue name, messages ready, messages unacknowledged, consumers, message rate (in/out per second)
   - Queue depth sparkline (last 30 minutes)
   - Queues with >100 messages ready are highlighted yellow; >1000 red
7. Message rates:
   - Total publish rate (msg/s)
   - Total deliver rate (msg/s)
   - Total acknowledge rate (msg/s)
   - Time series chart of publish/deliver/ack rates over selected period
8. Consumer status:
   - Per-queue: consumer count, consumer utilization percentage
   - Consumers with 0% utilization flagged as idle
9. Dead letter queue:
   - Dead letter queue name(s) and message count
   - "Inspect" button shows last 10 dead letter messages (headers + truncated body)
   - "Purge" button clears the dead letter queue (owner-only, with confirmation)
10. Connection list:
    - Client connections: name, protocol, state, channels, frame rate
    - Vhost: name, message count

### ChromaDB Panel

11. Collection overview:
    - Collection name, document count, embedding dimensions, distance metric
    - Collection size estimate (documents * embedding dimensions * 4 bytes)
    - "Healthy" status from heartbeat endpoint
12. Query latency:
    - Average similarity search latency (from knowledge-base analytics)
    - p50, p95, p99 latency
    - Latency trend sparkline
13. Embedding coverage:
    - Total documents indexed
    - Documents pending indexing (from IndexManagementService status)
    - Index freshness: last indexed timestamp, age of oldest un-indexed file
    - Indexing progress percentage

### OpenSearch Panel

14. Cluster health:
    - Status: green / yellow / red
    - Node count
    - Active shards, relocating shards, initializing shards, unassigned shards
    - Pending tasks count
15. Index overview:
    - Index name, document count, primary store size, total store size, health
    - `tamma-logs-*` indices listed with daily rollover info
    - Index creation date and age
16. Index operations:
    - Indexing rate (docs/s)
    - Search rate (queries/s)
    - Search latency (avg, p95)
17. Disk usage:
    - Per-node disk usage: used / total / percentage
    - Watermark status (low/high/flood thresholds)

### Docker Container Panel

18. Container list:
    - Container name, image, status (running/stopped/restarting), uptime
    - CPU usage percentage (bar)
    - Memory usage: used / limit (bar)
    - Network I/O: rx bytes/s, tx bytes/s
    - Block I/O: read bytes/s, write bytes/s
    - Restart count (flagged red if >0)
19. Container detail (expandable):
    - Environment variables (redacted for secrets)
    - Port mappings
    - Health check status and output
    - Recent logs (last 50 lines from Docker logs)
20. If Docker socket is not accessible, the panel shows "Docker monitoring unavailable -- container access required" with instructions.

### Network Latency Panel

21. Inter-service latency matrix:
    - Rows/columns: each service
    - Cell value: average round-trip time (ms) from health check probes
    - Color: green (<10ms), yellow (10-50ms), red (>50ms)
    - "N/A" for services that don't communicate directly
22. A network topology diagram (reuses DependencyGraph from story 23-1) with latency annotations on edges.

### Request Rate Per Service

23. A chart showing request rates broken down by service:
    - Tamma API: requests/s
    - ELSA Server: requests/s
    - OpenSearch: queries/s
    - RabbitMQ: messages/s
    - PostgreSQL: queries/s
    - ChromaDB: searches/s
24. Time series over selected period.

## API Endpoints Needed

- GET /api/monitoring/infra/postgres -- PostgreSQL metrics (connections, query perf, table sizes, slow queries, DB size)
- GET /api/monitoring/infra/rabbitmq -- RabbitMQ metrics (queues, rates, consumers, dead letters, connections)
- GET /api/monitoring/infra/rabbitmq/dead-letters -- inspect dead letter messages
- POST /api/monitoring/infra/rabbitmq/dead-letters/purge -- purge dead letter queue (owner-only)
- GET /api/monitoring/infra/chromadb -- ChromaDB metrics (collections, latency, coverage)
- GET /api/monitoring/infra/opensearch -- OpenSearch metrics (cluster health, indices, disk)
- GET /api/monitoring/infra/docker -- Docker container metrics (CPU, memory, network, restarts)
- GET /api/monitoring/infra/docker/:name -- single container detail
- GET /api/monitoring/infra/docker/:name/logs -- last N log lines from container
- GET /api/monitoring/infra/network -- inter-service latency matrix
- GET /api/monitoring/infra/request-rates -- per-service request rate time series
- GET /api/monitoring/infra/stream -- SSE stream of infrastructure metric updates (every 5s)

## Dashboard Components

- `InfrastructureMonitorPage` -- page container with tabs per service
- `PostgresPanel` -- PostgreSQL metrics dashboard
- `PgConnectionPool` -- connection pool visualization
- `PgQueryPerformance` -- query stats
- `PgTableSizes` -- table size table
- `PgSlowQueries` -- slow query list
- `RabbitMQPanel` -- RabbitMQ metrics dashboard
- `RmqQueueList` -- queue overview table
- `RmqMessageRates` -- publish/deliver/ack rate chart
- `RmqConsumerStatus` -- consumer utilization
- `RmqDeadLetters` -- dead letter inspection
- `ChromaDBPanel` -- ChromaDB metrics dashboard
- `ChromaCollections` -- collection overview
- `ChromaQueryLatency` -- latency stats
- `ChromaEmbeddingCoverage` -- indexing progress
- `OpenSearchPanel` -- OpenSearch metrics dashboard
- `OsClusterHealth` -- cluster status overview
- `OsIndexOverview` -- index table
- `OsDiskUsage` -- per-node disk bars
- `DockerPanel` -- Docker container dashboard
- `DockerContainerList` -- container table with resource bars
- `DockerContainerDetail` -- expanded container info
- `NetworkLatencyMatrix` -- inter-service latency heatmap
- `ServiceRequestRates` -- per-service request rate chart

## Data Sources

- PostgreSQL: direct SQL queries via pgPool (`pg_stat_activity`, `pg_stat_user_tables`, `pg_stat_statements`, `pg_database_size()`)
- RabbitMQ: Management API at `http://rabbitmq:15672/api/` (queues, connections, overview, vhosts)
- ChromaDB: API at `http://chromadb:8000/api/v2/` (heartbeat, collections, count)
- OpenSearch: API at `http://opensearch:9200/` (`_cluster/health`, `_cat/indices`, `_cat/nodes`, `_cat/shards`)
- Docker: Docker Engine API via `/var/run/docker.sock` (containers, stats)
- Health check probes: reuse `checkHttpService()` from existing admin health routes
- Knowledge Base services (existing): IndexManagementService for indexing status

## Implementation Notes

- PostgreSQL queries use the existing `pgPool` passed to admin health routes. Add new query methods for `pg_stat_activity`, `pg_stat_statements`, etc.
- `pg_stat_statements` requires the extension to be enabled (`CREATE EXTENSION IF NOT EXISTS pg_stat_statements`). If not available, show "pg_stat_statements not enabled" with setup instructions.
- RabbitMQ Management API uses Basic auth (existing credentials from env vars). All data comes from REST endpoints, not AMQP.
- Docker socket access: the API server must have `/var/run/docker.sock` mounted (add to docker-compose volumes). If not available, gracefully degrade.
- Docker stats are streaming -- the API calls `/containers/{id}/stats?stream=false` for a one-shot snapshot.
- Network latency: measured by timing the health check HTTP calls from the API server to each service. Latency matrix is computed on each request.
- OpenSearch watermarks: parse `cluster.routing.allocation.disk.watermark.*` from cluster settings.
- All infrastructure queries are cached for 5 seconds to prevent overloading backing services.

## Files to Create

- `packages/api/src/routes/monitoring/infra-routes.ts`
- `packages/api/src/services/monitoring/postgres-metrics.ts`
- `packages/api/src/services/monitoring/rabbitmq-metrics.ts`
- `packages/api/src/services/monitoring/chromadb-metrics.ts`
- `packages/api/src/services/monitoring/opensearch-metrics.ts`
- `packages/api/src/services/monitoring/docker-metrics.ts`
- `packages/api/src/services/monitoring/network-latency.ts`
- `packages/dashboard/src/pages/monitoring/InfrastructureMonitorPage.tsx`
- `packages/dashboard/src/components/monitoring/infra/PostgresPanel.tsx`
- `packages/dashboard/src/components/monitoring/infra/PgConnectionPool.tsx`
- `packages/dashboard/src/components/monitoring/infra/PgTableSizes.tsx`
- `packages/dashboard/src/components/monitoring/infra/PgSlowQueries.tsx`
- `packages/dashboard/src/components/monitoring/infra/RabbitMQPanel.tsx`
- `packages/dashboard/src/components/monitoring/infra/RmqQueueList.tsx`
- `packages/dashboard/src/components/monitoring/infra/RmqMessageRates.tsx`
- `packages/dashboard/src/components/monitoring/infra/RmqDeadLetters.tsx`
- `packages/dashboard/src/components/monitoring/infra/ChromaDBPanel.tsx`
- `packages/dashboard/src/components/monitoring/infra/OpenSearchPanel.tsx`
- `packages/dashboard/src/components/monitoring/infra/DockerPanel.tsx`
- `packages/dashboard/src/components/monitoring/infra/DockerContainerList.tsx`
- `packages/dashboard/src/components/monitoring/infra/NetworkLatencyMatrix.tsx`
- `packages/dashboard/src/components/monitoring/infra/ServiceRequestRates.tsx`
- `packages/dashboard/src/hooks/monitoring/useInfrastructureMonitor.ts`
- Tests for all API routes, services, and components
