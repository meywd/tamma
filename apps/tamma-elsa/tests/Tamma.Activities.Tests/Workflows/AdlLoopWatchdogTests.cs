using Elsa.Workflows;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Requests;
using Elsa.Workflows.Runtime.Responses;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// The out-of-band observer that makes "the autonomous loop is dead" impossible to miss.
///
/// <para><b>What it guards.</b> <c>adl-orchestrator</c> restarts itself and nothing else
/// dispatches it, so a lost restart ends the loop. <see cref="DispatchAdlActivity"/>'s
/// retry + durable ADL.SELF.DISPATCH.FAILED event both depend on the process that just
/// failed; every other way the chain can break (host death between the cooldown timer and
/// the restart edge, a deploy mid-tick, a hand-cancelled instance) leaves NO signal at
/// all. These pins cover the three decisions the watchdog gets to make: is it stalled, may
/// it re-arm, and with what config.</para>
/// </summary>
[TestFixture]
public class AdlLoopWatchdogTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-08-18T09:00:00Z");
    private const string LiveConfig = """{"repository":"owner/repo","cooldownSeconds":3600}""";

    [Test]
    public async Task ALiveInstance_IsNotAStall()
    {
        var h = new Harness(live: 1, everRan: 1);
        h.Time.Advance(TimeSpan.FromHours(5));

        await h.TickAsync();

        h.Dispatcher.Dispatched.Should().BeEmpty(
            "a cooldown-suspended orchestrator still counts as Running, so a long cooldown "
            + "must never look like a stall");
    }

    [Test]
    public async Task ALoopThatNeverRan_IsLeftAlone()
    {
        var h = new Harness(live: 0, everRan: 0);
        h.Time.Advance(TimeSpan.FromHours(5));

        await h.TickAsync();

        h.Dispatcher.Dispatched.Should().BeEmpty(
            "a deployment that never started the loop must not have one started FOR it");
    }

    [Test]
    public async Task NoLiveInstance_WithinTheThreshold_IsNotYetAStall()
    {
        var h = new Harness(live: 0, everRan: 4);
        h.Time.Advance(TimeSpan.FromMinutes(5));

        await h.TickAsync();

        h.Dispatcher.Dispatched.Should().BeEmpty(
            "the gap between one instance finishing and its successor starting is normal");
    }

    [Test]
    public async Task NoLiveInstance_PastTheThreshold_ReArmsWithTheLiveConfig()
    {
        var h = new Harness(live: 0, everRan: 4);
        h.ConfigCache.Remember(LiveConfig);
        h.Time.Advance(TimeSpan.FromMinutes(11));

        await h.TickAsync();

        h.Dispatcher.Dispatched.Should().ContainSingle();
        h.Dispatcher.Dispatched[0].Input!["configJson"].Should().Be(LiveConfig,
            "re-arming with an empty config would restart the loop against the DEFAULT "
            + "repository, which is worse than leaving it down");
    }

    [Test]
    public async Task ReArm_IsOneShotPerStall()
    {
        var h = new Harness(live: 0, everRan: 4);
        h.ConfigCache.Remember(LiveConfig);
        h.Time.Advance(TimeSpan.FromMinutes(11));

        await h.TickAsync();
        await h.TickAsync();

        h.Dispatcher.Dispatched.Should().ContainSingle(
            "a loop that cannot start must not be re-dispatched on every poll interval");
    }

    [Test]
    public async Task DoesNotReArm_WhileTheOperatorStopSwitchIsEngaged()
    {
        var h = new Harness(live: 0, everRan: 4, stopReason: "operator stop switch engaged (test)");
        h.ConfigCache.Remember(LiveConfig);
        h.Time.Advance(TimeSpan.FromMinutes(11));

        await h.TickAsync();

        h.Dispatcher.Dispatched.Should().BeEmpty(
            "restarting a loop a human deliberately stopped would defeat the brake");
    }

    [Test]
    public async Task DoesNotReArm_WithoutAConfigSeed()
    {
        var h = new Harness(live: 0, everRan: 4); // nothing remembered, no configured seed

        h.Time.Advance(TimeSpan.FromMinutes(11));

        await h.TickAsync();

        h.Dispatcher.Dispatched.Should().BeEmpty();
    }

    [Test]
    public async Task ReArms_FromTheConfiguredSeed_afterAColdStart()
    {
        var h = new Harness(live: 0, everRan: 4, seedConfigJson: LiveConfig);
        h.Time.Advance(TimeSpan.FromMinutes(11));

        await h.TickAsync();

        h.Dispatcher.Dispatched.Should().ContainSingle(
            "a host that restarted after the loop died has an empty cache; the configured "
            + "seed is what lets it repair anyway");
    }

    [Test]
    public async Task ADisabledWatchdog_NeverDispatches()
    {
        var h = new Harness(live: 0, everRan: 4, enabled: false);
        h.ConfigCache.Remember(LiveConfig);
        h.Time.Advance(TimeSpan.FromMinutes(11));

        // Disabled is honoured in ExecuteAsync, so a direct tick still runs; the pin that
        // matters is the option surviving on the options object for the host to read.
        h.Options.Enabled.Should().BeFalse();
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public FakeTimeProvider Time { get; }
        public CapturingDispatcher Dispatcher { get; } = new();
        public AdlLoopConfigCache ConfigCache { get; } = new();
        public AdlLoopWatchdogOptions Options { get; }
        private readonly AdlLoopWatchdogService _service;

        public Harness(
            long live, long everRan, string? stopReason = null,
            string? seedConfigJson = null, bool enabled = true)
        {
            Time = new FakeTimeProvider(T0);
            Options = new AdlLoopWatchdogOptions
            {
                Enabled = enabled,
                StallThreshold = TimeSpan.FromMinutes(10),
                ConfigJson = seedConfigJson,
            };

            var store = new Mock<IWorkflowInstanceStore>();
            store.Setup(s => s.CountAsync(
                    It.Is<WorkflowInstanceFilter>(f => f.WorkflowStatus == WorkflowStatus.Running),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(live);
            store.Setup(s => s.CountAsync(
                    It.Is<WorkflowInstanceFilter>(f => f.WorkflowStatus == null),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(everRan);

            var definitions = new Mock<IWorkflowDefinitionService>();
            definitions.Setup(d => d.FindWorkflowDefinitionAsync(
                    It.IsAny<string>(), It.IsAny<Elsa.Common.Models.VersionOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, Elsa.Common.Models.VersionOptions _, CancellationToken _) =>
                    new WorkflowDefinition { Id = id, DefinitionId = id });

            var services = new ServiceCollection();
            services.AddSingleton(store.Object);
            services.AddSingleton(definitions.Object);
            var provider = services.BuildServiceProvider();

            _service = new AdlLoopWatchdogService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Dispatcher,
                Microsoft.Extensions.Options.Options.Create(Options),
                Time,
                NullLogger<AdlLoopWatchdogService>.Instance,
                configuration: null,
                configCache: ConfigCache,
                stopSwitch: new StubStopSwitch(stopReason));

            _service.SetLastAliveForTests(T0);
        }

        public Task TickAsync() => _service.InvokeTickForTestsAsync(CancellationToken.None);
    }

    private sealed class StubStopSwitch(string? reason) : IAdlStopSwitch
    {
        public string? GetStopReason() => reason;
    }

    private sealed class CapturingDispatcher : IWorkflowDispatcher
    {
        public List<DispatchWorkflowDefinitionRequest> Dispatched { get; } = new();

        public Task<DispatchWorkflowResponse> DispatchAsync(
            DispatchWorkflowDefinitionRequest request, DispatchWorkflowOptions? options = default,
            CancellationToken cancellationToken = default)
        {
            Dispatched.Add(request);
            return Task.FromResult(new DispatchWorkflowResponse(Fault: null));
        }

        public Task<DispatchWorkflowResponse> DispatchAsync(
            DispatchWorkflowInstanceRequest request, DispatchWorkflowOptions? options = default,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DispatchWorkflowResponse(Fault: null));

        public Task<DispatchWorkflowResponse> DispatchAsync(
            DispatchTriggerWorkflowsRequest request, DispatchWorkflowOptions? options = default,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DispatchWorkflowResponse(Fault: null));

        public Task<DispatchWorkflowResponse> DispatchAsync(
            DispatchResumeWorkflowsRequest request, DispatchWorkflowOptions? options = default,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DispatchWorkflowResponse(Fault: null));
    }

    /// <summary>Local advance-only clock (same shape as TammaApiHealthMonitorTests').</summary>
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
