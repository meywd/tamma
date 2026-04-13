using System.Reflection;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using Moq;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Tests for Story 12-5c: Mentorship Skill-Level Adaptation Fix.
///
/// Validates that the MentorshipWorkflow routes VALIDATE -> "Valid" through the
/// Assessment sub-workflow and ExtractSkillLevel activity BEFORE reaching
/// AssessJuniorCapability, so the skill level is derived from assessment output
/// rather than using the hardcoded default of 3.
/// </summary>
[TestFixture]
public class MentorshipSkillLevelRoutingTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var workflow = new MentorshipWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    [Test]
    public void ValidateValid_ShouldRouteToAssessmentWorkflow_NotDirectlyToAssessJunior()
    {
        // The "Valid" outcome of ValidateStory should connect to the Assessment
        // sub-workflow (DispatchAssessment), not directly to AssessJuniorCapability.
        var validToAssessment = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ValidateStory" &&
            c.Source.Port == "Valid" &&
            c.Target.Activity.Id == "DispatchAssessment");

        validToAssessment.Should().BeTrue(
            "ValidateStory 'Valid' should route to DispatchAssessment sub-workflow " +
            "so skill level is assessed before AssessJuniorCapability");
    }

    [Test]
    public void ValidateValid_ShouldNotRouteDirectlyToAssessJunior()
    {
        // There should be NO direct connection from ValidateStory "Valid" to
        // AssessJuniorCapability — it must go through assessment first.
        var directToAssess = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ValidateStory" &&
            c.Source.Port == "Valid" &&
            c.Target.Activity.Id == "AssessJuniorCapability");

        directToAssess.Should().BeFalse(
            "ValidateStory 'Valid' should NOT connect directly to AssessJuniorCapability — " +
            "that would skip assessment and leave skill level at hardcoded default");
    }

    [Test]
    public void AssessmentWorkflow_ShouldConnectToExtractSkillLevel()
    {
        // Assessment sub-workflow output → ExtractSkillLevel
        var assessmentToExtract = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "DispatchAssessment" &&
            c.Target.Activity.Id == "ExtractSkillLevel");

        assessmentToExtract.Should().BeTrue(
            "DispatchAssessment should connect to ExtractSkillLevel " +
            "to map assessment confidence to skill level 1-5");
    }

    [Test]
    public void ExtractSkillLevel_ShouldConnectToAssessJunior()
    {
        // ExtractSkillLevel → AssessJuniorCapability
        var extractToAssess = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ExtractSkillLevel" &&
            c.Target.Activity.Id == "AssessJuniorCapability");

        extractToAssess.Should().BeTrue(
            "ExtractSkillLevel should connect to AssessJuniorCapability " +
            "so assessment uses the derived skill level");
    }

    [Test]
    public void SkillLevelAdaptationChain_IsComplete()
    {
        // Full chain: ValidateStory "Valid" → DispatchAssessment → ExtractSkillLevel → AssessJuniorCapability
        var step1 = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ValidateStory" &&
            c.Source.Port == "Valid" &&
            c.Target.Activity.Id == "DispatchAssessment");

        var step2 = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "DispatchAssessment" &&
            c.Target.Activity.Id == "ExtractSkillLevel");

        var step3 = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ExtractSkillLevel" &&
            c.Target.Activity.Id == "AssessJuniorCapability");

        step1.Should().BeTrue("step 1: ValidateStory Valid → DispatchAssessment");
        step2.Should().BeTrue("step 2: DispatchAssessment → ExtractSkillLevel");
        step3.Should().BeTrue("step 3: ExtractSkillLevel → AssessJuniorCapability");
    }

    [Test]
    public void ExtractSkillLevel_IsRegisteredInFlowchart()
    {
        // The ExtractSkillLevel activity must be present in the flowchart's activity list
        var extractActivity = _flowchart.Activities
            .FirstOrDefault(a => a.Id == "ExtractSkillLevel");

        extractActivity.Should().NotBeNull(
            "ExtractSkillLevel activity should be registered in the flowchart");
    }

    [Test]
    public void SkillLevelVariable_HasDefault3()
    {
        // The SkillLevel variable defaults to 3 (intermediate), which is fine as a
        // fallback — the fix ensures it gets overwritten by the assessment output
        // before it's used.
        var workflow = new MentorshipWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);

        var skillVar = builder.Object.Variables
            .OfType<Variable<int>>()
            .FirstOrDefault(v => v.Name == "SkillLevel");

        skillVar.Should().NotBeNull("SkillLevel variable should exist");
        skillVar!.Value.Should().Be(3,
            "SkillLevel default should be 3 (intermediate) as a fallback");
    }
}
