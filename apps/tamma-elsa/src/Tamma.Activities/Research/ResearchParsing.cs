using System.Text.Json;
using Tamma.Activities.Research.Models;

namespace Tamma.Activities.Research;

/// <summary>
/// Story 3.4 — pure, context-free parser that recovers the structured
/// <see cref="ResearchReport"/> from a mediated <c>llm-call</c> synthesis response.
/// Kept side-effect-free (no Elsa context) so the fail-closed behaviour is unit-testable
/// without a live LLM. Mirrors the JSON-slice approach in <c>ClarifyParsing</c> /
/// <c>AssessmentWorkflow</c> / <c>ContextGatheringWorkflow</c>.
///
/// <para>The parser is <b>fail-closed</b>: an empty response, a response with no JSON
/// object, a report missing its load-bearing <c>summary</c>, or a report with no usable
/// findings all yield <c>null</c>. The workflow routes a <c>null</c> parse to its
/// <c>RESEARCH.FAILED</c> error terminal — it NEVER fabricates a summary, a finding, or a
/// confidence score the downstream would act on.</para>
/// </summary>
public static class ResearchParsing
{
    /// <summary>
    /// Extract the synthesized research report from an <c>llm-call</c> text response.
    /// Expects a JSON object of the shape
    /// <c>{"summary":"...","findings":[{"title":"...","summary":"...","relevance":0.9,
    /// "confidence":0.8,"citations":["..."]}],"overallConfidence":0.85}</c>.
    ///
    /// <para>Findings are ranked most-relevant-first (relevance desc, then confidence
    /// desc) — the AC "findings are synthesized and ranked by relevance and confidence".
    /// <c>overallConfidence</c> is read from the object when present, otherwise computed
    /// as the mean of the findings' confidence (never invented from nothing).</para>
    ///
    /// <para>Returns <c>null</c> (fail-closed) when the text is empty, carries no JSON
    /// object, has no non-empty <c>summary</c>, or contains no usable findings.</para>
    /// </summary>
    /// <param name="llmText">The raw LLM synthesis response.</param>
    /// <param name="topic">Fallback topic when the response omits its own <c>topic</c>.</param>
    public static ResearchReport? ParseReport(string? llmText, string? topic = null)
    {
        if (string.IsNullOrWhiteSpace(llmText))
            return null;

        var objStart = llmText.IndexOf('{');
        var objEnd = llmText.LastIndexOf('}');
        if (objStart < 0 || objEnd <= objStart)
            return null;

        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(llmText[objStart..(objEnd + 1)]);

            var summary = element.TryGetProperty("summary", out var sv) && sv.ValueKind == JsonValueKind.String
                ? sv.GetString() ?? string.Empty
                : string.Empty;

            // Fail-closed: a research report with no overview summary is not actionable.
            if (string.IsNullOrWhiteSpace(summary))
                return null;

            var findings = ParseFindings(element);

            // Fail-closed: no findings → nothing was actually researched.
            if (findings.Count == 0)
                return null;

            // Rank by relevance desc, then confidence desc (AC: ranked by relevance and confidence).
            findings = findings
                .OrderByDescending(f => f.Relevance)
                .ThenByDescending(f => f.Confidence)
                .ToList();

            var overall = element.TryGetProperty("overallConfidence", out var oc) && oc.ValueKind == JsonValueKind.Number
                ? (decimal)oc.GetDouble()
                : AverageConfidence(findings);

            return new ResearchReport
            {
                Topic = element.TryGetProperty("topic", out var tv) && tv.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tv.GetString())
                    ? tv.GetString()!
                    : topic ?? string.Empty,
                Summary = summary,
                Findings = findings,
                OverallConfidence = overall,
            };
        }
        catch
        {
            // Malformed JSON → fail closed.
            return null;
        }
    }

    /// <summary>
    /// Recover the finding list from the report object. Each finding must carry at
    /// least a title or a summary — empty shells are dropped (item-level fail-closed).
    /// </summary>
    private static List<ResearchFinding> ParseFindings(JsonElement element)
    {
        if (!element.TryGetProperty("findings", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<ResearchFinding>();

        var findings = new List<ResearchFinding>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var title = ReadString(item, "title");
            var summary = ReadString(item, "summary");

            // A finding with neither a title nor a summary is an empty shell — skip it
            // rather than admit a fabricated blank finding.
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(summary))
                continue;

            findings.Add(new ResearchFinding
            {
                Title = title,
                Summary = summary,
                Relevance = ReadNumber(item, "relevance"),
                Confidence = ReadNumber(item, "confidence"),
                Citations = ExtractStringList(item, "citations"),
            });
        }

        return findings;
    }

    private static string ReadString(JsonElement element, string key)
        => element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static decimal ReadNumber(JsonElement element, string key)
        => element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? (decimal)v.GetDouble()
            : 0m;

    private static decimal AverageConfidence(IReadOnlyList<ResearchFinding> findings)
        => findings.Count == 0 ? 0m : Math.Round(findings.Sum(f => f.Confidence) / findings.Count, 4);

    private static List<string> ExtractStringList(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<string>();

        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .ToList();
    }
}
