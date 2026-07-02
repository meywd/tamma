using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Extensions;
using Tamma.Api.Services.Billing;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Secrets.Stopgap;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-5 (AC13) — mode-gated webhook DI. In SaaS the processor, registry,
/// default handlers, secret source, verifier, and follow-up task handler are
/// registered; in single-user NONE are (zero Stripe surface — the routes in
/// Program.cs are likewise unmapped).
/// </summary>
[TestFixture]
public class AddBillingWebhookIngestionTests
{
    private static ServiceProvider BuildProvider(string mode)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Tamma:Mode"] = mode })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ITammaModeProvider, TammaModeProvider>();
        services.TryAddSingletonDoubles();
        services.AddPlatformTaskWorker(configuration);
        // Story 35-4 supersedes 35-5's SubscriptionWebhookHandler with
        // SubscriptionMirrorWebhookHandler; register just its deps (the shared
        // mirror updater + subscription repo) so the SaaS handler set resolves.
        // (In Program.cs these come from AddTammaBilling, called before this.)
        services.AddScoped<IBillingSubscriptionRepository, BillingSubscriptionRepository>();
        services.AddScoped<SubscriptionMirrorUpdater>();
        services.AddBillingWebhookIngestion(configuration);
        return services.BuildServiceProvider();
    }

    [Test]
    public void Saas_Registers_Processor_Handlers_And_Followup()
    {
        using var sp = BuildProvider("saas");
        using var scope = sp.CreateScope();
        var s = scope.ServiceProvider;

        s.GetService<IStripeWebhookProcessor>().Should().NotBeNull();
        s.GetService<IBillingEventHandlerRegistry>().Should().NotBeNull();
        s.GetService<IStripeSigningSecretSource>().Should().NotBeNull();
        s.GetService<IStripeEventVerifier>().Should().NotBeNull();
        s.GetService<NullBillingEventHandler>().Should().NotBeNull();

        s.GetServices<IBillingEventHandler>().Select(h => h.GetType().Name)
            .Should().Contain(new[]
            {
                // Story 35-4 superseded SubscriptionWebhookHandler with the
                // mirror-writing SubscriptionMirrorWebhookHandler.
                "SubscriptionMirrorWebhookHandler", "InvoiceWebhookHandler",
                "PaymentWebhookHandler", "DisputeWebhookHandler",
            });
        s.GetServices<IPlatformTaskHandler>()
            .Should().Contain(h => h is BillingWebhookFollowupTaskHandler);
    }

    [Test]
    public void SingleUser_Registers_Nothing()
    {
        using var sp = BuildProvider("single-user");
        using var scope = sp.CreateScope();
        var s = scope.ServiceProvider;

        s.GetService<IStripeWebhookProcessor>().Should().BeNull("single-user has zero Stripe surface");
        s.GetService<IStripeSigningSecretSource>().Should().BeNull();
        s.GetServices<IBillingEventHandler>().Should().BeEmpty();
        s.GetServices<IPlatformTaskHandler>()
            .Should().NotContain(h => h is BillingWebhookFollowupTaskHandler);
    }
}

internal static class BillingWebhookTestServiceExtensions
{
    /// <summary>Register the lightweight doubles the SaaS processor graph needs to resolve.</summary>
    public static IServiceCollection TryAddSingletonDoubles(this IServiceCollection services)
    {
        services.AddDbContextFactory<ControlPlaneDbContext>(
            o => o.UseInMemoryDatabase($"webhook-di-{Guid.NewGuid():N}"));
        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>().CreateDbContext());
        services.AddScoped(_ => Moq.Mock.Of<IEventRepository>());
        services.AddScoped(_ => Moq.Mock.Of<IPlatformQueuedTaskRepository>());
        services.AddScoped(_ => Moq.Mock.Of<IRuntimeSecretResolver>());
        services.AddOptions<BillingOptions>();
        return services;
    }
}
