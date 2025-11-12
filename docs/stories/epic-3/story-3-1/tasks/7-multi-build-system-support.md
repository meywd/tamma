# Task 7: Multi-Build System Support

## Objective

Implement support for multiple build systems (GitHub Actions, GitLab CI, Jenkins, CircleCI, Azure DevOps) with unified interface and system-specific adapters.

## Acceptance Criteria

- [ ] Support for GitHub Actions workflows
- [ ] Support for GitLab CI/CD pipelines
- [ ] Support for Jenkins jobs
- [ ] Support for CircleCI configurations
- [ ] Support for Azure DevOps pipelines
- [ ] Unified build system interface
- [ ] Automatic build system detection
- [ ] System-specific configuration adapters
- [ ] Build system capability detection
- [ ] Cross-system status normalization

## Technical Implementation

### Core Interfaces

```typescript
// Build system types
export enum BuildSystemType {
  GITHUB_ACTIONS = 'github-actions',
  GITLAB_CI = 'gitlab-ci',
  JENKINS = 'jenkins',
  CIRCLECI = 'circleci',
  AZURE_DEVOPS = 'azure-devops',
}

// Build system capabilities
export interface BuildSystemCapabilities {
  supportedTriggers: string[];
  supportedArtifacts: string[];
  maxConcurrentBuilds: number;
  supportedLanguages: string[];
  customEnvironmentSupport: boolean;
  cachingSupport: boolean;
  parallelExecutionSupport: boolean;
}

// Build system adapter interface
export interface BuildSystemAdapter {
  readonly type: BuildSystemType;
  readonly capabilities: BuildSystemCapabilities;

  detect(repositoryPath: string): Promise<boolean>;
  initialize(config: BuildSystemConfig): Promise<void>;
  triggerBuild(params: BuildTriggerParams): Promise<BuildTriggerResult>;
  getBuildStatus(buildId: string): Promise<BuildStatus>;
  cancelBuild(buildId: string): Promise<void>;
  getBuildLogs(buildId: string): Promise<BuildLogEntry[]>;
  getBuildArtifacts(buildId: string): Promise<BuildArtifact[]>;
}

// Build system configuration
export interface BuildSystemConfig {
  type: BuildSystemType;
  repositoryUrl: string;
  credentials: BuildSystemCredentials;
  webhookSecret?: string;
  apiEndpoint?: string;
  customSettings?: Record<string, any>;
}

// Build system credentials
export interface BuildSystemCredentials {
  apiKey?: string;
  accessToken?: string;
  username?: string;
  password?: string;
  webhookUrl?: string;
}
```

### GitHub Actions Implementation

