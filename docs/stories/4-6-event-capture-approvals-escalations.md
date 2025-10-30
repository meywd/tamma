# Story 4.6: Event Capture - Approvals & Escalations

Status: ready-for-dev

## Story

As a **audit team**,
I want all user approvals and system escalations captured as events,
so that I can verify human oversight and understand when system needed help.

## Acceptance Criteria

1. `ApprovalRequestedEvent` captured when system requests user approval (plan, merge, etc.) including: approval type, context summary
2. `ApprovalProvidedEvent` captured when user responds including: decision (approved/rejected/edited), timestamp, user identity
3. `EscalationTriggeredEvent` captured when retry limits exhausted (Story 3.3) including: escalation reason, retry history, current state
4. `EscalationResolvedEvent` captured when human resolves escalation including: resolution description, time to resolve
5. Events support approval audit trail for compliance (who approved what when)
6. Events capture approval channel (CLI interactive, API call, webhook)

## Tasks / Subtasks

- [ ] Task 1: Implement approval request event capture (AC: 1)
  - [ ] Subtask 1.1: Create ApprovalRequestedEvent schema and payload structure
  - [ ] Subtask 1.2: Integrate event capture into approval request logic
  - [ ] Subtask 1.3: Add approval type classification and context
  - [ ] Subtask 1.4: Implement approval timeout tracking
  - [ ] Subtask 1.5: Add approval request event capture unit tests

- [ ] Task 2: Implement approval response event capture (AC: 2)
  - [ ] Subtask 2.1: Create ApprovalProvidedEvent schema and payload structure
  - [ ] Subtask 2.2: Integrate event capture into approval response handling
  - [ ] Subtask 2.3: Add user identity and authentication tracking
  - [ ] Subtask 2.4: Implement approval decision validation
  - [ ] Subtask 2.5: Add approval response event capture unit tests

- [ ] Task 3: Implement escalation trigger event capture (AC: 3)
  - [ ] Subtask 3.1: Create EscalationTriggeredEvent schema and payload structure
  - [ ] Subtask 3.2: Integrate event capture into escalation logic (Story 3.3)
  - [ ] Subtask 3.3: Add retry history and failure analysis
  - [ ] Subtask 3.4: Implement escalation severity classification
  - [ ] Subtask 3.5: Add escalation trigger event capture unit tests

- [ ] Task 4: Implement escalation resolution event capture (AC: 4)
  - [ ] Subtask 4.1: Create EscalationResolvedEvent schema and payload structure
  - [ ] Subtask 4.2: Integrate event capture into escalation resolution
  - [ ] Subtask 4.3: Add resolution time tracking and analysis
  - [ ] Subtask 4.4: Implement resolution effectiveness validation
  - [ ] Subtask 4.5: Add escalation resolution event capture unit tests

- [ ] Task 5: Implement approval audit trail (AC: 5)
  - [ ] Subtask 5.1: Create approval chain linking and tracking
  - [ ] Subtask 5.2: Implement approval history reconstruction
  - [ ] Subtask 5.3: Add compliance reporting capabilities
  - [ ] Subtask 5.4: Create approval audit queries and filters
  - [ ] Subtask 5.5: Add audit trail validation tests

- [ ] Task 6: Implement approval channel capture (AC: 6)
  - [ ] Subtask 6.1: Create channel detection and classification
  - [ ] Subtask 6.2: Implement CLI interactive approval tracking
  - [ ] Subtask 6.3: Add API call approval tracking
  - [ ] Subtask 6.4: Implement webhook approval tracking
  - [ ] Subtask 6.5: Add channel capture integration tests

## Dev Notes

### Requirements Context Summary

**Epic 4 Integration:** This story captures all human oversight events to provide a complete audit trail of when autonomous systems required human intervention. These events are critical for compliance, safety, and system improvement.

**Compliance Requirements:** Approval events must capture who approved what, when, and why to satisfy regulatory requirements for human oversight of autonomous systems. This enables audit trails for SOX, SOC2, and other compliance frameworks.

