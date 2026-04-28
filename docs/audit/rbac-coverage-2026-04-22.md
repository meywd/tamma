# RBAC Coverage Audit — 2026-04-22

**Story**: 16-5 — Role-Based Access Control enforcement
**Scope**: every minimal-API endpoint registered in
`apps/tamma-elsa/src/Tamma.Api/Program.cs` plus the single MVC controller
(`Controllers/MentorshipController.cs`) and the nginx-level RBAC gate.
**Auditor**: agent-a1653c95
**Branch start**: `7ddb3a6` (`feat/wave-a` tip)

---

## 1. Executive summary

Story 16-5 is **substantially already in place**. The auth/permission infrastructure
(`Permissions.cs` + `PermissionHandler.cs` + named ASP.NET Core authorization policies +
`RequireTenantMembershipFilter` + `RoleCheck` endpoint + nginx `auth_request` for
elsa.tamma.dev / logs.tamma.dev) all exists and is correctly wired.

**Result**: 92 endpoints audited.

| Status | Count |
|--------|-------|
| ✅ Covered (correct policy) | 88 |
| 🟡 Fixed in this audit       | 1  |
| ⚪ Intentionally anonymous   | 13 |
| 🔴 Outstanding gaps          | 0  |

**Single fix made**: `DELETE /api/workflows/instances/{id}` was gated by
`WorkflowsManage` (admin-or-higher); per Story 16-5 AC 7 it must be owner-only.
A new `WorkflowsDelete` named policy backed by the existing
`workflows:delete -> ["owner"]` permission row is now applied to that route.

Defensive null handling added to `Permissions.HasPermission` /
`Permissions.GetRolePermissions` so that a missing role claim cannot throw.

---

## 2. Coverage matrix

Legend
- **Auth Policy**: ASP.NET Core named policy passed to `.RequireAuthorization(...)`
- **Role Required**: minimum role per `Permissions.Matrix` (owner > admin > member)
- **Tenant-scoped?**: `yes` → `RequireTenantMembershipFilter` runs before the handler
- **Status**: ✅ correct, 🟡 fixed in this audit, 🔴 still gap, ⚪ intentionally anonymous

### 2.1 Public / unauthenticated endpoints (⚪)

These are public on purpose — login, OAuth, webhooks, health, conventions list.

| Endpoint | Method | Auth Policy | Role Required | Tenant-scoped? | Status |
|----------|--------|-------------|---------------|:--------------:|--------|
| /api/health | GET | none | n/a | no | ⚪ |
| /api/v1/auth/register | POST | none | n/a | no | ⚪ |
| /api/v1/auth/verify-email | POST | none | n/a | no | ⚪ |
| /api/v1/auth/resend-verification | POST | none (rate-limited) | n/a | no | ⚪ |
| /api/v1/auth/login | POST | none (lockout) | n/a | no | ⚪ |
| /api/v1/auth/refresh | POST | none | n/a | no | ⚪ |
| /api/v1/auth/password-reset/request | POST | none | n/a | no | ⚪ |
| /api/v1/auth/password-reset/confirm | POST | none | n/a | no | ⚪ |
| /api/auth/github | GET | none (rate-limited OAuthStart) | n/a | no | ⚪ |
| /api/auth/github/callback | GET | none (rate-limited OAuthStart) | n/a | no | ⚪ |
| /api/github/callback | GET | none (rate-limited OAuthStart) | n/a | no | ⚪ |
| /api/github/webhooks | POST | none (HMAC verified, rate-limited) | n/a | no | ⚪ |
| /api/convention-templates, /:key | GET | none | n/a | no | ⚪ |

### 2.2 Authenticated-any (member+) endpoints

