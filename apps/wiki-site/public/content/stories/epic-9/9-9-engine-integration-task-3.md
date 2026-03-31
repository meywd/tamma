---
title: "Task 3: Implement getAgentForPhase() and getEngineTaskOverrides()"
sidebar:
  order: 90
---

**Story:** 9-9-engine-integration - Engine Integration
**Epic:** 9

## Task Description

Add two private helper methods to `TammaEngine`: `getAgentForPhase()` for phase-aware agent resolution, and `getEngineTaskOverrides()` for extracting legacy engine-level task config. These helpers encapsulate the resolver vs single-agent branching logic so that `generatePlan()` and `implementCode()` can use them cleanly.

## Acceptance Criteria

- `getAgentForPhase(phase)` returns a resolved `IAgentProvider` from the `agentResolver` when it exists
- `getAgentForPhase(phase)` falls back to `this.agent` with runtime guard (no non-null assertion) when no resolver is present
- `getAgentForPhase(phase)` passes `projectId` and `engineId` context to the resolver
- `getEngineTaskOverrides()` returns `{ model, maxBudgetUsd, allowedTools, permissionMode }` from `this.config.agent` when present
- `getEngineTaskOverrides()` returns empty object `{}` when `this.config.agent` is undefined
- Neither method mutates input objects

## Implementation Details

### Technical Requirements

- [ ] Import `WorkflowPhase` from `@tamma/shared` (or its types path)
- [ ] The `engineId` field should already be added in Task 1 via `private readonly engineId = randomUUID()` (import from `node:crypto`). Verify it exists.
- [ ] Add `getAgentForPhase()` private method with error handling and runtime guard:
  ```typescript
  private async getAgentForPhase(phase: WorkflowPhase): Promise<IAgentProvider> {
    if (this.agentResolver) {
      try {
        return await this.agentResolver.getAgentForPhase(phase, {
          projectId: `${this.config.github.owner}/${this.config.github.repo}`,
          engineId: this.engineId,
        });
      } catch (err) {
        this.logger.error('Failed to resolve agent for phase', { phase, error: err instanceof Error ? err.message : String(err) });
        throw new EngineError(`Failed to resolve agent for phase ${phase}: ${err instanceof Error ? err.message : String(err)}`);
      }
    }
    if (!this.agent) {
      throw new EngineError('No agent available: neither agentResolver nor agent is configured');
    }
    return this.agent;
  }
  ```
  > **Note:** `projectId` uses `config.github.owner/repo` which is platform-specific. Future work should derive it from `this.platform.getProjectId()` for multi-platform support.
- [ ] Add `getEngineTaskOverrides()` private method:
  ```typescript
  private getEngineTaskOverrides(): Partial<AgentTaskConfig> {
    if (!this.config.agent) return {};
    return {
      model: this.config.agent.model,
      maxBudgetUsd: this.config.agent.maxBudgetUsd,
      allowedTools: this.config.agent.allowedTools,
      permissionMode: this.config.agent.permissionMode,
    };
  }
  ```
- [ ] The `AgentTaskConfig` type may need to be imported or defined. It should be a partial type matching the shape of `executeTask()`'s first parameter (prompt, cwd, model, maxBudgetUsd, allowedTools, permissionMode, outputFormat).

### Files to Modify

- `packages/orchestrator/src/engine.ts` -- Add two private methods and necessary imports

### Dependencies

- [ ] Task 1 must be completed (agent is optional, agentResolver field exists)
- [ ] `WorkflowPhase` type from `packages/shared/src/types/agent-config.ts` (Story 9-1)
- [ ] `IRoleBasedAgentResolver.getAgentForPhase()` method signature (Story 9-8)

## Testing Strategy

### Unit Tests

Testing private methods directly is not idiomatic in Vitest. Instead, these methods will be tested indirectly through `generatePlan()` and `implementCode()` in Task 4. However, we can verify behavior through observable side effects:

- [ ] Test that when `agentResolver` is provided and `generatePlan()` is called, the resolver's `getAgentForPhase()` is invoked with `'PLAN_GENERATION'` (verified in Task 4)
- [ ] Test that `getEngineTaskOverrides()` behavior is correct by checking the task config passed to `agent.executeTask()` in both modes (verified in Task 4)
- [ ] Test that `projectId` matches `owner/repo` format from config
- [ ] Test that resolver errors in `getAgentForPhase()` are caught, logged with `this.logger.error()`, and re-thrown as `EngineError` (verified in Task 5)
- [ ] Test that fallback path throws `EngineError('No agent available: neither agentResolver nor agent is configured')` instead of crashing on non-null assertion (verified in Task 5)

### Validation Steps

1. [ ] Add `WorkflowPhase` import
2. [ ] Implement `getAgentForPhase()` method
3. [ ] Implement `getEngineTaskOverrides()` method
4. [ ] Verify `engineId` field exists (added in Task 1 via `randomUUID()` from `node:crypto`)
5. [ ] Run `pnpm --filter @tamma/orchestrator run typecheck` -- must pass
6. [ ] Run existing tests -- all must pass (methods are private and unused until Task 4)

## Notes & Considerations

- **No provider pooling**: Each `getAgentForPhase()` call creates a fresh provider via the resolver's factory. This is intentional because subprocess-based providers (claude-code, opencode) are stateful processes that should not be shared across workflow phases.
- **Runtime guard instead of non-null assertion**: The fallback path replaces `return this.agent!` with an explicit `if (!this.agent)` check that throws `EngineError('No agent available: neither agentResolver nor agent is configured')`. While the constructor validates that at least one of `agent` or `agentResolver` exists, the runtime guard provides defense-in-depth and a descriptive error message.
- **`engineId`**: Must use `crypto.randomUUID()` from `node:crypto`. Do NOT use timestamp-based values. This field should already be added in Task 1.
- **`AgentTaskConfig` type**: If this type does not exist in `@tamma/providers`, use `Record<string, unknown>` or define a minimal `Partial<{...}>` inline. The important thing is that the return value can be spread into the `executeTask()` call's config argument.
- These methods are `private`, so they will not appear in the public API surface. They exist purely to reduce duplication between `generatePlan()` and `implementCode()`.

## Completion Checklist

- [ ] `getAgentForPhase()` implemented with resolver (try/catch error handling) and fallback (runtime guard, no non-null assertion) paths
- [ ] `getEngineTaskOverrides()` implemented with guard for missing `config.agent`
- [ ] `WorkflowPhase` imported
- [ ] `engineId` field verified (added in Task 1 via `randomUUID()` from `node:crypto`)
- [ ] No mutations of input objects
- [ ] Existing tests pass
- [ ] TypeScript strict mode compilation passes