**Safety Requirements:** Escalation events must capture when and why the autonomous system failed, providing insights for system improvement and preventing future failures.

### Implementation Guidance

**Event Schema Definitions:**

```typescript
interface ApprovalRequestedEvent extends BaseEvent {
  eventType: 'APPROVAL.REQUESTED';
  payload: {
    approval: {
      id: string; // Unique approval request ID
      type:
        | 'development_plan'
        | 'code_review'
        | 'pull_request_merge'
        | 'configuration_change'
        | 'escalation_resolution';
      title: string; // Human-readable title
      description: string; // Detailed description of what needs approval
      priority: 'low' | 'medium' | 'high' | 'critical';
      deadline?: string; // Approval deadline (ISO 8601)
    };
    context: {
      correlationId: string; // Workflow correlation
      stepId: string; // Current workflow step
      issueId?: string; // Related issue
      prId?: string; // Related PR
      projectId?: string; // Related project
      repository?: {
        name: string;
        owner: string;
        url: string;
      };
    };
    request: {
      actor: 'system' | 'ai' | 'user';
      actorId: string; // System component or user ID
      reason: string; // Why approval is needed
      alternatives: Array<{
        // Alternative approaches considered
        description: string;
        pros: string[];
        cons: string[];
        rejected: boolean;
        reason?: string;
      }>;
      risks: Array<{
        // Potential risks of approval/rejection
        type: 'security' | 'quality' | 'performance' | 'compliance' | 'operational';
        description: string;
        probability: 'low' | 'medium' | 'high';
        impact: 'low' | 'medium' | 'high';
      }>;
    };
    content: {
      summary: string; // Executive summary (max 500 chars)
      details: string; // Full details (truncated in event)
      attachments: Array<{
        // Supporting documents
        type: 'file' | 'url' | 'diff' | 'screenshot';
        name: string;
        description: string;
        blobKey?: string; // Key for file in blob storage
        url?: string;
      }>;
      metadata: {
        estimatedEffort?: number; // Hours of work
        estimatedCost?: number; // Monetary cost
        dependencies: string[]; // Dependencies
        impact: string; // Impact description
      };
    };
    channel: {
      type: 'cli_interactive' | 'api_call' | 'webhook' | 'email' | 'slack';
      target: string; // User ID, email, webhook URL, etc.
      instructions: string; // How to respond
      responseFormat: {
        type: 'boolean' | 'choice' | 'text' | 'edit';
        options?: string[]; // For choice type
        schema?: object; // For structured responses
      };
    };
    timeout: {
      duration: number; // Timeout in minutes
      action: 'auto_approve' | 'auto_reject' | 'escalate';
      message: string; // Timeout message
    };
  };
}

interface ApprovalProvidedEvent extends BaseEvent {
  eventType: 'APPROVAL.PROVIDED';
  payload: {
    approval: {
      id: string; // Matches ApprovalRequestedEvent.id
      type: string; // Matches request type
    };
    decision: {
      action: 'approved' | 'rejected' | 'edited' | 'deferred';
      confidence: number; // 0-100, decision confidence
      rationale: string; // Why this decision was made
    };
    response: {
      actor: {
        type: 'user' | 'system' | 'api';
        id: string; // User ID, API key, or system component
        name: string; // Human-readable name
        email?: string; // User email
        role?: string; // User role/permissions
      };
      timestamp: string; // When decision was made
      channel: string; // How response was provided
      duration: number; // Time from request to response (minutes)
    };
    modifications?: {
      type: 'text_edit' | 'file_change' | 'parameter_adjustment';
      description: string;
      before: string; // Original content
      after: string; // Modified content
      blobKey?: string; // Key for diff in blob storage
    };
    conditions?: {
      type: 'conditional_approval' | 'requirements' | 'monitoring';
      description: string;
      criteria: Array<{
        description: string;
        measurable: boolean;
        target?: string;
        deadline?: string;
      }>;
    };
    context: {
      correlationId: string;
      stepId: string;
      issueId?: string;
      prId?: string;
    };
  };
}

interface EscalationTriggeredEvent extends BaseEvent {
  eventType: 'SYSTEM.ESCALATION.TRIGGERED';
  payload: {
    escalation: {
      id: string; // Unique escalation ID
      type:
        | 'retry_exhausted'
        | 'error_critical'
        | 'timeout'
        | 'approval_timeout'
        | 'resource_exhausted';
      severity: 'low' | 'medium' | 'high' | 'critical';
      title: string; // Human-readable title
      description: string; // Detailed description
    };
    trigger: {
      component: string; // System component that triggered escalation
      operation: string; // Operation that failed
      error?: {
        type: string;
        message: string;
        code?: string;
        stack?: string;
      };
      timestamp: string; // When escalation was triggered
    };
    context: {
      correlationId: string;
      stepId: string;
      issueId?: string;
      prId?: string;
      workflowState: {
        currentStep: string;
        completedSteps: string[];
        progress: number; // 0-100 percentage
      };
    };
    history: {
      attempts: number; // Total retry attempts
      failures: Array<{
        // History of failures
        timestamp: string;
        error: string;
        component: string;
        attempt: number;
        duration: number;
      }>;
      lastSuccess?: {
        timestamp: string;
        operation: string;
        duration: number;
      };
    };
    impact: {
      affectedItems: string[]; // What is affected
      blockedOperations: string[]; // What's blocked
      estimatedDelay: number; // Estimated delay in minutes
      costImpact?: number; // Estimated cost impact
    };
    resolution: {
      required: boolean; // Whether human intervention is required
      suggestedActions: Array<{
        action: string;
        description: string;
        priority: 'low' | 'medium' | 'high';
        estimatedTime: number; // Minutes to resolve
      }>;
      autoRecovery: {
        possible: boolean;
        strategy?: string;
        maxAttempts?: number;
      };
    };
    notification: {
      channels: string[]; // Notification channels used
      recipients: string[]; // Who was notified
      message: string; // Notification message
    };
  };
}

interface EscalationResolvedEvent extends BaseEvent {
  eventType: 'SYSTEM.ESCALATION.RESOLVED';
  payload: {
    escalation: {
      id: string; // Matches EscalationTriggeredEvent.id
      type: string;
      severity: string;
    };
    resolution: {
      action: 'fixed' | 'bypassed' | 'deferred' | 'cancelled';
      description: string; // What was done to resolve
      rootCause: string; // Why escalation occurred
      preventive: string; // How to prevent future occurrences
    };
    resolver: {
      actor: {
        type: 'user' | 'system' | 'automated';
        id: string;
        name: string;
        role?: string;
      };
      timestamp: string; // When resolution was applied
      duration: number; // Time from trigger to resolution (minutes)
      effort: {
        timeSpent: number; // Minutes spent resolving
        complexity: 'low' | 'medium' | 'high';
        resources: string[]; // Resources used
      };
    };
    outcome: {
      success: boolean; // Was resolution successful
      resumed: boolean; // Did workflow resume
      quality: 'excellent' | 'good' | 'acceptable' | 'poor';
      sideEffects?: string[]; // Any unexpected consequences
    };
    learning: {
      insights: string[]; // Key insights from resolution
      improvements: Array<{
        // Suggested improvements
        area: string;
        description: string;
        priority: 'low' | 'medium' | 'high';
        estimatedEffort: number;
      }>;
      knowledgeBase: {
        articleCreated: boolean;
        articleId?: string;
        tags: string[];
      };
    };
    context: {
      correlationId: string;
      stepId: string;
      issueId?: string;
      prId?: string;
    };
  };
}
```

