---
title: "Story 9-4: Provider Factory API"
sidebar:
  order: 90
---

## User Story

As a platform operator, I want a centralized API endpoint that creates provider instances, so that Elsa workflows call this API instead of maintaining their own factory logic in C# (`CallLlmActivity.cs`).

## Goal

Expose the existing `AgentProviderFactory` functionality via a Fastify API endpoint. The TS engine continues to use the factory in-process. Elsa calls the API endpoint to trigger provider creation (the actual LLM call goes through the TS engine, not directly from C#).

## Acceptance Criteria

1. API endpoint:
   - `POST /api/v1/providers/create` -- given a `ProviderChainEntry`, creates and initializes a provider, returning a session handle.
   - `POST /api/v1/providers/:handle/execute` -- executes a task on the provider identified by handle.
   - `DELETE /api/v1/providers/:handle` -- disposes the provider.
2. The existing `AgentProviderFactory` class in `packages/providers/src/agent-provider-factory.ts` remains the core implementation. The API service wraps it.
3. Provider instances are tracked by session handle (UUID) with TTL-based cleanup for abandoned sessions.
4. The `wrapAsAgent()` function continues to work for LLM providers (OpenRouter, ZenMCP).
5. Custom provider registration via `register()` is exposed for plugin support.
6. All existing factory behaviors preserved: name validation, lock mechanism, `resolveApiKey()`, duck-typing detection.

## Technical Context

### Existing Files

- `packages/providers/src/agent-provider-factory.ts` -- `AgentProviderFactory`, `IAgentProviderFactory`, `wrapAsAgent()`, `resolveApiKey()`, `BUILTIN_PROVIDER_NAMES`
- `packages/providers/src/agent-types.ts` -- `IAgentProvider`, `AgentTaskConfig`, `AgentProgressCallback`
- `packages/providers/src/claude-agent-provider.ts` -- ClaudeAgentProvider (CLI subprocess)
- `packages/providers/src/opencode-provider.ts` -- OpenCodeProvider (CLI subprocess)
- `packages/providers/src/openrouter-provider.ts` -- OpenRouterProvider (LLM API)
- `packages/providers/src/zen-mcp-provider.ts` -- ZenMCPProvider (LLM API)
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmActivity.cs` -- C# HTTP calls to LLM providers directly (to be replaced)

### API Routes

```
POST /api/v1/providers/create
  → Body: { provider, model?, apiKeyRef?, config? }
  → Creates provider via AgentProviderFactory.create()
  → Returns: { handle: UUID, provider: string, model: string }

POST /api/v1/providers/:handle/execute
  → Body: AgentTaskConfig
  → Calls provider.executeTask()
  → Returns: AgentTaskResult

DELETE /api/v1/providers/:handle
  → Calls provider.dispose()
  → Removes from session map
  → Returns: { disposed: true }
```

### Session Management

Provider instances are stateful (subprocess providers like claude-code maintain state). Sessions are tracked in a `Map<string, { provider, createdAt, lastUsed }>` with a periodic cleanup sweep for sessions idle > 30 minutes.

### Architecture

```
Elsa Workflow (C#)                   TS Engine (in-process)
      │                                     │
  POST /providers/create              AgentProviderFactory.create()
  POST /providers/:handle/execute     provider.executeTask()
  DELETE /providers/:handle           provider.dispose()
      │                                     │
      └──────► ProviderSessionService ◄─────┘
                      │
               AgentProviderFactory (shared)
```

## Files

- CREATE `packages/api/src/services/provider-session.ts` -- session management wrapping factory
- CREATE `packages/api/src/services/provider-session.test.ts`
- CREATE `packages/api/src/routes/settings/providers-factory-routes.ts` -- API routes
- No changes to `packages/providers/src/agent-provider-factory.ts` (used as-is)

## Dependencies

- None (factory is self-contained; no account scoping needed)

## Effort Estimate

**12 hours**

- 3h: Provider session service (create, execute, dispose, TTL cleanup)
- 4h: API routes with input validation and error handling
- 2h: Handle-based lifecycle management
- 3h: Tests (session management, TTL cleanup, concurrent access)
