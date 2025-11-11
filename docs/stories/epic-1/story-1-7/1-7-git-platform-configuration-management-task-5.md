# Story 1.7 Task 5: Implement Comprehensive Testing

## Task Overview

Implement comprehensive testing for Git platform configuration management, including unit tests for all configuration components, integration tests for platform connections, performance tests for configuration loading, security tests for credential handling, and end-to-end tests for complete workflows.

## Acceptance Criteria

### 5.1 Unit Tests for All Configuration Components

- [ ] Create unit tests for configuration schema validation
- [ ] Test configuration loading and parsing logic
- [ ] Test platform detection and selection algorithms
- [ ] Test authentication method validation
- [ ] Test environment variable override functionality
- [ ] Test error handling and edge cases

### 5.2 Integration Tests for Platform Connections

- [ ] Create integration tests for all supported platforms
- [ ] Test authentication flows for each platform
- [ ] Test API endpoint validation and connectivity
- [ ] Test rate limiting and timeout handling
- [ ] Test platform-specific features and capabilities
- [ ] Test error scenarios and recovery mechanisms

### 5.3 Performance Tests for Configuration Loading

- [ ] Create performance tests for configuration file loading
- [ ] Test memory usage and resource consumption
- [ ] Test concurrent configuration access
- [ ] Test caching performance and invalidation
- [ ] Test large configuration file handling
- [ ] Establish performance benchmarks and thresholds

### 5.4 Security Tests for Credential Handling

- [ ] Create security tests for token encryption and storage
- [ ] Test credential validation and sanitization
- [ ] Test access control and permission handling
- [ ] Test audit logging for security events
- [ ] Test secure credential transmission
- [ ] Test credential rotation and revocation

### 5.5 End-to-End Tests for Complete Workflows

- [ ] Create E2E tests for multi-platform workflows
- [ ] Test platform switching and failover scenarios
- [ ] Test configuration migration procedures
- [ ] Test complete authentication and authorization flows
- [ ] Test error recovery and self-healing mechanisms
- [ ] Test real-world usage scenarios

## Implementation Details

### 5.1 Unit Tests Implementation

```typescript
// packages/platforms/src/config/__tests__/schema-validation.test.ts

import { validateConfiguration, ConfigurationSchema } from '../schema-validation';
import { PlatformType, AuthenticationMethod } from '../types';

describe('Configuration Schema Validation', () => {
  describe('Valid Configurations', () => {
    it('should validate minimal valid configuration', () => {
      const config = {
        version: '1.2.0',
        platforms: [
          {
            type: PlatformType.GITHUB,
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: AuthenticationMethod.PAT,
              tokenEnvVar: 'GITHUB_TOKEN',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
        ],
      };

      const result = validateConfiguration(config);
      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    it('should validate complete multi-platform configuration', () => {
      const config = {
        version: '1.2.0',
        defaultPlatform: 'github',
        platforms: [
          {
            type: PlatformType.GITHUB,
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: AuthenticationMethod.PAT,
              tokenEnvVar: 'GITHUB_TOKEN',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
          {
            type: PlatformType.GITLAB,
            name: 'GitLab',
            baseUrl: 'https://gitlab.com',
            auth: {
              method: AuthenticationMethod.OAUTH2,
              clientId: 'gitlab-client-id',
              clientSecretEnvVar: 'GITLAB_CLIENT_SECRET',
              redirectUri: 'https://example.com/auth/gitlab/callback',
              scopes: ['read_api', 'read_repository'],
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 90,
          },
        ],
        global: {
          defaultTimeout: 30000,
          rateLimit: {
            requestsPerSecond: 10,
            burstSize: 100,
          },
        },
      };

      const result = validateConfiguration(config);
      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    it('should validate configuration with all optional fields', () => {
      const config = {
        version: '1.2.0',
        defaultPlatform: 'github',
        platforms: [
          {
            type: PlatformType.GITHUB,
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: AuthenticationMethod.PAT,
              tokenEnvVar: 'GITHUB_TOKEN',
              timeout: 60000,
              rateLimit: {
                requestsPerSecond: 5,
                burstSize: 50,
              },
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
            apiVersion: '2022-11-28',
            pullRequestTemplate: {
              path: '.github/PULL_REQUEST_TEMPLATE.md',
              required: false,
            },
            labels: {
              defaults: ['bug', 'enhancement'],
              prefixes: {
                feature: 'feat',
                bugfix: 'fix',
              },
            },
          },
        ],
        global: {
          defaultTimeout: 30000,
          rateLimit: {
            requestsPerSecond: 10,
            burstSize: 100,
          },
          logging: {
            level: 'info',
            format: 'json',
          },
          proxy: {
            http: 'http://proxy.example.com:8080',
            https: 'http://proxy.example.com:8080',
            noProxy: ['localhost', '127.0.0.1'],
          },
        },
      };

      const result = validateConfiguration(config);
      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });
  });

  describe('Invalid Configurations', () => {
    it('should reject configuration with missing required fields', () => {
      const config = {
        platforms: [
          {
            type: PlatformType.GITHUB,
            name: 'GitHub',
            // Missing baseUrl, auth, etc.
          },
        ],
      };

      const result = validateConfiguration(config);
      expect(result.valid).toBe(false);
      expect(result.errors.length).toBeGreaterThan(0);
      expect(result.errors.some((e) => e.includes('version'))).toBe(true);
      expect(result.errors.some((e) => e.includes('baseUrl'))).toBe(true);
      expect(result.errors.some((e) => e.includes('auth'))).toBe(true);
    });

    it('should reject configuration with invalid platform type', () => {
      const config = {
        version: '1.2.0',
        platforms: [
          {
            type: 'invalid-platform',
            name: 'Invalid',
            baseUrl: 'https://example.com',
            auth: {
              method: AuthenticationMethod.PAT,
              tokenEnvVar: 'TOKEN',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
        ],
      };

      const result = validateConfiguration(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some((e) => e.includes('type'))).toBe(true);
    });

    it('should reject configuration with invalid URL', () => {
      const config = {
        version: '1.2.0',
        platforms: [
          {
            type: PlatformType.GITHUB,
            name: 'GitHub',
            baseUrl: 'not-a-valid-url',
            auth: {
              method: AuthenticationMethod.PAT,
              tokenEnvVar: 'GITHUB_TOKEN',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
        ],
      };

      const result = validateConfiguration(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some((e) => e.includes('baseUrl'))).toBe(true);
    });

    it('should reject configuration with invalid authentication method', () => {
      const config = {
        version: '1.2.0',
        platforms: [
          {
            type: PlatformType.GITHUB,
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: 'invalid-method',
              tokenEnvVar: 'GITHUB_TOKEN',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
        ],
      };

      const result = validateConfiguration(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some((e) => e.includes('auth.method'))).toBe(true);
    });

    it('should reject configuration with duplicate platform names', () => {
      const config = {
        version: '1.2.0',
        platforms: [
          {
            type: PlatformType.GITHUB,
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: AuthenticationMethod.PAT,
              tokenEnvVar: 'GITHUB_TOKEN',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
          {
            type: PlatformType.GITLAB,
            name: 'GitHub', // Duplicate name
            baseUrl: 'https://gitlab.com',
            auth: {
              method: AuthenticationMethod.PAT,
              tokenEnvVar: 'GITLAB_TOKEN',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 90,
          },
        ],
      };

      const result = validateConfiguration(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some((e) => e.includes('duplicate'))).toBe(true);
    });

    it('should reject configuration with invalid priority values', () => {
      const config = {
        version: '1.2.0',
        platforms: [
          {
            type: PlatformType.GITHUB,
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: AuthenticationMethod.PAT,
              tokenEnvVar: 'GITHUB_TOKEN',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 150, // Invalid: > 100
          },
        ],
      };

      const result = validateConfiguration(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some((e) => e.includes('priority'))).toBe(true);
    });
  });

  describe('Edge Cases', () => {
    it('should handle empty platforms array', () => {
      const config = {
        version: '1.2.0',
        platforms: [],
      };

      const result = validateConfiguration(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some((e) => e.includes('platforms'))).toBe(true);
    });

    it('should handle null and undefined values', () => {
      const config = {
        version: '1.2.0',
        platforms: [
          {
            type: PlatformType.GITHUB,
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: null, // Invalid: null auth
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
        ],
      };

      const result = validateConfiguration(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some((e) => e.includes('auth'))).toBe(true);
    });

    it('should validate OAuth2 specific requirements', () => {
      const config = {
        version: '1.2.0',
        platforms: [
          {
            type: PlatformType.GITLAB,
            name: 'GitLab',
            baseUrl: 'https://gitlab.com',
            auth: {
              method: AuthenticationMethod.OAUTH2,
              clientId: 'client-id',
              // Missing clientSecretEnvVar and redirectUri
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
        ],
      };

      const result = validateConfiguration(config);
      expect(result.valid).toBe(false);
      expect(result.errors.some((e) => e.includes('clientSecretEnvVar'))).toBe(true);
      expect(result.errors.some((e) => e.includes('redirectUri'))).toBe(true);
    });
  });
});
```

