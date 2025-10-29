# Story 2.4: Git Branch Creation

**Epic**: Epic 2 - Autonomous Development Loop - Core  
**Status**: Ready for Development  
**Priority**: High  
**Prerequisites**: Story 2.3 (development plan approval must complete first)

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
I want the system to create a Git branch for the approved development plan,
So that implementation work can be isolated from the main codebase.

---

## Acceptance Criteria

1. System creates a new Git branch based on approved development plan
2. Branch name follows configurable naming convention (e.g., feature/123-issue-title)
3. System handles branch name conflicts by appending suffix
4. System switches to the new branch for subsequent operations
5. System validates branch creation was successful
6. Branch creation and switching logged to event trail
7. Integration test validates branch creation workflow
8. Error handling for insufficient permissions, conflicts, and network issues

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

**BranchManager Service**:

```typescript
interface IBranchManager {
  createBranch(plan: DevelopmentPlan): Promise<BranchInfo>;
  switchBranch(branchName: string): Promise<void>;
  validateBranchCreation(branchName: string): Promise<boolean>;
  handleBranchConflict(baseName: string): Promise<string>;
  deleteBranch(branchName: string): Promise<void>;
  getCurrentBranch(): Promise<string>;
}

interface BranchInfo {
  name: string;
  baseBranch: string;
  issueNumber: number;
  issueTitle: string;
  createdAt: Date;
  createdBy: string;
  url?: string;
}

interface BranchCreationConfig {
  namingPattern: string; // e.g., "feature/{issue-number}-{issue-title}"
  baseBranch: string; // e.g., "main", "develop"
  maxNameLength: number; // Git branch name limit
  sanitizeNames: boolean; // Remove special characters
  conflictResolution: 'suffix' | 'timestamp' | 'abort';
  autoSwitch: boolean; // Switch to new branch after creation
}
```

### Implementation Strategy

**1. Branch Creation Engine**:

