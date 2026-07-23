using System;
using System.Linq;
using System.Reflection;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Research;
using Tamma.Api.Services.Agents;
using Tamma.Activities.Documents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-13 — structural pins for <see cref="ResearchWorkflow"/>, rebuilt as a THIN binding
/// over <c>document-lifecycle</c> (produces <c>findings</c>). Covers AC1 (two dispatches, zero
/// llm-call, canonical producer pair), AC3 (zero Finish, single ExposeOutput leaf), AC5 (legacy
/// RESEARCH.* emits), AC6 (resume declaration), AC8.
/// </summary>
[TestFixture]
public class ResearchWorkflowStructureTests
{
    private static Flowchart Flowchart()
        => WorkflowTestHelper.GetFlowchart(WorkflowTestHelper.BuildWorkflow(new ResearchWorkflow()));

    private static System.Collections.Generic.List<IActivity> AllActivities() => StructureWalk.All(Flowchart());

    [Test]
    public void Workflow_BuildsWithoutError()
        => ((Action)(() => WorkflowTestHelper.BuildWorkflow(new ResearchWorkflow()))).Should().NotThrow();

    [Test]
    public void Workflow_HasStableDefinitionId()
        => WorkflowTestHelper.BuildWorkflow(new ResearchWorkflow()).Object.DefinitionId.Should().Be("research");

    [Test]
    public void Workflow_ThreadsTenantId()
        => WorkflowTestHelper.BuildWorkflow(new ResearchWorkflow()).Object.Variables.Any(v => v.Name == "TenantId")
            .Should().BeTrue();

    [Test]
    public void Workflow_HasExactlyTwoDispatches_ContextGatheringAndLifecycle_NoLlmCall()
    {
        AllActivities().OfType<DispatchWorkflow>().Select(d => d.Id).OrderBy(x => x)
            .Should().BeEquivalentTo(new[] { "DispatchLifecycle", "GatherContext" });
        AllActivities().OfType<DispatchWorkflow>()
            .Where(d => StructureWalk.LiteralDefId(d) == "llm-call").Should().BeEmpty();
        StructureWalk.LiteralDefId(AllActivities().OfType<DispatchWorkflow>().Single(d => d.Id == "GatherContext"))
            .Should().Be("context-gathering");
        StructureWalk.LiteralDefId(AllActivities().OfType<DispatchWorkflow>().Single(d => d.Id == "DispatchLifecycle"))
            .Should().Be("document-lifecycle");
    }

    [Test]
    public void DispatchLifecycle_MaterializesCanonicalProducerPair_AndFindingsType()
    {
        TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches().Should().Contain(p =>
            p.Workflow == "ResearchWorkflow" && p.DispatchId == "DispatchLifecycle" &&
            p.Role == AgentRole.ProductOwner.ToWire() && p.Action == AgentAction.Research.ToWire());

        var input = TaxonomyDriftBuildTests.MaterializeDispatchInput("ResearchWorkflow", "DispatchLifecycle");
        input.Should().NotBeNull();
        (input!["documentType"] as string).Should().Be("findings");
    }

    [Test]
    public void Workflow_HasNoFinishActivity()
        => AllActivities().OfType<Finish>().Should().BeEmpty();

    [Test]
    public void Workflow_EveryGraphLeaf_IsTheSingleExposeOutputRegion()
    {
        var fc = Flowchart();
        var sources = fc.Connections.Select(c => c.Source.Activity.Id).ToHashSet();
        fc.Activities.Where(a => !sources.Contains(a.Id)).Select(a => a.Id)
            .Should().BeEquivalentTo(new[] { "ExposeOutput" });
    }

    [Test]
    public void Workflow_HasExactlyTheExpectedFlowDecisions()
        => AllActivities().OfType<FlowDecision>().Select(d => d.Id).OrderBy(x => x)
            .Should().BeEquivalentTo(new[] { "FreshRun", "LifecycleAccepted", "WasCompleteReEntry" });

    [Test]
    public void Workflow_HasAllResearchEmitNodes()
        => AllActivities().OfType<EmitResearchEventActivity>().Select(a => a.Id).ToHashSet()
            .Should().Contain(new[] { "EmitResearchStarted", "EmitContextGathered", "EmitResearchCompleted", "EmitResearchFailed" });

    [Test]
    public void Workflow_DeclaresLatestStateReEntry_AndCarriesTheReEntryNode()
    {
        var decl = typeof(ResearchWorkflow).GetCustomAttribute<ResumeBehaviorAttribute>(inherit: false);
        decl.Should().NotBeNull();
        decl!.Mode.Should().Be(ResumeMode.LatestStateReEntry);
        AllActivities().OfType<ComputeReEntryPositionActivity>().Should().ContainSingle();
    }

    [Test]
    public void Workflow_HasNoBookmarkSuspendActivity()
        => AllActivities().Where(a => a.GetType().Name.StartsWith("Wait", StringComparison.Ordinal))
            .Should().BeEmpty();
}
