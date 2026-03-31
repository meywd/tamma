---
title: "Task 4: Update Cost-Monitor Types and Create Provider Name Mapping"
sidebar:
  order: 90
---

**Story:** 9-2-provider-diagnostics - Provider Diagnostics
**Epic:** 9

## Task Description

Update the `Provider` type in `packages/cost-monitor/src/types.ts` to include the three missing provider identifiers (`opencode`, `z-ai`, `zen-mcp`), and replace the locally-defined `AgentType` with an import from `@tamma/shared` to eliminate type divergence between packages. Re-export `AgentType` so that existing consumers importing it from `@tamma/cost-monitor` continue to work.

Additionally, create a `mapProviderName()` utility in `packages/providers/src/provider-name-mapping.ts` that safely maps provider name strings to the `Provider` type, replacing unsafe `as Provider` casts throughout the codebase.

## Acceptance Criteria

- `Provider` type includes `'opencode' | 'z-ai' | 'zen-mcp'` in addition to existing values
- Local `AgentType` type definition is removed from `cost-monitor/src/types.ts`
- `AgentType` is imported from `@tamma/shared` and re-exported
- All existing references to `Provider` and `AgentType` within cost-monitor continue to compile
- `mapProviderName()` validates provider names against the known `Provider` values set
- `mapProviderName()` returns a safe default (`'claude-code'`) for unrecognized provider names instead of using unsafe `as Provider` cast
- No runtime behavior changes for existing types -- this is a types-only modification for cost-monitor

## Implementation Details

### Technical Requirements

#### Cost-Monitor Types Update

- [ ] Add `'opencode'`, `'z-ai'`, `'zen-mcp'` to the `Provider` type union:
  ```typescript
  export type Provider =
    | 'anthropic'
    | 'openai'
    | 'openrouter'
    | 'google'
    | 'local'
    | 'claude-code'
    | 'opencode'
    | 'z-ai'
    | 'zen-mcp';
  ```
- [ ] Remove the local `AgentType` type definition (lines 22-31 of current file)
- [ ] Add import at top of file: `import type { AgentType } from '@tamma/shared';`
- [ ] Add re-export: `export type { AgentType };`
- [ ] Verify that `UsageRecord`, `UsageRecordInput`, `UsageFilter`, `LimitContext` and any other types referencing `AgentType` still compile

#### Provider Name Mapping Utility

- [ ] Create `packages/providers/src/provider-name-mapping.ts` with:
  ```typescript
  import type { Provider } from '@tamma/cost-monitor';

  const KNOWN_PROVIDERS: ReadonlySet<string> = new Set<Provider>([
    'anthropic', 'openai', 'openrouter', 'google', 'local',
    'claude-code', 'opencode', 'z-ai', 'zen-mcp',
  ]);

  const DEFAULT_PROVIDER: Provider = 'claude-code';

  /**
   * Map a provider name string to the Provider type safely.
   * Returns the validated Provider value or defaults to 'claude-code'
   * if the name is not recognized.
   *
   * Replaces unsafe `as Provider` casts throughout the codebase.
   */
  export function mapProviderName(name: string | undefined): Provider {
    if (name && KNOWN_PROVIDERS.has(name)) {
      return name as Provider;
    }
    return DEFAULT_PROVIDER;
  }
  ```

### Files to Modify/Create

- MODIFY `packages/cost-monitor/src/types.ts`
- CREATE `packages/providers/src/provider-name-mapping.ts`
- CREATE `packages/providers/src/provider-name-mapping.test.ts`

### Dependencies

- [ ] `@tamma/shared` already listed as a dependency in `packages/cost-monitor/package.json`
- [ ] `AgentType` exported from `packages/shared/src/types/knowledge.ts` via the shared barrel
- [ ] `@tamma/cost-monitor` already a dependency of `packages/providers` (added in Task 5)

## Testing Strategy

### Unit Tests — cost-monitor types

