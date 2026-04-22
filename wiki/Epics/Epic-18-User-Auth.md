# Epic 18: End-User Auth & Registration

**Status:** Partially Implemented (1 done, 2 in progress, 3 drafted, 18-7/18-8 added Wave-2)
**Stories:** 8 (18-1 through 18-8)
**Estimated Effort:** ~29 days + 46h

## Overview

Epic 18 adds end-user registration, authentication, organization management, GitHub App onboarding, and a user-facing dashboard to the Tamma SaaS platform. It allows users to self-register, verify their email, log in (email+password or GitHub OAuth), create/join organizations (tenants), install the Tamma GitHub App, and access a user-facing dashboard — all separate from the existing admin dashboard at `app.tamma.dev`.

Stories 18-7 and 18-8 were added 2026-04-21 after the tenant-user-mgmt gap audit (`docs/stories/plans/tenant-user-mgmt-audit.md`). The backend for tenant-admin user management already lives in `OrgEndpoints.cs`; 18-7 closes three thin backend gaps; 18-8 ships the dashboard pages.

## Goals

1. Implement user registration with email verification
2. Build login with dual auth (email+password via Argon2, GitHub OAuth)
3. Enable organization/tenant creation and management
4. Integrate GitHub App installation onboarding flow
5. Create user-facing dashboard shell at `dash.tamma.dev`
6. Password reset flow
7. Tenant-admin user management API + UI

## Stories

| Story | Title | Effort | Status |
|-------|-------|--------|--------|
| 18-1 | User Registration & Email Verification | L (5 days) | Drafted |
| 18-2 | User Login & Session Management | L (5 days) | In Progress |
| 18-3 | Organization/Tenant Creation | XL (8 days) | Drafted |
| 18-4 | GitHub App Installation Onboarding | M (3 days) | **Done** |
| 18-5 | User-Facing Dashboard Shell | L (5 days) | In Progress |
| 18-6 | Password Reset Flow | M (3 days) | Drafted |
| 18-7 | Tenant-Admin User Management API Completion | S (14h) | Planned |
| 18-8 | Tenant-Admin User Management UI | M (32h) | Planned |

## Key technical details

### Architecture decisions

1. **Separate subdomain**: User-facing app at `dash.tamma.dev`; admin stays at `app.tamma.dev`
2. **Shared JWT**: Both apps share `tamma_session` cookie on `.tamma.dev` domain; JWT gains `tenantId` claim
3. **Argon2 for passwords**: `argon2id` via the `argon2` npm package — memory-hard, side-channel resistant
4. **Email verification**: Token-based (UUID v7), 24-hour expiry, sent via SMTP (nodemailer)
5. **Organization = tenant**: Story 18-3 does NOT create a separate `organizations` table; it uses the `tenants` table from Story 17-1. All resources scoped via `tenant_id`.
6. **User model extended**: Gains optional `passwordHash`, `emailVerified`, `authMethod` fields. The `users.tenant_id` column (from Epic 17) is nullable and represents the user's "active tenant" shortcut.
7. **M:N user-tenant relationship**: A user can belong to multiple tenants via `tenant_memberships`. This overrides Epic 17's single-FK model. `users.tenant_id` is the "active tenant" shortcut, not the ownership relationship.

### Onboarding flow

```
Register (email+password or GitHub OAuth)
    ↓
Verify email (skip if GitHub OAuth with verified email)
    ↓
Create or join organization (= tenant)
    ↓
Install GitHub App (github.com/apps/tamma/installations/new)
    ↓
Select repositories
    ↓
First workflow run (guided)
```

### Existing infrastructure leveraged

- Admin auth: GitHub OAuth via JWT cookies on `app.tamma.dev`
- API auth: JWT + API key for programmatic access
- RBAC: Three-tier role system (`member`, `admin`, `owner`)
- User model: GitHub-ID-centric; gains password/email fields in Epic 18
- Invite system: Token-based invites with role assignment
- Tenant-admin user management: Most backend lives in `OrgEndpoints.cs`; 18-7 closes the gaps

### Stories 18-7 and 18-8 — what's new

**18-7 (API completion, S/14h)**:
- Resend-invite endpoint
- Tenant-scoped audit endpoint
- Role-change event emission

**18-8 (UI, M/32h)**:
- Tenant-admin user management dashboard pages
- Invite / list / role-change / remove flows in the user-facing dashboard
- Audit log viewer for tenant admins

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| Unified Auth & RBAC | Epic 16 | Extends existing auth infrastructure |
| Multi-Tenancy | Epic 17 | Organization maps to tenant via `tenant_memberships` |
| GitHub App Auth | Epic 1.5 | Installation onboarding |
| API Framework | Epic 1.5 | Registration and login endpoints |
| Per-tenant IdP | Epic 33 | Local user-management is the fallback when no tenant IdP is configured |

## Story files

[Epic 18 stories on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-18)

---

_Last updated: 2026-04-21_
