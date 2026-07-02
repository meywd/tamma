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
public class CodeReviewActivityTests
{
    private Mock<ILogger<CodeReviewActivity>> _mockLogger = null!;
    private Mock<IMentorshipSessionRepository> _mockRepository = null!;
    private TammaApiClient _apiClient = null!;
    private Mock<IAnalyticsService> _mockAnalyticsService = null!;

    [SetUp]
    public void SetUp()
    {
        _mockLogger = new Mock<ILogger<CodeReviewActivity>>();
        _mockRepository = new Mock<IMentorshipSessionRepository>();
        // Story 38 (Phase 2): the git reads + PR create now route through the thin
        // TammaApiClient, not the credential-holding composite IIntegrationService.
        _apiClient = new TammaApiClient(new HttpClient(), NullLogger<TammaApiClient>.Instance, null, null);
        _mockAnalyticsService = new Mock<IAnalyticsService>();
    }

    [Test]
    public void Constructor_WithValidDependencies_ShouldNotThrow()
    {
        // Act
        Action act = () => new CodeReviewActivity(
            _mockLogger.Object,
            _mockRepository.Object,
            _apiClient,
            _mockAnalyticsService.Object);

        // Assert
        act.Should().NotThrow();
    }

    [Test]
    public void CodeReviewOutput_ShouldHaveExpectedDefaultValues()
    {
        // Arrange
        var output = new CodeReviewOutput();

        // Assert
        output.Success.Should().BeFalse();
        output.Status.Should().Be(ReviewStatus.Pending);
        output.PullRequestNumber.Should().BeNull();
        output.PullRequestUrl.Should().BeNull();
        output.FileChanges.Should().BeEmpty();
        output.ReviewComments.Should().BeEmpty();
    }

    [Test]
    public void CodeReviewOutput_WithApproval_ShouldTransitionToMerge()
    {
        // Arrange
        var output = new CodeReviewOutput
        {
            Success = true,
            Status = ReviewStatus.Approved,
            PullRequestNumber = 123,
            PullRequestUrl = "https://github.com/org/repo/pull/123",
            NextState = MentorshipState.MERGE_AND_COMPLETE,
            Message = "PR approved, ready to merge"
        };

        // Assert
        output.Success.Should().BeTrue();
        output.Status.Should().Be(ReviewStatus.Approved);
        output.NextState.Should().Be(MentorshipState.MERGE_AND_COMPLETE);
    }

    [Test]
    public void CodeReviewOutput_WithChangesRequested_ShouldTransitionToGuideFixes()
    {
        // Arrange
        var output = new CodeReviewOutput
        {
            Success = true,
            Status = ReviewStatus.ChangesRequested,
            PullRequestNumber = 123,
            ReviewComments = new List<ReviewComment>
            {
                new ReviewComment
                {
                    Comment = "Consider adding null check here",
                    FilePath = "Service.cs",
                    LineNumber = 45,
                    Author = "senior-reviewer"
                }
            },
            NextState = MentorshipState.GUIDE_FIXES,
            Message = "1 changes requested"
        };

        // Assert
        output.Success.Should().BeTrue();
        output.Status.Should().Be(ReviewStatus.ChangesRequested);
        output.ReviewComments.Should().HaveCount(1);
        output.NextState.Should().Be(MentorshipState.GUIDE_FIXES);
    }

    [Test]
    public void CodeReviewAction_ShouldHaveExpectedValues()
    {
        // Assert
        Enum.GetValues<CodeReviewAction>().Should().HaveCount(3);
        Enum.GetValues<CodeReviewAction>().Should().Contain(CodeReviewAction.Prepare);
        Enum.GetValues<CodeReviewAction>().Should().Contain(CodeReviewAction.Monitor);
        Enum.GetValues<CodeReviewAction>().Should().Contain(CodeReviewAction.RequestChanges);
    }

    [Test]
    public void ReviewStatus_ShouldHaveExpectedValues()
    {
        // Assert
        Enum.GetValues<ReviewStatus>().Should().HaveCount(4);
        Enum.GetValues<ReviewStatus>().Should().Contain(ReviewStatus.Pending);
        Enum.GetValues<ReviewStatus>().Should().Contain(ReviewStatus.Approved);
        Enum.GetValues<ReviewStatus>().Should().Contain(ReviewStatus.ChangesRequested);
        Enum.GetValues<ReviewStatus>().Should().Contain(ReviewStatus.Error);
    }

    [Test]
    public void ReviewComment_ShouldStoreCommentDetails()
    {
        // Arrange
        var comment = new ReviewComment
        {
            Comment = "Variable name could be more descriptive",
            FilePath = "Model.cs",
            LineNumber = 15,
            Author = "senior-dev"
        };

        // Assert
        comment.Comment.Should().NotBeEmpty();
        comment.FilePath.Should().Be("Model.cs");
        comment.LineNumber.Should().Be(15);
        comment.Author.Should().Be("senior-dev");
    }

    [Test]
    public void FileChangeInfo_ShouldStoreChangeDetails()
    {
        // Arrange
        var fileChange = new FileChangeInfo
        {
            FilePath = "src/Services/UserService.cs",
            ChangeType = "modified",
            Additions = 25,
            Deletions = 10
        };

        // Assert
        fileChange.FilePath.Should().Contain("UserService");
        fileChange.ChangeType.Should().Be("modified");
        fileChange.Additions.Should().Be(25);
        fileChange.Deletions.Should().Be(10);
    }

    [Test]
    public void CommentGuidance_ShouldMapCommentToGuidance()
    {
        // Arrange
        var guidance = new CommentGuidance
        {
            Comment = "Consider adding null check here",
            Guidance = "Add a null check using if (variable == null) or the null-conditional operator ?."
        };

        // Assert
        guidance.Comment.Should().Contain("null check");
        guidance.Guidance.Should().Contain("null");
    }

    [Test]
    public void CodeReviewOutput_Pending_ShouldContinueMonitoring()
    {
        // Arrange
        var output = new CodeReviewOutput
        {
            Success = true,
            Status = ReviewStatus.Pending,
            PullRequestNumber = 123,
            NextState = MentorshipState.MONITOR_REVIEW,
            Message = "Review still pending"
        };

        // Assert
        output.Status.Should().Be(ReviewStatus.Pending);
        output.NextState.Should().Be(MentorshipState.MONITOR_REVIEW);
    }

    [Test]
    public void CodeReviewOutput_Error_ShouldEscalate()
    {
        // Arrange
        var output = new CodeReviewOutput
        {
            Success = false,
            Status = ReviewStatus.Error,
            NextState = MentorshipState.ESCALATE_TO_SENIOR,
            Message = "Failed to create PR: Repository not found"
        };

        // Assert
        output.Success.Should().BeFalse();
        output.Status.Should().Be(ReviewStatus.Error);
        output.NextState.Should().Be(MentorshipState.ESCALATE_TO_SENIOR);
    }
}
