---
title: "Task 1: Define Agent Configuration Types"
sidebar:
  order: 90
---

**Story:** 9-1-configuration-schema - Multi-Agent Configuration Schema
**Epic:** 9

## Task Description

Create the new types file `packages/shared/src/types/agent-config.ts` containing multi-agent configuration types: `WorkflowPhase`, `PermissionMode`, `DEFAULT_PHASE_ROLE_MAP`, `ENGINE_STATE_TO_PHASE`, `ProviderChainEntry`, `AgentRoleConfig`, and `AgentsConfig`. `SecurityConfig` is extracted to its own file (`packages/shared/src/types/security-config.ts`). These types form the foundation for multi-agent provider chain configuration where different AI providers can be assigned to different agent roles.

## Acceptance Criteria

- `WorkflowPhase` union type includes all 8 workflow phases
- `DEFAULT_PHASE_ROLE_MAP` maps every `WorkflowPhase` to a valid `AgentType` from `knowledge.ts`
- `ProviderChainEntry` interface has `provider` (required), `model`, `apiKeyRef`, and `config` (all optional)
- `AgentRoleConfig` interface has `providerChain` (required) and optional `allowedTools`, `maxBudgetUsd`, `permissionMode`, `systemPrompt`, `providerPrompts`
- `AgentsConfig` interface has `defaults` (required), optional `roles` and `phaseRoleMap`
- `SecurityConfig` interface is defined in a separate file `packages/shared/src/types/security-config.ts` with all 5 optional fields: `sanitizeContent`, `validateUrls`, `gateActions`, `maxFetchSizeBytes`, `blockedCommandPatterns`
- `SecurityConfig` is re-exported from `packages/shared/src/types/index.ts`
- `PermissionMode` type is exported from `agent-config.ts` and used in `AgentRoleConfig.permissionMode`
- `ENGINE_STATE_TO_PHASE` mapping is exported and covers 6 engine states
- Both files compile under TypeScript strict mode
- `AgentType` is imported from `./knowledge.js`, not re-declared

## Implementation Details

### Technical Requirements

- [ ] Create `packages/shared/src/types/agent-config.ts`
- [ ] Import `AgentType` from `./knowledge.js` using `import type`
- [ ] Define `WorkflowPhase` as a union type of 8 string literals:
  - `'ISSUE_SELECTION'`, `'CONTEXT_ANALYSIS'`, `'PLAN_GENERATION'`, `'CODE_GENERATION'`
  - `'PR_CREATION'`, `'CODE_REVIEW'`, `'TEST_EXECUTION'`, `'STATUS_MONITORING'`
- [ ] Define `DEFAULT_PHASE_ROLE_MAP` as `Record<WorkflowPhase, AgentType>`, frozen with `Object.freeze()` and using `as const satisfies` pattern:
  - `ISSUE_SELECTION` -> `'scrum_master'`
  - `CONTEXT_ANALYSIS` -> `'analyst'`
  - `PLAN_GENERATION` -> `'architect'`
  - `CODE_GENERATION` -> `'implementer'`
  - `PR_CREATION` -> `'implementer'`
  - `CODE_REVIEW` -> `'reviewer'`
  - `TEST_EXECUTION` -> `'tester'`
  - `STATUS_MONITORING` -> `'scrum_master'`
- [ ] Define `ENGINE_STATE_TO_PHASE` as `Partial<Record<string, WorkflowPhase>>`, frozen with `Object.freeze()`:
  - `SELECTING_ISSUE` -> `'ISSUE_SELECTION'`
  - `ANALYZING` -> `'CONTEXT_ANALYSIS'`
  - `PLANNING` -> `'PLAN_GENERATION'`
  - `IMPLEMENTING` -> `'CODE_GENERATION'`
  - `CREATING_PR` -> `'PR_CREATION'`
  - `MONITORING` -> `'STATUS_MONITORING'`
  - Note: `CODE_REVIEW` and `TEST_EXECUTION` have no direct EngineState equivalent (they are sub-phases within the implementation cycle)
