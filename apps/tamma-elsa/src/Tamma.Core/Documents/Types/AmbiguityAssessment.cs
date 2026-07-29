using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;

namespace Tamma.Core.Documents.Types;

/// <summary>
/// The canonical ambiguity-type vocabulary (Design Decision D5 — strict closed
/// set, exactly the members <c>AmbiguityParsing</c> enumerates today, including
/// <c>unspecified</c>). Synonym folding becomes producer-side normalization at
/// 39-13. Shipped as a <c>[Wire]</c> enum per the <c>AgentAction</c> pattern.
/// </summary>
public enum AmbiguityCategory
{
    [Wire("vague")] Vague,
    [Wire("missing")] Missing,
    [Wire("contradictory")] Contradictory,
    [Wire("implicit")] Implicit,
    [Wire("unspecified")] Unspecified,
}

/// <summary>
/// The canonical ambiguity-severity vocabulary (Design Decision D5). Shipped as a
/// <c>[Wire]</c> enum.
/// </summary>
public enum AmbiguitySeverity
{
    [Wire("low")] Low,
    [Wire("medium")] Medium,
    [Wire("high")] High,
}

/// <summary>
/// One detected ambiguity — the typed analogue of the legacy
/// <c>Tamma.Activities.Ambiguity.Models.AmbiguityItem</c> (Design Decision D2:
/// wire shape verbatim).
/// </summary>
public sealed record AmbiguityConcern
{
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("severity")] public string Severity { get; init; } = "";
    [JsonPropertyName("recommendation")] public string Recommendation { get; init; } = "";
}

/// <summary>
/// The structured ambiguity assessment for a requirement: a quantitative
/// <see cref="Score"/> ∈ [0,1], a <see cref="Rationale"/>, the scorer's
/// <see cref="Confidence"/>, and the itemised <see cref="Ambiguities"/> breakdown
/// (which may legitimately be empty for a clear requirement).
///
/// <para><see cref="Score"/> is <c>required</c>: an assessment whose whole point
/// is the score must carry one, so an absent score fails loud at the
/// deserialization boundary (mirrors the baseline fail-closed on a missing
/// score).</para>
/// </summary>
public sealed record AmbiguityAssessment
{
    [JsonPropertyName("score")] public required decimal Score { get; init; }
    [JsonPropertyName("rationale")] public string Rationale { get; init; } = "";
    [JsonPropertyName("confidence")] public decimal Confidence { get; init; }
    [JsonPropertyName("ambiguities")] public IReadOnlyList<AmbiguityConcern> Ambiguities { get; init; } = [];
}

/// <summary>
/// <see cref="IDocumentType"/> for the <c>ambiguity-assessment</c> document (Story
/// 39-3 AC4). Enforces <c>score</c> ∈ [0,1], a closed typed ambiguity set, and a
/// non-empty rationale — while treating a clear (low-score) assessment with an
/// empty ambiguity list as valid.
/// </summary>
public sealed class AmbiguityAssessmentDocumentType : IDocumentType
{
    /// <summary>Payload could not be deserialized (absent/non-numeric score, or wrong types).</summary>
    public const string MalformedPayload = "MALFORMED_PAYLOAD";

    /// <summary>The <c>score</c> is outside [0,1] — rejected (parity with the baseline fail-closed).</summary>
    public const string ScoreOutOfRange = "SCORE_OUT_OF_RANGE";

    /// <summary>Baseline fail-closed: a score with no rationale is not auditable/actionable.</summary>
    public const string MissingRationale = "MISSING_RATIONALE";

    /// <summary>The <c>confidence</c> is outside [0,1] — rejected, not clamped (D6; baseline clamped).</summary>
    public const string ConfidenceOutOfRange = "CONFIDENCE_OUT_OF_RANGE";

    /// <summary>An ambiguity <c>type</c> outside the closed set — strict (D5; baseline normalized).</summary>
    public const string UnknownAmbiguityType = "UNKNOWN_AMBIGUITY_TYPE";

    /// <summary>An ambiguity <c>severity</c> outside the closed set — strict (D5; baseline normalized).</summary>
    public const string UnknownSeverity = "UNKNOWN_SEVERITY";

    /// <summary>An ambiguity item with no description is an empty shell.</summary>
    public const string AmbiguityEmptyShell = "AMBIGUITY_EMPTY_SHELL";

    public string Key => DocumentTypeKey.AmbiguityAssessment.ToWire();
    public int SchemaVersion => 1;
    public Type PayloadClrType => typeof(AmbiguityAssessment);

