using Microsoft.Extensions.DependencyInjection;

namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Adapts a scoped <see cref="ITenantInfrastructureProvider"/> implementation
/// (e.g. one that depends on <c>ControlPlaneDbContext</c> or another
/// scoped EF context) so it can live in the singleton-only
/// <see cref="TenantProviderRegistry"/>.
///
/// <para>The registry caches the provider list at construction time and
/// then hands the same instance to every caller — that contract is
/// fundamentally singleton-shaped. Real cloud-backed providers (Cranl,
/// Hetzner, Cloudflare) will be true singletons that own an HTTP client
/// + rate limiter, but the v2 Cranl wrapper (Story 30-3) reuses v1's
/// scoped <see cref="ControlPlaneDbContext"/> dependency, which forces
/// scope-per-call here.</para>
///
/// <para>Each method opens a fresh DI scope, resolves the implementation
/// via <see cref="IServiceProvider.GetRequiredService"/>, runs the call,
/// and disposes the scope. Functionally identical to a request-scoped
/// resolution — the EF context's lifetime stays bounded to the method call,
/// which matches what v1's per-request handler did.</para>
///
/// <para><b>Generic on <typeparamref name="TImplementation"/></b> so that
/// <see cref="Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable"/>
/// considers each provider a distinct implementation type — without the
/// generic parameter every closed adapter would collapse onto the same
/// concrete type, and DI would refuse the registration.</para>
/// </summary>
internal sealed class ScopedTenantInfrastructureProviderAdapter<TImplementation> : ITenantInfrastructureProvider
    where TImplementation : class, ITenantInfrastructureProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _providerKey;
    private readonly ProviderCapabilities _capabilities;

    public ScopedTenantInfrastructureProviderAdapter(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        // Capability is cached on construction — every implementation's
        // GetCapabilities is documented as cheap + cacheable, so we open a
        // single startup scope to read it. This avoids opening a scope on
        // every onboarding-UI list call.
        using var scope = _scopeFactory.CreateScope();
        var instance = scope.ServiceProvider.GetRequiredService<TImplementation>();
        _providerKey = instance.ProviderKey;
        _capabilities = instance.GetCapabilities();
    }

    public string ProviderKey => _providerKey;

    public ProviderCapabilities GetCapabilities() => _capabilities;

    public async Task<ProvisioningResult> ProvisionAsync(
        Guid tenantId,
        ProvisioningRequest request,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var instance = scope.ServiceProvider.GetRequiredService<TImplementation>();
        return await instance.ProvisionAsync(tenantId, request, ct);
    }

    public async Task<ProvisioningStatusSnapshot> GetStatusAsync(
        Guid tenantId,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var instance = scope.ServiceProvider.GetRequiredService<TImplementation>();
        return await instance.GetStatusAsync(tenantId, ct);
    }

    public async Task DeprovisionAsync(
        Guid tenantId,
        DeprovisioningRequest request,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var instance = scope.ServiceProvider.GetRequiredService<TImplementation>();
        await instance.DeprovisionAsync(tenantId, request, ct);
    }

    public async Task<TenantEndpoints> ResolveEndpointsAsync(
        Guid tenantId,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var instance = scope.ServiceProvider.GetRequiredService<TImplementation>();
        return await instance.ResolveEndpointsAsync(tenantId, ct);
    }
}
