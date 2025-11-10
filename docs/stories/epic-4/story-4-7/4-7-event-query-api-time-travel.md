# Story 4.7: Event Query API for Time-Travel

## Overview

Implement comprehensive event query API that enables time-travel debugging, state reconstruction at any point in time, and flexible event analysis for compliance, debugging, and workflow optimization purposes.

## Acceptance Criteria

### Event Query API

- [ ] Query events by time range with millisecond precision
- [ ] Filter events by event type, actor, correlation ID, and custom tags
- [ ] Support pagination for large result sets with cursor-based navigation
- [ ] Provide aggregation queries for event statistics and metrics
- [ ] Include full-text search across event payloads and metadata

### Time-Travel Capabilities

- [ ] Reconstruct aggregate state at any historical timestamp
- [ ] Query event history for specific entities (issues, PRs, workflows)
- [ ] Support point-in-time queries with consistency guarantees
- [ ] Enable event-by-event navigation with forward/backward stepping
- [ ] Provide state diff visualization between time points

### Performance and Optimization

- [ ] Query response time <500ms for common queries
- [ ] Support concurrent queries with proper resource management
- [ ] Implement query result caching for frequently accessed data
- [ ] Provide query performance monitoring and optimization
- [ ] Support query timeouts and resource limits

## Technical Context

### Query API Interface

```typescript
interface IEventQueryAPI {
  // Basic event queries
  queryEvents(request: EventQueryRequest): Promise<EventQueryResult>;
  getEvent(eventId: string): Promise<DomainEvent | null>;
  getEventsByCorrelation(correlationId: string): Promise<DomainEvent[]>;

  // Time-travel queries
  getStateAtTime(aggregateId: string, timestamp: string): Promise<AggregateState>;
  getEventHistory(aggregateId: string, timeRange?: TimeRange): Promise<DomainEvent[]>;
  getStateDiff(aggregateId: string, fromTime: string, toTime: string): Promise<StateDiff>;

  // Aggregation queries
  getEventStats(request: StatsQueryRequest): Promise<EventStats>;
  getWorkflowMetrics(workflowId: string, timeRange?: TimeRange): Promise<WorkflowMetrics>;
  getProviderUsage(timeRange?: TimeRange): Promise<ProviderUsageStats>;

  // Search queries
  searchEvents(request: SearchRequest): Promise<SearchResult>;
  searchByContent(query: string, options?: SearchOptions): Promise<SearchResult>;
}

interface EventQueryRequest {
  timeRange?: {
    from: string; // ISO 8601 timestamp
    to: string; // ISO 8601 timestamp
  };
  eventTypes?: string[]; // Event type filters
  actors?: {
    actorTypes?: string[];
    actorIds?: string[];
  };
  tags?: {
    [key: string]: string | string[];
  };
  metadata?: {
    [key: string]: unknown;
  };
  pagination?: {
    limit?: number; // Default: 100, Max: 1000
    cursor?: string; // Cursor for pagination
    orderBy?: 'timestamp' | 'eventId';
    orderDirection?: 'asc' | 'desc';
  };
  includePayload?: boolean; // Default: true
  includeMetadata?: boolean; // Default: true
}

interface EventQueryResult {
  events: DomainEvent[];
  totalCount: number;
  hasMore: boolean;
  nextCursor?: string;
  queryTime: number; // Query execution time in ms
  cacheHit?: boolean;
}

interface TimeRange {
  from: string; // ISO 8601 timestamp
  to: string; // ISO 8601 timestamp
}

interface AggregateState {
  aggregateId: string;
  timestamp: string; // State at this timestamp
  version: number; // Event version
  state: Record<string, unknown>;
  lastEventId: string;
  appliedEvents: number;
}

interface StateDiff {
  aggregateId: string;
  fromTime: string;
  toTime: string;
  changes: Array<{
    path: string; // JSON path to changed property
    from: unknown;
    to: unknown;
    changeType: 'added' | 'removed' | 'modified';
    eventId?: string; // Event that caused this change
  }>;
  summary: {
    added: number;
    removed: number;
    modified: number;
  };
}
```

### Query Implementation

