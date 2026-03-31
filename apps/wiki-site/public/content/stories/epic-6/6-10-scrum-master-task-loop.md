---
title: "Story 6-10: Scrum Master Task Loop"
sidebar:
  order: 60
---

## User Story

As a **Scrum Master agent**, I need a structured task execution loop so that I can properly coordinate task assignment, planning, approval, implementation, review, and learning capture while handling user interactions.

## Description

Implement a mini state machine for the Scrum Master that governs how tasks are assigned, planned, approved, implemented, and reviewed. This loop ensures quality gates are passed, learnings are captured, and the user is kept informed throughout the process.

## Acceptance Criteria

### AC1: Task Loop States
- [ ] PLAN: Generate or receive development plan
- [ ] APPROVE: Validate plan against knowledge base, request approval if needed
- [ ] IMPLEMENT: Assign to engine, monitor progress
- [ ] REVIEW: Validate implementation, check quality
- [ ] LEARN: Capture learnings from outcome
- [ ] ALERT: Notify of issues or approval requests
- [ ] REPEAT: Loop back if issues found

### AC2: Pre-Task Validation
- [ ] Check knowledge base for relevant prohibitions
- [ ] Check knowledge base for recommendations
- [ ] Validate against project conventions
- [ ] Estimate complexity and risk
- [ ] Block if critical prohibitions matched

### AC3: Approval Workflow
- [ ] Auto-approve low-risk tasks (configurable)
- [ ] Request user approval for medium/high risk
- [ ] Escalate blocked tasks to human
- [ ] Track approval status and history

### AC4: Implementation Monitoring
- [ ] Assign task to available engine
- [ ] Monitor engine progress
- [ ] Handle engine failures
- [ ] Track resource usage
- [ ] Timeout handling

### AC5: Review Phase
- [ ] Validate implementation matches plan
- [ ] Run quality checks (tests, lint, type check)
- [ ] Check for prohibited patterns
- [ ] Request human review if needed
- [ ] Iterate if issues found

### AC6: Learning Capture
- [ ] Capture learnings from successful tasks
- [ ] Capture learnings from failures
- [ ] Identify patterns for recommendations
- [ ] Update knowledge base

### AC7: User Interaction
- [ ] Status updates throughout loop
- [ ] Answer questions about progress
- [ ] Handle user commands (pause, cancel, modify)
- [ ] Alert on issues requiring attention

## Technical Design

### Scrum Master State Machine

