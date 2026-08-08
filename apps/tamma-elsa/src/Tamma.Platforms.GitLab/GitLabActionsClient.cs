using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.GitLab.Dtos;

namespace Tamma.Platforms.GitLab;

/// <summary>
/// Story 31-6 — <see cref="IGitPlatformActionsClient"/> against GitLab
/// pipelines.
///
/// <para>GitLab CI is materially different from GitHub Actions:</para>
/// <list type="bullet">
///   <item>No <c>workflow_dispatch</c>. Pipelines are triggered via
///         <c>POST /projects/:id/pipeline</c> with a <c>ref</c> and
///         <c>variables</c>. <c>WorkflowDispatchRequest.WorkflowFileName</c>
///         is therefore ignored — GitLab dispatches the project's
///         <c>.gitlab-ci.yml</c> regardless.</item>
///   <item>Pipelines are the unit of dispatch (analogous to a GitHub
///         workflow run); each pipeline has a list of jobs (analogous
///         to GitHub jobs).</item>
///   <item>Artifacts are per-job. The driver implements the abstraction
///         with two artifact id shapes: <c>"job:NNN"</c> for direct
///         job artifact downloads and a bare numeric <c>NNN</c> as
///         backwards-compat (treated as a job id). Caller obtains the
///         id from <see cref="ListRunJobsAsync"/>.</item>
/// </list>
///
/// <para>4MB cap mirrors the GitHub driver's
/// <c>OctokitGitHubActionsClient.DefaultMaxArtifactBytes</c> — same
/// reasoning (avoid OOM-via-CI-artifact attack surface).</para>
/// </summary>
internal sealed class GitLabActionsClient : IGitPlatformActionsClient
{
    /// <summary>4 MB hard cap on downloaded artifact bytes.</summary>
    public const long DefaultMaxArtifactBytes = 4L * 1024 * 1024;

    private readonly GitLabHttpClient _http;
    private readonly ILogger<GitLabActionsClient> _logger;
    private readonly long _maxArtifactBytes;

