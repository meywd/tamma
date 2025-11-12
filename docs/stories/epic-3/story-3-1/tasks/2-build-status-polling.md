# Task 2: Build Status Polling System

**Story**: 3.1 - Build Automation Gate Implementation  
**Phase**: Core MVP  
**Priority**: High  
**Estimated Time**: 2-3 days

## 🎯 Objective

Implement system to poll build status every 15 seconds until completion, capturing build progress and results.

## ✅ Acceptance Criteria

- [ ] System polls build status every 15 seconds
- [ ] Support for multiple CI/CD platforms (GitHub Actions, GitLab CI, Jenkins)
- [ ] Detect build completion (success, failure, cancelled)
- [ ] Capture build logs and error messages
- [ ] Handle polling timeouts and errors gracefully
- [ ] Stop polling when build completes
- [ ] Real-time status updates via events

## 🔧 Technical Implementation

### Core Interfaces

```typescript
interface IBuildStatusPoller {
  startPolling(buildId: string, platform: GitPlatform): Promise<BuildStatus>;
  stopPolling(buildId: string): Promise<void>;
  isPolling(buildId: string): boolean;
  getPollingStatus(buildId: string): PollingStatus;
}

interface BuildStatus {
  buildId: string;
  status: BuildState;
  startTime: Date;
  endTime?: Date;
  duration?: number;
  conclusion?: 'success' | 'failure' | 'cancelled' | 'neutral';
  logs?: BuildLog[];
  artifacts?: BuildArtifact[];
  error?: string;
}

enum BuildState {
  QUEUED = 'queued',
  IN_PROGRESS = 'in_progress',
  COMPLETED = 'completed',
  FAILED = 'failed',
  CANCELLED = 'cancelled',
  TIMEOUT = 'timeout',
}

interface PollingStatus {
  buildId: string;
  isPolling: boolean;
  pollCount: number;
  lastPollTime: Date;
  nextPollTime: Date;
  error?: string;
}
```

### Polling Implementation

```typescript
class BuildStatusPoller implements IBuildStatusPoller {
  private pollingJobs: Map<string, PollingJob> = new Map();
  private readonly POLL_INTERVAL = 15000; // 15 seconds
  private readonly MAX_POLL_TIME = 3600000; // 1 hour max

  constructor(
    private platformAdapters: Map<GitPlatform, IPlatformAdapter>,
    private eventStore: EventStore,
    private logger: Logger
  ) {}

  async startPolling(buildId: string, platform: GitPlatform): Promise<BuildStatus> {
    const adapter = this.platformAdapters.get(platform);
    if (!adapter) {
      throw new Error(`No adapter available for platform: ${platform}`);
    }

    const pollingJob: PollingJob = {
      buildId,
      platform,
      adapter,
      startTime: Date.now(),
      pollCount: 0,
      isPolling: true,
      timeout: setTimeout(() => this.handleTimeout(buildId), this.MAX_POLL_TIME),
    };

    this.pollingJobs.set(buildId, pollingJob);

    // Emit polling started event
    await this.eventStore.append({
      type: 'BUILD.POLLING_STARTED',
      tags: { buildId, platform },
      data: { startTime: new Date().toISOString() },
    });

    return await this.pollLoop(pollingJob);
  }

  private async pollLoop(job: PollingJob): Promise<BuildStatus> {
    while (job.isPolling) {
      try {
        job.pollCount++;
        job.lastPollTime = new Date();

        // Get current build status
        const status = await job.adapter.getBuildStatus(job.buildId);

        // Emit status update event
        await this.eventStore.append({
          type: 'BUILD.STATUS_UPDATED',
          tags: {
            buildId: job.buildId,
            platform: job.platform,
            status: status.status,
            pollCount: job.pollCount.toString(),
          },
          data: {
            status: status.status,
            duration: status.duration,
            timestamp: new Date().toISOString(),
          },
        });

        // Check if build is complete
        if (this.isBuildComplete(status.status)) {
          await this.completePolling(job, status);
          return status;
        }

        // Wait before next poll
        await this.delay(this.POLL_INTERVAL);
      } catch (error) {
        this.logger.error('Polling error', {
          buildId: job.buildId,
          error: error.message,
          pollCount: job.pollCount,
        });

        // Handle polling errors
        if (job.pollCount >= 10) {
          // Max 10 consecutive errors
          await this.handlePollingError(job, error);
          break;
        }

        await this.delay(this.POLL_INTERVAL * 2); // Backoff on error
      }
    }

    throw new Error(`Polling failed for build ${job.buildId}`);
  }

  private async completePolling(job: PollingJob, finalStatus: BuildStatus): Promise<void> {
    job.isPolling = false;
    clearTimeout(job.timeout);
    this.pollingJobs.delete(job.buildId);

    // Emit completion event
    await this.eventStore.append({
      type: 'BUILD.POLLING_COMPLETED',
      tags: {
        buildId: job.buildId,
        platform: job.platform,
        finalStatus: finalStatus.status,
        conclusion: finalStatus.conclusion,
      },
      data: {
        duration: finalStatus.duration,
        totalPolls: job.pollCount,
        completedAt: new Date().toISOString(),
      },
    });
  }

  private isBuildComplete(status: BuildState): boolean {
    return [
      BuildState.COMPLETED,
      BuildState.FAILED,
      BuildState.CANCELLED,
      BuildState.TIMEOUT,
    ].includes(status);
  }
}
```

