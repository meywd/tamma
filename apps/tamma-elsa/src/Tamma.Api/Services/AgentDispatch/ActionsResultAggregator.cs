using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Activities.AgentDispatch;
using Tamma.Activities.AgentDispatch.Models;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using PModels = Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Services.AgentDispatch;

/// <summary>
/// Story 38-2 (AC4) / Epic 31 P3 (seam 6) — the server-side collect
/// aggregation behind <c>GET .../runs/{id}/results</c>, now speaking ONLY the
/// resolved platform driver's <see cref="IGitPlatformActionsClient"/> /
/// <see cref="IGitPlatformClient"/> surfaces (the GitHub-only
/// <c>IGitHubActionsClient</c> seam is gone).
///
/// <para>Data sources, in priority order (unchanged from story 19-4):</para>
/// <list type="number">
///   <item><b>Result artifact</b> (<c>tamma-result/result.json</c>) — authoritative
///     Success / TokensUsed / DurationSeconds / AgentLogSummary.</item>
///   <item><b>PR data</b> — PrNumber / PrUrl for the branch.</item>
///   <item><b>Branch file changes</b> — FilesChanged fallback.</item>
///   <item><b>Commit reads</b> — CommitSha fallback (newest commit on the branch).</item>
/// </list>
///
/// <para><b>Capability degradation (plan §4).</b> A typed
/// <c>capability_unsupported</c> on any collect sub-read SKIPS that source
/// WITH ONE <c>AGENT_DISPATCH.COLLECT_STEP.SKIPPED</c> DCB audit event per
/// skipped source (never a throw, never silent) — the aggregation composes
/// whatever the platform can answer. Other sub-read failures stay best-effort
/// exactly as before (logged, source skipped, no event).</para>
///
/// <para>Never throws for "expected" failure modes; a genuinely unexpected
/// exception propagates to the mediation service's guarded envelope
/// (typed <c>PLATFORM_ERROR</c>, never a raw 5xx).</para>
/// </summary>
public interface IActionsResultAggregator
{
    Task<AgentRunResultsResult> AggregateAsync(
        IGitPlatformDriver driver, Guid? tenantId, string owner, string repo, long runId,
        CollectAgentRunRequest request, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ActionsResultAggregator : IActionsResultAggregator
{
    private const string ResultArtifactName = "tamma-result";
    private const string ResultArtifactFileName = "result.json";
    private const string DefaultBaseBranch = "main";

    /// <summary>DCB audit event for one capability-skipped collect source.</summary>
    internal const string CollectStepSkippedEventType = "AGENT_DISPATCH.COLLECT_STEP.SKIPPED";

    private readonly IEventRepository _events;
    private readonly ILogger<ActionsResultAggregator>? _logger;

    public ActionsResultAggregator(
        IEventRepository events,
        ILogger<ActionsResultAggregator>? logger = null)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger;
    }

    public async Task<AgentRunResultsResult> AggregateAsync(
        IGitPlatformDriver driver, Guid? tenantId, string owner, string repo, long runId,
        CollectAgentRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(request);
        var branch = request.BranchName;

        // 1. Result artifact — authoritative token counts / error.
        AgentResultArtifact? artifact = null;
        if (driver.Actions is not null)
        {
            try
            {
                artifact = await DownloadResultArtifactAsync(
                    driver.Actions, tenantId, owner, repo, runId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "Failed to download result artifact for run {RunId}; will fall back to git state", runId);
            }
        }
        else
        {
            await EmitSkippedAsync(tenantId, $"{owner}/{repo}", runId, "result_artifact",
                "the resolved driver has no Actions surface", ct).ConfigureAwait(false);
        }

        // 2. PR lookup by head branch (open PRs targeting the default base).
        var pr = await TryFindPullRequestAsync(driver.Client, tenantId, owner, repo, runId, branch, ct).ConfigureAwait(false);

        // 3. Branch file changes relative to the default branch.
        var files = await TryListFileChangesAsync(driver.Client, tenantId, owner, repo, runId, branch, ct).ConfigureAwait(false);

        // 4. Newest commit on the branch — the head SHA fallback.
        var headCommitSha = artifact?.CommitSha
            ?? await TryReadHeadShaAsync(driver.Client, owner, repo, branch, ct).ConfigureAwait(false);

        // 5. Merge sources into a unified result.
        var agentSuccess = DetermineSuccess(request.Conclusion, artifact);
        var filesChanged = artifact?.FilesChanged
            ?? files?.Select(f => f.Path).ToArray()
            ?? Array.Empty<string>();
        var commitSha = headCommitSha ?? string.Empty;
        var prNumber = artifact?.PrNumber ?? pr?.Number;
        var prUrl = pr?.Url;

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
            // The compare-range commit count did not survive the platform
            // abstraction (no base...head compare verb); 0 = "not computed".
            CommitsCount = 0,
            // Check-run reads are platform-specific and not abstracted; null =
            // "unknown" (the same value a still-pending check produced before).
            ChecksPassed = null,
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
        IGitPlatformActionsClient actions, Guid? tenantId, string owner, string repo, long runId, CancellationToken ct)
    {
        var runIdText = runId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var listRes = await actions.ListRunArtifactsAsync(owner, repo, runIdText, ct).ConfigureAwait(false);
        if (listRes is not PlatformResult<IReadOnlyList<PModels.Artifact>>.Ok listOk)
        {
            if (listRes is PlatformResult<IReadOnlyList<PModels.Artifact>>.Failed f
                && PlatformErrorText.IsCapabilityUnsupported(f.Error))
            {
                await EmitSkippedAsync(tenantId, $"{owner}/{repo}", runId, "result_artifact",
                    "artifact listing is capability_unsupported on this platform", ct).ConfigureAwait(false);
            }
            else
            {
                _logger?.LogInformation("Artifact listing failed for run {RunId}; falling back to git state", runId);
            }
            return null;
        }

        var result = listOk.Value.FirstOrDefault(a =>
            string.Equals(a.Name, ResultArtifactName, StringComparison.OrdinalIgnoreCase));
        // SizeBytes 0 + empty URL encodes "expired" in the platform model.
        if (result is null || (result.SizeBytes == 0 && string.IsNullOrEmpty(result.DownloadUrl)))
        {
            _logger?.LogInformation(
                "Result artifact not available for run {RunId} (count={Count})",
                runId, listOk.Value.Count);
            return null;
        }

        var downloadRes = await actions.DownloadArtifactAsync(owner, repo, result.Id, ct).ConfigureAwait(false);
        if (downloadRes is not PlatformResult<Stream>.Ok streamOk)
        {
            if (downloadRes is PlatformResult<Stream>.Failed df
                && PlatformErrorText.IsCapabilityUnsupported(df.Error))
            {
                await EmitSkippedAsync(tenantId, $"{owner}/{repo}", runId, "result_artifact",
                    "artifact download is capability_unsupported on this platform", ct).ConfigureAwait(false);
            }
            return null;
        }

        byte[] zipBytes;
        await using (var stream = streamOk.Value)
        using (var buffer = new MemoryStream())
        {
            try
            {
                await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("artifact_too_large"))
            {
                _logger?.LogWarning(
                    "Result artifact for run {RunId} exceeded the driver byte cap; skipping", runId);
                return null;
            }
            zipBytes = buffer.ToArray();
        }
        if (zipBytes.Length == 0) return null;

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

    private sealed record PrProjection(int Number, string Url);

    private async Task<PrProjection?> TryFindPullRequestAsync(
        IGitPlatformClient client, Guid? tenantId, string owner, string repo, long runId, string branch, CancellationToken ct)
    {
        try
        {
            var res = await client.ListOpenPullRequestsForBranchAsync(
                owner, repo, branch, DefaultBaseBranch, ct).ConfigureAwait(false);
            if (res is PlatformResult<IReadOnlyList<PModels.PullRequest>>.Ok ok)
            {
                var pr = ok.Value
                    .Select(p => new PrProjection(int.TryParse(p.Number, out var n) ? n : 0, p.HtmlUrl))
                    .OrderByDescending(p => p.Number)
                    .FirstOrDefault();
                return pr;
            }
            if (res is PlatformResult<IReadOnlyList<PModels.PullRequest>>.Failed f
                && PlatformErrorText.IsCapabilityUnsupported(f.Error))
            {
                await EmitSkippedAsync(tenantId, $"{owner}/{repo}", runId, "pr_lookup",
                    "PR-for-branch lookup is capability_unsupported on this platform", ct).ConfigureAwait(false);
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "PR lookup failed for {Owner}/{Repo} head={Branch}", owner, repo, branch);
            return null;
        }
    }

    private async Task<IReadOnlyList<PModels.PrFile>?> TryListFileChangesAsync(
        IGitPlatformClient client, Guid? tenantId, string owner, string repo, long runId, string branch, CancellationToken ct)
    {
        try
        {
            var res = await client.ListBranchFileChangesAsync(
                new PModels.ListBranchFileChangesRequest(owner, repo, branch), ct).ConfigureAwait(false);
            if (res is PlatformResult<IReadOnlyList<PModels.PrFile>>.Ok ok) return ok.Value;
            if (res is PlatformResult<IReadOnlyList<PModels.PrFile>>.Failed f
                && PlatformErrorText.IsCapabilityUnsupported(f.Error))
            {
                await EmitSkippedAsync(tenantId, $"{owner}/{repo}", runId, "file_changes",
                    "branch file-change reads are capability_unsupported on this platform", ct).ConfigureAwait(false);
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "File-change read failed for {Owner}/{Repo}@{Branch}", owner, repo, branch);
            return null;
        }
    }

    private async Task<string?> TryReadHeadShaAsync(
        IGitPlatformClient client, string owner, string repo, string branch, CancellationToken ct)
    {
        try
        {
            var res = await client.GetBranchAsync(owner, repo, branch, ct).ConfigureAwait(false);
            return res is PlatformResult<PModels.Branch>.Ok ok ? ok.Value.Sha : null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Head-SHA read failed for {Owner}/{Repo}@{Branch}", owner, repo, branch);
            return null;
        }
    }

    private async Task EmitSkippedAsync(
        Guid? tenantId, string repo, long runId, string step, string detail, CancellationToken ct)
    {
        _ = ct;
        try
        {
            await _events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = CollectStepSkippedEventType,
                TenantId = tenantId,
                Tags = JsonSerializer.Serialize(new
                {
                    tenantId = tenantId?.ToString(),
                    repo,
                    runId = runId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    step,
                    failureCode = PlatformErrorText.CapabilityUnsupportedCode,
                }),
                Metadata = JsonSerializer.Serialize(new { workflowVersion = "1.0.0", eventSource = "system" }),
                Data = JsonSerializer.Serialize(new { detail }),
                CreatedAt = DateTime.UtcNow,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "AGENT_DISPATCH.COLLECT_STEP.SKIPPED event append failed (step={Step}); the aggregation still returns", step);
        }
    }

    private static bool DetermineSuccess(string conclusion, AgentResultArtifact? artifact)
    {
        if (artifact is not null) return artifact.Success;
        return string.Equals(conclusion, "success", StringComparison.OrdinalIgnoreCase);
    }
}
