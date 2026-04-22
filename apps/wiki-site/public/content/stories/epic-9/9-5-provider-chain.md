---
title: "Story 9-5: Provider Chain API"
sidebar:
  order: 90
---

## User Story

As a platform operator, I want a single API endpoint that resolves the provider chain for a given (accountId, role, action), so that both the TS engine and Elsa workflows get the same ordered provider list with the same fallback behavior.

## Goal

Expose the existing `ProviderChain` resolution logic via a Fastify API endpoint. Given an account, role, and action context, the API returns the ordered provider chain with health and budget pre-filtering applied. Both consumers call this endpoint instead of independently resolving chains.

## Acceptance Criteria

1. API endpoint:
   - `POST /api/v1/providers/chain/resolve` -- given `{ accountId, role, agentType, projectId, engineId }`, returns the resolved provider chain with health and budget status per entry.
2. The response includes:
   - Ordered list of provider entries with health status (healthy/circuit-open/half-open)
   - Budget status per provider (allowed/exceeded)
   - The recommended first-available provider
3. The existing `ProviderChain` class in `packages/providers/src/provider-chain.ts` remains the core implementation. The API wraps it.
4. Health checks use the shared persistent store (Story 9-3), not in-memory-only state.
5. Budget checks use the diagnostics store (Story 9-2), not in-memory cost tracker only.
6. Empty chain returns an explicit error with `EMPTY_PROVIDER_CHAIN` code.
7. All providers exhausted returns `NO_AVAILABLE_PROVIDER` with per-provider error details.

## Technical Context

### Existing Files

- `packages/providers/src/provider-chain.ts` -- `ProviderChain`, `IProviderChain`, `ProviderChainOptions`, `ProviderChainContext`
- `packages/providers/src/provider-health.ts` -- `ProviderHealthTracker` (in-memory, to be backed by persistent store)
- `packages/providers/src/agent-provider-factory.ts` -- `AgentProviderFactory`
- `packages/providers/src/errors.ts` -- `createProviderError()`, `isProviderError()`
- `packages/providers/src/instrumented-agent-provider.ts` -- wraps resolved providers

### API Routes

```
POST /api/v1/providers/chain/resolve
  → Body: {
      accountId?: string,     // from JWT if not provided
      role: AgentType,
      projectId: string,
      engineId: string
    }
  → Resolution:
    1. Load AgentsConfig for account (Story 9-1)
    2. Determine provider chain entries for role (role-specific or defaults)
    3. For each entry, check health (Story 9-3) and budget (Story 9-2)
    4. Return ordered list with status
  → Returns: {
      entries: [{
        provider: string,
        model: string,
        healthy: boolean,
        budgetAllowed: boolean,
        recommended: boolean
      }],
      recommendedProvider: string | null,
      allExhausted: boolean
    }
```

### Architecture

```
Elsa Workflow (C#)              TS Engine (in-process)
      │                                │
  POST /providers/chain/resolve   ProviderChain.getProvider()
      │                                │
      └──────► ChainResolverService ◄──┘
                     │
              ┌──────┴──────┐
              │             │
         HealthStore    DiagnosticsStore
         (Story 9-3)    (Story 9-2)
```

### Note on TS Engine Path

The TS engine continues to use `ProviderChain.getProvider()` in-process for performance (avoids HTTP overhead for every provider resolution). However, the chain now reads health state from the shared persistent store, ensuring consistency with Elsa's view.

## Files

- CREATE `packages/api/src/services/chain-resolver.ts` -- wraps ProviderChain with persistent health/budget
- CREATE `packages/api/src/services/chain-resolver.test.ts`
- CREATE `packages/api/src/routes/settings/chain-routes.ts` -- POST resolve endpoint
- MODIFY `packages/providers/src/provider-chain.ts` -- accept `IProviderHealthTracker` that may read from persistent store (no breaking changes; interface is already abstract)

## Dependencies

- **Story 9-2** (diagnostics store for budget checks)
- **Story 9-3** (health store for circuit breaker state)
- **Story 9-4** (factory for creating providers)

## Effort Estimate

**14 hours**

- 4h: Chain resolver service (wraps ProviderChain with persistent stores)
- 4h: API route with detailed response model
- 3h: Integration with health store and diagnostics store
- 3h: Tests (resolution logic, health filtering, budget filtering, empty chain, all exhausted)
