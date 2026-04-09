# Story 9-5: Provider Chain API — Implementation Plan

## Overview

Expose the existing `ProviderChain` resolution logic via a Fastify API endpoint. Given an account, role, and action context, the API returns the ordered provider chain with per-entry health and budget status. The TS engine continues to use `ProviderChain.getProvider()` in-process, but the chain now reads health state from the shared persistent store (Story 9-3) and budget from the diagnostics store (Story 9-2), ensuring consistency with Elsa's view.

---

## Step-by-Step Implementation Tasks

### Task 1: Create ChainResolverService (4 hours)

**File to create**: `packages/api/src/services/chain-resolver.ts`

```typescript
import type { AgentType, ProviderChainEntry } from '@tamma/shared';
import type { IAgentConfigStore } from './agent-config-store.js';
import type { IHealthStore, HealthStatus } from './health-store.js';
import type { IDiagnosticsStore, BudgetStatus } from './diagnostics-store.js';
import { ProviderHealthTracker } from '@tamma/providers';

/** Request body for chain resolution. */
export interface ChainResolveRequest {
  accountId?: string;
  role: AgentType;
  projectId: string;
  engineId: string;
}

/** Per-entry status in the resolved chain. */
export interface ChainEntryStatus {
  provider: string;
  model: string;
  healthy: boolean;
  circuitOpen: boolean;
  circuitOpenUntil: string | null;
  budgetAllowed: boolean;
  budgetSpent: number;
  recommended: boolean;
}

/** Response from chain resolution. */
export interface ChainResolveResponse {
  entries: ChainEntryStatus[];
  recommendedProvider: string | null;
  allExhausted: boolean;
}

/** Interface for the chain resolver service. */
export interface IChainResolverService {
  resolve(request: ChainResolveRequest): Promise<ChainResolveResponse>;
}

export class ChainResolverService implements IChainResolverService {
  constructor(
    private readonly configStore: IAgentConfigStore,
    private readonly healthStore: IHealthStore,
    private readonly diagnosticsStore: IDiagnosticsStore,
  ) {}

  async resolve(request: ChainResolveRequest): Promise<ChainResolveResponse> {
    // 1. Load config for account
    const config = await this.configStore.get(request.accountId ?? null);
    const agentsConfig = config.config;

    // 2. Determine provider chain for role
    const roleConfig = agentsConfig.roles?.[request.role];
    let entries: ProviderChainEntry[];
    if (roleConfig?.providerChain && roleConfig.providerChain.length > 0) {
      entries = roleConfig.providerChain;
    } else {
      entries = agentsConfig.defaults.providerChain;
    }

    if (entries.length === 0) {
      return { entries: [], recommendedProvider: null, allExhausted: true };
    }

    // 3. For each entry, check health and budget
    const statusEntries: ChainEntryStatus[] = [];
    let recommendedProvider: string | null = null;

    for (const entry of entries) {
      const key = ProviderHealthTracker.buildKey(entry.provider, entry.model);

      // Health check
      const health = await this.healthStore.get(key);
      const healthy = health === null || health.healthy;
      const circuitOpen = health?.circuitOpen ?? false;
      const circuitOpenUntil = health?.circuitOpenUntil ?? null;

      // Budget check (monthly window)
      const budgetLimit = agentsConfig.defaults.maxBudgetUsd ?? 100;
      let budgetAllowed = true;
      let budgetSpent = 0;
      if (request.accountId) {
        const budget = await this.diagnosticsStore.checkBudget(request.accountId, budgetLimit);
        budgetAllowed = budget.remaining > 0;
        budgetSpent = budget.spent;
      }

      const recommended = healthy && budgetAllowed && recommendedProvider === null;
      if (recommended) {
        recommendedProvider = entry.provider;
      }

      statusEntries.push({
        provider: entry.provider,
        model: entry.model ?? 'default',
        healthy,
        circuitOpen,
        circuitOpenUntil,
        budgetAllowed,
        budgetSpent,
        recommended,
      });
    }

    return {
      entries: statusEntries,
      recommendedProvider,
      allExhausted: recommendedProvider === null,
    };
  }
}
```

---

### Task 2: Implement Fastify Route (3 hours)

**File to create**: `packages/api/src/routes/settings/chain-routes.ts`

```typescript
import type { FastifyInstance } from 'fastify';
import type { IChainResolverService, ChainResolveRequest } from '../../services/chain-resolver.js';

export function registerChainRoutes(app: FastifyInstance, service: IChainResolverService): void {
  // POST /api/v1/providers/chain/resolve
  app.post('/providers/chain/resolve', {
    schema: {
      body: {
        type: 'object',
        required: ['role', 'projectId', 'engineId'],
        properties: {
          accountId: { type: 'string', format: 'uuid' },
          role: { type: 'string' },
          projectId: { type: 'string' },
          engineId: { type: 'string' },
        },
      },
      response: {
        200: {
          type: 'object',
          properties: {
            entries: {
              type: 'array',
              items: {
                type: 'object',
                properties: {
                  provider: { type: 'string' },
                  model: { type: 'string' },
                  healthy: { type: 'boolean' },
                  circuitOpen: { type: 'boolean' },
                  circuitOpenUntil: { type: ['string', 'null'] },
                  budgetAllowed: { type: 'boolean' },
                  budgetSpent: { type: 'number' },
                  recommended: { type: 'boolean' },
                },
              },
            },
            recommendedProvider: { type: ['string', 'null'] },
            allExhausted: { type: 'boolean' },
          },
        },
      },
    },
  }, async (request, reply) => {
    const body = request.body as ChainResolveRequest;
    // Use accountId from JWT if not in body
    const accountId = body.accountId ?? (request as any).accountId ?? undefined;
    const result = await service.resolve({ ...body, accountId });
    return reply.send(result);
  });
}
```

