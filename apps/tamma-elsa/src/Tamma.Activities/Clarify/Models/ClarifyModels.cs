using System.Text.Json.Serialization;

namespace Tamma.Activities.Clarify.Models;

/// <summary>
/// Story 3.5 — the set of clarifying questions generated for an ambiguous
/// requirement. Serialised into the workflow's <c>questionsJson</c> variable and
/// surfaced to the stakeholder by <see cref="DeliverClarifyingQuestionsActivity"/>.
/// </summary>
public sealed class ClarifyQuestionSet
{
    /// <summary>The clarifying questions, prioritised (most impactful first).</summary>
    [JsonPropertyName("questions")]
    public List<string> Questions { get; set; } = new();

    /// <summary>Optional context summary the stakeholder can reference when answering.</summary>
    [JsonPropertyName("contextSummary")]
    public string? ContextSummary { get; set; }
}

/// <summary>Outcome of delivering the clarifying questions to the stakeholder.</summary>
public sealed class ClarifyDeliveryResult
{
    public bool Success { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime DeliveredAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The disambiguated requirement produced by incorporating the human answers.
/// Parsed defensively from the incorporation <c>llm-call</c> output; the workflow
/// fails closed (routes to the error terminal) if it cannot be recovered.
/// </summary>
public sealed class ClarificationResult
{
    /// <summary>The clarified / disambiguated requirement text.</summary>
    [JsonPropertyName("clarifiedRequirement")]
    public string ClarifiedRequirement { get; set; } = string.Empty;

    /// <summary>Remaining open points (empty when fully resolved).</summary>
    [JsonPropertyName("remainingAmbiguities")]
    public List<string> RemainingAmbiguities { get; set; } = new();

    /// <summary>Whether the workflow considers the requirement resolved.</summary>
    [JsonPropertyName("resolved")]
    public bool Resolved { get; set; }
}