```typescript
class BranchManager implements IBranchManager {
  constructor(
    private gitPlatform: IGitPlatform,
    private config: BranchCreationConfig,
    private logger: Logger,
    private eventStore: IEventStore
  ) {}

  async createBranch(plan: DevelopmentPlan): Promise<BranchInfo> {
    const startTime = Date.now();

    try {
      // Generate branch name from plan
      const branchName = this.generateBranchName(plan);

      // Check for conflicts and resolve if needed
      const finalBranchName = await this.handleBranchConflict(branchName);

      // Create the branch
      const branchInfo = await this.createBranchOnPlatform(finalBranchName, plan);

      // Switch to new branch if configured
      if (this.config.autoSwitch) {
        await this.switchBranch(finalBranchName);
      }

      // Log successful branch creation
      await this.eventStore.append({
        type: 'BRANCH.CREATED.SUCCESS',
        tags: {
          issueId: plan.issueId,
          issueNumber: plan.issueNumber.toString(),
          planId: plan.id,
          branchName: finalBranchName,
        },
        data: {
          baseBranch: this.config.baseBranch,
          createdAt: branchInfo.createdAt.toISOString(),
          creationTime: Date.now() - startTime,
          autoSwitched: this.config.autoSwitch,
        },
      });

      this.logger.info('Branch created successfully', {
        issueNumber: plan.issueNumber,
        branchName: finalBranchName,
        baseBranch: this.config.baseBranch,
      });

      return branchInfo;
    } catch (error) {
      await this.eventStore.append({
        type: 'BRANCH.CREATED.FAILED',
        tags: {
          issueId: plan.issueId,
          issueNumber: plan.issueNumber.toString(),
          planId: plan.id,
        },
        data: {
          error: error.message,
          creationTime: Date.now() - startTime,
        },
      });

      throw error;
    }
  }

  private generateBranchName(plan: DevelopmentPlan): string {
    const { namingPattern, maxNameLength, sanitizeNames } = this.config;

    // Extract variables from plan
    const variables = {
      'issue-number': plan.issueNumber.toString(),
      'issue-title': this.sanitizeTitle(plan.title),
      'plan-id': plan.id,
      timestamp: Date.now().toString(),
    };

    // Replace placeholders in pattern
    let branchName = namingPattern;
    for (const [key, value] of Object.entries(variables)) {
      branchName = branchName.replace(new RegExp(`{${key}}`, 'g'), value);
    }

    // Sanitize if enabled
    if (sanitizeNames) {
      branchName = this.sanitizeBranchName(branchName);
    }

    // Enforce length limit
    if (branchName.length > maxNameLength) {
      branchName = this.truncateBranchName(branchName, maxNameLength);
    }

    return branchName;
  }

  private sanitizeTitle(title: string): string {
    return title
      .toLowerCase()
      .replace(/[^a-z0-9\s-]/g, '') // Remove special characters
      .replace(/\s+/g, '-') // Replace spaces with hyphens
      .replace(/-+/g, '-') // Remove consecutive hyphens
      .trim();
  }

  private sanitizeBranchName(name: string): string {
    return name
      .toLowerCase()
      .replace(/[^a-z0-9-_\/]/g, '') // Allowed: alphanumeric, hyphen, underscore, slash
      .replace(/^[._-]/, '') // Don't start with special chars
      .replace(/[._-]$/, '') // Don't end with special chars
      .replace(/\/+/g, '/') // Remove consecutive slashes
      .trim();
  }

  private truncateBranchName(name: string, maxLength: number): string {
    if (name.length <= maxLength) {
      return name;
    }

    // Try to truncate at word boundaries
    const truncated = name.substring(0, maxLength - 8); // Leave room for suffix
    const lastHyphen = truncated.lastIndexOf('-');

    if (lastHyphen > truncated.length * 0.7) {
      return truncated.substring(0, lastHyphen);
    }

    return truncated;
  }

  async handleBranchConflict(baseName: string): Promise<string> {
    try {
      // Check if branch already exists
      const exists = await this.gitPlatform.branchExists(baseName);

      if (!exists) {
        return baseName;
      }

      this.logger.warn('Branch already exists, applying conflict resolution', {
        baseName,
        strategy: this.config.conflictResolution,
      });

      switch (this.config.conflictResolution) {
        case 'suffix':
          return await this.addConflictSuffix(baseName);

        case 'timestamp':
          return await this.addTimestampSuffix(baseName);

        case 'abort':
          throw new Error(
            `Branch '${baseName}' already exists and conflict resolution is set to abort`
          );

        default:
          throw new Error(
            `Unknown conflict resolution strategy: ${this.config.conflictResolution}`
          );
      }
    } catch (error) {
      if (error.message.includes('already exists')) {
        throw error;
      }

      // If we can't check existence, assume it doesn't and proceed
      this.logger.warn('Could not verify branch existence, proceeding with original name', {
        baseName,
        error: error.message,
      });

      return baseName;
    }
  }

  private async addConflictSuffix(baseName: string): Promise<string> {
    let suffix = 2;
    let candidateName = `${baseName}-${suffix}`;

    while (await this.gitPlatform.branchExists(candidateName)) {
      suffix++;
      candidateName = `${baseName}-${suffix}`;

      // Prevent infinite loop
      if (suffix > 100) {
        throw new Error(`Cannot find available branch name for '${baseName}' after 100 attempts`);
      }
    }

    this.logger.info('Resolved branch conflict with numeric suffix', {
      baseName,
      finalName: candidateName,
      suffix,
    });

    return candidateName;
  }

  private async addTimestampSuffix(baseName: string): Promise<string> {
    const timestamp = Date.now().toString();
    const candidateName = `${baseName}-${timestamp}`;

    // Double-check timestamp uniqueness (very unlikely to conflict)
    if (await this.gitPlatform.branchExists(candidateName)) {
      // Fallback to numeric suffix
      return await this.addConflictSuffix(baseName);
    }

    this.logger.info('Resolved branch conflict with timestamp', {
      baseName,
      finalName: candidateName,
      timestamp,
    });

    return candidateName;
  }

  private async createBranchOnPlatform(
    branchName: string,
    plan: DevelopmentPlan
  ): Promise<BranchInfo> {
    try {
      // Create branch on Git platform
      const branch = await this.gitPlatform.createBranch({
        name: branchName,
        baseBranch: this.config.baseBranch,
        issueNumber: plan.issueNumber,
      });

      const branchInfo: BranchInfo = {
        name: branch.name,
        baseBranch: this.config.baseBranch,
        issueNumber: plan.issueNumber,
        issueTitle: plan.title,
        createdAt: new Date(),
        createdBy: 'tamma-bot',
        url: branch.url,
      };

      return branchInfo;
    } catch (error) {
      // Handle common Git platform errors
      if (error.message.includes('permission')) {
        throw new BranchCreationError(
          `Insufficient permissions to create branch '${branchName}'`,
          'permission_denied',
          branchName
        );
      }

      if (error.message.includes('not found')) {
        throw new BranchCreationError(
          `Base branch '${this.config.baseBranch}' not found`,
          'base_branch_not_found',
          branchName
        );
      }

      if (error.message.includes('protected')) {
        throw new BranchCreationError(
          `Base branch '${this.config.baseBranch}' is protected`,
          'base_branch_protected',
          branchName
        );
      }

      throw new BranchCreationError(
        `Failed to create branch '${branchName}': ${error.message}`,
        'unknown_error',
        branchName,
        error
      );
    }
  }

  async switchBranch(branchName: string): Promise<void> {
    try {
      await this.gitPlatform.switchBranch(branchName);

      this.logger.info('Switched to branch', { branchName });

      await this.eventStore.append({
        type: 'BRANCH.SWITCHED.SUCCESS',
        tags: {
          branchName,
        },
        data: {
          switchedAt: new Date().toISOString(),
        },
      });
    } catch (error) {
      await this.eventStore.append({
        type: 'BRANCH.SWITCHED.FAILED',
        tags: {
          branchName,
        },
        data: {
          error: error.message,
        },
      });

      throw new BranchOperationError(
        `Failed to switch to branch '${branchName}': ${error.message}`,
        'switch_failed',
        branchName,
        error
      );
    }
  }

  async validateBranchCreation(branchName: string): Promise<boolean> {
    try {
      const exists = await this.gitPlatform.branchExists(branchName);
      const currentBranch = await this.getCurrentBranch();

      return exists && currentBranch === branchName;
    } catch (error) {
      this.logger.warn('Could not validate branch creation', {
        branchName,
        error: error.message,
      });

      return false;
    }
  }

  async deleteBranch(branchName: string): Promise<void> {
    try {
      await this.gitPlatform.deleteBranch(branchName);

      this.logger.info('Branch deleted', { branchName });

      await this.eventStore.append({
        type: 'BRANCH.DELETED.SUCCESS',
        tags: {
          branchName,
        },
        data: {
          deletedAt: new Date().toISOString(),
        },
      });
    } catch (error) {
      await this.eventStore.append({
        type: 'BRANCH.DELETED.FAILED',
        tags: {
          branchName,
        },
        data: {
          error: error.message,
        },
      });

      throw new BranchOperationError(
        `Failed to delete branch '${branchName}': ${error.message}`,
        'delete_failed',
        branchName,
        error
      );
    }
  }

  async getCurrentBranch(): Promise<string> {
    try {
      return await this.gitPlatform.getCurrentBranch();
    } catch (error) {
      this.logger.error('Failed to get current branch', { error });
      throw new BranchOperationError(
        'Failed to determine current branch',
        'get_current_failed',
        undefined,
        error
      );
    }
  }
}
```

