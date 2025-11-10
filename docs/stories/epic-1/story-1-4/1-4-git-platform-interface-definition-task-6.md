# Task 6: Add Comprehensive Interface Testing

**Story**: 1-4 Git Platform Interface Definition  
**Task**: 6 of 6 - Add Comprehensive Interface Testing  
**Epic**: 1 - Foundation & Core Infrastructure  
**Status**: Pending

## Task Description

Create comprehensive testing suite for the Git platform interface, including interface contract tests, mock implementations for testing consumers, and tests for pagination and rate limit handling. This ensures interface compliance, validates behavior across platforms, and provides reliable testing infrastructure for platform implementations.

## Acceptance Criteria

1. **Interface Contract Tests**: Write comprehensive tests validating all interface methods and contracts
2. **Mock Implementations**: Create mock platform implementations for testing consumers
3. **Pagination Tests**: Test pagination functionality across all strategies and edge cases
4. **Rate Limit Tests**: Test rate limit detection, handling, and recovery mechanisms
5. **Integration Test Suite**: Create end-to-end tests validating complete workflows

## Implementation Details

### 1. Interface Contract Tests

```typescript
// contracts/git-platform.contract.test.ts
describe('IGitPlatform Interface Contract', () => {
  let platform: IGitPlatform;
  let mockHttpClient: jest.Mocked<HttpClient>;
  let mockTransformer: jest.Mocked<PlatformTransformer>;
  let mockRateLimitHandler: jest.Mocked<RateLimitHandler>;
  let mockPaginationUtil: jest.Mocked<PaginationUtil>;

  beforeEach(() => {
    mockHttpClient = createMockHttpClient();
    mockTransformer = createMockTransformer();
    mockRateLimitHandler = createMockRateLimitHandler();
    mockPaginationUtil = createMockPaginationUtil();

    platform = new TestPlatform(
      mockHttpClient,
      mockTransformer,
      mockRateLimitHandler,
      mockPaginationUtil
    );
  });

  describe('Repository Operations', () => {
    describe('getRepository', () => {
      it('should return repository information', async () => {
        // Arrange
        const expectedRepo = createMockRepository();
        const platformRepo = createMockPlatformRepository();
        mockTransformer.transformRepository.mockReturnValue(expectedRepo);
        mockHttpClient.get.mockResolvedValue({ data: platformRepo });
        mockRateLimitHandler.executeWithRateLimit.mockImplementation((fn) => fn());

        // Act
        const result = await platform.getRepository('owner', 'repo');

        // Assert
        expect(result).toEqual(expectedRepo);
        expect(mockHttpClient.get).toHaveBeenCalledWith('/repositories/owner/repo');
        expect(mockTransformer.transformRepository).toHaveBeenCalledWith(platformRepo);
        expect(mockRateLimitHandler.executeWithRateLimit).toHaveBeenCalled();
      });

      it('should handle repository not found error', async () => {
        // Arrange
        const error = new NotFoundError('Repository not found');
        mockHttpClient.get.mockRejectedValue(error);
        mockRateLimitHandler.executeWithRateLimit.mockImplementation((fn) => fn());

        // Act & Assert
        await expect(platform.getRepository('owner', 'repo')).rejects.toThrow(NotFoundError);
      });

      it('should validate input parameters', async () => {
        // Act & Assert
        await expect(platform.getRepository('', 'repo')).rejects.toThrow('Owner is required');
        await expect(platform.getRepository('owner', '')).rejects.toThrow(
          'Repository name is required'
        );
      });
    });

    describe('createRepository', () => {
      it('should create a new repository', async () => {
        // Arrange
        const options: CreateRepositoryOptions = {
          description: 'Test repository',
          private: true,
          autoInit: true,
        };
        const expectedRepo = createMockRepository();
        const platformRepo = createMockPlatformRepository();
        const platformOptions = createMockPlatformCreateOptions();

        mockTransformer.transformCreateRepositoryOptionsToPlatform.mockReturnValue(platformOptions);
        mockHttpClient.post.mockResolvedValue({ data: platformRepo });
        mockTransformer.transformRepository.mockReturnValue(expectedRepo);
        mockRateLimitHandler.executeWithRateLimit.mockImplementation((fn) => fn());

        // Act
        const result = await platform.createRepository('owner', 'repo', options);

        // Assert
        expect(result).toEqual(expectedRepo);
        expect(mockHttpClient.post).toHaveBeenCalledWith(
          '/repositories/owner/repo',
          platformOptions
        );
        expect(mockTransformer.transformCreateRepositoryOptionsToPlatform).toHaveBeenCalledWith(
          options
        );
        expect(mockTransformer.transformRepository).toHaveBeenCalledWith(platformRepo);
      });
    });
  });

  describe('Pull Request Operations', () => {
    describe('createPR', () => {
      it('should create a pull request', async () => {
        // Arrange
        const options: CreatePROptions = {
          title: 'Test PR',
          body: 'Test description',
          head: 'feature-branch',
          base: 'main',
          draft: false,
        };
        const expectedPR = createMockPullRequest();
        const platformPR = createMockPlatformPullRequest();
        const platformOptions = createMockPlatformCreatePROptions();

        mockTransformer.transformCreatePROptionsToPlatform.mockReturnValue(platformOptions);
        mockHttpClient.post.mockResolvedValue({ data: platformPR });
        mockTransformer.transformPullRequest.mockReturnValue(expectedPR);
        mockRateLimitHandler.executeWithRateLimit.mockImplementation((fn) => fn());

        // Act
        const result = await platform.createPR('owner', 'repo', options);

        // Assert
        expect(result).toEqual(expectedPR);
        expect(mockHttpClient.post).toHaveBeenCalledWith(
          '/repositories/owner/repo/pulls',
          platformOptions
        );
        expect(mockTransformer.transformCreatePROptionsToPlatform).toHaveBeenCalledWith(options);
        expect(mockTransformer.transformPullRequest).toHaveBeenCalledWith(platformPR);
      });

      it('should validate required PR fields', async () => {
        // Arrange
        const invalidOptions = { title: '', head: '', base: '' } as CreatePROptions;

        // Act & Assert
        await expect(platform.createPR('owner', 'repo', invalidOptions)).rejects.toThrow(
          'Title is required'
        );
      });
    });

    describe('getPR', () => {
      it('should get pull request details', async () => {
        // Arrange
        const expectedPR = createMockPullRequest();
        const platformPR = createMockPlatformPullRequest();

        mockHttpClient.get.mockResolvedValue({ data: platformPR });
        mockTransformer.transformPullRequest.mockReturnValue(expectedPR);
        mockRateLimitHandler.executeWithRateLimit.mockImplementation((fn) => fn());

        // Act
        const result = await platform.getPR('owner', 'repo', 123);

        // Assert
        expect(result).toEqual(expectedPR);
        expect(mockHttpClient.get).toHaveBeenCalledWith('/repositories/owner/repo/pulls/123');
        expect(mockTransformer.transformPullRequest).toHaveBeenCalledWith(platformPR);
      });
    });

    describe('listPRs', () => {
      it('should list pull requests with pagination', async () => {
        // Arrange
        const options: ListPROptions = {
          state: 'open',
          pagination: { page: 1, size: 50 },
        };
        const expectedPRs = [createMockPullRequest(), createMockPullRequest()];
        const platformPRs = [createMockPlatformPullRequest(), createMockPlatformPullRequest()];
        const paginationInfo = createMockPaginationInfo();
        const paginationOptions = createMockPaginationOptions();

        mockPaginationUtil.createPaginationOptions.mockReturnValue(paginationOptions);
        mockHttpClient.get.mockResolvedValue({
          data: platformPRs,
          headers: { 'x-total-count': '100' },
        });
        mockTransformer.transformPullRequest.mockReturnValue(expectedPRs[0]);
        mockPaginationUtil.extractPaginationInfo.mockReturnValue(paginationInfo);
        mockRateLimitHandler.executeWithRateLimit.mockImplementation((fn) => fn());

        // Act
        const result = await platform.listPRs('owner', 'repo', options);

        // Assert
        expect(result.data).toEqual(expectedPRs);
        expect(result.pagination).toEqual(paginationInfo);
        expect(mockHttpClient.get).toHaveBeenCalledWith('/repositories/owner/repo/pulls', {
          params: expect.any(Object),
        });
      });
    });

    describe('commentOnPR', () => {
      it('should add comment to pull request', async () => {
        // Arrange
        const comment = 'Test comment';
        const expectedComment = createMockComment();
        const platformComment = createMockPlatformComment();

        mockHttpClient.post.mockResolvedValue({ data: platformComment });
        mockTransformer.transformComment.mockReturnValue(expectedComment);
        mockRateLimitHandler.executeWithRateLimit.mockImplementation((fn) => fn());

        // Act
        const result = await platform.commentOnPR('owner', 'repo', 123, comment);

        // Assert
        expect(result).toEqual(expectedComment);
        expect(mockHttpClient.post).toHaveBeenCalledWith(
          '/repositories/owner/repo/pulls/123/comments',
          { body: comment }
        );
      });
    });

    describe('mergePR', () => {
      it('should merge pull request', async () => {
        // Arrange
        const options: MergePROptions = {
          method: 'squash',
          commitMessage: 'Merge commit',
        };
        const expectedMergeResult = createMockMergeResult();
        const platformMergeResult = createMockPlatformMergeResult();
        const platformOptions = createMockPlatformMergeOptions();

        mockTransformer.transformMergePROptionsToPlatform.mockReturnValue(platformOptions);
        mockHttpClient.put.mockResolvedValue({ data: platformMergeResult });
        mockTransformer.transformMergeResult.mockReturnValue(expectedMergeResult);
        mockRateLimitHandler.executeWithRateLimit.mockImplementation((fn) => fn());

        // Act
        const result = await platform.mergePR('owner', 'repo', 123, options);

        // Assert
        expect(result).toEqual(expectedMergeResult);
        expect(mockHttpClient.put).toHaveBeenCalledWith(
          '/repositories/owner/repo/pulls/123/merge',
          platformOptions
        );
      });
    });
  });

  describe('Issue Operations', () => {
    describe('getIssue', () => {
      it('should get issue details', async () => {
        // Arrange
        const expectedIssue = createMockIssue();
        const platformIssue = createMockPlatformIssue();

        mockHttpClient.get.mockResolvedValue({ data: platformIssue });
        mockTransformer.transformIssue.mockReturnValue(expectedIssue);
        mockRateLimitHandler.executeWithRateLimit.mockImplementation((fn) => fn());

        // Act
        const result = await platform.getIssue('owner', 'repo', 456);

        // Assert
        expect(result).toEqual(expectedIssue);
        expect(mockHttpClient.get).toHaveBeenCalledWith('/repositories/owner/repo/issues/456');
        expect(mockTransformer.transformIssue).toHaveBeenCalledWith(platformIssue);
      });
    });

    describe('listIssues', () => {
      it('should list issues with filtering', async () => {
        // Arrange
        const options: ListIssuesOptions = {
          state: 'open',
          labels: ['bug', 'enhancement'],
        };
        const expectedIssues = [createMockIssue(), createMockIssue()];
        const platformIssues = [createMockPlatformIssue(), createMockPlatformIssue()];
        const paginationInfo = createMockPaginationInfo();
        const paginationOptions = createMockPaginationOptions();

        mockPaginationUtil.createPaginationOptions.mockReturnValue(paginationOptions);
        mockHttpClient.get.mockResolvedValue({
          data: platformIssues,
          headers: { 'x-total-count': '50' },
        });
        mockTransformer.transformIssue.mockReturnValue(expectedIssues[0]);
        mockPaginationUtil.extractPaginationInfo.mockReturnValue(paginationInfo);
        mockRateLimitHandler.executeWithRateLimit.mockImplementation((fn) => fn());

        // Act
        const result = await platform.listIssues('owner', 'repo', options);

        // Assert
        expect(result.data).toEqual(expectedIssues);
        expect(result.pagination).toEqual(paginationInfo);
        expect(mockHttpClient.get).toHaveBeenCalledWith('/repositories/owner/repo/issues', {
          params: expect.objectContaining({
            state: 'open',
            labels: 'bug,enhancement',
          }),
        });
      });
    });
  });

  describe('Branch Operations', () => {
    describe('getBranch', () => {
      it('should get branch information', async () => {
        // Arrange
        const expectedBranch = createMockBranch();
        const platformBranch = createMockPlatformBranch();

        mockHttpClient.get.mockResolvedValue({ data: platformBranch });
        mockTransformer.transformBranch.mockReturnValue(expectedBranch);
        mockRateLimitHandler.executeWithRateLimit.mockImplementation((fn) => fn());

        // Act
        const result = await platform.getBranch('owner', 'repo', 'main');

        // Assert
        expect(result).toEqual(expectedBranch);
        expect(mockHttpClient.get).toHaveBeenCalledWith('/repositories/owner/repo/branches/main');
        expect(mockTransformer.transformBranch).toHaveBeenCalledWith(platformBranch);
      });
    });

    describe('createBranch', () => {
      it('should create a new branch', async () => {
        // Arrange
        const expectedBranch = createMockBranch();
        const platformBranch = createMockPlatformBranch();
        const platformOptions = createMockPlatformCreateBranchOptions();

        mockTransformer.transformCreateBranchOptionsToPlatform.mockReturnValue(platformOptions);
        mockHttpClient.post.mockResolvedValue({ data: platformBranch });
        mockTransformer.transformBranch.mockReturnValue(expectedBranch);
        mockRateLimitHandler.executeWithRateLimit.mockImplementation((fn) => fn());

        // Act
        const result = await platform.createBranch('owner', 'repo', 'feature-branch', 'main');

        // Assert
        expect(result).toEqual(expectedBranch);
        expect(mockHttpClient.post).toHaveBeenCalledWith(
          '/repositories/owner/repo/branches',
          platformOptions
        );
      });
    });
  });

  describe('CI/CD Operations', () => {
    describe('triggerCI', () => {
      it('should trigger CI/CD pipeline', async () => {
        // Arrange
        const options: TriggerCIOptions = {
          ref: 'feature-branch',
          workflow: 'test.yml',
        };
        const expectedResult = createMockCITriggerResult();
        const platformResult = createMockPlatformCITriggerResult();
        const platformOptions = createMockPlatformTriggerCIOptions();

        mockTransformer.transformTriggerCIOptionsToPlatform.mockReturnValue(platformOptions);
        mockHttpClient.post.mockResolvedValue({ data: platformResult });
        mockTransformer.transformCITriggerResult.mockReturnValue(expectedResult);
        mockRateLimitHandler.executeWithRateLimit.mockImplementation((fn) => fn());

        // Act
        const result = await platform.triggerCI('owner', 'repo', options);

        // Assert
        expect(result).toEqual(expectedResult);
        expect(mockHttpClient.post).toHaveBeenCalledWith(
          '/repositories/owner/repo/dispatches',
          platformOptions
        );
      });
    });

    describe('getCIStatus', () => {
      it('should get CI/CD status', async () => {
        // Arrange
        const ref = 'feature-branch';
        const expectedStatus = createMockCIStatus();
        const platformStatus = createMockPlatformCIStatus();

        mockHttpClient.get.mockResolvedValue({ data: platformStatus });
        mockTransformer.transformCIStatus.mockReturnValue(expectedStatus);
        mockRateLimitHandler.executeWithRateLimit.mockImplementation((fn) => fn());

        // Act
        const result = await platform.getCIStatus('owner', 'repo', ref);

        // Assert
        expect(result).toEqual(expectedStatus);
        expect(mockHttpClient.get).toHaveBeenCalledWith(
          `/repositories/owner/repo/commits/${ref}/status`
        );
        expect(mockTransformer.transformCIStatus).toHaveBeenCalledWith(platformStatus);
      });
    });
  });

  describe('Webhook Operations', () => {
    describe('createWebhook', () => {
      it('should create webhook', async () => {
        // Arrange
        const options: CreateWebhookOptions = {
          url: 'https://example.com/webhook',
          events: ['push', 'pull_request'],
          secret: 'webhook-secret',
        };
        const expectedWebhook = createMockWebhook();
        const platformWebhook = createMockPlatformWebhook();
        const platformOptions = createMockPlatformCreateWebhookOptions();

        mockTransformer.transformCreateWebhookOptionsToPlatform.mockReturnValue(platformOptions);
        mockHttpClient.post.mockResolvedValue({ data: platformWebhook });
        mockTransformer.transformWebhook.mockReturnValue(expectedWebhook);
        mockRateLimitHandler.executeWithRateLimit.mockImplementation((fn) => fn());

        // Act
        const result = await platform.createWebhook('owner', 'repo', options);

        // Assert
        expect(result).toEqual(expectedWebhook);
        expect(mockHttpClient.post).toHaveBeenCalledWith(
          '/repositories/owner/repo/hooks',
          platformOptions
        );
      });
    });
  });

  describe('Platform Capabilities', () => {
    describe('getCapabilities', () => {
      it('should return platform capabilities', async () => {
        // Arrange
        const expectedCapabilities = createMockPlatformCapabilities();

        // Act
        const result = await platform.getCapabilities();

        // Assert
        expect(result).toEqual(expectedCapabilities);
      });
    });
  });
});
```

