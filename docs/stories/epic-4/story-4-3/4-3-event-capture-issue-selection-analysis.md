# Story 4.3: Event Capture - Issue Selection & Analysis

## Overview

Implement event capture for all issue selection and analysis actions to provide complete auditability of which issues are selected for autonomous development and why, ensuring compliance and transparency in the decision-making process.

## Acceptance Criteria

### Issue Selection Event Capture

- [ ] `IssueSelectedEvent` captured when issue is selected (Story 2.1) including issue ID, title, labels, selection criteria
- [ ] `IssueAnalysisCompletedEvent` captured after analysis (Story 2.2) including context summary length, referenced issues
- [ ] Events include actor (system in orchestrator mode, CI runner in worker mode)
- [ ] Events include correlation ID linking entire development cycle
- [ ] Events persisted to event store before proceeding to next step
- [ ] Event write failures trigger retry (3 attempts) then halt autonomous loop for data integrity

## Technical Context

### Event Capture Integration Points

This story integrates with the autonomous development workflow from Epic 2:

**Story 2.1 Integration - Issue Selection:**

```typescript
interface IssueSelectedEvent {
  eventId: string; // UUID v7
  timestamp: string; // ISO 8601 millisecond precision
  eventType: 'IssueSelected';
  actorType: 'system' | 'ci-runner';
  actorId: string; // System ID or CI runner ID
  payload: {
    issueId: string;
    title: string;
    description: string;
    labels: string[];
    assignee?: string;
    priority: 'low' | 'medium' | 'high' | 'critical';
    repository: {
      name: string;
      owner: string;
      platform: string; // github, gitlab, etc.
    };
    selectionCriteria: {
      labels?: string[];
      assignee?: string;
      priority?: string;
      customFilters?: Record<string, unknown>;
      excludedLabels?: string[];
      maxAge?: number; // days
    };
    selectionRationale: string; // Why this issue was chosen
    estimatedComplexity: 'low' | 'medium' | 'high';
    estimatedDuration: number; // minutes
  };
  metadata: {
    schemaVersion: '1.0.0';
    correlationId: string; // Links entire development cycle
    workflowId: string; // Workflow instance
    source: 'orchestrator' | 'worker';
    mode: 'dev' | 'business';
  };
}
```

**Story 2.2 Integration - Issue Analysis:**

```typescript
interface IssueAnalysisCompletedEvent {
  eventId: string;
  timestamp: string;
  eventType: 'IssueAnalysisCompleted';
  actorType: 'system' | 'ci-runner';
  actorId: string;
  payload: {
    issueId: string;
    analysisResults: {
      contextSummary: {
        length: number; // Character count
        tokenCount: number; // Estimated tokens
        sections: string[]; // Summary sections
      };
      referencedIssues: Array<{
        id: string;
        title: string;
        relevance: 'high' | 'medium' | 'low';
        relationship: 'duplicate' | 'related' | 'dependency' | 'blocked-by';
      }>;
      complexityAssessment: {
        score: number; // 1-10
        factors: string[]; // Complexity factors
        confidence: number; // 0-1
      };
      feasibilityAnalysis: {
        achievable: boolean;
        blockers: string[];
        prerequisites: string[];
        estimatedEffort: number; // hours
      };
      riskAssessment: {
        level: 'low' | 'medium' | 'high';
        factors: string[];
        mitigation: string[];
      };
    };
    analysisDuration: number; // milliseconds
    analysisModel: string; // AI model used
    analysisVersion: string; // Analysis algorithm version
  };
  metadata: {
    schemaVersion: '1.0.0';
    correlationId: string; // Same as IssueSelectedEvent
    workflowId: string;
    source: 'orchestrator' | 'worker';
    mode: 'dev' | 'business';
  };
}
```

### Event Capture Flow

```typescript
class IssueEventCapture {
  constructor(
    private eventStore: IEventStore,
    private retryPolicy: RetryPolicy
  ) {}

  async captureIssueSelection(
    issue: Issue,
    criteria: SelectionCriteria,
    correlationId: string
  ): Promise<void> {
    const event: IssueSelectedEvent = {
      eventId: generateUUIDv7(),
      timestamp: new Date().toISOString(),
      eventType: 'IssueSelected',
      actorType: this.getActorType(),
      actorId: this.getActorId(),
      payload: {
        issueId: issue.id,
        title: issue.title,
        description: issue.description,
        labels: issue.labels,
        assignee: issue.assignee,
        priority: issue.priority,
        repository: {
          name: issue.repository.name,
          owner: issue.repository.owner,
          platform: issue.repository.platform,
        },
        selectionCriteria: criteria,
        selectionRationale: this.generateSelectionRationale(issue, criteria),
        estimatedComplexity: this.assessComplexity(issue),
        estimatedDuration: this.estimateDuration(issue),
      },
      metadata: {
        schemaVersion: '1.0.0',
        correlationId,
        workflowId: this.getWorkflowId(),
        source: this.getSource(),
        mode: this.getMode(),
      },
    };

    await this.persistEventWithRetry(event);
  }

  async captureIssueAnalysis(
    issueId: string,
    analysis: AnalysisResults,
    correlationId: string
  ): Promise<void> {
    const event: IssueAnalysisCompletedEvent = {
      eventId: generateUUIDv7(),
      timestamp: new Date().toISOString(),
      eventType: 'IssueAnalysisCompleted',
      actorType: this.getActorType(),
      actorId: this.getActorId(),
      payload: {
        issueId,
        analysisResults: analysis,
        analysisDuration: analysis.duration,
        analysisModel: analysis.model,
        analysisVersion: analysis.version,
      },
      metadata: {
        schemaVersion: '1.0.0',
        correlationId,
        workflowId: this.getWorkflowId(),
        source: this.getSource(),
        mode: this.getMode(),
      },
    };

    await this.persistEventWithRetry(event);
  }

  private async persistEventWithRetry(event: DomainEvent): Promise<void> {
    await this.retryPolicy.execute(
      async () => {
        await this.eventStore.append(event);
      },
      {
        maxAttempts: 3,
        onFailure: () => {
          // Halt autonomous loop for data integrity
          throw new Error(`Failed to persist event ${event.eventId} after 3 attempts`);
        },
      }
    );
  }
}
```

