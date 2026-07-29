using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamma.Core.Documents.Types;

/// <summary>
/// One synthesized research finding — the typed analogue of the legacy
/// <c>Tamma.Activities.Research.Models.ResearchFinding</c> (Design Decision D2:
/// wire shape verbatim). An optional additive <see cref="Rank"/> makes the
/// ranking explicit; when absent, list order is the ranking (baseline behaviour).
/// </summary>
public sealed record Finding
{
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("summary")] public string Summary { get; init; } = "";
    [JsonPropertyName("relevance")] public decimal Relevance { get; init; }
    [JsonPropertyName("confidence")] public decimal Confidence { get; init; }
    [JsonPropertyName("citations")] public IReadOnlyList<string> Citations { get; init; } = [];

    /// <summary>Optional explicit rank. When present on ANY finding, it must be present on ALL, with no duplicates.</summary>
    [JsonPropertyName("rank")] public int? Rank { get; init; }
}

/// <summary>
/// A synthesized research report: an overview <see cref="Summary"/> plus the
/// ranked, scored <see cref="Items"/>. The record member is <c>Items</c> (C#
/// forbids a member named like its enclosing type) carrying
/// <c>[JsonPropertyName("findings")]</c> so the wire shape matches the legacy
/// <c>ResearchReport</c>.
/// </summary>
public sealed record Findings
{
    [JsonPropertyName("topic")] public string Topic { get; init; } = "";
    [JsonPropertyName("summary")] public string Summary { get; init; } = "";
    [JsonPropertyName("findings")] public IReadOnlyList<Finding> Items { get; init; } = [];
    [JsonPropertyName("overallConfidence")] public decimal OverallConfidence { get; init; }
}

/// <summary>
/// <see cref="IDocumentType"/> for the <c>findings</c> document (Story 39-3 AC3).
/// Enforces cited evidence per finding, <c>relevance</c>/<c>confidence</c> ∈ [0,1]
/// rejected (not clamped), and a ranking rule (explicit ranks or ordered with no
/// duplicates). Deliberate tightenings over the baseline are enumerated in the
/// completion notes (AC6).
/// </summary>
public sealed class FindingsDocumentType : IDocumentType
{
    /// <summary>Payload could not be deserialized into the typed shape.</summary>
    public const string MalformedPayload = "MALFORMED_PAYLOAD";

    /// <summary>Baseline fail-closed: a report with no overview summary is not actionable.</summary>
    public const string MissingSummary = "MISSING_SUMMARY";

    /// <summary>
    /// Inherited baseline choice: <c>ResearchParsing</c> fails closed on an empty findings list,
    /// so an empty list is a violation, NOT a valid "nothing found" (documented per AC3).
    /// </summary>
    public const string EmptyFindings = "EMPTY_FINDINGS";

    /// <summary>A finding with neither a title nor a summary is an empty shell.</summary>
    public const string FindingEmptyShell = "FINDING_EMPTY_SHELL";

    /// <summary>A finding with no citations — AC3's evidence rule (tightening; baseline never required citations).</summary>
    public const string MissingEvidence = "MISSING_EVIDENCE";

    /// <summary>A finding <c>relevance</c> outside [0,1] — rejected, not clamped (D6).</summary>
    public const string RelevanceOutOfRange = "RELEVANCE_OUT_OF_RANGE";

    /// <summary>A <c>confidence</c> (per-finding or <c>overallConfidence</c>) outside [0,1] — rejected, not clamped (D6).</summary>
    public const string ConfidenceOutOfRange = "CONFIDENCE_OUT_OF_RANGE";

    /// <summary>Two findings carry the same explicit <c>rank</c>.</summary>
    public const string DuplicateRank = "DUPLICATE_RANK";

    /// <summary>Some findings carry an explicit <c>rank</c> and some do not — ranks must be all-or-nothing.</summary>
    public const string PartialRanks = "PARTIAL_RANKS";

    public string Key => DocumentTypeKey.Findings.ToWire();
    public int SchemaVersion => 1;
    public Type PayloadClrType => typeof(Findings);

    public DocumentValidationResult Validate(JsonElement payload) =>
        DocumentPayloadGuard.Run(payload, ValidateCore);

