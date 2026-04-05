// Integration tests for TammaEngine with mock GitHub platform.
// Extracted from engine.e2e.test.ts — mock-agent tests that don't need real GitHub PAT.

import { describe, it, expect, vi } from 'vitest';
import { TammaEngine } from './engine.js';
import { EngineState } from '@tamma/shared';
import type { TammaConfig, AgentTaskResult } from '@tamma/shared';
import type { IAgentProvider } from '@tamma/providers';
import type { IGitPlatform } from '@tamma/platforms';
import type { ILogger } from '@tamma/shared/contracts';

const E2E_ENABLED = process.env['E2E_TEST_ENABLED'] === 'true';
const TOKEN = process.env['E2E_GITHUB_TOKEN'] ?? '';
const OWNER = process.env['E2E_GITHUB_OWNER'] ?? 'Tam-ma';
const REPO = process.env['E2E_GITHUB_REPO'] ?? 'tamma-test';

function createE2EConfig(): TammaConfig {
  return {
    mode: 'standalone',
    logLevel: 'debug',
    github: {
      authMode: 'pat' as const,
      token: TOKEN,
      owner: OWNER,
      repo: REPO,
      issueLabels: ['tamma'],
      excludeLabels: ['wontfix'],
      botUsername: 'tamma-bot',
    },
    agent: {
      model: 'claude-sonnet-4-5',
      maxBudgetUsd: 1.0,
      allowedTools: ['Read', 'Write', 'Edit', 'Bash', 'Glob', 'Grep'],
      permissionMode: 'bypassPermissions',
    },
    engine: {
      pollIntervalMs: 1000,
      workingDirectory: process.cwd(),
      approvalMode: 'auto',
      ciPollIntervalMs: 5000,
      ciMonitorTimeoutMs: 300_000,
    },
  };
}

function createE2ELogger(): ILogger {
  return {
    debug: (msg: string, ctx?: Record<string, unknown>) => { if (process.env['VERBOSE']) console.log('[DEBUG]', msg, ctx); },
    info: (msg: string, ctx?: Record<string, unknown>) => { console.log('[INFO]', msg, ctx ?? ''); },
    warn: (msg: string, ctx?: Record<string, unknown>) => { console.warn('[WARN]', msg, ctx ?? ''); },
    error: (msg: string, ctx?: Record<string, unknown>) => { console.error('[ERROR]', msg, ctx ?? ''); },
  };
}

function createMockAgent(overrides?: Partial<IAgentProvider>): IAgentProvider {
  return {
    executeTask: vi.fn().mockResolvedValue({
      success: true,
      output: '{"issueNumber":1,"summary":"Test","approach":"Test","fileChanges":[],"testingStrategy":"Test","estimatedComplexity":"low","risks":[]}',
      costUsd: 0.01,
      durationMs: 100,
    } satisfies AgentTaskResult),
    isAvailable: vi.fn().mockResolvedValue(true),
    dispose: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  };
}

const describeE2E = E2E_ENABLED ? describe : describe.skip;

describeE2E('TammaEngine GitHub Integration', () => {
  it('should select an issue from test repo', async () => {
    const { GitHubPlatform } = await import('@tamma/platforms');
    const platform = new GitHubPlatform();
    await platform.initialize({ type: 'pat', token: TOKEN });

    const config = createE2EConfig();
    const logger = createE2ELogger();
    const agent = createMockAgent();

    const engine = new TammaEngine({
      config,
      platform,
      agent,
      logger,
    });

    await engine.initialize();

    const issue = await engine.selectIssue();

    if (issue !== null) {
      expect(issue.number).toBeGreaterThan(0);
      expect(issue.title).toBeTruthy();
      expect(issue.labels).toContain('tamma');
      expect(engine.getState()).toBe(EngineState.SELECTING_ISSUE);
    } else {
      expect(engine.getState()).toBe(EngineState.IDLE);
    }

    await engine.dispose();
  });

  it('should analyze issue with context', async () => {
    const { GitHubPlatform } = await import('@tamma/platforms');
    const platform = new GitHubPlatform();
    await platform.initialize({ type: 'pat', token: TOKEN });

    const config = createE2EConfig();
    const logger = createE2ELogger();
    const agent = createMockAgent();

    const engine = new TammaEngine({
      config,
      platform,
      agent,
      logger,
    });

    await engine.initialize();

    const issue = await engine.selectIssue();
    if (issue === null) {
      console.log('No issues available, skipping analyze test');
      await engine.dispose();
      return;
    }

    const context = await engine.analyzeIssue(issue);

    expect(context).toContain(`#${issue.number}`);
    expect(context).toContain(issue.title);
    expect(context).toContain('Description');
    expect(context.length).toBeGreaterThan(50);

    await engine.dispose();
  });
});