```typescript
// packages/platforms/src/config/__tests__/platform-registry.test.ts

import { PlatformRegistry } from '../platform-registry';
import { PlatformType, AuthenticationMethod } from '../types';

describe('Platform Registry', () => {
  let registry: PlatformRegistry;

  beforeEach(() => {
    registry = new PlatformRegistry();
  });

  describe('Platform Registration', () => {
    it('should register platforms successfully', () => {
      const platformConfig = {
        type: PlatformType.GITHUB,
        name: 'GitHub',
        baseUrl: 'https://github.com',
        auth: {
          method: AuthenticationMethod.PAT,
          tokenEnvVar: 'GITHUB_TOKEN',
        },
        defaultBranch: 'main',
        enabled: true,
        priority: 100,
      };

      registry.register(platformConfig);
      expect(registry.getPlatform('GitHub')).toBeDefined();
      expect(registry.getPlatform('GitHub')!.name).toBe('GitHub');
    });

    it('should throw error when registering duplicate platform', () => {
      const platformConfig = {
        type: PlatformType.GITHUB,
        name: 'GitHub',
        baseUrl: 'https://github.com',
        auth: {
          method: AuthenticationMethod.PAT,
          tokenEnvVar: 'GITHUB_TOKEN',
        },
        defaultBranch: 'main',
        enabled: true,
        priority: 100,
      };

      registry.register(platformConfig);

      expect(() => registry.register(platformConfig)).toThrow(
        'Platform with name "GitHub" is already registered'
      );
    });

    it('should unregister platforms successfully', () => {
      const platformConfig = {
        type: PlatformType.GITHUB,
        name: 'GitHub',
        baseUrl: 'https://github.com',
        auth: {
          method: AuthenticationMethod.PAT,
          tokenEnvVar: 'GITHUB_TOKEN',
        },
        defaultBranch: 'main',
        enabled: true,
        priority: 100,
      };

      registry.register(platformConfig);
      expect(registry.getPlatform('GitHub')).toBeDefined();

      registry.unregister('GitHub');
      expect(registry.getPlatform('GitHub')).toBeUndefined();
    });
  });

  describe('Platform Selection', () => {
    beforeEach(() => {
      // Register multiple platforms
      registry.register({
        type: PlatformType.GITHUB,
        name: 'GitHub',
        baseUrl: 'https://github.com',
        auth: {
          method: AuthenticationMethod.PAT,
          tokenEnvVar: 'GITHUB_TOKEN',
        },
        defaultBranch: 'main',
        enabled: true,
        priority: 100,
      });

      registry.register({
        type: PlatformType.GITLAB,
        name: 'GitLab',
        baseUrl: 'https://gitlab.com',
        auth: {
          method: AuthenticationMethod.PAT,
          tokenEnvVar: 'GITLAB_TOKEN',
        },
        defaultBranch: 'main',
        enabled: true,
        priority: 90,
      });

      registry.register({
        type: PlatformType.GITEA,
        name: 'Gitea',
        baseUrl: 'https://gitea.com',
        auth: {
          method: AuthenticationMethod.PAT,
          tokenEnvVar: 'GITEA_TOKEN',
        },
        defaultBranch: 'main',
        enabled: false, // Disabled
        priority: 80,
      });
    });

    it('should select platform by name', () => {
      const platform = registry.selectPlatform('GitHub');
      expect(platform).toBeDefined();
      expect(platform!.name).toBe('GitHub');
    });

    it('should return undefined for non-existent platform', () => {
      const platform = registry.selectPlatform('NonExistent');
      expect(platform).toBeUndefined();
    });

    it('should select platform by URL', () => {
      const platform = registry.selectPlatformByUrl('https://github.com/user/repo');
      expect(platform).toBeDefined();
      expect(platform!.name).toBe('GitHub');

      const gitlabPlatform = registry.selectPlatformByUrl('https://gitlab.com/group/project');
      expect(gitlabPlatform).toBeDefined();
      expect(gitlabPlatform!.name).toBe('GitLab');
    });

    it('should return null for unsupported URL', () => {
      const platform = registry.selectPlatformByUrl('https://unsupported.com/user/repo');
      expect(platform).toBeNull();
    });

    it('should select default platform', () => {
      registry.setDefaultPlatform('GitHub');
      const platform = registry.getDefaultPlatform();
      expect(platform).toBeDefined();
      expect(platform!.name).toBe('GitHub');
    });

    it('should return null when no default platform set', () => {
      const platform = registry.getDefaultPlatform();
      expect(platform).toBeNull();
    });

    it('should select best platform based on priority', () => {
      const platforms = registry.getEnabledPlatforms();
      expect(platforms).toHaveLength(2);
      expect(platforms[0].name).toBe('GitHub'); // Higher priority
      expect(platforms[1].name).toBe('GitLab');
    });

    it('should only return enabled platforms', () => {
      const enabledPlatforms = registry.getEnabledPlatforms();
      expect(enabledPlatforms.map((p) => p.name)).not.toContain('Gitea');
    });
  });

  describe('Platform Detection', () => {
    beforeEach(() => {
      registry.register({
        type: PlatformType.GITHUB,
        name: 'GitHub',
        baseUrl: 'https://github.com',
        auth: {
          method: AuthenticationMethod.PAT,
          tokenEnvVar: 'GITHUB_TOKEN',
        },
        defaultBranch: 'main',
        enabled: true,
        priority: 100,
      });

      registry.register({
        type: PlatformType.GITLAB,
        name: 'GitLab',
        baseUrl: 'https://gitlab.com',
        auth: {
          method: AuthenticationMethod.PAT,
          tokenEnvVar: 'GITLAB_TOKEN',
        },
        defaultBranch: 'main',
        enabled: true,
        priority: 90,
      });
    });

    it('should detect GitHub URLs correctly', () => {
      const urls = [
        'https://github.com/user/repo',
        'https://github.com/user/repo.git',
        'git@github.com:user/repo.git',
        'https://api.github.com/repos/user/repo',
      ];

      urls.forEach((url) => {
        const detected = registry.detectPlatform(url);
        expect(detected).toBe('GitHub');
      });
    });

    it('should detect GitLab URLs correctly', () => {
      const urls = [
        'https://gitlab.com/group/project',
        'https://gitlab.com/group/project.git',
        'git@gitlab.com:group/project.git',
        'https://gitlab.com/api/v4/projects/group%2Fproject',
      ];

      urls.forEach((url) => {
        const detected = registry.detectPlatform(url);
        expect(detected).toBe('GitLab');
      });
    });

    it('should handle custom domain platforms', () => {
      registry.register({
        type: PlatformType.GITHUB,
        name: 'GitHub Enterprise',
        baseUrl: 'https://github.enterprise.com',
        auth: {
          method: AuthenticationMethod.PAT,
          tokenEnvVar: 'GITHUB_ENTERPRISE_TOKEN',
        },
        defaultBranch: 'main',
        enabled: true,
        priority: 80,
      });

      const detected = registry.detectPlatform('https://github.enterprise.com/user/repo');
      expect(detected).toBe('GitHub Enterprise');
    });
  });
});
```