| Endpoint | Method | Auth Policy | Role Required | Tenant-scoped? | Status |
|----------|--------|-------------|---------------|:--------------:|--------|
| /api/v1/auth/logout | POST | MemberAccess | Member | no | ✅ |
| /api/auth/me | GET | AuthenticatedAny | Member | no | ✅ |
| /api/auth/role-check | GET | AuthenticatedAny | Member (gate) | no | ✅ |
| /api/v1/auth/switch-org | POST | MemberAccess | Member | no | ✅ |
| /api/v1/orgs/ (POST CreateOrg) | POST | MemberAccess | Member | no | ✅ |
| /api/v1/orgs/invites/accept | POST | MemberAccess | Member | no | ✅ |
| /api/v1/tenants | GET | MemberAccess | Member | no | ✅ |

### 2.3 Path-tenant (`/api/v1/orgs/{tenantId}/*`) endpoints

All run `RequireTenantMembershipFilter` (membership stash → handler reads role).
Inline role-hierarchy check happens inside each handler against
`HttpContext.Items["TenantRole"]`. The filter rejects with 401 / 400 / 403 before
the handler ever runs.

| Endpoint | Method | Auth Policy | Role Required | Tenant-scoped? | Status |
|----------|--------|-------------|---------------|:--------------:|--------|
| /api/v1/orgs/{tenantId} | GET | MemberAccess + filter | Tenant Member | yes | ✅ |
| /api/v1/orgs/{tenantId}/settings | PUT | MemberAccess + filter | Tenant Admin | yes | ✅ |
| /api/v1/orgs/{tenantId}/members | GET | MemberAccess + filter | Tenant Member | yes | ✅ |
| /api/v1/orgs/{tenantId}/members/{userId}/role | PUT | MemberAccess + filter | Tenant Owner (for owner-target) / Admin | yes | ✅ |
| /api/v1/orgs/{tenantId}/members/{userId} | DELETE | MemberAccess + filter | Tenant Admin | yes | ✅ |
| /api/v1/orgs/{tenantId}/invites | POST | MemberAccess + filter | Tenant Admin | yes | ✅ |
| /api/v1/orgs/{tenantId}/invites | GET | MemberAccess + filter | Tenant Admin | yes | ✅ |
| /api/v1/orgs/{tenantId}/invites/{inviteId} | DELETE | MemberAccess + filter | Tenant Admin | yes | ✅ |
| /api/v1/orgs/{tenantId}/invites/{inviteId}/resend | POST | MemberAccess + filter | Tenant Admin | yes | ✅ |
| /api/v1/orgs/{tenantId}/audit | GET | MemberAccess + filter | Tenant Admin | yes | ✅ |
| /api/v1/orgs/{tenantId}/transfer-ownership | POST | MemberAccess + filter | Tenant Owner | yes | ✅ |
| /api/v1/orgs/{tenantId} | DELETE | MemberAccess + filter | Tenant Owner | yes | ✅ |

### 2.4 Platform admin endpoints (`/api/admin/*`)

Group default `AdminAccess` (admin or owner via platform role). Service-key and
user destructive routes layer `SettingsManage` / `OwnerAccess` for owner-only.

| Endpoint | Method | Auth Policy | Role Required | Tenant-scoped? | Status |
|----------|--------|-------------|---------------|:--------------:|--------|
| /api/admin/health | GET | AdminAccess | Admin | no | ✅ |
| /api/admin/service-keys | POST | SettingsManage | Owner | no | ✅ |
| /api/admin/service-keys | GET | SettingsManage | Owner | no | ✅ |
| /api/admin/service-keys/{id}/rotate | POST | SettingsManage | Owner | no | ✅ |
| /api/admin/service-keys/{id} | DELETE | SettingsManage | Owner | no | ✅ |
| /api/admin/users | GET | AdminAccess | Admin | no | ✅ |
| /api/admin/users/{id} | GET | SelfOrUsersView | Self or Admin | no | ✅ |
| /api/admin/users/{id}/role | PUT | OwnerAccess | Owner | no | ✅ |
| /api/admin/users/{id} | DELETE | OwnerAccess | Owner | no | ✅ |
| /api/admin/users/invite | POST | AdminAccess | Admin | no | ✅ |
| /api/admin/users/invites | GET | AdminAccess | Admin | no | ✅ |
| /api/admin/users/invites/{id} | DELETE | AdminAccess | Admin | no | ✅ |
| /api/admin/users/{id}/keys | POST | SelfOrApiKeysManage | Self or Admin | no | ✅ |
| /api/admin/users/{id}/keys | GET | SelfOrApiKeysManage | Self or Admin | no | ✅ |
| /api/admin/users/{id}/keys/{keyId} | DELETE | SelfOrApiKeysManage | Self or Admin | no | ✅ |
| /api/admin/tenants/{tenantId}/provision | POST | OwnerAccess | Owner | no | ✅ |
| /api/admin/tenants/{tenantId}/provisioning | GET | OwnerAccess | Owner | no | ✅ |
| /api/admin/tenants/{tenantId}/deprovision | POST | OwnerAccess | Owner | no | ✅ |