**Approval System Integration:**

```typescript
class EventCapturingApprovalSystem {
  constructor(
    private eventStore: IEventStore,
    private blobStorage: IBlobStorage,
    private correlationManager: CorrelationManager,
    private notificationService: NotificationService
  ) {}

  async requestApproval(request: ApprovalRequest): Promise<string> {
    const approvalId = generateApprovalId();

    // Store detailed content in blob storage
    const contentBlobKey = await this.storeContentInBlob(approvalId, request.content);

    // Create approval request event
    const event: ApprovalRequestedEvent = {
      eventId: generateEventId(),
      timestamp: new Date().toISOString(),
      eventType: 'APPROVAL.REQUESTED',
      actorType: request.requestActor.type,
      actorId: request.requestActor.id,
      correlationId: this.correlationManager.getCurrentCorrelationId(),
      schemaVersion: '1.0.0',
      payload: {
        approval: {
          id: approvalId,
          type: request.type,
          title: request.title,
          description: request.description,
          priority: request.priority,
          deadline: request.deadline,
        },
        context: {
          correlationId: this.correlationManager.getCurrentCorrelationId(),
          stepId: this.correlationManager.getCurrentStepId(),
          issueId: this.correlationManager.getCurrentIssueId(),
          prId: this.correlationManager.getCurrentPrId(),
          projectId: request.projectId,
          repository: request.repository,
        },
        request: {
          actor: request.requestActor.type,
          actorId: request.requestActor.id,
          reason: request.reason,
          alternatives: request.alternatives || [],
          risks: request.risks || [],
        },
        content: {
          summary: request.content.summary,
          details: request.content.details.substring(0, 1000),
          attachments: await this.processAttachments(request.content.attachments),
          metadata: request.content.metadata,
        },
        channel: {
          type: request.channel.type,
          target: request.channel.target,
          instructions: request.channel.instructions,
          responseFormat: request.channel.responseFormat,
        },
        timeout: {
          duration: request.timeout.duration,
          action: request.timeout.action,
          message: request.timeout.message,
        },
      },
      metadata: {
        source: 'orchestrator',
        version: process.env.TAMMA_VERSION || '1.0.0',
        environment: process.env.NODE_ENV || 'development',
      },
    };

    // Store full content in blob if large
    if (request.content.details.length > 1000) {
      event.payload.content.blobKey = contentBlobKey;
    }

    // Persist event
    await this.eventStore.append(event);

    // Send notification
    await this.notificationService.sendApprovalRequest(request);

    // Set up timeout handler
    if (request.deadline) {
      this.scheduleApprovalTimeout(approvalId, request);
    }

    return approvalId;
  }

  async provideApproval(approvalId: string, response: ApprovalResponse): Promise<void> {
    const startTime = Date.now();

    // Get original request
    const requestEvent = await this.getApprovalRequestEvent(approvalId);
    if (!requestEvent) {
      throw new Error(`Approval request ${approvalId} not found`);
    }

    // Store modifications in blob storage if present
    let modificationsBlobKey: string | undefined;
    if (response.modifications) {
      modificationsBlobKey = await this.storeModificationsInBlob(
        approvalId,
        response.modifications
      );
    }

    // Create approval response event
    const event: ApprovalProvidedEvent = {
      eventId: generateEventId(),
      timestamp: new Date().toISOString(),
      eventType: 'APPROVAL.PROVIDED',
      actorType: response.actor.type,
      actorId: response.actor.id,
      correlationId: requestEvent.correlationId,
      schemaVersion: '1.0.0',
      payload: {
        approval: {
          id: approvalId,
          type: requestEvent.payload.approval.type,
        },
        decision: {
          action: response.action,
          confidence: response.confidence,
          rationale: response.rationale,
        },
        response: {
          actor: response.actor,
          timestamp: new Date().toISOString(),
          channel: response.channel,
          duration: Math.round((Date.now() - startTime) / 60000), // minutes
        },
        modifications: response.modifications
          ? {
              type: response.modifications.type,
              description: response.modifications.description,
              before: response.modifications.before,
              after: response.modifications.after,
              blobKey: modificationsBlobKey,
            }
          : undefined,
        conditions: response.conditions,
        context: {
          correlationId: requestEvent.correlationId,
          stepId: requestEvent.payload.context.stepId,
          issueId: requestEvent.payload.context.issueId,
          prId: requestEvent.payload.context.prId,
        },
      },
      metadata: {
        source: 'orchestrator',
        version: process.env.TAMMA_VERSION || '1.0.0',
        environment: process.env.NODE_ENV || 'development',
      },
    };

    // Persist event
    await this.eventStore.append(event);

    // Cancel timeout if set
    this.cancelApprovalTimeout(approvalId);

    // Process approval decision
    await this.processApprovalDecision(approvalId, response);
  }
}
```

