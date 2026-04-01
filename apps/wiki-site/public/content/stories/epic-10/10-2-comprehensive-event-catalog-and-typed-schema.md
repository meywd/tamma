---
title: "Story 10.2: Comprehensive Event Catalog & Typed Schema"
sidebar:
  order: 100
---

Status: ready-for-dev

## Story

As a **platform architect**,
I want a preconfigured, typed event catalog covering every action, call, and state change in the system,
so that every component (engine, Elsa, UI, platform) can record events using well-defined types with required fields, and the event stream serves as the raw, complete record of everything that happened.

## Acceptance Criteria

1. Event catalog defines typed event schemas for every category: intake, decision, queue, workflow, LLM, security, platform, and state events
2. Each event type has a TypeScript interface with required and optional fields — no untyped `Record<string, unknown>` payloads
3. All events share a base structure: `eventId` (UUID v7), `timestamp` (ISO 8601), `eventType`, `actor`, `metadata` (correlationId, causationId, workflowId, schemaVersion)
4. Event types use discriminated union pattern — `eventType` field determines the payload shape
5. LLM events capture the full lifecycle: request created, sanitized, dispatched, response received, response sanitized, call completed — with separate events for raw and sanitized content
6. Security events exist for every sanitization action, PII detection, prompt injection detection, action blocking, and URL blocking
7. Platform events cover all git operations: issue CRUD, PR lifecycle, branch operations, CI status, webhook receipt
8. Event schema supports versioning via `schemaVersion` field for future evolution without breaking consumers
9. Validation functions exist for each event type — events are validated at write time
10. Event catalog is documented with examples for each event type
11. Secondary/derived events (sanitization results, state snapshots) are first-class event types, not metadata on other events

## Technical Context

### Base Event Structure

```typescript
interface BaseEvent {
  eventId: string;         // UUID v7 (time-sortable)
  timestamp: string;       // ISO 8601 with millisecond precision
  eventType: EventType;    // Discriminant field
  schemaVersion: string;   // "1.0.0" — for event evolution
  actor: EventActor;
  metadata: EventMetadata;
}

interface EventActor {
  type: 'user' | 'system' | 'ai-provider' | 'workflow-provider' | 'platform' | 'engine';
  id: string;
  name?: string;
}

interface EventMetadata {
  correlationId: string;   // Links all events in one user interaction / workflow
  causationId?: string;    // ID of the event that directly caused this one
  workflowId?: string;     // Elsa workflow instance ID
  issueId?: string;        // Related issue number
  prId?: string;           // Related PR number
  projectId?: string;      // Multi-project support
  sessionId?: string;      // User session for CLI/web
  tags?: Record<string, string>; // DCB tags for flexible querying
}
```

### Complete Event Catalog

#### Intake Events
```typescript
type IntakeEvents =
  | { eventType: 'INPUT_RECEIVED'; payload: { source: string; channel: string; inputType: string; rawSize: number } }
  | { eventType: 'INPUT_NORMALIZED'; payload: { normalizedType: string; normalizedPayload: NormalizedInput } }
  | { eventType: 'INPUT_REJECTED'; payload: { reason: string; validationErrors: string[] } };
```

#### Decision Events
```typescript
type DecisionEvents =
  | { eventType: 'CONTEXT_LOADED'; payload: { activeWorkflows: number; pendingApprovals: number; eventsRead: number; loadTimeMs: number } }
  | { eventType: 'LLM_DECISION_REQUESTED'; payload: { promptTokens: number; contextSize: number; model: string } }
  | { eventType: 'LLM_DECISION_RECEIVED'; payload: { action: string; confidence: number; reasoning: string; latencyMs: number; costUsd: number } }
  | { eventType: 'FAST_PATH_USED'; payload: { rule: string; action: string; reason: string } }
  | { eventType: 'ACTION_DECIDED'; payload: { action: string; target?: string; reason: string } }
  | { eventType: 'ACTION_REJECTED'; payload: { reason: string; duplicateOf?: string } }
  | { eventType: 'RESPONSE_SENT'; payload: { responseType: string; targetChannel: string; responseSize: number } };
```

#### Queue Events
```typescript
type QueueEvents =
  | { eventType: 'INTENT_QUEUED'; payload: { intentType: string; workflowName?: string; signal?: string; position: number } }
  | { eventType: 'INTENT_REVALIDATED'; payload: { intentId: string; stillValid: boolean; reason: string } }
  | { eventType: 'INTENT_DISPATCHED'; payload: { intentId: string; workflowInstanceId?: string; dispatchTimeMs: number } }
  | { eventType: 'INTENT_DROPPED'; payload: { intentId: string; reason: string } }
  | { eventType: 'QUEUE_DRAINED'; payload: { itemsDispatched: number; itemsDropped: number } }
  | { eventType: 'QUEUE_STALLED'; payload: { reason: string; queueDepth: number; oldestItemAge: number } };
```

