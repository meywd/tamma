using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents.Types;
using Tamma.ElsaServer.Workflows.Helpers;
using Agg = Tamma.ElsaServer.Workflows.Helpers.ReviewPanelAggregation;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-7 (AC8) — the recorded plan-review verdict corpus, replayed through the NEW
/// pipeline (<see cref="ReviewProducerHelper.MapReviewerReply"/> → panel members →
/// <see cref="ReviewPanelAggregation.Aggregate"/> at the default Unanimous + full-roster
/// config). The OLD <c>ReviewAggregationHelper</c> baseline that this harness once ran the
/// corpus against was DELETED in Story 39-14 (the migration that retired the bespoke
/// plan-review pipeline), so the old-side parity calls are removed; the recorded
/// expectations (what the old pipeline decided) are pinned inline as comments. The
/// load-bearing invariant survives: a set containing garbage/invalid members NEVER produces
/// a NEW <c>Approve</c>.
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

    // ── preserved-behaviour sets (old expectation pinned inline) ──

    [Test]
    public void AllApprove_NewApproves()
    {
        // OLD: AggregateVerdicts == true.
        var fixtures = new[] { ApproveStr, ObjApprove };
        var (decided, approve) = NewOutcome(fixtures);
        (decided && approve).Should().BeTrue("all-approve is preserved exactly");
    }

    [Test]
    public void OneConcerns_NewNonApprove()
    {
        // OLD: AggregateVerdicts == false.
        var fixtures = new[] { ApproveStr, ConcernsStr };
        var (decided, approve) = NewOutcome(fixtures);
        decided.Should().BeTrue();
        approve.Should().BeFalse("a concerns verdict makes the panel non-approve");
    }

    [Test]
    public void NeedsDiscussionCountsAsNonApprove()
    {
        // OLD: AggregateVerdicts == false.
        var fixtures = new[] { ApproveStr, ObjNeedsDiscussion };
        var (decided, approve) = NewOutcome(fixtures);
        decided.Should().BeTrue();
        approve.Should().BeFalse("needs-discussion counts as non-approve, matching the old 'concerns' mapping");
    }

    // ── documented divergences (D6) ──

    [Test]
    public void Divergence_EmptyPanel_NewIsUndecidable()
    {
        // OLD BUG: AggregateVerdicts([]) == true (All() on empty). NEW: EmptyPanel
        // undecidable — a deliberate divergence 39-14 relies on (we do NOT reproduce
        // the empty-approves bug).
        var (decided, approve) = NewOutcome(Array.Empty<string>());
        decided.Should().BeFalse("DIVERGENCE: an empty panel is undecidable, never a phantom approval");
        approve.Should().BeFalse();
    }

    [Test]
    public void Divergence_GarbageOrIncompleteMember_NewIsUndecidable()
    {
        // The object REQUEST_CHANGES fixture carries issues without a category/fix and
        // fix-less blocking issues → the strict Review type rejects it (routed to
        // repair). OLD laundered it to a "concerns" non-approve; NEW drops the panel
        // below quorum → undecidable. Deliberate D6 divergence — but still never approve.
        var fixtures = new[] { ApproveStr, ObjRequestChanges };
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

    // ── pipeline (new) ──

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
