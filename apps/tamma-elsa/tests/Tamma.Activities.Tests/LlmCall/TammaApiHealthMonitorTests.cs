using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.Tests.LlmCall;

/// <summary>
/// Wave C.4 §4 — unit tests for <see cref="TammaApiHealthMonitor"/>. The
/// monitor tracks every TammaApiClient call in a 5-min rolling window
/// and fires PLATFORM.API.UNHEALTHY exactly once per sustained-failure
/// episode.
///
/// <para>Determinism: tests drive time with a FakeTimeProvider so the
/// rolling window advances on demand without wall-clock reliance.</para>
/// </summary>
[TestFixture]
public class TammaApiHealthMonitorTests
{
    private sealed class RecordingAlertEmitter : IAlertEventEmitter
    {
        public List<PlatformApiUnhealthyEvent> Unhealthy { get; } = new();
        public Task EmitPlatformApiUnhealthyAsync(PlatformApiUnhealthyEvent evt, CancellationToken ct)
        { Unhealthy.Add(evt); return Task.CompletedTask; }

        public Task EmitBudgetExhaustedAsync(BudgetExhaustedEvent evt, CancellationToken ct) => Task.CompletedTask;
        public Task EmitAgentDispatchFailedAsync(AgentDispatchFailedEvent evt, CancellationToken ct) => Task.CompletedTask;
        public Task EmitWorkflowRetryExceededAsync(WorkflowRetryExceededEvent evt, CancellationToken ct) => Task.CompletedTask;
        public Task EmitSecretRotationFailedAsync(SecretRotationFailedEvent evt, CancellationToken ct) => Task.CompletedTask;
    }

    [Test]
    public async Task FewerThan10Requests_NoEmission()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-23T10:00:00Z"));
        var emitter = new RecordingAlertEmitter();
        var monitor = new TammaApiHealthMonitor(emitter, time);

        for (var i = 0; i < 9; i++)
            await monitor.RecordAsync(success: false, statusCode: 503, exceptionType: null, default);

