---
title: "Task 2: MonitoringAggregator Service & Time Buckets Utility"
sidebar:
  order: 230
---

**Story:** 23-11-monitoring-api-foundation
**Epic:** 23

## Task Description

Create the `MonitoringAggregator` service that combines data from multiple existing services into a unified system overview snapshot, with a 5-second TTL cache to prevent thundering herd on dashboard load. Also create the `time-buckets.ts` utility for grouping numeric data into fixed-width time buckets with statistical aggregation.

## Acceptance Criteria

- `MonitoringAggregator` at `packages/api/src/services/monitoring/aggregator.ts` provides `getSystemOverview()` returning a combined snapshot
- The aggregator accepts references to EngineRegistry, HealthService, DiagnosticsService, ConfigService, IWorkflowStore, ICostTracker
- Results are cached with a 5-second TTL using a simple TTL map
- `time-buckets.ts` at `packages/api/src/services/monitoring/time-buckets.ts` groups `{ timestamp, value }` data into buckets
- Bucket widths supported: 1min, 5min, 1hr, 1day
- Per-bucket stats: count, sum, min, max, avg, p50, p95, p99

## Implementation Details

### Technical Requirements

- [ ] Create `packages/api/src/services/monitoring/aggregator.ts`:
  ```typescript
  import type { EngineRegistry } from '../../engine-registry.js';
  import type { HealthService } from '../settings/HealthService.js';
  import type { DiagnosticsService } from '../settings/DiagnosticsService.js';
  import type { ConfigService } from '../settings/ConfigService.js';
  import type { IWorkflowStore } from '../../persistence/workflow-store.js';
  import type { ICostTracker } from '@tamma/cost-monitor';

  export interface SystemOverview {
    timestamp: string;                        // ISO 8601
    engines: {
      total: number;
      active: number;                          // not IDLE
      idle: number;
      error: number;
    };
    providerHealth: Record<string, {
      status: 'healthy' | 'degraded' | 'unhealthy';
      circuitState: 'closed' | 'open' | 'half-open';
    }>;
    workflows: {
      definitionCount: number;
      runningInstanceCount: number;
    };
    cost: {
      todayUsd: number;
      budgetLimitUsd: number | null;
      budgetUsedPercent: number | null;
    };
    diagnostics: {
      recentErrorCount: number;                // errors in last hour
      totalEventCount: number;
    };
  }

  export interface AggregatorDependencies {
    engineRegistry: EngineRegistry;
    healthService: HealthService;
    diagnosticsService: DiagnosticsService;
    configService: ConfigService;
    workflowStore: IWorkflowStore | null;
    costTracker: ICostTracker | null;
  }

  export class MonitoringAggregator {
    private deps: AggregatorDependencies;
    private cache: Map<string, { data: unknown; expiresAt: number }>;
    private readonly cacheTtlMs: number;

    constructor(deps: AggregatorDependencies, cacheTtlMs?: number);
    async getSystemOverview(): Promise<SystemOverview>;
    private async _fetchOverview(): Promise<SystemOverview>;
    clearCache(): void;
  }
  ```
  - `getSystemOverview()` checks cache first; if expired or missing, calls `_fetchOverview()` and stores result
  - `_fetchOverview()` calls all dependencies in parallel using `Promise.allSettled()` to be resilient
  - Each dependency failure results in safe defaults (empty objects, zero counts) rather than crashing

