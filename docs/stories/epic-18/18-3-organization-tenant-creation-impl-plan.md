# Story 18-3: Organization / Tenant Creation — Implementation Plan

## Overview

Wire the full organization (tenant) lifecycle onto the existing persistence layer from PR #328 (`InMemoryTenantMembershipStore` / `PgTenantMembershipStore`) and Epic 17's `ITenantStore`. The route plugin at `packages/api/src/routes/orgs/index.ts` already ships the core surface — create, get, update settings, list/update/remove members, invite create/list/revoke/accept, and `/auth/switch-org`. This plan fills the remaining gaps and adds the full test matrix.

Flows in scope:

1. **Registration → create first tenant**: authenticated user with zero memberships creates a tenant via `POST /api/v1/orgs` and becomes owner.
2. **Existing user invited to second tenant**: owner/admin posts to `/orgs/:id/invites`, invitee clicks email link, accepts at `/orgs/invites/accept`, joins with the configured role.
3. **Owner lifecycle**: rename via `PATCH /orgs/:id`, transfer ownership via `POST /orgs/:id/transfer-ownership`, soft-delete via `DELETE /orgs/:id`, hard-delete via same route with `confirm` token.
4. **Personal tenant auto-provisioning**: users who registered in 18-1 without any tenant are auto-assigned a `personal` tenant on their first authenticated API call. This replaces the "null tenant" ambiguity left by 18-1.

Reference implementation style: `docs/stories/epic-9/story-9-2/9-2-provider-diagnostics-impl-plan.md`.

---

## Step-by-Step Implementation Tasks

### Task 1: Audit & Finalize Persistence Layer (2 hours)

