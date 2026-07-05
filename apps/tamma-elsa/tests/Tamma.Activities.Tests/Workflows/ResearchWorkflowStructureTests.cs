using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Research;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 3.4 — structural verification for <see cref="ResearchWorkflow"/>.
///
/// Asserts the workflow:
/// 1. Builds and has DefinitionId "research".
/// 2. Threads <c>TenantId</c> so the prompt registry resolves tenant-scoped prompts
///    (resolution is tenant→system→error — never empty/plain).
/// 3. Investigates the codebase / prior art by REUSING the <c>context-gathering</c>
///    sub-workflow (not reinventing a scan).
/// 4. Synthesizes the research report via <c>DispatchWorkflow("llm-call")</c> (mediated —
///    the engine holds no LLM credential, TAMMA001) rather than any in-engine provider call.
/// 5. Is fail-closed: a <c>ResearchError</c> terminal exists and a <c>FlowDecision</c> gate
///    checks LLM-call success before proceeding.
/// 6. Emits the required RESEARCH.* DCB events (started / context gathered / completed /
///    failed) via <see cref="EmitResearchEventActivity"/> nodes.
/// 7. Is AUTONOMOUS — no human gate / bookmark (no suspend activity in the graph).
/// </summary>
[TestFixture]
public class ResearchWorkflowStructureTests
{
    private static Flowchart Flowchart()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new ResearchWorkflow());
        return WorkflowTestHelper.GetFlowchart(builder);
    }

    [Test]
    public void Workflow_BuildsWithoutError()
    {
        var act = () => WorkflowTestHelper.BuildWorkflow(new ResearchWorkflow());
        act.Should().NotThrow("ResearchWorkflow.Build() must complete without exceptions");
    }

    [Test]
    public void Workflow_HasCorrectDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new ResearchWorkflow());
        builder.Object.DefinitionId.Should().Be("research");
    }

    [Test]
    public void Workflow_ThreadsTenantId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new ResearchWorkflow());
        builder.Object.Variables
            .Any(v => v.Name == "TenantId")
            .Should().BeTrue(
                "the workflow must thread TenantId so llm-call resolves tenant-scoped prompts " +
                "(tenant→system→error) for the research synthesis");
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
    public void Workflow_SynthesizesViaMediatedLlmCall()
    {
        Flowchart().Activities
            .OfType<DispatchWorkflow>()
            .Should().Contain(d => d.Id == "SynthesizeResearchLlm",
                "the research report must be synthesized via the mediated llm-call " +
                "(engine holds no LLM credential, TAMMA001)");
    }

    [Test]
    public void Workflow_HasFailClosedErrorTerminal()
    {
        Flowchart().Activities
            .OfType<Finish>()
            .Should().Contain(f => f.Id == "ResearchError",
                "a fail-closed ResearchError terminal must exist — synthesis failures route " +
                "there, never proceeding with a fabricated research report");
    }

    [Test]
    public void Workflow_HasSuccessGateForSynthesis()
    {
        Flowchart().Activities
            .OfType<FlowDecision>()
            .Select(d => d.Id)
            .Should().Contain("ResearchLlmOk",
                "the report output must be gated behind a ResearchLlmOk decision (fail-closed)");
    }

    [Test]
    public void Workflow_EmitsRequiredResearchEvents()
    {
        var emitIds = Flowchart().Activities
            .OfType<EmitResearchEventActivity>()
            .Select(a => a.Id)
            .ToList();

        emitIds.Should().Contain("EmitResearchStarted",
            "must emit RESEARCH.STARTED when the investigation begins");
        emitIds.Should().Contain("EmitContextGathered",
            "must emit RESEARCH.CONTEXT_GATHERED when the codebase/prior-art context is gathered");
        emitIds.Should().Contain("EmitResearchCompleted",
            "must emit RESEARCH.COMPLETED when a ranked report is synthesized");
        emitIds.Should().Contain("EmitResearchFailed",
            "must emit a LOUD RESEARCH.FAILED when synthesis fails / is unparseable");
    }

    [Test]
    public void Workflow_IsAutonomous_NoHumanBookmark()
    {
        // Research is autonomous: unlike ClarifyingQuestionsWorkflow (which suspends on a
        // WaitForClarifyingAnswersActivity bookmark awaiting human answers) it has no human
        // gate. Assert no bookmark-style "Wait*" activity is present in the graph.
        var waitActivities = Flowchart().Activities
            .Where(a => a.GetType().Name.StartsWith("Wait", StringComparison.Ordinal))
            .Select(a => a.GetType().Name)
            .ToList();

        waitActivities.Should().BeEmpty(
            "the research workflow is autonomous — it must not suspend on a human bookmark " +
            "(no Wait* activity), unlike the clarifying-questions workflow");
    }

    [Test]
    public void Workflow_OnlyDispatchesContextGatheringAndLlmCall()
    {
        var dispatchIds = Flowchart().Activities
            .OfType<DispatchWorkflow>()
            .Select(d => d.Id)
            .OrderBy(x => x)
            .ToList();

        dispatchIds.Should().BeEquivalentTo(new[] { "GatherContext", "SynthesizeResearchLlm" },
            "research reuses context-gathering for investigation and the mediated llm-call for " +
            "synthesis — no other dispatch and, crucially, no direct in-engine provider call");
    }
}
