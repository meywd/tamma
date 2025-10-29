# Story 2.9: PR Status Monitoring

**Epic**: Epic 2 - Autonomous Development Loop - Core  
**Status**: Ready for Development  
**Priority**: High  
**Prerequisites**: Story 2.8 (pull request creation must complete first)

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
I want the system to monitor pull request status and CI/CD checks,
So that I can track progress and respond to feedback or failures.

---

## Acceptance Criteria

1. System monitors PR status changes (reviews, comments, CI/CD checks)
2. System detects and handles review feedback (approvals, requested changes)
3. System monitors CI/CD pipeline status and handles failures
4. System implements retry logic for transient CI/CD failures
5. System escalates to human when intervention is required
6. All status changes and actions logged to event trail
7. Integration test validates PR monitoring workflow
8. Configurable monitoring intervals and escalation policies

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

**PRStatusMonitor Service**:

```typescript
interface IPRStatusMonitor {
  startMonitoring(pr: PullRequest): Promise<void>;
  stopMonitoring(prNumber: number): Promise<void>;
  checkPRStatus(prNumber: number): Promise<PRStatus>;
  handleReviewFeedback(pr: PullRequest, reviews: Review[]): Promise<void>;
  handleCICDStatus(pr: PullRequest, checks: CICDCheck[]): Promise<void>;
  escalateToHuman(pr: PullRequest, reason: EscalationReason): Promise<void>;
}

interface PRStatus {
  number: number;
  state: 'open' | 'closed' | 'merged';
  mergeable: boolean | null;
  draft: boolean;
  reviews: Review[];
  comments: Comment[];
  checks: CICDCheck[];
  lastUpdated: Date;
  timeOpen: number;
  approvalCount: number;
  changeRequestCount: number;
  blockingIssues: BlockingIssue[];
}

interface Review {
  id: string;
  author: string;
  state: 'approved' | 'changes_requested' | 'commented' | 'dismissed';
  body: string;
  submittedAt: Date;
  lastEditedAt?: Date;
  commits: string[];
  files: string[];
  actionable: boolean;
  priority: 'high' | 'medium' | 'low';
  type: 'code_review' | 'security_review' | 'performance_review' | 'documentation_review';
}

interface Comment {
  id: string;
  author: string;
  body: string;
  createdAt: Date;
  lastEditedAt?: Date;
  resolved: boolean;
  actionable: boolean;
  type: 'general' | 'suggestion' | 'question' | 'issue' | 'approval' | 'blocker';
  mentions: string[];
  reactions: Reaction[];
}

interface Reaction {
  emoji: string;
  count: number;
  users: string[];
}

interface CICDCheck {
  id: string;
  name: string;
  status: 'queued' | 'in_progress' | 'completed' | 'failure' | 'cancelled' | 'timed_out';
  conclusion?: 'success' | 'failure' | 'neutral' | 'cancelled' | 'timed_out' | 'action_required';
  startedAt?: Date;
  completedAt?: Date;
  detailsUrl?: string;
  externalId?: string;
  app?: CICDApp;
  steps: CICDStep[];
  retryable: boolean;
  maxRetries: number;
  currentRetries: number;
}

interface CICDStep {
  name: string;
  status: 'queued' | 'in_progress' | 'completed' | 'failure';
  conclusion?: 'success' | 'failure' | 'cancelled';
  startedAt?: Date;
  completedAt?: Date;
  number: number;
  logUrl?: string;
}

interface CICDApp {
  id: string;
  name: string;
  slug: string;
  owner: string;
}

interface BlockingIssue {
  type:
    | 'review_changes'
    | 'ci_failure'
    | 'merge_conflict'
    | 'approval_required'
    | 'policy_violation';
  description: string;
  severity: 'blocking' | 'warning' | 'info';
  actionable: boolean;
  autoResolvable: boolean;
  resolution?: string;
}

interface EscalationReason {
  type: 'ci_failure' | 'review_blocking' | 'merge_conflict' | 'timeout' | 'policy_violation';
  description: string;
  severity: 'high' | 'medium' | 'low';
  autoResolvable: boolean;
  deadline?: Date;
}

interface PRMonitoringConfig {
  enabled: boolean;
  checkIntervalSeconds: number;
  maxMonitoringHours: number;
  autoRetryCI: boolean;
  maxCIRetries: number;
  escalationEnabled: boolean;
  escalationChannels: EscalationChannel[];
  reviewResponse: ReviewResponseConfig;
  ciFailureResponse: CIFailureResponseConfig;
  mergeConflictResponse: MergeConflictResponseConfig;
}

interface EscalationChannel {
  type: 'slack' | 'email' | 'webhook' | 'cli';
  enabled: boolean;
  config: Record<string, any>;
  severity: ('high' | 'medium' | 'low')[];
}

interface ReviewResponseConfig {
  autoRespondToSuggestions: boolean;
  autoImplementChanges: boolean;
  maxChangeComplexity: 'low' | 'medium' | 'high';
  responseDelayMinutes: number;
  acknowledgeReceipt: boolean;
}

interface CIFailureResponseConfig {
  autoRetryTransientFailures: boolean;
  retryablePatterns: string[];
  maxRetriesPerCheck: number;
  retryDelayMinutes: number;
  escalateOnPersistentFailure: boolean;
  failureAnalysisEnabled: boolean;
}

interface MergeConflictResponseConfig {
  autoRebase: boolean;
  autoMerge: boolean;
  conflictResolutionStrategy: 'manual' | 'auto_rebase' | 'auto_merge' | 'escalate';
  maxRebaseAttempts: number;
  escalateOnConflict: boolean;
}
```

