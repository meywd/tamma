# Task 1: Implement GitLabPlatform Class with IGitPlatform Interface

**Story**: 1-6 GitLab Platform Implementation  
**Task**: 1 of 6 - Implement GitLabPlatform Class with IGitPlatform Interface  
**Epic**: 1 - Foundation & Core Infrastructure  
**Status**: Pending

## Task Description

Implement the GitLabPlatform class that implements the IGitPlatform interface, providing complete GitLab API integration. This includes setting up the @gitbeaker/node SDK dependency and implementing all core platform operations for repositories, branches, merge requests, and issues.

## Acceptance Criteria

1. **SDK Integration**: Set up @gitbeaker/node SDK dependency with proper configuration
2. **Core Platform Operations**: Implement repository operations (getRepository, createRepository)
3. **Branch Operations**: Implement branch management (createBranch, getBranch, deleteBranch)
4. **Merge Request Operations**: Implement MR workflows (createPullRequest, getPullRequest, mergePullRequest)
5. **Issue Operations**: Implement issue tracking (getIssue, listIssues, createIssue)
6. **Interface Compliance**: Full compliance with IGitPlatform interface from Story 1.4

## Implementation Details

### 1. SDK Setup and Configuration

```typescript
// packages/platforms/src/gitlab/gitlab-sdk.ts
import { Gitlab } from '@gitbeaker/node';
import { GitlabConfig } from './gitlab.types';

/**
 * GitLab SDK wrapper with configuration and authentication
 */
export class GitLabSDK {
  private client: Gitlab;

  constructor(private config: GitlabConfig) {
    this.client = new Gitlab({
      host: config.api.baseUrl,
      token: config.auth.token,
      rejectUnauthorized: config.api.rejectUnauthorized ?? true,
      requestTimeout: config.api.timeout ?? 30000,
    });
  }

  /**
   * Get the underlying GitLab client
   */
  getClient(): Gitlab {
    return this.client;
  }

  /**
   * Test connection to GitLab API
   */
  async testConnection(): Promise<boolean> {
    try {
      await this.client.Users.current();
      return true;
    } catch (error) {
      throw new Error(`GitLab connection failed: ${error.message}`);
    }
  }

  /**
   * Get current authenticated user
   */
  async getCurrentUser(): Promise<GitLabUser> {
    return this.client.Users.current();
  }
}
```

### 2. Main GitLab Platform Class

