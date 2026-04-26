# Tenant Connection Pool — Operator Tuning Guide

**Story**: 28-4 (LRU pool resolver)
**Audience**: platform operators / oncall engineers
**Last updated**: 2026-04-26

The per-tenant database connection pool is owned by
`LruPooledTenantConnectionResolver` (in `Tamma.Data.Pooling`). One warm
`NpgsqlDataSource` is held per active tenant, capped by
`TenantConnectionPool:MaxEntries`. This doc covers the knobs, the
math, and the diagnostic surface.

---

## Configuration (appsettings.json)

```json
"TenantConnectionPool": {
  "MaxEntries": 500,
  "MaxPoolSize": 5,
  "MinPoolSize": 0,
  "ConnectionIdleLifetimeSeconds": 300,
  "ConnectTimeoutSeconds": 5,
  "CommandTimeoutSeconds": 30,
  "KeepAliveSeconds": 30,
  "TenantRowCacheSeconds": 30,
  "Warmup": {
    "Enabled": false,
    "TopTenants": 10,
    "PerTenantTimeoutSeconds": 5
  }
}
```

| Key | Default | Notes |
|---|---:|---|
| `MaxEntries` | 500 | LRU cap. Caps **process-wide** warm pools. Above this, the LRU evicts the least-recently-used tenant. |
| `MaxPoolSize` | 5 | Per-tenant Npgsql `MaxPoolSize`. Caps connections-per-tenant-per-process. |
| `MinPoolSize` | 0 | Idle tenants drain to zero — they pay memory only, not Postgres backend slots. |
| `ConnectionIdleLifetimeSeconds` | 300 (5m) | Per-connection idle reap. Inside Npgsql; doesn't affect LRU. |
| `ConnectTimeoutSeconds` | 5 | Postgres connect timeout. Bump to 10–15 in cross-region setups. |
| `CommandTimeoutSeconds` | 30 | Default per-statement timeout. Long migrations need a higher per-context override. |
| `KeepAliveSeconds` | 30 | Npgsql TCP keep-alive. Helps reset NAT idle-timers on long-lived hosted-service connections. |
| `TenantRowCacheSeconds` | 30 | How long the resolver caches a tenant's CP row. Eviction storms during warmup don't hammer CP. |

### Warmup sub-section

| Key | Default | Notes |
|---|---:|---|
| `Enabled` | `false` | Master kill-switch. Off until Story 28-10 analytics is producing rows. |
| `TopTenants` | 10 | Number of most-active tenants to pre-warm at startup. |
| `PerTenantTimeoutSeconds` | 5 | Per-tenant warmup timeout. A failure here is logged + skipped; the loop continues. |

---

## Sizing math

The hard ceiling on Postgres backends from this process is:

```
backends_max ≤ MaxEntries × MaxPoolSize
```

With defaults: **500 × 5 = 2500 backends**. Postgres `max_connections`
(from infra) caps the absolute total across all processes. Set
`max_connections` to **at least** `2 × backends_max` to leave headroom
for admin connections + replication + emergency forensics.

In practice most tenants are idle and their pools drain to 0 via
`ConnectionIdleLifetimeSeconds`. Steady-state backends are usually
**5–15% of theoretical max**. Watch `pg_stat_activity` to confirm
before raising `MaxEntries` past 500.

### Raising `MaxEntries`

Raise when:
- `tamma.tenant_pools.evicted_total{reason="lru"}` is climbing in
  steady state (eviction churn = pool rebuild cost on every other
  request)
- `cache_hit_ratio` < 0.95 with no obvious request-pattern change
- the tenant population is growing past 500 active tenants

Don't raise above what `Postgres.max_connections × 0.4` allows for
this process (the rest is for replication + admin + other apps).

### Raising `MaxPoolSize`

Raise when:
- One tenant is hot (>5 concurrent in-flight queries) and is being
  rate-limited by Npgsql's pool exhaustion
- p95 latency is worse than expected and connection-acquisition shows
  in profiling

Don't raise to the point where `MaxEntries × MaxPoolSize` exceeds
backend capacity. Prefer per-tenant overrides if one tenant truly
needs more slots than the rest (not yet supported — file an issue).

---

## Diagnostics surface

### Admin endpoints (owner-only)

```
GET  /api/admin/pools/stats
GET  /api/admin/pools/tenants?limit=50
POST /api/admin/pools/{tenantId}/evict
```

`stats` returns the `DetailedPoolStats` snapshot:

