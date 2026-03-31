---
title: "Task 3: Implement getTaskConfig with 3-Level Merge Precedence"
sidebar:
  order: 90
---

**Story:** 9-8-role-based-agent-resolver - Role-Based Agent Resolver
**Epic:** 9

## Task Description

Implement the `getTaskConfig()` method which merges task configuration from three levels: engine defaults, role-specific config, and caller-provided task overrides. The merge uses a "last wins" strategy where task overrides take highest precedence.

## Acceptance Criteria

- `getTaskConfig(role, taskOverrides?)` returns a `Partial<AgentTaskConfig>`
- Merge precedence (last wins, with clamping): engine defaults < role config < task overrides (clamped)
- Base config extracted from `this.config.defaults` (allowedTools, maxBudgetUsd, permissionMode)
- Role-level config extracted from `this.config.roles?.[role]` only for defined fields
- Undefined role config fields do NOT override defaults (conditional spread)
- Task overrides are **clamped** to prevent escalation:
  - `maxBudgetUsd`: cannot exceed the resolved config ceiling (uses `Math.min`)
  - `permissionMode: 'bypassPermissions'`: requires `TAMMA_ALLOW_BYPASS_PERMISSIONS=true` env var
  - `allowedTools`: overrides are intersected with (not replacing) the resolved allowed tools
- Forbidden role keys (`__proto__`, `constructor`, `prototype`) are rejected with an error
- Config objects are never mutated (immutable merge via spread)

## Implementation Details

### Technical Requirements

- [ ] Implement `getTaskConfig(role: AgentType, taskOverrides?: Partial<AgentTaskConfig>): Partial<AgentTaskConfig>`
- [ ] Build base from defaults:
  ```typescript
  const base: Partial<AgentTaskConfig> = {
    allowedTools: defaults.allowedTools,
    maxBudgetUsd: defaults.maxBudgetUsd,
    permissionMode: defaults.permissionMode,
  };
  ```
- [ ] Build roleLevel using conditional spread to avoid overriding with undefined:
  ```typescript
  const roleLevel: Partial<AgentTaskConfig> = {
    ...(roleConfig?.allowedTools !== undefined && { allowedTools: roleConfig.allowedTools }),
    ...(roleConfig?.maxBudgetUsd !== undefined && { maxBudgetUsd: roleConfig.maxBudgetUsd }),
    ...(roleConfig?.permissionMode !== undefined && { permissionMode: roleConfig.permissionMode }),
  };
  ```
- [ ] Add FORBIDDEN_KEYS guard at method entry
- [ ] Merge base and roleLevel into `merged` object
- [ ] Apply clamped task overrides:
  - `maxBudgetUsd`: `Math.min(taskOverrides.maxBudgetUsd, merged.maxBudgetUsd)`
  - `permissionMode: 'bypassPermissions'`: requires `TAMMA_ALLOW_BYPASS_PERMISSIONS=true`
  - `allowedTools`: intersect with `merged.allowedTools` (filter, not replace)
- [ ] Return `merged`

### Key Code

