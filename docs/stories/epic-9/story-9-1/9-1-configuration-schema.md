# Story 9-1: Config Schema + API

## User Story

As a platform operator, I want multi-agent configuration stored in Postgres (per account) with API endpoints for CRUD, so that both the TypeScript engine and Elsa workflows resolve agent config from one place instead of hardcoded values.

## Goal

Replace the file-based `TammaConfig.agents` / `TammaConfig.security` fields with a Postgres-backed configuration store scoped to accounts. Expose Fastify API endpoints for reading, writing, and validating agent config. Keep backward compatibility with legacy `agent: AgentConfig` for self-hosted/CLI mode via `normalizeAgentsConfig()`.

## Acceptance Criteria

1. `AgentsConfig`, `SecurityConfig`, `ProviderChainEntry`, `AgentRoleConfig`, `WorkflowPhase`, and `PermissionMode` types are defined in `packages/shared/src/types/agent-config.ts` (already partially exists).
2. A new Postgres table `agent_configs` stores per-account agent configuration as JSONB.
3. System defaults (NULL `account_id`) are seeded via migration.
4. API endpoints:
   - `GET /api/v1/agents/config` -- returns the resolved config for the authenticated account (account override with system default fallback).
   - `PUT /api/v1/agents/config` -- upserts the account-level config. Validates before saving.
   - `POST /api/v1/agents/config/validate` -- validates a config payload without saving.
5. `normalizeAgentsConfig()` still works for CLI/self-hosted mode (reads from file config, not API).
6. Config validation rules enforced at both API and CLI load time:
   - `providerChain` non-empty in defaults
   - `provider` matches `/^[a-z0-9][a-z0-9_-]{0,63}$/`, rejects `__proto__`/`constructor`/`prototype`
   - `maxBudgetUsd` in [0, 100], finite number
   - `blockedCommandPatterns` compile as valid regex, max 100 patterns, max 500 chars each
   - `maxFetchSizeBytes` in [0, 1 GiB]
   - `bypassPermissions` emits WARN and requires `TAMMA_ALLOW_BYPASS_PERMISSIONS=true` env var
7. Legacy `agent: AgentConfig` still works via `normalizeAgentsConfig()` mapping.

## Technical Context

### Existing Files

- `packages/shared/src/types/agent-config.ts` -- type definitions (already exists from prior work)
- `packages/shared/src/config/normalize-agents.ts` -- normalizer (already exists)
- `packages/cli/src/config.ts` -- CLI config loading with `mergeConfig()`
- `packages/api/src/routes/settings/agents-routes.ts` -- placeholder agent settings routes
- `packages/api/src/routes/settings/index.ts` -- settings route registration

### Database Schema

```sql
CREATE TABLE agent_configs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  account_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
  config JSONB NOT NULL,
  version INTEGER NOT NULL DEFAULT 1,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  created_by UUID NULL,
  updated_by UUID NULL,
  UNIQUE (account_id)
);

-- System defaults: account_id IS NULL
-- Account overrides: account_id = tenant UUID
```

### API Route Design

```
GET  /api/v1/agents/config
  → accountId from JWT
  → SELECT config FROM agent_configs WHERE account_id = :accountId
  → If not found, SELECT config FROM agent_configs WHERE account_id IS NULL
  → Returns: { config: AgentsConfig, security: SecurityConfig, source: 'account' | 'system' }

PUT  /api/v1/agents/config
  → accountId from JWT
  → Body: { config: AgentsConfig, security?: SecurityConfig }
  → Validates, then INSERT ... ON CONFLICT (account_id) DO UPDATE
  → Returns: { config, version }

POST /api/v1/agents/config/validate
  → Body: { config: AgentsConfig, security?: SecurityConfig }
  → Returns: { valid: boolean, errors: string[] }
```

### Resolution Order (API)

1. Account-specific row (`account_id = :accountId`)
2. System default row (`account_id IS NULL`)
3. Hardcoded defaults (built into the service)

### Elsa Integration Path

Elsa's `ResolveAgentConfigActivity.cs` currently reads from the ELSA Agents DB. After this story, it calls `GET /api/v1/agents/config` instead, passing the account context. This change is wired in Story 9-11.

## Files

- MODIFY `packages/shared/src/types/agent-config.ts` -- ensure all types exported
- MODIFY `packages/shared/src/config/normalize-agents.ts` -- no changes needed
- CREATE `packages/api/src/services/agent-config-store.ts` -- Postgres-backed config store
- CREATE `packages/api/src/services/agent-config-store.test.ts`
- MODIFY `packages/api/src/routes/settings/agents-routes.ts` -- implement CRUD endpoints
- CREATE `database/migrations/012_agent_configs.sql` (migration 012 -- see `/docs/stories/migration-ordering.md`)
- MODIFY `packages/cli/src/config.ts` -- ensure `mergeConfig()` propagates `agents`/`security`

## Dependencies

- **Epic 17** (tenants table must exist for `account_id` FK)
- **Epic 18** (JWT auth for API endpoints provides `accountId`)

## Effort Estimate

**16 hours**

- 4h: Database migration + seed data
- 4h: Config store service (Postgres read/write with resolution logic)
- 4h: API routes (GET/PUT/POST validate) with Fastify schema validation
- 2h: Integration with existing CLI normalizer
- 2h: Tests (service + route + validation)
