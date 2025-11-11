# Task 3: Normalize Platform-Specific Data Models

**Story**: 1-4 Git Platform Interface Definition  
**Task**: 3 of 6 - Normalize Platform-Specific Data Models  
**Epic**: 1 - Foundation & Core Infrastructure  
**Status**: Pending

## Task Description

Create unified data models and transformation utilities to normalize platform-specific differences between GitHub, GitLab, Gitea, and Forgejo. This ensures consistent behavior across platforms while preserving platform-specific features through extensible data structures.

## Acceptance Criteria

1. **Unified Data Models**: Create normalized interfaces for PR, Issue, Branch, and CI status across all platforms
2. **Mapping Patterns**: Define transformation patterns for converting platform-specific data to unified models
3. **Transformation Utilities**: Implement utilities for data normalization and platform-specific mapping
4. **Platform Data Preservation**: Preserve platform-specific features through extensible platformData fields
5. **Bidirectional Transformation**: Support transformation both to and from platform-specific formats

## Implementation Details

### 1. Unified Data Model Design

```typescript
/**
 * Unified pull request model that normalizes differences between platforms
 */
interface UnifiedPullRequest {
  /** Universal identifiers */
  id: string;
  number: number;
  url: string;

  /** Basic information */
  title: string;
  body?: string;
  state: PRState;
  status: PRStatus;

  /** Timestamps (normalized to ISO 8601) */
  createdAt: string;
  updatedAt: string;
  closedAt?: string;
  mergedAt?: string;

  /** Branch information */
  head: PRBranch;
  base: PRBranch;

  /** People information */
  author: PRUser;
  assignees: PRUser[];
  reviewers: PRReviewer[];
  requestedReviewers: PRUser[];

  /** Labels and milestones */
  labels: PRLabel[];
  milestone?: PRMilestone;

  /** Merge information */
  merge: PRMergeInfo;

  /** Review information */
  reviews: PRReview[];

  /** CI/CD status */
  ciStatus: UnifiedCIStatus;

  /** Comments */
  comments: UnifiedComment[];

  /** Files changed */
  files: PRFile[];

  /** Activity statistics */
  stats: PRStats;

  /** Platform-specific data */
  platformData: PlatformSpecificData;
}

/**
 * Unified issue model
 */
interface UnifiedIssue {
  /** Universal identifiers */
  id: string;
  number: number;
  url: string;

  /** Basic information */
  title: string;
  body?: string;
  state: IssueState;

  /** Timestamps */
  createdAt: string;
  updatedAt: string;
  closedAt?: string;

  /** People information */
  author: PRUser;
  assignees: PRUser[];

  /** Labels and milestones */
  labels: PRLabel[];
  milestone?: PRMilestone;

  /** Issue relationships */
  linkedIssues: IssueLink[];
  subIssues?: UnifiedIssue[];
  parentIssue?: UnifiedIssue;

  /** Comments */
  comments: UnifiedComment[];

  /** Reactions */
  reactions: IssueReactions;

  /** Time tracking */
  timeTracking?: TimeTrackingInfo;

  /** Platform-specific data */
  platformData: PlatformSpecificData;
}

/**
 * Unified branch model
 */
interface UnifiedBranch {
  /** Branch information */
  name: string;
  protected: boolean;
  default: boolean;

  /** Latest commit */
  commit: BranchCommit;

  /** Branch protection */
  protection?: BranchProtection;

  /** Pull requests associated with branch */
  pullRequests: PRBranch[];

  /** Upstream information */
  upstream?: BranchUpstream;

  /** Platform-specific data */
  platformData: PlatformSpecificData;
}

/**
 * Unified CI/CD status model
 */
interface UnifiedCIStatus {
  /** Overall status */
  status: CIStatusState;
  conclusion?: CIConclusion;

  /** Run information */
  runId?: string;
  runUrl?: string;

  /** Timestamps */
  startedAt?: string;
  completedAt?: string;

  /** Individual status checks */
  checks: CIStatusCheck[];

  /** Summary information */
  summary: {
    totalChecks: number;
    passedChecks: number;
    failedChecks: number;
    pendingChecks: number;
    skippedChecks: number;
  };

  /** Platform-specific data */
  platformData: PlatformSpecificData;
}
```

