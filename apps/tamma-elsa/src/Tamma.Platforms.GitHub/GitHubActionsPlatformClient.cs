using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.GitHub.Dtos;

namespace Tamma.Platforms.GitHub;

/// <summary>
/// Epic 31 P1 stage 2 — <see cref="IGitPlatformActionsClient"/>
/// implemented DIRECTLY against the GitHub Actions REST API over
/// <see cref="GitHubHttpClient"/>. This replaces the old adapter over
/// <c>Tamma.Api</c>'s <c>IGitHubActionsClient</c> seam — whose real
/// implementation only existed when the process-level GitHub App was
/// configured (the App-only conditional the execution plan bans), and
/// which lacked ListRunJobs + CancelRun entirely.
///
/// <para><b>Dispatch run correlation (at-least-once).</b> GitHub's
/// <c>workflow_dispatch</c> answers 204 with no run reference. After
/// the 204 this client re-fetches the runs of the SAME workflow file on
/// the SAME ref (<c>event=workflow_dispatch</c>, created≥dispatch
/// time) and returns the newest match, so callers receive a POLLABLE
/// <see cref="WorkflowRun.RunId"/> — never an empty placeholder.
/// Correlation is heuristic: with concurrent dispatches of the same
/// workflow+ref the returned run may belong to a sibling dispatch
/// (at-least-once semantics — the run IS a real dispatched run of that
/// workflow+ref, just possibly not "ours"). When no run is visible
/// after the probe window the client returns a typed
/// <see cref="PlatformError.Unknown"/> (<c>dispatch accepted…</c>)
/// rather than fabricating an unpollable placeholder.</para>
///
/// <para>4 MB artifact cap mirrors the absorbed
/// <c>OctokitGitHubActionsClient.DefaultMaxArtifactBytes</c> posture
/// (compromised-agent OOM defense).</para>
/// </summary>
public sealed class GitHubActionsPlatformClient : IGitPlatformActionsClient
{
    /// <summary>Default max artifact size (4 MB) — the absorbed cap.</summary>
    internal const long DefaultMaxArtifactBytes = 4L * 1024 * 1024;

    /// <summary>How many times dispatch re-fetches the run list before
    /// giving up on correlation.</summary>
    internal const int DefaultDispatchProbeAttempts = 5;

    private static readonly TimeSpan DefaultDispatchProbeDelay = TimeSpan.FromSeconds(2);

    private readonly GitHubHttpClient _http;
    private readonly ILogger _logger;
    private readonly long _maxArtifactBytes;
    private readonly int _dispatchProbeAttempts;
    private readonly TimeSpan _dispatchProbeDelay;
    private readonly TimeProvider _time;

    internal GitHubActionsPlatformClient(
        GitHubHttpClient http,
        ILogger? logger = null,
        long maxArtifactBytes = DefaultMaxArtifactBytes,
        int dispatchProbeAttempts = DefaultDispatchProbeAttempts,
        TimeSpan? dispatchProbeDelay = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (maxArtifactBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxArtifactBytes));
        if (dispatchProbeAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(dispatchProbeAttempts));
        _http = http;
        _logger = logger ?? NullLogger.Instance;
        _maxArtifactBytes = maxArtifactBytes;
        _dispatchProbeAttempts = dispatchProbeAttempts;
        _dispatchProbeDelay = dispatchProbeDelay ?? DefaultDispatchProbeDelay;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Effective artifact byte cap (test helper).</summary>
    internal long MaxArtifactBytes => _maxArtifactBytes;

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

        var dispatchedAt = _time.GetUtcNow();
        var dispatchPath = $"/repos/{Encode(owner)}/{Encode(repoName)}" +
                           $"/actions/workflows/{Encode(request.WorkflowFileName)}/dispatches";
        var dispatchBody = new
        {
            @ref = request.Ref,
            inputs = request.Inputs,
        };
        var dispatchResult = await _http
            .SendNoContentAsync(HttpMethod.Post, dispatchPath, dispatchBody, ct)
            .ConfigureAwait(false);
        switch (dispatchResult)
        {
            case PlatformResult<bool>.Failed failed:
                return PlatformResult<WorkflowRun>.FromError(failed.Error);
            case PlatformResult<bool>.ServiceUnavailable:
                return PlatformResult<WorkflowRun>.FromServiceUnavailable();
        }

        // Correlate: list this workflow's runs on the dispatched ref,
        // newest first, created at-or-after the dispatch instant
        // (60s clock-skew allowance). See type-level remarks for the
        // at-least-once semantics.
        var createdFloor = dispatchedAt.AddSeconds(-60);
        var listPath = $"/repos/{Encode(owner)}/{Encode(repoName)}" +
                       $"/actions/workflows/{Encode(request.WorkflowFileName)}/runs" +
                       $"?branch={Encode(request.Ref)}&event=workflow_dispatch&per_page=5" +
                       $"&created={Encode(">=" + createdFloor.UtcDateTime.ToString("O"))}";

