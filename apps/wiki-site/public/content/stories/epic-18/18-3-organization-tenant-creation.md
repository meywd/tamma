---
title: "Story 18-3: Organization / Tenant Creation"
sidebar:
  order: 180
---

Status: planned

## Story

As a **registered user**,
I want to create an organization (tenant) for my team and invite members,
so that we can collaborate on repositories and share Tamma workflows within a scoped workspace.

## Acceptance Criteria

1. **Create organization** endpoint `POST /api/v1/orgs` accepts `{ name, slug }` and creates an org with the current user as `owner`
2. **Organization slug** must be unique, URL-safe (lowercase alphanumeric + hyphens, 3-40 chars), and not conflict with reserved words (`admin`, `api`, `auth`, `settings`, `app`, `www`)
3. **Auto-create on registration**: When a user registers and has no org, the onboarding flow prompts them to create one before proceeding
4. **Organization model** includes: `id`, `name`, `slug`, `plan` (free/pro/enterprise), `createdAt`, `updatedAt`
5. **Membership model** links users to orgs with roles: `owner`, `admin`, `member`; a user can belong to multiple orgs
6. **Invite members** endpoint `POST /api/v1/orgs/:orgId/invites` sends an email invitation with a join token; only `admin+` can invite
7. **Accept invite** endpoint `POST /api/v1/orgs/invites/accept` accepts `{ token }`, adds user to org with invited role
8. **List members** endpoint `GET /api/v1/orgs/:orgId/members` returns paginated member list with roles
9. **Update member role** endpoint `PUT /api/v1/orgs/:orgId/members/:userId/role` allows `owner` to change roles; `admin` can change `member` roles only
10. **Remove member** endpoint `DELETE /api/v1/orgs/:orgId/members/:userId` allows `admin+` to remove members; owners cannot remove themselves if they are the last owner
11. **Organization settings** endpoint `GET/PUT /api/v1/orgs/:orgId/settings` for name, billing plan, default provider config
12. **Org context in JWT**: After login, the JWT `orgId` claim is set to the user's active org; a `POST /api/v1/auth/switch-org` endpoint allows switching
13. **All API resources scoped to org**: Installations, workflows, settings are scoped by `orgId` in all queries
14. **Event emission**: `ORG.CREATED.SUCCESS`, `ORG.MEMBER_INVITED.SUCCESS`, `ORG.MEMBER_JOINED.SUCCESS`, `ORG.MEMBER_REMOVED.SUCCESS` events

## Tasks / Subtasks

- [ ] Task 1: Design and create organization persistence
  - [ ] Subtask 1.1: Create `Organization` interface: `{ id, name, slug, plan, createdAt, updatedAt }`
  - [ ] Subtask 1.2: Create `OrgMembership` interface: `{ orgId, userId, role, joinedAt }`
  - [ ] Subtask 1.3: Create `IOrgStore` interface: `createOrg()`, `getOrg()`, `getOrgBySlug()`, `updateOrg()`, `deleteOrg()`
  - [ ] Subtask 1.4: Add membership methods: `addMember()`, `removeMember()`, `updateMemberRole()`, `listMembers()`, `getUserOrgs()`
  - [ ] Subtask 1.5: Create `packages/api/src/persistence/org-store.ts` with `InMemoryOrgStore` and `PgOrgStore`
  - [ ] Subtask 1.6: Create database migration `20260403_create_organizations.sql`
  - [ ] Subtask 1.7: Write unit tests for all persistence methods

- [ ] Task 2: Implement organization CRUD endpoints
  - [ ] Subtask 2.1: Create `packages/api/src/routes/orgs/index.ts` route plugin
  - [ ] Subtask 2.2: Implement `POST /api/v1/orgs` — create org, add creator as owner
  - [ ] Subtask 2.3: Implement `GET /api/v1/orgs/:orgId` — get org details (members only)
  - [ ] Subtask 2.4: Implement `PUT /api/v1/orgs/:orgId/settings` — update org settings (admin+)
  - [ ] Subtask 2.5: Implement slug validation (unique, URL-safe, not reserved)
  - [ ] Subtask 2.6: Write integration tests for CRUD operations

- [ ] Task 3: Implement member management endpoints
  - [ ] Subtask 3.1: Implement `GET /api/v1/orgs/:orgId/members` — list members with pagination
  - [ ] Subtask 3.2: Implement `PUT /api/v1/orgs/:orgId/members/:userId/role` — role change with hierarchy enforcement
  - [ ] Subtask 3.3: Implement `DELETE /api/v1/orgs/:orgId/members/:userId` — remove member with last-owner protection
  - [ ] Subtask 3.4: Write tests for role hierarchy, self-removal prevention, last-owner protection

- [ ] Task 4: Implement org invite system
  - [ ] Subtask 4.1: Create `OrgInvite` model: `{ id, orgId, email, role, token, invitedBy, expiresAt, acceptedAt, createdAt }`
  - [ ] Subtask 4.2: Add invite methods to `IOrgStore`: `createInvite()`, `getInviteByToken()`, `acceptInvite()`, `listPendingInvites()`, `revokeInvite()`
  - [ ] Subtask 4.3: Implement `POST /api/v1/orgs/:orgId/invites` — send invite email (reuse email service from 18-1)
  - [ ] Subtask 4.4: Implement `POST /api/v1/orgs/invites/accept` — accept invite, add to org
  - [ ] Subtask 4.5: Implement `GET /api/v1/orgs/:orgId/invites` — list pending invites (admin+)
  - [ ] Subtask 4.6: Implement `DELETE /api/v1/orgs/:orgId/invites/:inviteId` — revoke invite (admin+)
  - [ ] Subtask 4.7: Create invite email template (HTML + plaintext)
  - [ ] Subtask 4.8: Write tests for invite flow: create, accept, expire, revoke, duplicate