```
┌─────────────────────────────────────────────────────────────────────────────────┐
│                     SCRUM MASTER TASK LOOP                                       │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                  │
│                           ┌──────────────┐                                      │
│              ┌────────────│   RECEIVE    │◄────────────┐                        │
│              │            │    TASK      │             │                        │
│              │            └──────┬───────┘             │                        │
│              │                   │                     │                        │
│              │                   ▼                     │                        │
│              │            ┌──────────────┐             │                        │
│              │            │     PLAN     │             │                        │
│              │            │              │             │                        │
│              │            │ • Analyze    │             │                        │
│              │            │ • Research   │             │                        │
│              │            │ • Generate   │             │                        │
│              │            │   plan       │             │                        │
│              │            └──────┬───────┘             │                        │
│              │                   │                     │                        │
│              │                   ▼                     │                        │
│              │            ┌──────────────┐             │                        │
│  user        │            │   APPROVE    │─────────────┤ blocked               │
│  cancel      │            │              │             │                        │
│              │            │ • Check KB   │             │                        │
│              │            │ • Validate   │             │                        │
│              │            │ • Get OK     │             │                        │
│              │            └──────┬───────┘             │                        │
│              │                   │ approved            │                        │
│              │                   ▼                     │                        │
│              │            ┌──────────────┐             │                        │
│              │            │  IMPLEMENT   │             │                        │
│              │            │              │             │                        │
│              │            │ • Assign to  │             │                        │
│              │            │   engine     │             │                        │
│              │            │ • Monitor    │             │                        │
│              │            │ • Track cost │             │                        │
│              │            └──────┬───────┘             │                        │
│              │                   │                     │                        │
│              │          ┌────────┴────────┐            │                        │
│              │          │                 │            │                        │
│              │          ▼                 ▼            │                        │
│              │   ┌──────────┐      ┌──────────┐       │                        │
│              │   │ SUCCESS  │      │  FAILED  │       │                        │
│              │   └────┬─────┘      └────┬─────┘       │                        │
│              │        │                 │             │                        │
│              │        ▼                 ▼             │                        │
│              │   ┌──────────────────────────────┐     │                        │
│              │   │          REVIEW              │     │                        │
│              │   │                              │     │                        │
│              │   │ • Validate implementation   │     │                        │
│              │   │ • Run quality checks        │     │                        │
│              │   │ • Check prohibited patterns │     │                        │
│              │   └──────────────┬───────────────┘     │                        │
│              │                  │                     │                        │
│              │         ┌────────┴────────┐            │                        │
│              │         │                 │            │                        │
│              │         ▼                 ▼            │                        │
│              │  ┌───────────┐     ┌───────────┐      │                        │
│              │  │  PASSED   │     │  FAILED   │──────┼──► retry?              │
│              │  └─────┬─────┘     └───────────┘      │     │                  │
│              │        │                              │     │                  │
│              │        ▼                              │     │                  │
│              │  ┌──────────────┐                     │     │  yes             │
│              │  │    LEARN     │                     │     │  (max 3)         │
│              │  │              │                     │     │                  │
│              │  │ • Capture    │                     │     ▼                  │
│              │  │   learnings  │                     │  ┌───────────┐        │
│              │  │ • Update KB  │                     │  │  ADJUST   │        │
│              │  │ • Report     │                     │  │   PLAN    │────────┘│
│              │  └──────┬───────┘                     │  └───────────┘         │
│              │         │                             │                        │
│              │         ▼                             │  no (max retries)      │
│              │  ┌──────────────┐                     │     │                  │
│              │  │   COMPLETE   │                     │     ▼                  │
│              │  │              │                     │  ┌───────────┐        │
│              │  │ • Close task │                     └──│   ALERT   │        │
│              │  │ • Update     │                        │           │        │
│              │  │   status     │                        │ • Notify  │        │
│              │  └──────────────┘                        │   human   │        │
│              │                                          │ • Escalate│        │
│              └──────────────────────────────────────────└───────────┘        │
│                                                                                  │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### Scrum Master Task Loop Implementation

```typescript
enum ScrumMasterState {
  RECEIVE_TASK = 'RECEIVE_TASK',
  PLAN = 'PLAN',
  APPROVE = 'APPROVE',
  IMPLEMENT = 'IMPLEMENT',
  REVIEW = 'REVIEW',
  LEARN = 'LEARN',
  ALERT = 'ALERT',
  ADJUST_PLAN = 'ADJUST_PLAN',
  COMPLETE = 'COMPLETE',
}

interface TaskLoopContext {
  task: Task;
  plan?: DevelopmentPlan;
  knowledgeCheck?: KnowledgeCheckResult;
  implementation?: ImplementationResult;
  review?: ReviewResult;
  learnings?: LearningCapture[];
  retryCount: number;
  maxRetries: number;
}

class ScrumMasterTaskLoop {
  private state: ScrumMasterState = ScrumMasterState.RECEIVE_TASK;
  private context: TaskLoopContext;

  private knowledgeService: IKnowledgeService;
  private enginePool: IEnginePool;
  private llmProvider: ILLMProvider;
  private alertManager: IAlertManager;
  private userInterface: IUserInterface;

