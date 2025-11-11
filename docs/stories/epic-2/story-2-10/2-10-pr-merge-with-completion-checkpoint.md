# Story 2.10: PR Merge with Completion Checkpoint

**Epic**: Epic 2 - Autonomous Development Loop - Core  
**Status**: Ready for Development  
**Priority**: High  
**Prerequisites**: Story 2.9 (PR status monitoring must complete first)

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
I want the system to merge approved pull requests and close associated issues,
So that the autonomous development loop can complete and continue to the next issue.

---

## Acceptance Criteria

1. System merges PR when all requirements are met (approvals, checks, no conflicts)
2. System validates merge was successful and branch is cleaned up
3. System closes the associated issue with completion comment
4. System performs completion checkpoint validation
5. System logs merge, cleanup, and completion to event trail
6. System triggers next issue selection for continuous operation
7. Integration test validates merge and completion workflow
8. Error handling for merge failures, permission issues, and cleanup failures

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

**PRMergeManager Service**:

```typescript
interface IPRMergeManager {
  mergePR(pr: PullRequest, strategy?: MergeStrategy): Promise<MergeResult>;
  validateMergeRequirements(pr: PullRequest): Promise<MergeValidation>;
  cleanupAfterMerge(pr: PullRequest): Promise<CleanupResult>;
  closeAssociatedIssue(pr: PullRequest, mergeResult: MergeResult): Promise<void>;
  performCompletionCheckpoint(pr: PullRequest, mergeResult: MergeResult): Promise<CompletionResult>;
  triggerNextIssueSelection(): Promise<void>;
}

interface MergeResult {
  success: boolean;
  mergedAt: Date;
  mergeCommitSha: string;
  mergeMethod: MergeStrategy;
  mergedBy: string;
  changes: MergeChanges;
  postMergeActions: PostMergeAction[];
  errors: MergeError[];
}

interface MergeValidation {
  canMerge: boolean;
  requirements: MergeRequirement[];
  blockingIssues: BlockingIssue[];
  warnings: ValidationWarning[];
  readyAt?: Date;
  estimatedWaitTime?: number;
}

interface MergeRequirement {
  type: 'approvals' | 'ci_checks' | 'merge_conflicts' | 'branch_protection' | 'policy_compliance';
  status: 'satisfied' | 'pending' | 'failed' | 'unknown';
  description: string;
  required: boolean;
  currentValue?: any;
  targetValue?: any;
  lastChecked: Date;
}

interface MergeChanges {
  commitsAdded: number;
  filesChanged: number;
  linesAdded: number;
  linesDeleted: number;
  binaryFiles: number;
  fileTypes: Record<string, number>;
  contributors: string[];
}

interface PostMergeAction {
  type: 'branch_delete' | 'issue_close' | 'deployment_trigger' | 'notification_send' | 'cleanup';
  status: 'pending' | 'completed' | 'failed';
  startedAt?: Date;
  completedAt?: Date;
  error?: string;
  metadata: Record<string, any>;
}

interface MergeError {
  type:
    | 'permission_denied'
    | 'merge_conflict'
    | 'branch_protected'
    | 'ci_pending'
    | 'api_error'
    | 'policy_violation';
  message: string;
  retryable: boolean;
  suggestedAction: string;
  context?: Record<string, any>;
}

interface CleanupResult {
  branchDeleted: boolean;
  referencesRemoved: boolean;
  artifactsCleaned: boolean;
  actions: CleanupAction[];
  errors: CleanupError[];
}

interface CleanupAction {
  type: 'delete_branch' | 'remove_references' | 'clean_artifacts' | 'update_status';
  status: 'completed' | 'failed' | 'skipped';
  description: string;
  duration: number;
}

interface CleanupError {
  action: string;
  error: string;
  retryable: boolean;
  critical: boolean;
}

interface CompletionResult {
  success: boolean;
  completedAt: Date;
  issueClosed: boolean;
  branchCleaned: boolean;
  deploymentTriggered: boolean;
  notificationsSent: boolean;
  nextIssueTriggered: boolean;
  metrics: CompletionMetrics;
  errors: CompletionError[];
}

interface CompletionMetrics {
  totalCycleTime: number;
  mergeTime: number;
  cleanupTime: number;
  issueCloseTime: number;
  totalActions: number;
  successfulActions: number;
  failedActions: number;
}

interface CompletionError {
  type:
    | 'merge_failed'
    | 'cleanup_failed'
    | 'issue_close_failed'
    | 'deployment_failed'
    | 'notification_failed';
  message: string;
  critical: boolean;
  retryable: boolean;
  context?: Record<string, any>;
}

type MergeStrategy = 'merge' | 'squash' | 'rebase';

interface MergeConfig {
  defaultStrategy: MergeStrategy;
  requireAllChecksPass: boolean;
  requireMinApprovals: number;
  requireCodeOwnerApproval: boolean;
  autoDeleteBranch: boolean;
  closeAssociatedIssues: boolean;
  triggerDeployment: boolean;
  sendNotifications: boolean;
  completionCheckpoint: CompletionCheckpointConfig;
  postMergeActions: PostMergeActionConfig[];
}

interface CompletionCheckpointConfig {
  enabled: boolean;
  validateDeployment: boolean;
  validateIssueClosure: boolean;
  validateBranchCleanup: boolean;
  validateNotifications: boolean;
  timeoutMinutes: number;
  rollbackOnFailure: boolean;
}

interface PostMergeActionConfig {
  type: string;
  enabled: boolean;
  order: number;
  config: Record<string, any>;
  retryAttempts: number;
  critical: boolean;
}
```