```typescript
export class GitHubActionsAdapter implements BuildSystemAdapter {
  readonly type = BuildSystemType.GITHUB_ACTIONS;
  readonly capabilities: BuildSystemCapabilities = {
    supportedTriggers: ['push', 'pull_request', 'schedule', 'workflow_dispatch'],
    supportedArtifacts: ['logs', 'coverage', 'build', 'test-results'],
    maxConcurrentBuilds: 20,
    supportedLanguages: ['javascript', 'python', 'java', 'go', 'rust', 'c++'],
    customEnvironmentSupport: true,
    cachingSupport: true,
    parallelExecutionSupport: true,
  };

  private octokit: Octokit;
  private config: BuildSystemConfig;

  async detect(repositoryPath: string): Promise<boolean> {
    const workflowsPath = path.join(repositoryPath, '.github', 'workflows');
    try {
      const workflows = await fs.readdir(workflowsPath);
      return workflows.some((file) => file.endsWith('.yml') || file.endsWith('.yaml'));
    } catch {
      return false;
    }
  }

  async initialize(config: BuildSystemConfig): Promise<void> {
    this.config = config;
    this.octokit = new Octokit({
      auth: config.credentials.accessToken,
    });
  }

  async triggerBuild(params: BuildTriggerParams): Promise<BuildTriggerResult> {
    const [owner, repo] = this.extractRepoInfo();

    try {
      if (params.eventType === 'workflow_dispatch') {
        const response = await this.octokit.rest.actions.createWorkflowDispatch({
          owner,
          repo,
          workflow_id: params.workflowId,
          ref: params.branch || 'main',
          inputs: params.inputs,
        });

        return {
          buildId: response.data.id.toString(),
          buildUrl: response.data.url,
          status: 'queued',
          triggeredAt: new Date().toISOString(),
        };
      } else {
        // For push/PR triggers, we create a commit or PR
        const commitResponse = await this.createCommit(params);
        return {
          buildId: commitResponse.sha,
          buildUrl: `https://github.com/${owner}/${repo}/commit/${commitResponse.sha}`,
          status: 'pending',
          triggeredAt: new Date().toISOString(),
        };
      }
    } catch (error) {
      throw new BuildSystemError(`Failed to trigger GitHub Actions build: ${error.message}`);
    }
  }

  async getBuildStatus(buildId: string): Promise<BuildStatus> {
    const [owner, repo] = this.extractRepoInfo();

    try {
      // Check workflow runs for this commit
      const runsResponse = await this.octokit.rest.actions.listWorkflowRunsForRepo({
        owner,
        repo,
        head_sha: buildId,
      });

      const run = runsResponse.data.workflow_runs[0];
      if (!run) {
        return { status: 'not_found', message: 'No workflow run found' };
      }

      return {
        buildId: run.id.toString(),
        status: this.mapGitHubStatus(run.status),
        conclusion: run.conclusion,
        startedAt: run.created_at,
        completedAt: run.updated_at,
        duration: run.updated_at
          ? new Date(run.updated_at).getTime() - new Date(run.created_at).getTime()
          : undefined,
        url: run.html_url,
        jobs: await this.getJobStatuses(owner, repo, run.id),
      };
    } catch (error) {
      throw new BuildSystemError(`Failed to get GitHub Actions build status: ${error.message}`);
    }
  }

  private async getJobStatuses(owner: string, repo: string, runId: number): Promise<BuildJob[]> {
    const jobsResponse = await this.octokit.rest.actions.listJobsForWorkflowRun({
      owner,
      repo,
      run_id: runId,
    });

    return jobsResponse.data.jobs.map((job) => ({
      id: job.id.toString(),
      name: job.name,
      status: this.mapGitHubStatus(job.status),
      conclusion: job.conclusion,
      startedAt: job.started_at,
      completedAt: job.completed_at,
      steps:
        job.steps?.map((step) => ({
          name: step.name,
          status: this.mapGitHubStatus(step.status),
          conclusion: step.conclusion,
          number: step.number,
          startedAt: step.started_at,
          completedAt: step.completed_at,
        })) || [],
    }));
  }

  private mapGitHubStatus(status: string): BuildStatusType {
    switch (status) {
      case 'queued':
        return 'queued';
      case 'in_progress':
        return 'running';
      case 'completed':
        return 'completed';
      default:
        return 'unknown';
    }
  }
}
```

### GitLab CI Implementation

```typescript
export class GitLabCIAdapter implements BuildSystemAdapter {
  readonly type = BuildSystemType.GITLAB_CI;
  readonly capabilities: BuildSystemCapabilities = {
    supportedTriggers: ['push', 'merge_request', 'schedule', 'web'],
    supportedArtifacts: ['logs', 'coverage', 'build', 'test-results', 'reports'],
    maxConcurrentBuilds: 50,
    supportedLanguages: ['javascript', 'python', 'java', 'go', 'rust', 'c++', 'php', 'ruby'],
    customEnvironmentSupport: true,
    cachingSupport: true,
    parallelExecutionSupport: true,
  };

  private gitlab: Gitlab;
  private config: BuildSystemConfig;

  async detect(repositoryPath: string): Promise<boolean> {
    const gitlabCiPath = path.join(repositoryPath, '.gitlab-ci.yml');
    try {
      await fs.access(gitlabCiPath);
      return true;
    } catch {
      return false;
    }
  }

  async initialize(config: BuildSystemConfig): Promise<void> {
    this.config = config;
    this.gitlab = new Gitlab({
      host: config.apiEndpoint || 'https://gitlab.com',
      token: config.credentials.accessToken,
    });
  }

