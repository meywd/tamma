using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows.Helpers;
using Agg = Tamma.ElsaServer.Workflows.Helpers.ReviewPanelAggregation;
using CoreReview = Tamma.Core.Documents.Types.Review;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-14 (AC4) — the deleted <c>PlanReviewDecisionTests</c> corpus, re-asserted through the
/// TYPED mechanism: legacy verdict shapes fold via <c>Review.FromLegacyVerdictJson</c> /
/// 39-7's <see cref="ReviewProducerHelper.MapReviewerReply"/>; aggregation runs through
/// <see cref="ReviewPanelAggregation"/>; the flagship blocking rule is executable
/// (<see cref="ReviewDocumentType.Validate"/> rejects Approve-with-blocking, and 39-5's
/// <see cref="AcceptanceGuardrails.Clamp"/> converts a forged Accept to Escalate). The old
/// pessimistic-default behaviours become TYPED errors (settled by the lifecycle, not laundered).
///
/// <para>Discussion-result cases (<c>needsModification</c> → revise, <c>needsHuman</c> → escalate)
/// map to their lifecycle equivalents, asserted in <c>PlanningFamilyLifecycleExecutionTests</c>
/// (b)/(c) — cross-referenced here by comment, not re-implemented against a retired parser.</para>
/// </summary>
[TestFixture]
public class PlanReviewDecisionPortedTests
{
    private static readonly ReviewSubject Subject = new()
    {
        Kind = "document",
        DocumentId = Guid.Parse("0192a8b0-1111-7abc-8def-000000000001"),
        DocumentType = "plan",
    };

    // ── verdict shapes → typed decision (ported from ParseRoleVerdict) ──

    [TestCase("""{"verdict": "approve", "comments": "LGTM", "suggestedChanges": ""}""", ReviewDecision.Approve)]
    [TestCase("""{"verdict": "concerns", "comments": "Missing error handling"}""", ReviewDecision.RequestChanges)]
    [TestCase("""{"issues": [], "verdict": {"decision": "APPROVE", "summary": "Plan is solid", "blockingIssues": []}}""", ReviewDecision.Approve)]
    [TestCase("""{"verdict": {"decision": "NEEDS_DISCUSSION", "summary": "Scope unclear"}}""", ReviewDecision.NeedsDiscussion)]
    [TestCase("""{"verdict": {"decision": "approve"}}""", ReviewDecision.Approve)]
    public void LegacyVerdict_FoldsToTypedDecision(string json, ReviewDecision expected)
        => CoreReview.FromLegacyVerdictJson(json, Subject).Decision.Should().Be(expected);

    [Test]
    public void ObjectVerdict_TopLevelCommentsWin_MapsToSummary()
    {
        const string json = """
        { "verdict": {"decision": "APPROVE", "summary": "inner summary"}, "comments": "explicit top-level comments" }
        """;
        var review = CoreReview.FromLegacyVerdictJson(json, Subject);
        review.Decision.Should().Be(ReviewDecision.Approve);
        // The object shape carries its summary; the unified reader keeps the object summary
        // (top-level comments are only lifted for the STRING shape) — the fold is deterministic.
        review.Summary.Should().Be("inner summary");
    }

    // ── the old pessimistic default becomes a TYPED error (not a document) ──

    [TestCase("{}")]
    [TestCase("not json at all")]
    [TestCase("")]
    public void Garbage_IsATypedError_NotAConcernsDefault(string garbage)
    {
        var act = () => CoreReview.FromLegacyVerdictJson(garbage, Subject);
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.REVIEW.LEGACY_UNPARSEABLE");
    }

    [TestCase("""{"verdict": {"decision": "SHIP_IT"}}""")]
    [TestCase("""{"verdict": {"summary": "no decision field"}}""")]
    public void UnknownOrMissingDecision_IsATypedError(string json)
    {
        var act = () => CoreReview.FromLegacyVerdictJson(json, Subject);
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.REVIEW.UNKNOWN_DECISION");
    }

    // ── aggregation (ported from AggregateVerdicts) ─────────────────────