### Platform Adapters

#### GitHub Actions Adapter

```typescript
class GitHubActionsAdapter implements IPlatformAdapter {
  constructor(private githubClient: GitHubClient) {}

  async getBuildStatus(buildId: string): Promise<BuildStatus> {
    const [owner, repo, runId] = buildId.split(':');

    const run = await this.githubClient.actions.getWorkflowRun({
      owner,
      repo,
      run_id: parseInt(runId),
    });

    const logs = await this.getBuildLogs(owner, repo, parseInt(runId));

    return {
      buildId,
      status: this.mapGitHubStatus(run.data.status),
      startTime: new Date(run.data.created_at),
      endTime: run.data.updated_at ? new Date(run.data.updated_at) : undefined,
      duration: run.data.run_duration_ms,
      conclusion: run.data.conclusion as any,
      logs,
      artifacts: await this.getArtifacts(owner, repo, parseInt(runId)),
    };
  }

  private mapGitHubStatus(status: string): BuildState {
    switch (status) {
      case 'queued':
        return BuildState.QUEUED;
      case 'in_progress':
        return BuildState.IN_PROGRESS;
      case 'completed':
        return BuildState.COMPLETED;
      default:
        return BuildState.FAILED;
    }
  }

  private async getBuildLogs(owner: string, repo: string, runId: number): Promise<BuildLog[]> {
    try {
      const logs = await this.githubClient.actions.downloadWorkflowRunLogs({
        owner,
        repo,
        run_id: runId,
      });

      return this.parseLogs(logs.data as string);
    } catch (error) {
      // Logs might not be available yet
      return [];
    }
  }
}
```

#### GitLab CI Adapter

```typescript
class GitLabCIAdapter implements IPlatformAdapter {
  constructor(private gitlabClient: GitLabClient) {}

  async getBuildStatus(buildId: string): Promise<BuildStatus> {
    const [projectId, pipelineId] = buildId.split(':');

    const pipeline = await this.gitlabClient.pipeline.show(projectId, parseInt(pipelineId));
    const jobs = await this.gitlabCI.jobs.all(projectId, { pipelineId: parseInt(pipelineId) });

    const logs = await this.getJobLogs(projectId, jobs.data);

    return {
      buildId,
      status: this.mapGitLabStatus(pipeline.data.status),
      startTime: new Date(pipeline.data.created_at),
      endTime: pipeline.data.finished_at ? new Date(pipeline.data.finished_at) : undefined,
      duration: pipeline.data.duration,
      conclusion: this.mapGitLabConclusion(pipeline.data.status),
      logs,
      artifacts: await this.getArtifacts(projectId, parseInt(pipelineId)),
    };
  }

  private mapGitLabStatus(status: string): BuildState {
    switch (status) {
      case 'created':
      case 'waiting_for_resource':
      case 'preparing':
      case 'pending':
      case 'running':
        return BuildState.IN_PROGRESS;
      case 'success':
        return BuildState.COMPLETED;
      case 'failed':
      case 'canceled':
      case 'skipped':
      case 'manual':
        return BuildState.FAILED;
      default:
        return BuildState.FAILED;
    }
  }
}
```

### Polling Manager

```typescript
class PollingManager {
  private poller: BuildStatusPoller;
  private activePolls: Map<string, Promise<BuildStatus>> = new Map();

  constructor(
    platformAdapters: Map<GitPlatform, IPlatformAdapter>,
    eventStore: EventStore,
    logger: Logger
  ) {
    this.poller = new BuildStatusPoller(platformAdapters, eventStore, logger);
  }

  async startPolling(buildId: string, platform: GitPlatform): Promise<BuildStatus> {
    if (this.activePolls.has(buildId)) {
      throw new Error(`Already polling build: ${buildId}`);
    }

    const pollPromise = this.poller.startPolling(buildId, platform);
    this.activePolls.set(buildId, pollPromise);

    try {
      const result = await pollPromise;
      this.activePolls.delete(buildId);
      return result;
    } catch (error) {
      this.activePolls.delete(buildId);
      throw error;
    }
  }

  async stopPolling(buildId: string): Promise<void> {
    await this.poller.stopPolling(buildId);

    const pollPromise = this.activePolls.get(buildId);
    if (pollPromise) {
      this.activePolls.delete(buildId);
      // Note: We can't actually cancel the promise, but we can stop the polling
    }
  }

  getPollingStatus(buildId: string): PollingStatus | null {
    return this.poller.getPollingStatus(buildId);
  }

  getActivePolls(): string[] {
    return Array.from(this.activePolls.keys());
  }
}
```

