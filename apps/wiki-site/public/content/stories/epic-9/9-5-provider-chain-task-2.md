---
title: "Task 2: Add budget checking via ICostTracker"
sidebar:
  order: 90
---

**Story:** 9-5-provider-chain - Provider Chain
**Epic:** 9

## Task Description

Add budget checking to the `ProviderChain.getProvider()` method. Before attempting to create a provider via the factory, check `ICostTracker.checkLimit()` to verify the provider has not exceeded its budget. The check must safely validate the provider string against a known set before casting to the `Provider` type. If the budget is exceeded, skip the provider and log a warning. Unknown provider strings must not crash the budget check.

The budget check is wrapped in a try/catch with a **fail-closed policy**: if `checkLimit()` throws, the provider is skipped and a warning is logged.

## Acceptance Criteria

- Budget check occurs after health check but before `factory.create()`
- `costTracker.checkLimit()` is called with `{ provider, model, agentType }` from the entry and context
- Provider string is validated against the module-level `KNOWN_PROVIDERS` Set before casting to `Provider`
- `KNOWN_PROVIDERS` is a module-level constant (not re-created per iteration or inside the loop)
- If `limit.allowed` is false, the provider is skipped with a warning log including `key` and `percentUsed`
- If `costTracker` is not provided (undefined), budget check is skipped entirely
- Unknown provider strings (not in the known set) skip the budget check and proceed to `factory.create()`
- No unsafe `as Provider` cast without prior validation
- **Fail-closed policy**: if `checkLimit()` throws, log warning and skip the provider (do not crash or propagate the error)

## Implementation Details

### Technical Requirements

- [ ] Define `KNOWN_PROVIDERS` as a module-level `Set<string>` constant containing: `'anthropic'`, `'openai'`, `'openrouter'`, `'google'`, `'local'`, `'claude-code'`, `'opencode'`, `'z-ai'`, `'zen-mcp'`. Use `satisfies` for type safety if possible:
  ```typescript
  const KNOWN_PROVIDERS = new Set<string>([
    'anthropic', 'openai', 'openrouter', 'google', 'local',
    'claude-code', 'opencode', 'z-ai', 'zen-mcp',
  ] as const satisfies readonly string[]);
  ```
- [ ] Add budget check block inside the loop, after the health check and before `factory.create()`
- [ ] Wrap the entire budget check in `try/catch` for fail-closed policy
- [ ] Guard with `if (this.costTracker)` -- skip entirely if no cost tracker provided
- [ ] Guard with `if (KNOWN_PROVIDERS.has(entry.provider))` -- skip budget check for unknown providers
- [ ] Call `this.costTracker.checkLimit({ provider: entry.provider as Provider, model: entry.model, agentType: context.agentType })`
- [ ] If `!limit.allowed`, log warning and `continue` to next entry
- [ ] Warning log includes `{ key, percentUsed: limit.percentUsed }`
- [ ] In catch block, log warning with error details and `continue` (fail-closed):
  ```typescript
  catch (budgetErr) {
    this.logger?.warn('Budget check failed, skipping provider (fail-closed)', {
      key,
      error: budgetErr instanceof Error ? budgetErr.message : String(budgetErr),
    });
    continue;
  }
  ```

### Files to Modify/Create

- `packages/providers/src/provider-chain.ts` -- MODIFY: add module-level `KNOWN_PROVIDERS` constant and budget check to `getProvider()`

### Dependencies

- [ ] Task 1: Core ProviderChain class with `getProvider()` loop
- [ ] `ICostTracker` from `@tamma/cost-monitor` -- specifically the `checkLimit()` method
- [ ] `Provider` type from `@tamma/cost-monitor` -- the union type of known provider strings
- [ ] `LimitContext` and `LimitCheckResult` interfaces from `@tamma/cost-monitor/src/types.ts`

## Testing Strategy

### Unit Tests

- [ ] Test: provider skipped when `costTracker.checkLimit()` returns `{ allowed: false, percentUsed: 95 }`
- [ ] Test: provider NOT skipped when `costTracker.checkLimit()` returns `{ allowed: true, percentUsed: 50 }`
- [ ] Test: budget check not called when `costTracker` is undefined
- [ ] Test: budget check not called for unknown provider string (e.g., `'custom-provider'`)
- [ ] Test: budget check called with correct `LimitContext` shape (`{ provider, model, agentType }`)
- [ ] Test: logger.warn called with 'Budget exceeded for provider' when budget exceeded
- [ ] Test: warning log includes `key` and `percentUsed` in the logged object
- [ ] Test: if first provider exceeds budget, second provider is attempted and returned
- [ ] Test: all providers exceed budget -- throws `NO_AVAILABLE_PROVIDER`
- [ ] Test: `checkLimit()` throws -- provider skipped with warning log (fail-closed policy)
- [ ] Test: `checkLimit()` throws for unknown provider type -- provider skipped gracefully
- [ ] Test: `KNOWN_PROVIDERS` is module-level (not re-created per iteration)

### Validation Steps

1. [ ] Add module-level `KNOWN_PROVIDERS` Set constant
2. [ ] Add budget check block with try/catch (fail-closed)
3. [ ] Add `checkLimit()` call with proper `LimitContext`
4. [ ] Add skip logic with warning log
5. [ ] Add fail-closed catch block with warning log
6. [ ] Verify unknown provider strings do not crash
7. [ ] Verify TypeScript compilation: `pnpm --filter @tamma/providers run typecheck`
8. [ ] Run tests: `pnpm vitest run packages/providers/src/provider-chain`

## Notes & Considerations

- The `KNOWN_PROVIDERS` Set is defined at module level (not inside the method body or as a class field) to avoid re-creation on every `getProvider()` call or loop iteration. This is a performance optimization and code clarity improvement.
- The `Provider` type from `@tamma/cost-monitor` is a union of string literals. The Set-based check ensures type safety before the `as Provider` cast.
- The `LimitContext` interface accepts optional fields (`projectId`, `provider`, `agentType`, `model`, `estimatedCostUsd`). We pass `provider`, `model`, and `agentType`.
- **Fail-closed policy**: If `costTracker.checkLimit()` throws an error, the provider is skipped (not the error propagated). This prevents a misconfigured cost tracker from blocking all providers. The error is logged as a warning so operators can investigate.
- For unknown provider strings not in the `Provider` type, the `KNOWN_PROVIDERS.has()` check prevents the cast. However, if a new provider string is added to the cost-monitor but not to `KNOWN_PROVIDERS`, the budget check will be skipped for that provider. This is acceptable -- the alternative (crashing) is worse.
- The `percentUsed` in the warning log helps operators understand how close they are to the budget limit.

## Completion Checklist

- [ ] `KNOWN_PROVIDERS` Set defined at module level with all 9 known provider strings
- [ ] Budget check integrated into `getProvider()` loop (after health check, before factory.create)
- [ ] Budget check wrapped in try/catch with fail-closed policy
- [ ] `costTracker.checkLimit()` called with correct context
- [ ] Skip logic with `continue` on `!limit.allowed`
- [ ] Warning log with key and percentUsed on budget exceeded
- [ ] Warning log with error details on `checkLimit()` throw (fail-closed)
- [ ] No crash on unknown provider strings
- [ ] No unsafe `as Provider` cast without Set validation
- [ ] All budget-related unit tests passing
- [ ] TypeScript compilation successful
