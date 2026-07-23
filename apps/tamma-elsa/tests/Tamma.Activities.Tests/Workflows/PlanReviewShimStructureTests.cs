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
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-14 — structural pins for <see cref="PlanReviewWorkflow"/>, reduced to a deterministic
/// READ-THROUGH SHIM over the document store (D1). Covers AC2 (no independent produce-verdict
/// pipeline), AC5 (the old free-form verdict path is gone from the compiled graphs + assembly).
/// </summary>
[TestFixture]
public class PlanReviewShimStructureTests
{
    private static Flowchart Flowchart()
        => WorkflowTestHelper.GetFlowchart(WorkflowTestHelper.BuildWorkflow(new PlanReviewWorkflow()));

    private static System.Collections.Generic.List<IActivity> AllActivities() => StructureWalk.All(Flowchart());

    [Test]
    public void Workflow_BuildsWithoutError()
        => ((Action)(() => WorkflowTestHelper.BuildWorkflow(new PlanReviewWorkflow()))).Should().NotThrow();

    [Test]
    public void Workflow_HasStableDefinitionId()
        => WorkflowTestHelper.BuildWorkflow(new PlanReviewWorkflow()).Object.DefinitionId.Should().Be("plan-review",
            "the shim keeps the definition id byte-stable for the SingleIssueCycle call site (D1)");

    [Test]
    public void Workflow_ThreadsTenantId()
        => WorkflowTestHelper.BuildWorkflow(new PlanReviewWorkflow()).Object.Variables.Any(v => v.Name == "TenantId")
            .Should().BeTrue();

    [Test]
    public void Workflow_HasZeroDispatchWorkflowNodes()
        => AllActivities().OfType<DispatchWorkflow>().Should().BeEmpty(
            "the shim makes NO llm-call and dispatches NO sub-workflow — the review already ran inside the " +
            "Plan lifecycle; this is a pure store read (AC2/AC5)");

    [Test]
    public void Workflow_HasNoFinishActivity()
        => AllActivities().OfType<Finish>().Should().BeEmpty();

    [Test]
    public void Workflow_ReadsTheStoreViaTheFetchSeam()
        => AllActivities().OfType<FetchLatestAcceptedDocumentActivity>().Should().ContainSingle(
            "the shim reads the latest accepted plan + lineage via the 39-14 store read seam (D1/D8)");

    [Test]
    public void Workflow_ExposesTheLegacyOutputNameSet()
    {
        var outputNames = AllActivities()
            .OfType<Elsa.Workflows.Management.Activities.SetOutput.SetOutput>()
            .Select(o => o.OutputName.Expression?.Value as string)
            .Where(n => n is not null)
            .OrderBy(n => n)
            .ToArray();

        outputNames.Should().BeEquivalentTo(new[]
        {
            "decision", "deferred", "discussionLog", "planJson", "reviewNotes", "split", "suggestionsJson",
        }, "the shim maps to the exact legacy PlanReview output shape");
    }

    [Test]
    public void Workflow_DeclaresLatestStateReEntry_AndCarriesTheReEntryNode()
    {
        var decl = typeof(PlanReviewWorkflow).GetCustomAttribute<ResumeBehaviorAttribute>(inherit: false);
        decl.Should().NotBeNull();
        decl!.Mode.Should().Be(ResumeMode.LatestStateReEntry);
        AllActivities().OfType<ComputeReEntryPositionActivity>().Should().ContainSingle(
            "a LatestStateReEntry workflow must wire the generic ComputeReEntryPositionActivity (a harmless idempotent read)");
    }

    // ── AC5 (D6) — the old free-form verdict parsers no longer exist in the assembly ──

    [Test]
    public void ElsaServerAssembly_HasNoRetiredVerdictHelperTypes()
    {
        var assembly = typeof(PlanReviewWorkflow).Assembly;
        var retired = assembly.GetTypes()
            .Where(t => t.Name is "ReviewAggregationHelper" or "PlanValidationHelper")
            .Select(t => t.FullName)
            .ToList();

        retired.Should().BeEmpty(
            "the free-form verdict/validation parsers are DELETED — no parser-backed verdict dispatch can " +
            "compile, so the verdict fork is impossible by construction (AC5/D6)");
    }
}
