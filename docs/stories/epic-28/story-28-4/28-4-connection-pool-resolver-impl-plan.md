# Story 28-4 Implementation Plan — Tenant Connection Resolver + LRU Pool Cache

**Status**: Planned (2026-04-20)
**Story brief**: [`28-4-connection-pool-resolver.md`](./28-4-connection-pool-resolver.md)
**Epic 28 phase**: B (Data plane — parallel with 28-6)
**Branch**: `feat/story-28-4-connection-pool-resolver`

---

## 1. Objective

Replace the 28-3 stub with a production-grade
`TenantConnectionResolver` that owns a lock-free hot path, an
`IMemoryCache`-driven LRU, per-tenant `SemaphoreSlim` for
thundering-herd protection, and a ref-counted `TenantConnectionHandle`
so `EvictAsync` during a mid-request access does not yank the pool out
from under the in-flight query. Ships the real per-tenant Npgsql pool
with `ApplicationName=tamma-api;tenant=<guid>` tagging, 5-min idle
timeout, and a 1024-tenant soft cap. Emits `POOL.*` events on every
state change for observability.

## 2. Dependencies

Hard blockers:

- **Story 28-3** — `ITenantConnectionResolver` contract + stub.
- **Story 28-1** — tenant schema so `ApplicationName` matches a real
  tenant DB.
- **Story 28-12** — `ISecretsService.DecryptConnectionString(...)`
  returns the per-tenant plaintext connection string.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/TenantConnectionResolver/TenantConnectionResolver.cs` | Main impl. |
| `.../TenantConnectionHandle.cs` | `IAsyncDisposable` ref-count wrapper. |
| `.../TenantConnectionOptions.cs` | `IOptionsMonitor` binding for `TenantConnection` config section. |
| `.../ResolverStats.cs` | In-memory counters exposed to `GET /admin/pools/stats`. |
| `.../PoolWarmupService.cs` | Optional `IHostedService` that pre-warms N most-active tenants on boot (fed by 28-10 analytics). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Endpoints/PoolsAdminEndpoints.cs` | `GET /api/admin/pools/stats`, `POST /api/admin/pools/:tenantId/evict`. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/TenantConnectionResolver/TenantConnectionResolverTests.cs` | ~25 unit cases: LRU, thundering herd, ref-count eviction, options reload. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/TenantConnectionResolver/TenantConnectionResolverStressTests.cs` | 10k-request parallel stress; asserts no leaks or races. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/StubTenantConnectionResolver.cs` | Delete (replaced). |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Program.cs` | Replace stub registration with real resolver. Configure `IMemoryCache` with `SizeLimit=MaxTenants`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/appsettings.json` | Add `TenantConnection` options section. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/TenantDbContextFactory.cs` | Acquire `TenantConnectionHandle` on `CreateAsync`; dispose with context. |
| `/home/meywd/tamma/docs/deployment/connection-pool-tuning.md` | Operator guide: when to raise `MaxTenants`, how to read stats. |

## 5. Sequence of changes

### Step 1 — Options + stats skeleton (2h)

- `TenantConnectionOptions` with all fields per brief AC3.
- `ResolverStats` counter bag (`CacheHits`, `CacheMisses`, `EvictionsTotal`, `CurrentPoolCount`).
- **Commit**: `feat(pool): options + stats`.

### Step 2 — Ref-counted handle (3h)

- `TenantConnectionHandle` wraps `NpgsqlDataSource` + atomic counter.
- `Acquire()` increments; `DisposeAsync` decrements; when zero and
  marked `PendingDispose`, calls `dataSource.DisposeAsync()`.
- Unit tests: 6 cases covering acquire/dispose interleavings.
- **Commit**: `feat(pool): TenantConnectionHandle with ref counting`.

### Step 3 — Resolver main path (6h)

- `DataSourceFor(tenantId)`:
  1. Fast path: `_pools.TryGetValue(tenantId, out var lazy)` → `lazy.Value`.
  2. Slow path: acquire per-tenant `SemaphoreSlim` → double-check →
     fetch tenant row from CP → decrypt → build `NpgsqlDataSourceBuilder`
     with options → wrap in `Lazy<NpgsqlDataSource>` → `_pools.TryAdd`.
  3. `IMemoryCache.Set(tenantId, lazy, entryOptions)` with post-eviction
     callback that disposes via handle ref count.
