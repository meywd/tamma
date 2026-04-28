/**
 * Webhook Event Processor
 *
 * Processes webhook events from GitHub, GitLab, and other providers
 * Maps external events to internal actions and triggers appropriate workflows
 */

import { nanoid } from 'nanoid';
import type {
  WebhookProvider,
  WebhookProcessingResult,
  UnifiedWebhookEvent,
  UnifiedWebhookEventType,
  UnifiedPullRequestData,
  UnifiedReviewData,
  UnifiedCommentData,
  UnifiedPushData,
  GitHubPullRequestEvent,
  GitHubPullRequestReviewEvent,
  GitHubIssueCommentEvent,
  GitHubPushEvent,
  GitLabMergeRequestEvent,
  GitLabNoteEvent,
  GitLabPushEvent
} from './types';
import { WebhookStorage } from './storage.server';

export class WebhookProcessor {
  constructor(
    private db: D1Database,
    private storage: WebhookStorage
  ) {}

  /**
   * Process a webhook event
   */
  async processWebhookEvent(
    eventId: string,
    provider: WebhookProvider,
    eventType: string,
    payload: unknown
  ): Promise<WebhookProcessingResult> {
    try {
      // Convert to unified event format
      const unifiedEvent = this.unifyWebhookEvent(provider, eventType, payload);

      if (!unifiedEvent) {
        return {
          success: false,
          error: `Unsupported event type: ${eventType} from ${provider}`
        };
      }

      // Process based on event type
      const result = await this.processUnifiedEvent(unifiedEvent);

      // Mark event as processed
      if (result.success) {
        await this.storage.markWebhookEventProcessed(eventId);
      } else {
        await this.storage.markWebhookEventProcessed(eventId, result.error);
      }

      return result;
    } catch (error) {
      const errorMessage = error instanceof Error ? error.message : 'Unknown error';
      await this.storage.markWebhookEventProcessed(eventId, errorMessage);

      return {
        success: false,
        error: errorMessage
      };
    }
  }

  /**
   * Convert provider-specific webhook to unified format
   */
  private unifyWebhookEvent(
    provider: WebhookProvider,
    eventType: string,
    payload: unknown
  ): UnifiedWebhookEvent | null {
    if (provider === 'github') {
      return this.unifyGitHubEvent(eventType, payload);
    } else if (provider === 'gitlab') {
      return this.unifyGitLabEvent(eventType, payload);
    }

    return null;
  }