### Implementation Strategy

**1. PR Merge Engine**:

```typescript
class PRMergeManager implements IPRMergeManager {
  constructor(
    private gitPlatform: IGitPlatform,
    private deploymentManager: IDeploymentManager,
    private notificationManager: INotificationManager,
    private issueManager: IIssueManager,
    private config: MergeConfig,
    private logger: Logger,
    private eventStore: IEventStore
  ) {}

  async mergePR(
    pr: PullRequest,
    strategy: MergeStrategy = this.config.defaultStrategy
  ): Promise<MergeResult> {
    const startTime = Date.now();

    try {
      // Validate merge requirements
      const validation = await this.validateMergeRequirements(pr);

      if (!validation.canMerge) {
        throw new MergeError(
          'PR does not meet merge requirements',
          'requirements_not_met',
          false,
          this.generateRequirementMessage(validation)
        );
      }

      // Wait for any pending requirements
      if (validation.readyAt && validation.readyAt > new Date()) {
        await this.waitForMergeReadiness(validation);
      }

      // Perform merge
      const mergeResult = await this.performMerge(pr, strategy);

      // Execute post-merge actions
      await this.executePostMergeActions(pr, mergeResult);

      // Perform completion checkpoint
      const completionResult = await this.performCompletionCheckpoint(pr, mergeResult);

      // Trigger next issue selection
      if (completionResult.success) {
        await this.triggerNextIssueSelection();
      }

      const finalResult: MergeResult = {
        ...mergeResult,
        postMergeActions: completionResult.success
          ? this.generatePostMergeActions(completionResult)
          : [],
      };

      await this.eventStore.append({
        type: 'PR.MERGE.SUCCESS',
        tags: {
          prNumber: pr.number.toString(),
          issueNumber: pr.metadata?.issueNumber || 'unknown',
          mergeStrategy: strategy,
        },
        data: {
          mergedAt: finalResult.mergedAt.toISOString(),
          mergeCommitSha: finalResult.mergeCommitSha,
          changes: finalResult.changes,
          postMergeActions: finalResult.postMergeActions.length,
          totalMergeTime: Date.now() - startTime,
          completionSuccess: completionResult.success,
        },
      });

      this.logger.info('PR merged successfully', {
        prNumber: pr.number,
        mergeStrategy,
        mergeCommitSha: finalResult.mergeCommitSha,
        totalActions: finalResult.postMergeActions.length,
      });

      return finalResult;
    } catch (error) {
      await this.eventStore.append({
        type: 'PR.MERGE.FAILED',
        tags: {
          prNumber: pr.number.toString(),
          issueNumber: pr.metadata?.issueNumber || 'unknown',
        },
        data: {
          error: error.message,
          mergeTime: Date.now() - startTime,
          strategy,
        },
      });

      throw error;
    }
  }

  async validateMergeRequirements(pr: PullRequest): Promise<MergeValidation> {
    const requirements: MergeRequirement[] = [];
    const blockingIssues: BlockingIssue[] = [];
    const warnings: ValidationWarning[] = [];

    try {
      // Check approvals
      const approvalRequirement = await this.checkApprovalRequirement(pr);
      requirements.push(approvalRequirement);

      if (approvalRequirement.status === 'failed') {
        blockingIssues.push({
          type: 'approval_required',
          description: approvalRequirement.description,
          severity: 'blocking',
          actionable: true,
          autoResolvable: false,
        });
      }

      // Check CI/CD status
      const ciRequirement = await this.checkCIRequirement(pr);
      requirements.push(ciRequirement);

      if (ciRequirement.status === 'failed') {
        blockingIssues.push({
          type: 'ci_failure',
          description: ciRequirement.description,
          severity: 'blocking',
          actionable: true,
          autoResolvable: false,
        });
      }

      // Check merge conflicts
      const conflictRequirement = await this.checkConflictRequirement(pr);
      requirements.push(conflictRequirement);

      if (conflictRequirement.status === 'failed') {
        blockingIssues.push({
          type: 'merge_conflict',
          description: conflictRequirement.description,
          severity: 'blocking',
          actionable: true,
          autoResolvable: true,
        });
      }

      // Check branch protection
      const protectionRequirement = await this.checkBranchProtectionRequirement(pr);
      requirements.push(protectionRequirement);

      if (protectionRequirement.status === 'failed') {
        blockingIssues.push({
          type: 'branch_protected',
          description: protectionRequirement.description,
          severity: 'blocking',
          actionable: false,
          autoResolvable: false,
        });
      }

      // Check policy compliance
      const policyRequirement = await this.checkPolicyComplianceRequirement(pr);
      requirements.push(policyRequirement);

      if (policyRequirement.status === 'failed') {
        blockingIssues.push({
          type: 'policy_violation',
          description: policyRequirement.description,
          severity: 'blocking',
          actionable: false,
          autoResolvable: false,
        });
      }

      const canMerge =
        blockingIssues.length === 0 &&
        requirements.every((req) => req.status === 'satisfied' || !req.required);

      // Calculate readiness time
      let readyAt: Date | undefined;
      let estimatedWaitTime: number | undefined;

      if (!canMerge) {
        const pendingRequirements = requirements.filter((req) => req.status === 'pending');
        if (pendingRequirements.length > 0) {
          const maxWaitTime = Math.max(
            ...pendingRequirements.map((req) => this.estimateRequirementWaitTime(req))
          );
          readyAt = new Date(Date.now() + maxWaitTime);
          estimatedWaitTime = maxWaitTime;
        }
      }

      return {
        canMerge,
        requirements,
        blockingIssues,
        warnings,
        readyAt,
        estimatedWaitTime,
      };
    } catch (error) {
      this.logger.error('Failed to validate merge requirements', {
        prNumber: pr.number,
        error: error.message,
      });

      return {
        canMerge: false,
        requirements: [],
        blockingIssues: [
          {
            type: 'api_error',
            description: `Validation failed: ${error.message}`,
            severity: 'blocking',
            actionable: false,
            autoResolvable: true,
          },
        ],
        warnings: [],
      };
    }
  }

  private async checkApprovalRequirement(pr: PullRequest): Promise<MergeRequirement> {
    const reviews = await this.gitPlatform.getPRReviews(pr.number);
    const approvals = reviews.filter((r) => r.state === 'approved');
    const changesRequested = reviews.filter((r) => r.state === 'changes_requested');

    const requiredApprovals = this.config.requireMinApprovals;
    const hasRequiredApprovals = approvals.length >= requiredApprovals;
    const hasBlockingChanges = changesRequested.length > 0;

    let status: 'satisfied' | 'pending' | 'failed' | 'unknown';
    let description: string;

    if (hasBlockingChanges) {
      status = 'failed';
      description = `${changesRequested.length} review(s) requesting changes`;
    } else if (hasRequiredApprovals) {
      status = 'satisfied';
      description = `${approvals.length}/${requiredApprovals} required approvals received`;
    } else {
      status = 'pending';
      description = `${approvals.length}/${requiredApprovals} required approvals received`;
    }

    return {
      type: 'approvals',
      status,
      description,
      required: true,
      currentValue: approvals.length,
      targetValue: requiredApprovals,
      lastChecked: new Date(),
    };
  }

  private async checkCIRequirement(pr: PullRequest): Promise<MergeRequirement> {
    const checks = await this.gitPlatform.getPRChecks(pr.number);

    if (checks.length === 0) {
      return {
        type: 'ci_checks',
        status: 'unknown',
        description: 'No CI/CD checks found',
        required: this.config.requireAllChecksPass,
        lastChecked: new Date(),
      };
    }

    const completedChecks = checks.filter((c) => c.status === 'completed');
    const pendingChecks = checks.filter((c) => c.status === 'in_progress' || c.status === 'queued');
    const failedChecks = completedChecks.filter((c) => c.conclusion === 'failure');

    let status: 'satisfied' | 'pending' | 'failed';
    let description: string;

    if (failedChecks.length > 0) {
      status = 'failed';
      description = `${failedChecks.length} CI/CD check(s) failed: ${failedChecks.map((c) => c.name).join(', ')}`;
    } else if (pendingChecks.length > 0) {
      status = 'pending';
      description = `${pendingChecks.length} CI/CD check(s) pending: ${pendingChecks.map((c) => c.name).join(', ')}`;
    } else {
      status = 'satisfied';
      description = `All ${completedChecks.length} CI/CD checks passed`;
    }

    return {
      type: 'ci_checks',
      status,
      description,
      required: this.config.requireAllChecksPass,
      currentValue: completedChecks.length,
      targetValue: checks.length,
      lastChecked: new Date(),
    };
  }

  private async checkConflictRequirement(pr: PullRequest): Promise<MergeRequirement> {
    // Most platforms provide mergeable status
    const prDetails = await this.gitPlatform.getPullRequest(pr.number);

    let status: 'satisfied' | 'failed' | 'unknown';
    let description: string;

    if (prDetails.mergeable === true) {
      status = 'satisfied';
      description = 'No merge conflicts detected';
    } else if (prDetails.mergeable === false) {
      status = 'failed';
      description = 'Merge conflicts detected - rebase may be required';
    } else {
      status = 'unknown';
      description = 'Merge conflict status unknown - checking...';
    }

    return {
      type: 'merge_conflicts',
      status,
      description,
      required: true,
      lastChecked: new Date(),
    };
  }

  private async performMerge(pr: PullRequest, strategy: MergeStrategy): Promise<MergeResult> {
    try {
      const mergeRequest = {
        mergeMethod: strategy,
        commitTitle: this.generateMergeCommitTitle(pr),
        commitMessage: this.generateMergeCommitMessage(pr),
      };

      const mergeResult = await this.gitPlatform.mergePullRequest(pr.number, mergeRequest);

      // Get merge details
      const mergedPR = await this.gitPlatform.getPullRequest(pr.number);
      const changes = await this.calculateMergeChanges(pr);

      return {
        success: true,
        mergedAt: new Date(),
        mergeCommitSha: mergeResult.sha,
        mergeMethod: strategy,
        mergedBy: 'tamma-bot',
        changes,
        postMergeActions: [],
        errors: [],
      };
    } catch (error) {
      const mergeError = this.parseMergeError(error);

      return {
        success: false,
        mergedAt: new Date(),
        mergeCommitSha: '',
        mergeMethod: strategy,
        mergedBy: 'tamma-bot',
        changes: {
          commitsAdded: 0,
          filesChanged: 0,
          linesAdded: 0,
          linesDeleted: 0,
          binaryFiles: 0,
          fileTypes: {},
          contributors: [],
        },
        postMergeActions: [],
        errors: [mergeError],
      };
    }
  }

  private async executePostMergeActions(pr: PullRequest, mergeResult: MergeResult): Promise<void> {
    const actions = this.config.postMergeActions
      .filter((action) => action.enabled)
      .sort((a, b) => a.order - b.order);

    for (const actionConfig of actions) {
      try {
        await this.executePostMergeAction(pr, mergeResult, actionConfig);
      } catch (error) {
        this.logger.error('Post-merge action failed', {
          prNumber: pr.number,
          action: actionConfig.type,
          error: error.message,
          critical: actionConfig.critical,
        });

        if (actionConfig.critical) {
          throw error; // Stop execution for critical failures
        }
      }
    }
  }

  private async executePostMergeAction(
    pr: PullRequest,
    mergeResult: MergeResult,
    actionConfig: PostMergeActionConfig
  ): Promise<void> {
    switch (actionConfig.type) {
      case 'branch_delete':
        await this.deleteBranch(pr, actionConfig);
        break;

      case 'issue_close':
        await this.closeAssociatedIssue(pr, mergeResult);
        break;

      case 'deployment_trigger':
        await this.triggerDeployment(pr, mergeResult, actionConfig);
        break;

      case 'notification_send':
        await this.sendMergeNotification(pr, mergeResult, actionConfig);
        break;

      case 'cleanup':
        await this.performCleanup(pr, mergeResult, actionConfig);
        break;

      default:
        this.logger.warn('Unknown post-merge action', { type: actionConfig.type });
    }
  }

  async cleanupAfterMerge(pr: PullRequest): Promise<CleanupResult> {
    const startTime = Date.now();
    const actions: CleanupAction[] = [];
    const errors: CleanupError[] = [];

    try {
      // Delete feature branch
      if (this.config.autoDeleteBranch) {
        const deleteAction = await this.deleteBranchAction(pr);
        actions.push(deleteAction);

        if (deleteAction.status === 'failed') {
          errors.push({
            action: 'delete_branch',
            error: 'Branch deletion failed',
            retryable: true,
            critical: false,
          });
        }
      }

      // Remove references and clean up artifacts
      const cleanupAction = await this.cleanupArtifactsAction(pr);
      actions.push(cleanupAction);

      // Update status and close related items
      const statusAction = await this.updateStatusAction(pr);
      actions.push(statusAction);

      return {
        branchDeleted: actions.some((a) => a.type === 'delete_branch' && a.status === 'completed'),
        referencesRemoved: actions.some(
          (a) => a.type === 'remove_references' && a.status === 'completed'
        ),
        artifactsCleaned: actions.some(
          (a) => a.type === 'clean_artifacts' && a.status === 'completed'
        ),
        actions,
        errors,
      };
    } catch (error) {
      this.logger.error('Cleanup failed', {
        prNumber: pr.number,
        error: error.message,
      });

      return {
        branchDeleted: false,
        referencesRemoved: false,
        artifactsCleaned: false,
        actions,
        errors: [
          {
            action: 'general_cleanup',
            error: error.message,
            retryable: true,
            critical: false,
          },
        ],
      };
    }
  }

  async closeAssociatedIssue(pr: PullRequest, mergeResult: MergeResult): Promise<void> {
    if (!this.config.closeAssociatedIssues || !pr.metadata?.issueNumber) {
      return;
    }

    try {
      const issueNumber = pr.metadata.issueNumber;

      // Create closing comment
      const closeComment = this.generateIssueCloseComment(pr, mergeResult);
      await this.gitPlatform.addIssueComment(issueNumber, closeComment);

      // Close the issue
      await this.gitPlatform.closeIssue(issueNumber, {
        comment: 'Closed via merged PR',
        stateReason: 'completed',
      });

      await this.eventStore.append({
        type: 'ISSUE.CLOSED.SUCCESS',
        tags: {
          issueNumber: issueNumber.toString(),
          prNumber: pr.number.toString(),
        },
        data: {
          closedAt: new Date().toISOString(),
          closeComment: closeComment.substring(0, 100) + '...',
          mergeCommitSha: mergeResult.mergeCommitSha,
        },
      });

      this.logger.info('Associated issue closed', {
        issueNumber,
        prNumber: pr.number,
      });
    } catch (error) {
      await this.eventStore.append({
        type: 'ISSUE.CLOSED.FAILED',
        tags: {
          issueNumber: pr.metadata?.issueNumber?.toString() || 'unknown',
          prNumber: pr.number.toString(),
        },
        data: {
          error: error.message,
        },
      });

      throw new Error(`Failed to close associated issue: ${error.message}`);
    }
  }

  async performCompletionCheckpoint(
    pr: PullRequest,
    mergeResult: MergeResult
  ): Promise<CompletionResult> {
    const startTime = Date.now();
    const errors: CompletionError[] = [];
    let success = true;

    try {
      const checkpoint = this.config.completionCheckpoint;

      if (!checkpoint.enabled) {
        return {
          success: true,
          completedAt: new Date(),
          issueClosed: false,
          branchCleaned: false,
          deploymentTriggered: false,
          notificationsSent: false,
          nextIssueTriggered: false,
          metrics: this.calculateCompletionMetrics(startTime, [], errors),
          errors,
        };
      }

      // Validate deployment if required
      let deploymentTriggered = false;
      if (checkpoint.validateDeployment) {
        try {
          deploymentTriggered = await this.validateDeployment(pr, mergeResult);
        } catch (error) {
          errors.push({
            type: 'deployment_failed',
            message: `Deployment validation failed: ${error.message}`,
            critical: true,
            retryable: true,
          });
          success = false;
        }
      }

      // Validate issue closure if required
      let issueClosed = false;
      if (checkpoint.validateIssueClosure) {
        try {
          issueClosed = await this.validateIssueClosure(pr);
        } catch (error) {
          errors.push({
            type: 'issue_close_failed',
            message: `Issue closure validation failed: ${error.message}`,
            critical: false,
            retryable: true,
          });
          success = false;
        }
      }

      // Validate branch cleanup if required
      let branchCleaned = false;
      if (checkpoint.validateBranchCleanup) {
        try {
          branchCleaned = await this.validateBranchCleanup(pr);
        } catch (error) {
          errors.push({
            type: 'cleanup_failed',
            message: `Branch cleanup validation failed: ${error.message}`,
            critical: false,
            retryable: true,
          });
          success = false;
        }
      }

      // Validate notifications if required
      let notificationsSent = false;
      if (checkpoint.validateNotifications) {
        try {
          notificationsSent = await this.validateNotifications(pr);
        } catch (error) {
          errors.push({
            type: 'notification_failed',
            message: `Notification validation failed: ${error.message}`,
            critical: false,
            retryable: true,
          });
          success = false;
        }
      }

      // Rollback on failure if configured
      if (!success && checkpoint.rollbackOnFailure) {
        await this.performRollback(pr, mergeResult, errors);
      }

      const metrics = this.calculateCompletionMetrics(startTime, [], errors);

      return {
        success,
        completedAt: new Date(),
        issueClosed,
        branchCleaned,
        deploymentTriggered,
        notificationsSent,
        nextIssueTriggered: false, // Will be set separately
        metrics,
        errors,
      };
    } catch (error) {
      return {
        success: false,
        completedAt: new Date(),
        issueClosed: false,
        branchCleaned: false,
        deploymentTriggered: false,
        notificationsSent: false,
        nextIssueTriggered: false,
        metrics: this.calculateCompletionMetrics(
          startTime,
          [],
          [
            {
              type: 'merge_failed',
              message: error.message,
              critical: true,
              retryable: false,
            },
          ]
        ),
        errors: [
          {
            type: 'merge_failed',
            message: error.message,
            critical: true,
            retryable: false,
          },
        ],
      };
    }
  }

  async triggerNextIssueSelection(): Promise<void> {
    try {
      // Emit event to trigger next issue selection
      await this.eventStore.append({
        type: 'WORKFLOW.NEXT_ISSUE.TRIGGERED',
        tags: {},
        data: {
          triggeredAt: new Date().toISOString(),
          triggerReason: 'pr_merge_completed',
        },
      });

      this.logger.info('Next issue selection triggered');
    } catch (error) {
      this.logger.error('Failed to trigger next issue selection', {
        error: error.message,
      });
    }
  }

  private generateMergeCommitTitle(pr: PullRequest): string {
    // Include issue number and PR title
    const issueNumber = pr.metadata?.issueNumber;
    if (issueNumber) {
      return `#${issueNumber} ${pr.title.replace(/^#\d+\s*/, '')}`;
    }
    return pr.title;
  }

  private generateMergeCommitMessage(pr: PullRequest): string {
    const issueNumber = pr.metadata?.issueNumber;
    let message = pr.description || pr.title;

    if (issueNumber) {
      message += `\n\nCloses #${issueNumber}`;
    }

    message += `\n\n---\n🤖 Auto-merged by Tamma`;
    message += `\nMerge Strategy: ${this.config.defaultStrategy}`;
    message += `\nMerged at: ${new Date().toISOString()}`;

    return message;
  }

  private generateIssueCloseComment(pr: PullRequest, mergeResult: MergeResult): string {
    const issueNumber = pr.metadata?.issueNumber;

    return `✅ **Issue Resolved**

This issue has been resolved and merged via PR #${pr.number}.

**Merge Details:**
- **Commit:** ${mergeResult.mergeCommitSha}
- **Merged:** ${mergeResult.mergedAt.toLocaleString()}
- **Strategy:** ${mergeResult.mergeMethod}
- **Changes:** ${mergeResult.changes.filesChanged} files, ${mergeResult.changes.linesAdded} additions, ${mergeResult.changes.linesDeleted} deletions

**Implementation Summary:**
${pr.description.substring(0, 500)}${pr.description.length > 500 ? '...' : ''}

Thank you for your contribution! 🎉

---
*This issue was automatically closed by Tamma after successful merge*`;
  }

  private parseMergeError(error: any): MergeError {
    const message = error.message || 'Unknown merge error';

    if (message.includes('permission') || message.includes('unauthorized')) {
      return {
        type: 'permission_denied',
        message,
        retryable: false,
        suggestedAction: 'Check merge permissions and try again',
      };
    }

    if (message.includes('conflict')) {
      return {
        type: 'merge_conflict',
        message,
        retryable: true,
        suggestedAction: 'Resolve merge conflicts and try again',
      };
    }

    if (message.includes('protected') || message.includes('policy')) {
      return {
        type: 'branch_protected',
        message,
        retryable: false,
        suggestedAction: 'Check branch protection rules and required status checks',
      };
    }

    if (message.includes('pending') || message.includes('waiting')) {
      return {
        type: 'ci_pending',
        message,
        retryable: true,
        suggestedAction: 'Wait for CI/CD checks to complete',
      };
    }

    return {
      type: 'api_error',
      message,
      retryable: true,
      suggestedAction: 'Check API status and retry',
    };
  }

  private calculateCompletionMetrics(
    startTime: number,
    actions: CleanupAction[],
    errors: CompletionError[]
  ): CompletionMetrics {
    const totalTime = Date.now() - startTime;

    return {
      totalCycleTime: totalTime,
      mergeTime: 0, // Would be calculated from merge operation
      cleanupTime: actions.reduce((sum, action) => sum + action.duration, 0),
      issueCloseTime: 0, // Would be calculated from issue close operation
      totalActions: actions.length,
      successfulActions: actions.filter((a) => a.status === 'completed').length,
      failedActions: actions.filter((a) => a.status === 'failed').length,
    };
  }

  private async waitForMergeReadiness(validation: MergeValidation): Promise<void> {
    if (!validation.readyAt) {
      return;
    }

    const waitTime = validation.readyAt.getTime() - Date.now();
    if (waitTime <= 0) {
      return;
    }

    this.logger.info('Waiting for merge readiness', {
      waitTime: Math.round(waitTime / 1000 / 60), // minutes
      readyAt: validation.readyAt.toISOString(),
    });

    // Wait and periodically re-check
    const checkInterval = 30000; // 30 seconds
    const maxWait = Math.min(waitTime, this.config.completionCheckpoint.timeoutMinutes * 60 * 1000);

    await new Promise((resolve) => {
      const timeout = setTimeout(resolve, maxWait);
      const interval = setInterval(async () => {
        // Re-check validation (would need PR number)
        clearInterval(interval);
        clearTimeout(timeout);
        resolve();
      }, checkInterval);
    });
  }
}
```

### Integration Points

**1. Git Platform Integration**:

- `mergePullRequest()` - Perform the merge
- `getPullRequest()` - Get PR details and status
- `deleteBranch()` - Clean up feature branch
- `closeIssue()` - Close associated issue
- `addIssueComment()` - Add closing comment

**2. Deployment Manager Integration**:

- Trigger deployment after merge
- Validate deployment success
- Handle deployment failures

**3. Notification Manager Integration**:

- Send merge notifications
- Notify stakeholders
- Handle notification failures

**4. Event Store Integration**:

- `PR.MERGE.SUCCESS/FAILED`
- `ISSUE.CLOSED.SUCCESS/FAILED`
- `WORKFLOW.NEXT_ISSUE.TRIGGERED`
- Complete audit trail

### Testing Strategy

**Unit Tests**:

```typescript
describe('PRMergeManager', () => {
  let mergeManager: PRMergeManager;
  let mockGitPlatform: jest.Mocked<IGitPlatform>;
  let mockDeploymentManager: jest.Mocked<IDeploymentManager>;
  let mockNotificationManager: jest.Mocked<INotificationManager>;

  beforeEach(() => {
    mockGitPlatform = createMockGitPlatform();
    mockDeploymentManager = createMockDeploymentManager();
    mockNotificationManager = createMockNotificationManager();
    mergeManager = new PRMergeManager(
      mockGitPlatform,
      mockDeploymentManager,
      mockNotificationManager,
      mockIssueManager,
      createMockMergeConfig(),
      mockLogger,
      mockEventStore
    );
  });

  describe('mergePR', () => {
    it('should merge PR when all requirements met', async () => {
      const pr = createMockPullRequest({
        number: 123,
        metadata: { issueNumber: 456 },
      });

      mockGitPlatform.getPRReviews.mockResolvedValue([
        createMockReview({ state: 'approved' }),
        createMockReview({ state: 'approved' }),
      ]);

      mockGitPlatform.getPRChecks.mockResolvedValue([
        createMockCheck({ status: 'completed', conclusion: 'success' }),
      ]);

      mockGitPlatform.getPullRequest.mockResolvedValue({
        ...pr,
        mergeable: true,
      });

      mockGitPlatform.mergePullRequest.mockResolvedValue({
        sha: 'abc123',
        merged: true,
      });

      mockGitPlatform.closeIssue.mockResolvedValue();
      mockGitPlatform.addIssueComment.mockResolvedValue();

      const result = await mergeManager.mergePR(pr);

      expect(result.success).toBe(true);
      expect(result.mergeCommitSha).toBe('abc123');
      expect(mockGitPlatform.mergePullRequest).toHaveBeenCalledWith(123, expect.any(Object));
      expect(mockGitPlatform.closeIssue).toHaveBeenCalledWith(456, expect.any(Object));
    });

    it('should fail merge when requirements not met', async () => {
      const pr = createMockPullRequest({ number: 789 });

      mockGitPlatform.getPRReviews.mockResolvedValue([
        createMockReview({ state: 'approved' }), // Only 1 approval, need 2
      ]);

      mockGitPlatform.getPRChecks.mockResolvedValue([
        createMockCheck({ status: 'completed', conclusion: 'failure' }), // Failed check
      ]);

      await expect(mergeManager.mergePR(pr)).rejects.toThrow('does not meet merge requirements');
    });

    it('should handle merge conflicts gracefully', async () => {
      const pr = createMockPullRequest({ number: 101 });

      mockGitPlatform.getPRReviews.mockResolvedValue([
        createMockReview({ state: 'approved' }),
        createMockReview({ state: 'approved' }),
      ]);

      mockGitPlatform.getPRChecks.mockResolvedValue([
        createMockCheck({ status: 'completed', conclusion: 'success' }),
      ]);

      mockGitPlatform.getPullRequest.mockResolvedValue({
        ...pr,
        mergeable: false,
      });

      const validation = await mergeManager.validateMergeRequirements(pr);

      expect(validation.canMerge).toBe(false);
      expect(validation.blockingIssues).toContainEqual(
        expect.objectContaining({
          type: 'merge_conflict',
        })
      );
    });
  });

  describe('validateMergeRequirements', () => {
    it('should validate all merge requirements', async () => {
      const pr = createMockPullRequest({ number: 202 });

      mockGitPlatform.getPRReviews.mockResolvedValue([
        createMockReview({ state: 'approved' }),
        createMockReview({ state: 'approved' }),
        createMockReview({ state: 'changes_requested' }),
      ]);

      mockGitPlatform.getPRChecks.mockResolvedValue([
        createMockCheck({ status: 'completed', conclusion: 'success' }),
        createMockCheck({ status: 'in_progress' }),
      ]);

      const validation = await mergeManager.validateMergeRequirements(pr);

      expect(validation.requirements).toHaveLength(5); // approvals, ci, conflicts, protection, policy
      expect(validation.blockingIssues).toContainEqual(
        expect.objectContaining({
          type: 'approval_required',
        })
      );
      expect(validation.canMerge).toBe(false);
    });
  });

  describe('performCompletionCheckpoint', () => {
    it('should validate completion checkpoint', async () => {
      const pr = createMockPullRequest({
        number: 303,
        metadata: { issueNumber: 404 },
      });

      const mergeResult = createMockMergeResult();

      const config = createMockMergeConfig({
        completionCheckpoint: {
          enabled: true,
          validateDeployment: true,
          validateIssueClosure: true,
          validateBranchCleanup: true,
          validateNotifications: true,
          timeoutMinutes: 30,
          rollbackOnFailure: false,
        },
      });

      mergeManager = new PRMergeManager(
        mockGitPlatform,
        mockDeploymentManager,
        mockNotificationManager,
        mockIssueManager,
        config,
        mockLogger,
        mockEventStore
      );

      // Mock successful validations
      jest.spyOn(mergeManager as any, 'validateDeployment').mockResolvedValue(true);
      jest.spyOn(mergeManager as any, 'validateIssueClosure').mockResolvedValue(true);
      jest.spyOn(mergeManager as any, 'validateBranchCleanup').mockResolvedValue(true);
      jest.spyOn(mergeManager as any, 'validateNotifications').mockResolvedValue(true);

      const result = await mergeManager.performCompletionCheckpoint(pr, mergeResult);

      expect(result.success).toBe(true);
      expect(result.issueClosed).toBe(true);
      expect(result.branchCleaned).toBe(true);
      expect(result.deploymentTriggered).toBe(true);
      expect(result.notificationsSent).toBe(true);
    });
  });
});
```

### Configuration Examples

**PR Merge Configuration**:

```yaml
pr_merge:
  default_strategy: 'squash' # merge, squash, rebase
  require_all_checks_pass: true
  require_min_approvals: 2
  require_code_owner_approval: false
  auto_delete_branch: true
  close_associated_issues: true
  trigger_deployment: true
  send_notifications: true

  completion_checkpoint:
    enabled: true
    validate_deployment: true
    validate_issue_closure: true
    validate_branch_cleanup: true
    validate_notifications: true
    timeout_minutes: 30
    rollback_on_failure: false

  post_merge_actions:
    - type: 'deployment_trigger'
      enabled: true
      order: 1
      config:
        environment: 'staging'
        wait_for_health_check: true
      retry_attempts: 3
      critical: true

    - type: 'issue_close'
      enabled: true
      order: 2
      config:
        add_completion_comment: true
        link_merge_commit: true
      retry_attempts: 2
      critical: false

    - type: 'branch_delete'
      enabled: true
      order: 3
      config:
        wait_for_merge_propagation: true
        confirm_no_refs: true
      retry_attempts: 3
      critical: false

    - type: 'notification_send'
      enabled: true
      order: 4
      config:
        channels: ['slack', 'email']
        template: 'merge_completion'
        include_metrics: true
      retry_attempts: 2
      critical: false

    - type: 'cleanup'
      enabled: true
      order: 5
      config:
        remove_temp_files: true
        clear_cache: true
        update_status: true
      retry_attempts: 1
      critical: false

  merge_commit:
    include_issue_number: true
    include_pr_number: true
    include_summary: true
    include_auto_generated_tag: true
    max_title_length: 72

  branch_cleanup:
    wait_for_merge_propagation_seconds: 30
    confirm_no_open_prs: true
    delete_remote_only: false
    backup_before_delete: false

  issue_closure:
    add_completion_comment: true
    link_merge_commit: true
    include_metrics: true
    close_reason: 'completed'
    add_labels: ['completed']
