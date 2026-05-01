import React from 'react';
import { render } from 'ink';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { EngineState, DiagnosticsQueue, ContentSanitizer } from '@tamma/shared';
import type { IssueData } from '@tamma/shared';
import { TammaEngine } from '@tamma/orchestrator';
import type { EngineStats, ApprovalHandler } from '@tamma/orchestrator';
import {
  RoleBasedAgentResolver,
  AgentProviderFactory,
  ProviderHealthTracker,
  AgentPromptRegistry,
  createDiagnosticsProcessor,
} from '@tamma/providers';
import type { IRoleBasedAgentResolver } from '@tamma/providers';
import { GitHubPlatform } from '@tamma/platforms';
import type { Issue } from '@tamma/platforms';
import { createLogger } from '@tamma/observability';
import { createCostTracker, FileStore } from '@tamma/cost-monitor';
import type { CostTracker } from '@tamma/cost-monitor';
import { loadConfig, validateConfig, normalizeAgentsConfig, buildPlatformConfig } from '../config.js';
import type { CLIOptions } from '../config.js';
import type { ILogger } from '@tamma/shared/contracts';
import { writeLockfile, removeLockfile } from '../state.js';
import { createLogEmitter, createLoggerBridge } from '../log-emitter.js';
import { formatErrorWithSuggestions } from '../error-handler.js';
import { createFileLogSubscriber } from '../file-logger.js';
import type { StateEmitter, PendingApproval, CommandContext } from '../types.js';
import SessionLayout from '../components/SessionLayout.js';
import IssueSelector from '../components/IssueSelector.js';
import type { TammaConfig } from '@tamma/shared';

const HEALTH_SENTINEL = '/tmp/tamma-engine-healthy';

/**
 * A sleep that can be cancelled via AbortSignal.
 * Resolves immediately if the signal is already aborted.
 */
async function cancellableSleep(ms: number, signal?: AbortSignal): Promise<void> {
  if (signal?.aborted) return Promise.resolve();
  return new Promise<void>((resolve) => {
    const timer = setTimeout(resolve, ms);
    signal?.addEventListener('abort', () => {
      clearTimeout(timer);
      resolve();
    }, { once: true });
  });
}

function createStateEmitter(): StateEmitter {
  let lastArgs: [EngineState, IssueData | null, EngineStats] | null = null;
  const emitter: StateEmitter = {
    listener: null,
    emit(state, issue, stats) {
      lastArgs = [state, issue, stats];
      emitter.listener?.(state, issue, stats);
    },
    reEmit() {
      if (lastArgs !== null) {
        emitter.listener?.(...lastArgs);
      }
    },
  };
  return emitter;
}

/**
 * Show IssueSelector in a temporary Ink render, resolve with the user's choice.
 */
async function selectIssueInteractively(issues: Issue[]): Promise<Issue | null> {
  return new Promise((resolve) => {
    const { unmount } = render(
      <IssueSelector
        issues={issues}
        onSelect={(issue) => { unmount(); resolve(issue); }}
        onSkip={() => { unmount(); resolve(null); }}
      />,
    );
  });
}

/**
 * Resources created by agent setup that need disposal on shutdown.
 */
interface AgentSetupResult {
  agentResolver: IRoleBasedAgentResolver;
  diagnosticsQueue: DiagnosticsQueue;
  costTracker: CostTracker;
}

/**
 * Create config-driven agent resolver with all dependencies.
 * Returns the resolver and disposable resources for shutdown.
 */
function createAgentSetup(config: TammaConfig, logger: ILogger): AgentSetupResult {
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

  return { agentResolver, diagnosticsQueue, costTracker };
}

