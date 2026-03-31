---
title: "Task 3: MetricsCollector Service & Integration Tests"
sidebar:
  order: 230
---

**Story:** 23-11-monitoring-api-foundation
**Epic:** 23

## Task Description

Create the `MetricsCollector` service that tracks request count, error count, and latency per route using a Fastify `onResponse` hook. It maintains a sliding window of 1 hour of metrics in memory using a circular buffer. Also write integration tests covering route registration, permission enforcement, SSE stream lifecycle, and metrics collection.

## Acceptance Criteria

- `MetricsCollector` at `packages/api/src/services/monitoring/metrics-collector.ts` tracks per-route request metrics
- Uses a Fastify `onResponse` hook to record every response
- Maintains a circular buffer (default 36000 entries) for 1hr of data at ~10 req/s
- Exposes `getRequestMetrics(sinceMs?)` returning per-route stats
- Exposes `getErrorRate(windowMs?)` returning global error rate (4xx+5xx / total)
- Exposes `getLatencyPercentiles(windowMs?)` returning p50, p95, p99
- Integration tests verify the full pipeline: route registration, auth, SSE, and metrics

## Implementation Details

### Technical Requirements

- [ ] Create `packages/api/src/services/monitoring/metrics-collector.ts`:
  ```typescript
  import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';

  export interface RequestMetricEntry {
    timestamp: number;        // Date.now() when response was sent
    method: string;           // GET, POST, etc.
    routePath: string;        // Fastify route pattern (e.g., /api/monitoring/overview)
    statusCode: number;
    latencyMs: number;        // response time in ms
  }

  export interface RouteMetrics {
    route: string;
    method: string;
    requestCount: number;
    errorCount: number;       // 4xx + 5xx
    avgLatencyMs: number;
    p50LatencyMs: number;
    p95LatencyMs: number;
    p99LatencyMs: number;
  }

  export interface MetricsSnapshot {
    routes: RouteMetrics[];
    totalRequests: number;
    totalErrors: number;
    errorRate: number;          // 0.0 - 1.0
    latency: {
      p50: number;
      p95: number;
      p99: number;
    };
    windowMs: number;
    collectedSince: number;     // oldest entry timestamp
  }

  export class MetricsCollector {
    private buffer: RequestMetricEntry[];
    private writeIndex: number;
    private readonly maxSize: number;
    private count: number;

    constructor(maxSize?: number);  // default 36000

    // Register as Fastify onResponse hook
    registerHook(app: FastifyInstance): void;

    // Record a single request metric
    record(entry: RequestMetricEntry): void;

    // Get per-route metrics snapshot
    getRequestMetrics(sinceMs?: number): MetricsSnapshot;

    // Get global error rate
    getErrorRate(windowMs?: number): number;

    // Get global latency percentiles
    getLatencyPercentiles(windowMs?: number): { p50: number; p95: number; p99: number };

    // Internal: get entries within time window
    private _getEntriesInWindow(sinceMs?: number): RequestMetricEntry[];

    // Internal: compute percentile from sorted array
    private _percentile(sorted: number[], p: number): number;
  }
  ```
  - `registerHook(app)`: calls `app.addHook('onResponse', (request, reply, done) => { ... })`:
    - Extracts `request.routeOptions.url` for the route pattern (not the actual URL)
    - Computes latency from `reply.elapsedTime` (Fastify built-in)
    - Calls `this.record({ timestamp: Date.now(), method: request.method, routePath, statusCode: reply.statusCode, latencyMs })`
  - `record(entry)`: writes to circular buffer at `writeIndex`, increments and wraps
  - `_getEntriesInWindow(sinceMs)`: scans buffer for entries with `timestamp >= sinceMs` (default: last 1hr)
  - `getRequestMetrics(sinceMs)`:
    - Groups entries by `method + routePath`
    - Computes per-route: requestCount, errorCount (status >= 400), avgLatencyMs, p50, p95, p99
    - Computes global totals
  - `getErrorRate(windowMs)`: `totalErrors / totalRequests` for entries in window
  - `getLatencyPercentiles(windowMs)`: sorts all latencies, returns p50/p95/p99

- [ ] Wire `MetricsCollector` into `createApp()`:
  ```typescript
  const metricsCollector = new MetricsCollector();
  metricsCollector.registerHook(app);
  ```

### Files to Create

- CREATE `packages/api/src/services/monitoring/metrics-collector.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/metrics-collector.test.ts`
- CREATE `packages/api/src/routes/monitoring/__tests__/integration.test.ts`

### Files to Modify

- MODIFY `packages/api/src/create-app.ts` -- instantiate MetricsCollector and MonitoringAggregator, pass to registerMonitoringRoutes, register onResponse hook

### Dependencies

- Task 1: route registration and SSE helpers
- Task 2: MonitoringAggregator
- `FastifyInstance`, `FastifyRequest`, `FastifyReply` from `fastify` (existing)

## Testing Strategy

### Unit Tests -- metrics-collector.test.ts

- [ ] Test `record()` adds entry to buffer
- [ ] Test circular buffer wraps correctly when maxSize is exceeded
- [ ] Test `getRequestMetrics()` groups entries by route
- [ ] Test `getRequestMetrics(sinceMs)` filters by timestamp
- [ ] Test per-route requestCount is accurate
- [ ] Test per-route errorCount counts only status >= 400
- [ ] Test per-route avgLatencyMs is computed correctly
- [ ] Test per-route p50, p95, p99 percentiles are accurate
- [ ] Test global error rate is correct
- [ ] Test global latency percentiles aggregate across all routes
- [ ] Test empty buffer returns zero metrics
- [ ] Test single entry returns correct metrics
- [ ] Test `registerHook()` hooks into Fastify onResponse
- [ ] Test hook extracts route pattern, not actual URL (parameterized routes stay grouped)
- [ ] Test `_percentile()` edge cases: single element, two elements, empty array

### Integration Tests -- integration.test.ts

- [ ] Test `/api/monitoring/overview` returns 200 with system overview structure
- [ ] Test `/api/monitoring/overview` returns 401 without auth token
- [ ] Test `/api/monitoring/overview` returns 403 for member-role user (requires admin/owner)
- [ ] Test `/api/monitoring/metrics` returns 200 with metrics snapshot
- [ ] Test `/api/monitoring/metrics?since=...` filters metrics by time
- [ ] Test `/api/monitoring/metrics/stream` returns SSE headers
- [ ] Test SSE stream sends metrics events
- [ ] Test SSE stream sends heartbeat within 20 seconds
- [ ] Test SSE stream cleans up on client disconnect
- [ ] Test MetricsCollector records requests made to the monitoring endpoints themselves

## Completion Checklist

- [ ] `metrics-collector.ts` created with circular buffer
- [ ] `registerHook()` properly hooks into Fastify
- [ ] Circular buffer correctly wraps on overflow
- [ ] Percentile computation handles edge cases
- [ ] Integration with `createApp()` completed
- [ ] All unit tests written and passing
- [ ] All integration tests written and passing
- [ ] TypeScript strict mode compiles without errors
