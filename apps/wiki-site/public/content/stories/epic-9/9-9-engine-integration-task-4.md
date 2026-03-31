---
title: "Task 4: Update generatePlan() and implementCode() for Resolver Mode"
sidebar:
  order: 90
---

**Story:** 9-9-engine-integration - Engine Integration
**Epic:** 9

## Task Description

Refactor `generatePlan()` and `implementCode()` to use the new `getAgentForPhase()` and `getEngineTaskOverrides()` helpers instead of directly accessing `this.agent` and `this.config.agent`. This enables phase-aware agent resolution when an `IRoleBasedAgentResolver` is configured, while maintaining identical behavior in single-agent mode. Each resolver-created provider must be disposed after use via try/finally.

## Acceptance Criteria

- `generatePlan()` resolves agent via `getAgentForPhase('PLAN_GENERATION')`
- `generatePlan()` passes engine overrides through resolver clamping via `agentResolver?.getTaskConfig('architect', this.getEngineTaskOverrides())`
- `implementCode()` resolves agent via `getAgentForPhase('CODE_GENERATION')`
- `implementCode()` passes engine overrides through resolver clamping via `agentResolver?.getTaskConfig('implementer', this.getEngineTaskOverrides())`
- Task config merge: engine overrides passed through resolver's `getTaskConfig(role, overrides)` for clamping (budget ceiling, permission mode env var guard, allowedTools intersection); only `allowedTools`, `maxBudgetUsd`, `permissionMode` picked from result (explicit field allowlisting)
- Engine always sets `prompt` and `cwd` (these cannot be overridden by resolver config)
- Each resolver-created provider is disposed after use via try/finally with `await agent.dispose()`
- All existing prompt construction, JSON parsing, cost tracking, and event recording logic is preserved
- Both methods work identically in single-agent mode (no resolver)

## Implementation Details

### Technical Requirements

- [ ] Update `generatePlan()` (around line 430):
  ```typescript
  // Replace direct agent usage:
  const agent = await this.getAgentForPhase('PLAN_GENERATION');
  // Pass engine overrides through resolver's clamping logic
  const resolverConfig = this.agentResolver?.getTaskConfig('architect', this.getEngineTaskOverrides()) ?? {};
  // Explicit field allowlisting — only pick resolver-controlled fields
  const safeResolverConfig = {
    allowedTools: resolverConfig.allowedTools,
    maxBudgetUsd: resolverConfig.maxBudgetUsd,
    permissionMode: resolverConfig.permissionMode,
  };

  const taskConfig = {
    ...safeResolverConfig,                       // resolver-controlled fields (clamped)
    prompt: planPrompt,                          // engine always sets prompt
    cwd: this.config.engine.workingDirectory,    // engine always sets cwd
    outputFormat: {
      type: 'json_schema' as const,
      schema: { /* existing schema object -- unchanged */ },
    },
  };

  try {
    const result = await agent.executeTask(taskConfig, (event) => {
      this.logger.debug('Plan generation progress', {
        type: event.type,
        message: event.message,
      });
    });
    // ... existing cost tracking, event recording ...
  } finally {
    await agent.dispose(); // providers are NOT pooled — dispose after each phase
  }
  ```
- [ ] Update `implementCode()` (around line 683):
  ```typescript
  // Replace direct agent usage:
  const agent = await this.getAgentForPhase('CODE_GENERATION');
  // Pass engine overrides through resolver's clamping logic
  const resolverConfig = this.agentResolver?.getTaskConfig('implementer', this.getEngineTaskOverrides()) ?? {};
  // Explicit field allowlisting — only pick resolver-controlled fields
  const safeResolverConfig = {
    allowedTools: resolverConfig.allowedTools,
    maxBudgetUsd: resolverConfig.maxBudgetUsd,
    permissionMode: resolverConfig.permissionMode,
  };

  const taskConfig = {
    ...safeResolverConfig,                       // resolver-controlled fields (clamped)
    prompt: implPrompt,                          // engine always sets prompt
    cwd: this.config.engine.workingDirectory,    // engine always sets cwd
  };

  try {
    const result = await agent.executeTask(taskConfig, (event) => {
      this.logger.debug('Implementation progress', {
        type: event.type,
        message: event.message,
        costSoFar: event.costSoFar,
      });
    });
    // ... existing cost tracking, event recording ...
  } finally {
    await agent.dispose(); // providers are NOT pooled — dispose after each phase
  }
  ```
- [ ] Remove direct references to `this.config.agent.model`, `this.config.agent.maxBudgetUsd`, `this.config.agent.allowedTools`, and `this.config.agent.permissionMode` in both methods (these are now handled by `getEngineTaskOverrides()`)
- [ ] Keep all other logic intact: prompt construction, JSON parsing, cost tracking (`this.totalCostUsd += result.costUsd`), event recording (`this.recordEvent()`), state transitions (`this.setState()`), error handling

### Files to Modify

- `packages/orchestrator/src/engine.ts` -- Update `generatePlan()` and `implementCode()` method bodies

### Dependencies

