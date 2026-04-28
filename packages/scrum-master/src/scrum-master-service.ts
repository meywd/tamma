/**
 * Scrum Master Service
 *
 * Main implementation of the Scrum Master task loop with state machine.
 * Coordinates task assignment, planning, approval, implementation,
 * review, and learning capture.
 *
 * @module @tamma/scrum-master/scrum-master-service
 */

import { nanoid } from 'nanoid';
import type { IKnowledgeService } from '@tamma/intelligence';
import type { IPermissionService } from '@tamma/gates';
import type { ICostTracker } from '@tamma/cost-monitor';
import type { KnowledgeCheckResult } from '@tamma/shared';
import type {
  Task,
  TaskResult,
  TaskLoopContext,
  ScrumMasterPlan,
  ImplementationResult,
  ReviewResult,
  LearningCapture,
  Blocker,
  ApprovalDecision,
  IScrumMaster,
  IUserInterface,
  IEnginePool,
  ScrumMasterEvent,
  RiskAssessment,
} from './types.js';
import { ScrumMasterState, ScrumMasterEventType } from './types.js';
import type { ScrumMasterConfig } from './config.js';
import { DEFAULT_SCRUM_MASTER_CONFIG, mergeConfig } from './config.js';
import { TaskSupervisor } from './services/task-supervisor.js';
import { ApprovalWorkflow } from './services/approval-workflow.js';
import { LearningCaptureService } from './services/learning-capture.js';
import { AlertManager, AlertSender } from './services/alert-manager.js';
import { AgentCoordinator } from './coordinators/agent-coordinator.js';
import {
  InvalidStateTransitionError,
  TaskBlockedError,
  ApprovalDeniedError,
  MaxRetriesExceededError,
  TaskTimeoutError,
  CostLimitExceededError,
  ImplementationFailedError,
  ReviewFailedError,
  TaskCancelledError,
  EscalationRequiredError,
} from './errors.js';

/** Maximum number of events to retain in memory */
const MAX_EVENTS = 10_000;

/** Maximum number of event listeners allowed */
const MAX_EVENT_LISTENERS = 100;

export class ScrumMasterService implements IScrumMaster {
  private state: ScrumMasterState = ScrumMasterState.IDLE;
  private context: TaskLoopContext | undefined;
  private config: ScrumMasterConfig;
  private paused = false;
  private cancelled = false;

  // Services
  private supervisor: TaskSupervisor;
  private approvalWorkflow: ApprovalWorkflow;
  private learningCapture: LearningCaptureService;
  private alertManager: AlertManager;
  private coordinator: AgentCoordinator;

  // External dependencies (optional)
  private knowledgeService: IKnowledgeService | null = null;
  private permissionService: IPermissionService | null = null;
  private costTracker: ICostTracker | null = null;
  private userInterface: IUserInterface | null = null;
  private enginePool: IEnginePool | null = null;

  // Event history
  private events: ScrumMasterEvent[] = [];
  private eventListeners: Array<(event: ScrumMasterEvent) => void> = [];

  constructor(config?: Partial<ScrumMasterConfig>) {
    this.config = mergeConfig(config);

    // Initialize services
    this.supervisor = new TaskSupervisor(this.config);
    this.approvalWorkflow = new ApprovalWorkflow(this.config);
    this.learningCapture = new LearningCaptureService(this.config.learningCapture);
    this.alertManager = new AlertManager(this.config.alerts);
    this.coordinator = new AgentCoordinator(undefined, this.config.cost);
  }

  // ============================================
  // Dependency Injection
  // ============================================

  setKnowledgeService(service: IKnowledgeService): void {
    this.knowledgeService = service;
    this.learningCapture.setKnowledgeService(service);
  }

  setPermissionService(service: IPermissionService): void {
    this.permissionService = service;
    this.approvalWorkflow.setPermissionService(service);
  }

  setCostTracker(tracker: ICostTracker): void {
    this.costTracker = tracker;
    this.coordinator.setCostTracker(tracker);
  }

  setUserInterface(ui: IUserInterface): void {
    this.userInterface = ui;
    this.approvalWorkflow.setUserInterface(ui);
  }

  setEnginePool(pool: IEnginePool): void {
    this.enginePool = pool;
    this.coordinator.setEnginePool(pool);
  }

  // ============================================
  // IScrumMaster Implementation
  // ============================================

