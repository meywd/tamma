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
/// Tests for SingleIssueCycleWorkflow routing and decision logic.
/// Validates that FlowSwitch cases, revision limits, and connection targets are correct.
/// </summary>
[TestFixture]
public class SingleIssueCycleRoutingTests
{
    private Flowchart _flowchart = null!;

    [SetUp]
    public void SetUp()
    {
        var workflow = new SingleIssueCycleWorkflow();
        var builder = WorkflowTestHelper.BuildWorkflow(workflow);
        _flowchart = WorkflowTestHelper.GetFlowchart(builder);
    }

    // ================================================================
    // Review Outcome Routing
    // ================================================================

    [Test]
    public void ReviewOutcome_HasFiveCases()
    {
        var reviewOutcome = _flowchart.Activities
            .OfType<FlowSwitch>()
            .FirstOrDefault(fs => fs.Id == "ReviewOutcome");

        reviewOutcome.Should().NotBeNull("should have ReviewOutcome FlowSwitch");
        reviewOutcome!.Cases.Should().HaveCount(5,
            "review outcome should have 5 cases: Approved, NeedsModification, Defer, Split, NeedsHuman");
    }

    [Test]
    public void ReviewOutcome_CaseNames()
    {
        var reviewOutcome = _flowchart.Activities
            .OfType<FlowSwitch>()
            .First(fs => fs.Id == "ReviewOutcome");

        var caseNames = reviewOutcome.Cases.Select(c => c.Label).ToList();
        caseNames.Should().Contain("Approved");
        caseNames.Should().Contain("NeedsModification");
        caseNames.Should().Contain("Defer");
        caseNames.Should().Contain("Split");
        caseNames.Should().Contain("NeedsHuman");
    }

    [Test]
    public void ApprovedPath_ConnectsTo_CreateTasks()
    {
        var hasConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ReviewOutcome" &&
            c.Source.Port == "Approved" &&
            c.Target.Activity.Id == "CreateTasks");

        hasConnection.Should().BeTrue("Approved outcome should connect to CreateTasks");
    }

    [Test]
    public void DeferPath_ConnectsTo_CreateDeferredIssues()
    {
        var hasConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ReviewOutcome" &&
            c.Source.Port == "Defer" &&
            c.Target.Activity.Id == "CreateDeferredIssues");

        hasConnection.Should().BeTrue("Defer outcome should connect to CreateDeferredIssues");
    }

    [Test]
    public void DeferPath_ReportDeferred_ConnectsTo_Finish()
    {
        var hasConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ReportDeferred" &&
            c.Target.Activity.Id == "Finish");

        hasConnection.Should().BeTrue("ReportDeferred should connect to Finish");
    }

    [Test]
    public void SplitPath_ConnectsTo_CreateSplitIssues()
    {
        var hasConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ReviewOutcome" &&
            c.Source.Port == "Split" &&
            c.Target.Activity.Id == "CreateSplitIssues");

        hasConnection.Should().BeTrue("Split outcome should connect to CreateSplitIssues");
    }

    [Test]
    public void SplitPath_ReportSplit_ConnectsTo_Finish()
    {
        var hasConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ReportSplit" &&
            c.Target.Activity.Id == "Finish");

        hasConnection.Should().BeTrue("ReportSplit should connect to Finish");
    }

    [Test]
    public void NeedsHumanPath_ConnectsTo_ReportNeedsHuman()
    {
        var hasConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ReviewOutcome" &&
            c.Source.Port == "NeedsHuman" &&
            c.Target.Activity.Id == "ReportNeedsHuman");

        hasConnection.Should().BeTrue("NeedsHuman outcome should connect to ReportNeedsHuman");
    }

    [Test]
    public void NeedsModificationPath_Increments_Then_ChecksMax()
    {
        // NeedsModification → IncrPlanRevision → PlanMaxRevisions
        var hasIncrConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "ReviewOutcome" &&
            c.Source.Port == "NeedsModification" &&
            c.Target.Activity.Id == "IncrPlanRevision");

        var hasMaxCheck = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "IncrPlanRevision" &&
            c.Target.Activity.Id == "PlanMaxRevisions");

        hasIncrConnection.Should().BeTrue("NeedsModification should connect to IncrPlanRevision");
        hasMaxCheck.Should().BeTrue("IncrPlanRevision should connect to PlanMaxRevisions");
    }

    [Test]
    public void PlanMaxRevisions_False_LoopsToGeneratePlan()
    {
        var hasLoop = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "PlanMaxRevisions" &&
            c.Source.Port == "False" &&
            c.Target.Activity.Id == "GeneratePlan");

        hasLoop.Should().BeTrue("PlanMaxRevisions=False should loop back to GeneratePlan");
    }

    [Test]
    public void PlanMaxRevisions_True_EscalatesToNeedsHuman()
    {
        var hasEscalation = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "PlanMaxRevisions" &&
            c.Source.Port == "True" &&
            c.Target.Activity.Id == "ReportNeedsHuman");

        hasEscalation.Should().BeTrue("PlanMaxRevisions=True should escalate to ReportNeedsHuman");
    }

    // ================================================================
    // Task Review Outcome Routing
    // ================================================================

    [Test]
    public void TaskReviewOutcome_HasThreeCases()
    {
        var taskOutcome = _flowchart.Activities
            .OfType<FlowSwitch>()
            .FirstOrDefault(fs => fs.Id == "TaskReviewOutcome");

        taskOutcome.Should().NotBeNull("should have TaskReviewOutcome FlowSwitch");
        taskOutcome!.Cases.Should().HaveCount(3,
            "task review should have 3 cases: Approved, NeedsChanges, NeedsHuman");
    }

    [Test]
    public void TaskApproved_ConnectsTo_CreateBranch()
    {
        var hasConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "TaskReviewOutcome" &&
            c.Source.Port == "Approved" &&
            c.Target.Activity.Id == "CreateBranch");

        hasConnection.Should().BeTrue("Task Approved should connect to CreateBranch");
    }

    [Test]
    public void TaskNeedsChanges_ConnectsTo_IncrTaskRevision()
    {
        var hasConnection = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "TaskReviewOutcome" &&
            c.Source.Port == "NeedsChanges" &&
            c.Target.Activity.Id == "IncrTaskRevision");

        hasConnection.Should().BeTrue("Task NeedsChanges should connect to IncrTaskRevision");
    }

    [Test]
    public void TaskMaxRevisions_False_LoopsToCreateTasks()
    {
        var hasLoop = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "TaskMaxRevisions" &&
            c.Source.Port == "False" &&
            c.Target.Activity.Id == "CreateTasks");

        hasLoop.Should().BeTrue("TaskMaxRevisions=False should loop back to CreateTasks");
    }

    [Test]
    public void TaskMaxRevisions_True_EscalatesToNeedsHuman()
    {
        var hasEscalation = _flowchart.Connections.Any(c =>
            c.Source.Activity.Id == "TaskMaxRevisions" &&
            c.Source.Port == "True" &&
            c.Target.Activity.Id == "ReportNeedsHuman");

        hasEscalation.Should().BeTrue("TaskMaxRevisions=True should escalate to ReportNeedsHuman");
    }
}
