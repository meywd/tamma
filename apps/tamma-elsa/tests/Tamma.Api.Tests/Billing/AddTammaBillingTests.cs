using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Tamma.Api.Extensions;
using Tamma.Api.Services.Billing;
using Tamma.Api.Services.Billing.Tasks;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Secrets.Stopgap;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-1 (AC9, AC11) — mode-aware DI. SaaS config resolves
/// <see cref="StripeBillingProvider"/>; single-user resolves
/// <see cref="NullBillingProvider"/>. The retry handler is registered in both.
/// </summary>
[TestFixture]
public class AddTammaBillingTests
{
    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tamma.Api";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static ServiceProvider BuildProvider(IDictionary<string, string?> config)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment());
        services.AddSingleton<ITammaModeProvider, TammaModeProvider>();

        // Billing depends on a CP context + event repo + secret resolver for the
        // SaaS provider; register lightweight in-memory / mock doubles so the
        // graph resolves.
        services.AddDbContextFactory<ControlPlaneDbContext>(
            o => o.UseInMemoryDatabase($"billing-di-{Guid.NewGuid():N}"));
        services.AddScoped(sp =>
            sp.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>().CreateDbContext());
        services.AddScoped(_ => Moq.Mock.Of<IEventRepository>());
        services.AddScoped(_ => Moq.Mock.Of<IRuntimeSecretResolver>());
        services.AddPlatformTaskWorker(configuration);

        services.AddTammaBilling(configuration);
        return services.BuildServiceProvider();
    }

    [Test]
    public void Saas_Config_Resolves_StripeBillingProvider()
    {
        using var sp = BuildProvider(new Dictionary<string, string?>
        {
            ["Tamma:Mode"] = "saas",
        });
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetRequiredService<IBillingProvider>()
            .Should().BeOfType<StripeBillingProvider>();
    }

    [Test]
    public void SingleUser_Config_Resolves_NullBillingProvider()
    {
        using var sp = BuildProvider(new Dictionary<string, string?>
        {
            ["Tamma:Mode"] = "single-user",
        });
        using var scope = sp.CreateScope();

        var provider = scope.ServiceProvider.GetRequiredService<IBillingProvider>();
        provider.Should().BeOfType<NullBillingProvider>();
        provider.IsEnabled.Should().BeFalse();
    }

    [Test]
    public void Retry_Handler_Is_Registered()
    {
        using var sp = BuildProvider(new Dictionary<string, string?>
        {
            ["Tamma:Mode"] = "saas",
        });
        using var scope = sp.CreateScope();

        var handlers = scope.ServiceProvider.GetServices<IPlatformTaskHandler>();
        handlers.Should().Contain(h => h is CreateBillingCustomerTaskHandler);
    }

    [Test]
    public void Catalog_And_Factory_Are_Registered()
    {
        using var sp = BuildProvider(new Dictionary<string, string?>
        {
            ["Tamma:Mode"] = "saas",
        });
        using var scope = sp.CreateScope();

        scope.ServiceProvider.GetService<IBillingCatalog>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IStripeServicesFactory>().Should().NotBeNull();
    }
}
