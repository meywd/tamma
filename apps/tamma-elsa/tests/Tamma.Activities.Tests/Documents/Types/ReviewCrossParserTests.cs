using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents.Types;
using CoreReview = Tamma.Core.Documents.Types.Review;

namespace Tamma.Activities.Tests.Documents.Types;

/// <summary>
/// Story 39-4 AC2/AC8 — the legacy-verdict fold check for <see cref="Review"/> (Design
/// Decision D8). The OLD <c>ReviewAggregationHelper.ParseRoleVerdict</c> /
/// <c>AggregateVerdicts</c> baseline this suite once cross-checked against was DELETED in
/// Story 39-14; the recorded baseline approve/not-approve bucket for each legacy fixture is
/// pinned inline (the <c>expectedApprove</c> parameter), and the assertion stands on the
/// typed reader alone: <see cref="Review.FromLegacyVerdictJson"/> must fold every legacy
/// verdict shape (string AND object) to the SAME bucket the old aggregation saw, so the
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

    // Fixtures + recorded baseline buckets (the retired ParseRoleVerdict/AggregateVerdicts corpus).
    [TestCase("""{"verdict": "approve", "comments": "LGTM"}""", true)]
    [TestCase("""{"verdict": "concerns", "comments": "Missing error handling"}""", false)]
    [TestCase("""{"verdict": {"decision": "APPROVE", "summary": "Plan is solid", "blockingIssues": []}}""", true)]
    [TestCase("""{"verdict": {"decision": "REQUEST_CHANGES", "summary": "Rework needed", "blockingIssues": ["no rollback"]}}""", false)]
    [TestCase("""{"verdict": {"decision": "NEEDS_DISCUSSION", "summary": "Scope unclear"}}""", false)]
    [TestCase("""{"verdict": {"decision": "approve"}}""", true)]
    public void Our_decision_folds_to_the_recorded_baseline_bucket(string json, bool expectedApprove)
    {
        var review = CoreReview.FromLegacyVerdictJson(json, Subject);
        var oursApprovable = review.Decision == ReviewDecision.Approve;

        oursApprovable.Should().Be(expectedApprove,
            "the unified decision must land in the same approve / not-approve bucket the retired " +
            "AggregateVerdicts baseline saw for this legacy fixture");
    }

    [Test]
    public void Baseline_pessimistic_default_becomes_a_typed_error_not_a_document()
    {
        // The retired baseline laundered parse failure into the "concerns" default; the typed
        // reader refuses — the pessimistic-default question is settled by the lifecycle.
        foreach (var garbage in new[] { "{}", "not json at all", "" })
        {
            var act = () => CoreReview.FromLegacyVerdictJson(garbage, Subject);
            act.Should().Throw<Tamma.Core.TammaError>()
                .Which.Code.Should().Be("DOCUMENT.REVIEW.LEGACY_UNPARSEABLE");
        }
    }
}