**2. Branch Creation Error Handling**:

```typescript
class BranchCreationError extends Error {
  constructor(
    message: string,
    public readonly code: string,
    public readonly branchName: string,
    public readonly cause?: Error
  ) {
    super(message);
    this.name = 'BranchCreationError';
  }
}

class BranchOperationError extends Error {
  constructor(
    message: string,
    public readonly code: string,
    public readonly branchName?: string,
    public readonly cause?: Error
  ) {
    super(message);
    this.name = 'BranchOperationError';
  }
}

class BranchCreationService {
  constructor(
    private branchManager: IBranchManager,
    private config: BranchCreationConfig,
    private logger: Logger
  ) {}

  async createBranchWithRetry(plan: DevelopmentPlan): Promise<BranchInfo> {
    const maxAttempts = 3;
    let lastError: Error;

    for (let attempt = 1; attempt <= maxAttempts; attempt++) {
      try {
        return await this.branchManager.createBranch(plan);
      } catch (error) {
        lastError = error;

        // Don't retry certain errors
        if (error instanceof BranchCreationError) {
          switch (error.code) {
            case 'permission_denied':
            case 'base_branch_not_found':
            case 'base_branch_protected':
              throw error; // These won't be fixed by retrying
          }
        }

        if (attempt === maxAttempts) {
          break;
        }

        // Exponential backoff for retryable errors
        const delay = Math.pow(2, attempt) * 1000;
        this.logger.warn(`Branch creation attempt ${attempt} failed, retrying in ${delay}ms`, {
          attempt,
          maxAttempts,
          error: error.message,
        });

        await this.sleep(delay);
      }
    }

    throw lastError;
  }

  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }
}
```

### Integration Points

**1. Git Platform Integration**:

- `createBranch()` - Create new branch from base
- `branchExists()` - Check if branch already exists
- `switchBranch()` - Switch working branch
- `getCurrentBranch()` - Get current branch name
- `deleteBranch()` - Clean up branches

**2. Event Store Integration**:

