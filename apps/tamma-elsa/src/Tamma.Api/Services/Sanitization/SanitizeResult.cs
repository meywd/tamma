using System.Text.Json.Serialization;

namespace Tamma.Api.Services.Sanitization;

/// <summary>
/// One entry in <see cref="SanitizeResult.Hits"/>: how many times a specific
/// rule matched the input during a single sanitize call.
/// </summary>
public sealed record SanitizationHit(
    [property: JsonPropertyName("ruleName")] string RuleName,
    [property: JsonPropertyName("count")] int Count);

/// <summary>
/// The outcome of a <see cref="ISanitizationService.SanitizeAsync"/> call.
/// <see cref="SanitizedText"/> is the input with all matching rules applied
/// in priority order. <see cref="Hits"/> enumerates which rules fired and
/// how many times, for observability without leaking the matched content.
/// </summary>
public sealed record SanitizeResult(
    [property: JsonPropertyName("sanitizedText")] string SanitizedText,
    [property: JsonPropertyName("hits")] IReadOnlyList<SanitizationHit> Hits);
