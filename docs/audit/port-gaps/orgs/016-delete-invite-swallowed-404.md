# Finding 016: `DELETE /orgs/:id/invites/:iid` — Swallowed 404

**Scope**: orgs
**Severity**: P3 (contract drift)
**Status**: Behavioral drift
**Estimated port effort**: 0.25h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/orgs/index.ts`.

- File: `packages/api/src/routes/orgs/index.ts:508-535`.
- Contract/behavior: `membershipStore.revokeInvite(inviteId)` throws when the invite doesn't exist; the route catches the throw and returns 404. Admin+ membership of path tenant is required up-front (403 otherwise).
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/orgs/index.ts (9e9a57c~1) L508-L535
app.delete<{
  Params: { tenantId: string; inviteId: string };
}>(
  '/api/v1/orgs/:tenantId/invites/:inviteId',
  async (request, reply) => {
    const jwt = await getAuthenticatedUser(request, reply);
    if (!jwt) return;

    const { tenantId, inviteId } = request.params;

    // Verify admin+ role
    const membership = await membershipStore.getMembership(tenantId, jwt.sub);
    if (!membership || (ROLE_HIERARCHY[membership.role] ?? 0) < (ROLE_HIERARCHY['admin'] ?? 1)) {
      return reply.status(403).send({ error: 'Requires admin role or higher' });
    }

    try {
      await membershipStore.revokeInvite(inviteId);
    } catch {
      return reply.status(404).send({ error: 'Invite not found' });
    }

    return reply.send({ ok: true });
  },
);
```

The store's `revokeInvite` throws when `rowCount === 0`:

```typescript
// packages/api/src/persistence/tenant-membership-store.ts (9e9a57c~1) L312-L320
async revokeInvite(id: string): Promise<void> {
  const result = await this.pool.query(
    'DELETE FROM tenant_invites WHERE id = $1',
    [id],
  );
  if (result.rowCount === 0) {
    throw new Error('Invite not found');
  }
}
```

- Dependencies: `ITenantMembershipStore.revokeInvite`.
- Tests: `assert 404 on unknown invite id`.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:119-123`, `apps/tamma-elsa/src/Tamma.Data/Repositories/InviteRepository.cs:34-42`.
- Contract/behavior: `InviteRepository.DeleteAsync(id)` silently no-ops if the invite doesn't exist (`db.UserInvites.FindAsync(id)` returns null → early return). The endpoint always returns 200 with `{ message = "Invite deleted" }` regardless of whether anything was actually deleted. Also no scoping by `tenantId`: deleting `/api/v1/orgs/<tenant-A>/invites/<invite-from-tenant-B>` succeeds as long as the GUID matches any invite in the DB.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs (current) L119-L123
public static async Task<IResult> DeleteInvite(Guid tenantId, Guid inviteId, IInviteRepository inviteRepo)
{
    await inviteRepo.DeleteAsync(inviteId);
    return Results.Ok(new { message = "Invite deleted" });
}
```

```csharp
// apps/tamma-elsa/src/Tamma.Data/Repositories/InviteRepository.cs (current) L34-L42
public async Task DeleteAsync(Guid id)
{
    var invite = await db.UserInvites.FindAsync(id);
    if (invite is not null)
    {
        db.UserInvites.Remove(invite);
        await db.SaveChangesAsync();
    }
}
```

Note: the `tenantId` route parameter is accepted but unused — the deletion is keyed on `inviteId` alone.

- Dependencies: `IInviteRepository`.
- Tests: none.

## 3. The gap

Concrete behavioral difference.

- TS did: 404 on unknown invite id. 403 on non-admin.
- C# does: 200 on unknown invite id (idempotent-delete semantics). 200 from any tenant even on an invite belonging to another tenant. No role gate beyond platform `AdminAccess`.
- For a caller polling `DELETE /orgs/:id/invites/<random-guid>`: TS returns 404 (valuable signal); C# returns 200 (no signal about existence).
- For a cross-tenant attacker: `DELETE /api/v1/orgs/<mine>/invites/<victim-invite-id>` succeeds, revoking a pending invite to someone else's org.
- In production: minor contract drift for the success path; more serious cross-tenant revoke for the attack path (but the attacker needs to know the invite's UUID, which is not leaked publicly).

Error paths:
- TS error path: `404 { "error": "Invite not found" }`, `403 { "error": "Requires admin role or higher" }`.
- C# error path: always 200.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - Task 4 Subtask 4.6: "Implement `DELETE /api/v1/orgs/:tenantId/invites/:inviteId` -- revoke invite (admin+)".
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

Story doesn't explicitly specify 404-on-missing, but TS behavior is the strict interpretation of Task 4 Subtask 4.6.

## 5. Status

- **Classification**: Behavioral drift.
- **What's needed to finish**:
  1. In `DeleteInvite`, load the invite first via `GetByIdAsync` (add to `IInviteRepository`) and verify `invite.TenantId == tenantId`; return 404 otherwise.
  2. Return 404 when the repository reports zero rows affected, not 200.
  3. Add admin+ of path tenant gate.
- **Is it "just a stub" or is scope missing?** Partial port; the "return OK regardless" shape is a convention drift.
- **Blockers**: tied to finding 001 (membership gate).

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/IInviteRepository.cs` (add `GetByIdAsync`, or change `DeleteAsync` to accept `(tenantId, id)` and return a `bool`).
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/InviteRepository.cs`.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` (DeleteInvite).
- Files to create: `apps/tamma-elsa/tests/Tamma.Api.Tests/Orgs/DeleteInviteTests.cs`.
- Tests to add:
  - `DeleteInvite_Returns404_WhenInviteDoesNotExist`
  - `DeleteInvite_Returns404_WhenInviteBelongsToOtherTenant`
  - `DeleteInvite_Returns403_WhenCallerNotAdminOfPathTenant`
  - `DeleteInvite_Returns200_WhenSuccessful`
- Estimated effort: 0.25h broken down as:
  - Signature change + handler: 0.1h
  - Tests: 0.15h

## References

- TS source: `packages/api/src/routes/orgs/index.ts:508-535`, `packages/api/src/persistence/tenant-membership-store.ts:312-320` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:119-123`, `apps/tamma-elsa/src/Tamma.Data/Repositories/InviteRepository.cs:34-42`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (Task 4 Subtask 4.6)
- Related findings: `001-cross-tenant-access-on-path-tenantid.md`, `014-create-invite-weak-token-no-email.md`, `017-accept-invite-no-active-tenant.md`
