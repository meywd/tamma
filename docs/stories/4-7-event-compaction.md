# Story 4-7: Event Compaction

## Overview

Implement intelligent event compaction strategies to manage storage growth, optimize query performance, and maintain long-term data retention while preserving audit trail integrity for compliance requirements.

## Acceptance Criteria

### Compaction Strategies

- [ ] Implement event compaction based on retention policies
- [ ] Create aggregate state compaction for long-running workflows
- [ ] Support event type-based compaction rules
- [ ] Add time-based compaction scheduling
- [ ] Implement selective compaction with preservation rules

### Storage Optimization

- [ ] Implement event archival to cold storage
- [ ] Create compressed event storage format
- [ ] Add storage tier management (hot/warm/cold)
- [ ] Implement storage usage monitoring and alerts
- [ ] Create storage cleanup automation

### Performance Optimization

- [ ] Optimize query performance after compaction
- [ ] Implement compaction impact analysis
- [ ] Add compaction performance monitoring
- [ ] Create compaction scheduling optimization
- [ ] Implement incremental compaction strategies

### Compliance and Audit

- [ ] Maintain audit trail integrity during compaction
- [ ] Implement compaction audit logging
- [ ] Create data retention policy enforcement
- [ ] Add compliance reporting capabilities
- [ ] Implement legal hold and preservation features

## Technical Context

### Compaction Interface

```typescript
interface IEventCompactor {
  // Compaction operations
  compactEvents(strategy: CompactionStrategy): Promise<CompactionResult>;
  compactAggregate(aggregateId: string, strategy: CompactionStrategy): Promise<CompactionResult>;
  compactTimeRange(
    from: string,
    to: string,
    strategy: CompactionStrategy
  ): Promise<CompactionResult>;

  // Strategy management
  createCompactionStrategy(strategy: CompactionStrategyDefinition): Promise<string>;
  updateCompactionStrategy(
    strategyId: string,
    updates: Partial<CompactionStrategyDefinition>
  ): Promise<void>;
  deleteCompactionStrategy(strategyId: string): Promise<void>;
  listCompactionStrategies(): Promise<CompactionStrategyInfo[]>;

  // Scheduling and automation
  scheduleCompaction(schedule: CompactionSchedule): Promise<string>;
  cancelScheduledCompaction(scheduleId: string): Promise<void>;
  getCompactionSchedule(): Promise<CompactionScheduleInfo[]>;

  // Monitoring and analysis
  analyzeCompactionImpact(strategy: CompactionStrategy): Promise<CompactionImpact>;
  getCompactionStats(timeRange?: TimeRange): Promise<CompactionStats>;
  getStorageUsage(): Promise<StorageUsageStats>;
}

interface CompactionStrategy {
  id: string;
  name: string;
  description: string;
  rules: CompactionRule[];
  retention: RetentionPolicy;
  archival: ArchivalPolicy;
  performance: PerformancePolicy;
}

interface CompactionRule {
  condition: EventFilter;
  action: 'archive' | 'delete' | 'compress' | 'aggregate';
  parameters: Record<string, unknown>;
  priority: number;
}

interface RetentionPolicy {
  defaultRetention: number; // milliseconds
  eventSpecificRetention: Record<string, number>;
  legalHoldExceptions: string[];
  complianceRequirements: ComplianceRequirement[];
}
```

### Archival System