#### Workflow Events (written by Elsa via event store)
```typescript
type WorkflowEvents =
  | { eventType: 'WORKFLOW_STARTED'; payload: { workflowName: string; workflowInstanceId: string; input: Record<string, unknown> } }
  | { eventType: 'WORKFLOW_STEP_STARTED'; payload: { workflowInstanceId: string; step: string; activityType: string } }
  | { eventType: 'WORKFLOW_STEP_COMPLETED'; payload: { workflowInstanceId: string; step: string; durationMs: number; output?: unknown } }
  | { eventType: 'WORKFLOW_STEP_FAILED'; payload: { workflowInstanceId: string; step: string; error: string; retryable: boolean } }
  | { eventType: 'WORKFLOW_SIGNAL_RECEIVED'; payload: { workflowInstanceId: string; signal: string; signalPayload?: unknown } }
  | { eventType: 'WORKFLOW_COMPLETED'; payload: { workflowInstanceId: string; durationMs: number; stepsCompleted: number } }
  | { eventType: 'WORKFLOW_FAILED'; payload: { workflowInstanceId: string; error: string; failedStep: string } }
  | { eventType: 'WORKFLOW_CANCELLED'; payload: { workflowInstanceId: string; reason: string; cancelledBy: string } }
  | { eventType: 'WORKFLOW_PAUSED'; payload: { workflowInstanceId: string; reason: string } }
  | { eventType: 'WORKFLOW_RESUMED'; payload: { workflowInstanceId: string; resumedBy: string } };
```

#### LLM Events (full lifecycle — separate events for raw and sanitized)
```typescript
type LLMEvents =
  | { eventType: 'LLM_REQUEST_CREATED'; payload: { provider: string; model: string; role: string; rawPromptRef: string; promptTokensEstimate: number; budgetRemaining: number } }
  | { eventType: 'LLM_REQUEST_SANITIZED'; payload: { rawEventRef: string; sanitizedPromptRef: string; warningsCount: number; piiDetected: boolean; promptInjectionDetected: boolean; itemsMasked: SanitizationItem[] } }
  | { eventType: 'LLM_CALL_DISPATCHED'; payload: { provider: string; model: string; sanitizedPromptRef: string; estimatedCostUsd: number } }
  | { eventType: 'LLM_RESPONSE_RECEIVED'; payload: { provider: string; model: string; rawResponseRef: string; tokensIn: number; tokensOut: number; latencyMs: number; costUsd: number } }
  | { eventType: 'LLM_RESPONSE_SANITIZED'; payload: { rawEventRef: string; sanitizedResponseRef: string; warningsCount: number; piiRedacted: number; scriptsStripped: number; itemsMasked: SanitizationItem[] } }
  | { eventType: 'LLM_CALL_COMPLETED'; payload: { provider: string; model: string; success: boolean; totalCostUsd: number; totalLatencyMs: number; sanitizedResponseRef: string } }
  | { eventType: 'LLM_CALL_FAILED'; payload: { provider: string; model: string; error: string; errorCode: string; retryable: boolean } }
  | { eventType: 'LLM_PROVIDER_FALLBACK'; payload: { fromProvider: string; toProvider: string; reason: string } }
  | { eventType: 'LLM_BUDGET_EXCEEDED'; payload: { provider: string; budgetLimit: number; currentSpend: number; requestedCost: number } };

interface SanitizationItem {
  type: 'pii' | 'api_key' | 'prompt_injection' | 'html' | 'script' | 'zero_width';
  location: string; // approximate position or field
  action: 'redacted' | 'stripped' | 'escaped' | 'flagged';
}
```

#### Security Events
```typescript
type SecurityEvents =
  | { eventType: 'CONTENT_SANITIZED'; payload: { contentType: 'llm_prompt' | 'llm_response' | 'user_input' | 'webhook_payload'; rawRef: string; sanitizedRef: string; items: SanitizationItem[]; warnings: string[] } }
  | { eventType: 'PII_DETECTED'; payload: { contentType: string; piiTypes: string[]; action: 'redacted' | 'flagged'; sourceEventRef: string } }
  | { eventType: 'PROMPT_INJECTION_DETECTED'; payload: { category: string; pattern: string; action: 'blocked' | 'flagged'; sourceEventRef: string } }
  | { eventType: 'ACTION_BLOCKED'; payload: { command: string; reason: string; blockedBy: 'action_gating' | 'permissions' | 'budget' } }
  | { eventType: 'URL_BLOCKED'; payload: { url: string; reason: string; context: string } }
  | { eventType: 'ACCESS_DENIED'; payload: { resource: string; requiredPermission: string; actor: string } };
```