## 🧪 Testing Strategy

### Unit Tests

```typescript
describe('BuildStatusPoller', () => {
  let poller: BuildStatusPoller;
  let mockAdapter: jest.Mocked<IPlatformAdapter>;
  let mockEventStore: jest.Mocked<EventStore>;

  beforeEach(() => {
    mockAdapter = createMockPlatformAdapter();
    mockEventStore = createMockEventStore();
    poller = new BuildStatusPoller(new Map([['github', mockAdapter]]), mockEventStore, mockLogger);
  });

  it('should poll build status every 15 seconds', async () => {
    const buildId = 'test-build';
    const platform = 'github';

    // Mock in-progress then success
    mockAdapter.getBuildStatus
      .mockResolvedValueOnce({ status: BuildState.IN_PROGRESS })
      .mockResolvedValueOnce({ status: BuildState.COMPLETED });

    const result = await poller.startPolling(buildId, platform);

    expect(result.status).toBe(BuildState.COMPLETED);
    expect(mockAdapter.getBuildStatus).toHaveBeenCalledTimes(2);
    expect(mockEventStore.append).toHaveBeenCalledWith(
      expect.objectContaining({
        type: 'BUILD.POLLING_STARTED',
        tags: { buildId, platform },
      })
    );
  });

  it('should handle polling timeout', async () => {
    const buildId = 'timeout-build';
    const platform = 'github';

    // Mock continuous in-progress
    mockAdapter.getBuildStatus.mockResolvedValue({
      status: BuildState.IN_PROGRESS,
    });

    const pollPromise = poller.startPolling(buildId, platform);

    // Fast-forward time
    jest.advanceTimersByTime(3600000); // 1 hour

    await expect(pollPromise).rejects.toThrow('timeout');
  });
});
```

### Integration Tests

```typescript
describe('PollingManager Integration', () => {
  it('should poll real GitHub Actions build', async () => {
    const manager = new PollingManager(
      new Map([['github', new GitHubActionsAdapter(realGitHubClient)]]),
      realEventStore,
      realLogger
    );

    const buildId = 'owner:repo:12345';
    const result = await manager.startPolling(buildId, 'github');

    expect(result.status).toBeOneOf([
      BuildState.COMPLETED,
      BuildState.FAILED,
      BuildState.CANCELLED,
    ]);
  });
});
```

## 📊 Monitoring & Metrics

### Key Metrics

- Polling frequency and accuracy
- Build completion detection latency
- Polling error rates
- Platform-specific performance

### Events to Emit

```typescript
// Polling lifecycle events
BUILD.POLLING_STARTED;
BUILD.STATUS_UPDATED;
BUILD.POLLING_COMPLETED;
BUILD.POLLING_TIMEOUT;
BUILD.POLLING_ERROR;

// Status-specific events
BUILD.QUEUED;
BUILD.IN_PROGRESS;
BUILD.COMPLETED;
BUILD.FAILED;
BUILD.CANCELLED;
```

## 🔧 Configuration

### Environment Variables

```bash
# Polling configuration
BUILD_POLL_INTERVAL=15000
BUILD_MAX_POLL_TIME=3600000
BUILD_MAX_CONSECUTIVE_ERRORS=10

# Platform timeouts
GITHUB_API_TIMEOUT=30000
GITLAB_API_TIMEOUT=30000
JENKINS_API_TIMEOUT=30000
```

### Configuration File

```yaml
polling:
  interval: 15000 # 15 seconds
  max_poll_time: 3600000 # 1 hour
  max_consecutive_errors: 10
  backoff_multiplier: 2

platforms:
  github:
    timeout: 30000
    retry_attempts: 3

  gitlab:
    timeout: 30000
    retry_attempts: 3

  jenkins:
    timeout: 30000
    retry_attempts: 3
```

## 🚨 Error Handling

### Common Error Scenarios

1. **API rate limiting**
   - Implement exponential backoff
   - Respect platform rate limits

2. **Network connectivity issues**
   - Retry with increasing delays
   - Fail after max consecutive errors

3. **Build not found**
   - Immediate failure
   - Clear error message

4. **Authentication failures**
   - Immediate escalation
   - Token refresh attempt

### Recovery Strategies

- Automatic retry for transient errors
- Platform-specific error handling
- Graceful degradation for partial failures

## 📝 Implementation Checklist

- [ ] Define core interfaces and enums
- [ ] Implement BuildStatusPoller class
- [ ] Create platform adapters (GitHub, GitLab, Jenkins)
- [ ] Implement PollingManager
- [ ] Add comprehensive event logging
- [ ] Write unit tests for all components
- [ ] Write integration tests with real platforms
- [ ] Add monitoring and metrics
- [ ] Create configuration management
- [ ] Document error handling procedures
- [ ] Add performance optimizations

---

**Dependencies**: Task 1 (CI/CD Build Trigger)  
**Blocked By**: None  
**Blocks**: Task 3 (Build Failure Analysis)