### 2. Mock Implementations

```typescript
// mocks/mock-platform.ts
export class MockPlatform implements IGitPlatform {
  readonly platformName = 'mock';
  readonly apiVersion = '1.0';

  private repositories = new Map<string, Repository>();
  private pullRequests = new Map<string, PullRequest>();
  private issues = new Map<string, Issue>();
  private branches = new Map<string, Branch>();
  private webhooks = new Map<string, Webhook>();

  constructor() {
    this.initializeMockData();
  }

  // Repository Operations
  async getRepository(owner: string, repo: string): Promise<Repository> {
    const key = `${owner}/${repo}`;
    const repository = this.repositories.get(key);
    if (!repository) {
      throw new NotFoundError(`Repository ${key} not found`);
    }
    return repository;
  }

  async createRepository(
    owner: string,
    repo: string,
    options: CreateRepositoryOptions
  ): Promise<Repository> {
    const key = `${owner}/${repo}`;
    if (this.repositories.has(key)) {
      throw new ConflictError(`Repository ${key} already exists`);
    }

    const repository: Repository = {
      id: generateId(),
      name: repo,
      fullName: key,
      owner: {
        login: owner,
        type: 'user',
        id: generateId(),
      },
      description: options.description,
      private: options.private || false,
      defaultBranch: 'main',
      url: `https://mock.example.com/${key}`,
      urls: {
        https: `https://mock.example.com/${key}.git`,
        ssh: `git@mock.example.com:${key}.git`,
      },
      config: {
        allowMergeCommits: true,
        allowSquashMerge: true,
        allowRebaseMerge: true,
        deleteBranchOnMerge: false,
      },
      metadata: {
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
        language: 'TypeScript',
        size: 1000,
        stars: 0,
        forks: 0,
        openIssues: 0,
      },
      platformData: {
        platform: 'mock',
        raw: {},
        features: {},
        metadata: {},
      },
    };

    this.repositories.set(key, repository);
    return repository;
  }

  // Pull Request Operations
  async createPR(owner: string, repo: string, options: CreatePROptions): Promise<PullRequest> {
    const repoKey = `${owner}/${repo}`;
    const prKey = `${repoKey}/${Date.now()}`;

    if (!this.repositories.has(repoKey)) {
      throw new NotFoundError(`Repository ${repoKey} not found`);
    }

    const pullRequest: PullRequest = {
      id: generateId(),
      number: this.pullRequests.size + 1,
      url: `https://mock.example.com/${repoKey}/pull/${this.pullRequests.size + 1}`,
      title: options.title,
      body: options.body,
      state: PRState.OPEN,
      status: options.draft ? PRStatus.DRAFT : PRStatus.OPEN,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      head: {
        ref: options.head,
        sha: generateSha(),
        repo: await this.getRepository(owner, repo),
      },
      base: {
        ref: options.base,
        sha: generateSha(),
        repo: await this.getRepository(owner, repo),
      },
      author: {
        id: generateId(),
        login: 'mockuser',
        name: 'Mock User',
        type: 'user',
      },
      assignees:
        options.assignees?.map((login) => ({
          id: generateId(),
          login,
          name: login,
          type: 'user' as const,
        })) || [],
      reviewers:
        options.reviewers?.map((login) => ({
          id: generateId(),
          login,
          name: login,
          type: 'user' as const,
          state: 'pending' as const,
        })) || [],
      requestedReviewers: [],
      labels:
        options.labels?.map((name) => ({
          id: generateId(),
          name,
          color: '#000000',
        })) || [],
      merge: {
        merged: false,
        canMerge: true,
      },
      reviews: [],
      ciStatus: {
        status: CIStatusState.SUCCESS,
        checks: [],
      },
      comments: [],
      files: [],
      stats: {
        additions: 10,
        deletions: 5,
        changedFiles: 2,
        commits: 1,
      },
      platformData: {
        platform: 'mock',
        raw: {},
        features: {},
        metadata: {},
      },
    };

    this.pullRequests.set(prKey, pullRequest);
    return pullRequest;
  }

  async getPR(owner: string, repo: string, prNumber: number): Promise<PullRequest> {
    const repoKey = `${owner}/${repo}`;
    const prKey = Array.from(this.pullRequests.keys()).find(
      (key) => key.startsWith(repoKey) && this.pullRequests.get(key)?.number === prNumber
    );

    if (!prKey) {
      throw new NotFoundError(`Pull request #${prNumber} not found in ${repoKey}`);
    }

    return this.pullRequests.get(prKey)!;
  }

  async listPRs(
    owner: string,
    repo: string,
    options?: ListPROptions
  ): Promise<PaginatedResponse<PullRequest>> {
    const repoKey = `${owner}/${repo}`;
    const prs = Array.from(this.pullRequests.entries())
      .filter(([key]) => key.startsWith(repoKey))
      .map(([, pr]) => pr);

    // Apply filters
    let filteredPRs = prs;
    if (options?.state) {
      filteredPRs = filteredPRs.filter((pr) => pr.state === options.state);
    }

    // Apply pagination
    const page = options?.pagination?.page || 1;
    const size = options?.pagination?.size || 50;
    const startIndex = (page - 1) * size;
    const endIndex = startIndex + size;
    const paginatedPRs = filteredPRs.slice(startIndex, endIndex);

    return {
      data: paginatedPRs,
      pagination: {
        strategy: PaginationStrategy.PAGE_BASED,
        currentPage: {
          number: page,
          size,
          hasNext: endIndex < filteredPRs.length,
          hasPrev: page > 1,
        },
        totalCount: {
          items: filteredPRs.length,
          accuracy: 'exact',
        },
      },
    };
  }

  async commentOnPR(
    owner: string,
    repo: string,
    prNumber: number,
    comment: string
  ): Promise<Comment> {
    const pr = await this.getPR(owner, repo, prNumber);

    const newComment: Comment = {
      id: generateId(),
      user: {
        id: generateId(),
        login: 'mockuser',
        name: 'Mock User',
        type: 'user',
      },
      body: comment,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      url: `${pr.url}#comment-${generateId()}`,
      type: 'regular',
    };

    pr.comments.push(newComment);
    return newComment;
  }

  async mergePR(
    owner: string,
    repo: string,
    prNumber: number,
    options?: MergePROptions
  ): Promise<MergeResult> {
    const pr = await this.getPR(owner, repo, prNumber);

    if (pr.state === PRState.MERGED) {
      throw new ConflictError(`Pull request #${prNumber} is already merged`);
    }

    if (pr.state === PRState.CLOSED) {
      throw new ConflictError(`Pull request #${prNumber} is closed`);
    }

    // Simulate merge
    pr.state = PRState.MERGED;
    pr.status = PRStatus.MERGED;
    pr.mergedAt = new Date().toISOString();
    pr.merge = {
      merged: true,
      mergedAt: new Date().toISOString(),
      mergeCommitSha: generateSha(),
      method: options?.method || MergeMethod.MERGE,
      canMerge: false,
    };

    return {
      merged: true,
      commitSha: pr.merge.mergeCommitSha,
      method: options?.method || MergeMethod.MERGE,
    };
  }

  // Issue Operations
  async getIssue(owner: string, repo: string, issueNumber: number): Promise<Issue> {
    const repoKey = `${owner}/${repo}`;
    const issueKey = Array.from(this.issues.keys()).find(
      (key) => key.startsWith(repoKey) && this.issues.get(key)?.number === issueNumber
    );

    if (!issueKey) {
      throw new NotFoundError(`Issue #${issueNumber} not found in ${repoKey}`);
    }

    return this.issues.get(issueKey)!;
  }

  async listIssues(
    owner: string,
    repo: string,
    options?: ListIssuesOptions
  ): Promise<PaginatedResponse<Issue>> {
    const repoKey = `${owner}/${repo}`;
    const issues = Array.from(this.issues.entries())
      .filter(([key]) => key.startsWith(repoKey))
      .map(([, issue]) => issue);

    // Apply filters
    let filteredIssues = issues;
    if (options?.state) {
      filteredIssues = filteredIssues.filter((issue) => issue.state === options.state);
    }

    // Apply pagination
    const page = options?.pagination?.page || 1;
    const size = options?.pagination?.size || 50;
    const startIndex = (page - 1) * size;
    const endIndex = startIndex + size;
    const paginatedIssues = filteredIssues.slice(startIndex, endIndex);

    return {
      data: paginatedIssues,
      pagination: {
        strategy: PaginationStrategy.PAGE_BASED,
        currentPage: {
          number: page,
          size,
          hasNext: endIndex < filteredIssues.length,
          hasPrev: page > 1,
        },
        totalCount: {
          items: filteredIssues.length,
          accuracy: 'exact',
        },
      },
    };
  }

  async createIssue(owner: string, repo: string, options: CreateIssueOptions): Promise<Issue> {
    const repoKey = `${owner}/${repo}`;
    const issueKey = `${repoKey}/${Date.now()}`;

    if (!this.repositories.has(repoKey)) {
      throw new NotFoundError(`Repository ${repoKey} not found`);
    }

    const issue: Issue = {
      id: generateId(),
      number: this.issues.size + 1,
      url: `https://mock.example.com/${repoKey}/issues/${this.issues.size + 1}`,
      title: options.title,
      body: options.body,
      state: IssueState.OPEN,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      author: {
        id: generateId(),
        login: 'mockuser',
        name: 'Mock User',
        type: 'user',
      },
      assignees:
        options.assignees?.map((login) => ({
          id: generateId(),
          login,
          name: login,
          type: 'user' as const,
        })) || [],
      labels:
        options.labels?.map((name) => ({
          id: generateId(),
          name,
          color: '#000000',
        })) || [],
      comments: [],
      reactions: {
        total: 0,
        thumbsUp: 0,
        thumbsDown: 0,
        laugh: 0,
        hooray: 0,
        confused: 0,
        heart: 0,
        rocket: 0,
        eyes: 0,
      },
      platformData: {
        platform: 'mock',
        raw: {},
        features: {},
        metadata: {},
      },
    };

    this.issues.set(issueKey, issue);
    return issue;
  }

  async commentOnIssue(
    owner: string,
    repo: string,
    issueNumber: number,
    comment: string
  ): Promise<Comment> {
    const issue = await this.getIssue(owner, repo, issueNumber);

    const newComment: Comment = {
      id: generateId(),
      user: {
        id: generateId(),
        login: 'mockuser',
        name: 'Mock User',
        type: 'user',
      },
      body: comment,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      url: `${issue.url}#comment-${generateId()}`,
      type: 'regular',
    };

    issue.comments.push(newComment);
    return newComment;
  }

  // Branch Operations
  async getBranch(owner: string, repo: string, branch: string): Promise<Branch> {
    const repoKey = `${owner}/${repo}`;
    const branchKey = `${repoKey}/${branch}`;

    const branchData = this.branches.get(branchKey);
    if (!branchData) {
      throw new NotFoundError(`Branch ${branch} not found in ${repoKey}`);
    }

    return branchData;
  }

  async createBranch(
    owner: string,
    repo: string,
    branch: string,
    fromBranch: string
  ): Promise<Branch> {
    const repoKey = `${owner}/${repo}`;
    const branchKey = `${repoKey}/${branch}`;
    const sourceKey = `${repoKey}/${fromBranch}`;

    if (!this.repositories.has(repoKey)) {
      throw new NotFoundError(`Repository ${repoKey} not found`);
    }

    if (this.branches.has(branchKey)) {
      throw new ConflictError(`Branch ${branch} already exists in ${repoKey}`);
    }

    const sourceBranch = this.branches.get(sourceKey);
    if (!sourceBranch) {
      throw new NotFoundError(`Source branch ${fromBranch} not found in ${repoKey}`);
    }

    const newBranch: Branch = {
      name: branch,
      protected: false,
      default: false,
      commit: {
        sha: generateSha(),
        message: `Create branch ${branch}`,
        author: {
          name: 'Mock User',
          email: 'mock@example.com',
          date: new Date().toISOString(),
        },
        url: `https://mock.example.com/${repoKey}/commit/${generateSha()}`,
      },
      platformData: {
        platform: 'mock',
        raw: {},
        features: {},
        metadata: {},
      },
    };

    this.branches.set(branchKey, newBranch);
    return newBranch;
  }

  async deleteBranch(owner: string, repo: string, branch: string): Promise<void> {
    const repoKey = `${owner}/${repo}`;
    const branchKey = `${repoKey}/${branch}`;

    if (!this.branches.has(branchKey)) {
      throw new NotFoundError(`Branch ${branch} not found in ${repoKey}`);
    }

    this.branches.delete(branchKey);
  }

  // CI/CD Operations
  async triggerCI(
    owner: string,
    repo: string,
    options: TriggerCIOptions
  ): Promise<CITriggerResult> {
    const repoKey = `${owner}/${repo}`;

    if (!this.repositories.has(repoKey)) {
      throw new NotFoundError(`Repository ${repoKey} not found`);
    }

    return {
      triggered: true,
      runId: generateId(),
      runUrl: `https://mock.example.com/${repoKey}/actions/runs/${generateId()}`,
    };
  }

  async getCIStatus(owner: string, repo: string, ref: string): Promise<UnifiedCIStatus> {
    const repoKey = `${owner}/${repo}`;

    if (!this.repositories.has(repoKey)) {
      throw new NotFoundError(`Repository ${repoKey} not found`);
    }

    return {
      status: CIStatusState.SUCCESS,
      conclusion: CIConclusion.SUCCESS,
      runId: generateId(),
      runUrl: `https://mock.example.com/${repoKey}/actions/runs/${generateId()}`,
      startedAt: new Date().toISOString(),
      completedAt: new Date().toISOString(),
      checks: [
        {
          id: generateId(),
          name: 'test',
          status: CIStatusState.SUCCESS,
          conclusion: CIConclusion.SUCCESS,
          startedAt: new Date().toISOString(),
          completedAt: new Date().toISOString(),
        },
      ],
      summary: {
        totalChecks: 1,
        passedChecks: 1,
        failedChecks: 0,
        pendingChecks: 0,
        skippedChecks: 0,
      },
      platformData: {
        platform: 'mock',
        raw: {},
        features: {},
        metadata: {},
      },
    };
  }

  // Webhook Operations
  async createWebhook(
    owner: string,
    repo: string,
    options: CreateWebhookOptions
  ): Promise<Webhook> {
    const repoKey = `${owner}/${repo}`;
    const webhookKey = `${repoKey}/${Date.now()}`;

    if (!this.repositories.has(repoKey)) {
      throw new NotFoundError(`Repository ${repoKey} not found`);
    }

    const webhook: Webhook = {
      id: generateId(),
      url: options.url,
      contentType: options.contentType || 'json',
      events: options.events,
      active: options.active !== false,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      platformData: {
        platform: 'mock',
        raw: {},
        features: {},
        metadata: {},
      },
    };

    this.webhooks.set(webhookKey, webhook);
    return webhook;
  }

  async listWebhooks(owner: string, repo: string): Promise<Webhook[]> {
    const repoKey = `${owner}/${repo}`;

    return Array.from(this.webhooks.entries())
      .filter(([key]) => key.startsWith(repoKey))
      .map(([, webhook]) => webhook);
  }

  async deleteWebhook(owner: string, repo: string, webhookId: string): Promise<void> {
    const repoKey = `${owner}/${repo}`;

    const webhookKey = Array.from(this.webhooks.keys()).find(
      (key) => key.startsWith(repoKey) && this.webhooks.get(key)?.id === webhookId
    );

    if (!webhookKey) {
      throw new NotFoundError(`Webhook ${webhookId} not found in ${repoKey}`);
    }

    this.webhooks.delete(webhookKey);
  }

  // Platform Capabilities
  async getCapabilities(): Promise<PlatformCapabilities> {
    return {
      platform: {
        name: 'mock',
        version: '1.0',
        apiVersion: '1.0',
        displayName: 'Mock Platform',
      },
      repository: createMockRepositoryCapabilities(),
      branch: createMockBranchCapabilities(),
      pullRequest: createMockPRCapabilities(),
      issue: createMockIssueCapabilities(),
      ci: createMockCICapabilities(),
      webhook: createMockWebhookCapabilities(),
      authentication: createMockAuthenticationCapabilities(),
      rateLimit: createMockRateLimitCapabilities(),
      api: createMockAPICapabilities(),
      features: createMockPlatformFeatures(),
      limitations: createMockPlatformLimitations(),
    };
  }

  private initializeMockData(): void {
    // Initialize with default branch
    const defaultBranch: Branch = {
      name: 'main',
      protected: false,
      default: true,
      commit: {
        sha: generateSha(),
        message: 'Initial commit',
        author: {
          name: 'Mock User',
          email: 'mock@example.com',
          date: new Date().toISOString(),
        },
        url: 'https://mock.example.com/commit/abc123',
      },
      platformData: {
        platform: 'mock',
        raw: {},
        features: {},
        metadata: {},
      },
    };

    this.branches.set('test/test/main', defaultBranch);
  }
}

