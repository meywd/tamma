# Finding 013: `DELETE /orgs/:id/members/:uid` — No Hierarchy, No Last-Owner Guard, No Active-Tenant Cleanup

**Scope**: orgs
**Severity**: P0 (privilege / data-integrity)
**Status**: Behavioral drift (ported shape, dropped all invariants + side-effects)
**Estimated port effort**: 3h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/orgs/index.ts`.

- File: `packages/api/src/routes/orgs/index.ts:336-398`.
- Contract/behavior: before removing a member, TS checked requester membership + admin-or-higher role, loaded target membership (404 if missing), enforced last-owner protection (400 on self-removal when sole owner), enforced hierarchy (non-owner cannot remove an owner), and — after removal — cleared the removed user's `users.tenant_id` if it was this tenant (so they don't carry a stale active tenant). It also emitted `TENANT.MEMBER_REMOVED.SUCCESS`.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/orgs/index.ts (9e9a57c~1) L336-L398
app.delete<{
  Params: { tenantId: string; userId: string };
}>(
  '/api/v1/orgs/:tenantId/members/:userId',
  async (request, reply) => {
    const jwt = await getAuthenticatedUser(request, reply);
    if (!jwt) return;

    const { tenantId, userId } = request.params;

    const requesterMembership = await membershipStore.getMembership(tenantId, jwt.sub);
    if (!requesterMembership) {
      return reply.status(403).send({ error: 'Not a member of this organization' });
    }

    const requesterLevel = ROLE_HIERARCHY[requesterMembership.role] ?? 0;

    // Must be admin+
    if (requesterLevel < (ROLE_HIERARCHY['admin'] ?? 1)) {
      return reply.status(403).send({ error: 'Requires admin role or higher' });
    }

    const targetMembership = await membershipStore.getMembership(tenantId, userId);
    if (!targetMembership) {
      return reply.status(404).send({ error: 'User is not a member of this organization' });
    }

    // Cannot remove self if last owner
    if (userId === jwt.sub && targetMembership.role === 'owner') {
      const ownerCount = await membershipStore.countOwners(tenantId);
      if (ownerCount <= 1) {
        return reply.status(400).send({ error: 'Cannot remove yourself as the last owner' });
      }
    }

    // Admins cannot remove owners
    if (requesterMembership.role !== 'owner' && targetMembership.role === 'owner') {
      return reply.status(403).send({ error: 'Cannot remove an owner' });
    }

    await membershipStore.removeMember(tenantId, userId);

    // If removed user's active tenant was this one, clear it
    const user = await userStore.getUser(userId);
    if (user && user.tenantId === tenantId) {
      await userStore.updateActiveTenant(userId, null);
    }

    request.log.info({
      event: 'TENANT.MEMBER_REMOVED.SUCCESS',
      tenantId,
      userId,
      removedBy: jwt.sub,
    }, 'Member removed from organization');

    return reply.send({ ok: true });
  },
);
```

- Dependencies: `ITenantMembershipStore.countOwners`, `IUserStore.updateActiveTenant`.
- Tests: explicit tests for self-removal-last-owner, admin-cannot-remove-owner, 404 on non-member target.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:79-86`, `apps/tamma-elsa/src/Tamma.Api/Program.cs:362`.
- Contract/behavior: blind call to `RemoveAsync(tenantId, userId)`. No membership/role checks, no last-owner guard, no active-tenant cleanup, no event.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs (current) L79-L86
public static async Task<IResult> RemoveMember(
    Guid tenantId,
    Guid userId,
    ITenantMembershipRepository membershipRepo)
{
    await membershipRepo.RemoveAsync(tenantId, userId);
    return Results.Ok(new { message = "Member removed" });
}
```

```csharp
// apps/tamma-elsa/src/Tamma.Data/Repositories/TenantMembershipRepository.cs (current) L22-L31
public async Task RemoveAsync(Guid tenantId, Guid userId)
{
    var membership = await db.TenantMemberships
        .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId);
    if (membership is not null)
    {
        db.TenantMemberships.Remove(membership);
        await db.SaveChangesAsync();
    }
}
```

- Dependencies: none.
- Tests: none.

## 3. The gap

Concrete behavioral difference.

