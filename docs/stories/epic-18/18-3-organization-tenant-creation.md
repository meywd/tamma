# Story 18-3: Organization / Tenant Creation

Status: planned

## Story

As a **registered user**,
I want to create an organization (tenant) for my team and invite members,
so that we can collaborate on repositories and share Tamma workflows within a scoped workspace.

## IMPORTANT: Organization = Tenant (Epic 17 Alignment)

**This story does NOT create a separate `organizations` table.** An organization IS a tenant from Epic 17's `tenants` table (Story 17-1). Creating an organization means creating a row in `tenants`. The user-to-tenant relationship is M:N via the `tenant_memberships` table created in this story. This replaces the single `users.tenant_id` FK from Epic 17 as the canonical relationship. `users.tenant_id` remains as a nullable "active tenant" shortcut.

## Acceptance Criteria

1. **Create organization** endpoint `POST /api/v1/orgs` accepts `{ name, slug }` and creates a tenant in the `tenants` table with the current user as `owner` in `tenant_memberships`
2. **Organization slug** must be unique, URL-safe (lowercase alphanumeric + hyphens, 3-40 chars), and not conflict with reserved words (`admin`, `api`, `auth`, `settings`, `app`, `www`). Maps to `tenants.slug`.
3. **Auto-create on registration**: When a user registers and has no tenant, the onboarding flow prompts them to create one before proceeding
4. **Organization model** reuses `Tenant` from Epic 17: `{ id, name, slug, plan, settings, createdAt, updatedAt, deletedAt }`
5. **Membership model** links users to tenants with roles: `owner`, `admin`, `member`; a user can belong to multiple tenants via `tenant_memberships`
6. **Invite members** endpoint `POST /api/v1/orgs/:tenantId/invites` sends an email invitation with a join token; only `admin+` can invite
7. **Accept invite** endpoint `POST /api/v1/orgs/invites/accept` accepts `{ token }`, adds user to tenant with invited role
8. **List members** endpoint `GET /api/v1/orgs/:tenantId/members` returns paginated member list with roles
9. **Update member role** endpoint `PUT /api/v1/orgs/:tenantId/members/:userId/role` allows `owner` to change roles; `admin` can change `member` roles only
10. **Remove member** endpoint `DELETE /api/v1/orgs/:tenantId/members/:userId` allows `admin+` to remove members; owners cannot remove themselves if they are the last owner
11. **Organization settings** endpoint `GET/PUT /api/v1/orgs/:tenantId/settings` for name, billing plan, default provider config (updates `tenants.settings` and `tenants.name`)
12. **Tenant context in JWT**: After login, the JWT `tenantId` claim is set to the user's active tenant; a `POST /api/v1/auth/switch-org` endpoint allows switching
13. **All API resources scoped to tenant**: Installations, workflows, settings are scoped by `tenantId` in all queries (leveraging Epic 17's RLS)
14. **Event emission**: `TENANT.CREATED.SUCCESS`, `TENANT.MEMBER_INVITED.SUCCESS`, `TENANT.MEMBER_JOINED.SUCCESS`, `TENANT.MEMBER_REMOVED.SUCCESS` events

## Tasks / Subtasks

- [ ] Task 1: Create tenant membership persistence
  - [ ] Subtask 1.1: Reuse `Tenant` interface from `packages/shared/src/types/tenant.ts` (Epic 17). No new Organization interface needed.
  - [ ] Subtask 1.2: Create `TenantMembership` interface: `{ tenantId, userId, role, joinedAt }`
  - [ ] Subtask 1.3: Create `ITenantMembershipStore` interface: `addMember()`, `removeMember()`, `updateMemberRole()`, `listMembers()`, `getUserTenants()`
  - [ ] Subtask 1.4: Create `packages/api/src/persistence/tenant-membership-store.ts` with `InMemoryTenantMembershipStore` and `PgTenantMembershipStore`
  - [ ] Subtask 1.5: Create database migration for `tenant_memberships` table (Migration 016)
  - [ ] Subtask 1.6: Write unit tests for all persistence methods

- [ ] Task 2: Implement organization CRUD endpoints
  - [ ] Subtask 2.1: Create `packages/api/src/routes/orgs/index.ts` route plugin
  - [ ] Subtask 2.2: Implement `POST /api/v1/orgs` -- calls `ITenantStore.createTenant()` from Epic 17, then `ITenantMembershipStore.addMember()` with role=owner
  - [ ] Subtask 2.3: Implement `GET /api/v1/orgs/:tenantId` -- calls `ITenantStore.getTenant()`, verifies membership
  - [ ] Subtask 2.4: Implement `PUT /api/v1/orgs/:tenantId/settings` -- calls `ITenantStore.updateTenant()` (admin+)
  - [ ] Subtask 2.5: Implement slug validation (unique, URL-safe, not reserved) via `ITenantStore.getTenantBySlug()`
  - [ ] Subtask 2.6: Write integration tests for CRUD operations

- [ ] Task 3: Implement member management endpoints
  - [ ] Subtask 3.1: Implement `GET /api/v1/orgs/:tenantId/members` -- list members with pagination
  - [ ] Subtask 3.2: Implement `PUT /api/v1/orgs/:tenantId/members/:userId/role` -- role change with hierarchy enforcement
  - [ ] Subtask 3.3: Implement `DELETE /api/v1/orgs/:tenantId/members/:userId` -- remove member with last-owner protection
  - [ ] Subtask 3.4: Write tests for role hierarchy, self-removal prevention, last-owner protection

- [ ] Task 4: Implement tenant invite system
  - [ ] Subtask 4.1: Create `TenantInvite` model: `{ id, tenantId, email, role, token, invitedBy, expiresAt, acceptedAt, createdAt }`
  - [ ] Subtask 4.2: Add invite methods to `ITenantMembershipStore`: `createInvite()`, `getInviteByToken()`, `acceptInvite()`, `listPendingInvites()`, `revokeInvite()`
  - [ ] Subtask 4.3: Implement `POST /api/v1/orgs/:tenantId/invites` -- send invite email (reuse email service from 18-1)
  - [ ] Subtask 4.4: Implement `POST /api/v1/orgs/invites/accept` -- accept invite, add to tenant
  - [ ] Subtask 4.5: Implement `GET /api/v1/orgs/:tenantId/invites` -- list pending invites (admin+)
  - [ ] Subtask 4.6: Implement `DELETE /api/v1/orgs/:tenantId/invites/:inviteId` -- revoke invite (admin+)
  - [ ] Subtask 4.7: Create invite email template (HTML + plaintext)
  - [ ] Subtask 4.8: Write tests for invite flow: create, accept, expire, revoke, duplicate

- [ ] Task 5: Implement tenant-scoped middleware
  - [ ] Subtask 5.1: Create `packages/api/src/middleware/require-tenant.ts` -- extracts `tenantId` from JWT, verifies membership via `tenant_memberships`, decorates request
  - [ ] Subtask 5.2: Create `packages/api/src/middleware/require-tenant-role.ts` -- checks user's role within the tenant (not global role)
  - [ ] Subtask 5.3: Implement `POST /api/v1/auth/switch-org` -- validates membership, sets `users.tenant_id` to new active tenant, issues new JWT with different `tenantId`
  - [ ] Subtask 5.4: Write tests for middleware with various role/membership scenarios

- [ ] Task 6: Wire existing resources to tenant scope
  - [ ] Subtask 6.1: `github_installations` already has `tenant_id` from Epic 17 -- no new column needed
  - [ ] Subtask 6.2: Update existing queries to filter by `tenantId` from request context (leverages RLS from Story 17-2)
  - [ ] Subtask 6.3: Update `users.tenant_id` to be the "active tenant" shortcut, set on login and org-switch
  - [ ] Subtask 6.4: Write data migration to assign existing installations to the default tenant if not already assigned

## Technical Context

### Existing Code to Modify

| File | Change |
|------|--------|
| `packages/api/src/persistence/user-store.ts` | `users.tenant_id` is now the "active tenant" (nullable). No separate `orgId` field needed -- reuse `tenant_id`. |
| `packages/api/src/persistence/installation-store.ts` | Already has `tenantId` from Epic 17 -- no change needed |
| `packages/api/src/auth/index.ts` | Add `tenantId` to JWT claims (from `users.tenant_id`) |
| `packages/api/src/routes/auth/login.ts` (from 18-2) | Include `tenantId` in JWT on login |

### New Files to Create

| File | Purpose |
|------|---------|
| `packages/api/src/persistence/tenant-membership-store.ts` | Tenant membership + invite persistence |
| `packages/api/src/routes/orgs/index.ts` | Tenant CRUD + member + invite routes |
| `packages/api/src/middleware/require-tenant.ts` | Tenant-scoped request middleware |
| `packages/api/src/middleware/require-tenant-role.ts` | Tenant-level role enforcement |
| `packages/api/src/services/email-templates/tenant-invite.html` | Invite email template |
| `database/migrations/016_tenant_memberships.sql` | Tenant memberships + invites tables |

### Database Schema

**No `organizations` table is created.** This story creates only the membership and invite tables, referencing the existing `tenants` table from Epic 17 (Story 17-1):

```sql
-- Tenant memberships (M:N relationship between users and tenants)
CREATE TABLE tenant_memberships (
  tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  role TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('owner', 'admin', 'member')),
  joined_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (tenant_id, user_id)
);

CREATE INDEX idx_tenant_memberships_user_id ON tenant_memberships(user_id);

-- Tenant invites
CREATE TABLE tenant_invites (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
  email TEXT NOT NULL,
  role TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('owner', 'admin', 'member')),
  invite_token_hash TEXT NOT NULL UNIQUE,
  invited_by UUID NOT NULL REFERENCES users(id),
  expires_at TIMESTAMPTZ NOT NULL,
  accepted_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_tenant_invites_tenant_id ON tenant_invites(tenant_id);
CREATE INDEX idx_tenant_invites_email ON tenant_invites(email);
```

### Tenant-Scoped RBAC

The existing `permissions.ts` RBAC system uses global roles (`member`, `admin`, `owner`). With tenant memberships, there are now two levels of roles:

| Level | Roles | Where |
|-------|-------|-------|
| **Tenant** | `owner`, `admin`, `member` | Per-tenant membership in `tenant_memberships` |
| **Platform** | `user`, `platform_admin` | Global user status (future: platform-level admin) |

For now, all RBAC checks within tenant-scoped endpoints use the user's role within that specific tenant, not the global role. The `requireTenantRole()` middleware handles this.

### Multi-Tenant Support

A user can belong to multiple tenants. The JWT contains one `tenantId` at a time (the "active" tenant, from `users.tenant_id`). Users switch tenants via `POST /api/v1/auth/switch-org`, which:
1. Validates the user is a member of the target tenant via `tenant_memberships`
2. Updates `users.tenant_id` to the new active tenant
3. Reissues the JWT with the new `tenantId`

The frontend stores the active tenant in local state and displays a tenant switcher in the navigation.

## Implementation Notes

- **No parallel organizations table**: The `tenants` table from Epic 17 IS the organization table. This story extends it with M:N membership. The `tenants.slug` enables vanity URLs (e.g., `app.tamma.dev/orgs/acme-corp`).
- The existing `UserInstallation` link table (user-to-installation) is being replaced by tenant-to-installation scoping. Installations belong to tenants, not individual users.
- The invite system in this story is tenant-scoped, replacing the existing admin-level invite system from `packages/api/src/routes/users/invite-routes.ts` for end-user flows.
- Reserved slugs should be defined as a constant array and checked on tenant creation and slug update.
- Tenant deletion is not part of this story (future work with data retention policies). It would use `ITenantStore.deleteTenant()` (soft delete) from Epic 17.
- The `POST /api/v1/orgs` endpoint calls `ITenantStore.createTenant()` -- there is no separate "organization" store. The `ITenantMembershipStore` handles only memberships and invites.

### Invite Table Migration: `user_invites` -> `tenant_invites`

The `tenant_invites` table defined in this story **replaces** the existing `user_invites` table (from `packages/api/src/persistence/invite-store.ts`). The legacy `user_invites` table was platform-scoped (admin invites a user to the platform). The new `tenant_invites` table is tenant-scoped (tenant admin invites a user to a specific tenant).

**Migration steps:**

1. Create the new `tenant_invites` table (as defined in the Database Schema section above).
2. Migrate pending (non-expired, non-accepted) invites from `user_invites` to `tenant_invites`:
   - Map each `user_invites` row to the default tenant (created during the tenant migration in Epic 17).
   - Preserve the `email`, `role`, `invite_token_hash`, `invited_by`, `expires_at`, and `accepted_at` fields.
   - Set `tenant_id` to the default tenant's ID.
3. Update `packages/api/src/persistence/invite-store.ts` to delegate to the new `ITenantMembershipStore` invite methods.
4. After confirming all invite flows use `tenant_invites`, drop the `user_invites` table in a subsequent migration.
5. Update `packages/api/src/routes/users/invite-routes.ts` to redirect or proxy to the new tenant invite endpoints.

## Dependencies

- **Epic 17 Story 17-1** (Tenant Model): The `tenants` table must exist with `ITenantStore` interface and implementations
- **18-1**: User model with email-based authentication
- **18-2**: JWT session management with access+refresh tokens

## Migration Number

This story uses **migration 016** (`016_tenant_memberships.sql`). See `/docs/stories/migration-ordering.md` for the cross-epic migration sequence.

## Estimated Effort

**Large (8 days)** (revised from 5 days -- complexity due to tenant model integration):
- Day 1: Tenant membership model + persistence + migration + tests
- Day 2: Org CRUD endpoints (delegating to ITenantStore) + slug validation + tests
- Day 3: Member management + role hierarchy + tests
- Day 4: Tenant invite system + email templates + tests
- Day 5: Tenant-scoped middleware (require-tenant, require-tenant-role)
- Day 6: Resource wiring (installations, workflows to tenant scope) + RLS integration
- Day 7: Invite migration (user_invites -> tenant_invites) + backward compatibility
- Day 8: Integration tests + security review + edge case testing

> **Note**: Revised from 5d to 8d. The additional 3 days account for: (1) integration with Epic 17's tenant model and RLS, (2) migrating the existing invite system, and (3) ensuring the JWT tenantId claim works end-to-end across both OAuth flows.

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0.0 | Initial story creation | Architecture Team |
| 2026-04-09 | 2.0.0 | Aligned with Epic 17: organization = tenant. Removed `organizations` table, use `tenants` from 17-1. Renamed `org_memberships` to `tenant_memberships`. Changed all `org_id` to `tenant_id`. Added dependency on 17-1. Assigned migration 016. | Cross-epic review |
