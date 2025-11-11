# Story 4.7: Event Query API for Time-Travel

Status: ready-for-dev

## Story

As a **developer**,
I want to query events by time range and filters to reconstruct system state at any point,
so that I can debug issues by replaying what system did in the past.

## Acceptance Criteria

1. API endpoint: `GET /api/v1/events?since={timestamp}&until={timestamp}&type={type}&correlationId={id}`
2. API returns events in chronological order with pagination support (default 100 events per page)
3. API supports filtering by: event type, actor, correlation ID, issue number
4. API supports projection queries: "What was state of PR #123 at timestamp T?"
5. API includes efficient indexing for fast queries (query completes in <1 second for 1M events)
6. API requires authentication (prevent unauthorized event access)
7. API documentation includes usage examples and query patterns

## Tasks / Subtasks

- [ ] Task 1: Design event query API interface (AC: 1, 2, 3)
  - [ ] Subtask 1.1: Define query parameters and response format
  - [ ] Subtask 1.2: Design pagination strategy and controls
  - [ ] Subtask 1.3: Create filtering parameter specifications
  - [ ] Subtask 1.4: Design sorting and ordering options
  - [ ] Subtask 1.5: Add query validation and error handling

- [ ] Task 2: Implement projection query system (AC: 4)
  - [ ] Subtask 2.1: Design projection query syntax and semantics
  - [ ] Subtask 2.2: Implement state reconstruction algorithms
  - [ ] Subtask 2.3: Create projection query engine
  - [ ] Subtask 2.4: Add projection caching and optimization
  - [ ] Subtask 2.5: Create projection query validation

- [ ] Task 3: Implement efficient indexing (AC: 5)
  - [ ] Subtask 3.1: Design database indexes for common query patterns
  - [ ] Subtask 3.2: Implement query optimization strategies
  - [ ] Subtask 3.3: Add query performance monitoring
  - [ ] Subtask 3.4: Create index maintenance and updates
  - [ ] Subtask 3.5: Add query performance testing

- [ ] Task 4: Implement authentication and authorization (AC: 6)
  - [ ] Subtask 4.1: Design event access control policies
  - [ ] Subtask 4.2: Implement JWT token authentication
  - [ ] Subtask 4.3: Add role-based access control
  - [ ] Subtask 4.4: Create API key authentication
  - [ ] Subtask 4.5: Add authentication middleware and validation

- [ ] Task 5: Create API documentation (AC: 7)
  - [ ] Subtask 5.1: Document all endpoints and parameters
  - [ ] Subtask 5.2: Create usage examples and tutorials
  - [ ] Subtask 5.3: Add query pattern documentation
  - [ ] Subtask 5.4: Create OpenAPI/Swagger specification
  - [ ] Subtask 5.5: Add interactive API documentation

- [ ] Task 6: Implement API performance and monitoring (AC: all)
  - [ ] Subtask 6.1: Add request/response logging
  - [ ] Subtask 6.2: Implement rate limiting and throttling
  - [ ] Subtask 6.3: Create API health checks and metrics
  - [ ] Subtask 6.4: Add query performance analytics
  - [ ] Subtask 6.5: Create API monitoring dashboard

## Dev Notes

### Requirements Context Summary

**Epic 4 Integration:** This story provides the query interface for the event store, enabling time-travel debugging and state reconstruction. The API is essential for debugging autonomous loops and understanding system behavior.

**Debugging Requirements:** The API must support complex queries to reconstruct system state at any point in time. This enables developers to understand why autonomous systems made specific decisions and debug issues by replaying past events.

**Performance Requirements:** The API must handle large event volumes efficiently with sub-second query performance even with millions of events. This requires proper indexing and query optimization.

### Implementation Guidance

**API Interface Design:**