  /**
   * Unify GitHub webhook event
   */
  private unifyGitHubEvent(eventType: string, payload: unknown): UnifiedWebhookEvent | null {
    switch (eventType) {
      case 'pull_request': {
        const event = payload as GitHubPullRequestEvent;
        const unifiedType = this.mapGitHubPRAction(event.action);

        if (!unifiedType) return null;

        const data: UnifiedPullRequestData = {
          kind: 'pull_request',
          action: this.mapPRAction(event.action, event.pull_request.merged),
          prNumber: event.pull_request.number,
          title: event.pull_request.title,
          description: event.pull_request.body,
          state: event.pull_request.merged ? 'merged' : event.pull_request.state === 'open' ? 'open' : 'closed',
          draft: event.pull_request.draft,
          branch: event.pull_request.head.ref,
          baseBranch: event.pull_request.base.ref,
          url: event.pull_request.html_url,
          author: {
            id: String(event.pull_request.user.id),
            username: event.pull_request.user.login,
            avatarUrl: event.pull_request.user.avatar_url
          },
          createdAt: event.pull_request.created_at,
          updatedAt: event.pull_request.updated_at,
          mergedAt: event.pull_request.merged_at
        };

        return {
          provider: 'github',
          type: unifiedType,
          timestamp: new Date().toISOString(),
          data,
          raw: payload
        };
      }

      case 'pull_request_review': {
        const event = payload as GitHubPullRequestReviewEvent;

        if (event.action !== 'submitted' && event.action !== 'edited') return null;

        const data: UnifiedReviewData = {
          kind: 'review',
          action: event.action,
          prNumber: event.pull_request.number,
          reviewId: String(event.review.id),
          state: this.mapReviewState(event.review.state),
          body: event.review.body,
          author: {
            id: String(event.review.user.id),
            username: event.review.user.login,
            avatarUrl: event.review.user.avatar_url
          },
          createdAt: event.review.submitted_at || new Date().toISOString()
        };

        return {
          provider: 'github',
          type: event.action === 'submitted' ? 'review.submitted' : 'review.edited',
          timestamp: new Date().toISOString(),
          data,
          raw: payload
        };
      }

      case 'issue_comment': {
        const event = payload as GitHubIssueCommentEvent;

        // Only process PR comments
        if (!event.issue?.pull_request) return null;

        const actionMap: Record<string, 'created' | 'edited' | 'deleted'> = {
          created: 'created',
          edited: 'edited',
          deleted: 'deleted'
        };

        const action = actionMap[event.action];
        if (!action) return null;

        const data: UnifiedCommentData = {
          kind: 'comment',
          action,
          prNumber: event.issue.number,
          commentId: String(event.comment.id),
          body: event.comment.body,
          author: {
            id: String(event.comment.user.id),
            username: event.comment.user.login,
            avatarUrl: event.comment.user.avatar_url
          },
          createdAt: event.comment.created_at,
          updatedAt: event.comment.updated_at
        };

        return {
          provider: 'github',
          type: `comment.${action}` as UnifiedWebhookEventType,
          timestamp: new Date().toISOString(),
          data,
          raw: payload
        };
      }

      case 'push': {
        const event = payload as GitHubPushEvent;

        const data: UnifiedPushData = {
          kind: 'push',
          branch: event.ref.replace('refs/heads/', ''),
          commits: event.commits.map(commit => ({
            id: commit.id,
            message: commit.message,
            author: `${commit.author.name} <${commit.author.email}>`,
            timestamp: commit.timestamp,
            filesChanged: {
              added: commit.added,
              modified: commit.modified,
              removed: commit.removed
            }
          }))
        };

        return {
          provider: 'github',
          type: 'push.commits',
          timestamp: new Date().toISOString(),
          data,
          raw: payload
        };
      }

      default:
        return null;
    }
  }

  /**
   * Unify GitLab webhook event
   */
  private unifyGitLabEvent(eventType: string, payload: unknown): UnifiedWebhookEvent | null {
    switch (eventType) {
      case 'merge_request': {
        const event = payload as GitLabMergeRequestEvent;
        const action = event.object_attributes.action;

        if (!action) return null;

        const unifiedAction = this.mapGitLabMRAction(action);
        if (!unifiedAction) return null;

        const data: UnifiedPullRequestData = {
          kind: 'pull_request',
          action: unifiedAction,
          prNumber: event.object_attributes.iid,
          title: event.object_attributes.title,
          description: event.object_attributes.description,
          state: event.object_attributes.state === 'merged' ? 'merged' :
                 event.object_attributes.state === 'opened' ? 'open' : 'closed',
          draft: event.object_attributes.work_in_progress || event.object_attributes.draft,
          branch: event.object_attributes.source_branch,
          baseBranch: event.object_attributes.target_branch,
          url: event.object_attributes.web_url,
          author: {
            id: String(event.user.id),
            username: event.user.username,
            avatarUrl: event.user.avatar_url
          },
          createdAt: event.object_attributes.created_at,
          updatedAt: event.object_attributes.updated_at,
          mergedAt: event.object_attributes.merged_at
        };

        const unifiedType = `pull_request.${unifiedAction}` as UnifiedWebhookEventType;

        return {
          provider: 'gitlab',
          type: unifiedType,
          timestamp: new Date().toISOString(),
          data,
          raw: payload
        };
      }

      case 'note': {
        const event = payload as GitLabNoteEvent;

        // Only process MR notes
        if (event.object_attributes.noteable_type !== 'MergeRequest') return null;
        if (!event.merge_request) return null;

        const data: UnifiedCommentData = {
          kind: 'comment',
          action: 'created', // GitLab doesn't send edit/delete events for notes
          prNumber: event.merge_request.iid,
          commentId: String(event.object_attributes.id),
          body: event.object_attributes.note,
          author: {
            id: String(event.user.id),
            username: event.user.username,
            avatarUrl: event.user.avatar_url
          },
          createdAt: event.object_attributes.created_at,
          updatedAt: event.object_attributes.updated_at
        };

        return {
          provider: 'gitlab',
          type: 'comment.created',
          timestamp: new Date().toISOString(),
          data,
          raw: payload
        };
      }

      case 'push': {
        const event = payload as GitLabPushEvent;

        const data: UnifiedPushData = {
          kind: 'push',
          branch: event.ref.replace('refs/heads/', ''),
          commits: event.commits.map(commit => ({
            id: commit.id,
            message: commit.message,
            author: `${commit.author.name} <${commit.author.email}>`,
            timestamp: commit.timestamp,
            filesChanged: {
              added: commit.added,
              modified: commit.modified,
              removed: commit.removed
            }
          }))
        };

        return {
          provider: 'gitlab',
          type: 'push.commits',
          timestamp: new Date().toISOString(),
          data,
          raw: payload
        };
      }

      default:
        return null;
    }
  }

