using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Providers;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Extensions;

/// <summary>
/// Epic 46 — DI registration for the live model-listing seam (Story 46-0,
/// <see cref="IProviderModelCatalog"/>) and the persisted provider-settings
/// store (Story 46-1, <see cref="IProviderSettingsStore"/>).
///
/// <para>Both are singletons: the model catalog holds the 5-minute per
/// (provider, tenant) list cache; the settings store holds the volatile
/// whole-snapshot the SYNC egress reads ride
/// (<c>InlineToolLoopRunner.LoadProviderConfig</c> and
/// <c>LlmProxyService</c>). Their collaborators allow it —
/// <c>IProviderCredentialResolver</c> is a singleton (Story 32-3) and DB
/// access goes through the singleton-safe
/// <see cref="IDbContextFactory{TContext}"/> seam.</para>
///
/// <para>The settings repository is wired only when the control-plane
/// <see cref="ControlPlaneDbContext"/> factory is registered (same
/// conditional-wiring pattern as the BYOK reader in
/// <c>AddProviderCredentialResolution</c>): hosts without a CP database get a
/// store whose reads all answer "no row" — resolution stays byte-identical to
/// pre-46-1 — and whose writes fail loud.</para>
/// </summary>
public static class ProviderSettingsServiceCollectionExtensions
{
    public static IServiceCollection AddProviderModelCatalogAndSettings(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(d => d.ServiceType == typeof(IDbContextFactory<ControlPlaneDbContext>)))
        {
            services.TryAddSingleton<IProviderSettingsRepository, EfProviderSettingsRepository>();
        }

        services.TryAddSingleton<IProviderSettingsStore>(sp => new ProviderSettingsStore(
            sp.GetService<IProviderSettingsRepository>(),
            sp.GetRequiredService<ITammaModeProvider>(),
            sp.GetRequiredService<ILogger<ProviderSettingsStore>>(),
            sp.GetService<TimeProvider>()));

        services.TryAddSingleton<IProviderModelCatalog>(sp => new ProviderModelCatalogService(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<Tamma.Activities.LlmCall.Credentials.IProviderCredentialResolver>(),
            sp.GetRequiredService<ILogger<ProviderModelCatalogService>>(),
            sp.GetService<TimeProvider>()));

        return services;
    }
}
