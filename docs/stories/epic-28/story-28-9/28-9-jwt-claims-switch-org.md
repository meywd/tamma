# Story 28.9: JWT Claims + `/auth/switch-org` + Refresh Tokens Across Tenants

**Epic**: Epic 28 - Database-per-Tenant Isolation
**Category**: Auth
**Status**: MOSTLY DONE — AC3 (refresh-token tenant binding + reuse-detection) shipped 2026-05-29; AC3 follow-ups (`AUTH.REFRESH_REUSE_DETECTED` platform_events emission) shipped 2026-05-30; `tenant_mismatch_on_refresh` 400 marked intentionally-not-implemented per design (see Implementation Notes section below). Audit reference: `docs/superpowers/plans/2026-05-29-epic-28-status-audit.md`. Residuals: AC1 (`jti` + `tenantSlug` claim verification), AC2 5-step atomicity verification, AC6 logout-all path.
**Priority**: High (without per-tenant-scoped JWTs plus a switch-org
endpoint, users with memberships in more than one tenant cannot
navigate between them without re-logging-in, refresh tokens leak
across tenants, and role demotion inside one tenant applies to all)
**Estimated Effort**: L (24h)

## User Story

As a **user with memberships in more than one tenant**, I want **my
access token to carry the active tenant id and per-tenant role, plus
an atomic `/auth/switch-org` endpoint that revokes my old refresh
token and issues a new token pair scoped to the target tenant**, so
that **my role demotion in tenant A does not leak admin rights to
tenant B, my refresh token cannot be replayed against a different
tenant, and my experience is "one login, many workspaces" without
re-entering credentials**.

## Acceptance Criteria

### AC1: JWT claim set matches Doc 01 §2.5

The access token carries exactly these claims:

- [ ] `sub` — user id (UUID), the stable identity.
- [ ] `tenantId` — active tenant id (UUID), or **absent** in rootless
      mode (user has memberships but no active selection after login
      / refresh).
- [ ] `tenantSlug` — active tenant slug (for display in dashboards
      and breadcrumbs without a separate CP lookup).
- [ ] `role` — role in the active tenant (`owner`, `admin`, `member`,
      `viewer`), sourced from `tenant_memberships.Role` at token
      issue time. Absent when `tenantId` is absent.
- [ ] `isPlatformAdmin` — boolean, sourced from
      `users.IsPlatformAdmin`. **Orthogonal** to `role`: a platform
      admin still carries per-tenant `role` when a tenant is active
      (impersonation is separate per Story 28-8 AC5). A rootless
      platform admin carries `isPlatformAdmin=true` with no
      `tenantId`.
- [ ] `email` — user email (for display; not the authentication key).
- [ ] `iat` — issued-at (seconds).
- [ ] `exp` — expires-at (seconds). 15 minutes.
- [ ] `jti` — token id (UUID v7), used for the `/auth/logout?all=true`
      revocation path (AC6) and correlation into `platform_events`.

Removed versus the pre-epic token:

- [ ] `users.Role` is no longer a JWT source. The `PermissionHandler`
      reads `ClaimTypes.Role` (mapped from `role` above) exclusively.
      The old `users.Role` column stays for backward compatibility
      through this story's deploy; a follow-up drops it.

### AC2: `POST /api/v1/auth/switch-org` atomic handover

- [ ] Request shape: `{ "tenantId": "<uuid>" }`. Request carries a
      valid access token (rootless or tenant-scoped). The endpoint
      is on the `TenantFreePaths` allowlist per Story 28-8 AC1 —
      the middleware does not try to resolve the **current** JWT's
      tenant.
- [ ] Validation pipeline (each failure returns the indicated
      status):
  1. Token is a valid, non-expired access token → else 401.
  2. Target `tenants` row exists and `Status='active'` → else
     - `Status='pending_verification'` or `'provisioning'` → 503
       with `Retry-After: 5` and `progressUrl`.
     - `Status='failed'` → 424 per Story 28-8 AC2.
     - `Status='deleted'` → 410.
     - Row absent → 404.
  3. User has a non-revoked row in `tenant_memberships` with
     `UserId=<sub>, TenantId=<target>` → else 403.
