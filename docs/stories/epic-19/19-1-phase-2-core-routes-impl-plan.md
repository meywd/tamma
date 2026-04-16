# Epic 19 / Story 19-1 — Phase 2: Core Routes Implementation Plan

Status: planning

**Scope**: Port 43 endpoints from TypeScript Fastify to C# ASP.NET Core Minimal APIs.
**Prerequisite**: Phase 1 complete (EF Core DbContext, auth middleware, tenant middleware, repository layer).
**Estimated effort**: 48 hours.

---

## Table of Contents

1. [Route Group A — Health](#1-route-group-a--health)
2. [Route Group B — Admin Health](#2-route-group-b--admin-health)
3. [Route Group C — Admin Service Keys](#3-route-group-c--admin-service-keys)
4. [Route Group D — Admin User Management](#4-route-group-d--admin-user-management)
5. [Route Group E — Admin User API Keys](#5-route-group-e--admin-user-api-keys)
6. [Route Group F — Admin User Invites](#6-route-group-f--admin-user-invites)
7. [Route Group G — Auth Registration](#7-route-group-g--auth-registration)
8. [Route Group H — Auth Login/Refresh/Logout](#8-route-group-h--auth-loginrefreshlogout)
9. [Route Group I — Auth Password Reset](#9-route-group-i--auth-password-reset)
10. [Route Group J — Auth Identity (me, role-check)](#10-route-group-j--auth-identity-me-role-check)
11. [Route Group K — Auth GitHub OAuth](#11-route-group-k--auth-github-oauth)
12. [Route Group L — Organization/Tenant Routes](#12-route-group-l--organizationtenant-routes)
13. [nginx Routing Configuration](#13-nginx-routing-configuration)
14. [Rollback Instructions](#14-rollback-instructions)
15. [Implementation Order](#15-implementation-order)
16. [Phase 2 Success Checklist](#16-phase-2-success-checklist)

---

## 1. Route Group A — Health

### Source

- **TS path**: `packages/api/src/routes/index.ts` (inline health endpoint)
- **Endpoint**: `GET /api/health`

### C# Target

- **File**: `Tamma.Api/Endpoints/HealthEndpoints.cs`

### Implementation

```
MapGet("/api/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }))
    .AllowAnonymous()
    .WithTags("Health");
```

### Request / Response

| Field | Type | Notes |
|---|---|---|
| Request | none | No auth required |
| Response 200 | `{ status: "ok", timestamp: string }` | ISO 8601 |

### Auth Requirements

- None (anonymous access).

### Test Assertions

1. `GET /api/health` returns 200 with `status: "ok"`.
2. Response includes a valid ISO 8601 `timestamp`.
3. No auth header or cookie required.

---

## 2. Route Group B — Admin Health

### Source

- **TS path**: `packages/api/src/routes/admin/health-routes.ts`
- **Endpoint**: `GET /api/admin/health`

### C# Target

- **File**: `Tamma.Api/Endpoints/Admin/HealthEndpoints.cs`

### Implementation

Port the `checkHttpService` helper and parallel health check pattern. Services checked:
- Tamma API (self, always healthy)
- PostgreSQL (via `TammaDbContext.Database.CanConnectAsync()`)
- ELSA Server (`{ELSA_SERVER_URL}/health`)
- OpenSearch (`{OPENSEARCH_URL}/_cluster/health`)
- RabbitMQ Management (`{RABBITMQ_MANAGEMENT_URL}/api/health/checks/alarms` with Basic auth)
- ChromaDB (`{CHROMADB_URL}/api/v2/heartbeat`)

Use `HttpClient` via `IHttpClientFactory` (not raw `fetch`). Configure named clients in DI.

### Request / Response

| Field | Type | Notes |
|---|---|---|
| Request | JWT cookie | Requires admin or owner role |
| Response 200 | `{ services: ServiceCheck[], checkedAt: string }` | |
| ServiceCheck | `{ name, status, responseTime, checkedAt, details? }` | status: healthy/unhealthy/unknown |
| Response 401 | `{ error: "Not authenticated" }` | No valid JWT |
| Response 403 | `{ error: "Admin or owner role required" }` | Insufficient role |

### Auth Requirements

- JWT bearer or session cookie.
- Role: `admin` or `owner` (check via `ClaimsPrincipal`).

### Test Assertions

1. Returns 401 without JWT.
2. Returns 403 for `member` role.
3. Returns 200 with `services` array for `admin` role.
4. Returns 200 with `services` array for `owner` role.
5. PostgreSQL check returns `healthy` when `DbContext` is reachable.
6. External service check returns `unhealthy` when URL is unreachable.
7. Each service check includes `responseTime` in milliseconds.

---

## 3. Route Group C — Admin Service Keys

### Source

- **TS path**: `packages/api/src/routes/admin/service-keys.ts`
- **Endpoints**: 4 total

### C# Target

- **File**: `Tamma.Api/Endpoints/Admin/ServiceKeyEndpoints.cs`

### Endpoint Details

#### 3.1 POST /api/admin/service-keys

| Field | Type | Notes |
|---|---|---|
| Request body | `{ serviceName: string, label?: string, permissions?: string[] }` | |
| Response 201 | `{ id, serviceName, label, permissions, keyPrefix, createdAt, rawKey, warning }` | rawKey shown ONCE |
| Response 400 | `{ error: "serviceName is required" }` | |
| Auth | `requirePermission("settings:manage")` — owner only | |

#### 3.2 GET /api/admin/service-keys

| Field | Type | Notes |
|---|---|---|
| Request | none (beyond auth) | |
| Response 200 | `ServiceKey[]` (no rawKey) | Includes id, serviceName, label, permissions, keyPrefix, createdAt, lastUsedAt, revokedAt, rotatedFrom |
| Auth | `requirePermission("settings:manage")` | |

#### 3.3 POST /api/admin/service-keys/{id}/rotate

| Field | Type | Notes |
|---|---|---|
| Request params | `id` (path) | |
| Response 200 | `{ id, serviceName, label, permissions, keyPrefix, createdAt, rotatedFrom, rawKey, warning }` | New key; old valid 24h |
| Response 404 | `{ error: "Service key not found" }` | |
| Auth | `requirePermission("settings:manage")` | |

#### 3.4 DELETE /api/admin/service-keys/{id}

| Field | Type | Notes |
|---|---|---|
| Request params | `id` (path) | |
| Response 204 | (no body) | Immediate revocation |
| Response 404 | `{ error: "Service key not found" }` | |
| Auth | `requirePermission("settings:manage")` | |

### Auth Requirements

- All 4 endpoints require `settings:manage` permission (owner role).
- Implemented as ASP.NET Core authorization policy `RequireAuthorization("SettingsManage")`.

### Test Assertions

1. Create returns 201 with `rawKey` and valid prefix.
2. Create returns 400 when `serviceName` missing.
3. List returns all keys without `rawKey` field.
4. Rotate returns new key with `rotatedFrom` set to old key ID.
5. Rotate returns 404 for non-existent ID.
6. Delete returns 204 and subsequent list excludes revoked key.
7. Delete returns 404 for non-existent ID.
8. All endpoints return 403 for non-owner role.

---

## 4. Route Group D — Admin User Management

### Source

- **TS path**: `packages/api/src/routes/users/user-routes.ts`
- **Endpoints**: 4 total

### C# Target

- **File**: `Tamma.Api/Endpoints/Admin/UserEndpoints.cs`

### Endpoint Details

#### 4.1 GET /api/admin/users

| Field | Type | Notes |
|---|---|---|
| Query params | `limit` (max 100, default 50), `offset` (default 0), `role` (optional filter) | |
| Response 200 | `{ users: User[], total: number, limit, offset }` | |
| Auth | `requireRole("admin")` — admin or owner | |

#### 4.2 GET /api/admin/users/{id}

| Field | Type | Notes |
|---|---|---|
| Path params | `id` | |
| Response 200 | `{ user, installations, apiKeys }` | |
| Response 404 | `{ error: "User not found" }` | |
| Auth | `requireSelfOrRole("admin")` — self OR admin/owner | |

#### 4.3 PUT /api/admin/users/{id}/role

| Field | Type | Notes |
|---|---|---|
| Path params | `id` | |
| Request body | `{ role: "owner" \| "admin" \| "member" }` | |
| Response 200 | `{ user }` | Updated user object |
| Response 400 | Invalid role or self-change attempt | |
| Response 403 | Non-owner promoting to admin/owner | |
| Response 404 | User not found | |
| Auth | `requireRole("admin")` | |

Business logic:
- Only owners can promote to admin or owner.
- Cannot change your own role.

#### 4.4 DELETE /api/admin/users/{id}

| Field | Type | Notes |
|---|---|---|
| Path params | `id` | |
| Response 200 | `{ ok: true }` | Soft-delete + revoke keys + unlink installations |
| Response 400 | Cannot delete yourself | |
| Response 404 | User not found | |
| Auth | `requireRole("owner")` — owner only | |

### Auth Requirements

- List/role-update: admin or owner.
- Get: self or admin+.
- Delete: owner only.

### Test Assertions

1. List returns paginated users with correct `total`.
2. List respects `role` filter.
3. List caps `limit` at 100.
4. Get returns user with installations and API keys.
5. Get allows self-access even with `member` role.
6. Get returns 404 for non-existent user.
7. Role update succeeds for owner changing member to admin.
8. Role update rejects non-owner promoting to admin (403).
9. Role update rejects self-change (400).
10. Delete soft-deletes user, revokes keys, unlinks installations.
11. Delete rejects self-deletion (400).
12. All endpoints return 401 without auth, 403 for insufficient role.

---

## 5. Route Group E — Admin User API Keys

### Source

- **TS path**: `packages/api/src/routes/users/api-key-routes.ts`
- **Endpoints**: 3 total

### C# Target

- **File**: `Tamma.Api/Endpoints/Admin/UserApiKeyEndpoints.cs`

### Endpoint Details

#### 5.1 POST /api/admin/users/{id}/keys

| Field | Type | Notes |
|---|---|---|
| Path params | `id` (userId) | |
| Request body | `{ label?: string }` | Default: "default" |
| Response 201 | `{ id, key, prefix, label, createdAt }` | `key` shown ONCE |
| Response 404 | `{ error: "User not found" }` | |
| Auth | `requireSelfOrRole("admin")` | |

#### 5.2 GET /api/admin/users/{id}/keys

| Field | Type | Notes |
|---|---|---|
| Path params | `id` (userId) | |
| Response 200 | `{ apiKeys: ApiKey[] }` | No raw key |
| Response 404 | `{ error: "User not found" }` | |
| Auth | `requireSelfOrRole("admin")` | |

#### 5.3 DELETE /api/admin/users/{id}/keys/{keyId}

| Field | Type | Notes |
|---|---|---|
| Path params | `id` (userId), `keyId` | |
| Response 200 | `{ ok: true }` | |
| Response 404 | User or key not found | |
| Auth | `requireSelfOrRole("admin")` | |

### Auth Requirements

- Self or admin+ for all three endpoints.

### Test Assertions

1. Create returns 201 with raw key and prefix.
2. Create returns 404 if user does not exist.
3. List returns keys without raw key values.
4. Delete revokes key and subsequent list excludes it.
5. Delete returns 404 for non-existent key.
6. Member role can manage own keys but not others'.
7. Admin role can manage any user's keys.

---

## 6. Route Group F — Admin User Invites

### Source

- **TS path**: `packages/api/src/routes/users/invite-routes.ts`
- **Endpoints**: 3 total

### C# Target

- **File**: `Tamma.Api/Endpoints/Admin/InviteEndpoints.cs`

### Endpoint Details

#### 6.1 POST /api/admin/users/invite

| Field | Type | Notes |
|---|---|---|
| Request body | `{ email?: string, role?: string }` | role default: "member" |
| Response 201 | `{ id, inviteLink, role, expiresAt }` | 72-hour expiry |
| Response 400 | Invalid role or email format | |
| Response 403 | Non-owner inviting admin/owner | |
| Auth | `requireRole("admin")` | |

Business logic:
- Only owners can invite admin/owner roles.
- `inviteLink` = `{dashboardUrl}/invite/{token}`.
- Token: 32 bytes, base64url.

#### 6.2 GET /api/admin/users/invites

| Field | Type | Notes |
|---|---|---|
| Response 200 | `{ invites: Invite[] }` | Pending only |
| Auth | `requireRole("admin")` | |

#### 6.3 DELETE /api/admin/users/invites/{id}

| Field | Type | Notes |
|---|---|---|
| Path params | `id` | |
| Response 200 | `{ ok: true }` | |
| Response 404 | `{ error: "Invite not found" }` | |
| Auth | `requireRole("admin")` | |

### Auth Requirements

- All: admin or owner.
- Invite creation with elevated roles: owner only.

### Test Assertions

1. Create returns 201 with invite link containing base64url token.
2. Create validates email format (reject `>254` chars, malformed).
3. Create rejects admin inviting to owner role (403).
4. List returns only pending (non-accepted, non-expired) invites.
5. Delete revokes invite and subsequent list excludes it.
6. Delete returns 404 for non-existent invite.

---

## 7. Route Group G — Auth Registration

### Source

- **TS path**: `packages/api/src/routes/auth/register.ts`
- **Endpoints**: 3 total

### C# Target

- **File**: `Tamma.Api/Endpoints/Auth/RegisterEndpoints.cs`

### Endpoint Details

#### 7.1 POST /api/v1/auth/register

| Field | Type | Notes |
|---|---|---|
| Request body | `{ email: string, password: string, name: string }` | |
| Response 201 | `{ id, email, message: "Verification email sent" }` | |
| Response 400 | Missing fields, invalid email, weak password | |
| Response 409 | `{ error: "Email already registered" }` | |
| Auth | Anonymous | |

Business logic:
- Email: lowercase trim, basic format check (length 5-254, single `@`, domain with dot, no spaces).
- Password: strength validation (port `validatePasswordStrength` from `auth/password.ts`).
- Verification token: 32 random bytes hex, SHA-256 hashed for storage, 24-hour expiry.
- Send verification email fire-and-forget.

#### 7.2 POST /api/v1/auth/verify-email

| Field | Type | Notes |
|---|---|---|
| Request body | `{ token: string }` | Raw hex token |
| Response 200 | `{ message: "Email verified successfully" }` | |
| Response 400 | Invalid/expired token, already verified | |
| Auth | Anonymous | |

Business logic:
- Hash incoming token with SHA-256, look up user by `emailVerificationTokenHash`.
- Check expiry, check `emailVerified` flag.

#### 7.3 POST /api/v1/auth/resend-verification

| Field | Type | Notes |
|---|---|---|
| Request body | `{ email: string }` | |
| Response 200 | `{ message: "If the email exists..." }` | Anti-enumeration |
| Response 429 | Rate limited (3/hour/email) | |
| Auth | Anonymous | |

Business logic:
- Rate limit: 3 per hour per email (use `IMemoryCache` or `IDistributedCache`).
- Always returns 200 with generic message to prevent email enumeration.
- Generate new token, update user, send email.

### Auth Requirements

- All three: anonymous (no auth).

### Test Assertions

1. Register returns 201 with user ID and email.
2. Register rejects missing fields (400).
3. Register rejects invalid email format (400).
4. Register rejects weak password (400).
5. Register rejects duplicate email (409).
6. Verify-email marks user as verified.
7. Verify-email rejects expired token (400).
8. Verify-email rejects already-verified user (400).
9. Resend-verification returns 200 regardless of email existence.
10. Resend-verification returns 429 after 3 requests in one hour.

---

## 8. Route Group H — Auth Login/Refresh/Logout

### Source

- **TS path**: `packages/api/src/routes/auth/login.ts`
- **Endpoints**: 3 total

### C# Target

- **File**: `Tamma.Api/Endpoints/Auth/LoginEndpoints.cs`

### Endpoint Details

#### 8.1 POST /api/v1/auth/login

| Field | Type | Notes |
|---|---|---|
| Request body | `{ email: string, password: string }` | |
| Response 200 | `{ accessToken, refreshToken, user: { id, email, name, role, tenantId } }` | |
| Response 400 | Missing fields, invalid email | |
| Response 401 | `{ error: "Invalid email or password" }` | Constant-time |
| Response 403 | `{ error: "Please verify your email" }` | |
| Response 429 | Account locked (5 failed attempts) | |
| Auth | Anonymous | |

Business logic:
- Login lockout: port `ILoginLockoutService` — 5 failed attempts locks for configurable duration.
- Constant-time path: always hash something even when user not found (prevent timing attacks).
- JWT claims via `buildJwtClaims` equivalent: `sub`, `email`, `name`, `tenantId`, `tenantRole`, `platformRole`, `authMethod`.
- Access token: 15-minute default expiry.
- Refresh token: 7-day default expiry, SHA-256 hashed for storage.
- Set `tamma_session` HttpOnly cookie on `.tamma.dev`, `SameSite=Lax`, `Secure`.

#### 8.2 POST /api/v1/auth/refresh

| Field | Type | Notes |
|---|---|---|
| Request body | `{ refreshToken: string }` | Raw hex token |
| Response 200 | `{ accessToken, refreshToken }` | New token pair |
| Response 400 | Missing token | |
| Response 401 | Invalid, expired, revoked | |
| Auth | Anonymous (token-based) | |

Business logic:
- Token rotation: revoke old token, issue new pair.
- Reuse detection: if token already revoked, revoke ALL tokens for user (compromise mitigation).
- Update session cookie with new access token.

#### 8.3 POST /api/v1/auth/logout

| Field | Type | Notes |
|---|---|---|
| Request body | `{ refreshToken?: string }` | Optional |
| Response 200 | `{ ok: true }` | |
| Auth | Anonymous | |

Business logic:
- Revoke refresh token if provided.
- Clear `tamma_session` cookie.

### Auth Requirements

- All three: anonymous.

### Test Assertions

1. Login returns 200 with access token, refresh token, and user object.
2. Login sets `tamma_session` cookie with correct attributes.
3. Login returns 401 for wrong password (constant-time).
4. Login returns 403 for unverified email.
5. Login returns 429 after 5 failed attempts.
6. Refresh returns new token pair and revokes old refresh token.
7. Refresh detects reuse and revokes all user tokens (401).
8. Refresh returns 401 for expired token.
9. Logout clears cookie and revokes refresh token.
10. Logout returns 200 even without refresh token body.

---

## 9. Route Group I — Auth Password Reset

### Source

- **TS path**: `packages/api/src/routes/auth/password-reset.ts`
- **Endpoints**: 2 total

### C# Target

- **File**: `Tamma.Api/Endpoints/Auth/PasswordResetEndpoints.cs`

### Endpoint Details

#### 9.1 POST /api/v1/auth/password-reset/request

| Field | Type | Notes |
|---|---|---|
| Request body | `{ email: string }` | |
| Response 200 | `{ message: "If an account with that email exists..." }` | Anti-enumeration |
| Response 400 | Missing email, invalid format | |
| Response 429 | Rate limited (3/hour/email) | |
| Auth | Anonymous | |

Business logic:
- Rate limit: 3 per hour per email.
- Do not send email for GitHub-only users (`authMethod === "github"`).
- Reset token: 32 random bytes hex, SHA-256 hashed, 1-hour expiry.
- Always return 200 with generic message.

#### 9.2 POST /api/v1/auth/password-reset/confirm

| Field | Type | Notes |
|---|---|---|
| Request body | `{ token: string, newPassword: string }` | |
| Response 200 | `{ message: "Password has been reset..." }` | |
| Response 400 | Missing fields, weak password, invalid/expired/used token | |
| Auth | Anonymous | |

Business logic:
- Validate new password strength.
- Hash incoming token, look up in `password_reset_tokens` table.
- Check not consumed, not expired.
- Update password hash, consume token, revoke ALL refresh tokens (force re-login).

### Auth Requirements

- Both: anonymous.

### Test Assertions

1. Request returns 200 regardless of email existence (anti-enumeration).
2. Request returns 429 after 3 requests per email.
3. Request skips email for GitHub-only users.
4. Confirm resets password and invalidates all sessions.
5. Confirm rejects expired token (400).
6. Confirm rejects already-consumed token (400).
7. Confirm rejects weak new password (400).

---

## 10. Route Group J — Auth Identity (me, role-check)

### Source

- **TS paths**:
  - `packages/api/src/routes/auth/me-route.ts`
  - `packages/api/src/routes/auth/github-oauth.ts` (also registers `/api/auth/me`)
  - `packages/api/src/routes/auth/role-check.ts`
- **Endpoints**: 2 total

### C# Target

- **File**: `Tamma.Api/Endpoints/Auth/MeEndpoints.cs` + `Tamma.Api/Endpoints/Auth/RoleCheckEndpoints.cs`

### Endpoint Details

#### 10.1 GET /api/auth/me

| Field | Type | Notes |
|---|---|---|
| Response 200 | `{ user: { id, username, githubId, role } }` | From JWT claims |
| Response 401 | `{ error: "Not authenticated" }` | |
| Auth | JWT cookie (`tamma_session`) | |

Implementation: Read claims from `HttpContext.User`. No database call needed.

#### 10.2 GET /api/auth/role-check

| Field | Type | Notes |
|---|---|---|
| Query params | `service` (required): `elsa`, `logs`, `admin` | |
| Response 200 | `{ allowed: true }` | Has permission |
| Response 400 | Missing or unknown `service` | |
| Response 401 | No valid session | |
| Response 403 | `{ error: "Insufficient role" }` | |
| Auth | JWT cookie | |

Business logic:
- Map service to permission: `elsa -> elsa:access`, `logs -> logs:access`, `admin -> admin:access`.
- Port `hasPermission(role, permission)` from `auth/permissions.ts`.

**Critical note**: This endpoint is used by nginx `auth_request` for ELSA Studio and OpenSearch Dashboards. The C# port MUST maintain the same path (`/api/auth/role-check`) and response codes (200/401/403) for nginx compatibility. The `elsa.tamma.dev` and `logs.tamma.dev` server blocks proxy `auth_request` to `http://tamma-api:3100/api/auth/role-check?service=...`. After Phase 2, update these to point at `tamma-api-dotnet:5080`.

### Auth Requirements

- Both: JWT session cookie.

### Test Assertions

1. `/api/auth/me` returns user claims from valid JWT.
2. `/api/auth/me` returns 401 without cookie.
3. Role-check returns 200 for owner accessing elsa.
4. Role-check returns 200 for admin accessing logs.
5. Role-check returns 403 for member accessing admin.
6. Role-check returns 400 for unknown service.
7. Role-check returns 401 without cookie.

---

## 11. Route Group K — Auth GitHub OAuth

### Source

- **TS path**: `packages/api/src/routes/auth/github-oauth.ts`
- **Endpoints**: 2 total (redirect + callback)

### C# Target

- **File**: `Tamma.Api/Endpoints/Auth/GitHubOAuthEndpoints.cs`

### Endpoint Details

#### 11.1 GET /api/auth/github

| Field | Type | Notes |
|---|---|---|
| Query params | `rd` (optional redirect URL), `invite` (optional invite token) | |
| Response 302 | Redirect to `github.com/login/oauth/authorize` | |
| Auth | Anonymous | |

Business logic:
- Build GitHub OAuth URL with `client_id`, `redirect_uri` (dashboard callback), `scope=read:user user:email`.
- Encode `rd` and `invite` in base64url OAuth state parameter.
- Sanitize `rd`: must be relative path or `https://*.tamma.dev`.

#### 11.2 GET /api/auth/github/callback

| Field | Type | Notes |
|---|---|---|
| Query params | `code`, `error`, `state` | |
| Response 302 | Redirect to dashboard or error page | |
| Auth | Anonymous | |

Business logic:
- Exchange code for GitHub access token via `POST github.com/login/oauth/access_token`.
- Fetch GitHub user profile via `GET api.github.com/user`.
- Parse state for `rd` and `invite` token.
- Process invite: look up, validate expiry, assign role.
- Upsert user in database.
- Auto-link to installations if first login.
- Issue JWT, set `tamma_session` cookie.
- Redirect to sanitized `rd` or dashboard.

Port `sanitizeRedirectUrl` helper: reconstruct URL from parsed components (anti-taint).

### Auth Requirements

- Both: anonymous.

### Test Assertions

1. `/api/auth/github` redirects to GitHub with correct query params.
2. Redirect URL includes encoded `rd` in state.
3. Callback exchanges code and sets `tamma_session` cookie.
4. Callback creates new user on first login.
5. Callback updates existing user on subsequent login.
6. Callback processes invite token and assigns role.
7. Callback redirects to dashboard on success.
8. Callback redirects to error page when code missing.
9. `sanitizeRedirectUrl` rejects non-tamma.dev URLs.
10. `sanitizeRedirectUrl` allows relative paths.

---

## 12. Route Group L — Organization/Tenant Routes

### Source

- **TS path**: `packages/api/src/routes/orgs/index.ts`
- **Endpoints**: 14 total

### C# Target

- **File**: `Tamma.Api/Endpoints/Orgs/OrgEndpoints.cs`
- Large file; consider splitting into `OrgCrudEndpoints.cs`, `OrgMemberEndpoints.cs`, `OrgInviteEndpoints.cs`.

### Endpoint Details

#### 12.1 POST /api/v1/orgs

| Field | Type | Notes |
|---|---|---|
| Request body | `{ name: string, slug: string }` | |
| Response 201 | `{ id, name, slug, plan }` | |
| Response 400 | Invalid name (2-100 chars) or slug (regex, reserved) | |
| Response 409 | Slug already exists | |
| Auth | JWT (any authenticated user) | |

Business logic:
- Slug: `^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$`, reject reserved slugs.
- Create tenant, add user as owner, set as active tenant.

#### 12.2 GET /api/v1/orgs/{tenantId}

| Field | Type | Notes |
|---|---|---|
| Response 200 | `{ id, name, slug, plan, settings, createdAt, yourRole }` | |
| Response 403 | Not a member | |
| Response 404 | Organization not found | |
| Auth | JWT + membership in tenant | |

#### 12.3 PUT /api/v1/orgs/{tenantId}/settings

| Field | Type | Notes |
|---|---|---|
| Request body | `{ name?: string, settings?: object }` | At least one field |
| Response 200 | `{ id, name, slug, plan, settings }` | |
| Response 400 | No fields to update or invalid name | |
| Response 403 | Requires admin+ | |
| Auth | JWT + admin role in tenant | |

#### 12.4 GET /api/v1/orgs/{tenantId}/members

| Field | Type | Notes |
|---|---|---|
| Query params | `limit` (max 100, default 50), `offset` (default 0) | |
| Response 200 | `{ members, total, limit, offset }` | |
| Response 403 | Not a member | |
| Auth | JWT + membership in tenant | |

#### 12.5 PUT /api/v1/orgs/{tenantId}/members/{userId}/role

| Field | Type | Notes |
|---|---|---|
| Request body | `{ role: "owner" \| "admin" \| "member" }` | |
| Response 200 | `{ membership }` | |
| Response 400 | Invalid role, last owner check | |
| Response 403 | Insufficient privileges | |
| Response 404 | Target not a member | |
| Auth | JWT + admin+ in tenant | |

Business logic (role hierarchy: member=0, admin=1, owner=2):
- Only owners can change to/from owner level.
- Admins can only change roles below their level.
- Cannot demote the last owner.

#### 12.6 DELETE /api/v1/orgs/{tenantId}/members/{userId}

| Field | Type | Notes |
|---|---|---|
| Response 200 | `{ ok: true }` | |
| Response 400 | Last owner self-removal | |
| Response 403 | Insufficient role, cannot remove owner | |
| Response 404 | Not a member | |
| Auth | JWT + admin+ in tenant | |

Business logic:
- Admin+ required.
- Non-owners cannot remove owners.
- Cannot remove self if last owner.
- Clear removed user's active tenant if it was this one.

#### 12.7 POST /api/v1/orgs/{tenantId}/invites

| Field | Type | Notes |
|---|---|---|
| Request body | `{ email: string, role?: string }` | role default: "member" |
| Response 201 | `{ id, email, role, expiresAt }` | 72-hour expiry |
| Response 403 | Requires admin+ | |
| Response 404 | Organization not found | |
| Auth | JWT + admin+ in tenant | |

Business logic:
- Invite token: 32 random bytes hex, SHA-256 hashed.
- Send invite email (fire-and-forget).

#### 12.8 GET /api/v1/orgs/{tenantId}/invites

| Field | Type | Notes |
|---|---|---|
| Response 200 | `{ invites: [...] }` | Pending invites only |
| Auth | JWT + admin+ in tenant | |

#### 12.9 DELETE /api/v1/orgs/{tenantId}/invites/{inviteId}

| Field | Type | Notes |
|---|---|---|
| Response 200 | `{ ok: true }` | |
| Response 404 | Invite not found | |
| Auth | JWT + admin+ in tenant | |

#### 12.10 POST /api/v1/orgs/invites/accept

| Field | Type | Notes |
|---|---|---|
| Request body | `{ token: string }` | |
| Response 200 | `{ tenantId, role, message }` | |
| Response 400 | Invalid/expired/accepted token | |
| Auth | JWT (any authenticated user) | |

Business logic:
- Hash token, look up invite, check expiry/acceptance.
- Add user as member, set active tenant if none.

#### 12.11 POST /api/v1/auth/switch-org

| Field | Type | Notes |
|---|---|---|
| Request body | `{ tenantId: string }` | |
| Response 200 | `{ accessToken, tenantId, role }` | New JWT with updated tenant |
| Response 400 | Missing tenantId | |
| Response 403 | Not a member of target org | |
| Auth | JWT | |

Business logic:
- Verify membership in target tenant.
- Update user's active tenant.
- Issue new JWT with updated claims, set cookie.

#### 12.12 GET /api/v1/tenants

| Field | Type | Notes |
|---|---|---|
| Response 200 | `{ tenants: [{ id, name, slug, plan, role, joinedAt, isActive }] }` | |
| Auth | JWT | |

#### 12.13 POST /api/v1/orgs/{tenantId}/transfer-ownership

| Field | Type | Notes |
|---|---|---|
| Request body | `{ newOwnerUserId: string }` | |
| Response 200 | `{ tenantId, previousOwnerId, newOwnerId }` | |
| Response 400 | Self-transfer, target not a member | |
| Response 403 | Only owner can transfer | |
| Response 404 | Tenant not found/deleted | |
| Auth | JWT + owner in tenant | |

#### 12.14 DELETE /api/v1/orgs/{tenantId}

| Field | Type | Notes |
|---|---|---|
| Query params | `confirm` (optional HMAC token), `force` (optional) | |
| Without confirm: Response 202 | `{ message, confirmationToken, expiresAt }` | Soft-delete, 10-min HMAC |
| With confirm: Response 204 | (no body) | Hard-delete cascade |
| Response 400 | Expired/invalid confirmation | |
| Response 403 | Only owner can delete | |
| Response 409 | Cannot delete last tenant | |
| Auth | JWT + owner in tenant | |

Business logic:
- Two-step delete: soft-delete returns HMAC confirmation (10-min TTL).
- Hard-delete with valid HMAC: cascade remove memberships, invites, tenant.
- HMAC: `SHA256-HMAC(tenantId:userId:issuedAt, jwtSecret)`, constant-time comparison.
- Cannot delete user's only organization (409).

### Auth Requirements

- All: JWT required.
- Create/accept-invite/switch-org/list-tenants: any authenticated user.
- Get/list-members: membership in tenant.
- Settings/members-role/remove-member/invites: admin+ in tenant.
- Transfer/delete: owner in tenant.

### Test Assertions

1. Create org returns 201 with tenant details.
2. Create rejects reserved slugs (400).
3. Create rejects duplicate slug (409).
4. Get returns org with `yourRole` for member.
5. Get returns 403 for non-member.
6. Settings update requires admin+ (403 for member).
7. Member list returns paginated results.
8. Role update enforces hierarchy (owner-only for owner-level changes).
9. Role update prevents removal of last owner.
10. Remove member clears active tenant for removed user.
11. Invite sends email and returns 201 with expiry.
12. Accept invite adds membership and optionally sets active tenant.
13. Accept invite rejects expired token.
14. Switch-org issues new JWT with updated tenant claims.
15. List tenants returns all memberships with `isActive` flag.
16. Transfer ownership demotes old owner to admin.
17. Delete soft-deletes and returns HMAC confirmation token.
18. Delete with valid confirmation hard-deletes (204).
19. Delete rejects last tenant (409).
20. Delete rejects expired confirmation (400).

---

## 13. nginx Routing Configuration

### Changes to `docker/nginx-proxy.conf.template`

Add C# API route blocks BEFORE the catch-all `/api/` block in each server section. nginx uses first-match for `location` directives (with longest prefix match for non-regex).

#### app.tamma.dev server block

Insert these blocks **before** `location /api/`:

```nginx
    # ----- Phase 2: Core routes served by C# API -----
    location = /api/health {
        proxy_pass http://tamma-api-dotnet:5080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
    }

    location /api/admin/ {
        proxy_pass http://tamma-api-dotnet:5080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
    }

    location /api/v1/auth/ {
        proxy_pass http://tamma-api-dotnet:5080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
    }

    location /api/auth/ {
        proxy_pass http://tamma-api-dotnet:5080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
    }

    location /api/v1/orgs/ {
        proxy_pass http://tamma-api-dotnet:5080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
    }

    location = /api/v1/tenants {
        proxy_pass http://tamma-api-dotnet:5080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
    }

    # ----- Remaining API routes: still served by TS API -----
    location /api/ {
        proxy_pass http://tamma-api:3100/api/;
        ...existing config...
    }
```

#### api.tamma.dev server block

Same pattern: add the Phase 2 `location` blocks before the catch-all `location /`.

#### Bare IP server block (port 80)

Same pattern: add Phase 2 blocks before `location /api/`.

#### auth_request updates for elsa.tamma.dev and logs.tamma.dev

Update the internal `auth_request` proxy to point to the C# API:

```nginx
    # BEFORE (TS):
    location = /auth/role-check {
        internal;
        proxy_pass http://tamma-api:3100/api/auth/role-check?service=elsa;
        ...
    }

    # AFTER (C#):
    location = /auth/role-check {
        internal;
        proxy_pass http://tamma-api-dotnet:5080/api/auth/role-check?service=elsa;
        ...
    }
```

Apply the same change for `logs.tamma.dev`.

### Verification

After nginx reload:
1. `curl -s https://app.tamma.dev/api/health` hits C# API (check response format).
2. `curl -s https://app.tamma.dev/api/v1/settings` hits TS API (not ported yet).
3. ELSA Studio login flow still works (role-check via C# API).
4. OpenSearch Dashboards access still works (role-check via C# API).

---

## 14. Rollback Instructions

### Trigger Criteria

Roll back if any of these occur after deploy:
- Dashboard login flow broken (auth endpoints non-functional).
- ELSA Studio or Logs Dashboard returns 502/401 (role-check failure).
- Admin panel endpoints returning 5xx.
- JWT cookies issued by C# API not accepted by remaining TS endpoints.

### Step-by-Step Rollback

1. **Revert nginx config**: Remove all Phase 2 `location` blocks. Restore `auth/role-check` proxy targets to `tamma-api:3100`.

   ```bash
   # On VPS
   cd /root/tamma/docker
   git checkout HEAD~1 -- nginx-proxy.conf.template
   docker compose exec nginx-proxy nginx -s reload
   ```

2. **Verify TS API is still running**: The TS API (`tamma-api` container) retains all routes throughout Phase 2. No code was removed from it.

   ```bash
   docker compose ps tamma-api
   curl -s http://localhost:3100/api/health
   ```

3. **Verify traffic is flowing to TS**: Hit each affected path and confirm the response comes from the TS API (check response headers or format differences).

4. **No database rollback needed**: Phase 2 does not add new tables or columns. Both APIs use the same schema from Phase 1.

5. **Leave C# API running**: It can remain running on port 5080 without receiving traffic. This allows debugging without downtime.

### Rollback Time

Expected: < 2 minutes (nginx config revert + reload).

### Post-Rollback

- File an incident report documenting which endpoints failed and why.
- Fix C# implementation, re-run integration tests.
- Re-apply nginx changes only after all tests pass.

---

## 15. Implementation Order

Execute route groups in dependency order:

| Step | Group | Endpoints | Depends On | Estimated Hours |
|---|---|---|---|---|
| 1 | A: Health | 1 | Phase 1 foundation | 1h |
| 2 | J: Me + Role-check | 2 | Phase 1 auth middleware | 3h |
| 3 | K: GitHub OAuth | 2 | J (shared cookie/JWT logic) | 5h |
| 4 | G: Registration | 3 | Phase 1 repositories (User, Email) | 4h |
| 5 | H: Login/Refresh/Logout | 3 | G (user exists), Phase 1 (lockout, refresh token repo) | 6h |
| 6 | I: Password Reset | 2 | H (user with password), Phase 1 (password reset repo) | 3h |
| 7 | B: Admin Health | 1 | Phase 1 (auth middleware, HttpClient setup) | 3h |
| 8 | C: Service Keys | 4 | Phase 1 (API key repo, permissions) | 4h |
| 9 | D: User Management | 4 | Phase 1 (user repo, role middleware) | 5h |
| 10 | E: User API Keys | 3 | D (user routes exist) | 3h |
| 11 | F: User Invites | 3 | D (user routes exist) | 3h |
| 12 | L: Org/Tenant Routes | 14 | Phase 1 (tenant repo, membership repo), H (JWT issuance for switch-org) | 8h |
| **Total** | | **43** | | **48h** |

### File Creation Summary

```
Tamma.Api/
  Endpoints/
    HealthEndpoints.cs                          (Group A)
    Admin/
      HealthEndpoints.cs                        (Group B)
      ServiceKeyEndpoints.cs                    (Group C)
      UserEndpoints.cs                          (Group D)
      UserApiKeyEndpoints.cs                    (Group E)
      InviteEndpoints.cs                        (Group F)
    Auth/
      RegisterEndpoints.cs                      (Group G)
      LoginEndpoints.cs                         (Group H)
      PasswordResetEndpoints.cs                 (Group I)
      MeEndpoints.cs                            (Group J)
      RoleCheckEndpoints.cs                     (Group J)
      GitHubOAuthEndpoints.cs                   (Group K)
    Orgs/
      OrgEndpoints.cs                           (Group L)
      OrgMemberEndpoints.cs                     (Group L)
      OrgInviteEndpoints.cs                     (Group L)
  Models/
    Requests/                                   (request DTOs per group)
    Responses/                                  (response DTOs per group)
```

### Shared Services to Create

| Service | Purpose | Used By |
|---|---|---|
| `IEmailService` | Send verification/reset/invite emails | G, I, L |
| `LoginLockoutService` | Brute-force protection | H |
| `RateLimitService` | Per-email rate limiting | G, I |
| `RedirectSanitizer` | URL validation for OAuth | K |
| `DeleteConfirmationService` | HMAC token generation/verification | L |

---

## 16. Phase 2 Success Checklist

- [ ] All 43 endpoints return identical response shapes to TS originals.
- [ ] 120 xUnit tests green (45 admin + 40 auth + 35 org).
- [ ] JWT cookies issued by C# API accepted by TS API (shared `JWT_SECRET`).
- [ ] nginx routes validated: admin/auth/orgs to C#, all others to TS.
- [ ] Dashboard login flow works end-to-end (GitHub OAuth through C# API).
- [ ] Email/password login flow works end-to-end through C# API.
- [ ] ELSA Studio access works (role-check via C# API).
- [ ] OpenSearch Dashboards access works (role-check via C# API).
- [ ] Rate limiting functional on registration, password reset, resend-verification.
- [ ] Login lockout triggers after 5 failed attempts.
- [ ] Refresh token rotation and reuse detection working.
- [ ] Tenant isolation via EF Core global query filters verified.
- [ ] TS API still serves non-Phase-2 routes without regression.
- [ ] Rollback tested: removing nginx Phase 2 blocks restores TS routing in < 2 min.
