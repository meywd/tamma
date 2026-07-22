using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;

namespace Tamma.Core.Documents.Types;

/// <summary>
/// The closed triage-priority vocabulary (Story 39-4 Design Decision D6 — the Story
/// 26-1 vocabulary <c>TriagePoDecisionHelper</c> clamps to). Read aliases
/// <c>critical</c>→<see cref="Urgent"/> and <c>medium</c>→<see cref="Normal"/> fold
/// the helper's documented synonyms; out-of-vocab values are violations
/// (<c>OUT_OF_VOCABULARY</c>), never silent clamps.
/// </summary>
public enum TriagePriority
{
    [Wire("urgent")] Urgent,
    [Wire("high")] High,
    [Wire("normal")] Normal,
    [Wire("low")] Low,
}

/// <summary>The closed triage issue-type vocabulary (D6 — Story 26-1).</summary>
public enum TriageIssueType
{
    [Wire("bug")] Bug,
    [Wire("feature")] Feature,
    [Wire("chore")] Chore,
    [Wire("question")] Question,
    [Wire("security")] Security,
    [Wire("docs")] Docs,
}

/// <summary>The closed triage-complexity vocabulary (D6 — Story 26-1).</summary>
public enum TriageComplexity
{
    [Wire("trivial")] Trivial,
    [Wire("simple")] Simple,
    [Wire("medium")] Medium,
    [Wire("complex")] Complex,
    [Wire("epic")] Epic,
}

/// <summary>The closed triage-automation vocabulary (D6 — Story 26-1).</summary>
public enum TriageAutomation
{
    [Wire("tamma-auto")] TammaAuto,
    [Wire("tamma-assist")] TammaAssist,
    [Wire("needs-human")] NeedsHuman,
}

/// <summary>Alias-aware, case-insensitive parsing for the triage vocabularies (D6).</summary>
public static class TriageVocabulary
{
    public static string ToWire(this TriagePriority v) => EnumWire<TriagePriority>.ToWire(v);
    public static string ToWire(this TriageIssueType v) => EnumWire<TriageIssueType>.ToWire(v);
    public static string ToWire(this TriageComplexity v) => EnumWire<TriageComplexity>.ToWire(v);
    public static string ToWire(this TriageAutomation v) => EnumWire<TriageAutomation>.ToWire(v);

    /// <summary>Parse a priority, folding the helper's <c>critical</c>/<c>medium</c> synonyms.</summary>
    public static bool TryParsePriority(string? raw, out TriagePriority value)
    {
        switch ((raw ?? "").Trim().ToLowerInvariant())
        {
            case "urgent": case "critical": value = TriagePriority.Urgent; return true;
            case "high": value = TriagePriority.High; return true;
            case "normal": case "medium": value = TriagePriority.Normal; return true;
            case "low": value = TriagePriority.Low; return true;
            default: value = default; return false;
        }
    }

    public static bool TryParseType(string? raw, out TriageIssueType value)
    {
        switch ((raw ?? "").Trim().ToLowerInvariant())
        {
            case "bug": value = TriageIssueType.Bug; return true;
            case "feature": value = TriageIssueType.Feature; return true;
            case "chore": value = TriageIssueType.Chore; return true;
            case "question": value = TriageIssueType.Question; return true;
            case "security": value = TriageIssueType.Security; return true;
            case "docs": value = TriageIssueType.Docs; return true;
            default: value = default; return false;
        }
    }

    public static bool TryParseComplexity(string? raw, out TriageComplexity value)
    {
        switch ((raw ?? "").Trim().ToLowerInvariant())
        {
            case "trivial": value = TriageComplexity.Trivial; return true;
            case "simple": value = TriageComplexity.Simple; return true;
            case "medium": value = TriageComplexity.Medium; return true;
            case "complex": value = TriageComplexity.Complex; return true;
            case "epic": value = TriageComplexity.Epic; return true;
            default: value = default; return false;
        }
    }

    public static bool TryParseAutomation(string? raw, out TriageAutomation value)
    {
        switch ((raw ?? "").Trim().ToLowerInvariant())
        {
            case "tamma-auto": value = TriageAutomation.TammaAuto; return true;
            case "tamma-assist": value = TriageAutomation.TammaAssist; return true;
            case "needs-human": value = TriageAutomation.NeedsHuman; return true;
            default: value = default; return false;
        }
    }
}

/// <summary>
/// A product-owner triage decision (Story 39-4 AC6). Every classification field is a
/// closed enum from the Story 26-1 vocabulary; <see cref="Reasoning"/> is required
/// non-empty. The helper's honest-failure markers (<c>llm-failed</c>/<c>unparsed</c>/
/// <c>skipped</c>) do NOT enter the payload — they are lifecycle outcomes
/// (ValidationExhausted territory), per the story's technical note. The classification
/// fields are stored as their canonical wire strings so the serialized shape matches
/// the helper's output contract and <c>TriagePoDecisionHelper.ParseDecision</c> round-trips
/// clean (StatusOk, no clamp notes); validation over the raw payload enforces the closed
/// vocabulary via <see cref="TriageVocabulary"/>.
/// </summary>
public sealed record TriageDecision
{
    [JsonPropertyName("priority")] public string Priority { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("complexity")] public string Complexity { get; init; } = "";
    [JsonPropertyName("automation")] public string Automation { get; init; } = "";
    [JsonPropertyName("reasoning")] public string Reasoning { get; init; } = "";
    [JsonPropertyName("labels")] public IReadOnlyList<string>? Labels { get; init; }
    [JsonPropertyName("comment")] public string? Comment { get; init; }
}

