# Story 1.6 Task 5: Implement Comprehensive Unit Testing

## Task Overview

Implement comprehensive unit testing for the GitLab platform implementation, ensuring all components are thoroughly tested with high code coverage, proper mocking, and validation of error handling scenarios. This task will create a robust test suite that validates the functionality, reliability, and performance of the GitLab integration.

## Acceptance Criteria

### 5.1 Core Platform Unit Tests

- [ ] Implement unit tests for GitLabPlatform class with 100% method coverage
- [ ] Test all authentication methods and error scenarios
- [ ] Test API client configuration and request handling
- [ ] Test rate limiting and retry logic implementation
- [ ] Test error handling and recovery mechanisms

### 5.2 Authentication Module Tests

- [ ] Implement unit tests for GitLabAuthManager with all authentication flows
- [ ] Test PAT authentication with valid/invalid tokens
- [ ] Test OAuth2 authentication flow including token refresh
- [ ] Test credential management and secure storage
- [ ] Test authentication failure scenarios and recovery

### 5.3 CI/CD Module Tests

- [ ] Implement unit tests for GitLabCICDManager with all pipeline operations
- [ ] Test pipeline creation, monitoring, and management
- [ ] Test job operations and log retrieval
- [ ] Test CI/CD configuration parsing and validation
- [ ] Test webhook event handling and processing

### 5.4 Merge Request Module Tests

- [ ] Implement unit tests for GitLabMergeRequestManager with all MR operations
- [ ] Test MR creation, updates, and lifecycle management
- [ ] Test approval workflows and rule validation
- [ ] Test merge operations and conflict handling
- [ ] Test discussion and comment management

### 5.5 Workflow Integration Tests

- [ ] Implement unit tests for workflow integration components
- [ ] Test autonomous workflow decision making
- [ ] Test event emission and processing
- [ ] Test AI review integration and comment generation
- [ ] Test readiness evaluation and auto-merge logic

### 5.6 Test Infrastructure and Utilities

- [ ] Implement comprehensive test utilities and mock factories
- [ ] Test data generators for GitLab API responses
- [ ] Test helpers for authentication and credential mocking
- [ ] Performance testing utilities for API operations
- [ ] Integration test helpers for end-to-end validation

## Implementation Details

### 5.1 Test Infrastructure Setup

```typescript
// test-setup.ts
import 'jest';
import { configure } from '@testing-library/react';
import { jest } from '@jest/globals';

// Configure testing library
configure({ testIdAttribute: 'data-testid' });

// Global test setup
beforeAll(() => {
  // Set test environment variables
  process.env.NODE_ENV = 'test';
  process.env.LOG_LEVEL = 'error';

  // Mock console methods to reduce noise in tests
  jest.spyOn(console, 'log').mockImplementation(() => {});
  jest.spyOn(console, 'warn').mockImplementation(() => {});
  jest.spyOn(console, 'error').mockImplementation(() => {});
});

afterAll(() => {
  // Restore console methods
  jest.restoreAllMocks();
});

// Global test utilities
global.createMockHttpClient = () => ({
  get: jest.fn(),
  post: jest.fn(),
  put: jest.fn(),
  delete: jest.fn(),
  patch: jest.fn(),
});

global.createMockEventStore = () => ({
  append: jest.fn(),
  getEvents: jest.fn(),
  getEventsByTag: jest.fn(),
  replay: jest.fn(),
});

global.createMockLogger = () => ({
  info: jest.fn(),
  warn: jest.fn(),
  error: jest.fn(),
  debug: jest.fn(),
});

global.createMockCredentialManager = () => ({
  store: jest.fn(),
  retrieve: jest.fn(),
  delete: jest.fn(),
});
```

### 5.2 Mock Data Factories

