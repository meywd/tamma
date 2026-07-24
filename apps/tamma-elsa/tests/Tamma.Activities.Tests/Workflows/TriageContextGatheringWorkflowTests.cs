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
/// Story 39-15 (D5) — structural pins for the MIGRATED
/// <see cref="TriageContextGatheringWorkflow"/>: a thin binding over
/// <c>document-lifecycle</c> producing a typed <c>findings</c> document on the SPLIT
/// <c>(developer, triage-context-scan)</c> cell (replacing the bespoke scan → ExtractContext
/// path). Covers AC1 (structure half), AC5 (no-llm-call pin), AC7 (declaration half).
/// </summary>
[TestFixture]
public class TriageContextGatheringWorkflowTests
{
    private static Flowchart Flowchart()
        => WorkflowTestHelper.GetFlowchart(WorkflowTestHelper.BuildWorkflow(new TriageContextGatheringWorkflow()));

    private static System.Collections.Generic.List<IActivity> AllActivities() => StructureWalk.All(Flowchart());

    [Test]
    public void Workflow_BuildsWithoutError()
        => ((Action)(() => WorkflowTestHelper.BuildWorkflow(new TriageContextGatheringWorkflow()))).Should().NotThrow();

    [Test]
    public void Workflow_HasStableDefinitionId()
        => WorkflowTestHelper.BuildWorkflow(new TriageContextGatheringWorkflow()).Object.DefinitionId.Should().Be("triage-context-gathering");

    [Test]
    public void Workflow_HasExactlyOneDispatch_Lifecycle_NoLlmCall()
    {
        AllActivities().OfType<DispatchWorkflow>().Select(d => d.Id).OrderBy(x => x)
            .Should().BeEquivalentTo(new[] { "DispatchLifecycle" });
        AllActivities().OfType<DispatchWorkflow>()
            .Where(d => StructureWalk.LiteralDefId(d) == "llm-call").Should().BeEmpty(
                "no bespoke llm-call — context gathering rides the document-lifecycle producer cell (AC5)");
        StructureWalk.LiteralDefId(AllActivities().OfType<DispatchWorkflow>().Single(d => d.Id == "DispatchLifecycle"))
            .Should().Be("document-lifecycle");
    }

    [Test]
    public void DispatchLifecycle_MaterializesCanonicalPair_FindingsType()
    {
        TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches().Should().Contain(p =>
            p.Workflow == "TriageContextGatheringWorkflow" && p.DispatchId == "DispatchLifecycle" &&
            p.Role == AgentRole.Developer.ToWire() && p.Action == AgentAction.TriageContextScan.ToWire(),
            "the lifecycle binding hands the SPLIT (developer, triage-context-scan) producer pair (D5)");

        var input = TaxonomyDriftBuildTests.MaterializeDispatchInput("TriageContextGatheringWorkflow", "DispatchLifecycle");
        input.Should().NotBeNull();
        (input!["documentType"] as string).Should().Be("findings");
    }

    [Test]
    public void Workflow_HasNoFinishActivity()
        => AllActivities().OfType<Finish>().Should().BeEmpty(
            "every non-accept exit is a typed lifecycle outcome, not a dead terminal (AC1)");

    [Test]
    public void Workflow_CarriesTheReEntryNode()
        => AllActivities().OfType<ComputeReEntryPositionActivity>().Should().ContainSingle();

    [Test]
    public void Workflow_DeclaresLatestStateReEntry()
    {
        var decl = typeof(TriageContextGatheringWorkflow).GetCustomAttribute<ResumeBehaviorAttribute>(inherit: false);
        decl.Should().NotBeNull();
        decl!.Mode.Should().Be(ResumeMode.LatestStateReEntry);
    }

    [Test]
    public void Workflow_EmitsTriageContextEvents()
        => AllActivities().Select(a => a.GetType().Name)
            .Should().Contain("EmitTriageContextEventActivity",
                "TRIAGE.CONTEXT.STARTED/COMPLETED/FAILED continue to mirror the lifecycle exits (D6)");
}
