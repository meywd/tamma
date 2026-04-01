---
title: "Task 1: Wire Config-Driven Agent Setup in start.tsx"
sidebar:
  order: 90
---

**Story:** 9-10-cli-wiring - CLI Wiring
**Epic:** 9

## Task Description

Replace the hardcoded `new ClaudeAgentProvider()` in `packages/cli/src/commands/start.tsx` with a config-driven agent setup using `RoleBasedAgentResolver`. This involves creating a `normalizeAgentsConfig()`-driven pipeline of `AgentProviderFactory`, `ProviderHealthTracker`, `AgentPromptRegistry`, `DiagnosticsQueue`, `CostTracker`, and `ContentSanitizer`, then passing the resulting `agentResolver` to `TammaEngine` in both service mode and interactive mode.

## Acceptance Criteria

- `ClaudeAgentProvider` import and instantiation removed from start.tsx
- `RoleBasedAgentResolver` constructed from `normalizeAgentsConfig(config)` output
- `FileStore` constructed with plain string path: `path.join(config.engine.workingDirectory, '.tamma', 'cost-data.json')`
- `DiagnosticsQueue` created with `{ drainIntervalMs: 5000, maxQueueSize: 1000 }`
- `diagnosticsQueue.setProcessor()` wired to `createDiagnosticsProcessor(costTracker, logger)`
- `ContentSanitizer` created only when `config.security?.sanitizeContent !== false`
- `RoleBasedAgentResolver` uses options-object constructor (not positional params)
- `TammaEngine` receives `agentResolver` (typed as `IRoleBasedAgentResolver`) instead of `agent` in both service mode and interactive mode engine constructions
- When `ContentSanitizer` is created, an info-level log message is emitted
- Legacy config with only `agent` field continues to work via `normalizeAgentsConfig()`
- New `agents` config uses role-based resolution

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

#### 2. Replace agent setup (lines 88-93)

Remove:
```typescript
// Set up agent provider
const agent = new ClaudeAgentProvider();
```

