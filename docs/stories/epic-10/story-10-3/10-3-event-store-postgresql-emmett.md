# Story 10.3: Event Store — PostgreSQL/Emmett Implementation

Status: ready-for-dev

## Story

As a **platform architect**,
I want a persistent, production-grade event store backed by PostgreSQL and Emmett that all system components (engine, Elsa, UI, platform adapters) write to and read from,
so that the event stream is the single source of truth, survives restarts, handles production load, and supports the DCB (Dynamic Consistency Boundary) pattern for flexible querying.

## Acceptance Criteria

1. Event store implements `IEventStore` interface with: `append(event)`, `query(filter)`, `getStream(correlationId)`, `getLastSnapshot(type)`, `subscribe(filter, handler)`
2. Backed by PostgreSQL using Emmett library for DCB pattern support
3. Events are stored in append-only table with JSONB payload and GIN index on tags
4. Uses `@>` containment operator for tag queries (NOT `->>` extraction) — verified by query explain plans
5. Uses `jsonb_path_ops` GIN operator class for optimal containment query performance
6. Event store validates events against typed schemas (from Story 10.2) before appending
7. Supports blob storage for large content (raw LLM prompts/responses) with configurable backend (filesystem for dev, S3-compatible for production)
8. Handles sustained write throughput of 50 events/second with <10ms P95 append latency
9. Supports time-based table partitioning (monthly) configurable for retention management
10. Provides subscription mechanism for real-time event consumption (engine listens for Elsa callbacks)
11. Includes connection pooling configuration and health checks
12. Falls back to in-memory store for local development without PostgreSQL
13. Blob storage references use content-addressable hashing for deduplication
14. Retention policies configurable per event category (e.g., raw LLM content: 30 days, platform events: 1 year, security events: 7 years)

## Technical Context

### Database Schema

```sql
-- Main events table (partitioned by month)
CREATE TABLE events (
  event_id       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  timestamp      TIMESTAMPTZ NOT NULL DEFAULT now(),
  event_type     TEXT NOT NULL,
  schema_version TEXT NOT NULL DEFAULT '1.0.0',
  actor_type     TEXT NOT NULL,
  actor_id       TEXT NOT NULL,
  correlation_id UUID NOT NULL,
  causation_id   UUID,
  workflow_id    TEXT,
  issue_id       TEXT,
  pr_id          TEXT,
  project_id     TEXT,
  tags           JSONB NOT NULL DEFAULT '{}',
  payload        JSONB NOT NULL,
  created_at     TIMESTAMPTZ NOT NULL DEFAULT now()
) PARTITION BY RANGE (timestamp);

-- Monthly partitions (auto-created)
CREATE TABLE events_2026_03 PARTITION OF events
  FOR VALUES FROM ('2026-03-01') TO ('2026-04-01');

-- GIN index for tag-based DCB queries (jsonb_path_ops for containment only)
CREATE INDEX idx_events_tags ON events USING GIN (tags jsonb_path_ops);

-- B-tree indexes for common access patterns
CREATE INDEX idx_events_correlation ON events (correlation_id);
CREATE INDEX idx_events_type_time ON events (event_type, timestamp DESC);
CREATE INDEX idx_events_workflow ON events (workflow_id) WHERE workflow_id IS NOT NULL;
CREATE INDEX idx_events_issue ON events (issue_id) WHERE issue_id IS NOT NULL;

-- Blob references table
CREATE TABLE event_blobs (
  blob_id        TEXT PRIMARY KEY,  -- content-addressable hash (SHA-256)
  event_id       UUID NOT NULL REFERENCES events(event_id),
  content_type   TEXT NOT NULL,     -- 'llm_prompt' | 'llm_response' | 'webhook_payload'
  size_bytes     INTEGER NOT NULL,
  storage_backend TEXT NOT NULL,    -- 'filesystem' | 's3'
  storage_path   TEXT NOT NULL,     -- path or S3 key
  classification TEXT NOT NULL DEFAULT 'internal', -- 'public' | 'internal' | 'confidential' | 'restricted'
  retention_days INTEGER NOT NULL DEFAULT 30,
  created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
  expires_at     TIMESTAMPTZ
);

CREATE INDEX idx_blobs_expiry ON event_blobs (expires_at) WHERE expires_at IS NOT NULL;
```

### Interface Definition

