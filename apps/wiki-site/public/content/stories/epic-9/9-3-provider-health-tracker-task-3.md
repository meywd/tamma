---
title: "Task 3: Implement ProviderHealthTracker with Circuit Breaker"
sidebar:
  order: 90
---

**Story:** 9-3-provider-health-tracker - Provider Health Tracker
**Epic:** 9

## Task Description

Add the `IProviderHealthTracker` and `HealthStatusEntry` interfaces to `packages/providers/src/types.ts`. Then implement the `ProviderHealthTracker` class in `packages/providers/src/provider-health.ts` that implements `IProviderHealthTracker`. This class provides a circuit breaker per provider+model key with:
- Sliding window failure tracking with timestamp capping
- Configurable thresholds with constructor validation
- Half-open probing with thundering herd prevention and probe timeout
- Key validation (format and length)
- maxTrackedKeys limit to prevent unbounded memory growth
- Non-retryable error exclusion from circuit breaker
- Short-circuit when circuit already open
- `onCircuitChange` callback for diagnostics integration
- `reset(key)` and `clear()` methods for operational recovery
- Static `buildKey()` helper for consistent key construction

Write comprehensive unit tests covering all circuit breaker state transitions and new features.

## Acceptance Criteria

- `IProviderHealthTracker` interface is defined in `packages/providers/src/types.ts` with `isHealthy`, `recordFailure`, `recordSuccess`, `getStatus`, `reset`, `clear` methods
- `HealthStatusEntry` interface is defined in `packages/providers/src/types.ts` with `healthy`, `failures`, `circuitOpen` fields
- `ProviderHealthTracker` class implements `IProviderHealthTracker` and is exported from `packages/providers/src/provider-health.ts`
- Constructor validates all numeric inputs: `failureThreshold` >= 1 (positive integer), `failureWindowMs` >= 1000, `circuitOpenDurationMs` >= 1000, `halfOpenProbeTimeoutMs` >= 1000, `maxTrackedKeys` >= 1 (positive integer); all must be finite. Throws `Error` with descriptive message on invalid input.
- `isHealthy(key)` validates key, returns `true` for unknown keys and healthy keys
- `isHealthy(key)` returns `false` when circuit is open and not yet expired
- `isHealthy(key)` returns `true` for the FIRST caller when circuit transitions to half-open, then `false` for subsequent callers (thundering herd prevention)
- `isHealthy(key)` auto-resets stuck half-open probes after `halfOpenProbeTimeoutMs` (default 30s) -- prevents permanently stuck half-open state
- `recordFailure(key, error?)` skips non-retryable `ProviderError` instances (config/caller problems, not provider health)
- `recordFailure(key)` short-circuits when circuit is already open and not in half-open state (no-op)
- `recordFailure(key)` enforces `maxTrackedKeys` limit -- silently rejects new keys at capacity
- `recordFailure(key, error?)` adds failure timestamps to sliding window, caps array to `failureThreshold * 2`, and opens circuit when threshold is reached
- `recordFailure(key)` during half-open state immediately re-opens the circuit
- `recordFailure(key)` prunes timestamps outside the sliding window
- `recordSuccess(key)` during half-open state fully closes circuit and resets all failure timestamps
- `reset(key)` deletes health state for a single key
- `clear()` deletes all health state
- `static buildKey(provider, model?)` returns `"provider:model"` or `"provider:default"`
- Key validation: rejects keys > 256 chars or with invalid characters (must match `[a-zA-Z0-9._\-:/]+`)
- `onCircuitChange` callback fires on state transitions (open, half-open, closed)
- All state is in-memory only; no persistence (intentional design decision)

## Implementation Details

### Technical Requirements

**Step 1: Add interfaces to types.ts**

- [ ] Add `IProviderHealthTracker` interface to `packages/providers/src/types.ts`:
  ```typescript
  export interface IProviderHealthTracker {
    isHealthy(key: string): boolean;
    recordFailure(key: string, error?: Error): void;
    recordSuccess(key: string): void;
    getStatus(): Record<string, HealthStatusEntry>;
    reset(key: string): void;
    clear(): void;
  }

  export interface HealthStatusEntry {
    healthy: boolean;
    failures: number;
    circuitOpen: boolean;
  }
  ```

**Step 2: Create provider-health.ts**

