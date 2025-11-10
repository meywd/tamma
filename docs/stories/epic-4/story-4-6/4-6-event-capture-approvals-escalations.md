# Story 4.6: Event Capture - Approvals & Escalations

## Overview

Implement comprehensive event capture for all approval workflows and escalation processes to ensure complete auditability of human intervention points, decision-making authority, and compliance requirements in autonomous development workflows.

## Acceptance Criteria

### Approval and Escalation Event Capture

- [ ] `ApprovalRequestedEvent` captured when human approval is needed including: approval type, requester, approver, context, deadline
- [ ] `ApprovalCompletedEvent` captured when approval is granted/denied including: decision, rationale, approver, timestamp
- [ ] `EscalationTriggeredEvent` captured when automatic escalation occurs including: reason, escalation level, target, context
- [ ] `EscalationCompletedEvent` captured when escalation is resolved including: resolution, final decision, resolver
- [ ] Events include full approval/escalation context in separate storage for detailed analysis
- [ ] Events persisted to event store synchronously before workflow proceeds

## Technical Context

### Event Capture Integration Points

This story integrates with the quality gates and human intervention systems from Epic 3:

**Approval Requested Event:**

```typescript
interface ApprovalRequestedEvent {
  eventId: string; // UUID v7
  timestamp: string; // ISO 8601 millisecond precision
  eventType: 'ApprovalRequested';
  actorType: 'system' | 'ci-runner' | 'user';
  actorId: string; // System ID or user ID
  payload: {
    approval: {
      id: string; // Unique approval request ID
      type:
        | 'code_review'
        | 'security_scan'
        | 'compliance_check'
        | 'deployment'
        | 'configuration_change';
      priority: 'low' | 'medium' | 'high' | 'critical';
      category: 'mandatory' | 'optional' | 'emergency';
      title: string; // Human-readable approval title
      description: string; // Detailed description of what needs approval
    };
    requester: {
      id: string;
      name: string;
      email: string;
      role: string; // system, developer, lead, admin
    };
    approvers: Array<{
      id: string;
      name: string;
      email: string;
      role: string;
      required: boolean; // Whether this approval is mandatory
      order: number; // Approval order for sequential approvals
    }>;
    context: {
      workflowId: string;
      correlationId: string;
      issueId?: string;
      prId?: string;
      commitId?: string;
      branchName?: string;
      environment?: string; // dev, staging, production
      step: string; // Workflow step requiring approval
    };
    deadline: {
      requested: string; // ISO 8601 timestamp
      urgency: 'normal' | 'urgent' | 'emergency';
      autoEscalate: boolean;
      escalationDelay: number; // hours
    };
    artifacts: Array<{
      type: 'file' | 'url' | 'report' | 'screenshot';
      name: string;
      location: string; // File path, URL, or storage reference
      size?: number;
      checksum?: string;
      description: string;
    }>;
    criteria: {
      checklist: Array<{
        id: string;
        item: string;
        required: boolean;
        checked: boolean;
        notes?: string;
      }>;
      conditions: Array<{
        type: 'file_exists' | 'test_passed' | 'security_clear' | 'compliance_met';
        description: string;
        status: 'pending' | 'met' | 'failed';
        details?: string;
      }>;
    };
    policies: {
      applicable: string[]; // Policy names/IDs that require this approval
      complianceLevel: 'basic' | 'standard' | 'strict';
      auditRequired: boolean;
      retentionPeriod: number; // days
    };
  };
  metadata: {
    schemaVersion: '1.0.0';
    correlationId: string; // Links entire development cycle
    workflowId: string;
    source: 'orchestrator' | 'worker' | 'quality_gate';
    mode: 'dev' | 'business';
    approvalId: string; // Same as payload.approval.id
  };
}
```

**Approval Completed Event:**

