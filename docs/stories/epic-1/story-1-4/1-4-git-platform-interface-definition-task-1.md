# Task 1: Design Core Git Platform Interface Structure

**Story**: 1-4 Git Platform Interface Definition  
**Task**: 1 of 6 - Design Core Git Platform Interface Structure  
**Epic**: 1 - Foundation & Core Infrastructure  
**Status**: Pending

## Task Description

Design the core `IGitPlatform` interface structure with method signatures for essential Git platform operations. This includes defining the main interface, repository operations, branch management, pull request workflows, and issue tracking capabilities.

## Acceptance Criteria

1. **IGitPlatform Interface**: Create comprehensive interface with all core Git platform operations
2. **Core Method Signatures**: Define method signatures for repository, branch, PR, and issue operations
3. **Data Structures**: Define TypeScript interfaces for repository, branch, PR, and issue data structures
4. **Type Documentation**: Add comprehensive TypeScript documentation for all interface methods
5. **Interface Extensibility**: Design interface to support future platform additions and feature extensions

## Implementation Details

### 1. Core IGitPlatform Interface Design

```typescript
/**
 * Main interface for Git platform operations
 * Provides unified abstraction for GitHub, GitLab, Gitea, Forgejo, and other Git hosting platforms
 */
interface IGitPlatform {
  /** Platform identifier (e.g., 'github', 'gitlab', 'gitea') */
  readonly platformName: string;

  /** Platform version/API version being used */
  readonly apiVersion: string;

  // Repository Operations
  /**
   * Get repository information
   * @param owner Repository owner/organization
   * @param repo Repository name
   * @returns Repository metadata and configuration
   */
  getRepository(owner: string, repo: string): Promise<Repository>;

  /**
   * Create a new repository
   * @param owner Repository owner/organization
   * @param repo Repository name
   * @param options Repository creation options
   * @returns Created repository information
   */
  createRepository(
    owner: string,
    repo: string,
    options: CreateRepositoryOptions
  ): Promise<Repository>;

  // Branch Operations
  /**
   * Get branch information
   * @param owner Repository owner
   * @param repo Repository name
   * @param branch Branch name
   * @returns Branch information with latest commit
   */
  getBranch(owner: string, repo: string, branch: string): Promise<Branch>;

  /**
   * Create a new branch
   * @param owner Repository owner
   * @param repo Repository name
   * @param branch New branch name
   * @param fromBranch Source branch to create from
   * @returns Created branch information
   */
  createBranch(owner: string, repo: string, branch: string, fromBranch: string): Promise<Branch>;

  /**
   * Delete a branch
   * @param owner Repository owner
   * @param repo Repository name
   * @param branch Branch name to delete
   */
  deleteBranch(owner: string, repo: string, branch: string): Promise<void>;

  // Pull Request/Merge Request Operations
  /**
   * Create a pull request (or merge request)
   * @param owner Repository owner
   * @param repo Repository name
   * @param options Pull request creation options
   * @returns Created pull request information
   */
  createPR(owner: string, repo: string, options: CreatePROptions): Promise<PullRequest>;

  /**
   * Get pull request details
   * @param owner Repository owner
   * @param repo Repository name
   * @param prNumber Pull request number
   * @returns Pull request details with metadata
   */
  getPR(owner: string, repo: string, prNumber: number): Promise<PullRequest>;

  /**
   * List pull requests with filtering and pagination
   * @param owner Repository owner
   * @param repo Repository name
   * @param options List options (state, filters, pagination)
   * @returns Paginated list of pull requests
   */
  listPRs(
    owner: string,
    repo: string,
    options?: ListPROptions
  ): Promise<PaginatedResponse<PullRequest>>;

  /**
   * Add comment to pull request
   * @param owner Repository owner
   * @param repo Repository name
   * @param prNumber Pull request number
   * @param comment Comment content
   * @returns Created comment information
   */
  commentOnPR(owner: string, repo: string, prNumber: number, comment: string): Promise<Comment>;

  /**
   * Merge pull request
   * @param owner Repository owner
   * @param repo Repository name
   * @param prNumber Pull request number
   * @param options Merge options (method, commit message)
   * @returns Merge result information
   */
  mergePR(
    owner: string,
    repo: string,
    prNumber: number,
    options?: MergePROptions
  ): Promise<MergeResult>;

  // Issue Operations
  /**
   * Get issue details
   * @param owner Repository owner
   * @param repo Repository name
   * @param issueNumber Issue number
   * @returns Issue details with metadata
   */
  getIssue(owner: string, repo: string, issueNumber: number): Promise<Issue>;

  /**
   * List issues with filtering and pagination
   * @param owner Repository owner
   * @param repo Repository name
   * @param options List options (state, filters, pagination)
   * @returns Paginated list of issues
   */
  listIssues(
    owner: string,
    repo: string,
    options?: ListIssuesOptions
  ): Promise<PaginatedResponse<Issue>>;

  /**
   * Create a new issue
   * @param owner Repository owner
   * @param repo Repository name
   * @param options Issue creation options
   * @returns Created issue information
   */
  createIssue(owner: string, repo: string, options: CreateIssueOptions): Promise<Issue>;

  /**
   * Add comment to issue
   * @param owner Repository owner
   * @param repo Repository name
   * @param issueNumber Issue number
   * @param comment Comment content
   * @returns Created comment information
   */
  commentOnIssue(
    owner: string,
    repo: string,
    issueNumber: number,
    comment: string
  ): Promise<Comment>;

  // CI/CD Operations
  /**
   * Trigger CI/CD pipeline for repository
   * @param owner Repository owner
   * @param repo Repository name
   * @param options CI trigger options (branch, workflow, parameters)
   * @returns CI trigger result
   */
  triggerCI(owner: string, repo: string, options: TriggerCIOptions): Promise<CITriggerResult>;

  /**
   * Get CI/CD status for commit or pull request
   * @param owner Repository owner
   * @param repo Repository name
   * @param ref Git reference (commit SHA, branch, or PR)
   * @returns CI status information
   */
  getCIStatus(owner: string, repo: string, ref: string): Promise<CIStatus>;

  // Webhook Operations
  /**
   * Create webhook for repository
   * @param owner Repository owner
   * @param repo Repository name
   * @param options Webhook creation options
   * @returns Created webhook information
   */
  createWebhook(owner: string, repo: string, options: CreateWebhookOptions): Promise<Webhook>;

  /**
   * List webhooks for repository
   * @param owner Repository owner
   * @param repo Repository name
   * @returns List of repository webhooks
   */
  listWebhooks(owner: string, repo: string): Promise<Webhook[]>;

  /**
   * Delete webhook
   * @param owner Repository owner
   * @param repo Repository name
   * @param webhookId Webhook identifier
   */
  deleteWebhook(owner: string, repo: string, webhookId: string): Promise<void>;

  // Platform Capabilities (detailed in Task 2)
  /**
   * Get platform capabilities and supported features
   * @returns Platform capabilities information
   */
  getCapabilities(): Promise<PlatformCapabilities>;
}
```

