using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Secrets.Reveal;

namespace Tamma.Api.Tests.Secrets.Reveal;

/// <summary>
/// Tests for <see cref="RevealTokenSweeper"/>. The hosted service is
/// mostly a loop with a PeriodicTimer + scope resolution; the unit
/// test set focuses on the single-iteration helper
/// <c>RunOneSweepAsync</c> so timing does not make the suite flaky.
/// </summary>
[TestFixture]
public class RevealTokenSweeperTests
{
    [Test]
    public async Task RunOneSweep_DelegatesToService()
    {
        var service = new Mock<ISecretRevealService>();
        service.Setup(s => s.SweepExpiredAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var sp = BuildScope(service.Object);
        var sweeper = new RevealTokenSweeper(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RevealTokenSweeper>.Instance,
            TimeProvider.System);

        await sweeper.RunOneSweepAsync(CancellationToken.None);

        service.Verify(
            s => s.SweepExpiredAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task RunOneSweep_SwallowsServiceExceptions()
    {
        var service = new Mock<ISecretRevealService>();
        service.Setup(s => s.SweepExpiredAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var sp = BuildScope(service.Object);
        var sweeper = new RevealTokenSweeper(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RevealTokenSweeper>.Instance,
            TimeProvider.System);

        // Must not throw — sweeper is best-effort.
        Func<Task> act = () => sweeper.RunOneSweepAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Test]
    public void RunOneSweep_PropagatesOperationCanceled()
    {
        var service = new Mock<ISecretRevealService>();
        service.Setup(s => s.SweepExpiredAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var sp = BuildScope(service.Object);
        var sweeper = new RevealTokenSweeper(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RevealTokenSweeper>.Instance,
            TimeProvider.System);

        Func<Task> act = () => sweeper.RunOneSweepAsync(CancellationToken.None);
        act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static ServiceProvider BuildScope(ISecretRevealService service)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => service);
        return services.BuildServiceProvider();
    }
}
