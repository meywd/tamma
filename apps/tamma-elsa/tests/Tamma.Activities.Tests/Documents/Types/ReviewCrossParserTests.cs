using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows.Helpers;
using CoreReview = Tamma.Core.Documents.Types.Review;

namespace Tamma.Activities.Tests.Documents.Types;

/// <summary>
/// Story 39-4 AC2/AC8 — the two-parsers-one-truth cross-check for <see cref="Review"/>
/// (Design Decision D8): this half lives in <c>Tamma.Activities.Tests</c> because it
/// invokes the OLD <c>ReviewAggregationHelper.ParseRoleVerdict</c> /
/// <c>AggregateVerdicts</c> baseline. For every legacy verdict fixture (string AND
/// object shape) the unified <see cref="Review.FromLegacyVerdictJson"/> decision must
/// fold to the SAME approve / not-approve bucket the panel aggregation sees — so the
/// migration to the typed document cannot silently change a review's meaning.
/// </summary>
[TestFixture]
public class ReviewCrossParserTests
{
    private static readonly ReviewSubject Subject = new()
    {
        Kind = "document",
        DocumentId = Guid.NewGuid(),
        DocumentType = "plan",
    };

    // Fixtures drawn from PlanReviewDecisionTests.cs (the ParseRoleVerdict corpus).
    [TestCase("""{"verdict": "approve", "comments": "LGTM"}""")]
    [TestCase("""{"verdict": "concerns", "comments": "Missing error handling"}""")]
    [TestCase("""{"verdict": {"decision": "APPROVE", "summary": "Plan is solid", "blockingIssues": []}}""")]
    [TestCase("""{"verdict": {"decision": "REQUEST_CHANGES", "summary": "Rework needed", "blockingIssues": ["no rollback"]}}""")]
    [TestCase("""{"verdict": {"decision": "NEEDS_DISCUSSION", "summary": "Scope unclear"}}""")]
    [TestCase("""{"verdict": {"decision": "approve"}}""")]
    public void Our_decision_folds_to_the_same_approve_bucket_as_the_baseline(string json)
    {
        var (verdict, _, _) = ReviewAggregationHelper.ParseRoleVerdict(json);
        var baselineApprovable = ReviewAggregationHelper.AggregateVerdicts(new[] { verdict });

        var review = CoreReview.FromLegacyVerdictJson(json, Subject);
        var oursApprovable = review.Decision == ReviewDecision.Approve;

        oursApprovable.Should().Be(baselineApprovable,
            "two parsers, one truth: the unified decision must land in the same approve / not-approve " +
            "bucket AggregateVerdicts sees for this legacy fixture");
    }

    [Test]
    public void Baseline_pessimistic_default_becomes_a_typed_error_not_a_document()
    {
        // The baseline launders parse failure into the "concerns" default; the typed
        // reader refuses — the pessimistic-default question is settled by the lifecycle.
        foreach (var garbage in new[] { "{}", "not json at all", "" })
        {
            var (verdict, _, _) = ReviewAggregationHelper.ParseRoleVerdict(garbage);
            verdict.Should().Be("concerns", "the baseline defaults to concerns");

            var act = () => CoreReview.FromLegacyVerdictJson(garbage, Subject);
            act.Should().Throw<Tamma.Core.TammaError>()
                .Which.Code.Should().Be("DOCUMENT.REVIEW.LEGACY_UNPARSEABLE");
        }
    }
}
