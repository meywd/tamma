using System.Text.Json;
using Elsa.Workflows.Management;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
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

    [Test]
    public void ApplyReviewFixesActivity_WithFullDependencies_ShouldNotThrow()
    {
        var logger = new Mock<ILogger<ApplyReviewFixesActivity>>();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        var configuration = new Mock<IConfiguration>();

        Action act = () => new ApplyReviewFixesActivity(logger.Object, httpClientFactory.Object, configuration.Object);
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

        var store = new Mock<IWorkflowInstanceStore>();
        Action act = () => new CheckLimitsActivity(logger.Object, store.Object);
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

    // ================================================================
    // AnalyzeReviewActivity — Comment Categorization
    // ================================================================

    [TestCase("This will crash with a null reference", ReviewCommentCategory.Bug)]
    [TestCase("There's a race condition here", ReviewCommentCategory.Bug)]
    [TestCase("This fails when the list is empty", ReviewCommentCategory.Bug)]
    [TestCase("Off by one error in the loop", ReviewCommentCategory.Bug)]
    [TestCase("This could throw an unhandled exception", ReviewCommentCategory.Bug)]
    public void CategorizeComment_BugPatterns_ShouldReturnBug(string comment, string expected)
    {
        AnalyzeReviewActivity.CategorizeComment(comment).Should().Be(expected);
    }

    [TestCase("SQL injection vulnerability here", ReviewCommentCategory.Security)]
    [TestCase("Need to sanitize user input", ReviewCommentCategory.Security)]
    [TestCase("This token should not be hardcoded", ReviewCommentCategory.Security)]
    [TestCase("Missing authentication check", ReviewCommentCategory.Security)]
    public void CategorizeComment_SecurityPatterns_ShouldReturnSecurity(string comment, string expected)
    {
        AnalyzeReviewActivity.CategorizeComment(comment).Should().Be(expected);
    }

    [TestCase("This is an N+1 query", ReviewCommentCategory.Performance)]
    [TestCase("This is slow, consider caching", ReviewCommentCategory.Performance)]
    [TestCase("Unnecessary allocation in the loop", ReviewCommentCategory.Performance)]
    [TestCase("Consider lazy loading this", ReviewCommentCategory.Performance)]
    public void CategorizeComment_PerformancePatterns_ShouldReturnPerformance(string comment, string expected)
    {
        AnalyzeReviewActivity.CategorizeComment(comment).Should().Be(expected);
    }

    [TestCase("Consider extracting this into a separate class for better separation of concerns", ReviewCommentCategory.Design)]
    [TestCase("This violates single responsibility principle", ReviewCommentCategory.Design)]
    [TestCase("Should be refactored to use composition", ReviewCommentCategory.Design)]
    public void CategorizeComment_DesignPatterns_ShouldReturnDesign(string comment, string expected)
    {
        AnalyzeReviewActivity.CategorizeComment(comment).Should().Be(expected);
    }

    [TestCase("nit: rename this variable", ReviewCommentCategory.Style)]
    [TestCase("Typo in the variable name", ReviewCommentCategory.Style)]
    [TestCase("Missing documentation for public method", ReviewCommentCategory.Style)]
    [TestCase("Magic number, use a constant", ReviewCommentCategory.Style)]
    public void CategorizeComment_StylePatterns_ShouldReturnStyle(string comment, string expected)
    {
        AnalyzeReviewActivity.CategorizeComment(comment).Should().Be(expected);
    }

    [TestCase("Why is this needed?", ReviewCommentCategory.Question)]
    [TestCase("Could you explain this logic?", ReviewCommentCategory.Question)]
    [TestCase("What happens if the user is null?", ReviewCommentCategory.Question)]
    public void CategorizeComment_QuestionPatterns_ShouldReturnQuestion(string comment, string expected)
    {
        AnalyzeReviewActivity.CategorizeComment(comment).Should().Be(expected);
    }

    [TestCase("LGTM", ReviewCommentCategory.Praise)]
    [TestCase("Looks good to me!", ReviewCommentCategory.Praise)]
    [TestCase("Nice work, well done", ReviewCommentCategory.Praise)]
    [TestCase("Ship it! +1", ReviewCommentCategory.Praise)]
    public void CategorizeComment_PraisePatterns_ShouldReturnPraise(string comment, string expected)
    {
        AnalyzeReviewActivity.CategorizeComment(comment).Should().Be(expected);
    }

    [Test]
    public void CategorizeComment_EmptyString_ShouldReturnUnknown()
    {
        AnalyzeReviewActivity.CategorizeComment("").Should().Be(ReviewCommentCategory.Unknown);
        AnalyzeReviewActivity.CategorizeComment("  ").Should().Be(ReviewCommentCategory.Unknown);
    }

    // ================================================================
    // AnalyzeReviewActivity — Priority Determination
    // ================================================================

    [TestCase(ReviewCommentCategory.Bug, "critical")]
    [TestCase(ReviewCommentCategory.Security, "critical")]
    [TestCase(ReviewCommentCategory.Performance, "high")]
    [TestCase(ReviewCommentCategory.Design, "normal")]
    [TestCase(ReviewCommentCategory.Style, "low")]
    [TestCase(ReviewCommentCategory.Question, "low")]
    [TestCase(ReviewCommentCategory.Praise, "none")]
    [TestCase(ReviewCommentCategory.Unknown, "normal")]
    public void DeterminePriority_ShouldMapCategoryCorrectly(string category, string expectedPriority)
    {
        AnalyzeReviewActivity.DeterminePriority(category).Should().Be(expectedPriority);
    }

    // ================================================================
    // ReviewCommentCategory — IsActionable
    // ================================================================

    [TestCase(ReviewCommentCategory.Bug, true)]
    [TestCase(ReviewCommentCategory.Security, true)]
    [TestCase(ReviewCommentCategory.Performance, true)]
    [TestCase(ReviewCommentCategory.Design, true)]
    [TestCase(ReviewCommentCategory.Style, true)]
    [TestCase(ReviewCommentCategory.Question, false)]
    [TestCase(ReviewCommentCategory.Praise, false)]
    [TestCase(ReviewCommentCategory.Unknown, false)]
    public void ReviewCommentCategory_IsActionable_ShouldClassifyCorrectly(string category, bool expected)
    {
        ReviewCommentCategory.IsActionable(category).Should().Be(expected);
    }

    // ================================================================
    // ApplyReviewFixesActivity — BuildFixPrompt
    // ================================================================

    [Test]
    public void BuildFixPrompt_ShouldIncludeAllFixItems()
    {
        var analysis = new ReviewAnalysisResult
        {
            FixItems = new List<ReviewFixItem>
            {
                new() { FilePath = "src/foo.ts", Line = 42, Comment = "null check missing", Category = "bug", Priority = "critical" },
                new() { FilePath = "src/bar.ts", Line = 10, Comment = "rename this variable", Category = "style", Priority = "low", SuggestedFix = "Use camelCase" }
            }
        };

        var prompt = ApplyReviewFixesActivity.BuildFixPrompt(analysis, "owner/repo", "fix/review");

        prompt.Should().Contain("owner/repo");
        prompt.Should().Contain("fix/review");
        prompt.Should().Contain("src/foo.ts");
        prompt.Should().Contain("Line: 42");
        prompt.Should().Contain("null check missing");
        prompt.Should().Contain("[bug]");
        prompt.Should().Contain("Priority: critical");
        prompt.Should().Contain("src/bar.ts");
        prompt.Should().Contain("rename this variable");
        prompt.Should().Contain("Suggested fix: Use camelCase");
        prompt.Should().Contain("Comment 1");
        prompt.Should().Contain("Comment 2");
    }

    [Test]
    public void BuildFixPrompt_WithNoLine_ShouldOmitLineInfo()
    {
        var analysis = new ReviewAnalysisResult
        {
            FixItems = new List<ReviewFixItem>
            {
                new() { FilePath = "src/foo.ts", Line = null, Comment = "general comment", Category = "design", Priority = "normal" }
            }
        };

        var prompt = ApplyReviewFixesActivity.BuildFixPrompt(analysis, "owner/repo", "main");

        prompt.Should().Contain("src/foo.ts");
        prompt.Should().NotContain("Line:");
    }

    // ================================================================
    // ApplyReviewFixesActivity — ParseFixResponse
    // ================================================================

    [Test]
    public void ParseFixResponse_ValidJson_ShouldParseCorrectly()
    {
        var response = JsonSerializer.Serialize(new
        {
            fixedCode = "// fixed code here",
            filesFixed = new[] { "src/foo.ts", "src/bar.ts" },
            fixDescriptions = new[]
            {
                new { filePath = "src/foo.ts", originalComment = "null check", fixApplied = "added null check", line = 42 }
            }
        });

        var analysis = new ReviewAnalysisResult();
        var result = ApplyReviewFixesActivity.ParseFixResponse(response, analysis);

        result.Success.Should().BeTrue();
        result.FixedCode.Should().Be("// fixed code here");
        result.FilesFixed.Should().HaveCount(2);
        result.FilesFixed.Should().Contain("src/foo.ts");
        result.FixDescriptions.Should().HaveCount(1);
        result.FixDescriptions[0].FilePath.Should().Be("src/foo.ts");
        result.FixDescriptions[0].FixApplied.Should().Be("added null check");
        result.FixDescriptions[0].Line.Should().Be(42);
    }

    [Test]
    public void ParseFixResponse_MarkdownWrapped_ShouldExtractJson()
    {
        var json = JsonSerializer.Serialize(new
        {
            fixedCode = "// code",
            filesFixed = new[] { "src/foo.ts" },
            fixDescriptions = Array.Empty<object>()
        });
        var response = $"```json\n{json}\n```";

        var result = ApplyReviewFixesActivity.ParseFixResponse(response, new ReviewAnalysisResult());

        result.Success.Should().BeTrue();
        result.FilesFixed.Should().HaveCount(1);
    }

    [Test]
    public void ParseFixResponse_InvalidJson_ShouldReturnFailure()
    {
        var result = ApplyReviewFixesActivity.ParseFixResponse("not json at all", new ReviewAnalysisResult());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Failed to parse");
    }

    [Test]
    public void ParseFixResponse_EmptyJson_ShouldReturnFailure()
    {
        var result = ApplyReviewFixesActivity.ParseFixResponse("{}", new ReviewAnalysisResult());

        result.Success.Should().BeFalse();
        result.FilesFixed.Should().BeEmpty();
    }

    [Test]
    public void ParseFixResponse_NullLine_ShouldHandleGracefully()
    {
        var response = JsonSerializer.Serialize(new
        {
            fixedCode = "// code",
            filesFixed = new[] { "file.ts" },
            fixDescriptions = new[]
            {
                new { filePath = "file.ts", originalComment = "comment", fixApplied = "fix" }
            }
        });

        var result = ApplyReviewFixesActivity.ParseFixResponse(response, new ReviewAnalysisResult());

        result.Success.Should().BeTrue();
        result.FixDescriptions[0].Line.Should().BeNull();
    }

    // ================================================================
    // ApplyReviewFixesActivity — SimulateFixGeneration
    // ================================================================

    [Test]
    public void SimulateFixGeneration_ShouldOnlyIncludeActionableItems()
    {
        var analysis = new ReviewAnalysisResult
        {
            FixItems = new List<ReviewFixItem>
            {
                new() { FilePath = "src/a.ts", Comment = "bug here", Category = ReviewCommentCategory.Bug },
                new() { FilePath = "src/b.ts", Comment = "looks good!", Category = ReviewCommentCategory.Praise },
                new() { FilePath = "src/c.ts", Comment = "why?", Category = ReviewCommentCategory.Question },
                new() { FilePath = "src/d.ts", Comment = "sql injection", Category = ReviewCommentCategory.Security }
            }
        };

        var jsonResponse = ApplyReviewFixesActivity.SimulateFixGeneration(analysis);
        var parsed = JsonSerializer.Deserialize<JsonElement>(jsonResponse);

        var filesFixed = JsonSerializer.Deserialize<List<string>>(parsed.GetProperty("filesFixed").GetRawText());
        filesFixed.Should().HaveCount(2);
        filesFixed.Should().Contain("src/a.ts");
        filesFixed.Should().Contain("src/d.ts");
        filesFixed.Should().NotContain("src/b.ts");
        filesFixed.Should().NotContain("src/c.ts");
    }

    // ================================================================
    // ReviewFixResult model
    // ================================================================

    [Test]
    public void ReviewFixResult_Serialization_ShouldRoundTrip()
    {
        var result = new ReviewFixResult
        {
            Success = true,
            FilesFixed = new List<string> { "src/foo.ts" },
            FixedCode = "// fixed",
            FixDescriptions = new List<ReviewFixDescription>
            {
                new() { FilePath = "src/foo.ts", OriginalComment = "fix this", FixApplied = "fixed it", Line = 10 }
            }
        };

        var json = JsonSerializer.Serialize(result);
        var deserialized = JsonSerializer.Deserialize<ReviewFixResult>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Success.Should().BeTrue();
        deserialized.FilesFixed.Should().HaveCount(1);
        deserialized.FixDescriptions.Should().HaveCount(1);
        deserialized.FixDescriptions[0].Line.Should().Be(10);
    }
}
