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
/// <see cref="TriagePODecisionWorkflow"/>: a thin binding over <c>document-lifecycle</c>
/// producing a typed <c>triage-decision</c> on the <c>(product_owner, triage-intake)</c>
/// cell, whose REVIEW stage is the 39-7 panel over the draft. The bespoke
/// llm-call/parse/clamp path is gone; the empty-input SKIPPED short-circuit is kept.
/// Covers AC1 (structure half), AC5 (no-llm-call pin), AC7 (declaration half).
/// </summary>
[TestFixture]
public class TriagePODecisionWorkflowTests
{
    private static Flowchart Flowchart()
        => WorkflowTestHelper.GetFlowchart(WorkflowTestHelper.BuildWorkflow(new TriagePODecisionWorkflow()));

    private static System.Collections.Generic.List<IActivity> AllActivities() => StructureWalk.All(Flowchart());

    [Test]
    public void Workflow_BuildsWithoutError()
        => ((Action)(() => WorkflowTestHelper.BuildWorkflow(new TriagePODecisionWorkflow()))).Should().NotThrow();

    [Test]
    public void Workflow_HasStableDefinitionId()
        => WorkflowTestHelper.BuildWorkflow(new TriagePODecisionWorkflow()).Object.DefinitionId.Should().Be("triage-po-decision");

    [Test]
    public void Workflow_HasExactlyOneDispatch_Lifecycle_NoLlmCall()
    {
        AllActivities().OfType<DispatchWorkflow>().Select(d => d.Id).OrderBy(x => x)
            .Should().BeEquivalentTo(new[] { "DispatchLifecycle" });
        AllActivities().OfType<DispatchWorkflow>()
            .Where(d => StructureWalk.LiteralDefId(d) == "llm-call").Should().BeEmpty(
                "no bespoke llm-call — the decision rides the document-lifecycle producer cell (AC5)");
        AllActivities().OfType<DispatchWorkflow>()
            .Where(d => StructureWalk.LiteralDefId(d) == "triage-panel-review").Should().BeEmpty(
                "the 4-role panel is the lifecycle REVIEW stage now — no separate panel dispatch (D5)");
    }

    [Test]
    public void DispatchLifecycle_MaterializesCanonicalPair_TriageDecisionType_AndFeedbackCarrier()
    {
        TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches().Should().Contain(p =>
            p.Workflow == "TriagePODecisionWorkflow" && p.DispatchId == "DispatchLifecycle" &&
            p.Role == AgentRole.ProductOwner.ToWire() && p.Action == AgentAction.TriageIntake.ToWire(),
            "the lifecycle binding hands the canonical (product_owner, triage-intake) producer pair (D5)");

        var input = TaxonomyDriftBuildTests.MaterializeDispatchInput("TriagePODecisionWorkflow", "DispatchLifecycle");
        input.Should().NotBeNull();
        (input!["documentType"] as string).Should().Be("triage-decision");
        (input!["feedbackVariableName"] as string).Should().Be("contextFindings");
    }

    [Test]
    public void Workflow_KeepsEmptyInputSkipShortCircuit()
        => AllActivities().Select(a => a.Id).Should().Contain("InputsPresent",
            "the empty-input SKIPPED guard is kept (the one pre-lifecycle guard that saves LLM spend)");

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
        var decl = typeof(TriagePODecisionWorkflow).GetCustomAttribute<ResumeBehaviorAttribute>(inherit: false);
        decl.Should().NotBeNull();
        decl!.Mode.Should().Be(ResumeMode.LatestStateReEntry);
    }

    [Test]
    public void Workflow_EmitsTriagePoDecisionAndPanelMirrorEvents()
    {
        var names = AllActivities().Select(a => a.GetType().Name).ToList();
        names.Should().Contain("EmitTriagePoDecisionEventActivity",
            "TRIAGE.PO_DECISION.STARTED/COMPLETED/FAILED/SKIPPED continue (D6)");
        names.Should().Contain("EmitTriageEventActivity",
            "TRIAGE.PANEL.STARTED/COMPLETED/FAILED mirror the REVIEW boundary (D6)");
    }
}