```typescript
interface IEventArchiver {
  // Archival operations
  archiveEvents(filter: EventFilter, destination: ArchivalDestination): Promise<ArchivalResult>;
  retrieveEvents(archiveId: string, filter?: EventFilter): Promise<DomainEvent[]>;
  deleteArchive(archiveId: string): Promise<void>;

  // Storage management
  listArchives(filter?: ArchiveFilter): Promise<ArchiveInfo[]>;
  getArchiveSize(archiveId: string): Promise<number>;
  verifyArchiveIntegrity(archiveId: string): Promise<IntegrityResult>;

  // Migration operations
  migrateArchive(archiveId: string, destination: ArchivalDestination): Promise<MigrationResult>;
  restoreArchive(archiveId: string, targetDate?: string): Promise<RestoreResult>;
}

interface ArchivalDestination {
  type: 's3' | 'azure' | 'gcs' | 'local' | 'database';
  config: Record<string, unknown>;
  encryption: EncryptionConfig;
  compression: CompressionConfig;
}

interface ArchiveInfo {
  id: string;
  name: string;
  destination: ArchivalDestination;
  eventCount: number;
  sizeBytes: number;
  compressedSize: number;
  dateRange: TimeRange;
  eventTypes: string[];
  createdAt: string;
  retentionUntil: string;
}
```

### Storage Tier Management

```typescript
interface IStorageTierManager {
  // Tier operations
  createTier(config: StorageTierConfig): Promise<string>;
  updateTier(tierId: string, updates: Partial<StorageTierConfig>): Promise<void>;
  deleteTier(tierId: string): Promise<void>;

  // Data movement
  moveToTier(events: DomainEvent[], targetTier: string): Promise<TierMigrationResult>;
  promoteToHotTier(eventIds: string[]): Promise<PromotionResult>;
  demoteToColdTier(eventIds: string[]): Promise<DemotionResult>;

  // Tier monitoring
  getTierUsage(tierId: string): Promise<TierUsageStats>;
  getTierPerformance(tierId: string): Promise<TierPerformanceStats>;
  optimizeTierDistribution(): Promise<OptimizationResult>;
}

interface StorageTierConfig {
  name: string;
  type: 'hot' | 'warm' | 'cold' | 'archive';
  storage: StorageConfig;
  performance: PerformanceRequirements;
  retention: RetentionPolicy;
  cost: CostPolicy;
}

interface StorageConfig {
  type: 'database' | 'file' | 'object' | 'tape';
  connectionString?: string;
  parameters: Record<string, unknown>;
  encryption: EncryptionConfig;
  compression: CompressionConfig;
}
```

### Database Schema for Compaction

```sql
-- Compaction strategies table
CREATE TABLE compaction_strategies (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(255) NOT NULL UNIQUE,
  description TEXT,
  rules JSONB NOT NULL DEFAULT '[]',
  retention_policy JSONB NOT NULL DEFAULT '{}',
  archival_policy JSONB NOT NULL DEFAULT '{}',
  enabled BOOLEAN NOT NULL DEFAULT true,
  created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
  updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

-- Compaction jobs table
CREATE TABLE compaction_jobs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  strategy_id UUID REFERENCES compaction_strategies(id),
  status VARCHAR(50) NOT NULL DEFAULT 'pending',
  filter JSONB NOT NULL DEFAULT '{}',
  started_at TIMESTAMP WITH TIME ZONE,
  completed_at TIMESTAMP WITH TIME ZONE,
  events_processed INTEGER NOT NULL DEFAULT 0,
  events_compacted INTEGER NOT NULL DEFAULT 0,
  space_saved_bytes BIGINT NOT NULL DEFAULT 0,
  error_message TEXT,
  created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

-- Archive metadata table
CREATE TABLE event_archives (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(255) NOT NULL,
  destination_type VARCHAR(50) NOT NULL,
  destination_config JSONB NOT NULL DEFAULT '{}',
  event_count INTEGER NOT NULL,
  original_size_bytes BIGINT NOT NULL,
  compressed_size_bytes BIGINT NOT NULL,
  date_range_start TIMESTAMP WITH TIME ZONE NOT NULL,
  date_range_end TIMESTAMP WITH TIME ZONE NOT NULL,
  checksum VARCHAR(64) NOT NULL,
  retention_until TIMESTAMP WITH TIME ZONE NOT NULL,
  created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

-- Storage tiers table
CREATE TABLE storage_tiers (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name VARCHAR(255) NOT NULL UNIQUE,
  tier_type VARCHAR(20) NOT NULL,
  storage_config JSONB NOT NULL DEFAULT '{}',
  performance_config JSONB NOT NULL DEFAULT '{}',
  cost_config JSONB NOT NULL DEFAULT '{}',
  enabled BOOLEAN NOT NULL DEFAULT true,
  created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

-- Event location tracking table
CREATE TABLE event_locations (
  event_id VARCHAR(36) PRIMARY KEY,
  tier_id UUID REFERENCES storage_tiers(id),
  archive_id UUID REFERENCES event_archives(id),
  is_compacted BOOLEAN NOT NULL DEFAULT false,
  is_archived BOOLEAN NOT NULL DEFAULT false,
  moved_at TIMESTAMP WITH TIME ZONE,
  created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

-- Compaction audit log table
CREATE TABLE compaction_audit_log (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  job_id UUID REFERENCES compaction_jobs(id),
  event_id VARCHAR(36),
  action VARCHAR(100) NOT NULL,
  old_location VARCHAR(255),
  new_location VARCHAR(255),
  metadata JSONB NOT NULL DEFAULT '{}',
  timestamp TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);
```

