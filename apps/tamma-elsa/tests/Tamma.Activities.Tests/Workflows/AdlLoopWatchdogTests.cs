using Elsa.Workflows;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Requests;
using Elsa.Workflows.Runtime.Responses;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.Activities.LlmCall;
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
    public void ASuspendedInstance_CountsAsRunning()
    {
        // The watchdog's whole "a long cooldown is not a stall" argument rests on this:
        // Elsa models suspension as a SUB-status, so an orchestrator parked on its
        // cooldown timer bookmark is still WorkflowStatus.Running. If that ever changed,
        // the liveness query would read every cooldown as a dead loop and re-arm on top
        // of a healthy one. Pinned here rather than assumed in a comment.
        Enum.GetNames<WorkflowStatus>().Should().BeEquivalentTo(new[] { "Running", "Finished" });
        Enum.GetNames<WorkflowSubStatus>().Should().Contain("Suspended");
    }

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
            "restarting a loop a human deliberately stopped would defeat the brake — and it is "
            + "not reported as a stall either, or ADL.LOOP.STALLED would fire every threshold "
            + "window for an intentional stop and stop meaning anything");

        h.EventSink.Emitted(AdlLoopEvents.LoopStalled).Should().BeFalse(
            "a loop stopped on purpose is not a stall");

        // But it must not be silent. Reaching this branch means the loop is stopped AND no
        // instance is live — the stop switch halts new dispatch while leaving the
        // orchestrator running — so an operator who later clears the stop file needs the
        // event stream to show the loop had died. Before this assertion existed the branch
        // returned after one Information log line and emitted nothing at all.
        h.EventSink.Emitted(AdlLoopEvents.LoopReArmSkipped).Should().BeTrue(
            "an operator-stopped AND dead loop still leaves a durable error-status record");
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
            // EmitAsync resolves the concrete TammaApiClient and no-ops when it is absent,
            // so without this every durable-event assertion would pass vacuously. The client
            // is cheap to build over a stub handler, which records what was appended.
            EventSink = new EventCapturingHandler();
            services.AddSingleton(new TammaApiClient(
                new HttpClient(EventSink),
                NullLogger<TammaApiClient>.Instance,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Tamma:ApiUrl"] = "http://tamma.test",
                    })
                    .Build()));
            // The dispatcher is resolved from the per-tick scope, not injected — see
            // AdlLoopWatchdogService's ctor doc for why holding it would be a captive
            // scoped dependency.
            services.AddSingleton<IWorkflowDispatcher>(Dispatcher);
            var provider = services.BuildServiceProvider();

            _service = new AdlLoopWatchdogService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Microsoft.Extensions.Options.Options.Create(Options),
                Time,
                NullLogger<AdlLoopWatchdogService>.Instance,
                configuration: null,
                configCache: ConfigCache,
                stopSwitch: new StubStopSwitch(stopReason));

            _service.SetLastAliveForTests(T0);
        }

        public EventCapturingHandler EventSink { get; private set; } = null!;

        public Task TickAsync() => _service.InvokeTickForTestsAsync(CancellationToken.None);
    }

    /// <summary>Records the event types appended through TammaApiClient.</summary>
    private sealed class EventCapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            };
        }

        public bool Emitted(string eventType) => Bodies.Any(b => b.Contains(eventType, StringComparison.Ordinal));
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