  /**
   * Begin task supervision
   */
  async startSession(task: Task): Promise<TaskResult> {
    // Initialize context
    this.context = {
      task,
      learnings: [],
      blockers: [],
      retryCount: 0,
      maxRetries: this.config.taskLoop.maxRetries,
      startTime: new Date(),
      errors: [],
      costBudgetUsd: this.config.cost.defaultTaskBudgetUsd,
      currentCostUsd: 0,
    };

    this.cancelled = false;
    this.paused = false;

    // Start supervisor
    this.supervisor.startMonitoring(this.context);

    // Record event
    this.recordEvent(ScrumMasterEventType.TASK_RECEIVED, { taskId: task.id });

    // Transition to planning
    await this.transitionTo(ScrumMasterState.PLANNING);

    // Run the task loop
    try {
      return await this.runTaskLoop();
    } finally {
      this.supervisor.stopMonitoring();
    }
  }

  /**
   * Track agent progress
   */
  monitorProgress(): TaskLoopContext {
    if (!this.context) {
      throw new Error('No active session');
    }
    return { ...this.context };
  }

  /**
   * Respond to agent blockers
   */
  async handleBlocker(blocker: Blocker): Promise<void> {
    if (!this.context) {
      throw new Error('No active session');
    }

    // Add blocker to context
    this.context.blockers.push(blocker);

    // Record in supervisor
    this.supervisor.addBlocker(blocker.type, blocker.message, blocker.taskId);

    // Record event
    this.recordEvent(ScrumMasterEventType.BLOCKER_DETECTED, {
      blockerId: blocker.id,
      type: blocker.type,
      message: blocker.message,
    });

    // Send alert
    const alertSender = this.alertManager.createAlertSender(this.context.task.id);
    await alertSender.taskBlocked(blocker.message);

    // Transition to blocked state
    if (this.state !== ScrumMasterState.BLOCKED) {
      await this.transitionTo(ScrumMasterState.BLOCKED);
    }
  }

  /**
   * Review agent work
   */
  async reviewOutput(output: ImplementationResult): Promise<ReviewResult> {
    // Perform quality checks
    const qualityChecks = await this.runQualityChecks(output);

    // Calculate score
    const passedChecks = qualityChecks.filter((c) => c.passed).length;
    const score = Math.round((passedChecks / qualityChecks.length) * 100);

    // Gather issues
    const issues = qualityChecks
      .filter((c) => !c.passed)
      .map((c) => ({
        severity: 'error' as const,
        category: 'code' as const,
        message: c.message,
      }));

    const result: ReviewResult = {
      passed: score >= 70 && issues.filter((i) => i.severity === 'error').length === 0,
      score,
      issues,
      suggestions: [],
      qualityChecks,
    };

    return result;
  }

  /**
   * Approval decisions
   */
  async approveOrReject(decision: ApprovalDecision, reason?: string): Promise<void> {
    if (!this.context || this.state !== ScrumMasterState.AWAITING_APPROVAL) {
      throw new Error(`Cannot approve/reject in state ${this.state}`);
    }

    if (decision === 'approved') {
      this.context.approvalStatus = {
        status: 'approved',
        approvedBy: 'user',
        approvedAt: new Date(),
        reason,
      };
      this.recordEvent(ScrumMasterEventType.APPROVAL_GRANTED, { reason });
      await this.transitionTo(ScrumMasterState.IMPLEMENTING);
    } else if (decision === 'rejected') {
      this.context.approvalStatus = {
        status: 'rejected',
        reason,
      };
      this.recordEvent(ScrumMasterEventType.APPROVAL_DENIED, { reason });
      throw new ApprovalDeniedError(this.context.task.id, reason ?? 'No reason provided');
    } else if (decision === 'needs_revision') {
      // Go back to planning with adjustments
      await this.transitionTo(ScrumMasterState.PLANNING);
    }
  }

  /**
   * Record learnings
   */
  async captureLearning(outcome: LearningCapture): Promise<void> {
    if (!this.context) {
      throw new Error('No active session');
    }

    this.context.learnings.push(outcome);
    await this.learningCapture.submitLearning(outcome);

    this.recordEvent(ScrumMasterEventType.LEARNING_CAPTURED, {
      learningType: outcome.type,
      title: outcome.title,
    });
  }