### 2. Core Data Structures

```typescript
/**
 * Repository information and configuration
 */
interface Repository {
  /** Unique repository identifier */
  id: string;

  /** Repository name */
  name: string;

  /** Repository full name (owner/repo) */
  fullName: string;

  /** Repository owner/organization */
  owner: {
    login: string;
    type: 'user' | 'organization';
    id: string;
  };

  /** Repository description */
  description?: string;

  /** Default branch name */
  defaultBranch: string;

  /** Repository visibility */
  private: boolean;

  /** Repository URL */
  url: string;

  /** Clone URLs */
  urls: {
    https: string;
    ssh?: string;
    git?: string;
  };

  /** Repository configuration */
  config: {
    allowMergeCommits: boolean;
    allowSquashMerge: boolean;
    allowRebaseMerge: boolean;
    deleteBranchOnMerge: boolean;
  };

  /** Repository metadata */
  metadata: {
    createdAt: string;
    updatedAt: string;
    pushedAt?: string;
    language?: string;
    size: number;
    stars: number;
    forks: number;
    openIssues: number;
  };

  /** Platform-specific data */
  platformData?: Record<string, unknown>;
}

/**
 * Branch information
 */
interface Branch {
  /** Branch name */
  name: string;

  /** Latest commit information */
  commit: {
    sha: string;
    message: string;
    author: {
      name: string;
      email: string;
      date: string;
    };
    url: string;
  };

  /** Branch protection status */
  protected: boolean;

  /** Default branch flag */
  default: boolean;

  /** Platform-specific data */
  platformData?: Record<string, unknown>;
}

/**
 * Pull Request/Merge Request information
 */
interface PullRequest {
  /** Pull request number */
  number: number;

  /** Pull request title */
  title: string;

  /** Pull request description/body */
  body?: string;

  /** Pull request state */
  state: 'open' | 'closed' | 'merged';

  /** Pull request status */
  status: 'draft' | 'ready_for_review' | 'in_review' | 'approved' | 'changes_requested';

  /** Source branch */
  head: {
    ref: string;
    sha: string;
    repo: Repository;
  };

  /** Target branch */
  base: {
    ref: string;
    sha: string;
    repo: Repository;
  };

  /** Author information */
  author: {
    login: string;
    type: 'user' | 'bot';
    id: string;
  };

  /** Assignees */
  assignees: Array<{
    login: string;
    type: 'user' | 'bot';
    id: string;
  }>;

  /** Reviewers */
  reviewers: Array<{
    login: string;
    type: 'user' | 'bot';
    id: string;
    state: 'approved' | 'changes_requested' | 'commented';
  }>;

  /** Labels */
  labels: Array<{
    name: string;
    color?: string;
    description?: string;
  }>;

  /** Merge information */
  merge?: {
    merged: boolean;
    mergedAt?: string;
    mergedBy?: {
      login: string;
      id: string;
    };
    mergeCommitSha?: string;
  };

  /** CI/CD status */
  ciStatus?: CIStatus;

  /** Pull request URLs */
  urls: {
    html: string;
    api: string;
    diff?: string;
    patch?: string;
  };

  /** Timestamps */
  timestamps: {
    createdAt: string;
    updatedAt: string;
    closedAt?: string;
    mergedAt?: string;
  };

  /** Platform-specific data */
  platformData?: Record<string, unknown>;
}

/**
 * Issue information
 */
interface Issue {
  /** Issue number */
  number: number;

  /** Issue title */
  title: string;

  /** Issue description/body */
  body?: string;

  /** Issue state */
  state: 'open' | 'closed';

  /** Author information */
  author: {
    login: string;
    type: 'user' | 'bot';
    id: string;
  };

  /** Assignees */
  assignees: Array<{
    login: string;
    type: 'user' | 'bot';
    id: string;
  }>;

  /** Labels */
  labels: Array<{
    name: string;
    color?: string;
    description?: string;
  }>;

  /** Milestone */
  milestone?: {
    number: number;
    title: string;
    state: 'open' | 'closed';
  };

  /** Issue URLs */
  urls: {
    html: string;
    api: string;
  };

  /** Timestamps */
  timestamps: {
    createdAt: string;
    updatedAt: string;
    closedAt?: string;
  };

  /** Platform-specific data */
  platformData?: Record<string, unknown>;
}

/**
 * Comment information (for PRs and issues)
 */
interface Comment {
  /** Comment unique identifier */
  id: string;

  /** Comment body/content */
  body: string;

  /** Author information */
  author: {
    login: string;
    type: 'user' | 'bot';
    id: string;
  };

  /** Comment creation timestamp */
  createdAt: string;

  /** Comment update timestamp */
  updatedAt: string;

  /** Comment URLs */
  urls: {
    html: string;
    api: string;
  };

  /** Platform-specific data */
  platformData?: Record<string, unknown>;
}
```

