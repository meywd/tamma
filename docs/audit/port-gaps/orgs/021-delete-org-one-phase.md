# Finding 021: `DELETE /orgs/:id` — One-Phase Destructive, No Confirmation, No Cascade

**Scope**: orgs
**Severity**: P0 (data integrity / destructive action without safeguards)
**Status**: Semantic rewrite (TS two-phase with HMAC → C# one-phase)
**Estimated port effort**: 8h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/orgs/index.ts`.

- File: `packages/api/src/routes/orgs/index.ts:762-903`.
- Contract/behavior: two-phase delete.
  - Phase 1 (no `?confirm=`): verify requester is owner; guard against deleting the user's **only** tenant (400 `last_tenant`); soft-delete via `tenantStore.deleteTenant`; generate a 10-minute HMAC-signed confirmation token bound to `(tenantId, userId, issuedAt)` keyed by `jwtSecret`; clear the user's active tenant if it pointed at this one, switch to another; emit `TENANT.DELETED.SUCCESS`; return 202 with `{ message, confirmationToken, expiresAt }`.
  - Phase 2 (`?confirm=<token>`): validate HMAC (constant-time comparison, 10-min TTL); on success, cascade-delete all memberships, clear each removed user's active tenant, soft-delete the tenant row; emit `TENANT.PURGED.SUCCESS`; return 204.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/orgs/index.ts (9e9a57c~1) L779-L855
// Verify requester is owner
const requesterMembership = await membershipStore.getMembership(tenantId, jwt.sub);
if (!requesterMembership || requesterMembership.role !== 'owner') {
  return reply.status(403).send({ error: 'Only the owner can delete the organization' });
}

// Guard: cannot delete the last tenant the user belongs to
const userTenants = await membershipStore.getUserTenants(jwt.sub);
if (userTenants.length <= 1) {
  return reply.status(409).send({ error: 'last_tenant', message: 'Cannot delete your only organization. Create a replacement first.' });
}

const tenant = await tenantStore.getTenant(tenantId);
if (!tenant) {
  return reply.status(404).send({ error: 'Tenant not found' });
}

if (confirmToken) {
  // Hard-delete path: verify HMAC token
  const isValid = verifyDeleteConfirmation(confirmToken, tenantId, jwt.sub, options.jwtSecret);
  if (!isValid) {
    return reply.status(400).send({ error: 'confirmation_expired', message: 'Invalid or expired confirmation token' });
  }

  // Hard delete: remove memberships, invites, then tenant
  const members = await membershipStore.listMembers({ tenantId, limit: 10000, offset: 0 });
  for (const member of members.members) {
    await membershipStore.removeMember(tenantId, member.userId);
    const memberUser = await userStore.getUser(member.userId);
    if (memberUser && memberUser.tenantId === tenantId) {
      await userStore.updateActiveTenant(member.userId, null);
    }
  }

  await tenantStore.deleteTenant(tenantId);

  request.log.info({
    event: 'TENANT.PURGED.SUCCESS',
    tenantId,
    userId: jwt.sub,
  }, 'Tenant hard-deleted');

  return reply.status(204).send();
}

// Soft-delete path
await tenantStore.deleteTenant(tenantId);
const confirmation = generateDeleteConfirmation(tenantId, jwt.sub, options.jwtSecret);
// … switch active tenant …
return reply.status(202).send({
  message: 'Organization has been soft-deleted',
  confirmationToken: confirmation.token,
  expiresAt: confirmation.expiresAt,
});
```

```typescript
// packages/api/src/routes/orgs/index.ts (9e9a57c~1) L862-L903 — HMAC helpers
const DELETE_CONFIRM_TTL_MS = 10 * 60 * 1000;

function generateDeleteConfirmation(tenantId, userId, secret) {
  const issuedAt = Date.now();
  const payload = `${tenantId}:${userId}:${issuedAt}`;
  const hmac = createHmac('sha256', secret).update(payload).digest('hex');
  return { token: `${issuedAt}.${hmac}`, expiresAt: new Date(issuedAt + DELETE_CONFIRM_TTL_MS).toISOString() };
}

function verifyDeleteConfirmation(token, tenantId, userId, secret) {
  // parse issuedAt + hmac, check TTL, constant-time compare
  // …
}
```

- Dependencies: `node:crypto` `createHmac`, `ITenantMembershipStore`, `ITenantStore`, `IUserStore`.
- Tests: three buckets — soft-delete path, valid-token hard-delete path, invalid/expired token rejection.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:199-203`, `apps/tamma-elsa/src/Tamma.Data/Repositories/TenantRepository.cs:34-42`, `apps/tamma-elsa/src/Tamma.Api/Program.cs:368`.
- Contract/behavior: a single-call soft-delete with no confirmation, no last-tenant guard, no cascade, no event, no active-tenant cleanup, no hard-delete option.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs (current) L199-L203
public static async Task<IResult> DeleteOrg(Guid tenantId, ITenantRepository tenantRepo)
{
    await tenantRepo.SoftDeleteAsync(tenantId);
    return Results.Ok(new { message = "Organization deleted" });
}
```

```csharp
// apps/tamma-elsa/src/Tamma.Data/Repositories/TenantRepository.cs (current) L34-L42
public async Task SoftDeleteAsync(Guid id)
{
    var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id);
    if (tenant is not null)
    {
        tenant.DeletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}
```

- Dependencies: `ITenantRepository.SoftDeleteAsync`.
- Tests: none.

## 3. The gap

Concrete behavioral difference.

- TS did: two-phase with HMAC confirmation. A malicious or misclicked first-call soft-deletes reversibly; only an explicit second call with a freshly-generated HMAC-signed token cascades and purges. Users' active tenants are swapped to another of their memberships; victim users have their `users.tenant_id` cleared.
- C# does: one call soft-deletes with no guards. No way to hard-delete without direct DB access. No last-tenant guard — user can delete their only org and end up with `users.tenant_id` pointing at a soft-deleted row. `TenantRepository.GetByIdAsync` at `:17-18` does NOT filter on `DeletedAt`, so requests that read the tenant after delete still see it, but `TenantRepository.ListByUserAsync` joins through membership and includes deleted tenants (because memberships are not cascade-deleted).
- For a caller sending `DELETE /api/v1/orgs/<only-tenant>`: TS returns 409 `last_tenant`; C# soft-deletes, user's dashboard now shows a soft-deleted org they cannot manage.
- For a caller sending `DELETE /api/v1/orgs/<shared-tenant>`: TS soft-deletes + returns HMAC token, expects a confirm round-trip, on confirm cascades memberships and clears active-tenants of members; C# just sets `DeletedAt` — other members' memberships still point at this deleted tenant and `ListByUserAsync` returns the soft-deleted shell (with `Name` = "Unknown" depending on how the filter is applied).
- In production: easier data-corruption surface. Any "delete" is irreversible from a UX perspective (no 202 / confirm two-step) and yet corrupt (memberships and active-tenant pointers are stale). Combined with finding 001 (cross-tenant access), any caller can soft-delete any org.

Error paths:
- TS error paths: `403 { "error": "Only the owner can delete the organization" }`, `409 { "error": "last_tenant", "message": "Cannot delete your only organization. Create a replacement first." }`, `404 { "error": "Tenant not found" }`, `400 { "error": "confirmation_expired" }` (on hard-delete with invalid token), `202` (soft-delete), `204` (hard-delete).
- C# error paths: 200 always.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - The story explicitly **descopes** deletion: Implementation notes L159 — "Tenant deletion is not part of this story (future work with data retention policies). It would use `ITenantStore.deleteTenant()` (soft delete) from Epic 17."
  - So neither the two-phase HMAC nor the current single-phase flow have a formal AC. TS extended past scope; C# also extended past scope.
- Story alignment:
  - [ ] Matches TS behavior
  - [ ] Matches C# behavior
  - [x] Describes a third behavior (story says "future work")
  - [ ] No story

## 5. Status

- **Classification**: Semantic rewrite. The TS implementation chose a two-phase HMAC-confirmed flow as defense-in-depth for a destructive action; the C# port reduced it to a one-line repo call. The story intentionally left this unspecified, so "fix" needs a spec decision first.
- **What's needed to finish**:
  1. **Spec decision**: adopt the TS two-phase model as the C# contract, or write a new story that defines a different deletion UX.
  2. Implement owner-check (find finding 001 fix).
  3. Implement `last_tenant` guard via `GetUserTenantsAsync`.
  4. On soft-delete phase, generate a 10-minute HMAC token bound to `(tenantId, userId, issuedAt)` keyed by the JWT signing secret (or a separate `Delete:Secret` configuration) and return as 202.
  5. On hard-delete phase (`?confirm=<token>`), verify HMAC + TTL, cascade-remove memberships, clear affected users' `active_tenant`, cascade-delete invites, mark tenant deleted, emit `TENANT.PURGED.SUCCESS`, return 204.
  6. Emit `TENANT.DELETED.SUCCESS` on phase 1, `TENANT.PURGED.SUCCESS` on phase 2.
  7. Restrict the route to OwnerAccess policy (currently wired; finding 001 still applies — it uses platform role).
- **Is it "just a stub" or is scope missing?** Scope not in Story 18-3; TS shipped past-spec, C# trimmed far below TS.
- **Blockers**: requires new story approval or explicit decision to treat TS's shipped flow as the de-facto spec.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` (rewrite DeleteOrg).
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/ITenantMembershipRepository.cs` (add `RemoveAllByTenantAsync`).
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/IInviteRepository.cs` (add `DeleteAllByTenantAsync`).
  - `appsettings.json` (add `Delete:Secret` or reuse `Jwt:Secret`).
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Auth/DeleteConfirmationService.cs` (HMAC generate + verify, constant-time compare via `CryptographicOperations.FixedTimeEquals`).
  - `apps/tamma-elsa/tests/Tamma.Api.Tests/Orgs/DeleteOrgTests.cs`.
- Tests to add:
  - `DeleteOrg_Returns403_WhenRequesterNotOwnerOfPathTenant`
  - `DeleteOrg_Returns409_LastTenant_WhenUserHasOnlyOneTenant`
  - `DeleteOrg_Returns202WithConfirmationToken_OnSoftDeletePhase`
  - `DeleteOrg_SoftDelete_SwitchesUsersActiveTenant_WhenPointsAtDeleted`
  - `DeleteOrg_HardDelete_Returns400_WhenConfirmationTokenInvalid`
  - `DeleteOrg_HardDelete_Returns400_WhenConfirmationTokenExpired`
  - `DeleteOrg_HardDelete_RemovesAllMemberships`
  - `DeleteOrg_HardDelete_ClearsActiveTenant_ForEveryAffectedUser`
  - `DeleteOrg_HardDelete_DeletesPendingInvites`
  - `DeleteOrg_HardDelete_Returns204`
  - `DeleteOrg_EmitsTenantDeletedSuccess_OnPhase1`
  - `DeleteOrg_EmitsTenantPurgedSuccess_OnPhase2`
- Estimated effort: 8h broken down as:
  - DeleteConfirmationService (HMAC): 1h
  - Two-phase handler: 2h
  - Repository cascade methods: 1h
  - Active-tenant cleanup: 0.5h
  - Event emission: 0.5h
  - Tests (12 cases): 3h

## References

- TS source: `packages/api/src/routes/orgs/index.ts:762-903` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:199-203`, `apps/tamma-elsa/src/Tamma.Data/Repositories/TenantRepository.cs:34-42`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (Implementation notes L159 — deletion descoped)
- Related findings: `001-cross-tenant-access-on-path-tenantid.md`, `008-post-orgs-no-event-emission.md`, `020-transfer-ownership-non-atomic.md`, `013-delete-member-hierarchy-missing.md`