```typescript
class EventQueryService implements IEventQueryAPI {
  constructor(
    private eventStore: IEventStore,
    private stateReconstructor: StateReconstructor,
    private queryCache: QueryCache,
    private searchIndex: SearchIndex
  ) {}

  async queryEvents(request: EventQueryRequest): Promise<EventQueryResult> {
    const startTime = Date.now();

    // Check cache first
    const cacheKey = this.generateCacheKey(request);
    const cached = await this.queryCache.get(cacheKey);
    if (cached && !this.isRequestRealtime(request)) {
      return {
        ...cached,
        queryTime: Date.now() - startTime,
        cacheHit: true,
      };
    }

    // Build query
    const query = this.buildQuery(request);

    // Execute query with pagination
    const result = await this.eventStore.query(query);

    // Process results
    const events = await this.processQueryResults(result, request);

    const queryResult: EventQueryResult = {
      events,
      totalCount: result.totalCount,
      hasMore: result.hasMore,
      nextCursor: result.nextCursor,
      queryTime: Date.now() - startTime,
    };

    // Cache result if appropriate
    if (this.shouldCacheResult(request, queryResult)) {
      await this.queryCache.set(cacheKey, queryResult, this.getCacheTTL(request));
    }

    return queryResult;
  }

  async getStateAtTime(aggregateId: string, timestamp: string): Promise<AggregateState> {
    // Check cache first
    const cacheKey = `state:${aggregateId}:${timestamp}`;
    const cached = await this.queryCache.get(cacheKey);
    if (cached) {
      return cached;
    }

    // Get events up to timestamp
    const events = await this.eventStore.getEventsUpTo(aggregateId, timestamp);

    // Reconstruct state
    const state = await this.stateReconstructor.reconstructState(events, timestamp);

    // Cache result
    await this.queryCache.set(cacheKey, state, 300); // 5 minutes cache

    return state;
  }

  async getStateDiff(aggregateId: string, fromTime: string, toTime: string): Promise<StateDiff> {
    // Get both states
    const [fromState, toState] = await Promise.all([
      this.getStateAtTime(aggregateId, fromTime),
      this.getStateAtTime(aggregateId, toTime),
    ]);

    // Compute diff
    const diff = this.computeStateDiff(fromState.state, toState.state);

    // Annotate with event information
    const annotatedDiff = await this.annotateDiffWithEvents(aggregateId, fromTime, toTime, diff);

    return {
      aggregateId,
      fromTime,
      toTime,
      changes: annotatedDiff,
      summary: {
        added: annotatedDiff.filter((c) => c.changeType === 'added').length,
        removed: annotatedDiff.filter((c) => c.changeType === 'removed').length,
        modified: annotatedDiff.filter((c) => c.changeType === 'modified').length,
      },
    };
  }

  async searchEvents(request: SearchRequest): Promise<SearchResult> {
    // Use search index for full-text search
    const searchResults = await this.searchIndex.search(request.query, {
      filters: this.buildSearchFilters(request),
      pagination: request.pagination,
      highlight: request.highlight,
    });

    // Get full events for search results
    const events = await this.eventStore.getEventsByIds(search.results.map((r) => r.eventId));

    return {
      events,
      totalCount: searchResults.totalCount,
      hasMore: searchResults.hasMore,
      nextCursor: searchResults.nextCursor,
      highlights: searchResults.highlights,
      scores: searchResults.scores,
    };
  }

  async getEventStats(request: StatsQueryRequest): Promise<EventStats> {
    const timeRange = request.timeRange || this.getDefaultTimeRange();

    // Build aggregation query
    const aggregationQuery = {
      timeRange,
      groupBy: request.groupBy || ['eventType'],
      metrics: request.metrics || ['count', 'avgDuration'],
      filters: request.filters,
    };

    // Execute aggregation
    const result = await this.eventStore.aggregate(aggregationQuery);

    return {
      timeRange,
      groupBy: request.groupBy || ['eventType'],
      metrics: this.formatAggregationResult(result),
      queryTime: result.queryTime,
    };
  }

  private buildQuery(request: EventQueryRequest): EventStoreQuery {
    const query: EventStoreQuery = {};

    // Time range
    if (request.timeRange) {
      query.timestampRange = {
        from: new Date(request.timeRange.from),
        to: new Date(request.timeRange.to),
      };
    }

    // Event types
    if (request.eventTypes?.length) {
      query.eventTypes = request.eventTypes;
    }

    // Actors
    if (request.actors) {
      if (request.actors.actorTypes?.length) {
        query.actorTypes = request.actors.actorTypes;
      }
      if (request.actors.actorIds?.length) {
        query.actorIds = request.actors.actorIds;
      }
    }

    // Tags
    if (request.tags) {
      query.tags = {};
      for (const [key, value] of Object.entries(request.tags)) {
        query.tags[key] = Array.isArray(value) ? { $in: value } : value;
      }
    }

    // Metadata
    if (request.metadata) {
      query.metadata = request.metadata;
    }

    // Pagination
    if (request.pagination) {
      query.limit = Math.min(request.pagination.limit || 100, 1000);
      query.cursor = request.pagination.cursor;
      query.orderBy = request.pagination.orderBy || 'timestamp';
      query.orderDirection = request.pagination.orderDirection || 'desc';
    }

    return query;
  }

  private async processQueryResults(
    result: EventStoreQueryResult,
    request: EventQueryRequest
  ): Promise<DomainEvent[]> {
    let events = result.events;

    // Filter payload/metadata if requested
    if (!request.includePayload || !request.includeMetadata) {
      events = events.map((event) => {
        const filteredEvent = { ...event };
        if (!request.includePayload) {
          delete (filteredEvent as any).payload;
        }
        if (!request.includeMetadata) {
          delete (filteredEvent as any).metadata;
        }
        return filteredEvent;
      });
    }

    return events;
  }
}
```

