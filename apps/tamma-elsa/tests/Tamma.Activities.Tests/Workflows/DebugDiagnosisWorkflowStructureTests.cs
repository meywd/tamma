using System;
using System.Linq;
using System.Reflection;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-15 (D4) — structural pins for <see cref="DebugDiagnosisWorkflow"/>, the NEW thin
/// binding over <c>document-lifecycle</c> that produces a typed <c>diagnosis</c> (replacing the
/// retired <c>AIDiagnosisActivity</c> hand-parser). Covers AC4 (structure half), AC5 (no-llm-call
/// pin), AC7 (declaration half).
/// </summary>
[TestFixture]
public class DebugDiagnosisWorkflowStructureTests
{
    private static Flowchart Flowchart()
        => WorkflowTestHelper.GetFlowchart(WorkflowTestHelper.BuildWorkflow(new DebugDiagnosisWorkflow()));

    private static System.Collections.Generic.List<IActivity> AllActivities() => StructureWalk.All(Flowchart());

    [Test]
    public void Workflow_BuildsWithoutError()
        => ((Action)(() => WorkflowTestHelper.BuildWorkflow(new DebugDiagnosisWorkflow()))).Should().NotThrow();

    [Test]
    public void Workflow_HasStableDefinitionId()
        => WorkflowTestHelper.BuildWorkflow(new DebugDiagnosisWorkflow()).Object.DefinitionId.Should().Be("debug-diagnosis");

    [Test]
    public void Workflow_HasExactlyOneDispatch_Lifecycle_NoLlmCall()
    {
        AllActivities().OfType<DispatchWorkflow>().Select(d => d.Id).OrderBy(x => x)
            .Should().BeEquivalentTo(new[] { "DispatchLifecycle" });
        AllActivities().OfType<DispatchWorkflow>()
            .Where(d => StructureWalk.LiteralDefId(d) == "llm-call").Should().BeEmpty(
                "no bespoke llm-call — diagnosis production rides the document-lifecycle producer cell (AC5)");
        StructureWalk.LiteralDefId(AllActivities().OfType<DispatchWorkflow>().Single(d => d.Id == "DispatchLifecycle"))
            .Should().Be("document-lifecycle");
    }

    [Test]
    public void DispatchLifecycle_MaterializesCanonicalPair_DiagnosisType_AndFeedbackCarrier()
    {
        TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches().Should().Contain(p =>
            p.Workflow == "DebugDiagnosisWorkflow" && p.DispatchId == "DispatchLifecycle" &&
            p.Role == AgentRole.SeniorDeveloper.ToWire() && p.Action == AgentAction.DebugRootcause.ToWire(),
            "the lifecycle binding hands the canonical (senior_developer, debug-rootcause) producer pair (D4)");

        var input = TaxonomyDriftBuildTests.MaterializeDispatchInput("DebugDiagnosisWorkflow", "DispatchLifecycle");
        input.Should().NotBeNull();
        (input!["documentType"] as string).Should().Be("diagnosis");
        (input!["feedbackVariableName"] as string).Should().Be("errorContext",
            "repair/revise notes land in the DECLARED errorContext carrier (39-6 D11 / the render-drop lesson)");
    }

    [Test]
    public void Workflow_HasNoFinishActivity()
        => AllActivities().OfType<Finish>().Should().BeEmpty(
            "every non-accept exit is a typed lifecycle outcome, not a dead terminal (AC1/AC4)");

    [Test]
    public void Workflow_CarriesTheReEntryNode()
        => AllActivities().OfType<ComputeReEntryPositionActivity>().Should().ContainSingle();

    [Test]
    public void Workflow_DeclaresLatestStateReEntry()
    {
        var decl = typeof(DebugDiagnosisWorkflow).GetCustomAttribute<ResumeBehaviorAttribute>(inherit: false);
        decl.Should().NotBeNull();
        decl!.Mode.Should().Be(ResumeMode.LatestStateReEntry);
    }
}
