---
title: "Task 4: Handle edge cases, disposal, and comprehensive tests"
sidebar:
  order: 90
---

**Story:** 9-5-provider-chain - Provider Chain
**Epic:** 9

## Task Description

Complete the `ProviderChain` implementation by adding resource disposal logic and writing the comprehensive test suite. When `isAvailable()` returns false, call `provider.dispose()` to prevent resource leaks. When an error occurs during `factory.create()` or `isAvailable()`, call `provider.dispose()` with logged errors (not silently swallowed). Write the full test suite covering all verification scenarios from the story specification plus additional edge cases.

## Acceptance Criteria

- `provider.dispose()` called when `isAvailable()` returns false (before recording failure and continuing)
- `provider.dispose()` called in the catch block when `provider` is defined (error during `isAvailable()`)
- Dispose errors are logged (not silently swallowed): `this.logger?.warn('Failed to dispose provider', { key, error: ... })`
- If `provider` was never assigned (error during `factory.create()` before assignment), dispose is not called
- If `InstrumentedAgentProvider` constructor throws, inner provider is disposed
- Full test suite covering all verification scenarios from the story plus edge cases
- Test file created at `packages/providers/src/provider-chain.test.ts`

## Implementation Details

### Technical Requirements

- [ ] Add `let provider: IAgentProvider | undefined;` declaration before `try` block
- [ ] When `isAvailable()` returns false: call `await provider.dispose()` before `health.recordFailure(key)` and `continue`
- [ ] In catch block: check `if (provider)` before calling dispose with logged errors:
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
- [ ] Create comprehensive test file `packages/providers/src/provider-chain.test.ts`
- [ ] Mock all dependencies using interfaces: `IAgentProviderFactory`, `IProviderHealthTracker`, `DiagnosticsQueue`, `ICostTracker`, `ILogger`
- [ ] Mock `IAgentProvider` with `executeTask`, `isAvailable`, `dispose` methods
- [ ] Use `ProviderChainOptions` object to construct `ProviderChain` in tests

### Files to Modify/Create

- `packages/providers/src/provider-chain.ts` -- MODIFY: add disposal logic with logging
- `packages/providers/src/provider-chain.test.ts` -- CREATE: comprehensive test suite

### Dependencies

- [ ] Tasks 1-3: Complete ProviderChain implementation
- [ ] Vitest 3.x for test runner
- [ ] All mock interfaces from upstream stories (`IProviderHealthTracker`, `IAgentProviderFactory`)

## Testing Strategy

### Unit Tests

The test file must cover all verification scenarios from the story specification:

**Core chain behavior:**
- [ ] Test: returns first healthy provider (first entry healthy and available)
- [ ] Test: skips unhealthy (circuit-open) provider, returns second entry
- [ ] Test: all providers fail -- throws `NO_AVAILABLE_PROVIDER` with provider names (not raw error messages in message string)
- [ ] Test: `NO_AVAILABLE_PROVIDER` error has structured `context.errors` array
- [ ] Test: empty chain -- throws `EMPTY_PROVIDER_CHAIN` error (retryable: false, severity: 'critical')

**Budget checking:**
- [ ] Test: budget exceeded -- skips provider, attempts next
- [ ] Test: unknown provider string does not crash budget check
- [ ] Test: `checkLimit()` throws -- provider skipped with warning log (fail-closed policy)

**Instrumentation:**
- [ ] Test: returned provider is `InstrumentedAgentProvider` (records usage via diagnostics queue)
- [ ] Test: `InstrumentedAgentProvider` constructor throw disposes inner provider

**Disposal:**
- [ ] Test: `provider.dispose()` called when `isAvailable()` returns false
- [ ] Test: `provider.dispose()` called on error (e.g., `isAvailable()` throws)
- [ ] Test: `provider.dispose()` NOT called when `factory.create()` throws (provider never assigned)
- [ ] Test: dispose error is logged via `logger.warn` (not silently swallowed)

**Error handling details:**
- [ ] Test: `ProviderError` passed to `recordFailure(key, err)` via `isProviderError()` type guard
- [ ] Test: plain `Error` causes `recordFailure(key)` called without error argument
- [ ] Test: non-Error thrown value (string) wrapped and collected in errors array

**No-recordSuccess invariant:**
- [ ] Test: `health.recordSuccess()` is NOT called after successful `isAvailable()` -- verify spy has zero calls

**Constructor and interface verification:**
- [ ] Test: `ProviderChain` implements `IProviderChain` interface
- [ ] Test: constructor accepts `ProviderChainOptions` object (not positional params)
- [ ] Test: entries are defensively copied and frozen (mutation of original array does not affect chain)
- [ ] Test: `ProviderHealthTracker.buildKey()` used for key construction (not inline template literal)