### State Reconstruction

```typescript
class StateReconstructor {
  async reconstructState(events: DomainEvent[], timestamp: string): Promise<AggregateState> {
    if (events.length === 0) {
      return {
        aggregateId: '',
        timestamp,
        version: 0,
        state: {},
        lastEventId: '',
        appliedEvents: 0,
      };
    }

    // Sort events by timestamp
    const sortedEvents = events.sort(
      (a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime()
    );

    // Apply events in order
    let currentState = {};
    let lastEventId = '';

    for (const event of sortedEvents) {
      currentState = await this.applyEvent(currentState, event);
      lastEventId = event.eventId;
    }

    const aggregateId = this.extractAggregateId(events[0]);

    return {
      aggregateId,
      timestamp,
      version: events.length,
      state: currentState,
      lastEventId,
      appliedEvents: events.length,
    };
  }

  private async applyEvent(currentState: unknown, event: DomainEvent): Promise<unknown> {
    // Event-specific state application logic
    switch (event.eventType) {
      case 'IssueSelected':
        return this.applyIssueSelected(currentState, event);
      case 'AIRequest':
        return this.applyAIRequest(currentState, event);
      case 'AIResponse':
        return this.applyAIResponse(currentState, event);
      case 'CodeFileWritten':
        return this.applyCodeFileWritten(currentState, event);
      case 'ApprovalRequested':
        return this.applyApprovalRequested(currentState, event);
      case 'ApprovalCompleted':
        return this.applyApprovalCompleted(currentState, event);
      default:
        return currentState; // Unknown event type, no state change
    }
  }

  private applyIssueSelected(currentState: unknown, event: DomainEvent): unknown {
    const payload = event.payload as any;
    return {
      ...(currentState as object),
      issue: {
        id: payload.issueId,
        title: payload.title,
        selectedAt: event.timestamp,
        selectionCriteria: payload.selectionCriteria,
      },
    };
  }

  private applyAIRequest(currentState: unknown, event: DomainEvent): unknown {
    const payload = event.payload as any;
    const current = currentState as any;

    return {
      ...current,
      aiInteractions: [
        ...(current.aiInteractions || []),
        {
          requestId: event.metadata.requestId,
          provider: payload.provider.name,
          model: payload.model.name,
          requestType: payload.request.type,
          requestedAt: event.timestamp,
          estimatedTokens: payload.request.estimatedTokens,
        },
      ],
    };
  }

  private applyAIResponse(currentState: unknown, event: DomainEvent): unknown {
    const payload = event.payload as any;
    const current = currentState as any;

    // Update the corresponding AI request
    const aiInteractions = [...(current.aiInteractions || [])];
    const requestIndex = aiInteractions.findIndex(
      (interaction) => interaction.requestId === event.metadata.requestId
    );

    if (requestIndex >= 0) {
      aiInteractions[requestIndex] = {
        ...aiInteractions[requestIndex],
        response: {
          success: payload.response.success,
          actualTokens: payload.response.actualTokens,
          latency: payload.performance.latency,
          cost: payload.cost.totalCost,
          respondedAt: event.timestamp,
        },
      };
    }

    return {
      ...current,
      aiInteractions,
    };
  }

  // ... other event application methods
}
```

### Query Optimization

