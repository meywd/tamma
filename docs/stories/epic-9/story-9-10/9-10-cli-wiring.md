# Story 9-10: CLI Wiring

## User Story

As a developer using the Tamma CLI, I want the CLI to use the new unified services and display diagnostics, health status, and provider chain info, so that I can monitor and manage the agent system from the command line.

## Goal

Wire the CLI (`packages/cli/`) to construct the `RoleBasedAgentResolver` with store-backed dependencies and pass it to the engine. Add CLI commands for viewing diagnostics, health status, and managing provider chains. Replace hardcoded `new ClaudeAgentProvider()` with config-driven resolution.

## Acceptance Criteria

1. CLI `start` command constructs `RoleBasedAgentResolver` with:
   - `AgentProviderFactory` (existing)
   - `ProviderHealthTracker` with persistence sync (Story 9-3)
   - `DiagnosticsQueue` draining to diagnostics store (Story 9-2)
   - Config loaded via `normalizeAgentsConfig()` for self-hosted mode, or from API for SaaS mode
   - `ContentSanitizer` when `security.sanitizeContent !== false`
2. CLI `server` command does the same for the server/API mode.
3. Shutdown sequence flushes `DiagnosticsQueue` and disposes `CostTracker` before exit.
4. New CLI commands / subcommands:
   - `tamma diagnostics` -- show cost/usage summary for current session
   - `tamma health` -- show provider health status (circuit breaker states)
   - `tamma providers` -- list configured providers and their chain order
5. Legacy `agent` config still works via `normalizeAgentsConfig()`.
6. `agentResolver` is typed as `IRoleBasedAgentResolver` when passed to `TammaEngine`.
7. Shutdown re-entrancy guard and 10s timeout preserved.
8. Each disposal call is wrapped in try/catch so a single failure does not prevent subsequent disposals.

## Technical Context

### Existing Files

- `packages/cli/src/commands/start.tsx` -- CLI start command (creates engine, runs loop)
- `packages/cli/src/commands/server.ts` -- CLI server command (starts Fastify + engine)
- `packages/cli/src/config.ts` -- `loadConfig()`, `mergeConfig()`, `normalizeAgentsConfig` re-export
- `packages/providers/src/role-based-agent-resolver.ts` -- `RoleBasedAgentResolver`
- `packages/providers/src/agent-provider-factory.ts` -- `AgentProviderFactory`
- `packages/providers/src/provider-health.ts` -- `ProviderHealthTracker`
- `packages/shared/src/telemetry/diagnostics-queue.ts` -- `DiagnosticsQueue`
- `packages/shared/src/security/content-sanitizer.ts` -- `ContentSanitizer`

### CLI Wiring Pattern

```typescript
// In start command, replace `const agent = new ClaudeAgentProvider()`:

const agentsConfig = normalizeAgentsConfig(config);
const healthTracker = new ProviderHealthTracker({ /* with persistence sync */ });
const agentFactory = new AgentProviderFactory(logger);
const promptRegistry = new AgentPromptRegistry({ config: agentsConfig, logger });

const diagnosticsQueue = new DiagnosticsQueue({ drainIntervalMs: 5000 });
diagnosticsQueue.setProcessor(createDiagnosticsProcessor(costTracker, logger));

const sanitizer = config.security?.sanitizeContent !== false
  ? new ContentSanitizer()
  : undefined;

const agentResolver: IRoleBasedAgentResolver = new RoleBasedAgentResolver({
  config: agentsConfig,
  factory: agentFactory,
  health: healthTracker,
  promptRegistry,
  diagnostics: diagnosticsQueue,
  costTracker,
  sanitizer,
  logger,
});

const engine = new TammaEngine({
  config,
  platform,
  agentResolver,  // instead of agent
  logger,
});
```

### Shutdown Pattern

```typescript
const shutdown = async (): Promise<void> => {
  if (shuttingDown) { process.exit(1); return; }
  shuttingDown = true;
  const timer = setTimeout(() => process.exit(1), 10_000);
  timer.unref();

  try { await engine.dispose(); }
  catch (err) { logger.error('Engine disposal failed', { error: err }); }
  try { await diagnosticsQueue.dispose(); }
  catch (err) { logger.error('DiagnosticsQueue disposal failed', { error: err }); }
  try { await costTracker.dispose(); }
  catch (err) { logger.error('CostTracker disposal failed', { error: err }); }

  removeLockfile();
  process.exit(0);
};
```

## Files

- MODIFY `packages/cli/src/commands/start.tsx` -- replace hardcoded agent with resolver
- MODIFY `packages/cli/src/commands/server.ts` -- same pattern
- CREATE `packages/cli/src/commands/diagnostics.tsx` -- diagnostics command
- CREATE `packages/cli/src/commands/health.tsx` -- health status command
- CREATE `packages/cli/src/commands/providers.tsx` -- provider list command
- MODIFY `packages/cli/src/config.ts` -- ensure all exports available

## Dependencies

- **Story 9-1** (config loading for `normalizeAgentsConfig()`)
- **Story 9-9** (engine integration with resolver)
- **Story 9-11** (diagnostics queue wiring)

## Effort Estimate

**12 hours**

- 3h: Wire resolver into start.tsx and server.ts
- 3h: Shutdown sequence with diagnostics flush
- 3h: New CLI commands (diagnostics, health, providers)
- 3h: Tests (config-driven resolution, shutdown, CLI commands)
