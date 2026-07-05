namespace Tamma.Activities.Ambiguity;

/// <summary>
/// Story 3.6 — the threshold policy that turns a quantitative ambiguity score into a routing
/// DECISION (Story 3.6 AC6 — "ambiguity thresholds trigger appropriate workflows"). Pure and
/// side-effect-free so the decision boundary is unit-testable without a live LLM. The
/// <c>AmbiguityScoringWorkflow</c> reads a caller-supplied threshold, clamps it, and routes
/// score ≥ threshold to the sibling <c>ClarifyingQuestionsWorkflow</c> (Story 3.5) before
/// implementation proceeds.
/// </summary>
public static class AmbiguityThresholds
{
    /// <summary>
    /// Default clarify threshold used when the caller supplies none (or a non-positive value).
    /// A score at or above this routes the requirement to clarification. 0.5 is the neutral
    /// midpoint — a requirement judged more ambiguous than clear gets clarified first.
    /// </summary>
    public const decimal DefaultClarify = 0.5m;

    /// <summary>
    /// Resolve the effective threshold: a positive caller value (clamped to [0,1]) wins;
    /// anything ≤ 0 (i.e. "not supplied") falls back to <see cref="DefaultClarify"/>. A
    /// threshold of exactly 0 is rejected as "unset" because it would make every requirement —
    /// including a perfectly clear one scored 0 — trigger clarification, which is never useful.
    /// </summary>
    public static decimal Resolve(decimal requested)
        => requested <= 0m ? DefaultClarify : Clamp01(requested);

    /// <summary>
    /// The routing decision: <c>true</c> ⇒ score met/exceeded the threshold ⇒ trigger
    /// clarification; <c>false</c> ⇒ below threshold ⇒ proceed as-is.
    /// </summary>
    public static bool ShouldClarify(decimal score, decimal threshold)
        => score >= threshold;

    /// <summary>Clamp a value into [0,1]. Pure.</summary>
    public static decimal Clamp01(decimal value)
        => value < 0m ? 0m : value > 1m ? 1m : value;
}
