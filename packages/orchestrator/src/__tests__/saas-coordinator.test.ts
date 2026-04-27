import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { SaaSCoordinator } from '../saas-coordinator.js';
import type { SaaSCoordinatorOptions, ICoordinatorInstallationStore } from '../saas-coordinator.js';
import type { TammaConfig } from '@tamma/shared';
import type { ILogger } from '@tamma/shared/contracts';

// Mock createGitHubPlatformForInstallation
const mockDispose = vi.fn().mockResolvedValue(undefined);
const mockInitialize = vi.fn().mockResolvedValue(undefined);
vi.mock('@tamma/platforms', () => ({
  createGitHubPlatformForInstallation: vi.fn(async () => ({
    platformName: 'github',
    initialize: mockInitialize,
    dispose: mockDispose,
  })),
}));

// Mock TammaEngine
const mockEngineInitialize = vi.fn().mockResolvedValue(undefined);
const mockEngineDispose = vi.fn().mockResolvedValue(undefined);
const mockEngineRun = vi.fn().mockResolvedValue(undefined);
vi.mock('../engine.js', () => ({
  // Vitest 4: constructor mocks must use `function` keyword, not arrow
  TammaEngine: vi.fn(function () {
    return {
      initialize: mockEngineInitialize,
      dispose: mockEngineDispose,
      run: mockEngineRun,
    };
  }),
}));

function createMockLogger(): ILogger {
  return {
    debug: vi.fn(),
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    child: vi.fn().mockReturnThis(),
  };
}

function createBaseConfig(): TammaConfig {
  return {
    mode: 'standalone',
    logLevel: 'info',
    github: {
      authMode: 'saas' as const,
      appId: 3091468,
      privateKeyPath: '/path/to/key.pem',
      webhookSecret: 'whsec_test',
      owner: '',
      repo: '',
      issueLabels: ['tamma'],
      excludeLabels: ['wontfix'],
      botUsername: 'tamma-engine[bot]',
    },
    agent: {
      model: 'claude-sonnet-4-5',
      maxBudgetUsd: 1.0,
      allowedTools: ['Read', 'Write', 'Edit', 'Bash'],
      permissionMode: 'bypassPermissions',
    },
    engine: {
      pollIntervalMs: 300_000,
      workingDirectory: '/tmp/tamma',
      approvalMode: 'auto',
      ciPollIntervalMs: 30_000,
      ciMonitorTimeoutMs: 3_600_000,
    },
  };
}

describe('SaaSCoordinator', () => {
  let store: ICoordinatorInstallationStore;
  let logger: ILogger;
  let coordinator: SaaSCoordinator;

  beforeEach(() => {
    vi.clearAllMocks();

    store = {
      listAllActiveRepos: vi.fn().mockResolvedValue([]),
    };

    logger = createMockLogger();
  });

  afterEach(async () => {
    if (coordinator) {
      await coordinator.stop();
    }
  });

  function createCoordinator(overrides?: Partial<SaaSCoordinatorOptions>): SaaSCoordinator {
    coordinator = new SaaSCoordinator({
      baseConfig: createBaseConfig(),
      appCredentials: {
        appId: 3091468,
        privateKey: '-----BEGIN RSA PRIVATE KEY-----\nfake\n-----END RSA PRIVATE KEY-----',
      },
      installationStore: store,
      logger,
      createEngineContext: () => ({
        logger,
      }),
      pollIntervalMs: 60_000,
      ...overrides,
    });
    return coordinator;
  }

  it('should start with zero engines', () => {
    createCoordinator();
    expect(coordinator.engineCount).toBe(0);
    expect(coordinator.activeRepos).toEqual([]);
  });

  it('should spawn engines for active repos on start', async () => {
    vi.mocked(store.listAllActiveRepos).mockResolvedValue([
      { installationId: 100, owner: 'acme', name: 'web', fullName: 'acme/web' },
      { installationId: 100, owner: 'acme', name: 'api', fullName: 'acme/api' },
    ]);

    createCoordinator();
    await coordinator.start();

    expect(coordinator.engineCount).toBe(2);
    expect(coordinator.activeRepos).toContain('acme/web');
    expect(coordinator.activeRepos).toContain('acme/api');
    expect(mockEngineInitialize).toHaveBeenCalledTimes(2);
  });

  it('should prune engines when repos are removed', async () => {
    // Start with 2 repos
    vi.mocked(store.listAllActiveRepos).mockResolvedValue([
      { installationId: 100, owner: 'acme', name: 'web', fullName: 'acme/web' },
      { installationId: 100, owner: 'acme', name: 'api', fullName: 'acme/api' },
    ]);

    createCoordinator();
    await coordinator.start();
    expect(coordinator.engineCount).toBe(2);

    // Now simulate repo removal
    vi.mocked(store.listAllActiveRepos).mockResolvedValue([
      { installationId: 100, owner: 'acme', name: 'web', fullName: 'acme/web' },
    ]);

    // Directly trigger reconciliation instead of relying on timer
    // @ts-expect-error — accessing private for testing
    await coordinator.reconcile();

    expect(coordinator.engineCount).toBe(1);
    expect(coordinator.activeRepos).toEqual(['acme/web']);
    expect(mockEngineDispose).toHaveBeenCalled();
  });

  it('should dispose all engines on stop', async () => {
    vi.mocked(store.listAllActiveRepos).mockResolvedValue([
      { installationId: 100, owner: 'acme', name: 'web', fullName: 'acme/web' },
    ]);

    createCoordinator();
    await coordinator.start();
    expect(coordinator.engineCount).toBe(1);

    await coordinator.stop();
    expect(coordinator.engineCount).toBe(0);
    expect(mockEngineDispose).toHaveBeenCalled();
    expect(mockDispose).toHaveBeenCalled();
  });

  it('should not spawn duplicate engines for the same repo', async () => {
    vi.mocked(store.listAllActiveRepos).mockResolvedValue([
      { installationId: 100, owner: 'acme', name: 'web', fullName: 'acme/web' },
    ]);

    createCoordinator();
    await coordinator.start();

    // Manually trigger reconciliation again (simulating a poll)
    // @ts-expect-error — accessing private for testing
    await coordinator.reconcile();

    expect(coordinator.engineCount).toBe(1);
    expect(mockEngineInitialize).toHaveBeenCalledTimes(1);
  });

  it('should handle errors during engine spawn gracefully', async () => {
    mockEngineInitialize.mockRejectedValueOnce(new Error('init failed'));

    vi.mocked(store.listAllActiveRepos).mockResolvedValue([
      { installationId: 100, owner: 'acme', name: 'web', fullName: 'acme/web' },
    ]);

    createCoordinator();
    await coordinator.start();

    // Engine spawn failed, so count should be 0
    expect(coordinator.engineCount).toBe(0);
    expect(logger.error).toHaveBeenCalled();
  });
});
