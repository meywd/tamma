# Story 17.5: API Tenant Context Middleware

Status: ready-for-dev

## Story

As a **platform engineer**,
I want tenant context extracted from every authenticated request (JWT, API key, or oauth2-proxy headers) and propagated to all stores and the PostgreSQL session variable,
so that every downstream query is automatically scoped to the correct tenant without each route handler having to manually filter.

## Acceptance Criteria

1. A Fastify plugin `registerTenantContextPlugin` exists that runs as an `onRequest` hook after authentication
2. The plugin resolves the current tenant from one of three sources (in priority order):
   a. **JWT claims**: `tenantId` field in the decoded JWT payload
   b. **API key**: `InstallationContext.installationId` => lookup `tenants.external_id` => `tenant_id`
   c. **oauth2-proxy headers**: `X-Auth-Request-User` => lookup user => user's `tenant_id`
3. The resolved `tenantId` is decorated on the Fastify request as `request.tenantId`
4. Before any database query, the middleware calls `SET app.current_tenant_id = '<tenantId>'` on the PostgreSQL connection, activating RLS policies (Story 17.2)
5. CLI/self-hosted mode (auth disabled) uses `DEFAULT_TENANT_ID` as the implicit tenant
6. If tenant resolution fails (unknown installation, user not linked to a tenant), the request is rejected with 403
7. The `tenantId` is included in JWT claims when tokens are issued (login, OAuth callback, API key exchange)
8. All existing store methods (`IUserStore`, `IGitHubInstallationStore`, `IWorkflowStore`, `IEventStore`, etc.) receive the `tenantId` from the request context
9. Structured logs include `tenantId` in every log line via Pino child logger
10. The task queue (`ITaskQueue`) uses `tenantId` (mapped from `installationId`) for enqueue and dequeue operations
11. Tenant context is propagated to ELSA workflow dispatches via the workflow input variables
12. Health check endpoints (`/api/health`) do not require tenant context
13. Superadmin/platform operations (future) can set tenant context explicitly via a header for cross-tenant management

## Technical Context

### Current Authentication Flow

The API has two auth paths:

1. **JWT auth** (`packages/api/src/auth/index.ts`): Decodes JWT, sets `request.authUser` with `{ id, username, role }`
2. **API key auth** (`packages/api/src/auth/api-key-auth.ts`): Hashes the `tamma_sk_*` key, looks up installation, sets `request.installationContext` with `{ installationId, accountLogin, permissions }`

Neither path resolves or sets a tenant context.

### Tenant Resolution Chain

```
Request arrives
    |
    v
Auth Plugin (existing) -- sets request.authUser or request.installationContext
    |
    v
Tenant Context Plugin (NEW) -- resolves tenantId
    |
    +-- Source 1: JWT has tenantId claim? Use it directly.
    |
    +-- Source 2: request.installationContext exists?
    |     => tenantStore.getTenantByExternalId(String(installationId))
    |     => use tenant.id
    |
    +-- Source 3: request.authUser exists?
    |     => userStore.getUser(authUser.id)
    |     => use user.tenantId
    |
    +-- Source 4: Auth disabled (dev/CLI mode)?
    |     => DEFAULT_TENANT_ID
    |
    +-- None resolved? => 403 Forbidden
    |
    v
SET app.current_tenant_id = tenantId (on PG connection)
    |
    v
Route handler executes (all queries scoped by RLS)
```

### PostgreSQL Session Variable Lifecycle

For connection pools (`pg.Pool`), the session variable must be set on each request, not per-connection. Two approaches:

**Approach A: Per-request SET (recommended)**

```typescript
// In the onRequest hook, after resolving tenantId:
const client = await pool.connect();
await client.query('SET app.current_tenant_id = $1', [tenantId]);
// Attach client to request for use by route handler
request.pgClient = client;

// In onResponse hook:
await client.query('RESET app.current_tenant_id');
client.release();
```

**Approach B: Transaction-scoped SET**

```typescript
await client.query('BEGIN');
await client.query('SET LOCAL app.current_tenant_id = $1', [tenantId]);
// ... queries ...
await client.query('COMMIT');
// SET LOCAL only lasts for the transaction
```

