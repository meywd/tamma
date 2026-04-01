---
title: "Task 5: Add Backward Compatibility Tests"
sidebar:
  order: 90
---

**Story:** 9-9-engine-integration - Engine Integration
**Epic:** 9

## Task Description

Add a comprehensive test suite in `engine.test.ts` that validates backward compatibility (single-agent mode still works) and exercises all new resolver-mode code paths. This task consolidates all test additions from Tasks 1-4 into a coherent test plan and ensures full coverage of the integration changes.

## Acceptance Criteria

- All existing tests pass without modification
- New test suite `describe('resolver mode', ...)` covers all resolver-specific behaviors
- Constructor validation tests cover all four combinations (agent-only, resolver-only, both, neither)
- `initialize()` and `dispose()` tested in resolver-only mode
- `generatePlan()` and `implementCode()` tested with resolver providing phase-specific agents
- Task config merge order verified: engine overrides passed through resolver clamping, field allowlisting applied, prompt/cwd always engine-controlled
- Logging output verified: `'resolver-mode'` vs actual model name
- Full pipeline (`processOneIssue()`) tested in resolver mode
- Resolver error handling tested: errors caught, logged, re-thrown as `EngineError`
- Non-null assertion replaced: fallback throws descriptive `EngineError`
- Both-modes precedence tested: resolver wins, WARN logged, single agent still validated
- Events emitted during resolver mode include resolved agent/provider identity
- Provider disposal tested: agents disposed after each phase usage via try/finally
- Permission mode variant tested: `permissionMode: 'default'` (not just bypassPermissions)

## Implementation Details

### Technical Requirements

- [ ] Create a `createMockAgentResolver()` factory function that returns a mock satisfying the `IRoleBasedAgentResolver` interface directly (no `as unknown as RoleBasedAgentResolver` cast):
  ```typescript
  function createMockAgentResolver(): IRoleBasedAgentResolver {
    const mockPlanAgent = createMockAgent();
    const mockImplAgent = createMockAgent();
    return {
      getAgentForPhase: vi.fn().mockImplementation((phase: string) => {
        if (phase === 'PLAN_GENERATION') return Promise.resolve(mockPlanAgent);
        if (phase === 'CODE_GENERATION') return Promise.resolve(mockImplAgent);
        return Promise.resolve(mockPlanAgent); // default
      }),
      getTaskConfig: vi.fn().mockImplementation((role: string, _taskOverrides?: Partial<AgentTaskConfig>) => {
        if (role === 'architect') return { allowedTools: ['Read', 'Grep'], maxBudgetUsd: 0.5, permissionMode: 'default' };
        if (role === 'implementer') return { allowedTools: ['Read', 'Write', 'Edit', 'Bash'], maxBudgetUsd: 2.0, permissionMode: 'default' };
        return {};
      }),
      dispose: vi.fn().mockResolvedValue(undefined),
    };
  }
  ```
- [ ] Create a `createResolverEngine()` helper that builds an engine with `agentResolver` and no `agent`:
  ```typescript
  function createResolverEngine(overrides?: Partial<EngineContext>) {
    const config = createMockConfig();
    const logger = createMockLogger();
    const platform = createMockPlatform();
    const agentResolver = createMockAgentResolver();

    const engine = new TammaEngine({
      config,
      logger,
      platform,
      agentResolver,
      ...overrides,
    });

    return { engine, config, logger, platform, agentResolver };
  }
  ```
- [ ] Add test suites under a new top-level `describe('resolver mode', ...)` block

### Test Cases

#### Constructor Validation
- [ ] `it('should accept agent only')` -- existing behavior, passes
- [ ] `it('should accept agentResolver only')` -- new, no throw
- [ ] `it('should accept both agent and agentResolver')` -- new, no throw; verify WARN logged: "Both agent and agentResolver provided; resolver takes precedence for phase resolution"
- [ ] `it('should throw when neither agent nor agentResolver provided')` -- new, throws EngineError with message `'Either agent or agentResolver must be provided in EngineContext'`

