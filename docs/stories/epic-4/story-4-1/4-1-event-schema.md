# Story 4-1: Event Schema Definition

## Overview

Define and implement the comprehensive event schema for the DCB (Dynamic Consistency Boundary) event sourcing system that will serve as the foundation for Tamma's audit trail and time-travel debugging capabilities.

## Acceptance Criteria

### Core Event Schema

- [ ] Define `DomainEvent` interface with all required fields (id, type, timestamp, tags, metadata, data)
- [ ] Implement event type naming convention (AGGREGATE.ACTION.STATUS pattern)
- [ ] Create JSON schema validation for event structure
- [ ] Support UUID v7 for time-sortable event IDs
- [ ] Implement ISO 8601 millisecond precision timestamps

### Event Tagging System

- [ ] Design flexible JSONB tagging schema for query optimization
- [ ] Implement standard tag sets for different event types
- [ ] Support custom tags for extensibility
- [ ] Create tag indexing strategy for performance

### Event Type Registry

- [ ] Create comprehensive event type definitions for all system operations
- [ ] Implement event type validation and enumeration
- [ ] Support versioning of event schemas
- [ ] Create event type documentation generator

### Metadata Management

- [ ] Define metadata structure for workflow versioning
- [ ] Implement event source tracking (system vs plugin)
- [ ] Support correlation IDs for distributed tracing
- [ ] Create metadata enrichment pipeline

## Technical Context

### Event Schema Structure

Based on the DCB pattern from the architecture, events must follow this structure:

```typescript
interface DomainEvent {
  id: string; // UUID v7 (time-sortable)
  type: string; // "AGGREGATE.ACTION.STATUS"
  timestamp: string; // ISO 8601 millisecond precision
  tags: {
    // JSONB for flexible queries
    issueId?: string;
    prId?: string;
    userId?: string;
    mode?: 'dev' | 'business';
    provider?: string;
    platform?: string;
    [key: string]: string | undefined;
  };
  metadata: {
    workflowVersion: string;
    eventSource: 'system' | 'plugin';
    correlationId?: string;
    causationId?: string;
  };
  data: Record<string, unknown>;
}
```

### Event Type Categories

From the architecture document, we need to support these event categories:

**Issue Management Events:**

- ISSUE.ASSIGNED.SUCCESS
- ISSUE.UPDATED.SUCCESS
- ISSUE.COMMENT.ADDED
- ISSUE.LABELED.CHANGED

**Code Generation Events:**

- CODE.GENERATED.SUCCESS
- CODE.GENERATED.FAILED
- CODE.REVIEW.REQUESTED
- CODE.MERGED.SUCCESS

**Workflow Events:**

- WORKFLOW.STARTED
- WORKFLOW.STEP_COMPLETED
- WORKFLOW.PAUSED
- WORKFLOW.RESUMED
- WORKFLOW.COMPLETED

**Quality Gate Events:**

- GATE.BUILD.STARTED
- GATE.TEST.STARTED
- GATE.SECURITY.SCAN.STARTED
- GATE.REVIEW.REQUESTED

**Plugin Events:**

- PLUGIN.INSTALLED.SUCCESS
- PLUGIN.UNINSTALLED.SUCCESS
- PLUGIN.DEBUG_SNAPSHOT.SUCCESS

**Provider Events:**

- PROVIDER.API.CALL
- PROVIDER.API.SUCCESS
- PROVIDER.API.FAILED
- PROVIDER.QUOTA.EXCEEDED

**Platform Events:**

- PLATFORM.API.CALL
- PLATFORM.SUCCESS
- PLATFORM.FAILED
- PLATFORM.RATE_LIMITED

### Database Schema

Events will be stored in PostgreSQL with this schema:

```sql
CREATE TABLE events (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  event_id VARCHAR(36) NOT NULL UNIQUE,        -- UUID v7
  event_type VARCHAR(255) NOT NULL,
  timestamp TIMESTAMP WITH TIME ZONE NOT NULL,
  tags JSONB NOT NULL DEFAULT '{}',
  metadata JSONB NOT NULL DEFAULT '{}',
  data JSONB NOT NULL DEFAULT '{}',
  created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),

  -- Indexes for performance
  INDEX idx_events_timestamp (timestamp),
  INDEX idx_events_type (event_type),
  INDEX idx_events_tags (tags) USING GIN,
  INDEX idx_events_metadata (metadata) USING GIN,
  INDEX idx_events_issue_id ((tags->>'issueId')),
  INDEX idx_events_pr_id ((tags->>'prId'))
);
```

### Validation Requirements

- All events must pass JSON schema validation
- Required fields must be present and valid
- Event types must be registered in the type system
- Timestamps must be valid ISO 8601 with millisecond precision
- UUID v7 format validation for event IDs

## Implementation Tasks

### 1. Core Schema Definition

- [ ] Create `packages/events/src/schemas/event.schema.ts`
- [ ] Implement TypeScript interfaces for all event types
- [ ] Create JSON schema files for validation
- [ ] Implement event type registry

### 2. Validation System

- [ ] Create event validation service
- [ ] Implement schema validation using AJV
- [ ] Add custom validators for UUID v7 and timestamps
- [ ] Create validation error handling

### 3. Type System

- [ ] Define event type constants and enums
- [ ] Create event type builder utilities
- [ ] Implement event type versioning
- [ ] Create documentation generator

### 4. Database Integration

- [ ] Create database migration for events table
- [ ] Implement event storage service
- [ ] Add database indexes for performance
- [ ] Create event query builders

### 5. Testing

- [ ] Unit tests for schema validation
- [ ] Integration tests for database operations
- [ ] Performance tests for event insertion/querying
- [ ] Schema compatibility tests

## Dependencies

### Internal Dependencies

- `@tamma/shared` - Shared types and utilities
- Database migration system (to be implemented)

### External Dependencies

- `ajv` - JSON schema validation
- `uuid` - UUID generation and validation
- `pg` - PostgreSQL client

## Success Metrics

- All events pass schema validation with 100% compliance
- Event insertion performance: >10,000 events/second
- Query performance: <100ms for common tag-based queries
- Zero data loss in event storage
- Complete audit trail coverage for all operations

## Risks and Mitigations

### Performance Risks

- **Risk**: Event volume may impact database performance
- **Mitigation**: Implement proper indexing, consider partitioning by time

### Schema Evolution

- **Risk**: Event schema changes may break compatibility
- **Mitigation**: Implement versioning, backward compatibility checks

### Data Integrity

- **Risk**: Invalid events may corrupt the audit trail
- **Mitigation**: Strict validation, immutable event storage

## Notes

This story is foundational for the entire event sourcing system. All subsequent stories will depend on the event schema defined here. The schema must be designed to support the full lifecycle of autonomous development workflows while maintaining auditability and debuggability requirements.

The event schema directly supports compliance requirements (SOC2, ISO27001, GDPR) by providing a complete, immutable audit trail of all system actions.
