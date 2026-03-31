---
title: "Task 2: Update initialize() and dispose() Guards"
sidebar:
  order: 90
---

**Story:** 9-9-engine-integration - Engine Integration
**Epic:** 9

## Task Description

Guard the `initialize()` and `dispose()` methods so they do not call methods on `this.agent` when it is undefined (resolver-only mode). In resolver mode, agent availability is checked lazily when `getAgentForPhase()` is called, so `initialize()` should skip the `isAvailable()` check. The `dispose()` method must only call `agent.dispose()` when an agent instance exists.

## Acceptance Criteria

- `initialize()` does not call `this.agent.isAvailable()` when `this.agent` is undefined
- `initialize()` still calls `this.agent.isAvailable()` when `this.agent` exists (backward compat)
- `initialize()` logs `'resolver-mode'` as model when `this.config.agent?.model` is undefined
- `initialize()` logs the actual model name when `this.config.agent.model` exists
- `dispose()` does not call `this.agent.dispose()` when `this.agent` is undefined
- `dispose()` calls `this.agentResolver?.dispose()` to clear cached chains when resolver exists
- `dispose()` does not throw in resolver-only mode
- `dispose()` always calls `this.platform.dispose()` regardless of mode

## Implementation Details

### Technical Requirements

- [ ] Update `initialize()` method (line 101 of `engine.ts`). Add sanitizer warning when resolver is used:
  ```typescript
  async initialize(): Promise<void> {
    if (this.agent) {
      const available = await this.agent.isAvailable();
      if (!available) {
        throw new EngineError('Agent provider is not available. Check ANTHROPIC_API_KEY.');
      }
    }
    if (this.agentResolver) {
      this.logger.warn('Using resolver mode — ensure content sanitizer is configured in resolver options');
    }
    this.logger.info('TammaEngine initialized', {
      mode: this.config.mode,
      model: this.config.agent?.model ?? 'resolver-mode',
      approvalMode: this.config.engine.approvalMode,
    });
  }
  ```
- [ ] Update `dispose()` method (line 114 of `engine.ts`). Add `agentResolver.dispose()` call to clear cached chains:
  ```typescript
  async dispose(): Promise<void> {
    this.running = false;
    if (this.currentPipelinePromise) {
      await this.currentPipelinePromise.catch(() => {});
    }
    if (this.agent) {
      await this.agent.dispose();
    }
    if (this.agentResolver) {
      await this.agentResolver.dispose();
    }
    await this.platform.dispose();
    this.logger.info('TammaEngine disposed');
  }
  ```

### Files to Modify

- `packages/orchestrator/src/engine.ts` -- Update `initialize()` and `dispose()` methods

### Dependencies

- [ ] Task 1 must be completed first (agent field is now optional)

## Testing Strategy

### Unit Tests

- [ ] Test `initialize()` with single agent (existing behavior): `isAvailable()` is called, succeeds
- [ ] Test `initialize()` with single agent, `isAvailable()` returns false: throws `EngineError`
- [ ] Test `initialize()` with resolver only: does not call `isAvailable()` on any agent, succeeds
- [ ] Test `initialize()` with resolver only: logger.info is called with `model: 'resolver-mode'`
- [ ] Test `initialize()` with single agent: logger.info is called with actual model name (e.g., `'claude-sonnet-4-5'`)
- [ ] Test `dispose()` with single agent: `agent.dispose()` and `platform.dispose()` are both called
- [ ] Test `dispose()` with resolver only: `platform.dispose()` is called, no error thrown
- [ ] Test `dispose()` with resolver only: does not attempt to call `dispose()` on undefined agent
- [ ] Test `dispose()` with resolver: `agentResolver.dispose()` is called to clear cached chains
- [ ] Test `initialize()` with resolver: warns about sanitizer configuration

### Validation Steps

1. [ ] Update `initialize()` with agent guard
2. [ ] Update `dispose()` with agent guard
3. [ ] Run `pnpm --filter @tamma/orchestrator run typecheck` -- must pass
4. [ ] Run existing tests -- all must pass
5. [ ] Add new guard tests for resolver-only mode
6. [ ] Run `pnpm vitest run packages/orchestrator/src/engine.test.ts`

## Notes & Considerations

- **`TammaConfig.agent` remains required** in the current schema. The `this.config.agent?.model` optional chaining is future-proofing for when `TammaConfig.agent` is made optional in a future story. The `'resolver-mode'` log value will only appear after that change. Adjust test expectations accordingly — in the current schema, `config.agent.model` will always be present.
- In resolver mode, provider availability is checked on-demand when `getAgentForPhase()` creates a provider instance. This is intentional: the resolver may use different providers for different phases, so checking availability of all providers at `initialize()` time would be premature.
- The `dispose()` guard is straightforward: just wrap the `agent.dispose()` call in an `if (this.agent)` check. The `platform.dispose()` call remains unconditional.

## Completion Checklist

- [ ] `initialize()` guards `agent.isAvailable()` with `if (this.agent)` check
- [ ] `initialize()` warns about sanitizer when resolver mode is used
- [ ] `initialize()` logs `'resolver-mode'` when no `config.agent.model`
- [ ] `dispose()` guards `agent.dispose()` with `if (this.agent)` check
- [ ] `dispose()` calls `agentResolver.dispose()` when resolver exists
- [ ] `platform.dispose()` remains unconditional in `dispose()`
- [ ] Existing tests pass
- [ ] New resolver-only mode tests added and passing
- [ ] TypeScript strict mode compilation passes