### 2. Platform-Specific Data Structures

```typescript
/**
 * Platform-specific data container
 * Preserves platform-specific features while maintaining unified interface
 */
interface PlatformSpecificData {
  /** Platform identifier */
  platform: 'github' | 'gitlab' | 'gitea' | 'forgejo';

  /** Raw platform data */
  raw: Record<string, unknown>;

  /** Platform-specific features */
  features: {
    /** GitHub-specific features */
    github?: GitHubFeatures;

    /** GitLab-specific features */
    gitlab?: GitLabFeatures;

    /** Gitea-specific features */
    gitea?: GiteaFeatures;

    /** Forgejo-specific features */
    forgejo?: ForgejoFeatures;
  };

  /** API response metadata */
  metadata: {
    /** API endpoint used */
    endpoint?: string;

    /** Response headers */
    headers?: Record<string, string>;

    /** Rate limit information */
    rateLimit?: RateLimitInfo;
  };
}

/**
 * GitHub-specific features
 */
interface GitHubFeatures {
  /** GitHub-specific PR features */
  pullRequest?: {
    /** Draft PR indicator */
    draft?: boolean;

    /** Requested teams */
    requestedTeams?: GitHubTeam[];

    /** Review requests */
    reviewRequests?: GitHubReviewRequest[];

    /** Auto-merge enabled */
    autoMerge?: {
      enabled: boolean;
      mergeMethod: MergeMethod;
      commitTitle?: string;
      commitMessage?: string;
    };

    /** Rebaseable */
    rebaseable?: boolean;

    /** Mergeable */
    mergeable?: boolean;

    /** Merge conflict state */
    mergeStateStatus?: 'behind' | 'dirty' | 'draft' | 'has_hooks' | 'unknown';
  };

  /** GitHub-specific issue features */
  issue?: {
    /** Locked status */
    locked?: boolean;

    /** Active lock reason */
    activeLockReason?: 'off-topic' | 'too heated' | 'resolved' | 'spam';

    /** Reaction counts */
    reactions?: {
      total: number;
      '+1': number;
      '-1': number;
      laugh: number;
      hooray: number;
      confused: number;
      heart: number;
      rocket: number;
      eyes: number;
    };
  };

  /** GitHub-specific repository features */
  repository?: {
    /** Repository permissions */
    permissions?: {
      admin: boolean;
      push: boolean;
      pull: boolean;
      triage: boolean;
      maintain: boolean;
    };

    /** Security analysis */
    security?: {
      advancedSecurity: boolean;
      secretScanning: boolean;
      dependabot: boolean;
      codeScanning: boolean;
    };

    /** Pages configuration */
    pages?: {
      enabled: boolean;
      url?: string;
    };
  };
}

/**
 * GitLab-specific features
 */
interface GitLabFeatures {
  /** GitLab-specific merge request features */
  mergeRequest?: {
    /** Draft indicator (WIP) */
    workInProgress?: boolean;

    /** Merge status */
    mergeStatus?: 'can_be_merged' | 'cannot_be_merged' | 'checking' | 'unchecked';

    /** Merge when pipeline succeeds */
    mergeWhenPipelineSucceeds?: boolean;

    /** Squash option */
    squash?: boolean;

    /** Approvals required */
    approvalsRequired?: number;

    /** Approval rules */
    approvalRules?: GitLabApprovalRule[];

    /** Pipeline information */
    pipeline?: GitLabPipeline;

    /** Diff references */
    diffRefs?: {
      baseSha: string;
      headSha: string;
      startSha: string;
    };

    /** Time stats */
    timeStats?: {
      timeEstimate: number;
      totalTimeSpent: number;
      humanTimeEstimate: string;
      humanTotalTimeSpent: string;
    };
  };

  /** GitLab-specific issue features */
  issue?: {
    /** Issue type */
    issueType?: 'issue' | 'incident' | 'test_case';

    /** Confidential flag */
    confidential?: boolean;

    /** Weight */
    weight?: number;

    /** Discussion locked */
    discussionLocked?: boolean;

    /** Epic information */
    epic?: {
      id: number;
      iid: number;
      title: string;
      url: string;
    };

    /** Time tracking */
    timeStats?: {
      timeEstimate: number;
      totalTimeSpent: number;
      humanTimeEstimate: string;
      humanTotalTimeSpent: string;
    };
  };

  /** GitLab-specific project features */
  project?: {
    /** Project visibility */
    visibility?: 'public' | 'internal' | 'private';

    /** Issues enabled */
    issuesEnabled?: boolean;

    /** Merge requests enabled */
    mergeRequestsEnabled?: boolean;

    /** Wiki enabled */
    wikiEnabled?: boolean;

    /** Snippets enabled */
    snippetsEnabled?: boolean;

    /** Container registry enabled */
    containerRegistryEnabled?: boolean;

    /** Jobs enabled */
    jobsEnabled?: boolean;
  };
}

/**
 * Gitea-specific features
 */
interface GiteaFeatures {
  /** Gitea-specific pull request features */
  pullRequest?: {
    /** Pull request type */
    type?: 'pull' | 'patch';

    /** Mergeable */
    mergeable?: boolean;

    /** Has conflicts */
    hasConflicts?: boolean;

    /** Can auto merge */
    canAutoMerge?: boolean;

    /** Review state */
    reviewState?: 'pending' | 'approved' | 'changes_requested';

    /** Stale flag */
    stale?: boolean;

    /** Maintainer can modify */
    maintainerCanModify?: boolean;
  };

  /** Gitea-specific issue features */
  issue?: {
    /** Issue is pull request */
    isPull?: boolean;

    /** Closed timestamp */
    closedTime?: string;

    /** Deadline */
    deadline?: string;

    /** Milestone deadline */
    milestoneDeadline?: string;
  };

  /** Gitea-specific repository features */
  repository?: {
    /** Repository size */
    size?: number;

    /** Language statistics */
    languages?: Record<string, number>;

    /** Internal tracker enabled */
    internalTracker?: {
      enableTimeTracker: boolean;
      enableIssueDependencies: boolean;
      enableKanbanBoard: boolean;
    };

    /** External tracker enabled */
    externalTracker?: {
      externalTrackerURL: string;
      externalTrackerFormat: string;
    };

    /** Wiki enabled */
    wiki?: {
      enabled: boolean;
      externalWikiURL?: string;
    };
  };
}

/**
 * Forgejo-specific features (extends Gitea)
 */
interface ForgejoFeatures extends GiteaFeatures {
  /** Forgejo-specific additions */
  forgejo?: {
    /** ActivityPub federation */
    federation?: {
      enabled: boolean;
      instanceURL: string;
    };

    /** Code search */
    codeSearch?: {
      enabled: boolean;
      indexerType: string;
    };

    /** Repository actions */
    actions?: {
      enabled: boolean;
      defaultActionsURL?: string;
    };
  };
}
```

