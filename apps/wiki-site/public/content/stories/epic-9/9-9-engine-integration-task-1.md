---
title: "Task 1: Make EngineContext.agent Optional and Add agentResolver"
sidebar:
  order: 90
---

**Story:** 9-9-engine-integration - Engine Integration
**Epic:** 9

## Task Description

Update the `EngineContext` interface and `TammaEngine` constructor to support an optional `agentResolver` field as an alternative to the single `agent` field. The constructor must validate that at least one of `agent` or `agentResolver` is provided. This is the foundational change that all subsequent tasks build upon.

## Acceptance Criteria

- `EngineContext.agent` is optional (`agent?: IAgentProvider`)
- `EngineContext.agentResolver` is a new optional field (`agentResolver?: IRoleBasedAgentResolver`) — uses interface, not concrete class
- Constructor throws `EngineError('Either agent or agentResolver must be provided in EngineContext')` when neither is provided
- Constructor logs WARN when both `agent` and `agentResolver` are provided: "Both agent and agentResolver provided; resolver takes precedence for phase resolution"
- Constructor accepts `agent` only, `agentResolver` only, or both
- Private field `this.agent` is typed as `IAgentProvider | undefined`
- Private field `this.agentResolver` is typed as `IRoleBasedAgentResolver | undefined`
- Private field `this.engineId` is generated via `crypto.randomUUID()` (import from `node:crypto`)
- Existing tests that pass `agent` in `EngineContext` continue to work without modification

## Implementation Details

### Technical Requirements

- [ ] Import `import type { IRoleBasedAgentResolver } from '@tamma/providers'` (use the interface, not the concrete class)
- [ ] Import `import { randomUUID } from 'node:crypto'`
- [ ] Update `EngineContext` interface at line 33 of `engine.ts`:
  - Change `agent: IAgentProvider` to `agent?: IAgentProvider`
  - Add `agentResolver?: IRoleBasedAgentResolver`
- [ ] Update private field declarations (around line 83):
  - Change `private readonly agent: IAgentProvider` to `private readonly agent: IAgentProvider | undefined`
  - Add `private readonly agentResolver: IRoleBasedAgentResolver | undefined`
  - Add `private readonly engineId = randomUUID()` (import from `node:crypto`, do NOT use timestamp-based values)
- [ ] Update constructor (line 89):
  - Add validation: `if (!ctx.agent && !ctx.agentResolver) { throw new EngineError('Either agent or agentResolver must be provided in EngineContext'); }`
  - Add WARN log when both provided: `if (ctx.agent && ctx.agentResolver) { this.logger.warn('Both agent and agentResolver provided; resolver takes precedence for phase resolution'); }`
  - Assign `this.agentResolver = ctx.agentResolver`
  - Keep `this.agent = ctx.agent` (now possibly undefined)

### Files to Modify

- `packages/orchestrator/src/engine.ts` -- Modify `EngineContext` interface, private fields, and constructor

### Dependencies

- [ ] `IRoleBasedAgentResolver` interface from `@tamma/providers` (Story 9-8)
- [ ] `EngineError` from `@tamma/shared` (already imported)

## Testing Strategy

### Unit Tests

- [ ] Test constructor with `agent` only -- should succeed (backward compatibility)
- [ ] Test constructor with `agentResolver` only -- should succeed
- [ ] Test constructor with both `agent` and `agentResolver` -- should succeed; verify WARN logged: "Both agent and agentResolver provided; resolver takes precedence for phase resolution"
- [ ] Test constructor with neither `agent` nor `agentResolver` -- should throw `EngineError` with message `'Either agent or agentResolver must be provided in EngineContext'`
- [ ] Verify all existing `createEngine()` calls in tests still work (they pass `agent`)

### Validation Steps

1. [ ] Update the `EngineContext` interface
2. [ ] Update private field declarations
3. [ ] Add constructor validation
4. [ ] Run `pnpm --filter @tamma/orchestrator run typecheck` -- must pass
5. [ ] Run existing tests -- all must pass without modification
6. [ ] Add new constructor validation tests
7. [ ] Run `pnpm vitest run packages/orchestrator/src/engine.test.ts`

## Notes & Considerations

- The `IRoleBasedAgentResolver` interface may not exist yet if Story 9-8 is not complete. In that case, create a minimal type stub or use `import type` with a placeholder interface that has `getAgentForPhase()`, `getTaskConfig()`, and `dispose()` methods. This avoids blocking Task 1 on Story 9-8 completion. Always use the interface (`IRoleBasedAgentResolver`), never the concrete class (`RoleBasedAgentResolver`).
- After this change, TypeScript will flag all usages of `this.agent` where it is called without a null check (e.g., `this.agent.isAvailable()`, `this.agent.dispose()`, `this.agent.executeTask()`). These will be fixed in Tasks 2-4. You may temporarily suppress with `this.agent!` non-null assertions in this task if needed, but prefer fixing the call sites directly.
- The `EngineContext` interface is exported, so downstream consumers (CLI, tests) will see `agent` become optional. Existing code that always provides `agent` will continue to compile.

## Completion Checklist

- [ ] `EngineContext.agent` is optional
- [ ] `EngineContext.agentResolver` field added (typed as `IRoleBasedAgentResolver`)
- [ ] Private field types updated (using `IRoleBasedAgentResolver`, not concrete class)
- [ ] Private `engineId` field added via `randomUUID()` from `node:crypto`
- [ ] Constructor validation added with specific message: `'Either agent or agentResolver must be provided in EngineContext'`
- [ ] Constructor WARN log added when both `agent` and `agentResolver` provided
- [ ] Import for `IRoleBasedAgentResolver` added (use `import type`)
- [ ] Existing tests pass
- [ ] New constructor validation tests added and passing
- [ ] TypeScript strict mode compilation passes