**Multi-entry scenarios:**
- [ ] Test: 3-entry chain where first is unhealthy, second fails isAvailable, third succeeds -- verify correct provider returned
- [ ] Test: chain with costTracker=undefined -- no budget check calls made
- [ ] Test: chain with logger=undefined -- no logger calls made (no crashes)

### Test Structure

```typescript
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { ProviderChain } from './provider-chain.js';
import type { IProviderChain, ProviderChainOptions } from './provider-chain.js';
import type { IProviderHealthTracker } from './types.js';
import type { IAgentProviderFactory } from './agent-provider-factory.js';

describe('ProviderChain', () => {
  // Mock setup with vi.fn() for all dependencies
  // Health mock implements IProviderHealthTracker interface
  // Factory mock implements IAgentProviderFactory interface

  describe('getProvider()', () => {
    describe('empty chain guard', () => { /* ... */ });
    describe('health checking', () => { /* ... */ });
    describe('budget checking', () => {
      // Includes fail-closed policy test
    });
    describe('provider creation and availability', () => { /* ... */ });
    describe('instrumentation wrapping', () => {
      // Includes InstrumentedAgentProvider constructor throw test
    });
    describe('error handling', () => { /* ... */ });
    describe('disposal', () => {
      // Includes logged dispose errors (not silent swallow)
    });
    describe('constructor', () => {
      // Defensive copy/freeze, options object
    });
    describe('multi-entry scenarios', () => { /* ... */ });
  });
});
```

### Validation Steps

1. [ ] Add disposal logic for `isAvailable() === false` path
2. [ ] Add disposal logic in catch block with error logging (not silent swallow)
3. [ ] Add `provider` variable declaration before try block
4. [ ] Create test file with all mock setup using interfaces (`IProviderHealthTracker`, `IAgentProviderFactory`)
5. [ ] Write all story verification tests
6. [ ] Write disposal-specific tests (including logged errors)
7. [ ] Write error handling edge case tests
8. [ ] Write budget check fail-closed policy test
9. [ ] Write `InstrumentedAgentProvider` constructor throw test
10. [ ] Write constructor/interface verification tests (options object, defensive copy, `IProviderChain`)
11. [ ] Write multi-entry scenario tests
12. [ ] Verify TypeScript compilation: `pnpm --filter @tamma/providers run typecheck`
13. [ ] Run full test suite: `pnpm vitest run packages/providers/src/provider-chain`
14. [ ] Verify coverage: all branches in `getProvider()` are hit

## Notes & Considerations

- The `provider` variable must be declared with `let` before the `try` block so it is accessible in the `catch` block. If `factory.create()` throws before assigning to `provider`, it remains `undefined` and the `if (provider)` check in catch prevents calling dispose on undefined.
- Dispose errors are logged via `this.logger?.warn('Failed to dispose provider', { key, error })` instead of being silently swallowed with `.catch(() => {})`. This helps operators diagnose resource leak issues.
- All mocks should use `vi.fn()` and be reset in `beforeEach()` to ensure test isolation.
- Mocks for health should implement `IProviderHealthTracker` (not reference the concrete `ProviderHealthTracker`). Mocks for factory should implement `IAgentProviderFactory` (not reference the concrete `AgentProviderFactory`).
- The `InstrumentedAgentProvider` returned by the chain can be verified by checking that it delegates `executeTask()` to the inner provider and emits diagnostics events. However, for the chain tests, verifying the constructor call arguments is sufficient.
- The `NO_AVAILABLE_PROVIDER` error message format is: `All {count} providers exhausted. Tried: {providerNames}` -- detailed per-provider errors are in the structured `context.errors` field (not in the message string).
- The `InstrumentedAgentProvider` constructor throw test verifies that if `new InstrumentedAgentProvider(...)` throws, the inner provider is still disposed. This is covered by the existing try/catch structure.

## Completion Checklist

- [ ] Disposal on `isAvailable() === false` implemented
- [ ] Disposal on error with logging (not silent swallow) implemented
- [ ] `provider` variable scoped correctly for catch block access
- [ ] Test file created at `packages/providers/src/provider-chain.test.ts`
- [ ] All story verification scenarios tested
- [ ] Disposal edge cases tested (false, error, factory throws, dispose logged)
- [ ] Budget check fail-closed policy tested
- [ ] `InstrumentedAgentProvider` constructor throw test included
- [ ] Error type guard scenarios tested (ProviderError vs plain Error)
- [ ] Constructor/interface verification tests (options object, defensive copy, `IProviderChain`)
- [ ] Multi-entry chain scenarios tested
- [ ] No-recordSuccess invariant tested
- [ ] All tests passing
- [ ] TypeScript compilation successful
- [ ] Code reviewed: final `provider-chain.ts` matches story design exactly