### 3. Operation Options and Parameters

```typescript
/**
 * Repository creation options
 */
interface CreateRepositoryOptions {
  /** Repository description */
  description?: string;

  /** Repository visibility */
  private?: boolean;

  /** Initialize with README */
  autoInit?: boolean;

  /** Default branch name */
  defaultBranch?: string;

  /** Git ignore template */
  gitignoreTemplate?: string;

  /** License template */
  licenseTemplate?: string;
}

/**
 * Pull request creation options
 */
interface CreatePROptions {
  /** Pull request title */
  title: string;

  /** Pull request description/body */
  body?: string;

  /** Source branch */
  head: string;

  /** Target branch */
  base: string;

  /** Draft pull request */
  draft?: boolean;

  /** Assignees */
  assignees?: string[];

  /** Reviewers */
  reviewers?: string[];

  /** Labels */
  labels?: string[];
}

/**
 * Pull request list options
 */
interface ListPROptions {
  /** Pull request state */
  state?: 'open' | 'closed' | 'all';

  /** Head branch filter */
  head?: string;

  /** Base branch filter */
  base?: string;

  /** Author filter */
  author?: string;

  /** Label filter */
  labels?: string[];

  /** Sort order */
  sort?: 'created' | 'updated' | 'popularity';

  /** Sort direction */
  direction?: 'asc' | 'desc';

  /** Pagination options */
  pagination?: PaginationOptions;
}

/**
 * Merge pull request options
 */
interface MergePROptions {
  /** Merge method */
  method?: 'merge' | 'squash' | 'rebase';

  /** Merge commit title */
  commitTitle?: string;

  /** Merge commit message */
  commitMessage?: string;

  /** Remove source branch after merge */
  deleteBranch?: boolean;
}

/**
 * Issue creation options
 */
interface CreateIssueOptions {
  /** Issue title */
  title: string;

  /** Issue description/body */
  body?: string;

  /** Assignees */
  assignees?: string[];

  /** Labels */
  labels?: string[];

  /** Milestone */
  milestone?: number;
}

/**
 * Issue list options
 */
interface ListIssuesOptions {
  /** Issue state */
  state?: 'open' | 'closed' | 'all';

  /** Author filter */
  author?: string;

  /** Assignee filter */
  assignee?: string;

  /** Label filter */
  labels?: string[];

  /** Milestone filter */
  milestone?: string;

  /** Sort order */
  sort?: 'created' | 'updated' | 'comments';

  /** Sort direction */
  direction?: 'asc' | 'desc';

  /** Pagination options */
  pagination?: PaginationOptions;
}

/**
 * CI/CD trigger options
 */
interface TriggerCIOptions {
  /** Git reference (branch, tag, or commit SHA) */
  ref: string;

  /** Workflow name or identifier */
  workflow?: string;

  /** Input parameters for workflow */
  inputs?: Record<string, string>;
}

/**
 * Webhook creation options
 */
interface CreateWebhookOptions {
  /** Webhook URL */
  url: string;

  /** Content type */
  contentType?: 'json' | 'form';

  /** Secret for webhook validation */
  secret?: string;

  /** Events to subscribe to */
  events: string[];

  /** Active webhook flag */
  active?: boolean;
}
```

