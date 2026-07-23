using System;
using System.Linq;
using System.Reflection;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Ambiguity;
using Tamma.Api.Services.Agents;
using Tamma.Activities.Documents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-13 — structural pins for <see cref="AmbiguityScoringWorkflow"/>, rebuilt as a THIN
/// binding over <c>document-lifecycle</c> (produces <c>ambiguity-assessment</c>). The inline
/// threshold branch is retired (D7): ZERO <c>clarifying-questions</c> dispatch (AC4 no-edge
/// pin), no threshold constant. Covers AC1/AC3/AC4/AC5/AC6/AC8.
/// </summary>
[TestFixture]
public class AmbiguityScoringWorkflowStructureTests
{
    private static Flowchart Flowchart()
        => WorkflowTestHelper.GetFlowchart(WorkflowTestHelper.BuildWorkflow(new AmbiguityScoringWorkflow()));

    private static System.Collections.Generic.List<IActivity> AllActivities() => StructureWalk.All(Flowchart());

    [Test]
    public void Workflow_BuildsWithoutError()
        => ((Action)(() => WorkflowTestHelper.BuildWorkflow(new AmbiguityScoringWorkflow()))).Should().NotThrow();

    [Test]
    public void Workflow_HasStableDefinitionId()
        => WorkflowTestHelper.BuildWorkflow(new AmbiguityScoringWorkflow()).Object.DefinitionId.Should().Be("ambiguity-scoring");

    [Test]
    public void Workflow_ThreadsTenantId()
        => WorkflowTestHelper.BuildWorkflow(new AmbiguityScoringWorkflow()).Object.Variables.Any(v => v.Name == "TenantId")
            .Should().BeTrue();

    [Test]
    public void Workflow_HasExactlyOneDispatch_Lifecycle_NoLlmCall_NoClarifyingQuestions()
    {
        var ids = AllActivities().OfType<DispatchWorkflow>().Select(d => d.Id).OrderBy(x => x).ToList();
        ids.Should().BeEquivalentTo(new[] { "DispatchLifecycle" });
        AllActivities().OfType<DispatchWorkflow>()
            .Where(d => StructureWalk.LiteralDefId(d) == "llm-call").Should().BeEmpty();
        AllActivities().OfType<DispatchWorkflow>()
            .Where(d => StructureWalk.LiteralDefId(d) == "clarifying-questions").Should().BeEmpty(
                "AC4 no-edge pin: the binding contains NO bespoke dispatch of clarifying-questions — " +
                "above-threshold is the typed lifecycle outcome the orchestrator routes");
        StructureWalk.LiteralDefId(AllActivities().OfType<DispatchWorkflow>().Single(d => d.Id == "DispatchLifecycle"))
            .Should().Be("document-lifecycle");
    }

    [Test]
    public void DispatchLifecycle_MaterializesCanonicalProducerPair_AndAssessmentType()
    {
        TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches().Should().Contain(p =>
            p.Workflow == "AmbiguityScoringWorkflow" && p.DispatchId == "DispatchLifecycle" &&
            p.Role == AgentRole.ProductOwner.ToWire() && p.Action == AgentAction.ScoreAmbiguity.ToWire());

        var input = TaxonomyDriftBuildTests.MaterializeDispatchInput("AmbiguityScoringWorkflow", "DispatchLifecycle");
        input.Should().NotBeNull();
        (input!["documentType"] as string).Should().Be("ambiguity-assessment");
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
            .Should().BeEquivalentTo(new[] { "FreshRun", "HasAssessment", "IsAmbiguity", "WasCompleteReEntry" });

    [Test]
    public void Workflow_HasAllAmbiguityEmitNodes()
        => AllActivities().OfType<EmitAmbiguityEventActivity>().Select(a => a.Id).ToHashSet()
            .Should().Contain(new[]
            {
                "EmitAmbiguityStarted", "EmitAmbiguityScored", "EmitClarificationTriggered",
                "EmitBelowThreshold", "EmitAmbiguityFailed",
            });

    [Test]
    public void Workflow_DeclaresLatestStateReEntry_AndCarriesTheReEntryNode()
    {
        var decl = typeof(AmbiguityScoringWorkflow).GetCustomAttribute<ResumeBehaviorAttribute>(inherit: false);
        decl.Should().NotBeNull();
        decl!.Mode.Should().Be(ResumeMode.LatestStateReEntry);
        AllActivities().OfType<ComputeReEntryPositionActivity>().Should().ContainSingle();
    }

    [Test]
    public void Workflow_HasNoBookmarkSuspendActivity()
        => AllActivities().Where(a => a.GetType().Name.StartsWith("Wait", StringComparison.Ordinal))
            .Should().BeEmpty();
}
