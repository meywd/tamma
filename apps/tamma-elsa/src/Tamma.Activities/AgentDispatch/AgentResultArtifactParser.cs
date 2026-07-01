using System.Text.Json;
using Tamma.Activities.AgentDispatch.Models;

namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// Story 38-2 — pure parser for the <c>.tamma/result.json</c> artifact produced
/// by the agent runner (story 19-1). Extracted from the former
/// <c>AgentResultCollectorService</c> so it can be reused by the server-side
/// <c>ActionsResultAggregator</c> (Tamma.Api) — where the multi-read aggregation
/// now lives after the Class-C cutover — WITHOUT dragging the credential-holding
/// <c>IGitHubActionsClient</c> into Tamma.Activities. Contains NO platform calls;
/// it only decodes + clamps attacker-controlled JSON.
/// </summary>
public static class AgentResultArtifactParser
{
    // Review-session 2026-04-20 finding 6: cap the decompressed result.json entry
    // size so a 4 MB zip that decompresses to hundreds of MB of attacker-controlled
    // JSON cannot OOM the process. 4 MB is the same cap used on the artifact
    // download path.
    public const long MaxResultJsonBytes = 4L * 1024 * 1024;

    // Review-session 2026-04-20 finding 8 hint: clamp individual string fields so a
    // malicious agent cannot bloat the workflow_instances JSONB column. 32 KB for
    // the log summary (verbose tool traces), 2 KB for the short error / branch / sha
    // fields.
    public const int MaxAgentLogSummaryChars = 32 * 1024;
    public const int MaxShortStringChars = 2 * 1024;

    // Review-session 2026-06-30 finding 6: the per-string clamp above bounds each entry
    // but NOT the array LENGTH — a 4 MB result.json can carry ~1M tiny file entries
    // (~50-90 MB alloc across API+engine per collect, persisted to JSONB). Cap the
    // files_changed COUNT (enumeration stops at the cap, bounding allocation), and clamp
    // tokens_used to a sane ceiling so a poisoned value can't corrupt cost/analytics.
    public const int MaxFilesChangedCount = 2000;
    public const int MaxTokensUsed = 100_000_000;

    public static AgentResultArtifact? ParseResultJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var filesChanged = new List<string>();
            if (root.TryGetProperty("files_changed", out var fc) && fc.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in fc.EnumerateArray())
                {
                    // Finding 6: bound the COUNT (not just each entry) — stop enumerating
                    // at the cap so a million-entry array can't balloon the allocation.
                    if (filesChanged.Count >= MaxFilesChangedCount) break;
                    if (f.ValueKind == JsonValueKind.String)
                    {
                        var s = f.GetString();
                        if (!string.IsNullOrEmpty(s))
                        {
                            filesChanged.Add(Clamp(s, MaxShortStringChars)!);
                        }
                    }
                }
            }

            // Review-session 2026-04-20 finding 6 (+ finding 8 hint): clamp every
            // string field so a malicious agent cannot bloat the
            // workflow_instances.Result JSONB column or other downstream persistence
            // with multi-MB strings.
            return new AgentResultArtifact(
                Success: ReadBool(root, "success") ?? false,
                Task: Clamp(ReadString(root, "task"), MaxShortStringChars) ?? string.Empty,
                IssueNumber: ReadInt(root, "issue_number") ?? 0,
                BranchName: Clamp(ReadString(root, "branch_name"), MaxShortStringChars) ?? string.Empty,
                TammaSessionId: Clamp(ReadString(root, "tamma_session_id"), MaxShortStringChars) ?? string.Empty,
                FilesChanged: filesChanged.ToArray(),
                PrNumber: ReadInt(root, "pr_number"),
                CommitSha: Clamp(ReadString(root, "commit_sha"), MaxShortStringChars) ?? string.Empty,
                ErrorMessage: Clamp(ReadString(root, "error_message"), MaxShortStringChars),
                AgentLogSummary: Clamp(ReadString(root, "agent_log_summary"), MaxAgentLogSummaryChars),
                TokensUsed: Math.Clamp(ReadInt(root, "tokens_used") ?? 0, 0, MaxTokensUsed),
                DurationSeconds: ReadInt(root, "duration_seconds") ?? 0,
                AgentProvider: Clamp(ReadString(root, "agent_provider"), MaxShortStringChars) ?? "claude-code",
                AgentVersion: Clamp(ReadString(root, "agent_version"), MaxShortStringChars));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? Clamp(string? value, int maxChars)
    {
        if (value is null) return null;
        if (value.Length <= maxChars) return value;
        return value.Substring(0, maxChars);
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? ReadInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number when p.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(p.GetString(), out var s) => s,
            _ => null
        };
    }

    private static bool? ReadBool(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(p.GetString(), out var b) => b,
            _ => null
        };
    }
}
