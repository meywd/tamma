# Finding 019: `GET /tenants` — Response Missing `role` / `joinedAt` / `isActive`

**Scope**: orgs
**Severity**: P2 (contract drift)
**Status**: Incomplete (partial port — fields dropped)
**Estimated port effort**: 1h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/orgs/index.ts`.

- File: `packages/api/src/routes/orgs/index.ts:670-698`.
- Contract/behavior: returns `{ tenants: [{ id, name, slug, plan, role, joinedAt, isActive }] }` where `role` is the caller's role in that tenant, `joinedAt` is the membership timestamp, and `isActive` indicates whether this is the caller's current active tenant (`m.tenantId === jwt.tenantId`).
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/orgs/index.ts (9e9a57c~1) L670-L698
app.get(
  '/api/v1/tenants',
  async (request, reply) => {
    const jwt = await getAuthenticatedUser(request, reply);
    if (!jwt) return;

    const memberships = await membershipStore.getUserTenants(jwt.sub);

    const tenants = await Promise.all(
      memberships.map(async (m) => {
        const tenant = await tenantStore.getTenant(m.tenantId);
        return {
          id: m.tenantId,
          name: tenant?.name ?? 'Unknown',
          slug: tenant?.slug ?? '',
          plan: tenant?.plan ?? 'free',
          role: m.role,
          joinedAt: m.joinedAt,
          isActive: m.tenantId === jwt.tenantId,
        };
      }),
    );

    return reply.send({ tenants });
  },
);
```

- Dependencies: `ITenantMembershipStore.getUserTenants`, `ITenantStore.getTenant`.
- Tests: asserted the `role`, `joinedAt`, `isActive` fields round-tripped.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:167-175`, `apps/tamma-elsa/src/Tamma.Data/Repositories/TenantRepository.cs:44-51`.
- Contract/behavior: returns a bare array of `OrgResponse(Guid Id, string Name, string Slug, string Type, Guid? OwnerId, string Settings, DateTime CreatedAt)` — no `role`, no `joinedAt`, no `isActive`, no `plan`. Dashboard cannot tell which tenant is the caller's active one, cannot display role per tenant, cannot sort by join date.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs (current) L167-L175
public static async Task<IResult> ListTenants(
    ITenantRepository tenantRepo,
    ClaimsPrincipal principal)
{
    var userId = Guid.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    var tenants = await tenantRepo.ListByUserAsync(userId);
    return Results.Ok(tenants.Select(t =>
        new OrgResponse(t.Id, t.Name, t.Slug, t.Type, t.OwnerId, t.Settings, t.CreatedAt)));
}
```

```csharp
// apps/tamma-elsa/src/Tamma.Data/Repositories/TenantRepository.cs (current) L44-L51
public async Task<List<Tenant>> ListByUserAsync(Guid userId)
{
    return await db.TenantMemberships
        .Where(m => m.UserId == userId)
        .Include(m => m.Tenant)
        .Select(m => m.Tenant)    // ← drops the membership (role, joinedAt)
        .ToListAsync();
}
```

Note: the repo throws away the `TenantMembership` rows it queried to filter; it hands back bare `Tenant` entities. The handler has no access to role or joinedAt.

- Dependencies: `ITenantRepository.ListByUserAsync`.
- Tests: none.

## 3. The gap

Concrete behavioral difference.

- TS did: full `{ tenants: [{ id, name, slug, plan, role, joinedAt, isActive }] }`.
- C# does: bare `[{ Id, Name, Slug, Type, OwnerId, Settings, CreatedAt }]`.
- For the dashboard's left-rail org switcher: TS had everything needed to render the list with role badges and a checkmark on the active org. C# is missing the role, the active-tenant marker, and the membership timestamp.
- In production, the dashboard must either (a) hard-code "member" as the role, (b) make N+1 API calls to `GetMembership` for each tenant, or (c) be rewritten to use a different endpoint.

Error paths:
- n/a — this is a happy-path field-set regression.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - Implementation notes L143-L145: "The JWT contains one `tenantId` at a time (the 'active' tenant, from `users.tenant_id`). Users switch tenants via `POST /api/v1/auth/switch-org` … The frontend stores the active tenant in local state and displays a tenant switcher in the navigation."
  - AC 5: "Membership model links users to tenants with roles: `owner`, `admin`, `member`; a user can belong to multiple tenants via `tenant_memberships`" — implies tenant list needs role + isActive.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Incomplete (partial port).
- **What's needed to finish**:
  1. Change `TenantRepository.ListByUserAsync` to return `List<(Tenant Tenant, string Role, DateTime JoinedAt)>` (or a new DTO).
  2. In `ListTenants`, project to a new `TenantMembershipResponse(Guid Id, string Name, string Slug, string Plan, string Role, DateTime JoinedAt, bool IsActive)` DTO.
  3. Read the caller's active tenant from `ClaimsPrincipal.FindFirst("tid")?.Value` to set `IsActive`.
  4. Wrap in `{ tenants: [...] }` for envelope parity with TS.
  5. Add `Plan` field (also dropped).
- **Is it "just a stub" or is scope missing?** Port simplified the response. The underlying repository is already iterating `TenantMemberships`, so the data is there — just not projected.
- **Blockers**: none.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/ITenantRepository.cs` (change `ListByUserAsync` signature or add `ListMembershipsByUserAsync`).
  - `apps/tamma-elsa/src/Tamma.Data/Repositories/TenantRepository.cs`.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` (ListTenants).
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Orgs/OrgDtos.cs` (new DTO).
- Files to create: `apps/tamma-elsa/tests/Tamma.Api.Tests/Orgs/ListTenantsTests.cs`.
- Tests to add:
  - `ListTenants_Response_IncludesRolePerTenant`
  - `ListTenants_Response_IncludesJoinedAtPerTenant`
  - `ListTenants_Response_MarksActiveTenantIsActiveTrue`
  - `ListTenants_Response_IncludesPlan`
  - `ListTenants_ReturnsEmptyList_WhenUserHasNoMemberships`
- Estimated effort: 1h broken down as:
  - Repo + DTO: 0.5h
  - Tests: 0.5h

## References

- TS source: `packages/api/src/routes/orgs/index.ts:670-698` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:167-175`, `apps/tamma-elsa/src/Tamma.Data/Repositories/TenantRepository.cs:44-51`, `apps/tamma-elsa/src/Tamma.Api/Dtos/Orgs/OrgDtos.cs:10`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (AC 5, Implementation notes)
- Related findings: `018-switch-org-no-cookie.md`
