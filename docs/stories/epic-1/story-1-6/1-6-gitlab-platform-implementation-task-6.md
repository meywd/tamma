# Story 1.6 Task 6: Implement Integration Testing

## Task Overview

Implement comprehensive integration testing for the GitLab platform implementation, validating end-to-end workflows, real API interactions, and system integration points. This task will create integration tests that verify the complete functionality of the GitLab integration with real GitLab instances and test projects.

## Acceptance Criteria

### 6.1 Real API Integration Tests

- [ ] Implement integration tests with real GitLab instance (GitLab.com or self-hosted)
- [ ] Test all authentication methods with real credentials
- [ ] Test all API endpoints with real data and responses
- [ ] Test rate limiting and quota management with real API limits
- [ ] Test error handling with real API error responses

### 6.2 End-to-End Workflow Tests

- [ ] Implement complete autonomous development workflow tests
- [ ] Test issue-to-MR lifecycle with real GitLab projects
- [ ] Test CI/CD pipeline monitoring and decision making
- [ ] Test merge request creation, review, and merging workflows
- [ ] Test multi-step autonomous workflows with real data

### 6.3 Performance and Load Tests

- [ ] Implement performance tests for API operations under load
- [ ] Test concurrent request handling and rate limiting
- [ ] Test memory usage and resource management under sustained load
- [ ] Test webhook processing and event handling performance
- [ ] Test authentication token refresh and credential management performance

### 6.4 Security and Permission Tests

- [ ] Test authentication and authorization with different permission levels
- [ ] Test secure credential storage and retrieval
- [ ] Test API access control and permission validation
- [ ] Test webhook signature verification and security
- [ ] Test data sanitization and input validation

### 6.5 Compatibility and Version Tests

- [ ] Test compatibility with different GitLab versions (15.x, 16.x, 17.x)
- [ ] Test API version compatibility and deprecation handling
- [ ] Test backward compatibility with older GitLab instances
- [ ] Test feature detection and graceful degradation
- [ ] Test configuration validation for different GitLab setups

### 6.6 Test Infrastructure and Automation

- [ ] Implement automated test environment setup and teardown
- [ ] Implement test data management and cleanup utilities
- [ ] Implement test result reporting and analytics
- [ ] Implement CI/CD pipeline integration for automated testing
- [ ] Implement test monitoring and alerting

## Implementation Details

### 6.1 Integration Test Environment Setup

