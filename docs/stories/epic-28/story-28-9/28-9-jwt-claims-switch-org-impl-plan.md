# Story 28-9 Implementation Plan — JWT Claims + `/auth/switch-org`

**Status**: Planned (2026-04-20)
**Story brief**: [`28-9-jwt-claims-switch-org.md`](./28-9-jwt-claims-switch-org.md)
**Epic 28 phase**: C (Auth — final; after 28-8)
**Branch**: `feat/story-28-9-jwt-switch-org`

---

## 1. Objective

Rework JWT access tokens to carry per-tenant scope (`tenantId`,
`tenantSlug`, `role`) plus orthogonal `isPlatformAdmin`. Add
`POST /auth/switch-org` that atomically revokes the old refresh
token and issues a new token pair scoped to the target tenant,
so users in multiple orgs experience "one login, many workspaces"
without re-entering credentials or leaking roles between tenants.
Ships a `tokens_revoked` table + rotation logic, platform-admin
policy enforcement, and a 4-hour soak + fuzz harness.

## 2. Dependencies

Hard blockers:

- **Story 28-7** — API-key handler.
- **Story 28-8** — middleware honours the switch-org allowlist entry.
- **Story 28-5** — tenants can be in provisioning/failed/deleted
  states that switch-org must reject.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Auth/JwtClaimSet.cs` | Constants + factory `BuildForUserTenant(user, membership, isPlatformAdmin)`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthSwitchOrgEndpoint.cs` | `POST /api/v1/auth/switch-org`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Auth/PlatformAdminPolicy.cs` | `[Authorize(Policy = "PlatformAdmin")]` handler. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Entities/TokenRevocation.cs` | CP entity (already in 28-1 schema; map here). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Auth/RefreshTokenRotator.cs` | Atomic revoke-old + issue-new. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Auth/SwitchOrgTests.cs` | Unit + fuzz. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.IntegrationTests/Auth/SwitchOrgSoakTests.cs` | 4-hour soak (run as nightly). |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Auth/JwtTokenService.cs` | Issue tokens with new claim set; remove `users.Role` source. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Auth/PermissionHandler.cs` | Read `ClaimTypes.Role` mapped from `role` claim only. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` | Login + refresh emit new claim set. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Tenancy/TenantFreePaths.cs` | Add `/api/v1/auth/switch-org`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Register `PlatformAdminPolicy`. |
| `/home/meywd/tamma/docs/runbooks/auth-token-rotation.md` | Rotation on deploy, secret invalidation procedure. |

## 5. Sequence of changes

### Step 1 — Claim set + factory (2h)

- `JwtClaimSet` constants.
- `BuildForUserTenant(user, membership, isPlatformAdmin)` returns
  `ClaimsPrincipal`.
- Unit tests: claim presence/absence matrix.
- **Commit**: `feat(auth): JWT claim set + factory`.

### Step 2 — Token service rework (3h)

- `JwtTokenService.Issue(user, tenantId?)` computes claims.
- `exp=15min`, `jti=UUIDv7`.
- Refresh token has the same `jti` chain so revocation cascades.
- **Commit**: `feat(auth): JWT token service (per-tenant claims)`.

### Step 3 — PlatformAdminPolicy (2h)

- Authorization policy reads `isPlatformAdmin` claim.
- Apply to all `/admin/*` endpoints.
- Unit + integration tests.
- **Commit**: `feat(auth): PlatformAdmin policy`.

### Step 4 — RefreshTokenRotator (3h)

- `RotateAsync(oldRtId, newTenantId, userId)`:
  - CP transaction:
    1. `UPDATE refresh_tokens SET RevokedAt=NOW(), RevokedReason='switch_org' WHERE Id=@oldRtId`.
    2. `INSERT INTO refresh_tokens (...)` with new `jti`.
    3. Emit `AUTH.SWITCH_ORG.SUCCESS` to `platform_events`.
  - Commit → publish RabbitMQ event for real-time observers.
- Idempotent via `oldRtId`; double-switch returns same new token pair.
- **Commit**: `feat(auth): refresh token rotator`.

### Step 5 — SwitchOrg endpoint (3h)

- `POST /api/v1/auth/switch-org { tenantId }`:
  1. Validate access token.
  2. Validate target tenant state (403/404/410/424/503 per 28-8).
  3. Validate membership.
  4. Call `RefreshTokenRotator.RotateAsync`.
  5. Set new session cookie + return new access token.
- On `TenantFreePaths` so middleware doesn't gate.
- **Commit**: `feat(auth): /auth/switch-org endpoint`.

### Step 6 — Login + refresh updated (2h)

- Login emits new claim set.
- `/auth/refresh` validates + rotates + re-emits with same tenantId.
- **Commit**: `feat(auth): login/refresh emit new claim set`.

### Step 7 — Soak + fuzz (4h)

- Soak: 4-hour loop, 1000 sessions, random switch-orgs; assert no
  token leaks (verified via event stream inspection).
- Fuzz: property-based test asserts that a demoted role in tenant A
  never authorises admin actions in tenant B after switch.
- **Commit**: `test(auth): switch-org soak + fuzz`.

### Step 8 — Runbook + drop old column (2h)

- Document token-rotation runbook.
- Follow-up commit (separate PR) drops `users.Role`.
- **Commit**: `docs(auth): token rotation runbook`.

## 6. Test strategy

### Unit

- Claim factory (8 cases) × `RefreshTokenRotator` (10 cases).

### Integration

- Full register → login → switch-org → tenant read → second switch
  → first tenant read denied (refresh chain revoked).

### Security

- Fuzz: 10k random (user, source tenant, target tenant) triples;
  assert RBAC invariants.

## 7. Rollback plan

- **Flag**: `Auth:MultiTenantSessions=true`. Off disables switch-org
  and reverts to single-tenant login only.
- **Token invalidation**: rotate JWT signing secret on deploy =
  every old token invalid. Document in runbook.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Claim set | 2 |
| 2. Token service | 3 |
| 3. PlatformAdminPolicy | 2 |
| 4. Rotator | 3 |
| 5. SwitchOrg endpoint | 3 |
| 6. Login/refresh update | 2 |
| 7. Soak + fuzz | 4 |
| 8. Runbook | 2 |
| **Total** | **21** (brief 24; plan slightly under because
`TokenRevocation` entity was already scaffolded in 28-1). |

## 9. Open questions

- **JWT signing secret rotation**: blue/green deploy or in-place with
  24h grace? Runbook specifies blue/green.
- **Refresh token longevity**: currently 30 days. With switch-org
  rotation, active users never hit expiry. Idle users expire per
  default. Confirm with Security.
- **Race on double switch-org**: two simultaneous requests to switch
  between same tenants. CP transaction's `RevokedAt` UNIQUE-like
  conflict is handled via "already revoked" short-circuit.
- **Platform-admin impersonation with switch-org**: admin switches
  to tenant X, impersonates user Y inside tenant X. Flag:
  `X-Impersonate-User` honoured by 28-8 middleware; audit emits.
- **Claim max size**: `tenantSlug` could be long. Cap at 64 chars
  via slug validation. Documented in 18-3 impl plan.
