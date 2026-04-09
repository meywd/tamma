using System.Text.Json;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Extracted from PlanGenerationWorkflow — validates LLM plan output.
/// Pure logic, no Elsa runtime dependency.
/// </summary>
public static class PlanValidationHelper
{
    /// <summary>
    /// Extracts a JSON block from an LLM response string.
    /// Finds the first '{' and last '}' and returns the substring.
    /// Returns empty string if no JSON block found.
    /// </summary>
    public static string ExtractJson(string llmResponse)
    {
        if (string.IsNullOrWhiteSpace(llmResponse))
            return "";

        var jsonStart = llmResponse.IndexOf('{');
        var jsonEnd = llmResponse.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
            return llmResponse[jsonStart..(jsonEnd + 1)];

        return "";
    }

    /// <summary>
    /// Validates a plan JSON string for required fields.
    /// Returns (planJson, isValid, errors).
    /// </summary>
    public static (string planJson, bool isValid, string errors) ValidatePlan(string llmResponse)
    {
        var extracted = ExtractJson(llmResponse);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(extracted) || extracted == "{}")
        {
            errors.Add("Empty plan");
            return (extracted, false, string.Join("; ", errors));
        }

        try
        {
            var doc = JsonDocument.Parse(extracted);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tasks", out _) && !root.TryGetProperty("steps", out _))
                errors.Add("Missing 'tasks' or 'steps'");

            if (!root.TryGetProperty("fileMap", out _) && !root.TryGetProperty("files", out _) && !root.TryGetProperty("filesToModify", out _))
                errors.Add("Missing file map");
        }
        catch (JsonException ex)
        {
            errors.Add($"Invalid JSON: {ex.Message}");
        }

        return (extracted, errors.Count == 0, string.Join("; ", errors));
    }
}