### 3. Transformation Utilities

```typescript
/**
 * Platform transformer interface
 */
interface PlatformTransformer {
  /** Platform identifier */
  readonly platform: string;

  /** Transform platform-specific PR to unified model */
  transformPullRequest(platformPR: unknown): UnifiedPullRequest;

  /** Transform unified PR to platform-specific format */
  transformPullRequestToPlatform(unifiedPR: UnifiedPullRequest): unknown;

  /** Transform platform-specific issue to unified model */
  transformIssue(platformIssue: unknown): UnifiedIssue;

  /** Transform unified issue to platform-specific format */
  transformIssueToPlatform(unifiedIssue: UnifiedIssue): unknown;

  /** Transform platform-specific branch to unified model */
  transformBranch(platformBranch: unknown): UnifiedBranch;

  /** Transform unified branch to platform-specific format */
  transformBranchToPlatform(unifiedBranch: UnifiedBranch): unknown;

  /** Transform platform-specific CI status to unified model */
  transformCIStatus(platformCI: unknown): UnifiedCIStatus;

  /** Transform unified CI status to platform-specific format */
  transformCIStatusToPlatform(unifiedCI: UnifiedCIStatus): unknown;
}

/**
 * GitHub transformer implementation
 */
class GitHubTransformer implements PlatformTransformer {
  readonly platform = 'github';

  transformPullRequest(platformPR: GitHubPullRequest): UnifiedPullRequest {
    return {
      id: platformPR.id.toString(),
      number: platformPR.number,
      url: platformPR.html_url,
      title: platformPR.title,
      body: platformPR.body,
      state: this.mapGitHubState(platformPR.state),
      status: this.mapGitHubStatus(platformPR),
      createdAt: platformPR.created_at,
      updatedAt: platformPR.updated_at,
      closedAt: platformPR.closed_at,
      mergedAt: platformPR.merged_at,
      head: this.transformGitHubBranch(platformPR.head),
      base: this.transformGitHubBranch(platformPR.base),
      author: this.transformGitHubUser(platformPR.user),
      assignees: platformPR.assignees?.map((user) => this.transformGitHubUser(user)) || [],
      reviewers: this.transformGitHubReviewers(platformPR.requested_reviewers || []),
      requestedReviewers: (platformPR.requested_reviewers || []).map((user) =>
        this.transformGitHubUser(user)
      ),
      labels: platformPR.labels?.map((label) => this.transformGitHubLabel(label)) || [],
      milestone: platformPR.milestone
        ? this.transformGitHubMilestone(platformPR.milestone)
        : undefined,
      merge: this.transformGitHubMergeInfo(platformPR),
      reviews: [], // Would need separate API call
      ciStatus: this.transformGitHubCIStatus(platformPR),
      comments: [], // Would need separate API call
      files: [], // Would need separate API call
      stats: this.transformGitHubStats(platformPR),
      platformData: {
        platform: 'github',
        raw: platformPR,
        features: {
          github: this.extractGitHubFeatures(platformPR),
        },
        metadata: {},
      },
    };
  }

  transformPullRequestToPlatform(unifiedPR: UnifiedPullRequest): GitHubPullRequest {
    const githubFeatures = unifiedPR.platformData.features?.github;

    return {
      id: parseInt(unifiedPR.id),
      number: unifiedPR.number,
      title: unifiedPR.title,
      body: unifiedPR.body,
      state: this.mapToGitHubState(unifiedPR.state),
      html_url: unifiedPR.url,
      created_at: unifiedPR.createdAt,
      updated_at: unifiedPR.updatedAt,
      closed_at: unifiedPR.closedAt,
      merged_at: unifiedPR.mergedAt,
      head: this.transformToGitHubBranch(unifiedPR.head),
      base: this.transformToGitHubBranch(unifiedPR.base),
      user: this.transformToGitHubUser(unifiedPR.author),
      assignees: unifiedPR.assignees.map((user) => this.transformToGitHubUser(user)),
      labels: unifiedPR.labels.map((label) => this.transformToGitHubLabel(label)),
      milestone: unifiedPR.milestone ? this.transformToGitHubMilestone(unifiedPR.milestone) : null,
      draft: githubFeatures?.pullRequest?.draft || false,
      requested_reviewers: unifiedPR.requestedReviewers.map((user) =>
        this.transformToGitHubUser(user)
      ),
      // ... other GitHub-specific fields
    } as GitHubPullRequest;
  }

  // Similar methods for issues, branches, CI status...

  private mapGitHubState(state: string): PRState {
    switch (state) {
      case 'open':
        return PRState.OPEN;
      case 'closed':
        return PRState.CLOSED;
      default:
        return PRState.UNKNOWN;
    }
  }

  private mapToGitHubState(state: PRState): string {
    switch (state) {
      case PRState.OPEN:
        return 'open';
      case PRState.CLOSED:
        return 'closed';
      default:
        return 'open';
    }
  }

  private mapGitHubStatus(pr: GitHubPullRequest): PRStatus {
    if (pr.merged_at) return PRStatus.MERGED;
    if (pr.state === 'closed') return PRStatus.CLOSED;
    if (pr.draft) return PRStatus.DRAFT;
    return PRStatus.OPEN;
  }

  // ... other transformation methods
}

/**
 * GitLab transformer implementation
 */
class GitLabTransformer implements PlatformTransformer {
  readonly platform = 'gitlab';

  transformPullRequest(platformMR: GitLabMergeRequest): UnifiedPullRequest {
    return {
      id: platformMR.id.toString(),
      number: platformMR.iid,
      url: platformMR.web_url,
      title: platformMR.title,
      body: platformMR.description,
      state: this.mapGitLabState(platformMR.state),
      status: this.mapGitLabStatus(platformMR),
      createdAt: platformMR.created_at,
      updatedAt: platformMR.updated_at,
      closedAt: platformMR.closed_at,
      mergedAt: platformMR.merged_at,
      head: this.transformGitLabBranch(platformMR.source_branch, platformMR.source_project),
      base: this.transformGitLabBranch(platformMR.target_branch, platformMR.target_project),
      author: this.transformGitLabUser(platformMR.author),
      assignees: platformMR.assignees?.map((user) => this.transformGitLabUser(user)) || [],
      reviewers: this.transformGitLabReviewers(platformMR.reviewers || []),
      requestedReviewers: [],
      labels: platformMR.labels?.map((label) => this.transformGitLabLabel(label)) || [],
      milestone: platformMR.milestone
        ? this.transformGitLabMilestone(platformMR.milestone)
        : undefined,
      merge: this.transformGitLabMergeInfo(platformMR),
      reviews: [], // Would need separate API call
      ciStatus: this.transformGitLabCIStatus(platformMR),
      comments: [], // Would need separate API call
      files: [], // Would need separate API call
      stats: this.transformGitLabStats(platformMR),
      platformData: {
        platform: 'gitlab',
        raw: platformMR,
        features: {
          gitlab: this.extractGitLabFeatures(platformMR),
        },
        metadata: {},
      },
    };
  }

  // Similar transformation methods for GitLab...
}

/**
 * Transformer registry and factory
 */
class TransformerFactory {
  private transformers = new Map<string, PlatformTransformer>();

  constructor() {
    this.registerTransformer(new GitHubTransformer());
    this.registerTransformer(new GitLabTransformer());
    this.registerTransformer(new GiteaTransformer());
    this.registerTransformer(new ForgejoTransformer());
  }

  registerTransformer(transformer: PlatformTransformer): void {
    this.transformers.set(transformer.platform, transformer);
  }

  getTransformer(platform: string): PlatformTransformer {
    const transformer = this.transformers.get(platform);
    if (!transformer) {
      throw new Error(`No transformer registered for platform: ${platform}`);
    }
    return transformer;
  }

  getSupportedPlatforms(): string[] {
    return Array.from(this.transformers.keys());
  }
}
```