- `BRANCH.CREATED.SUCCESS/FAILED`
- `BRANCH.SWITCHED.SUCCESS/FAILED`
- `BRANCH.DELETED.SUCCESS/FAILED`
- Complete audit trail for branch operations

**3. Configuration Management**:

- Branch naming patterns
- Conflict resolution strategies
- Base branch selection
- Permission handling

### Testing Strategy

**Unit Tests**:

```typescript
describe('BranchManager', () => {
  let branchManager: BranchManager;
  let mockGitPlatform: jest.Mocked<IGitPlatform>;

  beforeEach(() => {
    mockGitPlatform = createMockGitPlatform();
    branchManager = new BranchManager(
      mockGitPlatform,
      createMockConfig(),
      mockLogger,
      mockEventStore
    );
  });

  describe('generateBranchName', () => {
    it('should generate branch name from pattern', () => {
      const plan = createMockDevelopmentPlan({
        issueNumber: 123,
        title: 'Add User Authentication Feature',
      });

      const config = {
        namingPattern: 'feature/{issue-number}-{issue-title}',
        sanitizeNames: true,
        maxNameLength: 100,
      };

      const manager = new BranchManager(mockGitPlatform, config, mockLogger, mockEventStore);
      const branchName = manager['generateBranchName'](plan);

      expect(branchName).toBe('feature/123-add-user-authentication-feature');
    });

    it('should truncate long branch names', () => {
      const plan = createMockDevelopmentPlan({
        issueNumber: 456,
        title:
          'This is a very long issue title that would normally exceed the maximum branch name length limit',
      });

      const config = {
        namingPattern: 'feature/{issue-number}-{issue-title}',
        sanitizeNames: true,
        maxNameLength: 50,
      };

      const manager = new BranchManager(mockGitPlatform, config, mockLogger, mockEventStore);
      const branchName = manager['generateBranchName'](plan);

      expect(branchName.length).toBeLessThanOrEqual(50);
      expect(branchName).toContain('456');
    });

    it('should sanitize special characters', () => {
      const plan = createMockDevelopmentPlan({
        issueNumber: 789,
        title: 'Fix: Bug with @special#chars! in title',
      });

      const config = {
        namingPattern: 'bugfix/{issue-number}-{issue-title}',
        sanitizeNames: true,
        maxNameLength: 100,
      };

      const manager = new BranchManager(mockGitPlatform, config, mockLogger, mockEventStore);
      const branchName = manager['generateBranchName'](plan);

      expect(branchName).toBe('bugfix/789-fix-bug-with-special-chars-in-title');
    });
  });

  describe('handleBranchConflict', () => {
    it('should add numeric suffix for conflicts', async () => {
      const baseName = 'feature/123-add-auth';

      mockGitPlatform.branchExists
        .mockResolvedValueOnce(true) // First call: branch exists
        .mockResolvedValueOnce(false) // feature/123-add-auth-2: available
        .mockResolvedValueOnce(false); // feature/123-add-auth-3: available (not needed)

      const config = {
        conflictResolution: 'suffix' as const,
      };

      const manager = new BranchManager(mockGitPlatform, config, mockLogger, mockEventStore);
      const result = await manager.handleBranchConflict(baseName);

      expect(result).toBe('feature/123-add-auth-2');
    });

    it('should add timestamp for conflicts', async () => {
      const baseName = 'feature/456-fix-bug';

      mockGitPlatform.branchExists.mockResolvedValueOnce(true);

      const config = {
        conflictResolution: 'timestamp' as const,
      };

      const manager = new BranchManager(mockGitPlatform, config, mockLogger, mockEventStore);
      const result = await manager.handleBranchConflict(baseName);

      expect(result).toMatch(/^feature\/456-fix-bug-\d+$/);
    });

    it('should abort on conflicts when configured', async () => {
      const baseName = 'feature/789-new-feature';

      mockGitPlatform.branchExists.mockResolvedValueOnce(true);

      const config = {
        conflictResolution: 'abort' as const,
      };

      const manager = new BranchManager(mockGitPlatform, config, mockLogger, mockEventStore);

      await expect(manager.handleBranchConflict(baseName)).rejects.toThrow(
        'already exists and conflict resolution is set to abort'
      );
    });
  });

  describe('createBranch', () => {
    it('should create branch successfully', async () => {
      const plan = createMockDevelopmentPlan();

      mockGitPlatform.branchExists.mockResolvedValue(false);
      mockGitPlatform.createBranch.mockResolvedValue({
        name: 'feature/123-add-auth',
        url: 'https://github.com/test/repo/tree/feature/123-add-auth',
      });

      const result = await branchManager.createBranch(plan);

      expect(result.name).toBe('feature/123-add-auth');
      expect(result.issueNumber).toBe(123);
      expect(mockGitPlatform.createBranch).toHaveBeenCalledWith({
        name: expect.any(String),
        baseBranch: 'main',
        issueNumber: 123,
      });
    });

    it('should handle permission errors', async () => {
      const plan = createMockDevelopmentPlan();

      mockGitPlatform.branchExists.mockResolvedValue(false);
      mockGitPlatform.createBranch.mockRejectedValue(new Error('Permission denied'));

      await expect(branchManager.createBranch(plan)).rejects.toThrow(BranchCreationError);
    });
  });
});
```

