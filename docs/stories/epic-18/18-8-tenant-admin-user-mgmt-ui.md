# Story 18-8: Tenant-Admin User Management UI

Status: todo (planning brief, 2026-04-21)

## Story

As a **tenant admin (role = admin or owner)**,
I want pages in `dash.tamma.dev` where I can see every member of my
tenant, invite new users by email, change a member's role, remove a
member, transfer ownership, view + revoke + resend pending invites,
and inspect a tenant-scoped audit log of all of those actions,
so that I can run day-to-day tenant administration without asking a
platform admin and without touching the API directly.

## Narrative

The gap audit
([`plans/tenant-user-mgmt-audit.md`](../plans/tenant-user-mgmt-audit.md))
shows the backend is 90% done: every hierarchy-respecting mutation,
plus the last-owner guards, plus ownership transfer with atomic tx,
all live in `OrgEndpoints.cs`. The only thing missing is a real UI —
18-5 Task 6.3 is a one-liner (`OrgSettings page: ... member
management (uses 18-3 APIs)`) that never got ACs or wireframes.

This story owns the full tenant-admin user-management UI surface
inside the user dashboard shell (18-5). Story 18-7 lands the tiny
backend completions this UI depends on (resend-invite endpoint,
audit-view endpoint, role-change event emission).

## Acceptance Criteria

1. Route `dash.tamma.dev/settings/organization/members` renders a
   members table with columns: display name, email, role badge
   (owner / admin / member), joined-at, menu (open actions). Data from
   `GET /api/v1/orgs/{tenantId}/members` with client-side search +
   server-side pagination (50/page).
2. "Invite member" primary action opens a drawer with fields:
   `email` (validated), `role` (select: member / admin / owner — owner
   only if current user is owner), `message` (optional, currently
   ignored on backend, pre-emptively in UI for future). Submits to
   `POST /api/v1/orgs/{tenantId}/invites`. On success, toast + drawer
   closes + pending-invites list refreshes.
3. Row action "Change role" opens an inline dialog with role options
   filtered by the caller's role (admin cannot promote to admin/owner;
   owner sees every option). PATCH to
   `/api/v1/orgs/{tenantId}/members/{userId}/role`. Error copy maps
   the backend's four 403 shapes to user-readable messages:
   - "Only owners can change owner-level roles"
   - "Cannot change role of users at or above your level"
   - "Cannot promote users to or above your level"
   - "Cannot remove the last owner" (400 on last-owner demote)
4. Row action "Remove from organization" opens a confirm dialog with
   explicit copy: "X will lose access to {tenantName}. Their API
   keys and workflow assignments are revoked." DELETE to
   `/api/v1/orgs/{tenantId}/members/{userId}`. Last-owner guard 400
   renders inline.
5. **Pending invites** section on the same page renders invites from
   `GET /api/v1/orgs/{tenantId}/invites` with columns: email, role,
   invited by, expires at (relative), menu. Empty state: "No pending
   invites."
   - Row action "Resend" → `POST /api/v1/orgs/{tenantId}/invites/{inviteId}/resend`
     (Story 18-7 endpoint); toast on success; 429 rate-limit banner
     if hit.
   - Row action "Revoke" → `DELETE /api/v1/orgs/{tenantId}/invites/{inviteId}`
     with confirm dialog.
6. **Transfer ownership** — separate dedicated section at
   `/settings/organization/danger`, only rendered when current user is
   owner. Flow: select a member (autocomplete from members list,
   admins only filter), confirm with "Type `{tenantSlug}` to confirm",
   POST to `/api/v1/orgs/{tenantId}/transfer-ownership`. On success,
   show a banner "Ownership transferred to X. You are now an admin."
   and navigate to `/settings/organization`. JWT refresh handled by
   the existing auth store (role in JWT drops to `admin`).
7. **Tenant audit log** at `/settings/organization/audit` — renders
   results from `GET /api/v1/orgs/{tenantId}/audit` (Story 18-7
   endpoint). Columns: timestamp, event type (badge), actor (resolved
   to displayName from tags.userId via a 1-hop lookup), summary
   (human copy derived from event type + data payload). Filters:
   event-type chip group (`INVITED / ROLE_CHANGED / REMOVED /
   OWNERSHIP_TRANSFERRED / JOINED / CREATED`), date range picker.
   Pagination 50/page.
8. RBAC guards: only `tenant_owner` / `tenant_admin` see the
   Members, Pending Invites, Audit, Danger sections. A `tenant_member`
   hitting the route directly renders a 403 page with copy "You need
   admin or owner role in this organization to view member management."
9. Optimistic UI: role change, invite send, resend, revoke all show
   optimistic state then reconcile from server response; failure
   reverts + toast.
10. E2E test (Playwright inside `packages/dashboard-user/tests/e2e/`):
    owner A invites user B → user B accepts via link → user B signs
    in as member → owner A promotes user B to admin → user B's role
    badge updates → owner A transfers ownership to user B → owner A
    sees the banner and their role changes to admin → user B (now
    owner) removes user A. Assert the audit log shows every step in
    order.
11. Copy review: every error + empty state + confirm dialog goes
    through the existing dashboard-user i18n catalog
    (`packages/dashboard-user/src/i18n/en.ts`). No hard-coded strings
    in components.

## Technical Context

### Pages + files

