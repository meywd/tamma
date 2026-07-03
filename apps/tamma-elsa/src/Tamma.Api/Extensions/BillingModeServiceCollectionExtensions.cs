using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Billing;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Services.PromptStore;

namespace Tamma.Api.Extensions;

/// <summary>
/// Story 35-2 — mode-aware billing-mode-tagger DI. Registers
/// <see cref="IBillingModeTagger"/> as the real <see cref="BillingModeTagger"/>
/// in SaaS (reads the 34-3 owner + reconciles 32-3's credential source) and the
/// no-op <see cref="NullBillingModeTagger"/> in single-user (no billing
/// dimension) — the same Null-seam pattern Story 35-1 uses for
/// <c>NullBillingProvider</c>, so request handlers never branch on mode.
///
/// <para>Mode is read once from <see cref="ITammaModeProvider"/> at composition
/// time (process-stable). The owner reader
/// (<see cref="ITenantProviderBillingResolver"/>) is registered by
/// <c>AddUsagePricingEngine</c>; a <see cref="ServiceCollectionDescriptorExtensions.TryAddScoped{TService,TImplementation}"/>
/// here keeps it available even if that extension has not run yet.</para>
/// </summary>
public static class BillingModeServiceCollectionExtensions
{
    public static IServiceCollection AddBillingModeTagging(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The 34-3 owner reader (Reader A also uses it). Idempotent.
        services.TryAddScoped<ITenantProviderBillingResolver, TenantProviderBillingResolver>();

        var mode = ResolveMode(services, configuration);
        if (mode == TammaMode.SaaS)
        {
            services.TryAddScoped<IBillingModeTagger, BillingModeTagger>();
        }
        else
        {
            services.TryAddScoped<IBillingModeTagger, NullBillingModeTagger>();
        }

        return services;
    }

    private static TammaMode ResolveMode(
        IServiceCollection services, IConfiguration configuration)
    {
        var registered = services
            .FirstOrDefault(d => d.ServiceType == typeof(ITammaModeProvider))?
            .ImplementationInstance as ITammaModeProvider;
        return registered?.Mode ?? TammaModeProvider.Resolve(configuration);
    }
}
