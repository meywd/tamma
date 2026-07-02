using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Activities.LlmCall;
using Tamma.Activities.Mentorship;
using Tamma.Core.Entities;
using Tamma.Core.Enums;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Tests;

[TestFixture]
public class MergeCompleteActivityTests
{
    private Mock<ILogger<MergeCompleteActivity>> _mockLogger = null!;
    private Mock<IMentorshipSessionRepository> _mockRepository = null!;
    // Story 38 (Phase 2, Batch C): the composite IIntegrationService injection is gone;
    // the merge + JIRA update now route through the thin TammaApiClient. A throwaway real
    // client (HttpClient + NullLogger, never hit) proves the ctor resolves the thin client.
    private TammaApiClient _apiClient = null!;
    private Mock<IAnalyticsService> _mockAnalyticsService = null!;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<MergeCompleteActivity>>();
        _mockRepository = new Mock<IMentorshipSessionRepository>();
        _apiClient = new TammaApiClient(new HttpClient(), NullLogger<TammaApiClient>.Instance, null, null);
        _mockAnalyticsService = new Mock<IAnalyticsService>();
    }

    [Test]
    public void Constructor_WithValidDependencies_ShouldNotThrow()
    {
        // Act
        Action act = () => new MergeCompleteActivity(
            _mockLogger.Object,
            _mockRepository.Object,
            _apiClient,
            _mockAnalyticsService.Object);

        // Assert
        act.Should().NotThrow();
    }

    [Test]
    public void MergeCompleteOutput_ShouldHaveExpectedDefaultValues()
    {
        // Arrange
        var output = new MergeCompleteOutput();

        // Assert
        output.Success.Should().BeFalse();
        output.MergeSha.Should().BeNull();
        output.MergeSuccessful.Should().BeFalse();
        output.Report.Should().BeNull();
        output.SkillUpdate.Should().BeNull();
    }

    [Test]
    public void MergeCompleteOutput_SuccessfulCompletion_ShouldContainAllDetails()
    {
        // Arrange
        var report = new SessionReport
        {
            SessionId = Guid.NewGuid(),
            StoryId = "STORY-123",
            StoryTitle = "Implement User Login",
            JuniorId = "junior-456",
            JuniorName = "Jane Developer",
            StartTime = DateTime.UtcNow.AddHours(-5),
            EndTime = DateTime.UtcNow,
            Duration = TimeSpan.FromHours(5),
            TotalEvents = 25,
            BlockerCount = 2,
            GuidanceProvided = 3,
            EstimatedHours = 6,
            ActualHours = 5,
            OverallScore = 85,
            Strengths = new List<string> { "Good code quality", "Completed ahead of time" },
            AreasForImprovement = new List<string> { "Consider writing tests earlier" }
        };

        var skillUpdate = new SkillUpdateResult
        {
            OldSkillLevel = 2,
            NewSkillLevel = 3,
            ShouldUpdateSkill = true,
            Reason = "Excellent performance across multiple sessions"
        };

        var output = new MergeCompleteOutput
        {
            Success = true,
            MergeSha = "abc123def456",
            MergeSuccessful = true,
            Report = report,
            SkillUpdate = skillUpdate,
            Message = "Mentorship session completed successfully!"
        };

        // Assert
        output.Success.Should().BeTrue();
        output.MergeSuccessful.Should().BeTrue();
        output.Report.Should().NotBeNull();
        output.Report!.OverallScore.Should().Be(85);
        output.SkillUpdate.Should().NotBeNull();
        output.SkillUpdate!.ShouldUpdateSkill.Should().BeTrue();
    }

    [Test]
    public void SessionReport_ShouldCalculateCorrectDuration()
    {
        // Arrange
        var startTime = DateTime.UtcNow.AddHours(-3);
        var endTime = DateTime.UtcNow;

        var report = new SessionReport
        {
            StartTime = startTime,
            EndTime = endTime,
            Duration = endTime - startTime,
            ActualHours = 3
        };

        // Assert
        report.Duration.TotalHours.Should().BeApproximately(3, 0.1);
        report.ActualHours.Should().Be(3);
    }

    [Test]
    public void SessionReport_WithStrengthsAndImprovements_ShouldContainBoth()
    {
        // Arrange
        var report = new SessionReport
        {
            Strengths = new List<string>
            {
                "Completed without major blockers",
                "Good code quality - few iterations needed"
            },
            AreasForImprovement = new List<string>
            {
                "Time management could be improved"
            },
            OverallScore = 75
        };

        // Assert
        report.Strengths.Should().HaveCount(2);
        report.AreasForImprovement.Should().HaveCount(1);
        report.OverallScore.Should().Be(75);
    }

    [Test]
    public void SkillUpdateResult_NoUpdate_ShouldIndicateNoChange()
    {
        // Arrange
        var skillUpdate = new SkillUpdateResult
        {
            OldSkillLevel = 3,
            NewSkillLevel = 3,
            ShouldUpdateSkill = false,
            Reason = "No change warranted"
        };

        // Assert
        skillUpdate.ShouldUpdateSkill.Should().BeFalse();
        skillUpdate.OldSkillLevel.Should().Be(skillUpdate.NewSkillLevel);
    }

    [Test]
    public void SkillUpdateResult_WithUpgrade_ShouldContainJustification()
    {
        // Arrange
        var skillUpdate = new SkillUpdateResult
        {
            OldSkillLevel = 2,
            NewSkillLevel = 3,
            ShouldUpdateSkill = true,
            Reason = "Consistent good performance indicates growth",
            SkillGaps = new List<string> { "System design", "Performance optimization" },
            RecommendedLearning = new List<string>
            {
                "Advanced design patterns",
                "Database optimization techniques"
            }
        };

        // Assert
        skillUpdate.ShouldUpdateSkill.Should().BeTrue();
        skillUpdate.NewSkillLevel.Should().BeGreaterThan(skillUpdate.OldSkillLevel);
        skillUpdate.Reason.Should().NotBeEmpty();
        skillUpdate.SkillGaps.Should().HaveCount(2);
        skillUpdate.RecommendedLearning.Should().HaveCount(2);
    }

    [Test]
    public void SessionReport_StateTransitions_ShouldTrackHistory()
    {
        // Arrange
        var report = new SessionReport
        {
            StateTransitions = new Dictionary<string, int>
            {
                { "INIT_STORY_PROCESSING->ASSESS_JUNIOR_CAPABILITY", 1 },
                { "ASSESS_JUNIOR_CAPABILITY->PLAN_DECOMPOSITION", 1 },
                { "PLAN_DECOMPOSITION->START_IMPLEMENTATION", 1 },
                { "START_IMPLEMENTATION->MONITOR_PROGRESS", 3 },
                { "MONITOR_PROGRESS->QUALITY_GATE_CHECK", 1 }
            }
        };

        // Assert
        report.StateTransitions.Should().HaveCount(5);
        report.StateTransitions["START_IMPLEMENTATION->MONITOR_PROGRESS"].Should().Be(3);
    }

    [Test]
    public void MergeCompleteOutput_FailedMerge_ShouldStillSucceedOverall()
    {
        // Arrange - PR can be merged manually
        var output = new MergeCompleteOutput
        {
            Success = true,
            MergeSuccessful = false, // Auto-merge failed
            Message = "Session completed, but auto-merge failed. PR can be merged manually."
        };

        // Assert
        output.Success.Should().BeTrue();
        output.MergeSuccessful.Should().BeFalse();
    }

    [Test]
    public void SessionReport_OverallScore_ShouldBeInValidRange()
    {
        // Arrange
        var report = new SessionReport
        {
            OverallScore = 85.5
        };

        // Assert
        report.OverallScore.Should().BeInRange(0, 100);
    }
}
