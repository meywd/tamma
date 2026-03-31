# Task 1: Infrastructure Metrics Collection Services

**Story:** 23-8-infrastructure-monitor
**Epic:** 23

## Task Description

Create backend services for collecting metrics from all backing infrastructure: PostgreSQL (connections, query performance, table sizes, slow queries), RabbitMQ (queues, message rates, consumers, dead letters), ChromaDB (collections, query latency), OpenSearch (cluster health, indices, disk), Docker containers (CPU, memory, network, restarts), and inter-service network latency.

## Acceptance Criteria

- `GET /api/monitoring/infra/postgres` returns PostgreSQL metrics
- `GET /api/monitoring/infra/rabbitmq` returns RabbitMQ metrics
- `GET /api/monitoring/infra/rabbitmq/dead-letters` inspects dead letter messages
- `POST /api/monitoring/infra/rabbitmq/dead-letters/purge` purges dead letter queue (owner-only)
- `GET /api/monitoring/infra/chromadb` returns ChromaDB metrics
- `GET /api/monitoring/infra/opensearch` returns OpenSearch metrics
- `GET /api/monitoring/infra/docker` returns Docker container metrics
- `GET /api/monitoring/infra/docker/:name` returns single container detail
- `GET /api/monitoring/infra/docker/:name/logs` returns last N log lines
- `GET /api/monitoring/infra/network` returns inter-service latency matrix
- `GET /api/monitoring/infra/request-rates` returns per-service request rate time series
- `GET /api/monitoring/infra/stream` SSE stream of infrastructure metrics every 5s
- All queries cached for 5 seconds to prevent overloading backing services

## Implementation Details

### Technical Requirements

- [ ] Create `packages/api/src/routes/monitoring/infra-routes.ts`:
  ```typescript
  export function registerInfraMonitoringRoutes(
    app: FastifyInstance,
    pgMetrics: PostgresMetrics,
    rmqMetrics: RabbitMQMetrics,
    chromaMetrics: ChromaDBMetrics,
    osMetrics: OpenSearchMetrics,
    dockerMetrics: DockerMetrics,
    networkLatency: NetworkLatency,
  ): void;
  ```

- [ ] Create `packages/api/src/services/monitoring/postgres-metrics.ts`:
  ```typescript
  export interface PgConnectionPoolMetrics {
    totalConnections: number;
    maxConnections: number;
    activeConnections: number;
    idleConnections: number;
    waitingConnections: number;
    utilizationPercent: number;
  }

  export interface PgQueryPerformance {
    queriesPerSecond: number;
    avgQueryDurationMs: number;
    totalQueries: number;
    cacheHitRatio: number;      // 0.0 - 1.0
  }

  export interface PgTableSize {
    tableName: string;
    rowCountEstimate: number;
    tableSizeBytes: number;
    indexSizeBytes: number;
    totalSizeBytes: number;
  }

  export interface PgSlowQuery {
    query: string;              // truncated to 200 chars
    fullQuery: string;
    meanTimeMs: number;
    calls: number;
    totalTimeMs: number;
  }

  export class PostgresMetrics {
    constructor(deps: { pgPool: unknown });

    async getMetrics(): Promise<{
      connections: PgConnectionPoolMetrics;
      queryPerformance: PgQueryPerformance;
      tableSizes: PgTableSize[];
      slowQueries: PgSlowQuery[];
      databaseSizeBytes: number;
      growthBytesPerDay: number | null;
    }>;
  }
  ```
  - Uses `pg_stat_activity` for connections
  - Uses `pg_stat_statements` for query performance (gracefully handles if extension not enabled)
  - Uses `pg_stat_user_tables` for cache hit ratio
  - Uses `pg_class` and `pg_indexes` for table/index sizes
  - Uses `pg_database_size()` for total size

- [ ] Create `packages/api/src/services/monitoring/rabbitmq-metrics.ts`:
  ```typescript
  export class RabbitMQMetrics {
    constructor(deps: { managementUrl: string; username: string; password: string });

    async getMetrics(): Promise<{
      queues: RmqQueueMetrics[];
      publishRate: number;
      deliverRate: number;
      ackRate: number;
      connections: RmqConnection[];
    }>;

    async getDeadLetters(limit?: number): Promise<RmqDeadLetterMessage[]>;
    async purgeDeadLetters(): Promise<void>;
  }
  ```
  - Uses RabbitMQ Management API: `GET /api/queues`, `GET /api/overview`, `GET /api/connections`
  - Dead letters: `GET /api/queues/%2F/{dlq_name}/get` with `count` param
  - Purge: `DELETE /api/queues/%2F/{dlq_name}/contents`

- [ ] Create `packages/api/src/services/monitoring/chromadb-metrics.ts`:
  ```typescript
  export class ChromaDBMetrics {
    constructor(deps: { chromaUrl: string });

    async getMetrics(): Promise<{
      collections: { name: string; documentCount: number; embeddingDimensions: number; distanceMetric: string; storageSizeEstimate: number }[];
      healthy: boolean;
      queryLatency: { avg: number; p50: number; p95: number; p99: number } | null;
      totalDocuments: number;
      pendingIndexing: number;
    }>;
  }
  ```
  - Uses ChromaDB API: `GET /api/v2/heartbeat`, `GET /api/v2/collections`, `GET /api/v2/collections/{name}/count`
  - Integrates with IndexManagementService for pending indexing count

