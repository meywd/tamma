using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Activities.AI;
using Tamma.Core.Interfaces;
using Tamma.Data.Repositories;

namespace Tamma.Activities.Tests.AI;

[TestFixture]
public class ContextGatheringActivityTests
{
    private Mock<ILogger<ContextGatheringActivity>> _mockLogger = null!;
    private Mock<IMentorshipSessionRepository> _mockRepository = null!;
    private Mock<IIntegrationService> _mockIntegrationService = null!;
    private Mock<IHttpClientFactory> _mockHttpClientFactory = null!;
    private Mock<IConfiguration> _mockConfiguration = null!;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<ContextGatheringActivity>>();
        _mockRepository = new Mock<IMentorshipSessionRepository>();
        _mockIntegrationService = new Mock<IIntegrationService>();
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _mockConfiguration = new Mock<IConfiguration>();
    }

    [Test]
    public void Constructor_WithValidDependencies_ShouldNotThrow()
    {
        // Act
        Action act = () => new ContextGatheringActivity(
            _mockLogger.Object,
            _mockRepository.Object,
            _mockIntegrationService.Object,
            _mockHttpClientFactory.Object,
            _mockConfiguration.Object);

        // Assert
        act.Should().NotThrow();
    }

    [Test]
    public void CodeContextOutput_ShouldHaveExpectedDefaultValues()
    {
        // Arrange
        var output = new CodeContextOutput();

        // Assert
        output.Success.Should().BeFalse();
        output.StoryId.Should().BeEmpty();
        output.RecentChanges.Should().BeEmpty();
        output.FileContents.Should().BeEmpty();
        output.SimilarPatterns.Should().BeEmpty();
        output.AcceptanceCriteria.Should().BeEmpty();
        output.TechnicalRequirements.Should().BeEmpty();
    }

    [Test]
    public void CodeContextOutput_WithFullContext_ShouldContainAllFields()
    {
        // Arrange
        var output = new CodeContextOutput
        {
            Success = true,
            StoryId = "STORY-123",
            StoryTitle = "Implement User Login",
            StoryDescription = "Create login functionality with JWT authentication",
            AcceptanceCriteria = new List<string>
            {
                "User can enter email and password",
                "System validates credentials",
                "JWT token is returned on success"
            },
            TechnicalRequirements = new Dictionary<string, string>
            {
                { "framework", ".NET 8" },
                { "database", "PostgreSQL" }
            },
            RecentChanges = new List<FileChange>
            {
                new FileChange
                {
                    FilePath = "src/Controllers/AuthController.cs",
                    CommitSha = "abc123",
                    CommitMessage = "Add login endpoint",
                    Author = "junior-dev",
                    Timestamp = DateTime.UtcNow.AddHours(-2)
                }
            },
            FileContents = new List<FileContent>
            {
                new FileContent
                {
                    FilePath = "src/Controllers/AuthController.cs",
                    Content = "public class AuthController { }",
                    Language = "csharp",
                    LineCount = 50
                }
            },
            SimilarPatterns = new List<SimilarPattern>
            {
                new SimilarPattern
                {
                    PatternName = "Controller Pattern",
                    FilePath = "src/Controllers/UserController.cs",
                    Description = "Example REST controller",
                    Relevance = 0.85
                }
            },
            TestContext = new TestContextInfo
            {
                TotalTests = 10,
                PassingTests = 8,
                FailingTests = 2,
                CoveragePercentage = 75.5
            },
            ContextSummary = "Story: Implement User Login | Files: 1 | Tests: 8/10 passing",
            TotalContextSize = 2500
        };

        // Assert
        output.Success.Should().BeTrue();
        output.StoryId.Should().Be("STORY-123");
        output.AcceptanceCriteria.Should().HaveCount(3);
        output.TechnicalRequirements.Should().HaveCount(2);
        output.RecentChanges.Should().HaveCount(1);
        output.FileContents.Should().HaveCount(1);
        output.SimilarPatterns.Should().HaveCount(1);
        output.TestContext.Should().NotBeNull();
        output.TestContext!.PassingTests.Should().Be(8);
    }

    [Test]
    public void FileChange_ShouldStoreChangeInformation()
    {
        // Arrange
        var change = new FileChange
        {
            FilePath = "src/Services/AuthService.cs",
            CommitSha = "def456",
            CommitMessage = "Add authentication logic",
            Author = "jane-doe",
            Timestamp = DateTime.UtcNow
        };

        // Assert
        change.FilePath.Should().Contain("AuthService");
        change.CommitSha.Should().HaveLength(6);
        change.CommitMessage.Should().NotBeEmpty();
        change.Author.Should().NotBeEmpty();
    }

    [Test]
    public void FileContent_ShouldStoreContentWithMetadata()
    {
        // Arrange
        var content = new FileContent
        {
            FilePath = "src/Models/User.cs",
            Content = "public class User { public string Email { get; set; } }",
            Language = "csharp",
            LineCount = 5
        };

        // Assert
        content.FilePath.Should().Contain("User.cs");
        content.Content.Should().Contain("class User");
        content.Language.Should().Be("csharp");
        content.LineCount.Should().Be(5);
    }

    [Test]
    public void SimilarPattern_ShouldStorePatternInformation()
    {
        // Arrange
        var pattern = new SimilarPattern
        {
            PatternName = "Repository Pattern",
            FilePath = "src/Repositories/UserRepository.cs",
            Description = "Data access using repository pattern",
            Relevance = 0.78
        };

        // Assert
        pattern.PatternName.Should().Be("Repository Pattern");
        pattern.Relevance.Should().BeInRange(0, 1);
        pattern.Description.Should().NotBeEmpty();
    }

    [Test]
    public void TestContextInfo_ShouldCalculateCoverage()
    {
        // Arrange
        var testContext = new TestContextInfo
        {
            TotalTests = 100,
            PassingTests = 85,
            FailingTests = 15,
            CoveragePercentage = 82.5,
            FailingTestDetails = new List<FailingTestInfo>
            {
                new FailingTestInfo
                {
                    TestName = "UserService_CreateUser_ShouldReturnUser",
                    ErrorMessage = "Expected: NotNull, Actual: Null",
                    StackTrace = "at UserServiceTests.cs:45"
                }
            }
        };

        // Assert
        testContext.TotalTests.Should().Be(100);
        testContext.PassingTests.Should().Be(85);
        testContext.FailingTests.Should().Be(15);
        testContext.CoveragePercentage.Should().Be(82.5);
        testContext.FailingTestDetails.Should().HaveCount(1);
    }

    [Test]
    public void ProjectStructure_ShouldStoreDirectoryInformation()
    {
        // Arrange
        var structure = new ProjectStructure
        {
            RootDirectory = "/app",
            MainDirectories = new List<string>
            {
                "src/Controllers",
                "src/Services",
                "src/Repositories"
            },
            ConfigurationFiles = new List<string>
            {
                "appsettings.json",
                "Program.cs"
            },
            EntryPoints = new List<string> { "Program.cs" }
        };

        // Assert
        structure.RootDirectory.Should().Be("/app");
        structure.MainDirectories.Should().HaveCount(3);
        structure.ConfigurationFiles.Should().HaveCount(2);
        structure.EntryPoints.Should().HaveCount(1);
    }

    [Test]
    public void SessionHistoryContext_ShouldTrackEvents()
    {
        // Arrange
        var history = new SessionHistoryContext
        {
            TotalEvents = 15,
            StateTransitions = new List<StateTransition>
            {
                new StateTransition
                {
                    From = "INIT_STORY_PROCESSING",
                    To = "ASSESS_JUNIOR_CAPABILITY",
                    Timestamp = DateTime.UtcNow.AddMinutes(-30)
                }
            },
            RecentEvents = new List<RecentEvent>
            {
                new RecentEvent
                {
                    EventType = "progress_update",
                    Timestamp = DateTime.UtcNow.AddMinutes(-5)
                }
            }
        };

        // Assert
        history.TotalEvents.Should().Be(15);
        history.StateTransitions.Should().HaveCount(1);
        history.RecentEvents.Should().HaveCount(1);
    }

    [Test]
    public void CodeContextOutput_Failure_ShouldContainErrorMessage()
    {
        // Arrange
        var output = new CodeContextOutput
        {
            Success = false,
            Message = "Story STORY-123 not found"
        };

        // Assert
        output.Success.Should().BeFalse();
        output.Message.Should().Contain("not found");
    }
}