- [ ] On success, atomically (single CP transaction plus a
      RabbitMQ event publish after commit):
  1. Revoke the previous refresh token: `UPDATE refresh_tokens SET
     RevokedAt=NOW(), RevokedReason='switch_org' WHERE Id=<prev_rtid>`.
     The previous refresh token id is embedded in the current access
     token's `jti` chain (see AC3 for how the link is kept).
  2. Insert a new `refresh_tokens` row stamped with the **target**
     `TenantId` column and a fresh 14-day expiration.
  3. Issue a new access token scoped to the target tenant per AC1.
  4. Emit `AUTH.TENANT_SWITCHED` to `platform_events` with `tags: {
     userId, previousTenantId, tenantId, jti }`.
- [ ] Response body: `{ "accessToken": "...", "refreshToken": "...",
      "tenant": { "id": "...", "slug": "...", "role": "..." } }`
      matching the existing `LoginResponse` shape.
- [ ] Concurrent switch-org calls from the same user are serialised
      by a CP `SELECT ... FOR UPDATE` on the user's current
      `refresh_tokens` row — the second caller waits, then observes
      the first caller's new row and issues its own pair (the
      second call's `previousTenantId` is the target of the first).

### AC3: Refresh tokens are tenant-scoped and never cross

- [ ] `refresh_tokens` table gains `TenantId UUID NULL` (nullable for
      rootless refresh tokens issued on login with no active tenant)
      and `JtiChainHead UUID NULL` — links a refresh token to the
      first access-token `jti` issued from it, so rotation can trace
      a session lineage.
- [ ] `POST /api/v1/auth/refresh` reads the presented refresh token,
      validates not expired / not revoked, issues a new access +
      refresh token pair scoped to the **same** `TenantId`. A
      refresh token with `TenantId=<A>` can NEVER mint an access
      token for tenant B — ~~a request asserting a different
      target returns 400 `{ "error": "tenant_mismatch_on_refresh",
      "action": "POST /api/v1/auth/switch-org" }`~~. **Intentionally
      not implemented (2026-05-30)**: `RefreshRequest` is
      `{ refreshToken }` — there is no target-tenant field for a
      caller to "assert" against. Refresh is single-tenant by
      design (the token IS the tenant binding), so a 400 with the
      `tenant_mismatch_on_refresh` code is unreachable. The DB-side
      enforcement (token.TenantId is the source of truth, membership
      loss returns 401 with `action: switch-org`) IS in place; the
      400 path that the AC text described is an error class the new
      DTO contract cannot produce. Switch-org is the cross-tenant
      path. See Implementation Notes for the design call.
- [ ] Refresh rotates: the incoming refresh token is marked
      `RevokedAt=NOW()` and the new one issued in a single CP
      transaction. Presenting the revoked token a second time
      triggers the **refresh-reuse detection**: revoke the entire
      session lineage (`JtiChainHead`) and emit
      `AUTH.REFRESH_REUSE_DETECTED` to `platform_events` with the
      offending user + IP.
- [ ] Refresh picks up **mid-session role changes inside the active
      tenant**: on each refresh, re-query
      `tenant_memberships.Role` for `(UserId, TenantId)` and bake
      the fresh value into the new access token's `role` claim.
      This is the mitigation for the 15-minute-demotion window per
      Doc 01 §2.4.

### AC4: Legacy token-without-`tenantId` graceful rejection

- [ ] An access token issued before this story's deploy does not
      carry `tenantId`. On a protected endpoint, the middleware
      (Story 28-8 AC1) returns 409 `no_active_tenant` → the client
      must call `/auth/switch-org` or re-login.
- [ ] `/auth/switch-org` accepts such legacy tokens for exactly one
      transition: after verifying the legacy token's `sub` against
      the user's memberships, it issues the new tenant-scoped pair.
      This is the opt-in upgrade path.
- [ ] The login endpoint is unchanged in contract but updated in
      implementation: when the user has exactly one active
      membership, login issues a tenant-scoped token targeting that
      tenant; when the user has zero active memberships, login
      issues a rootless token (no `tenantId`); when the user has
      multiple, login issues a rootless token and the client calls
      `/auth/switch-org` to pick one.

### AC5: Per-tenant role + orthogonal platform admin

- [ ] `PermissionHandler` reads `ClaimTypes.Role` (mapped from
      `role`) for per-tenant permission checks. Unchanged API
      surface — only the claim source changes.
- [ ] A new `IsPlatformAdminHandler` authorisation handler at
      `apps/tamma-elsa/src/Tamma.Api/Auth/IsPlatformAdminHandler.cs`
      gates `/api/admin/*` on `isPlatformAdmin=true`. The handler is
      registered with policy name `PlatformAdmin` per Doc 04 §3.4.
