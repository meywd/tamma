# Story 28.4: Tenant Connection Resolver + LRU Pool Cache

**Epic**: Epic 28 - Database-per-Tenant Isolation
**Category**: Foundation
**Status**: Draft
**Priority**: High (every tenant-scoped request ultimately pays through
this resolver; without it, production is stuck on the stub from Story
28-3)
**Estimated Effort**: L (20-40h) — target 22h

## User Story

As a **platform engineer**, I want **a production-grade
`TenantConnectionResolver` that caches warm `NpgsqlDataSource` instances
per tenant with LRU eviction and bounded memory**, so that **tenant-
scoped requests pay a cold-start cost only on first use and the API pod
never exhausts Postgres `max_connections` under load**.

## Acceptance Criteria

### AC1: Real `TenantConnectionResolver` replaces Story 28-3 stub

- [ ] `TenantConnectionResolver : ITenantConnectionResolver` under
      `apps/tamma-elsa/src/Tamma.Api/Services/TenantConnectionResolver/`
      implements all four interface methods from Story 28-3:
      `DataSourceFor`, `ElsaDataSourceFor`, `EvictAsync`, `GetStats`.
- [ ] `StubTenantConnectionResolver` (DEBUG-only) is removed from DI
      registration; the real resolver is registered for both DEBUG and
      Release builds.
- [ ] Resolver reads the encrypted connection string from `tenants`
      (via `ControlPlaneDbContext`), decrypts via
      `ISecretsService.DecryptConnectionString(...)` (see AC6), and
      builds an `NpgsqlDataSource` with the per-tenant settings from
      AC3.

### AC2: `ConcurrentDictionary<Guid, Lazy<NpgsqlDataSource>>` + LRU

- [ ] In-process cache backed by
      `ConcurrentDictionary<Guid, Lazy<NpgsqlDataSource>>` (hot path is
      lock-free per Doc 04 §2.2).
