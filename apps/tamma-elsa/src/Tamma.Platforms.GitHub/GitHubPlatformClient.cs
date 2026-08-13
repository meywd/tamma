using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;
using Tamma.Platforms.GitHub.Dtos;

namespace Tamma.Platforms.GitHub;

/// <summary>
/// Epic 31 P1 stage 2 — <see cref="IGitPlatformClient"/> backed by the
/// GitHub REST v3 + GraphQL v4 APIs, directly over
/// <see cref="GitHubHttpClient"/>. This is the ABSORBED implementation
/// (execution plan §2's "the GitHub driver ABSORBS the live client"):
/// the REST bodies were ported down from
/// <c>Tamma.Api/Services/GitHubIntegrationService.cs</c> (branch / PR /
/// merge / comments / issues / labels / releases / commits /
/// file-changes and the GraphQL set-draft at its lines 661-709) and
/// re-shaped onto the platform-neutral no-throw
/// <see cref="PlatformResult{T}"/> contract. <c>Tamma.Api</c> is NOT
/// referenced — the absorption goes down the layering, never across.
///
/// <para>Error classification parity: HTTP failures map through
/// <see cref="GitHubErrorMapper"/> to the same coarse classes the live
/// path's status-prefixed strings produce (404 → NotFound, 403
/// rate-limit vs permission, 422 validation/already-exists, 409
/// conflict) so the P2 mediation swap is behavior-identical.</para>
///
/// <para>Pagination: GitHub's max <c>per_page</c> is 100. List
/// helpers loop <c>page=1,2,…</c> until a partial page returns.</para>
/// </summary>
public sealed class GitHubPlatformClient : IGitPlatformClient
{
    private const int PageSize = 100;
    /// <summary>Commit listing mirrors the absorbed live path's
    /// <c>per_page=20</c> "recent commits" read.</summary>
    private const int CommitPageSize = 20;

    private readonly GitHubHttpClient _http;
    private readonly string _host;
    private readonly bool _appMode;
    private readonly ILogger _logger;

