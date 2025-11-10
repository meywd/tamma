# Story 1.6 Task 4: Integrate GitLab Merge Request API

## Task Overview

Implement comprehensive GitLab Merge Request (MR) API integration to enable autonomous creation, management, and review of merge requests within the Tamma platform. This integration will allow autonomous workflows to create MRs for code changes, manage review processes, and handle merge operations based on quality gate results.

## Acceptance Criteria

### 4.1 Merge Request Creation and Management

- [ ] Implement merge request creation with title, description, and source/target branches
- [ ] Support merge request template discovery and application
- [ ] Implement draft merge request creation and promotion to ready status
- [ ] Support merge request editing and metadata updates
- [ ] Add merge request labeling and milestone assignment

### 4.2 Review and Approval Workflow Integration

- [ ] Implement reviewer assignment and approval tracking
- [ ] Support merge request status monitoring (draft, ready, merged, closed)
- [ ] Implement approval rule configuration and validation
- [ ] Support required approver management and override capabilities
- [ ] Add merge request discussion and comment management

### 4.3 Merge Operations and Quality Gates

- [ ] Implement merge request merging with various strategies (merge, squash, rebase)
- [ ] Support merge conflict detection and resolution guidance
- [ ] Implement pipeline status validation before merging
- [ ] Support merge request dependency management and blocking
- [ ] Add merge protection rule enforcement

### 4.4 Integration with Autonomous Workflows

- [ ] Implement MR-based decision making for autonomous development
- [ ] Support automatic MR creation for completed development tasks
- [ ] Implement MR review automation with AI-powered suggestions
- [ ] Support MR status-based workflow progression
- [ ] Add MR analytics and impact assessment

### 4.5 Real-time Event Handling

- [ ] Implement MR webhook integration for real-time status updates
- [ ] Support MR event streaming for workflow triggers
- [ ] Implement MR change detection and delta analysis
- [ ] Support MR discussion monitoring and sentiment analysis
- [ ] Add MR lifecycle event tracking

### 4.6 Testing and Validation

- [ ] Implement comprehensive unit tests for all MR operations
- [ ] Add integration tests with GitLab MR test projects
- [ ] Implement end-to-end tests for MR workflows
- [ ] Add performance tests for MR API interactions
- [ ] Implement security tests for MR permissions and access control

## Implementation Details

### 4.1 Merge Request API Interface Design

