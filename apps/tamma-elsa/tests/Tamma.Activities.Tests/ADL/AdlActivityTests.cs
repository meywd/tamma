using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.Activities.ADL.Models;
using Tamma.Core.Interfaces;

namespace Tamma.Activities.Tests.ADL;

[TestFixture]
public class AdlActivityTests
{
    // ================================================================
    // SelectIssueActivity
    // ================================================================

    [Test]
    public void SelectIssueActivity_JsonConstructor_ShouldNotThrow()
    {
        Action act = () => new SelectIssueActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void SelectIssueActivity_WithDependencies_ShouldNotThrow()
    {
        var logger = new Mock<ILogger<SelectIssueActivity>>();
        var github = new Mock<IGitHubIntegrationService>();

        Action act = () => new SelectIssueActivity(logger.Object, github.Object);
        act.Should().NotThrow();
    }

    // ================================================================
    // WaitForPlanApprovalActivity
    // ================================================================

    [Test]
    public void WaitForPlanApprovalActivity_JsonConstructor_ShouldNotThrow()
    {
        Action act = () => new WaitForPlanApprovalActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void WaitForPlanApprovalActivity_WithLogger_ShouldNotThrow()
    {
        var logger = new Mock<ILogger<WaitForPlanApprovalActivity>>();

        Action act = () => new WaitForPlanApprovalActivity(logger.Object);
        act.Should().NotThrow();
    }

    // ================================================================
    // CreateBranchActivity
    // ================================================================

    [Test]
    public void CreateBranchActivity_JsonConstructor_ShouldNotThrow()
    {
        Action act = () => new CreateBranchActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void CreateBranchActivity_WithDependencies_ShouldNotThrow()
    {
        var logger = new Mock<ILogger<CreateBranchActivity>>();
        var github = new Mock<IGitHubIntegrationService>();

        Action act = () => new CreateBranchActivity(logger.Object, github.Object);
        act.Should().NotThrow();
    }

    // ================================================================
    // CreatePullRequestActivity
    // ================================================================

    [Test]
    public void CreatePullRequestActivity_JsonConstructor_ShouldNotThrow()
    {
        Action act = () => new CreatePullRequestActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void CreatePullRequestActivity_WithDependencies_ShouldNotThrow()
    {
        var logger = new Mock<ILogger<CreatePullRequestActivity>>();
        var github = new Mock<IGitHubIntegrationService>();

        Action act = () => new CreatePullRequestActivity(logger.Object, github.Object);
        act.Should().NotThrow();
    }

    // ================================================================
    // AnalyzeReviewActivity
    // ================================================================

    [Test]
    public void AnalyzeReviewActivity_JsonConstructor_ShouldNotThrow()
    {
        Action act = () => new AnalyzeReviewActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void AnalyzeReviewActivity_WithDependencies_ShouldNotThrow()
    {
        var logger = new Mock<ILogger<AnalyzeReviewActivity>>();
        var github = new Mock<IGitHubIntegrationService>();

        Action act = () => new AnalyzeReviewActivity(logger.Object, github.Object);
        act.Should().NotThrow();
    }

    // ================================================================
    // ApplyReviewFixesActivity
    // ================================================================

    [Test]
    public void ApplyReviewFixesActivity_JsonConstructor_ShouldNotThrow()
    {
        Action act = () => new ApplyReviewFixesActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void ApplyReviewFixesActivity_WithLogger_ShouldNotThrow()
    {
        var logger = new Mock<ILogger<ApplyReviewFixesActivity>>();

        Action act = () => new ApplyReviewFixesActivity(logger.Object);
        act.Should().NotThrow();
    }

    // ================================================================
    // WaitForMergeApprovalActivity
    // ================================================================

    [Test]
    public void WaitForMergeApprovalActivity_JsonConstructor_ShouldNotThrow()
    {
        Action act = () => new WaitForMergeApprovalActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void WaitForMergeApprovalActivity_WithLogger_ShouldNotThrow()
    {
        var logger = new Mock<ILogger<WaitForMergeApprovalActivity>>();

        Action act = () => new WaitForMergeApprovalActivity(logger.Object);
        act.Should().NotThrow();
    }

    // ================================================================
    // MergePullRequestActivity
    // ================================================================

    [Test]
    public void MergePullRequestActivity_JsonConstructor_ShouldNotThrow()
    {
        Action act = () => new MergePullRequestActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void MergePullRequestActivity_WithDependencies_ShouldNotThrow()
    {
        var logger = new Mock<ILogger<MergePullRequestActivity>>();
        var github = new Mock<IGitHubIntegrationService>();

        Action act = () => new MergePullRequestActivity(logger.Object, github.Object);
        act.Should().NotThrow();
    }

    // ================================================================
    // CheckLimitsActivity
    // ================================================================

    [Test]
    public void CheckLimitsActivity_JsonConstructor_ShouldNotThrow()
    {
        Action act = () => new CheckLimitsActivity();
        act.Should().NotThrow();
    }

    [Test]
    public void CheckLimitsActivity_WithLogger_ShouldNotThrow()
    {
        var logger = new Mock<ILogger<CheckLimitsActivity>>();

        Action act = () => new CheckLimitsActivity(logger.Object);
        act.Should().NotThrow();
    }

    // ================================================================
    // Config serialization
    // ================================================================

    [Test]
    public void AdlConfig_Serialization_ShouldRoundTrip()
    {
        var config = new AdlConfig
        {
            Repository = "owner/repo",
            IssueLabels = new[] { "tamma-auto", "bug" },
            BotAssignee = "tamma-bot",
            BaseBranch = "main",
            MaxIssuesPerRun = 5,
            CooldownSeconds = 15,
            Limits = new OperationalLimits
            {
                DailyIssueQuota = 10,
                DailyBudgetUsd = 25.0m,
                EmergencyStop = false
            }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(config);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<AdlConfig>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Repository.Should().Be("owner/repo");
        deserialized.IssueLabels.Should().HaveCount(2);
        deserialized.MaxIssuesPerRun.Should().Be(5);
        deserialized.Limits.DailyIssueQuota.Should().Be(10);
        deserialized.Limits.DailyBudgetUsd.Should().Be(25.0m);
    }
}
