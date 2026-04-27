import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// ---- Mocks ----
// vi.mock factories are hoisted — keep them fully self-contained.

// Track mock engine instance for assertions
const mockEngineInstance = {
  initialize: vi.fn(),
  processOneIssue: vi.fn(),
  dispose: vi.fn(),
  getStats: vi.fn().mockReturnValue({ issuesProcessed: 1, totalCostUsd: 0.5, startedAt: Date.now() }),
};

vi.mock('@tamma/orchestrator', () => ({
  // Vitest 4: constructor mocks must use `function` keyword, not arrow
  TammaEngine: vi.fn().mockImplementation(function () {
    return {
      initialize: vi.fn(),
      processOneIssue: vi.fn(),
      dispose: vi.fn(),
      getStats: vi.fn().mockReturnValue({ issuesProcessed: 1, totalCostUsd: 0.5, startedAt: Date.now() }),
    };
  }),
}));

vi.mock('@tamma/platforms', () => ({
  // Vitest 4: constructor mocks must use `function` keyword, not arrow
  GitHubPlatform: vi.fn().mockImplementation(function () {
    return {
      initialize: vi.fn(),
      dispose: vi.fn(),
    };
  }),
}));

vi.mock('@tamma/observability', () => ({
  createLogger: vi.fn().mockReturnValue({
    debug: vi.fn(),
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
  }),
}));

vi.mock('@tamma/shared', () => ({
  // Vitest 4: constructor mocks must use `function` keyword, not arrow
  DiagnosticsQueue: vi.fn().mockImplementation(function () {
    return {
      setProcessor: vi.fn(),
      dispose: vi.fn(),
    };
  }),
  ContentSanitizer: vi.fn(),
}));

vi.mock('@tamma/providers', () => ({
  // Vitest 4: constructor mocks must use `function` keyword, not arrow
  RoleBasedAgentResolver: vi.fn().mockImplementation(function () {
    return {};
  }),
  AgentProviderFactory: vi.fn(),
  ProviderHealthTracker: vi.fn(),
  AgentPromptRegistry: vi.fn(),
  createDiagnosticsProcessor: vi.fn(),
}));

vi.mock('@tamma/cost-monitor', () => ({
  createCostTracker: vi.fn().mockReturnValue({ dispose: vi.fn() }),
  FileStore: vi.fn(),
}));

vi.mock('../config.js', () => ({
  loadConfig: vi.fn().mockReturnValue({
    mode: 'standalone',
    logLevel: 'info',
    github: {
      authMode: 'pat',
      token: 'test-token',
      owner: 'test-owner',
      repo: 'test-repo',
      issueLabels: ['tamma'],
      excludeLabels: ['wontfix'],
      botUsername: 'tamma-bot',
    },
    agent: {
      model: 'claude-sonnet-4-5',
      maxBudgetUsd: 1.0,
      allowedTools: [],
      permissionMode: 'default',
    },
    engine: {
      pollIntervalMs: 300_000,
      workingDirectory: '.',
      approvalMode: 'cli',
      ciPollIntervalMs: 30_000,
      ciMonitorTimeoutMs: 3_600_000,
    },
  }),
  validateConfig: vi.fn().mockReturnValue([]),
  normalizeAgentsConfig: vi.fn().mockReturnValue({}),
  buildPlatformConfig: vi.fn().mockReturnValue({ type: 'pat', token: 'test-token' }),
}));

vi.mock('../error-handler.js', () => ({
  formatErrorWithSuggestions: vi.fn().mockImplementation((err: unknown) => ({
    message: err instanceof Error ? err.message : String(err),
    suggestions: ['Run with --verbose for details'],
  })),
}));

vi.mock('../worker/result-callback.js', () => ({
  // Vitest 4: constructor mocks must use `function` keyword, not arrow
  WorkerResultCallback: vi.fn().mockImplementation(function () {
    return {
      reportSuccess: vi.fn(),
      reportFailure: vi.fn(),
      reportStatus: vi.fn(),
    };
  }),
}));

