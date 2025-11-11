# Story 4.1: Event Schema Design

## Overview

Design a comprehensive event schema covering all system actions and state changes for the DCB (Dynamic Consistency Boundary) event sourcing system that will serve as the foundation for Tamma's complete audit trail and time-travel debugging capabilities.

## Acceptance Criteria

### Core Event Schema Structure

- [ ] Event schema defines base fields: `eventId`, `timestamp`, `eventType`, `actorType`, `actorId`, `payload`, `metadata`
- [ ] Schema includes event types for: issue selection, AI requests/responses, code changes, Git operations, approvals, escalations, errors
- [ ] Schema supports event versioning (schema version field) for future evolution
- [ ] Schema includes correlation IDs for linking related events (e.g., all events for single PR)
- [ ] Schema validated with JSON Schema or Protocol Buffers
- [ ] Documentation includes event catalog with examples for each event type

## Technical Context

### Base Event Structure

Based on the DCB pattern from the architecture, events must follow this structure:

```typescript
interface DomainEvent {
  eventId: string; // UUID v7 (time-sortable)
  timestamp: string; // ISO 8601 millisecond precision
  eventType: string; // "AGGREGATE.ACTION" pattern
  actorType: 'system' | 'user' | 'ai' | 'provider' | 'platform';
  actorId: string; // ID of the entity that performed the action
  payload: Record<string, unknown>; // Event-specific data
  metadata: {
    schemaVersion: string; // Event schema version for evolution
    correlationId?: string; // Links related events in a workflow
    causationId?: string; // Links to the event that caused this one
    workflowId?: string; // Workflow instance identifier
    issueId?: string; // Related issue ID
    prId?: string; // Related PR ID
    [key: string]: unknown;
  };
}
```

### Event Type Categories

From the epic specification, we need to support these event categories:

**Issue Management Events:**

- `IssueSelected` - When an issue is selected for development
- `IssueAnalysisCompleted` - When issue analysis is finished
- `IssueContextUpdated` - When issue context is modified

**AI Provider Events:**

- `AIRequest` - Before each AI provider call
- `AIResponse` - After AI provider response
- `AIProviderSelected` - When provider is chosen for a task
- `AICostTracked` - For cost tracking and quota management

**Code Change Events:**

- `CodeFileWritten` - When a file is created/updated/deleted
- `CodeFileRead` - When source code is read for analysis
- `CodeGenerationCompleted` - When code generation is finished

**Git Operation Events:**

- `BranchCreated` - When a new branch is created
- `CommitCreated` - When a commit is made
- `PRCreated` - When a pull request is created
- `PRUpdated` - When PR is updated
- `PRMerged` - When PR is merged

**Quality Gate Events:**

- `QualityGateStarted` - When a quality gate begins
- `QualityGatePassed` - When a quality gate succeeds
- `QualityGateFailed` - When a quality gate fails
- `QualityGateEscalated` - When human intervention is required

**System Events:**

- `WorkflowStarted` - When a workflow begins
- `WorkflowStepCompleted` - When a workflow step finishes
- `WorkflowCompleted` - When workflow ends successfully
- `WorkflowFailed` - When workflow fails
- `ErrorOccurred` - When any system error occurs

### Event Payload Schemas

Each event type will have a specific payload schema:

