# Finding 015: `GET /orgs/:id/invites` — Response Missing `invitedBy`

**Scope**: orgs
**Severity**: P3 (contract drift)
**Status**: Incomplete (partial port — one field dropped)
**Estimated port effort**: 0.25h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/orgs/index.ts`.

- File: `packages/api/src/routes/orgs/index.ts:474-506`.
- Contract/behavior: returns an array of pending invites with fields `{ id, email, role, invitedBy, expiresAt, createdAt }`. The `invitedBy` field is the UUID of the admin who created the invite; it is surfaced so the dashboard can display "invited by jane@example.com" alongside each pending invite.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/orgs/index.ts (9e9a57c~1) L474-L506
app.get<{
  Params: { tenantId: string };
}>(
  '/api/v1/orgs/:tenantId/invites',
  async (request, reply) => {
    const jwt = await getAuthenticatedUser(request, reply);
    if (!jwt) return;

    const { tenantId } = request.params;

    // Verify admin+ role
    const membership = await membershipStore.getMembership(tenantId, jwt.sub);
    if (!membership || (ROLE_HIERARCHY[membership.role] ?? 0) < (ROLE_HIERARCHY['admin'] ?? 1)) {
      return reply.status(403).send({ error: 'Requires admin role or higher' });
    }

    const invites = await membershipStore.listPendingInvites(tenantId);

    return reply.send({
      invites: invites.map((inv) => ({
        id: inv.id,
        email: inv.email,
        role: inv.role,
        invitedBy: inv.invitedBy,
        expiresAt: inv.expiresAt,
        createdAt: inv.createdAt,
      })),
    });
  },
);
```

- Dependencies: `ITenantMembershipStore.listPendingInvites(tenantId)` from `packages/api/src/persistence/tenant-membership-store.ts:304-310`.
- Tests: TS tests asserted the response shape included all six fields.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:113-117`, `apps/tamma-elsa/src/Tamma.Data/Repositories/InviteRepository.cs:29-32`.
- Contract/behavior: anonymous-object projection drops `InvitedBy` from the shape. Also no membership gate.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs (current) L113-L117
public static async Task<IResult> ListInvites(Guid tenantId, IInviteRepository inviteRepo)
{
    var invites = await inviteRepo.ListPendingByTenantAsync(tenantId);
    return Results.Ok(invites.Select(i => new { i.Id, i.Email, i.Role, i.ExpiresAt, i.CreatedAt }));
}
```

Note: no `i.InvitedBy` in the projection. Also note the response is a bare array, not `{ invites: [...] }` — a separate contract drift that the dashboard frontend would need to adapt to.

- Dependencies: `IInviteRepository.ListPendingByTenantAsync` returns the full entity (with `InvitedBy`).
- Tests: none.

## 3. The gap

Concrete behavioral difference.

- TS did: `{ "invites": [{ "id": "...", "email": "...", "role": "admin", "invitedBy": "<uuid>", "expiresAt": "...", "createdAt": "..." }] }`.
- C# does: `[{ "Id": "...", "Email": "...", "Role": "admin", "ExpiresAt": "...", "CreatedAt": "..." }]` — no wrapper, no `InvitedBy`, different casing.
- For a dashboard that wants to show "Invited by X" next to each pending invite, TS had the required UUID; C# does not return it so the dashboard has to either omit the column or make a second API call per invite to resolve it.
- In production, this is a contract-shape regression that also changes the envelope (`{ invites: [...] }` → bare array) and the casing (`invitedBy` → `InvitedBy`). The dashboard was likely written against the TS shape and would need to be updated.

Error paths:
- TS error path: `403 { "error": "Requires admin role or higher" }` when not admin of path tenant.
- C# error path: none (200 always, regardless of membership — see finding 001).

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - Task 4 Subtask 4.5: "Implement `GET /api/v1/orgs/:tenantId/invites` -- list pending invites (admin+)".
  - The story doesn't enumerate the response fields explicitly, but the model definition (Task 4 Subtask 4.1) includes `invitedBy`.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Incomplete (partial port — field missed).
- **What's needed to finish**:
  1. Add `i.InvitedBy` to the projection.
  2. Wrap the array in `{ invites: [...] }` for envelope parity with TS.
  3. Use camelCase JSON naming or reconfigure the global JSON options — or use an explicit DTO like `record PendingInviteResponse(Guid Id, string Email, string Role, Guid InvitedBy, DateTime ExpiresAt, DateTime CreatedAt)` with JSON naming policy.
  4. Add membership gate (shared with finding 001).
- **Is it "just a stub" or is scope missing?** Partial port — the repo has the column and returns the field; the endpoint just doesn't project it.
- **Blockers**: none.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` (ListInvites), possibly `apps/tamma-elsa/src/Tamma.Api/Dtos/Orgs/OrgDtos.cs` (new record).
- Files to create: `apps/tamma-elsa/tests/Tamma.Api.Tests/Orgs/ListInvitesTests.cs`.
- Tests to add:
  - `ListInvites_Response_IncludesInvitedBy`
  - `ListInvites_Response_WrappedInInvitesArray`
  - `ListInvites_Returns403_WhenCallerNotAdminOfPathTenant`
- Estimated effort: 0.25h broken down as:
  - Projection + envelope: 0.05h
  - Tests: 0.2h

## References

- TS source: `packages/api/src/routes/orgs/index.ts:474-506` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:113-117`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (Task 4 Subtasks 4.1, 4.5)
- Related findings: `001-cross-tenant-access-on-path-tenantid.md`, `016-delete-invite-swallowed-404.md`
