using System;
using System.Linq;
using System.Reflection;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Design;
using Tamma.Api.Services.Agents;
using Tamma.Activities.Documents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-13 — structural pins for <see cref="DesignProposalWorkflow"/>, rebuilt as a THIN
/// binding over <c>document-lifecycle</c> (produces <c>design</c>). The bespoke approval gate is
/// retired (D4) — NO <c>WaitForDesignApproval</c> type anywhere; the delivery hook is threaded
/// (D5). Covers AC1/AC2/AC3/AC5/AC6/AC8.
/// </summary>
[TestFixture]
public class DesignProposalWorkflowStructureTests
{
    private static Flowchart Flowchart()
        => WorkflowTestHelper.GetFlowchart(WorkflowTestHelper.BuildWorkflow(new DesignProposalWorkflow()));

    private static System.Collections.Generic.List<IActivity> AllActivities() => StructureWalk.All(Flowchart());

    [Test]
    public void Workflow_BuildsWithoutError()
        => ((Action)(() => WorkflowTestHelper.BuildWorkflow(new DesignProposalWorkflow()))).Should().NotThrow();

    [Test]
    public void Workflow_HasStableDefinitionId()
        => WorkflowTestHelper.BuildWorkflow(new DesignProposalWorkflow()).Object.DefinitionId.Should().Be("design-proposal");

    [Test]
    public void Workflow_ThreadsTenantId()
        => WorkflowTestHelper.BuildWorkflow(new DesignProposalWorkflow()).Object.Variables.Any(v => v.Name == "TenantId")
            .Should().BeTrue();

    [Test]
    public void Workflow_HasExactlyOneDispatch_Lifecycle_NoLlmCall()
    {
        AllActivities().OfType<DispatchWorkflow>().Select(d => d.Id).OrderBy(x => x)
            .Should().BeEquivalentTo(new[] { "DispatchLifecycle" });
        AllActivities().OfType<DispatchWorkflow>()
            .Where(d => StructureWalk.LiteralDefId(d) == "llm-call").Should().BeEmpty();
        StructureWalk.LiteralDefId(AllActivities().OfType<DispatchWorkflow>().Single(d => d.Id == "DispatchLifecycle"))
            .Should().Be("document-lifecycle");
    }

    [Test]
    public void DispatchLifecycle_MaterializesCanonicalPair_DesignType_AndDeliveryHook()
    {
        TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches().Should().Contain(p =>
            p.Workflow == "DesignProposalWorkflow" && p.DispatchId == "DispatchLifecycle" &&
            p.Role == AgentRole.Architect.ToWire() && p.Action == AgentAction.ProposeDesign.ToWire());

        var input = TaxonomyDriftBuildTests.MaterializeDispatchInput("DesignProposalWorkflow", "DispatchLifecycle");
        input.Should().NotBeNull();
        (input!["documentType"] as string).Should().Be("design");
        (input!["deliveryWorkflowDefinitionId"] as string).Should().Be("design-proposal-delivery",
            "the pre-ACCEPT delivery hook (D5) is threaded into the lifecycle dispatch");
    }

    [Test]
    public void Workflow_HasNoWaitForDesignApprovalType_Anywhere()
        => AllActivities().Any(a => a.GetType().Name.Contains("DesignApproval", StringComparison.Ordinal))
            .Should().BeFalse("the bespoke WaitForDesignApprovalActivity is retired — the accept gate lives in the lifecycle");

    [Test]
    public void Workflow_HasNoBookmarkSuspendActivity()
        => AllActivities().Where(a => a.GetType().Name.StartsWith("Wait", StringComparison.Ordinal))
            .Should().BeEmpty("the accept-gate suspend is inside the dispatched document-lifecycle child (D4)");

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
            .Should().BeEquivalentTo(new[] { "LifecycleAccepted", "LifecycleRejected" });

    [Test]
    public void Workflow_HasApprovedRejectedFailedEmitNodes()
        => AllActivities().OfType<EmitDesignEventActivity>().Select(a => a.Id).ToHashSet()
            .Should().Contain(new[] { "EmitProposalApproved", "EmitProposalRejected", "EmitProposalFailed" });

    [Test]
    public void Workflow_DeclaresLatestStateReEntry_AndCarriesTheReEntryNode()
    {
        var decl = typeof(DesignProposalWorkflow).GetCustomAttribute<ResumeBehaviorAttribute>(inherit: false);
        decl.Should().NotBeNull();
        decl!.Mode.Should().Be(ResumeMode.LatestStateReEntry);
        AllActivities().OfType<ComputeReEntryPositionActivity>().Should().ContainSingle();
    }
}
