using System.Text.Json;

namespace Tamma.Api.Services.Engine;

/// <summary>
/// Default <see cref="IGitHubEngineCallbackService"/> when no GitHub App
/// client is wired. Every method short-circuits to
/// <see cref="GitHubCallbackResult{T}.NotConfigured"/>.
///
/// <para>The endpoints translate <c>ServiceUnavailable</c> to HTTP 503 with
/// a <c>github_client_not_configured</c> error body — matching the TS
/// contract for the unwired-reader path. Callers (deployed Elsa
/// activities) treat 503 as a soft failure and skip; the previous one-line
/// stub returned 200 with bogus payloads, which crashed downstream
/// property-access calls.</para>
/// </summary>
public sealed class NullGitHubEngineCallbackService : IGitHubEngineCallbackService
{
    public Task<GitHubCallbackResult<JsonElement>> ReadRepoConfigAsync(
        string owner, string repo, string branch, CancellationToken ct = default)
        => Task.FromResult(GitHubCallbackResult<JsonElement>.NotConfigured());

    public Task<GitHubCallbackResult<IssueListResult>> ListIssuesAsync(
        string owner, string repo, string state, string? labels, int perPage, int page,
        CancellationToken ct = default)
        => Task.FromResult(GitHubCallbackResult<IssueListResult>.NotConfigured());

    public Task<GitHubCallbackResult<SecurityAlertResult>> ListSecurityAlertsAsync(
        string owner, string repo, string alertType, CancellationToken ct = default)
        => Task.FromResult(GitHubCallbackResult<SecurityAlertResult>.NotConfigured());

    public Task<GitHubCallbackResult<IssueCommentResult>> PostIssueCommentAsync(
        string owner, string repo, int issueNumber, string body, CancellationToken ct = default)
        => Task.FromResult(GitHubCallbackResult<IssueCommentResult>.NotConfigured());

    public Task<GitHubCallbackResult<string[]>> AddIssueLabelsAsync(
        string owner, string repo, int issueNumber, string[] labels, CancellationToken ct = default)
        => Task.FromResult(GitHubCallbackResult<string[]>.NotConfigured());

    public Task<GitHubCallbackResult<bool>> RemoveIssueLabelAsync(
        string owner, string repo, int issueNumber, string label, CancellationToken ct = default)
        => Task.FromResult(GitHubCallbackResult<bool>.NotConfigured());

    public Task<GitHubCallbackResult<CreatedIssueResult>> CreateIssueAsync(
        string owner, string repo, string title, string? body,
        string[]? labels, string[]? assignees, CancellationToken ct = default)
        => Task.FromResult(GitHubCallbackResult<CreatedIssueResult>.NotConfigured());

    public Task<GitHubCallbackResult<DispatchedWorkflowResult>> TriggerCiAsync(
        string owner, string repo, string branchName, string workflowFile,
        Dictionary<string, string>? inputs, CancellationToken ct = default)
        => Task.FromResult(GitHubCallbackResult<DispatchedWorkflowResult>.NotConfigured());
}
