# Story 9-12: Cross-Epic Integration Test

Status: planned

## Story

As a **platform engineer**,
I want an end-to-end integration test that validates the full chain from Elsa workflow dispatch through tenant-scoped prompt resolution and agent execution to diagnostics recording,
so that I can verify all cross-epic integrations work together correctly in a multi-tenant environment.

## Context

Epics 9 (Agent Management), 17 (Multi-Tenancy), and 27 (Prompt Store) each introduce components that must interoperate:

- **Epic 17** provides tenant context (resolved from GitHub App installation)
- **Epic 27** provides tenant-scoped prompt resolution
- **Epic 9** provides agent configuration, health tracking, provider chain resolution, diagnostics, and sanitization

No single story tests the full chain end-to-end across these three epics. This story fills that gap.

## Full Integration Chain

```
Elsa dispatches SingleIssueCycleWorkflow
  |
  | (carries tenantId from installation context — Epic 17)
  v
API receives workflow request
  |
  | (tenant context middleware resolves tenantId — Story 17-5)
  | (SET app.current_tenant_id on PG connection — Story 17-2)
  v
API resolves agent config for role
  |
  | (GET /api/v1/agents/:role/resolve — Story 9-8)
  | (reads agent_configs for tenant — Story 9-1)
  v
API resolves prompt for (tenantId, role, action)
  |
  | (POST /api/prompts/:role/:action/render — Story 27-3)
  | (IPromptStore.get(tenantId, role, action) — Story 27-2)
  | (falls back to system default if no tenant override)
  v
API checks provider health
  |
  | (GET /api/v1/health/providers/:key — Story 9-3)
  | (circuit breaker state per-tenant)
  v
API resolves provider chain
  |
  | (POST /api/v1/providers/chain/resolve — Story 9-5)
  | (respects health status and tenant config)
  v
API executes provider (LLM call)
  |
  | (POST /api/v1/providers/create — Story 9-4)
  | (content sanitized — Story 9-7)
  v
API records diagnostics
  |
  | (POST /api/v1/diagnostics — Story 9-2)
  | (cost, tokens, latency — tenant-scoped)
  v
All data is tenant-scoped
  |
  | (RLS ensures no cross-tenant leakage — Story 17-2)
  | (prompt tables exempt from RLS — Story 17-2 exemption list)
  v
Diagnostics queryable per-tenant
  |
  | (GET /api/v1/diagnostics?tenantId=... — Story 9-2)
```

## Acceptance Criteria

1. **Tenant isolation**: Two tenants (A and B) each have their own agent config, prompt overrides, and diagnostics. Tenant A's workflow never reads tenant B's data.
2. **Prompt resolution chain**: Tenant A has a custom prompt override for `developer/implement`. The integration test verifies that Tenant A's workflow uses the override, while Tenant B's workflow uses the system default.
3. **Agent config resolution**: Tenant A has a custom agent config (e.g., preferred provider = `openai`). Tenant B uses the default config (provider = `anthropic`). The resolver returns the correct config for each tenant.
4. **Health tracking isolation**: If Tenant A's provider is in a circuit-breaker open state, Tenant B's same provider remains healthy (circuit breaker state is per-tenant).
5. **Diagnostics isolation**: After both tenants execute a workflow, querying diagnostics for Tenant A returns only Tenant A's records.
6. **Prompt fallback**: Tenant B has no prompt overrides. The prompt store returns system defaults for all role+action combinations. System default rows (tenant_id IS NULL) are readable by any tenant.
7. **RLS enforcement**: Connecting as `tamma_app` with `SET app.current_tenant_id = tenant_A_id`, querying `agent_configs` returns only Tenant A's rows. Querying `prompts` returns Tenant A's overrides AND system defaults (because prompt tables are exempt from RLS).
8. **End-to-end timing**: The full chain (resolve tenant -> resolve prompt -> resolve agent -> check health -> execute -> record diagnostics) completes in under 2 seconds for a mocked LLM provider.
9. **Elsa workflow context**: When `SingleIssueCycleWorkflow` dispatches `LlmCallWorkflow` with a `tenantId`, the `ResolvePromptFromRegistryActivity` sends the `X-Tenant-Id` header, and the API resolves the correct tenant's prompt.
10. **Sanitization per-tenant**: Tenant A has a custom sanitization rule (e.g., redact API keys). The sanitization service applies Tenant A's rules, not Tenant B's.

## Test Plan

### Setup

1. Create two test tenants in the `tenants` table: `tenant_test_A` and `tenant_test_B`
2. Create test users linked to each tenant via `tenant_memberships`
3. Seed data for Tenant A:
   - Custom agent config (preferred provider: `openai`, fallback: `anthropic`)
   - Custom prompt override for `developer/implement` with a distinctive template text
   - Custom sanitization rule (redact `sk-*` patterns)
4. Seed data for Tenant B:
   - No custom agent config (uses defaults)
   - No prompt overrides (uses system defaults)
   - No custom sanitization rules (uses defaults)
5. Seed system default prompts (migration 011 seed data)
6. Create a mock LLM provider that returns a predictable response

### Test Cases

#### Test 1: Tenant A — Full Chain with Custom Config