- [ ] Create `packages/providers/src/provider-health.ts`
- [ ] Import `ProviderError`, `IProviderHealthTracker`, `HealthStatusEntry` from `./types.js` and `isProviderError` from `./errors.js`
- [ ] Define private `HealthEntry` interface with `failureTimestamps`, `circuitOpen`, `circuitOpenUntil`, `halfOpenInProgress`, `halfOpenStartedAt`
- [ ] Define key validation constants: `KEY_PATTERN = /^[a-zA-Z0-9._\-:/]+$/`, `MAX_KEY_LENGTH = 256`
- [ ] Use `Map<string, HealthEntry>` for tracking state per key
- [ ] Implement constructor with validation:
  - `failureThreshold` must be >= 1, a positive integer, and finite
  - `failureWindowMs` must be >= 1000 and finite
  - `circuitOpenDurationMs` must be >= 1000 and finite
  - `halfOpenProbeTimeoutMs` must be >= 1000 and finite (default: 30_000)
  - `maxTrackedKeys` must be >= 1, a positive integer, and finite (default: 1000)
  - Accept optional `onCircuitChange?: (key: string, state: 'open' | 'half-open' | 'closed') => void`
  - Throw `Error` with descriptive message on any invalid input
- [ ] Implement `static buildKey(provider: string, model?: string): string`
  - Returns `"provider:model"` or `"provider:default"`
- [ ] Implement `private validateKey(key: string): void`
  - Throw if key length > 256 or key does not match pattern
- [ ] Implement `isHealthy(key: string): boolean`
  - Validate key
  - Unknown key -> return `true`
  - Circuit not open -> return `true`
  - Circuit open and not expired -> return `false`
  - Circuit expired (half-open): check probe timeout first
    - If `halfOpenInProgress` and `Date.now() - halfOpenStartedAt > halfOpenProbeTimeoutMs`: auto-reset to open, fire `onCircuitChange('open')`, return `false`
    - If `halfOpenInProgress` (not timed out): return `false` (thundering herd)
    - Otherwise: set `halfOpenInProgress = true`, `halfOpenStartedAt = Date.now()`, fire `onCircuitChange('half-open')`, return `true`
- [ ] Implement `recordFailure(key: string, error?: Error | ProviderError): void`
  - Validate key
  - If error is a ProviderError with `retryable === false`: return immediately (non-retryable errors are config/caller problems, not health issues)
  - Create entry if key not in map, enforcing `maxTrackedKeys` limit (silently reject new keys at capacity)
  - If `halfOpenInProgress` is `true`: set `halfOpenInProgress = false`, re-open circuit, fire `onCircuitChange('open')`, return early
  - If circuit is already open and not expired (not half-open): return immediately (short-circuit, no-op)
  - Otherwise: add `Date.now()` to `failureTimestamps`, prune timestamps older than `failureWindowMs`, cap array to `failureThreshold * 2`, check threshold, fire `onCircuitChange('open')` if threshold reached
- [ ] Implement `recordSuccess(key: string): void`
  - Validate key
  - If entry exists and was open: fire `onCircuitChange('closed')`
  - Set `halfOpenInProgress = false`, `halfOpenStartedAt = 0`, `circuitOpen = false`, `circuitOpenUntil = 0`, clear `failureTimestamps`
- [ ] Implement `reset(key: string): void` -- deletes single key from map
- [ ] Implement `clear(): void` -- clears all keys from map
- [ ] Create `packages/providers/src/provider-health.test.ts`

### Files to Modify/Create

- **MODIFY** `packages/providers/src/types.ts` -- add `IProviderHealthTracker` and `HealthStatusEntry` interfaces
- **CREATE** `packages/providers/src/provider-health.ts` -- ProviderHealthTracker class implementing IProviderHealthTracker
- **CREATE** `packages/providers/src/provider-health.test.ts` -- unit tests

### Dependencies

- [ ] Task 1: `packages/providers/src/errors.ts` must exist with `isProviderError` exported
- [ ] `packages/providers/src/types.ts` -- `ProviderError` interface, plus new `IProviderHealthTracker` and `HealthStatusEntry`

## Testing Strategy

### Unit Tests

Tests in `packages/providers/src/provider-health.test.ts`:

