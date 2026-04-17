# Story 28.7: API-Key Prefix Routing (`tk_t_` / `tk_pl_` / `tk_u_`)

**Epic**: Epic 28 - Database-per-Tenant Isolation
**Category**: Auth
**Status**: Draft
**Priority**: High (API-key auth is broken under DB-per-tenant until a
prefix scheme routes the key to the correct DbContext; every bot /
CI / SDK integration depends on it)
**Estimated Effort**: M (14h)

## User Story

As a **platform engineer**, I want **`ApiKeyAuthHandler` to route an
inbound `Bearer tk_...` key to the right data source based on its
prefix (`tk_t_` tenant-scoped, `tk_pl_` platform-admin,
`tk_u_` user-scoped) and verify it via the `api_keys.KeyPrefix`
lookup index from Story 28-1**, so that **API-key authentication works
across CP and tenant DBs without a central synchronous key-hash index,
platform admins have a distinct key class, and legacy un-prefixed keys
continue to authenticate during the migration window**.

## Acceptance Criteria

### AC1: Three on-wire prefixes with distinct scopes

- [ ] `tk_t_<random>` — **tenant-scoped**. Stored in
      `api_keys` in the **tenant DB** with `Scope='tenant'`,
      `TenantId=<tid>`, `OwnerId=<userId-in-CP>`. Authenticates
      as the owning user inside the tenant. `TenantContext.TenantId`
      is populated from the `api_keys.TenantId` column on the CP-side
      index row (see AC2).
- [ ] `tk_pl_<random>` — **platform-admin-scoped**. Stored in
      `api_keys` in the **control plane** with `Scope='platform'`,
      `TenantId=NULL`. Authenticates as a platform admin
      (`IsPlatformAdmin=true` on the owning user). `TenantContext` is
      left unresolved — subsequent middleware only allows this class
      of key on `/api/admin/*` routes or explicitly marked
      rootless endpoints.
- [ ] `tk_u_<random>` — **user-scoped**. Stored in `api_keys` in the
      **control plane** with `Scope='user'`, `TenantId=NULL`.
      Authenticates as the owning user. On tenant-scoped routes the
      caller must also supply `X-Tenant-Id: <tid>`; the handler checks
      the user has an active membership for that tenant, then resolves
      the data source via `ITenantConnectionResolver` (Story 28-4).
- [ ] The `<random>` suffix is 32 bytes of `RandomNumberGenerator`
      output, base32-encoded (no padding), giving a 52-character
      random body. Total on-wire key length: prefix (5–6 chars) + 52 =
      57–58 chars. Well under the 8 KiB header limit.
- [ ] Per Epic 28 README conflict note — this story chose **the
      task-scoped "three-prefix + CP index" variant** over Doc 01 §3.1
      option 1 (tenant-id-encoded `tk_t_<tenant_b32>_...`). Rationale
      resolved in Technical Context below.

### AC2: `api_keys.KeyPrefix` lookup drives routing

- [ ] `ApiKeyAuthHandler` splits the incoming token on the second
      underscore, yielding `(prefix, suffix)` where `prefix ∈
      {tk_t, tk_pl, tk_u}`.
- [ ] For `tk_pl_` and `tk_u_`: query `control_plane.api_keys` by
      `KeyPrefix = <first 10 chars>` (b-tree index shipped in Story
      28-1, see `28-1-ef-migration-scripts.md` AC3), filter rows by
      `Scope`, and verify the Argon2id hash against the suffix.
- [ ] For `tk_t_`: query a new CP-side **routing index**
      `platform_api_key_index` created by this story —
      `(KeyPrefix TEXT PRIMARY KEY, TenantId UUID NOT NULL,
      CreatedAt TIMESTAMPTZ NOT NULL, RevokedAt TIMESTAMPTZ NULL)`.
      The row is written by the key-creation endpoint at the same
      time as the `api_keys` row in the tenant DB (two-phase: CP
      index first, then tenant row; CP row is deleted on tenant-row
      failure). The handler looks up the `TenantId`, resolves the
      data source via `ITenantConnectionResolver.GetAsync(tenantId)`,
      then queries `api_keys.KeyPrefix` inside the tenant DB and
      verifies the Argon2id hash.
- [ ] The two CP-side tables (`api_keys`, `platform_api_key_index`)
      both carry `KeyPrefix UNIQUE` so a collision is structurally
      impossible (Birthday bound at 32-byte random body > 10⁻²⁰ at
      10M keys).

### AC3: Argon2id verification with constant-time compare

