using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL.Models;

namespace Tamma.Activities.Tests.ADL;

[TestFixture]
public class AdlModelsTests
{
    [Test]
    public void AdlIssue_DefaultValues_ShouldBeCorrect()
    {
        var issue = new AdlIssue();

        issue.Number.Should().Be(0);
        issue.Title.Should().BeEmpty();
        issue.Body.Should().BeNull();
        issue.Labels.Should().BeEmpty();
        issue.Url.Should().BeEmpty();
    }

    [Test]
    public void AdlPlan_DefaultValues_ShouldBeCorrect()
    {
        var plan = new AdlPlan();

        plan.IssueTitle.Should().BeEmpty();
        plan.IssueNumber.Should().Be(0);
        plan.Summary.Should().BeEmpty();
        plan.Steps.Should().BeEmpty();
        plan.FilesToModify.Should().BeEmpty();
        plan.FilesToCreate.Should().BeEmpty();
        plan.TestStrategy.Should().BeNull();
        plan.EstimatedComplexity.Should().BeNull();
        plan.GeneratedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void ApprovalResult_DefaultValues_ShouldBeCorrect()
    {
        var result = new ApprovalResult();

        result.Decision.Should().Be(ApprovalDecision.Approve);
        result.Feedback.Should().BeNull();
        result.EditedPlan.Should().BeNull();
        result.ApprovedBy.Should().BeNull();
        result.DecidedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Test]
    public void CycleResult_DefaultValues_ShouldBeCorrect()
    {
        var result = new CycleResult();

        result.ExitReason.Should().Be(CycleExitReason.Success);
        result.IssueNumber.Should().BeNull();
        result.IssueTitle.Should().BeNull();
        result.BranchName.Should().BeNull();
        result.PrNumber.Should().BeNull();
        result.PrUrl.Should().BeNull();
        result.MergeSha.Should().BeNull();
        result.ErrorMessage.Should().BeNull();
        result.CompletedAt.Should().BeNull();
    }

    [Test]
    public void AdlConfig_DefaultValues_ShouldBeCorrect()
    {
        var config = new AdlConfig();

        config.Repository.Should().BeEmpty();
        config.IssueLabels.Should().ContainSingle("tamma-auto");
        config.BotAssignee.Should().Be("tamma-bot");
        config.BaseBranch.Should().Be("main");
        config.MaxIssuesPerRun.Should().Be(10);
        config.CooldownSeconds.Should().Be(10);
        config.Limits.Should().NotBeNull();
    }

    [Test]
    public void OperationalLimits_DefaultValues_ShouldBeCorrect()
    {
        var limits = new OperationalLimits();

        limits.DailyIssueQuota.Should().Be(20);
        limits.DailyBudgetUsd.Should().Be(50.0m);
        limits.EmergencyStop.Should().BeFalse();
        limits.MaxCycleDuration.Should().Be(TimeSpan.FromHours(2));
    }

    [Test]
    public void ReviewAnalysisResult_DefaultValues_ShouldBeCorrect()
    {
        var result = new ReviewAnalysisResult();

        result.HasActionableComments.Should().BeFalse();
        result.TotalComments.Should().Be(0);
        result.ActionableComments.Should().Be(0);
        result.FixItems.Should().BeEmpty();
        result.Summary.Should().BeNull();
    }

    [Test]
    public void ReviewFixItem_DefaultValues_ShouldBeCorrect()
    {
        var item = new ReviewFixItem();

        item.FilePath.Should().BeEmpty();
        item.Line.Should().BeNull();
        item.Comment.Should().BeEmpty();
        item.SuggestedFix.Should().BeNull();
        item.Priority.Should().Be("normal");
    }

    [TestCase(CycleExitReason.Success)]
    [TestCase(CycleExitReason.NoIssues)]
    [TestCase(CycleExitReason.PlanRejected)]
    [TestCase(CycleExitReason.TddFailed)]
    [TestCase(CycleExitReason.CiFailed)]
    [TestCase(CycleExitReason.MergeRejected)]
    [TestCase(CycleExitReason.MergeFailed)]
    [TestCase(CycleExitReason.Error)]
    public void CycleExitReason_AllValues_ShouldBeDefined(CycleExitReason reason)
    {
        Enum.IsDefined(typeof(CycleExitReason), reason).Should().BeTrue();
    }

    [TestCase(ApprovalDecision.Approve)]
    [TestCase(ApprovalDecision.Reject)]
    [TestCase(ApprovalDecision.Edit)]
    [TestCase(ApprovalDecision.Test)]
    public void ApprovalDecision_AllValues_ShouldBeDefined(ApprovalDecision decision)
    {
        Enum.IsDefined(typeof(ApprovalDecision), decision).Should().BeTrue();
    }
}
