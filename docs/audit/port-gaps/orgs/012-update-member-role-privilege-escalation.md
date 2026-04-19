# Finding 012: `PUT /orgs/:id/members/:uid/role` — No Role Validation, No Hierarchy, No Last-Owner Guard

**Scope**: orgs
**Severity**: P0 (privilege escalation)
**Status**: Behavioral drift (ported shape, dropped all invariants)
**Estimated port effort**: 3h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: 549f10d
- **Notes**: All five guards now live in `OrgEndpoints.UpdateMemberRole`: (1) `TenantRoleHierarchy.IsValid(req.Role)` whitelist → 400; (2) requester membership/role read from `HttpContext.Items["TenantRole"]` (filter stash) → 403; (3) `GetRoleAsync` of target → 404; (4) hierarchy: only owners touch owner-level, admins cannot touch peers/promote-up → 403; (5) new `CountOwnersAsync` repo method drives last-owner guard on demote → 400. Tests: `OrgEndpointHandlerTests.UpdateMemberRole_Returns400_WhenRoleUnknown`, `_Returns404_WhenTargetNotMember`, `_Returns403_WhenAdminTriesToPromoteToOwner`, `_Returns400_WhenDemotingLastOwner`, `_Succeeds_WhenOwnerPromotesMember`. CHECK constraint already in place from Phase-1 (finding 025).

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/orgs/index.ts`.

- File: `packages/api/src/routes/orgs/index.ts:272-334`.
- Contract/behavior: five distinct guards before a role change persists:
  1. Role string validation: must be one of `owner | admin | member` (400 otherwise).
  2. Requester membership in path tenant (403 otherwise).
  3. Target membership in path tenant (404 otherwise).
  4. Role-hierarchy: only owner can touch owner-level on either side; admin can change member roles only, and cannot promote a user to or above their own level.
  5. Last-owner guard: if demoting an owner, ensure `countOwners(tenantId) > 1` (400 otherwise).
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/orgs/index.ts (9e9a57c~1) L272-L334
app.put<{
  Params: { tenantId: string; userId: string };
  Body: { role?: string };
}>(
  '/api/v1/orgs/:tenantId/members/:userId/role',
  async (request, reply) => {
    const jwt = await getAuthenticatedUser(request, reply);
    if (!jwt) return;

    const { tenantId, userId } = request.params;
    const { role } = request.body ?? {};

    if (!role || !['owner', 'admin', 'member'].includes(role)) {
      return reply.status(400).send({ error: 'role must be one of: owner, admin, member' });
    }

    const requesterMembership = await membershipStore.getMembership(tenantId, jwt.sub);
    if (!requesterMembership) {
      return reply.status(403).send({ error: 'Not a member of this organization' });
    }

    const targetMembership = await membershipStore.getMembership(tenantId, userId);
    if (!targetMembership) {
      return reply.status(404).send({ error: 'User is not a member of this organization' });
    }

    const requesterLevel = ROLE_HIERARCHY[requesterMembership.role] ?? 0;
    const targetLevel = ROLE_HIERARCHY[targetMembership.role] ?? 0;
    const newLevel = ROLE_HIERARCHY[role] ?? 0;

    // Only owner can change roles to/from owner level
    if (requesterMembership.role !== 'owner' && (newLevel >= (ROLE_HIERARCHY['owner'] ?? 2) || targetLevel >= (ROLE_HIERARCHY['owner'] ?? 2))) {
      return reply.status(403).send({ error: 'Only owners can change owner-level roles' });
    }

    // Admin can only change member roles
    if (requesterMembership.role === 'admin') {
      if (targetLevel >= requesterLevel) {
        return reply.status(403).send({ error: 'Cannot change role of users at or above your level' });
      }
      if (newLevel >= requesterLevel) {
        return reply.status(403).send({ error: 'Cannot promote users to or above your level' });
      }
    }

    // If demoting from owner, ensure at least one owner remains
    if (targetMembership.role === 'owner' && role !== 'owner') {
      const ownerCount = await membershipStore.countOwners(tenantId);
      if (ownerCount <= 1) {
        return reply.status(400).send({ error: 'Cannot remove the last owner' });
      }
    }

    const updated = await membershipStore.updateMemberRole(tenantId, userId, role as 'owner' | 'admin' | 'member');

    return reply.send({ membership: updated });
  },
);
```

- Dependencies: `countOwners`, `getMembership`, `updateMemberRole`, `ROLE_HIERARCHY` map at `packages/api/src/routes/orgs/index.ts:51-56`.
- Tests: explicit tests for admin-tries-to-promote-self, demote-last-owner, unknown-role, target-is-not-member.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:69-77`, `apps/tamma-elsa/src/Tamma.Api/Program.cs:361`.
- Contract/behavior: blind call to `UpdateRoleAsync(tenantId, userId, req.Role)`. No validation of `req.Role`, no hierarchy check, no last-owner guard, no membership check for either requester or target. The only gate is the route policy `AdminAccess` which requires platform-level `admin:access` permission on the **caller's own tenant**.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs (current) L69-L77
public static async Task<IResult> UpdateMemberRole(
    Guid tenantId,
    Guid userId,
    UpdateMemberRoleRequest req,
    ITenantMembershipRepository membershipRepo)
{
    await membershipRepo.UpdateRoleAsync(tenantId, userId, req.Role);
    return Results.Ok(new { message = "Role updated" });
}
```

```csharp
// apps/tamma-elsa/src/Tamma.Data/Repositories/TenantMembershipRepository.cs (current) L51-L60
public async Task UpdateRoleAsync(Guid tenantId, Guid userId, string role)
{
    var membership = await db.TenantMemberships
        .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId);
    if (membership is not null)
    {
        membership.Role = role;
        await db.SaveChangesAsync();
    }
}
```