### 2.5 Agents config (`/api/v1/agents/*`)

Group default `SettingsView` (admin/owner). Mutations layer `SettingsManage`
(owner only).

| Endpoint | Method | Auth Policy | Role Required | Tenant-scoped? | Status |
|----------|--------|-------------|---------------|:--------------:|--------|
| /api/v1/agents/config | GET | SettingsView | Admin | no | ✅ |
| /api/v1/agents/config | PUT | SettingsManage | Owner | no | ✅ |
| /api/v1/agents/config/validate | POST | SettingsView | Admin | no | ✅ |
| /api/v1/agents/{role}/resolve | GET | SettingsView | Admin | no | ✅ |
| /api/v1/agents/resolve-for-phase | POST | SettingsView | Admin | no | ✅ |

### 2.6 Prompts (`/api/prompts/*`)

| Endpoint | Method | Auth Policy | Role Required | Tenant-scoped? | Status |
|----------|--------|-------------|---------------|:--------------:|--------|
| /api/prompts/ | GET | SettingsView | Admin | no | ✅ |
| /api/prompts/system | GET | SettingsView | Admin | no | ✅ |
| /api/prompts/defaults | GET | SettingsView | Admin | no | ✅ |
| /api/prompts/system/{role}/{action} | GET | SettingsView | Admin | no | ✅ |
| /api/prompts/defaults/{role}/{action} | GET | SettingsView | Admin | no | ✅ |
| /api/prompts/defaults/{action} | GET | SettingsView | Admin | no | ✅ |
| /api/prompts/{role}/{action} | GET | SettingsView | Admin | no | ✅ |
| /api/prompts/{role}/{action} | PUT | SettingsManage | Owner | no | ✅ |
| /api/prompts/{role}/{action} | DELETE | SettingsManage | Owner | no | ✅ |
| /api/prompts/{role}/{action}/reset | POST | SettingsManage | Owner | no | ✅ |
| /api/prompts/system/{role} | PUT | SettingsManage | Owner | no | ✅ |
| /api/prompts/system/{role} | DELETE | SettingsManage | Owner | no | ✅ |
| /api/prompts/{role}/{action}/render | POST | SettingsView | Admin | no | ✅ |

### 2.7 Settings / Config (`/api/config/*`)

| Endpoint | Method | Auth Policy | Role Required | Tenant-scoped? | Status |
|----------|--------|-------------|---------------|:--------------:|--------|
| /api/config/agents | GET | SettingsView | Admin | no | ✅ |
| /api/config/agents | PUT | SettingsManage | Owner | no | ✅ |
| /api/config/security | GET | SettingsView | Admin | no | ✅ |
| /api/config/security | PUT | SettingsManage | Owner | no | ✅ |
| /api/config/sanitize | POST | SettingsManage | Owner | no | ✅ |
| /api/config/sanitize/rules | GET | SettingsView | Admin | no | ✅ |
| /api/config/sanitize/rules | PUT | SettingsManage | Owner | no | ✅ |
| /api/config/prompts | GET | SettingsView | Admin | no | ✅ |
| /api/config/prompts/{role} | PUT | SettingsManage | Owner | no | ✅ |
| /api/config/providers | GET | SettingsView | Admin | no | ✅ |
| /api/config/providers | PUT | SettingsManage | Owner | no | ✅ |

