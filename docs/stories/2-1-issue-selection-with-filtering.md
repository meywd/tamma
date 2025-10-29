# Story 2.1: Issue Selection with Filtering

**Epic**: Epic 2 - Autonomous Development Loop - Core  
**Status**: Ready for Development  
**Priority**: High  
**Prerequisites**: Story 1.5 or 1.6 (Git platform implementation must exist)

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
I want the system to select the next unassigned issue from the configured repository,
So that the autonomous loop can start without manual issue specification.

---

## Acceptance Criteria

1. System queries Git platform API for open issues in configured repository
2. System filters issues by labels (configured inclusion/exclusion labels)
3. System prioritizes issues by age (oldest first) as default strategy
4. System assigns selected issue to configured bot user account
5. System logs issue selection with issue number, title, and URL
6. If no issues match criteria, system enters idle state and polls every 5 minutes
7. Integration test with mock Git platform API

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

**IssueSelector Service**:

```typescript
interface IIssueSelector {
  selectNextIssue(config: IssueSelectionConfig): Promise<SelectedIssue | null>;
  assignIssue(issueId: string, assignee: string): Promise<void>;
  startPolling(config: PollingConfig): void;
  stopPolling(): void;
}

interface IssueSelectionConfig {
  repository: RepositoryConfig;
  includeLabels: string[];
  excludeLabels: string[];
  priorityStrategy: 'oldest' | 'newest' | 'updated' | 'complexity';
  botUserId: string;
}

interface SelectedIssue {
  id: string;
  number: number;
  title: string;
  body: string;
  labels: string[];
  createdAt: Date;
  updatedAt: Date;
  url: string;
}
```

**Configuration Schema**:

```yaml
issue_selection:
  repository:
    owner: 'tamma'
    name: 'tamma'
    platform: 'github' # github, gitlab, etc.

  filtering:
    include_labels: ['bug', 'enhancement', 'feature']
    exclude_labels: ['wontfix', 'duplicate', 'question']
    priority_strategy: 'oldest' # oldest, newest, updated, complexity

  bot:
    user_id: 'tamma-bot'
    auto_assign: true

  polling:
    enabled: true
    interval_seconds: 300 # 5 minutes
    idle_timeout_minutes: 60
```

### Implementation Strategy

**1. Issue Query Pipeline**:

```typescript
class IssueSelector implements IIssueSelector {
  constructor(
    private gitPlatform: IGitPlatform,
    private config: IssueSelectionConfig,
    private logger: Logger,
    private eventStore: IEventStore
  ) {}

  async selectNextIssue(config: IssueSelectionConfig): Promise<SelectedIssue | null> {
    // Query open issues
    const issues = await this.gitPlatform.getIssues({
      state: 'open',
      assignee: null, // Unassigned only
      labels: config.includeLabels,
    });

    // Apply filtering logic
    const filteredIssues = this.filterIssues(issues, config);

    if (filteredIssues.length === 0) {
      return null;
    }

    // Apply priority strategy
    const selectedIssue = this.prioritizeIssues(filteredIssues, config)[0];

    // Auto-assign if configured
    if (config.botUserId) {
      await this.assignIssue(selectedIssue.id, config.botUserId);
    }

    // Log selection event
    await this.eventStore.append({
      type: 'ISSUE.SELECTED.SUCCESS',
      tags: {
        issueId: selectedIssue.id,
        issueNumber: selectedIssue.number,
        repository: `${config.repository.owner}/${config.repository.name}`,
        strategy: config.priorityStrategy,
      },
      data: {
        title: selectedIssue.title,
        labels: selectedIssue.labels,
        selectedAt: new Date().toISOString(),
      },
    });

    return selectedIssue;
  }

  private filterIssues(issues: Issue[], config: IssueSelectionConfig): Issue[] {
    return issues.filter((issue) => {
      // Check exclude labels
      const hasExcludedLabel = config.excludeLabels.some((label) => issue.labels.includes(label));
      if (hasExcludedLabel) return false;

      // Check include labels (if specified)
      if (config.includeLabels.length > 0) {
        const hasIncludedLabel = config.includeLabels.some((label) => issue.labels.includes(label));
        if (!hasIncludedLabel) return false;
      }

      return true;
    });
  }

  private prioritizeIssues(issues: Issue[], config: IssueSelectionConfig): Issue[] {
    switch (config.priorityStrategy) {
      case 'oldest':
        return issues.sort((a, b) => a.createdAt.getTime() - b.createdAt.getTime());
      case 'newest':
        return issues.sort((a, b) => b.createdAt.getTime() - a.createdAt.getTime());
      case 'updated':
        return issues.sort((a, b) => b.updatedAt.getTime() - a.updatedAt.getTime());
      case 'complexity':
        return this.sortByComplexity(issues);
      default:
        return issues;
    }
  }
}
```

