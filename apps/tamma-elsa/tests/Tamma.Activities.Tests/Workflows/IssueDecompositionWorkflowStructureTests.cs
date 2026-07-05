using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Decomposition;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 2.14 — structural verification for <see cref="IssueDecompositionWorkflow"/>.
///
/// Asserts the workflow:
/// 1. Builds and has DefinitionId "issue-decomposition".
/// 2. Threads <c>TenantId</c> so the prompt registry resolves tenant-scoped prompts
///    (resolution is tenant→system→error — never empty/plain).
/// 3. Investigates the codebase / prior art by REUSING the <c>context-gathering</c>
///    sub-workflow (not reinventing a scan).
/// 4. Decomposes the issue via <c>DispatchWorkflow("llm-call")</c> (mediated — the engine holds
///    no LLM credential, TAMMA001) rather than any in-engine provider call.
/// 5. Is fail-closed: a <c>DecompositionError</c> terminal exists and a <c>FlowDecision</c> gate
///    checks LLM-call success before proceeding.
/// 6. Emits the required DECOMPOSITION.* DCB events (started / context gathered / completed /
///    failed) via <see cref="EmitDecompositionEventActivity"/> nodes.
/// 7. Is AUTONOMOUS — no in-workflow human gate / bookmark (approval before EXECUTION, AC7, is a
///    downstream orchestration concern).
/// </summary>
[TestFixture]
public class IssueDecompositionWorkflowStructureTests
{
    private static Flowchart Flowchart()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new IssueDecompositionWorkflow());
        return WorkflowTestHelper.GetFlowchart(builder);
    }

    [Test]
    public void Workflow_BuildsWithoutError()
    {
        var act = () => WorkflowTestHelper.BuildWorkflow(new IssueDecompositionWorkflow());
        act.Should().NotThrow("IssueDecompositionWorkflow.Build() must complete without exceptions");
    }

    [Test]
    public void Workflow_HasCorrectDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new IssueDecompositionWorkflow());
        builder.Object.DefinitionId.Should().Be("issue-decomposition");
    }

    [Test]
    public void Workflow_ThreadsTenantId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new IssueDecompositionWorkflow());
        builder.Object.Variables
            .Any(v => v.Name == "TenantId")
            .Should().BeTrue(
                "the workflow must thread TenantId so llm-call resolves tenant-scoped prompts " +
                "(tenant→system→error) for the decomposition");
    }

    [Test]
    public void Workflow_ReusesContextGatheringForInvestigation()
    {
        Flowchart().Activities
            .OfType<DispatchWorkflow>()
            .Should().Contain(d => d.Id == "GatherContext",
                "the workflow must investigate the codebase / prior art by REUSING the " +
                "context-gathering sub-workflow rather than reinventing a scan");
    }

    [Test]
    public void Workflow_DecomposesViaMediatedLlmCall()
    {
        Flowchart().Activities
            .OfType<DispatchWorkflow>()
            .Should().Contain(d => d.Id == "DecomposeIssueLlm",
                "the decomposition must be produced via the mediated llm-call " +
                "(engine holds no LLM credential, TAMMA001)");
    }

    [Test]
    public void Workflow_HasFailClosedErrorTerminal()
    {
        Flowchart().Activities
            .OfType<Finish>()
            .Should().Contain(f => f.Id == "DecompositionError",
                "a fail-closed DecompositionError terminal must exist — decomposition failures " +
                "route there, never proceeding with a fabricated breakdown");
    }

    [Test]
    public void Workflow_HasSuccessGateForDecomposition()
    {
        Flowchart().Activities
            .OfType<FlowDecision>()
            .Select(d => d.Id)
            .Should().Contain("DecompositionLlmOk",
                "the decomposition output must be gated behind a DecompositionLlmOk decision (fail-closed)");
    }

    [Test]
    public void Workflow_EmitsRequiredDecompositionEvents()
    {
        var emitIds = Flowchart().Activities
            .OfType<EmitDecompositionEventActivity>()
            .Select(a => a.Id)
            .ToList();

        emitIds.Should().Contain("EmitDecompositionStarted",
            "must emit DECOMPOSITION.STARTED when the decomposition begins");
        emitIds.Should().Contain("EmitContextGathered",
            "must emit DECOMPOSITION.CONTEXT_GATHERED when the codebase/prior-art context is gathered");
        emitIds.Should().Contain("EmitDecompositionCompleted",
            "must emit DECOMPOSITION.COMPLETED when a valid sub-task set is produced");
        emitIds.Should().Contain("EmitDecompositionFailed",
            "must emit a LOUD DECOMPOSITION.FAILED when decomposition fails / is unparseable");
    }

    [Test]
    public void Workflow_IsAutonomous_NoHumanBookmark()
    {
        // Decomposition is autonomous: it PRODUCES the breakdown but does not suspend on a human
        // bookmark. Story 2.14 AC7 ("human approval before executing decomposed tasks") is a
        // downstream orchestration concern, not this workflow's job. Assert no bookmark-style
        // "Wait*" activity is present in the graph.
        var waitActivities = Flowchart().Activities
            .Where(a => a.GetType().Name.StartsWith("Wait", StringComparison.Ordinal))
            .Select(a => a.GetType().Name)
            .ToList();

        waitActivities.Should().BeEmpty(
            "the issue-decomposition workflow is autonomous — it must not suspend on a human " +
            "bookmark (no Wait* activity)");
    }

    [Test]
    public void Workflow_OnlyDispatchesContextGatheringAndLlmCall()
    {
        var dispatchIds = Flowchart().Activities
            .OfType<DispatchWorkflow>()
            .Select(d => d.Id)
            .OrderBy(x => x)
            .ToList();

        dispatchIds.Should().BeEquivalentTo(new[] { "DecomposeIssueLlm", "GatherContext" },
            "decomposition reuses context-gathering for investigation and the mediated llm-call " +
            "for the breakdown — no other dispatch and, crucially, no direct in-engine provider call");
    }
}