// Helper functions
function generateId(): string {
  return Math.random().toString(36).substr(2, 9);
}

function generateSha(): string {
  return Math.random().toString(36).substr(2, 40);
}

// Custom error classes
export class NotFoundError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'NotFoundError';
  }
}

export class ConflictError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ConflictError';
  }
}
```

### 3. Pagination Tests

```typescript
// pagination/pagination.test.ts
describe('Pagination Utilities', () => {
  let paginationUtil: PaginationUtil;

  beforeEach(() => {
    paginationUtil = new DefaultPaginationUtil();
  });

  describe('createPaginationOptions', () => {
    it('should create page-based pagination options', () => {
      const input = { page: 2, size: 25 };
      const result = paginationUtil.createPaginationOptions(input);

      expect(result.strategy).toBe(PaginationStrategy.PAGE_BASED);
      expect(result.page?.number).toBe(2);
      expect(result.page?.size).toBe(25);
    });

    it('should create offset-based pagination options', () => {
      const input = { offset: 100, limit: 50 };
      const result = paginationUtil.createPaginationOptions(input);

      expect(result.strategy).toBe(PaginationStrategy.OFFSET_BASED);
      expect(result.offset?.offset).toBe(100);
      expect(result.offset?.limit).toBe(50);
    });

    it('should create cursor-based pagination options', () => {
      const input = { cursor: 'abc123', limit: 30 };
      const result = paginationUtil.createPaginationOptions(input);

      expect(result.strategy).toBe(PaginationStrategy.CURSOR_BASED);
      expect(result.cursor?.value).toBe('abc123');
      expect(result.cursor?.limit).toBe(30);
    });

    it('should pass through existing pagination options', () => {
      const input: PaginationOptions = {
        strategy: PaginationStrategy.PAGE_BASED,
        page: { number: 1, size: 10 },
      };

      const result = paginationUtil.createPaginationOptions(input);
      expect(result).toEqual(input);
    });
  });

  describe('paginateAll', () => {
    it('should iterate through all pages', async () => {
      const mockData = [[{ id: '1' }, { id: '2' }], [{ id: '3' }, { id: '4' }], [{ id: '5' }]];

      let callCount = 0;
      const mockRequest = jest.fn().mockImplementation(() => {
        const response = {
          data: mockData[callCount],
          pagination: {
            strategy: PaginationStrategy.PAGE_BASED,
            currentPage: {
              number: callCount + 1,
              size: 2,
              hasNext: callCount < mockData.length - 1,
              hasPrev: callCount > 0,
            },
          },
        };
        callCount++;
        return Promise.resolve(response);
      });

      const initialOptions: PaginationOptions = {
        strategy: PaginationStrategy.PAGE_BASED,
        page: { number: 1, size: 2 },
      };

      const results = [];
      for await (const page of paginationUtil.paginateAll(mockRequest, initialOptions)) {
        results.push(...page);
      }

      expect(results).toHaveLength(5);
      expect(results.map((item) => item.id)).toEqual(['1', '2', '3', '4', '5']);
      expect(mockRequest).toHaveBeenCalledTimes(3);
    });

    it('should handle empty response', async () => {
      const mockRequest = jest.fn().mockResolvedValue({
        data: [],
        pagination: {
          strategy: PaginationStrategy.PAGE_BASED,
          currentPage: {
            number: 1,
            size: 10,
            hasNext: false,
            hasPrev: false,
          },
        },
      });

      const initialOptions: PaginationOptions = {
        strategy: PaginationStrategy.PAGE_BASED,
        page: { number: 1, size: 10 },
      };

      const results = [];
      for await (const page of paginationUtil.paginateAll(mockRequest, initialOptions)) {
        results.push(...page);
      }

      expect(results).toHaveLength(0);
      expect(mockRequest).toHaveBeenCalledTimes(1);
    });
  });

  describe('getNextPageOptions', () => {
    it('should return next page options when available', () => {
      const paginationInfo: PaginationInfo = {
        strategy: PaginationStrategy.PAGE_BASED,
        currentPage: {
          number: 2,
          size: 25,
          hasNext: true,
          hasPrev: true,
        },
        navigation: {
          next: {
            strategy: PaginationStrategy.PAGE_BASED,
            page: { number: 3, size: 25 },
          },
        },
      };

      const result = paginationUtil.getNextPageOptions(paginationInfo);

      expect(result).toEqual({
        strategy: PaginationStrategy.PAGE_BASED,
        page: { number: 3, size: 25 },
      });
    });

    it('should return null when no next page', () => {
      const paginationInfo: PaginationInfo = {
        strategy: PaginationStrategy.PAGE_BASED,
        currentPage: {
          number: 2,
          size: 25,
          hasNext: false,
          hasPrev: true,
        },
      };

      const result = paginationUtil.getNextPageOptions(paginationInfo);

      expect(result).toBeNull();
    });
  });
});
```

### 4. Rate Limit Tests

```typescript
// rate-limit/rate-limit.test.ts
describe('Rate Limit Handler', () => {
  let rateLimitHandler: RateLimitHandler;
  let mockRequest: jest.Mock;

  beforeEach(() => {
    rateLimitHandler = new DefaultRateLimitHandler();
    mockRequest = jest.fn();
  });

  describe('executeWithRateLimit', () => {
    it('should execute request normally when not rate limited', async () => {
      mockRequest.mockResolvedValue('success');

      const result = await rateLimitHandler.executeWithRateLimit(mockRequest);

      expect(result).toBe('success');
      expect(mockRequest).toHaveBeenCalledTimes(1);
    });

    it('should retry on rate limit error', async () => {
      const rateLimitError = new RateLimitError(
        {
          status: RateLimitStatus.LIMITED,
          limits: {
            maxRequests: 100,
            remainingRequests: 0,
            usedRequests: 100,
            resetAt: new Date(Date.now() + 60000).toISOString(),
            resetInSeconds: 60,
          },
          window: {
            duration: 3600,
            type: RateLimitWindowType.PER_HOUR,
          },
        },
        60
      );

      mockRequest.mockRejectedValueOnce(rateLimitError).mockResolvedValue('success');

      const result = await rateLimitHandler.executeWithRateLimit(mockRequest);

      expect(result).toBe('success');
      expect(mockRequest).toHaveBeenCalledTimes(2);
    });

    it('should respect max retry limit', async () => {
      const rateLimitError = new RateLimitError(
        {
          status: RateLimitStatus.LIMITED,
          limits: {
            maxRequests: 100,
            remainingRequests: 0,
            usedRequests: 100,
            resetAt: new Date(Date.now() + 60000).toISOString(),
            resetInSeconds: 60,
          },
          window: {
            duration: 3600,
            type: RateLimitWindowType.PER_HOUR,
          },
        },
        60
      );

      mockRequest.mockRejectedValue(rateLimitError);

      await expect(rateLimitHandler.executeWithRateLimit(mockRequest)).rejects.toThrow(
        'Max retries (5) exceeded'
      );

      expect(mockRequest).toHaveBeenCalledTimes(6); // 1 initial + 5 retries
    });

    it('should use exponential backoff', async () => {
      const rateLimitError = new RateLimitError(
        {
          status: RateLimitStatus.LIMITED,
          limits: {
            maxRequests: 100,
            remainingRequests: 0,
            usedRequests: 100,
            resetAt: new Date(Date.now() + 60000).toISOString(),
            resetInSeconds: 60,
          },
          window: {
            duration: 3600,
            type: RateLimitWindowType.PER_HOUR,
          },
        },
        1 // 1 second retry after
      );

      const startTime = Date.now();

      mockRequest
        .mockRejectedValueOnce(rateLimitError)
        .mockRejectedValueOnce(rateLimitError)
        .mockResolvedValue('success');

      await rateLimitHandler.executeWithRateLimit(mockRequest);

      const elapsed = Date.now() - startTime;

      // Should have waited with exponential backoff (1s + 2s + jitter)
      expect(elapsed).toBeGreaterThan(2500);
      expect(mockRequest).toHaveBeenCalledTimes(3);
    });
  });

  describe('updateRateLimit', () => {
    it('should update current rate limit info', () => {
      const rateLimitInfo: RateLimitInfo = {
        status: RateLimitStatus.WARNING,
        limits: {
          maxRequests: 100,
          remainingRequests: 20,
          usedRequests: 80,
          resetAt: new Date(Date.now() + 3600000).toISOString(),
          resetInSeconds: 3600,
        },
        window: {
          duration: 3600,
          type: RateLimitWindowType.PER_HOUR,
        },
      };

      rateLimitHandler.updateRateLimit(rateLimitInfo);

      expect(rateLimitHandler.getCurrentRateLimit()).toEqual(rateLimitInfo);
    });
  });

  describe('resetRateLimit', () => {
    it('should reset rate limit tracking', () => {
      const rateLimitInfo: RateLimitInfo = {
        status: RateLimitStatus.WARNING,
        limits: {
          maxRequests: 100,
          remainingRequests: 20,
          usedRequests: 80,
          resetAt: new Date(Date.now() + 3600000).toISOString(),
          resetInSeconds: 3600,
        },
        window: {
          duration: 3600,
          type: RateLimitWindowType.PER_HOUR,
        },
      };

      rateLimitHandler.updateRateLimit(rateLimitInfo);
      expect(rateLimitHandler.getCurrentRateLimit()).not.toBeNull();

      rateLimitHandler.resetRateLimit();
      expect(rateLimitHandler.getCurrentRateLimit()).toBeNull();
    });
  });
});
```

### 5. Integration Test Suite

```typescript
// integration/git-platform.integration.test.ts
describe('Git Platform Integration Tests', () => {
  let platforms: IGitPlatform[];

  beforeAll(async () => {
    // Initialize all available platforms for testing
    platforms = [
      new MockPlatform(),
      // Add real platform instances with test credentials
      // new GitHubPlatform(testConfig),
      // new GitLabPlatform(testConfig),
    ];
  });

  describe('Repository Operations Integration', () => {
    test.each(platforms)('should handle repository lifecycle on %s', async (platform) => {
      const owner = 'test-owner';
      const repo = `test-repo-${Date.now()}`;

      try {
        // Create repository
        const createOptions: CreateRepositoryOptions = {
          description: 'Integration test repository',
          private: true,
          autoInit: true,
        };

        const createdRepo = await platform.createRepository(owner, repo, createOptions);
        expect(createdRepo.name).toBe(repo);
        expect(createdRepo.fullName).toBe(`${owner}/${repo}`);
        expect(createdRepo.description).toBe(createOptions.description);
        expect(createdRepo.private).toBe(createOptions.private);

        // Get repository
        const retrievedRepo = await platform.getRepository(owner, repo);
        expect(retrievedRepo.id).toBe(createdRepo.id);
        expect(retrievedRepo.name).toBe(createdRepo.name);
      } catch (error) {
        // Skip if platform doesn't support repository creation
        if (error instanceof NotImplementedError) {
          console.warn(`Repository creation not implemented for ${platform.platformName}`);
          return;
        }
        throw error;
      }
    });
  });

  describe('Pull Request Workflow Integration', () => {
    test.each(platforms)('should handle complete PR workflow on %s', async (platform) => {
      const owner = 'test-owner';
      const repo = 'test-repo';
      const headBranch = `feature-${Date.now()}`;
      const baseBranch = 'main';

      try {
        // Create branches
        await platform.createBranch(owner, repo, headBranch, baseBranch);

        // Create pull request
        const createPROptions: CreatePROptions = {
          title: 'Integration Test PR',
          body: 'This is a test pull request for integration testing',
          head: headBranch,
          base: baseBranch,
          draft: false,
        };

        const pr = await platform.createPR(owner, repo, createPROptions);
        expect(pr.title).toBe(createPROptions.title);
        expect(pr.body).toBe(createPROptions.body);
        expect(pr.head.ref).toBe(headBranch);
        expect(pr.base.ref).toBe(baseBranch);
        expect(pr.state).toBe(PRState.OPEN);

        // Get pull request
        const retrievedPR = await platform.getPR(owner, repo, pr.number);
        expect(retrievedPR.id).toBe(pr.id);
        expect(retrievedPR.number).toBe(pr.number);

        // Add comment
        const comment = await platform.commentOnPR(owner, repo, pr.number, 'Test comment');
        expect(comment.body).toBe('Test comment');

        // List pull requests
        const prList = await platform.listPRs(owner, repo, { state: 'open' });
        expect(prList.data).toContainEqual(expect.objectContaining({ id: pr.id }));

        // Merge pull request
        const mergeResult = await platform.mergePR(owner, repo, pr.number, {
          method: MergeMethod.SQUASH,
        });
        expect(mergeResult.merged).toBe(true);

        // Verify merged state
        const mergedPR = await platform.getPR(owner, repo, pr.number);
        expect(mergedPR.state).toBe(PRState.MERGED);
      } catch (error) {
        // Skip if platform doesn't support full PR workflow
        if (error instanceof NotImplementedError) {
          console.warn(`Full PR workflow not implemented for ${platform.platformName}`);
          return;
        }
        throw error;
      }
    });
  });

  describe('Issue Management Integration', () => {
    test.each(platforms)('should handle issue lifecycle on %s', async (platform) => {
      const owner = 'test-owner';
      const repo = 'test-repo';

      try {
        // Create issue
        const createIssueOptions: CreateIssueOptions = {
          title: 'Integration Test Issue',
          body: 'This is a test issue for integration testing',
          labels: ['bug', 'integration-test'],
        };

        const issue = await platform.createIssue(owner, repo, createIssueOptions);
        expect(issue.title).toBe(createIssueOptions.title);
        expect(issue.body).toBe(createIssueOptions.body);
        expect(issue.labels.map((l) => l.name)).toContain('bug');
        expect(issue.labels.map((l) => l.name)).toContain('integration-test');
        expect(issue.state).toBe(IssueState.OPEN);

        // Get issue
        const retrievedIssue = await platform.getIssue(owner, repo, issue.number);
        expect(retrievedIssue.id).toBe(issue.id);
        expect(retrievedIssue.number).toBe(issue.number);

        // Add comment
        const comment = await platform.commentOnIssue(owner, repo, issue.number, 'Test comment');
        expect(comment.body).toBe('Test comment');

        // List issues
        const issueList = await platform.listIssues(owner, repo, { state: 'open' });
        expect(issueList.data).toContainEqual(expect.objectContaining({ id: issue.id }));
      } catch (error) {
        // Skip if platform doesn't support issue management
        if (error instanceof NotImplementedError) {
          console.warn(`Issue management not implemented for ${platform.platformName}`);
          return;
        }
        throw error;
      }
    });
  });

  describe('Platform Capabilities Integration', () => {
    test.each(platforms)('should provide valid capabilities on %s', async (platform) => {
      const capabilities = await platform.getCapabilities();

      expect(capabilities.platform).toBeDefined();
      expect(capabilities.platform.name).toBe(platform.platformName);
      expect(capabilities.repository).toBeDefined();
      expect(capabilities.branch).toBeDefined();
      expect(capabilities.pullRequest).toBeDefined();
      expect(capabilities.issue).toBeDefined();
      expect(capabilities.ci).toBeDefined();
      expect(capabilities.webhook).toBeDefined();
      expect(capabilities.authentication).toBeDefined();
      expect(capabilities.rateLimit).toBeDefined();
      expect(capabilities.api).toBeDefined();
    });
  });

  describe('Error Handling Integration', () => {
    test.each(platforms)('should handle not found errors gracefully on %s', async (platform) => {
      await expect(platform.getRepository('nonexistent', 'repo')).rejects.toThrow();

      await expect(platform.getPR('owner', 'repo', 99999)).rejects.toThrow();

      await expect(platform.getIssue('owner', 'repo', 99999)).rejects.toThrow();

      await expect(platform.getBranch('owner', 'repo', 'nonexistent')).rejects.toThrow();
    });
  });
});

