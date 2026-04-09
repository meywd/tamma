# Story 27-3: Prompt Store API Endpoints — Implementation Plan

## Overview

Rewrite `packages/api/src/routes/prompts/prompt-routes.ts` to add account-scoped CRUD, system default management (admin-only), and backward-compatible unauthenticated access. Register `/api/prompts/system*` static routes before parametric `:role/:action` routes to avoid Fastify route conflicts.

---

## Step-by-Step Implementation Tasks

### Task 1: Define Route Types and Schemas (1 hour)

**File to modify**: `packages/api/src/routes/prompts/prompt-routes.ts`

```typescript
// Route parameter types
interface RoleActionParams {
  role: string;
  action: string;
}

// Request body for PUT (create/update)
interface UpsertBody {
  template: string;
  variables?: string[];
  systemPrompt?: string;
  enableTools?: boolean;
  maxTokens?: number;
}

// Request body for POST render
interface RenderBody {
  variables: Record<string, string>;
}

// Extend FastifyRequest to include account context (from auth middleware, Epic 16/17)
declare module 'fastify' {
  interface FastifyRequest {
    accountId?: string;  // Set by auth middleware; undefined = unauthenticated
    userId?: string;     // Set by auth middleware
    userRole?: string;   // 'owner' | 'admin' | 'member' — set by auth middleware
  }
}
```

---

### Task 2: Implement System Default Routes (2 hours)

These routes must be registered BEFORE the parametric `:role/:action` routes so Fastify resolves the literal "system" path before attempting to match it as a `:role` parameter.

```typescript
export async function registerPromptRoutes(
  app: FastifyInstance,
  store: IPromptStore,
): Promise<void> {

  // =====================================================================
  // SYSTEM DEFAULT ROUTES (registered first for route priority)
  // =====================================================================

  // GET /api/prompts/system — list all system defaults
  app.get('/api/prompts/system', async (_request, reply) => {
    const defaults = await store.listSystemDefaults();
    return reply.send({ templates: defaults, total: defaults.length });
  });

  // GET /api/prompts/system/:role/:action — get specific system default
  app.get<{ Params: RoleActionParams }>(
    '/api/prompts/system/:role/:action',
    async (request, reply) => {
      const { role, action } = request.params;
      const template = await store.getSystemDefault(role, action);
      if (template === undefined) {
        return reply.status(404).send({ error: `System default not found for role="${role}", action="${action}"` });
      }
      return reply.send(template);
    },
  );

  // PUT /api/prompts/system/:role/:action — update system default (platform admin only)
  app.put<{ Params: RoleActionParams; Body: UpsertBody }>(
    '/api/prompts/system/:role/:action',
    { preHandler: [requirePlatformAdmin] },
    async (request, reply) => {
      const { role, action } = request.params;
      const body = request.body as UpsertBody;
      const error = validateUpsertBody(body);
      if (error) return reply.status(400).send({ error });

      const input = buildUpsertInput(body);
      const updated = await store.upsertSystemDefault(role, action, input, request.userId);
      return reply.send(updated);
    },
  );

  // DELETE /api/prompts/system/:role/:action — reset to hardcoded default (platform admin only)
  app.delete<{ Params: RoleActionParams }>(
    '/api/prompts/system/:role/:action',
    { preHandler: [requirePlatformAdmin] },
    async (request, reply) => {
      const { role, action } = request.params;
      const restored = await store.resetSystemDefault(role, action, request.userId);
      if (restored === undefined) {
        return reply.status(404).send({ error: 'No hardcoded default exists for this role+action' });
      }
      return reply.send(restored);
    },
  );
  // ... (account-scoped routes follow in Task 3)
}
```

---

### Task 3: Implement Account-Scoped Routes (2.5 hours)

