using FluentAssertions;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

[TestFixture]
public class PlanReviewDecisionTests
{
    // ================================================================
    // ParseRoleVerdict
    // ================================================================

    [Test]
    public void ParseRoleVerdict_ApproveVerdict_ReturnsApprove()
    {
        var json = """{"verdict": "approve", "comments": "LGTM", "suggestedChanges": ""}""";
        var (verdict, comments, _) = ReviewAggregationHelper.ParseRoleVerdict(json);

        verdict.Should().Be("approve");
        comments.Should().Be("LGTM");
    }

    [Test]
    public void ParseRoleVerdict_ConcernsVerdict_ReturnsConcerns()
    {
        var json = """{"verdict": "concerns", "comments": "Missing error handling"}""";
        var (verdict, comments, _) = ReviewAggregationHelper.ParseRoleVerdict(json);

        verdict.Should().Be("concerns");
        comments.Should().Be("Missing error handling");
    }

    [Test]
    public void ParseRoleVerdict_EmptyJson_ReturnsConcerns()
    {
        var (verdict, _, _) = ReviewAggregationHelper.ParseRoleVerdict("{}");
        verdict.Should().Be("concerns");
    }

    [Test]
    public void ParseRoleVerdict_InvalidJson_TreatedAsConcerns()
    {
        var (verdict, comments, _) = ReviewAggregationHelper.ParseRoleVerdict("not json at all");

        verdict.Should().Be("concerns");
        comments.Should().Be("not json at all");
    }

    [Test]
    public void ParseRoleVerdict_NullWhitespace_ReturnsConcerns()
    {
        var (verdict, _, _) = ReviewAggregationHelper.ParseRoleVerdict("");
        verdict.Should().Be("concerns");
    }

    // ================================================================
    // AggregateVerdicts
    // ================================================================

    [Test]
    public void AllSevenRolesApprove_ReturnsApproved()
    {
        var verdicts = Enumerable.Repeat("approve", 7);
        ReviewAggregationHelper.AggregateVerdicts(verdicts).Should().BeTrue();
    }

    [Test]
    public void OneRoleRejects_ReturnsNotApproved()
    {
        var verdicts = new[] { "approve", "approve", "concerns", "approve", "approve", "approve", "approve" };
        ReviewAggregationHelper.AggregateVerdicts(verdicts).Should().BeFalse();
    }

    [Test]
    public void AllConcerns_ReturnsNotApproved()
    {
        var verdicts = Enumerable.Repeat("concerns", 7);
        ReviewAggregationHelper.AggregateVerdicts(verdicts).Should().BeFalse();
    }

    [Test]
    public void EmptyVerdicts_ReturnsApproved()
    {
        // All() on empty enumerable returns true
        ReviewAggregationHelper.AggregateVerdicts(Array.Empty<string>()).Should().BeTrue();
    }

    // ================================================================
    // ParseDiscussionResult
    // ================================================================

    [Test]
    public void DiscussionResult_Approved_SetsDecision()
    {
        var json = """
        {
            "overallDecision": "approved",
            "reviewNotes": "All issues resolved",
            "modifiedPlan": "{\"tasks\":[]}",
            "resolutions": [{"concern": "x", "resolution": "fix"}]
        }
        """;

        var result = ReviewAggregationHelper.ParseDiscussionResult(json);

        result.Decision.Should().Be("approved");
        result.ReviewNotes.Should().Be("All issues resolved");
        result.ModifiedPlan.Should().Contain("tasks");
    }

    [Test]
    public void DiscussionResult_NeedsModification_TriggersReReview()
    {
        var json = """{"overallDecision": "needsModification", "reviewNotes": "Fix security issues"}""";

        var result = ReviewAggregationHelper.ParseDiscussionResult(json);

        result.Decision.Should().Be("needsModification");
        result.ReviewNotes.Should().Be("Fix security issues");
    }

    [Test]
    public void DiscussionResult_NeedsHuman_SetsDecision()
    {
        var json = """{"overallDecision": "needsHuman", "reviewNotes": "Cannot resolve automatically"}""";

        var result = ReviewAggregationHelper.ParseDiscussionResult(json);

        result.Decision.Should().Be("needsHuman");
    }

    [Test]
    public void DiscussionResult_WithDeferredItems_ExtractsDeferred()
    {
        var json = """{"overallDecision": "approved", "deferred": [{"title": "Defer item"}]}""";

        var result = ReviewAggregationHelper.ParseDiscussionResult(json);

        result.Deferred.Should().Contain("Defer item");
    }

    [Test]
    public void DiscussionResult_WithSplitItems_ExtractsSplit()
    {
        var json = """{"overallDecision": "approved", "split": [{"title": "Sub-issue 1"}]}""";

        var result = ReviewAggregationHelper.ParseDiscussionResult(json);

        result.Split.Should().Contain("Sub-issue 1");
    }

    [Test]
    public void EmptyDiscussionJson_DefaultsToNeedsHuman()
    {
        var result = ReviewAggregationHelper.ParseDiscussionResult("");

        result.Decision.Should().Be("needsHuman");
        result.ReviewNotes.Should().Contain("Failed to parse");
    }

    [Test]
    public void InvalidDiscussionJson_DefaultsToNeedsHuman()
    {
        var result = ReviewAggregationHelper.ParseDiscussionResult("not valid json");

        result.Decision.Should().Be("needsHuman");
    }

    [Test]
    public void DiscussionResult_WrappedInText_ExtractsJson()
    {
        var response = """Here is the result: {"overallDecision": "approved", "reviewNotes": "OK"} end.""";

        var result = ReviewAggregationHelper.ParseDiscussionResult(response);

        result.Decision.Should().Be("approved");
        result.ReviewNotes.Should().Be("OK");
    }
}