```

---

## Implementation Notes

**Key Considerations**:

1. **Merge Validation**: Comprehensive validation before attempting merge to prevent failures.

2. **Atomic Operations**: Ensure merge and related actions are atomic or can be rolled back.

3. **Error Recovery**: Handle merge failures gracefully with clear error messages and recovery paths.

4. **Completion Validation**: Verify all post-merge actions complete successfully.

5. **Audit Trail**: Complete logging of merge process for compliance and debugging.

6. **Next Issue Trigger**: Seamless transition to next issue for continuous operation.

**Performance Targets**:

- Merge validation: < 10 seconds
- Merge operation: < 15 seconds
- Post-merge actions: < 60 seconds
- Completion checkpoint: < 30 seconds
- Total merge cycle: < 2 minutes

**Security Considerations**:

- Validate merge permissions before attempting operations
- Handle sensitive information in merge messages appropriately
- Secure deployment triggers and notifications
- Proper authentication for all Git operations
- Audit all merge-related actions

**Merge Best Practices**:

- Clear, descriptive commit messages with issue references
- Appropriate merge strategy for project workflow
- Proper branch cleanup to maintain repository hygiene
- Timely issue closure with completion details
- Comprehensive stakeholder notifications
- Validation of deployment success
- Rollback capability for failed operations
- Complete audit trail for compliance

### References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md) - Search spikes, bugs, findings, decisions
