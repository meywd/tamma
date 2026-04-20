using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Activities.AgentDispatch.Models;

namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// Default <see cref="IAgentResultCollectorService"/> — reads the agent's
/// outputs from the completed workflow run (story 19-4).
///
/// <para>Data sources, in priority order:</para>
/// <list type="number">
///   <item>
///     <b>Result artifact</b> (<c>.tamma/result.json</c> uploaded by the
///     agent workflow template) — authoritative values for
///     <c>Success / TokensUsed / DurationSeconds / AgentLogSummary</c>.
///   </item>
///   <item>
///     <b>PR data</b> — if the agent opened a PR for the branch, we pull
///     <c>PrNumber</c> / <c>PrUrl</c> / <c>ChangedFiles</c>.
///   </item>
///   <item>
///     <b>Compare API</b> — fallback for <c>FilesChanged</c> /
///     <c>CommitSha</c> / <c>CommitsCount</c> when the artifact is
///     missing (agent crashed) or the PR wasn't opened yet.
///   </item>
///   <item>
///     <b>Check runs</b> on the head SHA — populates <c>ChecksPassed</c>.
///   </item>
/// </list>
///
/// <para>The service never throws for "expected" failure modes (expired
/// artifact, missing PR, conclusion==failure) — it surfaces them via
/// <see cref="AgentExecutionResult"/> with <c>Success=false</c> and a
/// descriptive <c>ErrorMessage</c>.</para>
/// </summary>
public sealed class AgentResultCollectorService : IAgentResultCollectorService
{
    private const string ResultArtifactName = "tamma-result";
    private const string ResultArtifactFileName = "result.json";
    private const string DefaultBaseBranch = "main";

    private readonly IGitHubActionsClient _client;
    private readonly ILogger<AgentResultCollectorService>? _logger;

    public AgentResultCollectorService(
        IGitHubActionsClient client,
        ILogger<AgentResultCollectorService>? logger = null)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<AgentExecutionResult> CollectAsync(
        AgentExecutionRequest request,
        AgentMonitorResult monitorResult,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseRepository(request.Repository, out var owner, out var repo))
        {
            return AgentExecutionResult.Failed(
                $"Invalid repository format '{request.Repository}'",
                request.AgentProvider,
                ExecutionModeNames.GitHubActions);
        }

        // 1. Try to download the result artifact first — it's the
        //    authoritative source for token counts / error messages.
        AgentResultArtifact? artifact = null;
        try
        {
            artifact = await DownloadResultArtifactAsync(
                owner, repo, monitorResult.WorkflowRunId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Failed to download result artifact for run {RunId}; will fall back to git state",
                monitorResult.WorkflowRunId);
        }

        // 2. PR lookup by head branch.
        var pr = await TryFindPullRequestAsync(owner, repo, request.BranchName, cancellationToken);

        // 3. Compare base...head to enumerate commits + files changed.
        //    Only needed when the artifact didn't provide them, but we
        //    always fetch so ChecksPassed calculation has a head SHA.
        var comparison = await TryCompareAsync(owner, repo, request.BranchName, cancellationToken);