**Escalation System Integration:**

```typescript
class EventCapturingEscalationSystem {
  constructor(
    private eventStore: IEventStore,
    private correlationManager: CorrelationManager,
    private retryManager: RetryManager
  ) {}

  async triggerEscalation(trigger: EscalationTrigger): Promise<string> {
    const escalationId = generateEscalationId();

    // Get retry history
    const retryHistory = await this.retryManager.getRetryHistory(trigger.correlationId);

    // Create escalation event
    const event: EscalationTriggeredEvent = {
      eventId: generateEventId(),
      timestamp: new Date().toISOString(),
      eventType: 'SYSTEM.ESCALATION.TRIGGERED',
      actorType: 'system',
      actorId: 'tamma-orchestrator',
      correlationId: trigger.correlationId,
      schemaVersion: '1.0.0',
      payload: {
        escalation: {
          id: escalationId,
          type: trigger.type,
          severity: trigger.severity,
          title: trigger.title,
          description: trigger.description,
        },
        trigger: {
          component: trigger.component,
          operation: trigger.operation,
          error: trigger.error,
          timestamp: new Date().toISOString(),
        },
        context: {
          correlationId: trigger.correlationId,
          stepId: trigger.stepId,
          issueId: trigger.issueId,
          prId: trigger.prId,
          workflowState: await this.getWorkflowState(trigger.correlationId),
        },
        history: {
          attempts: retryHistory.attempts,
          failures: retryHistory.failures,
          lastSuccess: retryHistory.lastSuccess,
        },
        impact: trigger.impact,
        resolution: {
          required: trigger.resolution.required,
          suggestedActions: trigger.resolution.suggestedActions,
          autoRecovery: trigger.resolution.autoRecovery,
        },
        notification: {
          channels: trigger.notification.channels,
          recipients: trigger.notification.recipients,
          message: trigger.notification.message,
        },
      },
      metadata: {
        source: 'orchestrator',
        version: process.env.TAMMA_VERSION || '1.0.0',
        environment: process.env.NODE_ENV || 'development',
      },
    };

    // Persist event
    await this.eventStore.append(event);

    // Send notifications
    await this.sendEscalationNotifications(escalationId, trigger);

    // Schedule escalation timeout if auto-recovery possible
    if (trigger.resolution.autoRecovery.possible) {
      this.scheduleAutoRecovery(escalationId, trigger);
    }

    return escalationId;
  }

  async resolveEscalation(escalationId: string, resolution: EscalationResolution): Promise<void> {
    // Get original escalation event
    const escalationEvent = await this.getEscalationEvent(escalationId);
    if (!escalationEvent) {
      throw new Error(`Escalation ${escalationId} not found`);
    }

    // Create resolution event
    const event: EscalationResolvedEvent = {
      eventId: generateEventId(),
      timestamp: new Date().toISOString(),
      eventType: 'SYSTEM.ESCALATION.RESOLVED',
      actorType: resolution.resolver.actor.type,
      actorId: resolution.resolver.actor.id,
      correlationId: escalationEvent.correlationId,
      schemaVersion: '1.0.0',
      payload: {
        escalation: {
          id: escalationId,
          type: escalationEvent.payload.escalation.type,
          severity: escalationEvent.payload.escalation.severity,
        },
        resolution: {
          action: resolution.action,
          description: resolution.description,
          rootCause: resolution.rootCause,
          preventive: resolution.preventive,
        },
        resolver: {
          actor: resolution.resolver.actor,
          timestamp: new Date().toISOString(),
          duration: resolution.resolver.duration,
          effort: resolution.resolver.effort,
        },
        outcome: {
          success: resolution.outcome.success,
          resumed: resolution.outcome.resumed,
          quality: resolution.outcome.quality,
          sideEffects: resolution.outcome.sideEffects,
        },
        learning: {
          insights: resolution.learning.insights,
          improvements: resolution.learning.improvements,
          knowledgeBase: resolution.learning.knowledgeBase,
        },
        context: {
          correlationId: escalationEvent.correlationId,
          stepId: escalationEvent.payload.context.stepId,
          issueId: escalationEvent.payload.context.issueId,
          prId: escalationEvent.payload.context.prId,
        },
      },
      metadata: {
        source: 'orchestrator',
        version: process.env.TAMMA_VERSION || '1.0.0',
        environment: process.env.NODE_ENV || 'development',
      },
    };

    // Persist event
    await this.eventStore.append(event);

    // Update knowledge base if applicable
    if (resolution.learning.knowledgeBase.articleCreated) {
      await this.createKnowledgeBaseArticle(escalationId, resolution);
    }

    // Resume workflow if applicable
    if (resolution.outcome.resumed) {
      await this.resumeWorkflow(escalationEvent.correlationId, resolution);
    }
  }
}
```

