---
title: "Task 1: Implement RoleBasedAgentResolver Core (Constructor, Phase-to-Role Mapping)"
sidebar:
  order: 90
---

**Story:** 9-8-role-based-agent-resolver - Role-Based Agent Resolver
**Epic:** 9

## Task Description

Create the `RoleBasedAgentResolver` class with an options-object constructor and implement the `getAgentForPhase()` method that maps workflow phases to agent roles. This task establishes the class skeleton and the phase-to-role resolution layer that the engine will consume.

## Acceptance Criteria

- `RoleBasedAgentResolverOptions` interface defined with all 8 fields (config, factory, health, promptRegistry, diagnostics, costTracker?, sanitizer?, logger?)
- Constructor accepts a single options object (no positional parameters)
- All dependencies stored as `private readonly` fields
- `chains` initialized as `Map<AgentType, IProviderChain>`
- `getAgentForPhase(phase, context)` resolves phase to role via `config.phaseRoleMap` then `DEFAULT_PHASE_ROLE_MAP` fallback
- `getAgentForPhase()` delegates to `getAgentForRole()` (implemented as stub in this task, completed in Task 2)
- Unit tests pass for phase-to-role mapping and custom phaseRoleMap overrides

## Implementation Details

### Technical Requirements

- [ ] Create `packages/providers/src/role-based-agent-resolver.ts`
- [ ] Define `IRoleBasedAgentResolver` interface with `getAgentForPhase()`, `getAgentForRole()`, `getTaskConfig()`, `getPrompt()`, `getRoleForPhase()`, `dispose()` methods
- [ ] Define `RoleBasedAgentResolverOptions` interface:
  ```typescript
  export interface RoleBasedAgentResolverOptions {
    config: AgentsConfig;
    factory: IAgentProviderFactory;
    health: IProviderHealthTracker;
    promptRegistry: IAgentPromptRegistry;
    diagnostics: DiagnosticsQueue;
    costTracker?: ICostTracker;
    sanitizer?: IContentSanitizer;
    logger?: ILogger;
  }
  ```
- [ ] Implement constructor that destructures the options object into private readonly fields
- [ ] Add constructor validation: throw if `config.defaults.providerChain` is empty or missing
- [ ] Add `private static readonly FORBIDDEN_KEYS = new Set(['__proto__', 'constructor', 'prototype'])`
- [ ] Add `private static readonly MERGEABLE_FIELDS = ['allowedTools', 'maxBudgetUsd', 'permissionMode'] as const`
- [ ] Initialize `private chains = new Map<AgentType, IProviderChain>()`
- [ ] Implement `getAgentForPhase(phase: WorkflowPhase, context: { projectId: string; engineId: string })`:
  - Look up `this.config.phaseRoleMap?.[phase]` first
  - Fall back to `DEFAULT_PHASE_ROLE_MAP[phase]`
  - Runtime validation: reject forbidden keys via `FORBIDDEN_KEYS.has(role)`
  - Delegate to `this.getAgentForRole(role, context)`
- [ ] Implement `getRoleForPhase(phase: WorkflowPhase): AgentType` — synchronous method returning the role without creating a provider

### Files to Create/Modify

- `packages/providers/src/role-based-agent-resolver.ts` -- CREATE: Main class file
- `packages/providers/src/role-based-agent-resolver.test.ts` -- CREATE: Unit tests (Task 1 subset)

### Imports Required

```typescript
import type { AgentType } from '@tamma/shared';
import type { ILogger } from '@tamma/shared/contracts';
import type { AgentsConfig, WorkflowPhase, ProviderChainEntry } from '@tamma/shared/src/types/agent-config.js';
import type { ICostTracker } from '@tamma/cost-monitor';
import type { DiagnosticsQueue } from '@tamma/shared/src/telemetry/index.js';
import type { AgentTaskConfig } from './agent-types.js';
import type { IAgentProvider } from './agent-types.js';
import type { IAgentProviderFactory } from './agent-provider-factory.js';
import type { IProviderHealthTracker } from './types.js';
import type { IProviderChain } from './provider-chain.js';
import type { IAgentPromptRegistry } from './agent-prompt-registry.js';
import type { IContentSanitizer } from '@tamma/shared/security';
import { DEFAULT_PHASE_ROLE_MAP } from '@tamma/shared/src/types/agent-config.js';
import { ProviderChain } from './provider-chain.js';
import { SecureAgentProvider } from './secure-agent-provider.js';
```

### Dependencies

- [ ] Story 9-1: AgentsConfig, WorkflowPhase, DEFAULT_PHASE_ROLE_MAP types
- [ ] Story 9-4: IAgentProviderFactory interface
- [ ] Story 9-3: IProviderHealthTracker interface
- [ ] Story 9-6: IAgentPromptRegistry interface
- [ ] Story 9-5: IProviderChain interface, ProviderChain class
- [ ] Story 9-7: SecureAgentProvider, IContentSanitizer
- [ ] Story 9-2/11: DiagnosticsQueue

