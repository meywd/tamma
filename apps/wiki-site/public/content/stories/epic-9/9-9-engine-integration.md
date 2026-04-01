---
title: "Story 9: Engine Integration"
sidebar:
  order: 90
---

## Goal
Update engine to use `IRoleBasedAgentResolver` (interface from Story 9-8) instead of single hardcoded agent. Backward compatible — still accepts single `agent`.

> **Story 9-10 compatibility note:** Story 9-10 (CLI Wiring) must pass `IRoleBasedAgentResolver` to the engine, not the concrete `RoleBasedAgentResolver` class. The engine's public API surface (`EngineContext.agentResolver`) accepts the interface, ensuring loose coupling.

## Actual constraints from engine.ts

1. **`EngineContext.agent` is required today** (line 36: `agent: IAgentProvider`). The constructor at line 92 assigns `this.agent = ctx.agent` unconditionally. `initialize()` at line 102 calls `this.agent.isAvailable()`. `dispose()` at line 122 calls `this.agent.dispose()`.

2. **`this.config.agent.model` is logged** in `initialize()` at line 108. This will throw if `config.agent` is undefined in resolver mode.

3. **`generatePlan()` and `implementCode()`** both read task config from `this.config.agent` (model, maxBudgetUsd, allowedTools, permissionMode) — lines 434-436 and 685-689.

## Design

**Prompt injection awareness:** Issue body and comments are interpolated unsanitized into prompts sent to AI providers. The `SecureAgentProvider` wrapper (when configured via the resolver) handles content sanitization. Future work should add engine-level sanitization as defense-in-depth. See Story 9-8 for `SecureAgentProvider` details.

**Modify: `packages/orchestrator/src/engine.ts`**

```typescript
import type { IRoleBasedAgentResolver } from '@tamma/providers';
import { randomUUID } from 'node:crypto';

// EngineContext — make agent optional, add resolver (uses interface, not concrete class)
export interface EngineContext {
  config: TammaConfig;
  platform: IGitPlatform;
  agent?: IAgentProvider;                     // now optional
  agentResolver?: IRoleBasedAgentResolver;    // NEW — uses interface for dependency inversion
  logger: ILogger;
  eventStore?: IEventStore;
  onStateChange?: OnStateChangeCallback;
  approvalHandler?: ApprovalHandler;
}
```

Private fields — use `IRoleBasedAgentResolver` interface, not the concrete class:
```typescript
private readonly agent: IAgentProvider | undefined;
private readonly agentResolver: IRoleBasedAgentResolver | undefined;
private readonly engineId = randomUUID(); // import from 'node:crypto'
```

Constructor validation — require at least one:
```typescript
constructor(ctx: EngineContext) {
  if (!ctx.agent && !ctx.agentResolver) {
    throw new EngineError('Either agent or agentResolver must be provided in EngineContext');
  }
  if (ctx.agent && ctx.agentResolver) {
    this.logger.warn('Both agent and agentResolver provided; resolver takes precedence for phase resolution');
  }
  this.config = ctx.config;
  this.platform = ctx.platform;
  this.agent = ctx.agent;              // may be undefined
  this.agentResolver = ctx.agentResolver;
  // ...
}
```

**Guard `initialize()`** — skip `agent.isAvailable()` in resolver mode (the resolver checks availability when `getProvider()` is called):
```typescript
async initialize(): Promise<void> {
  if (this.agent) {
    const available = await this.agent.isAvailable();
    if (!available) {
      throw new EngineError('Agent provider is not available. Check ANTHROPIC_API_KEY.');
    }
  }
  if (this.agentResolver) {
    this.logger.warn('Using resolver mode — ensure content sanitizer is configured in resolver options');
  }
  this.logger.info('TammaEngine initialized', {
    mode: this.config.mode,
    model: this.config.agent?.model ?? 'resolver-mode',
    approvalMode: this.config.engine.approvalMode,
  });
}
```

> **Note:** `TammaConfig.agent` remains required in the current schema. The `this.config.agent?.model` optional chaining is future-proofing for when `TammaConfig.agent` is made optional in a future story. The `'resolver-mode'` log value will only appear after that change.

**Guard `dispose()`** — only call `agent.dispose()` if agent exists. Also dispose resolver to clear cached chains:
```typescript
async dispose(): Promise<void> {
  this.running = false;
  if (this.currentPipelinePromise) {
    await this.currentPipelinePromise.catch(() => {});
  }
  if (this.agent) {
    await this.agent.dispose();
  }
  if (this.agentResolver) {
    await this.agentResolver.dispose();
  }
  await this.platform.dispose();
  this.logger.info('TammaEngine disposed');
}
```

**Phase-aware agent resolution:**
```typescript
private async getAgentForPhase(phase: WorkflowPhase): Promise<IAgentProvider> {
  if (this.agentResolver) {
    try {
      return await this.agentResolver.getAgentForPhase(phase, {
        projectId: `${this.config.github.owner}/${this.config.github.repo}`,
        engineId: this.engineId,
      });
    } catch (err) {
      this.logger.error('Failed to resolve agent for phase', { phase, error: err instanceof Error ? err.message : String(err) });
      throw new EngineError(`Failed to resolve agent for phase ${phase}: ${err instanceof Error ? err.message : String(err)}`);
    }
  }
  if (!this.agent) {
    throw new EngineError('No agent available: neither agentResolver nor agent is configured');
  }
  return this.agent;
}
```

> **Note:** `projectId` uses `config.github.owner/repo` which is platform-specific. Future work should derive it from `this.platform.getProjectId()` for multi-platform support.

