# Story 2.11: Auto Next Issue Selection

**Epic**: Epic 2 - Autonomous Development Loop - Core  
**Status**: Ready for Development  
**Priority**: High  
**Prerequisites**: Story 2.10 (PR merge with completion checkpoint must complete first)

---

## ⚠️ MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

📖 **[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)**

This mandatory guide includes:
- 7-Phase Development Workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling)
- Knowledge Base Usage (.dev/ directory: spikes, bugs, findings, decisions)
- TRACE/DEBUG Logging Requirements for all functions
- Test-Driven Development (TDD) mandatory workflow
- 100% Test Coverage requirement
- Build Success enforcement
- Automatic retry and developer alert procedures

**Failure to follow this process will result in rework.**

---

## User Story

As a **developer**,
I want the system to automatically select and start working on the next issue after completing the current one,
So that the autonomous development loop can continue without manual intervention.

---

## Acceptance Criteria

1. System automatically triggers next issue selection after successful PR merge
2. System validates current workflow completion before starting next issue
3. System respects configured delays and cooldown periods between issues
4. System handles no available issues gracefully (idle state)
5. System logs next issue selection and workflow state transitions
6. Integration test validates continuous autonomous operation
7. Configurable limits for maximum consecutive issues and daily quotas
8. Emergency stop capability and manual override options

---

## Technical Context

### ⚠️ Development Process Reminder

**Before implementing this story, ensure you have:**
1. ✅ Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. ✅ Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. ✅ Reviewed relevant documentation in `docs/` directory
4. ✅ Checked existing code patterns for similar functionality
5. ✅ Planned TDD approach (Red-Green-Refactor cycle)


### Core Components

**NextIssueSelector Service**:

```typescript
interface INextIssueSelector {
  triggerNextIssueSelection(context: WorkflowContext): Promise<NextIssueResult>;
  validateWorkflowCompletion(context: WorkflowContext): Promise<CompletionValidation>;
  selectNextIssue(criteria: IssueSelectionCriteria): Promise<SelectedIssue | null>;
  startNextIssueWorkflow(issue: SelectedIssue): Promise<WorkflowStartResult>;
  enterIdleState(reason: IdleReason): Promise<void>;
  checkOperationalLimits(): Promise<OperationalLimits>;
}

interface NextIssueResult {
  triggered: boolean;
  nextIssue?: SelectedIssue;
  workflowStarted: boolean;
  idleState: boolean;
  idleReason?: IdleReason;
  delayUntil?: Date;
  operationalLimits: OperationalLimits;
  transitionTime: number;
}

interface WorkflowContext {
  currentIssue?: SelectedIssue;
  previousIssues: CompletedIssue[];
  currentPR?: PullRequest;
  mergeResult?: MergeResult;
  completionResult?: CompletionResult;
  workflowStartTime: Date;
  lastActivityTime: Date;
  consecutiveIssues: number;
  dailyIssueCount: number;
  sessionMetrics: SessionMetrics;
}

interface CompletedIssue {
  issue: SelectedIssue;
  prNumber: number;
  mergeCommitSha: string;
  completedAt: Date;
  duration: number;
  success: boolean;
  errors: string[];
}

interface SessionMetrics {
  totalIssues: number;
  successfulIssues: number;
  failedIssues: number;
  averageIssueDuration: number;
  totalSessionTime: number;
  startTime: Date;
  lastResetTime: Date;
}

interface CompletionValidation {
  isComplete: boolean;
  completedSteps: WorkflowStep[];
  failedSteps: WorkflowStep[];
  pendingSteps: WorkflowStep[];
  canProceed: boolean;
  issues: ValidationIssue[];
}

interface WorkflowStep {
  name: string;
  status: 'completed' | 'failed' | 'pending' | 'skipped';
  completedAt?: Date;
  duration?: number;
  error?: string;
  metadata?: Record<string, any>;
}

interface ValidationIssue {
  type: 'step_failed' | 'incomplete_workflow' | 'data_inconsistency' | 'resource_unavailable';
  severity: 'blocking' | 'warning' | 'info';
  description: string;
  step?: string;
  autoResolvable: boolean;
}

interface IssueSelectionCriteria {
  repository: RepositoryConfig;
  includeLabels: string[];
  excludeLabels: string[];
  priorityStrategy: 'oldest' | 'newest' | 'updated' | 'complexity' | 'random';
  maxComplexity?: 'low' | 'medium' | 'high';
  excludeRecent: number; // Exclude issues worked on in last N hours
  excludeAuthors: string[];
  requireAssignee: boolean;
  maxDailyLimit?: number;
}

interface SelectedIssue {
  id: string;
  number: number;
  title: string;
  body: string;
  labels: string[];
  author: string;
  assignee?: string;
  createdAt: Date;
  updatedAt: Date;
  priority: number;
  complexity: 'low' | 'medium' | 'high';
  estimatedDuration: number;
  url: string;
}

interface WorkflowStartResult {
  started: boolean;
  workflowId?: string;
  startedAt?: Date;
  error?: string;
  estimatedDuration?: number;
}

interface IdleReason {
  type:
    | 'no_issues'
    | 'operational_limit'
    | 'manual_stop'
    | 'error_recovery'
    | 'maintenance'
    | 'rate_limit';
  description: string;
  autoResume: boolean;
  resumeAt?: Date;
  actionRequired: boolean;
}

interface OperationalLimits {
  maxConsecutiveIssues: number;
  currentConsecutive: number;
  maxDailyIssues: number;
  currentDaily: number;
  maxHourlyIssues: number;
  currentHourly: number;
  cooldownPeriodMinutes: number;
  nextAvailableTime?: Date;
  quotasExceeded: string[];
}

interface NextIssueConfig {
  enabled: boolean;
  autoTrigger: boolean;
  delayBetweenIssuesMinutes: number;
  maxConsecutiveIssues: number;
  maxDailyIssues: number;
  maxHourlyIssues: number;
  cooldownEnabled: boolean;
  idleTimeoutMinutes: number;
  emergencyStopEnabled: boolean;
  manualOverrideEnabled: boolean;
  selectionCriteria: IssueSelectionCriteria;
  operationalLimits: OperationalLimitsConfig;
}

interface OperationalLimitsConfig {
  enableDailyQuota: boolean;
  enableHourlyQuota: boolean;
  enableConsecutiveLimit: boolean;
  enableCooldownPeriod: boolean;
  quotaResetTime: string; // e.g., "00:00" for midnight
  timezone: string;
  exemptUsers: string[];
  exemptLabels: string[];
}
```

