using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Core;

namespace Tamma.Core.Documents.Types;

/// <summary>
/// One ranked root-cause hypothesis (Story 39-4 AC7). Confidence ∈ [0,1]; rank 1 is
/// the highest-confidence hypothesis. A non-empty <see cref="SuggestedFix"/> must name
/// the <see cref="AffectedFiles"/> it touches.
/// </summary>
public sealed record DiagnosisHypothesis
{
    [JsonPropertyName("rank")] public int Rank { get; init; }
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("confidence")] public decimal Confidence { get; init; }
    [JsonPropertyName("suggestedFix")] public string SuggestedFix { get; init; } = "";
    [JsonPropertyName("affectedFiles")] public IReadOnlyList<string> AffectedFiles { get; init; } = [];
}

/// <summary>
/// A diagnosis: an <see cref="AnalysisSummary"/> plus ranked <see cref="Hypotheses"/>.
/// Canonical wire is camelCase (39-2 D8); the legacy snake_case shape
/// (<c>analysis_summary</c>/<c>suggested_fix</c>/<c>affected_files</c>) the old
/// <c>AIDiagnosisActivity.ParseDiagnosisResponse</c> reads lives ONLY in the paired
/// <see cref="FromLegacyJson"/>/<see cref="ToLegacyJson"/> bridge (Design Decision D4).
/// </summary>
public sealed record Diagnosis
{
    [JsonPropertyName("analysisSummary")] public string AnalysisSummary { get; init; } = "";
    [JsonPropertyName("hypotheses")] public IReadOnlyList<DiagnosisHypothesis> Hypotheses { get; init; } = [];

    /// <summary>
    /// Read the legacy snake_case diagnosis wire (<c>analysis_summary</c>,
    /// <c>hypotheses[].rank/description/confidence/suggested_fix/affected_files</c>)
    /// into the typed shape (D4). A camelCase re-serialization would "parse" into an
    /// EMPTY (gate-failing) <c>DiagnosisResult</c>, so this reader is explicit.
    /// </summary>
    /// <exception cref="TammaError">
    /// Code <c>DOCUMENT.DIAGNOSIS.LEGACY_UNPARSEABLE</c> on unparseable / non-object
    /// input — never fabricated hypotheses.
    /// </exception>
    public static Diagnosis FromLegacyJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw LegacyUnparseable(json);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw LegacyUnparseable(json);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw LegacyUnparseable(json);

            var summary = root.TryGetProperty("analysis_summary", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? ""
                : "";

            var hypotheses = new List<DiagnosisHypothesis>();
            if (root.TryGetProperty("hypotheses", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in arr.EnumerateArray())
                {
                    if (h.ValueKind != JsonValueKind.Object)
                        continue;
                    hypotheses.Add(new DiagnosisHypothesis
                    {
                        Rank = h.TryGetProperty("rank", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0,
                        Description = h.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "",
                        Confidence = h.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? (decimal)c.GetDouble() : 0m,
                        SuggestedFix = h.TryGetProperty("suggested_fix", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : "",
                        AffectedFiles = h.TryGetProperty("affected_files", out var af) && af.ValueKind == JsonValueKind.Array
                            ? af.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "").Where(x => x.Length > 0).ToList()
                            : new List<string>(),
                    });
                }
            }

            return new Diagnosis { AnalysisSummary = summary, Hypotheses = hypotheses };
        }
    }

    /// <summary>
    /// Serialize back to the legacy snake_case wire the old
    /// <c>ParseDiagnosisResponse</c> reads (<c>analysis_summary</c>/<c>suggested_fix</c>/
    /// <c>affected_files</c>) — the transition-window writer (D4). Canonical camelCase
    /// serialization stays via <see cref="DocumentJson.Options"/>.
    /// </summary>
    public string ToLegacyJson()
    {
        var payload = new Dictionary<string, object?>
        {
            ["analysis_summary"] = AnalysisSummary,
            ["hypotheses"] = (Hypotheses ?? []).Select(h => new Dictionary<string, object?>
            {
                ["rank"] = h.Rank,
                ["description"] = h.Description,
                ["confidence"] = h.Confidence,
                ["suggested_fix"] = h.SuggestedFix,
                ["affected_files"] = h.AffectedFiles ?? [],
            }).ToList(),
        };
        return JsonSerializer.Serialize(payload);
    }

    private static TammaError LegacyUnparseable(string? json) => new(
        "DOCUMENT.DIAGNOSIS.LEGACY_UNPARSEABLE",
        "The legacy diagnosis JSON could not be parsed — a parse failure is a validation failure, " +
        "never fabricated hypotheses.",
        new Dictionary<string, object?> { ["json"] = json },
        retryable: false,
        severity: TammaErrorSeverity.High);
}

/// <summary>
/// <see cref="IDocumentType"/> for the <c>diagnosis</c> document (Story 39-4 AC7).
/// Enforces: each confidence ∈ [0,1] (rejected, not clamped); ranks unique; rank order
/// consistent with non-increasing confidence; a hypothesis with a suggested fix must
/// name ≥1 affected file.
/// </summary>
public sealed class DiagnosisDocumentType : IDocumentType
{
    /// <summary>Payload could not be deserialized into the typed shape.</summary>
    public const string MalformedPayload = "MALFORMED_PAYLOAD";

    /// <summary>A hypothesis confidence outside [0,1] — rejected, not clamped.</summary>
    public const string ConfidenceOutOfRange = "CONFIDENCE_OUT_OF_RANGE";