  /**
   * Process unified webhook event
   */
  private async processUnifiedEvent(event: UnifiedWebhookEvent): Promise<WebhookProcessingResult> {
    const actions: string[] = [];

    try {
      switch (event.data.kind) {
        case 'pull_request':
          await this.processPullRequestEvent(event.data, actions);
          break;

        case 'review':
          await this.processReviewEvent(event.data, actions);
          break;

        case 'comment':
          await this.processCommentEvent(event.data, actions);
          break;

        case 'push':
          await this.processPushEvent(event.data, actions);
          break;

        default:
          return {
            success: false,
            error: `Unknown event kind: ${(event.data as any).kind}`
          };
      }

      return {
        success: true,
        message: `Processed ${event.type} event`,
        actions
      };
    } catch (error) {
      return {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown error',
        actions
      };
    }
  }

  /**
   * Process pull request events
   */
  private async processPullRequestEvent(
    data: UnifiedPullRequestData,
    actions: string[]
  ): Promise<void> {
    // Find related session by branch name
    const session = await this.findSessionByBranch(data.branch);

    if (!session) {
      actions.push('No matching session found for branch');
      return;
    }

    switch (data.action) {
      case 'opened':
        // Create notification for session owner
        await this.createNotification({
          sessionId: session.id,
          type: 'pr_opened',
          title: 'Pull Request Opened',
          message: `PR #${data.prNumber}: ${data.title}`,
          metadata: {
            prNumber: data.prNumber,
            url: data.url
          }
        });
        actions.push('Created notification for PR opened');
        break;

      case 'merged':
        // Update all suggestions to merged status
        await this.updateSuggestionStatus(session.id, 'merged');
        await this.createNotification({
          sessionId: session.id,
          type: 'pr_merged',
          title: 'Pull Request Merged',
          message: `PR #${data.prNumber} has been merged`,
          metadata: {
            prNumber: data.prNumber,
            url: data.url
          }
        });
        actions.push('Updated suggestions to merged status');
        break;

      case 'closed':
        // Update all suggestions to rejected status if not merged
        if (data.state !== 'merged') {
          await this.updateSuggestionStatus(session.id, 'rejected');
          await this.createNotification({
            sessionId: session.id,
            type: 'pr_closed',
            title: 'Pull Request Closed',
            message: `PR #${data.prNumber} was closed without merging`,
            metadata: {
              prNumber: data.prNumber,
              url: data.url
            }
          });
          actions.push('Updated suggestions to rejected status');
        }
        break;

      case 'updated':
        // Sync any changes if needed
        actions.push('PR updated, no action needed');
        break;
    }
  }