        emitter.Unhealthy.Should().BeEmpty(
            "minimum 10 total requests before we call the API unhealthy");
    }

    [Test]
    public async Task FailureRateUnder50Percent_NoEmission()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-23T10:00:00Z"));
        var emitter = new RecordingAlertEmitter();
        var monitor = new TammaApiHealthMonitor(emitter, time);

        // 4 failures + 8 successes = 33% failure rate < 50%
        for (var i = 0; i < 4; i++)
            await monitor.RecordAsync(false, 503, null, default);
        for (var i = 0; i < 8; i++)
            await monitor.RecordAsync(true, 200, null, default);

        emitter.Unhealthy.Should().BeEmpty();
    }

    [Test]
    public async Task FailureRateOver50PercentWithEnoughRequests_EmitsOnce()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-23T10:00:00Z"));
        var emitter = new RecordingAlertEmitter();
        var monitor = new TammaApiHealthMonitor(emitter, time);

        // 6 failures + 4 successes = 60% failure rate, 10 total requests
        for (var i = 0; i < 6; i++)
            await monitor.RecordAsync(false, 503, null, default);
        for (var i = 0; i < 4; i++)
            await monitor.RecordAsync(true, 200, null, default);

        emitter.Unhealthy.Should().ContainSingle();
        var evt = emitter.Unhealthy[0];
        evt.WindowSeconds.Should().Be(300);
        evt.TotalRequests.Should().Be(10);
        evt.FailureCount.Should().Be(6);
        evt.FailureRate.Should().BeApproximately(0.6m, 0.01m);
        evt.TopFailureReasons.Should().ContainSingle(r => r.Reason == "503" && r.Count == 6);
    }

    [Test]
    public async Task SustainedFailures_EmitsOncePerFiveMinWindow()
    {
        var start = DateTimeOffset.Parse("2026-04-23T10:00:00Z");
        var time = new FakeTimeProvider(start);
        var emitter = new RecordingAlertEmitter();
        var monitor = new TammaApiHealthMonitor(emitter, time);

        // First burst — triggers emission
        for (var i = 0; i < 12; i++)
            await monitor.RecordAsync(false, 502, null, default);
        emitter.Unhealthy.Should().HaveCount(1);

        // Immediately follow with more failures — should NOT double-emit
        // because emitter-level dedup blocks within 5 min.
        for (var i = 0; i < 12; i++)
            await monitor.RecordAsync(false, 502, null, default);
        emitter.Unhealthy.Should().HaveCount(1,
            "emitter-level dedup keeps us quiet for 5 min after last fire");

        // Advance 5 min + 1 sec. Next failure burst should emit again.
        time.Advance(TimeSpan.FromSeconds(301));
        for (var i = 0; i < 12; i++)
            await monitor.RecordAsync(false, 502, null, default);
        emitter.Unhealthy.Should().HaveCount(2,
            "after the 5-min dedup window, a fresh failure burst re-emits");
    }

    [Test]
    public async Task OldRequests_DropFromWindow()
    {
        // Requests older than 5 minutes must NOT count toward the
        // totalRequests / failureCount calculation.
        var start = DateTimeOffset.Parse("2026-04-23T10:00:00Z");
        var time = new FakeTimeProvider(start);
        var emitter = new RecordingAlertEmitter();
        var monitor = new TammaApiHealthMonitor(emitter, time);

        // 10 successes at T+0
        for (var i = 0; i < 10; i++)
            await monitor.RecordAsync(true, 200, null, default);

        // Advance past the window
        time.Advance(TimeSpan.FromSeconds(301));

        // 6 failures + 4 successes at T+5min — only these should count
        for (var i = 0; i < 6; i++)
            await monitor.RecordAsync(false, 503, null, default);
        for (var i = 0; i < 4; i++)
            await monitor.RecordAsync(true, 200, null, default);

        emitter.Unhealthy.Should().ContainSingle();
        var evt = emitter.Unhealthy[0];
        evt.TotalRequests.Should().Be(10, "only new-window requests count");
        evt.FailureCount.Should().Be(6);
    }

    [Test]
    public async Task ExceptionsAlsoCountAsFailures()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-23T10:00:00Z"));
        var emitter = new RecordingAlertEmitter();
        var monitor = new TammaApiHealthMonitor(emitter, time);

        for (var i = 0; i < 6; i++)
            await monitor.RecordAsync(false, null, "HttpRequestException", default);
        for (var i = 0; i < 4; i++)
            await monitor.RecordAsync(true, 200, null, default);

        emitter.Unhealthy.Should().ContainSingle();
        var evt = emitter.Unhealthy[0];
        evt.FailureCount.Should().Be(6);
        evt.TopFailureReasons.Should().ContainSingle(r => r.Reason == "HttpRequestException");
    }

    [Test]
    public async Task TopFailureReasons_SortedByCountDesc()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-23T10:00:00Z"));
        var emitter = new RecordingAlertEmitter();
        var monitor = new TammaApiHealthMonitor(emitter, time);

        // 5 × 503, 2 × 502, 1 × HttpReqEx — all 3 reasons with mixed counts
        for (var i = 0; i < 5; i++) await monitor.RecordAsync(false, 503, null, default);
        for (var i = 0; i < 2; i++) await monitor.RecordAsync(false, 502, null, default);
        await monitor.RecordAsync(false, null, "HttpRequestException", default);
        for (var i = 0; i < 4; i++) await monitor.RecordAsync(true, 200, null, default);

        emitter.Unhealthy.Should().ContainSingle();
        var reasons = emitter.Unhealthy[0].TopFailureReasons;
        reasons[0].Reason.Should().Be("503");
        reasons[0].Count.Should().Be(5);
    }

    [Test]
    public async Task FourXxErrors_NotCountedAsFailures()
    {
        // 4xx is client-error — means Tamma-API is healthy but rejecting
        // a specific request. Don't count 400/401/403/404 toward the
        // "API unhealthy" failure rate.
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-23T10:00:00Z"));
        var emitter = new RecordingAlertEmitter();
        var monitor = new TammaApiHealthMonitor(emitter, time);

        for (var i = 0; i < 12; i++)
            await monitor.RecordAsync(false, 404, null, default);

        emitter.Unhealthy.Should().BeEmpty(
            "4xx responses don't indicate the platform API is unhealthy");
    }
}

/// <summary>
/// Minimal <see cref="TimeProvider"/> that only advances on
/// <see cref="Advance"/> calls — the platform-provided FakeTimeProvider
/// (Microsoft.Extensions.TimeProvider.Testing) lives in a different
/// package already used by AlertRuleEvaluator tests. Keep this one
/// local so we don't pull a test-only ref into Tamma.Activities.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;
    public FakeTimeProvider(DateTimeOffset start) => _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}
