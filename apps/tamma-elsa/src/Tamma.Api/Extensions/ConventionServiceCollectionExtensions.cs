using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Conventions;

namespace Tamma.Api.Extensions;

/// <summary>
/// DI registration helpers for the convention template service layer.
/// </summary>
public static class ConventionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IConventionTemplateService"/> as a singleton backed
    /// by the shipped static template data, and the Story 27-16
    /// <see cref="ConventionStoreSeeder"/> hosted service that seeds the
    /// <c>conventions</c> system-default rows (<c>tenant_id IS NULL</c>) on
    /// startup. Safe to call multiple times.
    /// </summary>
    public static IServiceCollection AddConventionServices(this IServiceCollection services)
    {
        services.AddSingleton<IConventionTemplateService, ConventionTemplateService>();

        // Story 27-9 — convention store service. Scoped (mirrors
        // PromptStoreService) because it leans on the tenant-scoped
        // IConventionRepository which resolves the per-request tenant DbContext.
        // Assumes AddTammaData() has registered IConventionRepository.
        services.AddScoped<IConventionStore, ConventionStore>();

        // TimeProvider is registered by Program.cs already; fall back to the
        // system provider for compositions that haven't staged one.
        services.TryAddSingleton(TimeProvider.System);

        // Story 27-16 — seed the convention system defaults on startup. Tests
        // can pre-stage ConventionStoreSeederOptions { RunOnStartup = false }
        // to skip the per-factory DB round-trip.
        services.TryAddSingleton<ConventionStoreSeederOptions>();
        services.AddHostedService<ConventionStoreSeeder>();
        return services;
    }
}
