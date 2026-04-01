# Epic 18: End-User Auth & Registration

**Status:** Partially Implemented (1 done, 2 in progress, 2 drafted)
**Stories:** 5 (18-1 through 18-5)
**Estimated Effort:** ~23 days (single developer)

## Overview

Epic 18 adds end-user registration, authentication, organization management, GitHub App onboarding, and a user-facing dashboard to the Tamma SaaS platform. It allows users to self-register, verify their email, log in (email+password or GitHub OAuth), create/join organizations (tenants), install the Tamma GitHub App, and access a user-facing dashboard -- all separate from the existing admin dashboard at `app.tamma.dev`.

## Goals

1. Implement user registration with email verification
2. Build login with dual auth (email+password via Argon2, GitHub OAuth)
3. Enable organization/tenant creation and management
4. Integrate GitHub App installation onboarding flow
5. Create user-facing dashboard shell at `dash.tamma.dev`

## Stories

| Story | Title | Effort | Status |
|-------|-------|--------|--------|
| 18-1 | User Registration & Email Verification | L (5 days) | Drafted |
| 18-2 | User Login & Session Management | L (5 days) | In Progress |
| 18-3 | Organization/Tenant Creation | L (5 days) | Drafted |
| 18-4 | GitHub App Installation Onboarding | M (3 days) | Done |
| 18-5 | User-Facing Dashboard Shell | L (5 days) | In Progress |

## Key Technical Details

### Architecture Decisions

1. **Separate subdomain**: User-facing app at `dash.tamma.dev`; admin stays at `app.tamma.dev`
2. **Shared JWT**: Both apps share `tamma_session` cookie on `.tamma.dev` domain; JWT gains `orgId` claim
3. **Argon2 for passwords**: `argon2id` via the `argon2` npm package -- memory-hard, side-channel resistant
4. **Email verification**: Token-based (UUID v7), 24-hour expiry, sent via SMTP (nodemailer)
5. **Organization = tenant**: All resources scoped to an organization
6. **User model extended**: Gains `passwordHash`, `emailVerified`, `orgId`, `authMethod` fields

### Onboarding Flow

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
Install GitHub App (github.com/apps/tamma/installations/new)
    |
    v
Select repositories
    |
    v
First workflow run (guided)
```

### Existing Infrastructure

The system already has:
- Admin auth: GitHub OAuth via JWT cookies on `app.tamma.dev`
- API auth: JWT + API key for programmatic access
- RBAC: Three-tier role system (`member`, `admin`, `owner`)
- User model: GitHub-ID-centric (no password field currently)
- Invite system: Token-based invites with role assignment

Epic 18 extends the platform to support self-service registration without requiring admin intervention.

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| Unified Auth & RBAC | Epic 16 | Extends existing auth infrastructure |
| Multi-Tenancy | Epic 17 | Organization maps to tenant |
| GitHub App Auth | Epic 1.5 | Installation onboarding |
| API Framework | Epic 1.5 | Registration and login endpoints |

## Story Files

[Story documents on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-18)