  /**
   * Get current state
   */
  getState(): ScrumMasterState {
    return this.state;
  }

  /**
   * Get current context
   */
  getContext(): TaskLoopContext | undefined {
    return this.context ? { ...this.context } : undefined;
  }

  /**
   * Pause execution
   */
  async pause(): Promise<void> {
    this.paused = true;
    this.notifyUser('Task execution paused');
  }

  /**
   * Resume execution
   */
  async resume(): Promise<void> {
    this.paused = false;
    this.notifyUser('Task execution resumed');
  }

  /**
   * Cancel task
   */
  async cancel(reason: string): Promise<void> {
    this.cancelled = true;
    this.recordEvent(ScrumMasterEventType.TASK_CANCELLED, { reason });
    await this.transitionTo(ScrumMasterState.CANCELLED);
    throw new TaskCancelledError(this.context?.task.id ?? 'unknown', reason);
  }

  // ============================================
  // Event Handling
  // ============================================

  addEventListener(listener: (event: ScrumMasterEvent) => void): void {
    if (this.eventListeners.length >= MAX_EVENT_LISTENERS) {
      throw new Error(
        `Maximum number of event listeners (${MAX_EVENT_LISTENERS}) reached. Remove unused listeners before adding new ones.`
      );
    }
    this.eventListeners.push(listener);
  }

  removeEventListener(listener: (event: ScrumMasterEvent) => void): void {
    const index = this.eventListeners.indexOf(listener);
    if (index !== -1) {
      this.eventListeners.splice(index, 1);
    }
  }

  getEvents(taskId?: string): ScrumMasterEvent[] {
    if (taskId) {
      return this.events.filter((e) => e.taskId === taskId);
    }
    return [...this.events];
  }

  // ============================================
  // Main Task Loop
  // ============================================

  private async runTaskLoop(): Promise<TaskResult> {
    while (true) {
      // Check for cancellation
      if (this.cancelled) {
        return this.buildTaskResult(false, 'Task cancelled');
      }

      // Check for pause
      while (this.paused) {
        await this.sleep(1000);
        if (this.cancelled) {
          return this.buildTaskResult(false, 'Task cancelled while paused');
        }
      }

      // Check for timeout
      const timeoutStatus = this.supervisor.getTimeoutStatus();
      if (timeoutStatus.timedOut) {
        await this.transitionTo(ScrumMasterState.FAILED);
        return this.buildTaskResult(false, 'Task timed out');
      }

      // Check for escalation
      if (this.supervisor.shouldEscalate()) {
        const reason = this.supervisor.getEscalationReason();
        await this.transitionTo(ScrumMasterState.ESCALATED);
        return this.buildTaskResult(false, `Escalated: ${reason ?? 'unknown'}`);
      }

      // Execute current state
      try {
        const result = await this.executeState();
        if (result) {
          return result;
        }
      } catch (error) {
        const handled = await this.handleError(error);
        if (!handled) {
          await this.transitionTo(ScrumMasterState.FAILED);
          return this.buildTaskResult(
            false,
            error instanceof Error ? error.message : 'Unknown error'
          );
        }
      }
    }
  }

  private async executeState(): Promise<TaskResult | null> {
    this.supervisor.recordActivity();

    switch (this.state) {
      case ScrumMasterState.PLANNING:
        await this.executePlanningState();
        return null;

      case ScrumMasterState.AWAITING_APPROVAL:
        await this.executeApprovalState();
        return null;

      case ScrumMasterState.IMPLEMENTING:
        await this.executeImplementingState();
        return null;

      case ScrumMasterState.REVIEWING:
        await this.executeReviewingState();
        return null;

      case ScrumMasterState.LEARNING:
        await this.executeLearningState();
        return null;

      case ScrumMasterState.COMPLETED:
        return this.buildTaskResult(true);

      case ScrumMasterState.BLOCKED:
        await this.executeBlockedState();
        return null;

      case ScrumMasterState.ESCALATED:
        return this.buildTaskResult(false, 'Task escalated');

      case ScrumMasterState.CANCELLED:
        return this.buildTaskResult(false, 'Task cancelled');

      case ScrumMasterState.FAILED:
        return this.buildTaskResult(false, 'Task failed');

      default:
        throw new Error(`Unknown state: ${this.state}`);
    }
  }

  // ============================================
  // State Handlers
  // ============================================