    [Test]
    public void AllSevenApprove_PanelApproves()
    {
        var reviews = Enumerable.Range(0, 7).Select(_ => Approve()).ToList();
        Agg.ComputeDecision(reviews, Agg.PanelDecisionRule.Unanimous).Should().Be(ReviewDecision.Approve);
    }

    [Test]
    public void OneRoleConcerns_PanelDoesNotApprove()
    {
        var reviews = new List<CoreReview> { Approve(), Approve(), RequestChanges(), Approve(), Approve(), Approve(), Approve() };
        Agg.ComputeDecision(reviews, Agg.PanelDecisionRule.Unanimous).Should().Be(ReviewDecision.RequestChanges);
    }

    [Test]
    public void AllConcerns_PanelDoesNotApprove()
    {
        var reviews = Enumerable.Range(0, 7).Select(_ => RequestChanges()).ToList();
        Agg.ComputeDecision(reviews, Agg.PanelDecisionRule.Unanimous).Should().Be(ReviewDecision.RequestChanges);
    }

    [Test]
    public void EmptyPanel_IsUndecidable_NotApproved()
    {
        // OLD BUG: AggregateVerdicts([]) == true (All() on empty). The typed aggregation surfaces
        // an EmptyPanel undecidable — the documented D6 divergence, never a phantom approval.
        var result = Agg.Aggregate(Array.Empty<Agg.PanelMember>(), Agg.DefaultsFor(0), Subject);
        result.Decided.Should().BeFalse();
        result.Reason.Should().Be(Agg.PanelUndecidableReason.EmptyPanel);
    }

    // ── AC4 flagship: blocking-issue rule is EXECUTABLE ─────────────────

    [Test]
    public void ApproveWithBlockingIssue_IsUnrepresentableAsAValidReview()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new CoreReview
        {
            Subject = Subject,
            Decision = ReviewDecision.Approve,
            Summary = "approving despite a blocker",
            Issues = new[] { new ReviewIssue(ReviewSeverity.Critical, "security", "SQL injection", "parameterize the query") },
        }, DocumentJson.Options);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = new ReviewDocumentType().Validate(doc.RootElement);

        result.IsValid.Should().BeFalse();
        result.Violations.Should().Contain(v => v.Code == ReviewDocumentType.ApproveWithBlockingIssues,
            "an approval cannot coexist with a blocking (critical) issue — the domain rule is executable (AC4)");
    }

    [Test]
    public void ForgedAccept_OnABlockingReview_IsClampedToEscalate()
    {
        // 39-5's guardrail: a forged Accept on a review that is not a clean approval (blocking issue)
        // is clamped to Escalate(BlockingReviewViolation) — the accept gate refuses (AC4 unit half).
        var ctx = GateContext(new ReviewFacts(ReviewDecision.Approve, HasBlockingIssues: true));
        var clamped = AcceptanceGuardrails.Clamp(new AcceptanceDecision.Accept(), ctx);

        clamped.Should().BeOfType<AcceptanceDecision.Escalate>()
            .Which.Reason.Should().Be(AcceptanceEscalationReason.BlockingReviewViolation);
    }

    [Test]
    public void ForgedAccept_OnANonApproveReview_IsClampedToEscalate()
    {
        var ctx = GateContext(new ReviewFacts(ReviewDecision.RequestChanges, HasBlockingIssues: false));
        AcceptanceGuardrails.Clamp(new AcceptanceDecision.Accept(), ctx)
            .Should().BeOfType<AcceptanceDecision.Escalate>()
            .Which.Reason.Should().Be(AcceptanceEscalationReason.BlockingReviewViolation);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static CoreReview Approve() => new() { Subject = Subject, Decision = ReviewDecision.Approve, Summary = "ok", Issues = [] };
    private static CoreReview RequestChanges() => new() { Subject = Subject, Decision = ReviewDecision.RequestChanges, Summary = "concerns", Issues = [] };

    private static AcceptanceGateContext GateContext(ReviewFacts review) => new(
        DocumentType: DocumentTypeKey.Plan,
        AgentActionWire: null,
        Review: review,
        RoundsUsed: 0,
        Rules: AcceptanceDefaults.For(DocumentTypeKey.Plan),
        DeciderChannel: ApprovalChannel.Orchestrator);
}