  async triggerBuild(params: BuildTriggerParams): Promise<BuildTriggerResult> {
    const projectId = await this.getProjectId();

    try {
      const pipeline = await this.gitlab.Pipelines.create(projectId, {
        ref: params.branch || 'main',
        variables: params.inputs
          ? Object.entries(params.inputs).map(([key, value]) => ({
              key,
              value: String(value),
            }))
          : undefined,
      });

      return {
        buildId: pipeline.id.toString(),
        buildUrl: pipeline.web_url,
        status: 'pending',
        triggeredAt: pipeline.created_at,
      };
    } catch (error) {
      throw new BuildSystemError(`Failed to trigger GitLab CI pipeline: ${error.message}`);
    }
  }

  async getBuildStatus(buildId: string): Promise<BuildStatus> {
    const projectId = await this.getProjectId();

    try {
      const pipeline = await this.gitlab.Pipelines.show(projectId, parseInt(buildId));
      const jobs = await this.gitlab.Pipelines.allJobs(projectId, parseInt(buildId));

      return {
        buildId: pipeline.id.toString(),
        status: this.mapGitLabStatus(pipeline.status),
        conclusion:
          pipeline.status === 'success'
            ? 'success'
            : pipeline.status === 'failed'
              ? 'failure'
              : pipeline.status === 'canceled'
                ? 'cancelled'
                : undefined,
        startedAt: pipeline.created_at,
        completedAt: pipeline.finished_at,
        duration: pipeline.duration ? pipeline.duration * 1000 : undefined,
        url: pipeline.web_url,
        jobs: jobs.map((job) => ({
          id: job.id.toString(),
          name: job.name,
          status: this.mapGitLabStatus(job.status),
          conclusion:
            job.status === 'success'
              ? 'success'
              : job.status === 'failed'
                ? 'failure'
                : job.status === 'canceled'
                  ? 'cancelled'
                  : undefined,
          startedAt: job.started_at,
          completedAt: job.finished_at,
        })),
      };
    } catch (error) {
      throw new BuildSystemError(`Failed to get GitLab CI pipeline status: ${error.message}`);
    }
  }

  private mapGitLabStatus(status: string): BuildStatusType {
    switch (status) {
      case 'pending':
        return 'queued';
      case 'running':
        return 'running';
      case 'success':
      case 'failed':
      case 'canceled':
      case 'skipped':
        return 'completed';
      default:
        return 'unknown';
    }
  }
}
```

### Jenkins Implementation

```typescript
export class JenkinsAdapter implements BuildSystemAdapter {
  readonly type = BuildSystemType.JENKINS;
  readonly capabilities: BuildSystemCapabilities = {
    supportedTriggers: ['scm', 'timer', 'manual', 'upstream'],
    supportedArtifacts: ['logs', 'build', 'test-results', 'archive'],
    maxConcurrentBuilds: 100,
    supportedLanguages: [
      'javascript',
      'python',
      'java',
      'go',
      'rust',
      'c++',
      'php',
      'ruby',
      '.net',
    ],
    customEnvironmentSupport: true,
    cachingSupport: false,
    parallelExecutionSupport: true,
  };

  private jenkins: JenkinsAPI;
  private config: BuildSystemConfig;

  async detect(repositoryPath: string): Promise<boolean> {
    // Check for Jenkinsfile or Jenkins configuration
    const jenkinsfilePath = path.join(repositoryPath, 'Jenkinsfile');
    try {
      await fs.access(jenkinsfilePath);
      return true;
    } catch {
      return false;
    }
  }

  async initialize(config: BuildSystemConfig): Promise<void> {
    this.config = config;
    this.jenkins = new JenkinsAPI({
      baseUrl: config.apiEndpoint,
      username: config.credentials.username,
      password: config.credentials.password || config.credentials.apiKey,
    });
  }