- [ ] The two policies are **independently evaluable**: a user who
      is `isPlatformAdmin=true` but `role=viewer` in the current
      tenant gets admin-on-platform rights but not admin-in-tenant
      rights. This is the correct behaviour for support staff who
      impersonate without elevating.
- [ ] `token_revocations` consultation (Doc 01 §2.4) is confined to
      `/api/admin/*` — the `IsPlatformAdminHandler` queries the
      table with a 1-minute in-process cache. Normal paths stay
      query-free. A demoted admin is invalidated within 1 minute
      on admin routes regardless of the 15-minute access-token
      window.

### AC6: `/auth/logout` revocation across tenants

- [ ] `POST /api/v1/auth/logout` with empty body → revoke only the
      current session's refresh token (existing behaviour).
- [ ] `POST /api/v1/auth/logout?all=true` → revoke **every**
      non-revoked refresh token where `UserId=<sub>`, across all
      tenants. Plus insert a `token_revocations` row for each
      distinct `(UserId, TenantId)` so in-flight admin access
      tokens invalidate within 1 minute per Doc 01 §2.4.
- [ ] Emit `AUTH.LOGOUT_ALL` to `platform_events` with
      `{ data: { tenantCount: <n>, reason: "user_initiated" } }`.
- [ ] `DELETE /api/admin/users/{userId}/sessions` (platform admin
      only) provides the same `logout-all` behaviour targeting
      another user — for the "admin fired, revoke now" workflow in
      Doc 01 §2.4.

### AC7: Instrumentation

- [ ] Counter `tamma_auth_switch_org_total{outcome}` with outcomes
      `success | denied_not_member | denied_tenant_not_active |
      error`.
- [ ] Counter `tamma_auth_refresh_reuse_detected_total` — any
      non-zero value pages ops (Doc 01 §2.4 security alert).
- [ ] Histogram `tamma_auth_switch_org_duration_ms` with p95 target
      < 50ms (the full revoke + insert + token-issue round trip).
- [ ] Log line on every switch: `log.Info("auth.switch_org",
      userId, previousTenantId, tenantId, outcome)`. Never log the
      access or refresh token.

## Technical Context

- **Design docs**:
  - `plans/db-per-tenant/01-control-plane-split.md` §2.4 (permission
    model: JWT-baked role + `token_revocations` on admin paths) and
    §2.5 (JWT claim table — source of truth for AC1).
  - `plans/db-per-tenant/03-async-tenant-provisioning.md` §6.1
    (`/auth/me` shape during provisioning; the `tenants` array
    returned here is what the dashboard consumes before calling
    `/auth/switch-org`).
  - `plans/db-per-tenant/04-connection-pool-and-delete.md` §3.4
    (`IsPlatformAdmin` flag and policy-gated admin routes — AC5's
    `PlatformAdmin` policy matches this).
  - Epic 28 README conflict resolution #1 (provisioning gate at
    verify-email) — the `Status='active'` check in AC2 is the
    consumer side of that gate.
- **File layout**:
  - `apps/tamma-elsa/src/Tamma.Api/Auth/JwtService.cs` — modified;
    `IssueAccessToken(userId, tenantId?, role?, isPlatformAdmin,
    jti)` signature.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` —
    modified; new `POST /api/v1/auth/switch-org` handler.
  - `apps/tamma-elsa/src/Tamma.Api/Auth/SwitchOrgHandler.cs` — new,
    extracts the validation + transition logic for testability.
  - `apps/tamma-elsa/src/Tamma.Api/Auth/IsPlatformAdminHandler.cs` —
    new, the authorisation handler + policy registration in
    `Program.cs`.
  - `apps/tamma-elsa/src/Tamma.Data/Entities/RefreshToken.cs` —
    modified; add `TenantId UUID NULL` + `JtiChainHead UUID NULL`.
  - `apps/tamma-elsa/src/Tamma.Data/Entities/TokenRevocation.cs` —
    new (Doc 01 §2.4).
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/04x_jwt_claims_refresh_scope.cs`
    — new, coordinates migration number with 28-10 + 28-11 per the
    sequencing doc's "Safe with caveats" list.
- **Shared service extracted from Story 28-8**:
  - `apps/tamma-elsa/src/Tamma.Api/Services/TenantStatusEvaluator.cs`
    — evaluates `tenants.Status` → HTTP-code + body shape. Used by
    both `TenantContextMiddleware` (Story 28-8) and
    `SwitchOrgHandler` (this story) so the state-machine response
    contract is defined once.

