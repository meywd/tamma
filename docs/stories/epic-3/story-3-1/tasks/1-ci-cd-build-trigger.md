# Task 1: CI/CD Build Trigger Implementation

**Story**: 3.1 - Build Automation Gate Implementation  
**Phase**: Core MVP  
**Priority**: High  
**Estimated Time**: 2-3 days

## 🎯 Objective

Implement system to trigger builds via platform-specific CI/CD APIs (GitHub Actions, GitLab CI, etc.) after each commit.

## ✅ Acceptance Criteria

- [ ] System detects new commits in repository
- [ ] System triggers appropriate CI/CD workflow based on platform
- [ ] Support for GitHub Actions, GitLab CI, Jenkins, Azure DevOps
- [ ] Build trigger includes relevant context (commit hash, branch, PR info)
- [ ] Error handling for failed trigger attempts
- [ ] Logging of all trigger attempts with timestamps

## 🔧 Technical Implementation

### Core Interfaces

```typescript
interface IBuildTrigger {
  triggerBuild(context: BuildTriggerContext): Promise<BuildTriggerResult>;
  isSupported(platform: GitPlatform): boolean;
  getTriggerConfig(): BuildTriggerConfig;
}

interface BuildTriggerContext {
  projectId: string;
  repositoryUrl: string;
  commitHash: string;
  branch: string;
  pullRequestId?: string;
  filesChanged: string[];
  environment: BuildEnvironment;
}

interface BuildTriggerResult {
  success: boolean;
  buildId?: string;
  buildUrl?: string;
  triggeredAt: Date;
  error?: string;
}

interface BuildTriggerConfig {
  platform: GitPlatform;
  workflowFile?: string; // .github/workflows/build.yml
  pipelineId?: string; // GitLab CI
  jobName?: string; // Jenkins
  parameters: Record<string, any>;
}
```

### Platform Implementations

#### GitHub Actions Trigger

```typescript
class GitHubActionsTrigger implements IBuildTrigger {
  constructor(private githubClient: GitHubClient) {}

  async triggerBuild(context: BuildTriggerContext): Promise<BuildTriggerResult> {
    try {
      const workflowResponse = await this.githubClient.actions.createWorkflowDispatch({
        owner: context.owner,
        repo: context.repository,
        workflow_id: context.config.workflowFile || 'build.yml',
        ref: context.branch,
        inputs: {
          commit_hash: context.commitHash,
          pull_request_id: context.pullRequestId,
          files_changed: context.filesChanged.join(','),
        },
      });

      return {
        success: true,
        buildId: workflowResponse.data.id.toString(),
        buildUrl: `${context.repositoryUrl}/actions/runs/${workflowResponse.data.id}`,
        triggeredAt: new Date(),
      };
    } catch (error) {
      return {
        success: false,
        triggeredAt: new Date(),
        error: error.message,
      };
    }
  }

  isSupported(platform: GitPlatform): boolean {
    return platform === 'github';
  }
}
```

#### GitLab CI Trigger

```typescript
class GitLabCITrigger implements IBuildTrigger {
  constructor(private gitlabClient: GitLabClient) {}

  async triggerBuild(context: BuildTriggerContext): Promise<BuildTriggerResult> {
    try {
      const pipelineResponse = await this.gitlabClient.pipelineTrigger.create(context.projectId, {
        ref: context.branch,
        variables: {
          COMMIT_HASH: context.commitHash,
          PULL_REQUEST_ID: context.pullRequestId,
          FILES_CHANGED: context.filesChanged.join(','),
        },
      });

      return {
        success: true,
        buildId: pipelineResponse.data.id.toString(),
        buildUrl: `${context.repositoryUrl}/-/pipelines/${pipelineResponse.data.id}`,
        triggeredAt: new Date(),
      };
    } catch (error) {
      return {
        success: false,
        triggeredAt: new Date(),
        error: error.message,
      };
    }
  }

  isSupported(platform: GitPlatform): boolean {
    return platform === 'gitlab';
  }
}
```

### Build Trigger Manager

```typescript
class BuildTriggerManager {
  private triggers: Map<GitPlatform, IBuildTrigger> = new Map();

  constructor(triggers: IBuildTrigger[]) {
    triggers.forEach((trigger) => {
      // Register trigger for supported platforms
      // This would be configured based on available triggers
    });
  }

  async triggerBuild(context: BuildTriggerContext): Promise<BuildTriggerResult> {
    const platform = this.detectPlatform(context.repositoryUrl);
    const trigger = this.triggers.get(platform);

    if (!trigger) {
      return {
        success: false,
        triggeredAt: new Date(),
        error: `No trigger available for platform: ${platform}`,
      };
    }

    // Log trigger attempt
    await this.logTriggerAttempt(context, platform);

    const result = await trigger.triggerBuild(context);

    // Log trigger result
    await this.logTriggerResult(context, result);

    return result;
  }

  private detectPlatform(repositoryUrl: string): GitPlatform {
    if (repositoryUrl.includes('github.com')) return 'github';
    if (repositoryUrl.includes('gitlab.com')) return 'gitlab';
    // Add more platform detection logic
    throw new Error(`Unsupported platform: ${repositoryUrl}`);
  }
}
```

