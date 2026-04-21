# Story 18-8 Implementation Plan — Tenant-Admin User Management UI

**Status**: Planned (2026-04-21)
**Story brief**: [`18-8-tenant-admin-user-mgmt-ui.md`](./18-8-tenant-admin-user-mgmt-ui.md)
**Epic 18 phase**: Layer 4 Team C (dashboard-user shell).
**Branch**: `feat/story-18-8-tenant-admin-user-mgmt-ui`

---

## 1. Objective

Ship the full tenant-admin user-management UI surface inside
`dash.tamma.dev`: members table, invite drawer, change-role dialog,
remove-member confirm, pending-invites list with resend + revoke,
transfer-ownership flow, and tenant-scoped audit log. Every page
consumes the existing `OrgEndpoints.cs` surface plus the three
handlers from Story 18-7 (resend-invite, tenant audit, role-change
event). RBAC guards limit access to `tenant_owner` + `tenant_admin`;
member-role users see a 403 with friendly copy.

## 2. Dependencies

Hard blockers:

- **Story 18-5** — dashboard-user shell + sidebar + settings layout
  must exist. This story plugs new pages into the existing
  `/settings/organization/*` router.
- **Story 18-7** — resend-invite endpoint + tenant audit endpoint +
  role-change event emission. Every page touches at least one of
  these.
- **Story 28-9** (switch-org / JWT refresh) — after transfer-ownership
  the caller's JWT role drops; UI relies on switch-org refresh to
  pick up the new role.
- **Story 29-5** (tenant admin UI) — provides the `RoleBadge`,
  `ConfirmDestructiveDialog`, `Drawer`, `DataTable` shared UI
  primitives under `packages/dashboard-ui/`. If 29-5 has not yet
  extracted these, this story duplicates the components and a follow-
  up extracts; documented as a soft dep.
- **Epic 16 RBAC** — `/auth/me` returns `tenantRole`; `TenantAdminGuard`
  reads from the auth store.

Soft:

- **Story 18-4** (GitHub onboarding) — no dep; runs in parallel.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/packages/dashboard-user/src/pages/settings/organization/members/page.tsx` | Members table + invite drawer root page. |
| `/home/meywd/tamma/packages/dashboard-user/src/pages/settings/organization/audit/page.tsx` | Audit log view. |
| `/home/meywd/tamma/packages/dashboard-user/src/pages/settings/organization/danger/page.tsx` | Danger zone: transfer-ownership + delete-org placeholder. |
| `/home/meywd/tamma/packages/dashboard-user/src/components/members/MembersTable.tsx` | Paginated members table with row actions. |
| `/home/meywd/tamma/packages/dashboard-user/src/components/members/InviteMemberDrawer.tsx` | Email + role form drawer. |
| `/home/meywd/tamma/packages/dashboard-user/src/components/members/ChangeRoleDialog.tsx` | Inline role picker with hierarchy filter. |
| `/home/meywd/tamma/packages/dashboard-user/src/components/members/RemoveMemberDialog.tsx` | Confirm dialog for `DELETE /members/:id`. |
| `/home/meywd/tamma/packages/dashboard-user/src/components/members/PendingInvitesList.tsx` | Invite list + resend + revoke actions. |
| `/home/meywd/tamma/packages/dashboard-user/src/components/members/AuditLogTable.tsx` | Audit event table with filters + summary generator. |
| `/home/meywd/tamma/packages/dashboard-user/src/components/members/TransferOwnershipForm.tsx` | Autocomplete + slug confirm + submit. |
| `/home/meywd/tamma/packages/dashboard-user/src/components/members/TenantAdminGuard.tsx` | Route wrapper that blocks tenant_member. |
| `/home/meywd/tamma/packages/dashboard-user/src/api-client/org-members.ts` | Thin typed wrapper over generated OpenAPI client. |
| `/home/meywd/tamma/packages/dashboard-user/src/api-client/org-audit.ts` | Thin wrapper for `GET /orgs/:tenantId/audit`. |
| `/home/meywd/tamma/packages/dashboard-user/src/lib/audit-summary.ts` | Event-to-summary mapper + unit-tested. |
| `/home/meywd/tamma/packages/dashboard-user/src/lib/audit-summary.test.ts` | Vitest unit tests for the mapper. |
| `/home/meywd/tamma/packages/dashboard-user/src/stores/org-members-store.ts` | Zustand store + optimistic-UI reducers. |
| `/home/meywd/tamma/packages/dashboard-user/tests/e2e/tenant-user-mgmt.spec.ts` | Playwright owner→member→admin→transfer→remove flow. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/packages/dashboard-user/src/pages/settings/_layout.tsx` | Add three sidebar entries: Members, Audit, Danger. Each guarded by `tenantRole >= admin`. |
| `/home/meywd/tamma/packages/dashboard-user/src/router.tsx` | Register three new routes + `TenantAdminGuard` wrapper. |
| `/home/meywd/tamma/packages/dashboard-user/src/i18n/en.ts` | Add strings for all error copies, empty states, confirm dialog copy (see brief AC §error-copy-mapping). |
| `/home/meywd/tamma/packages/dashboard-user/src/stores/auth-store.ts` | Ensure `refreshClaims()` is called after transfer-ownership; 28-9's `switch-org` already handles this — verify wire-up. |

