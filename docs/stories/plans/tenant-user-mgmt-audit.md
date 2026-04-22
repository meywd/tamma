# Tenant User Management — Gap Audit + Decision

**Status**: active, written 2026-04-21
**Scope**: assess what's already built for tenant-admin-driven user
management of their tenant (invite / list / role-change / remove /
transfer-ownership / audit-log-view), decide whether a new story set
is needed, and — if yes — write the briefs.
**Trigger**: review finding "the user can't add users to their
tenant" (raised in the 2026-04-20 review sweep; covered in
`plans/epic-29-30-placement.md` only for platform-secret access).

## TL;DR

**Decision: (a) Extend Epic 18 with two new stories — 18-7
(Tenant-Admin User Management API completion) and 18-8 (Tenant-Admin
User Management UI).** Rationale at bottom.

Backend for tenant-admin user management is **~90% done** — the
`OrgEndpoints` class in `Tamma.Api/Endpoints/OrgEndpoints.cs` already
ships every hierarchy-respecting mutation the capability list asks
for. Two small backend gaps remain (resend-invite, tenant-scoped audit
log view). The UI gap is nearly total — Story 18-5 only lists a single
subtask ("`OrgSettings` page: ... member management (uses 18-3 APIs)")
that's really a placeholder.

**Gap matrix summary**: 5 **Backend only** · 1 **Backend missing (thin)** ·
2 **Backend partial (thin gap)** · 1 **Planned in 18-5 brief only**.

## Evidence — what actually exists

### Backend (OrgEndpoints.cs)

Read `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs`
(694 lines, all handlers accounted for):

| Handler | Path | Status | Notes |
|---|---|---|---|
| `CreateOrg` | `POST /api/v1/orgs` | ✅ | Slug validation, reserved-slug guard, sets active tenant, emits `TENANT.CREATED.SUCCESS` |
| `GetOrg` | `GET /api/v1/orgs/{tenantId}` | ✅ | Membership-gated via `RequireTenantMembershipFilter` |
| `UpdateOrgSettings` | `PATCH /api/v1/orgs/{tenantId}` | ✅ | admin+ |
| `ListMembers` | `GET /api/v1/orgs/{tenantId}/members` | ✅ | Paginated, returns `{userId, role, joinedAt, displayName, email}` |
| `UpdateMemberRole` | `PATCH /api/v1/orgs/{tenantId}/members/{userId}/role` | ✅ | Owner-only for owner-level changes; admin can't touch peers or above; last-owner guard on demote |
| `RemoveMember` | `DELETE /api/v1/orgs/{tenantId}/members/{userId}` | ✅ | admin+; can't remove owner if admin; self-removal last-owner guard; clears active tenant for removed user |
| `CreateInvite` | `POST /api/v1/orgs/{tenantId}/invites` | ✅ | admin+; 256-bit token (SHA-256 hashed in DB); 72h expiry; fire-and-forget email; emits `TENANT.MEMBER_INVITED.SUCCESS`; response does **not** leak raw token |
| `ListInvites` | `GET /api/v1/orgs/{tenantId}/invites` | ✅ | admin+; pending only |
| `DeleteInvite` | `DELETE /api/v1/orgs/{tenantId}/invites/{inviteId}` | ✅ | admin+; tenant-scoped repo method (no cross-tenant delete) |
| `AcceptInvite` | `POST /api/v1/auth/invites/accept` | ✅ | Token-based; idempotent if already member; sets active tenant if user had none |
| `TransferOwnership` | `POST /api/v1/orgs/{tenantId}/transfer-ownership` | ✅ | Owner-only; atomic tx (role swap + tenants.OwnerId); emits `TENANT.OWNERSHIP_TRANSFERRED.SUCCESS` |
| `DeleteOrg` | `DELETE /api/v1/orgs/{tenantId}` | ✅ | Owner-only; last-tenant guard; 2-phase (soft-delete + HMAC confirmation for hard-delete) |
| `SwitchOrg` | `POST /api/v1/auth/switch-org` | ✅ | Mints new JWT with target `tenantId`, writes `tamma_session` cookie |
| `ListTenants` | `GET /api/v1/tenants` | ✅ | Caller's tenant list with `isActive` flag |

**Audit events currently emitted**: `TENANT.CREATED.SUCCESS`,
`TENANT.MEMBER_INVITED.SUCCESS`, `TENANT.MEMBER_JOINED.SUCCESS`,
`TENANT.MEMBER_REMOVED.SUCCESS`, `TENANT.OWNERSHIP_TRANSFERRED.SUCCESS`,
`TENANT.DELETED.SUCCESS`, `TENANT.PURGED.SUCCESS`. All carry
`tenantId` in `Tags` JSON — queryable by tenant-scoped filter on the
event store.

Missing: `TENANT.MEMBER_ROLE_CHANGED.SUCCESS` event emission from
`UpdateMemberRole` (the handler changes the role but does not append
to the event store — only logs). **Thin backend gap.**

### Existing story briefs

| File | Relevance |
|---|---|
| `docs/stories/epic-18/18-3-organization-tenant-creation.md` | API for CreateOrg, invites, membership — the source for OrgEndpoints |
| `docs/stories/epic-18/18-4-github-app-installation-onboarding.md` | GitHub App onboarding flow; does **not** cover tenant user mgmt UI |
| `docs/stories/epic-18/18-5-user-facing-dashboard-shell.md` | Task 6.3: "OrgSettings page: org name, slug, plan, member management (uses 18-3 APIs)" — one subtask, no AC decomposition, no mockup |
| `docs/stories/epic-16/16-2-user-management-api.md` | **Platform-scoped** user management (admin viewing/managing platform users). Different surface; covers `/api/admin/users/*` not `/api/v1/orgs/*/members/*` |
| `docs/stories/epic-16/16-5-role-based-access-control.md` | Permission matrix; overlays tenant roles correctly per `rbac-unified-model.md` |