- [ ] Create `packages/api/src/services/monitoring/opensearch-metrics.ts`:
  ```typescript
  export class OpenSearchMetrics {
    constructor(deps: { opensearchUrl: string | null });

    async getMetrics(): Promise<{
      clusterHealth: { status: string; nodeCount: number; activeShards: number; relocatingShards: number; initializingShards: number; unassignedShards: number; pendingTasks: number };
      indices: { name: string; documentCount: number; primarySize: number; totalSize: number; health: string; createdDate: string }[];
      indexingRate: number;
      searchRate: number;
      searchLatency: { avg: number; p95: number };
      diskUsage: { node: string; used: number; total: number; percent: number }[];
    }> | null;
  ```
  - Uses: `GET /_cluster/health`, `GET /_cat/indices?format=json`, `GET /_cat/nodes?format=json&h=name,disk.used,disk.total,disk.used_percent`
  - Returns null if OpenSearch not configured

- [ ] Create `packages/api/src/services/monitoring/docker-metrics.ts`:
  ```typescript
  export class DockerMetrics {
    constructor(deps: { socketPath?: string });  // default /var/run/docker.sock

    async getContainers(): Promise<DockerContainerMetrics[]>;
    async getContainerDetail(name: string): Promise<DockerContainerDetail | null>;
    async getContainerLogs(name: string, lines?: number): Promise<string[]>;
    isAvailable(): boolean;
  }
  ```
  - Uses Docker Engine API via Unix socket: `GET /containers/json`, `GET /containers/{id}/stats?stream=false`
  - Graceful degradation: if socket not mounted, `isAvailable()` returns false
  - Env vars redacted for secrets before returning

- [ ] Create `packages/api/src/services/monitoring/network-latency.ts`:
  ```typescript
  export interface LatencyMatrixEntry {
    from: string;
    to: string;
    latencyMs: number | null;     // null = not applicable
    status: 'green' | 'yellow' | 'red' | 'na';
  }

  export class NetworkLatency {
    constructor(deps: { serviceUrls: Record<string, string> });
    async getMatrix(): Promise<LatencyMatrixEntry[]>;
  }
  ```
  - Measures HTTP round-trip time to each service's health endpoint
  - Color: green (<10ms), yellow (10-50ms), red (>50ms)

### Files to Create

- CREATE `packages/api/src/routes/monitoring/infra-routes.ts`
- CREATE `packages/api/src/services/monitoring/postgres-metrics.ts`
- CREATE `packages/api/src/services/monitoring/rabbitmq-metrics.ts`
- CREATE `packages/api/src/services/monitoring/chromadb-metrics.ts`
- CREATE `packages/api/src/services/monitoring/opensearch-metrics.ts`
- CREATE `packages/api/src/services/monitoring/docker-metrics.ts`
- CREATE `packages/api/src/services/monitoring/network-latency.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/postgres-metrics.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/rabbitmq-metrics.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/chromadb-metrics.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/opensearch-metrics.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/docker-metrics.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/network-latency.test.ts`
- CREATE `packages/api/src/routes/monitoring/__tests__/infra-routes.test.ts`

### Files to Modify

- MODIFY `packages/api/src/routes/monitoring/index.ts` -- register infra routes

### Dependencies

- Story 23-11: route registration, SSE helpers
- `pg` Pool for PostgreSQL (existing)
- HTTP client for RabbitMQ Management API, ChromaDB API, OpenSearch API, Docker API
- Node.js `net` module for Docker Unix socket

## Testing Strategy

### Unit Tests

- [ ] PostgresMetrics: parses pg_stat_activity correctly
- [ ] PostgresMetrics: handles pg_stat_statements not installed gracefully
- [ ] PostgresMetrics: computes cache hit ratio correctly
- [ ] RabbitMQMetrics: parses management API responses
- [ ] RabbitMQMetrics: dead letter inspection returns truncated body
- [ ] ChromaDBMetrics: aggregates collection stats
- [ ] OpenSearchMetrics: returns null when not configured
- [ ] OpenSearchMetrics: parses cluster health response
- [ ] DockerMetrics: returns empty when socket not available
- [ ] DockerMetrics: redacts secret env vars
- [ ] DockerMetrics: parses stats response (CPU, memory, network)
- [ ] NetworkLatency: measures latency to all services
- [ ] NetworkLatency: categorizes latency into green/yellow/red
- [ ] All metrics services: cache results for 5 seconds

## Completion Checklist

- [ ] All 12 API endpoints implemented
- [ ] PostgreSQL metrics from pg_stat_* views
- [ ] RabbitMQ metrics from Management API
- [ ] ChromaDB metrics from HTTP API
- [ ] OpenSearch metrics from REST API
- [ ] Docker metrics from Unix socket (with graceful degradation)
- [ ] Network latency matrix
- [ ] 5-second caching on all queries
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
