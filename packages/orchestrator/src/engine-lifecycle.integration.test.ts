import { describe, it, expect, vi } from 'vitest';
import { TammaEngine } from './engine.js';
import { EngineState, EngineEventType, InMemoryEventStore, DEFAULT_TENANT_ID } from '@tamma/shared';
import type { TammaConfig } from '@tamma/shared';
import type { IAgentProvider } from '@tamma/providers';
import type { IGitPlatform } from '@tamma/platforms';
import type { ILogger } from '@tamma/shared/contracts';

function createIntegrationConfig(): TammaConfig {
  return {
    mode: 'standalone',
    logLevel: 'debug',
    github: {
      authMode: 'pat' as const,
      token: 'test-token',
      owner: 'test-owner',
      repo: 'test-repo',
      issueLabels: ['tamma'],
      excludeLabels: ['wontfix'],
      botUsername: 'tamma-bot',
    },
    agent: {
      model: 'sonnet',
      maxBudgetUsd: 1.0,
      allowedTools: ['Read', 'Write'],
      permissionMode: 'bypassPermissions',
    },
    engine: {
      pollIntervalMs: 100,
      workingDirectory: '/tmp/test',
      approvalMode: 'auto',
      ciPollIntervalMs: 100,
      ciMonitorTimeoutMs: 5000,
    },
  };
}

function createMockLogger(): ILogger {
  return { debug: vi.fn(), info: vi.fn(), warn: vi.fn(), error: vi.fn() };
}

