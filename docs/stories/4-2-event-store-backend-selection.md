# Story 4.2: Event Store Backend Selection

Status: ready-for-dev

## Story

As a **DevOps engineer**,
I want to implement a persistent, append-only event store for storing all system events,
so that events are never lost and can be replayed for debugging or audit purposes.

## Acceptance Criteria

1. Event store supports append-only writes (no updates or deletes)
2. Event store provides ordered reads by timestamp with efficient querying
3. Event store supports filtering by event type, actor, correlation ID
4. Event store handles high write throughput (100+ events/second)
5. Implementation supports multiple backends: local file (dev), PostgreSQL (prod), EventStore (optional)
6. Backend selection configurable via configuration file
7. Event store includes retention policy configuration (default: infinite retention)

## Tasks / Subtasks

- [ ] Task 1: Design event store interface abstraction (AC: 5)
  - [ ] Subtask 1.1: Define IEventStore interface with core operations
  - [ ] Subtask 1.2: Define EventStoreConfiguration interface
  - [ ] Subtask 1.3: Create factory pattern for backend selection
  - [ ] Subtask 1.4: Implement backend registration and discovery

- [ ] Task 2: Implement PostgreSQL backend (AC: 1, 2, 3, 4)
  - [ ] Subtask 2.1: Design PostgreSQL schema for events table
  - [ ] Subtask 2.2: Implement append-only write operations
  - [ ] Subtask 2.3: Create efficient indexes for common queries
  - [ ] Subtask 2.4: Implement connection pooling and transaction handling
  - [ ] Subtask 2.5: Add performance optimization (batch writes, prepared statements)

- [ ] Task 3: Implement local file backend (AC: 1, 2, 3)
  - [ ] Subtask 3.1: Design file-based storage format (JSONL or binary)
  - [ ] Subtask 3.2: Implement append-only file writing with rotation
  - [ ] Subtask 3.3: Create in-memory indexing for efficient queries
  - [ ] Subtask 3.4: Add file compression and cleanup policies
  - [ ] Subtask 3.5: Implement file-based retention management

- [ ] Task 4: Implement EventStore backend (optional) (AC: 1, 2, 3, 4)
  - [ ] Subtask 4.1: Research EventStoreDB client library
  - [ ] Subtask 4.2: Implement EventStoreDB connection and authentication
  - [ ] Subtask 4.3: Map Tamma events to EventStoreDB event format
  - [ ] Subtask 4.4: Implement EventStoreDB-specific optimizations
  - [ ] Subtask 4.5: Add EventStoreDB cluster support

- [ ] Task 5: Implement retention policy management (AC: 7)
  - [ ] Subtask 5.1: Define retention policy configuration schema
  - [ ] Subtask 5.2: Implement time-based retention (delete events older than X)
  - [ ] Subtask 5.3: Implement count-based retention (keep last N events)
  - [ ] Subtask 5.4: Implement tag-based retention (keep events matching criteria)
  - [ ] Subtask 5.5: Add retention policy enforcement scheduling

- [ ] Task 6: Add configuration and monitoring (AC: 6)
  - [ ] Subtask 6.1: Create event store configuration schema
  - [ ] Subtask 6.2: Implement backend selection logic
  - [ ] Subtask 6.3: Add health checks and monitoring endpoints
  - [ ] Subtask 6.4: Implement performance metrics collection
  - [ ] Subtask 6.5: Add backup and restore capabilities

## Dev Notes

### Requirements Context Summary

**Epic 4 Foundation:** This story implements the core storage layer for the DCB event sourcing pattern. The event store must provide reliable, append-only storage with efficient querying capabilities to support audit trails and time-travel debugging.

**Production Requirements:** The event store must handle high write throughput from multiple autonomous loops while maintaining data consistency and query performance. PostgreSQL backend provides the best balance of reliability, performance, and operational maturity for production use.

**Development Requirements:** Local file backend enables easy development and testing without requiring external database setup. This supports rapid iteration and local debugging capabilities.

### Implementation Guidance

**Event Store Interface:**

```typescript
interface IEventStore {
  // Core operations
  append(event: BaseEvent): Promise<void>;
  appendBatch(events: BaseEvent[]): Promise<void>;

  // Query operations
  getEvents(options: QueryOptions): Promise<EventPage>;
  getEvent(eventId: string): Promise<BaseEvent | null>;
  getEventsByCorrelation(correlationId: string): Promise<BaseEvent[]>;

  // Stream operations
  streamEvents(options: StreamOptions): AsyncIterable<BaseEvent>;
  streamEventsSince(timestamp: string): AsyncIterable<BaseEvent>;

  // Management operations
  createSnapshot(correlationId: string, state: unknown): Promise<void>;
  getSnapshot(correlationId: string): Promise<unknown | null>;

  // Health and monitoring
  healthCheck(): Promise<HealthStatus>;
  getMetrics(): Promise<EventStoreMetrics>;
}

interface QueryOptions {
  since?: string;
  until?: string;
  eventType?: string;
  actorId?: string;
  correlationId?: string;
  limit?: number;
  offset?: number;
  orderBy?: 'timestamp' | 'eventId';
  orderDirection?: 'asc' | 'desc';
}
```

