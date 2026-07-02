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
public class DiagnoseBlockerActivityTests
{
    private Mock<ILogger<DiagnoseBlockerActivity>> _mockLogger = null!;
    private Mock<IMentorshipSessionRepository> _mockRepository = null!;
    // Story 38 (Phase 2, Batch C): the composite IIntegrationService injection is gone; the
    // diagnostic git-commit / build-status / test-trigger reads now route through the thin
    // TammaApiClient. A throwaway real client (HttpClient + NullLogger, never hit) proves the
    // ctor resolves the thin client.
    private TammaApiClient _apiClient = null!;
    private Mock<IAnalyticsService> _mockAnalyticsService = null!;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<DiagnoseBlockerActivity>>();
        _mockRepository = new Mock<IMentorshipSessionRepository>();
        _apiClient = new TammaApiClient(new HttpClient(), NullLogger<TammaApiClient>.Instance, null, null);
        _mockAnalyticsService = new Mock<IAnalyticsService>();
    }

    [Test]
    public void Constructor_WithValidDependencies_ShouldNotThrow()
    {
        // Act
        Action act = () => new DiagnoseBlockerActivity(
            _mockLogger.Object,
            _mockRepository.Object,
            _apiClient,
            _mockAnalyticsService.Object);

        // Assert
        act.Should().NotThrow();
    }

    [Test]
    public void BlockerDiagnosisOutput_ShouldHaveExpectedDefaultValues()
    {
        // Arrange
        var output = new BlockerDiagnosisOutput();

        // Assert
        output.BlockerType.Should().Be(BlockerType.REQUIREMENTS_UNCLEAR);
        output.Severity.Should().Be(BlockerSeverity.Low);
        output.Description.Should().BeEmpty();
        output.NextState.Should().Be(MentorshipState.INIT_STORY_PROCESSING);
        output.RelatedResources.Should().BeEmpty();
    }

    [Test]
    public void BlockerDiagnosisOutput_WithBuildFailure_ShouldIndicateTechnicalIssue()
    {
        // Arrange
        var output = new BlockerDiagnosisOutput
        {
            BlockerType = BlockerType.TECHNICAL_KNOWLEDGE_GAP,
            Severity = BlockerSeverity.Medium,
            Description = "Build is failing: error CS1002: ; expected",
            RootCause = "Syntax or compilation error in code",
            SuggestedAction = "Review compiler errors and fix syntax issues",
            NextState = MentorshipState.PROVIDE_HINT,
            Message = "Build failure detected"
        };

        // Assert
        output.BlockerType.Should().Be(BlockerType.TECHNICAL_KNOWLEDGE_GAP);
        output.Severity.Should().Be(BlockerSeverity.Medium);
        output.NextState.Should().Be(MentorshipState.PROVIDE_HINT);
        output.SuggestedAction.Should().Contain("syntax");
    }

    [Test]
    public void BlockerDiagnosisOutput_WithTestingChallenge_ShouldProvideTestGuidance()
    {
        // Arrange
        var output = new BlockerDiagnosisOutput
        {
            BlockerType = BlockerType.TESTING_CHALLENGE,
            Severity = BlockerSeverity.Medium,
            Description = "Multiple tests failing: Test1, Test2, Test3",
            RootCause = "Struggling to understand test requirements",
            SuggestedAction = "Provide test-specific guidance and debugging tips",
            NextState = MentorshipState.PROVIDE_GUIDANCE,
            Message = "Testing challenges detected"
        };

        // Assert
        output.BlockerType.Should().Be(BlockerType.TESTING_CHALLENGE);
        output.NextState.Should().Be(MentorshipState.PROVIDE_GUIDANCE);
        output.Description.Should().Contain("tests failing");
    }

    [Test]
    public void BlockerDiagnosisOutput_WithMotivationIssue_ShouldProvideEncouragement()
    {
        // Arrange
        var output = new BlockerDiagnosisOutput
        {
            BlockerType = BlockerType.MOTIVATION_ISSUE,
            Severity = BlockerSeverity.Medium,
            Description = "Prolonged inactivity with signs of frustration",
            RootCause = "Junior may be feeling overwhelmed or stuck",
            SuggestedAction = "Reach out with encouragement and offer direct assistance",
            NextState = MentorshipState.PROVIDE_ASSISTANCE,
            Message = "Motivation blocker detected"
        };

        // Assert
        output.BlockerType.Should().Be(BlockerType.MOTIVATION_ISSUE);
        output.NextState.Should().Be(MentorshipState.PROVIDE_ASSISTANCE);
        output.SuggestedAction.Should().Contain("encouragement");
    }

    [Test]
    public void BlockerSeverity_ShouldHaveExpectedValues()
    {
        // Assert
        Enum.GetValues<BlockerSeverity>().Should().HaveCount(4);
        Enum.GetValues<BlockerSeverity>().Should().Contain(BlockerSeverity.Low);
        Enum.GetValues<BlockerSeverity>().Should().Contain(BlockerSeverity.Medium);
        Enum.GetValues<BlockerSeverity>().Should().Contain(BlockerSeverity.High);
        Enum.GetValues<BlockerSeverity>().Should().Contain(BlockerSeverity.Critical);
    }

    [Test]
    public void DiagnosticData_ShouldStoreAllRelevantInformation()
    {
        // Arrange
        var data = new DiagnosticData
        {
            StoryComplexity = 4,
            JuniorSkillLevel = 2,
            RecentCommitCount = 3,
            LastCommitTime = DateTime.UtcNow.AddMinutes(-30),
            TimeSinceLastActivity = TimeSpan.FromMinutes(30),
            BuildStatus = "Failed",
            BuildError = "error CS0001: Compilation failed",
            FailingTestCount = 5,
            FailingTests = new List<string> { "Test1", "Test2", "Test3", "Test4", "Test5" }
        };

        // Assert
        data.StoryComplexity.Should().Be(4);
        data.JuniorSkillLevel.Should().Be(2);
        data.RecentCommitCount.Should().Be(3);
        data.TimeSinceLastActivity.TotalMinutes.Should().Be(30);
        data.BuildStatus.Should().Be("Failed");
        data.FailingTestCount.Should().Be(5);
        data.FailingTests.Should().HaveCount(5);
    }

    [Test]
    public void BlockerType_ShouldCoverAllScenarios()
    {
        // Assert - verify all blocker types are defined
        Enum.GetValues<BlockerType>().Should().Contain(BlockerType.REQUIREMENTS_UNCLEAR);
        Enum.GetValues<BlockerType>().Should().Contain(BlockerType.TECHNICAL_KNOWLEDGE_GAP);
        Enum.GetValues<BlockerType>().Should().Contain(BlockerType.ENVIRONMENT_ISSUE);
        Enum.GetValues<BlockerType>().Should().Contain(BlockerType.DEPENDENCY_ISSUE);
        Enum.GetValues<BlockerType>().Should().Contain(BlockerType.ARCHITECTURE_CONFUSION);
        Enum.GetValues<BlockerType>().Should().Contain(BlockerType.TESTING_CHALLENGE);
        Enum.GetValues<BlockerType>().Should().Contain(BlockerType.MOTIVATION_ISSUE);
        Enum.GetValues<BlockerType>().Should().Contain(BlockerType.UNKNOWN);
    }
}