```typescript
// factories/gitlab-factory.ts
import {
  GitLabUser,
  GitLabProject,
  GitLabPipeline,
  GitLabJob,
  GitLabMergeRequest,
} from '../../../src/types/gitlab.types';

export class GitLabFactory {
  static createUser(overrides: Partial<GitLabUser> = {}): GitLabUser {
    return {
      id: Math.floor(Math.random() * 10000),
      username: `user_${Math.random().toString(36).substr(2, 9)}`,
      name: `Test User ${Math.random().toString(36).substr(2, 9)}`,
      email: `test${Math.random().toString(36).substr(2, 9)}@example.com`,
      state: 'active',
      avatar_url: `https://example.com/avatar/${Math.random().toString(36).substr(2, 9)}.png`,
      web_url: `https://gitlab.com/${Math.random().toString(36).substr(2, 9)}`,
      created_at: new Date().toISOString(),
      last_sign_in_at: new Date().toISOString(),
      ...overrides,
    };
  }

  static createProject(overrides: Partial<GitLabProject> = {}): GitLabProject {
    return {
      id: Math.floor(Math.random() * 10000),
      name: `Test Project ${Math.random().toString(36).substr(2, 9)}`,
      description: 'Test project description',
      path: `test-project-${Math.random().toString(36).substr(2, 9)}`,
      path_with_namespace: `test-group/test-project-${Math.random().toString(36).substr(2, 9)}`,
      created_at: new Date().toISOString(),
      default_branch: 'main',
      ssh_url_to_repo: `git@gitlab.com:test/project-${Math.random().toString(36).substr(2, 9)}.git`,
      http_url_to_repo: `https://gitlab.com/test/project-${Math.random().toString(36).substr(2, 9)}.git`,
      web_url: `https://gitlab.com/test/project-${Math.random().toString(36).substr(2, 9)}`,
      readme_url: `https://gitlab.com/test/project-${Math.random().toString(36).substr(2, 9)}/-/blob/main/README.md`,
      tag_list: ['test', 'automation'],
      topics: ['test', 'automation'],
      star_count: Math.floor(Math.random() * 100),
      forks_count: Math.floor(Math.random() * 50),
      last_activity_at: new Date().toISOString(),
      ...overrides,
    };
  }

  static createPipeline(overrides: Partial<GitLabPipeline> = {}): GitLabPipeline {
    return {
      id: Math.floor(Math.random() * 10000),
      iid: Math.floor(Math.random() * 1000),
      project_id: Math.floor(Math.random() * 1000),
      sha: Math.random().toString(36).substr(2, 40),
      ref: `feature-${Math.random().toString(36).substr(2, 9)}`,
      status: 'success',
      source: 'push',
      created_at: new Date().toISOString(),
      updated_at: new Date().toISOString(),
      web_url: `https://gitlab.com/test/project/-/pipelines/${Math.floor(Math.random() * 10000)}`,
      duration: Math.floor(Math.random() * 3600),
      queued_duration: Math.floor(Math.random() * 300),
      user: this.createUser(),
      variables: [],
      config: {
        stages: ['build', 'test', 'deploy'],
        jobs: {},
        variables: [],
        cache: {},
        services: [],
      },
      ...overrides,
    };
  }

  static createJob(overrides: Partial<GitLabJob> = {}): GitLabJob {
    return {
      id: Math.floor(Math.random() * 10000),
      name: `test-job-${Math.random().toString(36).substr(2, 9)}`,
      stage: 'test',
      status: 'success',
      created_at: new Date().toISOString(),
      started_at: new Date().toISOString(),
      finished_at: new Date().toISOString(),
      duration: Math.floor(Math.random() * 3600),
      user: this.createUser(),
      ref: `feature-${Math.random().toString(36).substr(2, 9)}`,
      commit: {
        id: Math.random().toString(36).substr(2, 40),
        short_id: Math.random().toString(36).substr(2, 8),
        title: 'Test commit',
        message: 'Test commit message',
        author_name: 'Test Author',
        author_email: 'test@example.com',
        created_at: new Date().toISOString(),
      },
      pipeline: this.createPipeline(),
      web_url: `https://gitlab.com/test/project/-/jobs/${Math.floor(Math.random() * 10000)}`,
      artifacts: [],
      retry_count: 0,
      ...overrides,
    };
  }

  static createMergeRequest(overrides: Partial<GitLabMergeRequest> = {}): GitLabMergeRequest {
    return {
      id: Math.floor(Math.random() * 10000),
      iid: Math.floor(Math.random() * 1000),
      project_id: Math.floor(Math.random() * 1000),
      title: `Test MR ${Math.random().toString(36).substr(2, 9)}`,
      description: 'Test merge request description',
      state: 'opened',
      created_at: new Date().toISOString(),
      updated_at: new Date().toISOString(),
      target_branch: 'main',
      source_branch: `feature-${Math.random().toString(36).substr(2, 9)}`,
      source_project_id: Math.floor(Math.random() * 1000),
      target_project_id: Math.floor(Math.random() * 1000),
      author: this.createUser(),
      assignees: [this.createUser()],
      reviewers: [this.createUser()],
      participants: [this.createUser()],
      labels: ['test', 'automation'],
      draft: false,
      work_in_progress: false,
      merge_when_pipeline_succeeds: false,
      merge_status: 'can_be_merged',
      sha: Math.random().toString(36).substr(2, 40),
      squash: false,
      discussion_locked: false,
      should_remove_source_branch: false,
      force_remove_source_branch: false,
      reference: `!${Math.floor(Math.random() * 1000)}`,
      references: {
        short: `!${Math.floor(Math.random() * 1000)}`,
        relative: `!${Math.floor(Math.random() * 1000)}`,
        full: `test-group/test-project!${Math.floor(Math.random() * 1000)}`,
      },
      web_url: `https://gitlab.com/test/project/-/merge_requests/${Math.floor(Math.random() * 1000)}`,
      time_stats: {
        time_estimate: 0,
        total_time_spent: 0,
        human_time_estimate: null,
        human_total_time_spent: null,
      },
      task_completion_status: {
        count: 0,
        completed_count: 0,
      },
      diff_refs: {
        base_sha: Math.random().toString(36).substr(2, 40),
        head_sha: Math.random().toString(36).substr(2, 40),
        start_sha: Math.random().toString(36).substr(2, 40),
      },
      ...overrides,
    };
  }
}
```

### 5.3 GitLabPlatform Unit Tests

```typescript
// gitlab-platform.test.ts
import { GitLabPlatform } from '../../../src/platforms/gitlab/gitlab-platform';
import { GitLabFactory } from '../factories/gitlab-factory';