  private async executePlanningState(): Promise<void> {
    this.recordEvent(ScrumMasterEventType.PLAN_STARTED, {});
    this.notifyUser('Generating development plan...');

    // Generate plan (mock for now)
    const plan = await this.generatePlan();
    this.context!.plan = plan;

    // Check knowledge base
    if (this.knowledgeService) {
      const knowledgeCheck = await this.knowledgeService.checkBeforeTask(
        {
          taskId: this.context!.task.id,
          type: this.context!.task.type,
          description: this.context!.task.description,
          projectId: this.context!.task.projectId,
          agentType: 'implementer',
        },
        {
          summary: plan.summary,
          approach: plan.approach,
          fileChanges: plan.fileChanges.map((f) => ({
            path: f.path,
            action: f.action,
            description: f.description,
          })),
        }
      );
      this.context!.knowledgeCheck = knowledgeCheck;

      this.recordEvent(ScrumMasterEventType.KNOWLEDGE_CHECKED, {
        canProceed: knowledgeCheck.canProceed,
        blockerCount: knowledgeCheck.blockers.length,
        warningCount: knowledgeCheck.warnings.length,
      });

      // Check for blockers
      if (!knowledgeCheck.canProceed) {
        const blocker = this.supervisor.addBlocker(
          'knowledge_prohibition',
          `Blocked by knowledge base: ${knowledgeCheck.blockers.map((b) => b.matchReason).join(', ')}`,
          this.context!.task.id
        );
        this.context!.blockers.push(blocker);
        await this.transitionTo(ScrumMasterState.BLOCKED);
        return;
      }
    }

    // Assess risk
    const riskAssessment = this.approvalWorkflow.assessRisk(plan);
    this.context!.riskAssessment = riskAssessment;

    this.recordEvent(ScrumMasterEventType.RISK_ASSESSED, {
      level: riskAssessment.level,
      score: riskAssessment.score,
      requiresApproval: riskAssessment.requiresApproval,
    });

    this.recordEvent(ScrumMasterEventType.PLAN_GENERATED, {
      summary: plan.summary,
      fileCount: plan.fileChanges.length,
      complexity: plan.complexity,
    });

    // Transition to approval
    await this.transitionTo(ScrumMasterState.AWAITING_APPROVAL);
  }

  private async executeApprovalState(): Promise<void> {
    const plan = this.context!.plan!;
    const riskAssessment = this.context!.riskAssessment!;
    const knowledgeCheck = this.context!.knowledgeCheck ?? {
      canProceed: true,
      recommendations: [],
      warnings: [],
      blockers: [],
      learnings: [],
    };

    // Try auto-approve
    const autoApproval = this.approvalWorkflow.tryAutoApprove(plan, riskAssessment);
    if (autoApproval) {
      this.context!.approvalStatus = autoApproval;
      this.recordEvent(ScrumMasterEventType.APPROVAL_AUTO, {
        reason: autoApproval.reason,
      });
      this.notifyUser(`Plan auto-approved: ${autoApproval.reason}`);
      await this.transitionTo(ScrumMasterState.IMPLEMENTING);
      return;
    }

    // Request approval
    this.recordEvent(ScrumMasterEventType.APPROVAL_REQUESTED, {
      riskLevel: riskAssessment.level,
    });

    // Send alert
    const alertSender = this.alertManager.createAlertSender(this.context!.task.id);
    await alertSender.approvalNeeded(plan.summary);

    // Wait for approval (if user interface available)
    if (this.userInterface) {
      const response = await this.userInterface.requestApproval(
        plan,
        riskAssessment.level,
        knowledgeCheck
      );

      if (response.approved) {
        this.context!.approvalStatus = {
          status: 'approved',
          approvedBy: 'user',
          approvedAt: new Date(),
          adjustments: response.adjustments,
        };

        // Apply adjustments if any
        if (response.adjustments && response.adjustments.length > 0) {
          this.context!.plan = this.approvalWorkflow.applyAdjustments(
            plan,
            response.adjustments
          );
        }

        this.recordEvent(ScrumMasterEventType.APPROVAL_GRANTED, {});
        await this.transitionTo(ScrumMasterState.IMPLEMENTING);
      } else {
        this.context!.approvalStatus = {
          status: 'rejected',
          reason: response.reason,
        };
        this.recordEvent(ScrumMasterEventType.APPROVAL_DENIED, {
          reason: response.reason,
        });
        throw new ApprovalDeniedError(
          this.context!.task.id,
          response.reason ?? 'No reason provided'
        );
      }
    } else {
      // No user interface - auto-approve for medium risk, block high risk
      if (riskAssessment.level !== 'high') {
        this.context!.approvalStatus = {
          status: 'auto_approved',
          approvedAt: new Date(),
          reason: 'Auto-approved (no user interface)',
        };
        this.recordEvent(ScrumMasterEventType.APPROVAL_AUTO, {});
        await this.transitionTo(ScrumMasterState.IMPLEMENTING);
      } else {
        throw new ApprovalDeniedError(
          this.context!.task.id,
          'High risk task requires manual approval'
        );
      }
    }
  }