  async triggerBuild(params: BuildTriggerParams): Promise<BuildTriggerResult> {
    const jobName = params.jobName || 'main';

    try {
      const queueItem = await this.jenkins.job.build({
        name: jobName,
        parameters: params.inputs
          ? Object.entries(params.inputs).map(([name, value]) => ({
              name,
              value: String(value),
            }))
          : undefined,
      });

      // Wait a moment and get the actual build number
      await new Promise((resolve) => setTimeout(resolve, 2000));
      const queueItemDetails = await this.jenkins.queue.item(queueItem.id);

      if (queueItemDetails.executable) {
        return {
          buildId: queueItemDetails.executable.number.toString(),
          buildUrl: queueItemDetails.executable.url,
          status: 'pending',
          triggeredAt: new Date().toISOString(),
        };
      } else {
        throw new Error('Build queued but not yet executable');
      }
    } catch (error) {
      throw new BuildSystemError(`Failed to trigger Jenkins build: ${error.message}`);
    }
  }

  async getBuildStatus(buildId: string): Promise<BuildStatus> {
    const jobName = 'main'; // This should be configurable

    try {
      const buildInfo = await this.jenkins.build.get(jobName, parseInt(buildId));

      return {
        buildId: buildInfo.id.toString(),
        status: this.mapJenkinsStatus(
          buildInfo.result || buildInfo.building ? 'BUILDING' : 'NOT_BUILDING'
        ),
        conclusion: buildInfo.result?.toLowerCase() as any,
        startedAt: new Date(buildInfo.timestamp).toISOString(),
        completedAt: buildInfo.duration
          ? new Date(buildInfo.timestamp + buildInfo.duration).toISOString()
          : undefined,
        duration: buildInfo.duration,
        url: buildInfo.url,
        jobs: [], // Jenkins jobs are more complex, could be implemented separately
      };
    } catch (error) {
      throw new BuildSystemError(`Failed to get Jenkins build status: ${error.message}`);
    }
  }

  private mapJenkinsStatus(status: string): BuildStatusType {
    switch (status) {
      case 'NOT_BUILT':
      case 'QUEUED':
        return 'queued';
      case 'BUILDING':
        return 'running';
      case 'SUCCESS':
      case 'FAILURE':
      case 'ABORTED':
      case 'UNSTABLE':
        return 'completed';
      default:
        return 'unknown';
    }
  }
}
```

### Build System Manager

```typescript
export class BuildSystemManager {
  private adapters: Map<BuildSystemType, BuildSystemAdapter> = new Map();
  private activeAdapter: BuildSystemAdapter | null = null;

  constructor() {
    this.registerDefaultAdapters();
  }

  private registerDefaultAdapters(): void {
    this.adapters.set(BuildSystemType.GITHUB_ACTIONS, new GitHubActionsAdapter());
    this.adapters.set(BuildSystemType.GITLAB_CI, new GitLabCIAdapter());
    this.adapters.set(BuildSystemType.JENKINS, new JenkinsAdapter());
    // Register CircleCI and Azure DevOps adapters similarly
  }

  async detectBuildSystem(repositoryPath: string): Promise<BuildSystemType | null> {
    for (const [type, adapter] of this.adapters) {
      try {
        if (await adapter.detect(repositoryPath)) {
          return type;
        }
      } catch (error) {
        console.warn(`Failed to detect ${type}: ${error.message}`);
      }
    }
    return null;
  }

  async initializeBuildSystem(config: BuildSystemConfig): Promise<void> {
    const adapter = this.adapters.get(config.type);
    if (!adapter) {
      throw new BuildSystemError(`Unsupported build system: ${config.type}`);
    }

    await adapter.initialize(config);
    this.activeAdapter = adapter;
  }

  async triggerBuild(params: BuildTriggerParams): Promise<BuildTriggerResult> {
    if (!this.activeAdapter) {
      throw new BuildSystemError('No build system initialized');
    }

    return this.activeAdapter.triggerBuild(params);
  }

  async getBuildStatus(buildId: string): Promise<BuildStatus> {
    if (!this.activeAdapter) {
      throw new BuildSystemError('No build system initialized');
    }

    return this.activeAdapter.getBuildStatus(buildId);
  }

  getCapabilities(): BuildSystemCapabilities | null {
    return this.activeAdapter?.capabilities || null;
  }

