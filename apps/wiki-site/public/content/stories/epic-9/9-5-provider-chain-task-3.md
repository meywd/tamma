---
title: "Task 3: Add instrumentation wrapping and error handling"
sidebar:
  order: 90
---

**Story:** 9-5-provider-chain - Provider Chain
**Epic:** 9

## Task Description

Wrap the successfully obtained provider with `InstrumentedAgentProvider` before returning it from `getProvider()`. Add proper error handling around `factory.create()` and `isAvailable()` using `try/catch`, use the `isProviderError()` type guard to pass typed errors to `health.recordFailure()`, and collect errors for the final exhaustion message.

This task transforms the bare `IAgentProvider` return from Task 1 into a fully instrumented provider with proper error classification. Dispose errors are logged (not silently swallowed). The `NO_AVAILABLE_PROVIDER` error message is sanitized -- provider names only in the message string, detailed errors in structured context.

## Acceptance Criteria

- Returned provider is an `InstrumentedAgentProvider` wrapping the inner provider
- `InstrumentedAgentProvider` constructor receives `(provider, diagnostics, context)` where context includes `providerName`, `model`, `agentType`, `projectId`, `engineId`
- If `InstrumentedAgentProvider` constructor throws, the inner provider is disposed
- `try/catch` wraps `factory.create()` and `isAvailable()` calls
- On catch, `isProviderError()` type guard determines how to call `health.recordFailure()`:
  - If `isProviderError(err)` is true: call `health.recordFailure(key, err)` (with the ProviderError)
  - If `isProviderError(err)` is false: call `health.recordFailure(key)` (without error argument)
- Caught errors are collected in structured format `{ provider, message }` for the context, not raw Error objects
- Dispose errors on catch are logged via `this.logger?.warn('Failed to dispose provider', ...)`, not silently swallowed
- `NO_AVAILABLE_PROVIDER` error message contains only provider names; detailed errors are in structured `context.errors`
- Does NOT call `health.recordSuccess()` after `isAvailable()` -- this is explicitly by design
- Success recording is the responsibility of `InstrumentedAgentProvider` on actual task completion

## Implementation Details

### Technical Requirements

- [ ] Import `InstrumentedAgentProvider` from `./instrumented-agent-provider.js`
- [ ] Import `isProviderError` from `./errors.js`
- [ ] Wrap `factory.create(entry)` and `provider.isAvailable()` in a `try/catch` block
- [ ] On successful `isAvailable()`, wrap provider:
  ```typescript
  return new InstrumentedAgentProvider(provider, this.diagnostics, {
    providerName: entry.provider,
    model: entry.model ?? 'default',
    agentType: context.agentType,
    projectId: context.projectId,
    engineId: context.engineId,
  });
  ```
- [ ] In catch block, log dispose errors (not silently swallow):
  ```typescript
  if (provider) {
    await provider.dispose().catch((disposeErr) => {
      this.logger?.warn('Failed to dispose provider', {
        key,
        error: disposeErr instanceof Error ? disposeErr.message : String(disposeErr),
      });
    });
  }
  ```
- [ ] In catch block, use `isProviderError(err)` type guard:
  - `true`: `this.health.recordFailure(key, err)`
  - `false`: `this.health.recordFailure(key)`
- [ ] Collect errors in structured format:
  ```typescript
  errors.push({
    provider: entry.provider,
    message: err instanceof Error ? err.message : String(err),
  });
  ```
- [ ] Throw sanitized `NO_AVAILABLE_PROVIDER`:
  ```typescript
  // NO_AVAILABLE_PROVIDER: retryable is false because the chain itself
  // does not retry. Callers (engine) should retry the entire chain
  // resolution with backoff if all providers are temporarily circuit-open.
  throw createProviderError(
    'NO_AVAILABLE_PROVIDER',
    `All ${this.entries.length} providers exhausted. Tried: ${this.entries.map(e => e.provider).join(', ')}`,
    false,
    'critical',
    { errors },
  );
  ```
- [ ] Verify no `recordSuccess()` call exists after `isAvailable()` (explicit design decision)

### Files to Modify/Create

- `packages/providers/src/provider-chain.ts` -- MODIFY: add instrumentation wrapping and error handling

### Dependencies

- [ ] Task 1: Core ProviderChain class with `getProvider()` loop
- [ ] Task 2: Budget checking (already in the loop)
- [ ] Story 9-2: `InstrumentedAgentProvider` from `./instrumented-agent-provider.js`
- [ ] Story 9-3: `isProviderError` from `./errors.js`
- [ ] Story 9-2: `DiagnosticsQueue` (already a constructor dependency)

## Testing Strategy

### Unit Tests

