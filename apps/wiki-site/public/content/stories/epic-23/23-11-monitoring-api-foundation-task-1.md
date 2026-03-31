---
title: "Task 1: Monitoring Route Registration & SSE Helpers"
sidebar:
  order: 230
---

**Story:** 23-11-monitoring-api-foundation
**Epic:** 23

## Task Description

Create the Fastify plugin for monitoring route registration and the reusable SSE helper module. The route plugin registers all monitoring sub-routes under `/api/monitoring/*` with `settings:view` permission enforcement. The SSE helper provides `createSSEStream()` for any monitoring endpoint that needs real-time updates.

## Acceptance Criteria

- `registerMonitoringRoutes()` Fastify plugin created at `packages/api/src/routes/monitoring/index.ts`
- All routes under `/api/monitoring/*` require `settings:view` permission via the existing `requirePermission` hook
- SSE helper at `packages/api/src/routes/monitoring/sse-helpers.ts` provides `createSSEStream(reply, options)` returning `{ send, cleanup }`
- SSE heartbeat pings every 15 seconds to keep nginx proxied connections alive
- Automatic cleanup of timers and listeners on client disconnect
- Backpressure handling: if `reply.raw.write()` returns false, the event is dropped and a warning is logged
- Initial overview route `GET /api/monitoring/overview` is wired up
- Initial metrics routes `GET /api/monitoring/metrics` and `GET /api/monitoring/metrics/stream` are wired up

## Implementation Details

### Technical Requirements

- [ ] Create `packages/api/src/routes/monitoring/index.ts`:
  ```typescript
  import type { FastifyInstance } from 'fastify';
  import type { MonitoringServices } from './types.js';
  import { requirePermission } from '../../auth/require-permission.js';
  import { registerOverviewRoutes } from './overview-routes.js';
  import { registerMetricsRoutes } from './metrics-routes.js';

  export async function registerMonitoringRoutes(
    app: FastifyInstance,
    services: MonitoringServices,
  ): Promise<void> {
    await app.register(
      async (instance) => {
        // Enforce settings:view on all monitoring routes
        instance.addHook('onRequest', async (request, reply) => {
          await requirePermission('settings:view')(request, reply);
        });

        registerOverviewRoutes(instance, services);
        registerMetricsRoutes(instance, services);
        // Future stories add more route registrations here
      },
      { prefix: '/api/monitoring' },
    );
  }
  ```
- [ ] Create `packages/api/src/routes/monitoring/types.ts`:
  ```typescript
  import type { MonitoringAggregator } from '../../services/monitoring/aggregator.js';
  import type { MetricsCollector } from '../../services/monitoring/metrics-collector.js';

  export interface MonitoringServices {
    aggregator: MonitoringAggregator;
    metricsCollector: MetricsCollector;
  }
  ```
- [ ] Create `packages/api/src/routes/monitoring/sse-helpers.ts`:
  ```typescript
  import type { FastifyReply } from 'fastify';
  import type { Logger } from 'pino';

  export interface SSEStreamOptions {
    heartbeatIntervalMs?: number; // default 15000
    logger?: Logger;
  }

  export interface SSEStream {
    send: (event: string, data: unknown) => boolean;
    cleanup: () => void;
  }

  export function createSSEStream(
    reply: FastifyReply,
    options?: SSEStreamOptions,
  ): SSEStream;
  ```
  - Sets response headers: `Content-Type: text/event-stream`, `Cache-Control: no-cache`, `Connection: keep-alive`, `X-Accel-Buffering: no` (for nginx)
  - Returns `send(event, data)`: writes `event: ${event}\ndata: ${JSON.stringify(data)}\n\n` to `reply.raw`
  - `send()` returns `false` if `reply.raw.write()` returns false (backpressure); logs warning via optional logger
  - Starts a heartbeat timer: `setInterval(() => reply.raw.write(': heartbeat\n\n'), heartbeatIntervalMs)`
  - Listens to `reply.raw.on('close', ...)` to call `cleanup()` automatically
  - `cleanup()` clears the heartbeat interval and removes the close listener
  - Heartbeat timer is `unref()`'d so it does not prevent process exit

- [ ] Create `packages/api/src/routes/monitoring/overview-routes.ts`:
  ```typescript
  import type { FastifyInstance } from 'fastify';
  import type { MonitoringServices } from './types.js';

  export function registerOverviewRoutes(
    app: FastifyInstance,
    services: MonitoringServices,
  ): void {
    app.get('/overview', async (_request, reply) => {
      const overview = await services.aggregator.getSystemOverview();
      return reply.send(overview);
    });
  }
  ```

