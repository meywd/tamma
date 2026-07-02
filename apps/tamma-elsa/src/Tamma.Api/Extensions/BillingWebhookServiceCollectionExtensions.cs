using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Billing;
using Tamma.Api.Services.Billing.Handlers;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Api.Services.PromptStore;

namespace Tamma.Api.Extensions;

/// <summary>
/// Story 35-5 — mode-gated DI for the Stripe webhook ingestion pipeline. Wired
/// only when <see cref="ITammaModeProvider.Mode"/> is <c>SaaS</c> (single-user's
/// <c>NullBillingProvider</c> means zero Stripe surface — 35-1 AC7 / 35-5 AC13).
///
/// <para>Registers the processor, the handler registry + the four default
/// DCB-emitting handlers, the <see cref="NullBillingEventHandler"/> fallthrough,
/// the signing-secret source, the Stripe event verifier, and the fast-ack
/// follow-up <see cref="IPlatformTaskHandler"/>. Mirrors
/// <c>PlatformTaskServiceCollectionExtensions</c>.</para>
/// </summary>
public static class BillingWebhookServiceCollectionExtensions
{
    /// <summary>Register the webhook pipeline when the process runs in SaaS mode.</summary>
    public static IServiceCollection AddBillingWebhookIngestion(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var mode = ResolveMode(services, configuration);
        if (mode != TammaMode.SaaS)
        {
            // Single-user: no Stripe surface. Route mapping is skipped in Program.cs.
            return services;
        }

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);

        // Signing-secret source + verifier are stateless singletons.
        services.TryAddSingleton<IStripeSigningSecretSource, CabinetStripeSigningSecretSource>();
        services.TryAddSingleton<IStripeEventVerifier, StripeEventVerifier>();

        // The processor + registry are Scoped (they take ControlPlaneDbContext +
        // scoped repositories), mirroring PlatformTaskHandlerRegistry.
        services.TryAddScoped<IBillingEventHandlerRegistry, BillingEventHandlerRegistry>();
        services.TryAddScoped<IStripeWebhookProcessor, StripeWebhookProcessor>();

        // The fallthrough handler is resolved directly by the processor (NOT via
        // the IEnumerable<IBillingEventHandler> registry set), so register the
        // concrete type.
        services.TryAddScoped<NullBillingEventHandler>();

        // 35-5's default DCB-emitting handlers. Sibling stories add their own via
        // AddBillingEventHandler<T>(); duplicate-claim detection supersedes these
        // cleanly when 35-4/35-7/35-8 land.
        //
        // Story 35-4 SUPERSEDES the audit-only SubscriptionWebhookHandler with
        // SubscriptionMirrorWebhookHandler, which drives the shared
        // SubscriptionMirrorUpdater (mirror + Tenant.Plan lockstep + the
        // BILLING.SUBSCRIPTION.* DCB event). Its dependency graph is registered by
        // AddTammaBilling, which Program.cs calls before this. Registry
        // duplicate-claim detection forbids BOTH claiming the subscription types, so
        // the 35-5 handler is no longer registered (its class stays for its own unit
        // tests).
        services.AddBillingEventHandler<SubscriptionMirrorWebhookHandler>();
        services.AddBillingEventHandler<InvoiceWebhookHandler>();
        services.AddBillingEventHandler<PaymentWebhookHandler>();
        services.AddBillingEventHandler<DisputeWebhookHandler>();

        // Fast-ack follow-up worker task handler (AC10).
        services.AddPlatformTaskHandler<BillingWebhookFollowupTaskHandler>();

        return services;
    }

    /// <summary>Register one <see cref="IBillingEventHandler"/> (scoped).</summary>
    public static IServiceCollection AddBillingEventHandler<THandler>(
        this IServiceCollection services)
        where THandler : class, IBillingEventHandler
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IBillingEventHandler, THandler>();
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