```typescript
interface IEventStore {
  // Write
  append(event: TammaEvent): Promise<AppendResult>;
  appendBatch(events: TammaEvent[]): Promise<AppendResult[]>;

  // Read
  query(filter: EventFilter): Promise<TammaEvent[]>;
  getStream(correlationId: string): Promise<TammaEvent[]>;
  getLastEvent(eventType: EventType, filter?: Partial<EventFilter>): Promise<TammaEvent | null>;
  getLastSnapshot(snapshotType: string): Promise<TammaEvent | null>;
  count(filter: EventFilter): Promise<number>;

  // Blob storage
  storeBlob(eventId: string, contentType: string, content: Buffer | string, classification: string): Promise<string>;
  getBlob(blobId: string): Promise<{ content: Buffer; metadata: BlobMetadata } | null>;

  // Subscription
  subscribe(filter: EventFilter, handler: (event: TammaEvent) => void): Subscription;

  // Maintenance
  getHealth(): Promise<HealthStatus>;
  createPartition(month: string): Promise<void>;
  cleanExpiredBlobs(): Promise<number>;
}

interface EventFilter {
  eventTypes?: EventType[];
  correlationId?: string;
  workflowId?: string;
  issueId?: string;
  projectId?: string;
  actorType?: string;
  tags?: Record<string, string>;      // Uses @> containment
  since?: string;                      // ISO 8601
  until?: string;                      // ISO 8601
  limit?: number;
  offset?: number;
  orderBy?: 'timestamp_asc' | 'timestamp_desc';
}

interface AppendResult {
  eventId: string;
  timestamp: string;
  position: number;  // Global stream position
}
```

### Performance Targets (from research)

| Metric | Target | Basis |
|--------|--------|-------|
| Append latency (P95) | <10ms | PG inserts: 1,000-18,500/sec even on minimal hardware |
| Append throughput | 50/sec sustained | Enterprise peak is ~3/sec; 50 gives 16x headroom |
| Query by correlation (500 events) | <5ms | B-tree index on correlation_id |
| Query by tags (10M rows) | <30ms | GIN jsonb_path_ops containment |
| Query by tags (30M rows) | <80ms P95 | Production-measured benchmark |
| State reconstruction (500 events) | <50ms | Aggregate rehydration benchmark: 10K events in 50ms |
| Blob store/retrieve | <50ms | Filesystem for dev; S3 with local cache for prod |

### Emmett Integration

```typescript
// Emmett provides the DCB append semantics
import { getPostgreSQLEventStore } from '@event-driven-io/emmett-postgresql';

// Custom wrapper to add: validation, blob storage, partitioning, subscriptions
class PostgreSQLEventStore implements IEventStore {
  private emmett: EmmettEventStore;
  private pool: Pool;                // pg connection pool
  private blobStorage: IBlobStorage;
  private validator: IEventValidator;

  async append(event: TammaEvent): Promise<AppendResult> {
    // 1. Validate event schema
    const validation = this.validator.validate(event);
    if (!validation.valid) throw new EventValidationError(validation.errors);

    // 2. Extract large content to blob storage
    const { slimEvent, blobRefs } = await this.extractBlobs(event);

    // 3. Append via Emmett (handles DCB consistency)
    const result = await this.emmett.appendToStream(/* ... */);

    // 4. Store blob references
    for (const ref of blobRefs) {
      await this.storeBlobRef(result.eventId, ref);
    }

    return result;
  }
}
```

## Tasks / Subtasks

- [ ] Task 1: Design and create database schema (AC: 3, 9)
  - [ ] Subtask 1.1: Create events table with partitioning by month
  - [ ] Subtask 1.2: Create event_blobs table with retention and expiry
  - [ ] Subtask 1.3: Create GIN index with `jsonb_path_ops` on tags
  - [ ] Subtask 1.4: Create B-tree indexes for correlation, type+time, workflow, issue
  - [ ] Subtask 1.5: Create partition auto-creation function (cron or on-demand)
  - [ ] Subtask 1.6: Verify index usage with `EXPLAIN ANALYZE` on representative queries

- [ ] Task 2: Implement `IEventStore` interface (AC: 1, 2, 6)
  - [ ] Subtask 2.1: Implement `PostgreSQLEventStore` class wrapping Emmett
  - [ ] Subtask 2.2: Implement `append()` with validation -> blob extraction -> Emmett append
  - [ ] Subtask 2.3: Implement `appendBatch()` for transactional multi-event writes
  - [ ] Subtask 2.4: Implement `query()` with `EventFilter` -> SQL generation using `@>` containment
  - [ ] Subtask 2.5: Implement `getStream()` for correlation-based retrieval
  - [ ] Subtask 2.6: Implement `getLastEvent()` and `getLastSnapshot()` with index hints