**Constructor validation:**
- [ ] Constructor with default options succeeds
- [ ] Constructor throws on `failureThreshold` = 0
- [ ] Constructor throws on `failureThreshold` = -1
- [ ] Constructor throws on `failureThreshold` = 1.5 (non-integer)
- [ ] Constructor throws on `failureThreshold` = Infinity
- [ ] Constructor throws on `failureThreshold` = NaN
- [ ] Constructor throws on `failureWindowMs` < 1000
- [ ] Constructor throws on `failureWindowMs` = Infinity
- [ ] Constructor throws on `circuitOpenDurationMs` < 1000
- [ ] Constructor throws on `circuitOpenDurationMs` = NaN
- [ ] Constructor throws on `halfOpenProbeTimeoutMs` < 1000
- [ ] Constructor throws on `maxTrackedKeys` = 0

**Key validation:**
- [ ] `isHealthy()` throws on key longer than 256 characters
- [ ] `isHealthy()` throws on key with spaces or special characters
- [ ] `recordFailure()` throws on invalid key
- [ ] `recordSuccess()` throws on invalid key
- [ ] `reset()` throws on invalid key
- [ ] Valid key formats accepted: `"openrouter:z-ai/z1-mini"`, `"anthropic:claude-opus-4"`

**Static buildKey:**
- [ ] `buildKey('openrouter', 'z-ai/z1-mini')` returns `"openrouter:z-ai/z1-mini"`
- [ ] `buildKey('openrouter')` returns `"openrouter:default"`
- [ ] `buildKey('anthropic', undefined)` returns `"anthropic:default"`

**Basic health checks:**
- [ ] `isHealthy()` returns `true` for unknown key (no entry in map)
- [ ] `isHealthy()` returns `true` after fewer failures than threshold
- [ ] `isHealthy()` returns `false` after threshold failures within window

**Sliding window:**
- [ ] Failures outside the window do not count toward threshold
- [ ] Old timestamps are pruned when new failures are recorded
- [ ] Exactly threshold failures within window trips circuit
- [ ] `failureTimestamps` array is capped to `failureThreshold * 2` after filtering

**Circuit open behavior:**
- [ ] `isHealthy()` returns `false` while circuit is open and not expired
- [ ] `recordFailure()` during open circuit (not half-open) is a no-op (short-circuit)

**Half-open behavior:**
- [ ] After `circuitOpenDurationMs` expires, first `isHealthy()` call returns `true` and sets `halfOpenInProgress`
- [ ] Second `isHealthy()` call while half-open probe is in progress returns `false` (thundering herd prevention)
- [ ] `recordSuccess()` after half-open probe fully closes circuit (circuitOpen = false, failureTimestamps = [])
- [ ] `recordFailure()` after half-open probe immediately re-opens circuit with fresh duration

**Half-open probe timeout:**
- [ ] If probe is in progress and `halfOpenProbeTimeoutMs` has elapsed, `isHealthy()` auto-resets to open state and returns `false`
- [ ] After probe timeout reset, a new probe can be started on the next `isHealthy()` call after `circuitOpenDurationMs`

**Non-retryable error exclusion:**
- [ ] `recordFailure(key, nonRetryableProviderError)` does NOT count toward threshold (where `error.retryable === false`)
- [ ] `recordFailure(key, retryableProviderError)` DOES count toward threshold (where `error.retryable === true`)
- [ ] `recordFailure(key, plainError)` DOES count toward threshold (plain Error, not ProviderError)
- [ ] `recordFailure(key)` with no error DOES count toward threshold

**maxTrackedKeys limit:**
- [ ] When `maxTrackedKeys` is reached, new keys are silently rejected by `recordFailure()`
- [ ] Existing keys continue to be tracked after limit is reached
- [ ] After `reset(key)` frees a slot, new keys can be tracked again

**recordFailure with error types:**
- [ ] `recordFailure(key)` works with no error argument
- [ ] `recordFailure(key, new Error('test'))` works with plain Error
- [ ] `recordFailure(key, providerError)` works with retryable ProviderError

**recordSuccess behavior:**
- [ ] `recordSuccess()` on unknown key is a no-op
- [ ] `recordSuccess()` resets `failureTimestamps` to empty array
- [ ] `recordSuccess()` sets `circuitOpen` to `false`

**reset() and clear():**
- [ ] `reset(key)` removes single key from tracking; `isHealthy(key)` returns `true` after reset
- [ ] `reset(key)` on unknown key is a no-op (no throw)
- [ ] `clear()` removes all keys; `getStatus()` returns empty Record
- [ ] After `clear()`, all keys report as healthy

