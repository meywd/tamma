# Story 4-4: Snapshot Management

## Overview

Implement snapshot management system to optimize event sourcing performance by periodically capturing aggregate state, reducing replay time for long-running workflows, and enabling efficient state reconstruction for time-travel debugging.

## Acceptance Criteria

### Snapshot Creation

- [ ] Implement automatic snapshot creation based on event count or time intervals
- [ ] Create manual snapshot triggering capabilities
- [ ] Support incremental and full snapshot strategies
- [ ] Implement snapshot compression for storage efficiency
- [ ] Add snapshot validation and integrity checks

### Snapshot Storage

- [ ] Design efficient snapshot storage schema
- [ ] Implement snapshot versioning and metadata management
- [ ] Support snapshot archival and cleanup policies
- [ ] Create snapshot indexing for fast retrieval
- [ ] Add backup and recovery capabilities

### State Reconstruction

- [ ] Implement efficient state reconstruction from snapshots
- [ ] Create hybrid replay (snapshot + events) algorithms
- [ ] Support point-in-time state queries
- [ ] Add caching for frequently accessed snapshots
- [ ] Implement snapshot merge strategies

### Performance Optimization

- [ ] Optimize snapshot creation performance
- [ ] Implement parallel snapshot processing
- [ ] Add intelligent snapshot scheduling
- [ ] Create snapshot size optimization
- [ ] Implement resource usage monitoring

## Technical Context

### Snapshot Interface

```typescript
interface ISnapshotManager {
  // Snapshot creation
  createSnapshot(aggregateId: string, version: number, state: unknown): Promise<Snapshot>;
  createSnapshotBatch(snapshots: SnapshotRequest[]): Promise<Snapshot[]>;

  // Snapshot retrieval
  getSnapshot(aggregateId: string, version?: number): Promise<Snapshot | null>;
  getLatestSnapshot(aggregateId: string): Promise<Snapshot | null>;
  getSnapshotsInRange(
    aggregateId: string,
    fromVersion: number,
    toVersion: number
  ): Promise<Snapshot[]>;

  // State reconstruction
  reconstructState(aggregateId: string, toVersion?: number): Promise<StateReconstruction>;
  reconstructStateAtTime(aggregateId: string, timestamp: string): Promise<StateReconstruction>;

  // Snapshot management
  deleteSnapshot(aggregateId: string, version: number): Promise<void>;
  deleteSnapshotsBefore(aggregateId: string, version: number): Promise<void>;
  compactSnapshots(aggregateId: string, strategy: CompactionStrategy): Promise<void>;

  // Monitoring and maintenance
  getSnapshotStats(aggregateId: string): Promise<SnapshotStats>;
  scheduleSnapshot(aggregateId: string, schedule: SnapshotSchedule): Promise<void>;
}

interface Snapshot {
  id: string;
  aggregateId: string;
  version: number;
  timestamp: string;
  data: unknown;
  metadata: {
    eventType: string;
    compressed: boolean;
    size: number;
    checksum: string;
    createdFromVersion: number;
  };
}

interface StateReconstruction {
  aggregateId: string;
  version: number;
  timestamp: string;
  state: unknown;
  fromSnapshot: boolean;
  eventsReplayed: number;
  reconstructionTime: number;
}
```

### Snapshot Configuration

```typescript
interface SnapshotConfig {
  // Creation triggers
  eventThreshold: number; // Create snapshot every N events
  timeThreshold: number; // Create snapshot every N milliseconds
  sizeThreshold: number; // Create snapshot when state exceeds N bytes

  // Retention policies
  maxSnapshots: number; // Maximum snapshots to retain
  retentionPeriod: number; // Retain snapshots for N milliseconds
  archiveAfter: number; // Archive snapshots after N milliseconds

  // Performance settings
  compressionEnabled: boolean;
  compressionLevel: number; // 1-9 compression level
  parallelProcessing: boolean;
  cacheEnabled: boolean;
  cacheTTL: number; // Cache TTL in milliseconds
}

interface SnapshotSchedule {
  aggregateId: string;
  schedule: string; // Cron expression
  enabled: boolean;
  nextRun: string;
}
```

### Database Schema

