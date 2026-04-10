# Story 16.7: Service-to-Service Authentication

Status: ready-for-dev

## Story

As a **platform operator**,
I want a unified API key system that can authenticate internal services (Elsa workflows, tamma-api-dotnet) alongside users and GitHub App installations,
so that service-to-service calls inside the Tamma platform are authenticated, auditable, and manageable without reusing user or installation credentials.

## Background

Tamma currently has two independent API key systems:

1. **`github_installations.api_key_hash`** — per-GitHub-App-installation keys. Used by the `tamma` CLI running inside GitHub Actions runners to call back to the SaaS API (`packages/cli/src/worker/result-callback.ts`). Provisioned on installation via `packages/api/src/routes/github/github-callback.ts`.
2. **`user_api_keys`** — per-user keys. Used by human users to call the API from their own scripts. Managed via Story 16.2's user management API.

There is **no dedicated path for internal service-to-service authentication**. Concretely, Elsa workflows (C#, running in `apps/tamma-elsa`) need to call the TypeScript `tamma-api` for:

- Prompt store reads (Epic 27)
- Agent config lookups
- Diagnostic event writes (Epic 9)
- Health tracker updates

The `tamma-api-dotnet` service may also need to call `tamma-api` for cross-service coordination. Today, operators would either have to (a) reuse an installation key (wrong scope, wrong audit trail) or (b) mint a fake "service user" and use its user key (incorrect audit attribution, no distinct permission model).

This story introduces a proper service account model and unifies all three key types (user, installation, service) behind a single `api_keys` table and a single auth middleware code path.

## Acceptance Criteria

1. A new `api_keys` table exists with a `scope` column constrained to `('user', 'installation', 'service')` — see schema below
2. A migration copies existing data from `user_api_keys` and `github_installations.api_key_hash` into the new unified `api_keys` table, preserving hashes, prefixes, created/revoked timestamps, and labels
3. A unified auth middleware (`authenticateApiKey`) validates any bearer token by a single lookup against `api_keys` and populates `request.authPrincipal` with a tagged union (`{ scope: 'user', userId, role }` | `{ scope: 'installation', installationId }` | `{ scope: 'service', serviceName, permissions, tenantId }`)
4. Service-scope keys support a `permissions` JSONB column with scope strings such as `prompts:read`, `prompts:write`, `diagnostics:write`, `agent_config:read`, `health:write` — requested via `X-Required-Scope` middleware annotation on protected routes
5. Service-scope keys are not tenant-scoped at creation time; callers must supply an `X-Tenant-Id` header on every tenant-scoped request, and the middleware validates the tenant exists and populates `request.authPrincipal.tenantId`
6. User-scope and installation-scope keys continue to derive tenant context from their owner (user's tenant or installation's tenant) — `X-Tenant-Id` is ignored for these scopes to prevent privilege escalation
7. Admin REST endpoints exist to manage service keys (platform-owner role only, enforced via Story 16.5 RBAC):
   - `POST /api/admin/service-keys` — create a new service key, returns raw key exactly once
   - `GET /api/admin/service-keys` — list all service keys (without raw keys)
   - `POST /api/admin/service-keys/:id/rotate` — generate a new key for the same service account, returns raw key exactly once; old key remains valid for a grace period
   - `DELETE /api/admin/service-keys/:id` — revoke immediately
8. Environment-variable injection pattern: Elsa reads `TAMMA_SERVICE_API_KEY` from `IConfiguration` at startup and passes it as `Authorization: Bearer <key>` on every outbound HTTP call to `tamma-api`; docker-compose provisions the variable from a host-level `.env` entry generated during `tamma` platform setup
9. Audit logging: every authenticated request (all three scopes) emits a structured Pino log at INFO level with `keyId`, `scope`, `ownerId`, `tenantId` (if any), `method`, `path`, and `statusCode`; failed auth attempts log at WARN with `reason` (invalid hash, revoked, expired, missing tenant header, insufficient scope)
10. Key rotation grace period: when a service key is rotated, the old key's `revoked_at` is set to `NOW() + 24h` instead of `NOW()`, allowing in-flight deployments to continue working; the middleware treats a key with `revoked_at > NOW()` as still valid but logs a WARN `rotating-key-still-in-use` on each use
11. Unit tests cover: auth middleware for all three scopes, permission-scope matching, tenant header validation, rotation grace period, and audit log emission
12. Integration tests cover: full round-trip from Elsa HTTP client → tamma-api protected endpoint, using a real service key stored in the test database

## Technical Context

### Current State

| File | Purpose |
|------|---------|
| `packages/api/src/persistence/user-api-key-store.ts` | `IUserApiKeyStore`, `InMemoryUserApiKeyStore`, `PgUserApiKeyStore` — per-user keys |
| `packages/api/src/persistence/pg-installation-store.ts` | `PgInstallationStore.updateApiKeyHash`, `findByApiKeyHash` — per-installation keys on `github_installations` |
| `packages/api/src/auth/api-key.ts` | `generateApiKey`, `hashApiKey`, `getApiKeyPrefix` — shared key primitives |
| `packages/api/src/auth/api-key-auth.ts` | Existing bearer token auth middleware (currently handles installation keys only) |
| `packages/api/src/routes/github/github-callback.ts` | Generates installation key at GitHub App install time |
| `packages/cli/src/worker/result-callback.ts` | CLI reads `TAMMA_API_KEY` env var and sends `Authorization: Bearer` header |

### Target State

A single `api_keys` table replaces both existing paths. A single `findByKeyHash(hash)` lookup returns a typed `ApiKeyRecord` with enough context to build the auth principal. The existing user-key and installation-key call sites are refactored to write to the new table but keep their public interfaces.

### Proposed Schema

```sql
-- database/migrations/0XX_unified_api_keys.sql

CREATE TABLE IF NOT EXISTS api_keys (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  scope         TEXT NOT NULL CHECK (scope IN ('user', 'installation', 'service')),
  owner_id      TEXT NOT NULL,               -- user_id UUID | installation_id bigint | service_name text
  key_hash      TEXT NOT NULL UNIQUE,
  key_prefix    TEXT NOT NULL,
  label         TEXT NOT NULL DEFAULT 'default',
  permissions   JSONB NOT NULL DEFAULT '[]'::jsonb,  -- ['prompts:read','diagnostics:write',...] (service scope only)
  tenant_id     UUID,                         -- NULL for service keys; set for user/installation
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  last_used_at  TIMESTAMPTZ,
  revoked_at    TIMESTAMPTZ,                  -- may be in the future during rotation grace period
  rotated_from  UUID REFERENCES api_keys(id)  -- track rotation chain
);

CREATE INDEX idx_api_keys_key_hash ON api_keys (key_hash);
CREATE INDEX idx_api_keys_scope_owner ON api_keys (scope, owner_id);
CREATE INDEX idx_api_keys_active ON api_keys (scope) WHERE revoked_at IS NULL OR revoked_at > NOW();
```

### Auth Principal Type

```typescript
// packages/api/src/auth/principal.ts
export type AuthPrincipal =
  | { scope: 'user'; keyId: string; userId: string; role: Role; tenantId: string }
  | { scope: 'installation'; keyId: string; installationId: number; tenantId: string }
  | {
      scope: 'service';
      keyId: string;
      serviceName: string;
      permissions: string[];
      tenantId: string | null;  // null until X-Tenant-Id header is parsed
    };
```

### Files to Create

| File | Purpose |
|------|---------|
| `database/migrations/0XX_unified_api_keys.sql` | New `api_keys` table + data migration from existing tables |
| `packages/api/src/persistence/api-key-store.ts` | `IApiKeyStore`, `ApiKeyRecord`, `InMemoryApiKeyStore`, `PgApiKeyStore` |
| `packages/api/src/auth/principal.ts` | `AuthPrincipal` tagged union type |
| `packages/api/src/auth/unified-auth.ts` | `authenticateApiKey` Fastify middleware |
| `packages/api/src/auth/require-scope.ts` | `requireScope('prompts:read')` preHandler for service endpoints |
| `packages/api/src/routes/admin/service-keys.ts` | Admin CRUD endpoints for service keys |
| `packages/api/src/__tests__/unified-auth.test.ts` | Middleware unit tests covering all three scopes |
| `packages/api/src/__tests__/service-key-integration.test.ts` | End-to-end test with rotation and grace period |

### Files to Modify

| File | Change |
|------|--------|
| `packages/api/src/persistence/user-api-key-store.ts` | Refactor `PgUserApiKeyStore` to write to `api_keys` with `scope='user'`; preserve existing `IUserApiKeyStore` interface |
| `packages/api/src/persistence/pg-installation-store.ts` | Refactor `updateApiKeyHash` and `findByApiKeyHash` to use `api_keys` with `scope='installation'` |
| `packages/api/src/auth/api-key-auth.ts` | Replace with thin wrapper that delegates to `unified-auth.ts` for backward compatibility |
| `packages/api/src/routes/github/github-callback.ts` | Use new `IApiKeyStore.createApiKey({ scope: 'installation', ... })` instead of `PgInstallationStore.updateApiKeyHash` |
| `apps/tamma-elsa/src/Tamma.Activities/Http/TammaApiClient.cs` (or equivalent) | Read `TAMMA_SERVICE_API_KEY` from `IConfiguration`, attach `Authorization` header, optionally attach `X-Tenant-Id` from workflow context |
| `docker/docker-compose.yml` | Pass `TAMMA_SERVICE_API_KEY` env var to the elsa service |
| `docker/.env.example` | Document `TAMMA_SERVICE_API_KEY` (generated by platform admin via CLI) |

## Implementation Plan

### Step 1: Create the Unified `api_keys` Table and Data Migration

Write `0XX_unified_api_keys.sql` that:
1. Creates the new `api_keys` table
2. Copies existing rows: `INSERT INTO api_keys (scope, owner_id, key_hash, key_prefix, label, tenant_id, created_at, revoked_at) SELECT 'user', user_id::text, key_hash, key_prefix, label, ... FROM user_api_keys`
3. Copies installation keys from `github_installations` (joining `user_installations` to derive `tenant_id`)
4. Leaves the old tables in place for one release cycle as a rollback safety net; a follow-up migration drops them

### Step 2: Implement `IApiKeyStore` and `PgApiKeyStore`

Single lookup by hash returns the full record including scope, owner, permissions, tenant, and grace-period status. Includes `createApiKey`, `rotateApiKey` (creates new key with `rotated_from` pointing to old, sets old `revoked_at = NOW() + 24h`), `revokeApiKey`, `listByScope(scope)`, `updateLastUsed(id)`.

### Step 3: Unified Auth Middleware

`authenticateApiKey` reads the `Authorization: Bearer <key>` header, hashes the value, calls `apiKeyStore.findByKeyHash`, and builds the appropriate `AuthPrincipal` variant. For service scope, it reads `X-Tenant-Id`, validates it against the `tenants` table (Epic 17), and rejects with 400 if the route requires tenant context but the header is missing.

### Step 4: Admin Service-Key CRUD Endpoints

Under `/api/admin/service-keys`, gated by `requirePermission('system_config', 'manage')` from Story 16.5. Create/list/rotate/revoke. Create and rotate return the raw key once in the response body with a clear warning that it cannot be retrieved again.

### Step 5: Wire Elsa

Add a `TammaApiClient` (or extend existing HTTP client) that:
- Injects `Authorization: Bearer {TAMMA_SERVICE_API_KEY}` on every request
- Attaches `X-Tenant-Id` from the current `WorkflowExecutionContext` when the workflow is tenant-scoped
- Logs a structured event for each outbound call

### Step 6: Audit Logging and Tests

Add a Fastify `onResponse` hook that logs authenticated request metadata. Write unit tests for the middleware and integration tests that create a real service key in the test DB, rotate it, and verify both old and new keys work during the grace period.

## Logging Requirements

| Event | Level | Output | Notes |
|-------|-------|--------|-------|
| Service key created | INFO | Pino structured log + audit event | Include key ID, service name, permissions, created by |
| Service key rotated | INFO | Pino structured log + audit event | Include old key ID, new key ID, service name, grace period end |
| Service key revoked | INFO | Pino structured log + audit event | Include key ID, reason, revoked by |
| Authenticated request | INFO | Pino structured log | Include key ID, scope, owner ID, tenant ID, method, path, status |
| Auth failure: invalid key | WARN | Pino structured log | Include key prefix (not full key), source IP, reason |
| Auth failure: missing tenant header | WARN | Pino structured log | Include key ID, path |
| Auth failure: insufficient scope | WARN | Pino structured log | Include key ID, required scope, present scopes |
| Rotating key still in use | WARN | Pino structured log | Include key ID, grace period end |

### Sensitive Data Redaction

- Never log the full bearer token — only the key prefix
- Never log the raw key hash — log the key ID (UUID) instead
- Tenant IDs are safe to log; user emails are not needed in service-call logs

### Audit Events

```
SERVICE_KEY.CREATED.SUCCESS
SERVICE_KEY.ROTATED.SUCCESS
SERVICE_KEY.REVOKED.SUCCESS
AUTH.SERVICE_CALL.SUCCESS
AUTH.SERVICE_CALL.FAILED
```

## Testing Strategy

### Unit Tests

1. `authenticateApiKey` resolves user scope and builds `{ scope: 'user', role, tenantId }` principal
2. `authenticateApiKey` resolves installation scope and builds `{ scope: 'installation', installationId, tenantId }` principal
3. `authenticateApiKey` resolves service scope and requires `X-Tenant-Id` header when route is tenant-scoped
4. `authenticateApiKey` rejects service key when `X-Tenant-Id` points to a non-existent tenant
5. `requireScope('prompts:read')` passes for service key with `prompts:read`, fails for service key without it
6. `requireScope` always passes for user scope (role-based elsewhere) — or fails with explicit "scope only applies to service keys"
7. Rotation: old key still validates during grace period, logs WARN
8. Rotation: after grace period, old key rejected
9. Revoked key (immediate) rejected
10. Migration preserves all existing user and installation keys

### Integration Tests

1. Elsa HTTP client → tamma-api protected endpoint, valid service key, returns 200
2. Elsa HTTP client → tamma-api protected endpoint, missing `X-Tenant-Id`, returns 400
3. Elsa HTTP client → tamma-api protected endpoint, service key lacks scope, returns 403
4. Admin creates service key → raw key returned once → reused in subsequent request → succeeds
5. Admin rotates service key → both old and new keys work for 24h → old key fails after grace period expires (tested with manipulated `revoked_at` in test DB)

### Manual Verification

1. Generate a service key via admin API on staging
2. Set `TAMMA_SERVICE_API_KEY` in staging docker-compose, restart elsa
3. Trigger an Elsa workflow that calls the prompt store; verify audit log entry in Pino logs
4. Rotate the key; verify old deployments continue working for the grace period

## Dependencies

- **Story 16.1** (oauth2-proxy) — required for the broader auth model this fits into
- **Story 16.2** (user management API) — defines `user_api_keys` table that gets migrated
- **Story 16.5** (RBAC enforcement) — `requirePermission` middleware used to gate admin service-key endpoints
- **Story 17.1** (tenants table) — required for `X-Tenant-Id` header validation

This story blocks:
- **Story 9.11** (Elsa API integration for diagnostic events)
- **Story 27.6** (Elsa prompt store integration)
- Any future internal service that needs to call `tamma-api`

## Estimated Effort

| Task | Hours |
|------|-------|
| Unified `api_keys` migration + data copy | 3 |
| `IApiKeyStore` + `PgApiKeyStore` implementation | 3 |
| Refactor existing user/installation stores to use `api_keys` | 3 |
| Unified auth middleware + principal type | 3 |
| Admin service-key CRUD routes | 2 |
| Elsa HTTP client integration (TAMMA_SERVICE_API_KEY) | 2 |
| Audit logging hook | 1 |
| Unit tests | 2 |
| Integration tests (Elsa round-trip, rotation grace period) | 1 |
| **Total** | **20 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-09 | 1.0 | Initial story creation | Architecture Team |
