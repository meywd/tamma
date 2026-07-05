using System.Text.Json;
using Tamma.Activities.Design.Models;

namespace Tamma.Activities.Design;

/// <summary>
/// Story 3.7 — pure, context-free parser that recovers a <see cref="DesignProposal"/> from
/// a mediated <c>llm-call</c> text response. Kept side-effect-free (no Elsa context) so the
/// fail-closed behaviour is unit-testable without a live LLM. Mirrors the JSON-slice
/// approach in <c>ClarifyParsing</c> / <c>AssessmentWorkflow</c>.
///
/// <para><b>Fail-closed:</b> unparseable / empty output — or output missing the
/// load-bearing <c>summary</c> field — yields <c>null</c>, which the workflow routes to its
/// <c>DESIGN.PROPOSAL.FAILED</c> error terminal. It NEVER fabricates a design a reviewer
/// would then approve.</para>
/// </summary>
public static class DesignParsing
{
    /// <summary>
    /// Extract the design proposal from an <c>llm-call</c> text response. Expects a JSON
    /// object <c>{"summary":"...","alternatives":[{"name":"...","tradeoffs":"..."}],
    /// "recommendation":"...","constraintEvaluation":"..."}</c>. Returns <c>null</c> when the
    /// object or the required <c>summary</c> field is missing / empty (fail-closed) so the
    /// workflow routes to its error terminal rather than delivering an empty design.
    /// </summary>
    public static DesignProposal? ParseProposal(string? llmText)
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
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            var summary = ReadString(element, "summary");

            // Fail-closed: the summary is the load-bearing field. Without it there is no
            // design to review — never fabricate one.
            if (string.IsNullOrWhiteSpace(summary))
                return null;

            return new DesignProposal
            {
                Summary = summary,
                Recommendation = ReadString(element, "recommendation"),
                ConstraintEvaluation = ReadString(element, "constraintEvaluation"),
                Alternatives = ParseAlternatives(element),
            };
        }
        catch
        {
            return null;
        }
    }

    private static List<DesignAlternative> ParseAlternatives(JsonElement element)
    {
        var result = new List<DesignAlternative>();
        if (!element.TryGetProperty("alternatives", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var name = item.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    result.Add(new DesignAlternative { Name = name!.Trim() });
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var altName = ReadString(item, "name");
            var tradeoffs = ReadString(item, "tradeoffs");
            if (string.IsNullOrWhiteSpace(altName) && string.IsNullOrWhiteSpace(tradeoffs))
                continue;

            result.Add(new DesignAlternative { Name = altName, Tradeoffs = tradeoffs });
        }

        return result;
    }

    private static string ReadString(JsonElement element, string key)
        => element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim() ?? string.Empty
            : string.Empty;
}
