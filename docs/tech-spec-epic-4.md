# Epic Technical Specification: Event Sourcing & Audit Trail

Date: 2025-10-28
Author: meywd
Epic ID: 4
Status: Draft

---

## Overview

Epic 4 implements the Event Sourcing & Audit Trail foundation that provides complete transparency, auditability, and time-travel debugging capabilities for Tamma's autonomous development workflows. Building on Epic 1's multi-provider abstractions, Epic 2's workflow orchestration, and Epic 3's quality gates and intelligence layer, this epic establishes the **DCB (Dynamic Consistency Boundary) pattern** event sourcing architecture that captures 100% of system operations—user actions, AI provider interactions, code changes, Git operations, approvals, escalations, and errors—as immutable events with millisecond-precision timestamps (PRD FR-20, FR-21).

Unlike traditional logging or metrics systems that provide post-hoc observability, Tamma's event sourcing architecture enables **deterministic replay and state reconstruction** at any point in time (FR-22), transforming debugging from "read logs and guess" to "replay exactly what happened and see why." This capability is critical for autonomous systems where understanding AI decision-making requires complete audit trails. The DCB pattern—a single event stream with JSONB tags instead of separate streams per aggregate—simplifies cross-aggregate queries (e.g., "show all events for PR #123 from issue selection through merge") and provides flexible tagging for multi-perspective reads without schema migrations. Epic 4 delivers the transparency foundation required for production adoption: compliance officers get complete audit trails for SOC2/ISO27001/GDPR certification (FR-24), developers get time-travel debugging for root cause analysis (FR-23), and AI governance teams get complete AI interaction history for cost tracking and decision auditing (Story 4.4).

## MVP Scope Clarification

**Epic 4 has PARTIAL MVP scope** - core event capture for debugging is MVP critical, while advanced features (full replay, AI interaction capture) are optional:

**MVP CRITICAL (3 stories):**
- **Story 4.1 (Event Schema)**: Foundational - defines event structure used throughout system
- **Story 4.2 (Event Store Backend)**: Essential infrastructure for event persistence
- **Story 4.3 (Event Capture - Issue Selection)**: Basic event capture for workflow debugging, enables correlation tracking

**MVP OPTIONAL (5 stories - Post-MVP):**
- **Story 4.4 (AI Interaction Capture)**: Advanced debugging - full AI prompt/response history nice-to-have but not critical for self-maintenance MVP
- **Story 4.5 (Code Changes Capture)**: Git provides commit history, event capture adds convenience but not essential
- **Story 4.6 (Approvals/Escalations Capture)**: Captured in logs, event store adds structured queryability (post-MVP enhancement)
- **Story 4.7 (Event Query API)**: Advanced feature for time-travel queries, basic log analysis sufficient for MVP debugging
- **Story 4.8 (Black-Box Replay)**: Advanced debugging feature, valuable for production but not required for MVP self-maintenance validation

**Rationale**: Self-maintenance goal requires basic event capture for debugging workflows (Stories 4.1-4.3 provide correlation tracking and workflow visibility). Advanced features (4.4-4.8) improve debugging experience but aren't blockers for validating Tamma can maintain itself. Logs + basic events + metrics (Epic 5) sufficient for MVP debugging needs.

**Note**: Stories 4.4-4.8 can be implemented post-MVP or by Tamma itself as self-maintenance validation (Tamma implements its own advanced debugging features).

## Objectives and Scope

**In Scope:**

- **Story 4.1:** Event Schema Design - Comprehensive event schema with base fields (eventId, timestamp, type, actor, payload, metadata), event types for all system operations, versioning support, correlation IDs, JSON Schema validation, event catalog documentation
- **Story 4.2:** Event Store Backend Selection - Append-only event store with ordered reads, filtering by type/actor/correlation, high write throughput (100+ events/sec), multiple backend support (local file for dev, PostgreSQL for prod), configurable retention policies
- **Story 4.3:** Event Capture - Issue Selection & Analysis - Capture IssueSelectedEvent and IssueAnalysisCompletedEvent from Epic 2 workflows, correlation IDs for development cycles, event write retry (3 attempts) with halt on failure
- **Story 4.4:** Event Capture - AI Provider Interactions - Capture AIRequestEvent and AIResponseEvent for all AI calls, full prompt/response in blob storage, sensitive data masking, provider selection rationale, synchronous persistence
- **Story 4.5:** Event Capture - Code Changes & Git Operations - Capture CodeFileWrittenEvent, CommitCreatedEvent, BranchCreatedEvent, PRCreatedEvent, PRMergedEvent, file diffs in blob storage, actor attribution (user vs autonomous)
- **Story 4.6:** Event Capture - Approvals & Escalations - Capture ApprovalRequestedEvent, ApprovalProvidedEvent, EscalationTriggeredEvent, EscalationResolvedEvent, approval audit trail for compliance, multiple channel support (CLI, API, webhook)
- **Story 4.7:** Event Query API for Time-Travel - REST API with time range and filter queries, chronological ordering with pagination, projection queries ("state at timestamp T"), efficient indexing (<1s for 1M events), authentication required
- **Story 4.8:** Black-Box Replay for Debugging - CLI command for state reconstruction at any timestamp, step-by-step interactive replay, HTML report export with diff views, complete reconstruction in <5 seconds for typical cycles (50-100 events)

**Out of Scope:**

- Event-driven notifications and webhooks (Epic 5 - alert system will consume events)
- Real-time event streaming dashboard UI (Epic 5 - Event Trail Exploration UI)
- Event analytics and trend analysis (Epic 5 - development velocity metrics derived from events)
- Event schema migration automation (manual migration scripts acceptable for MVP)
- Advanced blob storage backends beyond local filesystem (S3/Azure Blob deferred to production phase)
- Event encryption at rest (PostgreSQL transparent data encryption acceptable for MVP)
- Multi-tenancy event isolation (single-tenant deployment assumed for MVP)
- Event compression for long-term storage (optimize post-MVP based on actual data volumes)
- GraphQL query API for events (REST API sufficient for MVP, GraphQL deferred to extensibility phase)

## System Architecture Alignment

Epic 4 implements the `packages/events` package as defined in Architecture section 4.3 (Project Structure), establishing the event sourcing foundation with the **DCB (Dynamic Consistency Boundary) pattern** specified in section 3.1 (Novel Patterns). The DCB approach uses a **single event stream with JSONB tags** stored in PostgreSQL 17 (section 2.1 Technology Stack), enabling flexible multi-perspective queries without complex aggregate-per-stream management. Event IDs use UUID v7 (time-sortable) for chronological ordering without explicit sequence numbers, and GIN indexes on JSONB `tags` fields enable efficient filtering by `issueId`, `prId`, `buildId`, `actorId`, and arbitrary custom tags.

The event store backend (Story 4.2) leverages **Emmett 0.23+** (section 2.1) for PostgreSQL-based event sourcing with native DCB support, implementing append-only writes through the `events` table schema defined in Architecture section 10.1 (Database Schema). For local development, a file-based event store provides zero-dependency operation, writing line-delimited JSON to `~/.Tamma/events/stream.jsonl` with atomic append semantics. The backend abstraction layer (`EventStoreInterface`) follows Epic 1's plugin pattern, allowing future event store implementations (EventStoreDB, Apache Kafka) without workflow changes.

Event capture integration points (Stories 4.3-4.6) instrument Epic 2's workflow orchestration engine and Epic 3's quality gates through the event emission pattern: each service emits events synchronously to the event store before proceeding to the next step, ensuring events are never lost due to crashes or interruptions. The correlation ID mechanism uses Epic 2's workflow execution ID as the root correlation, linking all events from initial issue selection (Story 2.1) through final PR merge (Story 2.10) in a single queryable chain. Sensitive data masking (Story 4.4) integrates with Epic 1's credential management system (`packages/config/secrets`), automatically detecting and redacting API keys, passwords, and tokens before event persistence.

The Event Query API (Story 4.7) exposes a Fastify 5.2+ REST endpoint at `/api/v1/events` (section 5.2 API Design), implementing CQRS read models through projections built from the event stream. Projections materialize frequently-accessed views (e.g., "current PR state") to avoid full event replay on every query, with eventual consistency guarantees (typically <100ms lag). Authentication uses Epic 1's JWT-based auth middleware, restricting event access to authenticated users with appropriate permissions (admin role for cross-user event queries, standard role for own events only).

The Black-Box Replay system (Story 4.8) implements deterministic state reconstruction by replaying events through a read-only projection engine, avoiding side effects like re-triggering AI calls or Git operations. The CLI command `Tamma replay` integrates with Epic 1's CLI scaffolding (Story 1.9), using the same configuration system for event store connection. HTML report generation uses a lightweight templating system (Handlebars) to produce self-contained reports with embedded CSS, enabling offline viewing and sharing via email or collaboration tools.

## Detailed Design

### Services and Modules

**1. Event Store Core (`packages/events/src/event-store.ts`)**

*Responsibilities:*
- Append-only event persistence with atomic writes
- Event retrieval by ID, time range, filters
- Backend abstraction (PostgreSQL, file-based)
- Connection pooling and transaction management

*Interface:*
```typescript
interface EventStoreInterface {
  append(event: DomainEvent): Promise<void>;
  appendBatch(events: DomainEvent[]): Promise<void>;
  getEvents(query: EventQuery): Promise<EventPage>;
  getEventById(eventId: string): Promise<DomainEvent | null>;
  streamEvents(query: EventQuery): AsyncIterable<DomainEvent>;
}

interface EventQuery {
  since?: Date;
  until?: Date;
  types?: string[];
  tags?: Record<string, string>;
  limit?: number;
  offset?: number;
}

interface EventPage {
  events: DomainEvent[];
  total: number;
  hasMore: boolean;
  nextOffset?: number;
}
```

*Owner:* Epic 4 (Stories 4.1, 4.2)

**2. Event Schema Registry (`packages/events/src/schemas.ts`)**

*Responsibilities:*
- Event type definitions with JSON Schema validation
- Schema versioning and migration support
- Event catalog documentation generation
- Type-safe event builders

*Exports:*
```typescript
// Base event structure
interface DomainEvent {
  id: string;                    // UUID v7 (time-sortable)
  type: string;                  // "ISSUE.SELECTED" | "AI.REQUEST" | etc.
  timestamp: string;             // ISO 8601 with milliseconds
  schemaVersion: string;         // "1.0.0" for versioning

  actor: {
    type: 'user' | 'system' | 'ai-provider';
    id: string;                  // User ID, system component, provider name
    metadata?: Record<string, unknown>;
  };

  tags: Record<string, string>;  // DCB tags for multi-perspective reads
  payload: unknown;              // Event-specific data
  metadata?: Record<string, unknown>; // Context, trace IDs, etc.
}

// Event type registry
const EventTypes = {
  // Issue lifecycle (Story 4.3)
  ISSUE_SELECTED: 'ISSUE.SELECTED',
  ISSUE_ANALYSIS_COMPLETED: 'ISSUE.ANALYSIS.COMPLETED',

  // AI interactions (Story 4.4)
  AI_REQUEST_STARTED: 'AI.REQUEST.STARTED',
  AI_RESPONSE_RECEIVED: 'AI.RESPONSE.RECEIVED',

  // Code changes (Story 4.5)
  CODE_FILE_WRITTEN: 'CODE.FILE.WRITTEN',
  COMMIT_CREATED: 'COMMIT.CREATED',
  BRANCH_CREATED: 'BRANCH.CREATED',
  PR_CREATED: 'PR.CREATED',
  PR_MERGED: 'PR.MERGED',

  // Approvals (Story 4.6)
  APPROVAL_REQUESTED: 'APPROVAL.REQUESTED',
  APPROVAL_PROVIDED: 'APPROVAL.PROVIDED',
  ESCALATION_TRIGGERED: 'ESCALATION.TRIGGERED',
  ESCALATION_RESOLVED: 'ESCALATION.RESOLVED',
} as const;
```

*Owner:* Epic 4 (Story 4.1)

**3. Event Emitters (`packages/events/src/emitters/`)**

