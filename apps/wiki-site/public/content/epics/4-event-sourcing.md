---
title: "Epic 4: Event Sourcing & Audit Trail"
sidebar:
  order: 4
---

**Status:** Ready for Dev (all 8 stories ready)
**Stories:** 8 (4-1 through 4-8)
**Task Plans:** 0
**Tech Spec:** [tech-spec-epic-4.md](/stories/epic-4//tech-spec-epic-4.md)

## Overview

Epic 4 implements CQRS event sourcing for complete transparency and audit compliance. Every user action, AI action, and system state change is captured as an immutable event with millisecond precision. The DCB (Dynamic Consistency Boundary) pattern uses a single PostgreSQL stream with JSONB tags for flexible querying.

## Goals

1. Design comprehensive event schema covering all system actions
2. Implement persistent, append-only event store (PostgreSQL + Emmett)
3. Capture events for issue selection, AI interactions, code changes, Git operations
4. Capture approval and escalation events
5. Build event query API for time-travel debugging
6. Implement black-box replay for diagnosing past behavior

## Value Delivered

- **100% Traceability**: Complete audit trail of every action
- **Compliance Ready**: SOC2, ISO27001, GDPR audit requirements
- **Time-Travel Debugging**: Reconstruct system state at any point in time
- **Black-Box Replay**: Reproduce and diagnose issues from exact event history

## Stories

| Story | Title | Status |
|-------|-------|--------|
| 4-1 | Event Schema Design | Ready for Dev |
| 4-2 | Event Store Backend Selection | Ready for Dev |
| 4-3 | Event Capture -- Issue Selection & Analysis | Ready for Dev |
| 4-4 | Event Capture -- AI Provider Interactions | Ready for Dev |
| 4-5 | Event Capture -- Code Changes & Git Operations | Ready for Dev |
| 4-6 | Event Capture -- Approvals & Escalations | Ready for Dev |
| 4-7 | Event Query API for Time-Travel | Ready for Dev |
| 4-8 | Black-Box Replay for Debugging | Ready for Dev |

## Key Technical Details

### Event Schema (DCB Pattern)

```typescript
interface DomainEvent {
  id: string;                    // UUID v7 (time-sortable)
  type: string;                  // "CODE.GENERATED.SUCCESS"
  timestamp: string;             // ISO 8601 millisecond precision
  tags: {                        // JSONB for flexible queries
    issueId?: string;
    prId?: string;
    userId?: string;
    mode?: 'dev' | 'business';
    provider?: string;
  };
  metadata: {
    workflowVersion: string;
    eventSource: 'system' | 'plugin';
  };
  data: Record<string, unknown>;
}
```

### Event Types

Events follow the pattern `AGGREGATE.ACTION.STATUS`:
- `ISSUE.ASSIGNED.SUCCESS`
- `CODE.GENERATED.SUCCESS` / `CODE.GENERATED.FAILED`
- `GATE.REVIEW_REQUESTED`
- `WORKFLOW.STEP_COMPLETED`

### Event Categories

| Category | Events | Story |
|----------|--------|-------|
| Issue Selection & Analysis | `IssueSelectedEvent`, `IssueAnalysisCompletedEvent` | 4-3 |
| AI Provider Interactions | `AIRequestEvent`, `AIResponseEvent` | 4-4 |
| Code Changes & Git Ops | `CodeFileWrittenEvent`, `CommitCreatedEvent`, `BranchCreatedEvent`, `PRCreatedEvent`, `PRMergedEvent` | 4-5 |
| Approvals & Escalations | `ApprovalRequestedEvent`, `ApprovalProvidedEvent`, `EscalationTriggeredEvent`, `EscalationResolvedEvent` | 4-6 |

### Time-Travel Query API

```
GET /events?since={timestamp}&until={timestamp}&type={type}&correlationId={id}
```
- Chronological ordering with pagination (100 events/page)
- Filtering by event type, actor, correlation ID, issue number
- Projection queries for point-in-time state reconstruction
- Performance target: < 1 second for 1M events

### Black-Box Replay

```bash
tamma replay --correlation-id {id} --timestamp {timestamp}
```
- Reconstructs system state by replaying events
- Interactive step-by-step mode with `--interactive`
- Exports to HTML report
- Performance target: < 5 seconds for typical cycle (50-100 events)

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| AI Provider Interface | Epic 1 | Events capture AI interactions |
| Git Platform Interface | Epic 1 | Events capture Git operations |
| Autonomous Loop | Epic 2 | Loop steps emit events |
| Quality Gates | Epic 3 | Gate results captured as events |
| Engine Core | Epic 10 | Production event store implementation |

## Related

- Epic 10 (Engine Core) implemented a production-ready event store with PostgreSQL/Emmett in Story 10-3
- Epic 4 stories define the original spec; some overlap with Epic 10 implementation

## Story Files

[Story documents on GitHub](/stories/epic-4/)