// Error class for unsupported operations
class NotImplementedError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'NotImplementedError';
  }
}
```

## File Structure

```
packages/platforms/src/__tests__/
├── contracts/
│   ├── git-platform.contract.test.ts                 # Interface contract tests
│   ├── pagination.contract.test.ts                   # Pagination contract tests
│   └── rate-limit.contract.test.ts                   # Rate limit contract tests
├── mocks/
│   ├── index.ts                                      # Mock exports
│   ├── mock-platform.ts                              # Mock platform implementation
│   ├── mock-transformer.ts                           # Mock transformer
│   ├── mock-http-client.ts                           # Mock HTTP client
│   └── fixtures/                                     # Test data fixtures
│       ├── repositories.json
│       ├── pull-requests.json
│       ├── issues.json
│       ├── branches.json
│       └── webhooks.json
├── pagination/
│   ├── pagination.test.ts                            # Pagination utility tests
│   ├── strategies/
│   │   ├── page-based.test.ts                        # Page-based pagination tests
│   │   ├── offset-based.test.ts                      # Offset-based pagination tests
│   │   └── cursor-based.test.ts                      # Cursor-based pagination tests
│   └── integration/
│       └── pagination.integration.test.ts             # Pagination integration tests
├── rate-limit/
│   ├── rate-limit.test.ts                            # Rate limit handler tests
│   ├── detection.test.ts                             # Rate limit detection tests
│   ├── recovery.test.ts                              # Rate limit recovery tests
│   └── integration/
│       └── rate-limit.integration.test.ts            # Rate limit integration tests
├── transformers/
│   ├── transformer.test.ts                           # Transformer tests
│   ├── github.transformer.test.ts                    # GitHub transformer tests
│   ├── gitlab.transformer.test.ts                    # GitLab transformer tests
│   └── integration/
│       └── transformer.integration.test.ts            # Transformer integration tests
├── platforms/
│   ├── github/
│   │   ├── github.platform.test.ts                   # GitHub platform tests
│   │   └── integration/
│   │       └── github.integration.test.ts             # GitHub integration tests
│   ├── gitlab/
│   │   ├── gitlab.platform.test.ts                   # GitLab platform tests
│   │   └── integration/
│   │       └── gitlab.integration.test.ts             # GitLab integration tests
│   └── mock/
│       ├── mock.platform.test.ts                     # Mock platform tests
│       └── integration/
│           └── mock.integration.test.ts               # Mock integration tests
├── integration/
│   ├── git-platform.integration.test.ts               # Cross-platform integration tests
│   ├── workflows/
│   │   ├── pr-workflow.test.ts                       # PR workflow integration tests
│   │   ├── issue-workflow.test.ts                    # Issue workflow integration tests
│   │   └── repository-workflow.test.ts                # Repository workflow integration tests
│   └── performance/
│       ├── pagination.performance.test.ts             # Pagination performance tests
│       └── rate-limit.performance.test.ts            # Rate limit performance tests
└── utils/
    ├── test-helpers.ts                               # Test helper utilities
    ├── mock-factories.ts                             # Mock data factories
    └── test-config.ts                                # Test configuration