- [ ] LRU eviction via `IMemoryCache` with a size budget of
      `MaxTenants` (default 1024, cap from Epic 28 success metric #5).
- [ ] `MemoryCache.PostEvictionCallback` is the **single** site where
      `NpgsqlDataSource.DisposeAsync()` runs — removing from `_pools`
      without going through the callback is forbidden (avoid socket
      leaks per Doc 04 §2.3).
- [ ] Per-tenant `SemaphoreSlim` prevents thundering-herd rebuilds on
      cache miss (per Doc 04 §2.2 "double-check pattern with Lazy<>").

### AC3: Per-tenant pool configuration from options

- [ ] `TenantConnectionOptions` bound to `TenantConnection` config
      section with these defaults (per Doc 04 §2.4–2.5):
  - `MaxTenants = 1024`
  - `MaxPooledConnectionsPerTenant = 10` (Doc 04 recommends 5, Epic
    28 success metric #5 allows up to 10 for burst headroom)
  - `MinPoolSize = 0`
  - `IdleLifetime = 00:05:00` (5 minutes sliding)
  - `ConnectionLifetime = TimeSpan.Zero` (no forced rotation)
  - `ConnectTimeout = 00:00:05`
  - `CommandTimeout = 00:00:30`
  - `ApplicationName = tamma-api;tenant=<guid>` (Doc 04 §2.4, needed
    for `pg_stat_activity` forensics and deletion step D).
  - `KeepAlive = 00:00:30`
- [ ] Options are re-readable via `IOptionsMonitor` so eviction
      behaviour can be tuned without a restart.

### AC4: Ref-counted handle prevents mid-request pool teardown

- [ ] New type `TenantConnectionHandle : IAsyncDisposable` wraps an
      `NpgsqlDataSource` with a ref count. Acquiring a handle
      increments the count; `DisposeAsync` decrements.
- [ ] `EvictAsync(tenantId)` removes the pool from the cache but
      **does not** dispose the underlying `NpgsqlDataSource` until the
      ref count drops to zero (Doc 04 §2.3 + §6.3 Step B +
      §8.2 — deletion workflow must not yank the pool out from under
      an in-flight request).
- [ ] Ref-count assertions covered by unit tests: eviction during an
      open handle defers the actual dispose; eviction with no open
      handle disposes synchronously.

### AC5: Metrics and admin diagnostics

- [ ] Emit the four metrics from Doc 04 §2.6:
  - `tamma_tenant_pool_hits_total{tenant_id}`
  - `tamma_tenant_pool_misses_total{tenant_id}`
  - `tamma_tenant_pool_evictions_total{reason}` where reason ∈
    `{idle, lru, explicit}`
  - `tamma_tenant_pool_size` (gauge of current cache entries)
  - `tamma_tenant_pool_resolve_seconds` (histogram, cold-miss build
    duration)
- [ ] Structured logs per Doc 04 §2.6: `tenant.pool.created`,
      `tenant.pool.evicted`, `tenant.pool.build_failed`.
- [ ] New admin endpoint
      `GET /api/v1/admin/diagnostics/tenant-pool` (platform-admin
      auth) returns the `ResolverStats` record with cache size, hit
      rate, per-tenant last-access timestamps.

### AC6: `ISecretsService` decryption of stored connection strings

- [ ] New interface `ISecretsService` exposes
      `string DecryptConnectionString(byte[] envelope, short kekVersion)`.
- [ ] Default implementation uses AES-256-GCM per Doc 01 §8.1 and
      Doc 04 §4.1–4.2 (Approach A): KEK from env
      `TAMMA_TENANT_KEK` (base64 32 bytes), secondary KEK from
      `TAMMA_TENANT_KEK_SECONDARY` for two-key overlap during
      rotation.
- [ ] Envelope format: `[1 byte version=0x01][1 byte kek_slot]
      [12 bytes nonce][ciphertext][16 bytes GCM tag]` per Doc 01 §8.1.
- [ ] Decrypt path tries the KEK indicated by `kekVersion` first;
      on auth-tag mismatch, tries the secondary slot — per Doc 01
      §8.3 rotation behaviour.
- [ ] A `TenantConnectionDecryptionException` is raised (and logged
      **without** the envelope contents) when both slots fail.
- [ ] `ISecretsService` and `IConnectionStringDecryptor` share the
      same implementation; the latter is the interface-seam for a
      future KMS migration (Doc 04 §4.2 Phase 2 plan).

### AC7: Tenant row validation before pool build

- [ ] On cache miss, resolver queries CP `tenants` row via
      `ControlPlaneDbContext` and validates `Status = 'active'`:
      throws `TenantNotFoundException` (missing) or
      `TenantNotProvisionedException(tenantId, status)` (any other
      status) per Doc 04 §2.2.
- [ ] The CP query is cached for 30 seconds per tenant id to avoid
      hammering CP on repeated cache misses during eviction storms.

## Technical Context

- **Design docs**:
  - `plans/db-per-tenant/04-connection-pool-and-delete.md` §1 (scale
    targets + max-connections math), §2 (resolver contract +
    implementation shape), §4 (connection-string encryption).
  - `plans/db-per-tenant/01-control-plane-split.md` §8 (envelope
    format, KEK rotation), §9 (pool lifecycle).
  - Epic 28 README success metric #5 (steady-state connection ceiling).
- **File layout**:
  - `apps/tamma-elsa/src/Tamma.Api/Services/TenantConnectionResolver/TenantConnectionResolver.cs`
  - `.../TenantConnectionOptions.cs`
  - `.../TenantConnectionHandle.cs`
  - `.../ResolverStats.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/AesGcmSecretsService.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/ISecretsService.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/DiagnosticsEndpoints.cs`
- **DI registration**: `services.AddSingleton<
  ITenantConnectionResolver, TenantConnectionResolver>();`
  and `services.AddSingleton<ISecretsService, AesGcmSecretsService>();`.
  Resolver is a **singleton** — the cache is process-wide per Doc 04
  §2.1.
- **Eviction signal topic**: `TenantConnection:EvictionSignalTopic =
  "tenant.deleted"` per Doc 04 §2.5 — in-process event bus hook so
  Story 28-5's `DeleteTenantWorkflow` can trigger eviction without
  coupling to the resolver class directly.
- **Phase 3 Stream A**: per `00-sequencing.md`, lands in parallel with
  Story 28-6 (platform tables). Non-overlapping file set confirmed.

## Dependencies

- **Blocks**: 28-8 (middleware uses the resolver on every request),
  28-9 (switch-org re-resolves across tenants), 28-12 (KEK rotation
  uses the `ISecretsService` seam).
- **Blocked by**: 28-3 (`ITenantConnectionResolver` interface exists),
  28-1 (`tenants.EncryptedConnectionString` + `KekVersion` columns
  exist).
- **External**: Npgsql 8+, `Microsoft.Extensions.Caching.Memory`,
  `System.Security.Cryptography` (AES-GCM is in `.NET 8`).

## Test Plan

### Unit tests

- Cache hit: two consecutive `DataSourceFor(tid)` calls return the
  same `NpgsqlDataSource` instance, incrementing
  `tamma_tenant_pool_hits_total` once.
- Cache miss: `DataSourceFor(tid)` with an empty cache triggers CP
  lookup, decrypt, build; increments
  `tamma_tenant_pool_misses_total` once.
- LRU eviction: cache size budget = 2, access 3 distinct tenants in
  sequence, assert the least-recently-used is disposed via the
  `PostEvictionCallback`.
- Idle eviction: tenant not accessed for > `IdleLifetime` is evicted
  on the next sweep.
- Ref-count: evict during an open handle; `NpgsqlDataSource` not
  disposed until `await using` scope exits.
- Concurrent first-miss: 10 parallel `DataSourceFor(sameTid)` calls
  trigger exactly one pool build (semaphore guards correctly).
- Decrypt: round-trip an envelope with KEK v1; assert decrypt with
  KEK v2 fails with tag mismatch; assert fallback to secondary slot
  succeeds.
- `TenantNotProvisionedException` carries `Status = 'provisioning'`
  when the tenant row is mid-provision.

### Integration tests (Testcontainers.PostgreSQL)

- Spin up Postgres + seed 3 tenant DBs (`tamma_tenant_a`,
  `tamma_tenant_b`, `tamma_tenant_c`) + populate CP `tenants` rows
  with AES-GCM-encrypted connection strings. Access each tenant,
  assert round-trip `domain_events` insert/select against the
  correct DB.
- Inject a mid-request `EvictAsync(tid)` call while a query is
  running; assert the query completes, then the pool is disposed.
- `GET /api/v1/admin/diagnostics/tenant-pool` returns populated
  `ResolverStats` after 3 tenant accesses.
- End-to-end rotation: decrypt with v1, rotate KEK to v2, re-encrypt
  the `tenants` row, assert decrypt still succeeds (two-key overlap
  window) — full re-encrypt loop is Story 28-12.

### Manual verification

- `docker compose up` with 3 seeded tenants, run load test (500 RPS
  across all three), observe `pg_stat_activity` stays under the
  calculated ceiling (3 × 10 = 30 backends).
- Kill Postgres mid-request; observe resolver emits
  `tenant.pool.build_failed` and callers receive 503.

## Definition of Done

- [ ] Acceptance criteria all green
- [ ] Unit + integration tests added, suite passes
- [ ] No new CodeQL alerts (especially: no secrets in logs)
- [ ] Design-doc references updated if the impl deviated
- [ ] Reviewed by a second engineer (cross-stream)

## Risks / Open Questions

- **Eviction vs in-flight HTTP requests.** Ref-counted handle
  addresses the critical case (Doc 04 §8.2) but long-running SSE /
  WebSocket streams can hold handles indefinitely. Doc 04 §8.4
  mandates terminating SSE on `tenant.deleted` signal; the signal
  hand-off is in Story 28-5, not this story. Flagged so the two
  stories coordinate.
- **Max-connections math at 1024 tenants.** 1024 × 10 = 10,240
  worst-case backends exceeds the Doc 04 §1.2 safe ceiling
  (~800–1000). Epic 28 success metric #5 assumes observed steady
  state stays under 4096 because most tenants are idle. If idle-rate
  assumptions fail in production, reduce `MaxPooledConnectionsPerTenant`
  to 5 (Doc 04's original recommendation) via `IOptionsMonitor` hot
  reload. Documented in the admin runbook.
- **KMS migration seam.** Doc 04 §4.2 identifies `IConnectionStringDecryptor`
  as the seam for moving KEK to AWS KMS / HashiCorp Vault in Phase 2.
  This story ships the seam but implements only Approach A; KMS
  integration is a later epic.
- **Admin diagnostics endpoint auth.** Story 28-7 finalises the
  platform-admin authorisation policy. Until then, this endpoint is
  gated on `[Authorize(Policy = "PlatformAdmin")]` with the policy
  defined inline; Story 28-7 consolidates.