- [ ] Define `PermissionMode` shared type:
  ```typescript
  export type PermissionMode = 'bypassPermissions' | 'default';
  ```
  Use this type in `AgentRoleConfig.permissionMode` instead of an inline union. This replaces the inline union in existing `AgentConfig` for consistency.
- [ ] Define `ProviderChainEntry` interface:
  - `provider: string` (required) -- e.g. `'claude-code'`, `'opencode'`, `'openrouter'`, `'zen-mcp'`
  - `model?: string` -- e.g. `'claude-sonnet-4-5'`, `'z-ai/z1-mini'`
  - `apiKeyRef?: string` -- reference to env var name containing the API key (e.g. `'OPENROUTER_API_KEY'`). Resolved at runtime via `process.env[apiKeyRef]`. Raw API keys must NOT be stored in config files.
  - `config?: Record<string, unknown>` -- provider-specific (baseUrl, timeout, etc.)
- [ ] Define `AgentRoleConfig` interface:
  - `providerChain: ProviderChainEntry[]` (required)
  - `allowedTools?: string[]`
  - `maxBudgetUsd?: number`
  - `permissionMode?: PermissionMode`
  - `systemPrompt?: string`
  - `providerPrompts?: Record<string, string>` -- key = provider name
- [ ] Define `AgentsConfig` interface:
  - `defaults: AgentRoleConfig` (required)
  - `roles?: Partial<Record<AgentType, Partial<AgentRoleConfig>>>`
  - `phaseRoleMap?: Partial<Record<WorkflowPhase, AgentType>>`
- [ ] Create **separate file** `packages/shared/src/types/security-config.ts` with `SecurityConfig` interface:
  - `sanitizeContent?: boolean`
  - `validateUrls?: boolean`
  - `gateActions?: boolean`
  - `maxFetchSizeBytes?: number`
  - `blockedCommandPatterns?: string[]`
- [ ] Re-export `SecurityConfig` from `packages/shared/src/types/index.ts` via `export * from './security-config.js'`
- [ ] Export all types, `PermissionMode`, `DEFAULT_PHASE_ROLE_MAP`, and `ENGINE_STATE_TO_PHASE` from `agent-config.ts`

### Files to Modify/Create

- `packages/shared/src/types/agent-config.ts` -- **CREATE** -- `WorkflowPhase`, `PermissionMode`, `DEFAULT_PHASE_ROLE_MAP`, `ENGINE_STATE_TO_PHASE`, `ProviderChainEntry`, `AgentRoleConfig`, `AgentsConfig`
- `packages/shared/src/types/security-config.ts` -- **CREATE** -- `SecurityConfig` (extracted to its own file)
- `packages/shared/src/types/index.ts` -- **MODIFY** -- Add `export * from './security-config.js'`

### Dependencies

- [ ] `packages/shared/src/types/knowledge.ts` must export `AgentType` (already does)

## Testing Strategy

### Unit Tests

- [ ] Verify `DEFAULT_PHASE_ROLE_MAP` has exactly 8 entries (one per `WorkflowPhase`)
- [ ] Verify every value in `DEFAULT_PHASE_ROLE_MAP` is a valid `AgentType` string
- [ ] Verify `DEFAULT_PHASE_ROLE_MAP` is frozen (cannot be mutated)
- [ ] Verify `ENGINE_STATE_TO_PHASE` has exactly 6 entries
- [ ] Verify every value in `ENGINE_STATE_TO_PHASE` is a valid `WorkflowPhase` string
- [ ] Verify `ENGINE_STATE_TO_PHASE` is frozen (cannot be mutated)
- [ ] Verify type exports are accessible from the module
- [ ] Create a conformance test: construct a valid `AgentsConfig` object and ensure it compiles
- [ ] Create a conformance test: construct a valid `SecurityConfig` object (from `security-config.ts`)
- [ ] Verify `PermissionMode` type is usable in `AgentRoleConfig.permissionMode`

### Validation Steps