```typescript
interface ApprovalCompletedEvent {
  eventId: string;
  timestamp: string;
  eventType: 'ApprovalCompleted';
  actorType: 'user' | 'system';
  actorId: string;
  payload: {
    approval: {
      id: string; // Links to ApprovalRequestedEvent
      type: string;
      decision: 'approved' | 'denied' | 'approved_with_conditions' | 'escalated';
      finalDecision: 'approved' | 'denied';
    };
    approver: {
      id: string;
      name: string;
      email: string;
      role: string;
      authority: 'individual' | 'delegate' | 'escalated';
    };
    decision: {
      rationale: string; // Detailed explanation of decision
      conditions?: string[]; // Conditions for approval (if applicable)
      concerns?: string[]; // Concerns that led to denial
      suggestions?: string[]; // Suggestions for improvement
    };
    timing: {
      requestedAt: string; // From ApprovalRequestedEvent
      completedAt: string; // Current timestamp
      duration: number; // Time to decision in minutes
      firstViewAt?: string; // When approver first viewed request
      workingTime?: number; // Actual time spent reviewing
    };
    review: {
      artifactsReviewed: string[]; // List of artifacts examined
      checklistCompleted: Array<{
        id: string;
        item: string;
        checked: boolean;
        notes?: string;
      }>;
      findings: Array<{
        severity: 'info' | 'warning' | 'error' | 'critical';
        category: string;
        description: string;
        recommendation?: string;
      }>;
    };
    delegation?: {
      delegatedTo: {
        id: string;
        name: string;
        email: string;
        role: string;
      };
      reason: string;
      delegatedAt: string;
    };
    escalation?: {
      from: {
        id: string;
        role: string;
      };
      to: {
        id: string;
        role: string;
      };
      reason: string;
      escalatedAt: string;
    };
  };
  metadata: {
    schemaVersion: '1.0.0';
    correlationId: string; // Same as ApprovalRequestedEvent
    workflowId: string;
    source: 'quality_gate' | 'manual' | 'escalation';
    mode: 'dev' | 'business';
    approvalId: string; // Links to ApprovalRequestedEvent
  };
}
```

**Escalation Triggered Event:**

```typescript
interface EscalationTriggeredEvent {
  eventId: string;
  timestamp: string;
  eventType: 'EscalationTriggered';
  actorType: 'system' | 'user';
  actorId: string;
  payload: {
    escalation: {
      id: string; // Unique escalation ID
      type: 'timeout' | 'denial' | 'conflict' | 'emergency' | 'capacity';
      level: number; // 1=first escalation, 2=second, etc.
      priority: 'low' | 'medium' | 'high' | 'critical';
      automatic: boolean; // Whether triggered automatically
    };
    trigger: {
      reason: string; // Why escalation was triggered
      sourceEventId: string; // Event that triggered escalation
      sourceType: 'approval_timeout' | 'approval_denied' | 'conflict' | 'manual';
      context: string; // Additional context
    };
    from: {
      id: string;
      name: string;
      role: string;
      email: string;
    };
    to: {
      id: string;
      name: string;
      role: string;
      email: string;
      authority: 'primary' | 'backup' | 'management' | 'emergency';
    };
    context: {
      workflowId: string;
      correlationId: string;
      approvalId?: string; // If escalation from approval
      issueId?: string;
      prId?: string;
      originalDeadline?: string;
      newDeadline?: string;
    };
    urgency: {
      level: 'normal' | 'urgent' | 'emergency';
      justification: string;
      responseRequired: boolean;
      responseDeadline: string;
    };
    history: Array<{
      timestamp: string;
      level: number;
      from: string;
      to: string;
      reason: string;
      outcome?: string;
    }>;
  };
  metadata: {
    schemaVersion: '1.0.0';
    correlationId: string;
    workflowId: string;
    source: 'system' | 'manual';
    mode: 'dev' | 'business';
    escalationId: string; // Same as payload.escalation.id
  };
}
```

**Escalation Completed Event:**

```typescript
interface EscalationCompletedEvent {
  eventId: string;
  timestamp: string;
  eventType: 'EscalationCompleted';
  actorType: 'user' | 'system';
  actorId: string;
  payload: {
    escalation: {
      id: string; // Links to EscalationTriggeredEvent
      type: string;
      level: number;
      outcome: 'resolved' | 'escalated_further' | 'cancelled' | 'timeout';
    };
    resolver: {
      id: string;
      name: string;
      email: string;
      role: string;
      authority: string;
    };
    resolution: {
      decision: 'approved' | 'denied' | 'modified' | 'cancelled';
      rationale: string; // How the escalation was resolved
      finalAction: string; // What action was taken
      conditions?: string[]; // Any conditions applied
    };
    timing: {
      triggeredAt: string; // From EscalationTriggeredEvent
      completedAt: string; // Current timestamp
      duration: number; // Time to resolution in minutes
      firstResponseAt?: string;
      workingTime?: number;
    };
    impact: {
      workflowImpact: 'continued' | 'modified' | 'halted' | 'restarted';
      delayImpact: number; // Additional delay in minutes
      costImpact?: number; // Additional cost if applicable
      qualityImpact: 'none' | 'minor' | 'major' | 'critical';
    };
    followUp: {
      required: boolean;
      actions?: string[];
      assignedTo?: string;
      deadline?: string;
    };
    lessons: {
      rootCause?: string;
      prevention?: string;
      processImprovement?: string;
    };
  };
  metadata: {
    schemaVersion: '1.0.0';
    correlationId: string; // Same as EscalationTriggeredEvent
    workflowId: string;
    source: 'manual' | 'automatic';
    mode: 'dev' | 'business';
    escalationId: string; // Links to EscalationTriggeredEvent
  };
}
```

