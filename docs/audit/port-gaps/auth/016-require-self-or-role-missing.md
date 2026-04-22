# Finding 016: `requireSelfOrRole` helper missing — users cannot manage own API keys

**Scope**: auth
**Severity**: P2 (authorization contract)
**Status**: Incomplete
**Estimated port effort**: 3h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/middleware/require-role.ts`.

- File: `packages/api/src/middleware/require-role.ts:70-102`.
- Contract: A Fastify preHandler that allows a request through if EITHER the authenticated user is accessing their own resource (`params.id === user.id`) OR the user meets a minimum role. Used to guard self-service endpoints (a user can always GET their own record; admins can GET anyone's).
- Key code:

```typescript
// packages/api/src/middleware/require-role.ts:70-102 (9e9a57c~1)
export function requireSelfOrRole(minimumRole: Role) {
  return async (request: FastifyRequest, reply: FastifyReply): Promise<void> => {
    const user = getAuthUser(request);
    if (!user) {
      reply.status(401).send({ error: 'Not authenticated' });
      return;
    }

    (request as FastifyRequest & { authUser: AuthenticatedUser }).authUser = user;

    const params = request.params as { id?: string };
    if (params.id === user.id) return;      // allow self

    const userLevel = ROLE_HIERARCHY[user.role] ?? -1;
    const requiredLevel = ROLE_HIERARCHY[minimumRole];
    if (userLevel < requiredLevel) {
      reply.status(403).send({ error: `Requires ${minimumRole} role or access to own resource` });
      return;
    }
  };
}
```

- Callers (who relied on self-access semantics):
  - `routes/users/user-routes.ts:56` — `GET /api/admin/users/:id` — a user can fetch their own profile.
  - `routes/users/api-key-routes.ts:30` — `POST /api/admin/users/:id/keys` — a user can create their own API keys.
  - `routes/users/api-key-routes.ts:74` — `GET /api/admin/users/:id/keys` — a user can list their own API keys.
  - `routes/users/api-key-routes.ts:96` — `DELETE /api/admin/users/:id/keys/:keyId` — a user can revoke their own.
- Tests: `packages/api/src/routes/users/__tests__/` covered self-access for all three methods.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Program.cs:351-353` (route registrations); `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:148-184` (handler bodies); `apps/tamma-elsa/src/Tamma.Api/Auth/PermissionHandler.cs` (authorization logic).
- Contract: All `/api/admin/users/{id}/keys*` routes are gated behind `"ApiKeysManage"`, which in `Program.cs:237-241` maps to `PermissionRequirement("apikeys:manage")`. Per `Permissions.cs:25`, `apikeys:manage` requires role `admin` or `owner`.
- Key code (Program.cs routing):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Program.cs:351-353
admin.MapPost("/users/{id}/keys", AdminEndpoints.CreateUserApiKey).RequireAuthorization("ApiKeysManage");
admin.MapGet("/users/{id}/keys", AdminEndpoints.ListUserApiKeys).RequireAuthorization("ApiKeysManage");
admin.MapDelete("/users/{id}/keys/{keyId}", AdminEndpoints.DeleteUserApiKey).RequireAuthorization("ApiKeysManage");
```

- The `AdminEndpoints.CreateUserApiKey` handler itself (lines 148-171) does not check `principal.Id == id` — it just accepts the `id` route param and creates a key for it.
- There is no `requireSelfOrRole` C# analog anywhere in the codebase (greppable for `SelfOrRole`, `self_or_role`, `SelfAccess`, etc. — no results in `apps/tamma-elsa`).
- Also: `GET /api/admin/users/{id}` at `Program.cs:345` is gated by the outer group's `"AdminAccess"` — so a regular member cannot even fetch their own user record. Same regression.
- Tests: No tests of self-access paths.

## 3. The gap

- TS: A `member`-role user authenticates, calls `GET /api/admin/users/<theirOwnId>/keys`, gets 200 with their keys. They can rotate, delete, create their own keys.
- C#: Same user calls same endpoint, is gated by `ApiKeysManage` → fails `PermissionHandler` because `member` is not in `apikeys:manage`'s allowed roles → 403.

For a user wanting to generate an API token for their personal CLI:
- TS: dashboard button → POST `/api/admin/users/<self>/keys` → 201 with key.
- C#: dashboard button → 403 "Insufficient permissions". User cannot create a key without asking an `admin`.

Additional gap on `GET /api/admin/users/{id}`:
- TS: self-access via `requireSelfOrRole('admin')` at `user-routes.ts:56`.
- C#: gated by the outer `admin.MapGroup(..).RequireAuthorization("AdminAccess")` at `Program.cs:338`. A member cannot fetch their own profile via this endpoint.

**Workaround partially exists**: `/api/auth/me` returns the current user's profile, so at the profile level the user has SOME way to see themself. But `/api/admin/users/:id` returned additional data (installations + api-keys) that `/me` does not (the TS version of `/me` was JWT-only, no DB lookup).

Error paths:
- TS: 200 for self, 403 "Requires admin role or access to own resource" for non-self non-admin, 401 if unauthenticated.
- C#: 403 for any non-admin, 401 if unauthenticated.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-16/16-2-user-management-api.md` (not explicitly seen but mentioned in index) — covers user-management CRUD including API keys. Story 16-5 defines RBAC.
- Story 16-5 (`16-5-role-based-access-control.md`) does NOT explicitly describe a "self or role" pattern. The `PERMISSIONS` matrix only has single-role gates.
- Story 18-2 (`18-2-user-login-session-management.md`) mentions neither self-key management nor the `requireSelfOrRole` middleware.
- The only place it's documented is in the TS code itself — the TS implementation created this policy as part of Story 16-2 user-management-API (which I cannot read directly because it's not in the visible story files — but the index at `docs/stories/epic-16/README.md` references it).
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — the "self or role" pattern lives in TS code only

