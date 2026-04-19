# Finding 018: Admin `UpdateUserRole` missing self-protection and owner-only-promotes guards

**Scope**: auth
**Severity**: P1 (authz business-rule regression)
**Status**: Incomplete
**Estimated port effort**: 1h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/users/user-routes.ts`.

- File: `packages/api/src/routes/users/user-routes.ts:65-110`.
- Contract: `PUT /api/admin/users/:id/role` with four guards:
  1. Role must be one of `owner` / `admin` / `member` (400 otherwise).
  2. **Only owners can promote to admin or owner** — 403 otherwise.
  3. **Cannot change own role** — 400.
  4. Target user must exist — 404.
- Key code:

```typescript
// packages/api/src/routes/users/user-routes.ts:65-110 (9e9a57c~1)
app.put('/api/admin/users/:id/role', { preHandler: [requireRole('admin')] }, async (request, reply) => {
  const { id } = request.params as { id: string };
  const body = request.body as { role?: string } | null;
  const role = body?.role;
  const authUser = (request as FastifyRequest & { authUser: AuthenticatedUser }).authUser;

  if (!role || !['owner', 'admin', 'member'].includes(role)) {
    return reply.status(400).send({ error: 'Invalid role. Must be one of: owner, admin, member' });
  }

  // Only owners can promote to admin or owner
  if ((role === 'admin' || role === 'owner') && authUser.role !== 'owner') {
    return reply.status(403).send({ error: 'Only owners can promote to admin or owner' });
  }

  // Cannot change your own role
  if (id === authUser.id) {
    return reply.status(400).send({ error: 'Cannot change your own role' });
  }

  // Verify target user exists
  const targetUser = await userStore.getUser(id);
  if (!targetUser) {
    return reply.status(404).send({ error: 'User not found' });
  }

  const oldRole = targetUser.role;
  const updated = await userStore.updateUserRole(id, role as 'owner' | 'admin' | 'member');

  request.log.info({
    event: 'USER.ROLE_CHANGED.SUCCESS',
    targetUserId: id, oldRole, newRole: role, changedBy: authUser.id,
  }, 'User role changed');

  return reply.send({ user: updated });
});
```

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:83-99`.
- Contract: Fetches the target user. If membership exists in the current tenant, updates the membership's role. Updates the user's role column. Returns 200.
- Key code:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:83-99
public static async Task<IResult> UpdateUserRole(
    Guid id,
    UpdateUserRoleRequest req,
    IUserRepository userRepo,
    ITenantMembershipRepository membershipRepo,
    ITenantContext tenantContext)
{
    var user = await userRepo.GetByIdAsync(id);
    if (user is null) return Results.NotFound(new { error = "User not found" });

    if (tenantContext.TenantId.HasValue)
        await membershipRepo.UpdateRoleAsync(tenantContext.TenantId.Value, id, req.Role);

    user.Role = req.Role;
    await userRepo.UpdateAsync(user);
    return Results.Ok(new { message = "Role updated" });
}
```

- Registered at `Program.cs:346`: `admin.MapPut("/users/{id}/role", AdminEndpoints.UpdateUserRole).RequireAuthorization("OwnerAccess");` — so only owners can reach it. That's *one* piece of the TS guards (the owner-promotion guard).
- What's missing vs TS:
  - Role value validation (`owner|admin|member`).
  - Self-change protection.
  - No audit log event emission.
  - No `oldRole` capture.
- What TS did NOT gate (but C# does): the outer `OwnerAccess` policy requires `owner` for any call, while TS required `requireRole('admin')` (admins reach the route) and then gated the *promotion-to-admin-or-owner* part by `authUser.role !== 'owner'`. So:
  - TS: admins can DEMOTE owners to admins (wait, no — 'owner' path is blocked; they can only reach route then must pass promotion gate). Re-reading: admin can demote an admin to member, and demote an owner to member (since 'member' is not gated by the owner-required branch).
  - C#: demotions also require owner. Slightly stricter.

## 3. The gap

Three missing guards:

1. **Role value validation**. C# accepts any string. `POST /users/{id}/role { role: "superadmin" }` writes `"superadmin"` into `user.Role`. That doesn't match any permission matrix entry, so the user loses all permissions silently (`HasPermission` returns false for unknown role per `Permissions.cs:30-32`). Near-fatal for the target.
2. **Self-change protection**. An owner can demote themself to member. If there are no other owners, the tenant has no owner, and some owner-only endpoints (`OwnerAccess`) become unreachable by anyone. Recoverable only via DB write. TS blocks this with 400.
3. **Audit log**. TS emits `USER.ROLE_CHANGED.SUCCESS` with old + new + changedBy. C# emits nothing. Compliance regression per CLAUDE.md "complete audit trail" requirement.

Additionally:
- C# uses `tenantContext.TenantId` to update membership — good. But `tenantContext.TenantId` is sourced from `X-Tenant-Id` header (or JWT `tid` claim). If the header is absent and the user has multiple memberships, the TS code didn't have this distinction — it was role-at-user-level. C# now writes to both `user.Role` and the membership role in the current tenant. This is a semantic layering change that Epic 17 (tenant model) introduced; not a TS regression per se.

Error paths:
- TS: 400 invalid role / 403 non-owner promoting / 400 self-change / 404 not found / 200 success.
- C#: 404 not found / 200 success (all other malicious inputs silently accepted).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-16/16-5-role-based-access-control.md`; `docs/stories/epic-16/16-2-user-management-api.md` (referenced in index, not read here).
- Story 16-5 §537 (referring to role changes): *"When an admin changes a user's role (Story 16.2), the user's JWT has the old role. The role change takes effect when: The user's `tamma_session` JWT expires ..."*.
- Story 16-5 does not explicitly spec the self-change-forbidden or owner-only-promotion guards.
- The TS implementation added them as defensive engineering. No story line exists for "cannot change own role".
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — defensive measures in TS