---

### Task 3: Wire into Settings Index (1 hour)

**File to modify**: `packages/api/src/routes/settings/index.ts`

```typescript
import { registerChainRoutes } from './chain-routes.js';
import type { IChainResolverService } from '../services/chain-resolver.js';

export interface SettingsServices {
  // ... existing
  chainResolver: IChainResolverService;
}

// In registerSettingsRoutes, within the /api/providers/* block:
registerChainRoutes(instance, svc.chainResolver);
```

---

### Task 4: Ensure ProviderChain Uses Persistent Health (2 hours)

**File to modify**: `packages/providers/src/provider-chain.ts` (minimal)

No breaking changes required. The `IProviderHealthTracker` interface already abstracts the health check. When the in-process `ProviderHealthTracker` is constructed with an `onCircuitChange` callback syncing to `PgHealthStore` (Story 9-3), the chain automatically benefits from shared state.

However, for the API path (Elsa calling `POST /providers/chain/resolve`), the `ChainResolverService` reads health directly from `PgHealthStore` -- this is a separate resolution path that does not go through `ProviderChain.getProvider()`.

Document this two-path architecture in the service file:

```typescript
/**
 * ChainResolverService provides the API-path chain resolution.
 *
 * Two resolution paths exist:
 * 1. TS engine (in-process): ProviderChain.getProvider() -> in-memory ProviderHealthTracker
 *    (synced to Postgres via onCircuitChange callback)
 * 2. API callers (Elsa): POST /providers/chain/resolve -> ChainResolverService -> PgHealthStore
 *
 * Both paths see consistent health state because the in-process tracker syncs to Postgres.
 */
```

---

### Task 5: Tests (4 hours)

**File to create**: `packages/api/src/services/chain-resolver.test.ts`

| # | Test | Assertion |
|---|------|-----------|
| 1 | Resolves default chain when no role-specific chain | Uses defaults.providerChain |
| 2 | Resolves role-specific chain when configured | Uses role's providerChain |
| 3 | Marks unhealthy provider correctly | healthy=false, circuitOpen=true |
| 4 | Marks over-budget provider correctly | budgetAllowed=false |
| 5 | Recommends first healthy, within-budget provider | recommended=true on first available |
| 6 | Returns allExhausted=true when all providers failed | No recommendation |
| 7 | Returns allExhausted=true for empty chain | entries=[] |
| 8 | Multiple providers with mixed health | Only healthy ones considered |
| 9 | Budget check skipped when no accountId | budgetAllowed=true for all |
| 10 | Role not in config falls back to defaults | Default chain used |

**File to create**: `packages/api/src/routes/settings/__tests__/chain-routes.test.ts`

| # | Test | Assertion |
|---|------|-----------|
| 11 | POST /providers/chain/resolve with valid body | 200, correct response shape |
| 12 | POST /providers/chain/resolve with missing role | 400 validation error |
| 13 | POST /providers/chain/resolve with all exhausted | allExhausted=true |
| 14 | POST /providers/chain/resolve uses JWT accountId when not in body | Correct account resolution |

**Total tests**: ~14

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `packages/api/src/services/chain-resolver.ts` | Chain resolution service |
| 2 | `packages/api/src/routes/settings/chain-routes.ts` | POST resolve endpoint |
| 3 | `packages/api/src/services/chain-resolver.test.ts` | Service tests |
| 4 | `packages/api/src/routes/settings/__tests__/chain-routes.test.ts` | Route tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/api/src/routes/settings/index.ts` | Wire chain routes and service |

---

## Dependencies

- **Story 9-1** (IAgentConfigStore for loading per-account provider chains)
- **Story 9-2** (IDiagnosticsStore for budget checks)
- **Story 9-3** (IHealthStore for circuit breaker state)
- **Story 9-4** (IAgentProviderFactory for provider creation -- used in-process only)

## Migration from Existing Code

1. The existing `ProviderChain` class in `packages/providers/src/provider-chain.ts` is unchanged. The TS engine continues using it in-process.
2. `ChainResolverService` is a new service that replicates the chain resolution logic at the API level, reading from persistent stores instead of in-memory state.
3. The two-path architecture is documented: TS engine uses in-process `ProviderChain` with synced health; Elsa uses `ChainResolverService` with `PgHealthStore` directly.

---

## Estimated Effort

| Task | Hours |
|------|-------|
| ChainResolverService | 4 |
| Fastify route | 3 |
| Settings index wiring | 1 |
| Persistent health documentation | 2 |
| Tests (14 tests) | 4 |
| **Total** | **14 hours** |
