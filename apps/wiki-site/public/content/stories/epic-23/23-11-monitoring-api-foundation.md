---
title: "Story 23-11: Monitoring API Foundation"
sidebar:
  order: 230
---

Status: planned

## Summary

Build the shared API infrastructure that all monitoring dashboard screens depend on: route registration, SSE helpers, data aggregation services, and the monitoring middleware layer. This is the foundation story that must be completed before any dashboard screen story.

## Acceptance Criteria

1. A new Fastify plugin `registerMonitoringRoutes()` is created at `packages/api/src/routes/monitoring/index.ts` that registers all monitoring sub-routes under `/api/monitoring/*`.
2. All monitoring routes require `settings:view` permission enforced via the existing `requirePermission` hook.
3. A reusable SSE helper module `packages/api/src/routes/monitoring/sse-helpers.ts` provides:
   - `createSSEStream(reply, options)` -- sets headers, returns a `send(event, data)` function and cleanup handle
   - Automatic heartbeat every 15s to keep connections alive through nginx
   - Automatic cleanup on client disconnect
   - Backpressure handling (drop events if client is slow)
4. A `MonitoringAggregator` service at `packages/api/src/services/monitoring/aggregator.ts` that:
   - Accepts references to EngineRegistry, HealthService, DiagnosticsService, ConfigService, WorkflowStore, CostTracker
   - Provides `getSystemOverview()` returning a combined snapshot of all system metrics
   - Caches results for 5 seconds to prevent thundering herd on dashboard load
5. A time-series bucket helper `packages/api/src/services/monitoring/time-buckets.ts` that:
   - Groups numeric data into fixed-width time buckets (1min, 5min, 1hr, 1day)
   - Computes count, sum, min, max, avg, p50, p95, p99 per bucket
   - Accepts an array of `{ timestamp: number; value: number }` and returns buckets
6. A `MetricsCollector` service at `packages/api/src/services/monitoring/metrics-collector.ts` that:
   - Tracks request count, error count, and latency per route using Fastify `onResponse` hook
   - Maintains a sliding window of 1 hour of metrics in memory
   - Exposes `getRequestMetrics(sinceMs?)` returning per-route stats
   - Exposes `getErrorRate(windowMs?)` returning global error rate
   - Exposes `getLatencyPercentiles(windowMs?)` returning p50, p95, p99
7. Integration tests covering route registration, permission enforcement, SSE stream lifecycle, and metrics collection.
8. All new code follows existing patterns: kebab-case files, `I` prefix interfaces, async/await, Pino logging.

## API Endpoints Needed

- GET /api/monitoring/overview -- returns MonitoringAggregator.getSystemOverview()
- GET /api/monitoring/metrics -- returns MetricsCollector.getRequestMetrics()
- GET /api/monitoring/metrics/stream -- SSE stream of metrics every 5s

## Dashboard Components

None (this is backend-only infrastructure).

## Data Sources

- EngineRegistry (existing, `packages/api/src/engine-registry.ts`)
- HealthService (existing, `packages/api/src/services/settings/HealthService.ts`)
- DiagnosticsService (existing, `packages/api/src/services/settings/DiagnosticsService.ts`)
- ConfigService (existing, `packages/api/src/services/settings/ConfigService.ts`)
- IWorkflowStore (existing, `packages/api/src/persistence/workflow-store.ts`)
- ICostTracker (existing, `packages/cost-monitor/src/cost-tracker.ts`)
- Fastify `onResponse` hook for request metrics

## Implementation Notes

- The MetricsCollector uses a circular buffer of fixed size (e.g., 36000 entries for 1hr at 10 req/s) to bound memory.
- SSE backpressure: if `reply.raw.write()` returns false, skip that event and log a warning. Do not buffer unboundedly.
- The aggregator cache uses a simple TTL map (`Map<string, { data: unknown; expiresAt: number }>`).
- All monitoring services are instantiated once in `createApp()` and passed to route registration.

## Files to Create

- `packages/api/src/routes/monitoring/index.ts`
- `packages/api/src/routes/monitoring/overview-routes.ts`
- `packages/api/src/routes/monitoring/metrics-routes.ts`
- `packages/api/src/routes/monitoring/sse-helpers.ts`
- `packages/api/src/services/monitoring/aggregator.ts`
- `packages/api/src/services/monitoring/metrics-collector.ts`
- `packages/api/src/services/monitoring/time-buckets.ts`
- `packages/api/src/routes/monitoring/__tests__/overview-routes.test.ts`
- `packages/api/src/routes/monitoring/__tests__/metrics-routes.test.ts`
- `packages/api/src/services/monitoring/__tests__/aggregator.test.ts`
- `packages/api/src/services/monitoring/__tests__/metrics-collector.test.ts`
- `packages/api/src/services/monitoring/__tests__/time-buckets.test.ts`
