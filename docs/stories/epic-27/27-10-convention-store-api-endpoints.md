# Story 27-10: Convention Store API Endpoints

> Updated 2026-05-18: keyword model removed; see SPEC docs/superpowers/specs/2026-05-18-role-action-taxonomy-and-resolution-design.md

Status: ready-for-dev

## Story

As an **API consumer (dashboard, Elsa workflows, CLI)**,
I want REST API endpoints for convention CRUD with tenant-scoped resolution and platform admin access to system defaults,
so that conventions can be managed programmatically with proper authorization and the resolver can be tested via API.

## Acceptance Criteria

### Tenant-Scoped Endpoints

1. `GET /api/conventions` returns all resolved conventions for the authenticated user's tenant (tenant overrides merged with system defaults), each marked with `isOverride: true/false`
2. `GET /api/conventions/:role/:action` returns the resolved convention for the current tenant using exact `(role, action)` lookup with tenant override (SPEC §3.3): tenant-override row → system-default row
3. `PUT /api/conventions/:role/:action` creates or updates a tenant override for the current user's tenant; returns the updated convention
4. `DELETE /api/conventions/:role/:action` deletes the tenant override (falls back to system default); returns 204 No Content
5. `POST /api/conventions/resolve` accepts `{ role, action }` and returns the resolved convention body for the current tenant via exact `(role, action)` lookup (SPEC §3.3) — this is the test/preview endpoint

### System Default Endpoints

6. `GET /api/conventions/defaults` lists all system default conventions
7. `GET /api/conventions/defaults/:role/:action` returns a specific system default convention by exact `(role, action)` key
8. `PUT /api/admin/conventions/:role/:action` creates or updates a system default convention (platform admin only); returns 403 for non-admins
9. `DELETE /api/admin/conventions/:role/:action` deletes a system default convention (platform admin only)
10. `POST /api/admin/conventions/:role/:action/reset` resets a system default to the hardcoded original from `ConventionTemplates.cs` (platform admin only)

### Validation & Auth

11. All mutating endpoints validate request body: `name` required non-empty, `body` required non-empty, `role` and `action` must each be a known value from the canonical taxonomy (SPEC §4)
12. All endpoints require authentication (JWT or session cookie)
13. Tenant context is extracted from the authenticated user's session (from Epic 16/17 middleware)
14. Error responses use consistent format: `{ error: string, code?: string }`
15. Existing `/api/convention-templates` routes remain for backward compatibility (read-only, static templates from `ConventionTemplates.cs`)

### Registry Endpoints (for UI pickers)

16. `GET /api/conventions/registry/roles` returns the list of valid role values from the canonical taxonomy (SPEC §4)
17. `GET /api/conventions/registry/actions` returns the list of known action names per role from the canonical taxonomy (SPEC §4)
18. `GET /api/conventions/registry/role-actions` returns the full `(role, action)` matrix of valid combinations

## Technical Context

### Current API Routes

The existing convention endpoints in `apps/tamma-elsa/src/Tamma.Api/Endpoints/ConventionEndpoints.cs`:
- `GET /api/convention-templates` — list all static templates (key, name, description)
- `GET /api/convention-templates/:key` — get full template with conventions string

These are read-only endpoints serving static data from `ConventionTemplates.cs`. They remain unchanged for backward compatibility. The new endpoints serve the DB-backed convention store.

### Route Structure

```
-- Tenant-scoped (convention overrides + merged view)
GET    /api/conventions                           List resolved (merged)
GET    /api/conventions/:role/:action             Get resolved by (role, action)
PUT    /api/conventions/:role/:action             Create/update tenant override
DELETE /api/conventions/:role/:action             Delete tenant override

-- Resolution preview (exact (role, action) lookup, SPEC §3.3)
POST   /api/conventions/resolve                   Preview resolution with { role, action }

-- System defaults (platform admin)
GET    /api/conventions/defaults                  List system defaults
GET    /api/conventions/defaults/:role/:action    Get system default by (role, action)
PUT    /api/admin/conventions/:role/:action       Create/update system default (admin)
DELETE /api/admin/conventions/:role/:action       Delete system default (admin)
POST   /api/admin/conventions/:role/:action/reset Reset to hardcoded (admin)

-- Registry (for UI pickers)
GET    /api/conventions/registry/roles            Valid role values
GET    /api/conventions/registry/actions          Known action names per role
GET    /api/conventions/registry/role-actions     Full (role, action) matrix

-- Legacy (unchanged)
GET    /api/convention-templates                  Static template list
GET    /api/convention-templates/:key             Static template detail
```

