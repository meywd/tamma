using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.GitLab.Dtos;
using Tamma.Platforms.GitLab.Mapping;

namespace Tamma.Platforms.GitLab;

/// <summary>
/// Story 31-6 — <see cref="IGitPlatformClient"/> implementation against
/// GitLab REST API v4.
///
/// <para>GitLab quirks vs GitHub:</para>
/// <list type="bullet">
///   <item><b>Project addressing</b>: GitLab uses a numeric project id OR
///         the URL-encoded <c>group/subgroup/project</c> path. The driver
///         takes <c>(owner, repoName)</c> from the abstraction, joins them
///         with <c>/</c>, and URL-encodes — so nested-group projects
///         (<c>group/subgroup/project</c>) work by passing
///         <c>owner = "group/subgroup"</c>.</item>
///   <item><b>MR vs PR</b>: GitLab's "Merge Request" maps to the neutral
///         "PullRequest" record. The mapper at
///         <see cref="MrToPullRequestMapper"/> handles state + draft.</item>
///   <item><b>Branch creation</b>: separate API call before opening an MR.</item>
///   <item><b>Idempotent OpenPR</b>: the driver detects an existing MR
///         on the same (source, target) branch pair and returns it
///         rather than creating a duplicate.</item>
/// </list>
/// </summary>
internal sealed class GitLabPlatformClient : IGitPlatformClient
{
    private readonly GitLabHttpClient _http;
    private readonly ILogger<GitLabPlatformClient> _logger;

    /// <summary>Epic 31 P6 M1 — true when the factory-detected version is at
    /// or above <see cref="GitLabPlatformDriver.MinimumPrLifecycleVersion"/>.
    /// Kept in lock-step with <see cref="GitLabPlatformDriver.ComputeCapabilities"/>
    /// (pinned by the capability contract test): below the floor — or when
    /// the version probe failed — the six lifecycle verbs answer the typed
    /// <c>capability_unsupported</c> refusal without touching the network.</summary>
    private readonly bool _prLifecycleLive;

    /// <summary>Epic 31 P6 — bounded wait for a fresh MR's <c>diff_refs</c>
    /// to populate before the review-comment position falls back to the
    /// caller's single SHA (observed ~2s async population on 16.11).
    /// Internal-settable so unit tests exercising the fallback don't pay the
    /// full wait.</summary>
    internal int DiffRefsRetryAttempts { get; set; } = 5;

