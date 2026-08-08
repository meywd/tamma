using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.Gitea.Dtos;

namespace Tamma.Platforms.Gitea;

/// <summary>
/// Gitea Actions client (REST v1, available from Gitea 1.21+). Maps
/// 1:1 to GitHub Actions URL shapes — Gitea intentionally cloned the
/// GitHub Actions surface, so the brief calls this driver "thin".
///
/// <para>Plan §6 endpoints:</para>
/// <list type="bullet">
///   <item><c>POST /api/v1/repos/{o}/{r}/actions/workflows/{file}/dispatches</c></item>
///   <item><c>GET /api/v1/repos/{o}/{r}/actions/runs/{id}</c></item>
///   <item><c>GET /api/v1/repos/{o}/{r}/actions/runs/{id}/jobs</c></item>
///   <item><c>GET /api/v1/repos/{o}/{r}/actions/artifacts/{id}/zip</c></item>
///   <item><c>POST /api/v1/repos/{o}/{r}/actions/runs/{id}/cancel</c></item>
/// </list>
///
/// <para>4 MB artifact-cap pattern preserved from
/// <c>OctokitGitHubActionsClient</c> review-finding 6 — overridable via
/// <c>Agent:MaxArtifactBytes</c>. Setting to 0 / negative reverts to
/// the default; we do not allow unbounded.</para>
/// </summary>
public sealed class GiteaActionsPlatformClient : IGitPlatformActionsClient
{
    /// <summary>Default max artifact size (4 MB).</summary>
    internal const long DefaultMaxArtifactBytes = 4L * 1024 * 1024;
    private const string MaxArtifactBytesConfigKey = "Agent:MaxArtifactBytes";

    private readonly GiteaHttpClient _http;
    private readonly ILogger _logger;
    private readonly long _maxArtifactBytes;

    internal GiteaActionsPlatformClient(
        GiteaHttpClient http,
        ILogger? logger = null,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _logger = logger ?? NullLogger.Instance;
        _maxArtifactBytes = ResolveMaxArtifactBytes(configuration);
    }

    /// <summary>Effective artifact byte cap (test helper).</summary>
    internal long MaxArtifactBytes => _maxArtifactBytes;

