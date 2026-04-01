---
title: "Task 1: Log Explorer API Routes & OpenSearch Proxy"
sidebar:
  order: 230
---

**Story:** 23-7-log-explorer
**Epic:** 23

## Task Description

Create backend API routes and services for the log explorer: OpenSearch query proxy with Lucene syntax, log streaming via SSE, log level distribution aggregation, error log grouping with context, saved searches per user, and log alert rules with evaluation.

## Acceptance Criteria

- `GET /api/monitoring/logs/search` proxies Lucene queries to OpenSearch with fallback to in-memory
- `GET /api/monitoring/logs/stream` SSE stream of new log entries with service/level filters
- `GET /api/monitoring/logs/distribution` returns log level distribution with time buckets
- `GET /api/monitoring/logs/errors` returns error-only logs with grouping by message hash
- `GET /api/monitoring/logs/errors/:hash/occurrences` returns individual occurrences
- `GET /api/monitoring/logs/errors/:hash/context` returns surrounding log entries
- `GET /api/monitoring/logs/opensearch-link` returns URL to OpenSearch Dashboards
- Full CRUD for saved searches and alert rules
- `GET /api/monitoring/logs/active-alerts` returns triggered alerts
- `POST /api/monitoring/logs/active-alerts/:id/acknowledge` acknowledges an alert

## Implementation Details

### Technical Requirements

- [ ] Create `packages/api/src/routes/monitoring/log-routes.ts`:
  ```typescript
  export function registerLogMonitoringRoutes(
    app: FastifyInstance,
    openSearchProxy: OpenSearchProxy,
    logStreamService: LogStreamService,
    logAlertEvaluator: LogAlertEvaluator,
    savedSearchStore: SavedSearchStore,
  ): void;
  ```

- [ ] Create `packages/api/src/services/monitoring/opensearch-proxy.ts`:
  ```typescript
  export interface LogSearchOptions {
    q: string;                     // Lucene query string
    services?: string[];
    levels?: string[];
    engineId?: string;
    issueNumber?: number;
    since?: string;                // ISO 8601
    until?: string;
    page?: number;
    pageSize?: number;
  }

  export interface LogSearchResult {
    hits: LogEntry[];
    total: number;
    took: number;                 // ms
    page: number;
    pageSize: number;
  }

  export interface LogEntry {
    id: string;
    timestamp: string;
    level: string;
    service: string;
    msg: string;
    fields: Record<string, unknown>;
    stackTrace: string | null;
  }

  export interface LogLevelDistribution {
    buckets: {
      timestamp: number;
      debug: number;
      info: number;
      warn: number;
      error: number;
    }[];
    totals: { debug: number; info: number; warn: number; error: number };
  }

  export interface ErrorGroup {
    hash: string;
    message: string;
    count: number;
    firstOccurrence: string;
    lastOccurrence: string;
    service: string;
    frequency: number;            // count per hour
  }

  export class OpenSearchProxy {
    constructor(deps: {
      opensearchUrl: string | null;
      inMemoryBuffer: LogEntry[];     // fallback
    });

    async search(options: LogSearchOptions): Promise<LogSearchResult>;
    async getDistribution(options: { since: string; until: string; bucketSize: string }): Promise<LogLevelDistribution>;
    async getErrors(options: { since: string; until: string; service?: string; limit?: number }): Promise<ErrorGroup[]>;
    async getErrorOccurrences(hash: string, limit?: number): Promise<LogEntry[]>;
    async getErrorContext(hash: string): Promise<{ before: LogEntry[]; after: LogEntry[] }>;
    getOpenSearchDashboardsLink(query: string): string | null;

    private async _queryOpenSearch(body: object): Promise<unknown>;
    private _searchInMemory(options: LogSearchOptions): LogSearchResult;
    isAvailable(): boolean;
  }
  ```
  - Primary: constructs OpenSearch query DSL from Lucene query using `query_string` query type
  - Index pattern: `tamma-logs-*` (one index per day)
  - Fallback: when OpenSearch is unhealthy, searches in-memory buffer with string matching
  - Error grouping: hash first 200 chars of error message to create group key
  - Error context: query 5 entries before and 5 after the error timestamp from same service

- [ ] Create `packages/api/src/services/monitoring/log-stream-service.ts`:
  ```typescript
  export class LogStreamService {
    private subscribers: Set<(entry: LogEntry) => void>;

    constructor();
    subscribe(callback: (entry: LogEntry) => void): () => void;  // returns unsubscribe fn
    publish(entry: LogEntry): void;  // called by Pino transport
  }
  ```
  - Pub/sub pattern: Pino transport calls `publish()`, SSE endpoints call `subscribe()`
  - Fan-out to all connected SSE clients
  - SSE endpoint filters by service/level based on query params

