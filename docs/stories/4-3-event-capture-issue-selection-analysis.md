# Story 4.3: Event Capture - Issue Selection & Analysis

Status: ready-for-dev

## Story

As a **compliance officer**,
I want all issue selection and analysis actions captured as events,
so that I can audit which issues were selected and why decisions were made.

## Acceptance Criteria

1. `IssueSelectedEvent` captured when issue is selected (Story 2.1) including issue ID, title, labels, selection criteria
2. `IssueAnalysisCompletedEvent` captured after analysis (Story 2.2) including context summary length, referenced issues
3. Events include actor (system in orchestrator mode, CI runner in worker mode)
4. Events include correlation ID linking entire development cycle
5. Events persisted to event store before proceeding to next step
6. Event write failures trigger retry (3 attempts) then halt autonomous loop for data integrity

## Tasks / Subtasks

- [ ] Task 1: Implement issue selection event capture (AC: 1)
  - [ ] Subtask 1.1: Create IssueSelectedEvent schema and payload structure
  - [ ] Subtask 1.2: Integrate event capture into Story 2.1 issue selection logic
  - [ ] Subtask 1.3: Add selection criteria and scoring data to event payload
  - [ ] Subtask 1.4: Implement event validation and error handling
  - [ ] Subtask 1.5: Add event capture unit tests

- [ ] Task 2: Implement issue analysis event capture (AC: 2)
  - [ ] Subtask 2.1: Create IssueAnalysisCompletedEvent schema and payload
  - [ ] Subtask 2.2: Integrate event capture into Story 2.2 analysis completion
  - [ ] Subtask 2.3: Add analysis metrics and context data to event payload
  - [ ] Subtask 2.4: Implement analysis result validation before event capture
  - [ ] Subtask 2.5: Add analysis event capture unit tests

- [ ] Task 3: Implement actor and correlation tracking (AC: 3, 4)
  - [ ] Subtask 3.1: Create actor identification service (system vs user vs CI)
  - [ ] Subtask 3.2: Implement correlation ID generation and propagation
  - [ ] Subtask 3.3: Add workflow instance tracking across multiple steps
  - [ ] Subtask 3.4: Create causation chain linking for event relationships
  - [ ] Subtask 3.5: Add correlation tracking integration tests

- [ ] Task 4: Implement synchronous event persistence (AC: 5)
  - [ ] Subtask 4.1: Create event store client with write guarantees
  - [ ] Subtask 4.2: Implement transactional event writing
  - [ ] Subtask 4.3: Add event persistence validation before workflow continuation
  - [ ] Subtask 4.4: Create event write failure detection and handling
  - [ ] Subtask 4.5: Add persistence integration tests

- [ ] Task 5: Implement retry and halt logic (AC: 6)
  - [ ] Subtask 5.1: Create event write retry mechanism with exponential backoff
  - [ ] Subtask 5.2: Implement retry limit enforcement (3 attempts)
  - [ ] Subtask 5.3: Add autonomous loop halt on event store failure
  - [ ] Subtask 5.4: Create error escalation for persistent failures
  - [ ] Subtask 5.5: Add retry and halt logic integration tests

- [ ] Task 6: Add event capture monitoring and metrics (AC: all)
  - [ ] Subtask 6.1: Create event capture metrics (success rate, latency)
  - [ ] Subtask 6.2: Implement event capture health checks
  - [ ] Subtask 6.3: Add event capture logging and debugging
  - [ ] Subtask 6.4: Create event capture dashboard integration
  - [ ] Subtask 6.5: Add monitoring integration tests

## Dev Notes

### Requirements Context Summary

**Epic 4 Integration:** This story implements the first phase of event capture, focusing on the initial autonomous loop steps (issue selection and analysis). These events provide the foundation for audit trails and compliance reporting.

**Compliance Requirements:** Issue selection events must capture the decision-making process, including why specific issues were chosen and what criteria were used. This enables audit trails for autonomous decision-making and provides transparency for regulatory compliance.

**Data Integrity:** Events must be persisted before workflow continuation to ensure no data loss. Event store failures must halt the autonomous loop to maintain data integrity and prevent partial state updates.

### Implementation Guidance

**Event Schema Definitions:**

