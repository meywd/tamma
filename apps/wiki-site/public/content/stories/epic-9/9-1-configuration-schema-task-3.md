---
title: "Task 3: Fix mergeConfig() to Propagate Optional Fields"
sidebar:
  order: 90
---

**Story:** 9-1-configuration-schema - Multi-Agent Configuration Schema
**Epic:** 9

## Task Description

Fix the `mergeConfig()` function in `packages/cli/src/config.ts` to propagate all optional top-level fields that are currently silently dropped during config merging. The current implementation only merges `mode`, `logLevel`, `github`, `agent`, and `engine`. The new implementation must also propagate `agents`, `security`, `aiProviders`, `defaultProvider`, `elsa`, and `server` fields.

## Acceptance Criteria

- `mergeConfig()` shallow-merges `agents` field from base and override (base keys preserved, override keys win)
- `mergeConfig()` shallow-merges `security` field from base and override (base keys preserved, override keys win)
- `mergeConfig()` propagates `aiProviders` field from base or override
- `mergeConfig()` propagates `defaultProvider` field from base or override
- `mergeConfig()` propagates `elsa` field from base or override
- `mergeConfig()` propagates `server` field from base or override
- Override values take precedence over base values
- When neither base nor override has an optional field, it is not present in the result (no `undefined` injection)
- Existing required fields (`mode`, `logLevel`, `github`, `agent`, `engine`) continue to merge correctly

## Implementation Details

### Technical Requirements

- [ ] Replace the existing `mergeConfig()` function body in `packages/cli/src/config.ts`
- [ ] For `agents` and `security`, use shallow-merge pattern (so base keys are not lost when override only sets some keys):
  ```typescript
  ...(base.agents || override.agents
    ? { agents: { ...base.agents, ...override.agents } }
    : {}),
  ...(base.security || override.security
    ? { security: { ...base.security, ...override.security } }
    : {}),
  ```
- [ ] For remaining optional fields (`aiProviders`, `defaultProvider`, `elsa`, `server`), use override-wins pattern:
  ```typescript
  ...(base.aiProviders || override.aiProviders
    ? { aiProviders: override.aiProviders ?? base.aiProviders }
    : {}),
  ```
- [ ] Apply the appropriate pattern to all 6 optional fields: `agents` (shallow-merge), `security` (shallow-merge), `aiProviders` (override-wins), `defaultProvider` (override-wins), `elsa` (override-wins), `server` (override-wins)
- [ ] Keep the existing merge logic for required fields (`mode`, `logLevel`, `github`, `agent`, `engine`)
- [ ] The function signature remains unchanged: `(base: TammaConfig, override: Partial<TammaConfig>) => TammaConfig`

### Files to Modify/Create

- `packages/cli/src/config.ts` -- **MODIFY** -- Replace `mergeConfig()` function body

### Dependencies

- [ ] Task 2 must be completed first (TammaConfig must have the new fields)

## Testing Strategy

### Unit Tests

- [ ] Test: mergeConfig with base having `agents`, override empty -- result has base's `agents`
- [ ] Test: mergeConfig with base empty, override having `agents` -- result has override's `agents`
- [ ] Test: mergeConfig with both having `agents` -- result shallow-merges (base keys preserved, override keys win)
- [ ] Test: mergeConfig with neither having `agents` -- result does NOT have `agents` key
- [ ] Repeat above 4 patterns for `security` (also shallow-merge)
- [ ] Test: mergeConfig shallow-merge for security -- base has `{ sanitizeContent: true, validateUrls: true }`, override has `{ gateActions: true }` -- result has all three keys
- [ ] Repeat above 4 patterns for `aiProviders`
- [ ] Repeat above 4 patterns for `elsa`
- [ ] Repeat above 4 patterns for `server`
- [ ] Repeat above 4 patterns for `defaultProvider`
- [ ] Test: required fields (`mode`, `logLevel`, `github`, `agent`, `engine`) still merge correctly
- [ ] Test: full 3-layer merge (defaults -> file config -> env config) preserves all optional fields

### Validation Steps

1. [ ] Update the `mergeConfig()` function body
2. [ ] Run `pnpm --filter @tamma/cli run typecheck` -- must pass
3. [ ] Run existing config tests -- must still pass
4. [ ] Write new tests for optional field propagation
5. [ ] Run `pnpm --filter @tamma/cli test`
6. [ ] Manually verify with a config file containing `agents` and `security` that `loadConfig()` returns them

## Notes & Considerations

- Two merge strategies are used:
  - **Shallow-merge** (for `agents` and `security`): `{ ...base.X, ...override.X }` -- base keys are preserved, override keys win. This prevents env-var overrides from wiping out file-config keys.
  - **Override-wins** (for `aiProviders`, `defaultProvider`, `elsa`, `server`): `override.X ?? base.X` -- override replaces entire object.
- The conditional spread pattern `...(base.X || override.X ? { ... } : {})` ensures that if neither has the field, no key is added (clean output).
- This is a bug fix: the current `mergeConfig()` silently drops optional fields like `aiProviders`, `elsa`, and `server` that already exist on `TammaConfig`. This task fixes existing behavior in addition to supporting the new fields.
- Deep merging of nested `agents.roles` is NOT done -- shallow merge only applies one level deep. This keeps complexity manageable.
- The `mergeConfig` function is not exported (it is a private helper). Tests should exercise it through `loadConfig()` or by making it temporarily testable.

## Completion Checklist

- [ ] `mergeConfig()` propagates all 6 optional fields
- [ ] Override precedence works correctly for all fields
- [ ] No `undefined` values injected for missing optional fields
- [ ] Required field merging unchanged
- [ ] TypeScript compilation passes
- [ ] All existing tests still pass
- [ ] New tests for optional field propagation written and passing