describe('GitLabPlatform', () => {
  let gitlabPlatform: GitLabPlatform;
  let mockHttpClient: jest.Mocked<HttpClient>;
  let mockAuthManager: jest.Mocked<GitLabAuthManager>;
  let mockCredentialManager: jest.Mocked<CredentialManager>;

  beforeEach(() => {
    mockHttpClient = createMockHttpClient();
    mockAuthManager = createMockAuthManager();
    mockCredentialManager = createMockCredentialManager();

    gitlabPlatform = new GitLabPlatform({
      instanceUrl: 'https://gitlab.com',
      auth: {
        method: 'pat',
        token: 'test-token',
      },
    });
  });

  describe('Initialization', () => {
    it('should initialize with valid configuration', async () => {
      mockAuthManager.authenticate.mockResolvedValue({
        success: true,
        token: {
          accessToken: 'test-token',
          tokenType: 'Bearer',
          createdAt: new Date(),
        },
        user: GitLabFactory.createUser(),
      });

      await expect(gitlabPlatform.initialize()).resolves.not.toThrow();
      expect(mockAuthManager.authenticate).toHaveBeenCalled();
    });

    it('should throw error when authentication fails', async () => {
      mockAuthManager.authenticate.mockResolvedValue({
        success: false,
        error: 'Invalid credentials',
      });

      await expect(gitlabPlatform.initialize()).rejects.toThrow('Invalid credentials');
    });

    it('should handle network errors during initialization', async () => {
      mockAuthManager.authenticate.mockRejectedValue(new Error('Network error'));

      await expect(gitlabPlatform.initialize()).rejects.toThrow('Network error');
    });
  });

  describe('Project Operations', () => {
    beforeEach(async () => {
      mockAuthManager.authenticate.mockResolvedValue({
        success: true,
        token: {
          accessToken: 'test-token',
          tokenType: 'Bearer',
          createdAt: new Date(),
        },
        user: GitLabFactory.createUser(),
      });
      await gitlabPlatform.initialize();
    });

    it('should get project by ID', async () => {
      const mockProject = GitLabFactory.createProject({ id: 123 });
      mockHttpClient.get.mockResolvedValue({ data: mockProject });

      const result = await gitlabPlatform.getProject(123);

      expect(result).toEqual(mockProject);
      expect(mockHttpClient.get).toHaveBeenCalledWith(
        'https://gitlab.com/api/v4/projects/123',
        expect.any(Object)
      );
    });

    it('should handle project not found', async () => {
      mockHttpClient.get.mockRejectedValue({ status: 404 });

      await expect(gitlabPlatform.getProject(999)).rejects.toThrow();
    });

    it('should list projects with filters', async () => {
      const mockProjects = [GitLabFactory.createProject(), GitLabFactory.createProject()];
      mockHttpClient.get.mockResolvedValue({ data: mockProjects });

      const result = await gitlabPlatform.listProjects({
        search: 'test',
        owned: true,
      });

      expect(result).toEqual(mockProjects);
      expect(mockHttpClient.get).toHaveBeenCalledWith(
        expect.stringContaining('search=test&owned=true'),
        expect.any(Object)
      );
    });
  });

  describe('Repository Operations', () => {
    beforeEach(async () => {
      await gitlabPlatform.initialize();
    });

    it('should get repository branches', async () => {
      const mockBranches = [
        { name: 'main', merged: false, protected: true, default: true },
        { name: 'develop', merged: false, protected: false, default: false },
      ];
      mockHttpClient.get.mockResolvedValue({ data: mockBranches });

      const result = await gitlabPlatform.getBranches(123);

      expect(result).toEqual(mockBranches);
      expect(mockHttpClient.get).toHaveBeenCalledWith(
        'https://gitlab.com/api/v4/projects/123/repository/branches',
        expect.any(Object)
      );
    });

    it('should create branch', async () => {
      const mockBranch = {
        name: 'feature-branch',
        merged: false,
        protected: false,
        default: false,
      };
      mockHttpClient.post.mockResolvedValue({ data: mockBranch });

      const result = await gitlabPlatform.createBranch(123, 'feature-branch', 'main');

      expect(result).toEqual(mockBranch);
      expect(mockHttpClient.post).toHaveBeenCalledWith(
        'https://gitlab.com/api/v4/projects/123/repository/branches',
        {
          branch: 'feature-branch',
          ref: 'main',
        },
        expect.any(Object)
      );
    });

    it('should handle branch creation conflicts', async () => {
      mockHttpClient.post.mockRejectedValue({ status: 409 });

      await expect(gitlabPlatform.createBranch(123, 'existing-branch', 'main')).rejects.toThrow();
    });
  });

  describe('Error Handling', () => {
    beforeEach(async () => {
      await gitlabPlatform.initialize();
    });

    it('should handle rate limiting', async () => {
      const rateLimitError = {
        response: {
          status: 429,
          headers: {
            'retry-after': '60',
          },
        },
      };
      mockHttpClient.get.mockRejectedValue(rateLimitError);

      await expect(gitlabPlatform.getProject(123)).rejects.toThrow();
    });

    it('should handle authentication token expiration', async () => {
      const authError = {
        response: {
          status: 401,
        },
      };
      mockHttpClient.get.mockRejectedValue(authError);

      // Should attempt token refresh
      mockAuthManager.refreshToken.mockResolvedValue({
        accessToken: 'new-token',
        tokenType: 'Bearer',
        createdAt: new Date(),
      });

      await expect(gitlabPlatform.getProject(123)).rejects.toThrow();
      expect(mockAuthManager.refreshToken).toHaveBeenCalled();
    });

    it('should handle network timeouts', async () => {
      const timeoutError = new Error('Request timeout');
      timeoutError.code = 'ETIMEDOUT';
      mockHttpClient.get.mockRejectedValue(timeoutError);

      await expect(gitlabPlatform.getProject(123)).rejects.toThrow('Request timeout');
    });
  });

  describe('Performance', () => {
    beforeEach(async () => {
      await gitlabPlatform.initialize();
    });

    it('should handle concurrent requests', async () => {
      const mockProject = GitLabFactory.createProject();
      mockHttpClient.get.mockResolvedValue({ data: mockProject });

      const promises = Array.from({ length: 10 }, (_, i) => gitlabPlatform.getProject(i + 1));

      const results = await Promise.all(promises);

      expect(results).toHaveLength(10);
      expect(mockHttpClient.get).toHaveBeenCalledTimes(10);
    });

    it('should implement request caching', async () => {
      const mockProject = GitLabFactory.createProject({ id: 123 });
      mockHttpClient.get.mockResolvedValue({ data: mockProject });

      // First call
      const result1 = await gitlabPlatform.getProject(123);
      // Second call (should use cache)
      const result2 = await gitlabPlatform.getProject(123);

      expect(result1).toEqual(result2);
      expect(mockHttpClient.get).toHaveBeenCalledTimes(1);
    });
  });
});
```

### 5.4 Authentication Module Tests

```typescript
// gitlab-auth-manager.test.ts
import { GitLabAuthManager } from '../../../src/platforms/gitlab/gitlab-auth-manager';
import { GitLabFactory } from '../factories/gitlab-factory';