### Implementation Strategy

**1. Next Issue Selection Engine**:

```typescript
class NextIssueSelector implements INextIssueSelector {
  private operationalState: OperationalState;
  private idleTimer?: NodeJS.Timeout;
  private quotaTracker: QuotaTracker;

  constructor(
    private issueSelector: IIssueSelector,
    private workflowManager: IWorkflowManager,
    private gitPlatform: IGitPlatform,
    private config: NextIssueConfig,
    private logger: Logger,
    private eventStore: IEventStore
  ) {
    this.operationalState = {
      status: 'idle',
      lastActivity: new Date(),
      sessionStart: new Date(),
      consecutiveIssues: 0,
      dailyIssues: 0,
      hourlyIssues: 0,
    };

    this.quotaTracker = new QuotaTracker(this.config.operationalLimits);
  }

  async triggerNextIssueSelection(context: WorkflowContext): Promise<NextIssueResult> {
    const startTime = Date.now();

    try {
      // Validate workflow completion
      const validation = await this.validateWorkflowCompletion(context);

      if (!validation.isComplete) {
        return this.createIncompleteResult(validation, startTime);
      }

      // Check operational limits
      const limits = await this.checkOperationalLimits();

      if (!this.canProceedWithLimits(limits)) {
        return this.createLimitedResult(limits, startTime);
      }

      // Apply delay between issues
      await this.applyInterIssueDelay(context);

      // Select next issue
      const nextIssue = await this.selectNextIssue(this.config.selectionCriteria);

      if (!nextIssue) {
        return this.createIdleResult('no_issues', startTime);
      }

      // Start next workflow
      const workflowResult = await this.startNextIssueWorkflow(nextIssue);

      if (!workflowResult.started) {
        return this.createWorkflowStartFailedResult(workflowResult, startTime);
      }

      // Update operational state
      this.updateOperationalState(nextIssue, workflowResult);

      const result: NextIssueResult = {
        triggered: true,
        nextIssue,
        workflowStarted: true,
        idleState: false,
        operationalLimits: limits,
        transitionTime: Date.now() - startTime,
      };

      await this.logNextIssueSelection(result, context);

      return result;
    } catch (error) {
      await this.logSelectionError(error, context, startTime);

      return {
        triggered: false,
        workflowStarted: false,
        idleState: false,
        operationalLimits: await this.checkOperationalLimits(),
        transitionTime: Date.now() - startTime,
      };
    }
  }

  async validateWorkflowCompletion(context: WorkflowContext): Promise<CompletionValidation> {
    const steps: WorkflowStep[] = [];
    const issues: ValidationIssue[] = [];

    try {
      // Check issue selection completion
      if (context.currentIssue) {
        steps.push({
          name: 'issue_selection',
          status: 'completed',
          completedAt: context.workflowStartTime,
          metadata: { issueNumber: context.currentIssue.number },
        });
      } else {
        steps.push({
          name: 'issue_selection',
          status: 'pending',
        });
        issues.push({
          type: 'incomplete_workflow',
          severity: 'blocking',
          description: 'No current issue found in context',
          autoResolvable: false,
        });
      }

      // Check PR creation and merge
      if (context.currentPR) {
        if (context.mergeResult?.success) {
          steps.push({
            name: 'pr_merge',
            status: 'completed',
            completedAt: context.mergeResult.mergedAt,
            metadata: {
              prNumber: context.currentPR.number,
              mergeCommitSha: context.mergeResult.mergeCommitSha,
            },
          });
        } else {
          steps.push({
            name: 'pr_merge',
            status: 'failed',
            error: context.mergeResult?.errors?.[0]?.message || 'Unknown merge error',
          });
          issues.push({
            type: 'step_failed',
            severity: 'blocking',
            description: 'PR merge failed',
            step: 'pr_merge',
            autoResolvable: false,
          });
        }
      } else {
        steps.push({
          name: 'pr_merge',
          status: 'pending',
        });
      }

      // Check completion checkpoint
      if (context.completionResult) {
        if (context.completionResult.success) {
          steps.push({
            name: 'completion_checkpoint',
            status: 'completed',
            completedAt: context.completionResult.completedAt,
            metadata: {
              issueClosed: context.completionResult.issueClosed,
              branchCleaned: context.completionResult.branchCleaned,
            },
          });
        } else {
          steps.push({
            name: 'completion_checkpoint',
            status: 'failed',
            error: context.completionResult.errors?.[0]?.message || 'Completion failed',
          });
          issues.push({
            type: 'step_failed',
            severity: 'blocking',
            description: 'Completion checkpoint failed',
            step: 'completion_checkpoint',
            autoResolvable: false,
          });
        }
      } else {
        steps.push({
          name: 'completion_checkpoint',
          status: 'pending',
        });
      }

      const completedSteps = steps.filter((s) => s.status === 'completed');
      const failedSteps = steps.filter((s) => s.status === 'failed');
      const pendingSteps = steps.filter((s) => s.status === 'pending');

      const isComplete = failedSteps.length === 0 && pendingSteps.length === 0;
      const canProceed = isComplete || (failedSteps.length === 0 && pendingSteps.length > 0);

      return {
        isComplete,
        completedSteps,
        failedSteps,
        pendingSteps,
        canProceed,
        issues,
      };
    } catch (error) {
      this.logger.error('Workflow validation failed', {
        error: error.message,
      });

      return {
        isComplete: false,
        completedSteps: [],
        failedSteps: [],
        pendingSteps: [],
        canProceed: false,
        issues: [
          {
            type: 'data_inconsistency',
            severity: 'blocking',
            description: `Validation error: ${error.message}`,
            autoResolvable: false,
          },
        ],
      };
    }
  }

  async selectNextIssue(criteria: IssueSelectionCriteria): Promise<SelectedIssue | null> {
    try {
      // Apply exclusion filters
      const filteredCriteria = await this.applyExclusionFilters(criteria);

      // Select issue using configured strategy
      const selectedIssue = await this.issueSelector.selectNextIssue(filteredCriteria);

      if (!selectedIssue) {
        this.logger.info('No issues found matching criteria', {
          criteria: filteredCriteria,
        });
        return null;
      }

      // Validate selected issue
      const validation = await this.validateSelectedIssue(selectedIssue);

      if (!validation.valid) {
        this.logger.warn('Selected issue failed validation', {
          issueNumber: selectedIssue.number,
          issues: validation.issues,
        });
        return null;
      }

      // Enrich with additional metadata
      return await this.enrichSelectedIssue(selectedIssue);
    } catch (error) {
      this.logger.error('Issue selection failed', {
        error: error.message,
        criteria,
      });
      return null;
    }
  }

  private async applyExclusionFilters(
    criteria: IssueSelectionCriteria
  ): Promise<IssueSelectionCriteria> {
    const filteredCriteria = { ...criteria };

    // Exclude recently worked issues
    if (criteria.excludeRecent > 0) {
      const recentIssueNumbers = await this.getRecentIssueNumbers(criteria.excludeRecent);
      // This would need to be implemented in the issue selector
      filteredCriteria.excludeRecentIssues = recentIssueNumbers;
    }

    // Exclude specific authors
    if (criteria.excludeAuthors.length > 0) {
      filteredCriteria.excludeAuthors = criteria.excludeAuthors;
    }

    // Apply complexity limits
    if (criteria.maxComplexity) {
      filteredCriteria.maxComplexity = criteria.maxComplexity;
    }

    return filteredCriteria;
  }

  private async getRecentIssueNumbers(hours: number): Promise<number[]> {
    // Get issues worked on in the last N hours
    const cutoffTime = new Date(Date.now() - hours * 60 * 60 * 1000);

    // This would query the event store or workflow history
    const recentIssues = await this.eventStore.queryEvents({
      type: 'ISSUE.SELECTED.SUCCESS',
      since: cutoffTime,
      limit: 50,
    });

    return recentIssues
      .map((event) => event.tags.issueNumber)
      .filter((num) => num)
      .map((num) => parseInt(num));
  }

  private async validateSelectedIssue(issue: SelectedIssue): Promise<IssueValidation> {
    const issues: ValidationIssue[] = [];

    // Check if issue is still open
    if (issue.state !== 'open') {
      issues.push({
        type: 'data_inconsistency',
        severity: 'blocking',
        description: `Issue #${issue.number} is no longer open`,
        autoResolvable: false,
      });
    }

    // Check if issue is already assigned
    if (issue.assignee && issue.assignee !== 'tamma-bot') {
      issues.push({
        type: 'data_inconsistency',
        severity: 'blocking',
        description: `Issue #${issue.number} is already assigned to ${issue.assignee}`,
        autoResolvable: false,
      });
    }

    // Check for blocking labels
    const blockingLabels = ['blocked', 'on-hold', 'security-review', 'legal-review'];
    const hasBlockingLabel = issue.labels.some((label) =>
      blockingLabels.includes(label.toLowerCase())
    );

    if (hasBlockingLabel) {
      issues.push({
        type: 'data_inconsistency',
        severity: 'blocking',
        description: `Issue #${issue.number} has blocking labels`,
        autoResolvable: false,
      });
    }

    return {
      valid: issues.length === 0,
      issues,
    };
  }

  private async enrichSelectedIssue(issue: SelectedIssue): Promise<SelectedIssue> {
    // Add estimated duration based on complexity and historical data
    const estimatedDuration = await this.estimateIssueDuration(issue);

    // Calculate priority score
    const priority = await this.calculateIssuePriority(issue);

    // Assess complexity
    const complexity = await this.assessIssueComplexity(issue);

    return {
      ...issue,
      estimatedDuration,
      priority,
      complexity,
    };
  }

  private async estimateIssueDuration(issue: SelectedIssue): Promise<number> {
    // Base duration by complexity
    const baseDurations = {
      low: 30, // 30 minutes
      medium: 90, // 1.5 hours
      high: 240, // 4 hours
    };

    const complexity = await this.assessIssueComplexity(issue);
    let duration = baseDurations[complexity] || baseDurations.medium;

    // Adjust based on historical data
    const historicalData = await this.getHistoricalDurationData(issue);
    if (historicalData.averageDuration > 0) {
      duration = Math.round((duration + historicalData.averageDuration) / 2);
    }

    return duration;
  }

  private async calculateIssuePriority(issue: SelectedIssue): Promise<number> {
    let priority = 5; // Base priority

    // Adjust for age (older issues get higher priority)
    const ageInHours = (Date.now() - issue.createdAt.getTime()) / (1000 * 60 * 60);
    if (ageInHours > 168) {
      // 1 week
      priority += 2;
    } else if (ageInHours > 72) {
      // 3 days
      priority += 1;
    }

    // Adjust for labels
    const highPriorityLabels = ['urgent', 'critical', 'p0', 'p1'];
    const hasHighPriorityLabel = issue.labels.some((label) =>
      highPriorityLabels.includes(label.toLowerCase())
    );

    if (hasHighPriorityLabel) {
      priority += 3;
    }

    // Adjust for size/complexity
    const sizeLabels = { xs: -2, s: -1, m: 0, l: 1, xl: 2 };
    for (const [label, adjustment] of Object.entries(sizeLabels)) {
      if (issue.labels.includes(label)) {
        priority += adjustment;
        break;
      }
    }

    return Math.max(1, Math.min(10, priority));
  }

  async startNextIssueWorkflow(issue: SelectedIssue): Promise<WorkflowStartResult> {
    try {
      // Assign issue to bot
      await this.gitPlatform.assignIssue(issue.number, 'tamma-bot');

      // Start new workflow session
      const workflowId = await this.workflowManager.startWorkflow({
        issueId: issue.id,
        issueNumber: issue.number,
        mode: 'autonomous',
        triggeredBy: 'auto-next-issue',
        estimatedDuration: issue.estimatedDuration,
      });

      await this.eventStore.append({
        type: 'WORKFLOW.STARTED.AUTO',
        tags: {
          issueNumber: issue.number.toString(),
          workflowId,
        },
        data: {
          triggeredBy: 'auto-next-issue',
          previousIssueCompleted: this.operationalState.lastActivity?.toISOString(),
          estimatedDuration: issue.estimatedDuration,
          consecutiveIssues: this.operationalState.consecutiveIssues + 1,
        },
      });

      return {
        started: true,
        workflowId,
        startedAt: new Date(),
        estimatedDuration: issue.estimatedDuration,
      };
    } catch (error) {
      this.logger.error('Failed to start next issue workflow', {
        issueNumber: issue.number,
        error: error.message,
      });

      return {
        started: false,
        error: error.message,
      };
    }
  }

  async enterIdleState(reason: IdleReason): Promise<void> {
    this.operationalState.status = 'idle';
    this.operationalState.idleReason = reason;
    this.operationalState.lastActivity = new Date();

    // Clear any existing idle timer
    if (this.idleTimer) {
      clearTimeout(this.idleTimer);
    }

    // Set timer to check for new issues
    if (reason.autoResume && reason.resumeAt) {
      const resumeDelay = reason.resumeAt.getTime() - Date.now();

      this.idleTimer = setTimeout(async () => {
        this.logger.info('Attempting to resume from idle state');
        await this.attemptResumeFromIdle();
      }, resumeDelay);
    }

    await this.eventStore.append({
      type: 'WORKFLOW.IDLE.ENTERED',
      tags: {
        idleReason: reason.type,
      },
      data: {
        reason: reason.description,
        autoResume: reason.autoResume,
        resumeAt: reason.resumeAt?.toISOString(),
        actionRequired: reason.actionRequired,
      },
    });

    this.logger.info('Entered idle state', {
      reason: reason.type,
      description: reason.description,
      autoResume: reason.autoResume,
    });
  }

  async checkOperationalLimits(): Promise<OperationalLimits> {
    const now = new Date();
    const quotasExceeded: string[] = [];

    // Check consecutive issues limit
    const consecutiveLimit = this.config.maxConsecutiveIssues;
    const currentConsecutive = this.operationalState.consecutiveIssues;
    const consecutiveExceeded = currentConsecutive >= consecutiveLimit;

    if (consecutiveExceeded) {
      quotasExceeded.push('consecutive_issues');
    }

    // Check daily quota
    const dailyLimit = this.config.maxDailyIssues;
    const currentDaily = this.quotaTracker.getDailyCount(now);
    const dailyExceeded = dailyLimit && currentDaily >= dailyLimit;

    if (dailyExceeded) {
      quotasExceeded.push('daily_quota');
    }

    // Check hourly quota
    const hourlyLimit = this.config.maxHourlyIssues;
    const currentHourly = this.quotaTracker.getHourlyCount(now);
    const hourlyExceeded = hourlyLimit && currentHourly >= hourlyLimit;

    if (hourlyExceeded) {
      quotasExceeded.push('hourly_quota');
    }

    // Calculate next available time
    let nextAvailableTime: Date | undefined;

    if (quotasExceeded.length > 0) {
      nextAvailableTime = this.calculateNextAvailableTime(quotasExceeded, now);
    }

    return {
      maxConsecutiveIssues: consecutiveLimit,
      currentConsecutive,
      maxDailyIssues: dailyLimit || 0,
      currentDaily,
      maxHourlyIssues: hourlyLimit || 0,
      currentHourly,
      cooldownPeriodMinutes: this.config.delayBetweenIssuesMinutes,
      nextAvailableTime,
      quotasExceeded,
    };
  }

  private canProceedWithLimits(limits: OperationalLimits): boolean {
    // Can proceed if no quotas exceeded
    if (limits.quotasExceeded.length === 0) {
      return true;
    }

    // Check if any exemptions apply
    const hasExemptions =
      this.config.operationalLimits.exemptUsers.length > 0 ||
      this.config.operationalLimits.exemptLabels.length > 0;

    // Can proceed if in cooldown period but no hard limits exceeded
    const onlyCooldownExceeded =
      limits.quotasExceeded.length === 1 && limits.quotasExceeded[0] === 'cooldown_period';

    return hasExemptions || onlyCooldownExceeded;
  }

  private async applyInterIssueDelay(context: WorkflowContext): Promise<void> {
    if (this.config.delayBetweenIssuesMinutes <= 0) {
      return;
    }

    const timeSinceLastActivity = Date.now() - context.lastActivityTime.getTime();
    const requiredDelay = this.config.delayBetweenIssuesMinutes * 60 * 1000;

    if (timeSinceLastActivity < requiredDelay) {
      const remainingDelay = requiredDelay - timeSinceLastActivity;

      this.logger.info('Applying inter-issue delay', {
        remainingDelay: Math.round(remainingDelay / 1000),
        requiredDelay: this.config.delayBetweenIssuesMinutes,
      });

      await new Promise((resolve) => setTimeout(resolve, remainingDelay));
    }
  }

  private updateOperationalState(issue: SelectedIssue, workflowResult: WorkflowStartResult): void {
    this.operationalState.status = 'active';
    this.operationalState.lastActivity = new Date();
    this.operationalState.consecutiveIssues++;
    this.operationalState.currentIssue = issue;
    this.operationalState.currentWorkflowId = workflowResult.workflowId;

    // Update quota tracker
    this.quotaTracker.incrementIssue();
  }

  private createIncompleteResult(
    validation: CompletionValidation,
    startTime: number
  ): NextIssueResult {
    return {
      triggered: false,
      workflowStarted: false,
      idleState: false,
      operationalLimits: { quotasExceeded: [] } as OperationalLimits,
      transitionTime: Date.now() - startTime,
    };
  }

  private createLimitedResult(limits: OperationalLimits, startTime: number): NextIssueResult {
    const idleReason: IdleReason = {
      type: 'operational_limit',
      description: `Operational limits exceeded: ${limits.quotasExceeded.join(', ')}`,
      autoResume: limits.nextAvailableTime !== undefined,
      resumeAt: limits.nextAvailableTime,
      actionRequired: false,
    };

    return {
      triggered: false,
      workflowStarted: false,
      idleState: true,
      idleReason,
      delayUntil: limits.nextAvailableTime,
      operationalLimits: limits,
      transitionTime: Date.now() - startTime,
    };
  }

  private createIdleResult(reasonType: IdleReason['type'], startTime: number): NextIssueResult {
    const reason: IdleReason = {
      type: reasonType,
      description: this.getIdleReasonDescription(reasonType),
      autoResume: reasonType === 'no_issues',
      actionRequired: reasonType === 'manual_stop',
    };

    return {
      triggered: false,
      workflowStarted: false,
      idleState: true,
      idleReason: reason,
      operationalLimits: { quotasExceeded: [] } as OperationalLimits,
      transitionTime: Date.now() - startTime,
    };
  }

  private createWorkflowStartFailedResult(
    workflowResult: WorkflowStartResult,
    startTime: number
  ): NextIssueResult {
    return {
      triggered: false,
      workflowStarted: false,
      idleState: false,
      operationalLimits: { quotasExceeded: [] } as OperationalLimits,
      transitionTime: Date.now() - startTime,
    };
  }

  private getIdleReasonDescription(reasonType: IdleReason['type']): string {
    const descriptions: Record<IdleReason['type'], string> = {
      no_issues: 'No issues available for processing',
      operational_limit: 'Operational limits reached',
      manual_stop: 'Manually stopped',
      error_recovery: 'Waiting for error recovery',
      maintenance: 'System under maintenance',
      rate_limit: 'API rate limit reached',
    };

    return descriptions[reasonType] || 'Unknown idle reason';
  }

  private async logNextIssueSelection(
    result: NextIssueResult,
    context: WorkflowContext
  ): Promise<void> {
    await this.eventStore.append({
      type: result.triggered ? 'NEXT_ISSUE.SELECTED.SUCCESS' : 'NEXT_ISSUE.SELECTED.FAILED',
      tags: {
        triggered: result.triggered.toString(),
        workflowStarted: result.workflowStarted.toString(),
        idleState: result.idleState.toString(),
      },
      data: {
        nextIssueNumber: result.nextIssue?.number,
        transitionTime: result.transitionTime,
        operationalLimits: result.operationalLimits,
        idleReason: result.idleReason?.type,
      },
    });
  }

  private async logSelectionError(
    error: Error,
    context: WorkflowContext,
    startTime: number
  ): Promise<void> {
    await this.eventStore.append({
      type: 'NEXT_ISSUE.SELECTION.ERROR',
      tags: {},
      data: {
        error: error.message,
        transitionTime: Date.now() - startTime,
        context: {
          currentIssueNumber: context.currentIssue?.number,
          consecutiveIssues: context.consecutiveIssues,
        },
      },
    });
  }
}

