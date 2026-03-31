---
title: "Task 2: Update Existing Providers to Use Shared Error Factory"
sidebar:
  order: 90
---

**Story:** 9-3-provider-health-tracker - Provider Health Tracker
**Epic:** 9

## Task Description

Replace the local `createProviderError()` function in both `openrouter-provider.ts` and `zen-mcp-provider.ts` with an import from the shared `errors.ts` module created in Task 1. Delete the local copies. Verify ALL existing provider tests continue to pass -- run the full provider test suite, not just the modified files.

## Acceptance Criteria

- `openrouter-provider.ts` imports `createProviderError` from `./errors.js` instead of defining it locally
- `zen-mcp-provider.ts` imports `createProviderError` from `./errors.js` instead of defining it locally
- The local `createProviderError` function is fully removed from both files
- All existing tests in `openrouter-provider.test.ts` pass unchanged
- All existing tests in `zen-mcp-provider.test.ts` pass unchanged
- ALL provider tests pass (`pnpm test --filter @tamma/providers`) -- not just the modified files
- No behavioral changes to either provider

## Implementation Details

### Technical Requirements

- [ ] Add `import { createProviderError } from './errors.js';` to `openrouter-provider.ts`
- [ ] Remove the local `createProviderError` function from `openrouter-provider.ts` (lines 21-32)
- [ ] Add `import { createProviderError } from './errors.js';` to `zen-mcp-provider.ts`
- [ ] Remove the local `createProviderError` function from `zen-mcp-provider.ts` (lines 13-28)
- [ ] Ensure `mapHttpError()` in `openrouter-provider.ts` continues to work (it calls `createProviderError`)
- [ ] Ensure all `ensureInitialized()` and `sendMessageSync()` error paths in `zen-mcp-provider.ts` continue to work

### Files to Modify/Create

- **MODIFY** `packages/providers/src/openrouter-provider.ts` -- replace local function with import
- **MODIFY** `packages/providers/src/zen-mcp-provider.ts` -- replace local function with import

### Dependencies

- [ ] Task 1: `packages/providers/src/errors.ts` must exist with `createProviderError` exported

## Testing Strategy

### Unit Tests

No new tests needed. This is a pure refactoring task.

### Regression Tests

**IMPORTANT: Run ALL provider tests, not just the modified files.** The error factory extraction could have subtle type effects on other providers that import from the same types.

- [ ] Run `pnpm test --filter @tamma/providers -- openrouter-provider.test` -- all existing tests must pass
- [ ] Run `pnpm test --filter @tamma/providers -- zen-mcp-provider.test` -- all existing tests must pass
- [ ] Run `pnpm test --filter @tamma/providers` -- **full provider test suite** must pass (all test files, not just new/modified ones)

### Validation Steps

1. [ ] Update import in `openrouter-provider.ts`
2. [ ] Delete local `createProviderError` from `openrouter-provider.ts`
3. [ ] Update import in `zen-mcp-provider.ts`
4. [ ] Delete local `createProviderError` from `zen-mcp-provider.ts`
5. [ ] Run full test suite: `pnpm test --filter @tamma/providers` (ALL tests, not just modified files)
6. [ ] Verify TypeScript compilation: `pnpm build --filter @tamma/providers`

## Notes & Considerations

- The `openrouter-provider.ts` version returns `ProviderError` type. The `zen-mcp-provider.ts` version returns `Error & { code: string; retryable: boolean; severity: string }`. Both are structurally identical at runtime; the shared version uses the canonical `ProviderError` type from `types.ts`, which is a superset. This means `zen-mcp-provider.ts` may no longer need the inline type annotation if it relied on the local one.
- The `mapHttpError()` helper in `openrouter-provider.ts` is local to that file and calls `createProviderError()`. It does NOT need to be extracted -- it is specific to OpenRouter HTTP status mapping.
- The `ProviderError` type import in `openrouter-provider.ts` (line 11) can remain since it is also used for the `mapError()` method return type.
- This task is a pure refactoring with no behavioral changes. If any test fails, it indicates a type mismatch that must be resolved before proceeding.
- The shared `createProviderError()` now has optional `context` and `retryAfter` parameters (added in Task 1). These are backward-compatible -- existing call sites do not need to pass them.

## Completion Checklist

- [ ] `openrouter-provider.ts` updated to import from `./errors.js`
- [ ] Local `createProviderError` removed from `openrouter-provider.ts`
- [ ] `zen-mcp-provider.ts` updated to import from `./errors.js`
- [ ] Local `createProviderError` removed from `zen-mcp-provider.ts`
- [ ] All existing openrouter-provider tests passing
- [ ] All existing zen-mcp-provider tests passing
- [ ] **Full provider test suite passing** (`pnpm test --filter @tamma/providers` -- ALL test files)
- [ ] TypeScript compilation successful
