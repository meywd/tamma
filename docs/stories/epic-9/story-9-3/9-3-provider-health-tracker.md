# Story 3: Provider Health Tracker

## Goal
Circuit breaker per provider+model. Track failures and mark unhealthy providers so the fallback chain skips them. Feed failure data into diagnostics.

## Acceptance Criteria (Additions)

- Circuit breaker state is in-memory only. Process restart resets all state. This is an intentional design decision.

## Design

**IProviderHealthTracker interface: `packages/providers/src/types.ts`**

Story 9-5's `ProviderChain` must depend on the interface, not the concrete class. Add this interface to `packages/providers/src/types.ts` (or alongside the class):

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

**Extract shared error factory: `packages/providers/src/errors.ts`**

`createProviderError()` is currently duplicated in `openrouter-provider.ts` and `zen-mcp-provider.ts`. Extract to a shared module:

```typescript
import type { ProviderError } from './types.js';

export function createProviderError(
  code: string,
  message: string,
  retryable: boolean,
  severity: 'low' | 'medium' | 'high' | 'critical' = 'medium',
  context?: Record<string, unknown>,
  retryAfter?: number,
): ProviderError {
  const error = new Error(message) as ProviderError;
  error.code = code;
  error.retryable = retryable;
  error.severity = severity;
  if (context !== undefined) error.context = context;
  if (retryAfter !== undefined) error.retryAfter = retryAfter;
  return error;
}

/** Type guard: distinguish ProviderError from plain Error.
 *  Checks 'severity' in addition to 'code' and 'retryable' to reduce
 *  false positives from Node.js system errors that may coincidentally
 *  have 'code' and 'retryable' properties.
 */
export function isProviderError(err: unknown): err is ProviderError {
  return (
    err instanceof Error &&
    'code' in err &&
    typeof (err as ProviderError).code === 'string' &&
    'retryable' in err &&
    typeof (err as ProviderError).retryable === 'boolean' &&
    'severity' in err
  );
}
```

Then update `openrouter-provider.ts` and `zen-mcp-provider.ts` to `import { createProviderError } from './errors.js'` and delete their local copies.

**New file: `packages/providers/src/provider-health.ts`**

