# Finding 020: `POST /orgs/:id/transfer-ownership` — Non-Atomic, Dual Source of Truth

**Scope**: orgs
**Severity**: P0 (data integrity)
**Status**: Data-model regression + behavioral drift
**Estimated port effort**: 4h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/orgs/index.ts`.

- File: `packages/api/src/routes/orgs/index.ts:700-759`.
- Contract/behavior: verify requester is owner; reject transfer-to-self with `{"error":"same_user"}`; verify tenant exists and is not soft-deleted; verify new-owner is a member of the tenant (400 `{"error":"not_a_member"}` otherwise); then perform the swap purely via `tenant_memberships.role`: demote current owner to admin, promote target to owner, emit `TENANT.OWNERSHIP_TRANSFERRED.SUCCESS`. The `tenants` table in TS had **no** `owner_id` column — ownership was derived from `tenant_memberships.role = 'owner'`.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/orgs/index.ts (9e9a57c~1) L718-L758
// Verify requester is owner
const requesterMembership = await membershipStore.getMembership(tenantId, jwt.sub);
if (!requesterMembership || requesterMembership.role !== 'owner') {
  return reply.status(403).send({ error: 'Only the owner can transfer ownership' });
}

// Cannot transfer to self
if (newOwnerUserId === jwt.sub) {
  return reply.status(400).send({ error: 'same_user' });
}

// Verify tenant is not soft-deleted
const tenant = await tenantStore.getTenant(tenantId);
if (!tenant) {
  return reply.status(404).send({ error: 'Tenant not found or deleted' });
}

// Verify new owner is a member
const newOwnerMembership = await membershipStore.getMembership(tenantId, newOwnerUserId);
if (!newOwnerMembership) {
  return reply.status(400).send({ error: 'not_a_member' });
}

// Transfer: demote old owner to admin, promote new owner to owner
await membershipStore.updateMemberRole(tenantId, jwt.sub, 'admin');
await membershipStore.updateMemberRole(tenantId, newOwnerUserId, 'owner');

request.log.info({
  event: 'TENANT.OWNERSHIP_TRANSFERRED.SUCCESS',
  tenantId,
  previousOwnerId: jwt.sub,
  newOwnerId: newOwnerUserId,
}, 'Tenant ownership transferred');

return reply.send({
  tenantId,
  previousOwnerId: jwt.sub,
  newOwnerId: newOwnerUserId,
});
```

- Dependencies: `ITenantMembershipStore.{getMembership, updateMemberRole}`, `ITenantStore.getTenant`.
- Tests: `same_user`, `not_a_member`, `role !== owner`, soft-deleted tenant, success path all covered.

Note on non-atomicity in TS: the two `updateMemberRole` calls are also not wrapped in a transaction. Partial failure (e.g., DB disconnect between the two calls) could leave the tenant with two owners. This was a known lesser gap in TS; the C# port inherits it AND adds a new problem.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:177-197`, `apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs:9`, `apps/tamma-elsa/src/Tamma.Api/Program.cs:367`.
- Contract/behavior: reads `tenants.OwnerId` (a new column introduced in C# that did not exist in TS) to verify the requester is the owner, updates `tenants.OwnerId = newOwnerId`, then updates membership role for new owner to `owner` and old owner to `admin`. Three writes, none in a transaction. No check that new owner is a member. No self-transfer guard. No event emission.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs (current) L1-L20
public class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string Slug { get; set; } = null!;
    public string Type { get; set; } = "personal";
    public Guid? OwnerId { get; set; }       // ← NEW column; TS had no owner_id on tenants
    // …
}
```

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs (current) L177-L197
public static async Task<IResult> TransferOwnership(
    Guid tenantId,
    TransferOwnershipRequest req,
    ITenantRepository tenantRepo,
    ITenantMembershipRepository membershipRepo,
    ClaimsPrincipal principal)
{
    var tenant = await tenantRepo.GetByIdAsync(tenantId);
    if (tenant is null) return Results.NotFound(new { error = "Organization not found" });

    var currentOwnerId = Guid.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    if (tenant.OwnerId != currentOwnerId)
        return Results.Json(new { error = "Only the owner can transfer ownership" }, statusCode: 403);

    tenant.OwnerId = req.NewOwnerId;              // ← write #1 via UpdateAsync
    await tenantRepo.UpdateAsync(tenant);         // ← write #1 commits
    await membershipRepo.UpdateRoleAsync(tenantId, req.NewOwnerId, "owner");  // ← write #2
    await membershipRepo.UpdateRoleAsync(tenantId, currentOwnerId, "admin");  // ← write #3

    return Results.Ok(new { message = "Ownership transferred" });
}
```

- Dependencies: `ITenantRepository`, `ITenantMembershipRepository`.
- Tests: none.

## 3. The gap

Concrete behavioral difference.

- TS did: ownership lives in one place (`tenant_memberships.role`). Transfer = role swap. Validates target is a member; rejects self-transfer. Uses `membership.role === 'owner'` as the single source of truth.
- C# does: ownership lives in two places (`tenants.OwnerId` + `tenant_memberships.role`). Transfer = three writes across two tables, none in a transaction. Process killed between writes 1 and 2 leaves `tenants.OwnerId = <new>` but the membership still shows `<new>` as a regular member; write 1 succeeded but the dashboard shows "not your tenant" because `tenant_memberships.role != 'owner'`. No validation that `newOwnerId` is a member of the tenant — transfer succeeds to any arbitrary user GUID, orphaning the org (no one with an "owner" membership row).
- For a caller sending `POST /api/v1/orgs/<mine>/transfer-ownership {"newOwnerId":"00000000-0000-0000-0000-000000000000"}` (non-existent user): TS returns 400 `{"error":"not_a_member"}`; C# writes `tenants.OwnerId = 00000000-…` and tries to update a non-existent membership (no-op per `TenantMembershipRepository.UpdateRoleAsync:51-60`). Org now has `OwnerId` pointing at nobody, and no owner membership. Unrecoverable via the API without platform-admin intervention.
- For a caller sending `POST /api/v1/orgs/<mine>/transfer-ownership {"newOwnerId":"<self>"}`: TS returns 400 `{"error":"same_user"}`; C# happily demotes self to admin, then tries to promote self back to owner (no-op because already-updated-by-the-same-transaction, but on two separate contexts this could result in the old owner being admin only).
- In production: a well-intentioned "split-brain" data model where `tenants.OwnerId` and membership role can drift. Combined with the missing last-owner guard (finding 012), trivial to brick orgs.