**onCircuitChange callback:**
- [ ] Callback fires with `'open'` when circuit transitions from closed to open
- [ ] Callback fires with `'half-open'` when circuit transitions to half-open (first probe allowed)
- [ ] Callback fires with `'closed'` when `recordSuccess()` closes a previously open circuit
- [ ] Callback fires with `'open'` when half-open probe fails and circuit re-opens
- [ ] Callback fires with `'open'` when half-open probe times out and resets to open
- [ ] No callback when `onCircuitChange` is not provided (optional)
- [ ] Callback receives correct key

**Custom options:**
- [ ] Custom `failureThreshold` is honored
- [ ] Custom `failureWindowMs` is honored
- [ ] Custom `circuitOpenDurationMs` is honored
- [ ] Custom `halfOpenProbeTimeoutMs` is honored
- [ ] Custom `maxTrackedKeys` is honored

**Note in test comments:** Circuit breaker state is in-memory only. Process restart resets all state. This is an intentional design decision.

### Validation Steps

1. [ ] Add `IProviderHealthTracker` and `HealthStatusEntry` interfaces to `types.ts`
2. [ ] Create `provider-health.ts` with `ProviderHealthTracker` class implementing `IProviderHealthTracker`
3. [ ] Implement all methods: `isHealthy()`, `recordFailure()`, `recordSuccess()`, `getStatus()`, `reset()`, `clear()`, `static buildKey()`
4. [ ] Create `provider-health.test.ts` with all tests above
5. [ ] Run `pnpm test --filter @tamma/providers -- provider-health.test` and confirm all pass
6. [ ] Verify TypeScript compilation: `pnpm build --filter @tamma/providers`

## Notes & Considerations

- Use `Date.now()` for timestamps. Tests should use `vi.useFakeTimers()` / `vi.advanceTimersByTime()` from Vitest to control time-dependent behavior.
- The `error` parameter in `recordFailure()` is now actively used: non-retryable `ProviderError` instances (checked via `isProviderError()` and `error.retryable === false`) are excluded from the circuit breaker threshold because they indicate configuration or caller problems, not provider health issues.
- Key format convention is `"provider:model"` (e.g., `"openrouter:z-ai/z1-mini"`). This is now enforced by key validation (max 256 chars, pattern `[a-zA-Z0-9._\-:/]+`). Use `ProviderHealthTracker.buildKey()` for consistent key construction.
- The `HealthEntry` interface is private to the module. It should not be exported.
- All mutations are synchronous (no async). This is single-threaded Node.js, so the half-open guard (`halfOpenInProgress`) is safe without locks.
- The `halfOpenStartedAt` field tracks when the half-open probe started. If `Date.now() - halfOpenStartedAt > halfOpenProbeTimeoutMs`, the probe is considered stuck and auto-resets to open state. This prevents permanently stuck half-open state if the probe caller crashes or forgets to call `recordSuccess`/`recordFailure`.
- The `maxTrackedKeys` limit (default: 1000) prevents unbounded memory growth from rapid creation of provider+model keys. When the limit is reached, new keys are silently rejected. This is a safety net, not an expected operating mode.
- The `failureTimestamps` array is capped to `failureThreshold * 2` after filtering to prevent O(N^2) performance degradation from rapid failure injection.
- The `onCircuitChange` callback enables diagnostics integration without coupling to `DiagnosticsQueue`.

## Completion Checklist

- [ ] `IProviderHealthTracker` and `HealthStatusEntry` interfaces added to `packages/providers/src/types.ts`
- [ ] `packages/providers/src/provider-health.ts` created with `ProviderHealthTracker` implementing `IProviderHealthTracker`
- [ ] Constructor validation implemented (throws on invalid inputs)
- [ ] Key validation implemented (format and length)
- [ ] `static buildKey()` implemented
- [ ] `isHealthy()` implemented with circuit breaker logic and half-open probe timeout
- [ ] `recordFailure()` implemented with non-retryable skip, already-open short-circuit, maxTrackedKeys, timestamps cap, sliding window, half-open re-open
- [ ] `recordSuccess()` implemented with circuit close and reset
- [ ] `reset()` and `clear()` implemented
- [ ] `onCircuitChange` callback firing on state transitions
- [ ] `packages/providers/src/provider-health.test.ts` created
- [ ] All circuit breaker state transition tests passing
- [ ] Constructor and key validation tests passing
- [ ] TypeScript compilation successful
- [ ] No lint errors