#### Platform Events (git operations — written by platform adapters)
```typescript
type PlatformEvents =
  | { eventType: 'WEBHOOK_RECEIVED'; payload: { platform: string; eventType: string; deliveryId: string; rawSize: number } }
  | { eventType: 'ISSUE_FETCHED'; payload: { platform: string; issueNumber: number; title: string; labels: string[] } }
  | { eventType: 'ISSUE_ASSIGNED'; payload: { platform: string; issueNumber: number; assignee: string } }
  | { eventType: 'ISSUE_COMMENTED'; payload: { platform: string; issueNumber: number; commentId: string; author: string; isBot: boolean } }
  | { eventType: 'ISSUE_CLOSED'; payload: { platform: string; issueNumber: number; closedBy: string; reason: string } }
  | { eventType: 'BRANCH_CREATED'; payload: { platform: string; branchName: string; fromRef: string } }
  | { eventType: 'BRANCH_DELETED'; payload: { platform: string; branchName: string; deletedBy: string } }
  | { eventType: 'COMMIT_PUSHED'; payload: { platform: string; branch: string; commitSha: string; message: string; filesChanged: number } }
  | { eventType: 'PR_CREATED'; payload: { platform: string; prNumber: number; title: string; branch: string; targetBranch: string } }
  | { eventType: 'PR_UPDATED'; payload: { platform: string; prNumber: number; changes: string[] } }
  | { eventType: 'PR_REVIEWED'; payload: { platform: string; prNumber: number; reviewer: string; decision: 'approved' | 'changes_requested' | 'commented' } }
  | { eventType: 'PR_MERGED'; payload: { platform: string; prNumber: number; mergeStrategy: string; mergedBy: string } }
  | { eventType: 'CI_TRIGGERED'; payload: { platform: string; prNumber: number; checkName: string; runId: string } }
  | { eventType: 'CI_COMPLETED'; payload: { platform: string; prNumber: number; checkName: string; status: 'success' | 'failure' | 'cancelled'; durationMs: number } };
```

#### State Events
```typescript
type StateEvents =
  | { eventType: 'STATE_SNAPSHOT'; payload: { snapshotType: string; state: Record<string, unknown>; eventCountSinceLastSnapshot: number } }
  | { eventType: 'STATE_RECONSTRUCTED'; payload: { eventsProcessed: number; reconstructionTimeMs: number; snapshotUsed: boolean } }
  | { eventType: 'ENGINE_STARTED'; payload: { version: string; config: Record<string, unknown>; startTimeMs: number } }
  | { eventType: 'ENGINE_STOPPED'; payload: { reason: string; activeWorkflows: number; queueDepth: number } }
  | { eventType: 'ENGINE_HEALTH_CHECK'; payload: { healthy: boolean; checks: Record<string, { status: string; latencyMs: number }> } };
```

### Validation

Each event type has a corresponding validation function:

```typescript
interface IEventValidator {
  validate(event: TammaEvent): ValidationResult;
}

interface ValidationResult {
  valid: boolean;
  errors: Array<{ field: string; message: string; value?: unknown }>;
}
```

Validation happens at write time in the event store. Invalid events are rejected with detailed error messages. This prevents malformed data from entering the stream.

## Tasks / Subtasks

- [ ] Task 1: Define base event types and infrastructure (AC: 3, 4, 8)
  - [ ] Subtask 1.1: Define `BaseEvent`, `EventActor`, `EventMetadata` interfaces
  - [ ] Subtask 1.2: Define `EventType` string literal union covering all event types
  - [ ] Subtask 1.3: Define discriminated union `TammaEvent = BaseEvent & (IntakeEvents | DecisionEvents | ...)`
  - [ ] Subtask 1.4: Implement UUID v7 generation utility
  - [ ] Subtask 1.5: Define schema version constants and evolution strategy

- [ ] Task 2: Define intake and decision event types (AC: 1, 2)
  - [ ] Subtask 2.1: Define `IntakeEvents` with typed payloads
  - [ ] Subtask 2.2: Define `DecisionEvents` with typed payloads
  - [ ] Subtask 2.3: Define `QueueEvents` with typed payloads
  - [ ] Subtask 2.4: Ensure all payloads have required fields, no `Record<string, unknown>`