```typescript
// Query parameters interface
interface EventQueryParams {
  // Time range filtering
  since?: string; // ISO 8601 timestamp (inclusive)
  until?: string; // ISO 8601 timestamp (exclusive)

  // Event filtering
  type?: string; // Event type or pattern (supports wildcards)
  actorType?: 'user' | 'system' | 'ai' | 'service';
  actorId?: string; // Actor ID (supports wildcards)
  correlationId?: string; // Workflow correlation ID
  causationId?: string; // Causation chain ID

  // Content filtering
  issueId?: string; // Related issue ID
  prId?: string; // Related PR number
  projectId?: string; // Project ID
  repository?: string; // Repository name

  // Metadata filtering
  source?: string; // Event source (orchestrator, worker, api, cli)
  version?: string; // Tamma version
  environment?: string; // Environment (dev, staging, prod)

  // JSONB tag filtering
  tags?: Record<string, string>; // Key-value tag filters

  // Pagination and sorting
  limit?: number; // Max events per page (default: 100, max: 1000)
  offset?: number; // Pagination offset
  orderBy?: 'timestamp' | 'eventId';
  orderDirection?: 'asc' | 'desc';

  // Projection queries
  projection?: string; // Projection query syntax
  includePayload?: boolean; // Include full payload (default: true)
  includeMetadata?: boolean; // Include metadata (default: true)
}

// Query response interface
interface EventQueryResponse {
  events: Array<{
    eventId: string;
    timestamp: string;
    eventType: string;
    actorType: string;
    actorId: string;
    correlationId: string;
    causationId?: string;
    schemaVersion: string;
    payload?: unknown; // Included if includePayload=true
    metadata?: unknown; // Included if includeMetadata=true
  }>;

  pagination: {
    total: number; // Total events matching query
    limit: number; // Events per page
    offset: number; // Current offset
    hasMore: boolean; // Whether more events available
    nextOffset?: number; // Offset for next page
  };

  query: {
    executionTime: number; // Query execution time (ms)
    indexUsed: string[]; // Indexes used for query
    cacheHit: boolean; // Whether result was cached
  };
}

// Projection query interface
interface ProjectionQuery {
  type: 'state' | 'aggregate' | 'timeline' | 'diff';
  target: {
    type: 'issue' | 'pr' | 'workflow' | 'system';
    id: string; // Target ID (issue number, PR number, etc.)
  };
  at?: string; // Point in time (ISO 8601)
  since?: string; // Time range start
  until?: string; // Time range end

  // State reconstruction options
  include?: string[]; // Event types to include
  exclude?: string[]; // Event types to exclude
  snapshot?: boolean; // Use snapshots if available

  // Aggregation options
  aggregate?: {
    by: 'hour' | 'day' | 'week' | 'month';
    metrics: string[]; // Metrics to calculate
    groupBy?: string[]; // Grouping fields
  };

  // Timeline options
  timeline?: {
    granularity: 'event' | 'minute' | 'hour' | 'day';
    filters?: Record<string, unknown>;
  };

  // Diff options
  diff?: {
    from: string; // Starting point in time
    to: string; // Ending point in time
    fields?: string[]; // Fields to compare
  };
}
```

**API Implementation:**