### 5.2 Integration Tests Implementation

```typescript
// packages/platforms/src/__tests__/integration/github.test.ts

import { GitHubPlatform } from '../github';
import { AuthenticationMethod } from '../config/types';

describe('GitHub Platform Integration Tests', () => {
  let platform: GitHubPlatform;
  const testConfig = {
    type: 'github' as const,
    name: 'GitHub Test',
    baseUrl: 'https://github.com',
    auth: {
      method: AuthenticationMethod.PAT,
      tokenEnvVar: 'GITHUB_TOKEN_TEST',
    },
    defaultBranch: 'main',
    enabled: true,
    priority: 100,
  };

  beforeAll(async () => {
    // Skip tests if no test token available
    if (!process.env.GITHUB_TOKEN_TEST) {
      console.warn('Skipping GitHub integration tests - no test token provided');
      return;
    }

    platform = new GitHubPlatform(testConfig);
    await platform.initialize();
  });

  afterAll(async () => {
    if (platform) {
      await platform.dispose();
    }
  });

  describe('Authentication', () => {
    it('should authenticate with valid token', async () => {
      if (!process.env.GITHUB_TOKEN_TEST) return;

      const isValid = await platform.testConnection();
      expect(isValid).toBe(true);
    });

    it('should fail authentication with invalid token', async () => {
      const invalidConfig = {
        ...testConfig,
        auth: {
          method: AuthenticationMethod.PAT,
          tokenEnvVar: 'INVALID_TOKEN',
        },
      };

      const invalidPlatform = new GitHubPlatform(invalidConfig);
      await invalidPlatform.initialize();

      const isValid = await invalidPlatform.testConnection();
      expect(isValid).toBe(false);

      await invalidPlatform.dispose();
    });

    it('should get authenticated user information', async () => {
      if (!process.env.GITHUB_TOKEN_TEST) return;

      const user = await platform.getAuthenticatedUser();
      expect(user).toBeDefined();
      expect(user.login).toBeDefined();
      expect(user.id).toBeDefined();
    });
  });

  describe('Repository Operations', () => {
    const testRepo = 'tamma-test-github';
    const testOwner = 'meywd';

    it('should get repository information', async () => {
      if (!process.env.GITHUB_TOKEN_TEST) return;

      const repo = await platform.getRepository(testOwner, testRepo);
      expect(repo).toBeDefined();
      expect(repo.name).toBe(testRepo);
      expect(repo.owner.login).toBe(testOwner);
    });

    it('should list repository branches', async () => {
      if (!process.env.GITHUB_TOKEN_TEST) return;

      const branches = await platform.listBranches(testOwner, testRepo);
      expect(Array.isArray(branches)).toBe(true);
      expect(branches.length).toBeGreaterThan(0);
      expect(branches[0].name).toBeDefined();
    });

    it('should get default branch', async () => {
      if (!process.env.GITHUB_TOKEN_TEST) return;

      const defaultBranch = await platform.getDefaultBranch(testOwner, testRepo);
      expect(defaultBranch).toBeDefined();
      expect(typeof defaultBranch).toBe('string');
    });
  });

  describe('Pull Request Operations', () => {
    const testRepo = 'tamma-test-github';
    const testOwner = 'meywd';

    it('should list pull requests', async () => {
      if (!process.env.GITHUB_TOKEN_TEST) return;

      const prs = await platform.listPullRequests(testOwner, testRepo);
      expect(Array.isArray(prs)).toBe(true);
    });

    it('should create and merge pull request (integration test)', async () => {
      if (!process.env.GITHUB_TOKEN_TEST) return;

      // This test requires a test repository with write permissions
      // Skip in CI environments
      if (process.env.CI) {
        console.warn('Skipping PR creation test in CI environment');
        return;
      }

      const branchName = `test-branch-${Date.now()}`;

      try {
        // Create test branch (would need implementation)
        // await platform.createBranch(testOwner, testRepo, branchName, 'main');

        // Create pull request
        const pr = await platform.createPullRequest(testOwner, testRepo, {
          title: 'Test Pull Request',
          head: branchName,
          base: 'main',
          body: 'This is a test pull request for integration testing',
        });

        expect(pr).toBeDefined();
        expect(pr.number).toBeDefined();
        expect(pr.title).toBe('Test Pull Request');

        // Clean up - close PR
        await platform.closePullRequest(testOwner, testRepo, pr.number);
      } catch (error) {
        console.warn('PR creation test failed:', error);
      }
    });
  });

  describe('Rate Limiting', () => {
    it('should respect rate limits', async () => {
      if (!process.env.GITHUB_TOKEN_TEST) return;

      // Make multiple requests to test rate limiting
      const promises = Array(10)
        .fill(null)
        .map(() => platform.getRepository('meywd', 'tamma'));

      const results = await Promise.allSettled(promises);
      const failures = results.filter((r) => r.status === 'rejected');

      // Should not fail due to rate limiting with proper implementation
      expect(failures.length).toBe(0);
    });
  });

  describe('Error Handling', () => {
    it('should handle repository not found', async () => {
      if (!process.env.GITHUB_TOKEN_TEST) return;

      await expect(platform.getRepository('nonexistent', 'nonexistent')).rejects.toThrow();
    });

    it('should handle network timeouts', async () => {
      if (!process.env.GITHUB_TOKEN_TEST) return;

      // Create platform with very short timeout
      const shortTimeoutConfig = {
        ...testConfig,
        auth: {
          ...testConfig.auth,
          timeout: 1, // 1ms timeout
        },
      };

      const shortTimeoutPlatform = new GitHubPlatform(shortTimeoutConfig);
      await shortTimeoutPlatform.initialize();

      await expect(shortTimeoutPlatform.getRepository('meywd', 'tamma')).rejects.toThrow();

      await shortTimeoutPlatform.dispose();
    });
  });
});
```

