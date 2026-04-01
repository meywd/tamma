---
title: "Task 1: Implement ProviderChain core iteration with health checks"
sidebar:
  order: 90
---

**Story:** 9-5-provider-chain - Provider Chain
**Epic:** 9

## Task Description

Create the `ProviderChain` class in `packages/providers/src/provider-chain.ts` with its constructor and the core `getProvider()` method. This task focuses on:

- The `IProviderChain` interface (for dependency inversion)
- The `ProviderChainOptions` interface (options object constructor pattern)
- The class skeleton with defensive copy/freeze of entries
- The ordered iteration loop
- The empty-chain guard (`EMPTY_PROVIDER_CHAIN`)
- Health checking via `IProviderHealthTracker` (interface, not concrete class)
- Centralized key construction via `ProviderHealthTracker.buildKey()`
- Provider creation via `IAgentProviderFactory` (interface, not concrete class)
- The `isAvailable()` check
- The final `NO_AVAILABLE_PROVIDER` throw with sanitized error messages

Budget checking and instrumentation wrapping are added in Tasks 2 and 3 respectively. This task establishes the core fallback loop structure.

## Acceptance Criteria

- `IProviderChain` interface defined with `getProvider()` method signature
- `ProviderChainOptions` interface defined with all dependencies
- `ProviderChain` class implements `IProviderChain`
- Constructor accepts `ProviderChainOptions` object (not positional params)
- Constructor defensively copies and freezes entries: `this.entries = Object.freeze([...options.entries])`
- `getProvider()` throws `EMPTY_PROVIDER_CHAIN` error (retryable: false, severity: 'critical') when `entries` is empty
- `getProvider()` iterates entries in order, skipping circuit-open providers via `health.isHealthy(key)`
- Provider key construction uses `ProviderHealthTracker.buildKey(entry.provider, entry.model)` (centralized, not inline)
- `factory.create(entry)` is called to instantiate each provider (via `IAgentProviderFactory` interface)
- `provider.isAvailable()` is checked; if false, the entry is skipped
- When `isAvailable()` returns false, `health.recordFailure(key)` is called
- When all entries are exhausted, throws `NO_AVAILABLE_PROVIDER` with provider names only in message string; detailed per-provider errors in structured `context` (sanitized -- no raw `e.message` in the message string)
- `createProviderError()` from `./errors.js` is used for both error types
- Health dependency typed as `IProviderHealthTracker` (from `./types.js`), not concrete `ProviderHealthTracker`
- Factory dependency typed as `IAgentProviderFactory` (from `./agent-provider-factory.js`), not concrete `AgentProviderFactory`

## Implementation Details

### Technical Requirements

- [ ] Create `packages/providers/src/provider-chain.ts`
- [ ] Define `IProviderChain` interface:
  ```typescript
  export interface IProviderChain {
    getProvider(context: { agentType: AgentType; projectId: string; engineId: string }): Promise<IAgentProvider>;
  }
  ```
- [ ] Define `ProviderChainOptions` interface:
  ```typescript
  export interface ProviderChainOptions {
    entries: readonly ProviderChainEntry[];
    factory: IAgentProviderFactory;
    health: IProviderHealthTracker;
    diagnostics: DiagnosticsQueue;
    costTracker?: ICostTracker;
    logger?: ILogger;
  }
  ```
- [ ] Define `ProviderChain` class implementing `IProviderChain`
- [ ] Constructor accepts `ProviderChainOptions` and defensively copies/freezes entries
- [ ] Add JSDoc comment on the class documenting concurrency safety and no per-entry retry design
- [ ] Implement `getProvider()` with context parameter `{ agentType: AgentType; projectId: string; engineId: string }`
- [ ] Add empty-chain guard at the top of `getProvider()` using `createProviderError('EMPTY_PROVIDER_CHAIN', ..., false, 'critical')`
- [ ] Build provider key using `ProviderHealthTracker.buildKey(entry.provider, entry.model)` (import concrete class for static method only)
- [ ] Check `health.isHealthy(key)` before attempting each entry; skip with debug log if unhealthy
- [ ] Call `factory.create(entry)` to get an `IAgentProvider`
- [ ] Call `provider.isAvailable()` -- if false, call `health.recordFailure(key)` and continue
- [ ] Collect errors in structured array `errors: Array<{ provider: string; message: string }>` for the final throw
- [ ] After loop, throw `createProviderError('NO_AVAILABLE_PROVIDER', ...)` with sanitized message (provider names only) and errors in structured context

### Files to Modify/Create

- `packages/providers/src/provider-chain.ts` -- CREATE: IProviderChain, ProviderChainOptions, ProviderChain class

### Dependencies

