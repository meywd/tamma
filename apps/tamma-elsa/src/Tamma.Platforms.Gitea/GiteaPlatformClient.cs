using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.Gitea.Dtos;

namespace Tamma.Platforms.Gitea;

/// <summary>
/// Story 31-4 — <see cref="IGitPlatformClient"/> backed by Gitea REST
/// v1 (<c>/api/v1/...</c>). All endpoint mappings follow Gitea
/// upstream docs (https://docs.gitea.com/api). Auth + retry concerns
/// live in <see cref="GiteaHttpClient"/>; this class is the projection
/// layer between Gitea DTOs and the neutral
/// <see cref="Tamma.Platforms.Abstractions.Models"/> records.
///
/// <para>Endpoint pagination: Gitea's max <c>limit</c> is 50 (vs.
/// GitHub's 100). The list helpers here loop on <c>page=1,2,...</c>
/// until a partial page comes back.</para>
/// </summary>
public sealed class GiteaPlatformClient : IGitPlatformClient
{
    private const int PageSize = 50;
    private static readonly char[] HostTrim = ['/'];

    private readonly GiteaHttpClient _http;
    private readonly string _host;
    private readonly ILogger _logger;

    internal GiteaPlatformClient(
        GiteaHttpClient http,
        string host,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _http = http;
        _host = host;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public async Task<PlatformResult<Repo>> GetRepoAsync(
        string owner, string repoName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);

        var path = $"/api/v1/repos/{Encode(owner)}/{Encode(repoName)}";
        var result = await _http.GetJsonAsync<GiteaRepoDto>(path, ct).ConfigureAwait(false);
        return result.Map(dto => MapRepo(dto));
    }

    /// <inheritdoc />
    public async Task<PlatformResult<IReadOnlyList<Branch>>> ListRepoBranchesAsync(
        string owner, string repoName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);

