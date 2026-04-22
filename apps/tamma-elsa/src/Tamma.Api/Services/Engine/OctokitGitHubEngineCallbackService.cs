using System.Text;
using System.Text.Json;
using Octokit;
using Tamma.Api.Services.GitHub;

namespace Tamma.Api.Services.Engine;

/// <summary>
/// Real <see cref="IGitHubEngineCallbackService"/> implementation that uses
/// an installation-authenticated Octokit client (sourced from
/// <see cref="OctokitGitHubAppClient"/>) to service the engine callback
/// GitHub-proxy endpoints.
///
/// <para>This service sits behind the <c>/api/engine/*</c> endpoints that the
/// deployed Elsa activities hit (SelectWorkItemActivity, TriggerCIActivity,
/// UpdateIssueStatusActivity, etc.). Finding-to-method map:</para>
///
/// <list type="bullet">
/// <item>engine 005 — <see cref="ReadRepoConfigAsync"/></item>
/// <item>engine 006 — <see cref="ListIssuesAsync"/></item>
/// <item>engine 007 — <see cref="ListSecurityAlertsAsync"/></item>
/// <item>engine 008 — <see cref="PostIssueCommentAsync"/></item>
/// <item>engine 009 — <see cref="AddIssueLabelsAsync"/> /
/// <see cref="RemoveIssueLabelAsync"/></item>
/// <item>engine 010 — <see cref="CreateIssueAsync"/></item>
/// <item>engine 011 — <see cref="TriggerCiAsync"/></item>
/// </list>
///
/// <para>The endpoints accept a repo identifier without an installation id;
/// we resolve installation → client via <see cref="IRepoInstallationResolver"/>
/// so this service can remain stateless.</para>
/// </summary>
public sealed class OctokitGitHubEngineCallbackService : IGitHubEngineCallbackService
{
    private readonly OctokitGitHubAppClient _appClient;
    private readonly IRepoInstallationResolver _resolver;
    private readonly ILogger<OctokitGitHubEngineCallbackService> _logger;

    public OctokitGitHubEngineCallbackService(
        OctokitGitHubAppClient appClient,
        IRepoInstallationResolver resolver,
        ILogger<OctokitGitHubEngineCallbackService> logger)
    {
        _appClient = appClient;
        _resolver = resolver;
        _logger = logger;
    }

    // ─── Repo config (finding 005) ──────────────────────────────────────────