  async executeLoop(task: Task): Promise<TaskResult> {
    this.context = {
      task,
      retryCount: 0,
      maxRetries: 3,
    };

    while (true) {
      this.notifyUser(`State: ${this.state}`);

      switch (this.state) {
        case ScrumMasterState.RECEIVE_TASK:
          await this.handleReceiveTask();
          break;

        case ScrumMasterState.PLAN:
          await this.handlePlan();
          break;

        case ScrumMasterState.APPROVE:
          const approveResult = await this.handleApprove();
          if (approveResult === 'blocked') {
            return { success: false, reason: 'Blocked by knowledge base' };
          }
          break;

        case ScrumMasterState.IMPLEMENT:
          await this.handleImplement();
          break;

        case ScrumMasterState.REVIEW:
          await this.handleReview();
          break;

        case ScrumMasterState.LEARN:
          await this.handleLearn();
          break;

        case ScrumMasterState.ALERT:
          const alertResult = await this.handleAlert();
          if (alertResult === 'cancel') {
            return { success: false, reason: 'Cancelled by user' };
          }
          break;

        case ScrumMasterState.ADJUST_PLAN:
          await this.handleAdjustPlan();
          break;

        case ScrumMasterState.COMPLETE:
          return { success: true, result: this.context };
      }
    }
  }

  // --- State Handlers ---

  private async handleReceiveTask(): Promise<void> {
    // Log task receipt
    this.notifyUser(`Received task: ${this.context.task.title}`);

    // Transition to planning
    this.state = ScrumMasterState.PLAN;
  }

  private async handlePlan(): Promise<void> {
    // Research phase - gather context
    const context = await this.gatherContext(this.context.task);

    // Generate plan using LLM
    this.context.plan = await this.generatePlan(this.context.task, context);

    this.notifyUser(`Plan generated:\n${this.context.plan.summary}`);

    // Transition to approval
    this.state = ScrumMasterState.APPROVE;
  }

  private async handleApprove(): Promise<'approved' | 'blocked' | 'adjusted'> {
    // Check knowledge base
    this.context.knowledgeCheck = await this.checkKnowledgeBase(
      this.context.task,
      this.context.plan!
    );

    // Handle blockers
    if (this.context.knowledgeCheck.blockers.length > 0) {
      this.notifyUser('Task blocked by knowledge base:', this.context.knowledgeCheck.blockers);
      this.state = ScrumMasterState.ALERT;
      return 'blocked';
    }

    // Show warnings
    if (this.context.knowledgeCheck.warnings.length > 0) {
      this.notifyUser('Warnings:', this.context.knowledgeCheck.warnings);
    }

    // Show recommendations
    if (this.context.knowledgeCheck.recommendations.length > 0) {
      this.notifyUser('Recommendations:', this.context.knowledgeCheck.recommendations);
    }

    // Determine if approval needed
    const riskLevel = this.assessRisk(this.context.plan!);

    if (riskLevel === 'low' && this.config.autoApprovelow) {
      this.notifyUser('Auto-approved (low risk)');
      this.state = ScrumMasterState.IMPLEMENT;
      return 'approved';
    }

    // Request approval
    const approval = await this.requestApproval(this.context.plan!, riskLevel);

    if (approval.approved) {
      this.state = ScrumMasterState.IMPLEMENT;
      return 'approved';
    } else if (approval.adjustments) {
      this.context.plan = this.applyAdjustments(this.context.plan!, approval.adjustments);
      this.state = ScrumMasterState.APPROVE;  // Re-check adjusted plan
      return 'adjusted';
    } else {
      this.state = ScrumMasterState.ALERT;
      return 'blocked';
    }
  }

  private async handleImplement(): Promise<void> {
    // Acquire engine from pool
    const engine = await this.enginePool.acquire();

    try {
      // Assign task with knowledge context
      const prompt = this.buildImplementationPrompt(
        this.context.task,
        this.context.plan!,
        this.context.knowledgeCheck!
      );

      // Monitor implementation
      this.context.implementation = await engine.execute(prompt, {
        onProgress: (progress) => this.notifyUser(`Progress: ${progress.message}`),
      });

      // Transition based on result
      this.state = ScrumMasterState.REVIEW;

    } finally {
      await this.enginePool.release(engine);
    }
  }

  private async handleReview(): Promise<void> {
    // Run quality checks
    this.context.review = await this.runReview(this.context.implementation!);

    if (this.context.review.passed) {
      this.notifyUser('Review passed');
      this.state = ScrumMasterState.LEARN;
    } else {
      this.notifyUser('Review failed:', this.context.review.issues);

      if (this.context.retryCount < this.context.maxRetries) {
        this.context.retryCount++;
        this.state = ScrumMasterState.ADJUST_PLAN;
      } else {
        this.state = ScrumMasterState.ALERT;
      }
    }
  }

