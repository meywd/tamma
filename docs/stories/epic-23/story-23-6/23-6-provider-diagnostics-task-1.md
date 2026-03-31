# Task 1: Provider Diagnostics API Routes & Services

**Story:** 23-6-provider-diagnostics
**Epic:** 23

## Task Description

Create backend API routes and services for deep provider diagnostics: per-provider overview with health, latency histograms, error classification, token usage analytics, model availability matrix, provider comparison, rate limits, and API call log.

## Acceptance Criteria

- `GET /api/monitoring/providers/overview` returns per-provider summary
- `GET /api/monitoring/providers/:name/latency` returns latency histogram data
- `GET /api/monitoring/providers/:name/errors` returns error classification breakdown
- `GET /api/monitoring/providers/:name/errors/recent` returns last N errors
- `GET /api/monitoring/providers/:name/errors/trend` returns error count over time
- `GET /api/monitoring/providers/:name/tokens` returns token usage analytics
- `GET /api/monitoring/providers/:name/tokens/trend` returns daily token usage time series
- `GET /api/monitoring/providers/models` returns model availability matrix
- `POST /api/monitoring/providers/models/refresh` triggers live model check
- `GET /api/monitoring/providers/compare` returns comparison data for selected providers
- `GET /api/monitoring/providers/:name/rate-limits` returns rate limit status
- `GET /api/monitoring/providers/:name/rate-limits/history` returns historical rate limit hits
- `GET /api/monitoring/providers/calls` returns API call log

## Implementation Details

### Technical Requirements

- [ ] Create `packages/api/src/routes/monitoring/provider-routes.ts`:
  ```typescript
  export function registerProviderMonitoringRoutes(
    app: FastifyInstance,
    providerDiagService: ProviderDiagnosticsService,
    modelAvailService: ModelAvailabilityService,
  ): void;
  ```

- [ ] Create `packages/api/src/services/monitoring/provider-diagnostics-service.ts`:
  ```typescript
  export interface ProviderOverview {
    name: string;
    status: 'healthy' | 'degraded' | 'unhealthy';
    circuitState: 'closed' | 'open' | 'half-open';
    requestCountLastHour: number;
    errorCountLastHour: number;
    avgLatencyMs: number;
    totalCost24h: number;
    currentModel: string | null;
  }

  export interface LatencyBucket {
    rangeLabel: string;    // "0-50ms", "50-100ms", etc.
    rangeStart: number;
    rangeEnd: number;
    count: number;
  }

  export interface LatencyHistogramData {
    buckets: LatencyBucket[];
    p50: number;
    p95: number;
    p99: number;
  }

  export interface ErrorClassification {
    errorType: string;          // INVALID_API_KEY, RATE_LIMIT_EXCEEDED, TIMEOUT, etc.
    humanLabel: string;         // "Authentication Error", "Rate Limit", etc.
    count: number;
    percentage: number;
    lastOccurrence: string;
    sampleMessage: string;
    retryable: boolean;
  }

  export interface TokenUsageAnalytics {
    inputTokens: { today: number; week: number; month: number };
    outputTokens: { today: number; week: number; month: number };
    cacheReadTokens: { today: number; week: number; month: number };
    cacheWriteTokens: { today: number; week: number; month: number };
    avgInputPerRequest: number;
    avgOutputPerRequest: number;
    tokenEfficiency: number;    // output/input ratio
  }

  export interface ProviderComparisonData {
    providers: string[];
    costPerMillionInput: Record<string, number>;
    costPerMillionOutput: Record<string, number>;
    avgLatency: Record<string, number>;
    errorRate: Record<string, number>;
    successRate: Record<string, number>;
    tokenThroughput: Record<string, number>;  // tokens/second
  }

  export interface ApiCallLogEntry {
    timestamp: string;
    provider: string;
    model: string;
    durationMs: number;
    inputTokens: number;
    outputTokens: number;
    costUsd: number;
    success: boolean;
    traceId: string | null;
    issueId: string | null;
    agentType: string | null;
    taskType: string | null;
    finishReason: string | null;
    errorDetails: string | null;
  }

  export class ProviderDiagnosticsService {
    constructor(deps: {
      healthService: HealthService;
      diagnosticsService: DiagnosticsService;
      costTracker: ICostTracker | null;
      configService: ConfigService;
    });

    async getOverview(): Promise<ProviderOverview[]>;
    async getLatencyHistogram(provider: string, options?: { since?: number; until?: number }): Promise<LatencyHistogramData>;
    async getErrorClassification(provider: string, options?: { since?: number; until?: number }): Promise<ErrorClassification[]>;
    async getRecentErrors(provider: string, limit?: number): Promise<ApiCallLogEntry[]>;
    async getErrorTrend(provider: string, options?: { since?: number; until?: number }): Promise<{ timestamp: number; counts: Record<string, number> }[]>;
    async getTokenUsage(provider: string): Promise<TokenUsageAnalytics>;
    async getTokenTrend(provider: string, options?: { since?: number; until?: number }): Promise<{ date: string; input: number; output: number; cached: number; cost: number }[]>;
    async getComparisonData(providers: string[]): Promise<ProviderComparisonData>;
    async getRateLimits(provider: string): Promise<RateLimitStatus>;
    async getRateLimitHistory(provider: string): Promise<{ hour: string; count: number }[]>;
    async getCallLog(options?: { provider?: string; model?: string; success?: boolean; limit?: number; since?: number }): Promise<ApiCallLogEntry[]>;
  }
  ```
  - Latency histogram: buckets at 0-50ms, 50-100ms, 100-200ms, 200-500ms, 500ms-1s, 1s-2s, 2s-5s, 5s+
  - Error classification maps `ProviderError.code` to human-readable categories
  - Token usage from CostTracker usage records (already track input/output/cache tokens)
  - Call log from DiagnosticsService events of type `provider:complete` and `provider:error`
  - Comparison data computed by querying CostTracker for each provider

