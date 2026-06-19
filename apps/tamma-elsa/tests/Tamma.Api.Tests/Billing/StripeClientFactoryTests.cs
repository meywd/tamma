using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Billing;
using Tamma.Api.Services.Secrets.Stopgap;
using Tamma.Core;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-1 (AC5, AC15) — <see cref="StripeClientFactory"/> resolves the
/// Stripe key from the Epic 29 cabinet; in production a missing key is a hard
/// boot error (fail-fast), while development tolerates it (so local dev can run
/// without a Stripe account). The key value is never logged.
/// </summary>
[TestFixture]
public class StripeClientFactoryTests
{
    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Tamma.Api";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static StripeClientFactory Build(string? resolvedKey, string environment)
    {
        var resolver = new Mock<IRuntimeSecretResolver>();
        resolver.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedKey);

        var sp = new ServiceCollection()
            .AddSingleton(resolver.Object)
            .BuildServiceProvider();

        return new StripeClientFactory(
            sp,
            Options.Create(new BillingOptions()),
            new FakeEnv { EnvironmentName = environment },
            NullLogger<StripeClientFactory>.Instance);
    }

    [Test]
    public async Task CreateAsync_Resolves_Key_And_Builds_Services()
    {
        var factory = Build("sk_test_fake", "Production");

        var services = await factory.CreateAsync();

        services.Should().NotBeNull();
        services.Customers.Should().NotBeNull();
        services.Meters.Should().NotBeNull();
    }

    [Test]
    public async Task CreateAsync_Production_Missing_Key_Fails_Fast()
    {
        var factory = Build(null, "Production");

        var act = async () => await factory.CreateAsync();

        var ex = await act.Should().ThrowAsync<TammaError>();
        ex.Which.Code.Should().Be("BILLING.STRIPE.NO_KEY");
    }

    [Test]
    public async Task CreateAsync_Development_Missing_Key_Does_Not_Throw()
    {
        var factory = Build(null, "Development");

        // Development tolerates a missing key (returns a client that will fail
        // on the first real SDK call — but boot is not blocked).
        var services = await factory.CreateAsync();
        services.Should().NotBeNull();
    }
}