### 4. Response Types and Utilities

```typescript
/**
 * Paginated response wrapper
 */
interface PaginatedResponse<T> {
  /** Response items */
  data: T[];

  /** Pagination information */
  pagination: {
    /** Current page number */
    page?: number;

    /** Items per page */
    perPage?: number;

    /** Total items count */
    totalCount?: number;

    /** Total pages count */
    totalPages?: number;

    /** Has next page */
    hasNext: boolean;

    /** Has previous page */
    hasPrev: boolean;

    /** Next page cursor */
    nextCursor?: string;

    /** Previous page cursor */
    prevCursor?: string;
  };
}

/**
 * Pagination options
 */
interface PaginationOptions {
  /** Page number (for page-based pagination) */
  page?: number;

  /** Items per page */
  perPage?: number;

  /** Cursor (for cursor-based pagination) */
  cursor?: string;

  /** Limit (for offset-based pagination) */
  limit?: number;

  /** Offset (for offset-based pagination) */
  offset?: number;
}

/**
 * Merge result information
 */
interface MergeResult {
  /** Merge success flag */
  merged: boolean;

  /** Merge commit SHA */
  commitSha?: string;

  /** Merge message */
  message?: string;

  /** Merge method used */
  method?: 'merge' | 'squash' | 'rebase';

  /** Platform-specific data */
  platformData?: Record<string, unknown>;
}

/**
 * CI/CD trigger result
 */
interface CITriggerResult {
  /** Trigger success flag */
  triggered: boolean;

  /** CI/CD run identifier */
  runId?: string;

  /** CI/CD run URL */
  runUrl?: string;

  /** Platform-specific data */
  platformData?: Record<string, unknown>;
}

/**
 * CI/CD status information
 */
interface CIStatus {
  /** Overall status */
  status: 'pending' | 'running' | 'success' | 'failure' | 'cancelled';

  /** Conclusion (for completed runs) */
  conclusion?: 'success' | 'failure' | 'neutral' | 'cancelled' | 'timed_out' | 'action_required';

  /** CI/CD run identifier */
  runId?: string;

  /** CI/CD run URL */
  runUrl?: string;

  /** Individual job statuses */
  jobs?: Array<{
    name: string;
    status: 'pending' | 'running' | 'success' | 'failure' | 'cancelled';
    conclusion?: 'success' | 'failure' | 'neutral' | 'cancelled' | 'timed_out' | 'action_required';
    url?: string;
  }>;

  /** Timestamps */
  timestamps: {
    startedAt?: string;
    completedAt?: string;
  };

  /** Platform-specific data */
  platformData?: Record<string, unknown>;
}

/**
 * Webhook information
 */
interface Webhook {
  /** Webhook unique identifier */
  id: string;

  /** Webhook URL */
  url: string;

  /** Content type */
  contentType: string;

  /** Events subscribed to */
  events: string[];

  /** Active webhook flag */
  active: boolean;

  /** Last delivery information */
  lastDelivery?: {
    timestamp: string;
    status: 'success' | 'failure';
    statusCode?: number;
  };

  /** Creation timestamp */
  createdAt: string;

  /** Update timestamp */
  updatedAt: string;

  /** Platform-specific data */
  platformData?: Record<string, unknown>;
}
```

