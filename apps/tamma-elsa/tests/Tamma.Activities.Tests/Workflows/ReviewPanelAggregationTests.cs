using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows.Helpers;
using Agg = Tamma.ElsaServer.Workflows.Helpers.ReviewPanelAggregation;
using ReviewType = Tamma.Core.Documents.Types.Review;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-7 — unit pins for <see cref="ReviewPanelAggregation"/> (Design Decision
/// D6; covers AC3 + AC6's helper halves).
/// </summary>
[TestFixture]
public class ReviewPanelAggregationTests
{
    private static readonly ReviewSubject Subject = new()
    {
        Kind = "document",
        DocumentId = Guid.Parse("0192a8b0-1111-7abc-8def-000000000001"),
        DocumentType = "plan",
    };

    private static ReviewIssue Issue(ReviewSeverity sev) => new(sev, "cat", "desc", "fix");

    private static ReviewType MakeReview(ReviewDecision decision, params ReviewIssue[] issues) => new()
    {
        Subject = Subject, Decision = decision, Summary = "s", Issues = issues,
    };

    private static Agg.PanelMember Usable(string role, ReviewType review) =>
        new(role, Guid.NewGuid(), review, true, null);

    private static Agg.PanelMember Failed(string role) =>
        new(role, null, null, false, "validation-exhausted");

    // ── default-config (Unanimous + full roster) parity table ──

    [Test]
    public void DefaultConfig_AllApprove_Approves()
    {
        var members = new[]
        {
            Usable("architect", MakeReview(ReviewDecision.Approve)),
            Usable("developer", MakeReview(ReviewDecision.Approve)),
            Usable("security", MakeReview(ReviewDecision.Approve)),
        };

        var result = Agg.Aggregate(members, Agg.DefaultsFor(3), Subject);

        result.Decided.Should().BeTrue();
        result.Aggregate!.Decision.Should().Be(ReviewDecision.Approve);
    }

    [Test]
    public void DefaultConfig_AnyNonApprove_RequestsChanges()
    {
        var members = new[]
        {
            Usable("architect", MakeReview(ReviewDecision.Approve)),
            Usable("developer", MakeReview(ReviewDecision.RequestChanges, Issue(ReviewSeverity.Major))),
            Usable("security", MakeReview(ReviewDecision.Approve)),
        };

        var result = Agg.Aggregate(members, Agg.DefaultsFor(3), Subject);

        result.Decided.Should().BeTrue();
        result.Aggregate!.Decision.Should().Be(ReviewDecision.RequestChanges);
    }

    [Test]
    public void BlockingVeto_MajorityApproveWithOneCritical_RequestsChanges_AndAggregateValidates()
    {
        var members = new[]
        {
            Usable("architect", MakeReview(ReviewDecision.Approve)),
            Usable("developer", MakeReview(ReviewDecision.Approve)),
            Usable("security", MakeReview(ReviewDecision.RequestChanges, Issue(ReviewSeverity.Critical))),
        };

        var result = Agg.Aggregate(members, new Agg.PanelAggregationRules(Agg.PanelDecisionRule.Majority, 3), Subject);

        result.Decided.Should().BeTrue();
        result.Aggregate!.Decision.Should().Be(ReviewDecision.RequestChanges,
            "any member Critical issue vetoes the aggregate to RequestChanges regardless of majority");

        // The concatenated aggregate must itself pass validation (no APPROVE_WITH_BLOCKING escape).
        ValidateAggregate(result.Aggregate!).IsValid.Should().BeTrue();
    }

    [Test]
    public void EmptyPanel_IsUndecidable_EmptyPanel()
    {
        var result = Agg.Aggregate(Array.Empty<Agg.PanelMember>(), Agg.DefaultsFor(0), Subject);

        result.Decided.Should().BeFalse();
        result.Aggregate.Should().BeNull();
        result.Reason.Should().Be(Agg.PanelUndecidableReason.EmptyPanel);
    }

    [Test]
    public void FailedMemberUnderFullRosterMinimum_IsUndecidable_BelowQuorum_CarriesAllMembers()
    {
        var members = new[]
        {
            Usable("architect", MakeReview(ReviewDecision.Approve)),
            Usable("developer", MakeReview(ReviewDecision.Approve)),
            Failed("security"),
        };

        var result = Agg.Aggregate(members, Agg.DefaultsFor(3), Subject);

        result.Decided.Should().BeFalse();
        result.Aggregate.Should().BeNull();
        result.Reason.Should().Be(Agg.PanelUndecidableReason.BelowQuorum);
        result.SucceededCount.Should().Be(2);
        result.FailedRoles.Should().Contain("security");
        result.MemberReviewIds.Should().HaveCount(2, "the two usable members carry ids; the failed one has none");
    }

    [Test]
    public void MajorityRule_ExactTie_IsUndecidable_SplitDecision()
    {
        var members = new[]
        {
            Usable("architect", MakeReview(ReviewDecision.Approve)),
            Usable("developer", MakeReview(ReviewDecision.RequestChanges, Issue(ReviewSeverity.Major))),
        };

        var result = Agg.Aggregate(members, new Agg.PanelAggregationRules(Agg.PanelDecisionRule.Majority, 2), Subject);

        result.Decided.Should().BeFalse();
        result.Reason.Should().Be(Agg.PanelUndecidableReason.SplitDecision);
    }

    [Test]
    public void MajorityRule_ClearMajorityApprove_Approves()
    {
        var members = new[]
        {
            Usable("architect", MakeReview(ReviewDecision.Approve)),
            Usable("developer", MakeReview(ReviewDecision.Approve)),
            Usable("security", MakeReview(ReviewDecision.RequestChanges, Issue(ReviewSeverity.Major))),
        };

        var result = Agg.Aggregate(members, new Agg.PanelAggregationRules(Agg.PanelDecisionRule.Majority, 1), Subject);

        result.Decided.Should().BeTrue();
        result.Aggregate!.Decision.Should().Be(ReviewDecision.Approve);
    }

    [Test]
    public void AggregatedFrom_EqualsMemberIds_DuplicateFree()
    {
        var m1 = Usable("architect", MakeReview(ReviewDecision.Approve));
        var m2 = Usable("developer", MakeReview(ReviewDecision.Approve));
        var members = new[] { m1, m2 };

        var result = Agg.Aggregate(members, Agg.DefaultsFor(2), Subject);

        result.Aggregate!.AggregatedFrom.Should().BeEquivalentTo(new[] { m1.ReviewDocumentId!.Value, m2.ReviewDocumentId!.Value });
        result.Aggregate!.AggregatedFrom!.Distinct().Should().HaveCount(result.Aggregate!.AggregatedFrom!.Count);
    }

    [Test]
    public void ComputeDecision_AppliesBlockingVetoFirst()
    {
        var reviews = new[]
        {
            MakeReview(ReviewDecision.Approve),
            MakeReview(ReviewDecision.Approve),
            MakeReview(ReviewDecision.RequestChanges, Issue(ReviewSeverity.Critical)),
        };

        Agg.ComputeDecision(reviews, Agg.PanelDecisionRule.Majority).Should().Be(ReviewDecision.RequestChanges);
    }

    private static DocumentValidationResult ValidateAggregate(ReviewType aggregate)
    {
        var payload = JsonSerializer.Serialize(aggregate, DocumentJson.Options);
        using var doc = JsonDocument.Parse(payload);
        return new ReviewDocumentType().Validate(doc.RootElement);
    }
}