**PostgreSQL Schema Design:**

```sql
CREATE TABLE events (
    event_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    event_type VARCHAR(255) NOT NULL,
    actor_type VARCHAR(50) NOT NULL,
    actor_id VARCHAR(255) NOT NULL,
    correlation_id UUID NOT NULL,
    causation_id UUID,
    schema_version VARCHAR(20) NOT NULL,
    payload JSONB NOT NULL,
    metadata JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Indexes for common query patterns
CREATE INDEX idx_events_timestamp ON events (timestamp DESC);
CREATE INDEX idx_events_correlation_id ON events (correlation_id);
CREATE INDEX idx_events_actor_id ON events (actor_id);
CREATE INDEX idx_events_event_type ON events (event_type);
CREATE INDEX idx_events_type_timestamp ON events (event_type, timestamp DESC);
CREATE INDEX idx_events_correlation_timestamp ON events (correlation_id, timestamp DESC);

-- JSONB indexes for flexible querying
CREATE INDEX idx_events_metadata_tags ON events USING GIN ((metadata->'tags'));
CREATE INDEX idx_events_payload_type ON events USING GIN ((payload->'type'));
```

**Local File Backend Design:**

```typescript
interface FileEventStoreConfig {
  dataDirectory: string;
  fileRotation: {
    maxSize: number; // Max file size in bytes
    maxFiles: number; // Max files to retain
    compression: 'gzip' | 'none';
  };
  indexing: {
    enabled: boolean;
    maxMemorySize: number; // Max memory for in-memory index
  };
}

// File format: JSONL (one JSON event per line)
// File naming: events-YYYY-MM-DD-HH-mm-SSS.jsonl[.gz]
// Index format: SQLite database with event metadata
```

**Backend Selection Strategy:**

```typescript
type EventStoreBackend = 'postgresql' | 'file' | 'eventstore';

interface EventStoreConfig {
  backend: EventStoreBackend;
  postgresql?: {
    host: string;
    port: number;
    database: string;
    username: string;
    password: string;
    ssl?: boolean;
    poolSize?: number;
  };
  file?: FileEventStoreConfig;
  eventstore?: {
    connectionString: string;
    defaultCredentials?: {
      username: string;
      password: string;
    };
  };
  retention: {
    defaultPolicy: 'infinite' | 'time' | 'count' | 'custom';
    timePolicy?: {
      retainDays: number;
    };
    countPolicy?: {
      retainEvents: number;
    };
    customPolicy?: {
      rules: RetentionRule[];
    };
  };
}
```

### Technical Specifications

**Performance Requirements:**

- Write throughput: 100+ events/second sustained
- Query latency: <100ms for typical queries
- Storage efficiency: <1MB per 1000 events (with compression)
- Index rebuild time: <30 seconds for 1M events

**Reliability Requirements:**

- Data durability: 99.999% (PostgreSQL)
- Backup support: Point-in-time recovery
- Corruption detection: Checksums and validation
- Failover support: Connection retry and circuit breaker

**Security Requirements:**

- Encryption at rest: AES-256 (PostgreSQL)
- Access control: Role-based permissions
- Audit logging: All access attempts logged
- Data masking: Sensitive fields redacted

**Monitoring Requirements:**

- Health checks: Liveness and readiness probes
- Performance metrics: Write latency, query latency, storage usage
- Error tracking: Failed writes, connection errors
- Capacity planning: Storage growth trends

### Dependencies

**Internal Dependencies:**

- Story 4.1: Event schema design (provides event structure)
- Epic 1.5: Configuration management (provides config system)

**External Dependencies:**

- PostgreSQL client library (pg or node-postgres)
- SQLite library (for file backend indexing)
- EventStoreDB client library (optional)
- Compression libraries (zlib, gzip)

### Risks and Mitigations

| Risk                               | Severity | Mitigation                                        |
| ---------------------------------- | -------- | ------------------------------------------------- |
| PostgreSQL performance bottlenecks | High     | Proper indexing, connection pooling, partitioning |
| File backend scalability limits    | Medium   | Clear usage guidelines, performance monitoring    |
| Event store corruption             | High     | Checksums, backups, validation routines           |
| Backend lock-in                    | Medium   | Interface abstraction, migration utilities        |

### Success Metrics

- [ ] Write throughput: >100 events/second
- [ ] Query performance: <100ms for 95% of queries
- [ ] Data durability: 99.999% uptime
- [ ] Backend switching: <5 minutes configuration change
- [ ] Retention policy enforcement: automated and reliable

## Related

- Related story: `docs/stories/4-1-event-schema-design.md`
- Related story: `docs/stories/4-3-event-capture-issue-selection-analysis.md`
- Technical specification: `docs/tech-spec-epic-4.md`
- Architecture: `docs/architecture.md` (Event Sourcing section)

## References

- [PostgreSQL JSONB Documentation](https://www.postgresql.org/docs/current/datatype-jsonb.html)
- [EventStoreDB Documentation](https://developers.eventstore.com/)
- [Append-Only Storage Pattern](https://martinfowler.com/eaaDev/EventSourcing.html)
- [Database Partitioning Guide](https://www.postgresql.org/docs/current/ddl-partitioning.html)
