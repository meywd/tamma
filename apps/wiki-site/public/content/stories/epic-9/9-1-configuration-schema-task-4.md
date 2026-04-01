---
title: "Task 4: Implement normalizeAgentsConfig() with Legacy Provider Mapping"
sidebar:
  order: 90
---

**Story:** 9-1-configuration-schema - Multi-Agent Configuration Schema
**Epic:** 9

## Task Description

Implement the `normalizeAgentsConfig()` function in `packages/shared/src/config/normalize-agents.ts` that bridges legacy single-agent configuration to the new multi-agent `AgentsConfig` format. This function lives in `@tamma/shared` (not the CLI) so that any package (orchestrator, workers, etc.) can normalize legacy config without depending on the CLI. The CLI can re-export for convenience. When `config.agents` is already set, a deep clone is returned. Otherwise, the function derives an `AgentsConfig` from the legacy `config.agent` field using `LEGACY_PROVIDER_MAP` to translate `AIProviderType` values to provider chain entry names.

## Acceptance Criteria

- `normalizeAgentsConfig()` returns a deep clone of `config.agents` when it is defined (via `structuredClone`)
- When `config.agents` is undefined, it derives `AgentsConfig` from `config.agent`
- `LEGACY_PROVIDER_MAP` maps `'anthropic'` to `'claude-code'`
- `LEGACY_PROVIDER_MAP` maps `'openai'` to `'openrouter'`
- `LEGACY_PROVIDER_MAP` maps `'local'` to `'local'` (local providers keep their identity; the factory handles Ollama/llama.cpp/vLLM routing)
- Derived config includes `providerChain`, `allowedTools`, `maxBudgetUsd`, and `permissionMode` from legacy agent config
- Function does not mutate the input config object
- Function is exported for use by other packages

## Implementation Details

### Technical Requirements

- [ ] Define `LEGACY_PROVIDER_MAP` constant in `packages/shared/src/config/normalize-agents.ts`:
  ```typescript
  /** Map legacy AIProviderType to provider chain name */
  const LEGACY_PROVIDER_MAP: Record<AIProviderType, string> = Object.freeze({
    anthropic: 'claude-code',
    openai: 'openrouter',
    local: 'local',     // Local providers keep their identity; the factory handles Ollama/llama.cpp/vLLM routing
  });
  ```
- [ ] Implement `normalizeAgentsConfig(config: TammaConfig): AgentsConfig`:
  ```typescript
  export function normalizeAgentsConfig(config: TammaConfig): AgentsConfig {
    if (config.agents) return structuredClone(config.agents);

    const legacy = config.agent;
    const providerName = LEGACY_PROVIDER_MAP[legacy.provider ?? 'anthropic'];

    return {
      defaults: {
        providerChain: [{ provider: providerName, model: legacy.model }],
        allowedTools: legacy.allowedTools,
        maxBudgetUsd: legacy.maxBudgetUsd,
        permissionMode: legacy.permissionMode,
      },
    };
  }
  ```
- [ ] Add necessary imports: `AgentsConfig`, `TammaConfig`, `AIProviderType` from `../types/index.js`
- [ ] Export the function from `packages/shared/src/config/normalize-agents.ts`
- [ ] Re-export from `packages/shared/src/index.ts`
- [ ] The CLI (`packages/cli/src/config.ts`) should re-export `normalizeAgentsConfig` from `@tamma/shared` for convenience

### Files to Modify/Create

- `packages/shared/src/config/normalize-agents.ts` -- **CREATE** -- Add `LEGACY_PROVIDER_MAP` constant and `normalizeAgentsConfig()` function
- `packages/shared/src/index.ts` -- **MODIFY** -- Re-export `normalizeAgentsConfig` from `./config/normalize-agents.js`
- `packages/cli/src/config.ts` -- **MODIFY** -- Re-export `normalizeAgentsConfig` from `@tamma/shared` for convenience

### Dependencies

