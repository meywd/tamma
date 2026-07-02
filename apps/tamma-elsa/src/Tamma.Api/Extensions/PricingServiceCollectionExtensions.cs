using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Pricing;

namespace Tamma.Api.Extensions;

/// <summary>
/// Story 34-1 — DI registration for the plan price-book catalog. Single
/// entry-point so <c>Program.cs</c> wires it with one call. Both services are
/// scoped (they depend on the scoped <c>ControlPlaneDbContext</c>) and are
/// registered behind their interfaces — the read service as
/// <see cref="IPlanCatalogService"/> and the version editor as
/// <see cref="IPlanVersionEditor"/> (Story 34-2 resolves the latter from the
/// admin write endpoint).
/// </summary>
public static class PricingServiceCollectionExtensions
{
    public static IServiceCollection AddPlanCatalog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // TimeProvider is registered by Program.cs already; fall back to the
        // system provider for tests that haven't staged one.
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddScoped<IPlanCatalogService, PlanCatalogService>();
        services.TryAddScoped<IPlanVersionEditor, PlanVersionEditor>();

        return services;
    }

    /// <summary>
    /// Story 34-5 — the cost->price markup engine + its policy/mode resolvers.
    /// The engine is a pure singleton (depends only on the singleton
    /// <c>IProviderPricingService</c>); the resolvers are scoped (they read the
    /// scoped <c>ControlPlaneDbContext</c>). The
    /// <see cref="ITenantProviderPricingModeResolver"/> default reads the
    /// per-tenant <c>BillingCustomer.BillingMode</c> until Story 34-3 swaps a
    /// per-<c>(tenant, provider)</c> implementation behind the same seam.
    /// </summary>
    public static IServiceCollection AddUsagePricingEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IUsagePricingEngine, UsagePricingEngine>();
        services.TryAddScoped<IMarginPolicyResolver, MarginPolicyResolver>();
        services.TryAddScoped<ITenantProviderPricingModeResolver, BillingCustomerPricingModeResolver>();

        return services;
    }
}