### 4. Data Mapping and Validation

```typescript
/**
 * Data mapper for unified transformations
 */
class UnifiedDataMapper {
  constructor(private transformerFactory: TransformerFactory) {}

  /**
   * Transform platform-specific pull request to unified model
   */
  async mapPullRequest(platform: string, platformPR: unknown): Promise<UnifiedPullRequest> {
    const transformer = this.transformerFactory.getTransformer(platform);
    const unifiedPR = transformer.transformPullRequest(platformPR);

    // Validate unified model
    await this.validatePullRequest(unifiedPR);

    return unifiedPR;
  }

  /**
   * Transform unified pull request to platform-specific format
   */
  async mapPullRequestToPlatform(
    platform: string,
    unifiedPR: UnifiedPullRequest
  ): Promise<unknown> {
    const transformer = this.transformerFactory.getTransformer(platform);
    const platformPR = transformer.transformPullRequestToPlatform(unifiedPR);

    // Validate platform-specific model
    await this.validatePlatformPullRequest(platform, platformPR);

    return platformPR;
  }

  /**
   * Batch transform pull requests
   */
  async mapPullRequests(platform: string, platformPRs: unknown[]): Promise<UnifiedPullRequest[]> {
    const transformer = this.transformerFactory.getTransformer(platform);

    return Promise.all(
      platformPRs.map((pr) => {
        const unifiedPR = transformer.transformPullRequest(pr);
        return this.validatePullRequest(unifiedPR).then(() => unifiedPR);
      })
    );
  }

  /**
   * Validate unified pull request model
   */
  private async validatePullRequest(pr: UnifiedPullRequest): Promise<void> {
    // Required fields validation
    if (!pr.id) throw new Error('Pull request ID is required');
    if (!pr.number) throw new Error('Pull request number is required');
    if (!pr.title) throw new Error('Pull request title is required');
    if (!pr.state) throw new Error('Pull request state is required');
    if (!pr.createdAt) throw new Error('Pull request created date is required');
    if (!pr.author) throw new Error('Pull request author is required');

    // Date format validation
    this.validateISODate(pr.createdAt);
    if (pr.updatedAt) this.validateISODate(pr.updatedAt);
    if (pr.closedAt) this.validateISODate(pr.closedAt);
    if (pr.mergedAt) this.validateISODate(pr.mergedAt);

    // URL validation
    if (pr.url && !this.isValidURL(pr.url)) {
      throw new Error('Invalid pull request URL');
    }

    // Branch validation
    if (!pr.head || !pr.head.ref) throw new Error('Head branch is required');
    if (!pr.base || !pr.base.ref) throw new Error('Base branch is required');
  }

  /**
   * Validate platform-specific pull request model
   */
  private async validatePlatformPullRequest(platform: string, pr: unknown): Promise<void> {
    // Platform-specific validation logic
    switch (platform) {
      case 'github':
        await this.validateGitHubPullRequest(pr as GitHubPullRequest);
        break;
      case 'gitlab':
        await this.validateGitLabMergeRequest(pr as GitLabMergeRequest);
        break;
      // Add other platform validations
    }
  }

  private validateISODate(dateString: string): void {
    const date = new Date(dateString);
    if (isNaN(date.getTime())) {
      throw new Error(`Invalid ISO date: ${dateString}`);
    }
  }

  private isValidURL(url: string): boolean {
    try {
      new URL(url);
      return true;
    } catch {
      return false;
    }
  }

  // Platform-specific validation methods...
  private async validateGitHubPullRequest(pr: GitHubPullRequest): Promise<void> {
    if (!pr.id || !pr.number || !pr.title) {
      throw new Error('Invalid GitHub pull request structure');
    }
  }

  private async validateGitLabMergeRequest(mr: GitLabMergeRequest): Promise<void> {
    if (!mr.id || !mr.iid || !mr.title) {
      throw new Error('Invalid GitLab merge request structure');
    }
  }
}
```

