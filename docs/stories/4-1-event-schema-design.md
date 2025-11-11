# Story 4.1: Event Schema Design

Status: ready-for-dev

## Story

As a **system architect**,
I want to design a comprehensive event schema covering all system actions and state changes,
so that event sourcing captures complete system history for audit compliance and debugging.

## Acceptance Criteria

1. Event schema defines base fields: `eventId`, `timestamp`, `eventType`, `actorType`, `actorId`, `payload`, `metadata`
2. Schema includes event types for: issue selection, AI requests/responses, code changes, Git operations, approvals, escalations, errors
3. Schema supports event versioning (schema version field) for future evolution
4. Schema includes correlation IDs for linking related events (e.g., all events for single PR)
5. Schema validated with JSON Schema or Protocol Buffers
6. Documentation includes event catalog with examples for each event type

## Tasks / Subtasks

- [ ] Task 1: Design base event schema structure (AC: 1)
  - [ ] Subtask 1.1: Define base Event interface with required fields
  - [ ] Subtask 1.2: Define EventMetadata interface for versioning and system info
  - [ ] Subtask 1.3: Create TypeScript type definitions for all event structures
  - [ ] Subtask 1.4: Design event ID generation strategy (UUID v7 for time-sortable)

- [ ] Task 2: Define comprehensive event type catalog (AC: 2)
  - [ ] Subtask 2.1: Define IssueEvents (IssueSelected, IssueAnalysisCompleted, IssueAssigned)
  - [ ] Subtask 2.2: Define AIEvents (AIRequest, AIResponse, AIError, ProviderSelected)
  - [ ] Subtask 2.3: Define CodeEvents (FileWritten, FileDeleted, CommitCreated)
  - [ ] Subtask 2.4: Define GitEvents (BranchCreated, PRCreated, PRMerged, PRUpdated)
  - [ ] Subtask 2.5: Define ApprovalEvents (ApprovalRequested, ApprovalProvided, ApprovalRejected)
  - [ ] Subtask 2.6: Define SystemEvents (EscalationTriggered, EscalationResolved, ErrorOccurred)
  - [ ] Subtask 2.7: Define ConfigurationEvents (ConfigChanged, ProviderAdded, PlatformAdded)

- [ ] Task 3: Implement event versioning strategy (AC: 3)
  - [ ] Subtask 3.1: Add schemaVersion field to base event structure
  - [ ] Subtask 3.2: Design backward compatibility strategy for event schema evolution
  - [ ] Subtask 3.3: Create event migration utilities for schema upgrades
  - [ ] Subtask 3.4: Document versioning policy and breaking change guidelines

- [ ] Task 4: Design correlation and linking system (AC: 4)
  - [ ] Subtask 4.1: Define correlationId field for event grouping
  - [ ] Subtask 4.2: Define causationId field for event causality chains
  - [ ] Subtask 4.3: Design event aggregation patterns for complex workflows
  - [ ] Subtask 4.4: Create correlation ID generation and propagation utilities

- [ ] Task 5: Implement schema validation (AC: 5)
  - [ ] Subtask 5.1: Create JSON Schema definitions for all event types
  - [ ] Subtask 5.2: Implement event validation middleware
  - [ ] Subtask 5.3: Add Protocol Buffer definitions for high-performance scenarios
  - [ ] Subtask 5.4: Create validation error handling and reporting

- [ ] Task 6: Create comprehensive event documentation (AC: 6)
  - [ ] Subtask 6.1: Document each event type with field descriptions
  - [ ] Subtask 6.2: Create event flow diagrams for common workflows
  - [ ] Subtask 6.3: Provide example payloads for each event type
  - [ ] Subtask 6.4: Create event catalog reference documentation

## Dev Notes

### Requirements Context Summary

**Epic 4 Foundation:** This story establishes the foundational event schema that enables complete audit trail and time-travel debugging capabilities. The schema must capture all autonomous loop activities for compliance (SOC2, ISO27001, GDPR) and system observability.

**DCB Pattern Implementation:** The schema supports Dynamic Consistency Boundary event sourcing with flexible JSONB tagging for efficient querying. Events must be immutable, append-only, and contain sufficient context for state reconstruction.

**Compliance Requirements:** Event schema must support audit trail requirements including: who did what when, why decisions were made, what data was accessed, and what changes were made. All events must be tamper-evident and have non-repudiation properties.

### Implementation Guidance

**Core Event Structure:**

