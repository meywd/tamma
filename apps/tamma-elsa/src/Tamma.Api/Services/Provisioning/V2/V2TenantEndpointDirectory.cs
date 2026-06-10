using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Data.Abstractions;

namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Story 30-8 — production
/// <see cref="ITenantEndpointDirectory"/> that bridges
/// <see cref="LruPooledTenantConnectionResolver"/> (in
/// <c>Tamma.Data.Pooling</c>) to the V2 provider surface
/// (<see cref="ITenantInfrastructureProvider"/> +
/// <see cref="TenantProviderRegistry"/>).
///
/// <para>Resolution flow per <c>TryResolveAsync(tenantId)</c>:
/// <list type="number">
///   <item><description>Look up <c>tenants.provider_key</c> via
///     <see cref="ITenantProviderKeyLookup"/>. If <c>null</c> →
///     <see cref="TenantEndpointResolution.NotApplicable"/>; the LRU
///     resolver falls back to the legacy
///     <c>EncryptedConnectionString</c> path.</description></item>
///   <item><description>Look up the registered provider via
///     <see cref="TenantProviderRegistry.TryGetProvider"/>. If the
///     provider key is unknown to the registry → log a WARN +
///     <see cref="TenantEndpointResolution.NotApplicable"/>. This
///     handles deployments where a tenant has been migrated to a
///     provider the local process doesn't have wired (e.g. Cloudflare
///     provider on a deployment without the Cloudflare client). The
///     legacy fallback gives the operator a chance to recover.</description></item>
///   <item><description>Call
///     <see cref="ITenantInfrastructureProvider.ResolveEndpointsAsync"/>.
///     Provider-thrown
///     <see cref="InvalidOperationException"/> ("not in a state where
///     endpoints are available") translates to
///     <see cref="TenantNotProvisionedException"/> so the resolver's
///     negative cache + status middleware behave consistently across
///     the V2 path and the legacy path.</description></item>
///   <item><description>Build a
///     <see cref="TenantEndpointResolution"/> from the provider's
///     <see cref="TenantEndpoints"/> and return it.</description></item>
/// </list></para>
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

        // 1. Read provider_key from CP. NullTenantFoundException bubbles.
        string? providerKey;
        try
        {
            providerKey = await _providerKeyLookup
                .GetProviderKeyAsync(tenantId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TenantNotFoundException)
        {
            // Bubble verbatim — the resolver expects this to surface as
            // a definitive 404 / "no such tenant".
            throw;
        }

        if (string.IsNullOrWhiteSpace(providerKey))
        {
            // Legacy tenant — no V2 routing applies. Resolver falls
            // back to the EncryptedConnectionString path.
            return TenantEndpointResolution.NotApplicable;
        }

        // 2. Find the provider. Unknown key → NotApplicable so the
        //    resolver can still recover via the legacy path. We log a
        //    warning so operators see that a tenant claims to be on a
        //    provider that isn't wired in this deployment.
        if (!_registry.TryGetProvider(providerKey, out var provider) || provider is null)
        {
            _logger.LogWarning(
                "tenant.routing.provider_key_unregistered tenantId={TenantId} providerKey={ProviderKey} " +
                "registeredKeys={RegisteredKeys} fallback=legacy",
                tenantId,
                providerKey,
                string.Join(",", _registry.RegisteredKeys));
            return TenantEndpointResolution.NotApplicable;
        }

        // 3. Resolve endpoints. Translate provider exceptions into the
        //    abstraction-layer exceptions Tamma.Data understands.
        TenantEndpoints endpoints;
        try
        {
            endpoints = await provider
                .ResolveEndpointsAsync(tenantId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // The V2 contract uses InvalidOperationException to mean
            // "tenant is not in a state where endpoints are available"
            // (e.g. ProvisioningState.Pending). The LRU resolver's
            // negative cache + Story 28-8 middleware both speak
            // TenantNotProvisionedException, so translate.
            _logger.LogInformation(
                "tenant.routing.endpoints_unavailable tenantId={TenantId} providerKey={ProviderKey} reason={Reason}",
                tenantId,
                providerKey,
                ex.Message);
            throw new TenantNotProvisionedException(tenantId, providerKey);
        }
        catch (NotSupportedException)
        {
            // Null provider seam was selected (or another provider
            // explicitly opted out). NotApplicable so the resolver
            // falls back to the legacy path — distinct from the
            // not-provisioned semantic above.
            _logger.LogWarning(
                "tenant.routing.provider_not_supported tenantId={TenantId} providerKey={ProviderKey} fallback=legacy",
                tenantId,
                providerKey);
            return TenantEndpointResolution.NotApplicable;
        }

        if (string.IsNullOrWhiteSpace(endpoints.DatabaseUrl))
        {
            // Provider returned without a database URL — treat as
            // "endpoints unavailable" so the resolver doesn't try to
            // build a NpgsqlDataSource on an empty string.
            _logger.LogWarning(
                "tenant.routing.endpoints_missing_db_url tenantId={TenantId} providerKey={ProviderKey}",
                tenantId,
                providerKey);
            throw new TenantNotProvisionedException(tenantId, providerKey);
        }

        return TenantEndpointResolution.Resolved(
            endpoints.DatabaseUrl,
            endpoints.EngineUrl,
            providerKey);
    }
}