    internal GitHubPlatformClient(
        GitHubHttpClient http,
        string host,
        bool appMode = false,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        _http = http;
        _host = host;
        _appMode = appMode;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Hostname (e.g. <c>github.com</c>) the driver was constructed
    /// for — used to populate <see cref="Repo.Host"/>; test helper.
    /// </summary>
    public string Host => _host;

    // ================================================================
    // Repo / branch / file reads
    // ================================================================

    /// <inheritdoc />
    public async Task<PlatformResult<Repo>> GetRepoAsync(
        string owner, string repoName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);

        var result = await _http
            .GetJsonAsync<GitHubRepoDto>($"/repos/{Encode(owner)}/{Encode(repoName)}", ct)
            .ConfigureAwait(false);
        return result.Map(MapRepo);
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
            var path = $"/repos/{Encode(owner)}/{Encode(repoName)}/branches?per_page={PageSize}&page={page}";
            var result = await _http.GetJsonAsync<List<GitHubBranchDto>>(path, ct).ConfigureAwait(false);
            switch (result)
            {
                case PlatformResult<List<GitHubBranchDto>>.Ok ok:
                    foreach (var b in ok.Value) aggregate.Add(MapBranch(b));
                    if (ok.Value.Count < PageSize)
                    {
                        return PlatformResult<IReadOnlyList<Branch>>.FromOk(aggregate);
                    }
                    page++;
                    break;
                case PlatformResult<List<GitHubBranchDto>>.Failed failed:
                    return PlatformResult<IReadOnlyList<Branch>>.FromError(failed.Error);
                case PlatformResult<List<GitHubBranchDto>>.ServiceUnavailable:
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

        var path = $"/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}" +
                   $"/contents/{EncodePath(request.Path)}?ref={Encode(request.Ref)}";
        var result = await _http.GetJsonAsync<GitHubContentsDto>(path, ct).ConfigureAwait(false);
        return result.Map(DecodeContents);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<Branch>> CreateBranchAsync(
        CreateBranchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // POST /repos/{o}/{r}/git/refs — absorbed from the live
        // CreateGitHubBranchAsync (callers supply the source SHA per the
        // interface contract, so the live path's base-ref resolution
        // dance is not needed here).
        var path = $"/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}/git/refs";
        var body = new
        {
            @ref = $"refs/heads/{request.NewBranchName}",
            sha = request.FromSha,
        };
        var result = await _http.PostJsonAsync<GitHubRefDto>(path, body, ct).ConfigureAwait(false);
        return result.Map(dto => new Branch(
            Name: request.NewBranchName,
            Sha: dto.Object?.Sha ?? request.FromSha,
            Protected: false));
    }

    /// <inheritdoc />
    public async Task<PlatformResult<Branch>> GetBranchAsync(
        string owner, string repoName, string branchName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        // GET /repos/{o}/{r}/branches/{branch} — the live path's
        // BranchExistsAsync probe (404 = absent).
        var path = $"/repos/{Encode(owner)}/{Encode(repoName)}/branches/{EncodePath(branchName)}";
        var result = await _http.GetJsonAsync<GitHubBranchDto>(path, ct).ConfigureAwait(false);
        return result.Map(MapBranch);
    }

    /// <inheritdoc />
    public Task<PlatformResult<bool>> DeleteBranchAsync(
        string owner, string repoName, string branchName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        // DELETE /repos/{o}/{r}/git/refs/heads/{branch} → 204 (the live
        // DeleteGitHubBranchAsync shape).
        var path = $"/repos/{Encode(owner)}/{Encode(repoName)}/git/refs/heads/{EncodePath(branchName)}";
        return _http.SendNoContentAsync(HttpMethod.Delete, path, body: null, ct);
    }

    // ================================================================
    // Pull requests
    // ================================================================

    /// <inheritdoc />
    public async Task<PlatformResult<IReadOnlyList<PullRequest>>> ListOpenPullRequestsForBranchAsync(
        string owner, string repoName, string sourceBranch, string targetBranch,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetBranch);

        // The live GetGitHubOpenPullRequestForBranchAsync lookup — head
        // filter wants owner:branch.
        var headFilter = $"{owner}:{sourceBranch}";
        var path = $"/repos/{Encode(owner)}/{Encode(repoName)}/pulls" +
                   $"?state=open&head={Encode(headFilter)}&base={Encode(targetBranch)}&per_page=10";
        var result = await _http
            .GetJsonAsync<List<GitHubPullRequestDto>>(path, ct)
            .ConfigureAwait(false);
        return result.Map(list =>
        {
            IReadOnlyList<PullRequest> prs = list.Select(MapPullRequest).ToList();
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

        var result = await _http
            .PatchJsonAsync<GitHubPullRequestDto>(
                PullPath(request.Owner, request.RepoName, request.PrNumber), body, ct)
            .ConfigureAwait(false);
        return result.Map(MapPullRequest);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<PullRequest>> OpenPullRequestAsync(
        OpenPullRequestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Idempotency per 31-1 ADR §5 (the live path's
        // GetGitHubOpenPullRequestForBranchAsync shape): detect an
        // existing open PR for the same (head, base) pair first.
        var headFilter = $"{request.Owner}:{request.SourceBranch}";
        var lookupPath = $"/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}/pulls" +
                         $"?state=open&head={Encode(headFilter)}&base={Encode(request.TargetBranch)}&per_page=1";
        var existing = await _http
            .GetJsonAsync<List<GitHubPullRequestDto>>(lookupPath, ct)
            .ConfigureAwait(false);
        if (existing is PlatformResult<List<GitHubPullRequestDto>>.Ok existingOk
            && existingOk.Value.Count > 0)
        {
            _logger.LogInformation(
                "OpenPullRequestAsync returning existing PR {Number} for {Source}->{Target} (idempotent)",
                existingOk.Value[0].Number, request.SourceBranch, request.TargetBranch);
            return PlatformResult<PullRequest>.FromOk(MapPullRequest(existingOk.Value[0]));
        }

        var path = $"/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}/pulls";
        var body = new
        {
            title = request.Title,
            body = request.Body,
            head = request.SourceBranch,
            @base = request.TargetBranch,
            draft = request.IsDraft,
        };
        var result = await _http
            .PostJsonAsync<GitHubPullRequestDto>(path, body, ct)
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

        var result = await _http
            .GetJsonAsync<GitHubPullRequestDto>(PullPath(owner, repoName, prNumber), ct)
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
            var path = $"{PullPath(owner, repoName, prNumber)}/files?per_page={PageSize}&page={page}";
            var result = await _http.GetJsonAsync<List<GitHubPrFileDto>>(path, ct).ConfigureAwait(false);
            switch (result)
            {
                case PlatformResult<List<GitHubPrFileDto>>.Ok ok:
                    foreach (var f in ok.Value) aggregate.Add(MapPrFile(f));
                    if (ok.Value.Count < PageSize)
                    {
                        return PlatformResult<IReadOnlyList<PrFile>>.FromOk(aggregate);
                    }
                    page++;
                    break;
                case PlatformResult<List<GitHubPrFileDto>>.Failed failed:
                    return PlatformResult<IReadOnlyList<PrFile>>.FromError(failed.Error);
                case PlatformResult<List<GitHubPrFileDto>>.ServiceUnavailable:
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

        // Absorbed from the live PostPullRequestReviewCommentAsync —
        // the request record carries the anchoring CommitSha, so the
        // live path's head-SHA fallback resolution is unnecessary.
        var path = $"{PullPath(request.Owner, request.RepoName, request.PrNumber)}/comments";
        var body = new
        {
            body = request.Body,
            commit_id = request.CommitSha,
            path = request.Path,
            line = request.Line,
            side = string.IsNullOrWhiteSpace(request.Side) ? "RIGHT" : request.Side,
        };
        var result = await _http.PostJsonAsync<GitHubCommentDto>(path, body, ct).ConfigureAwait(false);
        return result.Map(MapIssueComment);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<PullRequest>> MergePullRequestAsync(
        MergePullRequestRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = $"{PullPath(request.Owner, request.RepoName, request.PrNumber)}/merge";
        var body = new Dictionary<string, object?>
        {
            ["merge_method"] = request.Method switch
            {
                MergeMethod.Merge => "merge",
                MergeMethod.Rebase => "rebase",
                _ => "squash",
            },
        };
        if (!string.IsNullOrWhiteSpace(request.CommitMessage))
        {
            body["commit_message"] = request.CommitMessage;
        }

        var mergeResult = await _http
            .PutJsonAsync<GitHubMergeResultDto>(path, body, ct)
            .ConfigureAwait(false);
        return mergeResult switch
        {
            // The merge response is {merged, sha, message} — re-fetch
            // the PR so callers get the full updated record (state =
            // Merged, merge SHA reachable via GetPullRequest).
            PlatformResult<GitHubMergeResultDto>.Ok => await GetPullRequestAsync(
                request.Owner, request.RepoName, request.PrNumber, ct).ConfigureAwait(false),
            PlatformResult<GitHubMergeResultDto>.Failed failed =>
                PlatformResult<PullRequest>.FromError(failed.Error),
            PlatformResult<GitHubMergeResultDto>.ServiceUnavailable =>
                PlatformResult<PullRequest>.FromServiceUnavailable(),
            _ => throw new InvalidOperationException("unhandled merge result"),
        };
    }

    // ================================================================
    // Story 31-13 — PR lifecycle (absorbed from the live path's
    // PatchPullRequestStateAsync / RequestReviewersAsync /
    // SetPullRequestDraftAsync + the label endpoints).
    // ================================================================

    /// <inheritdoc />
    public Task<PlatformResult<PullRequest>> ClosePullRequestAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default) =>
        PatchPullStateAsync(owner, repoName, prNumber, "closed", ct);

    /// <inheritdoc />
    public Task<PlatformResult<PullRequest>> ReopenPullRequestAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default) =>
        PatchPullStateAsync(owner, repoName, prNumber, "open", ct);

    private async Task<PlatformResult<PullRequest>> PatchPullStateAsync(
        string owner, string repoName, string prNumber, string state, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(prNumber);

        var result = await _http
            .PatchJsonAsync<GitHubPullRequestDto>(
                PullPath(owner, repoName, prNumber), new { state }, ct)
            .ConfigureAwait(false);
        return result.Map(MapPullRequest);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<PullRequest>> RequestReviewersAsync(
        RequestReviewersRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = $"{PullPath(request.Owner, request.RepoName, request.PrNumber)}/requested_reviewers";
        var body = new Dictionary<string, object?>
        {
            ["reviewers"] = request.Reviewers,
        };
        if (request.TeamReviewers is { Count: > 0 })
        {
            body["team_reviewers"] = request.TeamReviewers;
        }
        // GitHub answers 201 with the updated PR object.
        var result = await _http
            .PostJsonAsync<GitHubPullRequestDto>(path, body, ct)
            .ConfigureAwait(false);
        return result.Map(MapPullRequest);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<PullRequest>> AddPullRequestLabelsAsync(
        AddPullRequestLabelsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Labels ride the issue side of a PR (the live TryAddLabelsAsync
        // shape). The response is the label array, so re-fetch the PR to
        // honor the verb's updated-PR return.
        var path = $"/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}" +
                   $"/issues/{Encode(request.PrNumber)}/labels";
        var result = await _http
            .PostJsonAsync<List<GitHubLabelDto>>(path, new { labels = request.Labels }, ct)
            .ConfigureAwait(false);
        return result switch
        {
            PlatformResult<List<GitHubLabelDto>>.Ok => await GetPullRequestAsync(
                request.Owner, request.RepoName, request.PrNumber, ct).ConfigureAwait(false),
            PlatformResult<List<GitHubLabelDto>>.Failed failed =>
                PlatformResult<PullRequest>.FromError(failed.Error),
            PlatformResult<List<GitHubLabelDto>>.ServiceUnavailable =>
                PlatformResult<PullRequest>.FromServiceUnavailable(),
            _ => throw new InvalidOperationException("unhandled result variant"),
        };
    }

    /// <inheritdoc />
    public async Task<PlatformResult<PullRequest>> RemovePullRequestLabelAsync(
        string owner, string repoName, string prNumber, string label, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(prNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var path = $"/repos/{Encode(owner)}/{Encode(repoName)}" +
                   $"/issues/{Encode(prNumber)}/labels/{Encode(label)}";
        var result = await _http.DeleteJsonAsync<List<GitHubLabelDto>>(path, ct).ConfigureAwait(false);
        // 404 = label was not present — idempotent success (the live
        // RemoveIssueLabelAsync posture).
        if (result is PlatformResult<List<GitHubLabelDto>>.Failed f
            && f.Error is not PlatformError.NotFound)
        {
            return PlatformResult<PullRequest>.FromError(f.Error);
        }
        if (result is PlatformResult<List<GitHubLabelDto>>.ServiceUnavailable)
        {
            return PlatformResult<PullRequest>.FromServiceUnavailable();
        }
        return await GetPullRequestAsync(owner, repoName, prNumber, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<PullRequest>> SetDraftAsync(
        SetPullRequestDraftRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // GitHub REST cannot toggle draft — the GraphQL mutations
        // convertPullRequestToDraft / markPullRequestReadyForReview are
        // the only path (absorbed from GitHubIntegrationService.cs
        // ~661-709). 1) resolve the PR node_id via REST; 2) run the
        // mutation on the GHES-aware GraphQL endpoint; 3) re-fetch the
        // PR so callers get the full updated record.
        var prResult = await _http
            .GetJsonAsync<GitHubPullRequestDto>(
                PullPath(request.Owner, request.RepoName, request.PrNumber), ct)
            .ConfigureAwait(false);
        if (prResult is not PlatformResult<GitHubPullRequestDto>.Ok prOk)
        {
            return prResult.Map(MapPullRequest);
        }
        var nodeId = prOk.Value.NodeId;
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return PlatformResult<PullRequest>.FromError(
                new PlatformError.InvalidRequest(
                    "invalid_request",
                    $"could not resolve node_id for PR #{request.PrNumber}"));
        }

        var mutation = request.Draft
            ? "mutation($pullRequestId: ID!) { convertPullRequestToDraft(input: { pullRequestId: $pullRequestId }) { pullRequest { isDraft state number } } }"
            : "mutation($pullRequestId: ID!) { markPullRequestReadyForReview(input: { pullRequestId: $pullRequestId }) { pullRequest { isDraft state number } } }";
        var gql = await _http
            .PostGraphQlAsync(mutation, new { pullRequestId = nodeId }, ct)
            .ConfigureAwait(false);
        if (gql is PlatformResult<System.Text.Json.JsonElement>.Failed gqlFailed)
        {
            return PlatformResult<PullRequest>.FromError(gqlFailed.Error);
        }
        if (gql is PlatformResult<System.Text.Json.JsonElement>.ServiceUnavailable)
        {
            return PlatformResult<PullRequest>.FromServiceUnavailable();
        }

        return await GetPullRequestAsync(
            request.Owner, request.RepoName, request.PrNumber, ct).ConfigureAwait(false);
    }

    // ================================================================
    // Epic 31 P1 — the loop verbs (issue lifecycle, releases,
    // review-comment listing, commit reads), absorbed from the live
    // CloseGitHubIssueAsync / AddIssueLabelsAsync / RemoveIssueLabelAsync /
    // CreateGitHubReleaseAsync / GetPullRequestReviewCommentsAsync /
    // GetGitHubCommitsAsync / GetGitHubFileChangesAsync.
    // ================================================================

    /// <inheritdoc />
    public async Task<PlatformResult<Issue>> CloseIssueAsync(
        string owner, string repoName, string issueNumber, string? comment = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(issueNumber);

        // Optional closing comment first — best-effort like the live
        // path (a failed comment must not block the close).
        if (!string.IsNullOrEmpty(comment))
        {
            var commentResult = await _http
                .PostJsonAsync<GitHubCommentDto>(
                    $"{IssuePath(owner, repoName, issueNumber)}/comments",
                    new { body = comment }, ct)
                .ConfigureAwait(false);
            if (commentResult is not PlatformResult<GitHubCommentDto>.Ok)
            {
                _logger.LogWarning(
                    "Closing comment on issue #{Number} in {Owner}/{Repo} failed (continuing with close)",
                    issueNumber, owner, repoName);
            }
        }

        var result = await _http
            .PatchJsonAsync<GitHubIssueDto>(
                IssuePath(owner, repoName, issueNumber), new { state = "closed" }, ct)
            .ConfigureAwait(false);
        return result.Map(MapIssue);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<IReadOnlyList<string>>> AddIssueLabelsAsync(
        AddIssueLabelsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = $"{IssuePath(request.Owner, request.RepoName, request.IssueNumber)}/labels";
        var result = await _http
            .PostJsonAsync<List<GitHubLabelDto>>(path, new { labels = request.Labels }, ct)
            .ConfigureAwait(false);
        return result.Map(MapLabelNames);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<IReadOnlyList<string>>> RemoveIssueLabelAsync(
        string owner, string repoName, string issueNumber, string label,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(issueNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var path = $"{IssuePath(owner, repoName, issueNumber)}/labels/{Encode(label)}";
        var result = await _http.DeleteJsonAsync<List<GitHubLabelDto>>(path, ct).ConfigureAwait(false);
        if (result is PlatformResult<List<GitHubLabelDto>>.Failed f
            && f.Error is PlatformError.NotFound)
        {
            // Removing an absent label is idempotent success (interface
            // contract + the live path's 404-tolerant posture). Return
            // the issue's current label set.
            var current = await _http
                .GetJsonAsync<List<GitHubLabelDto>>(
                    $"{IssuePath(owner, repoName, issueNumber)}/labels?per_page={PageSize}", ct)
                .ConfigureAwait(false);
            return current.Map(MapLabelNames);
        }
        return result.Map(MapLabelNames);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<Release>> CreateReleaseAsync(
        CreateReleaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TagName))
        {
            return PlatformResult<Release>.FromError(
                new PlatformError.InvalidRequest(
                    "invalid_request", "a non-empty release tag is required"));
        }

        var path = $"/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}/releases";
        // target_commitish only when supplied — GitHub defaults it to
        // the repo's default branch and ignores it when the tag already
        // exists (the absorbed live-path comment).
        var body = new Dictionary<string, object?>
        {
            ["tag_name"] = request.TagName,
            ["name"] = string.IsNullOrWhiteSpace(request.Name) ? request.TagName : request.Name,
            ["body"] = request.Body ?? string.Empty,
            ["draft"] = request.Draft,
            ["prerelease"] = request.Prerelease,
        };
        if (!string.IsNullOrWhiteSpace(request.TargetCommitish))
        {
            body["target_commitish"] = request.TargetCommitish;
        }

        var result = await _http.PostJsonAsync<GitHubReleaseDto>(path, body, ct).ConfigureAwait(false);
        return result.Map(dto => new Release(
            Id: dto.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            TagName: dto.TagName ?? request.TagName,
            Name: dto.Name ?? request.TagName,
            HtmlUrl: dto.HtmlUrl ?? string.Empty,
            Draft: dto.Draft,
            Prerelease: dto.Prerelease));
    }

    /// <inheritdoc />
    public async Task<PlatformResult<IReadOnlyList<PullRequestReviewComment>>> ListPullRequestReviewCommentsAsync(
        string owner, string repoName, string prNumber, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(prNumber);

        var aggregate = new List<PullRequestReviewComment>();
        var page = 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var path = $"{PullPath(owner, repoName, prNumber)}/comments?per_page={PageSize}&page={page}";
            var result = await _http.GetJsonAsync<List<GitHubCommentDto>>(path, ct).ConfigureAwait(false);
            switch (result)
            {
                case PlatformResult<List<GitHubCommentDto>>.Ok ok:
                    foreach (var c in ok.Value)
                    {
                        aggregate.Add(new PullRequestReviewComment(
                            Id: c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            Body: c.Body ?? string.Empty,
                            AuthorLogin: c.User?.Login ?? "unknown",
                            CreatedAt: c.CreatedAt,
                            Path: c.Path,
                            Line: c.Line));
                    }
                    if (ok.Value.Count < PageSize)
                    {
                        return PlatformResult<IReadOnlyList<PullRequestReviewComment>>.FromOk(aggregate);
                    }
                    page++;
                    break;
                case PlatformResult<List<GitHubCommentDto>>.Failed failed:
                    return PlatformResult<IReadOnlyList<PullRequestReviewComment>>.FromError(failed.Error);
                case PlatformResult<List<GitHubCommentDto>>.ServiceUnavailable:
                    return PlatformResult<IReadOnlyList<PullRequestReviewComment>>.FromServiceUnavailable();
                default:
                    throw new InvalidOperationException("unhandled result variant");
            }
        }
    }

    /// <inheritdoc />
    public async Task<PlatformResult<IReadOnlyList<Commit>>> ListCommitsAsync(
        ListCommitsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = $"/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}" +
                   $"/commits?sha={Encode(request.Ref)}&per_page={CommitPageSize}";
        if (request.Since is { } since)
        {
            path += $"&since={Encode(since.UtcDateTime.ToString("O"))}";
        }
        var result = await _http.GetJsonAsync<List<GitHubCommitDto>>(path, ct).ConfigureAwait(false);
        return result.Map(list =>
        {
            IReadOnlyList<Commit> commits = list
                .Select(c => new Commit(
                    Sha: c.Sha ?? string.Empty,
                    Message: c.Commit?.Message ?? string.Empty,
                    AuthorName: c.Commit?.Author?.Name ?? string.Empty,
                    Timestamp: c.Commit?.Author?.Date ?? default))
                .ToList();
            return commits;
        });
    }

    /// <inheritdoc />
    public async Task<PlatformResult<IReadOnlyList<PrFile>>> ListBranchFileChangesAsync(
        ListBranchFileChangesRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Resolve the base ref: explicit, else the repo's default
        // branch (the interface contract; supersedes the live path's
        // main-then-master guess, which broke on repos with any other
        // default branch).
        var baseRef = request.BaseRef;
        if (string.IsNullOrWhiteSpace(baseRef))
        {
            var repoResult = await GetRepoAsync(request.Owner, request.RepoName, ct)
                .ConfigureAwait(false);
            switch (repoResult)
            {
                case PlatformResult<Repo>.Ok repoOk:
                    baseRef = repoOk.Value.DefaultBranch;
                    break;
                case PlatformResult<Repo>.Failed repoFailed:
                    return PlatformResult<IReadOnlyList<PrFile>>.FromError(repoFailed.Error);
                default:
                    return PlatformResult<IReadOnlyList<PrFile>>.FromServiceUnavailable();
            }
        }

        var path = $"/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}" +
                   $"/compare/{Encode(baseRef)}...{Encode(request.Branch)}";
        var result = await _http.GetJsonAsync<GitHubCompareDto>(path, ct).ConfigureAwait(false);
        return result.Map(dto =>
        {
            IReadOnlyList<PrFile> files = (dto.Files ?? [])
                .Select(MapPrFile)
                .ToList();
            return files;
        });
    }

    // ================================================================
    // Epic 31 P3 (seam 5) — the engine-callback verbs: issue listing,
    // issue creation, security-alert reads. Absorbed from
    // OctokitGitHubEngineCallbackService (Octokit dropped — plain REST
    // over GitHubHttpClient, same layering as everything above).
    // ================================================================

    /// <inheritdoc />
    public async Task<PlatformResult<IReadOnlyList<Issue>>> ListIssuesAsync(
        ListIssuesRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var perPage = Math.Clamp(request.PerPage, 1, 100);
        var page = Math.Max(1, request.Page);
        var state = request.State?.ToLowerInvariant() switch
        {
            "closed" => "closed",
            "all" => "all",
            _ => "open",
        };
        var path = $"/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}" +
                   $"/issues?state={state}&per_page={perPage}&page={page}";
        if (request.Labels is { Count: > 0 } labels)
        {
            path += $"&labels={Encode(string.Join(",", labels))}";
        }

        var result = await _http.GetJsonAsync<List<GitHubIssueDto>>(path, ct).ConfigureAwait(false);
        return result.Map(list =>
        {
            // GitHub's issues endpoint returns PRs too — a row with a
            // pull_request stanza is a PR, not an issue (the absorbed
            // Octokit filter).
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

        var body = new
        {
            title = request.Title,
            body = request.Body,
            labels = request.Labels ?? (IReadOnlyList<string>)[],
            assignees = request.Assignees ?? (IReadOnlyList<string>)[],
        };
        var result = await _http
            .PostJsonAsync<GitHubIssueDto>(
                $"/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}/issues", body, ct)
            .ConfigureAwait(false);
        return result.Map(MapIssue);
    }

    /// <inheritdoc />
    public async Task<PlatformResult<SecurityAlerts>> ListSecurityAlertsAsync(
        string owner, string repoName, string alertType, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);

        var wantDependabot = alertType is "dependabot" or "all";
        var wantCodeScanning = alertType is "codeql" or "codeScanning" or "all";

        var dependabot = wantDependabot
            ? await FetchAlertsAsync($"/repos/{Encode(owner)}/{Encode(repoName)}/dependabot/alerts?state=open&per_page=100", ct)
                .ConfigureAwait(false)
            : [];
        var codeScanning = wantCodeScanning
            ? await FetchAlertsAsync($"/repos/{Encode(owner)}/{Encode(repoName)}/code-scanning/alerts?state=open&per_page=100", ct)
                .ConfigureAwait(false)
            : [];

        return PlatformResult<SecurityAlerts>.FromOk(new SecurityAlerts(dependabot, codeScanning));
    }

    /// <summary>Per-scanner fetch — a repo with the scanner disabled (403/404)
    /// contributes an EMPTY list rather than failing the read (the absorbed
    /// per-scanner tolerance).</summary>
    private async Task<IReadOnlyList<string>> FetchAlertsAsync(string path, CancellationToken ct)
    {
        var result = await _http
            .GetJsonAsync<List<System.Text.Json.JsonElement>>(path, ct)
            .ConfigureAwait(false);
        if (result is PlatformResult<List<System.Text.Json.JsonElement>>.Ok ok)
        {
            return ok.Value.Select(e => e.GetRawText()).ToList();
        }
        _logger.LogWarning(
            "Security-alert read failed for {Path} — scanner disabled or inaccessible; returning empty for that scanner",
            path);
        return [];
    }

    // ================================================================
    // Comments / webhooks / repo listing
    // ================================================================

    /// <inheritdoc />
    public async Task<PlatformResult<IssueComment>> CreateIssueCommentAsync(
        string owner, string repoName, string issueOrPrNumber, string body,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(issueOrPrNumber);
        ArgumentNullException.ThrowIfNull(body);

        var path = $"{IssuePath(owner, repoName, issueOrPrNumber)}/comments";
        var result = await _http
            .PostJsonAsync<GitHubCommentDto>(path, new { body }, ct)
            .ConfigureAwait(false);
        return result.Map(MapIssueComment);
    }

    /// <inheritdoc />
    /// <remarks>GitHub issues and PRs share one number space and one
    /// discussion-comment surface (<c>/issues/{n}/comments</c>), so the PR
    /// verb IS the issue verb here — the split exists for GitLab, whose MR
    /// iids are a separate sequence.</remarks>
    public Task<PlatformResult<IssueComment>> CreatePullRequestCommentAsync(
        string owner, string repoName, string prNumber, string body,
        CancellationToken ct = default) =>
        CreateIssueCommentAsync(owner, repoName, prNumber, body, ct);

    /// <inheritdoc />
    public async Task<PlatformResult<WebhookRegistration>> RegisterWebhookAsync(
        RegisterWebhookRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = $"/repos/{Encode(request.Owner)}/{Encode(request.RepoName)}/hooks";
        var body = new
        {
            name = "web",
            active = request.Active,
            events = request.Events,
            config = new
            {
                url = request.DeliveryUrl,
                content_type = "json",
                secret = request.Secret,
            },
        };
        var result = await _http.PostJsonAsync<GitHubHookDto>(path, body, ct).ConfigureAwait(false);
        return result.Map(dto => new WebhookRegistration(
            Id: dto.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Url: dto.Config?.Url ?? request.DeliveryUrl,
            Events: dto.Events ?? new List<string>(request.Events),
            Active: dto.Active));
    }

    /// <inheritdoc />
    /// <remarks>
    /// PAT mode pages <c>GET /user/repos</c>; App mode pages
    /// <c>GET /installation/repositories</c> (the App-token surface —
    /// what <c>OctokitGitHubAppClient.ListInstallationReposAsync</c>
    /// did). Unlike the platform-error verbs, this enumeration THROWS
    /// <see cref="GitHubPlatformApiException"/> on failure — silently
    /// yielding nothing is the vacuous-probe bug (a junk token must
    /// fail onboarding, not connect).
    /// </remarks>
    public async IAsyncEnumerable<Repo> ListAccessibleReposAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var page = 1;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            List<GitHubRepoDto> batch;
            if (_appMode)
            {
                var path = $"/installation/repositories?per_page={PageSize}&page={page}";
                var result = await _http
                    .GetJsonAsync<GitHubInstallationReposDto>(path, ct)
                    .ConfigureAwait(false);
                batch = result switch
                {
                    PlatformResult<GitHubInstallationReposDto>.Ok ok =>
                        ok.Value.Repositories ?? [],
                    PlatformResult<GitHubInstallationReposDto>.Failed failed =>
                        throw new GitHubPlatformApiException(
                            $"GitHub accessible-repos listing failed: {failed.Error.GetType().Name}",
                            failed.Error),
                    _ => throw new GitHubPlatformApiException(
                        "GitHub accessible-repos listing failed: driver could not reach the platform",
                        new PlatformError.ServiceUnavailable()),
                };
            }
            else
            {
                var path = $"/user/repos?per_page={PageSize}&page={page}";
                var result = await _http
                    .GetJsonAsync<List<GitHubRepoDto>>(path, ct)
                    .ConfigureAwait(false);
                batch = result switch
                {
                    PlatformResult<List<GitHubRepoDto>>.Ok ok => ok.Value,
                    PlatformResult<List<GitHubRepoDto>>.Failed failed =>
                        throw new GitHubPlatformApiException(
                            $"GitHub accessible-repos listing failed: {failed.Error.GetType().Name}",
                            failed.Error),
                    _ => throw new GitHubPlatformApiException(
                        "GitHub accessible-repos listing failed: driver could not reach the platform",
                        new PlatformError.ServiceUnavailable()),
                };
            }

            foreach (var dto in batch)
            {
                yield return MapRepo(dto);
            }
            if (batch.Count < PageSize) yield break;
            page++;
        }
    }

    // ================================================================
    // Mapping helpers
    // ================================================================

    private static string PullPath(string owner, string repoName, string prNumber) =>
        $"/repos/{Encode(owner)}/{Encode(repoName)}/pulls/{Encode(prNumber)}";

    private static string IssuePath(string owner, string repoName, string issueNumber) =>
        $"/repos/{Encode(owner)}/{Encode(repoName)}/issues/{Encode(issueNumber)}";

    internal Repo MapRepo(GitHubRepoDto dto)
    {
        var owner = dto.Owner?.Login
            ?? dto.FullName?.Split('/').FirstOrDefault()
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

    internal static Branch MapBranch(GitHubBranchDto dto) => new(
        Name: dto.Name ?? string.Empty,
        Sha: dto.Commit?.Sha ?? string.Empty,
        Protected: dto.Protected);

    internal static byte[] DecodeContents(GitHubContentsDto dto)
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
                // GitHub base64 bodies carry embedded newlines.
                return Convert.FromBase64String(dto.Content.Replace("\n", string.Empty));
            }
            catch (FormatException)
            {
                return Array.Empty<byte>();
            }
        }
        return string.IsNullOrEmpty(dto.Content)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(dto.Content);
    }

    internal static PullRequest MapPullRequest(GitHubPullRequestDto dto)
    {
        var merged = dto.Merged || dto.MergedAt is not null;
        var state = (dto.State?.ToLowerInvariant(), merged) switch
        {
            (_, true) => PullRequestState.Merged,
            ("closed", _) => PullRequestState.Closed,
            _ => PullRequestState.Open,
        };
        return new PullRequest(
            Number: dto.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Title: dto.Title ?? string.Empty,
            Body: dto.Body,
            SourceBranch: dto.Head?.Ref ?? string.Empty,
            TargetBranch: dto.Base?.Ref ?? string.Empty,
            State: state,
            IsDraft: dto.Draft,
            HtmlUrl: dto.HtmlUrl ?? string.Empty,
            AuthorLogin: dto.User?.Login ?? "unknown",
            CreatedAt: dto.CreatedAt,
            UpdatedAt: dto.UpdatedAt)
        {
            // Epic 31 P2 — merge read-backs consumed by the retyped
            // merge core (idempotency + confirmed-conflict gate).
            MergeCommitSha = dto.MergeCommitSha,
            Mergeable = dto.Mergeable,
            MergeableState = dto.MergeableState,
        };
    }

    internal static PrFile MapPrFile(GitHubPrFileDto dto)
    {
        var status = dto.Status?.ToLowerInvariant() switch
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

    internal static IssueComment MapIssueComment(GitHubCommentDto dto) => new(
        Id: dto.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Body: dto.Body ?? string.Empty,
        AuthorLogin: dto.User?.Login ?? "unknown",
        CreatedAt: dto.CreatedAt);

    internal static Issue MapIssue(GitHubIssueDto dto) => new(
        Number: dto.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Title: dto.Title ?? string.Empty,
        Body: dto.Body,
        State: string.Equals(dto.State, "closed", StringComparison.OrdinalIgnoreCase)
            ? IssueState.Closed
            : IssueState.Open,
        HtmlUrl: dto.HtmlUrl ?? string.Empty,
        Labels: MapLabelNames(dto.Labels ?? []));

    private static IReadOnlyList<string> MapLabelNames(List<GitHubLabelDto> labels) =>
        labels
            .Select(l => l.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .ToList();

    private static string Encode(string segment) => Uri.EscapeDataString(segment);

    private static string EncodePath(string path)
    {
        // Slashes stay literal in contents paths — escape per segment.
        var parts = path.Split('/', StringSplitOptions.None);
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = Uri.EscapeDataString(parts[i]);
        }
        return string.Join('/', parts).TrimStart('/');
    }
}