```typescript
// Issue Selection Events
interface IssueSelectedPayload {
  issueId: string;
  title: string;
  description: string;
  labels: string[];
  assignee?: string;
  priority: 'low' | 'medium' | 'high' | 'critical';
  selectionCriteria: {
    labels?: string[];
    assignee?: string;
    priority?: string;
    customFilters?: Record<string, unknown>;
  };
}

// AI Provider Events
interface AIRequestPayload {
  providerName: string;
  model: string;
  prompt: string; // Truncated if >1000 chars for main event
  promptLength: number;
  estimatedTokens: number;
  requestType: 'code_generation' | 'analysis' | 'review' | 'planning';
  contextSummary: string;
}

interface AIResponsePayload {
  providerName: string;
  model: string;
  response: string; // Truncated for main event
  responseLength: number;
  actualTokens: number;
  latency: number; // Response time in milliseconds
  costEstimate: number;
  success: boolean;
  errorMessage?: string;
}

// Code Change Events
interface CodeFileWrittenPayload {
  filePath: string;
  changeType: 'create' | 'update' | 'delete';
  fileSize: number;
  linesAdded: number;
  linesRemoved: number;
  language: string;
  encoding: string;
}

// Git Operation Events
interface CommitCreatedPayload {
  commitSha: string;
  message: string;
  branchName: string;
  fileCount: number;
  author: {
    name: string;
    email: string;
  };
  parentCommits: string[];
}

interface PRCreatedPayload {
  prNumber: number;
  prUrl: string;
  title: string;
  description: string;
  baseBranch: string;
  headBranch: string;
  author: string;
  reviewers: string[];
  labels: string[];
}
```

### Schema Versioning Strategy

Events must support schema evolution:

```typescript
interface EventMetadata {
  schemaVersion: string; // "1.0.0", "1.1.0", etc.
  correlationId?: string; // UUID linking workflow events
  causationId?: string; // UUID linking cause-effect
  workflowId?: string; // Workflow instance
  issueId?: string; // Related issue
  prId?: string; // Related PR
  createdAt: string; // Event creation timestamp
  source: 'orchestrator' | 'worker' | 'cli' | 'api';
  version: string; // Event format version
}
```

### Validation Requirements

- All events must pass JSON schema validation
- Required fields must be present and valid
- Event types must be registered in the type system
- Timestamps must be valid ISO 8601 with millisecond precision
- UUID v7 format validation for event IDs
- Correlation and causation IDs must be valid UUIDs when present

## Implementation Tasks

### 1. Schema Definition

- [ ] Create `packages/events/src/schemas/domain-event.schema.ts`
- [ ] Define TypeScript interfaces for all event types
- [ ] Create JSON schema files for validation
- [ ] Implement event type registry with versioning

### 2. Validation System

- [ ] Create event validation service using AJV
- [ ] Implement schema version compatibility checks
- [ ] Add custom validators for UUID v7 and timestamps
- [ ] Create validation error handling with detailed messages

### 3. Event Type Registry

- [ ] Define event type constants and enums
- [ ] Create event type builder utilities
- [ ] Implement event type versioning support
- [ ] Create event catalog documentation generator

### 4. Payload Schemas

- [ ] Define payload schemas for all event categories
- [ ] Implement payload validation for each event type
- [ ] Create payload transformation utilities
- [ ] Add payload truncation logic for large data

### 5. Documentation

- [ ] Create comprehensive event catalog
- [ ] Document event relationships and correlation patterns
- [ ] Provide examples for each event type
- [ ] Create schema evolution guidelines

## Dependencies

### Internal Dependencies

- `@tamma/shared` - Shared types and utilities
- Validation system (to be implemented in later stories)

### External Dependencies

- `ajv` - JSON schema validation
- `uuid` - UUID generation and validation
- `@types/uuid` - TypeScript definitions

## Success Metrics

- All events pass schema validation with 100% compliance
- Event schema supports all identified use cases
- Schema versioning allows backward-compatible evolution
- Event catalog provides complete documentation
- Validation performance: <1ms per event validation

## Risks and Mitigations

### Schema Evolution Risks

- **Risk**: Event schema changes may break compatibility
- **Mitigation**: Implement strict versioning, backward compatibility checks

### Performance Risks

- **Risk**: Complex validation may impact performance
- **Mitigation**: Optimize validation logic, consider async validation for non-critical fields

### Completeness Risks

- **Risk**: Missing event types for edge cases
- **Mitigation**: Comprehensive event discovery, extensible event registry

## Notes

This story establishes the foundational event schema for the entire event sourcing system. All subsequent stories will depend on the event schema defined here. The schema must be designed to support the full lifecycle of autonomous development workflows while maintaining auditability and debuggability requirements.

The event schema directly supports compliance requirements (SOC2, ISO27001, GDPR) by providing a complete, structured audit trail of all system actions with proper correlation and causation tracking.
