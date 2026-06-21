using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Providers;

namespace Tamma.Api.Extensions;

/// <summary>
/// Story 34-11 — DI wiring for the DB-backed provider COST price-book. Swaps the
/// frozen <see cref="ProviderPricingService"/> for
/// <see cref="DbProviderPricingService"/> behind the unchanged
/// <see cref="IProviderPricingService"/> seam (a one-line registration change;
/// zero downstream consumer edits), and registers the EffectiveFrom-windowed
/// <see cref="IProviderCostResolver"/>.
///
/// <para>Call this AFTER <c>AddProviderSessionServices</c> (which registers the
/// frozen impl via <c>TryAddSingleton</c>) so this explicit registration wins:
/// it removes the frozen <see cref="IProviderPricingService"/> descriptor and
/// re-adds the DB-backed one. The frozen class is still resolvable on its own
/// concrete type as the seed source / boot fallback.</para>
/// </summary>
public static class ProviderPricingServiceCollectionExtensions
{
    public static IServiceCollection AddDbProviderPricing(this IServiceCollection services)
    {
        // Keep the frozen impl available on its concrete type — it is the
        // deterministic seed source + DbProviderPricingService's boot fallback.
        services.TryAddSingleton<ProviderPricingService>();

        // The EffectiveFrom-windowed resolver (used by the metering path). Register
        // the concrete singleton ONCE, then expose it on both interfaces so the
        // SAME instance is the cost resolver AND a cost-cache invalidator (C1).
        services.TryAddSingleton<ProviderCostResolver>();
        services.TryAddSingleton<IProviderCostResolver>(
            sp => sp.GetRequiredService<ProviderCostResolver>());

        // THE SWAP: replace any prior IProviderPricingService registration
        // (TryAddSingleton from AddProviderSessionServices) with the DB-backed
        // impl. The interface contract is unchanged. Register the concrete
        // singleton ONCE and project it onto IProviderPricingService so the SAME
        // live instance is what an admin write invalidates (C1) — registering a
        // second factory would invalidate a DIFFERENT instance than the one
        // serving Compute.
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(IProviderPricingService));
        if (existing is not null)
        {
            services.Remove(existing);
        }
        services.AddSingleton<DbProviderPricingService>(sp =>
            new DbProviderPricingService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<IProviderCostResolver>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DbProviderPricingService>>(),
                sp.GetRequiredService<ProviderPricingService>()));
        services.AddSingleton<IProviderPricingService>(
            sp => sp.GetRequiredService<DbProviderPricingService>());

        // C1 — both snapshot holders are cost-cache invalidators. The admin
        // endpoints resolve IEnumerable<IProviderCostCacheInvalidator> and flush
        // EVERY snapshot on a pricing/eligibility mutation, so a live Compute is
        // never stale after a re-price.
        services.AddSingleton<IProviderCostCacheInvalidator>(
            sp => sp.GetRequiredService<ProviderCostResolver>());
        services.AddSingleton<IProviderCostCacheInvalidator>(
            sp => sp.GetRequiredService<DbProviderPricingService>());

        return services;
    }
}