```typescript
@Controller('/api/v1/events')
@UseGuards(AuthGuard)
export class EventQueryController {
  constructor(
    private eventQueryService: EventQueryService,
    private projectionService: ProjectionService,
    private rateLimiter: RateLimiter
  ) {}

  @Get()
  @UseInterceptors(CacheInterceptor)
  async queryEvents(
    @Query() params: EventQueryParams,
    @Headers('authorization') authHeader: string,
    @Req() request: Request
  ): Promise<EventQueryResponse> {
    // Rate limiting
    await this.rateLimiter.checkLimit(request.ip, 'event-query');

    // Validate parameters
    const validatedParams = await this.validateQueryParams(params);

    // Execute query
    const startTime = Date.now();
    const result = await this.eventQueryService.queryEvents(validatedParams);
    const executionTime = Date.now() - startTime;

    // Log query for monitoring
    this.logQuery(validatedParams, executionTime, result.events.length);

    return {
      ...result,
      query: {
        executionTime,
        indexUsed: result.indexesUsed,
        cacheHit: result.cacheHit,
      },
    };
  }

  @Get('/projection')
  async queryProjection(
    @Query() query: ProjectionQuery,
    @Headers('authorization') authHeader: string
  ): Promise<ProjectionResponse> {
    // Validate projection query
    const validatedQuery = await this.validateProjectionQuery(query);

    // Execute projection
    const result = await this.projectionService.executeProjection(validatedQuery);

    return result;
  }

  @Get('/stream')
  @Header('Content-Type', 'text/event-stream')
  async streamEvents(
    @Query() params: EventQueryParams,
    @Headers('authorization') authHeader: string
  ): Promise<Observable<Event>> {
    // Validate parameters
    const validatedParams = await this.validateQueryParams(params);

    // Create event stream
    return this.eventQueryService.streamEvents(validatedParams);
  }

  private async validateQueryParams(params: EventQueryParams): Promise<EventQueryParams> {
    const schema = Joi.object({
      since: Joi.date().iso().optional(),
      until: Joi.date().iso().optional(),
      type: Joi.string().optional(),
      actorType: Joi.string().valid('user', 'system', 'ai', 'service').optional(),
      actorId: Joi.string().optional(),
      correlationId: Joi.string().uuid().optional(),
      causationId: Joi.string().uuid().optional(),
      issueId: Joi.string().optional(),
      prId: Joi.number().integer().positive().optional(),
      projectId: Joi.string().optional(),
      repository: Joi.string().optional(),
      source: Joi.string().optional(),
      version: Joi.string().optional(),
      environment: Joi.string().optional(),
      tags: Joi.object().optional(),
      limit: Joi.number().integer().min(1).max(1000).default(100),
      offset: Joi.number().integer().min(0).default(0),
      orderBy: Joi.string().valid('timestamp', 'eventId').default('timestamp'),
      orderDirection: Joi.string().valid('asc', 'desc').default('desc'),
      projection: Joi.string().optional(),
      includePayload: Joi.boolean().default(true),
      includeMetadata: Joi.boolean().default(true),
    });

    const { error, value } = schema.validate(params);
    if (error) {
      throw new BadRequestException(`Invalid query parameters: ${error.message}`);
    }

    return value;
  }
}
```

**Event Query Service:**