1. [ ] Create `agent-config.ts` with agent types, `PermissionMode`, `DEFAULT_PHASE_ROLE_MAP`, `ENGINE_STATE_TO_PHASE`
2. [ ] Create `security-config.ts` with `SecurityConfig`
3. [ ] Add `export * from './security-config.js'` to `types/index.ts`
4. [ ] Run `pnpm --filter @tamma/shared run typecheck` -- must pass
5. [ ] Verify imports resolve correctly (AgentType from knowledge.ts)
6. [ ] Write unit tests in `packages/shared/src/types/agent-config.test.ts`
7. [ ] Run `pnpm vitest run packages/shared/src/types/agent-config`

## Config Validation Rules

The types defined in this task must be accompanied by validation logic (can be a separate validator function exported alongside the types). The following constraints must be enforced at config load time:

- [ ] `blockedCommandPatterns`: validate each pattern compiles as a valid regex at load time; max pattern length 500 chars; max 100 patterns total. Invalid patterns should throw a `TammaError` with code `CONFIG.INVALID_REGEX`.
- [ ] `maxFetchSizeBytes`: must be >= 0 and <= 1_073_741_824 (1 GiB). Out-of-range values should throw.
- [ ] `maxBudgetUsd`: must be >= 0 and <= 100 (configurable upper bound); must be `Number.isFinite()`. `NaN` and `Infinity` must be rejected.
- [ ] `providerChain`: must be non-empty in `defaults`. An empty array should throw a `TammaError` with code `CONFIG.EMPTY_PROVIDER_CHAIN`.
- [ ] `provider` string in `ProviderChainEntry`: must match `/^[a-z0-9][a-z0-9_-]{0,63}$/` and must NOT be `__proto__`, `constructor`, or `prototype` (prototype pollution guard).
- [ ] `bypassPermissions`: when set, emit a WARN-level log at startup; additionally require `TAMMA_ALLOW_BYPASS_PERMISSIONS=true` environment variable to take effect. If the env var is not set, ignore the `bypassPermissions` setting and log a WARN explaining why.

## Notes & Considerations

- Use `import type` for the AgentType import since it is only used as a type in most contexts (but DEFAULT_PHASE_ROLE_MAP uses it as a value type annotation, so ensure the import works at runtime too)
- The `DEFAULT_PHASE_ROLE_MAP` is a runtime constant (not just a type), so it must be exported as a value
- `ProviderChainEntry.provider` uses `string` (not a union) to allow extensibility for custom/future providers
- `AgentRoleConfig.permissionMode` uses the shared `PermissionMode` type (extracted as `export type PermissionMode = 'bypassPermissions' | 'default'`) for consistency with `AgentConfig.permissionMode`
- `SecurityConfig` is extracted to its own file (`security-config.ts`) per architect review to keep concerns separated and allow independent evolution
- `ENGINE_STATE_TO_PHASE` uses `Partial<Record<string, WorkflowPhase>>` because not all engine states map to workflow phases (`CODE_REVIEW` and `TEST_EXECUTION` are sub-phases within implementation)
- ESM requires `.js` extensions in import paths even for `.ts` source files

## Completion Checklist

- [ ] `packages/shared/src/types/agent-config.ts` created with agent types
- [ ] `packages/shared/src/types/security-config.ts` created with `SecurityConfig`
- [ ] `WorkflowPhase` type has 8 phases
- [ ] `PermissionMode` type exported (`'bypassPermissions' | 'default'`)
- [ ] `DEFAULT_PHASE_ROLE_MAP` maps all 8 phases to valid `AgentType` values, frozen with `Object.freeze()`
- [ ] `ENGINE_STATE_TO_PHASE` maps 6 engine states to workflow phases, frozen with `Object.freeze()`
- [ ] `ProviderChainEntry` uses `apiKeyRef` (not `apiKey`)
- [ ] `AgentRoleConfig` uses `PermissionMode` type for `permissionMode`
- [ ] `AgentsConfig` interface defined
- [ ] `SecurityConfig` interface defined in separate file
- [ ] `SecurityConfig` re-exported from `types/index.ts`
- [ ] All types exported
- [ ] TypeScript strict mode compilation passes
- [ ] Unit tests written and passing