        // 4. Check runs on the head SHA.
        var headSha = artifact?.CommitSha
            ?? pr?.HeadSha
            ?? comparison?.HeadSha
            ?? string.Empty;
        bool? checksPassed = null;
        if (!string.IsNullOrEmpty(headSha))
        {
            try
            {
                var checks = await _client.ListCheckRunsAsync(owner, repo, headSha, cancellationToken);
                checksPassed = ComputeChecksPassed(checks);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex,
                    "Could not read check runs for {Repository}@{Sha}", request.Repository, headSha);
            }
        }

        // 5. Merge sources into a unified result.
        var success = DetermineSuccess(monitorResult, artifact);
        var filesChanged = artifact?.FilesChanged
            ?? comparison?.Files.Select(f => f.Filename).ToArray()
            ?? Array.Empty<string>();
        var commitsCount = comparison?.Commits.Count ?? 0;
        var commitSha = artifact?.CommitSha
            ?? pr?.HeadSha
            ?? comparison?.HeadSha
            ?? string.Empty;
        var prNumber = artifact?.PrNumber ?? pr?.Number;
        var prUrl = pr?.HtmlUrl;

        string? errorMessage = artifact?.ErrorMessage;
        if (string.IsNullOrEmpty(errorMessage) && !success)
        {
            if (!string.Equals(monitorResult.Conclusion, "success", StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = artifact is null
                    ? $"Agent workflow completed with conclusion: {monitorResult.Conclusion}; no result artifact found"
                    : $"Agent workflow completed with conclusion: {monitorResult.Conclusion}";
            }
        }

        var durationSeconds = artifact?.DurationSeconds ?? monitorResult.DurationSeconds;

        return new AgentExecutionResult(
            Success: success,
            PrNumber: prNumber,
            PrUrl: prUrl,
            CommitSha: commitSha,
            FilesChanged: filesChanged,
            CommitsCount: commitsCount,
            ChecksPassed: checksPassed,
            TokensUsed: artifact?.TokensUsed ?? 0,
            DurationSeconds: durationSeconds,
            ErrorMessage: errorMessage,
            AgentLogSummary: artifact?.AgentLogSummary,
            AgentProvider: artifact?.AgentProvider ?? request.AgentProvider,
            AgentVersion: artifact?.AgentVersion,
            ExecutionMode: ExecutionModeNames.GitHubActions);
    }

    private async Task<AgentResultArtifact?> DownloadResultArtifactAsync(
        string owner, string repo, long runId, CancellationToken ct)
    {
        var artifacts = await _client.ListRunArtifactsAsync(owner, repo, runId, ct);
        var result = artifacts.FirstOrDefault(a =>
            string.Equals(a.Name, ResultArtifactName, StringComparison.OrdinalIgnoreCase));
        if (result is null || result.Expired)
        {
            _logger?.LogInformation(
                "Result artifact not available for run {RunId} (expired={Expired}, count={Count})",
                runId, result?.Expired ?? false, artifacts.Count);
            return null;
        }

        var zipBytes = await _client.DownloadArtifactZipAsync(owner, repo, result.Id, ct);
        if (zipBytes is null || zipBytes.Length == 0)
        {
            return null;
        }

        using var ms = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        var entry = archive.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(ResultArtifactFileName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            _logger?.LogWarning(
                "Zip artifact for run {RunId} did not contain {FileName}", runId, ResultArtifactFileName);
            return null;
        }

        using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream);
        var json = await reader.ReadToEndAsync(ct);
        return ParseResultJson(json);
    }

    internal static AgentResultArtifact? ParseResultJson(string json)
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
                    if (f.ValueKind == JsonValueKind.String)
                    {
                        var s = f.GetString();
                        if (!string.IsNullOrEmpty(s)) filesChanged.Add(s);
                    }
                }
            }

            return new AgentResultArtifact(
                Success: ReadBool(root, "success") ?? false,
                Task: ReadString(root, "task") ?? string.Empty,
                IssueNumber: ReadInt(root, "issue_number") ?? 0,
                BranchName: ReadString(root, "branch_name") ?? string.Empty,
                TammaSessionId: ReadString(root, "tamma_session_id") ?? string.Empty,
                FilesChanged: filesChanged.ToArray(),
                PrNumber: ReadInt(root, "pr_number"),
                CommitSha: ReadString(root, "commit_sha") ?? string.Empty,
                ErrorMessage: ReadString(root, "error_message"),
                AgentLogSummary: ReadString(root, "agent_log_summary"),
                TokensUsed: ReadInt(root, "tokens_used") ?? 0,
                DurationSeconds: ReadInt(root, "duration_seconds") ?? 0,
                AgentProvider: ReadString(root, "agent_provider") ?? "claude-code",
                AgentVersion: ReadString(root, "agent_version"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<PullRequestSummary?> TryFindPullRequestAsync(
        string owner, string repo, string branch, CancellationToken ct)
    {
        try
        {
            var prs = await _client.ListPullRequestsForHeadAsync(owner, repo, branch, ct);
            // If multiple PRs share a head branch, prefer the highest-numbered
            // (most-recent) one.
            return prs.OrderByDescending(p => p.Number).FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "PR lookup failed for {Owner}/{Repo} head={Branch}", owner, repo, branch);
            return null;
        }
    }

    private async Task<BranchComparison?> TryCompareAsync(
        string owner, string repo, string headBranch, CancellationToken ct)
    {
        try
        {
            // Assume main as base. Callers can supply a different base via
            // AgentExecutionRequest in a future iteration if needed.
            return await _client.CompareRefsAsync(owner, repo, DefaultBaseBranch, headBranch, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex,
                "Compare failed for {Owner}/{Repo} {Base}...{Head}",
                owner, repo, DefaultBaseBranch, headBranch);
            return null;
        }
    }

    private static bool? ComputeChecksPassed(IReadOnlyList<CheckRunSummary> checks)
    {
        if (checks.Count == 0) return null;

        var terminal = checks.Where(c =>
            string.Equals(c.Status, "completed", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (terminal.Count == 0) return null; // still pending

        return terminal.All(c =>
            string.Equals(c.Conclusion, "success", StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Conclusion, "skipped", StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Conclusion, "neutral", StringComparison.OrdinalIgnoreCase));
    }

    private static bool DetermineSuccess(AgentMonitorResult monitor, AgentResultArtifact? artifact)
    {
        if (artifact is not null) return artifact.Success;
        return string.Equals(monitor.Conclusion, "success", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseRepository(string? repository, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        if (string.IsNullOrWhiteSpace(repository)) return false;
        var parts = repository.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        owner = parts[0];
        repo = parts[1];
        return !string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(repo);
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
