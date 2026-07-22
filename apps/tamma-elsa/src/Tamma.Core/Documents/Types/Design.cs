using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamma.Core.Documents.Types;

/// <summary>
/// One design alternative (Story 39-4, Design Decision D7). Adds an explicit
/// <see cref="Id"/> so the recommendation can reference an alternative
/// unambiguously; the legacy <c>DesignParsing.ParseProposal</c> ignores unknown
/// members and reads <see cref="Name"/>/<see cref="Tradeoffs"/>, so the additive
/// id round-trips losslessly.
/// </summary>
public sealed record DesignAlternative
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("tradeoffs")] public string Tradeoffs { get; init; } = "";
}

/// <summary>
/// A technical design proposal: a load-bearing <see cref="Summary"/>, ≥1
/// <see cref="Alternatives"/> each with trade-offs, a <see cref="Recommendation"/>
/// rationale, and a <see cref="RecommendedAlternativeId"/> that must reference one
/// of the listed alternatives. The typed analogue of the legacy
/// <c>DesignProposal</c>; additive where the old model has no field (D7).
/// </summary>
public sealed record Design
{
    [JsonPropertyName("summary")] public string Summary { get; init; } = "";
    [JsonPropertyName("alternatives")] public IReadOnlyList<DesignAlternative> Alternatives { get; init; } = [];
    [JsonPropertyName("recommendation")] public string Recommendation { get; init; } = "";
    [JsonPropertyName("recommendedAlternativeId")] public string RecommendedAlternativeId { get; init; } = "";
    [JsonPropertyName("constraintEvaluation")] public string? ConstraintEvaluation { get; init; }
}

/// <summary>
/// <see cref="IDocumentType"/> for the <c>design</c> document (Story 39-4 AC5).
/// Enforces ≥1 alternative each with stated trade-offs, a non-empty summary
/// (subsumes <c>DesignParsing</c>'s fail-closed rule), and a recommendation that
/// references a listed alternative by id.
/// </summary>
public sealed class DesignDocumentType : IDocumentType
{
    /// <summary>Payload could not be deserialized into the typed shape.</summary>
    public const string MalformedPayload = "MALFORMED_PAYLOAD";

    /// <summary>No summary — subsumes ParseProposal's fail-closed rule (the load-bearing field).</summary>
    public const string MissingSummary = "MISSING_SUMMARY";

    /// <summary>Fewer than one design alternative — a design with no options weighs nothing.</summary>
    public const string NoAlternatives = "NO_ALTERNATIVES";

    /// <summary>An alternative states no trade-offs.</summary>
    public const string AlternativeMissingTradeoffs = "ALTERNATIVE_MISSING_TRADEOFFS";

    /// <summary>The recommendation references no listed alternative by id (AC5).</summary>
    public const string RecommendationUnknownAlternative = "RECOMMENDATION_UNKNOWN_ALTERNATIVE";

    public string Key => DocumentTypeKey.Design.ToWire();
    public int SchemaVersion => 1;
    public Type PayloadClrType => typeof(Design);

    public DocumentValidationResult Validate(JsonElement payload)
    {
        Design? doc;
        try
        {
            doc = payload.Deserialize<Design>(DocumentJson.Options);
        }
        catch (JsonException)
        {
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload could not be parsed as a design document."));
        }

        if (doc is null)
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload deserialized to null."));

        var violations = new List<DocumentViolation>();

        if (string.IsNullOrWhiteSpace(doc.Summary))
            violations.Add(new DocumentViolation(
                MissingSummary,
                "The design has no summary — the load-bearing overview a reviewer weighs is required."));

        var alternatives = doc.Alternatives ?? [];
        if (alternatives.Count == 0)
            violations.Add(new DocumentViolation(
                NoAlternatives, "The design lists no alternatives — a design must weigh at least one option."));

        var index = 0;
        foreach (var alt in alternatives)
        {
            index++;
            var label = string.IsNullOrWhiteSpace(alt.Id)
                ? (string.IsNullOrWhiteSpace(alt.Name) ? $"#{index}" : $"'{alt.Name}'")
                : $"'{alt.Id}'";

            if (string.IsNullOrWhiteSpace(alt.Tradeoffs))
                violations.Add(new DocumentViolation(
                    AlternativeMissingTradeoffs,
                    $"Alternative {label} states no trade-offs — every alternative must state what it costs."));
        }

        var ids = alternatives.Select(a => a.Id?.Trim() ?? "").Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (!ids.Contains(doc.RecommendedAlternativeId?.Trim() ?? ""))
            violations.Add(new DocumentViolation(
                RecommendationUnknownAlternative,
                $"recommendedAlternativeId '{doc.RecommendedAlternativeId}' names no listed alternative — " +
                "the recommendation must reference one of the alternatives by id."));

        return violations.Count == 0
            ? DocumentValidationResult.Valid()
            : DocumentValidationResult.Invalid(violations.ToArray());
    }

    public string RenderContract() => Contract;

    public IReadOnlyList<DocumentExample> Examples => s_examples;

    // ── Contract + examples ──────────────────────────────────────────────────
    // The (architect, propose-design) cell is bound in ContractBindingTests to
    // DesignParsing.ParseProposal: "summary", "recommendation", "constraintEvaluation",
    // "alternatives", "name", "tradeoffs" — all appear below (plus the additive "id" /
    // "recommendedAlternativeId") so 39-16 regenerates the cell without breaking the binding.
    private const string Contract =
        """
        Return ONLY a JSON object of this shape:
        {
          "summary": "the recommended design in one or two sentences",
          "alternatives": [
            { "id": "ALT-1", "name": "short option name", "tradeoffs": "what this option costs and gains" }
          ],
          "recommendation": "why the chosen alternative wins the trade-offs",
          "recommendedAlternativeId": "ALT-1",
          "constraintEvaluation": "how the proposal meets the stated constraints"
        }
        Rules: list at least one alternative, each with non-empty "tradeoffs"; "summary" is
        required; "recommendedAlternativeId" must match the "id" of one listed alternative.
        """;

    private static readonly IReadOnlyList<DocumentExample> s_examples = new[]
    {
        new DocumentExample(
            "valid-two-alternatives",
            true,
            """
            {
              "summary": "Introduce a token-bucket limiter as middleware, keyed by tenant id.",
              "alternatives": [
                { "id": "ALT-1", "name": "Middleware token bucket", "tradeoffs": "Simple, in-process; loses state on restart" },
                { "id": "ALT-2", "name": "Redis-backed limiter", "tradeoffs": "Durable, shared; adds a Redis dependency" }
              ],
              "recommendation": "ALT-1 is lowest-risk for the current single-instance deployment.",
              "recommendedAlternativeId": "ALT-1",
              "constraintEvaluation": "Meets the no-new-infra constraint."
            }
            """),
        new DocumentExample(
            "invalid-recommendation-names-no-alternative",
            false,
            """
            {
              "summary": "Two options, but the recommendation points nowhere.",
              "alternatives": [
                { "id": "ALT-1", "name": "A", "tradeoffs": "cheap but slow" },
                { "id": "ALT-2", "name": "B", "tradeoffs": "fast but costly" }
              ],
              "recommendation": "Go with the third one.",
              "recommendedAlternativeId": "ALT-9"
            }
            """,
            new[] { RecommendationUnknownAlternative }),
    };
}