## 🧪 Testing Strategy

### Unit Tests

```typescript
describe('GitHubActionsTrigger', () => {
  it('should trigger GitHub Actions workflow successfully', async () => {
    const mockGithubClient = createMockGitHubClient();
    const trigger = new GitHubActionsTrigger(mockGithubClient);

    const context: BuildTriggerContext = {
      projectId: '123',
      repositoryUrl: 'https://github.com/user/repo',
      commitHash: 'abc123',
      branch: 'main',
      filesChanged: ['src/file.ts'],
      environment: { os: 'linux', arch: 'x64' },
    };

    const result = await trigger.triggerBuild(context);

    expect(result.success).toBe(true);
    expect(result.buildId).toBeDefined();
    expect(result.buildUrl).toContain('github.com');
  });

  it('should handle trigger failures gracefully', async () => {
    const mockGithubClient = createMockGitHubClient();
    mockGithubClient.actions.createWorkflowDispatch.mockRejectedValue(
      new Error('Workflow not found')
    );

    const trigger = new GitHubActionsTrigger(mockGithubClient);
    const context = createMockContext();

    const result = await trigger.triggerBuild(context);

    expect(result.success).toBe(false);
    expect(result.error).toContain('Workflow not found');
  });
});
```

### Integration Tests

```typescript
describe('BuildTriggerManager Integration', () => {
  it('should trigger build on GitHub repository', async () => {
    const manager = new BuildTriggerManager([new GitHubActionsTrigger(realGitHubClient)]);

    const context = createRealGitHubContext();

    const result = await manager.triggerBuild(context);

    expect(result.success).toBe(true);

    // Verify build actually started
    const buildStatus = await githubClient.actions.getWorkflowRun({
      owner: context.owner,
      repo: context.repository,
      run_id: parseInt(result.buildId),
    });

    expect(buildStatus.data.status).toBe('queued');
  });
});
```

## 📊 Monitoring & Metrics

### Key Metrics

- Build trigger success rate
- Trigger latency (time from commit to build start)
- Platform-specific trigger success rates
- Error frequency by platform

### Events to Emit

```typescript
// Build trigger events
BUILD.TRIGGER_ATTEMPTED;
BUILD.TRIGGER_SUCCESS;
BUILD.TRIGGER_FAILED;
BUILD.TRIGGER_TIMEOUT;

// Platform-specific events
GITHUB.ACTIONS_TRIGGERED;
GITLAB.CI_TRIGGERED;
JENKINS.BUILD_TRIGGERED;
```

## 🔧 Configuration

### Environment Variables

```bash
# GitHub
GITHUB_TOKEN=ghp_***
GITHUB_API_URL=https://api.github.com

# GitLab
GITLAB_TOKEN=glpat-***
GITLAB_API_URL=https://gitlab.com/api/v4

# Jenkins
JENKINS_URL=https://jenkins.example.com
JENKINS_USERNAME=***
JENKINS_API_TOKEN=***
```

### Configuration File

```yaml
build_triggers:
  github:
    enabled: true
    workflow_file: '.github/workflows/build.yml'
    timeout: 30000

  gitlab:
    enabled: true
    pipeline_variables:
      SKIP_TESTS: 'false'
      BUILD_TYPE: 'production'

  jenkins:
    enabled: false
    job_name: 'build-job'
    parameters:
      GIT_COMMIT: '${commitHash}'
      BRANCH: '${branch}'
```

## 🚨 Error Handling

### Common Error Scenarios

1. **Authentication failures**
   - Invalid/expired tokens
   - Insufficient permissions

2. **Configuration errors**
   - Missing workflow files
   - Invalid pipeline configurations

3. **Network issues**
   - API timeouts
   - Rate limiting

4. **Platform-specific errors**
   - Repository not found
   - Branch doesn't exist

### Recovery Strategies

- Retry with exponential backoff for transient errors
- Fallback to alternative trigger methods if available
- Immediate escalation for authentication/configuration errors

## 📝 Implementation Checklist

- [ ] Define core interfaces and types
- [ ] Implement GitHub Actions trigger
- [ ] Implement GitLab CI trigger
- [ ] Implement Jenkins trigger
- [ ] Create BuildTriggerManager
- [ ] Add comprehensive logging
- [ ] Write unit tests
- [ ] Write integration tests
- [ ] Add monitoring and metrics
- [ ] Create configuration management
- [ ] Document error handling procedures

---

**Dependencies**: Epic 1 (Git Platform Integration)  
**Blocked By**: None  
**Blocks**: Task 2 (Build Status Polling)