### Implementation Strategy

**1. PR Monitoring Engine**:

```typescript
class PRStatusMonitor implements IPRStatusMonitor {
  private monitoringSessions = new Map<number, MonitoringSession>();
  private checkIntervals = new Map<number, NodeJS.Timeout>();

  constructor(
    private gitPlatform: IGitPlatform,
    private ciAnalyzer: ICIAnalyzer,
    private reviewProcessor: IReviewProcessor,
    private escalationManager: IEscalationManager,
    private config: PRMonitoringConfig,
    private logger: Logger,
    private eventStore: IEventStore
  ) {}

  async startMonitoring(pr: PullRequest): Promise<void> {
    if (this.monitoringSessions.has(pr.number)) {
      this.logger.warn('PR monitoring already active', { prNumber: pr.number });
      return;
    }

    const session: MonitoringSession = {
      prNumber: pr.number,
      startedAt: new Date(),
      lastCheck: new Date(),
      status: 'active',
      checks: {},
      reviews: {},
      escalations: [],
    };

    this.monitoringSessions.set(pr.number, session);

    // Start periodic checking
    const interval = setInterval(
      () => this.checkPRStatus(pr.number),
      this.config.checkIntervalSeconds * 1000
    );

    this.checkIntervals.set(pr.number, interval);

    await this.eventStore.append({
      type: 'PR.MONITORING.STARTED',
      tags: {
        prNumber: pr.number.toString(),
        issueNumber: pr.metadata?.issueNumber || 'unknown',
      },
      data: {
        checkInterval: this.config.checkIntervalSeconds,
        maxMonitoringHours: this.config.maxMonitoringHours,
        startedAt: session.startedAt.toISOString(),
      },
    });

    this.logger.info('Started PR monitoring', {
      prNumber: pr.number,
      checkInterval: this.config.checkIntervalSeconds,
    });

    // Perform initial check
    await this.checkPRStatus(pr.number);
  }

  async stopMonitoring(prNumber: number): Promise<void> {
    const interval = this.checkIntervals.get(prNumber);
    if (interval) {
      clearInterval(interval);
      this.checkIntervals.delete(prNumber);
    }

    const session = this.monitoringSessions.get(prNumber);
    if (session) {
      session.status = 'stopped';
      session.stoppedAt = new Date();

      await this.eventStore.append({
        type: 'PR.MONITORING.STOPPED',
        tags: {
          prNumber: prNumber.toString(),
        },
        data: {
          duration: Date.now() - session.startedAt.getTime(),
          stoppedAt: session.stoppedAt.toISOString(),
        },
      });
    }

    this.monitoringSessions.delete(prNumber);

    this.logger.info('Stopped PR monitoring', { prNumber });
  }

  async checkPRStatus(prNumber: number): Promise<PRStatus> {
    const session = this.monitoringSessions.get(prNumber);
    if (!session || session.status !== 'active') {
      return null;
    }

    try {
      // Check if monitoring should timeout
      if (this.isMonitoringTimeout(session)) {
        await this.handleMonitoringTimeout(prNumber);
        return null;
      }

      // Get current PR status
      const pr = await this.gitPlatform.getPullRequest(prNumber);
      if (!pr) {
        this.logger.warn('PR not found during monitoring', { prNumber });
        await this.stopMonitoring(prNumber);
        return null;
      }

      // Get reviews and comments
      const reviews = await this.gitPlatform.getPRReviews(prNumber);
      const comments = await this.gitPlatform.getPRComments(prNumber);

      // Get CI/CD checks
      const checks = await this.gitPlatform.getPRChecks(prNumber);

      // Build status object
      const status: PRStatus = {
        number: prNumber,
        state: pr.state,
        mergeable: pr.mergeable,
        draft: pr.draft || false,
        reviews: this.processReviews(reviews, session),
        comments: this.processComments(comments, session),
        checks: this.processChecks(checks, session),
        lastUpdated: new Date(),
        timeOpen: Date.now() - pr.createdAt.getTime(),
        approvalCount: reviews.filter((r) => r.state === 'approved').length,
        changeRequestCount: reviews.filter((r) => r.state === 'changes_requested').length,
        blockingIssues: this.identifyBlockingIssues(pr, reviews, checks),
      };

      // Handle status changes
      await this.handleStatusChanges(pr, status, session);

      // Update session
      session.lastCheck = new Date();
      session.lastStatus = status;

      return status;
    } catch (error) {
      this.logger.error('Failed to check PR status', {
        prNumber,
        error: error.message,
      });

      await this.eventStore.append({
        type: 'PR.MONITORING.CHECK_FAILED',
        tags: {
          prNumber: prNumber.toString(),
        },
        data: {
          error: error.message,
          checkedAt: new Date().toISOString(),
        },
      });

      return null;
    }
  }

  private async handleStatusChanges(
    pr: PullRequest,
    status: PRStatus,
    session: MonitoringSession
  ): Promise<void> {
    // Handle new reviews
    const newReviews = this.findNewReviews(status.reviews, session);
    if (newReviews.length > 0) {
      await this.handleReviewFeedback(pr, newReviews);
    }

    // Handle new comments
    const newComments = this.findNewComments(status.comments, session);
    if (newComments.length > 0) {
      await this.handleNewComments(pr, newComments);
    }

    // Handle CI/CD status changes
    const changedChecks = this.findChangedChecks(status.checks, session);
    if (changedChecks.length > 0) {
      await this.handleCICDStatus(pr, changedChecks);
    }

    // Handle blocking issues
    if (status.blockingIssues.length > 0) {
      await this.handleBlockingIssues(pr, status.blockingIssues);
    }

    // Check if PR is ready for merge
    if (this.isReadyForMerge(status)) {
      await this.handleReadyForMerge(pr, status);
    }

    // Check if PR should be closed
    if (this.shouldClosePR(pr, status)) {
      await this.handlePRClosure(pr, status);
    }
  }

  async handleReviewFeedback(pr: PullRequest, reviews: Review[]): Promise<void> {
    for (const review of reviews) {
      try {
        switch (review.state) {
          case 'approved':
            await this.handleReviewApproval(pr, review);
            break;

          case 'changes_requested':
            await this.handleReviewChangesRequested(pr, review);
            break;

          case 'commented':
            await this.handleReviewComment(pr, review);
            break;

          case 'dismissed':
            await this.handleReviewDismissed(pr, review);
            break;
        }

        await this.eventStore.append({
          type: 'PR.REVIEW.PROCESSED',
          tags: {
            prNumber: pr.number.toString(),
            reviewState: review.state,
            reviewAuthor: review.author,
          },
          data: {
            reviewId: review.id,
            actionable: review.actionable,
            priority: review.priority,
            type: review.type,
          },
        });
      } catch (error) {
        this.logger.error('Failed to process review', {
          prNumber: pr.number,
          reviewId: review.id,
          error: error.message,
        });
      }
    }
  }

  private async handleReviewChangesRequested(pr: PullRequest, review: Review): Promise<void> {
    this.logger.info('Processing requested changes', {
      prNumber: pr.number,
      reviewAuthor: review.author,
      reviewId: review.id,
    });

    // Analyze review feedback
    const analysis = await this.reviewProcessor.analyzeReview(review, pr);

    if (analysis.actionable) {
      if (analysis.autoImplementable && this.config.reviewResponse.autoImplementChanges) {
        // Try to automatically implement changes
        const implementation = await this.reviewProcessor.implementChanges(review, pr);

        if (implementation.success) {
          await this.commitChanges(pr, implementation.changes, review);

          await this.gitPlatform.addPRComment(pr.number, {
            body: `🤖 **Automated Response**

