# Finding 022: `EnsurePersonalTenantMiddleware` — Different Slug Format, No Collision Retry, No Event

**Scope**: orgs
**Severity**: P3 (drift)
**Status**: Behavioral drift
**Estimated port effort**: 2h

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: 549f10d
- **Notes**: Slug now `u-{first-8-hex-of-userId}` matching TS exactly, with collision-retry suffix (`-1`..`-5`) and a logged-error escape hatch. Tenant name now `"{displayName}'s Workspace"` for UX parity. Existing-membership path now persists `users.tenant_id` via `UpdateActiveTenantAsync` and emits `TENANT.RESOLVED.SUCCESS`; first-login path emits `TENANT.AUTO_CREATED.SUCCESS`. Both events are best-effort (logged exceptions don't break the pipeline).

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/middleware/ensure-personal-tenant.ts`.

- File: `packages/api/src/middleware/ensure-personal-tenant.ts:32-101`.
- Contract/behavior: idempotent preHandler. Fast path: if JWT has `tenantId`, return. If user has no memberships, create a personal tenant with slug `u-<first-8-chars-of-userId>`, retry with `-1`, `-2`, ... on collision up to 5 attempts, then bail with a log; after success, call `addMember(owner)`, `updateActiveTenant`, and emit `TENANT.AUTO_CREATED.SUCCESS` with `reason: 'first_login'`. If user has existing memberships, pick the most-recently-joined and set that as active tenant; emit `TENANT.RESOLVED.SUCCESS` with `reason: 'existing_membership'`.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/middleware/ensure-personal-tenant.ts (9e9a57c~1) L69-L101
// No memberships: auto-create a personal tenant
const displayName = user.githubLogin || user.email?.split('@')[0] || 'User';
const baseSlug = `u-${jwt.sub.slice(0, 8)}`;

let slug = baseSlug;
let attempts = 0;
// Retry with suffix on collision (unlikely with UUID-based slugs)
while (await tenantStore.getTenantBySlug(slug)) {
  attempts++;
  slug = `${baseSlug}-${attempts}`;
  if (attempts > 5) {
    request.log.error({ userId: jwt.sub }, 'Failed to generate unique personal tenant slug');
    return;
  }
}

const tenant = await tenantStore.createTenant({
  name: `${displayName}'s Workspace`,
  slug,
});

await membershipStore.addMember(tenant.id, jwt.sub, 'owner');
await userStore.updateActiveTenant(jwt.sub, tenant.id);

