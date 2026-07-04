using System.Text.Json;
using Tamma.Activities.Clarify.Models;

namespace Tamma.Activities.Clarify;

/// <summary>
/// Story 3.5 — pure, context-free parsers that recover the structured shapes from a
/// mediated <c>llm-call</c> text response. Kept side-effect-free (no Elsa context) so
/// the fail-closed behaviour is unit-testable without a live LLM. Mirrors the
/// JSON-slice approach in <c>AssessmentWorkflow</c> / <c>ContextGatheringWorkflow</c>.
///
/// <para>Every parser is <b>fail-closed</b>: unparseable / empty output yields an
/// empty question list (or a null clarification), which the workflow routes to its
/// <c>CLARIFY.*.FAILED</c> error terminal — it NEVER fabricates questions or a
/// clarification the downstream would act on.</para>
/// </summary>
public static class ClarifyParsing
{
    /// <summary>
    /// Extract the clarifying questions from an <c>llm-call</c> text response.
    /// Tolerant of three shapes the prompt may produce:
    /// a bare JSON array of strings (<c>["Q1","Q2"]</c>), a
    /// <c>{"questions":[...]}</c> / <c>{"clarifyingQuestions":[...]}</c> object.
    /// Returns an EMPTY list when nothing parseable is found (fail-closed).
    /// </summary>
    public static List<string> ParseQuestions(string? llmText)
    {
        if (string.IsNullOrWhiteSpace(llmText))
            return new List<string>();

        // Shape 1: a bare JSON array of strings anywhere in the text.
        var arrStart = llmText.IndexOf('[');
        var arrEnd = llmText.LastIndexOf(']');
        if (arrStart >= 0 && arrEnd > arrStart)
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(llmText[arrStart..(arrEnd + 1)]);
                var cleaned = Clean(list);
                if (cleaned.Count > 0)
                    return cleaned;
            }
            catch { /* fall through to object shapes */ }
        }

        // Shape 2/3: a JSON object carrying a "questions" / "clarifyingQuestions" array.
        var objStart = llmText.IndexOf('{');
        var objEnd = llmText.LastIndexOf('}');
        if (objStart >= 0 && objEnd > objStart)
        {
            try
            {
                var element = JsonSerializer.Deserialize<JsonElement>(llmText[objStart..(objEnd + 1)]);
                foreach (var key in new[] { "questions", "clarifyingQuestions" })
                {
                    var cleaned = ExtractStringList(element, key);
                    if (cleaned.Count > 0)
                        return cleaned;
                }
            }
            catch { /* fail closed below */ }
        }

        return new List<string>();
    }

    /// <summary>
    /// Extract the disambiguated requirement from the incorporation <c>llm-call</c>
    /// response. Expects a JSON object
    /// <c>{"clarifiedRequirement":"...","remainingAmbiguities":[...],"resolved":bool}</c>.
    /// Returns <c>null</c> when the object or the required <c>clarifiedRequirement</c>
    /// field is missing / empty (fail-closed) so the workflow routes to its error
    /// terminal rather than emitting an empty clarification.
    /// </summary>
    public static ClarificationResult? ParseClarification(string? llmText)
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

            var requirement = element.TryGetProperty("clarifiedRequirement", out var cr) && cr.ValueKind == JsonValueKind.String
                ? cr.GetString() ?? string.Empty
                : string.Empty;

            // Fail-closed: the clarified requirement is the load-bearing field.
            if (string.IsNullOrWhiteSpace(requirement))
                return null;

            var resolved = element.TryGetProperty("resolved", out var rv)
                && (rv.ValueKind == JsonValueKind.True
                    || (rv.ValueKind == JsonValueKind.String
                        && string.Equals(rv.GetString(), "true", StringComparison.OrdinalIgnoreCase)));

            return new ClarificationResult
            {
                ClarifiedRequirement = requirement,
                RemainingAmbiguities = ExtractStringList(element, "remainingAmbiguities"),
                Resolved = resolved,
            };
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ExtractStringList(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<string>();

        return Clean(arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString())
            .ToList());
    }

    private static List<string> Clean(IEnumerable<string?>? values)
        => (values ?? Enumerable.Empty<string?>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .ToList();
}