```typescript
// test/integration/setup/integration-test-env.ts
import { execSync } from 'child_process';
import { DockerComposeEnvironment } from 'testcontainers';
import path from 'path';

export class IntegrationTestEnvironment {
  private static instance: IntegrationTestEnvironment;
  private dockerEnv: DockerComposeEnvironment | null = null;
  private gitlabContainer: any = null;
  private testProjects: Map<string, number> = new Map();

  private constructor() {}

  static getInstance(): IntegrationTestEnvironment {
    if (!IntegrationTestEnvironment.instance) {
      IntegrationTestEnvironment.instance = new IntegrationTestEnvironment();
    }
    return IntegrationTestEnvironment.instance;
  }

  async setup(): Promise<void> {
    console.log('Setting up integration test environment...');

    // Check if using external GitLab instance
    if (process.env.GITLAB_USE_EXTERNAL === 'true') {
      await this.setupExternalGitLab();
    } else {
      await this.setupDockerGitLab();
    }

    // Setup test projects and data
    await this.setupTestProjects();
    await this.setupTestUsers();
    await this.setupTestRepositories();

    console.log('Integration test environment ready');
  }

  async teardown(): Promise<void> {
    console.log('Tearing down integration test environment...');

    if (this.dockerEnv) {
      await this.dockerEnv.down();
      this.dockerEnv = null;
    }

    // Cleanup test data if using external GitLab
    if (process.env.GITLAB_USE_EXTERNAL === 'true') {
      await this.cleanupExternalGitLab();
    }

    console.log('Integration test environment torn down');
  }

  private async setupDockerGitLab(): Promise<void> {
    console.log('Starting GitLab Docker container...');

    const composeFilePath = path.join(__dirname, 'docker-compose.yml');
    const composeFile = 'docker-compose.yml';

    this.dockerEnv = await new DockerComposeEnvironment(composeFilePath, composeFile)
      .withWaitStrategy('gitlab', Wait.forLogMessage('GitLab is ready'))
      .withStartupTimeout(300000) // 5 minutes
      .up();

    this.gitlabContainer = this.dockerEnv.getContainer('gitlab');

    // Wait for GitLab to be fully ready
    await this.waitForGitLabReady();
  }

  private async setupExternalGitLab(): Promise<void> {
    console.log('Using external GitLab instance...');

    // Validate external GitLab configuration
    const gitlabUrl = process.env.GITLAB_EXTERNAL_URL;
    const gitlabToken = process.env.GITLAB_EXTERNAL_TOKEN;

    if (!gitlabUrl || !gitlabToken) {
      throw new Error(
        'GITLAB_EXTERNAL_URL and GITLAB_EXTERNAL_TOKEN must be set when using external GitLab'
      );
    }

    // Test connectivity
    const response = await fetch(`${gitlabUrl}/api/v4/version`, {
      headers: {
        Authorization: `Bearer ${gitlabToken}`,
        'User-Agent': 'Tamma-Integration-Test/1.0.0',
      },
    });

    if (!response.ok) {
      throw new Error(`Failed to connect to external GitLab: ${response.statusText}`);
    }

    console.log('External GitLab connection validated');
  }

  private async waitForGitLabReady(): Promise<void> {
    const maxAttempts = 60;
    const attemptDelay = 5000; // 5 seconds

    for (let attempt = 1; attempt <= maxAttempts; attempt++) {
      try {
        const gitlabUrl = this.getGitLabUrl();
        const response = await fetch(`${gitlabUrl}/api/v4/version`, {
          timeout: 10000,
        });

        if (response.ok) {
          console.log('GitLab is ready');
          return;
        }
      } catch (error) {
        console.log(`Attempt ${attempt}/${maxAttempts}: GitLab not ready yet`);
      }

      if (attempt === maxAttempts) {
        throw new Error('GitLab failed to start within timeout period');
      }

      await new Promise((resolve) => setTimeout(resolve, attemptDelay));
    }
  }

  private async setupTestProjects(): Promise<void> {
    console.log('Setting up test projects...');

    const testProjects = [
      { name: 'tamma-integration-test', description: 'Integration test project for Tamma' },
      { name: 'tamma-pipeline-test', description: 'Pipeline testing project' },
      { name: 'tamma-mr-test', description: 'Merge request testing project' },
    ];

    for (const projectConfig of testProjects) {
      const projectId = await this.createTestProject(projectConfig);
      this.testProjects.set(projectConfig.name, projectId);
    }

    console.log(`Created ${this.testProjects.size} test projects`);
  }

  private async createTestProject(config: { name: string; description: string }): Promise<number> {
    const gitlabUrl = this.getGitLabUrl();
    const token = this.getGitLabToken();

    const response = await fetch(`${gitlabUrl}/api/v4/projects`, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${token}`,
        'Content-Type': 'application/json',
        'User-Agent': 'Tamma-Integration-Test/1.0.0',
      },
      body: JSON.stringify({
        name: config.name,
        description: config.description,
        visibility: 'private',
        initialize_with_readme: true,
        default_branch: 'main',
      }),
    });

    if (!response.ok) {
      throw new Error(`Failed to create test project ${config.name}: ${response.statusText}`);
    }

    const project = await response.json();
    return project.id;
  }

  private async setupTestUsers(): Promise<void> {
    console.log('Setting up test users...');
    // Implementation for creating test users with specific permissions
  }

  private async setupTestRepositories(): Promise<void> {
    console.log('Setting up test repositories...');

    for (const [projectName, projectId] of this.testProjects) {
      await this.setupRepositoryContent(projectId, projectName);
    }
  }

  private async setupRepositoryContent(projectId: number, projectName: string): Promise<void> {
    const gitlabUrl = this.getGitLabUrl();
    const token = this.getGitLabToken();

    // Create basic repository structure
    const files = [
      {
        path: 'README.md',
        content: `# ${projectName}\n\nIntegration test project for Tamma platform.`,
      },
      {
        path: '.gitlab-ci.yml',
        content: this.getBasicCIConfig(),
      },
      {
        path: 'package.json',
        content: this.getBasicPackageJson(),
      },
    ];

    for (const file of files) {
      await this.createFile(projectId, file.path, file.content, 'Initial setup');
    }
  }

  private async createFile(
    projectId: number,
    path: string,
    content: string,
    message: string
  ): Promise<void> {
    const gitlabUrl = this.getGitLabUrl();
    const token = this.getGitLabToken();

    const response = await fetch(
      `${gitlabUrl}/api/v4/projects/${projectId}/repository/files/${encodeURIComponent(path)}`,
      {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${token}`,
          'Content-Type': 'application/json',
          'User-Agent': 'Tamma-Integration-Test/1.0.0',
        },
        body: JSON.stringify({
          branch: 'main',
          content,
          commit_message: message,
        }),
      }
    );

    if (!response.ok) {
      throw new Error(`Failed to create file ${path}: ${response.statusText}`);
    }
  }

  private getBasicCIConfig(): string {
    return `
stages:
  - build
  - test
  - deploy

build:
  stage: build
  script:
    - echo "Building..."
    - npm install
    - npm run build
  artifacts:
    paths:
      - dist/

test:
  stage: test
  script:
    - echo "Testing..."
    - npm test
  coverage: '/Coverage: \d+\.\d+%/'

deploy:
  stage: deploy
  script:
    - echo "Deploying..."
  only:
    - main
`;
  }

  private getBasicPackageJson(): string {
    return JSON.stringify(
      {
        name: 'tamma-test-project',
        version: '1.0.0',
        description: 'Test project for Tamma integration',
        scripts: {
          build: "echo 'Build completed'",
          test: "echo 'Tests passed' && echo 'Coverage: 85.5%'",
        },
        devDependencies: {
          jest: '^29.0.0',
        },
      },
      null,
      2
    );
  }

  private async cleanupExternalGitLab(): Promise<void> {
    console.log('Cleaning up test projects from external GitLab...');

    const gitlabUrl = this.getGitLabUrl();
    const token = this.getGitLabToken();

    for (const [projectName, projectId] of this.testProjects) {
      try {
        const response = await fetch(`${gitlabUrl}/api/v4/projects/${projectId}`, {
          method: 'DELETE',
          headers: {
            Authorization: `Bearer ${token}`,
            'User-Agent': 'Tamma-Integration-Test/1.0.0',
          },
        });

        if (response.ok) {
          console.log(`Deleted test project: ${projectName}`);
        } else {
          console.warn(`Failed to delete test project ${projectName}: ${response.statusText}`);
        }
      } catch (error) {
        console.warn(`Error deleting test project ${projectName}:`, error);
      }
    }
  }

  getGitLabUrl(): string {
    return process.env.GITLAB_USE_EXTERNAL === 'true'
      ? process.env.GITLAB_EXTERNAL_URL!
      : 'http://localhost:8080';
  }

  getGitLabToken(): string {
    return process.env.GITLAB_USE_EXTERNAL === 'true'
      ? process.env.GITLAB_EXTERNAL_TOKEN!
      : 'test-token'; // Root token for Docker GitLab
  }

  getTestProjectId(name: string): number {
    const projectId = this.testProjects.get(name);
    if (!projectId) {
      throw new Error(`Test project ${name} not found`);
    }
    return projectId;
  }

  getTestProjects(): Map<string, number> {
    return new Map(this.testProjects);
  }
}
```

### 6.2 End-to-End Workflow Integration Tests

```typescript
// test/integration/workflows/autonomous-development.test.ts
import { GitLabPlatform } from '../../../src/platforms/gitlab/gitlab-platform';
import { GitLabCICDManager } from '../../../src/platforms/gitlab/gitlab-cicd-manager';
import { GitLabMergeRequestManager } from '../../../src/platforms/gitlab/gitlab-mr-manager';
import { IntegrationTestEnvironment } from '../setup/integration-test-env';

describe('Autonomous Development Workflow Integration Tests', () => {
  let testEnv: IntegrationTestEnvironment;
  let gitlabPlatform: GitLabPlatform;
  let cicdManager: GitLabCICDManager;
  let mrManager: GitLabMergeRequestManager;

  beforeAll(async () => {
    testEnv = IntegrationTestEnvironment.getInstance();
    await testEnv.setup();

    // Initialize platform components
    gitlabPlatform = new GitLabPlatform({
      instanceUrl: testEnv.getGitLabUrl(),
      auth: {
        method: 'pat',
        token: testEnv.getGitLabToken(),
      },
    });

    await gitlabPlatform.initialize();

    cicdManager = new GitLabCICDManager(
      testEnv.getGitLabUrl(),
      gitlabPlatform.getHttpClient(),
      gitlabPlatform.getWebhookManager()
    );

    mrManager = new GitLabMergeRequestManager(
      testEnv.getGitLabUrl(),
      gitlabPlatform.getHttpClient(),
      gitlabPlatform.getWebhookManager()
    );
  }, 300000); // 5 minutes timeout

  afterAll(async () => {
    await testEnv.teardown();
  }, 300000);

  describe('Complete Issue-to-Merge Workflow', () => {
    const issueId = `TEST-${Date.now()}`;
    const featureBranch = `feature/${issueId.toLowerCase().replace(/[^a-z0-9]/g, '-')}`;

    it(
      'should complete full autonomous development workflow',
      async () => {
        const projectId = testEnv.getTestProjectId('tamma-integration-test');

        // Step 1: Create feature branch
        console.log('Creating feature branch...');
        await gitlabPlatform.createBranch(projectId, featureBranch, 'main');

        // Step 2: Make changes to repository
        console.log('Making changes to repository...');
        await gitlabPlatform.createFile(
          projectId,
          'feature.js',
          `
// New feature implementation
export function newFeature() {
  return 'This is a new feature';
}

export function anotherFeature() {
  return 'Another feature implementation';
}
      `,
          `Implement feature for ${issueId}`
        );

        // Step 3: Create merge request
        console.log('Creating merge request...');
        const mergeRequest = await mrManager.createMergeRequest(projectId, {
          sourceBranch: featureBranch,
          targetBranch: 'main',
          title: `Implement feature for ${issueId}`,
          description: `
## Summary
This merge request implements the new feature requested in ${issueId}.

## Changes
- Added new feature implementation
- Updated documentation
- Added unit tests

## Testing
- [ ] Unit tests pass
- [ ] Integration tests pass
- [ ] Manual testing completed

## Checklist
- [ ] Code follows project style guidelines
- [ ] Self-review completed
- [ ] Documentation updated
        `,
          labels: ['feature', 'automation'],
          template: 'feature-template',
          templateVariables: {
            ISSUE_ID: issueId,
            FEATURE_NAME: 'New Feature',
          },
        });

        expect(mergeRequest.id).toBeDefined();
        expect(mergeRequest.state).toBe('opened');
        expect(mergeRequest.source_branch).toBe(featureBranch);
        expect(mergeRequest.target_branch).toBe('main');

        // Step 4: Trigger CI/CD pipeline
        console.log('Triggering CI/CD pipeline...');
        const pipeline = await cicdManager.triggerPipeline(projectId, featureBranch, [
          {
            key: 'ISSUE_ID',
            value: issueId,
            variable_type: 'env_var',
            protected: false,
            masked: false,
            raw: false,
          },
        ]);

        expect(pipeline.id).toBeDefined();
        expect(pipeline.ref).toBe(featureBranch);

        // Step 5: Monitor pipeline completion
        console.log('Monitoring pipeline completion...');
        const completedPipeline = await cicdManager.waitForPipelineCompletion(
          projectId,
          pipeline.id,
          10 * 60 * 1000 // 10 minutes
        );

        expect(['success', 'failed']).toContain(completedPipeline.status);

        // Step 6: Add AI review comments
        console.log('Adding AI review comments...');
        const aiReview = {
          summary:
            'Overall the changes look good. The code follows best practices and includes proper documentation.',
          fileReviews: [
            {
              filePath: 'feature.js',
              comments: [
                {
                  line: 3,
                  message: 'Consider adding JSDoc comments for better documentation',
                },
                {
                  line: 8,
                  message: 'Good implementation of the feature',
                },
              ],
            },
          ],
        };

        await mrManager.addMergeRequestNote(
          projectId,
          mergeRequest.iid,
          `
## 🤖 AI Review Summary

${aiReview.summary}

### File-specific feedback:
- **feature.js**: Consider adding JSDoc comments for better documentation
      `
        );

        // Step 7: Approve merge request (simulating reviewer approval)
        console.log('Approving merge request...');
        await mrManager.approveMergeRequest(projectId, mergeRequest.iid);

        // Step 8: Check merge readiness
        console.log('Checking merge readiness...');
        const readiness = await mrManager.evaluateMergeRequestReadiness(
          projectId,
          mergeRequest.iid
        );

        console.log('Merge readiness:', readiness);

        // Step 9: Merge if ready (only if pipeline succeeded)
        if (completedPipeline.status === 'success' && readiness.readyToMerge) {
          console.log('Merging merge request...');
          const mergedMR = await mrManager.mergeMergeRequest(projectId, mergeRequest.iid, {
            squash: true,
            shouldRemoveSourceBranch: true,
            commitMessage: `Merge feature for ${issueId}\n\nAutomated merge by Tamma platform`,
          });

          expect(mergedMR.state).toBe('merged');
          expect(mergedMR.merged_at).toBeDefined();
        } else {
          console.log('Pipeline failed or MR not ready, skipping merge');
          // Clean up - close MR
          await mrManager.updateMergeRequest(projectId, mergeRequest.iid, {
            stateId: 'close',
          });
        }

        console.log('Autonomous development workflow completed successfully');
      },
      15 * 60 * 1000
    ); // 15 minutes timeout

    it(
      'should handle workflow failures gracefully',
      async () => {
        const projectId = testEnv.getTestProjectId('tamma-integration-test');
        const failingBranch = `feature/failing-${Date.now()}`;

        // Create branch with failing code
        await gitlabPlatform.createBranch(projectId, failingBranch, 'main');

        await gitlabPlatform.createFile(
          projectId,
          'failing-test.js',
          `
// This will cause test failures
export function failingFunction() {
  throw new Error('Intentional failure for testing');
}
      `,
          'Add failing test'
        );

        // Create MR
        const mergeRequest = await mrManager.createMergeRequest(projectId, {
          sourceBranch: failingBranch,
          targetBranch: 'main',
          title: 'Test failure handling',
          description: 'This MR should fail CI/CD',
        });

        // Trigger pipeline
        const pipeline = await cicdManager.triggerPipeline(projectId, failingBranch);

        // Wait for pipeline to fail
        const completedPipeline = await cicdManager.waitForPipelineCompletion(
          projectId,
          pipeline.id,
          5 * 60 * 1000 // 5 minutes
        );

        expect(completedPipeline.status).toBe('failed');

        // Check that MR is not ready to merge
        const readiness = await mrManager.evaluateMergeRequestReadiness(
          projectId,
          mergeRequest.iid
        );

        expect(readiness.readyToMerge).toBe(false);
        expect(readiness.blockers).toContain('Pipeline is not passing');

        // Clean up
        await mrManager.updateMergeRequest(projectId, mergeRequest.iid, {
          stateId: 'close',
        });
      },
      10 * 60 * 1000
    ); // 10 minutes timeout
  });

  describe('Multi-Project Workflow', () => {
    it(
      'should handle workflows across multiple projects',
      async () => {
        const mainProjectId = testEnv.getTestProjectId('tamma-integration-test');
        const pipelineProjectId = testEnv.getTestProjectId('tamma-pipeline-test');

        // Create changes in main project
        const featureBranch = `feature/multi-project-${Date.now()}`;
        await gitlabPlatform.createBranch(mainProjectId, featureBranch, 'main');

        await gitlabPlatform.createFile(
          mainProjectId,
          'shared-component.js',
          `
// Shared component for multiple projects
export class SharedComponent {
  constructor(name) {
    this.name = name;
  }
  
  render() {
    return \`Component: \${this.name}\`;
  }
}
      `,
          'Add shared component'
        );

        // Create MR in main project
        const mainMR = await mrManager.createMergeRequest(mainProjectId, {
          sourceBranch: featureBranch,
          targetBranch: 'main',
          title: 'Add shared component',
          description: 'Adds a shared component that can be used across projects',
        });

        // Trigger pipeline in main project
        const mainPipeline = await cicdManager.triggerPipeline(mainProjectId, featureBranch);

        // Create related changes in pipeline project
        const pipelineBranch = `feature/shared-component-${Date.now()}`;
        await gitlabPlatform.createBranch(pipelineProjectId, pipelineBranch, 'main');

        await gitlabPlatform.createFile(
          pipelineProjectId,
          'use-shared-component.js',
          `
import { SharedComponent } from '../shared-component';

const component = new SharedComponent('Pipeline Test');
console.log(component.render());
      `,
          'Use shared component in pipeline project'
        );

        // Create MR in pipeline project
        const pipelineMR = await mrManager.createMergeRequest(pipelineProjectId, {
          sourceBranch: pipelineBranch,
          targetBranch: 'main',
          title: 'Integrate shared component',
          description: 'Integrates the shared component from the main project',
        });

        // Trigger pipeline in pipeline project
        const pipelineProjectPipeline = await cicdManager.triggerPipeline(
          pipelineProjectId,
          pipelineBranch
        );

        // Wait for both pipelines
        const [mainCompleted, pipelineCompleted] = await Promise.all([
          cicdManager.waitForPipelineCompletion(mainProjectId, mainPipeline.id, 5 * 60 * 1000),
          cicdManager.waitForPipelineCompletion(
            pipelineProjectId,
            pipelineProjectPipeline.id,
            5 * 60 * 1000
          ),
        ]);

        expect(['success', 'failed']).toContain(mainCompleted.status);
        expect(['success', 'failed']).toContain(pipelineCompleted.status);

        // Clean up
        await Promise.all([
          mrManager.updateMergeRequest(mainProjectId, mainMR.iid, { stateId: 'close' }),
          mrManager.updateMergeRequest(pipelineProjectId, pipelineMR.iid, { stateId: 'close' }),
        ]);
      },
      15 * 60 * 1000
    ); // 15 minutes timeout
  });
});
```

### 6.3 Performance and Load Tests

```typescript
// test/integration/performance/load-tests.test.ts
import { GitLabPlatform } from '../../../src/platforms/gitlab/gitlab-platform';
import { IntegrationTestEnvironment } from '../setup/integration-test-env';
import { PerformanceTestUtils } from '../../../src/test/utils/performance-test';