```typescript
interface IssueSelectedEvent extends BaseEvent {
  eventType: 'ISSUE.SELECTED.SUCCESS';
  payload: {
    issue: {
      id: string;
      number: number;
      title: string;
      description: string;
      url: string;
      repository: {
        name: string;
        owner: string;
        url: string;
      };
      labels: string[];
      assignees: string[];
      state: 'open' | 'closed';
      createdAt: string;
      updatedAt: string;
    };
    selectionCriteria: {
      priority: number;
      complexity: 'low' | 'medium' | 'high';
      estimatedEffort: number;
      dependencies: string[];
      riskLevel: 'low' | 'medium' | 'high';
    };
    selectionScore: number;
    selectionReason: string;
    alternativeIssues: Array<{
      id: string;
      number: number;
      score: number;
      reason: string;
    }>;
  };
}

interface IssueAnalysisCompletedEvent extends BaseEvent {
  eventType: 'ISSUE.ANALYSIS.COMPLETED';
  payload: {
    issueId: string;
    analysis: {
      contextSummary: string;
      contextLength: number;
      referencedIssues: Array<{
        id: string;
        number: string;
        title: string;
        relevance: 'high' | 'medium' | 'low';
      }>;
      dependencies: Array<{
        type: 'code' | 'documentation' | 'external';
        description: string;
        status: 'resolved' | 'pending' | 'blocked';
      }>;
      requirements: Array<{
        type: 'functional' | 'non-functional' | 'constraint';
        description: string;
        priority: 'high' | 'medium' | 'low';
      }>;
      ambiguityScore: number; // 0-100, higher = more ambiguous
      complexityScore: number; // 0-100, higher = more complex
    };
    analysisDuration: number; // milliseconds
    aiProvider: {
      name: string;
      model: string;
      tokensUsed: number;
      cost: number;
    };
  };
}
```

**Event Capture Integration Points:**

```typescript
// Integration with Story 2.1 (Issue Selection)
class IssueSelectionService {
  constructor(
    private eventStore: IEventStore,
    private correlationManager: CorrelationManager
  ) {}

  async selectIssue(criteria: SelectionCriteria): Promise<SelectedIssue> {
    const correlationId = this.correlationManager.startWorkflow('issue-selection');

    try {
      // Issue selection logic
      const selectedIssue = await this.performSelection(criteria);

      // Capture event before returning
      const event: IssueSelectedEvent = {
        eventId: generateEventId(),
        timestamp: new Date().toISOString(),
        eventType: 'ISSUE.SELECTED.SUCCESS',
        actorType: 'system',
        actorId: 'tamma-orchestrator',
        correlationId,
        schemaVersion: '1.0.0',
        payload: {
          issue: selectedIssue,
          selectionCriteria: criteria,
          selectionScore: selectedIssue.score,
          selectionReason: selectedIssue.reason,
          alternativeIssues: criteria.alternatives,
        },
        metadata: {
          source: 'orchestrator',
          version: process.env.TAMMA_VERSION || '1.0.0',
          environment: process.env.NODE_ENV || 'development',
        },
      };

      // Synchronous event persistence
      await this.eventStore.append(event);

      return selectedIssue;
    } catch (error) {
      await this.handleEventCaptureError(error, correlationId);
      throw error;
    }
  }
}
```

**Retry and Halt Logic:**