### Integration with Workflow

The event capture must be integrated into the autonomous workflow:

```typescript
class AutonomousWorkflow {
  constructor(
    private issueEventCapture: IssueEventCapture,
    private issueSelector: IssueSelector,
    private issueAnalyzer: IssueAnalyzer
  ) {}

  async executeIssueSelection(correlationId: string): Promise<Issue> {
    // Select issue
    const issue = await this.issueSelector.selectIssue();

    // Capture selection event BEFORE proceeding
    await this.issueEventCapture.captureIssueSelection(
      issue,
      this.issueSelector.getCriteria(),
      correlationId
    );

    return issue;
  }

  async executeIssueAnalysis(issue: Issue, correlationId: string): Promise<AnalysisResults> {
    // Analyze issue
    const analysis = await this.issueAnalyzer.analyze(issue);

    // Capture analysis event BEFORE proceeding
    await this.issueEventCapture.captureIssueAnalysis(issue.id, analysis, correlationId);

    return analysis;
  }
}
```

### Error Handling and Data Integrity

**Event Persistence Failure:**

```typescript
class EventPersistenceError extends Error {
  constructor(
    public eventId: string,
    public attempt: number,
    public originalError: Error
  ) {
    super(`Failed to persist event ${eventId} after ${attempt} attempts`);
  }
}

// Retry policy configuration
const eventRetryPolicy = {
  maxAttempts: 3,
  baseDelay: 1000, // 1 second
  maxDelay: 10000, // 10 seconds
  backoffFactor: 2,
  retryableErrors: ['ConnectionError', 'TimeoutError', 'RateLimitError'],
};
```

**Data Integrity Guarantees:**

- Events are persisted BEFORE workflow proceeds to next step
- Failed event writes halt the autonomous loop
- All events in a workflow share the same correlation ID
- Event ordering is preserved through timestamp ordering

## Implementation Tasks

### 1. Event Schema Implementation

- [ ] Create `IssueSelectedEvent` and `IssueAnalysisCompletedEvent` schemas
- [ ] Implement TypeScript interfaces for event payloads
- [ ] Add JSON schema validation for both event types
- [ ] Create event builders with proper validation

### 2. Event Capture Service

- [ ] Implement `IssueEventCapture` class
- [ ] Add integration with issue selection (Story 2.1)
- [ ] Add integration with issue analysis (Story 2.2)
- [ ] Implement correlation ID management

### 3. Persistence Layer

- [ ] Implement retry policy for event persistence
- [ ] Add error handling for failed event writes
- [ ] Create data integrity checks
- [ ] Implement workflow halt on persistence failure

### 4. Workflow Integration

- [ ] Integrate event capture into autonomous workflow
- [ ] Ensure events are captured before workflow progression
- [ ] Add correlation ID propagation through workflow
- [ ] Implement actor identification (system vs CI runner)

### 5. Testing

- [ ] Unit tests for event capture service
- [ ] Integration tests with workflow
- [ ] Error handling and retry tests
- [ ] Data integrity validation tests

## Dependencies

### Internal Dependencies

- `@tamma/events` - Event schemas and validation
- `@tamma/workflow` - Autonomous workflow integration
- Story 2.1 - Issue Selection (for integration point)
- Story 2.2 - Issue Analysis (for integration point)
- Story 4.2 - Event Store Backend (for persistence)

### External Dependencies

- Event store backend (PostgreSQL, file system, or EventStore)

## Success Metrics

- 100% of issue selections captured as events
- 100% of issue analyses captured as events
- Zero data loss in event persistence
- Event capture adds <100ms overhead to workflow
- Failed event writes successfully halt workflow

## Risks and Mitigations

### Performance Risks

- **Risk**: Event capture may slow down workflow
- **Mitigation**: Async event persistence, optimized serialization

### Data Integrity Risks

- **Risk**: Event write failures may go unnoticed
- **Mitigation**: Retry policy, workflow halt on failure, comprehensive error handling

### Integration Risks

- **Risk**: Workflow integration may be complex
- **Mitigation**: Clear integration points, comprehensive testing, gradual rollout

## Notes

This story is critical for compliance and auditability. Every issue selection and analysis action must be captured with complete context to provide transparency into the autonomous decision-making process. The correlation ID linking enables complete reconstruction of entire development cycles for debugging and audit purposes.

The event capture must be synchronous to the workflow - events must be successfully persisted before the workflow can proceed to the next step. This ensures no actions are lost and provides a complete, ordered audit trail.
