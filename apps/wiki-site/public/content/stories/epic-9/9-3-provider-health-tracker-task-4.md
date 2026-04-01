---
title: "Task 4: Implement getStatus() Reporting and Finalize Exports"
sidebar:
  order: 90
---

**Story:** 9-3-provider-health-tracker - Provider Health Tracker
**Epic:** 9

## Task Description

Add the `getStatus()` method to `ProviderHealthTracker` that returns a JSON-serializable `Record<string, HealthStatusEntry>` (not `Map`) snapshot of health status for all tracked provider+model keys. Update the barrel exports in `packages/providers/src/index.ts` to export the new modules (`errors.ts` and `provider-health.ts`) and the new interfaces (`IProviderHealthTracker`, `HealthStatusEntry`). Write unit tests for `getStatus()` and validate the complete test suite -- run ALL provider tests, not just new files.

## Acceptance Criteria

- `getStatus()` returns a `Record<string, HealthStatusEntry>` (JSON-serializable, not `Map`) for all tracked keys
- `getStatus()` counts only failures within the current sliding window
- `getStatus()` correctly reports `healthy: false` when circuit is open and not expired
- `getStatus()` correctly reports `healthy: true` when circuit has expired (even if was previously open)
- `index.ts` exports `createProviderError` and `isProviderError` from `./errors.js`
- `index.ts` exports `ProviderHealthTracker` from `./provider-health.js`
- `index.ts` exports `IProviderHealthTracker` and `HealthStatusEntry` types from `./types.js` (or re-exports)
- Full provider test suite passes including all new and existing tests (`pnpm test --filter @tamma/providers`)

## Implementation Details

### Technical Requirements

- [ ] Add `getStatus()` method to `ProviderHealthTracker` in `provider-health.ts`
  - Iterate over all entries in the `health` Map
  - For each entry, prune `failureTimestamps` to current window and count
  - Determine `circuitOpen` as `entry.circuitOpen && Date.now() < entry.circuitOpenUntil`
  - Set `healthy` as `!circuitOpen` (inverse of open state)
  - Return `Record<string, HealthStatusEntry>` (plain object, NOT `Map`) for JSON serialization compatibility
- [ ] Update `packages/providers/src/index.ts` to add exports:
  - `export { createProviderError, isProviderError } from './errors.js';`
  - `export { ProviderHealthTracker } from './provider-health.js';`
  - `export type { IProviderHealthTracker, HealthStatusEntry } from './types.js';` (if defined there; otherwise re-export from provider-health.js)
- [ ] Write `getStatus()` unit tests in `provider-health.test.ts`

### Code Reference

```typescript
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
```

### Files to Modify/Create

- **MODIFY** `packages/providers/src/provider-health.ts` -- add `getStatus()` method (if not already added in Task 3)
- **MODIFY** `packages/providers/src/index.ts` -- add barrel exports for errors, provider-health, and interfaces
- **MODIFY** `packages/providers/src/provider-health.test.ts` -- add `getStatus()` tests

### Dependencies

- [ ] Task 3: `ProviderHealthTracker` class must exist with `isHealthy()`, `recordFailure()`, `recordSuccess()`, `reset()`, `clear()`
- [ ] Task 1: `errors.ts` must exist for barrel export
- [ ] Task 2: Provider updates must be complete to validate full suite

## Testing Strategy

### Unit Tests

Additional tests in `packages/providers/src/provider-health.test.ts`:

**getStatus() basic behavior:**
- [ ] `getStatus()` returns empty `Record` (empty object `{}`) when no keys have been tracked
- [ ] `getStatus()` returns entry for a key after `recordFailure()` is called
- [ ] `getStatus()` reports `{ healthy: true, failures: N, circuitOpen: false }` when below threshold

**getStatus() with open circuit:**
- [ ] `getStatus()` reports `{ healthy: false, failures: N, circuitOpen: true }` when circuit is open
- [ ] `getStatus()` reports `{ healthy: true, failures: N, circuitOpen: false }` after circuit duration expires

**getStatus() windowed failure count:**
- [ ] `getStatus()` counts only failures within the sliding window (old ones pruned)
- [ ] `getStatus()` reports `failures: 0` when all failures have aged out of the window

**getStatus() multiple keys:**
- [ ] `getStatus()` returns independent status for multiple provider+model keys
- [ ] One key can be unhealthy while another is healthy

**getStatus() after recovery:**
- [ ] `getStatus()` reports `{ healthy: true, failures: 0, circuitOpen: false }` after `recordSuccess()` resets circuit

**getStatus() JSON serialization:**
- [ ] `JSON.stringify(tracker.getStatus())` succeeds (Record is JSON-serializable, unlike Map)
- [ ] Deserialized result matches original structure

**getStatus() after reset/clear:**
- [ ] `getStatus()` does not include a key after `reset(key)` is called
- [ ] `getStatus()` returns empty `Record` after `clear()` is called

### Validation Steps

1. [ ] Add `getStatus()` method to `ProviderHealthTracker` (returning `Record`, not `Map`)
2. [ ] Add unit tests for `getStatus()` (including JSON serialization test)
3. [ ] Update `index.ts` with new exports (including `IProviderHealthTracker` and `HealthStatusEntry` types)
4. [ ] Run `pnpm test --filter @tamma/providers` -- **full test suite** must pass (ALL test files, not just new/modified ones)
5. [ ] Verify TypeScript compilation: `pnpm build --filter @tamma/providers`
6. [ ] Verify exports work: import `{ createProviderError, isProviderError, ProviderHealthTracker }` and `type { IProviderHealthTracker, HealthStatusEntry }` from the package

## Notes & Considerations

- `getStatus()` is read-only and does not mutate state. It creates a new `Record` with snapshot data.
- **Return type is `Record<string, HealthStatusEntry>`, NOT `Map`**. This is a deliberate choice for JSON serialization compatibility. `Map` objects do not serialize to JSON without explicit conversion. APIs, logging, and diagnostics consumers expect plain objects.
- The `failures` count in the status reflects only failures within the current `failureWindowMs`, not lifetime failures.
- The `healthy` field is the inverse of `circuitOpen` in the status. A key that has never been tracked will not appear in the status Record at all (callers should treat missing keys as healthy).
- The barrel export order in `index.ts` should follow the existing pattern: types first, then utility modules, then classes.
- Export `IProviderHealthTracker` and `HealthStatusEntry` as type exports so downstream consumers (like Story 9-5's `ProviderChain`) can depend on the interface, not the concrete class.
- After this task, Story 9-3 is complete. The `ProviderHealthTracker` (implementing `IProviderHealthTracker`) is ready to be consumed by Story 9-5 (Provider Chain).

## Completion Checklist

- [ ] `getStatus()` method added to `ProviderHealthTracker` returning `Record<string, HealthStatusEntry>` (not `Map`)
- [ ] `getStatus()` unit tests written and passing (including JSON serialization and reset/clear interaction)
- [ ] `index.ts` updated with exports for `errors.ts`, `provider-health.ts`, and `IProviderHealthTracker`/`HealthStatusEntry` types
- [ ] **Full provider test suite passing** (`pnpm test --filter @tamma/providers` -- ALL test files, not just new ones)
- [ ] TypeScript compilation successful (`pnpm build --filter @tamma/providers`)
- [ ] No lint errors
- [ ] Story 9-3 complete -- all 4 tasks done
