# Finding 019: Admin `DeleteUser` does not revoke API keys or unlink installations

**Scope**: auth
**Severity**: P1 (security — deleted users retain credentials)
**Status**: Incomplete
**Estimated port effort**: 1.5h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/users/user-routes.ts`.

- File: `packages/api/src/routes/users/user-routes.ts:115-144`.
- Contract: `DELETE /api/admin/users/:id` performs a three-part cascade as one transaction (logical — not DB-level):
  1. Soft-delete the user.
  2. Revoke all user API keys.
  3. Unlink from all GitHub App installations.
- Additional guards:
  - Cannot delete self (400).
  - Target must exist (404).
  - Requires `owner` role (outer `requireRole('owner')`).
- Key code:

```typescript
// packages/api/src/routes/users/user-routes.ts:115-144 (9e9a57c~1)
app.delete('/api/admin/users/:id', { preHandler: [requireRole('owner')] }, async (request, reply) => {
  const { id } = request.params as { id: string };
  const authUser = (request as FastifyRequest & { authUser: AuthenticatedUser }).authUser;

  if (id === authUser.id) {
    return reply.status(400).send({ error: 'Cannot delete yourself' });
  }

  const targetUser = await userStore.getUser(id);
  if (!targetUser) {
    return reply.status(404).send({ error: 'User not found' });
  }

  // Soft-delete user, revoke all API keys, and unlink installations
  await userStore.deleteUser(id);
  await apiKeyStore.revokeAllForUser(id);
  await userStore.unlinkAllInstallations(id);

  request.log.info({
    event: 'USER.DELETED.SUCCESS',
    targetUserId: id,
    deletedBy: authUser.id,
  }, 'User soft-deleted');

  return reply.send({ ok: true });
});
```

- Dependencies: `IUserStore.deleteUser`, `IUserApiKeyStore.revokeAllForUser`, `IUserStore.unlinkAllInstallations`.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:101-105`.
- Contract: Soft-deletes the user. That's it. No key revocation. No installation unlink. No self-delete guard. No not-found 404 (the `SoftDeleteAsync` method silently no-ops if the user doesn't exist — see `UserRepository.cs:44-53`).
- Key code (four lines in the body):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:101-105
public static async Task<IResult> DeleteUser(Guid id, IUserRepository userRepo)
{
    await userRepo.SoftDeleteAsync(id);
    return Results.Ok(new { message = "User deactivated" });
}
```

- `UserRepository.SoftDeleteAsync`:

```csharp
// apps/tamma-elsa/src/Tamma.Data/Repositories/UserRepository.cs:44-53
public async Task SoftDeleteAsync(Guid id)
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
    if (user is not null)
    {
        user.DeletedAt = DateTime.UtcNow;
        user.IsActive = false;
        await db.SaveChangesAsync();
    }
}
```

- Gated by `RequireAuthorization("OwnerAccess")` at `Program.cs:347`.
- Tests: None.

## 3. The gap

Four missing behaviors.

1. **API keys retain validity after delete**. The user's row in `api_keys` table still has `revoked_at = NULL`. Any CLI or script using a pre-deletion API key continues to authenticate as the now-soft-deleted user. The handler (`ApiKeyAuthHandler.cs:40-41`) checks `RevokedAt is not null`, not `User.DeletedAt is not null`, so the key stays live. This means:
   - A disgruntled ex-employee's personal API keys keep working until they individually expire.
   - A compromised user whose account was "deleted" in response to the compromise still has active keys in an attacker's hands.
2. **Installation links retain**. The user's rows in `user_installations` (though see Finding 023 — this table doesn't even exist in the C# schema) continue to associate them with GitHub App installations. If keys worked and installations were checked, the user could still operate on those repos.
3. **Cannot-delete-self guard missing**. An owner can delete themselves. `SoftDeleteAsync` flips `IsActive=false` and `DeletedAt=now`. After this, the `Login` endpoint's `if (!user.IsActive)` check (line 184) returns 403. The owner is locked out. Tenant has no owner. `OwnerAccess`-gated endpoints become unreachable. Recoverable only via SQL.
4. **Audit log missing**. TS emits `USER.DELETED.SUCCESS`. C# emits nothing.

Additionally, the C# response is `{ message: "User deactivated" }`; TS was `{ ok: true }`. Shape drift.

Error paths:
- TS: 400 "Cannot delete yourself" / 404 "User not found" / 200 `{ ok: true }`.
- C#: always 200 `{ message: "User deactivated" }` (even for non-existent IDs — silent).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-16/16-2-user-management-api.md` (referenced in index, not directly in scope); `docs/stories/epic-16/16-5-role-based-access-control.md`.
- No story explicitly mandates the cascade behavior. TS added it defensively.
- CLAUDE.md Security Requirements: *"Credential Management: API keys encrypted at rest... NO credentials in logs, error messages, or debug output"* — does not cover post-deletion credential revocation, but it's the implied posture.
- Story alignment:
  - [x] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — defensive cascades in TS

## 5. Status

- **Classification**: Incomplete (main action present, cascade omitted).
- **What's needed to finish**:
  1. Extract caller id from `ClaimsPrincipal`; 400 on self-delete.
  2. Fetch target; 404 if missing.
  3. After `SoftDeleteAsync(id)`, call:
     - `apiKeyRepo.RevokeAllByOwnerAsync(id.ToString())` (requires new repo method — the existing `ListByOwnerAsync` exists but no bulk-revoke).
     - An installation-unlink method — requires Finding 023 (the `user_installations` table) to be created first.
  4. Structured-log `USER.DELETED.SUCCESS` with `{ targetUserId, deletedBy }`.
  5. Change response to `{ ok: true }` for TS parity (or keep `{ message }` and update story).
- **Is it "just a stub" or is scope missing?** Scope missing (cascades + guards).
- **Blockers**: Finding 022 (`IUserRepository` missing `UnlinkAllInstallationsAsync`), Finding 023 (`user_installations` table absent).

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs` (DeleteUser), `apps/tamma-elsa/src/Tamma.Data/Repositories/IApiKeyRepository.cs` + impl (add `RevokeAllByOwnerAsync(string ownerId)`), `IUserRepository.cs` + impl (add `UnlinkAllInstallationsAsync(Guid userId)`).
- Files to create: None (depends on Finding 023 for `user_installations`).
- Tests to add:
  - `DeleteUser_Self_Returns400`.
  - `DeleteUser_Nonexistent_Returns404`.
  - `DeleteUser_Success_RevokesAllUserApiKeys`.
  - `DeleteUser_Success_UnlinksAllInstallations` (after Finding 023 lands).
  - `DeleteUser_EmitsStructuredLog`.
- Estimated effort: 1.5h
  - Cascade wiring + 2 new repo methods: 45m
  - Guards + structured log: 15m
  - Tests (5 cases): 30m

## References

- TS source: `packages/api/src/routes/users/user-routes.ts:115-144` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:101-105`, `apps/tamma-elsa/src/Tamma.Data/Repositories/UserRepository.cs:44-53`
- Story: `docs/stories/epic-16/16-2-user-management-api.md` (referenced)
- Related findings: `022-user-repository-missing-methods.md`, `023-user-installations-table-absent.md`, `018-admin-update-user-role-missing-guards.md`

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `e56b04d`
- **Notes**: DeleteUser now blocks self-delete (400), 404s on unknown id, calls RevokeAllByOwnerAsync to revoke every key owned by the user, emits USER.DELETED.SUCCESS log. Installation-unlink skipped per admin-db ruling — tenant_memberships replaces user_installations and an automated tenant-ownership unwind is out-of-scope for this finding.
