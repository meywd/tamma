using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Clarify;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 3.5 — structural verification for <see cref="ClarifyingQuestionsWorkflow"/>.
///
/// Asserts the workflow:
/// 1. Builds and has DefinitionId "clarifying-questions".
/// 2. Threads <c>TenantId</c> so the prompt registry resolves tenant-scoped prompts
///    (resolution is tenant→system→error — never empty/plain).
/// 3. Generates questions and incorporates answers via <c>DispatchWorkflow("llm-call")</c>
///    (mediated — the engine holds no LLM credential, TAMMA001) rather than any in-engine
///    provider call.
/// 4. Suspends on the <see cref="WaitForClarifyingAnswersActivity"/> bookmark awaiting the
///    human answers.
/// 5. Is fail-closed: an <c>LlmCallError</c> terminal exists and <c>FlowDecision</c> gates
///    check LLM-call success before proceeding.
/// 6. Emits the required CLARIFY.* DCB events (generated / delivered / answers received)
///    via <see cref="EmitClarifyEventActivity"/> nodes.
/// </summary>
[TestFixture]
public class ClarifyingQuestionsWorkflowStructureTests
{
    private static Flowchart Flowchart()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new ClarifyingQuestionsWorkflow());
        return WorkflowTestHelper.GetFlowchart(builder);
    }

    [Test]
    public void Workflow_BuildsWithoutError()
    {
        var act = () => WorkflowTestHelper.BuildWorkflow(new ClarifyingQuestionsWorkflow());
        act.Should().NotThrow("ClarifyingQuestionsWorkflow.Build() must complete without exceptions");
    }

    [Test]
    public void Workflow_HasCorrectDefinitionId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new ClarifyingQuestionsWorkflow());
        builder.Object.DefinitionId.Should().Be("clarifying-questions");
    }

    [Test]
    public void Workflow_ThreadsTenantId()
    {
        var builder = WorkflowTestHelper.BuildWorkflow(new ClarifyingQuestionsWorkflow());
        builder.Object.Variables
            .Any(v => v.Name == "TenantId")
            .Should().BeTrue(
                "the workflow must thread TenantId so llm-call resolves tenant-scoped prompts " +
                "(tenant→system→error) for clarify-requirements");
    }

    [Test]
    public void Workflow_DispatchesLlmCallForQuestionGeneration()
    {
        Flowchart().Activities
            .OfType<DispatchWorkflow>()
            .Should().Contain(d => d.Id == "GenerateQuestionsLlm",
                "questions must be generated via the mediated llm-call (engine holds no LLM credential)");
    }

    [Test]
    public void Workflow_DispatchesLlmCallForAnswerIncorporation()
    {
        Flowchart().Activities
            .OfType<DispatchWorkflow>()
            .Should().Contain(d => d.Id == "IncorporateAnswersLlm",
                "the human answers must be incorporated via the mediated llm-call");
    }

    [Test]
    public void Workflow_SuspendsOnAnswerBookmark()
    {
        Flowchart().Activities
            .OfType<WaitForClarifyingAnswersActivity>()
            .Should().ContainSingle(a => a.Id == "WaitForAnswers",
                "the workflow must suspend on the WaitForClarifyingAnswersActivity bookmark " +
                "awaiting the human answers");
    }

    [Test]
    public void Workflow_HasFailClosedErrorTerminal()
    {
        Flowchart().Activities
            .OfType<Finish>()
            .Should().Contain(f => f.Id == "LlmCallError",
                "a fail-closed LlmCallError terminal must exist — LLM-call failures route there, " +
                "never proceeding with fabricated questions or a fabricated clarification");
    }

    [Test]
    public void Workflow_HasSuccessGatesForBothLlmCalls()
    {
        var decisions = Flowchart().Activities.OfType<FlowDecision>().Select(d => d.Id).ToList();
        decisions.Should().Contain("QuestionsLlmOk",
            "question delivery must be gated behind a QuestionsLlmOk decision (fail-closed)");
        decisions.Should().Contain("IncorporationLlmOk",
            "the clarified output must be gated behind an IncorporationLlmOk decision (fail-closed)");
    }

    [Test]
    public void Workflow_EmitsRequiredClarifyEvents()
    {
        var emitIds = Flowchart().Activities
            .OfType<EmitClarifyEventActivity>()
            .Select(a => a.Id)
            .ToList();

        emitIds.Should().Contain("EmitQuestionsGenerated",
            "must emit CLARIFY.QUESTIONS.GENERATED when questions are produced");
        emitIds.Should().Contain("EmitQuestionsDelivered",
            "must emit CLARIFY.QUESTIONS.DELIVERED when questions are delivered");
        emitIds.Should().Contain("EmitAnswersReceived",
            "must emit CLARIFY.ANSWERS.RECEIVED when the human answers arrive");
    }

    [Test]
    public void Workflow_DeliversQuestions()
    {
        Flowchart().Activities
            .OfType<DeliverClarifyingQuestionsActivity>()
            .Should().ContainSingle(a => a.Id == "DeliverClarifyingQuestions",
                "the workflow must deliver the questions to the stakeholder");
    }
}