  private async executeImplementingState(): Promise<void> {
    this.recordEvent(ScrumMasterEventType.IMPLEMENTATION_STARTED, {});
    this.notifyUser('Starting implementation...');

    // Check cost budget before starting
    const costCheck = await this.coordinator.checkCostBudget(
      this.context!.task.projectId,
      this.context!.plan!.estimatedCostUsd
    );

    if (!costCheck.allowed) {
      const blocker = this.supervisor.addBlocker(
        'cost_limit_exceeded',
        `Cost limit would be exceeded: ${costCheck.warnings.join(', ')}`,
        this.context!.task.id
      );
      this.context!.blockers.push(blocker);
      await this.transitionTo(ScrumMasterState.BLOCKED);
      return;
    }

    // Acquire engine
    let engine;
    try {
      engine = await this.coordinator.acquireEngine(this.context!.task.projectId);
    } catch (error) {
      const blocker = this.supervisor.addBlocker(
        'external_dependency',
        'No engine available for implementation',
        this.context!.task.id
      );
      this.context!.blockers.push(blocker);
      await this.transitionTo(ScrumMasterState.BLOCKED);
      return;
    }

    try {
      // Build implementation prompt
      const prompt = this.buildImplementationPrompt();

      // Execute
      const result = await engine.execute(
        prompt,
        {
          maxCostUsd: this.context!.costBudgetUsd - this.context!.currentCostUsd,
          timeoutMs: this.supervisor.getTimeoutStatus().remainingMs,
        },
        (event) => {
          this.recordEvent(ScrumMasterEventType.IMPLEMENTATION_PROGRESS, {
            message: event.message,
          });
          this.notifyUser(event.message);
        }
      );

      this.context!.implementation = result;
      this.context!.currentCostUsd += result.costUsd;

      // Record cost usage
      await this.coordinator.recordCostUsage({
        taskId: this.context!.task.id,
        projectId: this.context!.task.projectId,
        inputTokens: 0, // Engine should provide this
        outputTokens: 0,
      });

      if (result.success) {
        this.recordEvent(ScrumMasterEventType.IMPLEMENTATION_COMPLETED, {
          filesModified: result.filesModified.length,
          costUsd: result.costUsd,
        });
        await this.transitionTo(ScrumMasterState.REVIEWING);
      } else {
        this.recordEvent(ScrumMasterEventType.IMPLEMENTATION_FAILED, {
          error: result.error,
        });
        this.supervisor.recordFailure();

        if (this.supervisor.shouldRetry()) {
          this.context!.retryCount++;
          this.notifyUser(
            `Implementation failed, retrying (${this.context!.retryCount}/${this.context!.maxRetries})`
          );
          await this.transitionTo(ScrumMasterState.PLANNING);
        } else {
          throw new MaxRetriesExceededError(
            this.context!.task.id,
            this.context!.retryCount,
            this.context!.maxRetries
          );
        }
      }
    } finally {
      await this.coordinator.releaseEngine(engine);
    }
  }

