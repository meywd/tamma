# Story 4-2: Event Store Interface

## Overview

Implement the core event store interface that provides the foundation for the DCB (Dynamic Consistency Boundary) event sourcing system, enabling reliable event storage, retrieval, and querying capabilities.

## Acceptance Criteria

### Core Event Store Interface

- [ ] Define `IEventStore` interface with all required methods
- [ ] Implement event append operation with validation
- [ ] Create event query methods with flexible filtering
- [ ] Support event streaming for real-time updates
- [ ] Implement transaction support for atomic operations

### Event Persistence

- [ ] Store events in PostgreSQL with proper schema
- [ ] Ensure immutability of stored events
- [ ] Implement optimistic concurrency control
- [ ] Support batch event operations for performance
- [ ] Create event compaction strategies for long-term storage

### Query Capabilities

- [ ] Support time-based event queries
- [ ] Implement tag-based filtering using JSONB indexes
- [ ] Create event type filtering capabilities
- [ ] Support pagination for large result sets
- [ ] Implement aggregate query functions

### Performance Optimization

- [ ] Implement connection pooling for database operations
- [ ] Create query result caching where appropriate
- [ ] Support read replicas for query scaling
- [ ] Implement proper database indexing strategy
- [ ] Create performance monitoring and metrics

## Technical Context

### Interface Definition

Based on the DCB pattern and event schema from Story 4-1:

```typescript
interface IEventStore {
  // Core operations
  append(event: DomainEvent): Promise<void>;
  appendBatch(events: DomainEvent[]): Promise<void>;

  // Query operations
  getEvent(eventId: string): Promise<DomainEvent | null>;
  getEvents(filter: EventFilter): Promise<DomainEvent[]>;
  getEventsByIssueId(issueId: string): Promise<DomainEvent[]>;
  getEventsByTimeRange(start: string, end: string): Promise<DomainEvent[]>;

  // Streaming operations
  streamEvents(filter: EventFilter): AsyncIterable<DomainEvent>;
  subscribeToEvents(filter: EventFilter): AsyncIterable<DomainEvent>;

  // Aggregate operations
  countEvents(filter: EventFilter): Promise<number>;
  getEventTypes(): Promise<string[]>;
  getTags(): Promise<Record<string, string[]>>;

  // Maintenance operations
  compactEvents(before: string): Promise<number>;
  createSnapshot(aggregateId: string, version: number): Promise<void>;
  getSnapshot(aggregateId: string): Promise<Snapshot | null>;
}
```

### Event Filter Structure

```typescript
interface EventFilter {
  eventIds?: string[];
  eventTypes?: string[];
  issueIds?: string[];
  prIds?: string[];
  userIds?: string[];
  providers?: string[];
  platforms?: string[];
  tags?: Record<string, string>;
  startTime?: string;
  endTime?: string;
  limit?: number;
  offset?: number;
  orderBy?: 'timestamp' | 'type';
  orderDirection?: 'asc' | 'desc';
}
```

### Database Implementation

The PostgreSQL implementation will use the schema from Story 4-1:

```sql
-- Core events table (from Story 4-1)
CREATE TABLE events (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  event_id VARCHAR(36) NOT NULL UNIQUE,
  event_type VARCHAR(255) NOT NULL,
  timestamp TIMESTAMP WITH TIME ZONE NOT NULL,
  tags JSONB NOT NULL DEFAULT '{}',
  metadata JSONB NOT NULL DEFAULT '{}',
  data JSONB NOT NULL DEFAULT '{}',
  created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

-- Additional indexes for query performance
CREATE INDEX idx_events_timestamp_desc ON events (timestamp DESC);
CREATE INDEX idx_events_type_timestamp ON events (event_type, timestamp);
CREATE INDEX idx_events_issue_timestamp ON events ((tags->>'issueId'), timestamp);
CREATE INDEX idx_events_provider_timestamp ON events ((tags->>'provider'), timestamp);
CREATE INDEX idx_events_platform_timestamp ON events ((tags->>'platform'), timestamp);

-- Snapshots table for performance optimization
CREATE TABLE event_snapshots (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  aggregate_id VARCHAR(255) NOT NULL,
  version INTEGER NOT NULL,
  data JSONB NOT NULL,
  created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
  UNIQUE(aggregate_id, version)
);
```

### Error Handling

```typescript
class EventStoreError extends Error {
  constructor(
    public code: 'DUPLICATE_EVENT' | 'VALIDATION_FAILED' | 'STORAGE_ERROR' | 'QUERY_ERROR',
    message: string,
    public context: Record<string, unknown> = {}
  ) {
    super(message);
    this.name = 'EventStoreError';
  }
}
```

## Implementation Tasks

### 1. Interface Definition

- [ ] Create `packages/events/src/interfaces/event-store.interface.ts`
- [ ] Define all method signatures with proper TypeScript types
- [ ] Create supporting interfaces (EventFilter, Snapshot, etc.)
- [ ] Add comprehensive JSDoc documentation

### 2. PostgreSQL Implementation

- [ ] Create `packages/events/src/implementations/postgresql-event-store.ts`
- [ ] Implement all interface methods using pg client
- [ ] Add connection pooling and transaction support
- [ ] Implement proper error handling and logging

### 3. Query Builder

- [ ] Create `packages/events/src/query-builders/event-query-builder.ts`
- [ ] Implement dynamic SQL generation for filters
- [ ] Add parameter binding for security
- [ ] Support complex queries with multiple conditions

### 4. Streaming Implementation

- [ ] Create `packages/events/src/streaming/event-streamer.ts`
- [ ] Implement cursor-based pagination for streaming
- [ ] Add backpressure handling for slow consumers
- [ ] Support Server-Sent Events for real-time updates

### 5. Performance Optimization

- [ ] Implement query result caching with TTL
- [ ] Add connection pool monitoring
- [ ] Create performance metrics collection
- [ ] Implement query optimization hints

### 6. Testing

- [ ] Unit tests for all interface methods
- [ ] Integration tests with PostgreSQL
- [ ] Performance tests for high-volume scenarios
- [ ] Concurrency tests for thread safety

## Dependencies

### Internal Dependencies

- `@tamma/shared` - Shared types and utilities
- Event schema from Story 4-1

### External Dependencies

- `pg` - PostgreSQL client
- `pg-pool` - Connection pooling
- `ioredis` - For caching (optional)

## Success Metrics

- Event append performance: >10,000 events/second
- Query performance: <100ms for filtered queries
- Zero data loss in event storage
- 99.9% uptime for event store operations
- Complete audit trail integrity

## Risks and Mitigations

### Performance Bottlenecks

- **Risk**: High event volume may impact database performance
- **Mitigation**: Implement proper indexing, connection pooling, and read replicas

### Data Consistency

- **Risk**: Concurrent operations may cause data inconsistency
- **Mitigation**: Use database transactions and optimistic concurrency control

### Storage Growth

- **Risk**: Event volume may lead to excessive storage requirements
- **Mitigation**: Implement event compaction and archival strategies

## Notes

The event store is the foundation of Tamma's audit trail and time-travel debugging capabilities. It must provide reliable, high-performance event storage while maintaining data integrity and supporting complex query patterns required for autonomous development workflows.

This implementation directly supports compliance requirements by ensuring all system actions are captured immutably with complete auditability.
