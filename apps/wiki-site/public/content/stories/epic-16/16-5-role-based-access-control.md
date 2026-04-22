---
title: "Story 16.5: Role-Based Access Control Enforcement"
sidebar:
  order: 160
---

Status: ready-for-dev

## Story

As a **platform owner**,
I want the three-tier role system (member, admin, owner) enforced across all API endpoints, dashboard pages, and proxied services,
so that users can only access the resources and actions their role permits, and sensitive tools like ELSA Studio and OpenSearch Dashboards are restricted to administrators.

## Acceptance Criteria

1. **member** role: can view the Tamma Dashboard, view own workflow runs, view own API keys, manage own settings
2. **admin** role: all member permissions + manage users (except promoting to owner), view all workflow runs, access ELSA Studio (elsa.tamma.dev), access OpenSearch Dashboards (logs.tamma.dev), access admin panel
3. **owner** role: all admin permissions + manage installations, promote/demote admins, delete users, delete data, system configuration
4. Tamma API enforces role checks on every protected endpoint via middleware (from Story 16.2's `requireRole` middleware)
5. `GET /api/workflows` returns only the user's own workflow runs for `member` role, all runs for `admin`/`owner`
6. `POST /api/workflows/*/cancel` is restricted to `admin`/`owner`
7. `DELETE /api/workflows/*` is restricted to `owner`
8. elsa.tamma.dev is accessible only to `admin` and `owner` users — `member` users see a "403 Forbidden" page
9. logs.tamma.dev is accessible only to `admin` and `owner` users — `member` users see a "403 Forbidden" page
10. Role enforcement at nginx level uses the `X-Auth-Request-Groups` header from oauth2-proxy, populated from a Tamma API endpoint that maps GitHub user to Tamma role
11. A custom 403 error page explains why access was denied and links back to app.tamma.dev
12. Role changes take effect within 1 hour (oauth2-proxy session refresh) without requiring the user to re-login
13. API responses include the user's role in the JWT claims so the dashboard can make client-side rendering decisions
14. All authorization failures are logged with the user ID, requested resource, and required role

## Technical Context

### Current Role State

The `users` table has a `role` column with CHECK constraint `('owner', 'admin', 'member')`. The GitHub OAuth callback sets `role: 'member'` by default. The JWT token includes `role` in its claims. However, no endpoint currently checks the role for authorization decisions.

### Role Enforcement Layers

RBAC must be enforced at multiple levels:

1. **API middleware layer** (Fastify hooks) — primary enforcement for all API calls
2. **nginx proxy layer** (auth_request + role header check) — gates access to entire subdomains
3. **Dashboard UI layer** (React route guards) — UX-level enforcement (not a security boundary)

### Enforcement Architecture

```
Browser Request
    |
    v
nginx-proxy
    |
    +-- auth_request --> oauth2-proxy (checks cookie)
    |                        |
    |                  X-Auth-Request-User header set
    |
    +-- role_check --> Tamma API /api/auth/role-check?service=elsa
    |                        |
    |                  Returns 200 (allowed) or 403 (denied)
    |
    +-- proxy_pass --> upstream service
```

### Files to Create

| File | Purpose |
|------|---------|
| `packages/api/src/middleware/rbac.ts` | Comprehensive RBAC middleware with per-route permission definitions |
| `packages/api/src/routes/auth/role-check.ts` | Endpoint for nginx to check if user can access a service |
| `docker/error-pages/403.html` | Custom "Access Denied" page |
| `packages/api/src/rbac/permissions.ts` | Permission matrix: role -> resource -> action mappings |

### Files to Modify

| File | Change |
|------|--------|
| `docker/nginx-proxy.conf` | Add role-based access checks for elsa.tamma.dev and logs.tamma.dev |
| `packages/api/src/auth/index.ts` | Integrate RBAC middleware into the global auth hook |
| `packages/api/src/routes/auth/github-oauth.ts` | Ensure JWT claims include role for dashboard rendering |
| `packages/dashboard/src/App.tsx` (or router) | Add role-based route guards for dashboard pages |
| `packages/api/src/serve.ts` (or route registration) | Register role-check endpoint |

## Implementation Plan

### Step 1: Permission Matrix

Define a centralized permission matrix that maps roles to resources and actions:

```typescript
// packages/api/src/rbac/permissions.ts

export type Role = 'owner' | 'admin' | 'member';
export type Resource =
  | 'dashboard'
  | 'workflows'
  | 'workflow_runs'
  | 'users'
  | 'api_keys'
  | 'installations'
  | 'elsa_studio'
  | 'opensearch'
  | 'admin_panel'
  | 'system_config';

export type Action = 'view' | 'create' | 'update' | 'delete' | 'manage';

/** Permission entry: [resource, action, minimum role]. */
type PermissionRule = [Resource, Action, Role];

const PERMISSIONS: PermissionRule[] = [
  // Dashboard — everyone
  ['dashboard', 'view', 'member'],

  // Workflows — view own for member, view all for admin
  ['workflows', 'view', 'member'],         // Scoped to own in handler
  ['workflows', 'create', 'member'],
  ['workflows', 'update', 'admin'],         // Cancel, retry
  ['workflows', 'delete', 'owner'],

  // Workflow runs — view own for member, view all for admin
  ['workflow_runs', 'view', 'member'],      // Scoped to own in handler
  ['workflow_runs', 'manage', 'admin'],

  // Users — admin can view/manage, owner for destructive actions
  ['users', 'view', 'admin'],
  ['users', 'create', 'admin'],             // Invites
  ['users', 'update', 'admin'],             // Role changes (owner-only for admin promotion)
  ['users', 'delete', 'owner'],

  // API keys — self for member, any user for admin
  ['api_keys', 'view', 'member'],           // Scoped to own in handler
  ['api_keys', 'create', 'member'],         // Scoped to own in handler
  ['api_keys', 'delete', 'member'],         // Scoped to own in handler
  ['api_keys', 'manage', 'admin'],          // Any user's keys

  // Installations — owner only for destructive, admin for view
  ['installations', 'view', 'admin'],
  ['installations', 'manage', 'owner'],
  ['installations', 'delete', 'owner'],

  // External services — admin/owner only
  ['elsa_studio', 'view', 'admin'],
  ['opensearch', 'view', 'admin'],

  // Admin panel
  ['admin_panel', 'view', 'admin'],

  // System config
  ['system_config', 'view', 'admin'],
  ['system_config', 'manage', 'owner'],
];

const ROLE_HIERARCHY: Record<Role, number> = {
  member: 0,
  admin: 1,
  owner: 2,
};

/**
 * Check if a role has permission for a resource/action.
 */
export function hasPermission(role: Role, resource: Resource, action: Action): boolean {
  const rule = PERMISSIONS.find(([r, a]) => r === resource && a === action);
  if (!rule) return false;

  const [, , minimumRole] = rule;
  return ROLE_HIERARCHY[role] >= ROLE_HIERARCHY[minimumRole];
}

/**
 * Get all permissions for a role.
 */
export function getRolePermissions(role: Role): Array<{ resource: Resource; action: Action }> {
  return PERMISSIONS
    .filter(([, , minRole]) => ROLE_HIERARCHY[role] >= ROLE_HIERARCHY[minRole])
    .map(([resource, action]) => ({ resource, action }));
}
```

### Step 2: RBAC Middleware

```typescript
// packages/api/src/middleware/rbac.ts
import type { FastifyRequest, FastifyReply } from 'fastify';
import { hasPermission, type Resource, type Action, type Role } from '../rbac/permissions.js';

export interface RBACOptions {
  resource: Resource;
  action: Action;
}

/**
 * Fastify preHandler that checks the authenticated user's role
 * against the permission matrix.
 */
export function requirePermission(resource: Resource, action: Action) {
  return async (request: FastifyRequest, reply: FastifyReply): Promise<void> => {
    const user = (request as FastifyRequest & { authUser?: { id: string; role: string } }).authUser;

    if (!user) {
      request.log.warn({ resource, action }, 'RBAC denied: not authenticated');
      reply.status(401).send({ error: 'Not authenticated' });
      return;
    }

    const role = user.role as Role;

    if (!hasPermission(role, resource, action)) {
      request.log.warn({
        userId: user.id,
        userRole: role,
        resource,
        action,
        requiredPermission: `${resource}:${action}`,
      }, 'RBAC denied: insufficient permissions');

      reply.status(403).send({
        error: 'Forbidden',
        message: `Role '${role}' does not have '${action}' permission on '${resource}'`,
      });
      return;
    }
  };
}
```

### Step 3: Role Check Endpoint for nginx

nginx needs a way to check if the authenticated user (from oauth2-proxy) has permission to access a specific service. This is done via a subrequest to the Tamma API:

```typescript
// packages/api/src/routes/auth/role-check.ts
import type { FastifyInstance, FastifyRequest, FastifyReply } from 'fastify';
import type { IUserStore } from '../../persistence/user-store.js';
import { hasPermission, type Resource, type Role } from '../../rbac/permissions.js';

const SERVICE_RESOURCE_MAP: Record<string, Resource> = {
  elsa: 'elsa_studio',
  logs: 'opensearch',
  admin: 'admin_panel',
};

export async function registerRoleCheckRoute(
  app: FastifyInstance,
  userStore: IUserStore,
): Promise<void> {
  /**
   * GET /api/auth/role-check?service=elsa
   *
   * Called by nginx auth_request to determine if the oauth2-proxy-authenticated
   * user has permission to access the specified service.
   *
   * The oauth2-proxy sets X-Auth-Request-User header with the GitHub username.
   * We look up the user by GitHub login and check their role.
   *
   * Returns:
   *   200 — access granted
   *   403 — access denied (user exists but lacks permission)
   *   401 — user not found
   */
  app.get<{
    Querystring: { service?: string };
  }>('/api/auth/role-check', async (request: FastifyRequest<{ Querystring: { service?: string } }>, reply: FastifyReply) => {
    const service = request.query.service;
    const githubUsername = request.headers['x-auth-request-user'] as string | undefined;

    if (!service || !githubUsername) {
      return reply.status(401).send({ error: 'Missing service or user header' });
    }

    const resource = SERVICE_RESOURCE_MAP[service];
    if (!resource) {
      return reply.status(400).send({ error: `Unknown service: ${service}` });
    }

    // Look up user by GitHub login
    // Note: Need to add getUserByGithubLogin to IUserStore, or use email
    // For now, we can query by the X-Auth-Request-Email header
    const email = request.headers['x-auth-request-email'] as string | undefined;

    // Try multiple lookup strategies
    let user = null;

    // Strategy 1: Look up by GitHub login (requires adding this method)
    // user = await userStore.getUserByGithubLogin(githubUsername);

    // Strategy 2: The oauth2-proxy session was created after GitHub OAuth.
    // The tamma_session JWT cookie (if present) has the user ID.
    // Parse it to get the role.
    const tammaSession = request.cookies?.['tamma_session'];
    if (tammaSession) {
      try {
        const decoded = app.jwt.verify<{ id: string; role: string }>(tammaSession);
        const role = decoded.role as Role;

        if (hasPermission(role, resource, 'view')) {
          return reply.status(200).send({ allowed: true });
        } else {
          request.log.warn({
            userId: decoded.id,
            role,
            service,
            resource,
          }, 'Service access denied by RBAC');
          return reply.status(403).send({ error: 'Insufficient role' });
        }
      } catch {
        // JWT invalid or expired — fall through
      }
    }

    // No valid session — deny
    return reply.status(401).send({ error: 'Not authenticated' });
  });
}
```

### Step 4: nginx Role-Based Service Gating

For elsa.tamma.dev and logs.tamma.dev, add a second `auth_request` check after oauth2-proxy:

```nginx
# elsa.tamma.dev — ELSA Studio (admin/owner only)
server {
    listen 443 ssl;
    server_name elsa.tamma.dev;

    # oauth2-proxy auth
    location /oauth2/ { ... }
    location = /oauth2/auth { ... }

    # Role check subrequest
    location = /auth/role-check {
        internal;
        proxy_pass http://tamma-api:3100/api/auth/role-check?service=elsa;
        proxy_set_header Host $host;
        proxy_set_header X-Auth-Request-User $auth_user;
        proxy_set_header X-Auth-Request-Email $auth_email;
        proxy_set_header Cookie $http_cookie;
        proxy_pass_request_body off;
        proxy_set_header Content-Length "";
    }

    # Custom 403 page
    error_page 403 /error/403.html;
    location = /error/403.html {
        root /usr/share/nginx/html;
        internal;
    }

    # ELSA Studio — requires oauth2-proxy auth + role check
    location / {
        auth_request /oauth2/auth;
        auth_request_set $auth_user $upstream_http_x_auth_request_user;
        auth_request_set $auth_email $upstream_http_x_auth_request_email;
        error_page 401 = /oauth2/sign_in;

        # Second auth check: role-based
        auth_request /auth/role-check;
        error_page 403 = /error/403.html;

        proxy_pass http://elsa-studio:8080;
        # ... proxy headers ...
    }
}
```

**Note**: nginx only supports one `auth_request` per location. To chain two checks, use a cascading approach where the role-check endpoint itself verifies the oauth2-proxy cookie, or combine both checks into a single endpoint. The simplest approach is:

```nginx
    location / {
        # Single auth_request that checks both oauth2-proxy session AND role
        auth_request /auth/role-check;
        error_page 401 = /oauth2/sign_in;
        error_page 403 = /error/403.html;

        proxy_pass http://elsa-studio:8080;
        # ...
    }
```

Where `/auth/role-check` verifies both the `_oauth2_proxy` cookie (via the user headers set by a parent auth_request) and the Tamma role.

In practice, the cleanest approach is:

1. oauth2-proxy `auth_request` gates ALL dashboard server blocks (returns 401 if no session)
2. For admin-only services (elsa, logs), add a separate `auth_request` to a Tamma API endpoint that checks the `tamma_session` JWT role

Since nginx does not support two `auth_request` directives in one location, the role-check endpoint must handle both concerns, or use a nested location pattern.

**Recommended approach**: Use a single `auth_request` that first validates oauth2-proxy, then checks role. This is achieved by having the role-check endpoint read the `_oauth2_proxy` cookie to verify identity and the `tamma_session` cookie to verify role:

```nginx
    location = /auth/service-gate {
        internal;
        proxy_pass http://tamma-api:3100/api/auth/role-check?service=elsa;
        proxy_set_header Host $host;
        proxy_set_header Cookie $http_cookie;
        proxy_pass_request_body off;
        proxy_set_header Content-Length "";
    }

    location / {
        auth_request /auth/service-gate;
        error_page 401 = @login_redirect;
        error_page 403 = /error/403.html;

        proxy_pass http://elsa-studio:8080;
        # ...
    }

    location @login_redirect {
        return 302 https://app.tamma.dev/oauth2/sign_in?rd=$scheme://$host$request_uri;
    }
```

### Step 5: Custom 403 Page

```html
<!-- docker/error-pages/403.html -->
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Access Denied - Tamma</title>
  <style>
    * { margin: 0; padding: 0; box-sizing: border-box; }
    body {
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
      background: #0f0f1e;
      color: #e0e0e0;
      display: flex;
      align-items: center;
      justify-content: center;
      min-height: 100vh;
    }
    .container {
      text-align: center;
      max-width: 500px;
      padding: 40px;
    }
    .status { font-size: 72px; font-weight: 700; color: #7c3aed; }
    h1 { font-size: 24px; margin: 16px 0 8px; }
    p { color: #999; margin: 8px 0; line-height: 1.6; }
    a {
      display: inline-block;
      margin-top: 24px;
      padding: 12px 24px;
      background: #7c3aed;
      color: #fff;
      text-decoration: none;
      border-radius: 6px;
      transition: background 0.2s;
    }
    a:hover { background: #6d28d9; }
  </style>
</head>
<body>
  <div class="container">
    <div class="status">403</div>
    <h1>Access Denied</h1>
    <p>Your current role does not have permission to access this service.
       Contact your organization administrator to request access.</p>
    <a href="https://app.tamma.dev">Return to Dashboard</a>
  </div>
</body>
</html>
```

### Step 6: API-Level RBAC Integration

Apply `requirePermission` to all existing and new API routes:

```typescript
// Example: workflow routes
app.get('/api/workflows', {
  preHandler: [requirePermission('workflows', 'view')],
}, async (request, reply) => {
  const user = (request as any).authUser;

  // member: filter to own workflow runs
  // admin/owner: return all
  const filter = user.role === 'member'
    ? { userId: user.id }
    : {};

  const workflows = await workflowStore.list(filter);
  return reply.send({ workflows });
});

app.post('/api/workflows/:id/cancel', {
  preHandler: [requirePermission('workflows', 'update')],
}, async (request, reply) => {
  // Only admin/owner reach here
  // ...
});

app.delete('/api/workflows/:id', {
  preHandler: [requirePermission('workflows', 'delete')],
}, async (request, reply) => {
  // Only owner reaches here
  // ...
});
```

### Step 7: Dashboard UI Role Guards

```tsx
// In App.tsx or router configuration
<Route path="/admin" element={
  <RoleGuard minimumRole="admin" redirectTo="/">
    <AdminPage />
  </RoleGuard>
} />

// Generic role guard component
function RoleGuard({
  minimumRole,
  redirectTo,
  children,
}: {
  minimumRole: 'member' | 'admin' | 'owner';
  redirectTo: string;
  children: React.ReactNode;
}) {
  const { user } = useAuth();
  const hierarchy = { member: 0, admin: 1, owner: 2 };

  if (!user || hierarchy[user.role] < hierarchy[minimumRole]) {
    return <Navigate to={redirectTo} replace />;
  }

  return <>{children}</>;
}
```

### Step 8: Token Refresh for Role Changes

When an admin changes a user's role (Story 16.2), the user's JWT has the old role. The role change takes effect when:

1. The user's `tamma_session` JWT expires (24 hours by default) and they re-authenticate
2. The oauth2-proxy session refreshes (every 1 hour as configured in Story 16.1)

To make role changes take effect sooner, the API can:

- Check the database role on each request (adds a DB query per request)
- Or use a short-lived JWT (e.g., 15 minutes) with a refresh token

For now, the 1-hour oauth2-proxy refresh provides a reasonable balance. The API role-check endpoint always reads from the database, so nginx-level RBAC updates within the oauth2-proxy refresh window.

## Logging Requirements

| Event | Level | Output | Notes |
|-------|-------|--------|-------|
| RBAC check passed | DEBUG | Pino structured log | Include user ID, role, resource, action |
| RBAC check denied | WARN | Pino structured log | Include user ID, role, resource, action, endpoint |
| Service access denied (nginx) | WARN | Via role-check endpoint log | Include GitHub username, service, role |
| Service access granted (nginx) | DEBUG | Via role-check endpoint log | Include GitHub username, service |
| Permission matrix loaded | INFO | Pino structured log | On server startup, log number of rules |

### Sensitive Data Redaction

- Log user ID and role, not email or token values
- Log resource and action names, not request bodies

### Audit Events

```typescript
// Event type patterns:
// AUTH.ACCESS_DENIED.RBAC   — RBAC check failed at API level
// AUTH.SERVICE_DENIED.RBAC  — nginx role-check denied service access
```

## Testing Strategy

### Unit Tests

Create `packages/api/src/rbac/permissions.test.ts`:

1. `hasPermission('member', 'dashboard', 'view')` returns true
2. `hasPermission('member', 'users', 'view')` returns false
3. `hasPermission('admin', 'users', 'view')` returns true
4. `hasPermission('admin', 'users', 'delete')` returns false
5. `hasPermission('owner', 'users', 'delete')` returns true
6. `hasPermission('admin', 'elsa_studio', 'view')` returns true
7. `hasPermission('member', 'elsa_studio', 'view')` returns false
8. `getRolePermissions('member')` returns correct subset
9. `getRolePermissions('owner')` returns all permissions

Create `packages/api/src/middleware/rbac.test.ts`:

1. `requirePermission('workflows', 'view')` passes for member
2. `requirePermission('users', 'view')` blocks member with 403
3. `requirePermission` returns 401 for unauthenticated requests
4. Response body includes meaningful error message

Create `packages/api/src/routes/auth/role-check.test.ts`:

1. Returns 200 for admin accessing elsa
2. Returns 403 for member accessing elsa
3. Returns 401 for unauthenticated request
4. Returns 400 for unknown service

### Integration Tests

1. Full RBAC flow: create user as member -> attempt to access /api/users -> 403 -> promote to admin -> retry -> 200
2. nginx service gating: authenticate as member -> access elsa.tamma.dev -> 403 page shown
3. nginx service gating: authenticate as admin -> access elsa.tamma.dev -> ELSA Studio loads

### Manual Verification

1. Log in as `member` -> verify Dashboard loads, /admin redirects, elsa.tamma.dev shows 403
2. Log in as `admin` -> verify /admin loads, elsa.tamma.dev loads, logs.tamma.dev loads, cannot delete users
3. Log in as `owner` -> verify all actions work
4. Change a user's role -> verify new permissions take effect within 1 hour

## Dependencies

- **Story 16.1** (OAuth2 Proxy) — oauth2-proxy must be in place for nginx-level role checking
- **Story 16.2** (User Management API) — `requireRole` middleware created there, extended here into full RBAC
- Internal: `packages/api/src/auth/index.ts` (auth plugin), `packages/api/src/persistence/user-store.ts`

## Estimated Effort

| Task | Hours |
|------|-------|
| Permission matrix definition | 2 |
| RBAC middleware | 2 |
| Role-check API endpoint | 2 |
| nginx configuration for elsa + logs | 3 |
| Custom 403 page | 1 |
| API route RBAC annotations | 2 |
| Dashboard role guards | 1 |
| Unit tests | 2 |
| Integration testing | 1 |
| **Total** | **16 hours** |

## Cross-References

- **Unified RBAC Role Model**: See `/home/meywd/tamma/docs/stories/rbac-unified-model.md` for the canonical two-level role model (platform roles + tenant roles) that reconciles Epic 16 and Epic 18 role sets.

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation | Architecture Team |
| 2026-04-09 | 1.1 | Added cross-reference to unified RBAC role model | Cross-epic review |
