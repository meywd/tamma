# Epic 16: Unified Auth, User Management & Admin

**Status:** Done
**Stories:** 6 (16-1 through 16-6)

## Overview

Epic 16 consolidates the fragmented authentication systems across Tamma services (Dashboard, ELSA Studio, OpenSearch Dashboards) into a single GitHub OAuth flow, adds user management capabilities, builds an admin panel, implements cross-service navigation, and enforces role-based access control.

## Goals

1. Unify authentication across all Tamma dashboards via GitHub OAuth
2. Build user management API with invite flow and role assignment
3. Create admin dashboard with system health overview
4. Implement unified navigation header across all services
5. Enforce role-based access control at API, nginx, and proxy levels
6. Enable ELSA Studio auto-login via unified auth

## Value Delivered

- Single sign-on across all Tamma dashboards (app.tamma.dev, elsa.tamma.dev, logs.tamma.dev)
- User management with invite flow, role assignment, and API key provisioning
- Admin panel with system health overview, user management UI, and quick links
- Consistent navigation header across all services
- Enforced RBAC (member, admin, owner roles)

## Stories

| Story | Title | Priority | Status |
|-------|-------|----------|--------|
| 16-1 | GitHub OAuth Unified Auth | P0 (Critical) | Done |
| 16-2 | User Management API | P0 (Critical) | Done |
| 16-3 | Admin Dashboard | P1 (High) | Done |
| 16-4 | Unified Navigation | P1 (High) | Done |
| 16-5 | Role-Based Access Control | P0 (Critical) | Done |
| 16-6 | ELSA Studio Auto-Login | P1 (High) | Done |

## Key Technical Details

### Roles

| Role | Scope |
|------|-------|
| **member** | View Dashboard, view own workflow runs, view own API keys |
| **admin** | All member + manage users, view all workflow runs, access ELSA Studio, access OpenSearch Dashboards |
| **owner** | All admin + manage installations, delete data, system configuration, promote/demote admins |

### Architecture

```
Cloudflare DNS + TLS
        |
   nginx-proxy (443)
        |
   +----+----+----+
   |         |         |
app.tamma  elsa.tamma  logs.tamma
(dashboard) (studio)   (opensearch)
```

All services share the GitHub OAuth session via JWT cookies on `.tamma.dev` domain.

### Estimated Effort

| Story | Estimate |
|-------|----------|
| 16-1 OAuth Unified Auth | 16 hours |
| 16-2 User Management API | 20 hours |
| 16-3 Admin Dashboard | 24 hours |
| 16-4 Unified Navigation | 12 hours |
| 16-5 RBAC Enforcement | 16 hours |
| 16-6 ELSA Studio Auto-Login | -- |
| **Total** | **88+ hours** |

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| Web Server & API | Epic 1.5 | Auth endpoints hosted on Fastify API |
| GitHub App Auth | Epic 1.5 | GitHub OAuth integration |
| Dashboard | Epic 5 | Admin dashboard extends existing React SPA |
| ELSA Studio | Epic 14 | Auto-login integration |

## Story Files

[Story documents on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-16)