interface OperationalState {
  status: 'idle' | 'active' | 'stopped' | 'maintenance';
  lastActivity: Date;
  sessionStart: Date;
  consecutiveIssues: number;
  dailyIssues: number;
  hourlyIssues: number;
  currentIssue?: SelectedIssue;
  currentWorkflowId?: string;
  idleReason?: IdleReason;
}

interface IssueValidation {
  valid: boolean;
  issues: ValidationIssue[];
}
```

### Integration Points

**1. Issue Selector Integration**:

- `selectNextIssue()` - Core issue selection logic
- Apply filters and criteria
- Handle exclusion rules

**2. Workflow Manager Integration**:

- `startWorkflow()` - Start new autonomous workflow
- Pass issue context and configuration
- Track workflow execution

**3. Git Platform Integration**:

- `assignIssue()` - Assign selected issue to bot
- `getIssue()` - Get issue details for validation
- Handle issue state changes

**4. Event Store Integration**:

- `NEXT_ISSUE.SELECTED.SUCCESS/FAILED`
- `WORKFLOW.STARTED.AUTO`
- `WORKFLOW.IDLE.ENTERED`
- Complete audit trail

### Testing Strategy

**Unit Tests**:

```typescript
describe('NextIssueSelector', () => {
  let selector: NextIssueSelector;
  let mockIssueSelector: jest.Mocked<IIssueSelector>;
  let mockWorkflowManager: jest.Mocked<IWorkflowManager>;
  let mockGitPlatform: jest.Mocked<IGitPlatform>;

  beforeEach(() => {
    mockIssueSelector = createMockIssueSelector();
    mockWorkflowManager = createMockWorkflowManager();
    mockGitPlatform = createMockGitPlatform();
    selector = new NextIssueSelector(
      mockIssueSelector,
      mockWorkflowManager,
      mockGitPlatform,
      createMockNextIssueConfig(),
      mockLogger,
      mockEventStore
    );
  });

  describe('triggerNextIssueSelection', () => {
    it('should select and start next issue after successful completion', async () => {
      const context = createMockWorkflowContext({
        currentIssue: createMockSelectedIssue({ number: 123 }),
        mergeResult: createMockMergeResult({ success: true }),
        completionResult: createMockCompletionResult({ success: true }),
      });

      const nextIssue = createMockSelectedIssue({ number: 124 });

      mockIssueSelector.selectNextIssue.mockResolvedValue(nextIssue);
      mockGitPlatform.assignIssue.mockResolvedValue();
      mockWorkflowManager.startWorkflow.mockResolvedValue({
        started: true,
        workflowId: 'workflow-456',
        startedAt: new Date(),
      });

      const result = await selector.triggerNextIssueSelection(context);

      expect(result.triggered).toBe(true);
      expect(result.nextIssue).toEqual(nextIssue);
      expect(result.workflowStarted).toBe(true);
      expect(mockGitPlatform.assignIssue).toHaveBeenCalledWith(124, 'tamma-bot');
      expect(mockWorkflowManager.startWorkflow).toHaveBeenCalled();
    });

    it('should enter idle state when no issues available', async () => {
      const context = createMockWorkflowContext({
        mergeResult: createMockMergeResult({ success: true }),
        completionResult: createMockCompletionResult({ success: true }),
      });

      mockIssueSelector.selectNextIssue.mockResolvedValue(null);

      const result = await selector.triggerNextIssueSelection(context);

      expect(result.triggered).toBe(false);
      expect(result.idleState).toBe(true);
      expect(result.idleReason?.type).toBe('no_issues');
    });

    it('should respect operational limits', async () => {
      const context = createMockWorkflowContext({
        consecutiveIssues: 10, // At limit
        mergeResult: createMockMergeResult({ success: true }),
        completionResult: createMockCompletionResult({ success: true }),
      });

      const result = await selector.triggerNextIssueSelection(context);

      expect(result.triggered).toBe(false);
      expect(result.idleState).toBe(true);
      expect(result.idleReason?.type).toBe('operational_limit');
      expect(result.operationalLimits.quotasExceeded).toContain('consecutive_issues');
    });

    it('should validate workflow completion before proceeding', async () => {
      const context = createMockWorkflowContext({
        currentIssue: createMockSelectedIssue({ number: 125 }),
        mergeResult: createMockMergeResult({ success: false }), // Failed merge
      });

      const result = await selector.triggerNextIssueSelection(context);

      expect(result.triggered).toBe(false);
      expect(result.idleState).toBe(false);
      // Should not proceed due to incomplete workflow
    });
  });

  describe('validateWorkflowCompletion', () => {
    it('should validate successful workflow completion', async () => {
      const context = createMockWorkflowContext({
        currentIssue: createMockSelectedIssue({ number: 126 }),
        currentPR: createMockPullRequest({ number: 789 }),
        mergeResult: createMockMergeResult({ success: true }),
        completionResult: createMockCompletionResult({ success: true }),
      });

      const validation = await selector.validateWorkflowCompletion(context);

      expect(validation.isComplete).toBe(true);
      expect(validation.canProceed).toBe(true);
      expect(validation.completedSteps).toHaveLength(3);
      expect(validation.failedSteps).toHaveLength(0);
    });

    it('should detect incomplete workflow', async () => {
      const context = createMockWorkflowContext({
        currentIssue: createMockSelectedIssue({ number: 127 }),
        mergeResult: createMockMergeResult({ success: false }),
      });

      const validation = await selector.validateWorkflowCompletion(context);

      expect(validation.isComplete).toBe(false);
      expect(validation.canProceed).toBe(false);
      expect(validation.failedSteps).toHaveLength(1);
      expect(validation.failedSteps[0].name).toBe('pr_merge');
    });
  });

  describe('checkOperationalLimits', () => {
    it('should enforce daily quota limits', async () => {
      // Mock quota tracker to return daily limit exceeded
      const quotaTracker = (selector as any).quotaTracker;
      quotaTracker.getDailyCount.mockReturnValue(25); // At daily limit of 25

      const limits = await selector.checkOperationalLimits();

      expect(limits.currentDaily).toBe(25);
      expect(limits.maxDailyIssues).toBe(25);
      expect(limits.quotasExceeded).toContain('daily_quota');
      expect(limits.nextAvailableTime).toBeDefined();
    });

    it('should enforce consecutive issue limits', async () => {
      (selector as any).operationalState.consecutiveIssues = 15; // At limit

      const limits = await selector.checkOperationalLimits();

      expect(limits.currentConsecutive).toBe(15);
      expect(limits.maxConsecutiveIssues).toBe(15);
      expect(limits.quotasExceeded).toContain('consecutive_issues');
    });
  });
});
```

### Configuration Examples

**Next Issue Selection Configuration**:

```yaml
next_issue_selection:
  enabled: true
  auto_trigger: true
  delay_between_issues_minutes: 5
  max_consecutive_issues: 10
  max_daily_issues: 25
  max_hourly_issues: 5
  cooldown_enabled: true
  idle_timeout_minutes: 60
  emergency_stop_enabled: true
  manual_override_enabled: true

  selection_criteria:
    repository:
      owner: 'tamma'
      name: 'tamma'
      platform: 'github'

    include_labels: ['ready-for-dev', 'automated']
    exclude_labels: ['blocked', 'on-hold', 'security-review', 'manual-only']
    priority_strategy: 'oldest' # oldest, newest, updated, complexity, random
    max_complexity: 'high'
    exclude_recent: 24 # hours
    exclude_authors: ['bot', 'external-contractor']
    require_assignee: false
    max_daily_limit: 25

  operational_limits:
    enable_daily_quota: true
    enable_hourly_quota: true
    enable_consecutive_limit: true
    enable_cooldown_period: true
    quota_reset_time: '00:00' # midnight
    timezone: 'UTC'
    exempt_users: ['admin', 'maintainer']
    exempt_labels: ['urgent', 'critical', 'security']

  idle_state:
    auto_resume: true
    check_interval_minutes: 10
    max_idle_hours: 24
    notification_on_idle: true
    idle_notification_channels: ['slack', 'email']

  emergency_controls:
    enable_emergency_stop: true
    stop_trigger_labels: ['emergency-stop', 'halt-automation']
    manual_override_commands: ['tamma stop', 'tamma pause', 'tamma resume']
    require_confirmation: true
    notification_on_stop: true

  monitoring:
    track_session_metrics: true
    log_transitions: true
    performance_tracking: true
    quota_monitoring: true
    alert_on_anomalies: true
```

---

## Implementation Notes

**Key Considerations**:

1. **Workflow Validation**: Ensure current workflow is fully complete before starting next.

2. **Operational Limits**: Prevent burnout and resource exhaustion through quotas.

3. **Idle State Management**: Graceful handling of no-work scenarios with auto-resume.

4. **Rate Limiting**: Respect API limits and prevent overwhelming systems.

5. **Emergency Controls**: Ability to stop or pause automation when needed.

6. **Continuous Operation**: Seamless transition between issues for true autonomy.

**Performance Targets**:

- Workflow validation: < 5 seconds
- Issue selection: < 10 seconds
- Workflow start: < 5 seconds
- Total transition: < 30 seconds

**Security Considerations**:

- Validate issue assignments and permissions
- Secure emergency stop mechanisms
- Proper authentication for all operations
- Audit all autonomous transitions
- Handle sensitive issues appropriately

**Autonomous Operation Best Practices**:

- Clear operational boundaries and limits
- Comprehensive monitoring and alerting
- Graceful degradation and error recovery
- Human oversight and intervention capabilities
- Detailed audit trails for compliance
- Performance optimization for continuous operation
- Resource management and quota enforcement
- Emergency response procedures

### References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md) - Search spikes, bugs, findings, decisions
