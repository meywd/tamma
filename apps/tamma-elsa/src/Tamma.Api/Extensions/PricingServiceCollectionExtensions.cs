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
}