Note: providers are NOT pooled. Each `getAgentForPhase()` call creates a fresh provider via the factory. Providers MUST be disposed after each phase usage (see `generatePlan()` and `implementCode()` below). This is intentional — subprocess-based providers (claude-code, opencode) are stateful processes that should not be shared across phases.

**Task config merge order** — pass engine overrides THROUGH the resolver's clamping logic (budget ceiling, permission mode env var guard, allowedTools intersection), then engine sets prompt and cwd on top:

Resolver-controlled fields: `allowedTools`, `maxBudgetUsd`, `permissionMode`
Engine-controlled fields: `prompt`, `cwd`, `outputFormat`

```typescript
// In generatePlan():
const agent = await this.getAgentForPhase('PLAN_GENERATION');
const resolverConfig = this.agentResolver?.getTaskConfig('architect', this.getEngineTaskOverrides()) ?? {};
const safeResolverConfig = {
  allowedTools: resolverConfig.allowedTools,
  maxBudgetUsd: resolverConfig.maxBudgetUsd,
  permissionMode: resolverConfig.permissionMode,
};

const taskConfig = {
  ...safeResolverConfig,                       // resolver-controlled fields (clamped)
  prompt: planPrompt,                          // engine always sets prompt
  cwd: this.config.engine.workingDirectory,    // engine always sets cwd
};

try {
  const result = await agent.executeTask(taskConfig, onProgress);
  // ... existing cost tracking, event recording ...
} finally {
  await agent.dispose(); // providers are NOT pooled — dispose after each phase
}
```

```typescript
// In implementCode():
const agent = await this.getAgentForPhase('CODE_GENERATION');
const resolverConfig = this.agentResolver?.getTaskConfig('implementer', this.getEngineTaskOverrides()) ?? {};
const safeResolverConfig = {
  allowedTools: resolverConfig.allowedTools,
  maxBudgetUsd: resolverConfig.maxBudgetUsd,
  permissionMode: resolverConfig.permissionMode,
};

const taskConfig = {
  ...safeResolverConfig,
  prompt: implPrompt,
  cwd: this.config.engine.workingDirectory,
};

try {
  const result = await agent.executeTask(taskConfig, onProgress);
  // ... existing cost tracking, event recording ...
} finally {
  await agent.dispose(); // providers are NOT pooled — dispose after each phase
}
```

Helper to extract engine-level overrides (passed to resolver's `getTaskConfig()` so clamping applies). Only applies when `config.agent` has meaningful values AND no resolver is present, or when both are present (documented merge behavior):
```typescript
private getEngineTaskOverrides(): Partial<AgentTaskConfig> {
  if (!this.config.agent) return {};
  return {
    model: this.config.agent.model,
    maxBudgetUsd: this.config.agent.maxBudgetUsd,
    allowedTools: this.config.agent.allowedTools,
    permissionMode: this.config.agent.permissionMode,
  };
}
```

> **Both-modes precedence rule:** When both `agent` AND `agentResolver` are provided, the resolver takes precedence for `getAgentForPhase()`. The single `agent` is still validated in `initialize()` for backward compatibility. `getEngineTaskOverrides()` values are passed through the resolver's clamping when a resolver is present.

## Files
- MODIFY `packages/orchestrator/src/engine.ts`
- MODIFY `packages/orchestrator/src/engine.test.ts` — add tests for resolver mode

## Verify
- Existing tests still pass (backward compat with single agent)
- New test: engine with `agentResolver` only (no `agent`) — `initialize()` does not call `isAvailable()`
- New test: `dispose()` with no `agent` — does not throw
- New test: `dispose()` calls `agentResolver.dispose()` to clear cached chains
- New test: `generatePlan()` uses `PLAN_GENERATION` phase, gets architect role
- New test: `generatePlan()` disposes agent after use (try/finally)
- New test: `implementCode()` uses `CODE_GENERATION` phase, gets implementer role
- New test: `implementCode()` disposes agent after use (try/finally)
- New test: task config merge order — engine overrides passed through resolver's `getTaskConfig(role, overrides)` for clamping, then prompt/cwd set on top
- New test: task config uses explicit field allowlisting (`allowedTools`, `maxBudgetUsd`, `permissionMode`) — no arbitrary resolver fields leak through
- New test: `config.agent?.model ?? 'resolver-mode'` logged correctly in both modes
- New test: when both `agent` and `agentResolver` provided, resolver takes precedence for `getAgentForPhase()` and a WARN is logged
- New test: `initialize()` with both `agent` and `agentResolver` — still validates single agent's availability
- New test: `getAgentForPhase()` error from resolver is caught, logged, and re-thrown as `EngineError`
- New test: fallback path throws `EngineError('No agent available: neither agentResolver nor agent is configured')` instead of non-null assertion
- New test: events emitted during resolver mode include resolved agent/provider identity (not just 'unknown')
- New test: task config merge with `permissionMode: 'default'` (not just bypassPermissions)
- New test: constructor error message is `'Either agent or agentResolver must be provided in EngineContext'`

## Logging Requirements

All provider-layer modules MUST use `ILogger` from `@tamma/shared/contracts` (not `console.log`).

- **INFO**: Provider initialization, config loaded, provider selected, chain fallback triggered
- **DEBUG**: Request parameters (redact API keys), response metadata, cache hits
- **WARN**: Provider degraded, rate limited, circuit breaker tripped, fallback to next provider
- **ERROR**: Provider call failed, config validation error, all providers exhausted
- **Structured context**: Always include `{ provider, model, issueId, duration }` where applicable
- **Credential safety**: NEVER log API keys, tokens, or secrets — log provider name and endpoint only