```typescript
// Merge Request related types
interface GitLabMergeRequest {
  id: number;
  iid: number;
  project_id: number;
  title: string;
  description: string;
  state: 'opened' | 'closed' | 'locked' | 'merged';
  created_at: string;
  updated_at: string;
  merged_at?: string;
  merged_by?: GitLabUser;
  target_branch: string;
  source_branch: string;
  source_project_id: number;
  target_project_id: number;
  author: GitLabUser;
  assignees: GitLabUser[];
  reviewers: GitLabUser[];
  participants: GitLabUser[];
  labels: string[];
  milestone?: GitLabMilestone;
  draft: boolean;
  work_in_progress: boolean;
  merge_when_pipeline_succeeds: boolean;
  merge_status: 'can_be_merged' | 'cannot_be_merged' | 'checking' | 'unchecked';
  sha: string;
  merge_commit_sha?: string;
  squash: boolean;
  discussion_locked: boolean;
  should_remove_source_branch: boolean;
  force_remove_source_branch: boolean;
  reference: string;
  references: {
    short: string;
    relative: string;
    full: string;
  };
  web_url: string;
  time_stats: GitLabTimeStats;
  task_completion_status: {
    count: number;
    completed_count: number;
  };
  diff_refs: {
    base_sha: string;
    head_sha: string;
    start_sha: string;
  };
}

interface GitLabMergeRequestDiff {
  old_path: string;
  new_path: string;
  new_file: boolean;
  renamed_file: boolean;
  deleted_file: boolean;
  diff: string;
}

interface GitLabMergeRequestDiscussion {
  id: string;
  individual_note: boolean;
  notes: GitLabNote[];
}

interface GitLabNote {
  id: number;
  type: 'DiffNote' | 'DiscussionNote' | 'Note';
  body: string;
  attachment?: string;
  author: GitLabUser;
  created_at: string;
  updated_at: string;
  system: boolean;
  noteable_id: number;
  noteable_type: 'MergeRequest';
  resolvable: boolean;
  resolved: boolean;
  resolved_by?: GitLabUser;
  resolved_at?: string;
}

interface GitLabApprovalRule {
  id: number;
  name: string;
  rule_type: 'regular' | 'code_owner' | 'any_approver';
  eligible_approvers: GitLabUser[];
  approvals_required: number;
  users: GitLabUser[];
  groups: GitLabGroup[];
  contains_hidden_groups: boolean;
  protected_branches: string[];
}

interface GitLabApprovalState {
  approval_rules_overwritten: boolean;
  rules: GitLabApprovalRule[];
  user_has_approved: boolean;
  approved: boolean;
  approved_by: GitLabUser[];
  suggested_approvers: GitLabUser[];
  requires_multiple_approvers: boolean;
  merge_request: {
    id: number;
    iid: number;
    project_id: number;
    title: string;
    description: string;
    state: string;
  };
}

// Merge Request Manager interface
interface IGitLabMergeRequestManager {
  // MR operations
  createMergeRequest(
    projectId: number,
    options: CreateMergeRequestOptions
  ): Promise<GitLabMergeRequest>;
  getMergeRequest(projectId: number, mergeRequestIid: number): Promise<GitLabMergeRequest>;
  updateMergeRequest(
    projectId: number,
    mergeRequestIid: number,
    options: UpdateMergeRequestOptions
  ): Promise<GitLabMergeRequest>;
  deleteMergeRequest(projectId: number, mergeRequestIid: number): Promise<void>;
  listMergeRequests(
    projectId: number,
    options?: ListMergeRequestsOptions
  ): Promise<GitLabMergeRequest[]>;

  // MR content operations
  getMergeRequestChanges(
    projectId: number,
    mergeRequestIid: number
  ): Promise<GitLabMergeRequestDiff[]>;
  getMergeRequestCommits(projectId: number, mergeRequestIid: number): Promise<GitLabCommit[]>;
  getMergeRequestPipelines(projectId: number, mergeRequestIid: number): Promise<GitLabPipeline[]>;

  // Review and approval operations
  getMergeRequestApprovals(
    projectId: number,
    mergeRequestIid: number
  ): Promise<GitLabApprovalState>;
  approveMergeRequest(
    projectId: number,
    mergeRequestIid: number,
    options?: ApproveOptions
  ): Promise<void>;
  unapproveMergeRequest(projectId: number, mergeRequestIid: number): Promise<void>;
  getApprovalRules(projectId: number, mergeRequestIid: number): Promise<GitLabApprovalRule[]>;

  // Merge operations
  mergeMergeRequest(
    projectId: number,
    mergeRequestIid: number,
    options?: MergeOptions
  ): Promise<GitLabMergeRequest>;
  cancelMergeWhenPipelineSucceeds(projectId: number, mergeRequestIid: number): Promise<void>;
  rebaseMergeRequest(projectId: number, mergeRequestIid: number): Promise<void>;

  // Discussion operations
  getMergeRequestDiscussions(
    projectId: number,
    mergeRequestIid: number
  ): Promise<GitLabMergeRequestDiscussion[]>;
  createMergeRequestDiscussion(
    projectId: number,
    mergeRequestIid: number,
    options: CreateDiscussionOptions
  ): Promise<GitLabMergeRequestDiscussion>;
  addMergeRequestNote(
    projectId: number,
    mergeRequestIid: number,
    body: string
  ): Promise<GitLabNote>;
  resolveMergeRequestDiscussion(
    projectId: number,
    mergeRequestIid: number,
    discussionId: string
  ): Promise<void>;

  // Event subscription
  subscribeToMergeRequestEvents(
    projectId: number,
    callback: MergeRequestEventCallback
  ): Promise<void>;
}
```

### 4.2 Merge Request Manager Implementation

