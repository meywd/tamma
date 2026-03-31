---
title: "Task 2: Wire Config-Driven Agent Setup in server.ts"
sidebar:
  order: 90
---

**Story:** 9-10-cli-wiring - CLI Wiring
**Epic:** 9

## Task Description

Replace the hardcoded `new ClaudeAgentProvider()` in `packages/cli/src/commands/server.ts` with the same config-driven agent setup pattern used in start.tsx (Task 1). Wire `normalizeAgentsConfig()`, `AgentProviderFactory`, `ProviderHealthTracker`, `AgentPromptRegistry`, `DiagnosticsQueue`, `CostTracker`, and `ContentSanitizer` into the server command, passing the resulting `RoleBasedAgentResolver` to `TammaEngine`. Add `diagnosticsQueue` and `costTracker` disposal to the shutdown handler.

## Acceptance Criteria

- `ClaudeAgentProvider` import and instantiation removed from server.ts
- Same config-driven agent setup pattern as start.tsx (Task 1) applied
- `TammaEngine` receives `agentResolver` (typed as `IRoleBasedAgentResolver`) instead of `agent`
- Server shutdown handler disposes in correct order: `engineRegistry.disposeAll()` -> `diagnosticsQueue.dispose()` -> `costTracker.dispose()` -> `app.close()` -> `process.exit(0)`
- Each disposal call in shutdown is wrapped in try/catch
- Shutdown handler includes a `shuttingDown` re-entrancy guard
- Shutdown handler sets a 10-second unref'd timeout that forces `process.exit(1)` if disposal hangs
- When `ContentSanitizer` is created, an info-level log message is emitted
- `FileStore` constructed with plain string path
- `DiagnosticsQueue` created with `{ drainIntervalMs: 5000, maxQueueSize: 1000 }`

## Implementation Details

### Technical Requirements

#### 1. Update imports

Remove:
```typescript
import { ClaudeAgentProvider } from '@tamma/providers';
```

Add:
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
```

#### 2. Replace agent setup

Remove (around line 53):
```typescript
const agent = new ClaudeAgentProvider();
```

Replace with:
```typescript
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
```

**IMPORTANT**: `FileStore` takes a plain string, not an object. `AgentPromptRegistry` is the correct class name (not `PromptRegistry`). The concrete `RoleBasedAgentResolver` is typed as `IRoleBasedAgentResolver` per Story 9-9's DIP requirement.

#### 3. Update TammaEngine construction (around line 59)

Change:
```typescript
const engine = new TammaEngine({
  config,
  platform,
  agent,
  logger,
  eventStore,
});
```

To:
```typescript
const engine = new TammaEngine({
  config,
  platform,
  agentResolver,
  logger,
  eventStore,
});
```

#### 4. Update shutdown handler (around line 97)

Change:
```typescript
const shutdown = async (): Promise<void> => {
  logger.info('Shutting down server...');
  await engineRegistry.disposeAll();
  await app.close();
  process.exit(0);
};
```

To:
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

**Disposal order rationale:**
1. **Re-entrancy guard**: Second signal causes immediate `process.exit(1)` to avoid hanging
2. **Shutdown timeout**: 10-second unref'd timer forces exit if disposal hangs
3. `engineRegistry.disposeAll()` -- stop all engines first (they may still emit diagnostics events)
4. `diagnosticsQueue.dispose()` -- flush remaining telemetry events to cost tracker
5. `costTracker.dispose()` -- flush cost data to disk (FileStore.dispose() does a final flush)
6. `app.close()` -- close Fastify HTTP server
7. `process.exit(0)` -- exit process

**Error handling**: Each disposal call is wrapped in try/catch so a single failure does not prevent subsequent disposals or process exit.

### Files to Modify

- `packages/cli/src/commands/server.ts` -- **MODIFY** -- Replace imports, agent setup, engine construction, and shutdown handler

### Dependencies

- [ ] Task 1 ideally done first (same pattern; can be done in parallel)
- [ ] All Story 9-1 through 9-9 prerequisites met. **Note:** Story 9-1's `mergeConfig()` fix must be implemented before or alongside Story 9-10.
- [ ] Story 9-9 complete (TammaEngine accepts agentResolver as IRoleBasedAgentResolver)
- [ ] Story 9-11 Part A complete (DiagnosticsQueue in @tamma/shared)

## Testing Strategy

### Unit Tests

- [ ] Mock all provider/shared/cost-monitor imports
- [ ] Test that `normalizeAgentsConfig(config)` is called
- [ ] Test that `TammaEngine` receives `agentResolver` (typed as `IRoleBasedAgentResolver`, not `agent`)
- [ ] Test shutdown calls disposal in correct order: engineRegistry -> diagnosticsQueue -> costTracker -> app.close -> exit
- [ ] Test shutdown re-entrancy guard: second signal causes immediate `process.exit(1)`
- [ ] Test shutdown timeout (10s) forces `process.exit(1)` if disposal hangs
- [ ] Test each disposal call is wrapped in try/catch — failure of one does not prevent others
- [ ] Test that `ContentSanitizer` creation logs info message when enabled
- [ ] Test that `FileStore` is constructed with plain string path
- [ ] Test that `DiagnosticsQueue` is created with correct options

### Validation Steps

1. [ ] Remove ClaudeAgentProvider import and instantiation
2. [ ] Add all new imports
3. [ ] Replace agent setup with config-driven pipeline
4. [ ] Update TammaEngine construction to use `agentResolver`
5. [ ] Update shutdown handler with diagnosticsQueue and costTracker disposal
6. [ ] Run `pnpm --filter @tamma/cli run typecheck` -- must pass
7. [ ] Run `pnpm --filter @tamma/cli test` -- must pass
8. [ ] Manual: `tamma server` starts without errors
9. [ ] Manual: Ctrl+C triggers clean shutdown with cost data flushed

## Notes & Considerations

- The server.ts logger is always pino (`createLogger`), unlike start.tsx where it varies by mode. This simplifies the setup -- `logger` is available before the agent setup block.
- The server command has a single TammaEngine and a single shutdown handler, making this simpler than start.tsx which has separate service/interactive paths.
- The `eventStore` parameter is still passed to TammaEngine (unchanged from current code).
- `process.on('beforeExit')` is NOT used -- disposal happens explicitly inside `shutdown()` before `process.exit(0)`.
- `normalizeAgentsConfig` is re-exported from `@tamma/shared` per Story 9-1.
- Story 9-1's `mergeConfig()` fix must be implemented before or alongside Story 9-10, otherwise `config.agents` and `config.security` will be silently dropped during config loading.

## Completion Checklist

- [ ] `ClaudeAgentProvider` import removed from server.ts
- [ ] All new imports added
- [ ] Config-driven agent setup replaces hardcoded agent
- [ ] `TammaEngine` uses `agentResolver` instead of `agent`
- [ ] Shutdown handler disposes diagnosticsQueue and costTracker
- [ ] Disposal order correct: engines -> diagnostics -> cost -> app -> exit
- [ ] TypeScript compilation passes
- [ ] Unit tests written and passing