- `ElsaDataSourceFor(tenantId)` mirrors for per-tenant Elsa DB.
- **Commit**: `feat(pool): TenantConnectionResolver main path`.

### Step 4 — Eviction + stats endpoint (3h)

- `EvictAsync(tenantId)` removes from cache (triggers callback).
- `GetStats()` snapshots counters.
- `PoolsAdminEndpoints` exposes stats + manual evict (RBAC: platform admin).
- **Commit**: `feat(pool): eviction + admin stats endpoint`.

### Step 5 — Warmup + options reload (2h)

- `PoolWarmupService` reads top-N active tenants from
  `platform_analytics_hourly` on boot (28-10) and pre-warms pools.
  Optional — fed by config flag.
- `IOptionsMonitor.OnChange` hook: when options change, new pools
  use new settings; existing pools keep old settings (Npgsql
  immutable).
- **Commit**: `feat(pool): warmup + options live reload`.

### Step 6 — Unit + stress tests (4h)

- 25 unit cases: cache hit/miss, thundering herd (50 parallel
  first-time requests → 1 DataSource build), LRU eviction, ref-count
  eviction deferral, options reload, connection-string decrypt
  failure → INFO log + 503 to caller.
- Stress test: 10k parallel requests across 1050 tenants (over cap
  → LRU evicts 26 tenants); assert `ResolverStats.EvictionsTotal == 26`.
- **Commit**: `test(pool): resolver unit + stress`.

### Step 7 — Docs (1h)

- `connection-pool-tuning.md`: `MaxTenants` sizing, relationship to
  Postgres `max_connections`, how to read stats.
- **Commit**: `docs(pool): operator tuning guide`.

## 6. Test strategy

### Unit

- Per AC1-AC4, AC6 (secrets integration mocked), option reload.
- Ref-count edge cases: dispose during eviction, double dispose, cancelled.

### Integration

- Testcontainers Postgres: provision 3 tenants, hit each from multiple
  parallel requests, assert pool stats match expectation.

### Stress

- 10k concurrent tenant lookups with 1050 distinct tenantIds; assert
  throughput > 5k ops/s, no socket leaks (verified via `ss -t` diff
  before/after).

### Performance benchmark

- Warm path p95 < 50µs (cache hit).
- Cold path p95 < 50ms (decrypt + build DataSource).

## 7. Rollback plan

- **Feature flag**: none needed — the resolver replaces the stub
  entirely. Rollback = revert the commit chain; stub remains in
  the repo history.
- **Mid-request safety**: ref-counted handle guarantees no pool
  yank mid-query.
- **Eviction safety**: `PostEvictionCallback` is the only dispose
  site; leaks are structurally impossible.
- **Non-reversible**: none.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Options + stats | 2 |
| 2. Ref-counted handle | 3 |
| 3. Resolver main path | 6 |
| 4. Eviction + admin endpoint | 3 |
| 5. Warmup + options reload | 2 |
| 6. Unit + stress tests | 4 |
| 7. Docs | 1 |
| **Total** | **21** (brief target 22) |

## 9. Open questions

- **`MaxTenants=1024` sizing**: Postgres `max_connections=300`
  (raised in 19-6 plan). With `MaxPooledConnectionsPerTenant=10`,
  1024 tenants × 10 = 10240 theoretical max — Postgres
  `max_connections` bounds this. The pool cap + LRU handles the
  overflow; in practice, idle tenants' pools drain to 0 via
  `IdleLifetime`. Document the math.
- **`NpgsqlDataSourceBuilder.EnableParameterLogging` default**:
  off in prod (leaks PII to logs). On in dev for debugging. Wire
  via `TenantConnection:EnableParameterLogging` (default false).
- **Pool warmup dependencies on 28-10**: warmup reads analytics. If
  28-10 not yet merged, warmup service is a no-op (safe).
- **Options reload semantics**: does changing `MaxTenants` at
  runtime evict overflow pools? Plan: yes — when new cap is
  smaller, next eviction cycle drains until under cap.
- **`ApplicationName` fingerprint size**: `tamma-api;tenant=<guid>`
  is 45 chars. Postgres truncates at 64; safe.
- **Secrets dependency**: until 28-12 ships, use a passthrough
  `ISecretsService` that returns the raw connection string from
  CP (effectively no-op encryption). Documented as a soft
  dependency — swap to real encryption when 28-12 lands.
