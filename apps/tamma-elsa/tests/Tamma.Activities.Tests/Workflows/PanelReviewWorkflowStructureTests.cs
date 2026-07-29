using Elsa.Expressions.Models;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Documents;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-7 — structural pins for <see cref="PanelReviewWorkflow"/> (AC2 graph
/// half, AC7 emit sites, AC9 definition-id surface).
/// </summary>
[TestFixture]
public class PanelReviewWorkflowStructureTests
{
    private static Elsa.Workflows.Activities.Flowchart.Activities.Flowchart Flowchart() =>
        WorkflowTestHelper.GetFlowchart(WorkflowTestHelper.BuildWorkflow(new PanelReviewWorkflow()));

    [Test]
    public void Workflow_HasStableDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new PanelReviewWorkflow());
        builder.Object.DefinitionId.Should().Be("review-panel");
    }

    [Test]
    public void Panel_HasZeroLlmCallNodes()
    {
        var llm = Flowchart().Activities.OfType<DispatchWorkflow>()
            .Where(d => ReadLiteralDefId(d) == "llm-call")
            .ToList();
        llm.Should().BeEmpty("the panel dispatches only the review-single-reviewer sub-workflow, never llm-call directly");
    }

    [Test]
    public void Panel_HasNineMemberDispatches_EachGuardedByAnInPanelDecision()
    {
        var fc = Flowchart();

        var memberDispatches = fc.Activities.OfType<DispatchWorkflow>()
            .Where(d => ReadLiteralDefId(d) == "review-single-reviewer")
            .ToList();
        // 7 → 9 (Story 41-1a D1/D2): tech_writer and ux_designer joined the
        // document-panel superset (ReviewerSelectionHelper.DocumentPanelRoster).
        // Their dispatches stay dormant unless a policy roster names them —
        // AcceptanceDefaults.PanelRoster is deliberately still 7 (C7).
        memberDispatches.Should().HaveCount(9, "the static roster is the 9-role document panel superset");

        // Every member dispatch is fed by an InPanel? FlowDecision "True" edge.
        foreach (var dispatch in memberDispatches)
        {
            fc.Connections.Any(c => c.Target.Activity.Id == dispatch.Id &&
                                    c.Source.Activity is FlowDecision && c.Source.Port == "True")
                .Should().BeTrue($"member dispatch {dispatch.Id} must be guarded by an InPanel? decision");
        }
    }

    [Test]
    public void Panel_EmitsStartedCompletedUndecidableMarkers()
    {
        var emitTypes = Flowchart().Activities.OfType<EmitDocumentEventActivity>()
            .Select(a => ReadLiteralString(a.EventType))
            .ToHashSet();

        emitTypes.Should().Contain(new[]
        {
            DocumentEvents.ReviewPanelStarted,
            DocumentEvents.ReviewPanelCompleted,
            DocumentEvents.ReviewPanelUndecidable,
        });
    }

    [Test]
    public void Undecidable_PathReachesNoEnvelopeBuild()
    {
        var fc = Flowchart();
        // The "False" (undecidable) branch of DecidedGate must NOT reach BuildAggregateEnvelope.
        fc.Connections.Any(c => c.Source.Activity.Id == "DecidedGate" && c.Source.Port == "False" && c.Target.Activity.Id == "EmitPanelUndecidable")
            .Should().BeTrue("an undecidable panel goes straight to the undecidable marker, no aggregate fabricated");
        fc.Connections.Any(c => c.Source.Activity.Id == "DecidedGate" && c.Source.Port == "True" && c.Target.Activity.Id == "BuildAggregateEnvelope")
            .Should().BeTrue("only a decided panel builds an aggregate envelope");
    }

    private static string? ReadLiteralDefId(DispatchWorkflow dispatch)
    {
        var value = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId")?.GetValue(dispatch);
        var expr = value?.GetType().GetProperty("Expression")?.GetValue(value) as Expression;
        return expr?.Value as string;
    }

    private static string? ReadLiteralString(object input)
    {
        var expr = input.GetType().GetProperty("Expression")?.GetValue(input) as Expression;
        return expr?.Value as string;
    }
}
