import { ConfigurationError } from '@tamma/shared';
import type { TammaConfig } from '@tamma/shared';
import { createLogger } from '@tamma/observability';
import { ClaudeAgentProvider } from '@tamma/providers';
import { GitHubPlatform } from '@tamma/platforms';
import { TammaEngine } from '@tamma/orchestrator';

function requireEnv(name: string): string {
  const value = process.env[name];
  if (value === undefined || value === '') {
    throw new ConfigurationError(`Missing required environment variable: ${name}`);
  }
  return value;
}

function optionalEnv(name: string, defaultValue: string): string {
  const value = process.env[name];
  if (value === undefined || value === '') {
    return defaultValue;
  }
  return value;
}

function loadConfig(): TammaConfig {
  return {
    mode: 'standalone',
    logLevel: optionalEnv('LOG_LEVEL', 'info') as TammaConfig['logLevel'],
    github: {
      authMode: 'pat' as const,
      token: requireEnv('GITHUB_TOKEN'),
      owner: requireEnv('GITHUB_OWNER'),
      repo: requireEnv('GITHUB_REPO'),
      issueLabels: optionalEnv('ISSUE_LABELS', 'tamma').split(',').map((l) => l.trim()),
      excludeLabels: optionalEnv('EXCLUDE_LABELS', '').split(',').map((l) => l.trim()).filter((l) => l.length > 0),
      botUsername: requireEnv('BOT_USERNAME'),
    },
    agent: {
      model: optionalEnv('AGENT_MODEL', 'sonnet'),
      maxBudgetUsd: parseFloat(optionalEnv('MAX_BUDGET_USD', '5.0')),
      allowedTools: optionalEnv('ALLOWED_TOOLS', 'Read,Write,Edit,Bash,Glob,Grep').split(',').map((t) => t.trim()),
      permissionMode: optionalEnv('PERMISSION_MODE', 'default') as 'bypassPermissions' | 'default',
    },
    engine: {
      pollIntervalMs: parseInt(optionalEnv('POLL_INTERVAL_MS', '300000'), 10),
      workingDirectory: optionalEnv('WORKING_DIR', process.cwd()),
      approvalMode: optionalEnv('APPROVAL_MODE', 'cli') as 'cli' | 'auto',
      ciPollIntervalMs: parseInt(optionalEnv('CI_POLL_INTERVAL_MS', '30000'), 10),
      ciMonitorTimeoutMs: parseInt(optionalEnv('CI_MONITOR_TIMEOUT_MS', '3600000'), 10),
      mergeStrategy: optionalEnv('MERGE_STRATEGY', 'squash') as 'squash' | 'merge' | 'rebase',
    },
  };
}

async function main(): Promise<void> {
  const config = loadConfig();
  const logger = createLogger('tamma-engine', config.logLevel);

  logger.info('Starting Tamma Engine', {
    owner: config.github.owner,
    repo: config.github.repo,
    model: config.agent.model,
    approvalMode: config.engine.approvalMode,
  });

  const agent = new ClaudeAgentProvider();
  const platform = new GitHubPlatform();

  await platform.initialize({
    type: 'pat',
    token: config.github.authMode === 'pat' ? config.github.token : '',
  });

  const engine = new TammaEngine({ config, platform, agent, logger });

  // Graceful shutdown
  const shutdown = async (): Promise<void> => {
    logger.info('Shutting down...');
    await engine.dispose();
    process.exit(0);
  };

  process.on('SIGINT', () => void shutdown());
  process.on('SIGTERM', () => void shutdown());

  await engine.initialize();
  await engine.run();
}

main().catch((err: unknown) => {
  const message = err instanceof Error ? err.message : String(err);
  console.error(`Fatal error: ${message}`);
  process.exit(1);
});