### Event Capture Integration

```typescript
class ApprovalEventCapture {
  constructor(
    private eventStore: IEventStore,
    private contentStorage: ApprovalContentStorage,
    private notificationService: NotificationService
  ) {}

  async captureApprovalRequest(request: ApprovalRequest, correlationId: string): Promise<string> {
    // Store full approval context in blob storage
    const contextStorageId = await this.contentStorage.storeApprovalContext(
      request.id,
      {
        fullDescription: request.description,
        detailedCriteria: request.criteria,
        artifactDetails: request.artifacts,
        policyDetails: request.policies,
        additionalContext: request.additionalContext,
      },
      {
        approvalId: request.id,
        type: 'approval_context',
        timestamp: new Date().toISOString(),
        size: JSON.stringify(request).length,
        retentionPeriod: this.calculateRetentionPeriod(request),
        classification: this.classifyApproval(request),
      }
    );

    // Create event
    const event: ApprovalRequestedEvent = {
      eventId: generateUUIDv7(),
      timestamp: new Date().toISOString(),
      eventType: 'ApprovalRequested',
      actorType: this.getActorType(),
      actorId: this.getActorId(),
      payload: {
        approval: {
          id: request.id,
          type: request.type,
          priority: request.priority,
          category: request.category,
          title: request.title,
          description:
            request.description.length > 500
              ? request.description.substring(0, 500) + '...'
              : request.description,
        },
        requester: request.requester,
        approvers: request.approvers,
        context: request.context,
        deadline: request.deadline,
        artifacts: request.artifacts.map((a) => ({
          ...a,
          description:
            a.description.length > 200 ? a.description.substring(0, 200) + '...' : a.description,
        })),
        criteria: request.criteria,
        policies: request.policies,
      },
      metadata: {
        schemaVersion: '1.0.0',
        correlationId,
        workflowId: this.getWorkflowId(),
        source: this.getSource(),
        mode: this.getMode(),
        approvalId: request.id,
      },
    };

    // Persist event synchronously
    await this.eventStore.append(event);

    // Send notifications to approvers
    await this.notificationService.notifyApprovers(request.approvers, event);

    return event.eventId;
  }

  async captureApprovalDecision(
    approvalId: string,
    decision: ApprovalDecision,
    correlationId: string
  ): Promise<string> {
    // Store full decision details
    const decisionStorageId = await this.contentStorage.storeApprovalDecision(
      approvalId,
      decision,
      {
        approvalId,
        type: 'approval_decision',
        timestamp: new Date().toISOString(),
        size: JSON.stringify(decision).length,
        retentionPeriod: 365, // 1 year for decisions
        classification: 'internal',
      }
    );

    // Create event
    const event: ApprovalCompletedEvent = {
      eventId: generateUUIDv7(),
      timestamp: new Date().toISOString(),
      eventType: 'ApprovalCompleted',
      actorType: decision.approver.role === 'system' ? 'system' : 'user',
      actorId: decision.approver.id,
      payload: {
        approval: {
          id: approvalId,
          type: decision.type,
          decision: decision.decision,
          finalDecision: decision.finalDecision,
        },
        approver: decision.approver,
        decision: {
          rationale: decision.rationale,
          conditions: decision.conditions,
          concerns: decision.concerns,
          suggestions: decision.suggestions,
        },
        timing: decision.timing,
        review: decision.review,
        delegation: decision.delegation,
        escalation: decision.escalation,
      },
      metadata: {
        schemaVersion: '1.0.0',
        correlationId,
        workflowId: this.getWorkflowId(),
        source: this.getSource(),
        mode: this.getMode(),
        approvalId,
      },
    };

    // Persist event synchronously
    await this.eventStore.append(event);

    return event.eventId;
  }

  async captureEscalationTriggered(
    escalation: EscalationTrigger,
    correlationId: string
  ): Promise<string> {
    const event: EscalationTriggeredEvent = {
      eventId: generateUUIDv7(),
      timestamp: new Date().toISOString(),
      eventType: 'EscalationTriggered',
      actorType: escalation.automatic ? 'system' : 'user',
      actorId: escalation.automatic ? 'system' : escalation.from.id,
      payload: {
        escalation: {
          id: escalation.id,
          type: escalation.type,
          level: escalation.level,
          priority: escalation.priority,
          automatic: escalation.automatic,
        },
        trigger: escalation.trigger,
        from: escalation.from,
        to: escalation.to,
        context: escalation.context,
        urgency: escalation.urgency,
        history: escalation.history,
      },
      metadata: {
        schemaVersion: '1.0.0',
        correlationId,
        workflowId: this.getWorkflowId(),
        source: escalation.automatic ? 'system' : 'manual',
        mode: this.getMode(),
        escalationId: escalation.id,
      },
    };

    // Persist event synchronously
    await this.eventStore.append(event);

    // Send escalation notifications
    await this.notificationService.notifyEscalation(escalation.to, event);

    return event.eventId;
  }

  async captureEscalationCompleted(
    escalationId: string,
    resolution: EscalationResolution,
    correlationId: string
  ): Promise<string> {
    const event: EscalationCompletedEvent = {
      eventId: generateUUIDv7(),
      timestamp: new Date().toISOString(),
      eventType: 'EscalationCompleted',
      actorType: resolution.resolver.role === 'system' ? 'system' : 'user',
      actorId: resolution.resolver.id,
      payload: {
        escalation: {
          id: escalationId,
          type: resolution.type,
          level: resolution.level,
          outcome: resolution.outcome,
        },
        resolver: resolution.resolver,
        resolution: {
          decision: resolution.decision,
          rationale: resolution.rationale,
          finalAction: resolution.finalAction,
          conditions: resolution.conditions,
        },
        timing: resolution.timing,
        impact: resolution.impact,
        followUp: resolution.followUp,
        lessons: resolution.lessons,
      },
      metadata: {
        schemaVersion: '1.0.0',
        correlationId,
        workflowId: this.getWorkflowId(),
        source: this.getSource(),
        mode: this.getMode(),
        escalationId,
      },
    };

    // Persist event synchronously
    await this.eventStore.append(event);

    return event.eventId;
  }
}
```

