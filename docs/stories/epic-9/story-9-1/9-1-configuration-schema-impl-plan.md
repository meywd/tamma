# Story 9-1: Config Schema + API — Implementation Plan

## Overview

Replace the in-memory `ConfigService` with a Postgres-backed `PgAgentConfigStore` and expose Fastify API endpoints for CRUD on per-account agent configuration. Keep backward compatibility with CLI/self-hosted mode via `normalizeAgentsConfig()`.

---

## Step-by-Step Implementation Tasks

### Task 1: Create the Migration SQL File (2 hours)

**File to create**: `database/migrations/012_agent_configs.sql`

```sql
-- Agent configs: per-account agent and security configuration
-- Epic 9, Story 9-1

CREATE TABLE IF NOT EXISTS agent_configs (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  account_id  UUID UNIQUE,  -- NULL = system default; FK deferred to Epic 17
  config      JSONB NOT NULL,
  security    JSONB NOT NULL DEFAULT '{}'::jsonb,
  version     INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
  created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  created_by  UUID,
  updated_by  UUID
);

-- Partial unique index for system default (account_id IS NULL)
CREATE UNIQUE INDEX IF NOT EXISTS idx_agent_configs_system_default
  ON agent_configs (account_id)
  WHERE account_id IS NULL;

-- Lookup index
CREATE INDEX IF NOT EXISTS idx_agent_configs_account_id
  ON agent_configs (account_id)
  WHERE account_id IS NOT NULL;

-- Seed system default
INSERT INTO agent_configs (account_id, config, security, version)
VALUES (
  NULL,
  '{"defaults":{"providerChain":[{"provider":"claude-code"}]}}'::jsonb,
  '{"sanitizeContent":true,"validateUrls":true,"gateActions":false,"maxFetchSizeBytes":10485760,"blockedCommandPatterns":["rm\\s+-rf\\s+/","DROP\\s+TABLE","DELETE\\s+FROM"]}'::jsonb,
  1
)
ON CONFLICT DO NOTHING;
```

---

### Task 2: Define IAgentConfigStore Interface + Types (2 hours)

**File to create**: `packages/api/src/services/agent-config-store.ts`

```typescript
import type pg from 'pg';
import type { IAgentsConfig, SecurityConfig } from '@tamma/shared';

/** Result of reading agent config, including source information. */
export interface AgentConfigResult {
  config: IAgentsConfig;
  security: SecurityConfig;
  source: 'account' | 'system' | 'hardcoded';
  version: number;
}

/** Validation result. */
export interface ConfigValidationResult {
  valid: boolean;
  errors: string[];
}

/** Interface for the agent config store. */
export interface IAgentConfigStore {
  /** Get resolved config: account override -> system default -> hardcoded. */
  get(accountId: string | null): Promise<AgentConfigResult>;
  /** Upsert config for an account (or system default if accountId is null). */
  upsert(
    accountId: string | null,
    config: IAgentsConfig,
    security?: SecurityConfig,
    userId?: string,
  ): Promise<AgentConfigResult>;
  /** Validate config without saving. */
  validate(config: IAgentsConfig, security?: SecurityConfig): ConfigValidationResult;
}

// Hardcoded defaults (fallback of last resort)
const HARDCODED_AGENTS_CONFIG: IAgentsConfig = {
  defaults: {
    providerChain: [{ provider: 'claude-code' }],
  },
};

const HARDCODED_SECURITY: SecurityConfig = {
  sanitizeContent: true,
  validateUrls: true,
  gateActions: false,
  maxFetchSizeBytes: 10_485_760,
  blockedCommandPatterns: ['rm\\s+-rf\\s+/', 'DROP\\s+TABLE', 'DELETE\\s+FROM'],
};
```

---

### Task 3: Implement PgAgentConfigStore (3 hours)

**File to create**: `packages/api/src/services/pg-agent-config-store.ts`

Follows the `PgInstallationStore` pattern: `pg.Pool` via constructor, parameterized queries, `_mapRow()` helper.

