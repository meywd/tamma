---
title: "Story 9-8: Unified Agent Resolver API"
sidebar:
  order: 90
---

## User Story

As a platform operator, I want a single API endpoint that, given (accountId, role, phase), returns the fully resolved agent configuration (provider, model, tools, budget, prompt), so that both the TS engine and Elsa workflows use identical resolution logic.

## Goal

Expose the existing `RoleBasedAgentResolver` as a unified API endpoint. This is the top-level orchestration endpoint that ties together config (9-1), health (9-3), factory (9-4), chain (9-5), sanitization (9-7), and prompts (Epic 27). Both consumers call this endpoint (or use the resolver in-process for the TS engine).

## Acceptance Criteria

1. API endpoints:
   - `GET /api/v1/agents/:role/resolve` -- resolve full agent configuration for a role. Returns provider, model, tools, budget, system prompt.
   - `POST /api/v1/agents/resolve-for-phase` -- resolve agent for a workflow phase. Maps phase to role first, then resolves.
2. Resolution logic (single path for all callers):
   - Phase -> role mapping via `phaseRoleMap` (account config or default)
   - Role -> provider chain (account config or default)
   - Provider chain -> first healthy, within-budget provider (via health + diagnostics stores)
   - Role -> task config merge (defaults < role < task overrides with clamping)
   - Role + provider -> prompt resolution (via Epic 27 Prompt Store)
   - Provider -> security wrapping (via sanitization rules)
3. The existing `RoleBasedAgentResolver` class in `packages/providers/src/role-based-agent-resolver.ts` remains the core implementation. The API wraps it.
4. Config merge clamping rules preserved:
   - `maxBudgetUsd` cannot exceed ceiling from defaults/role
   - `bypassPermissions` requires `TAMMA_ALLOW_BYPASS_PERMISSIONS=true`
   - `allowedTools` intersection only (restrict, never expand)
5. Template injection prevention preserved (strips `{{` and `}}` from variable values).
6. Elsa's `ResolveAgentConfigActivity.cs` is replaced with a call to this API.

## Technical Context

### Existing Files

- `packages/providers/src/role-based-agent-resolver.ts` -- `RoleBasedAgentResolver`, `IRoleBasedAgentResolver`, `RoleBasedAgentResolverOptions`
- `packages/providers/src/provider-chain.ts` -- `ProviderChain` (chain resolution)
- `packages/providers/src/agent-provider-factory.ts` -- `AgentProviderFactory` (provider creation)
- `packages/providers/src/secure-agent-provider.ts` -- `SecureAgentProvider` (security wrapping)
- `packages/providers/src/agent-prompt-registry.ts` -- `AgentPromptRegistry` (prompt resolution, to be backed by Epic 27)
- `packages/shared/src/types/agent-config.ts` -- `DEFAULT_PHASE_ROLE_MAP`, `ENGINE_STATE_TO_PHASE`
- `packages/api/src/routes/settings/agents-routes.ts` -- placeholder agent routes
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveAgentConfigActivity.cs` -- C# agent resolution (to be replaced)

### API Routes

```
GET /api/v1/agents/:role/resolve
  → accountId from JWT
  → Query params: projectId, engineId
  → Resolution:
    1. Load AgentsConfig for account (Story 9-1)
    2. Get provider chain for role (Story 9-5)
    3. Get task config with clamping (in-process resolver logic)
    4. Get prompt from Prompt Store (Epic 27)
    5. Get sanitization rules (Story 9-7)
  → Returns: {
      role: AgentType,
      provider: { name, model },
      taskConfig: { allowedTools, maxBudgetUsd, permissionMode },
      systemPrompt: string,
      sanitizationEnabled: boolean,
      chainEntries: ProviderChainEntry[]
    }

POST /api/v1/agents/resolve-for-phase
  → Body: { phase: WorkflowPhase, projectId: string, engineId: string, taskOverrides?: Partial<AgentTaskConfig> }
  → accountId from JWT
  → Maps phase to role, then delegates to role resolution
  → Returns: same as GET /agents/:role/resolve plus { phase, role }
```

### Architecture

```
Elsa Workflow (C#)                TS Engine (in-process)
      │                                  │
  GET /agents/:role/resolve       RoleBasedAgentResolver.getAgentForPhase()
  POST /agents/resolve-for-phase         │
      │                                  │
      └──────► AgentResolverService ◄────┘
                     │
         ┌───────────┼───────────┐
         │           │           │
   ConfigStore  HealthStore  PromptStore
   (9-1)        (9-3)        (Epic 27)
```

## Files

- CREATE `packages/api/src/services/agent-resolver.ts` -- wraps RoleBasedAgentResolver with store-backed deps
- CREATE `packages/api/src/services/agent-resolver.test.ts`
- MODIFY `packages/api/src/routes/settings/agents-routes.ts` -- add resolve endpoints
- No changes to `packages/providers/src/role-based-agent-resolver.ts` (used as-is)

## Dependencies

- **Story 9-1** (config store for per-account agent config)
- **Story 9-3** (health store for circuit breaker state)
- **Story 9-4** (factory for provider creation)
- **Story 9-5** (chain resolution)
- **Story 9-7** (sanitization rules)
- **Epic 27** (prompt store for prompt resolution)

## Effort Estimate

**18 hours**

- 5h: Agent resolver service (ties together all stores and the in-process resolver)
- 5h: API routes with comprehensive response models
- 4h: Integration with config, health, chain, prompt, and sanitization stores
- 4h: Tests (resolution logic, clamping, phase mapping, prompt resolution, error handling)
