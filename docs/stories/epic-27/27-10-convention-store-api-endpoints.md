# Story 27-10: Convention Store API Endpoints

Status: ready-for-dev

## Story

As an **API consumer (dashboard, Elsa workflows, CLI)**,
I want REST API endpoints for convention CRUD with tenant-scoped resolution and platform admin access to system defaults,
so that conventions can be managed programmatically with proper authorization and the resolver can be tested via API.

## Acceptance Criteria

### Tenant-Scoped Endpoints

1. `GET /api/conventions` returns all resolved conventions for the authenticated user's tenant (tenant overrides merged with system defaults), each marked with `isOverride: true/false`
2. `GET /api/conventions/:key` returns the resolved convention for the current tenant (tenant override if exists, otherwise system default)
3. `PUT /api/conventions/:key` creates or updates a tenant override for the current user's tenant; returns the updated convention
4. `DELETE /api/conventions/:key` deletes the tenant override (falls back to system default); returns 204 No Content
5. `POST /api/conventions/resolve` accepts `{ action, tools[], searchableText, repoLanguages[] }` and returns the full resolution result (triggered keys, skipped keys, merged body) for the current tenant — this is the test/preview endpoint

### System Default Endpoints

6. `GET /api/conventions/defaults` lists all system default conventions
7. `GET /api/conventions/defaults/:key` returns a specific system default convention
8. `PUT /api/admin/conventions/:key` creates or updates a system default convention (platform admin only); returns 403 for non-admins
9. `DELETE /api/admin/conventions/:key` deletes a system default convention (platform admin only)
10. `POST /api/admin/conventions/:key/reset` resets a system default to the hardcoded original from `ConventionTemplates.cs` (platform admin only)

### Validation & Auth

11. All mutating endpoints validate request body: `name` required non-empty, `body` required non-empty, `category` in known set, `keywords` is a string array (stored in the normalized `convention_keywords` table — the API abstracts this), `matchMode` in `['any', 'all']`
12. All endpoints require authentication (JWT or session cookie)
13. Tenant context is extracted from the authenticated user's session (from Epic 16/17 middleware)
14. Error responses use consistent format: `{ error: string, code?: string }`
15. Existing `/api/convention-templates` routes remain for backward compatibility (read-only, static templates from `ConventionTemplates.cs`)

### Registry Endpoints (for UI pickers)

16. `GET /api/conventions/registry/categories` returns the list of valid category values
17. `GET /api/conventions/registry/actions` returns the list of known action names (for keyword suggestions)
18. `GET /api/conventions/registry/tools` returns the list of known tool names (for keyword suggestions)

## Technical Context

### Current API Routes

The existing convention endpoints in `apps/tamma-elsa/src/Tamma.Api/Endpoints/ConventionEndpoints.cs`:
- `GET /api/convention-templates` — list all static templates (key, name, description)
- `GET /api/convention-templates/:key` — get full template with conventions string

These are read-only endpoints serving static data from `ConventionTemplates.cs`. They remain unchanged for backward compatibility. The new endpoints serve the DB-backed convention store.

### Route Structure

```
-- Tenant-scoped (convention overrides + merged view)
GET    /api/conventions                        List resolved (merged)
GET    /api/conventions/:key                   Get resolved by key
PUT    /api/conventions/:key                   Create/update tenant override
DELETE /api/conventions/:key                   Delete tenant override

-- Resolution preview
POST   /api/conventions/resolve                Preview resolution with context

-- System defaults (platform admin)
GET    /api/conventions/defaults               List system defaults
GET    /api/conventions/defaults/:key          Get system default
PUT    /api/admin/conventions/:key             Create/update system default (admin)
DELETE /api/admin/conventions/:key             Delete system default (admin)
POST   /api/admin/conventions/:key/reset       Reset to hardcoded (admin)

-- Registry (for UI pickers)
GET    /api/conventions/registry/categories    Valid categories
GET    /api/conventions/registry/actions       Known action names
GET    /api/conventions/registry/tools         Known tool names

-- Legacy (unchanged)
GET    /api/convention-templates               Static template list
GET    /api/convention-templates/:key          Static template detail
```

### Route Parameter Conflict: "defaults" / "resolve" / "registry" vs ":key"

Same pattern as prompt store (Story 27-3): register `/api/conventions/defaults*`, `/api/conventions/resolve`, and `/api/conventions/registry*` routes before the parameterized `/api/conventions/:key` route.

### Authentication & Authorization

| Endpoint | Auth Required | Authorization |
|----------|--------------|---------------|
| `GET /api/conventions` | Yes | Any authenticated user |
| `GET /api/conventions/:key` | Yes | Any authenticated user |
| `PUT /api/conventions/:key` | Yes | Tenant admin or owner |
| `DELETE /api/conventions/:key` | Yes | Tenant admin or owner |
| `POST /api/conventions/resolve` | Yes | Any authenticated user |
| `GET /api/conventions/defaults` | Yes | Any authenticated user (read-only) |
| `GET /api/conventions/defaults/:key` | Yes | Any authenticated user (read-only) |
| `PUT /api/admin/conventions/:key` | Yes | Platform admin only |
| `DELETE /api/admin/conventions/:key` | Yes | Platform admin only |
| `POST /api/admin/conventions/:key/reset` | Yes | Platform admin only |
| `GET /api/conventions/registry/*` | Yes | Any authenticated user |
| `GET /api/convention-templates*` | No | Public (legacy) |

### Request/Response Formats

**PUT body (create/update):**