```

## Testing Strategy

### Test Coverage Requirements

1. **Interface Coverage**: 100% coverage of all interface methods
2. **Edge Case Coverage**: Test all error conditions and edge cases
3. **Platform Coverage**: Test all supported platforms
4. **Integration Coverage**: Test complete workflows end-to-end

### Test Categories

1. **Unit Tests**: Individual component testing
2. **Integration Tests**: Cross-component testing
3. **Contract Tests**: Interface compliance testing
4. **Performance Tests**: Load and stress testing
5. **Error Handling Tests**: Error condition testing

### Test Data Management

1. **Mock Data**: Comprehensive mock data for all scenarios
2. **Fixtures**: Reusable test data fixtures
3. **Factories**: Dynamic test data generation
4. **Cleanup**: Proper test cleanup and isolation

## Completion Checklist

- [ ] Create comprehensive interface contract tests
- [ ] Implement mock platform for testing consumers
- [ ] Add pagination tests for all strategies
- [ ] Create rate limit handling tests
- [ ] Build integration test suite
- [ ] Add performance tests
- [ ] Create test utilities and helpers
- [ ] Implement test data factories
- [ ] Add error handling tests
- [ ] Document testing procedures

## Dependencies

- Task 1: Core Git Platform Interface Structure (interfaces to test)
- Task 2: Platform Capabilities Discovery (capabilities to test)
- Task 3: Data Model Normalization (transformations to test)
- Task 4: Pagination and Rate Limit Support (functionality to test)

## Risks and Mitigations

**Risk**: Test complexity leads to brittle tests
**Mitigation**: Use helper utilities, mock factories, and clear test structure

**Risk**: Integration tests require real credentials
**Mitigation**: Use mock implementations for most tests, limited real integration tests

**Risk**: Performance tests are flaky
**Mitigation**: Use appropriate test environments, consistent test data

## Success Criteria

- 100% interface coverage with comprehensive tests
- Reliable mock implementations for consumer testing
- Complete pagination and rate limit test coverage
- End-to-end integration tests for all workflows
- High-performance test suite with minimal flakiness

This comprehensive testing suite ensures interface compliance, validates behavior across platforms, and provides reliable testing infrastructure for the Git platform abstraction layer.
