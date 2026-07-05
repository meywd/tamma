using System.Text.Json.Serialization;

namespace Tamma.Activities.Ambiguity.Models;

/// <summary>
/// Story 3.6 — the canonical ambiguity <b>types</b> the scorer classifies a requirement's
/// problems into (Story 3.6 AC2 — "identifies different types of ambiguity (vague, missing,
/// contradictory, implicit)"). Kept as a small closed set so a drifting LLM label
/// (<c>"unclear"</c>, <c>"Vague."</c>) is normalised onto a known bucket rather than leaking
/// an arbitrary string downstream. An unrecognised / empty label normalises to
/// <see cref="Unspecified"/> — the item is still kept (its description carries the signal),
/// it is just not force-fit into a specific bucket.
/// </summary>
public static class AmbiguityTypes
{
    public const string Vague = "vague";
    public const string Missing = "missing";
    public const string Contradictory = "contradictory";
    public const string Implicit = "implicit";
    public const string Unspecified = "unspecified";

    private static readonly IReadOnlySet<string> Canonical = new HashSet<string>(StringComparer.Ordinal)
    {
        Vague, Missing, Contradictory, Implicit,
    };

    /// <summary>
    /// Normalise a raw LLM type label onto the canonical set: trimmed + lower-cased, with a
    /// couple of common synonyms folded in. Anything unrecognised → <see cref="Unspecified"/>.
    /// Pure; exposed for unit testing.
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Unspecified;

        var t = raw.Trim().TrimEnd('.').ToLowerInvariant();
        if (Canonical.Contains(t)) return t;

        return t switch
        {
            "unclear" or "ambiguous" or "imprecise" => Vague,
            "incomplete" or "underspecified" or "absent" => Missing,
            "conflicting" or "contradiction" or "inconsistent" => Contradictory,
            "assumed" or "implied" or "unstated" => Implicit,
            _ => Unspecified,
        };
    }
}

/// <summary>
/// Story 3.6 — the canonical severity buckets for a single detected ambiguity. Normalised the
/// same way as <see cref="AmbiguityTypes"/> so a drifting label folds onto a known bucket;
/// an unrecognised / empty label defaults to <see cref="Medium"/> (a neutral middle, never
/// silently dropped).
/// </summary>
public static class AmbiguitySeverities
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";

    private static readonly IReadOnlySet<string> Canonical = new HashSet<string>(StringComparer.Ordinal)
    {
        Low, Medium, High,
    };

    /// <summary>Normalise a raw severity label; unrecognised → <see cref="Medium"/>. Pure.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Medium;

        var s = raw.Trim().TrimEnd('.').ToLowerInvariant();
        if (Canonical.Contains(s)) return s;

        return s switch
        {
            "critical" or "blocker" or "severe" => High,
            "minor" or "trivial" or "info" => Low,
            _ => Medium,
        };
    }
}

/// <summary>
/// Story 3.6 — one detected ambiguity within a requirement: its classified
/// <see cref="Type"/> (Story 3.6 AC2), a human-readable <see cref="Description"/> of what is
/// unclear, a <see cref="Severity"/>, and a specific <see cref="Recommendation"/> for
/// resolving it (Story 3.6 AC4 — "detailed ambiguity breakdown with specific
/// recommendations"). Parsed defensively by <see cref="AmbiguityParsing.ParseAssessment"/>;
/// items with no description are dropped as empty shells rather than admitted blank.
/// </summary>
public sealed class AmbiguityItem
{
    /// <summary>The ambiguity type — one of the <see cref="AmbiguityTypes"/> buckets.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = AmbiguityTypes.Unspecified;

    /// <summary>What is unclear / missing / contradictory / implicit about the requirement.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Severity — one of the <see cref="AmbiguitySeverities"/> buckets.</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = AmbiguitySeverities.Medium;

    /// <summary>A specific recommendation for resolving this ambiguity (AC4).</summary>
    [JsonPropertyName("recommendation")]
    public string Recommendation { get; set; } = string.Empty;
}

/// <summary>
/// Story 3.6 — the structured ambiguity assessment for a requirement: a quantitative
/// <see cref="Score"/> in [0,1] (Story 3.6 AC1 — higher = more ambiguous / underspecified),
/// a <see cref="Rationale"/> explaining the score, the scorer's <see cref="Confidence"/>, and
/// the itemised <see cref="Ambiguities"/> breakdown. Serialised into the workflow's
/// <c>assessmentJson</c> output and carried onto the <c>AMBIGUITY.SCORED</c> DCB event so the
/// score and its reasons are fully auditable and feed the Epic-32 learning loop.
///
/// <para>A high score above the caller's threshold routes the requirement to the sibling
/// <c>ClarifyingQuestionsWorkflow</c> (Story 3.5) before implementation proceeds (Story 3.6
/// AC6 — "ambiguity thresholds trigger appropriate workflows"). The parser fails closed on a
/// missing / out-of-range score or a missing rationale, so a fabricated score is never acted
/// on.</para>
/// </summary>
public sealed class AmbiguityAssessment
{
    /// <summary>Overall ambiguity score in [0,1]; higher = more ambiguous / underspecified.</summary>
    [JsonPropertyName("score")]
    public decimal Score { get; set; }

    /// <summary>Overview rationale explaining the score — load-bearing (fail-closed if empty).</summary>
    [JsonPropertyName("rationale")]
    public string Rationale { get; set; } = string.Empty;

    /// <summary>The scorer's confidence in the assessment, in [0,1].</summary>
    [JsonPropertyName("confidence")]
    public decimal Confidence { get; set; }

    /// <summary>The itemised ambiguity breakdown (may be empty for a genuinely clear requirement).</summary>
    [JsonPropertyName("ambiguities")]
    public List<AmbiguityItem> Ambiguities { get; set; } = new();
}