describe('GitLabAuthManager', () => {
  let authManager: GitLabAuthManager;
  let mockHttpClient: jest.Mocked<HttpClient>;
  let mockCredentialManager: jest.Mocked<CredentialManager>;

  beforeEach(() => {
    mockHttpClient = createMockHttpClient();
    mockCredentialManager = createMockCredentialManager();

    authManager = new GitLabAuthManager(
      {
        method: 'pat',
        instanceUrl: 'https://gitlab.com',
        token: 'test-token',
      },
      mockHttpClient,
      mockCredentialManager
    );
  });

  describe('PAT Authentication', () => {
    it('should authenticate with valid PAT token', async () => {
      const mockUser = GitLabFactory.createUser();
      mockHttpClient.get
        .mockResolvedValueOnce({ data: mockUser }) // User validation
        .mockResolvedValueOnce({ data: { scopes: ['read_api', 'read_repository'] } }); // Token scopes

      const result = await authManager.authenticateWithPAT('glpat-1234567890abcdef1234');

      expect(result.success).toBe(true);
      expect(result.token?.accessToken).toBe('glpat-1234567890abcdef1234');
      expect(result.user).toEqual(mockUser);
    });

    it('should reject invalid token format', async () => {
      const result = await authManager.authenticateWithPAT('invalid-token');

      expect(result.success).toBe(false);
      expect(result.error).toContain('Invalid token format');
    });

    it('should handle expired token', async () => {
      mockHttpClient.get.mockRejectedValue({ status: 401 });

      const result = await authManager.authenticateWithPAT('glpat-1234567890abcdef1234');

      expect(result.success).toBe(false);
      expect(result.error).toContain('Invalid or expired token');
    });

    it('should validate required scopes', async () => {
      const mockUser = GitLabFactory.createUser();
      mockHttpClient.get
        .mockResolvedValueOnce({ data: mockUser })
        .mockResolvedValueOnce({ data: { scopes: ['read_api'] } }); // Missing required scopes

      const result = await authManager.authenticateWithPAT('glpat-1234567890abcdef1234');

      expect(result.success).toBe(false);
      expect(result.error).toContain('missing required scopes');
    });
  });

  describe('OAuth2 Authentication', () => {
    beforeEach(() => {
      authManager = new GitLabAuthManager(
        {
          method: 'oauth2',
          instanceUrl: 'https://gitlab.com',
          clientId: 'test-client-id',
          clientSecret: 'test-client-secret',
          redirectUri: 'https://example.com/callback',
        },
        mockHttpClient,
        mockCredentialManager
      );
    });

    it('should initiate OAuth2 flow', async () => {
      const result = await authManager.initiateOAuth2Flow();

      expect(result.url).toContain('oauth/authorize');
      expect(result.state).toBeDefined();
      expect(result.state).toHaveLength(64); // 32 bytes * 2 (hex)
    });

    it('should handle OAuth2 callback', async () => {
      const mockUser = GitLabFactory.createUser();
      const mockTokenResponse = {
        access_token: 'access-token',
        refresh_token: 'refresh-token',
        expires_in: 7200,
        scope: 'read_api read_repository',
      };

      mockHttpClient.post.mockResolvedValue({ data: mockTokenResponse });
      mockHttpClient.get.mockResolvedValue({ data: mockUser });

      const result = await authManager.handleOAuth2Callback('auth-code', 'test-state');

      expect(result.success).toBe(true);
      expect(result.token?.accessToken).toBe('access-token');
      expect(result.user).toEqual(mockUser);
    });

    it('should refresh OAuth2 token', async () => {
      const mockNewToken = {
        access_token: 'new-access-token',
        refresh_token: 'new-refresh-token',
        expires_in: 7200,
      };

      mockHttpClient.post.mockResolvedValue({ data: mockNewToken });

      const result = await authManager.refreshToken('refresh-token');

      expect(result.accessToken).toBe('new-access-token');
      expect(result.refreshToken).toBe('new-refresh-token');
    });
  });

  describe('Token Management', () => {
    it('should detect expired tokens', () => {
      const expiredToken = {
        accessToken: 'token',
        tokenType: 'Bearer' as const,
        expiresIn: 3600,
        createdAt: new Date(Date.now() - 2 * 60 * 60 * 1000), // 2 hours ago
      };

      expect(authManager.isTokenExpired(expiredToken)).toBe(true);
    });

    it('should detect valid tokens', () => {
      const validToken = {
        accessToken: 'token',
        tokenType: 'Bearer' as const,
        expiresIn: 3600,
        createdAt: new Date(Date.now() - 30 * 60 * 1000), // 30 minutes ago
      };

      expect(authManager.isTokenExpired(validToken)).toBe(false);
    });

    it('should calculate remaining time correctly', () => {
      const token = {
        accessToken: 'token',
        tokenType: 'Bearer' as const,
        expiresIn: 3600,
        createdAt: new Date(Date.now() - 30 * 60 * 1000), // 30 minutes ago
      };

      const remainingTime = authManager.getRemainingTime(token);
      expect(remainingTime).toBeGreaterThan(30 * 60 * 1000); // More than 30 minutes
      expect(remainingTime).toBeLessThan(60 * 60 * 1000); // Less than 60 minutes
    });
  });

  describe('Credential Management', () => {
    it('should store credentials securely', async () => {
      const token = {
        accessToken: 'sensitive-token',
        tokenType: 'Bearer' as const,
        createdAt: new Date(),
      };

      await authManager.storeCredentials(token);

      expect(mockCredentialManager.store).toHaveBeenCalledWith(
        'tamma-gitlab',
        'gitlab_com',
        expect.any(String) // Encrypted data
      );
    });

    it('should retrieve and decrypt credentials', async () => {
      const token = {
        accessToken: 'sensitive-token',
        tokenType: 'Bearer' as const,
        createdAt: new Date(),
      };

      mockCredentialManager.retrieve.mockResolvedValue('encrypted-data');

      // Mock the decryption
      jest.spyOn(authManager as any, 'decryptToken').mockResolvedValue(token);

      const result = await authManager.retrieveCredentials();

      expect(result).toEqual(token);
      expect(mockCredentialManager.retrieve).toHaveBeenCalledWith('tamma-gitlab', 'gitlab_com');
    });

    it('should handle corrupted credentials', async () => {
      mockCredentialManager.retrieve.mockResolvedValue('corrupted-data');

      jest
        .spyOn(authManager as any, 'decryptToken')
        .mockRejectedValue(new Error('Decryption failed'));

      const result = await authManager.retrieveCredentials();

      expect(result).toBeNull();
      expect(mockCredentialManager.delete).toHaveBeenCalled();
    });
  });
});
```

### 5.5 CI/CD Module Tests

```typescript
// gitlab-cicd-manager.test.ts
import { GitLabCICDManager } from '../../../src/platforms/gitlab/gitlab-cicd-manager';
import { GitLabFactory } from '../factories/gitlab-factory';

