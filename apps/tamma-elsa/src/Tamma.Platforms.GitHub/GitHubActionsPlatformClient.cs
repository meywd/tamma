using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Activities.AgentDispatch;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Platforms.GitHub;

/// <summary>
/// Story 31-3 — <see cref="IGitPlatformActionsClient"/> backed by the
/// existing Octokit-based <see cref="IGitHubActionsClient"/> living in
/// <c>Tamma.Activities</c>. This is a pure adapter — no Octokit calls
/// happen here; we delegate to the inner client and project its
/// summary records into the platform-neutral
/// <see cref="Tamma.Platforms.Abstractions.Models"/> shapes via
/// <see cref="GitHubModelMapper"/>.
///
/// <para>Error model: the inner <see cref="IGitHubActionsClient"/>
/// already returns null / empty / NotConfigured for failures, so we
/// translate those to <see cref="PlatformResult{T}.ServiceUnavailable"/>
/// (NotConfigured / null) or <see cref="PlatformResult{T}.Ok"/>
/// (success). Future stories (31-9, 31-10) can promote richer error
/// surfaces — today the seam mirrors the existing behaviour 1:1 so
/// no callers regress.</para>
/// </summary>
public sealed class GitHubActionsPlatformClient : IGitPlatformActionsClient
{
    private readonly IGitHubActionsClient _inner;
    private readonly ILogger _logger;

    /// <summary>
    /// Construct a new actions client wrapping
    /// <paramref name="inner"/>. <paramref name="inner"/> handles all
    /// Octokit / installation-token / rate-limit concerns; this class
    /// only translates request + result shapes.
    /// </summary>
    public GitHubActionsPlatformClient(
        IGitHubActionsClient inner,
        ILogger<GitHubActionsPlatformClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _logger = logger ?? NullLogger<GitHubActionsPlatformClient>.Instance;
    }

    /// <inheritdoc />
    public async Task<PlatformResult<WorkflowRun>> DispatchWorkflowAsync(
        string owner, string repoName,
        WorkflowDispatchRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.WorkflowFileName))
        {
            return PlatformResult<WorkflowRun>.FromError(
                new PlatformError.InvalidRequest(
                    "workflow_file_required",
                    "GitHub workflow_dispatch requires a workflow file name."));
        }

        var dispatchResult = await _inner.DispatchWorkflowAsync(
            owner, repoName, request.WorkflowFileName, request.Ref,
            request.Inputs, ct).ConfigureAwait(false);

        if (dispatchResult.NotConfigured)
        {
            return PlatformResult<WorkflowRun>.FromServiceUnavailable();
        }
        if (dispatchResult.HttpStatusCode != 204)
        {
            return TranslateDispatchError(dispatchResult);
        }

        // GitHub does not return the run on dispatch — the inner client's
        // ListWorkflowRunsAsync is the canonical "give callers a run id"
        // path used elsewhere in the codebase. We don't speculate on a
        // run here; callers using IGitPlatformActionsClient receive a
        // synthetic placeholder so they can still poll status by run id
        // they obtain via ListRunJobs / GetRunStatus paths.
        var placeholder = new WorkflowRun(
            RunId: string.Empty,
            Status: "queued",
            Conclusion: null,
            HtmlUrl: string.Empty,
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: null,
            RawMetadata: null);
        return PlatformResult<WorkflowRun>.FromOk(placeholder);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<WorkflowRun>> GetRunStatusAsync(
        string owner, string repoName, string runId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!long.TryParse(runId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRunId))
        {
            return PlatformResult<WorkflowRun>.FromError(
                new PlatformError.InvalidRequest("invalid_run_id", $"GitHub run id must be a positive integer; got '{runId}'."));
        }
        var run = await _inner.GetWorkflowRunAsync(owner, repoName, parsedRunId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return PlatformResult<WorkflowRun>.FromError(new PlatformError.NotFound());
        }
        return PlatformResult<WorkflowRun>.FromOk(GitHubModelMapper.ToWorkflowRun(run));
    }

    /// <inheritdoc />
    public Task<PlatformResult<IReadOnlyList<WorkflowJob>>> ListRunJobsAsync(
        string owner, string repoName, string runId,
        CancellationToken ct = default)
    {
        // The current Tamma.Activities seam doesn't expose per-job
        // listings; the agent-dispatch loop uses run-level status
        // only. Surface this as ServiceUnavailable so callers fall
        // back to GetRunStatus until a future story extends
        // IGitHubActionsClient with ListJobs.
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _logger.LogDebug(
            "ListRunJobsAsync not yet wired through the abstraction for {Owner}/{Repo} run={RunId}; returning ServiceUnavailable",
            owner, repoName, runId);
        return Task.FromResult(PlatformResult<IReadOnlyList<WorkflowJob>>.FromServiceUnavailable());
    }

    /// <inheritdoc />
    public async Task<PlatformResult<Stream>> DownloadArtifactAsync(
        string owner, string repoName, string artifactId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        if (!long.TryParse(artifactId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return PlatformResult<Stream>.FromError(
                new PlatformError.InvalidRequest("invalid_artifact_id",
                    $"GitHub artifact id must be a positive integer; got '{artifactId}'."));
        }

        var bytes = await _inner.DownloadArtifactZipAsync(owner, repoName, parsed, ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return PlatformResult<Stream>.FromError(new PlatformError.NotFound());
        }
        // The inner client already enforces the 4 MB cap and returns
        // the zip bytes in memory. Wrap in a non-disposable buffer
        // stream — caller owns it.
        return PlatformResult<Stream>.FromOk(new MemoryStream(bytes, writable: false));
    }

    /// <inheritdoc />
    public Task<PlatformResult<bool>> CancelRunAsync(
        string owner, string repoName, string runId,
        CancellationToken ct = default)
    {
        // Cancel is not exposed on the existing IGitHubActionsClient
        // surface. Callers that need it today reach into Octokit
        // directly; the seam will gain it when an agent-dispatch path
        // requires programmatic cancel. Returning ServiceUnavailable
        // keeps the abstraction honest.
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _logger.LogDebug(
            "CancelRunAsync not yet wired through the abstraction for {Owner}/{Repo} run={RunId}; returning ServiceUnavailable",
            owner, repoName, runId);
        return Task.FromResult(PlatformResult<bool>.FromServiceUnavailable());
    }

    private static PlatformResult<WorkflowRun> TranslateDispatchError(DispatchApiResult result)
    {
        // The inner client returns a numeric HTTP status + a stable
        // reason string. Map the well-known shapes to the right
        // PlatformError variant.
        return result.HttpStatusCode switch
        {
            401 => PlatformResult<WorkflowRun>.FromError(new PlatformError.AuthExpired()),
            403 => PlatformResult<WorkflowRun>.FromError(new PlatformError.PermissionDenied()),
            404 => PlatformResult<WorkflowRun>.FromError(new PlatformError.NotFound()),
            429 => PlatformResult<WorkflowRun>.FromError(new PlatformError.RateLimited(RetryAfter: null)),
            >= 500 and <= 599 => PlatformResult<WorkflowRun>.FromError(new PlatformError.ServiceUnavailable()),
            _ => PlatformResult<WorkflowRun>.FromError(
                new PlatformError.InvalidRequest(
                    Code: result.HttpStatusCode.ToString(CultureInfo.InvariantCulture),
                    Hint: result.ErrorReason)),
        };
    }
}
