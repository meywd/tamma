---
title: "Task 1: Health API Routes & Backend Services"
sidebar:
  order: 230
---

**Story:** 23-1-system-health-dashboard
**Epic:** 23

## Task Description

Create the backend API routes and services for the system health dashboard: health aggregation for all services, health history ring buffer, service dependency graph data, and disk usage collection. These endpoints serve the frontend System Health page.

## Acceptance Criteria

- `GET /api/monitoring/health/all` returns aggregated health for all services (PostgreSQL, RabbitMQ, ChromaDB, OpenSearch, ELSA, Tamma API, Engines, Nginx)
- `GET /api/monitoring/health/history` returns historical health status entries with time range filtering
- `GET /api/monitoring/health/stream` SSE stream emits health updates on each check cycle
- `GET /api/monitoring/health/dependencies` returns the service dependency graph as `{ nodes, edges }`
- `GET /api/monitoring/health/disk-usage` returns disk/storage usage per volume/service
- `GET /api/monitoring/metrics/summary` returns system-wide metrics bar data
- Health history uses in-memory ring buffer (1440 entries per service for 24h at 1/min)

## Implementation Details

### Technical Requirements

- [ ] Create `packages/api/src/routes/monitoring/health-routes.ts`:
  ```typescript
  import type { FastifyInstance } from 'fastify';
  import type { HealthAggregator } from '../../services/monitoring/health-aggregator.js';
  import type { HealthHistory } from '../../services/monitoring/health-history.js';
  import type { DependencyGraphService } from '../../services/monitoring/dependency-graph.js';
  import type { MetricsCollector } from '../../services/monitoring/metrics-collector.js';
  import { createSSEStream } from './sse-helpers.js';

  export function registerHealthMonitoringRoutes(
    app: FastifyInstance,
    healthAgg: HealthAggregator,
    healthHistory: HealthHistory,
    depGraph: DependencyGraphService,
    metricsCollector: MetricsCollector,
  ): void;
  ```
  - `GET /health/all`: calls `healthAgg.checkAll()`, records results in `healthHistory`, returns response
  - `GET /health/history`: query params `service`, `since`, `until`, `limit`; queries `healthHistory.getEntries()`
  - `GET /health/stream`: SSE stream, checks health every 10s and emits to connected clients
  - `GET /health/dependencies`: returns `depGraph.getGraph()`
  - `GET /health/disk-usage`: calls `healthAgg.getDiskUsage()`
  - `GET /metrics/summary`: combines `metricsCollector` data with engine count + cost data

- [ ] Create `packages/api/src/services/monitoring/health-aggregator.ts`:
  ```typescript
  export interface ServiceHealthResult {
    service: string;
    status: 'healthy' | 'degraded' | 'unhealthy' | 'unknown';
    responseTimeMs: number;
    uptime: string | null;            // "Xd Xh Xm" format
    errorCountLastHour: number;
    lastCheckAt: string;              // ISO 8601
    memoryMb: number | null;
    cpuPercent: number | null;
  }

  export interface DiskUsageEntry {
    volume: string;
    service: string;
    usedBytes: number;
    totalBytes: number | null;
    percentUsed: number | null;
    growthTrend: number | null;       // bytes/day over last 7 days
  }

  export class HealthAggregator {
    constructor(deps: {
      pgPool: unknown;           // pg Pool for PostgreSQL checks
      healthCheckFn: (url: string) => Promise<{ ok: boolean; responseTimeMs: number }>;
      serviceUrls: Record<string, string>;
      engineRegistry: unknown;
    });

    async checkAll(): Promise<ServiceHealthResult[]>;
    async getDiskUsage(): Promise<DiskUsageEntry[]>;
  }
  ```
  - `checkAll()` probes each service in parallel via HTTP health checks
  - PostgreSQL: `SELECT 1` query, `pg_database_size()` for disk
  - RabbitMQ: `GET http://rabbitmq:15672/api/healthchecks/node` with basic auth
  - ChromaDB: `GET http://chromadb:8000/api/v2/heartbeat`
  - OpenSearch: `GET http://opensearch:9200/_cluster/health`
  - ELSA: `GET http://elsa:8080/health` (existing)
  - Nginx: `GET http://nginx:80/health`
  - Engines: from EngineRegistry state
  - Uses `Promise.allSettled()` so one failure does not block others
  - Uptime tracking: stores `startedAt` per service in a Map; resets on failure