## Dependencies

- **Blocks**: none in Epic 28; this story is the third serial step
  in Phase 2 per `00-sequencing.md`.
- **Blocked by**: 28-2 (`ControlPlaneDbContext` — all refresh and
  revocation writes go through it), 28-4 (resolver — not directly,
  but `TenantStatusEvaluator` delegates to the resolver's tenant
  lookup for the `Status` column), 28-8 (`TenantStatusEvaluator`
  is first introduced in 28-8; this story extends it with the
  active-membership check).
- **External**: the existing Npgsql + EF Core 8 setup, RabbitMQ
  topic `tamma.platform.events`.

## Test Plan

### Unit tests

- `JwtServiceTests` — assert claim set round-trip:
  - Tenant-scoped token → decodes with exactly `sub, tenantId,
    tenantSlug, role, isPlatformAdmin, email, iat, exp, jti`.
  - Rootless token → no `tenantId`, no `tenantSlug`, no `role`.
  - Platform admin + no active tenant → `isPlatformAdmin=true`,
    no `tenantId`.
- `SwitchOrgHandlerTests`:
  - Happy path: rootless token → pick tenant A → returns new pair
    scoped to A, old refresh token revoked, event emitted.
  - Target `Status='provisioning'` → 503 + `Retry-After: 5`.
  - Target `Status='failed'` → 424.
  - Target `Status='deleted'` → 410.
  - No membership → 403.
  - Concurrent switch-org calls → second call sees the first's
    rotated refresh token, succeeds with `previousTenantId` = first
    caller's target.
- `RefreshHandlerTests`:
  - Refresh with `TenantId=<A>` token requesting tenant B → 400
    `tenant_mismatch_on_refresh`.
  - Refresh reuse: present a revoked refresh token → entire
    lineage revoked, `AUTH.REFRESH_REUSE_DETECTED` event.
  - Mid-session role change: demote user from `admin` to
    `viewer` in tenant A, refresh → new access token has
    `role=viewer`.
- `IsPlatformAdminHandlerTests`:
  - `isPlatformAdmin=true` + no `token_revocations` row → allow.
  - `isPlatformAdmin=true` + `token_revocations` row for user →
    deny.
  - `isPlatformAdmin=false` → deny on `/api/admin/*`.
  - `isPlatformAdmin=true` + `role=viewer` on tenant-scoped policy
    → deny (independence of AC5).

### Integration tests (Testcontainers.PostgreSQL)

- **T1 End-to-end switch-org**: register → login → create second
  tenant via invitation → `POST /auth/switch-org` → access the
  second tenant's data with the new token → `platform_events` has
  `AUTH.TENANT_SWITCHED`.
- **T2 Refresh-token reuse detection**: obtain a refresh token →
  refresh once (get pair B) → try to refresh with the old token
  (A) → 401 + `AUTH.REFRESH_REUSE_DETECTED` emitted → pair B is
  also revoked, next refresh with B also 401.
- **T3 Legacy token upgrade**: manually craft a legacy JWT (no
  `tenantId` claim) with a valid `sub` → present to
  `/auth/switch-org` → receive new tenant-scoped pair.
- **T4 Logout-all**: issue sessions across 3 tenants for one user
  → `POST /auth/logout?all=true` → assert all 3 refresh tokens
  revoked, 3 `token_revocations` rows exist, subsequent admin-route
  requests with still-warm access tokens return 401 within 1 min
  (cache expiry).
- **T5 Admin force-logout**: platform admin calls `DELETE
  /api/admin/users/{userId}/sessions` → same effect as T4 for the
  target user; emits `AUTH.ADMIN_FORCE_LOGOUT` with acting admin's
  id.
- **T6 Role independence**: a user who is `isPlatformAdmin=true`
  and `role=viewer` in tenant A accesses `/api/admin/tenants` (200)
  and attempts a tenant-write (403 per role).
- **T7 Refresh cross-tenant block**: present a refresh token
  scoped to tenant A with `targetTenant=B` in the request → 400
  `tenant_mismatch_on_refresh` + no token issued.

### Manual verification

- Local dev: create one user with memberships in two tenants.
  Login, observe the rootless token. Call `/auth/switch-org`
  against each → dashboard shows each tenant's workspace.