I've implemented the requested changes based on your review feedback:

${implementation.summary}

Changes have been committed to this branch. Please review the updates.

---
*This response was generated automatically by Tamma*`,
          });
        } else {
          // Auto-implementation failed, escalate
          await this.escalateToHuman(pr, {
            type: 'review_blocking',
            description: `Unable to automatically implement changes requested by ${review.author}: ${implementation.error}`,
            severity: 'medium',
            autoResolvable: false,
          });
        }
      } else {
        // Changes require human intervention
        await this.escalateToHuman(pr, {
          type: 'review_blocking',
          description: `Changes requested by ${review.author} require manual implementation: ${review.body.substring(0, 200)}...`,
          severity: 'medium',
          autoResolvable: false,
        });
      }
    } else {
      // Non-actionable feedback, just acknowledge
      if (this.config.reviewResponse.acknowledgeReceipt) {
        await this.gitPlatform.addPRComment(pr.number, {
          body: `👋 **Acknowledged**

Thank you for your review feedback, ${review.author}! I've noted your comments and will take them into consideration.

---
*This response was generated automatically by Tamma*`,
        });
      }
    }
  }

  async handleCICDStatus(pr: PullRequest, checks: CICDCheck[]): Promise<void> {
    for (const check of checks) {
      try {
        const session = this.monitoringSessions.get(pr.number);
        const previousStatus = session?.checks[check.id]?.status;

        // Only handle status changes
        if (previousStatus === check.status) {
          continue;
        }

        switch (check.status) {
          case 'failure':
            await this.handleCIFailure(pr, check);
            break;

          case 'completed':
            if (check.conclusion === 'success') {
              await this.handleCISuccess(pr, check);
            } else {
              await this.handleCIFailure(pr, check);
            }
            break;

          case 'cancelled':
          case 'timed_out':
            await this.handleCIInterrupted(pr, check);
            break;
        }

        await this.eventStore.append({
          type: 'PR.CI.STATUS_CHANGED',
          tags: {
            prNumber: pr.number.toString(),
            checkName: check.name,
            checkStatus: check.status,
            checkConclusion: check.conclusion || 'unknown',
          },
          data: {
            checkId: check.id,
            previousStatus,
            retryable: check.retryable,
            currentRetries: check.currentRetries,
            maxRetries: check.maxRetries,
          },
        });
      } catch (error) {
        this.logger.error('Failed to process CI check', {
          prNumber: pr.number,
          checkId: check.id,
          error: error.message,
        });
      }
    }
  }

  private async handleCIFailure(pr: PullRequest, check: CICDCheck): Promise<void> {
    this.logger.warn('CI check failed', {
      prNumber: pr.number,
      checkName: check.name,
      conclusion: check.conclusion,
    });

    // Analyze failure
    const analysis = await this.ciAnalyzer.analyzeFailure(check, pr);

    if (analysis.transient && this.config.ciFailureResponse.autoRetryTransientFailures) {
      // Retry transient failures
      if (check.currentRetries < check.maxRetries) {
        await this.retryCICheck(pr, check);
        return;
      }
    }

    // Persistent failure - escalate
    const escalationReason: EscalationReason = {
      type: 'ci_failure',
      description: `CI check "${check.name}" failed: ${analysis.description}`,
      severity: analysis.critical ? 'high' : 'medium',
      autoResolvable: false,
    };

    await this.escalateToHuman(pr, escalationReason);

    // Add comment to PR
    await this.gitPlatform.addPRComment(pr.number, {
      body: `❌ **CI Check Failed**