```typescript
// packages/platforms/src/gitlab/gitlab.platform.ts
import {
  IGitPlatform,
  Repository,
  Branch,
  PullRequest,
  Issue,
  Comment,
  CreateRepositoryOptions,
  CreatePROptions,
  CreateIssueOptions,
  ListPROptions,
  ListIssuesOptions,
  MergePROptions,
  PaginatedResponse,
  PlatformCapabilities,
  UnifiedCIStatus,
  Webhook,
  CreateWebhookOptions,
  TriggerCIOptions,
  CITriggerResult,
  MergeResult,
} from '../types';
import { GitLabSDK } from './gitlab-sdk';
import { GitLabTransformer } from './gitlab.transformer';
import { GitLabConfig } from './gitlab.types';
import { RateLimitHandler } from '../rate-limit';
import { PaginationUtil } from '../pagination';

/**
 * GitLab platform implementation
 * Implements IGitPlatform interface for GitLab integration
 */
export class GitLabPlatform implements IGitPlatform {
  readonly platformName = 'gitlab';
  readonly apiVersion = 'v4';

  constructor(
    private config: GitLabConfig,
    private sdk: GitLabSDK,
    private transformer: GitLabTransformer,
    private rateLimitHandler: RateLimitHandler,
    private paginationUtil: PaginationUtil
  ) {}

  // Repository Operations
  async getRepository(owner: string, repo: string): Promise<Repository> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();

      // GitLab uses project ID or path with namespace
      const projectPath = `${owner}/${repo}`;
      const gitlabProject = await client.Projects.show(projectPath);

      return this.transformer.transformRepository(gitlabProject);
    });
  }

  async createRepository(
    owner: string,
    repo: string,
    options: CreateRepositoryOptions
  ): Promise<Repository> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();

      const createOptions = this.transformer.transformCreateRepositoryOptionsToGitLab(options);

      // Create project under namespace (owner)
      const gitlabProject = await client.Projects.create({
        name: repo,
        path: repo,
        namespace_id: await this.getNamespaceId(owner),
        ...createOptions,
      });

      return this.transformer.transformRepository(gitlabProject);
    });
  }

  // Branch Operations
  async getBranch(owner: string, repo: string, branch: string): Promise<Branch> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();
      const projectPath = `${owner}/${repo}`;

      const gitlabBranch = await client.Branches.show(projectPath, branch);

      return this.transformer.transformBranch(gitlabBranch);
    });
  }

  async createBranch(
    owner: string,
    repo: string,
    branch: string,
    fromBranch: string
  ): Promise<Branch> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();
      const projectPath = `${owner}/${repo}`;

      const gitlabBranch = await client.Branches.create(projectPath, branch, fromBranch);

      return this.transformer.transformBranch(gitlabBranch);
    });
  }

  async deleteBranch(owner: string, repo: string, branch: string): Promise<void> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();
      const projectPath = `${owner}/${repo}`;

      await client.Branches.remove(projectPath, branch);
    });
  }

  // Pull Request (Merge Request) Operations
  async createPR(owner: string, repo: string, options: CreatePROptions): Promise<PullRequest> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();
      const projectPath = `${owner}/${repo}`;

      const createOptions = this.transformer.transformCreatePROptionsToGitLab(options);

      const gitlabMR = await client.MergeRequests.create(projectPath, createOptions);

      return this.transformer.transformPullRequest(gitlabMR);
    });
  }

  async getPR(owner: string, repo: string, prNumber: number): Promise<PullRequest> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();
      const projectPath = `${owner}/${repo}`;

      const gitlabMR = await client.MergeRequests.show(projectPath, prNumber);

      return this.transformer.transformPullRequest(gitlabMR);
    });
  }

  async listPRs(
    owner: string,
    repo: string,
    options?: ListPROptions
  ): Promise<PaginatedResponse<PullRequest>> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();
      const projectPath = `${owner}/${repo}`;

      const paginationOptions = this.paginationUtil.createPaginationOptions(
        options?.pagination || { page: 1, size: 20 }
      );

      const listOptions = this.buildListPROptions(options, paginationOptions);

      const gitlabMRs = await client.MergeRequests.all({
        projectId: projectPath,
        ...listOptions,
      });

      const unifiedPRs = gitlabMRs.map((mr) => this.transformer.transformPullRequest(mr));

      const paginationInfo = this.extractPaginationInfo(gitlabMRs, paginationOptions);

      return {
        data: unifiedPRs,
        pagination: paginationInfo,
      };
    });
  }

  async commentOnPR(
    owner: string,
    repo: string,
    prNumber: number,
    comment: string
  ): Promise<Comment> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();
      const projectPath = `${owner}/${repo}`;

      const gitlabNote = await client.MergeRequestNotes.create(projectPath, prNumber, {
        body: comment,
      });

      return this.transformer.transformComment(gitlabNote);
    });
  }

  async mergePR(
    owner: string,
    repo: string,
    prNumber: number,
    options?: MergePROptions
  ): Promise<MergeResult> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();
      const projectPath = `${owner}/${repo}`;

      const mergeOptions = this.transformer.transformMergePROptionsToGitLab(options);

      try {
        const result = await client.MergeRequests.accept(projectPath, prNumber, mergeOptions);

        return this.transformer.transformMergeResult(result);
      } catch (error) {
        if (error.message.includes('Cannot be merged')) {
          return {
            merged: false,
            canMerge: false,
            message: 'Merge request cannot be merged',
          };
        }
        throw error;
      }
    });
  }

  // Issue Operations
  async getIssue(owner: string, repo: string, issueNumber: number): Promise<Issue> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();
      const projectPath = `${owner}/${repo}`;

      const gitlabIssue = await client.Issues.show(projectPath, issueNumber);

      return this.transformer.transformIssue(gitlabIssue);
    });
  }

  async listIssues(
    owner: string,
    repo: string,
    options?: ListIssuesOptions
  ): Promise<PaginatedResponse<Issue>> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();
      const projectPath = `${owner}/${repo}`;

      const paginationOptions = this.paginationUtil.createPaginationOptions(
        options?.pagination || { page: 1, size: 20 }
      );

      const listOptions = this.buildListIssuesOptions(options, paginationOptions);

      const gitlabIssues = await client.Issues.all({
        projectId: projectPath,
        ...listOptions,
      });

      const unifiedIssues = gitlabIssues.map((issue) => this.transformer.transformIssue(issue));

      const paginationInfo = this.extractPaginationInfo(gitlabIssues, paginationOptions);

      return {
        data: unifiedIssues,
        pagination: paginationInfo,
      };
    });
  }

  async createIssue(owner: string, repo: string, options: CreateIssueOptions): Promise<Issue> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();
      const projectPath = `${owner}/${repo}`;

      const createOptions = this.transformer.transformCreateIssueOptionsToGitLab(options);

      const gitlabIssue = await client.Issues.create(projectPath, createOptions);

      return this.transformer.transformIssue(gitlabIssue);
    });
  }

  async commentOnIssue(
    owner: string,
    repo: string,
    issueNumber: number,
    comment: string
  ): Promise<Comment> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();
      const projectPath = `${owner}/${repo}`;

      const gitlabNote = await client.IssueNotes.create(projectPath, issueNumber, {
        body: comment,
      });

      return this.transformer.transformComment(gitlabNote);
    });
  }

  // CI/CD Operations
  async triggerCI(
    owner: string,
    repo: string,
    options: TriggerCIOptions
  ): Promise<CITriggerResult> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();
      const projectPath = `${owner}/${repo}`;

      const triggerOptions = this.transformer.transformTriggerCIOptionsToGitLab(options);

      const pipeline = await client.Pipelines.trigger(projectPath, triggerOptions);

      return this.transformer.transformCITriggerResult(pipeline);
    });
  }

  async getCIStatus(owner: string, repo: string, ref: string): Promise<UnifiedCIStatus> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();
      const projectPath = `${owner}/${repo}`;

      // Get pipelines for the ref
      const pipelines = await client.Pipelines.all({
        projectId: projectPath,
        ref: ref,
        perPage: 1,
      });

      if (pipelines.length === 0) {
        return {
          status: 'pending' as const,
          checks: [],
          summary: {
            totalChecks: 0,
            passedChecks: 0,
            failedChecks: 0,
            pendingChecks: 0,
            skippedChecks: 0,
          },
          platformData: {
            platform: 'gitlab',
            raw: {},
            features: {},
            metadata: {},
          },
        };
      }

      const latestPipeline = pipelines[0];
      return this.transformer.transformCIStatus(latestPipeline);
    });
  }

  // Webhook Operations
  async createWebhook(
    owner: string,
    repo: string,
    options: CreateWebhookOptions
  ): Promise<Webhook> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();
      const projectPath = `${owner}/${repo}`;

      const webhookOptions = this.transformer.transformCreateWebhookOptionsToGitLab(options);

      const gitlabHook = await client.ProjectHooks.add(projectPath, webhookOptions);

      return this.transformer.transformWebhook(gitlabHook);
    });
  }

  async listWebhooks(owner: string, repo: string): Promise<Webhook[]> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();
      const projectPath = `${owner}/${repo}`;

      const gitlabHooks = await client.ProjectHooks.all(projectPath);

      return gitlabHooks.map((hook) => this.transformer.transformWebhook(hook));
    });
  }

  async deleteWebhook(owner: string, repo: string, webhookId: string): Promise<void> {
    return this.executeWithRateLimit(async () => {
      const client = this.sdk.getClient();
      const projectPath = `${owner}/${repo}`;

      await client.ProjectHooks.remove(projectPath, parseInt(webhookId));
    });
  }

  // Platform Capabilities
  async getCapabilities(): Promise<PlatformCapabilities> {
    return this.transformer.getPlatformCapabilities();
  }

  // Helper Methods
  private async executeWithRateLimit<T>(operation: () => Promise<T>): Promise<T> {
    return this.rateLimitHandler.executeWithRateLimit(operation);
  }

  private async getNamespaceId(owner: string): Promise<number> {
    const client = this.sdk.getClient();

    try {
      // Try to get namespace by path
      const namespace = await client.Namespaces.show(owner);
      return namespace.id;
    } catch (error) {
      // If not found, try to get user/group by username
      try {
        const user = await client.Users.show(owner);
        return user.id;
      } catch (userError) {
        throw new Error(`Namespace or user '${owner}' not found: ${error.message}`);
      }
    }
  }

  private buildListPROptions(options?: ListPROptions, pagination?: any): any {
    const listOptions: any = {};

    if (options?.state) {
      listOptions.state = options.state === 'open' ? 'opened' : options.state;
    }

    if (pagination?.offset) {
      listOptions.page = Math.floor(pagination.offset / pagination.limit) + 1;
      listOptions.perPage = pagination.limit;
    } else if (pagination?.page) {
      listOptions.page = pagination.page.number;
      listOptions.perPage = pagination.page.size;
    }

    return listOptions;
  }

  private buildListIssuesOptions(options?: ListIssuesOptions, pagination?: any): any {
    const listOptions: any = {};

    if (options?.state) {
      listOptions.state = options.state === 'open' ? 'opened' : options.state;
    }

    if (options?.labels && options.labels.length > 0) {
      listOptions.labels = options.labels.join(',');
    }

    if (pagination?.offset) {
      listOptions.page = Math.floor(pagination.offset / pagination.limit) + 1;
      listOptions.perPage = pagination.limit;
    } else if (pagination?.page) {
      listOptions.page = pagination.page.number;
      listOptions.perPage = pagination.page.size;
    }

    return listOptions;
  }

  private extractPaginationInfo(items: any[], pagination?: any): any {
    // GitLab doesn't provide pagination info in the response itself
    // We need to extract it from headers or calculate based on request
    return {
      strategy: 'offset_based' as const,
      offset: {
        current: pagination?.offset || 0,
        limit: pagination?.limit || 20,
        hasMore: items.length === (pagination?.limit || 20),
      },
      totalCount: {
        accuracy: 'unknown' as const,
      },
    };
  }
}
```