- [ ] Create `packages/api/src/services/monitoring/time-buckets.ts`:
  ```typescript
  export type BucketWidth = '1min' | '5min' | '1hr' | '1day';

  export interface TimeBucketEntry {
    timestamp: number;  // epoch ms
    value: number;
  }

  export interface TimeBucket {
    bucketStart: number;      // epoch ms, start of bucket
    bucketEnd: number;        // epoch ms, end of bucket
    count: number;
    sum: number;
    min: number;
    max: number;
    avg: number;
    p50: number;
    p95: number;
    p99: number;
  }

  export function groupIntoBuckets(
    entries: TimeBucketEntry[],
    width: BucketWidth,
    options?: {
      since?: number;   // epoch ms, start of range
      until?: number;   // epoch ms, end of range
    },
  ): TimeBucket[];

  export function bucketWidthMs(width: BucketWidth): number;
  ```
  - `bucketWidthMs`: `1min` = 60000, `5min` = 300000, `1hr` = 3600000, `1day` = 86400000
  - `groupIntoBuckets()`:
    1. Sort entries by timestamp
    2. Determine range from `options.since`/`options.until` or from data min/max
    3. Create empty buckets across the range
    4. Assign each entry to its bucket via `Math.floor((timestamp - rangeStart) / widthMs)`
    5. Compute stats per bucket: count, sum, min, max, avg
    6. Compute percentiles using sorted values: p50 = value at index `floor(0.5 * count)`, p95 = `floor(0.95 * count)`, p99 = `floor(0.99 * count)`
    7. Empty buckets have all stats as 0

### Files to Create

- CREATE `packages/api/src/services/monitoring/aggregator.ts`
- CREATE `packages/api/src/services/monitoring/time-buckets.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/aggregator.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/time-buckets.test.ts`

### Dependencies

- `EngineRegistry` from `packages/api/src/engine-registry.ts` (existing)
- `HealthService` from `packages/api/src/services/settings/HealthService.ts` (existing)
- `DiagnosticsService` from `packages/api/src/services/settings/DiagnosticsService.ts` (existing)
- `ConfigService` from `packages/api/src/services/settings/ConfigService.ts` (existing)
- `IWorkflowStore` from `packages/api/src/persistence/workflow-store.ts` (existing)
- `ICostTracker` from `packages/cost-monitor/src/cost-tracker.ts` (existing)

## Testing Strategy

### Unit Tests -- aggregator.test.ts

- [ ] Test `getSystemOverview()` returns correct structure with all fields
- [ ] Test cache: second call within 5s returns cached result (dependencies not called again)
- [ ] Test cache: call after 5s fetches fresh data
- [ ] Test `clearCache()` forces next call to fetch fresh data
- [ ] Test resilience: if EngineRegistry throws, overview still returns with safe defaults
- [ ] Test resilience: if HealthService throws, providerHealth is empty object
- [ ] Test resilience: if CostTracker is null, cost fields are null/zero
- [ ] Test resilience: if WorkflowStore is null, workflow counts are zero
- [ ] Test engines counts: active, idle, error correctly partitioned
- [ ] Test provider health maps correctly from HealthService status
- [ ] Test diagnostics recentErrorCount filters events from last hour

### Unit Tests -- time-buckets.test.ts

- [ ] Test `bucketWidthMs()` returns correct ms for each width
- [ ] Test `groupIntoBuckets()` with 1min width produces correct bucket boundaries
- [ ] Test entries are assigned to correct buckets
- [ ] Test count, sum, min, max, avg computed correctly
- [ ] Test p50, p95, p99 percentiles computed correctly for known data
- [ ] Test empty input returns empty array
- [ ] Test single entry produces a single bucket
- [ ] Test `since` and `until` options constrain the bucket range
- [ ] Test entries outside the range are excluded
- [ ] Test empty buckets (no entries in time window) have all stats as 0
- [ ] Test 5min, 1hr, 1day widths produce correct bucket sizes
- [ ] Test unsorted input is handled correctly (sorted internally)

## Completion Checklist

- [ ] `aggregator.ts` created with cache and parallel fetching
- [ ] `time-buckets.ts` created with percentile computation
- [ ] All dependencies are injected, not imported directly
- [ ] Cache uses simple TTL map pattern
- [ ] `Promise.allSettled()` used for resilient parallel fetching
- [ ] All unit tests written and passing
- [ ] TypeScript strict mode compiles without errors