- [ ] Hash algorithm: Argon2id with parameters matching the existing
      `PasswordService` (time=3, memory=64MiB, parallelism=4, output
      32 bytes). Same parameters keep the dependency surface flat.
- [ ] Verification uses `Konscious.Security.Cryptography` (already in
      `Tamma.Api.csproj` per `PasswordService.cs`) via a new
      `IApiKeyHasher` abstraction at
      `apps/tamma-elsa/src/Tamma.Api/Auth/IApiKeyHasher.cs`.
- [ ] Comparison uses `CryptographicOperations.FixedTimeEquals` on
      the 32-byte hash output. No early-exit on mismatched-length.
- [ ] Failed verification returns 401 with `WWW-Authenticate: Bearer
      error="invalid_token"` and **no indication** of why the key
      failed (prefix unknown, hash mismatch, revoked, expired all
      return the same shape) — prevents oracle-based key harvesting.

### AC4: Key generation returns plaintext exactly once

- [ ] `POST /api/v1/tenants/{tid}/api-keys` (tenant-scoped) generates
      a `tk_t_<random>` key. Writes the `platform_api_key_index` CP
      row inside a CP transaction, then writes the tenant `api_keys`
      row inside a tenant transaction. On tenant-transaction failure,
      the CP transaction is rolled back (the ordering guarantees no
      orphan CP index row).
- [ ] `POST /api/v1/platform/api-keys` (platform-admin-only) generates
      a `tk_pl_<random>` key in CP `api_keys`.
- [ ] `POST /api/v1/users/me/api-keys` (rootless JWT OK, must be a
      real user) generates a `tk_u_<random>` key in CP `api_keys`.
- [ ] Response body includes `key: "tk_t_abc…"` (plaintext) **and** a
      banner field `warning: "Store this key securely. You will not
      be able to retrieve it again."` — the only endpoint that ever
      returns the plaintext.
- [ ] Subsequent `GET /api/v1/…/api-keys` responses return
      `KeyPrefix`, `CreatedAt`, `LastUsedAt`, `ExpiresAt`,
      `RevokedAt`, `Name` only — **no hash, no plaintext**.
- [ ] Plaintext is zeroed in memory immediately after the response
      serialises (`CryptographicOperations.ZeroMemory` on the byte
      buffer); logs never see it per Epic 28 README conflict
      resolution #4's PII-absence requirement.

### AC5: Legacy un-prefixed keys fall through with deprecation log

- [ ] If the incoming `Bearer` token does NOT start with `tk_t_`,
      `tk_pl_`, or `tk_u_`, the handler falls through to the
      **pre-Epic-28 lookup path** — query CP `api_keys` by hash,
      scope-gate as today. This keeps pre-existing keys working
      during the migration window.
- [ ] Each legacy-path verification emits a structured WARN:
      `log.Warn("api_key.legacy_unprefixed_auth",
      keyId=<guid>, userId=<guid>, scope=<scope>)` with the key id
      (not the key) for ops tracking. The `KeyPrefix` column on
      legacy rows is backfilled by Story 28-1's data migration (the
      first 10 chars of the hex-encoded hash serve as the prefix for
      index-lookup only — the next time the legacy key is rotated,
      the caller receives a new prefixed key).
- [ ] A configuration flag `Tamma:Auth:AllowLegacyUnprefixedKeys`
      (default `true`) gates the fallback. After the migration
      window (tracked separately), flipping it to `false` returns 401
      on legacy keys and surfaces deprecation guidance in the error
      body.
- [ ] A Prometheus gauge `tamma_api_key_legacy_unprefixed_total`
      tracks the count of legacy-path verifications per minute; ops
      cut over when it hits zero for 7 consecutive days.

### AC6: Revocation via soft-revoke column

- [ ] `api_keys.RevokedAt TIMESTAMPTZ NULL` (shipped by Story 28-1)
      is checked as part of the verification predicate: if non-null
      and older than the request timestamp, the handler returns 401
      as in AC3.
- [ ] `DELETE /api/v1/tenants/{tid}/api-keys/{keyId}` sets
      `RevokedAt=NOW()` in both the tenant `api_keys` row AND the CP
      `platform_api_key_index` row (same transaction-pair pattern
      from AC4, reverse order: CP index updated first so new
      verifications fail fast). Revocation is idempotent — repeated
      DELETE on an already-revoked row returns 204.
- [ ] `DELETE /api/v1/platform/api-keys/{keyId}` and
      `DELETE /api/v1/users/me/api-keys/{keyId}` revoke CP keys.