```typescript
class EventCaptureService {
  private readonly MAX_RETRIES = 3;
  private readonly RETRY_DELAYS = [1000, 2000, 4000]; // Exponential backoff

  async captureEventWithRetry(event: BaseEvent): Promise<void> {
    let lastError: Error;

    for (let attempt = 1; attempt <= this.MAX_RETRIES; attempt++) {
      try {
        await this.eventStore.append(event);
        this.metrics.recordEventCaptureSuccess();
        return;
      } catch (error) {
        lastError = error as Error;
        this.metrics.recordEventCaptureFailure(error);

        if (attempt < this.MAX_RETRIES) {
          const delay = this.RETRY_DELAYS[attempt - 1];
          await this.sleep(delay);
          continue;
        }
      }
    }

    // All retries failed - halt autonomous loop
    await this.handleCriticalEventCaptureFailure(lastError!, event);
  }

  private async handleCriticalEventCaptureFailure(error: Error, event: BaseEvent): Promise<never> {
    // Log critical failure
    this.logger.critical('Event capture failed after retries', {
      error: error.message,
      eventId: event.eventId,
      correlationId: event.correlationId,
      eventType: event.eventType,
    });

    // Create escalation event if possible
    try {
      const escalationEvent: SystemEscalationEvent = {
        eventId: generateEventId(),
        timestamp: new Date().toISOString(),
        eventType: 'SYSTEM.ESCALATION.TRIGGERED',
        actorType: 'system',
        actorId: 'tamma-orchestrator',
        correlationId: event.correlationId,
        schemaVersion: '1.0.0',
        payload: {
          reason: 'Event store write failure',
          context: {
            failedEvent: event,
            error: error.message,
            retryCount: this.MAX_RETRIES,
          },
          severity: 'critical',
          requiresManualIntervention: true,
        },
        metadata: {
          source: 'orchestrator',
          version: process.env.TAMMA_VERSION || '1.0.0',
          environment: process.env.NODE_ENV || 'development',
        },
      };

      // Best effort to write escalation event
      await this.eventStore.append(escalationEvent);
    } catch (escalationError) {
      this.logger.critical('Failed to write escalation event', {
        error: escalationError.message,
      });
    }

    // Halt autonomous loop
    throw new EventCaptureCriticalError(
      'Event capture failed after retries',
      event.correlationId,
      error
    );
  }
}
```

### Technical Specifications

**Performance Requirements:**

- Event capture latency: <50ms per event
- Event persistence: Synchronous, blocking on success
- Retry overhead: <10 seconds total for failed events
- Memory usage: <10MB for event capture buffers

**Reliability Requirements:**

- Event capture success rate: >99.9%
- Data loss prevention: Halt on persistence failure
- Retry success rate: >95% for transient failures
- Error detection: Immediate failure identification

**Security Requirements:**

- Sensitive data masking: API keys, tokens, passwords
- Event integrity: Checksums and validation
- Access control: Event store write permissions
- Audit trail: All event capture attempts logged

**Monitoring Requirements:**

- Event capture metrics: Success rate, latency, retry count
- Health checks: Event store connectivity and performance
- Error tracking: Failed events, retry patterns
- Capacity planning: Event volume and storage growth

### Dependencies

**Internal Dependencies:**

- Story 4.1: Event schema design (provides event structures)
- Story 4.2: Event store backend selection (provides storage)
- Story 2.1: Issue selection with filtering (integration point)
- Story 2.2: Issue context analysis (integration point)

**External Dependencies:**

- Event store client library
- Correlation ID generation library
- Metrics collection library
- Logging framework

### Risks and Mitigations

| Risk                                    | Severity | Mitigation                            |
| --------------------------------------- | -------- | ------------------------------------- |
| Event store performance bottlenecks     | High     | Async processing, connection pooling  |
| Event capture failures halting workflow | Medium   | Circuit breaker, graceful degradation |
| Large event payloads causing issues     | Medium   | Payload truncation, blob storage      |
| Correlation ID propagation failures     | Low      | Robust correlation management         |

### Success Metrics

- [ ] Event capture success rate: >99.9%
- [ ] Event capture latency: <50ms per event
- [ ] Data integrity: 100% (no lost events)
- [ ] Retry success rate: >95% for transient failures
- [ ] Autonomous loop halt rate: <0.1% due to event capture

## Related

- Related story: `docs/stories/4-1-event-schema-design.md`
- Related story: `docs/stories/4-2-event-store-backend-selection.md`
- Related story: `docs/stories/2-1-issue-selection-with-filtering.md`
- Related story: `docs/stories/2-2-issue-context-analysis.md`
- Technical specification: `docs/tech-spec-epic-4.md`

## References

- [Event Sourcing Pattern](https://martinfowler.com/eaaDev/EventSourcing.html)
- [Retry Pattern Implementation](https://docs.microsoft.com/en-us/azure/architecture/patterns/retry)
- [Circuit Breaker Pattern](https://martinfowler.com/bliki/CircuitBreaker.html)
- [Correlation ID Best Practices](https://microservices.io/patterns/observability/correlation-id.html)
