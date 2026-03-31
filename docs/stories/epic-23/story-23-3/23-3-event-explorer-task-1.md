# Task 1: Event Store Query API & Export Service

**Story:** 23-3-event-store-explorer
**Epic:** 23

## Task Description

Create the backend API routes and services for the event store explorer: paginated event listing with filtering, single event detail with related events, frequency aggregation, export (JSON/CSV), and replay marking. Aggregates events across all engines in the EngineRegistry.

## Acceptance Criteria

- `GET /api/monitoring/events` returns paginated event list with filters (type, issue, engine, time, search, success/failure)
- `GET /api/monitoring/events/:id` returns single event detail
- `GET /api/monitoring/events/:id/related` returns previous and next events for the same issue
- `GET /api/monitoring/events/frequency` returns event count per time bucket
- `GET /api/monitoring/events/types` returns all event types with counts
- `GET /api/monitoring/events/export` streams export in JSON or CSV format
- `POST /api/monitoring/events/replay` marks a replay point (owner-only)
- Full-text search across event type, issue number, data fields, engine ID

## Implementation Details

### Technical Requirements

- [ ] Create `packages/api/src/routes/monitoring/event-routes.ts`:
  ```typescript
  export function registerEventMonitoringRoutes(
    app: FastifyInstance,
    eventQueryService: EventQueryService,
    eventExportService: EventExportService,
  ): void;
  ```
  - Paginated list: `GET /events?page=1&pageSize=25&type[]=ISSUE_SELECTED&issueNumber=42&engineId=...&since=...&until=...&search=...&successOnly=true`
  - Export: uses `Accept` header (`application/json` or `text/csv`) or `format` query param
  - Replay: requires owner role via `requirePermission('admin:manage')`

- [ ] Create `packages/api/src/services/monitoring/event-query-service.ts`:
  ```typescript
  export interface EventQueryOptions {
    page: number;
    pageSize: number;
    types?: string[];
    issueNumber?: number;
    engineId?: string;
    since?: number;
    until?: number;
    search?: string;
    successOnly?: boolean;
    failureOnly?: boolean;
  }

  export interface PaginatedEvents {
    events: EngineEvent[];
    total: number;
    page: number;
    pageSize: number;
    totalPages: number;
  }

  export interface EventFrequencyBucket {
    bucketStart: number;
    bucketEnd: number;
    counts: Record<string, number>;  // event type -> count
    total: number;
  }

  export class EventQueryService {
    constructor(deps: { engineRegistry: EngineRegistry });

    async queryEvents(options: EventQueryOptions): Promise<PaginatedEvents>;
    async getEventById(id: string): Promise<EngineEvent | null>;
    async getRelatedEvents(id: string): Promise<{ previous: EngineEvent[]; next: EngineEvent[] }>;
    async getEventFrequency(options: { since: number; until: number; bucketSize: string; types?: string[] }): Promise<EventFrequencyBucket[]>;
    async getEventTypes(): Promise<{ type: string; count: number }[]>;
    async markReplay(fromEventId: string, userId: string): Promise<void>;
  }
  ```
  - Aggregates events from all engines in EngineRegistry via `engine.getEventHistory()`
  - Search: for in-memory event store, `JSON.stringify(event).toLowerCase().includes(search.toLowerCase())`
  - Success events: PLAN_APPROVED, IMPLEMENTATION_COMPLETED, PR_MERGED, ISSUE_CLOSED
  - Failure events: IMPLEMENTATION_FAILED, ERROR_OCCURRED, PLAN_REJECTED
  - Related events: filter by same issueNumber, find prev/next by timestamp relative to target event
  - Frequency: uses `groupIntoBuckets` from `time-buckets.ts` (Story 23-11)
  - Replay: emits a new event `{ type: 'REPLAY_INITIATED', data: { fromEventId, initiatedBy: userId } }`

- [ ] Create `packages/api/src/services/monitoring/event-export-service.ts`:
  ```typescript
  export class EventExportService {
    constructor(deps: { eventQueryService: EventQueryService });

    async exportJSON(options: EventQueryOptions, stream: NodeJS.WritableStream): Promise<void>;
    async exportCSV(options: EventQueryOptions, stream: NodeJS.WritableStream): Promise<void>;
  }
  ```
  - JSON: writes `[` then each event JSON-stringified with commas, then `]`
  - CSV: writes header row, then one row per event with flattened data fields
  - Streaming approach: does not load all events into memory at once

### Files to Create

- CREATE `packages/api/src/routes/monitoring/event-routes.ts`
- CREATE `packages/api/src/services/monitoring/event-query-service.ts`
- CREATE `packages/api/src/services/monitoring/event-export-service.ts`
- CREATE `packages/api/src/routes/monitoring/__tests__/event-routes.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/event-query-service.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/event-export-service.test.ts`

### Files to Modify

- MODIFY `packages/api/src/routes/monitoring/index.ts` -- register event routes

### Dependencies

- Story 23-11: route registration, time-buckets utility
- EngineRegistry (existing) for accessing event stores across all engines
- IEventStore / EngineEvent types from `@tamma/shared`

## Testing Strategy

### Unit Tests

- [ ] EventQueryService: queryEvents paginates correctly
- [ ] EventQueryService: filters by type, issueNumber, engineId, time range
- [ ] EventQueryService: full-text search matches event data
- [ ] EventQueryService: successOnly/failureOnly filter correctly
- [ ] EventQueryService: getRelatedEvents returns prev/next for same issue
- [ ] EventQueryService: getEventFrequency groups into correct buckets
- [ ] EventQueryService: getEventTypes returns counts per type
- [ ] EventQueryService: markReplay emits REPLAY_INITIATED event
- [ ] EventExportService: exportJSON writes valid JSON array
- [ ] EventExportService: exportCSV writes correct header + data rows
- [ ] Event routes: pagination query params parsed correctly
- [ ] Event routes: export uses Accept header for format selection

## Completion Checklist

- [ ] All 7 API endpoints implemented
- [ ] Event aggregation across all engines
- [ ] Full-text search working
- [ ] Streaming export for JSON and CSV
- [ ] Replay marking (owner-only)
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
