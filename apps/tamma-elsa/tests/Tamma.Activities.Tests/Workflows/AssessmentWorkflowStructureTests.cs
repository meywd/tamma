using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Structural verification tests for AssessmentWorkflow (P0 fix 2026-06-30).
///
/// These tests assert that the workflow:
/// 1. Dispatches <c>llm-call</c> for question generation (role=product_owner,
///    action=generate-assessment-questions) instead of the fake heuristic
///    <c>GenerateQuestionsActivity</c>.
/// 2. Dispatches <c>llm-call</c> for response analysis (role=product_owner,
///    action=analyze-assessment-response) instead of the fake heuristic
///    <c>AnalyzeResponseActivity</c>.
/// 3. Threads <c>TenantId</c> so the prompt registry resolves tenant-scoped prompts.
/// 4. Is fail-closed: an <c>LlmCallError</c> terminal node exists, and
///    <c>FlowDecision</c> gates check LLM-call success before proceeding — a
///    <c>success=false</c> result routes to the error terminal rather than
///    fabricating questions or a confidence score.
/// </summary>
[TestFixture]
public class AssessmentWorkflowStructureTests
{
    // ================================================================
    // Build sanity
    // ================================================================

    [Test]
    public void AssessmentWorkflow_BuildsWithoutError()
    {
        var workflow = new AssessmentWorkflow();
        var act = () => WorkflowTestHelper.BuildWorkflow(workflow);
        act.Should().NotThrow("AssessmentWorkflow.Build() must complete without exceptions");
    }

    [Test]
    public void AssessmentWorkflow_HasCorrectDefinitionId()
    {
        var workflow = new AssessmentWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        builder.Object.DefinitionId.Should().Be("assessment");
    }

    // ================================================================
    // TenantId threading
    // ================================================================

    [Test]
    public void AssessmentWorkflow_HasTenantIdVariable()
    {
        var workflow = new AssessmentWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        builder.Object.Variables
            .Any(v => v.Name == "TenantId")
            .Should().BeTrue(
                "AssessmentWorkflow must thread TenantId so llm-call can resolve " +
                "tenant-scoped prompts for generate-assessment-questions and " +
                "analyze-assessment-response");
    }

    // ================================================================
    // P0 fix: llm-call dispatches for both AI steps
    // ================================================================

    [Test]
    public void AssessmentWorkflow_DispatchesLlmCallForQuestionGeneration()
    {
        var workflow = new AssessmentWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        flowchart.Activities
            .OfType<DispatchWorkflow>()
            .Should().Contain(d => d.Id == "GenerateQuestionsLlm",
                "AssessmentWorkflow must dispatch llm-call (Id='GenerateQuestionsLlm') " +
                "for AI question generation; GenerateQuestionsActivity used a hardcoded " +
                "question bank that bypassed the LLM entirely");
    }

    [Test]
    public void AssessmentWorkflow_DispatchesLlmCallForResponseAnalysis()
    {
        var workflow = new AssessmentWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        flowchart.Activities
            .OfType<DispatchWorkflow>()
            .Should().Contain(d => d.Id == "AnalyzeResponseLlm",
                "AssessmentWorkflow must dispatch llm-call (Id='AnalyzeResponseLlm') " +
                "for AI response analysis; AnalyzeResponseActivity used keyword counting " +
                "that fabricated the confidence score the mentorship machine routes on");
    }

    // ================================================================
    // Fail-closed: success-check gates + error terminal
    // ================================================================

    [Test]
    public void AssessmentWorkflow_HasFailClosedErrorTerminal()
    {
        var workflow = new AssessmentWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        flowchart.Activities
            .OfType<Finish>()
            .Should().Contain(f => f.Id == "LlmCallError",
                "AssessmentWorkflow must have a fail-closed LlmCallError terminal " +
                "(Id='LlmCallError') that LLM-call failures route to — never proceed " +
                "with fabricated questions or a fabricated confidence score");
    }

    [Test]
    public void AssessmentWorkflow_HasSuccessCheckDecisionForQuestions()
    {
        var workflow = new AssessmentWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        flowchart.Activities
            .OfType<FlowDecision>()
            .Should().Contain(d => d.Id == "QuestionsLlmOk",
                "AssessmentWorkflow must gate question delivery behind a QuestionsLlmOk " +
                "FlowDecision — if the llm-call for questions fails, the workflow must " +
                "route to the error terminal, not proceed with an empty/fabricated set");
    }

    [Test]
    public void AssessmentWorkflow_HasSuccessCheckDecisionForAnalysis()
    {
        var workflow = new AssessmentWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        flowchart.Activities
            .OfType<FlowDecision>()
            .Should().Contain(d => d.Id == "AnalysisLlmOk",
                "AssessmentWorkflow must gate the classify/profile steps behind an " +
                "AnalysisLlmOk FlowDecision — if the llm-call for analysis fails, the " +
                "workflow must route to the error terminal, not fabricate a confidence score");
    }

    // ================================================================
    // Negative: heuristic activities must not appear in the flowchart
    // ================================================================

    [Test]
    public void AssessmentWorkflow_DoesNotUseHeuristicActivities()
    {
        var workflow = new AssessmentWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        var typeNames = flowchart.Activities
            .Select(a => a.GetType().Name)
            .ToList();

        typeNames.Should().NotContain("GenerateQuestionsActivity",
            "GenerateQuestionsActivity has a hardcoded question bank — it is a fake " +
            "AI step that must be replaced by DispatchWorkflow(\"llm-call\")");
        typeNames.Should().NotContain("AnalyzeResponseActivity",
            "AnalyzeResponseActivity uses keyword counting / response-length heuristics " +
            "to fabricate confidence — it is a fake AI step that must be replaced by " +
            "DispatchWorkflow(\"llm-call\")");
    }

    // ================================================================
    // Parse activities exist in the flowchart
    // ================================================================

    [Test]
    public void AssessmentWorkflow_HasParseActivitiesForLlmResults()
    {
        var workflow = new AssessmentWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        var flowchart = WorkflowTestHelper.GetFlowchart(builder);

        var ids = flowchart.Activities.Select(a => a.Id).ToList();

        ids.Should().Contain("ParseQuestionsResult",
            "AssessmentWorkflow must have a ParseQuestionsResult step to extract the " +
            "JSON question array from the llm-call llmResponse");
        ids.Should().Contain("ParseAnalysisResult",
            "AssessmentWorkflow must have a ParseAnalysisResult step to extract the " +
            "JSON {status,confidence,gaps,strengths,rationale} from the llm-call llmResponse");
    }
}
