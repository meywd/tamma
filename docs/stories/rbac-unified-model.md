# Unified RBAC Role Model

Status: reference

## Purpose

This document defines the unified role model used across all Tamma services. It resolves discrepancies between the role sets defined in Epic 16 (admin platform RBAC) and Epic 18 (multi-tenant RBAC). All stories that reference roles or permissions must conform to this model.

## Two-Level Role System

Tamma uses a **two-level role system**: platform roles (global) and tenant roles (per-organization).

### Platform Roles

Platform roles control access to platform-wide resources and cross-tenant operations.

| Role | Description |
|------|-------------|
| `user` | Default role for all registered users. Can access tenants they are a member of. |
| `platform_admin` | Platform-wide administrator. Can access any tenant's data, manage platform settings, and perform cross-tenant operations. |

Platform roles are stored on the `users` table in the `platform_role` column.

### Tenant Roles

Tenant roles control access to resources within a specific organization/tenant.

| Role | Description |
|------|-------------|
| `member` | Read access to tenant resources. Can view workflows, manage own API keys, and view own runs. |
| `admin` | All member permissions plus: manage users within the tenant, view all workflow runs, access ELSA Studio and OpenSearch Dashboards, manage tenant settings. |
| `owner` | All admin permissions plus: manage installations, promote/demote admins, delete users, delete data, system configuration within the tenant. |

Tenant roles are stored in the `org_memberships` table (Epic 18 Story 18-3), scoped to a specific `(org_id, user_id)` pair. A user can have different roles in different tenants.

## Role Resolution

When evaluating permissions for an API request:

1. **Extract `platformRole`** from the JWT `platformRole` claim.
2. **Extract `tenantId`** from the JWT `tenantId` claim.
3. **Extract `role`** (tenant role) from the JWT `role` claim.

### Decision Matrix

| Operation Type | Required Check | Notes |
|---------------|---------------|-------|
| Platform-wide (e.g., list all tenants) | `platformRole === 'platform_admin'` | Only platform admins |
| Tenant-scoped (e.g., list workflows) | `tenantId` is non-null AND `role` has sufficient permission | Use tenant role hierarchy |
| Cross-tenant (e.g., platform admin viewing any tenant) | `platformRole === 'platform_admin'` | Platform admin bypasses tenant membership check |

### Platform Admin Override

A `platform_admin` can access **any** tenant's resources regardless of membership. When a `platform_admin` accesses a tenant they are not a member of, the effective tenant role is treated as `owner` for permission checks.

### Tenant Role Hierarchy

```
owner > admin > member
```

Each higher role inherits all permissions of lower roles.

## Mapping to Existing Stories

### Epic 16 (Story 16-5: RBAC Enforcement)

Epic 16's `Role` type (`'owner' | 'admin' | 'member'`) maps to **tenant roles**. The `ROLE_HIERARCHY` and `hasPermission()` function in `packages/api/src/rbac/permissions.ts` operate on tenant roles.

To integrate platform roles, the RBAC middleware should:
1. If `platformRole === 'platform_admin'`, grant access (bypass tenant role check for non-tenant-scoped operations).
2. Otherwise, check the tenant role via `hasPermission(role, resource, action)`.

The `Role` type should be updated to explicitly distinguish:
```typescript
export type TenantRole = 'owner' | 'admin' | 'member';
export type PlatformRole = 'user' | 'platform_admin';
```

### Epic 18 (Stories 18-2, 18-3)

Epic 18's JWT payload uses `role` for the tenant role and `platformRole` for the platform role. The `UnifiedJwtPayload` interface (defined in Story 18-2) is the canonical JWT structure.

### Epic 9 (Agent Config API)

API endpoints in Epic 9 are tenant-scoped (config is per-account). The `accountId` is derived from the JWT `tenantId`. RBAC checks use the tenant role. Platform admins can access any tenant's config.

## Self-Hosted / CLI Mode

In self-hosted or CLI mode, there is a single default tenant (`DEFAULT_TENANT_ID` sentinel). The user is implicitly `owner` of the default tenant and `platform_admin` at the platform level. No role checks are enforced in CLI mode.

## Migration from Epic 16 Roles

The existing `users.role` column (from Epic 16) stores tenant-level roles for the platform's original single-tenant model. When migrating to multi-tenant:

1. The `users.role` column is renamed or replaced by `users.platform_role` (values: `'user'` or `'platform_admin'`).
2. Existing `owner` users become `platform_admin` + `owner` in the default tenant.
3. Existing `admin` users become `user` + `admin` in the default tenant.
4. Existing `member` users become `user` + `member` in the default tenant.

## References

- **Epic 16 Story 16-5**: `/home/meywd/tamma/docs/stories/epic-16/16-5-role-based-access-control.md`
- **Epic 18 Story 18-2**: `/home/meywd/tamma/docs/stories/epic-18/18-2-user-login-session-management.md` (Unified JWT Payload Contract section)
- **Epic 18 Story 18-3**: `/home/meywd/tamma/docs/stories/epic-18/18-3-organization-tenant-creation.md` (Org-Scoped RBAC section)

---

**Last Updated**: 2026-04-09
**Owner**: Architecture Team