The following CI check has failed:

**${check.name}**
${analysis.description}

${
  analysis.suggestions.length > 0
    ? `
**Suggestions:**
${analysis.suggestions.map((s) => `- ${s}`).join('\n')}
`
    : ''
}

${check.detailsUrl ? `[View Details](${check.detailsUrl})` : ''}

---
*This notification was generated automatically by Tamma*`,
    });
  }

  private async retryCICheck(pr: PullRequest, check: CICDCheck): Promise<void> {
    this.logger.info('Retrying CI check', {
      prNumber: pr.number,
      checkName: check.name,
      attempt: check.currentRetries + 1,
    });

    try {
      // Trigger retry (platform-specific implementation)
      await this.gitPlatform.rerunCheck(pr.number, check.id);

      // Update retry count
      check.currentRetries++;

      await this.eventStore.append({
        type: 'PR.CI.RETRY_TRIGGERED',
        tags: {
          prNumber: pr.number.toString(),
          checkName: check.name,
        },
        data: {
          checkId: check.id,
          attempt: check.currentRetries,
          maxRetries: check.maxRetries,
          retryDelay: this.config.ciFailureResponse.retryDelayMinutes,
        },
      });

      // Add comment about retry
      await this.gitPlatform.addPRComment(pr.number, {
        body: `🔄 **Retrying CI Check**

