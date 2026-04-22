# Finding 001: Cross-Tenant Read/Write Through Path `tenantId`

**Scope**: orgs
**Severity**: P0 (cutover-blocking)
**Status**: Behavioral drift (ported but semantics diverged)
**Estimated port effort**: 4h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: 549f10d
- **Notes**: New `RequireTenantMembershipFilter` (Authorization/) attached to every `/api/v1/orgs/{tenantId}/*` route in Program.cs. Filter calls `ITenantMembershipRepository.GetRoleAsync(pathTenantId, jwt.sub)` and 403s on null; on success it stashes the resolved role on `HttpContext.Items["TenantRole"]` for handlers + companion role filter. Replaced the old `AdminAccess`/`OwnerAccess`/`SettingsManage` policies on these routes (those checked JWT *platform* permission, not path-tenant role).

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/orgs/index.ts`.

- File: `packages/api/src/routes/orgs/index.ts:160-188` (`GET /api/v1/orgs/:tenantId`), and every sibling endpoint in the same file.
- Contract/behavior: on every path-bound tenant endpoint, the handler re-verifies that the authenticated user has a membership row in the **path** tenant before doing any work.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/orgs/index.ts (9e9a57c~1) L160-L188 — GET /orgs/:tenantId
app.get<{
  Params: { tenantId: string };
}>(
  '/api/v1/orgs/:tenantId',
  async (request, reply) => {
    const jwt = await getAuthenticatedUser(request, reply);
    if (!jwt) return;

    const { tenantId } = request.params;

    // Verify membership
    const membership = await membershipStore.getMembership(tenantId, jwt.sub);
    if (!membership) {
      return reply.status(403).send({ error: 'Not a member of this organization' });
    }

    const tenant = await tenantStore.getTenant(tenantId);
    // …
```

The identical pattern recurs at `packages/api/src/routes/orgs/index.ts:199-208` (settings), `:250-256` (members list), `:292-301` (role update), `:350-361` (remove), `:422-428` (invites), `:486-491` (list invites), `:521-525` (delete invite), `:622-628` (switch-org), `:720-723` (transfer-ownership), `:780-789` (delete).

- Dependencies: `ITenantMembershipStore.getMembership(tenantId, userId)` from `packages/api/src/persistence/tenant-membership-store.ts:68`.
- Tests: `packages/api/src/routes/orgs/__tests__/*` (deleted alongside the source).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:38-43`, `:45-55`, `:57-67`, `:69-77`, `:79-86`, `:88-111`, `:113-117`, `:119-123`, and `apps/tamma-elsa/src/Tamma.Api/Program.cs:356-368`.
- Contract/behavior: Each endpoint receives `Guid tenantId` from the route, runs the repository operation, returns. No endpoint calls `GetRoleAsync(tenantId, userId)` to confirm the caller is a member of that **path** tenant. The only authorization is the route-group policy `MemberAccess`, which just asserts `RequireAuthenticatedUser()` (Program.cs:207-211).
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs (current) L38-L43
public static async Task<IResult> GetOrg(Guid tenantId, ITenantRepository tenantRepo)
{
    var tenant = await tenantRepo.GetByIdAsync(tenantId);
    if (tenant is null) return Results.NotFound(new { error = "Organization not found" });
    return Results.Ok(new OrgResponse(tenant.Id, tenant.Name, tenant.Slug, tenant.Type, tenant.OwnerId, tenant.Settings, tenant.CreatedAt));
}
```

```csharp
// apps/tamma-elsa/src/Tamma.Api/Program.cs (current) L207-L211 — MemberAccess policy
options.AddPolicy("MemberAccess", p =>
{
    p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
    p.RequireAuthenticatedUser();
});
```

```csharp
// apps/tamma-elsa/src/Tamma.Api/Program.cs (current) L356-L368
var orgs = app.MapGroup("/api/v1/orgs").RequireAuthorization("MemberAccess");
orgs.MapPost("", OrgEndpoints.CreateOrg);
orgs.MapGet("/{tenantId}", OrgEndpoints.GetOrg);
orgs.MapPut("/{tenantId}/settings", OrgEndpoints.UpdateOrgSettings).RequireAuthorization("SettingsManage");
orgs.MapGet("/{tenantId}/members", OrgEndpoints.ListMembers);
orgs.MapPut("/{tenantId}/members/{userId}/role", OrgEndpoints.UpdateMemberRole).RequireAuthorization("AdminAccess");
orgs.MapDelete("/{tenantId}/members/{userId}", OrgEndpoints.RemoveMember).RequireAuthorization("AdminAccess");
orgs.MapPost("/{tenantId}/invites", OrgEndpoints.CreateInvite).RequireAuthorization("AdminAccess");
```

`AdminAccess` / `SettingsManage` only check the caller's **platform** permissions on the JWT, not their role inside the path `tenantId`.

- Dependencies: `ITenantMembershipRepository.GetRoleAsync` exists (`apps/tamma-elsa/src/Tamma.Data/Repositories/TenantMembershipRepository.cs:33-38`) but is never called by `OrgEndpoints`.
- Tests: no `OrgEndpoints` tests exist under `apps/tamma-elsa/tests/Tamma.Api.Tests/` (no `Orgs/` directory).

