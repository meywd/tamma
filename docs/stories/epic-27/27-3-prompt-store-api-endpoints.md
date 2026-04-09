# Story 27-3: Prompt Store API Endpoints

Status: ready-for-dev

## Story

As an **API consumer (dashboard, Elsa workflows, CLI)**,
I want REST API endpoints for prompt CRUD with tenant-scoped resolution and platform admin access to system defaults,
so that prompts can be managed programmatically with proper authorization.

## Acceptance Criteria

1. `GET /api/prompts` returns all resolved prompts for the authenticated user's account (tenant overrides merged with system defaults)
2. `GET /api/prompts/:role/:action` returns the resolved prompt for the current tenant (tenant override if exists, otherwise system default)
3. `PUT /api/prompts/:role/:action` creates or updates an tenant override for the current user's account; returns the updated prompt
4. `DELETE /api/prompts/:role/:action` deletes the tenant override (falls back to system default); returns 204 No Content
5. `GET /api/prompts/system` lists all system default prompts (read-only for non-platform-admins)
6. `GET /api/prompts/system/:role/:action` returns a specific system default prompt
7. `PUT /api/prompts/system/:role/:action` updates a system default prompt (platform admin only); returns 403 for non-admins
8. `POST /api/prompts/:role/:action/render` renders a prompt with variables for the current tenant (existing endpoint, now tenant-aware)
9. All mutating endpoints validate request body: `template` is required non-empty string, `maxTokens > 0`, `variables` is an array of strings if provided
10. All endpoints require authentication (JWT or API key)
11. Tenant context is extracted from the authenticated user's session (from Epic 16/17 middleware)
12. Error responses use consistent format: `{ error: string, code?: string }`
13. Existing `/api/prompts/:role/:action` routes continue to work for unauthenticated/CLI mode by resolving against system defaults

## Technical Context

### Current API Routes

The existing routes in `packages/api/src/routes/prompts/prompt-routes.ts`:
- `GET /api/prompts` -- list all (no tenant scoping)
- `GET /api/prompts/:role/:action` -- get (no tenant scoping)
- `PUT /api/prompts/:role/:action` -- upsert (modifies global store)
- `POST /api/prompts/:role/:action/render` -- render with variables

These must be replaced with tenant-aware versions.

### Route Structure

```
/api/prompts                         GET     List resolved prompts (tenant merged with system)
/api/prompts/:role/:action           GET     Get resolved prompt
/api/prompts/:role/:action           PUT     Create/update tenant override
/api/prompts/:role/:action           DELETE  Delete tenant override
/api/prompts/:role/:action/render    POST    Render prompt with variables
/api/prompts/system                  GET     List system defaults
/api/prompts/system/:role/:action    GET     Get system default
/api/prompts/system/:role/:action    PUT     Update system default (admin only)
/api/prompts/system/:role/:action    DELETE  Reset system default to hardcoded (admin only)
```

### Route Parameter Conflict: "system" vs ":role"

The routes `/api/prompts/system` and `/api/prompts/:role/:action` could conflict since "system" could match `:role`. This is handled by registering the `/api/prompts/system*` routes before the parameterized routes. Fastify resolves routes in registration order for parametric paths.

### Authentication & Authorization

| Endpoint | Auth Required | Authorization |
|----------|--------------|---------------|
| `GET /api/prompts` | Yes | Any authenticated user |
| `GET /api/prompts/:role/:action` | Yes (fallback to system defaults if unauthenticated) | Any authenticated user |
| `PUT /api/prompts/:role/:action` | Yes | Tenant admin or owner |
| `DELETE /api/prompts/:role/:action` | Yes | Tenant admin or owner |
| `GET /api/prompts/system` | Yes | Any authenticated user (read-only) |
| `GET /api/prompts/system/:role/:action` | Yes | Any authenticated user (read-only) |
| `PUT /api/prompts/system/:role/:action` | Yes | Platform admin only (owner role) |
| `DELETE /api/prompts/system/:role/:action` | Yes | Platform admin only (owner role) |
| `POST /api/prompts/:role/:action/render` | Yes | Any authenticated user |

### Files to Create

| File | Purpose |
|------|---------|
| `packages/api/src/routes/prompts/prompt-routes.ts` | Rewritten with tenant-scoped routes |
| `packages/api/src/routes/prompts/prompt-routes.test.ts` | Updated tests |

### Files to Modify

| File | Purpose |
|------|---------|
| `packages/api/src/index.ts` | Wire new route registration with `IPromptStore` and auth middleware |

## Implementation Plan

### Step 1: Define Route Handlers

Each route handler extracts `tenantId` from the request context (set by auth middleware from Epic 16):

```typescript
async function handleGetPrompt(
  request: FastifyRequest<{ Params: RoleActionParams }>,
  reply: FastifyReply,
) {
  const tenantId = request.tenantId ?? null; // from auth middleware
  const { role, action } = request.params;
  const template = await store.get(tenantId, role, action);
  if (template === undefined) {
    return reply.status(404).send({ error: `Prompt not found for role="${role}", action="${action}"` });
  }
  return reply.send(template);
}
```

### Step 2: Register System Routes First