  private async executeReviewingState(): Promise<void> {
    this.recordEvent(ScrumMasterEventType.REVIEW_STARTED, {});
    this.notifyUser('Reviewing implementation...');

    const review = await this.reviewOutput(this.context!.implementation!);
    this.context!.review = review;

    if (review.passed) {
      this.recordEvent(ScrumMasterEventType.REVIEW_PASSED, {
        score: review.score,
      });
      this.supervisor.resetFailures();
      await this.transitionTo(ScrumMasterState.LEARNING);
    } else {
      this.recordEvent(ScrumMasterEventType.REVIEW_FAILED, {
        score: review.score,
        issueCount: review.issues.length,
      });
      this.supervisor.recordFailure();

      // Send alert
      const alertSender = this.alertManager.createAlertSender(this.context!.task.id);
      await alertSender.reviewFailed(
        review.score,
        review.issues.map((i) => i.message)
      );

      if (this.supervisor.shouldRetry()) {
        this.context!.retryCount++;
        this.notifyUser(
          `Review failed, retrying (${this.context!.retryCount}/${this.context!.maxRetries})`
        );
        await this.transitionTo(ScrumMasterState.PLANNING);
      } else {
        throw new MaxRetriesExceededError(
          this.context!.task.id,
          this.context!.retryCount,
          this.context!.maxRetries
        );
      }
    }
  }

  private async executeLearningState(): Promise<void> {
    this.notifyUser('Capturing learnings...');

    // Extract learnings
    const learnings = await this.learningCapture.extractLearnings(this.context!);

    for (const learning of learnings) {
      this.context!.learnings.push(learning);
      this.recordEvent(ScrumMasterEventType.LEARNING_CAPTURED, {
        type: learning.type,
        title: learning.title,
      });
    }

    // Detect patterns
    const successPatterns = await this.learningCapture.detectSuccessPatterns(
      this.context!
    );
    const failurePatterns = await this.learningCapture.detectFailurePatterns(
      this.context!
    );

    if (successPatterns.length > 0) {
      this.notifyUser(`Success patterns: ${successPatterns.join(', ')}`);
    }
    if (failurePatterns.length > 0) {
      this.notifyUser(`Failure patterns: ${failurePatterns.join(', ')}`);
    }

    this.notifyUser(`Captured ${learnings.length} learnings`);

    await this.transitionTo(ScrumMasterState.COMPLETED);
  }

  private async executeBlockedState(): Promise<void> {
    const unresolvedBlockers = this.supervisor.getUnresolvedBlockers();

    // Check if all blockers are resolved
    if (unresolvedBlockers.length === 0) {
      // Resume from previous state
      // For now, go back to planning
      await this.transitionTo(ScrumMasterState.PLANNING);
      return;
    }

    // Check for escalation
    if (this.supervisor.shouldEscalate()) {
      const reason = this.supervisor.getEscalationReason();
      await this.transitionTo(ScrumMasterState.ESCALATED);
      throw new EscalationRequiredError(this.context!.task.id, reason ?? 'Unknown');
    }

    // Wait for resolution
    await this.sleep(5000);
  }

  // ============================================
  // Helper Methods
  // ============================================

  private async generatePlan(): Promise<ScrumMasterPlan> {
    // Mock plan generation
    // In production, this would use an LLM to generate the plan
    return {
      taskId: this.context!.task.id,
      summary: `Implementation plan for: ${this.context!.task.title}`,
      approach: 'Standard implementation approach',
      fileChanges: [
        {
          path: 'src/implementation.ts',
          action: 'modify',
          description: 'Main implementation changes',
          estimatedLines: 50,
        },
      ],
      testingStrategy: 'Unit tests for all changes',
      complexity: 'medium',
      estimatedTokens: 5000,
      estimatedCostUsd: 0.1,
      risks: [],
      dependencies: [],
      generatedAt: new Date(),
      version: 1,
    };
  }

  private buildImplementationPrompt(): string {
    const plan = this.context!.plan!;
    const task = this.context!.task;

    return `
Task: ${task.title}
Description: ${task.description}

Plan Summary: ${plan.summary}
Approach: ${plan.approach}

Files to modify:
${plan.fileChanges.map((f) => `- ${f.path} (${f.action}): ${f.description}`).join('\n')}

Testing Strategy: ${plan.testingStrategy}
`.trim();
  }