    /// <inheritdoc cref="DiffRefsRetryAttempts"/>
    internal TimeSpan DiffRefsRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    public GitLabPlatformClient(
        GitLabHttpClient http,
        ILogger<GitLabPlatformClient> logger,
        Version? detectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);
        _http = http;
        _logger = logger;
        _prLifecycleLive = GitLabPlatformDriver.SupportsPrLifecycle(detectedVersion);
    }

    /// <summary>
    /// URL-encode <c>owner/repoName</c> for use in
    /// <c>/projects/:id</c>. Slashes become <c>%2F</c>.
    /// </summary>
    internal static string EncodeProjectRef(string owner, string repoName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        return Uri.EscapeDataString($"{owner}/{repoName}");
    }

    public async Task<PlatformResult<Repo>> GetRepoAsync(string owner, string repoName, CancellationToken ct = default)
    {
        var pid = EncodeProjectRef(owner, repoName);
        try
        {
            var (resp, project) = await _http.GetJsonAsync<GitLabProject>($"projects/{pid}", ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.Response.IsSuccessStatusCode)
                {
                    return PlatformResult<Repo>.FromError(
                        GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
                }
                if (project is null)
                {
                    return PlatformResult<Repo>.FromError(new PlatformError.Unknown("empty body"));
                }
                return PlatformResult<Repo>.FromOk(MapRepo(project));
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "GitLab GetRepo network error for {Owner}/{Repo}", owner, repoName);
            return PlatformResult<Repo>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    public async Task<PlatformResult<IReadOnlyList<Branch>>> ListRepoBranchesAsync(
        string owner, string repoName, CancellationToken ct = default)
    {
        var pid = EncodeProjectRef(owner, repoName);
        var branches = new List<Branch>();
        try
        {
            await foreach (var dto in _http.EnumeratePagesAsync<GitLabBranchDto>(
                $"projects/{pid}/repository/branches", ct: ct).ConfigureAwait(false))
            {
                branches.Add(new Branch(
                    Name: dto.Name ?? string.Empty,
                    Sha: dto.Commit?.Id ?? string.Empty,
                    Protected: dto.Protected));
            }
            return PlatformResult<IReadOnlyList<Branch>>.FromOk(branches);
        }
        catch (GitLabRequestException ex)
        {
            return PlatformResult<IReadOnlyList<Branch>>.FromError(
                GitLabErrorMapper.Map(ex.Status, ex.Body, ex.RetryAfter));
        }
        catch (HttpRequestException)
        {
            return PlatformResult<IReadOnlyList<Branch>>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    public async Task<PlatformResult<byte[]>> GetFileContentAsync(GetFileContentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pid = EncodeProjectRef(request.Owner, request.RepoName);
        var encodedPath = Uri.EscapeDataString(request.Path);
        try
        {
            var (resp, file) = await _http.GetJsonAsync<GitLabFile>(
                $"projects/{pid}/repository/files/{encodedPath}?ref={Uri.EscapeDataString(request.Ref)}",
                ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.Response.IsSuccessStatusCode)
                {
                    return PlatformResult<byte[]>.FromError(
                        GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
                }
                if (file is null || file.Content is null)
                {
                    return PlatformResult<byte[]>.FromError(new PlatformError.NotFound());
                }
                // GitLab returns base64 by default (encoding="base64").
                if (string.Equals(file.Encoding, "base64", StringComparison.OrdinalIgnoreCase))
                {
                    return PlatformResult<byte[]>.FromOk(Convert.FromBase64String(file.Content));
                }
                return PlatformResult<byte[]>.FromOk(System.Text.Encoding.UTF8.GetBytes(file.Content));
            }
        }
        catch (HttpRequestException)
        {
            return PlatformResult<byte[]>.FromError(new PlatformError.ServiceUnavailable());
        }
        catch (FormatException ex)
        {
            return PlatformResult<byte[]>.FromError(
                new PlatformError.InvalidRequest("decode_failed", ex.Message));
        }
    }

    public async Task<PlatformResult<Branch>> CreateBranchAsync(CreateBranchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pid = EncodeProjectRef(request.Owner, request.RepoName);
        var path = $"projects/{pid}/repository/branches" +
                   $"?branch={Uri.EscapeDataString(request.NewBranchName)}" +
                   $"&ref={Uri.EscapeDataString(request.FromSha)}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _http.BuildUri(path));
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.Response.IsSuccessStatusCode)
            {
                return PlatformResult<Branch>.FromError(
                    GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
            }
            var dto = string.IsNullOrEmpty(resp.Body)
                ? null
                : JsonSerializer.Deserialize<GitLabBranchDto>(resp.Body, GitLabHttpClient.JsonDefaults);
            if (dto is null)
            {
                return PlatformResult<Branch>.FromError(new PlatformError.Unknown("empty body"));
            }
            return PlatformResult<Branch>.FromOk(new Branch(
                Name: dto.Name ?? request.NewBranchName,
                Sha: dto.Commit?.Id ?? request.FromSha,
                Protected: dto.Protected));
        }
        catch (HttpRequestException)
        {
            return PlatformResult<Branch>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    /// <inheritdoc />
    public async Task<PlatformResult<Branch>> GetBranchAsync(
        string owner, string repoName, string branchName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        var pid = EncodeProjectRef(owner, repoName);
        try
        {
            var (resp, dto) = await _http.GetJsonAsync<GitLabBranchDto>(
                $"projects/{pid}/repository/branches/{Uri.EscapeDataString(branchName)}", ct)
                .ConfigureAwait(false);
            using (resp)
            {
                if (!resp.Response.IsSuccessStatusCode)
                {
                    return PlatformResult<Branch>.FromError(
                        GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
                }
                if (dto is null)
                {
                    return PlatformResult<Branch>.FromError(new PlatformError.NotFound());
                }
                return PlatformResult<Branch>.FromOk(new Branch(
                    Name: dto.Name ?? branchName,
                    Sha: dto.Commit?.Id ?? string.Empty,
                    Protected: dto.Protected));
            }
        }
        catch (HttpRequestException)
        {
            return PlatformResult<Branch>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    /// <inheritdoc />
    public async Task<PlatformResult<bool>> DeleteBranchAsync(
        string owner, string repoName, string branchName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        var pid = EncodeProjectRef(owner, repoName);
        try
        {
            using var resp = await _http.DeleteAsync(
                $"projects/{pid}/repository/branches/{Uri.EscapeDataString(branchName)}", ct)
                .ConfigureAwait(false);
            if (!resp.Response.IsSuccessStatusCode)
            {
                return PlatformResult<bool>.FromError(
                    GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
            }
            return PlatformResult<bool>.FromOk(true);
        }
        catch (HttpRequestException)
        {
            return PlatformResult<bool>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    /// <inheritdoc />
    public async Task<PlatformResult<IReadOnlyList<PullRequest>>> ListOpenPullRequestsForBranchAsync(
        string owner, string repoName, string sourceBranch, string targetBranch,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBranch);
        var pid = EncodeProjectRef(owner, repoName);
        var path = $"projects/{pid}/merge_requests" +
                   $"?state=opened" +
                   $"&source_branch={Uri.EscapeDataString(sourceBranch)}" +
                   $"&target_branch={Uri.EscapeDataString(targetBranch)}";
        try
        {
            var results = new List<PullRequest>();
            await foreach (var mr in _http.EnumeratePagesAsync<GitLabMergeRequest>(path, ct: ct).ConfigureAwait(false))
            {
                results.Add(MrToPullRequestMapper.Map(mr));
            }
            return PlatformResult<IReadOnlyList<PullRequest>>.FromOk(results);
        }
        catch (GitLabRequestException ex)
        {
            return PlatformResult<IReadOnlyList<PullRequest>>.FromError(
                GitLabErrorMapper.Map(ex.Status, ex.Body, ex.RetryAfter));
        }
        catch (HttpRequestException)
        {
            return PlatformResult<IReadOnlyList<PullRequest>>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    /// <inheritdoc />
    public async Task<PlatformResult<PullRequest>> UpdatePullRequestAsync(
        UpdatePullRequestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new Dictionary<string, object?>();
        if (request.Title is not null) body["title"] = request.Title;
        if (request.Body is not null) body["description"] = request.Body;

        return await PutMergeRequestAsync(
            request.Owner, request.RepoName, request.PrNumber, body, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared PUT against the update-MR endpoint
    /// (<c>PUT /projects/:id/merge_requests/:iid</c>) mapping the response
    /// through <see cref="MrToPullRequestMapper"/>. All lifecycle verbs and
    /// <see cref="UpdatePullRequestAsync"/> funnel through here.
    /// </summary>
    private async Task<PlatformResult<PullRequest>> PutMergeRequestAsync(
        string owner, string repoName, string prNumber, object body, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(prNumber);
        var pid = EncodeProjectRef(owner, repoName);
        try
        {
            var (resp, mr) = await _http.PutJsonAsync<object, GitLabMergeRequest>(
                $"projects/{pid}/merge_requests/{prNumber}", body, ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.Response.IsSuccessStatusCode)
                {
                    return PlatformResult<PullRequest>.FromError(
                        GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
                }
                if (mr is null)
                {
                    return PlatformResult<PullRequest>.FromError(new PlatformError.Unknown("empty body"));
                }
                return PlatformResult<PullRequest>.FromOk(MrToPullRequestMapper.Map(mr));
            }
        }
        catch (HttpRequestException)
        {
            return PlatformResult<PullRequest>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    public async Task<PlatformResult<PullRequest>> OpenPullRequestAsync(
        OpenPullRequestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pid = EncodeProjectRef(request.Owner, request.RepoName);

        // Idempotency: GitLab doesn't reject duplicates with a stable
        // code, so we proactively look up an existing MR for the same
        // (source, target) pair before creating a new one.
        var existing = await FindExistingMergeRequestAsync(
            pid, request.SourceBranch, request.TargetBranch, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return PlatformResult<PullRequest>.FromOk(MrToPullRequestMapper.Map(existing));
        }

        var body = new
        {
            source_branch = request.SourceBranch,
            target_branch = request.TargetBranch,
            title = request.IsDraft ? GitLabDraftTitle.AddDraftPrefix(request.Title) : request.Title,
            description = request.Body,
        };
        try
        {
            var (resp, mr) = await _http.PostJsonAsync<object, GitLabMergeRequest>(
                $"projects/{pid}/merge_requests", body, ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.Response.IsSuccessStatusCode)
                {
                    return PlatformResult<PullRequest>.FromError(
                        GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
                }
                if (mr is null)
                {
                    return PlatformResult<PullRequest>.FromError(new PlatformError.Unknown("empty body"));
                }
                return PlatformResult<PullRequest>.FromOk(MrToPullRequestMapper.Map(mr));
            }
        }
        catch (HttpRequestException)
        {
            return PlatformResult<PullRequest>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    public async Task<PlatformResult<PullRequest>> GetPullRequestAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default)
    {
        var pid = EncodeProjectRef(owner, repoName);
        try
        {
            var (resp, mr) = await _http.GetJsonAsync<GitLabMergeRequest>(
                $"projects/{pid}/merge_requests/{prNumber}", ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.Response.IsSuccessStatusCode)
                {
                    return PlatformResult<PullRequest>.FromError(
                        GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
                }
                if (mr is null)
                {
                    return PlatformResult<PullRequest>.FromError(new PlatformError.NotFound());
                }
                return PlatformResult<PullRequest>.FromOk(MrToPullRequestMapper.Map(mr));
            }
        }
        catch (HttpRequestException)
        {
            return PlatformResult<PullRequest>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    public async Task<PlatformResult<IReadOnlyList<PrFile>>> ListPullRequestFilesAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default)
    {
        var pid = EncodeProjectRef(owner, repoName);
        try
        {
            var (resp, changes) = await _http.GetJsonAsync<GitLabMrChanges>(
                $"projects/{pid}/merge_requests/{prNumber}/changes", ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.Response.IsSuccessStatusCode)
                {
                    return PlatformResult<IReadOnlyList<PrFile>>.FromError(
                        GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
                }
                var list = new List<PrFile>();
                if (changes?.Changes is not null)
                {
                    foreach (var change in changes.Changes)
                    {
                        var (add, del) = MrToPullRequestMapper.CountDiffLines(change.Diff);
                        list.Add(new PrFile(
                            Path: change.NewPath ?? change.OldPath ?? string.Empty,
                            Status: MrToPullRequestMapper.MapFileStatus(change),
                            Additions: add,
                            Deletions: del));
                    }
                }
                return PlatformResult<IReadOnlyList<PrFile>>.FromOk(list);
            }
        }
        catch (HttpRequestException)
        {
            return PlatformResult<IReadOnlyList<PrFile>>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    public async Task<PlatformResult<IssueComment>> CreatePullRequestReviewCommentAsync(
        CreatePullRequestReviewCommentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pid = EncodeProjectRef(request.Owner, request.RepoName);

        // Epic 31 P6 hardening — a discussion position needs the MR's REAL
        // base/start/head SHAs. The old base=start=head=CommitSha shape only
        // holds for a single-commit MR whose head IS that commit; on a
        // multi-commit MR the live platform rejects it (observed: 500 on
        // 16.11; the line-code validation 400 on other versions). Fetch the
        // MR's diff_refs (single-MR GET) and use them. diff_refs is "empty
        // when the merge request is created, populates asynchronously" (per
        // the API doc; observed ~2s on 16.11), so the driver retries the read
        // briefly before falling back to the caller's CommitSha — the
        // fallback keeps a comment raced against a brand-new MR best-effort
        // instead of failing the fetch outright.
        var baseSha = request.CommitSha;
        var startSha = request.CommitSha;
        var headSha = request.CommitSha;
        for (var attempt = 0; attempt < DiffRefsRetryAttempts; attempt++)
        {
            try
            {
                var (mrResp, mr) = await _http.GetJsonAsync<GitLabMergeRequest>(
                    $"projects/{pid}/merge_requests/{request.PrNumber}", ct).ConfigureAwait(false);
                using (mrResp)
                {
                    if (!mrResp.Response.IsSuccessStatusCode)
                    {
                        break; // let the discussion POST surface the honest failure
                    }
                    if (mr?.DiffRefs is { BaseSha: not null, HeadSha: not null } refs)
                    {
                        baseSha = refs.BaseSha;
                        startSha = refs.StartSha ?? refs.BaseSha;
                        headSha = refs.HeadSha;
                        break;
                    }
                }
            }
            catch (HttpRequestException)
            {
                // Positioning lookup is best-effort; the discussion POST below
                // surfaces the honest failure if the platform is really down.
                break;
            }

            if (attempt + 1 < DiffRefsRetryAttempts)
            {
                await Task.Delay(DiffRefsRetryDelay, ct).ConfigureAwait(false);
            }
        }

        var body = new
        {
            body = request.Body,
            position = new
            {
                base_sha = baseSha,
                start_sha = startSha,
                head_sha = headSha,
                position_type = "text",
                new_path = request.Path,
                old_path = request.Path,
                new_line = request.Line,
            },
        };
        try
        {
            var (resp, discussion) = await _http.PostJsonAsync<object, GitLabDiscussion>(
                $"projects/{pid}/merge_requests/{request.PrNumber}/discussions", body, ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.Response.IsSuccessStatusCode)
                {
                    return PlatformResult<IssueComment>.FromError(
                        GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
                }
                var note = discussion?.Notes?.FirstOrDefault();
                if (note is null)
                {
                    return PlatformResult<IssueComment>.FromError(new PlatformError.Unknown("empty discussion notes"));
                }
                return PlatformResult<IssueComment>.FromOk(new IssueComment(
                    Id: note.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Body: note.Body ?? string.Empty,
                    AuthorLogin: note.Author?.Username ?? string.Empty,
                    CreatedAt: note.CreatedAt));
            }
        }
        catch (HttpRequestException)
        {
            return PlatformResult<IssueComment>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    public async Task<PlatformResult<PullRequest>> MergePullRequestAsync(
        MergePullRequestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pid = EncodeProjectRef(request.Owner, request.RepoName);

        // GitLab supports merge / squash. Rebase before merge requires a
        // separate /rebase call; squash flag is on the merge call itself.
        // We translate the neutral MergeMethod here.
        if (request.Method == MergeMethod.Rebase)
        {
            return PlatformResult<PullRequest>.FromError(
                new PlatformError.InvalidRequest(
                    "merge_method_unsupported",
                    "GitLab does not support rebase as a single merge call; rebase before merge separately"));
        }
        var body = new
        {
            squash = request.Method == MergeMethod.Squash,
            merge_commit_message = request.CommitMessage,
            squash_commit_message = request.Method == MergeMethod.Squash ? request.CommitMessage : null,
        };
        try
        {
            var (resp, mr) = await _http.PutJsonAsync<object, GitLabMergeRequest>(
                $"projects/{pid}/merge_requests/{request.PrNumber}/merge", body, ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.Response.IsSuccessStatusCode)
                {
                    return PlatformResult<PullRequest>.FromError(
                        GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
                }
                if (mr is null)
                {
                    return PlatformResult<PullRequest>.FromError(new PlatformError.Unknown("empty body"));
                }
                return PlatformResult<PullRequest>.FromOk(MrToPullRequestMapper.Map(mr));
            }
        }
        catch (HttpRequestException)
        {
            return PlatformResult<PullRequest>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    // ================================================================
    // Story 31-13 verbs, made REAL in Epic 31 P6 M1. Below the version
    // floor (GitLabPlatformDriver.MinimumPrLifecycleVersion, incl. a
    // failed probe) the verbs answer the typed capability_unsupported
    // refusal without touching the network — in lock-step with the
    // driver's ComputeCapabilities (pinned by the capability contract
    // test).
    // ================================================================

    private static Task<PlatformResult<PullRequest>> PrLifecycleUnsupported() =>
        Task.FromResult(PlatformResult<PullRequest>.FromError(
            new PlatformError.InvalidRequest("capability_unsupported",
                "this GitLab instance is below the PR-lifecycle version floor "
                + $"({GitLabPlatformDriver.MinimumPrLifecycleVersion}) or its version could not be detected")));

    /// <inheritdoc />
    public Task<PlatformResult<PullRequest>> ClosePullRequestAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default) =>
        _prLifecycleLive
            ? PutMergeRequestAsync(owner, repoName, prNumber, new { state_event = "close" }, ct)
            : PrLifecycleUnsupported();

    /// <inheritdoc />
    public Task<PlatformResult<PullRequest>> ReopenPullRequestAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default) =>
        _prLifecycleLive
            ? PutMergeRequestAsync(owner, repoName, prNumber, new { state_event = "reopen" }, ct)
            : PrLifecycleUnsupported();

    /// <inheritdoc />
    public async Task<PlatformResult<PullRequest>> RequestReviewersAsync(
        RequestReviewersRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_prLifecycleLive) return await PrLifecycleUnsupported().ConfigureAwait(false);

        // GitLab takes numeric reviewer_ids, not usernames — the username→id
        // resolver lives HERE, inside the driver (DG-3 owner decision). An
        // unresolvable username answers the typed `reviewer_unresolvable`
        // InvalidRequest, which mediation's DG-3 alternative step classifies
        // into skip-with-label. Team reviewers have no GitLab equivalent on
        // this endpoint (group review assignment is a Premium feature on a
        // different surface) and are ignored.
        var ids = new List<long>();
        var unresolved = new List<string>();
        foreach (var username in request.Reviewers
                     .Where(r => !string.IsNullOrWhiteSpace(r))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var resolved = await TryResolveUserIdAsync(username, ct).ConfigureAwait(false);
            if (resolved is not PlatformResult<long?>.Ok ok)
            {
                return resolved switch
                {
                    PlatformResult<long?>.Failed failed =>
                        PlatformResult<PullRequest>.FromError(failed.Error),
                    _ => PlatformResult<PullRequest>.FromServiceUnavailable(),
                };
            }
            if (ok.Value is { } id) ids.Add(id);
            else unresolved.Add(username);
        }

        if (unresolved.Count > 0)
        {
            return PlatformResult<PullRequest>.FromError(
                new PlatformError.InvalidRequest(
                    "reviewer_unresolvable",
                    $"username(s) not resolvable on this GitLab instance: {string.Join(", ", unresolved)}"));
        }
        if (ids.Count == 0)
        {
            return PlatformResult<PullRequest>.FromError(
                new PlatformError.InvalidRequest(
                    "reviewer_unresolvable", "no resolvable reviewers were supplied"));
        }

        return await PutMergeRequestAsync(
            request.Owner, request.RepoName, request.PrNumber,
            new { reviewer_ids = ids }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<PullRequest>> AddPullRequestLabelsAsync(
        AddPullRequestLabelsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_prLifecycleLive) return await PrLifecycleUnsupported().ConfigureAwait(false);

        // `add_labels` is comma-separated label NAMES and — per the API doc —
        // auto-creates missing project labels (GitHub-parity for free: the
        // loop's tamma-* labels work on a fresh project). Caveat recorded:
        // a label name containing a comma cannot be expressed through this
        // wire format (GitLab would split it); such names are rejected here
        // rather than silently split into two labels.
        var names = request.Labels
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0)
        {
            return await GetPullRequestAsync(
                request.Owner, request.RepoName, request.PrNumber, ct).ConfigureAwait(false);
        }
        var withComma = names.Where(n => n.Contains(',', StringComparison.Ordinal)).ToList();
        if (withComma.Count > 0)
        {
            return PlatformResult<PullRequest>.FromError(
                new PlatformError.InvalidRequest(
                    "label_name_unsupported",
                    "GitLab's comma-separated add_labels wire format cannot express label "
                    + $"name(s): {string.Join(" | ", withComma)}"));
        }

        return await PutMergeRequestAsync(
            request.Owner, request.RepoName, request.PrNumber,
            new { add_labels = string.Join(",", names) }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<PullRequest>> RemovePullRequestLabelAsync(
        string owner, string repoName, string prNumber, string label, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (!_prLifecycleLive) return await PrLifecycleUnsupported().ConfigureAwait(false);

        // `remove_labels` is idempotent on GitLab's side — removing a label
        // the MR doesn't carry (or that doesn't exist) succeeds and returns
        // the MR unchanged, matching the live GitHub path's posture.
        if (label.Contains(',', StringComparison.Ordinal))
        {
            return PlatformResult<PullRequest>.FromError(
                new PlatformError.InvalidRequest(
                    "label_name_unsupported",
                    $"GitLab's comma-separated remove_labels wire format cannot express label name: {label}"));
        }

        return await PutMergeRequestAsync(
            owner, repoName, prNumber, new { remove_labels = label }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<PullRequest>> SetDraftAsync(
        SetPullRequestDraftRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_prLifecycleLive) return await PrLifecycleUnsupported().ConfigureAwait(false);

        // Research (P6 M1, 2026-08-09): the update-MR API has NO draft field —
        // draft is the title prefix ("Draft: " since 13.2; legacy WIP forms
        // readable until 14.8, see GitLabDraftTitle). So the toggle is a
        // title edit. Idempotent: a PR already in the requested state is
        // returned unchanged (no PUT).
        var pid = EncodeProjectRef(request.Owner, request.RepoName);
        GitLabMergeRequest? dto;
        try
        {
            var (resp, mr) = await _http.GetJsonAsync<GitLabMergeRequest>(
                $"projects/{pid}/merge_requests/{request.PrNumber}", ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.Response.IsSuccessStatusCode)
                {
                    return PlatformResult<PullRequest>.FromError(
                        GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
                }
                if (mr is null)
                {
                    return PlatformResult<PullRequest>.FromError(new PlatformError.NotFound());
                }
                dto = mr;
            }
        }
        catch (HttpRequestException)
        {
            return PlatformResult<PullRequest>.FromError(new PlatformError.ServiceUnavailable());
        }

        var title = dto.Title ?? string.Empty;
        var isDraftNow = dto.Draft || dto.WorkInProgress || GitLabDraftTitle.HasDraftPrefix(title);
        if (isDraftNow == request.Draft)
        {
            return PlatformResult<PullRequest>.FromOk(MrToPullRequestMapper.Map(dto));
        }

        var newTitle = request.Draft
            ? GitLabDraftTitle.AddDraftPrefix(title)
            : GitLabDraftTitle.StripDraftPrefix(title);
        return await PutMergeRequestAsync(
            request.Owner, request.RepoName, request.PrNumber,
            new { title = newTitle }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Username→id lookup via <c>GET /users?username=</c> (exact-match
    /// filter per the users API). Ok(null) = the username does not exist;
    /// Failed = the lookup itself was rejected (auth, rate limit, …).
    /// </summary>
    private async Task<PlatformResult<long?>> TryResolveUserIdAsync(
        string username, CancellationToken ct)
    {
        try
        {
            var (resp, users) = await _http.GetJsonAsync<List<GitLabUser>>(
                $"users?username={Uri.EscapeDataString(username)}", ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.Response.IsSuccessStatusCode)
                {
                    return PlatformResult<long?>.FromError(
                        GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
                }
                var match = users?.FirstOrDefault(u =>
                    string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
                return PlatformResult<long?>.FromOk(match?.Id);
            }
        }
        catch (HttpRequestException)
        {
            return PlatformResult<long?>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    // Epic 31 P1 (stage 1) — the loop verbs (issue lifecycle, releases,
    // review-comment listing, commit reads). GitLab's API supports all of them,
    // but the driver does not implement them yet, records that via the absent
    // IssueLifecycle / Releases / PrReviewCommentRead / CommitReads capability
    // flags, and answers with typed capability_unsupported per the interface
    // no-throw contract. P6 scoped the GitLab driver to the six PR lifecycle
    // verbs + review-comment/pipeline hardening (per the execution plan);
    // the loop verbs stay typed-unsupported and are a recorded post-epic
    // follow-up (the DG-mediated degradation paths keep cycles alive).
    private static Task<PlatformResult<T>> LoopVerbUnsupported<T>(string capability) =>
        Task.FromResult(PlatformResult<T>.FromError(
            new PlatformError.InvalidRequest("capability_unsupported",
                $"the GitLab driver does not implement {capability} verbs (post-Epic-31 follow-up)")));

    public Task<PlatformResult<Issue>> CloseIssueAsync(
        string owner, string repoName, string issueNumber, string? comment = null,
        CancellationToken ct = default) =>
        LoopVerbUnsupported<Issue>("issue lifecycle");

    public Task<PlatformResult<IReadOnlyList<string>>> AddIssueLabelsAsync(
        AddIssueLabelsRequest request, CancellationToken ct = default) =>
        LoopVerbUnsupported<IReadOnlyList<string>>("issue lifecycle");

    public Task<PlatformResult<IReadOnlyList<string>>> RemoveIssueLabelAsync(
        string owner, string repoName, string issueNumber, string label,
        CancellationToken ct = default) =>
        LoopVerbUnsupported<IReadOnlyList<string>>("issue lifecycle");

    public Task<PlatformResult<Release>> CreateReleaseAsync(
        CreateReleaseRequest request, CancellationToken ct = default) =>
        LoopVerbUnsupported<Release>("release");

    public Task<PlatformResult<IReadOnlyList<PullRequestReviewComment>>> ListPullRequestReviewCommentsAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default) =>
        LoopVerbUnsupported<IReadOnlyList<PullRequestReviewComment>>("review-comment listing");

    public Task<PlatformResult<IReadOnlyList<Commit>>> ListCommitsAsync(
        ListCommitsRequest request, CancellationToken ct = default) =>
        LoopVerbUnsupported<IReadOnlyList<Commit>>("commit-read");

    public Task<PlatformResult<IReadOnlyList<PrFile>>> ListBranchFileChangesAsync(
        ListBranchFileChangesRequest request, CancellationToken ct = default) =>
        LoopVerbUnsupported<IReadOnlyList<PrFile>>("commit-read");

    // ── Epic 31 P3 (seam 5) — the engine-callback verbs stay typed-unsupported
    //    on GitLab (issues use iids + a different label model); P6 scoped the
    //    driver to the PR lifecycle family, so these are a recorded post-epic
    //    follow-up. The security-alert surface has no GitLab CE equivalent of
    //    the abstraction's dependabot/code-scanning split. ──

    public Task<PlatformResult<IReadOnlyList<Issue>>> ListIssuesAsync(
        ListIssuesRequest request, CancellationToken ct = default) =>
        LoopVerbUnsupported<IReadOnlyList<Issue>>("issue listing");

    public Task<PlatformResult<Issue>> CreateIssueAsync(
        Tamma.Platforms.Abstractions.Models.CreateIssueRequest request, CancellationToken ct = default) =>
        LoopVerbUnsupported<Issue>("issue creation");

    public Task<PlatformResult<SecurityAlerts>> ListSecurityAlertsAsync(
        string owner, string repoName, string alertType, CancellationToken ct = default) =>
        LoopVerbUnsupported<SecurityAlerts>("security alerts");

    public async Task<PlatformResult<IssueComment>> CreateIssueCommentAsync(
        string owner, string repoName, string issueOrPrNumber, string body, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        var pid = EncodeProjectRef(owner, repoName);
        var payload = new { body };
        try
        {
            // Notes endpoint also covers MRs, but for parity with the
            // method name we route to /issues/. PR comments use the
            // CreatePullRequestReviewCommentAsync entrypoint; this is
            // the issue-thread comment.
            var (resp, note) = await _http.PostJsonAsync<object, GitLabNote>(
                $"projects/{pid}/issues/{issueOrPrNumber}/notes", payload, ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.Response.IsSuccessStatusCode)
                {
                    return PlatformResult<IssueComment>.FromError(
                        GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
                }
                if (note is null)
                {
                    return PlatformResult<IssueComment>.FromError(new PlatformError.Unknown("empty body"));
                }
                return PlatformResult<IssueComment>.FromOk(new IssueComment(
                    Id: note.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Body: note.Body ?? string.Empty,
                    AuthorLogin: note.Author?.Username ?? string.Empty,
                    CreatedAt: note.CreatedAt));
            }
        }
        catch (HttpRequestException)
        {
            return PlatformResult<IssueComment>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    public async Task<PlatformResult<WebhookRegistration>> RegisterWebhookAsync(
        RegisterWebhookRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pid = EncodeProjectRef(request.Owner, request.RepoName);

        // Convert the abstraction's string event names into GitLab's
        // boolean flags.
        var events = new HashSet<string>(request.Events, StringComparer.OrdinalIgnoreCase);
        var body = new
        {
            url = request.DeliveryUrl,
            token = request.Secret,
            push_events = events.Contains("push"),
            merge_requests_events = events.Contains("merge_request") || events.Contains("merge_requests"),
            issues_events = events.Contains("issue") || events.Contains("issues"),
            pipeline_events = events.Contains("pipeline") || events.Contains("pipelines"),
            enable_ssl_verification = true,
        };
        try
        {
            var (resp, hook) = await _http.PostJsonAsync<object, GitLabHook>(
                $"projects/{pid}/hooks", body, ct).ConfigureAwait(false);
            using (resp)
            {
                if (!resp.Response.IsSuccessStatusCode)
                {
                    return PlatformResult<WebhookRegistration>.FromError(
                        GitLabErrorMapper.Map(resp.Response.StatusCode, resp.Body, resp.RetryAfter));
                }
                if (hook is null)
                {
                    return PlatformResult<WebhookRegistration>.FromError(new PlatformError.Unknown("empty body"));
                }
                var registeredEvents = new List<string>();
                if (hook.PushEvents) registeredEvents.Add("push");
                if (hook.MergeRequestsEvents) registeredEvents.Add("merge_request");
                if (hook.IssuesEvents) registeredEvents.Add("issue");
                if (hook.PipelineEvents) registeredEvents.Add("pipeline");
                return PlatformResult<WebhookRegistration>.FromOk(new WebhookRegistration(
                    Id: hook.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Url: hook.Url ?? request.DeliveryUrl,
                    Events: registeredEvents,
                    Active: request.Active));
            }
        }
        catch (HttpRequestException)
        {
            return PlatformResult<WebhookRegistration>.FromError(new PlatformError.ServiceUnavailable());
        }
    }

    public async IAsyncEnumerable<Repo> ListAccessibleReposAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // GET /projects?membership=true returns repos the credential
        // can access. Paginated via Link header.
        IAsyncEnumerable<GitLabProject> pages;
        try
        {
            pages = _http.EnumeratePagesAsync<GitLabProject>(
                "projects?membership=true&simple=false&order_by=last_activity_at",
                ct: ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "GitLab ListAccessibleRepos network error");
            yield break;
        }

        await foreach (var project in pages.ConfigureAwait(false))
        {
            yield return MapRepo(project);
        }
    }

    private async Task<GitLabMergeRequest?> FindExistingMergeRequestAsync(
        string pid, string sourceBranch, string targetBranch, CancellationToken ct)
    {
        var path = $"projects/{pid}/merge_requests" +
                   $"?state=opened" +
                   $"&source_branch={Uri.EscapeDataString(sourceBranch)}" +
                   $"&target_branch={Uri.EscapeDataString(targetBranch)}";
        try
        {
            await foreach (var mr in _http.EnumeratePagesAsync<GitLabMergeRequest>(path, ct: ct).ConfigureAwait(false))
            {
                return mr; // first hit wins
            }
        }
        catch (GitLabRequestException ex)
        {
            _logger.LogDebug(ex, "Idempotency lookup failed; continuing to create");
        }
        catch (HttpRequestException)
        {
            // Treat lookup failure as non-fatal for idempotency.
        }
        return null;
    }

    private static Repo MapRepo(GitLabProject project)
    {
        // GitLab path_with_namespace = "group/subgroup/project". Owner
        // is everything before the last segment; name is the last.
        var fullPath = project.PathWithNamespace ?? string.Empty;
        var lastSlash = fullPath.LastIndexOf('/');
        var owner = lastSlash > 0 ? fullPath[..lastSlash] : (project.Namespace?.FullPath ?? string.Empty);
        var name = lastSlash > 0 ? fullPath[(lastSlash + 1)..] : (project.Path ?? project.Name ?? string.Empty);

        var host = string.Empty;
        if (Uri.TryCreate(project.WebUrl, UriKind.Absolute, out var webUri))
        {
            host = webUri.Host;
        }

        return new Repo(
            Host: host,
            Owner: owner,
            Name: name,
            DefaultBranch: project.DefaultBranch ?? "main",
            IsPrivate: !string.Equals(project.Visibility, "public", StringComparison.OrdinalIgnoreCase),
            Description: project.Description,
            CloneUrl: project.HttpUrlToRepo ?? string.Empty,
            HtmlUrl: project.WebUrl ?? string.Empty);
    }
}