describe('GitLabCICDManager', () => {
  let cicdManager: GitLabCICDManager;
  let mockHttpClient: jest.Mocked<HttpClient>;
  let mockWebhookManager: jest.Mocked<GitLabWebhookManager>;

  beforeEach(() => {
    mockHttpClient = createMockHttpClient();
    mockWebhookManager = createMockWebhookManager();

    cicdManager = new GitLabCICDManager('https://gitlab.com', mockHttpClient, mockWebhookManager);
  });

  describe('Pipeline Operations', () => {
    it('should get pipeline details', async () => {
      const mockPipeline = GitLabFactory.createPipeline({ id: 456 });
      mockHttpClient.get.mockResolvedValue({ data: mockPipeline });

      const result = await cicdManager.getPipeline(123, 456);

      expect(result).toEqual(mockPipeline);
      expect(mockHttpClient.get).toHaveBeenCalledWith(
        'https://gitlab.com/api/v4/projects/123/pipelines/456',
        expect.any(Object)
      );
    });

    it('should list pipelines with filters', async () => {
      const mockPipelines = [GitLabFactory.createPipeline(), GitLabFactory.createPipeline()];
      mockHttpClient.get.mockResolvedValue({ data: mockPipelines });

      const result = await cicdManager.getPipelines(123, {
        status: 'success',
        ref: 'main',
      });

      expect(result).toEqual(mockPipelines);
      expect(mockHttpClient.get).toHaveBeenCalledWith(
        expect.stringContaining('status=success&ref=main'),
        expect.any(Object)
      );
    });

    it('should trigger pipeline with variables', async () => {
      const mockPipeline = GitLabFactory.createPipeline();
      mockHttpClient.post.mockResolvedValue({ data: mockPipeline });

      const variables = [
        { key: 'TEST_VAR', value: 'test_value', variable_type: 'env_var' as const },
      ];

      const result = await cicdManager.triggerPipeline(123, 'main', variables);

      expect(result).toEqual(mockPipeline);
      expect(mockHttpClient.post).toHaveBeenCalledWith(
        'https://gitlab.com/api/v4/projects/123/pipeline',
        expect.objectContaining({
          ref: 'main',
          variables: expect.arrayContaining([
            expect.objectContaining({ key: 'TEST_VAR', value: 'test_value' }),
          ]),
        }),
        expect.any(Object)
      );
    });

    it('should cancel pipeline', async () => {
      mockHttpClient.post.mockResolvedValue({});

      await cicdManager.cancelPipeline(123, 456);

      expect(mockHttpClient.post).toHaveBeenCalledWith(
        'https://gitlab.com/api/v4/projects/123/pipelines/456/cancel',
        {},
        expect.any(Object)
      );
    });

    it('should retry pipeline', async () => {
      const mockPipeline = GitLabFactory.createPipeline();
      mockHttpClient.post.mockResolvedValue({ data: mockPipeline });

      const result = await cicdManager.retryPipeline(123, 456);

      expect(result).toEqual(mockPipeline);
      expect(mockHttpClient.post).toHaveBeenCalledWith(
        'https://gitlab.com/api/v4/projects/123/pipelines/456/retry',
        {},
        expect.any(Object)
      );
    });
  });

  describe('Job Operations', () => {
    it('should get job details', async () => {
      const mockJob = GitLabFactory.createJob({ id: 789 });
      mockHttpClient.get.mockResolvedValue({ data: mockJob });

      const result = await cicdManager.getJob(123, 789);

      expect(result).toEqual(mockJob);
      expect(mockHttpClient.get).toHaveBeenCalledWith(
        'https://gitlab.com/api/v4/projects/123/jobs/789',
        expect.any(Object)
      );
    });

    it('should get jobs for pipeline', async () => {
      const mockJobs = [GitLabFactory.createJob(), GitLabFactory.createJob()];
      mockHttpClient.get.mockResolvedValue({ data: mockJobs });

      const result = await cicdManager.getJobs(123, 456);

      expect(result).toEqual(mockJobs);
      expect(mockHttpClient.get).toHaveBeenCalledWith(
        'https://gitlab.com/api/v4/projects/123/pipelines/456/jobs',
        expect.any(Object)
      );
    });

    it('should get job logs', async () => {
      const mockLogs = 'Job execution logs...';
      mockHttpClient.get.mockResolvedValue({ data: mockLogs });

      const result = await cicdManager.getJobLog(123, 789);

      expect(result).toBe(mockLogs);
      expect(mockHttpClient.get).toHaveBeenCalledWith(
        'https://gitlab.com/api/v4/projects/123/jobs/789/trace',
        expect.any(Object)
      );
    });

    it('should download job artifacts', async () => {
      const mockArtifacts = Buffer.from('artifact data');
      mockHttpClient.get.mockResolvedValue({ data: mockArtifacts });

      const result = await cicdManager.downloadJobArtifacts(123, 789);

      expect(result).toEqual(mockArtifacts);
      expect(mockHttpClient.get).toHaveBeenCalledWith(
        'https://gitlab.com/api/v4/projects/123/jobs/789/artifacts',
        expect.objectContaining({
          responseType: 'arraybuffer',
        })
      );
    });
  });

  describe('Configuration Validation', () => {
    it('should validate valid pipeline configuration', async () => {
      const validConfig = {
        stages: ['build', 'test', 'deploy'],
        jobs: {
          build: {
            stage: 'build',
            script: ['npm install', 'npm run build'],
          },
          test: {
            stage: 'test',
            script: ['npm test'],
          },
        },
        variables: [],
        cache: {},
        services: [],
      };

      const result = await cicdManager.validatePipelineConfig(validConfig);

      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    it('should detect missing stages', async () => {
      const invalidConfig = {
        stages: [],
        jobs: {
          build: {
            stage: 'build',
            script: ['npm build'],
          },
        },
        variables: [],
        cache: {},
        services: [],
      };

      const result = await cicdManager.validatePipelineConfig(invalidConfig);

      expect(result.valid).toBe(false);
      expect(result.errors).toContain('Pipeline must define at least one stage');
    });

    it('should detect circular dependencies', async () => {
      const configWithCircularDeps = {
        stages: ['build', 'test'],
        jobs: {
          job1: {
            stage: 'build',
            script: ['echo "job1"'],
            dependencies: ['job2'],
          },
          job2: {
            stage: 'test',
            script: ['echo "job2"'],
            dependencies: ['job1'],
          },
        },
        variables: [],
        cache: {},
        services: [],
      };

      const result = await cicdManager.validatePipelineConfig(configWithCircularDeps);

      expect(result.valid).toBe(false);
      expect(result.errors).toContain(expect.stringContaining('Circular dependencies detected'));
    });

    it('should detect undefined stage references', async () => {
      const invalidConfig = {
        stages: ['build'],
        jobs: {
          build: {
            stage: 'build',
            script: ['npm build'],
          },
          test: {
            stage: 'test', // Undefined stage
            script: ['npm test'],
          },
        },
        variables: [],
        cache: {},
        services: [],
      };

      const result = await cicdManager.validatePipelineConfig(invalidConfig);

      expect(result.valid).toBe(false);
      expect(result.errors).toContain(expect.stringContaining('references undefined stage "test"'));
    });
  });

  describe('Metrics and Analytics', () => {
    it('should calculate pipeline metrics', async () => {
      const mockPipelines = [
        GitLabFactory.createPipeline({ status: 'success', duration: 300 }),
        GitLabFactory.createPipeline({ status: 'failed', duration: 600 }),
        GitLabFactory.createPipeline({ status: 'success', duration: 400 }),
      ];
      mockHttpClient.get.mockResolvedValue({ data: mockPipelines });

      const timeRange = {
        start: new Date(Date.now() - 24 * 60 * 60 * 1000),
        end: new Date(),
      };

      const result = await cicdManager.getPipelineMetrics(123, timeRange);

      expect(result.total).toBe(3);
      expect(result.successful).toBe(2);
      expect(result.failed).toBe(1);
      expect(result.successRate).toBeCloseTo(66.67, 1);
      expect(result.averageDuration).toBeCloseTo(433.33, 1);
    });
  });
});
```

### 5.6 Test Coverage Configuration

```typescript
// jest.config.js
module.exports = {
  preset: 'ts-jest',
  testEnvironment: 'node',
  roots: ['<rootDir>/src', '<rootDir>/test'],
  testMatch: ['**/__tests__/**/*.ts', '**/?(*.)+(spec|test).ts'],
  transform: {
    '^.+\\.ts$': 'ts-jest',
  },
  collectCoverageFrom: [
    'src/**/*.ts',
    '!src/**/*.d.ts',
    '!src/**/*.interface.ts',
    '!src/**/*.types.ts',
  ],
  coverageDirectory: 'coverage',
  coverageReporters: ['text', 'lcov', 'html', 'json-summary'],
  coverageThreshold: {
    global: {
      branches: 80,
      functions: 85,
      lines: 80,
      statements: 80,
    },
    './src/platforms/gitlab/': {
      branches: 90,
      functions: 95,
      lines: 90,
      statements: 90,
    },
  },
  setupFilesAfterEnv: ['<rootDir>/test/test-setup.ts'],
  moduleNameMapping: {
    '^@/(.*)$': '<rootDir>/src/$1',
    '^@test/(.*)$': '<rootDir>/test/$1',
  },
  testTimeout: 30000,
  verbose: true,
};
```

### 5.7 Performance Testing Utilities

```typescript
// utils/performance-test.ts
export class PerformanceTestUtils {
  static async measureExecutionTime<T>(
    fn: () => Promise<T>,
    iterations: number = 1
  ): Promise<{ result: T; averageTime: number; totalTime: number }> {
    const startTime = Date.now();
    let result: T;

    for (let i = 0; i < iterations; i++) {
      result = await fn();
    }

    const totalTime = Date.now() - startTime;
    const averageTime = totalTime / iterations;

    return {
      result: result!,
      averageTime,
      totalTime,
    };
  }

  static async measureMemoryUsage<T>(
    fn: () => Promise<T>
  ): Promise<{ result: T; memoryBefore: number; memoryAfter: number; memoryDiff: number }> {
    const memoryBefore = process.memoryUsage().heapUsed;
    const result = await fn();
    const memoryAfter = process.memoryUsage().heapUsed;

    return {
      result,
      memoryBefore,
      memoryAfter,
      memoryDiff: memoryAfter - memoryBefore,
    };
  }

  static async runConcurrentTest<T>(
    fn: () => Promise<T>,
    concurrency: number = 10
  ): Promise<{ results: T[]; totalTime: number; errors: Error[] }> {
    const startTime = Date.now();
    const promises = Array.from({ length: concurrency }, () => fn());

    const results = await Promise.allSettled(promises);
    const totalTime = Date.now() - startTime;

    const successfulResults: T[] = [];
    const errors: Error[] = [];

    for (const result of results) {
      if (result.status === 'fulfilled') {
        successfulResults.push(result.value);
      } else {
        errors.push(result.reason);
      }
    }

    return {
      results: successfulResults,
      totalTime,
      errors,
    };
  }
}
```

## Completion Checklist

- [ ] Implement comprehensive test infrastructure and utilities
- [ ] Create mock data factories for all GitLab entities
- [ ] Implement GitLabPlatform unit tests with 100% coverage
- [ ] Implement GitLabAuthManager unit tests with all authentication flows
- [ ] Implement GitLabCICDManager unit tests with all CI/CD operations
- [ ] Implement GitLabMergeRequestManager unit tests with all MR operations
- [ ] Implement workflow integration unit tests
- [ ] Add performance testing utilities and benchmarks
- [ ] Configure Jest with coverage thresholds and reporting
- [ ] Add integration test helpers and utilities
- [ ] Verify all test scenarios including error cases
- [ ] Ensure code coverage targets are achieved (90%+ for GitLab modules)

## Dependencies

- Task 1-4: All GitLab platform implementation tasks
- Jest testing framework with TypeScript support
- Mock libraries for HTTP clients and external dependencies
- Test data factories and utilities
- Performance measurement tools

## Estimated Time

**Test Infrastructure**: 2-3 days
**Core Platform Tests**: 3-4 days
**Authentication Tests**: 2-3 days
**CI/CD Tests**: 3-4 days
**MR Tests**: 3-4 days
**Workflow Tests**: 2-3 days
**Performance Tests**: 1-2 days
**Total**: 16-23 days
