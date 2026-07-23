using Tamma.Core.Documents.Types;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Story 39-7 (Design Decision D6) — the PURE panel aggregation over
/// <c>IReadOnlyList&lt;<see cref="Review"/>&gt;</c> (AC3). NO JSON parsing anywhere
/// in this file: it operates on already-mapped unified reviews. The blocking veto
/// (<see cref="ReviewSeverityExtensions.IsBlocking"/>) is an INVARIANT, not a knob —
/// any member carrying a Critical issue forces the aggregate to
/// <see cref="ReviewDecision.RequestChanges"/> regardless of the decision rule, and
/// because the aggregate's <c>Issues</c> are the concatenation of member issues, an
/// <c>Approve</c> aggregate with a member's Critical issue would itself fail
/// <see cref="ReviewDocumentType.Validate"/> (<c>APPROVE_WITH_BLOCKING_ISSUES</c>).
///
/// <para><b>Default config = <see cref="PanelDecisionRule.Unanimous"/> +
/// <see cref="PanelAggregationRules.MinimumUsableReviews"/> = roster size</b> ⇒
/// behavioural parity with the retired legacy <c>AggregateVerdicts</c>
/// (<c>All(approve)</c>). Deliberate divergences (AC8): an empty panel is
/// <see cref="PanelUndecidableReason.EmptyPanel"/> (NOT approved — the old
/// <c>All()</c>-on-empty bug); a garbage/failed member drops the panel below quorum
/// to <see cref="PanelUndecidableReason.BelowQuorum"/> (NOT laundered to a
/// pessimistic concerns verdict).</para>
/// </summary>
public static class ReviewPanelAggregation
{
    /// <summary>How the panel resolves its aggregate decision (39-5 <c>ReviewDecisionRule</c>).</summary>
    public enum PanelDecisionRule
    {
        Unanimous,
        Majority,
    }

    /// <summary>Why a panel could not decide (AC6 — surfaced typed, never silently resolved).</summary>
    public enum PanelUndecidableReason
    {
        EmptyPanel,
        BelowQuorum,
        SplitDecision,
    }

    /// <summary>
    /// One panel member's outcome. <see cref="Ok"/> + a non-null <see cref="Review"/>
    /// marks a USABLE member; a failed member carries <see cref="FailureKind"/> and
    /// is never coalesced into a phantom participant.
    /// </summary>
    public sealed record PanelMember(
        string Role,
        Guid? ReviewDocumentId,
        Review? Review,
        bool Ok,
        string? FailureKind)
    {
        public bool IsUsable => Ok && Review is not null;
    }

    /// <summary>The two aggregation knobs (D6).</summary>
    public sealed record PanelAggregationRules(PanelDecisionRule DecisionRule, int MinimumUsableReviews);

    /// <summary>
    /// The aggregation outcome. <see cref="Decided"/> carries an
    /// <see cref="Aggregate"/> review; an undecidable result carries a
    /// <see cref="Reason"/> and null aggregate, plus ALL member review ids (AC6).
    /// </summary>
    public sealed record PanelResult(
        bool Decided,
        Review? Aggregate,
        IReadOnlyList<Guid> MemberReviewIds,
        int SucceededCount,
        IReadOnlyList<string> FailedRoles,
        PanelUndecidableReason? Reason);

    /// <summary>
    /// The default aggregation config for a roster of <paramref name="rosterSize"/>:
    /// unanimous + full-roster minimum — the legacy <c>AggregateVerdicts</c>
    /// parity configuration.
    /// </summary>
    public static PanelAggregationRules DefaultsFor(int rosterSize) =>
        new(PanelDecisionRule.Unanimous, rosterSize);

    /// <summary>
    /// AC3's pure decision core over the usable members' decisions, with the
    /// blocking veto applied first (any Critical issue ⇒ RequestChanges). A Majority
    /// tie defaults to the safe RequestChanges here — the caller
    /// (<see cref="Aggregate"/>) detects the tie separately to surface it as a
    /// <see cref="PanelUndecidableReason.SplitDecision"/>.
    /// </summary>
    public static ReviewDecision ComputeDecision(IReadOnlyList<Review> usableReviews, PanelDecisionRule rule)
    {
        if (HasBlockingIssue(usableReviews))
            return ReviewDecision.RequestChanges;

        var approve = usableReviews.Count(r => r.Decision == ReviewDecision.Approve);
        var nonApprove = usableReviews.Count - approve;

        return rule switch
        {
            PanelDecisionRule.Unanimous => nonApprove == 0 ? ReviewDecision.Approve : ReviewDecision.RequestChanges,
            PanelDecisionRule.Majority => approve > nonApprove ? ReviewDecision.Approve : ReviewDecision.RequestChanges,
            _ => ReviewDecision.RequestChanges,
        };
    }

