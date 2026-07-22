using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows.Helpers;
using Agg = Tamma.ElsaServer.Workflows.Helpers.ReviewPanelAggregation;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-7 (AC8) — the behavioural PARITY harness. Replays the recorded
/// <c>ParseRoleVerdict</c> verdict fixtures (the corpus <see cref="PlanReviewDecisionTests"/>
/// pins) through BOTH pipelines:
/// <list type="bullet">
///   <item>OLD: <c>AggregateVerdicts(fixtures.Select(ParseRoleVerdict))</c> — the
///     <see cref="ReviewAggregationHelper"/> baseline 39-14 retires.</item>
///   <item>NEW: <see cref="ReviewProducerHelper.MapReviewerReply"/> → panel members →
///     <see cref="ReviewPanelAggregation.Aggregate"/> at the default (Unanimous +
///     full-roster) config.</item>
/// </list>
/// Parity is asserted where behaviour is PRESERVED, and the Design Decision D6
/// divergences (empty panel; a garbage/incomplete member) are asserted AS
/// divergences with narrative comments — never silently. The invariant across every
/// fixture: a set containing garbage NEVER produces a NEW <c>Approve</c>.
/// </summary>
[TestFixture]
public class ReviewParityTests
{
    // ── the recorded corpus (mirrors PlanReviewDecisionTests fixtures) ──
    private const string ApproveStr = """{"verdict": "approve", "comments": "LGTM", "suggestedChanges": ""}""";
    private const string ConcernsStr = """{"verdict": "concerns", "comments": "Missing error handling"}""";
    private const string ObjApprove = """{"issues": [], "verdict": {"decision": "APPROVE", "summary": "Plan is solid", "blockingIssues": []}}""";
    private const string ObjRequestChanges = """{"issues": [{"task": "T1", "severity": "major", "issue": "No rollback"}], "verdict": {"decision": "REQUEST_CHANGES", "summary": "Missing rollback plan", "blockingIssues": ["No rollback strategy"]}}""";
    private const string ObjNeedsDiscussion = """{"verdict": {"decision": "NEEDS_DISCUSSION", "summary": "Scope unclear"}}""";
    private const string Garbage = "not json at all";
    private const string EmptyStr = "";

    private static readonly ReviewSubject Subject = new()
    {
        Kind = "document",
        DocumentId = Guid.Parse("0192a8b0-1111-7abc-8def-000000000001"),
        DocumentType = "plan",
    };

    // ── parity-preserved sets ──

    [Test]
    public void Parity_AllApprove_BothApprove()
    {
        var fixtures = new[] { ApproveStr, ObjApprove };
        OldApprove(fixtures).Should().BeTrue();
        var (decided, approve) = NewOutcome(fixtures);
        (decided && approve).Should().BeTrue("all-approve is preserved exactly");
    }

    [Test]
    public void Parity_OneConcerns_BothNonApprove()
    {
        var fixtures = new[] { ApproveStr, ConcernsStr };
        OldApprove(fixtures).Should().BeFalse();
        var (decided, approve) = NewOutcome(fixtures);
        decided.Should().BeTrue();
        approve.Should().BeFalse("a concerns verdict makes both pipelines non-approve");
    }

    [Test]
    public void Parity_NeedsDiscussionCountsAsNonApprove()
    {
        var fixtures = new[] { ApproveStr, ObjNeedsDiscussion };
        OldApprove(fixtures).Should().BeFalse();
        var (decided, approve) = NewOutcome(fixtures);
        decided.Should().BeTrue();
        approve.Should().BeFalse("needs-discussion counts as non-approve, matching the old 'concerns' mapping");
    }

    // ── documented divergences (D6) ──

    [Test]
    public void Divergence_EmptyPanel_OldApproves_NewIsUndecidable()
    {
        // OLD BUG: AggregateVerdicts([]) == true (All() on empty). NEW: EmptyPanel
        // undecidable — a deliberate divergence 39-14 relies on (we do NOT reproduce
        // the empty-approves bug).
        OldApprove(Array.Empty<string>()).Should().BeTrue("the old All()-on-empty bug approves an empty panel");
        var (decided, approve) = NewOutcome(Array.Empty<string>());
        decided.Should().BeFalse("DIVERGENCE: an empty panel is undecidable, never a phantom approval");
        approve.Should().BeFalse();
    }

    [Test]
    public void Divergence_GarbageOrIncompleteMember_OldDecidesNonApprove_NewIsUndecidable()
    {
        // The object REQUEST_CHANGES fixture carries issues without a category/fix and
        // fix-less blocking issues → the strict Review type rejects it (routed to
        // repair). OLD laundered it to a "concerns" non-approve; NEW drops the panel
        // below quorum → undecidable. Deliberate D6 divergence — but still never approve.
        var fixtures = new[] { ApproveStr, ObjRequestChanges };
        OldApprove(fixtures).Should().BeFalse("old laundered the incomplete member to a non-approve concerns");
        var (decided, approve) = NewOutcome(fixtures);
        decided.Should().BeFalse("DIVERGENCE: an unmappable/invalid member drops the panel below quorum → undecidable");
        approve.Should().BeFalse("the safe direction is preserved: never a false approval");
    }

    // ── the load-bearing invariant across the whole corpus ──

    [Test]
    public void Invariant_AnyGarbageSet_NeverProducesANewApprove()
    {
        var sets = new[]
        {
            new[] { Garbage, EmptyStr },
            new[] { ApproveStr, Garbage },
            new[] { ApproveStr, ObjRequestChanges },
            Array.Empty<string>(),
        };

        foreach (var set in sets)
        {
            var (_, approve) = NewOutcome(set);
            approve.Should().BeFalse(
                "a set containing garbage/invalid members must NEVER yield a new Approve (never a laundered approval)");
        }
    }

    // ── pipelines ──

    private static bool OldApprove(IReadOnlyList<string> fixtures)
    {
        var verdicts = fixtures.Select(f => ReviewAggregationHelper.ParseRoleVerdict(f).verdict);
        return ReviewAggregationHelper.AggregateVerdicts(verdicts);
    }

    private static (bool Decided, bool Approve) NewOutcome(IReadOnlyList<string> fixtures)
    {
        var members = new List<Agg.PanelMember>();
        foreach (var (fixture, i) in fixtures.Select((f, i) => (f, i)))
        {
            var map = ReviewProducerHelper.MapReviewerReply(fixture, Subject);
            members.Add(new Agg.PanelMember(
                Role: $"role{i}",
                ReviewDocumentId: map.IsValid ? Guid.NewGuid() : null,
                Review: map.Payload,
                Ok: map.IsValid,
                FailureKind: map.IsValid ? null : "validation-exhausted"));
        }

        var result = Agg.Aggregate(members, Agg.DefaultsFor(fixtures.Count), Subject);
        return (result.Decided, result.Decided && result.Aggregate!.Decision == ReviewDecision.Approve);
    }
}