- [ ] Create `packages/api/src/services/monitoring/health-history.ts`:
  ```typescript
  export interface HealthHistoryEntry {
    service: string;
    status: 'healthy' | 'degraded' | 'unhealthy' | 'unknown';
    timestamp: number;
    responseTimeMs: number;
  }

  export class HealthHistory {
    private buffers: Map<string, HealthHistoryEntry[]>;
    private readonly maxEntriesPerService: number; // default 1440

    constructor(maxEntriesPerService?: number);
    record(entry: HealthHistoryEntry): void;
    getEntries(options?: {
      service?: string;
      since?: number;
      until?: number;
      limit?: number;
    }): HealthHistoryEntry[];
    getServices(): string[];
  }
  ```
  - Per-service ring buffer using `Array` with splice/push
  - `record()` adds entry, drops oldest if exceeding max
  - `getEntries()` filters by service, time range, and limit

- [ ] Create `packages/api/src/services/monitoring/dependency-graph.ts`:
  ```typescript
  export interface DependencyNode {
    id: string;
    label: string;
    type: 'api' | 'database' | 'queue' | 'search' | 'vector' | 'workflow' | 'proxy' | 'engine';
  }

  export interface DependencyEdge {
    from: string;
    to: string;
    label?: string;
  }

  export interface DependencyGraph {
    nodes: DependencyNode[];
    edges: DependencyEdge[];
  }

  export class DependencyGraphService {
    getGraph(): DependencyGraph;
  }
  ```
  - Hardcoded topology from docker-compose:
    - tamma-api depends on: postgresql, rabbitmq, elsa, opensearch, chromadb
    - elsa depends on: postgresql
    - tamma-engine depends on: tamma-api
    - nginx depends on: tamma-api, tamma-dashboard, elsa
    - dashboard depends on: tamma-api (static, served by nginx)

### Files to Create

- CREATE `packages/api/src/routes/monitoring/health-routes.ts`
- CREATE `packages/api/src/services/monitoring/health-aggregator.ts`
- CREATE `packages/api/src/services/monitoring/health-history.ts`
- CREATE `packages/api/src/services/monitoring/dependency-graph.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/health-aggregator.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/health-history.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/dependency-graph.test.ts`
- CREATE `packages/api/src/routes/monitoring/__tests__/health-routes.test.ts`

### Files to Modify

- MODIFY `packages/api/src/routes/monitoring/index.ts` -- register health monitoring routes
- MODIFY `packages/api/src/routes/monitoring/types.ts` -- add health services to MonitoringServices

### Dependencies

- Story 23-11: SSE helpers, MonitoringServices interface, route registration
- Existing admin health routes at `packages/api/src/routes/admin/health-routes.ts` for reference
- `pg` Pool (existing) for PostgreSQL queries
- EngineRegistry (existing)

## Testing Strategy

### Unit Tests

- [ ] HealthAggregator: checkAll returns results for all services
- [ ] HealthAggregator: one service failure does not affect others (Promise.allSettled)
- [ ] HealthAggregator: uptime tracking resets on service failure
- [ ] HealthAggregator: getDiskUsage returns entries for all volumes
- [ ] HealthHistory: record adds entry to correct service buffer
- [ ] HealthHistory: ring buffer drops oldest when full
- [ ] HealthHistory: getEntries filters by service, time range, limit
- [ ] HealthHistory: getServices returns all tracked services
- [ ] DependencyGraph: getGraph returns correct nodes and edges
- [ ] DependencyGraph: all expected services present
- [ ] Health routes: GET /health/all returns expected structure
- [ ] Health routes: GET /health/history respects query params
- [ ] Health routes: GET /health/stream returns SSE headers
- [ ] Health routes: GET /health/dependencies returns graph

## Completion Checklist

- [ ] All 4 route endpoints implemented
- [ ] HealthAggregator probes all services in parallel
- [ ] HealthHistory ring buffer works correctly
- [ ] DependencyGraph returns hardcoded topology
- [ ] Routes registered in monitoring index
- [ ] All tests written and passing
- [ ] TypeScript strict mode compiles
