using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Billing;

namespace Tamma.Api.Tests.Billing;

/// <summary>
/// Story 35-1 (AC7, AC9) — <see cref="SeedBillingCommand"/> arg matching and the
/// single-user no-op (prints SaaS-only, exits 0, makes no Stripe call).
/// </summary>
[TestFixture]
public class SeedBillingCommandTests
{
    [TestCase("seed-billing", true)]
    [TestCase("SEED-BILLING", true)]
    [TestCase("migrate-secrets", false)]
    [TestCase("", false)]
    public void ShouldRun_Matches_First_Arg(string arg, bool expected)
    {
        var args = string.IsNullOrEmpty(arg) ? Array.Empty<string>() : new[] { arg };
        SeedBillingCommand.ShouldRun(args).Should().Be(expected);
    }

    [Test]
    public void ShouldRun_Empty_Args_Is_False()
    {
        SeedBillingCommand.ShouldRun(Array.Empty<string>()).Should().BeFalse();
    }

    [Test]
    public async Task RunAsync_SingleUser_Is_NoOp_Exit0()
    {
        var billing = new Mock<IBillingProvider>();
        billing.SetupGet(b => b.IsEnabled).Returns(false);

        var services = new ServiceCollection();
        services.AddScoped(_ => billing.Object);
        await using var sp = services.BuildServiceProvider();

        var exit = await SeedBillingCommand.RunAsync(sp);

        exit.Should().Be(0);
        billing.Verify(b => b.SyncCatalogAsync(It.IsAny<CancellationToken>()), Times.Never,
            "single-user makes no Stripe catalog call");
    }

    [Test]
    public async Task RunAsync_Saas_Runs_Sync_Exit0()
    {
        var billing = new Mock<IBillingProvider>();
        billing.SetupGet(b => b.IsEnabled).Returns(true);
        billing.Setup(b => b.SyncCatalogAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CatalogSyncResult(new[]
            {
                new CatalogSlugResult("free", 4, 0),
            }));

        var services = new ServiceCollection();
        services.AddScoped(_ => billing.Object);
        await using var sp = services.BuildServiceProvider();

        var exit = await SeedBillingCommand.RunAsync(sp);

        exit.Should().Be(0);
        billing.Verify(b => b.SyncCatalogAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task RunAsync_Saas_Failure_Exit1()
    {
        var billing = new Mock<IBillingProvider>();
        billing.SetupGet(b => b.IsEnabled).Returns(true);
        billing.Setup(b => b.SyncCatalogAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("stripe boom"));

        var services = new ServiceCollection();
        services.AddScoped(_ => billing.Object);
        await using var sp = services.BuildServiceProvider();

        var exit = await SeedBillingCommand.RunAsync(sp);

        exit.Should().Be(1);
    }
}