*Responsibilities:*
- Instrumentation hooks for Epic 2 workflows, Epic 3 quality gates
- Automatic correlation ID propagation
- Synchronous event persistence with retry
- Error handling and fallback logging

*Integration Points:*
- `WorkflowOrchestrator` (Epic 2) - Issue selection, plan generation, code implementation
- `BuildOrchestrator` (Epic 3) - Build triggers, test execution
- `AIProviderInterface` (Epic 1) - AI request/response interception
- `GitPlatformInterface` (Epic 1) - Git operation interception

*Example Usage:*
```typescript
// In WorkflowOrchestrator (Epic 2)
class WorkflowOrchestrator {
  constructor(
    private eventStore: EventStoreInterface,
    private correlationIdProvider: CorrelationIdProvider
  ) {}

  async selectIssue(issueId: number): Promise<Issue> {
    const correlationId = this.correlationIdProvider.generate();
    const issue = await this.gitPlatform.getIssue(issueId);

    // Emit event synchronously before proceeding
    await this.eventStore.append({
      id: uuidv7(),
      type: EventTypes.ISSUE_SELECTED,
      timestamp: new Date().toISOString(),
      schemaVersion: '1.0.0',
      actor: { type: 'system', id: 'workflow-orchestrator' },
      tags: {
        issueId: issueId.toString(),
        correlationId,
        repo: issue.repository,
      },
      payload: {
        issueId,
        title: issue.title,
        labels: issue.labels,
        selectionCriteria: 'auto-next',
      },
    });

    return issue;
  }
}
```

*Owner:* Epic 4 (Stories 4.3-4.6)

**4. Event Query Service (`packages/events/src/query-service.ts`)**

*Responsibilities:*
- REST API endpoint implementation
- Projection management and materialization
- Query optimization and caching
- Authentication and authorization

*API Endpoints:*
```typescript
// GET /api/v1/events
interface GetEventsRequest {
  since?: string;        // ISO 8601 timestamp
  until?: string;        // ISO 8601 timestamp
  type?: string;         // Event type filter
  correlationId?: string;
  issueId?: string;
  prId?: string;
  limit?: number;        // Default 100, max 1000
  offset?: number;
}

interface GetEventsResponse {
  events: DomainEvent[];
  pagination: {
    total: number;
    limit: number;
    offset: number;
    hasMore: boolean;
  };
}

// GET /api/v1/events/:eventId
interface GetEventByIdResponse {
  event: DomainEvent;
}

// GET /api/v1/projections/pr/:prId
interface GetPRProjectionResponse {
  prId: string;
  currentState: {
    status: 'open' | 'merged' | 'closed';
    branch: string;
    commits: number;
    files: number;
    approvals: number;
  };
  timeline: Array<{
    timestamp: string;
    type: string;
    description: string;
  }>;
}
```

*Owner:* Epic 4 (Story 4.7)

**5. Replay Engine (`packages/events/src/replay-engine.ts`)**

*Responsibilities:*
- Deterministic state reconstruction from events
- Step-by-step interactive replay
- HTML report generation with diffs
- Performance optimization (indexed reads, caching)

*CLI Command:*
```typescript
interface ReplayCommand {
  correlationId?: string;
  issueId?: string;
  prId?: string;
  timestamp?: string;     // Replay up to this point
  interactive?: boolean;  // Step-by-step mode
  output?: string;        // HTML report path
}

// Example: Tamma replay --correlation-id abc123 --interactive
// Example: Tamma replay --pr-id 456 --timestamp 2025-10-28T10:30:00Z --output report.html
```

*Owner:* Epic 4 (Story 4.8)

**6. Blob Storage Service (`packages/events/src/blob-storage.ts`)**

*Responsibilities:*
- Store large event payloads (AI prompts/responses, file diffs)
- Reference blobs from events via blobId
- Retention policy enforcement
- Local filesystem backend (MVP), extensible to S3/Azure

*Interface:*
```typescript
interface BlobStorageInterface {
  store(content: string | Buffer, metadata: BlobMetadata): Promise<string>; // Returns blobId
  retrieve(blobId: string): Promise<Blob>;
  delete(blobId: string): Promise<void>;
}

interface BlobMetadata {
  contentType: string;
  correlationId?: string;
  retentionDays?: number; // Default from config
}

interface Blob {
  id: string;
  content: Buffer;
  metadata: BlobMetadata;
  createdAt: Date;
}
```

*Owner:* Epic 4 (Stories 4.4, 4.5)

**7. Sensitive Data Masker (`packages/events/src/masker.ts`)**

*Responsibilities:*
- Detect and redact API keys, passwords, tokens
- Pattern-based detection (regex for common formats)
- Integration with Epic 1's credential management
- Audit trail of masking operations

*Rules:*
```typescript
const MaskingRules = [
  { pattern: /sk-[a-zA-Z0-9]{32,}/, replacement: '***REDACTED_API_KEY***' },
  { pattern: /ghp_[a-zA-Z0-9]{36}/, replacement: '***REDACTED_GITHUB_TOKEN***' },
  { pattern: /glpat-[a-zA-Z0-9]{20}/, replacement: '***REDACTED_GITLAB_TOKEN***' },
  { pattern: /password["\s:=]+([^\s"]+)/, replacement: 'password: ***REDACTED***' },
];

function maskSensitiveData(text: string): string {
  let masked = text;
  for (const rule of MaskingRules) {
    masked = masked.replace(rule.pattern, rule.replacement);
  }
  return masked;
}
```

*Owner:* Epic 4 (Story 4.4)

### Data Models and Contracts

**PostgreSQL Event Store Schema (Story 4.2):**

```sql
-- Events table with DCB pattern (single stream with JSONB tags)
CREATE TABLE events (
  id UUID PRIMARY KEY,                     -- UUID v7 (time-sortable)
  type VARCHAR(255) NOT NULL,              -- Event type (ISSUE.SELECTED, etc.)
  timestamp TIMESTAMPTZ NOT NULL,          -- Event timestamp with millisecond precision
  schema_version VARCHAR(20) NOT NULL,     -- Schema version for migrations (1.0.0)

  -- Actor information
  actor_type VARCHAR(50) NOT NULL,         -- user | system | ai-provider
  actor_id VARCHAR(255) NOT NULL,          -- User ID, component name, provider name
  actor_metadata JSONB DEFAULT '{}',

  -- DCB tags for multi-perspective reads
  tags JSONB NOT NULL DEFAULT '{}',        -- { issueId, prId, correlationId, ... }

  -- Event payload and metadata
  payload JSONB NOT NULL,                  -- Event-specific data
  metadata JSONB DEFAULT '{}',             -- Trace IDs, context

  -- Audit and indexing
  created_at TIMESTAMPTZ DEFAULT NOW()
);

-- GIN indexes for efficient JSONB queries
CREATE INDEX idx_events_timestamp ON events(timestamp DESC);
CREATE INDEX idx_events_type ON events(type);
CREATE INDEX idx_events_tags ON events USING GIN(tags);
CREATE INDEX idx_events_actor ON events(actor_type, actor_id);
CREATE INDEX idx_events_correlation ON events((tags->>'correlationId'));
CREATE INDEX idx_events_issue ON events((tags->>'issueId'));
CREATE INDEX idx_events_pr ON events((tags->>'prId'));

-- Blob storage table for large payloads (Story 4.4, 4.5)
CREATE TABLE event_blobs (
  id UUID PRIMARY KEY,                     -- Blob ID
  content_type VARCHAR(255) NOT NULL,      -- text/plain, application/json, text/x-diff
  content BYTEA NOT NULL,                  -- Actual blob data
  size_bytes INTEGER NOT NULL,             -- Content size for quota tracking

  -- Metadata
  correlation_id VARCHAR(255),             -- Link to event correlation
  retention_days INTEGER DEFAULT 90,       -- Retention policy
  created_at TIMESTAMPTZ DEFAULT NOW(),
  expires_at TIMESTAMPTZ                   -- Calculated: created_at + retention_days
);

CREATE INDEX idx_blobs_correlation ON event_blobs(correlation_id);
CREATE INDEX idx_blobs_expires ON event_blobs(expires_at) WHERE expires_at IS NOT NULL;

-- Projections table for materialized views (Story 4.7)
CREATE TABLE event_projections (
  id SERIAL PRIMARY KEY,
  projection_name VARCHAR(100) NOT NULL,   -- pr_state, issue_timeline, etc.
  entity_id VARCHAR(255) NOT NULL,         -- PR ID, issue ID, etc.
  state JSONB NOT NULL,                    -- Projected state
  last_event_id UUID NOT NULL,             -- Last processed event
  last_updated TIMESTAMPTZ DEFAULT NOW(),

  UNIQUE(projection_name, entity_id)
);

CREATE INDEX idx_projections_lookup ON event_projections(projection_name, entity_id);
```

**File-Based Event Store Format (Development Mode):**

```typescript
// File: ~/.Tamma/events/stream.jsonl
// Format: Line-delimited JSON (one event per line)

// Example entries:
{"id":"01933d52-1a89-7000-8000-000000000001","type":"ISSUE.SELECTED","timestamp":"2025-10-28T10:15:00.123Z","schemaVersion":"1.0.0","actor":{"type":"system","id":"workflow-orchestrator"},"tags":{"issueId":"123","correlationId":"corr-abc","repo":"tamma/tamma"},"payload":{"issueId":123,"title":"Add event sourcing","labels":["enhancement"]},"metadata":{}}
{"id":"01933d52-2b3c-7000-8000-000000000002","type":"AI.REQUEST.STARTED","timestamp":"2025-10-28T10:15:01.456Z","schemaVersion":"1.0.0","actor":{"type":"ai-provider","id":"claude-code"},"tags":{"correlationId":"corr-abc","provider":"claude-code"},"payload":{"model":"claude-sonnet-4","promptBlobId":"blob-xyz","estimatedTokens":1500},"metadata":{}}

// File: ~/.Tamma/events/blobs/{blobId}
// Format: Raw content with companion metadata JSON

// Blob content: blobs/blob-xyz.content
Analyze this issue and propose a development plan...

// Blob metadata: blobs/blob-xyz.meta.json
{"id":"blob-xyz","contentType":"text/plain","sizeBytes":1234,"correlationId":"corr-abc","retentionDays":90,"createdAt":"2025-10-28T10:15:01.400Z","expiresAt":"2026-01-26T10:15:01.400Z"}
```

**Event Type Definitions (Story 4.1):**