```typescript
interface BaseEvent {
  eventId: string; // UUID v7 (time-sortable)
  timestamp: string; // ISO 8601 with millisecond precision
  eventType: string; // EVENT_TYPE.SUBTYPE.ACTION format
  actorType: 'user' | 'system' | 'ai' | 'service';
  actorId: string; // User ID, system component, or AI provider
  correlationId: string; // Groups related events (workflow instance)
  causationId?: string; // Causal chain linking
  schemaVersion: string; // Event schema version
  payload: Record<string, unknown>; // Event-specific data
  metadata: {
    source: 'orchestrator' | 'worker' | 'api' | 'cli';
    version: string; // Tamma version
    environment: string; // dev/staging/prod
    tags?: Record<string, string>; // Flexible JSONB tags
  };
}
```

**Event Type Naming Convention:**

- Pattern: `AGGREGATE.ACTION.STATUS` (e.g., `ISSUE.SELECTED.SUCCESS`)
- Aggregates: ISSUE, AI, CODE, GIT, APPROVAL, SYSTEM, CONFIG
- Actions: SELECTED, REQUESTED, CREATED, UPDATED, DELETED, MERGED
- Status: SUCCESS, FAILED, PENDING, TIMEOUT, RETRY

**Correlation Strategy:**

- Workflow-level correlation ID for entire autonomous loop
- Step-level correlation ID for individual operations
- Causation IDs for event chains and retry loops
- Tag-based filtering for cross-cutting concerns

**Versioning Strategy:**

- Semantic versioning for schema (1.0.0, 1.1.0, 2.0.0)
- Backward compatibility for minor versions
- Migration utilities for major version changes
- Deprecation warnings for obsolete fields

### Technical Specifications

**Event Categories and Examples:**

1. **Issue Events:**
   - `ISSUE.SELECTED.SUCCESS`: Issue chosen for processing
   - `ISSUE.ANALYSIS.COMPLETED`: Context analysis finished
   - `ISSUE.ASSIGNED.SUCCESS`: Issue assigned to autonomous loop

2. **AI Events:**
   - `AI.REQUEST.STARTED`: Request sent to AI provider
   - `AI.RESPONSE.RECEIVED`: Response received from provider
   - `AI.PROVIDER.SELECTED`: Provider chosen for specific task

3. **Code Events:**
   - `CODE.FILE.CREATED`: New file written
   - `CODE.FILE.UPDATED`: Existing file modified
   - `CODE.COMMIT.CREATED`: Git commit created

4. **Git Events:**
   - `GIT.BRANCH.CREATED`: Feature branch created
   - `GIT.PULL_REQUEST.CREATED`: PR opened
   - `GIT.PULL_REQUEST.MERGED`: PR merged to main

5. **Approval Events:**
   - `APPROVAL.REQUESTED`: Human approval needed
   - `APPROVAL.PROVIDED`: User decision recorded
   - `APPROVAL.REJECTED`: User rejected proposal

6. **System Events:**
   - `SYSTEM.ESCALATION.TRIGGERED`: Automatic escalation
   - `SYSTEM.ERROR.OCCURRED`: System error
   - `SYSTEM.RETRY.EXHAUSTED`: Max retries reached

**Schema Validation:**

- JSON Schema for runtime validation
- TypeScript compile-time type checking
- Protocol Buffers for high-performance serialization
- Event schema registry for version management

**Performance Considerations:**

- Event size optimization (truncate large payloads, use blob storage)
- Efficient indexing strategy for common queries
- Compression for long-term storage
- Partitioning by time for scalability

### Dependencies

**Internal Dependencies:**

- None (foundational story for Epic 4)
- Epic 1-3 stories provide context for event types

**External Dependencies:**

- JSON Schema validation library
- UUID v7 generation library
- Protocol Buffers compiler (optional)
- Event schema registry service

### Risks and Mitigations

| Risk                        | Severity | Mitigation                                      |
| --------------------------- | -------- | ----------------------------------------------- |
| Schema evolution complexity | High     | Strict versioning policy, migration utilities   |
| Event size bloat            | Medium   | Payload truncation, blob storage for large data |
| Performance impact          | Medium   | Efficient indexing, compression, partitioning   |
| Compliance gaps             | High     | Legal review, audit trail validation            |

### Success Metrics

- [ ] Event schema covers 100% of system actions
- [ ] Schema validation passes for all event types
- [ ] Event documentation completeness: 100%
- [ ] Schema versioning strategy tested and documented
- [ ] Performance: event validation < 1ms per event

## Related

- Related story: `docs/stories/4-2-event-store-backend-selection.md`
- Related epic: `docs/epics.md` (Epic 4)
- Technical specification: `docs/tech-spec-epic-4.md`
- Architecture: `docs/architecture.md` (DCB Event Sourcing section)

## References

- [JSON Schema Specification](https://json-schema.org/)
- [UUID v7 Specification](https://datatracker.ietf.org/doc/html/draft-peabody-dispatch-new-uuid-format-04)
- [Event Sourcing Pattern](https://martinfowler.com/eaaDev/EventSourcing.html)
- [DCB Pattern Documentation](https://github.com/tamma/tamma/wiki/DCB-Pattern)
