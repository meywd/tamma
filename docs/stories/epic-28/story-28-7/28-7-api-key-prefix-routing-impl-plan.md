# Story 28-7 Implementation Plan — API-Key Prefix Routing

**Status**: Planned (2026-04-20)
**Story brief**: [`28-7-api-key-prefix-routing.md`](./28-7-api-key-prefix-routing.md)
**Epic 28 phase**: C (Auth — serial; 28-7 → 28-8 → 28-9)
**Branch**: `feat/story-28-7-api-key-prefix-routing`

---

## 1. Objective

Re-architect `ApiKeyAuthHandler` to route inbound `Bearer tk_...` keys
to the correct DbContext based on prefix: `tk_t_` tenant-scoped (CP
index + tenant DB lookup), `tk_pl_` platform-admin (CP only), `tk_u_`
user-scoped (CP). Adds the CP-side `platform_api_key_index` routing
table, an Argon2id `IApiKeyHasher` abstraction, reveal-once key
generation, soft-revoke by `RevokedAt`, and a legacy fallback gated by
a feature flag with deprecation metrics.

## 2. Dependencies

Hard blockers:

- **Story 28-1** — `api_keys.KeyPrefix` b-tree, `RevokedAt`,
  `RateLimitRpm` columns.
- **Story 28-4** — `ITenantConnectionResolver` for `tk_t_` routing.
- **Story 28-6** — `platform_events` for audit emission.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Auth/IApiKeyHasher.cs` | Contract. |
| `.../Auth/Argon2ApiKeyHasher.cs` | `Konscious.Security.Cryptography` impl (params match `PasswordService`). |
| `.../Auth/ApiKeyPrefixGenerator.cs` | Compile-time constants + `Generate(scope) → "tk_X_<b32>"`. |
| `.../Auth/ApiKeyPrefixParser.cs` | `Parse(token) → (prefix, suffix)`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Entities/PlatformApiKeyIndex.cs` | CP entity (KeyPrefix PK, TenantId, CreatedAt, RevokedAt). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/28_7_platform_api_key_index.cs` | Migration. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Dtos/ApiKeys/CreateApiKeyResponse.cs` | Plaintext + banner. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Auth/ApiKeyAuthHandlerTests.cs` | Unit: all AC paths. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.IntegrationTests/ApiKeys/ApiKeyLifecycleTests.cs` | T1-T10 per brief. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Auth/ApiKeyAuthHandler.cs` | Add `ParsePrefix`, `ResolveTenantScoped`, `ResolveCpScoped`, `FallbackLegacy`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/ApiKeysEndpoints.cs` | Three POST endpoints (tenant/platform/user); DELETE revoke; GET list. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Add `DbSet<PlatformApiKeyIndex>`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/appsettings.json` | `Tamma:Auth:AllowLegacyUnprefixedKeys=true`. |

## 5. Sequence of changes

### Step 1 — Prefix generator + parser (2h)

- 32-byte `RandomNumberGenerator` → base32 (no padding) → prepend prefix.
- Unit: round-trip, entropy > 5 bits/char, invalid parses return null.
- **Commit**: `feat(auth): api-key prefix generator + parser`.

### Step 2 — Argon2id hasher (2h)

- `Argon2ApiKeyHasher.Hash(plaintext, salt)` + `Verify(plaintext, hash)`
  using `Konscious.Security.Cryptography` (already pinned).
- `CryptographicOperations.FixedTimeEquals` for comparison.
- Unit: timing-consistency test (σ of match vs. mismatch duration
  within 3× jitter).
- **Commit**: `feat(auth): argon2 api-key hasher`.

### Step 3 — `platform_api_key_index` schema (2h)

- Migration + entity + repo (`LookupTenantAsync`, `InsertAsync`, `RevokeAsync`).
- **Commit**: `feat(db): platform_api_key_index`.

### Step 4 — Handler routing (6h)

- `ApiKeyAuthHandler.AuthenticateAsync`:
  1. Read `Authorization: Bearer <token>`.
  2. `ApiKeyPrefixParser.Parse` → `(prefix, suffix)`.
  3. If prefix matches `tk_t_` → `ResolveTenantScoped`:
     - CP query `platform_api_key_index` by `KeyPrefix`.
     - Resolve tenant DataSource via `ITenantConnectionResolver`.
     - Query tenant `api_keys` by `KeyPrefix`; verify hash.
  4. If `tk_pl_` or `tk_u_` → `ResolveCpScoped`:
     - CP `api_keys` by `KeyPrefix`; scope-gate; verify hash.
  5. Else → `FallbackLegacy` (gated by config flag).
- Populate `HttpContext.Items["TenantId"]` for `tk_t_`.
- Unit tests for every branch.
- **Commit**: `feat(auth): prefix-based api-key routing`.

### Step 5 — Endpoints (4h)

- `POST /tenants/{tid}/api-keys` — two-phase create: CP index
  first, then tenant row; rollback CP on tenant failure.
- `POST /platform/api-keys` — platform-admin only.
- `POST /users/me/api-keys` — rootless JWT.
- `DELETE` endpoints with two-phase revoke (CP first).
- `GET /api-keys` returns metadata only (no plaintext, no hash).
- **Commit**: `feat(auth): api-key create/revoke/list endpoints`.

### Step 6 — Rate limiting + last-used (3h)

- Per-key `IRateLimiter` bucket keyed `api_key:<keyId>`.
- `LastUsedAt` update throttled to 60s per key (in-memory coalescing).
- Prometheus metrics per brief AC7.
- **Commit**: `feat(auth): api-key rate limiting + last-used tracking`.

### Step 7 — Legacy fallback + deprecation metrics (2h)

- `FallbackLegacy` path emits
  `log.Warn("api_key.legacy_unprefixed_auth", ...)`.
- Prometheus gauge for deprecation tracking.
- Config flag defaults `true`.
- **Commit**: `feat(auth): legacy-key fallback + deprecation metrics`.

### Step 8 — Integration tests (3h)

- T1–T10 per brief.
- **Commit**: `test(auth): api-key prefix routing T1-T10`.

## 6. Test strategy

### Unit

- `ApiKeyPrefixGeneratorTests` — round-trip, entropy, format.
- `Argon2ApiKeyHasherTests` — hash/verify, constant-time.
- `ApiKeyAuthHandlerTests` — every branch including 429 rate-limit.

### Integration

- T1-T10 as listed in brief.

### Manual

- Dev dashboard: issue a `tk_t_`, use with curl on a tenant endpoint,
  confirm `TenantContext.TenantId` matches.

## 7. Rollback plan

- **Legacy fallback flag**: lets ops flip back to pre-epic behaviour
  by setting `AllowLegacyUnprefixedKeys=true` (default) while new
  prefixed keys coexist.
- **Schema rollback**: drop `platform_api_key_index`; legacy path
  continues.
- **Non-reversible**: new `tk_*` keys cannot be decoded without the
  index table; keep old legacy keys reachable during soak.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Generator/parser | 2 |
| 2. Argon2 hasher | 2 |
| 3. Index schema | 2 |
| 4. Handler routing | 6 |
| 5. Endpoints | 4 |
| 6. Rate limit + last-used | 3 |
| 7. Legacy fallback | 2 |
| 8. Integration tests | 3 |
| **Total** | **24** (brief 14h; +10h because handler rewrite is
deeper than brief counted once metrics + rate-limiter + two-phase
txn are added). |

## 9. Open questions

- **Two-phase CP+tenant rollback semantics**: if CP index row
  commits but tenant write fails, we `DELETE` the CP row. What if
  the CP delete also fails? Plan: background reconciliation job
  scans for orphan CP rows older than 5 minutes with no matching
  tenant row, deletes them. Not in this story's scope; tracked as
  28-7 follow-up.
- **`KeyPrefix` collision at 10 chars**: birthday at 5 bits/char × 10
  chars = 50 bits. Birthday-collision at 2^25 ≈ 33M keys — safe
  until tenant count × keys-per-tenant exceeds. Document in runbook.
- **`tk_x_` / `tk_s_` reserved**: per brief. Confirm at implementation
  that future import tools honour the reservation.
- **Prometheus cardinality**: `tamma_api_key_verify_total{prefix,outcome}`
  has 3 prefixes × 4 outcomes = 12 time series. Safe.
- **Legacy-key cutover milestone**: tracked separately. This story
  ships the instrumentation only.