### Technical Specifications

**Performance Requirements:**

- Approval request capture: <200ms per request
- Approval response capture: <100ms per response
- Escalation trigger capture: <300ms per escalation
- Escalation resolution capture: <200ms per resolution

**Security Requirements:**

- Approval authentication: Multi-factor authentication for critical approvals
- Authorization checks: Role-based approval permissions
- Audit trail: Immutable approval records
- Data privacy: Sensitive approval data protected

**Compliance Requirements:**

- Approval attribution: Clear who approved what and when
- Segregation of duties: Prevent self-approval scenarios
- Retention policy: Approval records retained for compliance period
- Reporting: Compliance reports and audit trails

**Integration Requirements:**

- Multiple approval channels: CLI, API, webhooks, email, Slack
- Notification systems: Integration with existing notification infrastructure
- Workflow integration: Seamless approval integration with autonomous loops
- User management: Integration with authentication and authorization systems

### Dependencies

**Internal Dependencies:**

- Story 4.1: Event schema design (provides event structures)
- Story 4.2: Event store backend selection (provides storage)
- Story 3.3: Escalation workflow implementation (integration point)
- Story 2.3: Development plan generation with approval checkpoint (integration point)

**External Dependencies:**

- Notification service (email, Slack, webhook)
- Authentication and authorization service
- User management system
- Knowledge base system