Automatically retrying "${check.name}" (attempt ${check.currentRetries}/${check.maxRetries})...

This is a ${this.config.ciFailureResponse.retryDelayMinutes} minute delay to allow the system to stabilize.

---
*This action was performed automatically by Tamma*`,
      });
    } catch (error) {
      this.logger.error('Failed to retry CI check', {
        prNumber: pr.number,
        checkId: check.id,
        error: error.message,
      });
    }
  }

  private identifyBlockingIssues(
    pr: PullRequest,
    reviews: Review[],
    checks: CICDCheck[]
  ): BlockingIssue[] {
    const issues: BlockingIssue[] = [];

    // Check for requested changes
    const changeRequests = reviews.filter((r) => r.state === 'changes_requested');
    if (changeRequests.length > 0) {
      issues.push({
        type: 'review_changes',
        description: `${changeRequests.length} review(s) requesting changes`,
        severity: 'blocking',
        actionable: true,
        autoResolvable: changeRequests.some((r) => r.actionable),
      });
    }

    // Check for CI failures
    const failedChecks = checks.filter(
      (c) => c.status === 'failure' || (c.status === 'completed' && c.conclusion === 'failure')
    );
    if (failedChecks.length > 0) {
      issues.push({
        type: 'ci_failure',
        description: `${failedChecks.length} CI check(s) failing`,
        severity: 'blocking',
        actionable: true,
        autoResolvable: failedChecks.some((c) => c.retryable),
      });
    }

    // Check for merge conflicts
    if (pr.mergeable === false) {
      issues.push({
        type: 'merge_conflict',
        description: 'Merge conflicts detected',
        severity: 'blocking',
        actionable: true,
        autoResolvable: this.config.mergeConflictResponse.autoRebase,
      });
    }

    // Check for missing approvals
    const requiredApprovals = this.getRequiredApprovals(pr);
    const currentApprovals = reviews.filter((r) => r.state === 'approved').length;
    if (currentApprovals < requiredApprovals) {
      issues.push({
        type: 'approval_required',
        description: `${requiredApprovals - currentApprovals} more approval(s) required`,
        severity: 'blocking',
        actionable: false,
        autoResolvable: false,
      });
    }

    return issues;
  }

  async escalateToHuman(pr: PullRequest, reason: EscalationReason): Promise<void> {
    if (!this.config.escalationEnabled) {
      this.logger.warn('Escalation disabled, skipping', {
        prNumber: pr.number,
        reason: reason.description,
      });
      return;
    }

    this.logger.warn('Escalating PR to human', {
      prNumber: pr.number,
      reason: reason.description,
      severity: reason.severity,
    });

    // Add escalation comment to PR
    await this.gitPlatform.addPRComment(pr.number, {
      body: `🚨 **Human Intervention Required**

**Issue:** ${reason.description}
**Severity:** ${reason.severity.toUpperCase()}
**Type:** ${reason.type.replace('_', ' ').toUpperCase()}

This PR requires human attention to proceed. Please review and take appropriate action.

${reason.deadline ? `**Deadline:** ${reason.deadline.toLocaleString()}` : ''}

---
*This escalation was generated automatically by Tamma*`,
    });

    // Send escalation through configured channels
    for (const channel of this.config.escalationChannels) {
      if (channel.enabled && channel.severity.includes(reason.severity)) {
        await this.escalationManager.sendEscalation(pr, reason, channel);
      }
    }

    await this.eventStore.append({
      type: 'PR.ESCALATION.TRIGGERED',
      tags: {
        prNumber: pr.number.toString(),
        escalationType: reason.type,
        severity: reason.severity,
      },
      data: {
        description: reason.description,
        autoResolvable: reason.autoResolvable,
        deadline: reason.deadline?.toISOString(),
        channels: this.config.escalationChannels
          .filter((c) => c.enabled && c.severity.includes(reason.severity))
          .map((c) => c.type),
      },
    });
  }

  private isReadyForMerge(status: PRStatus): boolean {
    return (
      status.state === 'open' &&
      !status.draft &&
      status.mergeable === true &&
      status.blockingIssues.length === 0 &&
      status.approvalCount >= this.getRequiredApprovals(status)
    );
  }

  private shouldClosePR(pr: PullRequest, status: PRStatus): boolean {
    // Close if stale for too long
    const staleHours = status.timeOpen / (1000 * 60 * 60);
    if (staleHours > this.config.maxMonitoringHours) {
      return true;
    }

    // Close if explicitly requested in comments
    const closeRequests = status.comments.filter(
      (c) =>
        c.body.toLowerCase().includes('close') && (c.author === pr.author || c.type === 'blocker')
    );

    return closeRequests.length > 0;
  }

  private async handleReadyForMerge(pr: PullRequest, status: PRStatus): Promise<void> {
    this.logger.info('PR ready for merge', {
      prNumber: pr.number,
      approvals: status.approvalCount,
      checksPassing: status.checks.every(
        (c) => c.status !== 'failure' && c.conclusion !== 'failure'
      ),
    });

    await this.gitPlatform.addPRComment(pr.number, {
      body: `✅ **Ready for Merge**