### 3. GitLab Type Definitions

```typescript
// packages/platforms/src/gitlab/gitlab.types.ts
import { AuthenticationMethod } from '../types';

/**
 * GitLab platform configuration
 */
export interface GitLabConfig {
  /** Authentication configuration */
  auth: {
    /** Personal access token */
    token: string;

    /** Authentication method */
    method: AuthenticationMethod.PERSONAL_ACCESS_TOKEN;
  };

  /** API configuration */
  api: {
    /** GitLab instance base URL */
    baseUrl: string;

    /** Request timeout in milliseconds */
    timeout?: number;

    /** SSL certificate verification */
    rejectUnauthorized?: boolean;
  };

  /** Rate limiting configuration */
  rateLimit?: {
    /** Maximum requests per second */
    maxRequestsPerSecond?: number;

    /** Enable rate limiting */
    enabled?: boolean;
  };

  /** Pagination configuration */
  pagination?: {
    /** Default page size */
    defaultPageSize?: number;

    /** Maximum page size */
    maxPageSize?: number;
  };
}

/**
 * GitLab user information
 */
export interface GitLabUser {
  id: number;
  username: string;
  name: string;
  email: string;
  state: string;
  avatar_url: string;
  web_url: string;
}

/**
 * GitLab project information
 */
export interface GitLabProject {
  id: number;
  name: string;
  path: string;
  description?: string;
  namespace: {
    id: number;
    name: string;
    path: string;
    kind: 'group' | 'user';
    full_path: string;
  };
  visibility: 'public' | 'internal' | 'private';
  web_url: string;
  http_url_to_repo: string;
  ssh_url_to_repo: string;
  default_branch: string;
  star_count: number;
  forks_count: number;
  open_issues_count: number;
  created_at: string;
  last_activity_at: string;
}

/**
 * GitLab branch information
 */
export interface GitLabBranch {
  name: string;
  merged: boolean;
  protected: boolean;
  default: boolean;
  can_push: boolean;
  commit: {
    id: string;
    short_id: string;
    title: string;
    message: string;
    author_name: string;
    author_email: string;
    created_at: string;
  };
}

/**
 * GitLab merge request information
 */
export interface GitLabMergeRequest {
  id: number;
  iid: number;
  title: string;
  description?: string;
  state: 'opened' | 'closed' | 'locked' | 'merged';
  created_at: string;
  updated_at: string;
  merged_at?: string;
  closed_at?: string;
  target_branch: string;
  source_branch: string;
  source_project_id: number;
  target_project_id: number;
  author: GitLabUser;
  assignees?: GitLabUser[];
  reviewers?: GitLabUser[];
  participants?: GitLabUser[];
  merge_status: 'can_be_merged' | 'cannot_be_merged' | 'checking' | 'unchecked';
  merge_when_pipeline_succeeds?: boolean;
  squash?: boolean;
  web_url: string;
}

/**
 * GitLab issue information
 */
export interface GitLabIssue {
  id: number;
  iid: number;
  title: string;
  description?: string;
  state: 'opened' | 'closed';
  created_at: string;
  updated_at: string;
  closed_at?: string;
  author: GitLabUser;
  assignees?: GitLabUser[];
  labels?: string[];
  milestone?: {
    id: number;
    title: string;
  };
  web_url: string;
}

/**
 * GitLab note (comment) information
 */
export interface GitLabNote {
  id: number;
  body: string;
  author: GitLabUser;
  created_at: string;
  updated_at: string;
  system: boolean;
  noteable_type: 'Issue' | 'MergeRequest' | 'Commit' | 'Snippet';
}

/**
 * GitLab pipeline information
 */
export interface GitLabPipeline {
  id: number;
  iid: number;
  project_id: number;
  sha: string;
  ref: string;
  status: 'pending' | 'running' | 'success' | 'failed' | 'canceled' | 'skipped';
  source: string;
  created_at: string;
  updated_at: string;
  web_url: string;
}

/**
 * GitLab webhook information
 */
export interface GitLabProjectHook {
  id: number;
  url: string;
  created_at: string;
  push_events: boolean;
  issues_events: boolean;
  merge_requests_events: boolean;
  tag_push_events: boolean;
  note_events: boolean;
  job_events: boolean;
  pipeline_events: boolean;
  wiki_page_events: boolean;
  deployment_events: boolean;
  releases_events: boolean;
  enable_ssl_verification: boolean;
}
```

