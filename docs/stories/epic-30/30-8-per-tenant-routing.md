# Story 30-8: Per-Tenant Routing — Resolve `tenantId` to Provider + Endpoints

Status: todo (planning brief, 2026-04-20)

## Story

As a **platform engineer**,
I want a `ITenantRoutingResolver` that, given a `tenantId`, returns the tenant's backend + DB connection + engine URL + any provider-specific metadata, cached in-memory with TTL, invalidated by provider-change events,
so that the per-request code path (finding 1 in the 2026-04-20 review) has a real wiring from tenant → connection string + engine URL — closing the "real per-tenant routing" half of that finding that Epic 28 Phase-B sketched but did not complete.

## Acceptance Criteria

1. `ITenantRoutingResolver` service: `Task<TenantRouting> ResolveAsync(Guid tenantId, CancellationToken ct)` returns `{ ProviderKey, TenantEndpoints, ProviderResourceIds, Cached, CachedAtUtc, ExpiresAtUtc }`.
2. Implementation `TenantRoutingResolver` backed by a `MemoryCache` with 5-min TTL + capacity 10000. Cache miss → reads `tenants.provider_key` + calls `registry.GetProvider(providerKey).ResolveEndpointsAsync(tenantId, ct)` + caches the result.
3. Invalidation listens for `TENANT.ROUTING.CHANGED` platform events and evicts the matching cache entry. The event is emitted by Story 30-2's `PersistEndpointsActivity` and Story 30-9's deprovisioning workflow.
4. `TammaAppDbContext` (Story 19-6) consumes this resolver in its `OnConfiguring` override: `ResolveAsync` yields the `DbUrl` → passes to Npgsql; per-tenant connection pooling (Story 28-4) keyed by `providerKey + tenantId`.
5. Elsa activities that dispatch workflows to the tenant's engine resolve `EngineUrl` via this resolver: `var url = (await resolver.ResolveAsync(tenantId)).Endpoints.EngineUrl;` Replaces today's hard-coded `CranlAppUrl` reads.
6. A `HealthCheckAsync(tenantId)` overload probes the resolved endpoints (calls provider's `GetStatusAsync`) — used by the admin audit panel and Story 30-10's dashboard.
7. When a tenant is in `provisioning_state != Ready`, resolver returns `TenantUnavailable` with the state as context — callers can decide to 503, 410, or queue (per Epic 28 Story 28-8's async-provisioning middleware).
8. Thread-safety: concurrent cache misses for the same tenant coalesce to a single provider call (`ConcurrentDictionary<Guid, Task<TenantRouting>>` with AsyncLazy per key; cleared on completion).
9. xUnit: concurrent miss test (100 parallel `ResolveAsync` calls for the same tenant ⇒ provider called exactly once). TTL test (cache entry expires after 5 min). Invalidation test (emit `TENANT.ROUTING.CHANGED` + verify cache eviction).
10. Closes finding 1 from the 2026-04-20 review along with Story 19-6 — 19-6 threads `TammaAppDbContext` through per-request paths; this story makes the connection string per-tenant-provider-aware. Together they are "per-request DB access is per-tenant, under RLS, with the correct backend".

## Technical Context

### Cache coherency

In-process cache with 5-min TTL + event-driven invalidation. Multiple
Tamma API instances each hold their own cache — the event bus
(`platform_events` → fan-out) ensures they all invalidate together.

Trade-off: after a rotation / re-provision, tenant routing can be
stale for up to 5 min on nodes that missed the invalidation event.
Acceptable because rotations are rare + probe/retry logic on
downstream callers handles the inconsistency window.

### Connection pool keying

`TammaAppDbContext`'s Npgsql pool is keyed by the connection string.
Two tenants on the same Hetzner VPS (DatabaseOnly topology) might
share a host:port but have different credentials + DB name — Npgsql
pools those separately, which is correct.

Tenants on Cloudflare D1 don't use Npgsql at all; they use the
Cloudflare D1 client. The resolver returns the abstract endpoint;
callers pick the right client based on `ProviderKey` (switch at the
outermost dispatch layer, not inside every repository).

### Reading the cabinet, not the row

The resolver does **not** read DB passwords directly from the
`tenants` table or from Epic 28's `cranl_database_url_encrypted`
column. It calls `ITenantInfrastructureProvider.ResolveEndpointsAsync`
which in turn reads Epic 29's secret cabinet. This means:

- Rotation through Story 29-7 / 29-8 automatically propagates.
- The resolver stays in memory; no plaintext on disk beyond the
  cabinet row.

### Event shape

```json
{
  "type": "TENANT.ROUTING.CHANGED",
  "tags": { "tenantId": "...", "providerKey": "hetzner" },
  "data": { "reason": "rotation" | "reprovision" | "deprovision", "at": "..." }
}
```

## Estimated hours

20 — resolver + coalesced cache + invalidation listener +
`TammaAppDbContext` integration + Elsa-activity integration + tests.

## Files to touch

- `apps/tamma-elsa/src/Tamma.Api/Services/Provisioning/TenantRoutingResolver.cs` (new)
- `apps/tamma-elsa/src/Tamma.Data/TammaAppDbContext.cs` (update `OnConfiguring`)
- `apps/tamma-elsa/src/Tamma.Activities/Shared/TenantEngineDispatchClient.cs` (new — consumes resolver for engine dispatch)

## References

- Review finding 1 (per-tenant wiring)
- Story 19-6 (`TammaAppDbContext` wiring)
- Story 28-4 (connection resolver + pool cache)
- Story 28-8 (provisioning-state middleware)
- Story 30-1, 30-2, 30-3..30-6
