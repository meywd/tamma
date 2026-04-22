# Story 30-8 Implementation Plan — Per-Tenant Routing Resolver

**Status**: Planned (2026-04-20)
**Story brief**: [`30-8-per-tenant-routing.md`](./30-8-per-tenant-routing.md)
**Epic 30 phase**: Runtime — after 30-3..30-6.
**Branch**: `feat/story-30-8-per-tenant-routing`

---

## 1. Objective

Ship `ITenantRoutingResolver` that maps a `tenantId` to
`{ ProviderKey, TenantEndpoints, ProviderResourceIds }` with an
in-process cache, 5-min TTL, and event-driven invalidation on
`TENANT.ROUTING.CHANGED`. Closes the "real per-tenant routing" half
of review finding 1 alongside 19-6. Replaces every hard-coded
`CranlAppUrl` read in Elsa activities and makes `TammaAppDbContext.OnConfiguring`
per-tenant aware.

## 2. Dependencies

Hard blockers:

- **Story 19-6** — app-role DbContext wiring.
- **Story 28-4** — per-tenant Npgsql pool.
- **Story 28-8** — provisioning-state middleware.
- **Stories 30-1..30-6** — providers resolve endpoints.

## 3. Files to create

| Absolute path | Purpose |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/TenantRoutingResolver.cs` | Impl. |
| `.../Services/Provisioning/ITenantRoutingResolver.cs` | Contract. |
| `.../Services/Provisioning/TenantRouting.cs` | Result record. |
| `.../Services/Provisioning/TenantRoutingCache.cs` | `MemoryCache` wrapper with coalescing. |
| `.../Services/Provisioning/TenantRoutingInvalidationListener.cs` | Subscribes to `TENANT.ROUTING.CHANGED`. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/Shared/TenantEngineDispatchClient.cs` | Uses resolver for engine dispatch. |
| `/home/meywd/tamma/apps/tamma-elsa/tests/Tamma.Api.Tests/Routing/TenantRoutingResolverTests.cs` | Concurrency + TTL + invalidation. |

## 4. Files to modify

| Absolute path | Change |
|---|---|
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Data/TammaAppDbContext.cs` | `OnConfiguring` reads resolver. |
| `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.Activities/**/*.cs` that use `CranlAppUrl` | Replace with resolver lookup. |

## 5. Sequence of changes

### Step 1 — Interface + result types (1h)

- Contract + `TenantRouting` record.
- **Commit**: `feat(routing): contract + types`.

### Step 2 — Coalescing cache (3h)

- `TenantRoutingCache` with `ConcurrentDictionary<Guid, Task<TenantRouting>>`
  + `MemoryCache` for TTL.
- `ResolveAsync` either reads cached task or creates a new
  `AsyncLazy<TenantRouting>` under per-key lock.
- Unit tests: 100 concurrent calls for same tenant → provider
  called exactly once.
- **Commit**: `feat(routing): coalescing cache`.

### Step 3 — Resolver impl (3h)

- `TenantRoutingResolver.ResolveAsync`:
  1. Read cache.
  2. On miss: `ControlPlaneDbContext` reads `tenants.provider_key` +
     `provider_resource_ids` + state.
  3. If state != Ready: return `TenantUnavailable(status)`.
  4. Resolve `TenantEndpoints` via
     `registry.GetProvider(key).ResolveEndpointsAsync(tenantId)`.
  5. Cache 5 min.
- **Commit**: `feat(routing): resolver main path`.

### Step 4 — Invalidation listener (2h)

- `TenantRoutingInvalidationListener` subscribes via RabbitMQ (or
  in-process bus) to `TENANT.ROUTING.CHANGED`.
- On event: evict cache entry.
- **Commit**: `feat(routing): event-driven invalidation`.

### Step 5 — `TammaAppDbContext.OnConfiguring` (3h)

- `OnConfiguring` injects resolver; reads `TenantContext.Current.TenantId`;
  calls resolver; uses `DbUrl` for the Npgsql options.
- Pool keyed by connection string (28-4 handles).
- Unit test: two tenants with different backends get different
  connections.
- **Commit**: `feat(routing): TammaAppDbContext per-tenant connect`.

### Step 6 — Elsa activity dispatch (3h)

- `TenantEngineDispatchClient.PostAsync(tenantId, path, body)`:
  - Resolves `EngineUrl` via resolver.
  - POSTs with service JWT.
- Refactor existing activities that call `CranlAppUrl` to use this
  client.
- **Commit**: `feat(routing): engine dispatch via resolver`.

### Step 7 — HealthCheckAsync (2h)

- `HealthCheckAsync(tenantId)` calls provider's `GetStatusAsync`;
  used by 28-11 admin audit + 30-10 dashboard.
- **Commit**: `feat(routing): health check pass-through`.

### Step 8 — Integration test + finding closeout (3h)

- E2E: create two tenants on different backends; verify both route
  correctly; simulate rotation; verify cache refresh.
- Update audit findings `orgs/002`, `orgs/004`, `admin-db/020`,
  `admin-db/021` cross-reference per finding 1 closure.
- **Commit**: `test(routing): multi-backend E2E + finding closure`.

## 6. Test strategy

### Unit

- Coalescing: 100 parallel miss for same tenant → 1 provider call.
- TTL expiry: cache entry gone after 5 min.
- Invalidation: event emitted → cache cleared.
- `TenantUnavailable` path for non-Ready states.

### Integration

- Two tenants, different backends, different connections.
- Rotation event → cache refresh → new value propagated.

## 7. Rollback plan

- **Feature flag**: `Routing:UseResolver=true`. Off → `TammaAppDbContext`
  falls back to static connection string (pre-30-8 behaviour).
- **Non-reversible**: rollback leaves caches warm but benign.

## 8. Estimated hours

| Step | Hours |
|---|---|
| 1. Contract | 1 |
| 2. Coalescing cache | 3 |
| 3. Resolver | 3 |
| 4. Invalidation listener | 2 |
| 5. TammaAppDbContext wire | 3 |
| 6. Engine dispatch | 3 |
| 7. HealthCheckAsync | 2 |
| 8. E2E | 3 |
| **Total** | **20** (matches brief). |

## 9. Open questions

- **5-min TTL vs. row-update timestamp**: TTL is simpler. Row-
  timestamp would avoid stale reads post-rotation. Acceptable for
  rotation-rare workflows.
- **Event transport**: RabbitMQ is cross-process; in-process bus
  would miss other API pods. Plan: RabbitMQ.
- **Cache size**: capacity 10000. Each entry ~1 KB = ~10 MB.
- **Provider call per miss**: some providers (Cloudflare, Hetzner)
  make external API calls in `ResolveEndpointsAsync`. The resolver
  avoids repeating this via cache + coalesce. Any single external
  call on cold path is <500 ms.
- **Non-Ready handling**: resolver returns `TenantUnavailable`;
  28-8 middleware translates to 503.
- **Connection-pool key for same `ProviderKey` + different tenants**:
  keyed by connection string (different per tenant). Safe.