```typescript
class QueryOptimizer {
  constructor(
    private indexManager: IndexManager,
    private statsCollector: QueryStatsCollector
  ) {}

  async optimizeQuery(query: EventQueryRequest): Promise<OptimizedQuery> {
    const optimizations: QueryOptimization[] = [];

    // Analyze query patterns
    const queryPattern = this.analyzeQueryPattern(query);

    // Suggest indexes
    const suggestedIndexes = this.suggestIndexes(queryPattern);
    if (suggestedIndexes.length > 0) {
      optimizations.push({
        type: 'index_suggestion',
        description: 'Add indexes for better query performance',
        suggestions: suggestedIndexes,
      });
    }

    // Optimize time range
    const optimizedTimeRange = this.optimizeTimeRange(query.timeRange);
    if (optimizedTimeRange !== query.timeRange) {
      optimizations.push({
        type: 'time_range_optimization',
        description: 'Optimized time range for better performance',
        original: query.timeRange,
        optimized: optimizedTimeRange,
      });
    }

    // Suggest query caching
    if (this.shouldCacheQuery(query)) {
      optimizations.push({
        type: 'caching_suggestion',
        description: 'This query would benefit from caching',
        cacheKey: this.generateCacheKey(query),
        suggestedTTL: this.suggestCacheTTL(query),
      });
    }

    return {
      originalQuery: query,
      optimizedQuery: this.applyOptimizations(query, optimizations),
      optimizations,
      estimatedImprovement: this.estimatePerformanceImprovement(optimizations),
    };
  }

  private analyzeQueryPattern(query: EventQueryRequest): QueryPattern {
    return {
      hasTimeRange: !!query.timeRange,
      hasEventTypeFilter: !!query.eventTypes?.length,
      hasActorFilter: !!(query.actors?.actorTypes?.length || query.actors?.actorIds?.length),
      hasTagFilters: !!query.tags && Object.keys(query.tags).length > 0,
      hasMetadataFilters: !!query.metadata && Object.keys(query.metadata).length > 0,
      isAggregation: false, // Would be set based on query type
      isSearch: false, // Would be set based on query type
    };
  }

  private suggestIndexes(pattern: QueryPattern): IndexSuggestion[] {
    const suggestions: IndexSuggestion[] = [];

    if (pattern.hasTimeRange) {
      suggestions.push({
        type: 'timestamp',
        fields: ['timestamp'],
        reason: 'Time range queries benefit from timestamp index',
      });
    }

    if (pattern.hasEventTypeFilter) {
      suggestions.push({
        type: 'event_type',
        fields: ['eventType'],
        reason: 'Event type filtering benefits from event type index',
      });
    }

    if (pattern.hasActorFilter) {
      suggestions.push({
        type: 'actor',
        fields: ['actorType', 'actorId'],
        reason: 'Actor filtering benefits from actor index',
      });
    }

    if (pattern.hasTagFilters) {
      const tagFields = Object.keys({});
      suggestions.push({
        type: 'tags',
        fields: ['tags'],
        reason: 'Tag filtering benefits from GIN index on tags',
      });
    }

    return suggestions;
  }
}
```

## Implementation Tasks

### 1. Query API Implementation

- [ ] Implement `IEventQueryAPI` interface
- [ ] Create `EventQueryService` with all query methods
- [ ] Add query validation and error handling
- [ ] Implement query result formatting

### 2. State Reconstruction

- [ ] Implement `StateReconstructor` for time-travel
- [ ] Add event-specific state application logic
- [ ] Create state diff computation
- [ ] Implement aggregate state management

### 3. Query Optimization

- [ ] Implement `QueryOptimizer` for performance
- [ ] Add query pattern analysis
- [ ] Create index suggestion system
- [ ] Implement query caching strategies

### 4. Search Integration

- [ ] Implement full-text search capabilities
- [ ] Add search index management
- [ ] Create search result highlighting
- [ ] Implement search relevance scoring

### 5. Performance Monitoring

- [ ] Add query performance metrics
- [ ] Implement query timeout handling
- [ ] Create resource usage monitoring
- [ ] Add query optimization recommendations

### 6. Testing

- [ ] Unit tests for all query methods
- [ ] Performance tests for query optimization
- [ ] Integration tests with event store
- [ ] Load testing for concurrent queries

## Dependencies

### Internal Dependencies

- `@tamma/events` - Event schemas and types
- `@tamma/shared` - Shared utilities
- Story 4.2 - Event Store Backend (for data source)
- Story 4.1 - Event Schema Design (for query structure)

### External Dependencies

- Search engine (Elasticsearch, OpenSearch, or built-in)
- Caching system (Redis, in-memory)
- Database query optimization tools

## Success Metrics

- Query response time <500ms for common queries
- Support for 100+ concurrent queries
- Cache hit rate >80% for frequent queries
- State reconstruction accuracy 100%
- Search relevance score >0.8 for common queries

## Risks and Mitigations

### Performance Risks

- **Risk**: Complex queries may impact system performance
- **Mitigation**: Query optimization, caching, resource limits

### Data Consistency Risks

- **Risk**: State reconstruction may produce inconsistent results
- **Mitigation**: Event ordering validation, consistency checks

### Storage Risks

- **Risk**: Search index may become out of sync
- **Mitigation**: Index validation, automatic reindexing

### Security Risks

- **Risk**: Query API may expose sensitive data
- **Mitigation**: Access controls, data filtering, audit logging

## Notes

This story provides the foundation for time-travel debugging and compliance analysis. The query API must be both powerful and performant, supporting complex queries while maintaining sub-second response times for common use cases.

The state reconstruction capability is critical for debugging and audit purposes, allowing users to see the exact state of any aggregate at any point in time. This supports the compliance requirements while providing valuable debugging capabilities for development teams.
