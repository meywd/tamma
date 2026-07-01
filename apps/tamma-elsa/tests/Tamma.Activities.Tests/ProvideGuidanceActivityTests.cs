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
public class ProvideGuidanceActivityTests
{
    private Mock<ILogger<ProvideGuidanceActivity>> _mockLogger = null!;
    private Mock<IMentorshipSessionRepository> _mockRepository = null!;
    private Mock<IAnalyticsService> _mockAnalyticsService = null!;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<ProvideGuidanceActivity>>();
        _mockRepository = new Mock<IMentorshipSessionRepository>();
        _mockAnalyticsService = new Mock<IAnalyticsService>();
    }

    [Test]
    public void Constructor_WithValidDependencies_ShouldNotThrow()
    {
        // Act — Story 38-3b: the activity no longer injects IIntegrationService
        // (Slack now routes through the mediated API seam), so the ctor is 3-arg.
        Action act = () => new ProvideGuidanceActivity(
            _mockLogger.Object,
            _mockRepository.Object,
            _mockAnalyticsService.Object);

        // Assert
        act.Should().NotThrow();
    }

    [Test]
    public void GuidanceOutput_ShouldHaveExpectedDefaultValues()
    {
        // Arrange
        var output = new GuidanceOutput();

        // Assert
        output.Success.Should().BeFalse();
        output.GuidanceProvided.Should().BeEmpty();
        output.GuidanceLevel.Should().Be(0);
        output.Examples.Should().BeEmpty();
        output.Resources.Should().BeEmpty();
        output.NextSteps.Should().BeEmpty();
    }

    [Test]
    public void GuidanceOutput_WithSuccessfulGuidance_ShouldContainAllFields()
    {
        // Arrange
        var output = new GuidanceOutput
        {
            Success = true,
            GuidanceProvided = "Let's break this problem down step by step",
            GuidanceLevel = 2,
            Examples = new List<string>
            {
                "Example 1: Simple implementation",
                "Example 2: With error handling"
            },
            Resources = new List<Resource>
            {
                new Resource
                {
                    Title = "Documentation",
                    Url = "https://docs.example.com",
                    Type = ResourceType.Documentation
                }
            },
            NextSteps = new List<string>
            {
                "Step 1: Define the interface",
                "Step 2: Implement the logic",
                "Step 3: Add tests"
            },
            NextState = MentorshipState.MONITOR_PROGRESS
        };

        // Assert
        output.Success.Should().BeTrue();
        output.GuidanceLevel.Should().Be(2);
        output.Examples.Should().HaveCount(2);
        output.Resources.Should().HaveCount(1);
        output.NextSteps.Should().HaveCount(3);
        output.NextState.Should().Be(MentorshipState.MONITOR_PROGRESS);
    }

    [Test]
    public void GuidanceContent_ShouldStoreGuidanceComponents()
    {
        // Arrange
        var content = new GuidanceContent
        {
            MainGuidance = "Take another look at the acceptance criteria",
            Examples = new List<string> { "Example 1", "Example 2" },
            NextSteps = new List<string> { "Step 1", "Step 2" }
        };

        // Assert
        content.MainGuidance.Should().NotBeEmpty();
        content.Examples.Should().HaveCount(2);
        content.NextSteps.Should().HaveCount(2);
    }

    [Test]
    public void Resource_ShouldStoreResourceInformation()
    {
        // Arrange
        var resource = new Resource
        {
            Title = "Team Knowledge Base",
            Url = "https://wiki.internal/tech-guides",
            Type = ResourceType.Documentation
        };

        // Assert
        resource.Title.Should().Be("Team Knowledge Base");
        resource.Url.Should().StartWith("https://");
        resource.Type.Should().Be(ResourceType.Documentation);
    }

    [Test]
    public void ResourceType_ShouldHaveExpectedValues()
    {
        // Assert
        Enum.GetValues<ResourceType>().Should().Contain(ResourceType.Documentation);
        Enum.GetValues<ResourceType>().Should().Contain(ResourceType.Guide);
        Enum.GetValues<ResourceType>().Should().Contain(ResourceType.Video);
        Enum.GetValues<ResourceType>().Should().Contain(ResourceType.CodeExample);
        Enum.GetValues<ResourceType>().Should().Contain(ResourceType.Tool);
    }

    [Test]
    public void GuidanceLevel_HintLevel_ShouldTransitionToProvideHint()
    {
        // Arrange - Level 1 is hint
        var output = new GuidanceOutput
        {
            Success = true,
            GuidanceLevel = 1,
            GuidanceProvided = "Check the error message carefully",
            NextState = MentorshipState.PROVIDE_HINT
        };

        // Assert
        output.GuidanceLevel.Should().Be(1);
        output.NextState.Should().Be(MentorshipState.PROVIDE_HINT);
    }

    [Test]
    public void GuidanceLevel_GuidanceLevel_ShouldTransitionToProvideGuidance()
    {
        // Arrange - Level 2 is guidance
        var output = new GuidanceOutput
        {
            Success = true,
            GuidanceLevel = 2,
            GuidanceProvided = "Here's a detailed explanation of the concept",
            NextState = MentorshipState.PROVIDE_GUIDANCE
        };

        // Assert
        output.GuidanceLevel.Should().Be(2);
    }

    [Test]
    public void GuidanceLevel_AssistanceLevel_ShouldTransitionToProvideAssistance()
    {
        // Arrange - Level 3 is direct assistance
        var output = new GuidanceOutput
        {
            Success = true,
            GuidanceLevel = 3,
            GuidanceProvided = "Let's pair on this and walk through it together",
            NextState = MentorshipState.START_IMPLEMENTATION
        };

        // Assert
        output.GuidanceLevel.Should().Be(3);
        output.NextState.Should().Be(MentorshipState.START_IMPLEMENTATION);
    }

    [Test]
    public void GuidanceOutput_FailedGuidance_ShouldEscalate()
    {
        // Arrange
        var output = new GuidanceOutput
        {
            Success = false,
            GuidanceProvided = "Unable to provide guidance - missing context",
            NextState = MentorshipState.ESCALATE_TO_SENIOR
        };

        // Assert
        output.Success.Should().BeFalse();
        output.NextState.Should().Be(MentorshipState.ESCALATE_TO_SENIOR);
    }
}