- [ ] Create `packages/api/src/routes/monitoring/metrics-routes.ts`:
  ```typescript
  import type { FastifyInstance } from 'fastify';
  import type { MonitoringServices } from './types.js';
  import { createSSEStream } from './sse-helpers.js';

  export function registerMetricsRoutes(
    app: FastifyInstance,
    services: MonitoringServices,
  ): void {
    // GET /metrics -- snapshot of request metrics
    app.get('/metrics', async (request, reply) => {
      const query = request.query as { since?: string };
      const sinceMs = query.since ? parseInt(query.since, 10) : undefined;
      const metrics = services.metricsCollector.getRequestMetrics(sinceMs);
      return reply.send(metrics);
    });

    // GET /metrics/stream -- SSE stream of metrics every 5s
    app.get('/metrics/stream', async (request, reply) => {
      const sse = createSSEStream(reply, { logger: request.log });
      const interval = setInterval(() => {
        const metrics = services.metricsCollector.getRequestMetrics();
        sse.send('metrics', metrics);
      }, 5000);

      reply.raw.on('close', () => {
        clearInterval(interval);
        sse.cleanup();
      });
    });
  }
  ```

### Files to Create

- CREATE `packages/api/src/routes/monitoring/index.ts`
- CREATE `packages/api/src/routes/monitoring/types.ts`
- CREATE `packages/api/src/routes/monitoring/sse-helpers.ts`
- CREATE `packages/api/src/routes/monitoring/overview-routes.ts`
- CREATE `packages/api/src/routes/monitoring/metrics-routes.ts`
- CREATE `packages/api/src/routes/monitoring/__tests__/sse-helpers.test.ts`
- CREATE `packages/api/src/routes/monitoring/__tests__/overview-routes.test.ts`
- CREATE `packages/api/src/routes/monitoring/__tests__/metrics-routes.test.ts`

### Files to Modify

- MODIFY `packages/api/src/create-app.ts` (or equivalent) -- call `registerMonitoringRoutes(app, monitoringServices)` during app initialization

### Dependencies

- `requirePermission` from `packages/api/src/auth/require-permission.ts` (existing)
- `FastifyInstance`, `FastifyReply` from `fastify` (existing dependency)
- `pino` Logger type (existing dependency)
- Task 2: `MonitoringAggregator` and `MetricsCollector` must exist for routes to work

## Testing Strategy

### Unit Tests -- sse-helpers.test.ts

- [ ] Test `createSSEStream()` sets correct response headers (Content-Type, Cache-Control, Connection, X-Accel-Buffering)
- [ ] Test `send()` writes correctly formatted SSE event to reply.raw
- [ ] Test `send()` serializes data as JSON
- [ ] Test `send()` returns false when `reply.raw.write()` returns false (backpressure)
- [ ] Test `send()` logs warning via logger when backpressure detected
- [ ] Test heartbeat pings at configured interval
- [ ] Test heartbeat uses `: heartbeat\n\n` comment format
- [ ] Test cleanup clears heartbeat interval
- [ ] Test automatic cleanup on client disconnect (reply.raw 'close' event)
- [ ] Test default heartbeat interval is 15000ms

### Unit Tests -- overview-routes.test.ts

- [ ] Test GET /api/monitoring/overview calls aggregator.getSystemOverview()
- [ ] Test response contains expected overview structure
- [ ] Test permission enforcement (settings:view required)

### Unit Tests -- metrics-routes.test.ts

- [ ] Test GET /api/monitoring/metrics returns metrics snapshot
- [ ] Test GET /api/monitoring/metrics accepts optional `since` query param
- [ ] Test GET /api/monitoring/metrics/stream returns SSE stream
- [ ] Test metrics stream emits data every 5 seconds
- [ ] Test permission enforcement on both endpoints

## Completion Checklist

- [ ] `index.ts` route plugin created with permission enforcement
- [ ] `types.ts` MonitoringServices interface defined
- [ ] `sse-helpers.ts` SSE helper with heartbeat, backpressure, cleanup
- [ ] `overview-routes.ts` overview endpoint wired
- [ ] `metrics-routes.ts` metrics + SSE stream endpoints wired
- [ ] All unit tests written and passing
- [ ] TypeScript strict mode compiles without errors
- [ ] Route registration integrated into app initialization
