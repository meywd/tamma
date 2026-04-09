# Story 9-8: Unified Agent Resolver API — Implementation Plan

## Overview

Expose the existing `RoleBasedAgentResolver` as unified API endpoints that tie together config (9-1), health (9-3), factory (9-4), chain (9-5), sanitization (9-7), and prompts (Epic 27). Both the TS engine (in-process) and Elsa workflows (via HTTP) use identical resolution logic. The `AgentResolverService` wraps the in-process resolver with store-backed dependencies.

---

## Step-by-Step Implementation Tasks

### Task 1: Create AgentResolverService (5 hours)

**File to create**: `packages/api/src/services/agent-resolver.ts`

This is the top-level orchestration service that coordinates all other stores.

```typescript
import type { AgentType, WorkflowPhase, ProviderChainEntry } from '@tamma/shared';
import { DEFAULT_PHASE_ROLE_MAP } from '@tamma/shared';
import type { IAgentConfigStore, AgentConfigResult } from './agent-config-store.js';
import type { IHealthStore, HealthStatus } from './health-store.js';
import type { IChainResolverService, ChainEntryStatus } from './chain-resolver.js';
import type { ISanitizationStore } from './sanitization-store.js';
import type { IPromptStore } from './prompt-store.js';

/** FORBIDDEN_KEYS prototype pollution guard (mirrors resolver). */
const FORBIDDEN_KEYS = new Set(['__proto__', 'constructor', 'prototype']);

/** Request for resolving by role. */
export interface ResolveByRoleRequest {
  accountId: string | null;
  role: AgentType;
  projectId: string;
  engineId: string;
}

/** Request for resolving by workflow phase. */
export interface ResolveByPhaseRequest {
  accountId: string | null;
  phase: WorkflowPhase;
  projectId: string;
  engineId: string;
  taskOverrides?: {
    maxBudgetUsd?: number;
    permissionMode?: string;
    allowedTools?: string[];
    prompt?: string;
  };
}

/** Merged task configuration from 3-level merge with clamping. */
export interface ResolvedTaskConfig {
  allowedTools?: string[];
  maxBudgetUsd?: number;
  permissionMode?: string;
}

/** Full resolution result. */
export interface AgentResolveResult {
  role: AgentType;
  phase?: WorkflowPhase;
  provider: { name: string; model: string } | null;
  taskConfig: ResolvedTaskConfig;
  systemPrompt: string | null;
  sanitizationEnabled: boolean;
  chainEntries: ChainEntryStatus[];
  allExhausted: boolean;
}

/** Interface for the agent resolver service. */
export interface IAgentResolverService {
  resolveByRole(request: ResolveByRoleRequest): Promise<AgentResolveResult>;
  resolveByPhase(request: ResolveByPhaseRequest): Promise<AgentResolveResult>;
  getRoleForPhase(accountId: string | null, phase: WorkflowPhase): Promise<AgentType>;
}

export class AgentResolverService implements IAgentResolverService {
  constructor(
    private readonly configStore: IAgentConfigStore,
    private readonly chainResolver: IChainResolverService,
    private readonly sanitizationStore: ISanitizationStore,
    private readonly promptStore?: IPromptStore,
  ) {}

  async resolveByRole(request: ResolveByRoleRequest): Promise<AgentResolveResult> {
    // Validate role
    if (FORBIDDEN_KEYS.has(request.role)) {
      throw new Error(`Forbidden role name: "${request.role}"`);
    }

    // 1. Load config
    const configResult = await this.configStore.get(request.accountId);
    const agentsConfig = configResult.config;

    // 2. Resolve provider chain with health + budget
    const chainResult = await this.chainResolver.resolve({
      accountId: request.accountId ?? undefined,
      role: request.role,
      projectId: request.projectId,
      engineId: request.engineId,
    });

    // 3. Get task config (3-level merge with clamping)
    const taskConfig = this._mergeTaskConfig(agentsConfig, request.role);

    // 4. Get prompt (via Epic 27 Prompt Store if available)
    let systemPrompt: string | null = null;
    if (this.promptStore) {
      systemPrompt = (await this.promptStore.getSystemPrompt(request.accountId, request.role)) ?? null;
    }
    // Fallback to config-level system prompt
    if (systemPrompt === null) {
      const roleConfig = agentsConfig.roles?.[request.role];
      systemPrompt = roleConfig?.systemPrompt ?? agentsConfig.defaults.systemPrompt ?? null;
    }

    // 5. Get sanitization status
    const sanitizationRules = await this.sanitizationStore.getRules(request.accountId);
    const sanitizationEnabled = sanitizationRules.enabled;

    // 6. Determine recommended provider
    let provider: { name: string; model: string } | null = null;
    if (chainResult.recommendedProvider !== null) {
      const recommended = chainResult.entries.find((e) => e.recommended);
      if (recommended) {
        provider = { name: recommended.provider, model: recommended.model };
      }
    }

    return {
      role: request.role,
      provider,
      taskConfig,
      systemPrompt,
      sanitizationEnabled,
      chainEntries: chainResult.entries,
      allExhausted: chainResult.allExhausted,
    };
  }

  async resolveByPhase(request: ResolveByPhaseRequest): Promise<AgentResolveResult> {
    const role = await this.getRoleForPhase(request.accountId, request.phase);
    const result = await this.resolveByRole({
      accountId: request.accountId,
      role,
      projectId: request.projectId,
      engineId: request.engineId,
    });

    // Apply task overrides with clamping
    if (request.taskOverrides) {
      this._applyTaskOverrides(result.taskConfig, request.taskOverrides);
    }

    return { ...result, phase: request.phase };
  }

  async getRoleForPhase(accountId: string | null, phase: WorkflowPhase): Promise<AgentType> {
    const configResult = await this.configStore.get(accountId);
    const customRole = configResult.config.phaseRoleMap?.[phase];
    const role = customRole ?? DEFAULT_PHASE_ROLE_MAP[phase];
    if (FORBIDDEN_KEYS.has(role)) {
      throw new Error(`Forbidden role name resolved for phase "${phase}": "${role}"`);
    }
    return role;
  }

  /**
   * 3-level merge: defaults < role < overrides with clamping.
   * Mirrors RoleBasedAgentResolver.getTaskConfig() logic.
   */
  private _mergeTaskConfig(config: import('@tamma/shared').IAgentsConfig, role: AgentType): ResolvedTaskConfig {
    const result: ResolvedTaskConfig = {};

    // Level 1: Defaults
    if (config.defaults.allowedTools !== undefined) result.allowedTools = [...config.defaults.allowedTools];
    if (config.defaults.maxBudgetUsd !== undefined) result.maxBudgetUsd = config.defaults.maxBudgetUsd;
    if (config.defaults.permissionMode !== undefined) result.permissionMode = config.defaults.permissionMode;

    // Level 2: Role overrides
    const roleConfig = config.roles?.[role];
    if (roleConfig) {
      if (roleConfig.allowedTools !== undefined) result.allowedTools = [...roleConfig.allowedTools];
      if (roleConfig.maxBudgetUsd !== undefined) result.maxBudgetUsd = roleConfig.maxBudgetUsd;
      if (roleConfig.permissionMode !== undefined) result.permissionMode = roleConfig.permissionMode;
    }

    return result;
  }

  /**
   * Apply task overrides with clamping:
   * - maxBudgetUsd: min(override, ceiling)
   * - bypassPermissions: requires env var
   * - allowedTools: intersection only
   */
  private _applyTaskOverrides(config: ResolvedTaskConfig, overrides: ResolveByPhaseRequest['taskOverrides']): void {
    if (!overrides) return;

    if (overrides.maxBudgetUsd !== undefined) {
      const ceiling = config.maxBudgetUsd;
      config.maxBudgetUsd = ceiling !== undefined
        ? Math.min(overrides.maxBudgetUsd, ceiling)
        : overrides.maxBudgetUsd;
    }

    if (overrides.permissionMode !== undefined) {
      if (overrides.permissionMode === 'bypassPermissions') {
        if (process.env['TAMMA_ALLOW_BYPASS_PERMISSIONS'] === 'true') {
          config.permissionMode = 'bypassPermissions';
        }
        // else: keep current (clamped)
      } else {
        config.permissionMode = overrides.permissionMode;
      }
    }

    if (overrides.allowedTools !== undefined) {
      const current = config.allowedTools;
      if (current && current.length > 0) {
        const currentSet = new Set(current);
        config.allowedTools = overrides.allowedTools.filter((t) => currentSet.has(t));
      } else {
        config.allowedTools = [...overrides.allowedTools];
      }
    }
  }
}
```