  private async runQualityChecks(
    output: ImplementationResult
  ): Promise<Array<{ name: string; passed: boolean; message: string }>> {
    const checks = [];

    // Check: Implementation success
    checks.push({
      name: 'implementation_success',
      passed: output.success,
      message: output.success ? 'Implementation completed' : `Implementation failed: ${output.error}`,
    });

    // Check: Tests run
    if (output.testsRun > 0) {
      const testsPassed = output.testsPassed === output.testsRun;
      checks.push({
        name: 'tests_passing',
        passed: testsPassed,
        message: testsPassed
          ? `All ${output.testsRun} tests passing`
          : `${output.testsRun - output.testsPassed}/${output.testsRun} tests failed`,
      });
    }

    // Check: Cost within budget
    const costOk = output.costUsd <= this.context!.costBudgetUsd;
    checks.push({
      name: 'cost_budget',
      passed: costOk,
      message: costOk
        ? `Cost $${output.costUsd.toFixed(4)} within budget`
        : `Cost $${output.costUsd.toFixed(4)} exceeds budget`,
    });

    // Check: Files modified
    const filesOk = output.filesModified.length > 0 || output.success;
    checks.push({
      name: 'files_modified',
      passed: filesOk,
      message: filesOk
        ? `${output.filesModified.length} files modified`
        : 'No files modified',
    });

    return checks;
  }

  private async transitionTo(newState: ScrumMasterState): Promise<void> {
    const analysis = this.supervisor.analyzeStateTransition(this.state, newState);

    if (!analysis.valid) {
      throw new InvalidStateTransitionError(this.state, newState, analysis.warning);
    }

    const oldState = this.state;
    this.state = newState;

    this.recordEvent(ScrumMasterEventType.STATE_TRANSITION, {
      from: oldState,
      to: newState,
      warning: analysis.warning,
    });
  }

  private async handleError(error: unknown): Promise<boolean> {
    const message = error instanceof Error ? error.message : 'Unknown error';

    this.recordEvent(ScrumMasterEventType.ERROR_OCCURRED, {
      message,
      recoverable: error instanceof TaskBlockedError,
    });

    this.context!.errors.push({
      state: this.state,
      message,
      timestamp: new Date(),
      recoverable: error instanceof TaskBlockedError,
    });

    // Handle specific errors
    if (error instanceof TaskBlockedError) {
      await this.transitionTo(ScrumMasterState.BLOCKED);
      return true;
    }

    if (error instanceof ApprovalDeniedError) {
      await this.transitionTo(ScrumMasterState.CANCELLED);
      throw error;
    }

    if (error instanceof MaxRetriesExceededError) {
      const alertSender = this.alertManager.createAlertSender(this.context!.task.id);
      await alertSender.maxRetriesExceeded(
        this.context!.retryCount,
        this.context!.maxRetries
      );
      return false;
    }

    if (error instanceof TaskCancelledError) {
      return false;
    }

    if (error instanceof EscalationRequiredError) {
      const alertSender = this.alertManager.createAlertSender(this.context!.task.id);
      await alertSender.escalation(message);
      return false;
    }

    // Unknown error - send alert
    const alertSender = this.alertManager.createAlertSender(this.context!.task.id);
    await alertSender.error(message);

    return false;
  }

  private buildTaskResult(success: boolean, error?: string): TaskResult {
    return {
      taskId: this.context!.task.id,
      success,
      state: this.state,
      plan: this.context!.plan,
      implementation: this.context!.implementation,
      review: this.context!.review,
      learnings: this.context!.learnings,
      totalCostUsd: this.context!.currentCostUsd,
      totalDurationMs: Date.now() - this.context!.startTime.getTime(),
      retryCount: this.context!.retryCount,
      error,
      completedAt: new Date(),
    };
  }

  private recordEvent(
    type: ScrumMasterEventType,
    data: Record<string, unknown>
  ): void {
    const event: ScrumMasterEvent = {
      id: nanoid(),
      type,
      timestamp: new Date(),
      taskId: this.context?.task.id,
      state: this.state,
      data,
    };

    this.events.push(event);

    // Enforce maximum events limit by removing oldest entries in a single splice call.
    if (this.events.length > MAX_EVENTS) {
      this.events.splice(0, this.events.length - MAX_EVENTS);
    }

    // Notify listeners
    for (const listener of this.eventListeners) {
      try {
        listener(event);
      } catch (error) {
        console.warn('[ScrumMasterService] Event listener error:', error instanceof Error ? error.message : String(error));
      }
    }
  }

  private notifyUser(message: string, data?: Record<string, unknown>): void {
    if (this.userInterface) {
      this.userInterface.notifyUser(message, data);
    }
  }

  private async sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }
}

/**
 * Create a Scrum Master service with optional config
 */
export function createScrumMasterService(
  config?: Partial<ScrumMasterConfig>
): ScrumMasterService {
  return new ScrumMasterService(config);
}