### 4. Package Dependencies

```json
// packages/platforms/package.json additions
{
  "dependencies": {
    "@gitbeaker/node": "^39.0.0",
    "@gitbeaker/rest": "^39.0.0"
  },
  "devDependencies": {
    "@types/node": "^20.0.0"
  }
}
```

## File Structure

```
packages/platforms/src/gitlab/
├── index.ts                                      # GitLab platform exports
├── gitlab.platform.ts                            # Main GitLab platform implementation
├── gitlab-sdk.ts                                # GitLab SDK wrapper
├── gitlab.types.ts                              # GitLab-specific types
├── gitlab.transformer.ts                        # GitLab data transformer
├── gitlab.config.ts                             # GitLab configuration
└── __tests__/
    ├── gitlab.platform.test.ts                   # Platform tests
    ├── gitlab-sdk.test.ts                       # SDK tests
    └── integration/
        └── gitlab.integration.test.ts           # Integration tests
```

## Testing Strategy

**Unit Testing**:

- Test all platform operations with mocked GitLab API
- Test error handling for API failures
- Test data transformation accuracy
- Test configuration validation

**Integration Testing**:

- Test with real GitLab instance using test credentials
- Test end-to-end workflows (MR creation, merge)
- Test authentication flows
- Test rate limiting behavior