  private async handleLearn(): Promise<void> {
    // Capture learnings from this task
    const learnings = await this.captureLearnings(
      this.context.task,
      this.context.plan!,
      this.context.implementation!,
      this.context.review!
    );

    this.context.learnings = learnings;

    // Submit learnings for approval
    for (const learning of learnings) {
      await this.knowledgeService.captureLearning(learning);
    }

    this.notifyUser(`Captured ${learnings.length} learnings`);

    // Transition to complete
    this.state = ScrumMasterState.COMPLETE;
  }

  private async handleAlert(): Promise<'continue' | 'cancel'> {
    // Determine alert type
    const alertType = this.determineAlertType();

    // Send alert
    await this.alertManager.send({
      type: alertType,
      severity: 'warning',
      title: `Task requires attention: ${this.context.task.title}`,
      details: this.buildAlertDetails(),
      actions: ['continue', 'cancel', 'modify'],
    });

    // Wait for user response
    const response = await this.userInterface.waitForResponse();

    if (response === 'cancel') {
      return 'cancel';
    } else if (response === 'modify') {
      this.state = ScrumMasterState.ADJUST_PLAN;
      return 'continue';
    } else {
      // Force continue
      this.state = ScrumMasterState.IMPLEMENT;
      return 'continue';
    }
  }

  private async handleAdjustPlan(): Promise<void> {
    // Analyze what went wrong
    const analysis = await this.analyzeFailure(
      this.context.plan!,
      this.context.implementation,
      this.context.review
    );

    // Generate adjusted plan
    const adjustedPlan = await this.generateAdjustedPlan(
      this.context.task,
      this.context.plan!,
      analysis
    );

    this.context.plan = adjustedPlan;

    this.notifyUser(`Plan adjusted (attempt ${this.context.retryCount}/${this.context.maxRetries})`);

    // Go back to approval
    this.state = ScrumMasterState.APPROVE;
  }

  // --- Helper Methods ---

  private async checkKnowledgeBase(
    task: Task,
    plan: DevelopmentPlan
  ): Promise<KnowledgeCheckResult> {
    return this.knowledgeService.checkBeforeTask(task, plan);
  }

  private assessRisk(plan: DevelopmentPlan): 'low' | 'medium' | 'high' {
    // Risk factors
    let risk = 0;

    // File count
    if (plan.fileChanges.length > 10) risk += 2;
    else if (plan.fileChanges.length > 5) risk += 1;

    // Complexity
    if (plan.complexity === 'high') risk += 2;
    else if (plan.complexity === 'medium') risk += 1;

    // Has warnings
    if (this.context.knowledgeCheck?.warnings.length) risk += 1;

    // Modifies critical files
    if (plan.fileChanges.some(f => this.isCriticalFile(f.path))) risk += 2;

    return risk >= 4 ? 'high' : risk >= 2 ? 'medium' : 'low';
  }

  private async captureLearnings(
    task: Task,
    plan: DevelopmentPlan,
    implementation: ImplementationResult,
    review: ReviewResult
  ): Promise<LearningCapture[]> {
    const learnings: LearningCapture[] = [];

    // Capture from successful implementation
    if (implementation.success && review.passed) {
      // What worked well
      const successLearning = await this.llmProvider.analyze({
        prompt: `Analyze this successful task and identify reusable learnings:
          Task: ${task.title}
          Approach: ${plan.approach}
          Outcome: Successful implementation, all tests passing`,
        outputSchema: LearningCaptureSchema,
      });
      learnings.push(successLearning);
    }

    // Capture from failures/retries
    if (this.context.retryCount > 0 || !review.passed) {
      const failureLearning = await this.llmProvider.analyze({
        prompt: `Analyze what went wrong and how it was resolved:
          Task: ${task.title}
          Initial approach: ${plan.approach}
          Issues: ${review.issues?.join(', ')}
          Retries: ${this.context.retryCount}`,
        outputSchema: LearningCaptureSchema,
      });
      learnings.push(failureLearning);
    }

    return learnings;
  }
}
```

### User Interaction Handler

```typescript
class UserInteractionHandler {
  private scrumMaster: ScrumMasterTaskLoop;
  private llmProvider: ILLMProvider;
  private conversationHistory: Message[] = [];