```sql
-- Snapshots table (extended from Story 4-2)
CREATE TABLE event_snapshots (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  aggregate_id VARCHAR(255) NOT NULL,
  version INTEGER NOT NULL,
  timestamp TIMESTAMP WITH TIME ZONE NOT NULL,
  data JSONB NOT NULL,
  metadata JSONB NOT NULL DEFAULT '{}',
  created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
  updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),

  UNIQUE(aggregate_id, version),

  -- Indexes for performance
  INDEX idx_snapshots_aggregate_version (aggregate_id, version DESC),
  INDEX idx_snapshots_timestamp (timestamp DESC),
  INDEX idx_snapshots_aggregate_timestamp (aggregate_id, timestamp DESC)
);

-- Snapshot schedule table
CREATE TABLE snapshot_schedules (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  aggregate_id VARCHAR(255) NOT NULL UNIQUE,
  schedule VARCHAR(100) NOT NULL,
  enabled BOOLEAN NOT NULL DEFAULT true,
  next_run TIMESTAMP WITH TIME ZONE NOT NULL,
  created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
  updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

-- Snapshot statistics table
CREATE TABLE snapshot_stats (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  aggregate_id VARCHAR(255) NOT NULL,
  snapshot_date DATE NOT NULL,
  snapshots_created INTEGER NOT NULL DEFAULT 0,
  snapshots_deleted INTEGER NOT NULL DEFAULT 0,
  total_size_bytes BIGINT NOT NULL DEFAULT 0,
  avg_creation_time_ms INTEGER NOT NULL DEFAULT 0,
  created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),

  UNIQUE(aggregate_id, snapshot_date)
);
```

### Compression and Serialization

```typescript
interface ISnapshotSerializer {
  serialize(state: unknown): Promise<Buffer>;
  deserialize(data: Buffer): Promise<unknown>;
  compress(data: Buffer): Promise<Buffer>;
  decompress(data: Buffer): Promise<Buffer>;
}

interface SnapshotCompressionStrategy {
  algorithm: 'gzip' | 'brotli' | 'lz4';
  level: number;
  threshold: number; // Compress only if size > threshold
}
```

## Implementation Tasks

### 1. Core Snapshot Manager

- [ ] Create `packages/events/src/snapshots/snapshot-manager.ts`
- [ ] Implement snapshot creation and retrieval logic
- [ ] Add state reconstruction algorithms
- [ ] Create snapshot validation and integrity checks

### 2. Storage Implementation

- [ ] Create `packages/events/src/snapshots/snapshot-store.ts`
- [ ] Implement database operations for snapshots
- [ ] Add compression and serialization support
- [ ] Create archival and cleanup procedures

### 3. State Reconstruction Engine

- [ ] Create `packages/events/src/snapshots/state-reconstructor.ts`
- [ ] Implement hybrid replay algorithms
- [ ] Add point-in-time query support
- [ ] Create caching layer for performance

### 4. Scheduling and Automation

- [ ] Create `packages/events/src/snapshots/snapshot-scheduler.ts`
- [ ] Implement cron-based snapshot scheduling
- [ ] Add intelligent snapshot triggering
- [ ] Create resource usage monitoring

### 5. Performance Optimization

- [ ] Implement parallel snapshot processing
- [ ] Add compression optimization
- [ ] Create snapshot size optimization
- [ ] Implement caching strategies

### 6. Monitoring and Metrics

- [ ] Create snapshot performance metrics
- [ ] Add storage usage monitoring
- [ ] Implement health checks
- [ ] Create alerting for snapshot issues

### 7. Testing

- [ ] Unit tests for snapshot operations
- [ ] Integration tests with event store
- [ ] Performance tests for large datasets
- [ ] Reliability tests for corruption scenarios

## Dependencies

### Internal Dependencies

- `@tamma/events` - Event store interface
- Event schema from Story 4-1
- Event store implementation from Story 4-2

### External Dependencies

- `node-cron` - Cron scheduling
- `zlib` - Compression utilities
- `ioredis` - Caching layer

## Success Metrics

- Snapshot creation time: <1 second for typical aggregate state
- State reconstruction performance: 10x faster than full event replay
- Storage efficiency: 70% reduction through compression
- Snapshot integrity: 100% validation success rate
- Automated scheduling: 99% on-time execution

## Risks and Mitigations

### Snapshot Corruption

- **Risk**: Snapshots may become corrupted, affecting state reconstruction
- **Mitigation**: Implement checksums, validation, and backup strategies

### Performance Impact

- **Risk**: Snapshot creation may impact system performance
- **Mitigation**: Implement background processing, resource limits, and intelligent scheduling

### Storage Growth

- **Risk**: Snapshots may consume excessive storage
- **Mitigation**: Implement retention policies, compression, and archival strategies

## Notes

Snapshot management is crucial for scaling the event sourcing system to support long-running autonomous development workflows. By periodically capturing aggregate state, we can significantly reduce the time required to reconstruct current state and enable efficient time-travel debugging capabilities.

The snapshot system must balance performance, storage efficiency, and reliability while maintaining the integrity guarantees required for audit trail compliance.