#### initialize() in Resolver Mode
- [ ] `it('should not call isAvailable() when using resolver')` -- create engine with resolver only, call `initialize()`, verify no `isAvailable()` call
- [ ] `it('should log resolver-mode as model')` -- verify `logger.info` called with `model: 'resolver-mode'`
- [ ] `it('should warn about sanitizer in resolver mode')` -- verify `logger.warn` called with 'Using resolver mode — ensure content sanitizer is configured in resolver options'
- [ ] `it('should still log actual model in single-agent mode')` -- verify `logger.info` called with `model: 'claude-sonnet-4-5'`
- [ ] `it('should validate single agent availability when both agent and resolver provided')` -- verify `isAvailable()` called on agent even when resolver present

#### dispose() in Resolver Mode
- [ ] `it('should not throw when no agent provided')` -- create engine with resolver only, call `dispose()`, no error
- [ ] `it('should call platform.dispose() in resolver mode')` -- verify `platform.dispose()` called
- [ ] `it('should not call agent.dispose() when agent is undefined')` -- verify no call on undefined
- [ ] `it('should call agentResolver.dispose() to clear cached chains')` -- verify `agentResolver.dispose()` called in dispose()

#### generatePlan() in Resolver Mode
- [ ] `it('should resolve agent via PLAN_GENERATION phase')` -- verify `agentResolver.getAgentForPhase` called with `'PLAN_GENERATION'`
- [ ] `it('should pass engine overrides through resolver clamping')` -- verify `agentResolver.getTaskConfig` called with `('architect', engineOverrides)`
- [ ] `it('should use field allowlisting on resolver config')` -- verify only `allowedTools`, `maxBudgetUsd`, `permissionMode` from resolver config appear in task config
- [ ] `it('should always set prompt from engine')` -- verify prompt in task config matches engine-generated prompt, not resolver
- [ ] `it('should always set cwd from engine config')` -- verify cwd matches `config.engine.workingDirectory`
- [ ] `it('should still parse plan JSON correctly')` -- verify plan object returned
- [ ] `it('should dispose agent after use')` -- verify `agent.dispose()` called in finally block even on error

#### implementCode() in Resolver Mode
- [ ] `it('should resolve agent via CODE_GENERATION phase')` -- verify `agentResolver.getAgentForPhase` called with `'CODE_GENERATION'`
- [ ] `it('should pass engine overrides through resolver clamping')` -- verify `agentResolver.getTaskConfig` called with `('implementer', engineOverrides)`
- [ ] `it('should use field allowlisting on resolver config')` -- verify only allowlisted fields in task config
- [ ] `it('should track cost from resolver-provided agent')` -- verify `totalCostUsd` updates
- [ ] `it('should dispose agent after use')` -- verify `agent.dispose()` called in finally block

#### Config Merge Order
- [ ] `it('should pass engine overrides through resolver clamping')` -- verify `getTaskConfig` receives engine overrides as second argument
- [ ] `it('should only pick allowlisted fields from resolver config')` -- resolver returns extra fields, verify they do NOT appear in `executeTask` arg
- [ ] `it('should always let prompt and cwd win')` -- resolver sets `prompt: 'wrong'`, engine prompt still used
- [ ] `it('should handle permissionMode default')` -- test with `permissionMode: 'default'` (not just bypassPermissions)

#### Error Handling
- [ ] `it('should catch and re-throw resolver errors as EngineError')` -- resolver's `getAgentForPhase()` throws, verify `EngineError` with message containing phase name
- [ ] `it('should log resolver errors before re-throwing')` -- verify `logger.error` called with phase and error message
- [ ] `it('should throw descriptive error when no agent available')` -- neither agent nor resolver configured (bypassing constructor check), verify `EngineError('No agent available: neither agentResolver nor agent is configured')`

#### Event Audit Trail
- [ ] `it('should include resolved agent/provider identity in events')` -- events emitted during resolver mode include provider identity, not 'unknown'

#### Both-Modes Precedence
- [ ] `it('should use resolver for phase resolution when both agent and resolver provided')` -- verify `agentResolver.getAgentForPhase` called, not fallback to `this.agent`
- [ ] `it('should log WARN when both agent and resolver provided')` -- verify WARN at construction time

