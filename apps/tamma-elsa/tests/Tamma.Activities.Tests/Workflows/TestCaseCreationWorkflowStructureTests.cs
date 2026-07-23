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
/// Story 39-15 — structural pins for <see cref="TestCaseCreationWorkflow"/>, rebuilt as a THIN
/// binding over <c>document-lifecycle</c> (consumes <c>plan</c>, produces <c>test-spec</c>, D3).
/// The bespoke validate-retry loop is DELETED; the cross-document task-ID ring is a
/// <c>validationContextJson</c> forwarded to the type's <c>ValidateWithContext</c>. Covers AC2
/// structure half, AC5 no-llm-call pin, AC7 declaration half, AC8.
/// </summary>
[TestFixture]
public class TestCaseCreationWorkflowStructureTests
{
    private static Flowchart Flowchart()
        => WorkflowTestHelper.GetFlowchart(WorkflowTestHelper.BuildWorkflow(new TestCaseCreationWorkflow()));

    private static System.Collections.Generic.List<IActivity> AllActivities() => StructureWalk.All(Flowchart());

    [Test]
    public void Workflow_BuildsWithoutError()
        => ((Action)(() => WorkflowTestHelper.BuildWorkflow(new TestCaseCreationWorkflow()))).Should().NotThrow();

    [Test]
    public void Workflow_HasStableDefinitionId()
        => WorkflowTestHelper.BuildWorkflow(new TestCaseCreationWorkflow()).Object.DefinitionId.Should().Be("test-case-creation",
            "the binding keeps the public definition id byte-stable (D1)");

    [Test]
    public void Workflow_HasNoRetryPlumbingVariables()
    {
        var names = WorkflowTestHelper.BuildWorkflow(new TestCaseCreationWorkflow()).Object.Variables.Select(v => v.Name).ToList();
        names.Should().NotContain("ValidationErrors");
        names.Should().NotContain("RetryCount");
        names.Should().NotContain("MaxRetries");
        names.Should().NotContain("TestsValid",
            "the inline validate/retry plumbing is deleted — validation flows through the lifecycle rings (AC2)");
    }

    [Test]
    public void Workflow_HasExactlyOneDispatch_Lifecycle_NoLlmCall()
    {
        AllActivities().OfType<DispatchWorkflow>().Select(d => d.Id).OrderBy(x => x)
            .Should().BeEquivalentTo(new[] { "DispatchLifecycle" });
        AllActivities().OfType<DispatchWorkflow>()
            .Where(d => StructureWalk.LiteralDefId(d) == "llm-call").Should().BeEmpty(
                "no bespoke llm-call — the producer dispatch lives inside document-lifecycle (AC5)");
    }

    [Test]
    public void DispatchLifecycle_MaterializesCanonicalPair_TestSpecType_FeedbackAndValidationContext()
    {
        TaxonomyDriftBuildTests.ScanLifecycleBindingDispatches().Should().Contain(p =>
            p.Workflow == "TestCaseCreationWorkflow" && p.DispatchId == "DispatchLifecycle" &&
            p.Role == AgentRole.Tester.ToWire() && p.Action == AgentAction.WriteTests.ToWire(),
            "the lifecycle binding hands the canonical (tester, write-tests) producer pair");

        var input = TaxonomyDriftBuildTests.MaterializeDispatchInput("TestCaseCreationWorkflow", "DispatchLifecycle");
        input.Should().NotBeNull();
        (input!["documentType"] as string).Should().Be("test-spec");
        (input!["feedbackVariableName"] as string).Should().Be("testTarget",
            "repair/revise notes land in the DECLARED write-tests carrier (the render-drop lesson)");
        input!.Should().ContainKey("validationContextJson",
            "the consumed plan threads to VALIDATE for the cross-document task-ID ring (D3/AC2)");
    }

    [Test]
    public void Workflow_HasNoFinishActivity()
        => AllActivities().OfType<Finish>().Should().BeEmpty(
            "the bespoke OutErr terminal is deleted — every non-accept exit is a typed lifecycle outcome (AC2)");

    [Test]
    public void Workflow_CarriesTheReEntryNode()
        => AllActivities().OfType<ComputeReEntryPositionActivity>().Should().ContainSingle();

    [Test]
    public void Workflow_DeclaresLatestStateReEntry()
    {
        var decl = typeof(TestCaseCreationWorkflow).GetCustomAttribute<ResumeBehaviorAttribute>(inherit: false);
        decl.Should().NotBeNull();
        decl!.Mode.Should().Be(ResumeMode.LatestStateReEntry);
    }

    [Test]
    public void Workflow_HasNoBookmarkSuspendActivity()
        => AllActivities().Where(a => a.GetType().Name.StartsWith("Wait", StringComparison.Ordinal))
            .Should().BeEmpty("the accept-gate suspend is inside the dispatched document-lifecycle child");
}