## 5. Status

- **Classification**: Incomplete (a cross-cutting authorization helper was never ported, and its three call sites degenerate to strict admin-only).
- **What's needed to finish**:
  1. Add a custom authorization requirement `SelfOrPermissionRequirement(string permission)` that accepts a route-param name (`"id"`) and compares to the authenticated user's `sub` claim.
  2. Register new policies `SelfOrApiKeysManage`, `SelfOrAdminAccess` for the three keys endpoints and the user-get endpoint.
  3. Re-register the routes with the new policies.
  4. Custom handler reads `AuthorizationHandlerContext.Resource` (which in ASP.NET 8+ is `HttpContext`), extracts `route.Values["id"]`, compares to `context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value`, succeeds-if-match.
- **Is it "just a stub" or is scope missing?** Scope missing — the concept doesn't exist in C#. Semantic rewrite-adjacent.
- **Blockers**: None in the core code. The stories should be updated to document the self-or-role pattern explicitly (and which endpoints apply).

## Remediation

- Files to modify: `Program.cs` (policies + route re-registrations), `apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs` (no change), `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs` (may need route-id equality helpers).
- Files to create: `apps/tamma-elsa/src/Tamma.Api/Auth/SelfOrPermissionRequirement.cs` + handler.
- Tests to add:
  - `SelfOrPermissionHandler_UserAccessingSelf_Allowed`.
  - `SelfOrPermissionHandler_UserAccessingOther_CheckedAgainstPermission`.
  - `SelfOrPermissionHandler_AdminAccessingOther_Allowed`.
  - `CreateUserApiKey_AsMember_ForSelf_Returns201`.
  - `CreateUserApiKey_AsMember_ForOther_Returns403`.
  - `GetUser_AsMember_ForSelf_Returns200`.
- Estimated effort: 3h
  - Requirement + handler: 1h
  - Policy + route wiring (4 routes): 1h
  - Tests (6 cases): 1h

## References

- TS source: `packages/api/src/middleware/require-role.ts:70-102`, `packages/api/src/routes/users/user-routes.ts`, `packages/api/src/routes/users/api-key-routes.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Program.cs:345, 351-353`, `AdminEndpoints.cs:148-184`, `Auth/Permissions.cs`, `Auth/PermissionHandler.cs`
- Story: `docs/stories/epic-16/16-5-role-based-access-control.md` (RBAC matrix, no self-or-role concept)
- Related findings: `018-admin-update-user-role-missing-guards.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `e56b04d`
- **Notes**: SelfOrPermissionRequirement + handler succeeds when the route {id} matches the caller's sub. Wired to admin user-keys (POST/GET/DELETE) and GET /admin/users/{id} via SelfOrApiKeysManage / SelfOrUsersView policies.