## 5. Sequence of changes

### Step 1 — `TenantAdminGuard` + sidebar wiring (1h)

- `TenantAdminGuard.tsx`: reads `tenantRole` from auth store. If
  `'member'` → render a `<Forbidden />` page with copy
  `ui.tenantAdmin.forbiddenCopy`.
- `_layout.tsx` sidebar: three new entries gated by
  `tenantRole in ['admin','owner']`.
- `router.tsx`: register routes `/settings/organization/members`,
  `/settings/organization/audit`, `/settings/organization/danger`.
- Component test: guard renders Forbidden for member; renders children
  for admin + owner.
- **Commit**: `feat(dashboard-user): tenant-admin guard + sidebar`.

### Step 2 — `org-members` API client + store (2h)

- `api-client/org-members.ts` typed wrappers: `listMembers`,
  `updateMemberRole`, `removeMember`, `createInvite`, `listInvites`,
  `resendInvite`, `deleteInvite`, `transferOwnership`.
- `api-client/org-audit.ts`: `listTenantAudit(tenantId, { limit,
  offset, type })`.
- `stores/org-members-store.ts`: Zustand store with optimistic-update
  reducers (applied on call, reverted on error). Hooks `useMembers`,
  `useInvites`, `useAudit`.
- Store unit tests (Vitest): optimistic update applied; server error
  rolls back + emits toast.
- **Commit**: `feat(dashboard-user): org-members API client + store`.

### Step 3 — Members table + invite drawer (4h)

- `MembersTable.tsx`: `<DataTable>` with columns (displayName, email,
  RoleBadge, joinedAt, menu). Server-side pagination 50/page.
  Client-side search input debounced 250ms.
- `InviteMemberDrawer.tsx`: form fields `email` (zod-validated),
  `role` (select; filtered by caller's role), `message` (optional,
  textarea — submitted but currently ignored backend-side).
- On submit → `createInvite` → close drawer → refetch pending-invites.
- Component tests: role select filters (admin sees only member/admin;
  owner sees all three); email validation errors render inline.
- **Commit**: `feat(members): table + invite drawer`.

### Step 4 — Change-role dialog (3h)

- `ChangeRoleDialog.tsx`: inline dropdown with allowed target roles
  given caller's role.
- Submit → `updateMemberRole`. Error handler maps 403 codes to the
  four strings in brief AC §3:
  - `"Only owners can change owner-level roles"`
  - `"Cannot change role of users at or above your level"`
  - `"Cannot promote users to or above your level"`
  - 400 `"Cannot remove the last owner"` (promoting-last-owner
    demotion)
