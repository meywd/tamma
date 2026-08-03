using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Tamma.Core.Interfaces;

namespace Tamma.Api.Services;

/// <summary>
/// GitHub integration service — branch, commit, PR, and file-change operations.
/// </summary>
public class GitHubIntegrationService : IGitHubIntegrationService
{
    private readonly ILogger<GitHubIntegrationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Story 38-1 — when set, this request-scoped token overrides the named
    /// "github" client's static <c>GitHub:Token</c> bearer for EVERY call made
    /// through this instance, so the platform call uses the per-tenant token the
    /// mediation layer resolved (the "token used == token resolved" invariant).
    /// Minted per-request by <see cref="Git.GitHubClientFactory"/>; the token is
    /// never logged, returned, or persisted. Null ⇒ legacy static-token behaviour.
    /// </summary>
    private readonly string? _tokenOverride;

    public GitHubIntegrationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GitHubIntegrationService> logger)
        : this(httpClientFactory, configuration, logger, tokenOverride: null)
    {
    }

    /// <summary>
    /// Story 38-1 — construct bound to a request-scoped <paramref name="tokenOverride"/>.
    /// Used by <see cref="Git.GitHubClientFactory"/> so the git-mediation endpoints
    /// perform the platform call with the resolved per-tenant token.
    /// </summary>
    public GitHubIntegrationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GitHubIntegrationService> logger,
        string? tokenOverride)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _tokenOverride = tokenOverride;
    }

    /// <summary>
    /// Create the named "github" <see cref="HttpClient"/>, applying the
    /// request-scoped <see cref="_tokenOverride"/> when present. The factory
    /// returns a fresh client instance per call, so mutating its default
    /// Authorization header is scoped to this instance only.
    /// </summary>
    private HttpClient CreateGitHubClient()
    {
        var client = _httpClientFactory.CreateClient("github");
        if (!string.IsNullOrWhiteSpace(_tokenOverride))
        {
            client.DefaultRequestHeaders.Remove("Authorization");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _tokenOverride);
        }
        return client;
    }

    public Task<IntegrationResult<GitHubBranchResult>> CreateGitHubBranchAsync(string repository, string branchName)
        => CreateGitHubBranchAsync(repository, branchName, baseBranch: null);

    public async Task<IntegrationResult<GitHubBranchResult>> CreateGitHubBranchAsync(string repository, string branchName, string? baseBranch)
    {
        var httpClient = CreateGitHubClient();
        // A request-scoped override (BYOK, resolved by the mediation layer) IS the
        // token; only require the static GitHub:Token when no override is bound.
        var token = _tokenOverride ?? _configuration["GitHub:Token"];
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("GitHub token not configured");
            return IntegrationResult<GitHubBranchResult>.Fail("GitHub token not configured");
        }

        try
        {
            _logger.LogInformation(
                "Creating branch {Branch} in {Repo} from base {Base}",
                branchName, repository, string.IsNullOrWhiteSpace(baseBranch) ? "<default>" : baseBranch);

            // Resolve the base SHA. With an explicit base, resolve THAT ref and do
            // NOT silently fall back to an unrelated branch (no false success).
            // Without one, use the default-branch behaviour (main → master).
            // Use the EXACT-MATCH singular `git/ref/heads/{ref}` endpoint — it
            // returns a single ref object on an exact hit and a real 404 on a
            // miss. The plural `git/refs/heads/{ref}` is the prefix-matching
            // list form: it 200s with a JSON ARRAY for any ref a sibling STARTS
            // WITH (e.g. `develop` when `develop-2` exists), which both breaks
            // the single-object parse below and masks a true miss.
            string sha;
            if (!string.IsNullOrWhiteSpace(baseBranch))
            {
                var baseResponse = await httpClient.GetAsync($"/repos/{repository}/git/ref/heads/{Uri.EscapeDataString(baseBranch)}");
                if (baseResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogError("Base branch {Base} not found in {Repo}", baseBranch, repository);
                    return IntegrationResult<GitHubBranchResult>.Fail($"base_branch_not_found: {baseBranch}");
                }
                if (!baseResponse.IsSuccessStatusCode)
                    return IntegrationResult<GitHubBranchResult>.Fail(
                        $"{(int)baseResponse.StatusCode}: failed to resolve base {baseBranch}");

                var baseData = await baseResponse.Content.ReadFromJsonAsync<JsonElement>();
                sha = baseData.GetProperty("object").GetProperty("sha").GetString()!;
            }
            else
            {
                var refsResponse = await httpClient.GetAsync($"/repos/{repository}/git/ref/heads/main");
                if (!refsResponse.IsSuccessStatusCode)
                    refsResponse = await httpClient.GetAsync($"/repos/{repository}/git/ref/heads/master");
                if (refsResponse.StatusCode == HttpStatusCode.NotFound)
                    return IntegrationResult<GitHubBranchResult>.Fail("base_branch_not_found: main/master");
                refsResponse.EnsureSuccessStatusCode();

                var refData = await refsResponse.Content.ReadFromJsonAsync<JsonElement>();
                sha = refData.GetProperty("object").GetProperty("sha").GetString()!;
            }

            // Create the branch
            var createPayload = new
            {
                @ref = $"refs/heads/{branchName}",
                sha
            };
            var createResponse = await httpClient.PostAsJsonAsync($"/repos/{repository}/git/refs", createPayload);
            if (!createResponse.IsSuccessStatusCode)
            {
                // Surface the status code + body so the activity can classify the
                // failure (permission / already-exists / protected-base) rather
                // than collapsing every error into a bare throw.
                var body = await createResponse.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Failed to create branch {Branch} in {Repo}: {Status} {Body}",
                    branchName, repository, (int)createResponse.StatusCode, body);
                return IntegrationResult<GitHubBranchResult>.Fail($"{(int)createResponse.StatusCode}: {body}");
            }

            var result = new GitHubBranchResult
            {
                Success = true,
                BranchName = branchName,
                BranchUrl = $"https://github.com/{repository}/tree/{branchName}",
                BaseSha = sha
            };
            return IntegrationResult<GitHubBranchResult>.Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create GitHub branch {Branch}", branchName);
            return IntegrationResult<GitHubBranchResult>.Fail(ex.Message);
        }
    }

    public async Task<IntegrationResult<bool>> BranchExistsAsync(string repository, string branchName)
    {
        var httpClient = CreateGitHubClient();

        try
        {
            // Exact-match singular `git/ref/heads/{branch}` — single object on an
            // exact hit, 404 on a true miss. The plural prefix-matching form would
            // 200 on a sibling ref (e.g. `adl/42-auth` when only `adl/42-auth-2`
            // exists) → a false conflict / bogus suffix. This 200/404 contract is
            // exactly what the branches below are coded against.
            var response = await httpClient.GetAsync($"/repos/{repository}/git/ref/heads/{Uri.EscapeDataString(branchName)}");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return IntegrationResult<bool>.Ok(false);
            if (response.IsSuccessStatusCode)
                return IntegrationResult<bool>.Ok(true);

            // Any other status (403/5xx/...) is an error — NOT "absent", so we
            // never mistake a transient failure for a free-to-create branch.
            var body = await response.Content.ReadAsStringAsync();
            return IntegrationResult<bool>.Fail($"{(int)response.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check branch existence {Branch} in {Repo}", branchName, repository);
            return IntegrationResult<bool>.Fail(ex.Message);
        }
    }

    public async Task<IntegrationResult<List<GitHubCommit>>> GetGitHubCommitsAsync(string repository, string branch, DateTime? since = null)
    {
        var httpClient = CreateGitHubClient();

        try
        {
            var url = $"/repos/{repository}/commits?sha={branch}&per_page=20";
            if (since.HasValue)
            {
                url += $"&since={since.Value:O}";
            }

            var response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var commits = await response.Content.ReadFromJsonAsync<JsonElement>();
            var results = new List<GitHubCommit>();

            foreach (var commit in commits.EnumerateArray())
            {
                var commitData = commit.GetProperty("commit");
                results.Add(new GitHubCommit
                {
                    Sha = commit.GetProperty("sha").GetString() ?? "",
                    Message = commitData.GetProperty("message").GetString() ?? "",
                    Author = commitData.GetProperty("author").GetProperty("name").GetString() ?? "",
                    Timestamp = commitData.GetProperty("author").GetProperty("date").GetDateTime(),
                    Files = new List<string>()
                });
            }

            return IntegrationResult<List<GitHubCommit>>.Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get GitHub commits for {Repo}/{Branch}", repository, branch);
            return IntegrationResult<List<GitHubCommit>>.Fail(ex.Message);
        }
    }

    public async Task<IntegrationResult<GitHubPullRequestResult>> CreateGitHubPullRequestAsync(string repository, CreatePullRequestRequest request)
    {
        var httpClient = CreateGitHubClient();

        try
        {
            _logger.LogInformation("Creating PR in {Repo}: {Title}", repository, request.Title);

            var payload = new
            {
                title = request.Title,
                body = request.Body,
                head = request.Head,
                @base = request.Base,
                draft = request.IsDraft
            };

            var response = await httpClient.PostAsJsonAsync($"/repos/{repository}/pulls", payload);
            response.EnsureSuccessStatusCode();

            var pr = await response.Content.ReadFromJsonAsync<JsonElement>();
            var prNumber = pr.GetProperty("number").GetInt32();

            // Best-effort post-create metadata: labels + reviewers. Failures here
            // MUST NOT fail PR creation (Story 2.8 AC3 — degrade gracefully).
            await TryAddLabelsAsync(httpClient, repository, prNumber, request.Labels);
            await TryRequestReviewersAsync(httpClient, repository, prNumber, request.Reviewers);

            var result = new GitHubPullRequestResult
            {
                Success = true,
                Number = prNumber,
                Url = pr.GetProperty("html_url").GetString() ?? ""
            };
            return IntegrationResult<GitHubPullRequestResult>.Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create GitHub PR in {Repo}", repository);
            return IntegrationResult<GitHubPullRequestResult>.Fail(ex.Message);
        }
    }

    public async Task<IntegrationResult<GitHubPullRequestRef?>> GetGitHubOpenPullRequestForBranchAsync(string repository, string headBranch, string baseBranch)
    {
        var httpClient = CreateGitHubClient();

        try
        {
            // GitHub's `head` filter wants `owner:branch`. Derive the owner from
            // the `owner/repo` repository string so a fork-less same-repo PR matches.
            var owner = repository.Contains('/') ? repository.Split('/')[0] : "";
            var headFilter = string.IsNullOrEmpty(owner) ? headBranch : $"{owner}:{headBranch}";
            var url = $"/repos/{repository}/pulls?state=open&head={Uri.EscapeDataString(headFilter)}&base={Uri.EscapeDataString(baseBranch)}&per_page=1";

            var response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadFromJsonAsync<JsonElement>();
            foreach (var pr in data.EnumerateArray())
            {
                return IntegrationResult<GitHubPullRequestRef?>.Ok(new GitHubPullRequestRef
                {
                    Number = pr.GetProperty("number").GetInt32(),
                    Url = pr.GetProperty("html_url").GetString() ?? "",
                    State = pr.GetProperty("state").GetString() ?? "open",
                    Title = pr.GetProperty("title").GetString() ?? "",
                    IsDraft = pr.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True
                });
            }

            return IntegrationResult<GitHubPullRequestRef?>.Ok(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to look up open PR for {Repo} {Head}->{Base}", repository, headBranch, baseBranch);
            return IntegrationResult<GitHubPullRequestRef?>.Fail(ex.Message);
        }
    }

    public async Task<IntegrationResult<GitHubPullRequestResult>> UpdateGitHubPullRequestAsync(string repository, int pullRequestNumber, CreatePullRequestRequest request)
    {
        var httpClient = CreateGitHubClient();

        try
        {
            _logger.LogInformation("Updating PR #{Number} in {Repo}", pullRequestNumber, repository);

            var payload = new { title = request.Title, body = request.Body };
            var response = await httpClient.PatchAsJsonAsync($"/repos/{repository}/pulls/{pullRequestNumber}", payload);
            response.EnsureSuccessStatusCode();

            var pr = await response.Content.ReadFromJsonAsync<JsonElement>();

            await TryAddLabelsAsync(httpClient, repository, pullRequestNumber, request.Labels);
            await TryRequestReviewersAsync(httpClient, repository, pullRequestNumber, request.Reviewers);

            return IntegrationResult<GitHubPullRequestResult>.Ok(new GitHubPullRequestResult
            {
                Success = true,
                Number = pr.GetProperty("number").GetInt32(),
                Url = pr.GetProperty("html_url").GetString() ?? ""
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update GitHub PR #{Number} in {Repo}", pullRequestNumber, repository);
            return IntegrationResult<GitHubPullRequestResult>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Best-effort label assignment. Reviewer / label failures must not fail PR
    /// creation (Story 2.8 AC3) — log a warning and continue.
    /// </summary>
    private async Task TryAddLabelsAsync(HttpClient httpClient, string repository, int prNumber, List<string> labels)
    {
        if (labels is not { Count: > 0 }) return;
        try
        {
            // Labels are attached via the shared issues endpoint (a PR IS an issue).
            var resp = await httpClient.PostAsJsonAsync(
                $"/repos/{repository}/issues/{prNumber}/labels", new { labels });
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("Label assignment for PR #{Number} returned {Status}", prNumber, resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to add labels to PR #{Number} (continuing)", prNumber);
        }
    }

    /// <summary>
    /// Best-effort reviewer assignment. Failures must not fail PR creation.
    /// </summary>
    private async Task TryRequestReviewersAsync(HttpClient httpClient, string repository, int prNumber, List<string> reviewers)
    {
        if (reviewers is not { Count: > 0 }) return;
        try
        {
            var resp = await httpClient.PostAsJsonAsync(
                $"/repos/{repository}/pulls/{prNumber}/requested_reviewers", new { reviewers });
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("Reviewer request for PR #{Number} returned {Status}", prNumber, resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to request reviewers for PR #{Number} (continuing)", prNumber);
        }
    }

    public Task<IntegrationResult<GitHubMergeResult>> MergeGitHubPullRequestAsync(string repository, int pullRequestNumber)
        => MergeGitHubPullRequestAsync(repository, pullRequestNumber, "squash");

    public async Task<IntegrationResult<GitHubMergeResult>> MergeGitHubPullRequestAsync(string repository, int pullRequestNumber, string mergeStrategy)
    {
        var httpClient = CreateGitHubClient();

        try
        {
            var method = NormalizeMergeMethod(mergeStrategy);
            _logger.LogInformation(
                "Merging PR #{Number} in {Repo} (method {Method})", pullRequestNumber, repository, method);

            var payload = new { merge_method = method };
            var response = await httpClient.PutAsJsonAsync(
                $"/repos/{repository}/pulls/{pullRequestNumber}/merge", payload);
            if (!response.IsSuccessStatusCode)
            {
                // Surface the status code + body so the merge activity can classify
                // the failure (409 conflict / 403 permission / 405 not-mergeable /
                // 422 branch-protected) rather than collapsing every error into a
                // bare throw → blind Error outcome.
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Failed to merge PR #{Number} in {Repo}: {Status} {Body}",
                    pullRequestNumber, repository, (int)response.StatusCode, body);
                return IntegrationResult<GitHubMergeResult>.Fail($"{(int)response.StatusCode}: {body}");
            }

            var data = await response.Content.ReadFromJsonAsync<JsonElement>();
            var result = new GitHubMergeResult
            {
                Success = data.TryGetProperty("merged", out var m) && m.GetBoolean(),
                MergeSha = data.TryGetProperty("sha", out var s) ? s.GetString() ?? "" : ""
            };
            return IntegrationResult<GitHubMergeResult>.Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to merge GitHub PR #{Number}", pullRequestNumber);
            return IntegrationResult<GitHubMergeResult>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Map a Tamma merge strategy (<c>merge | squash | rebase</c>) to GitHub's
    /// <c>merge_method</c>; an unknown / empty value falls back to <c>squash</c>
    /// (the platform default).
    /// </summary>
    internal static string NormalizeMergeMethod(string? strategy)
        => (strategy ?? "").Trim().ToLowerInvariant() switch
        {
            "merge" => "merge",
            "rebase" => "rebase",
            _ => "squash",
        };

    public async Task<IntegrationResult<GitHubPullRequestDetail>> GetGitHubPullRequestAsync(string repository, int pullRequestNumber)
    {
        var httpClient = CreateGitHubClient();

        try
        {
            var response = await httpClient.GetAsync($"/repos/{repository}/pulls/{pullRequestNumber}");
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Failed to read PR #{Number} in {Repo}: {Status} {Body}",
                    pullRequestNumber, repository, (int)response.StatusCode, body);
                return IntegrationResult<GitHubPullRequestDetail>.Fail($"{(int)response.StatusCode}: {body}");
            }

            var pr = await response.Content.ReadFromJsonAsync<JsonElement>();
            var detail = new GitHubPullRequestDetail
            {
                Number = pr.TryGetProperty("number", out var n) ? n.GetInt32() : pullRequestNumber,
                State = pr.TryGetProperty("state", out var st) ? st.GetString() ?? "open" : "open",
                Merged = pr.TryGetProperty("merged", out var mg) && mg.ValueKind == JsonValueKind.True,
                MergeCommitSha = pr.TryGetProperty("merge_commit_sha", out var ms) && ms.ValueKind == JsonValueKind.String
                    ? ms.GetString() : null,
                // `mergeable` is true/false/null (null = GitHub still computing).
                Mergeable = pr.TryGetProperty("mergeable", out var ma)
                    ? ma.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => (bool?)null,
                    }
                    : null,
                MergeableState = pr.TryGetProperty("mergeable_state", out var mst) && mst.ValueKind == JsonValueKind.String
                    ? mst.GetString() : null,
                IsDraft = pr.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True,
                // Story 43-12 — the base/target branch, for merge-target key selection.
                BaseBranch = pr.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.Object
                             && b.TryGetProperty("ref", out var br) && br.ValueKind == JsonValueKind.String
                    ? br.GetString() ?? string.Empty : string.Empty,
            };
            return IntegrationResult<GitHubPullRequestDetail>.Ok(detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read GitHub PR #{Number} in {Repo}", pullRequestNumber, repository);
            return IntegrationResult<GitHubPullRequestDetail>.Fail(ex.Message);
        }
    }

    public async Task<IntegrationResult<List<GitHubFileChange>>> GetGitHubFileChangesAsync(string repository, string branch)
    {
        var httpClient = CreateGitHubClient();

        try
        {
            // Compare branch against default branch
            var response = await httpClient.GetAsync(
                $"/repos/{repository}/compare/main...{branch}");
            if (!response.IsSuccessStatusCode)
            {
                response = await httpClient.GetAsync(
                    $"/repos/{repository}/compare/master...{branch}");
            }
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadFromJsonAsync<JsonElement>();
            var files = data.GetProperty("files");

            var results = new List<GitHubFileChange>();
            foreach (var file in files.EnumerateArray())
            {
                results.Add(new GitHubFileChange
                {
                    FilePath = file.GetProperty("filename").GetString() ?? "",
                    ChangeType = file.GetProperty("status").GetString() ?? "modified",
                    Additions = file.GetProperty("additions").GetInt32(),
                    Deletions = file.GetProperty("deletions").GetInt32()
                });
            }

            return IntegrationResult<List<GitHubFileChange>>.Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get file changes for {Repo}/{Branch}", repository, branch);
            return IntegrationResult<List<GitHubFileChange>>.Fail(ex.Message);
        }
    }

    public async Task<IntegrationResult<List<GitHubIssue>>> ListGitHubIssuesAsync(string repository, string[]? labels = null, string state = "open")
    {
        var httpClient = CreateGitHubClient();

        try
        {
            var url = $"/repos/{repository}/issues?state={state}&per_page=20";
            if (labels is { Length: > 0 })
                url += $"&labels={string.Join(",", labels)}";

            var response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadFromJsonAsync<JsonElement>();
            var results = new List<GitHubIssue>();

            foreach (var issue in data.EnumerateArray())
            {
                // Skip pull requests (GitHub returns PRs in the issues endpoint)
                if (issue.TryGetProperty("pull_request", out _))
                    continue;

                var issueLabels = new List<string>();
                if (issue.TryGetProperty("labels", out var labelsArr))
                {
                    foreach (var label in labelsArr.EnumerateArray())
                    {
                        var name = label.GetProperty("name").GetString();
                        if (name != null) issueLabels.Add(name);
                    }
                }

                results.Add(new GitHubIssue
                {
                    Number = issue.GetProperty("number").GetInt32(),
                    Title = issue.GetProperty("title").GetString() ?? "",
                    Body = issue.TryGetProperty("body", out var b) ? b.GetString() : null,
                    State = issue.GetProperty("state").GetString() ?? "open",
                    Labels = issueLabels,
                    Assignee = issue.TryGetProperty("assignee", out var a) && a.ValueKind != JsonValueKind.Null
                        ? a.GetProperty("login").GetString() : null,
                    CreatedAt = issue.GetProperty("created_at").GetDateTime(),
                    Url = issue.GetProperty("html_url").GetString() ?? ""
                });
            }

            return IntegrationResult<List<GitHubIssue>>.Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list issues for {Repo}", repository);
            return IntegrationResult<List<GitHubIssue>>.Fail(ex.Message);
        }
    }

    public async Task<IntegrationResult<bool>> AssignGitHubIssueAsync(string repository, int issueNumber, string assignee)
    {
        var httpClient = CreateGitHubClient();

        try
        {
            var payload = new { assignees = new[] { assignee } };
            var response = await httpClient.PostAsJsonAsync(
                $"/repos/{repository}/issues/{issueNumber}/assignees", payload);
            response.EnsureSuccessStatusCode();
            return IntegrationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to assign issue #{Number} in {Repo}", issueNumber, repository);
            return IntegrationResult<bool>.Fail(ex.Message);
        }
    }

    public async Task<IntegrationResult<bool>> CloseGitHubIssueAsync(string repository, int issueNumber, string? comment = null)
    {
        var httpClient = CreateGitHubClient();

        try
        {
            if (!string.IsNullOrEmpty(comment))
            {
                var commentPayload = new { body = comment };
                await httpClient.PostAsJsonAsync(
                    $"/repos/{repository}/issues/{issueNumber}/comments", commentPayload);
            }

            var payload = new { state = "closed" };
            var response = await httpClient.PatchAsJsonAsync(
                $"/repos/{repository}/issues/{issueNumber}", payload);
            response.EnsureSuccessStatusCode();
            return IntegrationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to close issue #{Number} in {Repo}", issueNumber, repository);
            return IntegrationResult<bool>.Fail(ex.Message);
        }
    }

    public async Task<IntegrationResult<bool>> DeleteGitHubBranchAsync(string repository, string branchName)
    {
        var httpClient = CreateGitHubClient();

        try
        {
            var response = await httpClient.DeleteAsync(
                $"/repos/{repository}/git/refs/heads/{branchName}");
            response.EnsureSuccessStatusCode();
            return IntegrationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete branch {Branch} in {Repo}", branchName, repository);
            return IntegrationResult<bool>.Fail(ex.Message);
        }
    }

    public async Task<IntegrationResult<List<GitHubReviewComment>>> GetPullRequestReviewCommentsAsync(string repository, int pullRequestNumber)
    {
        var httpClient = CreateGitHubClient();

        try
        {
            var response = await httpClient.GetAsync(
                $"/repos/{repository}/pulls/{pullRequestNumber}/comments");
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadFromJsonAsync<JsonElement>();
            var results = new List<GitHubReviewComment>();

            foreach (var comment in data.EnumerateArray())
            {
                results.Add(new GitHubReviewComment
                {
                    Id = comment.GetProperty("id").GetInt32(),
                    Body = comment.GetProperty("body").GetString() ?? "",
                    Path = comment.TryGetProperty("path", out var p) ? p.GetString() : null,
                    Line = comment.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null,
                    Author = comment.GetProperty("user").GetProperty("login").GetString() ?? "",
                    CreatedAt = comment.GetProperty("created_at").GetDateTime()
                });
            }

            return IntegrationResult<List<GitHubReviewComment>>.Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get review comments for PR #{Number} in {Repo}", pullRequestNumber, repository);
            return IntegrationResult<List<GitHubReviewComment>>.Fail(ex.Message);
        }
    }

    public async Task<IntegrationResult<bool>> PostIssueCommentAsync(string repository, int issueNumber, string body)
    {
        var httpClient = CreateGitHubClient();

        try
        {
            var response = await httpClient.PostAsJsonAsync(
                $"/repos/{repository}/issues/{issueNumber}/comments", new { body });
            if (!response.IsSuccessStatusCode)
            {
                var respBody = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Failed to post comment on issue #{Number} in {Repo}: {Status} {Body}",
                    issueNumber, repository, (int)response.StatusCode, respBody);
                return IntegrationResult<bool>.Fail($"{(int)response.StatusCode}: {respBody}");
            }
            return IntegrationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post comment on issue #{Number} in {Repo}", issueNumber, repository);
            return IntegrationResult<bool>.Fail(ex.Message);
        }
    }

    public async Task<IntegrationResult<bool>> AddIssueLabelsAsync(string repository, int issueNumber, string[] labels)
    {
        if (labels is not { Length: > 0 })
            return IntegrationResult<bool>.Ok(true);

        var httpClient = CreateGitHubClient();

        try
        {
            var response = await httpClient.PostAsJsonAsync(
                $"/repos/{repository}/issues/{issueNumber}/labels", new { labels });
            if (!response.IsSuccessStatusCode)
            {
                var respBody = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Failed to add labels to issue #{Number} in {Repo}: {Status} {Body}",
                    issueNumber, repository, (int)response.StatusCode, respBody);
                return IntegrationResult<bool>.Fail($"{(int)response.StatusCode}: {respBody}");
            }
            return IntegrationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add labels to issue #{Number} in {Repo}", issueNumber, repository);
            return IntegrationResult<bool>.Fail(ex.Message);
        }
    }

    public async Task<IntegrationResult<bool>> RemoveIssueLabelAsync(string repository, int issueNumber, string label)
    {
        var httpClient = CreateGitHubClient();

        try
        {
            var response = await httpClient.DeleteAsync(
                $"/repos/{repository}/issues/{issueNumber}/labels/{Uri.EscapeDataString(label)}");
            // A 404 means the label was not present — idempotently treat as removed.
            if (response.StatusCode == HttpStatusCode.NotFound)
                return IntegrationResult<bool>.Ok(true);
            if (!response.IsSuccessStatusCode)
            {
                var respBody = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Failed to remove label '{Label}' from issue #{Number} in {Repo}: {Status} {Body}",
                    label, issueNumber, repository, (int)response.StatusCode, respBody);
                return IntegrationResult<bool>.Fail($"{(int)response.StatusCode}: {respBody}");
            }
            return IntegrationResult<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove label '{Label}' from issue #{Number} in {Repo}", label, issueNumber, repository);
            return IntegrationResult<bool>.Fail(ex.Message);
        }
    }

    public async Task<IntegrationResult<GitHubReleaseResult>> CreateGitHubReleaseAsync(string repository, ReleaseCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var httpClient = CreateGitHubClient();
        // A request-scoped override (BYOK, resolved by the mediation layer) IS the
        // token; only require the static GitHub:Token when no override is bound.
        var token = _tokenOverride ?? _configuration["GitHub:Token"];
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("GitHub token not configured");
            return IntegrationResult<GitHubReleaseResult>.Fail("GitHub token not configured");
        }

        if (string.IsNullOrWhiteSpace(request.TagName))
            return IntegrationResult<GitHubReleaseResult>.Fail("a non-empty release tag is required");

        try
        {
            _logger.LogInformation("Creating release {Tag} in {Repo}", request.TagName, repository);

            // Snake-case GitHub REST payload. target_commitish is only sent when
            // supplied (GitHub defaults it to the repo's default branch otherwise,
            // and ignores it entirely when the tag already exists).
            var payload = new Dictionary<string, object?>
            {
                ["tag_name"] = request.TagName,
                ["name"] = string.IsNullOrWhiteSpace(request.Name) ? request.TagName : request.Name,
                ["body"] = request.Body ?? string.Empty,
                ["draft"] = request.Draft,
                ["prerelease"] = request.Prerelease,
            };
            if (!string.IsNullOrWhiteSpace(request.TargetCommitish))
                payload["target_commitish"] = request.TargetCommitish;

            var response = await httpClient.PostAsJsonAsync($"/repos/{repository}/releases", payload);
            if (!response.IsSuccessStatusCode)
            {
                // Surface the status code + body so the mediation layer can classify
                // the failure (422 tag exists / 403 permission / 404 repo) rather than
                // collapsing every error into a bare throw.
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "Failed to create release {Tag} in {Repo}: {Status} {Body}",
                    request.TagName, repository, (int)response.StatusCode, body);
                return IntegrationResult<GitHubReleaseResult>.Fail($"{(int)response.StatusCode}: {body}");
            }

            var data = await response.Content.ReadFromJsonAsync<JsonElement>();
            var result = new GitHubReleaseResult
            {
                Success = true,
                Id = data.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : null,
                HtmlUrl = data.TryGetProperty("html_url", out var url) ? url.GetString() : null,
                TagName = data.TryGetProperty("tag_name", out var t) ? t.GetString() ?? request.TagName : request.TagName,
            };
            return IntegrationResult<GitHubReleaseResult>.Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create GitHub release {Tag} in {Repo}", request.TagName, repository);
            return IntegrationResult<GitHubReleaseResult>.Fail(ex.Message);
        }
    }
}