- [ ] Task 1 must be completed (AgentsConfig type must exist)
- [ ] Task 2 must be completed (TammaConfig must have agents? field)
- [ ] `AIProviderType` from `@tamma/shared` (already exists)

## Testing Strategy

### Unit Tests

- [ ] Test: config with `agents` set -- returns a deep clone of `config.agents` (not the same reference)
- [ ] Test: config with `agents` undefined, `agent.provider = 'anthropic'` -- returns chain with `{ provider: 'claude-code' }`
- [ ] Test: config with `agents` undefined, `agent.provider = 'openai'` -- returns chain with `{ provider: 'openrouter' }`
- [ ] Test: config with `agents` undefined, `agent.provider = 'local'` -- returns chain with `{ provider: 'local' }`
- [ ] Test: config with `agents` undefined, `agent.provider` undefined -- defaults to `'anthropic'` mapping (`{ provider: 'claude-code' }`)
- [ ] Test: derived config includes `model` from `agent.model`
- [ ] Test: derived config includes `allowedTools` from `agent.allowedTools`
- [ ] Test: derived config includes `maxBudgetUsd` from `agent.maxBudgetUsd`
- [ ] Test: derived config includes `permissionMode` from `agent.permissionMode`
- [ ] Test: input config object is not mutated
- [ ] Test: LEGACY_PROVIDER_MAP covers all 3 AIProviderType values

### Validation Steps

1. [ ] Create `packages/shared/src/config/normalize-agents.ts` with `LEGACY_PROVIDER_MAP` and `normalizeAgentsConfig()`
2. [ ] Add necessary imports from `../types/index.js`
3. [ ] Re-export from `packages/shared/src/index.ts`
4. [ ] Re-export from `packages/cli/src/config.ts` for convenience
5. [ ] Run `pnpm --filter @tamma/shared run typecheck` -- must pass
6. [ ] Write unit tests in `packages/shared/src/config/normalize-agents.test.ts`
7. [ ] Run `pnpm --filter @tamma/shared test`
8. [ ] Verify with example: legacy config `{ agent: { model: 'claude-sonnet-4-5', provider: 'anthropic', ... } }` produces `{ defaults: { providerChain: [{ provider: 'claude-code', model: 'claude-sonnet-4-5' }], ... } }`
9. [ ] Verify `structuredClone` is used when `config.agents` is already set

## Notes & Considerations

- The function is intentionally simple: it does not attempt deep merging of legacy and new configs. If `config.agents` exists, it wins entirely.
- `LEGACY_PROVIDER_MAP` is NOT exported -- it is an internal implementation detail. Only `normalizeAgentsConfig()` is exported. It is frozen via `Object.freeze()` to prevent accidental mutation.
- The `provider ?? 'anthropic'` fallback handles the case where `agent.provider` is undefined (which is common since it is optional on `AgentConfig`)
- When `config.agents` is set, the function returns `structuredClone(config.agents)` to prevent callers from mutating the original config object. This is a defensive copy.
- The `model` field in `ProviderChainEntry` comes from `legacy.model` which is always defined (required on `AgentConfig`)
- The function lives in `packages/shared/src/config/normalize-agents.ts` (not the CLI) to avoid upward dependencies. Any package that needs to normalize config can import from `@tamma/shared`.

## Completion Checklist

- [ ] `packages/shared/src/config/normalize-agents.ts` created
- [ ] `LEGACY_PROVIDER_MAP` constant defined with all 3 mappings (`local` maps to `'local'`, not `'openrouter'`)
- [ ] `LEGACY_PROVIDER_MAP` frozen with `Object.freeze()`
- [ ] `normalizeAgentsConfig()` implemented and exported from `@tamma/shared`
- [ ] Re-exported from `packages/cli/src/config.ts` for convenience
- [ ] Handles `config.agents` present case (returns `structuredClone`)
- [ ] Handles legacy-only case with correct provider mapping
- [ ] Handles undefined `agent.provider` (defaults to anthropic)
- [ ] Input config not mutated
- [ ] TypeScript compilation passes
- [ ] All unit tests written and passing
