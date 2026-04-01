---
title: "Story 10: CLI Wiring"
sidebar:
  order: 90
---

## Goal
Replace hardcoded `new ClaudeAgentProvider()` in CLI with config-driven `RoleBasedAgentResolver`. Wire diagnostics queue lifecycle into both `start.tsx` and `server.ts`.

## Actual constraints from the codebase

1. **`process.on('beforeExit')` NEVER fires** when `shutdown()` calls `process.exit(0)`. Both `start.tsx` (lines 124, 211) and `server.ts` (line 101) call `process.exit(0)` inside `shutdown()`. The `beforeExit` event only fires when the event loop drains naturally — `process.exit()` bypasses it entirely.

2. **`FileStore` constructor takes a plain string** — `new FileStore(filePath: string)`, not `{ filePath }`. See `file-store.ts` line 39: `constructor(filePath: string, autoFlushIntervalMs = 30000)`.

3. **`cwd` does not exist as a variable** in `start.tsx` scope. The working directory is `config.engine.workingDirectory`.

4. **The diagnostics queue is `DiagnosticsQueue`** from `@tamma/shared` (see Story 11 split), not `ToolHookRegistry` from mcp-client.

## Design

**Modify: `packages/cli/src/commands/start.tsx`**

Replace the agent setup section (around lines 88-93):

```typescript
import { normalizeAgentsConfig } from '../config.js'; // Re-exported from @tamma/shared per Story 9-1
import {
  RoleBasedAgentResolver,
  AgentProviderFactory,
  ProviderHealthTracker,
  AgentPromptRegistry,
} from '@tamma/providers';
import type { IRoleBasedAgentResolver } from '@tamma/providers';
import { DiagnosticsQueue, createDiagnosticsProcessor } from '@tamma/shared';
import { ContentSanitizer } from '@tamma/shared/security';
import { createCostTracker, FileStore } from '@tamma/cost-monitor';
import * as path from 'node:path';

// ... inside startCommand(), replace `const agent = new ClaudeAgentProvider()`:

const agentsConfig = normalizeAgentsConfig(config);
const healthTracker = new ProviderHealthTracker();
const agentFactory = new AgentProviderFactory();
const promptRegistry = new AgentPromptRegistry(agentsConfig);

// Cost tracking
const costStorePath = path.join(config.engine.workingDirectory, '.tamma', 'cost-data.json');
const costTracker = createCostTracker({
  storage: new FileStore(costStorePath),
});

// Shared diagnostics queue — single queue for provider + MCP telemetry
const diagnosticsQueue = new DiagnosticsQueue({
  drainIntervalMs: 5000,
  maxQueueSize: 1000,
});
diagnosticsQueue.setProcessor(createDiagnosticsProcessor(costTracker, logger));

// Security
const sanitizer = config.security?.sanitizeContent !== false
  ? new ContentSanitizer()
  : undefined;

if (sanitizer) {
  logger.info('Content sanitization enabled (default-on; set security.sanitizeContent=false to disable)');
}

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
```

Engine construction changes — pass `agentResolver` instead of `agent`:
```typescript
// Service mode
const engine = new TammaEngine({
  config,
  platform,
  agentResolver,
  logger,
  onStateChange: (state, issue, stats) => { ... },
});

// Interactive mode
const engine = new TammaEngine({
  config,
  platform,
  agentResolver,
  logger,
  onStateChange: (state, issue, stats) => { ... },
  approvalHandler,
});
```

**Fix shutdown in both modes** — add `diagnosticsQueue.dispose()` BEFORE `process.exit(0)`:

```typescript
// Service mode shutdown (around line 118)
let shuttingDown = false;
const shutdown = async (): Promise<void> => {
  if (shuttingDown) { process.exit(1); return; }
  shuttingDown = true;
  const shutdownTimer = setTimeout(() => { process.exit(1); }, 10_000);
  shutdownTimer.unref();
  running = false;
  logger.info('Shutting down engine (service mode)...');
  removeHealthSentinel();
  try { await engine.dispose(); }
  catch (err) { logger.error('Engine disposal failed', { error: err }); }
  try { await diagnosticsQueue.dispose(); }
  catch (err) { logger.error('DiagnosticsQueue disposal failed', { error: err }); }
  try { await costTracker.dispose(); }
  catch (err) { logger.error('CostTracker disposal failed', { error: err }); }
  removeLockfile();
  process.exit(0);
};

// Interactive mode shutdown (around line 206)
let shuttingDown = false;
const shutdown = async (): Promise<void> => {
  if (shuttingDown) { process.exit(1); return; }
  shuttingDown = true;
  const shutdownTimer = setTimeout(() => { process.exit(1); }, 10_000);
  shutdownTimer.unref();
  running = false;
  logger.info('Shutting down...');
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

**Modify: `packages/cli/src/commands/server.ts`**

Same pattern — replace hardcoded agent with resolver, add diagnostics queue:

```typescript
import { normalizeAgentsConfig } from '../config.js'; // Re-exported from @tamma/shared per Story 9-1
import {
  RoleBasedAgentResolver,
  AgentProviderFactory,
  ProviderHealthTracker,
  AgentPromptRegistry,
} from '@tamma/providers';
import type { IRoleBasedAgentResolver } from '@tamma/providers';
import { DiagnosticsQueue, createDiagnosticsProcessor } from '@tamma/shared';
import { ContentSanitizer } from '@tamma/shared/security';
import { createCostTracker, FileStore } from '@tamma/cost-monitor';
import * as path from 'node:path';