- [ ] Test that `Provider` type accepts `'opencode'` (compile-time assertion)
- [ ] Test that `Provider` type accepts `'z-ai'` (compile-time assertion)
- [ ] Test that `Provider` type accepts `'zen-mcp'` (compile-time assertion)
- [ ] Test that `Provider` type still accepts all existing values (`'anthropic'`, `'openai'`, `'openrouter'`, `'google'`, `'local'`, `'claude-code'`)
- [ ] Test that `AgentType` imported from `@tamma/cost-monitor` is assignable to `AgentType` from `@tamma/shared` (type-level check)
- [ ] Test that existing `UsageRecordInput` still accepts `agentType: 'implementer'` (representative value)

### Unit Tests — mapProviderName

- [ ] Test `mapProviderName('anthropic')` returns `'anthropic'`
- [ ] Test `mapProviderName('openai')` returns `'openai'`
- [ ] Test `mapProviderName('openrouter')` returns `'openrouter'`
- [ ] Test `mapProviderName('google')` returns `'google'`
- [ ] Test `mapProviderName('local')` returns `'local'`
- [ ] Test `mapProviderName('claude-code')` returns `'claude-code'`
- [ ] Test `mapProviderName('opencode')` returns `'opencode'`
- [ ] Test `mapProviderName('z-ai')` returns `'z-ai'`
- [ ] Test `mapProviderName('zen-mcp')` returns `'zen-mcp'`
- [ ] Test `mapProviderName('unknown-provider')` returns `'claude-code'` (default)
- [ ] Test `mapProviderName(undefined)` returns `'claude-code'` (default)
- [ ] Test `mapProviderName('')` returns `'claude-code'` (default for empty string)
- [ ] Test `mapProviderName('ANTHROPIC')` returns `'claude-code'` (case-sensitive, uppercase not recognized)

### Validation Steps

1. [ ] Update `Provider` type with new provider identifiers
2. [ ] Remove local `AgentType` definition
3. [ ] Add import and re-export of `AgentType` from `@tamma/shared`
4. [ ] Create `provider-name-mapping.ts` with `mapProviderName()` function
5. [ ] Run `pnpm typecheck` in `packages/cost-monitor` to verify compilation
6. [ ] Run `pnpm typecheck` in `packages/providers` to verify compilation
7. [ ] Run `pnpm typecheck` across the monorepo to catch any cross-package issues
8. [ ] Run existing cost-monitor tests to verify no regressions
9. [ ] Write and run `mapProviderName` tests

## Notes & Considerations

- The `AgentType` values in `@tamma/shared` (`packages/shared/src/types/knowledge.ts`) and the local definition in cost-monitor are currently identical: `'scrum_master' | 'architect' | 'researcher' | 'analyst' | 'planner' | 'implementer' | 'reviewer' | 'tester' | 'documenter'`. This change eliminates the duplication so that future additions to `AgentType` only need to happen in one place.
- The re-export (`export type { AgentType }`) ensures backward compatibility. Any code doing `import { AgentType } from '@tamma/cost-monitor'` will continue to work.
- The three new providers align with the `PROVIDER_TYPES` constants already defined in `packages/providers/src/types.ts`: `OPENCODE`, `Z_AI`, `ZEN_MCP`.
- The `mapProviderName()` function replaces unsafe `as Provider` casts that appear in the diagnostics processor and other code. It validates the provider name against the known set and returns a safe default for unrecognized names, preventing runtime type violations.
- The `KNOWN_PROVIDERS` set is defined as `ReadonlySet<string>` with the initializer using `Set<Provider>` to ensure all known Provider values are included at compile time.
- The cost-monitor types update is a types-only change with no runtime impact. The risk is low.
- The `mapProviderName()` utility is in `packages/providers` (not `cost-monitor`) because it is used by the instrumentation layer which lives in providers.

## Completion Checklist

- [ ] `Provider` type updated with `'opencode' | 'z-ai' | 'zen-mcp'`
- [ ] Local `AgentType` definition removed
- [ ] `AgentType` imported from `@tamma/shared`
- [ ] `AgentType` re-exported for backward compatibility
- [ ] `mapProviderName()` created with validation against known providers
- [ ] `mapProviderName()` defaults to `'claude-code'` for unknown providers
- [ ] TypeScript compilation passes for cost-monitor package
- [ ] TypeScript compilation passes for providers package
- [ ] TypeScript compilation passes for full monorepo
- [ ] All tests written and passing (types + mapProviderName)
- [ ] Existing tests still pass
- [ ] Code reviewed and approved