### Route Parameter Conflict: "defaults" / "resolve" / "registry" vs ":role/:action"

Same pattern as prompt store (Story 27-3): register `/api/conventions/defaults*`, `/api/conventions/resolve`, and `/api/conventions/registry*` routes before the parameterized `/api/conventions/:role/:action` route.

### Authentication & Authorization

| Endpoint | Auth Required | Authorization |
|----------|--------------|---------------|
| `GET /api/conventions` | Yes | Any authenticated user |
| `GET /api/conventions/:role/:action` | Yes | Any authenticated user |
| `PUT /api/conventions/:role/:action` | Yes | Tenant admin or owner |
| `DELETE /api/conventions/:role/:action` | Yes | Tenant admin or owner |
| `POST /api/conventions/resolve` | Yes | Any authenticated user |
| `GET /api/conventions/defaults` | Yes | Any authenticated user (read-only) |
| `GET /api/conventions/defaults/:role/:action` | Yes | Any authenticated user (read-only) |
| `PUT /api/admin/conventions/:role/:action` | Yes | Platform admin only |
| `DELETE /api/admin/conventions/:role/:action` | Yes | Platform admin only |
| `POST /api/admin/conventions/:role/:action/reset` | Yes | Platform admin only |
| `GET /api/conventions/registry/*` | Yes | Any authenticated user |
| `GET /api/convention-templates*` | No | Public (legacy) |

### Request/Response Formats

**PUT body (create/update):**

```json
{
  "name": "Security Review Standards",
  "description": "OWASP-based security review conventions",
  "body": "# Security Review Conventions\n\n## Input Validation\n...",
  "enabled": true
}
```

The `role` and `action` are supplied via the URL path (`PUT /api/conventions/:role/:action`), not the request body.

**GET response (single):**

```json
{
  "id": "uuid",
  "role": "security-reviewer",
  "action": "review",
  "name": "Security Review Standards",
  "description": "OWASP-based security review conventions",
  "body": "# Security Review Conventions\n...",
  "enabled": true,
  "version": 2,
  "isOverride": true,
  "source": "tenant",
  "updatedAt": "2026-05-04T12:00:00.000Z"
}
```

**POST /resolve request/response:**

```json
// Request
{ "role": "security-reviewer", "action": "review" }

// Response — exact (role, action) lookup with tenant override (SPEC §3.3)
{
  "role": "security-reviewer",
  "action": "review",
  "body": "# Security Review Conventions\n...",
  "source": "tenant",
  "version": 2
}
```

### Rate Limiting

Following Epic 27 cross-cutting requirements:
- Read endpoints (`GET`): 100 requests/minute per tenant
- Write endpoints (`PUT`, `DELETE`): 30 requests/minute per tenant
- Resolve endpoint (`POST /resolve`): 300 requests/minute per tenant (called by Elsa workflows)

### Files to Create

| File | Purpose |
|------|---------|
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/ConventionStoreEndpoints.cs` | New DB-backed convention endpoints |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Conventions/ConventionStoreEndpointsTests.cs` | Endpoint tests |

### Files to Modify

| File | Change |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Register new endpoints |

## Implementation Plan

### Step 1: Create ConventionStoreEndpoints

Define the endpoint group with routes mapped to `IConventionStore` methods. Follow the existing `PromptEndpoints.cs` pattern for auth middleware, error handling, and response formatting.

### Step 2: Implement Resolve Endpoint

The `/resolve` endpoint accepts `{ role, action }`, calls `IConventionStore.GetAsync(tenantId, role, action)` (exact `(role, action)` lookup with tenant override, SPEC §3.3), and returns the resolved convention body. This is the same code path the Elsa workflow uses, exposed for testing.

### Step 3: Implement Registry Endpoints