- [ ] Create `packages/api/src/services/monitoring/model-availability-service.ts`:
  ```typescript
  export interface ModelAvailabilityEntry {
    provider: string;
    model: string;
    available: boolean | null;    // null = unknown
    lastChecked: string | null;
  }

  export class ModelAvailabilityService {
    constructor(deps: { configService: ConfigService });
    async getMatrix(): Promise<ModelAvailabilityEntry[]>;
    async refreshMatrix(): Promise<ModelAvailabilityEntry[]>;
  }
  ```
  - Matrix: rows=providers, columns=model IDs
  - For providers implementing `getModels()`: call it
  - For CLI agents: report capabilities from `CLIAgentCapabilities`
  - Caches result for 5 minutes

### Files to Create

- CREATE `packages/api/src/routes/monitoring/provider-routes.ts`
- CREATE `packages/api/src/services/monitoring/provider-diagnostics-service.ts`
- CREATE `packages/api/src/services/monitoring/model-availability-service.ts`
- CREATE `packages/api/src/routes/monitoring/__tests__/provider-routes.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/provider-diagnostics-service.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/model-availability-service.test.ts`

### Files to Modify

- MODIFY `packages/api/src/routes/monitoring/index.ts` -- register provider routes

### Dependencies

- Story 23-11: route registration, time-buckets
- HealthService (existing), DiagnosticsService (existing), CostTracker (existing), ConfigService (existing)
- ProviderHealthTracker (existing)

## Testing Strategy

### Unit Tests

- [ ] ProviderDiagnosticsService: overview aggregates from multiple services
- [ ] ProviderDiagnosticsService: latency histogram bins data correctly
- [ ] ProviderDiagnosticsService: error classification maps codes to categories
- [ ] ProviderDiagnosticsService: token usage aggregates from cost tracker
- [ ] ProviderDiagnosticsService: comparison data for multiple providers
- [ ] ProviderDiagnosticsService: call log filters by provider, model, success
- [ ] ModelAvailabilityService: returns matrix with provider x model entries
- [ ] ModelAvailabilityService: caches result for 5 minutes
- [ ] Provider routes: all 13 endpoints return expected structures

## Completion Checklist

- [ ] All 13 API endpoints implemented
- [ ] Latency histogram with configurable buckets
- [ ] Error classification with human-readable labels
- [ ] Token analytics with daily/weekly/monthly
- [ ] Model availability matrix
- [ ] Provider comparison computation
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