- [ ] Task 5: Implement org-scoped middleware
  - [ ] Subtask 5.1: Create `packages/api/src/middleware/require-org.ts` — extracts `orgId` from JWT, verifies membership, decorates request
  - [ ] Subtask 5.2: Create `packages/api/src/middleware/require-org-role.ts` — checks user's role within the org (not global role)
  - [ ] Subtask 5.3: Implement `POST /api/v1/auth/switch-org` — validates membership, issues new JWT with different `orgId`
  - [ ] Subtask 5.4: Write tests for middleware with various role/membership scenarios

- [ ] Task 6: Migrate existing resources to org scope
  - [ ] Subtask 6.1: Add `org_id` column to `github_installations` table (nullable for migration, required for new installs)
  - [ ] Subtask 6.2: Add `org_id` column to `workflow_definitions` table
  - [ ] Subtask 6.3: Update existing queries to filter by `orgId` when present
  - [ ] Subtask 6.4: Create migration `20260404_add_org_id_to_resources.sql`
  - [ ] Subtask 6.5: Write data migration to assign existing installations to a default org

## Technical Context

### Existing Code to Modify

| File | Change |
|------|--------|
| `packages/api/src/persistence/user-store.ts` | Add `orgId` to `User` model (active org reference) |
| `packages/api/src/persistence/installation-store.ts` | Add `orgId` scoping to installation queries |
| `packages/api/src/auth/index.ts` | Add `orgId` to JWT claims |
| `packages/api/src/routes/auth/login.ts` (from 18-2) | Include `orgId` in JWT on login |

### New Files to Create

| File | Purpose |
|------|---------|
| `packages/api/src/persistence/org-store.ts` | Organization + membership + invite persistence |
| `packages/api/src/routes/orgs/index.ts` | Organization CRUD + member + invite routes |
| `packages/api/src/middleware/require-org.ts` | Org-scoped request middleware |
| `packages/api/src/middleware/require-org-role.ts` | Org-level role enforcement |
| `packages/api/src/services/email-templates/org-invite.html` | Invite email template |
| `database/migrations/20260403_create_organizations.sql` | Organizations + memberships tables |
| `database/migrations/20260404_add_org_id_to_resources.sql` | Add org_id to existing resource tables |

### Database Schema

```sql
-- Organizations
CREATE TABLE organizations (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name TEXT NOT NULL,
  slug TEXT NOT NULL UNIQUE,
  plan TEXT NOT NULL DEFAULT 'free',
  settings JSONB NOT NULL DEFAULT '{}',
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX idx_orgs_slug ON organizations(slug);

-- Org memberships
CREATE TABLE org_memberships (
  org_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
  user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
  role TEXT NOT NULL DEFAULT 'member',
  joined_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (org_id, user_id)
);

CREATE INDEX idx_org_memberships_user_id ON org_memberships(user_id);

-- Org invites
CREATE TABLE org_invites (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  org_id UUID NOT NULL REFERENCES organizations(id) ON DELETE CASCADE,
  email TEXT NOT NULL,
  role TEXT NOT NULL DEFAULT 'member',
  invite_token_hash TEXT NOT NULL UNIQUE,
  invited_by UUID NOT NULL REFERENCES users(id),
  expires_at TIMESTAMPTZ NOT NULL,
  accepted_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

### Org-Scoped RBAC

The existing `permissions.ts` RBAC system uses global roles (`member`, `admin`, `owner`). With organizations, there are now two levels of roles:

| Level | Roles | Where |
|-------|-------|-------|
| **Organization** | `owner`, `admin`, `member` | Per-org membership |
| **Platform** | `user`, `platform_admin` | Global user status (future: platform-level admin) |

For now, all RBAC checks within org-scoped endpoints use the user's role within that specific organization, not the global role. The `requireOrgRole()` middleware handles this.

### Multi-Org Support

A user can belong to multiple organizations. The JWT contains one `orgId` at a time (the "active" org). Users switch orgs via `POST /api/v1/auth/switch-org`, which reissues the JWT with the new `orgId`.

The frontend stores the active org in local state and displays an org switcher in the navigation.

## Implementation Notes

- The existing `UserInstallation` link table (user-to-installation) is being replaced by org-to-installation scoping. Installations belong to orgs, not individual users.
- The invite system in this story is org-scoped, replacing the existing admin-level invite system from `packages/api/src/routes/users/invite-routes.ts` for end-user flows.
- Reserved slugs should be defined as a constant array and checked on org creation and slug update.
- Organization deletion is not part of this story (future work with data retention policies).

## Dependencies

- **18-1**: User model with email-based authentication
- **18-2**: JWT session management with access+refresh tokens

## Estimated Effort

**Large (5 days)**:
- Day 1: Organization model + persistence + migrations + tests
- Day 2: Org CRUD endpoints + slug validation + tests
- Day 3: Member management + role hierarchy + tests
- Day 4: Org invite system + email templates + tests
- Day 5: Org-scoped middleware + resource migration + integration tests

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0.0 | Initial story creation | Architecture Team |