**Files to verify**: `packages/api/src/persistence/tenant-membership-store.ts` (already exists from PR #328).

The store already exposes the full `ITenantMembershipStore` interface — `addMember`, `removeMember`, `updateMemberRole`, `listMembers`, `getUserTenants`, `getMembership`, `countOwners`, plus invite methods (`createInvite`, `getInviteByTokenHash`, `acceptInvite`, `listPendingInvites`, `revokeInvite`) and the token helpers `generateToken()` / `hashToken()`.

Gaps to close in this task:

- Add `getInviteById(id: string): Promise<TenantInvite | null>` — needed by `DELETE /orgs/:id/invites/:inviteId` for authz (must verify invite belongs to the tenant in the URL before revoking).
- Add `listTenantsWithMembership(userId: string): Promise<Array<TenantMembership & { tenant: Tenant }>>` — used by `GET /api/v1/tenants` to avoid N+1 lookups when a user is in multiple tenants.
- Confirm the Pg implementation maps `expires_at` as ISO string (not Date) for API-round-trip consistency.

No migration change: migration **017** (`017_tenant_memberships.sql`) is already merged. Do not renumber.

---

### Task 2: `POST /api/v1/tenants` — Create Tenant (3 hours)

**File to modify**: `packages/api/src/routes/orgs/index.ts` (existing `POST /api/v1/orgs` handler at line 89).

Contract:

```
POST /api/v1/tenants
Auth: Bearer <access token from 18-2>
Body: { name: string, slug: string, plan?: 'free' | 'pro' | 'enterprise' }
Response 201: { tenantId: string, name: string, slug: string, role: 'owner' }
```

Key behaviours:

1. Validate `name` (1–100 chars, trimmed) and `slug` via `SLUG_REGEX` (lowercase alphanumeric + hyphens, 3–40, not in `RESERVED_SLUGS`).
2. Call `ITenantStore.getTenantBySlug(slug)` — return 409 on collision.
3. Call `ITenantStore.createTenant({ name, slug, plan })`.
4. Call `membershipStore.addMember(tenant.id, userId, 'owner')`.
5. Update `userStore.setActiveTenant(userId, tenant.id)` so the caller's next JWT refresh picks it up.
6. Emit `TENANT.CREATED.SUCCESS` with tags `{ tenantId, userId }`.
7. Return `{ tenantId, name, slug, role: 'owner' }`.

Both `/api/v1/orgs` (legacy) and `/api/v1/tenants` (canonical) should resolve to the same handler via route aliasing — the story pivots naming to `tenants` but `orgs` stays mounted for dashboard compatibility.

---

### Task 3: `POST /api/v1/tenants/:id/invites` — Invite Member (3 hours)

**File to modify**: existing handler at line 404.

Contract:

```
POST /api/v1/tenants/:id/invites
Auth: Bearer + requireTenantRole('admin')
Body: { email: string, role: 'member' | 'admin' }
Response 201: { inviteId, email, role, expiresAt }
```

Key behaviours:

1. `requireTenant` middleware resolves `tenantMembership` on the request.
2. `requireTenantRole('admin')` gates the handler (`owner` and `admin` allowed; `member` rejected).
3. Guard: `admin` cannot issue `owner` invites — only `owner` can promote to `owner`.
4. Generate raw token via `generateToken()`; store `hashToken(raw)` only.
5. Default expiry **72 hours** (judgment call — long enough for an enterprise weekend but short enough that stale invites do not accumulate). Configurable via `TAMMA_INVITE_TTL_HOURS`.
6. Persist via `membershipStore.createInvite()` with `invitedBy = userId`.
7. Send mail via `emailService.sendEmail(buildTenantInviteEmail(...))` (helper already exists in `packages/api/src/services/email.ts:114`). Fire-and-forget with error log.
8. Emit `TENANT.MEMBER_INVITED.SUCCESS` with `{ tenantId, invitedEmail, role }`.
9. Return the invite handle **without** the raw token (token lives only in the email).

Edge cases:

- Invitee is already a member → 409 `already_member`.
- Invitee has a pending non-expired invite for the same tenant → 409 `invite_already_pending`.
- Email validation reuses the `isValidEmail()` helper from `register.ts`.

---

### Task 4: `POST /api/v1/tenants/:id/invites/:token/accept` — Accept Invite (2 hours)

**File to modify**: existing handler at line 540 (currently `/api/v1/orgs/invites/accept` — refactor to include `:id` and `:token` in the path to match the story spec).

Contract:

```
POST /api/v1/tenants/:id/invites/:token/accept
Auth: Bearer
Response 200: { tenantId, role, joinedAt }
```

Key behaviours:

1. `hashToken(token)` → `membershipStore.getInviteByTokenHash()`.
2. Reject with 404 if no match, 410 if accepted, 410 if expired, 404 if `invite.tenantId !== params.id`.
3. Email match check: the JWT user's email must equal `invite.email` (case-insensitive) — return 403 `invite_not_for_you` otherwise. Prevents invite-token stealing.
4. Transactionally: `membershipStore.addMember(invite.tenantId, userId, invite.role)` then `membershipStore.acceptInvite(invite.id)`.
5. Emit `TENANT.MEMBER_JOINED.SUCCESS`.
6. If user has no active tenant, set this one as active.

---

### Task 5: `GET /api/v1/tenants` — List My Tenants (1 hour)

**File to create**: add a new handler to `packages/api/src/routes/orgs/index.ts`.

Contract:

```
GET /api/v1/tenants
Auth: Bearer
Response 200: { tenants: Array<{ id, name, slug, plan, role, joinedAt, isActive }> }
```

Uses `listTenantsWithMembership(userId)` added in Task 1. `isActive` mirrors `users.tenant_id`. No pagination — a user will rarely belong to more than a handful of tenants.

---

### Task 6: `PATCH /api/v1/tenants/:id` — Rename & Update Settings (2 hours)

**File to modify**: existing `PUT /api/v1/orgs/:tenantId/settings` handler (line 194). Split into two:

- `PATCH /api/v1/tenants/:id` — rename only (`{ name, slug? }`). `requireTenantRole('owner')` — only the owner can rename.
- `PATCH /api/v1/tenants/:id/settings` — update billing / default provider config (`{ plan?, settings? }`). `requireTenantRole('admin')`.

If `slug` is provided, re-run slug validation and collision check before calling `tenantStore.updateTenant()`. Emit `TENANT.RENAMED.SUCCESS` with `{ tenantId, oldName, newName, oldSlug, newSlug }`.

---

### Task 7: `POST /api/v1/tenants/:id/transfer-ownership` — Transfer (2 hours)

**File to create**: new handler in `packages/api/src/routes/orgs/index.ts`.

Contract:

```
POST /api/v1/tenants/:id/transfer-ownership
Auth: Bearer + requireTenantRole('owner')
Body: { newOwnerUserId: string }
Response 200: { tenantId, previousOwnerId, newOwnerId }
```

Key behaviours:

1. Validate `newOwnerUserId` exists AND `membershipStore.getMembership(tenantId, newOwnerUserId)` is non-null. Return 400 `not_a_member` otherwise — **you cannot transfer ownership to someone who is not already a member**. The caller must invite them first.
2. Transactionally: `updateMemberRole(tenantId, oldOwnerId, 'admin')` then `updateMemberRole(tenantId, newOwnerUserId, 'owner')`.
3. Disallow transferring to self (400 `same_user`).
4. Disallow transferring if the tenant is soft-deleted.
5. Emit `TENANT.OWNERSHIP_TRANSFERRED.SUCCESS`.

Note: this is not a "co-owner" model. After transfer, the previous owner becomes `admin`. Multiple owners are not supported in 18-3 — a future story can introduce `countOwners >= 1` as the invariant.

---

### Task 8: `DELETE /api/v1/tenants/:id` — Soft & Hard Delete (3 hours)

**File to create**: new handler in `packages/api/src/routes/orgs/index.ts`.

Contract:

```
DELETE /api/v1/tenants/:id              → 202 Accepted, soft-deletes
DELETE /api/v1/tenants/:id?confirm=XXX  → 204, hard-deletes
```

Judgment call — **default is soft delete**. The `tenantStore.deleteTenant()` method already sets `deletedAt`, which is the standard path. A hard delete path exists for GDPR erasure requests and requires a two-step confirmation:

1. First `DELETE` without `confirm` returns `202 { confirmationToken, expiresAt }`. The token is a short-lived HMAC of `tenantId + userId + issuedAt`, 10-minute TTL, signed with the JWT secret. No DB write needed for the token itself.
2. Second `DELETE` with `?confirm=<token>` verifies the HMAC, then cascades deletes via Epic 17's foreign keys (`tenant_memberships`, `tenant_invites`, `github_installations`, all tenant-scoped tables). Postgres `ON DELETE CASCADE` already handles the bulk.

Guards:

- `requireTenantRole('owner')`.
- Cannot delete the **last tenant** a user belongs to unless they have already created a replacement — return 409 `last_tenant` with a hint to create a personal tenant first. This prevents orphaning a user.
- Cannot delete a tenant with active `github_installations` unless `?force=true` is also set — return 409 `has_installations`. Defensive check; the CASCADE would work, but we want explicit intent.

Emit `TENANT.DELETED.SUCCESS` (soft) or `TENANT.PURGED.SUCCESS` (hard).

---

### Task 9: Personal Tenant Auto-Provisioning Middleware (2 hours)

**File to create**: `packages/api/src/middleware/ensure-personal-tenant.ts`.

Users who registered in 18-1 before 18-3 shipped have `users.tenant_id = NULL` and zero rows in `tenant_memberships`. On their first authenticated request, this middleware:

1. Reads JWT → `userId`.
2. If `request.user.tenantId` is already set, no-op.
3. Otherwise, `membershipStore.getUserTenants(userId)` — if non-empty, pick the most-recently-joined and set `users.tenant_id` to it. Done.
4. If empty, auto-create a personal tenant: `name = "${user.name}'s Workspace"`, `slug = "u-${userId.slice(0, 8)}"` (collision-retried with a short suffix). Add user as `owner`. Set `users.tenant_id`.
5. Emit `TENANT.AUTO_CREATED.SUCCESS` with `{ tenantId, userId, reason: 'first_login' }`.
6. Attach the resolved `tenantId` to `request.user` so downstream handlers see it.

Wire this as a `preHandler` on every route under `/api/v1/*` **after** JWT verification but **before** `requireTenant`. Idempotent and cheap — a simple `users.tenant_id IS NOT NULL` check short-circuits 99% of requests.

This is a **data migration hook**, not a one-shot SQL migration. Safer than a backfill script because it handles users who registered after the deploy but before the next session.

---

### Task 10: Email Template & Service Wiring (1 hour)

The `buildTenantInviteEmail()` helper is already live in `packages/api/src/services/email.ts:114`. Verify:

- The `{{inviteUrl}}` points at `${FRONTEND_URL}/orgs/join?token=${rawToken}` — 18-5 will own the landing page, but the route must exist on day one.
- The expiry phrasing in the template matches the 72-hour default from Task 3. If `TAMMA_INVITE_TTL_HOURS` is overridden, the template should render the configured value, not a hard-coded "72 hours".

No template file to ship — the HTML is inline in `email.ts`, following the pattern from `buildVerificationEmail()` (18-1).

---

### Task 11: Route Registration & DI Wiring (1 hour)

**File to modify**: `packages/api/src/serve.ts` (or the HTTP bootstrap that mounts route plugins).

Register `registerOrgRoutes` with the existing `OrgRoutesOptions`:

```typescript
await app.register(async (fastify) => {
  await registerOrgRoutes(fastify, {
    tenantStore,
    userStore,
    membershipStore,
    emailService,
    jwtSecret: env.JWT_SECRET,
  });
});
```

Mount the `ensurePersonalTenant` preHandler globally for `/api/v1/*` excluding `/api/v1/auth/*`. Confirm the route ordering: `jwtVerify` → `ensurePersonalTenant` → `requireTenant` → `requireTenantRole`.

---

### Task 12: Tests (6 hours)

**File to extend**: `packages/api/src/routes/orgs/orgs.test.ts` (already exists).

| # | Group | Test | Assertion |
|---|-------|------|-----------|
| 1 | Create | `POST /tenants` with valid body returns 201 and owner membership | `role === 'owner'`, tenant persisted |
| 2 | Create | slug collision → 409 | `error === 'slug_taken'` |
| 3 | Create | reserved slug (`admin`, `api`) → 400 | `error === 'slug_reserved'` |
| 4 | Create | unauthenticated → 401 | no tenant created |
| 5 | List | `GET /tenants` returns all user's memberships with `isActive` flag | active tenant marked |
| 6 | Invite | owner invites admin → 201, email sent | mock email called once |
| 7 | Invite | admin cannot invite `owner` role → 403 `cannot_invite_owner` | no DB write |
| 8 | Invite | member cannot invite → 403 | RBAC enforced |
| 9 | Invite | existing member → 409 `already_member` | no invite row |
| 10 | Invite | duplicate pending invite → 409 `invite_already_pending` | single row |
| 11 | Accept | valid token → 200, membership created | correct role |
| 12 | Accept | wrong email JWT → 403 `invite_not_for_you` | no membership |
| 13 | Accept | expired token → 410 `invite_expired` | no membership |
| 14 | Accept | mismatched `:id` vs invite's tenant → 404 | no membership |
| 15 | Rename | owner renames → 200, `updatedAt` changes | `TENANT.RENAMED.SUCCESS` emitted |
| 16 | Rename | admin cannot rename → 403 | no update |
| 17 | Transfer | owner transfers to member → 200, roles swapped | old=admin, new=owner |
| 18 | Transfer | transfer to non-member → 400 `not_a_member` | no update |
| 19 | Transfer | transfer to self → 400 `same_user` | no update |
| 20 | Delete soft | owner soft-deletes → 202 + confirmationToken OR direct 202 if no force | `deletedAt` set |
| 21 | Delete hard | valid HMAC token → 204, rows cascaded | `tenant_memberships` empty |
| 22 | Delete hard | invalid/expired HMAC → 400 `confirmation_expired` | no cascade |
| 23 | Delete | last tenant guard → 409 `last_tenant` | no deletion |
| 24 | Delete | tenant with installations → 409 `has_installations` unless `?force=true` | no deletion |
| 25 | Auto-provision | first request with no memberships creates personal tenant | `u-<userId>` slug |
| 26 | Auto-provision | user with existing memberships no-ops | no new tenant |
| 27 | Switch-org | valid membership → new JWT with `tenantId` | claim updated |
| 28 | Switch-org | not a member → 403 | no JWT reissued |

**Total new/updated tests: ~28.** Run with `pnpm --filter @tamma/api test -- orgs`.

Integration tests (`packages/api/src/routes/orgs/orgs.integration.test.ts`, new): spin a Postgres testcontainer, register → verify → login → create tenant → invite second user → accept → switch-org → transfer → delete. Single happy-path flow covering the full lifecycle.

---

## RBAC Integration

Tenant-level RBAC lives entirely in `tenant_memberships.role`. The `requireTenantRole(minimumRole)` middleware at `packages/api/src/middleware/require-tenant-role.ts` already enforces the `owner > admin > member` hierarchy via the existing `ROLE_HIERARCHY` map. It requires `requireTenant` to run first to populate `request.tenantMembership`.

Platform-level RBAC (`platform_admin`) remains global and lives on the JWT's `platformRole` claim from 16-5. This story does **not** introduce a platform-level RBAC check — tenant operations are fully tenant-scoped, and a platform admin has no implicit authority over a tenant they do not belong to (unless they explicitly `/auth/switch-org` into it as a member).

Matrix:

| Action | member | admin | owner |
|--------|:------:|:-----:|:-----:|
| `GET /tenants/:id` | yes | yes | yes |
| `GET /tenants/:id/members` | yes | yes | yes |
| `POST /tenants/:id/invites` (role ≤ admin) | — | yes | yes |
| `POST /tenants/:id/invites` (role = owner) | — | — | yes |
| `PATCH /tenants/:id` (rename) | — | — | yes |
| `PATCH /tenants/:id/settings` | — | yes | yes |
| `PUT /tenants/:id/members/:uid/role` | — | yes (member↔member) | yes |
| `DELETE /tenants/:id/members/:uid` | — | yes | yes |
| `POST /tenants/:id/transfer-ownership` | — | — | yes |
| `DELETE /tenants/:id` | — | — | yes |

---

## Frontend Integration

This story is backend-only. Story 18-5 (user-facing dashboard shell) owns the UI for:

- Tenant selector / switcher in the top nav
- "Create organization" modal (calls `POST /api/v1/tenants`)
- Organization settings page (rename, transfer, delete)
- Member list + invite form
- Invite landing page (`/orgs/join?token=...`) that prompts login if needed, then calls `POST /api/v1/tenants/:id/invites/:token/accept`

The API contracts in this plan are stable — 18-5 can be built against them without further backend changes.

---

## Data Migration

No standalone SQL migration is required. Migration **017** (`017_tenant_memberships.sql`) already created `tenant_memberships` and `tenant_invites`. The existing-users backfill is handled by the `ensurePersonalTenant` middleware (Task 9), which runs lazily on first authenticated request.

For users who registered via 18-1 before this story shipped:

1. `users.tenant_id = NULL` and no rows in `tenant_memberships`.
2. First `/api/v1/*` request → middleware creates a personal tenant, adds the user as owner, updates `users.tenant_id`.
3. Subsequent requests short-circuit on the `users.tenant_id IS NOT NULL` check (one indexed lookup).

No batch backfill job is needed — the middleware converges the fleet naturally as users log in. Any user who never logs in again will never have a tenant, which is correct (they effectively remain inactive).

---

## Files to Modify

| # | File | Change |
|---|------|--------|
| 1 | `packages/api/src/persistence/tenant-membership-store.ts` | Add `getInviteById()` + `listTenantsWithMembership()` |
| 2 | `packages/api/src/routes/orgs/index.ts` | Add `PATCH /tenants/:id`, `POST /tenants/:id/transfer-ownership`, `DELETE /tenants/:id`, `GET /tenants`; refactor invite accept path to `/tenants/:id/invites/:token/accept`; add `/api/v1/tenants` aliases |
| 3 | `packages/api/src/services/email.ts` | Parameterize expiry text in `buildTenantInviteEmail()` |
| 4 | `packages/api/src/serve.ts` | Register `ensurePersonalTenant` preHandler on `/api/v1/*` |
| 5 | `packages/api/src/routes/orgs/orgs.test.ts` | Extend with the 28-test matrix |

## Files to Create

| # | File | Purpose |
|---|------|---------|
| 1 | `packages/api/src/middleware/ensure-personal-tenant.ts` | Auto-provision personal tenant on first authenticated call |
| 2 | `packages/api/src/routes/orgs/orgs.integration.test.ts` | Postgres testcontainer lifecycle test |

---

## Dependencies

- **18-1** (registration): provides `users` table with email+password, `auth_method`
- **18-2** (login): provides JWT middleware, `UnifiedJwtPayload`, refresh tokens
- **17-1** (tenant model): provides `tenants` table, `ITenantStore`, `DEFAULT_TENANT_ID`
- **17-2** (RLS): tenant-scoped isolation enforced at the DB level for cascade deletes
- **16-5** (RBAC): `UnifiedJwtPayload.platformRole` claim (not used for tenant authz but present in JWT)

---

## Estimated Effort

| Task | Hours |
|------|-------|
| 1. Audit persistence, add `getInviteById` + `listTenantsWithMembership` | 2 |
| 2. `POST /api/v1/tenants` create + owner bootstrap | 3 |
| 3. `POST /tenants/:id/invites` with role guards | 3 |
| 4. `POST /tenants/:id/invites/:token/accept` with email match | 2 |
| 5. `GET /api/v1/tenants` list | 1 |
| 6. `PATCH /tenants/:id` rename + `PATCH /tenants/:id/settings` | 2 |
| 7. `POST /tenants/:id/transfer-ownership` | 2 |
| 8. `DELETE /tenants/:id` (soft + hard with HMAC confirm) | 3 |
| 9. `ensurePersonalTenant` middleware | 2 |
| 10. Email template sanity check | 1 |
| 11. Route registration wiring | 1 |
| 12. Tests (28 unit + 1 integration happy-path) | 6 |
| **Total** | **28 hours** |

Rounds down from the 64h story estimate because PR #328 already shipped the store, the basic routes, and the role middleware. The remaining work is finishing touches, the destructive-action tasks (7 and 8), and the test matrix.

---

## Rollout

1. Ship behind `ENABLE_TENANT_MANAGEMENT` feature flag. Default `true` for new deploys, `false` for existing self-hosted installs where `DEFAULT_TENANT_ID` is the only tenant.
2. When the flag is off, `POST /api/v1/tenants` returns 403 `multi_tenant_disabled` and `ensurePersonalTenant` is a no-op — all users stay on the default sentinel tenant.
3. Staging smoke test: register → auto-provision personal tenant → create a second tenant → invite a dummy account → accept → switch-org → transfer → soft delete.

## Rollback

All endpoints are additive. If a regression is found:

1. Revert the feature flag to disable the new write paths.
2. The `ensurePersonalTenant` middleware is idempotent — reverting the code is safe. Rows it wrote are real tenants, not transient state.
3. Migration 017 stays. Dropping `tenant_memberships` would orphan installation FKs; do not touch it without a follow-up migration.