```typescript
// Story 4.3: Issue Selection & Analysis Events
interface IssueSelectedEvent extends DomainEvent {
  type: 'ISSUE.SELECTED';
  payload: {
    issueId: number;
    title: string;
    labels: string[];
    repository: string;
    selectionCriteria: 'auto-next' | 'manual' | 'high-priority';
    assignee?: string;
  };
  tags: {
    issueId: string;
    correlationId: string;
    repo: string;
  };
}

interface IssueAnalysisCompletedEvent extends DomainEvent {
  type: 'ISSUE.ANALYSIS.COMPLETED';
  payload: {
    issueId: number;
    contextSummaryLength: number;
    referencedIssues: number[];
    estimatedComplexity: 'low' | 'medium' | 'high';
    analysisDurationMs: number;
  };
  tags: {
    issueId: string;
    correlationId: string;
    repo: string;
  };
}

// Story 4.4: AI Provider Interaction Events
interface AIRequestStartedEvent extends DomainEvent {
  type: 'AI.REQUEST.STARTED';
  payload: {
    provider: string;           // claude-code, openai-codex, etc.
    model: string;              // claude-sonnet-4, gpt-4, etc.
    promptBlobId: string;       // Reference to blob storage
    promptLength: number;       // Character count
    estimatedTokens: number;
    requestContext: 'issue-analysis' | 'code-generation' | 'review' | 'research';
  };
  tags: {
    correlationId: string;
    provider: string;
    issueId?: string;
    prId?: string;
  };
}

interface AIResponseReceivedEvent extends DomainEvent {
  type: 'AI.RESPONSE.RECEIVED';
  payload: {
    provider: string;
    model: string;
    responseBlobId: string;     // Reference to blob storage
    responseLength: number;
    actualTokens: number;
    latencyMs: number;
    costEstimateUSD: number;
    providerSelectionRationale: string;
  };
  tags: {
    correlationId: string;
    provider: string;
    issueId?: string;
    prId?: string;
  };
}

// Story 4.5: Code Changes & Git Operations Events
interface CodeFileWrittenEvent extends DomainEvent {
  type: 'CODE.FILE.WRITTEN';
  payload: {
    filePath: string;
    changeType: 'create' | 'update' | 'delete';
    sizeBytes: number;
    diffBlobId?: string;        // Reference to diff blob
    linesAdded: number;
    linesDeleted: number;
  };
  tags: {
    correlationId: string;
    issueId: string;
    prId?: string;
    filePath: string;
  };
}

interface CommitCreatedEvent extends DomainEvent {
  type: 'COMMIT.CREATED';
  payload: {
    sha: string;
    message: string;
    branch: string;
    fileCount: number;
    author: string;             // user or 'tamma-bot'
  };
  tags: {
    correlationId: string;
    issueId: string;
    prId?: string;
    branch: string;
  };
}

interface BranchCreatedEvent extends DomainEvent {
  type: 'BRANCH.CREATED';
  payload: {
    branchName: string;
    baseBranch: string;
    repository: string;
  };
  tags: {
    correlationId: string;
    issueId: string;
    branch: string;
    repo: string;
  };
}

interface PRCreatedEvent extends DomainEvent {
  type: 'PR.CREATED';
  payload: {
    prNumber: number;
    url: string;
    title: string;
    baseBranch: string;
    headBranch: string;
    filesChanged: number;
    linesAdded: number;
    linesDeleted: number;
  };
  tags: {
    correlationId: string;
    issueId: string;
    prId: string;
    repo: string;
  };
}

interface PRMergedEvent extends DomainEvent {
  type: 'PR.MERGED';
  payload: {
    prNumber: number;
    mergeStrategy: 'merge' | 'squash' | 'rebase';
    mergeSha: string;
    mergedBy: string;
  };
  tags: {
    correlationId: string;
    issueId: string;
    prId: string;
    repo: string;
  };
}

// Story 4.6: Approvals & Escalations Events
interface ApprovalRequestedEvent extends DomainEvent {
  type: 'APPROVAL.REQUESTED';
  payload: {
    approvalType: 'plan' | 'design' | 'merge' | 'breaking-change';
    contextSummary: string;
    options?: Array<{ label: string; description: string }>;
    timeoutSeconds?: number;    // For orchestrator mode
  };
  tags: {
    correlationId: string;
    issueId: string;
    prId?: string;
    approvalType: string;
  };
}

interface ApprovalProvidedEvent extends DomainEvent {
  type: 'APPROVAL.PROVIDED';
  payload: {
    approvalType: string;
    decision: 'approved' | 'rejected' | 'edited';
    userIdentity: string;
    comments?: string;
    responseTimeMs: number;
    approvalChannel: 'cli' | 'api' | 'webhook';
  };
  tags: {
    correlationId: string;
    issueId: string;
    prId?: string;
    approvalType: string;
    userId: string;
  };
}

interface EscalationTriggeredEvent extends DomainEvent {
  type: 'ESCALATION.TRIGGERED';
  payload: {
    reason: 'retry-exhausted' | 'structural-issue' | 'security-critical' | 'manual-request';
    failureType: 'build' | 'test' | 'security-scan' | 'other';
    retryHistory: Array<{
      attemptNumber: number;
      timestamp: string;
      error: string;
    }>;
    currentState: string;       // Description of current system state
    suggestedActions: string[];
  };
  tags: {
    correlationId: string;
    issueId: string;
    prId?: string;
    escalationType: string;
  };
}

interface EscalationResolvedEvent extends DomainEvent {
  type: 'ESCALATION.RESOLVED';
  payload: {
    escalationId: string;
    resolution: string;
    resolvedBy: string;
    timeToResolveMs: number;
    actionsTaken: string[];
  };
  tags: {
    correlationId: string;
    issueId: string;
    prId?: string;
    escalationId: string;
    userId: string;
  };
}
```

**Correlation ID Structure:**

```typescript
// Correlation IDs link all events in a development cycle
// Format: corr-{timestamp}-{issueId}-{randomSuffix}
// Example: corr-20251028101500-123-a1b2c3

interface CorrelationIdProvider {
  generate(issueId: number): string;
  parse(correlationId: string): {
    timestamp: Date;
    issueId: number;
    suffix: string;
  };
}

// Usage ensures all events from issue selection → PR merge share same correlationId
```

### APIs and Interfaces

**Event Store Interface (Story 4.2):**

```typescript
// packages/events/src/event-store.ts
interface EventStoreInterface {
  // Write operations (append-only)
  append(event: DomainEvent): Promise<void>;
  appendBatch(events: DomainEvent[]): Promise<void>;

  // Read operations
  getEvents(query: EventQuery): Promise<EventPage>;
  getEventById(eventId: string): Promise<DomainEvent | null>;
  streamEvents(query: EventQuery): AsyncIterable<DomainEvent>;

  // Backend management
  connect(): Promise<void>;
  disconnect(): Promise<void>;
  healthCheck(): Promise<{ healthy: boolean; latencyMs: number }>;
}

// Configuration for backend selection
interface EventStoreConfig {
  backend: 'postgresql' | 'file';
  postgresql?: {
    connectionString: string;
    poolSize?: number;
  };
  file?: {
    streamPath: string;      // Default: ~/.Tamma/events/stream.jsonl
    blobPath: string;        // Default: ~/.Tamma/events/blobs
  };
  retentionPolicy?: {
    eventRetentionDays: number;   // Default: infinite (no deletion)
    blobRetentionDays: number;    // Default: 90 days
  };
}
```

**Event Query REST API (Story 4.7):**

```typescript
// GET /api/v1/events
// Query events with filters and pagination
interface GetEventsEndpoint {
  request: {
    query: {
      since?: string;        // ISO 8601 timestamp
      until?: string;        // ISO 8601 timestamp
      type?: string | string[];  // Single type or array
      correlationId?: string;
      issueId?: string;
      prId?: string;
      actorType?: 'user' | 'system' | 'ai-provider';
      actorId?: string;
      limit?: number;        // Default 100, max 1000
      offset?: number;       // Default 0
    };
    headers: {
      Authorization: string; // Bearer {jwt-token}
    };
  };
  response: {
    200: {
      events: DomainEvent[];
      pagination: {
        total: number;
        limit: number;
        offset: number;
        hasMore: boolean;
        nextUrl?: string;
      };
    };
    401: { error: 'Unauthorized' };
    400: { error: 'Invalid query parameters'; details: string[] };
  };
}

// GET /api/v1/events/:eventId
// Get single event by ID
interface GetEventByIdEndpoint {
  request: {
    params: { eventId: string };
    headers: { Authorization: string };
  };
  response: {
    200: { event: DomainEvent };
    404: { error: 'Event not found' };
    401: { error: 'Unauthorized' };
  };
}

// GET /api/v1/events/correlation/:correlationId
// Get all events for a correlation (development cycle)
interface GetEventsByCorrelationEndpoint {
  request: {
    params: { correlationId: string };
    headers: { Authorization: string };
  };
  response: {
    200: {
      correlationId: string;
      events: DomainEvent[];
      summary: {
        issueId: string;
        prId?: string;
        startTime: string;
        endTime?: string;
        eventCount: number;
      };
    };
    404: { error: 'Correlation not found' };
  };
}

// GET /api/v1/projections/pr/:prId
// Get PR state projection (CQRS read model)
interface GetPRProjectionEndpoint {
  request: {
    params: { prId: string };
    query: { timestamp?: string };  // Optional: state at specific time
    headers: { Authorization: string };
  };
  response: {
    200: {
      prId: string;
      timestamp: string;
      state: {
        status: 'open' | 'merged' | 'closed';
        branch: string;
        commits: number;
        filesChanged: number;
        linesAdded: number;
        linesDeleted: number;
        approvals: number;
        buildsStatus: 'pending' | 'passing' | 'failing';
      };
      timeline: Array<{
        timestamp: string;
        type: string;
        description: string;
      }>;
    };
    404: { error: 'PR not found' };
  };
}

// POST /api/v1/events/export
// Export events to JSON/CSV for compliance
interface ExportEventsEndpoint {
  request: {
    body: {
      query: EventQuery;
      format: 'json' | 'csv';
      includeBlobs?: boolean;
    };
    headers: {
      Authorization: string;
    };
  };
  response: {
    200: {
      exportId: string;
      downloadUrl: string;
      expiresAt: string;
    };
    401: { error: 'Unauthorized' };
    403: { error: 'Insufficient permissions' };  // Admin only
  };
}
```

**Blob Storage Interface (Stories 4.4, 4.5):**

```typescript
// packages/events/src/blob-storage.ts
interface BlobStorageInterface {
  store(content: string | Buffer, metadata: BlobMetadata): Promise<string>;
  retrieve(blobId: string): Promise<Blob>;
  delete(blobId: string): Promise<void>;
  pruneExpired(): Promise<number>;  // Returns count of deleted blobs
}

interface BlobMetadata {
  contentType: 'text/plain' | 'application/json' | 'text/x-diff';
  correlationId?: string;
  retentionDays?: number;
}

interface Blob {
  id: string;
  content: Buffer;
  metadata: BlobMetadata;
  createdAt: Date;
  expiresAt?: Date;
}
```

**Replay CLI Interface (Story 4.8):**

```bash
# Replay events by correlation ID
Tamma replay --correlation-id corr-20251028101500-123-a1b2c3

# Replay events for specific PR
Tamma replay --pr-id 456

# Replay up to specific timestamp
Tamma replay --correlation-id corr-abc --timestamp 2025-10-28T10:30:00Z

# Interactive step-by-step replay
Tamma replay --correlation-id corr-abc --interactive

# Generate HTML report
Tamma replay --correlation-id corr-abc --output report.html

# Replay with filtering
Tamma replay --correlation-id corr-abc --type AI.REQUEST.STARTED --type AI.RESPONSE.RECEIVED
```

```typescript
// CLI implementation
interface ReplayCommand {
  correlationId?: string;
  issueId?: string;
  prId?: string;
  timestamp?: string;
  interactive?: boolean;
  output?: string;
  types?: string[];
}

interface ReplayResult {
  correlationId: string;
  eventsProcessed: number;
  reconstructedState: {
    issue: IssueState;
    pr?: PRState;
    commits: CommitState[];
    aiInteractions: AIInteractionSummary[];
  };
  timeline: TimelineEvent[];
}
```

### Workflows and Sequencing

**Sequence 1: Event Emission During Workflow Execution (Stories 4.3-4.6)**

```
Actor: WorkflowOrchestrator (Epic 2)
Flow: Issue Selection → Analysis → Event Persistence

1. WorkflowOrchestrator.selectIssue(issueId)
   ↓
2. Generate correlationId (corr-{timestamp}-{issueId}-{random})
   ↓
3. Fetch issue from GitPlatform
   ↓
4. EventStore.append(IssueSelectedEvent)
   │  → If write fails: Retry 3 times with exponential backoff
   │  → If all retries fail: HALT autonomous loop (data integrity)
   ↓
5. Continue with issue analysis
   ↓
6. EventStore.append(IssueAnalysisCompletedEvent)
   ↓
7. Return analysis result

Error Handling:
- Network errors → Retry with backoff
- Database connection errors → Retry with backoff
- Validation errors → Log error + continue (non-critical events)
- All retries exhausted → HALT loop + escalate
```

**Sequence 2: AI Interaction Event Capture (Story 4.4)**