- [ ] Test: returned provider is instance of `InstrumentedAgentProvider`
- [ ] Test: `InstrumentedAgentProvider` receives correct context `{ providerName, model, agentType, projectId, engineId }`
- [ ] Test: `InstrumentedAgentProvider` receives the `diagnostics` queue from the constructor
- [ ] Test: when `factory.create()` throws a `ProviderError`, `health.recordFailure(key, err)` is called with the error
- [ ] Test: when `factory.create()` throws a plain `Error`, `health.recordFailure(key)` is called without error argument
- [ ] Test: when `isAvailable()` throws a `ProviderError`, `health.recordFailure(key, err)` is called with the error
- [ ] Test: when `isAvailable()` throws a plain `Error`, `health.recordFailure(key)` is called without error argument
- [ ] Test: non-Error thrown values (e.g., string) are wrapped in structured format
- [ ] Test: `NO_AVAILABLE_PROVIDER` error message contains provider names only (not raw error messages)
- [ ] Test: `NO_AVAILABLE_PROVIDER` error has structured `context.errors` array with per-provider details
- [ ] Test: `health.recordSuccess()` is NOT called after successful `isAvailable()` (verify with mock spy)
- [ ] Test: dispose errors are logged via logger.warn (not silently swallowed)
- [ ] Test: `InstrumentedAgentProvider` constructor throw disposes inner provider

### Validation Steps

1. [ ] Add `InstrumentedAgentProvider` wrapping on successful `isAvailable()`
2. [ ] Add `try/catch` around `factory.create()` and `isAvailable()`
3. [ ] Implement `isProviderError()` branching in catch block
4. [ ] Implement structured error collection
5. [ ] Implement logged dispose errors (not silent swallow)
6. [ ] Implement sanitized `NO_AVAILABLE_PROVIDER` throw with comment about retryable rationale
7. [ ] Verify no `recordSuccess()` call exists
8. [ ] Add test for `InstrumentedAgentProvider` constructor throw
9. [ ] Verify TypeScript compilation: `pnpm --filter @tamma/providers run typecheck`
10. [ ] Run tests: `pnpm vitest run packages/providers/src/provider-chain`

## Notes & Considerations

- The `InstrumentedAgentProvider` context does not include `taskId` or `taskType` -- these are unknown at chain resolution time. They are set later when the provider is used to execute a specific task.
- The `model` in the instrumentation context defaults to `'default'` when `entry.model` is undefined, matching the provider key format from `ProviderHealthTracker.buildKey()`.
- The catch block must handle both `ProviderError` (which has `code`, `retryable`, `severity`) and plain `Error`. The `isProviderError()` type guard from `errors.ts` checks for `code: string`, `retryable: boolean`, and `severity` properties.
- Recording success at the chain level was considered and explicitly rejected. The chain only determines availability; actual task success is recorded by `InstrumentedAgentProvider` when `executeTask()` completes.
- **Sanitized error messages**: The `NO_AVAILABLE_PROVIDER` error message does NOT include raw `e.message` from provider errors. Raw error messages may contain sensitive information (API keys, file paths, internal URLs). Instead, detailed error info is placed in the structured `context.errors` field.
- **NO_AVAILABLE_PROVIDER retryable rationale**: `retryable` is `false` because the chain itself does not retry. Callers (engine) should retry the entire chain resolution with backoff if all providers are temporarily circuit-open.
- **Dispose error logging**: Dispose errors in the catch block are logged via `this.logger?.warn()` instead of being silently swallowed with `.catch(() => {})`. This helps operators diagnose resource leak issues.
- **InstrumentedAgentProvider constructor throw**: If the `InstrumentedAgentProvider` constructor throws (e.g., due to invalid context), the inner provider must be disposed to prevent resource leaks. This should be covered by the existing catch block since the `new InstrumentedAgentProvider(...)` call is inside the try block.

## Completion Checklist

- [ ] `InstrumentedAgentProvider` wrapping added to successful path
- [ ] `InstrumentedAgentProvider` receives correct context object
- [ ] Inner provider disposed if `InstrumentedAgentProvider` constructor throws
- [ ] `try/catch` wraps `factory.create()` and `isAvailable()`
- [ ] `isProviderError()` type guard used in catch block
- [ ] `health.recordFailure()` called with error for ProviderError, without for plain Error
- [ ] Structured error collection in `errors[]` array
- [ ] Sanitized `NO_AVAILABLE_PROVIDER` error (no raw error messages in message string)
- [ ] Comment explaining `retryable: false` rationale
- [ ] Dispose errors logged, not silently swallowed
- [ ] No `recordSuccess()` call exists
- [ ] All instrumentation and error handling tests passing
- [ ] TypeScript compilation successful