- Revoke a tenant mid-session (`DELETE /api/admin/tenants/{id}`,
  wait grace) → the still-warm access token's next request
  returns 410 per Story 28-8. Refresh fails with 400.

## Definition of Done

- [ ] AC all green
- [ ] Unit + integration tests added, suite passes
- [ ] No new CodeQL alerts (review JWT library usage: ensure
      `SymmetricSecurityKey` is not logged and the `jti` generation
      uses a CSPRNG)
- [ ] Design-doc references updated if the impl deviated
- [ ] Reviewed by a second engineer (cross-stream)
- [ ] `token_revocations` table indexed on `(UserId, RevokedAt)`
      and `(UserId, TenantId, RevokedAt)` for the admin-path hot
      query (validated by EXPLAIN ANALYZE against a seeded dataset
      of 100k rows)

## Implementation notes for AC3 (2026-05-29)

AC3 (refresh-token tenant binding + reuse-detection) shipped on `feat/wave-b`.
Closes the gap flagged by the 2026-05-29 Epic 28 audit.

**Schema:**
- `apps/tamma-elsa/src/Tamma.Data/Entities/RefreshToken.cs` — added
  `TenantId UUID NULL`, `JtiChainHead UUID NULL`, `RevokedReason VARCHAR(32) NULL`.
  Closed-enum constants in `RefreshTokenRevokedReasons` (manual_logout, logout_all,
  rotation_consumed, switch_org, reuse_detected, password_reset, admin_force_logout).
- Migration: `Migrations/ControlPlane/20260529125335_Story28_9_RefreshTokenTenantBinding.cs`.
  Adds the three columns + two partial indexes (`IX_refresh_tokens_JtiChainHead`,
  `IX_refresh_tokens_UserId_TenantId`) + two CHECK constraints
  (closed enum on `RevokedReason`, NULL-parity between `RevokedAt` and `RevokedReason`).

**Repository (`apps/tamma-elsa/src/Tamma.Data/Repositories/RefreshTokenRepository.cs`):**
- New overload `CreateAsync(userId, tenantId, hash, expiresAt, jtiChainHead)`
  for tenant-bound issuance; legacy 3-arg overload preserved for transitional callers.
- New `RevokeAsync(id, reason)` + `RevokeAllForUserAsync(userId, reason)`
  overloads carrying explicit revoke reasons; legacy overloads default to
  `manual_logout` / `logout_all`.
- New `FindByJtiChainHeadAsync(chainHead)` and `RevokeChainAsync(chainHead, reason)`
  for reuse-detection.
- Client-side guard `EnsureKnownReason` rejects unknown reasons with
  `ArgumentException` before the DB CHECK fires.

**Endpoint changes (`apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs`):**
- `Login` mints refresh row with the active tenant (NULL when rootless) and a
  fresh `JtiChainHead`.
- `Refresh` re-resolves the role for the bound tenant on every rotation
  (the mid-session demotion catch-up), reads `token.TenantId` as the binding
  source of truth, propagates `JtiChainHead` to the rotated row, stamps the
  consumed row with `rotation_consumed`. Reuse-detection (revoked-then-replayed)
  burns the entire lineage via `RevokeChainAsync(chainHead, reuse_detected)`;
  pre-Story-28-9 rows with NULL chain head fall back to the previous
  `RevokeAllForUserAsync` semantics.
- `SwitchOrg` revokes the presented refresh row with `switch_org`, bulk-revokes
  with `switch_org` when no token is in the body, and mints a NEW chain head
  for the target-tenant lineage (the source-tenant chain terminates at
  switch-org).
- `Logout(?all=true)` tags the bulk revoke with `logout_all`; per-token logout
  uses `manual_logout`.
- `PasswordResetConfirm` uses `password_reset` reason.

**Tests:**
- `tests/Tamma.Api.Tests/Auth/RefreshTokenTenantBindingTests.cs` — entity shape,
  model graph, repository CRUD with new columns, CHECK constraints (32 assertions).
- `tests/Tamma.Api.Tests/Auth/RefreshTokenReuseDetectionTests.cs` —
  end-to-end reuse-detection via the Refresh handler (chain burn, sibling 401,
  pre-Story-28-9 fallback path).
- `tests/Tamma.Api.Tests/Auth/SwitchOrgRefreshTokenTests.cs` —
  tenant binding on the new refresh row, new chain head on switch-org,
  `switch_org` revoke reason on both single-token and bulk-revoke paths.