- [ ] Emit `API_KEY.REVOKED.SUCCESS` to `platform_events` (CP) with
      `{ tags: { userId, scope, tenantId? }, data: { keyPrefix,
      reason } }`. Tenant-scoped revocations also emit a mirror
      event to the tenant's `domain_events` for tenant-visible
      audit.

### AC7: Per-key rate limiting + last-used tracking

- [ ] Every successful verification updates `api_keys.LastUsedAt`
      with a rate-limited async write (one write per key per 60s,
      coalesced in memory to avoid hot-row contention). A hit on the
      same key within 60s skips the update.
- [ ] Rate limiting uses the existing `IRateLimiter` abstraction;
      bucket is `api_key:<keyId>` with default `1000 req/min` for
      `tk_u_` and `tk_pl_`, `10000 req/min` for `tk_t_` (tenant
      keys are typically CI/SDK and need headroom). Per-bucket
      overrides live on the `api_keys.RateLimitRpm` column (Story
      28-1). Exceeded limits return 429 with
      `Retry-After: <seconds-until-bucket-resets>`.
- [ ] `tamma_api_key_verify_duration_ms` histogram with prefix label;
      `tamma_api_key_verify_total{prefix,outcome}` counter; Grafana
      alert on p95 > 50ms (Argon2id is ~20ms budget).

## Technical Context

- **Design docs**:
  - `plans/db-per-tenant/01-control-plane-split.md` §2.6 (API keys
    after the split: platform / user / tenant scoping rationale) and
    §3.1 ("Which DB holds the key?" — options 1 & 2 compared). This
    story adopts a **hybrid**: option-1 prefixes for tier routing,
    option-2 CP index for tenant-scoped lookup without embedding the
    tenant id in the on-wire key. Rationale: shorter on-wire keys
    (58 chars vs 87 with base32 UUID), a revoked key's CP index row
    is hidden from tenant snooping, and `ITenantConnectionResolver`
    already performs the CP query this lookup needs.
  - `00-sequencing.md` §"Phase 3 Stream B" — this story sits in the
    auth-plane stream alongside 28-8.