export async function startCommand(options: CLIOptions): Promise<void> {
  const config = loadConfig(options);
  const errors = validateConfig(config);

  if (errors.length > 0) {
    console.error('Configuration errors:');
    for (const err of errors) {
      console.error(`  - ${err}`);
    }
    // In `--mode service` (docker tamma-engine container), sit idle
    // instead of exiting so the container doesn't restart-loop and so
    // the deploy's layer-4 health check passes. The engine just isn't
    // useful until config is provided — but it's also not the customer-
    // facing surface in SaaS deployments where workers run via GitHub
    // Actions per-tenant (Story 19-1). For a `tamma start` invocation
    // outside service mode (single-user CLI), the original fail-fast
    // behavior is preserved.
    if (options.mode === 'service') {
      console.error('');
      console.error('Engine is in --mode service with incomplete config; sitting idle.');
      console.error('Configure GitHub credentials and restart to activate the worker loop.');
      try { fs.writeFileSync('/tmp/tamma-engine-healthy', String(Date.now())); } catch { /* best-effort */ }
      await new Promise<void>(resolve => {
        process.on('SIGTERM', () => resolve());
        process.on('SIGINT',  () => resolve());
      });
      return;
    }
    process.exit(1);
  }

  // For dry-run mode, force auto-approval and stop after planning
  if (options.dryRun === true) {
    config.engine.approvalMode = 'auto';
  }

  // SaaS mode is not supported in the CLI — use the API server
  if (config.github.authMode === 'saas') {
    console.error('SaaS mode is not supported in the CLI. Use the API server instead.');
    process.exit(1);
  }

  // Set up platform with PAT or App auth
  const platform = new GitHubPlatform();
  await platform.initialize(buildPlatformConfig(config.github));

  // ---- Service mode: headless, no TUI, auto-approval, JSON logging ----
  if (options.mode === 'service') {
    config.engine.approvalMode = 'auto';

    const logger = createLogger('tamma-engine', config.logLevel);

    // Config-driven agent setup
    const { agentResolver, diagnosticsQueue, costTracker } = createAgentSetup(config, logger);

    const engine = new TammaEngine({
      config,
      platform,
      agentResolver,
      logger,
      onStateChange: (state, issue, stats) => {
        writeLockfile(state, issue, stats);
        logger.info('State changed', { state, issue: issue?.title ?? null });
      },
    });

    let running = true;
    let shuttingDown = false;

    const removeHealthSentinel = (): void => {
      try { fs.unlinkSync(HEALTH_SENTINEL); } catch { /* ignore */ }
    };

    const shutdown = async (): Promise<void> => {
      if (shuttingDown) { process.exit(1); return; }
      shuttingDown = true;
      const shutdownTimer = setTimeout(() => { process.exit(1); }, 10_000);
      shutdownTimer.unref();
      running = false;
      logger.info('Shutting down engine (service mode)...');
      removeHealthSentinel();
      try { await engine.dispose(); } catch (err) { logger.error('Engine disposal failed', { error: err }); }
      try { await diagnosticsQueue.dispose(); } catch (err) { logger.error('DiagnosticsQueue disposal failed', { error: err }); }
      try { await costTracker.dispose(); } catch (err) { logger.error('CostTracker disposal failed', { error: err }); }
      removeLockfile();
      process.exit(0);
    };

    process.on('SIGINT', () => { void shutdown(); });
    process.on('SIGTERM', () => { void shutdown(); });

    try {
      await engine.initialize();
      // Write health sentinel so Docker HEALTHCHECK passes
      fs.writeFileSync(HEALTH_SENTINEL, String(Date.now()), 'utf-8');
      logger.info('Engine initialized (service mode). Health sentinel written.');

      while (running) {
        try {
          await engine.processOneIssue();
        } catch (err: unknown) {
          const { message, suggestions } = formatErrorWithSuggestions(err);
          logger.error(`Error processing issue: ${message}`, { suggestions });
        }

        if (running) {
          logger.info('Polling for next issue...', { pollIntervalMs: config.engine.pollIntervalMs });
          await cancellableSleep(config.engine.pollIntervalMs);
        }
      }
    } catch (err: unknown) {
      const { message, suggestions } = formatErrorWithSuggestions(err);
      logger.error(`Fatal error in service mode: ${message}`, { suggestions });
      removeHealthSentinel();
      process.exit(1);
    }

    return;
  }

  // ---- Interactive / dry-run mode ----

  // State emitter bridges engine callbacks → React state
  const stateEmitter = createStateEmitter();

  // Approval ref holds the pending promise for Ink-based approval
  const approvalRef: { current: PendingApproval | null } = { current: null };

  // Log emitter and logger bridge for interactive mode
  const logEmitter = createLogEmitter();
  const interactiveLogger = createLoggerBridge(logEmitter);

  // Use pino for dry-run, bridge logger for interactive mode
  const logger: ILogger = options.dryRun === true
    ? createLogger('tamma', config.logLevel)
    : interactiveLogger;

  // Config-driven agent setup
  const { agentResolver, diagnosticsQueue, costTracker } = createAgentSetup(config, logger);

  // File logging when --debug is set
  if (options.debug === true) {
    const { listener, filePath } = createFileLogSubscriber();
    logEmitter.subscribe(listener);
    logEmitter.emit('info', `Debug logs: ${filePath}`);
  }

  // Ink-based approval handler
  const approvalHandler: ApprovalHandler = async (plan) => {
    return new Promise<'approve' | 'reject' | 'skip'>((resolve) => {
      approvalRef.current = { plan, resolve };
      stateEmitter.reEmit();
    });
  };

  // Create engine
  const engine = new TammaEngine({
    config,
    platform,
    agentResolver,
    logger,
    onStateChange: (state, issue, stats) => {
      writeLockfile(state, issue, stats);
      stateEmitter.emit(state, issue, stats);
    },
    approvalHandler,
  });

  // Handle graceful shutdown
  let running = true;
  let shuttingDown = false;
  const shutdown = async (): Promise<void> => {
    if (shuttingDown) { process.exit(1); return; }
    shuttingDown = true;
    const shutdownTimer = setTimeout(() => { process.exit(1); }, 10_000);
    shutdownTimer.unref();
    running = false;
    logger.info('Shutting down...');
    try { await engine.dispose(); } catch (err) { logger.error('Engine disposal failed', { error: err }); }
    try { await diagnosticsQueue.dispose(); } catch (err) { logger.error('DiagnosticsQueue disposal failed', { error: err }); }
    try { await costTracker.dispose(); } catch (err) { logger.error('CostTracker disposal failed', { error: err }); }
    removeLockfile();
    process.exit(0);
  };

  process.on('SIGINT', () => { void shutdown(); });
  process.on('SIGTERM', () => { void shutdown(); });

  // Interactive issue selection — let user pick before engine starts
  let selectedIssueNumber: number | null = null;
  if (options.interactive === true && options.dryRun !== true) {
    const { owner, repo } = config.github;
    const issueResponse = await platform.listIssues(owner, repo, {
      state: 'open',
      labels: config.github.issueLabels,
    });

    // Filter out excludeLabels (same logic engine uses)
    const excludeLabels = config.github.excludeLabels;
    const candidates = issueResponse.data.filter((issue) =>
      !issue.labels.some((label) => excludeLabels.includes(label)),
    );

    if (candidates.length === 0) {
      console.log('No issues found matching configured labels.');
      await platform.dispose();
      return;
    }

    if (candidates.length === 1) {
      console.log(`Only one candidate: #${candidates[0]!.number} ${candidates[0]!.title}`);
      selectedIssueNumber = candidates[0]!.number;
    } else {
      const selected = await selectIssueInteractively(candidates);
      if (selected === null) {
        console.log('Skipped issue selection.');
        await platform.dispose();
        return;
      }
      selectedIssueNumber = selected.number;
      console.log(`Selected: #${selected.number} ${selected.title}`);
    }
  }

  // Dry-run mode: select → analyze → plan → print → exit
  if (options.dryRun === true) {
    try {
      await engine.initialize();
      const issue = await engine.selectIssue();
      if (issue === null) {
        console.log('No issues found matching configured labels.');
        await engine.dispose();
        return;
      }

      const context = await engine.analyzeIssue(issue);
      const plan = await engine.generatePlan(issue, context);

      console.log('\n' + '='.repeat(60));
      console.log(`Development Plan for Issue #${plan.issueNumber}`);
      console.log('='.repeat(60));
      console.log(`\nSummary: ${plan.summary}`);
      console.log(`\nApproach: ${plan.approach}`);
      console.log('\nFile Changes:');
      for (const fc of plan.fileChanges) {
        console.log(`  - [${fc.action}] ${fc.filePath}: ${fc.description}`);
      }
      console.log(`\nTesting Strategy: ${plan.testingStrategy}`);
      console.log(`Complexity: ${plan.estimatedComplexity}`);
      console.log(`Risks: ${plan.risks.length > 0 ? plan.risks.join(', ') : 'None identified'}`);
      console.log('='.repeat(60));

      await engine.dispose();
      removeLockfile();
    } catch (err: unknown) {
      const { message, suggestions } = formatErrorWithSuggestions(err);
      console.error(`Dry run failed: ${message}`);
      for (const s of suggestions) {
        console.error(`  → ${s}`);
      }
      await engine.dispose();
      removeLockfile();
      process.exit(1);
    }
    return;
  }

  // Interactive mode with SessionLayout
  let paused = false;
  const sleepController = { current: new AbortController() };

  const commandContext: CommandContext = {
    config,
    stats: { issuesProcessed: 0, totalCostUsd: 0, startedAt: Date.now() },
    state: EngineState.IDLE,
    issue: null,
    logEmitter,
    platform,
    showDebug: options.verbose === true,
    paused: false,
    setShowDebug(_show: boolean) { /* overridden by SessionLayout */ },
    setPaused(p: boolean) {
      paused = p;
      commandContext.paused = p;
    },
    shutdown() {
      sleepController.current.abort();
      void shutdown();
    },
    skipIssue() {
      if (approvalRef.current !== null) {
        approvalRef.current.resolve('skip');
        approvalRef.current = null;
        logEmitter.emit('info', 'Skipping current issue.');
      } else {
        logEmitter.emit('warn', 'No pending approval to skip. /skip is only available during plan approval.');
      }
    },
    approveCurrentPlan() {
      if (approvalRef.current !== null) {
        approvalRef.current.resolve('approve');
        approvalRef.current = null;
      } else {
        logEmitter.emit('warn', 'No pending approval.');
      }
    },
    rejectCurrentPlan(feedback?: string) {
      if (approvalRef.current !== null) {
        approvalRef.current.resolve('reject');
        approvalRef.current = null;
        if (feedback !== undefined) {
          logEmitter.emit('info', `Rejection feedback: ${feedback}`);
        }
      } else {
        logEmitter.emit('warn', 'No pending approval.');
      }
    },
  };

  const { waitUntilExit } = render(
    <SessionLayout
      stateEmitter={stateEmitter}
      logEmitter={logEmitter}
      approvalRef={approvalRef}
      commandContext={commandContext}
    />,
  );

  // Custom run loop for pause/resume support
  void (async () => {
    try {
      await engine.initialize();
      logEmitter.emit('info', 'Engine initialized.');

      // If interactive mode selected a specific issue, temporarily override listIssues
      // and restore the original immediately after the first call.
      if (selectedIssueNumber !== null) {
        const originalListIssues = platform.listIssues.bind(platform);
        platform.listIssues = async (...args: Parameters<typeof platform.listIssues>) => {
          platform.listIssues = originalListIssues;
          const result = await originalListIssues(...args);
          const filtered = result.data.filter((i) => i.number === selectedIssueNumber);
          return { ...result, data: filtered, totalCount: filtered.length };
        };
      }

      if (options.once === true) {
        await engine.processOneIssue();
        logEmitter.emit('info', 'Processing complete.');
      } else {
        while (running) {
          if (!paused) {
            try {
              await engine.processOneIssue();
            } catch (err: unknown) {
              const { message, suggestions } = formatErrorWithSuggestions(err);
              logEmitter.emit('error', `Error processing issue: ${message}`);
              for (const s of suggestions) {
                logEmitter.emit('error', `  → ${s}`);
              }
            }

            if (running && !paused) {
              logEmitter.emit('info', `Polling for next issue in ${config.engine.pollIntervalMs / 1000}s...`);
              sleepController.current = new AbortController();
              await cancellableSleep(config.engine.pollIntervalMs, sleepController.current.signal);
            }
          } else {
            sleepController.current = new AbortController();
            await cancellableSleep(500, sleepController.current.signal);
          }
        }
      }
    } catch (err: unknown) {
      const { message, suggestions } = formatErrorWithSuggestions(err);
      logEmitter.emit('error', `Fatal: ${message}`);
      for (const s of suggestions) {
        logEmitter.emit('error', `  → ${s}`);
      }
    }
  })();

  await waitUntilExit();
}