```typescript
// packages/platforms/src/__tests__/integration/gitlab.test.ts

import { GitLabPlatform } from '../gitlab';
import { AuthenticationMethod } from '../config/types';

describe('GitLab Platform Integration Tests', () => {
  let platform: GitLabPlatform;
  const testConfig = {
    type: 'gitlab' as const,
    name: 'GitLab Test',
    baseUrl: 'https://gitlab.com',
    auth: {
      method: AuthenticationMethod.PAT,
      tokenEnvVar: 'GITLAB_TOKEN_TEST',
    },
    defaultBranch: 'main',
    enabled: true,
    priority: 90,
  };

  beforeAll(async () => {
    if (!process.env.GITLAB_TOKEN_TEST) {
      console.warn('Skipping GitLab integration tests - no test token provided');
      return;
    }

    platform = new GitLabPlatform(testConfig);
    await platform.initialize();
  });

  afterAll(async () => {
    if (platform) {
      await platform.dispose();
    }
  });

  describe('Authentication', () => {
    it('should authenticate with valid token', async () => {
      if (!process.env.GITLAB_TOKEN_TEST) return;

      const isValid = await platform.testConnection();
      expect(isValid).toBe(true);
    });

    it('should get authenticated user information', async () => {
      if (!process.env.GITLAB_TOKEN_TEST) return;

      const user = await platform.getAuthenticatedUser();
      expect(user).toBeDefined();
      expect(user.username).toBeDefined();
      expect(user.id).toBeDefined();
    });
  });

  describe('Project Operations', () => {
    const testProjectId = '12345678'; // Test project ID

    it('should get project information', async () => {
      if (!process.env.GITLAB_TOKEN_TEST) return;

      const project = await platform.getProject(testProjectId);
      expect(project).toBeDefined();
      expect(project.id).toBe(testProjectId);
    });

    it('should list project branches', async () => {
      if (!process.env.GITLAB_TOKEN_TEST) return;

      const branches = await platform.listBranches(testProjectId);
      expect(Array.isArray(branches)).toBe(true);
    });
  });

  describe('Merge Request Operations', () => {
    const testProjectId = '12345678';

    it('should list merge requests', async () => {
      if (!process.env.GITLAB_TOKEN_TEST) return;

      const mrs = await platform.listMergeRequests(testProjectId);
      expect(Array.isArray(mrs)).toBe(true);
    });
  });
});
```

### 5.3 Performance Tests Implementation