## File Structure

```
packages/platforms/src/
├── types/
│   ├── index.ts                                      # Main type exports
│   ├── git-platform.interface.ts                    # IGitPlatform interface
│   ├── repository.types.ts                          # Repository-related types
│   ├── pull-request.types.ts                        # PR-related types
│   ├── issue.types.ts                               # Issue-related types
│   ├── branch.types.ts                              # Branch-related types
│   ├── ci.types.ts                                  # CI/CD-related types
│   ├── webhook.types.ts                             # Webhook-related types
│   └── common.types.ts                              # Common utility types
├── contracts/
│   └── git-platform.contract.ts                     # Interface contract tests
└── __tests__/
    ├── git-platform.interface.test.ts               # Interface tests
    └── type-validation.test.ts                      # Type validation tests
```

## Testing Strategy

**Interface Contract Testing**:

- Define interface contracts using TypeScript
- Create mock implementations for testing
- Validate method signatures and return types
- Test interface extensibility and compatibility

**Type Validation**:

- Validate all interface definitions
- Test type safety and generic usage
- Ensure proper documentation coverage
- Validate platform data extensibility

**Success Metrics**:

- All interfaces properly defined with TypeScript
- Comprehensive documentation for all methods
- Type safety validated through compilation
- Interface extensibility demonstrated

## Completion Checklist

- [ ] Create IGitPlatform interface with all core method signatures
- [ ] Define Repository, Branch, PullRequest, and Issue interfaces
- [ ] Create operation options and parameter interfaces
- [ ] Define response types and utility interfaces
- [ ] Add comprehensive TypeScript documentation
- [ ] Create interface contract tests
- [ ] Validate type safety and compilation
- [ ] Document interface extensibility patterns
- [ ] Create type exports and index file
- [ ] Review interface design for completeness

## Dependencies

- Task 2: Platform Capabilities Discovery (extends interface with capabilities)
- Task 3: Data Model Normalization (refines data structures)
- Task 4: Pagination and Rate Limit Support (adds pagination interfaces)

## Risks and Mitigations

**Risk**: Interface too complex or over-engineered
**Mitigation**: Start with essential operations, extend based on requirements

**Risk**: Platform-specific differences hard to abstract
**Mitigation**: Use platformData field for platform-specific extensions

**Risk**: Interface changes break existing implementations
**Mitigation**: Design for extensibility, version interfaces carefully

## Success Criteria

- Comprehensive IGitPlatform interface covering all essential operations
- Well-defined data structures for repositories, branches, PRs, and issues
- Complete TypeScript documentation for all interface members
- Type-safe interface design validated through compilation
- Extensible interface design supporting future platform additions

This task establishes the foundation for Git platform abstraction in Tamma, enabling unified operations across multiple Git hosting services.