    private static long ResolveMaxArtifactBytes(IConfiguration? configuration)
    {
        if (configuration is null) return DefaultMaxArtifactBytes;
        var raw = configuration[MaxArtifactBytesConfigKey];
        if (long.TryParse(raw, out var parsed) && parsed > 0)
        {
            return parsed;
        }
        return DefaultMaxArtifactBytes;
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
                    "missing_workflow_file_name",
                    "Gitea Actions dispatch requires WorkflowDispatchRequest.WorkflowFileName"));
        }

        var path = $"/api/v1/repos/{Encode(owner)}/{Encode(repoName)}" +
                   $"/actions/workflows/{Encode(request.WorkflowFileName)}/dispatches";
        var body = new
        {
            @ref = request.Ref,
            inputs = request.Inputs,
        };
        var dispatchResult = await _http.PostNoContentAsync(path, body, ct).ConfigureAwait(false);
        if (dispatchResult is not PlatformResult<bool>.Ok)
        {
            return dispatchResult switch
            {
                PlatformResult<bool>.Failed failed =>
                    PlatformResult<WorkflowRun>.FromError(failed.Error),
                PlatformResult<bool>.ServiceUnavailable =>
                    PlatformResult<WorkflowRun>.FromServiceUnavailable(),
                _ => throw new InvalidOperationException("unhandled dispatch result"),
            };
        }

        // Gitea (like GitHub) doesn't return the run id from dispatch.
        // Re-fetch the most recent run on the requested ref + workflow
        // so callers get a usable run id.
        var listPath = $"/api/v1/repos/{Encode(owner)}/{Encode(repoName)}" +
                       $"/actions/runs?event=workflow_dispatch&branch={Encode(request.Ref)}&page=1&limit=1";
        var runsResult = await _http
            .GetJsonAsync<GiteaWorkflowRunsListDto>(listPath, ct)
            .ConfigureAwait(false);
        if (runsResult is PlatformResult<GiteaWorkflowRunsListDto>.Ok runsOk
            && runsOk.Value.WorkflowRuns is { Count: > 0 } runs)
        {
            return PlatformResult<WorkflowRun>.FromOk(MapRun(runs[0]));
        }
        // Dispatch succeeded but we couldn't see a run yet — return a
        // synthetic placeholder so callers can poll later.
        return PlatformResult<WorkflowRun>.FromOk(new WorkflowRun(
            RunId: "0",
            Status: "queued",
            Conclusion: null,
            HtmlUrl: string.Empty,
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: null,
            RawMetadata: null));
    }

    /// <inheritdoc />
    public async Task<PlatformResult<WorkflowRun>> GetRunStatusAsync(
        string owner, string repoName, string runId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var path = $"/api/v1/repos/{Encode(owner)}/{Encode(repoName)}/actions/runs/{Encode(runId)}";
        var result = await _http
            .GetJsonAsync<GiteaWorkflowRunDto>(path, ct)
            .ConfigureAwait(false);
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

        var limit = Math.Clamp(request.PerPage, 1, 50);
        var path = $"/api/v1/repos/{Encode(owner)}/{Encode(repoName)}" +
                   $"/actions/runs?page=1&limit={limit}";
        if (!string.IsNullOrWhiteSpace(request.Branch))
        {
            path += $"&branch={Encode(request.Branch)}";
        }

        var result = await _http
            .GetJsonAsync<GiteaWorkflowRunsListDto>(path, ct)
            .ConfigureAwait(false);
        return result.Map(list =>
        {
            IReadOnlyList<WorkflowRun> runs = (list.WorkflowRuns ?? new List<GiteaWorkflowRunDto>())
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

        var path = $"/api/v1/repos/{Encode(owner)}/{Encode(repoName)}" +
                   $"/actions/runs/{Encode(runId)}/jobs";
        var result = await _http
            .GetJsonAsync<GiteaJobsListDto>(path, ct)
            .ConfigureAwait(false);
        return result.Map(list =>
        {
            IReadOnlyList<WorkflowJob> jobs = (list.Jobs ?? new List<GiteaJobDto>())
                .Select(j => new WorkflowJob(
                    JobId: j.Id.ToString(),
                    Name: j.Name ?? string.Empty,
                    Status: j.Status ?? "unknown",
                    Conclusion: j.Conclusion,
                    RawMetadata: null))
                .ToList();
            return jobs;
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

        var path = $"/api/v1/repos/{Encode(owner)}/{Encode(repoName)}" +
                   $"/actions/artifacts/{Encode(artifactId)}/zip";
        var streamResult = await _http.GetStreamAsync(path, ct).ConfigureAwait(false);
        return streamResult switch
        {
            PlatformResult<Stream>.Ok ok =>
                PlatformResult<Stream>.FromOk(
                    new BoundedReadStream(ok.Value, _maxArtifactBytes)),
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

        var path = $"/api/v1/repos/{Encode(owner)}/{Encode(repoName)}" +
                   $"/actions/runs/{Encode(runId)}/cancel";
        return await _http.PostNoContentAsync(path, body: null, ct).ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────

    private static WorkflowRun MapRun(GiteaWorkflowRunDto dto)
    {
        var started = dto.StartedAt ?? dto.CreatedAt ?? DateTimeOffset.UtcNow;
        return new WorkflowRun(
            RunId: dto.Id.ToString(),
            Status: dto.Status ?? "unknown",
            Conclusion: dto.Conclusion,
            HtmlUrl: dto.HtmlUrl ?? string.Empty,
            StartedAt: started,
            CompletedAt: dto.CompletedAt,
            RawMetadata: null);
    }

    private static string Encode(string s) => Uri.EscapeDataString(s);

    /// <summary>
    /// Read-only stream that throws <see cref="InvalidOperationException"/>
    /// once the configured byte cap is exceeded — translates to
    /// <see cref="PlatformError.InvalidRequest"/> with code
    /// <c>"artifact_too_large"</c> at the call site (callers wrap reads
    /// per the platform abstraction contract).
    ///
    /// <para>We expose this enforcement as a thrown exception rather
    /// than truncation because partial-zip data is useless to the
    /// caller — better to fail loudly than silently corrupt.</para>
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
            EnforceBudget(count);
            var n = _inner.Read(buffer, offset, (int)Math.Min(count, _max - _read + 1));
            return Account(n);
        }

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken ct)
        {
            EnforceBudget(count);
            var n = await _inner
                .ReadAsync(buffer.AsMemory(offset, (int)Math.Min(count, _max - _read + 1)), ct)
                .ConfigureAwait(false);
            return Account(n);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken ct = default)
        {
            EnforceBudget(buffer.Length);
            var slice = buffer.Length > _max - _read + 1
                ? buffer[..(int)Math.Min(buffer.Length, _max - _read + 1)]
                : buffer;
            var n = await _inner.ReadAsync(slice, ct).ConfigureAwait(false);
            return Account(n);
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        private void EnforceBudget(int count)
        {
            // We only throw once we've actually read past the cap;
            // peeking the request size before each read would be too
            // strict (caller may pass a large buffer that'll only
            // partially fill).
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