Return static lists of valid values for use in UI pickers:
- Roles: from the canonical role enum (SPEC §4)
- Actions: from the canonical per-role action sets (SPEC §4)
- Role-action matrix: full set of valid `(role, action)` combinations

### Step 4: Validation

Request body validation using the same patterns as prompt endpoints:
- `name`: required, non-empty string, max 200 chars
- `body`: required, non-empty string, max 50000 chars
- URL path `role` and `action`: each must be a known value from the canonical taxonomy (SPEC §4); return 400 for unknown combinations

### Step 5: Route Parameters

Conventions are keyed by `(role, action)` path parameters: `PUT /api/conventions/:role/:action`. Both parameters are validated server-side against the canonical taxonomy enum values.

## Implementation Notes

1. The `ConventionStoreEndpoints` are registered in a separate endpoint group from the legacy `ConventionEndpoints` to avoid route conflicts.
2. The `/resolve` endpoint is intentionally accessible to all authenticated users (not just admins) because it's useful for tenant admins testing their overrides.
3. The response format for `GET /api/conventions` includes `isOverride` to distinguish tenant overrides from system defaults in the UI.
4. Platform admin system default endpoints use `/api/admin/conventions/` prefix to match the admin route namespace pattern.
5. The legacy `/api/convention-templates` endpoints remain unchanged and are NOT deprecated yet — they serve the `ConventionSelector` UI component until the new convention store UI replaces it.
6. **No keyword table**: The keyword model (`convention_keywords`, `matchMode`, `alwaysApply`, `priority`, `category` column) has been removed. Conventions are stored as a simple `(tenant_id, role, action, body, name, description, enabled, version)` row; resolution is exact `(role, action)` lookup (SPEC §3.3).

## Testing Strategy

### Unit Tests

1. `GET /api/conventions` returns merged view with correct `isOverride` flags
2. `GET /api/conventions/:key` returns tenant override when exists
3. `GET /api/conventions/:key` falls back to system default when no override
4. `PUT /api/conventions/:key` creates tenant override
5. `PUT /api/conventions/:role/:action` returns 400 for invalid body (missing name) or unknown `(role, action)` combination
6. `PUT /api/conventions/:key` returns 403 for non-admin tenant members
7. `DELETE /api/conventions/:key` removes override, returns 204
8. `POST /api/conventions/resolve` returns triggered + skipped conventions
9. `POST /api/conventions/resolve` returns empty body when nothing matches
10. `GET /api/conventions/defaults` lists system defaults
11. `PUT /api/admin/conventions/:key` returns 403 for non-platform-admins
12. `PUT /api/admin/conventions/:key` creates system default for platform admin
13. `POST /api/admin/conventions/:key/reset` restores hardcoded default
14. `GET /api/conventions/registry/roles` returns valid role list; `GET /api/conventions/registry/role-actions` returns full `(role, action)` matrix
15. Key validation rejects invalid slugs

### Integration Tests

16. Full CRUD lifecycle: create override → list (appears with isOverride) → delete → list (falls back)
17. Resolve end-to-end: create convention for `(role, action)` → call resolve with `{ role, action }` → verify correct body returned
18. Legacy `/api/convention-templates` still works alongside new endpoints

## Dependencies

- **Story 27-9** (Convention Store Service) — `IConventionStore` must exist
- **Story 27-15** (Taxonomy) — canonical `(role, action)` enum values used for validation and registry endpoints
- **Epic 16/17** — authentication middleware for tenant context extraction
- Internal: `apps/tamma-elsa/src/Tamma.Api/Endpoints/ConventionEndpoints.cs` (legacy, remains unchanged)

## Estimated Effort

| Task | Hours |
|------|-------|
| ConventionStoreEndpoints (tenant-scoped routes) | 3 |
| System default + admin routes | 2 |
| Resolve endpoint | 1 |
| Registry endpoints | 0.5 |
| Request validation | 1 |
| Rate limiting configuration | 0.5 |
| Unit tests (15 tests) | 2.5 |
| Integration tests (3 tests) | 1.5 |
| **Total** | **12 hours** |

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-05-04 | 1.0 | Initial story creation | Architecture Team |