## Testing Strategy

### Unit Tests

- [ ] Test: constructor stores all provided options as instance fields
- [ ] Test: constructor accepts options object (verify no positional param constructor exists)
- [ ] Test: `getAgentForPhase('PLAN_GENERATION', ctx)` resolves to 'architect' via DEFAULT_PHASE_ROLE_MAP
- [ ] Test: `getAgentForPhase('CODE_GENERATION', ctx)` resolves to 'implementer'
- [ ] Test: `getAgentForPhase('ISSUE_SELECTION', ctx)` resolves to 'scrum_master'
- [ ] Test: `getAgentForPhase('CODE_REVIEW', ctx)` resolves to 'reviewer'
- [ ] Test: custom `phaseRoleMap: { PLAN_GENERATION: 'researcher' }` overrides default
- [ ] Test: custom phaseRoleMap for one phase does not affect other phases (they still use DEFAULT_PHASE_ROLE_MAP)
- [ ] Test: `getAgentForPhase` calls `getAgentForRole` with the resolved role and context
- [ ] Test: constructor throws when `defaults.providerChain` is empty `[]`
- [ ] Test: constructor throws when `defaults.providerChain` is missing/undefined
- [ ] Test: `getAgentForPhase` throws on forbidden role key (e.g., `__proto__` in custom phaseRoleMap)
- [ ] Test: `getRoleForPhase('PLAN_GENERATION')` returns 'architect' synchronously
- [ ] Test: `getRoleForPhase` uses custom phaseRoleMap when configured
- [ ] Test: class implements `IRoleBasedAgentResolver` interface

### Mock Setup

```typescript
// Mock all dependencies using interfaces
const mockFactory = {} as IAgentProviderFactory;
const mockHealth = {} as IProviderHealthTracker;
const mockPromptRegistry = {} as IAgentPromptRegistry;
const mockDiagnostics = {} as DiagnosticsQueue;

const mockConfig: AgentsConfig = {
  defaults: {
    providerChain: [{ provider: 'claude-code' }],
  },
};
```

### Validation Steps

1. [ ] Create class file with full type definitions
2. [ ] Implement constructor with options destructuring
3. [ ] Implement getAgentForPhase with phase-to-role resolution
4. [ ] Write unit tests for all DEFAULT_PHASE_ROLE_MAP entries
5. [ ] Write unit tests for custom phaseRoleMap override
6. [ ] Verify TypeScript compilation under strict mode

## Notes & Considerations

- The `getAgentForRole()` method is implemented as a forward declaration in this task -- it will be fully implemented in Task 2. For testing `getAgentForPhase()`, spy on `getAgentForRole()` to verify delegation.
- The `DEFAULT_PHASE_ROLE_MAP` is imported from `@tamma/shared/src/types/agent-config.js`, not redefined locally.
- The `??` operator correctly handles `undefined` from partial phaseRoleMap -- if the phase is not in the custom map, it falls through to the default.
- All 8 workflow phases should be tested: ISSUE_SELECTION, CONTEXT_ANALYSIS, PLAN_GENERATION, CODE_GENERATION, PR_CREATION, CODE_REVIEW, TEST_EXECUTION, STATUS_MONITORING.
- The `IRoleBasedAgentResolver` interface must be defined alongside the class. This interface is consumed by Story 9-9 (engine integration) for dependency inversion.
- The `RoleBasedAgentResolverOptions` must use interface types (`IAgentProviderFactory`, `IProviderHealthTracker`, `IAgentPromptRegistry`, `IContentSanitizer`), NOT concrete classes.

## Completion Checklist

- [ ] `IRoleBasedAgentResolver` interface defined
- [ ] `RoleBasedAgentResolverOptions` interface defined (using interface types: `IAgentProviderFactory`, `IProviderHealthTracker`, `IAgentPromptRegistry`, `IContentSanitizer`)
- [ ] Constructor accepts options object
- [ ] Constructor validates `defaults.providerChain` is non-empty
- [ ] All fields stored as private readonly
- [ ] `FORBIDDEN_KEYS` and `MERGEABLE_FIELDS` static readonly sets defined
- [ ] `chains` Map initialized with `IProviderChain` type
- [ ] `getAgentForPhase()` resolves via phaseRoleMap then DEFAULT_PHASE_ROLE_MAP
- [ ] `getAgentForPhase()` validates role against FORBIDDEN_KEYS
- [ ] `getAgentForPhase()` delegates to `getAgentForRole()`
- [ ] `getRoleForPhase()` returns role synchronously
- [ ] All phase mapping tests passing
- [ ] Custom phaseRoleMap override test passing
- [ ] Constructor validation tests passing
- [ ] Forbidden key rejection tests passing
- [ ] TypeScript strict mode compilation verified