```
Actor: AIProviderWrapper (Epic 1)
Flow: AI Request → Response → Event Capture with Blob Storage

1. AIProvider.generateCode(prompt, context)
   ↓
2. BlobStorage.store(prompt) → promptBlobId
   ↓
3. SensitiveDataMasker.mask(prompt) → maskedPrompt
   ↓
4. EventStore.append(AIRequestStartedEvent {
     promptBlobId, maskedPrompt, estimatedTokens
   })
   ↓
5. Send request to AI provider (Claude Code, etc.)
   ↓
6. Receive response from AI provider
   ↓
7. BlobStorage.store(response) → responseBlobId
   ↓
8. Calculate cost: actualTokens × providerRates
   ↓
9. EventStore.append(AIResponseReceivedEvent {
     responseBlobId, actualTokens, latencyMs, costUSD
   })
   ↓
10. Return response to caller

Sensitive Data Masking Rules:
- API keys: sk-*, ghp-*, glpat-* → ***REDACTED***
- Passwords: password: "..." → password: ***REDACTED***
- Tokens: Bearer xyz → Bearer ***REDACTED***
```

**Sequence 3: Code Change Event Capture (Story 4.5)**

```
Actor: CodeGenerator (Epic 2)
Flow: Code Generation → File Write → Git Commit → Event Capture

1. CodeGenerator.writeFile(filePath, content)
   ↓
2. FileSystem.write(filePath, content)
   ↓
3. Git.diff(filePath) → diffContent
   ↓
4. BlobStorage.store(diffContent) → diffBlobId
   ↓
5. EventStore.append(CodeFileWrittenEvent {
     filePath, changeType, sizeBytes, diffBlobId
   })
   ↓
6. Git.add(filePath)
   ↓
7. Git.commit(message) → commitSha
   ↓
8. EventStore.append(CommitCreatedEvent {
     sha, message, branch, fileCount, author
   })
   ↓
9. Git.push()
   ↓
10. EventStore.append(CommitPushedEvent)

Diff Storage:
- Small diffs (<10KB): Store inline in event payload
- Large diffs (≥10KB): Store in blob storage, reference via blobId
```

**Sequence 4: Approval and Escalation Event Capture (Story 4.6)**

```
Actor: ApprovalCoordinator (Epic 2 + Epic 3)
Flow: Request Approval → User Response → Event Capture

1. WorkflowOrchestrator reaches approval checkpoint
   ↓
2. EventStore.append(ApprovalRequestedEvent {
     approvalType: 'plan' | 'merge',
     contextSummary, options
   })
   ↓
3. Display approval prompt to user (CLI or API)
   ↓
4. Wait for user response (with timeout in orchestrator mode)
   ↓
5. User provides decision (approved/rejected/edited)
   ↓
6. EventStore.append(ApprovalProvidedEvent {
     decision, userIdentity, responseTimeMs, channel
   })
   ↓
7. Continue or halt workflow based on decision

Escalation Flow (Epic 3 quality gates):
1. BuildOrchestrator.triggerBuild() fails 3 times
   ↓
2. EventStore.append(EscalationTriggeredEvent {
     reason: 'retry-exhausted',
     failureType: 'build',
     retryHistory: [{attempt: 1, error: '...'}, ...]
   })
   ↓
3. Notify user via escalation channel (CLI, webhook, email)
   ↓
4. User resolves issue manually
   ↓
5. EventStore.append(EscalationResolvedEvent {
     resolution, actionsTaken, timeToResolveMs
   })
   ↓
6. Resume or restart workflow
```

**Sequence 5: Event Query and Time-Travel (Story 4.7)**

```
Actor: Developer debugging PR #456
Flow: Query Events → Build Projection → Analyze Timeline

1. Developer: GET /api/v1/events?prId=456
   ↓
2. EventQueryService validates JWT token
   ↓
3. EventStore.getEvents({ tags: { prId: '456' } })
   ↓
4. Apply filters, pagination
   ↓
5. Return events in chronological order
   ↓
6. Developer notices unexpected behavior at 10:30 AM
   ↓
7. Developer: GET /api/v1/projections/pr/456?timestamp=2025-10-28T10:30:00Z
   ↓
8. ProjectionEngine.rebuild(prId, timestamp)
   │  → Replay all events up to timestamp
   │  → Build PR state snapshot
   ↓
9. Return PR state at specific time: {
     status: 'open', commits: 3, buildsStatus: 'failing'
   }
   ↓
10. Developer identifies root cause from timeline

Query Optimization:
- GIN indexes on JSONB tags enable <50ms queries for 1M events
- Projections cache frequently accessed states (eventual consistency <100ms lag)
- Streaming API for large result sets (AsyncIterable)
```

**Sequence 6: Black-Box Replay for Debugging (Story 4.8)**

```
Actor: Developer investigating why AI made specific decision
Flow: CLI Replay → Event Reconstruction → HTML Report Generation

1. Developer: Tamma replay --correlation-id corr-abc --output report.html
   ↓
2. ReplayEngine.loadEvents(correlationId)
   │  → EventStore.getEvents({ tags: { correlationId: 'corr-abc' } })
   ↓
3. ReplayEngine.reconstructState(events)
   │  → For each event in chronological order:
   │    - Apply event to state machine
   │    - Record state transitions
   │    - Build timeline with diffs
   ↓
4. ReplayEngine.loadBlobs(events)
   │  → For AIRequestEvent: Load full prompts
   │  → For CodeFileWrittenEvent: Load diffs
   ↓
5. ReplayEngine.generateReport(state, timeline)
   │  → Handlebars template rendering
   │  → Embed CSS for offline viewing
   │  → Include syntax-highlighted code diffs
   ↓
6. FileSystem.write('report.html', htmlContent)
   ↓
7. Display: "Replay complete. Report saved to report.html"
   ↓
8. Developer opens report.html in browser
   │  → Timeline view with expandable events
   │  → AI interactions with full prompts/responses
   │  → Code changes with before/after diffs
   │  → Approval checkpoints with user decisions
   ↓
9. Developer identifies: AI chose approach B because
   prompt included constraint X from issue analysis

Interactive Mode (--interactive flag):
1. Display first event
2. Prompt: [Next (n) | Jump (j) | Quit (q)]
3. User navigates event-by-event
4. Display full event details with blob content
5. Allow jumping to specific event types or timestamps
```

**Sequence 7: Event Store Backend Selection (Story 4.2)**

```
Configuration-Based Backend Selection:

Development Mode:
~/.Tamma/config.yaml:
  eventStore:
    backend: file
    file:
      streamPath: ~/.Tamma/events/stream.jsonl
      blobPath: ~/.Tamma/events/blobs

Production Mode:
~/.Tamma/config.yaml:
  eventStore:
    backend: postgresql
    postgresql:
      connectionString: postgres://user:pass@localhost:5432/tamma_events
      poolSize: 20

Initialization Flow:
1. ConfigLoader.load() → EventStoreConfig
   ↓
2. EventStoreFactory.create(config)
   │  → if backend == 'postgresql': new PostgreSQLEventStore()
   │  → if backend == 'file': new FileBasedEventStore()
   ↓
3. EventStore.connect()
   │  → PostgreSQL: Create connection pool
   │  → File: Ensure directories exist
   ↓
4. EventStore.healthCheck()
   │  → PostgreSQL: SELECT 1
   │  → File: Write test file
   ↓
5. Register as singleton in DI container
   ↓
6. All services receive same EventStore instance
```

## Non-Functional Requirements

### Performance

**Event Write Performance (Story 4.2):**
- **Target:** Event append operations complete in <10ms (P95) for PostgreSQL backend, <5ms for file backend
- **Throughput:** Support 100+ events/second sustained write load without degradation
- **Batch Writes:** `appendBatch()` reduces overhead to <2ms per event for batches of 10+ events
- **Connection Pooling:** PostgreSQL connection pool size of 20 connections prevents contention under load
- **File Backend:** Atomic append with O_APPEND flag, no file locking contention for single-writer workloads

**Event Query Performance (Story 4.7):**
- **Target:** Event queries return in <1 second for databases with 1M+ events
- **Indexing Strategy:** GIN indexes on JSONB `tags` field enable O(log n) lookups by correlationId, issueId, prId
- **Pagination:** Default page size of 100 events, maximum 1000 events per request
- **Projection Queries:** Pre-materialized projections return in <100ms with eventual consistency (<100ms lag)
- **Cache Strategy:** LRU cache for frequently accessed projections (PR state, issue timeline) with 5-minute TTL

**Replay Performance (Story 4.8):**
- **Target:** Complete state reconstruction in <5 seconds for typical development cycles (50-100 events)
- **Blob Loading:** Parallel blob retrieval using Promise.all() for 5x speedup over sequential loading
- **Memory Management:** Stream-based event processing for large replays (1000+ events) to avoid OOM
- **HTML Report Generation:** Generate and write 1MB HTML report in <2 seconds using Handlebars templates

**Blob Storage Performance (Stories 4.4, 4.5):**
- **Target:** Blob store/retrieve operations complete in <50ms for blobs up to 1MB
- **Retention Pruning:** `pruneExpired()` runs daily, processes 10,000 blobs in <30 seconds
- **Compression:** Optional gzip compression for blobs >100KB reduces storage by 60-80%

### Security

**Sensitive Data Protection (Story 4.4):**
- **Automatic Masking:** All events pass through `SensitiveDataMasker` before persistence, detecting API keys (sk-*, ghp-*, glpat-*), passwords, and tokens via regex patterns
- **Masking Audit Trail:** Masking operations logged separately for compliance, recording what was masked and why
- **Blob Encryption:** Optional at-rest encryption for blobs containing sensitive data (AES-256 via PostgreSQL Transparent Data Encryption or filesystem encryption)
- **No Bypass:** Masking cannot be disabled; unmasked data only available in blob storage with admin access control

**Authentication and Authorization (Story 4.7):**
- **JWT-Based Auth:** All Event Query API endpoints require valid JWT token in Authorization header
- **Role-Based Access Control (RBAC):**
  - **Admin Role:** Full access to all events across all users/issues/PRs, export capabilities
  - **Standard Role:** Access only to events associated with own user ID (actor.id matches authenticated user)
  - **Read-Only Role:** Query access only, no export capabilities
- **Token Expiration:** JWT tokens expire after 1 hour, require refresh for continued access
- **Rate Limiting:** API endpoints rate-limited to 100 requests/minute per user to prevent abuse

**Audit Compliance (FR-24):**
- **Immutability:** Events are append-only; no updates or deletes allowed (enforced at database constraint level)
- **Tamper Detection:** Event IDs use UUID v7 (time-sortable), enabling detection of missing or out-of-order events
- **Export Capabilities:** Admin users can export events in JSON/CSV formats for external compliance systems (SOC2, ISO27001, GDPR)
- **Retention Policies:** Configurable retention for events (default: infinite) and blobs (default: 90 days), with audit trail of deletions
- **User Attribution:** All events include `actor.id` and `actor.type`, enabling "who did what when" audit trails

**Database Security:**
- **Connection Security:** PostgreSQL connections use SSL/TLS encryption for data in transit
- **Credential Management:** Database credentials stored in Epic 1's secure config system (OS keychain integration)
- **Least Privilege:** Event store database user has only INSERT (events) and SELECT permissions, no DELETE or UPDATE
- **Prepared Statements:** All queries use parameterized prepared statements to prevent SQL injection

### Reliability/Availability

**Event Write Durability (Story 4.2):**
- **Synchronous Persistence:** Events written synchronously (await EventStore.append()) before workflow continues, ensuring no lost events on crashes
- **Retry Logic:** Event write failures retry 3 times with exponential backoff (1s → 2s → 4s)
- **Failure Handling:** After 3 failed retries, autonomous loop HALTS and escalates to human (data integrity over progress)
- **Transaction Guarantees:** PostgreSQL backend uses transactions for atomic writes; file backend uses atomic append (O_APPEND flag)
- **No Event Loss:** Events are never lost due to system crashes or network interruptions (at-least-once delivery guarantee)

**Backend Availability:**
- **PostgreSQL Backend:**
  - Connection pool maintains 20 connections with automatic reconnection on network failures
  - Health checks every 30 seconds detect database unavailability
  - Graceful degradation: Queue events in memory (max 1000) during temporary database outages, flush when connection restored
- **File Backend:**
  - No external dependencies, always available (only filesystem required)
  - File writes use fsync() to ensure data persisted to disk before returning success
  - Automatic directory creation if event storage paths don't exist