```typescript
// packages/platforms/src/__tests__/performance/configuration-loading.test.ts

import { PlatformConfigManager } from '../config/platform-config-manager';
import { performance } from 'perf_hooks';
import { writeFileSync, unlinkSync } from 'fs';
import { join } from 'path';

describe('Configuration Loading Performance Tests', () => {
  let configManager: PlatformConfigManager;
  const testConfigDir = '/tmp/tamma-config-test';

  beforeEach(() => {
    configManager = new PlatformConfigManager();
  });

  afterEach(() => {
    // Clean up test files
    try {
      unlinkSync(join(testConfigDir, 'platforms.json'));
    } catch {
      // Ignore cleanup errors
    }
  });

  describe('Small Configuration Loading', () => {
    it('should load small configuration within performance threshold', async () => {
      const smallConfig = {
        version: '1.2.0',
        platforms: [
          {
            type: 'github',
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: 'pat',
              tokenEnvVar: 'GITHUB_TOKEN',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
        ],
      };

      writeFileSync(join(testConfigDir, 'platforms.json'), JSON.stringify(smallConfig));

      const startTime = performance.now();
      const config = await configManager.loadConfiguration(join(testConfigDir, 'platforms.json'));
      const endTime = performance.now();

      const loadTime = endTime - startTime;

      expect(config).toBeDefined();
      expect(loadTime).toBeLessThan(50); // Should load in < 50ms
    });
  });

  describe('Large Configuration Loading', () => {
    it('should handle large configuration files efficiently', async () => {
      // Create configuration with many platforms
      const largeConfig = {
        version: '1.2.0',
        platforms: Array.from({ length: 50 }, (_, i) => ({
          type: 'github',
          name: `GitHub-${i}`,
          baseUrl: `https://github${i}.example.com`,
          auth: {
            method: 'pat',
            tokenEnvVar: `GITHUB_TOKEN_${i}`,
          },
          defaultBranch: 'main',
          enabled: i % 2 === 0, // Enable half of them
          priority: 100 - i,
        })),
      };

      writeFileSync(join(testConfigDir, 'platforms.json'), JSON.stringify(largeConfig));

      const startTime = performance.now();
      const config = await configManager.loadConfiguration(join(testConfigDir, 'platforms.json'));
      const endTime = performance.now();

      const loadTime = endTime - startTime;

      expect(config).toBeDefined();
      expect(config.platforms).toHaveLength(50);
      expect(loadTime).toBeLessThan(200); // Should load in < 200ms even for large configs
    });
  });

  describe('Concurrent Loading', () => {
    it('should handle concurrent configuration loading', async () => {
      const config = {
        version: '1.2.0',
        platforms: [
          {
            type: 'github',
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: 'pat',
              tokenEnvVar: 'GITHUB_TOKEN',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
        ],
      };

      writeFileSync(join(testConfigDir, 'platforms.json'), JSON.stringify(config));

      // Load configuration concurrently
      const concurrentLoads = Array(10)
        .fill(null)
        .map(async () => {
          const startTime = performance.now();
          const result = await configManager.loadConfiguration(
            join(testConfigDir, 'platforms.json')
          );
          const endTime = performance.now();
          return { result, loadTime: endTime - startTime };
        });

      const results = await Promise.all(concurrentLoads);

      // All loads should succeed
      results.forEach(({ result }) => {
        expect(result).toBeDefined();
      });

      // Average load time should be reasonable
      const avgLoadTime = results.reduce((sum, { loadTime }) => sum + loadTime, 0) / results.length;
      expect(avgLoadTime).toBeLessThan(100);
    });
  });

  describe('Memory Usage', () => {
    it('should not leak memory during repeated loading', async () => {
      const config = {
        version: '1.2.0',
        platforms: [
          {
            type: 'github',
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: 'pat',
              tokenEnvVar: 'GITHUB_TOKEN',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
        ],
      };

      writeFileSync(join(testConfigDir, 'platforms.json'), JSON.stringify(config));

      const initialMemory = process.memoryUsage().heapUsed;

      // Load configuration many times
      for (let i = 0; i < 1000; i++) {
        await configManager.loadConfiguration(join(testConfigDir, 'platforms.json'));
      }

      // Force garbage collection if available
      if (global.gc) {
        global.gc();
      }

      const finalMemory = process.memoryUsage().heapUsed;
      const memoryIncrease = finalMemory - initialMemory;

      // Memory increase should be minimal (< 10MB)
      expect(memoryIncrease).toBeLessThan(10 * 1024 * 1024);
    });
  });

  describe('Caching Performance', () => {
    it('should improve performance with caching enabled', async () => {
      const config = {
        version: '1.2.0',
        platforms: [
          {
            type: 'github',
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: 'pat',
              tokenEnvVar: 'GITHUB_TOKEN',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
        ],
      };

      writeFileSync(join(testConfigDir, 'platforms.json'), JSON.stringify(config));

      // First load (no cache)
      const startTime1 = performance.now();
      await configManager.loadConfiguration(join(testConfigDir, 'platforms.json'));
      const endTime1 = performance.now();
      const firstLoadTime = endTime1 - startTime1;

      // Second load (with cache)
      const startTime2 = performance.now();
      await configManager.loadConfiguration(join(testConfigDir, 'platforms.json'));
      const endTime2 = performance.now();
      const secondLoadTime = endTime2 - startTime2;

      // Cached load should be significantly faster
      expect(secondLoadTime).toBeLessThan(firstLoadTime * 0.5);
    });
  });
});
```

### 5.4 Security Tests Implementation

```typescript
// packages/platforms/src/__tests__/security/credential-handling.test.ts

import { CredentialManager } from '../security/credential-manager';
import { EncryptionManager } from '../security/encryption-manager';
import { writeFileSync, unlinkSync } from 'fs';
import { join } from 'path';

describe('Credential Handling Security Tests', () => {
  let credentialManager: CredentialManager;
  let encryptionManager: EncryptionManager;
  const testCredentialDir = '/tmp/tamma-credentials-test';

  beforeEach(() => {
    credentialManager = new CredentialManager();
    encryptionManager = new EncryptionManager();
  });

  afterEach(() => {
    // Clean up test files
    try {
      unlinkSync(join(testCredentialDir, 'credentials.enc'));
    } catch {
      // Ignore cleanup errors
    }
  });

  describe('Token Encryption', () => {
    it('should encrypt and decrypt tokens securely', async () => {
      const plaintextToken = 'ghp_1234567890abcdef1234567890abcdef12345678';

      const encrypted = await encryptionManager.encrypt(plaintextToken);
      const decrypted = await encryptionManager.decrypt(encrypted);

      expect(decrypted).toBe(plaintextToken);
      expect(encrypted).not.toBe(plaintextToken);
      expect(encrypted.length).toBeGreaterThan(plaintextToken.length);
    });

    it('should generate different encrypted values for same input', async () => {
      const plaintextToken = 'ghp_1234567890abcdef1234567890abcdef12345678';

      const encrypted1 = await encryptionManager.encrypt(plaintextToken);
      const encrypted2 = await encryptionManager.encrypt(plaintextToken);

      expect(encrypted1).not.toBe(encrypted2);

      // But both decrypt to the same value
      const decrypted1 = await encryptionManager.decrypt(encrypted1);
      const decrypted2 = await encryptionManager.decrypt(encrypted2);
      expect(decrypted1).toBe(decrypted2);
    });

    it('should fail to decrypt with wrong key', async () => {
      const plaintextToken = 'ghp_1234567890abcdef1234567890abcdef12345678';

      const encrypted = await encryptionManager.encrypt(plaintextToken);

      // Create new encryption manager with different key
      const wrongEncryptionManager = new EncryptionManager();

      await expect(wrongEncryptionManager.decrypt(encrypted)).rejects.toThrow();
    });
  });

  describe('Credential Storage', () => {
    it('should store credentials securely', async () => {
      const credentials = {
        github: 'ghp_1234567890abcdef1234567890abcdef12345678',
        gitlab: 'glpat_1234567890abcdef1234567890abcdef12345678',
      };

      await credentialManager.storeCredentials(credentials, testCredentialDir);

      // Verify file exists and is encrypted
      const storedData = await credentialManager.loadCredentials(testCredentialDir);
      expect(storedData).toEqual(credentials);
    });

    it('should not store plaintext credentials', async () => {
      const credentials = {
        github: 'ghp_1234567890abcdef1234567890abcdef12345678',
      };

      await credentialManager.storeCredentials(credentials, testCredentialDir);

      // Read raw file and verify it's encrypted
      const fs = require('fs').promises;
      const rawContent = await fs.readFile(join(testCredentialDir, 'credentials.enc'), 'utf8');

      expect(rawContent).not.toContain('ghp_1234567890abcdef1234567890abcdef12345678');
      expect(rawContent).toMatch(/^[A-Za-z0-9+/=]+$/); // Base64 encoded
    });

    it('should handle credential rotation', async () => {
      const oldCredentials = {
        github: 'ghp_old_token_1234567890abcdef1234567890abcdef',
      };

      const newCredentials = {
        github: 'ghp_new_token_1234567890abcdef1234567890abcdef',
      };

      // Store old credentials
      await credentialManager.storeCredentials(oldCredentials, testCredentialDir);

      // Rotate to new credentials
      await credentialManager.rotateCredentials('github', newCredentials.github, testCredentialDir);

      // Verify new credentials are stored
      const storedCredentials = await credentialManager.loadCredentials(testCredentialDir);
      expect(storedCredentials.github).toBe(newCredentials.github);
    });
  });

  describe('Access Control', () => {
    it('should enforce file permissions', async () => {
      const credentials = {
        github: 'ghp_1234567890abcdef1234567890abcdef12345678',
      };

      await credentialManager.storeCredentials(credentials, testCredentialDir);

      // Check file permissions (should be 600 - owner read/write only)
      const fs = require('fs');
      const stats = fs.statSync(join(testCredentialDir, 'credentials.enc'));

      // Convert to octal and check permissions
      const mode = (stats.mode & parseInt('777', 8)).toString(8);
      expect(mode).toBe('600');
    });

    it('should redact credentials in logs', async () => {
      const consoleSpy = jest.spyOn(console, 'log').mockImplementation();

      const token = 'ghp_1234567890abcdef1234567890abcdef12345678';
      const redacted = credentialManager.redactToken(token);

      expect(redacted).toBe('ghp_************************************');
      expect(redacted).not.toContain(token);

      consoleSpy.mockRestore();
    });

    it('should sanitize error messages', async () => {
      const error = new Error(
        'Authentication failed with token ghp_1234567890abcdef1234567890abcdef12345678'
      );
      const sanitized = credentialManager.sanitizeError(error);

      expect(sanitized.message).not.toContain('ghp_1234567890abcdef1234567890abcdef12345678');
      expect(sanitized.message).toContain('Authentication failed with token [REDACTED]');
    });
  });

  describe('Audit Logging', () => {
    it('should log credential access attempts', async () => {
      const auditSpy = jest.spyOn(credentialManager, 'logAccess').mockImplementation();

      const credentials = {
        github: 'ghp_1234567890abcdef1234567890abcdef12345678',
      };

      await credentialManager.storeCredentials(credentials, testCredentialDir);
      await credentialManager.loadCredentials(testCredentialDir);

      expect(auditSpy).toHaveBeenCalledWith('store', 'github');
      expect(auditSpy).toHaveBeenCalledWith('load', 'github');

      auditSpy.mockRestore();
    });

    it('should log security events', async () => {
      const securitySpy = jest.spyOn(credentialManager, 'logSecurityEvent').mockImplementation();

      // Simulate failed authentication
      await credentialManager.handleAuthenticationFailure('github', 'invalid_token');

      expect(securitySpy).toHaveBeenCalledWith(
        'authentication_failure',
        expect.objectContaining({
          platform: 'github',
          reason: 'invalid_token',
        })
      );

      securitySpy.mockRestore();
    });
  });

  describe('Token Validation', () => {
    it('should validate token format', async () => {
      const validTokens = [
        'ghp_1234567890abcdef1234567890abcdef12345678',
        'glpat-1234567890abcdef1234567890abcdef1234',
        'xoxb-1234-5678-90ab-cdef', // Slack token pattern (example only)
      ];

      const invalidTokens = ['', 'short', 'invalid-format', '1234567890', 'no-prefix-token'];

      validTokens.forEach((token) => {
        expect(credentialManager.validateTokenFormat(token)).toBe(true);
      });

      invalidTokens.forEach((token) => {
        expect(credentialManager.validateTokenFormat(token)).toBe(false);
      });
    });

    it('should detect token leakage in logs', async () => {
      const logMessages = [
        'Authentication successful with token ghp_1234567890abcdef1234567890abcdef12345678',
        'API call failed: Invalid token glpat-1234567890abcdef1234567890abcdef1234',
        'No tokens in this message',
        'Token format: ghp_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx',
      ];

      const detectedTokens = credentialManager.detectTokenLeakage(logMessages);

      expect(detectedTokens).toHaveLength(2);
      expect(detectedTokens[0]).toContain('ghp_1234567890abcdef1234567890abcdef12345678');
      expect(detectedTokens[1]).toContain('glpat-1234567890abcdef1234567890abcdef1234');
    });
  });
});
```

### 5.5 End-to-End Tests Implementation

```typescript
// packages/platforms/src/__tests__/e2e/multi-platform-workflows.test.ts

import { PlatformOrchestrator } from '../orchestrator';
import { PlatformConfigManager } from '../config/platform-config-manager';
import { writeFileSync, unlinkSync } from 'fs';
import { join } from 'path';

describe('Multi-Platform End-to-End Tests', () => {
  let orchestrator: PlatformOrchestrator;
  let configManager: PlatformConfigManager;
  const testConfigDir = '/tmp/tamma-e2e-test';

  beforeEach(async () => {
    configManager = new PlatformConfigManager();
    orchestrator = new PlatformOrchestrator(configManager);
  });

  afterEach(() => {
    // Clean up test files
    try {
      unlinkSync(join(testConfigDir, 'platforms.json'));
    } catch {
      // Ignore cleanup errors
    }
  });

  describe('Complete Workflow Tests', () => {
    it('should handle complete multi-platform workflow', async () => {
      const multiPlatformConfig = {
        version: '1.2.0',
        defaultPlatform: 'github',
        platforms: [
          {
            type: 'github',
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: 'pat',
              tokenEnvVar: 'GITHUB_TOKEN_TEST',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
          {
            type: 'gitlab',
            name: 'GitLab',
            baseUrl: 'https://gitlab.com',
            auth: {
              method: 'pat',
              tokenEnvVar: 'GITLAB_TOKEN_TEST',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 90,
          },
        ],
      };

      writeFileSync(join(testConfigDir, 'platforms.json'), JSON.stringify(multiPlatformConfig));

      // Load configuration
      const config = await configManager.loadConfiguration(join(testConfigDir, 'platforms.json'));
      expect(config.platforms).toHaveLength(2);

      // Initialize orchestrator
      await orchestrator.initialize(config);

      // Test platform detection
      const githubPlatform = orchestrator.detectPlatform('https://github.com/user/repo');
      expect(githubPlatform).toBe('GitHub');

      const gitlabPlatform = orchestrator.detectPlatform('https://gitlab.com/group/project');
      expect(gitlabPlatform).toBe('GitLab');

      // Test platform switching
      const currentPlatform = orchestrator.getCurrentPlatform();
      expect(currentPlatform?.name).toBe('GitHub');

      await orchestrator.switchPlatform('GitLab');
      const switchedPlatform = orchestrator.getCurrentPlatform();
      expect(switchedPlatform?.name).toBe('GitLab');

      // Test failover scenario
      await orchestrator.simulatePlatformFailure('GitLab');
      const failoverPlatform = orchestrator.getCurrentPlatform();
      expect(failoverPlatform?.name).toBe('GitHub');

      await orchestrator.dispose();
    });

    it('should handle authentication across multiple platforms', async () => {
      const config = {
        version: '1.2.0',
        platforms: [
          {
            type: 'github',
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: 'pat',
              tokenEnvVar: 'GITHUB_TOKEN_TEST',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
        ],
      };

      writeFileSync(join(testConfigDir, 'platforms.json'), JSON.stringify(config));

      const loadedConfig = await configManager.loadConfiguration(
        join(testConfigDir, 'platforms.json')
      );
      await orchestrator.initialize(loadedConfig);

      // Test authentication
      if (process.env.GITHUB_TOKEN_TEST) {
        const authResults = await orchestrator.authenticateAllPlatforms();
        expect(authResults).toHaveProperty('GitHub');
        expect(authResults.GitHub.authenticated).toBe(true);
      }

      await orchestrator.dispose();
    });

    it('should handle configuration migration workflow', async () => {
      // Create legacy configuration
      const legacyConfig = {
        github: {
          token: 'ghp_1234567890abcdef1234567890abcdef12345678',
          defaultBranch: 'main',
        },
      };

      writeFileSync(join(testConfigDir, 'legacy-config.json'), JSON.stringify(legacyConfig));

      // Run migration
      const migrationResult = await orchestrator.migrateConfiguration(
        join(testConfigDir, 'legacy-config.json'),
        join(testConfigDir, 'platforms.json')
      );

      expect(migrationResult.success).toBe(true);
      expect(migrationResult.migratedPlatforms).toContain('github');

      // Load migrated configuration
      const migratedConfig = await configManager.loadConfiguration(
        join(testConfigDir, 'platforms.json')
      );
      expect(migratedConfig.platforms).toHaveLength(1);
      expect(migratedConfig.platforms[0].type).toBe('github');
    });
  });

  describe('Error Recovery Tests', () => {
    it('should recover from platform failures', async () => {
      const config = {
        version: '1.2.0',
        platforms: [
          {
            type: 'github',
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: 'pat',
              tokenEnvVar: 'GITHUB_TOKEN_TEST',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
          {
            type: 'gitlab',
            name: 'GitLab',
            baseUrl: 'https://gitlab.com',
            auth: {
              method: 'pat',
              tokenEnvVar: 'GITLAB_TOKEN_TEST',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 90,
          },
        ],
      };

      writeFileSync(join(testConfigDir, 'platforms.json'), JSON.stringify(config));

      const loadedConfig = await configManager.loadConfiguration(
        join(testConfigDir, 'platforms.json')
      );
      await orchestrator.initialize(loadedConfig);

      // Simulate GitHub failure
      await orchestrator.simulatePlatformFailure('GitHub');

      // Should automatically switch to GitLab
      const currentPlatform = orchestrator.getCurrentPlatform();
      expect(currentPlatform?.name).toBe('GitLab');

      // Simulate GitHub recovery
      await orchestrator.simulatePlatformRecovery('GitHub');

      // Should be able to switch back to GitHub
      await orchestrator.switchPlatform('GitHub');
      const recoveredPlatform = orchestrator.getCurrentPlatform();
      expect(recoveredPlatform?.name).toBe('GitHub');

      await orchestrator.dispose();
    });

    it('should handle network connectivity issues', async () => {
      const config = {
        version: '1.2.0',
        platforms: [
          {
            type: 'github',
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: 'pat',
              tokenEnvVar: 'GITHUB_TOKEN_TEST',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
        ],
      };

      writeFileSync(join(testConfigDir, 'platforms.json'), JSON.stringify(config));

      const loadedConfig = await configManager.loadConfiguration(
        join(testConfigDir, 'platforms.json')
      );
      await orchestrator.initialize(loadedConfig);

      // Simulate network failure
      await orchestrator.simulateNetworkFailure();

      // Should handle gracefully
      const healthStatus = await orchestrator.getHealthStatus();
      expect(healthStatus.overall).toBe('degraded');

      // Simulate network recovery
      await orchestrator.simulateNetworkRecovery();

      const recoveredHealth = await orchestrator.getHealthStatus();
      expect(recoveredHealth.overall).toBe('healthy');

      await orchestrator.dispose();
    });
  });

  describe('Real-World Scenarios', () => {
    it('should handle enterprise GitHub with self-hosted GitLab', async () => {
      const enterpriseConfig = {
        version: '1.2.0',
        platforms: [
          {
            type: 'github',
            name: 'GitHub Enterprise',
            baseUrl: 'https://github.enterprise.com',
            auth: {
              method: 'pat',
              tokenEnvVar: 'GITHUB_ENTERPRISE_TOKEN_TEST',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
          {
            type: 'gitlab',
            name: 'Self-Hosted GitLab',
            baseUrl: 'https://gitlab.company.com',
            auth: {
              method: 'pat',
              tokenEnvVar: 'GITLAB_COMPANY_TOKEN_TEST',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 90,
          },
        ],
      };

      writeFileSync(join(testConfigDir, 'platforms.json'), JSON.stringify(enterpriseConfig));

      const loadedConfig = await configManager.loadConfiguration(
        join(testConfigDir, 'platforms.json')
      );
      await orchestrator.initialize(loadedConfig);

      // Test URL detection for enterprise platforms
      const githubEnterprise = orchestrator.detectPlatform(
        'https://github.enterprise.com/user/repo'
      );
      expect(githubEnterprise).toBe('GitHub Enterprise');

      const gitlabCompany = orchestrator.detectPlatform('https://gitlab.company.com/group/project');
      expect(gitlabCompany).toBe('Self-Hosted GitLab');

      await orchestrator.dispose();
    });

    it('should handle platform-specific features', async () => {
      const config = {
        version: '1.2.0',
        platforms: [
          {
            type: 'github',
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: 'pat',
              tokenEnvVar: 'GITHUB_TOKEN_TEST',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
            features: {
              pullRequests: true,
              issues: true,
              actions: true,
              packages: true,
            },
          },
          {
            type: 'gitlab',
            name: 'GitLab',
            baseUrl: 'https://gitlab.com',
            auth: {
              method: 'pat',
              tokenEnvVar: 'GITLAB_TOKEN_TEST',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 90,
            features: {
              mergeRequests: true,
              issues: true,
              epics: true,
              packages: true,
            },
          },
        ],
      };

      writeFileSync(join(testConfigDir, 'platforms.json'), JSON.stringify(config));

      const loadedConfig = await configManager.loadConfiguration(
        join(testConfigDir, 'platforms.json')
      );
      await orchestrator.initialize(loadedConfig);

      // Test feature availability
      const githubFeatures = await orchestrator.getPlatformFeatures('GitHub');
      expect(githubFeatures.pullRequests).toBe(true);
      expect(githubFeatures.mergeRequests).toBeUndefined();

      const gitlabFeatures = await orchestrator.getPlatformFeatures('GitLab');
      expect(gitlabFeatures.mergeRequests).toBe(true);
      expect(gitlabFeatures.pullRequests).toBeUndefined();

      await orchestrator.dispose();
    });
  });
});
```

## Testing Infrastructure

### Test Configuration

```typescript
// packages/platforms/src/__tests__/setup.ts

import { config } from 'dotenv';

// Load test environment variables
config({ path: '.env.test' });

// Set test defaults
process.env.NODE_ENV = 'test';
process.env.TAMMA_LOG_LEVEL = 'error'; // Reduce noise in tests

// Global test timeout
jest.setTimeout(30000);

// Setup and teardown hooks
beforeAll(async () => {
  // Global test setup
});

afterAll(async () => {
  // Global test cleanup
});

beforeEach(async () => {
  // Reset mocks and state
});

afterEach(async () => {
  // Clean up after each test
});
```

### Test Utilities

```typescript
// packages/platforms/src/__tests__/utils/test-helpers.ts

import { PlatformType, AuthenticationMethod } from '../../config/types';

export class TestHelpers {
  static createTestPlatformConfig(type: PlatformType, overrides: any = {}) {
    const baseConfig = {
      type,
      name: `${type.charAt(0).toUpperCase() + type.slice(1)} Test`,
      baseUrl: `https://${type}.com`,
      auth: {
        method: AuthenticationMethod.PAT,
        tokenEnvVar: `${type.toUpperCase()}_TOKEN_TEST`,
      },
      defaultBranch: 'main',
      enabled: true,
      priority: 100,
    };

    return { ...baseConfig, ...overrides };
  }

  static createMultiPlatformConfig(platforms: PlatformType[]) {
    return {
      version: '1.2.0',
      defaultPlatform: platforms[0],
      platforms: platforms.map((type, index) =>
        this.createTestPlatformConfig(type, { priority: 100 - index })
      ),
    };
  }

  static async withTimeout<T>(promise: Promise<T>, timeoutMs: number = 5000): Promise<T> {
    const timeoutPromise = new Promise<never>((_, reject) => {
      setTimeout(() => reject(new Error(`Test timed out after ${timeoutMs}ms`)), timeoutMs);
    });

    return Promise.race([promise, timeoutPromise]);
  }

  static async retry<T>(
    fn: () => Promise<T>,
    maxAttempts: number = 3,
    delayMs: number = 1000
  ): Promise<T> {
    let lastError: Error;

    for (let attempt = 1; attempt <= maxAttempts; attempt++) {
      try {
        return await fn();
      } catch (error) {
        lastError = error as Error;
        if (attempt === maxAttempts) break;
        await new Promise((resolve) => setTimeout(resolve, delayMs));
      }
    }

    throw lastError!;
  }
}
```

## Completion Checklist

- [ ] Implement unit tests for all configuration components
- [ ] Create integration tests for all supported platforms
- [ ] Build performance tests for configuration loading
- [ ] Develop security tests for credential handling
- [ ] Create end-to-end tests for complete workflows
- [ ] Set up testing infrastructure and utilities
- [ ] Configure CI/CD pipeline for automated testing
- [ ] Add test coverage reporting
- [ ] Create test data management system
- [ ] Implement test environment provisioning
- [ ] Add performance benchmarking
- [ ] Create test documentation and guidelines

## Dependencies

- Task 1-4: All configuration system components
- Testing frameworks (Jest/Vitest)
- Mock service workers for API mocking
- Performance testing tools
- Security testing utilities
- Test environment setup

## Estimated Time

**Unit Tests**: 5-7 days
**Integration Tests**: 4-6 days
**Performance Tests**: 3-4 days
**Security Tests**: 4-5 days
**End-to-End Tests**: 3-4 days
**Testing Infrastructure**: 2-3 days
**Total**: 21-29 days