```typescript
import type { ProviderError, IProviderHealthTracker, HealthStatusEntry } from './types.js';
import { isProviderError } from './errors.js';

interface HealthEntry {
  failureTimestamps: number[];        // Sliding window of failure times
  circuitOpen: boolean;
  circuitOpenUntil: number;
  halfOpenInProgress: boolean;        // Prevents half-open race
  halfOpenStartedAt: number;          // Epoch ms when half-open probe started
}

const KEY_PATTERN = /^[a-zA-Z0-9._\-:/]+$/;
const MAX_KEY_LENGTH = 256;

export class ProviderHealthTracker implements IProviderHealthTracker {
  private health = new Map<string, HealthEntry>();

  private failureThreshold: number;
  private failureWindowMs: number;
  private circuitOpenDurationMs: number;
  private halfOpenProbeTimeoutMs: number;
  private maxTrackedKeys: number;
  private onCircuitChange?: (key: string, state: 'open' | 'half-open' | 'closed') => void;

  constructor(options?: {
    failureThreshold?: number;        // default: 5, must be >= 1
    failureWindowMs?: number;         // default: 60_000, must be >= 1000
    circuitOpenDurationMs?: number;   // default: 300_000, must be >= 1000
    halfOpenProbeTimeoutMs?: number;  // default: 30_000, must be >= 1000
    maxTrackedKeys?: number;          // default: 1000, must be >= 1
    onCircuitChange?: (key: string, state: 'open' | 'half-open' | 'closed') => void;
  }) {
    const threshold = options?.failureThreshold ?? 5;
    const windowMs = options?.failureWindowMs ?? 60_000;
    const openMs = options?.circuitOpenDurationMs ?? 300_000;
    const probeMs = options?.halfOpenProbeTimeoutMs ?? 30_000;
    const maxKeys = options?.maxTrackedKeys ?? 1000;

    // Validate constructor inputs
    if (!Number.isFinite(threshold) || !Number.isInteger(threshold) || threshold < 1) {
      throw new Error(`failureThreshold must be a positive integer >= 1, got ${threshold}`);
    }
    if (!Number.isFinite(windowMs) || windowMs < 1000) {
      throw new Error(`failureWindowMs must be a finite number >= 1000, got ${windowMs}`);
    }
    if (!Number.isFinite(openMs) || openMs < 1000) {
      throw new Error(`circuitOpenDurationMs must be a finite number >= 1000, got ${openMs}`);
    }
    if (!Number.isFinite(probeMs) || probeMs < 1000) {
      throw new Error(`halfOpenProbeTimeoutMs must be a finite number >= 1000, got ${probeMs}`);
    }
    if (!Number.isFinite(maxKeys) || !Number.isInteger(maxKeys) || maxKeys < 1) {
      throw new Error(`maxTrackedKeys must be a positive integer >= 1, got ${maxKeys}`);
    }

    this.failureThreshold = threshold;
    this.failureWindowMs = windowMs;
    this.circuitOpenDurationMs = openMs;
    this.halfOpenProbeTimeoutMs = probeMs;
    this.maxTrackedKeys = maxKeys;
    this.onCircuitChange = options?.onCircuitChange;
  }

  /**
   * Build a standardized health key from provider and optional model.
   * Returns "provider:model" or "provider:default".
   */
  static buildKey(provider: string, model?: string): string {
    return `${provider}:${model ?? 'default'}`;
  }

  /**
   * Validate key format: max 256 chars, alphanumeric with . _ - : /
   */
  private validateKey(key: string): void {
    if (key.length > MAX_KEY_LENGTH) {
      throw new Error(`Health tracker key too long (max ${MAX_KEY_LENGTH}): ${key.slice(0, 50)}...`);
    }
    if (!KEY_PATTERN.test(key)) {
      throw new Error(`Health tracker key contains invalid characters: ${key.slice(0, 50)}`);
    }
  }

  /**
   * key = "provider:model" e.g. "openrouter:z-ai/z1-mini"
   * Use ProviderHealthTracker.buildKey() to construct keys.
   *
   * NOTE: Circuit state is in-memory only. Restarting the process resets
   * all health entries to healthy. This is an intentional design decision
   * because provider outages are typically transient.
   */
  isHealthy(key: string): boolean {
    this.validateKey(key);
    const entry = this.health.get(key);
    if (!entry) return true;

    if (!entry.circuitOpen) return true;

    if (Date.now() < entry.circuitOpenUntil) return false;

    // Half-open probe timeout: if the probe caller crashed or forgot to
    // call recordSuccess/recordFailure, auto-reset to open state.
    if (entry.halfOpenInProgress) {
      if (Date.now() - entry.halfOpenStartedAt > this.halfOpenProbeTimeoutMs) {
        // Probe timed out -- reset to open and allow a new probe
        entry.halfOpenInProgress = false;
        entry.circuitOpen = true;
        entry.circuitOpenUntil = Date.now() + this.circuitOpenDurationMs;
        this.onCircuitChange?.(key, 'open');
        return false;
      }
      // Another caller is already probing; block to prevent thundering herd
      return false;
    }

    entry.halfOpenInProgress = true;
    entry.halfOpenStartedAt = Date.now();
    this.onCircuitChange?.(key, 'half-open');
    return true;
  }

  /**
   * Record a failure. Accepts Error | ProviderError.
   *
   * If the error is a ProviderError with retryable === false, it is NOT
   * counted toward the circuit breaker. Non-retryable errors indicate
   * configuration or caller problems, not provider health issues.
   */
  recordFailure(key: string, error?: Error | ProviderError): void {
    this.validateKey(key);

    // Non-retryable ProviderErrors are config/caller problems, not health issues.
    // Do not count them toward the circuit breaker threshold.
    if (error && isProviderError(error) && error.retryable === false) {
      return;
    }

    const now = Date.now();
    let entry = this.health.get(key);

    if (!entry) {
      // Enforce maxTrackedKeys limit to prevent unbounded memory growth
      if (this.health.size >= this.maxTrackedKeys) {
        return; // Silently reject new keys when at capacity
      }
      entry = {
        failureTimestamps: [],
        circuitOpen: false,
        circuitOpenUntil: 0,
        halfOpenInProgress: false,
        halfOpenStartedAt: 0,
      };
      this.health.set(key, entry);
    }

    // If we were in half-open and the probe failed, re-open immediately
    if (entry.halfOpenInProgress) {
      entry.halfOpenInProgress = false;
      entry.circuitOpen = true;
      entry.circuitOpenUntil = now + this.circuitOpenDurationMs;
      this.onCircuitChange?.(key, 'open');
      return;
    }

    // Short-circuit: if circuit is already open (not half-open), no need
    // to track more failures. The circuit is already tripped.
    if (entry.circuitOpen && now < entry.circuitOpenUntil) {
      return;
    }

    // Add to sliding window, prune old entries
    entry.failureTimestamps.push(now);
    const windowStart = now - this.failureWindowMs;
    entry.failureTimestamps = entry.failureTimestamps.filter(t => t >= windowStart);

    // Cap the array to prevent O(N^2) degradation from rapid failure injection
    const maxTimestamps = this.failureThreshold * 2;
    if (entry.failureTimestamps.length > maxTimestamps) {
      entry.failureTimestamps = entry.failureTimestamps.slice(-maxTimestamps);
    }

    // Check threshold
    if (entry.failureTimestamps.length >= this.failureThreshold) {
      entry.circuitOpen = true;
      entry.circuitOpenUntil = now + this.circuitOpenDurationMs;
      this.onCircuitChange?.(key, 'open');
    }
  }

  recordSuccess(key: string): void {
    this.validateKey(key);
    const entry = this.health.get(key);
    if (!entry) return;

    const wasOpen = entry.circuitOpen;

    // If half-open probe succeeded, fully close circuit
    entry.halfOpenInProgress = false;
    entry.halfOpenStartedAt = 0;
    entry.circuitOpen = false;
    entry.circuitOpenUntil = 0;
    entry.failureTimestamps = [];

    if (wasOpen) {
      this.onCircuitChange?.(key, 'closed');
    }
  }

  /**
   * Returns a JSON-serializable Record (not Map) with health status for
   * all tracked provider+model keys within the current window.
   */
  getStatus(): Record<string, HealthStatusEntry> {
    const result: Record<string, HealthStatusEntry> = {};
    const now = Date.now();

    for (const [key, entry] of this.health) {
      const windowStart = now - this.failureWindowMs;
      const recentFailures = entry.failureTimestamps.filter(t => t >= windowStart).length;
      const isOpen = entry.circuitOpen && now < entry.circuitOpenUntil;

      result[key] = {
        healthy: !isOpen,
        failures: recentFailures,
        circuitOpen: isOpen,
      };
    }

    return result;
  }

  /** Delete health state for a single key. Useful for operational recovery. */
  reset(key: string): void {
    this.validateKey(key);
    this.health.delete(key);
  }

  /** Clear all health state. Useful for operational recovery and testing. */
  clear(): void {
    this.health.clear();
  }
}
```