```typescript
  // =====================================================================
  // ACCOUNT-SCOPED ROUTES
  // =====================================================================

  // GET /api/prompts — list resolved prompts for current account
  app.get('/api/prompts', async (request, reply) => {
    const accountId = request.accountId ?? null;
    const summaries = await store.list(accountId);
    return reply.send({ templates: summaries, total: summaries.length });
  });

  // GET /api/prompts/:role/:action — get resolved prompt
  app.get<{ Params: RoleActionParams }>(
    '/api/prompts/:role/:action',
    async (request, reply) => {
      const accountId = request.accountId ?? null;
      const { role, action } = request.params;
      const template = await store.get(accountId, role, action);
      if (template === undefined) {
        return reply.status(404).send({
          error: `Prompt not found for role="${role}", action="${action}"`,
        });
      }
      return reply.send(template);
    },
  );

  // PUT /api/prompts/:role/:action — create/update account override
  app.put<{ Params: RoleActionParams; Body: UpsertBody }>(
    '/api/prompts/:role/:action',
    { preHandler: [requireAccountAdmin] },
    async (request, reply) => {
      const accountId = request.accountId;
      if (!accountId) {
        return reply.status(401).send({ error: 'Authentication required to create account overrides' });
      }
      const { role, action } = request.params;
      const body = request.body as UpsertBody;
      const error = validateUpsertBody(body);
      if (error) return reply.status(400).send({ error });

      try {
        const input = buildUpsertInput(body);
        const updated = await store.upsert(accountId, role, action, input, request.userId);
        return reply.status(200).send(updated);
      } catch (err) {
        const message = err instanceof Error ? err.message : 'Failed to update prompt';
        return reply.status(400).send({ error: message });
      }
    },
  );

  // DELETE /api/prompts/:role/:action — delete account override
  app.delete<{ Params: RoleActionParams }>(
    '/api/prompts/:role/:action',
    { preHandler: [requireAccountAdmin] },
    async (request, reply) => {
      const accountId = request.accountId;
      if (!accountId) {
        return reply.status(401).send({ error: 'Authentication required' });
      }
      const { role, action } = request.params;
      const deleted = await store.delete(accountId, role, action, request.userId);
      if (!deleted) {
        return reply.status(404).send({ error: 'No account override exists for this role+action' });
      }
      return reply.status(204).send();
    },
  );

  // POST /api/prompts/:role/:action/render — render with account resolution
  app.post<{ Params: RoleActionParams; Body: RenderBody }>(
    '/api/prompts/:role/:action/render',
    async (request, reply) => {
      // Accept accountId from: auth session > X-Account-Id header > query param
      const accountId = request.accountId
        ?? (request.headers['x-account-id'] as string | undefined)
        ?? (request.query as Record<string, string>)['accountId']
        ?? null;

      const { role, action } = request.params;
      const body = request.body as RenderBody;

      // Validate variables
      if (!body?.variables || typeof body.variables !== 'object' || Array.isArray(body.variables)) {
        return reply.status(400).send({ error: 'Request body must include a "variables" object' });
      }
      for (const [key, value] of Object.entries(body.variables)) {
        if (typeof value !== 'string') {
          return reply.status(400).send({ error: `Variable "${key}" must be a string value` });
        }
      }

      const result = await store.render(accountId, role, action, { variables: body.variables });
      if (result === undefined) {
        return reply.status(404).send({ error: `Prompt not found for role="${role}", action="${action}"` });
      }
      return reply.send(result);
    },
  );
```

---

### Task 4: Implement Validation and Auth Helpers (1.5 hours)

```typescript
// --- Validation helper ---
function validateUpsertBody(body: UpsertBody | undefined): string | undefined {
  if (typeof body?.template !== 'string' || body.template.length === 0) {
    return 'Request body must include a non-empty "template" string';
  }
  if (body.template.length > 500_000) {
    return 'Template exceeds maximum size of 500,000 characters';
  }
  if (body.variables !== undefined && !Array.isArray(body.variables)) {
    return '"variables" must be an array of strings';
  }
  if (body.maxTokens !== undefined) {
    if (typeof body.maxTokens !== 'number' || body.maxTokens <= 0 || !Number.isFinite(body.maxTokens)) {
      return '"maxTokens" must be a positive number';
    }
  }
  return undefined;
}

// --- Input builder ---
function buildUpsertInput(body: UpsertBody): UpsertPromptInput {
  const input: UpsertPromptInput = { template: body.template };
  if (body.variables !== undefined) input.variables = body.variables;
  if (body.systemPrompt !== undefined) input.systemPrompt = body.systemPrompt;
  if (body.enableTools !== undefined) input.enableTools = body.enableTools;
  if (body.maxTokens !== undefined) input.maxTokens = body.maxTokens;
  return input;
}

// --- Auth middleware stubs (integrate with Epic 16 RBAC) ---
async function requirePlatformAdmin(request: FastifyRequest, reply: FastifyReply): Promise<void> {
  try {
    const decoded = await request.jwtVerify<{ role: string }>();
    if (decoded.role !== 'owner') {
      return reply.status(403).send({ error: 'Platform admin access required' });
    }
  } catch {
    return reply.status(401).send({ error: 'Not authenticated' });
  }
}

async function requireAccountAdmin(request: FastifyRequest, reply: FastifyReply): Promise<void> {
  try {
    const decoded = await request.jwtVerify<{ role: string }>();
    if (decoded.role !== 'owner' && decoded.role !== 'admin') {
      return reply.status(403).send({ error: 'Account admin access required' });
    }
  } catch {
    return reply.status(401).send({ error: 'Not authenticated' });
  }
}
```

**Note**: The `requirePlatformAdmin` and `requireAccountAdmin` hooks integrate with the JWT auth from Epic 16. If that middleware does not yet set `request.accountId`, a fallback extraction from the JWT payload or `X-Account-Id` header is used.

---

### Task 5: Wire Route Registration (1 hour)

**File to modify**: `packages/api/src/index.ts`

Update the route registration call to pass `IPromptStore` instead of `PromptStore`:

```typescript
// Before:
await registerPromptRoutes(app, promptStore);

// After (no change to call site if types align — just the store instance changes):
await registerPromptRoutes(app, promptStore); // promptStore is now IPromptStore
```

---

### Task 6: API Contract Documentation (1 hour)

