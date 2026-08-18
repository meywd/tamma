using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Tamma.Activities.ADL;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// <see cref="CheckLimitsActivity"/> is the loop's only gate on starting new work, so the
/// STOP paths are what matter: an operator brake that does not brake, or a spend ceiling
/// that does not cap, both look exactly like a healthy loop from outside. These run the
/// real activity through a real <see cref="IWorkflowRunner"/> and read the taken edge off
/// the workflow output surface.
///
/// <para>Stop is deliberately not fatal — the orchestrator's terminal paths all funnel
/// into <c>cooldown → DispatchAdl</c>, so a Stop skips ONE tick and the loop restarts.
/// That is what makes the fail-closed spend posture safe.</para>
/// </summary>
[TestFixture]
public class CheckLimitsGateTests
{
    [Test]
    public async Task Continues_WhenNothingIsTripped()
    {
        var output = await RunAsync(activeInstances: 0, config: new Dictionary<string, string?>());

        output["outcome"].Should().Be("continue");
        output["stopReason"].Should().Be("");
    }

    [Test]
    public async Task Stops_WhenTheOperatorStopSwitchIsEngaged()
    {
        var output = await RunAsync(activeInstances: 0, config: new Dictionary<string, string?>
        {
            [ConfigAdlStopSwitch.StoppedKey] = "true",
        });

        output["outcome"].Should().Be("stop", "the operator brake must halt NEW dispatch");
        output["stopReason"].ToString().Should().Contain(ConfigAdlStopSwitch.StoppedKey,
            "the reason is the audit record of why the loop went quiet");
    }

    [Test]
    public async Task TheStopSwitchWins_OverAnOtherwiseHealthyTick()
    {
        // Checked FIRST and before any I/O, so pulling the brake takes effect on the very
        // next tick even while the budget API is unreachable.
        var output = await RunAsync(activeInstances: 0, config: new Dictionary<string, string?>
        {
            [ConfigAdlStopSwitch.StoppedKey] = "true",
            [AdlSpendCeiling.MaxSpendKey] = "1000",
        });

        output["outcome"].Should().Be("stop");
    }

    [Test]
    public async Task Stops_OnThePerInstanceEmergencyFlag()
    {
        var output = await RunAsync(
            activeInstances: 0, config: new Dictionary<string, string?>(), emergencyStop: true);

        output["outcome"].Should().Be("stop");
        output["stopReason"].ToString().Should().Contain("Emergency");
    }

    [Test]
    public async Task Stops_WhenConcurrencyIsSaturated()
    {
        var output = await RunAsync(activeInstances: 1, config: new Dictionary<string, string?>());

        output["outcome"].Should().Be("stop");
        output["stopReason"].ToString().Should().Contain("Max concurrent");
    }

    [Test]
    public async Task Stops_WhenACeilingIsConfiguredButTheSpendCannotBeRead()
    {
        // No TammaApiClient is registered in this harness, so the spend is unreadable.
        // An operator who set a cap must not be silently uncapped by an outage.
        var output = await RunAsync(activeInstances: 0, config: new Dictionary<string, string?>
        {
            [AdlSpendCeiling.MaxSpendKey] = "25",
            [AdlSpendCeiling.BudgetOwnerKey] = "11111111-1111-1111-1111-111111111111",
        });

        output["outcome"].Should().Be("stop");
        output["stopReason"].ToString().Should().Contain("spend unknown");
    }

    [Test]
    public async Task Continues_WhenNoBudgetOwnerExists_evenWithACeiling()
    {
        // Single-user default: nothing to meter. Reported loudly rather than bricking a
        // fresh deployment on its first tick with no in-band way to recover.
        var output = await RunAsync(activeInstances: 0, config: new Dictionary<string, string?>
        {
            [AdlSpendCeiling.MaxSpendKey] = "25",
        });

        output["outcome"].Should().Be("continue");
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static async Task<IDictionary<string, object>> RunAsync(
        long activeInstances,
        Dictionary<string, string?> config,
        bool emergencyStop = false)
    {
        var store = new Mock<IWorkflowInstanceStore>();
        store.Setup(s => s.CountAsync(It.IsAny<WorkflowInstanceFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(activeInstances);
        store.Setup(s => s.FindManyAsync(It.IsAny<WorkflowInstanceFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowInstance>());

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElsa(elsa => elsa.AddActivity<CheckLimitsActivity>());
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(store.Object);
        services.AddSingleton<IAdlStopSwitch>(new ConfigAdlStopSwitch(configuration));

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();

        var result = await runner.RunAsync(
            new GateProbeWorkflow(emergencyStop), new RunWorkflowOptions(), CancellationToken.None);

        result.WorkflowState.Status.Should().Be(WorkflowStatus.Finished,
            "the gate must always reach a terminal — a faulted limits check is a stalled loop");
        return result.WorkflowState.Output;
    }

    /// <summary>
    /// Minimal flowchart that records WHICH edge the gate took, plus its stop reason, on
    /// the workflow output surface — the outcome itself is not otherwise observable from
    /// a single-activity run.
    /// </summary>
    private sealed class GateProbeWorkflow(bool emergencyStop) : WorkflowBase
    {
        protected override void Build(IWorkflowBuilder builder)
        {
            var stopReason = builder.WithVariable<string?>("StopReason", null);

            var gate = new CheckLimitsActivity
            {
                Id = "CheckLimits",
                MaxConcurrent = new Input<int>(1),
                EmergencyStop = new Input<bool>(emergencyStop),
                StopReason = new Output<string?>(stopReason),
            };

            var recordReason = new SetOutput
            {
                Id = "RecordReason",
                OutputName = new("stopReason"),
                OutputValue = new(ctx => (object?)(stopReason.Get(ctx) ?? "")),
            };

            var onContinue = new SetOutput
            {
                Id = "OnContinue",
                OutputName = new("outcome"),
                OutputValue = new(_ => (object)"continue"),
            };

            var onStop = new SetOutput
            {
                Id = "OnStop",
                OutputName = new("outcome"),
                OutputValue = new(_ => (object)"stop"),
            };

            builder.Root = new Flowchart
            {
                Start = gate,
                Activities = { gate, recordReason, onContinue, onStop },
                Connections =
                {
                    new FlowConnection(new FlowEndpoint(gate, "Continue"), new FlowEndpoint(onContinue)),
                    new FlowConnection(new FlowEndpoint(gate, "Stop"), new FlowEndpoint(onStop)),
                    new FlowConnection(new FlowEndpoint(onContinue), new FlowEndpoint(recordReason)),
                    new FlowConnection(new FlowEndpoint(onStop), new FlowEndpoint(recordReason)),
                },
            };
        }
    }
}