```typescript
// System default routes (must be registered before parametric :role/:action)
app.get('/api/prompts/system', handleListSystemDefaults);
app.get('/api/prompts/system/:role/:action', handleGetSystemDefault);
app.put('/api/prompts/system/:role/:action', { preHandler: [requirePlatformAdmin] }, handleUpsertSystemDefault);
app.delete('/api/prompts/system/:role/:action', { preHandler: [requirePlatformAdmin] }, handleResetSystemDefault);

// Tenant-scoped routes
app.get('/api/prompts', handleListPrompts);
app.get('/api/prompts/:role/:action', handleGetPrompt);
app.put('/api/prompts/:role/:action', { preHandler: [requireTenantAdmin] }, handleUpsertPrompt);
app.delete('/api/prompts/:role/:action', { preHandler: [requireTenantAdmin] }, handleDeletePrompt);
app.post('/api/prompts/:role/:action/render', handleRenderPrompt);
```

### Step 3: Request Validation

Use Fastify's schema validation or manual checks (matching current pattern):

```typescript
// PUT body validation
if (typeof body?.template !== 'string' || body.template.length === 0) {
  return reply.status(400).send({ error: 'Request body must include a non-empty "template" string' });
}
if (body.template.length > 500_000) {
  return reply.status(400).send({ error: 'Template exceeds maximum size of 500,000 characters' });
}
```

### Step 4: Render Endpoint

The render endpoint is updated to pass `tenantId`:

```typescript
async function handleRenderPrompt(request, reply) {
  const tenantId = request.tenantId ?? null;
  const { role, action } = request.params;
  const { variables } = request.body;
  const result = await store.render(tenantId, role, action, { variables });
  if (result === undefined) {
    return reply.status(404).send({ error: 'Prompt not found' });
  }
  return reply.send(result);
}
```

### Step 5: Delete System Default (Reset)

`DELETE /api/prompts/system/:role/:action` calls `store.resetSystemDefault(role, action)` to restore the hardcoded default from `default-prompts.ts`:

```typescript
async function handleResetSystemDefault(request, reply) {
  const { role, action } = request.params;
  const restored = await store.resetSystemDefault(role, action);
  if (restored === undefined) {
    return reply.status(404).send({ error: 'No hardcoded default exists for this role+action' });
  }
  return reply.send(restored);
}
```

## Implementation Notes

1. The `tenantId` is extracted from the request via `request.tenantId` which is set by the auth middleware from Epic 16/17. For unauthenticated requests or CLI mode, `tenantId` defaults to `null`, which resolves system defaults only.
2. The "system" literal in the URL path is not a valid role name (roles are lowercase single words like "developer"), so the route conflict is minimal. The Fastify router handles this correctly when static routes are registered before parametric routes.
3. The `DELETE /api/prompts/:role/:action` endpoint only deletes tenant overrides. System defaults cannot be deleted via this endpoint (only reset via the system endpoint).
4. Rate limiting should be applied to mutating endpoints (PUT, DELETE) to prevent abuse. This can use the existing rate limiting middleware.
5. Response format matches the existing `PromptTemplate` interface for backward compatibility with the Elsa `ResolvePromptFromRegistryActivity`.

## Testing Strategy

### Unit Tests

1. `GET /api/prompts` returns merged list for authenticated tenant
2. `GET /api/prompts` returns system defaults for unauthenticated request
3. `GET /api/prompts/:role/:action` returns tenant override when it exists
4. `GET /api/prompts/:role/:action` returns system default when no override exists
5. `PUT /api/prompts/:role/:action` creates tenant override; returns 200
6. `PUT /api/prompts/:role/:action` rejects invalid body; returns 400
7. `PUT /api/prompts/:role/:action` rejects non-admin user; returns 403
8. `DELETE /api/prompts/:role/:action` removes tenant override; returns 204
9. `DELETE /api/prompts/:role/:action` returns 404 when no override exists
10. `GET /api/prompts/system` returns all system defaults
11. `GET /api/prompts/system/:role/:action` returns specific system default
12. `PUT /api/prompts/system/:role/:action` updates system default for platform admin
13. `PUT /api/prompts/system/:role/:action` rejects non-platform-admin; returns 403
14. `DELETE /api/prompts/system/:role/:action` resets to hardcoded default
15. `POST /api/prompts/:role/:action/render` renders with tenant-scoped resolution

### Integration Tests

16. Full tenant override lifecycle: create, read, update, delete, verify fallback
17. Elsa workflow `ResolvePromptFromRegistryActivity` can call render endpoint with tenantId

## Dependencies

- **Story 27-2** (Prompt Store Service) -- `IPromptStore` implementation must exist
- **Epic 16** (Story 16.5: RBAC Enforcement) -- auth middleware provides `request.tenantId` and role checks
- Internal: `packages/api/src/routes/prompts/prompt-routes.ts` (replaced)

## Estimated Effort

| Task | Hours |
|------|-------|
| Route handler implementation (9 endpoints) | 4 |
| Request validation and error handling | 2 |
| Auth middleware integration | 1.5 |
| Unit tests (15 tests) | 3 |
| Integration tests (2 tests) | 1.5 |
| **Total** | **12 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-04-08 | 1.0 | Initial story creation | Architecture Team |