```json
{
  "detailed": {
    "warmPoolCount": 142,
    "openedTotal": 380,
    "evictedTotal": 238,
    "evictedByLru": 230,
    "evictedExplicit": 8,
    "hitsTotal": 19284,
    "missesTotal": 380,
    "hitRatio": 0.9806
  },
  "snapshot": { /* legacy 3-field GetStats() */ }
}
```

`tenants` returns currently-warm tenants in MRU order with
outstanding lease counts (>0 means an `EvictAsync` would be deferred):

```json
{
  "tenants": [
    { "tenantId": "...", "outstandingLeases": 0 },
    { "tenantId": "...", "outstandingLeases": 1 }   // SSE stream open
  ]
}
```

`evict` is for ops-firefighting (e.g. force a fresh pool after a
connection-string rotation that didn't go through the standard flow).

### OpenTelemetry metrics

| Metric | Type | Tags | Description |
|---|---|---|---|
| `tamma.tenant_pools.warm` | gauge | — | Current warm-pool count |
| `tamma.tenant_pools.opened_total` | counter | — | Pools built since startup |
| `tamma.tenant_pools.evicted_total` | counter | `reason` ∈ {lru, explicit, rotation} | Evictions by cause |
| `tamma.tenant_pools.cache_hit_ratio` | gauge | — | Lifetime hit ratio in [0,1] |

Meter name: `Tamma.TenantConnectionPool` (pin in dashboards).

### Structured log events

| Event | Level | Tags | Meaning |
|---|---|---|---|
| `tenant.pool.created` | INFO | `tenantId`, `maxPoolSize` | Cold-miss build succeeded |
| `tenant.pool.evicted` | INFO | `tenantId`, `reason` | Pool removed from cache |
| `tenant.pool.dispose_deferred` | INFO | `tenantId`, `outstandingLeases` | Eviction deferred until leases release |
| `tenant.pool.dispose_failed` | WARN | `tenantId` | Postgres dispose threw — pool dropped |
| `tenant.pool.build_failed` | WARN | `tenantId`, `stage` | Cold-miss build failed (decrypt / db) |

---

## Common scenarios

### "p95 connection-acquire jumped"

1. Check `tamma.tenant_pools.cache_hit_ratio` over the last hour. If
   dropping, you're pool-thrashing.
2. Check `tamma.tenant_pools.evicted_total{reason="lru"}` slope. If
   climbing > 1/s steady state, raise `MaxEntries` (see sizing math).
3. Check `pg_stat_activity` count by `application_name LIKE 'tamma-api;tenant=%'`.
   If close to `max_connections × 0.4`, lower `MaxPoolSize` or raise
   `max_connections`.

### "A tenant is stuck — eviction doesn't take effect"

1. Hit `GET /api/admin/pools/tenants` and look for the tenant's
   `outstandingLeases` count.
2. If > 0, a long-running consumer (SSE stream, hosted activity)
   holds a `LeaseAsync` reference. Check the SSE pipeline + Elsa
   long-running activities for that tenant.
3. The eviction IS effective — new requests build a fresh pool. The
   underlying Npgsql data source is just deferred-disposed until the
   last lease releases.

### "Restart cleared cache — first hour is slow"

1. Flip `TenantConnectionPool:Warmup:Enabled = true` (requires Story
   28-10 analytics to be producing rows).
2. Optionally raise `Warmup:TopTenants` to cover more of your active
   set.
3. Startup will pre-warm the top-N tenants — first-request cold-miss
   cost falls from ~50ms to a cache hit.

### "Force-rotation just ran — need fresh pools"

1. `POST /api/admin/pools/{tenantId}/evict` for each rotated tenant.
2. Subsequent requests build a fresh pool with the new credentials.
3. Confirm via `tamma.tenant_pools.opened_total` increment.

---

## Production safety notes

- **Singleton lifetime**: the resolver is a singleton per process. A
  config reload changes options for **future** pools only — existing
  pools keep their original settings (Npgsql data sources are immutable).
- **Eviction during in-flight**: Npgsql's own `DisposeAsync` waits for
  in-flight `NpgsqlConnection`s to return. Short-lived requests don't
  need ref-counting. Long-lived consumers should use `LeaseAsync` so
  eviction defers until the lease releases.
- **Warmup is best-effort**: a warmup failure for one tenant is logged
  and skipped; the API still serves cold-path requests for that
  tenant. Warmup is performance, not correctness.
- **MaxEntries is a soft cap**: bursts above the cap are evicted on
  next miss. There's no admission control — under sustained overload
  the LRU thrashes. Watch the eviction counter.