## Implementation Tasks

### 1. Core Compaction Engine

- [ ] Create `packages/events/src/compaction/event-compactor.ts`
- [ ] Implement compaction strategy execution
- [ ] Add event filtering and processing logic
- [ ] Create compaction result tracking

### 2. Archival System

- [ ] Create `packages/events/src/compaction/event-archiver.ts`
- [ ] Implement multi-destination archival support
- [ ] Add compression and encryption
- [ ] Create archive integrity verification

### 3. Storage Tier Manager

- [ ] Create `packages/events/src/compaction/storage-tier-manager.ts`
- [ ] Implement tier-based storage management
- [ ] Add automated data movement
- [ ] Create tier optimization algorithms

### 4. Retention Policy Engine

- [ ] Create `packages/events/src/compaction/retention-engine.ts`
- [ ] Implement retention rule evaluation
- [ ] Add legal hold and preservation features
- [ ] Create compliance reporting

### 5. Performance Optimization

- [ ] Implement incremental compaction
- [ ] Add parallel processing capabilities
- [ ] Create compaction scheduling optimization
- [ ] Optimize storage usage patterns

### 6. Monitoring and Analytics

- [ ] Create compaction performance monitoring
- [ ] Add storage usage analytics
- [ ] Implement cost optimization tracking
- [ ] Create compliance dashboards

### 7. Testing

- [ ] Unit tests for compaction strategies
- [ ] Integration tests with archival systems
- [ ] Performance tests for large-scale compaction
- [ ] Compliance tests for retention policies

## Dependencies

### Internal Dependencies

- `@tamma/events` - Event store interface
- Event schema from Story 4-1
- Event store implementation from Story 4-2

### External Dependencies

- `aws-sdk` - S3 archival support
- `azure-storage-blob` - Azure archival support
- `@google-cloud/storage` - GCS archival support
- `node-cron` - Compaction scheduling

## Success Metrics

- Storage reduction: 60-80% reduction through compaction and archival
- Query performance: No degradation after compaction
- Compliance: 100% retention policy enforcement
- Cost savings: 50% reduction in storage costs
- Performance: Compaction overhead <5% of system resources

## Risks and Mitigations

### Data Loss

- **Risk**: Compaction may accidentally delete required data
- **Mitigation**: Implement verification, backup strategies, and legal hold features

### Performance Impact

- **Risk**: Compaction may impact system performance
- **Mitigation**: Implement background processing, resource limits, and intelligent scheduling

### Compliance Violations

- **Risk**: Compaction may violate retention requirements
- **Mitigation**: Implement strict policy enforcement, audit logging, and compliance checks

## Notes

Event compaction is essential for managing the long-term storage requirements of an event sourcing system while maintaining the audit trail capabilities required for compliance. The compaction system must balance storage efficiency with performance and compliance requirements.

The archival and tiering strategies enable cost-effective long-term storage while maintaining the ability to access historical data when needed for debugging, analysis, or compliance purposes.
