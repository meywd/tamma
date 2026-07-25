using System.Text.Json.Serialization;
using Tamma.Core.Documents.Policy;

namespace Tamma.Api.Dtos.AcceptanceRules;

/// <summary>
/// PUT body for <c>/api/acceptance-rules/{documentTypeKey}</c> (Story 39-5). The
/// shape mirrors <see cref="AcceptanceRules"/>; <see cref="ToRules"/> maps it to
/// the domain record which the service validates fail-loud (rejects out-of-range
/// knobs and unknown taxonomy keys) before any write.
/// </summary>
public sealed record AcceptanceRulesUpsertRequest(
    [property: JsonPropertyName("autonomyLevel")] int AutonomyLevel,
    [property: JsonPropertyName("maxRevisionRounds")] int MaxRevisionRounds,
    [property: JsonPropertyName("maxValidationRepairAttempts")] int MaxValidationRepairAttempts,
    [property: JsonPropertyName("ambiguityEscalationThreshold")] double AmbiguityEscalationThreshold,
    [property: JsonPropertyName("alwaysEscalate")] IReadOnlyList<EscalationClass>? AlwaysEscalate,
    [property: JsonPropertyName("reviewerSelection")] ReviewerSelection ReviewerSelection,
    [property: JsonPropertyName("decisionGuidance")] string DecisionGuidance,
    [property: JsonPropertyName("routingGuidance")] string RoutingGuidance,
    // Story 39-13 D4 — the per-type autonomy floor. Trailing + defaulted so a body
    // written before the field existed still binds, to `any` (today's behavior).
    [property: JsonPropertyName("acceptorRequirement")] AcceptorRequirement AcceptorRequirement
        = AcceptorRequirement.Any)
{
    /// <summary>Map to the domain record (unvalidated — the service calls <c>Validate()</c>).</summary>
    public Tamma.Core.Documents.Policy.AcceptanceRules ToRules() => new()
    {
        AutonomyLevel = AutonomyLevel,
        MaxRevisionRounds = MaxRevisionRounds,
        MaxValidationRepairAttempts = MaxValidationRepairAttempts,
        AmbiguityEscalationThreshold = AmbiguityEscalationThreshold,
        AlwaysEscalate = AlwaysEscalate ?? Array.Empty<EscalationClass>(),
        ReviewerSelection = ReviewerSelection,
        DecisionGuidance = DecisionGuidance ?? string.Empty,
        RoutingGuidance = RoutingGuidance ?? string.Empty,
        AcceptorRequirement = AcceptorRequirement,
    };
}