Replace with:
```typescript
const agentsConfig = normalizeAgentsConfig(config);
const healthTracker = new ProviderHealthTracker();
const agentFactory = new AgentProviderFactory();
const promptRegistry = new AgentPromptRegistry(agentsConfig);

// Cost tracking
const costStorePath = path.join(config.engine.workingDirectory, '.tamma', 'cost-data.json');
const costTracker = createCostTracker({
  storage: new FileStore(costStorePath),
});

// Shared diagnostics queue -- single queue for provider + MCP telemetry
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

**IMPORTANT constraints:**
- `FileStore` constructor takes a **plain string** (`new FileStore(costStorePath)`), NOT an object. See `file-store.ts` line 39: `constructor(filePath: string, autoFlushIntervalMs = 30000)`.
- `cwd` does not exist as a variable in `start.tsx` scope. Use `config.engine.workingDirectory`.
- The class is `AgentPromptRegistry` (NOT `PromptRegistry`) to avoid collision with MCP's `PromptRegistry`.
- `RoleBasedAgentResolver` uses an **options object** constructor, not positional parameters.
- The concrete `RoleBasedAgentResolver` is typed as `IRoleBasedAgentResolver` when stored and passed to `TammaEngine`, per Story 9-9's Dependency Inversion Principle requirement.
- When `ContentSanitizer` is created (default-on), log an info message to make the behavior visible.

#### 3. Update service mode TammaEngine construction (around line 101)

Change:
```typescript
const engine = new TammaEngine({
  config,
  platform,
  agent,
  logger,
  onStateChange: (state, issue, stats) => { ... },
});
```

To:
```typescript
const engine = new TammaEngine({
  config,
  platform,
  agentResolver,
  logger,
  onStateChange: (state, issue, stats) => { ... },
});
```

#### 4. Update interactive mode TammaEngine construction (around line 192)

Change:
```typescript
const engine = new TammaEngine({
  config,
  platform,
  agent,
  logger,
  onStateChange: (state, issue, stats) => { ... },
  approvalHandler,
});
```

To:
```typescript
const engine = new TammaEngine({
  config,
  platform,
  agentResolver,
  logger,
  onStateChange: (state, issue, stats) => { ... },
  approvalHandler,
});
```

#### 5. Note on logger scope

The `logger` variable is created differently in service mode vs interactive mode:
- Service mode: `const logger = createLogger('tamma-engine', config.logLevel);` (line 99)
- Interactive mode: the `logger` is conditionally `createLogger(...)` or `interactiveLogger` (lines 172-174)

The agent setup code must be placed AFTER the logger is created but BEFORE the TammaEngine construction. Since the agent setup needs `logger` and is shared between both modes, the setup block should be placed:
- **Before** the `if (options.mode === 'service')` branch for shared initialization, OR
- **Duplicated** in each branch if the logger differs

Since the logger differs between service and interactive modes, and the agent setup references `logger`, the setup should be duplicated in each branch OR the shared parts (those not using logger) should be extracted and the logger-dependent parts placed after logger creation.

**Recommended approach**: Move the agent setup into a helper function or duplicate the setup in both mode branches. The story spec places it before the mode branch (around lines 88-93) which means the `logger` reference in `createDiagnosticsProcessor(costTracker, logger)` and `agentResolver` construction needs a logger that is created before the branch. The interactive mode creates a different logger though, so the `diagnosticsQueue.setProcessor()` call should use the correct logger for each mode. Review the story spec carefully for the exact placement.

### Files to Modify

- `packages/cli/src/commands/start.tsx` -- **MODIFY** -- Replace imports, agent setup, and engine construction

### Dependencies

- [ ] Story 9-1 complete (normalizeAgentsConfig exists in config.ts). **Note:** Story 9-1's `mergeConfig()` fix must be implemented before or alongside Story 9-10, otherwise `config.agents` and `config.security` will be silently dropped during config loading.
- [ ] Story 9-8 complete (RoleBasedAgentResolver exists in @tamma/providers)
- [ ] Story 9-9 complete (TammaEngine accepts agentResolver as IRoleBasedAgentResolver)
- [ ] Story 9-11 Part A complete (DiagnosticsQueue exists in @tamma/shared)
- [ ] Story 9-4 complete (AgentProviderFactory exists)
- [ ] Story 9-3 complete (ProviderHealthTracker exists)
- [ ] Story 9-6 complete (AgentPromptRegistry exists)
- [ ] Story 9-7 complete (ContentSanitizer exists)

## Testing Strategy

### Unit Tests

- [ ] Mock all provider/shared/cost-monitor imports
- [ ] Test that `normalizeAgentsConfig(config)` is called with loaded config
- [ ] Test that `AgentProviderFactory` is instantiated
- [ ] Test that `ProviderHealthTracker` is instantiated
- [ ] Test that `AgentPromptRegistry` is instantiated with agentsConfig
- [ ] Test that `FileStore` is constructed with `path.join(config.engine.workingDirectory, '.tamma', 'cost-data.json')` -- plain string, not object
- [ ] Test that `createCostTracker` is called with `{ storage: fileStore }`
- [ ] Test that `DiagnosticsQueue` is created with `{ drainIntervalMs: 5000, maxQueueSize: 1000 }`
- [ ] Test that `diagnosticsQueue.setProcessor` is called with processor from `createDiagnosticsProcessor`
- [ ] Test that `ContentSanitizer` is created when `config.security` is undefined
- [ ] Test that `ContentSanitizer` is NOT created when `config.security.sanitizeContent` is `false`
- [ ] Test that `RoleBasedAgentResolver` receives options object with all required fields
- [ ] Test that TammaEngine receives `agentResolver` (not `agent`) in service mode
- [ ] Test that TammaEngine receives `agentResolver` (not `agent`) in interactive mode
- [ ] Test that `agentResolver` is typed as `IRoleBasedAgentResolver` (not the concrete class)
- [ ] Test that `ContentSanitizer` creation logs info message when sanitization is enabled
- [ ] Test that no info message is logged when `config.security.sanitizeContent` is `false`

### Validation Steps

1. [ ] Remove ClaudeAgentProvider import and instantiation
2. [ ] Add all new imports
3. [ ] Replace agent setup with config-driven pipeline
4. [ ] Update both TammaEngine constructions to use `agentResolver`
5. [ ] Run `pnpm --filter @tamma/cli run typecheck` -- must pass
6. [ ] Run `pnpm --filter @tamma/cli test` -- must pass
7. [ ] Manual: start with legacy config (only `agent` field) -- verify engine initializes
8. [ ] Manual: start with new `agents` config -- verify role-based resolution

## Notes & Considerations

- The story spec uses `PromptRegistry` in the import but the actual class name is `AgentPromptRegistry` (per Story 9-6). Use `AgentPromptRegistry`.
- The `RoleBasedAgentResolver` options-object constructor was established in Story 9-8 to replace the original 8-parameter constructor for clarity.
- `DiagnosticsQueue` lives in `@tamma/shared` (not `@tamma/mcp-client`) per Story 9-11 to avoid circular dependencies.
- The cost data file path `.tamma/cost-data.json` is relative to `config.engine.workingDirectory`, not the current working directory.
- `normalizeAgentsConfig` is re-exported from `@tamma/shared` per Story 9-1.
- Agent setup code references `logger`, which is defined differently in service and interactive branches. The setup block must be placed inside each branch after logger creation, or the logger-independent parts must be extracted into a helper.
- Story 9-1's `mergeConfig()` fix must be implemented before or alongside this task, otherwise `config.agents` and `config.security` will be silently dropped during config loading.

## Completion Checklist

- [ ] `ClaudeAgentProvider` import removed from start.tsx
- [ ] All new imports added (providers, shared, cost-monitor)
- [ ] Config-driven agent setup replaces hardcoded agent
- [ ] `FileStore` constructed with plain string path
- [ ] `DiagnosticsQueue` created with correct options
- [ ] `ContentSanitizer` created conditionally
- [ ] `RoleBasedAgentResolver` created with options object
- [ ] Service mode TammaEngine uses `agentResolver`
- [ ] Interactive mode TammaEngine uses `agentResolver`
- [ ] TypeScript compilation passes
- [ ] Unit tests written and passing
