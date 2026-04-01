---
title: "Task 2: Extend TammaConfig with New Optional Fields"
sidebar:
  order: 90
---

**Story:** 9-1-configuration-schema - Multi-Agent Configuration Schema
**Epic:** 9

## Task Description

Modify the `TammaConfig` interface in `packages/shared/src/types/index.ts` to include the new `agents?: AgentsConfig` and `security?: SecurityConfig` optional fields. The existing `agent: AgentConfig` field must remain required for backward compatibility. Also update the barrel export in `packages/shared/src/index.ts` to re-export the new types, and add a re-export line in `packages/shared/src/types/index.ts`.

## Acceptance Criteria

- `TammaConfig` has `agents?: AgentsConfig` as an optional field
- `TammaConfig` has `security?: SecurityConfig` as an optional field
- `agent: AgentConfig` remains required (no breaking change)
- `AgentsConfig` and `SecurityConfig` types are importable from `@tamma/shared`
- All new types from `agent-config.ts` are re-exported through the barrel

## Implementation Details

### Technical Requirements

- [ ] Add import for `AgentsConfig` from `./agent-config.js` in `packages/shared/src/types/index.ts`
- [ ] Add import for `SecurityConfig` from `./security-config.js` in `packages/shared/src/types/index.ts`
- [ ] Add `agents?: AgentsConfig` field to `TammaConfig` interface with JSDoc comment
- [ ] Add `security?: SecurityConfig` field to `TammaConfig` interface with JSDoc comment
- [ ] Add re-export line `export * from './agent-config.js';` in `packages/shared/src/types/index.ts`
- [ ] Add re-export line `export * from './security-config.js';` in `packages/shared/src/types/index.ts`
- [ ] Verify `packages/shared/src/index.ts` already re-exports from `./types/index.js` (it does via `export * from './types/index.js'`)

### Files to Modify/Create

- `packages/shared/src/types/index.ts` -- **MODIFY** -- Add imports, fields to TammaConfig, re-export
- `packages/shared/src/index.ts` -- **VERIFY** -- Ensure barrel already chains exports (no change expected)

### Dependencies

- [ ] Task 1 must be completed first (agent-config.ts must exist)

## Testing Strategy

### Unit Tests

- [ ] Construct a `TammaConfig` object without `agents` and `security` -- must compile (backward compat)
- [ ] Construct a `TammaConfig` object with `agents` and `security` -- must compile
- [ ] Verify `AgentsConfig` is importable from `@tamma/shared`
- [ ] Verify `SecurityConfig` is importable from `@tamma/shared`
- [ ] Verify `WorkflowPhase` is importable from `@tamma/shared`
- [ ] Verify `ProviderChainEntry` is importable from `@tamma/shared`
- [ ] Verify `DEFAULT_PHASE_ROLE_MAP` is importable from `@tamma/shared`
- [ ] Verify `ENGINE_STATE_TO_PHASE` is importable from `@tamma/shared`
- [ ] Verify `PermissionMode` is importable from `@tamma/shared`

### Validation Steps

1. [ ] Add imports and re-export to `packages/shared/src/types/index.ts`
2. [ ] Add `agents?` and `security?` fields to `TammaConfig`
3. [ ] Run `pnpm --filter @tamma/shared run typecheck` -- must pass
4. [ ] Verify no existing code breaks (existing TammaConfig usages should not need changes)
5. [ ] Run full test suite `pnpm --filter @tamma/shared test`

## Notes & Considerations

- The re-export in `types/index.ts` already has `export * from './knowledge.js'` -- add `export * from './agent-config.js'` and `export * from './security-config.js'` in the same style
- The barrel in `packages/shared/src/index.ts` has `export * from './types/index.js'` which will automatically pick up the new re-export chain
- Keep `agent: AgentConfig` as **required** -- the story explicitly states backward compatibility. The `agents` field is the new multi-agent config and is **optional**
- JSDoc comments on the new fields should explain their purpose:
  - `agents`: "Multi-agent provider chain configuration"
  - `security`: "Security settings for content sanitization, URL validation, and action gating"

## Completion Checklist

- [ ] `AgentsConfig` and `SecurityConfig` imported in `types/index.ts`
- [ ] `agents?: AgentsConfig` added to `TammaConfig` with JSDoc
- [ ] `security?: SecurityConfig` added to `TammaConfig` with JSDoc
- [ ] Re-export `export * from './agent-config.js'` added
- [ ] Re-export `export * from './security-config.js'` added
- [ ] `agent: AgentConfig` remains required
- [ ] TypeScript compilation passes
- [ ] Existing tests still pass
- [ ] New types importable from `@tamma/shared`