**Mock Testing**:

- Mock @gitbeaker/node SDK responses
- Test error scenarios (network failures, API errors)
- Test pagination handling
- Test data transformation edge cases

## Completion Checklist

- [ ] Set up @gitbeaker/node SDK dependency
- [ ] Implement GitLabSDK wrapper class
- [ ] Create GitLabPlatform class with IGitPlatform interface
- [ ] Implement repository operations (getRepository, createRepository)
- [ ] Implement branch operations (createBranch, getBranch, deleteBranch)
- [ ] Implement MR operations (createPullRequest, getPullRequest, mergePullRequest)
- [ ] Implement issue operations (getIssue, listIssues, createIssue)
- [ ] Implement comment operations (commentOnPR, commentOnIssue)
- [ ] Implement CI/CD operations (triggerCI, getCIStatus)
- [ ] Implement webhook operations (createWebhook, listWebhooks, deleteWebhook)
- [ ] Add comprehensive error handling
- [ ] Create unit tests for all operations
- [ ] Add integration tests with real GitLab instance

## Dependencies

- Story 1.1: AI Provider Interface Definition (interface patterns)
- Story 1.4: Git Platform Interface Definition (IGitPlatform interface)
- Task 2: Authentication Handling (authentication implementation)
- Task 3: GitLab CI/CD Integration (CI operations)
- Task 4: GitLab Merge Request API (MR workflows)

## Risks and Mitigations

**Risk**: GitLab API differences from GitHub causing interface mismatch
**Mitigation**: Comprehensive transformer implementation, thorough testing

**Risk**: Self-hosted GitLab instances with different configurations
**Mitigation**: Flexible configuration options, extensive testing scenarios

**Risk**: Rate limiting differences between GitLab.com and self-hosted
**Mitigation**: Configurable rate limiting, adaptive handling

## Success Criteria

- Complete implementation of all IGitPlatform interface methods
- Successful integration with GitLab API using @gitbeaker/node
- Proper handling of GitLab-specific concepts (MR vs PR, namespaces)
- Comprehensive test coverage with both unit and integration tests
- Error handling that gracefully handles API failures and rate limits

This task establishes the foundation for GitLab integration in Tamma, enabling teams using GitLab to adopt the system without platform migration.
