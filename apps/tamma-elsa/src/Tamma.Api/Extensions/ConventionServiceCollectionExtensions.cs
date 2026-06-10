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
    /// by the shipped static template data, the Story 27-16
    /// <see cref="ConventionStoreSeeder"/> hosted service that seeds the
    /// <c>conventions</c> system-default rows (<c>tenant_id IS NULL</c>) on
    /// startup, and <see cref="ConventionEventsService"/> for DCB audit events.
    ///
    /// <para>Safe to call multiple times — all registrations use <c>TryAdd*</c>
    /// or an explicit hosted-service guard so a second call is a no-op rather
    /// than registering duplicate services.</para>
    /// </summary>
    public static IServiceCollection AddConventionServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IConventionTemplateService, ConventionTemplateService>();

        // Story 27-9 — convention store service. Scoped (mirrors
        // PromptStoreService) because it leans on the tenant-scoped
        // IConventionRepository which resolves the per-request tenant DbContext.
        // Assumes AddTammaData() has registered IConventionRepository.
        services.TryAddScoped<IConventionStore, ConventionStore>();

        // DCB audit events for convention mutations (backend review I-1).
        // Scoped to match IConventionStore lifetime (both need the per-request
        // IEventRepository which is scoped).
        services.TryAddScoped<ConventionEventsService>();

        // TimeProvider is registered by Program.cs already; fall back to the
        // system provider for compositions that haven't staged one.
        services.TryAddSingleton(TimeProvider.System);

        // Story 27-16 — seed the convention system defaults on startup. Tests
        // can pre-stage ConventionStoreSeederOptions { RunOnStartup = false }
        // to skip the per-factory DB round-trip.
        services.TryAddSingleton<ConventionStoreSeederOptions>();

        // Guard: add the hosted service only once — AddHostedService is NOT
        // idempotent (each call enqueues another instance). TryAdd on
        // IHostedService alone isn't sufficient (it is registered multiple times
        // by the framework itself), so we discriminate on ImplementationType.
        if (!services.Any(d =>
                d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                && d.ImplementationType == typeof(ConventionStoreSeeder)))
        {
            services.AddHostedService<ConventionStoreSeeder>();
        }

        return services;
    }
}
