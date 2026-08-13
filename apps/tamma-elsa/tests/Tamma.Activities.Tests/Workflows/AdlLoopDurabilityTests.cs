using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.IncidentStrategies;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Requests;
using Elsa.Workflows.Runtime.Responses;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.Activities.Core;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// LOOP DURABILITY — the autonomous loop must not be endable by one transient failure.
///
/// <para><b>The hole these tests pin.</b> <c>adl-orchestrator</c> restarts itself: every
/// terminal path runs <c>… → cooldown → DispatchAdl → Finish</c>, and
/// <see cref="DispatchAdlActivity"/> dispatches the SUCCESSOR instance. Nothing else
/// dispatches that definition — there is no cron trigger and no watchdog (verified:
/// the only other reference to "adl-orchestrator" in <c>src</c> is a Studio menu link).
/// So the restart is the LAST step of the instance it restarts, and under Elsa's
/// DEFAULT incident strategy a single throwing activity anywhere upstream faults the
/// instance BEFORE its successor exists — the loop stops permanently until a human
/// dispatches one by hand.</para>
///
/// <para>Six sibling workflows (SingleIssueCycle, MergeApproval, TriageItemCycle,
/// DeploymentPipeline, ReviewFix, CleanUpFailedTenant) already set
/// <see cref="ContinueWithIncidentsStrategy"/> for exactly this reason —
/// SingleIssueCycleWorkflow.cs names it "the silent-failure hole". The orchestrator,
/// which needs it most, was the one that did not.</para>
/// </summary>
[TestFixture]
public class AdlLoopDurabilityTests
{
    // ── The root cause: the incident strategy ───────────────────────────────

