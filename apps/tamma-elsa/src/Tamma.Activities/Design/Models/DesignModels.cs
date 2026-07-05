using System.Text.Json.Serialization;

namespace Tamma.Activities.Design.Models;

/// <summary>
/// Story 3.7 — a technical design proposal produced (via the mediated <c>llm-call</c>)
/// for a complex requirement. Parsed defensively from the generation <c>llm-call</c>
/// output; the workflow fails closed (routes to the error terminal) if the load-bearing
/// <see cref="Summary"/> cannot be recovered. Serialised into the workflow's
/// <c>proposalJson</c> variable and surfaced to the reviewer by
/// <see cref="Tamma.Activities.Design.DeliverDesignProposalActivity"/>.
/// </summary>
public sealed class DesignProposal
{
    /// <summary>The high-level summary of the recommended design (load-bearing field).</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>The candidate design alternatives with their trade-off analysis (AC 3).</summary>
    [JsonPropertyName("alternatives")]
    public List<DesignAlternative> Alternatives { get; set; } = new();

    /// <summary>The recommended approach + rationale (why this alternative wins the trade-offs).</summary>
    [JsonPropertyName("recommendation")]
    public string Recommendation { get; set; } = string.Empty;

    /// <summary>How the proposal was evaluated against the supplied technical / business
    /// constraints (AC 4). Empty when the model returned none.</summary>
    [JsonPropertyName("constraintEvaluation")]
    public string ConstraintEvaluation { get; set; } = string.Empty;
}

/// <summary>
/// A single design alternative in a <see cref="DesignProposal"/>: a named option with the
/// trade-offs a reviewer weighs before approving (AC 3 — "multiple design alternatives with
/// trade-off analysis").
/// </summary>
public sealed class DesignAlternative
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("tradeoffs")]
    public string Tradeoffs { get; set; } = string.Empty;
}

/// <summary>Outcome of delivering the design proposal to the reviewer / issue.</summary>
public sealed class DesignDeliveryResult
{
    public bool Success { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime DeliveredAt { get; set; } = DateTime.UtcNow;
}