    private DocumentValidationResult ValidateCore(JsonElement payload)
    {
        Findings? doc;
        try
        {
            doc = payload.Deserialize<Findings>(DocumentJson.Options);
        }
        catch (JsonException)
        {
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload could not be parsed as a findings document."));
        }

        if (doc is null)
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload deserialized to null."));

        var violations = new List<DocumentViolation>();

        if (string.IsNullOrWhiteSpace(doc.Summary))
            violations.Add(new DocumentViolation(
                MissingSummary, "The report has no summary — the overview of the research is required."));

        if (doc.OverallConfidence < 0m || doc.OverallConfidence > 1m)
            violations.Add(new DocumentViolation(
                ConfidenceOutOfRange,
                $"overallConfidence is {doc.OverallConfidence} — confidence must be within [0, 1]."));

        var findings = doc.Items ?? [];
        if (findings.Count == 0)
        {
            violations.Add(new DocumentViolation(
                EmptyFindings,
                "The report has no findings — the baseline research parser fails closed on an empty " +
                "findings list, so an empty list is a violation, not a valid 'nothing found'."));
        }

        var index = 0;
        foreach (var finding in findings)
        {
            index++;
            var label = string.IsNullOrWhiteSpace(finding.Title) ? $"#{index}" : $"'{finding.Title}'";

            if (string.IsNullOrWhiteSpace(finding.Title) && string.IsNullOrWhiteSpace(finding.Summary))
                violations.Add(new DocumentViolation(
                    FindingEmptyShell,
                    $"Finding {label} has neither a title nor a summary — it is an empty shell."));

            if ((finding.Citations ?? []).Count == 0)
                violations.Add(new DocumentViolation(
                    MissingEvidence,
                    $"Finding {label} cites no evidence — every finding must reference at least one source."));

            if (finding.Relevance < 0m || finding.Relevance > 1m)
                violations.Add(new DocumentViolation(
                    RelevanceOutOfRange,
                    $"Finding {label} has relevance {finding.Relevance} — relevance must be within [0, 1]."));

            if (finding.Confidence < 0m || finding.Confidence > 1m)
                violations.Add(new DocumentViolation(
                    ConfidenceOutOfRange,
                    $"Finding {label} has confidence {finding.Confidence} — confidence must be within [0, 1]."));
        }

        AddRankViolations(findings, violations);

        return violations.Count == 0
            ? DocumentValidationResult.Valid()
            : DocumentValidationResult.Invalid(violations.ToArray());
    }

    private static void AddRankViolations(IReadOnlyList<Finding> findings, List<DocumentViolation> violations)
    {
        var ranked = findings.Where(f => f.Rank.HasValue).ToList();
        if (ranked.Count == 0)
            return; // no explicit ranks — list order is the ranking (baseline parity).

        if (ranked.Count != findings.Count)
        {
            violations.Add(new DocumentViolation(
                PartialRanks,
                "Some findings carry an explicit rank and some do not — either every finding is ranked " +
                "or none are (list order is the ranking)."));
            return;
        }

        var seen = new HashSet<int>();
        var reported = false;
        foreach (var value in ranked.Select(f => f.Rank!.Value))
        {
            if (!seen.Add(value) && !reported)
            {
                violations.Add(new DocumentViolation(
                    DuplicateRank, $"Two findings share rank {value} — explicit ranks must be unique."));
                reported = true;
            }
        }
    }

    public string RenderContract() => Contract;

    public IReadOnlyList<DocumentExample> Examples => s_examples;

    // ── Contract + examples ──────────────────────────────────────────────────
    // The quoted tokens below are pinned by ContractBindingTests.Bindings for the
    // (product_owner, research) cell → ResearchParsing.ParseReport (7 tokens):
    // "summary", "findings", "title", "relevance", "confidence", "citations",
    // "overallConfidence".
    private const string Contract =
        """
        Return ONLY a JSON object of this shape:
        {
          "topic": "the question researched",
          "summary": "overview of the synthesized research",
          "findings": [
            {
              "title": "short finding headline",
              "summary": "what was learned and why it matters",
              "relevance": 0.9,
              "confidence": 0.8,
              "citations": ["src/File.cs", "https://example"]
            }
          ],
          "overallConfidence": 0.85
        }
        Rules: every finding must cite at least one source in "citations"; "relevance" and
        "confidence" (per finding and "overallConfidence") must be within [0, 1]; findings are
        ranked by list order unless every finding carries a unique "rank".
        """;

    private static readonly IReadOnlyList<DocumentExample> s_examples = new[]
    {
        new DocumentExample(
            "valid-two-findings",
            true,
            """
            {
              "topic": "per-tenant rate limiting",
              "summary": "No limiter exists; a token-bucket keyed by tenant id is the lowest-risk introduction.",
              "findings": [
                { "title": "No existing limiter", "summary": "The API pipeline has no rate-limiting middleware.", "relevance": 0.95, "confidence": 0.9, "citations": ["src/Program.cs"] },
                { "title": "Tenant id on context", "summary": "Every request already resolves a tenant id.", "relevance": 0.8, "confidence": 0.85, "citations": ["src/TenantContext.cs"] }
              ],
              "overallConfidence": 0.88
            }
            """),
        new DocumentExample(
            "invalid-no-evidence-and-out-of-range",
            false,
            """
            {
              "topic": "caching",
              "summary": "Two problematic findings.",
              "findings": [
                { "title": "Uncited", "summary": "No citation here.", "relevance": 0.5, "confidence": 0.5, "citations": [] },
                { "title": "Out of range", "summary": "Bad relevance.", "relevance": 1.5, "confidence": 0.5, "citations": ["a.cs"] }
              ],
              "overallConfidence": 0.7
            }
            """,
            new[] { MissingEvidence, RelevanceOutOfRange }),
    };
}
