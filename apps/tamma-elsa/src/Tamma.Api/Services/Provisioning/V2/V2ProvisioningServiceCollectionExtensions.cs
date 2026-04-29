using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Data.Abstractions;

namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Story 30-8 — DI wiring for the V2 provisioning surface and the
/// per-tenant routing directory the LRU resolver consumes.
///
/// <para>What this registers:</para>
/// <list type="bullet">
///   <item><description><see cref="NullTenantProvider"/> — always
///     wired so the registry has at least one entry. Real providers
///     (Cranl, Hetzner, Cloudflare, BYO) plug in via DI alongside.</description></item>
///   <item><description><see cref="TenantProviderRegistry"/> as a
///     singleton, built once over the registered providers.</description></item>
///   <item><description><see cref="ITenantProviderKeyLookup"/> as
///     <see cref="SqlTenantProviderKeyLookup"/> — gracefully handles
///     the "Story 30-3 column not yet present" case so deploying
///     30-8 alone is safe.</description></item>
///   <item><description><see cref="ITenantEndpointDirectory"/> as
///     <see cref="V2TenantEndpointDirectory"/> — the seam the LRU
///     resolver consults before its legacy decrypt path.</description></item>
/// </list>
///
/// <para>Idempotent — calling twice is safe; <c>TryAdd*</c> for the
/// non-collection registrations.</para>
/// </summary>
public static class V2ProvisioningServiceCollectionExtensions
{
    /// <summary>
    /// Wire the V2 provisioning + routing seam. Call once from
    /// <c>Program.cs</c> after <c>AddTammaData</c> +
    /// <c>AddTenantConnectionPool</c>.
    /// </summary>
    public static IServiceCollection AddTenantProvisioningV2(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Always register the null seam so the registry has at least
        // one provider — single-user mode + dev / test fixtures rely
        // on this. Real providers plug in via additional
        // AddSingleton<ITenantInfrastructureProvider, ...> calls.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITenantInfrastructureProvider, NullTenantProvider>());

        services.TryAddSingleton<TenantProviderRegistry>();
        services.TryAddSingleton<ITenantProviderKeyLookup, SqlTenantProviderKeyLookup>();

        // Replace any previously-registered (e.g. NullTenantEndpointDirectory)
        // ITenantEndpointDirectory binding so the V2 directory wins.
        services.RemoveAll<ITenantEndpointDirectory>();
        services.AddSingleton<ITenantEndpointDirectory, V2TenantEndpointDirectory>();

        return services;
    }
}