Approach B is safer (auto-resets on COMMIT/ROLLBACK) but requires all queries to run inside a transaction. Approach A is simpler but requires explicit cleanup.

**Recommended: Approach B with `SET LOCAL`** inside an explicit transaction. This prevents tenant context from leaking to the next request on a pooled connection.

### Request Decoration

```typescript
// Augment Fastify request type
declare module 'fastify' {
  interface FastifyRequest {
    tenantId: string;
    pgClient?: pg.PoolClient;
  }
}
```

### Files to Create

| File | Purpose |
|------|---------|
| `packages/api/src/middleware/tenant-context.ts` | Tenant context Fastify plugin |
| `packages/api/src/middleware/__tests__/tenant-context.test.ts` | Unit tests |
| `packages/api/src/persistence/tenant-aware-pool.ts` | Helper to wrap `pg.Pool` with automatic tenant context |

### Files to Modify

| File | Change |
|------|--------|
| `packages/api/src/auth/index.ts` | Include `tenantId` in JWT claims |
| `packages/api/src/auth/api-key-auth.ts` | Resolve tenant from installation |
| `packages/api/src/routes/auth/github-oauth.ts` | Include `tenantId` in JWT on OAuth callback |
| `packages/api/src/serve.ts` (or app setup) | Register tenant context plugin after auth plugin |
| `packages/api/src/routes/saas/index.ts` | Use `request.tenantId` instead of manual installation scoping |
| `packages/api/src/routes/workflows/index.ts` | Use `request.tenantId` for workflow queries |
| `packages/api/src/routes/users/user-routes.ts` | Scope user queries to tenant |
| `packages/api/src/routes/users/api-key-routes.ts` | Scope API key queries to tenant |

## Implementation Plan

### Step 1: Tenant Context Plugin

```typescript
// packages/api/src/middleware/tenant-context.ts

import fp from 'fastify-plugin';
import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';
import type pg from 'pg';
import { DEFAULT_TENANT_ID } from '@tamma/shared';
import type { ITenantStore } from '../persistence/tenant-store.js';
import type { IUserStore } from '../persistence/user-store.js';

export interface TenantContextConfig {
  tenantStore: ITenantStore;
  userStore: IUserStore;
  pool: pg.Pool;
  enableAuth: boolean;
}

async function tenantContextPlugin(
  fastify: FastifyInstance,
  opts: TenantContextConfig,
): Promise<void> {
  const { tenantStore, userStore, pool, enableAuth } = opts;

  // Decorate request
  fastify.decorateRequest('tenantId', '');
  fastify.decorateRequest('pgClient', null);

  // Paths that don't need tenant context
  const TENANT_FREE_PATHS = [
    '/api/health',
    '/api/auth/login',
    '/api/auth/api-key',
    '/api/auth/callback',
  ];

  fastify.addHook('onRequest', async (request: FastifyRequest, reply: FastifyReply) => {
    // Skip tenant resolution for health/auth endpoints
    if (TENANT_FREE_PATHS.some((p) => request.url.startsWith(p))) {
      return;
    }

    let tenantId: string | null = null;

    if (!enableAuth) {
      // CLI/self-hosted/dev mode — use default tenant
      tenantId = DEFAULT_TENANT_ID;
    } else {
      // Source 1: JWT tenantId claim
      const authUser = (request as any).authUser;
      if (authUser?.tenantId) {
        tenantId = authUser.tenantId;
      }

      // Source 2: Installation context (API key auth)
      if (!tenantId) {
        const installCtx = (request as any).installationContext;
        if (installCtx?.installationId) {
          const tenant = await tenantStore.getTenantByExternalId(
            String(installCtx.installationId),
          );
          if (tenant) {
            tenantId = tenant.id;
          }
        }
      }

      // Source 3: User's tenant (OAuth/JWT auth without tenantId claim)
      if (!tenantId && authUser?.id) {
        const user = await userStore.getUser(authUser.id);
        if (user) {
          tenantId = user.tenantId;
        }
      }
    }

    if (!tenantId) {
      return reply.status(403).send({
        error: 'Tenant context could not be resolved',
      });
    }

    // Set on request
    (request as any).tenantId = tenantId;

    // Set PostgreSQL session variable for RLS
    const client = await pool.connect();
    try {
      await client.query('SET LOCAL app.current_tenant_id = $1', [tenantId]);
    } catch {
      client.release();
      return reply.status(500).send({ error: 'Failed to set tenant context' });
    }
    (request as any).pgClient = client;

    // Add tenantId to request logger for structured logging
    request.log = request.log.child({ tenantId });
  });

  // Release PG client after response
  fastify.addHook('onResponse', async (request: FastifyRequest) => {
    const client = (request as any).pgClient as pg.PoolClient | null;
    if (client) {
      try {
        await client.query('RESET app.current_tenant_id');
      } finally {
        client.release();
      }
      (request as any).pgClient = null;
    }
  });

  // Also release on error
  fastify.addHook('onError', async (request: FastifyRequest) => {
    const client = (request as any).pgClient as pg.PoolClient | null;
    if (client) {
      try {
        await client.query('RESET app.current_tenant_id');
      } finally {
        client.release();
      }
      (request as any).pgClient = null;
    }
  });
}

export const registerTenantContextPlugin = fp(tenantContextPlugin, {
  name: 'tamma-tenant-context',
  dependencies: ['tamma-auth'],
});
```