This PR has passed all checks and received required approvals:

- ✅ Code reviews: ${status.approvalCount} approvals
- ✅ CI/CD checks: All passing
- ✅ No merge conflicts
- ✅ No blocking issues

${
  this.config.mergeConflictResponse.autoMerge
    ? 'This PR can be merged automatically.'
    : 'This PR is ready to be merged.'
}

---
*This notification was generated automatically by Tamma*`,
    });

    await this.eventStore.append({
      type: 'PR.READY_FOR_MERGE',
      tags: {
        prNumber: pr.number.toString(),
      },
      data: {
        approvals: status.approvalCount,
        checksCount: status.checks.length,
        timeToReady: status.timeOpen,
      },
    });

    // Auto-merge if configured
    if (this.config.mergeConflictResponse.autoMerge) {
      await this.attemptAutoMerge(pr);
    }
  }

  private async attemptAutoMerge(pr: PullRequest): Promise<void> {
    try {
      await this.gitPlatform.mergePullRequest(pr.number, {
        mergeMethod:
          this.config.mergeConflictResponse.conflictResolutionStrategy === 'auto_merge'
            ? 'squash'
            : 'merge',
        commitTitle: `Merge PR #${pr.number}: ${pr.title}`,
        commitMessage: pr.description,
      });

      await this.eventStore.append({
        type: 'PR.AUTO_MERGED',
        tags: {
          prNumber: pr.number.toString(),
        },
        data: {
          mergedAt: new Date().toISOString(),
          mergeMethod: this.config.mergeConflictResponse.conflictResolutionStrategy,
        },
      });

      await this.stopMonitoring(pr.number);
    } catch (error) {
      this.logger.error('Auto-merge failed', {
        prNumber: pr.number,
        error: error.message,
      });

      await this.gitPlatform.addPRComment(pr.number, {
        body: `❌ **Auto-Merge Failed**

Attempted to merge this PR automatically but encountered an error:

\`${error.message}\`

Please merge manually.

---
*This action was performed automatically by Tamma*`,
      });
    }
  }

  private findNewReviews(currentReviews: Review[], session: MonitoringSession): Review[] {
    const previousReviewIds = new Set(Object.keys(session.reviews));
    return currentReviews.filter((review) => !previousReviewIds.has(review.id));
  }

  private findNewComments(currentComments: Comment[], session: MonitoringSession): Comment[] {
    const previousCommentIds = new Set(Object.keys(session.comments));
    return currentComments.filter((comment) => !previousCommentIds.has(comment.id));
  }

  private findChangedChecks(currentChecks: CICDCheck[], session: MonitoringSession): CICDCheck[] {
    const changedChecks: CICDCheck[] = [];

    for (const check of currentChecks) {
      const previousCheck = session.checks[check.id];
      if (!previousCheck || previousCheck.status !== check.status) {
        changedChecks.push(check);
      }
    }

    return changedChecks;
  }

  private isMonitoringTimeout(session: MonitoringSession): boolean {
    const maxAge = this.config.maxMonitoringHours * 60 * 60 * 1000;
    return Date.now() - session.startedAt.getTime() > maxAge;
  }

  private async handleMonitoringTimeout(prNumber: number): Promise<void> {
    await this.escalateToHuman(await this.gitPlatform.getPullRequest(prNumber), {
      type: 'timeout',
      description: `PR monitoring timeout after ${this.config.maxMonitoringHours} hours`,
      severity: 'medium',
      autoResolvable: false,
    });

    await this.stopMonitoring(prNumber);
  }

  private getRequiredApprovals(prOrStatus: PullRequest | PRStatus): number {
    // This could be configured based on repository rules, file changes, etc.
    return 2; // Default to 2 approvals
  }

  private async commitChanges(
    pr: PullRequest,
    changes: FileChange[],
    review: Review
  ): Promise<void> {
    const commitMessage = `Address review feedback from ${review.author}

${review.body.substring(0, 100)}...

---
*This commit was generated automatically by Tamma*`;

    await this.gitPlatform.commitToPR(pr.number, {
      message: commitMessage,
      changes,
      author: 'tamma-bot',
    });
  }
}