```typescript
import type pg from 'pg';
import type { IAgentsConfig, SecurityConfig } from '@tamma/shared';
import { validateAgentsConfig, validateSecurityConfig } from '@tamma/shared';
import type { IAgentConfigStore, AgentConfigResult, ConfigValidationResult } from './agent-config-store.js';

export class PgAgentConfigStore implements IAgentConfigStore {
  constructor(private readonly pool: pg.Pool) {}

  async get(accountId: string | null): Promise<AgentConfigResult> {
    // 1. Try account override
    if (accountId !== null) {
      const override = await this.pool.query<Record<string, unknown>>(
        'SELECT * FROM agent_configs WHERE account_id = $1',
        [accountId],
      );
      if (override.rows.length > 0) return this._mapRow(override.rows[0]!, 'account');
    }
    // 2. System default
    const system = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM agent_configs WHERE account_id IS NULL',
    );
    if (system.rows.length > 0) return this._mapRow(system.rows[0]!, 'system');
    // 3. Hardcoded
    return { config: HARDCODED_AGENTS_CONFIG, security: HARDCODED_SECURITY, source: 'hardcoded', version: 0 };
  }

  async upsert(
    accountId: string | null,
    config: IAgentsConfig,
    security?: SecurityConfig,
    userId?: string,
  ): Promise<AgentConfigResult> {
    // Validate before saving
    const validation = this.validate(config, security);
    if (!validation.valid) {
      throw new Error(`Config validation failed: ${validation.errors.join('; ')}`);
    }

    const configJson = JSON.stringify(config);
    const securityJson = JSON.stringify(security ?? {});

    // UPSERT with ON CONFLICT on account_id (unique constraint)
    const result = await this.pool.query<Record<string, unknown>>(/* ... UPSERT SQL ... */);
    return this._mapRow(result.rows[0]!, accountId !== null ? 'account' : 'system');
  }

  validate(config: IAgentsConfig, security?: SecurityConfig): ConfigValidationResult {
    const errors: string[] = [];
    try { validateAgentsConfig(config); }
    catch (err) { errors.push(err instanceof Error ? err.message : String(err)); }
    if (security) {
      try { validateSecurityConfig(security); }
      catch (err) { errors.push(err instanceof Error ? err.message : String(err)); }
    }
    return { valid: errors.length === 0, errors };
  }

  private _mapRow(row: Record<string, unknown>, source: 'account' | 'system'): AgentConfigResult { /* ... */ }
}
```

---

### Task 4: Implement Fastify Routes (4 hours)

**File to modify**: `packages/api/src/routes/settings/agents-routes.ts`

Replace the placeholder routes with full CRUD:

```typescript
import type { FastifyInstance } from 'fastify';
import type { IAgentConfigStore } from '../../services/agent-config-store.js';

export function registerAgentsRoutes(app: FastifyInstance, store: IAgentConfigStore): void {
  // GET /api/v1/agents/config
  app.get('/agents/config', {
    schema: {
      response: {
        200: {
          type: 'object',
          properties: {
            config: { type: 'object' },
            security: { type: 'object' },
            source: { type: 'string', enum: ['account', 'system', 'hardcoded'] },
            version: { type: 'integer' },
          },
        },
      },
    },
  }, async (request, reply) => {
    const accountId = (request as any).accountId ?? null;
    const result = await store.get(accountId);
    return reply.send(result);
  });

  // PUT /api/v1/agents/config
  app.put('/agents/config', {
    schema: {
      body: {
        type: 'object',
        required: ['config'],
        properties: {
          config: { type: 'object' },
          security: { type: 'object' },
        },
      },
    },
  }, async (request, reply) => {
    const accountId = (request as any).accountId ?? null;
    const userId = (request as any).userId ?? null;
    const body = request.body as { config: IAgentsConfig; security?: SecurityConfig };
    const result = await store.upsert(accountId, body.config, body.security, userId);
    return reply.send(result);
  });

  // POST /api/v1/agents/config/validate
  app.post('/agents/config/validate', async (request, reply) => {
    const body = request.body as { config: IAgentsConfig; security?: SecurityConfig };
    const result = store.validate(body.config, body.security);
    return reply.send(result);
  });
}
```

---

### Task 5: Update Settings Index + Wire PgAgentConfigStore (2 hours)

**File to modify**: `packages/api/src/routes/settings/index.ts`

Update `SettingsServices` to accept `IAgentConfigStore` and wire `PgAgentConfigStore` when a pool is available.

