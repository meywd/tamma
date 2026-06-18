using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Billing;
using Tamma.Api.Services.Billing.Tasks;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Api.Services.PromptStore;

namespace Tamma.Api.Extensions;

/// <summary>
/// Story 35-1 — mode-aware billing DI. Registers <see cref="IBillingProvider"/>
/// as <see cref="StripeBillingProvider"/> in SaaS and
/// <see cref="NullBillingProvider"/> in single-user, plus the catalog reader,
/// Stripe client factory, options, and the customer-create retry handler.
///
/// <para>Mode is read once from <see cref="ITammaModeProvider"/> at composition
/// time (process-stable), so the binding never varies per request. In
/// single-user the Stripe-touching services are still registered but
/// unreachable — the no-op provider short-circuits before any of them is used.</para>
/// </summary>
public static class BillingServiceCollectionExtensions
{
    public static IServiceCollection AddTammaBilling(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<BillingOptions>()
            .Configure(opts =>
                configuration.GetSection(BillingOptions.SectionName).Bind(opts));

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);

        // Catalog reader + Stripe client factory are mode-agnostic singletons
        // (the catalog is platform-global; the factory caches the resolved key).
        services.TryAddSingleton<IBillingCatalog, BillingCatalog>();
        services.TryAddScoped<IStripeServicesFactory, StripeClientFactory>();

        // Mode-gated provider selection. Resolve the mode from the registered
        // ITammaModeProvider so detection stays in one place.
        var mode = ResolveMode(services, configuration);
        if (mode == TammaMode.SaaS)
        {
            services.AddScoped<IBillingProvider, StripeBillingProvider>();
        }
        else
        {
            services.AddScoped<IBillingProvider, NullBillingProvider>();
        }

        // Retry handler for the non-blocking tenant-create hook. Registered in
        // both modes — in single-user it dead-letters cleanly (handler guards on
        // IsEnabled) but the hook never enqueues a task there anyway.
        services.AddPlatformTaskHandler<CreateBillingCustomerTaskHandler>();

        return services;
    }

    private static TammaMode ResolveMode(
        IServiceCollection services, IConfiguration configuration)
    {
        // Prefer an already-registered provider so a test/host override wins;
        // fall back to the pure config-driven resolver otherwise.
        var registered = services
            .FirstOrDefault(d => d.ServiceType == typeof(ITammaModeProvider))?
            .ImplementationInstance as ITammaModeProvider;
        return registered?.Mode ?? TammaModeProvider.Resolve(configuration);
    }
}