interface MonitoringSession {
  prNumber: number;
  startedAt: Date;
  lastCheck: Date;
  status: 'active' | 'stopped';
  lastStatus?: PRStatus;
  checks: Record<string, CICDCheck>;
  reviews: Record<string, Review>;
  comments: Record<string, Comment>;
  escalations: EscalationReason[];
  stoppedAt?: Date;
}
```

### Integration Points

**1. Git Platform Integration**:

- `getPullRequest()` - Get PR details
- `getPRReviews()` - Get review feedback
- `getPRComments()` - Get PR comments
- `getPRChecks()` - Get CI/CD status
- `addPRComment()` - Add comments to PR
- `rerunCheck()` - Retry failed CI checks
- `mergePullRequest()` - Auto-merge PR

**2. CI Analyzer Integration**:

- Analyze CI failures for root causes
- Identify transient vs persistent failures
- Provide remediation suggestions

**3. Review Processor Integration**:

- Analyze review feedback for actionability
- Implement automatic changes where possible
- Generate response to reviewers

**4. Escalation Manager Integration**:

- Send notifications through various channels
- Track escalation history and resolution

### Testing Strategy

**Unit Tests**:

```typescript
describe('PRStatusMonitor', () => {
  let monitor: PRStatusMonitor;
  let mockGitPlatform: jest.Mocked<IGitPlatform>;
  let mockCIAnalyzer: jest.Mocked<ICIAnalyzer>;
  let mockReviewProcessor: jest.Mocked<IReviewProcessor>;
  let mockEscalationManager: jest.Mocked<IEscalationManager>;

  beforeEach(() => {
    mockGitPlatform = createMockGitPlatform();
    mockCIAnalyzer = createMockCIAnalyzer();
    mockReviewProcessor = createMockReviewProcessor();
    mockEscalationManager = createMockEscalationManager();
    monitor = new PRStatusMonitor(
      mockGitPlatform,
      mockCIAnalyzer,
      mockReviewProcessor,
      mockEscalationManager,
      createMockMonitoringConfig(),
      mockLogger,
      mockEventStore
    );
  });

  describe('startMonitoring', () => {
    it('should start monitoring PR', async () => {
      const pr = createMockPullRequest({ number: 123 });

      mockGitPlatform.getPullRequest.mockResolvedValue(pr);
      mockGitPlatform.getPRReviews.mockResolvedValue([]);
      mockGitPlatform.getPRComments.mockResolvedValue([]);
      mockGitPlatform.getPRChecks.mockResolvedValue([]);

      await monitor.startMonitoring(pr);

      expect(mockGitPlatform.getPullRequest).toHaveBeenCalledWith(123);
      // Should have called checkPRStatus during start
    });
  });

  describe('handleReviewFeedback', () => {
    it('should process changes requested review', async () => {
      const pr = createMockPullRequest({ number: 456 });
      const review = createMockReview({
        state: 'changes_requested',
        author: 'reviewer1',
        body: 'Please fix the authentication logic',
      });

      mockReviewProcessor.analyzeReview.mockResolvedValue({
        actionable: true,
        autoImplementable: false,
        complexity: 'medium',
      });

      await monitor.handleReviewFeedback(pr, [review]);

      expect(mockEscalationManager.sendEscalation).toHaveBeenCalledWith(
        pr,
        expect.objectContaining({
          type: 'review_blocking',
          severity: 'medium',
        }),
        expect.any(Object)
      );
    });

    it('should auto-implement simple changes', async () => {
      const pr = createMockPullRequest({ number: 789 });
      const review = createMockReview({
        state: 'changes_requested',
        author: 'reviewer2',
        body: 'Fix typo in variable name',
      });

      mockReviewProcessor.analyzeReview.mockResolvedValue({
        actionable: true,
        autoImplementable: true,
        complexity: 'low',
      });

      mockReviewProcessor.implementChanges.mockResolvedValue({
        success: true,
        changes: [createMockFileChange()],
        summary: 'Fixed typo in variable name',
      });

      mockGitPlatform.commitToPR.mockResolvedValue();

      await monitor.handleReviewFeedback(pr, [review]);

      expect(mockReviewProcessor.implementChanges).toHaveBeenCalled();
      expect(mockGitPlatform.commitToPR).toHaveBeenCalled();
    });
  });

  describe('handleCICDStatus', () => {
    it('should retry transient CI failures', async () => {
      const pr = createMockPullRequest({ number: 101 });
      const check = createMockCICDCheck({
        name: 'tests',
        status: 'failure',
        retryable: true,
        currentRetries: 0,
        maxRetries: 3,
      });

      mockCIAnalyzer.analyzeFailure.mockResolvedValue({
        transient: true,
        critical: false,
        description: 'Network timeout',
        suggestions: ['Retry the check'],
      });

      mockGitPlatform.rerunCheck.mockResolvedValue();

      await monitor.handleCICDStatus(pr, [check]);

      expect(mockGitPlatform.rerunCheck).toHaveBeenCalledWith(101, check.id);
      expect(check.currentRetries).toBe(1);
    });

    it('should escalate persistent CI failures', async () => {
      const pr = createMockPullRequest({ number: 202 });
      const check = createMockCICDCheck({
        name: 'build',
        status: 'failure',
        retryable: false,
        currentRetries: 0,
        maxRetries: 0,
      });

      mockCIAnalyzer.analyzeFailure.mockResolvedValue({
        transient: false,
        critical: true,
        description: 'Compilation error',
        suggestions: ['Fix syntax errors'],
      });

      await monitor.handleCICDStatus(pr, [check]);

      expect(mockEscalationManager.sendEscalation).toHaveBeenCalledWith(
        pr,
        expect.objectContaining({
          type: 'ci_failure',
          severity: 'high',
        }),
        expect.any(Object)
      );
    });
  });
});
```

### Configuration Examples

**PR Monitoring Configuration**:

```yaml
pr_monitoring:
  enabled: true
  check_interval_seconds: 60
  max_monitoring_hours: 24
  auto_retry_ci: true
  max_ci_retries: 3
  escalation_enabled: true

  escalation_channels:
    - type: 'slack'
      enabled: true
      config:
        webhook_url: '${SLACK_WEBHOOK_URL}'
        channel: '#pr-alerts'
      severity: ['high', 'medium']

    - type: 'email'
      enabled: true
      config:
        recipients: ['dev-team@company.com']
        template: 'pr-escalation'
      severity: ['high']

    - type: 'webhook'
      enabled: true
      config:
        url: '${ESCALATION_WEBHOOK_URL}'
        headers:
          Authorization: 'Bearer ${ESCALATION_TOKEN}'
      severity: ['high', 'medium', 'low']

  review_response:
    auto_respond_to_suggestions: true
    auto_implement_changes: true
    max_change_complexity: 'medium'
    response_delay_minutes: 5
    acknowledge_receipt: true

  ci_failure_response:
    auto_retry_transient_failures: true
    retryable_patterns:
      - 'timeout'
      - 'network'
      - 'rate limit'
      - 'infrastructure'
    max_retries_per_check: 3
    retry_delay_minutes: 5
    escalate_on_persistent_failure: true
    failure_analysis_enabled: true

  merge_conflict_response:
    auto_rebase: true
    auto_merge: false
    conflict_resolution_strategy: 'manual' # manual, auto_rebase, auto_merge, escalate
    max_rebase_attempts: 3
    escalate_on_conflict: true
```

---

## Implementation Notes

**Key Considerations**:

1. **Real-time Monitoring**: Balance monitoring frequency with API rate limits.

2. **Intelligent Response**: Differentiate between actionable and non-actionable feedback.

3. **Escalation Strategy**: Clear escalation paths with appropriate severity levels.

4. **Auto-retry Logic**: Smart retry for transient failures without infinite loops.

5. **Human-in-the-Loop**: Know when to escalate vs. when to handle automatically.

6. **Audit Trail**: Complete logging of all monitoring actions and decisions.

**Performance Targets**:

- Status check: < 5 seconds
- Review processing: < 10 seconds
- CI failure analysis: < 15 seconds
- Escalation notification: < 5 seconds

**Security Considerations**:

- Validate PR comments for malicious content
- Handle sensitive information in CI logs appropriately
- Secure escalation channels and webhooks
- Proper authentication for all PR operations
- Rate limiting to avoid abuse

**Monitoring Best Practices**:

- Proactive issue detection and resolution
- Clear communication with human reviewers
- Respect human working hours and availability
- Provide context and actionable information
- Track and learn from escalation patterns
- Maintain comprehensive audit trails

### References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md) - Search spikes, bugs, findings, decisions
