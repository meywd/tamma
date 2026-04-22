# Story 29-5: Tenant-Admin Secret Management UI

Status: todo (planning brief, 2026-04-20)

## Story

As a **tenant administrator**,
I want a secret-management page at `dash.tamma.dev/secrets` that lists only my tenant's secrets (database credentials, per-tenant API keys, webhook HMACs) with the same create/rotate/retire/history surface as the platform-admin UI,
so that I can rotate my tenant's DB password or API keys without asking a platform admin — matching the user's design intent: "tenant admins can generate and edit these passwords, but that means auto-generate and update, since they can't access dbs directly. Platform works the same for admin."

## Acceptance Criteria

1. Route `dash.tamma.dev/secrets` in the user dashboard (the `packages/dashboard-user/` app from Story 18-5) lists **only** the current tenant's secrets. The endpoint `GET /api/v1/secrets` returns `Scope = tenant` rows filtered to `TenantId = currentTenant`. RLS on `tenant_secrets` provides defense-in-depth: even if the endpoint forgot the filter, Postgres returns zero rows for the app-role connection.
2. Component shape reuses `SecretDetailDrawer`, `CreateSecretForm`, `RotateSecretDialog`, `RevealModal`, `ConsumerLink` from Story 29-4 (same files; the admin and tenant pages are thin wrappers over the shared components parameterised by scope).
3. Tenant admins cannot see platform secrets; a tenant-admin who attempts to `GET /api/v1/admin/secrets` is 403'd by RBAC (Epic 16) + RLS defense-in-depth.
4. Platform admins can optionally "view as tenant" to inspect a tenant's secret list — routed through `/admin/tenants/{id}/secrets` page with a visible banner "Viewing as tenant X (read-only)". Create / rotate is disabled from this view; platform admins use the tenant admin's own flow (switch-org per 28-9) to make changes.
5. Rotation handlers that need tenant-specific context (e.g. "rotate this tenant's `tamma_app` DB password") receive the `TenantId` from the workflow's resolved tenant scope — not from the UI request body. Prevents a tenant admin from requesting rotation of another tenant's secret by tampering with the payload.
6. RBAC within the tenant: the roles that can manage secrets are `tenant_owner` and `tenant_admin` (per Epic 16 RBAC matrix). `tenant_member` gets 403 on the `/secrets` route + endpoints.
7. UI shows a "Consumers" column that is tenant-aware: the typed `ConsumerRef` for a tenant-scoped DB password resolves to "Your tenant DB (role=tamma_app)" and links to the tenant's own runtime health page rather than a platform runbook.
8. E2E test (Playwright): tenant A admin creates a secret; log in as tenant B admin; assert the secret is not visible in the list or accessible by direct URL; assert the reveal token from tenant A is not valid when called from tenant B session.
9. Empty state copy for new tenants: "No secrets yet. When you create a tenant-scoped DB user, Cranl API key, or webhook HMAC, it shows up here. [Create your first secret]".
10. `/secrets` route is linked from the tenant dashboard side nav under "Settings" with a subtle badge if any secret has `NextRotationDueAt` in the past (overdue).

## Technical Context

### Defense-in-depth layering

1. RBAC at endpoint — `tenant_admin` or `tenant_owner` required.
2. Tenant filter at the repository level — `TammaAppDbContext` tenant filter (Story 19-6).
3. RLS on `tenant_secrets` — `secret_isolation_policy` matches `app.current_tenant_id`.
4. Additional scope check in `ISecretStore.GetAsync` — asserts `SecretMetadata.Scope == Tenant && TenantId == currentTenantId` before returning.

Any two layers could be removed and the remaining two still block cross-tenant access.

### Reuse vs. fork

The admin and tenant UIs share enough structure that they're in the
same component set. They differ on:

- Scope filter (admin sees `platform`; tenant sees `tenant`).
- Consumer link rendering (admin sees platform-facing links; tenant
  sees tenant-facing links).
- Role gate.
- `GET` endpoint path.

All three differences are parameterised rather than forked.

### Tenant-scoped secret examples

Typical rows for a tenant:

| Name | Purpose | Consumers | Rotation |
|---|---|---|---|
| `db/app-role` | DbCredential | postgres: role=tamma_app | Every 90 days |
| `cranl/api-key` | ApiKey | cranl: app=app_xyz | Every 180 days |
| `webhook/github/hmac` | HmacSharedSecret | github_webhook: installation=12345 | None (or cascade-on-key-rotation) |
| `engine/shared-hmac` | HmacSharedSecret | tamma-engine: request-signing | Every 30 days |

## Estimated hours

20 — routing + page + scope-aware wrappers + cross-tenant isolation
test + Playwright coverage + empty state + navigation wiring.

## Files to touch

- `packages/dashboard-user/src/pages/secrets/` (new folder)
- `packages/dashboard-user/src/api-client/secrets.ts` (new)

## References

- Dashboard-user shell: Story 18-5
- Shared components: Story 29-4
- RBAC model: Epic 16 Story 16-3, `docs/stories/rbac-unified-model.md`
- Switch-org: Story 28-9