- TS did: admin of tenant A trying to remove the sole owner of tenant A → 400. Admin of tenant A trying to remove owner of tenant B → 403 (not a member). Member trying to remove anyone → 403. After removal, the victim's active tenant was cleared so they don't carry a zombie `users.tenant_id`.
- C# does:
  - Caller with platform `admin:access` on tenant A sending `DELETE /api/v1/orgs/<tenant-B>/members/<owner-of-B>`: succeeds. Tenant B now has no owner and cannot be managed (transfer-ownership requires the caller to be current owner, finding 020).
  - Caller removing the sole owner of their own tenant (including self) → succeeds. Org is orphaned.
  - Removed user's `users.tenant_id` is still pointing at the tenant they were removed from. Next request from that user: `TenantContextMiddleware` sees `tid` on the JWT, `users.tenant_id = <ex-tenant>`, may or may not reject; any EF query filter on their personal data may return zero rows.
- For a caller sending `DELETE /api/v1/orgs/<own>/members/<sole-owner-self>`: TS returns 400 "Cannot remove yourself as the last owner"; C# removes them, org has zero owners.
- In production: combined with finding 020 (transfer-ownership), this creates a scenario where owners can permanently brick their own org by removing themselves when they're the last owner. Recovery requires platform-admin intervention on the database.

Error paths:
- TS error paths: `403 { "error": "Not a member of this organization" }`, `403 { "error": "Requires admin role or higher" }`, `403 { "error": "Cannot remove an owner" }`, `400 { "error": "Cannot remove yourself as the last owner" }`, `404 { "error": "User is not a member of this organization" }`.
- C# error paths: none (200 regardless).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - AC 10: "**Remove member** endpoint `DELETE /api/v1/orgs/:tenantId/members/:userId` allows `admin+` to remove members; owners cannot remove themselves if they are the last owner".
  - AC 14: "**Event emission**: `TENANT.MEMBER_REMOVED.SUCCESS`".
  - Task 3 Subtask 3.3: "Implement `DELETE /api/v1/orgs/:tenantId/members/:userId` -- remove member with last-owner protection".
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Behavioral drift.
- **What's needed to finish**:
  1. Load requester + target membership; 403/404 accordingly.
  2. Enforce admin-or-higher requester role.
  3. Enforce "admin cannot remove owner" hierarchy.
  4. Last-owner guard via new `CountOwnersAsync`.
  5. After `RemoveAsync`, call `userRepo.UpdateActiveTenantAsync(removedUserId, null)` if the user's active tenant was the removed one. (Requires reading the user first.)
  6. Emit `TENANT.MEMBER_REMOVED.SUCCESS` (finding 008 scope).
- **Is it "just a stub" or is scope missing?** Scope defined in AC 10 and Task 3 Subtask 3.3; not ported.
- **Blockers**: depends on finding 012 (CountOwnersAsync), finding 001 (membership gate).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` (RemoveMember).
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/IUserRepository.cs` (if `UpdateActiveTenantAsync` doesn't accept null — verify signature).
- Files to create: `apps/tamma-elsa/tests/Tamma.Api.Tests/Orgs/RemoveMemberTests.cs`.
- Tests to add:
  - `RemoveMember_Returns403_WhenRequesterIsMember`
  - `RemoveMember_Returns403_WhenAdminTriesToRemoveOwner`
  - `RemoveMember_Returns400_WhenSelfRemovalAndLastOwner`
  - `RemoveMember_Returns404_WhenTargetNotMember`
  - `RemoveMember_Succeeds_WhenOwnerRemovesAdmin`
  - `RemoveMember_Succeeds_WhenOwnerRemovesOtherOwner_WithMultipleOwners`
  - `RemoveMember_ClearsActiveTenant_WhenWasRemovedTenant`
- Estimated effort: 3h broken down as:
  - Guards + active-tenant cleanup: 1h
  - Event emission: 0.25h
  - Tests: 1.75h

## References

- TS source: `packages/api/src/routes/orgs/index.ts:336-398` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:79-86`, `apps/tamma-elsa/src/Tamma.Api/Program.cs:362`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (ACs 10, 14; Task 3 Subtask 3.3)
- Related findings: `001-cross-tenant-access-on-path-tenantid.md`, `012-update-member-role-privilege-escalation.md`, `008-post-orgs-no-event-emission.md`, `020-transfer-ownership-non-atomic.md`
