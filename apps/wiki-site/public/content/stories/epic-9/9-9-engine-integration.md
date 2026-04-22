---
title: "Story 9-9: Engine Integration"
sidebar:
  order: 90
---

## User Story

As a developer, I want the TS engine to use the new unified API services (config store, health tracker, diagnostics, resolver) instead of in-process-only classes, so that the engine's state is shared with Elsa workflows.

## Goal

Wire the `TammaEngine` in `packages/orchestrator/src/engine.ts` to use the new API-backed services. The engine continues to call the resolver in-process for performance (no HTTP overhead per phase), but the resolver's dependencies (health tracker, diagnostics, config) now read/write to the shared persistent stores.

## Acceptance Criteria

1. `EngineContext` accepts `agentResolver?: IRoleBasedAgentResolver` alongside the legacy `agent?: IAgentProvider`. At least one must be provided.
2. When `agentResolver` is provided, the engine uses it for phase-based agent resolution via `getAgentForPhase()`.
3. The resolver is constructed with store-backed dependencies:
   - `IProviderHealthTracker` syncs with the health store (Story 9-3)
   - `DiagnosticsQueue` drains to the diagnostics store (Story 9-2)
   - Config loaded from the config store (Story 9-1) for the account
4. Legacy mode preserved: when only `agent` is provided, the engine works exactly as before.
5. When both `agent` and `agentResolver` are provided, the resolver takes precedence with a WARN log.
6. `initialize()` skips `agent.isAvailable()` in resolver mode (availability checked lazily).
7. `dispose()` calls `agentResolver.dispose()` to clear cached chains.
8. Phase-aware resolution: `generatePlan()` uses `PLAN_GENERATION`, `implementCode()` uses `CODE_GENERATION`.
9. Task config merge: engine overrides are passed through the resolver's `getTaskConfig()` for clamping.
10. Providers are disposed after each phase usage (not pooled -- subprocess providers are stateful).
11. Error handling: resolver failures are caught, logged, and re-thrown as `EngineError`.

## Technical Context

### Existing Files

- `packages/orchestrator/src/engine.ts` -- `TammaEngine`, `EngineContext`
- `packages/providers/src/role-based-agent-resolver.ts` -- `IRoleBasedAgentResolver`
- `packages/providers/src/provider-chain.ts` -- `ProviderChain`
- `packages/providers/src/agent-provider-factory.ts` -- `AgentProviderFactory`
- `packages/providers/src/provider-health.ts` -- `ProviderHealthTracker`
- `packages/shared/src/telemetry/diagnostics-queue.ts` -- `DiagnosticsQueue`

### Engine Changes

```typescript
// EngineContext -- make agent optional, add resolver
export interface EngineContext {
  config: TammaConfig;
  platform: IGitPlatform;
  agent?: IAgentProvider;                     // now optional
  agentResolver?: IRoleBasedAgentResolver;    // NEW
  logger: ILogger;
  eventStore?: IEventStore;
  onStateChange?: OnStateChangeCallback;
  approvalHandler?: ApprovalHandler;
}
```

Key changes in engine methods:
- `initialize()`: skips `agent.isAvailable()` when only resolver is provided
- `dispose()`: calls both `agent?.dispose()` and `agentResolver?.dispose()`
- `generatePlan()`: calls `getAgentForPhase('PLAN_GENERATION')` and disposes after use
- `implementCode()`: calls `getAgentForPhase('CODE_GENERATION')` and disposes after use
- `getAgentForPhase()`: new private method that delegates to resolver or falls back to legacy agent
- `getEngineTaskOverrides()`: extracts task overrides from legacy config for clamping

### Architecture

```
TammaEngine
  │
  ├── getAgentForPhase('PLAN_GENERATION')
  │       │
  │       ▼
  │   IRoleBasedAgentResolver.getAgentForPhase()
  │       │
  │       ├── ConfigStore (per-account config)
  │       ├── HealthStore (shared circuit breaker)
  │       ├── DiagnosticsStore (shared cost tracking)
  │       └── PromptStore (per-account prompts)
  │
  └── Legacy: this.agent.executeTask()
```

## Files

- MODIFY `packages/orchestrator/src/engine.ts` -- add resolver support, phase-aware resolution
- MODIFY `packages/orchestrator/src/engine.test.ts` -- add tests for resolver mode

## Dependencies

- **Story 9-8** (IRoleBasedAgentResolver interface and implementation)

## Effort Estimate

**14 hours**

- 4h: Engine modifications (EngineContext, initialize, dispose, getAgentForPhase)
- 3h: Phase-aware resolution in generatePlan() and implementCode()
- 3h: Task config merge with clamping
- 4h: Tests (resolver mode, legacy mode, both-modes precedence, error handling, dispose lifecycle)