**Request/Response shapes for each endpoint:**

#### GET /api/prompts
```json
// Response 200
{
  "templates": [
    {
      "role": "developer",
      "action": "implement",
      "version": 2,
      "enableTools": true,
      "maxTokens": 16384,
      "variableCount": 6,
      "updatedAt": "2026-04-08T12:00:00.000Z",
      "source": "override",
      "accountId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
    }
  ],
  "total": 80
}
```

#### PUT /api/prompts/:role/:action
```json
// Request
{
  "template": "Implement {{feature}} using {{framework}}",
  "variables": ["feature", "framework"],
  "systemPrompt": "You are an expert developer.",
  "enableTools": true,
  "maxTokens": 8192
}

// Response 200 (full PromptTemplate)
{
  "role": "developer",
  "action": "implement",
  "version": 3,
  "template": "Implement {{feature}} using {{framework}}",
  "variables": ["feature", "framework"],
  "systemPrompt": "You are an expert developer.",
  "enableTools": true,
  "maxTokens": 8192,
  "createdAt": "2026-04-01T00:00:00.000Z",
  "updatedAt": "2026-04-08T12:00:00.000Z"
}
```

#### POST /api/prompts/:role/:action/render
```json
// Request
{
  "variables": {
    "feature": "authentication",
    "framework": "Fastify"
  }
}

// Response 200
{
  "role": "developer",
  "action": "implement",
  "version": 3,
  "renderedTemplate": "Implement authentication using Fastify",
  "renderedSystemPrompt": "You are an expert developer.",
  "enableTools": true,
  "maxTokens": 8192,
  "unresolvedVariables": []
}
```

---

### Task 7: Unit Tests (3 hours)

**File to rewrite**: `packages/api/src/routes/prompts/prompt-routes.test.ts`

Uses `InMemoryPromptStore` with seeded defaults. Tests auth by mocking JWT verification.

| # | Test | Expected |
|---|------|----------|
| 1 | `GET /api/prompts` — authenticated user | Returns merged list with source badges |
| 2 | `GET /api/prompts` — unauthenticated | Returns system defaults only |
| 3 | `GET /api/prompts/:role/:action` — with account override | Returns override |
| 4 | `GET /api/prompts/:role/:action` — no override | Returns system default |
| 5 | `PUT /api/prompts/:role/:action` — valid body | Creates override, returns 200 |
| 6 | `PUT /api/prompts/:role/:action` — invalid body | Returns 400 |
| 7 | `PUT /api/prompts/:role/:action` — non-admin | Returns 403 |
| 8 | `DELETE /api/prompts/:role/:action` — existing override | Returns 204 |
| 9 | `DELETE /api/prompts/:role/:action` — no override | Returns 404 |
| 10 | `GET /api/prompts/system` — list system defaults | Returns all 80 |
| 11 | `GET /api/prompts/system/:role/:action` — existing | Returns default |
| 12 | `PUT /api/prompts/system/:role/:action` — platform admin | Updates, returns 200 |
| 13 | `PUT /api/prompts/system/:role/:action` — non-admin | Returns 403 |
| 14 | `DELETE /api/prompts/system/:role/:action` — reset | Restores hardcoded default |
| 15 | `POST /api/prompts/:role/:action/render` — with accountId | Account-scoped resolution |
| 16 | `POST /api/prompts/:role/:action/render` — with X-Account-Id header | Elsa workflow path |
| 17 | Route priority: `/api/prompts/system` resolves before `:role` | "system" not treated as a role |

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| None | All work is modifications to existing files | |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/api/src/routes/prompts/prompt-routes.ts` | Complete rewrite with account-scoped + system routes |
| 2 | `packages/api/src/routes/prompts/prompt-routes.test.ts` | Complete rewrite with 17 test cases |
| 3 | `packages/api/src/index.ts` | Update route registration to pass `IPromptStore` |

---

## Dependencies

- **Story 27-2** (Prompt Store Service) — `IPromptStore` interface and `InMemoryPromptStore` must exist
- **Epic 16** (Story 16.5: RBAC) — auth middleware for `request.accountId`, `request.userId`, `request.userRole`

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Route conflict between `/api/prompts/system` and `/api/prompts/:role/:action` | Register system routes first; Fastify resolves static routes before parametric |
| Auth middleware from Epic 16 may not yet set `request.accountId` | Add fallback extraction from JWT payload and `X-Account-Id` header |
| Render endpoint used by Elsa (server-to-server) needs accountId without auth | Accept `X-Account-Id` header as fallback; validate UUID format |
| Breaking change for existing unauthenticated callers | Unauthenticated requests default to `accountId = null`, resolving system defaults (same as before) |

---

## Estimated Effort

| Task | Hours |
|------|-------|
| Route types and schemas | 1 |
| System default routes | 2 |
| Account-scoped routes | 2.5 |
| Validation and auth helpers | 1.5 |
| Route registration wiring | 1 |
| API contract documentation | 1 |
| Unit tests (17 tests) | 3 |
| **Total** | **12 hours** |