        for (var attempt = 1; attempt <= _dispatchProbeAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (attempt > 1 && _dispatchProbeDelay > TimeSpan.Zero)
            {
                await Task.Delay(_dispatchProbeDelay, ct).ConfigureAwait(false);
            }

            var runsResult = await _http
                .GetJsonAsync<GitHubWorkflowRunsListDto>(listPath, ct)
                .ConfigureAwait(false);
            if (runsResult is PlatformResult<GitHubWorkflowRunsListDto>.Ok ok
                && ok.Value.WorkflowRuns is { Count: > 0 } runs)
            {
                var newest = runs
                    .Where(r => r.Id > 0)
                    .OrderByDescending(r => r.CreatedAt)
                    .FirstOrDefault();
                if (newest is not null)
                {
                    return PlatformResult<WorkflowRun>.FromOk(MapRun(newest));
                }
            }
            // A transient listing failure is not fatal — the dispatch
            // already succeeded; keep probing until attempts run out.
        }

        _logger.LogWarning(
            "GitHub workflow_dispatch for {Owner}/{Repo} {Workflow}@{Ref} was accepted (204) " +
            "but no run appeared within {Attempts} probes",
            owner, repoName, request.WorkflowFileName, request.Ref, _dispatchProbeAttempts);
        return PlatformResult<WorkflowRun>.FromError(new PlatformError.Unknown(
            "dispatch accepted (204) but the created run could not be correlated; " +
            "the run may still start — list workflow runs to find it"));
    }

    /// <inheritdoc />
    public async Task<PlatformResult<WorkflowRun>> GetRunStatusAsync(
        string owner, string repoName, string runId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!long.TryParse(runId, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return PlatformResult<WorkflowRun>.FromError(
                new PlatformError.InvalidRequest(
                    "invalid_run_id",
                    $"GitHub run id must be a positive integer; got '{runId}'."));
        }

        var path = $"/repos/{Encode(owner)}/{Encode(repoName)}/actions/runs/{Encode(runId)}";
        var result = await _http.GetJsonAsync<GitHubWorkflowRunDto>(path, ct).ConfigureAwait(false);
        return result.Map(MapRun);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<IReadOnlyList<WorkflowRun>>> ListRunsAsync(
        string owner, string repoName,
        ListWorkflowRunsRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentNullException.ThrowIfNull(request);

        var perPage = Math.Clamp(request.PerPage, 1, 100);
        var path = $"/repos/{Encode(owner)}/{Encode(repoName)}/actions/runs?per_page={perPage}";
        if (!string.IsNullOrWhiteSpace(request.Branch))
        {
            path += $"&branch={Encode(request.Branch)}";
        }

        var result = await _http
            .GetJsonAsync<GitHubWorkflowRunsListDto>(path, ct)
            .ConfigureAwait(false);
        return result.Map(list =>
        {
            IReadOnlyList<WorkflowRun> runs = (list.WorkflowRuns ?? [])
                .Where(r => r.Id > 0)
                .OrderByDescending(r => r.CreatedAt)
                .Select(MapRun)
                .ToList();
            return runs;
        });
    }

    /// <inheritdoc />
    public async Task<PlatformResult<IReadOnlyList<WorkflowJob>>> ListRunJobsAsync(
        string owner, string repoName, string runId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var path = $"/repos/{Encode(owner)}/{Encode(repoName)}" +
                   $"/actions/runs/{Encode(runId)}/jobs?per_page=100";
        var result = await _http.GetJsonAsync<GitHubJobsListDto>(path, ct).ConfigureAwait(false);
        return result.Map(list =>
        {
            IReadOnlyList<WorkflowJob> jobs = (list.Jobs ?? [])
                .Select(j => new WorkflowJob(
                    JobId: j.Id.ToString(CultureInfo.InvariantCulture),
                    Name: j.Name ?? string.Empty,
                    Status: j.Status ?? "unknown",
                    Conclusion: j.Conclusion,
                    RawMetadata: null))
                .ToList();
            return jobs;
        });
    }

    /// <inheritdoc />
    public async Task<PlatformResult<IReadOnlyList<Artifact>>> ListRunArtifactsAsync(
        string owner, string repoName, string runId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var path = $"/repos/{Encode(owner)}/{Encode(repoName)}" +
                   $"/actions/runs/{Encode(runId)}/artifacts?per_page=100";
        var result = await _http.GetJsonAsync<GitHubArtifactsListDto>(path, ct).ConfigureAwait(false);
        return result.Map(list =>
        {
            IReadOnlyList<Artifact> artifacts = (list.Artifacts ?? [])
                .Select(a => new Artifact(
                    Id: a.Id.ToString(CultureInfo.InvariantCulture),
                    Name: a.Name ?? string.Empty,
                    // The abstraction encodes "expired" as SizeBytes 0 (the
                    // record's documented "may be 0 if expired"); callers
                    // that need the flag read SizeBytes==0 || empty URL.
                    SizeBytes: a.Expired ? 0 : a.SizeInBytes,
                    DownloadUrl: a.Expired ? string.Empty : a.ArchiveDownloadUrl ?? string.Empty))
                .ToList();
            return artifacts;
        });
    }

    /// <inheritdoc />
    public async Task<PlatformResult<Stream>> DownloadArtifactAsync(
        string owner, string repoName, string artifactId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        if (!long.TryParse(artifactId, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return PlatformResult<Stream>.FromError(
                new PlatformError.InvalidRequest(
                    "invalid_artifact_id",
                    $"GitHub artifact id must be a positive integer; got '{artifactId}'."));
        }

        var path = $"/repos/{Encode(owner)}/{Encode(repoName)}" +
                   $"/actions/artifacts/{Encode(artifactId)}/zip";
        var streamResult = await _http.GetStreamAsync(path, ct).ConfigureAwait(false);
        return streamResult switch
        {
            PlatformResult<Stream>.Ok ok =>
                PlatformResult<Stream>.FromOk(new BoundedReadStream(ok.Value, _maxArtifactBytes)),
            PlatformResult<Stream>.Failed failed =>
                PlatformResult<Stream>.FromError(failed.Error),
            PlatformResult<Stream>.ServiceUnavailable =>
                PlatformResult<Stream>.FromServiceUnavailable(),
            _ => throw new InvalidOperationException("unhandled stream result"),
        };
    }

    /// <inheritdoc />
    public async Task<PlatformResult<bool>> CancelRunAsync(
        string owner, string repoName, string runId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var path = $"/repos/{Encode(owner)}/{Encode(repoName)}" +
                   $"/actions/runs/{Encode(runId)}/cancel";
        var result = await _http
            .SendNoContentAsync(HttpMethod.Post, path, body: null, ct)
            .ConfigureAwait(false);
        // GitHub answers 409 when the run already completed — a no-op
        // cancel is success per the abstraction contract.
        if (result is PlatformResult<bool>.Failed failed
            && failed.Error is PlatformError.InvalidRequest { Code: "conflict" or "merge_conflict" })
        {
            return PlatformResult<bool>.FromOk(true);
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────

    internal static WorkflowRun MapRun(GitHubWorkflowRunDto dto) => new(
        RunId: dto.Id.ToString(CultureInfo.InvariantCulture),
        Status: dto.Status ?? "unknown",
        Conclusion: string.IsNullOrEmpty(dto.Conclusion) ? null : dto.Conclusion,
        HtmlUrl: dto.HtmlUrl ?? string.Empty,
        StartedAt: dto.RunStartedAt ?? dto.CreatedAt,
        CompletedAt: string.IsNullOrEmpty(dto.Conclusion) ? null : dto.UpdatedAt,
        RawMetadata: null);

    private static string Encode(string s) => Uri.EscapeDataString(s);

    /// <summary>
    /// Read-only stream enforcing the artifact byte cap — throws
    /// <see cref="InvalidOperationException"/> with message
    /// <see cref="TooLargeMessage"/> once the cap is crossed (partial
    /// zip data is useless; fail loud, never truncate silently). The
    /// Gitea / GitLab drivers carry the same shape.
    /// </summary>
    internal sealed class BoundedReadStream : Stream
    {
        /// <summary>Sentinel for "artifact_too_large" detection upstream.</summary>
        public const string TooLargeMessage = "artifact_too_large";

        private readonly Stream _inner;
        private readonly long _max;
        private long _read;

        public BoundedReadStream(Stream inner, long max)
        {
            _inner = inner;
            _max = max;
        }

        public long BytesRead => _read;
        public long MaxBytes => _max;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            EnforceBudget();
            var n = _inner.Read(buffer, offset, (int)Math.Min(count, _max - _read + 1));
            return Account(n);
        }

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken ct)
        {
            EnforceBudget();
            var n = await _inner
                .ReadAsync(buffer.AsMemory(offset, (int)Math.Min(count, _max - _read + 1)), ct)
                .ConfigureAwait(false);
            return Account(n);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            EnforceBudget();
            var slice = buffer.Length > _max - _read + 1
                ? buffer[..(int)Math.Min(buffer.Length, _max - _read + 1)]
                : buffer;
            var n = await _inner.ReadAsync(slice, ct).ConfigureAwait(false);
            return Account(n);
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        private void EnforceBudget()
        {
            if (_read > _max)
            {
                throw new InvalidOperationException(TooLargeMessage);
            }
        }

        private int Account(int n)
        {
            if (n > 0)
            {
                _read += n;
                if (_read > _max)
                {
                    throw new InvalidOperationException(TooLargeMessage);
                }
            }
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _inner.Dispose(); } catch { /* best effort */ }
            }
            base.Dispose(disposing);
        }
    }
}