## 3. The gap

Concrete behavioral difference — what a caller or user experiences differently.

- TS did: for every `/api/v1/orgs/:tenantId/*` endpoint, run `membershipStore.getMembership(paramTenantId, jwt.sub)` and 403 if not a member.
- C# does: trusts the path `tenantId` verbatim, performs the DB operation.
- For a caller with `jwt.sub = user-A`, `jwt.tid = tenant-A` sending `GET /api/v1/orgs/<tenant-B>`: TS returns `403 {"error":"Not a member of this organization"}`; C# returns `200` with tenant-B's name/slug/settings.
- In production, any authenticated user can:
  - Read any org's metadata (`GET /orgs/:id`) and settings.
  - List any org's members and their emails (`GET /orgs/:id/members`).
  - With `admin:access` platform permission on their own tenant, promote / demote / remove members of any other org (`PUT /orgs/:id/members/:uid/role`, `DELETE /orgs/:id/members/:uid`).
  - With `admin:access`, invite attackers into any other org via `POST /orgs/:id/invites`.
  - With `users:manage` (OwnerAccess), transfer ownership of any org or soft-delete it (`POST /orgs/:id/transfer-ownership`, `DELETE /orgs/:id`).

Error paths:
- TS error path: `403 { "error": "Not a member of this organization" }` or `403 { "error": "Requires admin role or higher" }`.
- C# error path: none — returns 200/204 on cross-tenant access.

## 4. Gap from stories

Which Epic / story file describes what this surface SHOULD be?

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - AC 8: "**List members** endpoint `GET /api/v1/orgs/:tenantId/members` returns paginated member list with roles" — implicit that the caller must be a member.
  - AC 9: "**Update member role** endpoint … allows `owner` to change roles; `admin` can change `member` roles only" — requires resolving the caller's role within the path tenant.
  - AC 10: "**Remove member** … allows `admin+` to remove members; owners cannot remove themselves if they are the last owner" — requires resolving the caller's role within the path tenant.
  - AC 6: "only `admin+` can invite" — per-tenant role, not platform role.
  - Task 5 Subtask 5.1 (L67): "Create `packages/api/src/middleware/require-tenant.ts` -- extracts `tenantId` from JWT, verifies membership via `tenant_memberships`, decorates request".
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Behavioral drift (ported but semantics diverged). The route surface exists but the membership gate was dropped.
- **What's needed to finish**:
  1. Add a `requireTenantMembership(request, tenantId)` helper (see finding 024) and call it at the top of every path-bound `/orgs/:tenantId/*` handler.
  2. Return `403 { "error": "Not a member of this organization" }` when `GetRoleAsync(tenantId, userId)` returns `null`.
  3. For handlers that require a specific role level (admin+, owner), layer a role-hierarchy check on top (see findings 012, 013, 020).
  4. Assert in xUnit tests that path-tenant != JWT-tenant returns 403 for GET, PUT, DELETE, POST variants.
- **Is it "just a stub" or is scope missing?** The scope was understood in TS and in the story; the port dropped it. The C# author apparently assumed `RequireAuthorization("MemberAccess")` was a tenant gate, but it is only an authentication gate.
- **Blockers**: Depends on finding 024 (port `requireTenant`).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` (all 13 handlers)
  - `apps/tamma-elsa/src/Tamma.Api/Program.cs` (register a per-endpoint requirement or a preHandler)
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Middleware/RequireTenantMembershipFilter.cs` (endpoint filter) — see finding 024.
- Tests to add (in `apps/tamma-elsa/tests/Tamma.Api.Tests/Orgs/CrossTenantAccessTests.cs`):
  - `Get_ReturnsForbidden_WhenUserIsNotMemberOfPathTenant`
  - `ListMembers_ReturnsForbidden_WhenUserIsNotMemberOfPathTenant`
  - `UpdateMemberRole_ReturnsForbidden_WhenUserIsNotMemberOfPathTenant`
  - `RemoveMember_ReturnsForbidden_WhenUserIsNotMemberOfPathTenant`
  - `CreateInvite_ReturnsForbidden_WhenUserIsNotMemberOfPathTenant`
  - `DeleteInvite_ReturnsForbidden_WhenUserIsNotMemberOfPathTenant`
  - `TransferOwnership_ReturnsForbidden_WhenUserIsNotMemberOfPathTenant`
  - `DeleteOrg_ReturnsForbidden_WhenUserIsNotMemberOfPathTenant`
- Estimated effort: 4h broken down as:
  - Add endpoint filter + DI: 1h
  - Wire into all 13 handlers: 1h
  - Unit + integration tests: 2h

## References

- TS source: `packages/api/src/routes/orgs/index.ts:160-188` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs`, `apps/tamma-elsa/src/Tamma.Api/Program.cs:207-211, 356-368`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (ACs 6, 8, 9, 10)
- Related findings: `docs/audit/port-gaps/orgs/012-*.md`, `013-*.md`, `023-*.md`, `024-*.md`
- CLAUDE.md section: n/a