**2. Polling Manager**:

```typescript
class PollingManager {
  private pollTimer: NodeJS.Timeout | null = null;
  private isPolling = false;

  constructor(
    private issueSelector: IIssueSelector,
    private config: PollingConfig,
    private logger: Logger
  ) {}

  startPolling(config: PollingConfig): void {
    if (this.isPolling) {
      this.logger.warn('Polling already active');
      return;
    }

    this.isPolling = true;
    this.logger.info('Starting issue polling', { interval: config.intervalSeconds });

    this.pollTimer = setInterval(async () => {
      try {
        const issue = await this.issueSelector.selectNextIssue(config.selectionConfig);

        if (issue) {
          this.logger.info('Issue selected, stopping polling', {
            issueNumber: issue.number,
            title: issue.title,
          });
          this.stopPolling();

          // Trigger workflow start
          await this.startWorkflow(issue);
        } else {
          this.logger.debug('No issues found, continuing polling');
        }
      } catch (error) {
        this.logger.error('Error during issue polling', { error });
      }
    }, config.intervalSeconds * 1000);
  }

  stopPolling(): void {
    if (this.pollTimer) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
    this.isPolling = false;
    this.logger.info('Issue polling stopped');
  }

  private async startWorkflow(issue: SelectedIssue): Promise<void> {
    // Emit event to start autonomous workflow
    await this.eventStore.append({
      type: 'WORKFLOW.STARTED',
      tags: {
        issueId: issue.id,
        issueNumber: issue.number,
        trigger: 'issue-selection',
      },
      data: {
        issue: issue,
        startedAt: new Date().toISOString(),
      },
    });
  }
}
```

### Error Handling

**Retry Strategy**:

```typescript
class IssueSelectionService {
  async selectNextIssueWithRetry(
    config: IssueSelectionConfig,
    maxAttempts = 3
  ): Promise<SelectedIssue | null> {
    let attempt = 0;

    while (attempt < maxAttempts) {
      try {
        return await this.issueSelector.selectNextIssue(config);
      } catch (error) {
        attempt++;

        if (error instanceof RateLimitError) {
          const delay = error.retryAfter || 60;
          this.logger.warn(`Rate limited, waiting ${delay}s`, { attempt });
          await sleep(delay * 1000);
          continue;
        }

        if (error instanceof NetworkError && attempt < maxAttempts) {
          const delay = Math.pow(2, attempt) * 1000; // Exponential backoff
          this.logger.warn(`Network error, retrying in ${delay}ms`, { attempt });
          await sleep(delay);
          continue;
        }

        this.logger.error('Failed to select issue', { error, attempt });
        throw error;
      }
    }

    return null;
  }
}
```

### Integration Points

**1. Git Platform Integration**:

- Uses `IGitPlatform.getIssues()` for querying issues
- Uses `IGitPlatform.assignIssue()` for assignment
- Handles platform-specific rate limits and pagination

**2. Event Store Integration**:

- Emits `ISSUE.SELECTED.SUCCESS` events
- Emits `ISSUE.SELECTION.FAILED` events on errors
- Emits `WORKFLOW.STARTED` events when issue is selected

**3. Configuration Management**:

- Reads from `tamma.config.yaml` issue selection section
- Supports environment variable overrides
- Validates configuration on startup

### Testing Strategy

**Unit Tests**:

```typescript
describe('IssueSelector', () => {
  it('should filter issues by include labels', async () => {
    const mockIssues = [
      createMockIssue({ labels: ['bug', 'high-priority'] }),
      createMockIssue({ labels: ['enhancement'] }),
      createMockIssue({ labels: ['documentation'] }),
    ];

    gitPlatform.getIssues.mockResolvedValue(mockIssues);

    const config = {
      includeLabels: ['bug', 'enhancement'],
      excludeLabels: [],
    };

    const result = await issueSelector.selectNextIssue(config);

    expect(result?.labels).toContain('bug');
    expect(result?.labels).not.toContain('documentation');
  });

  it('should prioritize by oldest first', async () => {
    const mockIssues = [
      createMockIssue({ createdAt: new Date('2025-10-25') }),
      createMockIssue({ createdAt: new Date('2025-10-23') }),
      createMockIssue({ createdAt: new Date('2025-10-24') }),
    ];

    gitPlatform.getIssues.mockResolvedValue(mockIssues);

    const result = await issueSelector.selectNextIssue({
      priorityStrategy: 'oldest',
    });

    expect(result?.createdAt).toEqual(new Date('2025-10-23'));
  });
});
```

**Integration Tests**:

```typescript
describe('IssueSelector Integration', () => {
  it('should work with real GitHub API', async () => {
    if (!process.env.GITHUB_TOKEN_TEST) {
      return; // Skip without test credentials
    }

    const githubPlatform = new GitHubPlatform({
      token: process.env.GITHUB_TOKEN_TEST,
    });

    const selector = new IssueSelector(githubPlatform, config, logger, eventStore);

    const result = await selector.selectNextIssue({
      repository: { owner: 'tamma', name: 'test-repo', platform: 'github' },
      includeLabels: ['test-automation'],
      excludeLabels: ['skip-automation'],
      priorityStrategy: 'oldest',
    });

    expect(result).toBeDefined();
    expect(result?.number).toBeGreaterThan(0);
  });
});
```

### Monitoring and Observability

**Metrics to Track**:

- Issue selection success rate
- Time spent in polling state
- Number of issues processed per hour
- Filter effectiveness (issues filtered vs. total)
- Assignment success rate

**Logging Strategy**:

```typescript
// Structured logging examples
logger.info('Issue selected successfully', {
  issueNumber: issue.number,
  title: issue.title,
  labels: issue.labels,
  repository: `${config.repository.owner}/${config.repository.name}`,
  selectionTime: Date.now() - startTime,
  strategy: config.priorityStrategy,
});

logger.warn('No matching issues found', {
  repository: `${config.repository.owner}/${config.repository.name}`,
  totalIssues: issues.length,
  filteredIssues: filteredIssues.length,
  pollingInterval: config.polling.intervalSeconds,
});
```

### Configuration Examples

**Development Environment**:

```yaml
issue_selection:
  repository:
    owner: 'my-org'
    name: 'my-repo'
    platform: 'github'

  filtering:
    include_labels: ['ready-for-dev', 'automated']
    exclude_labels: ['blocked', 'manual-only']
    priority_strategy: 'oldest'

  bot:
    user_id: 'my-bot'
    auto_assign: true

  polling:
    enabled: true
    interval_seconds: 60 # More frequent for development
```

**Production Environment**:

```yaml
issue_selection:
  repository:
    owner: 'tamma'
    name: 'tamma'
    platform: 'github'

  filtering:
    include_labels: ['bug', 'enhancement']
    exclude_labels: ['wontfix', 'duplicate', 'security-review']
    priority_strategy: 'complexity' # More sophisticated in production

  bot:
    user_id: 'tamma-prod-bot'
    auto_assign: true

  polling:
    enabled: true
    interval_seconds: 300 # 5 minutes
    idle_timeout_minutes: 60
```

---

## Implementation Notes

**Key Considerations**:

1. **Rate Limit Handling**: Git platforms have strict rate limits. Implement proper backoff and respect retry-after headers.

2. **Label Strategy**: Design label taxonomy that works across different teams and projects while maintaining automation compatibility.

3. **Priority Algorithms**: Start with simple age-based prioritization, evolve to complexity scoring based on issue size, labels, and historical data.

4. **Polling Efficiency**: Use webhooks where available to reduce polling frequency, fall back to polling for platforms without webhook support.

5. **Error Recovery**: Implement circuit breaker pattern for Git platform API failures to prevent cascading failures.

6. **Audit Trail**: Every selection action must be logged to the event store for complete traceability.

**Performance Targets**:

- Issue selection: < 2 seconds for typical repositories (< 1000 open issues)
- Polling overhead: < 1% CPU usage during idle periods
- Memory usage: < 50MB for issue caching and filtering

**Security Considerations**:

- Validate repository access permissions before processing
- Sanitize issue content to prevent injection attacks
- Use read-only API tokens where possible
- Implement proper token rotation and secret management

### References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md) - Search spikes, bugs, findings, decisions