```json
{
  "name": "Security Review Standards",
  "description": "OWASP-based security review conventions",
  "category": "security",
  "body": "# Security Review Conventions\n\n## Input Validation\n...",
  "keywords": ["security", "password", "jwt", "owasp", "auth"],
  "matchMode": "any",
  "alwaysApply": false,
  "priority": 10,
  "enabled": true
}
```

> **Note on keyword storage**: The `keywords` array in the JSON body is a convenience representation. The service layer translates this to rows in the normalized `convention_keywords` table (see Story 27-8). On write, existing keyword rows are diffed and updated (delete removed, insert added). On read, keywords are joined back into a string array. The API consumer never interacts with the keywords table directly.

**GET response (single):**

```json
{
  "id": "uuid",
  "key": "security-review",
  "name": "Security Review Standards",
  "description": "OWASP-based security review conventions",
  "category": "security",
  "body": "# Security Review Conventions\n...",
  "keywords": ["security", "password", "jwt", "owasp", "auth"],
  "matchMode": "any",
  "alwaysApply": false,
  "priority": 10,
  "enabled": true,
  "version": 2,
  "isOverride": true,
  "updatedAt": "2026-05-04T12:00:00.000Z"
}
```

**POST /resolve response:**

```json
{
  "body": "# TypeScript Conventions\n...\n\n---\n\n# Security Review Conventions\n...",
  "triggered": [
    { "key": "typescript-react", "reason": "keyword:typescript", "source": "system" },
    { "key": "security-review", "reason": "keyword:auth", "source": "tenant" }
  ],
  "skipped": ["python", "go", "rust"],
  "totalChars": 4523,
  "estimatedTokens": 1130
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

The `/resolve` endpoint accepts the LLM call context, calls `IConventionStore.ResolveAsync()`, and returns the full resolution result. This is the same code path the Elsa workflow uses, exposed for admin testing.

### Step 3: Implement Registry Endpoints

Return static lists of valid values for use in UI pickers:
- Categories: `["coding", "security", "testing", "devops", "api", "docs"]`
- Actions: from the known actions enum (same list used in prompt store)
- Tools: from the MCP tool registry or a static list

### Step 4: Validation

Request body validation using the same patterns as prompt endpoints:
- `name`: required, non-empty string, max 200 chars
- `body`: required, non-empty string, max 50000 chars
- `category`: required, must be in valid set
- `keywords`: required array, each element non-empty string, max 50 elements
- `matchMode`: optional, defaults to "any", must be "any" or "all"
- `priority`: optional, defaults to 0, range 0-100

### Step 5: Key Generation

When creating a new convention via `PUT /api/conventions/:key`, the `key` is part of the URL path. It must be a valid slug: lowercase alphanumeric + hyphens, 3-60 characters. Validated server-side.

### Keyword Autocomplete Endpoint

The registry endpoints power the keyword editor's autocomplete. An additional source for autocomplete suggestions is the `convention_keywords` table itself:

```sql
SELECT DISTINCT keyword FROM convention_keywords ORDER BY keyword
```

This is a single B-tree index scan and provides real-time autocomplete from all existing keywords across all conventions.

## Implementation Notes

1. The `ConventionStoreEndpoints` are registered in a separate endpoint group from the legacy `ConventionEndpoints` to avoid route conflicts.
2. The `/resolve` endpoint is intentionally accessible to all authenticated users (not just admins) because it's useful for tenant admins testing their overrides.
3. The response format for `GET /api/conventions` includes `isOverride` to distinguish tenant overrides from system defaults in the UI.
4. Platform admin system default endpoints use `/api/admin/conventions/` prefix to match the admin route namespace pattern.
5. The legacy `/api/convention-templates` endpoints remain unchanged and are NOT deprecated yet — they serve the `ConventionSelector` UI component until the new convention store UI replaces it.
6. **Keyword write path**: When the PUT body includes a `keywords` array, `PgConventionStore.UpsertAsync` diffs the current keyword rows against the new array and applies `INSERT`/`DELETE` on `convention_keywords` within the same transaction. This is transparent to the API layer — the service returns the convention with keywords populated from the table.

## Testing Strategy

### Unit Tests

1. `GET /api/conventions` returns merged view with correct `isOverride` flags
2. `GET /api/conventions/:key` returns tenant override when exists
3. `GET /api/conventions/:key` falls back to system default when no override
4. `PUT /api/conventions/:key` creates tenant override
5. `PUT /api/conventions/:key` returns 400 for invalid body (missing name, bad category)
6. `PUT /api/conventions/:key` returns 403 for non-admin tenant members
7. `DELETE /api/conventions/:key` removes override, returns 204
8. `POST /api/conventions/resolve` returns triggered + skipped conventions
9. `POST /api/conventions/resolve` returns empty body when nothing matches
10. `GET /api/conventions/defaults` lists system defaults
11. `PUT /api/admin/conventions/:key` returns 403 for non-platform-admins
12. `PUT /api/admin/conventions/:key` creates system default for platform admin
13. `POST /api/admin/conventions/:key/reset` restores hardcoded default
14. `GET /api/conventions/registry/categories` returns valid category list
15. Key validation rejects invalid slugs

### Integration Tests

16. Full CRUD lifecycle: create override → list (appears with isOverride) → delete → list (falls back)
17. Resolve end-to-end: create conventions with keywords → call resolve with matching context → verify correct body
18. Legacy `/api/convention-templates` still works alongside new endpoints

## Dependencies

- **Story 27-9** (Convention Store Service) — `IConventionStore` must exist
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