    public GitLabActionsClient(
        GitLabHttpClient http,
        ILogger<GitLabActionsClient> logger,
        long maxArtifactBytes = DefaultMaxArtifactBytes)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);
        if (maxArtifactBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxArtifactBytes));
        _http = http;
        _logger = logger;
        _maxArtifactBytes = maxArtifactBytes;
    }

    public async Task<PlatformResult<WorkflowRun>> DispatchWorkflowAsync(
        string owner, string repoName,
        WorkflowDispatchRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pid = GitLabPlatformClient.EncodeProjectRef(owner, repoName);

        // Merge inputs into variables — GitLab pipelines don't have a
        // typed "inputs" field on the create-pipeline endpoint (only
        // pipeline schedules carry them). All inputs become variables.
        var variables = new List<object>();
        foreach (var kv in request.Inputs)
        {
            variables.Add(new { key = kv.Key, value = kv.Value });
        }
        if (request.Variables is not null)
        {
            foreach (var kv in request.Variables)
            {
                variables.Add(new { key = kv.Key, value = kv.Value });
            }
        }
        var body = new
        {
            @ref = request.Ref,
            variables,
        };

        try
        {
            var (resp, pipeline) = await _http.PostJsonAsync<object, GitLabPipeline>(
                $"projects/{pid}/pipeline", body, ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.Response.IsSuccessStatusCode)
                {
                    return PlatformResult<WorkflowRun>.FromError(
                        GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
                }
                if (pipeline is null)
                {
                    return PlatformResult<WorkflowRun>.FromError(new PlatformError.Unknown("empty body"));
                }
                return PlatformResult<WorkflowRun>.FromOk(MapPipeline(pipeline));
            }
        }
        catch (HttpRequestException)
        {
            return PlatformResult<WorkflowRun>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    public async Task<PlatformResult<WorkflowRun>> GetRunStatusAsync(
        string owner, string repoName, string runId, CancellationToken ct = default)
    {
        var pid = GitLabPlatformClient.EncodeProjectRef(owner, repoName);
        try
        {
            var (resp, pipeline) = await _http.GetJsonAsync<GitLabPipeline>(
                $"projects/{pid}/pipelines/{runId}", ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.Response.IsSuccessStatusCode)
                {
                    return PlatformResult<WorkflowRun>.FromError(
                        GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
                }
                if (pipeline is null)
                {
                    return PlatformResult<WorkflowRun>.FromError(new PlatformError.NotFound());
                }
                return PlatformResult<WorkflowRun>.FromOk(MapPipeline(pipeline));
            }
        }
        catch (HttpRequestException)
        {
            return PlatformResult<WorkflowRun>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    public async Task<PlatformResult<IReadOnlyList<WorkflowRun>>> ListRunsAsync(
        string owner, string repoName,
        ListWorkflowRunsRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pid = GitLabPlatformClient.EncodeProjectRef(owner, repoName);
        var perPage = Math.Clamp(request.PerPage, 1, 100);

        // GitLab's list-pipelines endpoint is already newest-first
        // (order_by=id desc default). The listed pipeline records are a
        // slim shape (no source / finished_at on some versions) —
        // MapPipeline tolerates the nulls.
        var path = $"projects/{pid}/pipelines?per_page={perPage}";
        if (!string.IsNullOrWhiteSpace(request.Branch))
        {
            path += $"&ref={Uri.EscapeDataString(request.Branch)}";
        }

        try
        {
            var (resp, pipelines) = await _http.GetJsonAsync<List<GitLabPipeline>>(
                path, ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.Response.IsSuccessStatusCode)
                {
                    return PlatformResult<IReadOnlyList<WorkflowRun>>.FromError(
                        GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
                }
                IReadOnlyList<WorkflowRun> runs = (pipelines ?? [])
                    .Select(MapPipeline)
                    .ToList();
                return PlatformResult<IReadOnlyList<WorkflowRun>>.FromOk(runs);
            }
        }
        catch (HttpRequestException)
        {
            return PlatformResult<IReadOnlyList<WorkflowRun>>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    public async Task<PlatformResult<IReadOnlyList<WorkflowJob>>> ListRunJobsAsync(
        string owner, string repoName, string runId, CancellationToken ct = default)
    {
        var pid = GitLabPlatformClient.EncodeProjectRef(owner, repoName);
        var jobs = new List<WorkflowJob>();
        try
        {
            await foreach (var dto in _http.EnumeratePagesAsync<GitLabJob>(
                $"projects/{pid}/pipelines/{runId}/jobs", ct: ct).ConfigureAwait(false))
            {
                jobs.Add(MapJob(dto));
            }
            return PlatformResult<IReadOnlyList<WorkflowJob>>.FromOk(jobs);
        }
        catch (GitLabRequestException ex)
        {
            return PlatformResult<IReadOnlyList<WorkflowJob>>.FromError(
                GitLabErrorMapper.Map(ex.Status, ex.Body, ex.RetryAfter));
        }
        catch (HttpRequestException)
        {
            return PlatformResult<IReadOnlyList<WorkflowJob>>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    public async Task<PlatformResult<Stream>> DownloadArtifactAsync(
        string owner, string repoName, string artifactId, CancellationToken ct = default)
    {
        // Artifact id encoding: caller obtained it from
        // ListRunJobsAsync / WorkflowJob.JobId. We strip an optional
        // "job:" prefix for forward-compat with future encodings.
        var jobId = artifactId.StartsWith("job:", StringComparison.Ordinal)
            ? artifactId[4..]
            : artifactId;

        var pid = GitLabPlatformClient.EncodeProjectRef(owner, repoName);
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                _http.BuildUri($"projects/{pid}/jobs/{jobId}/artifacts"));
            // SendStreamingAsync because we don't want to buffer the
            // whole zip into memory before the cap check.
            var response = await _http.SendStreamingAsync(req, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Snapshot status + retry-after BEFORE Dispose — accessing
                // disposed HttpResponseMessage members is contract-undefined
                // (CA2000). Body must also be read before dispose.
                var body = response.Content is not null
                    ? await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)
                    : null;
                var status = response.StatusCode;
                var retryAfter = ParseRetryAfter(response);
                response.Dispose();
                return PlatformResult<Stream>.FromError(
                    GitLabErrorMapper.Map(status, body, retryAfter));
            }

            // Pre-flight: if Content-Length is set and exceeds the
            // cap, fail fast.
            var contentLength = response.Content?.Headers?.ContentLength;
            if (contentLength.HasValue && contentLength.Value > _maxArtifactBytes)
            {
                response.Dispose();
                return PlatformResult<Stream>.FromError(
                    new PlatformError.InvalidRequest(
                        "artifact_too_large",
                        $"artifact size {contentLength.Value} exceeds cap {_maxArtifactBytes}"));
            }

            var raw = await response.Content!.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return PlatformResult<Stream>.FromOk(new LimitedStream(raw, response, _maxArtifactBytes));
        }
        catch (HttpRequestException)
        {
            return PlatformResult<Stream>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    public async Task<PlatformResult<bool>> CancelRunAsync(
        string owner, string repoName, string runId, CancellationToken ct = default)
    {
        var pid = GitLabPlatformClient.EncodeProjectRef(owner, repoName);
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Post, _http.BuildUri($"projects/{pid}/pipelines/{runId}/cancel"));
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.Response.IsSuccessStatusCode)
            {
                return PlatformResult<bool>.FromOk(true);
            }
            // GitLab returns 403 on already-finished pipelines — treat
            // as no-op success per the abstraction's contract.
            if (resp.Response.StatusCode == HttpStatusCode.Forbidden)
            {
                return PlatformResult<bool>.FromOk(true);
            }
            return PlatformResult<bool>.FromError(
                GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
        }
        catch (HttpRequestException)
        {
            return PlatformResult<bool>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    private static WorkflowRun MapPipeline(GitLabPipeline pipeline)
    {
        // Conclusion lifecycle: GitLab uses a single "status" field —
        // "success" / "failed" / "canceled" / "skipped" are terminal.
        // We surface the same string in both Status and Conclusion when
        // terminal; null Conclusion while running.
        var terminalStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "success", "failed", "canceled", "cancelled", "skipped", "manual",
        };
        var status = pipeline.Status ?? "unknown";
        var conclusion = terminalStatuses.Contains(status) ? status : null;

        // Pipeline source goes into RawMetadata so callers can attribute
        // pipelines triggered by web/api/schedule/etc. Raw JSON text —
        // callers parse on demand (record can't own a JsonDocument
        // without leaking pooled buffers).
        string? metadata = !string.IsNullOrEmpty(pipeline.Source)
            ? JsonSerializer.Serialize(new { source = pipeline.Source })
            : null;

        return new WorkflowRun(
            RunId: pipeline.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Status: status,
            Conclusion: conclusion,
            HtmlUrl: pipeline.WebUrl ?? string.Empty,
            StartedAt: pipeline.CreatedAt,
            CompletedAt: pipeline.FinishedAt,
            RawMetadata: metadata);
    }

    private static WorkflowJob MapJob(GitLabJob job)
    {
        var terminalStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "success", "failed", "canceled", "cancelled", "skipped", "manual",
        };
        var status = job.Status ?? "unknown";
        var conclusion = terminalStatuses.Contains(status) ? status : null;

        // Encode artifact bearer with the "job:" prefix so callers can
        // pass directly to DownloadArtifactAsync. Raw JSON text — see
        // WorkflowRun comment about no-JsonDocument-in-records.
        string? metadata = job.ArtifactsFile is not null
            ? JsonSerializer.Serialize(new
            {
                stage = job.Stage,
                artifact_filename = job.ArtifactsFile.Filename,
                artifact_size = job.ArtifactsFile.Size,
                artifact_id = $"job:{job.Id}",
            })
            : null;

        return new WorkflowJob(
            JobId: job.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Name: job.Name ?? string.Empty,
            Status: status,
            Conclusion: conclusion,
            RawMetadata: metadata);
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta.HasValue) return ra.Delta;
        if (ra.Date.HasValue)
        {
            var delta = ra.Date.Value - DateTimeOffset.UtcNow;
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }
        return null;
    }
}

/// <summary>
/// Wrapping stream that enforces a max byte count and disposes the
/// underlying response when closed. Mirrors the cap implementation in
/// the GitHub driver.
/// </summary>
internal sealed class LimitedStream : Stream
{
    private readonly Stream _inner;
    private readonly HttpResponseMessage _response;
    private readonly long _maxBytes;
    private long _bytesRead;

    public LimitedStream(Stream inner, HttpResponseMessage response, long maxBytes)
    {
        _inner = inner;
        _response = response;
        _maxBytes = maxBytes;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => _bytesRead;
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        EnforceCap();
        var allowed = (int)Math.Min(count, _maxBytes - _bytesRead + 1);
        var n = _inner.Read(buffer, offset, allowed);
        _bytesRead += n;
        if (_bytesRead > _maxBytes)
        {
            throw new InvalidOperationException(
                $"GitLab artifact exceeds cap of {_maxBytes} bytes");
        }
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        EnforceCap();
        var allowed = (int)Math.Min(buffer.Length, _maxBytes - _bytesRead + 1);
        var n = await _inner.ReadAsync(buffer[..allowed], ct).ConfigureAwait(false);
        _bytesRead += n;
        if (_bytesRead > _maxBytes)
        {
            throw new InvalidOperationException(
                $"GitLab artifact exceeds cap of {_maxBytes} bytes");
        }
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void EnforceCap()
    {
        if (_bytesRead > _maxBytes)
        {
            throw new InvalidOperationException(
                $"GitLab artifact exceeds cap of {_maxBytes} bytes");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _response.Dispose();
        }
        base.Dispose(disposing);
    }
}
