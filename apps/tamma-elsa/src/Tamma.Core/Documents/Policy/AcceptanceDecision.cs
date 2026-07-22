using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;

namespace Tamma.Core.Documents.Policy;

/// <summary>
/// The closed acceptor decision vocabulary (Story 39-5 AC2). Serialized
/// polymorphically with a <c>kind</c> discriminator through
/// <see cref="AcceptanceRulesJson.Options"/>; the derived-type set is closed —
/// there is NO <c>AutoAccept</c> member, so the lifecycle cannot route around
/// the orchestrator at the contract level (Design Decision D7).
///
/// <para><c>Reject</c> is HUMAN-ONLY (settled design review 2026-07-21): the
/// orchestrator cannot reject without escalating. A <c>Reject</c> arriving on the
/// <c>orchestrator</c> channel is clamped to <c>Escalate(RejectRequiresHuman)</c>
/// by <see cref="AcceptanceGuardrails.Clamp"/>.</para>
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Accept), "accept")]
[JsonDerivedType(typeof(RequestRevision), "request-revision")]
[JsonDerivedType(typeof(Reject), "reject")]
[JsonDerivedType(typeof(Escalate), "escalate")]
public abstract record AcceptanceDecision
{
    /// <summary>A final "yes" — the document is accepted and published.</summary>
    public sealed record Accept : AcceptanceDecision;

    /// <summary>Send the document back for another revision round with reviewer notes.</summary>
    public sealed record RequestRevision(
        [property: JsonPropertyName("notes")] string Notes) : AcceptanceDecision;

    /// <summary>A final "no" → the document reaches the <c>Rejected</c> state. HUMAN-ONLY.</summary>
    public sealed record Reject(
        [property: JsonPropertyName("reason")] string Reason) : AcceptanceDecision;

    /// <summary>Hand the decision up to a human via the 39-8 escalation surface.</summary>
    public sealed record Escalate(
        [property: JsonPropertyName("reason")] AcceptanceEscalationReason Reason,
        [property: JsonPropertyName("detail")] string Detail) : AcceptanceDecision;
}

/// <summary>
/// The closed set of reasons a decision escalates (Story 39-5 Design Decision
/// D10 — EXACTLY 6 members, count-pinned). Two members map 1:1 onto 39-2's
/// <see cref="DocumentLifecycleOutcome"/> via
/// <see cref="AcceptanceEscalationReasonExtensions.ToLifecycleOutcome"/>; the
/// other four are escalation-only and map to <c>null</c>.
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<AcceptanceEscalationReason>))]
public enum AcceptanceEscalationReason
{
    [Wire("rounds-exhausted")]          RoundsExhausted,
    [Wire("always-escalate-class")]     AlwaysEscalateClass,
    [Wire("blocking-review-violation")] BlockingReviewViolation,
    [Wire("ambiguity-above-threshold")] AmbiguityAboveThreshold,
    [Wire("acceptor-judgment")]         AcceptorJudgment,
    [Wire("reject-requires-human")]     RejectRequiresHuman,
}

/// <summary><see cref="AcceptanceEscalationReason"/> wire + lifecycle-outcome mapping (D10).</summary>
public static class AcceptanceEscalationReasonExtensions
{
    /// <summary>The canonical wire string for <paramref name="reason"/>.</summary>
    public static string ToWire(this AcceptanceEscalationReason reason) =>
        EnumWire<AcceptanceEscalationReason>.ToWire(reason);

    /// <summary>
    /// Maps an escalation reason to its terminal <see cref="DocumentLifecycleOutcome"/>,
    /// or <c>null</c> when the reason is escalation-only (never a terminal
    /// lifecycle state). Drift-pinned so 39-6 never string-matches (D10):
    /// <list type="bullet">
    /// <item><c>RoundsExhausted</c> → <see cref="DocumentLifecycleOutcome.RoundsExhausted"/></item>
    /// <item><c>AmbiguityAboveThreshold</c> → <see cref="DocumentLifecycleOutcome.AmbiguityAboveThreshold"/></item>
    /// <item><c>AlwaysEscalateClass</c> / <c>BlockingReviewViolation</c> /
    ///   <c>AcceptorJudgment</c> / <c>RejectRequiresHuman</c> → <c>null</c></item>
    /// </list>
    /// </summary>
    public static DocumentLifecycleOutcome? ToLifecycleOutcome(this AcceptanceEscalationReason reason) =>
        reason switch
        {
            AcceptanceEscalationReason.RoundsExhausted => DocumentLifecycleOutcome.RoundsExhausted,
            AcceptanceEscalationReason.AmbiguityAboveThreshold => DocumentLifecycleOutcome.AmbiguityAboveThreshold,
            _ => null,
        };
}