```typescript
@Injectable()
export class EventQueryService {
  constructor(
    private eventStore: IEventStore,
    private cacheService: CacheService,
    private metricsService: MetricsService
  ) {}

  async queryEvents(params: EventQueryParams): Promise<EventQueryResult> {
    const cacheKey = this.generateCacheKey(params);

    // Check cache first
    const cached = await this.cacheService.get(cacheKey);
    if (cached) {
      this.metricsService.recordCacheHit('event-query');
      return { ...cached, cacheHit: true, indexesUsed: [] };
    }

    // Build query
    const query = this.buildQuery(params);

    // Execute query with appropriate strategy
    let result: EventQueryResult;
    if (params.projection) {
      result = await this.executeProjectionQuery(params);
    } else {
      result = await this.executeStandardQuery(query, params);
    }

    // Cache result
    await this.cacheService.set(cacheKey, result, { ttl: 300 }); // 5 minutes

    this.metricsService.recordCacheMiss('event-query');
    return { ...result, cacheHit: false, indexesUsed: query.indexesUsed };
  }

  private buildQuery(params: EventQueryParams): QueryBuilder {
    const builder = new QueryBuilder()
      .select([
        'event_id',
        'timestamp',
        'event_type',
        'actor_type',
        'actor_id',
        'correlation_id',
        'causation_id',
        'schema_version',
        ...(params.includePayload ? ['payload'] : []),
        ...(params.includeMetadata ? ['metadata'] : []),
      ])
      .from('events');

    // Time range filtering
    if (params.since) {
      builder.where('timestamp', '>=', params.since);
    }
    if (params.until) {
      builder.where('timestamp', '<', params.until);
    }

    // Event type filtering (supports wildcards)
    if (params.type) {
      if (params.type.includes('*')) {
        builder.where('event_type', 'LIKE', params.type.replace('*', '%'));
      } else {
        builder.where('event_type', '=', params.type);
      }
    }

    // Actor filtering
    if (params.actorType) {
      builder.where('actor_type', '=', params.actorType);
    }
    if (params.actorId) {
      if (params.actorId.includes('*')) {
        builder.where('actor_id', 'LIKE', params.actorId.replace('*', '%'));
      } else {
        builder.where('actor_id', '=', params.actorId);
      }
    }

    // Correlation filtering
    if (params.correlationId) {
      builder.where('correlation_id', '=', params.correlationId);
    }
    if (params.causationId) {
      builder.where('causation_id', '=', params.causationId);
    }

    // JSONB content filtering
    if (params.issueId) {
      builder.where("payload->>'issueId'", '=', params.issueId);
    }
    if (params.prId) {
      builder.where("payload->>'prId'", '=', params.prId.toString());
    }

    // Metadata filtering
    if (params.source) {
      builder.where("metadata->>'source'", '=', params.source);
    }
    if (params.version) {
      builder.where("metadata->>'version'", '=', params.version);
    }
    if (params.environment) {
      builder.where("metadata->>'environment'", '=', params.environment);
    }

    // JSONB tag filtering
    if (params.tags) {
      Object.entries(params.tags).forEach(([key, value]) => {
        builder.where(`metadata->'tags'->>'${key}'`, '=', value);
      });
    }

    // Ordering and pagination
    builder.orderBy(params.orderBy || 'timestamp', params.orderDirection || 'desc');
    builder.limit(params.limit || 100);
    builder.offset(params.offset || 0);

    return builder;
  }

  async executeStandardQuery(
    query: QueryBuilder,
    params: EventQueryParams
  ): Promise<EventQueryResult> {
    const startTime = Date.now();

    // Execute query
    const events = await this.eventStore.query(query.build());

    // Get total count for pagination
    const countQuery = query
      .clone()
      .select('COUNT(*) as total')
      .clearOrder()
      .clearLimit()
      .clearOffset();
    const countResult = await this.eventStore.query(countQuery.build());
    const total = countResult[0].total;

    const executionTime = Date.now() - startTime;

    return {
      events: events.map(this.formatEvent),
      pagination: {
        total,
        limit: params.limit || 100,
        offset: params.offset || 0,
        hasMore: (params.offset || 0) + (params.limit || 100) < total,
        nextOffset: (params.offset || 0) + (params.limit || 100),
      },
      indexesUsed: query.getIndexesUsed(),
      cacheHit: false,
    };
  }

  async streamEvents(params: EventQueryParams): Observable<Event> {
    const query = this.buildQuery(params);

    return new Observable((subscriber) => {
      const stream = this.eventStore.streamQuery(query.build());

      stream.on('data', (event) => {
        subscriber.next(this.formatEvent(event));
      });

      stream.on('error', (error) => {
        subscriber.error(error);
      });

      stream.on('end', () => {
        subscriber.complete();
      });

      return () => {
        stream.destroy();
      };
    });
  }

  private formatEvent(event: any): Event {
    return {
      eventId: event.event_id,
      timestamp: event.timestamp,
      eventType: event.event_type,
      actorType: event.actor_type,
      actorId: event.actor_id,
      correlationId: event.correlation_id,
      causationId: event.causation_id,
      schemaVersion: event.schema_version,
      payload: event.payload,
      metadata: event.metadata,
    };
  }
}
```

**Projection Service:**

