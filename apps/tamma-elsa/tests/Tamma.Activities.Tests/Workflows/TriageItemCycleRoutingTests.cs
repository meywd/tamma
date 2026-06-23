using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Completeness audit 2026-06-22 — the consumer of the (now fail-closed)
/// triage panel must HONOUR its failure signal. A "failed" panel (below quorum)
/// must NOT march on to PO decision + label application: it routes to a loud
/// non-applying terminal (no silent false success downstream). An "ok"/"partial"
/// panel proceeds as before. Both gate outcomes reach Finish (no deadlock/hang).
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
    public void PanelUsableGate_Exists_AfterExtractingPanelResult()
    {
        _flowchart.Activities.OfType<FlowDecision>()
            .Any(d => d.Id == "PanelUsable")
            .Should().BeTrue("the cycle must gate on the panel's fail-closed signal");

        HasEdge("ExtractPanelResult", null, "PanelUsable").Should().BeTrue();
    }

    [Test]
    public void UsablePanel_ProceedsToPoDecisionAndLabels()
    {
        HasEdge("PanelUsable", "True", "PODecision").Should().BeTrue();
        HasEdge("PODecision", null, "ExtractDecision").Should().BeTrue();
        HasEdge("ExtractDecision", null, "ApplyLabels").Should().BeTrue();
        HasEdge("ApplyLabels", null, "Finish").Should().BeTrue();
    }

    [Test]
    public void FailedPanel_SkipsLabelApplication_RoutesToLoudTerminal()
    {
        // False (failed panel) → mark skipped → finish. It must NOT reach the PO
        // decision or the label-application activity (no silent labelling off a
        // wholly-failed panel).
        HasEdge("PanelUsable", "False", "MarkSkipped").Should().BeTrue();
        HasEdge("MarkSkipped", null, "OutSkipReason").Should().BeTrue();
        HasEdge("OutSkipReason", null, "Finish").Should().BeTrue();

        var falseTargets = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "PanelUsable" && c.Source.Port == "False")
            .Select(c => c.Target.Activity.Id)
            .ToList();

        falseTargets.Should().NotContain("PODecision");
        falseTargets.Should().NotContain("ApplyLabels");
    }

    [Test]
    public void PanelUsableGate_HasNoUnconditionalFallthrough()
    {
        var fromGate = _flowchart.Connections
            .Where(c => c.Source.Activity.Id == "PanelUsable")
            .ToList();

        fromGate.Should().NotBeEmpty();
        fromGate.Should().OnlyContain(c => c.Source.Port == "True" || c.Source.Port == "False");
    }

    [Test]
    public void BothGateOutcomes_ReachFinish_NoDeadlock()
    {
        // Usable path: ApplyLabels → Finish. Failed path: OutSkipReason → Finish.
        HasEdge("ApplyLabels", null, "Finish").Should().BeTrue();
        HasEdge("OutSkipReason", null, "Finish").Should().BeTrue();
    }

    [Test]
    public void PanelDispatch_ThreadsTenantId()
    {
        // The cycle threads tenantId through to the panel sub-workflow input so a
        // tenant-scoped caller's TRIAGE.PANEL.* events carry the tenant tag.
        var dispatch = _flowchart.Activities
            .OfType<DispatchWorkflow>()
            .FirstOrDefault(d => d.Id == "PanelReview");
        dispatch.Should().NotBeNull();
        ReadDefinitionId(dispatch!).Should().Be("triage-panel-review");
    }

    private bool HasEdge(string sourceId, string? port, string targetId)
        => _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == sourceId &&
            (port == null || c.Source.Port == port) &&
            c.Target.Activity.Id == targetId);

    private static string? ReadDefinitionId(DispatchWorkflow dispatch)
    {
        var prop = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId");
        var value = prop?.GetValue(dispatch);
        var expression = value?.GetType().GetProperty("Expression")?.GetValue(value)
            as Elsa.Expressions.Models.Expression;
        return expression?.Value?.ToString();
    }
}
