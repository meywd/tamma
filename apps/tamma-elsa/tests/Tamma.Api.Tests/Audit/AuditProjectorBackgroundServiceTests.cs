using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Audit;

namespace Tamma.Api.Tests.Audit;

/// <summary>
/// Story 37-1 (AC9) — background-host gating + crash-isolation without a DB.
/// The RunOnStartup gate keeps the loop idle during unrelated tests; the loop
/// must not throw out of <c>StartAsync</c>.
/// </summary>
[TestFixture]
public class AuditProjectorBackgroundServiceTests
{
    [Test]
    public void RunOnStartup_Defaults_To_False()
    {
        // Critical — the loop must be opt-in so it never runs during the test
        // suite or a deployment that has not enabled it.
        new AuditProjectorOptions().RunOnStartup.Should().BeFalse();
    }

    [Test]
    public async Task Gated_Off_Loop_Does_Not_Touch_Services_On_Start()
    {
        // An empty service provider would throw if the loop tried to resolve the
        // ControlPlaneDbContext — proving the gate short-circuits before any work.
        var emptyServices = new ServiceCollection().BuildServiceProvider();
        var svc = new AuditProjectorBackgroundService(
            emptyServices,
            new AuditProjectorOptions { RunOnStartup = false },
            TimeProvider.System,
            new AuditProjectionMetrics(),
            NullLogger<AuditProjectorBackgroundService>.Instance);

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);   // must not throw — loop is gated off
        await svc.StopAsync(cts.Token);
    }

    [Test]
    public void Metrics_RecordLag_Clamps_Negative_To_Zero()
    {
        using var metrics = new AuditProjectionMetrics();
        metrics.RecordLag(42);
        metrics.Lag.Should().Be(42);
        metrics.RecordLag(-5);
        metrics.Lag.Should().Be(0, "lag is never negative");
    }
}