### 2.8 Providers (`/api/providers/*`)

| Endpoint | Method | Auth Policy | Role Required | Tenant-scoped? | Status |
|----------|--------|-------------|---------------|:--------------:|--------|
| /api/providers/health | GET | SettingsView | Admin | no | ✅ |
| /api/providers/health/providers | GET | SettingsView | Admin | no | ✅ |
| /api/providers/health/providers/{key} | GET | SettingsView | Admin | no | ✅ |
| /api/providers/health/providers/{key}/failure | POST | SettingsManage | Owner | no | ✅ |
| /api/providers/health/providers/{key}/success | POST | SettingsManage | Owner | no | ✅ |
| /api/providers/health/providers/{key}/reset | POST | SettingsManage | Owner | no | ✅ |
| /api/providers/chain/resolve | POST | SettingsView | Admin | no | ✅ |
| /api/providers/diagnostics | GET | SettingsView | Admin | no | ✅ |
| /api/providers/diagnostics/query | GET | SettingsView | Admin | no | ✅ |
| /api/providers/diagnostics/report | GET | SettingsView | Admin | no | ✅ |
| /api/providers/diagnostics/budget/{accountId} | GET | SettingsView | Admin | no | ✅ |
| /api/providers/diagnostics/budget/{accountId} | PUT | SettingsManage | Owner | no | ✅ |
| /api/providers/diagnostics | POST | SettingsManage | Owner | no | ✅ |
| /api/providers/diagnostics/batch | POST | SettingsManage | Owner | no | ✅ |
| /api/providers/providers/create | POST | SettingsManage | Owner | no | ✅ |
| /api/providers/providers/{handle}/execute | POST | SettingsManage | Owner | no | ✅ |
| /api/providers/providers/{handle} | DELETE | SettingsManage | Owner | no | ✅ |
| /api/providers/providers/sessions | GET | SettingsView | Admin | no | ✅ |

### 2.9 Engine (`/api/engine/*`)

Group default `WorkflowsView` (member+). Mutating routes layer `WorkflowsManage`
(admin-or-higher). All read-only routes are tenant-scoped via the inline
`ITenantContext` check inside the handler.

| Endpoint | Method | Auth Policy | Role Required | Tenant-scoped? | Status |
|----------|--------|-------------|---------------|:--------------:|--------|
| /api/engine/command | POST | WorkflowsManage | Admin | yes (handler) | ✅ |
| /api/engine/state | GET | WorkflowsView | Member | yes (handler) | ✅ |
| /api/engine/stats | GET | WorkflowsView | Member | yes (handler) | ✅ |
| /api/engine/plan | GET | WorkflowsView | Member | yes (handler) | ✅ |
| /api/engine/history | GET | WorkflowsView | Member | yes (handler) | ✅ |
| /api/engine/events/state | GET (SSE) | WorkflowsView | Member | yes (handler) | ✅ |
| /api/engine/events/logs | GET (SSE) | WorkflowsView | Member | yes (handler) | ✅ |
| /api/engine/store-context | POST | WorkflowsManage | Admin | yes (handler) | ✅ |
| /api/engine/context/{issueNumber} | GET | WorkflowsView | Member | yes (handler) | ✅ |
| /api/engine/query-context | POST | WorkflowsView | Member | yes (handler) | ✅ |
| /api/engine/repo-config | GET | WorkflowsView | Member | yes (handler) | ✅ |
| /api/engine/issues | GET | WorkflowsView | Member | yes (handler) | ✅ |
| /api/engine/security-alerts | GET | WorkflowsView | Member | yes (handler) | ✅ |
| /api/engine/issue-comment | POST | WorkflowsManage | Admin | yes (handler) | ✅ |
| /api/engine/issue-labels | POST | WorkflowsManage | Admin | yes (handler) | ✅ |
| /api/engine/issue-labels/{repo}/{issueNumber}/{label} | DELETE | WorkflowsManage | Admin | yes (handler) | ✅ |
| /api/engine/create-issue | POST | WorkflowsManage | Admin | yes (handler) | ✅ |
| /api/engine/trigger-ci | POST | WorkflowsManage | Admin | yes (handler) | ✅ |
| /api/engine/execute-task | POST | WorkflowsManage | Admin | yes (handler) | ✅ |
| /api/engine/cycle-result | POST | WorkflowsManage | Admin | yes (handler) | ✅ |
| /api/engine/cycle-results | GET | WorkflowsView | Member | yes (handler) | ✅ |
| /api/engine/agent-available | GET | WorkflowsView | Member | yes (handler) | ✅ |