**Query API Availability (Story 4.7):**
- **Target Uptime:** 99.9% availability for Event Query API (allows 8.76 hours downtime/year)
- **Read-Only Operations:** Event queries do not block event writes (CQRS separation)
- **Projection Staleness:** Projections may lag by up to 100ms during high event write load, acceptable for debugging use case
- **Graceful Degradation:** If projections unavailable, API falls back to raw event queries (slower but still functional)

**Replay Reliability (Story 4.8):**
- **Deterministic Replay:** State reconstruction produces identical results for same event sequence (idempotent)
- **Blob Unavailability:** If blob storage unavailable, replay continues with placeholder content "(blob unavailable)" rather than failing
- **Large Event Sequences:** Replay handles sequences of 10,000+ events without OOM via stream-based processing
- **Crash Recovery:** Replay operations are read-only, crashes have no side effects (safe to retry)

**Data Integrity:**
- **Schema Validation:** All events validated against JSON Schema before persistence, invalid events rejected
- **Correlation ID Integrity:** All events in development cycle share same correlationId, orphaned events detectable via queries
- **Event Ordering:** UUID v7 time-sortable IDs + timestamp field ensure consistent chronological ordering
- **Backup Strategy:** PostgreSQL backend uses pg_dump for daily backups, file backend uses filesystem snapshots

### Observability

**Event Store Metrics (Integration with Epic 5):**
- **Counter Metrics:**
  - `event_store_events_written_total{type, backend}` - Total events written by type and backend
  - `event_store_write_failures_total{backend, reason}` - Failed event writes by backend and reason
  - `event_query_requests_total{endpoint, status_code}` - API request counts
  - `blob_storage_operations_total{operation, backend}` - Blob store/retrieve/delete operations
- **Histogram Metrics:**
  - `event_store_write_duration_ms{backend}` - Event write latency distribution
  - `event_query_duration_ms{endpoint}` - Query API response time distribution
  - `replay_duration_ms{event_count_bucket}` - Replay performance by event count
  - `blob_storage_size_bytes{correlation_id}` - Blob size distribution
- **Gauge Metrics:**
  - `event_store_connection_pool_active{backend}` - Active database connections
  - `event_store_memory_queue_size` - Events queued during temporary outages
  - `projection_lag_ms{projection_name}` - Projection staleness

**Structured Logging (Integration with Epic 5):**
- **Event Write Logs:**
  ```json
  {
    "level": "info",
    "timestamp": "2025-10-28T10:15:00.123Z",
    "message": "Event written successfully",
    "context": {
      "eventId": "01933d52-1a89-7000-8000-000000000001",
      "eventType": "ISSUE.SELECTED",
      "correlationId": "corr-abc",
      "backend": "postgresql",
      "durationMs": 8
    }
  }
  ```
- **Event Query Logs:**
  ```json
  {
    "level": "info",
    "timestamp": "2025-10-28T10:16:00.456Z",
    "message": "Event query executed",
    "context": {
      "userId": "user-123",
      "queryType": "correlation",
      "correlationId": "corr-abc",
      "resultCount": 42,
      "durationMs": 45
    }
  }
  ```
- **Error Logs:**
  ```json
  {
    "level": "error",
    "timestamp": "2025-10-28T10:17:00.789Z",
    "message": "Event write failed after retries",
    "context": {
      "eventType": "AI.REQUEST.STARTED",
      "correlationId": "corr-xyz",
      "backend": "postgresql",
      "retryCount": 3,
      "error": "Connection timeout"
    }
  }
  ```

**Health Check Endpoint:**
```typescript
// GET /health/events
{
  "status": "healthy" | "degraded" | "unhealthy",
  "eventStore": {
    "backend": "postgresql",
    "connected": true,
    "latencyMs": 8,
    "lastWriteTimestamp": "2025-10-28T10:15:00.123Z"
  },
  "blobStorage": {
    "backend": "filesystem",
    "available": true,
    "usedSpaceGB": 12.5,
    "availableSpaceGB": 487.5
  },
  "projections": {
    "healthy": true,
    "lagMs": 42,
    "lastUpdateTimestamp": "2025-10-28T10:15:00.200Z"
  }
}
```

**Trace IDs and Distributed Tracing:**
- **Correlation ID Propagation:** All events include `correlationId` in tags, enabling cross-service tracing
- **OpenTelemetry Integration (Epic 5):** Event store operations emit spans with correlationId as trace ID
- **Parent-Child Relationships:** Events include optional `parentEventId` for hierarchical tracing (e.g., AIResponseEvent references AIRequestEvent)

**Replay Observability (Story 4.8):**
- **Progress Tracking:** Interactive replay displays progress: "Processing event 42/156 (27%)"
- **Performance Metrics:** Replay completion logs include: events processed, duration, blobs loaded, memory peak
- **Debugging Output:** Replay verbose mode (`--verbose`) logs each state transition for troubleshooting

## Dependencies and Integrations

**NPM Dependencies (packages/events/package.json):**

```json
{
  "name": "@tamma/events",
  "version": "0.1.0",
  "dependencies": {
    // Event Store Core
    "emmett": "^0.23.0",              // PostgreSQL event sourcing library
    "pg": "^8.11.0",                  // PostgreSQL client
    "uuid": "^10.0.0",                // UUID v7 generation

    // Validation
    "zod": "^3.23.0",                 // Schema validation (TypeScript-first)

    // Blob Storage
    "fs-extra": "^11.2.0",            // Enhanced filesystem operations

    // Query API
    "fastify": "^5.2.0",              // REST API framework
    "@fastify/jwt": "^8.0.0",         // JWT authentication
    "@fastify/rate-limit": "^10.0.0", // Rate limiting

    // Replay Engine
    "handlebars": "^4.7.8",           // HTML template rendering
    "highlight.js": "^11.9.0",        // Syntax highlighting for code diffs

    // Utilities
    "dayjs": "^1.11.10",              // Date manipulation (lightweight)
    "pino": "^9.0.0"                  // Structured logging
  },
  "devDependencies": {
    "@types/node": "^22.0.0",
    "@types/pg": "^8.11.0",
    "typescript": "^5.7.0",
    "jest": "^29.7.0",
    "@types/jest": "^29.5.0"
  }
}
```

**Epic 1 (Foundation) Integration:**

| Component | Integration Point | Purpose |
|-----------|------------------|---------|
| **AIProviderInterface** | Event emitter wrapper | Capture AIRequestStartedEvent and AIResponseReceivedEvent for all AI interactions |
| **GitPlatformInterface** | Event emitter wrapper | Capture Git operations (branch creation, commits, PR creation/merge) |
| **Config System** | EventStoreConfig | Load event store backend configuration, database credentials, retention policies |
| **Auth Middleware** | Event Query API | Reuse JWT auth from Epic 1 for Event Query API endpoints |
| **CLI Scaffolding** | Replay command | Integrate `tamma replay` command into Epic 1's CLI framework |

**Epic 2 (Workflow) Integration:**

| Component | Integration Point | Purpose |
|-----------|------------------|---------|
| **WorkflowOrchestrator** | Event emitters in workflow steps | Emit IssueSelectedEvent, IssueAnalysisCompletedEvent, PlanGeneratedEvent |
| **CodeGenerator** | Event emitters for file operations | Emit CodeFileWrittenEvent for each file create/update/delete |
| **ApprovalCoordinator** | Event emitters for checkpoints | Emit ApprovalRequestedEvent and ApprovalProvidedEvent at approval gates |
| **PRManager** | Event emitters for PR lifecycle | Emit PRCreatedEvent, PRMergedEvent when PR operations complete |
| **CorrelationIdProvider** | Shared correlation ID | Ensure all events in workflow share same correlationId from Epic 2 |

**Epic 3 (Quality Gates) Integration:**

| Component | Integration Point | Purpose |
|-----------|------------------|---------|
| **BuildOrchestrator** | Event emitters for build lifecycle | Emit BuildTriggeredEvent, BuildCompletedEvent, BuildFailedEvent |
| **TestExecutor** | Event emitters for test runs | Emit TestRunStartedEvent, TestRunCompletedEvent with pass/fail results |
| **EscalationManager** | Event emitters for escalations | Emit EscalationTriggeredEvent when retry limits exhausted, EscalationResolvedEvent when resolved |
| **SecurityScanner** | Event emitters for scan results | Emit SecurityScanCompletedEvent with vulnerability details |
| **AmbiguityDetector** | Event emitters for intelligence | Emit AmbiguityDetectedEvent, ClarifyingQuestionsAskedEvent |

**Epic 5 (Observability) Integration:**

| Component | Integration Point | Purpose |
|-----------|------------------|---------|
| **Metrics Collection** | Event store metrics | Expose event_store_* metrics for Prometheus scraping |
| **Structured Logging** | Event store logs | Integrate with Epic 5's logging infrastructure (Winston/Pino) |
| **Dashboard UI** | Event Query API | Dashboard fetches events via REST API for Event Trail Exploration UI |
| **Alert System** | Event stream consumption | Alerts consume events for critical issues (escalations, security vulnerabilities) |

**External Dependencies:**

| System | Version | Purpose | Required? |
|--------|---------|---------|-----------|
| **PostgreSQL** | 17+ | Production event store backend with JSONB support and GIN indexes | Yes (production) |
| **Node.js** | 22 LTS | Runtime environment | Yes |
| **pnpm** | 9.x | Package manager for monorepo | Yes |
| **Redis** | 7.x+ | Optional: Cache for projections (Epic 5), pub/sub for real-time event streaming | No (MVP) |

**Database Schema Migration:**

```typescript
// migrations/001_create_events_table.sql
CREATE TABLE events (
  id UUID PRIMARY KEY,
  type VARCHAR(255) NOT NULL,
  timestamp TIMESTAMPTZ NOT NULL,
  schema_version VARCHAR(20) NOT NULL,
  actor_type VARCHAR(50) NOT NULL,
  actor_id VARCHAR(255) NOT NULL,
  actor_metadata JSONB DEFAULT '{}',
  tags JSONB NOT NULL DEFAULT '{}',
  payload JSONB NOT NULL,
  metadata JSONB DEFAULT '{}',
  created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX idx_events_timestamp ON events(timestamp DESC);
CREATE INDEX idx_events_type ON events(type);
CREATE INDEX idx_events_tags ON events USING GIN(tags);
CREATE INDEX idx_events_actor ON events(actor_type, actor_id);
CREATE INDEX idx_events_correlation ON events((tags->>'correlationId'));
CREATE INDEX idx_events_issue ON events((tags->>'issueId'));
CREATE INDEX idx_events_pr ON events((tags->>'prId'));

// migrations/002_create_event_blobs_table.sql
CREATE TABLE event_blobs (
  id UUID PRIMARY KEY,
  content_type VARCHAR(255) NOT NULL,
  content BYTEA NOT NULL,
  size_bytes INTEGER NOT NULL,
  correlation_id VARCHAR(255),
  retention_days INTEGER DEFAULT 90,
  created_at TIMESTAMPTZ DEFAULT NOW(),
  expires_at TIMESTAMPTZ
);

CREATE INDEX idx_blobs_correlation ON event_blobs(correlation_id);
CREATE INDEX idx_blobs_expires ON event_blobs(expires_at) WHERE expires_at IS NOT NULL;

// migrations/003_create_event_projections_table.sql
CREATE TABLE event_projections (
  id SERIAL PRIMARY KEY,
  projection_name VARCHAR(100) NOT NULL,
  entity_id VARCHAR(255) NOT NULL,
  state JSONB NOT NULL,
  last_event_id UUID NOT NULL,
  last_updated TIMESTAMPTZ DEFAULT NOW(),
  UNIQUE(projection_name, entity_id)
);

CREATE INDEX idx_projections_lookup ON event_projections(projection_name, entity_id);
```

**Configuration Example:**