- **File layout**:
  - `apps/tamma-elsa/src/Tamma.Api/Auth/ApiKeyAuthHandler.cs` —
    modified; new `ParsePrefix()`, `ResolveTenantScoped()`,
    `ResolveCpScoped()`, `FallbackLegacy()` private methods.
  - `apps/tamma-elsa/src/Tamma.Api/Auth/IApiKeyHasher.cs` — new.
  - `apps/tamma-elsa/src/Tamma.Api/Auth/Argon2ApiKeyHasher.cs` — new.
  - `apps/tamma-elsa/src/Tamma.Api/Auth/ApiKeyPrefixGenerator.cs` —
    new, centralises prefix-to-scope map and the `tk_<scope>_`
    literals as compile-time constants.
  - `apps/tamma-elsa/src/Tamma.Data/Entities/PlatformApiKeyIndex.cs`
    — new CP entity (see AC2).
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/04x_platform_api_key_index.cs`
    — new migration, coordinate number with 28-10 per sequencing
    doc's "Safe with caveats" note.
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/ApiKeysEndpoints.cs` —
    modified; three `POST /api-keys` endpoints (tenant / platform /
    user) and matching `DELETE`/`GET` handlers.
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/ApiKeys/CreateApiKeyResponse.cs`
    — new, holds the plaintext-with-banner response shape.
- **CP index vs tenant-encoded prefix — recorded trade-off**:
  - CP index (this story): one additional CP round-trip per
    `tk_t_` verification, negated on the hot path by the resolver's
    existing CP query for the same tenant id.
  - Tenant-encoded prefix (Doc 01 §3.1 option 1): no CP round trip
    but on-wire key reveals the tenant id. This epic prioritises
    tenant-id opacity on leaked tokens and accepts the CP round
    trip.
- **Interaction with 28-8**: `TenantContextMiddleware` runs AFTER
  `ApiKeyAuthHandler` and reads `TenantContext.TenantId` populated
  here. For `tk_pl_` keys on admin routes, the middleware's
  rootless-JWT branch applies unchanged.

## Dependencies

- **Blocks**: none in Epic 28 — this story is a leaf in Stream B.
- **Blocked by**: 28-1 (ships `api_keys.KeyPrefix` b-tree index +
  `RevokedAt` column + `RateLimitRpm` column in both CP and tenant
  DBs), 28-6 (`platform_events` table for audit emission), 28-4
  (`ITenantConnectionResolver` for `tk_t_` routing).
- **External**: `Konscious.Security.Cryptography` (already pinned),
  the existing `IRateLimiter` service.

## Test Plan

### Unit tests

- `ApiKeyPrefixGeneratorTests`: round-trip generation → parse; assert
  every output starts with the correct prefix, has the right entropy
  (Shannon > 5 bits/char), and is base32-decodable.
- `Argon2ApiKeyHasherTests`: table-driven hash-then-verify; verify
  against the wrong suffix returns false; constant-time compare
  verified by a timing-harness test that asserts the σ of verify
  durations for matching vs mismatching-at-position-0 suffixes is
  within 3 × jitter.
- `ApiKeyAuthHandlerTests` (with mocked `ITenantConnectionResolver`
  + in-memory DbContexts):
  - Happy path per scope × 3.
  - Unknown prefix → 401.
  - Wrong hash → 401 with no oracle signal.
  - Revoked key → 401.
  - Expired key → 401.
  - Missing `X-Tenant-Id` header on `tk_u_` call to tenant-scoped
    route → 400.
  - `tk_u_` with membership-for-wrong-tenant → 403.
  - Rate-limit exceeded → 429 with correct `Retry-After`.
  - Legacy un-prefixed key → falls through, succeeds, emits WARN.

### Integration tests (Testcontainers.PostgreSQL)

- **T1 Tenant key create + authenticate**: `POST /tenants/{tid}
  /api-keys` → receive plaintext → next request with the key →
  `TenantContext.TenantId` matches.
- **T2 Platform key create + authenticate on admin route**: same,
  asserts `/api/admin/tenants` responds 200.
- **T3 Platform key rejected on tenant-scoped route**: same key hits
  `/api/v1/issues` → 403.
- **T4 User key with X-Tenant-Id**: resolves correct tenant data
  source, tenant-scoped read returns the user's rows.
- **T5 Two-phase create rollback**: inject a failure on the tenant
  transaction after CP index row commits → assert the CP index row
  is rolled back (no orphan).
- **T6 Two-phase revoke ordering**: revoke a `tk_t_` key; assert
  the CP index row's `RevokedAt` is set first and the tenant row's
  `RevokedAt` is set second within the same request; a concurrent
  verify mid-revoke returns 401 deterministically.
- **T7 Legacy key migration compatibility**: seed a pre-epic key
  (no `tk_` prefix, just a hex hash), assert it still authenticates
  and emits the WARN.
- **T8 Rate limit**: 10001 requests in 60s against a `tk_t_` key with
  default limit → 10000 succeed, 1 returns 429.
- **T9 Event emission**: `tamma_audit_query`
  `API_KEY.REVOKED.SUCCESS` row exists in `platform_events` after
  a revoke, with prefix in `data.keyPrefix` and no full key material.
- **T10 PII sweep**: grep the full `platform_events` + `domain_events`
  for any substring starting with `tk_` except `KeyPrefix` fields —
  asserts the plaintext is never persisted.

### Manual verification

- Local dev: issue a `tk_t_` key via `pnpm dev` + dashboard UI;
  confirm the plaintext is shown once, confirm subsequent views mask
  it to `tk_t_abc…`.
- Use the key with `curl` against a tenant-scoped endpoint; confirm
  200 and `TenantContext.TenantId` matches in the request log.
- Use a `tk_pl_` key against `/api/admin/tenants` → 200. Same key
  against `/api/v1/issues` → 403.

## Definition of Done

- [ ] AC all green
- [ ] Unit + integration tests added, suite passes
- [ ] No new CodeQL alerts (especially the "hardcoded credential" rule
      family — Argon2id params are constants, not secrets)
- [ ] Design-doc references updated if the impl deviated (expected:
      update `01-control-plane-split.md` §3.1 to record the hybrid
      approach resolution)
- [ ] Reviewed by a second engineer (cross-stream)

## Risks / Open Questions

- **Hybrid routing adds one CP round-trip for `tk_t_` keys.** The
  `platform_api_key_index` row is keyed by `KeyPrefix` (10 chars,
  unique), so the query is an index probe — budget 2ms. On the
  hot path, the existing resolver's tenant-row lookup lands in the
  same CP connection and can be batched via a JOIN if the 50ms
  SLO trips.
- **Legacy-key cutover is not time-bound in this story.** The flag
  `AllowLegacyUnprefixedKeys` stays `true` until ops flips it. A
  follow-up ticket tracks the cutover milestone; this story only
  ensures the instrumentation is in place.
- **Prefix space collisions during import from other tools.** If a
  future feature imports tokens from external systems that also use
  the `tk_` prefix, the three-scope letter map must grow. Reserve
  `tk_x_` (future extension) and `tk_s_` (system/service) in the
  `ApiKeyPrefixGenerator` constants so imports don't collide.