| Path | New | Purpose |
|---|---|---|
| `packages/dashboard-user/src/pages/settings/organization/members/page.tsx` | new | members table + invite drawer |
| `packages/dashboard-user/src/pages/settings/organization/audit/page.tsx` | new | audit log view |
| `packages/dashboard-user/src/pages/settings/organization/danger/page.tsx` | new | transfer-ownership + delete-org UI (delete org is existing but UI-placeholder) |
| `packages/dashboard-user/src/components/members/MembersTable.tsx` | new | |
| `packages/dashboard-user/src/components/members/InviteMemberDrawer.tsx` | new | |
| `packages/dashboard-user/src/components/members/ChangeRoleDialog.tsx` | new | |
| `packages/dashboard-user/src/components/members/RemoveMemberDialog.tsx` | new | |
| `packages/dashboard-user/src/components/members/PendingInvitesList.tsx` | new | |
| `packages/dashboard-user/src/components/members/AuditLogTable.tsx` | new | |
| `packages/dashboard-user/src/components/members/TransferOwnershipForm.tsx` | new | |
| `packages/dashboard-user/src/api-client/org-members.ts` | new | thin wrapper over generated OpenAPI client |
| `packages/dashboard-user/src/pages/settings/_layout.tsx` | modify | add sidebar entries for Members / Audit / Danger |
| `packages/dashboard-user/src/i18n/en.ts` | modify | strings |

### Component reuse

The admin dashboard (Story 29-4 pattern) already has:
`ConfirmDestructiveDialog`, `RoleBadge`, `DataTable` with server-side
pagination, `Drawer`, `Toast` wiring. 18-8 reuses those — extract
into `packages/dashboard-ui/` (shared UI package) if it doesn't exist
yet; if 29-4 already extracted, reuse as-is.

### Role guards

Route-level — a `TenantAdminGuard` wrapper component that reads the
current tenant role from the auth store (set by `GET
/api/v1/auth/me` at dashboard boot). `tenant_member` → 403 page
redirect. Store is already populated post-login; no new fetch needed.

### Error-copy mapping

Every 400 / 403 the backend can return must map to a human string:

| Backend error | UI copy |
|---|---|
| `"role must be one of: owner, admin, member"` | "Select a valid role." |
| `"Only owners can change owner-level roles"` | "Only the organization owner can promote or demote an owner." |
| `"Cannot change role of users at or above your level"` | "You can't change the role of someone at your level or above." |
| `"Cannot promote users to or above your level"` | "You can't promote someone to your own role." |
| `"Cannot remove the last owner"` | "There must be at least one owner. Transfer ownership first." |
| `"Cannot remove an owner"` (admin removing owner) | "Admins cannot remove an owner." |
| `"Cannot delete yourself"` (admin endpoint) | "You can't remove yourself; ask another admin." |
| `"Invite has already been accepted"` (resend/revoke) | "This invite has already been accepted." |
| `"Invite has expired"` | "This invite has expired. Send a new one." |
| 429 on resend | "Too many resends. Try again in a few minutes." |
| 403 cross-tenant | "You don't have access to this organization." |

### Audit log summary generation

The audit table needs human summaries — derived client-side from
`event.type` + `event.data`. Example mapping:

| Type | Summary |
|---|---|
| `TENANT.CREATED.SUCCESS` | "{actor} created the organization." |
| `TENANT.MEMBER_INVITED.SUCCESS` | "{actor} invited {data.email} as {data.role}." |
| `TENANT.MEMBER_JOINED.SUCCESS` | "{actor} accepted invite as {data.role}." |
| `TENANT.MEMBER_ROLE_CHANGED.SUCCESS` | "{actor} changed {data.targetUserId}'s role from {data.oldRole} to {data.newRole}." |
| `TENANT.MEMBER_REMOVED.SUCCESS` | "{actor} removed {data.removedUserId}." |
| `TENANT.MEMBER_INVITE_RESENT.SUCCESS` | "{actor} resent the invite to {data.email}." |
| `TENANT.OWNERSHIP_TRANSFERRED.SUCCESS` | "{actor} transferred ownership to {data.newOwnerId}." |
| `TENANT.DELETED.SUCCESS` | "{actor} soft-deleted the organization." |
| `TENANT.PURGED.SUCCESS` | "{actor} permanently deleted the organization." |

Unknown types render `{type}` verbatim so new event types stay safe.

## Dependencies

- **Story 18-5** — dashboard shell must exist
- **Story 18-7** — provides the resend-invite + audit endpoints + the
  role-change event emission
- **Story 28-9** — `switch-org` endpoint (used post-ownership-transfer
  to refresh JWT with new role)
- Epic 16 RBAC middleware (gates the admin+ routes at the backend)

## Estimated hours

**32h** — 11 components + 3 pages + e2e test.

| Task | Hours |
|---|---|
| Members table + server pagination + search | 4 |
| Invite member drawer + validation + role filter | 3 |
| Change role dialog + error-copy wiring | 3 |
| Remove member confirm + last-owner error inline | 2 |
| Pending invites list + resend + revoke actions | 3 |
| Audit log table + filters + summary mapper | 4 |
| Transfer ownership flow + slug confirm + JWT refresh handling | 4 |
| Tenant-admin guard component + route integration | 1 |
| i18n catalog + copy review | 2 |
| Unit + component tests | 3 |
| Playwright E2E test | 3 |

## Non-goals

- Does not implement a member-detail page (future story if product asks).
- Does not surface SSO / IdP settings (Epic 33, deferred).
- Does not include tenant-scoped invoice / billing (separate epic).
- Does not re-home delete-org UI fully — it's stubbed on the Danger
  page but the full 2-phase HMAC confirmation flow is a follow-up.

## References

- Gap audit: [`../plans/tenant-user-mgmt-audit.md`](../plans/tenant-user-mgmt-audit.md)
- Backend source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs`
- Backend completion story: [`18-7-tenant-admin-user-mgmt-api.md`](./18-7-tenant-admin-user-mgmt-api.md)
- Dashboard shell: [`18-5-user-facing-dashboard-shell.md`](./18-5-user-facing-dashboard-shell.md)
- Unified RBAC model: [`../rbac-unified-model.md`](../rbac-unified-model.md)