request.log.info({
  event: 'TENANT.AUTO_CREATED.SUCCESS',
  tenantId: tenant.id,
  userId: jwt.sub,
  reason: 'first_login',
}, 'Auto-provisioned personal tenant');
```

The existing-membership path at L56-L68:

```typescript
if (existingTenants.length > 0) {
  const latest = existingTenants[existingTenants.length - 1]!;
  await userStore.updateActiveTenant(jwt.sub, latest.tenantId);

  request.log.info({
    event: 'TENANT.RESOLVED.SUCCESS',
    tenantId: latest.tenantId,
    userId: jwt.sub,
    reason: 'existing_membership',
  }, 'Resolved tenant from existing membership');
  return;
}
```

- Dependencies: `ITenantStore`, `IUserStore`, `ITenantMembershipStore`, Pino logger.
- Tests: covered all three paths (fast-path JWT has tenantId, existing-membership, auto-create-personal).

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Middleware/EnsurePersonalTenantMiddleware.cs:28-95`.
- Contract/behavior: similar skeleton but with four behavioral differences:
  1. Slug is built from the **email** (`user.Email.Split('@')[0].ToLowerInvariant().Replace(".", "-").Replace("+", "-")`) and prefixed `personal-`, suffixed with a random 8-char GUID fragment. Example: `personal-jane-doe-3a4b5c6d`.
  2. No collision retry loop — the random GUID fragment is trusted to not collide (reasonable, but different from TS).
  3. Tenant `Type = "personal"` (a field the C# port added — see finding 020 regarding `OwnerId`).
  4. No event emission on either the existing-membership path or the auto-create path.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Middleware/EnsurePersonalTenantMiddleware.cs (current) L62-L94
// Check existing memberships
var memberships = await membershipRepo.GetUserTenantsAsync(userId);
if (memberships.Count > 0)
{
    var mostRecent = memberships.OrderByDescending(m => m.JoinedAt).First();
    tenantContext.SetTenantId(mostRecent.TenantId);
    await next(context);
    return;
}

// Auto-create personal tenant
var user = await userRepo.GetByIdAsync(userId);
if (user is null)
{
    await next(context);
    return;
}

var slug = user.Email.Split('@')[0].ToLowerInvariant().Replace(".", "-").Replace("+", "-");
var tenant = await tenantRepo.CreateAsync(new Tenant
{
    Name = user.DisplayName ?? user.Email,
    Slug = $"personal-{slug}-{Guid.NewGuid().ToString()[..8]}",
    Type = "personal",
    OwnerId = userId
});

await membershipRepo.AddAsync(tenant.Id, userId, "owner");
await userRepo.UpdateActiveTenantAsync(userId, tenant.Id);
tenantContext.SetTenantId(tenant.Id);

await next(context);
```

Two additional drifts visible here:
- The "existing membership" path sets `tenantContext.SetTenantId` but does NOT update `users.tenant_id`. TS did (via `updateActiveTenant`). So on the next request, the same discovery dance happens — stateless resolution rather than caching.
- `Name = user.DisplayName ?? user.Email` vs TS `${displayName}'s Workspace` — the TS version emphasises "workspace" for UX; C# just uses the user's display name or email as the tenant name.

- Dependencies: `ITenantRepository`, `ITenantMembershipRepository`, `IUserRepository`, `ITenantContext`.
- Tests: none.

## 3. The gap

Concrete behavioral difference.

- TS did: slug `u-ab12cd34`, falls back to `u-ab12cd34-1` on collision; emits `TENANT.AUTO_CREATED.SUCCESS` and `TENANT.RESOLVED.SUCCESS`. Persists the resolved active tenant to `users.tenant_id` even on the existing-membership path.
- C# does: slug `personal-jane-doe-3a4b5c6d`; no events; existing-membership path does not persist.
- For a freshly-registered user hitting `/api/prompts` for the first time: TS creates tenant with slug `u-5a6b7c8d`; C# creates with slug `personal-jane-doe-5a6b7c8d`. Slugs are user-facing (vanity URLs per Story 18-3 L155 "enables vanity URLs (e.g., app.tamma.dev/orgs/acme-corp)") so this is a contract drift for any dashboard or link that depends on the format.
- For a user with email `jane.doe+tamma@example.com`: the C# slug becomes `personal-jane-doe-tamma-xxxxxxxx` (42+ chars), risking a collision with the 40-char slug length cap from Story 18-3 AC 2 (not currently enforced in C# — see finding 007). If enforced, the middleware would raise an error.
- For a user with a long email prefix (>30 chars): `personal-<prefix>-xxxxxxxx` may exceed the 100-char `tenants.slug` column width; current column is `HasMaxLength(100)`. Silent truncation is not configured; EF will throw on save.
- In production: the TS-era auto-provision emitted events for audit; the C# version is invisible. Users' existing-tenant resolution is recomputed on every request (small perf hit + eternal log noise).

Error paths:
- TS error path: logs error if 5 collision attempts fail; never 5xx-s the request.
- C# error path: no collision handling; if the random GUID slug collides (astronomically unlikely), EF raises a unique-index violation and the request 500s.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (Subtask 6.3: "Update `users.tenant_id` to be the 'active tenant' shortcut, set on login and org-switch") and Implementation notes L162-L175 about the `tenant_invites` rename.
- Story's acceptance criteria for this behavior:
  - No explicit AC on personal-tenant auto-provisioning. TS shipped past-spec as a DX improvement for unmigrated users (users registered before 18-3 with `tenant_id = NULL`).
  - AC 3: "Auto-create on registration: When a user registers and has no tenant, the onboarding flow prompts them to create one before proceeding" — this AC leans toward a user-initiated flow, not a silent middleware auto-creation. Both TS and C# ported the silent middleware version.
- Story alignment:
  - [ ] Matches TS behavior
  - [ ] Matches C# behavior
  - [x] Describes a third behavior (AC 3 is about onboarding UX; both implementations added invisible middleware behavior)
  - [ ] No story

## 5. Status

- **Classification**: Behavioral drift.
- **What's needed to finish**:
  1. Choose the slug format. Recommend migrating to TS's `u-<8hex>` format for parity and to stay under the 40-char Story 18-3 cap (if enforced per finding 007). Add a collision-retry loop.
  2. In the existing-membership path, persist `users.tenant_id` via `UpdateActiveTenantAsync` so subsequent requests skip this middleware's expensive discovery.
  3. Emit `TENANT.AUTO_CREATED.SUCCESS` and `TENANT.RESOLVED.SUCCESS` events.
  4. Tenant name: use `"{displayName}'s Workspace"` pattern for UX consistency.
  5. If the `Type = "personal"` field is retained, ensure it's enforced as a valid enum value (currently just a string; no CHECK).
  6. Verify whether this middleware is actually needed in the new C# model — registration now creates a tenant eagerly (or should, per AC 3). If yes, middleware should be a no-op for modern users.
- **Is it "just a stub" or is scope missing?** Scope never in a story — both impls extend past spec. Drift needs either alignment or an explicit story.
- **Blockers**: finding 007 (slug validation) may rule out email-based slugs by enforcing the 3-40 range. Finding 009 (active-tenant update in CreateOrg) overlaps with the existing-membership persistence bug.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Middleware/EnsurePersonalTenantMiddleware.cs`.
- Files to create: `apps/tamma-elsa/tests/Tamma.Api.Tests/Tenancy/EnsurePersonalTenantMiddlewareTests.cs`.
- Tests to add:
  - `Middleware_NoOps_WhenJwtAlreadyHasTid`
  - `Middleware_SetsActiveTenant_FromMostRecentMembership`
  - `Middleware_PersistsActiveTenant_ToUsersTable_OnExistingMembershipPath`
  - `Middleware_GeneratesSlug_InTsCompatibleFormat` (assert `u-<8hex>` pattern)
  - `Middleware_RetriesWithSuffix_OnSlugCollision`
  - `Middleware_EmitsAutoCreatedEvent_OnFirstLogin`
  - `Middleware_EmitsResolvedEvent_WhenPickingExistingMembership`
- Estimated effort: 2h broken down as:
  - Slug format + retry: 0.5h
  - Persist active tenant on existing-membership path: 0.25h
  - Events: 0.25h
  - Tests: 1h

## References

- TS source: `packages/api/src/middleware/ensure-personal-tenant.ts:32-101` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Middleware/EnsurePersonalTenantMiddleware.cs:28-95`
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation.md` (AC 3, Subtask 6.3)
- Related findings: `007-post-orgs-validation-missing.md`, `009-post-orgs-no-active-tenant-update.md`, `020-transfer-ownership-non-atomic.md`, `023-tenant-context-middleware-shallow.md`
