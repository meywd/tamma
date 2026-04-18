# Finding 024: `requireTenant` Middleware Has No C# Equivalent

**Scope**: orgs
**Severity**: P0 (cutover-blocking — underpins finding 001 for all path-tenant endpoints)
**Status**: Not-yet-implemented
**Estimated port effort**: 4h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/middleware/require-tenant.ts`.

- File: `packages/api/src/middleware/require-tenant.ts:16-39`.
- Contract/behavior: a preHandler factory that returns an async hook. Reads `request.user` (the JWT payload populated by `@fastify/jwt`). 401 if missing. 403 if `jwt.tenantId` is missing. 403 if `membershipStore.getMembership(jwt.tenantId, jwt.sub)` returns null. On success, decorates `request.tenantMembership` with the loaded membership for downstream handlers to key on role.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/middleware/require-tenant.ts (9e9a57c~1) L16-L39
export function requireTenant(membershipStore: ITenantMembershipStore) {
  return async (request: FastifyRequest, reply: FastifyReply): Promise<void> => {
    const jwt = (request as FastifyRequest & { user?: UnifiedJwtPayload }).user as UnifiedJwtPayload | undefined;

    if (!jwt) {
      reply.status(401).send({ error: 'Not authenticated' });
      return;
    }

    if (!jwt.tenantId) {
      reply.status(403).send({ error: 'No active tenant. Please create or join an organization.' });
      return;
    }

    const membership = await membershipStore.getMembership(jwt.tenantId, jwt.sub);
    if (!membership) {
      reply.status(403).send({ error: 'Not a member of the active tenant' });
      return;
    }

    (request as FastifyRequest & { tenantMembership?: TenantMembership }).tenantMembership = membership;
  };
}
```

- Dependencies: `ITenantMembershipStore.getMembership`, `UnifiedJwtPayload` type.
- Tests: `packages/api/src/middleware/__tests__/require-tenant.test.ts` (deleted).

Story 18-3 Task 5 Subtask 5.1 called for this, **plus** a Subtask 5.2 for a `require-tenant-role.ts` that would layer role-level checks on top. Subtask 5.2 was never implemented in TS either, but route handlers inlined the role hierarchy check (finding 012) on a per-endpoint basis.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: none. Searched `apps/tamma-elsa/src/Tamma.Api/Middleware/*` and `apps/tamma-elsa/src/Tamma.Api/Auth/*` — zero files match "RequireTenant" or "TenantMembership" middleware/filter.
- Contract/behavior: path-tenant endpoints rely on the route-group policy `MemberAccess` (Program.cs:207-211) which only checks `RequireAuthenticatedUser()`. There is no middleware or endpoint filter that asserts "the caller is a member of the tenant identified by the route". See finding 001 for the full cross-tenant-access chain.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Program.cs (current) L207-L211 — closest equivalent
options.AddPolicy("MemberAccess", p =>
{
    p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
    p.RequireAuthenticatedUser();
});