### 5. Type Definitions and Enums

```typescript
/**
 * Unified enums for normalized data models
 */
enum PRState {
  OPEN = 'open',
  CLOSED = 'closed',
  MERGED = 'merged',
  DRAFT = 'draft',
  UNKNOWN = 'unknown',
}

enum PRStatus {
  OPEN = 'open',
  CLOSED = 'closed',
  MERGED = 'merged',
  DRAFT = 'draft',
  READY_FOR_REVIEW = 'ready_for_review',
  IN_REVIEW = 'in_review',
  APPROVED = 'approved',
  CHANGES_REQUESTED = 'changes_requested',
  UNKNOWN = 'unknown',
}

enum IssueState {
  OPEN = 'open',
  CLOSED = 'closed',
  LOCKED = 'locked',
  UNKNOWN = 'unknown',
}

enum CIStatusState {
  PENDING = 'pending',
  RUNNING = 'running',
  SUCCESS = 'success',
  FAILURE = 'failure',
  ERROR = 'error',
  CANCELLED = 'cancelled',
  SKIPPED = 'skipped',
  UNKNOWN = 'unknown',
}

enum CIConclusion {
  SUCCESS = 'success',
  FAILURE = 'failure',
  NEUTRAL = 'neutral',
  CANCELLED = 'cancelled',
  TIMED_OUT = 'timed_out',
  ACTION_REQUIRED = 'action_required',
  UNKNOWN = 'unknown',
}

/**
 * Supporting interfaces for unified models
 */
interface PRUser {
  id: string;
  login: string;
  name?: string;
  email?: string;
  avatarUrl?: string;
  type: 'user' | 'bot';
  url?: string;
}

interface PRReviewer extends PRUser {
  state: 'approved' | 'changes_requested' | 'commented' | 'pending';
  submittedAt?: string;
}

interface PRBranch {
  ref: string;
  sha: string;
  repo: {
    id: string;
    name: string;
    fullName: string;
    private: boolean;
    url: string;
  };
}

interface PRLabel {
  id: string;
  name: string;
  color?: string;
  description?: string;
}

interface PRMilestone {
  id: string;
  number: number;
  title: string;
  description?: string;
  state: 'open' | 'closed';
  dueOn?: string;
  createdAt: string;
  updatedAt: string;
  url: string;
}

interface PRMergeInfo {
  merged: boolean;
  mergedAt?: string;
  mergedBy?: PRUser;
  mergeCommitSha?: string;
  method?: MergeMethod;
  canMerge: boolean;
  mergeable?: boolean;
}

interface PRReview {
  id: string;
  user: PRUser;
  state: 'approved' | 'changes_requested' | 'commented' | 'pending' | 'dismissed';
  body?: string;
  submittedAt: string;
  commitId?: string;
  comments?: ReviewComment[];
}

interface UnifiedComment {
  id: string;
  user: PRUser;
  body: string;
  createdAt: string;
  updatedAt: string;
  url: string;
  reactions?: CommentReactions;
  type: 'regular' | 'review' | 'suggestion';
}

interface PRFile {
  sha: string;
  filename: string;
  status: 'added' | 'removed' | 'modified' | 'renamed';
  additions: number;
  deletions: number;
  changes: number;
  patch?: string;
  blobUrl?: string;
  rawUrl?: string;
}

interface PRStats {
  additions: number;
  deletions: number;
  changedFiles: number;
  commits: number;
}

interface IssueLink {
  type: IssueLinkType;
  issueId: string;
  issueNumber: number;
  url: string;
}

interface IssueReactions {
  total: number;
  thumbsUp: number;
  thumbsDown: number;
  laugh: number;
  hooray: number;
  confused: number;
  heart: number;
  rocket: number;
  eyes: number;
}

interface TimeTrackingInfo {
  timeEstimate: number; // seconds
  totalTimeSpent: number; // seconds
  humanTimeEstimate: string;
  humanTotalTimeSpent: string;
}

interface BranchCommit {
  sha: string;
  message: string;
  author: {
    name: string;
    email: string;
    date: string;
  };
  url: string;
}

interface BranchProtection {
  enabled: boolean;
  requiredStatusChecks?: {
    strict: boolean;
    contexts: string[];
  };
  enforceAdmins?: boolean;
  requiredPullRequestReviews?: {
    requiredApprovingReviewCount: number;
    dismissStaleReviews: boolean;
    requireCodeOwnerReviews: boolean;
  };
  restrictions?: {
    users: PRUser[];
    teams: string[];
  };
}

interface CIStatusCheck {
  id: string;
  name: string;
  status: CIStatusState;
  conclusion?: CIConclusion;
  startedAt?: string;
  completedAt?: string;
  url?: string;
  externalId?: string;
  detailsUrl?: string;
}
```

