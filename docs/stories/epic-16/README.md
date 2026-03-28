# Epic 16: Unified Authentication, User Management & Admin

## Overview

**Goal**: Consolidate the fragmented authentication systems across Tamma services (Dashboard, ELSA Studio, OpenSearch Dashboards) into a single GitHub OAuth flow, add user management capabilities, build an admin panel, implement cross-service navigation, and enforce role-based access control.

**Value Delivered**:
- Single sign-on across all Tamma dashboards (app.tamma.dev, elsa.tamma.dev, logs.tamma.dev)
- User management with invite flow, role assignment, and API key provisioning
- Admin panel with system health overview, user management UI, and quick links
- Consistent navigation header across all services showing logged-in user and service links
- Enforced RBAC at API, nginx, and proxy levels (member, admin, owner roles)

## Current State (Problems)

| Service | Auth Method | User Store | Issues |
|---------|------------|------------|--------|
| Tamma Dashboard (app.tamma.dev) | GitHub OAuth -> JWT cookie | PostgreSQL `users` table | Only auth that uses Tamma user store |
| ELSA Studio (elsa.tamma.dev) | ELSA Identity (admin user + signing key) | ELSA internal tables | Completely separate from Tamma users |
| OpenSearch Dashboards (logs.tamma.dev) | None (security plugin disabled) | None | Wide open behind nginx; "Cloudflare-authenticated" is vague |
| Tamma API (api.tamma.dev) | JWT + API key | PostgreSQL | JWT works, but API-level RBAC not enforced |

**Additional gaps**:
- No user management UI (users created only via GitHub OAuth self-service)
- No admin panel for system oversight
- No way to navigate between services without typing URLs manually
- `user.role` column exists (`owner`, `admin`, `member`) but is not enforced anywhere meaningful
- ELSA Studio accessible to anyone who can reach elsa.tamma.dev

## Target Architecture

```
                    +------------------+
                    |   Cloudflare     |
                    |   DNS + TLS      |
                    +--------+---------+
                             |
                    +--------v---------+
                    |   nginx-proxy    |
                    |   (port 443)     |
                    +--------+---------+
                             |
              +--------------+--------------+
              |              |              |
     +--------v------+ +----v------+ +-----v---------+
     | oauth2-proxy  | | oauth2-   | | oauth2-proxy  |
     | (app.tamma)   | | proxy     | | (logs.tamma)  |
     |               | | (elsa)    | |               |
     +--------+------+ +----+------+ +-----+---------+
              |              |              |
     +--------v------+ +----v------+ +-----v---------+
     | tamma-        | | elsa-     | | opensearch-   |
     | dashboard     | | studio    | | dashboards    |
     +---------------+ +-----------+ +---------------+

     Shared cookie: _oauth2_proxy (domain: .tamma.dev)
     Session store: Redis or cookie-encrypted
     Identity source: GitHub OAuth (same app as current)
```

## Roles

| Role | Scope |
|------|-------|
| **member** | View Dashboard, view own workflow runs, view own API keys |
| **admin** | All member + manage users, view all workflow runs, access ELSA Studio, access OpenSearch Dashboards |
| **owner** | All admin + manage installations, delete data, system configuration, promote/demote admins |

## Stories

| Story | Title | Priority | Dependencies | Status |
|-------|-------|----------|-------------|--------|
| 16.1 | OAuth2 Proxy Unified Auth | P0 (Critical) | None | Planned |
| 16.2 | User Management REST API | P0 (Critical) | Story 16.1 | Planned |
| 16.3 | Admin Dashboard | P1 (High) | Story 16.2 | Planned |
| 16.4 | Unified Navigation Header | P1 (High) | Story 16.1 | Planned |
| 16.5 | Role-Based Access Control Enforcement | P0 (Critical) | Story 16.1, 16.2 | Planned |

## Dependency Graph

```
Story 16.1 (oauth2-proxy unified auth)
  |
  +---> Story 16.2 (user management API)
  |       |
  |       +---> Story 16.3 (admin dashboard UI)
  |       |
  |       +---> Story 16.5 (RBAC enforcement)
  |
  +---> Story 16.4 (unified navigation header)
```

## Estimated Total Effort

| Story | Estimate |
|-------|----------|
| 16.1 OAuth2 Proxy Unified Auth | 16 hours |
| 16.2 User Management REST API | 20 hours |
| 16.3 Admin Dashboard | 24 hours |
| 16.4 Unified Navigation Header | 12 hours |
| 16.5 RBAC Enforcement | 16 hours |
| **Total** | **88 hours** |

## Host Constraints

- **VPS**: Hetzner CPX42, 16 GB RAM, 8 vCPU (AMD EPYC)
- **oauth2-proxy**: ~30 MB RAM (negligible)
- No additional database required (oauth2-proxy uses cookie-encrypted sessions or the existing PostgreSQL)

---

**Last Updated**: 2026-03-28
**Epic Owner**: Platform Engineering
