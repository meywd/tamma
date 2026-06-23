using System.Text.Json;
using Tamma.Activities.ADL;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Completeness audit 2026-06-22 (<c>TriageContextGathering.md</c>) — pure helpers
/// for the built-out <c>triage-context-gathering</c> workflow. Kept side-effect-free
/// and Elsa-context-free so the load-bearing logic (item-type detection that the
/// prompt actually consumes, and the no-false-success context extraction) is
/// unit-testable directly.
/// </summary>
public static class TriageContextHelper
{
    public const string ItemTypeIssue = "issue";
    public const string ItemTypeSecurity = "security";
    public const string ItemTypeDependency = "dependency";

    /// <summary>
    /// Detect the triage item type by PARSING the item JSON (not substring-sniffing
    /// the raw text — the prior approach broke on whitespaced / pretty-printed JSON,
    /// audit §5 #5). Reads the <c>type</c> field plus the presence of
    /// <c>advisory</c>/<c>cve</c>/<c>ghsaId</c> (security) or <c>dependency</c>/
    /// <c>package</c>/<c>manifestPath</c> (dependency) markers. Defaults to
    /// <see cref="ItemTypeIssue"/> only when genuinely absent. Never throws — a
    /// malformed body falls back to a tolerant raw-text sniff and then to
    /// <c>issue</c>.
    /// </summary>
    public static string DetectItemType(string? itemJson)
    {
        if (string.IsNullOrWhiteSpace(itemJson)) return ItemTypeIssue;

        try
        {
            using var doc = JsonDocument.Parse(itemJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                // Explicit "type" field takes precedence.
                if (root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    var type = (t.GetString() ?? "").Trim().ToLowerInvariant();
                    if (type.StartsWith("security", StringComparison.Ordinal)) return ItemTypeSecurity;
                    if (type.StartsWith("dependabot", StringComparison.Ordinal)
                        || type.StartsWith("dependency", StringComparison.Ordinal)) return ItemTypeDependency;
                    if (type.StartsWith("vuln", StringComparison.Ordinal)) return ItemTypeSecurity;
                }

                // Structural markers — a security advisory carries advisory/cve/ghsa.
                if (HasAnyProperty(root, "advisory", "cve", "ghsaId", "ghsa_id", "cvss"))
                    return ItemTypeSecurity;

                // A dependency/dependabot alert carries a dependency/package descriptor.
                if (HasAnyProperty(root, "dependency", "manifestPath", "manifest_path"))
                    return ItemTypeDependency;
            }
        }
        catch
        {
            // Malformed JSON — fall through to the tolerant raw-text sniff so a
            // best-effort type is still derived (never throws).
        }

        return SniffRawText(itemJson);
    }

    /// <summary>
    /// Extract the gathered context JSON from the mediated <c>llm-call</c> result,
    /// reporting a <c>contextStatus</c> so the workflow never presents a failed /
    /// empty scan as a false success (audit §5 #1/#2/#9).
    ///
    /// <list type="bullet">
    ///   <item><description><c>failed</c> — the call reported failure / produced no
    ///     response. ContextJson is the <c>"{}"</c> sentinel; the workflow routes to
    ///     the FAILED terminal (it does NOT present this as gathered context).</description></item>
    ///   <item><description><c>empty</c> — the call succeeded but yielded no usable
    ///     structured context (whitespace, or an empty <c>{}</c> object). Degraded,
    ///     not failed — emitted loud (warning).</description></item>
    ///   <item><description><c>ok</c> — a usable JSON object was extracted, OR
    ///     free-form prose was wrapped as <c>{"rawContext": ...}</c> (the scan still
    ///     produced content). ContextJson carries it.</description></item>
    /// </list>
    /// </summary>
    public static (string ContextJson, string ContextStatus) ExtractContext(
        IDictionary<string, object>? llmResult)
    {
        // No result at all → the mediated call never completed → failed.
        if (llmResult == null)
            return ("{}", TriageContextEvents.StatusFailed);

        // Explicit success=false from llm-call (all providers failed) → failed.
        // Absence of the flag is treated as success (back-compat with callers that
        // don't surface it), matching the panel's ExtractTriageReview convention.
        var success = !llmResult.TryGetValue("success", out var s) || s is true;
        if (!success)
            return ("{}", TriageContextEvents.StatusFailed);

        if (!llmResult.TryGetValue("llmResponse", out var r))
            return ("{}", TriageContextEvents.StatusFailed);

        var output = r?.ToString();
        if (string.IsNullOrWhiteSpace(output))
            return ("{}", TriageContextEvents.StatusEmpty);

        var jsonStart = output.IndexOf('{');
        var jsonEnd = output.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            var candidate = output[jsonStart..(jsonEnd + 1)];
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var hasAny = false;
                    foreach (var _ in doc.RootElement.EnumerateObject()) { hasAny = true; break; }
                    // An empty {} object is not usable structured context → degraded.
                    if (hasAny) return (candidate, TriageContextEvents.StatusOk);
                    return ("{}", TriageContextEvents.StatusEmpty);
                }
            }
            catch { /* not valid JSON — wrap the prose below */ }
        }

        // Free-form prose is still gathered content — wrap it (ok, not empty). The
        // schema is unstructured, but the panel/PO still receive real context.
        var wrapped = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["rawContext"] = output,
        });
        return (wrapped, TriageContextEvents.StatusOk);
    }

    private static bool HasAnyProperty(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.TryGetProperty(name, out _)) return true;
        return false;
    }

    /// <summary>
    /// Tolerant raw-text fallback used only when the body is not parseable JSON.
    /// Best-effort — never the primary detection path.
    /// </summary>
    private static string SniffRawText(string itemJson)
    {
        if (itemJson.Contains("\"type\":\"security", StringComparison.OrdinalIgnoreCase)
            || itemJson.Contains("\"advisory\"", StringComparison.OrdinalIgnoreCase)
            || itemJson.Contains("\"cve\"", StringComparison.OrdinalIgnoreCase))
            return ItemTypeSecurity;
        if (itemJson.Contains("\"type\":\"dependabot", StringComparison.OrdinalIgnoreCase)
            || itemJson.Contains("\"dependency\"", StringComparison.OrdinalIgnoreCase))
            return ItemTypeDependency;
        return ItemTypeIssue;
    }
}