  getSupportedSystems(): BuildSystemType[] {
    return Array.from(this.adapters.keys());
  }
}
```

## Testing Strategy

### Unit Tests

```typescript
describe('BuildSystemManager', () => {
  let manager: BuildSystemManager;
  let mockAdapter: jest.Mocked<BuildSystemAdapter>;

  beforeEach(() => {
    manager = new BuildSystemManager();
    mockAdapter = {
      type: BuildSystemType.GITHUB_ACTIONS,
      capabilities: {} as BuildSystemCapabilities,
      detect: jest.fn(),
      initialize: jest.fn(),
      triggerBuild: jest.fn(),
      getBuildStatus: jest.fn(),
      cancelBuild: jest.fn(),
      getBuildLogs: jest.fn(),
      getBuildArtifacts: jest.fn(),
    };
  });

  describe('detectBuildSystem', () => {
    it('should detect GitHub Actions', async () => {
      mockAdapter.detect.mockResolvedValue(true);
      manager['adapters'].set(BuildSystemType.GITHUB_ACTIONS, mockAdapter);

      const detected = await manager.detectBuildSystem('/path/to/repo');

      expect(detected).toBe(BuildSystemType.GITHUB_ACTIONS);
      expect(mockAdapter.detect).toHaveBeenCalledWith('/path/to/repo');
    });

    it('should return null when no system detected', async () => {
      mockAdapter.detect.mockResolvedValue(false);
      manager['adapters'].set(BuildSystemType.GITHUB_ACTIONS, mockAdapter);

      const detected = await manager.detectBuildSystem('/path/to/repo');

      expect(detected).toBeNull();
    });
  });

  describe('initializeBuildSystem', () => {
    it('should initialize the correct adapter', async () => {
      const config: BuildSystemConfig = {
        type: BuildSystemType.GITHUB_ACTIONS,
        repositoryUrl: 'https://github.com/user/repo',
        credentials: { accessToken: 'token' },
      };

      manager['adapters'].set(BuildSystemType.GITHUB_ACTIONS, mockAdapter);

      await manager.initializeBuildSystem(config);

      expect(mockAdapter.initialize).toHaveBeenCalledWith(config);
      expect(manager['activeAdapter']).toBe(mockAdapter);
    });

    it('should throw error for unsupported system', async () => {
      const config: BuildSystemConfig = {
        type: 'unsupported' as BuildSystemType,
        repositoryUrl: 'https://example.com/repo',
        credentials: {},
      };

      await expect(manager.initializeBuildSystem(config)).rejects.toThrow(BuildSystemError);
    });
  });
});
```

### Integration Tests

```typescript
describe('Build System Integration', () => {
  describe('GitHub Actions', () => {
    it('should trigger and monitor real GitHub Actions workflow', async () => {
      const adapter = new GitHubActionsAdapter();
      const config: BuildSystemConfig = {
        type: BuildSystemType.GITHUB_ACTIONS,
        repositoryUrl: process.env.GITHUB_TEST_REPO!,
        credentials: { accessToken: process.env.GITHUB_TOKEN! },
      };

      await adapter.initialize(config);

      const result = await adapter.triggerBuild({
        eventType: 'workflow_dispatch',
        workflowId: 'ci.yml',
        branch: 'test-branch',
      });

      expect(result.buildId).toBeDefined();
      expect(result.status).toBe('queued');

      // Poll for completion
      let status: BuildStatus;
      do {
        await new Promise((resolve) => setTimeout(resolve, 5000));
        status = await adapter.getBuildStatus(result.buildId);
      } while (status.status === 'running' || status.status === 'queued');

      expect(['completed', 'unknown']).toContain(status.status);
    });
  });
});
```

## Monitoring and Metrics

### Build System Metrics

```typescript
export interface BuildSystemMetrics {
  buildSystemType: BuildSystemType;
  totalBuilds: number;
  successfulBuilds: number;
  failedBuilds: number;
  averageBuildTime: number;
  buildsPerDay: number;
  errorRate: number;
  lastBuildTime: number;
  systemCapabilities: BuildSystemCapabilities;
}