// L356-L368 — path-tenant routes rely on this policy
var orgs = app.MapGroup("/api/v1/orgs").RequireAuthorization("MemberAccess");
orgs.MapPost("", OrgEndpoints.CreateOrg);
orgs.MapGet("/{tenantId}", OrgEndpoints.GetOrg);
// …
```

The `AdminAccess` and `OwnerAccess` policies check platform-level permissions (`admin:access`, `users:manage`) against the caller's JWT-resolved role, **not** against their role in the path tenant.

- Dependencies: none exist.
- Tests: none.

## 3. The gap

Concrete behavioral difference.

- TS did: `requireTenant(membershipStore)` was a preHandler reusable on any path-tenant route that confirmed the authenticated user's JWT `tenantId` matched their active membership. While individual org handlers inlined their own membership check per route (for flexibility), `requireTenant` was used by non-org route groups (workflows, prompts, agents) where the route didn't carry a `tenantId` param but the user's JWT tenant had to be verified as live.
- C# does: no equivalent. Consequences:
  - The `/api/v1/orgs/:tenantId/*` surface is exposed to the cross-tenant bug (finding 001).
  - Non-org routes that rely on `TenantContext.TenantId` being set correctly (prompts, agents, workflows) trust the JWT `tid` claim without re-verifying that the user still has a membership in that tenant. If a user was removed from their tenant but their JWT is still valid (900s TTL), they retain access until the JWT expires.
  - There is no single place to return 403 "No active tenant. Please create or join an organization." — users with no tenant (e.g., new registrations before `EnsurePersonalTenantMiddleware` runs) get random failures downstream.
- For a caller whose `jwt.tid` is a tenant they were just removed from: TS rejected with 403 on every protected route; C# passes until the JWT expires.
- For a caller whose JWT has no `tid` (legacy token, pre-18-3): TS rejected with "No active tenant"; C# reaches handlers with `TenantContext.TenantId = null` and triggers finding 002's fail-open.

Error paths:
- TS error paths: `401 { "error": "Not authenticated" }`, `403 { "error": "No active tenant. Please create or join an organization." }`, `403 { "error": "Not a member of the active tenant" }`.
- C# error paths: none — fails open to the handler.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - Task 5 Subtask 5.1: "Create `packages/api/src/middleware/require-tenant.ts` -- extracts `tenantId` from JWT, verifies membership via `tenant_memberships`, decorates request".
  - Task 5 Subtask 5.2: "Create `packages/api/src/middleware/require-tenant-role.ts` -- checks user's role within the tenant (not global role)".
  - Task 5 Subtask 5.4: "Write tests for middleware with various role/membership scenarios".
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Not-yet-implemented.
- **What's needed to finish**:
  1. Create `apps/tamma-elsa/src/Tamma.Api/Authorization/RequireTenantMembershipFilter.cs` (implements `IEndpointFilter`).
  2. Resolve caller's `userId` from `ClaimsPrincipal` and `tenantId` either from (a) the `"tenantId"` route value if present (for path-tenant endpoints), or (b) the `"tid"` claim (for JWT-scoped endpoints).
  3. Call `ITenantMembershipRepository.GetRoleAsync(tenantId, userId)` → 403 if null.
  4. Stash the resolved role on `HttpContext.Items["TenantRole"]` for downstream role-hierarchy checks (finding 012, 013).
  5. Create a companion `RequireTenantRoleFilter(string minRole)` that reads the stashed role and compares against a hierarchy map.
  6. Wire the filters in `Program.cs`:
     - `orgs.MapGet("/{tenantId}", OrgEndpoints.GetOrg).AddEndpointFilter<RequireTenantMembershipFilter>();`
     - `orgs.MapPut("/{tenantId}/members/{userId}/role", OrgEndpoints.UpdateMemberRole).AddEndpointFilter<RequireTenantRoleFilter>("admin");`
- **Is it "just a stub" or is scope missing?** Scope clearly defined in Story 18-3 Task 5 Subtask 5.1; simply not ported.
- **Blockers**: depends on finding 023 (improved middleware) for reliable tenant-context resolution on non-path-tenant endpoints.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs` (register filter and attach to path-tenant endpoints).
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` (remove inlined membership checks after filter takes over — but see findings 012, 013 which still need role-specific logic inside handlers).
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Authorization/RequireTenantMembershipFilter.cs`.
  - `apps/tamma-elsa/src/Tamma.Api/Authorization/RequireTenantRoleFilter.cs`.
  - `apps/tamma-elsa/src/Tamma.Api/Authorization/TenantRoleHierarchy.cs` (the `{owner:2, admin:1, member:0}` map).
  - `apps/tamma-elsa/tests/Tamma.Api.Tests/Authorization/RequireTenantMembershipFilterTests.cs`.
  - `apps/tamma-elsa/tests/Tamma.Api.Tests/Authorization/RequireTenantRoleFilterTests.cs`.
- Tests to add:
  - `Filter_Returns401_WhenUnauthenticated`
  - `Filter_Returns403_WhenJwtHasNoTid_AndRouteHasNoTenantIdParam`
  - `Filter_Returns403_WhenNotMember_OfPathTenant`
  - `Filter_Returns403_WhenNotMember_OfJwtTenant` (removed user, JWT still valid)
  - `Filter_StashesRoleInHttpContextItems_OnSuccess`
  - `RoleFilter_Returns403_WhenRoleBelowRequired`
  - `RoleFilter_Succeeds_WhenRoleAtOrAboveRequired`
- Estimated effort: 4h broken down as:
  - Two filters + hierarchy helper: 1.5h
  - Wire into all path-tenant routes: 0.5h
  - Tests (7 cases): 2h

## References

- TS source: `packages/api/src/middleware/require-tenant.ts:16-39` (commit `9e9a57c~1`)
- C# source: none (not ported)
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (Task 5 Subtasks 5.1, 5.2, 5.4)
- Related findings: `001-cross-tenant-access-on-path-tenantid.md`, `012-update-member-role-privilege-escalation.md`, `013-delete-member-hierarchy-missing.md`, `023-tenant-context-middleware-shallow.md`
