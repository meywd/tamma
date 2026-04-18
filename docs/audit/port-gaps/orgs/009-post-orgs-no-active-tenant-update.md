# Finding 009: `POST /orgs` Does Not Call `UpdateActiveTenantAsync`

**Scope**: orgs
**Severity**: P2 (correctness)
**Status**: Incomplete (partial port — persistence call dropped)
**Estimated port effort**: 0.25h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/orgs/index.ts`.

- File: `packages/api/src/routes/orgs/index.ts:127-144`.
- Contract/behavior: after creating the tenant and adding the current user as `owner`, TS called `userStore.updateActiveTenant(jwt.sub, tenant.id)` so the new tenant became the user's active tenant. On the next request, the JWT re-issue path (or `ensurePersonalTenant` middleware short-circuit) would see `users.tenant_id` set to the new tenant and populate the `tid` claim accordingly.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/orgs/index.ts (9e9a57c~1) L127-L144
// Create tenant
const tenant = await tenantStore.createTenant({
  name: name.trim(),
  slug,
});

// Add current user as owner
await membershipStore.addMember(tenant.id, jwt.sub, 'owner');

// Set as active tenant
await userStore.updateActiveTenant(jwt.sub, tenant.id);

request.log.info({
  event: 'TENANT.CREATED.SUCCESS',
  tenantId: tenant.id,
  userId: jwt.sub,
}, 'Organization created');
```

- Dependencies: `IUserStore.updateActiveTenant(userId, tenantId | null)` from `packages/api/src/persistence/user-store.ts`.
- Tests: TS tests asserted `user.tenantId === createdTenant.id` after create.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:12-36`.
- Contract/behavior: `CreateOrg` takes `IUserRepository userRepo` as a parameter (signals the intent) but never uses it. `userRepo.UpdateActiveTenantAsync(userId, tenant.Id)` is not invoked.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs (current) L12-L36
public static async Task<IResult> CreateOrg(
    CreateOrgRequest req,
    ITenantRepository tenantRepo,
    ITenantMembershipRepository membershipRepo,
    IUserRepository userRepo,            // ← injected …
    ClaimsPrincipal principal)
{
    var userId = Guid.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);

    var existing = await tenantRepo.GetBySlugAsync(req.Slug);
    if (existing is not null)
        return Results.Conflict(new { error = "Slug already taken" });

    var tenant = await tenantRepo.CreateAsync(new Tenant
    {
        Name = req.Name,
        Slug = req.Slug.ToLowerInvariant(),
        Type = "org",
        OwnerId = userId
    });

    await membershipRepo.AddAsync(tenant.Id, userId, "owner");
    // ← …but never called. No userRepo.UpdateActiveTenantAsync(userId, tenant.Id).
    return Results.Created($"/api/v1/orgs/{tenant.Id}",
        new OrgResponse(tenant.Id, tenant.Name, tenant.Slug, tenant.Type, tenant.OwnerId, tenant.Settings, tenant.CreatedAt));
}
```

- Dependencies: `IUserRepository.UpdateActiveTenantAsync(Guid userId, Guid tenantId)` exists (used by `SwitchOrg` L161). Not called here.
- Tests: none.

## 3. The gap

Concrete behavioral difference — what the next request sees.

- TS did: after create, `users.tenant_id` pointed at the new tenant. The dashboard could re-issue the JWT with the new `tid` claim or read `users.tenant_id` for display.
- C# does: `users.tenant_id` remains unchanged (likely NULL for a freshly-registered user, or still pointing at a prior personal tenant). The user "created an org" but the system still treats their previous tenant as active. The dashboard's next API call carries the stale `tid` claim and queries against the old tenant.
- For a caller that just called `POST /orgs` and now calls `GET /api/v1/orgs/<newTenantId>/members`: TS returns 200 (because the active tenant changed and `jwt.tid` matches); C# may return 403 (finding 001) or query against the old tenant depending on downstream handler logic.
- In production: the onboarding flow described in Story 18-3 AC 3 ("Auto-create on registration: When a user registers and has no tenant, the onboarding flow prompts them to create one before proceeding") breaks. After create, the user still has no "active tenant" and the next page load fails to find their newly-created org.

Error paths:
- n/a — this is a happy-path regression.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md`.
- Story's acceptance criteria for this behavior:
  - AC 3: "**Auto-create on registration**: When a user registers and has no tenant, the onboarding flow prompts them to create one before proceeding" — implies the created org becomes active.
  - Implementation notes L143-L145 on multi-tenant support: "The JWT contains one `tenantId` at a time (the 'active' tenant, from `users.tenant_id`). Users switch tenants via `POST /api/v1/auth/switch-org`, which: 1. Validates the user is a member of the target tenant via `tenant_memberships` 2. Updates `users.tenant_id` to the new active tenant 3. Reissues the JWT with the new `tenantId`".
  - Subtask 6.3: "Update `users.tenant_id` to be the 'active tenant' shortcut, set on login and org-switch".
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS)
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [ ] No story

## 5. Status

- **Classification**: Incomplete (partial port — `IUserRepository` was injected, the step was simply forgotten).
- **What's needed to finish**:
  1. After `membershipRepo.AddAsync(tenant.Id, userId, "owner")`, call `await userRepo.UpdateActiveTenantAsync(userId, tenant.Id)`.
  2. Issue a fresh JWT with the new `tid` claim in the response body (or on a cookie — see finding 018 for the parallel gap).
  3. Assert in tests that `users.tenant_id` is updated after create.
- **Is it "just a stub" or is scope missing?** Just a missed line; `userRepo` is right there in the constructor.
- **Blockers**: none.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs`.
- Files to create: `apps/tamma-elsa/tests/Tamma.Api.Tests/Orgs/CreateOrgActiveTenantTests.cs`.
- Tests to add:
  - `CreateOrg_UpdatesUsersActiveTenantId_ToNewTenant`
  - `CreateOrg_ReIssuesJwt_WithNewTidClaim` (if we also fix finding 018's class of bugs here)
- Estimated effort: 0.25h broken down as:
  - Add single line: 0.05h
  - Test: 0.2h

## References

- TS source: `packages/api/src/routes/orgs/index.ts:127-144` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs:12-36`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (AC 3, Subtask 6.3)
- Related findings: `018-switch-org-no-cookie.md`, `022-personal-tenant-slug-drift.md`
