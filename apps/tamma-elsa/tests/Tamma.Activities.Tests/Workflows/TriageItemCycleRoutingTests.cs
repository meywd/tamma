using System.Reflection;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.Activities.Documents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-15 (D5/D8) — routing pins for the MIGRATED <see cref="TriageItemCycleWorkflow"/>.
/// The 4-role panel is now the lifecycle REVIEW stage inside the <c>triage-po-decision</c>
/// binding — the <c>triage-panel-review</c> dispatch + PanelUsable gate are DELETED. The
/// cycle gates on the context stage's fail-closed signal and the TYPED decision, threads
/// <c>findingsDocumentId</c> into the PO dispatch, and declares
/// <c>[ResumeBehavior(LatestStateReEntry)]</c> with an apply-idempotence gate on the item's
/// accepted triage-decision. All terminals reach Finish (no deadlock).
/// </summary>
[TestFixture]
public class TriageItemCycleRoutingTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TriageItemCycleWorkflow());
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    [Test]
    public void Workflow_BuildsWithExpectedDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TriageItemCycleWorkflow());
        builder.Object.DefinitionId.Should().Be("triage-item-cycle");
    }

    [Test]
    public void Workflow_HasNoPanelDispatch_PanelIsTheLifecycleReviewStageNow()
    {
        _flowchart.Activities.OfType<DispatchWorkflow>()
            .Where(d => ReadDefinitionId(d) == "triage-panel-review").Should().BeEmpty(
                "the 4-role panel is the triage-decision lifecycle REVIEW stage now (D5)");
        _flowchart.Activities.OfType<FlowDecision>().Select(d => d.Id)
            .Should().NotContain("PanelUsable", "the panel-usable gate is gone with the panel dispatch");
    }

    // ── Re-entry + apply-idempotence (D8) ──

    [Test]
    public void Workflow_DeclaresLatestStateReEntry_WithComputeNode()
    {
        var decl = typeof(TriageItemCycleWorkflow).GetCustomAttribute<ResumeBehaviorAttribute>(inherit: false);
        decl.Should().NotBeNull();
        decl!.Mode.Should().Be(ResumeMode.LatestStateReEntry);
        _flowchart.Activities.OfType<ComputeReEntryPositionActivity>().Should().ContainSingle();
    }

    [Test]
    public void AlreadyCompleteReEntry_ShortCircuitsToOneIdempotentCompleted_NoReApply()
    {
        HasEdge("ReadPositionStage", null, "AlreadyComplete").Should().BeTrue();
        HasEdge("AlreadyComplete", "True", "EmitCycleCompletedReentry").Should().BeTrue();
        HasEdge("EmitCycleCompletedReentry", null, "OutReentryResult").Should().BeTrue();
        HasEdge("OutReentryResult", null, "Finish").Should().BeTrue();

        // The re-entry short-circuit must NOT re-dispatch context/po or re-apply.
        var reach = Reachable("EmitCycleCompletedReentry");
        reach.Should().NotContain("GatherTriageContext");
        reach.Should().NotContain("PODecision");
        reach.Should().NotContain("ApplyLabels");

        // Fresh / mid-flow re-entry → the normal cycle.
        HasEdge("AlreadyComplete", "False", "EmitCycleStarted").Should().BeTrue();
    }

    // ── Context fail-closed signal ──

    [Test]
    public void ContextGatheredGate_Exists_AfterExtractingContext()
    {
        _flowchart.Activities.OfType<FlowDecision>().Any(d => d.Id == "ContextGathered").Should().BeTrue();
        HasEdge("ExtractContext", null, "ContextGathered").Should().BeTrue();
    }

    [Test]
    public void GatheredContext_ProceedsToPoDecisionDirectly_NoPanelStage()
    {
        HasEdge("ContextGathered", "True", "PODecision").Should().BeTrue();
        HasEdge("PODecision", null, "ExtractDecision").Should().BeTrue();
        HasEdge("ExtractDecision", null, "DecisionOK").Should().BeTrue();
    }

    [Test]
    public void FailedContext_SkipsDecisionAndLabels_RoutesToLoudTerminal()
    {
        HasEdge("ContextGathered", "False", "SetContextFailedReason").Should().BeTrue();
        HasEdge("SetContextFailedReason", null, "MarkSkipped").Should().BeTrue();

        var falseTargets = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "ContextGathered" && c.Source.Port == "False")
            .Select(c => c.Target.Activity.Id).ToList();
        falseTargets.Should().NotContain("PODecision");
        falseTargets.Should().NotContain("ApplyLabels");
    }

    [Test]
    public void ContextDispatch_ThreadsFindingsLineageAndTargetsBinding()
    {
        var dispatch = _flowchart.Activities.OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "GatherTriageContext");
        dispatch.Should().NotBeNull();
        ReadDefinitionId(dispatch!).Should().Be("triage-context-gathering");
    }

    [Test]
    public void PoDispatch_TargetsBinding()
    {
        var dispatch = _flowchart.Activities.OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "PODecision");
        dispatch.Should().NotBeNull();
        ReadDefinitionId(dispatch!).Should().Be("triage-po-decision");
    }

    // ── Typed decision gate before apply ──

    [Test]
    public void GoodDecision_RoutesToBuildApplyInputsThenApply()
    {
        HasEdge("DecisionOK", "True", "BuildApplyInputs").Should().BeTrue();
        HasEdge("BuildApplyInputs", null, "EmitLabelsInvalid").Should().BeTrue();
        HasEdge("EmitLabelsInvalid", null, "SeedFailedReason").Should().BeTrue();
        HasEdge("SeedFailedReason", null, "SeedFailedResult").Should().BeTrue();
        HasEdge("SeedFailedResult", null, "ApplyLabels").Should().BeTrue();
    }

    [Test]
    public void BadDecision_RoutesToFailedTerminal_NeverToApply()
    {
        HasEdge("DecisionOK", "False", "SetDecisionFailedReason").Should().BeTrue();
        HasEdge("SetDecisionFailedReason", null, "EmitCycleFailed").Should().BeTrue();
        HasEdge("EmitCycleFailed", null, "OutFailedResult").Should().BeTrue();
        HasEdge("OutFailedResult", null, "Finish").Should().BeTrue();

        var falseTargets = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "DecisionOK" && c.Source.Port == "False")
            .Select(c => c.Target.Activity.Id).ToList();
        falseTargets.Should().NotContain("BuildApplyInputs");
        falseTargets.Should().NotContain("ApplyLabels");
    }

    [Test]
    public void AllGates_HaveNoUnconditionalFallthrough()
    {
        foreach (var gateId in new[] { "AlreadyComplete", "ContextGathered", "DecisionOK" })
        {
            var fromGate = _flowchart.Connections.Where(c => c.Source.Activity.Id == gateId).ToList();
            fromGate.Should().NotBeEmpty();
            fromGate.Should().OnlyContain(c => c.Source.Port == "True" || c.Source.Port == "False",
                $"gate '{gateId}' must branch only on True/False");
        }
    }

    // ── Cycle-scoped TRIAGE.ISSUE.* events ──

    [Test]
    public void StartedEvent_EmittedAfterAlreadyCompleteGate_BeforeContextGathering()
    {
        CycleEventNode("EmitCycleStarted").Should().NotBeNull();
        HasEdge("EmitCycleStarted", null, "GatherTriageContext").Should().BeTrue();
    }

    [Test]
    public void ApplySuccessOutcome_RoutesToCompletedTerminal()
    {
        HasEdge("ApplyLabels", "Success", "EmitCycleCompleted").Should().BeTrue();
        HasEdge("EmitCycleCompleted", null, "OutCompletedResult").Should().BeTrue();
        HasEdge("OutCompletedResult", null, "Finish").Should().BeTrue();
    }

    [Test]
    public void ApplyFailureOutcome_RoutesToLoudFailedTerminal_NotCompleted()
    {
        HasEdge("ApplyLabels", "Failure", "SetApplyFailedReason").Should().BeTrue();
        HasEdge("SetApplyFailedReason", null, "EmitCycleApplyFailed").Should().BeTrue();
        HasEdge("EmitCycleApplyFailed", null, "OutApplyFailedResult").Should().BeTrue();
        HasEdge("OutApplyFailedResult", null, "Finish").Should().BeTrue();

        var failureReach = Reachable("SetApplyFailedReason");
        failureReach.Should().NotContain("EmitCycleCompleted");
    }

    [Test]
    public void ApplyNode_HasBothOutcomePorts_NoUnconditionalFallthrough()
    {
        var fromApply = _flowchart.Connections.Where(c => c.Source.Activity.Id == "ApplyLabels").ToList();
        fromApply.Should().NotBeEmpty();
        fromApply.Should().OnlyContain(c => c.Source.Port == "Success" || c.Source.Port == "Failure");
        fromApply.Select(c => c.Source.Port).Should().Contain(new[] { "Success", "Failure" });
    }

    [Test]
    public void Workflow_SeedsFailClosedItemResultBeforeApply()
    {
        var seed = _flowchart.Activities.OfType<SetOutput>().FirstOrDefault(o => o.Id == "SeedFailedResult");
        seed.Should().NotBeNull();
        ReadOutputName(seed!).Should().Be("itemResult");
        HasEdge("SeedFailedResult", null, "ApplyLabels").Should().BeTrue();
    }

    [Test]
    public void Workflow_UsesContinueWithIncidentsStrategy()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new TriageItemCycleWorkflow());
        builder.Object.WorkflowOptions.IncidentStrategyType
            .Should().Be(typeof(Elsa.Workflows.IncidentStrategies.ContinueWithIncidentsStrategy));
    }

    [Test]
    public void EveryTerminal_EmitsAnItemResultOutput()
    {
        var itemResultNodes = _flowchart.Activities.OfType<SetOutput>()
            .Where(o => ReadOutputName(o) == "itemResult").Select(o => o.Id).ToList();
        itemResultNodes.Should().Contain(new[]
        {
            "OutCompletedResult", "OutSkippedResult", "OutFailedResult", "OutReentryResult",
        });
    }

    [Test]
    public void EveryActivity_RoutesToATerminal_NoDanglingEdge()
    {
        var allIds = _flowchart.Activities.Select(a => a.Id!).ToHashSet();
        var sources = _flowchart.Connections.Select(c => c.Source.Activity.Id!).ToHashSet();
        foreach (var id in allIds.Where(i => i != "Finish"))
            sources.Should().Contain(id, $"activity '{id}' must route somewhere");
    }

    private EmitTriageCycleEventActivity? CycleEventNode(string id)
        => _flowchart.Activities.OfType<EmitTriageCycleEventActivity>().FirstOrDefault(a => a.Id == id);

    private static string? ReadOutputName(SetOutput output)
    {
        var value = output.OutputName;
        var expression = value?.GetType().GetProperty("Expression")?.GetValue(value)
            as Elsa.Expressions.Models.Expression;
        return expression?.Value?.ToString();
    }

    private bool HasEdge(string sourceId, string? port, string targetId)
        => _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == sourceId &&
            (port == null || c.Source.Port == port) &&
            c.Target.Activity.Id == targetId);

    private HashSet<string> Reachable(string startId)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(startId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var c in _flowchart.Connections.Where(c => c.Source.Activity.Id == id))
            {
                var t = c.Target.Activity.Id;
                if (t != null && seen.Add(t)) queue.Enqueue(t);
            }
        }
        return seen;
    }

    private static string? ReadDefinitionId(DispatchWorkflow dispatch)
    {
        var prop = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId");
        var value = prop?.GetValue(dispatch);
        var expression = value?.GetType().GetProperty("Expression")?.GetValue(value)
            as Elsa.Expressions.Models.Expression;
        return expression?.Value?.ToString();
    }
}