    public DocumentValidationResult Validate(JsonElement payload) =>
        DocumentPayloadGuard.Run(payload, ValidateCore);

    private DocumentValidationResult ValidateCore(JsonElement payload)
    {
        AmbiguityAssessment? doc;
        try
        {
            doc = payload.Deserialize<AmbiguityAssessment>(DocumentJson.Options);
        }
        catch (JsonException)
        {
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload,
                "The payload could not be parsed as an ambiguity assessment (a missing or non-numeric " +
                "score fails here — the score is load-bearing)."));
        }

        if (doc is null)
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload deserialized to null."));

        var violations = new List<DocumentViolation>();

        if (doc.Score < 0m || doc.Score > 1m)
            violations.Add(new DocumentViolation(
                ScoreOutOfRange, $"score is {doc.Score} — the ambiguity score must be within [0, 1]."));

        if (string.IsNullOrWhiteSpace(doc.Rationale))
            violations.Add(new DocumentViolation(
                MissingRationale, "The assessment has no rationale — a score with no rationale is not auditable."));

        if (doc.Confidence < 0m || doc.Confidence > 1m)
            violations.Add(new DocumentViolation(
                ConfidenceOutOfRange, $"confidence is {doc.Confidence} — confidence must be within [0, 1]."));

        // A clear requirement (low score) with an empty ambiguities list is valid (AC4).
        var index = 0;
        foreach (var concern in doc.Ambiguities ?? [])
        {
            index++;
            var label = string.IsNullOrWhiteSpace(concern.Description) ? $"#{index}" : $"'{concern.Description}'";

            if (string.IsNullOrWhiteSpace(concern.Description))
                violations.Add(new DocumentViolation(
                    AmbiguityEmptyShell, $"Ambiguity {label} has no description — it is an empty shell."));

            if (!EnumWire<AmbiguityCategory>.TryParse(concern.Type ?? "", out _))
                violations.Add(new DocumentViolation(
                    UnknownAmbiguityType,
                    $"Ambiguity {label} has type '{concern.Type}', which is not one of vague, missing, " +
                    "contradictory, implicit, unspecified."));

            if (!EnumWire<AmbiguitySeverity>.TryParse(concern.Severity ?? "", out _))
                violations.Add(new DocumentViolation(
                    UnknownSeverity,
                    $"Ambiguity {label} has severity '{concern.Severity}', which is not one of low, medium, high."));
        }

        return violations.Count == 0
            ? DocumentValidationResult.Valid()
            : DocumentValidationResult.Invalid(violations.ToArray());
    }

    public string RenderContract() => Contract;

    public IReadOnlyList<DocumentExample> Examples => s_examples;

    // ── Contract + examples ──────────────────────────────────────────────────
    // The quoted tokens below are pinned by ContractBindingTests.Bindings for the
    // (product_owner, score-ambiguity) cell → AmbiguityParsing.ParseAssessment (8
    // tokens): "score", "confidence", "rationale", "ambiguities", "type",
    // "description", "severity", "recommendation".
    private const string Contract =
        """
        Return ONLY a JSON object of this shape:
        {
          "score": 0.72,
          "confidence": 0.8,
          "rationale": "why the requirement scored this way",
          "ambiguities": [
            {
              "type": "vague | missing | contradictory | implicit | unspecified",
              "description": "what is unclear",
              "severity": "low | medium | high",
              "recommendation": "how to resolve it"
            }
          ]
        }
        Rules: "score" and "confidence" must be within [0, 1]; "rationale" is required;
        each ambiguity "type" and "severity" must be from the closed sets above; the
        "ambiguities" list may be empty for a genuinely clear requirement.
        """;

    private static readonly IReadOnlyList<DocumentExample> s_examples = new[]
    {
        new DocumentExample(
            "valid-clear-requirement-empty-breakdown",
            true,
            """
            { "score": 0.05, "confidence": 0.9, "rationale": "Fully specified with clear ACs.", "ambiguities": [] }
            """),
        new DocumentExample(
            "invalid-out-of-range-and-unknown-type",
            false,
            """
            {
              "score": 1.5,
              "confidence": 0.8,
              "rationale": "Bad score and a bad label.",
              "ambiguities": [
                { "type": "unclear", "description": "vague wording", "severity": "high", "recommendation": "quantify it" }
              ]
            }
            """,
            new[] { ScoreOutOfRange, UnknownAmbiguityType }),
    };
}
