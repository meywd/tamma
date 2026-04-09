# Story 9-9: Engine Integration — Implementation Plan

## Overview

Wire the `TammaEngine` in `packages/orchestrator/src/engine.ts` to use the new unified API services via the `IRoleBasedAgentResolver`. The engine continues to call the resolver in-process for performance (no HTTP overhead per phase), but the resolver's dependencies (health tracker, diagnostics, config) now read/write to the shared persistent stores. Legacy mode (direct `agent` property) is preserved.

---

## Step-by-Step Implementation Tasks

### Task 1: Update EngineContext and Constructor (2 hours)

**File to modify**: `packages/orchestrator/src/engine.ts`

The `EngineContext` interface already supports both `agent` and `agentResolver` (verified in current code at line 36-44). The constructor already validates that at least one is provided and logs a warning when both are present.

Needed changes:
- Ensure `agentResolver` takes precedence in all code paths (currently only partially wired)
- Add validation that when only `agentResolver` is provided, `agent` is truly skipped everywhere

```typescript
// Already in place (verified):
export interface EngineContext {
  config: TammaConfig;
  platform: IGitPlatform;
  agent?: IAgentProvider;               // now optional
  agentResolver?: IRoleBasedAgentResolver;  // already present
  logger: ILogger;
  eventStore?: IEventStore;
  onStateChange?: OnStateChangeCallback;
  approvalHandler?: ApprovalHandler;
}
```

---

### Task 2: Implement getAgentForPhase() Private Method (3 hours)

**File to modify**: `packages/orchestrator/src/engine.ts`

Add a private method that delegates to the resolver or falls back to the legacy agent:

```typescript
/**
 * Get an agent provider for a workflow phase.
 *
 * Resolution order:
 * 1. If agentResolver is provided, use getAgentForPhase()
 * 2. Otherwise, return the legacy agent (same provider for all phases)
 *
 * The returned provider should be disposed after use (resolver creates
 * new instances per call; legacy agent is long-lived).
 */
private async _getAgentForPhase(
  phase: WorkflowPhase,
  context: { projectId: string; engineId: string },
): Promise<{ provider: IAgentProvider; isFromResolver: boolean }> {
  if (this.agentResolver) {
    try {
      const provider = await this.agentResolver.getAgentForPhase(phase, context);
      return { provider, isFromResolver: true };
    } catch (err) {
      this.logger.error('Agent resolver failed for phase', {
        phase,
        error: err instanceof Error ? err.message : String(err),
      });
      throw new EngineError(
        `Agent resolution failed for phase ${phase}: ${err instanceof Error ? err.message : String(err)}`,
      );
    }
  }

  if (!this.agent) {
    throw new EngineError('No agent or agentResolver available');
  }

  return { provider: this.agent, isFromResolver: false };
}
```

---

### Task 3: Update generatePlan() to Use Phase Resolution (2 hours)

**File to modify**: `packages/orchestrator/src/engine.ts`

Modify the `generatePlan()` method to use `_getAgentForPhase('PLAN_GENERATION')`:

```typescript
private async generatePlan(issue: IssueData): Promise<DevelopmentPlan> {
  this.transitionTo(EngineState.PLANNING);

  const { provider, isFromResolver } = await this._getAgentForPhase('PLAN_GENERATION', {
    projectId: `${this.config.github.owner}/${this.config.github.repo}`,
    engineId: this.engineId,
  });

  try {
    // Get task config with clamping (if resolver available)
    let taskConfig: Partial<AgentTaskConfig> | undefined;
    if (this.agentResolver) {
      const role = this.agentResolver.getRoleForPhase('PLAN_GENERATION');
      taskConfig = this.agentResolver.getTaskConfig(role, this._getEngineTaskOverrides());
    }

    const config: AgentTaskConfig = {
      prompt: this._buildPlanPrompt(issue),
      cwd: this.config.engine.workingDirectory,
      ...taskConfig,
    };

    const result = await provider.executeTask(config);
    // ... existing plan parsing logic ...
    return plan;
  } finally {
    // Dispose resolver-created providers (they are per-call)
    if (isFromResolver) {
      await provider.dispose().catch((err: unknown) => {
        this.logger.warn('Failed to dispose plan provider', {
          error: err instanceof Error ? err.message : String(err),
        });
      });
    }
  }
}
```

---

### Task 4: Update implementCode() to Use Phase Resolution (2 hours)

**File to modify**: `packages/orchestrator/src/engine.ts`

Same pattern as generatePlan() but using `CODE_GENERATION` phase:

```typescript
private async implementCode(plan: DevelopmentPlan): Promise<AgentTaskResult> {
  this.transitionTo(EngineState.IMPLEMENTING);

  const { provider, isFromResolver } = await this._getAgentForPhase('CODE_GENERATION', {
    projectId: `${this.config.github.owner}/${this.config.github.repo}`,
    engineId: this.engineId,
  });

  try {
    let taskConfig: Partial<AgentTaskConfig> | undefined;
    if (this.agentResolver) {
      const role = this.agentResolver.getRoleForPhase('CODE_GENERATION');
      taskConfig = this.agentResolver.getTaskConfig(role, this._getEngineTaskOverrides());
    }

    const config: AgentTaskConfig = {
      prompt: this._buildImplementPrompt(plan),
      cwd: this.config.engine.workingDirectory,
      ...taskConfig,
    };

    return await provider.executeTask(config);
  } finally {
    if (isFromResolver) {
      await provider.dispose().catch((err: unknown) => {
        this.logger.warn('Failed to dispose implementation provider', {
          error: err instanceof Error ? err.message : String(err),
        });
      });
    }
  }
}
```