```
Given: Tenant A has custom agent config and prompt override
When: API receives a resolve request with tenantId = tenant_A_id
  AND role = 'developer', action = 'implement'
Then:
  - Agent config resolves to Tenant A's custom config (provider = openai)
  - Prompt resolves to Tenant A's custom override (not system default)
  - Provider chain uses openai as primary (from Tenant A's config)
  - Diagnostics record is created with tenant_id = tenant_A_id
  - Sanitization uses Tenant A's custom rules
```

#### Test 2: Tenant B — Full Chain with Defaults

```
Given: Tenant B has no custom config or prompt overrides
When: API receives a resolve request with tenantId = tenant_B_id
  AND role = 'developer', action = 'implement'
Then:
  - Agent config resolves to system defaults
  - Prompt resolves to system default (tenant_id IS NULL row)
  - Provider chain uses default provider
  - Diagnostics record is created with tenant_id = tenant_B_id
  - Sanitization uses default rules
```

#### Test 3: Cross-Tenant Isolation

```
Given: Both tenants have executed workflows
When: Querying diagnostics for Tenant A
Then: Only Tenant A's diagnostics are returned (not Tenant B's)

When: Querying agent config for Tenant B
Then: Only Tenant B's config (or defaults) are returned

When: Connecting as tamma_app with SET app.current_tenant_id = tenant_A_id
  AND querying SELECT * FROM agent_configs
Then: Only Tenant A's rows are returned (RLS enforcement)
```

#### Test 4: Prompt Store Exemption from RLS

```
Given: RLS is active, current tenant is Tenant A
When: IPromptStore.get(tenant_A_id, 'developer', 'implement')
Then: Returns Tenant A's override (tenant_id = tenant_A_id)

When: IPromptStore.get(tenant_A_id, 'developer', 'context-scan')
  AND Tenant A has no override for context-scan
Then: Returns system default (tenant_id IS NULL)
  -- This proves prompt tables correctly allow reading NULL rows
```

#### Test 5: Elsa Workflow → API → Prompt Resolution

```
Given: LlmCallWorkflow is triggered with tenantId = tenant_A_id
When: ResolvePromptFromRegistryActivity calls POST /api/prompts/developer/implement/render
  WITH header X-Tenant-Id: tenant_A_id
Then: The render endpoint resolves Tenant A's custom prompt
  AND the rendered template contains Tenant A's distinctive text
```

#### Test 6: Circuit Breaker Isolation

```
Given: Provider 'openai' is in OPEN state for Tenant A
  BUT provider 'openai' is CLOSED (healthy) for Tenant B
When: Tenant A checks health for openai
Then: Returns OPEN (unhealthy)

When: Tenant B checks health for openai
Then: Returns CLOSED (healthy)
```

#### Test 7: Diagnostics Budget Check

```
Given: Tenant A has used $90 of a $100 budget
  AND Tenant B has used $10 of a $100 budget
When: GET /api/v1/diagnostics/budget/tenant_A_id
Then: Returns { used: 90, limit: 100, remaining: 10, warning: true }

When: GET /api/v1/diagnostics/budget/tenant_B_id
Then: Returns { used: 10, limit: 100, remaining: 90, warning: false }
```

### Teardown

1. Delete test tenant rows (CASCADE deletes all tenant-scoped data)
2. Verify no orphaned data remains in `agent_configs`, `prompts`, `provider_diagnostics`, `provider_health`, `sanitization_rules`

## Technical Context

### Files to Create

| File | Purpose |
|------|---------|
| `packages/api/src/__tests__/cross-epic-integration.test.ts` | Main integration test file |
| `packages/api/src/__tests__/helpers/test-tenant-setup.ts` | Helper to create test tenants, seed data, and teardown |

### Test Infrastructure

- Requires a test PostgreSQL database (`DATABASE_URL_TEST`)
- Requires the Tamma API server running (or started in-process via Fastify injection)
- Uses `InMemory` LLM provider mock (not real API calls)
- Tests run against the full migration set (001-017)

### Mock LLM Provider

The integration test registers a mock provider that:
- Accepts any prompt and returns a predictable response
- Records the request for assertion (prompt text, headers, tenant context)
- Does not make real API calls

## Dependencies

- **Story 9-11** (Diagnostics Queue + Elsa Integration): The Elsa-to-API integration must be wired
- **Story 27-6** (Elsa Workflow Integration): The `X-Tenant-Id` header propagation must work
- **Story 17-5** (API Tenant Context Middleware): Tenant resolution from JWT/API key must be implemented
- **All migrations 008-017**: Database schema must be complete

## Estimated Effort

| Task | Hours |
|------|-------|
| Test tenant setup/teardown helper | 3 |
| Test cases 1-2 (full chain per tenant) | 4 |
| Test case 3 (cross-tenant isolation) | 2 |
| Test case 4 (prompt RLS exemption) | 1 |
| Test case 5 (Elsa workflow integration) | 3 |
| Test cases 6-7 (circuit breaker + budget) | 2 |
| Mock LLM provider setup | 1 |
| CI pipeline integration | 1 |
| **Total** | **17 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-09 | 1.0 | Initial story creation from cross-epic review | Cross-epic review |