describe('TammaEngine Integration', () => {
  it('should process a full issue lifecycle with mocked dependencies', async () => {
    const config = createIntegrationConfig();
    const logger = createMockLogger();
    const eventStore = new InMemoryEventStore();

    const mockAgent: IAgentProvider = {
      executeTask: vi.fn()
        .mockResolvedValueOnce({
          success: true,
          output: JSON.stringify({
            issueNumber: 1,
            summary: 'Add greeting feature',
            approach: 'Create greeting.ts',
            fileChanges: [{ filePath: 'src/greeting.ts', action: 'create', description: 'New greeting module' }],
            testingStrategy: 'Unit tests',
            estimatedComplexity: 'low',
            risks: [],
          }),
          costUsd: 0.02,
          durationMs: 500,
        })
        .mockResolvedValueOnce({
          success: true,
          output: 'Implementation complete',
          costUsd: 0.15,
          durationMs: 3000,
        }),
      isAvailable: vi.fn().mockResolvedValue(true),
      dispose: vi.fn().mockResolvedValue(undefined),
    };

    const mockPlatform: IGitPlatform = {
      platformName: 'github',
      initialize: vi.fn().mockResolvedValue(undefined),
      dispose: vi.fn().mockResolvedValue(undefined),
      getRepository: vi.fn().mockResolvedValue({
        owner: 'test-owner', name: 'test-repo', fullName: 'test-owner/test-repo',
        defaultBranch: 'main', url: 'https://github.com/test-owner/test-repo', isPrivate: false,
      }),
      getBranch: vi.fn().mockResolvedValue({ name: 'feature/1-test', sha: 'abc', isProtected: false }),
      createBranch: vi.fn().mockResolvedValue({ name: 'feature/1-test', sha: 'abc', isProtected: false }),
      deleteBranch: vi.fn().mockResolvedValue(undefined),
      createPR: vi.fn().mockResolvedValue({
        number: 10, title: 'feat: test', body: 'body', state: 'open', head: 'feature/1-test',
        base: 'main', url: 'https://github.com/test-owner/test-repo/pull/10',
        mergeable: true, labels: [], createdAt: '', updatedAt: '',
      }),
      getPR: vi.fn().mockResolvedValue({
        number: 10, state: 'open', head: 'feature/1-test', base: 'main',
        title: '', body: '', url: '', mergeable: true, labels: [], createdAt: '', updatedAt: '',
      }),
      updatePR: vi.fn().mockResolvedValue({}),
      mergePR: vi.fn().mockResolvedValue({ merged: true, sha: 'merge-sha', message: 'Merged' }),
      addPRComment: vi.fn().mockResolvedValue({ id: 1, author: 'bot', body: '', createdAt: '' }),
      getIssue: vi.fn().mockResolvedValue({
        number: 1, title: 'Add greeting', body: 'Please add a greeting feature',
        state: 'open', labels: ['tamma'], assignees: [],
        url: 'https://github.com/test-owner/test-repo/issues/1',
        createdAt: '2024-01-01T00:00:00Z', updatedAt: '2024-01-01T00:00:00Z', comments: [],
      }),
      listIssues: vi.fn().mockResolvedValue({
        data: [{
          number: 1, title: 'Add greeting', body: 'Please add a greeting feature',
          state: 'open', labels: ['tamma'], assignees: [],
          url: 'https://github.com/test-owner/test-repo/issues/1',
          createdAt: '2024-01-01T00:00:00Z', updatedAt: '2024-01-01T00:00:00Z', comments: [],
        }],
        totalCount: 1, hasNextPage: false, page: 1,
      }),
      updateIssue: vi.fn().mockResolvedValue({}),
      addIssueComment: vi.fn().mockResolvedValue({ id: 2, author: 'bot', body: '', createdAt: '' }),
      assignIssue: vi.fn().mockResolvedValue({}),
      getCIStatus: vi.fn().mockResolvedValue({
        state: 'success', totalCount: 1, successCount: 1, failureCount: 0, pendingCount: 0,
      }),
      listCommits: vi.fn().mockResolvedValue([
        { sha: 'abc1234567890', message: 'initial commit', author: 'dev', date: '2024-01-01T00:00:00Z' },
      ]),
    };

    const engine = new TammaEngine({
      config, platform: mockPlatform, agent: mockAgent, logger, eventStore,
    });

    await engine.initialize();
    await engine.processOneIssue();

    // Verify the full pipeline executed
    expect(mockPlatform.listIssues).toHaveBeenCalled();
    expect(mockPlatform.assignIssue).toHaveBeenCalled();
    expect(mockAgent.executeTask).toHaveBeenCalledTimes(2);
    expect(mockPlatform.createBranch).toHaveBeenCalled();
    expect(mockPlatform.createPR).toHaveBeenCalled();
    expect(mockPlatform.mergePR).toHaveBeenCalled();
    expect(mockPlatform.deleteBranch).toHaveBeenCalled();
    expect(mockPlatform.updateIssue).toHaveBeenCalledWith(
      'test-owner', 'test-repo', 1, expect.objectContaining({ state: 'closed' }),
    );

    // Verify event store recorded the full lifecycle
    const events = eventStore.getEvents(DEFAULT_TENANT_ID, 1);
    const eventTypes = events.map((e) => e.type);
    expect(eventTypes).toContain(EngineEventType.ISSUE_SELECTED);
    expect(eventTypes).toContain(EngineEventType.ISSUE_ANALYZED);
    expect(eventTypes).toContain(EngineEventType.PLAN_GENERATED);
    expect(eventTypes).toContain(EngineEventType.PLAN_APPROVED);
    expect(eventTypes).toContain(EngineEventType.BRANCH_CREATED);
    expect(eventTypes).toContain(EngineEventType.IMPLEMENTATION_STARTED);
    expect(eventTypes).toContain(EngineEventType.IMPLEMENTATION_COMPLETED);
    expect(eventTypes).toContain(EngineEventType.PR_CREATED);
    expect(eventTypes).toContain(EngineEventType.PR_MERGED);
    expect(eventTypes).toContain(EngineEventType.ISSUE_CLOSED);

    // Verify state machine ended correctly
    expect(engine.getState()).toBe(EngineState.MERGING);

    await engine.dispose();
  });
});
