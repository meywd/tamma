using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Services.Provisioning;

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

    /// <summary>
    /// Story 34-6 — the entitlement &amp; quota resolution service + its
    /// per-tenant snapshot cache, gauge-metric usage reader, and event-driven
    /// cache-invalidation listener.
    ///
    /// <list type="bullet">
    ///   <item><description><see cref="IEntitlementSnapshotCache"/> — singleton
    ///     (one cache shared across requests + the invalidation listener).</description></item>
    ///   <item><description><see cref="IEntitlementService"/>,
    ///     <see cref="IActivePlanAssignmentSource"/>,
    ///     <see cref="IEntitlementUsageReader"/> — scoped (depend on the scoped
    ///     <c>ControlPlaneDbContext</c> / catalog service).</description></item>
    ///   <item><description><see cref="EntitlementCacheInvalidationListener"/> —
    ///     hosted service subscribing the in-process event bus.</description></item>
    /// </list>
    ///
    /// <para><b>34-4 wired:</b> the <see cref="IActivePlanAssignmentSource"/> now
    /// reads the tenant's active <c>TenantPlanAssignment</c> row via
    /// <see cref="PlanAssignmentActivePlanSource"/> (which delegates to
    /// <see cref="IPlanAssignmentService"/>, registered by
    /// <see cref="AddPlanAssignment"/>). This is the one-line swap Story 34-6
    /// anticipated — it replaces the interim
    /// <see cref="TenantShadowColumnPlanAssignmentSource"/> behind the same seam
    /// with no entitlement-resolver change, and now supplies the pinned
    /// <c>PlanVersion</c> directly.</para>
    /// </summary>
    public static IServiceCollection AddEntitlementResolution(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // One shared cache across requests + the invalidation listener.
        services.TryAddSingleton<IEntitlementSnapshotCache>(sp =>
            new EntitlementSnapshotCache(sp.GetRequiredService<TimeProvider>()));

        // Story 34-4 — read the pinned (PlanId, PlanVersion) from the real
        // assignment table. Ensure IPlanAssignmentService is registered
        // (AddPlanAssignment); called independently from Program.cs.
        services.TryAddScoped<IActivePlanAssignmentSource, PlanAssignmentActivePlanSource>();
        services.TryAddScoped<IEntitlementUsageReader, ControlPlaneEntitlementUsageReader>();
        services.TryAddScoped<IEntitlementService, EntitlementService>();

        services.AddHostedService<EntitlementCacheInvalidationListener>();

        return services;
    }

    /// <summary>
    /// Story 34-4 — the version-pinned plan-assignment service + its usage seam
    /// and the platform-queue boundary-activation handler.
    ///
    /// <list type="bullet">
    ///   <item><description><see cref="IPlanAssignmentService"/> — scoped
    ///     (depends on the scoped <c>ControlPlaneDbContext</c> + catalog).</description></item>
    ///   <item><description><see cref="ITenantUsageReader"/> —
    ///     <see cref="NullTenantUsageReader"/> default (Epic 35 supplies the real
    ///     metering reader behind this same seam; keeps Billing out of the
    ///     assignment path).</description></item>
    ///   <item><description><see cref="ActivateScheduledPlanTaskHandler"/> —
    ///     platform-task handler promoting a due scheduled assignment at its
    ///     boundary (requires <c>AddPlatformTaskWorker</c>).</description></item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddPlanAssignment(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        services.TryAddScoped<ITenantUsageReader, NullTenantUsageReader>();
        services.TryAddScoped<IPlanAssignmentService, PlanAssignmentService>();

        // Boundary-activation platform-task handler (period-end cancel → free).
        services.AddPlatformTaskHandler<ActivateScheduledPlanTaskHandler>();

        return services;
    }
}
