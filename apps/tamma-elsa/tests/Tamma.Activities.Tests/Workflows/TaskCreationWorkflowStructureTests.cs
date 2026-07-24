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
/// Story 39-15 — structural pins for <see cref="TaskCreationWorkflow"/>, rebuilt as a THIN
/// binding over <c>document-lifecycle</c> (consumes <c>plan</c>, produces a task-breakdown
/// <c>plan</c>, D2). The bespoke validate-retry loop (<c>ValidationErrors</c>, <c>maxRetries</c>,
/// inline extract, <c>OutErr</c>, <see cref="Finish"/>) is DELETED. Covers AC3 structure half,
/// AC5 no-llm-call pin, AC7 declaration half, AC8.
/// </summary>
[TestFixture]
public class TaskCreationWorkflowStructureTests
{
    private static Flowchart Flowchart()
        => WorkflowTestHelper.GetFlowchart(WorkflowTestHelper.BuildWorkflow(new TaskCreationWorkflow()));

    private static System.Collections.Generic.List<IActivity> AllActivities() => StructureWalk.All(Flowchart());

    [Test]
    public void Workflow_BuildsWithoutError()
        => ((Action)(() => WorkflowTestHelper.BuildWorkflow(new TaskCreationWorkflow()))).Should().NotThrow();

    [Test]
    public void Workflow_HasStableDefinitionId()
        => WorkflowTestHelper.BuildWorkflow(new TaskCreationWorkflow()).Object.DefinitionId.Should().Be("task-creation",
            "the binding keeps the public definition id byte-stable so the SingleIssueCycle dispatch is untouched (D1)");

    [Test]
    public void Workflow_ThreadsTenantId()
        => WorkflowTestHelper.BuildWorkflow(new TaskCreationWorkflow()).Object.Variables.Any(v => v.Name == "TenantId")
            .Should().BeTrue();

    [Test]
    public void Workflow_HasNoRetryPlumbingVariables()
    {
        var names = WorkflowTestHelper.BuildWorkflow(new TaskCreationWorkflow()).Object.Variables.Select(v => v.Name).ToList();
        names.Should().NotContain("ValidationErrors");
        names.Should().NotContain("RetryCount");
        names.Should().NotContain("MaxRetries");
        names.Should().NotContain("TasksValid",
            "the inline validate/retry plumbing is deleted — validation flows through the lifecycle rings (AC3)");
    }

    [Test]
    public void Workflow_HasExactlyOneDispatch_Lifecycle_NoLlmCall()
    {
        AllActivities().OfType<DispatchWorkflow>().Select(d => d.Id).OrderBy(x => x)
            .Should().BeEquivalentTo(new[] { "DispatchLifecycle" });
        AllActivities().OfType<DispatchWorkflow>()
            .Where(d => StructureWalk.LiteralDefId(d) == "llm-call").Should().BeEmpty(
                "no bespoke llm-call — the producer dispatch lives inside document-lifecycle (AC5)");
        StructureWalk.LiteralDefId(AllActivities().OfType<DispatchWorkflow>().Single(d => d.Id == "DispatchLifecycle"))
            .Should().Be("document-lifecycle");
    }

    [Test]
    public void DispatchLifecycle_MaterializesCanonicalPair_PlanType_AndFeedbackCarrier()
    {
        TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches().Should().Contain(p =>
            p.Workflow == "TaskCreationWorkflow" && p.DispatchId == "DispatchLifecycle" &&
            p.Role == AgentRole.SeniorDeveloper.ToWire() && p.Action == AgentAction.CreateTasks.ToWire(),
            "the lifecycle binding hands the canonical (senior_developer, create-tasks) producer pair");

        var input = TaxonomyDriftBuildTests.MaterializeDispatchInput("TaskCreationWorkflow", "DispatchLifecycle");
        input.Should().NotBeNull();
        (input!["documentType"] as string).Should().Be("plan");
        (input!["feedbackVariableName"] as string).Should().Be("contextFindings",
            "repair/revise notes land in the DECLARED create-tasks carrier (the render-drop lesson)");
    }

    [Test]
    public void Workflow_HasNoFinishActivity()
        => AllActivities().OfType<Finish>().Should().BeEmpty(
            "the bespoke OutErr terminal is deleted — every non-accept exit is a typed lifecycle outcome (AC3)");

    [Test]
    public void Workflow_CarriesTheReEntryAndConsumedPlanFetchNodes()
    {
        AllActivities().OfType<ComputeReEntryPositionActivity>().Should().ContainSingle();
        AllActivities().OfType<FetchLatestAcceptedDocumentActivity>().Should().ContainSingle(
            "the consumed system plan is read via the store read seam for lineage (D2/D8)");
    }

    [Test]
    public void Workflow_DeclaresLatestStateReEntry()
    {
        var decl = typeof(TaskCreationWorkflow).GetCustomAttribute<ResumeBehaviorAttribute>(inherit: false);
        decl.Should().NotBeNull();
        decl!.Mode.Should().Be(ResumeMode.LatestStateReEntry);
    }

    [Test]
    public void Workflow_HasNoBookmarkSuspendActivity()
        => AllActivities().Where(a => a.GetType().Name.StartsWith("Wait", StringComparison.Ordinal))
            .Should().BeEmpty("the accept-gate suspend is inside the dispatched document-lifecycle child");
}
