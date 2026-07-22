using System.Text.Json.Serialization;
using Tamma.Core;

namespace Tamma.Core.Documents.Policy;

/// <summary>
/// The payload the accept stage publishes on the workflow↔orchestrator channel
/// (Story 39-5 AC2/AC3): the finished document, its <c>Review</c>, its lineage,
/// the rounds used so far, the server-resolved rules (including the autonomy
/// level and version), and the decision-session id the 39-8 gate resumes on.
///
/// <para>The rules are resolved SERVER-SIDE and embedded here for context + audit
/// (the <see cref="ResolvedAcceptanceRules.Version"/> is what 39-6's decision
/// event records the decision was made under). The caller never passes rules from
/// the client — the same discipline as <c>LlmCallWorkflow</c>'s conventions
/// resolution.</para>
/// </summary>
public sealed record AcceptanceRequest
{
    [JsonPropertyName("decisionSessionId")]
    public required Guid DecisionSessionId { get; init; }

    [JsonPropertyName("document")]
    public required DocumentEnvelope Document { get; init; }

    [JsonPropertyName("review")]
    public required DocumentEnvelope Review { get; init; }

    [JsonPropertyName("lineage")]
    public required IReadOnlyList<DocumentEnvelope> Lineage { get; init; }

    [JsonPropertyName("roundsUsed")]
    public required int RoundsUsed { get; init; }

    [JsonPropertyName("rules")]
    public required ResolvedAcceptanceRules Rules { get; init; }

    [JsonPropertyName("issueId")]
    public required string IssueId { get; init; }
}

/// <summary>
/// The ONLY way to build an <see cref="AcceptanceRequest"/> (Story 39-5 Design
/// Decision D7). There is no autonomy-level branch and no accept/skip output:
/// every request goes to the orchestrator over the channel, regardless of
/// autonomy level. This makes "route around the orchestrator" unrepresentable at
/// the contract level; 39-6 re-pins the same invariant at the workflow level.
/// </summary>
public static class AcceptanceRequestFactory
{
    /// <summary>
    /// Build an orchestrator-bound acceptance request. Mints a fresh UUID v7
    /// decision-session id. Rejects a <paramref name="review"/> envelope whose
    /// type is not <c>review</c> (the request must carry the unified review
    /// document, 39-4) and a negative <paramref name="roundsUsed"/>.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>ACCEPTANCE_REQUEST.INVALID</c> for a non-review review envelope,
    /// an empty issue id, or a negative rounds count.
    /// </exception>
    public static AcceptanceRequest Create(
        DocumentEnvelope document,
        DocumentEnvelope review,
        IReadOnlyList<DocumentEnvelope> lineage,
        int roundsUsed,
        ResolvedAcceptanceRules rules)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(lineage);
        ArgumentNullException.ThrowIfNull(rules);

        var reviewKey = DocumentTypeKey.Review.ToWire();
        if (!string.Equals(review.Type, reviewKey, StringComparison.Ordinal))
            throw Invalid("review",
                $"The review envelope must be of type '{reviewKey}'; got '{review.Type}'.");

        if (string.IsNullOrWhiteSpace(document.IssueId))
            throw Invalid("issueId", "The document envelope carries no issueId.");

        if (roundsUsed < 0)
            throw Invalid("roundsUsed", $"RoundsUsed must be non-negative; got {roundsUsed}.");

        return new AcceptanceRequest
        {
            DecisionSessionId = UuidV7.NewGuid(),
            Document = document,
            Review = review,
            Lineage = lineage,
            RoundsUsed = roundsUsed,
            Rules = rules,
            IssueId = document.IssueId,
        };
    }

    private static TammaError Invalid(string field, string message) =>
        new(
            "ACCEPTANCE_REQUEST.INVALID",
            message,
            new Dictionary<string, object?> { ["field"] = field },
            retryable: false,
            severity: TammaErrorSeverity.High);
}