#### Full Pipeline in Resolver Mode
- [ ] `it('should complete processOneIssue with resolver')` -- run full pipeline, verify 2 `getAgentForPhase` calls (plan + implement), PR created, merged

### Files to Modify

- `packages/orchestrator/src/engine.test.ts` -- Add new describe blocks and test cases

### Dependencies

- [ ] Tasks 1-4 must be completed (all engine changes in place)
- [ ] Mock satisfying `IRoleBasedAgentResolver` interface (created in this task's test helpers)

## Testing Strategy

### Validation Steps

1. [ ] Create `createMockAgentResolver()` helper (satisfies `IRoleBasedAgentResolver` interface, includes `dispose()`)
2. [ ] Create `createResolverEngine()` helper
3. [ ] Add constructor validation tests (including WARN log and specific error message)
4. [ ] Add initialize/dispose guard tests (including sanitizer warning and resolver disposal)
5. [ ] Add generatePlan resolver tests (including field allowlisting and agent disposal)
6. [ ] Add implementCode resolver tests (including field allowlisting and agent disposal)
7. [ ] Add config merge order tests (including `permissionMode: 'default'` variant)
8. [ ] Add error handling tests (resolver error, fallback guard)
9. [ ] Add event audit trail test (resolved identity in events)
10. [ ] Add both-modes precedence tests
11. [ ] Add full pipeline test
12. [ ] Run all tests: `pnpm vitest run packages/orchestrator/src/engine.test.ts`
13. [ ] Verify existing tests still pass (no modifications to existing tests)
14. [ ] Check coverage: `pnpm vitest run --coverage packages/orchestrator/src/engine.test.ts`

## Notes & Considerations

- **Do not modify existing tests**: All existing `describe` blocks and `it` blocks must remain unchanged. The new tests go in new `describe` blocks.
- **Mock agent resolver**: The mock should satisfy the `IRoleBasedAgentResolver` interface directly. Do NOT use `as unknown as RoleBasedAgentResolver` casts. The mock needs three methods: `getAgentForPhase()`, `getTaskConfig()`, and `dispose()`.
- **Separate mock agents**: The resolver should return different mock agent instances for different phases. This lets tests verify that `generatePlan()` uses the plan agent and `implementCode()` uses the implementation agent by checking which mock's `executeTask` was called.
- **Config merge assertions**: Verify that `getTaskConfig()` is called with engine overrides as the second argument, and that only allowlisted fields (`allowedTools`, `maxBudgetUsd`, `permissionMode`) from the result appear in the `executeTask()` call argument. Inspect using `vi.mocked(agent.executeTask).mock.calls[0][0]`.
- **Full pipeline test**: This is the most important test. It validates that the entire `selectIssue -> analyzeIssue -> generatePlan -> awaitApproval -> createBranch -> implementCode -> createPR -> monitorAndMerge` flow works with resolver-provided agents.

## Completion Checklist

- [ ] `createMockAgentResolver()` helper created (satisfies `IRoleBasedAgentResolver` interface directly, no cast)
- [ ] `createResolverEngine()` helper created
- [ ] Constructor validation tests (4 cases) added, including WARN log and specific error message
- [ ] Initialize guard tests (5 cases) added, including sanitizer warning and both-modes validation
- [ ] Dispose guard tests (4 cases) added, including `agentResolver.dispose()` call
- [ ] generatePlan resolver tests (7 cases) added, including field allowlisting and agent disposal
- [ ] implementCode resolver tests (5 cases) added, including field allowlisting and agent disposal
- [ ] Config merge order tests (4 cases) added, including permissionMode: 'default' variant
- [ ] Error handling tests (3 cases) added: resolver error, fallback guard, descriptive messages
- [ ] Event audit trail test added: resolved agent identity in events
- [ ] Both-modes precedence tests (2 cases) added
- [ ] Full pipeline test added
- [ ] All existing tests pass without modification
- [ ] All new tests pass
- [ ] Coverage meets 80% line / 75% branch / 85% function targets
- [ ] TypeScript compilation passes