```yaml
# ~/.tamma/config.yaml

eventStore:
  # Backend selection: 'postgresql' | 'file'
  backend: postgresql

  # PostgreSQL configuration (production)
  postgresql:
    connectionString: ${EVENT_STORE_DATABASE_URL}
    poolSize: 20
    ssl: true

  # File configuration (development)
  file:
    streamPath: ~/.tamma/events/stream.jsonl
    blobPath: ~/.tamma/events/blobs

  # Retention policies
  retentionPolicy:
    eventRetentionDays: -1         # -1 = infinite (no deletion)
    blobRetentionDays: 90
    pruneSchedule: "0 2 * * *"     # Daily at 2 AM

blobStorage:
  backend: filesystem               # 'filesystem' | 's3' (future)
  filesystem:
    path: ~/.tamma/events/blobs
  compression:
    enabled: true
    minSizeKB: 100                  # Compress blobs > 100KB

eventQueryAPI:
  enabled: true
  port: 3001
  authentication:
    required: true
    jwtSecret: ${JWT_SECRET}
    tokenExpiration: 3600           # 1 hour
  rateLimit:
    requestsPerMinute: 100
```

**Deployment Considerations:**

- **Development:** File-based event store (zero external dependencies)
- **Production:** PostgreSQL event store with connection pooling, pg_dump backups
- **Migration Path:** Export events from file backend, import to PostgreSQL via `EventStore.appendBatch()`
- **Backward Compatibility:** Event schema versioning ensures old events readable after schema updates

## Acceptance Criteria (Authoritative)

### Story 4.1: Event Schema Design

**AC-4.1.1:** Event schema defines base fields: `eventId`, `timestamp`, `eventType`, `actorType`, `actorId`, `payload`, `metadata`

**AC-4.1.2:** Schema includes event types for: issue selection, AI requests/responses, code changes, Git operations, approvals, escalations, errors

**AC-4.1.3:** Schema supports event versioning (schema version field) for future evolution

**AC-4.1.4:** Schema includes correlation IDs for linking related events (e.g., all events for single PR)

**AC-4.1.5:** Schema validated with JSON Schema or Protocol Buffers

**AC-4.1.6:** Documentation includes event catalog with examples for each event type

### Story 4.2: Event Store Backend Selection

**AC-4.2.1:** Event store supports append-only writes (no updates or deletes)

**AC-4.2.2:** Event store provides ordered reads by timestamp with efficient querying

**AC-4.2.3:** Event store supports filtering by event type, actor, correlation ID

**AC-4.2.4:** Event store handles high write throughput (100+ events/second)

**AC-4.2.5:** Implementation supports multiple backends: local file (dev), PostgreSQL (prod), EventStore (optional)

**AC-4.2.6:** Backend selection configurable via configuration file

**AC-4.2.7:** Event store includes retention policy configuration (default: infinite retention)

### Story 4.3: Event Capture - Issue Selection & Analysis

**AC-4.3.1:** `IssueSelectedEvent` captured when issue is selected (Story 2.1) including issue ID, title, labels, selection criteria

**AC-4.3.2:** `IssueAnalysisCompletedEvent` captured after analysis (Story 2.2) including context summary length, referenced issues

**AC-4.3.3:** Events include actor (system in orchestrator mode, CI runner in worker mode)

**AC-4.3.4:** Events include correlation ID linking entire development cycle

**AC-4.3.5:** Events persisted to event store before proceeding to next step

**AC-4.3.6:** Event write failures trigger retry (3 attempts) then halt autonomous loop for data integrity

### Story 4.4: Event Capture - AI Provider Interactions

**AC-4.4.1:** `AIRequestEvent` captured before each AI provider call including: provider name, model, prompt (truncated if >1000 chars), token count estimate

**AC-4.4.2:** `AIResponseEvent` captured after response including: provider name, model, response (truncated), token count, latency, cost estimate

**AC-4.4.3:** Events include full prompt/response in separate blob storage for detailed analysis (with retention policy)

**AC-4.4.4:** Events mask sensitive data (API keys, passwords) before persistence

**AC-4.4.5:** Events include provider selection rationale (why this provider was chosen)

**AC-4.4.6:** Events persisted to event store synchronously (block on write completion)

### Story 4.5: Event Capture - Code Changes & Git Operations

**AC-4.5.1:** `CodeFileWrittenEvent` captured for each file write including: file path, file size, change type (create/update/delete)

**AC-4.5.2:** `CommitCreatedEvent` captured for each commit including: commit SHA, message, branch name, file count

**AC-4.5.3:** `BranchCreatedEvent` captured when branch created (Story 2.4)

**AC-4.5.4:** `PRCreatedEvent` captured when PR created (Story 2.8) including: PR number, URL, base/head branches

**AC-4.5.5:** `PRMergedEvent` captured when PR merged (Story 2.10) including: merge strategy, merge SHA

**AC-4.5.6:** Events include file diffs stored in blob storage (linked from event)

**AC-4.5.7:** Events capture who triggered the action (user approval vs autonomous decision)

### Story 4.6: Event Capture - Approvals & Escalations

**AC-4.6.1:** `ApprovalRequestedEvent` captured when system requests user approval (plan, merge, etc.) including: approval type, context summary

**AC-4.6.2:** `ApprovalProvidedEvent` captured when user responds including: decision (approved/rejected/edited), timestamp, user identity

**AC-4.6.3:** `EscalationTriggeredEvent` captured when retry limits exhausted (Story 3.3) including: escalation reason, retry history, current state

**AC-4.6.4:** `EscalationResolvedEvent` captured when human resolves escalation including: resolution description, time to resolve

**AC-4.6.5:** Events support approval audit trail for compliance (who approved what when)

**AC-4.6.6:** Events capture approval channel (CLI interactive, API call, webhook)

### Story 4.7: Event Query API for Time-Travel

**AC-4.7.1:** API endpoint: `GET /events?since={timestamp}&until={timestamp}&type={type}&correlationId={id}`

**AC-4.7.2:** API returns events in chronological order with pagination support (default 100 events per page)

**AC-4.7.3:** API supports filtering by: event type, actor, correlation ID, issue number

**AC-4.7.4:** API supports projection queries: "What was state of PR #123 at timestamp T?"

**AC-4.7.5:** API includes efficient indexing for fast queries (query completes in <1 second for 1M events)

**AC-4.7.6:** API requires authentication (prevent unauthorized event access)

**AC-4.7.7:** API documentation includes usage examples and query patterns

### Story 4.8: Black-Box Replay for Debugging

**AC-4.8.1:** CLI command: `Tamma replay --correlation-id {id} --timestamp {timestamp}`

**AC-4.8.2:** Command reconstructs system state by replaying events up to specified timestamp

**AC-4.8.3:** Command displays: issue context, AI provider decisions, code changes, approval points, errors

**AC-4.8.4:** Command supports step-by-step replay (pause at each event) via `--interactive` flag

**AC-4.8.5:** Command exports replay to HTML report for sharing with team

**AC-4.8.6:** Replay includes diff view showing state changes between events

**AC-4.8.7:** Replay performance: complete reconstruction in <5 seconds for typical development cycle (50-100 events)

## Traceability Mapping

