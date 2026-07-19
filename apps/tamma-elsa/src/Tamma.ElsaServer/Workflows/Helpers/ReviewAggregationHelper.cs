using System.Text.Json;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Extracted from PlanReviewWorkflow — parses and aggregates role verdicts.
/// Pure logic, no Elsa runtime dependency.
/// </summary>
public static class ReviewAggregationHelper
{
    /// <summary>
    /// Parses a single role's review JSON to extract verdict, comments, and suggestedChanges.
    /// Returns "concerns" verdict on parse failure (pessimistic default).
    ///
    /// <para>Accepts BOTH verdict shapes: the legacy string form
    /// (<c>{"verdict":"approve", ...}</c>) and the object form the PlanReview prompt
    /// template (<c>SystemPrompts.PlanReview</c>) actually instructs —
    /// <c>{"verdict":{"decision":"APPROVE|REQUEST_CHANGES|NEEDS_DISCUSSION","summary":"...","blockingIssues":[...]}}</c>.
    /// Object decisions map onto the existing verdict vocabulary consumed by
    /// <c>PlanReviewWorkflow</c> / <see cref="AggregateVerdicts"/>: <c>APPROVE</c> →
    /// <c>"approve"</c>; <c>REQUEST_CHANGES</c>, <c>NEEDS_DISCUSSION</c> and anything
    /// unrecognized → <c>"concerns"</c> (pessimistic). The object's <c>summary</c> and
    /// <c>blockingIssues</c> are carried into comments when the reply has no top-level
    /// <c>comments</c> string.</para>
    /// </summary>
    public static (string verdict, string comments, string suggestedChanges) ParseRoleVerdict(string reviewJson)
    {
        var verdict = "concerns";
        var comments = "";
        var suggestedChanges = "";

        try
        {
            if (!string.IsNullOrWhiteSpace(reviewJson) && reviewJson != "{}")
            {
                var doc = JsonDocument.Parse(reviewJson);
                var root = doc.RootElement;

                var objectVerdictComments = "";
                if (root.TryGetProperty("verdict", out var v))
                {
                    if (v.ValueKind == JsonValueKind.Object)
                        (verdict, objectVerdictComments) = ParseObjectVerdict(v);
                    else
                        verdict = v.GetString() ?? "concerns";
                }
                if (root.TryGetProperty("comments", out var c))
                    comments = c.GetString() ?? "";
                if (root.TryGetProperty("suggestedChanges", out var s))
                    suggestedChanges = s.GetString() ?? "";

                // Object-shape replies carry their narrative inside the verdict object;
                // surface it when there is no top-level comments string.
                if (string.IsNullOrWhiteSpace(comments))
                    comments = objectVerdictComments;
            }
        }
        catch
        {
            // Treat parse errors as concerns
            comments = reviewJson;
        }

        return (verdict, comments, suggestedChanges);
    }

    /// <summary>
    /// Maps an object-shaped verdict (<c>{"decision":..., "summary":..., "blockingIssues":[...]}</c>)
    /// onto the existing string vocabulary, and folds summary + blocking issues into a
    /// comments string. Unknown / missing decisions stay pessimistic ("concerns").
    /// </summary>
    private static (string verdict, string comments) ParseObjectVerdict(JsonElement verdictObj)
    {
        var decision = verdictObj.TryGetProperty("decision", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() ?? ""
            : "";

        var verdict = decision.Trim().ToUpperInvariant() switch
        {
            "APPROVE" => "approve",
            // REQUEST_CHANGES / NEEDS_DISCUSSION / anything else → existing pessimistic vocabulary
            _ => "concerns",
        };

        var comments = "";
        if (verdictObj.TryGetProperty("summary", out var sum) && sum.ValueKind == JsonValueKind.String)
            comments = sum.GetString() ?? "";

        if (verdictObj.TryGetProperty("blockingIssues", out var bi) && bi.ValueKind == JsonValueKind.Array)
        {
            var blocking = string.Join("; ", bi.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText())
                .Where(sVal => !string.IsNullOrWhiteSpace(sVal)));

            if (!string.IsNullOrWhiteSpace(blocking))
                comments = string.IsNullOrWhiteSpace(comments)
                    ? $"Blocking issues: {blocking}"
                    : $"{comments} Blocking issues: {blocking}";
        }

        return (verdict, comments);
    }

    /// <summary>
    /// Aggregates verdicts from all roles. Returns true only if ALL verdicts are "approve".
    /// </summary>
    public static bool AggregateVerdicts(IEnumerable<string> verdicts)
    {
        return verdicts.All(v => v == "approve");
    }

    /// <summary>
    /// Parses a discussion result JSON. Returns parsed fields or defaults on failure.
    /// </summary>
    public static DiscussionResult ParseDiscussionResult(string discussionJson)
    {
        var result = new DiscussionResult();

        var extracted = "";
        if (!string.IsNullOrWhiteSpace(discussionJson))
        {
            var jsonStart = discussionJson.IndexOf('{');
            var jsonEnd = discussionJson.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
                extracted = discussionJson[jsonStart..(jsonEnd + 1)];
        }

        if (string.IsNullOrWhiteSpace(extracted))
        {
            result.Decision = "needsHuman";
            result.ReviewNotes = "Failed to parse discussion result";
            return result;
        }

        try
        {
            var doc = JsonDocument.Parse(extracted);
            var root = doc.RootElement;

            if (root.TryGetProperty("modifiedPlan", out var mp))
            {
                result.ModifiedPlan = mp.ValueKind == JsonValueKind.String
                    ? mp.GetString() ?? ""
                    : mp.GetRawText();
            }

            if (root.TryGetProperty("deferred", out var def))
                result.Deferred = def.GetRawText();

            if (root.TryGetProperty("split", out var sp))
                result.Split = sp.GetRawText();

            if (root.TryGetProperty("overallDecision", out var od))
                result.Decision = od.GetString() ?? "needsHuman";

            if (root.TryGetProperty("reviewNotes", out var rn))
                result.ReviewNotes = rn.GetString() ?? "";

            if (root.TryGetProperty("resolutions", out var res))
                result.Resolutions = res.GetRawText();
        }
        catch
        {
            result.Decision = "needsHuman";
            result.ReviewNotes = $"Failed to parse discussion result: {discussionJson}";
        }

        return result;
    }
}

public class DiscussionResult
{
    public string Decision { get; set; } = "needsHuman";
    public string ReviewNotes { get; set; } = "";
    public string ModifiedPlan { get; set; } = "";
    public string Deferred { get; set; } = "[]";
    public string Split { get; set; } = "[]";
    public string Resolutions { get; set; } = "[]";
}
