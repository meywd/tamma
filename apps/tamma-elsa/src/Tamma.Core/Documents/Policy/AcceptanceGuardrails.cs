using Tamma.Core.Documents.Types;

namespace Tamma.Core.Documents.Policy;

/// <summary>
/// The projection of 39-4's review the guardrails read (Design Decision D8). It
/// carries 39-4's <see cref="ReviewDecision"/> DIRECTLY (referenced from
/// <c>Tamma.Core/Documents/Types/Review.cs</c> — <c>Approve | RequestChanges |
/// NeedsDiscussion</c>), never a local shadow copy, plus whether any blocking
/// (critical-severity) issue exists.
/// </summary>
public sealed record ReviewFacts(ReviewDecision Decision, bool HasBlockingIssues);

/// <summary>
/// Everything the pure guardrails need to gate one acceptor decision. The
/// <see cref="DeciderChannel"/> is typed as <see cref="ApprovalChannel"/> — a
/// <c>[Wire]</c> enum OWNED BY 39-5 (39-8 maps its server-derived resume
/// transport onto it).
/// </summary>
public sealed record AcceptanceGateContext(
    DocumentTypeKey DocumentType,
    string? AgentActionWire,
    ReviewFacts Review,
    int RoundsUsed,
    AcceptanceRules Rules,
    ApprovalChannel DeciderChannel);

/// <summary>
/// The deterministic hard guardrails AROUND the acceptor's decision (Story 39-5
/// AC8). PURE — no I/O, no Elsa, no configuration read. It validates and clamps a
/// decision; it never makes one. <see cref="Clamp"/> can only pass a decision
/// through or convert it to <see cref="AcceptanceDecision.Escalate"/> — it
/// structurally cannot manufacture an <see cref="AcceptanceDecision.Accept"/>
/// from a non-Accept input.
/// </summary>
public static class AcceptanceGuardrails
{
    /// <summary>
    /// Pre-gate BEFORE any acceptor runs (AC8a): an always-escalate class match
    /// short-circuits to <c>Escalate(AlwaysEscalateClass)</c>; a rounds-exhausted
    /// state short-circuits to <c>Escalate(RoundsExhausted)</c>. Returns
    /// <c>true</c> (with <paramref name="escalation"/> set) when the request must
    /// escalate before an acceptor is even consulted.
    /// </summary>
    public static bool TryPreGate(AcceptanceGateContext ctx, out AcceptanceDecision.Escalate escalation)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Always-escalate class match (by document type or by agent action).
        foreach (var cls in ctx.Rules.AlwaysEscalate)
        {
            var matched = cls.Kind switch
            {
                EscalationClassKind.DocumentType =>
                    string.Equals(cls.Key, ctx.DocumentType.ToWire(), StringComparison.Ordinal),
                EscalationClassKind.AgentAction =>
                    ctx.AgentActionWire is not null &&
                    string.Equals(cls.Key, ctx.AgentActionWire, StringComparison.Ordinal),
                _ => false,
            };
            if (matched)
            {
                escalation = new AcceptanceDecision.Escalate(
                    AcceptanceEscalationReason.AlwaysEscalateClass,
                    $"Document/action class '{cls.Key}' ({cls.Kind.ToWire()}) is configured to always escalate.");
                return true;
            }
        }

        // Rounds exhausted.
        if (ctx.RoundsUsed >= ctx.Rules.MaxRevisionRounds)
        {
            escalation = new AcceptanceDecision.Escalate(
                AcceptanceEscalationReason.RoundsExhausted,
                $"Revision rounds exhausted: {ctx.RoundsUsed} used of a {ctx.Rules.MaxRevisionRounds}-round budget.");
            return true;
        }

        escalation = null!;
        return false;
    }

    /// <summary>
    /// Post-gate clamp AROUND the acceptor's <paramref name="proposed"/> decision
    /// (AC8b):
    /// <list type="bullet">
    /// <item><c>Accept</c> for a review that is not <c>Approve</c> OR carries a
    ///   blocking issue → <c>Escalate(BlockingReviewViolation)</c> (forged approval).</item>
    /// <item><c>Reject</c> on the <c>orchestrator</c> channel →
    ///   <c>Escalate(RejectRequiresHuman)</c> (reject is human-only).</item>
    /// <item><c>RequestRevision</c> that would exceed the round budget →
    ///   <c>Escalate(RoundsExhausted)</c>.</item>
    /// <item>anything else → pass through unchanged.</item>
    /// </list>
    /// </summary>
    public static AcceptanceDecision Clamp(AcceptanceDecision proposed, AcceptanceGateContext ctx)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(ctx);

        switch (proposed)
        {
            case AcceptanceDecision.Accept:
                if (ctx.Review.Decision != ReviewDecision.Approve || ctx.Review.HasBlockingIssues)
                {
                    return new AcceptanceDecision.Escalate(
                        AcceptanceEscalationReason.BlockingReviewViolation,
                        "Accept refused: the review is not a clean approval " +
                        $"(decision={ctx.Review.Decision.ToWire()}, hasBlockingIssues={ctx.Review.HasBlockingIssues}).");
                }
                return proposed;

            case AcceptanceDecision.Reject:
                if (ctx.DeciderChannel == ApprovalChannel.Orchestrator)
                {
                    return new AcceptanceDecision.Escalate(
                        AcceptanceEscalationReason.RejectRequiresHuman,
                        "Reject refused on the orchestrator channel: rejection is human-only; escalate instead.");
                }
                return proposed;

            case AcceptanceDecision.RequestRevision:
                if (ctx.RoundsUsed + 1 > ctx.Rules.MaxRevisionRounds)
                {
                    return new AcceptanceDecision.Escalate(
                        AcceptanceEscalationReason.RoundsExhausted,
                        $"RequestRevision refused: another round would exceed the {ctx.Rules.MaxRevisionRounds}-round budget " +
                        $"(rounds used={ctx.RoundsUsed}).");
                }
                return proposed;

            default:
                return proposed;
        }
    }
}