```typescript
getTaskConfig(
  role: AgentType,
  taskOverrides?: Partial<AgentTaskConfig>,
): Partial<AgentTaskConfig> {
  if (RoleBasedAgentResolver.FORBIDDEN_KEYS.has(role)) {
    throw new Error(`Forbidden role key: ${role}`);
  }

  const defaults = this.config.defaults;
  const roleConfig = this.config.roles?.[role];

  const base: Partial<AgentTaskConfig> = {
    allowedTools: defaults.allowedTools,
    maxBudgetUsd: defaults.maxBudgetUsd,
    permissionMode: defaults.permissionMode,
  };

  const roleLevel: Partial<AgentTaskConfig> = {
    ...(roleConfig?.allowedTools !== undefined && { allowedTools: roleConfig.allowedTools }),
    ...(roleConfig?.maxBudgetUsd !== undefined && { maxBudgetUsd: roleConfig.maxBudgetUsd }),
    ...(roleConfig?.permissionMode !== undefined && { permissionMode: roleConfig.permissionMode }),
  };

  const merged = { ...base, ...roleLevel };

  // Apply task overrides with clamping to prevent escalation
  if (taskOverrides) {
    // Budget: task override cannot exceed the resolved config ceiling
    if (taskOverrides.maxBudgetUsd !== undefined && merged.maxBudgetUsd !== undefined) {
      merged.maxBudgetUsd = Math.min(taskOverrides.maxBudgetUsd, merged.maxBudgetUsd);
    }
    // Permission mode: bypassPermissions in overrides requires env var guard
    if (taskOverrides.permissionMode === 'bypassPermissions') {
      if (process.env['TAMMA_ALLOW_BYPASS_PERMISSIONS'] !== 'true') {
        this.logger?.warn('taskOverrides requested bypassPermissions but TAMMA_ALLOW_BYPASS_PERMISSIONS is not set');
      } else {
        merged.permissionMode = taskOverrides.permissionMode;
      }
    } else if (taskOverrides.permissionMode !== undefined) {
      merged.permissionMode = taskOverrides.permissionMode;
    }
    // Allowed tools: overrides can only restrict, not expand
    if (taskOverrides.allowedTools !== undefined && merged.allowedTools !== undefined) {
      merged.allowedTools = taskOverrides.allowedTools.filter(t => merged.allowedTools!.includes(t));
    }
  }

  return merged;
}
```

### Files to Modify

- `packages/providers/src/role-based-agent-resolver.ts` -- ADD: getTaskConfig() method
- `packages/providers/src/role-based-agent-resolver.test.ts` -- ADD: merge precedence tests

### Dependencies

- [ ] Task 1: Class skeleton and constructor
- [ ] Story 9-1: AgentsConfig with defaults and roles fields

## Testing Strategy

### Unit Tests

- [ ] Test: defaults only -- when no role config and no overrides, returns defaults values
  ```typescript
  const config: AgentsConfig = {
    defaults: {
      providerChain: [{ provider: 'claude-code' }],
      allowedTools: ['Read', 'Write'],
      maxBudgetUsd: 1.0,
      permissionMode: 'default',
    },
  };
  const result = resolver.getTaskConfig('researcher');
  expect(result).toEqual({
    allowedTools: ['Read', 'Write'],
    maxBudgetUsd: 1.0,
    permissionMode: 'default',
  });
  ```
- [ ] Test: role config overrides defaults
  ```typescript
  const config: AgentsConfig = {
    defaults: {
      providerChain: [{ provider: 'claude-code' }],
      maxBudgetUsd: 1.0,
      permissionMode: 'default',
    },
    roles: {
      implementer: {
        providerChain: [{ provider: 'claude-code' }],
        maxBudgetUsd: 5.0,
        allowedTools: ['Read', 'Write', 'Edit', 'Bash'],
      },
    },
  };
  const result = resolver.getTaskConfig('implementer');
  expect(result.maxBudgetUsd).toBe(5.0);
  expect(result.allowedTools).toEqual(['Read', 'Write', 'Edit', 'Bash']);
  expect(result.permissionMode).toBe('default'); // from defaults
  ```
- [ ] Test: task overrides win over role config
  ```typescript
  const result = resolver.getTaskConfig('implementer', { maxBudgetUsd: 10.0 });
  expect(result.maxBudgetUsd).toBe(10.0);
  ```
- [ ] Test: partial role config -- only defined fields override (undefined fields do not clobber defaults)
  ```typescript
  // Role config only sets maxBudgetUsd, not allowedTools or permissionMode
  const config: AgentsConfig = {
    defaults: {
      providerChain: [...],
      allowedTools: ['Read'],
      maxBudgetUsd: 1.0,
      permissionMode: 'default',
    },
    roles: {
      architect: {
        providerChain: [...],
        maxBudgetUsd: 2.0,
        // allowedTools and permissionMode NOT set
      },
    },
  };
  const result = resolver.getTaskConfig('architect');
  expect(result.allowedTools).toEqual(['Read']); // from defaults
  expect(result.maxBudgetUsd).toBe(2.0); // from role
  expect(result.permissionMode).toBe('default'); // from defaults
  ```