  async handleUserMessage(message: string): Promise<string> {
    // Add to history
    this.conversationHistory.push({ role: 'user', content: message });

    // Determine intent
    const intent = await this.classifyIntent(message);

    let response: string;

    switch (intent) {
      case 'status_query':
        response = await this.handleStatusQuery();
        break;

      case 'task_command':
        response = await this.handleTaskCommand(message);
        break;

      case 'approval_response':
        response = await this.handleApprovalResponse(message);
        break;

      case 'question':
        response = await this.handleQuestion(message);
        break;

      case 'new_task':
        response = await this.handleNewTask(message);
        break;

      default:
        response = await this.handleGeneralChat(message);
    }

    // Add to history
    this.conversationHistory.push({ role: 'assistant', content: response });

    return response;
  }

  private async handleStatusQuery(): Promise<string> {
    const state = this.scrumMaster.getState();
    const context = this.scrumMaster.getContext();

    return `Current status: ${state}
Task: ${context.task?.title ?? 'None'}
Progress: ${context.retryCount}/${context.maxRetries} attempts
${context.plan ? `Plan: ${context.plan.summary}` : ''}`;
  }

  private async handleTaskCommand(message: string): Promise<string> {
    const command = this.parseCommand(message);

    switch (command.action) {
      case 'pause':
        await this.scrumMaster.pause();
        return 'Task paused. Use "resume" to continue.';

      case 'cancel':
        await this.scrumMaster.cancel();
        return 'Task cancelled.';

      case 'retry':
        await this.scrumMaster.retry();
        return 'Retrying task...';

      default:
        return `Unknown command: ${command.action}`;
    }
  }
}
```

## Configuration

```yaml
scrum_master:
  task_loop:
    max_retries: 3
    auto_approve_low_risk: true
    require_approval_high_risk: true

  risk_thresholds:
    low:
      max_files: 5
      max_complexity: low
    medium:
      max_files: 10
      max_complexity: medium

  learning_capture:
    capture_success: true
    capture_failure: true
    require_approval: true

  alerts:
    on_block: true
    on_max_retries: true
    on_approval_needed: true

  user_interaction:
    proactive_updates: true
    update_interval_seconds: 30
```

## Dependencies

- Story 6-7: Cost Monitoring (track implementation costs)
- Story 6-8: Agent Permissions (enforce during loop)
- Story 6-9: Agent Knowledge Base (pre-task checking)
- Engine pool management
- Alert Manager
- LLM Provider for planning

## Testing Strategy

### Unit Tests
- State transitions
- Risk assessment
- Learning capture
- User intent classification

### Integration Tests
- Full loop execution
- Approval workflow
- Alert handling
- Retry logic

### E2E Tests
- Complete task lifecycle
- User interaction scenarios
- Failure recovery

## Success Metrics

- Task completion rate > 85%
- Average retries < 1.5
- Learning capture rate > 90%
- User satisfaction with updates > 4/5

## Logging Requirements

All intelligence modules MUST accept `ILogger` from `@tamma/shared/contracts` via constructor injection.

- **INFO**: Index scan started/completed (file count, duration), vector search executed (query, result count), RAG pipeline invoked, knowledge base query matched
- **DEBUG**: Chunk boundaries, embedding dimensions, similarity scores, cache hit/miss, budget allocation
- **WARN**: Embedding API rate limited, vector store connection degraded, stale index detected, budget threshold approaching
- **ERROR**: Embedding generation failed, vector store unreachable, index corruption detected, MCP tool call failed
- **Structured context**: Always include `{ repository, indexId, queryId, sourceType }` where applicable
- **Performance**: Log duration for all external API calls (embedding, vector search, MCP tool invocations)
- **Cost tracking**: Log token counts for embedding operations to support LLM cost monitoring (Story 6-7)