- All existing Auth + Epic28 tests (311 + 272) continue to pass.

**Run:** `sg docker -c "dotnet test apps/tamma-elsa/Tamma.sln"`.

**Out of scope (AC1 / AC2 / AC4–AC7 parts):** the audit's other AC3-adjacent
notes (jti claim explicit in tokens, `tenant_mismatch_on_refresh` 400 — the
current `RefreshRequest` DTO has no target-tenant field, so that error path
is only reachable when a future DTO addition lands), `IsPlatformAdminHandler`,
`tamma_auth_*` metrics, and the `DELETE /api/admin/users/{userId}/sessions`
endpoint stay on the parent agent's plan for the rest of Story 28-9.

## Closed by 2026-05-30 follow-up

Two of the AC3 residuals listed in the Status line above closed on
`feat/wave-b`:

### `AUTH.REFRESH_REUSE_DETECTED` platform_events emission — SHIPPED

`AuthEndpoints.Refresh` now emits an `AUTH.REFRESH_REUSE_DETECTED` row to
`platform_events` whenever the reuse-detection branch fires (revoked
token replayed). Tags carry `userId`, `tenantId` (when bound),
`jtiChainHead` (when known), `actorIp` (resolved through
`TrustedProxyResolver`), and `source=auth`. Data carries
`revokedTokenCount` so the dashboard can show "this incident burned N
sessions". Best-effort: a publisher failure logs `LogWarning` and does
NOT mask the 401 because the security action (lineage burn) already
happened. The legacy fallback path (pre-Story-28-9 row with NULL
`JtiChainHead` / NULL `TenantId`) emits the event without the
`jtiChainHead` / `tenantId` tags so SIEM can distinguish the two paths.

Code: `AuthEndpoints.cs` — new private helper
`BuildRefreshReuseDetectedEvent`; the reuse-detection branch in `Refresh`
calls the publisher inside a try/catch.

Tests: `RefreshTokenReuseDetectionTests.cs` — two new tests
(`Refresh_ReuseDetection_EmitsAuthRefreshReuseDetectedEvent`,
`Refresh_ReuseDetection_LegacyNullChainHead_EmitsEventWithoutChainHeadTag`).

### `tenant_mismatch_on_refresh` 400 — INTENTIONALLY NOT IMPLEMENTED

Decision: do NOT add a target-tenant field to `RefreshRequest`.

**Rationale:** the AC3 text describes a 400 error code emitted when a
client asserts a different target tenant on a refresh call. The current
DTO (`{ refreshToken }`) provides no way for a client to assert a target
— refresh's contract is "give me a new pair for the tenant I'm on", and
the token IS the binding (token.TenantId is the source of truth, set
when the row was created at login / switch-org). Adding a target field
would change the contract for zero functional gain: the cross-tenant
flow already exists as `POST /api/v1/auth/switch-org` with explicit
membership validation, audit emission, and chain-head reset. Refresh
deliberately stays single-tenant.

The DB-side enforcement of the same invariant IS in place: if the
refresh row is bound to tenant A but the user's membership in A was
revoked between refreshes, the handler returns 401 with `action: POST
/api/v1/auth/switch-org` (the membership-lost branch in `Refresh`). That
is the actual user-visible expression of "this refresh token can't mint
an access token for somewhere else".

If a future client capability requires cross-tenant assertion on
refresh, re-open this residual: add the field, the 400 path, the test.

## Risks / Open Questions

- **Concurrent switch-org serialisation uses `SELECT ... FOR
  UPDATE` on the user's current refresh-tokens row.** If a user
  somehow has zero current refresh tokens (rootless token only),
  the lock target must be the `users` row instead. Add a fall-back
  branch in `SwitchOrgHandler` that locks `users` when no current
  refresh token is present — covered by an AC2 unit test.
- **`JtiChainHead` semantics under logout-all.** When `logout?all=
  true` revokes tokens across tenants, the `JtiChainHead` values
  are still valid pointers. A subsequent login starts a new chain
  head — no stitching needed. Document this in a comment block on
  the `RefreshToken` entity.
- **Clock skew between CP and API pods.** The 15-minute access-token
  window + 1-minute admin-route cache means up to 16 minutes of
  drift. If deployment adds read-replicas on a different cluster
  with unsynced NTP, the `exp` check could fail spuriously. The
  existing deployment uses a single NTP source; no mitigation in
  this story, flagged for the deploy runbook.
