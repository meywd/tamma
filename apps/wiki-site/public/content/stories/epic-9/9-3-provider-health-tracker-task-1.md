---
title: "Task 1: Extract Shared Error Factory"
sidebar:
  order: 90
---

**Story:** 9-3-provider-health-tracker - Provider Health Tracker
**Epic:** 9

## Task Description

Extract the duplicated `createProviderError()` function from `openrouter-provider.ts` and `zen-mcp-provider.ts` into a new shared module `packages/providers/src/errors.ts`. Extend `createProviderError()` with optional `context` and `retryAfter` parameters. Add an `isProviderError()` type guard that distinguishes `ProviderError` from plain `Error` by checking for `code`, `retryable`, AND `severity` properties (the `severity` check reduces false positives from Node.js system errors). Write unit tests for both functions.

## Acceptance Criteria

- `createProviderError()` is exported from `packages/providers/src/errors.ts`
- `isProviderError()` type guard is exported from `packages/providers/src/errors.ts`
- `createProviderError()` returns a `ProviderError` with correct `code`, `message`, `retryable`, and `severity` fields
- `createProviderError()` defaults `severity` to `'medium'` when not specified
- `createProviderError()` accepts optional `context?: Record<string, unknown>` parameter and sets it on the error when provided
- `createProviderError()` accepts optional `retryAfter?: number` parameter and sets it on the error when provided
- `isProviderError()` returns `true` for `ProviderError` instances created by the factory
- `isProviderError()` returns `false` for plain `Error` instances and non-Error values
- `isProviderError()` checks `'severity' in err` in addition to `code` and `retryable` to reduce false positives from Node.js system errors
- Unit tests cover all edge cases

## Implementation Details

### Technical Requirements

- [ ] Create `packages/providers/src/errors.ts` with `createProviderError()` and `isProviderError()`
- [ ] Import `ProviderError` type from `./types.js`
- [ ] `createProviderError()` must accept `context?: Record<string, unknown>` as an optional 5th parameter
- [ ] `createProviderError()` must accept `retryAfter?: number` as an optional 6th parameter
- [ ] When `context` is provided, set `error.context = context`
- [ ] When `retryAfter` is provided, set `error.retryAfter = retryAfter`
- [ ] `isProviderError()` must check: `err instanceof Error`, `'code' in err` with `typeof === 'string'`, `'retryable' in err` with `typeof === 'boolean'`, AND `'severity' in err` (to exclude Node.js system errors that have `code` and might coincidentally have `retryable`)
- [ ] Create `packages/providers/src/errors.test.ts` with comprehensive tests

### Code Reference

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

### Files to Modify/Create

- **CREATE** `packages/providers/src/errors.ts` -- shared error factory and type guard
- **CREATE** `packages/providers/src/errors.test.ts` -- unit tests

### Dependencies

- `packages/providers/src/types.ts` -- `ProviderError` interface (already exists at line 296)
- No external dependencies

## Testing Strategy

### Unit Tests

Tests in `packages/providers/src/errors.test.ts`:

- [ ] `createProviderError()` returns an Error instance with code, retryable, severity set
- [ ] `createProviderError()` uses provided severity value
- [ ] `createProviderError()` defaults severity to `'medium'` when omitted
- [ ] `createProviderError()` message is accessible via `.message`
- [ ] `createProviderError()` sets `context` when provided
- [ ] `createProviderError()` does NOT set `context` property when not provided (property absent)
- [ ] `createProviderError()` sets `retryAfter` when provided
- [ ] `createProviderError()` does NOT set `retryAfter` property when not provided (property absent)
- [ ] `createProviderError()` sets both `context` and `retryAfter` together when both provided
- [ ] `isProviderError()` returns `true` for a `ProviderError` created by `createProviderError()`
- [ ] `isProviderError()` returns `false` for a plain `new Error('test')`
- [ ] `isProviderError()` returns `false` for `null`
- [ ] `isProviderError()` returns `false` for `undefined`
- [ ] `isProviderError()` returns `false` for a string
- [ ] `isProviderError()` returns `false` for a number
- [ ] `isProviderError()` returns `false` for an object with `code` but no `retryable`
- [ ] `isProviderError()` returns `false` for an Error with `code` as number (not string)
- [ ] `isProviderError()` returns `false` for an Error with `code` (string) and `retryable` (boolean) but NO `severity` (Node.js system error false positive)

### Validation Steps

1. [ ] Create `errors.ts` with both functions (including optional `context` and `retryAfter` params)
2. [ ] Create `errors.test.ts` with all tests (including `severity` check tests)
3. [ ] Run `pnpm test --filter @tamma/providers -- errors.test` and confirm all pass
4. [ ] Verify TypeScript compilation: `pnpm build --filter @tamma/providers`

## Notes & Considerations

- The `zen-mcp-provider.ts` version has a slightly different return type annotation (`Error & { code: string; retryable: boolean; severity: string }`) compared to `openrouter-provider.ts` which uses `ProviderError`. The shared version should use the `ProviderError` type from `types.ts` which is the canonical type.
- The `isProviderError()` type guard will be used by `ProviderHealthTracker.recordFailure()` in Task 3 to check the `retryable` flag -- non-retryable ProviderErrors are NOT counted toward the circuit breaker threshold.
- The `'severity' in err` check in `isProviderError()` is important to reduce false positives from Node.js system errors (e.g., `ECONNREFUSED`) that have a `code` property (string) and might coincidentally have a `retryable` property.
- Both functions should use `.js` extension in import paths for ESM compatibility.
- The optional `context` parameter enables attaching structured metadata to errors (e.g., request IDs, model names).
- The optional `retryAfter` parameter enables providers to signal how long to wait before retrying (e.g., from HTTP 429 Retry-After headers).

## Completion Checklist

- [ ] `packages/providers/src/errors.ts` created with `createProviderError()` (with optional `context`/`retryAfter`) and `isProviderError()` (with `'severity' in err` check)
- [ ] `packages/providers/src/errors.test.ts` created with comprehensive tests (including severity false positive and context/retryAfter tests)
- [ ] All tests passing
- [ ] TypeScript compilation successful
- [ ] No lint errors