    public async Task<GitHubCallbackResult<JsonElement>> ReadRepoConfigAsync(
        string owner, string repo, string branch, CancellationToken ct = default)
    {
        return await WithClientAsync<JsonElement>(owner, repo, ct, async (client) =>
        {
            try
            {
                // GitHub's contents API accepts ref=branch to pin to a branch
                // without cloning. Order of fallback matches the TS impl:
                // .tamma/config.yaml, .tamma/config.yml, .tamma/config.json.
                foreach (var path in new[] { ".tamma/config.yaml", ".tamma/config.yml", ".tamma/config.json" })
                {
                    try
                    {
                        var content = await client.Repository.Content
                            .GetAllContentsByRef(owner, repo, path, branch)
                            .WaitAsync(ct).ConfigureAwait(false);

                        var file = content.FirstOrDefault();
                        if (file is null || string.IsNullOrEmpty(file.Content))
                            continue;

                        // Return raw JSON when we hit a .json file. For .yaml
                        // we return a stubbed {rawYaml: ...} envelope — full
                        // YAML parsing lives downstream (conventions reader in
                        // Elsa activity handles both shapes).
                        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(file.Content);
                                return GitHubCallbackResult<JsonElement>.Ok(doc.RootElement.Clone());
                            }
                            catch (JsonException)
                            {
                                // Invalid JSON in repo — fall through to the
                                // graceful `{}` branch (TS returned {} on any
                                // read failure).
                                break;
                            }
                        }

                        var envelope = JsonSerializer.SerializeToDocument(new { rawYaml = file.Content });
                        return GitHubCallbackResult<JsonElement>.Ok(envelope.RootElement.Clone());
                    }
                    catch (NotFoundException)
                    {
                        // Try next path.
                    }
                }

                // No config file — TS contract returns {} not a 404 so the
                // `conventions` injection path keeps working.
                using var empty = JsonDocument.Parse("{}");
                return GitHubCallbackResult<JsonElement>.Ok(empty.RootElement.Clone());
            }
            catch (ApiException ex)
            {
                _logger.LogWarning(ex,
                    "Failed to read repo config from {Owner}/{Repo}@{Branch}",
                    owner, repo, branch);
                return GitHubCallbackResult<JsonElement>.Failed("repo_config_error");
            }
        });
    }

    // ─── Issues list (finding 006) ──────────────────────────────────────────

    public async Task<GitHubCallbackResult<IssueListResult>> ListIssuesAsync(
        string owner, string repo, string state, string? labels, int perPage, int page,
        CancellationToken ct = default)
    {
        return await WithClientAsync<IssueListResult>(owner, repo, ct, async (client) =>
        {
            var request = new RepositoryIssueRequest
            {
                State = ParseIssueState(state),
            };
            if (!string.IsNullOrEmpty(labels))
            {
                foreach (var label in labels.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    request.Labels.Add(label.Trim());
            }

            var apiOptions = new ApiOptions
            {
                PageSize = perPage,
                PageCount = 1,
                StartPage = page
            };

            var issues = await client.Issue.GetAllForRepository(owner, repo, request, apiOptions)
                .WaitAsync(ct).ConfigureAwait(false);

            // Filter out pull requests — GitHub's issues endpoint returns
            // both. Octokit's Issue.PullRequest is non-null when the row is
            // actually a PR.
            var filtered = issues
                .Where(i => i.PullRequest is null)
                .Select(IssueToJson)
                .ToList();

            return GitHubCallbackResult<IssueListResult>.Ok(
                new IssueListResult(filtered, filtered.Count));
        });
    }

    private static ItemStateFilter ParseIssueState(string state) => state?.ToLowerInvariant() switch
    {
        "closed" => ItemStateFilter.Closed,
        "all" => ItemStateFilter.All,
        _ => ItemStateFilter.Open,
    };

    // ─── Security alerts (finding 007) ──────────────────────────────────────

    public async Task<GitHubCallbackResult<SecurityAlertResult>> ListSecurityAlertsAsync(
        string owner, string repo, string alertType, CancellationToken ct = default)
    {
        return await WithClientAsync<SecurityAlertResult>(owner, repo, ct, async (client) =>
        {
            var wantDependabot = alertType is "dependabot" or "all";
            var wantCodeScanning = alertType is "codeql" or "codeScanning" or "all";

            var dependabot = new List<JsonElement>();
            var codeScanning = new List<JsonElement>();

            if (wantDependabot)
            {
                try
                {
                    // Dependabot alerts — use the generic HTTP layer since
                    // Octokit's typed client for these is inconsistent.
                    var uri = new Uri($"repos/{owner}/{repo}/dependabot/alerts?state=open&per_page=100", UriKind.Relative);
                    var response = await client.Connection.Get<object>(uri, null, null)
                        .WaitAsync(ct).ConfigureAwait(false);
                    if (response.HttpResponse.Body is string body && !string.IsNullOrEmpty(body))
                    {
                        dependabot.AddRange(ParseJsonArray(body));
                    }
                }
                catch (Exception ex) when (ex is ApiException or NotFoundException)
                {
                    // Dependabot may not be enabled on the repo — per TS,
                    // log a warning and return [] for that scanner only.
                    _logger.LogWarning(ex,
                        "Failed to fetch dependabot alerts for {Owner}/{Repo}", owner, repo);
                }
            }

            if (wantCodeScanning)
            {
                try
                {
                    var uri = new Uri($"repos/{owner}/{repo}/code-scanning/alerts?state=open&per_page=100", UriKind.Relative);
                    var response = await client.Connection.Get<object>(uri, null, null)
                        .WaitAsync(ct).ConfigureAwait(false);
                    if (response.HttpResponse.Body is string body && !string.IsNullOrEmpty(body))
                    {
                        codeScanning.AddRange(ParseJsonArray(body));
                    }
                }
                catch (Exception ex) when (ex is ApiException or NotFoundException)
                {
                    _logger.LogWarning(ex,
                        "Failed to fetch code-scanning alerts for {Owner}/{Repo}", owner, repo);
                }
            }

            return GitHubCallbackResult<SecurityAlertResult>.Ok(
                new SecurityAlertResult(dependabot, codeScanning));
        });
    }

    // ─── Issue comment (finding 008) ────────────────────────────────────────

    public async Task<GitHubCallbackResult<IssueCommentResult>> PostIssueCommentAsync(
        string owner, string repo, int issueNumber, string body, CancellationToken ct = default)
    {
        return await WithClientAsync<IssueCommentResult>(owner, repo, ct, async (client) =>
        {
            var comment = await client.Issue.Comment.Create(owner, repo, issueNumber, body)
                .WaitAsync(ct).ConfigureAwait(false);
            return GitHubCallbackResult<IssueCommentResult>.Ok(
                new IssueCommentResult(comment.Id, comment.HtmlUrl ?? string.Empty));
        });
    }

    // ─── Issue labels (finding 009) ─────────────────────────────────────────

    public async Task<GitHubCallbackResult<string[]>> AddIssueLabelsAsync(
        string owner, string repo, int issueNumber, string[] labels, CancellationToken ct = default)
    {
        return await WithClientAsync<string[]>(owner, repo, ct, async (client) =>
        {
            var result = await client.Issue.Labels.AddToIssue(owner, repo, issueNumber, labels)
                .WaitAsync(ct).ConfigureAwait(false);
            return GitHubCallbackResult<string[]>.Ok(result.Select(l => l.Name).ToArray());
        });
    }

    public async Task<GitHubCallbackResult<bool>> RemoveIssueLabelAsync(
        string owner, string repo, int issueNumber, string label, CancellationToken ct = default)
    {
        return await WithClientAsync<bool>(owner, repo, ct, async (client) =>
        {
            await client.Issue.Labels.RemoveFromIssue(owner, repo, issueNumber, label)
                .WaitAsync(ct).ConfigureAwait(false);
            return GitHubCallbackResult<bool>.Ok(true);
        });
    }

    // ─── Create issue (finding 010) ─────────────────────────────────────────

    public async Task<GitHubCallbackResult<CreatedIssueResult>> CreateIssueAsync(
        string owner, string repo, string title, string? body,
        string[]? labels, string[]? assignees, CancellationToken ct = default)
    {
        return await WithClientAsync<CreatedIssueResult>(owner, repo, ct, async (client) =>
        {
            var newIssue = new NewIssue(title);
            if (!string.IsNullOrEmpty(body)) newIssue.Body = body;
            if (labels is not null)
                foreach (var l in labels) newIssue.Labels.Add(l);
            if (assignees is not null)
                foreach (var a in assignees) newIssue.Assignees.Add(a);

            var created = await client.Issue.Create(owner, repo, newIssue)
                .WaitAsync(ct).ConfigureAwait(false);

            return GitHubCallbackResult<CreatedIssueResult>.Ok(
                new CreatedIssueResult(created.Number, created.HtmlUrl ?? string.Empty, created.Title));
        });
    }

    // ─── Trigger CI (finding 011) ───────────────────────────────────────────

    public async Task<GitHubCallbackResult<DispatchedWorkflowResult>> TriggerCiAsync(
        string owner, string repo, string branchName, string workflowFile,
        Dictionary<string, string>? inputs, CancellationToken ct = default)
    {
        return await WithClientAsync<DispatchedWorkflowResult>(owner, repo, ct, async (client) =>
        {
            // CreateWorkflowDispatch accepts a workflow file name ("ci.yml")
            // or a numeric id. Pass the file-name overload.
            var dispatch = new CreateWorkflowDispatch(branchName);
            if (inputs is not null && inputs.Count > 0)
            {
                foreach (var kv in inputs)
                    dispatch.Inputs[kv.Key] = kv.Value;
            }

            await client.Actions.Workflows.CreateDispatch(owner, repo, workflowFile, dispatch)
                .WaitAsync(ct).ConfigureAwait(false);

            return GitHubCallbackResult<DispatchedWorkflowResult>.Ok(
                new DispatchedWorkflowResult(Dispatched: true, workflowFile, branchName));
        });
    }

    // ─── Shared plumbing ────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the installation for the given repo, obtains an installation-
    /// authenticated Octokit client, and invokes <paramref name="work"/>.
    /// Maps Octokit rate-limit / abuse / API exceptions to
    /// <see cref="GitHubCallbackResult{T}"/> failures so endpoints get
    /// structured, non-throwing results.
    /// </summary>
    private async Task<GitHubCallbackResult<T>> WithClientAsync<T>(
        string owner, string repo, CancellationToken ct,
        Func<IGitHubClient, Task<GitHubCallbackResult<T>>> work)
    {
        var installationId = await _resolver.ResolveInstallationIdAsync(owner, repo, ct).ConfigureAwait(false);
        if (installationId is null)
        {
            _logger.LogWarning(
                "No installation found for {Owner}/{Repo}; returning 503 (not_configured)",
                owner, repo);
            return GitHubCallbackResult<T>.NotConfigured();
        }

        try
        {
            var client = await _appClient.GetInstallationClientAsync(installationId.Value, ct).ConfigureAwait(false);
            return await work(client).ConfigureAwait(false);
        }
        catch (RateLimitExceededException ex)
        {
            _logger.LogWarning(ex,
                "Rate limit hit on {Owner}/{Repo} (installation={InstallationId}); resetAt={ResetAt:o}",
                owner, repo, installationId, ex.Reset);
            return GitHubCallbackResult<T>.Failed("github_rate_limited");
        }
        catch (AbuseException ex)
        {
            _logger.LogWarning(ex,
                "Abuse detection on {Owner}/{Repo} (installation={InstallationId})",
                owner, repo, installationId);
            return GitHubCallbackResult<T>.Failed("github_abuse_detected");
        }
        catch (AuthorizationException ex)
        {
            _logger.LogWarning(ex,
                "Auth failure on {Owner}/{Repo} — invalidating cached installation token",
                owner, repo);
            _appClient.InvalidateInstallationToken(installationId.Value);
            return GitHubCallbackResult<T>.Failed("github_unauthorized");
        }
        catch (ApiException ex)
        {
            _logger.LogWarning(ex,
                "GitHub API error on {Owner}/{Repo}: {Status}",
                owner, repo, (int)ex.StatusCode);
            return GitHubCallbackResult<T>.Failed($"github_api_error_{(int)ex.StatusCode}");
        }
    }

    private static JsonElement IssueToJson(Issue issue)
    {
        // Shape a minimal JSON projection matching what TS returned — full
        // Issue serialization would include internal HATEOAS URLs the Elsa
        // activities don't need.
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", issue.Id);
            writer.WriteNumber("number", issue.Number);
            writer.WriteString("title", issue.Title ?? string.Empty);
            writer.WriteString("state", issue.State.StringValue);
            writer.WriteString("body", issue.Body ?? string.Empty);
            writer.WriteString("html_url", issue.HtmlUrl ?? string.Empty);
            writer.WriteStartArray("labels");
            foreach (var lbl in issue.Labels)
            {
                writer.WriteStartObject();
                writer.WriteString("name", lbl.Name ?? string.Empty);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }

    private static IEnumerable<JsonElement> ParseJsonArray(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var el in doc.RootElement.EnumerateArray())
            yield return el.Clone();
    }
}