```typescript
export interface SettingsServices {
  configStore: IAgentConfigStore;   // replaces ConfigService for agents
  configService: ConfigService;      // retained for prompts, user providers
  healthService: HealthService;
  diagnosticsService: DiagnosticsService;
}
```

**File to modify**: `packages/api/src/routes/settings/agents-routes.ts` -- change signature to accept `IAgentConfigStore`.

---

### Task 6: Integration with CLI normalizer (1 hour)

**File to modify**: `packages/cli/src/config.ts`

Ensure `normalizeAgentsConfig()` continues to work for CLI mode. No breaking changes -- CLI reads from file config, API reads from Postgres store.

---

### Task 7: Tests (3 hours)

**File to create**: `packages/api/src/services/agent-config-store.test.ts`

| # | Test | Assertion |
|---|------|-----------|
| 1 | `get(null)` returns system default | Source = 'system' |
| 2 | `get(accountId)` returns account override | Source = 'account' |
| 3 | `get(accountId)` falls back to system default | When no override exists |
| 4 | `get(null)` falls back to hardcoded | When no rows exist |
| 5 | `upsert(null, ...)` creates system default | Version = 1 |
| 6 | `upsert(null, ...)` increments version | Version = 2 |
| 7 | `upsert(accountId, ...)` creates override | Source = 'account' |
| 8 | `validate()` rejects empty providerChain | `errors.length > 0` |
| 9 | `validate()` rejects `__proto__` provider | Forbidden name detected |
| 10 | `validate()` rejects maxBudgetUsd > 100 | Out of bounds |
| 11 | `validate()` rejects invalid regex pattern | Syntax error |
| 12 | `validate()` accepts valid config | `valid === true` |

**File to create**: `packages/api/src/routes/settings/__tests__/agents-routes.test.ts`

| # | Test | Assertion |
|---|------|-----------|
| 13 | GET /agents/config returns 200 | Correct shape |
| 14 | PUT /agents/config with valid body returns 200 | Updated config |
| 15 | PUT /agents/config with invalid body returns 400 | Error message |
| 16 | POST /agents/config/validate with valid returns valid=true | No errors |
| 17 | POST /agents/config/validate with invalid returns valid=false | Errors listed |

**Total tests**: ~17

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `database/migrations/012_agent_configs.sql` | DDL + system default seed |
| 2 | `packages/api/src/services/agent-config-store.ts` | Interface + types |
| 3 | `packages/api/src/services/pg-agent-config-store.ts` | Postgres implementation |
| 4 | `packages/api/src/services/agent-config-store.test.ts` | Service tests |
| 5 | `packages/api/src/routes/settings/__tests__/agents-routes.test.ts` | Route tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/api/src/routes/settings/agents-routes.ts` | Replace placeholder with full CRUD |
| 2 | `packages/api/src/routes/settings/index.ts` | Wire PgAgentConfigStore into SettingsServices |
| 3 | `packages/cli/src/config.ts` | Verify normalizeAgentsConfig still works (may be no-op) |

---

## Dependencies

- **Epic 17** (tenants table for account_id FK -- deferred via nullable column)
- **Epic 18** (JWT auth for extracting accountId from request)
- **Migration 007** must be applied first (next migration is 008)

## Migration from Existing Code

The existing `ConfigService` in `packages/api/src/services/settings/ConfigService.ts` stores agents config in memory. The migration path:

1. `PgAgentConfigStore` replaces the agents/security portions of `ConfigService`.
2. `ConfigService` is retained for prompt template and user provider operations.
3. Routes are updated to accept `IAgentConfigStore` instead of `ConfigService` for agent CRUD.
4. `normalizeAgentsConfig()` in `packages/shared/src/config/normalize-agents.ts` is unchanged -- CLI continues to use it.

---

## Estimated Effort

| Task | Hours |
|------|-------|
| Migration SQL (DDL + seed) | 2 |
| IAgentConfigStore interface + types | 2 |
| PgAgentConfigStore implementation | 3 |
| Fastify routes (GET/PUT/POST validate) | 4 |
| Settings index wiring | 2 |
| CLI normalizer integration | 1 |
| Tests (17 tests) | 3 |
| **Total** | **17 hours** |