    /// <summary>
    /// Aggregate the panel members into a decided aggregate <see cref="Review"/> or a
    /// typed undecidable result (D6/AC6). The aggregate's subject is the reviewed
    /// <paramref name="subject"/>; its issues are the concatenation of member issues;
    /// its <c>AggregatedFrom</c> lists the contributing member review ids.
    /// </summary>
    public static PanelResult Aggregate(
        IReadOnlyList<PanelMember> members,
        PanelAggregationRules rules,
        ReviewSubject subject)
    {
        var usable = members.Where(m => m.IsUsable).ToList();
        var usableReviews = usable.Select(m => m.Review!).ToList();
        var allMemberIds = members
            .Where(m => m.ReviewDocumentId.HasValue)
            .Select(m => m.ReviewDocumentId!.Value)
            .ToList();
        var failedRoles = members.Where(m => !m.IsUsable).Select(m => m.Role).ToList();

        // Divergence (a): empty panel is undecidable, NOT the old All()-on-empty approve.
        if (usableReviews.Count == 0)
            return Undecidable(PanelUndecidableReason.EmptyPanel, allMemberIds, 0, failedRoles);

        // Divergence (b): a below-quorum panel (a failed/garbage member under the
        // full-roster minimum) is undecidable, NOT laundered to a pessimistic verdict.
        if (usableReviews.Count < rules.MinimumUsableReviews)
            return Undecidable(PanelUndecidableReason.BelowQuorum, allMemberIds, usableReviews.Count, failedRoles);

        // Majority split (no blocking veto in play) is undecidable.
        if (rules.DecisionRule == PanelDecisionRule.Majority &&
            !HasBlockingIssue(usableReviews) &&
            IsSplit(usableReviews))
            return Undecidable(PanelUndecidableReason.SplitDecision, allMemberIds, usableReviews.Count, failedRoles);

        var decision = ComputeDecision(usableReviews, rules.DecisionRule);
        var aggregatedFrom = usable
            .Where(m => m.ReviewDocumentId.HasValue)
            .Select(m => m.ReviewDocumentId!.Value)
            .ToList();

        var issues = usable.SelectMany(m => m.Review!.Issues).ToList();
        var summary = string.Join(
            " | ",
            usable.Select(m => $"{m.Role}: {m.Review!.Decision.ToWire()} — {Trim(m.Review!.Summary)}"));

        var aggregate = new Review
        {
            Subject = subject,
            Decision = decision,
            Summary = string.IsNullOrWhiteSpace(summary) ? "Panel aggregate review." : summary,
            Issues = issues,
            AggregatedFrom = aggregatedFrom.Count > 0 ? aggregatedFrom : null,
        };

        return new PanelResult(
            Decided: true,
            Aggregate: aggregate,
            MemberReviewIds: allMemberIds,
            SucceededCount: usableReviews.Count,
            FailedRoles: failedRoles,
            Reason: null);
    }

    private static PanelResult Undecidable(
        PanelUndecidableReason reason, IReadOnlyList<Guid> memberIds, int succeeded, IReadOnlyList<string> failedRoles) =>
        new(false, null, memberIds, succeeded, failedRoles, reason);

    private static bool HasBlockingIssue(IReadOnlyList<Review> reviews) =>
        reviews.Any(r => r.Issues.Any(i => i.Severity.IsBlocking()));

    /// <summary>An exact approve/non-approve tie among the usable members.</summary>
    private static bool IsSplit(IReadOnlyList<Review> reviews)
    {
        var approve = reviews.Count(r => r.Decision == ReviewDecision.Approve);
        var nonApprove = reviews.Count - approve;
        return approve == nonApprove;
    }

    private static string Trim(string? s)
    {
        s ??= string.Empty;
        return s.Length <= 120 ? s : s[..120];
    }
}