---

### Task 2: Implement Fastify Routes (4 hours)

**File to modify**: `packages/api/src/routes/settings/agents-routes.ts`

Add resolver endpoints alongside config CRUD:

```typescript
import type { IAgentResolverService, ResolveByPhaseRequest } from '../../services/agent-resolver.js';

// Within registerAgentsRoutes (or a new registerAgentResolverRoutes):

// GET /api/v1/agents/:role/resolve
app.get('/agents/:role/resolve', {
  schema: {
    params: { type: 'object', properties: { role: { type: 'string' } } },
    querystring: {
      type: 'object',
      properties: {
        projectId: { type: 'string' },
        engineId: { type: 'string' },
      },
    },
    response: {
      200: {
        type: 'object',
        properties: {
          role: { type: 'string' },
          provider: { type: ['object', 'null'] },
          taskConfig: { type: 'object' },
          systemPrompt: { type: ['string', 'null'] },
          sanitizationEnabled: { type: 'boolean' },
          chainEntries: { type: 'array' },
          allExhausted: { type: 'boolean' },
        },
      },
    },
  },
}, async (request, reply) => {
  const { role } = request.params as { role: string };
  const { projectId, engineId } = request.query as { projectId?: string; engineId?: string };
  const accountId = (request as any).accountId ?? null;
  const result = await resolverService.resolveByRole({
    accountId,
    role: role as AgentType,
    projectId: projectId ?? '',
    engineId: engineId ?? '',
  });
  return reply.send(result);
});

// POST /api/v1/agents/resolve-for-phase
app.post('/agents/resolve-for-phase', {
  schema: {
    body: {
      type: 'object',
      required: ['phase', 'projectId', 'engineId'],
      properties: {
        phase: { type: 'string' },
        projectId: { type: 'string' },
        engineId: { type: 'string' },
        taskOverrides: { type: 'object' },
      },
    },
  },
}, async (request, reply) => {
  const body = request.body as ResolveByPhaseRequest;
  const accountId = (request as any).accountId ?? null;
  const result = await resolverService.resolveByPhase({ ...body, accountId });
  return reply.send(result);
});
```

