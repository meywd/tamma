# Orgs / Tenants Port-Gap Findings

**Scope**: `apps/tamma-elsa/src/Tamma.Api/Endpoints/OrgEndpoints.cs` and tenant middleware / persistence.
**Source audit**: `/tmp/tamma-audit/30-orgs.md`
**TS baseline**: `packages/api/src/routes/orgs/index.ts` at `9e9a57c~1` (deleted in `9e9a57c`).
**Story references**: `docs/stories/epic-17/*` (tenancy + RLS), `docs/stories/epic-18/18-3-organization-tenant-creation.md`.

This is the largest production-risk surface of the C# cutover. The TS API shipped 13 tenant/org endpoints with slug/role/role-hierarchy/last-owner/HMAC-confirm / email-dispatch / cookie-set / RLS plumbing; the C# port is a shallow CRUD re-draw with most invariants and defense-in-depth stripped out.

## Findings

| # | Severity | Title |
|---|---|---|
| 001 | P0 | Cross-tenant read/write: path `tenantId` accepted without membership verification |
| 002 | P0 | EF query filter permissive when `TenantContext.TenantId` is null |
| 003 | P0 | RLS policies (archived migration 010) entirely absent |
| 004 | P0 | `withTenantContext` / `SET LOCAL app.current_tenant_id` gone |
| 005 | P0 | `prevent_tenant_id_change` trigger not ported |
| 006 | P1 | Default tenant sentinel `00000000-…` not seeded |
| 007 | P1 | `POST /orgs` skips name/slug validation and reserved-slug list |
| 008 | P2 | `POST /orgs` emits no `TENANT.CREATED.SUCCESS` event |
| 009 | P2 | `POST /orgs` does not call `UpdateActiveTenantAsync` |
| 010 | P2 | `PUT /orgs/:id/settings` cannot rename and drops length validation |
| 011 | P2 | `GET /orgs/:id/members` missing 100-row limit cap and membership gate |
| 012 | P0 | `PUT /orgs/:id/members/:uid/role`: no role validation, no role hierarchy, no last-owner guard |
| 013 | P0 | `DELETE /orgs/:id/members/:uid`: no hierarchy check, no last-owner guard, no active-tenant cleanup |
| 014 | P1 | `POST /orgs/:id/invites`: weak Guid token, no email sent, raw token returned in response body |
| 015 | P3 | `GET /orgs/:id/invites`: response omits `InvitedBy` |
| 016 | P3 | `DELETE /orgs/:id/invites/:iid`: swallowed 404 |
| 017 | P1 | `POST /orgs/invites/accept`: 500 on re-accept, no active-tenant update, no event |
| 018 | P1 | `POST /auth/switch-org`: does not set `tamma_session` cookie |
| 019 | P2 | `GET /tenants`: response missing `role` / `joinedAt` / `isActive` fields |
| 020 | P0 | `POST /orgs/:id/transfer-ownership`: non-atomic, dual source-of-truth (`OwnerId` column + membership role) |
| 021 | P0 | `DELETE /orgs/:id`: one-phase soft delete; no HMAC confirmation, no last-tenant guard, no cascade |
| 022 | P3 | `EnsurePersonalTenantMiddleware`: slug format diverged, no collision retry, no event |
| 023 | P0 | `TenantContextMiddleware`: JWT-only source, no 403 on unresolved, no installation/user fallback |
| 024 | P0 | `requireTenant` middleware has no C# equivalent |
| 025 | P2 | `tenant_memberships.role` CHECK constraint lost |
| 026 | P3 | `tenant_memberships` PK changed from composite `(tenant_id, user_id)` to surrogate `Id` |
| 027 | P1 | `tenant_invites` table absent — conflated with `user_invites` |

## Priority buckets

- **P0 (cutover-blocking, security/correctness)**: 001, 002, 003, 004, 005, 012, 013, 020, 021, 023, 024 — 11 findings
- **P1 (feature broken end-to-end)**: 006, 007, 014, 017, 018, 027 — 6 findings
- **P2 (correctness / observability gaps)**: 008, 009, 010, 011, 019, 025 — 6 findings
- **P3 (drift / contract)**: 015, 016, 022, 026 — 4 findings

## Cross-cutting themes

1. **Defense-in-depth stripped**: TS shipped four layers — app-level `WHERE tenant_id`, `withTenantContext` SET LOCAL, RLS policies, and a `prevent_tenant_id_change` trigger. C# keeps only the app-level layer (findings 003–005), and even that is softened by a permissive null-coalescing EF filter (finding 002). Any raw SQL from Elsa, `psql`, or ADO.NET completely bypasses tenant isolation.
2. **Path tenantId is trusted**: Findings 001, 012, 013 all share the same root cause — no middleware asserts that `route.tenantId == jwt.tid` or that the caller is a member of the **path** tenant. `MemberAccess` policy only checks the JWT is authenticated; `AdminAccess` only checks a platform-wide permission. This is the single highest-impact security gap.
3. **Role lifecycle has no guard rails**: Findings 012, 013, 020, 025 — arbitrary role strings accepted, no hierarchy ("admin cannot touch owner"), no last-owner guard, `OwnerId` column duplicates membership data and drifts.
4. **Invite flow non-functional**: Findings 014, 017 — Guid token has 122 bits of entropy vs TS 256 bits, no email is dispatched at all, the raw token leaks to HTTP access logs, and re-accepting throws because there is no pre-check.
5. **Session/cookie semantics diverged**: Finding 018 — dashboard org switcher is broken because the C# switch-org returns an access token in JSON only, while the TS path set the `tamma_session` httpOnly cookie on `.tamma.dev`.

## Estimated total remediation

~36-48 hours per the source audit. This folder's per-finding effort sums track closely.
