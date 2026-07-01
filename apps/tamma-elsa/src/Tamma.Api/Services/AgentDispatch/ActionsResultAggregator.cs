using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Tamma.Activities.AgentDispatch;
using Tamma.Activities.AgentDispatch.Models;

namespace Tamma.Api.Services.AgentDispatch;

/// <summary>
/// Story 38-2 (AC4) — the server-side collect aggregation, moved out of the
/// engine's former <c>AgentResultCollectorService</c> so the multiple GitHub
/// reads (result artifact, PR-for-head, base...head compare, check runs) happen
/// INSIDE <c>Tamma.Api</c> behind <c>GET .../runs/{id}/results</c>. The engine's
/// collector is now a thin <c>TammaApiClient</c> client mapping this one
/// aggregated result to its output variables.
///
/// <para>Data sources, in priority order (unchanged from story 19-4):</para>
/// <list type="number">
///   <item><b>Result artifact</b> (<c>tamma-result/result.json</c>) — authoritative
///     Success / TokensUsed / DurationSeconds / AgentLogSummary.</item>
///   <item><b>PR data</b> — PrNumber / PrUrl / head sha for the branch.</item>
///   <item><b>Compare API</b> — FilesChanged / CommitSha / CommitsCount fallback.</item>
///   <item><b>Check runs</b> on the head SHA — ChecksPassed.</item>
/// </list>
///
/// <para>Never throws for "expected" failure modes (expired artifact, missing PR,
/// conclusion==failure) — those surface via the returned result. A genuinely
/// unexpected exception propagates to the mediation service's guarded envelope,
/// which converts it to a typed <c>PLATFORM_ERROR</c> (never a raw 5xx).</para>
/// </summary>
public interface IActionsResultAggregator
{
    Task<AgentRunResultsResult> AggregateAsync(
        string owner, string repo, long runId, CollectAgentRunRequest request, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ActionsResultAggregator : IActionsResultAggregator
{
    private const string ResultArtifactName = "tamma-result";
    private const string ResultArtifactFileName = "result.json";
    private const string DefaultBaseBranch = "main";

    private readonly IGitHubActionsClient _client;
    private readonly ILogger<ActionsResultAggregator>? _logger;

    public ActionsResultAggregator(
        IGitHubActionsClient client,
        ILogger<ActionsResultAggregator>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger;
    }

    public async Task<AgentRunResultsResult> AggregateAsync(
        string owner, string repo, long runId, CollectAgentRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var branch = request.BranchName;

        // 1. Try to download the result artifact first — authoritative token counts / error.
        AgentResultArtifact? artifact = null;
        try
        {
            artifact = await DownloadResultArtifactAsync(owner, repo, runId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Failed to download result artifact for run {RunId}; will fall back to git state", runId);
        }

        // 2. PR lookup by head branch.
        var pr = await TryFindPullRequestAsync(owner, repo, branch, ct).ConfigureAwait(false);

        // 3. Compare base...head to enumerate commits + files changed.
        var comparison = await TryCompareAsync(owner, repo, branch, ct).ConfigureAwait(false);

        // 4. Check runs on the head SHA.
        var headSha = artifact?.CommitSha ?? pr?.HeadSha ?? comparison?.HeadSha ?? string.Empty;
        bool? checksPassed = null;
        if (!string.IsNullOrEmpty(headSha))
        {
            try
            {
                var checks = await _client.ListCheckRunsAsync(owner, repo, headSha, ct).ConfigureAwait(false);
                checksPassed = ComputeChecksPassed(checks);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Could not read check runs for {Owner}/{Repo}@{Sha}", owner, repo, headSha);
            }
        }

        // 5. Merge sources into a unified result.
        var agentSuccess = DetermineSuccess(request.Conclusion, artifact);
        var filesChanged = artifact?.FilesChanged
            ?? comparison?.Files.Select(f => f.Filename).ToArray()
            ?? Array.Empty<string>();
        var commitsCount = comparison?.Commits.Count ?? 0;
        var commitSha = artifact?.CommitSha ?? pr?.HeadSha ?? comparison?.HeadSha ?? string.Empty;
        var prNumber = artifact?.PrNumber ?? pr?.Number;
        var prUrl = pr?.HtmlUrl;

        string? errorMessage = artifact?.ErrorMessage;
        if (string.IsNullOrEmpty(errorMessage) && !agentSuccess)
        {
            if (!string.Equals(request.Conclusion, "success", StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = artifact is null
                    ? $"Agent workflow completed with conclusion: {request.Conclusion}; no result artifact found"
                    : $"Agent workflow completed with conclusion: {request.Conclusion}";
            }
        }

        var durationSeconds = artifact?.DurationSeconds ?? request.DurationSeconds;

        return new AgentRunResultsResult
        {
            Success = true, // mediation succeeded — the aggregation ran (agent success rides in AgentSuccess).
            CredentialSource = AgentDispatchCredentialSources.Installation,
            AgentSuccess = agentSuccess,
            PrNumber = prNumber,
            PrUrl = prUrl,
            CommitSha = commitSha,
            FilesChanged = filesChanged,
            CommitsCount = commitsCount,
            ChecksPassed = checksPassed,
            TokensUsed = artifact?.TokensUsed ?? 0,
            DurationSeconds = durationSeconds,
            ErrorMessage = errorMessage,
            AgentLogSummary = artifact?.AgentLogSummary,
            AgentProvider = artifact?.AgentProvider ?? request.AgentProvider,
            AgentVersion = artifact?.AgentVersion,
            CorrelationId = request.CorrelationId,
        };
    }

    private async Task<AgentResultArtifact?> DownloadResultArtifactAsync(
        string owner, string repo, long runId, CancellationToken ct)
    {
        var artifacts = await _client.ListRunArtifactsAsync(owner, repo, runId, ct).ConfigureAwait(false);
        var result = artifacts.FirstOrDefault(a =>
            string.Equals(a.Name, ResultArtifactName, StringComparison.OrdinalIgnoreCase));
        if (result is null || result.Expired)
        {
            _logger?.LogInformation(
                "Result artifact not available for run {RunId} (expired={Expired}, count={Count})",
                runId, result?.Expired ?? false, artifacts.Count);
            return null;
        }

        var zipBytes = await _client.DownloadArtifactZipAsync(owner, repo, result.Id, ct).ConfigureAwait(false);
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
            _logger?.LogWarning("Zip artifact for run {RunId} did not contain {FileName}", runId, ResultArtifactFileName);
            return null;
        }

        // Review-session 2026-04-20 finding 6: cap the decompressed entry size.
        if (entry.Length > AgentResultArtifactParser.MaxResultJsonBytes)
        {
            _logger?.LogWarning(
                "result.json entry for run {RunId} exceeds cap: declared length={Length} cap={Cap}",
                runId, entry.Length, AgentResultArtifactParser.MaxResultJsonBytes);
            return null;
        }

        using var entryStream = entry.Open();
        using var limited = new LimitedStream(entryStream, AgentResultArtifactParser.MaxResultJsonBytes);
        using var reader = new StreamReader(limited);
        string json;
        try
        {
            json = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        catch (ArtifactTooLargeException ex)
        {
            _logger?.LogWarning(
                "result.json entry for run {RunId} exceeded decompressed cap {Limit}; rejecting", runId, ex.Limit);
            return null;
        }
        return AgentResultArtifactParser.ParseResultJson(json);
    }

    private async Task<PullRequestSummary?> TryFindPullRequestAsync(
        string owner, string repo, string branch, CancellationToken ct)
    {
        try
        {
            var prs = await _client.ListPullRequestsForHeadAsync(owner, repo, branch, ct).ConfigureAwait(false);
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
            return await _client.CompareRefsAsync(owner, repo, DefaultBaseBranch, headBranch, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Compare failed for {Owner}/{Repo} {Base}...{Head}", owner, repo, DefaultBaseBranch, headBranch);
            return null;
        }
    }

    private static bool? ComputeChecksPassed(IReadOnlyList<CheckRunSummary> checks)
    {
        if (checks.Count == 0) return null;

        var terminal = checks.Where(c =>
            string.Equals(c.Status, "completed", StringComparison.OrdinalIgnoreCase)).ToList();
        if (terminal.Count == 0) return null; // still pending

        return terminal.All(c =>
            string.Equals(c.Conclusion, "success", StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Conclusion, "skipped", StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Conclusion, "neutral", StringComparison.OrdinalIgnoreCase));
    }

    private static bool DetermineSuccess(string conclusion, AgentResultArtifact? artifact)
    {
        if (artifact is not null) return artifact.Success;
        return string.Equals(conclusion, "success", StringComparison.OrdinalIgnoreCase);
    }
}
