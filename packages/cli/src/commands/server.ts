/**
 * `tamma server` command
 *
 * Starts the Tamma engine with a C# API sidecar:
 *  - Spawns the C# API process (ASP.NET Core) as the HTTP backend
 *  - Runs the TammaEngine in-process for autonomous issue processing
 *
 * Loads configuration from tamma.config.json + environment variables,
 * creates a TammaEngine, and connects it to the C# API over HTTP.
 */

import * as path from 'node:path';
import * as fs from 'node:fs';
import { spawn, type ChildProcess } from 'node:child_process';
import { TammaEngine } from '@tamma/orchestrator';
import { InMemoryEventStore, DiagnosticsQueue, ContentSanitizer } from '@tamma/shared';
import {
  RoleBasedAgentResolver,
  AgentProviderFactory,
  ProviderHealthTracker,
  AgentPromptRegistry,
  createDiagnosticsProcessor,
} from '@tamma/providers';
import type { IRoleBasedAgentResolver } from '@tamma/providers';
import { GitHubPlatform } from '@tamma/platforms';
import { createLogger } from '@tamma/observability';
import { createCostTracker, FileStore } from '@tamma/cost-monitor';
import { loadConfig, validateConfig, normalizeAgentsConfig, buildPlatformConfig } from '../config.js';
import type { CLIOptions } from '../config.js';

export interface ServerOptions extends CLIOptions {
  port?: number;
  host?: string;
}

function findApiBinary(): string {
  const envBinary = process.env['TAMMA_API_BINARY'];
  if (envBinary && fs.existsSync(envBinary)) return envBinary;

  const repoRoot = path.resolve(import.meta.dirname ?? __dirname, '..', '..', '..', '..');
  const candidates = [
    path.join(repoRoot, 'apps', 'tamma-elsa', 'src', 'Tamma.Api', 'bin', 'Release', 'net8.0', 'Tamma.Api.dll'),
    path.join(repoRoot, 'apps', 'tamma-elsa', 'src', 'Tamma.Api', 'bin', 'Debug', 'net8.0', 'Tamma.Api.dll'),
  ];

  for (const candidate of candidates) {
    if (fs.existsSync(candidate)) return candidate;
  }

  return '';
}

async function waitForApiHealth(port: number, maxRetries = 30): Promise<void> {
  const url = `http://127.0.0.1:${port}/api/health`;
  for (let i = 0; i < maxRetries; i++) {
    try {
      const response = await fetch(url);
      if (response.ok) return;
    } catch {
      // Not ready yet
    }
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  throw new Error(`API sidecar health check failed after ${maxRetries}s at ${url}`);
}

function spawnApiSidecar(port: number): ChildProcess {
  const apiBinary = findApiBinary();
  const env: Record<string, string> = {
    ...process.env as Record<string, string>,
    ASPNETCORE_URLS: `http://+:${port}`,
  };

  if (apiBinary && apiBinary.endsWith('.dll')) {
    return spawn('dotnet', [apiBinary], { env, stdio: 'pipe' });
  }

  const repoRoot = path.resolve(import.meta.dirname ?? __dirname, '..', '..', '..', '..');
  const projectDir = path.join(repoRoot, 'apps', 'tamma-elsa', 'src', 'Tamma.Api');
  return spawn('dotnet', ['run', '--project', projectDir], { env, stdio: 'pipe' });
}

export async function serverCommand(options: ServerOptions): Promise<void> {
  const config = loadConfig(options);
  const errors = validateConfig(config);

  if (errors.length > 0) {
    console.error('Configuration errors:');
    for (const err of errors) {
      console.error(`  - ${err}`);
    }
    process.exit(1);
  }

  const port = options.port ?? 3001;
  const host = options.host ?? '127.0.0.1';

  const logger = createLogger('tamma-server', config.logLevel);

  // SaaS mode is not supported in the server command — use the API server
  if (config.github.authMode === 'saas') {
    console.error('SaaS mode is not supported in the CLI server command. Use the standalone API server.');
    process.exit(1);
  }

  // Start C# API sidecar
  logger.info(`Starting C# API sidecar on port ${port}...`);
  const apiProcess = spawnApiSidecar(port);

  apiProcess.stdout?.on('data', (data: Buffer) => {
    logger.debug(`[api] ${data.toString().trim()}`);
  });
  apiProcess.stderr?.on('data', (data: Buffer) => {
    logger.warn(`[api] ${data.toString().trim()}`);
  });
  apiProcess.on('error', (err) => {
    logger.error(`Failed to start API sidecar: ${err.message}`);
    logger.error('Ensure the .NET 8 SDK is installed: https://dotnet.microsoft.com/download');
    process.exit(1);
  });

  try {
    await waitForApiHealth(port);
    logger.info(`C# API sidecar healthy on port ${port}`);
  } catch (err) {
    logger.error('C# API sidecar did not become healthy', { error: err });
    apiProcess.kill();
    process.exit(1);
  }

  // Platform with PAT or App auth
  const platform = new GitHubPlatform();
  await platform.initialize(buildPlatformConfig(config.github));

  // Config-driven agent setup
  const agentsConfig = normalizeAgentsConfig(config);
  const healthTracker = new ProviderHealthTracker();
  const agentFactory = new AgentProviderFactory();
  const promptRegistry = new AgentPromptRegistry({ config: agentsConfig });

  const costStorePath = path.join(config.engine.workingDirectory, '.tamma', 'cost-data.json');
  const costTracker = createCostTracker({ storage: new FileStore(costStorePath) });

  const diagnosticsQueue = new DiagnosticsQueue({ drainIntervalMs: 5000, maxQueueSize: 1000 });
  diagnosticsQueue.setProcessor(createDiagnosticsProcessor(costTracker, logger));

  const sanitizer = config.security?.sanitizeContent !== false ? new ContentSanitizer() : undefined;
  if (sanitizer) {
    logger.info('Content sanitization enabled');
  }

  const resolverOptions: ConstructorParameters<typeof RoleBasedAgentResolver>[0] = {
    config: agentsConfig,
    factory: agentFactory,
    health: healthTracker,
    promptRegistry,
    diagnostics: diagnosticsQueue,
    logger,
  };
  if (costTracker !== undefined) {
    resolverOptions.costTracker = costTracker;
  }
  if (sanitizer !== undefined) {
    resolverOptions.sanitizer = sanitizer;
  }

  const agentResolver: IRoleBasedAgentResolver = new RoleBasedAgentResolver(resolverOptions);

  // Event store for audit trail
  const eventStore = new InMemoryEventStore();

  // Engine
  const engine = new TammaEngine({
    config,
    platform,
    agentResolver,
    logger,
    eventStore,
  });

  await engine.initialize();

  // Graceful shutdown
  let shuttingDown = false;
  const shutdown = async (): Promise<void> => {
    if (shuttingDown) { process.exit(1); return; }
    shuttingDown = true;
    const shutdownTimer = setTimeout(() => { process.exit(1); }, 10_000);
    shutdownTimer.unref();
    logger.info('Shutting down server...');
    try { apiProcess.kill('SIGTERM'); } catch { /* ignore */ }
    try { await diagnosticsQueue.dispose(); } catch (err) { logger.error('DiagnosticsQueue disposal failed', { error: err }); }
    try { await costTracker.dispose(); } catch (err) { logger.error('CostTracker disposal failed', { error: err }); }
    process.exit(0);
  };

  process.on('SIGINT', () => {
    void shutdown();
  });
  process.on('SIGTERM', () => {
    void shutdown();
  });

  logger.info(`Tamma server running — engine in-process, C# API sidecar on ${host}:${port}`);
}