## File Structure

```
packages/platforms/src/
├── transformers/
│   ├── index.ts                                      # Transformer exports
│   ├── transformer.interface.ts                      # Transformer interface
│   ├── transformer-factory.ts                        # Factory implementation
│   ├── github.transformer.ts                         # GitHub transformer
│   ├── gitlab.transformer.ts                         # GitLab transformer
│   ├── gitea.transformer.ts                          # Gitea transformer
│   └── forgejo.transformer.ts                        # Forgejo transformer
├── models/
│   ├── index.ts                                      # Model exports
│   ├── unified/
│   │   ├── pull-request.model.ts                     # Unified PR model
│   │   ├── issue.model.ts                            # Unified issue model
│   │   ├── branch.model.ts                           # Unified branch model
│   │   ├── ci-status.model.ts                        # Unified CI status model
│   │   └── common.model.ts                           # Common unified models
│   ├── platform-specific/
│   │   ├── github.types.ts                           # GitHub-specific types
│   │   ├── gitlab.types.ts                           # GitLab-specific types
│   │   ├── gitea.types.ts                            # Gitea-specific types
│   │   └── forgejo.types.ts                          # Forgejo-specific types
│   └── enums/
│       ├── pr.enums.ts                               # PR-related enums
│       ├── issue.enums.ts                            # Issue-related enums
│       ├── ci.enums.ts                               # CI-related enums
│       └── common.enums.ts                           # Common enums
├── mappers/
│   ├── index.ts                                      # Mapper exports
│   ├── unified-data-mapper.ts                        # Main data mapper
│   ├── validation.ts                                 # Data validation
│   └── mapping-utils.ts                              # Mapping utilities
└── __tests__/
    ├── transformers/                                  # Transformer tests
    ├── models/                                       # Model tests
    ├── mappers/                                      # Mapper tests
    └── fixtures/                                     # Test data
```