- [ ] Task 3 must be completed (`getAgentForPhase()` and `getEngineTaskOverrides()` exist)
- [ ] `IRoleBasedAgentResolver.getTaskConfig()` method signature (Story 9-8) — now takes optional `taskOverrides` parameter

## Testing Strategy

### Unit Tests

- [ ] Test `generatePlan()` in single-agent mode: `agent.executeTask()` is called with config containing `model`, `maxBudgetUsd`, `permissionMode` from `config.agent`, plus `prompt` and `cwd`
- [ ] Test `generatePlan()` in resolver mode: resolver's `getAgentForPhase()` is called with `'PLAN_GENERATION'`
- [ ] Test `generatePlan()` in resolver mode: resolver's `getTaskConfig('architect', engineOverrides)` is called with engine overrides for clamping
- [ ] Test `generatePlan()` resolver config merge: only `allowedTools`, `maxBudgetUsd`, `permissionMode` are picked from resolver result (field allowlisting)
- [ ] Test `generatePlan()` config merge: engine `prompt` and `cwd` always override resolver config even if resolver provides them
- [ ] Test `generatePlan()` disposes agent after use via try/finally
- [ ] Test `implementCode()` in single-agent mode: `agent.executeTask()` called with correct config
- [ ] Test `implementCode()` in resolver mode: resolver's `getAgentForPhase()` is called with `'CODE_GENERATION'`
- [ ] Test `implementCode()` in resolver mode: resolver's `getTaskConfig('implementer', engineOverrides)` is called with engine overrides for clamping
- [ ] Test `implementCode()` resolver config merge: only allowlisted fields picked, same merge verification
- [ ] Test `implementCode()` disposes agent after use via try/finally
- [ ] Test that plan JSON parsing still works in resolver mode
- [ ] Test that cost tracking still works in resolver mode
- [ ] Test that event recording still works in resolver mode

### Integration-Style Tests

- [ ] Test full `processOneIssue()` pipeline in resolver mode: all 8 steps complete successfully
- [ ] Verify `agent.executeTask()` is called exactly twice (plan + implement) with different agents from resolver

### Validation Steps

1. [ ] Refactor `generatePlan()` to use helpers
2. [ ] Refactor `implementCode()` to use helpers
3. [ ] Run `pnpm --filter @tamma/orchestrator run typecheck` -- must pass
4. [ ] Run all existing tests -- must pass unchanged (backward compatibility)
5. [ ] Add resolver mode tests for both methods
6. [ ] Add config merge order tests
7. [ ] Run `pnpm vitest run packages/orchestrator/src/engine.test.ts`

## Notes & Considerations

- **Merge order matters**: Engine overrides are passed through the resolver's `getTaskConfig(role, overrides)` so the resolver's clamping logic applies (budget ceiling, permission mode env var guard, allowedTools intersection). Then explicit field allowlisting picks only `allowedTools`, `maxBudgetUsd`, `permissionMode` from the result. Finally, `prompt` and `cwd` are set by the engine (these are always engine-controlled). This prevents arbitrary resolver fields from leaking into the task config.
- **`outputFormat` in `generatePlan()`**: The `outputFormat` field with the JSON schema is an engine concern, not a provider/resolver concern. It should be set in the task config after the merge, alongside `prompt` and `cwd`.
- **Single-agent mode produces identical behavior**: When no resolver is present, `getAgentForPhase()` returns `this.agent` (with runtime guard) and `getTaskConfig()` is not called (short-circuit via `?? {}`). The `getEngineTaskOverrides()` returns the same values that were previously inlined. So the resulting `executeTask()` call is equivalent.
- **Provider disposal**: Each `getAgentForPhase()` call creates a fresh provider. Wrap the agent usage in try/finally with `await agent.dispose()` in the finally block. This ensures subprocess-based providers are cleaned up even on error.
- **Cost tracking**: The `result.costUsd` tracking and `this.totalCostUsd` accumulation remain unchanged. The cost comes from the agent's result, regardless of how the agent was resolved.
- **The `onProgress` callback** stays inline as before -- it only uses `this.logger` which is always available.

## Completion Checklist

- [ ] `generatePlan()` uses `getAgentForPhase('PLAN_GENERATION')` instead of `this.agent`
- [ ] `generatePlan()` passes engine overrides through resolver's clamping, uses field allowlisting
- [ ] `generatePlan()` disposes agent after use via try/finally
- [ ] `implementCode()` uses `getAgentForPhase('CODE_GENERATION')` instead of `this.agent`
- [ ] `implementCode()` passes engine overrides through resolver's clamping, uses field allowlisting
- [ ] `implementCode()` disposes agent after use via try/finally
- [ ] Direct `this.config.agent.*` references removed from both methods
- [ ] `prompt` and `cwd` always set by engine (not overridable by resolver)
- [ ] Only `allowedTools`, `maxBudgetUsd`, `permissionMode` picked from resolver result
- [ ] `outputFormat` preserved in `generatePlan()`
- [ ] All existing prompt construction logic unchanged
- [ ] Cost tracking, event recording, state transitions unchanged
- [ ] Existing tests pass (backward compatibility)
- [ ] New resolver mode tests added and passing
- [ ] Config merge order tests added and passing
- [ ] TypeScript strict mode compilation passes