- [ ] Story 9-3: `IProviderHealthTracker` from `./types.js` (interface)
- [ ] Story 9-3: `ProviderHealthTracker` from `./provider-health.js` (for `buildKey()` static method only)
- [ ] Story 9-3: `createProviderError` from `./errors.js`
- [ ] Story 9-4: `IAgentProviderFactory` from `./agent-provider-factory.js` (interface)
- [ ] Story 9-1: `ProviderChainEntry` from `@tamma/shared/src/types/agent-config.js`
- [ ] `IAgentProvider` from `./agent-types.js`
- [ ] `AgentType` from `@tamma/shared`
- [ ] `DiagnosticsQueue` from `@tamma/shared/src/telemetry/index.js`
- [ ] `ICostTracker`, `Provider` from `@tamma/cost-monitor` (typed but not used in this task)
- [ ] `ILogger` from `@tamma/shared/contracts`

## Testing Strategy

### Unit Tests

- [ ] Test: returns first healthy provider when first entry is healthy and available
- [ ] Test: skips unhealthy (circuit-open) provider, attempts second entry
- [ ] Test: skips provider where `isAvailable()` returns false, attempts next
- [ ] Test: `health.recordFailure(key)` called when `isAvailable()` returns false
- [ ] Test: throws `EMPTY_PROVIDER_CHAIN` when entries array is empty (retryable: false, severity: 'critical')
- [ ] Test: throws `NO_AVAILABLE_PROVIDER` when all entries are unhealthy
- [ ] Test: `NO_AVAILABLE_PROVIDER` error message includes provider names but not raw error messages
- [ ] Test: `NO_AVAILABLE_PROVIDER` error has structured context.errors array
- [ ] Test: logger.debug called with 'Skipping unhealthy provider' when provider is circuit-open
- [ ] Test: provider key uses `ProviderHealthTracker.buildKey()` (not inline template literal)
- [ ] Test: constructor accepts `ProviderChainOptions` object
- [ ] Test: entries are defensively copied and frozen (mutation of original array does not affect chain)
- [ ] Test: `ProviderChain` implements `IProviderChain`
- [ ] Test: health mock implements `IProviderHealthTracker` interface
- [ ] Test: factory mock implements `IAgentProviderFactory` interface

### Validation Steps

1. [ ] Create class file with all imports
2. [ ] Define `IProviderChain` and `ProviderChainOptions` interfaces
3. [ ] Implement constructor with `ProviderChainOptions` and defensive copy/freeze
4. [ ] Implement empty-chain guard
5. [ ] Implement health check loop using `ProviderHealthTracker.buildKey()`
6. [ ] Implement `factory.create()` and `isAvailable()` check
7. [ ] Implement `NO_AVAILABLE_PROVIDER` final throw with sanitized error message
8. [ ] Verify TypeScript compilation: `pnpm --filter @tamma/providers run typecheck`

## Notes & Considerations

- The `getProvider()` method returns a bare `IAgentProvider` in this task. Task 3 adds the `InstrumentedAgentProvider` wrapping.
- Budget checking is not wired in this task -- Task 2 adds the `costTracker.checkLimit()` call between the health check and `factory.create()`.
- Error handling (`try/catch` around `factory.create()` and `isAvailable()`) is partially here for the loop structure but the full `isProviderError()` type guard usage and `provider.dispose()` on error are completed in Tasks 3 and 4.
- The `errors[]` array is initialized in this task to collect errors for the final `NO_AVAILABLE_PROVIDER` message. It uses a structured format `{ provider, message }` instead of raw `Error` objects, so errors can be placed in the structured context without exposing raw error messages in the user-facing error string.
- Provider key format `provider:model` matches the format used by `ProviderHealthTracker.buildKey()` in Story 9-3. Key construction is centralized -- do NOT use inline template literals.
- The `IProviderChain` interface enables dependency inversion: Story 9-8's `RoleBasedAgentResolver` depends on the interface, not the concrete class.
- The `ProviderChainOptions` object pattern replaces the previous 6-positional-parameter constructor for clarity and extensibility.
- **Concurrency**: ProviderChain is safe for concurrent `getProvider()` calls because state management is delegated to injected services.
- **No per-entry retry**: Per-entry retries are not supported. Transient failures are handled by the circuit breaker's half-open recovery mechanism.

## Completion Checklist

- [ ] `IProviderChain` interface defined
- [ ] `ProviderChainOptions` interface defined
- [ ] `ProviderChain` class created implementing `IProviderChain`
- [ ] Constructor accepts `ProviderChainOptions` (not positional params)
- [ ] Entries defensively copied and frozen
- [ ] `getProvider()` method implemented with core loop
- [ ] Empty-chain guard throws `EMPTY_PROVIDER_CHAIN` (retryable: false, severity: 'critical')
- [ ] Health check via `IProviderHealthTracker.isHealthy(key)` implemented
- [ ] Key construction via `ProviderHealthTracker.buildKey()` (centralized)
- [ ] `factory.create(entry)` called via `IAgentProviderFactory` interface
- [ ] `isAvailable()` check implemented
- [ ] `NO_AVAILABLE_PROVIDER` thrown on exhaustion with sanitized message
- [ ] TypeScript compilation passes
- [ ] Unit tests for core iteration written and passing