- Error-copy mapper unit test: each backend string → UI copy.
- **Commit**: `feat(members): change-role dialog`.

### Step 5 — Remove-member confirm (2h)

- `RemoveMemberDialog.tsx`: uses shared `ConfirmDestructiveDialog`.
  Body copy: `"X will lose access to {tenantName}. Their API keys and
  workflow assignments are revoked."` (strings via i18n).
- DELETE `/members/:id`; handle 400 last-owner guard inline.
- **Commit**: `feat(members): remove-member confirm`.

### Step 6 — Pending invites list + resend/revoke (3h)

- `PendingInvitesList.tsx`: table with columns email, role,
  invitedBy, expiresAt (relative dayjs.fromNow()), menu.
- Row action "Resend" → `resendInvite`. 429 response renders a
  banner `"Too many resends. Try again in a few minutes."`
- Row action "Revoke" → `deleteInvite` + `ConfirmDestructiveDialog`.
- Empty state: `"No pending invites."`
- Component test: 429 branch renders banner; 400 (already-accepted)
  renders row-level error.
- **Commit**: `feat(members): pending invites + resend + revoke`.

### Step 7 — Audit log table + summary generator (4h)

- `lib/audit-summary.ts`: pure function `eventToSummary(event)` with
  the mapping table in brief §audit-log-summary-generation. Returns
  `{actorId, summary, icon}`. Unknown types → raw `{type}` with
  warning badge.
- `AuditLogTable.tsx`: fetches `listTenantAudit`, renders columns
  timestamp (relative), event-type badge, actor (displayName resolved
  via shared `UserDisplayCache`), summary (markdown-safe render).
- Filters: chip-group for event-type families (`INVITED / ROLE_CHANGED
  / REMOVED / OWNERSHIP_TRANSFERRED / JOINED / CREATED`); date range
  picker (client-side filter, backend only offers `type` + pagination).
- Pagination 50/page.
- Unit tests: every event type in the mapper; unknown types fall
  through to raw.
- **Commit**: `feat(members): audit log table + summary mapper`.

### Step 8 — Transfer-ownership flow (4h)

- `danger/page.tsx`: renders `<TransferOwnershipForm />` only if
  `tenantRole === 'owner'`. Below, placeholder for
  delete-org (copy "Delete organization — coming soon").
- `TransferOwnershipForm.tsx`:
  1. Autocomplete of members filtered to `role === 'admin'`.
  2. "Type `{tenantSlug}` to confirm" input.
  3. Submit → `transferOwnership`.
  4. On success: call `switch-org` to refresh JWT (auth store handles);
     render banner `"Ownership transferred to X. You are now an admin."`
     then navigate to `/settings/organization`.
- JWT role change is picked up by auth store's `refreshClaims()` call.
- **Commit**: `feat(members): transfer-ownership form`.

### Step 9 — i18n catalog + copy review (2h)

- Extract every string used in the above components to
  `i18n/en.ts`. Run the i18n lint rule (existing) to catch hard-
  coded strings.
- **Commit**: `chore(i18n): tenant-user-mgmt strings`.

### Step 10 — Unit + component tests (3h)

- Vitest component tests for each new component: render + key user
  actions.
- Audit-summary mapper 100% branch coverage.
- Guard + role-filter tests for dialogs.
- **Commit**: `test(members): component + mapper coverage`.

### Step 11 — Playwright E2E (3h)

- `tests/e2e/tenant-user-mgmt.spec.ts`: full loop matches brief AC 10:
  owner A invites B → B accepts → B signs in → A promotes B to admin
  → A transfers ownership to B → A sees "now an admin" banner → B
  removes A. Assert audit log reflects each step.
- E2E runs against the in-process dashboard-user dev server pointed
  at the C# API testcontainer stack.
- **Commit**: `test(e2e): tenant-user-mgmt happy path`.

### Step 12 — Accessibility + RTL sweep (1h)

- Every form field has an `<label htmlFor>`.
- Every error message uses `aria-describedby` on the offending input.
- Dialogs use focus trap + initial-focus + restore-focus on close
  (existing `Dialog` primitive handles).
