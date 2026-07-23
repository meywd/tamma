using System;
using System.Linq;
using System.Reflection;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Clarify;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-13 — structural pins for <see cref="ClarifyingQuestionsWorkflow"/>, rebuilt as a
/// THIN binding that runs <c>document-lifecycle</c> TWICE (questions → suspend → resolution) with
/// a generic <see cref="WaitForDocumentInputActivity"/> input gate between them (D2/D3). Covers
/// AC1/AC3/AC5/AC6/AC8.
/// </summary>
[TestFixture]
public class ClarifyingQuestionsWorkflowStructureTests
{
    private static Flowchart Flowchart()
        => WorkflowTestHelper.GetFlowchart(WorkflowTestHelper.BuildWorkflow(new ClarifyingQuestionsWorkflow()));

    private static System.Collections.Generic.List<IActivity> AllActivities() => StructureWalk.All(Flowchart());

    [Test]
    public void Workflow_BuildsWithoutError()
        => ((Action)(() => WorkflowTestHelper.BuildWorkflow(new ClarifyingQuestionsWorkflow()))).Should().NotThrow();

    [Test]
    public void Workflow_HasStableDefinitionId()
        => WorkflowTestHelper.BuildWorkflow(new ClarifyingQuestionsWorkflow()).Object.DefinitionId.Should().Be("clarifying-questions");

    [Test]
    public void Workflow_ThreadsTenantId()
        => WorkflowTestHelper.BuildWorkflow(new ClarifyingQuestionsWorkflow()).Object.Variables.Any(v => v.Name == "TenantId")
            .Should().BeTrue();

    [Test]
    public void Workflow_HasTwoLifecycleDispatches_NoLlmCall()
    {
        var lifecycle = AllActivities().OfType<DispatchWorkflow>()
            .Where(d => StructureWalk.LiteralDefId(d) == "document-lifecycle").Select(d => d.Id).OrderBy(x => x).ToList();
        lifecycle.Should().BeEquivalentTo(new[] { "DispatchRunA", "DispatchRunB" },
            "the binding runs the clarification lifecycle twice — questions (Run A) and resolution (Run B)");
        AllActivities().OfType<DispatchWorkflow>()
            .Where(d => StructureWalk.LiteralDefId(d) == "llm-call").Should().BeEmpty();
    }

    [Test]
    public void Workflow_HasExactlyOneInputGate_AndTheDeliverActivity()
    {
        AllActivities().OfType<WaitForDocumentInputActivity>().Should().ContainSingle(
            "the wait-for-answers step rides ONE generic input gate (D3)");
        AllActivities().OfType<DeliverClarifyingQuestionsActivity>().Should().ContainSingle(
            "the accepted questions are delivered between Run A and the input gate");
    }

    [Test]
    public void RunA_And_RunB_MaterializeTheirCanonicalCells()
    {
        var pairs = TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches();
        pairs.Should().Contain(p => p.Workflow == "ClarifyingQuestionsWorkflow" && p.DispatchId == "DispatchRunA" &&
            p.Role == AgentRole.ProductOwner.ToWire() && p.Action == AgentAction.ClarifyRequirements.ToWire());
        pairs.Should().Contain(p => p.Workflow == "ClarifyingQuestionsWorkflow" && p.DispatchId == "DispatchRunB" &&
            p.Role == AgentRole.ProductOwner.ToWire() && p.Action == AgentAction.IncorporateAnswers.ToWire());

        (TaxonomyDriftBuildTests.MaterializeDispatchInput("ClarifyingQuestionsWorkflow", "DispatchRunA")!["documentType"] as string)
            .Should().Be("clarification");
        (TaxonomyDriftBuildTests.MaterializeDispatchInput("ClarifyingQuestionsWorkflow", "DispatchRunB")!["documentType"] as string)
            .Should().Be("clarification");
    }

    [Test]
    public void Workflow_HasNoFinishActivity()
        => AllActivities().OfType<Finish>().Should().BeEmpty();

    [Test]
    public void Workflow_HasAllClarifyEmitNodes()
        => AllActivities().OfType<EmitClarifyEventActivity>().Select(a => a.Id).ToHashSet()
            .Should().Contain(new[]
            {
                "EmitQuestionsGenerated", "EmitQuestionsDelivered", "EmitAnswersReceived",
                "EmitRequirementsClarified", "EmitQuestionsFailed", "EmitIncorporationFailed", "EmitTimedOut",
            });

    [Test]
    public void Workflow_DeclaresBoth_WithTheInputGateAsSuspend_AndCarriesTheReEntryNode()
    {
        var decl = typeof(ClarifyingQuestionsWorkflow).GetCustomAttribute<ResumeBehaviorAttribute>(inherit: false);
        decl.Should().NotBeNull();
        decl!.Mode.Should().Be(ResumeMode.Both,
            "Clarify owns its input-gate bookmark AND re-enters from the latest accepted clarification state");
        decl.SuspendActivities.Should().Contain(typeof(WaitForDocumentInputActivity));
        AllActivities().OfType<ComputeReEntryPositionActivity>().Should().ContainSingle();
    }
}