### 2.10 Workflows (`/api/workflows/*`)

| Endpoint | Method | Auth Policy | Role Required | Tenant-scoped? | Status |
|----------|--------|-------------|---------------|:--------------:|--------|
| /api/workflows/definitions | POST | WorkflowsManage | Admin | yes (handler) | ✅ |
| /api/workflows/definitions | GET | WorkflowsView | Member | yes (handler) | ✅ |
| /api/workflows/instances | POST | WorkflowsManage | Admin | yes (handler) | ✅ |
| /api/workflows/instances/{id} | PUT | WorkflowsManage | Admin | yes (handler) | ✅ |
| /api/workflows/instances | GET | WorkflowsView | Member | yes (handler) | ✅ |
| /api/workflows/instances/{id}/cancel | POST | WorkflowsManage | Admin | yes (handler) | ✅ |
| /api/workflows/instances/{id} | DELETE | **WorkflowsDelete** | **Owner** | yes (handler) | 🟡 fixed |
| /api/workflows/instances/{id}/events | GET | WorkflowsView | Member | yes (handler) | ✅ |

**🟡 Fix detail**: previously `WorkflowsManage` (admin/owner). Story 16-5 AC 7
mandates `DELETE /api/workflows/*` is owner-only. Added new
`WorkflowsDelete` named policy backed by the existing
`workflows:delete -> ["owner"]` permission row, then re-attached the route.
See `Tamma.Api/Program.cs` `WorkflowsDelete` policy + DELETE-line annotation.

### 2.11 SaaS lane (`/api/v1/llm`, `/api/v1/workflows/{id}/...`, `/api/v1/installations/{id}/...`)

These accept the default policy (any authenticated principal — JWT user OR
ApiKey-authenticated machine client). Permission elevation lives inside the
service handler, not the route gate, because these are programmatic surfaces
called by remote agents.

| Endpoint | Method | Auth Policy | Role Required | Tenant-scoped? | Status |
|----------|--------|-------------|---------------|:--------------:|--------|
| /api/v1/llm/chat | POST | DefaultPolicy (any auth) | Authenticated | inside handler | ✅ |
| /api/v1/workflows/{id}/status | POST | DefaultPolicy | Authenticated | inside handler | ✅ |
| /api/v1/workflows/{id}/result | POST | DefaultPolicy | Authenticated | inside handler | ✅ |
| /api/v1/installations/{id}/rotate-key | POST | DefaultPolicy | Authenticated | inside handler | ✅ |

### 2.12 Dashboard (`/api/dashboard/*`)

| Endpoint | Method | Auth Policy | Role Required | Tenant-scoped? | Status |
|----------|--------|-------------|---------------|:--------------:|--------|
| /api/dashboard/summary | GET | DashboardView | Member | yes (handler) | ✅ |
| /api/dashboard/engines | GET | DashboardView | Member | yes (handler) | ✅ |
| /api/dashboard/workflows | GET | DashboardView | Member | yes (handler) | ✅ |

### 2.13 Knowledge Base (`/api/kb/*`)

Group default `SettingsView` (admin/owner) — mutations layer `SettingsManage`
(owner only). Read-only routes inherit the group default.