- **Commit**: `a11y(members): focus + aria sweep`.

## 6. Test strategy

### Unit (Vitest)

- `audit-summary.ts` mapper — every event type in the mapping table
  + unknown-type fallback.
- Error-copy mapper — every 400/403 backend error string.
- Optimistic-update reducers — applied on call, reverted on error.
- Role-filter logic in the invite drawer + change-role dialog.

### Component (Vitest + Testing Library)

- `MembersTable` — loads data, respects pagination.
- `InviteMemberDrawer` — zod validation errors render.
- `ChangeRoleDialog` — 403 branches.
- `RemoveMemberDialog` — confirm + 400 last-owner.
- `PendingInvitesList` — 429 banner.
- `AuditLogTable` — renders + filters.
- `TransferOwnershipForm` — slug confirm + autocomplete restrict.
- `TenantAdminGuard` — renders Forbidden for member.

### E2E (Playwright)

- Single happy-path spec covering the 7-step owner→transfer→remove
  flow described in brief AC 10.

### a11y

- Axe-core run on every new page as part of the Playwright spec.

## 7. Rollback plan

- **Revert**: commits are UI-only; reverting removes the pages, store,
  API client, guard. Backend surface from 18-7 remains accessible to
  any other consumer (CLI, admin dashboard).
- **Router safety**: the three route entries land gated by the guard;
  reverting the router entries returns users to the fallback
  `/settings/organization` page, which 18-5 still serves.
- **Non-reversible**: none. All state lives in the backend; UI is
  stateless.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Guard + sidebar | 1 |
| 2. API client + store | 2 |
| 3. Members table + invite drawer | 4 |
| 4. Change-role dialog | 3 |
| 5. Remove-member confirm | 2 |
| 6. Pending invites + resend/revoke | 3 |
| 7. Audit log table + summary mapper | 4 |
| 8. Transfer-ownership form | 4 |
| 9. i18n catalog | 2 |
| 10. Unit + component tests | 3 |
| 11. Playwright E2E | 3 |
| 12. a11y sweep | 1 |
| **Total** | **32** (matches brief). |

## 9. Open questions

- **Shared UI primitives sourcing**: `ConfirmDestructiveDialog`,
  `RoleBadge`, `Drawer`, `DataTable` are planned in
  `packages/dashboard-ui/` but the extraction may lag 29-5. Plan:
  start by duplicating inside `dashboard-user/src/components/ui/`;
  when 29-5's extraction lands, swap imports. Document the
  duplication as tech debt.
- **Audit date-range filter**: backend only supports pagination +
  type prefix. Date range is client-side filter on the current page.
  Trade-off: to filter across all pages, we'd need to extend 18-7's
  endpoint with `from`/`to` params. Plan: ship client-side filter
  first; extend backend in a follow-up if users request it.
- **Actor displayName resolution**: `GET /users/{id}/display` returns
  a single row. Audit log of 50 rows = 50 round trips (bad). Plan:
  add `GET /users/display?ids=<csv>` batch endpoint. Small backend
  addition; documented as mini-dep on 18-7 — add to 18-7's scope via
  follow-up if this story can't slip.
- **Transfer-ownership JWT refresh race**: after `transferOwnership`
  returns 200, the JWT still has `role=owner` until refresh. The
  banner might flash "You are now an admin" before the store
  actually updates. Plan: await `refreshClaims()` before rendering
  the banner; document in `auth-store.ts` contract.
- **Delete-org on Danger page**: listed as placeholder. Actual
  2-phase HMAC confirmation flow is a follow-up story. Plan:
  render a disabled button with tooltip "Delete coming soon" —
  no hidden dead code.
- **RBAC matrix edge case**: an admin promoting themselves to owner
  is forbidden by backend. UI should pre-disable that option in the
  dropdown. Current role-filter logic handles this — confirm with
  the 16-5 matrix doc before the dialog ships.