## Covered-vs-Missing matrix

Legend: ✅ **Backend + UI** · 🟡 **Backend only** · 🔴 **Backend missing** · ⚪ **Planned in brief only**.

| Capability | Backend | UI | Status | Reference |
|---|---|---|---|---|
| Tenant admin invites a new user (email-based) | Full | None | 🟡 | `CreateInvite` in OrgEndpoints.cs |
| Tenant admin lists users in their tenant | Full | None | 🟡 | `ListMembers` in OrgEndpoints.cs |
| Tenant admin changes a user's role (member ↔ admin ↔ owner) | Full (+ hierarchy guards) | None | 🟡 | `UpdateMemberRole` in OrgEndpoints.cs |
| Tenant admin removes a user | Full (+ last-owner guard) | None | 🟡 | `RemoveMember` in OrgEndpoints.cs |
| Tenant admin transfers ownership | Full (atomic tx) | None | 🟡 | `TransferOwnership` in OrgEndpoints.cs |
| Tenant admin views pending invites | Full | None | 🟡 | `ListInvites` in OrgEndpoints.cs |
| Tenant admin revokes pending invite | Full (tenant-scoped) | None | 🟡 | `DeleteInvite` in OrgEndpoints.cs |
| Tenant admin **resends** invite email | Partial (delete + recreate works but no first-class "resend" endpoint; rate-limiting not parameterised for resend-invite scope) | None | 🔴 (thin) | no `POST /invites/{id}/resend` |
| Tenant-scoped audit log view | Partial (events ARE in event store with tenantId tag; no tenant-scoped API endpoint + no `TENANT.MEMBER_ROLE_CHANGED.SUCCESS` emission) | None | 🔴 (thin) | `GET /api/v1/orgs/{tenantId}/audit` not implemented |
| Member-management UI surface (single AC in 18-5) | n/a | Placeholder only | ⚪ | 18-5 subtask 6.3 |

**Totals**: 7 capabilities backend-only; 2 thin backend gaps; 1 UI
placeholder; 0 complete.

## Decision — (a) extend Epic 18

Three options considered:

| Option | Scope | Hours | Verdict |
|---|---|---|---|
| (a) Extend Epic 18 with 18-7 (API completion) + 18-8 (UI) | Finish resend-invite, audit view, emit missing events + build the UI pages | ~60 | **Chosen** |
| (b) Create a thin Epic 32 for tenant user management | Separate epic, 3-5 stories | ~50 | Rejected — splits the auth plane across two epics; 18-5 already names the feature |
| (c) Already covered by 18-5 — no new work | Trust the one-liner in 18-5 subtask 6.3 | 0 | Rejected — "member management (uses 18-3 APIs)" is not an implementable spec; no ACs, no wireframes, no audit view, no resend-invite |

**Why (a) over (b)**: the user management flow is a direct continuation
of Epic 18's theme (end-user-facing auth + tenant lifecycle). Shipping
it as two stories inside Epic 18 means the same team / same sprint
backlog / same `dash.tamma.dev` shell. A separate Epic 32 would
reshuffle ownership without changing scope.

**Why (a) over (c)**: 18-5 AC 10 mentions "Settings pages at
/settings/* for: profile, organization, connected accounts,
notifications" without tenant member management. Task 6.3's single
subtask ("member management (uses 18-3 APIs)") is a hand-wave — not
an implementable spec. Shipping without a pointed story would leave
the backend stranded: a full set of hierarchy-respecting APIs nobody
can reach from the dashboard.

## New stories — briefs

Brief documents written in this same pass, same template as Epic 29/30:

- [`../epic-18/18-7-tenant-admin-user-mgmt-api.md`](../epic-18/18-7-tenant-admin-user-mgmt-api.md) — 14h — finish the three thin backend gaps
- [`../epic-18/18-8-tenant-admin-user-mgmt-ui.md`](../epic-18/18-8-tenant-admin-user-mgmt-ui.md) — 32h — members page + invite drawer + role editor + pending invite list + audit log view + transfer-ownership flow

Total new work: **~46h** (less than a half-sprint).

## Review-finding cross-reference

| Finding | Closes via |
|---|---|
| "The user can't add users to their tenant" (2026-04-20 review) | 18-8 (UI) — backend already exists |
| Missing `TENANT.MEMBER_ROLE_CHANGED.SUCCESS` event (self-identified here) | 18-7 task 1 |
| Tenant-scoped audit view gap | 18-7 task 2 + 18-8 task 5 |
| Resend-invite UX parity gap | 18-7 task 3 + 18-8 task 4 |

## Sources

- `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` — primary evidence
- `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs` — platform-admin surface (different scope)
- `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` — invite accept + GitHub OAuth invite handling
- `/home/meywd/tamma/docs/stories/epic-18/18-4-github-app-installation-onboarding.md`
- `/home/meywd/tamma/docs/stories/epic-18/18-5-user-facing-dashboard-shell.md`
- `/home/meywd/tamma/docs/stories/epic-16/16-2-user-management-api.md`
- `/home/meywd/tamma/docs/stories/epic-16/16-5-role-based-access-control.md`
- `/home/meywd/tamma/docs/stories/rbac-unified-model.md`
