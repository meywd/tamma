# Finding 011: `GET /orgs/:id/members` Missing Limit Cap and Membership Gate

**Scope**: orgs
**Severity**: P2 (correctness/DoS exposure)
**Status**: Incomplete (partial port)
**Estimated port effort**: 0.5h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/orgs/index.ts`.

- File: `packages/api/src/routes/orgs/index.ts:241-270`.
- Contract/behavior: TS required the caller to be a member of the path tenant (403 otherwise), clamped the `limit` query param to `min(parseInt, 100)` with a default of 50, and set `offset = parseInt || 0`. The response included `{ members, total, limit, offset }` so the dashboard could paginate correctly.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/orgs/index.ts (9e9a57c~1) L241-L269
app.get<{
  Params: { tenantId: string };
  Querystring: { limit?: string; offset?: string };
}>(
  '/api/v1/orgs/:tenantId/members',
  async (request, reply) => {
    const jwt = await getAuthenticatedUser(request, reply);
    if (!jwt) return;

    const { tenantId } = request.params;

    // Verify membership
    const membership = await membershipStore.getMembership(tenantId, jwt.sub);
    if (!membership) {
      return reply.status(403).send({ error: 'Not a member of this organization' });
    }

    const limit = Math.min(parseInt(request.query.limit ?? '50', 10) || 50, 100);
    const offset = parseInt(request.query.offset ?? '0', 10) || 0;

    const result = await membershipStore.listMembers({ tenantId, limit, offset });

    return reply.send({
      members: result.members,
      total: result.total,
      limit,
      offset,
    });
  },
);
```

- Dependencies: `ITenantMembershipStore.listMembers({ tenantId, limit, offset })`.
- Tests: TS tests asserted 403 on non-member, 200 with default pagination, 200 with offset, cap-at-100 on very large `limit` values.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:57-67`.
- Contract/behavior: no membership check, no limit cap, no offset clamp. Defaults are 50 / 0 but a caller can request `?limit=1000000` and receive the full members table.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs (current) L57-L67
public static async Task<IResult> ListMembers(
    Guid tenantId,
    ITenantMembershipRepository membershipRepo,
    int? limit,
    int? offset)
{
    var (members, total) = await membershipRepo.ListByTenantAsync(tenantId, limit ?? 50, offset ?? 0);
    var response = members.Select(m =>
        new MemberResponse(m.UserId, m.Role, m.JoinedAt, m.User?.DisplayName, m.User?.Email)).ToList();
    return Results.Ok(new { members = response, total });
}
```

```csharp
// apps/tamma-elsa/src/Tamma.Data/Repositories/TenantMembershipRepository.cs (current) L40-L46
public async Task<(List<TenantMembership> Members, int Total)> ListByTenantAsync(Guid tenantId, int limit, int offset)
{
    var query = db.TenantMemberships.Where(m => m.TenantId == tenantId).Include(m => m.User);
    var total = await query.CountAsync();
    var members = await query.OrderBy(m => m.JoinedAt).Skip(offset).Take(limit).ToListAsync();
    return (members, total);
}
```

- Dependencies: none.
- Tests: none.

## 3. The gap

Concrete behavioral difference — what a caller experiences.

- TS did: `?limit=1000` returned 100 members. `?limit=abc` returned 50. Non-member of tenant → 403. Response echoed pagination cursors.
- C# does: `?limit=1000000` streams the entire table into memory and into the response. `?limit=-5` results in an EF `Take(-5)` which throws `InvalidOperationException` at runtime. Non-member of tenant → 200 (finding 001).
- For a caller hitting `GET /api/v1/orgs/<any-tenant>/members?limit=10000000`: TS returned 100 members; C# attempts to allocate a list with 10M EF entity instances (or throws, depending on `Take` bounds-checking).
- In production: a small DoS vector on any authenticated request. Cross-tenant member listing is also covered by finding 001 but the missing cap is independently harmful even for legitimate callers.

Error paths:
- TS error path: `403 { "error": "Not a member of this organization" }` when not a member; always 200 otherwise (with clamped pagination).
- C# error path: 200 regardless of membership; possible `InvalidOperationException`/500 on negative `limit`.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - AC 8: "**List members** endpoint `GET /api/v1/orgs/:tenantId/members` returns paginated member list with roles".
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

The story itself doesn't call out the 100-cap explicitly, but the TS implementation ported a defensive default that the story's intent ("paginated") implies.

## 5. Status

- **Classification**: Incomplete (partial port).
- **What's needed to finish**:
  1. Add membership gate (see finding 001) — return 403 when caller is not a member.
  2. Clamp `limit` to `Math.Clamp(limit ?? 50, 1, 100)` and `offset` to `Math.Max(offset ?? 0, 0)` before calling the repository.
  3. Include `limit`, `offset` in the response body so the caller knows what it actually got.
- **Is it "just a stub" or is scope missing?** The port persisted the DB call correctly but skipped the web-layer clamping and the membership gate.
- **Blockers**: membership gate is shared with finding 001.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` (ListMembers).
- Files to create: `apps/tamma-elsa/tests/Tamma.Api.Tests/Orgs/ListMembersTests.cs`.
- Tests to add:
  - `ListMembers_ClampsLimitToHundred_WhenLargeValueRequested`
  - `ListMembers_DefaultsTo50_WhenNoLimit`
  - `ListMembers_ReturnsBadRequest_WhenNegativeOffsetOrLimit` (or clamps)
  - `ListMembers_ReturnsForbidden_WhenCallerNotMember` (shared with finding 001)
  - `ListMembers_EchoesLimitOffsetInResponse`
- Estimated effort: 0.5h broken down as:
  - Clamping + response shape: 0.1h
  - Tests: 0.4h

## References

- TS source: `packages/api/src/routes/orgs/index.ts:241-270` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:57-67`, `apps/tamma-elsa/src/Tamma.Data/Repositories/TenantMembershipRepository.cs:40-46`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (AC 8)
- Related findings: `001-cross-tenant-access-on-path-tenantid.md`