/// <summary>
/// Resolves <c>owner/repo</c> → <c>installationId</c> for the engine callback
/// service. The default implementation looks up the installation via the
/// <c>GitHubInstallationRepos</c> table using the repo's full name; when no
/// installation is stored locally, returns null (the service falls through to
/// 503 <c>github_client_not_configured</c>).
/// </summary>
public interface IRepoInstallationResolver
{
    Task<long?> ResolveInstallationIdAsync(string owner, string repo, CancellationToken ct = default);
}

public sealed class InstallationRepoResolver : IRepoInstallationResolver
{
    private readonly Data.Repositories.IInstallationRepository _installations;
    private readonly ILogger<InstallationRepoResolver> _logger;

    public InstallationRepoResolver(
        Data.Repositories.IInstallationRepository installations,
        ILogger<InstallationRepoResolver> logger)
    {
        _installations = installations;
        _logger = logger;
    }

    public async Task<long?> ResolveInstallationIdAsync(string owner, string repo, CancellationToken ct = default)
    {
        var fullName = $"{owner}/{repo}";
        var installation = await _installations.GetByRepoFullNameAsync(fullName).ConfigureAwait(false);
        if (installation is null)
        {
            _logger.LogDebug(
                "No local installation row for {FullName} — engine callback will 503",
                fullName);
            return null;
        }
        return installation.InstallationId;
    }
}
