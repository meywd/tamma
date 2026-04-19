# Finding 010: `/api/auth/role-check` ignores `?service=` (nginx gateway broken)

**Scope**: auth
**Severity**: P0 (cutover-blocking)
**Status**: Behavioral drift (endpoint returns unrelated data)
**Estimated port effort**: 3h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/auth/role-check.ts`.

- File: `packages/api/src/routes/auth/role-check.ts:1-77`.
- Contract: Endpoint used by nginx's `auth_request` directive to gate access to proxied services at `elsa.tamma.dev`, `logs.tamma.dev`, `admin.tamma.dev`. Accepts `?service=elsa|logs|admin`. Reads JWT from `tamma_session` cookie. Maps the service to a permission (`elsa:access` / `logs:access` / `admin:access`), evaluates against the user's role via `hasPermission`, returns 200/401/403/400.
- Key code:

```typescript
// packages/api/src/routes/auth/role-check.ts:23-30 (9e9a57c~1)
const SERVICE_PERMISSION_MAP: Record<string, Permission> = {
  elsa: 'elsa:access',
  logs: 'logs:access',
  admin: 'admin:access',
};

// packages/api/src/routes/auth/role-check.ts:35-74
app.get('/api/auth/role-check', { config: { rateLimit: false } }, async (request, reply) => {
  const service = request.query.service;
  if (!service) return reply.status(400).send({ error: 'Missing required query parameter: service' });

  const permission = SERVICE_PERMISSION_MAP[service];
  if (!permission) return reply.status(400).send({ error: `Unknown service: ${service}` });

  try {
    const decoded = await request.jwtVerify<{ id, username, githubId, role }>();
    const role = decoded.role;
    if (!isValidRole(role)) return reply.status(403).send({ error: 'Insufficient role' });
    if (hasPermission(role, permission)) return reply.status(200).send({ allowed: true });
    return reply.status(403).send({ error: 'Insufficient role' });
  } catch {
    return reply.status(401).send({ error: 'Not authenticated' });
  }
});
```

- Dependencies: `hasPermission` / `isValidRole` from `auth/permissions.ts`; `@fastify/jwt` cookie-based verify.
- Tests: No dedicated test file visible, but the endpoint was used in production nginx config.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:370-375`.
- Contract: Read the role claim from the already-authenticated `ClaimsPrincipal`, return all permissions granted to that role. Does NOT accept `?service=` at all.
- Key code (six lines):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:370-375
public static Task<IResult> RoleCheck(ClaimsPrincipal principal)
{
    var role = principal.FindFirst(ClaimTypes.Role)?.Value ?? "member";
    var permissions = Auth.Permissions.GetRolePermissions(role);
    return Task.FromResult(Results.Ok(new RoleCheckResponse(role, permissions)));
}
```

- Registered with `RequireAuthorization("MemberAccess")` at `Program.cs:333`, which means the JWT must verify via the standard `Authorization: Bearer` scheme — **not via the `tamma_session` cookie**.
- Dependencies: `ClaimsPrincipal`, `Permissions.GetRolePermissions`.
- Tests: None check behavior against a `?service=` query param.

## 3. The gap

Three composed regressions:

1. **Query param dropped**: Caller sends `GET /api/auth/role-check?service=elsa`. C# ignores `service` entirely. Returns a role-and-permissions bundle. nginx's `auth_request` then has to interpret the JSON — but `auth_request` only reads the HTTP status code, not the body. So nginx sees 200 regardless of whether the user has `elsa:access`. **Access-control is bypassed for every gated subdomain.**
2. **JWT source mismatch**: TS called `request.jwtVerify()` which read the `tamma_session` cookie; C# relies on `RequireAuthorization("MemberAccess")` which requires `Authorization: Bearer` header. nginx's `auth_request` forwards the original request's cookies, not an Authorization header. So nginx cannot authenticate at all — returns 401 for any subdomain user.
3. **Return shape**: TS `{ allowed: true }`. C# `{ role: "admin", permissions: [ "..." ] }`. Any downstream consumer that assumed the TS shape gets a different schema.

Composition of (1), (2), (3) means nginx-gated subdomains behave one of two ways:
- Users NEVER reach the service (401 because cookie isn't attached to Bearer).
- Or, if someone reconfigures nginx to fix auth-header injection (via `proxy_set_header Authorization`), the endpoint returns 200 for ANY authenticated user regardless of `service`, so `member`-role users can reach `elsa.tamma.dev` (which was supposed to be `admin`-gated).

Either way, the security gate is broken.

Error paths:
- TS: 400 / 401 / 403 / 200 with distinct semantics per service.
- C#: 401 (MemberAccess failed) / 200 (any authenticated user).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-16/16-5-role-based-access-control.md`
- §384: *"For admin-only services (elsa, logs), add a separate `auth_request` to a Tamma API endpoint that checks the `tamma_session` JWT role"*.
- §285-305 of 16-5 shows expected Fastify code:
  ```ts
  // The tamma_session JWT cookie (if present) has the user ID.
  const decoded = app.jwt.verify<{ id: string; role: string }>(tammaSession);
  ```
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

TS precisely matches the story.

## 5. Status

- **Classification**: Behavioral drift (endpoint name survived but purpose changed).
- **What's needed to finish**:
  1. Accept `?service=` query param.
  2. Validate against the service→permission map (`elsa`, `logs`, `admin`).
  3. Read the JWT from the `tamma_session` cookie (requires Finding 004 to restore cookie-carries-JWT).
  4. Evaluate `Permissions.HasPermission(role, requiredPermission)`.
  5. Return 200/401/403/400 with **status-code-driven** semantics (for nginx).
  6. Remove the `RequireAuthorization("MemberAccess")` gate — the endpoint must handle unauthenticated callers itself (401) so nginx sees the right status.
  7. Configure a custom JWT-from-cookie authentication scheme OR manually verify using `IJwtService.ValidateToken`.
- **Is it "just a stub" or is scope missing?** Scope was understood but replaced — the endpoint returns *different* data rather than implementing the gating protocol.
- **Blockers**: Finding 004 (cookie must carry JWT), Finding 002 (role claim shape).

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` (RoleCheck), `Program.cs` (remove RequireAuthorization wrap), `apps/tamma-elsa/src/Tamma.Api/Auth/JwtService.cs` (ensure `ValidateToken` is usable standalone — already is).
- Files to create: None.
- Tests to add:
  - `RoleCheck_MissingService_Returns400`.
  - `RoleCheck_UnknownService_Returns400`.
  - `RoleCheck_NoCookie_Returns401`.
  - `RoleCheck_MemberRoleForElsa_Returns403`.
  - `RoleCheck_AdminRoleForElsa_Returns200`.
  - `RoleCheck_OwnerRoleForAdmin_Returns200`.
  - `RoleCheck_ResponseBodyIsAllowedTrue_OnSuccess`.
- Estimated effort: 3h
  - Endpoint rewrite: 1h
  - Cookie-JWT read helper: 0.5h
  - Tests (7 cases): 1h
  - nginx integration verification (smoke): 0.5h

## References

- TS source: `packages/api/src/routes/auth/role-check.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:370-375`
- Story: `docs/stories/epic-16/16-5-role-based-access-control.md` (§285, §384)
- Related findings: `002-jwt-claim-shape.md`, `004-session-cookie-payload-and-domain.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `e56b04d`
- **Notes**: RoleCheck accepts ?service=elsa|logs|admin, maps to the corresponding `*:access` permission, returns 200/401/403 by status code (nginx-friendly). Endpoint uses AuthenticatedAny so the cookie-bound JWT verifies.
