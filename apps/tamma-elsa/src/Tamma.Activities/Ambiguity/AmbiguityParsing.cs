using System.Text.Json;
using Tamma.Activities.Ambiguity.Models;

namespace Tamma.Activities.Ambiguity;

/// <summary>
/// Story 3.6 — pure, context-free parser that recovers the structured
/// <see cref="AmbiguityAssessment"/> from a mediated <c>llm-call</c> scoring response. Kept
/// side-effect-free (no Elsa context) so the fail-closed behaviour is unit-testable without a
/// live LLM. Mirrors the JSON-slice approach in <c>ResearchParsing</c> / <c>ClarifyParsing</c>
/// / <c>AssessmentWorkflow</c>.
///
/// <para>The parser is <b>fail-closed</b>: an empty response, a response with no JSON object, a
/// response whose <c>score</c> is missing / non-numeric / outside [0,1], or a response with no
/// non-empty <c>rationale</c> all yield <c>null</c>. The workflow routes a <c>null</c> parse to
/// its <c>AMBIGUITY.FAILED</c> error terminal — it NEVER fabricates a score, a rationale, or an
/// ambiguity item the downstream would act on. A <b>valid</b> assessment with an empty
/// <c>ambiguities</c> list is legitimate (a genuinely clear requirement scored near 0), so an
/// empty breakdown is NOT a failure.</para>
/// </summary>
public static class AmbiguityParsing
{
    /// <summary>
    /// Extract the ambiguity assessment from an <c>llm-call</c> text response. Expects a JSON
    /// object of the shape
    /// <c>{"score":0.72,"confidence":0.8,"rationale":"...","ambiguities":[{"type":"vague",
    /// "description":"...","severity":"high","recommendation":"..."}]}</c>.
    ///
    /// <para>The <c>score</c> and <c>rationale</c> are load-bearing: the score is the whole
    /// point of the workflow, and a score with no rationale is not auditable/actionable.
    /// Ambiguity item <c>type</c> / <c>severity</c> labels are normalised onto the canonical
    /// buckets (<see cref="AmbiguityTypes"/> / <see cref="AmbiguitySeverities"/>); items with
    /// no <c>description</c> are dropped as empty shells (item-level fail-closed) but do not
    /// fail the whole parse.</para>
    ///
    /// <para>Returns <c>null</c> (fail-closed) when the text is empty, carries no JSON object,
    /// has a missing / non-numeric / out-of-range <c>score</c>, or has no non-empty
    /// <c>rationale</c>.</para>
    /// </summary>
    /// <param name="llmText">The raw LLM scoring response.</param>
    public static AmbiguityAssessment? ParseAssessment(string? llmText)
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

            // Fail-closed: the score is the whole point — it must be a number in [0,1].
            if (!element.TryGetProperty("score", out var sv) || sv.ValueKind != JsonValueKind.Number)
                return null;

            var score = (decimal)sv.GetDouble();
            if (score < 0m || score > 1m)
                return null;

            var rationale = ReadString(element, "rationale");

            // Fail-closed: a score with no rationale is not auditable/actionable.
            if (string.IsNullOrWhiteSpace(rationale))
                return null;

            var confidence = element.TryGetProperty("confidence", out var cv) && cv.ValueKind == JsonValueKind.Number
                ? ClampConfidence((decimal)cv.GetDouble())
                : 0m;

            return new AmbiguityAssessment
            {
                Score = score,
                Rationale = rationale,
                Confidence = confidence,
                Ambiguities = ParseAmbiguities(element),
            };
        }
        catch
        {
            // Malformed JSON → fail closed.
            return null;
        }
    }

    /// <summary>
    /// Recover the itemised ambiguity breakdown. Each item must carry a non-empty
    /// <c>description</c> — empty shells are dropped. Type / severity labels are normalised
    /// onto the canonical buckets.
    /// </summary>
    private static List<AmbiguityItem> ParseAmbiguities(JsonElement element)
    {
        if (!element.TryGetProperty("ambiguities", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<AmbiguityItem>();

        var items = new List<AmbiguityItem>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var description = ReadString(item, "description");

            // An item with no description is an empty shell — skip it rather than admit a
            // fabricated blank ambiguity.
            if (string.IsNullOrWhiteSpace(description))
                continue;

            items.Add(new AmbiguityItem
            {
                Type = AmbiguityTypes.Normalize(ReadString(item, "type")),
                Description = description,
                Severity = AmbiguitySeverities.Normalize(ReadString(item, "severity")),
                Recommendation = ReadString(item, "recommendation"),
            });
        }

        return items;
    }

    private static string ReadString(JsonElement element, string key)
        => element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static decimal ClampConfidence(decimal value)
        => value < 0m ? 0m : value > 1m ? 1m : value;
}
