using Elsa.Expressions.Models;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-7 — structural pins for <see cref="SingleReviewerWorkflow"/> (AC1 graph
/// half + the no-laundering structural pin, AC9 definition-id surface).
/// </summary>
[TestFixture]
public class SingleReviewerWorkflowStructureTests
{
    private static Flowchart Flowchart() =>
        WorkflowTestHelper.GetFlowchart(WorkflowTestHelper.BuildWorkflow(new SingleReviewerWorkflow()));

    [Test]
    public void Workflow_HasStableDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new SingleReviewerWorkflow());
        builder.Object.DefinitionId.Should().Be("review-single-reviewer");
    }

    [Test]
    public void Workflow_HasExactlyOneLlmCallDispatch()
    {
        var llm = Flowchart().Activities.OfType<DispatchWorkflow>()
            .Where(d => ReadLiteralDefId(d) == "llm-call")
            .Select(d => d.Id)
            .ToList();

        llm.Should().BeEquivalentTo(new[] { "DispatchReviewerCall" },
            "the single-reviewer producer makes exactly ONE mediated llm-call");
    }

    [Test]
    public void Repair_LoopsBackToTheSameDispatchNode()
    {
        Flowchart().Connections.Any(c =>
            c.Source.Activity.Id == "BuildRepairFeedback" && c.Target.Activity.Id == "DispatchReviewerCall")
            .Should().BeTrue("the bounded repair ring re-runs the SAME dispatch node");
    }

    [Test]
    public void ValidationGate_RoutesTrueToEnvelope_FalseToRepairGate()
    {
        var fc = Flowchart();
        fc.Connections.Any(c => c.Source.Activity.Id == "ValidationGate" && c.Source.Port == "True" && c.Target.Activity.Id == "BuildEnvelope")
            .Should().BeTrue("a valid review builds the envelope");
        fc.Connections.Any(c => c.Source.Activity.Id == "ValidationGate" && c.Source.Port == "False" && c.Target.Activity.Id == "RepairGate")
            .Should().BeTrue("an invalid review goes to the repair gate, never to success");
    }

    [Test]
    public void NoLaundering_SuccessOutputsReachedOnlyThroughTheValidChain()
    {
        var fc = Flowchart();

        // The success outputs node is reached ONLY from EmitValidated (the valid chain);
        // no invalid/repair-exhaustion path may feed it.
        var inbound = fc.Connections.Where(c => c.Target.Activity.Id == "SetOutputsSuccess").ToList();
        inbound.Should().OnlyContain(c => c.Source.Activity.Id == "EmitValidated",
            "the ONLY way to the success outputs is the valid produce→validate chain (no laundering)");

        // The repair-exhaustion path terminates at the FAIL outputs, never success.
        fc.Connections.Any(c => c.Source.Activity.Id == "RepairGate" && c.Source.Port == "False" && c.Target.Activity.Id == "SetFailureKind")
            .Should().BeTrue("exhausted repair goes to the typed failure terminal");
        fc.Connections.Any(c => c.Source.Activity.Id == "SetOutputsFail")
            .Should().BeTrue("there is a dedicated failure-outputs terminal");
    }

    [Test]
    public void EmitsProducedAndValidated_OnBothPaths()
    {
        var emitIds = Flowchart().Activities.OfType<Tamma.Activities.Documents.EmitDocumentEventActivity>()
            .Select(a => a.Id).ToHashSet();
        emitIds.Should().Contain(new[] { "EmitProduced", "EmitValidated", "EmitProducedFailed", "EmitValidatedFailed" });
    }

    private static string? ReadLiteralDefId(DispatchWorkflow dispatch)
    {
        var value = typeof(DispatchWorkflow).GetProperty("WorkflowDefinitionId")?.GetValue(dispatch);
        var expr = value?.GetType().GetProperty("Expression")?.GetValue(value) as Expression;
        return expr?.Value as string;
    }
}