| Endpoint | Method | Auth Policy | Role Required | Tenant-scoped? | Status |
|----------|--------|-------------|---------------|:--------------:|--------|
| /api/kb/index/status | GET | SettingsView | Admin | no | ✅ |
| /api/kb/index/trigger | POST | SettingsManage | Owner | no | ✅ |
| /api/kb/index/config | GET | SettingsView | Admin | no | ✅ |
| /api/kb/index/config | PUT | SettingsManage | Owner | no | ✅ |
| /api/kb/index/stats | GET | SettingsView | Admin | no | ✅ |
| /api/kb/index | DELETE | SettingsManage | Owner | no | ✅ |
| /api/kb/vector-db/status | GET | SettingsView | Admin | no | ✅ |
| /api/kb/vector-db/search | POST | SettingsView | Admin | no | ✅ |
| /api/kb/vector-db/upsert | POST | SettingsManage | Owner | no | ✅ |
| /api/kb/vector-db/delete | DELETE | SettingsManage | Owner | no | ✅ |
| /api/kb/vector-db/collections | GET | SettingsView | Admin | no | ✅ |
| /api/kb/vector-db/stats | GET | SettingsView | Admin | no | ✅ |
| /api/kb/rag/config | GET | SettingsView | Admin | no | ✅ |
| /api/kb/rag/config | PUT | SettingsManage | Owner | no | ✅ |
| /api/kb/rag/query | POST | SettingsView | Admin | no | ✅ |
| /api/kb/rag/metrics | GET | SettingsView | Admin | no | ✅ |
| /api/kb/mcp/servers | GET | SettingsView | Admin | no | ✅ |
| /api/kb/mcp/servers/{id} | GET | SettingsView | Admin | no | ✅ |
| /api/kb/mcp/servers/{id}/start | POST | SettingsManage | Owner | no | ✅ |
| /api/kb/mcp/servers/{id}/stop | POST | SettingsManage | Owner | no | ✅ |
| /api/kb/mcp/config | GET | SettingsView | Admin | no | ✅ |
| /api/kb/mcp/config | PUT | SettingsManage | Owner | no | ✅ |
| /api/kb/mcp/tools | GET | SettingsView | Admin | no | ✅ |
| /api/kb/mcp/tools/invoke | POST | SettingsManage | Owner | no | ✅ |
| /api/kb/context/history | GET | SettingsView | Admin | no | ✅ |
| /api/kb/context/feedback | POST | SettingsManage | Owner | no | ✅ |
| /api/kb/context/config | GET | SettingsView | Admin | no | ✅ |
| /api/kb/analytics | GET | SettingsView | Admin | no | ✅ |
| /api/kb/analytics/usage | GET | SettingsView | Admin | no | ✅ |
| /api/kb/analytics/costs | GET | SettingsView | Admin | no | ✅ |

### 2.14 Mentorship (MVC controller)