        var aggregate = new List<Branch>();
        var page = 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var path = $"/api/v1/repos/{Encode(owner)}/{Encode(repoName)}/branches?page={page}&limit={PageSize}";
            var result = await _http
                .GetJsonAsync<List<GiteaBranchDto>>(path, ct)
                .ConfigureAwait(false);
            switch (result)
            {
                case PlatformResult<List<GiteaBranchDto>>.Ok ok:
                    var batch = ok.Value;
                    foreach (var b in batch)
                    {
                        aggregate.Add(MapBranch(b));
                    }
                    if (batch.Count < PageSize)
                    {
                        return PlatformResult<IReadOnlyList<Branch>>.FromOk(aggregate);
                    }
                    page++;
                    break;
                case PlatformResult<List<GiteaBranchDto>>.Failed failed:
                    return PlatformResult<IReadOnlyList<Branch>>.FromError(failed.Error);
                case PlatformResult<List<GiteaBranchDto>>.ServiceUnavailable:
                    return PlatformResult<IReadOnlyList<Branch>>.FromServiceUnavailable();
                default:
                    throw new InvalidOperationException("unhandled result variant");
            }
        }
    }

    /// <inheritdoc />
    public async Task<PlatformResult<byte[]>> GetFileContentAsync(
        GetFileContentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = $"/api/v1/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}" +
                   $"/contents/{EncodePath(request.Path)}?ref={Encode(request.Ref)}";
        var result = await _http.GetJsonAsync<GiteaContentsDto>(path, ct).ConfigureAwait(false);
        return result.Map(DecodeContents);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<Branch>> CreateBranchAsync(
        CreateBranchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = $"/api/v1/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}/branches";
        // Gitea wants either old_branch_name OR old_ref_name (a SHA).
        // Plan §locked-decisions: callers supply a SHA, so we use
        // old_ref_name verbatim.
        var body = new GiteaCreateBranchDto
        {
            NewBranchName = request.NewBranchName,
            OldRefName = request.FromSha,
        };
        var result = await _http
            .PostJsonAsync<GiteaBranchDto>(path, body, ct)
            .ConfigureAwait(false);
        return result.Map(MapBranch);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<Branch>> GetBranchAsync(
        string owner, string repoName, string branchName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        // GET /repos/{o}/{r}/branches/{branch} — 404 = absent.
        var path = $"/api/v1/repos/{Encode(owner)}/{Encode(repoName)}/branches/{EncodePath(branchName)}";
        var result = await _http.GetJsonAsync<GiteaBranchDto>(path, ct).ConfigureAwait(false);
        return result.Map(MapBranch);
    }

    /// <inheritdoc />
    public Task<PlatformResult<bool>> DeleteBranchAsync(
        string owner, string repoName, string branchName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        // DELETE /repos/{o}/{r}/branches/{branch} → 204.
        var path = $"/api/v1/repos/{Encode(owner)}/{Encode(repoName)}/branches/{EncodePath(branchName)}";
        return _http.DeleteNoContentAsync(path, ct);
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

        // Gitea has no head/base filter on the list endpoint — list open
        // PRs (first page) and filter client-side, the same shape the
        // idempotent-open lookup uses.
        var path = $"/api/v1/repos/{Encode(owner)}/{Encode(repoName)}" +
                   $"/pulls?state=open&page=1&limit={PageSize}";
        var result = await _http
            .GetJsonAsync<List<GiteaPullRequestDto>>(path, ct)
            .ConfigureAwait(false);
        return result.Map(list =>
        {
            IReadOnlyList<PullRequest> prs = list
                .Where(dto =>
                    string.Equals(dto.Head?.Ref, sourceBranch, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(dto.Base?.Ref, targetBranch, StringComparison.OrdinalIgnoreCase))
                .Select(MapPullRequest)
                .ToList();
            return prs;
        });
    }

    /// <inheritdoc />
    public async Task<PlatformResult<PullRequest>> UpdatePullRequestAsync(
        UpdatePullRequestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new Dictionary<string, object?>();
        if (request.Title is not null) body["title"] = request.Title;
        if (request.Body is not null) body["body"] = request.Body;

        var path = $"/api/v1/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}" +
                   $"/pulls/{Encode(request.PrNumber)}";
        var result = await _http
            .PatchJsonAsync<GiteaPullRequestDto>(path, body, ct)
            .ConfigureAwait(false);
        return result.Map(MapPullRequest);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<PullRequest>> OpenPullRequestAsync(
        OpenPullRequestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Idempotency per 31-1 ADR §5: detect existing PR with the
        // same (head, base) pair and return it instead of failing.
        var existing = await FindOpenPullByBranchPairAsync(
            request.Owner, request.RepoName,
            request.SourceBranch, request.TargetBranch, ct)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogInformation(
                "OpenPullRequestAsync returning existing PR {Number} for {Source}->{Target} (idempotent)",
                existing.Number, request.SourceBranch, request.TargetBranch);
            return PlatformResult<PullRequest>.FromOk(existing);
        }

        var path = $"/api/v1/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}/pulls";
        var body = new GiteaCreatePullDto
        {
            Title = request.Title,
            Body = request.Body,
            Head = request.SourceBranch,
            Base = request.TargetBranch,
            Draft = request.IsDraft,
        };
        var result = await _http
            .PostJsonAsync<GiteaPullRequestDto>(path, body, ct)
            .ConfigureAwait(false);
        return result.Map(MapPullRequest);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<PullRequest>> GetPullRequestAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(prNumber);

        var path = $"/api/v1/repos/{Encode(owner)}/{Encode(repoName)}/pulls/{Encode(prNumber)}";
        var result = await _http
            .GetJsonAsync<GiteaPullRequestDto>(path, ct)
            .ConfigureAwait(false);
        return result.Map(MapPullRequest);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<IReadOnlyList<PrFile>>> ListPullRequestFilesAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(prNumber);

        var aggregate = new List<PrFile>();
        var page = 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var path = $"/api/v1/repos/{Encode(owner)}/{Encode(repoName)}/pulls/" +
                       $"{Encode(prNumber)}/files?page={page}&limit={PageSize}";
            var result = await _http
                .GetJsonAsync<List<GiteaPrFileDto>>(path, ct)
                .ConfigureAwait(false);
            switch (result)
            {
                case PlatformResult<List<GiteaPrFileDto>>.Ok ok:
                    foreach (var f in ok.Value) aggregate.Add(MapPrFile(f));
                    if (ok.Value.Count < PageSize)
                    {
                        return PlatformResult<IReadOnlyList<PrFile>>.FromOk(aggregate);
                    }
                    page++;
                    break;
                case PlatformResult<List<GiteaPrFileDto>>.Failed failed:
                    return PlatformResult<IReadOnlyList<PrFile>>.FromError(failed.Error);
                case PlatformResult<List<GiteaPrFileDto>>.ServiceUnavailable:
                    return PlatformResult<IReadOnlyList<PrFile>>.FromServiceUnavailable();
                default:
                    throw new InvalidOperationException("unhandled result variant");
            }
        }
    }

    /// <inheritdoc />
    public async Task<PlatformResult<IssueComment>> CreatePullRequestReviewCommentAsync(
        CreatePullRequestReviewCommentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = $"/api/v1/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}" +
                   $"/pulls/{Encode(request.PrNumber)}/reviews";
        var body = new GiteaCreateReviewDto
        {
            Body = request.Body,
            Event = "COMMENT",
            CommitId = request.CommitSha,
            Comments = new List<GiteaCreateReviewCommentDto>
            {
                new()
                {
                    Path = request.Path,
                    Body = request.Body,
                    NewPosition = request.Line,
                },
            },
        };
        var result = await _http
            .PostJsonAsync<GiteaReviewDto>(path, body, ct)
            .ConfigureAwait(false);
        return result.Map(r => new IssueComment(
            Id: r.Id.ToString(),
            Body: r.Body ?? string.Empty,
            AuthorLogin: r.User?.Login ?? "unknown",
            CreatedAt: r.SubmittedAt));
    }

    /// <inheritdoc />
    public async Task<PlatformResult<PullRequest>> MergePullRequestAsync(
        MergePullRequestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = $"/api/v1/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}" +
                   $"/pulls/{Encode(request.PrNumber)}/merge";
        var body = new GiteaMergePullDto
        {
            Do = request.Method switch
            {
                MergeMethod.Merge => "merge",
                MergeMethod.Squash => "squash",
                MergeMethod.Rebase => "rebase",
                _ => "merge",
            },
            MergeMessage = request.CommitMessage,
        };
        var mergeResult = await _http.PostNoContentAsync(path, body, ct).ConfigureAwait(false);
        return mergeResult switch
        {
            PlatformResult<bool>.Ok => await GetPullRequestAsync(
                request.Owner, request.RepoName, request.PrNumber, ct).ConfigureAwait(false),
            PlatformResult<bool>.Failed failed => PlatformResult<PullRequest>.FromError(failed.Error),
            PlatformResult<bool>.ServiceUnavailable => PlatformResult<PullRequest>.FromServiceUnavailable(),
            _ => throw new InvalidOperationException("unhandled merge result"),
        };
    }

    // Story 31-13 — Gitea (and Forgejo, which rides this client) does not yet
    // carry the PrLifecycle capability, so the six PR lifecycle verbs return
    // capability_unsupported per the interface no-throw contract. Wiring them for
    // real is 31-6 follow-up work, recorded via the absent capability flag.
    private static Task<PlatformResult<PullRequest>> PrLifecycleUnsupported() =>
        Task.FromResult(PlatformResult<PullRequest>.FromError(
            new PlatformError.InvalidRequest("capability_unsupported",
                "the Gitea driver does not implement PR lifecycle verbs yet (31-6)")));

    /// <inheritdoc />
    public Task<PlatformResult<PullRequest>> ClosePullRequestAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default) =>
        PrLifecycleUnsupported();

    /// <inheritdoc />
    public Task<PlatformResult<PullRequest>> ReopenPullRequestAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default) =>
        PrLifecycleUnsupported();

    /// <inheritdoc />
    public Task<PlatformResult<PullRequest>> RequestReviewersAsync(
        RequestReviewersRequest request, CancellationToken ct = default) =>
        PrLifecycleUnsupported();

    /// <inheritdoc />
    public Task<PlatformResult<PullRequest>> AddPullRequestLabelsAsync(
        AddPullRequestLabelsRequest request, CancellationToken ct = default) =>
        PrLifecycleUnsupported();

    /// <inheritdoc />
    public Task<PlatformResult<PullRequest>> RemovePullRequestLabelAsync(
        string owner, string repoName, string prNumber, string label, CancellationToken ct = default) =>
        PrLifecycleUnsupported();

    /// <inheritdoc />
    public Task<PlatformResult<PullRequest>> SetDraftAsync(
        SetPullRequestDraftRequest request, CancellationToken ct = default) =>
        PrLifecycleUnsupported();

    // Epic 31 P1 (stage 1) — the loop verbs (issue lifecycle, releases,
    // review-comment listing, commit reads). Gitea's API supports all of them,
    // but the driver does not implement them yet, records that via the absent
    // IssueLifecycle / Releases / PrReviewCommentRead / CommitReads capability
    // flags, and answers with typed capability_unsupported per the interface
    // no-throw contract. Real wiring is P5 (Gitea end-to-end) work.
    private static Task<PlatformResult<T>> LoopVerbUnsupported<T>(string capability) =>
        Task.FromResult(PlatformResult<T>.FromError(
            new PlatformError.InvalidRequest("capability_unsupported",
                $"the Gitea driver does not implement {capability} verbs yet (Epic 31 P5)")));

    /// <inheritdoc />
    public Task<PlatformResult<Issue>> CloseIssueAsync(
        string owner, string repoName, string issueNumber, string? comment = null,
        CancellationToken ct = default) =>
        LoopVerbUnsupported<Issue>("issue lifecycle");

    /// <inheritdoc />
    public Task<PlatformResult<IReadOnlyList<string>>> AddIssueLabelsAsync(
        AddIssueLabelsRequest request, CancellationToken ct = default) =>
        LoopVerbUnsupported<IReadOnlyList<string>>("issue lifecycle");

    /// <inheritdoc />
    public Task<PlatformResult<IReadOnlyList<string>>> RemoveIssueLabelAsync(
        string owner, string repoName, string issueNumber, string label,
        CancellationToken ct = default) =>
        LoopVerbUnsupported<IReadOnlyList<string>>("issue lifecycle");

    /// <inheritdoc />
    public Task<PlatformResult<Release>> CreateReleaseAsync(
        CreateReleaseRequest request, CancellationToken ct = default) =>
        LoopVerbUnsupported<Release>("release");

    /// <inheritdoc />
    public Task<PlatformResult<IReadOnlyList<PullRequestReviewComment>>> ListPullRequestReviewCommentsAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default) =>
        LoopVerbUnsupported<IReadOnlyList<PullRequestReviewComment>>("review-comment listing");

    /// <inheritdoc />
    public Task<PlatformResult<IReadOnlyList<Commit>>> ListCommitsAsync(
        ListCommitsRequest request, CancellationToken ct = default) =>
        LoopVerbUnsupported<IReadOnlyList<Commit>>("commit-read");

    /// <inheritdoc />
    public Task<PlatformResult<IReadOnlyList<PrFile>>> ListBranchFileChangesAsync(
        ListBranchFileChangesRequest request, CancellationToken ct = default) =>
        LoopVerbUnsupported<IReadOnlyList<PrFile>>("commit-read");

    // ── Epic 31 P3 (seam 5) — issue listing + creation are REAL on Gitea
    //    (the loop's work selection must run off-GitHub — P3 acceptance
    //    names a Gitea instance); the security-alert surface has no Gitea
    //    equivalent and answers the typed capability refusal. ──

    /// <inheritdoc />
    public async Task<PlatformResult<IReadOnlyList<Issue>>> ListIssuesAsync(
        ListIssuesRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var limit = Math.Clamp(request.PerPage, 1, 50);
        var page = Math.Max(1, request.Page);
        var state = request.State?.ToLowerInvariant() switch
        {
            "closed" => "closed",
            "all" => "all",
            _ => "open",
        };
        // type=issues excludes pull requests server-side; the row-level
        // pull_request filter below is belt-and-braces for older Gitea.
        var path = $"/api/v1/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}" +
                   $"/issues?state={state}&type=issues&page={page}&limit={limit}";
        if (request.Labels is { Count: > 0 } labels)
        {
            path += $"&labels={Encode(string.Join(",", labels))}";
        }

        var result = await _http.GetJsonAsync<List<GiteaIssueDto>>(path, ct).ConfigureAwait(false);
        return result.Map(list =>
        {
            IReadOnlyList<Issue> issues = list
                .Where(i => i.PullRequest is null)
                .Select(MapIssue)
                .ToList();
            return issues;
        });
    }

    /// <inheritdoc />
    public async Task<PlatformResult<Issue>> CreateIssueAsync(
        Tamma.Platforms.Abstractions.Models.CreateIssueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);

        // Gitea's create-issue takes label IDs, not names — resolve them
        // best-effort (a miss skips that label; the create never fails
        // because a label doesn't exist).
        var labelIds = new List<long>();
        if (request.Labels is { Count: > 0 } wanted)
        {
            var labelsRes = await _http
                .GetJsonAsync<List<GiteaLabelDto>>(
                    $"/api/v1/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}/labels?limit=50", ct)
                .ConfigureAwait(false);
            if (labelsRes is PlatformResult<List<GiteaLabelDto>>.Ok labelsOk)
            {
                labelIds.AddRange(labelsOk.Value
                    .Where(l => l.Name is not null && wanted.Contains(l.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(l => l.Id));
            }
        }

        var body = new
        {
            title = request.Title,
            body = request.Body,
            labels = labelIds,
            assignees = request.Assignees ?? (IReadOnlyList<string>)Array.Empty<string>(),
        };
        var result = await _http
            .PostJsonAsync<GiteaIssueDto>(
                $"/api/v1/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}/issues", body, ct)
            .ConfigureAwait(false);
        return result.Map(MapIssue);
    }

    /// <inheritdoc />
    public Task<PlatformResult<SecurityAlerts>> ListSecurityAlertsAsync(
        string owner, string repoName, string alertType, CancellationToken ct = default) =>
        Task.FromResult(PlatformResult<SecurityAlerts>.FromError(
            new PlatformError.InvalidRequest("capability_unsupported",
                "Gitea has no security-alert (dependabot/code-scanning) API surface")));

    private static Issue MapIssue(GiteaIssueDto dto) => new(
        Number: dto.Number.ToString(),
        Title: dto.Title ?? string.Empty,
        Body: dto.Body,
        State: string.Equals(dto.State, "closed", StringComparison.OrdinalIgnoreCase)
            ? IssueState.Closed
            : IssueState.Open,
        HtmlUrl: dto.HtmlUrl ?? string.Empty,
        Labels: (dto.Labels ?? [])
            .Select(l => l.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .ToList());

    /// <inheritdoc />
    public async Task<PlatformResult<IssueComment>> CreateIssueCommentAsync(
        string owner, string repoName, string issueOrPrNumber, string body,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(issueOrPrNumber);
        ArgumentNullException.ThrowIfNull(body);

        var path = $"/api/v1/repos/{Encode(owner)}/{Encode(repoName)}/issues/{Encode(issueOrPrNumber)}/comments";
        var payload = new { body };
        var result = await _http
            .PostJsonAsync<GiteaIssueCommentDto>(path, payload, ct)
            .ConfigureAwait(false);
        return result.Map(c => new IssueComment(
            Id: c.Id.ToString(),
            Body: c.Body ?? string.Empty,
            AuthorLogin: c.User?.Login ?? "unknown",
            CreatedAt: c.CreatedAt));
    }

    /// <inheritdoc />
    public async Task<PlatformResult<WebhookRegistration>> RegisterWebhookAsync(
        RegisterWebhookRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = $"/api/v1/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}/hooks";
        var body = new GiteaCreateWebhookDto
        {
            Type = "gitea",
            Config = new Dictionary<string, string>
            {
                ["url"] = request.DeliveryUrl,
                ["content_type"] = "json",
                ["secret"] = request.Secret,
            },
            Events = new List<string>(request.Events),
            Active = request.Active,
        };
        var result = await _http
            .PostJsonAsync<GiteaWebhookDto>(path, body, ct)
            .ConfigureAwait(false);
        return result.Map(MapWebhook);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Repo> ListAccessibleReposAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var page = 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var path = $"/api/v1/user/repos?page={page}&limit={PageSize}";
            var result = await _http
                .GetJsonAsync<List<GiteaRepoDto>>(path, ct)
                .ConfigureAwait(false);
            List<GiteaRepoDto>? batch = null;
            switch (result)
            {
                case PlatformResult<List<GiteaRepoDto>>.Ok ok:
                    batch = ok.Value;
                    break;
                case PlatformResult<List<GiteaRepoDto>>.Failed failed:
                    _logger.LogWarning(
                        "ListAccessibleReposAsync stopped at page {Page} due to {Error}",
                        page, failed.Error.GetType().Name);
                    yield break;
                case PlatformResult<List<GiteaRepoDto>>.ServiceUnavailable:
                    yield break;
            }
            if (batch is null) yield break;
            foreach (var dto in batch)
            {
                yield return MapRepo(dto);
            }
            if (batch.Count < PageSize) yield break;
            page++;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Mapping helpers
    // ─────────────────────────────────────────────────────────────────

    internal Repo MapRepo(GiteaRepoDto dto)
    {
        var owner = dto.Owner?.Login
            ?? (dto.FullName?.Split('/').FirstOrDefault())
            ?? "unknown";
        return new Repo(
            Host: _host,
            Owner: owner,
            Name: dto.Name ?? string.Empty,
            DefaultBranch: dto.DefaultBranch ?? "main",
            IsPrivate: dto.Private,
            Description: dto.Description,
            CloneUrl: dto.CloneUrl ?? string.Empty,
            HtmlUrl: dto.HtmlUrl ?? string.Empty);
    }

    internal static Branch MapBranch(GiteaBranchDto dto)
    {
        var sha = dto.Commit?.Id ?? dto.Commit?.Sha ?? string.Empty;
        return new Branch(
            Name: dto.Name ?? string.Empty,
            Sha: sha,
            Protected: dto.Protected);
    }

    internal static byte[] DecodeContents(GiteaContentsDto dto)
    {
        if (!string.Equals(dto.Type, "file", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<byte>();
        }
        if (string.Equals(dto.Encoding, "base64", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(dto.Content))
        {
            try
            {
                return Convert.FromBase64String(dto.Content);
            }
            catch (FormatException)
            {
                return Array.Empty<byte>();
            }
        }
        // Plain text — Gitea sometimes returns no encoding for small
        // text files. Fall back to UTF-8 bytes of the content.
        return string.IsNullOrEmpty(dto.Content)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(dto.Content);
    }

    internal static PullRequest MapPullRequest(GiteaPullRequestDto dto)
    {
        var state = (dto.State?.ToLowerInvariant(), dto.Merged) switch
        {
            (_, true) => PullRequestState.Merged,
            ("closed", _) => PullRequestState.Closed,
            ("open", _) => PullRequestState.Open,
            _ => PullRequestState.Open,
        };
        return new PullRequest(
            Number: dto.Number.ToString(),
            Title: dto.Title ?? string.Empty,
            Body: dto.Body,
            SourceBranch: dto.Head?.Ref ?? string.Empty,
            TargetBranch: dto.Base?.Ref ?? string.Empty,
            State: state,
            IsDraft: dto.Draft,
            HtmlUrl: dto.HtmlUrl ?? string.Empty,
            AuthorLogin: dto.User?.Login ?? "unknown",
            CreatedAt: dto.CreatedAt,
            UpdatedAt: dto.UpdatedAt);
    }

    internal static PrFile MapPrFile(GiteaPrFileDto dto)
    {
        var status = (dto.Status?.ToLowerInvariant()) switch
        {
            "added" => PrFileStatus.Added,
            "modified" => PrFileStatus.Modified,
            "removed" => PrFileStatus.Removed,
            "renamed" => PrFileStatus.Renamed,
            "copied" => PrFileStatus.Copied,
            _ => PrFileStatus.Other,
        };
        return new PrFile(
            Path: dto.Filename ?? string.Empty,
            Status: status,
            Additions: dto.Additions,
            Deletions: dto.Deletions);
    }

    internal static WebhookRegistration MapWebhook(GiteaWebhookDto dto)
    {
        var url = dto.Config is { } cfg && cfg.TryGetValue("url", out var u) ? u : string.Empty;
        return new WebhookRegistration(
            Id: dto.Id.ToString(),
            Url: url,
            Events: dto.Events ?? new List<string>(),
            Active: dto.Active);
    }

    /// <summary>
    /// Find an open PR with matching (head, base) pair so OpenPR is
    /// idempotent (31-1 ADR §5). Best-effort — on failure we return
    /// null and let the caller create.
    /// </summary>
    private async Task<PullRequest?> FindOpenPullByBranchPairAsync(
        string owner, string repoName, string head, string targetBase,
        CancellationToken ct)
    {
        // Gitea accepts head as "user:branch" or just "branch"; we
        // pass branch directly. state=open filters to live PRs.
        var path = $"/api/v1/repos/{Encode(owner)}/{Encode(repoName)}" +
                   $"/pulls?state=open&page=1&limit={PageSize}";
        var result = await _http
            .GetJsonAsync<List<GiteaPullRequestDto>>(path, ct)
            .ConfigureAwait(false);
        if (result is not PlatformResult<List<GiteaPullRequestDto>>.Ok ok)
        {
            return null;
        }
        foreach (var dto in ok.Value)
        {
            if (string.Equals(dto.Head?.Ref, head, StringComparison.OrdinalIgnoreCase)
                && string.Equals(dto.Base?.Ref, targetBase, StringComparison.OrdinalIgnoreCase))
            {
                return MapPullRequest(dto);
            }
        }
        return null;
    }

    private static string Encode(string segment) => Uri.EscapeDataString(segment);

    private static string EncodePath(string path)
    {
        // Gitea wants slashes preserved in paths — escape segments
        // individually.
        var parts = path.Split('/', StringSplitOptions.None);
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = Uri.EscapeDataString(parts[i]);
        }
        return string.Join('/', parts).TrimStart(HostTrim);
    }
}