- [ ] Create `packages/api/src/services/monitoring/log-alert-evaluator.ts`:
  ```typescript
  export interface LogAlertRule {
    id: string;
    name: string;
    query: string;               // Lucene syntax
    threshold: number;           // count per window
    windowMinutes: number;       // 1, 5, 15, 60
    severity: 'info' | 'warning' | 'critical';
    enabled: boolean;
    channels: string[];          // AlertManager channel names
  }

  export interface ActiveLogAlert {
    id: string;
    ruleId: string;
    ruleName: string;
    triggerCount: number;
    lastTriggered: string;
    acknowledged: boolean;
  }

  export class LogAlertEvaluator {
    private rules: LogAlertRule[];
    private activeAlerts: ActiveLogAlert[];
    private timer: ReturnType<typeof setInterval> | null;

    constructor(deps: { openSearchProxy: OpenSearchProxy });
    start(): void;               // starts 30-second evaluation loop
    stop(): void;

    getRules(): LogAlertRule[];
    addRule(rule: Omit<LogAlertRule, 'id'>): LogAlertRule;
    updateRule(id: string, updates: Partial<LogAlertRule>): LogAlertRule;
    deleteRule(id: string): void;

    getActiveAlerts(): ActiveLogAlert[];
    acknowledgeAlert(id: string): void;
  }
  ```
  - Runs every 30 seconds: queries OpenSearch for each rule's query within its window
  - If count exceeds threshold, creates/updates active alert
  - Fires notification via existing AlertManager channels
  - Timer is `unref()`'d

- [ ] Create `packages/api/src/services/monitoring/saved-search-store.ts`:
  ```typescript
  export interface SavedSearch {
    id: string;
    userId: string;
    name: string;
    description: string | null;
    query: string;
    filters: Record<string, unknown>;
    pinned: boolean;
    createdAt: string;
    lastUsedAt: string | null;
  }

  export class SavedSearchStore {
    private searches: Map<string, SavedSearch>;

    constructor();
    getByUser(userId: string): SavedSearch[];
    create(userId: string, data: Omit<SavedSearch, 'id' | 'userId' | 'createdAt' | 'lastUsedAt'>): SavedSearch;
    update(id: string, userId: string, data: Partial<SavedSearch>): SavedSearch;
    delete(id: string, userId: string): void;
    markUsed(id: string): void;
  }
  ```

### Files to Create

- CREATE `packages/api/src/routes/monitoring/log-routes.ts`
- CREATE `packages/api/src/services/monitoring/opensearch-proxy.ts`
- CREATE `packages/api/src/services/monitoring/log-stream-service.ts`
- CREATE `packages/api/src/services/monitoring/log-alert-evaluator.ts`
- CREATE `packages/api/src/services/monitoring/saved-search-store.ts`
- CREATE `packages/api/src/routes/monitoring/__tests__/log-routes.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/opensearch-proxy.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/log-alert-evaluator.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/saved-search-store.test.ts`

### Files to Modify

- MODIFY `packages/api/src/routes/monitoring/index.ts` -- register log routes

### Dependencies

- Story 23-11: route registration, SSE helpers
- OpenSearch client (HTTP, existing or new)
- AlertManager from `@tamma/cost-monitor` for alert delivery
- Pino structured logging (existing)

## Testing Strategy

### Unit Tests

- [ ] OpenSearchProxy: constructs correct query DSL from Lucene query
- [ ] OpenSearchProxy: falls back to in-memory when OpenSearch unavailable
- [ ] OpenSearchProxy: in-memory search matches string content
- [ ] OpenSearchProxy: error grouping hashes correctly
- [ ] OpenSearchProxy: error context returns 5 before/after
- [ ] LogStreamService: subscribe/publish fan-out to all subscribers
- [ ] LogStreamService: unsubscribe removes callback
- [ ] LogAlertEvaluator: evaluates rules on schedule
- [ ] LogAlertEvaluator: creates alert when threshold exceeded
- [ ] LogAlertEvaluator: acknowledgeAlert marks alert as acknowledged
- [ ] SavedSearchStore: CRUD operations per user
- [ ] SavedSearchStore: cannot delete another user's saved search
- [ ] Log routes: search proxies to OpenSearchProxy
- [ ] Log routes: stream returns SSE headers

## Completion Checklist

- [ ] All 18 API endpoints implemented (search, stream, distribution, errors, saved searches, alerts)
- [ ] OpenSearch proxy with Lucene query passthrough
- [ ] In-memory fallback when OpenSearch unavailable
- [ ] Log streaming via pub/sub + SSE
- [ ] Alert evaluation every 30 seconds
- [ ] Saved searches per user
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