### Risks and Mitigations

| Risk                  | Severity | Mitigation                                   |
| --------------------- | -------- | -------------------------------------------- |
| Approval bottlenecks  | High     | Multiple approval channels, timeout handling |
| Escalation overload   | Medium   | Escalation throttling, priority routing      |
| Approval fraud        | High     | Multi-factor auth, segregation of duties     |
| Notification failures | Medium   | Redundant channels, retry mechanisms         |

### Success Metrics

- [ ] Approval request success rate: >99.5%
- [ ] Approval response time: <30 minutes average
- [ ] Escalation resolution time: <2 hours average
- [ ] Approval audit completeness: 100%
- [ ] Notification delivery rate: >99%

## Related

- Related story: `docs/stories/4-1-event-schema-design.md`
- Related story: `docs/stories/4-2-event-store-backend-selection.md`
- Related story: `docs/stories/3-3-escalation-workflow-implementation.md`
- Related story: `docs/stories/2-3-development-plan-generation-with-approval-checkpoint.md`
- Technical specification: `docs/tech-spec-epic-4.md`

## References

- [Approval Workflow Patterns](https://patterns.dev/posts/approval-workflow)
- [Escalation Management Best Practices](https://itsm.tools/escalation-management)
- [Compliance Framework Requirements](https://www.coso.org/Pages/default.aspx)
- [Multi-factor Authentication Guidelines](https://nvlpubs.nist.gov/nistpubs/SpecialPublications/NIST.SP.800-63B.html)