- [ ] Test: undefined role (no entry in roles map) returns defaults only
- [ ] Test: empty taskOverrides `{}` does not affect result
- [ ] Test: config objects are not mutated after getTaskConfig call
- [ ] Test: `getTaskConfig('__proto__')` throws forbidden key error
- [ ] Test: `getTaskConfig('constructor')` throws forbidden key error
- [ ] Test: clamping -- `maxBudgetUsd: 10.0` override is clamped to resolved ceiling (e.g., 5.0)
  ```typescript
  // role config sets maxBudgetUsd: 5.0
  const result = resolver.getTaskConfig('implementer', { maxBudgetUsd: 10.0 });
  expect(result.maxBudgetUsd).toBe(5.0); // clamped to ceiling
  ```
- [ ] Test: clamping -- `maxBudgetUsd: 2.0` override below ceiling is used as-is
  ```typescript
  const result = resolver.getTaskConfig('implementer', { maxBudgetUsd: 2.0 });
  expect(result.maxBudgetUsd).toBe(2.0); // below ceiling, used as-is
  ```
- [ ] Test: clamping -- `permissionMode: 'bypassPermissions'` is ignored when env var not set (warn logged)
- [ ] Test: clamping -- `permissionMode: 'bypassPermissions'` is applied when `TAMMA_ALLOW_BYPASS_PERMISSIONS=true`
- [ ] Test: clamping -- `permissionMode: 'plan'` (non-bypass) is applied without env var check
- [ ] Test: clamping -- `allowedTools: ['Read', 'Bash']` override intersected with resolved `['Read', 'Write']` produces `['Read']`
- [ ] Test: clamping -- `allowedTools` override with no overlap produces empty array `[]`

### Validation Steps

1. [ ] Implement getTaskConfig method
2. [ ] Write tests for defaults-only case
3. [ ] Write tests for role override case
4. [ ] Write tests for task override case
5. [ ] Write tests for full 3-level merge
6. [ ] Write tests for partial role config (conditional spread)
7. [ ] Write tests for immutability
8. [ ] Verify TypeScript compilation

## Notes & Considerations

- **Conditional spread pattern**: The `...(roleConfig?.allowedTools !== undefined && { allowedTools: roleConfig.allowedTools })` pattern ensures that `undefined` role fields do not appear in the spread at all. This is important because `{ ...{ allowedTools: ['a'] }, ...{ allowedTools: undefined } }` would result in `{ allowedTools: undefined }`, which is wrong.
- **Fields merged**: Only the fields listed in `MERGEABLE_FIELDS` (`allowedTools`, `maxBudgetUsd`, `permissionMode`) participate in the merge cascade. Other AgentTaskConfig fields (`prompt`, `cwd`, `model`, `outputFormat`, `sessionId`) are set by the engine at call time, not by the resolver config.
- **Clamping prevents escalation**: Task overrides cannot increase `maxBudgetUsd` beyond the resolved ceiling, cannot enable `bypassPermissions` without an explicit env var, and cannot expand `allowedTools` beyond the resolved set.
- **This method is synchronous**: Unlike `getAgentForRole`, `getTaskConfig` does no async operations.
- **Engine usage (Story 9-9)**: The engine calls `resolver.getTaskConfig('architect')` to get defaults, then spreads its own prompt and cwd on top.

## Completion Checklist

- [ ] `getTaskConfig()` implemented with 3-level merge and clamping
- [ ] FORBIDDEN_KEYS guard at method entry
- [ ] Conditional spread for undefined role fields
- [ ] Immutable merge (no mutation of config objects)
- [ ] Clamping: maxBudgetUsd cannot exceed resolved ceiling
- [ ] Clamping: bypassPermissions requires TAMMA_ALLOW_BYPASS_PERMISSIONS env var
- [ ] Clamping: allowedTools intersected (not replaced)
- [ ] Defaults-only test passing
- [ ] Role override test passing
- [ ] Clamping tests passing (budget, permissions, tools)
- [ ] Forbidden key rejection tests passing
- [ ] Partial role config test passing
- [ ] Undefined role test passing
- [ ] Immutability test passing
- [ ] TypeScript strict mode compilation verified
