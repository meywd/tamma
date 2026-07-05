using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Ambiguity;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 3.6 — structural verification for <see cref="AmbiguityScoringWorkflow"/>.
///
/// Asserts the workflow:
/// 1. Builds and has DefinitionId "ambiguity-scoring".
/// 2. Threads <c>TenantId</c> so the prompt registry resolves tenant-scoped prompts
///    (resolution is tenant→system→error — never empty/plain).
/// 3. Scores the requirement via <c>DispatchWorkflow("llm-call")</c> (mediated — the engine
///    holds no LLM credential, TAMMA001) and dispatches NOTHING else (no direct provider call).
/// 4. Is fail-closed: an <c>AmbiguityError</c> terminal exists and a <c>FlowDecision</c> gate
///    checks LLM-call success before proceeding.
/// 5. Applies the threshold policy via a <c>ShouldClarify</c> decision (AC6).
/// 6. Emits the required AMBIGUITY.* DCB events (started / scored / clarification-triggered /
///    below-threshold / failed) via <see cref="EmitAmbiguityEventActivity"/> nodes.
/// 7. Is AUTONOMOUS — no human gate / bookmark (no suspend activity in the graph).
/// </summary>
[TestFixture]
public class AmbiguityScoringWorkflowStructureTests
{
    private static Flowchart Flowchart()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new AmbiguityScoringWorkflow());
        return WorkflowTestHelper.GetFlowchart(builder);
    }

    [Test]
    public void Workflow_BuildsWithoutError()
    {
        var act = () => WorkflowTestHelper.BuildWorkflow(new AmbiguityScoringWorkflow());
        act.Should().NotThrow("AmbiguityScoringWorkflow.Build() must complete without exceptions");
    }

    [Test]
    public void Workflow_HasCorrectDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new AmbiguityScoringWorkflow());
        builder.Object.DefinitionId.Should().Be("ambiguity-scoring");
    }

    [Test]
    public void Workflow_ThreadsTenantId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new AmbiguityScoringWorkflow());
        builder.Object.Variables
            .Any(v => v.Name == "TenantId")
            .Should().BeTrue(
                "the workflow must thread TenantId so llm-call resolves tenant-scoped prompts " +
                "(tenant→system→error) for the ambiguity scoring");
    }

    [Test]
    public void Workflow_ScoresViaMediatedLlmCall()
    {
        Flowchart().Activities
            .OfType<DispatchWorkflow>()
            .Should().Contain(d => d.Id == "ScoreAmbiguityLlm",
                "the ambiguity score must be produced via the mediated llm-call " +
                "(engine holds no LLM credential, TAMMA001)");
    }

    [Test]
    public void Workflow_OnlyDispatchesTheLlmCall()
    {
        var dispatchIds = Flowchart().Activities
            .OfType<DispatchWorkflow>()
            .Select(d => d.Id)
            .OrderBy(x => x)
            .ToList();

        dispatchIds.Should().BeEquivalentTo(new[] { "ScoreAmbiguityLlm" },
            "scoring is a single mediated llm-call — no other dispatch and, crucially, no direct " +
            "in-engine provider call");
    }

    [Test]
    public void Workflow_HasFailClosedErrorTerminal()
    {
        Flowchart().Activities
            .OfType<Finish>()
            .Should().Contain(f => f.Id == "AmbiguityError",
                "a fail-closed AmbiguityError terminal must exist — scoring failures route there, " +
                "never proceeding with a fabricated score");
    }

    [Test]
    public void Workflow_HasSuccessGateForScoring()
    {
        Flowchart().Activities
            .OfType<FlowDecision>()
            .Select(d => d.Id)
            .Should().Contain("AmbiguityLlmOk",
                "the assessment must be gated behind an AmbiguityLlmOk decision (fail-closed)");
    }

    [Test]
    public void Workflow_HasThresholdDecision()
    {
        Flowchart().Activities
            .OfType<FlowDecision>()
            .Select(d => d.Id)
            .Should().Contain("ShouldClarify",
                "the clarify/proceed routing must go through a ShouldClarify threshold decision (AC6)");
    }

    [Test]
    public void Workflow_EmitsRequiredAmbiguityEvents()
    {
        var emitIds = Flowchart().Activities
            .OfType<EmitAmbiguityEventActivity>()
            .Select(a => a.Id)
            .ToList();

        emitIds.Should().Contain("EmitAmbiguityStarted",
            "must emit AMBIGUITY.STARTED when scoring begins");
        emitIds.Should().Contain("EmitAmbiguityScored",
            "must emit AMBIGUITY.SCORED when a valid score is produced");
        emitIds.Should().Contain("EmitClarificationTriggered",
            "must emit AMBIGUITY.CLARIFICATION_TRIGGERED when the score meets the threshold");
        emitIds.Should().Contain("EmitBelowThreshold",
            "must emit AMBIGUITY.BELOW_THRESHOLD when the score is below the threshold");
        emitIds.Should().Contain("EmitAmbiguityFailed",
            "must emit a LOUD AMBIGUITY.FAILED when scoring fails / is unparseable");
    }

    [Test]
    public void Workflow_IsAutonomous_NoHumanBookmark()
    {
        // Ambiguity scoring is autonomous: unlike ClarifyingQuestionsWorkflow (which suspends on a
        // WaitForClarifyingAnswersActivity bookmark awaiting human answers) it has no human gate.
        // Assert no bookmark-style "Wait*" activity is present in the graph.
        var waitActivities = Flowchart().Activities
            .Where(a => a.GetType().Name.StartsWith("Wait", StringComparison.Ordinal))
            .Select(a => a.GetType().Name)
            .ToList();

        waitActivities.Should().BeEmpty(
            "the ambiguity-scoring workflow is autonomous — it must not suspend on a human " +
            "bookmark (no Wait* activity); the clarification it can trigger is the sibling " +
            "ClarifyingQuestionsWorkflow's job");
    }
}
