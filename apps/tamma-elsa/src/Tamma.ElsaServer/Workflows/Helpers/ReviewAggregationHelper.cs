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

                if (root.TryGetProperty("verdict", out var v))
                    verdict = v.GetString() ?? "concerns";
                if (root.TryGetProperty("comments", out var c))
                    comments = c.GetString() ?? "";
                if (root.TryGetProperty("suggestedChanges", out var s))
                    suggestedChanges = s.GetString() ?? "";
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