describe('Performance and Load Tests', () => {
  let testEnv: IntegrationTestEnvironment;
  let gitlabPlatform: GitLabPlatform;

  beforeAll(async () => {
    testEnv = IntegrationTestEnvironment.getInstance();
    await testEnv.setup();

    gitlabPlatform = new GitLabPlatform({
      instanceUrl: testEnv.getGitLabUrl(),
      auth: {
        method: 'pat',
        token: testEnv.getGitLabToken(),
      },
    });

    await gitlabPlatform.initialize();
  }, 300000);

  afterAll(async () => {
    await testEnv.teardown();
  }, 300000);

  describe('API Performance Tests', () => {
    it('should handle concurrent project requests', async () => {
      const projectId = testEnv.getTestProjectId('tamma-integration-test');

      const { results, totalTime, errors } = await PerformanceTestUtils.runConcurrentTest(
        () => gitlabPlatform.getProject(projectId),
        20 // 20 concurrent requests
      );

      expect(errors).toHaveLength(0);
      expect(results).toHaveLength(20);
      expect(totalTime).toBeLessThan(10000); // Should complete within 10 seconds

      const averageTime = totalTime / 20;
      expect(averageTime).toBeLessThan(1000); // Average under 1 second per request
    });

    it('should handle concurrent branch operations', async () => {
      const projectId = testEnv.getTestProjectId('tamma-integration-test');

      const { results, totalTime, errors } = await PerformanceTestUtils.runConcurrentTest(
        async () => {
          const branchName = `perf-test-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;
          return await gitlabPlatform.createBranch(projectId, branchName, 'main');
        },
        10 // 10 concurrent branch creations
      );

      expect(errors).toHaveLength(0);
      expect(results).toHaveLength(10);
      expect(totalTime).toBeLessThan(30000); // Should complete within 30 seconds
    });

    it('should maintain performance under sustained load', async () => {
      const projectId = testEnv.getTestProjectId('tamma-integration-test');
      const duration = 60000; // 1 minute
      const requestInterval = 100; // Request every 100ms
      const startTime = Date.now();
      const results = [];
      let errors = 0;

      while (Date.now() - startTime < duration) {
        try {
          const start = Date.now();
          await gitlabPlatform.getProject(projectId);
          const requestTime = Date.now() - start;
          results.push(requestTime);
        } catch (error) {
          errors++;
        }

        await new Promise((resolve) => setTimeout(resolve, requestInterval));
      }

      const averageTime = results.reduce((sum, time) => sum + time, 0) / results.length;
      const p95Time = results.sort((a, b) => a - b)[Math.floor(results.length * 0.95)];

      expect(errors).toBeLessThan(results.length * 0.01); // Less than 1% error rate
      expect(averageTime).toBeLessThan(500); // Average under 500ms
      expect(p95Time).toBeLessThan(2000); // 95th percentile under 2 seconds
    });
  });

  describe('Memory Usage Tests', () => {
    it('should not leak memory during extended operation', async () => {
      const projectId = testEnv.getTestProjectId('tamma-integration-test');
      const iterations = 100;

      const { memoryBefore, memoryAfter, memoryDiff } =
        await PerformanceTestUtils.measureMemoryUsage(async () => {
          for (let i = 0; i < iterations; i++) {
            await gitlabPlatform.getProject(projectId);
            await gitlabPlatform.getBranches(projectId);

            // Force garbage collection if available
            if (global.gc) {
              global.gc();
            }
          }
        });

      const memoryIncreasePerIteration = memoryDiff / iterations;

      // Memory increase should be minimal (less than 1KB per iteration)
      expect(memoryIncreasePerIteration).toBeLessThan(1024);
      console.log(
        `Memory usage: ${memoryBefore / 1024 / 1024}MB -> ${memoryAfter / 1024 / 1024}MB (${memoryDiff / 1024 / 1024}MB increase)`
      );
    });
  });

  describe('Rate Limiting Tests', () => {
    it('should handle rate limiting gracefully', async () => {
      const projectId = testEnv.getTestProjectId('tamma-integration-test');
      const requests = [];

      // Make rapid requests to trigger rate limiting
      for (let i = 0; i < 100; i++) {
        requests.push(
          gitlabPlatform.getProject(projectId).catch((error) => {
            if (error.status === 429) {
              return { rateLimited: true };
            }
            throw error;
          })
        );
      }

      const results = await Promise.allSettled(requests);
      const rateLimited = results.filter((r) => r.status === 'fulfilled' && r.value.rateLimited);

      // Should handle rate limiting without crashing
      expect(rateLimited.length).toBeGreaterThan(0);

      // Should eventually recover from rate limiting
      const successful = results.filter((r) => r.status === 'fulfilled' && !r.value.rateLimited);
      expect(successful.length).toBeGreaterThan(0);
    });
  });
});
```

### 6.4 Security and Permission Tests

```typescript
// test/integration/security/permission-tests.test.ts
import { GitLabPlatform } from '../../../src/platforms/gitlab/gitlab-platform';
import { IntegrationTestEnvironment } from '../setup/integration-test-env';

describe('Security and Permission Tests', () => {
  let testEnv: IntegrationTestEnvironment;
  let gitlabPlatform: GitLabPlatform;
  let limitedGitlabPlatform: GitLabPlatform;

  beforeAll(async () => {
    testEnv = IntegrationTestEnvironment.getInstance();
    await testEnv.setup();

    // Admin platform with full permissions
    gitlabPlatform = new GitLabPlatform({
      instanceUrl: testEnv.getGitLabUrl(),
      auth: {
        method: 'pat',
        token: testEnv.getGitLabToken(),
      },
    });

    await gitlabPlatform.initialize();

    // Limited platform with restricted permissions (if available)
    if (process.env.GITLAB_LIMITED_TOKEN) {
      limitedGitlabPlatform = new GitLabPlatform({
        instanceUrl: testEnv.getGitLabUrl(),
        auth: {
          method: 'pat',
          token: process.env.GITLAB_LIMITED_TOKEN,
        },
      });

      await limitedGitlabPlatform.initialize();
    }
  }, 300000);

  afterAll(async () => {
    await testEnv.teardown();
  }, 300000);

  describe('Authentication Security', () => {
    it('should reject invalid authentication', async () => {
      const invalidPlatform = new GitLabPlatform({
        instanceUrl: testEnv.getGitLabUrl(),
        auth: {
          method: 'pat',
          token: 'invalid-token',
        },
      });

      await expect(invalidPlatform.initialize()).rejects.toThrow();
    });

    it('should handle token expiration gracefully', async () => {
      // This test would require a token that's about to expire
      // For now, we'll simulate the behavior
      const platform = gitlabPlatform as any;

      // Mock expired token
      platform.authManager.currentToken = {
        accessToken: 'expired-token',
        tokenType: 'Bearer',
        createdAt: new Date(Date.now() - 2 * 60 * 60 * 1000), // 2 hours ago
        expiresIn: 3600, // 1 hour expiration
      };

      // Should attempt token refresh
      const projectId = testEnv.getTestProjectId('tamma-integration-test');

      // This should trigger token refresh logic
      await expect(platform.getProject(projectId)).resolves.toBeDefined();
    });

    it('should store credentials securely', async () => {
      const platform = gitlabPlatform as any;
      const credentialManager = platform.authManager.credentialManager;

      const token = {
        accessToken: 'sensitive-token-data',
        tokenType: 'Bearer',
        createdAt: new Date(),
      };

      await credentialManager.storeToken(testEnv.getGitLabUrl(), token);

      // Verify stored data is encrypted
      const stored = await credentialManager['osCredentialManager'].retrieve(
        'tamma-gitlab',
        'gitlab_com'
      );

      expect(stored).not.toContain('sensitive-token-data');
      expect(stored).toMatch(/^[a-f0-9]+:/); // Should be encrypted format
    });
  });

  describe('Access Control', () => {
    it('should respect project permissions', async () => {
      if (!limitedGitlabPlatform) {
        console.warn('Skipping limited permission tests - no limited token provided');
        return;
      }

      const projectId = testEnv.getTestProjectId('tamma-integration-test');

      // Limited user should be able to read public data
      const project = await limitedGitlabPlatform.getProject(projectId);
      expect(project).toBeDefined();

      // But might not be able to write
      try {
        await limitedGitlabPlatform.createBranch(projectId, 'test-branch', 'main');
        // If this succeeds, the user has write permissions
      } catch (error) {
        // Expected for limited permissions
        expect([403, 401]).toContain(error.status);
      }
    });

    it('should validate input and prevent injection', async () => {
      const projectId = testEnv.getTestProjectId('tamma-integration-test');

      // Test malicious input in branch name
      const maliciousBranchName = '../../../etc/passwd';

      await expect(
        gitlabPlatform.createBranch(projectId, maliciousBranchName, 'main')
      ).rejects.toThrow();

      // Test malicious input in file content
      const maliciousContent = '<script>alert("xss")</script>';

      // Should handle content safely
      await expect(
        gitlabPlatform.createFile(projectId, 'test.js', maliciousContent, 'Test')
      ).resolves.toBeDefined();
    });
  });

  describe('Webhook Security', () => {
    it('should verify webhook signatures', async () => {
      const webhookManager = gitlabPlatform.getWebhookManager();

      // Mock webhook payload
      const payload = {
        object_kind: 'push',
        project: { id: 123 },
        commits: [],
      };

      const secret = 'webhook-secret';
      const signature = webhookManager.generateSignature(JSON.stringify(payload), secret);

      // Should verify valid signature
      expect(webhookManager.verifySignature(JSON.stringify(payload), signature, secret)).toBe(true);

      // Should reject invalid signature
      expect(
        webhookManager.verifySignature(JSON.stringify(payload), 'invalid-signature', secret)
      ).toBe(false);
    });
  });
});
```

### 6.5 Test Automation and CI/CD Integration

```yaml
# .github/workflows/integration-tests.yml
name: Integration Tests

on:
  push:
    branches: [main, develop]
  pull_request:
    branches: [main]
  schedule:
    - cron: '0 2 * * *' # Daily at 2 AM

jobs:
  integration-tests:
    runs-on: ubuntu-latest

    services:
      gitlab:
        image: gitlab/gitlab-ce:latest
        ports:
          - 8080:80
        env:
          GITLAB_OMNIBUS_CONFIG: |
            external_url 'http://localhost:8080'
            gitlab_rails['initial_root_password'] = 'password123'
        options: >-
          --health-cmd "curl -f http://localhost:8080/-/health || exit 1"
          --health-interval 30s
          --health-timeout 10s
          --health-retries 5

    steps:
      - uses: actions/checkout@v3

      - name: Setup Node.js
        uses: actions/setup-node@v3
        with:
          node-version: '18'
          cache: 'npm'

      - name: Install dependencies
        run: npm ci

      - name: Wait for GitLab
        run: |
          echo "Waiting for GitLab to be ready..."
          timeout 300 bash -c 'until curl -f http://localhost:8080/-/health; do sleep 10; done'

      - name: Setup GitLab access token
        run: |
          # Get root token from GitLab
          TOKEN=$(curl -X POST -H "Content-Type: application/json" \
            -d '{"grant_type": "password", "username": "root", "password": "password123"}' \
            http://localhost:8080/oauth/token | jq -r '.access_token')
          echo "GITLAB_EXTERNAL_URL=http://localhost:8080" >> $GITHUB_ENV
          echo "GITLAB_EXTERNAL_TOKEN=$TOKEN" >> $GITHUB_ENV
          echo "GITLAB_USE_EXTERNAL=true" >> $GITHUB_ENV

      - name: Run integration tests
        run: npm run test:integration
        env:
          GITLAB_EXTERNAL_URL: http://localhost:8080
          GITLAB_EXTERNAL_TOKEN: ${{ secrets.GITLAB_TOKEN }}
          GITLAB_USE_EXTERNAL: true

      - name: Upload test results
        uses: actions/upload-artifact@v3
        if: always()
        with:
          name: integration-test-results
          path: |
            coverage/
            test-results/
            logs/

      - name: Upload coverage to Codecov
        uses: codecov/codecov-action@v3
        if: always()
        with:
          file: ./coverage/lcov.info
          flags: integration-tests
          name: integration-coverage

  performance-tests:
    runs-on: ubuntu-latest
    needs: integration-tests

    steps:
      - uses: actions/checkout@v3

      - name: Setup Node.js
        uses: actions/setup-node@v3
        with:
          node-version: '18'
          cache: 'npm'

      - name: Install dependencies
        run: npm ci

      - name: Run performance tests
        run: npm run test:performance
        env:
          GITLAB_EXTERNAL_URL: ${{ secrets.GITLAB_URL }}
          GITLAB_EXTERNAL_TOKEN: ${{ secrets.GITLAB_TOKEN }}
          GITLAB_USE_EXTERNAL: true

      - name: Upload performance results
        uses: actions/upload-artifact@v3
        if: always()
        with:
          name: performance-test-results
          path: performance-results/
```

## Completion Checklist

- [ ] Implement integration test environment setup with Docker GitLab
- [ ] Implement external GitLab instance support
- [ ] Create comprehensive end-to-end workflow tests
- [ ] Implement performance and load testing utilities
- [ ] Add security and permission validation tests
- [ ] Implement compatibility tests for different GitLab versions
- [ ] Create automated test data management and cleanup
- [ ] Set up CI/CD pipeline integration for automated testing
- [ ] Add test result reporting and analytics
- [ ] Implement test monitoring and alerting
- [ ] Verify all integration scenarios work correctly
- [ ] Ensure test coverage and reliability targets are met

## Dependencies

- Task 1-5: All GitLab platform implementation and unit testing tasks
- Docker and Docker Compose for test environment
- Testcontainers for containerized testing
- Real GitLab instance or self-hosted GitLab for testing
- CI/CD pipeline integration (GitHub Actions, GitLab CI, etc.)
- Performance monitoring and reporting tools

## Estimated Time

**Test Environment Setup**: 3-4 days
**End-to-End Workflow Tests**: 4-5 days
**Performance Tests**: 2-3 days
**Security Tests**: 2-3 days
**Compatibility Tests**: 2-3 days
**CI/CD Integration**: 1-2 days
**Test Automation**: 2-3 days
**Total**: 16-23 days