    [Test]
    public void AdlOrchestrator_ContinuesWithIncidents_soOneThrowCannotEndTheLoop()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new AdlOrchestratorWorkflow());

        builder.Object.WorkflowOptions.IncidentStrategyType
            .Should().Be(typeof(ContinueWithIncidentsStrategy),
                "the orchestrator's restart is the LAST step of the instance it restarts and nothing "
                + "else dispatches adl-orchestrator, so under the default fault strategy one throwing "
                + "activity ends the autonomous loop permanently. This is the same setting the six "
                + "long-running sibling workflows already carry.");
    }

    [Test]
    public void AdlOrchestrator_KeepsTheRestartEdge_fromCooldown()
    {
        // The strategy above only helps if the restart is still downstream of the
        // cooldown every terminal path funnels into. If this edge is ever cut, the
        // loop stops after one tick and no incident-strategy setting can save it.
        var builder = WorkflowTestHelper.BuildWorkflow(new AdlOrchestratorWorkflow());
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        flowchart.Activities.OfType<DispatchAdlActivity>().Should().ContainSingle(
            "the orchestrator restarts itself by dispatching a new adl-orchestrator instance");

        var cooldown = flowchart.Activities.OfType<CooldownActivity>().Single();
        var restart = flowchart.Activities.OfType<DispatchAdlActivity>().Single();

        flowchart.Connections.Should().Contain(
            c => c.Source.Activity == cooldown && c.Target.Activity == restart,
            "cooldown must lead to the restart dispatch — that edge IS the loop");
    }

    // ── The dispatch activities must not throw out into the flow ────────────

    [Test]
    public async Task ARestartDispatchFailure_doesNotFaultTheInstance()
    {
        // Before the fix `await _dispatcher.DispatchAsync(...)` was unguarded, so a
        // transient broker/DB blip propagated, faulted the instance, and the loop
        // stopped with no successor. It must now be swallowed (and logged Critical).
        var dispatcher = new ThrowingDispatcher { Throw = _ => new InvalidOperationException("broker down") };

        var result = await RunAsync(dispatcher, new DispatchAdlActivity
        {
            ConfigJson = new Input<string>("{}"),
        });

        result.WorkflowState.Status.Should().Be(WorkflowStatus.Finished,
            "a failed restart dispatch must not fault the instance");
        result.WorkflowState.Incidents.Should().BeEmpty(
            "the failure is handled inside the activity, not surfaced as an incident");
    }

    [Test]
    public async Task ARestartDispatch_isRetried_beforeGivingUp()
    {
        // A single transient blip must not cost the loop a cycle either.
        var dispatcher = new ThrowingDispatcher
        {
            Throw = attempt => attempt <= 2 ? new InvalidOperationException("transient") : null,
        };

        var result = await RunAsync(dispatcher, new DispatchAdlActivity
        {
            ConfigJson = new Input<string>("{}"),
        });

        result.WorkflowState.Status.Should().Be(WorkflowStatus.Finished);
        dispatcher.Dispatched.Should().ContainSingle(
            "the third attempt must succeed and actually dispatch the successor");
        dispatcher.Attempts.Should().Be(3, "two failures then a success");
    }

    [Test]
    public async Task AnIssueCycleDispatchFailure_doesNotFaultTheInstance()
    {
        // Fire & forget by design: failing to start ONE issue cycle must cost that
        // issue, never the loop — this activity sits UPSTREAM of the restart edge.
        var dispatcher = new ThrowingDispatcher { Throw = _ => new InvalidOperationException("broker down") };

        var result = await RunAsync(dispatcher, new DispatchCycleActivity
        {
            Repository = new Input<string>("owner/repo"),
            WorkItemJson = new Input<string>("{}"),
            IssueNumber = new Input<int>(7),
            BotAssignee = new Input<string>("tamma-bot"),
            BaseBranch = new Input<string>("main"),
            TenantId = new Input<string>(string.Empty),
            Mode = new Input<string>("dev"),
        });

        result.WorkflowState.Status.Should().Be(WorkflowStatus.Finished,
            "a failed issue-cycle dispatch must not fault the orchestrator instance");
        result.WorkflowState.Incidents.Should().BeEmpty();
    }

    [Test]
    public async Task ASUCCESSFULCycleDispatch_doesNotFaultTheInstance()
    {
        // The orchestrator constructs DispatchCycleActivity WITHOUT wiring its
        // InstanceId output (AdlOrchestratorWorkflow.cs "DispatchIssueCycle" sets
        // Repository/WorkItemJson/IssueNumber/BotAssignee/BaseBranch/Mode/TenantId
        // and nothing else), so `Output<string?> InstanceId` is left at `default!`
        // — null. `InstanceId.Set(...)` on an unwired output throws NRE, which under
        // the old fault strategy killed the loop on the HAPPY path, every tick.
        var dispatcher = new ThrowingDispatcher();

        var result = await RunAsync(dispatcher, new DispatchCycleActivity
        {
            Repository = new Input<string>("owner/repo"),
            WorkItemJson = new Input<string>("{}"),
            IssueNumber = new Input<int>(7),
            BotAssignee = new Input<string>("tamma-bot"),
            BaseBranch = new Input<string>("main"),
            TenantId = new Input<string>(string.Empty),
            Mode = new Input<string>("dev"),
        });

        result.WorkflowState.Status.Should().Be(WorkflowStatus.Finished);
        result.WorkflowState.Incidents.Should().BeEmpty(
            "dispatching a cycle must not fault just because the caller does not consume "
            + "the InstanceId output");
        dispatcher.Dispatched.Should().ContainSingle("the cycle must actually be dispatched");
    }

    [Test]
    public async Task ATriageDispatchFailure_doesNotFaultTheInstance()
    {
        var dispatcher = new ThrowingDispatcher { Throw = _ => new InvalidOperationException("broker down") };

        var result = await RunAsync(dispatcher, new DispatchTriageActivity
        {
            Repository = new Input<string>("owner/repo"),
            UntriagedCount = new Input<int>(3),
        });

        result.WorkflowState.Status.Should().Be(WorkflowStatus.Finished,
            "a failed triage dispatch must not fault the orchestrator instance");
        result.WorkflowState.Incidents.Should().BeEmpty();
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static async Task<RunWorkflowResult> RunAsync(IWorkflowDispatcher dispatcher, IActivity activity)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElsa(elsa =>
        {
            elsa.AddActivity<DispatchAdlActivity>();
            elsa.AddActivity<DispatchCycleActivity>();
            elsa.AddActivity<DispatchTriageActivity>();
        });
        // Registered so the activities' `_dispatcher ?? context.GetService<…>()`
        // fallback resolves it — the same path a rehydrated (JSON-constructed)
        // activity takes in production.
        services.AddSingleton(dispatcher);

        // 2026-08-13 — the dispatch activities now resolve the PUBLISHED
        // definition VERSION id first (PublishedWorkflowDispatch); stub the
        // definition service to answer "<definitionId>" verbatim (AddElsa's
        // real one has no published definitions in this harness).
        var definitionService = new Moq.Mock<Elsa.Workflows.Management.IWorkflowDefinitionService>();
        definitionService
            .Setup(d => d.FindWorkflowDefinitionAsync(
                Moq.It.IsAny<string>(),
                Moq.It.IsAny<Elsa.Common.Models.VersionOptions>(),
                Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, Elsa.Common.Models.VersionOptions _, CancellationToken _) =>
                new Elsa.Workflows.Management.Entities.WorkflowDefinition
                {
                    Id = id,
                    DefinitionId = id,
                });
        services.AddSingleton(definitionService.Object);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();

        return await runner.RunAsync(
            new SingleActivityWorkflow(activity), new RunWorkflowOptions(), CancellationToken.None);
    }

    private sealed class SingleActivityWorkflow(IActivity activity) : WorkflowBase
    {
        protected override void Build(IWorkflowBuilder builder) => builder.Root = activity;
    }

    /// <summary>
    /// A dispatcher whose definition-dispatch throws according to <see cref="Throw"/>
    /// (called with the 1-based attempt number, return null to let it succeed).
    /// Shape copied from TenantScheduledTriggerServiceTests' CapturingDispatcher.
    /// </summary>
    private sealed class ThrowingDispatcher : IWorkflowDispatcher
    {
        public List<DispatchWorkflowDefinitionRequest> Dispatched { get; } = new();
        public int Attempts { get; private set; }
        public Func<int, Exception?>? Throw { get; set; }

        public Task<DispatchWorkflowResponse> DispatchAsync(
            DispatchWorkflowDefinitionRequest request, DispatchWorkflowOptions? options = default,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Throw?.Invoke(Attempts) is { } ex) throw ex;
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
}