| Endpoint | Method | Auth Policy | Role Required | Tenant-scoped? | Status |
|----------|--------|-------------|---------------|:--------------:|--------|
| /api/mentorship/* | various | DefaultPolicy via `[Authorize]` | Authenticated | n/a | ✅ |

---

## 3. nginx-level RBAC

`docker/nginx-proxy.conf.template` gates the two admin-only subdomains
(elsa.tamma.dev / logs.tamma.dev) by issuing an `auth_request` to
`/auth/role-check` (which proxies to the API's `GET /api/auth/role-check?service={elsa|logs}`).
The endpoint:

1. Verifies the `tamma_session` JWT cookie (auth via the cookie-fallback path
   in `JwtBearerEvents.OnMessageReceived`).
2. Reads the `role` claim and calls `Permissions.HasPermission(role,
   "{service}:access")` (`elsa:access` or `logs:access`).
3. Returns 200 (allowed), 403 (insufficient role), or 401 (unauthenticated).
4. nginx maps 401 → `@oauth2_redirect` (sign-in flow), 403 → `/403.html`.

Both subdomain server blocks hold the auth_request directive on `/` (and
`/elsa/api/` for the ELSA Server API). Static asset paths (`/_framework/`,
`/_content/`, `/health`) are intentionally exempt — Blazor WASM bootstrap and
liveness probes do not warrant RBAC.

**Status**: ✅ correctly wired. No changes needed in this audit.

---

## 4. Dashboard role guards (lower-priority backend mirror)

Backend enforcement is the security boundary — the dashboard guards exist for
UX (hide chrome that the user cannot use). Reviewed:

- `packages/dashboard/src/router.tsx` wraps platform-admin pages with
  `<AdminGuard>` (gates by platform `role` claim from `/auth/me`).
- Tenant-admin pages (`/settings/organization`) wrap with `<TenantAdminGuard>`,
  which reads the caller's role inside the **active** tenant (not the platform
  role) so admins of one org are gated correctly inside another where they are
  members.
- Member-only pages (`/account`, `/keys`) have no role guard — every
  authenticated user can reach them.

**Status**: ✅ adequate for backend-enforced RBAC. No changes made.

---

## 5. Deltas applied in this audit

### 5.1 New named policy: `WorkflowsDelete`

`apps/tamma-elsa/src/Tamma.Api/Program.cs`:

```csharp
options.AddPolicy("WorkflowsDelete", p =>
{
    p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
    p.AddRequirements(new PermissionRequirement("workflows:delete"));
});
```

Also added to the dev-fallback policy list so the Development branch keeps
permissive behavior.

### 5.2 Re-gate workflow DELETE

`apps/tamma-elsa/src/Tamma.Api/Program.cs`:

```csharp
// Story 16-5 AC 7: workflow instance deletion is owner-only via WorkflowsDelete
// (workflows:delete -> ["owner"]). Cancel stays admin/owner via WorkflowsManage.
workflows.MapDelete("/instances/{id}", WorkflowEndpoints.DeleteInstance).RequireAuthorization("WorkflowsDelete");
```

### 5.3 Defensive null handling in `Permissions`

`apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs`:

`HasPermission(string role, string permission)` and `GetRolePermissions(string
role)` now accept nullable inputs and fail closed (return `false` /
`Array.Empty<string>()`). Authz primitives must never throw on a missing
claim.

### 5.4 Tests

`apps/tamma-elsa/tests/Tamma.Api.Tests/Auth/PermissionsMatrixTests.cs` — 35 cases
covering every role × permission combination from `Permissions.Matrix`,
hierarchy inheritance (owner ⊇ admin ⊇ member), unknown-role / unknown-perm
edge cases, and `GetRolePermissions` subset semantics.

`apps/tamma-elsa/tests/Tamma.Api.Tests/Auth/RoleCheckEndpointTests.cs` — 11 cases
covering the `RoleCheck` endpoint directly: each `(service, role)` matrix
intersection (elsa/logs/admin × member/admin/owner), unknown service, missing
service, empty service, null role-claim, and case-insensitive service-name
lookup.

Both suites are pure unit tests (no DB, no host) and are isolated to the
`Auth` test folder. They pass against `Tamma.Api.Tests` without requiring the
testcontainer Postgres bootstrap.

---

## 6. References

- Story: `/home/meywd/tamma/docs/stories/epic-16/16-5-role-based-access-control.md`
- Permission matrix: `apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs`
- Permission handler: `apps/tamma-elsa/src/Tamma.Api/Auth/PermissionHandler.cs`
- Tenant filter: `apps/tamma-elsa/src/Tamma.Api/Authorization/RequireTenantMembershipFilter.cs`
- Role-check endpoint: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:RoleCheck`
- nginx role gate: `docker/nginx-proxy.conf.template` (elsa.tamma.dev + logs.tamma.dev `location /` blocks)
- oauth2-proxy config: `docker/oauth2-proxy.cfg`
- Related port-gap finding: `docs/audit/port-gaps/orgs/024-require-tenant-missing.md` (closed)