Error paths:
- TS error paths: `403 { "error": "Only the owner can transfer ownership" }`, `400 { "error": "same_user" }`, `400 { "error": "not_a_member" }`, `404 { "error": "Tenant not found or deleted" }`.
- C# error paths: `404 { "error": "Organization not found" }`, `403 { "error": "Only the owner can transfer ownership" }`.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - Story 18-3 does not explicitly enumerate ownership transfer — it was a later addition. The closest AC is AC 9 (role change) which the story deliberately scopes as member-level.
  - However, the story's data model (AC 4: "Organization model reuses `Tenant` from Epic 17: `{ id, name, slug, plan, settings, createdAt, updatedAt, deletedAt }`" — no `ownerId`) and Story 17-1 AC 1 (the canonical `tenants` column list) establish that **ownership is not a column on tenants**. The `OwnerId` column is net-new in the C# port and breaks the Epic 17/18 data contract.
- Story alignment:
  - [ ] Matches TS behavior
  - [ ] Matches C# behavior
  - [x] Describes a third behavior (story says ownership is via membership role, not a separate column; C# added the column; both TS and C# have endpoints the story did not fully spec)
  - [ ] No story

Note: Task 3 (member management) is where transfer-ownership organically fits. Both implementations extended scope past the story ACs.

## 5. Status

- **Classification**: Data-model regression + behavioral drift.
- **What's needed to finish**:
  1. **Decision required**: drop `tenants.OwnerId` column entirely (single source of truth = membership role) OR formalize it and keep a trigger/constraint to maintain parity with membership. Recommend the former — drop the column — to align with Story 17-1 and Story 18-3.
  2. Add the missing guards: reject self-transfer (400 `same_user`); verify new owner is a member (400 `not_a_member`); verify tenant not soft-deleted.
  3. Wrap the role swap in an EF transaction: `using var tx = await db.Database.BeginTransactionAsync(); … await tx.CommitAsync();`.
  4. Emit `TENANT.OWNERSHIP_TRANSFERRED.SUCCESS`.
  5. If keeping the column, wrap all three writes in one transaction.
- **Is it "just a stub" or is scope missing?** The story left transfer-ownership underspecified; both implementations built beyond it. The port introduced a column that conflicts with the story's data model.
- **Blockers**: schema migration to drop `tenants.owner_id` (or repurpose). Impacts `ListByUserAsync`, `CreateOrg`, `EnsurePersonalTenantMiddleware`.

## Remediation

Two paths; path A is strongly preferred.

### Path A (recommended): Drop `tenants.OwnerId`

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs` (remove `OwnerId`, `Owner`).
  - `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:119-141` (remove `HasOne(e => e.Owner)` relationship).
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` (CreateOrg stops setting OwnerId; TransferOwnership becomes membership-only; DeleteOrg checks membership; GetOrg response drops OwnerId).
  - `apps/tamma-elsa/src/Tamma.Api/Middleware/EnsurePersonalTenantMiddleware.cs` (stop setting OwnerId).
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Orgs/OrgDtos.cs` (remove OwnerId from OrgResponse).
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/XXXXXXXXXXXX_DropTenantsOwnerId.cs`.
  - `apps/tamma-elsa/tests/Tamma.Api.Tests/Orgs/TransferOwnershipTests.cs`.
- Tests to add:
  - `TransferOwnership_Returns400_WhenSelfTransfer`
  - `TransferOwnership_Returns400_WhenNewOwnerNotMember`
  - `TransferOwnership_Returns403_WhenRequesterNotOwner`
  - `TransferOwnership_Returns404_WhenTenantSoftDeleted`
  - `TransferOwnership_IsAtomic_AcrossRoleSwap` (force an exception after first update, assert both roles unchanged)
  - `TransferOwnership_EmitsOwnershipTransferredEvent`

### Path B: Keep column, enforce invariant

- Add a deferred constraint / trigger that ensures `tenants.owner_id = <user>` iff `tenant_memberships.(tenant_id, user_id, role='owner')` exists.
- Wrap all 3 writes in a transaction.

- Estimated effort: 4h broken down as:
  - Path A: migration + refactor = 2.5h; transaction + guards = 0.5h; tests = 1h.

## References

- TS source: `packages/api/src/routes/orgs/index.ts:700-759` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:177-197`, `apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs:9`, `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs:119-141`, `apps/tamma-elsa/src/Tamma.Api/Program.cs:367`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (AC 4 — data model), `docs/stories/epic-17/17-1-tenant-model-database-schema.md` (AC 1 — canonical tenants columns)
- Related findings: `012-update-member-role-privilege-escalation.md`, `013-delete-member-hierarchy-missing.md`, `021-delete-org-one-phase.md`, `008-post-orgs-no-event-emission.md`
- Archived SQL migration: `database/archived-sql-migrations/008_tenants.sql` (shows no `owner_id` column)
