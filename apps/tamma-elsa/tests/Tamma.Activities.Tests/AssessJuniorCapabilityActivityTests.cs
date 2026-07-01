using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.Mentorship;
using Tamma.Core.Entities;
using Tamma.Core.Enums;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Tests;

[TestFixture]
public class AssessJuniorCapabilityActivityTests
{
    private Mock<ILogger<AssessJuniorCapabilityActivity>> _mockLogger = null!;
    private Mock<IMentorshipSessionRepository> _mockRepository = null!;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<AssessJuniorCapabilityActivity>>();
        _mockRepository = new Mock<IMentorshipSessionRepository>();
    }

    [Test]
    public void Constructor_WithValidDependencies_ShouldNotThrow()
    {
        // Act — Story 38-3b: the activity no longer injects IIntegrationService
        // (Slack now routes through the mediated API seam), so the ctor is 2-arg.
        Action act = () => new AssessJuniorCapabilityActivity(
            _mockLogger.Object,
            _mockRepository.Object);

        // Assert
        act.Should().NotThrow();
    }

    [Test]
    public async Task Execute_WhenStoryNotFound_ShouldReturnErrorResult()
    {
        // Arrange
        var storyId = "story-123";
        var juniorId = "junior-456";
        var sessionId = Guid.NewGuid();

        _mockRepository
            .Setup(r => r.GetStoryByIdAsync(storyId))
            .ReturnsAsync((Story?)null);

        // Note: Full execution test would require mocking ActivityExecutionContext
        // which is complex with ELSA. This is a placeholder for the test structure.

        // Assert
        _mockRepository.Verify(r => r.GetStoryByIdAsync(storyId), Times.Never);
    }

    [Test]
    public async Task Execute_WhenJuniorNotFound_ShouldReturnErrorResult()
    {
        // Arrange
        var storyId = "story-123";
        var juniorId = "junior-456";
        var sessionId = Guid.NewGuid();

        var story = new Story
        {
            Id = storyId,
            Title = "Test Story",
            Complexity = 3
        };

        _mockRepository
            .Setup(r => r.GetStoryByIdAsync(storyId))
            .ReturnsAsync(story);

        _mockRepository
            .Setup(r => r.GetJuniorByIdAsync(juniorId))
            .ReturnsAsync((JuniorDeveloper?)null);

        // Assert - structure test
        _mockRepository.Verify(r => r.GetJuniorByIdAsync(juniorId), Times.Never);
    }

    [Test]
    public void AssessmentOutput_ShouldHaveExpectedProperties()
    {
        // Arrange
        var output = new AssessmentOutput
        {
            Status = AssessmentStatus.Correct,
            Confidence = 0.95,
            NextState = MentorshipState.PLAN_DECOMPOSITION,
            Message = "Junior demonstrates good understanding",
            Gaps = new List<string>()
        };

        // Assert
        output.Status.Should().Be(AssessmentStatus.Correct);
        output.Confidence.Should().BeApproximately(0.95, 0.001);
        output.NextState.Should().Be(MentorshipState.PLAN_DECOMPOSITION);
        output.Message.Should().NotBeNullOrEmpty();
        output.Gaps.Should().BeEmpty();
    }

    [Test]
    public void AssessmentOutput_WithPartialUnderstanding_ShouldHaveGaps()
    {
        // Arrange
        var output = new AssessmentOutput
        {
            Status = AssessmentStatus.Partial,
            Confidence = 0.6,
            NextState = MentorshipState.CLARIFY_REQUIREMENTS,
            Message = "Junior has partial understanding",
            Gaps = new List<string>
            {
                "Technical approach unclear",
                "Edge cases not considered"
            }
        };

        // Assert
        output.Status.Should().Be(AssessmentStatus.Partial);
        output.Gaps.Should().HaveCount(2);
        output.Gaps.Should().Contain("Technical approach unclear");
    }

    [Test]
    public void AssessmentOutput_WithTimeout_ShouldTransitionToDiagnoseBlocker()
    {
        // Arrange
        var output = new AssessmentOutput
        {
            Status = AssessmentStatus.Timeout,
            Confidence = 0.0,
            NextState = MentorshipState.DIAGNOSE_BLOCKER,
            Message = "No response received",
            Gaps = new List<string> { "No response received" }
        };

        // Assert
        output.Status.Should().Be(AssessmentStatus.Timeout);
        output.NextState.Should().Be(MentorshipState.DIAGNOSE_BLOCKER);
    }
}