```typescript
class GitLabMergeRequestManager implements IGitLabMergeRequestManager {
  private httpClient: HttpClient;
  private instanceUrl: string;
  private eventEmitter: EventEmitter;
  private webhookManager: GitLabWebhookManager;

  constructor(instanceUrl: string, httpClient: HttpClient, webhookManager: GitLabWebhookManager) {
    this.instanceUrl = instanceUrl;
    this.httpClient = httpClient;
    this.eventEmitter = new EventEmitter();
    this.webhookManager = webhookManager;
  }

  // MR Operations
  async createMergeRequest(
    projectId: number,
    options: CreateMergeRequestOptions
  ): Promise<GitLabMergeRequest> {
    const payload = {
      source_branch: options.sourceBranch,
      target_branch: options.targetBranch,
      title: options.title,
      description: options.description || '',
      assignee_ids: options.assigneeIds || [],
      reviewer_ids: options.reviewerIds || [],
      milestone_id: options.milestoneId,
      labels: options.labels || [],
      remove_source_branch: options.removeSourceBranch || false,
      squash: options.squash || false,
      allow_collaboration: options.allowCollaboration || false,
      allow_maintainer_to_push: options.allowMaintainerToPush || false,
    };

    // Apply template if specified
    if (options.template) {
      payload.description = await this.applyMergeRequestTemplate(
        options.template,
        payload.description,
        options.templateVariables || {}
      );
    }

    const response = await this.httpClient.post(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests`,
      payload,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    const mergeRequest = response.data as GitLabMergeRequest;

    // Emit MR created event
    this.eventEmitter.emit('merge_request.created', { projectId, mergeRequest });

    return mergeRequest;
  }

  async getMergeRequest(projectId: number, mergeRequestIid: number): Promise<GitLabMergeRequest> {
    const response = await this.httpClient.get(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests/${mergeRequestIid}`,
      {
        headers: await this.getAuthHeaders(),
        params: {
          include_diverged_commits_count: true,
          include_rebase_in_progress: true,
        },
      }
    );

    return response.data as GitLabMergeRequest;
  }

  async updateMergeRequest(
    projectId: number,
    mergeRequestIid: number,
    options: UpdateMergeRequestOptions
  ): Promise<GitLabMergeRequest> {
    const payload: any = {};

    if (options.title) payload.title = options.title;
    if (options.description !== undefined) payload.description = options.description;
    if (options.targetBranch) payload.target_branch = options.targetBranch;
    if (options.assigneeIds !== undefined) payload.assignee_ids = options.assigneeIds;
    if (options.reviewerIds !== undefined) payload.reviewer_ids = options.reviewerIds;
    if (options.milestoneId !== undefined) payload.milestone_id = options.milestoneId;
    if (options.labels !== undefined) payload.labels = options.labels;
    if (options.stateId !== undefined) payload.state_event = options.stateId;
    if (options.removeSourceBranch !== undefined)
      payload.remove_source_branch = options.removeSourceBranch;
    if (options.squash !== undefined) payload.squash = options.squash;
    if (options.draft !== undefined) payload.draft = options.draft;

    const response = await this.httpClient.put(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests/${mergeRequestIid}`,
      payload,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    const mergeRequest = response.data as GitLabMergeRequest;
    this.eventEmitter.emit('merge_request.updated', { projectId, mergeRequest });

    return mergeRequest;
  }

  async deleteMergeRequest(projectId: number, mergeRequestIid: number): Promise<void> {
    await this.httpClient.delete(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests/${mergeRequestIid}`,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    this.eventEmitter.emit('merge_request.deleted', { projectId, mergeRequestIid });
  }

  async listMergeRequests(
    projectId: number,
    options: ListMergeRequestsOptions = {}
  ): Promise<GitLabMergeRequest[]> {
    const params = new URLSearchParams();

    if (options.state) params.append('state', options.state);
    if (options.authorId) params.append('author_id', options.authorId.toString());
    if (options.authorUsername) params.append('author_username', options.authorUsername);
    if (options.assigneeId) params.append('assignee_id', options.assigneeId.toString());
    if (options.approverIds) params.append('approver_ids', options.approverIds.join(','));
    if (options.labels) params.append('labels', options.labels.join(','));
    if (options.milestone) params.append('milestone', options.milestone);
    if (options.sourceBranch) params.append('source_branch', options.sourceBranch);
    if (options.targetBranch) params.append('target_branch', options.targetBranch);
    if (options.search) params.append('search', options.search);
    if (options.createdAfter) params.append('created_after', options.createdAfter.toISOString());
    if (options.createdBefore) params.append('created_before', options.createdBefore.toISOString());
    if (options.updatedAfter) params.append('updated_after', options.updatedAfter.toISOString());
    if (options.updatedBefore) params.append('updated_before', options.updatedBefore.toISOString());
    if (options.orderBy) params.append('order_by', options.orderBy);
    if (options.sort) params.append('sort', options.sort);

    const response = await this.httpClient.get(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests?${params.toString()}`,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    return response.data as GitLabMergeRequest[];
  }

  // MR Content Operations
  async getMergeRequestChanges(
    projectId: number,
    mergeRequestIid: number
  ): Promise<GitLabMergeRequestDiff[]> {
    const response = await this.httpClient.get(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests/${mergeRequestIid}/changes`,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    return response.data.changes as GitLabMergeRequestDiff[];
  }

  async getMergeRequestCommits(
    projectId: number,
    mergeRequestIid: number
  ): Promise<GitLabCommit[]> {
    const response = await this.httpClient.get(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests/${mergeRequestIid}/commits`,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    return response.data as GitLabCommit[];
  }

  async getMergeRequestPipelines(
    projectId: number,
    mergeRequestIid: number
  ): Promise<GitLabPipeline[]> {
    const response = await this.httpClient.get(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests/${mergeRequestIid}/pipelines`,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    return response.data as GitLabPipeline[];
  }

  // Review and Approval Operations
  async getMergeRequestApprovals(
    projectId: number,
    mergeRequestIid: number
  ): Promise<GitLabApprovalState> {
    const response = await this.httpClient.get(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests/${mergeRequestIid}/approvals`,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    return response.data as GitLabApprovalState;
  }

  async approveMergeRequest(
    projectId: number,
    mergeRequestIid: number,
    options: ApproveOptions = {}
  ): Promise<void> {
    const payload: any = {};

    if (options.sha) payload.sha = options.sha;
    if (options.approvalPassword) payload.approval_password = options.approvalPassword;

    await this.httpClient.post(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests/${mergeRequestIid}/approve`,
      payload,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    this.eventEmitter.emit('merge_request.approved', { projectId, mergeRequestIid });
  }

  async unapproveMergeRequest(projectId: number, mergeRequestIid: number): Promise<void> {
    await this.httpClient.post(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests/${mergeRequestIid}/unapprove`,
      {},
      {
        headers: await this.getAuthHeaders(),
      }
    );

    this.eventEmitter.emit('merge_request.unapproved', { projectId, mergeRequestIid });
  }

  async getApprovalRules(
    projectId: number,
    mergeRequestIid: number
  ): Promise<GitLabApprovalRule[]> {
    const response = await this.httpClient.get(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests/${mergeRequestIid}/approval_rules`,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    return response.data as GitLabApprovalRule[];
  }

  // Merge Operations
  async mergeMergeRequest(
    projectId: number,
    mergeRequestIid: number,
    options: MergeOptions = {}
  ): Promise<GitLabMergeRequest> {
    const payload: any = {
      squash: options.squash || false,
      should_remove_source_branch: options.shouldRemoveSourceBranch || false,
      merge_when_pipeline_succeeds: options.mergeWhenPipelineSucceeds || false,
    };

    if (options.squashCommitMessage) payload.squash_commit_message = options.squashCommitMessage;
    if (options.commitMessage) payload.commit_message = options.commitMessage;

    const response = await this.httpClient.put(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests/${mergeRequestIid}/merge`,
      payload,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    const mergeRequest = response.data as GitLabMergeRequest;
    this.eventEmitter.emit('merge_request.merged', { projectId, mergeRequest });

    return mergeRequest;
  }

  async cancelMergeWhenPipelineSucceeds(projectId: number, mergeRequestIid: number): Promise<void> {
    await this.httpClient.post(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests/${mergeRequestIid}/cancel_merge_when_pipeline_succeeds`,
      {},
      {
        headers: await this.getAuthHeaders(),
      }
    );

    this.eventEmitter.emit('merge_request.merge_cancelled', { projectId, mergeRequestIid });
  }

  async rebaseMergeRequest(projectId: number, mergeRequestIid: number): Promise<void> {
    await this.httpClient.put(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests/${mergeRequestIid}/rebase`,
      {},
      {
        headers: await this.getAuthHeaders(),
      }
    );

    this.eventEmitter.emit('merge_request.rebased', { projectId, mergeRequestIid });
  }

  // Discussion Operations
  async getMergeRequestDiscussions(
    projectId: number,
    mergeRequestIid: number
  ): Promise<GitLabMergeRequestDiscussion[]> {
    const response = await this.httpClient.get(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests/${mergeRequestIid}/discussions`,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    return response.data as GitLabMergeRequestDiscussion[];
  }

  async createMergeRequestDiscussion(
    projectId: number,
    mergeRequestIid: number,
    options: CreateDiscussionOptions
  ): Promise<GitLabMergeRequestDiscussion> {
    const payload: any = {
      body: options.body,
    };

    if (options.position) {
      payload.position = options.position;
    }

    if (options.commitId) {
      payload.commit_id = options.commitId;
    }

    const response = await this.httpClient.post(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests/${mergeRequestIid}/discussions`,
      payload,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    return response.data as GitLabMergeRequestDiscussion;
  }

  async addMergeRequestNote(
    projectId: number,
    mergeRequestIid: number,
    body: string
  ): Promise<GitLabNote> {
    const response = await this.httpClient.post(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests/${mergeRequestIid}/notes`,
      { body },
      {
        headers: await this.getAuthHeaders(),
      }
    );

    return response.data as GitLabNote;
  }

  async resolveMergeRequestDiscussion(
    projectId: number,
    mergeRequestIid: number,
    discussionId: string
  ): Promise<void> {
    await this.httpClient.post(
      `${this.instanceUrl}/api/v4/projects/${projectId}/merge_requests/${mergeRequestIid}/discussions/${discussionId}/resolve`,
      {},
      {
        headers: await this.getAuthHeaders(),
      }
    );
  }

  // Event Subscription
  async subscribeToMergeRequestEvents(
    projectId: number,
    callback: MergeRequestEventCallback
  ): Promise<void> {
    await this.webhookManager.subscribe(projectId, 'merge_request', callback);
  }

  // Private helper methods
  private async applyMergeRequestTemplate(
    template: string,
    description: string,
    variables: Record<string, string>
  ): Promise<string> {
    // Load template
    const templateContent = await this.loadMergeRequestTemplate(template);

    // Replace variables
    let content = templateContent;
    for (const [key, value] of Object.entries(variables)) {
      const regex = new RegExp(`\\$\\{${key}\\}`, 'g');
      content = content.replace(regex, value);
    }

    // Combine with existing description
    if (description) {
      content = `${description}\n\n${content}`;
    }

    return content;
  }

  private async loadMergeRequestTemplate(template: string): Promise<string> {
    const templatePath = path.join(__dirname, 'templates', 'merge-requests', `${template}.md`);

    try {
      return await fs.readFile(templatePath, 'utf-8');
    } catch (error) {
      throw new Error(`Merge request template "${template}" not found`);
    }
  }

  private async getAuthHeaders(): Promise<Record<string, string>> {
    return {
      Authorization: 'Bearer token',
      'User-Agent': 'Tamma/1.0.0',
    };
  }
}
```

### 4.3 Merge Request Workflow Integration

```typescript
class GitLabMergeRequestWorkflowIntegration {
  private mrManager: IGitLabMergeRequestManager;
  private cicdManager: IGitLabCICDManager;
  private eventStore: IEventStore;
  private logger: Logger;

  constructor(
    mrManager: IGitLabMergeRequestManager,
    cicdManager: IGitLabCICDManager,
    eventStore: IEventStore,
    logger: Logger
  ) {
    this.mrManager = mrManager;
    this.cicdManager = cicdManager;
    this.eventStore = eventStore;
    this.logger = logger;
  }

  async createMergeRequestForIssue(
    issueId: string,
    projectId: number,
    sourceBranch: string,
    targetBranch: string = 'main',
    options: CreateMergeRequestForIssueOptions = {}
  ): Promise<GitLabMergeRequest> {
    try {
      // Generate MR title and description
      const { title, description } = await this.generateMergeRequestContent(issueId, options);

      // Create merge request
      const mergeRequest = await this.mrManager.createMergeRequest(projectId, {
        sourceBranch,
        targetBranch,
        title,
        description,
        labels: options.labels || ['tamma-generated'],
        assigneeIds: options.assigneeIds,
        reviewerIds: options.reviewerIds,
        template: options.template,
        templateVariables: {
          ISSUE_ID: issueId,
          SOURCE_BRANCH: sourceBranch,
          TARGET_BRANCH: targetBranch,
          ...options.templateVariables,
        },
        removeSourceBranch: true,
        squash: options.squash !== false,
      });

      await this.emitEvent('MERGE_REQUEST.CREATED.SUCCESS', {
        issueId,
        projectId,
        mergeRequestId: mergeRequest.id,
        mergeRequestIid: mergeRequest.iid,
        sourceBranch,
        targetBranch,
      });

      return mergeRequest;
    } catch (error) {
      await this.emitEvent('MERGE_REQUEST.CREATED.FAILED', {
        issueId,
        projectId,
        sourceBranch,
        targetBranch,
        error: error.message,
      });

      throw error;
    }
  }

  async monitorMergeRequestForIssue(
    issueId: string,
    projectId: number,
    mergeRequestIid: number
  ): Promise<void> {
    // Subscribe to MR events
    await this.mrManager.subscribeToMergeRequestEvents(projectId, async (event) => {
      await this.handleMergeRequestEvent(issueId, event);
    });

    // Get current MR status
    const mergeRequest = await this.mrManager.getMergeRequest(projectId, mergeRequestIid);
    await this.emitMergeRequestStatusEvent(issueId, mergeRequest);
  }

  async waitForMergeRequestCompletion(
    issueId: string,
    projectId: number,
    mergeRequestIid: number,
    timeout: number = 7 * 24 * 60 * 60 * 1000 // 7 days
  ): Promise<GitLabMergeRequest> {
    const startTime = Date.now();

    while (Date.now() - startTime < timeout) {
      const mergeRequest = await this.mrManager.getMergeRequest(projectId, mergeRequestIid);

      if (['merged', 'closed'].includes(mergeRequest.state)) {
        await this.emitMergeRequestCompletionEvent(issueId, mergeRequest);
        return mergeRequest;
      }

      // Wait 5 minutes before checking again
      await new Promise((resolve) => setTimeout(resolve, 5 * 60 * 1000));
    }

    throw new Error(`Merge request ${mergeRequestIid} did not complete within ${timeout}ms`);
  }

  async evaluateMergeRequestReadiness(
    issueId: string,
    projectId: number,
    mergeRequestIid: number
  ): Promise<MergeRequestReadinessResult> {
    const mergeRequest = await this.mrManager.getMergeRequest(projectId, mergeRequestIid);
    const approvals = await this.mrManager.getMergeRequestApprovals(projectId, mergeRequestIid);
    const pipelines = await this.mrManager.getMergeRequestPipelines(projectId, mergeRequestIid);

    const result: MergeRequestReadinessResult = {
      mergeRequest,
      approvals,
      pipelines,
      readyToMerge: false,
      blockers: [],
      recommendations: [],
    };

    // Check if MR is in draft state
    if (mergeRequest.draft || mergeRequest.work_in_progress) {
      result.blockers.push('Merge request is in draft state');
      result.recommendations.push('Mark merge request as ready for review');
    }

    // Check approvals
    if (!approvals.approved) {
      result.blockers.push('Merge request is not approved');
      result.recommendations.push('Obtain required approvals before merging');
    }

    // Check pipeline status
    const latestPipeline = pipelines[0];
    if (!latestPipeline || latestPipeline.status !== 'success') {
      result.blockers.push('Pipeline is not passing');
      result.recommendations.push('Wait for pipeline to pass before merging');
    }

    // Check merge conflicts
    if (mergeRequest.merge_status !== 'can_be_merged') {
      result.blockers.push('Merge conflicts detected');
      result.recommendations.push('Resolve merge conflicts before merging');
    }

    // Check for unresolved discussions
    const discussions = await this.mrManager.getMergeRequestDiscussions(projectId, mergeRequestIid);
    const unresolvedDiscussions = discussions.filter((d) =>
      d.notes.some((n) => n.resolvable && !n.resolved)
    );

    if (unresolvedDiscussions.length > 0) {
      result.blockers.push('Unresolved discussions exist');
      result.recommendations.push('Resolve all discussions before merging');
    }

    result.readyToMerge = result.blockers.length === 0;

    await this.emitEvent('MERGE_REQUEST.READINESS_EVALUATED', {
      issueId,
      projectId,
      mergeRequestIid,
      readiness: result,
    });

    return result;
  }

  async autoMergeIfReady(
    issueId: string,
    projectId: number,
    mergeRequestIid: number,
    options: AutoMergeOptions = {}
  ): Promise<boolean> {
    const readiness = await this.evaluateMergeRequestReadiness(issueId, projectId, mergeRequestIid);

    if (!readiness.readyToMerge) {
      this.logger.info('Merge request not ready for auto-merge', {
        issueId,
        projectId,
        mergeRequestIid,
        blockers: readiness.blockers,
      });

      return false;
    }

    try {
      await this.mrManager.mergeMergeRequest(projectId, mergeRequestIid, {
        squash: options.squash !== false,
        shouldRemoveSourceBranch: options.shouldRemoveSourceBranch !== false,
        mergeWhenPipelineSucceeds: options.mergeWhenPipelineSucceeds || false,
        commitMessage: options.commitMessage,
      });

      await this.emitEvent('MERGE_REQUEST.AUTO_MERGED.SUCCESS', {
        issueId,
        projectId,
        mergeRequestIid,
      });

      return true;
    } catch (error) {
      await this.emitEvent('MERGE_REQUEST.AUTO_MERGED.FAILED', {
        issueId,
        projectId,
        mergeRequestIid,
        error: error.message,
      });

      return false;
    }
  }

  async addAIReviewComments(
    issueId: string,
    projectId: number,
    mergeRequestIid: number,
    review: AIReviewResult
  ): Promise<void> {
    const mergeRequest = await this.mrManager.getMergeRequest(projectId, mergeRequestIid);

    // Add overall review comment
    if (review.summary) {
      await this.mrManager.addMergeRequestNote(
        projectId,
        mergeRequestIid,
        `## 🤖 AI Review Summary\n\n${review.summary}`
      );
    }

    // Add file-specific comments
    for (const fileReview of review.fileReviews) {
      for (const comment of fileReview.comments) {
        await this.mrManager.createMergeRequestDiscussion(projectId, mergeRequestIid, {
          body: `## 🤖 AI Suggestion\n\n${comment.message}`,
          position: {
            base_sha: mergeRequest.diff_refs.base_sha,
            start_sha: mergeRequest.diff_refs.start_sha,
            head_sha: mergeRequest.diff_refs.head_sha,
            old_path: fileReview.filePath,
            new_path: fileReview.filePath,
            position_type: 'text',
            new_line: comment.line,
          },
        });
      }
    }

    await this.emitEvent('MERGE_REQUEST.AI_REVIEW_ADDED', {
      issueId,
      projectId,
      mergeRequestIid,
      review,
    });
  }

  private async generateMergeRequestContent(
    issueId: string,
    options: CreateMergeRequestForIssueOptions
  ): Promise<{ title: string; description: string }> {
    // Generate title based on issue or custom title
    const title = options.title || `Implement changes for issue ${issueId}`;

    // Generate description
    let description = options.description || '';

    if (!description) {
      description = `## Summary\n\nThis merge request implements the changes required for issue ${issueId}.\n\n`;

      if (options.changes) {
        description += `## Changes\n\n${options.changes}\n\n`;
      }

      description += `## Testing\n\n- [ ] Unit tests pass\n- [ ] Integration tests pass\n- [ ] Manual testing completed\n\n`;
      description += `## Checklist\n\n- [ ] Code follows project style guidelines\n- [ ] Self-review completed\n- [ ] Documentation updated if required\n`;
    }

    return { title, description };
  }

  private async handleMergeRequestEvent(issueId: string, event: any): Promise<void> {
    const eventType = event.object_kind;

    if (eventType === 'merge_request') {
      await this.handleMergeRequestEvent(issueId, event.object_attributes);
    }
  }

  private async handleMergeRequestEvent(issueId: string, mr: any): Promise<void> {
    await this.emitMergeRequestStatusEvent(issueId, mr);

    // If MR was merged, trigger completion workflow
    if (mr.state === 'merged') {
      await this.emitEvent('MERGE_REQUEST.MERGED', {
        issueId,
        projectId: mr.project_id,
        mergeRequestId: mr.id,
        mergeRequestIid: mr.iid,
      });
    }

    // If MR was closed without merging, trigger analysis
    if (mr.state === 'closed' && !mr.merged_at) {
      await this.emitEvent('MERGE_REQUEST.CLOSED', {
        issueId,
        projectId: mr.project_id,
        mergeRequestId: mr.id,
        mergeRequestIid: mr.iid,
      });
    }
  }

  private async emitMergeRequestStatusEvent(
    issueId: string,
    mergeRequest: GitLabMergeRequest
  ): Promise<void> {
    await this.emitEvent('MERGE_REQUEST.STATUS_CHANGED', {
      issueId,
      projectId: mergeRequest.project_id,
      mergeRequestId: mergeRequest.id,
      mergeRequestIid: mergeRequest.iid,
      state: mergeRequest.state,
      title: mergeRequest.title,
      draft: mergeRequest.draft,
      mergeStatus: mergeRequest.merge_status,
    });
  }

  private async emitMergeRequestCompletionEvent(
    issueId: string,
    mergeRequest: GitLabMergeRequest
  ): Promise<void> {
    await this.emitEvent('MERGE_REQUEST.COMPLETED', {
      issueId,
      projectId: mergeRequest.project_id,
      mergeRequestId: mergeRequest.id,
      mergeRequestIid: mergeRequest.iid,
      state: mergeRequest.state,
      merged: mergeRequest.state === 'merged',
      mergedAt: mergeRequest.merged_at,
    });
  }

  private async emitEvent(eventType: string, data: any): Promise<void> {
    await this.eventStore.append({
      type: eventType,
      timestamp: new Date().toISOString(),
      tags: {
        issueId: data.issueId,
        projectId: data.projectId?.toString(),
      },
      metadata: {
        workflowVersion: '1.0.0',
        eventSource: 'gitlab-mr-integration',
      },
      data,
    });
  }
}
```

## Testing Strategy

### 4.1 Unit Tests

```typescript
describe('GitLabMergeRequestManager', () => {
  let mrManager: GitLabMergeRequestManager;
  let mockHttpClient: jest.Mocked<HttpClient>;
  let mockWebhookManager: jest.Mocked<GitLabWebhookManager>;

  beforeEach(() => {
    mockHttpClient = createMockHttpClient();
    mockWebhookManager = createMockWebhookManager();
    mrManager = new GitLabMergeRequestManager(
      'https://gitlab.com',
      mockHttpClient,
      mockWebhookManager
    );
  });

  describe('Merge Request Creation', () => {
    it('should create merge request with template', async () => {
      const mockMR = createMockMergeRequest();
      mockHttpClient.post.mockResolvedValue({ data: mockMR });

      const result = await mrManager.createMergeRequest(123, {
        sourceBranch: 'feature-branch',
        targetBranch: 'main',
        title: 'Test MR',
        template: 'feature-template',
        templateVariables: { ISSUE_ID: '123' },
      });

      expect(result).toEqual(mockMR);
      expect(mockHttpClient.post).toHaveBeenCalledWith(
        'https://gitlab.com/api/v4/projects/123/merge_requests',
        expect.objectContaining({
          source_branch: 'feature-branch',
          target_branch: 'main',
          title: 'Test MR',
        }),
        expect.any(Object)
      );
    });

    it('should update merge request status', async () => {
      const mockMR = createMockMergeRequest();
      mockHttpClient.put.mockResolvedValue({ data: mockMR });

      const result = await mrManager.updateMergeRequest(123, 456, {
        stateId: 'close',
        labels: ['completed'],
      });

      expect(result).toEqual(mockMR);
      expect(mockHttpClient.put).toHaveBeenCalledWith(
        'https://gitlab.com/api/v4/projects/123/merge_requests/456',
        expect.objectContaining({
          state_event: 'close',
          labels: ['completed'],
        }),
        expect.any(Object)
      );
    });
  });

  describe('Merge Operations', () => {
    it('should merge merge request with squash', async () => {
      const mockMR = createMockMergeRequest({ state: 'merged' });
      mockHttpClient.put.mockResolvedValue({ data: mockMR });

      const result = await mrManager.mergeMergeRequest(123, 456, {
        squash: true,
        shouldRemoveSourceBranch: true,
      });

      expect(result.state).toBe('merged');
      expect(mockHttpClient.put).toHaveBeenCalledWith(
        'https://gitlab.com/api/v4/projects/123/merge_requests/456/merge',
        expect.objectContaining({
          squash: true,
          should_remove_source_branch: true,
        }),
        expect.any(Object)
      );
    });
  });

  describe('Approval Operations', () => {
    it('should approve merge request', async () => {
      mockHttpClient.post.mockResolvedValue({ data: {} });

      await mrManager.approveMergeRequest(123, 456, {
        sha: 'commit-sha',
      });

      expect(mockHttpClient.post).toHaveBeenCalledWith(
        'https://gitlab.com/api/v4/projects/123/merge_requests/456/approve',
        expect.objectContaining({
          sha: 'commit-sha',
        }),
        expect.any(Object)
      );
    });
  });
});
```

### 4.2 Integration Tests

```typescript
describe('GitLab Merge Request Integration', () => {
  let mrManager: GitLabMergeRequestManager;
  const testProjectId = parseInt(process.env.GITLAB_TEST_PROJECT_ID || '0');

  beforeAll(async () => {
    if (!testProjectId) {
      throw new Error('GITLAB_TEST_PROJECT_ID environment variable required');
    }

    mrManager = new GitLabMergeRequestManager(
      process.env.GITLAB_TEST_URL || 'https://gitlab.com',
      new HttpClient(),
      new GitLabWebhookManager(
        process.env.GITLAB_TEST_URL || 'https://gitlab.com',
        new HttpClient()
      )
    );
  });

  it('should create and manage merge request lifecycle', async () => {
    // Create a test branch first (this would be done by the workflow)
    const sourceBranch = `test-mr-${Date.now()}`;

    // Create merge request
    const mergeRequest = await mrManager.createMergeRequest(testProjectId, {
      sourceBranch,
      targetBranch: 'main',
      title: 'Integration Test MR',
      description: 'This is a test merge request for integration testing',
      labels: ['integration-test'],
    });

    expect(mergeRequest.id).toBeDefined();
    expect(mergeRequest.state).toBe('opened');

    // Get merge request details
    const retrievedMR = await mrManager.getMergeRequest(testProjectId, mergeRequest.iid);
    expect(retrievedMR.id).toBe(mergeRequest.id);

    // Update merge request
    const updatedMR = await mrManager.updateMergeRequest(testProjectId, mergeRequest.iid, {
      description: 'Updated description for integration test',
    });
    expect(updatedMR.description).toContain('Updated description');

    // Clean up - close the merge request
    await mrManager.updateMergeRequest(testProjectId, mergeRequest.iid, {
      stateId: 'close',
    });
  });

  it('should handle merge request discussions', async () => {
    // Create a test merge request
    const mergeRequest = await mrManager.createMergeRequest(testProjectId, {
      sourceBranch: 'main',
      targetBranch: 'main',
      title: 'Discussion Test MR',
      description: 'Test MR for discussion functionality',
    });

    // Add a note
    const note = await mrManager.addMergeRequestNote(
      testProjectId,
      mergeRequest.iid,
      'This is a test comment from integration tests'
    );

    expect(note.id).toBeDefined();
    expect(note.body).toContain('test comment');

    // Get discussions
    const discussions = await mrManager.getMergeRequestDiscussions(testProjectId, mergeRequest.iid);
    expect(discussions.length).toBeGreaterThan(0);

    // Clean up
    await mrManager.updateMergeRequest(testProjectId, mergeRequest.iid, {
      stateId: 'close',
    });
  });
});
```

## Completion Checklist

- [ ] Implement GitLabMergeRequestManager class with all MR operations
- [ ] Implement GitLabMergeRequestWorkflowIntegration for autonomous workflows
- [ ] Add comprehensive unit tests for all MR functionality
- [ ] Add integration tests with GitLab MR test projects
- [ ] Add end-to-end tests for MR workflows
- [ ] Implement MR template system and variable substitution
- [ ] Add MR readiness evaluation and auto-merge capabilities
- [ ] Implement AI review integration for MR comments
- [ ] Update documentation with MR integration instructions
- [ ] Verify all acceptance criteria are met
- [ ] Ensure code coverage targets are achieved
- [ ] Validate webhook event handling and processing

## Dependencies

- Task 1: GitLabPlatform class implementation (for base integration)
- Task 2: Authentication handling (for API access)
- Task 3: CI/CD API integration (for pipeline status validation)
- GitLab test project with merge request permissions
- Test credentials with MR creation and approval permissions
- Merge request templates for automated content generation

## Estimated Time

**Implementation**: 5-6 days
**Testing**: 3-4 days
**Documentation**: 1 day
**Total**: 9-11 days