// Import after mocks — use dynamic import so mocks are applied
const { processIssueCommand, EXIT_SUCCESS, EXIT_FAILURE, EXIT_SKIPPED } = await import('./process-issue.js');
const { loadConfig, validateConfig } = await import('../config.js');
const { TammaEngine } = await import('@tamma/orchestrator');

/**
 * Helper to get the mock engine instance that was created during the test.
 * Since TammaEngine is mocked, we can inspect the last instance returned.
 */
function getLastEngineInstance(): typeof mockEngineInstance {
  const engineMock = vi.mocked(TammaEngine);
  const lastCall = engineMock.mock.results[engineMock.mock.results.length - 1];
  return lastCall?.value as typeof mockEngineInstance;
}

describe('processIssueCommand', () => {
  const originalEnv = { ...process.env };

  beforeEach(() => {
    vi.clearAllMocks();
    // Reset env
    delete process.env['GITHUB_ACTIONS'];
    delete process.env['TAMMA_API_KEY'];
    delete process.env['TAMMA_API_URL'];
    delete process.env['TAMMA_WORKFLOW_ID'];
  });

  afterEach(() => {
    process.env = { ...originalEnv };
  });

  describe('exit codes', () => {
    it('should return EXIT_SUCCESS (0) on successful processing', async () => {
      const exitCode = await processIssueCommand({
        issue: 42,
        installationId: 'inst-123',
      });

      expect(exitCode).toBe(EXIT_SUCCESS);
      const engine = getLastEngineInstance();
      expect(engine.initialize).toHaveBeenCalled();
      expect(engine.processOneIssue).toHaveBeenCalled();
    });

    it('should return EXIT_FAILURE (1) on processing error', async () => {
      // Override the mock for this test: make processOneIssue reject
      // Vitest 4: constructor mocks must use `function` keyword, not arrow
      vi.mocked(TammaEngine).mockImplementationOnce(function () {
        const inst = {
          initialize: vi.fn().mockResolvedValue(undefined),
          processOneIssue: vi.fn().mockRejectedValue(new Error('Agent provider is not available')),
          dispose: vi.fn().mockResolvedValue(undefined),
          getStats: vi.fn().mockReturnValue({ issuesProcessed: 0, totalCostUsd: 0, startedAt: Date.now() }),
        };
        return inst as any;
      });

      const exitCode = await processIssueCommand({
        issue: 42,
        installationId: 'inst-123',
      });

      expect(exitCode).toBe(EXIT_FAILURE);
    });

    it('should return EXIT_SKIPPED (78) when no issues found', async () => {
      // Vitest 4: constructor mocks must use `function` keyword, not arrow
      vi.mocked(TammaEngine).mockImplementationOnce(function () {
        return {
          initialize: vi.fn().mockResolvedValue(undefined),
          processOneIssue: vi.fn().mockRejectedValue(new Error('No issues found matching labels')),
          dispose: vi.fn().mockResolvedValue(undefined),
          getStats: vi.fn().mockReturnValue({ issuesProcessed: 0, totalCostUsd: 0, startedAt: Date.now() }),
        } as any;
      });

      const exitCode = await processIssueCommand({
        issue: 42,
        installationId: 'inst-123',
      });

      expect(exitCode).toBe(EXIT_SKIPPED);
    });

    it('should return EXIT_SKIPPED (78) when issue is skipped', async () => {
      // Vitest 4: constructor mocks must use `function` keyword, not arrow
      vi.mocked(TammaEngine).mockImplementationOnce(function () {
        return {
          initialize: vi.fn().mockResolvedValue(undefined),
          processOneIssue: vi.fn().mockRejectedValue(new Error('Plan skipped by user')),
          dispose: vi.fn().mockResolvedValue(undefined),
          getStats: vi.fn().mockReturnValue({ issuesProcessed: 0, totalCostUsd: 0, startedAt: Date.now() }),
        } as any;
      });

      const exitCode = await processIssueCommand({
        issue: 42,
        installationId: 'inst-123',
      });

      expect(exitCode).toBe(EXIT_SKIPPED);
    });

    it('should return EXIT_FAILURE (1) on config validation errors', async () => {
      vi.mocked(validateConfig).mockReturnValueOnce(['GitHub token is required']);

      const exitCode = await processIssueCommand({
        issue: 42,
        installationId: 'inst-123',
      });

      expect(exitCode).toBe(EXIT_FAILURE);
    });
  });

  describe('engine initialization', () => {
    it('should call engine.initialize()', async () => {
      await processIssueCommand({
        issue: 42,
        installationId: 'inst-123',
      });

      const engine = getLastEngineInstance();
      expect(engine.initialize).toHaveBeenCalled();
    });

    it('should clean up resources even on failure', async () => {
      // Vitest 4: constructor mocks must use `function` keyword, not arrow
      vi.mocked(TammaEngine).mockImplementationOnce(function () {
        return {
          initialize: vi.fn().mockResolvedValue(undefined),
          processOneIssue: vi.fn().mockRejectedValue(new Error('boom')),
          dispose: vi.fn().mockResolvedValue(undefined),
          getStats: vi.fn().mockReturnValue({ issuesProcessed: 0, totalCostUsd: 0, startedAt: Date.now() }),
        } as any;
      });

      await processIssueCommand({
        issue: 42,
        installationId: 'inst-123',
      });

      const engine = getLastEngineInstance();
      expect(engine.dispose).toHaveBeenCalled();
    });

    it('should pass config option through to loadConfig', async () => {
      await processIssueCommand({
        issue: 42,
        installationId: 'inst-123',
        config: '/custom/path.json',
      });

      expect(loadConfig).toHaveBeenCalledWith({ config: '/custom/path.json' });
    });
  });

  describe('GitHub Actions integration', () => {
    it('should not write annotations when not in GitHub Actions', async () => {
      const consoleSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      await processIssueCommand({
        issue: 42,
        installationId: 'inst-123',
      });

      // Should not have any ::group:: output
      const groupCalls = consoleSpy.mock.calls.filter(
        (call) => typeof call[0] === 'string' && call[0].startsWith('::')
      );
      expect(groupCalls).toHaveLength(0);

      consoleSpy.mockRestore();
    });

    it('should write ::group:: annotations when in GitHub Actions', async () => {
      process.env['GITHUB_ACTIONS'] = 'true';
      const consoleSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      await processIssueCommand({
        issue: 42,
        installationId: 'inst-123',
      });

      const groupCalls = consoleSpy.mock.calls.filter(
        (call) => typeof call[0] === 'string' && call[0].startsWith('::group::')
      );
      expect(groupCalls.length).toBeGreaterThan(0);

      consoleSpy.mockRestore();
    });

    it('should write ::error:: annotation on processing failure in GitHub Actions', async () => {
      process.env['GITHUB_ACTIONS'] = 'true';

      // Vitest 4: constructor mocks must use `function` keyword, not arrow
      vi.mocked(TammaEngine).mockImplementationOnce(function () {
        return {
          initialize: vi.fn().mockResolvedValue(undefined),
          processOneIssue: vi.fn().mockRejectedValue(new Error('Agent crashed')),
          dispose: vi.fn().mockResolvedValue(undefined),
          getStats: vi.fn().mockReturnValue({ issuesProcessed: 0, totalCostUsd: 0, startedAt: Date.now() }),
        } as any;
      });

      const consoleSpy = vi.spyOn(console, 'log').mockImplementation(() => {});

      const exitCode = await processIssueCommand({
        issue: 42,
        installationId: 'inst-123',
      });

      expect(exitCode).toBe(EXIT_FAILURE);

      // The ::error:: annotation comes from the actionsError helper via the logger wrapper
      const errorCalls = consoleSpy.mock.calls.filter(
        (call) => typeof call[0] === 'string' && call[0].startsWith('::error::')
      );
      expect(errorCalls.length).toBeGreaterThan(0);

      consoleSpy.mockRestore();
    });
  });

  describe('constant values', () => {
    it('should export correct exit codes', () => {
      expect(EXIT_SUCCESS).toBe(0);
      expect(EXIT_FAILURE).toBe(1);
      expect(EXIT_SKIPPED).toBe(78);
    });
  });
});