```csharp
// apps/tamma-elsa/src/Tamma.Api/Program.cs (current) L361
orgs.MapPut("/{tenantId}/members/{userId}/role", OrgEndpoints.UpdateMemberRole).RequireAuthorization("AdminAccess");
```

Note also that the `tenant_memberships.role` CHECK constraint from `017:11` was dropped (finding 025), so arbitrary strings persist.

- Dependencies: none.
- Tests: none.

## 3. The gap

Concrete behavioral difference — what an attacker or careless admin can do.

- TS did: a `member`-role caller attempting `PUT /orgs/<t>/members/<self>/role {"role":"owner"}` was rejected with 403 (requester not admin-or-higher; also not owner). An `admin` attempting to promote another admin to owner was rejected with 403. Demoting the sole owner → 400.
- C# does:
  - Caller with platform `admin:access` on tenant A sending `PUT /api/v1/orgs/<tenant-B>/members/<attacker-user-id>/role {"role":"owner"}`: succeeds. Silent cross-tenant privilege escalation (finding 001 + this).
  - Legitimate member of tenant A sending `PUT /api/v1/orgs/<tenant-A>/members/<sole-owner>/role {"role":"member"}` — because the `AdminAccess` policy passes and no hierarchy/last-owner check exists — demotes the sole owner. Org now has zero owners.
  - Caller sending `{"role":"root"}` or `{"role":""}` — succeeds. `tenant_memberships.role` persists arbitrary strings (no CHECK constraint, finding 025). Permission checks that key on role strings silently fail.
- For an attacker sending `{"role":"owner"}` against their own membership: TS rejects with 403 "Cannot promote users to or above your level"; C# persists `role = "owner"`.
- In production: this is the single most severe finding in the orgs scope — any admin-role user on any tenant can self-promote to owner of any org, or wipe out owners of other orgs.

Error paths:
- TS error paths: `400 { "error": "role must be one of: owner, admin, member" }`, `403 { "error": "Only owners can change owner-level roles" }`, `403 { "error": "Cannot change role of users at or above your level" }`, `403 { "error": "Cannot promote users to or above your level" }`, `400 { "error": "Cannot remove the last owner" }`, `404 { "error": "User is not a member of this organization" }`.
- C# error paths: none (200 regardless).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - AC 9: "**Update member role** endpoint `PUT /api/v1/orgs/:tenantId/members/:userId/role` allows `owner` to change roles; `admin` can change `member` roles only".
  - AC 10: "**Remove member** … owners cannot remove themselves if they are the last owner" — establishes the last-owner invariant also applies to demotion.
  - Task 3 Subtask 3.2: "Implement `PUT /api/v1/orgs/:tenantId/members/:userId/role` -- role change with hierarchy enforcement".
  - Task 3 Subtask 3.4: "Write tests for role hierarchy, self-removal prevention, last-owner protection".
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Behavioral drift (shape ported, all 5 invariants dropped).
- **What's needed to finish**:
  1. Validate `req.Role` against the literal set `{ "owner", "admin", "member" }`; 400 otherwise.
  2. Load requester membership via `GetRoleAsync(tenantId, callerId)`. 403 if null.
  3. Load target membership. 404 if null.
  4. Enforce role hierarchy: non-owners cannot touch owner-level; admins cannot change roles at-or-above their own level.
  5. Call `CountOwnersAsync(tenantId)` (new repo method) when demoting from owner; 400 if would leave zero owners.
  6. Also re-add the tenant_memberships CHECK constraint (finding 025).
- **Is it "just a stub" or is scope missing?** Scope was explicitly documented in AC 9 and in Task 3 Subtask 3.2; port fell back to "call the repo and return OK".
- **Blockers**: depends on finding 001 (requester membership gate), finding 025 (CHECK constraint).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` (UpdateMemberRole).
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/ITenantMembershipRepository.cs` (add `CountOwnersAsync`).
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/TenantMembershipRepository.cs` (implement).
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Auth/RoleHierarchy.cs` (the `{owner:2, admin:1, member:0}` map).
  - `apps/tamma-elsa/tests/Tamma.Api.Tests/Orgs/UpdateMemberRoleTests.cs`.
- Tests to add:
  - `UpdateRole_Returns400_WhenRoleIsUnknownString`
  - `UpdateRole_Returns403_WhenRequesterNotMember`
  - `UpdateRole_Returns404_WhenTargetNotMember`
  - `UpdateRole_Returns403_WhenAdminTriesToPromoteToOwner`
  - `UpdateRole_Returns403_WhenAdminTriesToChangeAdmin`
  - `UpdateRole_Returns400_WhenDemotingLastOwner`
  - `UpdateRole_Succeeds_WhenOwnerPromotesMemberToAdmin`
  - `UpdateRole_Succeeds_WhenOwnerDemotesOtherOwner_IfMultipleOwners`
- Estimated effort: 3h broken down as:
  - Role hierarchy helper + repo CountOwnersAsync: 0.5h
  - All five guards in handler: 1h
  - Tests: 1.5h

## References

- TS source: `packages/api/src/routes/orgs/index.ts:272-334, 51-56` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:69-77`, `apps/tamma-elsa/src/Tamma.Data/Repositories/TenantMembershipRepository.cs:51-60`, `apps/tamma-elsa/src/Tamma.Api/Program.cs:361`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (ACs 9, 10; Task 3)
- Related findings: `001-cross-tenant-access-on-path-tenantid.md`, `013-delete-member-hierarchy-missing.md`, `020-transfer-ownership-non-atomic.md`, `025-tenant-memberships-check-constraint-lost.md`
- Archived SQL migration: `database/archived-sql-migrations/017_tenant_memberships.sql:11`