export class BuildSystemMonitor {
  private metricsCollector: MetricsCollector;

  async recordBuildTrigger(systemType: BuildSystemType, buildId: string): Promise<void> {
    await this.metricsCollector.increment('build_triggers_total', {
      system_type: systemType,
    });
  }

  async recordBuildCompletion(
    systemType: BuildSystemType,
    buildId: string,
    status: BuildStatusType,
    duration?: number
  ): Promise<void> {
    await this.metricsCollector.increment('build_completions_total', {
      system_type: systemType,
      status,
    });

    if (duration) {
      await this.metricsCollector.record('build_duration', duration, {
        system_type: systemType,
        status,
      });
    }
  }

  async getSystemMetrics(systemType: BuildSystemType): Promise<BuildSystemMetrics> {
    // Implementation for collecting system-specific metrics
    return {} as BuildSystemMetrics;
  }
}
```

## Configuration Management

### Build System Configuration

```typescript
export interface MultiBuildSystemConfig {
  defaultSystem: BuildSystemType;
  systems: Record<BuildSystemType, BuildSystemConfig>;
  fallbackOrder: BuildSystemType[];
  autoDetect: boolean;
  systemPreferences: {
    [key in BuildSystemType]?: {
      priority: number;
      maxConcurrentBuilds: number;
      timeout: number;
    };
  };
}

export const defaultMultiSystemConfig: MultiBuildSystemConfig = {
  defaultSystem: BuildSystemType.GITHUB_ACTIONS,
  systems: {},
  fallbackOrder: [
    BuildSystemType.GITHUB_ACTIONS,
    BuildSystemType.GITLAB_CI,
    BuildSystemType.JENKINS,
    BuildSystemType.CIRCLECI,
    BuildSystemType.AZURE_DEVOPS,
  ],
  autoDetect: true,
  systemPreferences: {
    [BuildSystemType.GITHUB_ACTIONS]: {
      priority: 1,
      maxConcurrentBuilds: 20,
      timeout: 3600000, // 1 hour
    },
    [BuildSystemType.GITLAB_CI]: {
      priority: 2,
      maxConcurrentBuilds: 50,
      timeout: 3600000,
    },
  },
};
```

## Error Handling

### Build System Errors

```typescript
export class BuildSystemError extends Error {
  constructor(
    message: string,
    public readonly systemType?: BuildSystemType,
    public readonly buildId?: string,
    public readonly originalError?: Error
  ) {
    super(message);
    this.name = 'BuildSystemError';
  }
}

export class BuildSystemNotSupportedError extends BuildSystemError {
  constructor(systemType: BuildSystemType) {
    super(`Build system ${systemType} is not supported`, systemType);
    this.name = 'BuildSystemNotSupportedError';
  }
}

export class BuildSystemAuthenticationError extends BuildSystemError {
  constructor(systemType: BuildSystemType, message: string) {
    super(`Authentication failed for ${systemType}: ${message}`, systemType);
    this.name = 'BuildSystemAuthenticationError';
  }
}

export class BuildSystemQuotaExceededError extends BuildSystemError {
  constructor(systemType: BuildSystemType, resetTime?: Date) {
    super(`Quota exceeded for ${systemType}`, systemType);
    this.name = 'BuildSystemQuotaExceededError';
  }
}
```

## Implementation Checklist

- [ ] Implement GitHub Actions adapter
- [ ] Implement GitLab CI adapter
- [ ] Implement Jenkins adapter
- [ ] Implement CircleCI adapter
- [ ] Implement Azure DevOps adapter
- [ ] Create BuildSystemManager
- [ ] Add automatic build system detection
- [ ] Implement unified status mapping
- [ ] Add capability detection
- [ ] Create configuration management
- [ ] Add comprehensive error handling
- [ ] Implement monitoring and metrics
- [ ] Add unit tests for all adapters
- [ ] Add integration tests
- [ ] Create documentation for each system
- [ ] Add fallback system support
- [ ] Implement system-specific optimizations
- [ ] Add webhook support for real-time updates
- [ ] Create performance benchmarks