### Step 2: Include tenantId in JWT Claims

Update the JWT signing in `packages/api/src/auth/index.ts` and `github-oauth.ts`:

```typescript
const token = fastify.jwt.sign({
  id: user.id,
  username: user.username,
  role: user.role,
  tenantId: user.tenantId,  // NEW
});
```

### Step 3: Tenant-Aware Pool Helper

For stores that need to use the request's PG client (with tenant context already set):

```typescript
// packages/api/src/persistence/tenant-aware-pool.ts

import type pg from 'pg';

/**
 * Extracts the tenant-scoped PG client from a Fastify request.
 * Falls back to the pool if no client is attached (e.g., in tests).
 */
export function getClientFromRequest(
  request: { pgClient?: pg.PoolClient },
  pool: pg.Pool,
): pg.PoolClient | pg.Pool {
  return request.pgClient ?? pool;
}
```

### Step 4: Update Route Handlers

Route handlers no longer need to manually filter by tenant. The PG client on the request already has `app.current_tenant_id` set, so RLS handles filtering. However, for in-memory stores (dev/test), explicit `tenantId` passing is still needed:

```typescript
// Example route using request.tenantId
app.get('/api/workflows/instances', async (request, reply) => {
  const instances = await workflowStore.listInstances({
    tenantId: request.tenantId,
    page: request.query.page,
    pageSize: request.query.pageSize,
  });
  return reply.send(instances);
});
```

### Step 5: Tenant Provisioning on GitHub App Install

When the `installation.created` webhook fires:

```typescript
// In github-webhook.ts handler for 'installation.created'
async function handleInstallationCreated(payload: InstallationCreatedPayload) {
  // 1. Create tenant
  const tenant = await tenantStore.createTenant({
    name: payload.installation.account.login,
    slug: payload.installation.account.login.toLowerCase(),
    externalId: String(payload.installation.id),
  });

  // 2. Upsert installation with tenant_id
  await installationStore.upsertInstallation({
    installationId: payload.installation.id,
    accountLogin: payload.installation.account.login,
    accountType: payload.installation.account.type,
    appId: payload.installation.app_id,
    permissions: payload.installation.permissions,
    suspendedAt: null,
    apiKeyHash: null,
    apiKeyPrefix: null,
    apiKeyEncrypted: null,
    tenantId: tenant.id,
  });

  // 3. Link installing user to tenant
  // ... (on OAuth callback, user is linked to their tenant)
}
```

### Step 6: Task Queue Tenant Mapping

The existing `ITask.installationId` maps to a tenant. Update the task processing loop:

```typescript
// When dequeuing a task, resolve its tenant
const task = await taskQueue.dequeue();
if (task?.installationId) {
  const tenant = await tenantStore.getTenantByExternalId(String(task.installationId));
  if (tenant) {
    // Process task in tenant context
    await setTenantContext(pgClient, tenant.id);
  }
}
```

### Step 7: ELSA Workflow Dispatch

When dispatching an ELSA workflow from the API:

```typescript
const instanceId = await elsaWorkflowService.startWorkflowAsync(
  'ADL',
  {
    issueNumber: issue.number,
    repoFullName: repo.fullName,
    TenantId: request.tenantId,  // Passed as workflow variable
  },
  request.tenantId,
);
```

## Implementation Notes

1. **SET LOCAL vs SET**: `SET LOCAL` scopes the variable to the current transaction. When the transaction ends (COMMIT/ROLLBACK), the variable resets. This is safer than `SET` for pooled connections. However, it requires an open transaction. If the route handler does not use transactions, use `SET` with explicit `RESET` in the `onResponse` hook.
2. **Connection pool lifecycle**: The `pgClient` attached to the request is checked out from the pool in `onRequest` and released in `onResponse`/`onError`. This means each request holds a connection for its entire lifetime. For high-concurrency SaaS, monitor pool exhaustion.
3. **Superadmin override**: A future `X-Tamma-Tenant-Id` header can allow platform admins to impersonate a tenant for debugging. This MUST be gated behind an `owner` role check and logged as a security event.
4. **Caching tenant lookups**: The tenant resolution from `installationId` or user ID involves a DB query per request. For performance, consider caching tenant mappings with a short TTL (60s). The `ITenantStore` can be wrapped in a caching decorator.
5. **Multi-tenant users**: The current model assumes one user belongs to one tenant. If a user can be a member of multiple tenants (via multiple installations), the tenant is resolved from the installation context, not the user. The JWT would need a `tenantId` claim that matches the currently selected organization.
6. **Token invalidation on tenant change**: If a user switches between organizations, a new JWT must be issued with the correct `tenantId`. The dashboard can provide an organization switcher that triggers re-authentication.

## Testing Strategy

### Unit Tests

Create `packages/api/src/middleware/__tests__/tenant-context.test.ts`:

1. Auth disabled (dev mode): `tenantId` is set to `DEFAULT_TENANT_ID`
2. JWT with `tenantId` claim: uses the claim directly
3. API key auth: resolves tenant from installation's `external_id`
4. OAuth user without JWT tenantId: resolves from `user.tenantId`
5. No tenant resolvable: returns 403
6. Health check path: no tenant resolution attempted
7. `pgClient` is attached to request after resolution
8. `pgClient` is released on response completion
9. `pgClient` is released on error
10. Request logger includes `tenantId`

### Integration Tests

11. Full flow: API key auth => tenant resolved => RLS active => query returns only tenant's data
12. Full flow: JWT auth => tenant from claims => workflow list scoped
13. Cross-tenant rejection: authenticate as tenant A, query with forged tenant B context => 403 or zero rows
14. PG connection pool stress: 100 concurrent requests with different tenants => no context leakage between requests
15. GitHub webhook `installation.created` => tenant created, installation linked

### Backward Compatibility

16. All existing API tests pass with `enableAuth: false` (default tenant used)
17. CLI mode workflows operate normally with `DEFAULT_TENANT_ID`
18. Existing SaaS routes using `installationContext` continue to work (now augmented with tenant context)

### Security Tests

19. Tenant context cannot be spoofed via request headers (only resolved server-side)
20. Expired JWT with valid `tenantId` => rejected by auth layer before tenant middleware
21. Valid JWT with `tenantId` for a deleted tenant => 403 from tenant middleware

## Dependencies

- **Story 17.1** (Tenant Model + Database Schema) — `tenants` table, `ITenantStore`, `tenant_id` on all tables
- **Story 17.2** (Row-Level Security) — RLS policies must be in place for the PG session variable to have effect
- Internal: `packages/api/src/auth/index.ts`, `packages/api/src/auth/api-key-auth.ts`
- Internal: `packages/api/src/persistence/` (all stores)
- Internal: `packages/api/src/routes/` (all route files)

## Estimated Effort

| Task | Hours |
|------|-------|
| Tenant context Fastify plugin | 3 |
| JWT claims update (auth + OAuth) | 1.5 |
| Tenant-aware pool helper | 1 |
| Route handler updates (workflows, users, API keys, invites) | 2 |
| Task queue tenant mapping | 1 |
| ELSA dispatch tenant propagation | 1 |
| Tenant provisioning in webhook handler | 1 |
| Unit tests | 2 |
| Integration tests | 1.5 |
| **Total** | **14 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation | Architecture Team |
