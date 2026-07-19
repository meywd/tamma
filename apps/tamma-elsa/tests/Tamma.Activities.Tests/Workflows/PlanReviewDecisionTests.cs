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
    // ParseRoleVerdict — object-shaped verdict (the shape the PlanReview
    // prompt template actually instructs: {"verdict":{"decision":...}})
    // ================================================================

    [Test]
    public void ParseRoleVerdict_ObjectVerdict_Approve_ReturnsApprove()
    {
        // Conforming reply per SystemPrompts.PlanReview — previously threw on
        // GetString() and landed the pessimistic "concerns" default.
        var json = """
        {
            "issues": [],
            "verdict": {"decision": "APPROVE", "summary": "Plan is solid", "blockingIssues": []}
        }
        """;

        var (verdict, comments, _) = ReviewAggregationHelper.ParseRoleVerdict(json);

        verdict.Should().Be("approve");
        comments.Should().Be("Plan is solid");
    }

    [Test]
    public void ParseRoleVerdict_ObjectVerdict_RequestChanges_ReturnsConcerns()
    {
        var json = """
        {
            "issues": [{"task": "T1", "severity": "major", "issue": "No rollback"}],
            "verdict": {
                "decision": "REQUEST_CHANGES",
                "summary": "Missing rollback plan",
                "blockingIssues": ["No rollback strategy"]
            }
        }
        """;

        var (verdict, comments, _) = ReviewAggregationHelper.ParseRoleVerdict(json);

        verdict.Should().Be("concerns");
        comments.Should().Contain("Missing rollback plan");
        comments.Should().Contain("No rollback strategy");
    }

    [Test]
    public void ParseRoleVerdict_ObjectVerdict_NeedsDiscussion_ReturnsConcerns()
    {
        var json = """{"verdict": {"decision": "NEEDS_DISCUSSION", "summary": "Scope unclear"}}""";

        var (verdict, comments, _) = ReviewAggregationHelper.ParseRoleVerdict(json);

        verdict.Should().Be("concerns");
        comments.Should().Be("Scope unclear");
    }

    [Test]
    public void ParseRoleVerdict_ObjectVerdict_CaseInsensitiveDecision_ReturnsApprove()
    {
        var json = """{"verdict": {"decision": "approve"}}""";

        var (verdict, _, _) = ReviewAggregationHelper.ParseRoleVerdict(json);

        verdict.Should().Be("approve");
    }

    [Test]
    public void ParseRoleVerdict_ObjectVerdict_UnknownOrMissingDecision_ReturnsConcerns()
    {
        var (verdictUnknown, _, _) = ReviewAggregationHelper.ParseRoleVerdict(
            """{"verdict": {"decision": "SHIP_IT"}}""");
        var (verdictMissing, _, _) = ReviewAggregationHelper.ParseRoleVerdict(
            """{"verdict": {"summary": "no decision field"}}""");

        verdictUnknown.Should().Be("concerns");
        verdictMissing.Should().Be("concerns");
    }

    [Test]
    public void ParseRoleVerdict_ObjectVerdict_TopLevelCommentsWin()
    {
        var json = """
        {
            "verdict": {"decision": "APPROVE", "summary": "inner summary"},
            "comments": "explicit top-level comments",
            "suggestedChanges": "rename x"
        }
        """;

        var (verdict, comments, suggestedChanges) = ReviewAggregationHelper.ParseRoleVerdict(json);

        verdict.Should().Be("approve");
        comments.Should().Be("explicit top-level comments");
        suggestedChanges.Should().Be("rename x");
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
