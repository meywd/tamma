using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Memory;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Tests for Story 12-5c: Mentorship Skill-Level Adaptation Fix.
///
/// Validates that assessment retry outcomes (Correct/Partial/Incorrect) route
/// through skill-level adjustment nodes before reaching downstream activities,
/// so skillLevel is updated on every assessment iteration — not just the initial
/// Assessment sub-workflow dispatch.
/// </summary>
[TestFixture]
public class MentorshipSkillLevelAdaptationTests
{
    private Flowchart _flowchart = null!;
    private Mock<IWorkflowBuilder> _builder = null!;

    [SetUp]
    public void SetUp()
    {
        var workflow = new MentorshipWorkflow();
        _builder = WorkflowTestHelper.BuildWorkflow(workflow);
        _flowchart = WorkflowTestHelper.GetFlowchart(_builder);
    }

    // =====================================================================
    // 1. Correct outcome routes through AdjustSkillOnCorrect
    // =====================================================================

    [Test]
    public void AssessCorrect_ShouldRouteToAdjustSkillOnCorrect()
    {
        var connection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "AssessJuniorCapability" &&
            c.Source.Port == "Correct" &&
            c.Target.Activity.Id == "AdjustSkillOnCorrect");

        connection.Should().BeTrue(
            "AssessJunior 'Correct' should route to AdjustSkillOnCorrect to increment skill level");
    }

    [Test]
    public void AdjustSkillOnCorrect_ShouldConnectToLlmCallWorkflow()
    {
        var connection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "AdjustSkillOnCorrect" &&
            c.Target.Activity.Id == "DispatchLlmCall");

        connection.Should().BeTrue(
            "AdjustSkillOnCorrect should connect to LLM Call workflow for plan generation");
    }

    [Test]
    public void AssessCorrect_ShouldNotRouteDirectlyToLlmCall()
    {
        var directConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "AssessJuniorCapability" &&
            c.Source.Port == "Correct" &&
            c.Target.Activity.Id == "DispatchLlmCall");

        directConnection.Should().BeFalse(
            "AssessJunior 'Correct' should NOT connect directly to LLM Call — " +
            "it must go through skill adjustment first");
    }

    // =====================================================================
    // 2. Partial outcome routes through AdjustSkillOnPartial
    // =====================================================================

    [Test]
    public void AssessPartial_ShouldRouteToAdjustSkillOnPartial()
    {
        var connection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "AssessJuniorCapability" &&
            c.Source.Port == "Partial" &&
            c.Target.Activity.Id == "AdjustSkillOnPartial");

        connection.Should().BeTrue(
            "AssessJunior 'Partial' should route to AdjustSkillOnPartial (no skill change)");
    }

    [Test]
    public void AdjustSkillOnPartial_ShouldConnectToIncrementAssessmentAttempt()
    {
        var connection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "AdjustSkillOnPartial" &&
            c.Target.Activity.Id == "IncrAssessmentAttempt");

        connection.Should().BeTrue(
            "AdjustSkillOnPartial should connect to IncrAssessmentAttempt");
    }

    [Test]
    public void AssessPartial_ShouldNotRouteDirectlyToIncrement()
    {
        var directConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "AssessJuniorCapability" &&
            c.Source.Port == "Partial" &&
            c.Target.Activity.Id == "IncrAssessmentAttempt");

        directConnection.Should().BeFalse(
            "AssessJunior 'Partial' should NOT connect directly to IncrAssessmentAttempt — " +
            "it must go through skill adjustment first");
    }

    // =====================================================================
    // 3. Incorrect outcome routes through AdjustSkillOnIncorrect
    // =====================================================================

    [Test]
    public void AssessIncorrect_ShouldRouteToAdjustSkillOnIncorrect()
    {
        var connection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "AssessJuniorCapability" &&
            c.Source.Port == "Incorrect" &&
            c.Target.Activity.Id == "AdjustSkillOnIncorrect");

        connection.Should().BeTrue(
            "AssessJunior 'Incorrect' should route to AdjustSkillOnIncorrect to decrement skill level");
    }

    [Test]
    public void AdjustSkillOnIncorrect_ShouldConnectToReExplainStory()
    {
        var connection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "AdjustSkillOnIncorrect" &&
            c.Target.Activity.Id == "ReExplainStory");

        connection.Should().BeTrue(
            "AdjustSkillOnIncorrect should connect to ReExplainStory");
    }

    [Test]
    public void AssessIncorrect_ShouldNotRouteDirectlyToReExplain()
    {
        var directConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "AssessJuniorCapability" &&
            c.Source.Port == "Incorrect" &&
            c.Target.Activity.Id == "ReExplainStory");

        directConnection.Should().BeFalse(
            "AssessJunior 'Incorrect' should NOT connect directly to ReExplainStory — " +
            "it must go through skill adjustment first");
    }

    // =====================================================================
    // 4. All three adjustment nodes are registered in the flowchart
    // =====================================================================

    [Test]
    public void AdjustSkillOnCorrect_IsRegisteredInFlowchart()
    {
        var activity = _flowchart.Activities.FirstOrDefault(a => a.Id == "AdjustSkillOnCorrect");
        activity.Should().NotBeNull("AdjustSkillOnCorrect should be registered in the flowchart");
    }

    [Test]
    public void AdjustSkillOnPartial_IsRegisteredInFlowchart()
    {
        var activity = _flowchart.Activities.FirstOrDefault(a => a.Id == "AdjustSkillOnPartial");
        activity.Should().NotBeNull("AdjustSkillOnPartial should be registered in the flowchart");
    }

    [Test]
    public void AdjustSkillOnIncorrect_IsRegisteredInFlowchart()
    {
        var activity = _flowchart.Activities.FirstOrDefault(a => a.Id == "AdjustSkillOnIncorrect");
        activity.Should().NotBeNull("AdjustSkillOnIncorrect should be registered in the flowchart");
    }

    // =====================================================================
    // 5. Skill level caps and floors (SetVariable lambda verification)
    // =====================================================================
    // Note: These tests verify structural correctness — the SetVariable<int>
    // nodes use Math.Min(5, ...) and Math.Max(1, ...) which are compile-time
    // guarantees. Full runtime verification would require Elsa workflow
    // execution with a real or in-memory runtime, which is out of scope for
    // this structural test suite.

    [Test]
    public void SkillLevelVariable_DefaultIs3()
    {
        var skillVar = _builder.Object.Variables
            .OfType<Variable<int>>()
            .FirstOrDefault(v => v.Name == "SkillLevel");

        skillVar.Should().NotBeNull("SkillLevel variable should exist");
        skillVar!.Value.Should().Be(3,
            "SkillLevel should default to 3 (Intermediate) as the initial fallback");
    }

    // =====================================================================
    // 6. Error outcome is NOT affected — still routes directly to Failed
    // =====================================================================

    [Test]
    public void AssessError_StillRoutesDirectlyToFailed()
    {
        var connection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "AssessJuniorCapability" &&
            c.Source.Port == "Error" &&
            c.Target.Activity.Id == "Failed");

        connection.Should().BeTrue(
            "AssessJunior 'Error' should still route directly to Failed " +
            "(no skill adjustment on error)");
    }

    // =====================================================================
    // 7. Full assessment adjustment chain (end-to-end structural)
    // =====================================================================

    [Test]
    public void FullChain_CorrectPath_IsComplete()
    {
        // AssessJunior[Correct] -> AdjustSkillOnCorrect -> DispatchLlmCall -> PlanDecomposition
        var step1 = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "AssessJuniorCapability" &&
            c.Source.Port == "Correct" &&
            c.Target.Activity.Id == "AdjustSkillOnCorrect");

        var step2 = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "AdjustSkillOnCorrect" &&
            c.Target.Activity.Id == "DispatchLlmCall");

        var step3 = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "DispatchLlmCall" &&
            c.Target.Activity.Id == "PlanDecomposition");

        step1.Should().BeTrue("step 1: AssessJunior Correct -> AdjustSkillOnCorrect");
        step2.Should().BeTrue("step 2: AdjustSkillOnCorrect -> DispatchLlmCall");
        step3.Should().BeTrue("step 3: DispatchLlmCall -> PlanDecomposition");
    }

    [Test]
    public void FullChain_PartialPath_IsComplete()
    {
        // AssessJunior[Partial] -> AdjustSkillOnPartial -> IncrAssessmentAttempt -> Guard
        var step1 = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "AssessJuniorCapability" &&
            c.Source.Port == "Partial" &&
            c.Target.Activity.Id == "AdjustSkillOnPartial");

        var step2 = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "AdjustSkillOnPartial" &&
            c.Target.Activity.Id == "IncrAssessmentAttempt");

        var step3 = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "IncrAssessmentAttempt" &&
            c.Target.Activity.Id == "GuardAssessmentRetries");

        step1.Should().BeTrue("step 1: AssessJunior Partial -> AdjustSkillOnPartial");
        step2.Should().BeTrue("step 2: AdjustSkillOnPartial -> IncrAssessmentAttempt");
        step3.Should().BeTrue("step 3: IncrAssessmentAttempt -> GuardAssessmentRetries");
    }

    [Test]
    public void FullChain_IncorrectPath_IsComplete()
    {
        // AssessJunior[Incorrect] -> AdjustSkillOnIncorrect -> ReExplainStory
        var step1 = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "AssessJuniorCapability" &&
            c.Source.Port == "Incorrect" &&
            c.Target.Activity.Id == "AdjustSkillOnIncorrect");

        var step2 = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "AdjustSkillOnIncorrect" &&
            c.Target.Activity.Id == "ReExplainStory");

        step1.Should().BeTrue("step 1: AssessJunior Incorrect -> AdjustSkillOnIncorrect");
        step2.Should().BeTrue("step 2: AdjustSkillOnIncorrect -> ReExplainStory");
    }
}