```typescript
@Injectable()
export class ProjectionService {
  constructor(
    private eventStore: IEventStore,
    private stateReconstructor: StateReconstructor
  ) {}

  async executeProjection(query: ProjectionQuery): Promise<ProjectionResponse> {
    switch (query.type) {
      case 'state':
        return this.executeStateProjection(query);
      case 'aggregate':
        return this.executeAggregateProjection(query);
      case 'timeline':
        return this.executeTimelineProjection(query);
      case 'diff':
        return this.executeDiffProjection(query);
      default:
        throw new BadRequestException(`Unknown projection type: ${query.type}`);
    }
  }

  private async executeStateProjection(query: ProjectionQuery): Promise<StateProjectionResponse> {
    // Get events up to the specified point in time
    const events = await this.eventStore.getEventsUntil(query.at || new Date().toISOString(), {
      correlationId: query.target.id,
      include: query.include,
      exclude: query.exclude,
    });

    // Reconstruct state
    const state = await this.stateReconstructor.reconstructState(events, query.target);

    return {
      type: 'state',
      target: query.target,
      at: query.at,
      state,
      eventCount: events.length,
      reconstructionTime: Date.now(),
    };
  }

  private async executeAggregateProjection(
    query: ProjectionQuery
  ): Promise<AggregateProjectionResponse> {
    const { by, metrics, groupBy } = query.aggregate!;

    // Build aggregation query
    const aggregationQuery = this.buildAggregationQuery(query, by, metrics, groupBy);

    // Execute aggregation
    const results = await this.eventStore.aggregate(aggregationQuery);

    return {
      type: 'aggregate',
      target: query.target,
      since: query.since,
      until: query.until,
      aggregation: {
        by,
        metrics,
        groupBy,
        results,
      },
      executionTime: Date.now(),
    };
  }

  private async executeTimelineProjection(
    query: ProjectionQuery
  ): Promise<TimelineProjectionResponse> {
    const { granularity, filters } = query.timeline!;

    // Get events with time bucketing
    const events = await this.eventStore.getTimelineEvents(query.since, query.until, {
      granularity,
      filters,
      targetId: query.target.id,
    });

    return {
      type: 'timeline',
      target: query.target,
      since: query.since,
      until: query.until,
      timeline: {
        granularity,
        events,
        buckets: this.bucketEventsByTime(events, granularity),
      },
      executionTime: Date.now(),
    };
  }

  private async executeDiffProjection(query: ProjectionQuery): Promise<DiffProjectionResponse> {
    const { from, to, fields } = query.diff!;

    // Get state at both points in time
    const fromState = await this.executeStateProjection({
      ...query,
      type: 'state',
      at: from,
    });

    const toState = await this.executeStateProjection({
      ...query,
      type: 'state',
      at: to,
    });

    // Calculate differences
    const differences = this.calculateDifferences(fromState.state, toState.state, fields);

    return {
      type: 'diff',
      target: query.target,
      from,
      to,
      differences,
      fields: fields || 'all',
      executionTime: Date.now(),
    };
  }
}
```

### Technical Specifications

**Performance Requirements:**

- Query latency: <1 second for 1M events
- Pagination: <100ms per page
- Projection queries: <5 seconds for complex reconstructions
- Concurrent queries: Support 100+ concurrent requests

**Security Requirements:**

- Authentication: JWT tokens and API keys
- Authorization: Role-based access control
- Data privacy: Sensitive event data filtered by role
- Audit logging: All query attempts logged

**Caching Requirements:**

- Query result caching: 5-minute TTL
- Projection caching: 1-hour TTL for complex projections
- Cache invalidation: Event-based invalidation
- Cache performance: >90% hit rate for common queries

**API Requirements:**

- RESTful design: Standard HTTP methods and status codes
- OpenAPI specification: Complete API documentation
- Rate limiting: Prevent abuse and ensure fair usage
- Error handling: Comprehensive error responses

### Dependencies

**Internal Dependencies:**

- Story 4.1: Event schema design (provides event structures)
- Story 4.2: Event store backend selection (provides storage)
- Story 4.3-4.6: Event capture stories (provides event data)

**External Dependencies:**

- Authentication service (JWT validation)
- Caching service (Redis or in-memory)
- Metrics collection service
- API documentation service

### Risks and Mitigations

| Risk                          | Severity | Mitigation                                   |
| ----------------------------- | -------- | -------------------------------------------- |
| Query performance degradation | High     | Proper indexing, query optimization, caching |
| Unauthorized data access      | High     | Strong authentication, role-based access     |
| Cache invalidation issues     | Medium   | Event-driven cache invalidation              |
| Complex projection queries    | Medium   | Query complexity limits, timeout handling    |

### Success Metrics

- [ ] Query performance: <1 second for 95% of queries
- [ ] API availability: >99.9% uptime
- [ ] Cache hit rate: >90% for common queries
- [ ] Authentication success rate: >99%
- [ ] Documentation completeness: 100% API coverage

## Related

- Related story: `docs/stories/4-1-event-schema-design.md`
- Related story: `docs/stories/4-2-event-store-backend-selection.md`
- Related story: `docs/stories/4-8-black-box-replay-for-debugging.md`
- Technical specification: `docs/tech-spec-epic-4.md`
- API documentation: `docs/5-9c-api-reference-documentation.md`

## References

- [REST API Design Guidelines](https://restfulapi.net/)
- [PostgreSQL Query Optimization](https://www.postgresql.org/docs/current/performance-tips.html)
- [Event Sourcing Query Patterns](https://eventstore.com/docs/server/projections/)
- [API Authentication Best Practices](https://oauth.net/2/)