    /// <summary>Two hypotheses share a rank.</summary>
    public const string DuplicateRank = "DUPLICATE_RANK";

    /// <summary>Rank order contradicts confidence order (a lower rank has lower confidence).</summary>
    public const string RankConfidenceMismatch = "RANK_CONFIDENCE_MISMATCH";

    /// <summary>A hypothesis has a suggested fix but names no affected files (AC7).</summary>
    public const string FixMissingAffectedFiles = "FIX_MISSING_AFFECTED_FILES";

    public string Key => DocumentTypeKey.Diagnosis.ToWire();
    public int SchemaVersion => 1;
    public Type PayloadClrType => typeof(Diagnosis);

    public DocumentValidationResult Validate(JsonElement payload) =>
        DocumentPayloadGuard.Run(payload, ValidateCore);

    private DocumentValidationResult ValidateCore(JsonElement payload)
    {
        Diagnosis? doc;
        try
        {
            doc = payload.Deserialize<Diagnosis>(DocumentJson.Options);
        }
        catch (JsonException)
        {
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload could not be parsed as a diagnosis document."));
        }

        if (doc is null)
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload deserialized to null."));

        var violations = new List<DocumentViolation>();
        var hypotheses = doc.Hypotheses ?? [];

        var index = 0;
        foreach (var h in hypotheses)
        {
            index++;
            var label = string.IsNullOrWhiteSpace(h.Description) ? $"#{index}" : $"'{h.Description}'";

            if (h.Confidence < 0m || h.Confidence > 1m)
                violations.Add(new DocumentViolation(
                    ConfidenceOutOfRange,
                    $"Hypothesis {label} has confidence {h.Confidence} — confidence must be within [0, 1]."));

            if (!string.IsNullOrWhiteSpace(h.SuggestedFix) && (h.AffectedFiles ?? []).All(string.IsNullOrWhiteSpace))
                violations.Add(new DocumentViolation(
                    FixMissingAffectedFiles,
                    $"Hypothesis {label} proposes a fix but names no affected files — a fix must say what it touches."));
        }

        var ranks = hypotheses.Select(h => h.Rank).ToList();
        var duplicateRanks = ranks.GroupBy(r => r).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicateRanks.Count > 0)
            violations.Add(new DocumentViolation(
                DuplicateRank,
                $"Hypotheses share rank(s) {string.Join(", ", duplicateRanks.OrderBy(r => r))} — ranks must be unique."));
        else
        {
            // Only check ordering when ranks are unambiguous. Rank ascending ⇒ confidence non-increasing.
            var ordered = hypotheses.OrderBy(h => h.Rank).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Confidence > ordered[i - 1].Confidence)
                {
                    violations.Add(new DocumentViolation(
                        RankConfidenceMismatch,
                        $"Hypothesis ranked {ordered[i].Rank} has higher confidence ({ordered[i].Confidence}) than " +
                        $"the one ranked {ordered[i - 1].Rank} ({ordered[i - 1].Confidence}) — rank order must follow confidence."));
                    break;
                }
            }
        }

        return violations.Count == 0
            ? DocumentValidationResult.Valid()
            : DocumentValidationResult.Invalid(violations.ToArray());
    }

    public string RenderContract() => Contract;

    public IReadOnlyList<DocumentExample> Examples => s_examples;

    // ── Contract + examples ──────────────────────────────────────────────────
    // The debugging / blocker-diagnosis producers are not ContractBindingTests-bound
    // (their callers read the DiagnosisResult, not a pinned reply shape). The canonical
    // wire is camelCase; the snake_case tokens live only in the legacy bridge above.
    private const string Contract =
        """
        Return ONLY a JSON object of this shape:
        {
          "analysisSummary": "brief summary of the analysis",
          "hypotheses": [
            {
              "rank": 1,
              "description": "root cause description",
              "confidence": 0.85,
              "suggestedFix": "how to fix it",
              "affectedFiles": ["src/Foo.cs"]
            }
          ]
        }
        Rules: each "confidence" must be within [0, 1]; "rank" values must be unique and
        ordered by decreasing confidence (rank 1 = highest); a non-empty "suggestedFix"
        must name at least one file in "affectedFiles".
        """;

    private static readonly IReadOnlyList<DocumentExample> s_examples = new[]
    {
        new DocumentExample(
            "valid-two-ranked-hypotheses",
            true,
            """
            {
              "analysisSummary": "Null ref surfaces on cache miss.",
              "hypotheses": [
                { "rank": 1, "description": "Resolver returns null on cache miss", "confidence": 0.85, "suggestedFix": "Guard the cache miss", "affectedFiles": ["src/Resolver.cs"] },
                { "rank": 2, "description": "Race on cache warm-up", "confidence": 0.4, "suggestedFix": "", "affectedFiles": [] }
              ]
            }
            """),
        new DocumentExample(
            "invalid-out-of-range-and-fix-without-files",
            false,
            """
            {
              "analysisSummary": "Two broken hypotheses.",
              "hypotheses": [
                { "rank": 1, "description": "Bad confidence", "confidence": 1.4, "suggestedFix": "", "affectedFiles": [] },
                { "rank": 2, "description": "Fix names no files", "confidence": 0.3, "suggestedFix": "patch it", "affectedFiles": [] }
              ]
            }
            """,
            new[] { ConfidenceOutOfRange, FixMissingAffectedFiles }),
    };
}