## Implementation Tasks

### 1. Event Schema Implementation

- [ ] Create approval and escalation event schemas
- [ ] Implement TypeScript interfaces for all event payloads
- [ ] Add JSON schema validation for all event types
- [ ] Create event builders with proper validation

### 2. Content Storage System

- [ ] Implement `ApprovalContentStorage` for full approval contexts
- [ ] Add retention policy management based on approval type
- [ ] Create content retrieval and audit capabilities
- [ ] Implement secure storage with access controls

### 3. Event Capture Service

- [ ] Implement `ApprovalEventCapture` class
- [ ] Add integration with approval workflow systems
- [ ] Implement notification integration
- [ ] Add synchronous event persistence

### 4. Workflow Integration

- [ ] Integrate event capture into quality gate systems
- [ ] Ensure events are captured before workflow progression
- [ ] Add correlation ID propagation through approval chains
- [ ] Implement escalation trigger detection

### 5. Testing

- [ ] Unit tests for event capture service
- [ ] Integration tests with approval workflows
- [ ] Escalation scenario testing
- [ ] Content storage and retrieval tests

## Dependencies

### Internal Dependencies

- `@tamma/events` - Event schemas and validation
- `@tamma/workflow` - Autonomous workflow integration
- `@tamma/gates` - Quality gate integration
- Story 4.2 - Event Store Backend (for persistence)

### External Dependencies

- Notification service (email, Slack, Teams, etc.)
- Approval workflow system
- User directory/authentication system

## Success Metrics

- 100% of approval requests captured as events
- 100% of approval decisions captured as events
- 100% of escalations captured as events
- Event capture adds <150ms overhead to approval workflows
- Complete audit trail for all human intervention points

## Risks and Mitigations

### Privacy Risks

- **Risk**: Personal information in approval events may violate privacy
- **Mitigation**: Data classification, access controls, retention policies

### Performance Risks

- **Risk**: Event capture may slow down approval workflows
- **Mitigation**: Async content storage, optimized serialization

### Compliance Risks

- **Risk**: Approval decisions may not be properly audited
- **Mitigation**: Complete event capture, immutable storage, correlation tracking

### Integration Risks

- **Risk**: Complex approval workflows may be difficult to integrate
- **Mitigation**: Clear integration points, comprehensive testing, gradual rollout

## Notes

This story is critical for compliance and auditability of human intervention points. Every approval request, decision, and escalation must be captured with complete context to provide transparency into the governance process. The correlation ID linking enables complete reconstruction of approval chains for audit and analysis purposes.

The synchronous persistence ensures no approval actions are lost, while the separate content storage allows for detailed documentation without bloating the main event store. This supports compliance requirements while maintaining system performance.