---

### Task 5: Add _getEngineTaskOverrides() Helper (1 hour)

**File to modify**: `packages/orchestrator/src/engine.ts`

Extracts task overrides from the engine's legacy config for clamping:

```typescript
/**
 * Extract task overrides from legacy engine config.
 * These are passed to resolver.getTaskConfig() for clamping.
 */
private _getEngineTaskOverrides(): Partial<AgentTaskConfig> {
  const overrides: Partial<AgentTaskConfig> = {};

  if (this.config.agent?.maxBudgetUsd !== undefined) {
    overrides.maxBudgetUsd = this.config.agent.maxBudgetUsd;
  }
  if (this.config.agent?.allowedTools !== undefined) {
    overrides.allowedTools = this.config.agent.allowedTools;
  }
  if (this.config.agent?.permissionMode !== undefined) {
    overrides.permissionMode = this.config.agent.permissionMode;
  }

  return overrides;
}
```

---

### Task 6: Update initialize() and dispose() (1 hour)

**File to modify**: `packages/orchestrator/src/engine.ts`

```typescript
async initialize(): Promise<void> {
  // Skip agent availability check in resolver mode
  if (this.agentResolver) {
    this.logger.info('Engine initialized with agent resolver (availability checked lazily)');
    return;
  }

  // Legacy mode: check agent availability eagerly
  if (this.agent) {
    const available = await this.agent.isAvailable();
    if (!available) {
      throw new EngineError('Agent is not available');
    }
  }
}

async dispose(): Promise<void> {
  this.running = false;

  // Dispose resolver (clears cached chains)
  if (this.agentResolver) {
    try {
      await this.agentResolver.dispose();
    } catch (err) {
      this.logger.error('Agent resolver disposal failed', {
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }

  // Dispose legacy agent
  if (this.agent) {
    try {
      await this.agent.dispose();
    } catch (err) {
      this.logger.error('Agent disposal failed', {
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }
}
```

---

### Task 7: Tests (4 hours)

**File to modify**: `packages/orchestrator/src/engine.test.ts`

Add test suites for resolver mode:

| # | Test | Assertion |
|---|------|-----------|
| 1 | Constructor with only agentResolver succeeds | No error |
| 2 | Constructor with neither agent nor resolver throws | EngineError |
| 3 | Constructor with both logs warning | warn called with precedence message |
| 4 | `initialize()` in resolver mode skips availability check | No isAvailable call |
| 5 | `initialize()` in legacy mode checks availability | isAvailable called |
| 6 | `generatePlan()` in resolver mode calls getAgentForPhase('PLAN_GENERATION') | Correct phase |
| 7 | `generatePlan()` disposes resolver-provided agent | dispose called |
| 8 | `generatePlan()` does NOT dispose legacy agent | dispose not called |
| 9 | `implementCode()` in resolver mode calls getAgentForPhase('CODE_GENERATION') | Correct phase |
| 10 | `implementCode()` disposes resolver-provided agent | dispose called |
| 11 | `_getAgentForPhase()` falls back to legacy agent when no resolver | agent returned |
| 12 | `_getAgentForPhase()` catches resolver error and throws EngineError | Wrapped error |
| 13 | `dispose()` calls resolver.dispose() | Method called |
| 14 | `dispose()` calls agent.dispose() for legacy | Method called |
| 15 | `dispose()` handles resolver.dispose() throwing | Logged, not re-thrown |
| 16 | Task config merge from resolver is used | maxBudgetUsd clamped |
| 17 | Engine overrides are passed through resolver.getTaskConfig | Clamping applied |

**Total tests**: ~17

---

## Files to Create

None. All changes are modifications to existing files.

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/orchestrator/src/engine.ts` | Add _getAgentForPhase(), _getEngineTaskOverrides(); update generatePlan(), implementCode(), initialize(), dispose() |
| 2 | `packages/orchestrator/src/engine.test.ts` | Add resolver mode test suite (17 tests) |

---

## Dependencies

- **Story 9-8** (IRoleBasedAgentResolver interface and implementation)
- The engine already imports `IRoleBasedAgentResolver` from `@tamma/providers` (verified in current code)

## Migration from Existing Code

1. The `EngineContext` already accepts both `agent` and `agentResolver` (verified in current codebase at line 36-44).
2. The constructor already validates and warns about dual-mode (verified at lines 95-99).
3. The main migration work is updating `generatePlan()` and `implementCode()` to use `_getAgentForPhase()` with try/finally dispose for resolver-created providers.
4. Legacy mode (only `agent` provided) works exactly as before -- no behavior changes.
5. The `_getEngineTaskOverrides()` method extracts overrides from the legacy `config.agent` object, bridging old config format to the new resolver's clamping API.

---

## Estimated Effort

| Task | Hours |
|------|-------|
| EngineContext + constructor update (mostly done, verify) | 2 |
| _getAgentForPhase() implementation | 3 |
| generatePlan() update with phase resolution | 2 |
| implementCode() update with phase resolution | 2 |
| _getEngineTaskOverrides() helper | 1 |
| initialize() + dispose() updates | 1 |
| Tests (17 tests) | 4 |
| **Total** | **15 hours** |
