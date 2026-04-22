# Epic 18: End-User Authentication & Registration for Tamma SaaS

This directory contains all stories for Epic 18, which adds end-user registration, authentication, organization management, GitHub App onboarding, and a user-facing dashboard to the Tamma SaaS platform.

## Epic Overview

**Goal**: Allow end users to self-register, verify their email, log in (email+password or GitHub OAuth), create/join organizations (tenants), install the Tamma GitHub App, and access a user-facing dashboard -- all separate from the existing admin dashboard at `app.tamma.dev`.

## Context

The existing system has:
- **Admin auth**: GitHub OAuth via `oauth2-proxy` + JWT cookies on `app.tamma.dev` (see `packages/api/src/routes/auth/github-oauth.ts`)
- **API auth**: JWT + API key auth for programmatic access (see `packages/api/src/auth/index.ts`, `api-key-auth.ts`)
- **RBAC**: Three-tier role system (`member`, `admin`, `owner`) with permission matrix (see `packages/api/src/auth/permissions.ts`)
- **User model**: GitHub-ID-centric `User` in `packages/api/src/persistence/user-store.ts` -- no password field, no email-based login
- **Invite system**: Token-based invites with role assignment (see `packages/api/src/persistence/invite-store.ts`)
- **Installation model**: GitHub App installations linked to users (see `packages/api/src/persistence/installation-store.ts`)

Epic 18 extends the platform to support self-service registration without requiring admin intervention.

## Stories

| Story | Title | Effort | Dependencies |
|-------|-------|--------|-------------|
| 18-1 | User registration + email verification | L (5 days) | None |
| 18-2 | User login + session management | L (5 days) | 18-1 |
| 18-3 | Organization/tenant creation | XL (8 days) | 18-2, Epic 17 (17-1) |
| 18-4 | GitHub App installation onboarding | M (3 days) | 18-3 |
| 18-5 | User-facing dashboard shell | L (5 days) | 18-2 |
| 18-6 | Password reset flow | M (3 days) | 18-1, 18-2 |
| 18-7 | Tenant-admin user management API completion | S (14h) | 18-3 |
| 18-8 | Tenant-admin user management UI | M (32h) | 18-5, 18-7 |

**Total estimated effort**: ~29 days + ~46h (single developer)

> **18-7 and 18-8** were added 2026-04-21 after the tenant-user-mgmt
> gap audit (see [`../plans/tenant-user-mgmt-audit.md`](../plans/tenant-user-mgmt-audit.md)).
> The backend for tenant-admin user management already lives in
> `OrgEndpoints.cs`; 18-7 closes three thin backend gaps (resend-invite,
> tenant-scoped audit endpoint, role-change event emission) and 18-8
> ships the dashboard pages.

## Architecture Decisions

1. **Separate subdomain**: User-facing app lives at `dash.tamma.dev`; admin stays at `app.tamma.dev`
2. **Shared JWT**: Both apps share the `tamma_session` cookie on `.tamma.dev` domain; JWT payload gains `tenantId` claim
3. **Argon2 for passwords**: Using `argon2id` via the `argon2` npm package -- memory-hard, side-channel resistant
4. **Email verification**: Token-based (UUID v7), 24-hour expiry, sent via configurable SMTP transport (nodemailer)
5. **Organization = tenant**: An organization IS a tenant from Epic 17. Story 18-3 does NOT create a separate `organizations` table; it uses the `tenants` table from Story 17-1. All resources (installations, workflows, settings) are scoped via `tenant_id`.
6. **User model extended**: The `User` interface gains optional `passwordHash`, `emailVerified`, `authMethod` fields. The `users.tenant_id` column (from Epic 17) is nullable and represents the user's "active tenant" shortcut. The canonical user-to-tenant relationship is M:N via the `tenant_memberships` table (Story 18-3).
7. **M:N user-tenant relationship**: A user can belong to multiple tenants via `tenant_memberships`. This overrides Epic 17's single FK model. `users.tenant_id` is the "active tenant" shortcut, not the ownership relationship.

## Onboarding Flow

```
Register (email+password or GitHub OAuth)
    |
    v
Verify email (skip if GitHub OAuth with verified email)
    |
    v
Create or join organization
    |
    v
Install GitHub App (redirects to github.com/apps/tamma/installations/new)
    |
    v
Select repositories
    |
    v
First workflow run (guided)
```

## File Types

- **`18-*.md` files**: Story specifications with acceptance criteria and technical details

## Implementation Status

All stories are planned and ready for implementation.

---

**Last Updated**: 2026-04-09
**Epic Owner**: TBD