## Files
- MODIFY `packages/providers/src/types.ts` -- add `IProviderHealthTracker` and `HealthStatusEntry` interfaces
- CREATE `packages/providers/src/errors.ts` -- extract `createProviderError()` (with optional `context`/`retryAfter` params) + `isProviderError()` (with `'severity' in err` check) (deduplicate from openrouter and zen-mcp providers)
- MODIFY `packages/providers/src/openrouter-provider.ts` -- import from `./errors.js`, delete local `createProviderError()`
- MODIFY `packages/providers/src/zen-mcp-provider.ts` -- import from `./errors.js`, delete local `createProviderError()`
- CREATE `packages/providers/src/provider-health.ts` -- implements `IProviderHealthTracker`
- CREATE `packages/providers/src/provider-health.test.ts`

## Verify
- Test: 5 failures within 60s opens circuit
- Test: failures outside window do not count (sliding window prunes old timestamps)
- Test: circuit closes after 300s (half-open)
- Test: half-open allows exactly ONE probe (`halfOpenInProgress` blocks concurrent probes)
- Test: half-open probe success fully closes circuit and resets failures
- Test: half-open probe failure immediately re-opens circuit
- Test: `recordFailure()` accepts both plain `Error` and `ProviderError` (type guard)
- Test: `recordFailure()` with non-retryable `ProviderError` does NOT count toward circuit breaker
- Test: `recordFailure()` short-circuits when circuit is already open (not half-open)
- Test: `recordFailure()` caps `failureTimestamps` array to `failureThreshold * 2`
- Test: `recordFailure()` respects `maxTrackedKeys` limit (rejects new keys at capacity)
- Test: success resets all failure timestamps
- Test: `getStatus()` returns `Record<string, HealthStatusEntry>` (JSON-serializable) with correct counts within window
- Test: `reset(key)` removes single key; `clear()` removes all keys
- Test: `buildKey('openrouter', 'z-ai/z1-mini')` returns `"openrouter:z-ai/z1-mini"`; `buildKey('openrouter')` returns `"openrouter:default"`
- Test: constructor validates inputs -- throws on invalid `failureThreshold`, `failureWindowMs`, `circuitOpenDurationMs`
- Test: half-open probe timeout auto-resets to open state after `halfOpenProbeTimeoutMs`
- Test: `onCircuitChange` callback fires on state transitions (open, half-open, closed)
- Test: key validation rejects keys > 256 chars or with invalid characters
- Regression: run ALL provider tests after `errors.ts` extraction (`pnpm test --filter @tamma/providers`), not just new test files
- Note in test comments: circuit state is in-memory only -- process restart resets all state (intentional design decision)

## Logging Requirements

All provider-layer modules MUST use `ILogger` from `@tamma/shared/contracts` (not `console.log`).

- **INFO**: Provider initialization, config loaded, provider selected, chain fallback triggered
- **DEBUG**: Request parameters (redact API keys), response metadata, cache hits
- **WARN**: Provider degraded, rate limited, circuit breaker tripped, fallback to next provider
- **ERROR**: Provider call failed, config validation error, all providers exhausted
- **Structured context**: Always include `{ provider, model, issueId, duration }` where applicable
- **Credential safety**: NEVER log API keys, tokens, or secrets — log provider name and endpoint only