## Testing Strategy

**Transformation Testing**:

- Test bidirectional transformations for each platform
- Validate data preservation during transformations
- Test edge cases and error handling
- Validate platform-specific feature preservation

**Model Validation Testing**:

- Test unified model validation rules
- Test platform-specific model validation
- Test data type conversions and formatting
- Test required field validation

**Integration Testing**:

- Test end-to-end transformation workflows
- Test with real platform API responses
- Test performance with large datasets
- Test concurrent transformations

## Completion Checklist

- [ ] Create unified data models for PR, Issue, Branch, CI status
- [ ] Define platform-specific data structures and features
- [ ] Implement transformation utilities for all platforms
- [ ] Create transformer factory and registry
- [ ] Implement data mapper with validation
- [ ] Add comprehensive type definitions and enums
- [ ] Create bidirectional transformation support
- [ ] Add platform data preservation mechanisms
- [ ] Implement comprehensive testing suite
- [ ] Document transformation patterns and usage

## Dependencies

- Task 1: Core Git Platform Interface Structure (base interfaces)
- Task 2: Platform Capabilities Discovery (capability-based transformations)

## Risks and Mitigations

**Risk**: Platform-specific features lost during normalization
**Mitigation**: Comprehensive platformData preservation, extensive testing

**Risk**: Transformation complexity leads to bugs
**Mitigation**: Comprehensive test coverage, validation at each step

**Risk**: Performance issues with large datasets
**Mitigation**: Efficient transformation algorithms, streaming for large data

## Success Criteria

- Complete normalization of platform-specific data models
- Bidirectional transformation support for all platforms
- Preservation of platform-specific features
- Comprehensive validation and error handling
- High-performance transformation with test coverage

This task ensures consistent behavior across Git platforms while preserving unique platform capabilities through intelligent data normalization and transformation.