- [ ] Task 3: Implement blob storage (AC: 7, 13)
  - [ ] Subtask 3.1: Define `IBlobStorage` interface (store, retrieve, delete)
  - [ ] Subtask 3.2: Implement `FilesystemBlobStorage` for local development
  - [ ] Subtask 3.3: Implement `S3BlobStorage` for production (using AWS SDK or MinIO client)
  - [ ] Subtask 3.4: Implement content-addressable hashing (SHA-256) for deduplication
  - [ ] Subtask 3.5: Wire blob extraction into event append pipeline (detect >1KB payloads)

- [ ] Task 4: Implement subscription mechanism (AC: 10)
  - [ ] Subtask 4.1: Implement PostgreSQL LISTEN/NOTIFY for real-time event notifications
  - [ ] Subtask 4.2: Create `Subscription` class with filter matching and handler dispatch
  - [ ] Subtask 4.3: Support multiple concurrent subscribers with independent filters
  - [ ] Subtask 4.4: Handle subscriber reconnection on connection loss
  - [ ] Subtask 4.5: Implement catch-up subscription (read missed events since last position)

- [ ] Task 5: Implement retention and maintenance (AC: 9, 14)
  - [ ] Subtask 5.1: Implement configurable retention policies per event category
  - [ ] Subtask 5.2: Implement `cleanExpiredBlobs()` for blob lifecycle management
  - [ ] Subtask 5.3: Implement partition detach for old data (don't delete, detach from query)
  - [ ] Subtask 5.4: Create maintenance cron schedule recommendations in docs

- [ ] Task 6: Implement connection pooling and health (AC: 11)
  - [ ] Subtask 6.1: Configure pg connection pool with min/max/idle settings
  - [ ] Subtask 6.2: Implement `getHealth()` checking: pool status, partition existence, disk space
  - [ ] Subtask 6.3: Add connection pool metrics (active, idle, waiting)

- [ ] Task 7: Implement in-memory fallback (AC: 12)
  - [ ] Subtask 7.1: Upgrade existing `InMemoryEventStore` to implement new `IEventStore` interface
  - [ ] Subtask 7.2: Add in-memory blob storage implementation
  - [ ] Subtask 7.3: Add in-memory subscription support
  - [ ] Subtask 7.4: Auto-detect PostgreSQL availability and fall back gracefully

- [ ] Task 8: Performance validation (AC: 4, 5, 8)
  - [ ] Subtask 8.1: Write benchmark: 50 events/sec sustained for 60 seconds
  - [ ] Subtask 8.2: Write benchmark: query by tags on 100K events using `@>` containment
  - [ ] Subtask 8.3: Verify `EXPLAIN ANALYZE` shows GIN index usage (not seq scan)
  - [ ] Subtask 8.4: Write benchmark: state reconstruction from 500 events
  - [ ] Subtask 8.5: Write benchmark: blob store/retrieve round-trip

- [ ] Task 9: Testing (AC: all)
  - [ ] Subtask 9.1: Unit test event validation rejects invalid events
  - [ ] Subtask 9.2: Unit test blob extraction detects large payloads
  - [ ] Subtask 9.3: Integration test with real PostgreSQL: append -> query -> verify
  - [ ] Subtask 9.4: Integration test subscription receives events in real-time
  - [ ] Subtask 9.5: Integration test partition creation and retention
  - [ ] Subtask 9.6: Test fallback to in-memory when PostgreSQL unavailable

## Dev Notes

### Project Structure Notes

- New implementation: `packages/shared/src/event-store/postgresql-event-store.ts`
- New implementation: `packages/shared/src/event-store/blob-storage/filesystem.ts`
- New implementation: `packages/shared/src/event-store/blob-storage/s3.ts`
- New implementation: `packages/shared/src/event-store/subscriptions.ts`
- New migration: `packages/shared/src/event-store/migrations/001-events-table.sql`
- Modified: `packages/shared/src/event-store.ts` (upgrade InMemoryEventStore)
- Modified: `packages/shared/src/types/index.ts` (new IEventStore interface)

### PostgreSQL Configuration Notes

```ini
# Required for event store performance
shared_buffers = 25% of RAM
effective_cache_size = 75% of RAM
synchronous_commit = on           # Durability > speed for events
wal_level = replica               # Required for LISTEN/NOTIFY
max_connections = 50-200          # Based on tier
```

### References

- **Epic 10 Tech Spec:** `docs/stories/epic-10/tech-spec-epic-10.md` (hardware sizing section)
- **Story 10.2:** Event catalog and types (prerequisite)
- **Emmett Docs:** `@event-driven-io/emmett-postgresql`
- **Current Event Store:** `packages/shared/src/event-store.ts`
- **Story 4.2:** `docs/stories/epic-4/story-4-2/` (event store backend selection)

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-26 | 1.0 | Initial story creation | Architecture Team |