/// <summary>
/// <see cref="IDocumentType"/> for the <c>triage-decision</c> document (Story 39-4
/// AC6). Every classification field is validated against its closed enum vocabulary;
/// an out-of-vocab value is a violation (<c>OUT_OF_VOCABULARY</c> naming field +
/// offending value), never a silent clamp — the clamp-and-flag behaviour moves to the
/// visible repair/review layer. <c>reasoning</c> is required non-empty.
/// </summary>
public sealed class TriageDecisionDocumentType : IDocumentType
{
    /// <summary>The payload is not a JSON object — prose / an array / a scalar cannot be a triage decision.</summary>
    public const string MalformedPayload = "MALFORMED_PAYLOAD";

    /// <summary>A classification field is missing or outside its closed vocabulary (names field + value).</summary>
    public const string OutOfVocabulary = "OUT_OF_VOCABULARY";

    /// <summary>The <c>reasoning</c> is missing/empty — a decision must justify itself.</summary>
    public const string ReasoningRequired = "REASONING_REQUIRED";

    public string Key => DocumentTypeKey.TriageDecision.ToWire();
    public int SchemaVersion => 1;
    public Type PayloadClrType => typeof(TriageDecision);

    public DocumentValidationResult Validate(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload is not a JSON object — a triage decision must be an object."));

        var violations = new List<DocumentViolation>();

        CheckField(payload, "priority", "urgent, high, normal, low",
            raw => TriageVocabulary.TryParsePriority(raw, out _), violations);
        CheckField(payload, "type", "bug, feature, chore, question, security, docs",
            raw => TriageVocabulary.TryParseType(raw, out _), violations);
        CheckField(payload, "complexity", "trivial, simple, medium, complex, epic",
            raw => TriageVocabulary.TryParseComplexity(raw, out _), violations);
        CheckField(payload, "automation", "tamma-auto, tamma-assist, needs-human",
            raw => TriageVocabulary.TryParseAutomation(raw, out _), violations);

        var reasoning = ReadString(payload, "reasoning");
        if (string.IsNullOrWhiteSpace(reasoning))
            violations.Add(new DocumentViolation(
                ReasoningRequired, "The triage decision has no reasoning — a classification must justify itself."));

        return violations.Count == 0
            ? DocumentValidationResult.Valid()
            : DocumentValidationResult.Invalid(violations.ToArray());
    }

    private static void CheckField(JsonElement root, string field, string allowed, Func<string?, bool> tryParse, List<DocumentViolation> violations)
    {
        if (!root.TryGetProperty(field, out var el))
        {
            violations.Add(new DocumentViolation(
                OutOfVocabulary, $"'{field}' is required and must be one of: {allowed}."));
            return;
        }

        if (el.ValueKind != JsonValueKind.String)
        {
            violations.Add(new DocumentViolation(
                OutOfVocabulary, $"'{field}' must be a string from: {allowed}."));
            return;
        }

        var raw = el.GetString();
        if (!tryParse(raw))
            violations.Add(new DocumentViolation(
                OutOfVocabulary, $"'{field}' has value '{raw}', which is not one of: {allowed}."));
    }

    private static string? ReadString(JsonElement root, string field)
        => root.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public string RenderContract() => Contract;

    public IReadOnlyList<DocumentExample> Examples => s_examples;

    // ── Contract + examples ──────────────────────────────────────────────────
    // (product_owner, triage-intake) is IntentionallyUnbound in ContractBindingTests
    // (TriagePoDecisionHelper.ParseDecision is fail-safe/lenient), so this renderer has
    // no CI token conflict. NOTE: the shipped triage-intake.md instructs a DIFFERENT
    // vocabulary (P0..P3 / severity / ownerRole) that the helper already clamps away;
    // this type defines the 26-1 vocabulary per AC6 and the prompt divergence is
    // 39-15/39-16 migration scope (recorded in completion notes; no prompt changed here).
    private const string Contract =
        """
        Return ONLY a JSON object of this shape:
        {
          "priority": "urgent | high | normal | low",
          "type": "bug | feature | chore | question | security | docs",
          "complexity": "trivial | simple | medium | complex | epic",
          "automation": "tamma-auto | tamma-assist | needs-human",
          "reasoning": "why this classification",
          "labels": ["optional", "labels"],
          "comment": "optional human-facing note"
        }
        Rules: "priority", "type", "complexity", and "automation" must each be one of the
        closed sets above; "reasoning" is required and non-empty.
        """;

    private static readonly IReadOnlyList<DocumentExample> s_examples = new[]
    {
        new DocumentExample(
            "valid-classified-bug",
            true,
            """
            {
              "priority": "high",
              "type": "bug",
              "complexity": "simple",
              "automation": "tamma-auto",
              "reasoning": "Reproducible null-ref with a clear fix scope; safe to automate.",
              "labels": ["bug"]
            }
            """),
        new DocumentExample(
            "invalid-out-of-vocab-priority",
            false,
            """
            {
              "priority": "P0",
              "type": "feature",
              "complexity": "medium",
              "automation": "needs-human",
              "reasoning": "Uses the un-migrated P-vocabulary."
            }
            """,
            new[] { OutOfVocabulary }),
    };
}