## 5. Status

- **Classification**: Incomplete (three guards + audit event missing from otherwise-present handler).
- **What's needed to finish**:
  1. Validate `req.Role` against `{owner, admin, member}` → 400 otherwise.
  2. Extract the caller's user id from `ClaimsPrincipal`; 400 if `id == callerId`.
  3. Capture `oldRole`, structured-log `{ event: USER.ROLE_CHANGED.SUCCESS, targetUserId, oldRole, newRole, changedBy }` after success.
  4. Keep the outer `OwnerAccess` policy OR relax to `AdminAccess` and add an explicit branch: *"if newRole in (admin,owner) and callerRole != owner → 403"*. The current policy is stricter than TS but blocks admins from doing admin-level demotions — should be discussed with product.
- **Is it "just a stub" or is scope missing?** Scope missing (three engineering-defense guards).
- **Blockers**: None.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs` (UpdateUserRole).
- Files to create: None.
- Tests to add:
  - `UpdateUserRole_InvalidRoleValue_Returns400`.
  - `UpdateUserRole_ChangingSelf_Returns400`.
  - `UpdateUserRole_AdminPromotingToOwner_Returns403` (if policy is relaxed to AdminAccess).
  - `UpdateUserRole_OwnerPromotingToOwner_Returns200`.
  - `UpdateUserRole_EmitsStructuredLog_WithOldAndNewRole`.
- Estimated effort: 1h
  - Guards + audit log: 30m
  - Tests (5 cases): 30m

## References

- TS source: `packages/api/src/routes/users/user-routes.ts:65-110` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:83-99`, `Program.cs:346`
- Story: `docs/stories/epic-16/16-5-role-based-access-control.md` (§537); `docs/stories/epic-16/16-2-user-management-api.md` (referenced)
- Related findings: `016-require-self-or-role-missing.md`, `019-admin-delete-user-no-cascade.md`
- CLAUDE.md section: "Self-Maintenance Goal" / "complete audit trail" — required structured logging

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `e56b04d`
- **Notes**: UpdateUserRole validates against {owner,admin,member}, blocks self-change, blocks non-owner promotions (defense-in-depth on top of the OwnerAccess route gate), emits USER.ROLE_CHANGED.SUCCESS structured log.