  /**
   * Process review events
   */
  private async processReviewEvent(
    data: UnifiedReviewData,
    actions: string[]
  ): Promise<void> {
    // Find session by PR number
    const session = await this.findSessionByPRNumber(data.prNumber);

    if (!session) {
      actions.push('No matching session found for PR');
      return;
    }

    // Create a comment in the doc-review system
    await this.createCommentFromReview({
      sessionId: session.id,
      reviewId: data.reviewId,
      author: data.author.username,
      body: data.body || `Review: ${data.state}`,
      state: data.state,
      createdAt: data.createdAt
    });

    actions.push(`Created comment from ${data.state} review`);

    // Create notification
    await this.createNotification({
      sessionId: session.id,
      type: 'review_submitted',
      title: 'Review Submitted',
      message: `${data.author.username} ${data.state} the changes`,
      metadata: {
        prNumber: data.prNumber,
        reviewId: data.reviewId,
        state: data.state
      }
    });
  }

  /**
   * Process comment events
   */
  private async processCommentEvent(
    data: UnifiedCommentData,
    actions: string[]
  ): Promise<void> {
    if (!data.prNumber) {
      actions.push('No PR number in comment event');
      return;
    }

    // Find session by PR number
    const session = await this.findSessionByPRNumber(data.prNumber);

    if (!session) {
      actions.push('No matching session found for PR');
      return;
    }

    switch (data.action) {
      case 'created':
        // Sync comment to doc-review
        await this.syncCommentToDocReview({
          sessionId: session.id,
          commentId: data.commentId,
          author: data.author.username,
          body: data.body,
          createdAt: data.createdAt
        });
        actions.push('Synced comment to doc-review');
        break;

      case 'edited':
        // Update existing comment
        await this.updateSyncedComment({
          commentId: data.commentId,
          body: data.body,
          updatedAt: data.updatedAt
        });
        actions.push('Updated synced comment');
        break;

      case 'deleted':
        // Mark comment as deleted
        await this.deleteSyncedComment(data.commentId);
        actions.push('Marked synced comment as deleted');
        break;
    }
  }

  /**
   * Process push events
   */
  private async processPushEvent(
    data: UnifiedPushData,
    actions: string[]
  ): Promise<void> {
    // Find session by branch
    const session = await this.findSessionByBranch(data.branch);

    if (!session) {
      actions.push('No matching session found for branch');
      return;
    }

    // Check if any doc files were modified
    const docFiles = data.commits.flatMap(commit => [
      ...commit.filesChanged.added,
      ...commit.filesChanged.modified
    ]).filter(file => file.endsWith('.md') || file.includes('/docs/'));

    if (docFiles.length > 0) {
      // Create notification about doc updates
      await this.createNotification({
        sessionId: session.id,
        type: 'docs_updated',
        title: 'Documentation Updated',
        message: `${docFiles.length} documentation file(s) updated`,
        metadata: {
          files: docFiles,
          commits: data.commits.length
        }
      });
      actions.push(`Notified about ${docFiles.length} doc file updates`);
    } else {
      actions.push('No doc files in push, skipping');
    }
  }

  // ============================================================================
  // Helper Methods
  // ============================================================================

  private mapGitHubPRAction(action: string): UnifiedWebhookEventType | null {
    const mapping: Record<string, UnifiedWebhookEventType> = {
      opened: 'pull_request.opened',
      closed: 'pull_request.closed',
      reopened: 'pull_request.opened',
      edited: 'pull_request.updated',
      synchronize: 'pull_request.updated'
    };

    return mapping[action] || null;
  }

  private mapPRAction(
    githubAction: string,
    merged: boolean
  ): 'opened' | 'closed' | 'merged' | 'updated' {
    if (githubAction === 'closed' && merged) return 'merged';
    if (githubAction === 'opened' || githubAction === 'reopened') return 'opened';
    if (githubAction === 'closed') return 'closed';
    return 'updated';
  }

