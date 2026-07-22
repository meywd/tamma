using Elsa.Expressions.Models;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-7 — structural pins for the <see cref="DocumentReviewWorkflow"/> router
/// (Design Decision D1; the 39-6 D10 definition-id contract, AC9).
/// </summary>
[TestFixture]
public class DocumentReviewWorkflowStructureTests
{
    private static Elsa.Workflows.Activities.Flowchart.Activities.Flowchart Flowchart() =>
        WorkflowTestHelper.GetFlowchart(WorkflowTestHelper.BuildWorkflow(new DocumentReviewWorkflow()));

    [Test]
    public void Workflow_HasStableDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new DocumentReviewWorkflow());
        builder.Object.DefinitionId.Should().Be("document-review");
    }

    [Test]
    public void Router_HasZeroLlmCallNodes()
    {
        Flowchart().Activities.OfType<DispatchWorkflow>()
            .Where(d => ReadLiteralDefId(d) == "llm-call")
            .Should().BeEmpty("the router only dispatches the two producer sub-workflows");
    }

    [Test]
    public void Router_BothBranchesDispatchThePinnedProducerDefinitionIds()
    {
        var defIds = Flowchart().Activities.OfType<DispatchWorkflow>()
            .Select(ReadLiteralDefId)
            .Where(x => x is not null)
            .ToHashSet();

        defIds.Should().Contain(new[] { "review-panel", "review-single-reviewer" },
            "the router dispatches the panel and single-reviewer producers by their pinned definition ids");
    }

    [Test]
    public void ModeGate_RoutesPanelAndSingle()
    {
        var fc = Flowchart();
        fc.Connections.Any(c => c.Source.Activity.Id == "ModeGate" && c.Source.Port == "True" && c.Target.Activity.Id == "DispatchPanel")
            .Should().BeTrue("panel mode dispatches the panel producer");
        fc.Connections.Any(c => c.Source.Activity.Id == "ModeGate" && c.Source.Port == "False" && c.Target.Activity.Id == "DispatchSingle")
            .Should().BeTrue("single mode dispatches the single-reviewer producer");
    }

    private static string? ReadLiteralDefId(DispatchWorkflow dispatch)
    {
        var value = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId")?.GetValue(dispatch);
        var expr = value?.GetType().GetProperty("Expression")?.GetValue(value) as Expression;
        return expr?.Value as string;
    }
}
