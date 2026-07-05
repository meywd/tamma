using System.Text.Json.Serialization;

namespace Tamma.Activities.Research.Models;

/// <summary>
/// Story 3.4 — one synthesized, relevance-and-confidence-scored research finding
/// recovered from the mediated <c>llm-call</c> synthesis response. Parsed defensively
/// by <see cref="ResearchParsing.ParseReport"/>; the workflow fails closed (routes to
/// its <c>RESEARCH.FAILED</c> error terminal) when no structured findings can be
/// recovered rather than emitting a fabricated finding.
/// </summary>
public sealed class ResearchFinding
{
    /// <summary>Short title / headline for the finding.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>The finding body — what was learned and why it matters.</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>Relevance score (0..1) — how directly the finding bears on the topic.</summary>
    [JsonPropertyName("relevance")]
    public decimal Relevance { get; set; }

    /// <summary>Confidence score (0..1) — how well-supported / cross-referenced the finding is.</summary>
    [JsonPropertyName("confidence")]
    public decimal Confidence { get; set; }

    /// <summary>Citations (file paths, URLs, doc refs) that back the finding for traceability.</summary>
    [JsonPropertyName("citations")]
    public List<string> Citations { get; set; } = new();
}

/// <summary>
/// Story 3.4 — the synthesized research report for an issue / topic: an overview
/// summary plus the ranked, scored <see cref="ResearchFinding"/> set. Serialised into
/// the workflow's <c>reportJson</c> output variable and carried onto the
/// <c>RESEARCH.COMPLETED</c> DCB event so the research is fully auditable and linked
/// back to the originating issue (Story 3.4 AC "Research results are stored and linked
/// to original issues for traceability").
/// </summary>
public sealed class ResearchReport
{
    /// <summary>The topic / question the research investigated.</summary>
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = string.Empty;

    /// <summary>Overview summary of the synthesized research.</summary>
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    /// <summary>Findings, ranked most-relevant-first (relevance desc, then confidence desc).</summary>
    [JsonPropertyName("findings")]
    public List<ResearchFinding> Findings { get; set; } = new();

    /// <summary>Overall confidence (0..1) across the findings.</summary>
    [JsonPropertyName("overallConfidence")]
    public decimal OverallConfidence { get; set; }
}