| AC ID | Spec Section(s) | Component(s)/API(s) | Test Idea |
|-------|----------------|-------------------|-----------|
| **AC-4.1.1** | Data Models: DomainEvent interface | Event Schema Registry (schemas.ts) | Unit test: Validate DomainEvent has required fields (id, timestamp, type, actor, tags, payload) |
| **AC-4.1.2** | Data Models: EventTypes constant, Event type definitions | Event Schema Registry (schemas.ts) | Unit test: Verify all 11+ event types defined (ISSUE.SELECTED, AI.REQUEST.STARTED, etc.) |
| **AC-4.1.3** | Data Models: schemaVersion field in DomainEvent | Event Schema Registry (schemas.ts) | Unit test: Events serialize with schemaVersion="1.0.0", version bumps handled gracefully |
| **AC-4.1.4** | Data Models: Correlation ID Structure, tags field | CorrelationIdProvider, DomainEvent.tags | Unit test: Generate correlationId, verify format (corr-timestamp-issueId-random), parse back |
| **AC-4.1.5** | Services: Event Schema Registry, Data Models | Zod schema validation | Unit test: Invalid events rejected (missing fields, wrong types), valid events accepted |
| **AC-4.1.6** | Data Models: Event type definitions with examples | Event catalog documentation | Manual test: Review generated event catalog docs, verify examples for each event type |
| **AC-4.2.1** | Services: Event Store Core, Data Models: PostgreSQL schema | EventStoreInterface.append(), CREATE TABLE events (no UPDATE/DELETE) | Integration test: Attempt UPDATE/DELETE on events table → fails with permission error |
| **AC-4.2.2** | Services: Event Store Core, APIs: EventStoreInterface.getEvents() | PostgreSQL indexes (timestamp DESC), EventQuery | Integration test: Insert 1000 events, query by time range, verify chronological order |
| **AC-4.2.3** | APIs: EventQuery filters, Data Models: GIN indexes on tags | EventStoreInterface.getEvents({ tags: {...} }) | Integration test: Query by correlationId/issueId/prId, verify only matching events returned |
| **AC-4.2.4** | NFR Performance: 100+ events/sec throughput | EventStoreInterface.appendBatch(), connection pool | Load test: Insert 150 events/sec for 1 minute, verify <10ms P95 latency, no errors |
| **AC-4.2.5** | Services: Event Store Core, Workflows: Backend Selection | PostgreSQLEventStore, FileBasedEventStore, EventStoreFactory | Integration test: Switch config between backends, verify both work correctly |
| **AC-4.2.6** | Dependencies: Configuration Example, Workflows: Backend Selection | EventStoreConfig, config.yaml | Integration test: Change config backend from 'file' to 'postgresql', restart, verify backend switch |
| **AC-4.2.7** | Dependencies: Configuration Example (retentionPolicy) | EventStoreConfig.retentionPolicy | Unit test: Config with eventRetentionDays=-1 (infinite), blobRetentionDays=90 |
| **AC-4.3.1** | Data Models: IssueSelectedEvent, Workflows: Event Emission | Event Emitters, WorkflowOrchestrator | Integration test: selectIssue(123) → verify IssueSelectedEvent persisted with issueId, title, labels |
| **AC-4.3.2** | Data Models: IssueAnalysisCompletedEvent, Workflows: Event Emission | Event Emitters, WorkflowOrchestrator | Integration test: analyzeIssue() → verify IssueAnalysisCompletedEvent with contextSummaryLength |
| **AC-4.3.3** | Data Models: DomainEvent.actor field | Event Emitters (actorType, actorId) | Unit test: Event in orchestrator mode has actor.type='system', worker mode has actor.type='ci-runner' |
| **AC-4.3.4** | Data Models: Correlation ID, DomainEvent.tags | CorrelationIdProvider, Event Emitters | Integration test: selectIssue() → all events share same correlationId in tags |
| **AC-4.3.5** | Workflows: Event Emission (synchronous persistence), NFR Reliability | EventStore.append() before workflow continues | Integration test: Mock event store failure → verify workflow halts before next step |
| **AC-4.3.6** | Workflows: Event Emission Error Handling, NFR Reliability: Retry Logic | Event write retry with exponential backoff (3 attempts) | Integration test: Simulate transient DB error → retries 3 times → escalates on failure |
| **AC-4.4.1** | Data Models: AIRequestStartedEvent, Workflows: AI Interaction Capture | AIProviderWrapper, Event Emitters | Integration test: AI request → verify AIRequestStartedEvent with promptBlobId, estimatedTokens |
| **AC-4.4.2** | Data Models: AIResponseReceivedEvent, Workflows: AI Interaction Capture | AIProviderWrapper, Event Emitters | Integration test: AI response → verify AIResponseReceivedEvent with responseBlobId, actualTokens, costUSD |
| **AC-4.4.3** | Services: Blob Storage Service, Data Models: event_blobs table | BlobStorageInterface.store(), event.promptBlobId | Integration test: Store 10MB prompt → retrieve via blobId → verify content matches |
| **AC-4.4.4** | Services: Sensitive Data Masker, Workflows: AI Interaction Capture, NFR Security | SensitiveDataMasker.mask(), MaskingRules | Unit test: Event with "sk-abc123" → masked to "***REDACTED_API_KEY***" |
| **AC-4.4.5** | Data Models: AIRequestStartedEvent.providerSelectionRationale | AIProviderWrapper, Event payload | Integration test: AI request → verify event includes rationale (e.g., "cost optimization") |
| **AC-4.4.6** | Workflows: AI Interaction Capture (synchronous), NFR Reliability | EventStore.append() blocks until written | Integration test: Mock slow event store (100ms delay) → verify AI call waits for event write |
| **AC-4.5.1** | Data Models: CodeFileWrittenEvent, Workflows: Code Change Capture | CodeGenerator, Event Emitters | Integration test: writeFile() → verify CodeFileWrittenEvent with filePath, changeType, sizeBytes |
| **AC-4.5.2** | Data Models: CommitCreatedEvent, Workflows: Code Change Capture | CodeGenerator, Git wrapper | Integration test: git commit → verify CommitCreatedEvent with sha, message, branch, fileCount |
| **AC-4.5.3** | Data Models: BranchCreatedEvent, Workflows: Code Change Capture | Git wrapper, Event Emitters | Integration test: git branch → verify BranchCreatedEvent with branchName, baseBranch |
| **AC-4.5.4** | Data Models: PRCreatedEvent, Workflows: Code Change Capture | PRManager, Event Emitters | Integration test: createPR() → verify PRCreatedEvent with prNumber, url, base/head branches |
| **AC-4.5.5** | Data Models: PRMergedEvent, Workflows: Code Change Capture | PRManager, Event Emitters | Integration test: mergePR() → verify PRMergedEvent with mergeStrategy, mergeSha, mergedBy |
| **AC-4.5.6** | Services: Blob Storage Service, Data Models: CodeFileWrittenEvent.diffBlobId | BlobStorage.store(diff), event.diffBlobId | Integration test: File write with diff → verify diffBlobId references retrievable blob |
| **AC-4.5.7** | Data Models: DomainEvent.actor, Workflows: Code Change Capture | Event Emitters (actor.id: user vs 'tamma-bot') | Unit test: User-initiated commit has actor.id='user-123', autonomous has actor.id='tamma-bot' |
| **AC-4.6.1** | Data Models: ApprovalRequestedEvent, Workflows: Approval Capture | ApprovalCoordinator, Event Emitters | Integration test: Request approval → verify ApprovalRequestedEvent with approvalType, contextSummary |
| **AC-4.6.2** | Data Models: ApprovalProvidedEvent, Workflows: Approval Capture | ApprovalCoordinator, Event Emitters | Integration test: User responds → verify ApprovalProvidedEvent with decision, userIdentity, responseTimeMs |
| **AC-4.6.3** | Data Models: EscalationTriggeredEvent, Workflows: Escalation Capture | EscalationManager (Epic 3), Event Emitters | Integration test: Build fails 3 times → verify EscalationTriggeredEvent with retryHistory |
| **AC-4.6.4** | Data Models: EscalationResolvedEvent, Workflows: Escalation Capture | EscalationManager, Event Emitters | Integration test: Resolve escalation → verify EscalationResolvedEvent with resolution, timeToResolveMs |
| **AC-4.6.5** | Data Models: ApprovalEvents, NFR Security: Audit Compliance | Events include actor.id, timestamp, approval audit trail | Manual test: Query all approval events for issue → verify complete audit trail (who/when/what) |
| **AC-4.6.6** | Data Models: ApprovalProvidedEvent.approvalChannel | Event payload (cli/api/webhook) | Unit test: Approval via CLI → channel='cli', via API → channel='api' |
| **AC-4.7.1** | APIs: Event Query REST API (GetEventsEndpoint) | GET /api/v1/events with query params | Integration test: Query with since/until/type/correlationId → verify filtered results |
| **AC-4.7.2** | APIs: Event Query REST API, NFR Performance: Pagination | EventPage with pagination, default 100 limit | Integration test: Query 250 events → first page has 100, hasMore=true, nextUrl provided |
| **AC-4.7.3** | APIs: EventQuery filters, Data Models: GIN indexes | EventStoreInterface.getEvents() with multiple filters | Integration test: Filter by type AND correlationId → verify only matching events |
| **AC-4.7.4** | APIs: Event Query REST API (GetPRProjectionEndpoint), Services: Event Query Service | GET /api/v1/projections/pr/:prId?timestamp | Integration test: Query PR state at timestamp → verify state reconstructed from events |
| **AC-4.7.5** | NFR Performance: Event Query (<1s for 1M events), Data Models: GIN indexes | GIN indexes on JSONB tags, query optimization | Load test: Insert 1M events, query by correlationId → verify <1s response time |
| **AC-4.7.6** | APIs: Event Query REST API, NFR Security: Authentication | JWT auth middleware, Authorization header | Integration test: Query without JWT → 401, with expired JWT → 401, with valid JWT → 200 |
| **AC-4.7.7** | APIs: Event Query REST API documentation | OpenAPI/Swagger docs for /api/v1/events endpoints | Manual test: Review API docs, verify usage examples for all endpoints |
| **AC-4.8.1** | APIs: Replay CLI Interface, Services: Replay Engine | Tamma replay command with options | Integration test: Run `Tamma replay --correlation-id corr-abc` → verify state reconstruction |
| **AC-4.8.2** | Services: Replay Engine (reconstructState), Workflows: Black-Box Replay | ReplayEngine.reconstructState(events) | Integration test: Replay up to timestamp → verify state matches expected state at that time |
| **AC-4.8.3** | Services: Replay Engine, Workflows: Black-Box Replay | ReplayResult with issue/pr/commits/aiInteractions | Integration test: Replay → verify output includes issue context, AI decisions, code changes, approvals |
| **AC-4.8.4** | APIs: Replay CLI Interface (--interactive flag), Workflows: Black-Box Replay | Interactive mode with [Next/Jump/Quit] prompts | Manual test: Run `Tamma replay --interactive` → verify step-by-step navigation works |
| **AC-4.8.5** | Services: Replay Engine (generateReport), Workflows: Black-Box Replay | ReplayEngine.generateReport() with Handlebars | Integration test: Replay with --output report.html → verify HTML file created with embedded CSS |
| **AC-4.8.6** | Services: Replay Engine (diff views), Workflows: Black-Box Replay | HTML report with code diffs (highlight.js) | Integration test: Replay → verify HTML report includes diff views with before/after code |
| **AC-4.8.7** | NFR Performance: Replay (<5s for 50-100 events), Services: Replay Engine | ReplayEngine with parallel blob loading | Performance test: Replay 100 events with 50 blobs → verify completes in <5 seconds |

**PRD Requirements Covered:**

- **FR-20:** Event capture for all user/AI/system actions → AC-4.3.1 through AC-4.6.6 (event emission for all operations)
- **FR-21:** Append-only log with versioning → AC-4.2.1 (append-only), AC-4.1.3 (versioning), AC-4.2.7 (retention)
- **FR-22:** State reconstruction at any time → AC-4.8.2 (deterministic replay), AC-4.7.4 (projection queries)
- **FR-23:** Minute-by-minute playback → AC-4.8.3 (replay displays timeline), AC-4.8.4 (interactive step-by-step)
- **FR-24:** Audit log export → AC-4.7.7 (API documentation), Integration with Epic 5 export endpoint

## Risks, Assumptions, Open Questions

### Risks

**RISK-4.1: Event Store Performance Degradation at Scale**
- **Description:** PostgreSQL event store performance may degrade beyond 10M events despite GIN indexes
- **Impact:** Query API latency exceeds 1s SLA, affecting time-travel debugging usability
- **Likelihood:** Medium (depends on query patterns and PostgreSQL tuning)
- **Mitigation:** Implement partitioning strategy (partition by month/year), archive old events to cold storage, add read replicas for query workload separation
- **Owner:** Database Architect

**RISK-4.2: Blob Storage Growth Exceeding Disk Capacity**
- **Description:** AI prompts/responses and code diffs accumulate rapidly (estimated 10GB/month for active development), potentially exhausting disk
- **Impact:** Event writes fail when blob storage full, halting autonomous loops
- **Likelihood:** Medium (depends on AI usage patterns and retention policies)
- **Mitigation:** Enforce blob retention policies (90-day default), implement compression (60-80% reduction), alert when disk usage exceeds 80%, S3 backend for production (Epic 5+)
- **Owner:** DevOps Engineer

**RISK-4.3: Event Schema Evolution Breaking Replay**
- **Description:** Adding/removing fields in event schema may break replay for historical events
- **Impact:** Cannot debug past issues, compliance audit trails incomplete
- **Likelihood:** High (inevitable as system evolves)
- **Mitigation:** Schema versioning with backward-compatible migrations, replay engine handles multiple schema versions, document migration process, test replay across schema versions
- **Owner:** System Architect

**RISK-4.4: Sensitive Data Leakage Despite Masking**
- **Description:** Regex-based masking may miss novel token formats or context-specific secrets
- **Impact:** API keys, passwords, or tokens exposed in event store/audit logs
- **Likelihood:** Low-Medium (regex patterns cover common formats)
- **Mitigation:** Regular security audits of event data, allow custom masking rules via config, manual review of exported audit logs before external sharing, encrypt blob storage at rest
- **Owner:** Security Auditor

**RISK-4.5: Synchronous Event Writes Increasing Workflow Latency**
- **Description:** Blocking on event writes (10ms per event) adds cumulative latency to workflows (e.g., 50 events = 500ms overhead)
- **Impact:** Autonomous loop slows down, reducing throughput (issues/hour)
- **Likelihood:** Medium (depends on event volume per workflow)
- **Mitigation:** Use appendBatch() for non-critical events, async event writes for non-critical paths (with eventual consistency), optimize PostgreSQL write performance (SSD, tuned config)
- **Owner:** Performance Engineer

### Assumptions

**ASSUMPTION-4.1:** PostgreSQL 17+ available in production environment with JSONB and GIN index support
- **Validation:** Confirm with DevOps team during Epic 1 infrastructure setup
- **If False:** Use PostgreSQL 12+ (JSONB available since 9.4), adjust index strategy if GIN not optimal

**ASSUMPTION-4.2:** Development cycles generate 50-100 events on average (issue selection → PR merge)
- **Validation:** Measure event counts during Epic 2/3 integration testing
- **If False:** Adjust replay performance targets (scale up/down based on actual event volumes)

**ASSUMPTION-4.3:** 90-day blob retention sufficient for compliance and debugging needs
- **Validation:** Consult with compliance team and end users during alpha release
- **If False:** Adjust retention policy in config (extend to 180 days or implement tiered retention)

**ASSUMPTION-4.4:** File-based event store acceptable for single-developer local development
- **Validation:** Test file backend with 10,000+ events during Story 4.2 implementation
- **If False:** Require PostgreSQL for all environments (adds setup complexity)

**ASSUMPTION-4.5:** JWT authentication from Epic 1 sufficient for Event Query API security
- **Validation:** Security review during Epic 1 auth implementation
- **If False:** Implement additional security measures (API keys, OAuth2, mTLS)

**ASSUMPTION-4.6:** Correlation IDs uniquely identify development cycles without collisions
- **Validation:** Unit test correlationId generation with 1M samples, verify uniqueness
- **If False:** Switch to UUID-based correlation IDs (lose human readability)

**ASSUMPTION-4.7:** HTML reports with embedded CSS acceptable for replay output (no external dependencies)
- **Validation:** User testing during Story 4.8 implementation
- **If False:** Implement alternative formats (PDF, interactive web UI)

### Open Questions

**QUESTION-4.1:** Should event store support multi-tenancy isolation (separate event streams per organization)?
- **Context:** MVP assumes single-tenant deployment, but SaaS future may require multi-tenancy
- **Decision Needed By:** Epic 5 (production readiness planning)
- **Options:** (A) Single event store with tenantId tag, (B) Separate databases per tenant, (C) Defer to post-MVP
- **Recommendation:** Defer to post-MVP (single-tenant sufficient for initial launch)