  private mapGitLabMRAction(action: string): 'opened' | 'closed' | 'merged' | 'updated' | null {
    const mapping: Record<string, 'opened' | 'closed' | 'merged' | 'updated'> = {
      open: 'opened',
      close: 'closed',
      reopen: 'opened',
      update: 'updated',
      merge: 'merged'
    };

    return mapping[action] || null;
  }

  private mapReviewState(githubState: string): 'pending' | 'commented' | 'approved' | 'changes_requested' {
    const mapping: Record<string, 'pending' | 'commented' | 'approved' | 'changes_requested'> = {
      PENDING: 'pending',
      COMMENTED: 'commented',
      APPROVED: 'approved',
      CHANGES_REQUESTED: 'changes_requested',
      DISMISSED: 'commented'
    };

    return mapping[githubState] || 'commented';
  }

  // ============================================================================
  // Database Operations (simplified stubs - implement based on your schema)
  // ============================================================================

  private async findSessionByBranch(branch: string): Promise<{ id: string } | null> {
    // TODO: Implement based on your session storage
    const result = await this.db
      .prepare('SELECT id FROM sessions WHERE branch = ? LIMIT 1')
      .bind(branch)
      .first<{ id: string }>();

    return result || null;
  }

  private async findSessionByPRNumber(prNumber: number): Promise<{ id: string } | null> {
    // TODO: Implement based on your session storage
    const result = await this.db
      .prepare('SELECT id FROM sessions WHERE pr_number = ? LIMIT 1')
      .bind(prNumber)
      .first<{ id: string }>();

    return result || null;
  }

  private async updateSuggestionStatus(sessionId: string, status: string): Promise<void> {
    await this.db
      .prepare('UPDATE suggestions SET status = ? WHERE session_id = ?')
      .bind(status, sessionId)
      .run();
  }

  private async createNotification(params: {
    sessionId: string;
    type: string;
    title: string;
    message: string;
    metadata?: Record<string, unknown>;
  }): Promise<void> {
    // TODO: Implement notification creation
    console.log('Creating notification:', params);
  }

  private async createCommentFromReview(params: {
    sessionId: string;
    reviewId: string;
    author: string;
    body: string;
    state: string;
    createdAt: string;
  }): Promise<void> {
    const id = nanoid();
    const now = Date.now();

    await this.db
      .prepare(`
        INSERT INTO comments (
          id, session_id, doc_path, content, user_id,
          created_at, updated_at, metadata
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
      `)
      .bind(
        id,
        params.sessionId,
        'review', // Special doc_path for reviews
        params.body,
        params.author,
        now,
        now,
        JSON.stringify({
          source: 'webhook',
          reviewId: params.reviewId,
          reviewState: params.state
        })
      )
      .run();
  }

  private async syncCommentToDocReview(params: {
    sessionId: string;
    commentId: string;
    author: string;
    body: string;
    createdAt: string;
  }): Promise<void> {
    const id = nanoid();
    const now = Date.now();

    await this.db
      .prepare(`
        INSERT INTO comments (
          id, session_id, doc_path, content, user_id,
          created_at, updated_at, metadata
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
      `)
      .bind(
        id,
        params.sessionId,
        'pr-comment', // Special doc_path for PR comments
        params.body,
        params.author,
        now,
        now,
        JSON.stringify({
          source: 'webhook',
          externalCommentId: params.commentId
        })
      )
      .run();
  }

  private async updateSyncedComment(params: {
    commentId: string;
    body: string;
    updatedAt: string;
  }): Promise<void> {
    const now = Date.now();

    await this.db
      .prepare(`
        UPDATE comments
        SET content = ?, updated_at = ?
        WHERE JSON_EXTRACT(metadata, '$.externalCommentId') = ?
      `)
      .bind(params.body, now, params.commentId)
      .run();
  }

  private async deleteSyncedComment(commentId: string): Promise<void> {
    const now = Date.now();

    await this.db
      .prepare(`
        UPDATE comments
        SET deleted_at = ?
        WHERE JSON_EXTRACT(metadata, '$.externalCommentId') = ?
      `)
      .bind(now, commentId)
      .run();
  }
}