**Integration Tests**:

```typescript
describe('BranchManager Integration', () => {
  it('should create real branch on GitHub', async () => {
    if (!process.env.GITHUB_TOKEN_TEST) {
      return; // Skip without test credentials
    }

    const githubPlatform = new GitHubPlatform({
      token: process.env.GITHUB_TOKEN_TEST,
      repository: { owner: 'tamma', name: 'test-repo' },
    });

    const config = {
      namingPattern: 'test/{issue-number}-{issue-title}',
      baseBranch: 'main',
      maxNameLength: 100,
      sanitizeNames: true,
      conflictResolution: 'suffix' as const,
      autoSwitch: false,
    };

    const branchManager = new BranchManager(githubPlatform, config, logger, eventStore);

    const plan = createMockDevelopmentPlan({
      issueNumber: 999,
      title: 'Integration Test Branch',
    });

    const result = await branchManager.createBranch(plan);

    expect(result.name).toContain('test/999-integration-test-branch');
    expect(result.issueNumber).toBe(999);

    // Cleanup
    await branchManager.deleteBranch(result.name);
  });
});
```

### Monitoring and Observability

**Metrics to Track**:

- Branch creation success rate
- Branch conflict resolution frequency
- Average branch creation time
- Branch naming pattern effectiveness
- Permission error rates

**Logging Strategy**:

```typescript
logger.info('Branch creation started', {
  issueNumber: plan.issueNumber,
  planId: plan.id,
  baseBranch: this.config.baseBranch,
  namingPattern: this.config.namingPattern,
});

logger.info('Branch conflict resolved', {
  baseName,
  finalName: resolvedName,
  strategy: this.config.conflictResolution,
  attempts: suffix,
});

logger.error('Branch creation failed', {
  issueNumber: plan.issueNumber,
  branchName: attemptedName,
  error: error.message,
  errorCode: error.code,
  retryAttempt: attempt,
});
```

### Configuration Examples

**Branch Creation Configuration**:

```yaml
branch_creation:
  naming_pattern: 'feature/{issue-number}-{issue-title}'
  base_branch: 'main'
  max_name_length: 100
  sanitize_names: true
  conflict_resolution: 'suffix' # suffix, timestamp, abort
  auto_switch: true

  # Platform-specific settings
  github:
    default_base: 'main'
    protected_branches: ['main', 'develop', 'release/*']

  gitlab:
    default_base: 'main'
    protected_branches: ['main', 'develop']

  # Advanced options
  advanced:
    validate_base_branch: true
    check_permissions: true
    cleanup_on_failure: false
    branch_description_template: |
      Addresses issue #{issue-number}: {issue-title}

      Development Plan: {plan-id}
      Created by: Tamma Bot
      Created at: {created_at}
```

---

## Implementation Notes

**Key Considerations**:

1. **Branch Naming**: Consistent naming patterns are crucial for organization and automation.

2. **Conflict Resolution**: Multiple strategies ensure branch creation succeeds even with naming conflicts.

3. **Permission Handling**: Proper error handling for protected branches and insufficient permissions.

4. **Audit Trail**: Complete logging of branch operations for compliance and debugging.

5. **Platform Differences**: Handle variations between Git platforms (GitHub, GitLab, etc.).

6. **Cleanup Strategy**: Consider automatic branch cleanup after PR merge or abandonment.

**Performance Targets**:

- Branch creation: < 5 seconds
- Branch switching: < 2 seconds
- Conflict resolution: < 10 seconds
- Validation: < 1 second

**Security Considerations**:

- Validate branch names to prevent injection attacks
- Check permissions before attempting operations
- Use appropriate authentication tokens
- Handle sensitive branch names appropriately
- Implement rate limiting for branch operations

**Git Best Practices**:

- Use descriptive branch names
- Keep branch names under reasonable length limits
- Avoid special characters that cause issues
- Follow team conventions for branch organization
- Consider branch protection rules for important branches

### References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md) - Search spikes, bugs, findings, decisions