// Inside serverCommand():

const agentsConfig = normalizeAgentsConfig(config);
const healthTracker = new ProviderHealthTracker();
const agentFactory = new AgentProviderFactory();
const promptRegistry = new AgentPromptRegistry(agentsConfig);

const costStorePath = path.join(config.engine.workingDirectory, '.tamma', 'cost-data.json');
const costTracker = createCostTracker({
  storage: new FileStore(costStorePath),
});

const diagnosticsQueue = new DiagnosticsQueue({
  drainIntervalMs: 5000,
  maxQueueSize: 1000,
});
diagnosticsQueue.setProcessor(createDiagnosticsProcessor(costTracker, logger));

const sanitizer = config.security?.sanitizeContent !== false
  ? new ContentSanitizer()
  : undefined;

if (sanitizer) {
  logger.info('Content sanitization enabled (default-on; set security.sanitizeContent=false to disable)');
}

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

// Engine — pass agentResolver instead of agent
const engine = new TammaEngine({
  config,
  platform,
  agentResolver,
  logger,
  eventStore,
});
```

Server shutdown — add queue + cost tracker disposal with re-entrancy guard and timeout:
```typescript
let shuttingDown = false;
const shutdown = async (): Promise<void> => {
  if (shuttingDown) { process.exit(1); return; }
  shuttingDown = true;
  const shutdownTimer = setTimeout(() => { process.exit(1); }, 10_000);
  shutdownTimer.unref();
  logger.info('Shutting down server...');
  try { await engineRegistry.disposeAll(); }
  catch (err) { logger.error('Engine registry disposal failed', { error: err }); }
  try { await diagnosticsQueue.dispose(); }
  catch (err) { logger.error('DiagnosticsQueue disposal failed', { error: err }); }
  try { await costTracker.dispose(); }
  catch (err) { logger.error('CostTracker disposal failed', { error: err }); }
  await app.close();
  process.exit(0);
};
```

## Design Notes

- The CLI creates a concrete `RoleBasedAgentResolver` but passes it to the engine as `IRoleBasedAgentResolver` per Story 9-9's DIP requirement.
- Agent setup code references `logger`, which is defined differently in service and interactive branches. The setup block must be placed inside each branch after logger creation, or the logger-independent parts must be extracted into a helper.

## Files
- MODIFY `packages/cli/src/commands/start.tsx`
- MODIFY `packages/cli/src/commands/server.ts`
- MODIFY `packages/cli/src/config.ts` — ensure `normalizeAgentsConfig()` is exported
- MODIFY `packages/cli/package.json` — add `@tamma/cost-monitor` dependency

## Dependencies / Notes
- Story 9-1's `mergeConfig()` fix must be implemented before or alongside Story 9-10, otherwise `config.agents` and `config.security` will be silently dropped during config loading.

## Verify
- Start with legacy `agent` config → works via `normalizeAgentsConfig()` producing a single-entry chain
- Start with new `agents` config → uses role-based resolution
- Cost data saved to `.tamma/cost-data.json` after a run
- `diagnosticsQueue.dispose()` flushes before `process.exit(0)` — verified by checking cost file is written
- Server shutdown disposes engine registry, diagnostics queue, cost tracker, then closes app
- `FileStore` constructed with plain string path, not object
- Test: dry-run mode disposes diagnosticsQueue and costTracker before exit
- Test: interactive mode early-exit paths (no candidates, user skips) dispose diagnosticsQueue and costTracker
- Test: shutdown re-entrancy guard — second signal causes immediate `process.exit(1)`
- Test: shutdown timeout (10s) forces `process.exit(1)` if disposal hangs
- Test: each disposal call is wrapped in try/catch so a single failure does not prevent subsequent disposals
- Test: `ContentSanitizer` creation logs an info message when sanitization is enabled
- Test: `agentResolver` is typed as `IRoleBasedAgentResolver` when passed to TammaEngine

## Logging Requirements

All provider-layer modules MUST use `ILogger` from `@tamma/shared/contracts` (not `console.log`).

- **INFO**: Provider initialization, config loaded, provider selected, chain fallback triggered
- **DEBUG**: Request parameters (redact API keys), response metadata, cache hits
- **WARN**: Provider degraded, rate limited, circuit breaker tripped, fallback to next provider
- **ERROR**: Provider call failed, config validation error, all providers exhausted
- **Structured context**: Always include `{ provider, model, issueId, duration }` where applicable
- **Credential safety**: NEVER log API keys, tokens, or secrets — log provider name and endpoint only
