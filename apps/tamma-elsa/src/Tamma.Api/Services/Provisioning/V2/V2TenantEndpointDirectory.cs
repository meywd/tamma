using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Data.Abstractions;

namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Story 30-8 / Epic 30 Phase B (B1) — production
/// <see cref="ITenantEndpointDirectory"/> that bridges
/// <see cref="LruPooledTenantConnectionResolver"/> (in
/// <c>Tamma.Data.Pooling</c>) to the V2 provider surface
/// (<see cref="ITenantInfrastructureProvider"/> +
/// <see cref="TenantProviderRegistry"/>).
///
/// <para><b>B1 — the DB-routing bypass is removed.</b> The unified
/// per-tenant <c>EncryptedConnectionString</c> envelope (via the
/// <c>tenant_databases</c> pool) is the SINGLE DB route. This directory
/// no longer turns a provider-supplied <c>DatabaseUrl</c> into a routed
/// connection string. <c>TryResolveAsync(tenantId)</c> therefore always
/// returns <see cref="TenantEndpointResolution.NotApplicable"/> for the
/// resolver's DB-routing purpose, so the resolver falls through to the
/// unified <c>EncryptedConnectionString</c> path for EVERY tenant —
/// including provider-backed (Cranl / dedicated-compute) tenants. That
/// path is fail-closed: a provider-keyed tenant whose unified envelope is
/// missing surfaces <c>TenantConnectionStringMissingException</c> rather
/// than silently routing to the central connection.</para>
///
/// <para>The provider <c>provider_key</c> is still read (via
/// <see cref="ITenantProviderKeyLookup"/>) purely for observability — a
/// provider-backed tenant is logged at Debug, and a tenant claiming a
/// key that isn't wired in this deployment (unknown to
/// <see cref="TenantProviderRegistry.TryGetProvider"/>) is logged at
/// Warn — but neither changes the DB route. Engine-URL resolution for a
/// future dedicated-compute engine dispatch stays available on
/// <see cref="ITenantInfrastructureProvider.ResolveEndpointsAsync"/> for
/// a distinct future consumer; it is intentionally NOT called on the
/// DB-routing hot path.</para>
///
/// <para><b>Concurrency</b>: this type is a thread-safe singleton.
/// Multiple LRU cold-misses for the same tenant call
/// <c>TryResolveAsync</c> in parallel; the resolver's per-tenant
/// <c>SemaphoreSlim</c> already collapses the herd to one call per
/// build, so this type does not need its own coalescing. Provider
/// implementations are expected to be cheap on the cache-hit path
/// (Story 30-1 ADR §"Idempotency").</para>
/// </summary>
public sealed class V2TenantEndpointDirectory : ITenantEndpointDirectory
{
    private readonly TenantProviderRegistry _registry;
    private readonly ITenantProviderKeyLookup _providerKeyLookup;
    private readonly ILogger<V2TenantEndpointDirectory> _logger;

    public V2TenantEndpointDirectory(
        TenantProviderRegistry registry,
        ITenantProviderKeyLookup providerKeyLookup,
        ILogger<V2TenantEndpointDirectory>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(providerKeyLookup);
        _registry = registry;
        _providerKeyLookup = providerKeyLookup;
        _logger = logger ?? NullLogger<V2TenantEndpointDirectory>.Instance;
    }

    public async Task<TenantEndpointResolution> TryResolveAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Epic 30 Phase B (B1) — the unified per-tenant
        // EncryptedConnectionString envelope (via the tenant_databases
        // pool) is the SINGLE DB route. This directory no longer turns a
        // provider-supplied DatabaseUrl into a routed connection string;
        // that bypass is removed. EVERY tenant — including provider-backed
        // (Cranl / dedicated-compute) tenants — resolves its DB connection
        // through the unified envelope, so this method always returns
        // NotApplicable for the resolver's DB-routing purpose. The resolver
        // then falls through to its EncryptedConnectionString path, which is
        // fail-closed: a provider-keyed tenant whose unified envelope is
        // missing surfaces TenantConnectionStringMissingException rather than
        // silently routing to the central connection.
        //
        // Engine-URL resolution for a future dedicated-compute engine
        // dispatch stays available on
        // ITenantInfrastructureProvider.ResolveEndpointsAsync (reachable via
        // the registry) for a distinct future consumer. It is intentionally
        // NOT called here — DB routing must never depend on a provider
        // round-trip, and must never be short-circuited by the provider's
        // provisioning state (which would preempt the unified envelope's
        // own fail-closed handling).

        // 1. Read provider_key from CP — purely for observability now.
        //    TenantNotFoundException bubbles verbatim (definitive 404),
        //    matching the resolver's own not-found surface.
        var providerKey = await _providerKeyLookup
            .GetProviderKeyAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(providerKey))
        {
            // Legacy / unified-only tenant — nothing provider-specific to
            // note. Resolver uses the EncryptedConnectionString path.
            return TenantEndpointResolution.NotApplicable;
        }

        // 2. Provider-backed tenant. Emit an observability signal, then still
        //    route via the unified envelope. A tenant claiming a provider key
        //    that isn't wired in this deployment is worth a WARN — but it no
        //    longer changes the DB route (the pool envelope is authoritative).
        if (_registry.TryGetProvider(providerKey, out _))
        {
            _logger.LogDebug(
                "tenant.routing.provider_backed_via_unified_envelope tenantId={TenantId} providerKey={ProviderKey}",
                tenantId,
                providerKey);
        }
        else
        {
            _logger.LogWarning(
                "tenant.routing.provider_key_unregistered tenantId={TenantId} providerKey={ProviderKey} " +
                "registeredKeys={RegisteredKeys} route=unified_envelope",
                tenantId,
                providerKey,
                string.Join(",", _registry.RegisteredKeys));
        }

        // B1: never return a provider DatabaseUrl. Unified envelope is the
        // one and only DB route.
        return TenantEndpointResolution.NotApplicable;
    }
}
