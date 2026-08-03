using System;
using System.Linq;
using System.Reflection;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Context;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-14 — structural pins for <see cref="PlanGenerationWorkflow"/>, rebuilt as a THIN
/// binding over <c>document-lifecycle</c> (consumes <c>decomposition</c>, produces <c>plan</c>).
/// The bespoke validation-retry loop (<c>ValidationErrors</c> loop-back, <c>OutErr</c> terminal,
/// <c>maxRetries</c>, <c>PlanValidationHelper</c>) is DELETED. Covers AC1 (structure), AC5
/// (no-llm-call pin), AC7 (declaration half).
/// </summary>
[TestFixture]
public class PlanGenerationWorkflowStructureTests
{
    private static Flowchart Flowchart()
        => WorkflowTestHelper.GetFlowchart(WorkflowTestHelper.BuildWorkflow(new PlanGenerationWorkflow()));

    private static System.Collections.Generic.List<IActivity> AllActivities() => StructureWalk.All(Flowchart());

    [Test]
    public void Workflow_BuildsWithoutError()
        => ((Action)(() => WorkflowTestHelper.BuildWorkflow(new PlanGenerationWorkflow()))).Should().NotThrow();

    [Test]
    public void Workflow_HasStableDefinitionId()
        => WorkflowTestHelper.BuildWorkflow(new PlanGenerationWorkflow()).Object.DefinitionId.Should().Be("plan-generation",
            "the binding keeps the public definition id byte-stable so the SingleIssueCycle dispatch is untouched (D1)");

    [Test]
    public void Workflow_ThreadsTenantId()
        => WorkflowTestHelper.BuildWorkflow(new PlanGenerationWorkflow()).Object.Variables.Any(v => v.Name == "TenantId")
            .Should().BeTrue();

    [Test]
    public void Workflow_HasExactlyOneDispatch_Lifecycle_NoLlmCall()
    {
        AllActivities().OfType<DispatchWorkflow>().Select(d => d.Id).OrderBy(x => x)
            .Should().BeEquivalentTo(new[] { "DispatchLifecycle" },
                "the binding dispatches ONLY document-lifecycle; the decomposition fetch is an activity, not a dispatch");
        AllActivities().OfType<DispatchWorkflow>()
            .Where(d => StructureWalk.LiteralDefId(d) == "llm-call").Should().BeEmpty(
                "no bespoke llm-call — all producer/review dispatch lives inside document-lifecycle (AC5)");
        StructureWalk.LiteralDefId(AllActivities().OfType<DispatchWorkflow>().Single(d => d.Id == "DispatchLifecycle"))
            .Should().Be("document-lifecycle");
    }

    [Test]
    public void DispatchLifecycle_MaterializesCanonicalPair_PlanType_AndFeedbackCarrier()
    {
        TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches().Should().Contain(p =>
            p.Workflow == "PlanGenerationWorkflow" && p.DispatchId == "DispatchLifecycle" &&
            p.Role == AgentRole.Architect.ToWire() && p.Action == AgentAction.PlanSystemDesign.ToWire(),
            "the lifecycle binding hands the canonical (architect, plan-system-design) producer pair");

        var input = TaxonomyDriftBuildTests.MaterializeDispatchInput("PlanGenerationWorkflow", "DispatchLifecycle");
        input.Should().NotBeNull();
        (input!["documentType"] as string).Should().Be("plan");
        (input!["feedbackVariableName"] as string).Should().Be("contextFindings",
            "repair/revise notes land in the DECLARED carrier (39-6 D11 / the render-drop lesson)");
    }

    [Test]
    public void Workflow_HasNoFinishActivity()
        => AllActivities().OfType<Finish>().Should().BeEmpty(
            "the bespoke OutErr terminal is deleted — every non-accept exit is a typed lifecycle outcome (AC1/AC3)");

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
            .Should().BeEquivalentTo(new[] { "FreshRun", "LifecycleAccepted" },
                "no parse/verdict gate can reappear — the routing is exactly two typed FlowDecisions");

    [Test]
    public void Workflow_CarriesTheReEntryFetchAndStoreNodes()
    {
        AllActivities().OfType<ComputeReEntryPositionActivity>().Should().ContainSingle();
        AllActivities().OfType<FetchLatestAcceptedDocumentActivity>().Select(a => a.Id).OrderBy(x => x)
            .Should().BeEquivalentTo(new[] { "FetchAmbiguityAssessment", "FetchDecomposition" },
                "the consumed decomposition is read via the 39-14 store read seam (D4/D8), and 39-25 " +
                "adds the accepted ambiguity-assessment fetch that threads leg 1");
        AllActivities().OfType<StoreRoleFindingActivity>().Should().ContainSingle(
            "one aggregate-review store keeps the CONTEXT.STORE_ROLE.* family alive (D5)");
    }

    [Test]
    public void Workflow_DeclaresLatestStateReEntry()
    {
        var decl = typeof(PlanGenerationWorkflow).GetCustomAttribute<ResumeBehaviorAttribute>(inherit: false);
        decl.Should().NotBeNull();
        decl!.Mode.Should().Be(ResumeMode.LatestStateReEntry);
    }

    [Test]
    public void Workflow_HasNoBookmarkSuspendActivity()
        => AllActivities().Where(a => a.GetType().Name.StartsWith("Wait", StringComparison.Ordinal))
            .Should().BeEmpty("the accept-gate suspend is inside the dispatched document-lifecycle child");
}