---

### Task 3: Wire into Settings Index (2 hours)

**File to modify**: `packages/api/src/routes/settings/index.ts`

```typescript
import type { IAgentResolverService } from '../services/agent-resolver.js';

export interface SettingsServices {
  // ... existing
  agentResolver: IAgentResolverService;
}

// Construction in createSettingsServices:
const agentResolver = new AgentResolverService(
  configStore,
  chainResolver,
  sanitizationStore,
  promptStore,  // from Epic 27
);
```

---

### Task 4: Tests (5 hours)

**File to create**: `packages/api/src/services/agent-resolver.test.ts`

| # | Test | Assertion |
|---|------|-----------|
| 1 | `resolveByRole()` returns provider from chain | provider.name matches recommended |
| 2 | `resolveByRole()` with all providers exhausted | allExhausted = true, provider = null |
| 3 | `resolveByRole()` includes task config from defaults | maxBudgetUsd, permissionMode present |
| 4 | `resolveByRole()` includes task config from role override | Role config takes precedence |
| 5 | `resolveByRole()` includes system prompt | From prompt store or config |
| 6 | `resolveByRole()` includes sanitization status | Matches account rules |
| 7 | `resolveByRole()` rejects forbidden role name | Error thrown |
| 8 | `resolveByPhase()` maps phase to role | Correct role for PLAN_GENERATION |
| 9 | `resolveByPhase()` uses custom phaseRoleMap | Account override |
| 10 | `resolveByPhase()` applies task overrides | Budget clamped |
| 11 | `resolveByPhase()` clamps bypassPermissions | Denied without env var |
| 12 | `resolveByPhase()` clamps allowedTools (intersection) | Only common tools |
| 13 | `getRoleForPhase()` returns default mapping | Standard roles |
| 14 | `getRoleForPhase()` returns custom mapping | Account override |
| 15 | `getRoleForPhase()` rejects forbidden result | Error thrown |
| 16 | Template injection prevention: strips {{ }} from var values | Variables sanitized |

**File to create**: `packages/api/src/routes/settings/__tests__/agents-resolver-routes.test.ts`

| # | Test | Assertion |
|---|------|-----------|
| 17 | GET /agents/:role/resolve returns 200 | Full resolution result |
| 18 | GET /agents/invalid-role/resolve returns error | Descriptive error |
| 19 | POST /agents/resolve-for-phase returns 200 | Phase + role in result |
| 20 | POST /agents/resolve-for-phase with overrides | Clamped task config |
| 21 | POST /agents/resolve-for-phase with missing fields | 400 validation |

**Total tests**: ~21

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `packages/api/src/services/agent-resolver.ts` | Service + interface |
| 2 | `packages/api/src/services/agent-resolver.test.ts` | Service tests |
| 3 | `packages/api/src/routes/settings/__tests__/agents-resolver-routes.test.ts` | Route tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/api/src/routes/settings/agents-routes.ts` | Add resolve endpoints |
| 2 | `packages/api/src/routes/settings/index.ts` | Wire AgentResolverService |

---

## Dependencies

- **Story 9-1** (IAgentConfigStore for per-account agent config)
- **Story 9-3** (IHealthStore via ChainResolverService for circuit breaker state)
- **Story 9-4** (IAgentProviderFactory via ChainResolverService for provider creation)
- **Story 9-5** (IChainResolverService for provider chain resolution)
- **Story 9-7** (ISanitizationStore for sanitization rules)
- **Epic 27** (IPromptStore for prompt resolution -- optional, falls back to config)

## Migration from Existing Code

1. The existing `RoleBasedAgentResolver` in `packages/providers/src/role-based-agent-resolver.ts` remains unchanged. The TS engine continues using it in-process.
2. `AgentResolverService` is a new API-level service that replicates the same resolution logic using store-backed dependencies instead of in-memory state.
3. The task config merge and clamping logic in `AgentResolverService._mergeTaskConfig()` and `_applyTaskOverrides()` mirrors `RoleBasedAgentResolver.getTaskConfig()` exactly.
4. Elsa's `ResolveAgentConfigActivity.cs` transitions from DB lookups to `GET /api/v1/agents/:role/resolve` (wired in Story 9-11).

---

## Estimated Effort

| Task | Hours |
|------|-------|
| AgentResolverService (ties together all stores) | 5 |
| Fastify routes (GET resolve, POST resolve-for-phase) | 4 |
| Settings index wiring | 2 |
| Tests (21 tests) | 5 |
| **Total** | **16 hours** |