- [ ] Task 3: Define workflow and LLM event types (AC: 1, 5)
  - [ ] Subtask 3.1: Define `WorkflowEvents` covering full lifecycle
  - [ ] Subtask 3.2: Define `LLMEvents` with separate raw/sanitized events
  - [ ] Subtask 3.3: Define `SanitizationItem` type for tracking what was sanitized
  - [ ] Subtask 3.4: Define content reference pattern (`rawPromptRef`, `sanitizedResponseRef`) for linking to blob storage

- [ ] Task 4: Define security and platform event types (AC: 6, 7, 11)
  - [ ] Subtask 4.1: Define `SecurityEvents` for all sanitization actions
  - [ ] Subtask 4.2: Define `PlatformEvents` for all git operations
  - [ ] Subtask 4.3: Define `StateEvents` for snapshots and engine lifecycle
  - [ ] Subtask 4.4: Ensure sanitization events are first-class (not metadata on other events)

- [ ] Task 5: Implement event validation (AC: 9)
  - [ ] Subtask 5.1: Create `IEventValidator` interface
  - [ ] Subtask 5.2: Implement per-type validators checking required fields
  - [ ] Subtask 5.3: Validate metadata (correlationId required, UUID format, timestamp format)
  - [ ] Subtask 5.4: Validate actor field (known types, non-empty id)
  - [ ] Subtask 5.5: Wire validators into event store write path

- [ ] Task 6: Documentation and examples (AC: 10)
  - [ ] Subtask 6.1: Create event catalog markdown document with all types
  - [ ] Subtask 6.2: Provide JSON examples for each event category
  - [ ] Subtask 6.3: Document event correlation patterns (how correlationId/causationId chain)
  - [ ] Subtask 6.4: Document event versioning strategy (additive-only for minor versions)

- [ ] Task 7: Migration from existing event types (AC: all)
  - [ ] Subtask 7.1: Map current 18 `EngineEventType` values to new event types
  - [ ] Subtask 7.2: Create backward-compatible adapter for existing event consumers
  - [ ] Subtask 7.3: Define deprecation timeline for old event types

- [ ] Task 8: Testing (AC: all)
  - [ ] Subtask 8.1: Unit test each event type validator
  - [ ] Subtask 8.2: Test discriminated union narrowing works correctly in TypeScript
  - [ ] Subtask 8.3: Test UUID v7 generation produces time-sortable IDs
  - [ ] Subtask 8.4: Test correlation chain: event A causes B causes C
  - [ ] Subtask 8.5: Test schema version field presence and format

## Dev Notes

### Project Structure Notes

- New types: `packages/shared/src/types/events/` directory with per-category files
  - `base.ts` — BaseEvent, EventActor, EventMetadata
  - `intake.ts` — IntakeEvents
  - `decision.ts` — DecisionEvents
  - `queue.ts` — QueueEvents
  - `workflow.ts` — WorkflowEvents
  - `llm.ts` — LLMEvents, SanitizationItem
  - `security.ts` — SecurityEvents
  - `platform.ts` — PlatformEvents
  - `state.ts` — StateEvents
  - `index.ts` — Union type TammaEvent, EventType literal union
- New validation: `packages/shared/src/events/validators/`
- Modified: `packages/shared/src/types/index.ts` (re-export new event types, deprecate old)

### References

- **Epic 10 Tech Spec:** `docs/stories/epic-10/tech-spec-epic-10.md`
- **Current Event Types:** `packages/shared/src/types/index.ts` (lines 267-301)
- **Story 4.1 Event Schema Design:** `docs/stories/epic-4/story-4-1/4-1-event-schema-design.md`
- **Story 4.4 AI Provider Events:** `docs/stories/epic-4/story-4-4/`

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-26 | 1.0 | Initial story creation | Architecture Team |

## Logging Requirements

Engine core is the most critical path — logging must be comprehensive without being noisy.

- **INFO**: Engine started/stopped, workflow dispatched (workflow ID, issue ID), step transition (from state -> to state), queue item enqueued/dequeued
- **DEBUG**: State reconstruction details, event replay progress, queue deduplication decisions, ELSA workflow variable snapshots
- **WARN**: Queue backpressure detected, state reconstruction took >5s, event gap in stream, workflow execution slow
- **ERROR**: Engine crash (with full context for restart), state reconstruction failed, event store unreachable, workflow dispatch failed, queue corruption
- **Structured context**: Always include `{ workflowInstanceId, issueId, engineState, queueDepth }`
- **Idempotency**: Log enough context to verify idempotent replay (event IDs, sequence numbers, dedup keys)
