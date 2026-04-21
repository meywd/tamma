# Story 29-5 Implementation Plan — Tenant-Admin Secret Management UI

**Status**: Planned (2026-04-20)
**Story brief**: [`29-5-tenant-admin-ui.md`](./29-5-tenant-admin-ui.md)
**Epic 29 phase**: UI layer — after 29-4 and 18-5.
**Branch**: `feat/story-29-5-tenant-secrets-ui`

---

## 1. Objective

Ship `dash.tamma.dev/secrets` for tenant administrators. Same
create/rotate/retire surface as the platform UI (reuses 29-4
components) but scoped to the current tenant via RLS + RBAC +
repository filter + store assertion (four-layer defense-in-depth).
Platform admins can inspect a tenant's list read-only through
`/admin/tenants/{id}/secrets`.

## 2. Dependencies

Hard blockers:

- **Story 18-5** — `packages/dashboard-user/` shell.
- **Story 28-9** — switch-org for multi-tenant users.
- **Story 29-4** — shared components.
- **Story 19-6** — app-role RLS enforcement.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/packages/dashboard-user/src/pages/secrets/SecretsPage.tsx` | Tenant-scoped list. |
| `.../pages/secrets/SecretDetailPage.tsx` | Tenant detail. |
| `/home/meywd/tamma/packages/dashboard-user/src/api-client/secrets.ts` | Tenant-scoped API client. |
| `/home/meywd/tamma/packages/dashboard/src/admin/secrets/TenantViewAsPage.tsx` | Platform-admin "view as tenant" read-only. |
| `/home/meywd/tamma/packages/dashboard-user/e2e/secrets-isolation.spec.ts` | Cross-tenant isolation E2E. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/packages/dashboard-user/src/router.tsx` | Add `/secrets` route (role-gated). |
| `/home/meywd/tamma/packages/dashboard-user/src/layouts/AppLayout.tsx` | Sidebar link under "Settings" with overdue badge. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/SecretEndpoints.cs` (from 29-3) | Add scope-aware filtering on `GET /api/v1/secrets`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Secrets/PostgresSecretStore.cs` | Add `Scope==Tenant && TenantId==currentTenantId` assertion on `GetAsync` (fourth layer). |

## 5. Sequence of changes

### Step 1 — Shared component extraction (3h)

- Extract `SecretDetailDrawer`, `CreateSecretForm`, `RotateSecretDialog`,
  `RevealModal`, `ConsumerLink` from `packages/dashboard` into
  `packages/dashboard/src/admin/secrets/shared/` (or
  `packages/ui/secrets/` if the team prefers; coordinate with Team C's
  shared-UI decision from 18-5).
- **Commit**: `refactor(ui): extract secrets shared components`.

### Step 2 — Tenant API client (2h)

- `secrets.ts` for dashboard-user — hits tenant-scoped endpoints
  with credentials.
- **Commit**: `feat(tenant-ui): secrets API client`.

### Step 3 — Tenant pages (5h)

- `SecretsPage` — thin wrapper, tenant-scope parameter.
- `SecretDetailPage` — reuses drawer.
- Empty state copy (AC9).
- **Commit**: `feat(tenant-ui): secret list + detail pages`.

### Step 4 — RBAC + nav (2h)

- Route guard: `tenant_owner` | `tenant_admin`.
- Sidebar link.
- Overdue badge: count of `NextRotationDueAt < now()`.
- **Commit**: `feat(tenant-ui): RBAC + overdue nav badge`.

### Step 5 — Platform "view as tenant" (2h)

- `/admin/tenants/{id}/secrets` in admin dashboard renders
  read-only list with banner "Viewing as tenant X".
- Create/rotate disabled.
- **Commit**: `feat(admin-ui): view-as-tenant secrets`.

### Step 6 — Defense-in-depth verification (3h)

- Server: scope assertion in `PostgresSecretStore.GetAsync`.
- Cross-tenant isolation E2E:
  - Tenant A creates secret.
  - Tenant B (different session) requests by secret ID directly → 404.
  - Tenant B's reveal-token attempt on A's token → 403.
- **Commit**: `feat(secrets): defense-in-depth + cross-tenant E2E`.

### Step 7 — Empty state + a11y (3h)

- Empty state copy + illustration.
- axe-clean.
- **Commit**: `feat(tenant-ui): empty state + a11y`.

## 6. Test strategy

### Unit

- Route guard for role combinations.
- Overdue badge computation.

### Integration

- React Query hooks against MSW shapes.

### E2E

- Tenant A creates → tenant B doesn't see.
- Platform admin view-as-tenant read-only verified.
- Switch-org between tenants A and B flips the list content.

### Security

- Server-side reveal token isolation: attempt to redeem tenant A's
  token from tenant B's session → 403.

## 7. Rollback plan

- **Feature flag**: `TenantUI:Secrets=true` hides pages + links.
- **Non-reversible**: none.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Shared extraction | 3 |
| 2. API client | 2 |
| 3. Tenant pages | 5 |
| 4. RBAC + nav | 2 |
| 5. View-as-tenant | 2 |
| 6. Defense-in-depth | 3 |
| 7. Empty state + a11y | 3 |
| **Total** | **20** (matches brief). |

## 9. Open questions

- **Shared components location**: inline in dashboard vs. new
  `packages/ui/`. Plan: follow 18-5's decision (inline-first,
  extract later).
- **Overdue calculation**: server computes + returns a flag;
  client filters. Avoids client-side time-skew issues.
- **Platform-admin impersonation for write actions**: not in this
  story. Uses switch-org per Doc 01.
- **RLS in tests**: connects as `tamma_app`; test fixture creates
  role + grants.
- **Tenant cannot generate platform-scoped secret**: enforced by
  RBAC + endpoint split (`/admin/secrets` vs. `/secrets`).