**QUESTION-4.2:** Should blob storage use S3-compatible backend for production, or is PostgreSQL BYTEA sufficient?
- **Context:** PostgreSQL BYTEA scales to 1GB per blob, but S3 provides better separation of concerns
- **Decision Needed By:** Story 4.2 implementation start
- **Options:** (A) PostgreSQL BYTEA (MVP), add S3 backend later, (B) S3 from start (AWS SDK dependency)
- **Recommendation:** PostgreSQL BYTEA for MVP, S3 backend as optional enhancement in Epic 5

**QUESTION-4.3:** Should event query API support GraphQL in addition to REST?
- **Context:** GraphQL enables flexible queries without predefined endpoints, but adds complexity
- **Decision Needed By:** Story 4.7 implementation start
- **Options:** (A) REST only (MVP), (B) GraphQL only, (C) Both REST and GraphQL
- **Recommendation:** REST only for MVP (simpler, meets requirements), GraphQL as optional enhancement

**QUESTION-4.4:** Should replay engine support exporting to formats beyond HTML (e.g., PDF, Markdown, JSON)?
- **Context:** HTML sufficient for browser viewing, but some users may prefer other formats
- **Decision Needed By:** Story 4.8 implementation
- **Options:** (A) HTML only, (B) HTML + JSON (machine-readable), (C) HTML + PDF (printable)
- **Recommendation:** HTML + JSON for MVP (covers human and machine use cases)

**QUESTION-4.5:** Should event schema validation use Zod (TypeScript runtime) or JSON Schema (language-agnostic)?
- **Context:** Zod provides TypeScript integration, JSON Schema enables cross-language validation
- **Decision Needed By:** Story 4.1 implementation start
- **Options:** (A) Zod (TypeScript-first), (B) JSON Schema (standard), (C) Both (Zod for runtime, JSON Schema for docs)
- **Recommendation:** Zod for MVP (TypeScript project), JSON Schema export for API documentation

**QUESTION-4.6:** Should projections rebuild automatically on schema changes, or require manual migration?
- **Context:** Automatic rebuild ensures consistency but may be slow for large event stores
- **Decision Needed By:** Story 4.7 implementation
- **Options:** (A) Automatic rebuild with background job, (B) Manual migration command, (C) Both (auto for small stores, manual for large)
- **Recommendation:** Manual migration command for MVP (explicit control), auto-rebuild as optional optimization

## Test Strategy Summary

### Testing Approach

Epic 4 testing focuses on **data integrity, auditability, and replay determinism** as the core value propositions of event sourcing. The strategy emphasizes integration testing of event capture across Epic 1-3 components and performance testing at scale (1M+ events) to validate production readiness.

### Test Levels

**Unit Tests (Estimated: 85 tests)**

*Scope:* Individual components in isolation

| Component | Test Count | Coverage |
|-----------|------------|----------|
| Event Schema Registry | 15 | Schema validation, event type definitions, correlation ID generation/parsing, schema versioning |
| Event Store Core (PostgreSQL) | 20 | Append operations, query operations, filtering, pagination, connection pooling |
| Event Store Core (File) | 15 | File append, JSONL parsing, atomic writes, directory creation |
| Blob Storage Service | 12 | Store/retrieve operations, retention policy enforcement, compression |
| Sensitive Data Masker | 10 | Masking rules (API keys, passwords, tokens), regex pattern matching |
| Replay Engine | 8 | State reconstruction logic, HTML report generation, diff computation |
| Event Emitters | 5 | Correlation ID propagation, actor attribution, retry logic |

*Example Unit Test:*
```typescript
describe('Event Schema Registry', () => {
  it('should validate IssueSelectedEvent against schema', () => {
    const event: IssueSelectedEvent = {
      id: uuidv7(),
      type: EventTypes.ISSUE_SELECTED,
      timestamp: new Date().toISOString(),
      schemaVersion: '1.0.0',
      actor: { type: 'system', id: 'workflow-orchestrator' },
      tags: { issueId: '123', correlationId: 'corr-abc' },
      payload: { issueId: 123, title: 'Test', labels: [] },
    };

    const result = validateEvent(event);
    expect(result.valid).toBe(true);
  });

  it('should reject event with missing required fields', () => {
    const invalidEvent = { type: 'ISSUE.SELECTED' }; // Missing other fields
    const result = validateEvent(invalidEvent);
    expect(result.valid).toBe(false);
    expect(result.errors).toContain('Missing required field: id');
  });
});
```

**Integration Tests (Estimated: 62 tests)**

*Scope:* Component interactions and Epic 1-3 integration

| Integration Area | Test Count | Coverage |
|------------------|------------|----------|
| Event Capture - Epic 2 Workflow | 15 | Issue selection, analysis, plan generation, code implementation events |
| Event Capture - Epic 3 Quality Gates | 12 | Build, test, escalation, security scan events |
| Event Capture - Epic 1 Providers | 8 | AI request/response, Git operations (branch, commit, PR) |
| Event Store Backend Switching | 5 | File → PostgreSQL migration, backend selection via config |
| Event Query API | 10 | REST endpoints, authentication, filtering, pagination, projections |
| Blob Storage Integration | 7 | Large payload storage, retrieval, retention pruning |
| Replay Engine with Event Store | 5 | Full replay with blob loading, HTML report generation |

*Example Integration Test:*
```typescript
describe('Event Capture - Issue Selection Integration', () => {
  it('should capture IssueSelectedEvent when WorkflowOrchestrator selects issue', async () => {
    const orchestrator = new WorkflowOrchestrator(eventStore, gitPlatform, aiProvider);

    await orchestrator.selectIssue(123);

    const events = await eventStore.getEvents({
      tags: { issueId: '123' },
      types: [EventTypes.ISSUE_SELECTED]
    });

    expect(events.events).toHaveLength(1);
    expect(events.events[0].payload.issueId).toBe(123);
    expect(events.events[0].tags.correlationId).toMatch(/^corr-/);
  });

  it('should halt workflow when event write fails after 3 retries', async () => {
    const orchestrator = new WorkflowOrchestrator(mockFailingEventStore, gitPlatform, aiProvider);

    await expect(orchestrator.selectIssue(123)).rejects.toThrow('Event write failed after 3 retries');

    // Verify workflow did not proceed to next step
    expect(gitPlatform.getIssue).not.toHaveBeenCalled();
  });
});
```

**End-to-End Tests (Estimated: 12 tests)**

*Scope:* Complete development cycle with event capture and replay

| Scenario | Test Count | Coverage |
|----------|------------|----------|
| Full Autonomous Loop with Event Trail | 3 | Issue selection → PR merge, verify complete event trail |
| Time-Travel Debugging Workflow | 3 | Query events, reconstruct state at timestamp, replay |
| Approval Audit Trail | 2 | Request approval → user responds → verify audit trail |
| Escalation Workflow | 2 | Quality gate failure → escalation → resolution → event capture |
| Export and Compliance | 2 | Export events to JSON/CSV, verify compliance requirements |

*Example E2E Test:*
```typescript
describe('Full Autonomous Loop with Event Trail', () => {
  it('should capture complete event trail from issue selection to PR merge', async () => {
    const correlationId = await runAutonomousLoop(issueId: 456);

    const events = await eventQueryAPI.get(`/events?correlationId=${correlationId}`);

    // Verify event types in order
    const eventTypes = events.events.map(e => e.type);
    expect(eventTypes).toEqual([
      'ISSUE.SELECTED',
      'ISSUE.ANALYSIS.COMPLETED',
      'AI.REQUEST.STARTED',
      'AI.RESPONSE.RECEIVED',
      'BRANCH.CREATED',
      'CODE.FILE.WRITTEN',
      'COMMIT.CREATED',
      'PR.CREATED',
      'BUILD.TRIGGERED',
      'BUILD.COMPLETED',
      'PR.MERGED',
    ]);

    // Verify all events share same correlationId
    expect(events.events.every(e => e.tags.correlationId === correlationId)).toBe(true);
  });
});
```

**Performance/Load Tests (Estimated: 8 tests)**

*Scope:* Performance at scale and load testing

| Performance Area | Test Count | Target |
|------------------|------------|--------|
| Event Write Throughput | 2 | 100+ events/sec, <10ms P95 latency |
| Event Query at Scale | 2 | <1s for 1M events with filters |
| Replay Performance | 2 | <5s for 100 events with 50 blobs |
| Concurrent Event Writes | 2 | No deadlocks, correct ordering |

*Example Performance Test:*
```typescript
describe('Event Write Throughput', () => {
  it('should sustain 150 events/sec without degradation', async () => {
    const events = generateTestEvents(9000); // 9000 events = 1 minute at 150/sec

    const startTime = Date.now();

    for (const event of events) {
      await eventStore.append(event);
    }

    const duration = Date.now() - startTime;
    const eventsPerSecond = (events.length / duration) * 1000;

    expect(eventsPerSecond).toBeGreaterThan(150);

    // Verify P95 latency
    const latencies = await getEventWriteLatencies();
    const p95 = calculatePercentile(latencies, 95);
    expect(p95).toBeLessThan(10); // <10ms P95
  });
});
```

**Manual/Exploratory Tests (Estimated: 6 tests)**

*Scope:* Human verification and usability testing

| Area | Test Count | Coverage |
|------|------------|----------|
| Replay HTML Report Usability | 2 | Verify HTML report readability, code diffs, timeline navigation |
| Event Catalog Documentation | 1 | Review event catalog, verify examples accurate |
| Interactive Replay CLI | 1 | Test `--interactive` mode step-by-step navigation |
| API Documentation Review | 1 | Review Swagger/OpenAPI docs for completeness |
| Compliance Audit Trail | 1 | Verify exported audit logs meet SOC2/ISO27001 requirements |

### Test Execution Plan

**Phase 1: Unit Testing (Story-by-Story)**
- Execute unit tests during implementation of each story (Stories 4.1-4.8)
- Target: 95%+ code coverage for event sourcing package
- Tools: Jest with coverage reporting

**Phase 2: Integration Testing (Cross-Epic Integration)**
- Execute integration tests after completing Stories 4.3-4.6 (event capture)
- Test event capture from Epic 1 (AI/Git), Epic 2 (Workflow), Epic 3 (Quality Gates)
- Tools: Jest with testcontainers for PostgreSQL

**Phase 3: Performance Testing (Pre-Production)**
- Execute performance tests after completing Story 4.7 (Query API)
- Load 1M events into PostgreSQL, measure query performance
- Tools: k6 or Artillery for load generation

**Phase 4: E2E Testing (Epic Integration Validation)**
- Execute E2E tests after completing Story 4.8 (Replay)
- Run complete autonomous loops with event capture and replay
- Tools: Playwright or Cypress for CLI interaction testing

**Phase 5: Manual Testing (User Acceptance)**
- Execute manual tests during Epic 5 (Observability) integration
- User testing of HTML reports, interactive replay, API documentation
- Tools: Manual test scripts, user feedback surveys

### Test Data Strategy

**Event Fixtures:**
- Pre-generated event fixtures for common scenarios (issue selection, AI interactions, commits, PRs)
- Event catalog with examples serves as test data reference
- Correlation ID fixtures for multi-event scenarios

**Database Seeding:**
- Scripts to seed PostgreSQL with 10K, 100K, 1M events for performance testing
- Realistic event distributions (70% code changes, 20% AI interactions, 10% approvals)

**Blob Fixtures:**
- Sample AI prompts/responses (1KB, 10KB, 100KB, 1MB) for blob storage testing
- Sample code diffs (small, medium, large) for diff storage testing

### Coverage Targets

| Test Level | Target Coverage | Critical Paths |
|------------|----------------|----------------|
| Unit Tests | 95% | Event validation, schema versioning, masking, append operations |
| Integration Tests | 85% | Event capture, backend switching, query API, replay engine |
| E2E Tests | 100% of user workflows | Issue → PR merge with event trail, time-travel debugging |

### Test Summary

**Total Estimated Test Cases: 173**
- Unit Tests: 85
- Integration Tests: 62
- E2E Tests: 12
- Performance Tests: 8
- Manual Tests: 6

**Critical Success Metrics:**
- 100% of acceptance criteria covered by automated tests
- All performance targets validated (write throughput, query latency, replay speed)
- Zero data loss in event store under failure conditions (validated via chaos testing)
- Deterministic replay produces identical results across 100 runs
